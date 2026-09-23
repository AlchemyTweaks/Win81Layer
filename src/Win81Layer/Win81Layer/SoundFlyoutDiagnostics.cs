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

public sealed class SoundFlyoutQaMetrics
{
	public double WidthDiu { get; set; }
	public double HeightDiu { get; set; }
	public int OutputRows { get; set; }
	public int InputRows { get; set; }
	public int AppSessionRows { get; set; }
	public int ActionTiles { get; set; }
	public bool ActionIconsCentered { get; set; }
	public bool ActionIconsVector { get; set; }
	public string DefaultOutputName { get; set; } = string.Empty;
	public double MasterIconContainerWidthDiu { get; set; }
	public double MasterIconGlyphWidthDiu { get; set; }
	public double MasterIconSafeInsetDiu { get; set; }
	public bool MasterIconHasSafeInsets { get; set; }
	public double MasterSliderMinimum { get; set; }
	public double MasterSliderMaximum { get; set; }
	public bool BoundedScrollEnabled { get; set; }
	public bool PixelScrollingEnabled { get; set; }
	public bool SmoothWheelCyclePassed { get; set; }
	public bool SmoothWheelReachedTarget { get; set; }
	public bool WheelOverContentRouted { get; set; }
	public bool WheelOverHeaderRouted { get; set; }
	public bool WheelOverFooterRouted { get; set; }
	public bool WheelOverScrollBarRouted { get; set; }
	public bool HandledWheelOverScrollBarRouted { get; set; }
	public bool RapidWheelReversalPassed { get; set; }
	public bool GlobalWheelCaptureReady { get; set; }
	public bool GlobalWheelRoutePassed { get; set; }
	public bool GlobalWheelReachedTarget { get; set; }
	public long GlobalWheelSettleMs { get; set; }
	public long SmoothWheelSettleMs { get; set; }
	public double ScrollBarHitWidthDiu { get; set; }
	public bool CustomPlacementEnabled { get; set; }
	public bool SquareCorners { get; set; }
	public bool ThemeBackgroundSynchronized { get; set; }
	public string ThemeAccent { get; set; } = string.Empty;
	public string DefaultInputColor { get; set; } = string.Empty;
	public string AlternateInputColor { get; set; } = string.Empty;
	public double InputStateColorDistance { get; set; }
	public long VisualBuildMs { get; set; }
	public long RenderAndLayoutMs { get; set; }
	public bool TimerActiveDuringDetachedRender { get; set; }
	public int RightClickCommands { get; set; }
	public bool RightClickIconsVector { get; set; }
	public bool RightClickMetroSurface { get; set; }
	public bool RightClickCustomPlacement { get; set; }
	public string RightClickScreenshot { get; set; } = string.Empty;
	public string Screenshot { get; set; } = string.Empty;
}

internal static class SoundFlyoutDiagnostics
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
			string screenshot = Path.Combine(qaRoot, "SOUND-FLYOUT-QA-LATEST.png");
			string compactScreenshot = Path.Combine(qaRoot, "SOUND-FLYOUT-QA-COMPACT.png");
			string commandScreenshot = Path.Combine(qaRoot, "SOUND-RIGHT-CLICK-QA.png");
			string reportPath = Path.Combine(qaRoot, "sound-flyout-qa-latest.json");
			Dictionary<string, string> stateBefore = HashMutableState();
			TaskbarWindow? taskbar = null;
			try
			{
				AudioController controller = new AudioController();
				float volumeBefore = controller.GetVolume();
				bool muteBefore = controller.GetMute();
				Stopwatch liveProbe = Stopwatch.StartNew();
				var liveSnapshot = Task.Run(delegate
				{
					return (
						Outputs: AudioDevices.Enumerate(capture: false),
						Inputs: AudioDevices.Enumerate(capture: true),
						Sessions: AudioSessions.Enumerate());
				}).GetAwaiter().GetResult();
				liveProbe.Stop();
				List<AudioDevice> outputsBefore = liveSnapshot.Outputs;
				List<AudioDevice> inputsBefore = liveSnapshot.Inputs;
				List<AudioSessionVm> liveSessions = liveSnapshot.Sessions;

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
				SoundFlyoutQaMetrics metrics = taskbar.QaRenderSoundFlyout(screenshot, 420.0, 540.0, commandScreenshot);
				SoundFlyoutQaMetrics compactMetrics = taskbar.QaRenderSoundFlyout(compactScreenshot, 380.0, 500.0);

				controller.Reset();
				float volumeAfter = controller.GetVolume();
				bool muteAfter = controller.GetMute();
				List<AudioDevice> outputsAfter = AudioDevices.Enumerate(capture: false);
				List<AudioDevice> inputsAfter = AudioDevices.Enumerate(capture: true);
				Dictionary<string, string> stateAfter = HashMutableState();
				bool stateUnchanged = stateBefore.Count == stateAfter.Count && stateBefore.All(pair => stateAfter.TryGetValue(pair.Key, out string value) && value == pair.Value);
				bool endpointsUnchanged = DeviceSignature(outputsBefore) == DeviceSignature(outputsAfter) && DeviceSignature(inputsBefore) == DeviceSignature(inputsAfter);
				bool masterUnchanged = Math.Abs(volumeBefore - volumeAfter) <= 0.005f && muteBefore == muteAfter;
				FileInfo fullImage = new FileInfo(screenshot);
				FileInfo compactImage = new FileInfo(compactScreenshot);
				FileInfo commandImage = new FileInfo(commandScreenshot);

				var checks = new Dictionary<string, bool>
				{
					["full-layout-420x540"] = metrics.WidthDiu == 420.0 && metrics.HeightDiu == 540.0,
					["compact-layout-380x500"] = compactMetrics.WidthDiu == 380.0 && compactMetrics.HeightDiu == 500.0,
					["preview-output-two-input-states-and-session-present"] = metrics.OutputRows == 1 && metrics.InputRows == 2 && metrics.AppSessionRows == 1,
					["four-action-routes-present"] = metrics.ActionTiles == 4 && compactMetrics.ActionTiles == 4,
					["four-action-icons-use-centered-crisp-vectors"] = metrics.ActionIconsCentered && compactMetrics.ActionIconsCentered && metrics.ActionIconsVector && compactMetrics.ActionIconsVector,
					["start-theme-background-synchronized"] = metrics.ThemeBackgroundSynchronized && compactMetrics.ThemeBackgroundSynchronized,
					["default-and-alternate-input-colors-distinct"] = metrics.InputStateColorDistance >= 70.0 && compactMetrics.InputStateColorDistance >= 70.0,
					["master-range-0-to-100"] = metrics.MasterSliderMinimum == 0.0 && metrics.MasterSliderMaximum == 100.0,
					["master-volume-icon-has-safe-insets-at-100-percent"] = metrics.MasterIconHasSafeInsets && compactMetrics.MasterIconHasSafeInsets && metrics.MasterIconSafeInsetDiu >= 4.0 && compactMetrics.MasterIconSafeInsetDiu >= 4.0,
					["bounded-scroll-enabled"] = metrics.BoundedScrollEnabled && compactMetrics.BoundedScrollEnabled,
					["pixel-smooth-wheel-scroll-enabled"] = metrics.PixelScrollingEnabled && compactMetrics.PixelScrollingEnabled && metrics.SmoothWheelCyclePassed && metrics.GlobalWheelRoutePassed && compactMetrics.GlobalWheelRoutePassed,
					["wheel-routes-over-entire-flyout-and-scrollbar-even-when-handled"] = metrics.WheelOverHeaderRouted && compactMetrics.WheelOverHeaderRouted && metrics.WheelOverContentRouted && compactMetrics.WheelOverContentRouted && metrics.WheelOverFooterRouted && compactMetrics.WheelOverFooterRouted && metrics.WheelOverScrollBarRouted && compactMetrics.WheelOverScrollBarRouted && metrics.HandledWheelOverScrollBarRouted && compactMetrics.HandledWheelOverScrollBarRouted,
					["live-global-wheel-route-captures-real-popup-area"] = metrics.GlobalWheelCaptureReady && metrics.GlobalWheelRoutePassed && metrics.GlobalWheelReachedTarget && metrics.GlobalWheelSettleMs <= 220 && compactMetrics.GlobalWheelCaptureReady && compactMetrics.GlobalWheelRoutePassed && compactMetrics.GlobalWheelReachedTarget && compactMetrics.GlobalWheelSettleMs <= 220,
					["rapid-wheel-reversal-has-no-target-backlog"] = metrics.RapidWheelReversalPassed,
					["smooth-wheel-reaches-target-under-220ms"] = metrics.SmoothWheelReachedTarget && metrics.SmoothWheelSettleMs <= 220,
					["scrollbar-wheel-hit-area-at-least-14dip"] = metrics.ScrollBarHitWidthDiu >= 14.0 && compactMetrics.ScrollBarHitWidthDiu >= 14.0,
					["custom-edge-placement-enabled"] = metrics.CustomPlacementEnabled && compactMetrics.CustomPlacementEnabled,
					["metro-square-corners"] = metrics.SquareCorners && compactMetrics.SquareCorners,
					["preview-build-under-100ms"] = metrics.VisualBuildMs <= 100 && compactMetrics.VisualBuildMs <= 100,
					["render-and-layout-under-1000ms"] = metrics.RenderAndLayoutMs <= 1000 && compactMetrics.RenderAndLayoutMs <= 1000,
					["no-closed-state-timer"] = !metrics.TimerActiveDuringDetachedRender && !compactMetrics.TimerActiveDuringDetachedRender,
					["sound-right-click-is-five-command-metro-popup"] = metrics.RightClickCommands == 5 && metrics.RightClickIconsVector && metrics.RightClickMetroSurface && metrics.RightClickCustomPlacement,
					["live-output-endpoint-readable"] = outputsBefore.Count > 0 && outputsBefore.Any(device => device.IsDefault),
					["live-core-audio-probe-under-3000ms"] = liveProbe.ElapsedMilliseconds <= 3000,
					["master-volume-and-mute-unchanged"] = masterUnchanged,
					["default-endpoints-unchanged"] = endpointsUnchanged,
					["mutable-state-unchanged"] = stateUnchanged,
					["screenshots-present"] = fullImage.Exists && fullImage.Length > 10000 && compactImage.Exists && compactImage.Length > 10000 && commandImage.Exists && commandImage.Length > 5000
				};
				bool passed = checks.Values.All(value => value);
				var report = new
				{
					SchemaVersion = 5,
					GeneratedUtc = DateTime.UtcNow,
					Passed = passed,
					Checks = checks,
					Metrics = metrics,
					CompactMetrics = compactMetrics,
					TaskbarWindowStartupMs = startup.ElapsedMilliseconds,
					LiveAudio = new
					{
						OutputDevices = outputsBefore.Select(device => new { device.Name, device.IsDefault }).ToArray(),
						InputDevices = inputsBefore.Select(device => new { device.Name, device.IsDefault }).ToArray(),
						Sessions = liveSessions.Select(session => new { session.Name, session.VolumePct, session.Muted, HasIcon = session.Icon != null }).ToArray(),
						ProbeMs = liveProbe.ElapsedMilliseconds,
						MasterVolumeBefore = volumeBefore,
						MasterVolumeAfter = volumeAfter,
						MuteBefore = muteBefore,
						MuteAfter = muteAfter,
						StateUnchanged = masterUnchanged && endpointsUnchanged
					},
					MutableStateUnchanged = stateUnchanged,
					ScreenshotSha256 = fullImage.Exists ? Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(screenshot))) : null,
					CompactScreenshotSha256 = compactImage.Exists ? Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(compactScreenshot))) : null,
					RightClickScreenshotSha256 = commandImage.Exists ? Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(commandScreenshot))) : null,
					DesignContract = new
					{
						Style = "Windows 8.1 Metro sound flyout",
						Sections = new[] { "master", "output", "input", "app-volume", "audio-routes" },
						Refresh = "Core Audio inventory on background worker; 750ms master sync and 4.5s inventory cadence only while open",
						Placement = "monitor-aware custom placement for all four taskbar edges",
						Motion = "frame-synchronized low-latency wheel easing with bounded target look-ahead, immediate reversal and temporary edge-transition bitmap cache",
						Interaction = "the existing low-level mouse hook captures wheel input over the popup screen rect, coalesces bursts on the UI dispatcher, and retains WPF routed-event fallback across content and the 14-DIP scrollbar",
						RightClick = "five-command theme-synchronized Metro popup: mixer, playback, recording, sounds and settings",
						MasterIcon = "100-percent speaker glyph uses a fixed 48-DIP viewport with at least 5-DIP horizontal ink safety insets",
						Icons = "four centered font-independent vector symbols using Windows 8.1 Metro stroke geometry",
						Theme = "background chrome recomputed from the active Start background/accent; controls retain the reference Metro color identities",
						ClosedIdleCost = "zero sound-flyout timers and zero audio polling"
					}
				};
				File.WriteAllText(reportPath, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
				Logger.Log($"SOUND-FLYOUT-QA: passed={passed}, liveProbe={liveProbe.ElapsedMilliseconds}ms, render={metrics.RenderAndLayoutMs}/{compactMetrics.RenderAndLayoutMs}ms -> {reportPath}");
				app.Shutdown(passed ? 0 : 1);
			}
			catch (Exception ex)
			{
				File.WriteAllText(reportPath, JsonSerializer.Serialize(new { SchemaVersion = 5, GeneratedUtc = DateTime.UtcNow, Passed = false, Error = ex.ToString() }, new JsonSerializerOptions { WriteIndented = true }));
				Logger.Log("SOUND-FLYOUT-QA failed: " + ex);
				app.Shutdown(1);
			}
			finally
			{
				taskbar?.StopBar();
			}
		}, DispatcherPriority.Loaded);
	}

	private static string DeviceSignature(IEnumerable<AudioDevice> devices)
	{
		return string.Join("|", devices.OrderBy(device => device.Id, StringComparer.Ordinal).Select(device => device.Id + ":" + device.IsDefault));
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
