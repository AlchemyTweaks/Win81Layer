using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Win81Layer;

internal static class TextPrompt
{
	internal static string? Show(string title, string initial)
	{
		try
		{
			string result = null;
			Window owner = Application.Current?.Windows.OfType<Window>().FirstOrDefault((Window w) => w.IsActive);
			Window win = new Window
			{
				Title = title,
				Width = 400.0,
				Height = 168.0,
				Owner = owner,
				WindowStartupLocation = ((owner == null) ? WindowStartupLocation.CenterScreen : WindowStartupLocation.CenterOwner),
				ResizeMode = ResizeMode.NoResize,
				ShowInTaskbar = false,
				Background = Brushes.White   // pattern 8: a flyout is a light surface with regular pattern controls
			};
			Grid grid = new Grid
			{
				Margin = new Thickness(18.0)
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
			TextBlock label = new TextBlock
			{
				Text = title,
				Foreground = Brushes.Black,
				FontFamily = new FontFamily("Segoe UI"),
				FontSize = 14.0,
				Margin = new Thickness(0.0, 0.0, 0.0, 8.0)
			};
			TextBox box = new TextBox
			{
				Text = initial,
				FontFamily = new FontFamily("Segoe UI"),
				FontSize = 14.0,
				Padding = new Thickness(5.0)
			};
			StackPanel buttons = new StackPanel
			{
				Orientation = Orientation.Horizontal,
				HorizontalAlignment = HorizontalAlignment.Right,
				VerticalAlignment = VerticalAlignment.Bottom,
				Margin = new Thickness(0.0, 14.0, 0.0, 0.0)
			};
			Button ok = new Button
			{
				Content = "OK",
				Width = 84.0,
				Height = 32.0,
				Margin = new Thickness(0.0, 0.0, 8.0, 0.0),
				IsDefault = true
			};
			Button cancel = new Button
			{
				Content = "Cancel",
				Width = 84.0,
				Height = 32.0,
				IsCancel = true
			};
			// Windows 8 Patterns: primary = accent button, secondary = the gray pattern button, field = pattern text box.
			ok.SetResourceReference(FrameworkElement.StyleProperty, "Metro81.AccentButton");
			cancel.SetResourceReference(FrameworkElement.StyleProperty, "Metro81.Button");
			box.SetResourceReference(FrameworkElement.StyleProperty, "Metro81.TextBox");
			ok.Click += delegate
			{
				result = box.Text;
				win.DialogResult = true;
			};
			buttons.Children.Add(ok);
			buttons.Children.Add(cancel);
			Grid.SetRow(label, 0);
			Grid.SetRow(box, 1);
			Grid.SetRow(buttons, 2);
			grid.Children.Add(label);
			grid.Children.Add(box);
			grid.Children.Add(buttons);
			win.Content = grid;
			box.Loaded += delegate
			{
				box.SelectAll();
				box.Focus();
			};
			return (win.ShowDialog() == true) ? result : null;
		}
		catch (Exception ex)
		{
			Logger.Log("TextPrompt: " + ex.Message);
			return null;
		}
	}
}
