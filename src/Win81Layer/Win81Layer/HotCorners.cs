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

	private const int LeftEdgeDwell = 5;

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

	public bool Enabled { get; set; }

	public HotCorners()
	{
		//IL_0058: Unknown result type (might be due to invalid IL or missing references)
		//IL_005d: Unknown result type (might be due to invalid IL or missing references)
		//IL_0071: Expected O, but got Unknown
		Enabled = true;
		_hits = new int[5];
		_armed = new bool[5] { true, true, true, true, true };
		_screens = Array.Empty<Rectangle>();
		RefreshBounds();
		SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
		// Input priority prevents taskbar hover/layout work from starving the bottom-right corner, which physically
		// sits inside the custom taskbar. The 100 ms poll remains negligible but guarantees a sub-300 ms two-hit dwell.
		_timer = new DispatcherTimer(DispatcherPriority.Input)
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
		_timer.Start();
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
		if (!Enabled)
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
		Point p = Cursor.Position;
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
		Check(4, leftOuter && p.X <= b.Left + LeftEdgeSize && p.Y > b.Top + 48 && p.Y < b.Bottom - 48, LeftEdge, LeftEdgeDwell);
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
		_timer.Stop();
	}
}
