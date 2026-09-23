using System;
using System.Collections.Concurrent;
using System.IO;
using System.Text;
using System.Threading;

namespace Win81Layer;

internal sealed class DurableStateReadResult
{
	public string? Json { get; init; }

	public string Source { get; init; } = "missing";

	public bool PrimaryHealthy { get; init; }

	public bool Recovered { get; init; }
}

internal sealed class DurableStateCommitResult
{
	public bool PendingStored { get; init; }

	public bool PrimaryCommitted { get; init; }

	public bool RecoveryCommitted { get; init; }

	public bool IsDurable => PendingStored || PrimaryCommitted;
}

// One user-owned state root for every launcher build. A staged/source/live EXE must never carry its own
// mutable fallback copy, otherwise the layout selected after a transient read failure depends on which EXE ran.
internal static class DurableStateStore
{
	internal static bool ReadOnlyDiagnosticsSupported => true;
	private static readonly ConcurrentDictionary<string, object> Gates = new ConcurrentDictionary<string, object>(StringComparer.OrdinalIgnoreCase);

	public static readonly string RecoveryRoot = Path.Combine(
		Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
		"Win81Layer",
		"state");

	public static string PendingPath(string stateName) => StatePath(stateName, "pending");

	public static string LastGoodPath(string stateName) => StatePath(stateName, "lastgood");

	public static string PreviousPath(string stateName) => StatePath(stateName, "previous");

	public static DateTime GetStateStampUtc(string stateName, string primaryPath)
	{
		long ticks = 0;
		foreach (string path in new[] { primaryPath, PendingPath(stateName), LastGoodPath(stateName) })
		{
			try
			{
				if (File.Exists(path))
				{
					ticks = Math.Max(ticks, File.GetLastWriteTimeUtc(path).Ticks);
				}
			}
			catch
			{
			}
		}
		return ticks <= 0 ? DateTime.MinValue : new DateTime(ticks, DateTimeKind.Utc);
	}

	public static DurableStateReadResult Read(
		string stateName,
		string primaryPath,
		string? legacySeedPath,
		Func<string, bool> validate,
		int primaryAttempts = 8,
		bool repair = true)
	{
		repair &= !SettingsStore.ReadOnlyDiagnostics;
		object gate = Gates.GetOrAdd(stateName, static _ => new object());
		lock (gate)
		{
			if (repair) Directory.CreateDirectory(RecoveryRoot);
			string pendingPath = PendingPath(stateName);
			if (TryReadValid(pendingPath, validate, 4, out string? pendingJson, out _))
			{
				bool promoted = repair && PromoteSnapshot(stateName, primaryPath, pendingJson!, validate, removePending: true);
				Logger.Log($"State[{stateName}] loaded pending user change; primary promotion={(promoted ? "ok" : "deferred")}");
				return new DurableStateReadResult
				{
					Json = pendingJson,
					Source = "pending",
					PrimaryHealthy = promoted,
					Recovered = true
				};
			}

			if (TryReadValid(primaryPath, validate, Math.Max(1, primaryAttempts), out string? primaryJson, out string? primaryError))
			{
				if (repair) RefreshLastGoodIfNeeded(stateName, primaryJson!);
				return new DurableStateReadResult
				{
					Json = primaryJson,
					Source = "primary",
					PrimaryHealthy = true
				};
			}

			string lastGoodPath = LastGoodPath(stateName);
			if (TryReadValid(lastGoodPath, validate, 4, out string? lastGoodJson, out _))
			{
				if (repair) ArchiveInvalidPrimary(stateName, primaryPath, primaryError);
				bool restored = repair && PromoteSnapshot(stateName, primaryPath, lastGoodJson!, validate, removePending: false);
				Logger.Log($"State[{stateName}] recovered from stable last-good; primary restore={(restored ? "ok" : "deferred")}");
				return new DurableStateReadResult
				{
					Json = lastGoodJson,
					Source = "lastgood",
					PrimaryHealthy = restored,
					Recovered = true
				};
			}

			if (!string.IsNullOrWhiteSpace(legacySeedPath)
				&& TryReadValid(legacySeedPath!, validate, 2, out string? legacyJson, out _))
			{
				DurableStateCommitResult migrated = repair ? CommitCore(stateName, primaryPath, legacyJson!, validate) : new DurableStateCommitResult();
				Logger.Log($"State[{stateName}] imported one-time legacy EXE seed; primary={(migrated.PrimaryCommitted ? "ok" : "queued")}");
				return new DurableStateReadResult
				{
					Json = legacyJson,
					Source = "legacy-seed",
					PrimaryHealthy = migrated.PrimaryCommitted,
					Recovered = true
				};
			}

			if (!string.IsNullOrWhiteSpace(primaryError))
			{
				Logger.Log($"State[{stateName}] unavailable and no recovery snapshot exists: {primaryError}");
			}
			return new DurableStateReadResult();
		}
	}

	public static DurableStateCommitResult Commit(string stateName, string primaryPath, string json, Func<string, bool> validate)
	{
		if (SettingsStore.ReadOnlyDiagnostics) throw new InvalidOperationException("State writes are disabled in read-only diagnostics mode.");
		object gate = Gates.GetOrAdd(stateName, static _ => new object());
		lock (gate)
		{
			return CommitCore(stateName, primaryPath, json, validate);
		}
	}

	private static DurableStateCommitResult CommitCore(string stateName, string primaryPath, string json, Func<string, bool> validate)
	{
		if (string.IsNullOrWhiteSpace(json) || !SafeValidate(validate, json))
		{
			Logger.Log($"State[{stateName}] commit rejected: serialized state did not validate");
			return new DurableStateCommitResult();
		}

		Directory.CreateDirectory(RecoveryRoot);
		string pendingPath = PendingPath(stateName);
		bool pendingStored = TryWriteAtomic(pendingPath, json, out string? pendingError);
		if (!pendingStored)
		{
			Logger.Log($"State[{stateName}] commit failed before primary write; pending journal unavailable: {pendingError}");
			return new DurableStateCommitResult();
		}

		BackupCurrentPrimary(stateName, primaryPath, validate, json);
		bool primaryCommitted = TryWriteAtomic(primaryPath, json, out string? primaryError)
			&& TryReadValid(primaryPath, validate, 2, out string? verified, out _)
			&& string.Equals(verified, json, StringComparison.Ordinal);
		if (!primaryCommitted)
		{
			Logger.Log($"State[{stateName}] primary commit deferred; user change is durable in {pendingPath}: {primaryError}");
			return new DurableStateCommitResult
			{
				PendingStored = true
			};
		}

		bool recoveryCommitted = TryWriteAtomic(LastGoodPath(stateName), json, out string? recoveryError);
		if (recoveryCommitted)
		{
			TryDelete(pendingPath);
		}
		else
		{
			Logger.Log($"State[{stateName}] primary committed but last-good refresh deferred; pending retained: {recoveryError}");
		}
		return new DurableStateCommitResult
		{
			PendingStored = true,
			PrimaryCommitted = true,
			RecoveryCommitted = recoveryCommitted
		};
	}

	private static bool PromoteSnapshot(string stateName, string primaryPath, string json, Func<string, bool> validate, bool removePending)
	{
		BackupCurrentPrimary(stateName, primaryPath, validate, json);
		if (!TryWriteAtomic(primaryPath, json, out _)
			|| !TryReadValid(primaryPath, validate, 2, out string? verified, out _)
			|| !string.Equals(verified, json, StringComparison.Ordinal))
		{
			return false;
		}
		if (!TryWriteAtomic(LastGoodPath(stateName), json, out _))
		{
			return false;
		}
		if (removePending)
		{
			TryDelete(PendingPath(stateName));
		}
		return true;
	}

	private static void BackupCurrentPrimary(string stateName, string primaryPath, Func<string, bool> validate, string replacement)
	{
		if (TryReadValid(primaryPath, validate, 1, out string? current, out _)
			&& !string.Equals(current, replacement, StringComparison.Ordinal))
		{
			TryWriteAtomic(PreviousPath(stateName), current!, out _);
		}
	}

	private static void RefreshLastGoodIfNeeded(string stateName, string json)
	{
		string path = LastGoodPath(stateName);
		if (TryReadText(path, 1, out string? existing, out _) && string.Equals(existing, json, StringComparison.Ordinal))
		{
			return;
		}
		TryWriteAtomic(path, json, out _);
	}

	private static void ArchiveInvalidPrimary(string stateName, string primaryPath, string? error)
	{
		if (string.IsNullOrWhiteSpace(error))
		{
			return;
		}
		try
		{
			if (!File.Exists(primaryPath))
			{
				return;
			}
			string archive = Path.Combine(RecoveryRoot, $"{SafeName(stateName)}.corrupt-{DateTime.UtcNow:yyyyMMdd-HHmmss-fff}.json");
			using FileStream input = new FileStream(primaryPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
			using FileStream output = new FileStream(archive, FileMode.CreateNew, FileAccess.Write, FileShare.Read, 4096, FileOptions.WriteThrough);
			input.CopyTo(output);
			output.Flush(flushToDisk: true);
			Logger.Log($"State[{stateName}] archived unreadable primary: {archive}");
		}
		catch
		{
		}
	}

	private static bool TryReadValid(string path, Func<string, bool> validate, int attempts, out string? json, out string? error)
	{
		json = null;
		if (!TryReadText(path, attempts, out string? text, out error))
		{
			return false;
		}
		if (!SafeValidate(validate, text!))
		{
			error = "content failed validation";
			return false;
		}
		json = text;
		return true;
	}

	private static bool TryReadText(string path, int attempts, out string? text, out string? error)
	{
		text = null;
		error = null;
		for (int attempt = 0; attempt < Math.Max(1, attempts); attempt++)
		{
			try
			{
				using FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
				using StreamReader reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
				text = reader.ReadToEnd();
				return true;
			}
			catch (FileNotFoundException)
			{
				return false;
			}
			catch (DirectoryNotFoundException)
			{
				return false;
			}
			catch (Exception ex)
			{
				error = ex.Message;
				if (attempt + 1 < attempts)
				{
					Thread.Sleep(25);
				}
			}
		}
		return false;
	}

	private static bool TryWriteAtomic(string path, string content, out string? error)
	{
		error = null;
		string directory = Path.GetDirectoryName(path) ?? RecoveryRoot;
		string temp = path + ".tmp-" + Environment.ProcessId + "-" + Guid.NewGuid().ToString("N");
		try
		{
			Directory.CreateDirectory(directory);
			using (FileStream stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
			using (StreamWriter writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)))
			{
				writer.Write(content);
				writer.Flush();
				stream.Flush(flushToDisk: true);
			}
			File.Move(temp, path, overwrite: true);
			return true;
		}
		catch (Exception ex)
		{
			error = ex.Message;
			return false;
		}
		finally
		{
			TryDelete(temp);
		}
	}

	private static bool SafeValidate(Func<string, bool> validate, string json)
	{
		try
		{
			return validate(json);
		}
		catch
		{
			return false;
		}
	}

	private static void TryDelete(string path)
	{
		try
		{
			if (File.Exists(path))
			{
				File.Delete(path);
			}
		}
		catch
		{
		}
	}

	private static string StatePath(string stateName, string suffix)
	{
		return Path.Combine(RecoveryRoot, $"{SafeName(stateName)}.{suffix}.json");
	}

	private static string SafeName(string value)
	{
		foreach (char invalid in Path.GetInvalidFileNameChars())
		{
			value = value.Replace(invalid, '_');
		}
		return string.IsNullOrWhiteSpace(value) ? "state" : value;
	}
}
