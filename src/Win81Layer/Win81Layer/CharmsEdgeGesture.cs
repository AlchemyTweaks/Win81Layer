using System;
using System.Drawing;
using System.Windows.Forms;

namespace Win81Layer;

// Deliberate right-edge -> left DRAG that opens the Charms bar — the intentional replacement for the old accidental
// hot corners. SAFETY BY CONSTRUCTION:
//   * It arms ONLY when the LEFT button goes down inside the thin OUTER-right edge zone of the OUTER-right monitor.
//     A mouse-down anywhere else (including the internal boundary between two monitors) never arms it, so crossing
//     monitors, dragging windows, hitting scrollbars/resize borders or the desktop can't trigger Charms.
//   * It is a pure OBSERVER — WinKeyHook never swallows the events (returns CallNextHookEx), so the underlying window
//     still receives every move/up unchanged. The gesture only *reveals* Charms on top.
//   * PROGRESSIVE: while dragging left it reports a 0..1 reveal so the bar tracks the pointer; on release it commits
//     (open) only past the threshold, otherwise it slides back off-screen.
// Constants are here (not scattered through the UI) so activation feel is tunable in one place.
internal sealed class CharmsEdgeGesture : IDisposable
{
	private const int EdgeZonePx = 4;        // outermost activation band (2-4 px)
	private const int ThresholdPx = 50;      // minimum leftward drag to OPEN on release
	private const int FullRevealPx = 150;    // leftward drag at which the bar is fully revealed (~bar width)

	private readonly WinKeyHook _hook;

	public Action<double>? OnProgress;   // 0 = hidden .. 1 = fully revealed; fired during the drag
	public Action<bool>? OnCommit;       // true = open, false = cancel; fired on release
	public Func<bool>? IsBlocked;        // gesture disabled while true (e.g. a fullscreen app is foreground)

	private volatile bool _active;
	private int _edgeRight;

	public CharmsEdgeGesture(WinKeyHook hook)
	{
		_hook = hook;
		_hook.GlobalLeftDown += OnLeftDown;
		_hook.GlobalMouseMove += OnMouseMove;
		_hook.GlobalLeftUp += OnLeftUp;
	}

	private void OnLeftDown(int x, int y)
	{
		if (_active)
		{
			return;
		}
		try
		{
			if (IsBlocked != null && IsBlocked())
			{
				return;   // e.g. fullscreen game/video — never steal the edge
			}
		}
		catch
		{
		}
		try
		{
			// Only the OUTER-right physical desktop edge activates — never an internal monitor boundary.
			Rectangle vs = SystemInformation.VirtualScreen;
			Screen scr = Screen.FromPoint(new Point(x, y));
			int right = scr.Bounds.Right;
			if (right != vs.Right)
			{
				return;   // the cursor's monitor is not the rightmost one
			}
			if (x < right - EdgeZonePx)
			{
				return;   // the mouse-down did not start inside the thin edge band
			}
			_edgeRight = right;
			_active = true;
			_hook.TrackMouseDrag = true;   // start surfacing move/up ONLY now
			OnProgress?.Invoke(0.0);
		}
		catch
		{
		}
	}

	private void OnMouseMove(int x, int y)
	{
		if (!_active)
		{
			return;
		}
		int dragLeft = _edgeRight - x;
		if (dragLeft < 0)
		{
			dragLeft = 0;
		}
		double reveal = Math.Min(1.0, (double)dragLeft / (double)FullRevealPx);
		try
		{
			OnProgress?.Invoke(reveal);
		}
		catch
		{
		}
	}

	private void OnLeftUp(int x, int y)
	{
		if (!_active)
		{
			return;
		}
		_active = false;
		_hook.TrackMouseDrag = false;
		int dragLeft = _edgeRight - x;
		bool open = dragLeft >= ThresholdPx;
		try
		{
			OnCommit?.Invoke(open);
		}
		catch
		{
		}
	}

	public void Dispose()
	{
		_hook.GlobalLeftDown -= OnLeftDown;
		_hook.GlobalMouseMove -= OnMouseMove;
		_hook.GlobalLeftUp -= OnLeftUp;
		_hook.TrackMouseDrag = false;
	}
}
