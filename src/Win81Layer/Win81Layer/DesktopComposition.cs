using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;

namespace Win81Layer;

public static class DesktopComposition
{
	private delegate bool EnumProc(nint hwnd, nint l);

	private delegate void WinEventDelegate(IntPtr hook, uint ev, IntPtr hwnd, int idObject, int idChild, uint thread, uint time);

	private const uint EVENT_SYSTEM_FOREGROUND = 3u;

	private const uint EVENT_OBJECT_STATECHANGE = 0x800Au;   // fires on maximize / restore / minimise state changes

	private const int DWMWA_COLOR_NONE = unchecked((int)0xFFFFFFFEu);   // border attr value meaning "no border line"

	private const uint WINEVENT_OUTOFCONTEXT = 0u;

	private const uint WINEVENT_SKIPOWNPROCESS = 2u;

	private const uint WINEVENT_SKIPOWNTHREAD = 1u;

	private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;

	private const int DWMWA_BORDER_COLOR = 34;

	private const int DWMWA_CAPTION_COLOR = 35;

	private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;

	private const int DWMWCP_DONOTROUND = 1;

	private const int DWMWA_COLOR_DEFAULT = -1;

	private const int DWMWA_CLOAKED = 14;

	private static readonly bool Win11 = Environment.OSVersion.Version.Build >= 22000;

	private static readonly int OwnPid = Environment.ProcessId;

	private const string DwmKey = "Software\\Microsoft\\Windows\\DWM";
	private static readonly string LegacyForeignResetMarker = Path.Combine(
		Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
		"Win81Layer", "qa", "foreign-dwm-reset-20260831-v2.marker");

	// Foreground WinEventHook: applies per-window composition attrs to each new foreground window (event-driven,
	// near-zero idle) instead of the old 2s EnumWindows polling. Technique from LuSlower/dwm-basic.
	private static IntPtr _fgHook;

	internal static bool ForegroundHookActiveForDiagnostics => _fgHook != IntPtr.Zero;

	internal static bool AccentBorderEnabledForDiagnostics => AccentBorderIsOn();

	private static WinEventDelegate _fgDelegate;   // kept alive so the GC can't collect the native callback

	private static IntPtr _stateHook;

	private static WinEventDelegate _stateDelegate;   // re-applies the 8.1 border when a window's maximize state changes

	private const string AccentKey = "Software\\Microsoft\\Windows\\CurrentVersion\\Explorer\\Accent";

	private const string PersonalizeKey = "Software\\Microsoft\\Windows\\CurrentVersion\\Themes\\Personalize";

	private static readonly (string Key, string Name)[] AccentValues = new(string, string)[8]
	{
		("Software\\Microsoft\\Windows\\CurrentVersion\\Explorer\\Accent", "AccentColorMenu"),
		("Software\\Microsoft\\Windows\\CurrentVersion\\Explorer\\Accent", "AccentPalette"),
		("Software\\Microsoft\\Windows\\CurrentVersion\\Explorer\\Accent", "StartColorMenu"),
		("Software\\Microsoft\\Windows\\DWM", "AccentColor"),
		("Software\\Microsoft\\Windows\\DWM", "ColorizationColor"),
		("Software\\Microsoft\\Windows\\DWM", "ColorPrevalence"),
		("Software\\Microsoft\\Windows\\DWM", "AccentColorInactive"),
		("Software\\Microsoft\\Windows\\CurrentVersion\\Themes\\Personalize", "ColorPrevalence")
	};

	private const int GWL_EXSTYLE = -20;

	private const int WS_EX_TOOLWINDOW = 128;

	private const uint WM_SETTINGCHANGE = 26u;

	private const uint WM_DWMCOLORIZATIONCOLORCHANGED = 800u;

	// The persisted mode (what boots next time). During a live Preview the APPLIED mode differs — see EffectiveMode.
	public static string Mode => SettingsStore.Load().DesktopCompositionMode;

	// Non-null only while a Preview is running; EffectiveMode is what is actually applied right now.
	private static string _previewMode;

	private static DispatcherTimer _previewTimer;

	private static readonly object NativeQueueGate = new object();

	private static readonly object AppliedProfileGate = new object();

	private static Task _nativeQueueTail = Task.CompletedTask;

	private static CompositionProfile _appliedProfile = CompositionProfiles.Resolve("native");

	private static bool _appliedProfileKnown;

	private static int _transitionVersion;

	private static int _accentVersion;

	private static int _queuedNativeOperations;

	private static int _modeChangePending;

	private static int _accentBroadcastBatches;

	private static int _completedTransitions;

	private static int _coalescedTransitions;

	private const int TransitionCoalesceMs = 90;

	// Queue diagnostics exercise the real registry/DWM transition pipeline without writing preview recovery
	// fields into the user's durable settings. Production previews keep their crash-recovery journal unchanged.
	internal static bool TransientPreviewDiagnostics { get; set; }

	public static string EffectiveMode => _previewMode ?? Mode;

	public static bool IsPreviewing => _previewMode != null;

	public static bool IsTransitioning => Volatile.Read(ref _queuedNativeOperations) > 0;

	internal static int DiagnosticAccentBroadcastBatches => Volatile.Read(ref _accentBroadcastBatches);

	internal static int DiagnosticCompletedTransitions => Volatile.Read(ref _completedTransitions);

	internal static int DiagnosticCoalescedTransitions => Volatile.Read(ref _coalescedTransitions);

	public enum ApplyStatus
	{
		Ok,
		Degraded,
		Partial,
		Preview
	}

	public static ApplyStatus LastStatus { get; private set; }

	public static string LastError { get; private set; } = "";

	private static string SnapPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Win81Layer", "deskcomp-accent.json");

	// The composition profile currently APPLIED (the previewed one while previewing, else the persisted mode).
	private static CompositionProfile CurrentProfile => CompositionProfiles.Resolve(EffectiveMode);

	// Commit a mode: cancels any live preview, records the previous mode for Restore Previous, persists + applies.
	public static void SetMode(string mode)
	{
		CompositionProfile prev = CurrentProfile;   // whatever is applied now (respects an active preview)
		CompositionProfile target = CompositionProfiles.Resolve(mode);
		string old = Mode;
		StopPreviewTimer();
		_previewMode = null;
		AppSettings s = SettingsStore.Load();
		if (!string.Equals(mode, old, StringComparison.OrdinalIgnoreCase))
		{
			s.DeskCompPreviousMode = old;   // enables "Restore Previous"
		}
		s.DesktopCompositionMode = mode;
		s.DeskCompPreviewMode = "";
		s.DeskCompPreviewUntilUtcTicks = 0L;
		SettingsStore.Save(s);
		QueueTransition(prev, target, "set " + target.Id);
		RaiseModeChanged();
	}

	// Apply a mode for `seconds` then auto-restore the persisted mode. Does NOT change what boots next time.
	public static void PreviewMode(string mode, int seconds = 10)
	{
		CompositionProfile prev = CurrentProfile;
		StopPreviewTimer();
		_previewMode = mode;
		if (!TransientPreviewDiagnostics)
		{
			AppSettings s = SettingsStore.Load();
			s.DeskCompPreviewMode = mode;
			s.DeskCompPreviewUntilUtcTicks = DateTime.UtcNow.Ticks + TimeSpan.FromSeconds(seconds).Ticks;
			SettingsStore.Save(s);
		}
		QueueTransition(prev, CompositionProfiles.Resolve(mode), "preview " + mode);
		try
		{
			_previewTimer = new DispatcherTimer((DispatcherPriority)4)
			{
				Interval = TimeSpan.FromSeconds(seconds)
			};
			_previewTimer.Tick += delegate
			{
				EndPreview();
			};
			_previewTimer.Start();
		}
		catch
		{
		}
		Logger.Log($"Desktop composition preview requested: {mode} ({seconds}s)");
		RaiseModeChanged();
	}

	// End a live preview early and snap back to the persisted mode.
	public static void EndPreview()
	{
		if (_previewMode == null)
		{
			return;
		}
		CompositionProfile prev = CurrentProfile;   // the preview profile
		_previewMode = null;
		StopPreviewTimer();
		AppSettings s = SettingsStore.Load();
		if (!TransientPreviewDiagnostics)
		{
			s.DeskCompPreviewMode = "";
			s.DeskCompPreviewUntilUtcTicks = 0L;
			SettingsStore.Save(s);
		}
		QueueTransition(prev, CompositionProfiles.Resolve(s.DesktopCompositionMode), "end preview");
		Logger.Log("Desktop composition preview restore requested: " + s.DesktopCompositionMode);
		RaiseModeChanged();
	}

	// Restore the mode that was active before the last change.
	public static void RestorePrevious()
	{
		string p = SettingsStore.Load().DeskCompPreviousMode;
		if (!string.IsNullOrEmpty(p))
		{
			SetMode(p);
		}
	}

	// The shell-profile transaction persists its complete target settings in one durable write. Re-applying the
	// composition through SetMode would perform a second settings mutation and break that transaction boundary, so
	// this method changes only runtime effects using an explicit previous mode.
	public static void ApplyPersistedModeTransition(string previousMode)
	{
		CompositionProfile previous = CompositionProfiles.Resolve(previousMode);
		StopPreviewTimer();
		_previewMode = null;
		CompositionProfile target = CurrentProfile;
		QueueTransition(previous, target, "profile transaction " + previous.Id + " -> " + target.Id);
		RaiseModeChanged();
	}

	// Unconditional escape hatch → clean Windows-native baseline, even if the snapshot is missing/corrupt.
	public static void RestoreWindowsDefault()
	{
		CompositionProfile previous = CurrentProfile;
		StopPreviewTimer();
		_previewMode = null;
		AppSettings s = SettingsStore.Load();
		if (!string.Equals(s.DesktopCompositionMode, "native", StringComparison.OrdinalIgnoreCase))
		{
			s.DeskCompPreviousMode = s.DesktopCompositionMode;   // remember what we left, so Restore Previous can undo
		}
		s.DesktopCompositionMode = "native";
		s.DeskCompPreviewMode = "";
		s.DeskCompPreviewUntilUtcTicks = 0L;
		SettingsStore.Save(s);
		QueueTransition(previous, CompositionProfiles.Resolve("native"), "forced Windows native restore", forceNative: true);
		RaiseModeChanged();
	}

	// Restore native + wipe all composition overrides/snapshot + reset the option/slider values to defaults.
	public static void Reset()
	{
		RestoreWindowsDefault();
		try
		{
			string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Win81Layer", "composition");
			if (Directory.Exists(dir))
			{
				foreach (string f in Directory.GetFiles(dir, "*.json"))
				{
					File.Delete(f);
				}
			}
			if (File.Exists(SnapPath))
			{
				File.Delete(SnapPath);
			}
		}
		catch
		{
		}
		AppSettings s = SettingsStore.Load();
		s.DeskCompTransparency = true;
		s.DeskCompShadows = true;
		s.DeskCompAnimations = true;
		s.DeskCompOptimizePerf = false;
		s.UseDwmBlurGlassForWin7 = false;
		s.DeskCompBlur = 60;
		s.DeskCompAnimSpeed = 50;
		SettingsStore.Save(s);
		Logger.Log("Desktop composition settings reset");
	}

	// Revert a profile's applied mechanisms (per-window sweep OFF + accent registry) — profile-agnostic OFF pass.
	private static void RevertProfile(CompositionProfile prev)
	{
		StopTimer();
		if (prev.IsNative)
		{
			return;
		}
		if (NeedsPerWindowSweep(prev))
		{
			Sweep(on: false, prev);
		}
		if (prev.GlobalAccentColorization)
		{
			RevertAccent();
		}
	}

	private static void StopPreviewTimer()
	{
		try
		{
			_previewTimer?.Stop();
		}
		catch
		{
		}
		_previewTimer = null;
	}

	private static CompositionProfile AppliedProfile
	{
		get
		{
			lock (AppliedProfileGate)
			{
				return _appliedProfile;
			}
		}
	}

	// The profile actually APPLIED to the desktop right now (unlike Mode, which reads the persisted setting). Lets the
	// dominance backstop detect a drift where an early degraded settings read applied 'native' instead of the saved mode.
	public static string AppliedMode => AppliedProfile?.Id ?? "native";

	private static void SetAppliedProfile(CompositionProfile profile, bool known = true)
	{
		lock (AppliedProfileGate)
		{
			_appliedProfile = profile;
			_appliedProfileKnown = known;
		}
	}

	private static void QueueTransition(CompositionProfile previousHint, CompositionProfile target, string reason, bool forceNative = false)
	{
		int version = Interlocked.Increment(ref _transitionVersion);
		EnqueueNative("composition " + reason, delegate
		{
			if (version != Volatile.Read(ref _transitionVersion))
			{
				Interlocked.Increment(ref _coalescedTransitions);
				return;
			}
			// Give fast repeated clicks one frame to settle. Older queued requests then become no-ops before they
			// touch the registry, foreign windows or DWMBlurGlass.
			Thread.Sleep(TransitionCoalesceMs);
			if (version != Volatile.Read(ref _transitionVersion))
			{
				Interlocked.Increment(ref _coalescedTransitions);
				return;
			}
			CompositionProfile previous;
			lock (AppliedProfileGate)
			{
				previous = _appliedProfileKnown ? _appliedProfile : previousHint;
			}
			// Once native work starts, finish one internally-consistent transaction. A newer request is coalesced
			// behind it and diffs from this exact applied state; never mark an interrupted half-revert as native.
			ApplyTransition(previous, target, forceNative);
			Interlocked.Increment(ref _completedTransitions);
			Logger.Log($"Desktop composition applied: {previous.Id} -> {target.Id} (caps: {CompositionCapabilities.Describe()})");
		});
	}

	private static void EnqueueNative(string label, Action action)
	{
		Interlocked.Increment(ref _queuedNativeOperations);
		lock (NativeQueueGate)
		{
			_nativeQueueTail = _nativeQueueTail.ContinueWith(delegate
			{
				Stopwatch sw = Stopwatch.StartNew();
				try
				{
					action();
				}
				catch (Exception ex)
				{
					LastStatus = ApplyStatus.Partial;
					LastError = label + ": " + ex.Message;
					Logger.Log(label + " failed: " + ex.Message);
				}
				finally
				{
					sw.Stop();
					Interlocked.Decrement(ref _queuedNativeOperations);
					Logger.Log(label + " completed in " + sw.ElapsedMilliseconds + " ms (background queue)");
				}
			}, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
		}
	}

	internal static bool WaitForIdle(TimeSpan timeout)
	{
		Task pending;
		lock (NativeQueueGate)
		{
			pending = _nativeQueueTail;
		}
		try
		{
			return pending.Wait(timeout);
		}
		catch
		{
			return false;
		}
	}

	private static void RaiseModeChanged()
	{
		try
		{
			TaskbarContextMenu.InvalidateTheme();
			System.Windows.Threading.Dispatcher dispatcher = System.Windows.Application.Current?.Dispatcher;
			if (dispatcher != null)
			{
				if (Interlocked.Exchange(ref _modeChangePending, 1) == 0)
				{
					dispatcher.BeginInvoke((Action)delegate
					{
						Interlocked.Exchange(ref _modeChangePending, 0);
						ModeChanged?.Invoke();
					}, DispatcherPriority.Render);
				}
			}
			else
			{
				ModeChanged?.Invoke();
			}
		}
		catch
		{
		}
	}

	// Live status for the Desktop Composition settings page (mode, effect toggles, backend, honest limitations).
	public static CompositionStatus DescribeStatus()
	{
		CompositionProfile p = CurrentProfile;
		AppSettings s = SettingsStore.Load();
		CompositionStatus st = new CompositionStatus
		{
			ModeId = p.Id,
			ModeName = p.Name,
			Transparency = (s.DeskCompTransparency && p.ShellGlass),
			Shadows = (s.DeskCompShadows && !p.IsNative),
			Animations = (s.DeskCompAnimations && !p.DisableWindowTransitions),
			Backend = SafeBackend(),
			DwmBlurGlassPolicy = "Disabled - owned frames only",
			DwmBlurGlassState = DwmBlurGlassBridge.DescribeState(p, s.UseDwmBlurGlassForWin7),
			Previewing = IsPreviewing,
			RestoreAvailable = !string.IsNullOrEmpty(s.DeskCompPreviousMode)
		};
		st.Transitioning = IsTransitioning;
		st.Status = (IsTransitioning ? "Applying in background" : IsPreviewing ? "Preview" : LastStatus switch
		{
			ApplyStatus.Degraded => "Degraded — native fallback",
			ApplyStatus.Partial => "Applied (partial)",
			_ => (p.IsNative ? "Windows Native" : "Applied")
		});
		st.Unsupported = BuildUnsupported(p, s);
		return st;
	}

	private static string SafeBackend()
	{
		try
		{
			return CompositionCapabilities.Describe();
		}
		catch
		{
			return "Windows DWM";
		}
	}

	// Honest "Unsupported features" list surfaced in the settings UI (documented-DWM only; owned vs foreign).
	private static List<string> BuildUnsupported(CompositionProfile p, AppSettings settings)
	{
		List<string> list = new List<string>();
		if (p.IsNative)
		{
			return list;
		}
		if (p.ShellGlass)
		{
			list.Add("Our 'glass' surfaces are translucent tints, not live blur — a deliberate safety choice.");
		}
		bool realExternalAero = p.ExternalAeroGlass && settings.UseDwmBlurGlassForWin7 && DwmBlurGlassBridge.AeroEnabled;
		if (p.ExternalAeroGlass && !realExternalAero)
		{
			list.Add(settings.UseDwmBlurGlassForWin7
				? "DWMBlurGlass is not active; Windows 7 uses the lightweight owned-frame fallback until the external engine is available."
				: "DWMBlurGlass is disabled; Windows 7 uses the lightweight owned-frame fallback.");
		}
		else if (realExternalAero)
		{
			list.Add("Classic Win32 frames receive real Aero glass; client-drawn Chrome, Electron and UWP title bars remain app-owned.");
		}
		list.Add("Other apps keep their native DWM frame, border, corners and transitions so composition switching cannot damage fullscreen or resize geometry.");
		if (!realExternalAero)
		{
			list.Add("Other apps' window glow, border thickness, and caption buttons can't be restyled with supported APIs.");
		}
		return list;
	}

	// Raised after the composition mode changes so the launcher's OWN shell (taskbar/flyouts/windows) can re-skin.
	public static event Action ModeChanged;

	private static void ApplyTransition(CompositionProfile previous, CompositionProfile target, bool forceNative)
	{
		StopTimer();
		bool previousSweep = NeedsPerWindowSweep(previous);
		bool targetSweep = NeedsPerWindowSweep(target);
		SetAppliedProfile(target);

		// Unsupported-build auto-fallback: if the modern compositor is off, apply NOTHING and mark degraded —
		// but do NOT overwrite the persisted mode, so it self-heals if DWM composition returns (spec recovery).
		if (!target.IsNative && !CompositionCapabilities.CompositionEnabled)
		{
			if (previousSweep)
			{
				Sweep(on: false, previous);
			}
			if (previous.GlobalAccentColorization || forceNative)
			{
				RevertAccent();
			}
			try { DwmBlurGlassBridge.Disable(); } catch { }
			LastStatus = ApplyStatus.Degraded;
			LastError = "DWM composition is disabled on this system — effects cannot be applied.";
			Logger.Log("Composition disabled — applying nothing (self-heals if DWM returns)");
			return;
		}
		LastError = "";
		bool accentOk = true;
		bool sweepOk = true;
		// Accent is common to Win7, Win8.1 and Alchemy. Keep it in place across those profiles and only
		// reconcile if the actual registry values differ; this removes the old revert+broadcast+apply+broadcast.
		if (target.GlobalAccentColorization)
		{
			accentOk = ApplyAccent();   // now returns success — a failed registry write is no longer reported as Ok
			if (!accentOk)
			{
				LastError = "Accent title-bar registry write failed (see log).";
			}
		}
		else if (previous.GlobalAccentColorization || forceNative)
		{
			RevertAccent();
		}
		else if (target.IsNative && AccentBorderIsOn())
		{
			// Self-heal a stuck accent border. Native mode must never leave the Windows "accent colour on title
			// bars AND window borders" flag (DWM ColorPrevalence) on — a prior Windows 8.1 / Aero session turns it
			// on, and if the revert to native didn't clear it (a poisoned snapshot, or a boot that applies
			// native -> native with no accent step and reapplyComposition=False) it stays stuck, drawing a thin
			// accent line down the edge of every maximised window that reaches the screen edge (Chromium/Electron
			// apps maximise flush to x=0). Force ONLY that flag off (leaving the accent HUE untouched) and drop any
			// stale snapshot. Gated on the LIVE registry value (not the snapshot file, whose %LOCALAPPDATA% path is
			// unreliable under app redirection) so it always self-heals — including the current stuck state.
			Logger.Log("Composition: native mode cleared a stuck accent title-bar/border (ColorPrevalence=1)");
			ForceTitleBarAccentOff();
			BroadcastColor();
			try { File.Delete(SnapPath); } catch { }
		}

		// Apply only the target attributes. If both profiles need a sweep, one target pass replaces the old
		// off-pass + on-pass. On Windows 10 the built-in 7/8.1 profiles need no foreign-window sweep at all.
		if (targetSweep)
		{
			sweepOk = Sweep(on: true, target);
			if (!sweepOk)
			{
				LastError = "Per-window DWM sweep failed (see log).";
			}
			StartTimer();
		}
		else if (previousSweep)
		{
			sweepOk = Sweep(on: false, previous);
		}
		// External real-Aero glass on classic foreign windows is strictly Win7-only. Every other profile restores
		// the exact external config baseline and gracefully detaches the hook/host so it cannot affect DWM latency.
		try { DwmBlurGlassBridge.Apply(target, SettingsStore.Load().UseDwmBlurGlassForWin7); } catch (Exception exg) { Logger.Log("DwmBlurGlassBridge.Apply: " + exg.Message); }
		LastStatus = (_previewMode != null) ? ApplyStatus.Preview : ((accentOk && sweepOk) ? ApplyStatus.Ok : ApplyStatus.Partial);
	}

	// Whether the profile requires per-window DWM attributes ON THIS BUILD. If not (e.g. an accent-only profile
	// on Win10 where the Win11 colour/corner/backdrop attrs no-op), we skip the EnumWindows sweep + its 2s timer
	// entirely, keeping idle CPU near zero (spec §36) — the global accent registry already does the colouring.
	private static bool NeedsPerWindowSweep(CompositionProfile p)
	{
		// Re-enabled 2026-09-05 for the authentic 8.1 profile (StyleForeignWindows=true): it applies the FLAT light
		// 8.1 chrome to every window via cross-process DWM attributes (verified working on this Win11 build). Gated on
		// Win11 frame-colour support so it no-ops on Win10; ALL OTHER profiles return false (owned-window-only), so
		// their idle CPU stays near zero and no foreground hook is installed for them.
		return p != null && p.StyleForeignWindows && CompositionCapabilities.SupportsFrameColors;
	}

	public static void Resume()
	{
		// Boot reconciliation: if the launcher died mid-preview, the previewed profile's registry accent may still be
		// applied. Revert its mechanisms, clear the preview flags, THEN apply the real persisted mode.
		CompositionProfile previousHint = CurrentProfile;
		AppSettings recovered = null;
		try
		{
			AppSettings s = SettingsStore.Load();
			recovered = s;
			if (!string.IsNullOrEmpty(s.DeskCompPreviewMode))
			{
				CompositionProfile pv = CompositionProfiles.Resolve(s.DeskCompPreviewMode);
				previousHint = pv;
				s.DeskCompPreviewMode = "";
				s.DeskCompPreviewUntilUtcTicks = 0L;
				SettingsStore.Save(s);
				Logger.Log("Composition: recovered from an interrupted preview → restoring " + s.DesktopCompositionMode);
			}
		}
		catch
		{
		}
		QueueLegacyForeignResetOnce();
		CompositionProfile target = CompositionProfiles.Resolve((recovered ?? SettingsStore.Load()).DesktopCompositionMode);
		QueueTransition(previousHint, target, "startup resume");
	}

	private static void QueueLegacyForeignResetOnce()
	{
		if (File.Exists(LegacyForeignResetMarker))
		{
			return;
		}
		EnqueueNative("legacy foreign DWM reset", delegate
		{
			try
			{
				if (!Sweep(on: false, CompositionProfiles.Resolve("native")))
				{
					Logger.Log("Legacy foreign DWM reset did not complete; it will retry next startup");
					return;
				}
				Directory.CreateDirectory(Path.GetDirectoryName(LegacyForeignResetMarker)!);
				File.WriteAllText(LegacyForeignResetMarker, DateTime.UtcNow.ToString("O"));
				Logger.Log("Legacy foreign DWM attributes reset once; no foreground hook installed");
			}
			catch (Exception ex)
			{
				Logger.Log("Legacy foreign DWM reset failed: " + ex.Message);
			}
		});
	}

	public static void RefreshAccent()
	{
		int version = Interlocked.Increment(ref _accentVersion);
		EnqueueNative("composition accent refresh", delegate
		{
			if (version == Volatile.Read(ref _accentVersion))
			{
				CompositionProfile profile = AppliedProfile;
				if (profile.GlobalAccentColorization)
				{
					ApplyAccent();
				}
			}
		});
	}

	public static void RevertOnExit()
	{
		try
		{
			WaitForIdle(TimeSpan.FromMilliseconds(1500.0));
			CompositionProfile p = AppliedProfile;
			StopTimer();
			try { DwmBlurGlassBridge.Disable(); } catch { }   // never leave external Aero glass on after we exit
			if (!p.IsNative)
			{
				if (NeedsPerWindowSweep(p))
				{
					Sweep(on: false, p);
				}
				if (p.GlobalAccentColorization)
				{
					RevertAccent();   // otherwise quitting leaves the accent frame colours applied system-wide
				}
			}
		}
		catch
		{
		}
	}

	private static void StartTimer()   // now: install the foreground hook (event-driven; replaces 2s polling)
	{
		StartForegroundHook();
	}

	private static void StopTimer()
	{
		StopForegroundHook();
	}

	private static void StartForegroundHook()
	{
		if (_fgHook != IntPtr.Zero)
		{
			return;
		}
		try
		{
			_fgDelegate = OnForeground;
			_fgHook = SetWinEventHook(EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_FOREGROUND, IntPtr.Zero, _fgDelegate, 0u, 0u, WINEVENT_OUTOFCONTEXT | WINEVENT_SKIPOWNPROCESS | WINEVENT_SKIPOWNTHREAD);
		}
		catch (Exception ex)
		{
			Logger.Log("Composition foreground hook failed: " + ex.Message);
		}
		try
		{
			// Re-apply the border when a window maximizes/restores (the foreground hook only fires on focus change,
			// so a window maximized while already focused would keep its windowed grey border as an edge line).
			_stateDelegate = OnStateChange;
			_stateHook = SetWinEventHook(EVENT_OBJECT_STATECHANGE, EVENT_OBJECT_STATECHANGE, IntPtr.Zero, _stateDelegate, 0u, 0u, WINEVENT_OUTOFCONTEXT | WINEVENT_SKIPOWNPROCESS | WINEVENT_SKIPOWNTHREAD);
		}
		catch (Exception ex)
		{
			Logger.Log("Composition state hook failed: " + ex.Message);
		}
		StartSettleSweep();
	}

	private static CancellationTokenSource _settleSweep;

	// Safety net for the settle: the two hooks catch the common cases (maximize transition, focus of an open-maximized
	// window), but a low-frequency sweep guarantees ANY eligible maximized window gets settled once - windows that were
	// already maximized before the hooks armed, snapped windows, or windows dragged maximized onto another monitor.
	// _settledMax makes each window settle at most once per maximize, so the sweep never re-toggles a settled window.
	private static void StartSettleSweep()
	{
		if (_settleSweep != null) return;
		_settleSweep = new CancellationTokenSource();
		CancellationToken tok = _settleSweep.Token;
		Task.Run(async delegate
		{
			while (!tok.IsCancellationRequested)
			{
				try { await Task.Delay(1500, tok).ConfigureAwait(false); } catch { break; }
				CompositionProfile p = AppliedProfile;
				if (p == null || !p.StyleForeignWindows) continue;
				try
				{
					EnumWindows(delegate(nint h, nint _)
					{
						if (!Eligible(h)) return true;
						if (FillsWorkArea(h)) { int none = DWMWA_COLOR_NONE; DwmSetWindowAttribute(h, 34, ref none, 4); }  // work-area-filling window stays flush (no border line)
						if (IsZoomed(h)) ConsiderSettle(h);
						return true;
					}, IntPtr.Zero);
				}
				catch { }
			}
		});
	}

	private static void StopSettleSweep()
	{
		try { _settleSweep?.Cancel(); } catch { }
		_settleSweep = null;
		lock (_settledMax) { _settledMax.Clear(); }
	}

	private static void StopForegroundHook()
	{
		if (_fgHook != IntPtr.Zero)
		{
			try
			{
				UnhookWinEvent(_fgHook);
			}
			catch
			{
			}
			_fgHook = IntPtr.Zero;
		}
		_fgDelegate = null;
		if (_stateHook != IntPtr.Zero)
		{
			try
			{
				UnhookWinEvent(_stateHook);
			}
			catch
			{
			}
			_stateHook = IntPtr.Zero;
		}
		_stateDelegate = null;
		StopSettleSweep();
	}

	// A window's maximize/restore only changes its border requirement; re-run the flat-8.1 styling so a window
	// maximized after it was styled drops its edge line, and a restored window gets its grey border back. Cheap:
	// filtered to top-level window state changes, skips our own process, and no-ops unless 8.1 chrome is active.
	private static void OnStateChange(IntPtr hook, uint ev, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
	{
		try
		{
			if (hwnd == IntPtr.Zero || idObject != 0 || idChild != 0)   // OBJID_WINDOW, the window itself
			{
				return;
			}
			CompositionProfile p = AppliedProfile;
			if (p == null || !p.StyleForeignWindows || !CompositionCapabilities.SupportsFrameColors)
			{
				return;
			}
			if (Eligible(hwnd))
			{
				int brd = Flush(hwnd) ? DWMWA_COLOR_NONE : Win81BorderColor;
				DwmSetWindowAttribute(hwnd, 34, ref brd, 4);   // BORDER_COLOR only — the one attribute maximize affects
				ConsiderSettle(hwnd);                          // fix the intermittent ~2px unpainted edge on maximize
			}
		}
		catch
		{
		}
	}

	private static readonly HashSet<IntPtr> _settling = new HashSet<IntPtr>();     // during the restore->maximize toggle (loop guard)

	private static readonly HashSet<IntPtr> _settledMax = new HashSet<IntPtr>();   // windows already settled while currently maximized

	// Decide whether a window needs a one-shot settle: settle each window ONCE per time it is maximized (whether it
	// maximized via a transition or was born maximized and merely came to the foreground), and re-arm when it leaves
	// the maximized state. This avoids re-settling on every focus (which would flicker on each Alt-Tab).
	private static void ConsiderSettle(IntPtr h)
	{
		bool zoomed = IsZoomed(h);
		lock (_settledMax)
		{
			if (!zoomed) { _settledMax.Remove(h); return; }
			if (_settledMax.Contains(h)) return;
			_settledMax.Add(h);
		}
		SettleMaximized(h);
	}

	// A freshly-maximized window intermittently leaves a ~1-2px strip on one edge unpainted (the desktop shows
	// through) until its maximized client layout is recomputed. Nothing short of a restore->maximize toggle forces
	// that recompute (frame-change, resize jitter and NCPAINT all fail). So do exactly that, once, off the hook
	// thread, with DWM window transitions disabled so it is instant and produces no visible restore animation.
	// A guard set stops the toggle's own state changes from re-entering this path (which would loop forever).
	private static void SettleMaximized(IntPtr h)
	{
		lock (_settling)
		{
			if (_settling.Contains(h)) return;
			_settling.Add(h);
		}
		Task.Run(async delegate
		{
			try
			{
				// TRUE OS-maximize (square corners, edge-to-edge fill) - NOT a work-area fit. Measurement proved a
				// fit leaves framed apps (Chrome/Discord/Explorer) in the RESTORED visual state, where they draw a
				// resize frame + rounded corners + shadow: the desktop then shows through the corners and one edge -
				// the "window inside a window / corners showing / white top" the user reported. A real maximize has
				// square corners and its client fills every pixel of the work area; the -8,-8 overhang is only the
				// invisible maximize border and is NOT painted (verified: nothing spills onto the other monitor).
				// The restore->maximize toggle (transitions disabled, so it is instant and invisible) forces the
				// client layout to recompute, clearing the intermittent unpainted strip on a fresh maximize, and we
				// assert BORDER_COLOR=NONE so the global accent-on-borders setting (ColorPrevalence) draws no line.
				int noAnim = 1, anim = 0, none = DWMWA_COLOR_NONE;
				DwmSetWindowAttribute(h, 3, ref noAnim, 4);      // DWMWA_TRANSITIONS_FORCEDISABLED = 1 (instant, no flash)
				ShowWindow(h, 9);                                 // SW_RESTORE
				await Task.Delay(1).ConfigureAwait(false);
				ShowWindow(h, 3);                                 // SW_MAXIMIZE -> end in the true maximized state
				await Task.Delay(40).ConfigureAwait(false);
				DwmSetWindowAttribute(h, 34, ref none, 4);        // BORDER_COLOR = none (no accent border line on the edge)
				DwmSetWindowAttribute(h, 3, ref anim, 4);         // transitions back on
			}
			catch
			{
			}
			finally
			{
				await Task.Delay(400).ConfigureAwait(false);      // let the toggle's STATECHANGE events drain first
				lock (_settling) { _settling.Remove(h); }
			}
		});
	}

	[DllImport("user32.dll")]
	private static extern bool ShowWindow(nint hWnd, int nCmdShow);

	[DllImport("user32.dll", SetLastError = true)]
	private static extern bool SetWindowPos(nint hWnd, nint after, int x, int y, int cx, int cy, uint flags);

	private static bool WorkAreaOf(nint h, out WRECT w)
	{
		w = default;
		MINFO mi = new MINFO { cbSize = Marshal.SizeOf<MINFO>() };
		if (!GetMonitorInfo(MonitorFromWindow(h, 2u), ref mi)) return false;
		w = mi.rcWork;
		return true;
	}

	private static void OnForeground(IntPtr hook, uint ev, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
	{
		try
		{
			if (hwnd == IntPtr.Zero || idObject != 0)   // OBJID_WINDOW == 0: ignore caret/menu/etc. sub-objects
			{
				return;
			}
			if (Eligible(hwnd))
			{
				ApplyTo(hwnd, on: true, AppliedProfile);   // style the freshly-foregrounded window (excludes our own via Eligible's own-pid check)
				CompositionProfile p = AppliedProfile;
				if (p != null && p.StyleForeignWindows)
				{
					ConsiderSettle(hwnd);   // covers windows that OPEN already maximized (no state transition fires)
				}
			}
		}
		catch
		{
		}
	}

	// Returns true if the EnumWindows pass completed, false if it failed at the outer level — so ApplyCurrent can
	// report ApplyStatus.Partial honestly. The inner per-window try/catch stays (one bad window must never unwind
	// through native EnumWindows) and does NOT flip the result. Revert callers (Sweep(off)) ignore the bool.
	private static bool Sweep(bool on, CompositionProfile profile)
	{
		try
		{
			EnumWindows(delegate(nint h, nint _)
			{
				if (Eligible(h))
				{
					try
					{
						ApplyTo(h, on, profile);   // per-window guard: one bad window is skipped, never unwinds through native EnumWindows
					}
					catch
					{
					}
				}
				return true;
			}, IntPtr.Zero);
			return true;
		}
		catch (Exception ex)
		{
			Logger.Log("Desktop composition sweep failed: " + ex.Message);
			return false;
		}
	}

	private static bool SystemPrefersDark()
	{
		return ShellTheme.IsDark;   // single source of truth (cached + change-signalled)
	}

	private static bool Eligible(nint h)
	{
		if (!IsWindowVisible(h) || GetWindowTextLength(h) == 0)
		{
			return false;
		}
		if ((GetWindowLong(h, -20) & 0x80) != 0)
		{
			return false;
		}
		if (DwmGetWindowAttribute(h, 14, out var cloaked, 4) == 0 && cloaked != 0)
		{
			return false;
		}
		GetWindowThreadProcessId(h, out var pid);
		return pid != OwnPid;
	}

	// Authentic flat Windows 8.1 window chrome (COLORREF 0x00BBGGRR): light silver caption, near-black title text,
	// thin grey border, square corners. Tunable here.
	private const int Win81CaptionColor = 0x00EAEAEA;
	private const int Win81TextColor = 0x001A1A1A;
	private const int Win81BorderColor = 0x00B0B0B0;

	private static void ApplyTo(nint h, bool on, CompositionProfile p)
	{
		// Apply the flat 8.1 chrome to a FOREIGN window only when the target profile asks for it (StyleForeignWindows =
		// the authentic 8.1 mode) AND we are applying (on). Otherwise reset every attribute to the Windows default —
		// this is also the revert path (on=false → back to native caption/border colour + rounded corners). The
		// launcher's OWN windows are handled by a separate owned-frame renderer and excluded by Eligible()'s pid check.
		bool style81 = on && p != null && p.StyleForeignWindows && CompositionCapabilities.SupportsFrameColors;
		if (CompositionCapabilities.SupportsDarkTitleBar)
		{
			int dark = style81 ? 0 : (SystemPrefersDark() ? 1 : 0);   // 8.1 chrome is light; else follow the system theme
			DwmSetWindowAttribute(h, CompositionCapabilities.DarkModeAttr, ref dark, 4);
		}
		if (CompositionCapabilities.SupportsFrameColors)
		{
			if (style81)
			{
				int cap = Win81CaptionColor;
				int txt = Win81TextColor;
				// A maximized window must have NO border colour: on Win11 a coloured BORDER_COLOR leaves a 1-2px
				// line at the screen edges (and across the monitor seam, so it reads as "spilling" onto the second
				// monitor). Authentic 8.1 maximized windows are flush to the edge. Grey border only when windowed.
				int brd = Flush(h) ? DWMWA_COLOR_NONE : Win81BorderColor;
				int corner = 1;                              // DWMWCP_DONOTROUND = square 8.1 corners
				DwmSetWindowAttribute(h, 35, ref cap, 4);    // CAPTION_COLOR
				DwmSetWindowAttribute(h, 36, ref txt, 4);    // TEXT_COLOR
				DwmSetWindowAttribute(h, 34, ref brd, 4);    // BORDER_COLOR
				DwmSetWindowAttribute(h, 33, ref corner, 4);
			}
			else
			{
				int def = -1;   // -1 = restore Windows default (no custom colour)
				DwmSetWindowAttribute(h, 35, ref def, 4);   // CAPTION_COLOR -> native
				DwmSetWindowAttribute(h, 36, ref def, 4);   // TEXT_COLOR -> native
				DwmSetWindowAttribute(h, 34, ref def, 4);   // BORDER_COLOR -> native (removes the accent border line)
				int corner = 0;                              // 0 = native corner shape (restores the rounded resize grip)
				DwmSetWindowAttribute(h, 33, ref corner, 4);
			}
		}
		if (CompositionCapabilities.SupportsSystemBackdrop)
		{
			int backdrop = 0;   // 0 = auto / native
			DwmSetWindowAttribute(h, 38, ref backdrop, 4);
		}
		if (CompositionCapabilities.SupportsTransitionToggle)
		{
			int noTrans = 0;   // native window transitions enabled
			DwmSetWindowAttribute(h, 3, ref noTrans, 4);
		}
		// DWMNCRP_USEWINDOWSTYLE is the native/default policy. DWMNCRP_ENABLED (2) is not a reset: it forces
		// non-client rendering into client-drawn Electron/Chromium windows and can expose a thin edge frame.
		int ncrp = 0;
		DwmSetWindowAttribute(h, 2, ref ncrp, 4);
	}

	// Returns true on success, false if the registry write / broadcast failed — so ApplyCurrent can honestly report
	// ApplyStatus.Partial instead of always claiming Ok (the failure detail is logged). Revert callers ignore the bool.
	private static bool ApplyAccent()
	{
		try
		{
			Color c = StartAccent.Color();
			if (AccentMatches(c))
			{
				return true;
			}
			SnapshotAccentOnce();
			int abgr = -16777216 | (c.B << 16) | (c.G << 8) | c.R;
			using (RegistryKey acc = Registry.CurrentUser.CreateSubKey("Software\\Microsoft\\Windows\\CurrentVersion\\Explorer\\Accent"))
			{
				acc.SetValue("AccentColorMenu", abgr, RegistryValueKind.DWord);
				acc.SetValue("AccentPalette", BuildPalette(c), RegistryValueKind.Binary);
				acc.SetValue("StartColorMenu", ShadeAbgr(c, -0.35), RegistryValueKind.DWord);
			}
			using (RegistryKey dwm = Registry.CurrentUser.CreateSubKey("Software\\Microsoft\\Windows\\DWM"))
			{
				dwm.SetValue("AccentColor", abgr, RegistryValueKind.DWord);
				dwm.SetValue("ColorizationColor", -1006632960 | (c.R << 16) | (c.G << 8) | c.B, RegistryValueKind.DWord);
				dwm.SetValue("ColorPrevalence", 1, RegistryValueKind.DWord);
				dwm.SetValue("AccentColorInactive", InactiveAbgr(c), RegistryValueKind.DWord);
			}
			using (RegistryKey pers = Registry.CurrentUser.CreateSubKey("Software\\Microsoft\\Windows\\CurrentVersion\\Themes\\Personalize"))
			{
				pers?.SetValue("ColorPrevalence", 1, RegistryValueKind.DWord);
			}
			BroadcastColor();
			return true;
		}
		catch (Exception ex)
		{
			Logger.Log("Accent title bars apply failed: " + ex.Message);
			return false;
		}
	}

	public static void RefreshDwmBlurGlassPolicy()
	{
		CompositionProfile target = CurrentProfile;
		QueueTransition(AppliedProfile, target, "composition policy refresh");
		RaiseModeChanged();
	}

	private static bool AccentMatches(Color c)
	{
		try
		{
			int abgr = -16777216 | (c.B << 16) | (c.G << 8) | c.R;
			int colorization = -1006632960 | (c.R << 16) | (c.G << 8) | c.B;
			using RegistryKey acc = Registry.CurrentUser.OpenSubKey(AccentKey);
			using RegistryKey dwm = Registry.CurrentUser.OpenSubKey(DwmKey);
			using RegistryKey pers = Registry.CurrentUser.OpenSubKey(PersonalizeKey);
			return IntValueEquals(acc, "AccentColorMenu", abgr)
				&& ByteValueEquals(acc, "AccentPalette", BuildPalette(c))
				&& IntValueEquals(acc, "StartColorMenu", ShadeAbgr(c, -0.35))
				&& IntValueEquals(dwm, "AccentColor", abgr)
				&& IntValueEquals(dwm, "ColorizationColor", colorization)
				&& IntValueEquals(dwm, "ColorPrevalence", 1)
				&& IntValueEquals(dwm, "AccentColorInactive", InactiveAbgr(c))
				&& IntValueEquals(pers, "ColorPrevalence", 1);
		}
		catch
		{
			return false;
		}
	}

	private static bool IntValueEquals(RegistryKey key, string name, int expected)
	{
		return key?.GetValue(name) is int value && value == expected;
	}

	private static bool ByteValueEquals(RegistryKey key, string name, byte[] expected)
	{
		return key?.GetValue(name) is byte[] value && value.AsSpan().SequenceEqual(expected);
	}

	private static void RevertAccent()
	{
		try
		{
			ForceTitleBarAccentOff();   // clean native baseline FIRST (works even with no/broken snapshot)
			if (File.Exists(SnapPath))
			{
				try
				{
					Dictionary<string, string> snap = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(SnapPath));
					if (snap != null)
					{
						// Restore EVERY captured value, including the original ColorPrevalence/AccentColorInactive, so a
						// user who already had accent-coloured title bars before Alchemy keeps them.
						(string, string)[] accentValues = AccentValues;
						for (int i = 0; i < accentValues.Length; i++)
						{
							var (key, name) = accentValues[i];
							using RegistryKey k = Registry.CurrentUser.CreateSubKey(key);
							if (k != null)
							{
								if (name == "ColorPrevalence") { continue; }   // never restore the accent title-bar/border flag: native must stay off, and a snapshot captured while 8.1/Aero was already applied can be poisoned with 1 (would redraw the thin accent line on maximized windows)
								string v = snap.GetValueOrDefault(key + "\\" + name);
								if (v == null)
								{
									k.DeleteValue(name, throwOnMissingValue: false);
								}
								else if (name == "AccentPalette")
								{
									k.SetValue(name, HexToBytes(v), RegistryValueKind.Binary);
								}
								else
								{
									k.SetValue(name, (int)uint.Parse(v), RegistryValueKind.DWord);
								}
							}
						}
					}
				}
				catch (Exception exSnap)
				{
					Logger.Log("Accent snapshot restore failed (kept force-off baseline): " + exSnap.Message);
				}
				finally
				{
					try
					{
						File.Delete(SnapPath);   // always drop it so the next apply re-captures a clean restore point
					}
					catch
					{
					}
				}
			}
			BroadcastColor();
		}
		catch (Exception ex)
		{
			Logger.Log("Accent title bars revert failed: " + ex.Message);
		}
	}

	private static void ForceTitleBarAccentOff()
	{
		using (RegistryKey dwm = Registry.CurrentUser.CreateSubKey("Software\\Microsoft\\Windows\\DWM"))
		{
			dwm?.SetValue("ColorPrevalence", 0, RegistryValueKind.DWord);
			dwm?.DeleteValue("AccentColorInactive", throwOnMissingValue: false);
		}
		using RegistryKey pers = Registry.CurrentUser.CreateSubKey("Software\\Microsoft\\Windows\\CurrentVersion\\Themes\\Personalize");
		pers?.SetValue("ColorPrevalence", 0, RegistryValueKind.DWord);
	}

	// Reads the live Windows "accent colour on title bars and window borders" flag (DWM ColorPrevalence).
	// Native mode must keep it OFF; a stuck 1 (left by an earlier 8.1/Aero session) is what draws the accent
	// edge line on maximised windows. Read from the registry, which is consistent across processes, rather than
	// inferring from the snapshot file.
	private static bool AccentBorderIsOn()
	{
		try
		{
			using RegistryKey dwm = Registry.CurrentUser.OpenSubKey("Software\\Microsoft\\Windows\\DWM");
			return dwm?.GetValue("ColorPrevalence") is int v && v == 1;
		}
		catch
		{
			return false;
		}
	}

	private static void SnapshotAccentOnce()
	{
		if (File.Exists(SnapPath))
		{
			return;
		}
		Dictionary<string, string> snap = new Dictionary<string, string>();
		(string, string)[] accentValues = AccentValues;
		for (int i = 0; i < accentValues.Length; i++)
		{
			var (key, name) = accentValues[i];
			using RegistryKey k = Registry.CurrentUser.OpenSubKey(key);
			object val = k?.GetValue(name);
			string value;
			if (val is int i2)
			{
				value = ((uint)i2).ToString();
			}
			else if (val is byte[] b)
			{
				value = Convert.ToHexString(b);
			}
			else
			{
				value = null;
			}
			snap[key + "\\" + name] = value;
		}
		Directory.CreateDirectory(Path.GetDirectoryName(SnapPath));
		string tmp = SnapPath + ".tmp";
		File.WriteAllText(tmp, JsonSerializer.Serialize(snap));   // atomic: write tmp then rename, so a crash mid-write can't corrupt the restore point
		File.Move(tmp, SnapPath, overwrite: true);
	}

	private static byte[] BuildPalette(Color c)
	{
		byte[] p = new byte[32];
		Put(0, Lgt(c.R, 0.44), Lgt(c.G, 0.44), Lgt(c.B, 0.44));
		Put(1, Lgt(c.R, 0.31), Lgt(c.G, 0.31), Lgt(c.B, 0.31));
		Put(2, Lgt(c.R, 0.18), Lgt(c.G, 0.18), Lgt(c.B, 0.18));
		Put(3, c.R, c.G, c.B);
		Put(4, Drk(c.R, 0.29), Drk(c.G, 0.29), Drk(c.B, 0.29));
		Put(5, Drk(c.R, 0.5), Drk(c.G, 0.5), Drk(c.B, 0.5));
		Put(6, Drk(c.R, 0.67), Drk(c.G, 0.67), Drk(c.B, 0.67));
		Put(7, Drk(c.R, 0.78), Drk(c.G, 0.78), Drk(c.B, 0.78));
		return p;
		static byte Drk(byte v, double f)
		{
			return (byte)Math.Clamp((double)(int)v * (1.0 - f), 0.0, 255.0);
		}
		static byte Lgt(byte v, double f)
		{
			return (byte)Math.Clamp((double)(int)v + (double)(255 - v) * f, 0.0, 255.0);
		}
		void Put(int i, byte r, byte g, byte b)
		{
			p[i * 4] = r;
			p[i * 4 + 1] = g;
			p[i * 4 + 2] = b;
			p[i * 4 + 3] = 0;
		}
	}

	private static int ShadeAbgr(Color c, double f)
	{
		return -16777216 | (S(c.B) << 16) | (S(c.G) << 8) | S(c.R);
		byte S(byte v)
		{
			return (byte)Math.Clamp((f < 0.0) ? ((double)(int)v * (1.0 + f)) : ((double)(int)v + (double)(255 - v) * f), 0.0, 255.0);
		}
	}

	private static int InactiveAbgr(Color c)
	{
		return -16777216 | (Dim(c.B) << 16) | (Dim(c.G) << 8) | Dim(c.R);
		static byte Dim(byte v)
		{
			return (byte)Math.Clamp((double)(int)v + (double)(236 - v) * 0.58, 0.0, 255.0);
		}
	}

	private static byte[] HexToBytes(string hex)
	{
		return Convert.FromHexString(hex);
	}

	private static void BroadcastColor()
	{
		try
		{
			Interlocked.Increment(ref _accentBroadcastBatches);
			// HWND_BROADCAST + SendMessageTimeout applies its timeout to every recipient and can block a
			// composition switch for more than a second when one or more desktop apps are busy. Post a
			// generic settings notification to each top-level window instead. No unmanaged string pointer
			// is queued, and the hidden Explorer taskbars are left alone while our taskbar owns the shell.
			EnumWindows(delegate(nint h, nint _)
			{
				if (!IsExplorerTaskbar(h))
				{
					PostMessage(h, WM_DWMCOLORIZATIONCOLORCHANGED, IntPtr.Zero, IntPtr.Zero);
					PostMessage(h, WM_SETTINGCHANGE, IntPtr.Zero, IntPtr.Zero);
				}
				return true;
			}, IntPtr.Zero);
		}
		catch
		{
		}
	}

	private static bool IsExplorerTaskbar(nint h)
	{
		StringBuilder className = new(64);
		if (GetClassName(h, className, className.Capacity) == 0)
		{
			return false;
		}

		return className.ToString() is "Shell_TrayWnd" or "Shell_SecondaryTrayWnd";
	}

	[DllImport("user32.dll")]
	private static extern bool EnumWindows(EnumProc cb, nint l);

	[DllImport("user32.dll")]
	private static extern IntPtr SetWinEventHook(uint min, uint max, IntPtr hmod, WinEventDelegate cb, uint pid, uint thread, uint flags);

	[DllImport("user32.dll")]
	private static extern bool UnhookWinEvent(IntPtr hook);

	[DllImport("user32.dll")]
	private static extern bool IsWindowVisible(nint h);

	[DllImport("user32.dll")]
	private static extern bool IsZoomed(nint h);

	[StructLayout(LayoutKind.Sequential)]
	private struct WRECT { public int Left, Top, Right, Bottom; }

	[StructLayout(LayoutKind.Sequential)]
	private struct MINFO { public int cbSize; public WRECT rcMonitor; public WRECT rcWork; public uint dwFlags; }

	[DllImport("user32.dll")]
	private static extern bool GetWindowRect(nint h, out WRECT r);

	[DllImport("user32.dll")]
	private static extern nint MonitorFromWindow(nint h, uint flags);

	[DllImport("user32.dll")]
	private static extern bool GetMonitorInfo(nint h, ref MINFO mi);

	// A window that covers its monitor's WORK AREA is effectively maximized/immersive even if it isn't in the OS
	// "maximized" state (e.g. a frameless app that sizes itself to the work area, like Metro Browser). Such a window
	// should be flush with no 8.1 border line, exactly like a real maximized window.
	private static bool FillsWorkArea(nint h)
	{
		try
		{
			if (!GetWindowRect(h, out WRECT r)) return false;
			MINFO mi = new MINFO { cbSize = Marshal.SizeOf<MINFO>() };
			if (!GetMonitorInfo(MonitorFromWindow(h, 2u), ref mi)) return false;   // MONITOR_DEFAULTTONEAREST
			WRECT w = mi.rcWork;
			return r.Left <= w.Left + 2 && r.Top <= w.Top + 2 && r.Right >= w.Right - 2 && r.Bottom >= w.Bottom - 2;
		}
		catch { return false; }
	}
	private static bool Flush(nint h) => IsZoomed(h) || FillsWorkArea(h);

	[DllImport("user32.dll")]
	private static extern int GetWindowTextLength(nint h);

	[DllImport("user32.dll")]
	private static extern int GetWindowLong(nint h, int i);

	[DllImport("user32.dll")]
	private static extern uint GetWindowThreadProcessId(nint h, out uint pid);

	[DllImport("user32.dll", CharSet = CharSet.Unicode)]
	private static extern int GetClassName(nint hWnd, StringBuilder className, int maxCount);

	[DllImport("user32.dll", SetLastError = true)]
	private static extern bool PostMessage(nint hWnd, uint msg, nint wParam, nint lParam);

	[DllImport("dwmapi.dll")]
	private static extern int DwmSetWindowAttribute(nint h, int attr, ref int val, int size);

	[DllImport("dwmapi.dll")]
	private static extern int DwmGetWindowAttribute(nint h, int attr, out int val, int size);
}

// Snapshot of the composition state for the Desktop Composition settings page (Current Status block).
public sealed class CompositionStatus
{
	public string ModeId = "native";

	public string ModeName = "Windows Native";

	public bool Transparency;

	public bool Shadows;

	public bool Animations;

	public string Backend = "Windows DWM";

	public string DwmBlurGlassPolicy = "Disabled - owned frames only";

	public string DwmBlurGlassState = "Not installed - lightweight frame fallback";

	public string Status = "Windows Native";

	public bool RestoreAvailable;

	public bool Previewing;

	public bool Transitioning;

	public List<string> Unsupported = new List<string>();
}
