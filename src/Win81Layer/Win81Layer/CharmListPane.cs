using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Forms;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;

namespace Win81Layer;

public sealed class CharmListPane : Window
{
	public sealed record Row(int Glyph, string Title, string? Subtitle, Action OnClick, bool Enabled = true);

	private const double PaneWidth = 346.0;

	private const string SymFont = "Segoe MDL2 Assets";

	private readonly string _title;

	private readonly string? _scope;

	private readonly Func<IReadOnlyList<Row>> _rows;

	private readonly Border _root;

	private readonly TranslateTransform _slide = new TranslateTransform(346.0, 0.0);

	private bool _hiding;

	private static string G(int cp)
	{
		return ((char)cp).ToString();
	}

	public CharmListPane(string title, string? scope, Func<IReadOnlyList<Row>> rows)
	{
		_title = title;
		_scope = scope;
		_rows = rows;
		base.WindowStyle = WindowStyle.None;
		base.ResizeMode = ResizeMode.NoResize;
		base.ShowInTaskbar = false;
		base.AllowsTransparency = true;
		base.Background = System.Windows.Media.Brushes.Transparent;
		base.Topmost = true;
		base.Width = 346.0;
		base.Title = title;
		_root = new Border
		{
			Background = SettingsPane.PaneBg(),
			RenderTransform = _slide
		};
		base.Content = _root;
		base.Deactivated += delegate
		{
			HidePane();
		};
		base.PreviewKeyDown += delegate(object _, System.Windows.Input.KeyEventArgs e)
		{
			//IL_0002: Unknown result type (might be due to invalid IL or missing references)
			//IL_0009: Invalid comparison between Unknown and I4
			if ((int)e.Key == 13)
			{
				HidePane();
			}
		};
	}

	public void ShowPane()
	{
		//IL_0065: Unknown result type (might be due to invalid IL or missing references)
		//IL_006a: Unknown result type (might be due to invalid IL or missing references)
		//IL_00b0: Unknown result type (might be due to invalid IL or missing references)
		//IL_00b5: Unknown result type (might be due to invalid IL or missing references)
		_hiding = false;
		_root.Background = SettingsPane.PaneBg();
		Build();
		Show();
		ShellSkin.ApplyGlass(this);   // defensively clear any stale accent region; glass look comes from PaneBg alpha
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
		base.Height = (double)b.Height / sy;
		base.Top = (double)b.Top / sy;
		base.Left = (double)b.Right / sx - base.Width;
		WindowUtil.ForceForeground(this);
		_slide.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(0.0, Motion.Dur(Motion.Cat.EdgeEnter))
		{
			EasingFunction = Motion.Ease(Motion.Cat.EdgeEnter)
		});
	}

	public void HidePane()
	{
		if (_hiding || !base.IsVisible)
		{
			return;
		}
		_hiding = true;
		DoubleAnimation slide = new DoubleAnimation(346.0, Motion.Dur(Motion.Cat.EdgeExit))
		{
			EasingFunction = Motion.Ease(Motion.Cat.EdgeExit)
		};
		slide.Completed += delegate
		{
			if (_hiding)
			{
				Hide();
				_hiding = false;
			}
		};
		_slide.BeginAnimation(TranslateTransform.XProperty, slide);
	}

	private void Act(Action a)
	{
		HidePane();
		try
		{
			a();
		}
		catch (Exception ex)
		{
			Logger.Log(_title + " pane: " + ex.Message);
		}
	}

	private void Build()
	{
		Grid grid = new Grid
		{
			Margin = new Thickness(24.0, 22.0, 20.0, 18.0)
		};
		grid.RowDefinitions.Add(new RowDefinition
		{
			Height = GridLength.Auto
		});
		grid.RowDefinitions.Add(new RowDefinition
		{
			Height = GridLength.Auto
		});
		grid.RowDefinitions.Add(new RowDefinition
		{
			Height = new GridLength(1.0, GridUnitType.Star)
		});
		TextBlock header = new TextBlock
		{
			Text = _title,
			Foreground = System.Windows.Media.Brushes.White,
			FontFamily = new System.Windows.Media.FontFamily("Segoe UI Light"),
			FontSize = 34.0,
			Margin = new Thickness(0.0, 0.0, 0.0, 12.0)
		};
		Grid.SetRow(header, 0);
		grid.Children.Add(header);
		if (_scope != null)
		{
			StackPanel sp = new StackPanel
			{
				Orientation = System.Windows.Controls.Orientation.Horizontal,
				Margin = new Thickness(0.0, 0.0, 0.0, 16.0)
			};
			sp.Children.Add(new TextBlock
			{
				Text = _scope,
				Foreground = System.Windows.Media.Brushes.White,
				FontFamily = new System.Windows.Media.FontFamily("Segoe UI"),
				FontSize = 13.0,
				VerticalAlignment = VerticalAlignment.Center
			});
			sp.Children.Add(new TextBlock
			{
				Text = G(59149),
				FontFamily = new System.Windows.Media.FontFamily("Segoe MDL2 Assets"),
				FontSize = 9.0,
				Foreground = new SolidColorBrush(System.Windows.Media.Color.FromArgb(204, byte.MaxValue, byte.MaxValue, byte.MaxValue)),
				VerticalAlignment = VerticalAlignment.Center,
				Margin = new Thickness(7.0, 2.0, 0.0, 0.0)
			});
			Grid.SetRow(sp, 1);
			grid.Children.Add(sp);
		}
		StackPanel list = new StackPanel();
		foreach (Row r in _rows())
		{
			list.Children.Add(RowButton(r));
		}
		ScrollViewer scroller = new ScrollViewer
		{
			VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
			HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
			Content = list
		};
		try { if (System.Windows.Application.Current.TryFindResource("Metro81.ScrollBarDark") is Style sbDark) scroller.Resources[typeof(System.Windows.Controls.Primitives.ScrollBar)] = new Style(typeof(System.Windows.Controls.Primitives.ScrollBar), sbDark); } catch { }   // dark pane
		Grid.SetRow(scroller, 2);
		grid.Children.Add(scroller);
		_root.Child = grid;
	}

	private System.Windows.Controls.Button RowButton(Row r)
	{
		Grid row = new Grid();
		row.ColumnDefinitions.Add(new ColumnDefinition
		{
			Width = new GridLength(40.0)
		});
		row.ColumnDefinitions.Add(new ColumnDefinition());
		double op = (r.Enabled ? 1.0 : 0.4);
		TextBlock glyph = new TextBlock
		{
			Text = G(r.Glyph),
			FontFamily = new System.Windows.Media.FontFamily("Segoe MDL2 Assets"),
			FontSize = 22.0,
			Foreground = System.Windows.Media.Brushes.White,
			VerticalAlignment = VerticalAlignment.Center,
			Opacity = op
		};
		Grid.SetColumn(glyph, 0);
		StackPanel texts = new StackPanel
		{
			VerticalAlignment = VerticalAlignment.Center,
			Opacity = op
		};
		texts.Children.Add(new TextBlock
		{
			Text = r.Title,
			Foreground = System.Windows.Media.Brushes.White,
			FontFamily = new System.Windows.Media.FontFamily("Segoe UI"),
			FontSize = 15.0
		});
		if (!string.IsNullOrEmpty(r.Subtitle))
		{
			texts.Children.Add(new TextBlock
			{
				Text = r.Subtitle,
				Foreground = new SolidColorBrush(System.Windows.Media.Color.FromArgb(170, byte.MaxValue, byte.MaxValue, byte.MaxValue)),
				FontFamily = new System.Windows.Media.FontFamily("Segoe UI"),
				FontSize = 12.0,
				Margin = new Thickness(0.0, 1.0, 0.0, 0.0)
			});
		}
		Grid.SetColumn(texts, 1);
		row.Children.Add(glyph);
		row.Children.Add(texts);
		System.Windows.Controls.Button b = new System.Windows.Controls.Button
		{
			Content = row,
			Background = System.Windows.Media.Brushes.Transparent,
			BorderThickness = new Thickness(0.0),
			Cursor = (r.Enabled ? System.Windows.Input.Cursors.Hand : System.Windows.Input.Cursors.Arrow),
			FocusVisualStyle = null,
			Padding = new Thickness(4.0, 10.0, 4.0, 10.0),
			IsEnabled = r.Enabled,
			HorizontalContentAlignment = System.Windows.HorizontalAlignment.Left,
			Template = HoverRowTemplate()
		};
		b.Click += delegate
		{
			Act(r.OnClick);
		};
		return b;
	}

	public static void Launch(string cmd)
	{
		ShellLaunch.Run(delegate
		{
			if (!AppLauncher.TryLaunch(cmd, null, null, asAdmin: false, out string error))
			{
				Logger.Log("Charm pane open '" + cmd + "': " + error);
			}
		});
	}

	public void QaRender(string outPath, double height = 900.0)
	{
		//IL_003e: Unknown result type (might be due to invalid IL or missing references)
		//IL_006b: Unknown result type (might be due to invalid IL or missing references)
		_root.Background = SettingsPane.PaneBg();
		Build();
		_slide.X = 0.0;
		_root.Measure(new System.Windows.Size(346.0, height));
		_root.Arrange(new Rect(0.0, 0.0, 346.0, height));
		_root.UpdateLayout();
		RenderTargetBitmap rtb = new RenderTargetBitmap(346, (int)height, 96.0, 96.0, PixelFormats.Pbgra32);
		rtb.Render(_root);
		PngBitmapEncoder enc = new PngBitmapEncoder();
		enc.Frames.Add(BitmapFrame.Create(rtb));
		using FileStream fs = File.Create(outPath);
		enc.Save(fs);
	}

	private static ControlTemplate HoverRowTemplate()
	{
		ControlTemplate t = new ControlTemplate(typeof(System.Windows.Controls.Button));
		FrameworkElementFactory bd = new FrameworkElementFactory(typeof(Border))
		{
			Name = "bd"
		};
		bd.SetValue(Border.BackgroundProperty, System.Windows.Media.Brushes.Transparent);
		FrameworkElementFactory cp = new FrameworkElementFactory(typeof(ContentPresenter));
		cp.SetValue(FrameworkElement.MarginProperty, new TemplateBindingExtension(System.Windows.Controls.Control.PaddingProperty));
		cp.SetValue(FrameworkElement.HorizontalAlignmentProperty, System.Windows.HorizontalAlignment.Left);
		cp.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
		bd.AppendChild(cp);
		t.VisualTree = bd;
		Trigger hover = new Trigger
		{
			Property = UIElement.IsMouseOverProperty,
			Value = true
		};
		hover.Setters.Add(new Setter(Border.BackgroundProperty, new SolidColorBrush(System.Windows.Media.Color.FromArgb(24, byte.MaxValue, byte.MaxValue, byte.MaxValue)), "bd"));
		t.Triggers.Add(hover);
		Trigger press = new Trigger
		{
			Property = System.Windows.Controls.Primitives.ButtonBase.IsPressedProperty,
			Value = true
		};
		press.Setters.Add(new Setter(Border.BackgroundProperty, new SolidColorBrush(System.Windows.Media.Color.FromArgb(48, byte.MaxValue, byte.MaxValue, byte.MaxValue)), "bd"));
		t.Triggers.Add(press);
		return t;
	}
}
