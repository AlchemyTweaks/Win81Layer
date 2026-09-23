using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;

namespace Win81Layer;

internal static class ActionCenterDiagnostics
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
			string emptyScreenshot = Path.Combine(qaRoot, "ACTION-CENTER-QA-EMPTY.png");
			string notificationsScreenshot = Path.Combine(qaRoot, "ACTION-CENTER-QA-NOTIFICATIONS.png");
			string compactScreenshot = Path.Combine(qaRoot, "ACTION-CENTER-QA-COMPACT.png");
			string reportPath = Path.Combine(qaRoot, "action-center-qa-latest.json");
			Dictionary<string, string> stateBefore = HashMutableState();
			try
			{
				ActionCenterQaMetrics empty = ActionCenter.QaRenderMetro(emptyScreenshot, 440.0, 860.0, compact: false, withNotifications: false);
				ActionCenterQaMetrics notifications = ActionCenter.QaRenderMetro(notificationsScreenshot, 440.0, 860.0, compact: false, withNotifications: true);
				ActionCenterQaMetrics compact = ActionCenter.QaRenderMetro(compactScreenshot, 376.0, 700.0, compact: true, withNotifications: true);

				Dictionary<string, string> stateAfter = HashMutableState();
				bool stateUnchanged = stateBefore.Count == stateAfter.Count
					&& stateBefore.All(pair => stateAfter.TryGetValue(pair.Key, out string value) && value == pair.Value);
				ActionCenterQaMetrics[] metrics = { empty, notifications, compact };
				FileInfo[] screenshots = { new FileInfo(emptyScreenshot), new FileInfo(notificationsScreenshot), new FileInfo(compactScreenshot) };
				var checks = new Dictionary<string, bool>
				{
					["full-layout-440x860"] = empty.WidthDiu == 440.0 && empty.HeightDiu == 860.0 && notifications.WidthDiu == 440.0 && notifications.HeightDiu == 860.0,
					["compact-layout-376x700"] = compact.WidthDiu == 376.0 && compact.HeightDiu == 700.0,
					["four-column-reference-order"] = metrics.All(item => item.TileColumns == 4 && item.FourColumnReferenceOrder),
					["all-tiles-have-exact-functional-route-contracts"] = metrics.All(item => item.DefinedTiles >= 13
						&& item.FunctionalRoutes == item.DefinedTiles
						&& item.RouteContracts == item.DefinedTiles
						&& item.ExactRouteContracts),
					["maps-opens-google-maps-in-default-browser"] = metrics.All(item => item.MapsUsesGoogleMaps
						&& item.ExternalLinksUseDefaultHandler
						&& item.MapsTarget == "https://www.google.com/maps"),
					["premium-vector-icons-and-active-state"] = metrics.All(item => item.IconsAreVectors
						&& item.IconsUseUniformPremiumGeometry
						&& item.TileIconSizeDiu == 34.0
						&& item.ActiveChecksPresent
						&& item.StatefulTiles >= 6),
					["laptop-and-desktop-device-rules"] = metrics.All(item => item.LaptopRulesPassed && item.DesktopRulesPassed),
					["volume-and-brightness-are-live-controls"] = metrics.All(item => item.SlidersAreLiveControls),
					["empty-notification-state-correct"] = empty.EmptyStateVisible && empty.NotificationCards == 0,
					["notification-cards-and-clear-all-work"] = !notifications.EmptyStateVisible && notifications.NotificationCards == 7 && notifications.ClearAllFunctional && !compact.EmptyStateVisible && compact.NotificationCards == 7 && compact.ClearAllFunctional,
					["fewer-more-settings-toggle-works"] = metrics.All(item => item.FewerSettingsFunctional),
					["fewer-more-uses-canonical-directional-arrow"] = metrics.All(item => item.CollapseUsesCanonicalDirectionalArrow
						&& item.CollapseDirectionMatchesAction
						&& item.CollapseHitTargetExpanded),
					["bounded-smooth-scroll-and-wide-hit-target"] = metrics.All(item => item.NotificationViewportBounded && item.PixelScrollingEnabled && item.ScrollBarHitWidthDiu >= 14.0) && notifications.ScrollRangeDiu > 0.0 && compact.ScrollRangeDiu > 0.0,
					["start-theme-background-synchronized"] = metrics.All(item => item.ThemeBackgroundSynchronized),
					["red-blue-theme-switch-is-not-hardcoded"] = metrics.All(item => item.ThemeSwitchPassed),
					["authentic-motion-contract"] = metrics.All(item => item.AuthenticOpenMs == 250 && item.AuthenticCloseMs == 200 && item.TileHoverMs == 100 && item.TileClickMs == 100 && item.NotificationTransitionMs == 200 && item.ClearAllTransitionMs == 150),
					["cold-build-under-350ms-and-warm-build-under-50ms"] = empty.VisualBuildMs <= 350 && notifications.VisualBuildMs <= 50 && compact.VisualBuildMs <= 50,
					["render-and-layout-under-1000ms"] = metrics.All(item => item.RenderAndLayoutMs <= 1000),
					["zero-closed-idle-worker-or-render-hook"] = metrics.All(item => !item.RefreshWorkerActiveDuringDetachedRender && item.IdleRenderingHookInactive),
					["mutable-state-unchanged"] = stateUnchanged,
					["screenshots-present"] = screenshots.All(file => file.Exists && file.Length > 10000)
				};
				bool passed = checks.Values.All(value => value);
				var report = new
				{
					SchemaVersion = 3,
					GeneratedUtc = DateTime.UtcNow,
					Passed = passed,
					Checks = checks,
					EmptyMetrics = empty,
					NotificationsMetrics = notifications,
					CompactMetrics = compact,
					MutableStateUnchanged = stateUnchanged,
					EmptyScreenshotSha256 = HashFile(screenshots[0]),
					NotificationsScreenshotSha256 = HashFile(screenshots[1]),
					CompactScreenshotSha256 = HashFile(screenshots[2]),
					DesignContract = new
					{
						Style = "Windows 8.1 Metro Action Center with modern responsive refinements",
						Layout = "Reduced 440-DIP dynamic Start-theme surface with notifications, live sliders, four-column square quick-action grid and fewer/more control",
						DirectToggles = new[] { "Wi-Fi radio", "Bluetooth radio", "airplane radios", "quiet notifications", "volume", "brightness" },
						SystemSurfaces = new[] { "Google Maps in default browser", "battery saver", "night light", "Project", "Connect", "All settings", "Power" },
						Routes = "Every tile carries an explicit route contract; Maps is fixed to https://www.google.com/maps with UseShellExecute=true",
						DeviceRules = "Brightness, airplane mode, battery saver and night light adapt to hardware availability; unsupported controls are hidden",
						Motion = "250 ms open, 200 ms close, 100 ms tile feedback, 200 ms notification entry and 150 ms clear-all",
						Performance = "No polling timer; cancellable one-shot state and notification workers only while open; temporary bitmap cache only during edge motion",
						DirectionalNavigation = "Fewer/More uses the shared canonical Windows 8.1 circular vector: Up collapses, Down expands, 32-DIP glyph inside a 42-DIP hit target",
						Accessibility = "Keyboard activation, automation labels, visible active check marks, live text status and 14+ DIU scrollbar hit target"
					}
				};
				File.WriteAllText(reportPath, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
				Logger.Log($"ACTION-CENTER-QA: passed={passed}, render={empty.RenderAndLayoutMs}/{notifications.RenderAndLayoutMs}/{compact.RenderAndLayoutMs}ms -> {reportPath}");
				app.Shutdown(passed ? 0 : 1);
			}
			catch (Exception ex)
			{
				File.WriteAllText(reportPath, JsonSerializer.Serialize(new { SchemaVersion = 3, GeneratedUtc = DateTime.UtcNow, Passed = false, Error = ex.ToString() }, new JsonSerializerOptions { WriteIndented = true }));
				Logger.Log("ACTION-CENTER-QA failed: " + ex);
				app.Shutdown(1);
			}
		}, DispatcherPriority.Loaded);
	}

	private static string? HashFile(FileInfo file)
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
