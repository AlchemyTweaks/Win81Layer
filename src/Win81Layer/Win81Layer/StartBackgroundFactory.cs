using System.Windows;
using System.Windows.Media;

namespace Win81Layer;

public static class StartBackgroundFactory
{
	public sealed record Preset(string Pattern, string Base, string Accent);

	public static readonly string[] Patterns = new string[14]
	{
		"none", "dots", "diagonal", "grid", "circles", "triangles", "weave", "waves", "chevron", "crosshatch",
		"rings", "confetti", "plus", "scales"
	};

	public static readonly Preset[] Presets = new Preset[20]
	{
		new Preset("waves", "#FF6A2C91", "#FFB388D6"),
		new Preset("dots", "#FF1F1F1F", "#FF5A5A5A"),
		new Preset("circles", "#FFA61E5A", "#FFE86AA0"),
		new Preset("diagonal", "#FF0B5394", "#FF5FA8E8"),
		new Preset("triangles", "#FF00695C", "#FF5FD6C4"),
		new Preset("chevron", "#FFB5651D", "#FFF0A868"),
		new Preset("scales", "#FF283593", "#FF7C88D6"),
		new Preset("confetti", "#FF2E2E2E", "#FFE81123"),
		new Preset("grid", "#FF37474F", "#FF8BA3AE"),
		new Preset("rings", "#FF880E4F", "#FFF06AAE"),
		new Preset("weave", "#FF4E342E", "#FFB08876"),
		new Preset("plus", "#FF006B8F", "#FF66C6E0"),
		new Preset("crosshatch", "#FF5A3E85", "#FFB39CE0"),
		new Preset("waves", "#FF9E3B2E", "#FFE8917F"),
		new Preset("dots", "#FF2E7D32", "#FF88D68C"),
		new Preset("chevron", "#FF7A6A00", "#FFD6C84A"),
		new Preset("circles", "#FF0091F7", "#FF9CD2FF"),
		new Preset("triangles", "#FFC81E5B", "#FFF07CA6"),
		new Preset("none", "#FF1F1F1F", "#FF1F1F1F"),
		new Preset("none", "#FFF2F2F2", "#FFF2F2F2")
	};

	public static Brush Build(Color baseColor, Color accent, string pattern)
	{
		//IL_00dc: Unknown result type (might be due to invalid IL or missing references)
		//IL_07af: Unknown result type (might be due to invalid IL or missing references)
		//IL_07fb: Unknown result type (might be due to invalid IL or missing references)
		//IL_0b10: Unknown result type (might be due to invalid IL or missing references)
		//IL_0a19: Unknown result type (might be due to invalid IL or missing references)
		//IL_0a45: Unknown result type (might be due to invalid IL or missing references)
		//IL_0a62: Unknown result type (might be due to invalid IL or missing references)
		//IL_0a92: Unknown result type (might be due to invalid IL or missing references)
		//IL_0aa5: Unknown result type (might be due to invalid IL or missing references)
		//IL_0568: Unknown result type (might be due to invalid IL or missing references)
		//IL_05ba: Unknown result type (might be due to invalid IL or missing references)
		if (pattern == "none" || string.IsNullOrEmpty(pattern))
		{
			SolidColorBrush solid = new SolidColorBrush(baseColor);
			((Freezable)solid).Freeze();
			return solid;
		}
		Color strong = Color.FromArgb(102, accent.R, accent.G, accent.B);
		Color soft = Color.FromArgb(64, accent.R, accent.G, accent.B);
		Pen pen = new Pen(new SolidColorBrush(strong), 2.4);
		SolidColorBrush fill = new SolidColorBrush(strong);
		SolidColorBrush softFill = new SolidColorBrush(soft);
		double s = TileSize(pattern);
		DrawingGroup draw = new DrawingGroup();
		draw.Children.Add(new GeometryDrawing(new SolidColorBrush(baseColor), null, new RectangleGeometry(new Rect(0.0, 0.0, s, s))));
		switch (pattern)
		{
		case "dots":
			Circle(s * 0.25, s * 0.25, 5.0, filled: true);
			Circle(s * 0.75, s * 0.75, 5.0, filled: true);
			Circle(s * 0.75, s * 0.25, 5.0, filled: true);
			Circle(s * 0.25, s * 0.75, 5.0, filled: true);
			break;
		case "diagonal":
		{
			for (double o2 = 0.0 - s; o2 <= s; o2 += s / 3.0)
			{
				Line(o2, s, o2 + s, 0.0);
			}
			break;
		}
		case "grid":
			Line(0.0, 0.0, 0.0, s);
			Line(0.0, 0.0, s, 0.0);
			break;
		case "circles":
			Circle(s * 0.5, s * 0.5, s * 0.42, filled: false);
			Circle(0.0, 0.0, s * 0.42, filled: false);
			Circle(s, 0.0, s * 0.42, filled: false);
			Circle(0.0, s, s * 0.42, filled: false);
			Circle(s, s, s * 0.42, filled: false);
			break;
		case "triangles":
			draw.Children.Add(new GeometryDrawing(fill, null, Tri(s * 0.5, s * 0.15, s * 0.85, s * 0.8, s * 0.15, s * 0.8)));
			break;
		case "weave":
			draw.Children.Add(new GeometryDrawing(softFill, null, new RectangleGeometry(new Rect(0.0, 0.0, s * 0.5, s * 0.5))));
			draw.Children.Add(new GeometryDrawing(softFill, null, new RectangleGeometry(new Rect(s * 0.5, s * 0.5, s * 0.5, s * 0.5))));
			break;
		case "chevron":
			Line(0.0, s * 0.5, s * 0.5, 0.0);
			Line(s * 0.5, 0.0, s, s * 0.5);
			Line(0.0, s, s * 0.5, s * 0.5);
			Line(s * 0.5, s * 0.5, s, s);
			break;
		case "crosshatch":
		{
			for (double o = 0.0 - s; o <= s; o += s / 2.5)
			{
				Line(o, s, o + s, 0.0);
				Line(o, 0.0, o + s, s);
			}
			break;
		}
		case "rings":
			Circle(s * 0.5, s * 0.5, s * 0.4, filled: false);
			Circle(s * 0.5, s * 0.5, s * 0.25, filled: false);
			Circle(s * 0.5, s * 0.5, s * 0.1, filled: true);
			break;
		case "confetti":
			draw.Children.Add(new GeometryDrawing(fill, null, new RectangleGeometry(new Rect(s * 0.15, s * 0.2, 8.0, 8.0))));
			draw.Children.Add(new GeometryDrawing(softFill, null, new RectangleGeometry(new Rect(s * 0.6, s * 0.55, 8.0, 8.0))));
			Circle(s * 0.8, s * 0.2, 4.0, filled: true);
			Circle(s * 0.3, s * 0.75, 4.0, filled: true);
			break;
		case "plus":
			Line(s * 0.5, s * 0.3, s * 0.5, s * 0.7);
			Line(s * 0.3, s * 0.5, s * 0.7, s * 0.5);
			break;
		case "scales":
			Circle(s * 0.25, 0.0, s * 0.28, filled: false);
			Circle(s * 0.75, 0.0, s * 0.28, filled: false);
			Circle(0.0, s * 0.5, s * 0.28, filled: false);
			Circle(s * 0.5, s * 0.5, s * 0.28, filled: false);
			Circle(s, s * 0.5, s * 0.28, filled: false);
			Circle(s * 0.25, s, s * 0.28, filled: false);
			Circle(s * 0.75, s, s * 0.28, filled: false);
			break;
		default:
		{
			PathGeometry wave = new PathGeometry();
			PathFigure wf = new PathFigure
			{
				StartPoint = new Point(0.0, s * 0.5)
			};
			wf.Segments.Add(new QuadraticBezierSegment(new Point(s * 0.25, s * 0.1), new Point(s * 0.5, s * 0.5), isStroked: true));
			wf.Segments.Add(new QuadraticBezierSegment(new Point(s * 0.75, s * 0.9), new Point(s, s * 0.5), isStroked: true));
			wave.Figures.Add(wf);
			draw.Children.Add(new GeometryDrawing(null, pen, wave));
			break;
		}
		}
		DrawingBrush brush = new DrawingBrush(draw)
		{
			TileMode = TileMode.Tile,
			Viewport = new Rect(0.0, 0.0, s, s),
			ViewportUnits = BrushMappingMode.Absolute,
			Stretch = Stretch.None
		};
		((Freezable)brush).Freeze();
		return brush;
		void Circle(double cx, double cy, double r, bool filled)
		{
			//IL_0028: Unknown result type (might be due to invalid IL or missing references)
			draw.Children.Add(new GeometryDrawing(filled ? fill : null, filled ? null : pen, new EllipseGeometry(new Point(cx, cy), r, r)));
		}
		void Line(double x1, double y1, double x2, double y2)
		{
			//IL_0016: Unknown result type (might be due to invalid IL or missing references)
			//IL_001d: Unknown result type (might be due to invalid IL or missing references)
			draw.Children.Add(new GeometryDrawing(null, pen, new LineGeometry(new Point(x1, y1), new Point(x2, y2))));
		}
	}

	private static PathGeometry Tri(double x1, double y1, double x2, double y2, double x3, double y3)
	{
		//IL_000f: Unknown result type (might be due to invalid IL or missing references)
		//IL_002b: Unknown result type (might be due to invalid IL or missing references)
		//IL_0046: Unknown result type (might be due to invalid IL or missing references)
		PathGeometry g = new PathGeometry();
		PathFigure f = new PathFigure
		{
			StartPoint = new Point(x1, y1),
			IsClosed = true
		};
		f.Segments.Add(new LineSegment(new Point(x2, y2), isStroked: true));
		f.Segments.Add(new LineSegment(new Point(x3, y3), isStroked: true));
		g.Figures.Add(f);
		return g;
	}

	private static double TileSize(string pattern)
	{
		if (1 == 0)
		{
		}
		int num;
		switch (pattern)
		{
		case "circles":
		case "rings":
		case "scales":
			num = 72;
			break;
		case "waves":
			num = 120;
			break;
		case "grid":
			num = 40;
			break;
		case "crosshatch":
			num = 56;
			break;
		default:
			num = 48;
			break;
		}
		if (1 == 0)
		{
		}
		return num;
	}

	public static Color Parse(string hex, Color fallback)
	{
		try
		{
			return (Color)ColorConverter.ConvertFromString(hex);
		}
		catch
		{
			return fallback;
		}
	}
}
