using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Win81Layer;

public sealed class ShrinkStrip : Panel
{
	private const double MinBtn = 40.0;

	private double _per = -1.0;

	private readonly Dictionary<UIElement, double> _lastX = new Dictionary<UIElement, double>();

	public ShrinkStrip()
	{
		base.ClipToBounds = true;
	}

	protected override Size MeasureOverride(Size available)
	{
		//IL_005d: Unknown result type (might be due to invalid IL or missing references)
		//IL_006b: Unknown result type (might be due to invalid IL or missing references)
		//IL_0070: Unknown result type (might be due to invalid IL or missing references)
		//IL_007d: Unknown result type (might be due to invalid IL or missing references)
		//IL_0082: Unknown result type (might be due to invalid IL or missing references)
		//IL_0096: Unknown result type (might be due to invalid IL or missing references)
		//IL_009b: Unknown result type (might be due to invalid IL or missing references)
		//IL_012f: Unknown result type (might be due to invalid IL or missing references)
		//IL_0134: Unknown result type (might be due to invalid IL or missing references)
		//IL_01da: Unknown result type (might be due to invalid IL or missing references)
		//IL_0193: Unknown result type (might be due to invalid IL or missing references)
		//IL_01d1: Unknown result type (might be due to invalid IL or missing references)
		//IL_01d6: Unknown result type (might be due to invalid IL or missing references)
		int n = 0;
		double natural = 0.0;
		double h = 0.0;
		foreach (UIElement c in base.InternalChildren)
		{
			if (c.Visibility != Visibility.Collapsed)
			{
				c.Measure(new Size(double.PositiveInfinity, available.Height));
				double num = natural;
				Size desiredSize = c.DesiredSize;
				natural = num + desiredSize.Width;
				desiredSize = c.DesiredSize;
				if (desiredSize.Height > h)
				{
					desiredSize = c.DesiredSize;
					h = desiredSize.Height;
				}
				n++;
			}
		}
		double avail = available.Width;
		double height = (double.IsInfinity(available.Height) ? h : available.Height);
		if (n == 0 || double.IsInfinity(avail) || natural <= avail)
		{
			_per = -1.0;
			return new Size(double.IsInfinity(avail) ? natural : Math.Min(natural, avail), height);
		}
		_per = Math.Max(40.0, avail / (double)n);
		foreach (UIElement c2 in base.InternalChildren)
		{
			if (c2.Visibility != Visibility.Collapsed)
			{
				c2.Measure(new Size(_per, available.Height));
			}
		}
		return new Size(Math.Min(avail, _per * (double)n), height);
	}

	protected override Size ArrangeOverride(Size finalSize)
	{
		//IL_014d: Unknown result type (might be due to invalid IL or missing references)
		//IL_014e: Unknown result type (might be due to invalid IL or missing references)
		//IL_0070: Unknown result type (might be due to invalid IL or missing references)
		//IL_0075: Unknown result type (might be due to invalid IL or missing references)
		//IL_0152: Unknown result type (might be due to invalid IL or missing references)
		//IL_0094: Unknown result type (might be due to invalid IL or missing references)
		double x = 0.0;
		HashSet<UIElement> live = new HashSet<UIElement>();
		foreach (UIElement c in base.InternalChildren)
		{
			if (c.Visibility != Visibility.Collapsed)
			{
				double num;
				if (!(_per < 0.0))
				{
					num = _per;
				}
				else
				{
					Size desiredSize = c.DesiredSize;
					num = desiredSize.Width;
				}
				double w = num;
				c.Arrange(new Rect(x, 0.0, w, finalSize.Height));
				live.Add(c);
				if (_lastX.TryGetValue(c, out var old) && Math.Abs(old - x) > 0.5)
				{
					Glide(c, old - x);
				}
				_lastX[c] = x;
				x += w;
			}
		}
		_lastX.Keys.Where((UIElement k) => !live.Contains(k)).ToList().ForEach(delegate(UIElement k)
		{
			_lastX.Remove(k);
		});
		return finalSize;
	}

	private static void Glide(UIElement child, double dx)
	{
		if (child is FrameworkElement fe)
		{
			TranslateTransform tt = fe.RenderTransform as TranslateTransform;
			if (tt == null)
			{
				tt = (TranslateTransform)(fe.RenderTransform = new TranslateTransform());
			}
			tt.BeginAnimation(TranslateTransform.XProperty, Motion.Glide(dx));
		}
	}
}
