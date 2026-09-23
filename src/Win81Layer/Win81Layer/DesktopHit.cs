using System;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Automation;

namespace Win81Layer;

internal static class DesktopHit
{
	private struct POINT
	{
		public int X;

		public int Y;
	}

	private const uint GA_ROOT = 2u;

	[DllImport("user32.dll")]
	private static extern nint WindowFromPoint(POINT p);

	[DllImport("user32.dll")]
	private static extern nint GetAncestor(nint h, uint flags);

	[DllImport("user32.dll", CharSet = CharSet.Unicode)]
	private static extern int GetClassName(nint h, StringBuilder buf, int max);

	internal static bool IsDesktop(int sx, int sy)
	{
		nint h = WindowFromPoint(new POINT
		{
			X = sx,
			Y = sy
		});
		if (h == IntPtr.Zero)
		{
			return false;
		}
		nint root = GetAncestor(h, 2u);
		if (root == IntPtr.Zero)
		{
			return false;
		}
		StringBuilder sb = new StringBuilder(64);
		GetClassName(root, sb, sb.Capacity);
		string text = sb.ToString();
		return (text == "Progman" || text == "WorkerW") ? true : false;
	}

	internal static bool IsEmptyBackground(int sx, int sy)
	{
		//IL_0019: Unknown result type (might be due to invalid IL or missing references)
		if (!IsDesktop(sx, sy))
		{
			return false;
		}
		try
		{
			AutomationElement el = AutomationElement.FromPoint(new Point((double)sx, (double)sy));
			if ((object)el == null)
			{
				return false;
			}
			return el.Current.ControlType != ControlType.ListItem;
		}
		catch
		{
			return false;
		}
	}

	internal static void Warm()
	{
		//IL_0014: Unknown result type (might be due to invalid IL or missing references)
		try
		{
			_ = AutomationElement.FromPoint(new Point(0.0, 0.0))?.Current.ControlType;
		}
		catch
		{
		}
	}
}
