using System;
using System.Runtime.InteropServices;
using System.Text;

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

	// MSAA, not the managed UIA client: UIA keeps a QueueProcessor thread and WinEvent hooks on explorer alive forever.
	[DllImport("oleacc.dll")]
	private static extern int AccessibleObjectFromPoint(POINT pt, [MarshalAs(UnmanagedType.Interface)] out Accessibility.IAccessible acc, out object child);

	private const int ROLE_SYSTEM_LISTITEM = 34;

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
		if (!IsDesktop(sx, sy))
		{
			return false;
		}
		try
		{
			if (AccessibleObjectFromPoint(new POINT
			{
				X = sx,
				Y = sy
			}, out var acc, out object child) != 0 || acc == null)
			{
				return false;
			}
			try
			{
				return !(acc.get_accRole(child) is int role && role == ROLE_SYSTEM_LISTITEM);
			}
			finally
			{
				Marshal.ReleaseComObject(acc);
			}
		}
		catch
		{
			return false;
		}
	}
}
