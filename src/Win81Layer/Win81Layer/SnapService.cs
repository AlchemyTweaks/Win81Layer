using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

namespace Win81Layer;

internal static class SnapService
{
	private struct RECT
	{
		public int L;

		public int T;

		public int R;

		public int B;
	}

	internal static SnapAssist? Assist;

	private const int SW_RESTORE = 9;

	private const int SW_MAXIMIZE = 3;

	private const uint SWP_NOZORDER = 4u;

	private const uint SWP_NOACTIVATE = 16u;

	public static void SnapForeground(bool left)
	{
		try
		{
			nint h = GetForegroundWindow();
			if (h != IntPtr.Zero && !IsShellSurface(h) && GetWindowRect(h, out var wr))
			{
				Rectangle wa = TaskbarWorkArea.Current(Screen.FromRectangle(Rectangle.FromLTRB(wr.L, wr.T, wr.R, wr.B)));
				int hw = wa.Width / 2;
				Rectangle target = (left ? new Rectangle(wa.Left, wa.Top, hw, wa.Height) : new Rectangle(wa.Left + hw, wa.Top, wa.Width - hw, wa.Height));
				uint dpi = GetDpiForWindow(h);
				double sc = ((dpi == 0) ? 1.0 : ((double)dpi / 96.0));
				SnapManager.SetScale(sc, sc);
				SnapZones.Apply(h, target);
				SnapManager.Record(h, target);
				SnapRect empty = (left ? new SnapRect
				{
					Left = wa.Left + hw,
					Top = wa.Top,
					Right = wa.Right,
					Bottom = wa.Bottom
				} : new SnapRect
				{
					Left = wa.Left,
					Top = wa.Top,
					Right = wa.Left + hw,
					Bottom = wa.Bottom
				});
				Assist?.OfferFor(empty, h);
			}
		}
		catch (Exception ex)
		{
			Logger.Log("SnapService: " + ex.Message);
		}
	}

	public static void MoveForegroundToNeighbor(bool left)
	{
		try
		{
			nint h = GetForegroundWindow();
			if (h == IntPtr.Zero || IsShellSurface(h) || !GetWindowRect(h, out var wr))
			{
				return;
			}
			Rectangle cur = TaskbarWorkArea.Current(Screen.FromRectangle(Rectangle.FromLTRB(wr.L, wr.T, wr.R, wr.B)));
			Rectangle? rectangle = MonitorManager.NeighborWorkArea(cur, left);
			if (!rectangle.HasValue)
			{
				return;
			}
			Rectangle twa = rectangle.GetValueOrDefault();
			if (true)
			{
				bool wasMax = IsZoomed(h);
				double fx = (double)(wr.L - cur.Left) / (double)cur.Width;
				double fy = (double)(wr.T - cur.Top) / (double)cur.Height;
				double fw = (double)(wr.R - wr.L) / (double)cur.Width;
				double fh = (double)(wr.B - wr.T) / (double)cur.Height;
				int nx = twa.Left + (int)Math.Round(fx * (double)twa.Width);
				int ny = twa.Top + (int)Math.Round(fy * (double)twa.Height);
				int nw = Math.Max(1, (int)Math.Round(fw * (double)twa.Width));
				int nh = Math.Max(1, (int)Math.Round(fh * (double)twa.Height));
				ShowWindow(h, 9);
				SetWindowPos(h, IntPtr.Zero, nx, ny, nw, nh, 20u);
				if (wasMax)
				{
					ShowWindow(h, 3);
				}
				uint dpi = GetDpiForWindow(h);
				double sc = ((dpi == 0) ? 1.0 : ((double)dpi / 96.0));
				SnapManager.SetScale(sc, sc);
				SnapManager.Record(h, new Rectangle(nx, ny, nw, nh));
			}
		}
		catch (Exception ex)
		{
			Logger.Log("SnapService move: " + ex.Message);
		}
	}

	private static bool IsShellSurface(nint h)
	{
		StringBuilder sb = new StringBuilder(64);
		if (GetClassName(h, sb, sb.Capacity) == 0)
		{
			return false;
		}
		bool result;
		switch (sb.ToString())
		{
		case "Progman":
		case "WorkerW":
		case "Shell_TrayWnd":
		case "Shell_SecondaryTrayWnd":
		case "SHELLDLL_DefView":
			result = true;
			break;
		default:
			result = false;
			break;
		}
		return result;
	}

	[DllImport("user32.dll")]
	private static extern nint GetForegroundWindow();

	[DllImport("user32.dll")]
	private static extern bool GetWindowRect(nint h, out RECT r);

	[DllImport("user32.dll")]
	private static extern uint GetDpiForWindow(nint h);

	[DllImport("user32.dll", CharSet = CharSet.Unicode)]
	private static extern int GetClassName(nint h, StringBuilder buf, int max);

	[DllImport("user32.dll")]
	private static extern bool IsZoomed(nint h);

	[DllImport("user32.dll")]
	private static extern bool ShowWindow(nint h, int n);

	[DllImport("user32.dll")]
	private static extern bool SetWindowPos(nint h, nint after, int x, int y, int cx, int cy, uint flags);
}
