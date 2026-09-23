using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Forms;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;

namespace Win81Layer;

internal sealed class LockWindow : Window
{
	private struct POINT
	{
		public int X;

		public int Y;
	}

	private readonly TranslateTransform _slide = new TranslateTransform(0.0, 0.0);

	private readonly TextBlock _time = new TextBlock();

	private readonly TextBlock _date = new TextBlock();

	private readonly StackPanel _status = new StackPanel
	{
		Orientation = System.Windows.Controls.Orientation.Horizontal
	};

	private readonly Grid _bgGrid = new Grid
	{
		Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(27, 42, 74))
	};

	private readonly bool _primary;

	private readonly int _guardUntil = Environment.TickCount + 350;

	private bool _dragging;

	private bool _closing;

	private System.Windows.Point _dragStart;

	private const uint MONITOR_DEFAULTTONEAREST = 2u;

	private const int MDT_EFFECTIVE_DPI = 0;

	public event Action? Dismissed;

	public LockWindow(bool primary)
	{
		_primary = primary;
		base.WindowStyle = WindowStyle.None;
		base.ResizeMode = ResizeMode.NoResize;
		base.ShowInTaskbar = false;
		base.AllowsTransparency = false;
		base.Background = System.Windows.Media.Brushes.Black;
		base.Topmost = true;
		base.WindowStartupLocation = WindowStartupLocation.Manual;
		base.ShowActivated = primary;
		Grid root = new Grid
		{
			RenderTransform = _slide
		};
		root.Children.Add(_bgGrid);
		root.Children.Add(new Grid
		{
			Background = new LinearGradientBrush(new GradientStopCollection
			{
				new GradientStop(System.Windows.Media.Color.FromArgb(0, 0, 0, 0), 0.5),
				new GradientStop(System.Windows.Media.Color.FromArgb(153, 0, 0, 0), 1.0)
			}, 90.0)
		});
		if (primary)
		{
			_time.FontFamily = new System.Windows.Media.FontFamily("Segoe UI Light");
			_time.FontSize = 120.0;
			_time.Foreground = System.Windows.Media.Brushes.White;
			_time.Effect = new DropShadowEffect
			{
				BlurRadius = 14.0,
				ShadowDepth = 0.0,
				Opacity = 0.55,
				Color = Colors.Black
			};
			_date.FontFamily = new System.Windows.Media.FontFamily("Segoe UI Semilight");
			_date.FontSize = 34.0;
			_date.Foreground = System.Windows.Media.Brushes.White;
			_date.Margin = new Thickness(4.0, -8.0, 0.0, 0.0);
			_status.Margin = new Thickness(6.0, 20.0, 0.0, 0.0);
			StackPanel stack = new StackPanel
			{
				HorizontalAlignment = System.Windows.HorizontalAlignment.Left,
				VerticalAlignment = VerticalAlignment.Bottom,
				Margin = new Thickness(48.0, 0.0, 0.0, 54.0)
			};
			stack.Children.Add(_time);
			stack.Children.Add(_date);
			stack.Children.Add(_status);
			root.Children.Add(stack);
			root.Children.Add(new TextBlock
			{
				Text = "Press any key or click to unlock",
				FontFamily = new System.Windows.Media.FontFamily("Segoe UI"),
				FontSize = 13.0,
				Foreground = new SolidColorBrush(System.Windows.Media.Color.FromArgb(204, byte.MaxValue, byte.MaxValue, byte.MaxValue)),
				HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
				VerticalAlignment = VerticalAlignment.Bottom,
				Margin = new Thickness(0.0, 0.0, 0.0, 22.0)
			});
		}
		base.Content = root;
		base.KeyDown += delegate
		{
			TryDismiss();
		};
		base.MouseLeftButtonDown += delegate(object _, MouseButtonEventArgs e)
		{
			//IL_000b: Unknown result type (might be due to invalid IL or missing references)
			//IL_0010: Unknown result type (might be due to invalid IL or missing references)
			_dragging = true;
			_dragStart = e.GetPosition(this);
			CaptureMouse();
		};
		base.MouseMove += OnMove;
		base.MouseLeftButtonUp += delegate(object _, MouseButtonEventArgs e)
		{
			//IL_000b: Unknown result type (might be due to invalid IL or missing references)
			//IL_0010: Unknown result type (might be due to invalid IL or missing references)
			int num;
			if (_dragging)
			{
				System.Windows.Point position = e.GetPosition(this);
				num = ((Math.Abs(position.Y - _dragStart.Y) < 8.0) ? 1 : 0);
			}
			else
			{
				num = 0;
			}
			bool flag = (byte)num != 0;
			ReleaseMouseCapture();
			if (flag)
			{
				TryDismiss();
			}
		};
		base.LostMouseCapture += delegate
		{
			_dragging = false;
			_slide.Y = 0.0;
		};
	}

	private void OnMove(object sender, System.Windows.Input.MouseEventArgs e)
	{
		//IL_0012: Unknown result type (might be due to invalid IL or missing references)
		//IL_0017: Unknown result type (might be due to invalid IL or missing references)
		if (_dragging)
		{
			System.Windows.Point position = e.GetPosition(this);
			double dy = position.Y - _dragStart.Y;
			if (dy < -80.0)
			{
				_dragging = false;
				ReleaseMouseCapture();
				TryDismiss();
			}
			else
			{
				_slide.Y = ((dy < 0.0) ? dy : 0.0);
			}
		}
	}

	private void TryDismiss()
	{
		if (Environment.TickCount >= _guardUntil)
		{
			Dismissed?.Invoke();
		}
	}

	public void PlaceOn(Screen screen)
	{
		Show();
		Rectangle b = screen.Bounds;
		double sx = 1.0;
		double sy = 1.0;
		try
		{
			POINT c = new POINT
			{
				X = b.Left + b.Width / 2,
				Y = b.Top + b.Height / 2
			};
			nint hmon = MonitorFromPoint(c, 2u);
			if (hmon != IntPtr.Zero && GetDpiForMonitor(hmon, 0, out var dx, out var dy) == 0 && dx != 0 && dy != 0)
			{
				sx = (double)dx / 96.0;
				sy = (double)dy / 96.0;
			}
		}
		catch
		{
		}
		base.Left = (double)b.Left / sx;
		base.Top = (double)b.Top / sy;
		base.Width = (double)b.Width / sx;
		base.Height = (double)b.Height / sy;
		base.Topmost = true;
	}

	public void SetBackground(ImageSource img)
	{
		_bgGrid.Background = new ImageBrush(img)
		{
			Stretch = Stretch.UniformToFill
		};
		_bgGrid.BeginAnimation(UIElement.OpacityProperty, null);
		_bgGrid.Opacity = 0.0;
		_bgGrid.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0.0, 1.0, Motion.Dur(Motion.Cat.Micro)));
	}

	[DllImport("user32.dll")]
	private static extern nint MonitorFromPoint(POINT pt, uint flags);

	[DllImport("shcore.dll")]
	private static extern int GetDpiForMonitor(nint hmon, int dpiType, out uint dpiX, out uint dpiY);

	public void UpdateClock()
	{
		DateTime now = DateTime.Now;
		_time.Text = now.ToString("HH:mm");
		_date.Text = now.ToString("dddd, MMMM d");
	}

	public void SetNetwork(NetState81 s)
	{
		if (!_primary)
		{
			return;
		}
		string label = s.Kind switch
		{
			NetKind.Wifi or NetKind.Cellular => s.Label,
			NetKind.Ethernet => "Ethernet",
			NetKind.Airplane => "Airplane mode",
			_ => s.Status
		};
		_status.Children.Add(StatusImage(NetIcons81.For(s, 32, Colors.White), label));
	}

	public void SetBattery((bool present, int percent, bool charging, bool saver) p)
	{
		if (_primary && p.present)
		{
			_status.Children.Add(StatusGlyph(p.charging ? 59454 : 59455, $"{p.percent}%"));
		}
	}

	public void SetMail(int unread)
	{
		if (_primary && unread > 0)
		{
			_status.Children.Add(StatusGlyph(59157, unread.ToString()));
		}
	}

	private static UIElement StatusGlyph(int mdl2, string text)
	{
		StackPanel sp = new StackPanel
		{
			Orientation = System.Windows.Controls.Orientation.Horizontal,
			Margin = new Thickness(0.0, 0.0, 24.0, 0.0)
		};
		sp.Children.Add(new TextBlock
		{
			Text = ((char)mdl2).ToString(),
			FontFamily = new System.Windows.Media.FontFamily("Segoe MDL2 Assets"),
			FontSize = 18.0,
			Foreground = System.Windows.Media.Brushes.White,
			VerticalAlignment = VerticalAlignment.Center
		});
		if (!string.IsNullOrEmpty(text))
		{
			sp.Children.Add(Label(text));
		}
		return sp;
	}

	private static UIElement StatusImage(ImageSource img, string text)
	{
		StackPanel sp = new StackPanel
		{
			Orientation = System.Windows.Controls.Orientation.Horizontal,
			Margin = new Thickness(0.0, 0.0, 24.0, 0.0)
		};
		sp.Children.Add(new System.Windows.Controls.Image
		{
			Source = img,
			Width = 22.0,
			Height = 22.0,
			VerticalAlignment = VerticalAlignment.Center
		});
		if (!string.IsNullOrEmpty(text))
		{
			sp.Children.Add(Label(text));
		}
		return sp;
	}

	private static TextBlock Label(string text)
	{
		return new TextBlock
		{
			Text = text,
			FontFamily = new System.Windows.Media.FontFamily("Segoe UI"),
			FontSize = 15.0,
			Foreground = System.Windows.Media.Brushes.White,
			Margin = new Thickness(6.0, 0.0, 0.0, 0.0),
			VerticalAlignment = VerticalAlignment.Center
		};
	}

	public void DismissAndClose()
	{
		if (_closing)
		{
			return;
		}
		_closing = true;
		try
		{
			Duration dur = Motion.Dur(Motion.Cat.Exit);
			DoubleAnimation up = new DoubleAnimation(_slide.Y, 0.0 - Math.Max(base.Height, 100.0), dur)
			{
				EasingFunction = Motion.Ease(Motion.Cat.Exit)
			};
			up.Completed += delegate
			{
				try
				{
					Close();
				}
				catch
				{
				}
			};
			_slide.BeginAnimation(TranslateTransform.YProperty, up);
			BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(1.0, 0.0, dur));
		}
		catch
		{
			try
			{
				Close();
			}
			catch
			{
			}
		}
	}
}
