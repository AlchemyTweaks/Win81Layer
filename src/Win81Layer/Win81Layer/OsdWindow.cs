using System;
using System.ComponentModel;
using System.Drawing;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Forms;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace Win81Layer;

public sealed class OsdWindow : Window
{
	private const int SegCount = 16;

	private readonly TextBlock _icon;

	private readonly System.Windows.Controls.Image _iconImg;

	private readonly TextBlock _value;

	private readonly Border[] _segs;

	private readonly DispatcherTimer _hide;

	private bool _fadingOut;

	private static readonly System.Windows.Media.Brush EmptyBrush = Freeze(4283058762u);

	private static readonly System.Windows.Media.Brush PanelBrush = Freeze(4028505630u);

	public OsdWindow()
	{
		//IL_029f: Unknown result type (might be due to invalid IL or missing references)
		//IL_02a4: Unknown result type (might be due to invalid IL or missing references)
		//IL_02bb: Expected O, but got Unknown
		_segs = new Border[16];
		base.WindowStyle = WindowStyle.None;
		base.ResizeMode = ResizeMode.NoResize;
		base.ShowInTaskbar = false;
		base.Topmost = true;
		base.ShowActivated = false;
		base.AllowsTransparency = true;
		base.Background = System.Windows.Media.Brushes.Transparent;
		base.Focusable = false;
		base.IsHitTestVisible = false;
		base.SizeToContent = SizeToContent.WidthAndHeight;
		base.UseLayoutRounding = true;
		base.Title = "OSD";
		_icon = new TextBlock
		{
			FontFamily = new System.Windows.Media.FontFamily("Segoe MDL2 Assets"),
			FontSize = 24.0,
			Foreground = System.Windows.Media.Brushes.White,
			VerticalAlignment = VerticalAlignment.Center,
			Width = 34.0,
			TextAlignment = TextAlignment.Center
		};
		// Authentic SndVolSSO speaker (falls back to the MDL2 glyph in _icon when the asset can't resolve).
		_iconImg = new System.Windows.Controls.Image
		{
			Width = 24.0,
			Height = 24.0,
			Stretch = Stretch.Uniform,
			VerticalAlignment = VerticalAlignment.Center,
			HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
			SnapsToDevicePixels = true,
			Visibility = Visibility.Collapsed
		};
		System.Windows.Controls.Grid iconSlot = new System.Windows.Controls.Grid
		{
			Width = 34.0,
			VerticalAlignment = VerticalAlignment.Center
		};
		iconSlot.Children.Add(_icon);
		iconSlot.Children.Add(_iconImg);
		StackPanel bar = new StackPanel
		{
			Orientation = System.Windows.Controls.Orientation.Horizontal,
			VerticalAlignment = VerticalAlignment.Center,
			Margin = new Thickness(8.0, 0.0, 12.0, 0.0)
		};
		for (int i = 0; i < 16; i++)
		{
			Border s = new Border
			{
				Width = 13.0,
				Height = 24.0,
				Margin = new Thickness(0.0, 0.0, 4.0, 0.0),
				Background = EmptyFor()
			};
			_segs[i] = s;
			bar.Children.Add(s);
		}
		_value = new TextBlock
		{
			Text = "0",
			FontFamily = new System.Windows.Media.FontFamily("Segoe UI"),
			FontSize = 22.0,
			Foreground = System.Windows.Media.Brushes.White,
			VerticalAlignment = VerticalAlignment.Center,
			MinWidth = 42.0,
			TextAlignment = TextAlignment.Right
		};
		StackPanel row = new StackPanel
		{
			Orientation = System.Windows.Controls.Orientation.Horizontal
		};
		row.Children.Add(iconSlot);
		row.Children.Add(bar);
		row.Children.Add(_value);
		base.Content = new Border
		{
			Background = CardBackground(),
			Padding = new Thickness(16.0, 11.0, 16.0, 11.0),
			Child = row
		};
		_hide = new DispatcherTimer
		{
			Interval = TimeSpan.FromMilliseconds(1500L)
		};
		_hide.Tick += delegate
		{
			_hide.Stop();
			FadeOut();
		};
	}

	// The card colour, read on every show: the OSD window lives for the whole session, so a change of skin or of Dark
	// rest shows on the next volume or brightness change.
	private static System.Windows.Media.Brush CardBackground()
	{
		if (DarkRest.Visuals)
		{
			return DarkRestPalette.OsdCard;
		}
		if (ShellSkin.GlassOn)
		{
			System.Windows.Media.Color a = ShellSkin.AccentTone(0.16);
			return new SolidColorBrush(System.Windows.Media.Color.FromArgb(0xE8, a.R, a.G, a.B));   // Win7-Aero smoked-accent OSD
		}
		return PanelBrush;
	}

	private static System.Windows.Media.Brush EmptyFor()
	{
		return DarkRest.Visuals ? DarkRestPalette.OsdEmpty : EmptyBrush;
	}

	public void ShowVolume(int pct, bool muted)
	{
		PrepareVolume(pct, muted);
		ShowNow();
	}

	private void PrepareVolume(int pct, bool muted)
	{
		System.Windows.Media.ImageSource img = null;
		try { img = Win81AssetResolver.VolumeImage(pct, muted, 32); } catch { }
		if (img != null)
		{
			_iconImg.Source = img;
			_iconImg.Visibility = Visibility.Visible;
			_icon.Visibility = Visibility.Collapsed;
		}
		else
		{
			_icon.Text = (muted ? G(59215) : VolGlyph(pct));
			_iconImg.Visibility = Visibility.Collapsed;
			_icon.Visibility = Visibility.Visible;
		}
		SetLevel(pct, muted);
	}

	public void ShowBrightness(int pct)
	{
		PrepareBrightness(pct);
		ShowNow();
	}

	private void PrepareBrightness(int pct)
	{
		_iconImg.Visibility = Visibility.Collapsed;
		_icon.Visibility = Visibility.Visible;
		_icon.Text = G(59142);
		SetLevel(pct, muted: false);
	}

	private void SetLevel(int pct, bool muted)
	{
		pct = Math.Clamp(pct, 0, 100);
		int filled = (int)Math.Round((double)pct / 100.0 * 16.0);
		bool rest = DarkRest.Visuals;
		// Dark rest: the accent line for the level and a lighter grey when muted, both 3:1 or more against the empty
		// segments; soft ink for the value and the glyph.
		System.Windows.Media.Brush fill = rest ? (muted ? DarkRestPalette.OsdMuted : DarkRestPalette.OsdFill) : (muted ? Freeze(4285164138u) : new SolidColorBrush(SafeAccent()));
		System.Windows.Media.Brush empty = EmptyFor();
		System.Windows.Media.Brush ink = rest ? DarkRestPalette.Ink : System.Windows.Media.Brushes.White;
		_value.Foreground = ink;
		_icon.Foreground = ink;
		// the white volume picture at the soft ink's brightness
		_iconImg.Opacity = rest ? 0.86 : 1.0;
		for (int i = 0; i < 16; i++)
		{
			bool on = i < filled;
			if (rest)
			{
				// Dark rest: the empty segments are hollow, outlined in the control grey (3.8:1 against the card), so
				// the whole scale stays visible and filled differs from empty by shape as well as by colour
				_segs[i].Background = on ? fill : System.Windows.Media.Brushes.Transparent;
				_segs[i].BorderBrush = on ? null : DarkRestPalette.Boundary;
				_segs[i].BorderThickness = new Thickness(on ? 0.0 : 1.0);
			}
			else
			{
				_segs[i].Background = on ? fill : empty;
				_segs[i].ClearValue(Border.BorderBrushProperty);
				_segs[i].ClearValue(Border.BorderThicknessProperty);
			}
		}
		_value.Text = pct.ToString();
	}

	private void ShowNow()
	{
		((Border)base.Content).Background = CardBackground();
		if (!base.IsVisible)
		{
			base.Opacity = 0.0;
		}
		Show();
		UpdateLayout();
		Reposition();
		_fadingOut = false;
		BeginAnimation(UIElement.OpacityProperty, Motion.To(1.0, Motion.Cat.Micro));
		_hide.Stop();
		_hide.Start();
	}

	private void FadeOut()
	{
		_fadingOut = true;
		DoubleAnimation a = Motion.To(0.0, Motion.Cat.Exit);
		a.Completed += delegate
		{
			if (_fadingOut)
			{
				Hide();
			}
		};
		BeginAnimation(UIElement.OpacityProperty, a);
	}

	private void Reposition()
	{
		//IL_003f: Unknown result type (might be due to invalid IL or missing references)
		//IL_0044: Unknown result type (might be due to invalid IL or missing references)
		//IL_008a: Unknown result type (might be due to invalid IL or missing references)
		//IL_008f: Unknown result type (might be due to invalid IL or missing references)
		Screen screen = Screen.FromPoint(System.Windows.Forms.Cursor.Position);
		Rectangle b = screen.Bounds;
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
		double sx = num ?? 1.0;
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
		double sy = num2 ?? 1.0;
		if (sx <= 0.0)
		{
			sx = 1.0;
		}
		if (sy <= 0.0)
		{
			sy = 1.0;
		}
		base.Left = (double)b.Left / sx + ((double)b.Width / sx - base.ActualWidth) / 2.0;
		base.Top = (double)b.Top / sy + 40.0;
	}

	private static string VolGlyph(int pct)
	{
		return (pct <= 0) ? G(59794) : ((pct <= 33) ? G(59795) : ((pct <= 66) ? G(59796) : G(59797)));
	}

	private static string G(int cp)
	{
		return ((char)cp).ToString();
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

	private static SolidColorBrush Freeze(uint argb)
	{
		SolidColorBrush b = new SolidColorBrush(System.Windows.Media.Color.FromArgb((byte)(argb >> 24), (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb));
		((Freezable)b).Freeze();
		return b;
	}

	protected override void OnClosing(CancelEventArgs e)
	{
		e.Cancel = true;
		Hide();
	}

	public void QaRender(string outPath, bool brightness = false)
	{
		base.Left = -4000.0;
		base.Top = -4000.0;
		if (brightness)
		{
			ShowBrightness(58);
		}
		else
		{
			ShowVolume(67, muted: false);
		}
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
		_hide.Stop();
		Hide();
	}

	// QA: the card exactly as the next show paints it, rendered without showing the window (nothing appears on screen).
	internal void QaRenderCard(string outPath, bool brightness = false)
	{
		if (brightness)
		{
			PrepareBrightness(58);
		}
		else
		{
			PrepareVolume(67, muted: false);
		}
		Border card = (Border)base.Content;
		card.Background = CardBackground();
		card.Measure(new System.Windows.Size(double.PositiveInfinity, double.PositiveInfinity));
		card.Arrange(new Rect(card.DesiredSize));
		card.UpdateLayout();
		int w = Math.Max(1, (int)Math.Ceiling(card.ActualWidth));
		int h = Math.Max(1, (int)Math.Ceiling(card.ActualHeight));
		RenderTargetBitmap rtb = new RenderTargetBitmap(w, h, 96.0, 96.0, PixelFormats.Pbgra32);
		rtb.Render(card);
		PngBitmapEncoder enc = new PngBitmapEncoder();
		enc.Frames.Add(BitmapFrame.Create(rtb));
		using FileStream fs = File.Create(outPath);
		enc.Save(fs);
	}
}
