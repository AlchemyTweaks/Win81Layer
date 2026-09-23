using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Win81Layer;

public sealed class AnimatedBar : Panel
{
	private readonly Dictionary<UIElement, double> _lastX = new Dictionary<UIElement, double>();

	public static UIElement? DragExempt { get; set; }

	protected override Size MeasureOverride(Size available)
	{
		//IL_0053: Unknown result type (might be due to invalid IL or missing references)
		//IL_0060: Unknown result type (might be due to invalid IL or missing references)
		//IL_0065: Unknown result type (might be due to invalid IL or missing references)
		//IL_0072: Unknown result type (might be due to invalid IL or missing references)
		//IL_0077: Unknown result type (might be due to invalid IL or missing references)
		//IL_00bf: Unknown result type (might be due to invalid IL or missing references)
		//IL_00c4: Unknown result type (might be due to invalid IL or missing references)
		//IL_00c8: Unknown result type (might be due to invalid IL or missing references)
		double w = 0.0;
		double h = 0.0;
		foreach (UIElement c in base.InternalChildren)
		{
			if (c.Visibility != Visibility.Collapsed)
			{
				c.Measure(new Size(double.PositiveInfinity, available.Height));
				double num = w;
				Size desiredSize = c.DesiredSize;
				w = num + desiredSize.Width;
				double val = h;
				desiredSize = c.DesiredSize;
				h = Math.Max(val, desiredSize.Height);
			}
		}
		return new Size(w, double.IsInfinity(available.Height) ? h : available.Height);
	}

	protected override Size ArrangeOverride(Size finalSize)
	{
		//IL_0057: Unknown result type (might be due to invalid IL or missing references)
		//IL_005c: Unknown result type (might be due to invalid IL or missing references)
		//IL_007b: Unknown result type (might be due to invalid IL or missing references)
		//IL_013f: Unknown result type (might be due to invalid IL or missing references)
		//IL_0140: Unknown result type (might be due to invalid IL or missing references)
		//IL_0144: Unknown result type (might be due to invalid IL or missing references)
		double x = 0.0;
		HashSet<UIElement> live = new HashSet<UIElement>();
		foreach (UIElement c in base.InternalChildren)
		{
			if (c.Visibility != Visibility.Collapsed)
			{
				Size desiredSize = c.DesiredSize;
				double w = desiredSize.Width;
				c.Arrange(new Rect(x, 0.0, w, finalSize.Height));
				live.Add(c);
				if (_lastX.TryGetValue(c, out var old) && Math.Abs(old - x) > 0.5 && c != DragExempt)
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
			// Preserve visual continuity. dx == (oldBase - newBase); the element was already Arrange()d at
			// newBase, so its current on-screen position is newBase + tt.X. If a previous glide is still in
			// flight, tt.X is non-zero — start the new glide from (dx + tt.X) so newBase + from lands exactly
			// on that current position instead of snapping back to the old slot and jumping.
			double from = dx + tt.X;
			tt.BeginAnimation(TranslateTransform.XProperty, Motion.Glide(from));
		}
	}
}
