using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace Win81Layer;

public sealed class SnapAssist : Window
{
	private struct DWM_TP
	{
		public int dwFlags;

		public SnapRect rc;

		public SnapRect rcSource;

		public byte opacity;

		[MarshalAs(UnmanagedType.Bool)]
		public bool fVisible;

		[MarshalAs(UnmanagedType.Bool)]
		public bool fSourceOnly;
	}

	private readonly WrapPanel _list;

	private readonly List<nint> _thumbs = new List<nint>();

	private readonly List<(Border Box, nint Hwnd)> _boxes = new List<(Border, nint)>();

	private nint _hwnd;

	private double _sx = 1.0;

	private double _sy = 1.0;

	private SnapRect _fillPx;

	private bool _hiding;

	public bool IsShown => base.IsVisible && !_hiding;

	public SnapAssist()
	{
		base.WindowStyle = WindowStyle.None;
		base.ResizeMode = ResizeMode.NoResize;
		base.ShowInTaskbar = false;
		base.Topmost = true;
		base.ShowActivated = false;
		base.AllowsTransparency = false;
		// Opaque window (keep AllowsTransparency=false). Win7-Aero: a dark accent-tinted "smoked" ground; flat: neutral dark.
		base.Background = new SolidColorBrush(ShellSkin.GlassOn ? ShellSkin.AccentTone(0.14) : System.Windows.Media.Color.FromRgb(26, 26, 26));
		base.UseLayoutRounding = true;
		base.Title = "Snap Assist";
		_list = new WrapPanel
		{
			HorizontalAlignment = HorizontalAlignment.Center,
			VerticalAlignment = VerticalAlignment.Center
		};
		base.Content = new Grid
		{
			Children = { (UIElement)_list }
		};
		base.Loaded += delegate
		{
			_hwnd = new WindowInteropHelper(this).Handle;
		};
		base.PreviewKeyDown += delegate(object _, KeyEventArgs e)
		{
			//IL_0002: Unknown result type (might be due to invalid IL or missing references)
			//IL_0009: Invalid comparison between Unknown and I4
			if ((int)e.Key == 13)
			{
				HideAssist();
			}
		};
		base.MouseLeftButtonDown += delegate(object _, MouseButtonEventArgs e)
		{
			object originalSource = e.OriginalSource;
			if (!IsInsideEntry((DependencyObject?)((originalSource is DependencyObject) ? originalSource : null)))
			{
				HideAssist();
			}
		};
	}

	private static bool IsInsideEntry(DependencyObject? d)
	{
		for (DependencyObject cur = d; cur != null; cur = VisualTreeHelper.GetParent(cur))
		{
			if (cur is Border { Tag: var tag } && tag is nint)
			{
				return true;
			}
		}
		return false;
	}

	public void ShowFor(SnapRect areaPx, List<nint> others)
	{
		//IL_006c: Unknown result type (might be due to invalid IL or missing references)
		//IL_0071: Unknown result type (might be due to invalid IL or missing references)
		//IL_00bc: Unknown result type (might be due to invalid IL or missing references)
		//IL_00c1: Unknown result type (might be due to invalid IL or missing references)
		if (others.Count == 0)
		{
			HideAssist();
			return;
		}
		_fillPx = areaPx;
		_hiding = false;
		Show();
		PresentationSource src = PresentationSource.FromVisual(this);
		double? num;
		Matrix transformToDevice;
		if (src == null)
		{
			num = null;
		}
		else
		{
			CompositionTarget compositionTarget = src.CompositionTarget;
			if (compositionTarget == null)
			{
				num = null;
			}
			else
			{
				transformToDevice = compositionTarget.TransformToDevice;
				num = transformToDevice.M11;
			}
		}
		_sx = num ?? 1.0;
		double? num2;
		if (src == null)
		{
			num2 = null;
		}
		else
		{
			CompositionTarget compositionTarget2 = src.CompositionTarget;
			if (compositionTarget2 == null)
			{
				num2 = null;
			}
			else
			{
				transformToDevice = compositionTarget2.TransformToDevice;
				num2 = transformToDevice.M22;
			}
		}
		_sy = num2 ?? 1.0;
		if (_sx <= 0.0)
		{
			_sx = 1.0;
		}
		if (_sy <= 0.0)
		{
			_sy = 1.0;
		}
		base.Left = (double)areaPx.Left / _sx;
		base.Top = (double)areaPx.Top / _sy;
		base.Width = (double)(areaPx.Right - areaPx.Left) / _sx;
		base.Height = (double)(areaPx.Bottom - areaPx.Top) / _sy;
		Unregister();
		_list.Children.Clear();
		_boxes.Clear();
		SolidColorBrush accent = new SolidColorBrush(SafeAccent());
		double bw = Math.Min(260.0, Math.Max(160.0, (base.Width - 80.0) / 2.0));
		foreach (nint hwnd in others)
		{
			double bh = ThumbHeight(hwnd, bw);
			Border thumbBox = new Border
			{
				Width = bw,
				Height = bh,
				Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(42, 42, 42))
			};
			Grid label = new Grid
			{
				Width = bw,
				Height = 28.0,
				Background = new SolidColorBrush(System.Windows.Media.Color.FromArgb(240, 0, 0, 0))
			};
			StackPanel row = new StackPanel
			{
				Orientation = Orientation.Horizontal,
				VerticalAlignment = VerticalAlignment.Center,
				Margin = new Thickness(8.0, 0.0, 8.0, 0.0)
			};
			ImageSource icon = WindowList.GetIcon(hwnd, preferLarge: false);
			if (icon != null)
			{
				row.Children.Add(new System.Windows.Controls.Image
				{
					Source = icon,
					Width = 16.0,
					Height = 16.0,
					Margin = new Thickness(0.0, 0.0, 8.0, 0.0)
				});
			}
			row.Children.Add(new TextBlock
			{
				Text = WindowList.GetTitle(hwnd),
				Foreground = System.Windows.Media.Brushes.White,
				FontFamily = new System.Windows.Media.FontFamily("Segoe UI"),
				FontSize = 13.0,
				VerticalAlignment = VerticalAlignment.Center,
				MaxWidth = bw - 40.0,
				TextTrimming = TextTrimming.CharacterEllipsis
			});
			label.Children.Add(row);
			StackPanel stack = new StackPanel();
			stack.Children.Add(thumbBox);
			stack.Children.Add(label);
			Border entry = new Border
			{
				Margin = new Thickness(10.0),
				BorderBrush = System.Windows.Media.Brushes.Transparent,
				BorderThickness = new Thickness(2.0),
				Cursor = Cursors.Hand,
				Child = stack,
				Tag = hwnd
			};
			entry.MouseEnter += delegate
			{
				entry.BorderBrush = accent;
			};
			entry.MouseLeave += delegate
			{
				entry.BorderBrush = System.Windows.Media.Brushes.Transparent;
			};
			nint h = hwnd;
			entry.MouseLeftButtonUp += delegate
			{
				Pick(h);
			};
			_list.Children.Add(entry);
			_boxes.Add((thumbBox, hwnd));
		}
		UpdateLayout();
		((DispatcherObject)this).Dispatcher.BeginInvoke((Delegate)new Action(Register), (DispatcherPriority)6, Array.Empty<object>());
		TranslateTransform t = new TranslateTransform(0.0, 24.0);
		((UIElement)base.Content).RenderTransform = t;
		((UIElement)base.Content).Opacity = 0.0;
		t.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(24.0, 0.0, Motion.Dur(Motion.Cat.Enter))
		{
			EasingFunction = Motion.Ease(Motion.Cat.Enter)
		});
		((UIElement)base.Content).BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0.0, 1.0, Motion.Dur(Motion.Cat.Enter))
		{
			EasingFunction = Motion.Ease(Motion.Cat.Enter)
		});
	}

	private void Pick(nint hwnd)
	{
		try
		{
			Rectangle rect = new Rectangle(_fillPx.Left, _fillPx.Top, _fillPx.Right - _fillPx.Left, _fillPx.Bottom - _fillPx.Top);
			SnapZones.Apply(hwnd, rect);
			SnapManager.SetScale(_sx, _sy);
			SnapManager.Record(hwnd, rect);
			WindowList.Activate(hwnd);
		}
		catch (Exception ex)
		{
			Logger.Log("Snap fill: " + ex.Message);
		}
		HideAssist();
	}

	public void HideAssist()
	{
		if (!_hiding && base.IsVisible)
		{
			_hiding = true;
			Unregister();
			Hide();
			_hiding = false;
		}
	}

	private void Register()
	{
		//IL_006b: Unknown result type (might be due to invalid IL or missing references)
		//IL_0070: Unknown result type (might be due to invalid IL or missing references)
		//IL_0075: Unknown result type (might be due to invalid IL or missing references)
		foreach (var (box, srcHwnd) in _boxes)
		{
			try
			{
				if (DwmRegisterThumbnail(_hwnd, srcHwnd, out var thumb) == 0)
				{
					_thumbs.Add(thumb);
					System.Windows.Point tl = box.TransformToVisual(this).Transform(new System.Windows.Point(0.0, 0.0));
					SnapRect rc = new SnapRect
					{
						Left = (int)Math.Round(tl.X * _sx),
						Top = (int)Math.Round(tl.Y * _sy),
						Right = (int)Math.Round((tl.X + box.ActualWidth) * _sx),
						Bottom = (int)Math.Round((tl.Y + box.ActualHeight) * _sy)
					};
					DWM_TP props = new DWM_TP
					{
						dwFlags = 29,
						rc = rc,
						opacity = byte.MaxValue,
						fVisible = true
					};
					DwmUpdateThumbnailProperties(thumb, ref props);
				}
			}
			catch
			{
			}
		}
	}

	private void Unregister()
	{
		foreach (nint t in _thumbs)
		{
			try
			{
				DwmUnregisterThumbnail(t);
			}
			catch
			{
			}
		}
		_thumbs.Clear();
	}

	private static double ThumbHeight(nint hwnd, double w)
	{
		try
		{
			if (GetWindowRect(hwnd, out var r))
			{
				double sw = r.Right - r.Left;
				double sh = r.Bottom - r.Top;
				if (sw > 0.0 && sh > 0.0)
				{
					return Math.Clamp(w * sh / sw, 110.0, 200.0);
				}
			}
		}
		catch
		{
		}
		return w * 0.6;
	}

	public void OfferFor(SnapRect emptyPx, nint exclude)
	{
		try
		{
			uint own = (uint)Environment.ProcessId;
			List<nint> others = new List<nint>();
			bool occupied = false;
			foreach (var item in WindowList.Enumerate(IntPtr.Zero))
			{
				nint h = item.Hwnd;
				if (h == exclude)
				{
					continue;
				}
				GetWindowThreadProcessId(h, out var hp);
				if (hp == own)
				{
					continue;
				}
				if (!IsIconic(h) && GetWindowRect(h, out var r))
				{
					int cx = (r.Left + r.Right) / 2;
					int cy = (r.Top + r.Bottom) / 2;
					if (cx >= emptyPx.Left && cx < emptyPx.Right && cy >= emptyPx.Top && cy < emptyPx.Bottom)
					{
						occupied = true;
						continue;
					}
				}
				others.Add(h);
			}
			if (occupied || others.Count == 0)
			{
				HideAssist();
			}
			else
			{
				ShowFor(emptyPx, others);
			}
		}
		catch (Exception ex)
		{
			Logger.Log("OfferFor: " + ex.Message);
		}
	}

	private static System.Windows.Media.Color SafeAccent()
	{
		try
		{
			return StartAccent.Color();
		}
		catch
		{
			return System.Windows.Media.Color.FromRgb(42, 125, 225);
		}
	}

	protected override void OnClosing(CancelEventArgs e)
	{
		e.Cancel = true;
		HideAssist();
	}

	[DllImport("dwmapi.dll")]
	private static extern int DwmRegisterThumbnail(nint dest, nint src, out nint thumb);

	[DllImport("dwmapi.dll")]
	private static extern int DwmUnregisterThumbnail(nint thumb);

	[DllImport("dwmapi.dll")]
	private static extern int DwmUpdateThumbnailProperties(nint thumb, ref DWM_TP props);

	[DllImport("user32.dll")]
	private static extern bool GetWindowRect(nint hwnd, out SnapRect r);

	[DllImport("user32.dll")]
	private static extern uint GetWindowThreadProcessId(nint h, out uint pid);

	[DllImport("user32.dll")]
	private static extern bool IsIconic(nint h);
}
