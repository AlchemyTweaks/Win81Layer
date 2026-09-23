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

	public static List<PinnedApp> Load()
	{
		lock (Gate)
		{
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
			SaveCore(pins ?? new List<PinnedApp>(), "user change");
		}
	}

	private static void SaveCore(List<PinnedApp> pins, string reason)
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
			return;
		}
		LoadWasDegraded = false;
		CachePins(pins);
		Logger.Log($"Pins saved durably: {pins.Count}; primary={(committed.PrimaryCommitted ? "committed" : "queued")}; reason={reason}");
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
			GroupName = pin.GroupName
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

	private static string? ResolveStubExe(string updateExe, string args)
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
						IconPath = (File.Exists(iconFile) ? iconFile : null)
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

	public static void Launch(PinnedTile tile)
	{
		string launchPath = tile.LaunchPath;
		string args = tile.Args;
		string aumid = tile.Aumid;
		ShellLaunch.Run(delegate
		{
			if (!AppLauncher.TryLaunch(launchPath, args, aumid, asAdmin: false, out string error))
			{
				Logger.Log("Pin launch '" + launchPath + "' failed: " + error);
			}
		});
	}
}
