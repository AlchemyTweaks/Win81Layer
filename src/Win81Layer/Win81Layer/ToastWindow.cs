using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace Win81Layer;

public sealed class ToastWindow : Window
{
	public const double ToastWidth = 360.0;

	private readonly DispatcherTimer _life;

	private readonly Action? _onClick;

	private bool _closing;

	public ToastWindow(ImageSource? icon, string appName, string title, string message, Action? onClick, string? timestamp)
	{
		//IL_05c5: Unknown result type (might be due to invalid IL or missing references)
		//IL_05ca: Unknown result type (might be due to invalid IL or missing references)
		//IL_05dd: Expected O, but got Unknown
		_onClick = onClick;
		base.WindowStyle = WindowStyle.None;
		base.ResizeMode = ResizeMode.NoResize;
		base.ShowInTaskbar = false;
		base.Topmost = true;
		base.ShowActivated = false;
		base.AllowsTransparency = true;
		base.Background = Brushes.Transparent;
		base.Width = 360.0;
		base.SizeToContent = SizeToContent.Height;
		base.UseLayoutRounding = true;
		Grid grid = new Grid
		{
			ColumnDefinitions = 
			{
				new ColumnDefinition
				{
					Width = new GridLength((icon != null) ? 58 : 16)
				},
				new ColumnDefinition(),
				new ColumnDefinition
				{
					Width = GridLength.Auto
				}
			}
		};
		if (icon != null)
		{
			Image img = new Image
			{
				Source = icon,
				Width = 32.0,
				Height = 32.0,
				HorizontalAlignment = HorizontalAlignment.Center,
				VerticalAlignment = VerticalAlignment.Top,
				Margin = new Thickness(0.0, 14.0, 0.0, 0.0)
			};
			RenderOptions.SetBitmapScalingMode((DependencyObject)(object)img, BitmapScalingMode.HighQuality);
			Grid.SetColumn(img, 0);
			grid.Children.Add(img);
		}
		StackPanel texts = new StackPanel
		{
			Margin = new Thickness(0.0, 11.0, 8.0, 11.0),
			VerticalAlignment = VerticalAlignment.Center
		};
		if (!string.IsNullOrEmpty(appName))
		{
			texts.Children.Add(new TextBlock
			{
				Text = appName,
				Foreground = new SolidColorBrush(Color.FromRgb(110, 110, 110)),
				FontFamily = new FontFamily("Segoe UI"),
				FontSize = 12.0,
				TextTrimming = TextTrimming.CharacterEllipsis
			});
		}
		texts.Children.Add(new TextBlock
		{
			Text = title,
			Foreground = new SolidColorBrush(Color.FromRgb(26, 26, 26)),
			FontFamily = new FontFamily("Segoe UI Semibold"),
			FontSize = 14.0,
			TextTrimming = TextTrimming.CharacterEllipsis,
			Margin = new Thickness(0.0, 1.0, 0.0, 0.0)
		});
		if (!string.IsNullOrEmpty(message))
		{
			texts.Children.Add(new TextBlock
			{
				Text = message,
				Foreground = new SolidColorBrush(Color.FromRgb(58, 58, 58)),
				FontFamily = new FontFamily("Segoe UI"),
				FontSize = 13.0,
				TextWrapping = TextWrapping.Wrap,
				Margin = new Thickness(0.0, 1.0, 0.0, 0.0),
				MaxHeight = 42.0,
				TextTrimming = TextTrimming.CharacterEllipsis
			});
		}
		Grid.SetColumn(texts, 1);
		grid.Children.Add(texts);
		Button close = new Button
		{
			Content = "\ue711",
			FontFamily = new FontFamily("Segoe MDL2 Assets"),
			FontSize = 13.0,
			Width = 42.0,
			Height = 42.0,
			VerticalAlignment = VerticalAlignment.Top,
			HorizontalAlignment = HorizontalAlignment.Right,
			Foreground = new SolidColorBrush(Color.FromRgb(58, 58, 58)),
			Background = Brushes.Transparent,
			BorderThickness = new Thickness(0.0),
			Cursor = Cursors.Hand,
			FocusVisualStyle = null,
			Template = CloseTemplate()
		};
		close.Click += delegate
		{
			Dismiss();
		};
		Grid.SetColumn(close, 2);
		grid.Children.Add(close);
		if (!string.IsNullOrEmpty(timestamp))
		{
			TextBlock ts = new TextBlock
			{
				Text = timestamp,
				Foreground = new SolidColorBrush(Color.FromRgb(154, 154, 154)),
				FontFamily = new FontFamily("Segoe UI"),
				FontSize = 11.0,
				HorizontalAlignment = HorizontalAlignment.Right,
				VerticalAlignment = VerticalAlignment.Bottom,
				Margin = new Thickness(0.0, 0.0, 10.0, 6.0)
			};
			Grid.SetColumn(ts, 1);
			Grid.SetColumnSpan(ts, 2);
			grid.Children.Add(ts);
		}
		Border card = new Border
		{
			Background = (ShellSkin.GlassOn ? new SolidColorBrush(Color.FromArgb(224, byte.MaxValue, byte.MaxValue, byte.MaxValue)) : Brushes.White),
			BorderBrush = new SolidColorBrush(Color.FromRgb(173, 173, 173)),
			BorderThickness = new Thickness(1.0),
			Margin = new Thickness(1.0, 1.0, 1.0, 1.0),
			Child = grid,
			SnapsToDevicePixels = true,
			Cursor = ((_onClick != null) ? Cursors.Hand : Cursors.Arrow)
		};
		card.MouseLeftButtonUp += delegate(object _, MouseButtonEventArgs e)
		{
			object originalSource = e.OriginalSource;
			DependencyObject val = (DependencyObject)((originalSource is DependencyObject) ? originalSource : null);
			if (val == null || !IsInsideButton(val))
			{
				if (_onClick != null)
				{
					try
					{
						_onClick();
					}
					catch
					{
					}
				}
				Dismiss();
			}
		};
		base.Content = card;
		_life = new DispatcherTimer
		{
			Interval = TimeSpan.FromSeconds(6L)
		};
		_life.Tick += delegate
		{
			Dismiss();
		};
		base.MouseEnter += delegate
		{
			_life.Stop();
		};
		base.MouseLeave += delegate
		{
			if (!_closing)
			{
				_life.Start();
			}
		};
	}

	public void ShowToast()
	{
		Show();
		TranslateTransform t = new TranslateTransform(360.0, 0.0);
		((UIElement)base.Content).RenderTransform = t;
		t.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(360.0, 0.0, Motion.Dur(Motion.Cat.EdgeEnter))
		{
			EasingFunction = Motion.Ease(Motion.Cat.EdgeEnter)
		});
		_life.Start();
	}

	public void Dismiss()
	{
		if (!_closing)
		{
			_closing = true;
			_life.Stop();
			TranslateTransform t = (((UIElement)base.Content).RenderTransform as TranslateTransform) ?? new TranslateTransform();
			((UIElement)base.Content).RenderTransform = t;
			DoubleAnimation slide = new DoubleAnimation(360.0, Motion.Dur(Motion.Cat.EdgeExit))
			{
				EasingFunction = Motion.Ease(Motion.Cat.EdgeExit)
			};
			slide.Completed += delegate
			{
				Close();
			};
			t.BeginAnimation(TranslateTransform.XProperty, slide);
		}
	}

	public void QaRender(string outPath)
	{
		base.Left = -4000.0;
		base.Top = -4000.0;
		Show();
		UpdateLayout();
		FrameworkElement card = (FrameworkElement)base.Content;
		int w = Math.Max(1, (int)Math.Ceiling(card.ActualWidth));
		int h = Math.Max(1, (int)Math.Ceiling(card.ActualHeight));
		RenderTargetBitmap rtb = new RenderTargetBitmap(w, h, 96.0, 96.0, PixelFormats.Pbgra32);
		rtb.Render(card);
		PngBitmapEncoder enc = new PngBitmapEncoder();
		enc.Frames.Add(BitmapFrame.Create(rtb));
		using (FileStream fs = File.Create(outPath))
		{
			enc.Save(fs);
		}
		Hide();
	}

	private static bool IsInsideButton(DependencyObject d)
	{
		for (DependencyObject cur = d; cur != null; cur = VisualTreeHelper.GetParent(cur))
		{
			if (cur is Button)
			{
				return true;
			}
		}
		return false;
	}

	private static Color SafeAccent()
	{
		try
		{
			return StartAccent.Color();
		}
		catch
		{
			return Color.FromRgb(42, 125, 225);
		}
	}

	private static ControlTemplate CloseTemplate()
	{
		ControlTemplate t = new ControlTemplate(typeof(Button));
		FrameworkElementFactory bd = new FrameworkElementFactory(typeof(Border))
		{
			Name = "bd"
		};
		bd.SetValue(Border.BackgroundProperty, Brushes.Transparent);
		FrameworkElementFactory cp = new FrameworkElementFactory(typeof(ContentPresenter));
		cp.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
		cp.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
		bd.AppendChild(cp);
		t.VisualTree = bd;
		Trigger hover = new Trigger
		{
			Property = UIElement.IsMouseOverProperty,
			Value = true
		};
		hover.Setters.Add(new Setter(Border.BackgroundProperty, new SolidColorBrush(Color.FromRgb(230, 230, 230)), "bd"));
		t.Triggers.Add(hover);
		return t;
	}
}
