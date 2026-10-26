using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Color = System.Windows.Media.Color;

namespace Win81Layer;

// --action-center-test [outDir]: offscreen QA of the compact Quick settings Action Center. Read-only (settings writes throw),
// never shows the panel on screen, never sends input, never touches Windows' notifications; exits by itself.
internal static class ActionCenterDiagnostics
{
	private static readonly string[] StateNames = { "settings.json", "profile.json", "taskbar-pins.json", "shell-profile-state.json" };

	public static void Begin(Application app, string[] args = null)
	{
		PersistenceDiagnostics.SuppressAutomaticPostBootVerification = true;
		app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
		args ??= Array.Empty<string>();
		int idx = Array.IndexOf(args, "--action-center-test");
		string qaRoot = (idx >= 0 && idx + 1 < args.Length && !args[idx + 1].StartsWith("--", StringComparison.Ordinal))
			? args[idx + 1]
			: Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Win81Layer", "qa");
		app.Dispatcher.BeginInvoke((Action)delegate
		{
			Directory.CreateDirectory(qaRoot);
			string laptopShot = Path.Combine(qaRoot, "ACTION-CENTER-QA-LAPTOP.png");
			string desktopShot = Path.Combine(qaRoot, "ACTION-CENTER-QA-DESKTOP.png");
			string clampedShot = Path.Combine(qaRoot, "ACTION-CENTER-QA-CLAMPED.png");
			string lightShot = Path.Combine(qaRoot, "ACTION-CENTER-QA-LIVE-LIGHT.png");
			string darkShot = Path.Combine(qaRoot, "ACTION-CENTER-QA-LIVE-DARK.png");
			string reportPath = Path.Combine(qaRoot, "action-center-qa-latest.json");
			Dictionary<string, string> stateBefore = HashMutableState();
			try
			{
				ActionCenterQaMetrics laptop = ActionCenter.QaRenderMetro(laptopShot, ActionCenterQaDevice.Laptop);
				ActionCenterQaMetrics desktop = ActionCenter.QaRenderMetro(desktopShot, ActionCenterQaDevice.Desktop);
				ActionCenterQaMetrics clamped = ActionCenter.QaRenderMetro(clampedShot, ActionCenterQaDevice.Laptop, maxHeightDip: 260.0);
				// The live Start theme with Windows in light mode and in dark mode: the Metro panel must not change.
				bool realDark = ShellTheme.IsDark;
				ShellTheme.ForceForTest(false);
				ActionCenterQaMetrics live = ActionCenter.QaRenderMetro(lightShot, ActionCenterQaDevice.Desktop, currentTheme: true);
				ShellTheme.ForceForTest(true);
				ActionCenter.QaRenderMetro(darkShot, ActionCenterQaDevice.Desktop, currentTheme: true);
				ShellTheme.ForceForTest(realDark);
				(int lightDarkDiff, int lightDarkTotal) = PixelDiff(lightShot, darkShot);

				Dictionary<string, string> stateAfter = HashMutableState();
				bool stateUnchanged = stateBefore.Count == stateAfter.Count
					&& stateBefore.All(pair => stateAfter.TryGetValue(pair.Key, out string value) && value == pair.Value);
				ActionCenterQaMetrics[] metrics = { laptop, desktop, clamped };
				FileInfo[] screenshots = { new FileInfo(laptopShot), new FileInfo(desktopShot), new FileInfo(clampedShot), new FileInfo(lightShot), new FileInfo(darkShot) };
				(bool anchoringOk, List<string> anchoring) = AnchoringChecks();
				(bool paletteOk, string palette) = PaletteChecks(laptop);
				(bool migrationOk, List<string> migration) = MigrationChecks();
				(bool noSurfaceOk, string noSurface) = NoNotificationSurface(metrics);
				string[] desktopOrder = { "bluetooth", "notifications", "theme", "maps", "project", "connect" };
				var checks = new Dictionary<string, bool>
				{
					["panel-width-376"] = metrics.All(m => m.WidthDiu == 376.0),
					["height-sized-to-content"] = laptop.HeightDiu == 357.0 && desktop.HeightDiu == 247.0
						&& laptop.HeightDiu == laptop.ExpectedHeightDiu && desktop.HeightDiu == desktop.ExpectedHeightDiu
						&& laptop.ExpectedHeightDiu == 71.0 + (3 * 66.0 + 2 * 4.0) + 40.0 * 2 && desktop.ExpectedHeightDiu == 71.0 + (2 * 66.0 + 4.0) + 40.0,
					// Clamped: never taller than the available 260 DIP, and the tile viewport shows whole rows only (k x 66 + (k - 1) x 4),
					// so the panel is the chrome + those rows + the sliders and the rest of the tiles scroll.
					["height-clamped-to-available"] = clamped.HeightDiu <= 260.0 && clamped.TileScrollableHeightDiu > 0.0
						&& clamped.HeightDiu == 71.0 + clamped.TileViewportHeightDiu + 40.0 * clamped.SliderRows
						&& Math.Abs((clamped.TileViewportHeightDiu + 4.0) % 70.0) < 0.01
						&& clamped.SlidersAndFooterInside && clamped.SliderRows == 2 && clamped.TileViewportHeightDiu >= 66.0
						&& laptop.SlidersAndFooterInside && desktop.SlidersAndFooterInside && laptop.TileScrollableHeightDiu <= 0.5 && desktop.TileScrollableHeightDiu <= 0.5,
					["anchoring-per-edge"] = anchoringOk,
					["four-column-reference-order"] = metrics.All(m => m.TileColumns == 4 && m.FourColumnReferenceOrder)
						&& laptop.VisibleTileOrder.SequenceEqual(ActionCenter.TileReferenceOrder) && desktop.VisibleTileOrder.SequenceEqual(desktopOrder)
						&& Math.Abs(laptop.TileWidthDiu - 85.0) < 0.5,
					["exact-route-contracts"] = metrics.All(m => m.RouteContracts == 13 && m.FunctionalRoutes == 13 && m.ExactRouteContracts
						&& m.RouteContractMap.TryGetValue("notifications", out string n) && n == "shell:Win+N|fallback:ms-settings:notifications"
						&& m.RouteContractMap.TryGetValue("settings", out string st) && st == "settings:ms-settings:"
						&& m.RouteContractMap.TryGetValue("power", out string pw) && pw == "launcher:power-menu"
						&& !m.RouteContractMap.ContainsKey("quiet")),
					["notifications-tile-dispatches-win-n"] = metrics.All(m => m.NotificationsChordCaptured && m.NotificationsChord == "91,78"),
					["maps-opens-google-maps-in-default-browser"] = metrics.All(m => m.MapsUsesGoogleMaps && m.ExternalLinksUseDefaultHandler && m.MapsTarget == "https://www.google.com/maps"),
					["premium-vector-icons-and-active-state"] = metrics.All(m => m.IconsAreVectors && m.IconsUseUniformPremiumGeometry && m.TileIconSizeDiu == 20.0
						&& m.ActiveChecksPresent && m.StatefulTiles >= 5) && laptop.ChecksShown >= 1,
					["laptop-and-desktop-device-rules"] = metrics.All(m => m.LaptopRulesPassed && m.DesktopRulesPassed)
						&& laptop.VisibleTiles == 11 && laptop.SliderRows == 2 && laptop.TileRows == 3
						&& desktop.VisibleTiles == 6 && desktop.SliderRows == 1 && desktop.TileRows == 2,
					["volume-and-brightness-are-live-controls"] = metrics.All(m => m.SlidersAreLiveControls),
					["no-notification-surface"] = noSurfaceOk,
					["flyout-palette-shared"] = paletteOk,
					["normal-ignores-windows-light-dark"] = lightDarkTotal > 0 && lightDarkDiff == 0,
					["start-theme-background-synchronized"] = metrics.All(m => m.ThemeBackgroundSynchronized),
					["red-blue-theme-switch-is-not-hardcoded"] = metrics.All(m => m.ThemeSwitchPassed),
					["motion-contract"] = metrics.All(m => m.OpenTravelDiu == 12.0 && m.OpenMoveMs == 250 && m.OpenFadeMs == 120
						&& m.CloseTravelDiu == 6.0 && m.CloseMoveMs == 100 && m.CloseFadeMs == 100
						&& m.ReducedOpenTravelDiu == 6.0 && m.ReducedCloseTravelDiu == 3.0 && m.TileHoverMs == 100 && m.TileClickMs == 100),
					["legacy-quiet-migration-decisions"] = migrationOk,
					["cold-build-under-350ms-and-warm-build-under-50ms"] = laptop.VisualBuildMs <= 350 && desktop.VisualBuildMs <= 50 && clamped.VisualBuildMs <= 50,
					["render-and-layout-under-1000ms"] = metrics.All(m => m.RenderAndLayoutMs <= 1000),
					["zero-closed-idle-worker-or-render-hook"] = metrics.All(m => !m.RefreshWorkerActiveDuringDetachedRender && m.IdleRenderingHookInactive),
					["mutable-state-unchanged"] = stateUnchanged,
					["screenshots-present"] = screenshots.All(file => file.Exists && file.Length > 5000)
				};
				bool passed = checks.Values.All(value => value);
				var report = new
				{
					SchemaVersion = 4,
					GeneratedUtc = DateTime.UtcNow,
					Passed = passed,
					Checks = checks,
					Failed = checks.Where(c => !c.Value).Select(c => c.Key).ToArray(),
					Anchoring = anchoring,
					Palette = palette,
					Migration = migration,
					NoNotificationSurface = noSurface,
					LightVsDarkDifferingPixels = lightDarkDiff,
					LaptopMetrics = laptop,
					DesktopMetrics = desktop,
					ClampedMetrics = clamped,
					LiveMetrics = live,
					MutableStateUnchanged = stateUnchanged,
					LaptopScreenshotSha256 = HashFile(screenshots[0]),
					DesktopScreenshotSha256 = HashFile(screenshots[1]),
					ClampedScreenshotSha256 = HashFile(screenshots[2]),
					DesignContract = new
					{
						Style = "Compact Windows 8.1 Quick settings panel in the tray flyout family (clock, volume, network)",
						Layout = "376 DIP wide, sized to content (71 + tile rows + 40 per slider), anchored 6 DIP from the taskbar at its tray end on the monitor it opened from",
						Content = "Four-column quick tiles, volume and brightness sliders, All settings link and Power button; no header, no notification list",
						Notifications = "Entirely Windows' own: the Notifications tile sends Win+N; the launcher never reads, counts, mirrors or dismisses notifications",
						Motion = "Open: 12 DIP rise over 250 ms DECELERATE + 120 ms fade; close: 100 ms fade + 6 DIP toward the taskbar, ACCELERATE",
						Performance = "No timer or worker while closed; one cancellable state snapshot per open"
					}
				};
				File.WriteAllText(reportPath, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
				Logger.Log($"ACTION-CENTER-QA: passed={passed}, render={laptop.RenderAndLayoutMs}/{desktop.RenderAndLayoutMs}/{clamped.RenderAndLayoutMs}ms -> {reportPath}");
				app.Shutdown(passed ? 0 : 1);
			}
			catch (Exception ex)
			{
				File.WriteAllText(reportPath, JsonSerializer.Serialize(new { SchemaVersion = 4, GeneratedUtc = DateTime.UtcNow, Passed = false, Error = ex.ToString() }, new JsonSerializerOptions { WriteIndented = true }));
				Logger.Log("ACTION-CENTER-QA failed: " + ex);
				app.Shutdown(1);
			}
		}, DispatcherPriority.Loaded);
	}

	// ac_design §3: the placement vectors (scale 1, 376 x 247 DIP unless noted), the toward-taskbar vectors and the clamp.
	private static (bool, List<string>) AnchoringChecks()
	{
		List<string> rows = new List<string>();
		bool ok = true;
		void Case(string name, Rectangle work, Rectangle? bar, string edge, double scale, double w, double h, Rectangle? monitor, Rectangle want, System.Windows.Vector toward)
		{
			ActionCenterPlacement p = ActionCenter.PlacePanel(work, bar, edge, scale, w, h, monitor);
			bool hit = p.Px == want && p.Toward == toward;
			ok &= hit;
			rows.Add($"{(hit ? "PASS" : "FAIL")} {name}: got {p.Px.X},{p.Px.Y} {p.Px.Width}x{p.Px.Height} toward ({p.Toward.X},{p.Toward.Y}) maxH {p.MaxHeightPx}; want {want.X},{want.Y} {want.Width}x{want.Height} ({toward.X},{toward.Y})");
		}
		System.Windows.Vector down = new System.Windows.Vector(0.0, 1.0);
		System.Windows.Vector up = new System.Windows.Vector(0.0, -1.0);
		System.Windows.Vector left = new System.Windows.Vector(-1.0, 0.0);
		System.Windows.Vector right = new System.Windows.Vector(1.0, 0.0);
		Rectangle full = Rectangle.FromLTRB(0, 0, 1920, 1080);
		Case("bottom", Rectangle.FromLTRB(0, 0, 1920, 1040), null, "Bottom", 1.0, 376, 247, full, new Rectangle(1538, 787, 376, 247), down);
		Case("bottom with bar", Rectangle.FromLTRB(0, 0, 1920, 1040), Rectangle.FromLTRB(0, 1040, 1920, 1080), "Bottom", 1.0, 376, 247, full, new Rectangle(1538, 787, 376, 247), down);
		Case("top", Rectangle.FromLTRB(0, 40, 1920, 1080), null, "Top", 1.0, 376, 247, full, new Rectangle(1538, 46, 376, 247), up);
		Case("left", Rectangle.FromLTRB(48, 0, 1920, 1080), null, "Left", 1.0, 376, 247, full, new Rectangle(54, 827, 376, 247), left);
		Case("right", Rectangle.FromLTRB(0, 0, 1872, 1080), null, "Right", 1.0, 376, 247, full, new Rectangle(1490, 827, 376, 247), right);
		Case("secondary monitor (negative x), bottom", Rectangle.FromLTRB(-1920, 0, 0, 1040), null, "Bottom", 1.0, 376, 247, Rectangle.FromLTRB(-1920, 0, 0, 1080), new Rectangle(-382, 787, 376, 247), down);
		Case("auto-hide, bottom (work area = monitor, bar shown)", full, Rectangle.FromLTRB(0, 1040, 1920, 1080), "Bottom", 1.0, 376, 247, full, new Rectangle(1538, 787, 376, 247), down);
		Case("150% DPI, bottom", Rectangle.FromLTRB(0, 0, 2880, 1560), null, "Bottom", 1.5, 376, 247, Rectangle.FromLTRB(0, 0, 2880, 1620), new Rectangle(2307, 1180, 564, 371), down);
		// clamp: a panel taller than the space above a top-docked short work area stays on the monitor (6 px inset)
		Case("clamp inside the monitor", Rectangle.FromLTRB(0, 0, 1280, 300), null, "Bottom", 1.0, 376, 400, Rectangle.FromLTRB(0, 0, 1280, 300), new Rectangle(898, 6, 376, 400), down);
		// the tallest panel per edge
		int MaxH(Rectangle work, Rectangle? bar, string edge) => ActionCenter.PlacePanel(work, bar, edge, 1.0, 376, 247, null).MaxHeightPx;
		bool maxOk = MaxH(Rectangle.FromLTRB(0, 0, 1920, 1040), null, "Bottom") == 1040 - 6 - 6
			&& MaxH(Rectangle.FromLTRB(0, 40, 1920, 1080), null, "Top") == 1080 - 6 - (40 + 6)
			&& MaxH(Rectangle.FromLTRB(48, 0, 1920, 1080), null, "Left") == 1080 - 12
			&& MaxH(Rectangle.FromLTRB(0, 0, 1872, 1080), null, "Right") == 1080 - 12;
		ok &= maxOk;
		rows.Add($"{(maxOk ? "PASS" : "FAIL")} max heights per edge");
		bool widthOk = ActionCenter.PanelWidthFor(1920) == 376.0 && ActionCenter.PanelWidthFor(360) == 348.0 && ActionCenter.PanelWidthFor(200) == 320.0;
		ok &= widthOk;
		rows.Add($"{(widthOk ? "PASS" : "FAIL")} narrow monitor width = clamp(available - 12, 320, 376)");
		return (ok, rows);
	}

	// The Normal tones are the flyouts' own (FlyoutPalette.Tone through TaskbarWindow's tone path) for the same accent.
	private static (bool, string) PaletteChecks(ActionCenterQaMetrics m)
	{
		Color blue = Color.FromRgb(0, 112, 192);
		Color cyan = Color.FromRgb(0, 174, 239);
		Color top = FlyoutPalette.Tone(blue, 0.0, 0.0, 0.24);
		Color bottom = FlyoutPalette.Tone(cyan, 0.0, 0.0, 0.14);
		Color border = FlyoutPalette.Tone(cyan, 0.0, 0.42, 0.48);
		Color footer = FlyoutPalette.Tone(cyan, 0.0, 0.0, 0.16);
		Color linkTone = FlyoutPalette.Tone(cyan, -0.01, 0.55, 0.68);
		Color fill = FlyoutPalette.Brighten(cyan);
		bool sameAsTaskbar = top == TaskbarWindow.QaSoundTone(blue, 0.0, 0.0, 0.24) && bottom == TaskbarWindow.QaSoundTone(cyan, 0.0, 0.0, 0.14)
			&& border == TaskbarWindow.QaSoundTone(cyan, 0.0, 0.42, 0.48) && linkTone == TaskbarWindow.QaSoundTone(cyan, -0.01, 0.55, 0.68)
			&& footer == TaskbarWindow.QaSoundTone(cyan, 0.0, 0.0, 0.16);
		Color link = Parse(m.LinkColor);
		double linkContrast = new[] { top, bottom, footer }.Min(g => FlyoutPalette.Contrast(link, g));
		bool linkOk = (link == linkTone && linkContrast >= 4.5) || (link != linkTone && linkContrast >= 4.5 && new[] { top, bottom, footer }.Min(g => FlyoutPalette.Contrast(linkTone, g)) < 4.5);
		bool rendered = Parse(m.BodyTop) == top && Parse(m.BodyBottom) == bottom && Parse(m.BorderColor) == border && Parse(m.FooterColor) == footer && Parse(m.SliderFill) == fill;
		// a season keeps its base hue for the whole body
		Color pine = Color.FromRgb(0x1E, 0x5B, 0x3A);
		Color red = Color.FromRgb(0xC8, 0x1E, 0x2D);
		ActionCenterTones season = ActionCenter.QaNormalTones(pine, red, seasonal: true);
		bool seasonal = season.BodyTop == FlyoutPalette.Tone(pine, 0.0, 0.0, 0.26) && season.BodyBottom == FlyoutPalette.Tone(pine, 0.0, 0.0, 0.17)
			&& season.Border == FlyoutPalette.Tone(red, 0.0, 0.42, 0.48) && new[] { season.BodyTop, season.BodyBottom, season.Footer }.Min(g => FlyoutPalette.Contrast(season.Link, g)) >= 4.5;
		bool ok = sameAsTaskbar && linkOk && rendered && seasonal;
		return (ok, $"taskbarTones={sameAsTaskbar} rendered={rendered} (top {m.BodyTop}/{top} bottom {m.BodyBottom}/{bottom} border {m.BorderColor}/{border} footer {m.FooterColor}/{footer} fill {m.SliderFill}/{fill}) link {m.LinkColor} tone {linkTone} contrast {linkContrast:F2} ok={linkOk} seasonal={seasonal}");
	}

	private static Color Parse(string hex)
	{
		try
		{
			return (Color)System.Windows.Media.ColorConverter.ConvertFromString(hex);
		}
		catch
		{
			return System.Windows.Media.Colors.Magenta;
		}
	}

	// notifications_plan §3a: the full decision table of the legacy quiet-hours migration.
	private static (bool, List<string>) MigrationChecks()
	{
		List<string> rows = new List<string>();
		bool ok = true;
		void Case(string name, string snap, int? current, NativeBanner.LegacyWrite write, int value, bool deleteSnap)
		{
			NativeBanner.LegacyDecision d = NativeBanner.DecideLegacyRestore(snap, current);
			bool hit = d.Write == write && (write != NativeBanner.LegacyWrite.Set || d.Value == value) && d.DeleteSnap == deleteSnap;
			ok &= hit;
			rows.Add($"{(hit ? "PASS" : "FAIL")} {name}: {d.Write} {d.Value} deleteSnap={d.DeleteSnap}");
		}
		Case("no snapshot, value 0", null, 0, NativeBanner.LegacyWrite.None, 0, false);
		Case("no snapshot, unset", null, null, NativeBanner.LegacyWrite.None, 0, false);
		Case("0 with snapshot 'unset' -> delete", "unset", 0, NativeBanner.LegacyWrite.Delete, 0, true);
		Case("0 with snapshot '1' -> set 1", "1", 0, NativeBanner.LegacyWrite.Set, 1, true);
		Case("0 with snapshot ' 1\\r\\n' -> set 1", " 1\r\n", 0, NativeBanner.LegacyWrite.Set, 1, true);
		Case("0 with snapshot '0' -> set 0", "0", 0, NativeBanner.LegacyWrite.Set, 0, true);
		Case("0 with garbage snapshot -> set 1", "garbage", 0, NativeBanner.LegacyWrite.Set, 1, true);
		Case("changed since (1) -> nothing", "unset", 1, NativeBanner.LegacyWrite.None, 0, true);
		Case("changed since (unset) -> nothing", "1", null, NativeBanner.LegacyWrite.None, 0, true);
		return (ok, rows);
	}

	// No notification UI and no notification API anywhere: the rendered texts, the scroll viewers, the deleted types and the
	// assembly's type references.
	private static (bool, string) NoNotificationSurface(ActionCenterQaMetrics[] metrics)
	{
		// (built from parts, so a source search for the removed strings finds none outside the panel's history)
		string[] banned = { "Action " + "center", "Clear " + "all", "caught " + "up", "Notification " + "access", "No new " + "notifications", "Fewer " + "settings", "More " + "settings", "Quiet " + "hours" };
		List<string> found = metrics.SelectMany(m => m.Texts).Where(t => banned.Any(b => t.Contains(b, StringComparison.OrdinalIgnoreCase))).Distinct().ToList();
		bool scrollOk = metrics.All(m => m.OnlyScrollViewerIsTileViewport);
		string[] deleted = { "Win81Layer.Toast" + "Window", "Win81Layer.Toast" + "Service", "Win81Layer.Notification" + "Router", "Win81Layer.Notification" + "Settings" };
		List<string> stillThere = deleted.Where(n => typeof(App).Assembly.GetType(n) != null).ToList();
		List<string> refs = new List<string>();
		try
		{
			using FileStream fs = File.OpenRead(typeof(App).Assembly.Location);
			using PEReader pe = new PEReader(fs);
			MetadataReader md = pe.GetMetadataReader();
			foreach (TypeReferenceHandle h in md.TypeReferences)
			{
				TypeReference tr = md.GetTypeReference(h);
				string ns = md.GetString(tr.Namespace);
				string name = md.GetString(tr.Name);
				if (ns.StartsWith("Windows.UI." + "Notifications", StringComparison.Ordinal) || name == "UserNotification" + "Listener" || name == "UserNotification" || name == "ToastNotification" + "Manager")
				{
					refs.Add(ns + "." + name);
				}
			}
		}
		catch (Exception ex)
		{
			refs.Add("metadata scan failed: " + ex.Message);
		}
		bool ok = found.Count == 0 && scrollOk && stillThere.Count == 0 && refs.Count == 0;
		return (ok, $"texts={string.Join("|", found)} onlyTileScroll={scrollOk} deletedTypesPresent={string.Join(",", stillThere)} notificationTypeRefs={string.Join(",", refs)}");
	}

	private static (int, int) PixelDiff(string a, string b)
	{
		try
		{
			BitmapSource x = Load(a);
			BitmapSource y = Load(b);
			if (x.PixelWidth != y.PixelWidth || x.PixelHeight != y.PixelHeight)
			{
				return (int.MaxValue, 1);
			}
			int stride = x.PixelWidth * 4;
			byte[] px = new byte[stride * x.PixelHeight];
			byte[] py = new byte[stride * y.PixelHeight];
			new FormatConvertedBitmap(x, System.Windows.Media.PixelFormats.Bgra32, null, 0).CopyPixels(px, stride, 0);
			new FormatConvertedBitmap(y, System.Windows.Media.PixelFormats.Bgra32, null, 0).CopyPixels(py, stride, 0);
			int diff = 0;
			for (int i = 0; i < px.Length; i += 4)
			{
				if (px[i] != py[i] || px[i + 1] != py[i + 1] || px[i + 2] != py[i + 2] || px[i + 3] != py[i + 3])
				{
					diff++;
				}
			}
			return (diff, x.PixelWidth * x.PixelHeight);
		}
		catch
		{
			return (int.MaxValue, 0);
		}
	}

	private static BitmapSource Load(string path)
	{
		BitmapImage b = new BitmapImage();
		b.BeginInit();
		b.CacheOption = BitmapCacheOption.OnLoad;
		b.UriSource = new Uri(path);
		b.EndInit();
		b.Freeze();
		return b;
	}

	private static string HashFile(FileInfo file)
	{
		return file.Exists ? Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file.FullName))) : null;
	}

	private static Dictionary<string, string> HashMutableState()
	{
		string root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Win81Layer");
		Dictionary<string, string> hashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
		foreach (string name in StateNames)
		{
			string path = Path.Combine(root, name);
			if (File.Exists(path)) hashes[name] = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
		}
		return hashes;
	}
}
