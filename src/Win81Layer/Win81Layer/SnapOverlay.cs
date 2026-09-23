using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;

namespace Win81Layer;

internal sealed class SnapOverlay : Window
{
	private readonly Border _box;

	private readonly double _sx;

	private readonly double _sy;

	private const int GWL_EXSTYLE = -20;

	private const int WS_EX_TRANSPARENT = 32;

	private const int WS_EX_TOOLWINDOW = 128;

	private const int WS_EX_NOACTIVATE = 134217728;

	public SnapOverlay(double scaleX, double scaleY)
	{
		_sx = ((scaleX <= 0.0) ? 1.0 : scaleX);
		_sy = ((scaleY <= 0.0) ? 1.0 : scaleY);
		base.WindowStyle = WindowStyle.None;
		base.ResizeMode = ResizeMode.NoResize;
		base.ShowInTaskbar = false;
		base.Topmost = true;
		base.ShowActivated = false;
		base.AllowsTransparency = true;
		base.Background = System.Windows.Media.Brushes.Transparent;
		base.WindowStartupLocation = WindowStartupLocation.Manual;
		base.IsHitTestVisible = false;
		_box = new Border
		{
			BorderThickness = new Thickness(2.0),
			CornerRadius = new CornerRadius(3.0),
			Margin = new Thickness(6.0)
		};
		base.Content = _box;
		base.SourceInitialized += delegate
		{
			MakeClickThrough();
		};
	}

	public void ShowZone(Rectangle physical, System.Windows.Media.Color accent)
	{
		base.Left = (double)physical.Left / _sx;
		base.Top = (double)physical.Top / _sy;
		base.Width = Math.Max(1.0, (double)physical.Width / _sx);
		base.Height = Math.Max(1.0, (double)physical.Height / _sy);
		bool glass = ShellSkin.GlassOn;
		// Win8.1 flat: square, stronger fill, solid accent border. Win7-Aero: rounded, softer fill + glow border.
		_box.CornerRadius = new CornerRadius(glass ? 3.0 : 0.0);
		_box.Background = new SolidColorBrush(System.Windows.Media.Color.FromArgb((byte)(glass ? 64 : 96), accent.R, accent.G, accent.B));
		_box.BorderBrush = new SolidColorBrush(System.Windows.Media.Color.FromArgb((byte)(glass ? 216 : 255), accent.R, accent.G, accent.B));
		if (!base.IsVisible)
		{
			Show();
		}
	}

	public void HideZone()
	{
		if (base.IsVisible)
		{
			Hide();
		}
	}

	private void MakeClickThrough()
	{
		nint h = new WindowInteropHelper(this).Handle;
		int ex = GetWindowLong(h, -20);
		SetWindowLong(h, -20, ex | 0x20 | 0x80 | 0x8000000);
	}

	[DllImport("user32.dll")]
	private static extern int GetWindowLong(nint h, int i);

	[DllImport("user32.dll")]
	private static extern int SetWindowLong(nint h, int i, int v);
}
