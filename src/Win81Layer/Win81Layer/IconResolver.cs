using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Win81Layer;

// Single choke-point that guarantees NO app/window/tray icon in the launcher ever renders as a blank or a
// featureless white circle. Two jobs: detect a "blank" bitmap (fully transparent, near-all-white/low-chroma, a
// single uniform colour, or near-empty) so a bad HICON is treated as a miss and the caller's fallback chain keeps
// walking; and, as the last resort, synthesise a Metro-style letter tile (the app's initial on a stable branded
// colour) so the result is always a real, recognisable icon. See memory icon-whitecircle-and-start-hotspot.
public static class IconResolver
{
	// Authentic-ish Metro accent palette; index chosen by a stable name hash so a given app always gets the same
	// colour across sessions (matches the deterministic-tile-colour behaviour used elsewhere).
	private static readonly Color[] Palette = new Color[]
	{
		Color.FromRgb(0, 120, 215),   // blue
		Color.FromRgb(16, 137, 62),   // green
		Color.FromRgb(202, 80, 16),   // orange
		Color.FromRgb(180, 0, 158),   // magenta
		Color.FromRgb(0, 153, 188),   // cyan
		Color.FromRgb(122, 117, 116), // taupe
		Color.FromRgb(104, 33, 122),  // purple
		Color.FromRgb(0, 130, 114),   // teal
		Color.FromRgb(191, 0, 119),   // pink
		Color.FromRgb(67, 81, 112),   // slate
		Color.FromRgb(218, 59, 1),    // vermilion
		Color.FromRgb(45, 125, 154)   // steel
	};

	private static readonly Dictionary<string, ImageSource> _tileCache = new Dictionary<string, ImageSource>();

	// Returns true when the bitmap carries no meaningful icon: null/zero-size, essentially fully transparent,
	// overwhelmingly near-white and low-chroma (the old FallbackTrayIcon and Gather's degenerate white HICON),
	// or a single uniform colour. Kept conservative so a real white-on-transparent glyph is NOT flagged.
	public static bool IsBlank(ImageSource? source)
	{
		if (source is not BitmapSource bitmap)
		{
			return source == null;
		}
		try
		{
			BitmapSource bmp = bitmap.Format == PixelFormats.Bgra32 ? bitmap : new FormatConvertedBitmap(bitmap, PixelFormats.Bgra32, null, 0.0);
			int w = bmp.PixelWidth;
			int h = bmp.PixelHeight;
			if (w <= 0 || h <= 0)
			{
				return true;
			}
			int stride = w * 4;
			byte[] px = new byte[h * stride];
			bmp.CopyPixels(px, stride, 0);
			long visible = 0L;
			int rMin = 255, rMax = 0, gMin = 255, gMax = 0, bMin = 255, bMax = 0;
			for (int o = 0; o < px.Length; o += 4)
			{
				if (px[o + 3] < 24)
				{
					continue;
				}
				int b = px[o];
				int g = px[o + 1];
				int r = px[o + 2];
				visible++;
				if (r < rMin) rMin = r; if (r > rMax) rMax = r;
				if (g < gMin) gMin = g; if (g > gMax) gMax = g;
				if (b < bMin) bMin = b; if (b > bMax) bMax = b;
			}
			long total = (long)w * h;
			if (visible * 200 < total)
			{
				return true;   // < 0.5% opaque coverage -> effectively empty/transparent
			}
			// A single-colour result counts as blank ONLY when the opaque pixels FILL essentially the whole tile (a
			// solid block with no drawn shape). A monochrome SILHOUETTE — a recognisable shape on transparency, even
			// in one colour (a white app logo, a whitened dark glyph, a mono mark) — has lower coverage and is KEPT,
			// never replaced by a letter tile. This is the fix for real icons being wrongly turned into "X" tiles.
			bool fills = visible * 100 >= total * 70;
			if (fills && rMax - rMin <= 10 && gMax - gMin <= 10 && bMax - bMin <= 10)
			{
				return true;
			}
			return false;
		}
		catch
		{
			return false;
		}
	}

	// The candidate if it is a real, non-blank icon; otherwise a generated letter tile. Never returns null.
	public static ImageSource EnsureNonBlank(ImageSource? candidate, string? name, Color? accent = null)
	{
		if (candidate != null && !IsBlank(candidate))
		{
			return candidate;
		}
		return LetterTile(name, accent ?? AccentFor(name));
	}

	public static Color AccentFor(string? name)
	{
		if (string.IsNullOrWhiteSpace(name))
		{
			return Palette[0];
		}
		uint hash = 2166136261u;
		string s = name.Trim().ToLowerInvariant();
		foreach (char c in s)
		{
			hash = (hash ^ c) * 16777619u;
		}
		return Palette[(int)(hash % (uint)Palette.Length)];
	}

	private static string FirstGrapheme(string? name)
	{
		if (string.IsNullOrWhiteSpace(name))
		{
			return "#";
		}
		string t = name.TrimStart();
		foreach (char c in t)
		{
			if (!char.IsWhiteSpace(c))
			{
				return char.ToUpperInvariant(c).ToString();
			}
		}
		return "#";
	}

	// A frozen accent square with the app's initial centred in white — the Metro "no icon" tile. Rendered large (128px)
	// so it stays sharp when a big Start tile (150px) uses it; it downscales cleanly for the ~24px taskbar.
	public static ImageSource LetterTile(string? name, Color accent, int px = 128)
	{
		string letter = FirstGrapheme(name);
		string key = letter + "|" + accent.ToString();
		if (_tileCache.TryGetValue(key, out ImageSource cached))
		{
			return cached;
		}
		DrawingVisual dv = new DrawingVisual();
		using (DrawingContext dc = dv.RenderOpen())
		{
			DrawLetter(dc, new Rect(0.0, 0.0, px, px), letter, accent);
		}
		RenderTargetBitmap rtb = new RenderTargetBitmap(px, px, 96.0, 96.0, PixelFormats.Pbgra32);
		rtb.Render(dv);
		rtb.Freeze();
		if (_tileCache.Count >= 256) { _tileCache.Clear(); }   // bound the strong bitmap cache over long uptime; icons re-render cheaply on a miss
		_tileCache[key] = rtb;
		return rtb;
	}

	// Shared draw used both by LetterTile and by surfaces (e.g. AppListItem) that render into a DrawingContext.
	public static void DrawLetter(DrawingContext dc, Rect rect, string? name, Color accent)
	{
		string letter = FirstGrapheme(name);
		dc.DrawRoundedRectangle(new SolidColorBrush(accent), null, rect, 2.0, 2.0);
		Typeface tf = new Typeface(new FontFamily("Segoe UI Semilight, Segoe UI"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
		FormattedText ft = new FormattedText(letter, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, tf, rect.Height * 0.52, Brushes.White, 1.0)
		{
			TextAlignment = TextAlignment.Center
		};
		double y = rect.Y + (rect.Height - ft.Height) / 2.0;
		dc.DrawText(ft, new System.Windows.Point(rect.X + rect.Width / 2.0, y));
	}
}
