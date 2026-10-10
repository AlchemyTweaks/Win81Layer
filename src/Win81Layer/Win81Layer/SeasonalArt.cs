#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Win81Layer;

// Seasonal artwork drawn entirely in code: the Start pattern tiles, the desktop wallpapers and the tiles of the
// ambient Start effect. Nothing is shipped or downloaded. Every random choice uses a fixed seed, so the same art
// renders every time. Unit geometries are built once and frozen, so they can be shared with worker threads.
internal static class SeasonalArt
{
	public static readonly string[] PatternIds = new string[6] { "pumpkins", "bats", "webs", "ornaments", "snowflakes", "holly" };

	public const int WallpaperVersion = 1;

	public static readonly string SeasonalDir = Path.Combine(
		Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
		"Win81Layer",
		"seasonal");

	private static readonly Color Gold = Color.FromRgb(201, 161, 59);

	private static readonly Geometry BatUnit = BuildBat();

	private static readonly Geometry BatWideUnit = BuildBatWide();

	private static readonly Geometry PumpkinLobeLeft = Ellipse(-0.20, 0.07, 0.30, 0.33);

	private static readonly Geometry PumpkinLobeRight = Ellipse(0.20, 0.07, 0.30, 0.33);

	private static readonly Geometry PumpkinLobeCenter = Ellipse(0.0, 0.05, 0.26, 0.36);

	private static readonly Geometry PumpkinRidges = BuildPumpkinRidges();

	private static readonly Geometry PumpkinStem = BuildPumpkinStem();

	private static readonly Geometry PumpkinFace = BuildPumpkinFace();

	private static readonly Geometry PumpkinSilhouette = Union(PumpkinLobeLeft, PumpkinLobeRight, PumpkinLobeCenter);

	private static readonly Geometry SnowflakeUnit = BuildSnowflake();

	private static readonly Geometry HollyLeafUnit = BuildHollyLeaf();

	private static readonly Geometry HollyVeinUnit = Freeze(new LineGeometry(new Point(0.03, 0.0), new Point(0.49, 0.0)));

	public static bool IsSeasonalPattern(string? pattern)
	{
		return pattern != null && Array.IndexOf(PatternIds, pattern) >= 0;
	}

	public static double TileSize(string pattern)
	{
		return pattern switch
		{
			"pumpkins" => 440.0,
			"ornaments" => 440.0,
			"bats" => 240.0,
			"webs" => 260.0,
			_ => 220.0,
		};
	}

	// The signature tiles leave a clear band at their top and bottom edges (tile y 0-44 and 408-440). Shifting the
	// tiles down by this much lines that band up with the Start header (y 40-110), so no motif sits behind the title,
	// the user name or the header buttons.
	public static double TileOffsetY(string pattern)
	{
		return (pattern == "pumpkins" || pattern == "ornaments") ? 70.0 : 0.0;
	}

	// Where a scaled-down tile starts in a small preview (scale < 1). The large signature tiles centre their main
	// motif in the preview (an 80x50 thumbnail at scale 0.4) instead of showing their empty header band.
	public static Point ThumbnailOrigin(string pattern, double scale)
	{
		(double fx, double fy) = pattern switch
		{
			"pumpkins" => (104.0, 146.0),
			"ornaments" => (98.0, 140.0),
			_ => (double.NaN, double.NaN),
		};
		if (double.IsNaN(fx))
		{
			return new Point(0.0, 0.0);
		}
		return new Point(100.0 * scale - fx * scale, 62.5 * scale - fy * scale);
	}

	// ===================================================================== pattern tiles

	// Draws one tile of a seasonal Start pattern into g (the base rectangle is already there). Every motif keeps at
	// least 2 DIU away from the tile edge, so the tiled brush has no seams.
	public static void DrawPatternTile(DrawingGroup g, string pattern, Color baseColor, Color accent, double s)
	{
		using DrawingContext dc = g.Append();
		switch (pattern)
		{
		case "pumpkins":
			DrawPumpkinsTile(dc, baseColor, accent);
			break;
		case "bats":
			DrawBatsTile(dc, baseColor, accent);
			break;
		case "webs":
			DrawWebsTile(dc, baseColor, accent);
			break;
		case "ornaments":
			DrawOrnamentsTile(dc, baseColor, accent);
			break;
		case "snowflakes":
			DrawSnowflakesTile(dc, baseColor, accent);
			break;
		case "holly":
			DrawHollyTile(dc, baseColor, accent);
			break;
		}
	}

	// 440 DIU tile: five lit jack-o'-lanterns of different sizes and tilts scattered over two staggered rows, so the
	// repeat does not read as a grid. Motifs stay within tile y 48-404 (see TileOffsetY).
	private static void DrawPumpkinsTile(DrawingContext dc, Color baseColor, Color accent)
	{
		PumpkinColors pc = PumpkinColors.FromAccent(accent, Color.FromRgb(255, 210, 122)) with
		{
			Ridge = ColorMath.Multiply(accent, 0.40)
		};
		Stars(dc, new (double, double, double)[9]
		{
			(214, 64, 1.5), (36, 226, 1.1), (402, 196, 1.3), (176, 236, 0.9), (290, 392, 1.2), (128, 388, 1.0), (424, 62, 1.0), (22, 70, 0.9), (360, 234, 0.8)
		}, Color.FromArgb(105, 255, 255, 255));
		foreach ((double x, double y, double w, double rot) in new (double, double, double, double)[5]
		{
			(104, 146, 92, -8), (316, 108, 56, 10), (252, 300, 84, 4), (66, 336, 50, -12), (398, 304, 40, 14)
		})
		{
			DrawGlow(dc, x, y + 0.08 * w, 0.95 * w, Color.FromArgb(34, 255, 160, 60));
			DrawPumpkin(dc, x, y, w, rot, pc, 0.78, face: true, glow: null);
		}
	}

	private static void DrawBatsTile(DrawingContext dc, Color baseColor, Color accent)
	{
		Stars(dc, new (double, double, double)[5] { (110, 100, 1.4), (20, 120, 1.0), (215, 120, 1.2), (120, 225, 1.0), (30, 20, 0.9) }, Color.FromArgb(89, 255, 255, 255));
		Geometry crescent = Crescent(200, 200, 16);
		DrawGlow(dc, 200, 200, 34, Color.FromArgb(46, accent.R, accent.G, accent.B));
		dc.DrawGeometry(Solid(Color.FromArgb(150, Lerp(accent.R, 255, 0.35), Lerp(accent.G, 255, 0.35), Lerp(accent.B, 255, 0.35))), null, crescent);
		Brush fill = Solid(Color.FromArgb(185, 0, 0, 0));
		Pen rim = MakePen(Solid(Color.FromArgb(64, accent.R, accent.G, accent.B)), 1.0);
		DrawBat(dc, 62, 58, 52, -10, fill, rim);
		DrawBat(dc, 176, 42, 30, 8, fill, rim);
		DrawBat(dc, 148, 146, 64, -4, fill, rim);
		DrawBat(dc, 42, 190, 26, 14, fill, rim);
	}

	private static void DrawWebsTile(DrawingContext dc, Color baseColor, Color accent)
	{
		Pen web = MakePen(Solid(Color.FromArgb(70, 255, 255, 255)), 1.0);
		dc.DrawGeometry(null, web, OrbWeb(new Point(84, 92), 70, 11, 6, 0.0));
		dc.DrawGeometry(null, MakePen(Solid(Color.FromArgb(52, 255, 255, 255)), 0.9), OrbWeb(new Point(204, 210), 38, 9, 4, 12.0));
		dc.DrawLine(MakePen(Solid(Color.FromArgb(70, 255, 255, 255)), 0.9), new Point(150, 6), new Point(150, 120));
		DrawSpider(dc, 150, 128, 1.0, Solid(Color.FromArgb(220, 0, 0, 0)));
		PumpkinColors pc = PumpkinColors.FromAccent(accent, Color.FromRgb(255, 210, 122));
		DrawPumpkin(dc, 212, 74, 34, 6, pc, 0.75, face: true, glow: null);
		Stars(dc, new (double, double, double)[3] { (40, 210, 1.0), (240, 140, 1.2), (110, 238, 0.9) }, Color.FromArgb(80, 255, 255, 255));
	}

	// 440 DIU tile: baubles in red, gold and silver at different sizes, a holly sprig and a few sparkles, over two
	// staggered rows. No strings hanging from nowhere. Motifs stay within tile y 48-404 (see TileOffsetY).
	private static void DrawOrnamentsTile(DrawingContext dc, Color baseColor, Color accent)
	{
		Color sparkle = Color.FromArgb(170, 255, 243, 196);
		Sparkle(dc, 196, 66, 7, sparkle);
		Sparkle(dc, 38, 236, 5, sparkle);
		Sparkle(dc, 352, 214, 4.5, sparkle);
		Sparkle(dc, 152, 384, 5, sparkle);
		Sparkle(dc, 420, 92, 3.5, sparkle);
		Sparkle(dc, 300, 388, 3.5, sparkle);
		Color gold = Color.FromRgb(240, 180, 41);
		Color silver = Color.FromRgb(201, 214, 224);
		dc.PushOpacity(0.88);
		DrawBauble(dc, 98, 140, 34, accent, string_: 0);
		DrawBauble(dc, 304, 112, 24, gold, string_: 0);
		DrawBauble(dc, 250, 300, 40, silver, string_: 0);
		DrawBauble(dc, 62, 340, 20, gold, string_: 0);
		DrawBauble(dc, 392, 334, 18, accent, string_: 0);
		dc.Pop();
		HollyColors hc = new HollyColors(Color.FromRgb(46, 139, 87), Color.FromRgb(20, 90, 50), accent, Color.FromArgb(51, 255, 255, 255));
		dc.PushOpacity(0.85);
		DrawHolly(dc, 172, 222, 58, -24, hc);
		dc.Pop();
	}

	private static void DrawSnowflakesTile(DrawingContext dc, Color baseColor, Color accent)
	{
		Color flake = Color.FromArgb(118, 255, 255, 255);
		Snowflake(dc, 60, 64, 26, 0, flake);
		Snowflake(dc, 168, 46, 14, 15, Color.FromArgb(92, 255, 255, 255));
		Snowflake(dc, 150, 160, 34, 8, flake);
		Snowflake(dc, 40, 176, 12, 20, Color.FromArgb(84, 255, 255, 255));
		Snowflake(dc, 204, 204, 9, 0, Color.FromArgb(76, 255, 255, 255));
		Brush dot = Solid(Color.FromArgb(89, 255, 255, 255));
		foreach ((double x, double y) in new (double, double)[5] { (110, 110), (20, 30), (190, 110), (95, 200), (130, 20) })
		{
			dc.DrawEllipse(dot, null, new Point(x, y), 2.0, 2.0);
		}
	}

	private static void DrawHollyTile(DrawingContext dc, Color baseColor, Color accent)
	{
		HollyColors hc = new HollyColors(Color.FromRgb(46, 139, 87), Color.FromRgb(20, 90, 50), accent, Color.FromArgb(51, 255, 255, 255));
		dc.PushOpacity(0.62);
		DrawHolly(dc, 72, 76, 84, -20, hc);
		DrawHolly(dc, 166, 168, 66, 150, hc);
		dc.Pop();
		Snowflake(dc, 176, 56, 6, 0, Color.FromArgb(102, 255, 255, 255));
		Snowflake(dc, 52, 176, 6, 10, Color.FromArgb(102, 255, 255, 255));
	}

	// ===================================================================== motifs

	private sealed record PumpkinColors(Color Light, Color Mid, Color Dark, Color Ridge, Color Stem, Color FaceInner, Color FaceOuter)
	{
		public static PumpkinColors FromAccent(Color accent, Color face)
		{
			return new PumpkinColors(
				ColorMath.Lighten(accent, 0.30),
				accent,
				ColorMath.Multiply(accent, 0.45),
				ColorMath.Multiply(accent, 0.58),
				Color.FromRgb(94, 122, 46),
				Color.FromRgb(255, 244, 196),
				face);
		}
	}

	private sealed record HollyColors(Color Leaf, Color Vein, Color Berry, Color Outline);

	private static void DrawPumpkin(DrawingContext dc, double cx, double cy, double w, double rot, PumpkinColors pc, double opacity, bool face, Color? glow)
	{
		if (glow.HasValue)
		{
			DrawGlow(dc, cx, cy, w * 1.05, glow.Value);
		}
		bool pushed = opacity < 0.999;
		if (pushed)
		{
			dc.PushOpacity(opacity);
		}
		Pen seam = MakePen(Solid(pc.Ridge), Math.Max(0.8, w * 0.016));
		Brush side = Radial(new Point(0.42, 0.32), new Point(0.48, 0.42), 0.62, (0.0, ColorMath.Lighten(pc.Mid, 0.10)), (0.62, Mix(pc.Mid, pc.Dark, 0.25)), (1.0, pc.Dark));
		Brush center = Radial(new Point(0.40, 0.28), new Point(0.46, 0.40), 0.66, (0.0, pc.Light), (0.55, pc.Mid), (1.0, Mix(pc.Mid, pc.Dark, 0.65)));
		dc.DrawGeometry(side, seam, Placed(PumpkinLobeLeft, cx, cy, w, rot));
		dc.DrawGeometry(side, seam, Placed(PumpkinLobeRight, cx, cy, w, rot));
		dc.DrawGeometry(center, seam, Placed(PumpkinLobeCenter, cx, cy, w, rot));
		dc.DrawGeometry(null, MakePen(Solid(Color.FromArgb(120, pc.Ridge.R, pc.Ridge.G, pc.Ridge.B)), Math.Max(0.7, w * 0.012)), Placed(PumpkinRidges, cx, cy, w, rot));
		dc.DrawGeometry(Linear(new Point(0, 1), new Point(1, 0), (0.0, Mix(pc.Stem, Colors.Black, 0.35)), (1.0, ColorMath.Lighten(pc.Stem, 0.18))), null, Placed(PumpkinStem, cx, cy, w, rot));
		if (pushed)
		{
			dc.Pop();
		}
		if (face)
		{
			// The carved face is lit from inside: always at full strength, with a soft glow, even on a faded pumpkin.
			if (pushed)
			{
				Point fc = Rotate(0.0, 0.10 * w, rot);
				DrawGlow(dc, cx + fc.X, cy + fc.Y, 0.30 * w, Color.FromArgb(70, pc.FaceOuter.R, pc.FaceOuter.G, pc.FaceOuter.B));
			}
			Brush faceFill = Radial(new Point(0.5, 0.45), new Point(0.5, 0.5), 0.75, (0.0, pc.FaceInner), (1.0, pc.FaceOuter));
			dc.DrawGeometry(faceFill, null, Placed(PumpkinFace, cx, cy, w, rot));
		}
	}

	private static void DrawBat(DrawingContext dc, double cx, double cy, double w, double rot, Brush fill, Pen? rim)
	{
		dc.DrawGeometry(fill, rim, Placed(BatUnit, cx, cy, w, rot));
	}

	private static void Snowflake(DrawingContext dc, double cx, double cy, double r, double rot, Color color)
	{
		Brush b = Solid(color);
		Pen pen = MakePen(b, Math.Max(1.1, 0.09 * r));
		dc.DrawGeometry(null, pen, Placed(SnowflakeUnit, cx, cy, r, rot));
		dc.DrawEllipse(b, null, new Point(cx, cy), 0.12 * r + 0.4, 0.12 * r + 0.4);
	}

	private static void Sparkle(DrawingContext dc, double cx, double cy, double r, Color color)
	{
		StreamGeometry g = new StreamGeometry();
		using (StreamGeometryContext c = g.Open())
		{
			for (int i = 0; i < 16; i++)
			{
				double a = (-90.0 + i * 22.5) * Math.PI / 180.0;
				double rr = (i % 2 == 0) ? (i % 4 == 0 ? r : r * 0.62) : r * 0.2;
				Point p = new Point(cx + Math.Cos(a) * rr, cy + Math.Sin(a) * rr);
				if (i == 0)
				{
					c.BeginFigure(p, isFilled: true, isClosed: true);
				}
				else
				{
					c.LineTo(p, isStroked: false, isSmoothJoin: false);
				}
			}
		}
		g.Freeze();
		DrawGlow(dc, cx, cy, r * 1.6, Color.FromArgb((byte)(color.A / 4), color.R, color.G, color.B));
		dc.DrawGeometry(Solid(color), null, g);
	}

	private static void DrawBauble(DrawingContext dc, double cx, double cy, double r, Color c, double string_)
	{
		Color capDark = Color.FromRgb(122, 92, 30);
		if (string_ > 0)
		{
			dc.DrawLine(MakePen(Solid(Color.FromArgb(150, 230, 230, 230)), Math.Max(0.8, r * 0.035)), new Point(cx, cy - 1.32 * r - string_), new Point(cx, cy - 1.44 * r));
		}
		dc.DrawEllipse(null, MakePen(Solid(Gold), Math.Max(1.0, 0.06 * r)), new Point(cx, cy - 1.32 * r), 0.12 * r, 0.12 * r);
		Brush cap = Linear(new Point(0, 0), new Point(1, 0), (0.0, capDark), (0.45, Color.FromRgb(240, 214, 130)), (1.0, Color.FromRgb(150, 115, 40)));
		dc.DrawRoundedRectangle(cap, null, new Rect(cx - 0.28 * r, cy - 1.18 * r, 0.56 * r, 0.26 * r), 0.05 * r, 0.05 * r);
		Brush body = Radial(new Point(0.32, 0.28), new Point(0.40, 0.38), 0.75, (0.0, ColorMath.Lighten(c, 0.30)), (0.42, c), (0.85, ColorMath.Multiply(c, 0.35)), (1.0, ColorMath.Multiply(c, 0.5)));
		dc.DrawEllipse(body, null, new Point(cx, cy), r, r);
		// soft rim light on the lower right, as if lit from the room behind
		dc.DrawEllipse(Radial(new Point(0.74, 0.80), new Point(0.62, 0.64), 0.55, (0.0, Color.FromArgb(46, 255, 255, 255)), (1.0, Color.FromArgb(0, 255, 255, 255))), null, new Point(cx, cy), r, r);
		dc.PushTransform(new RotateTransform(-30.0, cx - 0.38 * r, cy - 0.42 * r));
		dc.DrawEllipse(Solid(Color.FromArgb(150, 255, 255, 255)), null, new Point(cx - 0.38 * r, cy - 0.42 * r), 0.20 * r, 0.12 * r);
		dc.Pop();
	}

	private static void DrawHolly(DrawingContext dc, double cx, double cy, double scale, double rot, HollyColors hc)
	{
		Brush leafFill = Linear(new Point(0, 0), new Point(0, 1), (0.0, ColorMath.Lighten(hc.Leaf, 0.12)), (1.0, Mix(hc.Leaf, Colors.Black, 0.25)));
		Pen outline = MakePen(Solid(hc.Outline), 0.8);
		Pen vein = MakePen(Solid(hc.Vein), Math.Max(0.8, 0.015 * scale));
		// two leaves fanning out and a shorter one behind them pointing up, berries in front
		foreach (double leafRot in new double[3] { -95.0, -150.0, -30.0 })
		{
			double s = (leafRot == -95.0) ? scale * 0.72 : scale;
			Geometry leaf = Placed(HollyLeafUnit, cx, cy, s, rot + leafRot);
			dc.DrawGeometry(leafFill, outline, leaf);
			dc.DrawGeometry(null, vein, Placed(HollyVeinUnit, cx, cy, s, rot + leafRot));
		}
		Brush berry = Radial(new Point(0.35, 0.3), new Point(0.45, 0.45), 0.7, (0.0, ColorMath.Lighten(hc.Berry, 0.35)), (0.6, hc.Berry), (1.0, ColorMath.Multiply(hc.Berry, 0.35)));
		Brush glint = Solid(Color.FromArgb(179, 255, 255, 255));
		foreach ((double bx, double by) in new (double, double)[3] { (-0.045, 0.015), (0.05, 0.02), (0.0, 0.095) })
		{
			Point p = Rotate(bx * scale, by * scale, rot);
			double br = 0.075 * scale;
			dc.DrawEllipse(berry, null, new Point(cx + p.X, cy + p.Y), br, br);
			dc.DrawEllipse(glint, null, new Point(cx + p.X - 0.025 * scale, cy + p.Y - 0.025 * scale), 0.02 * scale, 0.02 * scale);
		}
	}

	private static void DrawSpider(DrawingContext dc, double cx, double cy, double k, Brush fill)
	{
		Pen leg = MakePen(fill, 1.1 * k);
		StreamGeometry legs = new StreamGeometry();
		using (StreamGeometryContext c = legs.Open())
		{
			for (int side = -1; side <= 1; side += 2)
			{
				for (int j = 0; j < 4; j++)
				{
					double y0 = (j - 1.5) * 2.4 * k;
					c.BeginFigure(new Point(cx + side * 4.0 * k, cy + y0), isFilled: false, isClosed: false);
					c.LineTo(new Point(cx + side * 10.0 * k, cy + y0 * 1.5 - 3.0 * k), isStroked: true, isSmoothJoin: true);
					c.LineTo(new Point(cx + side * 13.0 * k, cy + y0 * 2.0 + 2.0 * k), isStroked: true, isSmoothJoin: true);
				}
			}
		}
		legs.Freeze();
		dc.DrawGeometry(null, leg, legs);
		dc.DrawEllipse(fill, null, new Point(cx, cy + 4.0 * k), 5.0 * k, 6.0 * k);
		dc.DrawEllipse(fill, null, new Point(cx, cy - 4.0 * k), 3.2 * k, 3.2 * k);
	}

	// A round orb web: radial threads with sagging spiral rings. startDeg rotates the thread fan.
	private static Geometry OrbWeb(Point hub, double r, int threads, int rings, double startDeg)
	{
		List<(double Angle, double Length)> spokes = new List<(double, double)>();
		for (int i = 0; i < threads; i++)
		{
			double wobble = ((i * 37) % 7 - 3) * 0.02;
			spokes.Add(((startDeg + i * 360.0 / threads) * Math.PI / 180.0, r * (1.0 + wobble)));
		}
		return FramedWeb(hub, spokes, rings, closed: true);
	}

	// A web whose spokes end at given lengths (so it can fill a fork between branches). Rings sit at fixed fractions
	// of each spoke and sag toward the hub between spokes.
	private static Geometry FramedWeb(Point hub, List<(double Angle, double Length)> spokes, int rings, bool closed)
	{
		StreamGeometry g = new StreamGeometry();
		using (StreamGeometryContext c = g.Open())
		{
			foreach ((double a, double len) in spokes)
			{
				c.BeginFigure(hub, isFilled: false, isClosed: false);
				c.LineTo(new Point(hub.X + Math.Cos(a) * len, hub.Y + Math.Sin(a) * len), isStroked: true, isSmoothJoin: false);
			}
			int n = spokes.Count;
			int segs = closed ? n : n - 1;
			for (int k = 1; k <= rings; k++)
			{
				double f = 0.14 + 0.80 * k / rings;
				for (int i = 0; i < segs; i++)
				{
					(double a0, double l0) = spokes[i];
					(double a1, double l1) = spokes[(i + 1) % n];
					Point p0 = new Point(hub.X + Math.Cos(a0) * l0 * f, hub.Y + Math.Sin(a0) * l0 * f);
					Point p1 = new Point(hub.X + Math.Cos(a1) * l1 * f, hub.Y + Math.Sin(a1) * l1 * f);
					double mid = Math.Atan2(Math.Sin(a0) + Math.Sin(a1), Math.Cos(a0) + Math.Cos(a1));
					double lm = (l0 + l1) * 0.5 * f * 0.86;
					Point ctl = new Point(hub.X + Math.Cos(mid) * lm, hub.Y + Math.Sin(mid) * lm);
					c.BeginFigure(p0, isFilled: false, isClosed: false);
					c.QuadraticBezierTo(ctl, p1, isStroked: true, isSmoothJoin: true);
				}
			}
		}
		g.Freeze();
		return g;
	}

	private static Geometry Crescent(double cx, double cy, double r)
	{
		CombinedGeometry g = new CombinedGeometry(GeometryCombineMode.Exclude, new EllipseGeometry(new Point(cx, cy), r, r), new EllipseGeometry(new Point(cx + 0.38 * r, cy - 0.25 * r), 0.88 * r, 0.88 * r));
		g.Freeze();
		return g;
	}

	private static void Stars(DrawingContext dc, (double X, double Y, double R)[] stars, Color color)
	{
		Brush b = Solid(color);
		foreach ((double x, double y, double r) in stars)
		{
			dc.DrawEllipse(b, null, new Point(x, y), r, r);
		}
	}

	private static void DrawGlow(DrawingContext dc, double cx, double cy, double r, Color color)
	{
		Brush glow = Radial(new Point(0.5, 0.5), new Point(0.5, 0.5), 0.5, (0.0, color), (0.45, Color.FromArgb((byte)(color.A * 0.55), color.R, color.G, color.B)), (1.0, Color.FromArgb(0, color.R, color.G, color.B)));
		dc.DrawEllipse(glow, null, new Point(cx, cy), r, r);
	}

	// ===================================================================== unit geometry builders

	private static Geometry BuildBat()
	{
		StreamGeometry g = new StreamGeometry { FillRule = FillRule.Nonzero };
		using (StreamGeometryContext c = g.Open())
		{
			c.BeginFigure(new Point(0.0, -0.07), isFilled: true, isClosed: true);
			c.LineTo(new Point(0.03, -0.14), true, false);
			c.LineTo(new Point(0.055, -0.06), true, false);
			c.QuadraticBezierTo(new Point(0.20, -0.16), new Point(0.50, -0.10), true, true);
			c.QuadraticBezierTo(new Point(0.42, -0.02), new Point(0.40, 0.06), true, false);
			c.QuadraticBezierTo(new Point(0.33, 0.0), new Point(0.27, 0.07), true, false);
			c.QuadraticBezierTo(new Point(0.20, 0.01), new Point(0.13, 0.06), true, false);
			c.QuadraticBezierTo(new Point(0.08, 0.02), new Point(0.04, 0.10), true, false);
			c.LineTo(new Point(0.0, 0.14), true, false);
			c.LineTo(new Point(-0.04, 0.10), true, false);
			c.QuadraticBezierTo(new Point(-0.08, 0.02), new Point(-0.13, 0.06), true, false);
			c.QuadraticBezierTo(new Point(-0.20, 0.01), new Point(-0.27, 0.07), true, false);
			c.QuadraticBezierTo(new Point(-0.33, 0.0), new Point(-0.40, 0.06), true, false);
			c.QuadraticBezierTo(new Point(-0.42, -0.02), new Point(-0.50, -0.10), true, false);
			c.QuadraticBezierTo(new Point(-0.20, -0.16), new Point(-0.055, -0.06), true, true);
			c.LineTo(new Point(-0.03, -0.14), true, false);
		}
		g.Freeze();
		return g;
	}

	// A bat with raised, broad wings and deep scallops along the trailing edge (width 1, origin at the body).
	private static Geometry BuildBatWide()
	{
		(double X, double Y, double CX, double CY, bool Curve)[] half = new (double, double, double, double, bool)[9]
		{
			(0.028, -0.135, 0, 0, false),
			(0.048, -0.050, 0, 0, false),
			(0.500, -0.170, 0.170, -0.210, true),
			(0.412, 0.070, 0.440, -0.070, true),
			(0.300, 0.105, 0.360, -0.040, true),
			(0.185, 0.110, 0.240, -0.025, true),
			(0.070, 0.140, 0.125, 0.005, true),
			(0.030, 0.125, 0, 0, false),
			(0.000, 0.175, 0, 0, false)
		};
		StreamGeometry g = new StreamGeometry { FillRule = FillRule.Nonzero };
		using (StreamGeometryContext c = g.Open())
		{
			c.BeginFigure(new Point(0.0, -0.065), isFilled: true, isClosed: true);
			foreach ((double x, double y, double cx, double cy, bool curve) in half)
			{
				if (curve)
				{
					c.QuadraticBezierTo(new Point(cx, cy), new Point(x, y), true, false);
				}
				else
				{
					c.LineTo(new Point(x, y), true, false);
				}
			}
			// mirror back to the start: each segment ends at the previous point, with the same control point mirrored
			for (int i = half.Length - 1; i >= 0; i--)
			{
				Point end = i == 0 ? new Point(0.0, -0.065) : new Point(-half[i - 1].X, half[i - 1].Y);
				if (half[i].Curve)
				{
					c.QuadraticBezierTo(new Point(-half[i].CX, half[i].CY), end, true, false);
				}
				else
				{
					c.LineTo(end, true, false);
				}
			}
		}
		g.Freeze();
		return g;
	}

	private static Geometry BuildPumpkinRidges()
	{
		StreamGeometry g = new StreamGeometry();
		using (StreamGeometryContext c = g.Open())
		{
			// two inner ridge arcs on the front lobe, from the stem to the base
			c.BeginFigure(new Point(-0.03, -0.29), isFilled: false, isClosed: false);
			c.QuadraticBezierTo(new Point(-0.16, 0.05), new Point(-0.04, 0.40), true, true);
			c.BeginFigure(new Point(0.03, -0.29), isFilled: false, isClosed: false);
			c.QuadraticBezierTo(new Point(0.16, 0.05), new Point(0.04, 0.40), true, true);
		}
		g.Freeze();
		return g;
	}

	private static Geometry BuildPumpkinStem()
	{
		StreamGeometry g = new StreamGeometry();
		using (StreamGeometryContext c = g.Open())
		{
			c.BeginFigure(new Point(-0.05, -0.27), isFilled: true, isClosed: true);
			c.QuadraticBezierTo(new Point(-0.01, -0.36), new Point(0.03, -0.45), true, true);
			c.LineTo(new Point(0.09, -0.43), true, true);
			c.QuadraticBezierTo(new Point(0.05, -0.36), new Point(0.06, -0.27), true, true);
			c.QuadraticBezierTo(new Point(0.0, -0.25), new Point(-0.05, -0.27), true, true);
		}
		g.Freeze();
		return g;
	}

	private static Geometry BuildPumpkinFace()
	{
		StreamGeometry g = new StreamGeometry { FillRule = FillRule.Nonzero };
		using (StreamGeometryContext c = g.Open())
		{
			Poly(c, (-0.18, -0.01), (-0.05, -0.02), (-0.12, -0.14));
			Poly(c, (0.18, -0.01), (0.05, -0.02), (0.12, -0.14));
			Poly(c, (-0.035, 0.065), (0.035, 0.065), (0.0, 0.01));
			Poly(c, (-0.22, 0.11), (-0.12, 0.155), (-0.08, 0.115), (-0.03, 0.155), (0.03, 0.155), (0.08, 0.115), (0.12, 0.155), (0.22, 0.11),
				(0.15, 0.245), (0.06, 0.285), (0.03, 0.235), (-0.03, 0.235), (-0.06, 0.285), (-0.15, 0.245));
		}
		g.Freeze();
		return g;
	}

	private static Geometry BuildSnowflake()
	{
		StreamGeometry g = new StreamGeometry();
		using (StreamGeometryContext c = g.Open())
		{
			for (int k = 0; k < 6; k++)
			{
				double a = (-90.0 + 60.0 * k) * Math.PI / 180.0;
				Point tip = new Point(Math.Cos(a), Math.Sin(a));
				c.BeginFigure(new Point(0, 0), isFilled: false, isClosed: false);
				c.LineTo(tip, true, false);
				foreach ((double t, double len) in new (double, double)[2] { (0.5, 0.30), (0.76, 0.19) })
				{
					Point p = new Point(tip.X * t, tip.Y * t);
					foreach (double sign in new double[2] { -1.0, 1.0 })
					{
						double b = a + sign * 45.0 * Math.PI / 180.0;
						c.BeginFigure(p, isFilled: false, isClosed: false);
						c.LineTo(new Point(p.X + Math.Cos(b) * len, p.Y + Math.Sin(b) * len), true, false);
					}
				}
			}
		}
		g.Freeze();
		return g;
	}

	private static Geometry BuildHollyLeaf()
	{
		StreamGeometry g = new StreamGeometry();
		using (StreamGeometryContext c = g.Open())
		{
			(double, double)[] pts = new (double, double)[14]
			{
				(0.0, 0.0), (0.09, -0.10), (0.15, -0.065), (0.23, -0.13), (0.30, -0.075), (0.38, -0.12), (0.45, -0.05),
				(0.56, 0.0), (0.45, 0.05), (0.38, 0.12), (0.30, 0.075), (0.23, 0.13), (0.15, 0.065), (0.09, 0.10)
			};
			c.BeginFigure(new Point(pts[0].Item1, pts[0].Item2), isFilled: true, isClosed: true);
			for (int i = 1; i < pts.Length; i++)
			{
				// spines: the inner (valley) points are reached with a gentle curve so the leaf edge looks scalloped
				(double x, double y) = pts[i];
				(double px, double py) = pts[i - 1];
				bool valley = i % 2 == 0;
				if (valley)
				{
					c.QuadraticBezierTo(new Point((px + x) * 0.5, (py + y) * 0.5 * 0.55), new Point(x, y), true, false);
				}
				else
				{
					c.LineTo(new Point(x, y), true, false);
				}
			}
		}
		g.Freeze();
		return g;
	}

	private static void Poly(StreamGeometryContext c, params (double X, double Y)[] pts)
	{
		c.BeginFigure(new Point(pts[0].X, pts[0].Y), isFilled: true, isClosed: true);
		for (int i = 1; i < pts.Length; i++)
		{
			c.LineTo(new Point(pts[i].X, pts[i].Y), true, false);
		}
	}

	// ===================================================================== wallpapers

	public static (int Width, int Height) TargetWallpaperSize()
	{
		int w = 0;
		int h = 0;
		try
		{
			foreach (System.Windows.Forms.Screen screen in System.Windows.Forms.Screen.AllScreens)
			{
				if ((long)screen.Bounds.Width * screen.Bounds.Height > (long)w * h)
				{
					w = screen.Bounds.Width;
					h = screen.Bounds.Height;
				}
			}
		}
		catch
		{
		}
		if (w <= 0 || h <= 0)
		{
			w = 1920;
			h = 1080;
		}
		return (Math.Clamp(w, 1280, 7680), Math.Clamp(h, 720, 4320));
	}

	// A season can show the user's own desktop picture instead of the drawn scene. First match wins:
	// <launcher data>\seasonal\custom\<art>.* (chosen in PC Settings) and <launcher folder>\seasonal\<art>.* (a
	// picture dropped in by hand). The picture itself is never set as the wallpaper: it is cropped to the screen into
	// SeasonalDir, so the restore logic handles it exactly like the drawn wallpaper.
	public static readonly string CustomDir = Path.Combine(SeasonalDir, "custom");

	private static readonly string[] PictureExtensions = new string[4] { ".jpg", ".jpeg", ".png", ".bmp" };

	private static IEnumerable<string> PictureDirs()
	{
		yield return CustomDir;
		yield return Path.Combine(AppContext.BaseDirectory, "seasonal");
	}

	public static string? CustomPictureFor(string art)
	{
		foreach (string dir in PictureDirs())
		{
			foreach (string ext in PictureExtensions)
			{
				string path = Path.Combine(dir, art + ext);
				try
				{
					if (File.Exists(path) && new FileInfo(path).Length > 1024)
					{
						return path;
					}
				}
				catch
				{
				}
			}
		}
		return null;
	}

	// Copies the chosen picture in as the season's picture (replacing an earlier one in any format).
	public static void SetCustomPicture(string art, string source)
	{
		string ext = Path.GetExtension(source).ToLowerInvariant();
		if (Array.IndexOf(PictureExtensions, ext) < 0)
		{
			throw new InvalidOperationException("Choose a JPG, PNG or BMP picture.");
		}
		Directory.CreateDirectory(CustomDir);
		string target = Path.Combine(CustomDir, art + ext);
		string tmp = target + ".tmp-" + Environment.ProcessId;
		File.Copy(source, tmp, overwrite: true);
		foreach (string other in PictureExtensions)
		{
			string old = Path.Combine(CustomDir, art + other);
			if (!string.Equals(old, target, StringComparison.OrdinalIgnoreCase) && File.Exists(old))
			{
				File.Delete(old);
			}
		}
		File.Move(tmp, target, overwrite: true);
	}

	// Back to the drawn scene: removes the season's picture from both places.
	public static void ClearCustomPicture(string art)
	{
		foreach (string dir in PictureDirs())
		{
			foreach (string ext in PictureExtensions)
			{
				string path = Path.Combine(dir, art + ext);
				if (File.Exists(path))
				{
					File.Delete(path);
				}
			}
		}
	}

	// File name of the screen-sized copy of a custom picture. The stamp changes when the picture changes, so a new picture
	// is never mistaken for the old copy.
	public static string CustomWallpaperFileName(string art, string source, int width, int height)
	{
		uint hash = 2166136261u;
		try
		{
			FileInfo fi = new FileInfo(source);
			string key = fi.FullName.ToLowerInvariant() + "|" + fi.Length + "|" + fi.LastWriteTimeUtc.Ticks;
			foreach (char c in key)
			{
				hash = unchecked((hash ^ c) * 16777619u);
			}
		}
		catch
		{
		}
		return $"{art}-picture-{hash:x8}-{width}x{height}.png";
	}

	// Crops the picture to fill width x height (centred) and writes it as PNG. Must run on an STA thread.
	public static void RenderPicturePng(string source, int width, int height, string path)
	{
		BitmapImage img = new BitmapImage();
		img.BeginInit();
		img.CacheOption = BitmapCacheOption.OnLoad;   // read fully now, so the source file is not kept open
		img.CreateOptions = BitmapCreateOptions.PreservePixelFormat;
		img.UriSource = new Uri(source, UriKind.Absolute);
		img.EndInit();
		img.Freeze();
		DrawingVisual dv = new DrawingVisual();
		RenderOptions.SetBitmapScalingMode(dv, BitmapScalingMode.HighQuality);
		using (DrawingContext dc = dv.RenderOpen())
		{
			ImageBrush fill = new ImageBrush(img)
			{
				Stretch = Stretch.UniformToFill,
				AlignmentX = AlignmentX.Center,
				AlignmentY = AlignmentY.Center
			};
			dc.DrawRectangle(fill, null, new Rect(0.0, 0.0, width, height));
		}
		RenderTargetBitmap rtb = new RenderTargetBitmap(width, height, 96.0, 96.0, PixelFormats.Pbgra32);
		rtb.Render(dv);
		rtb.Freeze();
		WritePngAtomic(rtb, path);
	}

	// File name of the season's wallpaper as it would be prepared now: the user's picture when the season has one,
	// otherwise the drawn scene, both at the size of the largest screen.
	public static string ExpectedWallpaperName(string art)
	{
		(int w, int h) = TargetWallpaperSize();
		string? picture = CustomPictureFor(art);
		return picture != null ? CustomWallpaperFileName(art, picture, w, h) : WallpaperFileName(art, w, h);
	}

	public static string WallpaperFileName(string art, int width, int height)
	{
		return $"{art}-v{WallpaperVersion}-{width}x{height}.png";
	}

	public static bool IsSeasonalFile(string? path)
	{
		if (string.IsNullOrWhiteSpace(path))
		{
			return false;
		}
		try
		{
			string full = Path.GetFullPath(path);
			string dir = Path.GetFullPath(SeasonalDir).TrimEnd('\\') + "\\";
			return full.StartsWith(dir, StringComparison.OrdinalIgnoreCase);
		}
		catch
		{
			return false;
		}
	}

	// Renders the wallpaper for art ("halloween" / "christmas") to path. Must run on an STA thread. The file is written
	// to a temporary name first and then moved into place, so a crash never leaves a half-written wallpaper.
	public static void RenderWallpaperPng(string art, int width, int height, string path)
	{
		WritePngAtomic(RenderWallpaper(art, width, height), path);
	}

	private static void WritePngAtomic(BitmapSource bmp, string path)
	{
		string dir = Path.GetDirectoryName(path) ?? SeasonalDir;
		Directory.CreateDirectory(dir);
		string tmp = path + ".tmp-" + Environment.ProcessId;
		try
		{
			using (FileStream fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 65536, FileOptions.WriteThrough))
			{
				PngBitmapEncoder enc = new PngBitmapEncoder();
				enc.Frames.Add(BitmapFrame.Create(bmp));
				enc.Save(fs);
				fs.Flush(flushToDisk: true);
			}
			File.Move(tmp, path, overwrite: true);
		}
		finally
		{
			try
			{
				if (File.Exists(tmp))
				{
					File.Delete(tmp);
				}
			}
			catch
			{
			}
		}
	}

	private static readonly Dictionary<string, Brush> Previews = new Dictionary<string, Brush>(StringComparer.Ordinal);

	// A small render of the season's desktop scene for the preview tiles and chips, drawn once per process. UI thread.
	public static Brush PreviewBrush(string art)
	{
		string? picture = CustomPictureFor(art);
		string key = picture == null ? art : CustomWallpaperFileName(art, picture, 480, 270);
		lock (Previews)
		{
			if (Previews.TryGetValue(key, out Brush? hit))
			{
				return hit;
			}
		}
		Brush brush;
		try
		{
			ImageBrush ib = new ImageBrush(picture != null ? LoadPreviewPicture(picture) : RenderWallpaper(art, 480, 270))
			{
				Stretch = Stretch.UniformToFill,
				// the lower part holds the ground and the caption covers it, so keep the sky and the decorations
				AlignmentY = AlignmentY.Top
			};
			RenderOptions.SetBitmapScalingMode(ib, BitmapScalingMode.HighQuality);
			ib.Freeze();
			brush = ib;
		}
		catch (Exception ex)
		{
			Logger.Log("Seasonal: preview render failed: " + ex.Message);
			brush = Solid(art == "christmas" ? Hex("#FF0B3D2C") : Hex("#FF22103A"));
		}
		lock (Previews)
		{
			Previews[key] = brush;
		}
		return brush;
	}

	private static BitmapSource LoadPreviewPicture(string path)
	{
		BitmapImage img = new BitmapImage();
		img.BeginInit();
		img.CacheOption = BitmapCacheOption.OnLoad;
		img.DecodePixelWidth = 480;
		img.UriSource = new Uri(path, UriKind.Absolute);
		img.EndInit();
		img.Freeze();
		return img;
	}

	public static BitmapSource RenderWallpaper(string art, int width, int height)
	{
		DrawingVisual dv = new DrawingVisual();
		using (DrawingContext dc = dv.RenderOpen())
		{
			dc.DrawDrawing(WallpaperDrawing(art, width, height));
		}
		RenderTargetBitmap rtb = new RenderTargetBitmap(width, height, 96.0, 96.0, PixelFormats.Pbgra32);
		rtb.Render(dv);
		rtb.Freeze();
		FormatConvertedBitmap opaque = new FormatConvertedBitmap(rtb, PixelFormats.Bgr24, null, 0.0);
		opaque.Freeze();
		return opaque;
	}

	public static Drawing WallpaperDrawing(string art, double w, double h)
	{
		DrawingGroup group = new DrawingGroup();
		using (DrawingContext dc = group.Open())
		{
			dc.PushClip(new RectangleGeometry(new Rect(0, 0, w, h)));
			if (art == "christmas")
			{
				DrawChristmasWallpaper(dc, w, h);
			}
			else
			{
				DrawHalloweenWallpaper(dc, w, h);
			}
			dc.Pop();
		}
		group.Freeze();
		return group;
	}

	// ----- Halloween "Moonlit hill"

	private static void DrawHalloweenWallpaper(DrawingContext dc, double W, double H)
	{
		double u = H / 1080.0;
		Random rnd = new Random(1031);
		dc.DrawRectangle(Linear(new Point(0, 0), new Point(0, 1), (0.0, Hex("#FF140824")), (0.55, Hex("#FF2E1250")), (0.80, Hex("#FF4A1F66")), (1.0, Hex("#FF4A1F66"))), null, new Rect(0, 0, W, H));

		Point moon = new Point(0.72 * W, 0.30 * H);
		double R = 0.13 * H;
		// stars, kept out of the moon's halo
		for (int i = 0; i < 120; i++)
		{
			double x = rnd.NextDouble() * W;
			double y = Math.Pow(rnd.NextDouble(), 1.3) * 0.64 * H;
			double r = (0.5 + rnd.NextDouble() * 1.1) * u;
			byte a = (byte)(70 + rnd.Next(150));
			if ((x - moon.X) * (x - moon.X) + (y - moon.Y) * (y - moon.Y) < (1.9 * R) * (1.9 * R))
			{
				continue;
			}
			if (i % 11 == 0)
			{
				DrawGlow(dc, x, y, r * 6.0, Color.FromArgb((byte)(a / 3), 255, 236, 210));
				r *= 1.35;
			}
			dc.DrawEllipse(Solid(Color.FromArgb(a, 255, 246, 236)), null, new Point(x, y), r, r);
		}

		// moon halo, moon and a few soft craters
		dc.DrawEllipse(Radial(new Point(0.5, 0.5), new Point(0.5, 0.5), 0.5, (0.0, Hex("#70F5A04A")), (0.33, Hex("#48F5A04A")), (0.5, Hex("#1EF5A04A")), (0.75, Hex("#08F5A04A")), (1.0, Hex("#00F5A04A"))), null, moon, 3.0 * R, 3.0 * R);
		dc.DrawEllipse(Radial(new Point(0.40, 0.36), new Point(0.45, 0.42), 0.62, (0.0, Hex("#FFFFF2D6")), (0.55, Hex("#FFFFD995")), (1.0, Hex("#FFF2A453"))), null, moon, R, R);
		Brush crater = Solid(Hex("#1CB0602A"));
		foreach ((double cx, double cy, double cr) in new (double, double, double)[6] { (-0.30, -0.18, 0.16), (0.24, 0.22, 0.21), (0.06, -0.42, 0.10), (-0.12, 0.44, 0.12), (0.44, -0.20, 0.08), (-0.48, 0.18, 0.07) })
		{
			dc.DrawEllipse(crater, null, new Point(moon.X + cx * R, moon.Y + cy * R), cr * R, cr * R);
		}
		// two soft lavender cloud wisps, each a few overlapping puffs, trailing off the lower edge of the moon: lighter
		// than the sky, so they read as moonlit cloud rather than smudges
		foreach ((double ox, double oy, double sx, double sy, byte a) in new (double, double, double, double, byte)[9]
		{
			(0.55, 0.62, 0.62, 0.11, 120), (1.05, 0.58, 0.48, 0.085, 105), (0.20, 0.70, 0.40, 0.075, 90), (1.42, 0.66, 0.32, 0.06, 70), (0.82, 0.70, 0.30, 0.05, 80),
			(-0.95, 0.30, 0.46, 0.08, 95), (-1.32, 0.36, 0.34, 0.06, 75), (-0.62, 0.38, 0.28, 0.05, 70), (-1.62, 0.31, 0.22, 0.045, 55)
		})
		{
			Color cc = Color.FromArgb(a, 106, 74, 138);
			dc.DrawEllipse(Radial(new Point(0.5, 0.5), new Point(0.5, 0.5), 0.5, (0.0, cc), (0.55, Color.FromArgb((byte)(a * 0.5), cc.R, cc.G, cc.B)), (1.0, Color.FromArgb(0, cc.R, cc.G, cc.B))), null, new Point(moon.X + ox * R, moon.Y + oy * R), sx * R * 1.6, sy * R * 1.6);
		}

		// far hills, a band of mist, graves, then the near ground
		dc.DrawGeometry(Solid(Hex("#FF1E0B30")), null, SmoothRidge(W, H, 21, i => 0.78 * H + 0.03 * H * Math.Sin(0.9 * i) + 0.02 * H * Math.Sin(2.3 * i)));
		dc.DrawRectangle(Linear(new Point(0, 0), new Point(0, 1), (0.0, Hex("#00B9A2D9")), (0.55, Hex("#1CB9A2D9")), (1.0, Hex("#00B9A2D9"))), null, new Rect(0, 0.76 * H, W, 0.14 * H));
		Brush grave = Solid(Hex("#FF170A26"));
		foreach ((double gx, double gw, double gh, double gr, bool cross) in new (double, double, double, double, bool)[5]
		{
			(0.545, 40, 62, -3, false), (0.590, 26, 84, 2, true), (0.634, 48, 52, 5, false), (0.692, 32, 46, -6, false), (0.737, 22, 66, 3, true)
		})
		{
			DrawGrave(dc, gx * W, 0.872 * H, gw * u, gh * u, gr, cross, grave);
		}
		Brush ground = Solid(Hex("#FF0E0618"));
		dc.DrawGeometry(ground, null, SmoothRidge(W, H, 25, i => 0.865 * H + 0.015 * H * Math.Sin(1.7 * i) + 0.010 * H * Math.Sin(3.1 * i)));
		DrawGrassTufts(dc, W, H, u, rnd, ground, i => 0.865 * H + 0.015 * H * Math.Sin(1.7 * i) + 0.010 * H * Math.Sin(3.1 * i), 25);

		DrawHalloweenTree(dc, W, H, u, ground, rnd);

		// three carved pumpkins glowing on the ground
		PumpkinColors pc = new PumpkinColors(Hex("#FFF79A3E"), Hex("#FFE0620B"), Hex("#FF8A3605"), Hex("#FF9A3F06"), Hex("#FF4E6B1E"), Hex("#FFFFF6C8"), Hex("#FFFFB547"));
		foreach ((double px, double pw, double prot) in new (double, double, double)[3] { (0.30, 0.14, -4.0), (0.405, 0.10, 6.0), (0.855, 0.08, -3.0) })
		{
			double wpx = pw * H;
			double cy = 0.885 * H - 0.30 * wpx;
			dc.DrawEllipse(Radial(new Point(0.5, 0.5), new Point(0.5, 0.5), 0.5, (0.0, Hex("#38FF9A3C")), (1.0, Hex("#00FF9A3C"))), null, new Point(px * W, cy + 0.38 * wpx), 1.1 * wpx, 0.22 * wpx);
			DrawGlow(dc, px * W, cy, 1.15 * wpx, Hex("#40FFB347"));
			DrawPumpkin(dc, px * W, cy, wpx, prot, pc, 1.0, face: true, glow: null);
		}

		// bats, two of them crossing the moon
		Brush bat = Solid(Hex("#FF0A0412"));
		foreach ((double bx, double by, double bw, double br) in new (double, double, double, double)[7]
		{
			(0.665, 0.255, 0.120, -8), (0.700, 0.355, 0.070, 10), (0.815, 0.195, 0.090, -14), (0.480, 0.140, 0.060, 6), (0.560, 0.300, 0.050, -4), (0.905, 0.405, 0.060, 12), (0.365, 0.235, 0.080, -10)
		})
		{
			DrawBat(dc, bx * W, by * H, bw * H, br, bat, null);
		}

		DrawVignette(dc, W, H, 0x58);
	}

	private static void DrawGrave(DrawingContext dc, double x, double groundY, double w, double h, double rot, bool cross, Brush fill)
	{
		dc.PushTransform(new RotateTransform(rot, x, groundY));
		if (cross)
		{
			double bar = w * 0.36;
			dc.DrawRectangle(fill, null, new Rect(x - bar / 2, groundY - h, bar, h + 6));
			dc.DrawRectangle(fill, null, new Rect(x - w / 2, groundY - h * 0.74, w, bar));
		}
		else
		{
			StreamGeometry g = new StreamGeometry();
			using (StreamGeometryContext c = g.Open())
			{
				c.BeginFigure(new Point(x - w / 2, groundY + 6), isFilled: true, isClosed: true);
				c.LineTo(new Point(x - w / 2, groundY - h + w / 2), true, false);
				c.ArcTo(new Point(x + w / 2, groundY - h + w / 2), new Size(w / 2, w / 2), 0, false, SweepDirection.Clockwise, true, false);
				c.LineTo(new Point(x + w / 2, groundY + 6), true, false);
			}
			g.Freeze();
			dc.DrawGeometry(fill, null, g);
		}
		dc.Pop();
	}

	private static void DrawGrassTufts(DrawingContext dc, double W, double H, double u, Random rnd, Brush fill, Func<double, double> ridge, int points)
	{
		StreamGeometry g = new StreamGeometry();
		using (StreamGeometryContext c = g.Open())
		{
			for (double x = 0; x < W; x += (10 + rnd.NextDouble() * 22) * u)
			{
				double i = (x + 0.02 * W) / (1.04 * W) * (points - 1);
				double y = ridge(i) + 3 * u;
				int blades = 2 + rnd.Next(3);
				for (int b = 0; b < blades; b++)
				{
					double bx = x + (rnd.NextDouble() - 0.5) * 8 * u;
					double bh = (5 + rnd.NextDouble() * 12) * u;
					double lean = (rnd.NextDouble() - 0.5) * 7 * u;
					c.BeginFigure(new Point(bx - 1.6 * u, y + 2 * u), true, true);
					c.QuadraticBezierTo(new Point(bx + lean * 0.3, y - bh * 0.6), new Point(bx + lean, y - bh), true, false);
					c.QuadraticBezierTo(new Point(bx + lean * 0.3 + 1.2 * u, y - bh * 0.5), new Point(bx + 1.6 * u, y + 2 * u), true, false);
				}
			}
		}
		g.Freeze();
		dc.DrawGeometry(fill, null, g);
	}

	private static void DrawHalloweenTree(DrawingContext dc, double W, double H, double u, Brush fill, Random rnd)
	{
		Point P(double fx, double fy) => new Point(fx * W, fy * H);
		// trunk with flared roots
		DrawLimb(dc, fill, new (Point, double)[10]
		{
			(P(0.110, 0.905), 84 * u), (P(0.117, 0.83), 56 * u), (P(0.121, 0.72), 45 * u), (P(0.118, 0.62), 38 * u), (P(0.126, 0.52), 31 * u),
			(P(0.124, 0.42), 25 * u), (P(0.133, 0.33), 18 * u), (P(0.142, 0.25), 11 * u), (P(0.150, 0.19), 5 * u), (P(0.156, 0.15), 1.5 * u)
		});
		DrawLimb(dc, fill, new (Point, double)[3] { (P(0.100, 0.885), 28 * u), (P(0.077, 0.898), 12 * u), (P(0.050, 0.906), 3 * u) });
		DrawLimb(dc, fill, new (Point, double)[3] { (P(0.122, 0.888), 26 * u), (P(0.147, 0.899), 11 * u), (P(0.175, 0.907), 3 * u) });
		// main limbs
		List<(Point, double)[]> limbs = new List<(Point, double)[]>
		{
			new (Point, double)[6] { (P(0.124, 0.565), 26 * u), (P(0.160, 0.525), 18 * u), (P(0.205, 0.478), 12 * u), (P(0.250, 0.452), 8 * u), (P(0.290, 0.438), 4.5 * u), (P(0.325, 0.410), 1.5 * u) },
			new (Point, double)[5] { (P(0.124, 0.43), 20 * u), (P(0.098, 0.37), 13 * u), (P(0.070, 0.30), 8 * u), (P(0.046, 0.245), 4.5 * u), (P(0.030, 0.205), 1.5 * u) },
			new (Point, double)[5] { (P(0.131, 0.335), 15 * u), (P(0.165, 0.275), 10 * u), (P(0.195, 0.215), 6 * u), (P(0.222, 0.165), 3.5 * u), (P(0.240, 0.135), 1.2 * u) },
			new (Point, double)[5] { (P(0.119, 0.70), 18 * u), (P(0.088, 0.645), 11 * u), (P(0.062, 0.600), 6 * u), (P(0.040, 0.575), 2.5 * u), (P(0.024, 0.565), 1.0 * u) },
			new (Point, double)[3] { (P(0.121, 0.78), 11 * u), (P(0.150, 0.745), 6 * u), (P(0.172, 0.728), 1.6 * u) }
		};
		foreach ((Point, double)[] limb in limbs)
		{
			DrawLimb(dc, fill, limb);
		}
		// gnarled twigs off every limb joint, and a fork at every limb tip
		foreach ((Point, double)[] limb in limbs)
		{
			for (int i = 1; i < limb.Length - 1; i++)
			{
				(Point p, double wNode) = limb[i];
				Point prev = limb[i - 1].Item1;
				Point next = limb[i + 1].Item1;
				double baseAngle = Math.Atan2(next.Y - prev.Y, next.X - prev.X);
				int twigs = 1 + rnd.Next(2);
				for (int t = 0; t < twigs; t++)
				{
					double side = ((i + t) % 2 == 0) ? 1.0 : -1.0;
					double angle = baseAngle + side * (0.45 + rnd.NextDouble() * 0.45) - 0.22;
					double len = (34 + rnd.NextDouble() * 56) * u * Math.Clamp(wNode / (10 * u), 0.6, 1.5);
					Twig(dc, fill, p, angle, len, Math.Max(1.6 * u, wNode * 0.5), rnd, depth: 3);
				}
			}
			Point tip = limb[limb.Length - 1].Item1;
			Point before = limb[limb.Length - 2].Item1;
			double tipAngle = Math.Atan2(tip.Y - before.Y, tip.X - before.X);
			Twig(dc, fill, before, tipAngle - 0.5, 40 * u, 2.4 * u, rnd, depth: 2);
		}

		// a web stretched across the fork between the trunk and the two right-hand limbs. Its open right-hand side is
		// one bridge thread from the upper limb down to the lower limb, so every outer point hangs from wood or silk.
		Point hub = P(0.176, 0.40);
		Point bridgeTop = P(0.195, 0.213);
		Point bridgeBottom = P(0.222, 0.462);
		Point OnBridge(double t) => new Point(bridgeTop.X + (bridgeBottom.X - bridgeTop.X) * t, bridgeTop.Y + (bridgeBottom.Y - bridgeTop.Y) * t);
		List<Point> anchors = new List<Point>
		{
			P(0.132, 0.36), P(0.131, 0.41), P(0.131, 0.46), P(0.133, 0.51), P(0.160, 0.517), P(0.190, 0.486), bridgeBottom,
			OnBridge(0.70), OnBridge(0.42), bridgeTop, P(0.172, 0.262), P(0.150, 0.300)
		};
		List<(double Angle, double Length)> spokes = anchors
			.Select(a => (Math.Atan2(a.Y - hub.Y, a.X - hub.X), Math.Sqrt((a.X - hub.X) * (a.X - hub.X) + (a.Y - hub.Y) * (a.Y - hub.Y))))
			.OrderBy(sp => sp.Item1)
			.ToList();
		Pen silk = MakePen(Solid(Hex("#4CFFFFFF")), 1.2 * u);
		dc.DrawGeometry(null, silk, FramedWeb(hub, spokes, 6, closed: true));
		dc.DrawLine(silk, bridgeTop, bridgeBottom);
		// a spider dangling from the long limb
		Point hang = P(0.290, 0.440);
		double drop = 0.09 * H;
		dc.DrawLine(MakePen(Solid(Hex("#66FFFFFF")), 1.1 * u), hang, new Point(hang.X, hang.Y + drop));
		DrawSpider(dc, hang.X, hang.Y + drop + 7 * u, 1.8 * u, fill);
	}

	private static void Twig(DrawingContext dc, Brush fill, Point from, double angle, double len, double width, Random rnd, int depth)
	{
		double a1 = angle + (rnd.NextDouble() - 0.5) * 0.5;
		Point p1 = new Point(from.X + Math.Cos(a1) * len * 0.4, from.Y + Math.Sin(a1) * len * 0.4);
		double a2 = a1 + (rnd.NextDouble() - 0.5) * 0.7;
		Point p2 = new Point(p1.X + Math.Cos(a2) * len * 0.35, p1.Y + Math.Sin(a2) * len * 0.35);
		double a3 = a2 + (rnd.NextDouble() - 0.5) * 0.7;
		Point p3 = new Point(p2.X + Math.Cos(a3) * len * 0.25, p2.Y + Math.Sin(a3) * len * 0.25);
		DrawLimb(dc, fill, new (Point, double)[4] { (from, width), (p1, width * 0.66), (p2, width * 0.4), (p3, Math.Max(0.7, width * 0.16)) });
		if (depth > 0)
		{
			double side = rnd.NextDouble() < 0.5 ? -1.0 : 1.0;
			Twig(dc, fill, p1, a1 + side * (0.55 + rnd.NextDouble() * 0.35), len * 0.6, width * 0.55, rnd, depth - 1);
			if (depth > 1 && rnd.NextDouble() < 0.6)
			{
				Twig(dc, fill, p2, a2 - side * (0.5 + rnd.NextDouble() * 0.4), len * 0.45, width * 0.4, rnd, depth - 2);
			}
		}
	}

	// A tapered limb: one smooth outline through the left and right edges of the polyline, with a rounded tip.
	private static void DrawLimb(DrawingContext dc, Brush fill, (Point P, double W)[] nodes)
	{
		int n = nodes.Length;
		Point[] left = new Point[n];
		Point[] right = new Point[n];
		for (int i = 0; i < n; i++)
		{
			Point a = nodes[Math.Max(0, i - 1)].P;
			Point b = nodes[Math.Min(n - 1, i + 1)].P;
			double dx = b.X - a.X;
			double dy = b.Y - a.Y;
			double len = Math.Max(0.001, Math.Sqrt(dx * dx + dy * dy));
			double nx = -dy / len;
			double ny = dx / len;
			double hw = nodes[i].W * 0.5;
			left[i] = new Point(nodes[i].P.X + nx * hw, nodes[i].P.Y + ny * hw);
			right[i] = new Point(nodes[i].P.X - nx * hw, nodes[i].P.Y - ny * hw);
		}
		StreamGeometry g = new StreamGeometry();
		using (StreamGeometryContext c = g.Open())
		{
			c.BeginFigure(left[0], isFilled: true, isClosed: true);
			SmoothThrough(c, left);
			c.LineTo(right[n - 1], true, true);
			Point[] back = Enumerable.Reverse(right).ToArray();
			SmoothThrough(c, back);
		}
		g.Freeze();
		dc.DrawGeometry(fill, null, g);
		dc.DrawEllipse(fill, null, nodes[0].P, nodes[0].W * 0.5, nodes[0].W * 0.5);
	}

	// Quadratic curve through the midpoints of pts (the figure is already at pts[0]).
	private static void SmoothThrough(StreamGeometryContext c, Point[] pts)
	{
		if (pts.Length < 3)
		{
			for (int i = 1; i < pts.Length; i++)
			{
				c.LineTo(pts[i], true, true);
			}
			return;
		}
		for (int i = 1; i < pts.Length - 1; i++)
		{
			Point mid = new Point((pts[i].X + pts[i + 1].X) * 0.5, (pts[i].Y + pts[i + 1].Y) * 0.5);
			c.QuadraticBezierTo(pts[i], (i == pts.Length - 2) ? pts[i + 1] : mid, true, true);
		}
	}

	private static Geometry SmoothRidge(double W, double H, int points, Func<double, double> y)
	{
		Point[] pts = new Point[points];
		for (int i = 0; i < points; i++)
		{
			pts[i] = new Point(-0.02 * W + i * 1.04 * W / (points - 1), y(i));
		}
		StreamGeometry g = new StreamGeometry();
		using (StreamGeometryContext c = g.Open())
		{
			c.BeginFigure(new Point(-0.02 * W, H + 2), isFilled: true, isClosed: true);
			c.LineTo(pts[0], true, true);
			SmoothThrough(c, pts);
			c.LineTo(new Point(1.02 * W, H + 2), true, true);
		}
		g.Freeze();
		return g;
	}

	private static void DrawVignette(DrawingContext dc, double W, double H, byte edgeAlpha)
	{
		dc.DrawRectangle(Radial(new Point(0.5, 0.45), new Point(0.5, 0.45), 0.78, (0.0, Color.FromArgb(0, 0, 0, 0)), (0.62, Color.FromArgb(0, 0, 0, 0)), (1.0, Color.FromArgb(edgeAlpha, 0, 0, 0))), null, new Rect(0, 0, W, H));
	}

	// ----- Christmas "Garland"

	private static void DrawChristmasWallpaper(DrawingContext dc, double W, double H)
	{
		double u = H / 1080.0;
		Random rnd = new Random(1225);
		RadialGradientBrush bg = new RadialGradientBrush
		{
			MappingMode = BrushMappingMode.Absolute,
			Center = new Point(0.5 * W, 0.45 * H),
			GradientOrigin = new Point(0.5 * W, 0.45 * H),
			RadiusX = 0.9 * W,
			RadiusY = 0.9 * W
		};
		bg.GradientStops.Add(new GradientStop(Hex("#FF1C6B47"), 0.0));
		bg.GradientStops.Add(new GradientStop(Hex("#FF0F4A31"), 0.45));
		bg.GradientStops.Add(new GradientStop(Hex("#FF072A1D"), 1.0));
		bg.Freeze();
		dc.DrawRectangle(bg, null, new Rect(0, 0, W, H));

		Point g0 = new Point(-0.05 * W, 0.04 * H);
		Point g1 = new Point(0.5 * W, 0.20 * H);
		Point g2 = new Point(1.05 * W, 0.04 * H);
		(double T, double Len, double Rad, string Hex)[] baubles = new (double, double, double, string)[7]
		{
			(0.10, 0.10, 0.045, "#FFC8102E"), (0.215, 0.20, 0.055, "#FFE3A21A"), (0.335, 0.13, 0.040, "#FFDDE6EE"), (0.50, 0.22, 0.060, "#FFC8102E"),
			(0.645, 0.12, 0.045, "#FFE3A21A"), (0.775, 0.19, 0.050, "#FFDDE6EE"), (0.90, 0.09, 0.042, "#FFC8102E")
		};
		List<(Point C, double R)> baubleDiscs = baubles.Select(b =>
		{
			Point top = Quad(g0, g1, g2, b.T);
			double r = b.Rad * H;
			return (new Point(top.X, top.Y + b.Len * H + 1.32 * r), r);
		}).ToList();

		// soft out-of-focus lights: a few warm discs near the garland, where the light comes from, each a bright core
		// fading out (no ring), kept clear of the baubles
		Color[] bokeh = new Color[2] { Hex("#FFFFE6A6"), Hex("#FFFFF8E8") };
		int placedBokeh = 0;
		for (int attempt = 0; attempt < 400 && placedBokeh < 11; attempt++)
		{
			double t = rnd.NextDouble();
			Point on = Quad(g0, g1, g2, t);
			double x = on.X + (rnd.NextDouble() - 0.5) * 0.06 * W;
			double y = on.Y + (0.03 + rnd.NextDouble() * 0.20) * H;
			double r = (0.016 + rnd.NextDouble() * 0.026) * H;
			if (x < 0 || x > W || baubleDiscs.Any(b => Distance(b.C, new Point(x, y)) < b.R * 1.4 + r))
			{
				continue;
			}
			Color c = bokeh[placedBokeh % 2];
			byte a = (byte)(72 + rnd.Next(26));
			dc.DrawEllipse(Radial(new Point(0.5, 0.5), new Point(0.5, 0.5), 0.5, (0.0, Color.FromArgb(a, c.R, c.G, c.B)), (0.45, Color.FromArgb((byte)(a * 0.55), c.R, c.G, c.B)), (1.0, Color.FromArgb(0, c.R, c.G, c.B))), null, new Point(x, y), r, r);
			placedBokeh++;
		}

		// falling snow behind the decorations: fine dots for depth and a few crisp flakes
		for (int i = 0; i < 140; i++)
		{
			double x = rnd.NextDouble() * W;
			double y = (0.16 + rnd.NextDouble() * 0.72) * H;
			double r = (0.8 + rnd.NextDouble() * 1.9) * u;
			dc.DrawEllipse(Solid(Color.FromArgb((byte)(50 + rnd.Next(120)), 255, 255, 255)), null, new Point(x, y), r, r);
		}

		// snowy firs standing on the drifts
		(double X, double Ground, double Height)[] firs = new (double, double, double)[4]
		{
			(0.035 * W, 0.885 * H, 0.24 * H), (0.088 * W, 0.893 * H, 0.165 * H), (0.910 * W, 0.893 * H, 0.18 * H), (0.962 * W, 0.885 * H, 0.255 * H)
		};

		// crisp flakes, never on a fir, a bauble or another flake
		List<(Point C, double R)> flakes = new List<(Point, double)>();
		for (int attempt = 0; attempt < 600 && flakes.Count < 26; attempt++)
		{
			Point p = new Point(rnd.NextDouble() * W, (0.30 + rnd.NextDouble() * 0.52) * H);
			double r = (0.010 + rnd.NextDouble() * 0.024) * H;
			double rot = rnd.NextDouble() * 60.0;
			byte a = (byte)(64 + rnd.Next(96));
			bool onFir = firs.Any(f => Math.Abs(p.X - f.X) < 0.40 * f.Height + r && p.Y > f.Ground - f.Height - r);
			bool onBauble = baubleDiscs.Any(b => Distance(b.C, p) < b.R * 1.45 + r);
			bool crowded = flakes.Any(f => Distance(f.C, p) < 2.5 * Math.Max(f.R, r));
			if (onFir || onBauble || crowded)
			{
				continue;
			}
			flakes.Add((p, r));
			Snowflake(dc, p.X, p.Y, r, rot, Color.FromArgb(a, 255, 255, 255));
		}

		foreach ((double fx, double ground, double fh) in firs)
		{
			DrawFir(dc, fx, ground, fh, u);
		}
		Func<double, double> back = i => 0.866 * H + 0.012 * H * Math.Sin(1.3 * i) + 0.008 * H * Math.Sin(2.9 * i);
		dc.DrawGeometry(Solid(Hex("#FFC9D9E2")), null, SmoothRidge(W, H, 25, i => back(i) - 0.006 * H));
		dc.DrawGeometry(Linear(new Point(0, 0), new Point(0, 1), (0.0, Hex("#FFF7FBFC")), (1.0, Hex("#FFD5E2EA"))), null, SmoothRidge(W, H, 19, i => 0.888 * H + 0.010 * H * Math.Sin(1.1 * i + 0.8) + 0.007 * H * Math.Sin(2.6 * i)));

		// a sprig of holly resting on the snow
		HollyColors hc = new HollyColors(Hex("#FF2E8B57"), Hex("#FF145A32"), Hex("#FFC8102E"), Color.FromArgb(60, 255, 255, 255));
		DrawHolly(dc, 0.19 * W, 0.875 * H, 0.13 * H, -8.0, hc);

		// dark pine boughs across the foreground: the taskbar sits on deep green, never on white snow
		DrawBoughBand(dc, W, H, u, rnd, 0.942 * H);

		// the garland
		DrawGarland(dc, g0, g1, g2, u, rnd);

		// baubles hanging from the garland
		for (int i = 0; i < baubles.Length; i++)
		{
			DrawBauble(dc, baubleDiscs[i].C.X, baubleDiscs[i].C.Y, baubleDiscs[i].R, Hex(baubles[i].Hex), string_: baubles[i].Len * H - 0.12 * baubleDiscs[i].R);
		}

		// warm fairy lights woven through the garland, drawn last so they glow over the needles
		DrawFairyLights(dc, g0, g1, g2, u, rnd);

		DrawVignette(dc, W, H, 0x40);
	}

	private static double Distance(Point a, Point b)
	{
		double dx = a.X - b.X;
		double dy = a.Y - b.Y;
		return Math.Sqrt(dx * dx + dy * dy);
	}

	// Pine boughs along the bottom edge from top (y) down, echoing the garland at the top: two dense boughs over a
	// deep green base, with a few berry clusters. The taskbar sits on this dark band.
	private static void DrawBoughBand(DrawingContext dc, double W, double H, double u, Random rnd, double top)
	{
		dc.DrawRectangle(Linear(new Point(0, 0), new Point(0, 1), (0.0, Hex("#FF0B2E20")), (1.0, Hex("#FF051710"))), null, new Rect(0, top + 4 * u, W, H - top));
		(Color Color, double Length, double Width, double Spread)[] layers = new (Color, double, double, double)[4]
		{
			(Hex("#FF05201A"), 50.0, 2.8, 26.0),
			(Hex("#FF0A3324"), 42.0, 2.5, 20.0),
			(Hex("#FF15482F"), 32.0, 2.2, 14.0),
			(Hex("#FF2A6A48"), 20.0, 1.8, 10.0)
		};
		double[] fan = new double[9] { -150, -120, -60, -30, 0, 30, 60, 120, 150 };
		// a lower bough first, then the upper one sagging gently in front of it
		foreach ((Point b0, Point b1, Point b2) in new (Point, Point, Point)[2]
		{
			(new Point(-0.05 * W, top + 34 * u), new Point(0.5 * W, top + 18 * u), new Point(1.05 * W, top + 34 * u)),
			(new Point(-0.05 * W, top + 2 * u), new Point(0.5 * W, top + 16 * u), new Point(1.05 * W, top + 2 * u))
		})
		{
			foreach ((Color color, double length, double width, double spread) in layers)
			{
				StreamGeometry needles = new StreamGeometry();
				using (StreamGeometryContext c = needles.Open())
				{
					int steps = (int)Math.Ceiling(W / (6.0 * u));
					for (int i = 0; i <= steps; i++)
					{
						double t = i / (double)steps;
						Point p = Quad(b0, b1, b2, t);
						Point d = QuadTangent(b0, b1, b2, t);
						double ta = Math.Atan2(d.Y, d.X);
						double off = (rnd.NextDouble() - 0.5) * spread * u;
						p = new Point(p.X - Math.Sin(ta) * off, p.Y + Math.Cos(ta) * off);
						foreach (double f in fan)
						{
							double a = ta + (f + (rnd.NextDouble() - 0.5) * 12.0) * Math.PI / 180.0;
							double l = length * u * (0.75 + rnd.NextDouble() * 0.5);
							c.BeginFigure(p, isFilled: false, isClosed: false);
							c.LineTo(new Point(p.X + Math.Cos(a) * l, p.Y + Math.Sin(a) * l), true, false);
						}
					}
				}
				needles.Freeze();
				dc.DrawGeometry(null, MakePen(Solid(color), width * u), needles);
			}
		}
		Brush berry = Radial(new Point(0.35, 0.3), new Point(0.45, 0.45), 0.7, (0.0, Hex("#FFFF6A7A")), (0.55, Hex("#FFC8102E")), (1.0, Hex("#FF6A0614")));
		Brush glint = Solid(Hex("#B3FFFFFF"));
		foreach (double fx in new double[7] { 0.05, 0.21, 0.36, 0.52, 0.67, 0.81, 0.95 })
		{
			Point p = Quad(new Point(-0.05 * W, top + 2 * u), new Point(0.5 * W, top + 16 * u), new Point(1.05 * W, top + 2 * u), (fx + 0.05) / 1.1);
			foreach ((double bx, double by) in new (double, double)[3] { (-6.0, 1.5), (5.5, 0.5), (0.0, 8.5) })
			{
				Point bp = new Point(p.X + bx * u, p.Y + (by - 4.0) * u);
				dc.DrawEllipse(berry, null, bp, 5.6 * u, 5.6 * u);
				dc.DrawEllipse(glint, null, new Point(bp.X - 1.8 * u, bp.Y - 2.0 * u), 1.4 * u, 1.4 * u);
			}
		}
	}

	private static void DrawGarland(DrawingContext dc, Point g0, Point g1, Point g2, double u, Random rnd)
	{
		(Color Color, double Length, double Width, double Spread)[] layers = new (Color, double, double, double)[4]
		{
			(Hex("#FF082E1E"), 46.0, 2.6, 22.0),
			(Hex("#FF0F4A30"), 40.0, 2.3, 18.0),
			(Hex("#FF1E6B45"), 32.0, 2.1, 14.0),
			(Hex("#FF3F8C5E"), 20.0, 1.8, 10.0)
		};
		double[] fan = new double[9] { -150, -120, -60, -30, 0, 30, 60, 120, 150 };
		foreach ((Color color, double length, double width, double spread) in layers)
		{
			StreamGeometry needles = new StreamGeometry();
			using (StreamGeometryContext c = needles.Open())
			{
				for (int i = 0; i <= 300; i++)
				{
					double t = i / 300.0;
					Point p = Quad(g0, g1, g2, t);
					Point d = QuadTangent(g0, g1, g2, t);
					double ta = Math.Atan2(d.Y, d.X);
					double off = (rnd.NextDouble() - 0.5) * spread * u;
					p = new Point(p.X - Math.Sin(ta) * off, p.Y + Math.Cos(ta) * off);
					foreach (double f in fan)
					{
						double a = ta + (f + (rnd.NextDouble() - 0.5) * 12.0) * Math.PI / 180.0;
						double l = length * u * (0.75 + rnd.NextDouble() * 0.5);
						c.BeginFigure(p, isFilled: false, isClosed: false);
						c.LineTo(new Point(p.X + Math.Cos(a) * l, p.Y + Math.Sin(a) * l), true, false);
					}
				}
			}
			needles.Freeze();
			dc.DrawGeometry(null, MakePen(Solid(color), width * u), needles);
		}
		// clusters of red berries tucked into the garland
		Brush berry = Radial(new Point(0.35, 0.3), new Point(0.45, 0.45), 0.7, (0.0, Hex("#FFFF6A7A")), (0.55, Hex("#FFC8102E")), (1.0, Hex("#FF6A0614")));
		Brush glint = Solid(Hex("#B3FFFFFF"));
		foreach (double t in new double[7] { 0.04, 0.16, 0.28, 0.42, 0.58, 0.72, 0.86 })
		{
			Point p = Quad(g0, g1, g2, t + 0.035);
			foreach ((double bx, double by) in new (double, double)[3] { (-6.5, 2.0), (6.0, 1.0), (0.0, 9.5) })
			{
				Point bp = new Point(p.X + bx * u, p.Y + by * u + 6 * u);
				dc.DrawEllipse(berry, null, bp, 6.2 * u, 6.2 * u);
				dc.DrawEllipse(glint, null, new Point(bp.X - 2.0 * u, bp.Y - 2.2 * u), 1.6 * u, 1.6 * u);
			}
		}
	}

	private static void DrawFairyLights(DrawingContext dc, Point g0, Point g1, Point g2, double u, Random rnd)
	{
		Color[] bulbs = new Color[3] { Hex("#FFFFF1C9"), Hex("#FFFFD27A"), Hex("#FFFFB86B") };
		for (int i = 0; i < 38; i++)
		{
			double t = 0.012 + i * 0.976 / 37.0 + (rnd.NextDouble() - 0.5) * 0.008;
			Point p = Quad(g0, g1, g2, t);
			Point d = QuadTangent(g0, g1, g2, t);
			double ta = Math.Atan2(d.Y, d.X);
			double off = ((i % 2 == 0) ? 1.0 : -1.0) * (4 + rnd.NextDouble() * 8) * u;
			p = new Point(p.X - Math.Sin(ta) * off, p.Y + Math.Cos(ta) * off);
			Color c = bulbs[i % 3];
			DrawGlow(dc, p.X, p.Y, 24 * u, Color.FromArgb(96, c.R, c.G, c.B));
			dc.DrawEllipse(Radial(new Point(0.4, 0.35), new Point(0.5, 0.5), 0.6, (0.0, Colors.White), (0.55, c), (1.0, Mix(c, Color.FromRgb(200, 120, 40), 0.4))), null, p, 4.6 * u, 4.6 * u);
		}
	}

	private static void DrawFir(DrawingContext dc, double x, double groundY, double h, double u)
	{
		Brush needles = Linear(new Point(0, 0), new Point(1, 0), (0.0, Hex("#FF0F4A31")), (0.5, Hex("#FF0B3C27")), (1.0, Hex("#FF06291A")));
		Brush snow = Linear(new Point(0, 0), new Point(0, 1), (0.0, Hex("#FFFFFFFF")), (1.0, Hex("#FFD8E5EC")));
		dc.DrawRectangle(Solid(Hex("#FF2A1A10")), null, new Rect(x - 0.035 * h, groundY - 0.14 * h, 0.07 * h, 0.16 * h));
		for (int tier = 0; tier < 4; tier++)
		{
			double top = groundY - h + tier * 0.19 * h;
			double bottom = top + 0.40 * h;
			double half = (0.16 + tier * 0.075) * h;
			StreamGeometry g = new StreamGeometry();
			using (StreamGeometryContext c = g.Open())
			{
				c.BeginFigure(new Point(x, top), true, true);
				c.QuadraticBezierTo(new Point(x + half * 0.35, top + 0.25 * h), new Point(x + half, bottom), true, true);
				c.QuadraticBezierTo(new Point(x, bottom - 0.06 * h), new Point(x - half, bottom), true, true);
				c.QuadraticBezierTo(new Point(x - half * 0.35, top + 0.25 * h), new Point(x, top), true, true);
			}
			g.Freeze();
			dc.DrawGeometry(needles, null, g);
			// a thin layer of snow resting on the upper edges of each tier
			StreamGeometry cap = new StreamGeometry();
			using (StreamGeometryContext c = cap.Open())
			{
				double sb = top + 0.40 * h * 0.30;
				double sh = half * 0.42;
				c.BeginFigure(new Point(x, top), true, true);
				c.QuadraticBezierTo(new Point(x + sh * 0.35, top + 0.07 * h), new Point(x + sh, sb), true, true);
				c.QuadraticBezierTo(new Point(x + sh * 0.55, sb - 0.012 * h), new Point(x + sh * 0.25, sb + 0.012 * h), true, true);
				c.QuadraticBezierTo(new Point(x, sb - 0.02 * h), new Point(x - sh * 0.3, sb + 0.008 * h), true, true);
				c.QuadraticBezierTo(new Point(x - sh * 0.6, sb - 0.016 * h), new Point(x - sh, sb), true, true);
				c.QuadraticBezierTo(new Point(x - sh * 0.35, top + 0.07 * h), new Point(x, top), true, true);
			}
			cap.Freeze();
			dc.DrawGeometry(snow, null, cap);
		}
	}

	private static Point Quad(Point p0, Point p1, Point p2, double t)
	{
		double a = (1 - t) * (1 - t);
		double b = 2 * (1 - t) * t;
		double c = t * t;
		return new Point(a * p0.X + b * p1.X + c * p2.X, a * p0.Y + b * p1.Y + c * p2.Y);
	}

	private static Point QuadTangent(Point p0, Point p1, Point p2, double t)
	{
		return new Point(2 * (1 - t) * (p1.X - p0.X) + 2 * t * (p2.X - p1.X), 2 * (1 - t) * (p1.Y - p0.Y) + 2 * t * (p2.Y - p1.Y));
	}

	// ===================================================================== ambient tiles

	// One tile of an ambient layer. kind = "snow" or "bats"; layer 0 = far, 1 = near. Frozen, tiled, cached.
	public static DrawingBrush AmbientTile(string kind, int layer, Color accent, out Size tile)
	{
		DrawingGroup g = new DrawingGroup();
		if (kind == "bats")
		{
			tile = layer == 0 ? new Size(720, 420) : new Size(1040, 600);
		}
		else
		{
			tile = layer == 0 ? new Size(300, 300) : new Size(460, 460);
		}
		using (DrawingContext dc = g.Open())
		{
			dc.DrawRectangle(Brushes.Transparent, null, new Rect(tile));
			if (kind == "bats")
			{
				// Broad-winged bats with deep scallops, so they read as bats in motion rather than smudges; the accent
				// rim is only a faint edge light.
				if (layer == 0)
				{
					Brush fill = Solid(Color.FromArgb(179, 0, 0, 0));
					Pen rim = MakePen(Solid(Color.FromArgb(38, accent.R, accent.G, accent.B)), 0.8);
					dc.DrawGeometry(fill, rim, Placed(BatWideUnit, 120, 90, 24, -8));
					dc.DrawGeometry(fill, rim, Placed(BatWideUnit, 470, 260, 30, 6));
				}
				else
				{
					Brush fill = Solid(Color.FromArgb(222, 0, 0, 0));
					Pen rim = MakePen(Solid(Color.FromArgb(46, accent.R, accent.G, accent.B)), 1.0);
					dc.DrawGeometry(fill, rim, Placed(BatWideUnit, 260, 150, 66, -4));
					dc.DrawGeometry(fill, rim, Placed(BatWideUnit, 780, 400, 80, 10));
				}
			}
			else
			{
				Random rnd = new Random(layer == 0 ? 11 : 23);
				if (layer == 0)
				{
					Brush dot = Solid(Color.FromArgb(140, 255, 255, 255));
					for (int i = 0; i < 14; i++)
					{
						double r = 1.2 + rnd.NextDouble() * 1.0;
						dc.DrawEllipse(dot, null, new Point(6 + rnd.NextDouble() * (tile.Width - 12), 6 + rnd.NextDouble() * (tile.Height - 12)), r, r);
					}
				}
				else
				{
					for (int i = 0; i < 7; i++)
					{
						double r = 4.0 + rnd.NextDouble() * 3.0;
						Snowflake(dc, 12 + rnd.NextDouble() * (tile.Width - 24), 12 + rnd.NextDouble() * (tile.Height - 24), r, rnd.NextDouble() * 60.0, Color.FromArgb(230, 255, 255, 255));
					}
					Brush dot = Solid(Color.FromArgb(204, 255, 255, 255));
					for (int i = 0; i < 5; i++)
					{
						double r = 2.6 + rnd.NextDouble() * 0.6;
						dc.DrawEllipse(dot, null, new Point(8 + rnd.NextDouble() * (tile.Width - 16), 8 + rnd.NextDouble() * (tile.Height - 16)), r, r);
					}
				}
			}
		}
		g.Freeze();
		DrawingBrush brush = new DrawingBrush(g)
		{
			TileMode = TileMode.Tile,
			Viewbox = new Rect(tile),
			ViewboxUnits = BrushMappingMode.Absolute,
			Viewport = new Rect(tile),
			ViewportUnits = BrushMappingMode.Absolute,
			Stretch = Stretch.Fill
		};
		RenderOptions.SetCachingHint(brush, CachingHint.Cache);
		brush.Freeze();
		return brush;
	}

	// ===================================================================== small helpers

	internal static Color Hex(string hex)
	{
		return (Color)ColorConverter.ConvertFromString(hex);
	}

	private static byte Lerp(byte a, byte b, double t)
	{
		return (byte)Math.Clamp(a + (b - a) * t, 0.0, 255.0);
	}

	private static Color Mix(Color a, Color b, double t)
	{
		return Color.FromArgb(Lerp(a.A, b.A, t), Lerp(a.R, b.R, t), Lerp(a.G, b.G, t), Lerp(a.B, b.B, t));
	}

	private static Point Rotate(double x, double y, double deg)
	{
		double a = deg * Math.PI / 180.0;
		return new Point(x * Math.Cos(a) - y * Math.Sin(a), x * Math.Sin(a) + y * Math.Cos(a));
	}

	private static SolidColorBrush Solid(Color c)
	{
		SolidColorBrush b = new SolidColorBrush(c);
		b.Freeze();
		return b;
	}

	private static Pen MakePen(Brush b, double width)
	{
		Pen p = new Pen(b, width)
		{
			StartLineCap = PenLineCap.Round,
			EndLineCap = PenLineCap.Round,
			LineJoin = PenLineJoin.Round
		};
		p.Freeze();
		return p;
	}

	private static Brush Radial(Point origin, Point center, double radius, params (double Offset, Color Color)[] stops)
	{
		RadialGradientBrush b = new RadialGradientBrush
		{
			GradientOrigin = origin,
			Center = center,
			RadiusX = radius,
			RadiusY = radius
		};
		foreach ((double offset, Color color) in stops)
		{
			b.GradientStops.Add(new GradientStop(color, offset));
		}
		b.Freeze();
		return b;
	}

	private static Brush Linear(Point start, Point end, params (double Offset, Color Color)[] stops)
	{
		LinearGradientBrush b = new LinearGradientBrush
		{
			StartPoint = start,
			EndPoint = end
		};
		foreach ((double offset, Color color) in stops)
		{
			b.GradientStops.Add(new GradientStop(color, offset));
		}
		b.Freeze();
		return b;
	}

	private static Geometry Placed(Geometry unit, double cx, double cy, double scale, double rotDeg)
	{
		Matrix m = Matrix.Identity;
		m.Scale(scale, scale);
		m.Rotate(rotDeg);
		m.Translate(cx, cy);
		Geometry g = unit.Clone();
		g.Transform = new MatrixTransform(m);
		g.Freeze();
		return g;
	}

	private static Geometry Ellipse(double cx, double cy, double rx, double ry)
	{
		return Freeze(new EllipseGeometry(new Point(cx, cy), rx, ry));
	}

	private static Geometry Union(params Geometry[] parts)
	{
		GeometryGroup g = new GeometryGroup { FillRule = FillRule.Nonzero };
		foreach (Geometry p in parts)
		{
			g.Children.Add(p);
		}
		g.Freeze();
		return g;
	}

	private static Geometry Freeze(Geometry g)
	{
		g.Freeze();
		return g;
	}
}
