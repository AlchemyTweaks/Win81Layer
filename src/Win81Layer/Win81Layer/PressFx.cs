using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;

namespace Win81Layer;

public static class PressFx
{
	public static readonly DependencyProperty CircleProperty;

	public static void SetCircle(DependencyObject d, bool value)
	{
		d.SetValue(CircleProperty, (object)value);
	}

	public static bool GetCircle(DependencyObject d)
	{
		return (bool)d.GetValue(CircleProperty);
	}

	private static void OnCircleChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
	{
		if (d is Button b)
		{
			b.PreviewMouseLeftButtonDown -= OnDown;
			b.PreviewMouseLeftButtonUp -= OnUp;
			b.MouseLeave -= OnLeave;
			if (e.NewValue is int num && num != 0)
			{
				b.PreviewMouseLeftButtonDown += OnDown;
				b.PreviewMouseLeftButtonUp += OnUp;
				b.MouseLeave += OnLeave;
			}
		}
	}

	private static void OnDown(object sender, MouseButtonEventArgs e)
	{
		Press((Button)sender, on: true);
	}

	private static void OnUp(object sender, MouseButtonEventArgs e)
	{
		Press((Button)sender, on: false);
	}

	private static void OnLeave(object sender, MouseEventArgs e)
	{
		Press((Button)sender, on: false);
	}

	private static void Press(Button b, bool on)
	{
		if (b.Template != null)
		{
			b.ApplyTemplate();
			Duration dur = Motion.Dur(Motion.Cat.PressFill);
			IEasingFunction ease = Motion.Ease(Motion.Cat.PressFill);
			if (b.Template.FindName("Fill", b) is Ellipse fill)
			{
				fill.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(on ? 1 : 0, dur)
				{
					EasingFunction = ease
				});
			}
			if (b.Template.FindName("Sc", b) is ScaleTransform sc)
			{
				sc.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(on ? 1.07 : 1.0, dur)
				{
					EasingFunction = ease
				});
				sc.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(on ? 1.07 : 1.0, dur)
				{
					EasingFunction = ease
				});
			}
		}
	}

	static PressFx()
	{
		//IL_0026: Unknown result type (might be due to invalid IL or missing references)
		//IL_0030: Expected O, but got Unknown
		//IL_002b: Unknown result type (might be due to invalid IL or missing references)
		//IL_0035: Expected O, but got Unknown
		CircleProperty = DependencyProperty.RegisterAttached("Circle", typeof(bool), typeof(PressFx), new PropertyMetadata((object)false, new PropertyChangedCallback(OnCircleChanged)));
	}
}
