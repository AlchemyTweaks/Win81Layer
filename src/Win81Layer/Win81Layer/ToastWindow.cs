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
	public const double ToastWidth = 320.0;

	private readonly DispatcherTimer _life;

	private readonly Action? _onClick;

	private bool _closing;

	public ToastWindow(ImageSource? icon, string appName, string title, string message, Action? onClick, string? timestamp)
	{
		//IL_05c5: Unknown result type (might be due to invalid IL or missing references)
		//IL_05ca: Unknown result type (might be due to invalid IL or missing references)
		//IL_05dd: Expected O, but got Unknown
		// Dark rest: a dark card with soft ink, read once for this toast.
		bool rest = DarkRest.Visuals;
		_onClick = onClick;
		base.WindowStyle = WindowStyle.None;
		base.ResizeMode = ResizeMode.NoResize;
		base.ShowInTaskbar = false;
		base.Topmost = true;
		base.ShowActivated = false;
		base.AllowsTransparency = true;
		base.Background = Brushes.Transparent;
		base.Width = ToastWidth;
		base.SizeToContent = SizeToContent.Height;
		base.UseLayoutRounding = true;
		Grid grid = new Grid
		{
			ColumnDefinitions = 
			{
				new ColumnDefinition
				{
					Width = new GridLength((icon != null) ? 52 : 14)
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
				// Dark rest: a dark app icon is lifted or outlined so it does not vanish on the dark card
				Source = rest ? DarkRestIcons.Visible(icon) : icon,
				Width = 28.0,
				Height = 28.0,
				HorizontalAlignment = HorizontalAlignment.Center,
				VerticalAlignment = VerticalAlignment.Top,
				Margin = new Thickness(0.0, 12.0, 0.0, 0.0)
			};
			RenderOptions.SetBitmapScalingMode((DependencyObject)(object)img, BitmapScalingMode.HighQuality);
			Grid.SetColumn(img, 0);
			grid.Children.Add(img);
		}
		StackPanel texts = new StackPanel
		{
			Margin = new Thickness(0.0, 10.0, 8.0, 10.0),
			VerticalAlignment = VerticalAlignment.Center
		};
		if (!string.IsNullOrEmpty(appName))
		{
			texts.Children.Add(new TextBlock
			{
				Text = appName,
				Foreground = rest ? DarkRestPalette.ToastApp : new SolidColorBrush(Color.FromRgb(110, 110, 110)),
				FontFamily = new FontFamily("Segoe UI"),
				FontSize = 12.0,
				TextTrimming = TextTrimming.CharacterEllipsis
			});
		}
		texts.Children.Add(new TextBlock
		{
			Text = title,
			Foreground = rest ? DarkRestPalette.ToastTitle : new SolidColorBrush(Color.FromRgb(26, 26, 26)),
			FontFamily = new FontFamily("Segoe UI Semibold"),
			FontSize = 13.0,
			TextTrimming = TextTrimming.CharacterEllipsis,
			Margin = new Thickness(0.0, 1.0, 0.0, 0.0)
		});
		if (!string.IsNullOrEmpty(message))
		{
			texts.Children.Add(new TextBlock
			{
				Text = message,
				Foreground = rest ? DarkRestPalette.ToastMessage : new SolidColorBrush(Color.FromRgb(58, 58, 58)),
				FontFamily = new FontFamily("Segoe UI"),
				FontSize = 12.0,
				TextWrapping = TextWrapping.Wrap,
				Margin = new Thickness(0.0, 1.0, 0.0, 0.0),
				MaxHeight = 34.0,
				TextTrimming = TextTrimming.CharacterEllipsis
			});
		}
		Grid.SetColumn(texts, 1);
		grid.Children.Add(texts);
		Button close = new Button
		{
			Content = "\ue711",
			FontFamily = new FontFamily("Segoe MDL2 Assets"),
			FontSize = 12.0,
			Width = 36.0,
			Height = 36.0,
			VerticalAlignment = VerticalAlignment.Top,
			HorizontalAlignment = HorizontalAlignment.Right,
			Foreground = rest ? DarkRestPalette.ToastClose : new SolidColorBrush(Color.FromRgb(58, 58, 58)),
			Background = Brushes.Transparent,
			BorderThickness = new Thickness(0.0),
			Cursor = Cursors.Hand,
			FocusVisualStyle = null,
			Template = CloseTemplate(rest)
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
				Foreground = rest ? DarkRestPalette.ToastTime : new SolidColorBrush(Color.FromRgb(154, 154, 154)),
				FontFamily = new FontFamily("Segoe UI"),
				FontSize = 12.0,
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
			Background = rest
				? (ShellSkin.GlassOn ? DarkRestPalette.ToastCardGlass : DarkRestPalette.ToastCard)
				: (ShellSkin.GlassOn ? new SolidColorBrush(Color.FromArgb(224, byte.MaxValue, byte.MaxValue, byte.MaxValue)) : Brushes.White),
			BorderBrush = rest ? DarkRestPalette.ToastBorder : new SolidColorBrush(Color.FromRgb(173, 173, 173)),
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
			DismissTimeout();
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

	private TranslateTransform Slide()
	{
		UIElement card = (UIElement)base.Content;
		if (card.RenderTransform is TranslateTransform t && !t.IsFrozen)
		{
			return t;
		}
		TranslateTransform created = new TranslateTransform();
		card.RenderTransform = created;
		return created;
	}

	// In: the whole card travels in from the screen edge (320 DIP) over 300 ms SOFT DECELERATE, without a fade (the 8.1
	// identity; the first frame moves less than a pixel). Calm motion: 24 DIP over 250 ms with a 120 ms fade.
	public void ShowToast()
	{
		Show();
		TranslateTransform t = Slide();
		UIElement card = (UIElement)base.Content;
		t.X = 0.0;
		card.Opacity = 1.0;
		if (Motion.Enabled)
		{
			double travel = Motion.Distance(ToastWidth);
			if (Math.Abs(travel) > 0.01)
			{
				t.BeginAnimation(TranslateTransform.XProperty, Motion.Anim(travel, 0.0, Motion.Move(travel)), HandoffBehavior.SnapshotAndReplace);
			}
			if (Motion.Mode == MotionMode.Reduced)
			{
				card.BeginAnimation(UIElement.OpacityProperty, Motion.Anim(0.0, 1.0, Motion.Tok.FadeIn), HandoffBehavior.SnapshotAndReplace);
			}
		}
		_life.Start();
	}

	// Close button or swipe: the card leaves to the edge (320 DIP) over 167 ms ACCELERATE; calm motion fades out.
	public void Dismiss()
	{
		if (_closing)
		{
			return;
		}
		_closing = true;
		_life.Stop();
		if (!Motion.Enabled)
		{
			Close();
			return;
		}
		if (Motion.Mode == MotionMode.Reduced)
		{
			FadeAway(Motion.Tok.FadeOut, 0.0);
			return;
		}
		DoubleAnimation slide = new DoubleAnimation(ToastWidth, Motion.Dur(Motion.Tok.ExitMoveLong))
		{
			EasingFunction = Motion.Ease(Motion.Tok.ExitMoveLong)
		};
		slide.Completed += delegate
		{
			Close();
		};
		Slide().BeginAnimation(TranslateTransform.XProperty, slide, HandoffBehavior.SnapshotAndReplace);
	}

	// Timeout: a 167 ms linear fade while moving 24 DIP toward the edge with ACCELERATE (calm motion: the fade only).
	private void DismissTimeout()
	{
		if (_closing)
		{
			return;
		}
		_closing = true;
		_life.Stop();
		if (!Motion.Enabled)
		{
			Close();
			return;
		}
		FadeAway(Motion.Tok.FadeOutLong, Motion.Mode == MotionMode.Reduced ? 0.0 : 24.0);
	}

	private void FadeAway(Motion.Tok fadeTok, double travel)
	{
		UIElement card = (UIElement)base.Content;
		DoubleAnimation fade = new DoubleAnimation(0.0, Motion.Dur(fadeTok))
		{
			EasingFunction = Motion.Ease(fadeTok)
		};
		fade.Completed += delegate
		{
			Close();
		};
		card.BeginAnimation(UIElement.OpacityProperty, fade, HandoffBehavior.SnapshotAndReplace);
		if (travel > 0.0)
		{
			Slide().BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(travel, Motion.Dur(fadeTok)) { EasingFunction = Motion.Accelerate }, HandoffBehavior.SnapshotAndReplace);
		}
	}

	private bool _placed;

	private int _reflowGeneration;

	// Reflow (ToastService): the window is set at its new place once and the card glides from where it was (250 ms
	// DECELERATE). While it glides the window also covers the old place (a transparent margin, which takes no clicks), so
	// the card is never clipped; no window moves per frame. A reflow that lands mid-glide starts from where the card is
	// drawn at that moment, and only the latest glide's end settles the window (a replaced clock still completes).
	internal void PlaceAt(double left, double top)
	{
		int generation = ++_reflowGeneration;
		FrameworkElement card = (FrameworkElement)base.Content;
		TranslateTransform t = Slide();
		Thickness rest = new Thickness(1.0);
		// where the card is drawn now: the window top, the extra top margin of a downward glide, and the glide's offset
		double visualTop = double.IsNaN(base.Top) ? top : base.Top + (card.Margin.Top - rest.Top) + t.Y;
		if (!_placed || _closing || !Motion.Enabled || double.IsNaN(base.Top) || Math.Abs(visualTop - top) < 0.5)
		{
			_placed = true;
			t.BeginAnimation(TranslateTransform.YProperty, null);
			t.Y = 0.0;
			card.Margin = rest;
			base.Left = left;
			base.Top = top;
			return;
		}
		double dy = visualTop - top;   // > 0: the card moves up
		base.Left = left;
		if (dy > 0.0)
		{
			base.Top = top;
			card.Margin = new Thickness(1.0, 1.0, 1.0, 1.0 + dy);
		}
		else
		{
			// moving down: the window stays at the card's current top until the glide ends
			base.Top = visualTop;
			card.Margin = new Thickness(1.0, 1.0 - dy, 1.0, 1.0);
		}
		DoubleAnimation y = Motion.Anim(dy, 0.0, Motion.Tok.MoveShort);
		y.Completed += delegate
		{
			if (generation != _reflowGeneration)
			{
				return;
			}
			card.Margin = rest;
			if (!_closing)
			{
				base.Top = top;
			}
		};
		t.BeginAnimation(TranslateTransform.YProperty, null);
		t.Y = 0.0;
		t.BeginAnimation(TranslateTransform.YProperty, y, HandoffBehavior.SnapshotAndReplace);
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

	private static ControlTemplate CloseTemplate(bool rest)
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
		hover.Setters.Add(new Setter(Border.BackgroundProperty, rest ? DarkRestPalette.ToastCloseHover : new SolidColorBrush(Color.FromRgb(230, 230, 230)), "bd"));
		t.Triggers.Add(hover);
		return t;
	}
}
