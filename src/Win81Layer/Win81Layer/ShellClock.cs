#nullable enable

using System;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Windows.Threading;
using Microsoft.Win32;

namespace Win81Layer;

// One coalesced timer for the shell's periodic work, replacing the per-bar 2 s refresh and 1 s fast timers and the 1 s
// shell timer. Cadence follows the user:
// - Active (input within 60 s): 1 s ticks; bars refresh every 2 s; foreground/fullscreen checks every tick; supervisor
//   every 2 s.
// - Idle (no input for 60 s): 3 s ticks; bars refresh every 10 s.
// - Asleep (session locked or display off): 5 s ticks with only the heartbeat and the supervisor; everything is
//   refreshed once on wake.
// The heartbeat is stamped on every tick in every mode (the supervisor treats > 30 s without one as a dead shell, the
// watchdog > 12 s), so it is never paused. Input ends Idle at once: the input hook calls RequestKick on the first event.
// Input cannot end Asleep (unlock and display-on are notifications), so Asleep never asks for kicks; a tick that sees
// input newer than the display-off notification treats the display as on again (a lost display-on message).
// Foreground and fullscreen changes that come with an activation arrive through the bars' shell hook at once; the
// FastBars tick is the backstop for in-place changes (a window entering fullscreen without an activation), so those
// can take up to one idle tick (3 s) when nobody has touched the input for a minute.
internal sealed class ShellClock : IDisposable
{
	internal enum Mode
	{
		Active,
		Idle,
		Asleep
	}

	internal const long IdleAfterMs = 60000L;

	internal const long SupervisorEveryMs = 2000L;

	// --- pure cadence rules (tested by --perfprobe unit)

	internal static Mode Decide(long inputIdleMs, bool asleep)
	{
		if (asleep)
		{
			return Mode.Asleep;
		}
		return inputIdleMs >= IdleAfterMs ? Mode.Idle : Mode.Active;
	}

	internal static int IntervalMs(Mode m)
	{
		return m switch
		{
			Mode.Active => 1000,
			Mode.Idle => 3000,
			_ => 5000
		};
	}

	internal static long RefreshEveryMs(Mode m)
	{
		return m == Mode.Active ? 2000L : 10000L;
	}

	internal static long WardenEveryMs(Mode m)
	{
		return m == Mode.Active ? 60000L : 300000L;
	}

	// Set while not Active: the input hooks read it on every event (one volatile load) and kick the clock once.
	internal static volatile bool WantKick;

	// Probe only (--assume-idle): the input idle time to use instead of GetLastInputInfo.
	internal static Func<long>? InputIdleOverride;

	internal static ShellClock? Current;

	// --- consumers (UI thread)

	internal Action? Heartbeat;

	internal Action? Supervisor;

	internal Action? RefreshBars;

	internal Action? FastBars;

	// Idle ticks on which the bars do not refresh: the user-visible tray readings (volume, the CPU/RAM meter) keep a
	// 3 s cadence instead of following the 10 s idle refresh.
	internal Action? Telemetry;

	internal Action<long, long>? Maintenance;

	internal Action<Mode, Mode>? ModeChanged;

	internal Action<bool>? Asleep;

	private readonly DispatcherTimer _timer;

	private readonly Dispatcher _dispatcher;

	private Mode _mode = Mode.Active;

	private bool _locked;

	private bool _displayOff;

	private long _displayOffSinceMs;

	private long _lastRefreshMs = long.MinValue / 2;

	private long _lastSupervisorMs = long.MinValue / 2;

	private HwndSource? _powerWindow;

	private nint _powerNotify;

	private bool _sessionHooked;

	internal long Ticks;

	internal long Refreshes;

	internal Mode CurrentMode => _mode;

	internal ShellClock(Dispatcher dispatcher, bool watchSession = true)
	{
		_dispatcher = dispatcher;
		_timer = new DispatcherTimer(DispatcherPriority.Background, dispatcher)
		{
			Interval = TimeSpan.FromMilliseconds(IntervalMs(Mode.Active))
		};
		_timer.Tick += delegate
		{
			Tick();
		};
		if (watchSession)
		{
			try
			{
				SystemEvents.SessionSwitch += OnSessionSwitch;
				_sessionHooked = true;
			}
			catch
			{
			}
			RegisterDisplayNotification();
		}
	}

	internal void Start()
	{
		Current = this;
		_timer.Start();
	}

	// Input arrived while Idle (any thread): one hop to the UI thread runs a tick at the Active cadence.
	internal static void RequestKick()
	{
		if (!WantKick)
		{
			return;
		}
		WantKick = false;
		ShellClock? c = Current;
		if (c == null)
		{
			return;
		}
		try
		{
			c._dispatcher.BeginInvoke(DispatcherPriority.Background, (Action)c.Kick);
		}
		catch
		{
		}
	}

	internal void Kick()
	{
		if (_mode != Mode.Active)
		{
			Tick();
		}
	}

	internal static long InputIdleMs()
	{
		Func<long>? o = InputIdleOverride;
		if (o != null)
		{
			return o();
		}
		LASTINPUTINFO li = new LASTINPUTINFO { cbSize = (uint)Marshal.SizeOf<LASTINPUTINFO>() };
		if (!GetLastInputInfo(ref li))
		{
			_lastInputKnown = false;
			return 0L;
		}
		_lastInputKnown = true;
		return unchecked((uint)Environment.TickCount - li.dwTime);
	}

	// False when the last GetLastInputInfo failed (InputIdleMs then reports 0, which must not count as fresh input).
	private static bool _lastInputKnown = true;

	internal void Tick()
	{
		long now = Environment.TickCount64;
		long idle = InputIdleMs();
		if (_displayOff && !_locked && (_lastInputKnown || InputIdleOverride != null) && now - idle > _displayOffSinceMs + 500L)
		{
			// Input after the display went off turns it back on: a display-on notification was lost.
			_displayOff = false;
			Logger.Log("Shell clock: input while the display was off; treating the display as on");
		}
		Mode m = Decide(idle, _locked || _displayOff);
		if (m != _mode)
		{
			Mode old = _mode;
			_mode = m;
			_timer.Interval = TimeSpan.FromMilliseconds(IntervalMs(m));
			if (old == Mode.Asleep)
			{
				_lastRefreshMs = long.MinValue / 2;   // wake: refresh everything on this tick
			}
			try { ModeChanged?.Invoke(old, m); } catch (Exception ex) { Logger.Log("shell clock mode change: " + ex.Message); }
		}
		Ticks++;
		// The liveness stamp stays outside every guard below.
		try { Heartbeat?.Invoke(); } catch { }
		// Each consumer has its own guard (as the separate timers it replaced had): one that keeps failing never stops the
		// others, so the supervisor, the maintenance and the input-hook checks keep running whatever a bar does.
		if (m != Mode.Asleep)
		{
			try { FastBars?.Invoke(); } catch (Exception ex) { LogFailure("fast bar tick", ex); }
			if (now - _lastRefreshMs >= RefreshEveryMs(m))
			{
				_lastRefreshMs = now;
				Refreshes++;
				try { RefreshBars?.Invoke(); } catch (Exception ex) { LogFailure("bar refresh", ex); }
			}
			else if (m == Mode.Idle)
			{
				try { Telemetry?.Invoke(); } catch (Exception ex) { LogFailure("tray telemetry", ex); }
			}
		}
		if (m != Mode.Active || now - _lastSupervisorMs >= SupervisorEveryMs)
		{
			_lastSupervisorMs = now;
			try { Supervisor?.Invoke(); } catch (Exception ex) { LogFailure("supervisor", ex); }
		}
		try { Maintenance?.Invoke(now, idle); } catch (Exception ex) { LogFailure("maintenance", ex); }
		// Only Idle waits for input. Asleep ends with an unlock or display-on notification, never with input (each event
		// would otherwise run a full tick back to back while the display wakes).
		WantKick = m == Mode.Idle;
	}

	// A failing shell-clock consumer (or one part of it) is logged once a minute per name, not on every tick.
	private static readonly System.Collections.Generic.Dictionary<string, long> _failureLoggedMs = new System.Collections.Generic.Dictionary<string, long>();

	internal static int Failures;

	internal static void LogFailure(string what, Exception ex)
	{
		Failures++;
		long now = Environment.TickCount64;
		lock (_failureLoggedMs)
		{
			if (_failureLoggedMs.TryGetValue(what, out long last) && now - last < 60000L)
			{
				return;
			}
			_failureLoggedMs[what] = now;
		}
		Logger.Log($"shell clock: {what} failed (the other consumers and the heartbeat keep running): {ex.GetType().Name}: {ex.Message}");
	}

	private void OnSessionSwitch(object? sender, SessionSwitchEventArgs e)
	{
		bool? locked = e.Reason switch
		{
			SessionSwitchReason.SessionLock or SessionSwitchReason.SessionLogoff or SessionSwitchReason.ConsoleDisconnect or SessionSwitchReason.RemoteDisconnect => true,
			SessionSwitchReason.SessionUnlock or SessionSwitchReason.SessionLogon or SessionSwitchReason.ConsoleConnect or SessionSwitchReason.RemoteConnect => false,
			_ => null
		};
		if (locked == null)
		{
			return;
		}
		try
		{
			_dispatcher.BeginInvoke(DispatcherPriority.Background, (Action)delegate
			{
				SetAsleepState(locked: locked.Value, displayOff: _displayOff, lockEvent: true);
			});
		}
		catch
		{
		}
	}

	// --perfprobe unit: the display-state notification, as GUID_SESSION_DISPLAY_STATUS delivers it.
	internal void QaDisplayState(bool off)
	{
		SetAsleepState(_locked, displayOff: off, lockEvent: false);
	}

	internal bool DisplayOff => _displayOff;

	private void SetAsleepState(bool locked, bool displayOff, bool lockEvent)
	{
		bool wasAsleep = _locked || _displayOff;
		bool turnedOff = lockEvent ? (locked && !_locked) : (displayOff && !_displayOff);
		if (displayOff && !_displayOff)
		{
			_displayOffSinceMs = Environment.TickCount64;
		}
		_locked = locked;
		_displayOff = displayOff;
		bool asleep = _locked || _displayOff;
		if (turnedOff)
		{
			try { Asleep?.Invoke(lockEvent); } catch { }
		}
		if (asleep != wasAsleep)
		{
			Tick();   // switch cadence now (and refresh everything when waking)
		}
	}

	// --- display on/off (GUID_SESSION_DISPLAY_STATUS on a message-only window)

	private static readonly Guid SessionDisplayStatus = new Guid("2B84C20E-AD23-4ddf-93DB-05FFBD7EFCA5");

	private const int WM_POWERBROADCAST = 0x0218;

	private const int PBT_POWERSETTINGCHANGE = 0x8013;

	private void RegisterDisplayNotification()
	{
		try
		{
			HwndSourceParameters p = new HwndSourceParameters("Win81Layer.ShellClock")
			{
				ParentWindow = new IntPtr(-3),   // HWND_MESSAGE
				WindowStyle = 0
			};
			_powerWindow = new HwndSource(p);
			_powerWindow.AddHook(PowerHook);
			Guid g = SessionDisplayStatus;
			_powerNotify = RegisterPowerSettingNotification(_powerWindow.Handle, ref g, 0);
		}
		catch (Exception ex)
		{
			Logger.Log("Shell clock display-state notification unavailable: " + ex.Message);
		}
	}

	private nint PowerHook(nint hwnd, int msg, nint wParam, nint lParam, ref bool handled)
	{
		if (msg == WM_POWERBROADCAST && wParam.ToInt64() == PBT_POWERSETTINGCHANGE && lParam != IntPtr.Zero)
		{
			try
			{
				Guid g = Marshal.PtrToStructure<Guid>(lParam);
				if (g == SessionDisplayStatus)
				{
					int state = Marshal.ReadInt32(lParam, 20);   // after the Guid (16) and DataLength (4): 0 off, 1 on, 2 dimmed
					SetAsleepState(_locked, displayOff: state == 0, lockEvent: false);
				}
			}
			catch
			{
			}
		}
		return IntPtr.Zero;
	}

	public void Dispose()
	{
		_timer.Stop();
		if (Current == this)
		{
			Current = null;
		}
		if (_sessionHooked)
		{
			try { SystemEvents.SessionSwitch -= OnSessionSwitch; } catch { }
		}
		if (_powerNotify != IntPtr.Zero)
		{
			try { UnregisterPowerSettingNotification(_powerNotify); } catch { }
			_powerNotify = IntPtr.Zero;
		}
		try { _powerWindow?.Dispose(); } catch { }
	}

	[StructLayout(LayoutKind.Sequential)]
	private struct LASTINPUTINFO
	{
		public uint cbSize;
		public uint dwTime;
	}

	[DllImport("user32.dll")]
	private static extern bool GetLastInputInfo(ref LASTINPUTINFO plii);

	[DllImport("user32.dll")]
	private static extern nint RegisterPowerSettingNotification(nint recipient, ref Guid powerSetting, int flags);

	[DllImport("user32.dll")]
	private static extern bool UnregisterPowerSettingNotification(nint handle);
}
