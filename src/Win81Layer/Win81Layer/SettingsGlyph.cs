using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;

namespace Win81Layer;

// The ONE canonical Settings / "All settings" glyph for the whole launcher: the EXACT Segoe UI Symbol E115 cog (the
// classic Win8.1 / Windows Phone "Settings" gear the user picked), converted from the font to a vector path, white
// monochrome, no gradients, shadows, outlines or 3D. Drawn as a frozen, cached vector DrawingImage on a 32-unit grid, so it stays
// pixel-sharp at every size (16-256 px) instead of a downscaled raster, and so one semantic action (Settings)
// maps to one icon everywhere it appears (Action Center, PC Settings, etc.).
//
// The sun/rays glyph stays reserved EXCLUSIVELY for display Brightness — it must never be used for Settings.
// Attention/error overlays are intentionally NOT baked in: a Settings icon is just the clean gear unless the
// launcher has a real state that needs one.
internal static class SettingsGlyph
{
	private static readonly object Gate = new object();

	private static readonly Dictionary<string, ImageSource> Cache = new Dictionary<string, ImageSource>(StringComparer.Ordinal);

	// The gear is the EXACT Segoe UI Symbol E115 glyph (the classic Win8.1 / Windows Phone "Settings" cog), converted
	// to a vector path and scaled/centred on the 32-unit grid (see BuildGear) so it renders identically at any size.
	private static Geometry _geo;

	// White by default; pass a colour for the disabled (gray) variant. The result is a size-independent vector, so
	// callers scale it via their Image Width/Height — one DrawingImage serves 16-256 px, always crisp.
	public static ImageSource Gear(Color? color = null)
	{
		Color c = color ?? Colors.White;
		string key = c.ToString();
		lock (Gate)
		{
			if (Cache.TryGetValue(key, out ImageSource cached))
			{
				return cached;
			}
			ImageSource img = Build(c);
			Cache[key] = img;
			return img;
		}
	}

	private static ImageSource Build(Color color)
	{
		if (_geo == null)
		{
			Geometry g = BuildGear();
			g.Freeze();
			_geo = g;
		}
		SolidColorBrush brush = new SolidColorBrush(color);
		brush.Freeze();
		// No GuidelineSet (its 32-grid pixel-snapping distorted the glyph's curves = the "eaten" look) and no clip.
		DrawingGroup group = new DrawingGroup();
		group.Children.Add(new GeometryDrawing(Brushes.Transparent, null, new RectangleGeometry(new Rect(0.0, 0.0, 32.0, 32.0))));
		group.Children.Add(new GeometryDrawing(brush, null, _geo));
		group.Freeze();
		DrawingImage image = new DrawingImage(group);
		image.Freeze();
		return image;
	}

	// Build the geometry from the actual Segoe UI Symbol E115 glyph so the shape is EXACT (not an approximation),
	// scaled + centred into the 32-unit grid. Falls back to a plain disc only if the font/glyph is unavailable.
	private static Geometry BuildGear()
	{
		try
		{
			Typeface tf = new Typeface(new FontFamily("Segoe UI Symbol"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
			FormattedText ft = new FormattedText(((char)57621).ToString(), System.Globalization.CultureInfo.InvariantCulture,
				FlowDirection.LeftToRight, tf, 100.0, Brushes.White, 1.0);
			Geometry raw = ft.BuildGeometry(new Point(0.0, 0.0));
			Rect b = raw.Bounds;
			if (!b.IsEmpty && b.Width > 0.0 && b.Height > 0.0)
			{
				Rect target = new Rect(1.0, 1.0, 30.0, 30.0);
				double scale = Math.Min(target.Width / b.Width, target.Height / b.Height);
				TransformGroup tg = new TransformGroup();
				tg.Children.Add(new TranslateTransform(0.0 - b.X, 0.0 - b.Y));
				tg.Children.Add(new ScaleTransform(scale, scale));
				tg.Children.Add(new TranslateTransform(
					target.X + (target.Width - b.Width * scale) / 2.0,
					target.Y + (target.Height - b.Height * scale) / 2.0));
				Geometry g = raw.CloneCurrentValue();
				g.Transform = tg;
				g.Freeze();   // keep the SMOOTH bezier curves — do NOT flatten (flattening faceted the edges = the "eaten" look)
				return g;
			}
		}
		catch
		{
		}
		return new EllipseGeometry(new Point(16.0, 16.0), 14.0, 14.0);
	}

}
