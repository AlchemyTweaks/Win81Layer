using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace Win81Layer;

internal sealed class PersistedStateSummary
{
	public string Path { get; set; } = string.Empty;

	public string Source { get; set; } = "missing";

	public bool Valid { get; set; }

	public string Sha256 { get; set; } = string.Empty;

	public int ItemCount { get; set; }

	public string Detail { get; set; } = string.Empty;
}

internal sealed class PersistenceVerificationReport
{
	public int SchemaVersion { get; set; } = 3;

	public DateTime GeneratedUtc { get; set; }

	public DateTime BootUtc { get; set; }

	public string Mode { get; set; } = string.Empty;

	public string ProcessPath { get; set; } = string.Empty;

	public string ProcessSha256 { get; set; } = string.Empty;

	public string ManagedAssemblyPath { get; set; } = string.Empty;

	public string ManagedAssemblySha256 { get; set; } = string.Empty;

	public string ScheduledTaskCommand { get; set; } = string.Empty;

	public bool ScheduledTaskIsCanonical { get; set; }

	public PersistedStateSummary Profile { get; set; } = new PersistedStateSummary();

	public PersistedStateSummary Settings { get; set; } = new PersistedStateSummary();

	public PersistedStateSummary TaskbarPins { get; set; } = new PersistedStateSummary();

	public PersistedStateSummary ShellProfile { get; set; } = new PersistedStateSummary();

	public bool? MatchesExpectation { get; set; }

	public List<string> Mismatches { get; set; } = new List<string>();
}

public static class PersistenceDiagnostics
{
	internal static bool SuppressAutomaticPostBootVerification { get; set; }

	private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
	{
		WriteIndented = true
	};

	public static readonly string QaRoot = Path.Combine(
		Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
		"Win81Layer",
		"qa");

	public static readonly string SelfTestPath = Path.Combine(QaRoot, "persistence-selftest-latest.json");

	public static readonly string MigrationPath = Path.Combine(QaRoot, "persistence-migration-latest.json");

	public static readonly string ExpectationPath = Path.Combine(QaRoot, "reboot-expectation.json");

	public static readonly string PostBootPath = Path.Combine(QaRoot, "postboot-latest.json");

	public static readonly string AppLaunchTestPath = Path.Combine(QaRoot, "packaged-app-launch-latest.json");

	public static string RunSelfTest()
	{
		Directory.CreateDirectory(QaRoot);
		string stateName = "qa-" + Guid.NewGuid().ToString("N");
		string tempRoot = Path.Combine(Path.GetTempPath(), "Win81Layer-state-qa-" + Guid.NewGuid().ToString("N"));
		string primary = Path.Combine(tempRoot, "state.json");
		List<string> checks = new List<string>();
		bool passed = false;
		string? error = null;
		try
		{
			Directory.CreateDirectory(tempRoot);
			Func<string, bool> validator = ValidJsonObject;
			DurableStateCommitResult first = DurableStateStore.Commit(stateName, primary, "{\"generation\":1}", validator);
			Require(first.PrimaryCommitted && first.RecoveryCommitted, "initial verified commit");
			checks.Add("initial-commit");

			using (new FileStream(primary, FileMode.Open, FileAccess.Read, FileShare.Read))
			{
				DurableStateCommitResult queued = DurableStateStore.Commit(stateName, primary, "{\"generation\":2}", validator);
				Require(queued.PendingStored && !queued.PrimaryCommitted, "pending journal under denied primary replacement");
			}
			checks.Add("pending-journal");

			DurableStateReadResult recovered = DurableStateStore.Read(stateName, primary, null, validator, 2);
			Require(recovered.Json != null && recovered.Json.Contains("\"generation\":2", StringComparison.Ordinal), "pending recovery content");
			Require(recovered.PrimaryHealthy, "pending promotion");
			checks.Add("pending-recovery");

			Parallel.For(0, 24, i =>
			{
				DurableStateCommitResult commit = DurableStateStore.Commit(stateName, primary, $"{{\"generation\":{100 + i}}}", validator);
				if (!commit.IsDurable)
				{
					throw new InvalidOperationException("Concurrent commit lost durability.");
				}
			});
			DurableStateReadResult concurrent = DurableStateStore.Read(stateName, primary, null, validator, 2);
			Require(concurrent.Json != null && validator(concurrent.Json), "concurrent final state validity");
			checks.Add("serialized-concurrent-commits");
			passed = true;
		}
		catch (Exception ex)
		{
			error = ex.ToString();
		}
		finally
		{
			TryDeleteTree(tempRoot);
			TryDelete(DurableStateStore.PendingPath(stateName));
			TryDelete(DurableStateStore.LastGoodPath(stateName));
			TryDelete(DurableStateStore.PreviousPath(stateName));
		}

		AtomicWrite(SelfTestPath, JsonSerializer.Serialize(new
		{
			SchemaVersion = 1,
			GeneratedUtc = DateTime.UtcNow,
			Passed = passed,
			Checks = checks,
			Error = error
		}, JsonOptions));
		Logger.Log($"Persistence self-test {(passed ? "PASSED" : "FAILED")}: {SelfTestPath}");
		return SelfTestPath;

		static void Require(bool condition, string message)
		{
			if (!condition)
			{
				throw new InvalidOperationException("Self-test assertion failed: " + message);
			}
		}
	}

	public static string MigrateActiveState(string? profileSource)
	{
		bool profileOk = false;
		string selectedProfile = string.IsNullOrWhiteSpace(profileSource) ? Profile.ProfilePath : profileSource!;
		try
		{
			string profileJson = ReadShared(selectedProfile);
			profileOk = Profile.ImportJson(profileJson, "canonical migration from " + selectedProfile);
		}
		catch (Exception ex)
		{
			Logger.Log("Canonical profile migration failed: " + ex.Message);
		}

		AppSettings settings = SettingsStore.Load();
		SettingsStore.Save(settings);
		List<PinnedApp> pins = TaskbarPins.Load();
		TaskbarPins.Save(pins);
		PersistenceVerificationReport report = BuildReport("migration", expectation: null);
		if (!profileOk)
		{
			report.Mismatches.Add("profile-import-failed");
		}
		AtomicWrite(MigrationPath, JsonSerializer.Serialize(report, JsonOptions));
		Logger.Log($"Persistence migration report: {MigrationPath}");
		return MigrationPath;
	}

	public static string RunPackagedAppLaunchTest(string aumid)
	{
		if (string.IsNullOrWhiteSpace(aumid) || !aumid.Contains('!'))
		{
			throw new ArgumentException("A packaged-app AUMID is required.", nameof(aumid));
		}
		bool passed = AppLauncher.TryLaunch(
			"shell:AppsFolder\\" + aumid.Trim(),
			null,
			aumid.Trim(),
			asAdmin: false,
			out string? error);
		AtomicWrite(AppLaunchTestPath, JsonSerializer.Serialize(new
		{
			SchemaVersion = 1,
			GeneratedUtc = DateTime.UtcNow,
			Aumid = aumid.Trim(),
			Passed = passed,
			Error = error
		}, JsonOptions));
		Logger.Log($"Packaged app launch test {(passed ? "PASSED" : "FAILED")}: {aumid}; {AppLaunchTestPath}");
		return AppLaunchTestPath;
	}

	public static string WriteRebootExpectation()
	{
		PersistenceVerificationReport report = BuildReport("pre-reboot-expectation", expectation: null);
		AtomicWrite(ExpectationPath, JsonSerializer.Serialize(report, JsonOptions));
		Logger.Log($"Reboot persistence expectation written: {ExpectationPath}");
		return ExpectationPath;
	}

	public static string WriteVerification(string mode = "manual")
	{
		PersistenceVerificationReport? expectation = ReadExpectation();
		PersistenceVerificationReport report = BuildReport(mode, expectation);
		string path = mode.Equals("postboot", StringComparison.OrdinalIgnoreCase)
			? PostBootPath
			: Path.Combine(QaRoot, "persistence-verify-latest.json");
		AtomicWrite(path, JsonSerializer.Serialize(report, JsonOptions));
		Logger.Log($"Persistence verification ({mode}): matches={report.MatchesExpectation?.ToString() ?? "n/a"}; {path}");
		return path;
	}

	public static void TryWritePostBootVerification()
	{
		if (SuppressAutomaticPostBootVerification)
		{
			return;
		}
		try
		{
			PersistenceVerificationReport? expectation = ReadExpectation();
			if (expectation == null)
			{
				return;
			}
			DateTime bootUtc = CurrentBootUtc();
			if (Math.Abs((bootUtc - expectation.BootUtc).TotalMinutes) < 2.0)
			{
				return;
			}
			PersistenceVerificationReport report = BuildReport("postboot", expectation);
			AtomicWrite(PostBootPath, JsonSerializer.Serialize(report, JsonOptions));
			Logger.Log($"POSTBOOT persistence verification: matches={report.MatchesExpectation}; mismatches={string.Join(",", report.Mismatches)}; {PostBootPath}");
		}
		catch (Exception ex)
		{
			Logger.Log("Postboot persistence verification failed: " + ex.Message);
		}
	}

	private static PersistenceVerificationReport BuildReport(string mode, PersistenceVerificationReport? expectation)
	{
		Directory.CreateDirectory(QaRoot);
		string processPath = Environment.ProcessPath ?? string.Empty;
		string managedAssemblyPath = typeof(PersistenceDiagnostics).Assembly.Location;
		string taskCommand = LogonTask.CurrentCommand() ?? string.Empty;
		PersistenceVerificationReport report = new PersistenceVerificationReport
		{
			GeneratedUtc = DateTime.UtcNow,
			BootUtc = CurrentBootUtc(),
			Mode = mode,
			ProcessPath = processPath,
			ProcessSha256 = HashFile(processPath),
			ManagedAssemblyPath = managedAssemblyPath,
			ManagedAssemblySha256 = HashFile(managedAssemblyPath),
			ScheduledTaskCommand = taskCommand,
			ScheduledTaskIsCanonical = LogonTask.IsCanonical(),
			Profile = SummarizeProfile(),
			Settings = SummarizeSettings(),
			TaskbarPins = SummarizePins(),
			ShellProfile = SummarizeShellProfile()
		};
		if (expectation != null)
		{
			Compare(report.Profile.Sha256, expectation.Profile.Sha256, "profile-hash");
			Compare(report.Settings.Sha256, expectation.Settings.Sha256, "settings-hash");
			Compare(report.TaskbarPins.Sha256, expectation.TaskbarPins.Sha256, "taskbar-pins-hash");
			Compare(report.ShellProfile.Sha256, expectation.ShellProfile.Sha256, "shell-profile-hash");
			Compare(report.ProcessSha256, expectation.ProcessSha256, "launcher-binary-hash");
			Compare(report.ManagedAssemblySha256, expectation.ManagedAssemblySha256, "launcher-managed-assembly-hash");
			if (!report.ScheduledTaskIsCanonical)
			{
				report.Mismatches.Add("scheduled-task-command");
			}
			report.MatchesExpectation = report.Mismatches.Count == 0
				&& report.Profile.Valid
				&& report.Settings.Valid
				&& report.TaskbarPins.Valid
				&& report.ShellProfile.Valid;
		}
		return report;

		void Compare(string actual, string expected, string label)
		{
			if (string.IsNullOrWhiteSpace(actual) || !string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
			{
				report.Mismatches.Add(label);
			}
		}
	}

	private static PersistedStateSummary SummarizeProfile()
	{
		DurableStateReadResult state = DurableStateStore.Read("profile", Profile.ProfilePath, Profile.GoldenProfilePath, ValidProfileJson, 4);
		PersistedStateSummary summary = BaseSummary(Profile.ProfilePath, state, ValidProfileJson);
		if (!summary.Valid || state.Json == null)
		{
			return summary;
		}
		using JsonDocument document = JsonDocument.Parse(state.Json);
		JsonElement groups = document.RootElement.GetProperty("Groups");
		int tiles = groups.EnumerateArray().Sum(group => group.TryGetProperty("Tiles", out JsonElement value) ? value.GetArrayLength() : 0);
		summary.ItemCount = tiles;
		summary.Detail = $"groups={groups.GetArrayLength()}, tiles={tiles}";
		return summary;
	}

	private static PersistedStateSummary SummarizeSettings()
	{
		DurableStateReadResult state = DurableStateStore.Read("settings", SettingsStore.SettingsPath, SettingsStore.GoldenSettingsPath, ValidSettingsJson, 4);
		PersistedStateSummary summary = BaseSummary(SettingsStore.SettingsPath, state, ValidSettingsJson);
		if (!summary.Valid || state.Json == null)
		{
			return summary;
		}
		AppSettings settings = JsonSerializer.Deserialize<AppSettings>(state.Json, JsonOptions)!;
		summary.ItemCount = 1;
		summary.Detail = $"mode={settings.DesktopCompositionMode}, taskbar={settings.TaskbarEnabled}, align={settings.TaskbarAlignment}, size={settings.TaskbarSize}";
		return summary;
	}

	private static PersistedStateSummary SummarizePins()
	{
		DurableStateReadResult state = DurableStateStore.Read("taskbar-pins", TaskbarPins.PinsPath, TaskbarPins.GoldenPinsPath, ValidPinsJson, 4);
		PersistedStateSummary summary = BaseSummary(TaskbarPins.PinsPath, state, ValidPinsJson);
		if (!summary.Valid || state.Json == null)
		{
			return summary;
		}
		List<PinnedApp> pins = JsonSerializer.Deserialize<List<PinnedApp>>(state.Json, JsonOptions) ?? new List<PinnedApp>();
		summary.ItemCount = pins.Count;
		summary.Detail = $"pins={pins.Count}, packaged={pins.Count(p => !string.IsNullOrWhiteSpace(p.Aumid))}";
		return summary;
	}

	private static PersistedStateSummary SummarizeShellProfile()
	{
		DurableStateReadResult state = DurableStateStore.Read("shell-profile", ShellProfileManager.StatePath, null, ValidShellProfileJson, 4);
		PersistedStateSummary summary = BaseSummary(ShellProfileManager.StatePath, state, ValidShellProfileJson);
		if (!summary.Valid || state.Json == null)
		{
			return summary;
		}
		ShellProfileState profile = JsonSerializer.Deserialize<ShellProfileState>(state.Json, JsonOptions)!;
		summary.ItemCount = profile.Slots?.Count ?? 0;
		summary.Detail = $"active={profile.ActiveProfileId}, previous={profile.PreviousProfileId}, slots={summary.ItemCount}, pending={profile.Pending != null}";
		return summary;
	}

	private static PersistedStateSummary BaseSummary(string path, DurableStateReadResult state, Func<string, bool> validate)
	{
		bool valid = state.Json != null && validate(state.Json);
		return new PersistedStateSummary
		{
			Path = path,
			Source = state.Source,
			Valid = valid,
			Sha256 = valid ? HashText(state.Json!) : string.Empty
		};
	}

	private static bool ValidProfileJson(string json)
	{
		try
		{
			using JsonDocument document = JsonDocument.Parse(json);
			return document.RootElement.TryGetProperty("Groups", out JsonElement groups)
				&& groups.ValueKind == JsonValueKind.Array
				&& groups.GetArrayLength() > 0;
		}
		catch
		{
			return false;
		}
	}

	private static bool ValidSettingsJson(string json)
	{
		try
		{
			return JsonSerializer.Deserialize<AppSettings>(json, JsonOptions) != null;
		}
		catch
		{
			return false;
		}
	}

	private static bool ValidPinsJson(string json)
	{
		try
		{
			List<PinnedApp>? pins = JsonSerializer.Deserialize<List<PinnedApp>>(json, JsonOptions);
			return pins != null && pins.All(p => p != null && !string.IsNullOrWhiteSpace(p.LaunchPath));
		}
		catch
		{
			return false;
		}
	}

	private static bool ValidShellProfileJson(string json)
	{
		try
		{
			ShellProfileState? state = JsonSerializer.Deserialize<ShellProfileState>(json, JsonOptions);
			return state != null
				&& state.SchemaVersion == 1
				&& !string.IsNullOrWhiteSpace(state.ActiveProfileId)
				&& state.Slots != null
				&& state.Slots.Count > 0
				&& state.Pending == null;
		}
		catch
		{
			return false;
		}
	}

	private static bool ValidJsonObject(string json)
	{
		try
		{
			using JsonDocument document = JsonDocument.Parse(json);
			return document.RootElement.ValueKind == JsonValueKind.Object;
		}
		catch
		{
			return false;
		}
	}

	private static PersistenceVerificationReport? ReadExpectation()
	{
		try
		{
			return File.Exists(ExpectationPath)
				? JsonSerializer.Deserialize<PersistenceVerificationReport>(ReadShared(ExpectationPath), JsonOptions)
				: null;
		}
		catch
		{
			return null;
		}
	}

	private static DateTime CurrentBootUtc()
	{
		return DateTime.UtcNow - TimeSpan.FromMilliseconds(Math.Max(0L, Environment.TickCount64));
	}

	private static string HashFile(string path)
	{
		try
		{
			using FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
			return Convert.ToHexString(SHA256.HashData(stream));
		}
		catch
		{
			return string.Empty;
		}
	}

	private static string HashText(string text)
	{
		return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
	}

	private static string ReadShared(string path)
	{
		using FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
		using StreamReader reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
		return reader.ReadToEnd();
	}

	private static void AtomicWrite(string path, string content)
	{
		Directory.CreateDirectory(Path.GetDirectoryName(path) ?? QaRoot);
		string temp = path + ".tmp-" + Environment.ProcessId + "-" + Guid.NewGuid().ToString("N");
		try
		{
			using (FileStream stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
			using (StreamWriter writer = new StreamWriter(stream, new UTF8Encoding(false)))
			{
				writer.Write(content);
				writer.Flush();
				stream.Flush(flushToDisk: true);
			}
			File.Move(temp, path, overwrite: true);
		}
		finally
		{
			TryDelete(temp);
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

	private static void TryDeleteTree(string path)
	{
		try
		{
			if (Directory.Exists(path))
			{
				Directory.Delete(path, recursive: true);
			}
		}
		catch
		{
		}
	}
}
