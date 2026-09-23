using System;
using System.Collections.Generic;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Forms;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Threading;
using Windows.Media.Control;

namespace Win81Layer;

public partial class ThumbnailPreview : Window, IComponentConnector
{
	private struct RECT
	{
		public int Left;

		public int Top;

		public int Right;

		public int Bottom;
	}

	private struct DWM_THUMBNAIL_PROPERTIES
	{
		public int dwFlags;

		public RECT rcDestination;

		public RECT rcSource;

		public byte opacity;

		[MarshalAs(UnmanagedType.Bool)]
		public bool fVisible;

		[MarshalAs(UnmanagedType.Bool)]
		public bool fSourceClientAreaOnly;
	}

	private readonly List<nint> _thumbs = new List<nint>();

	private readonly List<(Border Box, nint Hwnd)> _boxes = new List<(Border, nint)>();

	private nint _hwnd;

	private const int DWM_TNP_RECTDESTINATION = 1;

	private const int DWM_TNP_VISIBLE = 8;

	private const int DWM_TNP_OPACITY = 4;

	private const int DWM_TNP_SOURCECLIENTAREAONLY = 16;

	public bool IsHovering { get; private set; }

	public event Action? RequestHide;

	public ThumbnailPreview()
	{
		InitializeComponent();
		base.Loaded += delegate
		{
			_hwnd = new WindowInteropHelper(this).Handle;
			try
			{
				base.Resources["WindowAccent"] = new SolidColorBrush(StartAccent.Color());
			}
			catch
			{
			}
		};
		base.MouseEnter += delegate
		{
			IsHovering = true;
		};
		base.MouseLeave += delegate
		{
			IsHovering = false;
			RequestHide?.Invoke();
		};
	}

	public void ShowFor(List<(nint Hwnd, ImageSource? Icon, string Title)> items, Rect anchorPx, double dpiX, double dpiY, string edge = "Bottom")
	{
		Unregister();
		Cells.Children.Clear();
		_boxes.Clear();
		if (items.Count == 0)
		{
			Hide();
			return;
		}
		try
		{
			System.Windows.Media.Color cc = TaskbarTheme.ChromeColor();
			System.Windows.Media.Brush chrome;
			if (ShellSkin.GlassOn)
			{
				// Win7-Aero preview: a subtle top-lit accent gradient (opaque — this window stays non-transparent).
				chrome = new LinearGradientBrush(ColorMath.Lighten(cc, 0.18), System.Windows.Media.Color.FromRgb(cc.R, cc.G, cc.B), 90.0);
			}
			else
			{
				chrome = new SolidColorBrush(System.Windows.Media.Color.FromRgb(cc.R, cc.G, cc.B));
			}
			((Freezable)chrome).Freeze();
			base.Background = chrome;
			base.Resources["WindowAccent"] = new SolidColorBrush(StartAccent.Color());
		}
		catch
		{
		}
		// Per-cell hover highlight: faint-white border by default, ACCENT border on hover so the window you're about
		// to activate is obvious. COLOR-ONLY (BorderThickness stays 1px) — the DWM live thumbnail is registered to a
		// fixed pixel rect and must never move, so we must not change the box's layout on hover. Built once from the
		// current accent and shared by every cell.
		System.Windows.Style boxStyle = new System.Windows.Style(typeof(Border));
		try
		{
			SolidColorBrush faintBorder = new SolidColorBrush(System.Windows.Media.Color.FromArgb(64, byte.MaxValue, byte.MaxValue, byte.MaxValue));
			((Freezable)faintBorder).Freeze();
			SolidColorBrush hoverBorder = new SolidColorBrush(ColorMath.Lighten(StartAccent.Color(), 0.15));
			((Freezable)hoverBorder).Freeze();
			boxStyle.Setters.Add(new Setter(Border.BorderBrushProperty, faintBorder));
			Trigger hov = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
			hov.Setters.Add(new Setter(Border.BorderBrushProperty, hoverBorder));
			boxStyle.Triggers.Add(hov);
		}
		catch
		{
		}
		foreach (var it in items)
		{
			StackPanel cell = new StackPanel
			{
				Margin = new Thickness(3.0, 0.0, 3.0, 0.0)
			};
			Grid header = new Grid
			{
				Height = 24.0,
				Width = 208.0
			};
			StackPanel hp = new StackPanel
			{
				Orientation = System.Windows.Controls.Orientation.Horizontal,
				VerticalAlignment = VerticalAlignment.Center,
				Margin = new Thickness(3.0, 0.0, 0.0, 0.0)
			};
			if (it.Icon != null)
			{
				hp.Children.Add(new System.Windows.Controls.Image
				{
					Source = it.Icon,
					Width = 16.0,
					Height = 16.0,
					Margin = new Thickness(0.0, 0.0, 6.0, 0.0)
				});
			}
			hp.Children.Add(new TextBlock
			{
				Text = it.Title,
				Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(240, 240, 240)),
				FontFamily = new System.Windows.Media.FontFamily("Segoe UI"),
				FontSize = 12.0,
				VerticalAlignment = VerticalAlignment.Center,
				TextTrimming = TextTrimming.CharacterEllipsis,
				MaxWidth = 150.0
			});
			header.Children.Add(hp);
			nint hwndC = it.Hwnd;
			System.Windows.Controls.Button close = new System.Windows.Controls.Button
			{
				Content = "\ue8bb",
				FontFamily = new System.Windows.Media.FontFamily("Segoe MDL2 Assets"),
				FontSize = 9.0,
				Width = 22.0,
				Height = 22.0,
				HorizontalAlignment = System.Windows.HorizontalAlignment.Right,
				Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(208, 208, 208)),
				Cursor = System.Windows.Input.Cursors.Hand,
				Background = System.Windows.Media.Brushes.Transparent,
				BorderThickness = new Thickness(0.0),
				FocusVisualStyle = null,
				Template = CloseButtonTemplate()
			};
			close.Click += delegate
			{
				WindowList.Close(hwndC);
				RequestHide?.Invoke();
			};
			header.Children.Add(close);
			cell.Children.Add(header);
			(double W, double H) tuple = ThumbBox(it.Hwnd);
			double bw = tuple.W;
			double bh = tuple.H;
			Border box = new Border
			{
				Width = bw,
				Height = bh,
				Background = System.Windows.Media.Brushes.Transparent,
				Cursor = System.Windows.Input.Cursors.Hand,
				BorderThickness = new Thickness(1.0),
				Style = boxStyle
			};
			box.MouseLeftButtonUp += delegate
			{
				WindowList.Activate(hwndC);
				RequestHide?.Invoke();
			};
			cell.Children.Add(box);
			GlobalSystemMediaTransportControlsSession session = MediaControls.ForApp(WindowList.GetExePath(it.Hwnd));
			if ((object)session != null)
			{
				StackPanel row = new StackPanel
				{
					Orientation = System.Windows.Controls.Orientation.Horizontal,
					HorizontalAlignment = System.Windows.HorizontalAlignment.Center
				};
				System.Windows.Controls.Button prev = Media("\ue892");
				prev.Click += delegate
				{
					MediaControls.Previous(session);
				};
				System.Windows.Controls.Button play = Media(MediaControls.Playing(session) ? "\ue769" : "\ue768");
				play.FontSize = 17.0;
				play.Click += delegate
				{
					MediaControls.TogglePlayPause(session);
					play.Content = ((play.Content as string == "\ue769") ? "\ue768" : "\ue769");
				};
				System.Windows.Controls.Button next = Media("\ue893");
				next.Click += delegate
				{
					MediaControls.Next(session);
				};
				row.Children.Add(prev);
				row.Children.Add(play);
				row.Children.Add(next);
				Border mediaBar = new Border
				{
					Background = new SolidColorBrush(System.Windows.Media.Color.FromArgb(46, byte.MaxValue, byte.MaxValue, byte.MaxValue)),
					CornerRadius = new CornerRadius(5.0),
					Margin = new Thickness(0.0, 5.0, 0.0, 1.0),
					HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
					Child = row
				};
				cell.Children.Add(mediaBar);
			}
			Cells.Children.Add(cell);
			_boxes.Add((box, it.Hwnd));
		}
		Show();
		UpdateLayout();
		double w = base.ActualWidth;
		double h = base.ActualHeight;
		double anchorCxDiu = (anchorPx.Left + anchorPx.Width / 2.0) / dpiX;
		double anchorCyDiu = (anchorPx.Top + anchorPx.Height / 2.0) / dpiY;
		System.Drawing.Point centerPx = new System.Drawing.Point((int)(anchorPx.Left + anchorPx.Width / 2.0), (int)(anchorPx.Top + anchorPx.Height / 2.0));
		Rectangle wa = TaskbarWorkArea.Current(Screen.FromPoint(centerPx));
		double waL = (double)wa.Left / dpiX;
		double waR = (double)wa.Right / dpiX;
		double waT = (double)wa.Top / dpiY;
		double waB = (double)wa.Bottom / dpiY;
		double left;
		double top;
		switch (edge)
		{
		case "Top":
			left = anchorCxDiu - w / 2.0;
			top = anchorPx.Bottom / dpiY + 6.0;
			break;
		case "Left":
			left = anchorPx.Right / dpiX + 6.0;
			top = anchorCyDiu - h / 2.0;
			break;
		case "Right":
			left = anchorPx.Left / dpiX - w - 6.0;
			top = anchorCyDiu - h / 2.0;
			break;
		default:
			left = anchorCxDiu - w / 2.0;
			top = anchorPx.Top / dpiY - h - 6.0;
			break;
		}
		left = Math.Max(waL + 2.0, Math.Min(left, waR - w - 2.0));
		top = Math.Max(waT + 2.0, Math.Min(top, waB - h - 2.0));
		base.Left = left;
		base.Top = top;
		((DispatcherObject)this).Dispatcher.BeginInvoke((Delegate)(Action)delegate
		{
			Register(dpiX, dpiY);
		}, (DispatcherPriority)6, Array.Empty<object>());
		static System.Windows.Controls.Button Media(string glyph)
		{
			return new System.Windows.Controls.Button
			{
				Content = glyph,
				FontFamily = new System.Windows.Media.FontFamily("Segoe MDL2 Assets"),
				FontSize = 14.0,
				Width = 40.0,
				Height = 28.0,
				Foreground = System.Windows.Media.Brushes.White,
				Background = System.Windows.Media.Brushes.Transparent,
				BorderThickness = new Thickness(0.0),
				Cursor = System.Windows.Input.Cursors.Hand,
				FocusVisualStyle = null,
				Template = MediaButtonTemplate()
			};
		}
	}

	private void Register(double dpiX, double dpiY)
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
					RECT rc = new RECT
					{
						Left = (int)Math.Round(tl.X * dpiX),
						Top = (int)Math.Round(tl.Y * dpiY),
						Right = (int)Math.Round((tl.X + box.ActualWidth) * dpiX),
						Bottom = (int)Math.Round((tl.Y + box.ActualHeight) * dpiY)
					};
					DWM_THUMBNAIL_PROPERTIES props = new DWM_THUMBNAIL_PROPERTIES
					{
						dwFlags = 29,
						rcDestination = rc,
						opacity = byte.MaxValue,
						fVisible = true,
						fSourceClientAreaOnly = false
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

	public new void Hide()
	{
		Unregister();
		base.Hide();
	}

	private static (double W, double H) ThumbBox(nint hwnd)
	{
		try
		{
			if (GetWindowRect(hwnd, out var r))
			{
				double sw = r.Right - r.Left;
				double sh = r.Bottom - r.Top;
				if (sw > 0.0 && sh > 0.0)
				{
					double h = Math.Clamp(200.0 * sh / sw, 96.0, 150.0);
					return (W: 200.0, H: h);
				}
			}
		}
		catch
		{
		}
		return (W: 200.0, H: 120.0);
	}

	private static ControlTemplate MediaButtonTemplate()
	{
		ControlTemplate t = new ControlTemplate(typeof(System.Windows.Controls.Button));
		FrameworkElementFactory bd = new FrameworkElementFactory(typeof(Border))
		{
			Name = "bd"
		};
		bd.SetValue(Border.BackgroundProperty, System.Windows.Media.Brushes.Transparent);
		bd.SetValue(Border.CornerRadiusProperty, new CornerRadius(4.0));
		FrameworkElementFactory cp = new FrameworkElementFactory(typeof(ContentPresenter));
		cp.SetValue(FrameworkElement.HorizontalAlignmentProperty, System.Windows.HorizontalAlignment.Center);
		cp.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
		bd.AppendChild(cp);
		t.VisualTree = bd;
		Trigger hover = new Trigger
		{
			Property = UIElement.IsMouseOverProperty,
			Value = true
		};
		hover.Setters.Add(new Setter(Border.BackgroundProperty, new SolidColorBrush(System.Windows.Media.Color.FromArgb(85, byte.MaxValue, byte.MaxValue, byte.MaxValue)), "bd"));
		t.Triggers.Add(hover);
		return t;
	}

	private static ControlTemplate CloseButtonTemplate()
	{
		ControlTemplate t = new ControlTemplate(typeof(System.Windows.Controls.Button));
		FrameworkElementFactory bd = new FrameworkElementFactory(typeof(Border))
		{
			Name = "bd"
		};
		bd.SetValue(Border.BackgroundProperty, System.Windows.Media.Brushes.Transparent);
		bd.SetValue(Border.CornerRadiusProperty, new CornerRadius(3.0));
		FrameworkElementFactory cp = new FrameworkElementFactory(typeof(ContentPresenter));
		cp.SetValue(FrameworkElement.HorizontalAlignmentProperty, System.Windows.HorizontalAlignment.Center);
		cp.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
		bd.AppendChild(cp);
		t.VisualTree = bd;
		Trigger hover = new Trigger
		{
			Property = UIElement.IsMouseOverProperty,
			Value = true
		};
		hover.Setters.Add(new Setter(Border.BackgroundProperty, new SolidColorBrush(System.Windows.Media.Color.FromArgb(224, 224, 67, 67)), "bd"));
		hover.Setters.Add(new Setter(System.Windows.Controls.Control.ForegroundProperty, System.Windows.Media.Brushes.White));
		t.Triggers.Add(hover);
		return t;
	}

	[DllImport("dwmapi.dll")]
	private static extern int DwmRegisterThumbnail(nint dest, nint src, out nint thumb);

	[DllImport("dwmapi.dll")]
	private static extern int DwmUnregisterThumbnail(nint thumb);

	[DllImport("dwmapi.dll")]
	private static extern int DwmUpdateThumbnailProperties(nint thumb, ref DWM_THUMBNAIL_PROPERTIES props);

	[DllImport("user32.dll")]
	private static extern bool GetWindowRect(nint hwnd, out RECT r);
}
