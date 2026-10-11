#nullable enable

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace Win81Layer;

internal sealed record SeasonPreset(string Id, string Name, string Pattern, string Base, string Accent, string Ambient, string Art);

// The user's own look before the first seasonal change. Normal writes exactly these values back.
internal sealed class SeasonalOriginal
{
	public DateTime CapturedUtc { get; set; }

	public string StartBgMode { get; set; } = "";

	public string StartBgColor { get; set; } = "";

	public string StartBgCustomPath { get; set; } = "";

	public string StartPattern { get; set; } = "";

	public string StartAccentColor { get; set; } = "";

	// null = the launcher has not changed the desktop wallpaper.
	public DesktopWallpaperSnapshot? Wallpaper { get; set; }

	public SeasonalOriginal Clone()
	{
		SeasonalOriginal c = (SeasonalOriginal)MemberwiseClone();
		c.Wallpaper = Wallpaper?.Clone();
		return c;
	}
}

internal sealed class SeasonalStateFile
{
	public int SchemaVersion { get; set; } = 1;

	// idle | applying | restoring
	public string Phase { get; set; } = SeasonalTheme.PhaseIdle;

	// The season before the pending transaction ("" = normal) and the one it moves to.
	public string Source { get; set; } = "";

	public string Target { get; set; } = "";

	public SeasonalOriginal? Original { get; set; }

	public DateTime ActiveSinceUtc { get; set; }

	public string LastResult { get; set; } = "";

	public string LastError { get; set; } = "";

	public DateTime UpdatedUtc { get; set; }
}

internal sealed class WallpaperProbe
{
	public DesktopWallpaperSnapshot? Current { get; set; }

	public string? SkipReason { get; set; }
}

internal sealed class SeasonalResult
{
	public bool Success { get; set; }

	public string Season { get; set; } = "";

	public string Message { get; set; } = "";

	public string? WallpaperOutcome { get; set; }
}

internal sealed class SeasonalStatus
{
	public string ActiveId { get; set; } = "";

	public string ActiveName { get; set; } = "Normal";

	public DateTime? SinceUtc { get; set; }

	public bool OwnsWallpaper { get; set; }

	public string Phase { get; set; } = SeasonalTheme.PhaseIdle;

	public bool Busy { get; set; }

	public string BusyTarget { get; set; } = "";

	public string LastResult { get; set; } = "";

	public bool Unreadable { get; set; }
}

// Raised by the world when the settings or the restore point exist but cannot be read reliably right now (for example
// a file locked by another program). The engine then changes nothing, so a degraded read can never be saved as truth.
internal sealed class SeasonalReadException : Exception
{
	public SeasonalReadException(string message)
		: base(message)
	{
	}
}

// Everything the engine reads or changes goes through this, so the self-test can drive the engine with a fake.
internal interface ISeasonalWorld
{
	// Throws SeasonalReadException when the settings could not be read from the durable store.
	AppSettings LoadSettings();

	bool MutateSettings(Action<AppSettings> mutate);

	// Throws SeasonalReadException when a restore point exists but could not be read.
	SeasonalStateFile LoadState();

	bool SaveState(SeasonalStateFile state);

	WallpaperProbe ProbeWallpaper();

	// Copies wallpaper files Windows may overwrite (for example TranscodedWallpaper) to a safe place and points the
	// snapshot at the copies. Returns the snapshot to keep.
	DesktopWallpaperSnapshot PreserveVolatileFiles(DesktopWallpaperSnapshot snapshot);

	// File name PrepareWallpaperFile would produce now (the drawn scene, or the user's own picture for the season).
	string ExpectedWallpaperName(string art);

	string? PrepareWallpaperFile(string art);

	bool SetWallpaper(string path);

	string RestoreWallpaper(DesktopWallpaperSnapshot snapshot);

	bool IsSeasonalFile(string? path);

	bool FileExists(string path);

	// Deletes seasonal pictures and wallpaper backups that no display shows and keep (the restore point) does not use.
	void CleanupArt(SeasonalOriginal? keep);

	DateTime UtcNow { get; }
}

// The transaction logic. Every step that changes something is preceded by a durable state write, so a crash at any
// point is finished or rolled back by Recover on the next start.
internal sealed class SeasonalEngine
{
	private readonly ISeasonalWorld _w;

	public SeasonalEngine(ISeasonalWorld world)
	{
		_w = world;
	}

	public SeasonalResult Apply(string id)
	{
		string target = SeasonalTheme.NormalizeId(id);
		if (target.Length == 0)
		{
			return Restore();
		}
		try
		{
			return ApplyTo(SeasonalTheme.Find(target)!);
		}
		catch (SeasonalReadException ex)
		{
			Logger.Log("Seasonal: apply " + target + " refused: " + ex.Message);
			return Result(false, target, ex.Message);
		}
	}

	private SeasonalResult ApplyTo(SeasonPreset p)
	{
		string target = p.Id;
		SeasonalStateFile st = _w.LoadState();
		if (st.Phase != SeasonalTheme.PhaseIdle)
		{
			SeasonalResult recovered = RecoverCore();
			st = _w.LoadState();
			if (!recovered.Success || st.Phase != SeasonalTheme.PhaseIdle)
			{
				return Result(false, target, "An interrupted seasonal change could not be finished yet.");
			}
		}
		AppSettings s = _w.LoadSettings();
		string cur = SeasonalTheme.NormalizeId(s.SeasonalActive);
		// Clicking the season that is on only does nothing when its whole look is still in place; after a tweak (another
		// design or accent, or a desktop picture changed outside the launcher) it is applied again over the same restore point.
		if (target == cur && st.Original != null && IsFullyOn(s, st.Original, p))
		{
			bool swapped = EnsureWallpaperFile(st, p);
			return Result(true, target, swapped ? p.Name + " desktop picture updated." : p.Name + " is already on.");
		}
		SeasonalOriginal orig;
		if (st.Original != null)
		{
			orig = st.Original.Clone();
		}
		else if (cur.Length > 0)
		{
			// The marker is saved in the same write as the Start values, so a marker without a restore point means the
			// restore point was lost while a season was on: never snapshot that seasonal look as the user's own.
			orig = Stamp(FromSettings(new AppSettings()));
			Logger.Log("Seasonal: restore point missing while a seasonal look is active; Normal will use the default Start look.");
		}
		else
		{
			// Normal is on, so these are the user's own values, even when they happen to equal a seasonal design.
			orig = Stamp(FromSettings(s));
		}
		return ApplyCore(p, orig, cur, s.SeasonalWallpaper, st.ActiveSinceUtc, isRecovery: false);
	}

	private bool IsFullyOn(AppSettings s, SeasonalOriginal orig, SeasonPreset p)
	{
		if (!StartMatches(s, p))
		{
			return false;
		}
		if (!s.SeasonalWallpaper)
		{
			return orig.Wallpaper == null;
		}
		WallpaperProbe probe = _w.ProbeWallpaper();
		if (probe.SkipReason != null)
		{
			// A slideshow or Spotlight desktop is never changed, so there is nothing more to put back.
			return true;
		}
		List<MonitorWallpaper> shown = probe.Current?.Monitors ?? new List<MonitorWallpaper>();
		return orig.Wallpaper != null && shown.Count > 0 && shown.All(m => _w.IsSeasonalFile(m.Path));
	}

	public SeasonalResult Restore()
	{
		try
		{
			return RestoreToNormal();
		}
		catch (SeasonalReadException ex)
		{
			Logger.Log("Seasonal: restore refused: " + ex.Message);
			return Result(false, "", ex.Message);
		}
	}

	private SeasonalResult RestoreToNormal()
	{
		SeasonalStateFile st = _w.LoadState();
		AppSettings s = _w.LoadSettings();
		string cur = SeasonalTheme.NormalizeId(s.SeasonalActive);
		if (st.Original == null)
		{
			if (st.Phase != SeasonalTheme.PhaseIdle)
			{
				MarkIdle(null, "recovered: no restore point");
			}
			return RestoreWithoutRestorePoint(s, cur);
		}
		// Going back to normal never needs an interrupted change to be finished or rolled back first: the restore point
		// is put back directly, so a season that cannot be applied again (for example its wallpaper keeps failing)
		// never blocks Normal.
		SeasonalStateFile pending = new SeasonalStateFile
		{
			Phase = SeasonalTheme.PhaseRestoring,
			Source = st.Phase == SeasonalTheme.PhaseIdle ? cur : st.Source,
			Target = "",
			Original = st.Original,
			ActiveSinceUtc = st.ActiveSinceUtc,
			LastResult = st.LastResult
		};
		if (!_w.SaveState(pending))
		{
			// The state still holds the restore point; a crash from here is reconciled on the next start.
			Logger.Log("Seasonal: could not mark the restore as pending; continuing with the restore.");
		}
		return RestoreCore(st.Original, cur);
	}

	// The marker says a season is on but its restore point is gone. The user's own Start look cannot be recovered, so a
	// seasonal Start look falls back to the default one; any other look the user picked since is kept.
	private SeasonalResult RestoreWithoutRestorePoint(AppSettings s, string cur)
	{
		if (cur.Length == 0)
		{
			return Result(true, "", "Normal look restored.");
		}
		bool seasonalLook = string.Equals(s.StartBgMode, "pattern", StringComparison.Ordinal) && SeasonalArt.IsSeasonalPattern(s.StartPattern);
		SeasonalOriginal d = FromSettings(new AppSettings());
		if (!_w.MutateSettings(delegate(AppSettings x)
		{
			if (seasonalLook)
			{
				x.StartBgMode = d.StartBgMode;
				x.StartBgColor = d.StartBgColor;
				x.StartBgCustomPath = d.StartBgCustomPath;
				x.StartPattern = d.StartPattern;
				x.StartAccentColor = d.StartAccentColor;
			}
			x.SeasonalActive = "";
		}))
		{
			return Result(false, "", "Restoring did not finish; it will be retried when the launcher starts.");
		}
		bool seasonalWallpaper = _w.ProbeWallpaper().Current?.Monitors.Any(m => _w.IsSeasonalFile(m.Path)) == true;
		string message = "Normal look restored.";
		if (seasonalLook)
		{
			message += " Your previous Start look could not be recovered, so the default look was used.";
		}
		if (seasonalWallpaper)
		{
			message += " The desktop wallpaper could not be put back automatically; choose your own picture in Windows Settings.";
		}
		Logger.Log($"Seasonal: restore without a restore point (defaults={seasonalLook}, seasonalWallpaperShowing={seasonalWallpaper})");
		return new SeasonalResult { Success = true, Season = "", Message = message, WallpaperOutcome = seasonalWallpaper ? "unknown" : "none" };
	}

	// Finishes or rolls back an interrupted change, and reconciles the marker with the restore point.
	public SeasonalResult Recover()
	{
		try
		{
			return RecoverCore();
		}
		catch (SeasonalReadException ex)
		{
			Logger.Log("Seasonal: recovery postponed: " + ex.Message);
			return Result(false, "", ex.Message);
		}
	}

	private SeasonalResult RecoverCore()
	{
		SeasonalStateFile st = _w.LoadState();
		string phase = st.Phase;
		SeasonalResult r;
		if (phase == SeasonalTheme.PhaseApplying)
		{
			if (st.Original == null)
			{
				r = MarkIdle(null, "recovered: no restore point");
			}
			else if (st.Source.Length == 0)
			{
				r = RestoreCore(st.Original, st.Target);
			}
			else
			{
				SeasonPreset? source = SeasonalTheme.Find(st.Source);
				r = source == null
					? RestoreCore(st.Original, st.Source)
					: ApplyCore(source, st.Original.Clone(), st.Source, _w.LoadSettings().SeasonalWallpaper, st.ActiveSinceUtc, isRecovery: true);
			}
		}
		else if (phase == SeasonalTheme.PhaseRestoring)
		{
			r = st.Original == null ? MarkIdle(null, "recovered: no restore point") : RestoreCore(st.Original, st.Source);
		}
		else
		{
			r = Reconcile(st);
		}
		Logger.Log($"Seasonal: recovery phase={phase} source={st.Source} -> {(r.Success ? "ok" : "pending")}: {r.Message}");
		return r;
	}

	private SeasonalResult Reconcile(SeasonalStateFile st)
	{
		AppSettings s = _w.LoadSettings();
		string active = SeasonalTheme.NormalizeId(s.SeasonalActive);
		if (active.Length > 0 && st.Original == null)
		{
			// Nothing to restore. The marker is kept so Normal still offers a way out of the seasonal look.
			Logger.Log("Seasonal: a seasonal look is on without a restore point; Normal will use the default Start look.");
			return Result(true, active, "Nothing to recover.");
		}
		if (active.Length == 0 && st.Original != null)
		{
			if (StartEquals(s, st.Original) || MatchesAnyPreset(s))
			{
				// The final commit of a restore was lost: the Start values are already back (or still seasonal).
				return RestoreCore(st.Original, "");
			}
			// The settings were replaced outside a seasonal change (for example by a launcher profile import). Keep
			// them, put back only a desktop wallpaper the launcher still shows, and drop the restore point.
			string wallpaper = "none";
			if (st.Original.Wallpaper != null)
			{
				wallpaper = _w.RestoreWallpaper(st.Original.Wallpaper);
				if (wallpaper.StartsWith("failed", StringComparison.Ordinal))
				{
					return Result(false, "", "Restoring did not finish; it will be retried when the launcher starts.");
				}
			}
			MarkIdle(null, $"restore point dropped after an outside settings change (wallpaper={wallpaper})");
			Cleanup(null);
			Logger.Log($"Seasonal: settings changed outside the launcher while a season was on; they are kept (wallpaper={wallpaper}).");
			return new SeasonalResult { Success = true, Season = "", Message = "Normal look restored.", WallpaperOutcome = wallpaper };
		}
		if (active.Length > 0 && st.Original?.Wallpaper != null)
		{
			SeasonPreset? p = SeasonalTheme.Find(active);
			if (p != null && EnsureWallpaperFile(st, p))
			{
				return Result(true, active, "Seasonal wallpaper redrawn.");
			}
		}
		return Result(true, active, "Nothing to recover.");
	}

	// Re-draws the applied wallpaper when its file went missing while it is still on screen, and swaps it when the season's
	// picture changed (the user chose or removed their own picture) while the old one is still on screen.
	private bool EnsureWallpaperFile(SeasonalStateFile st, SeasonPreset p)
	{
		DesktopWallpaperSnapshot? wp = st.Original?.Wallpaper;
		if (wp == null || string.IsNullOrEmpty(wp.AppliedPath))
		{
			return false;
		}
		bool missing = !_w.FileExists(wp.AppliedPath);
		bool changed = !string.Equals(Path.GetFileName(wp.AppliedPath), _w.ExpectedWallpaperName(p.Art), StringComparison.OrdinalIgnoreCase);
		if (!missing && !changed)
		{
			return false;
		}
		WallpaperProbe probe = _w.ProbeWallpaper();
		bool showing = probe.Current?.Monitors.Any(m => string.Equals(m.Path, wp.AppliedPath, StringComparison.OrdinalIgnoreCase)) == true;
		if (!showing)
		{
			return false;
		}
		string? path = _w.PrepareWallpaperFile(p.Art);
		if (path == null)
		{
			return false;
		}
		if (!string.Equals(path, wp.AppliedPath, StringComparison.OrdinalIgnoreCase))
		{
			if (!_w.SetWallpaper(path))
			{
				return false;
			}
			wp.AppliedPath = path;
			_w.SaveState(st);
		}
		Logger.Log(missing ? "Seasonal: the seasonal wallpaper file was missing and has been drawn again." : "Seasonal: the season's desktop picture changed; the new one is on.");
		return true;
	}

	private SeasonalResult ApplyCore(SeasonPreset p, SeasonalOriginal orig, string source, bool wantWallpaper, DateTime activeSince, bool isRecovery)
	{
		Stopwatch sw = Stopwatch.StartNew();
		DesktopWallpaperSnapshot? before = orig.Wallpaper?.Clone();
		string plan = "none";
		string? note = null;
		string? skip = null;
		WallpaperProbe probe = _w.ProbeWallpaper();
		if (wantWallpaper && probe.SkipReason != null)
		{
			skip = probe.SkipReason;
		}
		else if (wantWallpaper)
		{
			bool anyOurs = probe.Current?.Monitors.Any(m => _w.IsSeasonalFile(m.Path)) == true;
			if (orig.Wallpaper != null && anyOurs)
			{
				// Keep the original capture. A display the user changed in the meantime is captured again.
				MergeUserChanges(orig.Wallpaper, probe.Current!);
				plan = "set";
			}
			else if (orig.Wallpaper == null && anyOurs)
			{
				Logger.Log("Seasonal: a seasonal picture is showing but no restore point owns it; the wallpaper is left alone.");
			}
			else if (probe.Current != null && probe.Current.Monitors.Count > 0)
			{
				// No seasonal picture is showing: (re)capture the user's own wallpaper, including any change made
				// outside the launcher since the last capture.
				DesktopWallpaperSnapshot fresh = _w.PreserveVolatileFiles(probe.Current.Clone());
				fresh.AppliedPath = "";
				orig.Wallpaper = fresh;
				plan = "set";
			}
		}
		else if (orig.Wallpaper != null)
		{
			plan = "restore-drop";
		}

		string? path = null;
		if (plan == "set")
		{
			path = _w.PrepareWallpaperFile(p.Art);
			if (path == null)
			{
				plan = "none";
				note = "the wallpaper could not be drawn";
				orig.Wallpaper = before;
			}
		}

		SeasonalStateFile pending = new SeasonalStateFile
		{
			Phase = SeasonalTheme.PhaseApplying,
			Source = source,
			Target = p.Id,
			Original = orig,
			ActiveSinceUtc = activeSince,
			LastResult = "applying " + p.Id
		};
		if (!_w.SaveState(pending))
		{
			return Result(false, p.Id, isRecovery ? "Restoring did not finish; it will be retried when the launcher starts." : "Could not save a restore point, so nothing was changed.");
		}
		if (!_w.MutateSettings(delegate(AppSettings x)
		{
			x.StartBgMode = "pattern";
			x.StartPattern = p.Pattern;
			x.StartBgColor = p.Base;
			x.StartAccentColor = p.Accent;
			x.SeasonalActive = p.Id;
		}))
		{
			return Rollback(pending, p, "the settings could not be saved", wantWallpaper, isRecovery);
		}
		string wallpaperOutcome = wantWallpaper ? (skip != null ? "skipped:" + skip : (note != null ? "skipped:not-drawn" : "none")) : "off";
		if (plan == "set")
		{
			if (!_w.SetWallpaper(path!))
			{
				return Rollback(pending, p, "the desktop wallpaper could not be set", wantWallpaper, isRecovery);
			}
			orig.Wallpaper!.AppliedPath = path!;
			wallpaperOutcome = "set";
		}
		else if (plan == "restore-drop")
		{
			string restored = _w.RestoreWallpaper(orig.Wallpaper!);
			if (restored.StartsWith("failed", StringComparison.Ordinal))
			{
				return Rollback(pending, p, "the desktop wallpaper could not be restored", wantWallpaper, isRecovery);
			}
			orig.Wallpaper = null;
			wallpaperOutcome = "restored";
		}
		bool switched = source != p.Id || activeSince == default;
		SeasonalStateFile done = new SeasonalStateFile
		{
			Phase = SeasonalTheme.PhaseIdle,
			Original = orig,
			ActiveSinceUtc = switched ? _w.UtcNow : activeSince,
			LastResult = "applied " + p.Id
		};
		if (!_w.SaveState(done))
		{
			Logger.Log("Seasonal: final state write failed; the next start will roll back to the previous look.");
		}
		Cleanup(orig);
		Logger.Log($"Seasonal: apply {p.Id} (source={source}, wallpaper={wallpaperOutcome}){(isRecovery ? " [recovery]" : "")} in {sw.ElapsedMilliseconds} ms");
		string message = p.Name + " is on.";
		if (skip != null)
		{
			message += " Your desktop wallpaper was left as it is because it uses " + (skip == "slideshow" ? "a slideshow." : skip + ".");
		}
		else if (note != null)
		{
			message += " The desktop wallpaper was left as it is because " + note + ".";
		}
		return new SeasonalResult { Success = true, Season = p.Id, Message = message, WallpaperOutcome = wallpaperOutcome };
	}

	private SeasonalResult Rollback(SeasonalStateFile pending, SeasonPreset p, string reason, bool wantWallpaper, bool isRecovery)
	{
		Logger.Log($"Seasonal: apply {p.Id} failed ({reason}); rolling back to '{pending.Source}'.");
		if (isRecovery)
		{
			return Result(false, p.Id, "Restoring did not finish; it will be retried when the launcher starts.");
		}
		SeasonalResult back;
		SeasonPreset? source = SeasonalTheme.Find(pending.Source);
		if (source == null)
		{
			back = RestoreCore(pending.Original!, p.Id);
		}
		else
		{
			back = ApplyCore(source, pending.Original!.Clone(), source.Id, wantWallpaper, pending.ActiveSinceUtc, isRecovery: true);
		}
		string tail = back.Success ? " Your previous look was restored." : " Your previous look will be restored when the launcher starts.";
		return new SeasonalResult { Success = false, Season = pending.Source, Message = "Could not apply " + p.Name + ": " + reason + "." + tail, WallpaperOutcome = back.WallpaperOutcome };
	}

	private SeasonalResult RestoreCore(SeasonalOriginal orig, string source)
	{
		if (!_w.MutateSettings(delegate(AppSettings x)
		{
			x.StartBgMode = orig.StartBgMode;
			x.StartBgColor = orig.StartBgColor;
			x.StartBgCustomPath = orig.StartBgCustomPath;
			x.StartPattern = orig.StartPattern;
			x.StartAccentColor = orig.StartAccentColor;
			x.SeasonalActive = "";
		}))
		{
			return Result(false, "", "Restoring did not finish; it will be retried when the launcher starts.");
		}
		string wallpaper = "none";
		if (orig.Wallpaper != null)
		{
			wallpaper = _w.RestoreWallpaper(orig.Wallpaper);
			if (wallpaper.StartsWith("failed", StringComparison.Ordinal))
			{
				Logger.Log("Seasonal: restore left pending: " + wallpaper);
				return Result(false, "", "Restoring did not finish; it will be retried when the launcher starts.");
			}
		}
		SeasonalResult marked = MarkIdle(null, $"restored (wallpaper={wallpaper})");
		Cleanup(null);
		Logger.Log($"Seasonal: restore (source={source}, wallpaper={wallpaper})");
		string message = "Normal look restored.";
		bool wasApplied = !string.IsNullOrEmpty(orig.Wallpaper?.AppliedPath);
		if (wallpaper == "kept-user" && wasApplied)
		{
			message += " Your desktop wallpaper was changed outside the launcher, so it was kept.";
		}
		else if (wallpaper == "partial")
		{
			message += " Your desktop wallpaper was changed outside the launcher on some displays, so those were kept.";
		}
		return new SeasonalResult { Success = marked.Success, Season = "", Message = message, WallpaperOutcome = wallpaper };
	}

	private SeasonalResult MarkIdle(SeasonalOriginal? original, string result)
	{
		bool ok = _w.SaveState(new SeasonalStateFile
		{
			Phase = SeasonalTheme.PhaseIdle,
			Original = original,
			LastResult = result
		});
		if (!ok)
		{
			Logger.Log("Seasonal: final state write failed; the next start reconciles it.");
		}
		return Result(true, "", result);
	}

	// A display whose wallpaper is not seasonal any more was changed by the user: that choice becomes the original.
	private void MergeUserChanges(DesktopWallpaperSnapshot orig, DesktopWallpaperSnapshot current)
	{
		foreach (MonitorWallpaper now in current.Monitors)
		{
			if (_w.IsSeasonalFile(now.Path))
			{
				continue;
			}
			MonitorWallpaper? saved = orig.Monitors.FirstOrDefault(m => string.Equals(m.MonitorId, now.MonitorId, StringComparison.OrdinalIgnoreCase));
			if (saved == null)
			{
				orig.Monitors.Add(new MonitorWallpaper { MonitorId = now.MonitorId, Path = now.Path });
			}
			else if (!string.Equals(saved.Path, now.Path, StringComparison.OrdinalIgnoreCase))
			{
				Logger.Log("Seasonal: a display's wallpaper was changed outside the launcher; that picture becomes the one Normal restores.");
				saved.Path = now.Path;
			}
		}
	}

	private static SeasonalOriginal FromSettings(AppSettings s)
	{
		return new SeasonalOriginal
		{
			StartBgMode = s.StartBgMode ?? "",
			StartBgColor = s.StartBgColor ?? "",
			StartBgCustomPath = s.StartBgCustomPath ?? "",
			StartPattern = s.StartPattern ?? "",
			StartAccentColor = s.StartAccentColor ?? ""
		};
	}

	private static bool MatchesAnyPreset(AppSettings s)
	{
		return SeasonalTheme.Presets.Any(p => StartMatches(s, p));
	}

	// The Start values a preset writes (the custom picture path is not one of them).
	private static bool StartMatches(AppSettings s, SeasonPreset p)
	{
		return string.Equals(s.StartBgMode, "pattern", StringComparison.Ordinal)
			&& string.Equals(s.StartPattern, p.Pattern, StringComparison.Ordinal)
			&& string.Equals(s.StartBgColor, p.Base, StringComparison.OrdinalIgnoreCase)
			&& string.Equals(s.StartAccentColor, p.Accent, StringComparison.OrdinalIgnoreCase);
	}

	private static bool StartEquals(AppSettings s, SeasonalOriginal o)
	{
		return string.Equals(s.StartBgMode ?? "", o.StartBgMode, StringComparison.Ordinal)
			&& string.Equals(s.StartBgColor ?? "", o.StartBgColor, StringComparison.OrdinalIgnoreCase)
			&& string.Equals(s.StartBgCustomPath ?? "", o.StartBgCustomPath, StringComparison.OrdinalIgnoreCase)
			&& string.Equals(s.StartPattern ?? "", o.StartPattern, StringComparison.Ordinal)
			&& string.Equals(s.StartAccentColor ?? "", o.StartAccentColor, StringComparison.OrdinalIgnoreCase);
	}

	// Best effort: unused seasonal pictures and wallpaper backups are removed, never anything the restore point needs.
	private void Cleanup(SeasonalOriginal? keep)
	{
		try
		{
			_w.CleanupArt(keep);
		}
		catch (Exception ex)
		{
			Logger.Log("Seasonal: cleanup skipped: " + ex.Message);
		}
	}

	private SeasonalOriginal Stamp(SeasonalOriginal o)
	{
		o.CapturedUtc = _w.UtcNow;
		return o;
	}

	private static SeasonalResult Result(bool ok, string season, string message)
	{
		return new SeasonalResult { Success = ok, Season = season, Message = message };
	}
}

// Seasonal presets: Halloween and Christmas restyle the Start background, the accent everything else follows, and
// (optionally) the desktop wallpaper. Normal restores exactly what the user had before the first seasonal change.
internal static class SeasonalTheme
{
	public const string Normal = "normal";

	public const string Halloween = "halloween";

	public const string Christmas = "christmas";

	public const string PhaseIdle = "idle";

	public const string PhaseApplying = "applying";

	public const string PhaseRestoring = "restoring";

	public static readonly SeasonPreset[] Presets = new SeasonPreset[2]
	{
		new SeasonPreset(Halloween, "Halloween", "pumpkins", "#FF22103A", "#FFE0620B", "bats", "halloween"),
		new SeasonPreset(Christmas, "Christmas", "ornaments", "#FF0B3D2C", "#FFC8102E", "snow", "christmas")
	};

	private const string StateName = "seasonal";

	private const string MutexName = "Local\\Win81Layer.SeasonalTheme.v1";

	public static readonly string StatePath = Path.Combine(
		Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
		"Win81Layer",
		"seasonal-state.json");

	internal static readonly JsonSerializerOptions Json = new JsonSerializerOptions
	{
		PropertyNameCaseInsensitive = true,
		WriteIndented = true
	};

	private static readonly object Gate = new object();

	private static SeasonalStateFile? _cache;

	// True while a restore point exists but could not be read; ownership of the desktop wallpaper is then assumed.
	private static volatile bool _cacheUnreadable;

	private static volatile bool _busy;

	private static string _busyTarget = "";

	// QA hooks, honoured only in read-only diagnostics.
	internal static bool? DiagnosticsOwnsWallpaper { get; set; }

	internal static SeasonalEngine CreateEngine(ISeasonalWorld world)
	{
		return new SeasonalEngine(world);
	}

	public static SeasonPreset? Find(string? id)
	{
		string n = NormalizeId(id);
		return Presets.FirstOrDefault(p => p.Id == n);
	}

	// "", null, "normal" and unknown ids map to "" (normal).
	public static string NormalizeId(string? id)
	{
		if (string.IsNullOrWhiteSpace(id))
		{
			return "";
		}
		string t = id.Trim();
		if (string.Equals(t, Halloween, StringComparison.OrdinalIgnoreCase))
		{
			return Halloween;
		}
		if (string.Equals(t, Christmas, StringComparison.OrdinalIgnoreCase))
		{
			return Christmas;
		}
		return "";
	}

	// True while the restore point holds the user's desktop wallpaper (the launcher changed it). Cached, no IO.
	public static bool OwnsDesktopWallpaper
	{
		get
		{
			if (SettingsStore.ReadOnlyDiagnostics && DiagnosticsOwnsWallpaper.HasValue)
			{
				return DiagnosticsOwnsWallpaper.Value;
			}
			// An unreadable restore point may hold the user's wallpaper, so assume it does until it can be read.
			return _cacheUnreadable || _cache?.Original?.Wallpaper != null;
		}
	}

	// The restore point as the UI last saw it (null when none), for previews of what Normal brings back. No IO.
	public static SeasonalOriginal? CachedOriginal
	{
		get
		{
			if (SettingsStore.ReadOnlyDiagnostics && DiagnosticsOriginal != null)
			{
				return DiagnosticsOriginal;
			}
			return _cache?.Original;
		}
	}

	// QA hook, honoured only in read-only diagnostics: the restore point the previews show.
	internal static SeasonalOriginal? DiagnosticsOriginal { get; set; }

	private static readonly Dictionary<string, ImageSource> PreviewThumbs = new Dictionary<string, ImageSource>(StringComparer.OrdinalIgnoreCase);

	// What Normal brings back, for the Normal tile and chip: the restore point while a season is on, otherwise the
	// current look. UI thread.
	public static Brush NormalPreviewBrush(double scale)
	{
		AppSettings s = SettingsStore.FastSnapshot;
		SeasonalOriginal? o = NormalizeId(s.SeasonalActive).Length > 0 ? CachedOriginal : null;
		string mode = o != null ? o.StartBgMode : s.StartBgMode;
		string color = o != null ? o.StartBgColor : s.StartBgColor;
		string pattern = o != null ? o.StartPattern : s.StartPattern;
		string accent = o != null ? o.StartAccentColor : s.StartAccentColor;
		string custom = o != null ? o.StartBgCustomPath : s.StartBgCustomPath;
		try
		{
			switch (mode)
			{
			case "pattern":
			{
				Color bc = StartBackgroundFactory.Parse(color, Color.FromRgb(60, 30, 112));
				Color ac = string.IsNullOrWhiteSpace(accent) ? StartAccent.AutoFromBase(bc) : StartBackgroundFactory.Parse(accent, bc);
				return StartBackgroundFactory.Build(bc, ac, pattern, scale);
			}
			case "custom":
				if (PreviewThumb(custom) is ImageSource pic)
				{
					return FrozenImageBrush(pic);
				}
				break;
			case "desktop":
			{
				string? wp = o?.Wallpaper?.Monitors.Select(m => m.Path).FirstOrDefault(p => !string.IsNullOrEmpty(p));
				if (wp == null)
				{
					string? now = TaskbarTheme.WallpaperPath();
					wp = SeasonalArt.IsSeasonalFile(now) ? null : now;
				}
				if (wp != null && PreviewThumb(wp) is ImageSource desk)
				{
					return FrozenImageBrush(desk);
				}
				break;
			}
			}
		}
		catch (Exception ex)
		{
			Logger.Log("Seasonal: Normal preview fell back to the default background: " + ex.Message);
		}
		return System.Windows.Application.Current?.TryFindResource("StartBackground") as Brush ?? new SolidColorBrush(Color.FromRgb(60, 30, 112));
	}

	private static ImageSource? PreviewThumb(string? path)
	{
		if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
		{
			return null;
		}
		string key = path + "|" + File.GetLastWriteTimeUtc(path).Ticks;
		if (PreviewThumbs.TryGetValue(key, out ImageSource? hit))
		{
			return hit;
		}
		BitmapImage bmp = new BitmapImage();
		bmp.BeginInit();
		bmp.UriSource = new Uri(path);
		bmp.DecodePixelWidth = 240;
		bmp.CacheOption = BitmapCacheOption.OnLoad;
		bmp.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
		bmp.EndInit();
		bmp.Freeze();
		if (PreviewThumbs.Count >= 4)
		{
			PreviewThumbs.Clear();
		}
		PreviewThumbs[key] = bmp;
		return bmp;
	}

	private static Brush FrozenImageBrush(ImageSource image)
	{
		ImageBrush b = new ImageBrush(image) { Stretch = Stretch.UniformToFill };
		b.Freeze();
		return b;
	}

	public static bool IsBusy => _busy;

	public static Task<SeasonalResult> ApplyAsync(string id)
	{
		TaskCompletionSource<SeasonalResult> tcs = new TaskCompletionSource<SeasonalResult>(TaskCreationOptions.RunContinuationsAsynchronously);
		Thread worker = new Thread((ThreadStart)delegate
		{
			try
			{
				tcs.TrySetResult(ApplyBlocking(id));
			}
			catch (Exception ex)
			{
				tcs.TrySetResult(new SeasonalResult { Success = false, Season = NormalizeId(id), Message = "Could not change the seasonal theme: " + ex.Message });
			}
			finally
			{
				// Rendering the wallpaper creates a dispatcher for this thread; shut it down with the thread.
				Dispatcher.FromThread(Thread.CurrentThread)?.InvokeShutdown();
			}
		})
		{
			IsBackground = true,
			Name = "SeasonalApply"
		};
		worker.SetApartmentState(ApartmentState.STA);
		worker.Start();
		return tcs.Task;
	}

	// Runs on the calling thread, which must be STA when a wallpaper may be drawn.
	public static SeasonalResult ApplyBlocking(string id)
	{
		string target = NormalizeId(id);
		if (SettingsStore.ReadOnlyDiagnostics)
		{
			return new SeasonalResult { Success = false, Season = target, Message = "Read-only diagnostics: no changes made." };
		}
		if (!Monitor.TryEnter(Gate))
		{
			return new SeasonalResult { Success = false, Season = target, Message = "Another seasonal change is still running." };
		}
		try
		{
			_busy = true;
			_busyTarget = target;
			return RunLocked(engine => target.Length == 0 ? engine.Restore() : engine.Apply(target), target);
		}
		finally
		{
			_busy = false;
			_busyTarget = "";
			Monitor.Exit(Gate);
		}
	}

	private static SeasonalResult RunLocked(Func<SeasonalEngine, SeasonalResult> work, string target)
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
				Logger.Log("Seasonal: transaction lock was abandoned by another process; ownership recovered.");
			}
			if (!owned)
			{
				return new SeasonalResult { Success = false, Season = target, Message = "Another seasonal change is still running." };
			}
			SeasonalResult r = work(new SeasonalEngine(new RealSeasonalWorld()));
			RefreshCache();
			return r;
		}
		catch (Exception ex)
		{
			Logger.Log("Seasonal: change failed: " + ex);
			RefreshCache();
			return new SeasonalResult { Success = false, Season = target, Message = "Could not change the seasonal theme: " + ex.Message };
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

	// True when the active season's desktop picture is no longer the one applied (the user chose or removed a picture
	// while the launcher was not running). A few file checks; safe on the UI thread.
	private static bool PictureChanged(AppSettings s, string applied)
	{
		try
		{
			SeasonPreset? p = Find(NormalizeId(s.SeasonalActive));
			return p != null && !string.Equals(Path.GetFileName(applied), SeasonalArt.ExpectedWallpaperName(p.Art), StringComparison.OrdinalIgnoreCase);
		}
		catch
		{
			return false;
		}
	}

	// Startup: load the state (one small file) on the UI thread. An interrupted change, a marker that disagrees with
	// the restore point, or a missing wallpaper file is handled on a worker a few seconds later, once Explorer is up.
	public static void InitializeAndRecover(App app)
	{
		SeasonalStateFile st = LoadStateFile(out bool unreadable) ?? new SeasonalStateFile();
		_cache = st;
		_cacheUnreadable = unreadable;
		if (SettingsStore.ReadOnlyDiagnostics)
		{
			return;
		}
		AppSettings s = SettingsStore.Load();
		bool marker = NormalizeId(s.SeasonalActive).Length > 0;
		string applied = st.Original?.Wallpaper?.AppliedPath ?? "";
		bool needs;
		if (unreadable || SettingsStore.LoadWasDegraded)
		{
			// The marker cannot be trusted from a degraded read. The worker reads both again and changes nothing while
			// either is still unreadable.
			needs = unreadable || st.Phase != PhaseIdle || st.Original != null;
		}
		else
		{
			needs = st.Phase != PhaseIdle || (!marker && st.Original != null) || (marker && applied.Length > 0 && (!File.Exists(applied) || PictureChanged(s, applied)));
		}
		if (!needs)
		{
			return;
		}
		Logger.Log($"Seasonal: startup recovery scheduled (phase={st.Phase}, marker={marker}, restorePoint={st.Original != null}, unreadable={unreadable}, settingsDegraded={SettingsStore.LoadWasDegraded})");
		Thread worker = new Thread((ThreadStart)delegate
		{
			try
			{
				Thread.Sleep(5000);
				bool ran = false;
				if (Monitor.TryEnter(Gate, TimeSpan.FromSeconds(30)))
				{
					try
					{
						_busy = true;
						RunLocked(engine => engine.Recover(), "");
						ran = true;
					}
					finally
					{
						_busy = false;
						Monitor.Exit(Gate);
					}
				}
				if (ran)
				{
					app.Dispatcher.BeginInvoke((Action)app.ApplySeasonalLive);
				}
			}
			catch (Exception ex)
			{
				Logger.Log("Seasonal: startup recovery failed: " + ex.Message);
			}
			finally
			{
				Dispatcher.FromThread(Thread.CurrentThread)?.InvokeShutdown();
			}
		})
		{
			IsBackground = true,
			Name = "SeasonalRecover"
		};
		worker.SetApartmentState(ApartmentState.STA);
		worker.Start();
	}

	public static void InvalidateCache()
	{
		RefreshCache();
	}

	private static void RefreshCache()
	{
		try
		{
			SeasonalStateFile? st = LoadStateFile(out bool unreadable);
			_cacheUnreadable = unreadable;
			if (st != null || !unreadable)
			{
				_cache = st ?? new SeasonalStateFile();
			}
		}
		catch
		{
		}
	}

	public static SeasonalStatus Describe()
	{
		SeasonalStateFile st = _cache ?? new SeasonalStateFile();
		string active = NormalizeId(SettingsStore.FastSnapshot.SeasonalActive);
		return new SeasonalStatus
		{
			ActiveId = active,
			ActiveName = Find(active)?.Name ?? "Normal",
			SinceUtc = active.Length > 0 && st.ActiveSinceUtc != default ? st.ActiveSinceUtc : null,
			OwnsWallpaper = OwnsDesktopWallpaper,
			Phase = st.Phase,
			Busy = _busy,
			BusyTarget = _busyTarget,
			LastResult = st.LastResult,
			Unreadable = _cacheUnreadable
		};
	}

	public static string StatusText(SeasonalStatus status)
	{
		if (status.Busy)
		{
			return status.BusyTarget.Length == 0 ? "Restoring your normal look…" : "Applying " + (Find(status.BusyTarget)?.Name ?? "the seasonal theme") + "…";
		}
		if (status.Unreadable)
		{
			return "The seasonal restore point cannot be read right now, so seasonal changes are paused. Try again in a moment.";
		}
		if (status.Phase != PhaseIdle)
		{
			return "A seasonal change was interrupted; it will be finished automatically.";
		}
		if (status.ActiveId.Length == 0)
		{
			return "Normal is active.";
		}
		string since = status.SinceUtc.HasValue ? " since " + status.SinceUtc.Value.ToLocalTime().ToString("d MMM yyyy", System.Globalization.CultureInfo.InvariantCulture) : "";
		return status.ActiveName + " is on" + since + ". " + RestoresText(status.OwnsWallpaper);
	}

	// "Normal restores your previous Start background, accent colour and desktop wallpaper, and your apps' usual icons."
	public static string RestoresText(bool ownsWallpaper)
	{
		string list = ownsWallpaper ? "Start background, accent colour and desktop wallpaper" : "Start background and accent colour";
		bool icons = false;
		try
		{
			icons = SeasonalAppIcons.IsActive;
		}
		catch
		{
		}
		return "Normal restores your previous " + list + (icons ? ", and your apps' usual icons." : ".");
	}

	// null when there is no restore point. unreadable is set when one exists but cannot be opened right now (for
	// example locked by another program): that is never treated as "no restore point".
	internal static SeasonalStateFile? LoadStateFile(out bool unreadable)
	{
		unreadable = false;
		DurableStateReadResult read = DurableStateStore.Read(StateName, StatePath, null, ValidateStateJson, 8);
		if (read.Json == null)
		{
			unreadable = StateFileLocked();
			if (unreadable)
			{
				Logger.Log("Seasonal: the restore point exists but could not be read right now.");
			}
			return null;
		}
		try
		{
			return JsonSerializer.Deserialize<SeasonalStateFile>(read.Json, Json);
		}
		catch (Exception ex)
		{
			Logger.Log("Seasonal: state parse failed after validation: " + ex.Message);
			unreadable = true;
			return null;
		}
	}

	// A state file that exists but cannot be opened is a transient failure. One that opens but holds no valid state
	// (corrupt everywhere) cannot be recovered and is treated as missing, so it can never block the seasonal controls.
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
				Logger.Log("Seasonal: state file not readable: " + ex.Message);
				return true;
			}
		}
		if (File.Exists(StatePath))
		{
			Logger.Log("Seasonal: the restore point is corrupt and has no valid copy; it is treated as missing.");
		}
		return false;
	}

	internal static bool SaveStateFile(SeasonalStateFile state)
	{
		string json = JsonSerializer.Serialize(state, Json);
		return DurableStateStore.Commit(StateName, StatePath, json, ValidateStateJson).IsDurable;
	}

	internal static bool ValidateStateJson(string json)
	{
		try
		{
			SeasonalStateFile? st = JsonSerializer.Deserialize<SeasonalStateFile>(json, Json);
			return st != null && st.SchemaVersion == 1;
		}
		catch
		{
			return false;
		}
	}
}

// The real world: durable settings and state, the desktop wallpaper through Windows, art drawn by SeasonalArt.
internal sealed class RealSeasonalWorld : ISeasonalWorld
{
	private static readonly string BackupDir = Path.Combine(
		Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
		"Win81Layer",
		"seasonal-backup");

	public DateTime UtcNow => DateTime.UtcNow;

	public AppSettings LoadSettings()
	{
		if (!SettingsStore.TryLoadDurable(out AppSettings settings))
		{
			throw new SeasonalReadException("Could not read the launcher settings, so nothing was changed.");
		}
		return settings;
	}

	public bool MutateSettings(Action<AppSettings> mutate)
	{
		return SettingsStore.TryUpdate(mutate);
	}

	public SeasonalStateFile LoadState()
	{
		SeasonalStateFile? st = SeasonalTheme.LoadStateFile(out bool unreadable);
		if (st == null && unreadable)
		{
			throw new SeasonalReadException("Could not read the restore point, so nothing was changed.");
		}
		return st ?? new SeasonalStateFile();
	}

	public bool SaveState(SeasonalStateFile state)
	{
		state.UpdatedUtc = DateTime.UtcNow;
		return SeasonalTheme.SaveStateFile(state);
	}

	public WallpaperProbe ProbeWallpaper()
	{
		DesktopWallpaperSnapshot snap = DesktopWallpaperApi.Capture(out string? skip);
		return new WallpaperProbe { Current = snap, SkipReason = skip };
	}

	public DesktopWallpaperSnapshot PreserveVolatileFiles(DesktopWallpaperSnapshot snapshot)
	{
		string themes = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Microsoft", "Windows", "Themes");
		for (int i = 0; i < snapshot.Monitors.Count; i++)
		{
			MonitorWallpaper m = snapshot.Monitors[i];
			try
			{
				if (string.IsNullOrEmpty(m.Path) || !File.Exists(m.Path))
				{
					continue;
				}
				string full = Path.GetFullPath(m.Path);
				// Windows rewrites these files whenever the wallpaper changes, so they would show the seasonal picture later.
				if (!full.StartsWith(themes.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase))
				{
					continue;
				}
				Directory.CreateDirectory(BackupDir);
				string copy = Path.Combine(BackupDir, $"original-{i}-{DateTime.UtcNow:yyyyMMddHHmmss}{ImageExtension(full)}");
				File.Copy(full, copy, overwrite: true);
				Logger.Log("Seasonal: copied a wallpaper Windows may overwrite so it can be restored exactly.");
				m.Path = copy;
			}
			catch (Exception ex)
			{
				Logger.Log("Seasonal: wallpaper backup skipped: " + ex.Message);
			}
		}
		return snapshot;
	}

	private static string ImageExtension(string path)
	{
		string ext = Path.GetExtension(path);
		if (!string.IsNullOrEmpty(ext))
		{
			return ext;
		}
		try
		{
			byte[] head = new byte[4];
			using (FileStream fs = File.OpenRead(path))
			{
				fs.ReadExactly(head, 0, 4);
			}
			if (head[0] == 0x89 && head[1] == 0x50)
			{
				return ".png";
			}
			if (head[0] == 0x42 && head[1] == 0x4D)
			{
				return ".bmp";
			}
		}
		catch
		{
		}
		return ".jpg";
	}

	public string? PrepareWallpaperFile(string art)
	{
		try
		{
			(int w, int h) = SeasonalArt.TargetWallpaperSize();
			string? picture = SeasonalArt.CustomPictureFor(art);
			string path = Path.Combine(SeasonalArt.SeasonalDir, picture != null ? SeasonalArt.CustomWallpaperFileName(art, picture, w, h) : SeasonalArt.WallpaperFileName(art, w, h));
			if (!File.Exists(path) || new FileInfo(path).Length < 1024)
			{
				Stopwatch sw = Stopwatch.StartNew();
				if (picture != null)
				{
					SeasonalArt.RenderPicturePng(picture, w, h, path);
					Logger.Log($"Seasonal: fitted the user's {art} picture to {w}x{h} in {sw.ElapsedMilliseconds} ms");
				}
				else
				{
					SeasonalArt.RenderWallpaperPng(art, w, h, path);
					Logger.Log($"Seasonal: drew the {art} wallpaper at {w}x{h} in {sw.ElapsedMilliseconds} ms");
				}
			}
			return path;
		}
		catch (Exception ex)
		{
			Logger.Log("Seasonal: wallpaper drawing failed: " + ex.Message);
			return null;
		}
	}

	public string ExpectedWallpaperName(string art)
	{
		return SeasonalArt.ExpectedWallpaperName(art);
	}

	public bool SetWallpaper(string path)
	{
		return DesktopWallpaperApi.SetAll(path);
	}

	public string RestoreWallpaper(DesktopWallpaperSnapshot snapshot)
	{
		return DesktopWallpaperApi.RestoreIfOurs(snapshot, SeasonalArt.IsSeasonalFile);
	}

	public bool IsSeasonalFile(string? path)
	{
		return SeasonalArt.IsSeasonalFile(path);
	}

	public bool FileExists(string path)
	{
		return File.Exists(path);
	}

	public void CleanupArt(SeasonalOriginal? keep)
	{
		bool art = Directory.Exists(SeasonalArt.SeasonalDir);
		bool backups = Directory.Exists(BackupDir);
		if (!art && !backups)
		{
			return;
		}
		DesktopWallpaperSnapshot now = DesktopWallpaperApi.Capture(out _);
		HashSet<string> inUse = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		foreach (string path in now.Monitors.Select(m => m.Path)
			.Concat(keep?.Wallpaper?.Monitors.Select(m => m.Path) ?? Enumerable.Empty<string>())
			.Append(keep?.Wallpaper?.AppliedPath ?? ""))
		{
			string full = FullPath(path);
			if (full.Length > 0)
			{
				inUse.Add(full);
			}
		}
		if (art)
		{
			DeleteUnused(Directory.EnumerateFiles(SeasonalArt.SeasonalDir, "*.png"), inUse);
		}
		if (backups)
		{
			// Copies of the user's own wallpaper made when a season started: kept while a display shows one or the
			// restore point refers to it, removed afterwards.
			DeleteUnused(Directory.EnumerateFiles(BackupDir, "original-*"), inUse);
		}
	}

	private static void DeleteUnused(IEnumerable<string> files, HashSet<string> inUse)
	{
		foreach (string file in files.ToList())
		{
			try
			{
				if (!inUse.Contains(FullPath(file)))
				{
					File.Delete(file);
				}
			}
			catch
			{
			}
		}
	}

	private static string FullPath(string? path)
	{
		if (string.IsNullOrWhiteSpace(path))
		{
			return "";
		}
		try
		{
			return Path.GetFullPath(path);
		}
		catch
		{
			return path;
		}
	}
}
