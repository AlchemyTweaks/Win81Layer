using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Forms;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;

namespace Win81Layer;

public partial class App : System.Windows.Application
{
	[Serializable]
	[CompilerGenerated]
	private sealed class _003C_003Ec
	{
		public static readonly _003C_003Ec _003C_003E9 = new _003C_003Ec();

		public static Func<(AppEntry Entry, AppInventory.IShellItem Item), AppEntry> _003C_003E9__30_104;

		public static Func<AppEntry, string> _003C_003E9__30_106;

		public static Func<SearchExtras.Setting, string> _003C_003E9__30_107;

		public static Func<JumpList.Item, string> _003C_003E9__30_108;

		public static Action<AppEntry> _003C_003E9__30_114;

		public static Action _003C_003E9__30_119;

		public static Action _003C_003E9__30_120;

		public static Action _003C_003E9__30_121;

		public static Action _003C_003E9__30_122;

		public static Func<IReadOnlyList<CharmListPane.Row>> _003C_003E9__30_118;

		public static DispatcherUnhandledExceptionEventHandler _003C_003E9__30_2;

		public static UnhandledExceptionEventHandler _003C_003E9__30_3;

		public static EventHandler<UnobservedTaskExceptionEventArgs> _003C_003E9__30_5;

		public static Action<AppSettings> _003C_003E9__30_6;

		public static Action _003C_003E9__30_7;

		public static Action _003C_003E9__30_128;

		public static Action _003C_003E9__30_129;

		public static Action _003C_003E9__30_130;

		public static Action _003C_003E9__30_131;

		public static Action _003C_003E9__30_31;

		public static Action _003C_003E9__30_145;

		public static Action _003C_003E9__30_146;

		public static Action _003C_003E9__30_147;

		public static Action _003C_003E9__30_148;

		public static CancelEventHandler _003C_003E9__30_49;

		public static Action _003C_003E9__30_171;

		public static Action _003C_003E9__30_175;

		public static EventHandler _003C_003E9__30_92;

		public static EventHandler _003C_003E9__30_94;

		internal AppEntry _003COnStartup_003Eb__30_104((AppEntry Entry, AppInventory.IShellItem Item) t)
		{
			return t.Entry;
		}

		internal string _003COnStartup_003Eb__30_106(AppEntry a)
		{
			return a.Name;
		}

		internal string _003COnStartup_003Eb__30_107(SearchExtras.Setting s)
		{
			return s.Name;
		}

		internal string _003COnStartup_003Eb__30_108(JumpList.Item i)
		{
			return i.Name;
		}

		internal void _003COnStartup_003Eb__30_114(AppEntry _)
		{
		}

		internal IReadOnlyList<CharmListPane.Row> _003COnStartup_003Eb__30_118()
		{
			return new CharmListPane.Row[4]
			{
				new CharmListPane.Row(59240, "Play", "Play on TV", delegate
				{
				}),
				new CharmListPane.Row(59209, "Print", "Print", delegate
				{
				}),
				new CharmListPane.Row(59380, "Project", "Project to a second screen", delegate
				{
				}),
				new CharmListPane.Row(59152, "Add a device", "Add a device", delegate
				{
				})
			};
		}

		internal void _003COnStartup_003Eb__30_119()
		{
		}

		internal void _003COnStartup_003Eb__30_120()
		{
		}

		internal void _003COnStartup_003Eb__30_121()
		{
		}

		internal void _003COnStartup_003Eb__30_122()
		{
		}

		internal void _003COnStartup_003Eb__30_2(object _, DispatcherUnhandledExceptionEventArgs args)
		{
			Logger.Log($"Dispatcher exception: {args.Exception}");
			args.Handled = true;
		}

		internal void _003COnStartup_003Eb__30_3(object _, UnhandledExceptionEventArgs args)
		{
			Logger.Log($"Unhandled exception: {args.ExceptionObject}");
			NativeShell.ResumeShellHosts();
			AppBar.ShowNativeTaskbar();
		}

		internal void _003COnStartup_003Eb__30_5(object? _, UnobservedTaskExceptionEventArgs args)
		{
			Logger.Log($"Unobserved task exception: {args.Exception}");
			args.SetObserved();
		}

		internal void _003COnStartup_003Eb__30_6(AppSettings s)
		{
			s.PackedGrid = false;
			s.FreeGridDefaultV1 = true;
		}

		internal void _003COnStartup_003Eb__30_7()
		{
			try
			{
				LogonTask.SetEnabled(enabled: true);
			}
			catch
			{
			}
			try
			{
				if (Autostart.IsEnabled())
				{
					Autostart.SetEnabled(enabled: false);
					Logger.Log("Autostart de-dup: removed HKCU Run key - logon task is the single autostart");
				}
			}
			catch
			{
			}
		}

		internal void _003COnStartup_003Eb__30_128()
		{
			Keystroke.Chord(91, 75);
		}

		internal void _003COnStartup_003Eb__30_129()
		{
			CharmListPane.Launch("DisplaySwitch.exe");
		}

		internal void _003COnStartup_003Eb__30_130()
		{
			CharmListPane.Launch("ms-settings:bluetooth");
		}

		internal void _003COnStartup_003Eb__30_131()
		{
			CharmListPane.Launch("ms-windows-store:");
		}

		internal void _003COnStartup_003Eb__30_31()
		{
			try
			{
				Process.GetCurrentProcess().PriorityClass = ProcessPriorityClass.Normal;
			}
			catch
			{
			}
		}

		internal void _003COnStartup_003Eb__30_145()
		{
			SnapService.SnapForeground(left: true);
		}

		internal void _003COnStartup_003Eb__30_146()
		{
			SnapService.SnapForeground(left: false);
		}

		internal void _003COnStartup_003Eb__30_147()
		{
			SnapService.MoveForegroundToNeighbor(left: true);
		}

		internal void _003COnStartup_003Eb__30_148()
		{
			SnapService.MoveForegroundToNeighbor(left: false);
		}

		internal void _003COnStartup_003Eb__30_49(object? sender, CancelEventArgs e)
		{
			ForegroundContext.Capture();
		}

		internal void _003COnStartup_003Eb__30_171()
		{
			TaskbarWindow.ReloadPins();
		}

		internal void _003COnStartup_003Eb__30_175()
		{
			TaskbarWindow.ReloadPerfMode();
		}

		internal void _003COnStartup_003Eb__30_92(object? sender, EventArgs e)
		{
			GoogleCalendarConnectWindow.ShowFor();
		}

		internal void _003COnStartup_003Eb__30_94(object? sender, EventArgs e)
		{
			ShellRestart.RestartExplorer();
		}
	}

	private StartScreen? _startScreen;

	private Win7StartMenu? _win7Start;   // lazily built; only used when AppSettings.Win7StartMenuEnabled

	private CharmsBar? _charmsBar;

	private SettingsPane? _settingsPane;

	private SearchPane? _searchPane;

	private CharmListPane? _devicesPane;

	private CharmListPane? _sharePane;

	private PcSettingsWindow? _pcSettings;

	private AppSwitcher? _appSwitcher;

	private SnapAssist? _snapAssist;

	private SnapWatcher? _snapWatcher;

	private NotificationRouter? _notifRouter;

	private HotCorners? _hotCorners;

	private CharmsEdgeGesture? _charmsGesture;

	private TaskbarManager? _taskbar;

	private WinKeyHook? _winHook;

	private readonly AudioController _osdAudio = new AudioController();

	private NotifyIcon? _trayIcon;

	private DispatcherTimer? _idleTrimTimer;

	private Process? _watchdog;

	private DispatcherTimer? _shellTimer;

	private bool _managed;   // true when launched by the supervisor (--managed) Ã¢â€ â€™ do not spawn a self-watchdog

	private volatile bool _sessionEnding;

	private int _cleanupStarted;

	private Mutex? _instanceMutex;

	private bool _isPrimary;

	private IdleAutoLock? _idleAutoLock;

	private int _lastToggleTick;

	private static string CleanExitMarkerPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Win81Layer", "clean-exit.marker");

	private static string RelaunchStampPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Win81Layer", "shell-relaunch.txt");

	// Safe-mode boot: count of consecutive unhealthy boots (within a 15-min window). After SafeModeThreshold the
	// supervisor boots the shell with only CORE surfaces (taskbar+Start); after SafeModeThreshold+SafeModeMaxAttempts
	// it stands down to the native shell. The counter is CLEARED after ~90s of sustained health (see OnStartup), and
	// the 15-min window is a hard age cap — together these guarantee safe-mode can never get permanently stuck.
	private static string UnhealthyBootStampPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Win81Layer", "unhealthy-boots.txt");

	private const int SafeModeThreshold = 2;

	private const int SafeModeMaxAttempts = 2;

	private const double UnhealthyBootWindowMinutes = 15.0;

	private bool _safeMode;   // true when the supervisor escalated to --safe-mode after repeated unhealthy boots

	// Read by subsystems (e.g. StartScreen live tiles) to skip risky work in safe mode. In-memory ONLY — never
	// persisted to settings/profile, so it always resets to false on the next clean boot (cannot get stuck).
	internal static bool SafeMode { get; private set; }

	// ---- hang detection (frozen-UI heartbeat) ----
	// The UI thread stamps HeartbeatPath with Environment.TickCount64 on every _shellTimer tick. A frozen UI
	// thread can't run the DispatcherTimer, so the stamp goes stale. The watchdog (a separate process, same
	// system tick clock) compares the stamp to its own TickCount64; if the shell stops stamping for
	// HeartbeatHangThresholdMs AND the OS confirms the window is hung (or it stays stale past the hard
	// timeout), it kills the frozen shell and relaunches it through the same path a crash uses.
	private const long HeartbeatHangThresholdMs = 12000L;
	private const long HeartbeatHardHangMs = 25000L;

	// A shell that never produces a fresh (this-session) heartbeat within this window is treated as wedged-at-startup
	// and recovered (kill + --healed relaunch) Ã¢â‚¬â€ the auto-fix for a post-reboot cold-boot shell that never comes up.
	private const long StartupGraceMs = 90000L;
	private const int WatchdogPollMs = 2000;

	// pid of the shell the supervisor last launched. Enables HANG-PROOF liveness/kill by pid (a plain OpenProcess),
	// instead of Process.GetProcessesByName which can stall during the boot storm and freeze the whole recovery loop.
	private static int _supManagedPid;

	private static int _heartbeatTick;   // gate WriteHeartbeat to ~every 4th shell tick (~4s)

	private static System.DateTime _supManagedLaunchUtc;   // when the CURRENT healthy managed shell was launched (for young-death detection)

	private static string HeartbeatPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Win81Layer", "heartbeat.dat");

	private static void WriteHeartbeat()
	{
		try
		{
			using FileStream fs = new FileStream(HeartbeatPath, FileMode.Create, FileAccess.Write, FileShare.ReadWrite);
			// WALL-CLOCK, not Environment.TickCount64: TickCount64 resets to ~0 on reboot while heartbeat.dat persists,
			// which made the watchdog mis-read a pre-reboot beat as a bogus/future age and never recover a wedged boot.
			byte[] bytes = BitConverter.GetBytes(DateTime.UtcNow.Ticks);
			fs.Write(bytes, 0, bytes.Length);
		}
		catch
		{
		}
	}

	private static long ReadHeartbeat()
	{
		try
		{
			using FileStream fs = new FileStream(HeartbeatPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
			byte[] bytes = new byte[8];
			if (fs.Read(bytes, 0, 8) == 8)
			{
				return BitConverter.ToInt64(bytes, 0);
			}
		}
		catch
		{
		}
		return -1L;
	}

	[DllImport("user32.dll")]
	private static extern bool IsHungAppWindow(IntPtr hwnd);

	private static bool IsShellWindowHung(Process p)
	{
		try
		{
			p.Refresh();
			IntPtr h = p.MainWindowHandle;
			if (h == IntPtr.Zero)
			{
				return false;   // no top-level window to query Ã¢â‚¬â€ caller falls back to the hard timeout
			}
			return IsHungAppWindow(h);
		}
		catch
		{
			return false;
		}
	}

		// ---- live-apply hooks for the PC Settings "Launcher" page ----
	// PcSettingsWindow persists launcher settings via SettingsStore.Update, then calls these on the running App
	// (Application.Current as App) to apply the change LIVE through the App-private shell objects it cannot otherwise
	// reach Ã¢â‚¬â€ mirroring exactly what the tray-menu handlers do, so both surfaces stay in sync.
	public void ApplyHotCorners(bool on)
	{
		try { if (_hotCorners != null) { _hotCorners.Enabled = on; } } catch { }
	}

	// Turn the app-switcher mouse reveal (left edge + top-left corner) on/off live. When off, the switcher opens only
	// via Win+Tab or the taskbar Task View button.
	public void ApplySwitcherEdge(bool on)
	{
		try
		{
			if (_hotCorners == null) return;
			Action reveal = delegate { _appSwitcher?.ShowSwitcher(); };
			_hotCorners.LeftEdge = on ? reveal : null;
			_hotCorners.TopLeft = on ? reveal : null;
		}
		catch { }
	}

	public void ApplyReplaceStartButton(bool on)
	{
		try { if (_winHook != null) { _winHook.ReplaceStartButton = on; } } catch { }
	}

	public void ApplySuspendNativeStart(bool on)
	{
		try { if (on) { NativeShell.SuspendShellHosts(); } else { NativeShell.ResumeShellHosts(); } } catch { }
	}

	public void ApplyTaskbarEnabled(bool on)
	{
		try { if (_taskbar != null) { if (on) { _taskbar.Activate(); } else { _taskbar.Deactivate(); } } } catch { }
	}

	// Dominant mode cascades: it also flips suspend-native and the custom taskbar (matches the tray handler, which
	// toggles those two checkboxes). PcSettingsWindow persists all three fields; here we apply all three live.
	public void ApplyDominantMode(bool on)
	{
		try
		{
			if (_winHook != null) { _winHook.DominantMode = on; }
			ApplySuspendNativeStart(on);
			ApplyTaskbarEnabled(on);
		}
		catch { }
	}

	// Applies the already-persisted profile to live shell objects. Registry-backed side effects and composition are
	// handled by ShellProfileManager before this call; this method owns only App-private windows/hooks.
	// Dominance backstop: the composition/native-suppression applied at startup reads DesktopCompositionMode via an early
	// SettingsStore.Load(); if the config folder is briefly locked (session-ending churn / cold boot) that read returns
	// DEFAULTS (native, no suspend), leaving the native taskbar/Start showing (verified: those starts logged
	// "resumed(all)" + "composition native" and never suspended). A few seconds in the golden-primed settings are
	// reliable, so re-assert the persisted composition + native suppression. Idempotent (SuspendShellHosts self-guards).
	private void ScheduleDominanceBackstop()
	{
		int[] delays = new int[2] { 4000, 12000 };
		foreach (int ms in delays)
		{
			DispatcherTimer t = new DispatcherTimer((DispatcherPriority)4)
			{
				Interval = TimeSpan.FromMilliseconds(ms)
			};
			t.Tick += delegate
			{
				t.Stop();
				try
				{
					// Forced read bypasses any poisoned early cache (see SettingsStore.LoadForced): the persisted
					// dominance config is authoritative, never a native-defaults fallback cached during a cold-boot race.
					AppSettings s = SettingsStore.LoadForced();
					bool suspended = false;
					bool taskbar = false;
					bool reapplied = false;
					if (s.SuspendNativeStart)
					{
						NativeShell.SuspendShellHosts();   // idempotent-guarded; no-op if already suspended
						NativeShell.ReassertSuspension();   // catch any host Windows respawned during boot -> re-suspend + trim to WS ~0
						suspended = true;
					}
					if (s.TaskbarEnabled)
					{
						ApplyTaskbarEnabled(on: true);
						taskbar = true;
					}
					if (!string.Equals(DesktopComposition.AppliedMode, s.DesktopCompositionMode, StringComparison.OrdinalIgnoreCase))
					{
						DesktopComposition.ApplyPersistedModeTransition(s.DesktopCompositionMode);
						reapplied = true;
					}
					Logger.Log($"Dominance backstop @{ms}ms: want(mode={s.DesktopCompositionMode}, suspend={s.SuspendNativeStart}, taskbar={s.TaskbarEnabled}) applied={DesktopComposition.AppliedMode}; actions(suspend={suspended}, taskbar={taskbar}, reapplyComposition={reapplied})");
				}
				catch (Exception ex)
				{
					Logger.Log("Dominance backstop failed: " + ex.Message);
				}
			};
			t.Start();
		}
	}

	public void ApplyShellProfileLive(AppSettings settings)
	{
		if (settings == null)
		{
			throw new ArgumentNullException(nameof(settings));
		}
		try
		{
			if (_win7Start != null && _win7Start.IsVisible)
			{
				_win7Start.Dismiss();
			}
			if (_winHook != null)
			{
				_winHook.ReplaceStartButton = settings.ReplaceStartMenu;
				_winHook.ReplaceDesktopMenu = settings.ReplaceDesktopMenu;
				_winHook.DominantMode = settings.DominantMode;
			}
			ApplyHotCorners(settings.HotCornersEnabled);
			ApplySuspendNativeStart(settings.SuspendNativeStart);
			ApplyTaskbarEnabled(settings.TaskbarEnabled);
			if (!settings.TaskbarEnabled)
			{
				AppBar.ShowNativeTaskbar();
			}
			TileMetrics.Scale = settings.TileScale;
			_startScreen?.SetTileDensity(settings.TileScale);
			_startScreen?.RefreshBackground();
			Motion.Mode = settings.DeskCompOptimizePerf
				? MotionMode.Reduced
				: Motion.Parse(settings.MotionMode);
			TaskbarWindow.RaiseTaskbarPositionChanged();
			TaskbarWindow.RaiseTaskbarSizeChanged();
			TaskbarWindow.RaiseTaskbarAlignmentChanged();
			TaskbarWindow.RaiseTaskbarButtonsChanged();
			TaskbarWindow.RaiseTaskbarColorChanged();
			TaskbarWindow.RaiseTaskbarLayoutChanged();
			TaskbarWindow.RaiseAutoHideChanged();
			Win81Window.RefreshAllAccents();
			Win81Window.RefreshAllShadows();
			Logger.Log($"Live shell profile refreshed: win7Start={settings.Win7StartMenuEnabled}, taskbar={settings.TaskbarEnabled}, dominant={settings.DominantMode}");
		}
		catch (Exception ex)
		{
			Logger.Log("Live shell profile refresh failed: " + ex.Message);
			throw;
		}
	}

	public void ApplyTileDensity(double scale)
	{
		try { TileMetrics.Scale = scale; _startScreen?.SetTileDensity(scale); } catch { }
	}

	public void ApplyPackedGrid(bool on)
	{
		try { TilePanel.Packed = on; _startScreen?.RepackTiles(); } catch { }
	}

	public void ApplyAutoLock(int minutes)
	{
		try { _idleAutoLock?.SetMinutes(minutes); } catch { }
	}

	public void ApplyWeatherCity(string city)
	{
		try { _startScreen?.SetWeatherCity(city); } catch { }
	}

	public void ApplyWeatherUnits(string units)
	{
		try { _startScreen?.SetWeatherUnits(units); } catch { }
	}

	private NewsWindow? _newsWindow;
	public void ApplyNewsSource(string url)
	{
		_startScreen?.SetNewsSource(url);
		if (_newsWindow?.IsVisible == true) _ = _newsWindow.RefreshAsync();
	}
	public void PinNewsTile() => _startScreen?.AddLiveTile(LiveKind.News);
	public void OpenNews()
	{
		if (_newsWindow == null)
		{
			_newsWindow = new NewsWindow();
			_newsWindow.Closed += delegate { _newsWindow = null; };
		}
		_newsWindow.Show();
		_newsWindow.Activate();
	}

	public void RefreshGoogleTiles()
	{
		try { _startScreen?.RefreshGoogleTiles(); } catch { }
	}

	public void ClearGoogleTiles()
	{
		try { _startScreen?.ClearGoogleTiles(); } catch { }
	}

	private void GoogleConnectionChanged(object? sender, EventArgs e)
	{
		if (Dispatcher.HasShutdownStarted) return;
		Dispatcher.BeginInvoke(new Action(() =>
		{
			ClearGoogleTiles();
		}));
	}

	internal bool TryShowStartPersonalize()
	{
		if (_startScreen == null)
		{
			return false;
		}

		Dispatcher.BeginInvoke(new Action(_startScreen.ShowPersonalizeStandalone), DispatcherPriority.Input);
		return true;
	}

	// ---- supervisor (launched by the logon Task) ----
	// A tiny, UI-less, COM-less process that keeps a HEALTHY shell running, so IT can never wedge. It launches the
	// shell via Process.Start, verifies a fresh advancing wall-clock heartbeat, and RETRIES until healthy Ã¢â‚¬â€ then
	// monitors and relaunches on wedge/crash/freeze. Respects the clean-exit marker (user chose to quit). This is
	// the definitive cure for the boot-wedge: a shell that fails to come up is simply relaunched until one does.
	private void RunSupervisor()
	{
		bool ownsSupervisor = false;
		Mutex supervisorMutex = new Mutex(false, "Local\\Win81Layer.Supervisor.v2");
		try
		{
			try { ownsSupervisor = supervisorMutex.WaitOne(0); }
			catch (AbandonedMutexException) { ownsSupervisor = true; }
			if (!ownsSupervisor)
			{
				Logger.Log("Supervisor duplicate suppressed by Local\\Win81Layer.Supervisor.v2");
				Shutdown();
				return;
			}
		}
		catch (Exception ex)
		{
			Logger.Log("Supervisor mutex failed: " + ex.Message);
			supervisorMutex.Dispose();
			Shutdown(1);
			return;
		}
		System.DateTime supStart = System.DateTime.UtcNow;
		bool gaveUp = false;   // hard cap hit THIS session: stay stood-down (native shell up) until the machine reboots (a new supervisor process resets this)
		try { Logger.Log($"=== Supervisor started (pid {Environment.ProcessId}) ==="); } catch { }
		try { if (File.Exists(CleanExitMarkerPath)) { File.Delete(CleanExitMarkerPath); } } catch { }   // fresh boot Ã¢â€ â€™ run

		while (true)
		{
			try
			{
				// mtime-aware: honor the marker ONLY if written during THIS supervisor session (ignore a stale one
				// left by a previous shutdown whose boot-storm delete failed — else a degraded cold-boot shell is abandoned).
				if (CleanExitInProgress(supStart))
				{
					System.Threading.Thread.Sleep(3000);// user intentionally quit Ã¢â‚¬â€ stand down until reboot
					continue;
				}
				if (gaveUp)
				{
					System.Threading.Thread.Sleep(5000);   // terminal stand-down: native shell up; don't resurrect a known-failing boot
					continue;
				}
				if (SupervisorShellHealthy())
				{
					System.Threading.Thread.Sleep(4000);   // healthy Ã¢â‚¬â€ keep watching
					continue;
				}
				// Not healthy on the first read. Before killing anything, CONFIRM it is really down Ã¢â‚¬â€ a lone stale read
				// can be a transient heartbeat-file race (WriteHeartbeat truncates then writes) or a brief UI freeze.
				// Killing a healthy shell here would show the user a needless restart, which must never happen.
				bool reallyDown = SupervisorShellCount() == 0;
				for (int i = 0; !reallyDown && i < 3; i++)
				{
					System.Threading.Thread.Sleep(1500);
					if (CleanExitInProgress(supStart)) { break; }   // user quit THIS session mid-check (mtime-aware)
					if (SupervisorShellHealthy()) { break; }
					reallyDown = i == 2;
				}
				if (!reallyDown)
				{
					continue;
				}
				// A shell that was declared healthy but then died YOUNG (before proving stable) is an unhealthy boot too.
				// The initial heartbeat can be stamped mid-OnStartup, so a post-heartbeat fatal fault would otherwise be
				// misclassified as healthy and never counted — this makes safe-mode / stand-down still engage on such loops.
				if (_supManagedLaunchUtc != default(System.DateTime) && System.DateTime.UtcNow - _supManagedLaunchUtc < System.TimeSpan.FromSeconds(90.0))
				{
					NoteUnhealthyBoot();
					Logger.Log($"Supervisor: previously-healthy shell died young (unhealthy boot #{UnhealthyBootCount()} in window).");
				}
				_supManagedLaunchUtc = default(System.DateTime);
				int staleShells = SupervisorShellCount();
				if (staleShells > 0)
				{
					SupervisorKillShells();
					System.Threading.Thread.Sleep(1200);
				}
				// Safe-mode escalation: after repeated unhealthy boots, launch with CORE surfaces only; once even safe-mode
				// has used its attempts, stand down to the native shell instead of looping forever on a deterministic fault.
				int unhealthyBoots = UnhealthyBootCount();
				if (unhealthyBoots >= SafeModeThreshold + SafeModeMaxAttempts)
				{
					gaveUp = true;   // terminal for this session: don't let the decaying counter resurrect a known-failing boot
					Logger.Log($"Supervisor: {unhealthyBoots} unhealthy boots in window - safe-mode also failing; standing down to native shell until next reboot.");
					try { ShellSupervisor.RecoverAfterOurExit(); } catch (Exception exRec) { Logger.Log("Supervisor stand-down restore failed: " + exRec.Message); }
					System.Threading.Thread.Sleep(5000);
					continue;
				}
				bool bootSafeMode = unhealthyBoots >= SafeModeThreshold;
				string shellArgs = bootSafeMode ? "--autostart --managed --safe-mode" : "--autostart --managed";
				System.DateTime launchUtc = System.DateTime.UtcNow;
				int launchedPid = 0;
				try
				{
					if (!InteractiveProcessLauncher.TryStartManagedShell(Environment.ProcessPath!, shellArgs, out launchedPid, out string launchError))
					{
						throw new InvalidOperationException(launchError);
					}
					_supManagedPid = launchedPid;
					Logger.Log($"Supervisor: launched interactive-user shell pid={launchedPid} ({shellArgs}); waiting for a healthy heartbeat...");
				}
				catch (Exception ex)
				{
					Logger.Log("Supervisor: launch failed: " + ex.Message);
					System.Threading.Thread.Sleep(5000);
					continue;
				}
				if (SupervisorWaitHealthy(launchUtc, launchedPid, System.TimeSpan.FromSeconds(45.0)))
				{
					_supManagedLaunchUtc = launchUtc;   // track for young-death detection (a heartbeat can be stamped mid-boot)
					Logger.Log("Supervisor: shell is healthy.");
				}
				else
				{
					NoteUnhealthyBoot();
					Logger.Log($"Supervisor: shell pid={launchedPid} did not become healthy in 45s (unhealthy boot #{UnhealthyBootCount()} in window) - force-killing by pid and retrying.");
					SupervisorKillPid(launchedPid);
					SupervisorKillShells();
					System.Threading.Thread.Sleep(3000);
				}
			}
			catch (Exception ex)
			{
				try { Logger.Log("Supervisor loop error: " + ex.Message); } catch { }
				System.Threading.Thread.Sleep(3000);
			}
		}
	}

	// Healthy = wall-clock heartbeat recent (advancing) AND at least one shell process alive. 30s tolerance so a
	// briefly-busy UI (Background-priority heartbeat momentarily starved) is never mistaken for a wedge.
	private static bool SupervisorShellHealthy()
	{
		try
		{
			long beat = ReadHeartbeat();
			if (beat <= 0L) { return false; }
			System.DateTime b;
			try { b = new System.DateTime(beat, System.DateTimeKind.Utc); } catch { return false; }
			double staleMs = (System.DateTime.UtcNow - b).TotalMilliseconds;
			if (staleMs < 0.0 || staleMs > 30000.0) { return false; }
			// Tracked pid alive short-circuits (fast + hang-proof). Only if it is genuinely dead do we fall back to the
			// bounded name count — which also covers a legit --healed self-relaunch that changed the pid out from under us.
			int tracked = _supManagedPid;
			return (tracked > 0 && SupervisorPidAlive(tracked)) || SupervisorShellCount() > 0;
		}
		catch { return false; }
	}

	private static bool SupervisorWaitHealthy(System.DateTime launchUtc, int pid, System.TimeSpan timeout)
	{
		System.DateTime deadline = System.DateTime.UtcNow + timeout;
		while (System.DateTime.UtcNow < deadline)
		{
			try
			{
				long beat = ReadHeartbeat();
				if (beat > 0L)
				{
					System.DateTime b;
					try { b = new System.DateTime(beat, System.DateTimeKind.Utc); } catch { b = System.DateTime.MinValue; }
					if (b >= launchUtc && (System.DateTime.UtcNow - b).TotalMilliseconds < 12000.0)
					{
						return true;   // stamped a fresh beat AFTER we launched Ã¢â€ â€™ it came up
					}
				}
				if (pid > 0 && !SupervisorPidAlive(pid) && SupervisorShellCount() == 0 && System.DateTime.UtcNow > launchUtc.AddSeconds(8.0))
				{
					return false;   // crashed out entirely Ã¢â‚¬â€ retry sooner
				}
			}
			catch { }
			System.Threading.Thread.Sleep(1500);
		}
		return false;
	}

	private static int SupervisorShellCount()
	{
		int n = 0;
		// Bounded: Process.GetProcessesByName can stall while the boot storm churns the process table; if it does not
		// finish quickly we return 0 rather than let the enumeration wedge the whole recovery loop. A false 0 only ever
		// triggers a relaunch, and SupervisorShellHealthy checks the heartbeat first, so a healthy shell is not killed.
		RunBounded(delegate
		{
			int me = Environment.ProcessId;
			int c = 0;
			Process[] all = Process.GetProcessesByName("Win81Layer");
			foreach (Process p in all)
			{
				try { if (p.Id != me) { c++; } } catch { }
				try { p.Dispose(); } catch { }
			}
			n = c;
		}, 4000);
		return n;
	}

	private static void SupervisorKillShells()
	{
		// Kill the exact tracked shell first by pid (hang-proof), so the wedged shell always dies even if the name-based
		// sweep below stalls; then sweep for orphans under a time bound so it can never freeze the loop.
		int tracked = _supManagedPid;
		if (tracked > 0) { SupervisorKillPid(tracked); }
		RunBounded(delegate
		{
			int me = Environment.ProcessId;
			Process[] all = Process.GetProcessesByName("Win81Layer");
			foreach (Process p in all)
			{
				try { if (p.Id != me) { p.Kill(); } } catch { }
				try { p.Dispose(); } catch { }
			}
		}, 5000);
	}

	// Run an action on a background thread and wait at most ms for it. If it does not finish (e.g. a boot-stalled
	// process enumeration) we abandon it and move on — the supervisor loop must never wedge on a single hung call.
	private static void RunBounded(Action a, int ms)
	{
		try
		{
			Thread t = new Thread(delegate () { try { a(); } catch { } })
			{
				IsBackground = true
			};
			t.Start();
			t.Join(ms);
		}
		catch { }
	}

	// Hang-proof liveness for ONE pid: a plain OpenProcess + zero-timeout wait, never enumerates the process table.
	private static bool SupervisorPidAlive(int pid)
	{
		if (pid <= 0) { return false; }
		nint h = OpenProcess(0x00100000u /*SYNCHRONIZE*/, inherit: false, (uint)pid);
		if (h == IntPtr.Zero) { return false; }
		try { return WaitForSingleObject(h, 0u) == 0x00000102u; /*WAIT_TIMEOUT => still running*/ }
		finally { CloseHandle(h); }
	}

	// Hang-proof kill of ONE pid.
	private static void SupervisorKillPid(int pid)
	{
		if (pid <= 0) { return; }
		nint h = OpenProcess(0x0001u /*PROCESS_TERMINATE*/, inherit: false, (uint)pid);
		if (h == IntPtr.Zero) { return; }
		try { TerminateProcess(h, 1u); } catch { } finally { CloseHandle(h); }
	}

	[DllImport("kernel32.dll", SetLastError = true)]
	private static extern nint OpenProcess(uint access, bool inherit, uint pid);

	[DllImport("kernel32.dll", SetLastError = true)]
	private static extern bool TerminateProcess(nint h, uint exitCode);

	[DllImport("kernel32.dll")]
	private static extern uint WaitForSingleObject(nint h, uint ms);

	[DllImport("kernel32.dll", SetLastError = true)]
	private static extern bool CloseHandle(nint h);

	// A clean-exit marker is only honored if written DURING this watchdog session (mtime >= wdStart). A marker left
	// from a PREVIOUS boot's shutdown (a user Restart whose next-boot shell wedged before deleting it) is STALE:
	// ignore + delete it, so it can never make the watchdog stand down and abandon a wedged boot shell.
	private static bool CleanExitInProgress(System.DateTime wdStart)
	{
		try
		{
			if (!File.Exists(CleanExitMarkerPath)) { return false; }
			if (File.GetLastWriteTimeUtc(CleanExitMarkerPath) >= wdStart) { return true; }
			try { File.Delete(CleanExitMarkerPath); } catch { }
			return false;
		}
		catch { return false; }
	}

	private void RunWatchdog(string? pidArg)
	{
		System.DateTime wdStart = System.DateTime.UtcNow;
		try
		{
			if (int.TryParse(pidArg, out var pid) && pid > 0)
			{
				try
				{
					using Process parent = Process.GetProcessById(pid);
					// Poll instead of a plain WaitForExit so we also catch a FROZEN (not just crashed) shell.
						while (!parent.WaitForExit(WatchdogPollMs))
						{
							if (parent.HasExited || CleanExitInProgress(wdStart))
							{
								continue;
							}
							long beatTicks = ReadHeartbeat();
							System.DateTime nowUtc = System.DateTime.UtcNow;
							bool freshBeat = false;
							double staleMs = double.MaxValue;
							if (beatTicks > 0L)
							{
								System.DateTime beat;
								try { beat = new System.DateTime(beatTicks, System.DateTimeKind.Utc); }
								catch { beat = System.DateTime.MinValue; }
								freshBeat = beat > wdStart;
								staleMs = (nowUtc - beat).TotalMilliseconds;
							}
							if (freshBeat && staleMs < (double)HeartbeatHangThresholdMs)
							{
								continue;
							}
							bool osConfirmsHung = IsShellWindowHung(parent);
							if (!freshBeat)
							{
								if (!osConfirmsHung && (nowUtc - wdStart).TotalMilliseconds < (double)StartupGraceMs)
								{
									continue;
								}
							}
							else
							{
								// A fresh beat that went stale while the window still RESPONDS = a busy/starved UI timer,
								// NOT a freeze Ã¢â‚¬â€ never kill it (that was false-killing healthy shells). Recover a
								// fresh-then-stale shell only when the OS confirms the window is actually hung.
								if (!osConfirmsHung)
								{
									continue;
								}
							}
							Logger.Log($"Watchdog: recovering shell (osHung={osConfirmsHung}, freshBeat={freshBeat}, staleMs={(long)staleMs}, wdAgeMs={(long)(nowUtc - wdStart).TotalMilliseconds}) - kill + relaunch");
							try { parent.Kill(); } catch { }
							try { parent.WaitForExit(5000); } catch { }
							break;
							}
					}
					catch
				{
				}
			}
			ShellSupervisor.RecoverAfterOurExit();
			try
			{
				if (!CleanExitInProgress(wdStart) && RelaunchAllowed())
				{
					NoteRelaunch();
					Process.Start(new ProcessStartInfo(Environment.ProcessPath, "--autostart --healed")
					{
						UseShellExecute = false,
						CreateNoWindow = true
					});
					Logger.Log("Watchdog: shell exited unexpectedly - relaunched (--autostart --healed)");
				}
				else if (CleanExitInProgress(wdStart))
				{
					Logger.Log("Watchdog: clean exit - not relaunching");
				}
				else
				{
					Logger.Log("Watchdog: relaunch anti-loop tripped (>=3 in 5min) - standing down, native shell left in place");
				}
			}
			catch (Exception ex)
			{
				Logger.Log("Watchdog relaunch failed: " + ex.Message);
			}
		}
		catch
		{
		}
		finally
		{
			Shutdown();
		}
	}

	private static bool RelaunchAllowed()
	{
		try
		{
			if (!File.Exists(RelaunchStampPath))
			{
				return true;
			}
			DateTime cutoff = DateTime.Now.AddMinutes(-5.0);
			int recent = 0;
			string[] array = File.ReadAllLines(RelaunchStampPath);
			foreach (string line in array)
			{
				if (DateTime.TryParse(line, out var t) && t > cutoff)
				{
					recent++;
				}
			}
			return recent < 3;
		}
		catch
		{
			return true;
		}
	}

	private static void NoteRelaunch()
	{
		try
		{
			DateTime cutoff = DateTime.Now.AddMinutes(-5.0);
			List<string> kept = new List<string>();
			if (File.Exists(RelaunchStampPath))
			{
				string[] array = File.ReadAllLines(RelaunchStampPath);
				foreach (string line in array)
				{
					if (DateTime.TryParse(line, out var t) && t > cutoff)
					{
						kept.Add(line);
					}
				}
			}
			kept.Add(DateTime.Now.ToString("o"));
			Directory.CreateDirectory(Path.GetDirectoryName(RelaunchStampPath));
			File.WriteAllLines(RelaunchStampPath, kept);
		}
		catch
		{
		}
	}

	// Count of unhealthy boots within the rolling 15-min window. Same shape as RelaunchAllowed.
	private static int UnhealthyBootCount()
	{
		try
		{
			if (!File.Exists(UnhealthyBootStampPath))
			{
				return 0;
			}
			DateTime cutoff = DateTime.Now.AddMinutes(0.0 - UnhealthyBootWindowMinutes);
			int recent = 0;
			string[] array = File.ReadAllLines(UnhealthyBootStampPath);
			foreach (string line in array)
			{
				if (DateTime.TryParse(line, out var t) && t > cutoff)
				{
					recent++;
				}
			}
			return recent;
		}
		catch
		{
			return 0;
		}
	}

	private static void NoteUnhealthyBoot()
	{
		try
		{
			DateTime cutoff = DateTime.Now.AddMinutes(0.0 - UnhealthyBootWindowMinutes);
			List<string> kept = new List<string>();
			if (File.Exists(UnhealthyBootStampPath))
			{
				string[] array = File.ReadAllLines(UnhealthyBootStampPath);
				foreach (string line in array)
				{
					if (DateTime.TryParse(line, out var t) && t > cutoff)
					{
						kept.Add(line);
					}
				}
			}
			kept.Add(DateTime.Now.ToString("o"));
			Directory.CreateDirectory(Path.GetDirectoryName(UnhealthyBootStampPath));
			File.WriteAllLines(UnhealthyBootStampPath, kept);
		}
		catch
		{
		}
	}

	// Cleared after ~90s of sustained health so a one-off bad boot can never keep the shell stuck in safe mode.
	internal static void ClearUnhealthyBoots()
	{
		try
		{
			if (File.Exists(UnhealthyBootStampPath))
			{
				File.Delete(UnhealthyBootStampPath);
			}
		}
		catch
		{
		}
	}

	protected override void OnStartup(StartupEventArgs e)
	{
		//IL_1adb: Unknown result type (might be due to invalid IL or missing references)
		//IL_1ae0: Unknown result type (might be due to invalid IL or missing references)
		//IL_1af7: Expected O, but got Unknown
		//IL_1517: Unknown result type (might be due to invalid IL or missing references)
		//IL_151c: Unknown result type (might be due to invalid IL or missing references)
		//IL_1522: Expected O, but got Unknown
		//IL_2e99: Unknown result type (might be due to invalid IL or missing references)
		//IL_2e9e: Unknown result type (might be due to invalid IL or missing references)
		//IL_2eb5: Expected O, but got Unknown
		//IL_2eda: Unknown result type (might be due to invalid IL or missing references)
		//IL_2ee4: Expected O, but got Unknown
		//IL_2ef2: Unknown result type (might be due to invalid IL or missing references)
		//IL_2efc: Expected O, but got Unknown
		//IL_2f0a: Unknown result type (might be due to invalid IL or missing references)
		//IL_2f14: Expected O, but got Unknown
		//IL_2f22: Unknown result type (might be due to invalid IL or missing references)
		//IL_2f2c: Expected O, but got Unknown
		//IL_2fed: Unknown result type (might be due to invalid IL or missing references)
		//IL_2ff2: Unknown result type (might be due to invalid IL or missing references)
		//IL_3009: Expected O, but got Unknown
		base.OnStartup(e);
		// Single light/dark authority: arm the OS preference hook and paint the semantic brush tokens before any
		// window is shown, so the whole shell (menus, controls, tooltips, dialogs) opens in the correct theme.
		try { ShellTheme.Initialize(); ShellTheme.ApplyResourceTokens(); } catch (Exception themeEx) { Logger.Log("ShellTheme init failed: " + themeEx.Message); }
		if (e.Args.Contains("--metro-integration-test"))
		{
			SettingsStore.ReadOnlyDiagnostics = true;
			MetroIntegrationProbe.Begin(this);
			return;
		}
		if (e.Args.Contains("--place-search-test"))
		{
			SettingsStore.ReadOnlyDiagnostics = true;
			MetroPlaceSearchProbe.Begin(this);
			return;
		}
		if (e.Args.Contains("--launch-probe"))
		{
			Shutdown();
			return;
		}
		if (e.Args.Contains("--taskbar-lifecycle-probe"))
		{
			ShutdownMode = ShutdownMode.OnMainWindowClose;
			Window probe = new Window
			{
				Title = "Win81Layer Taskbar Lifecycle Probe",
				WindowStyle = WindowStyle.SingleBorderWindow,
				ResizeMode = ResizeMode.CanResize,
				ShowInTaskbar = true,
				Width = 640.0,
				Height = 420.0,
				WindowStartupLocation = WindowStartupLocation.CenterScreen,
				Background = System.Windows.Media.Brushes.White
			};
			MainWindow = probe;
			probe.Show();
			probe.Activate();
			return;
		}
		if (e.Args.Contains("--fullscreen-probe"))
		{
			// Separate-process, dependency-free fullscreen fixture used by TaskbarRuntimeDiagnostics.
			// The parent positions this HWND in physical pixels and closes it after the round trip.
			ShutdownMode = ShutdownMode.OnMainWindowClose;
			Window probe = new Window
			{
				Title = "Win81Layer Fullscreen QA Probe",
				WindowStyle = WindowStyle.None,
				ResizeMode = ResizeMode.NoResize,
				ShowInTaskbar = true,
				Topmost = true,
				Width = 320.0,
				Height = 200.0,
				Background = System.Windows.Media.Brushes.Black
			};
			MainWindow = probe;
			probe.Show();
			probe.Activate();
			return;
		}
		if (e.Args.Contains("--explorer81test") || e.Args.Contains("--contextmenutest") || e.Args.Contains("--starttransitiontest") || e.Args.Contains("--taskbar-runtime-test") || e.Args.Contains("--taskbar-lifecycle-test") || e.Args.Contains("--uxpaneltest") || e.Args.Contains("--compositionqueuetest") || e.Args.Contains("--launchperformancetest") || e.Args.Contains("--switcher-edge-test") || e.Args.Contains("--clock-calendar-test") || e.Args.Contains("--sound-flyout-test") || e.Args.Contains("--network-flyout-test") || e.Args.Contains("--action-center-test") || e.Args.Contains("--directional-arrow-test") || e.Args.Contains("--weathertiletest") || e.Args.Contains("--metrotiletest") || e.Args.Contains("--flagtest"))
		{
			SettingsStore.ReadOnlyDiagnostics = true;
		}
		if (e.Args.Contains("--switcher-edge-test"))
		{
			SwitcherEdgeDiagnostics.Begin(this);
			return;
		}
		// The logon Task launches "--supervisor": a tiny, UI-less, COM-less process that keeps a HEALTHY shell running
		// (launch via Process.Start Ã¢â€ â€™ verify a fresh heartbeat Ã¢â€ â€™ retry until healthy Ã¢â€ â€™ monitor). This is the definitive
		// cure for the boot-wedge: a shell that fails to come up is simply relaunched until one does.
		if (e.Args.Contains("--supervisor"))
		{
			RunSupervisor();
			return;
		}
		// A shell started BY the supervisor is "managed" Ã¢â‚¬â€ it must NOT spawn its own watchdog (the supervisor is the
		// single monitor); double-monitoring would fight over kill/relaunch.
		_managed = e.Args.Contains("--managed");
		_safeMode = e.Args.Contains("--safe-mode");
		SafeMode = _safeMode;
		if (_safeMode) { Logger.Log("[safe-mode] booting with CORE surfaces only (taskbar + Start); composition/charms/tray-host/live-tiles/snap disabled."); }
		if (_managed)
		{
			try { Process.GetCurrentProcess().PriorityClass = ProcessPriorityClass.AboveNormal; } catch { }
		}
		int wdIdx = Array.IndexOf(e.Args, "--watchdog");
		if (wdIdx >= 0)
		{
			RunWatchdog((wdIdx + 1 < e.Args.Length) ? e.Args[wdIdx + 1] : null);
			return;
		}
		if (e.Args.Contains("--revert-icons"))
		{
			try
			{
				SystemIcons81.Restore();
				Logger.Log("[qa] --revert-icons done");
			}
			catch (Exception value)
			{
				Logger.Log($"[qa] --revert-icons failed: {value}");
			}
			Shutdown();
			return;
		}
		Logger.Log("=== Startup ===");
		bool experimentAudit = e.Args.Contains("--experiments-audit");
		bool persistenceSelfTest = e.Args.Contains("--persistence-selftest");
		bool persistenceMigrate = e.Args.Contains("--persistence-migrate");
		bool persistenceVerify = e.Args.Contains("--persistence-verify");
		bool persistenceExpect = e.Args.Contains("--persistence-expect");
		bool ensureLogonTask = e.Args.Contains("--ensure-logon-task");
		bool appLaunchTest = e.Args.Contains("--app-launch-test");
		bool restoreNativeShell = e.Args.Contains("--restore-native-shell");
		bool shellProfileSelfTest = e.Args.Contains("--shell-profile-selftest");
		bool shellProfileStatus = e.Args.Contains("--shell-profile-status");
		bool shellProfileApply = e.Args.Contains("--shell-profile-apply");
		bool shellProfileRestorePrevious = e.Args.Contains("--shell-profile-restore-previous");
		bool pcSettingsTest = e.Args.Contains("--pcsettingstest");
		bool iconTest = e.Args.Contains("--icontest");
		bool trayDump = e.Args.Contains("--traydump");
		bool uxPanelTest = e.Args.Contains("--uxpaneltest");
		bool compositionQueueTest = e.Args.Contains("--compositionqueuetest");
		bool dwmBlurGlassPolicyTest = e.Args.Contains("--dwmblurglasstest");
		bool dwmBlurGlassDetach = e.Args.Contains("--dwmblurglassdetach");
		bool explorer81Test = e.Args.Contains("--explorer81test");
		bool contextMenuTest = e.Args.Contains("--contextmenutest");
		bool startTransitionTest = e.Args.Contains("--starttransitiontest");
		bool taskbarLayoutSelfTest = e.Args.Contains("--taskbar-layout-selftest");
		bool taskbarRuntimeTest = e.Args.Contains("--taskbar-runtime-test");
		bool launchPerformanceTest = e.Args.Contains("--launchperformancetest");
		bool clockCalendarTest = e.Args.Contains("--clock-calendar-test");
		bool soundFlyoutTest = e.Args.Contains("--sound-flyout-test");
		bool networkFlyoutTest = e.Args.Contains("--network-flyout-test");
		bool actionCenterTest = e.Args.Contains("--action-center-test");
		bool directionalArrowTest = e.Args.Contains("--directional-arrow-test");
		bool trayIconTest = e.Args.Contains("--trayicontest");
		bool weatherTileTest = e.Args.Contains("--weathertiletest") || e.Args.Contains("--metrotiletest");
		bool pinDropTest = e.Args.Contains("--pindroptest");
		bool persistenceCli = persistenceSelfTest || persistenceMigrate || persistenceVerify || persistenceExpect || ensureLogonTask || appLaunchTest || restoreNativeShell || shellProfileSelfTest || shellProfileStatus || shellProfileApply || shellProfileRestorePrevious || taskbarLayoutSelfTest;
		// Safety net: unconditionally resume any native host a PRIOR unclean exit may have left suspended (no-op if none).
		// Skip for mutex-free diagnostic CLIs: they must not resume hosts intentionally suspended by the live shell.
		// The isolated taskbar runtime test replaces the live shell, so it must recover stale suspension first.
		if (!e.Args.Contains("--dwmdiag") && !e.Args.Contains("--dwmperf") && !e.Args.Contains("--flagtest") && !experimentAudit && !persistenceCli && !pcSettingsTest && !iconTest && !trayDump && !uxPanelTest && !compositionQueueTest && !dwmBlurGlassPolicyTest && !dwmBlurGlassDetach && !explorer81Test && !contextMenuTest && !startTransitionTest && !launchPerformanceTest && !clockCalendarTest && !soundFlyoutTest && !networkFlyoutTest && !actionCenterTest && !directionalArrowTest && !trayIconTest && !weatherTileTest && !pinDropTest)
		{
			try { NativeShell.ResumeAll(); } catch { }
		}
		if (pinDropTest)
		{
			try
			{
				int pdi = Array.IndexOf(e.Args, "--pindroptest");
				string arg = ((pdi >= 0 && pdi + 1 < e.Args.Length) ? e.Args[pdi + 1] : "");
				PinnedApp resolved = ShellDropResolver.ResolveParsingName(arg);
				List<PinnedApp> current = TaskbarPins.Load();
				// Must mirror TaskbarWindow.SamePinIdentity exactly: exe + arguments + AUMID. Comparing the exe alone
				// would report a false "already pinned" for shortcuts that share an exe but differ in arguments
				// (e.g. Metro Browser vs plain Chrome) — the very bug this hook exists to catch.
				bool already = resolved != null && current.Any((PinnedApp p) =>
					string.Equals(p.ExePath ?? p.LaunchPath, resolved.ExePath ?? resolved.LaunchPath, StringComparison.OrdinalIgnoreCase)
					&& string.Equals(p.Args ?? "", resolved.Args ?? "", StringComparison.OrdinalIgnoreCase)
					&& string.Equals(p.Aumid ?? "", resolved.Aumid ?? "", StringComparison.OrdinalIgnoreCase));
				string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Win81Layer", "qa");
				Directory.CreateDirectory(dir);
				string json = System.Text.Json.JsonSerializer.Serialize(new
				{
					arg,
					resolved = (resolved == null) ? null : new { resolved.Name, resolved.LaunchPath, resolved.ExePath, resolved.Aumid, resolved.Args },
					wouldPin = (resolved != null && !already),
					alreadyPinned = already
				}, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
				File.WriteAllText(Path.Combine(dir, "pindroptest.json"), json);
				Logger.Log("[pindroptest] " + json);
			}
			catch (Exception ex) { Logger.Log("[pindroptest] failed: " + ex); }
			Shutdown();
			return;
		}
		if (explorer81Test)
		{
			Explorer81Diagnostics.Begin(this);
			return;
		}
		if (contextMenuTest)
		{
			ContextMenuDiagnostics.Begin(this);
			return;
		}
		if (startTransitionTest)
		{
			StartTransitionDiagnostics.Begin(this);
			return;
		}
		if (launchPerformanceTest)
		{
			LaunchPerformanceDiagnostics.Begin(this);
			return;
		}
		if (clockCalendarTest)
		{
			ClockCalendarDiagnostics.Begin(this);
			return;
		}
		if (soundFlyoutTest)
		{
			SoundFlyoutDiagnostics.Begin(this);
			return;
		}
		if (networkFlyoutTest)
		{
			NetworkFlyoutDiagnostics.Begin(this);
			return;
		}
		if (actionCenterTest)
		{
			ActionCenterDiagnostics.Begin(this);
			return;
		}
		if (directionalArrowTest)
		{
			DirectionalArrow81Diagnostics.Begin(this);
			return;
		}
		if (trayIconTest)
		{
			TrayIconProbe.Begin(this);
			return;
		}
		if (weatherTileTest)
		{
			if (e.Args.Contains("--metrotiletest")) MetroTileProbe.Begin(this);
			else WeatherTileProbe.Begin(this);
			return;
		}
		if (experimentAudit)
		{
			try { ExperimentRegistry.WriteAuditReport(); }
			catch (Exception ex) { Logger.Log("[experiments-audit] failed: " + ex); }
			Shutdown();
			return;
		}
		if (persistenceCli)
		{
			try
			{
				if (taskbarLayoutSelfTest)
				{
					TaskbarDiagnostics.RunLayoutSelfTest();
				}
				else if (shellProfileSelfTest)
				{
					ShellProfileDiagnostics.RunSelfTest();
				}
				else if (shellProfileStatus)
				{
					ShellProfileDiagnostics.WriteStatus();
				}
				else if (shellProfileApply)
				{
					int profileIndex = Array.IndexOf(e.Args, "--shell-profile-apply");
					if (profileIndex < 0 || profileIndex + 1 >= e.Args.Length)
					{
						throw new ArgumentException("--shell-profile-apply requires windows81, windows7, or native.");
					}
					ShellProfileApplyResult applied = ShellProfileManager.Apply(e.Args[profileIndex + 1]);
					if (!applied.Success)
					{
						throw new InvalidOperationException(applied.Message);
					}
					ShellProfileDiagnostics.WriteStatus();
				}
				else if (shellProfileRestorePrevious)
				{
					ShellProfileApplyResult restored = ShellProfileManager.RestorePrevious();
					if (!restored.Success)
					{
						throw new InvalidOperationException(restored.Message);
					}
					ShellProfileDiagnostics.WriteStatus();
				}
				else if (persistenceSelfTest)
				{
					PersistenceDiagnostics.RunSelfTest();
				}
				else if (restoreNativeShell)
				{
					ShellProfileApplyResult recovery = ShellProfileManager.Apply(ShellProfileIds.Native);
					if (!recovery.Success)
					{
						throw new InvalidOperationException(recovery.Message);
					}
					NativeShell.ResumeAll();
					AppBar.ShowNativeTaskbar();
					Logger.Log("Native shell profile committed and explicitly restored by maintenance command.");
				}
				else if (appLaunchTest)
				{
					int testIndex = Array.IndexOf(e.Args, "--app-launch-test");
					if (testIndex < 0 || testIndex + 1 >= e.Args.Length)
					{
						throw new ArgumentException("--app-launch-test requires an AUMID argument.");
					}
					PersistenceDiagnostics.RunPackagedAppLaunchTest(e.Args[testIndex + 1]);
				}
				else if (ensureLogonTask)
				{
					if (!LogonTask.SetEnabled(true) || !LogonTask.IsCanonical())
					{
						throw new InvalidOperationException("Scheduled Task could not be updated to the canonical launcher definition.");
					}
					PersistenceDiagnostics.WriteVerification("scheduled-task-update");
				}
				else if (persistenceMigrate)
				{
					string profileSource = null;
					int sourceIndex = Array.IndexOf(e.Args, "--profile-source");
					if (sourceIndex >= 0 && sourceIndex + 1 < e.Args.Length)
					{
						profileSource = e.Args[sourceIndex + 1];
					}
					PersistenceDiagnostics.MigrateActiveState(profileSource);
				}
				else if (persistenceExpect)
				{
					PersistenceDiagnostics.WriteRebootExpectation();
				}
				else
				{
					PersistenceDiagnostics.WriteVerification();
				}
			}
			catch (Exception ex)
			{
				Logger.Log("[persistence-cli] failed: " + ex);
				Shutdown(1);
				return;
			}
			Shutdown();
			return;
		}
		ExperimentRegistry.LogCompatibilitySummary();
		if (e.Args.Contains("--dwmdiag"))
		{
			// Phase-0 DWM diagnostics: one baseline (capabilities + system composition timing delta + live-shell process
			// counters), out-of-process, then exit. Runs BEFORE the single-instance mutex so it never disturbs the shell.
			try { string dp = DwmDiagnostics.Baseline(); Logger.Log("[dwmdiag] baseline: " + dp); }
			catch (Exception dex) { Logger.Log("[dwmdiag] failed: " + dex); }
			Shutdown();
			return;
		}
		if (e.Args.Contains("--dwmperf"))
		{
			// Phase-3 frame-timing VALIDATION. Unlike --dwmdiag (windowless → DWM 0x88980090), this creates a REAL
			// composed WPF window so THIS process owns a live composition session, then runs the bounded burst on a
			// background thread (the UI thread keeps pumping frames so the window keeps compositing) and exits. Proves
			// the in-shell tray / PC-Settings capture path returns real frame numbers. Second, mutex-free instance.
			try
			{
				Window probe = new Window
				{
					Width = 240.0,
					Height = 160.0,
					Left = -2400.0,
					Top = -2400.0,
					WindowStyle = WindowStyle.None,
					ShowInTaskbar = false,
					Topmost = false,
					Background = System.Windows.Media.Brushes.CornflowerBlue,
					Title = "dwmperf"
				};
				probe.Loaded += delegate
				{
					nint hh = new System.Windows.Interop.WindowInteropHelper(probe).EnsureHandle();
					System.Threading.Tasks.Task.Run(delegate
					{
						try { string pp = DwmDiagnostics.FrameTimingBurst(hh, 180, 16); Logger.Log("[dwmperf] report: " + pp); }
						catch (Exception pex) { Logger.Log("[dwmperf] burst failed: " + pex); }
						try { ((DispatcherObject)this).Dispatcher.BeginInvoke((Delegate)(Action)delegate { Shutdown(); }, Array.Empty<object>()); } catch { }
					});
				};
				probe.Show();
				return;   // keep the WPF message loop running; the burst task calls Shutdown when done
			}
			catch (Exception dpx)
			{
				Logger.Log("[dwmperf] setup failed: " + dpx);
				Shutdown();
				return;
			}
		}
		int dumpIdx = Array.IndexOf(e.Args, "--dumpjl");
		if (dumpIdx >= 0 && dumpIdx + 1 < e.Args.Length)
		{
			List<JumpList.Item> items = JumpList.ReadFile(e.Args[dumpIdx + 1], 15);
			Logger.Log($"JL DUMP: {items.Count} items");
			foreach (JumpList.Item it in items)
			{
				Logger.Log("  JL: " + it.Name + "  <=  " + it.Path);
			}
			Shutdown();
			return;
		}
		// External invocation (§8): `Win81Layer.exe --uri "launcher://display/extend"` — routes through the SAME Action
		// Router. Transient process, so UI-bound routes (workspace/search) need the running launcher (their hooks are null
		// here); fire-and-forget routes (display/settings/bluetooth/action) work standalone. Input is validated by Invoke.
		int uriIdx = Array.IndexOf(e.Args, "--uri");
		if (uriIdx >= 0 && uriIdx + 1 < e.Args.Length)
		{
			string uri = e.Args[uriIdx + 1];
			bool ok = ActionRouter.Invoke(uri);
			Logger.Log($"URI '{uri}': {(ok ? "handled" : "rejected/unsupported")}");
			((DispatcherObject)this).Dispatcher.BeginInvoke((DispatcherPriority)4, (Delegate)(Action)delegate { Shutdown(); });
			return;
		}
		int expIdx = Array.IndexOf(e.Args, "--export-profile");
		if (expIdx >= 0 && expIdx + 1 < e.Args.Length)
		{
			bool ok = LauncherProfile.Export(e.Args[expIdx + 1], out string? err);
			Logger.Log($"EXPORT-PROFILE '{e.Args[expIdx + 1]}': {(ok ? "ok" : "failed: " + err)}");
			Shutdown();
			return;
		}
		int impIdx = Array.IndexOf(e.Args, "--import-profile");
		if (impIdx >= 0 && impIdx + 1 < e.Args.Length)
		{
			bool ok = LauncherProfile.Import(e.Args[impIdx + 1], out string? err);
			Logger.Log($"IMPORT-PROFILE '{e.Args[impIdx + 1]}': {(ok ? "ok (applies on next launch)" : "rejected: " + err)}");
			Shutdown();
			return;
		}
		int stIdx = Array.IndexOf(e.Args, "--searchtest");
		if (stIdx >= 0 && stIdx + 1 < e.Args.Length)
		{
			string q = e.Args[stIdx + 1];
			List<string> apps = (from tuple3 in AppInventory.EnumerateAppsFolder()
				select tuple3.Entry into a
				where SearchExtras.AppMatches(a.Name, q)
				select a.Name).Take(8).ToList();
			List<string> settingResults = (from setting in SearchExtras.MatchingSettings(q)
				select setting.Name).ToList();
			Logger.Log($"SEARCH '{q}': settings=[{string.Join(", ", settingResults)}] apps=[{string.Join(", ", apps)}]");
			Shutdown();
			return;
		}
		if (e.Args.Contains("--jltest"))
		{
			int withItems = 0;
			foreach (var item3 in AppInventory.EnumerateAppsFolder())
			{
				AppEntry entry = item3.Entry;
				List<JumpList.Item> items2 = JumpListApi.GetRecent(entry.AppId, 5);
				if (items2.Count > 0)
				{
					withItems++;
					Logger.Log($"JLAPI [{entry.Name}] appid={entry.AppId} {items2.Count}: {string.Join(" | ", from i in items2.Take(4)
						select i.Name)}");
				}
			}
			Logger.Log($"JLAPI: {withItems} apps with recent items");
			Shutdown();
			return;
		}
		if (e.Args.Contains("--flagtest"))
		{
			TaskbarWindow tb = new TaskbarWindow
			{
				Left = -4000.0,
				Top = -4000.0,
				Width = 220.0,
				Height = TaskbarMetrics.BarHeight
			};
			tb.Show();
			((DispatcherObject)this).Dispatcher.BeginInvoke((DispatcherPriority)6, (Delegate)(Action)delegate
			{
				//IL_005c: Unknown result type (might be due to invalid IL or missing references)
				//IL_0061: Unknown result type (might be due to invalid IL or missing references)
				//IL_0083: Unknown result type (might be due to invalid IL or missing references)
				//IL_0088: Unknown result type (might be due to invalid IL or missing references)
				//IL_009c: Unknown result type (might be due to invalid IL or missing references)
				//IL_00a1: Unknown result type (might be due to invalid IL or missing references)
				try
				{
					if (tb.FindName("StartButton") is System.Windows.Controls.Button startButton)
					{
						FrameworkElement frameworkElement = startButton;
						tb.PrepareStartButtonForDiagnostics("idle");
						frameworkElement.UpdateLayout();
						PresentationSource presentationSource = PresentationSource.FromVisual(tb);
						double? num3;
						if (presentationSource == null)
						{
							num3 = null;
						}
						else
						{
							CompositionTarget compositionTarget = presentationSource.CompositionTarget;
							if (compositionTarget == null)
							{
								num3 = null;
							}
							else
							{
								System.Windows.Media.Matrix transformToDevice = compositionTarget.TransformToDevice;
								num3 = transformToDevice.M11;
							}
						}
						double num4 = num3 ?? 1.0;
						System.Windows.Size renderSize = frameworkElement.RenderSize;
						int num5 = (int)Math.Ceiling(renderSize.Width * num4);
						renderSize = frameworkElement.RenderSize;
						int num6 = (int)Math.Ceiling(renderSize.Height * num4);
						string dataDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Win81Layer");
						(System.Windows.Media.Color? Surface, System.Windows.Media.Color? Frame, System.Windows.Media.Color? Glyph, bool OrbVisible, string Screenshot) RenderState(string state, string fileName)
						{
							tb.PrepareStartButtonForDiagnostics(state);
							frameworkElement.UpdateLayout();
							RenderTargetBitmap bitmap = new RenderTargetBitmap(Math.Max(num5, 1), Math.Max(num6, 1), 96.0 * num4, 96.0 * num4, PixelFormats.Pbgra32);
							bitmap.Render(frameworkElement);
							PngBitmapEncoder encoder = new PngBitmapEncoder
							{
								Frames = { BitmapFrame.Create(bitmap) }
							};
							string path = Path.Combine(dataDirectory, fileName);
							using (FileStream stream = File.Create(path))
							{
								encoder.Save(stream);
							}
							System.Windows.Controls.Border stateBorder = startButton.Template.FindName("Bd", startButton) as System.Windows.Controls.Border;
							System.Windows.Shapes.Rectangle stateGlyph = startButton.Template.FindName("StartGlyph", startButton) as System.Windows.Shapes.Rectangle;
							System.Windows.Shapes.Ellipse stateOrb = startButton.Template.FindName("StartOrb", startButton) as System.Windows.Shapes.Ellipse;
							return (
								(stateBorder?.Background as SolidColorBrush)?.Color,
								(stateBorder?.BorderBrush as SolidColorBrush)?.Color,
								(stateGlyph?.Fill as SolidColorBrush)?.Color,
								stateOrb?.Visibility == Visibility.Visible,
								path);
						}

						var idle = RenderState("idle", "flagtest.png");
						var pressed = RenderState("pressed", "flagtest-pressed.png");
						var open = RenderState("open", "flagtest-open.png");
						string mode = DesktopComposition.EffectiveMode;
						bool metroFrame = TaskbarWindow.UsesMetroStartFrame(mode);
						System.Windows.Media.Color expectedAccent = StartAccent.Color();
						System.Windows.Media.Color expectedPressedGlyph = TaskbarWindow.StartGlyphTone(mode, expectedAccent, false, false, true);
						System.Windows.Media.Color expectedOpenGlyph = TaskbarWindow.StartGlyphTone(mode, expectedAccent, true, false, false);
						bool visualContract = metroFrame
							? idle.Surface?.A == 0 && idle.Frame?.A == 0 && idle.Glyph == Colors.White && !idle.OrbVisible
								&& pressed.Surface == Colors.Black && pressed.Frame == Colors.Black && pressed.Glyph == expectedPressedGlyph && !pressed.OrbVisible
								&& open.Surface == Colors.Black && open.Frame == Colors.Black && open.Glyph == expectedOpenGlyph && !open.OrbVisible
							: idle.Surface?.A == 0 && pressed.Surface?.A == 0 && open.Surface?.A == 0
								&& idle.Glyph == Colors.White && idle.OrbVisible && pressed.OrbVisible && open.OrbVisible;
						object report = new
						{
							SchemaVersion = 2,
							GeneratedUtc = DateTime.UtcNow,
							Passed = visualContract,
							CompositionMode = mode,
							MetroPressFrame = metroFrame,
							Idle = new { SurfaceColor = idle.Surface?.ToString(), FrameColor = idle.Frame?.ToString(), GlyphColor = idle.Glyph?.ToString(), idle.OrbVisible, idle.Screenshot },
							Pressed = new { SurfaceColor = pressed.Surface?.ToString(), FrameColor = pressed.Frame?.ToString(), GlyphColor = pressed.Glyph?.ToString(), pressed.OrbVisible, pressed.Screenshot },
							Open = new { SurfaceColor = open.Surface?.ToString(), FrameColor = open.Frame?.ToString(), GlyphColor = open.Glyph?.ToString(), open.OrbVisible, open.Screenshot },
							ExpectedStartThemeAccent = expectedAccent.ToString(),
							ExpectedPressedGlyph = expectedPressedGlyph.ToString(),
							ExpectedOpenGlyph = expectedOpenGlyph.ToString(),
							WidthPx = num5,
							HeightPx = num6,
							StartGlyphSizeDiu = TaskbarMetrics.StartGlyphSize,
							IdleScreenshot = idle.Screenshot,
							PressedScreenshot = pressed.Screenshot,
							OpenScreenshot = open.Screenshot
						};
						string qaDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Win81Layer", "qa");
						Directory.CreateDirectory(qaDirectory);
						File.WriteAllText(
							Path.Combine(qaDirectory, "start-button-render-latest.json"),
							System.Text.Json.JsonSerializer.Serialize(report, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
						Logger.Log($"FLAGTEST: rendered idle/pressed/open Start button {num5}x{num6} @scale {num4} -> {dataDirectory}");
					}
				}
				catch (Exception value2)
				{
					Logger.Log($"FLAGTEST failed: {value2}");
				}
				finally
				{
					Shutdown();
				}
			});
			return;
		}
		if (e.Args.Contains("--importpins"))
		{
			List<PinnedApp> pins = TaskbarPins.ImportFromNative();
			if (pins.Count > 0)
			{
				TaskbarPins.Save(pins);
			}
			Shutdown();
			return;
		}
		if (e.Args.Contains("--audiotest"))
		{
			(string, bool)[] array = new(string, bool)[2]
			{
				("OUTPUT", false),
				("INPUT", true)
			};
			for (int num = 0; num < array.Length; num++)
			{
				(string, bool) tuple = array[num];
				string label = tuple.Item1;
				bool cap = tuple.Item2;
				List<AudioDevice> devs = AudioDevices.Enumerate(cap);
				Logger.Log($"AUDIO {label}: {devs.Count} devices");
				foreach (AudioDevice d in devs)
				{
					Logger.Log($"  {(d.IsDefault ? "*" : " ")} {d.Name}  [{d.Id}]");
				}
			}
			Shutdown();
			return;
		}
		if (e.Args.Contains("--grouptest"))
		{
			TaskbarWindow tb2 = new TaskbarWindow
			{
				Left = -4000.0,
				Top = -4000.0,
				Width = 300.0,
				Height = 40.0
			};
			tb2.Show();
			((DispatcherObject)this).Dispatcher.BeginInvoke((DispatcherPriority)6, (Delegate)(Action)delegate
			{
				try
				{
					string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Win81Layer", "grouptest.png");
					tb2.QaRenderGroupFlyout(path);
				}
				catch (Exception value2)
				{
					Logger.Log($"GROUPTEST failed: {value2}");
				}
				finally
				{
					Shutdown();
				}
			});
			return;
		}
		if (e.Args.Contains("--charmstest"))
		{
			CharmsBar cb = new CharmsBar
			{
				Left = -4000.0,
				Top = -4000.0
			};
			((DispatcherObject)this).Dispatcher.BeginInvoke((DispatcherPriority)6, (Delegate)(Action)delegate
			{
				try
				{
					string outPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Win81Layer", "charmstest.png");
					cb.QaRender(outPath);
				}
				catch (Exception value2)
				{
					Logger.Log($"CHARMSTEST failed: {value2}");
				}
				finally
				{
					Shutdown();
				}
			});
			return;
		}
		if (e.Args.Contains("--searchbench"))
		{
			List<AppEntry> apps3 = AppInventory.LoadFromStartMenuLinks();
			SearchPane bench = new SearchPane(() => apps3, delegate
			{
			})
			{
				Left = -4000.0,
				Top = -4000.0
			};
			((DispatcherObject)this).Dispatcher.BeginInvoke((DispatcherPriority)6, (Delegate)(Action)delegate
			{
				try
				{
					string outPathB = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Win81Layer", "searchbench.txt");
					File.WriteAllText(outPathB, bench.RunBenchmark());
					Logger.Log("SEARCHBENCH -> " + outPathB);
				}
				catch (Exception valueB)
				{
					Logger.Log($"SEARCHBENCH failed: {valueB}");
				}
				finally
				{
					Shutdown();
				}
			});
			return;
		}
		if (e.Args.Contains("--searchpanetest"))
		{
			int qi = Array.IndexOf(e.Args, "--searchpanetest");
			string q2 = ((qi + 1 < e.Args.Length) ? e.Args[qi + 1] : "ca");
			List<AppEntry> apps2 = AppInventory.LoadFromStartMenuLinks();
			SearchPane pane = new SearchPane(() => apps2, delegate
			{
			})
			{
				Left = -4000.0,
				Top = -4000.0
			};
			((DispatcherObject)this).Dispatcher.BeginInvoke((DispatcherPriority)6, (Delegate)(Action)delegate
			{
				try
				{
					string outPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Win81Layer", "searchpanetest.png");
					pane.QaRender(outPath, q2);
				}
				catch (Exception value2)
				{
					Logger.Log($"SEARCHPANETEST failed: {value2}");
				}
				finally
				{
					Shutdown();
				}
			});
			return;
		}
		if (e.Args.Contains("--pcsettingstest"))
		{
			StartScreen st = new StartScreen();
			PcSettingsWindow pc = new PcSettingsWindow(st)
			{
				Left = -4000.0,
				Top = -4000.0
			};
			((DispatcherObject)this).Dispatcher.BeginInvoke((DispatcherPriority)6, (Delegate)(Action)delegate
			{
				try
				{
					string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Win81Layer");
					pc.QaRender(Path.Combine(path, "pcsettingstest.png"));
					pc.QaRender(Path.Combine(path, "pcsettings_launcher.png"), "Launcher");
					pc.QaRenderTall(Path.Combine(path, "launcher_tall.png"), "Launcher");
					pc.QaRender(Path.Combine(path, "pcsettings_eoa.png"), "Ease of Access");
					pc.QaRender(Path.Combine(path, "pcsettings_net.png"), "Network");
					pc.QaRender(Path.Combine(path, "pcsettings_time.png"), "Time and language");
					pc.QaRender(Path.Combine(path, "pcsettings_display.png"), "Display");
					pc.QaRender(Path.Combine(path, "pcsettings_composition.png"), "Desktop composition");
					pc.QaRenderTall(Path.Combine(path, "pcsettings_composition_tall.png"), "Desktop composition");
					pc.QaRender(Path.Combine(path, "pcsettings_pcinfo.png"), "PC info");
					pc.QaRender(Path.Combine(path, "pcsettings_power.png"), "Power & sleep");
					pc.QaRender(Path.Combine(path, "pcsettings_lock.png"), "Lock screen");
					pc.QaRender(Path.Combine(path, "pcsettings_accounts.png"), "Accounts");
					pc.QaRender(Path.Combine(path, "pcsettings_searchapps.png"), "Search and apps");
					pc.QaRender(Path.Combine(path, "pcsettings_notif.png"), "Notifications");
					pc.QaRender(Path.Combine(path, "pcsettings_update.png"), "Update and recovery");
					PcSettingsWindow compact = new PcSettingsWindow(st)
					{
						Left = -4000.0,
						Top = -4000.0
					};
					compact.QaRender(Path.Combine(path, "pcsettings_launcher_1024.png"), "Launcher", 1024.0, 768.0);
					compact.Close();
				}
				catch (Exception value2)
				{
					Logger.Log($"PCSETTINGSTEST: {value2}");
				}
				finally
				{
					Shutdown();
				}
			});
			return;
		}
		if (e.Args.Contains("--switchertest"))
		{
			AppSwitcher sw = new AppSwitcher();
			((DispatcherObject)this).Dispatcher.BeginInvoke((DispatcherPriority)6, (Delegate)(Action)delegate
			{
				try
				{
					string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Win81Layer");
					sw.QaRender(Path.Combine(path, "switchertest.png"));
				}
				catch (Exception value2)
				{
					Logger.Log($"SWITCHERTEST: {value2}");
				}
				finally
				{
					Shutdown();
				}
			});
			return;
		}
		if (e.Args.Contains("--toasttest"))
		{
			((DispatcherObject)this).Dispatcher.BeginInvoke((DispatcherPriority)6, (Delegate)(Action)delegate
			{
				try
				{
					string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Win81Layer");
					ImageSource icon = null;
					try
					{
						icon = new BitmapImage(new Uri("pack://application:,,,/Assets/user-icon.png"));
					}
					catch
					{
					}
					new ToastWindow(icon, "SkyDrive", "Screenshot saved", "The screenshot was added to your library.", null, "3:07 PM").QaRender(Path.Combine(path, "toasttest.png"));
					new ToastWindow(null, "Personalize", "Start colour updated", "Your Start colour was updated.", null, "3:07 PM").QaRender(Path.Combine(path, "toasttest2.png"));
				}
				catch (Exception value2)
				{
					Logger.Log($"TOASTTEST: {value2}");
				}
				finally
				{
					Shutdown();
				}
			});
			return;
		}
		if (e.Args.Contains("--osdtest"))
		{
			((DispatcherObject)this).Dispatcher.BeginInvoke((DispatcherPriority)6, (Delegate)(Action)delegate
			{
				try
				{
					string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Win81Layer");
					new OsdWindow().QaRender(Path.Combine(path, "osdtest_vol.png"));
					new OsdWindow().QaRender(Path.Combine(path, "osdtest_bri.png"), brightness: true);
				}
				catch (Exception value2)
				{
					Logger.Log($"OSDTEST: {value2}");
				}
				finally
				{
					Shutdown();
				}
			});
			return;
		}
		if (e.Args.Contains("--devicestest"))
		{
			CharmListPane dp = new CharmListPane("Devices", "Desktop", () => new CharmListPane.Row[4]
			{
				new CharmListPane.Row(59240, "Play", "Play on TV", delegate
				{
				}),
				new CharmListPane.Row(59209, "Print", "Print", delegate
				{
				}),
				new CharmListPane.Row(59380, "Project", "Project to a second screen", delegate
				{
				}),
				new CharmListPane.Row(59152, "Add a device", "Add a device", delegate
				{
				})
			})
			{
				Left = -4000.0,
				Top = -4000.0
			};
			((DispatcherObject)this).Dispatcher.BeginInvoke((DispatcherPriority)6, (Delegate)(Action)delegate
			{
				try
				{
					dp.QaRender(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Win81Layer", "devicestest.png"));
				}
				catch (Exception value2)
				{
					Logger.Log($"DEVICESTEST: {value2}");
				}
				finally
				{
					Shutdown();
				}
			});
			return;
		}
		if (e.Args.Contains("--settingsblurcap"))
		{
			SettingsPane sp = new SettingsPane();
			((DispatcherObject)this).Dispatcher.BeginInvoke((DispatcherPriority)6, (Delegate)(Action)delegate
			{
				//IL_001c: Unknown result type (might be due to invalid IL or missing references)
				//IL_0021: Unknown result type (might be due to invalid IL or missing references)
				//IL_0038: Expected O, but got Unknown
				try
				{
					sp.QaShowBlur();
					DispatcherTimer t2 = new DispatcherTimer
					{
						Interval = TimeSpan.FromMilliseconds(1500L)
					};
					t2.Tick += delegate
					{
						t2.Stop();
						try
						{
							string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Win81Layer");
							sp.QaCaptureScreen(Path.Combine(path, "settings_blur.png"));
						}
						catch (Exception value3)
						{
							Logger.Log($"SETTINGSBLURCAP capture failed: {value3}");
						}
						finally
						{
							Shutdown();
						}
					};
					t2.Start();
				}
				catch (Exception value2)
				{
					Logger.Log($"SETTINGSBLURCAP failed: {value2}");
					Shutdown();
				}
			});
			return;
		}
		if (e.Args.Contains("--settingstest"))
		{
			SettingsPane sp2 = new SettingsPane
			{
				Left = -4000.0,
				Top = -4000.0
			};
			((DispatcherObject)this).Dispatcher.BeginInvoke((DispatcherPriority)6, (Delegate)(Action)delegate
			{
				try
				{
					string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Win81Layer");
					sp2.QaRender(Path.Combine(path, "settingstest.png"));
					sp2.QaRender(Path.Combine(path, "settings_customise.png"), 1040.0, customise: true);
					sp2.QaRender(Path.Combine(path, "settings_compact.png"), 1040.0, customise: false, true);
				}
				catch (Exception value2)
				{
					Logger.Log($"SETTINGSTEST failed: {value2}");
				}
				finally
				{
					Shutdown();
				}
			});
			return;
		}
		if (e.Args.Contains("--sessiontest"))
		{
			List<AudioSessionVm> sessions = AudioSessions.Enumerate();
			Logger.Log($"SESSIONTEST: {sessions.Count} sessions");
			foreach (AudioSessionVm s in sessions)
			{
				Logger.Log($"  session: {s.Name} vol={s.VolumePct} muted={s.Muted} icon={s.Icon != null}");
			}
			Shutdown();
			return;
		}
		if (e.Args.Contains("--applycursors"))
		{
			CursorScheme.Apply();
			SettingsStore.Update(delegate(AppSettings appSettings) { appSettings.UseWin81Cursors = CursorScheme.IsApplied; });
			Shutdown();
			return;
		}
		if (e.Args.Contains("--clocktest"))
		{
			CharmsClock clk = new CharmsClock
			{
				Left = -4000.0,
				Top = -4000.0
			};
			((DispatcherObject)this).Dispatcher.BeginInvoke((DispatcherPriority)6, (Delegate)(Action)delegate
			{
				try
				{
					string outPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Win81Layer", "clocktest.png");
					clk.QaRender(outPath);
				}
				catch (Exception value2)
				{
					Logger.Log($"CLOCKTEST failed: {value2}");
				}
				finally
				{
					Shutdown();
				}
			});
			return;
		}
		if (e.Args.Contains("--icontest"))
		{
			string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Win81Layer", "icontest");
			Directory.CreateDirectory(dir);
			foreach (string old in Directory.GetFiles(dir, "*.png"))
			{
				try { File.Delete(old); } catch { }
			}
			int n = 0;
			int ordinal = 0;
			HashSet<string> required = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
			{
				"LatencyMon",
				"Windows Performance Analyzer",
				"Windows Performance Recorder"
			};
			foreach (var (entry2, item) in AppInventory.EnumerateAppsFolder())
			{
				bool requiredProbe = required.Contains(entry2.Name);
				if (ordinal++ >= 28 && !requiredProbe)
				{
					continue;
				}
				if (!((AppInventory.LoadIcon(entry2.LaunchPath) ?? AppInventory.IconFromItem(item)) is BitmapSource src))
				{
					Logger.Log("ICON [" + entry2.Name + "] null");
					continue;
				}
				string safe = string.Concat(entry2.Name.Split(Path.GetInvalidFileNameChars()));
				PngBitmapEncoder enc = new PngBitmapEncoder();
				enc.Frames.Add(BitmapFrame.Create(src));
				using (FileStream fs = File.Create(Path.Combine(dir, $"{n:00}_{safe}_{src.PixelWidth}x{src.PixelHeight}.png")))
				{
					enc.Save(fs);
				}
				string resolved = AppInventory.ResolveFileBackedPath(entry2.LaunchPath) ?? "shell";
				Logger.Log($"ICON [{entry2.Name}] {src.PixelWidth}x{src.PixelHeight} source={resolved}");
				n++;
			}
			Shutdown();
			return;
		}
		if (e.Args.Contains("--traydump"))
		{
			List<TrayIconInfo> icons = TrayReader.Enumerate();
			Logger.Log($"TRAYDUMP: {icons.Count} notification icons");
			string dir2 = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Win81Layer", "traydump");
			Directory.CreateDirectory(dir2);
			foreach (string old in Directory.GetFiles(dir2, "*.png"))
			{
				try { File.Delete(old); } catch { }
			}
			int n2 = 0;
			foreach (TrayIconInfo ic in icons)
			{
				bool savedIcon = false;
				bool contrastAdjusted = false;
				try
				{
					if (ic.HIcon != IntPtr.Zero)
					{
						BitmapSource raw = System.Windows.Interop.Imaging.CreateBitmapSourceFromHIcon(ic.HIcon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
						ImageSource visible = AppInventory.EnsureVisibleOnDark(raw);
						contrastAdjusted = !ReferenceEquals(raw, visible);
						SavePng(raw, Path.Combine(dir2, $"icon_{n2:00}_raw.png"));
						if (visible is BitmapSource visibleBitmap)
						{
							SavePng(visibleBitmap, Path.Combine(dir2, $"icon_{n2:00}_dark.png"));
						}
						savedIcon = true;
					}
				}
				catch (Exception ex)
				{
					Logger.Log($"  icon {n2} capture failed: {ex.Message}");
				}
				Logger.Log($"  [{n2:00}] tip='{ic.Tooltip}' owner=0x{((IntPtr)ic.OwnerHwnd).ToInt64():X} id={ic.Id} msg=0x{ic.CallbackMessage:X} hidden={ic.Hidden} overflow={ic.FromOverflow} hIcon={ic.HIcon != IntPtr.Zero} savedPng={savedIcon} contrastAdjusted={contrastAdjusted}");
				n2++;
			}
			Shutdown();
			return;

			static void SavePng(BitmapSource bitmap, string path)
			{
				PngBitmapEncoder encoder = new PngBitmapEncoder();
				encoder.Frames.Add(BitmapFrame.Create(bitmap));
				using FileStream stream = File.Create(path);
				encoder.Save(stream);
			}
		}
		if (e.Args.Contains("--traytest"))
		{
			TaskbarWindow tb3 = new TaskbarWindow
			{
				Left = -4000.0,
				Top = -4000.0,
				Width = 900.0,
				Height = 40.0
			};
			tb3.Show();
			((DispatcherObject)this).Dispatcher.BeginInvoke((DispatcherPriority)6, (Delegate)(Action)delegate
			{
				try
				{
					string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Win81Layer");
					tb3.QaRenderTray(Path.Combine(path, "traytest.png"));
					tb3.QaRenderFlyouts(Path.Combine(path, "flyout_volume.png"), Path.Combine(path, "flyout_clock.png"), Path.Combine(path, "flyout_network.png"));
				}
				catch (Exception value2)
				{
					Logger.Log($"TRAYTEST failed: {value2}");
				}
				finally
				{
					Shutdown();
				}
			});
			return;
		}
		if (uxPanelTest)
		{
			try
			{
				PersistenceDiagnostics.SuppressAutomaticPostBootVerification = true;
				string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Win81Layer", "uxqa");
				Directory.CreateDirectory(dir);
				(int wallpaperCount, long decodeMs) = StartScreen.QaWarmWallpaperThumbnails();
				StartScreen start = new StartScreen
				{
					Left = -4000.0,
					Top = -4000.0
				};
				long personalizeBuildMs = start.QaRenderPersonalize(Path.Combine(dir, "personalize.png"));
				start.Close();
				int quickActionCount = ActionCenter.QaRenderPanel(Path.Combine(dir, "action-center.png"));
				var report = new
				{
					generatedUtc = DateTime.UtcNow,
					wallpaperCount,
					wallpaperDecodeMs = decodeMs,
					personalizeControlBuildMs = personalizeBuildMs,
					quickActionCount,
					personalizeScreenshot = Path.Combine(dir, "personalize.png"),
					actionCenterScreenshot = Path.Combine(dir, "action-center.png")
				};
				string reportPath = Path.Combine(dir, "ux-panel-qa.json");
				File.WriteAllText(reportPath, System.Text.Json.JsonSerializer.Serialize(report, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
				Logger.Log($"UXPANELTEST: actions={quickActionCount}, wallpapers={wallpaperCount}, decode={decodeMs}ms, controls={personalizeBuildMs}ms -> {reportPath}");
			}
			catch (Exception ex)
			{
				Logger.Log("UXPANELTEST failed: " + ex);
				Shutdown(1);
				return;
			}
			Shutdown();
			return;
		}
		if (compositionQueueTest)
		{
			DesktopComposition.TransientPreviewDiagnostics = true;
			string beforeMode = DesktopComposition.Mode;
			string targetMode = string.Equals(beforeMode, "windows81", StringComparison.OrdinalIgnoreCase) ? "native" : "windows81";
			string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Win81Layer", "uxqa");
			Directory.CreateDirectory(dir);
			Stopwatch request = Stopwatch.StartNew();
			bool targetIdle = false;
			bool restoreIdle = false;
			bool targetForeignBorderOff = false;
			bool restoreForeignBorderOff = false;
			long targetBackgroundMs = -1;
			long restoreRequestMs = -1;
			long restoreBackgroundMs = -1;
			int broadcastsBefore = DesktopComposition.DiagnosticAccentBroadcastBatches;
			int bridgeRefreshesBefore = DwmBlurGlassBridge.DiagnosticRefreshPosts;
			int targetAccentBroadcastBatches = -1;
			int targetBridgeRefreshPosts = -1;
			int restoreAccentBroadcastBatches = -1;
			int restoreBridgeRefreshPosts = -1;
			int completedBefore = DesktopComposition.DiagnosticCompletedTransitions;
			int coalescedBefore = DesktopComposition.DiagnosticCoalescedTransitions;
			int targetCompletedTransitions = -1;
			int targetCoalescedTransitions = -1;
			int restoreCompletedTransitions = -1;
			try
			{
				// Simulate a user clicking through several mode tiles quickly. Only the final target may touch native
				// state; the first two requests must be coalesced during the 90 ms settle window.
				DesktopComposition.PreviewMode("alchemy-enhanced", 120);
				DesktopComposition.PreviewMode("native", 120);
				DesktopComposition.PreviewMode(targetMode, 120);
				request.Stop();
				Stopwatch targetBackground = Stopwatch.StartNew();
				targetIdle = DesktopComposition.WaitForIdle(TimeSpan.FromSeconds(90.0));
				targetBackground.Stop();
				targetBackgroundMs = targetBackground.ElapsedMilliseconds;
				targetAccentBroadcastBatches = DesktopComposition.DiagnosticAccentBroadcastBatches - broadcastsBefore;
				targetBridgeRefreshPosts = DwmBlurGlassBridge.DiagnosticRefreshPosts - bridgeRefreshesBefore;
				targetCompletedTransitions = DesktopComposition.DiagnosticCompletedTransitions - completedBefore;
				targetCoalescedTransitions = DesktopComposition.DiagnosticCoalescedTransitions - coalescedBefore;
				targetForeignBorderOff = !DesktopComposition.AccentBorderEnabledForDiagnostics;
				Stopwatch restoreRequest = Stopwatch.StartNew();
				DesktopComposition.EndPreview();
				restoreRequest.Stop();
				restoreRequestMs = restoreRequest.ElapsedMilliseconds;
				Stopwatch restoreBackground = Stopwatch.StartNew();
				restoreIdle = DesktopComposition.WaitForIdle(TimeSpan.FromSeconds(90.0));
				restoreBackground.Stop();
				restoreBackgroundMs = restoreBackground.ElapsedMilliseconds;
				restoreAccentBroadcastBatches = DesktopComposition.DiagnosticAccentBroadcastBatches - broadcastsBefore - targetAccentBroadcastBatches;
				restoreBridgeRefreshPosts = DwmBlurGlassBridge.DiagnosticRefreshPosts - bridgeRefreshesBefore - targetBridgeRefreshPosts;
				restoreCompletedTransitions = DesktopComposition.DiagnosticCompletedTransitions - completedBefore - targetCompletedTransitions;
				restoreForeignBorderOff = !DesktopComposition.AccentBorderEnabledForDiagnostics;
			}
			catch (Exception ex)
			{
				Logger.Log("COMPOSITIONQUEUETEST failed: " + ex);
				try { DesktopComposition.EndPreview(); } catch { }
			}
			AppSettings after = SettingsStore.Load();
			bool persistedRestored = string.Equals(after.DesktopCompositionMode, beforeMode, StringComparison.OrdinalIgnoreCase)
				&& string.IsNullOrEmpty(after.DeskCompPreviewMode)
				&& after.DeskCompPreviewUntilUtcTicks == 0L;
			var report = new
			{
				generatedUtc = DateTime.UtcNow,
				persistedMode = beforeMode,
				targetMode,
				requestReturnMs = request.ElapsedMilliseconds,
				targetBackgroundMs,
				targetIdle,
				targetAccentBroadcastBatches,
				targetBridgeRefreshPosts,
				targetCompletedTransitions,
				targetCoalescedTransitions,
				targetForeignBorderOff,
				restoreRequestMs,
				restoreBackgroundMs,
				restoreIdle,
				restoreAccentBroadcastBatches,
				restoreBridgeRefreshPosts,
				restoreCompletedTransitions,
				restoreForeignBorderOff,
				persistedRestored,
				lastStatus = DesktopComposition.LastStatus.ToString(),
				lastError = DesktopComposition.LastError
			};
			string reportPath = Path.Combine(dir, "composition-queue-qa.json");
			File.WriteAllText(reportPath, System.Text.Json.JsonSerializer.Serialize(report, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
			Logger.Log($"COMPOSITIONQUEUETEST: request={request.ElapsedMilliseconds}ms apply-bg={targetBackgroundMs}ms restore-request={restoreRequestMs}ms restore-bg={restoreBackgroundMs}ms state-restored={persistedRestored} -> {reportPath}");
			Shutdown((targetIdle && restoreIdle && persistedRestored && targetForeignBorderOff && restoreForeignBorderOff) ? 0 : 1);
			return;
		}
		if (dwmBlurGlassPolicyTest)
		{
			try
			{
				PersistenceDiagnostics.SuppressAutomaticPostBootVerification = true;
				string reportPath = DwmBlurGlassDiagnostics.RunPolicyRoundTrip();
				Logger.Log("DWMBLURGLASSTEST passed -> " + reportPath);
			}
			catch (Exception ex)
			{
				Logger.Log("DWMBLURGLASSTEST failed: " + ex);
				Shutdown(1);
				return;
			}
			Shutdown();
			return;
		}
		if (dwmBlurGlassDetach)
		{
			try
			{
				DwmBlurGlassBridge.Disable();
				if (DwmBlurGlassBridge.Injected || DwmBlurGlassBridge.HostRunning || DwmBlurGlassBridge.RollbackSnapshotPresent)
				{
					throw new InvalidOperationException("DWMBlurGlass did not reach the fully detached, journal-free state.");
				}
				Logger.Log("DWMBLURGLASSDETACH passed: hook and host stopped; external config restored.");
			}
			catch (Exception ex)
			{
				Logger.Log("DWMBLURGLASSDETACH failed: " + ex);
				Shutdown(1);
				return;
			}
			Shutdown();
			return;
		}
		_instanceMutex = new Mutex(initiallyOwned: true, "Local\\Win81Layer.SingleInstance", out var createdNew);
		_isPrimary = createdNew;
		if (!createdNew)
		{
			if (Array.IndexOf(e.Args, "--healed") >= 0)
			{
				try
				{
					if (_instanceMutex.WaitOne(TimeSpan.FromSeconds(12L)))
					{
						_isPrimary = true;
					}
				}
				catch (AbandonedMutexException)
				{
					_isPrimary = true;
				}
			}
			if (!_isPrimary)
			{
				Logger.Log("Another Win81Layer instance is already running - exiting this one.");
				_instanceMutex = null;
				Shutdown();
				return;
			}
			Logger.Log("Self-heal (--healed): acquired the shell from the degraded instance.");
		}
		object obj = _003C_003Ec._003C_003E9__30_2;
		if (obj == null)
		{
			DispatcherUnhandledExceptionEventHandler val = delegate(object _, DispatcherUnhandledExceptionEventArgs args)
			{
				Logger.Log($"Dispatcher exception: {args.Exception}");
				args.Handled = true;
			};
			_003C_003Ec._003C_003E9__30_2 = val;
			obj = (object)val;
		}
		base.DispatcherUnhandledException += (DispatcherUnhandledExceptionEventHandler)obj;
		AppDomain.CurrentDomain.UnhandledException += delegate(object _, UnhandledExceptionEventArgs args)
		{
			Logger.Log($"Unhandled exception: {args.ExceptionObject}");
			if (!args.IsTerminating)
			{
				return;   // non-terminating (rare) — don't tear the shell down to native for a survivable fault
			}
			try { NativeShell.ResumeShellHosts(); } catch { }   // guard each so one failure can't skip the other
			try { AppBar.ShowNativeTaskbar(); } catch { }
		};
		AppDomain.CurrentDomain.ProcessExit += delegate
		{
			if (!_sessionEnding)
			{
				try { NativeShell.ResumeShellHosts(); } catch { }
				try { AppBar.ShowNativeTaskbar(); } catch { }
			}
			try
			{
				Logger.Flush();   // persist any buffered log lines before the process dies
				Process watchdog = _watchdog;
				if (watchdog != null && !watchdog.HasExited)
				{
					_watchdog.Kill();
				}
			}
			catch
			{
			}
		};
		TaskScheduler.UnobservedTaskException += delegate(object? _, UnobservedTaskExceptionEventArgs args)
		{
			Logger.Log($"Unobserved task exception: {args.Exception}");
			args.SetObserved();
		};
		// COLD-BOOT FIX: kick an off-UI settings prime as early as possible so the taskbar's first re-apply and the
		// Start screen build see the REAL saved settings (align=Center, custom wallpaper) instead of degraded defaults
		// when the boot storm briefly locks settings.json. Fire-and-forget; the UI Load below stays fast, and the
		// primed cache lands before the 2.5s/6s re-apply backstops read it. Idempotent with StartScreen's own prime.
		// The first synchronous Load warms the in-memory settings cache. Do it BEFORE spawning the deep-retry warm
		// thread — that thread can hold the settings lock across many cold-boot retries, so spawning it first could
		// head-of-line block this Load (and thus first taskbar paint). Subsequent Loads then hit the warm cache.
		AppSettings settings = SettingsStore.Load();
		// A prepared profile transaction means the prior process stopped between prepare and commit. Restore its
		// source slot before any taskbar/native-host decision is made, then continue from the reconciled settings.
		try { ShellProfileManager.InitializeAndRecover(); } catch (Exception exRec) { Logger.Log("Profile InitializeAndRecover failed (continuing with current settings): " + exRec.Message); }
		settings = SettingsStore.Load();
		if (_isPrimary)
		{
			try
			{
				System.Threading.Thread prime = new System.Threading.Thread((System.Threading.ThreadStart)delegate
				{
					SettingsStore.PrimeCacheOffThread();
				})
				{
					IsBackground = true,
					Name = "SettingsPrime",
					Priority = System.Threading.ThreadPriority.AboveNormal
				};
				prime.Start();
			}
			catch
			{
			}
		}
		if (_isPrimary && settings.TaskbarEnabled)
		{
			try
			{
				AppBar.HideNativeTaskbar();
			}
			catch
			{
			}
		}
		try
		{
			Process.GetCurrentProcess().PriorityClass = ProcessPriorityClass.AboveNormal;
		}
		catch
		{
		}
		try { ProcessPerformancePolicy.ApplyInteractiveShell(); } catch (Exception exPol) { Logger.Log("ApplyInteractiveShell failed: " + exPol.Message); }
		try { ShellLaunch.Warm(); } catch (Exception exWarm) { Logger.Log("ShellLaunch.Warm failed: " + exWarm.Message); }
		if (_isPrimary)
		{
			try
			{
				File.Delete(CleanExitMarkerPath);
			}
			catch
			{
			}
		}
		// UsageStore.Load() (usage.json read/deserialize) moved off the boot UI thread to the head of the apps STA
		// thread (StartScreen.LoadAppsOnStaThread), before its first consumer UsageStore.RegisterInventory.
		Motion.Mode = Motion.Parse(settings.MotionMode);
		if (settings.MotionMode == "Authentic" && !SystemParameters.ClientAreaAnimation)
		{
			Motion.Mode = Motion.Parse("Reduced");
		}
		if (!settings.FreeGridDefaultV1)
		{
			SettingsStore.Update(delegate(AppSettings appSettings)
			{
				appSettings.PackedGrid = false;
				appSettings.FreeGridDefaultV1 = true;
			});
			settings.PackedGrid = false;
		}
		TilePanel.Packed = settings.PackedGrid;
		TileMetrics.Scale = settings.TileScale;
		Task.Run((Action)Wallpapers.SeedFromPack);
		if (settings.RunFirstTask)
		{
			Task.Run(delegate
			{
				try
				{
					LogonTask.SetEnabled(enabled: true);
				}
				catch
				{
				}
				try
				{
					if (Autostart.IsEnabled())
					{
						Autostart.SetEnabled(enabled: false);
						Logger.Log("Autostart de-dup: removed HKCU Run key - logon task is the single autostart");
					}
				}
				catch
				{
				}
			});
		}
		else if (Autostart.IsEnabled())
		{
			Autostart.SetEnabled(enabled: true);
			Logger.Log("Autostart path refreshed -> " + Environment.ProcessPath);
		}
		Task.Run((Action)delegate
		{
			if (settings.UseWin81Sounds) SoundScheme.Apply(); else SoundScheme.Revert();
		});
		Task.Run(delegate
		{
			SeedWin81CursorAssets();   // ensure the bundled aero cursors exist where CursorScheme.Resolve() looks
			CursorScheme.EnsureSchemeInstalled();
			if (settings.UseWin81Cursors) CursorScheme.Apply(); else CursorScheme.Revert();
		});
		TaskbarWorkArea.RecoverFromUncleanExit();
		Task.Run((Action)DwmBlurGlassBridge.EnsureOnDemandAutostartPolicy);
		((DispatcherObject)this).Dispatcher.BeginInvoke((Delegate)new Action(DesktopComposition.Resume), (DispatcherPriority)4, Array.Empty<object>());
		// StartScreen is the CORE Metro surface; it has 30+ synchronous call sites, so isolating it to null would just
		// move the crash to the first unguarded use. A ctor failure here is left FATAL by design — the supervisor +
		// safe-mode hard-cap fall back to the native shell, which is safer than a half-null limping shell.
		_startScreen = new StartScreen();
		GoogleAuth.ConnectionChanged += GoogleConnectionChanged;
		// Fault isolation: the charm panes + charms bar are non-core and self-contained; a construction fault here must
		// not abort the whole boot (taskbar + Start keep working; only charms/search/settings-pane degrade).
		try
		{
		_settingsPane = new SettingsPane();
		_settingsPane.PersonalizeRequested += delegate
		{
			_startScreen.ShowPersonalize();
		};
		_settingsPane.ChangePcSettingsRequested += OpenPcSettings;
		_searchPane = new SearchPane(() => _startScreen.Apps, delegate(AppEntry a)
		{
			_startScreen.LaunchApp(a);
		}, () => _startScreen.WorkspaceNames(), delegate(string name)
		{
			_startScreen.LaunchWorkspaceByName(name);
		});
		_startScreen.SearchRequested += delegate
		{
			_searchPane.ShowPane();
		};
		// Unified Action Router hooks (marshal UI-touching routes to the UI thread) — Deep-Pin tiles, Search and launcher://
		// URIs all resolve through these to the SAME workspace/search backends.
		ActionRouter.WorkspaceLaunch = delegate(string name)
		{
			((DispatcherObject)this).Dispatcher.BeginInvoke((Delegate)(Action)delegate { _startScreen.LaunchWorkspaceByName(name); }, Array.Empty<object>());
		};
		ActionRouter.ShowSearch = delegate(string q)
		{
			((DispatcherObject)this).Dispatcher.BeginInvoke((Delegate)(Action)delegate { _searchPane.ShowQuery(q); }, Array.Empty<object>());
		};
		_startScreen.PcSettingsRequested += OpenPcSettings;
		// UNIFIED DEVICE CENTER (directive §14-20): the four Win+P projection modes, cast/print, a Bluetooth toggle (only when
		// radios are present), sound settings, and per removable-drive Open/Eject — all reusing existing services. Per-open
		// factory so removable drives + radio presence are current each time.
		_devicesPane = new CharmListPane("Devices", "Desktop", delegate
		{
			List<CharmListPane.Row> rows = new List<CharmListPane.Row>
			{
				new CharmListPane.Row(59380, "Duplicate", "Show the same on both screens", delegate { DeviceActions.Display("/clone"); }),
				new CharmListPane.Row(59380, "Extend", "Extend across your screens", delegate { DeviceActions.Display("/extend"); }),
				new CharmListPane.Row(59380, "Second screen only", "Use the external display only", delegate { DeviceActions.Display("/external"); }),
				new CharmListPane.Row(59380, "PC screen only", "Use this display only", delegate { DeviceActions.Display("/internal"); }),
				new CharmListPane.Row(59240, "Play", "Cast to a wireless display or device", delegate { Keystroke.Chord(91, 75); }),
				new CharmListPane.Row(59209, "Print", "Print the current app", PrintForegroundApp)
			};
			if (NetCaps.RadiosPresent)
			{
				rows.Add(new CharmListPane.Row(59152, "Toggle Bluetooth", "Turn Bluetooth on or off", delegate { _ = RadioQuick.ToggleBluetooth(); }));
			}
			rows.Add(new CharmListPane.Row(59152, "Add a device", "Bluetooth & other devices", delegate { CharmListPane.Launch("ms-settings:bluetooth"); }));
			rows.Add(new CharmListPane.Row(59239, "Sound settings", "Output, input & volume", delegate { CharmListPane.Launch("ms-settings:sound"); }));
			foreach (string drive in DeviceActions.RemovableDrives())
			{
				string d = drive;
				rows.Add(new CharmListPane.Row(59152, "Open " + d, "Removable drive", delegate { DeviceActions.Open(d); }));
				rows.Add(new CharmListPane.Row(59152, "Eject " + d, "Safely remove hardware", delegate { DeviceActions.Eject(d); }));
			}
			return rows;
		});
		// CONTEXT-AWARE SHARE (directive §6/§22): compose rows from the app that was foreground when Charms opened — reliable
		// facts only (app name, window title, Explorer folder), via existing FileShell/Clipboard. Per-open factory.
		_sharePane = new CharmListPane("Share", "Desktop", delegate
		{
			List<CharmListPane.Row> rows = new List<CharmListPane.Row>
			{
				new CharmListPane.Row(59170, "Screenshot", "Capture the screen to a file & clipboard", CaptureScreenshot)
			};
			string? folder = ShareActions.ExplorerFolderPath();
			if (!string.IsNullOrEmpty(folder))
			{
				string f = folder;
				rows.Add(new CharmListPane.Row(59592, "Copy folder path", f, delegate { ShareActions.CopyText(f); }));
				rows.Add(new CharmListPane.Row(59222, "Open PowerShell here", f, delegate { FileShell.TerminalHere(f, powershell: true); }));
			}
			string title = ShareActions.ForegroundTitle();
			if (!string.IsNullOrEmpty(title))
			{
				string tt = title;
				rows.Add(new CharmListPane.Row(59592, "Copy window title", tt, delegate { ShareActions.CopyText(tt); }));
			}
			string app = ShareActions.ForegroundAppName();
			if (!string.IsNullOrEmpty(app))
			{
				string an = app;
				rows.Add(new CharmListPane.Row(59592, "Copy app name", an, delegate { ShareActions.CopyText(an); }));
			}
			rows.Add(new CharmListPane.Row(59161, "Find more apps in the Store", null, delegate { CharmListPane.Launch("ms-windows-store:"); }));
			return rows;
		});
		_charmsBar = new CharmsBar();
		_charmsBar.StartRequested += ToggleStart;
		_charmsBar.SearchRequested += delegate
		{
			_searchPane.ShowPane();
		};
		_charmsBar.SettingsRequested += delegate
		{
			_settingsPane.ShowPane();
		};
		_charmsBar.DevicesRequested += delegate
		{
			_devicesPane.ShowPane();
		};
		_charmsBar.ShareRequested += delegate
		{
			_sharePane.ShowPane();
		};
		}
		catch (Exception exPanes)
		{
			Logger.Log("Charms/panes init failed (charms + search + settings pane degraded, shell continues): " + exPanes.Message);
		}
		Action topLeft = WindowUtil.OpenTaskView;
		try
		{
			_appSwitcher = new AppSwitcher();
			topLeft = delegate
			{
				_appSwitcher.ShowSwitcher();
			};
			_appSwitcher.PinToStartRequested = delegate(string? exe, string name)
			{
				((DispatcherObject)this).Dispatcher.BeginInvoke((Delegate)(Action)delegate
				{
					_startScreen?.PinAppByPath(exe, name);
				}, Array.Empty<object>());
			};
			_appSwitcher.StartRequested = delegate
			{
				((DispatcherObject)this).Dispatcher.BeginInvoke((Delegate)new Action(ToggleStart), Array.Empty<object>());
			};
		}
		catch (Exception ex3)
		{
			Logger.Log("AppSwitcher init failed, keeping Task View: " + ex3.Message);
		}
		FileContextMenu.PinToStart = delegate(string path)
		{
			((DispatcherObject)this).Dispatcher.BeginInvoke((Delegate)(Action)delegate
			{
				_startScreen?.PinAppByPath(path, string.Empty);
			}, Array.Empty<object>());
		};
		FileContextMenu.PinToTaskbar = delegate(string path)
		{
			((DispatcherObject)this).Dispatcher.BeginInvoke((Delegate)(Action)delegate
			{
				try
				{
					List<PinnedApp> list = TaskbarPins.Load();
					if (!list.Any((PinnedApp p) => string.Equals(p.ExePath, path, StringComparison.OrdinalIgnoreCase) || string.Equals(p.LaunchPath, path, StringComparison.OrdinalIgnoreCase)))
					{
						list.Add(new PinnedApp
						{
							Name = Path.GetFileNameWithoutExtension(path),
							LaunchPath = path,
							ExePath = path
						});
						TaskbarPins.Save(list);
						TaskbarWindow.ReloadPins();
					}
				}
				catch (Exception ex7)
				{
					Logger.Log("pin-to-taskbar " + path + ": " + ex7.Message);
				}
			}, Array.Empty<object>());
		};
		_hotCorners = new HotCorners
		{
			// Charms NO LONGER opens from the top-right / bottom-right hot corners — that caused constant accidental
			// activation (throwing the cursor toward Close, scrollbars, the clock or another monitor). Charms is now a
			// deliberate action only: the right-edge pull gesture (see CharmsEdgeGesture) or Win+C. RightCorners left
			// unset so the poll no longer triggers Charms.
			TopLeft = settings.SwitcherEdgeReveal ? topLeft : null,
			LeftEdge = settings.SwitcherEdgeReveal ? topLeft : null,
			LeftEdgeDwellTicks = System.Math.Max(2, settings.SwitcherEdgeDwellMs / 100),
			Enabled = settings.HotCornersEnabled
		};
		TaskbarWindow.SearchRequested += delegate
		{
			((DispatcherObject)this).Dispatcher.BeginInvoke((Delegate)(Action)delegate
			{
				if (_searchPane.IsVisible)
				{
					_searchPane.HidePane();
				}
				else
				{
					_searchPane.ShowPane();
				}
			}, Array.Empty<object>());
		};
		TaskbarWindow.TaskViewRequested += delegate
		{
			((DispatcherObject)this).Dispatcher.BeginInvoke((Delegate)(Action)delegate
			{
				if (_appSwitcher != null)
				{
					_appSwitcher.Toggle();
				}
				else
				{
					topLeft();
				}
			}, Array.Empty<object>());
		};
		_taskbar = new TaskbarManager();
		_taskbar.StartRequested += delegate
		{
			((DispatcherObject)this).Dispatcher.BeginInvoke((Delegate)new Action(ToggleStart), Array.Empty<object>());
		};
		_taskbar.StartPeekRequested += delegate
		{
			((DispatcherObject)this).Dispatcher.BeginInvoke((Delegate)(Action)delegate
			{
				StartScreen startScreen = _startScreen;
				// Win7 opt-in: never peek the Metro Start (it would flash before the Win7 menu opens on release).
				if (startScreen != null && !startScreen.IsVisible && !SettingsStore.Current.Win7StartMenuEnabled)
				{
					_startScreen.ShowPeek();
				}
			}, Array.Empty<object>());
		};
		_taskbar.StartPeekEnded += delegate(bool commit)
		{
			((DispatcherObject)this).Dispatcher.BeginInvoke((Delegate)(Action)delegate
			{
				if (SettingsStore.Current.Win7StartMenuEnabled)
				{
					// No Metro peek was shown; a committed hold-release (StartRequested is suppressed on holds) opens the Win7 menu.
					if (commit)
					{
						ToggleStart();
					}
					return;
				}
				if (commit)
				{
					_startScreen?.CommitPeek();
				}
				else
				{
					_startScreen?.CancelPeek();
				}
			}, Array.Empty<object>());
		};
		if (settings.TaskbarEnabled)
		{
			((DispatcherObject)this).Dispatcher.BeginInvoke((Delegate)(Action)delegate
			{
				// Activate() hides the native taskbar and builds ours; if it throws mid-build, re-assert the native
				// taskbar so the user is never left with NO taskbar at all.
				try
				{
					_taskbar.Activate();
				}
				catch (Exception exTb)
				{
					Logger.Log("Taskbar activate failed - reverting to native taskbar: " + exTb.Message);
					try { _taskbar.Deactivate(); } catch { }
					try { AppBar.ShowNativeTaskbar(); } catch { }
				}
				if (taskbarRuntimeTest)
				{
					TaskbarRuntimeDiagnostics.Begin(this, _taskbar);
				}
			}, (DispatcherPriority)6, Array.Empty<object>());
		}
		else if (taskbarRuntimeTest)
		{
			Logger.Log("[taskbar-runtime-qa] cannot start because the taskbar is disabled");
			Shutdown(1);
		}
		_selfHealArgs = e.Args;   // stored so StartScreen can re-check after Profile.Load (dropped-tiles case)
		((DispatcherObject)this).Dispatcher.BeginInvoke((Delegate)(Action)delegate
		{
			MaybeSelfHealDegradedBoot(e.Args);
		}, (DispatcherPriority)2, Array.Empty<object>());
		ScheduleDominanceBackstop();   // re-assert dominant composition + native suppression if an early degraded read applied native
		AppLauncher.PrewarmIdentities();   // warm the packaged-app AUMID cache off-thread so the first app launch is instant
		_shellTimer = new DispatcherTimer((DispatcherPriority)4)   // Background: never competes with input/animation
		{
			Interval = TimeSpan.FromMilliseconds(1000L)   // heartbeat + supervisor tick; thresholds are 12s+, so 1s is ample
		};
		_shellTimer.Tick += delegate
		{
			// Heartbeat only needs to stay ahead of the 12s watchdog threshold; writing on every 4th tick (~4s)
			// instead of every second cuts heartbeat.dat create/writes ~4x (SEP scan + SSD churn); coverage kept.
			if ((++_heartbeatTick & 3) == 0) WriteHeartbeat();   // UI-thread liveness stamp (stays OUTSIDE the guard)
			try
			{
				if (ShellProfileChangeSignal.TryConsume())
				{
					AppSettings refreshed = SettingsStore.LoadForced();   // external profile change must bypass the Load() TTL
					DesktopComposition.ApplyPersistedModeTransition(refreshed.DesktopCompositionMode);
					ApplyShellProfileLive(refreshed);
					Logger.Log("Shell profile refresh signal consumed by the managed shell.");
				}
				ShellSupervisor.Tick(_taskbar?.IsActive ?? false);
			}
			catch (Exception exTick)
			{
				Logger.Log("shell tick body failed (heartbeat unaffected, watchdog still fed): " + exTick.Message);
			}
		};
		WriteHeartbeat();   // initial stamp so the watchdog has a baseline before it starts polling
		_shellTimer.Start();
		SystemEvents.DisplaySettingsChanged += delegate
		{
			((DispatcherObject)this).Dispatcher.BeginInvoke((Delegate)(Action)delegate
			{
				TaskbarManager? taskbar = _taskbar;
				if (taskbar != null && taskbar.IsActive)
				{
					try
					{
						AppBar.ReassertHidden();
					}
					catch
					{
					}
				}
			}, Array.Empty<object>());
		};
		((DispatcherObject)this).Dispatcher.BeginInvoke((Delegate)(Action)delegate
		{
			try
			{
				Process.GetCurrentProcess().PriorityClass = ProcessPriorityClass.Normal;
			}
			catch
			{
			}
		}, (DispatcherPriority)2, Array.Empty<object>());
		((DispatcherObject)this).Dispatcher.BeginInvoke((Delegate)new Action(TaskbarContextMenu.Warm), (DispatcherPriority)2, Array.Empty<object>());
		if (!_managed)
		{
			Task.Run(delegate
			{
				try
				{
					_watchdog = Process.Start(new ProcessStartInfo(Environment.ProcessPath, $"--watchdog {Environment.ProcessId}")
					{
						UseShellExecute = false,
						CreateNoWindow = true
					});
					Logger.Log($"Watchdog started pid={_watchdog?.Id}");
				}
				catch (Exception ex7)
				{
					Logger.Log("Watchdog start failed: " + ex7.Message);
				}
			});
		}
		if (e.Args.Contains("--samplewin"))
		{
			((DispatcherObject)this).Dispatcher.BeginInvoke((Delegate)new Action(Win81Window.ShowSample), (DispatcherPriority)6, Array.Empty<object>());
		}
		_winHook = new WinKeyHook
		{
			ReplaceStartButton = settings.ReplaceStartMenu,
			ReplaceDesktopMenu = settings.ReplaceDesktopMenu,
			ReplaceExplorerShortcut = settings.DominantMode,
			DominantMode = settings.DominantMode
		};
		_winHook.WinTapped += delegate
		{
			((DispatcherObject)this).Dispatcher.BeginInvoke((Delegate)new Action(ToggleStart), Array.Empty<object>());
		};
		_winHook.WinCTapped += delegate
		{
			((DispatcherObject)this).Dispatcher.BeginInvoke((Delegate)(Action)delegate
			{
				_charmsBar?.ToggleCharms();
			}, Array.Empty<object>());
		};
		_winHook.WinITapped += delegate
		{
			((DispatcherObject)this).Dispatcher.BeginInvoke((Delegate)new Action(OpenPcSettings), Array.Empty<object>());
		};
		// Panic / escape hatch (Ctrl+Alt+Shift+Backspace): cleanly exit the launcher and hand the desktop back to the
		// native Windows shell. ExitApp writes the clean-exit marker (supervisor stands down until next reboot), reverts
		// composition, resumes the suspended native hosts and shows the native taskbar — the safe "give me Windows back".
		_winHook.PanicRestore += delegate
		{
			Logger.Log("Panic hotkey (Ctrl+Alt+Shift+Backspace): restoring native shell and exiting.");
			((DispatcherObject)this).Dispatcher.BeginInvoke((Delegate)new Action(ExitApp), Array.Empty<object>());
		};
		_winHook.WinETapped += delegate
		{
			((DispatcherObject)this).Dispatcher.BeginInvoke((Delegate)(Action)delegate
			{
				if (!FileBrowser.TryShow())
				{
					try
					{
						// Off the UI thread: synchronous ShellExecuteEx can freeze the whole shell on a cold/loaded system.
						ShellLaunch.Run(delegate
						{
							Process.Start(new ProcessStartInfo("explorer.exe") { UseShellExecute = true });
						});
					}
					catch (Exception ex)
					{
						Logger.Log("Win+E fallback failed: " + ex.Message);
					}
				}
			}, Array.Empty<object>());
		};
		_winHook.WinTabTapped += delegate
		{
			((DispatcherObject)this).Dispatcher.BeginInvoke((Delegate)(Action)delegate
			{
				_appSwitcher?.CycleOrShow();
			}, Array.Empty<object>());
		};
		_winHook.WinSTapped += delegate
		{
			((DispatcherObject)this).Dispatcher.BeginInvoke((Delegate)(Action)delegate
			{
				_searchPane.ShowPane();
			}, Array.Empty<object>());
		};
		_winHook.WinATapped += delegate
		{
			((DispatcherObject)this).Dispatcher.BeginInvoke((Delegate)(Action)delegate
			{
				ActionCenter.Toggle();
			}, Array.Empty<object>());
		};
		_winHook.WinSnapLeft += delegate
		{
			((DispatcherObject)this).Dispatcher.BeginInvoke((Delegate)(Action)delegate
			{
				SnapService.SnapForeground(left: true);
			}, Array.Empty<object>());
		};
		_winHook.WinSnapRight += delegate
		{
			((DispatcherObject)this).Dispatcher.BeginInvoke((Delegate)(Action)delegate
			{
				SnapService.SnapForeground(left: false);
			}, Array.Empty<object>());
		};
		_winHook.WinMoveLeft += delegate
		{
			((DispatcherObject)this).Dispatcher.BeginInvoke((Delegate)(Action)delegate
			{
				SnapService.MoveForegroundToNeighbor(left: true);
			}, Array.Empty<object>());
		};
		_winHook.WinMoveRight += delegate
		{
			((DispatcherObject)this).Dispatcher.BeginInvoke((Delegate)(Action)delegate
			{
				SnapService.MoveForegroundToNeighbor(left: false);
			}, Array.Empty<object>());
		};
		_winHook.StartButtonClicked += delegate
		{
			((DispatcherObject)this).Dispatcher.BeginInvoke((Delegate)new Action(ToggleStart), Array.Empty<object>());
		};
		_winHook.VolumeUp += delegate
		{
			AdjustVolume(2);
		};
		_winHook.VolumeDown += delegate
		{
			AdjustVolume(-2);
		};
		_winHook.VolumeMute += delegate
		{
			Task.Run(delegate
			{
				try
				{
					_osdAudio.ToggleMute();
					bool muted = _osdAudio.GetMute();
					int pct = (int)Math.Round(_osdAudio.GetVolume() * 100f);
					((DispatcherObject)this).Dispatcher.BeginInvoke((Delegate)(Action)delegate
					{
						OsdService.ShowVolume(pct, muted);
					}, Array.Empty<object>());
				}
				catch (Exception ex7)
				{
					Logger.Log("Volume mute: " + ex7.Message);
				}
			});
		};
		// Audio endpoint init does a full COM activation (CoCreateInstance MMDeviceEnumerator -> GetDefaultAudioEndpoint
		// -> Activate IAudioEndpointVolume) which can stall on cold boot while audiosrv is still starting. Off the
		// first-paint path; volume keys arm a beat later (HandleVolumeKeys is volatile — imperceptible).
		Task.Run(delegate
		{
			try
			{
				_osdAudio.GetVolume();
				_winHook.HandleVolumeKeys = true;
			}
			catch (Exception ex4)
			{
				Logger.Log("Volume keys left native (audio init failed): " + ex4.Message);
			}
		});
		_winHook.GlobalLeftDown += delegate(int x, int y)
		{
			((DispatcherObject)this).Dispatcher.BeginInvoke((Delegate)(Action)delegate
			{
				_taskbar?.CloseFlyoutsOutside(x, y);
				TaskbarWindow.CloseContextMenuOnOutsideClick(x, y);
				ActionCenter.CloseOnOutsideClick(x, y);
			}, Array.Empty<object>());
		};
		_winHook.GlobalRightDown += delegate(int x, int y)
		{
			((DispatcherObject)this).Dispatcher.BeginInvoke((Delegate)(Action)delegate
			{
				_taskbar?.CloseFlyoutsOutside(x, y);
				TaskbarWindow.CloseContextMenuOnOutsideClick(x, y);
				ActionCenter.CloseOnOutsideClick(x, y);
			}, Array.Empty<object>());
		};
		_winHook.GlobalMouseWheel += delegate(int x, int y, int delta)
		{
			return _taskbar?.TryRouteGlobalFlyoutMouseWheel(x, y, delta) == true;
		};
		_winHook.DesktopRightClick += delegate(int x, int y)
		{
			((DispatcherObject)this).Dispatcher.BeginInvoke((Delegate)(Action)delegate
			{
				_taskbar?.ShowDesktopMenu(x, y);
			}, Array.Empty<object>());
		};
		// Deliberate right-edge -> left DRAG opens Charms (replacing the removed hot corners): it arms only on a
		// mouse-down inside the thin OUTER-right edge zone, tracks the pointer progressively, and completes/cancels on
		// release. Disabled under a true-fullscreen app. Win+C (above) still toggles it instantly.
		if (!_safeMode)
		{
		try
		{
		_charmsGesture = new CharmsEdgeGesture(_winHook)
		{
			OnProgress = delegate(double reveal)
			{
				((DispatcherObject)this).Dispatcher.BeginInvoke((Delegate)(Action)delegate { _charmsBar?.TrackReveal(reveal); }, Array.Empty<object>());
			},
			OnCommit = delegate(bool open)
			{
				((DispatcherObject)this).Dispatcher.BeginInvoke((Delegate)(Action)delegate { _charmsBar?.FinishReveal(open); }, Array.Empty<object>());
			},
			IsBlocked = () => TaskbarWindow.AnyFullscreenForeground()
		};
		}
		catch (Exception exGesture) { Logger.Log("Charms edge gesture init failed: " + exGesture.Message); }
		}
		// One-shot working-set trim ~12s after startup, once the boot-time decode spike (icons, tiles, wallpaper) has
		// settled — returns that transient RAM to the OS so the idle Task-Manager footprint reflects steady state, not
		// the boot peak. Recurring reclaim after heavy surfaces is handled event-driven (StartScreen.ScheduleIdleTrim).
		DispatcherTimer bootTrim = new DispatcherTimer((DispatcherPriority)4)
		{
			Interval = TimeSpan.FromSeconds(12.0)
		};
		bootTrim.Tick += delegate
		{
			bootTrim.Stop();
			try
			{
				GC.Collect(2, GCCollectionMode.Optimized, blocking: false);
				NativeShell.TrimSelf();
			}
			catch
			{
			}
		};
		bootTrim.Start();
		// Pre-warm our Start screen off the first-open critical path. A few seconds after boot — once the taskbar and
		// first paint are up — force one off-screen layout pass so the FIRST real Start open is instant instead of
		// paying WPF's first measure/arrange of the whole tile board + the initial tile-icon decode on the user's
		// click (this is why the first open used to lag the already-warm taskbar). Background priority; never shown.
		DispatcherTimer startWarm = new DispatcherTimer((DispatcherPriority)4)
		{
			Interval = TimeSpan.FromSeconds(3.5)
		};
		startWarm.Tick += delegate
		{
			startWarm.Stop();
			try
			{
				_startScreen?.Prewarm();
			}
			catch
			{
			}
		};
		startWarm.Start();
		// Safe-mode never-stuck guarantee: after ~90s of sustained health (the shell timer has been stamping the
		// heartbeat every second the whole time = every subsystem initialized without crashing), clear the unhealthy-boot
		// counter so a one-off bad boot can never keep the shell in safe mode. If the process dies first, the counter is
		// simply left to escalate (correct). One-shot, Background priority, guarded.
		DispatcherTimer healthClear = new DispatcherTimer((DispatcherPriority)4)
		{
			Interval = TimeSpan.FromSeconds(90.0)
		};
		healthClear.Tick += delegate
		{
			healthClear.Stop();
			try
			{
				ClearUnhealthyBoots();
				if (_safeMode)
				{
					Logger.Log("[safe-mode] shell healthy for 90s; unhealthy-boot counter cleared (next boot returns to full mode).");
				}
			}
			catch
			{
			}
		};
		healthClear.Start();
		// Best-effort self-heal: re-assert Fast Startup OFF at boot so "Shut down" always does a real full power-off
		// (not a hybrid shutdown that behaves like sleep on this box). Succeeds only when elevated (logon task runs
		// HighestAvailable); a manual non-elevated launch no-ops. Background, guarded, only writes if not already 0.
		Task.Run((Action)PowerActions.EnsureFastStartupDisabled);
		if (settings.ReplaceDesktopMenu)
		{
			Task.Run((Action)DesktopHit.Warm);
		}
		// Defer SnapAssist (a full WPF Window + WinEvent hook + a registry write) off the first-paint path; it's only
		// needed when the user snaps a window. Background priority runs it right after paint; only assigned here and
		// disposed on exit, so nothing dereferences it in between.
		// 2026-09-07 (user): DISABLED. The launcher must not overlay or manage other apps' windows. The custom Snap
		// system (SnapAssist panel drawn over the empty half + SnapWatcher WinEvent hook watching every window move +
		// its native-SnapAssist registry override) is exactly the kind of DWM/window feature that acted unbidden and
		// drew a panel over a maximized window. It is removed; Windows' OWN native snap (and native snap assist) is
		// used untouched. Reversible: restore the SnapAssist/SnapWatcher construction to bring the custom snap back.
		((DispatcherObject)this).Dispatcher.BeginInvoke((Delegate)(Action)delegate
		{
			try { SnapWatcher.EnsureNativeSnapAssistRestored(); }
			catch (Exception ex5) { Logger.Log("Native snap-assist restore failed: " + ex5.Message); }
		}, DispatcherPriority.Background, Array.Empty<object>());
		if (settings.RouteNotifications)
		{
			try
			{
				_notifRouter = new NotificationRouter();
				_notifRouter.Start();
			}
			catch (Exception ex6)
			{
				Logger.Log("Notification router init failed: " + ex6.Message);
			}
		}
		// Custom Win8.1 system icons are DISABLED (user directive): they kept fighting Windows' icon cache, so we always
		// keep the NATIVE This PC / Recycle Bin icons regardless of the ReplaceSystemIcons setting (the setting/profile
		// layer kept flipping it back on). Restore() is change-detected — it no-ops once already native, so it never
		// wipes the icon cache on a normal boot. RE-ENABLED 2026-09-04 with AUTHENTIC imageres icons (This PC / Recycle Bin),
		// honoring the ReplaceSystemIcons setting. Apply is change-detected: it does a ONE-TIME icon-cache refresh when the
		// authentic icons first apply, then no-ops on later boots (so it won't re-nuke the cache every boot).
		Task.Run((Action)(() => { try { if (SettingsStore.Load().ReplaceSystemIcons) { SystemIcons81.Apply(); RecycleBinWatcher.Start(); } else { SystemIcons81.Restore(); } } catch (Exception ex) { Logger.Log("[sysicons] boot: " + ex.Message); } }));
		// Win11: become the real shell tray host so third-party tray icons carry their owner window + callback message
		// and their OWN menus work (no-op on Win10/8.1, which keep the classic ToolbarWindow32 path). Hands the tray
		// back to explorer on exit.
		// Fault isolation: both run synchronously in OnStartup, so a tray-takeover or icon-factory fault would crash the
		// whole shell before the first heartbeat. Contain it — the tray is non-core; taskbar/Start keep working without it.
		try
		{
			if (!_safeMode) { TrayHostService.Start(); }   // safe mode: leave the native tray host in place
			_trayIcon = new NotifyIcon
			{
				Icon = TrayIconFactory.Win81Flag(32, TrayMetro.Accent()),
				Text = "Win81 Experience Layer",
				Visible = true
			};
			// Re-tint the tray flag to the Start-menu accent whenever the theme changes (sync with Start).
			TaskbarWindow.AccentChanged += delegate
			{
				((DispatcherObject)this).Dispatcher.BeginInvoke((Delegate)(Action)delegate
				{
					try
					{
						if (_trayIcon != null)
						{
							System.Drawing.Icon prev = _trayIcon.Icon;
							_trayIcon.Icon = TrayIconFactory.Win81Flag(32, TrayMetro.Accent());
							prev?.Dispose();
						}
					}
					catch (Exception exRt) { Logger.Log("Tray icon re-tint failed: " + exRt.Message); }
				}, DispatcherPriority.Background, Array.Empty<object>());
			};
		}
		catch (Exception exTray)
		{
			Logger.Log("Tray host/icon init failed (tray menu unavailable, shell continues): " + exTray.Message);
		}
		// These three submenu roots are hoisted to method scope because the AutoLockItem/ColorItem/MakeDensity local
		// functions capture them; the deferred build closure below assigns them.
		ToolStripMenuItem autoLockMenu = null!;
		ToolStripMenuItem colorMenu = null!;
		ToolStripMenuItem tileSize = null!;
		// Suppress native Start/Search a beat sooner: post it as its own Background op queued BEFORE the ~680-line menu
		// build below (it used to sit at that closure's TAIL, so native stayed live until the whole menu finished).
		// Idempotent + re-asserted by the 4s/12s dominance backstops; resume-on-exit safety net untouched.
		if (settings.SuspendNativeStart)
		{
			((DispatcherObject)this).Dispatcher.BeginInvoke((Delegate)(Action)delegate
			{
				try { NativeShell.SuspendShellHosts(); } catch { }
			}, DispatcherPriority.Background, Array.Empty<object>());
		}
		// Defer the ~680-line tray menu build off the synchronous startup path — taskbar first-paint is gated behind
		// OnStartup returning, and this menu is only needed on right-click. Background priority runs it right after paint.
		((DispatcherObject)this).Dispatcher.BeginInvoke((Delegate)(Action)delegate
		{
		ContextMenuStrip menu = new ContextMenuStrip();
		// 8.1 Metro look, synced to the Start-menu theme: dark #1E1E1E surface, white text, accent selection bar,
		// 8.1 chevron submenu arrows (renderer reads StartAccent live, so it follows the theme). The tray menu is the
		// only WinForms ToolStrip in this WPF app, so setting the global ToolStripManager.Renderer is safe and is what
		// makes SUBMENUS (which default to ManagerRenderMode) inherit the Metro style too.
		Win81TrayMenuRenderer metroRenderer = new Win81TrayMenuRenderer();
		ToolStripManager.Renderer = metroRenderer;
		menu.Renderer = metroRenderer;
		menu.BackColor = TrayMetro.Bg;
		menu.ForeColor = TrayMetro.Fg;
		menu.ShowImageMargin = true;   // keep the gutter so checkmarks/radios on the toggle items still render
		menu.Font = new System.Drawing.Font("Segoe UI", 9.75f);
		menu.Opening += delegate
		{
			ForegroundContext.Capture();
		};
		menu.Items.Add("Open Start (Win)", null, delegate
		{
			((DispatcherObject)this).Dispatcher.BeginInvoke((Delegate)new Action(ToggleStart), Array.Empty<object>());
		});
		menu.Items.Add("Open Charms (Win+C)", null, delegate
		{
			((DispatcherObject)this).Dispatcher.BeginInvoke((Delegate)(Action)delegate
			{
				_charmsBar?.ToggleCharms();
			}, Array.Empty<object>());
		});
		menu.Items.Add("Lock screen (Win8.1)", null, delegate
		{
			((DispatcherObject)this).Dispatcher.BeginInvoke((Delegate)new Action(LockScreen.Show), Array.Empty<object>());
		});
		_idleAutoLock = new IdleAutoLock();
		_idleAutoLock.SetMinutes(settings.AutoLockMinutes);
		autoLockMenu = new ToolStripMenuItem("Auto-lock (Win8.1)");
		autoLockMenu.DropDownItems.Add(AutoLockItem("Off", 0));
		autoLockMenu.DropDownItems.Add(AutoLockItem("After 5 minutes", 5));
		autoLockMenu.DropDownItems.Add(AutoLockItem("After 10 minutes", 10));
		autoLockMenu.DropDownItems.Add(AutoLockItem("After 15 minutes", 15));
		menu.Items.Add(autoLockMenu);
		ToolStripMenuItem cornersItem = new ToolStripMenuItem("Hot corners")
		{
			Checked = settings.HotCornersEnabled,
			CheckOnClick = true
		};
		cornersItem.CheckedChanged += delegate
		{
			_hotCorners.Enabled = cornersItem.Checked;
			SettingsStore.Update(delegate(AppSettings appSettings)
			{
				appSettings.HotCornersEnabled = cornersItem.Checked;
			});
		};
		menu.Items.Add(cornersItem);
		ToolStripMenuItem replaceStartItem = new ToolStripMenuItem("Replace Start button")
		{
			Checked = settings.ReplaceStartMenu,
			CheckOnClick = true
		};
		replaceStartItem.CheckedChanged += delegate
		{
			_winHook.ReplaceStartButton = replaceStartItem.Checked;
			SettingsStore.Update(delegate(AppSettings appSettings)
			{
				appSettings.ReplaceStartMenu = replaceStartItem.Checked;
			});
		};
		menu.Items.Add(replaceStartItem);
		ToolStripMenuItem suspendNativeItem = new ToolStripMenuItem("Suspend native Start host")
		{
			Checked = settings.SuspendNativeStart,
			CheckOnClick = true,
			ToolTipText = "Freezes StartMenuExperienceHost while we run. Always resumed on exit."
		};
		suspendNativeItem.CheckedChanged += delegate
		{
			SettingsStore.Update(delegate(AppSettings appSettings)
			{
				appSettings.SuspendNativeStart = suspendNativeItem.Checked;
			});
			if (suspendNativeItem.Checked)
			{
				NativeShell.SuspendShellHosts();
			}
			else
			{
				NativeShell.ResumeShellHosts();
			}
		};
		menu.Items.Add(suspendNativeItem);
		ToolStripMenuItem taskbarItem = new ToolStripMenuItem("Taskbar (Win8.1)")
		{
			Checked = settings.TaskbarEnabled,
			CheckOnClick = true,
			ToolTipText = "Replaces the primary taskbar. Native is restored on exit / toggle off."
		};
		taskbarItem.CheckedChanged += delegate
		{
			SettingsStore.Update(delegate(AppSettings appSettings)
			{
				appSettings.TaskbarEnabled = taskbarItem.Checked;
			});
			if (taskbarItem.Checked)
			{
				_taskbar.Activate();
			}
			else
			{
				_taskbar.Deactivate();
			}
		};
		menu.Items.Add(taskbarItem);
		ToolStripMenuItem appIcons81Item = new ToolStripMenuItem("Win8.1 app icons")
		{
			Checked = settings.Replace81AppIcons,
			CheckOnClick = true,
			ToolTipText = "Show authentic Win8.1 / Office 2013 icons for Calculator, Photos, Store, PC Settings, Word/Excel... on Start tiles, the all-apps list and taskbar pins. Off = native icons. Swaps live, no restart."
		};
		appIcons81Item.CheckedChanged += delegate
		{
			SettingsStore.Update(delegate(AppSettings appSettings)
			{
				appSettings.Replace81AppIcons = appIcons81Item.Checked;
			});
			AppIconOverrides.RefreshAllSurfaces();
		};
		menu.Items.Add(appIcons81Item);
		// "Native windows: 8.1 title bars" removed Ã¢â‚¬â€ the NativeChromeManager overlay was frozen/glitchy and is
		// superseded by the documented-DWM accent title bars (Desktop Composition modes) + real Aero glass via
		// DwmBlurGlass. Phase-2 consolidation RETIRED it: the 3 overlay files were removed from the build (backed up
		// to _retired-overlay-2026-08-28/); nothing references them any more.
		ToolStripMenuItem dominantItem = new ToolStripMenuItem("Dominant mode (Win8.1 takes over)")
		{
			Checked = settings.DominantMode,
			CheckOnClick = true,
			ToolTipText = "Win8.1 takes over: suspends the native Start host, replaces the taskbar, and routes Win+S to our Search. Reversible; native is restored on exit / toggle off."
		};
		dominantItem.CheckedChanged += delegate
		{
			bool on = dominantItem.Checked;
			SettingsStore.Update(delegate(AppSettings appSettings)
			{
				appSettings.DominantMode = on;
			});
			_winHook.DominantMode = on;
			_winHook.ReplaceExplorerShortcut = on;
			suspendNativeItem.Checked = on;
			taskbarItem.Checked = on;
		};
		menu.Items.Add(dominantItem);
		ToolStripMenuItem runFirstItem = new ToolStripMenuItem("Run first at boot (earlier)")
		{
			Checked = settings.RunFirstTask,
			CheckOnClick = true,
			ToolTipText = "Adds a per-user logon Scheduled Task so we start earlier at logon than the Run entry. Reversible; never a shell replacement."
		};
		bool runFirstBusy = false;
		runFirstItem.CheckedChanged += delegate
		{
			if (!runFirstBusy)
			{
				bool on = runFirstItem.Checked;
				Task.Run(delegate
				{
					bool ok = LogonTask.SetEnabled(on);
					((DispatcherObject)this).Dispatcher.BeginInvoke((Delegate)(Action)delegate
					{
						if (ok)
						{
							SettingsStore.Update(delegate(AppSettings appSettings)
							{
								appSettings.RunFirstTask = on;
							});
						}
						else
						{
							ToastService.Show(null, "Win8.1 layer", "Couldn't set the logon task", "The Scheduled Task couldn't be created - the HKCU Run autostart still works.");
							runFirstBusy = true;
							runFirstItem.Checked = !on;
							runFirstBusy = false;
						}
					}, Array.Empty<object>());
				});
			}
		};
		menu.Items.Add(runFirstItem);
		ToolStripMenuItem recoverMenu = new ToolStripMenuItem("Shell recovery");
		ToolStripMenuItem autoRecoverItem = new ToolStripMenuItem("Auto-recover shell")
		{
			Checked = settings.AutoRecoverShell,
			CheckOnClick = true,
			ToolTipText = "While our taskbar is the shell: keep re-hiding any native taskbar that reappears, and restart Explorer if it dies."
		};
		autoRecoverItem.CheckedChanged += delegate
		{
			SettingsStore.Update(delegate(AppSettings appSettings)
			{
				appSettings.AutoRecoverShell = autoRecoverItem.Checked;
			});
		};
		recoverMenu.DropDownItems.Add(autoRecoverItem);
		ToolStripMenuItem rebootItem = new ToolStripMenuItem("Force-reboot if Explorer can't recover")
		{
			Checked = settings.ForceRebootOnShellFailure,
			CheckOnClick = true,
			ToolTipText = "Last resort only: reboot the PC (30-second cancellable countdown, never twice within 10 minutes) if Explorer will not come back after repeated restarts. Requires Auto-recover shell."
		};
		rebootItem.CheckedChanged += delegate
		{
			SettingsStore.Update(delegate(AppSettings appSettings)
			{
				appSettings.ForceRebootOnShellFailure = rebootItem.Checked;
			});
		};
		recoverMenu.DropDownItems.Add(rebootItem);
		menu.Items.Add(recoverMenu);
		if (string.IsNullOrWhiteSpace(settings.ScreenshotApp))
		{
			string detected = DetectScreenshotApp();
			if (detected != null)
			{
				settings.ScreenshotApp = detected;
				SettingsStore.Update(delegate(AppSettings appSettings)
				{
					appSettings.ScreenshotApp = detected;
				});
			}
		}
		ToolStripMenuItem shotItem = new ToolStripMenuItem(ShotLabel(settings.ScreenshotApp))
		{
			ToolTipText = "The Share charm launches this tool and sends Print Screen to capture."
		};
		shotItem.Click += delegate
		{
			System.Windows.Forms.OpenFileDialog dlg = new System.Windows.Forms.OpenFileDialog
			{
				Filter = "Programs (*.exe)|*.exe|All files (*.*)|*.*",
				Title = "Choose the screenshot app for the Share charm"
			};
			try
			{
				try
				{
					if (!string.IsNullOrWhiteSpace(settings.ScreenshotApp))
					{
						dlg.InitialDirectory = Path.GetDirectoryName(settings.ScreenshotApp);
					}
				}
				catch
				{
				}
				if (dlg.ShowDialog() == DialogResult.OK)
				{
					settings.ScreenshotApp = dlg.FileName;
					SettingsStore.Update(delegate(AppSettings appSettings)
					{
						appSettings.ScreenshotApp = dlg.FileName;
					});
					shotItem.Text = ShotLabel(settings.ScreenshotApp);
				}
			}
			finally
			{
				if (dlg != null)
				{
					((IDisposable)dlg).Dispose();
				}
			}
		};
		menu.Items.Add(shotItem);
		ToolStripMenuItem syncPinsItem = new ToolStripMenuItem("Sync pins from native taskbar")
		{
			ToolTipText = "Mirror the apps currently pinned to your Windows taskbar."
		};
		syncPinsItem.Click += delegate
		{
			List<PinnedApp> list = TaskbarPins.ImportFromNative();
			if (list.Count > 0)
			{
				TaskbarPins.Save(list);
				((DispatcherObject)this).Dispatcher.BeginInvoke((Delegate)(Action)delegate
				{
					TaskbarWindow.ReloadPins();
				}, Array.Empty<object>());
			}
		};
		menu.Items.Add(syncPinsItem);
		ToolStripMenuItem bgMenu = new ToolStripMenuItem("Start background");
		ToolStripMenuItem bgDefault = new ToolStripMenuItem("Default wallpaper");
		bgDefault.Click += delegate
		{
			SetBg("default");
		};
		ToolStripMenuItem bgDesktop = new ToolStripMenuItem("Desktop wallpaper");
		bgDesktop.Click += delegate
		{
			SetBg("desktop");
		};
		ToolStripMenuItem bgCustom = new ToolStripMenuItem("Custom image...");
		bgCustom.Click += delegate
		{
			using System.Windows.Forms.OpenFileDialog openFileDialog = new System.Windows.Forms.OpenFileDialog
			{
				Filter = "Images (*.png;*.jpg;*.jpeg;*.bmp)|*.png;*.jpg;*.jpeg;*.bmp|All files (*.*)|*.*",
				Title = "Choose a Start background image"
			};
			if (openFileDialog.ShowDialog() == DialogResult.OK)
			{
				SetBg("custom", openFileDialog.FileName);
			}
		};
		ToolStripMenuItem perfItem = new ToolStripMenuItem("Performance monitor (CPU/RAM/net)")
		{
			Checked = settings.PerfMonEnabled,
			CheckOnClick = true,
			ToolTipText = "Show CPU/RAM/network telemetry on the taskbar."
		};
		perfItem.CheckedChanged += delegate
		{
			SettingsStore.Update(delegate(AppSettings appSettings)
			{
				appSettings.PerfMonEnabled = perfItem.Checked;
			});
			((DispatcherObject)this).Dispatcher.BeginInvoke((Delegate)(Action)delegate
			{
				TaskbarWindow.ReloadPerfMode();
			}, Array.Empty<object>());
		};
		menu.Items.Add(perfItem);
		ToolStripMenuItem dwmTimingItem = new ToolStripMenuItem("Capture DWM frame timing (5s)")
		{
			ToolTipText = "Phase-3 diagnostics: sample DWM composition timing from the live shell (has a real composition context) and write a report to %LocalAppData%\\Win81Layer\\dwm-diag. Requires 'DWM frame-timing telemetry' enabled in PC Settings → Desktop composition."
		};
		dwmTimingItem.Click += delegate
		{
			if (!SettingsStore.Current.EnableDwmTimingTelemetry)
			{
				Logger.Log("[dwmperf] skipped — EnableDwmTimingTelemetry is off");
				return;
			}
			((DispatcherObject)this).Dispatcher.BeginInvoke((Delegate)(Action)delegate
			{
				nint h = ShellHwndForDiagnostics();
				System.Threading.Tasks.Task.Run((Action)delegate
				{
					try { DwmDiagnostics.FrameTimingBurst(h, 300, 16); }
					catch (Exception ex) { Logger.Log("[dwmperf] failed: " + ex.Message); }
				});
			}, Array.Empty<object>());
		};
		menu.Items.Add(dwmTimingItem);
		ToolStripMenuItem soundsItem = new ToolStripMenuItem("Win8.1 sounds")
		{
			Checked = settings.UseWin81Sounds,
			CheckOnClick = true,
			ToolTipText = "Use the authentic Windows 8.1 system sounds (fully reversible)."
		};
		soundsItem.CheckedChanged += delegate
		{
			bool on = soundsItem.Checked;
			SettingsStore.Update(delegate(AppSettings appSettings) { appSettings.UseWin81Sounds = on; });
			if (on)
			{
				SoundScheme.Apply();
			}
			else
			{
				SoundScheme.Revert();
			}
		};
		menu.Items.Add(soundsItem);
		ToolStripMenuItem cursorsItem = new ToolStripMenuItem("Win8.1 cursors")
		{
			Checked = settings.UseWin81Cursors,
			CheckOnClick = true,
			ToolTipText = "Apply the authentic Windows 8.1 pointer scheme (fully reversible; also selectable in Mouse Properties)."
		};
		cursorsItem.CheckedChanged += delegate
		{
			bool on = cursorsItem.Checked;
			SettingsStore.Update(delegate(AppSettings appSettings) { appSettings.UseWin81Cursors = on; });
			if (on)
			{
				CursorScheme.Apply();
			}
			else
			{
				CursorScheme.Revert();
			}
		};
		menu.Items.Add(cursorsItem);
		colorMenu = new ToolStripMenuItem("Taskbar color");
		colorMenu.DropDownItems.Add(ColorItem("From desktop wallpaper", "Wallpaper"));
		colorMenu.DropDownItems.Add(ColorItem("Match Start screen", "Start"));
		colorMenu.DropDownItems.Add(ColorItem("Transparent", "Transparent"));
		menu.Items.Add(colorMenu);
		bgMenu.DropDownItems.AddRange(bgDefault, bgCustom, bgDesktop);
		bool presetsLoaded = false;
		bgMenu.DropDownOpening += delegate
		{
			if (!presetsLoaded)
			{
				presetsLoaded = true;
				Wallpapers.SeedFromPack();
				List<string> list = Wallpapers.Presets();
				if (list.Count != 0)
				{
					bgMenu.DropDownItems.Add(new ToolStripSeparator());
					foreach (string current in list)
					{
						string path = current;
						ToolStripMenuItem toolStripMenuItem = new ToolStripMenuItem(Path.GetFileNameWithoutExtension(current))
						{
							ImageScaling = ToolStripItemImageScaling.None
						};
						try
						{
							toolStripMenuItem.Image = MakeThumb(path);
						}
						catch
						{
						}
						toolStripMenuItem.Click += delegate
						{
							SetBg("custom", path);
						};
						bgMenu.DropDownItems.Add(toolStripMenuItem);
					}
				}
			}
		};
		menu.Items.Add(bgMenu);
		ToolStripMenuItem autostartItem = new ToolStripMenuItem("Start with Windows")
		{
			Checked = Autostart.IsEnabled(),
			CheckOnClick = true
		};
		autostartItem.CheckedChanged += delegate
		{
			Autostart.SetEnabled(autostartItem.Checked);
		};
		menu.Items.Add(autostartItem);
		ToolStripMenuItem bootItem = new ToolStripMenuItem("Open Start at login")
		{
			Checked = settings.BootToStart,
			CheckOnClick = true
		};
		bootItem.CheckedChanged += delegate
		{
			SettingsStore.Update(delegate(AppSettings appSettings)
			{
				appSettings.BootToStart = bootItem.Checked;
			});
		};
		menu.Items.Add(bootItem);
		ToolStripMenuItem personalization = new ToolStripMenuItem("Personalization (Win8.1)");
		ToolStripMenuItem wallpaperItem = new ToolStripMenuItem("Desktop wallpaper")
		{
			Checked = settings.Win81Wallpaper,
			CheckOnClick = true,
			Enabled = WallpaperModule.AssetAvailable
		};
		wallpaperItem.CheckedChanged += delegate
		{
			if (wallpaperItem.Checked ? WallpaperModule.Apply() : WallpaperModule.Revert())
			{
				SettingsStore.Update(delegate(AppSettings appSettings)
				{
					appSettings.Win81Wallpaper = wallpaperItem.Checked;
				});
			}
			else
			{
				wallpaperItem.Checked = settings.Win81Wallpaper;
			}
		};
		personalization.DropDownItems.Add(wallpaperItem);
		ToolStripMenuItem cursorsInfo = new ToolStripMenuItem("Cursors: extracted (module on request)")
		{
			Enabled = false
		};
		ToolStripMenuItem soundsInfo = new ToolStripMenuItem("Sounds: extracted (module on request)")
		{
			Enabled = false
		};
		personalization.DropDownItems.Add(cursorsInfo);
		personalization.DropDownItems.Add(soundsInfo);
		ToolStripMenuItem deskComp = new ToolStripMenuItem("Desktop composition");
		foreach (var (Label, Id) in CompositionProfiles.MenuModes)   // the 4 canonical modes, single source of truth
		{
			deskComp.DropDownItems.Add(ModeItem(Label, Id));
		}
		deskComp.DropDownItems.Add(new ToolStripSeparator());
		ToolStripMenuItem previewSub = new ToolStripMenuItem("Preview (10s)");
		foreach (var (Label, Id) in CompositionProfiles.MenuModes)
		{
			string pid = Id;
			previewSub.DropDownItems.Add(Label, null, delegate
			{
				DesktopComposition.PreviewMode(pid, 10);
			});
		}
		deskComp.DropDownItems.Add(previewSub);
		deskComp.DropDownItems.Add("Restore previous", null, delegate
		{
			DesktopComposition.RestorePrevious();
		});
		deskComp.DropDownItems.Add("Restore Windows default", null, delegate
		{
			DesktopComposition.RestoreWindowsDefault();
		});
		deskComp.DropDownOpening += delegate
		{
			string cur = CompositionProfiles.Resolve(DesktopComposition.Mode).Id;   // live check marks
			foreach (ToolStripItem tsi in deskComp.DropDownItems)
			{
				if (tsi is ToolStripMenuItem mi && mi.Tag is string tid)
				{
					mi.Checked = string.Equals(CompositionProfiles.Resolve(tid).Id, cur, StringComparison.OrdinalIgnoreCase);
				}
			}
		};
		personalization.DropDownItems.Add(deskComp);
		menu.Items.Add(personalization);
		// Removed: "Files (Win8.1)" (own FileBrowser) and "Win8.1 window (sample)" (demo) Ã¢â‚¬â€ unused / superseded.
		tileSize = new ToolStripMenuItem("Tile size");
		tileSize.DropDownItems.Add(MakeDensity("Comfortable", 1.0));
		tileSize.DropDownItems.Add(MakeDensity("Compact", 0.8));
		menu.Items.Add(tileSize);
		ToolStripMenuItem liveTiles = new ToolStripMenuItem("Add live tile");
		liveTiles.DropDownItems.Add("Clock", null, delegate
		{
			((DispatcherObject)this).Dispatcher.BeginInvoke((Delegate)(Action)delegate
			{
				_startScreen.AddLiveTile(LiveKind.Clock);
			}, Array.Empty<object>());
		});
		liveTiles.DropDownItems.Add("Calendar", null, delegate
		{
			((DispatcherObject)this).Dispatcher.BeginInvoke((Delegate)(Action)delegate
			{
				_startScreen.AddLiveTile(LiveKind.Calendar);
			}, Array.Empty<object>());
		});
		liveTiles.DropDownItems.Add("Weather", null, delegate
		{
			((DispatcherObject)this).Dispatcher.BeginInvoke((Delegate)(Action)delegate
			{
				_startScreen.AddLiveTile(LiveKind.Weather);
			}, Array.Empty<object>());
		});
		liveTiles.DropDownItems.Add("Photos", null, delegate
		{
			((DispatcherObject)this).Dispatcher.BeginInvoke((Delegate)(Action)delegate
			{
				_startScreen.AddLiveTile(LiveKind.Photo);
			}, Array.Empty<object>());
		});
		liveTiles.DropDownItems.Add("Mail", null, delegate
		{
			((DispatcherObject)this).Dispatcher.BeginInvoke((Delegate)(Action)delegate
			{
				_startScreen.AddLiveTile(LiveKind.Mail);
			}, Array.Empty<object>());
		});
		liveTiles.DropDownItems.Add("Agenda", null, delegate
		{
			((DispatcherObject)this).Dispatcher.BeginInvoke((Delegate)(Action)delegate
			{
				_startScreen.AddLiveTile(LiveKind.Agenda);
			}, Array.Empty<object>());
		});
		liveTiles.DropDownItems.Add("News", null, delegate { Dispatcher.BeginInvoke(new Action(PinNewsTile)); });
		liveTiles.DropDownItems.Add("News sources...", null, delegate { Dispatcher.BeginInvoke(new Action(OpenNews)); });
		liveTiles.DropDownItems.Add(new ToolStripSeparator());
		ToolStripMenuItem cityItem = new ToolStripMenuItem("Weather city: " + settings.WeatherCity + "...");
		cityItem.Click += delegate
		{
			string text = PromptForCity(settings.WeatherCity);
			if (!string.IsNullOrWhiteSpace(text))
			{
				settings.WeatherCity = text.Trim();
				SettingsStore.Update(delegate(AppSettings appSettings)
				{
					appSettings.WeatherCity = settings.WeatherCity;
				});
				cityItem.Text = "Weather city: " + settings.WeatherCity + "...";
				((DispatcherObject)this).Dispatcher.BeginInvoke((Delegate)(Action)delegate
				{
					_startScreen.SetWeatherCity(settings.WeatherCity);
				}, Array.Empty<object>());
			}
		};
		liveTiles.DropDownItems.Add(cityItem);
		ToolStripMenuItem weatherUnitsItem = new ToolStripMenuItem("Weather units");
		string activeWeatherUnits = string.Equals(settings.WeatherUnits, "F", StringComparison.OrdinalIgnoreCase) ? "F" : "C";
		ToolStripMenuItem celsiusItem = new ToolStripMenuItem("Celsius") { Checked = activeWeatherUnits == "C" };
		ToolStripMenuItem fahrenheitItem = new ToolStripMenuItem("Fahrenheit") { Checked = activeWeatherUnits == "F" };
		Action<string> setWeatherUnits = delegate(string units)
		{
			settings.WeatherUnits = units;
			celsiusItem.Checked = units == "C";
			fahrenheitItem.Checked = units == "F";
			SettingsStore.Update(delegate(AppSettings appSettings) { appSettings.WeatherUnits = units; });
			((DispatcherObject)this).Dispatcher.BeginInvoke((Delegate)(Action)delegate
			{
				_startScreen.SetWeatherUnits(units);
			}, Array.Empty<object>());
		};
		celsiusItem.Click += delegate { setWeatherUnits("C"); };
		fahrenheitItem.Click += delegate { setWeatherUnits("F"); };
		weatherUnitsItem.DropDownItems.Add(celsiusItem);
		weatherUnitsItem.DropDownItems.Add(fahrenheitItem);
		liveTiles.DropDownItems.Add(weatherUnitsItem);
		menu.Items.Add(liveTiles);
		ToolStripMenuItem googleMenu = new ToolStripMenuItem("Google account (Mail/Agenda)");
		ToolStripMenuItem gStatus = new ToolStripMenuItem("(status)")
		{
			Enabled = false
		};
		ToolStripMenuItem gSignIn = new ToolStripMenuItem("Connect Google Calendar...");
		ToolStripMenuItem gSignOut = new ToolStripMenuItem("Sign out");
		ToolStripMenuItem gCreds = new ToolStripMenuItem("Manage Calendar account...");
		googleMenu.DropDownItems.AddRange(gStatus, new ToolStripSeparator(), gSignIn, gSignOut, new ToolStripSeparator(), gCreds);
		googleMenu.DropDownOpening += delegate
		{
			bool isConfigured = GoogleAuth.IsConfigured;
			bool isSignedIn = GoogleAuth.IsSignedIn;
			gStatus.Text = ((!isConfigured) ? "Not set up - add OAuth credentials first" : (isSignedIn ? "Signed in" : "Not signed in"));
			gSignIn.Enabled = !GoogleAuth.HasCalendarAccess;
			gSignOut.Enabled = isSignedIn;
		};
		gSignIn.Click += delegate
		{
			GoogleCalendarConnectWindow.ShowFor();
		};
		gSignOut.Click += delegate
		{
			try
			{
				GoogleAuth.SignOut();
				ToastService.Show(null, "Google", "Signed out", "The local Google connection was removed.");
			}
			catch (Exception ex)
			{
				Logger.Log("Google disconnect failed: " + ex.GetType().Name);
				GoogleCalendarConnectWindow.ShowFor();
				ToastService.Show(null, "Google", "Could not disconnect", "The saved connection could not be removed. Manage the account to try again.");
			}
		};
		gCreds.Click += delegate
		{
			GoogleCalendarConnectWindow.ShowFor();
		};
		menu.Items.Add(googleMenu);
		ToolStripMenuItem motionItem = new ToolStripMenuItem("Motion");
		string[] array2 = new string[4] { "Authentic", "Fast", "Reduced", "Off" };
		foreach (string m in array2)
		{
			ToolStripMenuItem mi = new ToolStripMenuItem(m)
			{
				Checked = (settings.MotionMode == m)
			};
			string mode = m;
			mi.Click += delegate
			{
				Motion.Mode = Motion.Parse(mode);
				SettingsStore.Update(delegate(AppSettings appSettings)
				{
					appSettings.MotionMode = mode;
				});
				foreach (ToolStripMenuItem toolStripMenuItem in motionItem.DropDownItems)
				{
					toolStripMenuItem.Checked = toolStripMenuItem.Text == mode;
				}
			};
			motionItem.DropDownItems.Add(mi);
		}
		menu.Items.Add(motionItem);
		ToolStripMenuItem packedItem = new ToolStripMenuItem("Packed grid (auto-arrange)")
		{
			Checked = settings.PackedGrid,
			CheckOnClick = true
		};
		packedItem.Click += delegate
		{
			TilePanel.Packed = packedItem.Checked;
			SettingsStore.Update(delegate(AppSettings appSettings)
			{
				appSettings.PackedGrid = packedItem.Checked;
			});
			((DispatcherObject)this).Dispatcher.BeginInvoke((Delegate)(Action)delegate
			{
				_startScreen.RepackTiles();
			}, Array.Empty<object>());
		};
		menu.Items.Add(packedItem);
		_startScreen.SetWeatherCity(settings.WeatherCity);
		_startScreen.SetWeatherUnits(settings.WeatherUnits);
		menu.Items.Add(new ToolStripSeparator());
		menu.Items.Add("Restart Explorer", null, delegate
		{
			ShellRestart.RestartExplorer();
		});
		menu.Items.Add("Exit", null, delegate
		{
			((DispatcherObject)this).Dispatcher.BeginInvoke((Delegate)new Action(ExitApp), Array.Empty<object>());
		});
		if (_trayIcon != null) { _trayIcon.ContextMenuStrip = menu; }
		}, (DispatcherPriority)4, Array.Empty<object>());
		// _trayIcon may be null if the tray-host/NotifyIcon init above was caught (fault isolation). Guard the
		// synchronous handler wire-up so a failed tray never crashes the OnStartup body (which runs pre-dispatcher,
		// where an NRE would terminate the process instead of degrading).
		if (_trayIcon != null)
		{
			_trayIcon.DoubleClick += delegate
			{
				((DispatcherObject)this).Dispatcher.BeginInvoke((Delegate)new Action(ToggleStart), Array.Empty<object>());
			};
		}
		_idleTrimTimer = new DispatcherTimer((DispatcherPriority)2)
		{
			Interval = TimeSpan.FromMilliseconds(45000L)
		};
		_idleTrimTimer.Tick += delegate
		{
			_idleTrimTimer.Stop();
			try
			{
			StartScreen? startScreen = _startScreen;
			if (startScreen == null || !startScreen.IsVisible)
			{
				CharmsBar? charmsBar = _charmsBar;
				if (charmsBar == null || !charmsBar.IsVisible)
				{
					bool releasedApps = startScreen?.ReleaseIdleResources() == true;
					if (releasedApps)
					{
						GC.Collect(2, GCCollectionMode.Optimized, blocking: false, compacting: false);
					}
					NativeShell.TrimHosts();
				}
			}
			}
			catch (Exception exTrim)
			{
				Logger.Log("idle trim failed (best-effort, skipped one cycle): " + exTrim.Message);
			}
		};
		_startScreen.IsVisibleChanged += (DependencyPropertyChangedEventHandler)delegate
		{
			ScheduleIdleTrim();
		};
		_startScreen.IsVisibleChanged += (DependencyPropertyChangedEventHandler)delegate
		{
			TaskbarWindow.RaiseStartOpen(_startScreen.IsVisible);
			// Gate the global Start-button click hook while Metro Start is open so the Apps down-arrow (and tiles)
			// in the bottom-left are never swallowed as a Start toggle back to Desktop.
			if (_winHook != null)
			{
				_winHook.StartOpen = _startScreen.IsVisible;
			}
		};
		// _charmsBar may be null if the panes/charms ctor was caught (fault isolation). Guard the synchronous
		// IsVisibleChanged wire-ups so a failed charms bar degrades instead of crashing the pre-dispatcher boot body.
		if (_charmsBar != null)
		{
			_charmsBar.IsVisibleChanged += (DependencyPropertyChangedEventHandler)delegate
			{
				ScheduleIdleTrim();
			};
			_charmsBar.IsVisibleChanged += (DependencyPropertyChangedEventHandler)delegate
			{
				TaskbarWindow.RaiseCharmsOpen(_charmsBar.IsVisible);
			};
		}
		if (e.Args.Contains("--start"))
		{
			((DispatcherObject)this).Dispatcher.BeginInvoke((Delegate)(Action)delegate
			{
				_startScreen.QaKeepOpen = true;
				_startScreen.ShowStart();
			}, (DispatcherPriority)6, Array.Empty<object>());
		}
		else if (e.Args.Contains("--apps"))
		{
			((DispatcherObject)this).Dispatcher.BeginInvoke((Delegate)(Action)delegate
			{
				_startScreen.QaKeepOpen = true;
				_startScreen.ShowAppsList();
			}, (DispatcherPriority)6, Array.Empty<object>());
		}
		else if (e.Args.Contains("--speedtest"))
		{
			int n3 = 0;
			DispatcherTimer t = new DispatcherTimer
			{
				Interval = TimeSpan.FromMilliseconds(2000L)
			};
			t.Tick += delegate
			{
				if (_startScreen.IsVisible)
				{
					_startScreen.HideStart();
				}
				else
				{
					WinKeyHook.LastTriggerTs = Stopwatch.GetTimestamp();
					ToggleStart();
				}
				if (++n3 > 8)
				{
					t.Stop();
					Shutdown();
				}
			};
			t.Start();
		}
		else if (e.Args.Contains("--show") || (e.Args.Contains("--autostart") && settings.BootToStart))
		{
			WinKeyHook.LastTriggerTs = Stopwatch.GetTimestamp();
			((DispatcherObject)this).Dispatcher.BeginInvoke((Delegate)new Action(ToggleStart), Array.Empty<object>());
		}
		else
		{
			ScheduleIdleTrim();
		}
		Dispatcher.BeginInvoke(new Action(ActionCenter.Prewarm), DispatcherPriority.ApplicationIdle);
		ToolStripMenuItem AutoLockItem(string text, int minutes)
		{
			ToolStripMenuItem it2 = new ToolStripMenuItem(text)
			{
				Checked = (settings.AutoLockMinutes == minutes)
			};
			it2.Click += delegate
			{
				foreach (ToolStripMenuItem toolStripMenuItem in autoLockMenu.DropDownItems)
				{
					toolStripMenuItem.Checked = false;
				}
				it2.Checked = true;
				settings.AutoLockMinutes = minutes;
				SettingsStore.Update(delegate(AppSettings appSettings)
				{
					appSettings.AutoLockMinutes = minutes;
				});
				_idleAutoLock.SetMinutes(minutes);
			};
			return it2;
		}
		ToolStripMenuItem ColorItem(string text, string text2)
		{
			ToolStripMenuItem it2 = new ToolStripMenuItem(text)
			{
				Checked = (settings.TaskbarColorMode == text2)
			};
			it2.Click += delegate
			{
				foreach (ToolStripMenuItem toolStripMenuItem in colorMenu.DropDownItems)
				{
					toolStripMenuItem.Checked = false;
				}
				it2.Checked = true;
				SettingsStore.Update(delegate(AppSettings appSettings)
				{
					appSettings.TaskbarColorMode = text2;
				});
				((DispatcherObject)this).Dispatcher.BeginInvoke((Delegate)new Action(TaskbarWindow.RaiseTaskbarColorChanged), Array.Empty<object>());
			};
			return it2;
		}
		ToolStripMenuItem MakeDensity(string text, double scale)
		{
			ToolStripMenuItem item2 = new ToolStripMenuItem(text)
			{
				Checked = (Math.Abs(settings.TileScale - scale) < 0.01)
			};
			item2.Click += delegate
			{
				foreach (ToolStripMenuItem toolStripMenuItem in tileSize.DropDownItems)
				{
					toolStripMenuItem.Checked = false;
				}
				item2.Checked = true;
				SettingsStore.Update(delegate(AppSettings appSettings)
				{
					appSettings.TileScale = scale;
				});
				((DispatcherObject)this).Dispatcher.BeginInvoke((Delegate)(Action)delegate
				{
					_startScreen.SetTileDensity(scale);
				}, Array.Empty<object>());
			};
			return item2;
		}
		ToolStripMenuItem ModeItem(string text, string text2)
		{
			ToolStripMenuItem it2 = new ToolStripMenuItem(text)
			{
				Tag = text2,   // used by deskComp.DropDownOpening to recompute the checkmark live
				Checked = (CompositionProfiles.Resolve(DesktopComposition.Mode).Id == CompositionProfiles.Resolve(text2).Id)
			};
			it2.Click += delegate
			{
				DesktopComposition.SetMode(text2);   // check marks refresh on next DropDownOpening
			};
			return it2;
		}
		static void Open(string file, string? args = null)
		{
			try
			{
				Process.Start(new ProcessStartInfo(file, args ?? "")
				{
					UseShellExecute = true
				});
			}
			catch (Exception ex7)
			{
				Logger.Log("open " + file + ": " + ex7.Message);
			}
		}
		void SetBg(string startBgMode, string? customPath = null)
		{
			SettingsStore.Update(delegate(AppSettings appSettings)
			{
				appSettings.StartBgMode = startBgMode;
				if (customPath != null)
				{
					appSettings.StartBgCustomPath = customPath;
				}
			});
			((DispatcherObject)this).Dispatcher.BeginInvoke((Delegate)(Action)delegate
			{
				_startScreen.RefreshBackground();
			}, Array.Empty<object>());
		}
	}

	private void ScheduleIdleTrim()
	{
		if (_idleTrimTimer == null)
		{
			return;
		}
		_idleTrimTimer.Stop();
		StartScreen? startScreen = _startScreen;
		if (startScreen == null || !startScreen.IsVisible)
		{
			CharmsBar? charmsBar = _charmsBar;
			if (charmsBar == null || !charmsBar.IsVisible)
			{
				_idleTrimTimer.Start();
			}
		}
	}

	private void AdjustVolume(int deltaPct)
	{
		Task.Run(delegate
		{
			try
			{
				int pct = Math.Clamp((int)Math.Round(_osdAudio.GetVolume() * 100f) + deltaPct, 0, 100);
				_osdAudio.SetVolume((float)pct / 100f);
				if (pct > 0 && _osdAudio.GetMute())
				{
					_osdAudio.SetMute(mute: false);
				}
				bool muted = _osdAudio.GetMute();
				((DispatcherObject)this).Dispatcher.BeginInvoke((Delegate)(Action)delegate
				{
					OsdService.ShowVolume(pct, muted);
				}, Array.Empty<object>());
			}
			catch (Exception ex)
			{
				Logger.Log("Volume adjust: " + ex.Message);
			}
		});
	}

	private void OpenPcSettings()
	{
		try
		{
			PcSettingsWindow existing = _pcSettings;
			if (existing != null)
			{
				try
				{
					if (existing.IsVisible)
					{
						existing.Activate();
						return;
					}
				}
				catch
				{
				}
				// A stale/broken instance (e.g. an earlier render failure that never fired Closed) would otherwise
				// block reopening â€” drop it and rebuild a fresh window.
				try { existing.Close(); } catch { }
				_pcSettings = null;
			}
			PcSettingsWindow win = new PcSettingsWindow(_startScreen);
			win.SearchRequested += delegate
			{
				_searchPane.ShowPane();
			};
			win.Closed += delegate
			{
				if (_pcSettings == win)
				{
					_pcSettings = null;
				}
			};
			_pcSettings = win;
			win.ShowFullScreen();
		}
		catch (Exception ex)
		{
			Logger.Log("OpenPcSettings failed: " + ex.Message);
			_pcSettings = null;   // reset so the next attempt starts clean
		}
	}

	private void ToggleStart()
	{
		if (_startScreen == null)
		{
			return;
		}
		int now = Environment.TickCount;
		if (now - _lastToggleTick >= 250)
		{
			_lastToggleTick = now;
			// Opt-in: route Start to the classic Win7 orb menu instead of Metro. The SINGLE choke-point — every
			// Start route (taskbar button, Win key, charms, hot-corner) funnels here, so this one branch covers all.
			// When the flag is OFF this method is byte-for-byte the original Metro behaviour (Metro stays default).
			if (SettingsStore.Current.Win7StartMenuEnabled)
			{
				if (_startScreen.IsVisible)
				{
					_startScreen.HideStart(animate: false);
				}
				_win7Start ??= new Win7StartMenu(() => _startScreen.AllApps.ToList(), a => _startScreen.LaunchApp(a));
				if (_win7Start.IsVisible)
				{
					_win7Start.Dismiss();
				}
				else
				{
					_win7Start.ShowMenu();
				}
				return;
			}
			if (_win7Start != null && _win7Start.IsVisible)
			{
				_win7Start.Dismiss();   // flag was turned off while the Win7 menu was open
			}
			if (_startScreen.IsVisible)
			{
				_startScreen.HideStart(animate: true);
			}
			else
			{
				_startScreen.ShowStart();
			}
		}
	}

	// Diagnostics accessor: the shell's own composed top-level HWND (materialised without showing Start) for the
	// Phase-3 in-shell frame-timing burst. Called on the UI thread (EnsureHandle requirement).
	public nint ShellHwndForDiagnostics()
	{
		try
		{
			return (_startScreen != null) ? new System.Windows.Interop.WindowInteropHelper(_startScreen).EnsureHandle() : 0;
		}
		catch
		{
			return 0;
		}
	}

	// Seed the bundled authentic 8.1 cursor files into the LocalAppData path CursorScheme.Resolve() searches, so the
	// pointer scheme resolves on ANY machine (not just where they were hand-placed). Idempotent; copies only missing files.
	private static void SeedWin81CursorAssets()
	{
		try
		{
			string src = Path.Combine(AppContext.BaseDirectory, "Assets", "Cursors81");
			if (!Directory.Exists(src))
			{
				return;
			}
			string dst = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Win81Layer", "assets", "win81", "cursors");
			Directory.CreateDirectory(dst);
			foreach (string f in Directory.GetFiles(src))
			{
				string target = Path.Combine(dst, Path.GetFileName(f));
				if (!File.Exists(target))
				{
					File.Copy(f, target, overwrite: false);
				}
			}
		}
		catch (Exception ex)
		{
			try { Logger.Log("SeedWin81CursorAssets: " + ex.Message); } catch { }
		}
	}

	private static Image MakeThumb(string path)
	{
		using Image src = Image.FromFile(path);
		Bitmap thumb = new Bitmap(48, 27);
		using Graphics g = Graphics.FromImage(thumb);
		g.InterpolationMode = InterpolationMode.HighQualityBicubic;
		g.DrawImage(src, 0, 0, 48, 27);
		return thumb;
	}

	private static string ShotLabel(string path)
	{
		return "Screenshot app: " + (string.IsNullOrWhiteSpace(path) ? "(Print Screen)" : Path.GetFileNameWithoutExtension(path)) + "...";
	}

	private static string? DetectScreenshotApp()
	{
		string[] candidates = new string[4]
		{
			Environment.ExpandEnvironmentVariables("%ProgramFiles(x86)%\\Skillbrains\\lightshot\\Lightshot.exe"),
			Environment.ExpandEnvironmentVariables("%ProgramFiles%\\Skillbrains\\lightshot\\Lightshot.exe"),
			Environment.ExpandEnvironmentVariables("%ProgramFiles%\\Greenshot\\Greenshot.exe"),
			Environment.ExpandEnvironmentVariables("%ProgramFiles%\\ShareX\\ShareX.exe")
		};
		string[] array = candidates;
		foreach (string c in array)
		{
			if (File.Exists(c))
			{
				return c;
			}
		}
		return null;
	}

	private static string? PromptForCity(string current)
	{
		// Windows 8 Patterns: the weather-city prompt is the Metro flyout dialog, not a WinForms box.
		return TextPrompt.Show("Weather city", current ?? "");
	}

	private void PrintForegroundApp()
	{
		nint hwnd = ForegroundContext.Hwnd;
		if (hwnd == IntPtr.Zero)
		{
			CharmListPane.Launch("ms-settings:printers");
			return;
		}
		WindowList.Activate(hwnd);
		DelayThen(280, delegate
		{
			if (WindowList.IsForeground(hwnd))
			{
				Keystroke.Chord(17, 80);
			}
			else
			{
				Logger.Log("Print: target app didn't come to the foreground; Ctrl+P suppressed");
			}
		});
	}

	private void CaptureScreenshot()
	{
		nint hwnd = ForegroundContext.Hwnd;
		DelayThen(300, delegate
		{
			Task.Run(delegate
			{
				var (path, bmp) = ScreenCapture.CaptureToFile(hwnd);
				((DispatcherObject)this).Dispatcher.BeginInvoke((Delegate)(Action)delegate
				{
					if (bmp != null)
					{
						try
						{
							System.Windows.Forms.Clipboard.SetImage(bmp);
						}
						catch (Exception ex)
						{
							Logger.Log("clipboard image: " + ex.Message);
						}
						bmp.Dispose();
					}
					if (path != null)
					{
						ToastService.Show(null, "Share", "Screen captured", "Saved as " + Path.GetFileName(path) + " - copied to clipboard.", delegate
						{
							try
							{
								Process.Start(new ProcessStartInfo(path)
								{
									UseShellExecute = true
								});
							}
							catch
							{
							}
						});
					}
					else
					{
						ToastService.Show(null, "Share", "Screenshot failed", "Couldn't capture the screen.");
					}
				}, Array.Empty<object>());
			});
		});
	}

	private void DelayThen(int ms, Action action)
	{
		//IL_000f: Unknown result type (might be due to invalid IL or missing references)
		//IL_0014: Unknown result type (might be due to invalid IL or missing references)
		//IL_0027: Expected O, but got Unknown
		DispatcherTimer t = new DispatcherTimer
		{
			Interval = TimeSpan.FromMilliseconds(ms)
		};
		t.Tick += delegate
		{
			t.Stop();
			try
			{
				action();
			}
			catch (Exception ex)
			{
				Logger.Log("DelayThen: " + ex.Message);
			}
		};
		t.Start();
	}

	private int _selfHealArmed;   // 0 until the self-heal restart is armed — armed AT MOST ONCE per process

	private string[]? _selfHealArgs;

	// Re-checkable from StartScreen AFTER Profile.Load has run. The profile-degraded / dropped-tiles case is NOT known
	// at the early OnStartup call (Profile.Load runs late in the inventory task), so this second trigger catches a
	// heartbeating shell that loaded a PARTIAL layout. Idempotent via the _selfHealArmed guard.
	public void MaybeSelfHealAfterLayout()
	{
		try { MaybeSelfHealDegradedBoot(_selfHealArgs ?? Array.Empty<string>()); } catch { }
	}

	private void MaybeSelfHealDegradedBoot(string[] args)
	{
		if (Array.IndexOf(args, "--healed") >= 0 || !AppInventory.ColdBoot() || (!Profile.LoadWasDegraded && Profile.LastDropped <= 0 && !TaskbarPins.LoadWasDegraded && !AppInventory.LastLoadDegraded))
		{
			return;
		}
		if (System.Threading.Interlocked.Exchange(ref _selfHealArmed, 1) != 0)
		{
			return;   // self-heal already armed by an earlier trigger
		}
		Logger.Log($"Cold-boot DEGRADED start (profile={Profile.LoadWasDegraded} pins={TaskbarPins.LoadWasDegraded} inventory={AppInventory.LastLoadDegraded}) - self-heal will restart once the shell/files are ready.");
		Task.Run(async delegate
		{
			for (int waited = 0; waited < 240; waited += 5)
			{
				await Task.Delay(5000);
				if (waited >= 40 && EnvironmentReady())
				{
					break;
				}
			}
			if (!EnvironmentReady())
			{
				Logger.Log("Self-heal: shell/files still not ready after 4 min - giving up (data preserved; a manual restart fixes it).");
			}
			else
			{
				string exe = Environment.ProcessPath;
				if (exe != null)
				{
					await ((DispatcherObject)this).Dispatcher.BeginInvoke((Delegate)(Action)delegate
					{
						try
						{
							Logger.Log("Self-heal: shell ready - clean restart to load the real layout/pins/inventory.");
							Process.Start(new ProcessStartInfo(exe, "--autostart --healed")
							{
								UseShellExecute = false,
								WorkingDirectory = AppContext.BaseDirectory
							});
							ExitApp();
						}
						catch (Exception ex)
						{
							Logger.Log("Self-heal restart failed: " + ex.Message);
						}
					}, Array.Empty<object>());
				}
			}
		});
	}

	private static bool EnvironmentReady()
	{
		try
		{
			string[] array = new string[2]
			{
				Profile.ProfilePath,
				TaskbarPins.PinsPath
			};
			foreach (string p in array)
			{
				if (File.Exists(p))
				{
					using (File.Open(p, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
					{
					}
				}
			}
		}
		catch
		{
			return false;
		}
		if (Process.GetProcessesByName("explorer").Length == 0)
		{
			return false;
		}
		if (AppInventory.LastLoadDegraded && AppInventory.LastGoodCount > 20)
		{
			return StaRescanCount() >= AppInventory.LastGoodCount * 7 / 10;
		}
		return true;
	}

	private static int StaRescanCount()
	{
		int result = 0;
		Thread t = new Thread((ThreadStart)delegate
		{
			result = AppInventory.RescanCount();
		});
		t.SetApartmentState(ApartmentState.STA);
		t.IsBackground = true;
		t.Start();
		t.Join(TimeSpan.FromSeconds(10L));
		return result;
	}

	private void ExitApp()
	{
		Cleanup();
		Shutdown();
	}

	protected override void OnSessionEnding(SessionEndingCancelEventArgs e)
	{
		_sessionEnding = true;
		try { Logger.Log("Windows session ending: fast cleanup path armed"); } catch { }
		base.OnSessionEnding(e);
	}

	protected override void OnExit(ExitEventArgs e)
	{
		if (SettingsStore.ReadOnlyDiagnostics)
		{
			base.OnExit(e);
			return;
		}
		GoogleAuth.ConnectionChanged -= GoogleConnectionChanged;
		Cleanup();
		base.OnExit(e);
	}

	private void Cleanup()
	{
		if (!_isPrimary || Interlocked.Exchange(ref _cleanupStarted, 1) != 0)
		{
			return;
		}
		// Guard the exit flush so a flush fault can NEVER skip the clean-exit marker + native restore below (that would
		// turn a clean/panic exit into a supervisor relaunch loop with the native shell still hidden).
		try { _startScreen?.CommitActiveGroupEditAndFlush(); } catch (Exception exFlush) { Logger.Log("exit flush failed (continuing clean exit): " + exFlush.Message); }
		try
		{
			File.WriteAllText(CleanExitMarkerPath, DateTime.Now.ToString("o"));
		}
		catch
		{
		}
		DispatcherTimer? shellTimer = _shellTimer;
		if (shellTimer != null)
		{
			shellTimer.Stop();
		}
		try
		{
			_instanceMutex?.ReleaseMutex();
			_instanceMutex?.Dispose();
			_instanceMutex = null;
		}
		catch
		{
		}
		if (!_sessionEnding)
		{
			// Guard each restore step so one failure can't skip the others — native shell must come back fully.
			try { DesktopComposition.RevertOnExit(); } catch (Exception ex1) { Logger.Log("RevertOnExit failed: " + ex1.Message); }
			try { NativeShell.ResumeShellHosts(); } catch (Exception ex2) { Logger.Log("ResumeShellHosts failed: " + ex2.Message); }
			try { _taskbar?.Deactivate(); } catch (Exception ex3) { Logger.Log("taskbar Deactivate failed: " + ex3.Message); }
			try { AppBar.ShowNativeTaskbar(); } catch (Exception ex4) { Logger.Log("ShowNativeTaskbar failed: " + ex4.Message); }
		}
		TrayHostService.Stop();   // destroy our Shell_TrayWnd + broadcast TaskbarCreated so explorer reclaims the tray
		_winHook?.Dispose();
		_snapWatcher?.Dispose();
		_notifRouter?.Dispose();
		try
		{
			if (!_sessionEnding && SettingsStore.Load().ReplaceSystemIcons)
			{
				SystemIcons81.Restore();
			}
		}
		catch
		{
		}
		_hotCorners?.Dispose();
		_charmsGesture?.Dispose();
		if (_trayIcon != null)
		{
			_trayIcon.Visible = false;
			_trayIcon.Dispose();
		}
	}

	[CompilerGenerated]
	internal static void _003COnStartup_003Eg__Open_007C30_195(string file, string? args = null)
	{
		try
		{
			Process.Start(new ProcessStartInfo(file, args ?? "")
			{
				UseShellExecute = true
			});
		}
		catch (Exception ex)
		{
			Logger.Log("open " + file + ": " + ex.Message);
		}
	}
}
