using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;

namespace Win81Layer;

public sealed class NetworkFlyoutQaMetrics
{
	public double WidthDiu { get; set; }
	public double HeightDiu { get; set; }
	public int ActionTiles { get; set; }
	public bool ActionIconsCentered { get; set; }
	public bool ActionIconsVector { get; set; }
	public bool HeroIconsVector { get; set; }
	public bool TrayIconsVector { get; set; }
	public bool TrayStateIconsDistinct { get; set; }
	public int DetailRows { get; set; }
	public string Title { get; set; } = string.Empty;
	public string ConnectionStatus { get; set; } = string.Empty;
	public string PrimaryLabel { get; set; } = string.Empty;
	public string PrimaryValue { get; set; } = string.Empty;
	public bool StateBannerVisible { get; set; }
	public bool WifiHeroVisible { get; set; }
	public bool EthernetHeroVisible { get; set; }
	public bool OfflineHeroVisible { get; set; }
	public bool AirplaneHeroVisible { get; set; }
	public bool BoundedScrollEnabled { get; set; }
	public bool PixelScrollingEnabled { get; set; }
	public double ScrollBarHitWidthDiu { get; set; }
	public bool CustomPlacementEnabled { get; set; }
	public bool SquareCorners { get; set; }
	public bool ThemeBackgroundSynchronized { get; set; }
	public bool ThemeSwitchPassed { get; set; }
	public string ThemeAccent { get; set; } = string.Empty;
	public long VisualBuildMs { get; set; }
	public long RenderAndLayoutMs { get; set; }
	public bool RefreshWorkerActiveDuringDetachedRender { get; set; }
	public int RightClickCommands { get; set; }
	public bool RightClickIconsVector { get; set; }
	public bool RightClickMetroSurface { get; set; }
	public bool RightClickCustomPlacement { get; set; }
	public string RightClickScreenshot { get; set; } = string.Empty;
	public string Screenshot { get; set; } = string.Empty;
}

internal static class NetworkFlyoutDiagnostics
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
			string connectedScreenshot = Path.Combine(qaRoot, "NETWORK-FLYOUT-QA-CONNECTED.png");
			string ethernetScreenshot = Path.Combine(qaRoot, "NETWORK-FLYOUT-QA-ETHERNET.png");
			string limitedScreenshot = Path.Combine(qaRoot, "NETWORK-FLYOUT-QA-LIMITED.png");
			string offlineScreenshot = Path.Combine(qaRoot, "NETWORK-FLYOUT-QA-OFFLINE.png");
			string commandScreenshot = Path.Combine(qaRoot, "NETWORK-RIGHT-CLICK-QA.png");
			string iconAtlasScreenshot = Path.Combine(qaRoot, "NETWORK-ICON-ATLAS-QA.png");
			string reportPath = Path.Combine(qaRoot, "network-flyout-qa-latest.json");
			Dictionary<string, string> stateBefore = HashMutableState();
			TaskbarWindow? taskbar = null;
			try
			{
				Stopwatch liveProbe = Stopwatch.StartNew();
				var live = Task.Run(() => (State: NetState81.Read(), Local: NetInfo.Local())).GetAwaiter().GetResult();
				liveProbe.Stop();

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

				NetInfo.LocalNet previewLocal = new NetInfo.LocalNet(
					"Wi-Fi",
					"192.168.1.9",
					"192.168.1.1",
					"1.1.1.1, 8.8.8.8",
					"Intel Wi-Fi 6E AX210",
					"866 Mbps");
				NetworkFlyoutQaMetrics connected = taskbar.QaRenderNetworkFlyout(
					connectedScreenshot,
					new NetState81(NetKind.Wifi, "HomeNetwork", 5, Internet: true),
					previewLocal,
					"203.0.113.42",
					420.0,
					500.0,
					commandScreenshot);
				NetworkFlyoutQaMetrics limited = taskbar.QaRenderNetworkFlyout(
					limitedScreenshot,
					new NetState81(NetKind.Wifi, "HomeNetwork", 3, Internet: false),
					previewLocal,
					null,
					380.0,
					450.0);
				NetworkFlyoutQaMetrics ethernet = taskbar.QaRenderNetworkFlyout(
					ethernetScreenshot,
					new NetState81(NetKind.Ethernet, "Ethernet", 0, Internet: true),
					new NetInfo.LocalNet("Ethernet", "192.168.1.9", "192.168.1.1", "1.1.1.1", "Realtek PCIe 2.5GbE Family Controller", "2.5 Gbps"),
					"203.0.113.42",
					420.0,
					500.0);
				NetworkFlyoutQaMetrics offline = taskbar.QaRenderNetworkFlyout(
					offlineScreenshot,
					new NetState81(NetKind.Offline, "Not connected", 0, Internet: false),
					new NetInfo.LocalNet("Offline", "N/A", "N/A", "N/A", "", "—"),
					null,
					380.0,
					450.0);
				NetworkIconSetQa iconSet = NetworkIconSetDiagnostics.Render(iconAtlasScreenshot);

				Dictionary<string, string> stateAfter = HashMutableState();
				bool stateUnchanged = stateBefore.Count == stateAfter.Count
					&& stateBefore.All(pair => stateAfter.TryGetValue(pair.Key, out string value) && value == pair.Value);
				FileInfo connectedImage = new FileInfo(connectedScreenshot);
				FileInfo ethernetImage = new FileInfo(ethernetScreenshot);
				FileInfo limitedImage = new FileInfo(limitedScreenshot);
				FileInfo offlineImage = new FileInfo(offlineScreenshot);
				FileInfo commandImage = new FileInfo(commandScreenshot);
				FileInfo iconAtlasImage = new FileInfo(iconAtlasScreenshot);
				var checks = new Dictionary<string, bool>
				{
					["full-layout-420x500"] = connected.WidthDiu == 420.0 && connected.HeightDiu == 500.0,
					["compact-layouts-380x450"] = limited.WidthDiu == 380.0 && limited.HeightDiu == 450.0 && offline.WidthDiu == 380.0 && offline.HeightDiu == 450.0,
					["three-functional-action-routes"] = connected.ActionTiles == 3 && limited.ActionTiles == 3 && offline.ActionTiles == 3,
					["action-icons-use-centered-crisp-vectors"] = connected.ActionIconsCentered && connected.ActionIconsVector && limited.ActionIconsCentered && limited.ActionIconsVector && offline.ActionIconsCentered && offline.ActionIconsVector,
					["all-network-state-icons-are-modern-vectors"] = connected.HeroIconsVector && connected.TrayIconsVector && connected.TrayStateIconsDistinct,
					["complete-wifi-ethernet-generic-cellular-state-matrix"] = iconSet.CompleteReferenceMatrix && iconSet.Variants >= 49,
					["exact-32-16-12-pixel-variants"] = iconSet.Exact12_16_32Sizes && iconSet.RenderedSizes == iconSet.Variants * 3,
					["network-icons-are-frozen-cache-stable-drawing-vectors"] = iconSet.AllDrawingImages && iconSet.AllFrozenAndCacheStable,
					["all-icon-sizes-are-nonblank-and-transparent"] = iconSet.AllSizesNonBlank && iconSet.AllBackgroundsTransparent,
					["every-icon-state-has-a-distinct-fingerprint"] = iconSet.EveryVariantDistinct && iconSet.Distinct32PxFingerprints == iconSet.Variants,
					["windows81-reference-icon-palette-present"] = iconSet.ReferencePalettePresent,
					["network-right-click-is-four-command-metro-popup"] = connected.RightClickCommands == 4 && connected.RightClickIconsVector && connected.RightClickMetroSurface && connected.RightClickCustomPlacement,
					["seven-live-detail-rows"] = connected.DetailRows == 7 && limited.DetailRows == 7 && offline.DetailRows == 7,
					["connected-wifi-state-correct"] = connected.Title == "Wi-Fi" && connected.ConnectionStatus == "Connected" && connected.WifiHeroVisible && !connected.StateBannerVisible,
					["connected-ethernet-state-correct"] = ethernet.Title == "Ethernet" && ethernet.ConnectionStatus == "Connected" && ethernet.EthernetHeroVisible && !ethernet.StateBannerVisible,
					["limited-state-correct"] = limited.ConnectionStatus == "Limited connectivity" && limited.WifiHeroVisible && limited.StateBannerVisible,
					["offline-state-correct"] = offline.Title == "No network" && offline.ConnectionStatus == "Not connected" && offline.OfflineHeroVisible && offline.StateBannerVisible,
					["start-theme-background-synchronized"] = connected.ThemeBackgroundSynchronized && ethernet.ThemeBackgroundSynchronized && limited.ThemeBackgroundSynchronized && offline.ThemeBackgroundSynchronized,
					["red-blue-theme-switch-is-not-hardcoded"] = connected.ThemeSwitchPassed && ethernet.ThemeSwitchPassed && limited.ThemeSwitchPassed && offline.ThemeSwitchPassed,
					["pixel-scroll-and-wide-hit-target"] = connected.BoundedScrollEnabled && connected.PixelScrollingEnabled && connected.ScrollBarHitWidthDiu >= 14.0 && limited.BoundedScrollEnabled && limited.PixelScrollingEnabled && limited.ScrollBarHitWidthDiu >= 14.0,
					["custom-edge-placement-enabled"] = connected.CustomPlacementEnabled && limited.CustomPlacementEnabled && offline.CustomPlacementEnabled,
					["metro-square-corners"] = connected.SquareCorners && limited.SquareCorners && offline.SquareCorners,
					["preview-build-under-100ms"] = connected.VisualBuildMs <= 100 && ethernet.VisualBuildMs <= 100 && limited.VisualBuildMs <= 100 && offline.VisualBuildMs <= 100,
					["render-and-layout-under-1000ms"] = connected.RenderAndLayoutMs <= 1000 && ethernet.RenderAndLayoutMs <= 1000 && limited.RenderAndLayoutMs <= 1000 && offline.RenderAndLayoutMs <= 1000,
					["no-closed-state-network-worker"] = !connected.RefreshWorkerActiveDuringDetachedRender && !ethernet.RefreshWorkerActiveDuringDetachedRender && !limited.RefreshWorkerActiveDuringDetachedRender && !offline.RefreshWorkerActiveDuringDetachedRender,
					["live-network-inventory-under-1000ms"] = liveProbe.ElapsedMilliseconds <= 1000,
					["live-network-state-readable"] = !string.IsNullOrWhiteSpace(live.State.Status) && !string.IsNullOrWhiteSpace(live.Local.Type),
					["mutable-state-unchanged"] = stateUnchanged,
					["screenshots-present"] = connectedImage.Exists && connectedImage.Length > 10000 && ethernetImage.Exists && ethernetImage.Length > 10000 && limitedImage.Exists && limitedImage.Length > 10000 && offlineImage.Exists && offlineImage.Length > 10000 && commandImage.Exists && commandImage.Length > 5000 && iconAtlasImage.Exists && iconAtlasImage.Length > 20000
				};
				bool passed = checks.Values.All(value => value);
				var report = new
				{
					SchemaVersion = 3,
					GeneratedUtc = DateTime.UtcNow,
					Passed = passed,
					Checks = checks,
					ConnectedMetrics = connected,
					EthernetMetrics = ethernet,
					LimitedMetrics = limited,
					OfflineMetrics = offline,
					IconSet = iconSet,
					TaskbarWindowStartupMs = startup.ElapsedMilliseconds,
					LiveNetwork = new
					{
						Kind = live.State.Kind.ToString(),
						live.State.Label,
						live.State.Status,
						live.State.Bars,
						live.State.Internet,
						live.Local.Type,
						live.Local.Adapter,
						live.Local.Speed,
						live.Local.LocalIp,
						ProbeMs = liveProbe.ElapsedMilliseconds
					},
					MutableStateUnchanged = stateUnchanged,
					ConnectedScreenshotSha256 = HashFile(connectedImage),
					EthernetScreenshotSha256 = HashFile(ethernetImage),
					LimitedScreenshotSha256 = HashFile(limitedImage),
					OfflineScreenshotSha256 = HashFile(offlineImage),
					RightClickScreenshotSha256 = HashFile(commandImage),
					IconAtlasScreenshotSha256 = HashFile(iconAtlasImage),
					DesignContract = new
					{
						Style = "Windows 8.1 Metro network flyout",
						States = Enum.GetNames<NetworkIconState>(),
						IconKinds = Enum.GetNames<NetworkIconKind>(),
						ExactSizes = new[] { 32, 16, 12 },
						Details = new[] { "SSID or adapter", "link speed", "signal strength", "IPv4", "gateway", "DNS", "public IP" },
						Actions = new[] { "Wi-Fi settings", "Network connections", "Troubleshoot problems", "Network and Sharing Center" },
						Refresh = "Local inventory on a cancellable background worker; event-driven network changes; cached external public-IP lookup",
						Placement = "monitor-aware custom placement for all four taskbar edges",
						Motion = "temporary bitmap cache only during edge enter/exit; no idle animation or timer",
						Icons = "one cached DrawingImage engine shared by tray, flyout hero, Action Center, lock screen and PC Settings; Wi-Fi, Ethernet, generic and cellular bases use composable Windows 8.1 badges for every production state",
						RightClick = "four-command theme-synchronized Metro popup matching the Windows 8.1 reference surface",
						Theme = "header, body, footer, border, links and action palette are regenerated from the active Start background/accent",
						ClosedIdleCost = "zero timers, zero polling and zero external network requests"
					}
				};
				File.WriteAllText(reportPath, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
				Logger.Log($"NETWORK-FLYOUT-QA: passed={passed}, liveProbe={liveProbe.ElapsedMilliseconds}ms, render={connected.RenderAndLayoutMs}/{ethernet.RenderAndLayoutMs}/{limited.RenderAndLayoutMs}/{offline.RenderAndLayoutMs}ms -> {reportPath}");
				app.Shutdown(passed ? 0 : 1);
			}
			catch (Exception ex)
			{
				File.WriteAllText(reportPath, JsonSerializer.Serialize(new { SchemaVersion = 3, GeneratedUtc = DateTime.UtcNow, Passed = false, Error = ex.ToString() }, new JsonSerializerOptions { WriteIndented = true }));
				Logger.Log("NETWORK-FLYOUT-QA failed: " + ex);
				app.Shutdown(1);
			}
			finally
			{
				taskbar?.StopBar();
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
			if (File.Exists(path))
			{
				hashes[name] = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
			}
		}
		return hashes;
	}
}
