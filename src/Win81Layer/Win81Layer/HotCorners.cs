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

	private Rectangle[] _screens;

	private Rectangle _virtual;

	private bool _boundsValid;

	// volatile: written on the SystemEvents thread (OnSessionSwitch), read on the UI thread (Poll).
	private volatile bool _locked;

	private bool _enabled = true;

	// Setting Enabled now actually stops/starts the timer (not just short-circuits Poll), so turning hot corners off
	// truly ends the idle wakeups.
	public bool Enabled
	{
		get => _enabled;
		set { _enabled = value; if (value) { _timer?.Start(); } else { _timer?.Stop(); } }
	}

	[System.Runtime.InteropServices.DllImport("user32.dll")]
	private static extern bool GetCursorPos(out Point lpPoint);

	public HotCorners()
	{
		_hits = new int[5];
		_armed = new bool[5] { true, true, true, true, true };
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
			// Guard the 10x/sec poll: a corner-action fault must not re-throw into the dispatcher handler every 100ms.
			try
			{
				Poll();
			}
			catch (Exception ex)
			{
				Logger.Log("HotCorners poll failed: " + ex.Message);
			}
		};
		if (_enabled) { _timer.Start(); }
	}

	private void OnSessionSwitch(object? sender, SessionSwitchEventArgs e)
	{
		if (e.Reason == SessionSwitchReason.SessionLock || e.Reason == SessionSwitchReason.SessionLogoff
			|| e.Reason == SessionSwitchReason.ConsoleDisconnect || e.Reason == SessionSwitchReason.RemoteDisconnect)
		{
			_locked = true;
		}
		else if (e.Reason == SessionSwitchReason.SessionUnlock || e.Reason == SessionSwitchReason.SessionLogon
			|| e.Reason == SessionSwitchReason.ConsoleConnect || e.Reason == SessionSwitchReason.RemoteConnect)
		{
			_locked = false;
		}
	}

	private void OnDisplaySettingsChanged(object? sender, EventArgs e)
	{
		_boundsValid = false;
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

	private void Poll()
	{
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
		}
		// Use GetCursorPos directly and HONOUR its failure: on the lock/secure desktop it returns false and WinForms'
		// Cursor.Position would instead silently yield (0,0) == top-left hot corner, auto-firing the overlay at idle.
		if (!GetCursorPos(out Point p))
		{
			for (int i = 0; i < _hits.Length; i++) { _hits[i] = 0; _armed[i] = true; }
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
		Check(0, leftOuter && bottomOuter && p.X <= b.Left + CornerSize && p.Y >= b.Bottom - CornerSize, BottomLeft, BottomLeftDwell);
		Check(1, leftOuter && topOuter && p.X <= b.Left + CornerSize && p.Y <= b.Top + CornerSize, TopLeft, TopLeftDwell);
		// Arm the two right corners independently. Sharing one latch made a rapid bottom-right -> top-right workflow
		// occasionally miss the second action while the first Charms dismissal was still completing.
		Check(2, rightOuter && topOuter && p.X >= b.Right - CornerSize && p.Y <= b.Top + CornerSize, RightCorners, RightDwell);
		Check(3, rightOuter && bottomOuter && p.X >= b.Right - CornerSize && p.Y >= b.Bottom - CornerSize, RightCorners, RightDwell);
		Check(4, leftOuter && p.X <= b.Left + LeftEdgeSize && p.Y > b.Top + 48 && p.Y < b.Bottom - 48, LeftEdge, LeftEdgeDwellTicks);
	}

	private void Check(int index, bool inside, Action? action, int dwellTicks)
	{
		if (!inside)
		{
			_hits[index] = 0;
			_armed[index] = true;
		}
		else if (_armed[index] && ++_hits[index] >= dwellTicks)
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
	}
}
