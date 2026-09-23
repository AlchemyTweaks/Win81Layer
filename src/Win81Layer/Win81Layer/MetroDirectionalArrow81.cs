using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Win81Layer;

public enum MetroArrowDirection81
{
	Up,
	Right,
	Down,
	Left
}

internal enum MetroArrowState81
{
	Normal,
	Hover,
	Pressed,
	Disabled
}

internal static class MetroDirectionalArrow81Vectors
{
	internal const double CanonicalSize = 32.0;
	internal const double CircleStroke = 2.0;
	internal const double ArrowStroke = 2.25;

	private static readonly Geometry Circle = Freeze(new EllipseGeometry(new Point(16.0, 16.0), 13.75, 13.75));
	private static Geometry _arrow;

	// Built lazily on the UI thread (first OnRender) so FormattedText glyph resolution never runs
	// from a static initializer on an unknown thread.
	private static Geometry Arrow => _arrow ??= BuildArrow();
	private static readonly Brush White = Freeze(new SolidColorBrush(Colors.White));
	private static readonly Brush HoverFill = Freeze(new SolidColorBrush(Color.FromArgb(16, 255, 255, 255)));
	private static readonly Dictionary<CacheKey, DrawingImage> Cache = new Dictionary<CacheKey, DrawingImage>();
	private static readonly object Gate = new object();

	internal static DrawingImage Draw(MetroArrowDirection81 direction, MetroArrowState81 state, Color accent)
	{
		CacheKey key = new CacheKey(direction, state, state == MetroArrowState81.Pressed ? accent : Colors.Transparent);
		lock (Gate)
		{
			if (Cache.TryGetValue(key, out DrawingImage cached))
			{
				return cached;
			}

			DrawingImage image = Build(direction, state, accent);
			Cache[key] = image;
			return image;
		}
	}

	internal static bool QaUsesCanonicalGeometry(DrawingImage image, MetroArrowDirection81 direction)
	{
		if (image?.Drawing is not DrawingGroup group
			|| group.Children.Count != 2
			|| group.Children[0] is not GeometryDrawing circle
			|| group.Children[1] is not GeometryDrawing arrow
			|| group.Transform is not RotateTransform rotation)
		{
			return false;
		}
		return ReferenceEquals(circle.Geometry, Circle)
			&& ReferenceEquals(arrow.Geometry, Arrow)
			&& Math.Abs(rotation.Angle - DirectionAngle(direction)) < 0.001
			&& Math.Abs(rotation.CenterX - 16.0) < 0.001
			&& Math.Abs(rotation.CenterY - 16.0) < 0.001;
	}

	private static DrawingImage Build(MetroArrowDirection81 direction, MetroArrowState81 state, Color accent)
	{
		DrawingGroup glyph = new DrawingGroup
		{
			ClipGeometry = new RectangleGeometry(new Rect(0.0, 0.0, CanonicalSize, CanonicalSize)),
			Opacity = state == MetroArrowState81.Disabled ? 0.38 : 1.0
		};

		Brush circleFill = state switch
		{
			MetroArrowState81.Pressed => White,
			MetroArrowState81.Hover => HoverFill,
			_ => null
		};
		Pen ring = NewPen(White, CircleStroke);
		Brush arrowBrush = state == MetroArrowState81.Pressed
			? Freeze(new SolidColorBrush(accent))
			: White;

		glyph.Children.Add(new GeometryDrawing(circleFill, ring, Circle));
		// The authentic Windows 8.1 Segoe UI Symbol arrow is a SOLID glyph -> fill it (no stroke).
		glyph.Children.Add(new GeometryDrawing(arrowBrush, null, Arrow));
		glyph.Transform = new RotateTransform(DirectionAngle(direction), 16.0, 16.0);
		glyph.Freeze();

		DrawingImage result = new DrawingImage(glyph);
		result.Freeze();
		return result;
	}

	private static Geometry BuildArrow()
	{
		// Authentic Windows 8.1 nav arrow = the Segoe UI Symbol AppBar UP glyph (U+E110), rendered as
		// a FILLED glyph. The Right (E111) / Left (E112) / Down variants are produced by rotating this
		// exact glyph (the E110/E111/E112 family is the same solid arrow at different orientations), so
		// the whole set matches the official reference sheet. See memory: win81-appbar-glyphs.
		FormattedText text = new FormattedText(
			char.ConvertFromUtf32(0xE110),
			CultureInfo.InvariantCulture,
			FlowDirection.LeftToRight,
			new Typeface(new FontFamily("Segoe UI Symbol"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal),
			100.0,
			Brushes.White,
			1.0);
		Geometry raw = text.BuildGeometry(new Point(0.0, 0.0));
		Rect bounds = raw.Bounds;
		double extent = Math.Max(bounds.Width, bounds.Height);
		// Fit the glyph into ~16px inside the 27.5px circle interior => ~20% internal padding (per spec).
		double scale = extent > 0.0 ? 16.0 / extent : 1.0;
		TransformGroup transform = new TransformGroup();
		transform.Children.Add(new TranslateTransform(0.0 - (bounds.X + bounds.Width / 2.0), 0.0 - (bounds.Y + bounds.Height / 2.0)));
		transform.Children.Add(new ScaleTransform(scale, scale));
		transform.Children.Add(new TranslateTransform(16.0, 16.0));
		raw.Transform = transform;
		// Bake the transform into the path so the stored geometry is centred on (16,16) and pre-scaled.
		Geometry baked = raw.GetFlattenedPathGeometry();
		baked.Freeze();
		return baked;
	}

	private static Pen NewPen(Brush brush, double thickness)
	{
		Pen pen = new Pen(brush, thickness)
		{
			StartLineCap = PenLineCap.Round,
			EndLineCap = PenLineCap.Round,
			LineJoin = PenLineJoin.Round
		};
		pen.Freeze();
		return pen;
	}

	private static double DirectionAngle(MetroArrowDirection81 direction)
	{
		return direction switch
		{
			MetroArrowDirection81.Right => 90.0,
			MetroArrowDirection81.Down => 180.0,
			MetroArrowDirection81.Left => 270.0,
			_ => 0.0
		};
	}

	private static T Freeze<T>(T value) where T : Freezable
	{
		value.Freeze();
		return value;
	}

	private readonly record struct CacheKey(MetroArrowDirection81 Direction, MetroArrowState81 State, Color Accent);
}

public sealed class MetroDirectionalArrow81 : Button
{
	public static readonly DependencyProperty DirectionProperty = DependencyProperty.Register(
		nameof(Direction),
		typeof(MetroArrowDirection81),
		typeof(MetroDirectionalArrow81),
		new FrameworkPropertyMetadata(MetroArrowDirection81.Up, FrameworkPropertyMetadataOptions.AffectsRender));

	public static readonly DependencyProperty GlyphSizeProperty = DependencyProperty.Register(
		nameof(GlyphSize),
		typeof(double),
		typeof(MetroDirectionalArrow81),
		new FrameworkPropertyMetadata(32.0, FrameworkPropertyMetadataOptions.AffectsRender, null, CoerceGlyphSize));

	public static readonly DependencyProperty AccentBrushProperty = DependencyProperty.Register(
		nameof(AccentBrush),
		typeof(Brush),
		typeof(MetroDirectionalArrow81),
		new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

	public MetroArrowDirection81 Direction
	{
		get => (MetroArrowDirection81)GetValue(DirectionProperty);
		set => SetValue(DirectionProperty, value);
	}

	public double GlyphSize
	{
		get => (double)GetValue(GlyphSizeProperty);
		set => SetValue(GlyphSizeProperty, value);
	}

	public Brush AccentBrush
	{
		get => (Brush)GetValue(AccentBrushProperty);
		set => SetValue(AccentBrushProperty, value);
	}

	public MetroDirectionalArrow81()
	{
		Background = Brushes.Transparent;
		BorderThickness = new Thickness(0.0);
		FocusVisualStyle = null;
		Cursor = System.Windows.Input.Cursors.Hand;
		SnapsToDevicePixels = true;
		UseLayoutRounding = true;
	}

	protected override void OnRender(DrawingContext drawingContext)
	{
		base.OnRender(drawingContext);
		double size = Math.Min(GlyphSize, Math.Min(RenderSize.Width, RenderSize.Height));
		if (size <= 0.0)
		{
			return;
		}

		double x = (RenderSize.Width - size) * 0.5;
		double y = (RenderSize.Height - size) * 0.5;
		MetroArrowState81 state = !IsEnabled
			? MetroArrowState81.Disabled
			: IsPressed
				? MetroArrowState81.Pressed
				: IsMouseOver
					? MetroArrowState81.Hover
					: MetroArrowState81.Normal;
		drawingContext.DrawImage(
			MetroDirectionalArrow81Vectors.Draw(Direction, state, ResolveAccent()),
			new Rect(x, y, size, size));
	}

	protected override void OnPropertyChanged(DependencyPropertyChangedEventArgs e)
	{
		base.OnPropertyChanged(e);
		if (e.Property == IsPressedProperty || e.Property == IsMouseOverProperty || e.Property == IsEnabledProperty)
		{
			InvalidateVisual();
		}
	}

	private Color ResolveAccent()
	{
		if (AccentBrush is SolidColorBrush solid)
		{
			return solid.Color;
		}
		return StartAccent.FastColor();
	}

	private static object CoerceGlyphSize(DependencyObject dependencyObject, object value)
	{
		double size = value is double number ? number : 32.0;
		return double.IsFinite(size) ? Math.Clamp(size, 8.0, 96.0) : 32.0;
	}
}
