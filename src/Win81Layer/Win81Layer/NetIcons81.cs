using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;

namespace Win81Layer;

public enum NetworkIconKind
{
	Wifi,
	Ethernet,
	Generic,
	Cellular
}

public enum NetworkIconState
{
	Auto,
	Connected,
	Scanning,
	Connecting,
	Identifying,
	NotConnected,
	CableUnplugged,
	Limited,
	NoInternet,
	CaptivePortal,
	Secured,
	Wps,
	Metered,
	Airplane,
	NetworkError,
	Unidentified,
	Authenticated,
	Work,
	Offline,
	Disabled,
	HardwareOff,
	Activity,
	VpnConnected,
	VpnDisconnected,
	Roaming,
	DataWarning
}

internal static class NetIcons81
{
	private static readonly object Gate = new object();

	private static readonly Dictionary<string, ImageSource> Cache = new Dictionary<string, ImageSource>(StringComparer.Ordinal);

	private static readonly Color Primary = Color.FromRgb(255, 255, 255);
	private static readonly Color Disabled = Color.FromRgb(166, 166, 166);
	private static readonly Color Dimmed = Color.FromRgb(102, 102, 102);
	private static readonly Color Secondary = Color.FromRgb(200, 200, 200);
	private static readonly Color Error = Color.FromRgb(232, 17, 35);
	private static readonly Color Warning = Color.FromRgb(255, 185, 0);
	private static readonly Color WarningInk = Color.FromRgb(54, 43, 0);

	internal static readonly (NetworkIconKind Kind, NetworkIconState State, int Signal)[] QaMatrix =
	{
		(NetworkIconKind.Wifi, NetworkIconState.NotConnected, 0),
		(NetworkIconKind.Wifi, NetworkIconState.Scanning, 0),
		(NetworkIconKind.Wifi, NetworkIconState.Connecting, 2),
		(NetworkIconKind.Wifi, NetworkIconState.Connected, 1),
		(NetworkIconKind.Wifi, NetworkIconState.Connected, 2),
		(NetworkIconKind.Wifi, NetworkIconState.Connected, 3),
		(NetworkIconKind.Wifi, NetworkIconState.Connected, 4),
		(NetworkIconKind.Wifi, NetworkIconState.Limited, 4),
		(NetworkIconKind.Wifi, NetworkIconState.NoInternet, 4),
		(NetworkIconKind.Wifi, NetworkIconState.CaptivePortal, 4),
		(NetworkIconKind.Wifi, NetworkIconState.Secured, 4),
		(NetworkIconKind.Wifi, NetworkIconState.Wps, 4),
		(NetworkIconKind.Wifi, NetworkIconState.Metered, 4),
		(NetworkIconKind.Wifi, NetworkIconState.Airplane, 0),
		(NetworkIconKind.Wifi, NetworkIconState.Disabled, 0),
		(NetworkIconKind.Wifi, NetworkIconState.HardwareOff, 0),
		(NetworkIconKind.Ethernet, NetworkIconState.NotConnected, 0),
		(NetworkIconKind.Ethernet, NetworkIconState.CableUnplugged, 0),
		(NetworkIconKind.Ethernet, NetworkIconState.Connecting, 0),
		(NetworkIconKind.Ethernet, NetworkIconState.Identifying, 0),
		(NetworkIconKind.Ethernet, NetworkIconState.Connected, 0),
		(NetworkIconKind.Ethernet, NetworkIconState.Limited, 0),
		(NetworkIconKind.Ethernet, NetworkIconState.NoInternet, 0),
		(NetworkIconKind.Ethernet, NetworkIconState.NetworkError, 0),
		(NetworkIconKind.Ethernet, NetworkIconState.Unidentified, 0),
		(NetworkIconKind.Ethernet, NetworkIconState.Authenticated, 0),
		(NetworkIconKind.Ethernet, NetworkIconState.Metered, 0),
		(NetworkIconKind.Ethernet, NetworkIconState.Work, 0),
		(NetworkIconKind.Ethernet, NetworkIconState.HardwareOff, 0),
		(NetworkIconKind.Generic, NetworkIconState.Connected, 0),
		(NetworkIconKind.Generic, NetworkIconState.Connecting, 0),
		(NetworkIconKind.Generic, NetworkIconState.Limited, 0),
		(NetworkIconKind.Generic, NetworkIconState.NoInternet, 0),
		(NetworkIconKind.Generic, NetworkIconState.NetworkError, 0),
		(NetworkIconKind.Generic, NetworkIconState.Identifying, 0),
		(NetworkIconKind.Generic, NetworkIconState.CaptivePortal, 0),
		(NetworkIconKind.Generic, NetworkIconState.Metered, 0),
		(NetworkIconKind.Generic, NetworkIconState.Offline, 0),
		(NetworkIconKind.Generic, NetworkIconState.Airplane, 0),
		(NetworkIconKind.Generic, NetworkIconState.Disabled, 0),
		(NetworkIconKind.Generic, NetworkIconState.HardwareOff, 0),
		(NetworkIconKind.Generic, NetworkIconState.Activity, 0),
		(NetworkIconKind.Generic, NetworkIconState.VpnConnected, 0),
		(NetworkIconKind.Generic, NetworkIconState.VpnDisconnected, 0),
		(NetworkIconKind.Generic, NetworkIconState.Roaming, 0),
		(NetworkIconKind.Generic, NetworkIconState.DataWarning, 0),
		(NetworkIconKind.Cellular, NetworkIconState.Connected, 4),
		(NetworkIconKind.Cellular, NetworkIconState.Roaming, 3),
		(NetworkIconKind.Cellular, NetworkIconState.DataWarning, 2)
	};

	internal static ImageSource For(NetState81 state, int size = 20, Color? foreground = null)
	{
		// Canonical network icon used EVERYWHERE (tray, flyout, action center, settings, lock, charms). Prefer the
		// authentic Win8.1 pnidui asset so all surfaces share one icon set + its state variants; fall back to the vector
		// renderer when the library is absent or a caller requests a specific tint (foreground) the flat asset can't do.
		if (foreground == null)
		{
			ImageSource authentic = Win81AssetResolver.NetworkImage(state, size <= 0 ? 20 : size);
			if (authentic != null) return authentic;
		}
		NetworkIconKind kind = state.Kind switch
		{
			NetKind.Wifi => NetworkIconKind.Wifi,
			NetKind.Ethernet => NetworkIconKind.Ethernet,
			NetKind.Cellular => NetworkIconKind.Cellular,
			_ => NetworkIconKind.Generic
		};
		return Draw(kind, state.EffectiveIconState, state.Bars, size, foreground);
	}

	internal static ImageSource For(bool up, bool wifi, int size = 0, bool internet = true)
	{
		NetworkIconState state = !up ? NetworkIconState.NotConnected : (!internet ? NetworkIconState.Limited : NetworkIconState.Connected);
		NetState81 ns = new NetState81(up ? (wifi ? NetKind.Wifi : NetKind.Ethernet) : NetKind.Offline, wifi ? "Wi-Fi" : "Ethernet", (wifi && up) ? 4 : 0, internet, state);
		ImageSource authentic = Win81AssetResolver.NetworkImage(ns, size <= 0 ? 20 : size);
		if (authentic != null) return authentic;
		return Draw(wifi ? NetworkIconKind.Wifi : NetworkIconKind.Ethernet, state, wifi && up ? 4 : 0, size <= 0 ? 20 : size);
	}

	internal static ImageSource ForType(string? type, int size = 0)
	{
		return type switch
		{
			"Wi-Fi" => Draw(NetworkIconKind.Wifi, NetworkIconState.Connected, 4, size <= 0 ? 20 : size),
			"Ethernet" => Draw(NetworkIconKind.Ethernet, NetworkIconState.Connected, 0, size <= 0 ? 20 : size),
			"Cellular" => Draw(NetworkIconKind.Cellular, NetworkIconState.Connected, 4, size <= 0 ? 20 : size),
			_ => Draw(NetworkIconKind.Generic, NetworkIconState.Offline, 0, size <= 0 ? 20 : size)
		};
	}

	internal static ImageSource Draw(NetworkIconKind kind, NetworkIconState state, int signal = 4, int size = 32, Color? foreground = null)
	{
		state = state == NetworkIconState.Auto ? NetworkIconState.Connected : state;
		int targetSize = size <= 12 ? 12 : (size <= 16 ? 16 : 32);
		int level = Math.Clamp(signal, 0, 4);
		Color color = foreground ?? Primary;
		string key = $"{kind}:{state}:{level}:{targetSize}:{color}";
		lock (Gate)
		{
			if (Cache.TryGetValue(key, out ImageSource cached))
			{
				return cached;
			}
			ImageSource icon = Build(kind, state, level, targetSize, color);
			Cache[key] = icon;
			return icon;
		}
	}

	private static ImageSource Build(NetworkIconKind kind, NetworkIconState state, int signal, int targetSize, Color foreground)
	{
		DrawingGroup group = new DrawingGroup
		{
			ClipGeometry = new RectangleGeometry(new Rect(0.0, 0.0, 32.0, 32.0)),
			GuidelineSet = BuildGuidelines()
		};
		group.Children.Add(new GeometryDrawing(Brushes.Transparent, null, new RectangleGeometry(new Rect(0.0, 0.0, 32.0, 32.0))));

		bool muted = state is NetworkIconState.NotConnected or NetworkIconState.CableUnplugged
			or NetworkIconState.Offline or NetworkIconState.Disabled or NetworkIconState.HardwareOff
			or NetworkIconState.Airplane;
		SolidColorBrush primary = FrozenBrush(muted ? Dimmed : foreground);
		SolidColorBrush secondary = FrozenBrush(state == NetworkIconState.Scanning ? Secondary : Dimmed);
		double stroke = targetSize <= 12 ? 3.25 : (targetSize <= 16 ? 2.95 : 2.65);

		switch (kind)
		{
			case NetworkIconKind.Wifi:
				DrawWifi(group, primary, secondary, signal, state, stroke);
				break;
			case NetworkIconKind.Ethernet:
				DrawEthernet(group, primary, state, stroke);
				break;
			case NetworkIconKind.Cellular:
				DrawCellular(group, primary, secondary, signal);
				break;
			default:
				DrawGlobe(group, primary, stroke);
				break;
		}

		DrawStateOverlay(group, state, targetSize);
		group.Freeze();
		DrawingImage image = new DrawingImage(group);
		image.Freeze();
		return image;
	}

	private static void DrawWifi(DrawingGroup group, Brush primary, Brush secondary, int signal, NetworkIconState state, double stroke)
	{
		int level = signal;
		if (state is NetworkIconState.Scanning or NetworkIconState.Identifying)
		{
			level = 0;
		}
		Brush dotBrush = level >= 1 ? primary : secondary;
		AddFill(group, dotBrush, new EllipseGeometry(new Point(16.0, 27.0), 2.15, 2.15));
		for (int arc = 1; arc <= 3; arc++)
		{
			double radius = arc switch { 1 => 5.0, 2 => 9.8, _ => 14.5 };
			Brush brush = level >= arc + 1 ? primary : secondary;
			AddStroke(group, brush, stroke, WifiArc(radius), round: true);
		}
	}

	private static void DrawEthernet(DrawingGroup group, Brush brush, NetworkIconState state, double stroke)
	{
		// Authentic Windows 11 Ethernet glyph (Segoe Fluent Icons E839 — a monitor with a network plug), fit into the
		// 32-unit grid and filled so it pixel-snaps and composes with the status overlays like the rest of the set.
		// Replaces the old cramped hand-drawn monitor+plug. Connection status is conveyed by tint + the corner
		// overlay badge (as in Windows), so a single glyph serves connected / limited / unplugged.
		AddFill(group, brush, GlyphGeometry("Segoe Fluent Icons", "", new Rect(2.5, 5.5, 21.5, 19.0)));
	}

	// Renders a symbol-font glyph (Segoe Fluent Icons / Segoe MDL2 Assets) as a frozen, transform-flattened Geometry
	// scaled and centered into a target box on the 32-unit design grid, so a real Windows icon can be composed into
	// the same DrawingGroup as the vector overlays and share the icon's pixel-snapping.
	private static Geometry GlyphGeometry(string font, string glyph, Rect target)
	{
		try
		{
			Typeface tf = new Typeface(new FontFamily(font), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
			FormattedText ft = new FormattedText(glyph, System.Globalization.CultureInfo.InvariantCulture,
				FlowDirection.LeftToRight, tf, 100.0, Brushes.White, 1.0);
			Geometry raw = ft.BuildGeometry(new Point(0.0, 0.0));
			Rect b = raw.Bounds;
			if (b.IsEmpty || b.Width <= 0.0 || b.Height <= 0.0)
			{
				return raw;
			}
			double scale = Math.Min(target.Width / b.Width, target.Height / b.Height);
			TransformGroup tg = new TransformGroup();
			tg.Children.Add(new TranslateTransform(-b.X, -b.Y));
			tg.Children.Add(new ScaleTransform(scale, scale));
			tg.Children.Add(new TranslateTransform(
				target.X + (target.Width - b.Width * scale) / 2.0,
				target.Y + (target.Height - b.Height * scale) / 2.0));
			Geometry g = raw.CloneCurrentValue();
			g.Transform = tg;
			Geometry flat = g.GetFlattenedPathGeometry();
			flat.Freeze();
			return flat;
		}
		catch
		{
			return Geometry.Empty;
		}
	}

	private static void DrawCellular(DrawingGroup group, Brush primary, Brush secondary, int signal)
	{
		for (int i = 0; i < 4; i++)
		{
			double height = 6.0 + i * 5.0;
			Brush brush = i < signal ? primary : secondary;
			AddFill(group, brush, new RectangleGeometry(new Rect(3.0 + i * 7.0, 28.0 - height, 5.0, height)));
		}
	}

	private static void DrawGlobe(DrawingGroup group, Brush brush, double stroke)
	{
		AddStroke(group, brush, stroke, new EllipseGeometry(new Point(15.0, 15.0), 11.5, 11.5), round: true);
		AddStroke(group, brush, stroke * 0.72, Parsed("M 3.8,15 L 26.2,15 M 6.7,8.2 C 11.4,11 18.6,11 23.3,8.2 M 6.7,21.8 C 11.4,19 18.6,19 23.3,21.8 M 15,3.5 C 9.2,8.8 9.2,21.2 15,26.5 M 15,3.5 C 20.8,8.8 20.8,21.2 15,26.5"), round: true);
	}

	private static void DrawStateOverlay(DrawingGroup group, NetworkIconState state, int targetSize)
	{
		double fine = targetSize <= 16 ? 2.2 : 1.9;
		SolidColorBrush white = FrozenBrush(Primary);
		SolidColorBrush gray = FrozenBrush(Disabled);
		SolidColorBrush red = FrozenBrush(Error);
		SolidColorBrush amber = FrozenBrush(Warning);
		SolidColorBrush amberInk = FrozenBrush(WarningInk);
		switch (state)
		{
			case NetworkIconState.NotConnected:
				DrawErrorBadge(group, red, white, 25.0, 25.0, fine);
				break;
			case NetworkIconState.CableUnplugged:
				AddFill(group, red, new EllipseGeometry(new Point(25.0, 25.0), 6.5, 6.5));
				AddStroke(group, white, fine, Parsed("M 21,25 L 24,25 M 26,22 L 26,28 M 28,25 L 30,25"));
				break;
			case NetworkIconState.NetworkError:
				AddFill(group, red, Parsed("M 25,18 L 32,25 L 25,32 L 18,25 Z"));
				AddStroke(group, white, fine, Parsed("M 22,22 L 28,28 M 28,22 L 22,28"));
				break;
			case NetworkIconState.Limited:
				AddFill(group, amber, new EllipseGeometry(new Point(25.0, 25.0), 6.5, 6.5));
				AddFill(group, amberInk, new RectangleGeometry(new Rect(24.1, 20.7, 1.8, 5.1)));
				AddFill(group, amberInk, new EllipseGeometry(new Point(25.0, 28.2), 1.05, 1.05));
				break;
			case NetworkIconState.NoInternet:
				DrawWarningTriangle(group, amber, amberInk, false);
				break;
			case NetworkIconState.DataWarning:
				DrawWarningTriangle(group, amber, amberInk, true);
				break;
			case NetworkIconState.CaptivePortal:
				AddFill(group, white, new RectangleGeometry(new Rect(18.0, 20.0, 13.0, 9.5), 0.8, 0.8));
				AddFill(group, gray, new RectangleGeometry(new Rect(19.5, 21.5, 10.0, 2.0)));
				AddFill(group, gray, new EllipseGeometry(new Point(28.2, 26.5), 1.2, 1.2));
				break;
			case NetworkIconState.Secured:
			case NetworkIconState.Authenticated:
				DrawLock(group, white, fine);
				break;
			case NetworkIconState.Metered:
				DrawClock(group, white, fine);
				break;
			case NetworkIconState.Work:
				DrawBuilding(group, white);
				break;
			case NetworkIconState.Connecting:
				DrawCircularArrows(group, white, fine, false);
				break;
			case NetworkIconState.Wps:
				DrawCircularArrows(group, white, fine, true);
				break;
			case NetworkIconState.Scanning:
				DrawDots(group, white);
				break;
			case NetworkIconState.Identifying:
				DrawDots(group, white);
				AddFill(group, amber, new EllipseGeometry(new Point(29.3, 20.7), 1.2, 1.2));
				break;
			case NetworkIconState.Unidentified:
				DrawQuestion(group, white, fine);
				break;
			case NetworkIconState.Offline:
				AddStroke(group, red, targetSize <= 16 ? 4.1 : 3.6, Parsed("M 6,27 L 27,6"), round: true);
				break;
			case NetworkIconState.Airplane:
				DrawPlane(group, white);
				break;
			case NetworkIconState.Disabled:
				AddStroke(group, gray, fine + 0.5, Parsed("M 20,20 L 30,30"));
				break;
			case NetworkIconState.HardwareOff:
				AddFill(group, FrozenBrush(Color.FromRgb(58, 65, 72)), new EllipseGeometry(new Point(25.0, 25.0), 6.5, 6.5));
				AddStroke(group, gray, fine, Parsed("M 21.5,21.5 L 28.5,28.5 M 28.5,21.5 L 21.5,28.5"));
				break;
			case NetworkIconState.Activity:
				AddStroke(group, white, fine, Parsed("M 20,29 L 20,20 M 17,23 L 20,20 L 23,23 M 28,20 L 28,29 M 25,26 L 28,29 L 31,26"));
				break;
			case NetworkIconState.VpnConnected:
				DrawShield(group, white, fine, connected: true);
				break;
			case NetworkIconState.VpnDisconnected:
				DrawShield(group, red, fine, connected: false);
				break;
			case NetworkIconState.Roaming:
				AddStroke(group, white, fine, Parsed("M 19,22 C 22,18 28,18 30,22 L 27,21 M 30,22 L 29,19 M 30,27 C 27,31 21,31 19,27 L 22,28 M 19,27 L 20,30"), round: true);
				break;
		}
	}

	private static void DrawErrorBadge(DrawingGroup group, Brush fill, Brush ink, double x, double y, double stroke)
	{
		AddFill(group, fill, new EllipseGeometry(new Point(x, y), 6.5, 6.5));
		AddStroke(group, ink, stroke, Parsed("M 21.5,21.5 L 28.5,28.5 M 28.5,21.5 L 21.5,28.5"));
	}

	private static void DrawWarningTriangle(DrawingGroup group, Brush fill, Brush ink, bool doubleMark)
	{
		AddFill(group, fill, Parsed("M 25,17.5 L 32,30 L 18,30 Z"));
		AddFill(group, ink, new RectangleGeometry(new Rect(24.1, 21.4, 1.8, 4.8)));
		AddFill(group, ink, new EllipseGeometry(new Point(25.0, 28.2), 1.0, 1.0));
		if (doubleMark)
		{
			AddFill(group, ink, new RectangleGeometry(new Rect(28.0, 24.0, 1.4, 4.0)));
		}
	}

	private static void DrawLock(DrawingGroup group, Brush brush, double stroke)
	{
		AddFill(group, brush, new RectangleGeometry(new Rect(19.0, 23.0, 12.0, 8.5), 0.7, 0.7));
		AddStroke(group, brush, stroke, Parsed("M 21.5,23 L 21.5,20.5 C 21.5,16.5 28.5,16.5 28.5,20.5 L 28.5,23"), round: true);
		AddFill(group, FrozenBrush(Dimmed), new EllipseGeometry(new Point(25.0, 27.0), 1.15, 1.15));
	}

	private static void DrawClock(DrawingGroup group, Brush brush, double stroke)
	{
		AddStroke(group, brush, stroke, new EllipseGeometry(new Point(25.0, 25.0), 6.0, 6.0), round: true);
		AddStroke(group, brush, stroke, Parsed("M 25,21 L 25,25 L 28.2,27"), round: true);
	}

	private static void DrawBuilding(DrawingGroup group, Brush brush)
	{
		AddFill(group, brush, Parsed("M 18,30 L 18,20 L 23,18 L 23,30 Z M 24,30 L 24,15 L 31,18 L 31,30 Z"));
		SolidColorBrush holes = FrozenBrush(Dimmed);
		foreach (Rect rect in new[] { new Rect(20,22,1.5,1.5), new Rect(20,26,1.5,1.5), new Rect(26,20,1.5,1.5), new Rect(29,20,1.5,1.5), new Rect(26,24,1.5,1.5), new Rect(29,24,1.5,1.5) })
		{
			AddFill(group, holes, new RectangleGeometry(rect));
		}
	}

	private static void DrawCircularArrows(DrawingGroup group, Brush brush, double stroke, bool wps)
	{
		AddStroke(group, brush, stroke, Parsed("M 19,23 C 21,18 27,17 30,20 M 30,20 L 27,19 M 30,20 L 29,17 M 31,25 C 29,30 23,31 19,28 M 19,28 L 22,29 M 19,28 L 20,31"), round: true);
		if (wps)
		{
			AddFill(group, brush, new EllipseGeometry(new Point(25.0, 24.5), 1.4, 1.4));
		}
	}

	private static void DrawDots(DrawingGroup group, Brush brush)
	{
		AddFill(group, brush, new EllipseGeometry(new Point(20.0, 27.0), 1.4, 1.4));
		AddFill(group, brush, new EllipseGeometry(new Point(25.0, 27.0), 1.4, 1.4));
		AddFill(group, brush, new EllipseGeometry(new Point(30.0, 27.0), 1.4, 1.4));
	}

	private static void DrawQuestion(DrawingGroup group, Brush brush, double stroke)
	{
		AddStroke(group, brush, stroke, Parsed("M 21,21 C 21,17 29,17 29,21 C 29,24 25,24 25,27"), round: true);
		AddFill(group, brush, new EllipseGeometry(new Point(25.0, 30.0), 1.2, 1.2));
	}

	private static void DrawPlane(DrawingGroup group, Brush brush)
	{
		AddFill(group, brush, Parsed("M 18,24 L 23,22 L 25,17 C 25.7,15.4 27.2,14.6 28.5,15.1 C 29.3,16.2 29,17.5 28,19 L 26,22.5 L 31,25 L 31.8,28 L 24.5,26 L 21.5,30 L 19.5,30 L 21,25.5 L 18,26 Z"));
	}

	private static void DrawShield(DrawingGroup group, Brush brush, double stroke, bool connected)
	{
		AddStroke(group, brush, stroke, Parsed("M 25,17 L 31,20 L 30,27 C 29,30 27,31 25,32 C 23,31 21,30 20,27 L 19,20 Z"), round: true);
		if (connected)
		{
			AddStroke(group, brush, stroke, Parsed("M 22,25 L 24.2,27 L 28.5,22.5"), round: true);
		}
		else
		{
			AddStroke(group, brush, stroke, Parsed("M 22,23 L 28,29 M 28,23 L 22,29"), round: true);
		}
	}

	private static Geometry WifiArc(double radius)
	{
		Point start = Polar(16.0, 27.0, radius, 220.0);
		Point end = Polar(16.0, 27.0, radius, 320.0);
		PathFigure figure = new PathFigure { StartPoint = start, IsClosed = false, IsFilled = false };
		figure.Segments.Add(new ArcSegment(end, new Size(radius, radius), 0.0, false, SweepDirection.Clockwise, true));
		PathGeometry geometry = new PathGeometry();
		geometry.Figures.Add(figure);
		geometry.Freeze();
		return geometry;
	}

	private static Point Polar(double cx, double cy, double radius, double degrees)
	{
		double radians = degrees * Math.PI / 180.0;
		return new Point(cx + radius * Math.Cos(radians), cy + radius * Math.Sin(radians));
	}

	private static Geometry Parsed(string data)
	{
		Geometry geometry = Geometry.Parse(data);
		geometry.Freeze();
		return geometry;
	}

	private static void AddFill(DrawingGroup group, Brush brush, Geometry geometry)
	{
		if (geometry.CanFreeze && !geometry.IsFrozen) geometry.Freeze();
		group.Children.Add(new GeometryDrawing(brush, null, geometry));
	}

	private static void AddStroke(DrawingGroup group, Brush brush, double thickness, Geometry geometry, bool round = false)
	{
		if (geometry.CanFreeze && !geometry.IsFrozen) geometry.Freeze();
		Pen pen = new Pen(brush, thickness)
		{
			StartLineCap = round ? PenLineCap.Round : PenLineCap.Square,
			EndLineCap = round ? PenLineCap.Round : PenLineCap.Square,
			LineJoin = round ? PenLineJoin.Round : PenLineJoin.Miter
		};
		pen.Freeze();
		group.Children.Add(new GeometryDrawing(null, pen, geometry));
	}

	private static SolidColorBrush FrozenBrush(Color color)
	{
		SolidColorBrush brush = new SolidColorBrush(color);
		brush.Freeze();
		return brush;
	}

	private static GuidelineSet BuildGuidelines()
	{
		GuidelineSet guidelines = new GuidelineSet();
		for (int i = 0; i <= 32; i++)
		{
			guidelines.GuidelinesX.Add(i + 0.5);
			guidelines.GuidelinesY.Add(i + 0.5);
		}
		guidelines.Freeze();
		return guidelines;
	}
}
