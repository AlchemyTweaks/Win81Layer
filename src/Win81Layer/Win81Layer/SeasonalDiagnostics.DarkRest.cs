#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;

namespace Win81Layer;

// --seasonaltest, Dark rest part: the engine against the fake world (light/dark snapshot, restore, crash recovery),
// the pure rules (preset, tones, motion, pointers), the drawn art, and every surface rendered in its Dark rest variant
// with Normal rendered before and after it and compared pixel by pixel. Read-only like the rest of the harness.
internal static partial class SeasonalDiagnostics
{
	private const string RestQuery = "QA Application 00";

	// ===================================================================== engine (fake world)

	private static void RestEngineChecks()
	{
		SeasonPreset rest = SeasonalTheme.Find(SeasonalTheme.Rest)!;
		const string lightTheme = "apps=DWord:1|sys=absent";
		const string darkTheme = "apps=DWord:0|sys=DWord:0";
		// round trip: Windows goes dark with the look, and Normal brings back exactly what was there (the missing
		// system value is deleted again)
		{
			FakeSeasonalWorld w = new FakeSeasonalWorld();
			string original = w.World();
			SeasonalEngine e = SeasonalTheme.CreateEngine(w);
			SeasonalResult on = e.Apply(SeasonalTheme.Rest);
			AppSettings s = w.Peek();
			bool during = on.Success && on.Message == "Dark rest is on." && s.SeasonalActive == SeasonalTheme.Rest && s.StartBgMode == "pattern"
				&& s.StartPattern == rest.Pattern && s.StartBgColor == rest.Base && s.StartAccentColor == rest.Accent
				&& w.Monitors.All(m => w.IsSeasonalFile(m.Path) && m.Path.Contains("rest-", StringComparison.Ordinal)) && w.ThemeText() == darkTheme
				&& w.PeekState().Original?.WindowsTheme != null;
			string duringWorld = w.World();
			w.Now = w.Now.AddHours(1);
			SeasonalResult off = e.Apply(SeasonalTheme.Normal);
			SeasonalStateFile st = w.PeekState();
			bool ok = during && off.Success && w.World() == original && w.Sys == null && st.Phase == SeasonalTheme.PhaseIdle && st.Original == null && w.Files.Count == 0
				&& off.Message == "Normal look restored.";
			Check("rest-roundtrip-normal-rest-normal", ok, $"original={original} during={duringWorld} after={w.World()} msg={on.Message}/{off.Message} files={w.Files.Count}");
			Metrics["RestRoundTripMessages"] = new[] { on.Message, off.Message };
		}
		// already on: no writes
		{
			FakeSeasonalWorld w = From(SeasonalTheme.Rest, out SeasonalEngine e);
			SeasonalResult again = e.Apply(SeasonalTheme.Rest);
			Check("rest-already-on-is-a-no-op", again.Success && w.Writes == 0 && again.Message == "Dark rest is already on.", $"writes={w.Writes} msg={again.Message}");
		}
		// Windows already dark: no light/dark write, and Normal leaves it dark
		{
			FakeSeasonalWorld light = new FakeSeasonalWorld();
			SeasonalTheme.CreateEngine(light).Apply(SeasonalTheme.Rest);
			int lightWrites = light.Writes;
			FakeSeasonalWorld w = new FakeSeasonalWorld { Apps = Dw(WindowsThemeMode.AppsName, "0"), Sys = Dw(WindowsThemeMode.SystemName, "0") };
			SeasonalEngine e = SeasonalTheme.CreateEngine(w);
			SeasonalResult on = e.Apply(SeasonalTheme.Rest);
			int darkWrites = w.Writes;
			SeasonalResult off = e.Apply(SeasonalTheme.Normal);
			bool ok = on.Success && darkWrites == lightWrites - 1 && off.Success && off.Message == "Normal look restored." && w.ThemeText() == darkTheme && w.PeekState().Original == null;
			Check("rest-theme-already-dark", ok, $"writes light={lightWrites} dark={darkWrites} after={w.ThemeText()} msg={off.Message}");
		}
		// light/dark changed by the user while Dark rest is on: that choice is kept
		{
			// the system value (missing before) was set by the user: kept; the apps value still holds Dark rest's 0: restored
			FakeSeasonalWorld w = From(SeasonalTheme.Rest, out SeasonalEngine e);
			w.Sys = Dw(WindowsThemeMode.SystemName, "1");
			SeasonalResult r = e.Apply(SeasonalTheme.Normal);
			bool partly = r.Success && w.ThemeText() == "apps=DWord:1|sys=DWord:1" && r.Message.Contains("partly changed outside the launcher", StringComparison.Ordinal);
			FakeSeasonalWorld w2 = From(SeasonalTheme.Rest, out SeasonalEngine e2);
			w2.Apps = Dw(WindowsThemeMode.AppsName, "1");
			w2.Sys = Dw(WindowsThemeMode.SystemName, "1");
			SeasonalResult r2 = e2.Apply(SeasonalTheme.Normal);
			bool both = r2.Success && w2.ThemeText() == "apps=DWord:1|sys=DWord:1" && r2.Message.Contains("was changed outside the launcher, so it was kept", StringComparison.Ordinal);
			Check("rest-user-theme-change-kept", partly && both, $"one: {w.ThemeText()} msg={r.Message} | both: {w2.ThemeText()} msg={r2.Message}");
		}
		// Dark rest applied again after such a change: the user's value becomes what Normal restores
		{
			FakeSeasonalWorld w = From(SeasonalTheme.Rest, out SeasonalEngine e);
			w.Apps = Dw(WindowsThemeMode.AppsName, "1");
			SeasonalResult again = e.Apply(SeasonalTheme.Rest);
			RegValue? kept = w.PeekState().Original?.WindowsTheme?.AppsUseLightTheme;
			bool reapplied = again.Success && again.Message == "Dark rest is on." && w.ThemeText() == darkTheme && kept?.Kind == "DWord" && kept.Data == "1";
			SeasonalResult off = e.Apply(SeasonalTheme.Normal);
			bool back = off.Success && w.ThemeText() == lightTheme;
			// the same with a value that did not exist before: the user's new value replaces "missing"
			FakeSeasonalWorld w2 = From(SeasonalTheme.Rest, out SeasonalEngine e2);
			w2.Sys = Dw(WindowsThemeMode.SystemName, "1");
			e2.Apply(SeasonalTheme.Rest);
			RegValue? merged = w2.PeekState().Original?.WindowsTheme?.SystemUsesLightTheme;
			e2.Apply(SeasonalTheme.Normal);
			bool mergedOk = merged?.Data == "1" && w2.ThemeText() == "apps=DWord:1|sys=DWord:1";
			Check("rest-reapply-merges-user-theme", reapplied && back && mergedOk, $"again={again.Message} theme={w.ThemeText()} originalApps={kept} after={off.Message} | missing-sys merged={merged} final={w2.ThemeText()}");
		}
		// A value that is already back at the original (a restore interrupted after its first write, or a crash before the
		// restore point was dropped) is simply done: it is not reported as changed outside the launcher.
		{
			FakeSeasonalWorld w = From(SeasonalTheme.Rest, out SeasonalEngine e);
			w.Apps = Dw(WindowsThemeMode.AppsName, "1");
			SeasonalResult r = e.Apply(SeasonalTheme.Normal);
			bool half = r.Success && r.Message == "Normal look restored." && w.ThemeText() == lightTheme;
			FakeSeasonalWorld w2 = From(SeasonalTheme.Rest, out SeasonalEngine e2);
			w2.Apps = Dw(WindowsThemeMode.AppsName, "1");
			w2.Sys = null;
			SeasonalResult r2 = e2.Apply(SeasonalTheme.Normal);
			bool done = r2.Success && r2.Message == "Normal look restored." && w2.ThemeText() == lightTheme;
			int written = 0;
			int kept = 0;
			bool plan = !WindowsThemeMode.Plan(Dw(WindowsThemeMode.AppsName, "1"), Dw(WindowsThemeMode.AppsName, "1"), ref written, ref kept) && written == 0 && kept == 0;
			Check("rest-restore-values-already-back", half && done && plan, $"one back: {r.Message} {w.ThemeText()} | both back: {r2.Message} {w2.ThemeText()} | plan written={written} kept={kept}");
		}
		// Changing a Dark rest option (its wallpaper option or picture) brings the look up to date but never turns Windows
		// dark again after the user chose light, and never takes that choice into the restore point.
		{
			FakeSeasonalWorld w = From(SeasonalTheme.Rest, out SeasonalEngine e);
			WindowsThemeSnapshot before = w.PeekState().Original!.WindowsTheme!.Clone();
			w.Apps = Dw(WindowsThemeMode.AppsName, "1");
			w.Sys = Dw(WindowsThemeMode.SystemName, "1");
			w.SetSettings(x => x.RestWallpaper = false);
			SeasonalResult r = e.Apply(SeasonalTheme.Rest, keepTheme: true);
			WindowsThemeSnapshot? after = w.PeekState().Original?.WindowsTheme;
			bool wallpaperOff = r.Success && r.WallpaperOutcome == "restored" && w.Monitors[0].Path == "C:\\Pictures\\desk1.jpg" && w.PeekState().Original?.Wallpaper == null;
			string optionTheme = w.ThemeText();
			bool themeKept = optionTheme == "apps=DWord:1|sys=DWord:1" && after != null && WindowsThemeSnapshot.Same(after.AppsUseLightTheme, before.AppsUseLightTheme)
				&& WindowsThemeSnapshot.Same(after.SystemUsesLightTheme, before.SystemUsesLightTheme) && w.Peek().SeasonalActive == SeasonalTheme.Rest;
			// with nothing else to bring up to date, the option refresh is a no-op even though Windows is light
			w.Writes = 0;
			SeasonalResult again = e.Apply(SeasonalTheme.Rest, keepTheme: true);
			int noopWrites = w.Writes;
			bool noop = again.Success && noopWrites == 0 && again.Message == "Dark rest is already on.";
			// clicking Dark rest itself still re-applies everything (and makes Windows dark again)
			SeasonalResult click = e.Apply(SeasonalTheme.Rest);
			bool full = click.Success && w.ThemeText() == darkTheme;
			// an option refresh for Dark rest while it is not on changes nothing
			FakeSeasonalWorld n = new FakeSeasonalWorld();
			string normalWorld = n.World();
			SeasonalResult stale = SeasonalTheme.CreateEngine(n).Apply(SeasonalTheme.Rest, keepTheme: true);
			bool notOn = stale.Success && n.Writes == 0 && n.World() == normalWorld && n.StateJson == null;
			Check("rest-option-keeps-user-theme", wallpaperOff && themeKept && noop && full && notOn,
				$"option: {r.Message} wp={r.WallpaperOutcome} theme={optionTheme} | noop={again.Message} writes={noopWrites} | click={click.Message} theme={w.ThemeText()} | not on: {stale.Message}");
		}
		// The same option change crashing at any write: recovery puts the look back and still leaves light/dark alone.
		{
			FakeSeasonalWorld LightRest()
			{
				FakeSeasonalWorld x = From(SeasonalTheme.Rest, out _);
				x.Apps = Dw(WindowsThemeMode.AppsName, "1");
				x.Sys = Dw(WindowsThemeMode.SystemName, "1");
				x.SetSettings(s => s.RestWallpaper = false);
				x.Writes = 0;
				return x;
			}
			FakeSeasonalWorld probe = LightRest();
			SeasonalTheme.CreateEngine(probe).Apply(SeasonalTheme.Rest, keepTheme: true);
			int total = probe.Writes;
			List<string> failures = new List<string>();
			for (int n = 1; n <= total; n++)
			{
				FakeSeasonalWorld w = LightRest();
				w.CrashAfter = n;
				bool crashed = false;
				try
				{
					SeasonalTheme.CreateEngine(w).Apply(SeasonalTheme.Rest, keepTheme: true);
				}
				catch (SimulatedCrash)
				{
					crashed = true;
				}
				w.CrashAfter = -1;
				SeasonalTheme.CreateEngine(w).Recover();
				if (!crashed || w.ThemeText() != "apps=DWord:1|sys=DWord:1" || w.LoadState().Phase != SeasonalTheme.PhaseIdle || w.Peek().SeasonalActive != SeasonalTheme.Rest)
				{
					failures.Add($"n={n} crashed={crashed} theme={w.ThemeText()} phase={w.LoadState().Phase}");
				}
			}
			Check("crash-recovery-rest-option", failures.Count == 0 && total >= 2, failures.Count == 0 ? $"{total} crash points" : string.Join(" ; ", failures));
		}
		// Halloween, Dark rest, Christmas, Normal: one restore point; light/dark changes only while Dark rest is on
		{
			FakeSeasonalWorld w = new FakeSeasonalWorld();
			string original = w.World();
			SeasonalEngine e = SeasonalTheme.CreateEngine(w);
			e.Apply(SeasonalTheme.Halloween);
			SeasonalOriginal o1 = w.PeekState().Original!;
			DateTime captured = o1.CapturedUtc;
			bool afterHalloween = o1.WindowsTheme == null && w.ThemeText() == lightTheme;
			w.Now = w.Now.AddDays(5);
			e.Apply(SeasonalTheme.Rest);
			SeasonalOriginal o2 = w.PeekState().Original!;
			bool afterRest = o2.WindowsTheme != null && o2.CapturedUtc == captured && w.ThemeText() == darkTheme;
			w.Now = w.Now.AddDays(5);
			e.Apply(SeasonalTheme.Christmas);
			SeasonalOriginal o3 = w.PeekState().Original!;
			bool afterChristmas = o3.WindowsTheme == null && o3.CapturedUtc == captured && w.ThemeText() == lightTheme && w.Peek().SeasonalActive == SeasonalTheme.Christmas;
			w.Now = w.Now.AddDays(5);
			SeasonalResult off = e.Apply(SeasonalTheme.Normal);
			bool ok = afterHalloween && afterRest && afterChristmas && off.Success && w.World() == original;
			Check("switch-halloween-rest-christmas-normal", ok, $"halloween={afterHalloween} rest={afterRest} christmas={afterChristmas} final={w.World()}");
		}
		// Windows does not take dark mode: the apply rolls back completely
		{
			FakeSeasonalWorld w = new FakeSeasonalWorld { FailSetDark = true };
			string original = w.World();
			SeasonalResult r = SeasonalTheme.CreateEngine(w).Apply(SeasonalTheme.Rest);
			SeasonalStateFile st = w.PeekState();
			bool ok = !r.Success && w.World() == original && st.Original == null && st.Phase == SeasonalTheme.PhaseIdle
				&& r.Message.StartsWith("Could not apply Dark rest: Windows light/dark mode could not be changed.", StringComparison.Ordinal);
			Check("rest-theme-write-failure-rolls-back", ok, $"msg={r.Message} world={w.World()}");
		}
		// putting light/dark back fails: Normal stays pending and finishes on the next recovery
		{
			FakeSeasonalWorld w = From(SeasonalTheme.Rest, out SeasonalEngine e);
			string original = new FakeSeasonalWorld().World();
			w.FailRestoreTheme = true;
			SeasonalResult r = e.Apply(SeasonalTheme.Normal);
			string phase = w.PeekState().Phase;
			w.FailRestoreTheme = false;
			SeasonalResult rec = SeasonalTheme.CreateEngine(w).Recover();
			SeasonalStateFile st = w.PeekState();
			bool ok = !r.Success && phase == SeasonalTheme.PhaseRestoring && rec.Success && w.World() == original && st.Phase == SeasonalTheme.PhaseIdle && st.Original == null;
			Check("rest-restore-theme-failure-pending", ok, $"normal={r.Message} phase={phase} recover={rec.Message} final={w.World()}");
		}
		// the restore point was lost while Dark rest is on
		{
			FakeSeasonalWorld w = LostRest(themeUnreadable: false);
			SeasonalResult r = SeasonalTheme.CreateEngine(w).Apply(SeasonalTheme.Normal);
			AppSettings s = w.Peek();
			AppSettings d = new AppSettings();
			bool ok = r.Success && s.SeasonalActive == "" && s.StartBgMode == d.StartBgMode && s.StartPattern == d.StartPattern && s.StartBgColor == d.StartBgColor
				&& r.Message.Contains("still in dark mode", StringComparison.Ordinal) && w.ThemeText() == darkTheme;
			FakeSeasonalWorld w2 = LostRest(themeUnreadable: true);
			SeasonalResult r2 = SeasonalTheme.CreateEngine(w2).Apply(SeasonalTheme.Normal);
			bool unreadable = r2.Success && w2.Peek().SeasonalActive == "" && !r2.Message.Contains("dark mode", StringComparison.Ordinal);
			Check("rest-lost-restore-point", ok && unreadable, $"msg={r.Message} | unreadable: {r2.Message}");
		}
		// the settings were replaced outside the launcher while Dark rest was on: kept, the rest goes back
		{
			FakeSeasonalWorld w = From(SeasonalTheme.Rest, out _);
			w.SetSettings(delegate(AppSettings x)
			{
				x.StartBgMode = "custom";
				x.StartBgCustomPath = "C:\\Pictures\\imported.jpg";
				x.StartBgColor = "#FF112233";
				x.StartPattern = "dots";
				x.StartAccentColor = "#FF445566";
				x.SeasonalActive = "";
			});
			SeasonalResult r = SeasonalTheme.CreateEngine(w).Recover();
			AppSettings s = w.Peek();
			SeasonalStateFile st = w.PeekState();
			bool ok = r.Success && s.StartBgCustomPath == "C:\\Pictures\\imported.jpg" && s.StartPattern == "dots" && s.StartAccentColor == "#FF445566"
				&& w.Monitors[0].Path == "C:\\Pictures\\desk1.jpg" && w.Monitors[1].Path == "C:\\Pictures\\desk2.jpg" && w.ThemeText() == lightTheme
				&& st.Original == null && st.Phase == SeasonalTheme.PhaseIdle;
			Check("rest-outside-settings-change", ok, w.World());
		}
		// light/dark cannot be read: Dark rest is refused before anything is written
		{
			FakeSeasonalWorld w = new FakeSeasonalWorld { ThemeUnreadable = true };
			string before = w.World();
			SeasonalResult r = SeasonalTheme.CreateEngine(w).Apply(SeasonalTheme.Rest);
			bool ok = !r.Success && w.Writes == 0 && w.World() == before && w.StateJson == null && r.Message == "Could not read the Windows light/dark mode, so nothing was changed.";
			Check("rest-unreadable-theme-refused", ok, $"writes={w.Writes} msg={r.Message}");
		}
		// Dark rest has its own wallpaper option
		{
			FakeSeasonalWorld w = new FakeSeasonalWorld();
			w.SetSettings(x => x.RestWallpaper = false);
			SeasonalResult r = SeasonalTheme.CreateEngine(w).Apply(SeasonalTheme.Rest);
			bool off = r.Success && r.WallpaperOutcome == "off" && w.PeekState().Original!.Wallpaper == null && w.Monitors[0].Path == "C:\\Pictures\\desk1.jpg"
				&& w.Position == 3 && w.ThemeText() == darkTheme;
			FakeSeasonalWorld w2 = new FakeSeasonalWorld();
			w2.SetSettings(x => x.SeasonalWallpaper = false);
			SeasonalResult r2 = SeasonalTheme.CreateEngine(w2).Apply(SeasonalTheme.Rest);
			bool independent = r2.Success && r2.WallpaperOutcome == "set" && w2.Monitors.All(m => w2.IsSeasonalFile(m.Path)) && w2.PeekState().Original!.Wallpaper != null;
			Check("rest-wallpaper-pref", off && independent, $"restWallpaper=false: {r.WallpaperOutcome} {w.World()} | seasonalWallpaper=false: {r2.WallpaperOutcome}");
		}
		// the restore point with the light/dark values round-trips through JSON
		{
			SeasonalStateFile st = new SeasonalStateFile
			{
				Phase = SeasonalTheme.PhaseIdle,
				ActiveSinceUtc = new DateTime(2026, 10, 1, 8, 0, 0, DateTimeKind.Utc),
				LastResult = "applied rest",
				UpdatedUtc = new DateTime(2026, 10, 9, 8, 0, 0, DateTimeKind.Utc),
				Original = new SeasonalOriginal
				{
					CapturedUtc = new DateTime(2026, 10, 1, 7, 59, 0, DateTimeKind.Utc),
					StartBgMode = "pattern",
					StartBgColor = "#FFA61E5A",
					StartPattern = "waves",
					WindowsTheme = new WindowsThemeSnapshot
					{
						AppsUseLightTheme = Dw(WindowsThemeMode.AppsName, "1"),
						SystemUsesLightTheme = null
					}
				}
			};
			string json = JsonSerializer.Serialize(st, SeasonalTheme.Json);
			SeasonalStateFile back = JsonSerializer.Deserialize<SeasonalStateFile>(json, SeasonalTheme.Json)!;
			string again = JsonSerializer.Serialize(back, SeasonalTheme.Json);
			WindowsThemeSnapshot? t = back.Original?.WindowsTheme;
			bool ok = json == again && SeasonalTheme.ValidateStateJson(json) && t?.AppsUseLightTheme?.Data == "1" && t.AppsUseLightTheme.Kind == "DWord"
				&& t.SystemUsesLightTheme == null && !json.Contains("IsDark", StringComparison.Ordinal);
			Check("state-json-roundtrip-theme", ok, json == again ? "" : again);
		}
		CrashRecovery("normal-rest", "", SeasonalTheme.Rest);
		CrashRecovery("rest-normal", SeasonalTheme.Rest, SeasonalTheme.Normal);
		CrashRecovery("halloween-rest", SeasonalTheme.Halloween, SeasonalTheme.Rest);
		CrashRecovery("rest-christmas", SeasonalTheme.Rest, SeasonalTheme.Christmas);
	}

	// Dark rest's look and marker without a restore point, with Windows dark.
	private static FakeSeasonalWorld LostRest(bool themeUnreadable)
	{
		SeasonPreset rest = SeasonalTheme.Find(SeasonalTheme.Rest)!;
		FakeSeasonalWorld w = new FakeSeasonalWorld
		{
			Apps = Dw(WindowsThemeMode.AppsName, "0"),
			Sys = Dw(WindowsThemeMode.SystemName, "0"),
			ThemeUnreadable = themeUnreadable
		};
		w.SetSettings(delegate(AppSettings x)
		{
			x.StartBgMode = "pattern";
			x.StartPattern = rest.Pattern;
			x.StartBgColor = rest.Base;
			x.StartAccentColor = rest.Accent;
			x.SeasonalActive = SeasonalTheme.Rest;
		});
		w.StateJson = null;
		return w;
	}

	// ===================================================================== pure rules

	private static void RestPureChecks(AppSettings real)
	{
		SeasonPreset? found = SeasonalTheme.Find(" REST ");
		SeasonPreset rest = found ?? SeasonalTheme.Presets[SeasonalTheme.Presets.Length - 1];
		{
			bool ok = found != null && SeasonalTheme.NormalizeId(" REST ") == SeasonalTheme.Rest && found.Name == "Dark rest" && SeasonalTheme.Presets.Length == 3
				&& string.Join(",", SeasonalTheme.Seasons.Select(p => p.Id)) == "halloween,christmas"
				&& string.Join(",", SeasonalTheme.Choices.Select(c => c.Id + ":" + c.Label)) == "normal:Normal,halloween:Halloween,christmas:Christmas,rest:Dark rest"
				&& !SeasonalTheme.IsSeason(SeasonalTheme.Rest) && SeasonalTheme.IsSeason(SeasonalTheme.Halloween) && SeasonalTheme.IsRest(SeasonalTheme.Rest)
				&& !SeasonalTheme.WantsWallpaper(new AppSettings { RestWallpaper = false, SeasonalWallpaper = true }, rest)
				&& SeasonalTheme.WantsWallpaper(new AppSettings { RestWallpaper = true, SeasonalWallpaper = false }, rest)
				&& rest.Ambient.Length == 0 && SeasonalArt.IsPresetPattern(rest.Pattern) && !SeasonalArt.IsSeasonalPattern(rest.Pattern)
				&& !StartBackgroundFactory.Patterns.Contains(rest.Pattern) && StartBackgroundFactory.Presets.All(p => p.Pattern != rest.Pattern);
			Check("rest-preset-wiring", ok, $"choices={string.Join(",", SeasonalTheme.Choices.Select(c => c.Label))} seasons={SeasonalTheme.Seasons.Length}");
		}
		AppSettings? saved = SettingsStore.DiagnosticsOverride;
		try
		{
			SettingsStore.DiagnosticsOverride = RestSettings(real);
			Color accent = SeasonalArt.Hex(rest.Accent);
			{
				var (_, s, _) = Hsl(accent.R, accent.G, accent.B);
				double contrast = 1.05 / (ColorMath.RelLuminance(accent) + 0.05);
				Color start = StartAccent.Color();
				Check("rest-accent-muted", s <= 0.25 && contrast >= 4.5 && start == accent, $"s={s:F3} whiteContrast={contrast:F2} startAccent={Hex(start)}");
			}
			{
				List<string> bad = new List<string>();
				bool visuals = DarkRest.Visuals;
				if (!visuals)
				{
					bad.Add("Dark rest visuals are off (High Contrast?)");
				}
				(string Hex, double L)[] table = new (string, double)[8]
				{
					("#273038", 0.34), ("#222B32", 0.30), ("#20282E", 0.28), ("#252E35", 0.32), ("#35424C", 0.46), ("#394753", 0.50), ("#3C4A56", 0.52), ("#12171B", 0.16)
				};
				double accentHue = Hsl(accent.R, accent.G, accent.B).H;
				List<string> got = new List<string>();
				foreach ((string want, double l) in table)
				{
					Color c = SettingsPane.AccentToneColor(l);
					got.Add($"{l:F2}={Hex(c)}");
					var (h, s, _) = Hsl(c.R, c.G, c.B);
					if (Hex(c) != want)
					{
						bad.Add($"{l:F2}: {Hex(c)} want {want}");
					}
					// byte rounding at these dark values moves saturation and hue a little
					if (s > 0.205 || HueDistance(h, accentHue) > 3.0)
					{
						bad.Add($"{l:F2}: s={s:F3} hue={h:F1} accent hue={accentHue:F1}");
					}
				}
				foreach ((string from, string want) in new (string, string)[3] { ("#FF00A0E7", "#325767"), ("#FFCF2056", "#603945"), ("#FF2DB72C", "#3C5D3C") })
				{
					Color m = DarkRestPalette.Mute(SeasonalArt.Hex(from));
					got.Add($"mute {from}={Hex(m)}");
					if (Hex(m) != want)
					{
						bad.Add($"mute {from}: {Hex(m)} want {want}");
					}
				}
				Metrics["RestTones"] = got;
				Check("rest-tone-rules", bad.Count == 0, bad.Count == 0 ? string.Join(" ", got) : string.Join(" ; ", bad));
			}
		}
		finally
		{
			SettingsStore.DiagnosticsOverride = saved;
		}
		// Calmer animations: Authentic and Fast play as Reduced, Off and Reduced stay; the user's mode is never changed
		{
			MotionMode savedMode = Motion.UserMode;
			bool savedCalm = Motion.CalmOverride;
			List<string> bad = new List<string>();
			try
			{
				foreach (MotionMode m in new MotionMode[4] { MotionMode.Authentic, MotionMode.Fast, MotionMode.Reduced, MotionMode.Off })
				{
					foreach (bool calm in new bool[2] { false, true })
					{
						Motion.CalmOverride = calm;
						Motion.Mode = m;
						MotionMode want = calm && (m == MotionMode.Authentic || m == MotionMode.Fast) ? MotionMode.Reduced : m;
						if (Motion.Mode != want || Motion.UserMode != m)
						{
							bad.Add($"{m}/calm={calm}: mode={Motion.Mode} user={Motion.UserMode}");
						}
					}
				}
			}
			finally
			{
				Motion.CalmOverride = savedCalm;
				Motion.Mode = savedMode;
			}
			AppSettings on = RestSettings(real);
			AppSettings noCalm = RestSettings(real);
			noCalm.RestCalmMotion = false;
			if (!DarkRest.CalmMotion(on) || DarkRest.CalmMotion(noCalm) || DarkRest.CalmMotion(NormalSettings(real)))
			{
				bad.Add("CalmMotion does not follow the marker and the option");
			}
			Check("rest-motion-override", bad.Count == 0, string.Join(" ; ", bad));
		}
		// Pointers: Dark rest is the dark style for every Alchemy mode; other pointer choices are never forced
		{
			List<string> bad = new List<string>();
			foreach (string m in new string[3] { AlchemyPalettes.Dark, AlchemyPalettes.Light, AlchemyPalettes.Auto })
			{
				foreach (bool isDark in new bool[2] { true, false })
				{
					string on = AlchemyPalettes.Resolve(m, isDark, SeasonalTheme.Rest, seasonalOn: true);
					string off = AlchemyPalettes.Resolve(m, isDark, SeasonalTheme.Rest, seasonalOn: false);
					if (on != "rest-dark" || off != AlchemyPalettes.Dark)
					{
						bad.Add($"{m}/{(isDark ? "dark" : "light")}: {on}/{off}");
					}
				}
			}
			if (AlchemyPalettes.Resolve("", true, SeasonalTheme.Rest, true) != "")
			{
				bad.Add("pointers off must stay off");
			}
			if (PointerScheme.Effective(new AppSettings { UseWin81Cursors = true, AlchemyCursors = "", SeasonalActive = SeasonalTheme.Rest }, true).Kind != PointerKind.Win81
				|| PointerScheme.Effective(new AppSettings { UseWin81Cursors = false, AlchemyCursors = "", SeasonalActive = SeasonalTheme.Rest }, true).Kind != PointerKind.Windows)
			{
				bad.Add("Windows and Windows 8.1 pointers must not be forced");
			}
			if (AlchemyPalettes.OtherTone("rest-dark") != "")
			{
				bad.Add("rest-dark has no other tone");
			}
			AlchemyPalette? p = AlchemyPalettes.Find("rest-dark");
			double accentS = p == null ? 1.0 : Hsl((p.Accent >> 16) & 0xFF, (p.Accent >> 8) & 0xFF, p.Accent & 0xFF).S;
			if (p == null || p.Tone != AlchemyPalettes.Dark || p.Season != AlchemyPalettes.RestSeason || accentS > 0.25)
			{
				bad.Add($"rest-dark palette: {p} s={accentS:F2}");
			}
			Check("rest-pointer-resolve", bad.Count == 0, string.Join(" ; ", bad));
		}
	}

	// ===================================================================== art

	private static void RestArtChecks(string outDir)
	{
		// the drawn desktop scene: deterministic, dark, low contrast, no bright or saturated element
		{
			BitmapSource a = SeasonalArt.RenderWallpaper(SeasonalArt.RestArt, 1920, 1080);
			BitmapSource b = SeasonalArt.RenderWallpaper(SeasonalArt.RestArt, 1920, 1080);
			byte[] pa = Pixels(a);
			byte[] pb = Pixels(b);
			bool same = SHA256.HashData(pa).AsSpan().SequenceEqual(SHA256.HashData(pb));
			double max = 0;
			double sum = 0;
			double sq = 0;
			int chroma = 0;
			double bottomL = 0;
			int n = pa.Length / 4;
			for (int i = 0; i < pa.Length; i += 4)
			{
				double l = 0.0722 * pa[i] + 0.7152 * pa[i + 1] + 0.2126 * pa[i + 2];
				max = Math.Max(max, l);
				sum += l;
				sq += l * l;
				chroma = Math.Max(chroma, Math.Max(pa[i], Math.Max(pa[i + 1], pa[i + 2])) - Math.Min(pa[i], Math.Min(pa[i + 1], pa[i + 2])));
				if (i / 4 / 1920 >= 1040)
				{
					bottomL = Math.Max(bottomL, Hsl(pa[i + 2], pa[i + 1], pa[i]).L);
				}
			}
			double mean = sum / n;
			double stdev = Math.Sqrt(Math.Max(0, sq / n - mean * mean));
			SavePng(a, Path.Combine(outDir, "wallpaper-rest.png"));
			BitmapSource small = SeasonalArt.RenderWallpaper(SeasonalArt.RestArt, 1280, 1024);
			SavePng(small, Path.Combine(outDir, "wallpaper-rest-1280x1024.png"));
			bool ok = same && max <= 48 && mean >= 8 && mean <= 30 && stdev <= 8 && chroma <= 24 && bottomL <= 0.10 && small.PixelWidth == 1280;
			string detail = $"deterministic={same} maxLuma={max:F1} meanLuma={mean:F1} stdev={stdev:F2} maxChroma={chroma} bottomMaxL={bottomL:F3}";
			Metrics["RestWallpaper"] = detail;
			Check("rest-wallpaper-calm", ok, detail);
		}
		// the dusk Start pattern: a frozen 480 DIU tile, seamless, barely lighter than the base
		{
			SeasonPreset rest = SeasonalTheme.Find(SeasonalTheme.Rest)!;
			Color bc = SeasonalArt.Hex(rest.Base);
			Color ac = SeasonalArt.Hex(rest.Accent);
			Brush brush = StartBackgroundFactory.Build(bc, ac, rest.Pattern);
			double offset = SeasonalArt.TileOffsetY(rest.Pattern);
			bool frozen = brush is DrawingBrush db && db.IsFrozen && db.Viewbox.Width == 480.0 && db.Viewport.Width == 480.0 && db.Viewport.Y == offset && offset == 50.0 && db.TileMode == TileMode.Tile;
			// The header row of Start (y 24-88) lies between two lines, exactly as Start draws the brush.
			DrawingVisual header = new DrawingVisual();
			using (DrawingContext dc = header.RenderOpen())
			{
				dc.DrawRectangle(brush, null, new Rect(0, 0, 960, 120));
			}
			byte[] hp = Pixels(Render(header, 960, 120));
			int headerDev = 0;
			for (int y = 24; y <= 88; y++)
			{
				for (int x = 0; x < 960; x++)
				{
					int i = (y * 960 + x) * 4;
					headerDev = Math.Max(headerDev, Math.Max(Math.Abs(hp[i] - bc.B), Math.Max(Math.Abs(hp[i + 1] - bc.G), Math.Abs(hp[i + 2] - bc.R))));
				}
			}
			// One whole tile from its own top edge for the edge, seam and contrast checks (the header offset taken out).
			DrawingBrush aligned = ((DrawingBrush)brush).Clone();
			aligned.Viewport = new Rect(0.0, 0.0, 480.0, 480.0);
			aligned.Freeze();
			DrawingVisual dv = new DrawingVisual();
			using (DrawingContext dc = dv.RenderOpen())
			{
				dc.DrawRectangle(aligned, null, new Rect(0, 0, 960, 480));
			}
			BitmapSource render = Render(dv, 960, 480);
			byte[] px = Pixels(render);
			int Px(int x, int y, int c)
			{
				return px[(y * 960 + x) * 4 + c];
			}
			int edge = 0;
			int seam = 0;
			int dev = 0;
			for (int x = 0; x < 960; x++)
			{
				foreach (int y in new int[2] { 0, 479 })
				{
					edge = Math.Max(edge, Math.Max(Math.Abs(Px(x, y, 0) - bc.B), Math.Max(Math.Abs(Px(x, y, 1) - bc.G), Math.Abs(Px(x, y, 2) - bc.R))));
				}
			}
			for (int y = 0; y < 480; y++)
			{
				for (int c = 0; c < 3; c++)
				{
					seam = Math.Max(seam, Math.Abs(Px(479, y, c) - Px(480, y, c)));
				}
				for (int x = 0; x < 960; x++)
				{
					dev = Math.Max(dev, Math.Max(Math.Abs(Px(x, y, 0) - bc.B), Math.Max(Math.Abs(Px(x, y, 1) - bc.G), Math.Abs(Px(x, y, 2) - bc.R))));
				}
			}
			double stdev = LuminanceStdev(px);
			SavePng(render, Path.Combine(outDir, "pattern-dusk.png"));
			bool ok = frozen && edge <= 2 && seam <= 3 && dev <= 24 && stdev >= 0.2 && stdev <= 4.0 && headerDev <= 2;
			string detail = $"frozen={frozen} offsetY={offset} edgeRows={edge} seam={seam} maxDeviation={dev} stdev={stdev:F2} headerRowDeviation={headerDev}";
			Metrics["DuskPattern"] = detail;
			Check("rest-pattern-dusk", ok, detail);
		}
	}

	// ===================================================================== live renders

	// The user's own settings with Dark rest on and all three Dark rest options on. The taskbar is set to follow the
	// wallpaper, transparent, to prove that Dark rest ignores both.
	private static AppSettings RestSettings(AppSettings real, bool dimTiles = true)
	{
		SeasonPreset p = SeasonalTheme.Find(SeasonalTheme.Rest)!;
		AppSettings s = real.Clone();
		s.StartBgMode = "pattern";
		s.StartPattern = p.Pattern;
		s.StartBgColor = p.Base;
		s.StartAccentColor = p.Accent;
		s.SeasonalActive = p.Id;
		s.RestWallpaper = true;
		s.RestCalmMotion = true;
		s.RestDimTiles = dimTiles;
		s.TaskbarColorMode = "Wallpaper";
		s.TaskbarTransparent = true;
		return s;
	}

	private sealed class RestFixtures
	{
		public TaskbarWindow Taskbar = null!;

		public SearchPane Search = null!;

		public SettingsPane Settings = null!;

		public CharmsBar Charms = null!;

		public OsdWindow Osd = null!;

		public PcSettingsWindow Pc = null!;

		public Win7StartMenu Win7 = null!;
	}

	private static readonly DateTime FlyoutSample = new DateTime(2026, 8, 31, 17, 17, 6, DateTimeKind.Local);

	// The Normal renders compared before and after the Dark rest pass ("*-normal-before.png" / "*-normal-after.png").
	// The switcher is not among them: it lists the real open windows, which may change between the two renders.
	private static readonly string[] NormalPairs = new string[14]
	{
		"taskbar-strip", "actioncenter", "searchpane", "settingspane", "charms", "toast", "osd", "pcsettings", "flyout-clock", "flyout-volume", "flyout-network",
		"start-apps", "personalize", "win7start"
	};

	private static readonly NetInfo.LocalNet QaLocalNet = new NetInfo.LocalNet("Wi-Fi", "192.168.1.9", "192.168.1.1", "1.1.1.1, 8.8.8.8", "Intel Wi-Fi 6E AX210", "866 Mbps");

	private static readonly NetState81 QaNetState = new NetState81(NetKind.Wifi, "HomeNetwork", 5, Internet: true);

	private static void RenderNormalSet(string outDir, StartScreen start, AppSettings real, RestFixtures f, string when)
	{
		SettingsStore.DiagnosticsOverride = NormalSettings(real);
		SeasonalTheme.DiagnosticsOwnsWallpaper = false;
		SeasonalTheme.DiagnosticsOwnsWindowsTheme = false;
		TaskbarTheme.DiagnosticsWallpaperPath = null;
		TaskbarTheme.Invalidate();
		DarkRest.Sync();
		// As the live refresh does: Start writes the accent resources menus and links share.
		start.RefreshBackground();
		string P(string name) => Path.Combine(outDir, name + "-normal-" + when + ".png");
		RenderTaskbarNormal(P("taskbar-strip"), real);
		ActionCenter.QaRenderMetro(P("actioncenter"), 468.0, 860.0, compact: false, withNotifications: false, currentTheme: true);
		f.Search.QaRender(P("searchpane"), RestQuery, 920.0);
		f.Settings.QaRender(P("settingspane"), 1040.0);
		f.Charms.QaRender(P("charms"));
		RenderToast(P("toast"));
		f.Osd.QaRenderCard(P("osd"));
		f.Pc.QaRender(P("pcsettings"), "PC & devices", 1366.0, 768.0);
		f.Taskbar.QaRenderClockCalendar(P("flyout-clock"), FlyoutSample, 340.0, 500.0);
		f.Taskbar.QaRenderSoundFlyout(P("flyout-volume"), 420.0, 540.0);
		f.Taskbar.QaRenderNetworkFlyout(P("flyout-network"), QaNetState, QaLocalNet, "203.0.113.42", 420.0, 500.0);
		start.QaRenderSeason(P("start-apps"), 1920.0, 1080.0, withAmbient: true, allApps: true);
		start.QaRenderPersonalize(P("personalize"));
		f.Win7.QaRenderPreset(P("win7start"), TaskbarTheme.SourceAccent());
	}

	private static void RenderToast(string path)
	{
		ToastWindow toast = new ToastWindow(null, "Personalize", "Start colour updated", "Your Start colour was updated.", null, "3:07 PM");
		toast.QaRender(path);
		toast.Close();
	}

	private static void RestChecks(string outDir, StartScreen start, AppSettings real)
	{
		// Pin the real light/dark value first, so the Normal renders before and after use the same applied tokens.
		bool realDark = ShellTheme.IsDark;
		ShellTheme.ForceForTest(realDark);
		RestFixtures f = new RestFixtures
		{
			Taskbar = new TaskbarWindow { Left = -4000.0, Top = -4000.0, Width = 1366.0, Height = 48.0 },
			Search = new SearchPane(() => start.AllApps, delegate { }) { Left = -4000.0, Top = -4000.0, ShowActivated = false },
			Settings = new SettingsPane { Left = -4000.0, Top = -4000.0, ShowActivated = false },
			Charms = new CharmsBar { Left = -4000.0, Top = -4000.0, ShowActivated = false },
			Osd = new OsdWindow(),
			Pc = new PcSettingsWindow(start) { Left = -6000.0, Top = -6000.0, ShowActivated = false }
		};
		Win7StartMenu? win7 = null;
		win7 = new Win7StartMenu(() => start.AllApps, delegate(AppEntry a, bool asAdmin)
		{
			win7?.QaRecordLaunch(a, asAdmin);
		});
		f.Win7 = win7;
		f.Taskbar.Show();
		// One pass that is not compared first, so lazily built views (the all-apps list, the Personalize pane) are in
		// place before the Normal renders that are.
		string warm = Path.Combine(outDir, "rest-warmup");
		Directory.CreateDirectory(warm);
		RenderNormalSet(warm, start, real, f, "warmup");
		try
		{
			Directory.Delete(warm, recursive: true);
		}
		catch
		{
		}
		RenderNormalSet(outDir, start, real, f, "before");
		try
		{
			SettingsStore.DiagnosticsOverride = RestSettings(real);
			TaskbarTheme.DiagnosticsWallpaperPath = Path.Combine(outDir, "wallpaper-rest.png");
			SeasonalTheme.DiagnosticsOwnsWallpaper = true;
			SeasonalTheme.DiagnosticsOwnsWindowsTheme = true;
			ShellTheme.ForceForTest(true);
			TaskbarTheme.Invalidate();
			DarkRest.Sync();
			Check("rest-visuals-on", DarkRest.Visuals && Motion.CalmOverride, $"visuals={DarkRest.Visuals} calm={Motion.CalmOverride} highContrast={SystemParameters.HighContrast}");
			Metrics["RestStatusText"] = SeasonalTheme.StatusText(new SeasonalStatus { ActiveId = SeasonalTheme.Rest, ActiveName = "Dark rest", OwnsWallpaper = true, OwnsWindowsTheme = true });
			Metrics["RestStartHint"] = SeasonalTheme.NormalRestoresHint();
			RestStartRenders(outDir, start, real);
			RestTaskbar(outDir, real, f.Taskbar);
			RestFlyouts(outDir, f.Taskbar);
			RestPanes(outDir, start, f);
			RestPcSettings(outDir, f.Pc);
			RestCaption(real);
		}
		finally
		{
			ShellTheme.ForceForTest(realDark);
			SettingsStore.DiagnosticsOverride = NormalSettings(real);
			SeasonalTheme.DiagnosticsOwnsWallpaper = false;
			SeasonalTheme.DiagnosticsOwnsWindowsTheme = null;
			TaskbarTheme.DiagnosticsWallpaperPath = null;
			Motion.CalmOverride = false;
			TaskbarTheme.Invalidate();
		}
		RenderNormalSet(outDir, start, real, f, "after");
		List<string> diffs = new List<string>();
		foreach (string name in NormalPairs)
		{
			string before = Path.Combine(outDir, name + "-normal-before.png");
			string after = Path.Combine(outDir, name + "-normal-after.png");
			(int diff, int total) = PixelDiff(before, after);
			diffs.Add($"{name}={diff}/{total}");
			if (diff != 0)
			{
				// keep the pair for review
				continue;
			}
			try
			{
				File.Delete(after);
			}
			catch
			{
			}
		}
		Metrics["NormalAfterRestDiffs"] = diffs;
		Check("normal-unchanged-after-rest", diffs.All(d => d.Contains("=0/", StringComparison.Ordinal)), string.Join(" ", diffs));
		f.Taskbar.Close();
		f.Pc.Close();
		f.Search.Close();
		f.Settings.Close();
		f.Win7.DisposeMenu();
	}

	private static void RestStartRenders(string outDir, StartScreen start, AppSettings real)
	{
		string startPath = Path.Combine(outDir, "start-rest.png");
		start.QaRenderSeason(startPath, 1920.0, 1080.0, withAmbient: true);
		bool noAmbient = !start.QaBgLayerHasChild;
		RegionStats header = Region(startPath, 0, 0, 1920, 36);
		RegionStats board = Region(startPath, 0, 520, 1920, 560);
		Check("rest-start-dark", noAmbient && header.L <= 0.10 && header.Chroma <= 12 && board.L <= 0.10 && board.Chroma <= 12,
			$"ambient={!noAmbient} header={header} board={board}");
		string appsPath = Path.Combine(outDir, "start-rest-apps.png");
		start.QaRenderSeason(appsPath, 1920.0, 1080.0, withAmbient: true, allApps: true);
		Rect field = start.QaLastSearchField;
		RegionStats fieldStats = field.Width > 4 ? Region(appsPath, (int)field.X + 2, (int)field.Y + 2, (int)field.Width - 4, (int)field.Height - 4) : default;
		Check("rest-start-apps-search-dark", field.Width > 4 && fieldStats.L <= 0.20, $"field={field} {fieldStats}");
		start.QaRenderPersonalize(Path.Combine(outDir, "personalize-rest.png"));
		(int count, string selected) = start.QaSeasonChips();
		Check("rest-personalize-chips", count == 4 && selected == SeasonalTheme.Rest, $"chips={count} selected={selected}");
		// the tile dimming: the same tile with and without it
		SettingsStore.DiagnosticsOverride = RestSettings(real, dimTiles: false);
		string noDim = Path.Combine(outDir, "start-rest-nodim.png");
		start.QaRenderSeason(noDim, 1920.0, 1080.0, withAmbient: true);
		SettingsStore.DiagnosticsOverride = RestSettings(real);
		byte[] dim = Pixels(new CroppedBitmap(LoadPng(startPath), new Int32Rect(155, 200, 1, 1)));
		byte[] full = Pixels(new CroppedBitmap(LoadPng(noDim), new Int32Rect(155, 200, 1, 1)));
		bool dimmed = true;
		List<string> ch = new List<string>();
		for (int c = 0; c < 3; c++)
		{
			double want = full[c] * (1.0 - 64.0 / 255.0);
			ch.Add($"{full[c]}->{dim[c]} (want {want:F1})");
			dimmed &= Math.Abs(dim[c] - want) <= Math.Max(1.5, 0.02 * full[c]);
		}
		Check("rest-tiles-dimmed", dimmed && full.Take(3).Any(v => v >= 60), string.Join(" ", ch));
		// the tile names are drawn above the dimming: the first tile's name is as bright as without it
		int NameInk(string path)
		{
			byte[] p = Pixels(new CroppedBitmap(LoadPng(path), new Int32Rect(90, 280, 130, 16)));
			int m = 0;
			for (int i = 0; i < p.Length; i += 4)
			{
				m = Math.Max(m, Math.Min(p[i], Math.Min(p[i + 1], p[i + 2])));
			}
			return m;
		}
		int nameDim = NameInk(startPath);
		int nameFull = NameInk(noDim);
		Check("rest-tile-names-not-dimmed", nameFull >= 200 && nameDim >= nameFull - 3, $"name ink with dimming={nameDim} without={nameFull}");
		start.RefreshBackground();
	}

	private static void RestTaskbar(string outDir, AppSettings real, TaskbarWindow taskbar)
	{
		List<string> bad = new List<string>();
		foreach (string mode in new string[3] { "Wallpaper", "Start", "Transparent" })
		{
			foreach (bool transparent in new bool[2] { true, false })
			{
				foreach (bool owns in new bool[2] { true, false })
				{
					AppSettings s = RestSettings(real);
					s.TaskbarColorMode = mode;
					s.TaskbarTransparent = transparent;
					SettingsStore.DiagnosticsOverride = s;
					SeasonalTheme.DiagnosticsOwnsWallpaper = owns;
					TaskbarTheme.Invalidate();
					Color bg = (TaskbarTheme.CurrentBackground() as SolidColorBrush)?.Color ?? Colors.Transparent;
					Color chrome = TaskbarTheme.ChromeColor();
					Color source = TaskbarTheme.SourceAccent();
					bool cached = TaskbarTheme.TryCachedSourceAccent(out Color cachedSource);
					if (bg != SeasonalArt.Hex("#F2161A1F") || chrome != SeasonalArt.Hex("#FF161A1F") || source != SeasonalArt.Hex("#FF4F6272") || !cached || cachedSource != source)
					{
						bad.Add($"{mode}/{transparent}/{owns}: bg={bg} chrome={chrome} source={source} cached={cached}");
					}
				}
			}
		}
		Check("rest-taskbar", bad.Count == 0, string.Join(" ; ", bad));
		// Three colour modes over the bottom of the Dark rest wallpaper (all the same calm slate), then the real taskbar.
		BitmapSource wp = LoadPng(Path.Combine(outDir, "wallpaper-rest.png"));
		const int w = 1366;
		const int band = 40;
		const int strip = 200;
		(string Label, string Mode, bool Transparent)[] rows = new (string, string, bool)[3]
		{
			("Dark rest, your colour mode: from the desktop wallpaper, transparent", "Wallpaper", true),
			("Dark rest, colour mode: Start", "Start", false),
			("Dark rest, colour mode: Transparent", "Transparent", false)
		};
		double scale = w / (double)wp.PixelWidth;
		DrawingVisual dv = new DrawingVisual();
		using (DrawingContext dc = dv.RenderOpen())
		{
			for (int i = 0; i < rows.Length; i++)
			{
				AppSettings s = RestSettings(real);
				s.TaskbarColorMode = rows[i].Mode;
				s.TaskbarTransparent = rows[i].Transparent;
				SettingsStore.DiagnosticsOverride = s;
				TaskbarTheme.Invalidate();
				double y = i * (strip + 30);
				dc.DrawRectangle(Brushes.Black, null, new Rect(0, y, w, strip + 30));
				Label(dc, rows[i].Label, 8, y + 6, 13);
				dc.PushClip(new RectangleGeometry(new Rect(0, y + 30, w, strip)));
				dc.DrawImage(wp, new Rect(0, y + 30 + strip - wp.PixelHeight * scale, w, wp.PixelHeight * scale));
				dc.Pop();
				dc.DrawRectangle(TaskbarTheme.CurrentBackground(), null, new Rect(0, y + 30 + strip - band, w, band));
				Label(dc, "\u2756  Taskbar  9:41 PM", 12, y + 30 + strip - band + 10, 13);
			}
		}
		SettingsStore.DiagnosticsOverride = RestSettings(real);
		SeasonalTheme.DiagnosticsOwnsWallpaper = true;
		TaskbarWindow.RaiseTaskbarColorChanged(invalidateWallpaper: true);
		taskbar.UpdateLayout();
		RenderTargetBitmap bar = new RenderTargetBitmap((int)Math.Ceiling(taskbar.ActualWidth), (int)Math.Ceiling(taskbar.ActualHeight), 96.0, 96.0, PixelFormats.Pbgra32);
		bar.Render(taskbar);
		DrawingVisual all = new DrawingVisual();
		int total = 3 * (strip + 30) + 30 + bar.PixelHeight;
		using (DrawingContext dc = all.RenderOpen())
		{
			dc.DrawRectangle(Brushes.Black, null, new Rect(0, 0, w, total));
			dc.DrawDrawing(dv.Drawing);
			double y = 3 * (strip + 30);
			Label(dc, "The taskbar window itself (" + bar.PixelWidth + " x " + bar.PixelHeight + ")", 8, y + 6, 13);
			dc.DrawImage(bar, new Rect(0, y + 30, bar.PixelWidth, bar.PixelHeight));
		}
		SavePng(Render(all, w, total), Path.Combine(outDir, "taskbar-rest.png"));
	}

	private static void RestFlyouts(string outDir, TaskbarWindow taskbar)
	{
		string clock = Path.Combine(outDir, "flyout-clock-rest.png");
		taskbar.QaRenderClockCalendar(clock, FlyoutSample, 340.0, 500.0);
		// the rest tone rule, read back from the brushes the flyout theme wrote
		List<string> bad = new List<string>();
		List<string> got = new List<string>();
		void Want(string key, int stop, string want)
		{
			Brush? b = taskbar.QaResource(key);
			Color? c = b is LinearGradientBrush lg && stop < lg.GradientStops.Count ? lg.GradientStops[stop].Color : (b as SolidColorBrush)?.Color;
			string label = key + (stop > 0 || b is LinearGradientBrush ? "[" + stop + "]" : "");
			got.Add(label + "=" + (c.HasValue ? Hex(c.Value) : "none"));
			Color w = SeasonalArt.Hex(want);
			if (!c.HasValue || Math.Abs(c.Value.R - w.R) > 1 || Math.Abs(c.Value.G - w.G) > 1 || Math.Abs(c.Value.B - w.B) > 1)
			{
				bad.Add(label + " want " + want);
			}
		}
		Want("TB.ClockBody", 0, "#FF252B33");
		Want("TB.ClockBody", 1, "#FF13171B");
		Want("TB.ClockHeader", 0, "#FF323E48");
		Want("TB.ClockFooter", 0, "#FF161B20");
		Want("TB.ClockBorder", 0, "#FF647C91");
		Want("TB.ClockLink", 0, "#FF9FAFBC");
		Want("TB.ClockSelection", 0, "#FF688197");
		string volume = Path.Combine(outDir, "flyout-volume-rest.png");
		taskbar.QaRenderSoundFlyout(volume, 420.0, 540.0);
		string network = Path.Combine(outDir, "flyout-network-rest.png");
		taskbar.QaRenderNetworkFlyout(network, QaNetState, QaLocalNet, "203.0.113.42", 420.0, 500.0);
		Want("TB.NetworkState", 0, "#FF313C46");
		// the volume slider fill and the foreground button's line: the selection tone, not a near-white accent
		Want("TB.Accent", 0, "#FF688197");
		Want("Metro81.ScrollThumbBrushDark", 0, "#FF4A525B");
		Metrics["RestFlyoutTones"] = got;
		Check("rest-flyout-tones", bad.Count == 0, bad.Count == 0 ? string.Join(" ", got) : string.Join(" ; ", bad) + " | " + string.Join(" ", got));
		List<string> bodies = new List<string>();
		bool dark = true;
		foreach (string path in new string[3] { clock, volume, network })
		{
			// the body's left margin below the header (the body background itself; the tiles are checked by the tones)
			RegionStats body = Region(path, 1, 160, 11, 200);
			bodies.Add(Path.GetFileName(path) + ": " + body);
			dark &= body.L <= 0.22 && body.S <= 0.25;
		}
		Check("rest-flyouts-dark", dark, string.Join(" ; ", bodies));
	}

	private static void RestPanes(string outDir, StartScreen start, RestFixtures f)
	{
		string ac = Path.Combine(outDir, "actioncenter-rest.png");
		ActionCenter.QaRenderMetro(ac, 468.0, 860.0, compact: false, withNotifications: false, currentTheme: true);
		RegionStats panel = Region(ac, 20, 300, 420, 80);
		Check("rest-actioncenter", panel.L <= 0.16 && panel.S <= 0.25, panel.ToString());
		string search = Path.Combine(outDir, "searchpane-rest.png");
		f.Search.QaRender(search, RestQuery, 920.0);
		RegionStats searchPane = Region(search, 10, 700, 300, 200);
		// the empty part of the field, right of the query text
		RegionStats searchField = Region(search, 160, 100, 70, 30);
		// the empty field is darker than the pane around it, so it reads as a field even before anything is typed
		RegionStats besideField = Region(search, 10, 160, 280, 20);
		Check("rest-searchpane", searchPane.L <= 0.20 && searchPane.S <= 0.25 && searchField.L <= 0.20 && searchField.L <= besideField.L - 0.04,
			$"pane={searchPane} field={searchField} paneBesideField={besideField}");
		string settings = Path.Combine(outDir, "settingspane-rest.png");
		f.Settings.QaRender(settings, 1040.0);
		RegionStats settingsPane = Region(settings, 10, 880, 320, 140);
		Check("rest-settingspane", settingsPane.L <= 0.20 && settingsPane.S <= 0.25, settingsPane.ToString());
		string charms = Path.Combine(outDir, "charms-rest.png");
		f.Charms.QaRender(charms);
		RegionStats charmsBar = Region(charms, 0, 0, Math.Max(1, LoadPng(charms).PixelWidth), 40);
		Check("rest-charms", charmsBar.L <= 0.20 && charmsBar.S <= 0.25, charmsBar.ToString());
		string toast = Path.Combine(outDir, "toast-rest.png");
		RenderToast(toast);
		BitmapSource toastBmp = LoadPng(toast);
		// the card's left padding, full height (no text there)
		RegionStats toastCard = Region(toast, 2, 2, 12, toastBmp.PixelHeight - 4);
		Check("rest-toast", toastCard.L <= 0.16, toastCard.ToString());
		string osd = Path.Combine(outDir, "osd-rest.png");
		f.Osd.QaRenderCard(osd);
		BitmapSource osdBmp = LoadPng(osd);
		RegionStats osdCard = Region(osd, 0, 0, osdBmp.PixelWidth, 8);
		Check("rest-osd", osdCard.L <= 0.16, osdCard.ToString());
		string switcher = Path.Combine(outDir, "switcher-rest.png");
		AppSwitcher sw = new AppSwitcher();
		sw.QaRender(switcher);
		BitmapSource swBmp = LoadPng(switcher);
		// the panel margin left of the window list, from below the search box to above the Start bar
		RegionStats swPanel = Region(switcher, 0, 50, 8, swBmp.PixelHeight - 120);
		Check("rest-switcher", swPanel.L <= 0.16, swPanel.ToString());
		// Windows 7 Start menu: Rule 1 (it follows Windows dark mode) with the muted accent as its glass colour, over the
		// Dark rest desktop colour (not the harness's blue-green test backdrop)
		Color glass = SeasonalArt.Hex("#FF4F6272");
		string win7Path = Path.Combine(outDir, "win7start-rest.png");
		Color? pane = f.Win7.QaRenderPreset(win7Path, glass, SeasonalArt.Hex("#FF121519"));
		Color[] stops = Win7Palette.GlassStopColors(glass, dark: true, transparency: true, out _, out _);
		double maxS = stops.Max(c => Hsl(c.R, c.G, c.B).S);
		// the glass column right of the place links (no text there)
		RegionStats glassColumn = Region(win7Path, 388, 40, 16, 400);
		Check("rest-win7start", pane == Win7Palette.PaneDark && maxS <= 0.20 && glassColumn.S <= 0.20 && glassColumn.L <= 0.25,
			$"pane={(pane.HasValue ? Hex(pane.Value) : "none")} glassMaxS={maxS:F3} glassColumn={glassColumn}");
		// context menus and Metro controls: Rule 1, the dark token set
		Color menuBg = TaskbarContextMenu.QaMenuBackground();
		Color surface = (Application.Current.Resources["Metro81.MenuSurface"] as SolidColorBrush)?.Color ?? Colors.Transparent;
		Check("contextmenu-rest", menuBg == SeasonalArt.Hex("#FF2B2B2B") && surface == SeasonalArt.Hex("#FF2B2B2B"), $"menu={Hex(menuBg)} token={Hex(surface)}");
	}

	private static void RestPcSettings(string outDir, PcSettingsWindow pc)
	{
		string launcher = Path.Combine(outDir, "pcsettings-rest-launcher.png");
		pc.QaRenderTall(launcher, "Launcher");
		(int tiles, string selected) = pc.QaSeasonTiles();
		BitmapSource bmp = LoadPng(launcher);
		// the page right of the content column, full height
		RegionStats page = Region(launcher, 820, 0, 80, bmp.PixelHeight);
		Check("rest-pcsettings-launcher", tiles == 4 && selected == SeasonalTheme.RestTooltip && page.L <= 0.15,
			$"tiles={tiles} selected={selected} page={page}");
		string devices = Path.Combine(outDir, "pcsettings-rest-devices.png");
		pc.QaRender(devices, "PC & devices", 1366.0, 768.0);
		RegionStats right = Region(devices, 1000, 400, 360, 360);
		Check("rest-pcsettings-devices", right.L <= 0.15, right.ToString());
		// The controls of the dark page: the selected picker tile is framed clearly (3:1 or more on the page), On and Off
		// toggles read apart (3:1 between the fills), and the scrollbar thumb is a quiet blue-grey.
		Color pageBg = SeasonalArt.Hex("#FF1A1D21");
		(_, _, Color frame) = pc.QaSeasonTilesWithFrame();
		Color on = ((pc.TryFindResource("Metro81.ToggleSwitch") as Style)?.Resources["MenuAccent"] as SolidColorBrush)?.Color ?? Colors.Transparent;
		Color off = (pc.TryFindResource("Metro81.ToggleTrack") as SolidColorBrush)?.Color ?? Colors.Transparent;
		Color thumb = (pc.TryFindResource("Metro81.ScrollThumbBrush") as SolidColorBrush)?.Color ?? Colors.Transparent;
		Color field = (pc.TryFindResource("Metro81.Surface") as SolidColorBrush)?.Color ?? Colors.Transparent;
		double frameC = Contrast(frame, pageBg);
		double toggleC = Contrast(on, off);
		bool quietThumb = thumb.A == 255 && ColorMath.RelLuminance(thumb) <= 0.10 && Hsl(thumb.R, thumb.G, thumb.B).S <= 0.20;
		Check("rest-pcsettings-controls", frameC >= 3.0 && toggleC >= 3.0 && quietThumb && field == SeasonalArt.Hex("#FF23272C"),
			$"selectedFrame={Hex(frame)} {frameC:F2}:1 toggleOn={Hex(on)} off={Hex(off)} {toggleC:F2}:1 thumb={Hex(thumb)} field={Hex(field)}");
	}

	// Owned 8.1 windows: the white caption is replaced by the accent caption while Dark rest is on.
	private static void RestCaption(AppSettings real)
	{
		AppSettings rest = RestSettings(real);
		rest.Win81LightCaption = true;
		AppSettings normal = NormalSettings(real);
		normal.Win81LightCaption = true;
		Win81Window w = new Win81Window { Left = -4000.0, Top = -4000.0, ShowActivated = false };
		try
		{
			SettingsStore.DiagnosticsOverride = rest;
			w.RefreshAccent();
			Color restInk = (w.Resources["CaptionInk"] as SolidColorBrush)?.Color ?? Colors.Transparent;
			Color wantRest = ColorMath.ReadableInk(StartAccent.Color());
			SettingsStore.DiagnosticsOverride = normal;
			w.RefreshAccent();
			Color normalInk = (w.Resources["CaptionInk"] as SolidColorBrush)?.Color ?? Colors.Transparent;
			bool glass = ShellSkin.OwnedAeroOn;
			bool ok = glass || (restInk == wantRest && normalInk == Color.FromRgb(68, 68, 68));
			Check("caption-rest", ok, $"rest={Hex(restInk)} want={Hex(wantRest)} normal={Hex(normalInk)} ownedAero={glass}");
		}
		finally
		{
			SettingsStore.DiagnosticsOverride = RestSettings(real);
			w.Close();
		}
	}

	private static double Contrast(Color a, Color b)
	{
		double la = ColorMath.RelLuminance(a);
		double lb = ColorMath.RelLuminance(b);
		return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
	}

	// Mean HSL lightness and mean chroma (max - min channel, 0-255) of the pixels, and the HSL saturation of their average.
	private readonly record struct RegionStats(double L, double Chroma, double S)
	{
		public override string ToString()
		{
			return $"L={L:F3} chroma={Chroma:F1} s={S:F3}";
		}
	}

	private static RegionStats Region(string path, int x, int y, int w, int h)
	{
		BitmapSource bmp = LoadPng(path);
		x = Math.Clamp(x, 0, bmp.PixelWidth - 1);
		y = Math.Clamp(y, 0, bmp.PixelHeight - 1);
		w = Math.Clamp(w, 1, bmp.PixelWidth - x);
		h = Math.Clamp(h, 1, bmp.PixelHeight - y);
		byte[] px = Pixels(new CroppedBitmap(bmp, new Int32Rect(x, y, w, h)));
		double l = 0;
		double chroma = 0;
		int n = px.Length / 4;
		for (int i = 0; i < px.Length; i += 4)
		{
			int max = Math.Max(px[i], Math.Max(px[i + 1], px[i + 2]));
			int min = Math.Min(px[i], Math.Min(px[i + 1], px[i + 2]));
			l += (max + min) / 510.0;
			chroma += max - min;
		}
		(double r, double g, double b) = Average(px);
		return new RegionStats(l / n, chroma / n, Hsl(r, g, b).S);
	}
}
