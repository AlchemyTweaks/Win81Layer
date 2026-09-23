using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;

namespace Win81Layer;

public sealed class AppsColumnsPanel : Panel
{
	public const double ItemHeight = 50.0;   // taller row so long app names can wrap to a 2nd line instead of being clipped

	public const double HeaderHeight = 40.0;

	public const double ColumnWidth = 260.0;

	public static readonly DependencyProperty IsHeaderProperty = DependencyProperty.RegisterAttached("IsHeader", typeof(bool), typeof(AppsColumnsPanel), (PropertyMetadata)(object)new FrameworkPropertyMetadata((object)false));

	private readonly Dictionary<UIElement, Point> _pos = new Dictionary<UIElement, Point>();

	public static void SetIsHeader(DependencyObject d, bool v)
	{
		d.SetValue(IsHeaderProperty, (object)v);
	}

	public static bool GetIsHeader(DependencyObject d)
	{
		return (bool)d.GetValue(IsHeaderProperty);
	}

	private Size Layout(double availableHeight, bool measure)
	{
		//IL_0178: Unknown result type (might be due to invalid IL or missing references)
		//IL_017d: Unknown result type (might be due to invalid IL or missing references)
		//IL_0181: Unknown result type (might be due to invalid IL or missing references)
		//IL_011d: Unknown result type (might be due to invalid IL or missing references)
		//IL_0108: Unknown result type (might be due to invalid IL or missing references)
		_pos.Clear();
		double h = ((double.IsInfinity(availableHeight) || availableHeight < 88.0) ? 700.0 : availableHeight);
		double x = 0.0;
		double y = 0.0;
		foreach (UIElement child in base.InternalChildren)
		{
			if (child.Visibility != Visibility.Collapsed)
			{
				bool header = GetIsHeader((DependencyObject)(object)child);
				double ch = (header ? HeaderHeight : ItemHeight);
				double need = (header ? (HeaderHeight + ItemHeight) : ItemHeight);
				if (y > 0.0 && y + need > h + 0.5)
				{
					x += 260.0;
					y = 0.0;
				}
				if (measure)
				{
					child.Measure(new Size(260.0, ch));
				}
				_pos[child] = new Point(x, y);
				y += ch;
			}
		}
		double totalWidth = ((_pos.Count == 0) ? 0.0 : (x + 260.0));
		return new Size(totalWidth, h);
	}

	protected override Size MeasureOverride(Size availableSize)
	{
		//IL_0009: Unknown result type (might be due to invalid IL or missing references)
		return Layout(availableSize.Height, measure: true);
	}

	protected override Size ArrangeOverride(Size finalSize)
	{
		//IL_000a: Unknown result type (might be due to invalid IL or missing references)
		//IL_0036: Unknown result type (might be due to invalid IL or missing references)
		//IL_0038: Unknown result type (might be due to invalid IL or missing references)
		//IL_0098: Unknown result type (might be due to invalid IL or missing references)
		//IL_0099: Unknown result type (might be due to invalid IL or missing references)
		//IL_0072: Unknown result type (might be due to invalid IL or missing references)
		//IL_009d: Unknown result type (might be due to invalid IL or missing references)
		Layout(finalSize.Height, measure: false);
		foreach (KeyValuePair<UIElement, Point> po in _pos)
		{
			var (child, p) = po;
			double ch = (GetIsHeader((DependencyObject)(object)child) ? HeaderHeight : ItemHeight);
			child.Arrange(new Rect(p.X, p.Y, 260.0, ch));
		}
		return finalSize;
	}

	public double HeaderOffset(string letter)
	{
		foreach (UIElement child in base.InternalChildren)
		{
			if (GetIsHeader((DependencyObject)(object)child) && child is FrameworkElement { Tag: string t } && t == letter && _pos.TryGetValue(child, out var p))
			{
				return p.X;
			}
		}
		return -1.0;
	}

	public IEnumerable<string> Letters()
	{
		string t = default(string);
		foreach (UIElement child in base.InternalChildren)
		{
			int num;
			if (GetIsHeader((DependencyObject)(object)child))
			{
				if (child is FrameworkElement { Tag: var tag })
				{
					t = tag as string;
					num = ((t != null) ? 1 : 0);
				}
				else
				{
					num = 0;
				}
			}
			else
			{
				num = 0;
			}
			if (num != 0)
			{
				yield return t;
			}
			t = null;
		}
	}
}
