using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Windows.Forms;
using System.Windows.Media;

namespace Win81Layer;

internal static class TaskbarDiagnostics
{
	public static void RunLayoutSelfTest()
	{
		List<object> checks = new List<object>();
		bool passed = true;
		passed &= CheckPlan(checks, "exact-fit", 4, 2, 272.0, 4, 2, false);
		passed &= CheckPlan(checks, "overflow", 16, 20, 1000.0, 16, 4, true);
		passed &= CheckPlan(checks, "task-reserved", 10, 1, 100.0, 0, 1, true);
		passed &= CheckPlan(checks, "tiny", 2, 2, 39.0, 0, 0, true);
		passed &= CheckPlan(checks, "empty", 0, 0, 0.0, 0, 0, false);
		passed &= CheckStartButtonContract(checks);

		foreach (Screen screen in Screen.AllScreens)
		{
			System.Drawing.Rectangle bounds = screen.Bounds;
			passed &= CheckArea(checks, screen, "Bottom", 40, bounds.Left, bounds.Top, bounds.Right, bounds.Bottom - 40);
			passed &= CheckArea(checks, screen, "Top", 40, bounds.Left, bounds.Top + 40, bounds.Right, bounds.Bottom);
			passed &= CheckArea(checks, screen, "Left", 40, bounds.Left + 40, bounds.Top, bounds.Right, bounds.Bottom);
			passed &= CheckArea(checks, screen, "Right", 40, bounds.Left, bounds.Top, bounds.Right - 40, bounds.Bottom);
		}
		object report = new
		{
			SchemaVersion = 3,
			Passed = passed,
			GeneratedUtc = DateTime.UtcNow,
			Screens = Screen.AllScreens.Length,
			ForeignCompositionHookActive = DesktopComposition.ForegroundHookActiveForDiagnostics,
			Checks = checks
		};
		string directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Win81Layer", "qa");
		Directory.CreateDirectory(directory);
		string path = Path.Combine(directory, "taskbar-layout-qa-latest.json");
		File.WriteAllText(path, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
		Logger.Log($"[taskbar-layout-qa] passed={passed} checks={checks.Count} report={path}");
		if (!passed)
		{
			throw new InvalidOperationException("Taskbar layout self-test failed. See " + path);
		}
	}

	private static bool CheckStartButtonContract(List<object> checks)
	{
		Color red = Color.FromRgb(196, 31, 58);
		Color teal = Color.FromRgb(0, 174, 184);
		bool profileRules = TaskbarWindow.UsesMetroStartFrame("windows81")
			&& TaskbarWindow.UsesMetroStartFrame("native")
			&& TaskbarWindow.UsesMetroStartFrame("alchemy-enhanced")
			&& !TaskbarWindow.UsesMetroStartFrame("windows7-aero");
		bool idleTransparent = TaskbarWindow.StartSurfaceTone("windows81", "normal") == Colors.Transparent
			&& TaskbarWindow.StartSurfaceTone("windows81", "hover") == Colors.Transparent
			&& TaskbarWindow.StartSurfaceTone("native", "normal") == Colors.Transparent
			&& TaskbarWindow.StartSurfaceTone("alchemy-enhanced", "hover") == Colors.Transparent;
		bool activeBlack = TaskbarWindow.StartSurfaceTone("windows81", "pressed") == Colors.Black
			&& TaskbarWindow.StartSurfaceTone("windows81", "open") == Colors.Black
			&& TaskbarWindow.StartSurfaceTone("native", "pressed") == Colors.Black
			&& TaskbarWindow.StartSurfaceTone("alchemy-enhanced", "open") == Colors.Black
			&& TaskbarWindow.StartSurfaceTone("windows7-aero", "pressed") == Colors.Transparent
			&& TaskbarWindow.StartSurfaceTone("windows7-aero", "open") == Colors.Transparent;
		bool themeAccent = TaskbarWindow.StartGlyphTone("windows81", red, false, false, false) == red
			&& TaskbarWindow.StartGlyphTone("windows81", teal, false, false, false) == teal
			&& red != teal;
		bool interactionTheme = TaskbarWindow.StartGlyphTone("windows81", red, true, false, false)
			!= TaskbarWindow.StartGlyphTone("windows81", teal, true, false, false)
			&& TaskbarWindow.StartGlyphTone("windows81", red, false, false, true)
			!= TaskbarWindow.StartGlyphTone("windows81", teal, false, false, true)
			&& TaskbarWindow.StartGlyphTone("windows7-aero", red, false, false, false) == Colors.White;
		bool passed = profileRules && idleTransparent && activeBlack && themeAccent && interactionTheme
			&& TaskbarMetrics.StartGlyphSize is >= 18.0 and <= 28.0;
		checks.Add(new
		{
			Name = "start-button-profile-theme-contract",
			Passed = passed,
			MetroProfilesUsePressFrame = profileRules,
			Windows7KeepsOrbSurface = !TaskbarWindow.UsesMetroStartFrame("windows7-aero"),
			IdleAndHoverAreTransparent = idleTransparent,
			PressedAndOpenAreOpaqueBlack = activeBlack,
			GlyphUsesExactStartThemeAccent = themeAccent,
			InteractionGlyphTracksThemeAccent = interactionTheme,
			StartGlyphSizeDiu = TaskbarMetrics.StartGlyphSize,
			RedAccent = red.ToString(),
			TealAccent = teal.ToString()
		});
		return passed;
	}

	private static bool CheckPlan(List<object> checks, string name, int pins, int tasks, double available,
		int expectedPins, int expectedTasks, bool expectedOverflow)
	{
		TaskbarLayoutPlan actual = TaskbarLayoutPlanner.Compute(pins, tasks, available);
		bool bounded = actual.VisiblePins * TaskbarLayoutPlanner.PinSlot
			+ actual.VisibleTasks * TaskbarLayoutPlanner.TaskSlot
			+ (actual.HasOverflow ? TaskbarLayoutPlanner.OverflowSlot : 0.0) <= available + 0.5
			|| available < TaskbarLayoutPlanner.OverflowSlot;
		bool passed = actual.VisiblePins == expectedPins && actual.VisibleTasks == expectedTasks
			&& actual.HasOverflow == expectedOverflow && bounded;
		checks.Add(new
		{
			Name = name,
			Passed = passed,
			Pins = pins,
			Tasks = tasks,
			Available = available,
			Actual = actual,
			Bounded = bounded
		});
		return passed;
	}

	private static bool CheckArea(List<object> checks, Screen screen, string edge, int thickness,
		int left, int top, int right, int bottom)
	{
		System.Drawing.Rectangle actual = TaskbarWorkArea.Expected(screen, edge, thickness);
		bool passed = actual.Left == left && actual.Top == top && actual.Right == right && actual.Bottom == bottom;
		checks.Add(new
		{
			Name = "work-area-" + screen.DeviceName + "-" + edge,
			Passed = passed,
			Actual = actual.ToString(),
			Expected = $"{left},{top},{right},{bottom}"
		});
		return passed;
	}
}
