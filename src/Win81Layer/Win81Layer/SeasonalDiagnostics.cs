#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;

namespace Win81Layer;

// --seasonaltest [outDir]: offscreen QA of the seasonal presets. Read-only by construction: settings and state writes
// throw, the desktop wallpaper setters refuse to run, and renders use diagnostics overrides instead of real writes.
// The transaction logic is driven against an in-memory fake world, including a crash at every write.
internal static class SeasonalDiagnostics
{
	private static readonly List<(string Name, bool Pass, string Detail)> Checks = new List<(string, bool, string)>();

	private static readonly Dictionary<string, object> Metrics = new Dictionary<string, object>();

	private static readonly string Root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Win81Layer");

	public static void Begin(Application app, string[] args)
	{
		app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
		int idx = Array.IndexOf(args, "--seasonaltest");
		string outDir = (idx >= 0 && idx + 1 < args.Length && !args[idx + 1].StartsWith("--", StringComparison.Ordinal))
			? args[idx + 1]
			: Path.Combine(Root, "qa", "seasonal");
		app.Dispatcher.BeginInvoke((Action)delegate
		{
			int code = 1;
			try
			{
				code = Run(outDir) ? 0 : 1;
			}
			catch (Exception ex)
			{
				try
				{
					Directory.CreateDirectory(outDir);
					File.WriteAllText(Path.Combine(outDir, "seasonaltest-error.txt"), ex.ToString());
				}
				catch
				{
				}
			}
			app.Shutdown(code);
		}, DispatcherPriority.Loaded);
	}

	private static bool Run(string outDir)
	{
		Directory.CreateDirectory(outDir);
		Baseline before = Baseline.Capture();
		AppSettings real = SettingsStore.Load();
		try
		{
			SafetyChecks(outDir);
			EngineChecks();
			ArtChecks(outDir);
			LiveChecks(outDir, real);
		}
		catch (Exception ex)
		{
			Check("harness-completed", false, ex.ToString());
		}
		finally
		{
			SettingsStore.DiagnosticsOverride = null;
			TaskbarTheme.DiagnosticsWallpaperPath = null;
			SeasonalTheme.DiagnosticsOwnsWallpaper = null;
			SeasonalTheme.DiagnosticsOriginal = null;
			TaskbarTheme.Invalidate();
		}
		Baseline after = Baseline.Capture();
		Check("no-settings-write", before.Hashes == after.Hashes, before.Hashes == after.Hashes ? "" : $"before={before.Hashes} after={after.Hashes}");
		Check("no-wallpaper-change", before.Wallpaper == after.Wallpaper && before.Registry == after.Registry, $"before={before.Wallpaper} after={after.Wallpaper}");
		Check("no-seasonal-folder-files", before.SeasonalFolder == after.SeasonalFolder, $"before={before.SeasonalFolder} after={after.SeasonalFolder}");
		string[] pngs = Directory.GetFiles(outDir, "*.png");
		string[] expected = new string[25]
		{
			"wallpaper-halloween.png", "wallpaper-christmas.png", "wallpaper-christmas-1280x1024.png", "wallpaper-halloween-1280x1024.png", "patterns.png", "designs-thumbs.png",
			"start-halloween.png", "start-christmas.png", "personalize-halloween.png", "ambient-bats.png", "ambient-snow.png", "actioncenter-halloween.png", "actioncenter-christmas.png",
			"taskbar-halloween.png", "taskbar-christmas.png", "win7glass-halloween.png", "win7glass-christmas.png", "pcsettings-seasonal-halloween.png", "personalize-christmas.png",
			"pcsettings-startscreen-christmas.png", "start-normal.png", "wallpaper-halloween-2560x1080.png", "start-before.png", "actioncenter-normal.png", "taskbar-normal.png"
		};
		List<string> missing = expected.Where(n => !File.Exists(Path.Combine(outDir, n)) || new FileInfo(Path.Combine(outDir, n)).Length <= 10240).ToList();
		Check("renders-present", missing.Count == 0, missing.Count == 0 ? $"{expected.Length} renders" : "missing or small: " + string.Join(", ", missing));
		Metrics["Renders"] = pngs.Select(Path.GetFileName).ToArray();

		bool passed = Checks.All(c => c.Pass);
		StringBuilder txt = new StringBuilder();
		foreach ((string name, bool pass, string detail) in Checks)
		{
			txt.AppendLine(pass ? "PASS " + name : "FAIL " + name + ": " + detail);
		}
		File.WriteAllText(Path.Combine(outDir, "seasonaltest-results.txt"), txt.ToString());
		var report = new
		{
			SchemaVersion = 1,
			GeneratedUtc = DateTime.UtcNow,
			Passed = passed,
			Checks = Checks.Select(c => new { c.Name, c.Pass, c.Detail }).ToArray(),
			Metrics
		};
		File.WriteAllText(Path.Combine(outDir, "seasonaltest-report.json"), JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
		Logger.Log($"SEASONAL-QA: passed={passed} checks={Checks.Count} failed={Checks.Count(c => !c.Pass)} -> {outDir}");
		return passed;
	}

	private static void Check(string name, bool pass, string detail = "")
	{
		Checks.Add((name, pass, detail));
	}

	// ===================================================================== safety

	private static void SafetyChecks(string outDir)
	{
		bool refused = false;
		string detail = "";
		try
		{
			SeasonalResult a = SeasonalTheme.ApplyBlocking(SeasonalTheme.Halloween);
			SeasonalResult b = SeasonalTheme.ApplyAsync(SeasonalTheme.Normal).Result;
			refused = !a.Success && !b.Success && a.Message == "Read-only diagnostics: no changes made." && b.Message == a.Message;
			detail = a.Message + " | " + b.Message;
		}
		catch (Exception ex)
		{
			detail = ex.Message;
		}
		Check("readonly-apply-refused", refused, detail);
		bool guarded = false;
		try
		{
			DesktopWallpaperApi.SetAll(Path.Combine(outDir, "never-applied.png"));
		}
		catch (InvalidOperationException)
		{
			guarded = true;
		}
		catch (Exception ex)
		{
			detail = ex.Message;
		}
		bool restoreGuarded = false;
		try
		{
			DesktopWallpaperApi.RestoreIfOurs(new DesktopWallpaperSnapshot(), _ => true);
		}
		catch (InvalidOperationException)
		{
			restoreGuarded = true;
		}
		Check("wallpaper-setters-guarded", guarded && restoreGuarded, guarded ? "" : detail);
	}

	// ===================================================================== engine (fake world)

	private sealed class SimulatedCrash : Exception
	{
	}

	private sealed class FakeSeasonalWorld : ISeasonalWorld
	{
		public const string Folder = "X:\\seasonal\\";

		private string _settingsJson;

		public string? StateJson;

		public List<MonitorWallpaper> Monitors = new List<MonitorWallpaper>
		{
			new MonitorWallpaper { MonitorId = "MON1", Path = "C:\\Pictures\\desk1.jpg" },
			new MonitorWallpaper { MonitorId = "MON2", Path = "C:\\Pictures\\desk2.jpg" }
		};

		public int Position = 3;

		public uint BgColor = 0x102030;

		public string Style = "6";

		public string? SkipReason;

		public int CrashAfter = -1;

		public int Writes;

		// Failure injection: the restore point or the settings exist but cannot be read; setting the wallpaper fails.
		public bool StateUnreadable;

		public bool SettingsUnreadable;

		public bool FailSetWallpaper;

		public readonly HashSet<string> Files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

		public DateTime Now = new DateTime(2026, 10, 9, 10, 0, 0, DateTimeKind.Utc);

		public FakeSeasonalWorld()
		{
			AppSettings s = new AppSettings
			{
				StartBgMode = "custom",
				StartBgCustomPath = "C:\\Pictures\\mine.jpg",
				StartBgColor = "#FFA61E5A",
				StartPattern = "waves",
				StartAccentColor = ""
			};
			_settingsJson = JsonSerializer.Serialize(s);
		}

		public DateTime UtcNow => Now;

		public AppSettings LoadSettings()
		{
			if (SettingsUnreadable)
			{
				throw new SeasonalReadException("Could not read the launcher settings, so nothing was changed.");
			}
			return Peek();
		}

		public AppSettings Peek()
		{
			return JsonSerializer.Deserialize<AppSettings>(_settingsJson)!;
		}

		public void SetSettings(Action<AppSettings> change)
		{
			AppSettings s = Peek();
			change(s);
			_settingsJson = JsonSerializer.Serialize(s);
		}

		public bool MutateSettings(Action<AppSettings> mutate)
		{
			if (SettingsUnreadable)
			{
				return false;
			}
			AppSettings s = Peek();
			mutate(s);
			s.SeasonalActive = SeasonalTheme.NormalizeId(s.SeasonalActive);
			_settingsJson = JsonSerializer.Serialize(s);
			Landed();
			return true;
		}

		public SeasonalStateFile LoadState()
		{
			if (StateUnreadable)
			{
				throw new SeasonalReadException("Could not read the restore point, so nothing was changed.");
			}
			return PeekState();
		}

		public SeasonalStateFile PeekState()
		{
			return StateJson == null ? new SeasonalStateFile() : JsonSerializer.Deserialize<SeasonalStateFile>(StateJson, SeasonalTheme.Json)!;
		}

		public bool SaveState(SeasonalStateFile state)
		{
			state.UpdatedUtc = Now;
			StateJson = JsonSerializer.Serialize(state, SeasonalTheme.Json);
			Landed();
			return true;
		}

		public WallpaperProbe ProbeWallpaper()
		{
			return new WallpaperProbe
			{
				Current = new DesktopWallpaperSnapshot
				{
					Api = "com",
					Monitors = Monitors.Select(m => new MonitorWallpaper { MonitorId = m.MonitorId, Path = m.Path }).ToList(),
					Position = Position,
					BackgroundColor = BgColor,
					RegWallPaper = Monitors[0].Path,
					RegWallpaperStyle = Style,
					RegTileWallpaper = "0",
					History = new List<string?> { "C:\\Pictures\\desk1.jpg", null, null, null, null }
				},
				SkipReason = SkipReason
			};
		}

		public DesktopWallpaperSnapshot PreserveVolatileFiles(DesktopWallpaperSnapshot snapshot)
		{
			return snapshot;
		}

		// Drawing the picture into the launcher's own folder is not a change to the user's state, so it is not a write.
		public string? PrepareWallpaperFile(string art)
		{
			string path = Folder + SeasonalArt.WallpaperFileName(art, 1920, 1080);
			Files.Add(path);
			return path;
		}

		public bool SetWallpaper(string path)
		{
			if (FailSetWallpaper)
			{
				return false;
			}
			foreach (MonitorWallpaper m in Monitors)
			{
				m.Path = path;
			}
			SetPosition(4);
			Landed();
			return true;
		}

		public string RestoreWallpaper(DesktopWallpaperSnapshot snapshot)
		{
			int restored = 0;
			int kept = 0;
			string first = snapshot.Monitors.Select(m => m.Path).FirstOrDefault(p => !string.IsNullOrEmpty(p)) ?? "";
			foreach (MonitorWallpaper m in Monitors)
			{
				MonitorWallpaper? saved = snapshot.Monitors.FirstOrDefault(x => x.MonitorId == m.MonitorId);
				if (!IsSeasonalFile(m.Path))
				{
					if (saved != null)
					{
						kept++;
					}
					continue;
				}
				string target = saved?.Path ?? first;
				if (target.Length > 0)
				{
					m.Path = target;
					restored++;
				}
			}
			if (restored > 0)
			{
				SetPosition(snapshot.Position);
				BgColor = snapshot.BackgroundColor;
			}
			Landed();
			return restored == 0 ? "kept-user" : (kept > 0 ? "partial" : "restored");
		}

		private void SetPosition(int position)
		{
			Position = position;
			Style = position switch
			{
				3 => "6",
				4 => "10",
				2 => "2",
				_ => "0",
			};
		}

		public bool IsSeasonalFile(string? path)
		{
			return path != null && path.StartsWith(Folder, StringComparison.OrdinalIgnoreCase);
		}

		public bool FileExists(string path)
		{
			return !IsSeasonalFile(path) || Files.Contains(path);
		}

		public void CleanupArt(SeasonalOriginal? keep)
		{
			Files.RemoveWhere(f => Monitors.All(m => !string.Equals(m.Path, f, StringComparison.OrdinalIgnoreCase))
				&& !string.Equals(keep?.Wallpaper?.AppliedPath, f, StringComparison.OrdinalIgnoreCase));
		}

		private void Landed()
		{
			Writes++;
			if (CrashAfter >= 0 && Writes == CrashAfter)
			{
				throw new SimulatedCrash();
			}
		}

		public string World()
		{
			AppSettings s = Peek();
			return $"bg={s.StartBgMode}|{s.StartBgCustomPath}|{s.StartBgColor}|{s.StartPattern}|{s.StartAccentColor}|active={s.SeasonalActive}|wp={string.Join(",", Monitors.Select(m => m.MonitorId + "=" + m.Path))}|pos={Position}|style={Style}|color={BgColor:X6}";
		}

		public string OriginalJson(bool blankApplied)
		{
			SeasonalStateFile st = PeekState();
			SeasonalOriginal? o = st.Original?.Clone();
			if (o?.Wallpaper != null && blankApplied)
			{
				o.Wallpaper.AppliedPath = "";
			}
			return o == null ? "null" : JsonSerializer.Serialize(o, SeasonalTheme.Json);
		}
	}

	private static FakeSeasonalWorld Halloweened(out SeasonalEngine engine)
	{
		FakeSeasonalWorld w = new FakeSeasonalWorld();
		engine = SeasonalTheme.CreateEngine(w);
		engine.Apply(SeasonalTheme.Halloween);
		w.Now = w.Now.AddHours(1);
		w.Writes = 0;
		return w;
	}

	private static void EngineChecks()
	{
		// round trip
		{
			FakeSeasonalWorld w = new FakeSeasonalWorld();
			string original = w.World();
			SeasonalEngine e = SeasonalTheme.CreateEngine(w);
			SeasonalResult on = e.Apply(SeasonalTheme.Halloween);
			string during = w.World();
			bool seasonal = on.Success && w.LoadSettings().SeasonalActive == SeasonalTheme.Halloween && w.Monitors.All(m => w.IsSeasonalFile(m.Path)) && w.Position == 4;
			w.Now = w.Now.AddHours(1);
			SeasonalResult off = e.Apply(SeasonalTheme.Normal);
			SeasonalStateFile st = w.LoadState();
			bool ok = seasonal && off.Success && w.World() == original && st.Phase == SeasonalTheme.PhaseIdle && st.Original == null && off.Message == "Normal look restored." && w.Files.Count == 0;
			Check("roundtrip-normal-halloween-normal", ok, $"original={original} during={during} after={w.World()} msg={on.Message}/{off.Message} files={w.Files.Count}");
			Metrics["EngineRoundTripMessages"] = new[] { on.Message, off.Message };
		}
		// already on: no writes
		{
			FakeSeasonalWorld w = Halloweened(out SeasonalEngine e);
			SeasonalResult again = e.Apply(SeasonalTheme.Halloween);
			Check("already-on-is-a-no-op", again.Success && w.Writes == 0 && again.Message == "Halloween is already on.", $"writes={w.Writes} msg={again.Message}");
		}
		// switch keeps the original
		{
			FakeSeasonalWorld w = new FakeSeasonalWorld();
			string original = w.World();
			SeasonalEngine e = SeasonalTheme.CreateEngine(w);
			e.Apply(SeasonalTheme.Halloween);
			string origAfterHalloween = w.OriginalJson(blankApplied: true);
			DateTime captured = w.LoadState().Original!.CapturedUtc;
			w.Now = w.Now.AddDays(10);
			SeasonalResult xmas = e.Apply(SeasonalTheme.Christmas);
			string origAfterChristmas = w.OriginalJson(blankApplied: true);
			SeasonalOriginal o = w.LoadState().Original!;
			bool clean = !SeasonalArt.IsSeasonalPattern(o.StartPattern)
				&& SeasonalTheme.Presets.All(p => !string.Equals(p.Base, o.StartBgColor, StringComparison.OrdinalIgnoreCase) && !string.Equals(p.Accent, o.StartAccentColor, StringComparison.OrdinalIgnoreCase))
				&& o.Wallpaper != null && o.Wallpaper.Monitors.All(m => !w.IsSeasonalFile(m.Path));
			bool christmasOn = xmas.Success && w.LoadSettings().SeasonalActive == SeasonalTheme.Christmas && w.Monitors.All(m => m.Path.Contains("christmas"));
			w.Now = w.Now.AddDays(10);
			e.Apply(SeasonalTheme.Normal);
			bool ok = origAfterHalloween == origAfterChristmas && o.CapturedUtc == captured && clean && christmasOn && w.World() == original;
			Check("switch-keeps-original", ok, $"same={origAfterHalloween == origAfterChristmas} captured={o.CapturedUtc == captured} clean={clean} christmasOn={christmasOn} final={w.World()}");
		}
		// crash at every write, then recover
		CrashRecovery("normal-to-halloween", fromHalloween: false, SeasonalTheme.Halloween);
		CrashRecovery("halloween-to-christmas", fromHalloween: true, SeasonalTheme.Christmas);
		CrashRecovery("halloween-to-normal", fromHalloween: true, SeasonalTheme.Normal);
		// the user's newer wallpaper wins
		{
			FakeSeasonalWorld w = Halloweened(out SeasonalEngine e);
			w.Monitors[0].Path = "D:\\new.jpg";
			SeasonalResult r = e.Apply(SeasonalTheme.Normal);
			bool ok = w.Monitors[0].Path == "D:\\new.jpg" && w.Monitors[1].Path == "C:\\Pictures\\desk2.jpg" && r.Message.Contains("kept") && r.WallpaperOutcome == "partial";
			Check("user-wallpaper-wins", ok, $"{w.World()} msg={r.Message} outcome={r.WallpaperOutcome}");
		}
		// re-capture after an out-of-band change (both displays, then one display)
		{
			FakeSeasonalWorld w = Halloweened(out SeasonalEngine e);
			foreach (MonitorWallpaper m in w.Monitors)
			{
				m.Path = "D:\\new.jpg";
			}
			e.Apply(SeasonalTheme.Christmas);
			bool recaptured = w.LoadState().Original!.Wallpaper!.Monitors.All(m => m.Path == "D:\\new.jpg") && w.Monitors.All(m => w.IsSeasonalFile(m.Path));
			e.Apply(SeasonalTheme.Normal);
			bool allNew = w.Monitors.All(m => m.Path == "D:\\new.jpg");

			FakeSeasonalWorld w2 = Halloweened(out SeasonalEngine e2);
			w2.Monitors[0].Path = "D:\\new.jpg";
			e2.Apply(SeasonalTheme.Christmas);
			e2.Apply(SeasonalTheme.Normal);
			bool merged = w2.Monitors[0].Path == "D:\\new.jpg" && w2.Monitors[1].Path == "C:\\Pictures\\desk2.jpg";
			Check("recapture-after-user-change", recaptured && allNew && merged, $"recaptured={recaptured} final={w.World()} oneDisplay={w2.World()}");
		}
		// slideshow / Spotlight: Start look applied, wallpaper untouched
		foreach ((string name, string reason) in new (string, string)[2] { ("skip-slideshow", "slideshow"), ("skip-spotlight", "Windows Spotlight") })
		{
			FakeSeasonalWorld w = new FakeSeasonalWorld { SkipReason = reason };
			string wpBefore = string.Join(",", w.Monitors.Select(m => m.Path)) + w.Position;
			SeasonalEngine e = SeasonalTheme.CreateEngine(w);
			SeasonalResult r = e.Apply(SeasonalTheme.Halloween);
			string wpAfter = string.Join(",", w.Monitors.Select(m => m.Path)) + w.Position;
			bool ok = r.Success && w.LoadSettings().StartPattern == "pumpkins" && wpBefore == wpAfter && r.Message.Contains(reason) && w.LoadState().Original!.Wallpaper == null;
			Check(name, ok, r.Message);
		}
		// wallpaper option turned off while a season is on
		{
			FakeSeasonalWorld w = Halloweened(out SeasonalEngine e);
			w.SetSettings(x => x.SeasonalWallpaper = false);
			SeasonalResult r = e.Apply(SeasonalTheme.Halloween);
			SeasonalStateFile st = w.LoadState();
			bool ok = r.Success && w.Monitors[0].Path == "C:\\Pictures\\desk1.jpg" && w.Monitors[1].Path == "C:\\Pictures\\desk2.jpg" && w.Position == 3
				&& st.Original != null && st.Original.Wallpaper == null && w.LoadSettings().SeasonalActive == SeasonalTheme.Halloween;
			w.SetSettings(x => x.SeasonalWallpaper = true);
			SeasonalResult back = e.Apply(SeasonalTheme.Halloween);
			bool onAgain = back.Success && w.Monitors.All(m => w.IsSeasonalFile(m.Path)) && w.LoadState().Original!.Wallpaper != null;
			Check("wallpaper-option-off-restores", ok && onAgain, $"{w.World()} msg={r.Message} onAgain={onAgain}");
		}
		// lost restore point: never snapshot a seasonal look
		{
			FakeSeasonalWorld w = new FakeSeasonalWorld();
			w.SetSettings(delegate(AppSettings x)
			{
				x.StartBgMode = "pattern";
				x.StartPattern = "pumpkins";
				x.StartBgColor = "#FF22103A";
				x.StartAccentColor = "#FFE0620B";
				x.SeasonalActive = SeasonalTheme.Halloween;
			});
			SeasonalEngine e = SeasonalTheme.CreateEngine(w);
			e.Apply(SeasonalTheme.Christmas);
			SeasonalOriginal o = w.LoadState().Original!;
			AppSettings d = new AppSettings();
			bool fromDefaults = o.StartBgMode == d.StartBgMode && o.StartPattern == d.StartPattern && o.StartBgColor == d.StartBgColor && o.StartAccentColor == d.StartAccentColor && o.StartBgCustomPath == d.StartBgCustomPath;
			e.Apply(SeasonalTheme.Normal);
			AppSettings s = w.LoadSettings();
			bool restored = s.StartBgMode == d.StartBgMode && s.StartPattern == d.StartPattern && s.StartBgColor == d.StartBgColor && s.SeasonalActive == "";
			Check("lost-snapshot-guard", fromDefaults && restored, $"original={o.StartBgMode}/{o.StartPattern}/{o.StartBgColor} after={s.StartBgMode}/{s.StartPattern}");
		}
		// state file round trip
		{
			SeasonalStateFile st = new SeasonalStateFile
			{
				Phase = SeasonalTheme.PhaseApplying,
				Source = SeasonalTheme.Halloween,
				Target = SeasonalTheme.Christmas,
				ActiveSinceUtc = new DateTime(2026, 10, 1, 8, 0, 0, DateTimeKind.Utc),
				LastResult = "applied halloween",
				LastError = "none",
				UpdatedUtc = new DateTime(2026, 10, 9, 8, 0, 0, DateTimeKind.Utc),
				Original = new SeasonalOriginal
				{
					CapturedUtc = new DateTime(2026, 10, 1, 7, 59, 0, DateTimeKind.Utc),
					StartBgMode = "custom",
					StartBgColor = "#FFA61E5A",
					StartBgCustomPath = "C:\\Pictures\\mine.jpg",
					StartPattern = "waves",
					StartAccentColor = "",
					Wallpaper = new DesktopWallpaperSnapshot
					{
						Api = "com",
						Monitors = new List<MonitorWallpaper> { new MonitorWallpaper { MonitorId = "MON1", Path = "C:\\a.jpg" }, new MonitorWallpaper { MonitorId = "MON2", Path = "" } },
						Position = 3,
						BackgroundColor = 0x102030,
						RegWallPaper = "C:\\a.jpg",
						RegWallpaperStyle = "6",
						RegTileWallpaper = "0",
						History = new List<string?> { "C:\\a.jpg", null, "C:\\b.jpg", null, null },
						AppliedPath = "X:\\seasonal\\halloween-v1-1920x1080.png"
					}
				}
			};
			string json = JsonSerializer.Serialize(st, SeasonalTheme.Json);
			SeasonalStateFile back = JsonSerializer.Deserialize<SeasonalStateFile>(json, SeasonalTheme.Json)!;
			string again = JsonSerializer.Serialize(back, SeasonalTheme.Json);
			Check("state-json-roundtrip", json == again && SeasonalTheme.ValidateStateJson(json), json == again ? "" : again);
		}
		// Normal is on and the user picked a design that equals a preset's values: those are still the user's own
		{
			FakeSeasonalWorld w = new FakeSeasonalWorld();
			w.SetSettings(delegate(AppSettings x)
			{
				x.StartBgMode = "pattern";
				x.StartPattern = "pumpkins";
				x.StartBgColor = "#FF22103A";
				x.StartAccentColor = "#FFE0620B";
			});
			string original = w.World();
			SeasonalEngine e = SeasonalTheme.CreateEngine(w);
			e.Apply(SeasonalTheme.Christmas);
			SeasonalOriginal o = w.PeekState().Original!;
			bool captured = o.StartPattern == "pumpkins" && o.StartBgCustomPath == "C:\\Pictures\\mine.jpg";
			e.Apply(SeasonalTheme.Normal);
			Check("matching-design-is-captured", captured && w.World() == original, $"captured={captured} final={w.World()}");
		}
		// Normal is never blocked by a season that cannot be applied again
		{
			FakeSeasonalWorld w = Halloweened(out SeasonalEngine e);
			string original = new FakeSeasonalWorld().World();
			w.FailSetWallpaper = true;
			SeasonalResult failed = e.Apply(SeasonalTheme.Christmas);
			string phase = w.PeekState().Phase;
			SeasonalResult normal = e.Apply(SeasonalTheme.Normal);
			SeasonalStateFile st = w.PeekState();
			bool ok = !failed.Success && phase != SeasonalTheme.PhaseIdle && normal.Success && w.World() == original && st.Phase == SeasonalTheme.PhaseIdle && st.Original == null;
			Check("normal-not-blocked-by-failed-switch", ok, $"failed={failed.Message} phase={phase} normal={normal.Message} final={w.World()}");
		}
		// the active season re-applies its whole look after a tweak, over the same restore point
		{
			FakeSeasonalWorld w = Halloweened(out SeasonalEngine e);
			string origJson = w.OriginalJson(blankApplied: false);
			w.SetSettings(x => x.StartPattern = "bats");
			SeasonalResult r = e.Apply(SeasonalTheme.Halloween);
			bool restyled = r.Success && r.Message == "Halloween is on." && w.Peek().StartPattern == "pumpkins" && w.OriginalJson(blankApplied: false) == origJson;
			w.Monitors[1].Path = "D:\\other.jpg";
			SeasonalResult r2 = e.Apply(SeasonalTheme.Halloween);
			bool repainted = r2.Success && w.Monitors.All(m => w.IsSeasonalFile(m.Path)) && w.PeekState().Original!.Wallpaper!.Monitors[1].Path == "D:\\other.jpg";
			Check("active-season-reapplies-after-tweak", restyled && repainted, $"restyled={restyled} ({r.Message}) repainted={repainted} ({r2.Message})");
		}
		// settings replaced outside a seasonal change (a profile import) while a season was on: kept on recovery
		{
			FakeSeasonalWorld w = Halloweened(out SeasonalEngine e);
			w.SetSettings(delegate(AppSettings x)
			{
				x.StartBgMode = "custom";
				x.StartBgCustomPath = "C:\\Pictures\\imported.jpg";
				x.StartBgColor = "#FF112233";
				x.StartPattern = "dots";
				x.StartAccentColor = "#FF445566";
				x.SeasonalActive = "";
			});
			SeasonalTheme.CreateEngine(w).Recover();
			AppSettings s = w.Peek();
			SeasonalStateFile st = w.PeekState();
			bool ok = s.StartBgCustomPath == "C:\\Pictures\\imported.jpg" && s.StartPattern == "dots" && s.StartAccentColor == "#FF445566"
				&& w.Monitors[0].Path == "C:\\Pictures\\desk1.jpg" && w.Monitors[1].Path == "C:\\Pictures\\desk2.jpg" && st.Original == null && st.Phase == SeasonalTheme.PhaseIdle;
			Check("outside-settings-change-kept", ok, w.World());
		}
		// a lost restore point: the marker stays, and Normal still leaves the seasonal look (to the defaults)
		{
			FakeSeasonalWorld w = new FakeSeasonalWorld();
			w.SetSettings(delegate(AppSettings x)
			{
				x.StartBgMode = "pattern";
				x.StartPattern = "pumpkins";
				x.StartBgColor = "#FF22103A";
				x.StartAccentColor = "#FFE0620B";
				x.SeasonalActive = SeasonalTheme.Halloween;
			});
			SeasonalEngine e = SeasonalTheme.CreateEngine(w);
			e.Recover();
			bool markerKept = w.Peek().SeasonalActive == SeasonalTheme.Halloween;
			SeasonalResult r = e.Apply(SeasonalTheme.Normal);
			AppSettings s = w.Peek();
			AppSettings d = new AppSettings();
			bool ok = markerKept && r.Success && s.SeasonalActive == "" && s.StartPattern == d.StartPattern && s.StartBgMode == d.StartBgMode && r.Message.Contains("could not be recovered");
			Check("lost-restore-point-normal", ok, $"markerKept={markerKept} msg={r.Message} after={s.StartBgMode}/{s.StartPattern}");
		}
		// an unreadable restore point or unreadable settings: nothing is written
		{
			FakeSeasonalWorld w = Halloweened(out SeasonalEngine e);
			string before = w.World() + "|" + w.StateJson;
			w.StateUnreadable = true;
			SeasonalResult a = e.Apply(SeasonalTheme.Christmas);
			SeasonalResult b = e.Apply(SeasonalTheme.Normal);
			SeasonalResult c = e.Recover();
			w.StateUnreadable = false;
			bool stateOk = !a.Success && !b.Success && !c.Success && w.Writes == 0 && before == w.World() + "|" + w.StateJson && a.Message == "Could not read the restore point, so nothing was changed.";
			w.SettingsUnreadable = true;
			SeasonalResult d2 = e.Apply(SeasonalTheme.Normal);
			SeasonalResult r2 = e.Recover();
			w.SettingsUnreadable = false;
			bool settingsOk = !d2.Success && !r2.Success && w.Writes == 0 && before == w.World() + "|" + w.StateJson && d2.Message == "Could not read the launcher settings, so nothing was changed.";
			Check("unreadable-state-or-settings-refused", stateOk && settingsOk, $"state={stateOk} ({a.Message}) settings={settingsOk} ({d2.Message}) writes={w.Writes}");
		}
	}

	private static void CrashRecovery(string name, bool fromHalloween, string target)
	{
		FakeSeasonalWorld probe;
		SeasonalEngine e;
		if (fromHalloween)
		{
			probe = Halloweened(out e);
		}
		else
		{
			probe = new FakeSeasonalWorld();
			e = SeasonalTheme.CreateEngine(probe);
		}
		string sourceWorld = probe.World();
		string sourceOriginal = probe.OriginalJson(blankApplied: false);
		e.Apply(target);
		string targetWorld = probe.World();
		int total = probe.Writes;
		List<string> failures = new List<string>();
		for (int n = 1; n <= total; n++)
		{
			FakeSeasonalWorld w;
			if (fromHalloween)
			{
				w = Halloweened(out _);
			}
			else
			{
				w = new FakeSeasonalWorld();
			}
			w.CrashAfter = n;
			bool crashed = false;
			try
			{
				SeasonalTheme.CreateEngine(w).Apply(target);
			}
			catch (SimulatedCrash)
			{
				crashed = true;
			}
			w.CrashAfter = -1;
			string phase = w.LoadState().Phase;
			SeasonalTheme.CreateEngine(w).Recover();
			// An apply that did not commit rolls back to its source; a restore always rolls forward to normal.
			bool toTarget = phase == SeasonalTheme.PhaseIdle || target == SeasonalTheme.Normal;
			string expect = toTarget ? targetWorld : sourceWorld;
			SeasonalStateFile st = w.LoadState();
			bool ok = crashed && w.World() == expect && st.Phase == SeasonalTheme.PhaseIdle;
			if (ok && !toTarget && fromHalloween)
			{
				ok = w.OriginalJson(blankApplied: false) == sourceOriginal;
			}
			if (!ok)
			{
				failures.Add($"n={n} crashed={crashed} phase={phase} got={w.World()} want={expect} state={st.Phase}");
			}
		}
		Check("crash-recovery-" + name, failures.Count == 0 && total >= 3, failures.Count == 0 ? $"{total} crash points" : string.Join(" ; ", failures));
		Metrics["CrashPoints-" + name] = total;
	}

	// ===================================================================== art

	private static void ArtChecks(string outDir)
	{
		List<string> frozenFail = new List<string>();
		List<string> edgeFail = new List<string>();
		List<string> scaleFail = new List<string>();
		List<string> headerFail = new List<string>();
		Dictionary<string, double> stdevs = new Dictionary<string, double>();
		foreach (string id in SeasonalArt.PatternIds)
		{
			StartBackgroundFactory.Preset p = Array.Find(StartBackgroundFactory.Presets, x => x.Pattern == id)!;
			Color bc = StartBackgroundFactory.Parse(p.Base, Colors.Gray);
			Color ac = StartBackgroundFactory.Parse(p.Accent, Colors.Gray);
			Brush b = StartBackgroundFactory.Build(bc, ac, id);
			double s = SeasonalArt.TileSize(id);
			if (!(b is DrawingBrush db && db.IsFrozen && db.Viewbox.Width == s && db.Viewport.Width == s && db.TileMode == TileMode.Tile))
			{
				frozenFail.Add(id);
			}
			Brush thumb = StartBackgroundFactory.Build(bc, ac, id, 0.4);
			if (!(thumb is DrawingBrush tb && Math.Abs(tb.Viewport.Width - s * 0.4) < 1e-9))
			{
				scaleFail.Add(id);
			}
			int n = (int)s;
			// One whole tile: the header offset of the signature patterns is taken out again for the edge check.
			Brush tileOnly = b;
			if (b is DrawingBrush offsetBrush && offsetBrush.Viewport.Y != 0.0)
			{
				DrawingBrush aligned = offsetBrush.Clone();
				aligned.Viewport = new Rect(0.0, 0.0, s, s);
				aligned.Freeze();
				tileOnly = aligned;
			}
			if (Math.Abs(((DrawingBrush)b).Viewport.Y - SeasonalArt.TileOffsetY(id)) > 1e-9)
			{
				frozenFail.Add(id + ":offset");
			}
			DrawingVisual dv = new DrawingVisual();
			using (DrawingContext dc = dv.RenderOpen())
			{
				dc.DrawRectangle(tileOnly, null, new Rect(0, 0, n, n));
			}
			byte[] px = Pixels(dv, n, n);
			double stdev = LuminanceStdev(px);
			stdevs[id] = Math.Round(stdev, 2);
			if (stdev <= 4.0)
			{
				frozenFail.Add(id + ":flat");
			}
			int worst = 0;
			for (int y = 0; y < n; y++)
			{
				for (int x = 0; x < n; x++)
				{
					if (x != 0 && y != 0 && x != n - 1 && y != n - 1)
					{
						continue;
					}
					int i = (y * n + x) * 4;
					worst = Math.Max(worst, Math.Max(Math.Abs(px[i] - bc.B), Math.Max(Math.Abs(px[i + 1] - bc.G), Math.Abs(px[i + 2] - bc.R))));
				}
			}
			if (worst > 2)
			{
				edgeFail.Add($"{id}:{worst}");
			}
			if (SeasonalArt.TileOffsetY(id) > 0.0)
			{
				// the band that lines up with the Start header must hold nothing but the base colour
				int band = 0;
				for (int y = 0; y < n; y++)
				{
					if (y >= 44 && y < 408)
					{
						continue;
					}
					for (int x = 0; x < n; x++)
					{
						int i = (y * n + x) * 4;
						band = Math.Max(band, Math.Max(Math.Abs(px[i] - bc.B), Math.Max(Math.Abs(px[i + 1] - bc.G), Math.Abs(px[i + 2] - bc.R))));
					}
				}
				if (band > 2)
				{
					headerFail.Add($"{id}:{band}");
				}
			}
		}
		Check("pattern-header-band-clear", headerFail.Count == 0, string.Join(",", headerFail));
		Metrics["PatternLuminanceStdev"] = stdevs;
		Check("patterns-build-frozen", frozenFail.Count == 0, string.Join(",", frozenFail));
		Check("pattern-edges-clean", edgeFail.Count == 0, string.Join(",", edgeFail));
		Check("pattern-thumbnail-scale", scaleFail.Count == 0, string.Join(",", scaleFail));

		// wallpapers: deterministic, on-palette, safe-area variants
		bool deterministic = true;
		Dictionary<string, string> avg = new Dictionary<string, string>();
		Dictionary<string, string> taskbarZone = new Dictionary<string, string>();
		bool taskbarZoneOk = true;
		(double H, double S, double L) halloween = default;
		(double H, double S, double L) christmas = default;
		foreach (string art in new string[2] { "halloween", "christmas" })
		{
			BitmapSource a = SeasonalArt.RenderWallpaper(art, 1920, 1080);
			BitmapSource b = SeasonalArt.RenderWallpaper(art, 1920, 1080);
			byte[] pa = Pixels(a);
			byte[] pb = Pixels(b);
			if (!SHA256.HashData(pa).AsSpan().SequenceEqual(SHA256.HashData(pb)))
			{
				deterministic = false;
			}
			SavePng(a, Path.Combine(outDir, $"wallpaper-{art}.png"));
			SavePng(SeasonalArt.RenderWallpaper(art, 1280, 1024), Path.Combine(outDir, $"wallpaper-{art}-1280x1024.png"));
			if (art == "halloween")
			{
				SavePng(SeasonalArt.RenderWallpaper(art, 2560, 1080), Path.Combine(outDir, $"wallpaper-{art}-2560x1080.png"));
			}
			(double r, double g, double bl) = Average(pa);
			(double h, double sat, double l) = Hsl(r, g, bl);
			avg[art] = $"rgb({r:F0},{g:F0},{bl:F0}) hue={h:F0} sat={sat:F2} light={l:F2}";
			// the strip under the taskbar (bottom 40 px) must be dark, so the translucent taskbar keeps its colour
			byte[] strip = pa.AsSpan(1040 * 1920 * 4).ToArray();
			(double sr, double sg, double sb) = Average(strip);
			double stripLight = Hsl(sr, sg, sb).L;
			taskbarZone[art] = $"rgb({sr:F0},{sg:F0},{sb:F0}) light={stripLight:F2}";
			if (stripLight > 0.22)
			{
				taskbarZoneOk = false;
			}
			if (art == "halloween")
			{
				halloween = (h, sat, l);
			}
			else
			{
				christmas = (h, sat, l);
			}
		}
		Metrics["WallpaperAverage"] = avg;
		Metrics["WallpaperTaskbarZone"] = taskbarZone;
		Check("wallpaper-taskbar-zone-dark", taskbarZoneOk, string.Join(" ; ", taskbarZone.Select(kv => kv.Key + "=" + kv.Value)));
		Check("wallpaper-deterministic", deterministic);
		Check("wallpaper-hue", halloween.H >= 255 && halloween.H <= 300 && halloween.L < 0.25 && christmas.H >= 130 && christmas.H <= 170, string.Join(" ; ", avg.Values));
		List<string> contrast = new List<string>();
		bool contrastOk = true;
		foreach (SeasonPreset p in SeasonalTheme.Presets)
		{
			double ratio = 1.05 / (ColorMath.RelLuminance(SeasonalArt.Hex(p.Accent)) + 0.05);
			contrast.Add($"{p.Id}={ratio:F2}");
			contrastOk &= ratio >= 3.0;
		}
		Check("accent-contrast", contrastOk, string.Join(",", contrast));

		RenderPatterns(Path.Combine(outDir, "patterns.png"));
		RenderThumbs(Path.Combine(outDir, "designs-thumbs.png"));
	}

	private static void RenderPatterns(string path)
	{
		const int cellW = 480;
		const int cellH = 270;
		string[] ids = SeasonalArt.PatternIds;
		DrawingVisual dv = new DrawingVisual();
		using (DrawingContext dc = dv.RenderOpen())
		{
			for (int i = 0; i < ids.Length; i++)
			{
				StartBackgroundFactory.Preset p = Array.Find(StartBackgroundFactory.Presets, x => x.Pattern == ids[i])!;
				Brush b = StartBackgroundFactory.Build(StartBackgroundFactory.Parse(p.Base, Colors.Gray), StartBackgroundFactory.Parse(p.Accent, Colors.Gray), p.Pattern);
				Rect r = new Rect((i % 3) * cellW, (i / 3) * cellH, cellW, cellH);
				dc.PushTransform(new TranslateTransform(r.X, r.Y));
				dc.DrawRectangle(b, null, new Rect(0, 0, cellW, cellH));
				dc.Pop();
				Label(dc, p.Pattern, r.X + 8, r.Y + 6, 14);
			}
		}
		SavePng(Render(dv, cellW * 3, cellH * 2), path);
	}

	// The Personalize thumbnails (80x50 at scale 0.4) and the seasonal chips, drawn at 2x for inspection.
	private static void RenderThumbs(string path)
	{
		DrawingVisual dv = new DrawingVisual();
		using (DrawingContext dc = dv.RenderOpen())
		{
			dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(26, 26, 26)), null, new Rect(0, 0, 360, 150));
			int i = 0;
			foreach (string id in SeasonalArt.PatternIds)
			{
				StartBackgroundFactory.Preset p = Array.Find(StartBackgroundFactory.Presets, x => x.Pattern == id)!;
				Brush b = StartBackgroundFactory.Build(StartBackgroundFactory.Parse(p.Base, Colors.Gray), StartBackgroundFactory.Parse(p.Accent, Colors.Gray), id, 0.4);
				double x = 12 + (i % 4) * 87;
				double y = 12 + (i / 4) * 57;
				dc.PushTransform(new TranslateTransform(x, y));
				dc.DrawRectangle(b, new Pen(new SolidColorBrush(Color.FromArgb(68, 255, 255, 255)), 1), new Rect(0, 0, 80, 50));
				dc.Pop();
				i++;
			}
		}
		SavePng(Render(dv, 360, 150, 2.0), path);
	}

	// ===================================================================== live wiring (overrides) and renders

	private static AppSettings SeasonSettings(AppSettings real, SeasonPreset? p, string colorMode)
	{
		AppSettings s = real.Clone();
		if (p != null)
		{
			s.StartBgMode = "pattern";
			s.StartPattern = p.Pattern;
			s.StartBgColor = p.Base;
			s.StartAccentColor = p.Accent;
			s.SeasonalActive = p.Id;
		}
		else
		{
			s.SeasonalActive = "";
		}
		s.SeasonalAmbient = true;
		s.TaskbarColorMode = colorMode;
		s.TaskbarTransparent = false;
		return s;
	}

	// The user's own settings with no season.
	private static AppSettings NormalSettings(AppSettings real)
	{
		AppSettings s = real.Clone();
		s.SeasonalActive = "";
		return s;
	}

	// The user's own settings (taskbar included) with a preset's Start look on.
	private static AppSettings UserSeasonSettings(AppSettings real, SeasonPreset p)
	{
		AppSettings s = real.Clone();
		s.StartBgMode = "pattern";
		s.StartPattern = p.Pattern;
		s.StartBgColor = p.Base;
		s.StartAccentColor = p.Accent;
		s.SeasonalActive = p.Id;
		return s;
	}

	private static (double R, double G, double B) RegionAverage(string path, int x, int y, int w, int h)
	{
		CroppedBitmap crop = new CroppedBitmap(LoadPng(path), new Int32Rect(x, y, w, h));
		return Average(Pixels(crop));
	}

	private static (int Diff, int Total) PixelDiff(string a, string b)
	{
		byte[] pa = Pixels(LoadPng(a));
		byte[] pb = Pixels(LoadPng(b));
		if (pa.Length != pb.Length)
		{
			return (-1, pa.Length / 4);
		}
		int diff = 0;
		for (int i = 0; i < pa.Length; i += 4)
		{
			if (pa[i] != pb[i] || pa[i + 1] != pb[i + 1] || pa[i + 2] != pb[i + 2] || pa[i + 3] != pb[i + 3])
			{
				diff++;
			}
		}
		return (diff, pa.Length / 4);
	}

	// The taskbar as the user has it with no season: their colour mode over their own desktop picture (read only).
	private static void RenderTaskbarNormal(string path, AppSettings real)
	{
		const int w = 1366;
		const int h = 40;
		string? wallpaper = TaskbarTheme.WallpaperPath();
		BitmapSource? wp = null;
		try
		{
			if (wallpaper != null && File.Exists(wallpaper))
			{
				wp = LoadPng(wallpaper);
			}
		}
		catch
		{
		}
		DrawingVisual dv = new DrawingVisual();
		using (DrawingContext dc = dv.RenderOpen())
		{
			dc.DrawRectangle(Brushes.Black, null, new Rect(0, 0, w, h + 30));
			Label(dc, $"Normal (your settings: {real.TaskbarColorMode}{(real.TaskbarTransparent ? ", transparent" : "")})", 8, 6, 13);
			if (wp != null)
			{
				double scale = w / (double)wp.PixelWidth;
				dc.PushClip(new RectangleGeometry(new Rect(0, 30, w, h)));
				dc.DrawImage(wp, new Rect(0, 30 + h - wp.PixelHeight * scale, w, wp.PixelHeight * scale));
				dc.Pop();
			}
			dc.DrawRectangle(TaskbarTheme.CurrentBackground(), null, new Rect(0, 30, w, h));
			Label(dc, "❖  Taskbar  9:41 PM", 12, 40, 13);
		}
		SavePng(Render(dv, w, h + 30), path);
	}

	private static void LiveChecks(string outDir, AppSettings real)
	{
		StartScreen start = new StartScreen(transitionDiagnostics: true)
		{
			Left = -6000.0,
			Top = -6000.0,
			ShowActivated = false,
			Topmost = false
		};
		start.QaVaryTileColors();
		List<string> accentFail = new List<string>();
		List<string> overlayFail = new List<string>();
		Dictionary<string, object> palettes = new Dictionary<string, object>();
		Dictionary<string, string> acSeason = new Dictionary<string, string>();
		bool acSeasonOk = true;
		// The previews of what Normal brings back show the user's own Start look and desktop picture (read only).
		TaskbarTheme.DiagnosticsWallpaperPath = null;
		string? ownWallpaper = TaskbarTheme.WallpaperPath();
		SeasonalTheme.DiagnosticsOriginal = new SeasonalOriginal
		{
			StartBgMode = real.StartBgMode ?? "",
			StartBgColor = real.StartBgColor ?? "",
			StartBgCustomPath = real.StartBgCustomPath ?? "",
			StartPattern = real.StartPattern ?? "",
			StartAccentColor = real.StartAccentColor ?? "",
			Wallpaper = ownWallpaper == null ? null : new DesktopWallpaperSnapshot
			{
				Monitors = new List<MonitorWallpaper> { new MonitorWallpaper { MonitorId = "QA", Path = ownWallpaper } }
			}
		};
		try
		{
			// The user's own look first, so the Normal render at the end can be compared with it.
			SettingsStore.DiagnosticsOverride = NormalSettings(real);
			SeasonalTheme.DiagnosticsOwnsWallpaper = false;
			TaskbarTheme.DiagnosticsWallpaperPath = null;
			TaskbarTheme.Invalidate();
			start.QaRenderSeason(Path.Combine(outDir, "start-before.png"), 1920.0, 1080.0, withAmbient: true);
			bool beforeSettled = start.QaBackgroundSettled;

			foreach (SeasonPreset p in SeasonalTheme.Presets)
			{
				Color accent = SeasonalArt.Hex(p.Accent);
				string wallpaper = Path.Combine(outDir, $"wallpaper-{p.Art}.png");
				TaskbarTheme.DiagnosticsWallpaperPath = wallpaper;
				SeasonalTheme.DiagnosticsOwnsWallpaper = true;
				TaskbarTheme.Invalidate();

				// accent flows
				SettingsStore.DiagnosticsOverride = SeasonSettings(real, p, "Start");
				Color startAccent = StartAccent.Color();
				Color source = TaskbarTheme.SourceAccent();
				Color startChrome = TaskbarTheme.ChromeColor();
				Color tone = SettingsPane.AccentToneColor(0.34);
				double hueDiff = HueDistance(Hsl(accent.R, accent.G, accent.B).H, Hsl(tone.R, tone.G, tone.B).H);
				if (startAccent != accent || source != accent || hueDiff > 3.0)
				{
					accentFail.Add($"{p.Id}: start={startAccent} source={source} toneHueDiff={hueDiff:F1}");
				}

				// taskbar overlay
				SettingsStore.DiagnosticsOverride = SeasonSettings(real, p, "Wallpaper");
				SeasonalTheme.DiagnosticsOwnsWallpaper = false;
				Color overlay = TaskbarTheme.ChromeColor();
				Color overlaySource = TaskbarTheme.SourceAccent();
				SeasonalTheme.DiagnosticsOwnsWallpaper = true;
				Color owned = TaskbarTheme.ChromeColor();
				Color ownedSource = TaskbarTheme.SourceAccent();
				Color fromWallpaper = TaskbarTheme.FromWallpaper();
				SettingsStore.DiagnosticsOverride = SeasonSettings(real, null, "Wallpaper");
				SeasonalTheme.DiagnosticsOwnsWallpaper = false;
				Color normal = TaskbarTheme.ChromeColor();
				if (overlay != startChrome || owned != fromWallpaper || normal != fromWallpaper || overlaySource != accent)
				{
					overlayFail.Add($"{p.Id}: overlay={overlay} want={startChrome} owned={owned} wallpaper={fromWallpaper} normal={normal}");
				}

				// renders with the season fully on (wallpaper owned, Wallpaper colour mode)
				SettingsStore.DiagnosticsOverride = SeasonSettings(real, p, "Wallpaper");
				SeasonalTheme.DiagnosticsOwnsWallpaper = true;
				TaskbarTheme.Invalidate();
				start.QaRenderSeason(Path.Combine(outDir, $"start-{p.Id}.png"), 1920.0, 1080.0, withAmbient: true);
				start.QaRenderPersonalize(Path.Combine(outDir, $"personalize-{p.Id}.png"));
				ActionCenter.QaRenderMetro(Path.Combine(outDir, $"actioncenter-{p.Id}.png"), 468.0, 860.0, compact: false, withNotifications: false, currentTheme: true);
				{
					// an empty part of the panel keeps the season's base hue and some colour (no grey mix of opposite hues)
					(double ar, double ag, double ab) = RegionAverage(Path.Combine(outDir, $"actioncenter-{p.Id}.png"), 20, 300, 420, 80);
					(double ah, double asat, double al) = Hsl(ar, ag, ab);
					Color bcol = SeasonalArt.Hex(p.Base);
					double bh = Hsl(bcol.R, bcol.G, bcol.B).H;
					bool keeps = HueDistance(ah, bh) <= 20.0 && asat >= 0.30;
					acSeason[p.Id] = $"rgb({ar:F0},{ag:F0},{ab:F0}) hue={ah:F0} (base {bh:F0}) sat={asat:F2}";
					acSeasonOk &= keeps;
				}
				RenderTaskbar(Path.Combine(outDir, $"taskbar-{p.Id}.png"), real, p, wallpaper);
				RenderGlass(Path.Combine(outDir, $"win7glass-{p.Id}.png"), real, p);
				(double r, double g, double b) = Average(Pixels(LoadPng(wallpaper)));
				palettes[p.Id] = new
				{
					StartAccent = Hex(StartAccent.Color()),
					Tint06 = Hex(StartAccent.Tint(0.6)),
					AccentTone034 = Hex(SettingsPane.AccentToneColor(0.34)),
					ChromeWallpaperMode = Hex(owned),
					ChromeStartModeOrOverlay = Hex(startChrome),
					SourceAccentWallpaperMode = Hex(ownedSource),
					SourceAccentStartMode = Hex(source),
					WallpaperAverage = $"#{(byte)r:X2}{(byte)g:X2}{(byte)b:X2}"
				};
				File.WriteAllText(Path.Combine(outDir, $"palette-{p.Id}.txt"), JsonSerializer.Serialize(palettes[p.Id], new JsonSerializerOptions { WriteIndented = true }));
				// PC Settings shows the user's own taskbar settings, with the season on.
				SettingsStore.DiagnosticsOverride = UserSeasonSettings(real, p);
				SeasonalTheme.DiagnosticsOwnsWallpaper = true;
				TaskbarTheme.Invalidate();
				PcSettingsWindow pc = new PcSettingsWindow(start)
				{
					Left = -6000.0,
					Top = -6000.0
				};
				if (p.Id == SeasonalTheme.Halloween)
				{
					pc.QaRenderTall(Path.Combine(outDir, $"pcsettings-seasonal-{p.Id}.png"), "Launcher");
				}
				else
				{
					pc.QaRenderTall(Path.Combine(outDir, $"pcsettings-startscreen-{p.Id}.png"), "Start screen");
				}
				pc.Close();
			}
			// Normal again: the user's own look must come back exactly as it was rendered before any season.
			SettingsStore.DiagnosticsOverride = NormalSettings(real);
			SeasonalTheme.DiagnosticsOwnsWallpaper = false;
			TaskbarTheme.DiagnosticsWallpaperPath = null;
			TaskbarTheme.Invalidate();
			start.QaRenderSeason(Path.Combine(outDir, "start-normal.png"), 1920.0, 1080.0, withAmbient: true);
			bool normalSettled = start.QaBackgroundSettled;
			(int diff, int total) = PixelDiff(Path.Combine(outDir, "start-before.png"), Path.Combine(outDir, "start-normal.png"));
			Check("normal-look-matches-before", beforeSettled && normalSettled && diff == 0, $"differing pixels={diff}/{total} settled={beforeSettled}/{normalSettled} mode={real.StartBgMode}");
			Metrics["NormalVsBeforeDiffPixels"] = diff;
			ActionCenter.QaRenderMetro(Path.Combine(outDir, "actioncenter-normal.png"), 468.0, 860.0, compact: false, withNotifications: false, currentTheme: true);
			RenderTaskbarNormal(Path.Combine(outDir, "taskbar-normal.png"), real);

			AmbientChecks(outDir, start, real);
		}
		finally
		{
			try
			{
				start.Close();
			}
			catch
			{
			}
		}
		Metrics["Palettes"] = palettes;
		Metrics["ActionCenterSeason"] = acSeason;
		Check("actioncenter-keeps-season", acSeasonOk && acSeason.Count == SeasonalTheme.Presets.Length, string.Join(" ; ", acSeason.Select(kv => kv.Key + "=" + kv.Value)));
		Check("accent-flows", accentFail.Count == 0, string.Join(" ; ", accentFail));
		Check("taskbar-overlay", overlayFail.Count == 0, string.Join(" ; ", overlayFail));
	}

	private static void AmbientChecks(string outDir, StartScreen start, AppSettings real)
	{
		foreach (SeasonPreset p in SeasonalTheme.Presets)
		{
			Color accent = SeasonalArt.Hex(p.Accent);
			Border host = new Border
			{
				Width = 800.0,
				Height = 450.0,
				Background = StartBackgroundFactory.Build(SeasonalArt.Hex(p.Base), accent, p.Pattern)
			};
			SeasonalAmbientLayer layer = new SeasonalAmbientLayer();
			host.Child = layer;
			host.Measure(new Size(800, 450));
			host.Arrange(new Rect(0, 0, 800, 450));
			host.UpdateLayout();
			layer.Show(p.Ambient, accent, MotionMode.Off);
			host.UpdateLayout();
			var off = layer.QaState();
			RenderTargetBitmap rtb = new RenderTargetBitmap(800, 450, 96, 96, PixelFormats.Pbgra32);
			rtb.Render(host);
			SavePng(rtb, Path.Combine(outDir, $"ambient-{p.Ambient}.png"));
			layer.Show(p.Ambient, accent, MotionMode.Authentic);
			var authentic = layer.QaState();
			layer.Show(p.Ambient, accent, MotionMode.Reduced);
			var reduced = layer.QaState();
			int reducedSway = layer.QaSwayClocks();
			layer.Hide();
			var hidden = layer.QaState();
			bool retired = layer.QaRetiredStillAnimated();
			Check("ambient-off-static-" + p.Ambient, off.Clocks == 0 && off.NearVisible, $"clocks={off.Clocks}");
			Check("ambient-authentic-30fps-" + p.Ambient, authentic.Clocks > 0 && authentic.DesiredFps.All(f => f == 30) && authentic.NearVisible, $"clocks={authentic.Clocks} fps={string.Join(",", authentic.DesiredFps)} near={authentic.NearVisible}");
			Check("ambient-reduced-far-only-" + p.Ambient, reduced.Clocks > 0 && !reduced.NearVisible && reduced.DesiredFps.All(f => f == 20) && reducedSway == 0, $"clocks={reduced.Clocks} fps={string.Join(",", reduced.DesiredFps)} near={reduced.NearVisible} sway={reducedSway}");
			Check("ambient-stop-releases-" + p.Ambient, hidden.Clocks == 0 && !retired && layer.Children.Count == 0, $"clocks={hidden.Clocks} retired={retired}");
		}
		// The Start screen releases its layer when it hides.
		SettingsStore.DiagnosticsOverride = SeasonSettings(real, SeasonalTheme.Presets[1], "Wallpaper");
		start.QaRenderSeason(Path.Combine(outDir, "start-ambient-probe.png"), 1366.0, 768.0, withAmbient: true);
		bool attached = start.QaBgLayerHasChild && start.QaAmbient?.IsShown == true;
		start.QaHideForAmbientCheck();
		bool released = !start.QaBgLayerHasChild && start.QaAmbient?.IsShown != true;
		Check("ambient-stop-releases-start", attached && released, $"attached={attached} released={released}");
		try
		{
			File.Delete(Path.Combine(outDir, "start-ambient-probe.png"));
		}
		catch
		{
		}
	}

	// Bottom strips: the season's wallpaper behind the taskbar colour in Wallpaper mode (wallpaper owned), the runtime
	// overlay (season on, wallpaper not owned) and Start mode.
	private static void RenderTaskbar(string path, AppSettings real, SeasonPreset p, string wallpaper)
	{
		BitmapSource wp = LoadPng(wallpaper);
		const int w = 1366;
		const int h = 40;
		DrawingVisual dv = new DrawingVisual();
		using (DrawingContext dc = dv.RenderOpen())
		{
			(string label, string mode, bool owns)[] rows = new (string, string, bool)[3] { ("Wallpaper mode", "Wallpaper", true), ("Overlay (no seasonal wallpaper)", "Wallpaper", false), ("Start mode", "Start", true) };
			for (int i = 0; i < rows.Length; i++)
			{
				SettingsStore.DiagnosticsOverride = SeasonSettings(real, p, rows[i].mode);
				SeasonalTheme.DiagnosticsOwnsWallpaper = rows[i].owns;
				TaskbarTheme.Invalidate();
				double y = i * (h + 30);
				dc.DrawRectangle(Brushes.Black, null, new Rect(0, y, w, h + 30));
				Label(dc, rows[i].label, 8, y + 6, 13);
				dc.PushClip(new RectangleGeometry(new Rect(0, y + 30, w, h)));
				dc.DrawImage(wp, new Rect(0, y + 30 - (wp.PixelHeight - h), wp.PixelWidth, wp.PixelHeight));
				dc.Pop();
				dc.DrawRectangle(TaskbarTheme.CurrentBackground(), null, new Rect(0, y + 30, w, h));
				Label(dc, "\u2756  Taskbar  9:41 PM", 12, y + 40, 13);
			}
		}
		SeasonalTheme.DiagnosticsOwnsWallpaper = true;
		SavePng(Render(dv, w, 3 * (h + 30)), path);
	}

	private static void RenderGlass(string path, AppSettings real, SeasonPreset p)
	{
		DrawingVisual dv = new DrawingVisual();
		using (DrawingContext dc = dv.RenderOpen())
		{
			dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(40, 40, 40)), null, new Rect(0, 0, 840, 300));
			int row = 0;
			foreach ((string mode, bool owns) in new (string, bool)[2] { ("Wallpaper", true), ("Start", true) })
			{
				SettingsStore.DiagnosticsOverride = SeasonSettings(real, p, mode);
				SeasonalTheme.DiagnosticsOwnsWallpaper = owns;
				TaskbarTheme.Invalidate();
				Color source = TaskbarTheme.SourceAccent();
				int col = 0;
				foreach (bool dark in new bool[2] { false, true })
				{
					Color[] stops = Win7Palette.GlassStopColors(source, dark, transparency: true, out _, out _);
					LinearGradientBrush lg = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(0, 1) };
					for (int i = 0; i < stops.Length; i++)
					{
						lg.GradientStops.Add(new GradientStop(stops[i], stops.Length == 1 ? 0 : (double)i / (stops.Length - 1)));
					}
					double x = 20 + col * 410;
					double y = 30 + row * 140;
					Label(dc, $"{mode} source {Hex(source)} {(dark ? "dark" : "light")}", x, y - 22, 12);
					dc.DrawRectangle(lg, null, new Rect(x, y, 390, 100));
					// the glossy diagonal streak of the Aero glass, and a mock list pane
					LinearGradientBrush streak = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 1) };
					streak.GradientStops.Add(new GradientStop(Color.FromArgb(0, 255, 255, 255), 0.30));
					streak.GradientStops.Add(new GradientStop(Color.FromArgb(dark ? (byte)13 : (byte)26, 255, 255, 255), 0.42));
					streak.GradientStops.Add(new GradientStop(Color.FromArgb(0, 255, 255, 255), 0.55));
					dc.DrawRectangle(streak, null, new Rect(x, y, 390, 100));
					dc.DrawRectangle(new SolidColorBrush(dark ? Win7Palette.PaneDark : Win7Palette.PaneLight), null, new Rect(x + 8, y + 8, 200, 84));
					Label(dc, "Seasonal", x + 216, y + 12, 13);
					col++;
				}
				row++;
			}
		}
		SavePng(Render(dv, 840, 300, 2.0), path);
	}

	// ===================================================================== helpers

	private sealed class Baseline
	{
		public string Hashes = "";

		public string Wallpaper = "";

		public string Registry = "";

		public string SeasonalFolder = "";

		public static Baseline Capture()
		{
			Baseline b = new Baseline();
			List<string> files = new List<string>();
			foreach (string name in new string[5] { "settings.json", "seasonal-state.json", "profile.json", "taskbar-pins.json", "shell-profile-state.json" })
			{
				files.Add(Path.Combine(Root, name));
			}
			string state = Path.Combine(Root, "state");
			if (Directory.Exists(state))
			{
				files.AddRange(Directory.GetFiles(state, "settings.*").Concat(Directory.GetFiles(state, "seasonal.*")).OrderBy(f => f, StringComparer.OrdinalIgnoreCase));
			}
			b.Hashes = string.Join(";", files.Select(f => Path.GetFileName(f) + "=" + (File.Exists(f) ? Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(f)))[..16] : "-")));
			try
			{
				DesktopWallpaperSnapshot snap = DesktopWallpaperApi.Capture(out string? skip);
				b.Wallpaper = $"{snap.Api}|{string.Join(",", snap.Monitors.Select(m => m.MonitorId + "=" + m.Path))}|pos={snap.Position}|color={snap.BackgroundColor:X6}|skip={skip}";
				b.Registry = $"{snap.RegWallPaper}|{snap.RegWallpaperStyle}|{snap.RegTileWallpaper}";
			}
			catch (Exception ex)
			{
				b.Wallpaper = "error:" + ex.Message;
			}
			b.SeasonalFolder = Directory.Exists(SeasonalArt.SeasonalDir)
				? string.Join(";", Directory.GetFiles(SeasonalArt.SeasonalDir).OrderBy(f => f).Select(f => Path.GetFileName(f) + ":" + new FileInfo(f).Length + ":" + File.GetLastWriteTimeUtc(f).Ticks))
				: "(none)";
			return b;
		}
	}

	private static void Label(DrawingContext dc, string text, double x, double y, double size)
	{
		FormattedText ft = new FormattedText(text, System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), size, Brushes.White, 1.0);
		dc.DrawText(ft, new Point(x, y));
	}

	private static BitmapSource Render(DrawingVisual dv, int w, int h, double scale = 1.0)
	{
		RenderTargetBitmap rtb = new RenderTargetBitmap((int)(w * scale), (int)(h * scale), 96.0 * scale, 96.0 * scale, PixelFormats.Pbgra32);
		rtb.Render(dv);
		return rtb;
	}

	private static byte[] Pixels(DrawingVisual dv, int w, int h)
	{
		return Pixels(Render(dv, w, h));
	}

	private static byte[] Pixels(BitmapSource src)
	{
		FormatConvertedBitmap conv = new FormatConvertedBitmap(src, PixelFormats.Bgra32, null, 0.0);
		int stride = conv.PixelWidth * 4;
		byte[] px = new byte[stride * conv.PixelHeight];
		conv.CopyPixels(px, stride, 0);
		return px;
	}

	private static BitmapSource LoadPng(string path)
	{
		BitmapImage bmp = new BitmapImage();
		bmp.BeginInit();
		bmp.UriSource = new Uri(path);
		bmp.CacheOption = BitmapCacheOption.OnLoad;
		bmp.EndInit();
		bmp.Freeze();
		return bmp;
	}

	private static (double R, double G, double B) Average(byte[] px)
	{
		double r = 0;
		double g = 0;
		double b = 0;
		int n = px.Length / 4;
		for (int i = 0; i < px.Length; i += 4)
		{
			b += px[i];
			g += px[i + 1];
			r += px[i + 2];
		}
		return (r / n, g / n, b / n);
	}

	private static double LuminanceStdev(byte[] px)
	{
		int n = px.Length / 4;
		double sum = 0;
		double sq = 0;
		for (int i = 0; i < px.Length; i += 4)
		{
			double l = 0.0722 * px[i] + 0.7152 * px[i + 1] + 0.2126 * px[i + 2];
			sum += l;
			sq += l * l;
		}
		double mean = sum / n;
		return Math.Sqrt(Math.Max(0, sq / n - mean * mean));
	}

	private static (double H, double S, double L) Hsl(double r, double g, double b)
	{
		ColorMath.RgbToHsl(r / 255.0, g / 255.0, b / 255.0, out double h, out double s, out double l);
		return (h * 360.0, s, l);
	}

	private static double HueDistance(double a, double b)
	{
		double d = Math.Abs(a - b) % 360.0;
		return d > 180.0 ? 360.0 - d : d;
	}

	private static string Hex(Color c)
	{
		return $"#{c.R:X2}{c.G:X2}{c.B:X2}";
	}

	private static void SavePng(BitmapSource bmp, string path)
	{
		PngBitmapEncoder enc = new PngBitmapEncoder();
		enc.Frames.Add(BitmapFrame.Create(bmp));
		using FileStream fs = File.Create(path);
		enc.Save(fs);
	}
}
