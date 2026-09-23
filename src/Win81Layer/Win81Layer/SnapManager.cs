using System;
using System.Collections.Generic;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace Win81Layer;

internal static class SnapManager
{
	private static readonly Dictionary<string, (nint Left, nint Right, Rectangle Wa)> _pairs = new Dictionary<string, (nint, nint, Rectangle)>();

	private static readonly Dictionary<string, SnapDivider> _dividers = new Dictionary<string, SnapDivider>();

	private static double _sx = 1.0;

	private static double _sy = 1.0;

	public static void SetScale(double sx, double sy)
	{
		_sx = ((sx <= 0.0) ? 1.0 : sx);
		_sy = ((sy <= 0.0) ? 1.0 : sy);
	}

	public static void Record(nint hwnd, Rectangle target)
	{
		try
		{
			Rectangle wa = TaskbarWorkArea.Current(Screen.FromRectangle(target));
			string key = wa.ToString();
			bool halfHeight = Math.Abs(target.Height - wa.Height) < 24 && Math.Abs(target.Top - wa.Top) < 24;
			bool halfWidth = Math.Abs(target.Width - wa.Width / 2) < 24;
			if (!halfHeight || !halfWidth)
			{
				HideFor(key);
				return;
			}
			(nint, nint, Rectangle) pair = (_pairs.TryGetValue(key, out (nint, nint, Rectangle) p) ? p : (IntPtr.Zero, IntPtr.Zero, wa));
			pair.Item3 = wa;
			if (Math.Abs(target.Left - wa.Left) < 24)
			{
				pair.Item1 = hwnd;
				if (pair.Item2 == hwnd)
				{
					pair.Item2 = IntPtr.Zero;
				}
			}
			else
			{
				pair.Item2 = hwnd;
				if (pair.Item1 == hwnd)
				{
					pair.Item1 = IntPtr.Zero;
				}
			}
			_pairs[key] = pair;
			if (pair.Item1 != IntPtr.Zero && pair.Item2 != IntPtr.Zero && IsWindow(pair.Item1) && IsWindow(pair.Item2) && !IsIconic(pair.Item1) && !IsIconic(pair.Item2))
			{
				if (!_dividers.TryGetValue(key, out SnapDivider d))
				{
					d = new SnapDivider(_sx, _sy);
					_dividers[key] = d;
				}
				d.Attach(pair.Item1, pair.Item2, wa, _sx, _sy);
			}
			else
			{
				HideFor(key);
			}
		}
		catch
		{
		}
	}

	internal static void DropPair(Rectangle wa)
	{
		string key = wa.ToString();
		_pairs.Remove(key);
		HideFor(key);
	}

	private static void HideFor(string key)
	{
		if (_dividers.TryGetValue(key, out SnapDivider d))
		{
			d.HideDivider();
		}
	}

	[DllImport("user32.dll")]
	private static extern bool IsWindow(nint h);

	[DllImport("user32.dll")]
	private static extern bool IsIconic(nint h);
}
