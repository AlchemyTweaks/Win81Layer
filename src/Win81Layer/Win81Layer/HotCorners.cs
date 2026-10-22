using System;
using System.Drawing;
using System.Windows.Forms;
using System.Windows.Threading;
using Microsoft.Win32;

namespace Win81Layer;

public sealed class HotCorners : IDisposable
{
	private const int CornerSize = 6;

	private const int BottomLeftDwell = 3;

	private const int RightDwell = 2;

	private const int TopLeftDwell = 2;

	private const int LeftEdgeSize = 4;

	// Poll-ticks (100 ms each) the cursor must dwell on the left edge before the switcher reveals. Settable so the
	// launcher can make the reveal more deliberate (SwitcherEdgeDwellMs) or the user can disable the edge action entirely.
	public int LeftEdgeDwellTicks { get; set; } = 5;

	public Action? BottomLeft;

	public Action? TopLeft;

	public Action? RightCorners;

	public Action? LeftEdge;

	private readonly DispatcherTimer _timer;

	private readonly int[] _hits;

	private readonly bool[] _armed;

	private readonly bool[] _inside;

	private Rectangle[] _screens;

	private Rectangle _virtual;

	private bool _boundsValid;

	// volatile: written on the SystemEvents thread (OnSessionSwitch), read on the UI thread (Poll).
	private volatile bool _locked;

	private bool _enabled = true;

	// Hook-armed mode: the low-level mouse hook reports when the pointer enters or leaves a corner zone, and the dwell
	// timer runs only while the pointer is inside one. Without the hook (it failed to install, or was given up), the
	// 10 Hz poll runs as before.
	private WinKeyHook? _hook;

	// Hook-armed mode is on (the probe arms it without a hook: the zones are then only checked by CheckNow).
	private bool _armedMode;

	// The first tick after entering a zone comes after 50 ms, later ones every 100 ms: the dwell then has the same mean
	// as the free-running 100 ms poll (whose phase was random), so the corners feel the same.
	private bool _firstTick;

	// Armed mode while the pointer is over a window whose input never reaches this medium-integrity hook (an elevated
	// window such as Task Manager or an admin terminal): the 100 ms poll then runs freely, as it did before the hook
	// arming, until a shell clock tick finds the pointer over a window the hook sees again (ClockTick).
	private bool _blindPoll;

	internal bool BlindPoll => _blindPoll;

	// Probe statistics: switches into or out of the blind poll, and the shell clock's cursor checks.
	internal long BlindSwitches;

	internal long SweepCount;

	// Setting Enabled now actually stops/starts the timer (not just short-circuits Poll), so turning hot corners off
	// truly ends the idle wakeups.
	public bool Enabled
	{
		get => _enabled;
		set
		{
			_enabled = value;
			if (_armedMode)
			{
				PublishZones();
				if (value)
				{
					CheckNow();
				}
				else
				{
					_blindPoll = false;
					_timer?.Stop();
				}
				return;
			}
			if (value) { _timer?.Start(); } else { _timer?.Stop(); }
		}
	}

	internal bool HookArmed => _armedMode;

	internal bool TimerRunning => _timer.IsEnabled;

	[System.Runtime.InteropServices.DllImport("user32.dll")]
	private static extern bool GetCursorPos(out Point lpPoint);

	public HotCorners()
	{
		_hits = new int[5];
		_armed = new bool[5] { true, true, true, true, true };
		_inside = new bool[5];
		_screens = Array.Empty<Rectangle>();
		RefreshBounds();
		SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
		// Gate the poll while the session is locked: when locked, GetCursorPos fails and WinForms' Cursor.Position
		// silently returns (0,0) == the top-left hot corner, which auto-fired the overlay by itself at idle on laptops.
		SystemEvents.SessionSwitch += OnSessionSwitch;
		// Background priority: the poll is just a cursor read, so it never preempts input/animation frames (smoother).
		_timer = new DispatcherTimer(DispatcherPriority.Background)
		{
			Interval = TimeSpan.FromMilliseconds(100L)
		};
		_timer.Tick += delegate
		{
			if (_firstTick)
			{
				_firstTick = false;
				_timer.Interval = TimeSpan.FromMilliseconds(100L);
			}
			// Guard the 10x/sec poll: a corner-action fault must not re-throw into the dispatcher handler every 100ms.
			try
			{
				Poll(count: true);
				if (_armedMode && !_blindPoll && !DwellPending())
				{
					_timer.Stop();   // armed mode: nothing left to wait for until the hook reports the next zone change
				}
			}
			catch (Exception ex)
			{
				Logger.Log("HotCorners poll failed: " + ex.Message);
			}
		};
		if (_enabled) { _timer.Start(); }
	}

	// Switches to hook-armed mode (UI thread). The hook thread reports zone changes through HotZoneChanged.
	internal void AttachHook(WinKeyHook? hook)
	{
		if (_armedMode && ReferenceEquals(_hook, hook))
		{
			PublishZones();
			CheckNow();
			return;
		}
		_hook = hook;
		_armedMode = true;
		Dispatcher ui = _timer.Dispatcher;
		if (hook != null)
		{
			hook.HotZoneChanged = delegate(int mask)
			{
				// Hook thread: one post per zone transition (never per mouse move).
				ui.BeginInvoke(DispatcherPriority.Input, (Action)delegate
				{
					OnZoneMask(mask);
				});
			};
		}
		_blindPoll = false;
		_timer.Stop();
		PublishZones();
		CheckNow();
	}

	// Back to the 10 Hz poll (the hook was lost).
	internal void DetachHook()
	{
		WinKeyHook? hook = _hook;
		_hook = null;
		_armedMode = false;
		_blindPoll = false;
		if (hook != null)
		{
			hook.HotZoneChanged = null;
			hook.SetHotZones(Array.Empty<int>());
		}
		_firstTick = false;
		_timer.Interval = TimeSpan.FromMilliseconds(100L);
		if (_enabled && !_locked)
		{
			_timer.Start();
		}
	}

	private void OnZoneMask(int mask)
	{
		if (!_armedMode || !_enabled || _locked)
		{
			return;
		}
		if (mask != 0)
		{
			StartDwell();
			return;
		}
		// Left every zone as the hook saw it: one cursor check (not counted as a dwell tick) resets the counters and
		// re-arms the fired corners. GetCursorPos has the final, clipped position: if the pointer still rests in an
		// unfired zone, the dwell goes on instead of stopping with the pointer parked in the corner.
		Poll(count: false);
		if (DwellPending())
		{
			StartDwell();
		}
		else if (!_blindPoll)
		{
			_timer.Stop();
		}
	}

	// Shell clock tick (UI thread, armed mode, session not asleep). Covers what the hook cannot report:
	// - the hook is blind: the pointer is over, or the foreground is, a window of a higher integrity level (an elevated
	//   app: UIPI keeps its input from this process's hooks), or the cursor moved since the last tick without the mouse
	//   hook seeing it (mouseHookSilent: a hook Windows removed, until it is re-installed). The 100 ms poll then runs, as
	//   before the hook arming, until a tick finds the hook seeing the pointer again;
	// - otherwise one cursor check (not a dwell tick): a zone entry the hook missed starts the dwell, and a corner whose
	//   leave the hook missed is re-armed, within one tick.
	internal void ClockTick(bool mouseHookSilent = false)
	{
		if (!_armedMode || !_enabled || _locked)
		{
			return;
		}
		if (!GetCursorPos(out Point p))
		{
			return;   // secure desktop or lock screen: nothing to watch
		}
		bool blind = _hook != null   // the probe arms without a hook: nothing to be blind to (the checks below still run)
			&& (mouseHookSilent || WindowUtil.WindowAtInputHiddenFromHooks(p.X, p.Y) || WindowUtil.ForegroundInputHiddenFromHooks());
		if (blind != _blindPoll)
		{
			_blindPoll = blind;
			BlindSwitches++;
			if (blind)
			{
				_firstTick = false;
				_timer.Interval = TimeSpan.FromMilliseconds(100L);
				if (!_timer.IsEnabled)
				{
					_timer.Start();
				}
			}
			else
			{
				// The hook's idea of the zone under the pointer may be stale (it saw nothing meanwhile): start it afresh,
				// so the next move inside a zone is reported as an entry.
				PublishZones();
				if (!DwellPending())
				{
					_timer.Stop();
				}
			}
			if (_blindLogs < 10)
			{
				_blindLogs++;
				Logger.Log(blind
					? "Hot corners: the mouse hook cannot see the pointer (" + (mouseHookSilent ? "no hook event for a move" : "a window of a higher integrity level") + "), 100 ms poll until it can"
					: "Hot corners: back to hook-armed zones");
			}
		}
		if (!_blindPoll && !_timer.IsEnabled)
		{
			SweepCount++;
			Poll(count: false);
			if (DwellPending())
			{
				StartDwell();
			}
		}
	}

	private int _blindLogs;

	private void StartDwell()
	{
		if (_timer.IsEnabled)
		{
			return;
		}
		_firstTick = true;
		_timer.Interval = TimeSpan.FromMilliseconds(50L);
		_timer.Start();
	}

	// One cursor check outside the hook (boot, unlock, display change, enable): starts the dwell when the pointer already
	// rests in a zone (no mouse event would report it).
	internal void CheckNow()
	{
		if (!_armedMode || !_enabled || _locked)
		{
			return;
		}
		if (!_boundsValid)
		{
			RefreshBounds();
			PublishZones();
		}
		if (GetCursorPos(out Point p) && ZoneMask(p.X, p.Y, BuildZones()) != 0)
		{
			StartDwell();
		}
		else if (!_timer.IsEnabled)
		{
			Poll(count: false);   // re-arm a corner whose leave was not seen (the pointer is outside every zone now)
		}
	}

	// True while the pointer sits in a wired zone whose action has not fired yet (the timer must keep counting).
	private bool DwellPending()
	{
		return (_inside[0] && _armed[0] && BottomLeft != null)
			|| (_inside[1] && _armed[1] && TopLeft != null)
			|| (_inside[2] && _armed[2] && RightCorners != null)
			|| (_inside[3] && _armed[3] && RightCorners != null)
			|| (_inside[4] && _armed[4] && LeftEdge != null);
	}

	// Zone rectangles (inclusive left/top, exclusive right/bottom) for the wired actions on the outer monitor edges, as
	// [left, top, right, bottom, bit] quintuples. On screen they are exactly the regions Poll tests, so a zone change is
	// a change of what Poll would see. Bounds on the outer edge of the desktop are open outward (Poll's tests have no
	// bound there either): a low-level mouse hook gets the point before it is clipped to the screen, so a push into the
	// corner reports points such as (-12, -7), which must still be inside the zone. Nothing lies beyond an outer edge,
	// so an open bound never reaches another monitor.
	internal int[] BuildZones()
	{
		if (!_boundsValid || !_enabled)
		{
			return Array.Empty<int>();
		}
		return BuildZonesFor(_screens, _virtual, BottomLeft != null, TopLeft != null, RightCorners != null, LeftEdge != null);
	}

	internal static int[] BuildZonesFor(Rectangle[] screens, Rectangle virtualScreen, bool bottomLeft, bool topLeft, bool rightCorners, bool leftEdge)
	{
		System.Collections.Generic.List<int> z = new System.Collections.Generic.List<int>(40);
		foreach (Rectangle b in screens)
		{
			bool leftOuter = b.Left == virtualScreen.Left;
			bool rightOuter = b.Right == virtualScreen.Right;
			bool topOuter = b.Top == virtualScreen.Top;
			bool bottomOuter = b.Bottom == virtualScreen.Bottom;
			if (bottomLeft && leftOuter && bottomOuter)
			{
				Add(z, OpenLow, b.Bottom - CornerSize, b.Left + CornerSize + 1, OpenHigh, 0);
			}
			if (topLeft && leftOuter && topOuter)
			{
				Add(z, OpenLow, OpenLow, b.Left + CornerSize + 1, b.Top + CornerSize + 1, 1);
			}
			if (rightCorners && rightOuter && topOuter)
			{
				Add(z, b.Right - CornerSize, OpenLow, OpenHigh, b.Top + CornerSize + 1, 2);
			}
			if (rightCorners && rightOuter && bottomOuter)
			{
				Add(z, b.Right - CornerSize, b.Bottom - CornerSize, OpenHigh, OpenHigh, 3);
			}
			if (leftEdge && leftOuter)
			{
				Add(z, OpenLow, b.Top + 49, b.Left + LeftEdgeSize + 1, b.Bottom - 48, 4);
			}
		}
		return z.ToArray();
		static void Add(System.Collections.Generic.List<int> z, int l, int t, int r, int bt, int bit)
		{
			if (r > l && bt > t)
			{
				z.Add(l);
				z.Add(t);
				z.Add(r);
				z.Add(bt);
				z.Add(1 << bit);
			}
		}
	}

	// Open bounds of the outer-edge zones (far beyond any screen, with room for the compares).
	internal const int OpenLow = int.MinValue / 2;

	internal const int OpenHigh = int.MaxValue / 2;

	// The same test the hook thread runs on every mouse move.
	internal static int ZoneMask(int x, int y, int[] zones)
	{
		int mask = 0;
		for (int i = 0; i + 4 < zones.Length; i += 5)
		{
			if (x >= zones[i] && y >= zones[i + 1] && x < zones[i + 2] && y < zones[i + 3])
			{
				mask |= zones[i + 4];
			}
		}
		return mask;
	}

	private void PublishZones()
	{
		_hook?.SetHotZones(_locked ? Array.Empty<int>() : BuildZones());
	}

	private void OnSessionSwitch(object? sender, SessionSwitchEventArgs e)
	{
		if (e.Reason == SessionSwitchReason.SessionLock || e.Reason == SessionSwitchReason.SessionLogoff
			|| e.Reason == SessionSwitchReason.ConsoleDisconnect || e.Reason == SessionSwitchReason.RemoteDisconnect)
		{
			_locked = true;
			// Nothing to watch while locked: stop the timer (it used to keep polling the lock screen at 10 Hz).
			_timer.Dispatcher.BeginInvoke(DispatcherPriority.Background, (Action)delegate
			{
				if (_locked)
				{
					_blindPoll = false;
					_timer.Stop();
					PublishZones();
				}
			});
		}
		else if (e.Reason == SessionSwitchReason.SessionUnlock || e.Reason == SessionSwitchReason.SessionLogon
			|| e.Reason == SessionSwitchReason.ConsoleConnect || e.Reason == SessionSwitchReason.RemoteConnect)
		{
			_locked = false;
			_timer.Dispatcher.BeginInvoke(DispatcherPriority.Background, (Action)delegate
			{
				if (_locked || !_enabled)
				{
					return;
				}
				if (_armedMode)
				{
					PublishZones();
					CheckNow();
				}
				else
				{
					_timer.Start();
				}
			});
		}
	}

	private void OnDisplaySettingsChanged(object? sender, EventArgs e)
	{
		_boundsValid = false;
		if (_armedMode)
		{
			_timer.Dispatcher.BeginInvoke(DispatcherPriority.Background, (Action)delegate
			{
				if (_armedMode)
				{
					RefreshBounds();
					PublishZones();
					CheckNow();
				}
			});
		}
	}

	private void RefreshBounds()
	{
		Screen[] all = Screen.AllScreens;
		if (all.Length == 0)
		{
			_boundsValid = false;
			return;
		}
		_screens = Array.ConvertAll(all, (Screen s) => s.Bounds);
		_virtual = SystemInformation.VirtualScreen;
		_boundsValid = true;
	}

	// Poll ticks so far (probe statistics).
	internal long PollCount;

	// count: a dwell tick (the timer). A check from the hook's leave report or the shell clock only refreshes which zones
	// hold the pointer and re-arms the zones it is outside of; it never advances a dwell or fires an action.
	private void Poll(bool count)
	{
		if (count)
		{
			PollCount++;
		}
		if (!_enabled || _locked)
		{
			return;
		}
		if (!_boundsValid)
		{
			RefreshBounds();
			if (!_boundsValid)
			{
				return;
			}
			PublishZones();
		}
		// Use GetCursorPos directly and HONOUR its failure: on the lock/secure desktop it returns false and WinForms'
		// Cursor.Position would instead silently yield (0,0) == top-left hot corner, auto-firing the overlay at idle.
		if (!GetCursorPos(out Point p))
		{
			for (int i = 0; i < _hits.Length; i++) { _hits[i] = 0; _armed[i] = true; _inside[i] = false; }
			return;
		}
		Rectangle b = _screens[0];
		Rectangle[] screens = _screens;
		for (int i = 0; i < screens.Length; i++)
		{
			Rectangle s = screens[i];
			if (s.Contains(p))
			{
				b = s;
				break;
			}
		}
		bool leftOuter = b.Left == _virtual.Left;
		bool rightOuter = b.Right == _virtual.Right;
		bool topOuter = b.Top == _virtual.Top;
		bool bottomOuter = b.Bottom == _virtual.Bottom;
		Check(0, leftOuter && bottomOuter && p.X <= b.Left + CornerSize && p.Y >= b.Bottom - CornerSize, BottomLeft, BottomLeftDwell, count);
		Check(1, leftOuter && topOuter && p.X <= b.Left + CornerSize && p.Y <= b.Top + CornerSize, TopLeft, TopLeftDwell, count);
		// Arm the two right corners independently. Sharing one latch made a rapid bottom-right -> top-right workflow
		// occasionally miss the second action while the first Charms dismissal was still completing.
		Check(2, rightOuter && topOuter && p.X >= b.Right - CornerSize && p.Y <= b.Top + CornerSize, RightCorners, RightDwell, count);
		Check(3, rightOuter && bottomOuter && p.X >= b.Right - CornerSize && p.Y >= b.Bottom - CornerSize, RightCorners, RightDwell, count);
		Check(4, leftOuter && p.X <= b.Left + LeftEdgeSize && p.Y > b.Top + 48 && p.Y < b.Bottom - 48, LeftEdge, LeftEdgeDwellTicks, count);
	}

	private void Check(int index, bool inside, Action? action, int dwellTicks, bool count)
	{
		_inside[index] = inside;
		if (!inside)
		{
			_hits[index] = 0;
			_armed[index] = true;
		}
		else if (count && _armed[index] && ++_hits[index] >= dwellTicks)
		{
			_armed[index] = false;
			_hits[index] = 0;
			try
			{
				action?.Invoke();
			}
			catch (Exception value)
			{
				Logger.Log($"Hot corner action failed: {value}");
			}
		}
	}

	public void Dispose()
	{
		SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
		SystemEvents.SessionSwitch -= OnSessionSwitch;
		_timer.Stop();
		WinKeyHook? hook = _hook;
		if (hook != null)
		{
			hook.HotZoneChanged = null;
			hook.SetHotZones(Array.Empty<int>());
		}
	}
}
