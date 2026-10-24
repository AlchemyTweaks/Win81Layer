using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Win81Layer;

public static class TaskbarPins
{
	internal static readonly string PinsPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Win81Layer", "taskbar-pins.json");

	// Legacy one-time import only. Mutable pin recovery is user-scoped in DurableStateStore.
	public static readonly string GoldenPinsPath = Path.Combine(AppContext.BaseDirectory, "golden", "taskbar-pins.json");

	private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
	{
		WriteIndented = true
	};

	public static bool LoadWasDegraded { get; private set; }

	private static readonly object Gate = new object();

	private static List<PinnedApp>? _cache;

	private static DateTime _cacheStampUtc;

	private static long _lastDegradedProbeMs;

	private static long _revision;

	public static long Revision
	{
		get
		{
			lock (Gate)
			{
				return _revision;
			}
		}
	}

	// When the user last changed the launcher's pins before this process touched them: the primary file's (or an
	// unflushed pending journal's) last write, taken once on the first Load. The lastgood copy is excluded on purpose:
	// it is refreshed on reads, so it says nothing about when the pins were last edited. Null when neither exists.
	// The native pin sync uses it on its first run to catch up only native pins made after that moment.
	internal static DateTime? StoreStampAtFirstLoadUtc
	{
		get
		{
			lock (Gate)
			{
				CaptureStoreStampOnce();
				return _storeStampAtFirstLoadUtc;
			}
		}
	}

	private static DateTime? _storeStampAtFirstLoadUtc;

	private static bool _storeStampCaptured;

	private static void CaptureStoreStampOnce()
	{
		if (_storeStampCaptured)
		{
			return;
		}
		_storeStampCaptured = true;
		_storeStampAtFirstLoadUtc = StoreStampOf(PinsPath, DurableStateStore.PendingPath("taskbar-pins"));
	}

	// The later last write of a pin store's primary file and its pending journal (null when neither exists). Also
	// used by --pinsynctest's dry run on the real files.
	internal static DateTime? StoreStampOf(string primaryPath, string pendingPath)
	{
		long ticks = 0;
		foreach (string path in new[] { primaryPath, pendingPath })
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
		return ticks > 0 ? new DateTime(ticks, DateTimeKind.Utc) : null;
	}

	public static List<PinnedApp> Load()
	{
		lock (Gate)
		{
			CaptureStoreStampOnce();
			DateTime stateStamp = DurableStateStore.GetStateStampUtc("taskbar-pins", PinsPath);
			long now = Environment.TickCount64;
			if (_cache != null && stateStamp == _cacheStampUtc && (!LoadWasDegraded || now - _lastDegradedProbeMs < 2000L))
			{
				return ClonePins(_cache);
			}
			DurableStateReadResult result = DurableStateStore.Read("taskbar-pins", PinsPath, GoldenPinsPath, ValidatePinsJson, 20);
			if (result.Json == null)
			{
				LoadWasDegraded = File.Exists(PinsPath);
				_lastDegradedProbeMs = now;
				if (_cache != null)
				{
					return ClonePins(_cache);
				}
				CachePins(new List<PinnedApp>());
				return new List<PinnedApp>();
			}
			try
			{
				List<PinnedApp> parsed = JsonSerializer.Deserialize<List<PinnedApp>>(result.Json, JsonOptions) ?? new List<PinnedApp>();
				bool changed = false;
				foreach (PinnedApp pin in parsed)
				{
					NormalizePin(pin);
					changed |= AppLauncher.NormalizePinnedApp(pin);
				}
				LoadWasDegraded = !result.PrimaryHealthy;
				if (LoadWasDegraded)
				{
					_lastDegradedProbeMs = now;
				}
				if (changed)
				{
					SaveCore(parsed, "identity migration");
				}
				else
				{
					CachePins(parsed);
				}
				Logger.Log($"Pins loaded: {parsed.Count} from {result.Source}");
				return ClonePins(parsed);
			}
			catch (Exception ex)
			{
				Logger.Log("Pins parse failed after durable validation: " + ex.Message);
				LoadWasDegraded = true;
				_lastDegradedProbeMs = now;
				return _cache == null ? new List<PinnedApp>() : ClonePins(_cache);
			}
		}
	}

	public static void Save(List<PinnedApp> pins)
	{
		lock (Gate)
		{
			CaptureStoreStampOnce();
			SaveCore(pins ?? new List<PinnedApp>(), "user change");
		}
	}

	// Same as Save, with the reason written to the log line; true when the state was stored durably.
	internal static bool Save(List<PinnedApp> pins, string reason)
	{
		lock (Gate)
		{
			CaptureStoreStampOnce();
			return SaveCore(pins ?? new List<PinnedApp>(), reason);
		}
	}

	private static bool SaveCore(List<PinnedApp> pins, string reason)
	{
		foreach (PinnedApp pin in pins)
		{
			NormalizePin(pin);
			AppLauncher.NormalizePinnedApp(pin);
		}
		string json = JsonSerializer.Serialize(pins, JsonOptions);
		DurableStateCommitResult committed = DurableStateStore.Commit("taskbar-pins", PinsPath, json, ValidatePinsJson);
		if (!committed.IsDurable)
		{
			Logger.Log("Pins save FAILED: neither primary nor pending journal accepted the state");
			return false;
		}
		LoadWasDegraded = false;
		CachePins(pins);
		Logger.Log($"Pins saved durably: {pins.Count}; primary={(committed.PrimaryCommitted ? "committed" : "queued")}; reason={reason}");
		return true;
	}

	private static void CachePins(List<PinnedApp> pins)
	{
		_cache = ClonePins(pins);
		_cacheStampUtc = DurableStateStore.GetStateStampUtc("taskbar-pins", PinsPath);
		_revision++;
	}

	private static List<PinnedApp> ClonePins(IEnumerable<PinnedApp> pins)
	{
		return pins.Select(pin => new PinnedApp
		{
			Name = pin.Name,
			LaunchPath = pin.LaunchPath,
			Args = pin.Args,
			ExePath = pin.ExePath,
			IconPath = pin.IconPath,
			Aumid = pin.Aumid,
			GroupName = pin.GroupName,
			WorkingDir = pin.WorkingDir
		}).ToList();
	}

	private static bool ValidatePinsJson(string json)
	{
		try
		{
			List<PinnedApp> pins = JsonSerializer.Deserialize<List<PinnedApp>>(json, JsonOptions);
			return pins != null && pins.All((PinnedApp p) => p != null && !string.IsNullOrWhiteSpace(p.LaunchPath));
		}
		catch
		{
			return false;
		}
	}

	private static void NormalizePin(PinnedApp pin)
	{
		pin.Name = string.IsNullOrWhiteSpace(pin.Name) ? Path.GetFileNameWithoutExtension(pin.ExePath ?? pin.LaunchPath) : pin.Name.Trim();
		pin.LaunchPath = pin.LaunchPath?.Trim() ?? string.Empty;
		pin.Args = string.IsNullOrWhiteSpace(pin.Args) ? null : pin.Args;
		pin.ExePath = string.IsNullOrWhiteSpace(pin.ExePath) ? null : pin.ExePath;
		pin.IconPath = string.IsNullOrWhiteSpace(pin.IconPath) ? null : pin.IconPath;
		pin.Aumid = string.IsNullOrWhiteSpace(pin.Aumid) ? null : pin.Aumid;
		pin.GroupName ??= string.Empty;
		pin.WorkingDir = string.IsNullOrWhiteSpace(pin.WorkingDir) ? null : pin.WorkingDir.Trim();
	}

	public static List<PinnedApp> ImportFromNative()
	{
		List<PinnedApp> result = new List<PinnedApp>();
		string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Microsoft\\Internet Explorer\\Quick Launch\\User Pinned\\TaskBar");
		if (!Directory.Exists(dir))
		{
			return result;
		}
		Type shellType = Type.GetTypeFromProgID("WScript.Shell");
		if (shellType == null)
		{
			return result;
		}
		dynamic shell = Activator.CreateInstance(shellType);
		if (shell == null)
		{
			return result;
		}
		try
		{
			foreach (string lnk in Directory.EnumerateFiles(dir, "*.lnk"))
			{
				try
				{
					string name = Path.GetFileNameWithoutExtension(lnk);
					dynamic sc = shell.CreateShortcut(lnk);
					string target = (sc.TargetPath as string) ?? "";
					string args = (sc.Arguments as string) ?? "";
					string iconLoc = (sc.IconLocation as string) ?? "";
					if (string.IsNullOrWhiteSpace(target) && name.Equals("File Explorer", StringComparison.OrdinalIgnoreCase))
					{
						target = Environment.ExpandEnvironmentVariables("%windir%\\explorer.exe");
					}
					if (string.IsNullOrWhiteSpace(target) || !File.Exists(target) || !target.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || result.Any((PinnedApp p) => string.Equals(p.ExePath, target, StringComparison.OrdinalIgnoreCase) && (p.Args ?? "") == args))
					{
						continue;
					}
					string matchExe = target;
					if (target.EndsWith("Update.exe", StringComparison.OrdinalIgnoreCase))
					{
						string resolved = ResolveStubExe(target, args);
						if (resolved != null)
						{
							matchExe = resolved;
						}
					}
					// .lnk icon paths are often stored unexpanded (%USERPROFILE%\...), so expand before probing - otherwise
					// File.Exists fails and the pin silently falls back to the target exe's icon instead of the real one.
					string iconFile = Environment.ExpandEnvironmentVariables(iconLoc.Split(',')[0].Trim());
					// A File Explorer pin is plain explorer.exe with no args (a folder / This PC pin carries a
					// path or CLSID in args). Stamp the shell AppID so its taskbar button resolves to the
					// authentic 8.1 Explorer icon via AppIconOverrides (explorer.exe stays out of ExeTable so
					// Control Panel / This PC windows keep their own icons).
					string? pinAumid = (Path.GetFileName(matchExe).Equals("explorer.exe", StringComparison.OrdinalIgnoreCase) && string.IsNullOrWhiteSpace(args))
						? "Microsoft.Windows.Explorer"
						: null;
					result.Add(new PinnedApp
					{
						Name = name,
						LaunchPath = target,
						Args = (string.IsNullOrWhiteSpace(args) ? null : args),
						ExePath = matchExe,
						Aumid = pinAumid,
						IconPath = (File.Exists(iconFile) ? iconFile : null)
					});
				}
				catch
				{
				}
			}
		}
		finally
		{
			try
			{
				Marshal.FinalReleaseComObject(shell);
			}
			catch
			{
			}
		}
		Logger.Log($"Imported {result.Count} native taskbar pins");
		return result;
	}

	internal static string? ResolveStubExe(string updateExe, string args)
	{
		try
		{
			Match m = Regex.Match(args, "--processStart(?:AndWait)?\\s+\"?([^\"\\s]+\\.exe)\"?", RegexOptions.IgnoreCase);
			if (!m.Success)
			{
				return null;
			}
			string appExe = m.Groups[1].Value;
			string baseDir = Path.GetDirectoryName(updateExe) ?? "";
			string best = null;
			Version bestV = new Version(0, 0);
			string[] directories = Directory.GetDirectories(baseDir, "app-*");
			foreach (string d in directories)
			{
				string cand = Path.Combine(d, appExe);
				if (!File.Exists(cand))
				{
					continue;
				}
				string fileName = Path.GetFileName(d);
				if (Version.TryParse(fileName.Substring(4, fileName.Length - 4), out Version v))
				{
					if (v > bestV)
					{
						bestV = v;
						best = cand;
					}
				}
				else if (best == null)
				{
					best = cand;
				}
			}
			return best;
		}
		catch
		{
			return null;
		}
	}

	// Shell apps whose AppsFolder pin has no ExePath and whose live windows expose no AppUserModel.ID on Win11: the
	// backing process exe file name (see TaskbarWindow's filename index). Null for AUMIDs that are not mapped.
	internal static string? ShellPinExeName(string? aumid)
	{
		if (string.IsNullOrEmpty(aumid))
		{
			return null;
		}
		if (aumid.Equals("Microsoft.Windows.Explorer", StringComparison.OrdinalIgnoreCase))
		{
			return "explorer.exe";
		}
		if (aumid.Equals("Microsoft.AutoGenerated.{923DD477-5846-686B-A659-0FCCD73851A8}", StringComparison.OrdinalIgnoreCase))
		{
			return "taskmgr.exe";   // Task Manager (this box's shell-generated AppID; matches AppIconOverrides)
		}
		return null;
	}

	public static bool IsPinned(List<PinnedApp> pins, string? exePath)
	{
		if (string.IsNullOrEmpty(exePath))
		{
			return false;
		}
		return pins.Any((PinnedApp p) => string.Equals(p.ExePath, exePath, StringComparison.OrdinalIgnoreCase));
	}

	public static PinnedApp? FromDroppedPath(string path)
	{
		try
		{
			if (string.IsNullOrWhiteSpace(path))
			{
				return null;
			}
			if (path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && File.Exists(path))
			{
				return new PinnedApp
				{
					Name = Path.GetFileNameWithoutExtension(path),
					LaunchPath = path,
					ExePath = path
				};
			}
			if (path.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase) && File.Exists(path))
			{
				Type shellType = Type.GetTypeFromProgID("WScript.Shell");
				if (shellType == null)
				{
					return null;
				}
				dynamic shell = Activator.CreateInstance(shellType);
				if (shell == null)
				{
					return null;
				}
				dynamic sc = null;
				try
				{
					sc = shell.CreateShortcut(path);
					string target = (sc.TargetPath as string) ?? "";
					string args = (sc.Arguments as string) ?? "";
					string iconLoc = (sc.IconLocation as string) ?? "";
					string workingDir = (sc.WorkingDirectory as string) ?? "";
					if (string.IsNullOrWhiteSpace(target) || !File.Exists(target))
					{
						return null;
					}
					if (!target.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
					{
						return null;
					}
					string matchExe = target;
					if (target.EndsWith("Update.exe", StringComparison.OrdinalIgnoreCase))
					{
						string resolved = ResolveStubExe(target, args);
						if (resolved != null)
						{
							matchExe = resolved;
						}
					}
					// .lnk icon paths are often stored unexpanded (%USERPROFILE%\...), so expand before probing - otherwise
					// File.Exists fails and the pin silently falls back to the target exe's icon instead of the real one.
					string iconFile = Environment.ExpandEnvironmentVariables(iconLoc.Split(',')[0].Trim());
					return new PinnedApp
					{
						Name = Path.GetFileNameWithoutExtension(path),
						LaunchPath = target,
						Args = (string.IsNullOrWhiteSpace(args) ? null : args),
						ExePath = matchExe,
						IconPath = (File.Exists(iconFile) ? iconFile : null),
						WorkingDir = LinkWorkingDir(workingDir, target)
					};
				}
				finally
				{
					try
					{
						if (sc != null)
						{
							Marshal.FinalReleaseComObject(sc);
						}
					}
					catch
					{
					}
					try
					{
						Marshal.FinalReleaseComObject(shell);
					}
					catch
					{
					}
				}
			}
			return null;
		}
		catch (Exception ex)
		{
			Logger.Log("FromDroppedPath " + path + ": " + ex.Message);
			return null;
		}
	}

	// A shortcut's "Start in" folder, kept only when it is set, exists and differs from the target's own folder (the
	// launcher's default), so ordinary pins stay exactly as they were.
	internal static string? LinkWorkingDir(string? workingDir, string? target)
	{
		try
		{
			if (string.IsNullOrWhiteSpace(workingDir))
			{
				return null;
			}
			string wd = Environment.ExpandEnvironmentVariables(workingDir.Trim().Trim('"'));
			if (!Path.IsPathFullyQualified(wd) || !Directory.Exists(wd))
			{
				return null;
			}
			string? targetDir = string.IsNullOrWhiteSpace(target) ? null : Path.GetDirectoryName(target);
			if (targetDir != null && string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(wd)), Path.TrimEndingDirectorySeparator(Path.GetFullPath(targetDir)), StringComparison.OrdinalIgnoreCase))
			{
				return null;
			}
			return wd;
		}
		catch
		{
			return null;
		}
	}

	public static void Launch(PinnedTile tile)
	{
		string launchPath = tile.LaunchPath;
		string args = tile.Args;
		string aumid = tile.Aumid;
		string? workingDir = tile.WorkingDir;
		ShellLaunch.Run(delegate
		{
			if (!AppLauncher.TryLaunch(launchPath, args, aumid, asAdmin: false, workingDir, out string error))
			{
				Logger.Log("Pin launch '" + launchPath + "' failed: " + error);
			}
		});
	}
}
