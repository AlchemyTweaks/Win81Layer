using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace Win81Layer;

// Renders a symbol-font glyph (Segoe MDL2 Assets etc.) as a frozen VECTOR DrawingImage — resolution-independent, so a WPF
// Image displays it razor-sharp at ANY size, unlike a raster icon that must up-scale. Used where a glyph must be an
// ImageSource bound into a LARGE Image (e.g. the volume-flyout header, whose authentic SndVolSSO raster topped out at a
// 32px native frame and upscaled soft in the ~44px header). The glyph is normalised into a transparent 100x100 box (so it
// keeps a square aspect + a little padding under Stretch=Uniform, never clipped) and flattened. Cached + frozen.
internal static class GlyphImage81
{
	private static readonly object _gate = new object();
	private static readonly Dictionary<string, ImageSource> _cache = new Dictionary<string, ImageSource>(StringComparer.Ordinal);

	public static ImageSource Get(string font, int codepoint, Color color, double fillFraction = 0.8)
	{
		string key = font + ":" + codepoint + ":" + color + ":" + fillFraction.ToString("0.00", CultureInfo.InvariantCulture);
		lock (_gate)
		{
			if (_cache.TryGetValue(key, out ImageSource c))
			{
				return c;
			}
			ImageSource img = Build(font, codepoint, color, fillFraction);
			_cache[key] = img;
			return img;
		}
	}

	private static ImageSource Build(string font, int codepoint, Color color, double fillFraction)
	{
		try
		{
			Typeface tf = new Typeface(new FontFamily(font), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
			FormattedText ft = new FormattedText(char.ConvertFromUtf32(codepoint), CultureInfo.InvariantCulture,
				FlowDirection.LeftToRight, tf, 100.0, Brushes.White, 1.0);
			Geometry raw = ft.BuildGeometry(new Point(0.0, 0.0));
			Rect b = raw.Bounds;

			DrawingGroup group = new DrawingGroup();
			// transparent 100x100 bound -> square aspect + built-in padding so Uniform never crops the glyph edge-to-edge.
			group.Children.Add(new GeometryDrawing(Brushes.Transparent, null, new RectangleGeometry(new Rect(0.0, 0.0, 100.0, 100.0))));
			if (!b.IsEmpty && b.Width > 0.0 && b.Height > 0.0)
			{
				double target = 100.0 * Math.Clamp(fillFraction, 0.1, 1.0);
				double scale = Math.Min(target / b.Width, target / b.Height);
				TransformGroup tg = new TransformGroup();
				tg.Children.Add(new TranslateTransform(-b.X, -b.Y));
				tg.Children.Add(new ScaleTransform(scale, scale));
				tg.Children.Add(new TranslateTransform((100.0 - b.Width * scale) / 2.0, (100.0 - b.Height * scale) / 2.0));
				Geometry g = raw.CloneCurrentValue();
				g.Transform = tg;
				Geometry flat = g.GetFlattenedPathGeometry();
				flat.Freeze();
				SolidColorBrush brush = new SolidColorBrush(color);
				brush.Freeze();
				group.Children.Add(new GeometryDrawing(brush, null, flat));
			}
			group.Freeze();
			DrawingImage di = new DrawingImage(group);
			di.Freeze();
			return di;
		}
		catch
		{
			return null;
		}
	}
}
