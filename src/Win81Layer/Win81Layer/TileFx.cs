using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace Win81Layer;

public static class TileFx
{
	public static readonly DependencyProperty AnimatedSourceProperty;

	public static void SetAnimatedSource(DependencyObject o, ImageSource? v)
	{
		o.SetValue(AnimatedSourceProperty, (object)v);
	}

	public static ImageSource? GetAnimatedSource(DependencyObject o)
	{
		return (ImageSource)o.GetValue(AnimatedSourceProperty);
	}

	private static void OnAnimatedSourceChanged(DependencyObject o, DependencyPropertyChangedEventArgs e)
	{
		if (o is Image img)
		{
			img.Source = e.NewValue as ImageSource;
			if (e.OldValue != null && e.NewValue != null && !(img.Opacity < 0.99) && Motion.Mode != MotionMode.Off)
			{
				img.BeginAnimation(UIElement.OpacityProperty, null);
				img.Opacity = 0.25;
				img.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0.25, 1.0, Motion.Dur(Motion.Cat.Micro))
				{
					EasingFunction = Motion.Ease(Motion.Cat.Micro)
				});
			}
		}
	}

	static TileFx()
	{
		//IL_0021: Unknown result type (might be due to invalid IL or missing references)
		//IL_002b: Expected O, but got Unknown
		//IL_0026: Unknown result type (might be due to invalid IL or missing references)
		//IL_0030: Expected O, but got Unknown
		AnimatedSourceProperty = DependencyProperty.RegisterAttached("AnimatedSource", typeof(ImageSource), typeof(TileFx), new PropertyMetadata((object)null, new PropertyChangedCallback(OnAnimatedSourceChanged)));
	}
}
