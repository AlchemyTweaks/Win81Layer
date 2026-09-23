using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Win81Layer;

public sealed class TilePanel : Panel
{
	public static bool Packed;

	public static readonly DependencyProperty ColsProperty = DependencyProperty.RegisterAttached("Cols", typeof(int), typeof(TilePanel), (PropertyMetadata)(object)new FrameworkPropertyMetadata((object)1, FrameworkPropertyMetadataOptions.AffectsParentMeasure | FrameworkPropertyMetadataOptions.AffectsParentArrange));

	public static readonly DependencyProperty RowsProperty = DependencyProperty.RegisterAttached("Rows", typeof(int), typeof(TilePanel), (PropertyMetadata)(object)new FrameworkPropertyMetadata((object)1, FrameworkPropertyMetadataOptions.AffectsParentMeasure | FrameworkPropertyMetadataOptions.AffectsParentArrange));

	public static readonly DependencyProperty ColProperty = DependencyProperty.RegisterAttached("Col", typeof(int), typeof(TilePanel), (PropertyMetadata)(object)new FrameworkPropertyMetadata((object)(-1), FrameworkPropertyMetadataOptions.AffectsParentMeasure | FrameworkPropertyMetadataOptions.AffectsParentArrange));

	public static readonly DependencyProperty RowProperty = DependencyProperty.RegisterAttached("Row", typeof(int), typeof(TilePanel), (PropertyMetadata)(object)new FrameworkPropertyMetadata((object)(-1), FrameworkPropertyMetadataOptions.AffectsParentMeasure | FrameworkPropertyMetadataOptions.AffectsParentArrange));

	private readonly Dictionary<UIElement, (int Col, int Row, int Cols, int Rows)> _placements = new Dictionary<UIElement, (int, int, int, int)>();

	// Height that _placements was last packed for. MeasureOverride always re-packs (so child add/remove/resize is
	// always reflected, since any change re-triggers Measure); ArrangeOverride then reuses that pack when it arranges
	// at the same height — eliminating the duplicate Pack per layout pass.
	private double _packedHeight = double.NaN;

	private readonly Dictionary<UIElement, Point> _lastArranged = new Dictionary<UIElement, Point>();

	public static int BandRowCapacity = 8;

	private static double Cell => 70.0 * TileMetrics.Scale;

	private static double Gap => 10.0 * TileMetrics.Scale;

	private static double Pitch => Cell + Gap;

	public static double PitchValue => Pitch;

	public static void SetCols(DependencyObject d, int value)
	{
		d.SetValue(ColsProperty, (object)value);
	}

	public static int GetCols(DependencyObject d)
	{
		return (int)d.GetValue(ColsProperty);
	}

	public static void SetRows(DependencyObject d, int value)
	{
		d.SetValue(RowsProperty, (object)value);
	}

	public static int GetRows(DependencyObject d)
	{
		return (int)d.GetValue(RowsProperty);
	}

	public static void SetCol(DependencyObject d, int value)
	{
		d.SetValue(ColProperty, (object)value);
	}

	public static int GetCol(DependencyObject d)
	{
		return (int)d.GetValue(ColProperty);
	}

	public static void SetRow(DependencyObject d, int value)
	{
		d.SetValue(RowProperty, (object)value);
	}

	public static int GetRow(DependencyObject d)
	{
		return (int)d.GetValue(RowProperty);
	}

	public static (int Col, int Row) CellAt(Point p)
	{
		return (Col: (int)Math.Floor(Math.Max(0.0, p.X) / Pitch), Row: (int)Math.Floor(Math.Max(0.0, p.Y) / Pitch));
	}

	public bool TryGetCell(UIElement child, out int col, out int row)
	{
		if (!_placements.TryGetValue(child, out (int, int, int, int) p))
		{
			col = (row = -1);
			return false;
		}
		(col, row, _, _) = p;
		return true;
	}

	public static int RowsForHeight(double height)
	{
		return RowCapacity(height);
	}

	private static int RowCapacity(double height)
	{
		return double.IsInfinity(height) ? Math.Max(1, BandRowCapacity) : Math.Max(1, (int)((height + Gap) / Pitch));
	}

	private void Pack(double availableHeight)
	{
		_placements.Clear();
		int capacity = RowCapacity(availableHeight);
		List<bool[]> columns = new List<bool[]>();
		if (!Packed)
		{
			foreach (UIElement child in base.InternalChildren)
			{
				if (child.Visibility == Visibility.Collapsed)
				{
					continue;
				}
				int col = GetCol((DependencyObject)(object)child);
				int row = GetRow((DependencyObject)(object)child);
				if (col >= 0 && row >= 0)
				{
					int cols = Math.Max(1, GetCols((DependencyObject)(object)child));
					int rows = Math.Min(Math.Max(1, GetRows((DependencyObject)(object)child)), capacity);
					if (row + rows > capacity)
					{
						row = Math.Max(0, capacity - rows);
					}
					if (Fits(col, row, cols, rows))
					{
						Occupy(col, row, cols, rows);
						_placements[child] = (col, row, cols, rows);
					}
				}
			}
		}
		foreach (UIElement child2 in base.InternalChildren)
		{
			if (child2.Visibility == Visibility.Collapsed || _placements.ContainsKey(child2))
			{
				continue;
			}
			int cols2 = Math.Max(1, GetCols((DependencyObject)(object)child2));
			int rows2 = Math.Min(Math.Max(1, GetRows((DependencyObject)(object)child2)), capacity);
			int col2 = 0;
			while (true)
			{
				bool placed = false;
				for (int i = 0; i + rows2 <= capacity; i++)
				{
					if (Fits(col2, i, cols2, rows2))
					{
						Occupy(col2, i, cols2, rows2);
						_placements[child2] = (col2, i, cols2, rows2);
						placed = true;
						break;
					}
				}
				if (placed)
				{
					break;
				}
				col2++;
			}
		}
		bool Fits(int num, int num2, int num4, int num3)
		{
			if (num < 0 || num2 < 0 || num2 + num3 > capacity)
			{
				return false;
			}
			for (int c = num; c < num + num4; c++)
			{
				while (c >= columns.Count)
				{
					columns.Add(new bool[capacity]);
				}
				for (int r = num2; r < num2 + num3; r++)
				{
					if (columns[c][r])
					{
						return false;
					}
				}
			}
			return true;
		}
		void Occupy(int num, int num3, int num2, int num4)
		{
			for (int c = num; c < num + num2; c++)
			{
				while (c >= columns.Count)
				{
					columns.Add(new bool[capacity]);
				}
				for (int r = num3; r < num3 + num4; r++)
				{
					columns[c][r] = true;
				}
			}
		}
	}

	protected override Size MeasureOverride(Size availableSize)
	{
		//IL_0076: Unknown result type (might be due to invalid IL or missing references)
		//IL_00e0: Unknown result type (might be due to invalid IL or missing references)
		//IL_00e5: Unknown result type (might be due to invalid IL or missing references)
		//IL_00e9: Unknown result type (might be due to invalid IL or missing references)
		Pack(availableSize.Height);
		_packedHeight = availableSize.Height;
		int maxCol = 0;
		int capacity = RowCapacity(availableSize.Height);
		foreach (var (child, p) in _placements)
		{
			child.Measure(new Size((double)p.Item3 * Pitch - Gap, (double)p.Item4 * Pitch - Gap));
			maxCol = Math.Max(maxCol, p.Item1 + p.Item3);
		}
		double width = ((maxCol == 0) ? 0.0 : ((double)maxCol * Pitch - Gap));
		double height = (double)capacity * Pitch - Gap;
		return new Size(width, height);
	}

	protected override Size ArrangeOverride(Size finalSize)
	{
		//IL_00ab: Unknown result type (might be due to invalid IL or missing references)
		//IL_01a2: Unknown result type (might be due to invalid IL or missing references)
		//IL_01a3: Unknown result type (might be due to invalid IL or missing references)
		//IL_01a7: Unknown result type (might be due to invalid IL or missing references)
		//IL_0149: Unknown result type (might be due to invalid IL or missing references)
		if (_packedHeight != finalSize.Height)
		{
			Pack(finalSize.Height);
			_packedHeight = finalSize.Height;
		}
		HashSet<UIElement> live = new HashSet<UIElement>();
		Point target = default(Point);
		foreach (var (child, p) in _placements)
		{
			target = new System.Windows.Point((double)p.Item1 * Pitch, (double)p.Item2 * Pitch);
			child.Arrange(new Rect(target.X, target.Y, (double)p.Item3 * Pitch - Gap, (double)p.Item4 * Pitch - Gap));
			live.Add(child);
			if (_lastArranged.TryGetValue(child, out var old) && (Math.Abs(old.X - target.X) > 0.5 || Math.Abs(old.Y - target.Y) > 0.5))
			{
				Glide(child, old.X - target.X, old.Y - target.Y);
			}
			_lastArranged[child] = target;
		}
		_lastArranged.Keys.Where((UIElement k) => !live.Contains(k)).ToList().ForEach(delegate(UIElement k)
		{
			_lastArranged.Remove(k);
		});
		return finalSize;
	}

	private static void Glide(UIElement child, double dx, double dy)
	{
		if (child is FrameworkElement fe)
		{
			TranslateTransform tt = fe.RenderTransform as TranslateTransform;
			if (tt == null)
			{
				tt = (TranslateTransform)(fe.RenderTransform = new TranslateTransform());
			}
			// Skip the no-op axis: horizontal-only reflow (the common case) leaves dy≈0, so don't spin up a 0→0 clock.
			if (Math.Abs(dx) > 0.5)
			{
				tt.BeginAnimation(TranslateTransform.XProperty, Motion.Glide(dx));
			}
			if (Math.Abs(dy) > 0.5)
			{
				tt.BeginAnimation(TranslateTransform.YProperty, Motion.Glide(dy));
			}
		}
	}
}
