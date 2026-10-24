#nullable enable

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;

namespace Win81Layer;

// Native pin sync: while the launcher is the shell, a "Pin to taskbar" / "Unpin from taskbar" / "Pin to Start" made in
// Explorer's OWN menus (desktop icons, File Explorer items, native Start) lands in Windows' stores only. This keeps the
// launcher's taskbar and Start in step with those stores, one way (native -> launcher), driven by changes:
//
//  - Taskbar: HKCU\...\Explorer\Taskband\Favorites (the real native pin list; the .lnk folder alone is not), watched with
//    RegNotifyChangeKeyValue. Start: start2.bin, watched with a FileSystemWatcher. Both cost nothing while idle, and a
//    watch that breaks (the key or folder deleted and re-created) is re-established.
//  - Each pass diffs the native sets against the snapshot the launcher last saw (native-pin-snapshot.json): pins ADDED
//    natively are added to the launcher (taskbar at the end; Start in the Start style the user uses), pins REMOVED
//    natively are removed from the launcher if present. Never a blind mirror: the launcher's own curated pins and the
//    pins it does not share with the native taskbar are left alone.
//  - An added pin that cannot be pinned yet for a reason that may go away (its shortcut is locked or still being
//    written, its program's drive is not there yet, its app is not in the app list yet) stays out of the snapshot and is
//    retried; only pins that were applied, already present or refused for good become part of it.
//  - First run (no snapshot): the native sets become the baseline. Only native taskbar pins made after the launcher's
//    pins were last saved are caught up (each Favorites entry carries its pin time). Start has no per-pin time, so its
//    first run is a baseline only.
//  - Native stores are only read: never written, the native hosts are never resumed to read them. When the launcher
//    is not running nothing happens (native stays native), and changes made meanwhile are picked up at the next start.
//  - Reads, diffs and file checks run on a short-lived STA worker; every launcher change runs on the UI thread in one
//    step (load -> change -> save -> live refresh) so a bar's or menu's own copy can never overwrite it.
internal static class NativePinSync
{
	internal const string AppsFolderPrefix = "shell:AppsFolder\\";

	internal static string DefaultLinkFolder => Path.Combine(
		Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
		"Microsoft\\Internet Explorer\\Quick Launch\\User Pinned\\TaskBar");

	[DllImport("shell32.dll")]
	private static extern int SHGetKnownFolderPath([MarshalAs(UnmanagedType.LPStruct)] Guid rfid, uint dwFlags, nint hToken, out nint ppszPath);

	private static readonly ConcurrentDictionary<Guid, string?> KnownFolders = new ConcurrentDictionary<Guid, string?>();

	internal static string? KnownFolderPath(Guid id)
	{
		return KnownFolders.GetOrAdd(id, static g =>
		{
			nint p = 0;
			try
			{
				return SHGetKnownFolderPath(g, 0, 0, out p) == 0 && p != 0 ? Marshal.PtrToStringUni(p) : null;
			}
			catch
			{
				return null;
			}
			finally
			{
				if (p != 0)
				{
					Marshal.FreeCoTaskMem(p);
				}
			}
		});
	}

	internal static string NormPath(string p)
	{
		string s = Environment.ExpandEnvironmentVariables(p.Trim().Trim('"'));
		try
		{
			if (Path.IsPathFullyQualified(s))
			{
				s = Path.GetFullPath(s);
			}
		}
		catch
		{
		}
		return s.ToLowerInvariant();
	}

	internal static string NormArgs(string? args)
	{
		return string.Join(' ', (args ?? string.Empty).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).ToLowerInvariant();
	}

	// Implicit (exe-derived) AppUserModelIDs are a full path or "{KnownFolderGuid}\relative\path". Null otherwise.
	internal static string? ExpandImplicit(string? aumid)
	{
		if (string.IsNullOrWhiteSpace(aumid))
		{
			return null;
		}
		string a = aumid.Trim();
		if (a.Length > 39 && a[0] == '{' && a[37] == '}' && a[38] == '\\' && Guid.TryParse(a.AsSpan(1, 36), out Guid folder))
		{
			string? baseDir = KnownFolderPath(folder);
			return baseDir == null ? null : Path.Combine(baseDir, a.Substring(39));
		}
		if (a.Length > 3 && a[1] == ':' && a[2] == '\\')
		{
			return a;
		}
		return null;
	}

	// Every identity a pin can be recognised by; two pins are the same app when their key sets intersect.
	internal static SortedSet<string> KeysFor(string? aumid, string? target, string? args)
	{
		SortedSet<string> keys = new SortedSet<string>(StringComparer.Ordinal);
		if (!string.IsNullOrWhiteSpace(aumid))
		{
			keys.Add("aumid:" + aumid.Trim().ToLowerInvariant());
			string? implicitPath = ExpandImplicit(aumid);
			if (implicitPath != null)
			{
				keys.Add("path:" + NormPath(implicitPath) + "|");
			}
		}
		if (!string.IsNullOrWhiteSpace(target))
		{
			keys.Add("path:" + NormPath(target) + "|" + NormArgs(args));
		}
		return keys;
	}

	// The same identities for a launcher pin, across the shapes the store holds: a plain exe (+args), an AppsFolder
	// item whose id is an AUMID, a full path or "{KnownFolder}\rel", an explicit Aumid, a Squirrel stub's real exe, and
	// the shell apps whose AppsFolder pin has no exe (File Explorer, Task Manager).
	internal static SortedSet<string> LauncherKeys(PinnedApp p)
	{
		SortedSet<string> keys = new SortedSet<string>(StringComparer.Ordinal);
		string lp = p.LaunchPath ?? string.Empty;
		string? appsId = null;
		if (lp.StartsWith(AppsFolderPrefix, StringComparison.OrdinalIgnoreCase))
		{
			appsId = lp.Substring(AppsFolderPrefix.Length);
			keys.UnionWith(KeysFor(appsId, null, null));
		}
		else if (!string.IsNullOrWhiteSpace(lp))
		{
			keys.UnionWith(KeysFor(null, lp, p.Args));
		}
		keys.UnionWith(KeysFor(p.Aumid, null, null));
		if (!string.IsNullOrWhiteSpace(p.ExePath) && !string.Equals(p.ExePath, lp, StringComparison.OrdinalIgnoreCase))
		{
			keys.Add("path:" + NormPath(p.ExePath) + "|");
		}
		string? shellExe = TaskbarPins.ShellPinExeName(p.Aumid ?? appsId);
		if (shellExe != null && string.IsNullOrWhiteSpace(p.Args))
		{
			string dir = shellExe.Equals("explorer.exe", StringComparison.OrdinalIgnoreCase)
				? Environment.GetFolderPath(Environment.SpecialFolder.Windows)
				: Environment.SystemDirectory;
			keys.Add("path:" + NormPath(Path.Combine(dir, shellExe)) + "|");
		}
		return keys;
	}

	// File-backed form of a launch path for Start matching: a plain path, or an AppsFolder id that is a path or
	// "{KnownFolder}\rel". Null for packaged/explicit AUMIDs.
	private static string? FileBacked(string launchPath)
	{
		string s = launchPath.Trim();
		if (s.StartsWith(AppsFolderPrefix, StringComparison.OrdinalIgnoreCase))
		{
			string? p = ExpandImplicit(s.Substring(AppsFolderPrefix.Length));
			return p == null ? null : NormPath(p);
		}
		return Path.IsPathFullyQualified(Environment.ExpandEnvironmentVariables(s)) ? NormPath(s) : null;
	}

	// Same app for Start purposes: equal launch paths, or the same backing file.
	internal static bool SameLaunchTarget(string? a, string? b)
	{
		if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b))
		{
			return false;
		}
		if (string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase))
		{
			return true;
		}
		string? fa = FileBacked(a);
		string? fb = fa == null ? null : FileBacked(b);
		return fa != null && fb != null && string.Equals(fa, fb, StringComparison.Ordinal);
	}

	// "P~<aumid>" / "W~<parsing name>" -> the AppsFolder id; false for placeholders and unknown kinds.
	internal static bool TryAppsFolderId(string? startId, out string appsId)
	{
		appsId = string.Empty;
		if (string.IsNullOrWhiteSpace(startId) || startId.Length < 3 || startId[1] != '~')
		{
			return false;
		}
		char kind = char.ToUpperInvariant(startId[0]);
		if (kind != 'P' && kind != 'W')
		{
			return false;
		}
		appsId = startId.Substring(2).Trim();
		return appsId.Length > 0;
	}

	// The app-list entry a launcher "Pin to Start" means, for the Windows 7 menu (it lists app-list entries only).
	// Tried in order: the same launch path; an app id (the window's or pin's AppUserModelID, or the one a shortcut
	// carries); the same program file (an entry whose AppsFolder id is a path); and an entry whose Start-menu shortcut
	// starts the program. The last step finds the apps that have an app id of their own (Chrome is "Chrome", Discord is
	// "com.squirrel.Discord.Discord"), whose AppsFolder ids are not paths, from just an exe path. program is the file
	// the item starts (null when it is not an .exe or a shortcut passes arguments of its own); startMenuLinkTarget gives
	// the target of the entry's Start-menu shortcut by display name.
	internal static AppEntry? FindStartEntry(IReadOnlyList<AppEntry> apps, string path, IEnumerable<string?> appIds, string? program, Func<string, string?> startMenuLinkTarget)
	{
		string p = path.Trim();
		AppEntry? hit = apps.FirstOrDefault(a => string.Equals(a.LaunchPath, p, StringComparison.OrdinalIgnoreCase));
		if (hit != null)
		{
			return hit;
		}
		foreach (string? raw in appIds)
		{
			if (string.IsNullOrWhiteSpace(raw))
			{
				continue;
			}
			string id = raw.Trim();
			string lp = AppsFolderPrefix + id;
			hit = apps.FirstOrDefault(a => string.Equals(a.LaunchPath, lp, StringComparison.OrdinalIgnoreCase))
				?? apps.FirstOrDefault(a => !string.IsNullOrEmpty(a.AppId) && string.Equals(a.AppId, id, StringComparison.OrdinalIgnoreCase));
			if (hit != null)
			{
				return hit;
			}
		}
		if (string.IsNullOrWhiteSpace(program) || !program.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || !Path.IsPathFullyQualified(Environment.ExpandEnvironmentVariables(program.Trim())))
		{
			return null;
		}
		hit = apps.FirstOrDefault(a => SameLaunchTarget(a.LaunchPath, program));
		if (hit != null)
		{
			return hit;
		}
		string prog = NormPath(program);
		foreach (AppEntry a in apps)
		{
			if (!a.LaunchPath.StartsWith(AppsFolderPrefix, StringComparison.OrdinalIgnoreCase) || a.LaunchPath.IndexOf('!') >= 0 || FileBacked(a.LaunchPath) != null)
			{
				continue;
			}
			string? target = null;
			try
			{
				target = startMenuLinkTarget(a.Name);
			}
			catch
			{
			}
			if (string.IsNullOrWhiteSpace(target))
			{
				continue;
			}
			string t = NormPath(target);
			if (t == prog || IsSquirrelStubFor(t, prog))
			{
				return a;
			}
		}
		return null;
	}

	// Squirrel installs start "<app>\Update.exe --processStart X.exe" for "<app>\app-<version>\X.exe" (normalized paths).
	internal static bool IsSquirrelStubFor(string updateExe, string program)
	{
		if (!string.Equals(Path.GetFileName(updateExe), "update.exe", StringComparison.OrdinalIgnoreCase))
		{
			return false;
		}
		string? versionDir = Path.GetDirectoryName(program);
		return versionDir != null
			&& Path.GetFileName(versionDir).StartsWith("app-", StringComparison.OrdinalIgnoreCase)
			&& string.Equals(Path.GetDirectoryName(versionDir), Path.GetDirectoryName(updateExe), StringComparison.OrdinalIgnoreCase);
	}
}

// One native pin as the launcher saw it (current read and snapshot). Plain settable properties: it round-trips JSON.
internal sealed class NativePinRecord
{
	public List<string> Keys { get; set; } = new List<string>();

	public string Name { get; set; } = string.Empty;

	// "lnk" (a shortcut in the User Pinned\TaskBar folder) or "packaged" (an AppsFolder item, no .lnk).
	public string Source { get; set; } = "lnk";

	public string? LinkName { get; set; }

	public string? Origin { get; set; }

	public DateTime? PinnedUtc { get; set; }

	// The AUMID Explorer resolved for the pin (identity only: never copied into a launcher pin's Aumid for .lnk pins).
	public string? Aumid { get; set; }

	public string? Target { get; set; }

	public string? Args { get; set; }

	public string? WorkingDir { get; set; }

	public string? IconPath { get; set; }

	public string? MatchExe { get; set; }

	// The shortcut could not be read (missing, locked by a scanner or half written): its keys are only what Favorites
	// says (the AppUserModelID), so it is matched by its shortcut name too, and an earlier full record is kept for it.
	[JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
	public bool Incomplete { get; set; }

	public override string ToString() => $"'{Name}'";
}

internal sealed class NativePinSnapshot
{
	public int Version { get; set; } = 1;

	public DateTime TakenUtc { get; set; }

	public bool TaskbarKnown { get; set; }

	public List<NativePinRecord> Taskbar { get; set; } = new List<NativePinRecord>();

	public bool StartKnown { get; set; }

	public long StartFileTime { get; set; }

	public List<string> Start { get; set; } = new List<string>();

	private static readonly JsonSerializerOptions Json = new JsonSerializerOptions { WriteIndented = true };

	public string Serialize() => JsonSerializer.Serialize(this, Json);

	public static NativePinSnapshot? Deserialize(string json)
	{
		try
		{
			NativePinSnapshot? s = JsonSerializer.Deserialize<NativePinSnapshot>(json, Json);
			return s != null && IsValid(s) ? s : null;
		}
		catch
		{
			return null;
		}
	}

	public static bool Validate(string json) => Deserialize(json) != null;

	private static bool IsValid(NativePinSnapshot s)
	{
		return s.Version >= 1
			&& s.Taskbar != null && s.Start != null
			&& s.Taskbar.All(r => r != null && r.Keys != null && r.Keys.Count > 0 && r.Keys.All(k => !string.IsNullOrWhiteSpace(k)))
			&& s.Start.All(id => !string.IsNullOrWhiteSpace(id));
	}

	public NativePinSnapshot Clone() => Deserialize(Serialize()) ?? new NativePinSnapshot();
}

// The launcher pin for an added native pin, built on the worker (it checks files), or why there is none.
internal sealed class BuiltPin
{
	public PinnedApp? Pin { get; init; }

	public string? Why { get; init; }

	// The reason may go away (the shortcut could not be read yet, or its program's drive is not there yet): retried.
	public bool Transient { get; init; }
}

// A launcher pin the sync removed in this session. When Windows takes a provisioned pin away and puts it back (a
// re-provisioning seen as two passes), the launcher pin returns where it was instead of being lost.
internal sealed class RemovedLauncherPin
{
	public required SortedSet<string> Keys { get; init; }

	public required PinnedApp Pin { get; init; }

	public int Index { get; init; }
}

internal sealed class TaskbarPinDiff
{
	public bool FirstRun { get; init; }

	public DateTime? CatchUpSinceUtc { get; init; }

	// Started by a native change seen while running (not the startup catch-up or a retry).
	public bool Live { get; set; }

	public List<NativePinRecord> Added { get; } = new List<NativePinRecord>();

	public List<NativePinRecord> Removed { get; } = new List<NativePinRecord>();

	public int Renamed { get; set; }

	// New native entries for an app that was already pinned natively (nothing to add).
	public List<NativePinRecord> SameAppAgain { get; } = new List<NativePinRecord>();

	// Every identity still pinned natively: a launcher pin that matches one of them is not removed by an unpin of
	// another native entry of the same app.
	public HashSet<string> NowKeys { get; } = new HashSet<string>(StringComparer.Ordinal);

	// First run: recent native user pins made before the catch-up threshold (logged when not on the launcher taskbar).
	public List<NativePinRecord> NearMisses { get; } = new List<NativePinRecord>();

	// Worker -> UI: the launcher pin for each added record (PinSyncEngine.PrepareTaskbar).
	public Dictionary<NativePinRecord, BuiltPin> Built { get; } = new Dictionary<NativePinRecord, BuiltPin>(ReferenceEqualityComparer.Instance);

	public IReadOnlyList<RemovedLauncherPin> RecentlyRemoved { get; set; } = Array.Empty<RemovedLauncherPin>();

	// UI -> worker (PlanTaskbar's results).
	public List<NativePinRecord> RetryLater { get; } = new List<NativePinRecord>();

	public List<RemovedLauncherPin> RemovedPins { get; } = new List<RemovedLauncherPin>();

	public List<RemovedLauncherPin> Restored { get; } = new List<RemovedLauncherPin>();

	// Native unpins of apps that are not on the launcher taskbar (Explorer's menu showed "Unpin" for them).
	public List<string> NotOnTaskbar { get; } = new List<string>();

	// Worker -> UI: removed native pins whose program is gone (an uninstall took the pin away, not the user).
	public HashSet<NativePinRecord> ProgramGone { get; } = new HashSet<NativePinRecord>(ReferenceEqualityComparer.Instance);

	public bool HasWork => Added.Count > 0 || Removed.Count > 0;
}

internal sealed class StartPinDiff
{
	public bool FirstRun { get; init; }

	public List<string> Added { get; } = new List<string>();

	public List<string> Removed { get; } = new List<string>();

	// Added ids that could not be pinned yet (the app is not in the app list yet): kept out of the snapshot, retried.
	public List<string> RetryLater { get; } = new List<string>();

	public bool HasWork => Added.Count > 0 || Removed.Count > 0;
}

// Deferred is 0 on purpose: an aborted UI-thread invoke returns default, which must mean "try again later".
internal enum PinSyncOutcome
{
	Deferred = 0,
	Applied = 1,
	Failed = 2
}

// The two halves of a pass. A change of one native store reads only that store.
[Flags]
internal enum PinSyncHalves
{
	None = 0,
	Taskbar = 1,
	Start = 2,
	Both = 3
}

// The launcher side of the sync. The live implementation marshals to the UI thread; the --pinsynctest one is in memory.
internal interface IPinSyncTarget
{
	// When the launcher's taskbar pins were last saved before this process started (first-run catch-up threshold).
	DateTime? FirstRunCatchUpSinceUtc { get; }

	PinSyncOutcome ApplyTaskbar(TaskbarPinDiff diff, List<string> log, out string? deferReason);

	PinSyncOutcome ApplyStart(StartPinDiff diff, List<string> log, out string? deferReason);
}

// The launcher's Start pins as the engine sees them: the Windows 7 menu's pinned list and the Metro board's top-level
// app tiles, plus the inventory used to resolve native ids.
internal interface IStartPinModel
{
	bool Win7Style { get; }

	(string LaunchPath, string Name)? Resolve(string appsFolderId);

	bool Win7Has(string launchPath);

	void Win7Add(string launchPath);

	int Win7Remove(string launchPath);

	bool MetroHas(string launchPath);

	bool MetroAdd(string launchPath);

	int MetroRemove(string launchPath);
}

internal interface IPinSnapshotStore
{
	// ok == false: a snapshot exists but cannot be read (the sync pauses rather than re-baselining). ok with null:
	// first run.
	NativePinSnapshot? Load(out bool ok);

	bool Save(NativePinSnapshot snapshot);
}

// Pure decisions, shared by the live target and the test harness.
internal static class PinSyncEngine
{
	// A pass that would add more than this many pins, or remove more than max(3, half the known set), is a reset,
	// restore or migration of the native store rather than the user's own pinning: re-baseline, change nothing.
	internal const int MaxAddsPerPass = 8;

	// First run: native user pins made this long before the catch-up threshold are reported when they are missing.
	internal static readonly TimeSpan NearMissWindow = TimeSpan.FromDays(14);

	internal static bool SameLink(NativePinRecord a, NativePinRecord b)
	{
		return a.LinkName != null && string.Equals(a.LinkName, b.LinkName, StringComparison.OrdinalIgnoreCase);
	}

	// The same native pin: a shared identity, or the same shortcut file when one side could not be read.
	internal static bool Matches(NativePinRecord a, NativePinRecord b)
	{
		return a.Keys.Any(b.Keys.Contains) || ((a.Incomplete || b.Incomplete) && SameLink(a, b));
	}

	internal static TaskbarPinDiff DiffTaskbar(IReadOnlyList<NativePinRecord>? previous, IReadOnlyList<NativePinRecord> now, DateTime? catchUpSinceUtc)
	{
		TaskbarPinDiff diff = new TaskbarPinDiff { FirstRun = previous == null, CatchUpSinceUtc = previous == null ? catchUpSinceUtc : null };
		diff.NowKeys.UnionWith(now.SelectMany(r => r.Keys));
		if (previous == null)
		{
			if (catchUpSinceUtc is DateTime since)
			{
				diff.Added.AddRange(now.Where(r => r.PinnedUtc is DateTime t && t > since));
				diff.NearMisses.AddRange(now
					.Where(r => IsUserPin(r) && r.PinnedUtc is DateTime t && t <= since && t > since - NearMissWindow)
					.OrderByDescending(r => r.PinnedUtc)
					.Take(10));
			}
			return diff;
		}
		HashSet<string> nowNames = new HashSet<string>(now.Where(r => r.LinkName != null).Select(r => r.LinkName!), StringComparer.OrdinalIgnoreCase);
		foreach (NativePinRecord r in now)
		{
			List<NativePinRecord> same = previous.Where(p => Matches(p, r)).ToList();
			if (same.Count == 0)
			{
				diff.Added.Add(r);
			}
			else if (!same.Any(p => string.Equals(p.LinkName, r.LinkName, StringComparison.OrdinalIgnoreCase)))
			{
				// Same app under a new .lnk name: a rename when the old name is gone, otherwise a second native pin of an
				// app that was already pinned natively. Neither is an unpin + pin.
				if (same.Any(p => p.LinkName != null && !nowNames.Contains(p.LinkName)))
				{
					diff.Renamed++;
				}
				else
				{
					diff.SameAppAgain.Add(r);
				}
			}
		}
		// Removed only when NONE of its identities is still present (an AUMID change or a .lnk rename is not an unpin).
		diff.Removed.AddRange(previous.Where(p => !now.Any(r => Matches(p, r))));
		return diff;
	}

	internal static string? TaskbarMassChange(TaskbarPinDiff diff, int previousCount)
	{
		int userAdds = diff.Added.Count(IsUserPin);
		if (userAdds > MaxAddsPerPass)
		{
			return diff.FirstRun
				? $"first-run catch-up has {userAdds} candidates (more than {MaxAddsPerPass}): looks like a restored profile, catch-up skipped"
				: $"{userAdds} pins added at once (more than {MaxAddsPerPass}): looks like a reset or restore of the native taskbar";
		}
		if (!diff.FirstRun && diff.Removed.Count > Math.Max(3, previousCount / 2))
		{
			return $"{diff.Removed.Count} of {previousCount} pins removed at once: looks like a reset of the native taskbar";
		}
		return null;
	}

	// Explorer stamps every pin with its origin; only "UserPinned" is the user's own action. SystemPinned, OOBEPinned,
	// CloudPinned, Restore*, Placeholder* and Programmable* are OS/OEM provisioning (a feature update adding a pin).
	internal static bool IsUserPin(NativePinRecord r)
	{
		return string.IsNullOrEmpty(r.Origin) || string.Equals(r.Origin, "UserPinned", StringComparison.OrdinalIgnoreCase);
	}

	// Worker side of a taskbar apply: builds the launcher pin for every added record, and notes the removed records
	// whose program is gone. Both check files, and a target on a sleeping, offline or network drive can block for
	// seconds, so this never runs on the UI thread. appInstalled answers for packaged apps (null: assume installed).
	internal static void PrepareTaskbar(TaskbarPinDiff diff, Func<string, bool>? appInstalled = null)
	{
		foreach (NativePinRecord a in diff.Added)
		{
			if (!diff.Built.ContainsKey(a))
			{
				PinnedApp? pin = BuildPin(a, out string? why, out bool transient);
				diff.Built[a] = new BuiltPin { Pin = pin, Why = why, Transient = transient };
			}
		}
		foreach (NativePinRecord r in diff.Removed)
		{
			bool gone;
			try
			{
				gone = r.Source == "packaged"
					? !string.IsNullOrWhiteSpace(r.Aumid) && appInstalled != null && !appInstalled(r.Aumid)
					: !string.IsNullOrWhiteSpace(r.Target) && !File.Exists(Environment.ExpandEnvironmentVariables(r.Target));
			}
			catch
			{
				gone = false;
			}
			if (gone)
			{
				diff.ProgramGone.Add(r);
			}
		}
	}

	private static PinnedApp? BuiltFor(TaskbarPinDiff diff, NativePinRecord a, out string? why, out bool transient)
	{
		if (!diff.Built.TryGetValue(a, out BuiltPin? built))
		{
			// Only the harness and the dry run plan without PrepareTaskbar; the service always prepares first.
			PinnedApp? pin = BuildPin(a, out string? w, out bool t);
			built = new BuiltPin { Pin = pin, Why = w, Transient = t };
			diff.Built[a] = built;
		}
		why = built.Why;
		transient = built.Transient;
		return built.Pin == null ? null : ClonePin(built.Pin);
	}

	internal static PinnedApp ClonePin(PinnedApp p) => new PinnedApp
	{
		Name = p.Name,
		LaunchPath = p.LaunchPath,
		Args = p.Args,
		ExePath = p.ExePath,
		IconPath = p.IconPath,
		Aumid = p.Aumid,
		GroupName = p.GroupName,
		WorkingDir = p.WorkingDir
	};

	private static string Describe(PinnedApp pin)
	{
		return pin.LaunchPath + (string.IsNullOrEmpty(pin.Args) ? "" : " " + pin.Args) + (pin.WorkingDir == null ? "" : "; in " + pin.WorkingDir);
	}

	// Applies a taskbar diff to the launcher's pin list in place (UI thread: no file checks here, see PrepareTaskbar).
	// Returns the number of changes to save; the diff's RetryLater / RemovedPins / Restored / NotOnTaskbar say what
	// else happened.
	internal static int PlanTaskbar(List<PinnedApp> launcher, TaskbarPinDiff diff, List<string> log)
	{
		List<(PinnedApp Pin, SortedSet<string> Keys)> known = launcher.Select(p => (p, NativePinSync.LauncherKeys(p))).ToList();
		HashSet<NativePinRecord> doneAdds = new HashSet<NativePinRecord>(ReferenceEqualityComparer.Instance);
		HashSet<NativePinRecord> doneRemovals = new HashSet<NativePinRecord>(ReferenceEqualityComparer.Instance);
		int changes = 0;

		// 1. An updated native pin (the same shortcut, now with another program and AppUserModelID) diffs as remove +
		// add: the launcher pin is updated in place, keeping its position, group and name.
		foreach (NativePinRecord r in diff.Removed)
		{
			NativePinRecord? a = r.LinkName == null ? null : diff.Added.FirstOrDefault(x => !doneAdds.Contains(x) && SameLink(x, r));
			if (a == null)
			{
				continue;
			}
			List<(PinnedApp Pin, SortedSet<string> Keys)> hits = known.Where(k => k.Keys.Overlaps(r.Keys) && !k.Keys.Overlaps(diff.NowKeys)).ToList();
			if (hits.Count != 1 || known.Any(k => k.Keys.Overlaps(a.Keys)))
			{
				continue;   // nothing to update, ambiguous, or the new program is pinned already: a plain remove + add
			}
			PinnedApp? built = BuiltFor(diff, a, out _, out _);
			if (built == null)
			{
				continue;
			}
			PinnedApp old = hits[0].Pin;
			int index = launcher.IndexOf(old);
			PinnedApp updated = new PinnedApp
			{
				Name = old.Name,
				LaunchPath = built.LaunchPath,
				Args = built.Args,
				ExePath = built.ExePath,
				IconPath = built.IconPath ?? old.IconPath,
				Aumid = built.Aumid,
				GroupName = old.GroupName,
				WorkingDir = built.WorkingDir
			};
			launcher[index] = updated;
			known.RemoveAll(k => ReferenceEquals(k.Pin, old));
			SortedSet<string> keys = NativePinSync.LauncherKeys(updated);
			keys.UnionWith(a.Keys);
			known.Add((updated, keys));
			doneAdds.Add(a);
			doneRemovals.Add(r);
			changes++;
			log.Add($"Pin sync: taskbar ~ '{old.Name}' updated in place (its native shortcut '{r.LinkName}' now starts {Describe(updated)})");
		}

		// 2. Pins added natively.
		foreach (NativePinRecord a in diff.Added)
		{
			if (doneAdds.Contains(a))
			{
				continue;
			}
			(PinnedApp Pin, SortedSet<string> Keys) hit = known.FirstOrDefault(k => k.Keys.Overlaps(a.Keys));
			if (hit.Pin != null)
			{
				log.Add($"Pin sync: taskbar = '{a.Name}' already pinned as '{hit.Pin.Name}'");
				continue;
			}
			if (!IsUserPin(a))
			{
				RemovedLauncherPin? back = diff.RecentlyRemoved.FirstOrDefault(x => x.Keys.Overlaps(a.Keys) && !diff.Restored.Contains(x));
				if (back != null)
				{
					PinnedApp restored = ClonePin(back.Pin);
					int at = Math.Clamp(back.Index, 0, launcher.Count);
					launcher.Insert(at, restored);
					SortedSet<string> rk = NativePinSync.LauncherKeys(restored);
					rk.UnionWith(a.Keys);
					known.Add((restored, rk));
					diff.Restored.Add(back);
					changes++;
					log.Add($"Pin sync: taskbar + '{restored.Name}' restored at position {at + 1} (Windows put the pin back, origin {a.Origin})");
					continue;
				}
				log.Add($"Pin sync: taskbar skip '{a.Name}' (origin {a.Origin}: not a user pin)");
				continue;
			}
			PinnedApp? pin = BuiltFor(diff, a, out string? why, out bool transient);
			if (pin == null)
			{
				log.Add($"Pin sync: taskbar skip '{a.Name}' ({why}{(transient ? "; retried later" : "")})");
				if (transient)
				{
					diff.RetryLater.Add(a);
				}
				continue;
			}
			launcher.Add(pin);
			SortedSet<string> pk = NativePinSync.LauncherKeys(pin);
			pk.UnionWith(a.Keys);
			known.Add((pin, pk));
			changes++;
			string when = a.PinnedUtc is DateTime t ? $"; pinned {t.ToLocalTime():yyyy-MM-dd HH:mm}" : "";
			log.Add($"Pin sync: taskbar + '{pin.Name}' ({Describe(pin)}{when})");
		}

		// 3. Pins removed natively. A launcher pin that is still the same app as another native entry stays.
		foreach (NativePinRecord r in diff.Removed)
		{
			if (doneRemovals.Contains(r))
			{
				continue;
			}
			List<(PinnedApp Pin, SortedSet<string> Keys)> overlapping = known.Where(k => k.Keys.Overlaps(r.Keys)).ToList();
			if (overlapping.Count == 0)
			{
				if (diff.ProgramGone.Contains(r))
				{
					log.Add($"Pin sync: taskbar '{r.Name}' left the native taskbar (its program is gone), not on the launcher taskbar");
					continue;
				}
				log.Add($"Pin sync: taskbar '{r.Name}' unpinned natively, not on the launcher taskbar");
				diff.NotOnTaskbar.Add(r.Name);
				continue;
			}
			foreach ((PinnedApp pin, SortedSet<string> keys) in overlapping)
			{
				if (keys.Overlaps(diff.NowKeys))
				{
					log.Add($"Pin sync: taskbar = '{pin.Name}' kept (unpinned natively as '{r.Name}', still pinned natively as another entry)");
					continue;
				}
				int index = launcher.IndexOf(pin);
				launcher.RemoveAt(index);
				known.RemoveAll(k => ReferenceEquals(k.Pin, pin));
				SortedSet<string> rememberKeys = new SortedSet<string>(keys, StringComparer.Ordinal);
				rememberKeys.UnionWith(r.Keys);
				diff.RemovedPins.Add(new RemovedLauncherPin { Keys = rememberKeys, Pin = ClonePin(pin), Index = index });
				changes++;
				log.Add($"Pin sync: taskbar - '{pin.Name}' (unpinned natively as '{r.Name}')");
			}
		}

		// 4. First run: say which recent native pins were not caught up (made before the launcher's pins were saved).
		foreach (NativePinRecord m in diff.NearMisses)
		{
			if (known.Any(k => k.Keys.Overlaps(m.Keys)))
			{
				continue;
			}
			string since = diff.CatchUpSinceUtc is DateTime s ? s.ToLocalTime().ToString("yyyy-MM-dd HH:mm") : "?";
			log.Add($"Pin sync: taskbar first run: '{m.Name}' was pinned natively {m.PinnedUtc?.ToLocalTime():yyyy-MM-dd HH:mm}, before the launcher's pins were last saved ({since}), and is not on the launcher taskbar: not added (pin it from the launcher, or unpin and pin it again in Explorer)");
		}
		return changes;
	}

	// The launcher pin for a native one, in the shapes the store already uses. Never puts a .lnk pin's resolved AUMID
	// into Aumid (that would turn its launch into an AppsFolder activation, which fails for desktop-only shortcuts).
	internal static PinnedApp? BuildPin(NativePinRecord r, out string? why) => BuildPin(r, out why, out _);

	internal static PinnedApp? BuildPin(NativePinRecord r, out string? why, out bool transient)
	{
		why = null;
		transient = false;
		string name = string.IsNullOrWhiteSpace(r.Name) ? (r.Aumid ?? "App") : r.Name;
		bool packagedAumid = !string.IsNullOrWhiteSpace(r.Aumid) && r.Aumid.IndexOf('!') > 0;
		if (r.Source == "packaged" || (string.IsNullOrWhiteSpace(r.Target) && !r.Incomplete && packagedAumid))
		{
			if (!packagedAumid)
			{
				why = "packaged pin without an app id";
				return null;
			}
			return new PinnedApp { Name = name, LaunchPath = NativePinSync.AppsFolderPrefix + r.Aumid, Aumid = r.Aumid };
		}
		if (string.IsNullOrWhiteSpace(r.Target))
		{
			if (r.Incomplete)
			{
				why = "shortcut could not be read yet";
				transient = true;
				return null;
			}
			why = "shortcut has no file target";
			return null;
		}
		string target = Environment.ExpandEnvironmentVariables(r.Target);
		if (!target.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
		{
			why = "target is not a program: " + target;
			return null;
		}
		if (!File.Exists(target))
		{
			// An offline or removable drive, or one not mounted yet at logon: may come back.
			why = "target not found: " + target;
			transient = true;
			return null;
		}
		string exe = string.IsNullOrWhiteSpace(r.MatchExe) ? target : r.MatchExe;
		bool fileExplorer = string.Equals(Path.GetFileName(exe), "explorer.exe", StringComparison.OrdinalIgnoreCase) && string.IsNullOrWhiteSpace(r.Args);
		return new PinnedApp
		{
			Name = name,
			LaunchPath = target,
			Args = string.IsNullOrWhiteSpace(r.Args) ? null : r.Args,
			ExePath = exe,
			IconPath = !string.IsNullOrWhiteSpace(r.IconPath) && File.Exists(r.IconPath) ? r.IconPath : null,
			Aumid = fileExplorer ? "Microsoft.Windows.Explorer" : null,
			WorkingDir = r.WorkingDir
		};
	}

	// The native taskbar as the next snapshot records it: the current read, without the added pins that are retried
	// later (so the next pass sees them as added again), and with the earlier full record for a pin whose shortcut could
	// not be read this time (so its identities are not narrowed to what Favorites alone says).
	internal static List<NativePinRecord> NextTaskbarBaseline(IReadOnlyList<NativePinRecord>? previous, IReadOnlyList<NativePinRecord> now, IEnumerable<NativePinRecord>? retryLater)
	{
		HashSet<NativePinRecord> retry = new HashSet<NativePinRecord>(retryLater ?? Enumerable.Empty<NativePinRecord>(), ReferenceEqualityComparer.Instance);
		List<NativePinRecord> next = new List<NativePinRecord>(now.Count);
		foreach (NativePinRecord r in now)
		{
			if (retry.Contains(r))
			{
				continue;
			}
			if (r.Incomplete && previous != null)
			{
				NativePinRecord? full = previous.FirstOrDefault(p => !p.Incomplete && SameLink(p, r)) ?? previous.FirstOrDefault(p => !p.Incomplete && Matches(p, r));
				if (full != null)
				{
					if (!next.Contains(full))
					{
						next.Add(full);
					}
					continue;
				}
			}
			next.Add(r);
		}
		return next;
	}

	internal static bool SameRecords(IReadOnlyList<NativePinRecord> a, IReadOnlyList<NativePinRecord> b)
	{
		if (a.Count != b.Count)
		{
			return false;
		}
		for (int i = 0; i < a.Count; i++)
		{
			if (!string.Equals(a[i].LinkName, b[i].LinkName, StringComparison.OrdinalIgnoreCase)
				|| !a[i].Keys.SequenceEqual(b[i].Keys, StringComparer.Ordinal)
				|| a[i].PinnedUtc != b[i].PinnedUtc
				|| a[i].Incomplete != b[i].Incomplete)
			{
				return false;
			}
		}
		return true;
	}

	internal static StartPinDiff DiffStart(IReadOnlyList<string>? previous, IReadOnlyList<string> now)
	{
		if (previous == null)
		{
			return new StartPinDiff { FirstRun = true };
		}
		HashSet<string> prev = new HashSet<string>(previous, StringComparer.OrdinalIgnoreCase);
		HashSet<string> cur = new HashSet<string>(now, StringComparer.OrdinalIgnoreCase);
		StartPinDiff diff = new StartPinDiff();
		diff.Added.AddRange(now.Where(id => !prev.Contains(id)).Distinct(StringComparer.OrdinalIgnoreCase));
		diff.Removed.AddRange(previous.Where(id => !cur.Contains(id)).Distinct(StringComparer.OrdinalIgnoreCase));
		return diff;
	}

	internal static string? StartMassChange(StartPinDiff diff, int previousCount)
	{
		if (diff.Added.Count > MaxAddsPerPass)
		{
			return $"{diff.Added.Count} Start pins added at once (more than {MaxAddsPerPass}): looks like a reset of the native Start layout";
		}
		if (diff.Removed.Count > Math.Max(3, previousCount / 2))
		{
			return $"{diff.Removed.Count} of {previousCount} Start pins removed at once: looks like a reset of the native Start layout";
		}
		return null;
	}

	// Applies a Start diff to the launcher's Start: adds go to the Start style in use (the Windows 7 menu's pinned list,
	// or the Metro board), removals come out of both. Ids of apps that are not in the app list (yet) are retried later.
	internal static int PlanStart(IStartPinModel model, StartPinDiff diff, List<string> log)
	{
		int changes = 0;
		string style = model.Win7Style ? "Windows 7 menu" : "Start screen";
		foreach (string id in diff.Added)
		{
			if (!NativePinSync.TryAppsFolderId(id, out string appsId))
			{
				log.Add($"Pin sync: Start skip '{id}' (not an app pin)");
				continue;
			}
			(string LaunchPath, string Name)? app = model.Resolve(appsId);
			if (app == null)
			{
				log.Add($"Pin sync: Start skip '{appsId}' (not installed; retried later)");
				diff.RetryLater.Add(id);
				continue;
			}
			string lp = app.Value.LaunchPath;
			bool present = model.Win7Style ? model.Win7Has(lp) : model.MetroHas(lp);
			if (present)
			{
				log.Add($"Pin sync: Start = '{app.Value.Name}' already pinned ({style})");
				continue;
			}
			if (model.Win7Style)
			{
				model.Win7Add(lp);
			}
			else if (!model.MetroAdd(lp))
			{
				log.Add($"Pin sync: Start skip '{app.Value.Name}' (could not be added to the {style}; retried later)");
				diff.RetryLater.Add(id);
				continue;
			}
			changes++;
			log.Add($"Pin sync: Start + '{app.Value.Name}' ({style}; {lp})");
		}
		foreach (string id in diff.Removed)
		{
			if (!NativePinSync.TryAppsFolderId(id, out string appsId))
			{
				continue;
			}
			(string LaunchPath, string Name)? app = model.Resolve(appsId);
			List<string> candidates = new List<string>();
			if (app != null)
			{
				candidates.Add(app.Value.LaunchPath);
			}
			string direct = NativePinSync.AppsFolderPrefix + appsId;
			if (!candidates.Any(c => string.Equals(c, direct, StringComparison.OrdinalIgnoreCase)))
			{
				candidates.Add(direct);
			}
			int removed = 0;
			foreach (string c in candidates)
			{
				removed += model.Win7Remove(c);
				removed += model.MetroRemove(c);
			}
			string label = app?.Name ?? appsId;
			if (removed == 0)
			{
				log.Add($"Pin sync: Start '{label}' unpinned natively, not pinned in the launcher's Start");
				continue;
			}
			changes += removed;
			log.Add($"Pin sync: Start - '{label}' (unpinned natively; {removed} launcher pin(s))");
		}
		return changes;
	}
}

// The real native stores, read only. ReadTaskbar must run on an STA thread (WScript.Shell is apartment COM).
internal sealed class NativePinSource
{
	public string TaskbandKeyPath { get; init; } = NativeTaskbarFavorites.KeyPath;

	public string LinkFolder { get; init; } = NativePinSync.DefaultLinkFolder;

	// Null: the Start half of the sync is off.
	public string? Start2Path { get; init; } = StartLayoutReader.DefaultPath;

	public Func<string, string?> PackagedName { get; init; } = AppLauncher.PackagedDisplayName;

	// Whether a packaged app is still installed (asked of the shell, not the session's identity cache).
	public Func<string, bool> AppInstalled { get; init; } = static aumid => ShellDropResolver.AppsFolderDisplayName(aumid) != null;

	public List<NativePinRecord>? ReadTaskbar(out string? error)
	{
		List<NativeTaskbarPin>? pins = NativeTaskbarFavorites.Read(TaskbandKeyPath, out int _, out error);
		if (pins == null)
		{
			return null;
		}
		List<NativePinRecord> records = new List<NativePinRecord>(pins.Count);
		dynamic? shell = null;
		try
		{
			foreach (NativeTaskbarPin pin in pins)
			{
				if (pin.Kind == "packaged")
				{
					records.Add(PackagedRecord(pin));
				}
				else if (pin.Kind == "lnk")
				{
					if (shell == null)
					{
						Type? shellType = Type.GetTypeFromProgID("WScript.Shell");
						shell = shellType == null ? null : Activator.CreateInstance(shellType);
						if (shell == null)
						{
							error = "WScript.Shell unavailable";
							return null;
						}
					}
					records.Add(LinkRecord(pin, shell));
				}
			}
			return records;
		}
		catch (Exception ex)
		{
			error = "native taskbar read failed: " + ex.Message;
			return null;
		}
		finally
		{
			if (shell != null)
			{
				try { Marshal.FinalReleaseComObject(shell); } catch { }
			}
		}
	}

	private NativePinRecord PackagedRecord(NativeTaskbarPin pin)
	{
		string aumid = pin.Aumid ?? string.Empty;
		string? name = null;
		try
		{
			name = PackagedName(aumid);
		}
		catch
		{
		}
		if (string.IsNullOrWhiteSpace(name))
		{
			int cut = aumid.IndexOfAny(new[] { '_', '!' });
			name = cut > 0 ? aumid.Substring(0, cut) : aumid;
		}
		return new NativePinRecord
		{
			Keys = NativePinSync.KeysFor(aumid, null, null).ToList(),
			Name = name,
			Source = "packaged",
			Origin = pin.Origin,
			PinnedUtc = pin.PinnedUtc,
			Aumid = pin.Aumid
		};
	}

	// The shortcut's bytes, read with sharing that tolerates readers and writers; null when it cannot be read now.
	private static byte[]? ReadShared(string path)
	{
		try
		{
			using FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
			if (fs.Length > 4 * 1024 * 1024)
			{
				return Array.Empty<byte>();
			}
			byte[] data = new byte[fs.Length];
			fs.ReadExactly(data);
			return data;
		}
		catch
		{
			return null;
		}
	}

	private NativePinRecord LinkRecord(NativeTaskbarPin pin, dynamic shell)
	{
		string linkName = pin.LnkName ?? string.Empty;
		string? target = null, args = null, workingDir = null, iconLocation = null;
		bool incomplete = false;
		bool safeName = linkName.Length > 4 && linkName.IndexOfAny(new[] { '\\', '/', ':' }) < 0;
		string path = safeName ? Path.Combine(LinkFolder, linkName) : string.Empty;
		if (safeName)
		{
			// The shell's shortcut object reads a locked or half-written file as an empty shortcut, without an error.
			// So the file is read here first: one that cannot be read now (a scanner holds it, it is still being
			// written, or it is missing) makes the record incomplete, which is retried rather than taken as "no program".
			byte[]? bytes = ReadShared(path);
			if (bytes == null || bytes.Length < 76 || BitConverter.ToUInt32(bytes, 0) != 76)
			{
				incomplete = true;
			}
			else
			{
				dynamic? sc = null;
				try
				{
					sc = shell.CreateShortcut(path);
					target = sc.TargetPath as string;
					args = sc.Arguments as string;
					workingDir = sc.WorkingDirectory as string;
					iconLocation = sc.IconLocation as string;
				}
				catch (Exception ex)
				{
					incomplete = true;
					Logger.Log($"Pin sync: could not read '{linkName}': {ex.Message}");
				}
				finally
				{
					if (sc != null)
					{
						try { Marshal.FinalReleaseComObject(sc); } catch { }
					}
				}
				if (string.IsNullOrWhiteSpace(target) && !string.IsNullOrWhiteSpace(ShellLink.GetTargetPath(bytes)))
				{
					incomplete = true;   // the file names a program the shell object did not return: read it again later
				}
			}
		}
		string name = Path.GetFileNameWithoutExtension(linkName);
		// File Explorer's pin is an ID-list-only shortcut (no file target); it is explorer.exe with the shell AppID.
		if (string.IsNullOrWhiteSpace(target)
			&& (string.Equals(pin.Aumid, "Microsoft.Windows.Explorer", StringComparison.OrdinalIgnoreCase) || name.Equals("File Explorer", StringComparison.OrdinalIgnoreCase)))
		{
			target = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");
			incomplete = false;
		}
		target = string.IsNullOrWhiteSpace(target) ? null : target;
		args = string.IsNullOrWhiteSpace(args) ? null : args;
		string? matchExe = null;
		if (target != null && args != null && target.EndsWith("Update.exe", StringComparison.OrdinalIgnoreCase))
		{
			matchExe = TaskbarPins.ResolveStubExe(target, args);
		}
		string? iconFile = null;
		if (!string.IsNullOrWhiteSpace(iconLocation))
		{
			string candidate = Environment.ExpandEnvironmentVariables(iconLocation.Split(',')[0].Trim());
			iconFile = candidate.Length > 0 && File.Exists(candidate) ? candidate : null;
		}
		List<string> keys = NativePinSync.KeysFor(pin.Aumid, target, args).ToList();
		if (keys.Count == 0)
		{
			keys.Add("lnk:" + linkName.ToLowerInvariant());
		}
		return new NativePinRecord
		{
			Keys = keys,
			Name = name,
			Source = "lnk",
			LinkName = linkName,
			Origin = pin.Origin,
			PinnedUtc = pin.PinnedUtc,
			Aumid = pin.Aumid,
			Target = target,
			Args = args,
			WorkingDir = TaskbarPins.LinkWorkingDir(workingDir, target),
			IconPath = iconFile,
			MatchExe = matchExe,
			Incomplete = incomplete
		};
	}

	public StartLayoutRead ReadStart()
	{
		if (Start2Path == null)
		{
			return new StartLayoutRead { Ok = false, Error = "Start sync off" };
		}
		if (!StartLayoutReader.Available && string.Equals(Start2Path, StartLayoutReader.DefaultPath, StringComparison.OrdinalIgnoreCase))
		{
			return new StartLayoutRead { Ok = false, Error = "Start sync off: the Start layout reader is not included in this build" };
		}
		return StartLayoutReader.TryRead(Start2Path);
	}
}

// %LOCALAPPDATA%\Win81Layer\native-pin-snapshot.json through the durable state store (journal + lastgood).
internal sealed class DurablePinSnapshotStore : IPinSnapshotStore
{
	private const string StateName = "native-pin-snapshot";

	internal static readonly string PrimaryPath = Path.Combine(
		Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
		"Win81Layer",
		"native-pin-snapshot.json");

	public NativePinSnapshot? Load(out bool ok)
	{
		ok = true;
		bool any = File.Exists(PrimaryPath) || File.Exists(DurableStateStore.PendingPath(StateName)) || File.Exists(DurableStateStore.LastGoodPath(StateName));
		if (!any)
		{
			return null;   // first run
		}
		DurableStateReadResult r = DurableStateStore.Read(StateName, PrimaryPath, null, NativePinSnapshot.Validate, 8);
		NativePinSnapshot? s = r.Json == null ? null : NativePinSnapshot.Deserialize(r.Json);
		ok = s != null;
		return s;
	}

	public bool Save(NativePinSnapshot snapshot)
	{
		if (SettingsStore.ReadOnlyDiagnostics)
		{
			return false;
		}
		try
		{
			return DurableStateStore.Commit(StateName, PrimaryPath, snapshot.Serialize(), NativePinSnapshot.Validate).IsDurable;
		}
		catch (Exception ex)
		{
			Logger.Log("Pin sync: snapshot save failed: " + ex.Message);
			return false;
		}
	}
}

internal sealed class PinSyncPassReport
{
	public string Reasons { get; init; } = string.Empty;

	public PinSyncHalves Halves { get; init; }

	public List<string> Lines { get; } = new List<string>();

	public bool TaskbarRead { get; set; }

	public bool StartRead { get; set; }

	public bool Deferred { get; set; }

	public bool SnapshotSaved { get; set; }

	public bool Paused { get; set; }

	// Added pins kept out of the snapshot to be retried.
	public int TaskbarPending { get; set; }

	public int StartPending { get; set; }

	public string? Error { get; set; }
}

// Watches the native stores and runs sync passes. One pass at a time on a short-lived STA thread; changes that arrive
// during a pass queue exactly one more. No thread, timer or poll exists while nothing changes (retries and watch
// recovery are bounded and only exist while something is waiting).
internal sealed class NativePinSyncService : IDisposable
{
	// Per half: deferral and pending-retry budgets, and the retry timer.
	private sealed class HalfState
	{
		public HalfState(string name)
		{
			Name = name;
		}

		public string Name { get; }

		public int Deferrals;

		public int PendingRetries;

		// What is waiting now: a newly waiting pin gets the full retry budget, even when an older one (an app that is
		// never installed) has used its own up.
		public HashSet<string> PendingIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

		public Timer? Retry;
	}

	private static readonly int[] DefaultPendingDelaysMs = { 5000, 15000, 60000, 300000 };

	private static readonly int[] DefaultWatchRecoveryDelaysMs = { 1000, 2000, 5000, 10000, 30000, 60000, 300000 };

	// About two hours of attempts with the default delays, then the watch waits for the next start.
	internal const int MaxWatchRecoveryAttempts = 30;

	private readonly NativePinSource _source;

	private readonly IPinSyncTarget _target;

	private readonly IPinSnapshotStore _store;

	private readonly int _taskbandDebounceMs;

	private readonly int _startDebounceMs;

	private readonly int _retryMs;

	private readonly int _maxDeferrals;

	private readonly int[] _pendingDelaysMs;

	private readonly int[] _watchRecoveryDelaysMs;

	private readonly object _gate = new object();

	private readonly HalfState _taskbarHalf = new HalfState("taskbar");

	private readonly HalfState _startHalf = new HalfState("Start");

	// Worker-only state (one pass at a time).
	private readonly List<RemovedLauncherPin> _recentRemovals = new List<RemovedLauncherPin>();

	private readonly HashSet<string> _loggedSkips = new HashSet<string>(StringComparer.Ordinal);

	private TaskbandChangeWatcher? _taskbandWatcher;

	private FileSystemWatcher? _startWatcher;

	private Timer? _startDebounce;

	private Timer? _startWatchRecovery;

	private int _startWatchAttempts;

	private bool _running;

	private bool _rerun;

	private string _reasons = string.Empty;

	private PinSyncHalves _halves;

	private volatile bool _disposed;

	private string? _lastTaskbarError;

	private string? _lastStartError;

	private bool _pausedLogged;

	private int _passCount;

	public NativePinSyncService(NativePinSource source, IPinSyncTarget target, IPinSnapshotStore store, int taskbandDebounceMs = 400, int startDebounceMs = 2000, int retryMs = 3000, int maxDeferrals = 100, int[]? pendingDelaysMs = null, int[]? watchRecoveryDelaysMs = null)
	{
		_source = source;
		_target = target;
		_store = store;
		_taskbandDebounceMs = taskbandDebounceMs;
		_startDebounceMs = startDebounceMs;
		_retryMs = retryMs;
		_maxDeferrals = maxDeferrals;
		_pendingDelaysMs = pendingDelaysMs is { Length: > 0 } ? pendingDelaysMs : DefaultPendingDelaysMs;
		_watchRecoveryDelaysMs = watchRecoveryDelaysMs is { Length: > 0 } ? watchRecoveryDelaysMs : DefaultWatchRecoveryDelaysMs;
	}

	public event Action<PinSyncPassReport>? PassCompleted;

	public int PassCount => Volatile.Read(ref _passCount);

	public bool TaskbarWatched => _taskbandWatcher?.IsArmed == true;

	public bool StartWatched
	{
		get
		{
			lock (_gate)
			{
				return _startWatcher != null;
			}
		}
	}

	public void Start(bool startupPass = true)
	{
		_taskbandWatcher = TaskbandChangeWatcher.TryCreate(_source.TaskbandKeyPath,
			() => RequestPass("taskbar change", PinSyncHalves.Taskbar),
			() => RequestPass("taskbar watch recovered", PinSyncHalves.Taskbar),
			_taskbandDebounceMs, _watchRecoveryDelaysMs, MaxWatchRecoveryAttempts, out string? error);
		if (_taskbandWatcher == null)
		{
			Logger.Log("Pin sync: native taskbar watch unavailable (" + error + "); taskbar changes are picked up at the next start");
		}
		if (_source.Start2Path != null)
		{
			_startDebounce = new Timer(_ => RequestPass("Start change", PinSyncHalves.Start), null, Timeout.Infinite, Timeout.Infinite);
			FileSystemWatcher? w = TryCreateStartWatcher(out string? startError);
			if (w == null)
			{
				Logger.Log("Pin sync: native Start watch unavailable (" + startError + "); Start changes are picked up at the next start");
			}
			else
			{
				lock (_gate)
				{
					_startWatcher = w;
				}
			}
		}
		if (startupPass)
		{
			RequestPass("startup", PinSyncHalves.Both);
		}
	}

	private FileSystemWatcher? TryCreateStartWatcher(out string? error)
	{
		error = null;
		try
		{
			string? dir = Path.GetDirectoryName(_source.Start2Path);
			if (dir == null || !Directory.Exists(dir))
			{
				error = "folder missing";
				return null;
			}
			FileSystemWatcher w = new FileSystemWatcher(dir, Path.GetFileName(_source.Start2Path!))
			{
				NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName,
				IncludeSubdirectories = false
			};
			w.Changed += OnStartFileEvent;
			w.Created += OnStartFileEvent;
			w.Renamed += OnStartFileEvent;
			w.Error += OnStartWatchError;
			w.EnableRaisingEvents = true;
			return w;
		}
		catch (Exception ex)
		{
			error = ex.Message;
			return null;
		}
	}

	private void OnStartFileEvent(object sender, FileSystemEventArgs e)
	{
		try
		{
			if (!_disposed)
			{
				_startDebounce?.Change(_startDebounceMs, Timeout.Infinite);
			}
		}
		catch (ObjectDisposedException)
		{
		}
	}

	// The watcher stops for good after a buffer overflow or when its folder is deleted (the Start host's LocalState
	// reset or re-created): it is replaced, with backoff while the folder is missing, and a Start pass catches up.
	private void OnStartWatchError(object sender, ErrorEventArgs e)
	{
		lock (_gate)
		{
			if (_disposed || !ReferenceEquals(sender, _startWatcher))
			{
				return;
			}
			_startWatcher = null;
			_startWatchAttempts = 0;
		}
		try
		{
			FileSystemWatcher w = (FileSystemWatcher)sender;
			w.EnableRaisingEvents = false;
			w.Dispose();
		}
		catch
		{
		}
		Logger.Log("Pin sync: native Start watch lost (" + (e.GetException()?.Message ?? "error") + "); re-creating it");
		ScheduleStartWatchRecovery();
	}

	private void ScheduleStartWatchRecovery()
	{
		bool gaveUp = false;
		lock (_gate)
		{
			if (_disposed)
			{
				return;
			}
			int attempt = _startWatchAttempts++;
			if (attempt >= MaxWatchRecoveryAttempts)
			{
				gaveUp = true;
			}
			else
			{
				_startWatchRecovery ??= new Timer(_ => TryRecoverStartWatch(), null, Timeout.Infinite, Timeout.Infinite);
				_startWatchRecovery.Change(_watchRecoveryDelaysMs[Math.Min(attempt, _watchRecoveryDelaysMs.Length - 1)], Timeout.Infinite);
			}
		}
		if (gaveUp)
		{
			Logger.Log("Pin sync: native Start watch could not be re-created; Start changes are picked up at the next start");
		}
	}

	private void TryRecoverStartWatch()
	{
		if (_disposed)
		{
			return;
		}
		FileSystemWatcher? w = TryCreateStartWatcher(out _);
		if (w == null)
		{
			ScheduleStartWatchRecovery();
			return;
		}
		lock (_gate)
		{
			if (_disposed)
			{
				w.Dispose();
				return;
			}
			_startWatcher = w;
			_startWatchAttempts = 0;
		}
		Logger.Log("Pin sync: native Start watch recovered");
		RequestPass("Start watch recovered", PinSyncHalves.Start);
	}

	public void RequestPass(string reason, PinSyncHalves halves = PinSyncHalves.Both)
	{
		lock (_gate)
		{
			if (_disposed)
			{
				return;
			}
			if (_reasons.Length == 0)
			{
				_reasons = reason;
			}
			else if (!_reasons.Contains(reason, StringComparison.Ordinal))
			{
				_reasons += ", " + reason;
			}
			_halves |= halves == PinSyncHalves.None ? PinSyncHalves.Both : halves;
			if (_running)
			{
				_rerun = true;
				return;
			}
			_running = true;
		}
		Thread t = new Thread(Worker)
		{
			IsBackground = true,
			Name = "NativePinSync",
			Priority = ThreadPriority.BelowNormal
		};
		t.SetApartmentState(ApartmentState.STA);
		t.Start();
	}

	private void Worker()
	{
		while (true)
		{
			string reasons;
			PinSyncHalves halves;
			lock (_gate)
			{
				reasons = _reasons;
				halves = _halves == PinSyncHalves.None ? PinSyncHalves.Both : _halves;
				_reasons = string.Empty;
				_halves = PinSyncHalves.None;
				_rerun = false;
			}
			PinSyncPassReport report;
			try
			{
				report = RunPass(reasons, halves);
			}
			catch (Exception ex)
			{
				report = new PinSyncPassReport { Reasons = reasons, Halves = halves, Error = ex.ToString() };
				Logger.Log("Pin sync: pass failed: " + ex.Message);
			}
			Interlocked.Increment(ref _passCount);
			try
			{
				PassCompleted?.Invoke(report);
			}
			catch
			{
			}
			lock (_gate)
			{
				if (_disposed || !_rerun)
				{
					_running = false;
					return;
				}
			}
		}
	}

	private PinSyncPassReport RunPass(string reasons, PinSyncHalves halves)
	{
		PinSyncPassReport report = new PinSyncPassReport { Reasons = reasons, Halves = halves };
		List<string> lines = report.Lines;
		NativePinSnapshot? snapshot = _store.Load(out bool storeOk);
		if (!storeOk)
		{
			report.Paused = true;
			if (!_pausedLogged)
			{
				_pausedLogged = true;
				Logger.Log("Pin sync: snapshot unreadable; sync paused (nothing changed)");
			}
			return report;
		}
		_pausedLogged = false;
		NativePinSnapshot next = snapshot?.Clone() ?? new NativePinSnapshot();
		bool dirty = false;
		bool taskbarDeferred = false, startDeferred = false;
		string? taskbarWhy = null, startWhy = null;
		List<string> taskbarPending = new List<string>(), startPending = new List<string>();
		bool doTaskbar = (halves & PinSyncHalves.Taskbar) != 0;
		bool doStart = (halves & PinSyncHalves.Start) != 0 && _source.Start2Path != null;

		// ---- taskbar
		if (doTaskbar)
		{
			List<NativePinRecord>? taskbar = _source.ReadTaskbar(out string? tbError);
			if (taskbar == null)
			{
				if (!string.Equals(tbError, _lastTaskbarError, StringComparison.Ordinal))
				{
					lines.Add("Pin sync: native taskbar unreadable (" + tbError + "); nothing changed");
				}
				_lastTaskbarError = tbError;
			}
			else
			{
				_lastTaskbarError = null;
				report.TaskbarRead = true;
				bool first = snapshot == null || !snapshot.TaskbarKnown;
				IReadOnlyList<NativePinRecord>? previous = first ? null : snapshot!.Taskbar;
				DateTime? since = first ? _target.FirstRunCatchUpSinceUtc : null;
				TaskbarPinDiff diff = PinSyncEngine.DiffTaskbar(previous, taskbar, since);
				diff.Live = reasons.Contains("taskbar change", StringComparison.Ordinal);
				if (first)
				{
					lines.Add($"Pin sync: taskbar first run: baseline {taskbar.Count} native pins; catch-up of pins made after {(since is DateTime s ? s.ToLocalTime().ToString("yyyy-MM-dd HH:mm") : "(no launcher pins file: none)")}: {diff.Added.Count}");
				}
				string? mass = PinSyncEngine.TaskbarMassChange(diff, previous?.Count ?? 0);
				List<NativePinRecord>? baseline = null;
				if (mass != null)
				{
					lines.Add("Pin sync: taskbar " + mass + "; re-baselined, launcher unchanged");
					baseline = PinSyncEngine.NextTaskbarBaseline(previous, taskbar, null);
				}
				else
				{
					if (diff.Renamed > 0)
					{
						lines.Add($"Pin sync: taskbar {diff.Renamed} native pin(s) renamed; launcher unchanged");
					}
					foreach (NativePinRecord again in diff.SameAppAgain)
					{
						lines.Add($"Pin sync: taskbar = '{again.Name}' is another native pin of an app already pinned natively; launcher unchanged");
					}
					if (diff.HasWork || diff.NearMisses.Count > 0)
					{
						PinSyncEngine.PrepareTaskbar(diff, _source.AppInstalled);   // file and shell checks stay on this worker
						diff.RecentlyRemoved = _recentRemovals.ToList();
						PinSyncOutcome outcome = _target.ApplyTaskbar(diff, lines, out string? why);
						if (outcome == PinSyncOutcome.Applied)
						{
							baseline = PinSyncEngine.NextTaskbarBaseline(previous, taskbar, diff.RetryLater);
							report.TaskbarPending = diff.RetryLater.Count;
							taskbarPending.AddRange(diff.RetryLater.Select(r => r.LinkName ?? r.Keys.FirstOrDefault() ?? r.Name));
							RememberRemovals(diff);
						}
						else if (outcome == PinSyncOutcome.Deferred)
						{
							taskbarDeferred = true;
							taskbarWhy = why;
						}
						else
						{
							lines.Add("Pin sync: taskbar change not saved; it is re-checked on the next change or start");
						}
					}
					else
					{
						baseline = PinSyncEngine.NextTaskbarBaseline(previous, taskbar, null);
						if (previous != null && PinSyncEngine.SameRecords(previous, baseline))
						{
							baseline = null;
						}
					}
				}
				if (baseline != null)
				{
					next.Taskbar = baseline;
					next.TaskbarKnown = true;
					dirty = true;
				}
			}
		}

		// ---- Start
		if (doStart)
		{
			StartLayoutRead start = _source.ReadStart();
			if (!start.Ok)
			{
				if (!string.Equals(start.Error, _lastStartError, StringComparison.Ordinal))
				{
					lines.Add("Pin sync: native Start layout unreadable (" + start.Error + "); Start sync paused, nothing changed");
				}
				_lastStartError = start.Error;
			}
			else
			{
				_lastStartError = null;
				report.StartRead = true;
				bool first = snapshot == null || !snapshot.StartKnown;
				StartPinDiff diff = PinSyncEngine.DiffStart(first ? null : snapshot!.Start, start.Ids);
				if (first)
				{
					lines.Add($"Pin sync: Start first run: baseline {start.Ids.Count} native pins (the native Start store keeps no pin times, so nothing is caught up)");
				}
				string? mass = first ? null : PinSyncEngine.StartMassChange(diff, snapshot!.Start.Count);
				List<string>? baseline = null;
				if (mass != null)
				{
					lines.Add("Pin sync: " + mass + "; re-baselined, launcher unchanged");
					baseline = start.Ids;
				}
				else if (diff.HasWork)
				{
					PinSyncOutcome outcome = _target.ApplyStart(diff, lines, out string? why);
					if (outcome == PinSyncOutcome.Applied)
					{
						HashSet<string> retry = new HashSet<string>(diff.RetryLater, StringComparer.OrdinalIgnoreCase);
						baseline = start.Ids.Where(id => !retry.Contains(id)).ToList();
						report.StartPending = diff.RetryLater.Count;
						startPending.AddRange(diff.RetryLater);
					}
					else if (outcome == PinSyncOutcome.Deferred)
					{
						startDeferred = true;
						startWhy = why;
					}
					else
					{
						lines.Add("Pin sync: Start change not saved; it is re-checked on the next change or start");
					}
				}
				else if (first || !start.Ids.SequenceEqual(snapshot!.Start, StringComparer.OrdinalIgnoreCase))
				{
					baseline = start.Ids;
				}
				if (baseline != null)
				{
					next.Start = baseline;
					next.StartFileTime = start.FileTime;
					next.StartKnown = true;
					dirty = true;
				}
			}
		}

		if (dirty)
		{
			next.TakenUtc = DateTime.UtcNow;
			report.SnapshotSaved = _store.Save(next);
			if (!report.SnapshotSaved)
			{
				lines.Add("Pin sync: snapshot not saved; the same native changes are re-checked next time (adds and removals are idempotent)");
			}
		}
		if (doTaskbar)
		{
			Schedule(_taskbarHalf, PinSyncHalves.Taskbar, taskbarDeferred, taskbarWhy, taskbarPending, lines);
		}
		if (doStart)
		{
			Schedule(_startHalf, PinSyncHalves.Start, startDeferred, startWhy, startPending, lines);
		}
		report.Deferred = taskbarDeferred || startDeferred;
		if (_loggedSkips.Count > 1000)
		{
			_loggedSkips.Clear();
		}
		foreach (string line in lines)
		{
			// A pin that is retried logs its skip once, not on every retry.
			bool skip = line.StartsWith("Pin sync: taskbar skip ", StringComparison.Ordinal) || line.StartsWith("Pin sync: Start skip ", StringComparison.Ordinal);
			if (skip && !_loggedSkips.Add(line))
			{
				continue;
			}
			Logger.Log(line);
		}
		return report;
	}

	private void RememberRemovals(TaskbarPinDiff diff)
	{
		foreach (RemovedLauncherPin back in diff.Restored)
		{
			_recentRemovals.Remove(back);
		}
		_recentRemovals.AddRange(diff.RemovedPins);
		if (_recentRemovals.Count > 32)
		{
			_recentRemovals.RemoveRange(0, _recentRemovals.Count - 32);
		}
	}

	// Each half has its own budget and timer: a Start that stays deferred (the Start screen left open) never uses up
	// the taskbar's retries, and a retry reads only the half that waits.
	private void Schedule(HalfState half, PinSyncHalves which, bool deferred, string? why, List<string> pending, List<string> lines)
	{
		int delay = -1;
		if (deferred)
		{
			half.Deferrals++;
			if (half.Deferrals == 1)
			{
				lines.Add($"Pin sync: {half.Name} waiting ({why ?? "launcher busy"})");
			}
			if (half.Deferrals <= _maxDeferrals)
			{
				delay = _retryMs;
			}
			else if (half.Deferrals == _maxDeferrals + 1)
			{
				lines.Add($"Pin sync: {half.Name} still waiting; retried on the next native change or start");
			}
		}
		else
		{
			half.Deferrals = 0;
			if (pending.Count > 0)
			{
				if (pending.Any(id => !half.PendingIds.Contains(id)))
				{
					half.PendingRetries = 0;
				}
				half.PendingIds = new HashSet<string>(pending, StringComparer.OrdinalIgnoreCase);
				half.PendingRetries++;
				if (half.PendingRetries <= _pendingDelaysMs.Length)
				{
					delay = _pendingDelaysMs[half.PendingRetries - 1];
				}
				else if (half.PendingRetries == _pendingDelaysMs.Length + 1)
				{
					lines.Add($"Pin sync: {half.Name}: {pending.Count} pin(s) still waiting; retried on the next native change or start");
				}
			}
			else
			{
				half.PendingRetries = 0;
				half.PendingIds.Clear();
			}
		}
		if (delay < 0)
		{
			return;
		}
		lock (_gate)
		{
			if (!_disposed)
			{
				half.Retry ??= new Timer(_ => RequestPass("retry", which), null, Timeout.Infinite, Timeout.Infinite);
				half.Retry.Change(delay, Timeout.Infinite);
			}
		}
	}

	public void Dispose()
	{
		FileSystemWatcher? startWatcher;
		lock (_gate)
		{
			if (_disposed)
			{
				return;
			}
			_disposed = true;
			startWatcher = _startWatcher;
			_startWatcher = null;
		}
		try { (_target as IDisposable)?.Dispose(); } catch { }
		try { _taskbandWatcher?.Dispose(); } catch { }
		try
		{
			if (startWatcher != null)
			{
				startWatcher.EnableRaisingEvents = false;
				startWatcher.Dispose();
			}
		}
		catch
		{
		}
		try { _startDebounce?.Dispose(); } catch { }
		try { _startWatchRecovery?.Dispose(); } catch { }
		try { _taskbarHalf.Retry?.Dispose(); } catch { }
		try { _startHalf.Retry?.Dispose(); } catch { }
	}
}
