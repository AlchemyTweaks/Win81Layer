using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;

namespace Win81Layer;

internal sealed class TrayDragGhost : IDisposable
{
	private struct POINT
	{
		public int X;

		public int Y;
	}

	private readonly Window _w;

	private const int GWL_EXSTYLE = -20;

	private const int WS_EX_TOOLWINDOW = 128;

	private const int WS_EX_TRANSPARENT = 32;

	private const int WS_EX_NOACTIVATE = 134217728;

	private const uint SWP_NOSIZE = 1u;

	private const uint SWP_NOZORDER = 4u;

	private const uint SWP_NOACTIVATE = 16u;

	public TrayDragGhost(ImageSource? icon)
	{
		Image img = new Image
		{
			Source = icon,
			Width = 18.0,
			Height = 18.0,
			Stretch = Stretch.Uniform
		};
		RenderOptions.SetBitmapScalingMode((DependencyObject)(object)img, BitmapScalingMode.HighQuality);
		_w = new Window
		{
			WindowStyle = WindowStyle.None,
			ResizeMode = ResizeMode.NoResize,
			ShowInTaskbar = false,
			AllowsTransparency = true,
			Background = Brushes.Transparent,
			Topmost = true,
			ShowActivated = false,
			Width = 26.0,
			Height = 26.0,
			Opacity = 0.85,
			WindowStartupLocation = WindowStartupLocation.Manual,
			Content = new Border
			{
				Background = new SolidColorBrush(Color.FromArgb(85, 0, 0, 0)),
				CornerRadius = new CornerRadius(3.0),
				Child = img
			}
		};
		_w.SourceInitialized += delegate
		{
			MakeClickThrough();
		};
		_w.Show();
		MoveToCursor();
	}

	public void MoveToCursor()
	{
		if (GetCursorPos(out var p))
		{
			nint h = new WindowInteropHelper(_w).Handle;
			if (h != IntPtr.Zero)
			{
				SetWindowPos(h, IntPtr.Zero, p.X + 14, p.Y + 14, 0, 0, 21u);
			}
		}
	}

	private void MakeClickThrough()
	{
		nint h = new WindowInteropHelper(_w).Handle;
		int ex = GetWindowLong(h, -20);
		SetWindowLong(h, -20, ex | 0x20 | 0x80 | 0x8000000);
	}

	public void Dispose()
	{
		try
		{
			_w.Close();
		}
		catch
		{
		}
	}

	[DllImport("user32.dll")]
	private static extern bool GetCursorPos(out POINT p);

	[DllImport("user32.dll")]
	private static extern bool SetWindowPos(nint h, nint after, int x, int y, int cx, int cy, uint flags);

	[DllImport("user32.dll")]
	private static extern int GetWindowLong(nint h, int i);

	[DllImport("user32.dll")]
	private static extern int SetWindowLong(nint h, int i, int v);
}
