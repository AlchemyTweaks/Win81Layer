using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;

namespace Win81Layer;

public sealed class ClockCalendarQaMetrics
{
	public double WidthDiu { get; set; }
	public double HeightDiu { get; set; }
	public int CalendarColumns { get; set; }
	public int CalendarRows { get; set; }
	public int DayVisuals { get; set; }
	public int VisibleDays { get; set; }
	public string SelectedDate { get; set; } = string.Empty;
	public string MonthTitle { get; set; } = string.Empty;
	public string ClockText { get; set; } = string.Empty;
	public bool ThemeBackgroundSynchronized { get; set; }
	public bool ThemeSwitchPassed { get; set; }
	public string ThemeAccent { get; set; } = string.Empty;
	public long InitialVisualBuildMs { get; set; }
	public long RenderAndLayoutMs { get; set; }
	public bool TimerActiveDuringDetachedRender { get; set; }
	public string Screenshot { get; set; } = string.Empty;
}

internal static class ClockCalendarDiagnostics
{
	private static readonly string[] StateNames = { "settings.json", "profile.json", "taskbar-pins.json", "shell-profile-state.json" };

	public static void Begin(Application app)
	{
		PersistenceDiagnostics.SuppressAutomaticPostBootVerification = true;
		app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
		app.Dispatcher.BeginInvoke((Action)delegate
		{
			string qaRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Win81Layer", "qa");
			Directory.CreateDirectory(qaRoot);
			string screenshot = Path.Combine(qaRoot, "CLOCK-CALENDAR-QA-LATEST.png");
			string compactScreenshot = Path.Combine(qaRoot, "CLOCK-CALENDAR-QA-COMPACT.png");
			string reportPath = Path.Combine(qaRoot, "clock-calendar-qa-latest.json");
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
				DateTime reference = new DateTime(2026, 8, 31, 17, 17, 6, DateTimeKind.Local);
				ClockCalendarQaMetrics metrics = taskbar.QaRenderClockCalendar(screenshot, reference, 340.0, 500.0);
				ClockCalendarQaMetrics compactMetrics = taskbar.QaRenderClockCalendar(compactScreenshot, reference, 310.0, 440.0);
				Dictionary<string, string> after = HashMutableState();
				bool stateUnchanged = before.Count == after.Count && before.All(pair => after.TryGetValue(pair.Key, out string value) && value == pair.Value);
				FileInfo image = new FileInfo(screenshot);
				FileInfo compactImage = new FileInfo(compactScreenshot);
				var checks = new Dictionary<string, bool>
				{
					["reference-layout-340x500"] = metrics.WidthDiu == 340.0 && metrics.HeightDiu == 500.0,
					["calendar-grid-7x7"] = metrics.CalendarColumns == 7 && metrics.CalendarRows == 7,
					["calendar-visuals-preallocated"] = metrics.DayVisuals == 42,
					["reference-month-has-31-days"] = metrics.VisibleDays == 31,
					["reference-date-selected"] = metrics.SelectedDate == "2026-08-31",
					["live-seconds-separated"] = metrics.ClockText == "17:17:06",
					["compact-layout-310x440"] = compactMetrics.WidthDiu == 310.0 && compactMetrics.HeightDiu == 440.0,
					["compact-month-complete"] = compactMetrics.VisibleDays == 31 && compactMetrics.DayVisuals == 42,
					["start-theme-background-synchronized"] = metrics.ThemeBackgroundSynchronized && compactMetrics.ThemeBackgroundSynchronized,
					["red-blue-theme-switch-is-not-hardcoded"] = metrics.ThemeSwitchPassed && compactMetrics.ThemeSwitchPassed,
					["initial-visual-build-under-100ms"] = metrics.InitialVisualBuildMs <= 100,
					["render-and-layout-under-1000ms"] = metrics.RenderAndLayoutMs <= 1000,
					["no-detached-idle-timer"] = !metrics.TimerActiveDuringDetachedRender,
					["screenshot-present"] = image.Exists && image.Length > 10000,
					["compact-screenshot-present"] = compactImage.Exists && compactImage.Length > 10000,
					["mutable-state-unchanged"] = stateUnchanged
				};
				bool passed = checks.Values.All(value => value);
				var report = new
				{
					SchemaVersion = 2,
					GeneratedUtc = DateTime.UtcNow,
					Passed = passed,
					Checks = checks,
					Metrics = metrics,
					CompactMetrics = compactMetrics,
					TaskbarWindowStartupMs = startup.ElapsedMilliseconds,
					ScreenshotSha256 = image.Exists ? Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(screenshot))) : null,
					CompactScreenshotSha256 = compactImage.Exists ? Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(compactScreenshot))) : null,
					MutableStateUnchanged = stateUnchanged,
					DesignContract = new
					{
						Style = "Windows 8.1 Metro clock and calendar flyout",
						Placement = "monitor-aware custom taskbar-edge placement",
						Motion = "shared Motion edge/micro categories",
						Theme = "header, body, footer, border, selection and settings link are regenerated from the active Start background/accent",
						SettingsUri = "ms-settings:dateandtime",
						IdleCost = "no clock timer while closed"
					}
				};
				File.WriteAllText(reportPath, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
				Logger.Log($"CLOCK-CALENDAR-QA: passed={passed}, build={metrics.InitialVisualBuildMs}ms, render={metrics.RenderAndLayoutMs}ms -> {reportPath}");
				app.Shutdown(passed ? 0 : 1);
			}
			catch (Exception ex)
			{
				File.WriteAllText(reportPath, JsonSerializer.Serialize(new { SchemaVersion = 2, GeneratedUtc = DateTime.UtcNow, Passed = false, Error = ex.ToString() }, new JsonSerializerOptions { WriteIndented = true }));
				Logger.Log("CLOCK-CALENDAR-QA failed: " + ex);
				app.Shutdown(1);
			}
			finally
			{
				taskbar?.StopBar();
			}
		}, DispatcherPriority.Loaded);
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
