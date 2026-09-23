using System;
using System.Drawing;
using System.Runtime.InteropServices;

namespace Win81Layer;

// Per-monitor DPI scale for a TARGET monitor, chosen by a screen rectangle's centre. Mirrors the proven approach in
// ActionCenter.ScaleFor: ask the MONITOR (MonitorFromPoint + GetDpiForMonitor) rather than reading the window's current
// CompositionTarget.TransformToDevice. The latter reflects the monitor the window is CURRENTLY on, which is the wrong
// scale when a surface is about to be shown on a DIFFERENT-scale monitor (mis-sizes Start/Charms/Settings on a mixed-DPI
// setup). On a single-monitor / same-DPI setup this returns exactly the same value, so it is a no-op there. Degrades to
// 1.0 on any failure.
internal static class MonitorDpi
{
	[StructLayout(LayoutKind.Sequential)]
	private struct POINT
	{
		public int X;

		public int Y;
	}

	private const uint MONITOR_DEFAULTTONEAREST = 2u;

	private const uint MDT_EFFECTIVE_DPI = 0u;

	[DllImport("user32.dll")]
	private static extern nint MonitorFromPoint(POINT p, uint flags);

	[DllImport("shcore.dll")]
	private static extern int GetDpiForMonitor(nint mon, uint type, out uint dx, out uint dy);

	// Scale (1.0 == 96 dpi) for the monitor containing the centre of the given PHYSICAL-pixel rectangle.
	public static double ScaleFor(Rectangle b)
	{
		try
		{
			POINT c = new POINT
			{
				X = b.Left + b.Width / 2,
				Y = b.Top + b.Height / 2
			};
			nint mon = MonitorFromPoint(c, MONITOR_DEFAULTTONEAREST);
			if (mon != IntPtr.Zero && GetDpiForMonitor(mon, MDT_EFFECTIVE_DPI, out var dx, out var _) == 0 && dx != 0u)
			{
				return (double)dx / 96.0;
			}
		}
		catch
		{
		}
		return 1.0;
	}
}
