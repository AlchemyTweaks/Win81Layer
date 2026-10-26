using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace Win81Layer;

public sealed class SoundFlyoutQaMetrics
{
	public string Edge { get; set; } = string.Empty;
	public double WidthDiu { get; set; }
	public double HeightDiu { get; set; }
	public double ExpectedHeightDiu { get; set; }
	public double NaturalHeightDiu { get; set; }
	public double MaxHeightDiu { get; set; }
	public double BodyViewportDiu { get; set; }
	public bool BodyScrollable { get; set; }
	public bool MasterAndFooterInside { get; set; }
	public int OutputRows { get; set; }
	public int InputRows { get; set; }
	public int AppSessionRows { get; set; }
	public bool InputsExpanded { get; set; }
	public bool AppsExpanded { get; set; }
	public bool TicksOnDefaultOnly { get; set; }
	public bool SwitchMovesTickAtOnce { get; set; }
	public bool RefusedSwitchRollsBack { get; set; }
	public bool DisclosureOrderAwayFromTaskbar { get; set; }
	public bool ChevronsMatch { get; set; }
	public bool RowHeightsExact { get; set; }
	public string AppsCountText { get; set; } = string.Empty;
	public string InputRowText { get; set; } = string.Empty;
	public int DefaultInputNameShown { get; set; }
	public bool ExpanderStaysPutWhenClamped { get; set; }
	public bool KeyboardRoutePassed { get; set; }
	public string[] KeyboardTrace { get; set; } = Array.Empty<string>();
	public int FooterRoutes { get; set; }
	public bool FooterRoutesNamed { get; set; }
	public bool FooterIconsVector40 { get; set; }
	public Rect FooterGearRect { get; set; }
	public double FooterLabelLeftDiu { get; set; }
	public Rect FooterRect { get; set; }
	public bool ErrorInFooterSlot { get; set; }
	public bool MasterIconOk { get; set; }
	public double MasterSliderMinimum { get; set; }
	public double MasterSliderMaximum { get; set; }
	public bool SlidersKeyboardReady { get; set; }
	public bool MasterWheelSteps2 { get; set; }
	public bool AppWheelSteps2 { get; set; }
	public bool FittingBodyConsumesWheel { get; set; }
	public bool NoPendingDeltaAfterExpand { get; set; }
	public bool PixelScrollingEnabled { get; set; }
	public bool SmoothWheelCyclePassed { get; set; }
	public bool SmoothWheelReachedTarget { get; set; }
	public bool WheelOverContentRouted { get; set; }
	public bool WheelOverMasterRouted { get; set; }
	public bool WheelOverFooterRouted { get; set; }
	public bool WheelOverScrollBarRouted { get; set; }
	public bool RapidWheelReversalPassed { get; set; }
	public long SmoothWheelSettleMs { get; set; }
	public string[] WheelProbeTrace { get; set; } = Array.Empty<string>();
	public bool GlobalWheelCaptureReady { get; set; }
	public bool GlobalWheelRoutePassed { get; set; }
	public bool GlobalWheelReachedTarget { get; set; }
	public long GlobalWheelSettleMs { get; set; }
	public bool GlobalWheelOverSliderSteps { get; set; }
	public double ScrollBarWidthDiu { get; set; }
	public bool CustomPlacementEnabled { get; set; }
	public bool SquareCorners { get; set; }
	public bool TypographyTwelve { get; set; }
	public bool ThemeBackgroundSynchronized { get; set; }
	public string ThemeAccent { get; set; } = string.Empty;
	public string BodyTop { get; set; } = string.Empty;
	public string BodyBottom { get; set; } = string.Empty;
	public string FooterColor { get; set; } = string.Empty;
	public string BorderColor { get; set; } = string.Empty;
	public string LinkColor { get; set; } = string.Empty;
	public string ValueInkColor { get; set; } = string.Empty;
	public string SecondaryInkColor { get; set; } = string.Empty;
	public bool FrameStatic { get; set; }
	public bool PrepareTouchesContentOnly { get; set; }
	public Rect BodyProbe { get; set; }
	public Rect[] OutputRowRects { get; set; } = Array.Empty<Rect>();
	public Rect[] InputRowRects { get; set; } = Array.Empty<Rect>();
	public Rect[] AppRowRects { get; set; } = Array.Empty<Rect>();
	public string[] AudioWrites { get; set; } = Array.Empty<string>();
	public long VisualBuildMs { get; set; }
	public long RenderAndLayoutMs { get; set; }
	public bool TimerActiveDuringDetachedRender { get; set; }
	public int RightClickCommands { get; set; }
	public bool RightClickIconsVector { get; set; }
	public bool RightClickMetroSurface { get; set; }
	public bool RightClickButtonAnchored { get; set; }
	public string RightClickScreenshot { get; set; } = string.Empty;
	public string Screenshot { get; set; } = string.Empty;
}

// --sound-flyout-test [outDir]: offscreen QA of the compact volume flyout. Read-only (settings writes throw); the real audio
// is only read (the live probe), never written: every write of the flyout goes to the taskbar's QA audio sink. Exits by
// itself.
internal static class SoundFlyoutDiagnostics
{
	private static readonly string[] StateNames = { "settings.json", "profile.json", "taskbar-pins.json", "shell-profile-state.json" };

	public static void Begin(Application app, string[]? args = null)
	{
		PersistenceDiagnostics.SuppressAutomaticPostBootVerification = true;
		app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
		args ??= Array.Empty<string>();
		int idx = Array.IndexOf(args, "--sound-flyout-test");
		string qaRoot = (idx >= 0 && idx + 1 < args.Length && !args[idx + 1].StartsWith("--", StringComparison.Ordinal))
			? args[idx + 1]
			: Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Win81Layer", "qa");
		app.Dispatcher.BeginInvoke((Action)delegate
		{
			Directory.CreateDirectory(qaRoot);
			string P(string name) => Path.Combine(qaRoot, name);
			string screenshot = P("SOUND-FLYOUT-QA-LATEST.png");
			string commandScreenshot = P("SOUND-RIGHT-CLICK-QA.png");
			string reportPath = P("sound-flyout-qa-latest.json");
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
				// every write of the flyout lands here (QA never touches the endpoint)
				List<string> sink = new List<string>();
				taskbar.QaAudioSink = sink;
				ImageSource playerIcon = PlayerIcon();
				SoundFlyoutQaMetrics collapsed = taskbar.QaRenderSoundFlyout(screenshot, commandScreenshotPath: commandScreenshot);
				SoundFlyoutQaMetrics inputs = taskbar.QaRenderSoundFlyout(P("SOUND-FLYOUT-QA-INPUTS.png"), expandInputs: true, probes: false);
				SoundFlyoutQaMetrics apps = taskbar.QaRenderSoundFlyout(P("SOUND-FLYOUT-QA-APPS.png"), expandApps: true);
				SoundFlyoutQaMetrics both = taskbar.QaRenderSoundFlyout(P("SOUND-FLYOUT-QA-BOTH.png"), expandInputs: true, expandApps: true, probes: false);
				SoundFlyoutQaMetrics narrow = taskbar.QaRenderSoundFlyout(P("SOUND-FLYOUT-QA-NARROW.png"), ActionCenter.PanelWidthFor(332.0), probes: false);
				SoundFlyoutQaMetrics clamped = taskbar.QaRenderSoundFlyout(P("SOUND-FLYOUT-QA-CLAMPED.png"), maxHeightDiu: 220.0, expandInputs: true, expandApps: true);
				SoundFlyoutQaMetrics top = taskbar.QaRenderSoundFlyout(P("SOUND-FLYOUT-QA-TOP.png"), expandInputs: true, expandApps: true, edge: "Top", probes: false);
				SoundFlyoutQaMetrics left = taskbar.QaRenderSoundFlyout(P("SOUND-FLYOUT-QA-LEFT.png"), expandInputs: true, expandApps: true, edge: "Left", probes: false);
				SoundFlyoutQaMetrics player = taskbar.QaRenderSoundFlyout(P("SOUND-FLYOUT-QA-PLAYER.png"), expandInputs: true, expandApps: true, qaSessionIcon: playerIcon, probes: false);
				// three outputs (speakers, headphones, a display), two inputs, three apps (one muted)
				SoundFlyoutQaMetrics many = taskbar.QaRenderSoundFlyout(P("SOUND-FLYOUT-QA-MANY.png"), expandInputs: true, expandApps: true, qaSessionIcon: playerIcon, manyDevices: true);
				// the looks (the Action Center QA's fixed blue theme, red, the two seasons) and the live theme with Windows in
				// light and in dark mode (Normal ignores it: the two renders are pixel-identical)
				Dictionary<string, SoundFlyoutQaMetrics> looks = new Dictionary<string, SoundFlyoutQaMetrics>(StringComparer.Ordinal);
				foreach ((string name, Color themeBase, Color accent, bool seasonal) in FlyoutLooks.All)
				{
					looks[name] = taskbar.QaRenderSoundFlyout(P("SOUND-FLYOUT-QA-LOOK-" + name.ToUpperInvariant() + ".png"), expandInputs: true, expandApps: true, qaSessionIcon: playerIcon, tones: (themeBase, accent, seasonal), probes: false);
				}
				bool realDark = ShellTheme.IsDark;
				ShellTheme.ForceForTest(false);
				taskbar.QaRenderSoundFlyout(P("SOUND-FLYOUT-QA-LIVE-LIGHT.png"), expandApps: true, probes: false);
				ShellTheme.ForceForTest(true);
				taskbar.QaRenderSoundFlyout(P("SOUND-FLYOUT-QA-LIVE-DARK.png"), expandApps: true, probes: false);
				ShellTheme.ForceForTest(realDark);
				(int lightDarkDiff, int lightDarkTotal) = FlyoutQa.PixelDiff(P("SOUND-FLYOUT-QA-LIVE-LIGHT.png"), P("SOUND-FLYOUT-QA-LIVE-DARK.png"));
				(bool contrastOk, List<string> contrast) = FlyoutQa.LookContrast(looks.Append(new KeyValuePair<string, SoundFlyoutQaMetrics>("live", collapsed))
					.ToDictionary(l => l.Key, l => (l.Value.BodyTop, l.Value.BodyBottom, l.Value.FooterColor, l.Value.LinkColor, l.Value.SecondaryInkColor, l.Value.ValueInkColor)));
				(bool newerLevelKept, List<string> levelTrace) = RefreshKeepsNewerAppLevel();
				(bool hookKeysOk, List<string> hookKeyTrace) = HookKeyBookkeeping();

				controller.Reset();
				float volumeAfter = controller.GetVolume();
				bool muteAfter = controller.GetMute();
				List<AudioDevice> outputsAfter = AudioDevices.Enumerate(capture: false);
				List<AudioDevice> inputsAfter = AudioDevices.Enumerate(capture: true);
				Dictionary<string, string> stateAfter = HashMutableState();
				bool stateUnchanged = stateBefore.Count == stateAfter.Count && stateBefore.All(pair => stateAfter.TryGetValue(pair.Key, out string? value) && value == pair.Value);
				bool endpointsUnchanged = DeviceSignature(outputsBefore) == DeviceSignature(outputsAfter) && DeviceSignature(inputsBefore) == DeviceSignature(inputsAfter);
				bool masterUnchanged = Math.Abs(volumeBefore - volumeAfter) <= 0.005f && muteBefore == muteAfter;
				SoundFlyoutQaMetrics[] all = new[] { collapsed, inputs, apps, both, narrow, clamped, top, left, player, many }.Concat(looks.Values).ToArray();
				string[] shots = { screenshot, commandScreenshot, P("SOUND-FLYOUT-QA-INPUTS.png"), P("SOUND-FLYOUT-QA-APPS.png"), P("SOUND-FLYOUT-QA-BOTH.png"), P("SOUND-FLYOUT-QA-NARROW.png"), P("SOUND-FLYOUT-QA-CLAMPED.png"), P("SOUND-FLYOUT-QA-TOP.png"), P("SOUND-FLYOUT-QA-PLAYER.png"), P("SOUND-FLYOUT-QA-MANY.png") };

				var checks = new Dictionary<string, bool>
				{
					// sized to content: 95 + 4 + 32 + 4 + 32 + 32 = 199 collapsed; +64 inputs, +48 one app
					["heights-sized-to-content"] = collapsed.WidthDiu == 376.0 && collapsed.HeightDiu == 199.0 && inputs.HeightDiu == 263.0 && apps.HeightDiu == 247.0 && both.HeightDiu == 311.0
						&& all.All(m => Math.Abs(m.HeightDiu - m.ExpectedHeightDiu) < 0.5),
					["narrow-width-320"] = narrow.WidthDiu == 320.0 && narrow.HeightDiu == 199.0,
					["clamped-body-scrolls-master-and-footer-whole"] = clamped.HeightDiu == 220.0 && Math.Abs(clamped.BodyViewportDiu - 125.0) < 0.5 && clamped.BodyScrollable
						&& clamped.MasterAndFooterInside && all.All(m => m.MasterAndFooterInside) && !collapsed.BodyScrollable && !both.BodyScrollable,
					["rows-1-output-2-inputs-apps"] = collapsed.OutputRows == 1 && collapsed.InputRows == 2 && collapsed.AppSessionRows == 1 && player.AppSessionRows == 2
						&& !collapsed.InputsExpanded && !collapsed.AppsExpanded && both.InputsExpanded && both.AppsExpanded,
					["row-heights-32-and-48"] = all.All(m => m.RowHeightsExact),
					["tick-and-semibold-on-default-only"] = all.All(m => m.TicksOnDefaultOnly),
					["many-devices-sized-to-content"] = many.OutputRows == 3 && many.AppSessionRows == 3 && many.HeightDiu == 95.0 + 4.0 + 96.0 + 4.0 + 96.0 + 32.0 + 144.0,
					// the tick moves at once, every read begun before the switch is turned away, a refused switch rolls back
					["switch-moves-tick-at-once-and-rolls-back"] = many.SwitchMovesTickAtOnce && many.RefusedSwitchRollsBack,
					["disclosure-away-from-taskbar-per-edge"] = all.All(m => m.DisclosureOrderAwayFromTaskbar && m.ChevronsMatch) && top.Edge == "Top" && left.Edge == "Left",
					["clamped-expanders-stay-under-the-pointer"] = clamped.ExpanderStaysPutWhenClamped,
					// the input row names the default input while closed and the section over the open list: the name once
					["expander-texts"] = collapsed.AppsCountText == "1 app" && player.AppsCountText == "2 apps" && collapsed.InputRowText == "Mic ακουστικων (Realtek(R) Audio)"
						&& both.InputRowText == "Input devices" && all.All(m => m.DefaultInputNameShown == 1),
					["keys-through-the-input-hook"] = collapsed.KeyboardRoutePassed && apps.KeyboardRoutePassed && clamped.KeyboardRoutePassed && many.KeyboardRoutePassed,
					["hook-never-strands-a-key"] = hookKeysOk,
					["refresh-keeps-a-newer-app-level"] = newerLevelKept,
					["footer-five-named-routes"] = all.All(m => m.FooterRoutes == 5 && m.FooterRoutesNamed && m.FooterIconsVector40),
					["footer-matches-action-center"] = Math.Abs(collapsed.FooterRect.Height - 41.0) < 0.5 && Math.Abs(collapsed.FooterGearRect.Left - 20.0) < 0.5
						&& Math.Abs(collapsed.FooterGearRect.Width - 16.0) < 0.5 && Math.Abs(collapsed.FooterLabelLeftDiu - 52.0) < 0.5,
					["error-in-footer-slot-tooltip-stays"] = collapsed.ErrorInFooterSlot,
					["master-icon-vector-20-in-32x40"] = collapsed.MasterIconOk && collapsed.MasterSliderMinimum == 0.0 && collapsed.MasterSliderMaximum == 100.0,
					["sliders-keyboard-1-and-10"] = all.All(m => m.SlidersKeyboardReady),
					["wheel-over-sliders-steps-2-no-scroll"] = collapsed.MasterWheelSteps2 && apps.MasterWheelSteps2 && apps.AppWheelSteps2 && clamped.MasterWheelSteps2 && clamped.AppWheelSteps2,
					["wheel-consumed-when-body-fits"] = collapsed.FittingBodyConsumesWheel && apps.FittingBodyConsumesWheel,
					["no-pending-wheel-after-expand"] = collapsed.NoPendingDeltaAfterExpand && apps.NoPendingDeltaAfterExpand && clamped.NoPendingDeltaAfterExpand,
					["clamped-body-smooth-wheel"] = clamped.PixelScrollingEnabled && clamped.SmoothWheelCyclePassed && clamped.SmoothWheelReachedTarget && clamped.SmoothWheelSettleMs <= 220
						&& clamped.WheelOverContentRouted && clamped.WheelOverMasterRouted && clamped.WheelOverFooterRouted && clamped.WheelOverScrollBarRouted && clamped.RapidWheelReversalPassed,
					["global-wheel-route-hit-tests"] = collapsed.GlobalWheelCaptureReady && collapsed.GlobalWheelOverSliderSteps
						&& clamped.GlobalWheelCaptureReady && clamped.GlobalWheelOverSliderSteps && clamped.GlobalWheelRoutePassed && clamped.GlobalWheelReachedTarget && clamped.GlobalWheelSettleMs <= 220,
					["slim-scrollbar-10dip"] = clamped.ScrollBarWidthDiu == 10.0,
					["tray-end-placement-callback"] = all.All(m => m.CustomPlacementEnabled),
					["metro-square-corners"] = all.All(m => m.SquareCorners),
					["typography-12px"] = all.All(m => m.TypographyTwelve),
					["action-center-tones"] = collapsed.ThemeBackgroundSynchronized && looks.Values.All(m => m.ThemeBackgroundSynchronized),
					["normal-ignores-windows-light-dark"] = lightDarkTotal > 0 && lightDarkDiff == 0,
					["inks-readable-on-every-look"] = contrastOk,
					["motion-frame-static-content-moves"] = all.All(m => m.FrameStatic && m.PrepareTouchesContentOnly),
					["qa-audio-sink-only"] = sink.Count > 0 && sink.All(w => w.StartsWith("volume:", StringComparison.Ordinal) || w.StartsWith("mute:", StringComparison.Ordinal) || w.StartsWith("default:", StringComparison.Ordinal)),
					["preview-build-under-100ms"] = all.All(m => m.VisualBuildMs <= 100),
					["render-and-layout-under-1000ms"] = all.All(m => m.RenderAndLayoutMs <= 1000),
					["no-closed-state-timer"] = all.All(m => !m.TimerActiveDuringDetachedRender),
					["sound-right-click-unchanged-five-commands-button-anchored"] = collapsed.RightClickCommands == 5 && collapsed.RightClickIconsVector && collapsed.RightClickMetroSurface && collapsed.RightClickButtonAnchored,
					["live-output-endpoint-readable"] = outputsBefore.Count > 0 && outputsBefore.Any(device => device.IsDefault),
					["live-core-audio-probe-under-3000ms"] = liveProbe.ElapsedMilliseconds <= 3000,
					["master-volume-and-mute-unchanged"] = masterUnchanged,
					["default-endpoints-unchanged"] = endpointsUnchanged,
					["mutable-state-unchanged"] = stateUnchanged,
					["screenshots-present"] = shots.All(s => new FileInfo(s).Exists && new FileInfo(s).Length > 3000)
				};
				bool passed = checks.Values.All(value => value);
				var report = new
				{
					SchemaVersion = 6,
					GeneratedUtc = DateTime.UtcNow,
					Passed = passed,
					Failed = checks.Where(c => !c.Value).Select(c => c.Key).ToArray(),
					Checks = checks,
					LookContrast = contrast,
					RefreshLevelTrace = levelTrace,
					HookKeyTrace = hookKeyTrace,
					LightVsDarkDifferingPixels = lightDarkDiff,
					Metrics = collapsed,
					InputsMetrics = inputs,
					AppsMetrics = apps,
					BothMetrics = both,
					NarrowMetrics = narrow,
					ClampedMetrics = clamped,
					TopMetrics = top,
					LeftMetrics = left,
					PlayerMetrics = player,
					ManyMetrics = many,
					LookMetrics = looks,
					AudioSink = sink,
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
					ScreenshotSha256 = FlyoutQa.HashFile(screenshot),
					DesignContract = new
					{
						Style = "Compact quick-settings volume flyout in the Action Center family",
						Layout = "376 DIP (the Action Center's width rule), sized to content: 95 + body; 199 DIP with one output, the input row and the app row collapsed",
						Sections = new[] { "master (32 | 8 | slider | 44)", "output rows (32 DIP, tick on the default)", "input row (names the default input; open, it reads \"Input devices\" over every input, the default ticked)", "app volume row (discloses 48 DIP app rows)", "footer: Sound settings + playback, recording, sounds, classic mixer" },
						Keys = "through the input hook while open (the flyout never takes the foreground): Escape closes; arrows +-1, PageUp / PageDown +-10 on the master level",
						Placement = "the Action Center's PlacePanel: tray end, 6 DIP from the bar and the screen edge, per edge, work-area and mixed-DPI aware",
						Motion = "the frame appears at once; the content rises 12 DIP over 250 ms with a 120 ms fade; closes with a 100 ms fade and 6 DIP",
						Wheel = "over a slider: +-2 per notch; elsewhere: scrolls a clamped body, else consumed",
						ClosedIdleCost = "zero sound-flyout timers and zero audio polling"
					}
				};
				File.WriteAllText(reportPath, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true, NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals }));
				Logger.Log($"SOUND-FLYOUT-QA: passed={passed}, liveProbe={liveProbe.ElapsedMilliseconds}ms, render={collapsed.RenderAndLayoutMs}ms -> {reportPath}");
				app.Shutdown(passed ? 0 : 1);
			}
			catch (Exception ex)
			{
				File.WriteAllText(reportPath, JsonSerializer.Serialize(new { SchemaVersion = 6, GeneratedUtc = DateTime.UtcNow, Passed = false, Error = ex.ToString() }, new JsonSerializerOptions { WriteIndented = true, NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals }));
				Logger.Log("SOUND-FLYOUT-QA failed: " + ex);
				app.Shutdown(1);
			}
			finally
			{
				taskbar?.StopBar();
			}
		}, DispatcherPriority.Loaded);
	}

	// The input hook's bookkeeping of the flyout keys (WinKeyHook.FlyoutKeyStep), driven with scripted events: a press the
	// flyout takes is swallowed with its repeats and release; a press the app got keeps its repeats and release even when a
	// flyout opens meanwhile; a repeat the closed flyout no longer takes reaches the app with its release; Win combos and other
	// keys are never offered. Nothing is installed: no real key is sent or swallowed.
	private static (bool, List<string>) HookKeyBookkeeping()
	{
		List<string> rows = new List<string>();
		bool ok = true;
		int ups = 0;
		int downs = 0;
		bool open = false;
		int offered = 0;
		bool Route(int vk)
		{
			offered++;
			return open;
		}
		void Step(string name, int vk, bool down, bool winDown, bool wantSwallowed, int wantOffered)
		{
			offered = 0;
			bool swallowed = WinKeyHook.FlyoutKeyStep(ref ups, ref downs, vk, down, !down, winDown, Route);
			bool hit = swallowed == wantSwallowed && offered == wantOffered;
			ok &= hit;
			rows.Add($"{(hit ? "PASS" : "FAIL")} {name}: swallowed={swallowed} offered={offered}");
		}
		const int vkEsc = 0x1B, vkDown = 0x28, vkLeft = 0x25;
		open = true;
		Step("open: Escape press", vkEsc, true, false, true, 1);
		Step("open: Escape release", vkEsc, false, false, true, 0);
		Step("open: Down press", vkDown, true, false, true, 1);
		Step("open: Down repeat", vkDown, true, false, true, 1);
		Step("open: Down release", vkDown, false, false, true, 0);
		open = false;
		Step("closed: Escape press", vkEsc, true, false, false, 1);
		Step("closed: Escape release", vkEsc, false, false, false, 0);
		Step("app holds Down", vkDown, true, false, false, 1);
		open = true;
		Step("a flyout opens: the app's repeat stays the app's", vkDown, true, false, false, 0);
		Step("the app gets its release", vkDown, false, false, false, 0);
		Step("open: Down press", vkDown, true, false, true, 1);
		open = false;
		Step("the flyout closed: the repeat reaches the app", vkDown, true, false, false, 1);
		Step("and so does its release", vkDown, false, false, false, 0);
		open = true;
		Step("Win+Left is never offered", vkLeft, true, true, false, 0);
		Step("its release passes", vkLeft, false, true, false, 0);
		Step("A is never offered", 0x41, true, false, false, 0);
		ok &= ups == 0 && downs == 0;
		rows.Add($"{(ups == 0 && downs == 0 ? "PASS" : "FAIL")} nothing left held: flyout={ups} app={downs}");
		return (ok, rows);
	}

	// A refresh reads the app levels on a worker and applies them later: a level or a mute the user set after the read began
	// is newer and stays; one set before it gives way to the read. (Preview rows drive no session: nothing is written.)
	private static (bool, List<string>) RefreshKeepsNewerAppLevel()
	{
		List<string> rows = new List<string>();
		AudioSessionVm row = AudioSessionVm.Preview("Player", 40);
		long readBefore = Environment.TickCount64 - 1000;
		row.VolumePct = 60;
		row.SyncFrom(AudioSessionVm.Preview("Player", 40), readBefore);
		bool levelKept = row.VolumePct == 60;
		rows.Add($"{(levelKept ? "PASS" : "FAIL")} a level set after the read began stays: {row.VolumePct}");
		row.Muted = true;
		row.SyncFrom(AudioSessionVm.Preview("Player", 40, muted: false), readBefore);
		bool muteKept = row.Muted && row.VolumePct == 60;
		rows.Add($"{(muteKept ? "PASS" : "FAIL")} a mute set after the read began stays: muted={row.Muted} level={row.VolumePct}");
		row.SyncFrom(AudioSessionVm.Preview("Player", 35, muted: false), Environment.TickCount64 + 1);
		bool readTaken = row.VolumePct == 35 && !row.Muted;
		rows.Add($"{(readTaken ? "PASS" : "FAIL")} a read begun after the user's change is taken: muted={row.Muted} level={row.VolumePct}");
		return (levelKept && muteKept && readTaken, rows);
	}

	// A small dark app logo (a black disc with a play mark), like the Player app the Dark rest QA uses.
	private static ImageSource PlayerIcon()
	{
		DrawingGroup g = new DrawingGroup();
		g.Children.Add(new GeometryDrawing(Brushes.Transparent, null, new RectangleGeometry(new Rect(0, 0, 32, 32))));
		g.Children.Add(new GeometryDrawing(new SolidColorBrush(Color.FromRgb(0x1E, 0x88, 0xE5)), null, new EllipseGeometry(new Point(16, 16), 14, 14)));
		g.Children.Add(new GeometryDrawing(Brushes.White, null, Geometry.Parse("M 12,9 L 23,16 L 12,23 Z")));
		g.Freeze();
		DrawingImage image = new DrawingImage(g);
		image.Freeze();
		return image;
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

// The themes the clock and volume QA render besides the live one: the Action Center QA's fixed blue (blue base, cyan
// accent), a red one, and the two seasons (their base hue and accent, as the seasonal presets set them).
internal static class FlyoutLooks
{
	internal static readonly (string Name, Color ThemeBase, Color Accent, bool Seasonal)[] All =
	{
		("blue", Color.FromRgb(0, 112, 192), Color.FromRgb(0, 174, 239), false),
		("red", Color.FromRgb(170, 24, 36), Color.FromRgb(235, 50, 60), false),
		("halloween", Color.FromRgb(0x22, 0x10, 0x3A), Color.FromRgb(0xE0, 0x62, 0x0B), true),
		("christmas", Color.FromRgb(0x0B, 0x3D, 0x2C), Color.FromRgb(0xC8, 0x10, 0x2E), true)
	};
}

// Helpers shared by the clock and volume QA.
internal static class FlyoutQa
{
	internal static string? HashFile(string path)
	{
		return File.Exists(path) ? Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))) : null;
	}

	// The secondary ink (weekdays, counts, a footer message), the value ink (the date, the percentages), white (names, the
	// time) and the link, as rendered, stay at 4.5:1 or more on the body's two stops and the footer of every look.
	internal static (bool, List<string>) LookContrast(Dictionary<string, (string Top, string Bottom, string Footer, string Link, string Secondary, string Value)> looks)
	{
		List<string> rows = new List<string>();
		bool ok = true;
		foreach (KeyValuePair<string, (string Top, string Bottom, string Footer, string Link, string Secondary, string Value)> look in looks)
		{
			Color[] grounds = { Parse(look.Value.Top), Parse(look.Value.Bottom), Parse(look.Value.Footer) };
			double Min(Color ink) => grounds.Min(g => FlyoutPalette.Contrast(FlyoutPalette.Flatten(ink, g), g));
			double s = Min(Parse(look.Value.Secondary));
			double v = Min(Parse(look.Value.Value));
			double w = Min(Colors.White);
			double l = Min(Parse(look.Value.Link));
			bool hit = s >= 4.5 && v >= 4.5 && w >= 4.5 && l >= 4.5;
			ok &= hit;
			rows.Add($"{(hit ? "PASS" : "FAIL")} {look.Key}: secondary {look.Value.Secondary} {s:F2} value {look.Value.Value} {v:F2} white {w:F2} link {l:F2}");
		}
		return (ok, rows);
	}

	internal static Color Parse(string hex)
	{
		try
		{
			return (Color)ColorConverter.ConvertFromString(hex);
		}
		catch
		{
			return Colors.Magenta;
		}
	}

	internal static (int, int) PixelDiff(string a, string b)
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
			new FormatConvertedBitmap(x, PixelFormats.Bgra32, null, 0).CopyPixels(px, stride, 0);
			new FormatConvertedBitmap(y, PixelFormats.Bgra32, null, 0).CopyPixels(py, stride, 0);
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
}
