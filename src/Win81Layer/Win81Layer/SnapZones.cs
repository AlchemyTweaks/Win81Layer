using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace Win81Layer;

internal static class SnapZones
{
	private const int SW_RESTORE = 9;

	private const uint SWP_NOZORDER = 4u;

	private const uint SWP_NOACTIVATE = 16u;

	public static Rectangle Compute(Point cursor, out string label)
	{
		Rectangle wa = TaskbarWorkArea.Current(Screen.FromPoint(cursor));
		int col = ((cursor.X >= wa.Left + wa.Width / 3) ? ((cursor.X < wa.Left + 2 * wa.Width / 3) ? 1 : 2) : 0);
		int row = ((cursor.Y >= wa.Top + wa.Height / 3) ? ((cursor.Y < wa.Top + 2 * wa.Height / 3) ? 1 : 2) : 0);
		int hw = wa.Width / 2;
		int hh = wa.Height / 2;
		Rectangle r;
		if (col == 0 && row == 0)
		{
			r = new Rectangle(wa.Left, wa.Top, hw, hh);
			label = "Top-left";
		}
		else if (col == 2 && row == 0)
		{
			r = new Rectangle(wa.Left + hw, wa.Top, wa.Width - hw, hh);
			label = "Top-right";
		}
		else if (col == 0 && row == 2)
		{
			r = new Rectangle(wa.Left, wa.Top + hh, hw, wa.Height - hh);
			label = "Bottom-left";
		}
		else if (col == 2 && row == 2)
		{
			r = new Rectangle(wa.Left + hw, wa.Top + hh, wa.Width - hw, wa.Height - hh);
			label = "Bottom-right";
		}
		else
		{
			switch (col)
			{
			case 0:
				r = new Rectangle(wa.Left, wa.Top, hw, wa.Height);
				label = "Left half";
				break;
			case 2:
				r = new Rectangle(wa.Left + hw, wa.Top, wa.Width - hw, wa.Height);
				label = "Right half";
				break;
			default:
				switch (row)
				{
				case 0:
					r = new Rectangle(wa.Left, wa.Top, wa.Width, hh);
					label = "Top half";
					break;
				case 2:
					r = new Rectangle(wa.Left, wa.Top + hh, wa.Width, wa.Height - hh);
					label = "Bottom half";
					break;
				default:
					r = wa;
					label = "Maximize";
					break;
				}
				break;
			}
		}
		return r;
	}

	public static void Apply(nint hwnd, Rectangle physical)
	{
		try
		{
			ShowWindow(hwnd, 9);
			SetWindowPos(hwnd, IntPtr.Zero, physical.Left, physical.Top, physical.Width, physical.Height, 20u);
		}
		catch
		{
		}
	}

	[DllImport("user32.dll")]
	private static extern bool ShowWindow(nint hWnd, int nCmdShow);

	[DllImport("user32.dll")]
	private static extern bool SetWindowPos(nint hWnd, nint after, int x, int y, int cx, int cy, uint flags);
}
