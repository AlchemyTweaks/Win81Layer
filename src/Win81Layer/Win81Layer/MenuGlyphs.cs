using System.Windows;
using System.Windows.Media;
using System.Windows.Shapes;

namespace Win81Layer;

// The one family of menu glyphs, drawn as strokes so no font fallback or glyph hinting is involved: the submenu
// chevron (6x10 box), the drop-down chevron (10x6 box) and the check mark (12x10 box). Every menu implementation
// (Win81Menu.xaml, Metro81.Controls.xaml, the WinForms tray menu) and the flyout rows use these same shapes, with a
// 1.5 DIP stroke and round caps. The geometries are frozen, so they are shared across threads and windows.
public static class MenuGlyphs
{
	// An open 90-degree polyline with 45-degree legs, 4 wide and 4 tall each; the tip sits on the box's centre line.
	public static readonly Geometry Chevron = Make("M1,1 L5,5 L1,9");

	public static readonly Geometry ChevronDown = Make("M1,1 L5,5 L9,1");

	public static readonly Geometry Tick = Make("M1,5 L4.5,8.5 L11,2");

	public const double Stroke = 1.5;

	private static Geometry Make(string data)
	{
		Geometry g = Geometry.Parse(data);
		g.Freeze();
		return g;
	}

	// A code-built chevron for flyout rows and tiles: 6x10, Stretch None, round caps, in the given ink.
	public static Path ChevronPath(Brush ink)
	{
		return new Path
		{
			Data = Chevron,
			Width = 6.0,
			Height = 10.0,
			Stretch = Stretch.None,
			Stroke = ink,
			StrokeThickness = Stroke,
			StrokeStartLineCap = PenLineCap.Round,
			StrokeEndLineCap = PenLineCap.Round,
			StrokeLineJoin = PenLineJoin.Round,
			SnapsToDevicePixels = false,
			UseLayoutRounding = true,
			HorizontalAlignment = HorizontalAlignment.Center,
			VerticalAlignment = VerticalAlignment.Center,
			IsHitTestVisible = false
		};
	}
}
