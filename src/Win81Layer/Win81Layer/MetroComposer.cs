using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Win81Layer;

internal static class MetroComposer
{
	private static readonly Brush Ink = Brushes.White;

	private static readonly Brush InkDim = new SolidColorBrush(Color.FromArgb(204, byte.MaxValue, byte.MaxValue, byte.MaxValue));

	private static readonly Brush InkFaint = new SolidColorBrush(Color.FromArgb(176, byte.MaxValue, byte.MaxValue, byte.MaxValue));

	private static readonly Brush Hair = new SolidColorBrush(Color.FromArgb(38, byte.MaxValue, byte.MaxValue, byte.MaxValue));

	private const string Mdl2 = "Segoe MDL2 Assets";

	public static FrameworkElement BuildPlaceCard(EntityCard card, Func<string?, ImageSource?> heroLoader)
	{
		Border root = new Border
		{
			Background = ShellSkin.CardBg(),
			BorderBrush = Hair,
			BorderThickness = new Thickness(1.0),
			Margin = new Thickness(0.0, 2.0, 0.0, 10.0),
			SnapsToDevicePixels = true
		};
		StackPanel stack = new StackPanel();
		ImageSource hero = heroLoader(card.HeroImageUrl);
		if (hero != null)
		{
			Image img = new Image
			{
				Source = hero,
				Stretch = Stretch.UniformToFill,
				Height = 130.0
			};
			Border holder = new Border
			{
				Height = 130.0,
				ClipToBounds = true,
				Child = img
			};
			stack.Children.Add(holder);
		}
		StackPanel body = new StackPanel
		{
			Margin = new Thickness(14.0, 12.0, 14.0, 12.0)
		};
		body.Children.Add(new TextBlock
		{
			Text = card.Name,
			Foreground = Ink,
			FontFamily = new FontFamily("Segoe UI Light"),
			FontSize = 30.0,
			TextTrimming = TextTrimming.CharacterEllipsis
		});
		body.Children.Add(new TextBlock
		{
			Text = card.TypeLabel,
			Foreground = InkFaint,
			FontFamily = new FontFamily("Segoe UI"),
			FontSize = 12.0,
			Margin = new Thickness(0.0, 0.0, 0.0, 8.0)
		});
		foreach (Fact f in card.Facts)
		{
			if (string.IsNullOrEmpty(f.Label))
			{
				body.Children.Add(new TextBlock
				{
					Text = f.Value,
					Foreground = InkDim,
					FontFamily = new FontFamily("Segoe UI"),
					FontSize = 13.0,
					TextWrapping = TextWrapping.Wrap,
					Margin = new Thickness(0.0, 2.0, 0.0, 8.0)
				});
				continue;
			}
			StackPanel row = new StackPanel
			{
				Orientation = Orientation.Horizontal,
				Margin = new Thickness(0.0, 3.0, 0.0, 3.0)
			};
			row.Children.Add(new TextBlock
			{
				Text = f.Label + ":",
				Foreground = InkFaint,
				FontFamily = new FontFamily("Segoe UI"),
				FontSize = 13.0,
				MinWidth = 62.0,
				VerticalAlignment = VerticalAlignment.Center
			});
			row.Children.Add(new TextBlock
			{
				Text = f.Value,
				Foreground = InkDim,
				FontFamily = new FontFamily("Segoe UI"),
				FontSize = 13.0,
				VerticalAlignment = VerticalAlignment.Center,
				TextTrimming = TextTrimming.CharacterEllipsis
			});
			body.Children.Add(row);
		}
		if (card.Actions.Count > 0)
		{
			WrapPanel actions = new WrapPanel
			{
				Margin = new Thickness(0.0, 8.0, 0.0, 0.0)
			};
			foreach (LauncherAction a in card.Actions)
			{
				actions.Children.Add(ActionButton(a));
			}
			body.Children.Add(new Border
			{
				BorderBrush = Hair,
				BorderThickness = new Thickness(0.0, 1.0, 0.0, 0.0),
				Margin = new Thickness(0.0, 6.0, 0.0, 0.0),
				Child = actions
			});
		}
		stack.Children.Add(body);
		root.Child = stack;
		return root;
	}

	public static FrameworkElement BuildCalcCard(EntityCard card)
	{
		Border root = new Border
		{
			Background = ShellSkin.CardBg(),
			BorderBrush = Hair,
			BorderThickness = new Thickness(1.0),
			Margin = new Thickness(0.0, 2.0, 0.0, 10.0),
			SnapsToDevicePixels = true
		};
		StackPanel body = new StackPanel
		{
			Margin = new Thickness(14.0, 12.0, 14.0, 12.0)
		};
		body.Children.Add(new TextBlock
		{
			Text = card.TypeLabel + " =",
			Foreground = InkFaint,
			FontFamily = new FontFamily("Segoe UI"),
			FontSize = 13.0,
			TextTrimming = TextTrimming.CharacterEllipsis,
			Margin = new Thickness(0.0, 0.0, 0.0, 2.0)
		});
		body.Children.Add(new TextBlock
		{
			Text = card.Name,
			Foreground = Ink,
			FontFamily = new FontFamily("Segoe UI Light"),
			FontSize = 40.0,
			TextTrimming = TextTrimming.CharacterEllipsis
		});
		if (card.Actions.Count > 0)
		{
			WrapPanel actions = new WrapPanel
			{
				Margin = new Thickness(0.0, 6.0, 0.0, 0.0)
			};
			foreach (LauncherAction a in card.Actions)
			{
				actions.Children.Add(ActionButton(a));
			}
			body.Children.Add(actions);
		}
		root.Child = body;
		return root;
	}

	public static FrameworkElement BuildColorCard(EntityCard card)
	{
		Border root = new Border
		{
			Background = ShellSkin.CardBg(),
			BorderBrush = Hair,
			BorderThickness = new Thickness(1.0),
			Margin = new Thickness(0.0, 2.0, 0.0, 10.0),
			SnapsToDevicePixels = true
		};
		StackPanel stack = new StackPanel();
		Color sw;
		try { sw = (Color)ColorConverter.ConvertFromString(card.Name); }
		catch { sw = Colors.Gray; }
		SolidColorBrush swb = new SolidColorBrush(sw);
		swb.Freeze();
		stack.Children.Add(new Border { Height = 72.0, Background = swb });
		StackPanel body = new StackPanel
		{
			Margin = new Thickness(14.0, 12.0, 14.0, 12.0)
		};
		body.Children.Add(new TextBlock
		{
			Text = card.Name,
			Foreground = Ink,
			FontFamily = new FontFamily("Segoe UI Light"),
			FontSize = 30.0
		});
		body.Children.Add(new TextBlock
		{
			Text = card.TypeLabel,
			Foreground = InkFaint,
			FontFamily = new FontFamily("Segoe UI"),
			FontSize = 12.0,
			Margin = new Thickness(0.0, 0.0, 0.0, 8.0)
		});
		foreach (Fact f in card.Facts)
		{
			StackPanel row = new StackPanel
			{
				Orientation = Orientation.Horizontal,
				Margin = new Thickness(0.0, 3.0, 0.0, 3.0)
			};
			row.Children.Add(new TextBlock
			{
				Text = (f.Label ?? "") + ":",
				Foreground = InkFaint,
				FontFamily = new FontFamily("Segoe UI"),
				FontSize = 13.0,
				MinWidth = 44.0,
				VerticalAlignment = VerticalAlignment.Center
			});
			row.Children.Add(new TextBlock
			{
				Text = f.Value,
				Foreground = InkDim,
				FontFamily = new FontFamily("Segoe UI"),
				FontSize = 13.0,
				VerticalAlignment = VerticalAlignment.Center,
				TextTrimming = TextTrimming.CharacterEllipsis
			});
			body.Children.Add(row);
		}
		if (card.Actions.Count > 0)
		{
			WrapPanel actions = new WrapPanel
			{
				Margin = new Thickness(0.0, 6.0, 0.0, 0.0)
			};
			foreach (LauncherAction a in card.Actions)
			{
				actions.Children.Add(ActionButton(a));
			}
			body.Children.Add(actions);
		}
		stack.Children.Add(body);
		root.Child = stack;
		return root;
	}

	private static Button ActionButton(LauncherAction a)
	{
		StackPanel content = new StackPanel
		{
			Orientation = Orientation.Horizontal
		};
		if (!string.IsNullOrEmpty(a.Glyph))
		{
			content.Children.Add(new TextBlock
			{
				Text = a.Glyph,
				FontFamily = new FontFamily("Segoe MDL2 Assets"),
				FontSize = 13.0,
				Foreground = Ink,
				Margin = new Thickness(0.0, 0.0, 6.0, 0.0),
				VerticalAlignment = VerticalAlignment.Center
			});
		}
		content.Children.Add(new TextBlock
		{
			Text = a.Label,
			Foreground = Ink,
			FontFamily = new FontFamily("Segoe UI"),
			FontSize = 13.0,
			VerticalAlignment = VerticalAlignment.Center
		});
		Button btn = new Button
		{
			Content = content,
			Background = Brushes.Transparent,
			BorderThickness = new Thickness(0.0),
			Cursor = Cursors.Hand,
			FocusVisualStyle = null,
			Padding = new Thickness(8.0, 6.0, 8.0, 6.0),
			Margin = new Thickness(0.0, 6.0, 8.0, 0.0),
			Template = FlatHoverTemplate()
		};
		btn.Click += delegate
		{
			try
			{
				a.Invoke();
			}
			catch (Exception ex)
			{
				Logger.Log("Action " + a.Id + ": " + ex.Message);
			}
		};
		return btn;
	}

	private static ControlTemplate FlatHoverTemplate()
	{
		ControlTemplate t = new ControlTemplate(typeof(Button));
		FrameworkElementFactory bd = new FrameworkElementFactory(typeof(Border))
		{
			Name = "bd"
		};
		bd.SetValue(Border.BackgroundProperty, new SolidColorBrush(Color.FromArgb(20, byte.MaxValue, byte.MaxValue, byte.MaxValue)));
		FrameworkElementFactory cp = new FrameworkElementFactory(typeof(ContentPresenter));
		cp.SetValue(FrameworkElement.MarginProperty, new TemplateBindingExtension(Control.PaddingProperty));
		cp.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
		bd.AppendChild(cp);
		t.VisualTree = bd;
		Trigger hover = new Trigger
		{
			Property = UIElement.IsMouseOverProperty,
			Value = true
		};
		hover.Setters.Add(new Setter(Border.BackgroundProperty, new SolidColorBrush(Color.FromArgb(51, byte.MaxValue, byte.MaxValue, byte.MaxValue)), "bd"));
		t.Triggers.Add(hover);
		return t;
	}
}
