#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace Win81Layer;

internal enum PointerKind
{
	Windows,
	Win81,
	Alchemy
}

// What the pointers should be. Palette is the Alchemy palette id (empty otherwise); Mode is the Alchemy setting
// ("dark", "light" or "auto") that produced it.
internal sealed record PointerTarget(PointerKind Kind, string Palette, string Mode)
{
	public static readonly PointerTarget Windows = new PointerTarget(PointerKind.Windows, "", "");

	public bool Is(PointerApplied? applied)
	{
		if (applied == null)
		{
			return Kind == PointerKind.Windows;
		}
		return string.Equals(applied.Kind, Kind.ToString(), StringComparison.Ordinal)
			&& string.Equals(applied.Palette ?? "", Palette, StringComparison.OrdinalIgnoreCase);
	}
}

// One registry value exactly as Windows stores it: name (original casing, "" for the default value), kind (a
// RegistryValueKind name) and data (DWORD/QWORD as decimal, binary as base64, multi-string joined with NUL).
internal sealed class RegValue
{
	public string Name { get; set; } = "";

	public string Kind { get; set; } = nameof(RegistryValueKind.String);

	public string Data { get; set; } = "";

	public RegValue()
	{
	}

	public RegValue(string name, RegistryValueKind kind, string data)
	{
		Name = name;
		Kind = kind.ToString();
		Data = data;
	}

	public RegValue Clone()
	{
		return new RegValue { Name = Name, Kind = Kind, Data = Data };
	}

	public string Expanded()
	{
		return string.Equals(Kind, nameof(RegistryValueKind.ExpandString), StringComparison.Ordinal) ? Environment.ExpandEnvironmentVariables(Data ?? "") : (Data ?? "");
	}

	public override string ToString()
	{
		return $"{(Name.Length == 0 ? "(Default)" : Name)}[{Kind}]={Data}";
	}
}

// The user's own pointers before the launcher first changed them. "Windows default" writes exactly these back.
internal sealed class PointerOriginal
{
	public DateTime CapturedUtc { get; set; }

	// captured | legacy-snapshot | system-default-fallback
	public string Source { get; set; } = "captured";

	// The 17 roles, "(Default)" (the scheme name) and "Scheme Source"; null = the value did not exist.
	public Dictionary<string, RegValue?> Values { get; set; } = new Dictionary<string, RegValue?>(StringComparer.Ordinal);

	// HKCU scheme entries the launcher writes ("Alchemy pointers"); null = it did not exist.
	public Dictionary<string, RegValue?> Schemes { get; set; } = new Dictionary<string, RegValue?>(StringComparer.Ordinal);

	public PointerOriginal Clone()
	{
		return new PointerOriginal
		{
			CapturedUtc = CapturedUtc,
			Source = Source,
			Values = Values.ToDictionary(kv => kv.Key, kv => kv.Value?.Clone(), StringComparer.Ordinal),
			Schemes = Schemes.ToDictionary(kv => kv.Key, kv => kv.Value?.Clone(), StringComparer.Ordinal)
		};
	}
}

internal sealed class PointerApplied
{
	public string Kind { get; set; } = "";

	public string Palette { get; set; } = "";

	public Dictionary<string, RegValue?> Values { get; set; } = new Dictionary<string, RegValue?>(StringComparer.Ordinal);

	public DateTime AppliedUtc { get; set; }
}

internal sealed class PointerStateFile
{
	public int SchemaVersion { get; set; } = 1;

	// idle | applying | restoring
	public string Phase { get; set; } = PointerScheme.PhaseIdle;

	public PointerOriginal? Original { get; set; }

	public PointerApplied? Applied { get; set; }

	public string TargetKind { get; set; } = "";

	public string TargetPalette { get; set; } = "";

	public string LastResult { get; set; } = "";

	public string LastError { get; set; } = "";

	// Set when the last change found pointers changed outside the launcher and kept them as the user's own.
	public DateTime DriftMergedUtc { get; set; }

	public DateTime UpdatedUtc { get; set; }
}

internal sealed class PointerResult
{
	public bool Success { get; set; }

	public bool Changed { get; set; }

	public PointerKind Kind { get; set; }

	public string Palette { get; set; } = "";

	public string Message { get; set; } = "";
}

internal sealed class PointerStatus
{
	public bool Busy { get; set; }

	public bool BusyAlchemy { get; set; }

	public bool Unreadable { get; set; }

	public string Phase { get; set; } = PointerScheme.PhaseIdle;

	public PointerKind AppliedKind { get; set; }

	public string AppliedPalette { get; set; } = "";

	public string Mode { get; set; } = "";

	public bool Drift { get; set; }

	public string Failure { get; set; } = "";
}

// Raised by the world when the restore point (or the legacy snapshot) exists but cannot be read right now. The engine
// then changes nothing, so a degraded read can never be saved as the user's own pointers.
internal sealed class PointerReadException : Exception
{
	public PointerReadException(string message)
		: base(message)
	{
	}
}

// Everything the engine reads or changes goes through this, so the self-test can drive it with a fake.
internal interface IPointerWorld
{
	// Folder the launcher draws its Alchemy pointers into; a pointer under it is never the user's own.
	string AlchemyRoot { get; }

	// Folder the launcher copies its Windows 8.1 pointer files into; also never the user's own.
	string Win81AssetsRoot { get; }

	DateTime UtcNow { get; }

	// HKCU\Control Panel\Cursors, keyed case-insensitively by value name ("" = the default value).
	Dictionary<string, RegValue> ReadRegistry();

	RegValue? ReadSchemeEntry(string name);

	// Writes in order; a null value deletes. A value whose name differs only in case is replaced, so the stored name
	// ends up exactly as given.
	void WriteValues(IReadOnlyList<(string Name, RegValue? Value)> values);

	void WriteSchemeEntry(string name, RegValue? value);

	// Asks Windows to load the pointers from the registry; false when Windows refused.
	bool RefreshSystemCursors();

	// Throws PointerReadException when a restore point exists but could not be read.
	PointerStateFile LoadState();

	bool SaveState(PointerStateFile state);

	// The snapshot older builds kept for the Windows 8.1 pointers (role/"(Default)" to path), null when there is none.
	Dictionary<string, string>? LegacySnapshot();

	void DeleteLegacySnapshot();

	// HKLM's default scheme, verbatim.
	Dictionary<string, RegValue> SystemDefault();

	IReadOnlyDictionary<CursorRole, string> ResolveWin81();

	AlchemyPaletteFiles? EnsurePalette(string paletteId);

	void CleanupPalettes(IReadOnlyCollection<string> keep);
}

// The transaction logic. Every registry change is preceded by a durable state write that holds the user's own
// pointers, so a crash anywhere is finished on the next start by writing the same full set again.
internal sealed class PointerEngine
{
	private readonly IPointerWorld _w;

	public PointerEngine(IPointerWorld world)
	{
		_w = world;
	}

	public PointerResult Apply(PointerTarget target)
	{
		try
		{
			return ApplyCore(target);
		}
		catch (PointerReadException ex)
		{
			Logger.Log("Pointers: change refused: " + ex.Message);
			return Fail(target, ex.Message);
		}
	}

	// Finishes an interrupted change by applying what the settings ask for now.
	public PointerResult Recover(Func<PointerTarget> effective)
	{
		try
		{
			PointerStateFile st = _w.LoadState();
			if (st.Phase == PointerScheme.PhaseIdle)
			{
				return new PointerResult { Success = true, Message = "Nothing to recover." };
			}
			PointerTarget target = effective();
			Logger.Log($"Pointers: finishing an interrupted change (phase={st.Phase}) -> {Describe(target)}");
			return ApplyCore(target);
		}
		catch (PointerReadException ex)
		{
			Logger.Log("Pointers: recovery postponed: " + ex.Message);
			return new PointerResult { Success = false, Message = ex.Message };
		}
	}

	private PointerResult ApplyCore(PointerTarget target)
	{
		PointerStateFile st = _w.LoadState();
		bool interrupted = st.Phase != PointerScheme.PhaseIdle;
		Dictionary<string, RegValue> cur = _w.ReadRegistry();
		PointerOriginal? orig = st.Original?.Clone();
		bool drift = false;
		if (orig != null && !interrupted)
		{
			// A committed restore point supersedes the older build's snapshot; one left by an interrupted run goes now.
			_w.DeleteLegacySnapshot();
		}
		if (orig == null)
		{
			Dictionary<string, string>? legacy = _w.LegacySnapshot();
			if (legacy != null)
			{
				orig = ImportLegacy(legacy, cur);
				Logger.Log("Pointers: imported the pointers saved by an older build as the ones Windows default restores.");
			}
			else if (LooksLauncherOwned(cur))
			{
				// The restore point was lost while launcher pointers were on: never take those as the user's own.
				orig = FromSystemDefault();
				Logger.Log("Pointers: no restore point while launcher pointers are on; Windows default will use the system pointers.");
			}
			else if (target.Kind != PointerKind.Windows)
			{
				orig = Capture(cur);
			}
		}
		else if (!interrupted && st.Applied != null)
		{
			drift = MergeDrift(orig, st.Applied, cur);
		}

		if (target.Kind == PointerKind.Windows)
		{
			if (orig == null)
			{
				if (interrupted)
				{
					_w.SaveState(new PointerStateFile { Phase = PointerScheme.PhaseIdle, LastResult = "recovered: no restore point" });
				}
				return Ok(target, changed: false, "Your own Windows pointers are on.");
			}
			return Restore(st, orig, target);
		}
		return ApplyScheme(st, orig!, cur, target, interrupted, drift);
	}

	private PointerResult Restore(PointerStateFile st, PointerOriginal orig, PointerTarget target)
	{
		PointerStateFile pending = new PointerStateFile
		{
			Phase = PointerScheme.PhaseRestoring,
			Original = orig,
			Applied = st.Applied,
			TargetKind = nameof(PointerKind.Windows),
			LastResult = "restoring",
			DriftMergedUtc = st.DriftMergedUtc
		};
		if (!_w.SaveState(pending))
		{
			// The state still holds the restore point (or the legacy snapshot is still there); a crash from here is
			// finished on the next start.
			Logger.Log("Pointers: could not mark the restore as pending; continuing with the restore.");
		}
		Dictionary<string, RegValue> cur = _w.ReadRegistry();
		_w.WriteValues(OriginalWrites(orig, cur));
		_w.WriteSchemeEntry(PointerScheme.AlchemySchemeName, SchemeOf(orig));
		bool refreshed = _w.RefreshSystemCursors();
		Dictionary<string, RegValue> back = _w.ReadRegistry();
		string? mismatch = VerifyOriginal(orig, back);
		if (mismatch != null)
		{
			Logger.Log("Pointers: restore did not verify (" + mismatch + "); it will be finished on the next start.");
			return Fail(target, "Your pointers could not be put back completely; this will be finished automatically.");
		}
		bool committed = _w.SaveState(new PointerStateFile
		{
			Phase = PointerScheme.PhaseIdle,
			LastResult = "restored (" + orig.Source + ")"
		});
		if (committed)
		{
			_w.DeleteLegacySnapshot();
		}
		else
		{
			Logger.Log("Pointers: final state write failed; the next start finishes the restore.");
		}
		Cleanup(Array.Empty<string>());
		if (!refreshed)
		{
			// The registry holds exactly the user's own pointers, so the restore is complete; Windows loads them at the
			// next sign-in.
			Logger.Log("Pointers: Windows did not reload the pointers after the restore; they show after the next sign-in.");
			return Ok(target, changed: true, "Your own Windows pointers are back; Windows shows them after the next sign-in.");
		}
		Logger.Log($"Pointers: your own pointers are back (source={orig.Source}).");
		return Ok(target, changed: true, "Your own Windows pointers are on.");
	}

	private PointerResult ApplyScheme(PointerStateFile st, PointerOriginal orig, Dictionary<string, RegValue> cur, PointerTarget target, bool interrupted, bool drift)
	{
		IReadOnlyDictionary<CursorRole, string> paths;
		bool generated = false;
		if (target.Kind == PointerKind.Win81)
		{
			paths = _w.ResolveWin81();
		}
		else
		{
			AlchemyPaletteFiles? files = _w.EnsurePalette(target.Palette);
			if (files == null || files.Paths.Count < PointerScheme.RoleNames.Length)
			{
				return Fail(target, "Could not create the Alchemy pointers; nothing was changed.");
			}
			paths = files.Paths;
			generated = files.Generated;
		}
		string schemeName = target.Kind == PointerKind.Win81 ? CursorScheme.SchemeName : PointerScheme.AlchemySchemeName;
		Dictionary<string, RegValue> desired = Desired(paths, schemeName);
		RegValue? schemeEntry = target.Kind == PointerKind.Alchemy ? AlchemySchemeEntry(paths) : SchemeOf(orig);
		bool registryMatches = Matches(cur, desired) && SameScheme(_w.ReadSchemeEntry(PointerScheme.AlchemySchemeName), schemeEntry);
		if (!interrupted && !drift && !generated && st.Original != null && target.Is(st.Applied) && registryMatches)
		{
			return Ok(target, changed: false, Describe(target) + " already on.");
		}
		PointerStateFile pending = new PointerStateFile
		{
			Phase = PointerScheme.PhaseApplying,
			Original = orig,
			Applied = st.Applied,
			TargetKind = target.Kind.ToString(),
			TargetPalette = target.Palette,
			LastResult = "applying " + Describe(target),
			DriftMergedUtc = drift ? _w.UtcNow : st.DriftMergedUtc
		};
		if (!_w.SaveState(pending))
		{
			return Fail(target, "Could not save the pointer restore point, so nothing was changed.");
		}
		if (!registryMatches || interrupted)
		{
			_w.WriteValues(PointerScheme.Keys.Select(k => (RegName(k), (RegValue?)desired[k])).ToList());
			_w.WriteSchemeEntry(PointerScheme.AlchemySchemeName, schemeEntry);
		}
		bool refreshed = _w.RefreshSystemCursors();
		Dictionary<string, RegValue> back = _w.ReadRegistry();
		if (!Matches(back, desired))
		{
			Logger.Log("Pointers: the registry did not read back as written; the change will be finished on the next start.");
			return Fail(target, "The pointers could not be changed completely; this will be finished automatically.");
		}
		if (!refreshed)
		{
			// Not saved as applied: the change stays pending, so the next check or start tries it again.
			Logger.Log("Pointers: Windows did not load the new pointers; the change will be tried again.");
			return Fail(target, "Windows did not load the new pointers; this will be tried again automatically.");
		}
		bool committed = _w.SaveState(new PointerStateFile
		{
			Phase = PointerScheme.PhaseIdle,
			Original = orig,
			Applied = new PointerApplied
			{
				Kind = target.Kind.ToString(),
				Palette = target.Palette,
				Values = desired.ToDictionary(kv => kv.Key, kv => (RegValue?)kv.Value.Clone(), StringComparer.Ordinal),
				AppliedUtc = _w.UtcNow
			},
			LastResult = "applied " + Describe(target),
			DriftMergedUtc = drift ? _w.UtcNow : st.DriftMergedUtc
		});
		if (committed)
		{
			_w.DeleteLegacySnapshot();
		}
		else
		{
			Logger.Log("Pointers: final state write failed; the next start finishes the change.");
		}
		List<string> keep = new List<string>();
		if (target.Kind == PointerKind.Alchemy)
		{
			keep.Add(target.Palette);
			string other = target.Mode == AlchemyPalettes.Auto ? AlchemyPalettes.OtherTone(target.Palette) : "";
			if (other.Length > 0)
			{
				keep.Add(other);
			}
		}
		Cleanup(keep);
		Logger.Log($"Pointers: {Describe(target)} on (generated={generated}, drift={drift}, recovered={interrupted}, original={orig.Source}).");
		return Ok(target, changed: true, Describe(target) + " on.");
	}

	// ===================================================================== restore point

	private PointerOriginal Capture(Dictionary<string, RegValue> cur)
	{
		PointerOriginal o = new PointerOriginal { CapturedUtc = _w.UtcNow, Source = "captured" };
		foreach (string key in PointerScheme.Keys)
		{
			o.Values[key] = Find(cur, key)?.Clone();
		}
		o.Schemes[PointerScheme.AlchemySchemeName] = OwnSchemeEntry();
		return o;
	}

	// Older builds kept the roles and the scheme name as plain strings and wrote them back as REG_EXPAND_SZ / REG_SZ.
	private PointerOriginal ImportLegacy(Dictionary<string, string> legacy, Dictionary<string, RegValue> cur)
	{
		PointerOriginal o = new PointerOriginal { CapturedUtc = _w.UtcNow, Source = "legacy-snapshot" };
		Dictionary<string, string> snap = new Dictionary<string, string>(legacy, StringComparer.OrdinalIgnoreCase);
		foreach (string role in PointerScheme.RoleNames)
		{
			o.Values[role] = new RegValue(role, RegistryValueKind.ExpandString, snap.TryGetValue(role, out string? v) ? (v ?? "") : "");
		}
		o.Values[PointerScheme.DefaultKey] = new RegValue("", RegistryValueKind.String, snap.TryGetValue(PointerScheme.DefaultKey, out string? d) ? (d ?? "") : "");
		o.Values[PointerScheme.SchemeSourceKey] = Find(cur, PointerScheme.SchemeSourceKey)?.Clone();
		o.Schemes[PointerScheme.AlchemySchemeName] = OwnSchemeEntry();
		return o;
	}

	private PointerOriginal FromSystemDefault()
	{
		Dictionary<string, RegValue> sd = _w.SystemDefault();
		PointerOriginal o = new PointerOriginal { CapturedUtc = _w.UtcNow, Source = "system-default-fallback" };
		foreach (string key in PointerScheme.Keys)
		{
			o.Values[key] = Find(sd, key)?.Clone();
		}
		o.Schemes[PointerScheme.AlchemySchemeName] = null;
		return o;
	}

	// A user scheme of the same name is kept; the launcher's own entry is not part of the user's pointers.
	private RegValue? OwnSchemeEntry()
	{
		RegValue? e = _w.ReadSchemeEntry(PointerScheme.AlchemySchemeName);
		if (e == null || e.Data.Split(',').Any(IsLauncherPath))
		{
			return null;
		}
		return e.Clone();
	}

	// A value that differs from what the launcher wrote was changed outside it (for example in Mouse Properties):
	// that choice becomes what Windows default brings back. Launcher pointers are never taken as the user's own.
	private bool MergeDrift(PointerOriginal orig, PointerApplied applied, Dictionary<string, RegValue> cur)
	{
		if (LooksLikeWin81Set(cur))
		{
			// The launcher's own Windows 8.1 set (put back by an older build, for example) is never the user's choice.
			return false;
		}
		bool changed = false;
		foreach (string key in PointerScheme.Keys)
		{
			applied.Values.TryGetValue(key, out RegValue? a);
			RegValue? c = Find(cur, key);
			if (SameData(a, c))
			{
				continue;
			}
			if (c != null && IsLauncherPath(c.Expanded()))
			{
				continue;
			}
			if (key == PointerScheme.DefaultKey && c != null && (string.Equals(c.Data, PointerScheme.AlchemySchemeName, StringComparison.OrdinalIgnoreCase) || string.Equals(c.Data, CursorScheme.SchemeName, StringComparison.OrdinalIgnoreCase)))
			{
				continue;
			}
			orig.Values[key] = c?.Clone();
			changed = true;
		}
		if (changed)
		{
			Logger.Log("Pointers: pointers were changed outside the launcher; that choice is what Windows default brings back.");
		}
		return changed;
	}

	private bool LooksLauncherOwned(Dictionary<string, RegValue> cur)
	{
		foreach (string role in PointerScheme.RoleNames)
		{
			RegValue? v = Find(cur, role);
			if (v != null && IsLauncherPath(v.Expanded()))
			{
				return true;
			}
		}
		RegValue? d = Find(cur, PointerScheme.DefaultKey);
		if (d != null && string.Equals(d.Data, PointerScheme.AlchemySchemeName, StringComparison.OrdinalIgnoreCase))
		{
			return true;
		}
		return LooksLikeWin81Set(cur);
	}

	// The launcher's Windows 8.1 set: its scheme name with every role on the file the launcher picks for it.
	private bool LooksLikeWin81Set(Dictionary<string, RegValue> cur)
	{
		RegValue? d = Find(cur, PointerScheme.DefaultKey);
		if (d == null || !string.Equals(d.Data, CursorScheme.SchemeName, StringComparison.OrdinalIgnoreCase))
		{
			return false;
		}
		IReadOnlyDictionary<CursorRole, string> win81 = _w.ResolveWin81();
		foreach (CursorRole role in AlchemyCursorArt.Roles)
		{
			string want = win81.TryGetValue(role, out string? p) ? (p ?? "") : "";
			string got = Find(cur, role.ToString())?.Expanded() ?? "";
			if (!string.Equals(Environment.ExpandEnvironmentVariables(want).Trim(), got.Trim(), StringComparison.OrdinalIgnoreCase))
			{
				return false;
			}
		}
		return true;
	}

	private bool IsLauncherPath(string? path)
	{
		if (string.IsNullOrWhiteSpace(path))
		{
			return false;
		}
		string full = Environment.ExpandEnvironmentVariables(path.Trim());
		foreach (string folder in new string[2] { _w.AlchemyRoot, _w.Win81AssetsRoot })
		{
			if (!string.IsNullOrEmpty(folder) && full.StartsWith(folder.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase))
			{
				return true;
			}
		}
		return false;
	}

	// ===================================================================== registry values

	private static Dictionary<string, RegValue> Desired(IReadOnlyDictionary<CursorRole, string> paths, string schemeName)
	{
		Dictionary<string, RegValue> d = new Dictionary<string, RegValue>(StringComparer.Ordinal);
		foreach (CursorRole role in AlchemyCursorArt.Roles)
		{
			string name = role.ToString();
			d[name] = new RegValue(name, RegistryValueKind.ExpandString, paths.TryGetValue(role, out string? p) ? (p ?? "") : "");
		}
		d[PointerScheme.DefaultKey] = new RegValue("", RegistryValueKind.String, schemeName);
		d[PointerScheme.SchemeSourceKey] = new RegValue(PointerScheme.SchemeSourceKey, RegistryValueKind.DWord, "1");
		return d;
	}

	// Mouse Properties lists the set as a scheme. Skipped when a path contains a comma (the list separator).
	private static RegValue? AlchemySchemeEntry(IReadOnlyDictionary<CursorRole, string> paths)
	{
		List<string> list = AlchemyCursorArt.Roles.Select(r => paths.TryGetValue(r, out string? p) ? (p ?? "") : "").ToList();
		if (list.Any(p => p.Contains(',')))
		{
			return null;
		}
		return new RegValue(PointerScheme.AlchemySchemeName, RegistryValueKind.ExpandString, string.Join(",", list));
	}

	private static RegValue? SchemeOf(PointerOriginal orig)
	{
		return orig.Schemes.TryGetValue(PointerScheme.AlchemySchemeName, out RegValue? v) ? v?.Clone() : null;
	}

	private static List<(string Name, RegValue? Value)> OriginalWrites(PointerOriginal orig, Dictionary<string, RegValue> cur)
	{
		List<(string, RegValue?)> list = new List<(string, RegValue?)>();
		foreach (string key in PointerScheme.Keys)
		{
			orig.Values.TryGetValue(key, out RegValue? v);
			list.Add(v == null ? (RegName(key), null) : (v.Name, v.Clone()));
		}
		return list;
	}

	private static string? VerifyOriginal(PointerOriginal orig, Dictionary<string, RegValue> back)
	{
		foreach (string key in PointerScheme.Keys)
		{
			orig.Values.TryGetValue(key, out RegValue? want);
			RegValue? got = Find(back, key);
			if (want == null)
			{
				if (got != null)
				{
					return key + " still present";
				}
				continue;
			}
			if (got == null || !string.Equals(got.Kind, want.Kind, StringComparison.Ordinal) || !string.Equals(got.Data, want.Data, StringComparison.Ordinal))
			{
				return key + " differs";
			}
		}
		return null;
	}

	private static bool Matches(Dictionary<string, RegValue> cur, Dictionary<string, RegValue> desired)
	{
		foreach (KeyValuePair<string, RegValue> kv in desired)
		{
			RegValue? got = Find(cur, kv.Key);
			if (got == null || !string.Equals(got.Kind, kv.Value.Kind, StringComparison.Ordinal) || !string.Equals(got.Data, kv.Value.Data, StringComparison.OrdinalIgnoreCase))
			{
				return false;
			}
		}
		return true;
	}

	private static bool SameScheme(RegValue? a, RegValue? b)
	{
		if (a == null || b == null)
		{
			return a == null && b == null;
		}
		return string.Equals(a.Kind, b.Kind, StringComparison.Ordinal) && string.Equals(a.Data, b.Data, StringComparison.OrdinalIgnoreCase);
	}

	private static bool SameData(RegValue? a, RegValue? b)
	{
		if (a == null || b == null)
		{
			return a == null && b == null;
		}
		return string.Equals(a.Expanded(), b.Expanded(), StringComparison.OrdinalIgnoreCase);
	}

	internal static RegValue? Find(Dictionary<string, RegValue> values, string key)
	{
		return values.TryGetValue(RegName(key), out RegValue? v) ? v : null;
	}

	internal static string RegName(string key)
	{
		return key == PointerScheme.DefaultKey ? "" : key;
	}

	private void Cleanup(IReadOnlyCollection<string> keep)
	{
		try
		{
			_w.CleanupPalettes(keep);
		}
		catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
		{
			Logger.Log("Pointers: cleanup skipped: " + ex.Message);
		}
	}

	internal static string Describe(PointerTarget t)
	{
		return t.Kind switch
		{
			PointerKind.Win81 => "Windows 8.1 pointers",
			PointerKind.Alchemy => "Alchemy " + t.Palette + " pointers",
			_ => "Your own Windows pointers"
		};
	}

	private static PointerResult Ok(PointerTarget t, bool changed, string message)
	{
		return new PointerResult { Success = true, Changed = changed, Kind = t.Kind, Palette = t.Palette, Message = message };
	}

	private static PointerResult Fail(PointerTarget t, string message)
	{
		return new PointerResult { Success = false, Kind = t.Kind, Palette = t.Palette, Message = message };
	}
}

// One engine for the Windows 8.1 and the Alchemy pointers, and the only code that writes HKCU\Control Panel\Cursors.
// The user's own pointers are kept once, before the first change, and "Windows default" writes exactly those back.
internal static class PointerScheme
{
	public const string AlchemySchemeName = "Alchemy pointers";

	public const string PhaseIdle = "idle";

	public const string PhaseApplying = "applying";

	public const string PhaseRestoring = "restoring";

	public const string DefaultKey = "(Default)";

	public const string SchemeSourceKey = "Scheme Source";

	private const string StateName = "pointer";

	private const string MutexName = "Local\\Win81Layer.PointerScheme.v1";

	public static readonly string StatePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Win81Layer", "pointer-state.json");

	// Written by older builds while their Windows 8.1 pointers were on; imported once, deleted after the next commit.
	public static readonly string LegacySnapshotPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Win81Layer", "cursor-snapshot.json");

	public static readonly string[] RoleNames = AlchemyCursorArt.Roles.Select(r => r.ToString()).ToArray();

	public static readonly string[] Keys = RoleNames.Concat(new string[2] { DefaultKey, SchemeSourceKey }).ToArray();

	// The choices offered in PC Settings and the tray, in order.
	public static readonly string[] ChoiceLabels = new string[5] { "Windows default", "Windows 8.1", "Alchemy dark", "Alchemy light", "Alchemy auto (follows light/dark mode)" };

	internal static readonly JsonSerializerOptions Json = new JsonSerializerOptions
	{
		PropertyNameCaseInsensitive = true,
		WriteIndented = true
	};

	private static readonly object Gate = new object();

	private static volatile bool _busy;

	private static volatile bool _busyAlchemy;

	private static PointerStateFile? _cache;

	private static volatile bool _cacheUnreadable;

	private static string _lastFailure = "";

	private static readonly object TimerGate = new object();

	private static Timer? _themeTimer;

	private static Task<PointerResult>? _pending;

	// Changes started and not finished yet (waiting for the lock or running).
	private static int _inflight;

	// Delays of the startup check's retries when the restore point or the settings could not be read.
	private static readonly int[] StartupRetryMs = new int[2] { 5000, 30000 };

	// Self-test only: the status PC Settings shows, so a render can show the text a user sees after a change.
	internal static PointerStatus? StatusOverrideForTest;

	// Raised on a worker thread after every pointer change attempt (applied, already on or failed).
	public static event Action? StateChanged;

	public static bool IsBusy => _busy;

	// The change started last (completed when none runs), so a page can show the result once it lands.
	public static Task Pending => (Task?)_pending ?? Task.CompletedTask;

	// ===================================================================== settings

	public static PointerTarget Effective(AppSettings s, bool isDark)
	{
		string mode = AlchemyPalettes.NormalizeMode(s.AlchemyCursors);
		if (mode.Length > 0)
		{
			return new PointerTarget(PointerKind.Alchemy, AlchemyPalettes.Resolve(mode, isDark, s.SeasonalActive, s.AlchemyCursorsSeasonal), mode);
		}
		if (s.UseWin81Cursors)
		{
			return new PointerTarget(PointerKind.Win81, "", "");
		}
		return PointerTarget.Windows;
	}

	public static int ChoiceIndex(AppSettings s)
	{
		if (s.UseWin81Cursors && AlchemyPalettes.NormalizeMode(s.AlchemyCursors).Length == 0)
		{
			return 1;
		}
		return AlchemyPalettes.NormalizeMode(s.AlchemyCursors) switch
		{
			AlchemyPalettes.Dark => 2,
			AlchemyPalettes.Light => 3,
			AlchemyPalettes.Auto => 4,
			_ => 0
		};
	}

	// Writes a choice into the settings: one pointer scheme at a time.
	public static void SetChoice(AppSettings x, int index)
	{
		x.UseWin81Cursors = index == 1;
		x.AlchemyCursors = index switch
		{
			2 => AlchemyPalettes.Dark,
			3 => AlchemyPalettes.Light,
			4 => AlchemyPalettes.Auto,
			_ => ""
		};
	}

	// ===================================================================== apply

	// Applies what the saved settings ask for. The settings and light/dark are read only once the change holds the
	// lock, so when changes queue up the one that runs last always applies the current choice.
	public static PointerResult ApplyBlocking(string reason)
	{
		return Run(null, reason);
	}

	// Applies what the given settings ask for (a shell profile passes the settings it is committing).
	public static PointerResult ApplyBlocking(AppSettings settings, string reason)
	{
		return Run(settings, reason);
	}

	private static PointerResult Run(AppSettings? given, string reason)
	{
		if (SettingsStore.ReadOnlyDiagnostics)
		{
			return ReadOnlyRefusal();
		}
		Interlocked.Increment(ref _inflight);
		try
		{
			if (!Monitor.TryEnter(Gate, TimeSpan.FromSeconds(60)))
			{
				return Failed("Another pointer change is still running.");
			}
			try
			{
				_busy = true;
				return RunLocked(given, reason);
			}
			finally
			{
				_busy = false;
				_busyAlchemy = false;
				Monitor.Exit(Gate);
			}
		}
		finally
		{
			Interlocked.Decrement(ref _inflight);
			RaiseStateChanged();
		}
	}

	public static Task<PointerResult> ApplyAsync(string reason)
	{
		if (SettingsStore.ReadOnlyDiagnostics)
		{
			return Task.FromResult(ReadOnlyRefusal());
		}
		Task<PointerResult> t = Task.Run(delegate
		{
			try
			{
				return ApplyBlocking(reason);
			}
			catch (Exception ex)
			{
				Logger.Log("Pointers: change failed: " + ex);
				return Failed("Could not change the pointers: " + ex.Message);
			}
		});
		_pending = t;
		return t;
	}

	private static PointerResult RunLocked(AppSettings? given, string reason)
	{
		Mutex? mutex = null;
		bool owned = false;
		try
		{
			mutex = new Mutex(false, MutexName);
			try
			{
				owned = mutex.WaitOne(TimeSpan.FromSeconds(10));
			}
			catch (AbandonedMutexException)
			{
				owned = true;
				Logger.Log("Pointers: transaction lock was abandoned by another process; ownership recovered.");
			}
			if (!owned)
			{
				return Failed("Another pointer change is still running.");
			}
			AppSettings? settings = given;
			if (settings == null && !SettingsStore.TryLoadDurable(out settings))
			{
				return Failed("Could not read the launcher settings, so the pointers were not changed.");
			}
			PointerTarget target = Effective(settings!, ShellTheme.IsDark);
			_busyAlchemy = target.Kind == PointerKind.Alchemy;
			Logger.Log($"Pointers: apply {PointerEngine.Describe(target)} ({reason})");
			PointerResult r = new PointerEngine(new RealPointerWorld()).Apply(target);
			_lastFailure = r.Success ? "" : r.Message;
			RefreshCache();
			return r;
		}
		catch (Exception ex)
		{
			Logger.Log("Pointers: change failed: " + ex);
			RefreshCache();
			return Failed("Could not change the pointers: " + ex.Message);
		}
		finally
		{
			if (owned)
			{
				try
				{
					mutex!.ReleaseMutex();
				}
				catch
				{
				}
			}
			mutex?.Dispose();
		}
	}

	private static PointerResult ReadOnlyRefusal()
	{
		return new PointerResult { Success = false, Message = "Read-only diagnostics: no changes made." };
	}

	private static PointerResult Failed(string message)
	{
		_lastFailure = message;
		return new PointerResult { Success = false, Message = message };
	}

	// ===================================================================== reconcile

	// Startup (worker thread): finish an interrupted change, migrate the older Windows 8.1 snapshot, and put the
	// effective pointers on when the settings and the applied state disagree. Writes nothing when they agree.
	// When the restore point or the settings cannot be read yet (a scanner holding the file during sign-in, say), or the
	// change fails, the check runs again 5 s and then 30 s later; after that only the usual triggers remain.
	public static void InitializeAndReconcile()
	{
		StartupCheck(0);
	}

	private static void StartupCheck(int attempt)
	{
		RefreshCache();
		if (SettingsStore.ReadOnlyDiagnostics)
		{
			return;
		}
		bool retry = false;
		try
		{
			if (_cacheUnreadable)
			{
				Logger.Log("Pointers: the restore point cannot be read right now; pointer changes are paused.");
				retry = true;
			}
			else if (!SettingsStore.TryLoadDurable(out AppSettings s))
			{
				Logger.Log("Pointers: startup check skipped: the settings could not be read.");
				retry = true;
			}
			else
			{
				string why = NeedsApply(_cache, Effective(s, ShellTheme.IsDark), startup: true);
				if (why.Length > 0)
				{
					retry = !ApplyBlocking("startup: " + why).Success;
				}
			}
		}
		catch (Exception ex)
		{
			Logger.Log("Pointers: startup check failed: " + ex.Message);
			retry = true;
		}
		if (retry && attempt < StartupRetryMs.Length)
		{
			int delay = StartupRetryMs[attempt];
			Logger.Log($"Pointers: the startup check runs again in {delay / 1000} s.");
			Task.Delay(delay).ContinueWith(delegate
			{
				StartupCheck(attempt + 1);
			}, TaskScheduler.Default);
		}
	}

	// "" when the applied pointers already match the target. Startup also checks the files and the legacy snapshot.
	private static string NeedsApply(PointerStateFile? st, PointerTarget target, bool startup)
	{
		if (st != null && st.Phase != PhaseIdle)
		{
			return "interrupted change";
		}
		if (!target.Is(st?.Applied))
		{
			return "settings differ";
		}
		if (startup)
		{
			if (File.Exists(LegacySnapshotPath))
			{
				return "older pointer snapshot";
			}
			if (target.Kind == PointerKind.Win81)
			{
				// The Windows 8.1 files can appear (seeded after the first apply) or go away; follow where they are now.
				IReadOnlyDictionary<CursorRole, string> now = CursorScheme.ResolveWin81();
				foreach (CursorRole role in AlchemyCursorArt.Roles)
				{
					st!.Applied!.Values.TryGetValue(role.ToString(), out RegValue? applied);
					string want = now.TryGetValue(role, out string? p) ? (p ?? "") : "";
					if (!string.Equals(applied?.Data ?? "", want, StringComparison.OrdinalIgnoreCase))
					{
						return "pointer files moved";
					}
				}
			}
			if (target.Kind == PointerKind.Alchemy)
			{
				string folder = AlchemyCursorSet.FolderFor(target.Palette);
				st!.Applied!.Values.TryGetValue("Arrow", out RegValue? arrow);
				if (arrow == null || !arrow.Data.StartsWith(folder + "\\", StringComparison.OrdinalIgnoreCase))
				{
					return "pointer files moved";
				}
				if (!AlchemyCursorSet.IsReady(target.Palette))
				{
					return "pointer files missing";
				}
			}
		}
		return "";
	}

	// Cheap: compares the cached state with the settings; only a real difference starts a change on a worker. While
	// another change is waiting or running the cache is about to change, so the check is queued behind it instead (it
	// reads the settings again once it runs and writes nothing when they are already on).
	public static Task<PointerResult?> ReconcileAsync(string reason)
	{
		if (SettingsStore.ReadOnlyDiagnostics)
		{
			return Task.FromResult<PointerResult?>(null);
		}
		try
		{
			Task<PointerResult>? pending = _pending;
			bool changing = Volatile.Read(ref _inflight) > 0 || (pending != null && !pending.IsCompleted);
			PointerTarget target = Effective(SettingsStore.FastSnapshot, ShellTheme.IsDark);
			if (!changing && !_cacheUnreadable && NeedsApply(_cache, target, startup: false).Length == 0)
			{
				return Task.FromResult<PointerResult?>(null);
			}
		}
		catch (Exception ex)
		{
			Logger.Log("Pointers: reconcile check failed: " + ex.Message);
		}
		return ApplyAsync(reason).ContinueWith((Task<PointerResult> t) => (PointerResult?)t.Result, TaskScheduler.Default);
	}

	// Light/dark flips only matter for "auto"; a burst of preference changes becomes one check 300 ms later.
	public static void OnThemeChanged(object? sender, EventArgs e)
	{
		try
		{
			if (SettingsStore.ReadOnlyDiagnostics || AlchemyPalettes.NormalizeMode(SettingsStore.FastSnapshot.AlchemyCursors) != AlchemyPalettes.Auto)
			{
				return;
			}
			lock (TimerGate)
			{
				if (_themeTimer == null)
				{
					_themeTimer = new Timer(delegate
					{
						lock (TimerGate)
						{
							_themeTimer?.Dispose();
							_themeTimer = null;
						}
						ReconcileAsync("theme");
					}, null, 300, Timeout.Infinite);
				}
				else
				{
					_themeTimer.Change(300, Timeout.Infinite);
				}
			}
		}
		catch (Exception ex)
		{
			Logger.Log("Pointers: theme follow failed: " + ex.Message);
		}
	}

	// ===================================================================== status

	private static void RaiseStateChanged()
	{
		try
		{
			StateChanged?.Invoke();
		}
		catch (Exception ex)
		{
			Logger.Log("Pointers: change notification failed: " + ex.Message);
		}
	}

	public static PointerStatus Describe()
	{
		PointerStatus? fixedStatus = StatusOverrideForTest;
		if (fixedStatus != null)
		{
			return fixedStatus;
		}
		if (_cache == null && !_cacheUnreadable)
		{
			RefreshCache();
		}
		PointerStateFile st = _cache ?? new PointerStateFile();
		PointerKind kind = PointerKind.Windows;
		if (st.Applied != null && Enum.TryParse(st.Applied.Kind, out PointerKind k))
		{
			kind = k;
		}
		AppSettings s = SettingsStore.FastSnapshot;
		return new PointerStatus
		{
			Busy = _busy,
			BusyAlchemy = _busyAlchemy,
			Unreadable = _cacheUnreadable,
			Phase = st.Phase,
			AppliedKind = kind,
			AppliedPalette = st.Applied?.Palette ?? "",
			Mode = AlchemyPalettes.NormalizeMode(s.AlchemyCursors),
			Drift = st.DriftMergedUtc != default && st.Original != null,
			Failure = _lastFailure
		};
	}

	public static string StatusText(PointerStatus st)
	{
		if (st.Busy)
		{
			return st.BusyAlchemy ? "Preparing the Alchemy pointers…" : "Changing pointers…";
		}
		if (st.Unreadable)
		{
			return "The pointer restore point cannot be read right now, so pointer changes are paused.";
		}
		if (st.Phase != PhaseIdle)
		{
			return "A pointer change was interrupted; it will be finished automatically.";
		}
		if (st.Failure.Length > 0)
		{
			return st.Failure;
		}
		string text;
		switch (st.AppliedKind)
		{
		case PointerKind.Win81:
			text = "Windows 8.1 pointers are on. Windows default puts back the pointers you had before.";
			break;
		case PointerKind.Alchemy:
		{
			AlchemyPalette? p = AlchemyPalettes.Find(st.AppliedPalette);
			string tone = p?.Tone ?? AlchemyPalettes.Dark;
			bool restSet = p != null && p.Season == AlchemyPalettes.RestSeason;
			if ((st.Mode == AlchemyPalettes.Light || st.Mode == AlchemyPalettes.Auto) && tone == AlchemyPalettes.Dark && (restSet || DarkRest.IsOn))
			{
				// Dark rest keeps the pointers dark whatever the light/dark choice says.
				text = "Alchemy dark pointers are on while Dark rest is on" + (restSet ? ", in muted colours" : "") + ". "
					+ (st.Mode == AlchemyPalettes.Auto ? "Afterwards they follow light/dark mode again." : "Normal brings back your light pointers.");
				break;
			}
			string season = p == null || p.Season.Length == 0 ? "" : (restSet ? " in Dark rest colours" : " in " + char.ToUpperInvariant(p.Season[0]) + p.Season.Substring(1) + " colours");
			text = st.Mode == AlchemyPalettes.Auto
				? $"Alchemy auto is on: {tone} pointers{season} now, they follow light/dark mode."
				: $"Alchemy {tone} pointers are on{(season.Length > 0 ? "," + season : "")}.";
			break;
		}
		default:
			return "Your own Windows pointers are on.";
		}
		if (st.Drift)
		{
			text += " Your pointers were changed outside the launcher; that choice is what Windows default brings back.";
		}
		return text;
	}

	// What the state says is on, without taking the lock. An older build's snapshot means its Windows 8.1 pointers.
	public static PointerKind PeekAppliedKind()
	{
		PointerStateFile? st = LoadStateFile(out bool unreadable);
		if (st?.Applied != null && Enum.TryParse(st.Applied.Kind, out PointerKind k))
		{
			return k;
		}
		if (st?.Original == null && !unreadable && File.Exists(LegacySnapshotPath))
		{
			return PointerKind.Win81;
		}
		return PointerKind.Windows;
	}

	// True when the last committed change is what these settings ask for.
	public static bool MatchesEffective(AppSettings s)
	{
		RefreshCache();
		PointerStateFile? st = _cache;
		if (_cacheUnreadable || (st != null && st.Phase != PhaseIdle))
		{
			return false;
		}
		PointerTarget target = Effective(s, ShellTheme.IsDark);
		if (!target.Is(st?.Applied))
		{
			return false;
		}
		return target.Kind != PointerKind.Windows || !File.Exists(LegacySnapshotPath);
	}

	private static void RefreshCache()
	{
		try
		{
			PointerStateFile? st = LoadStateFile(out bool unreadable);
			_cacheUnreadable = unreadable;
			if (st != null || !unreadable)
			{
				_cache = st ?? new PointerStateFile();
			}
		}
		catch
		{
		}
	}

	// ===================================================================== state file

	// null when there is no restore point. unreadable is set when one exists but cannot be opened right now; a file
	// that is corrupt everywhere counts as missing, so it can never block the pointer controls.
	internal static PointerStateFile? LoadStateFile(out bool unreadable)
	{
		unreadable = false;
		DurableStateReadResult read = DurableStateStore.Read(StateName, StatePath, null, ValidateStateJson, 8);
		if (read.Json == null)
		{
			unreadable = StateFileLocked();
			return null;
		}
		try
		{
			return JsonSerializer.Deserialize<PointerStateFile>(read.Json, Json);
		}
		catch (Exception ex)
		{
			Logger.Log("Pointers: state parse failed after validation: " + ex.Message);
			unreadable = true;
			return null;
		}
	}

	private static bool StateFileLocked()
	{
		foreach (string path in new string[3] { StatePath, DurableStateStore.PendingPath(StateName), DurableStateStore.LastGoodPath(StateName) })
		{
			try
			{
				if (!File.Exists(path))
				{
					continue;
				}
				using FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
			}
			catch (Exception ex)
			{
				Logger.Log("Pointers: state file not readable: " + ex.Message);
				return true;
			}
		}
		if (File.Exists(StatePath))
		{
			Logger.Log("Pointers: the restore point is corrupt and has no valid copy; it is treated as missing.");
		}
		return false;
	}

	internal static bool SaveStateFile(PointerStateFile state)
	{
		string json = JsonSerializer.Serialize(state, Json);
		return DurableStateStore.Commit(StateName, StatePath, json, ValidateStateJson).IsDurable;
	}

	internal static bool ValidateStateJson(string json)
	{
		try
		{
			PointerStateFile? st = JsonSerializer.Deserialize<PointerStateFile>(json, Json);
			return st != null && st.SchemaVersion == 1 && (st.Phase == PhaseIdle || st.Phase == PhaseApplying || st.Phase == PhaseRestoring);
		}
		catch
		{
			return false;
		}
	}
}

// The real world: HKCU\Control Panel\Cursors, the durable state store and the launcher's pointer folder. Every write
// refuses to run in read-only diagnostics.
internal sealed class RealPointerWorld : IPointerWorld
{
	private const string CursorsKey = "Control Panel\\Cursors";

	private const string SchemesKey = "Control Panel\\Cursors\\Schemes";

	private const string SystemDefaultKey = "SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Control Panel\\Cursors\\Default";

	public string AlchemyRoot => AlchemyCursorSet.Root;

	public string Win81AssetsRoot => CursorScheme.AssetsDir;

	public DateTime UtcNow => DateTime.UtcNow;

	private static void Guard()
	{
		if (SettingsStore.ReadOnlyDiagnostics)
		{
			throw new InvalidOperationException("Pointer changes are disabled in read-only diagnostics mode.");
		}
	}

	public Dictionary<string, RegValue> ReadRegistry()
	{
		using RegistryKey? key = Registry.CurrentUser.OpenSubKey(CursorsKey);
		return ReadAll(key);
	}

	private static Dictionary<string, RegValue> ReadAll(RegistryKey? key)
	{
		Dictionary<string, RegValue> d = new Dictionary<string, RegValue>(StringComparer.OrdinalIgnoreCase);
		if (key == null)
		{
			return d;
		}
		foreach (string name in key.GetValueNames())
		{
			RegValue? v = ReadValue(key, name);
			if (v != null)
			{
				d[name] = v;
			}
		}
		return d;
	}

	internal static RegValue? ReadValue(RegistryKey key, string name)
	{
		RegistryValueKind kind = key.GetValueKind(name);
		object? raw = key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
		string data = raw switch
		{
			null => "",
			string str => str,
			int i => ((uint)i).ToString(System.Globalization.CultureInfo.InvariantCulture),
			long l => ((ulong)l).ToString(System.Globalization.CultureInfo.InvariantCulture),
			byte[] b => Convert.ToBase64String(b),
			string[] m => string.Join("\0", m),
			_ => raw.ToString() ?? ""
		};
		return new RegValue { Name = name, Kind = kind.ToString(), Data = data };
	}

	public RegValue? ReadSchemeEntry(string name)
	{
		using RegistryKey? key = Registry.CurrentUser.OpenSubKey(SchemesKey);
		if (key == null)
		{
			return null;
		}
		string? actual = key.GetValueNames().FirstOrDefault(n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase));
		return actual == null ? null : ReadValue(key, actual);
	}

	public void WriteValues(IReadOnlyList<(string Name, RegValue? Value)> values)
	{
		Guard();
		using RegistryKey key = Registry.CurrentUser.CreateSubKey(CursorsKey, writable: true);
		foreach ((string name, RegValue? value) in values)
		{
			WriteOne(key, name, value);
		}
	}

	public void WriteSchemeEntry(string name, RegValue? value)
	{
		Guard();
		using RegistryKey key = Registry.CurrentUser.CreateSubKey(SchemesKey, writable: true);
		WriteOne(key, name, value == null ? null : new RegValue { Name = name, Kind = value.Kind, Data = value.Data });
	}

	internal static void WriteOne(RegistryKey key, string name, RegValue? value)
	{
		string? existing = key.GetValueNames().FirstOrDefault(n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase));
		if (value == null)
		{
			if (existing != null)
			{
				key.DeleteValue(existing, throwOnMissingValue: false);
			}
			return;
		}
		if (existing != null && !string.Equals(existing, value.Name, StringComparison.Ordinal))
		{
			key.DeleteValue(existing, throwOnMissingValue: false);
		}
		RegistryValueKind kind = Enum.TryParse(value.Kind, out RegistryValueKind k) ? k : RegistryValueKind.String;
		object data = kind switch
		{
			RegistryValueKind.DWord => unchecked((int)uint.Parse(value.Data, System.Globalization.CultureInfo.InvariantCulture)),
			RegistryValueKind.QWord => unchecked((long)ulong.Parse(value.Data, System.Globalization.CultureInfo.InvariantCulture)),
			RegistryValueKind.Binary => Convert.FromBase64String(value.Data),
			RegistryValueKind.MultiString => value.Data.Split('\0'),
			_ => value.Data
		};
		if (kind == RegistryValueKind.Unknown || kind == RegistryValueKind.None)
		{
			kind = RegistryValueKind.String;
		}
		key.SetValue(value.Name, data, kind);
	}

	public bool RefreshSystemCursors()
	{
		Guard();
		if (SystemParametersInfo(0x57u, 0u, nint.Zero, 3u))
		{
			return true;
		}
		Logger.Log("Pointers: SPI_SETCURSORS failed (error " + Marshal.GetLastWin32Error() + ").");
		return false;
	}

	public PointerStateFile LoadState()
	{
		PointerStateFile? st = PointerScheme.LoadStateFile(out bool unreadable);
		if (st == null && unreadable)
		{
			throw new PointerReadException("The pointer restore point cannot be read right now, so nothing was changed.");
		}
		return st ?? new PointerStateFile();
	}

	public bool SaveState(PointerStateFile state)
	{
		Guard();
		state.UpdatedUtc = DateTime.UtcNow;
		return PointerScheme.SaveStateFile(state);
	}

	public Dictionary<string, string>? LegacySnapshot()
	{
		string path = PointerScheme.LegacySnapshotPath;
		if (!File.Exists(path))
		{
			return null;
		}
		string text;
		try
		{
			using FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
			using StreamReader reader = new StreamReader(fs);
			text = reader.ReadToEnd();
		}
		catch (Exception ex)
		{
			throw new PointerReadException("The saved pointers of an older version cannot be read right now (" + ex.Message + "), so nothing was changed.");
		}
		try
		{
			Dictionary<string, string>? d = JsonSerializer.Deserialize<Dictionary<string, string>>(text);
			if (d == null || !d.Keys.Any(k => string.Equals(k, "Arrow", StringComparison.OrdinalIgnoreCase)))
			{
				Logger.Log("Pointers: the older pointer snapshot is incomplete; it is ignored.");
				return null;
			}
			return d;
		}
		catch (Exception ex)
		{
			Logger.Log("Pointers: the older pointer snapshot is corrupt; it is ignored: " + ex.Message);
			return null;
		}
	}

	public void DeleteLegacySnapshot()
	{
		Guard();
		try
		{
			if (File.Exists(PointerScheme.LegacySnapshotPath))
			{
				File.Delete(PointerScheme.LegacySnapshotPath);
				Logger.Log("Pointers: the older pointer snapshot was retired.");
			}
		}
		catch (Exception ex)
		{
			Logger.Log("Pointers: the older pointer snapshot could not be removed yet: " + ex.Message);
		}
	}

	public Dictionary<string, RegValue> SystemDefault()
	{
		Dictionary<string, RegValue> d;
		using (RegistryKey? key = Registry.LocalMachine.OpenSubKey(SystemDefaultKey))
		{
			d = ReadAll(key);
		}
		if (d.Keys.Any(k => string.Equals(k, "Arrow", StringComparison.OrdinalIgnoreCase)))
		{
			return d;
		}
		// The system default key is missing: the standard Windows pointers.
		d = new Dictionary<string, RegValue>(StringComparer.OrdinalIgnoreCase);
		(string, string)[] std = new (string, string)[17]
		{
			("Arrow", "aero_arrow.cur"), ("Help", "aero_helpsel.cur"), ("AppStarting", "aero_working.ani"), ("Wait", "aero_busy.ani"),
			("Crosshair", ""), ("IBeam", ""), ("NWPen", "aero_pen.cur"), ("No", "aero_unavail.cur"), ("SizeNS", "aero_ns.cur"),
			("SizeWE", "aero_ew.cur"), ("SizeNWSE", "aero_nwse.cur"), ("SizeNESW", "aero_nesw.cur"), ("SizeAll", "aero_move.cur"),
			("UpArrow", "aero_up.cur"), ("Hand", "aero_link.cur"), ("Pin", "aero_pin.cur"), ("Person", "aero_person.cur")
		};
		foreach ((string role, string file) in std)
		{
			d[role] = new RegValue(role, RegistryValueKind.ExpandString, file.Length == 0 ? "" : "%SystemRoot%\\cursors\\" + file);
		}
		d[""] = new RegValue("", RegistryValueKind.String, "Windows Default");
		d[PointerScheme.SchemeSourceKey] = new RegValue(PointerScheme.SchemeSourceKey, RegistryValueKind.DWord, "2");
		return d;
	}

	public IReadOnlyDictionary<CursorRole, string> ResolveWin81()
	{
		return CursorScheme.ResolveWin81();
	}

	public AlchemyPaletteFiles? EnsurePalette(string paletteId)
	{
		Guard();
		return AlchemyCursorSet.Ensure(paletteId, null, AlchemyCursorSet.ValidateWithLoadImage);
	}

	public void CleanupPalettes(IReadOnlyCollection<string> keep)
	{
		Guard();
		AlchemyCursorSet.Cleanup(keep);
	}

	[DllImport("user32.dll", SetLastError = true)]
	private static extern bool SystemParametersInfo(uint action, uint param, nint ptr, uint winIni);
}
