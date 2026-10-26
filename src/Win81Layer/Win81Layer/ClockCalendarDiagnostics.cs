using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using Color = System.Windows.Media.Color;
using Rectangle = System.Drawing.Rectangle;

namespace Win81Layer;

public sealed class ClockCalendarQaMetrics
{
	public double WidthDiu { get; set; }
	public double HeightDiu { get; set; }
	public double ExpectedHeightDiu { get; set; }
	public double MaxHeightDiu { get; set; }
	public double WeekRowDiu { get; set; }
	public int CalendarColumns { get; set; }
	public int CalendarRows { get; set; }
	public bool CalendarRowsExact { get; set; }
	public int DayVisuals { get; set; }
	public int VisibleDays { get; set; }
	public string SelectedDate { get; set; } = string.Empty;
	public bool SelectedFillIsSelection { get; set; }
	public Rect SelectedDayRect { get; set; }
	public Rect SelectedDayTextRect { get; set; }
	public bool NoTodayOutlineWhenSelected { get; set; }
	public bool TodayOutlineWhenNotSelected { get; set; }
	public string MonthTitle { get; set; } = string.Empty;
	public string PreviousMonthTitle { get; set; } = string.Empty;
	public string NextMonthTitle { get; set; } = string.Empty;
	public string ExpectedPreviousTitle { get; set; } = string.Empty;
	public string ExpectedNextTitle { get; set; } = string.Empty;
	public bool HeightStableAcrossMonths { get; set; }
	public string ClockText { get; set; } = string.Empty;
	public bool TimeBaselinesShared { get; set; }
	public bool TypographyTwelve { get; set; }
	public double HeaderLeftDiu { get; set; }
	public double HeaderRightDiu { get; set; }
	public double NavLeftDiu { get; set; }
	public double NavRightDiu { get; set; }
	public double NavHeightDiu { get; set; }
	public double GridFillLeftDiu { get; set; }
	public double GridFillRightDiu { get; set; }
	public double GutterDiu { get; set; }
	public Rect FooterRect { get; set; }
	public Rect FooterGearRect { get; set; }
	public double FooterLabelLeftDiu { get; set; }
	public string BodyTop { get; set; } = string.Empty;
	public string BodyBottom { get; set; } = string.Empty;
	public string FooterColor { get; set; } = string.Empty;
	public string BorderColor { get; set; } = string.Empty;
	public string LinkColor { get; set; } = string.Empty;
	public string ValueInkColor { get; set; } = string.Empty;
	public string SecondaryInkColor { get; set; } = string.Empty;
	public bool LinkIsPanelLink { get; set; }
	public double LinkContrast { get; set; }
	public string SelectionColor { get; set; } = string.Empty;
	public double SelectionContrast { get; set; }
	public double SelectionOnBodyContrast { get; set; }
	public double SecondaryInkContrast { get; set; }
	public double InkOnFooterContrast { get; set; }
	public bool ThemeBackgroundSynchronized { get; set; }
	public bool ThemeSwitchPassed { get; set; }
	public bool SeasonalBodyPassed { get; set; }
	public string ThemeAccent { get; set; } = string.Empty;
	public bool FrameStatic { get; set; }
	public bool PrepareTouchesContentOnly { get; set; }
	public Rect BodyProbe { get; set; }
	public long InitialVisualBuildMs { get; set; }
	public long RenderAndLayoutMs { get; set; }
	public bool TimerActiveDuringDetachedRender { get; set; }
	public string Screenshot { get; set; } = string.Empty;
}

// --clock-calendar-test [outDir]: offscreen QA of the compact clock and calendar flyout. Read-only (settings writes throw),
// never shows the flyout on screen, exits by itself.
internal static class ClockCalendarDiagnostics
{
	private static readonly string[] StateNames = { "settings.json", "profile.json", "taskbar-pins.json", "shell-profile-state.json" };

	public static void Begin(Application app, string[] args = null)
	{
		PersistenceDiagnostics.SuppressAutomaticPostBootVerification = true;
		app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
		args ??= Array.Empty<string>();
		int idx = Array.IndexOf(args, "--clock-calendar-test");
		string qaRoot = (idx >= 0 && idx + 1 < args.Length && !args[idx + 1].StartsWith("--", StringComparison.Ordinal))
			? args[idx + 1]
			: Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Win81Layer", "qa");
		app.Dispatcher.BeginInvoke((Action)delegate
		{
			Directory.CreateDirectory(qaRoot);
			string P(string name) => Path.Combine(qaRoot, name);
			string screenshot = P("CLOCK-CALENDAR-QA-LATEST.png");
			string reportPath = P("clock-calendar-qa-latest.json");
			Dictionary<string, string> before = HashMutableState();
			TaskbarWindow taskbar = null;
			try
			{
				Stopwatch startup = Stopwatch.StartNew();
				taskbar = new TaskbarWindow
				{
					Left = -4000.0,
					Top = -4000.0,
					Width = 900.0,
					Height = 48.0
				};
				taskbar.Show();
				startup.Stop();
				// a 31-day month with six week rows in most cultures (it starts on a Saturday)
				DateTime reference = new DateTime(2026, 8, 31, 17, 17, 6, DateTimeKind.Local);
				ClockCalendarQaMetrics metrics = taskbar.QaRenderClockCalendar(screenshot, reference);
				ClockCalendarQaMetrics narrow = taskbar.QaRenderClockCalendar(P("CLOCK-CALENDAR-QA-NARROW.png"), reference, ActionCenter.PanelWidthFor(332.0));
				ClockCalendarQaMetrics clamped330 = taskbar.QaRenderClockCalendar(P("CLOCK-CALENDAR-QA-CLAMPED-330.png"), reference, maxHeightDiu: 330.0);
				ClockCalendarQaMetrics clamped300 = taskbar.QaRenderClockCalendar(P("CLOCK-CALENDAR-QA-CLAMPED-300.png"), reference, maxHeightDiu: 300.0);
				// the 15th selected while today is the 31st: today's outline
				ClockCalendarQaMetrics midMonth = taskbar.QaRenderClockCalendar(P("CLOCK-CALENDAR-QA-TODAY-OUTLINE.png"), reference, selectDate: new DateTime(2026, 8, 15));
				Dictionary<string, ClockCalendarQaMetrics> looks = new Dictionary<string, ClockCalendarQaMetrics>(StringComparer.Ordinal);
				foreach ((string name, Color themeBase, Color accent, bool seasonal) in FlyoutLooks.All)
				{
					looks[name] = taskbar.QaRenderClockCalendar(P("CLOCK-CALENDAR-QA-LOOK-" + name.ToUpperInvariant() + ".png"), reference, tones: (themeBase, accent, seasonal));
				}
				bool realDark = ShellTheme.IsDark;
				ShellTheme.ForceForTest(false);
				taskbar.QaRenderClockCalendar(P("CLOCK-CALENDAR-QA-LIVE-LIGHT.png"), reference);
				ShellTheme.ForceForTest(true);
				taskbar.QaRenderClockCalendar(P("CLOCK-CALENDAR-QA-LIVE-DARK.png"), reference);
				ShellTheme.ForceForTest(realDark);
				(int lightDarkDiff, int lightDarkTotal) = FlyoutQa.PixelDiff(P("CLOCK-CALENDAR-QA-LIVE-LIGHT.png"), P("CLOCK-CALENDAR-QA-LIVE-DARK.png"));
				(bool contrastOk, List<string> contrast) = FlyoutQa.LookContrast(looks.Append(new KeyValuePair<string, ClockCalendarQaMetrics>("live", metrics))
					.ToDictionary(l => l.Key, l => (l.Value.BodyTop, l.Value.BodyBottom, l.Value.FooterColor, l.Value.LinkColor, l.Value.SecondaryInkColor, l.Value.ValueInkColor)));
				(bool placementOk, List<string> placement) = PlacementChecks();
				// the keys through the input hook's route (the flyout never takes the foreground)
				(bool keysOk, string[] keyTrace) = taskbar.QaProbeClockKeys(reference);
				Dictionary<string, string> after = HashMutableState();
				bool stateUnchanged = before.Count == after.Count && before.All(pair => after.TryGetValue(pair.Key, out string value) && value == pair.Value);
				ClockCalendarQaMetrics[] all = new[] { metrics, narrow, clamped330, clamped300, midMonth }.Concat(looks.Values).ToArray();
				string[] shots = { screenshot, P("CLOCK-CALENDAR-QA-NARROW.png"), P("CLOCK-CALENDAR-QA-CLAMPED-330.png"), P("CLOCK-CALENDAR-QA-CLAMPED-300.png"), P("CLOCK-CALENDAR-QA-TODAY-OUTLINE.png") };
				bool Edge(double v, double want) => Math.Abs(v - want) < 0.75;
				var checks = new Dictionary<string, bool>
				{
					// 177 + 6R: 357 at R = 30 (the Action Center's laptop panel)
					["layout-376x357"] = metrics.WidthDiu == 376.0 && metrics.HeightDiu == 357.0 && metrics.WeekRowDiu == 30.0,
					["narrow-320x357"] = narrow.WidthDiu == 320.0 && narrow.HeightDiu == 357.0,
					["clamped-rows-compress"] = clamped330.WeekRowDiu == 25.0 && clamped330.HeightDiu == 327.0 && clamped300.WeekRowDiu == 24.0 && clamped300.HeightDiu == 321.0
						&& all.All(m => Math.Abs(m.HeightDiu - m.ExpectedHeightDiu) < 0.5),
					["calendar-grid-7x7-six-week-rows"] = all.All(m => m.CalendarColumns == 7 && m.CalendarRows == 7 && m.CalendarRowsExact),
					["calendar-visuals-preallocated"] = metrics.DayVisuals == 42,
					["reference-month-has-31-days"] = metrics.VisibleDays == 31,
					["reference-date-selected-with-selection-fill"] = metrics.SelectedDate == "2026-08-31" && metrics.SelectedFillIsSelection,
					["today-outline-only-when-not-selected"] = metrics.NoTodayOutlineWhenSelected && metrics.TodayOutlineWhenNotSelected && midMonth.NoTodayOutlineWhenSelected,
					["live-seconds-separated"] = metrics.ClockText == "17:17:06",
					["time-and-seconds-share-a-baseline"] = metrics.TimeBaselinesShared,
					["typography-12px-except-the-time"] = all.All(m => m.TypographyTwelve),
					["month-navigation-keeps-height"] = metrics.PreviousMonthTitle == metrics.ExpectedPreviousTitle && metrics.NextMonthTitle == metrics.ExpectedNextTitle && metrics.HeightStableAcrossMonths,
					["edges-12-and-364"] = Edge(metrics.HeaderLeftDiu, 12.0) && Edge(metrics.HeaderRightDiu, 364.0) && Edge(metrics.NavLeftDiu, 12.0) && Edge(metrics.NavRightDiu, 364.0)
						&& Edge(metrics.GridFillLeftDiu, 12.0) && Edge(metrics.GridFillRightDiu, 364.0) && Edge(metrics.GutterDiu, 4.0) && Edge(metrics.NavHeightDiu, 32.0),
					["footer-matches-action-center"] = Edge(metrics.FooterRect.Height, 41.0) && Edge(metrics.FooterGearRect.Left, 20.0) && Edge(metrics.FooterGearRect.Width, 16.0)
						&& Edge(metrics.FooterLabelLeftDiu, 52.0) && Edge(metrics.FooterRect.Bottom, 356.0),
					["link-is-panel-link-4.5"] = all.All(m => m.LinkIsPanelLink && m.LinkContrast >= 4.5),
					// the number at 4.5:1 on its fill, and the fill (also today's outline) at 3:1 against both body stops
					["selection-4.5-against-day-ink-3-against-body"] = all.All(m => m.SelectionContrast >= 4.5 && m.SelectionOnBodyContrast >= 3.0),
					["secondary-ink-4.5"] = all.All(m => m.SecondaryInkContrast >= 4.5 && m.InkOnFooterContrast >= 4.5),
					["action-center-tones"] = metrics.ThemeBackgroundSynchronized && looks.Values.All(m => m.ThemeBackgroundSynchronized),
					["red-blue-theme-switch-is-not-hardcoded"] = metrics.ThemeSwitchPassed,
					["season-keeps-base-hue"] = metrics.SeasonalBodyPassed,
					["normal-ignores-windows-light-dark"] = lightDarkTotal > 0 && lightDarkDiff == 0,
					["inks-readable-on-every-look"] = contrastOk,
					["motion-frame-static-content-moves"] = all.All(m => m.FrameStatic && m.PrepareTouchesContentOnly) && MotionContract(),
					["keys-through-the-input-hook"] = keysOk,
					["placement-equals-action-center"] = placementOk,
					["initial-visual-build-under-100ms"] = metrics.InitialVisualBuildMs <= 100,
					["render-and-layout-under-1000ms"] = all.All(m => m.RenderAndLayoutMs <= 1000),
					["no-detached-idle-timer"] = all.All(m => !m.TimerActiveDuringDetachedRender),
					["screenshots-present"] = shots.All(s => new FileInfo(s).Exists && new FileInfo(s).Length > 5000),
					["mutable-state-unchanged"] = stateUnchanged
				};
				bool passed = checks.Values.All(value => value);
				var report = new
				{
					SchemaVersion = 3,
					GeneratedUtc = DateTime.UtcNow,
					Passed = passed,
					Failed = checks.Where(c => !c.Value).Select(c => c.Key).ToArray(),
					Checks = checks,
					Placement = placement,
					Keys = keyTrace,
					LookContrast = contrast,
					LightVsDarkDifferingPixels = lightDarkDiff,
					Metrics = metrics,
					NarrowMetrics = narrow,
					Clamped330Metrics = clamped330,
					Clamped300Metrics = clamped300,
					TodayOutlineMetrics = midMonth,
					LookMetrics = looks,
					TaskbarWindowStartupMs = startup.ElapsedMilliseconds,
					ScreenshotSha256 = FlyoutQa.HashFile(screenshot),
					MutableStateUnchanged = stateUnchanged,
					DesignContract = new
					{
						Style = "Compact quick-settings clock and calendar flyout in the Action Center family",
						Layout = "376 x 357 DIP (177 + 6 week rows of 30; the rows compress to 24 on a short work area): the time (Light 32/15), the date, the month row, the weekdays, six week rows, the settings footer",
						Placement = "the Action Center's PlacePanel: tray end, 6 DIP from the bar and the screen edge, per edge, work-area and mixed-DPI aware",
						Motion = "the frame appears at once; the content rises 12 DIP over 250 ms with a 120 ms fade; closes with a 100 ms fade and 6 DIP",
						Theme = "ActionCenter.NormalTones for the same theme; Dark rest the panel's rest set; the selection raised until it stands at 3:1 against the body with its number (white or dark) at 4.5:1",
						Keys = "through the input hook while open (the flyout never takes the foreground): Escape closes; arrows a day / a week; PageUp / PageDown a month; Home today",
						SettingsUri = "ms-settings:dateandtime",
						IdleCost = "no clock timer while closed"
					}
				};
				File.WriteAllText(reportPath, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true, NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals }));
				Logger.Log($"CLOCK-CALENDAR-QA: passed={passed}, build={metrics.InitialVisualBuildMs}ms, render={metrics.RenderAndLayoutMs}ms -> {reportPath}");
				app.Shutdown(passed ? 0 : 1);
			}
			catch (Exception ex)
			{
				File.WriteAllText(reportPath, JsonSerializer.Serialize(new { SchemaVersion = 3, GeneratedUtc = DateTime.UtcNow, Passed = false, Error = ex.ToString() }, new JsonSerializerOptions { WriteIndented = true, NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals }));
				Logger.Log("CLOCK-CALENDAR-QA failed: " + ex);
				app.Shutdown(1);
			}
			finally
			{
				taskbar?.StopBar();
			}
		}, DispatcherPriority.Loaded);
	}

	// The flyouts' motion tokens (the Action Center's): open 12 DIP / 250 ms / 120 ms fade, close 6 DIP / 100 ms / 100 ms fade,
	// calm 6 / 3 DIP.
	private static bool MotionContract()
	{
		return Motion.DistanceFor(12.0, MotionMode.Authentic) == 12.0 && Motion.DistanceFor(6.0, MotionMode.Authentic) == 6.0
			&& Motion.DistanceFor(12.0, MotionMode.Reduced) == 6.0 && Motion.DistanceFor(6.0, MotionMode.Reduced) == 3.0
			&& (int)Motion.TokMs(Motion.Move(12.0), MotionMode.Authentic) == 250 && (int)Motion.TokMs(Motion.Tok.FadeIn, MotionMode.Authentic) == 120
			&& (int)Motion.TokMs(Motion.Tok.ExitMove, MotionMode.Authentic) == 100 && (int)Motion.TokMs(Motion.Tok.FadeOut, MotionMode.Authentic) == 100;
	}

	// The tray flyouts land exactly where the Action Center's PlacePanel puts a panel of their size: every edge, 100 % and
	// 150 %, with and without the bar's rect, and on a secondary monitor left of the primary (negative x). The target (the
	// clock or the volume icon) may sit anywhere on the bar.
	private static (bool, List<string>) PlacementChecks()
	{
		List<string> rows = new List<string>();
		bool ok = true;
		void Case(string name, string edge, Rectangle work, Rectangle? bar, double scale, Rectangle monitor, Point target)
		{
			foreach (Size size in new[] { new Size(376.0, 357.0), new Size(376.0, 199.0), new Size(320.0, 311.0) })
			{
				Rectangle got = TaskbarWindow.QaFlyoutPlacement(edge, work, bar, scale, size, monitor, target);
				Rectangle want = ActionCenter.PlacePanel(work, bar, edge, scale, size.Width, size.Height, monitor).Px;
				bool hit = got == want;
				ok &= hit;
				rows.Add($"{(hit ? "PASS" : "FAIL")} {name} {size.Width}x{size.Height}: got {got.X},{got.Y} {got.Width}x{got.Height}; want {want.X},{want.Y} {want.Width}x{want.Height}");
			}
		}
		Rectangle full = Rectangle.FromLTRB(0, 0, 1920, 1080);
		Case("bottom", "Bottom", Rectangle.FromLTRB(0, 0, 1920, 1040), null, 1.0, full, new Point(1800, 1046));
		Case("bottom with bar", "Bottom", Rectangle.FromLTRB(0, 0, 1920, 1040), Rectangle.FromLTRB(0, 1040, 1920, 1080), 1.0, full, new Point(1800, 1046));
		Case("top", "Top", Rectangle.FromLTRB(0, 40, 1920, 1080), null, 1.0, full, new Point(1800, 6));
		Case("top with bar", "Top", Rectangle.FromLTRB(0, 40, 1920, 1080), Rectangle.FromLTRB(0, 0, 1920, 40), 1.0, full, new Point(1800, 6));
		Case("left", "Left", Rectangle.FromLTRB(48, 0, 1920, 1080), null, 1.0, full, new Point(6, 1000));
		Case("left with bar", "Left", Rectangle.FromLTRB(48, 0, 1920, 1080), Rectangle.FromLTRB(0, 0, 48, 1080), 1.0, full, new Point(6, 1000));
		Case("right", "Right", Rectangle.FromLTRB(0, 0, 1872, 1080), null, 1.0, full, new Point(1878, 1000));
		Case("right with bar", "Right", Rectangle.FromLTRB(0, 0, 1872, 1080), Rectangle.FromLTRB(1872, 0, 1920, 1080), 1.0, full, new Point(1878, 1000));
		Case("150% bottom", "Bottom", Rectangle.FromLTRB(0, 0, 2880, 1560), null, 1.5, Rectangle.FromLTRB(0, 0, 2880, 1620), new Point(2700, 1570));
		Case("150% bottom with bar", "Bottom", Rectangle.FromLTRB(0, 0, 2880, 1560), Rectangle.FromLTRB(0, 1560, 2880, 1620), 1.5, Rectangle.FromLTRB(0, 0, 2880, 1620), new Point(2700, 1570));
		Case("150% top", "Top", Rectangle.FromLTRB(0, 60, 2880, 1620), null, 1.5, Rectangle.FromLTRB(0, 0, 2880, 1620), new Point(2700, 9));
		Case("150% left", "Left", Rectangle.FromLTRB(72, 0, 2880, 1620), Rectangle.FromLTRB(0, 0, 72, 1620), 1.5, Rectangle.FromLTRB(0, 0, 2880, 1620), new Point(9, 1500));
		Case("150% right", "Right", Rectangle.FromLTRB(0, 0, 2808, 1620), null, 1.5, Rectangle.FromLTRB(0, 0, 2880, 1620), new Point(2817, 1500));
		Case("secondary monitor (negative x), bottom", "Bottom", Rectangle.FromLTRB(-1920, 0, 0, 1040), Rectangle.FromLTRB(-1920, 1040, 0, 1080), 1.0, Rectangle.FromLTRB(-1920, 0, 0, 1080), new Point(-120, 1046));
		Case("secondary monitor (negative x) at 150%, top", "Top", Rectangle.FromLTRB(-2880, 60, 0, 1620), Rectangle.FromLTRB(-2880, 0, 0, 60), 1.5, Rectangle.FromLTRB(-2880, 0, 0, 1620), new Point(-200, 9));
		Case("auto-hide, bottom (work area = monitor, bar shown)", "Bottom", full, Rectangle.FromLTRB(0, 1040, 1920, 1080), 1.0, full, new Point(1800, 1046));
		// the week rows for the tallest the flyout may be (R = 30, else floor((max - 177) / 6) within 24..30)
		bool rowsOk = TaskbarWindow.ClockWeekRowFor(double.PositiveInfinity) == 30 && TaskbarWindow.ClockWeekRowFor(357.0) == 30
			&& TaskbarWindow.ClockWeekRowFor(330.0) == 25 && TaskbarWindow.ClockWeekRowFor(300.0) == 24 && TaskbarWindow.ClockWeekRowFor(200.0) == 24;
		ok &= rowsOk;
		rows.Add($"{(rowsOk ? "PASS" : "FAIL")} week rows for the available height");
		return (ok, rows);
	}

	private static Dictionary<string, string> HashMutableState()
	{
		string root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Win81Layer");
		Dictionary<string, string> hashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
		foreach (string name in StateNames)
		{
			string path = Path.Combine(root, name);
			if (File.Exists(path))
			{
				hashes[name] = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
			}
		}
		return hashes;
	}
}
