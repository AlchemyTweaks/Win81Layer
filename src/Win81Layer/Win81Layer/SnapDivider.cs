using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Forms;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace Win81Layer;

internal sealed class SnapDivider : Window
{
	private const int BarW = 8;

	private const int MinPx = 320;

	private double _sx;

	private double _sy;

	private nint _left;

	private nint _right;

	private System.Drawing.Rectangle _wa;

	private bool _dragging;

	private bool _parked;

	private readonly DispatcherTimer _guard;

	private const uint SWP = 20u;

	private const int GWL_EXSTYLE = -20;

	private const int WS_EX_TOOLWINDOW = 128;

	private const int WS_EX_NOACTIVATE = 134217728;

	private const int SW_MINIMIZE = 6;

	private const int SW_RESTORE = 9;

	public SnapDivider(double sx, double sy)
	{
		//IL_01c6: Unknown result type (might be due to invalid IL or missing references)
		//IL_01cb: Unknown result type (might be due to invalid IL or missing references)
		//IL_01e2: Expected O, but got Unknown
		_sx = ((sx <= 0.0) ? 1.0 : sx);
		_sy = ((sy <= 0.0) ? 1.0 : sy);
		base.WindowStyle = WindowStyle.None;
		base.ResizeMode = ResizeMode.NoResize;
		base.ShowInTaskbar = false;
		base.Topmost = true;
		base.ShowActivated = false;
		base.AllowsTransparency = true;
		base.Background = System.Windows.Media.Brushes.Transparent;
		base.WindowStartupLocation = WindowStartupLocation.Manual;
		base.Cursor = System.Windows.Input.Cursors.SizeWE;
		System.Windows.Media.Color dAcc;
		try
		{
			dAcc = StartAccent.Color();
		}
		catch
		{
			dAcc = System.Windows.Media.Color.FromRgb(77, 144, 254);
		}
		Border bar = new Border
		{
			// Win8.1 flat: near-opaque dark divider. Win7-Aero: translucent accent-tinted divider.
			Background = (ShellSkin.GlassOn
				? new SolidColorBrush(System.Windows.Media.Color.FromArgb(150, dAcc.R, dAcc.G, dAcc.B))
				: new SolidColorBrush(System.Windows.Media.Color.FromArgb(220, 32, 32, 32)))
		};
		StackPanel grip = new StackPanel
		{
			HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
			VerticalAlignment = VerticalAlignment.Center
		};
		for (int i = 0; i < 3; i++)
		{
			grip.Children.Add(new Ellipse
			{
				Width = 3.0,
				Height = 3.0,
				Margin = new Thickness(0.0, 2.0, 0.0, 2.0),
				Fill = new SolidColorBrush(System.Windows.Media.Color.FromArgb(204, byte.MaxValue, byte.MaxValue, byte.MaxValue))
			});
		}
		bar.Child = grip;
		base.Content = bar;
		base.MouseLeftButtonDown += delegate(object _, MouseButtonEventArgs e)
		{
			_dragging = true;
			CaptureMouse();
			e.Handled = true;
		};
		base.MouseMove += OnDrag;
		base.MouseLeftButtonUp += delegate
		{
			_dragging = false;
			ReleaseMouseCapture();
		};
		base.SourceInitialized += delegate
		{
			NoActivate();
		};
		_guard = new DispatcherTimer((DispatcherPriority)4)
		{
			Interval = TimeSpan.FromMilliseconds(400L)
		};
		_guard.Tick += delegate
		{
			GuardTick();
		};
	}

	public void Attach(nint left, nint right, System.Drawing.Rectangle wa, double sx, double sy)
	{
		_left = left;
		_right = right;
		_wa = wa;
		_parked = false;
		_sx = ((sx <= 0.0) ? 1.0 : sx);
		_sy = ((sy <= 0.0) ? 1.0 : sy);
		int boundary = wa.Left + wa.Width / 2;
		PositionAt(boundary);
		ShowWithFade();
		_guard.Start();
	}

	public void HideDivider()
	{
		_guard.Stop();
		if (base.IsVisible)
		{
			Hide();
		}
	}

	private void ShowWithFade()
	{
		if (base.IsVisible)
		{
			base.Topmost = true;
			return;
		}
		BeginAnimation(UIElement.OpacityProperty, null);
		if (Motion.Mode == MotionMode.Off)
		{
			base.Opacity = 1.0;
			Show();
			base.Topmost = true;
		}
		else
		{
			base.Opacity = 0.0;
			Show();
			base.Topmost = true;
			BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0.0, 1.0, Motion.Dur(Motion.Cat.Micro))
			{
				EasingFunction = Motion.Ease(Motion.Cat.Micro)
			});
		}
	}

	private void GuardTick()
	{
		if (!IsWindow(_left) || !IsWindow(_right))
		{
			_parked = false;
			HideDivider();
			SnapManager.DropPair(_wa);
			return;
		}
		bool lMin = IsIconic(_left);
		bool rMin = IsIconic(_right);
		if (lMin & rMin)
		{
			_parked = true;
			if (base.IsVisible)
			{
				Hide();
			}
			return;
		}
		if (lMin != rMin)
		{
			if (_parked)
			{
				ShowWindow(lMin ? _left : _right, 9);
				return;
			}
			ShowWindow(lMin ? _right : _left, 6);
			_parked = true;
			if (base.IsVisible)
			{
				Hide();
			}
			return;
		}
		_parked = false;
		SnapRect lr;
		SnapRect rr;
		if (IsZoomed(_left) || IsZoomed(_right))
		{
			HideDivider();
			SnapManager.DropPair(_wa);
		}
		else if (GetWindowRect(_left, out lr) && GetWindowRect(_right, out rr))
		{
			bool leftOk = Math.Abs(lr.Left - _wa.Left) <= 24 && Math.Abs(lr.Top - _wa.Top) <= 24 && Math.Abs(lr.Bottom - _wa.Bottom) <= 24;
			bool rightOk = Math.Abs(rr.Right - _wa.Right) <= 24 && Math.Abs(rr.Top - _wa.Top) <= 24 && Math.Abs(rr.Bottom - _wa.Bottom) <= 24;
			if (!leftOk || !rightOk)
			{
				HideDivider();
				SnapManager.DropPair(_wa);
			}
			else if (!base.IsVisible)
			{
				PositionAt(lr.Right);
				ShowWithFade();
			}
		}
	}

	private void PositionAt(int boundaryPhysicalX)
	{
		base.Left = ((double)boundaryPhysicalX - 4.0) / _sx;
		base.Top = (double)_wa.Top / _sy;
		base.Width = 8.0 / _sx;
		base.Height = (double)_wa.Height / _sy;
	}

	private void OnDrag(object sender, System.Windows.Input.MouseEventArgs e)
	{
		if (_dragging && e.LeftButton == MouseButtonState.Pressed)
		{
			int x = Math.Clamp(System.Windows.Forms.Cursor.Position.X, _wa.Left + 320, _wa.Right - 320);
			SetWindowPos(_left, IntPtr.Zero, _wa.Left, _wa.Top, x - _wa.Left, _wa.Height, 20u);
			SetWindowPos(_right, IntPtr.Zero, x, _wa.Top, _wa.Right - x, _wa.Height, 20u);
			PositionAt(x);
		}
	}

	private void NoActivate()
	{
		nint h = new WindowInteropHelper(this).Handle;
		int ex = GetWindowLong(h, -20);
		SetWindowLong(h, -20, ex | 0x80 | 0x8000000);
	}

	[DllImport("user32.dll")]
	private static extern bool SetWindowPos(nint h, nint after, int x, int y, int cx, int cy, uint flags);

	[DllImport("user32.dll")]
	private static extern int GetWindowLong(nint h, int i);

	[DllImport("user32.dll")]
	private static extern int SetWindowLong(nint h, int i, int v);

	[DllImport("user32.dll")]
	private static extern bool IsWindow(nint h);

	[DllImport("user32.dll")]
	private static extern bool IsIconic(nint h);

	[DllImport("user32.dll")]
	private static extern bool IsZoomed(nint h);

	[DllImport("user32.dll")]
	private static extern bool GetWindowRect(nint h, out SnapRect r);

	[DllImport("user32.dll")]
	private static extern bool ShowWindow(nint h, int n);
}
