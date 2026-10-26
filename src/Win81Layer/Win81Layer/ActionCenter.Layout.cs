using System;
using System.Drawing;

namespace Win81Layer;

// Where the Action Center goes: the physical rectangle of the panel, the unit vector that points from the panel toward the
// taskbar (the side it rises from) and the tallest the panel may be there (physical pixels).
internal readonly record struct ActionCenterPlacement(Rectangle Px, System.Windows.Vector Toward, int MaxHeightPx);

public sealed partial class ActionCenter
{
	// The flyout family's width (the volume and network flyouts are 376 DIP) and its narrow-monitor floor.
	internal const double PanelWidthDip = 376.0;

	internal const double MinPanelWidthDip = 320.0;

	// The gap between the panel and the taskbar (the flyouts' popup gap) and the inset from the screen edge at the tray end.
	internal const double BarGapDip = 6.0;

	internal const double EdgeInsetDip = 6.0;

	// The content metrics (DIP): a 1 DIP frame, 11 DIP side padding, 12 DIP above the tiles, 85x66 tiles with 4 DIP gutters,
	// 10 DIP to the 40 DIP slider rows, 6 DIP to the footer's 1 DIP divider and its 40 DIP row.
	internal const double FrameDip = 1.0;

	internal const double SidePaddingDip = 11.0;

	internal const double TopPaddingDip = 12.0;

	internal const double TileHeightDip = 66.0;

	internal const double TileGapDip = 4.0;

	internal const double TilesToSlidersDip = 10.0;

	internal const double SliderRowDip = 40.0;

	internal const double SlidersToFooterDip = 6.0;

	internal const double FooterDip = 40.0;

	// Everything but the tile block and the slider rows: 1 + 12 + 10 + 6 + 1 + 40 + 1 = 71.
	internal const double FixedChromeDip = FrameDip + TopPaddingDip + TilesToSlidersDip + SlidersToFooterDip + 1.0 + FooterDip + FrameDip;

	private System.Windows.Vector _toward = new System.Windows.Vector(0.0, 1.0);

	private Rectangle _placedPx;

	// The monitor (device name) the panel was last placed on.
	private string? _openDevice;

	// True while the panel is open (not closing) on the given monitor: an auto-hide taskbar there stays shown under it.
	internal static bool IsOpenOn(string? deviceName)
	{
		ActionCenter? ac = _instance;
		if (ac == null || deviceName == null || !ac.CheckAccess())
		{
			return false;
		}
		return ac.IsVisible && !ac._dismissing && string.Equals(ac._openDevice, deviceName, StringComparison.OrdinalIgnoreCase);
	}

	internal static string NormalizeEdge(string edge)
	{
		if (string.Equals(edge, "Top", StringComparison.OrdinalIgnoreCase))
		{
			return "Top";
		}
		if (string.Equals(edge, "Left", StringComparison.OrdinalIgnoreCase))
		{
			return "Left";
		}
		if (string.Equals(edge, "Right", StringComparison.OrdinalIgnoreCase))
		{
			return "Right";
		}
		return "Bottom";
	}

	// W = 376 DIP, or the available width less 12 DIP on a narrow monitor (never under 320).
	internal static double PanelWidthFor(double availableWidthDip)
	{
		return Math.Clamp(availableWidthDip - 12.0, MinPanelWidthDip, PanelWidthDip);
	}

	// The tile block's natural height for a number of rows (rows x 66 plus the gutters between them).
	internal static double TileBlockDip(int rows)
	{
		rows = Math.Max(1, rows);
		return rows * TileHeightDip + (rows - 1) * TileGapDip;
	}

	// H = 71 + T + 40n: 247 DIP for two tile rows and one slider, 357 for three rows and two sliders.
	internal static double ContentHeightDip(int tileRows, int sliderRows)
	{
		return FixedChromeDip + TileBlockDip(tileRows) + SliderRowDip * Math.Max(0, sliderRows);
	}

	// One tile row and the gutter under it: the tile viewport's scroll step.
	internal const double TileStrideDip = TileHeightDip + TileGapDip;

	// The tile viewport when the panel cannot be tall enough for every row: the most whole rows that fit the available
	// height (k x 66 + (k - 1) x 4), at least one and at most all of them, so no row is ever shown cut through its label.
	internal static double TileViewportDip(double availableDip, int rows)
	{
		rows = Math.Max(1, rows);
		int fit = double.IsNaN(availableDip) ? 1 : (int)Math.Floor((availableDip + TileGapDip) / TileStrideDip + 1e-9);
		return TileBlockDip(Math.Clamp(fit, 1, rows));
	}

	internal static int ToPx(double dip, double scale)
	{
		return (int)Math.Round(dip * scale, MidpointRounding.AwayFromZero);
	}

	// Pure placement (no window, no monitor query): the panel of wDip x hDip at the tray end of the taskbar on the given
	// edge, 6 DIP from the bar (its rect when known, which also covers auto-hide; else the work area edge) and 6 DIP from the
	// screen edge, clamped inside the monitor less that inset. monitorPx defaults to the work area joined with the bar.
	internal static ActionCenterPlacement PlacePanel(Rectangle workPx, Rectangle? barPx, string edge, double scale, double wDip, double hDip, Rectangle? monitorPx = null)
	{
		if (scale <= 0.0 || double.IsNaN(scale))
		{
			scale = 1.0;
		}
		int w = ToPx(wDip, scale);
		int h = ToPx(hDip, scale);
		int g = ToPx(BarGapDip, scale);
		int e = ToPx(EdgeInsetDip, scale);
		bool hasBar = barPx is { IsEmpty: false };
		Rectangle bar = hasBar ? barPx!.Value : Rectangle.Empty;
		int x;
		int y;
		int maxH;
		System.Windows.Vector toward;
		switch (NormalizeEdge(edge))
		{
		case "Top":
		{
			int anchor = hasBar ? bar.Bottom : workPx.Top;
			x = workPx.Right - e - w;
			y = anchor + g;
			maxH = workPx.Bottom - e - (anchor + g);
			toward = new System.Windows.Vector(0.0, -1.0);
			break;
		}
		case "Left":
		{
			int anchor = hasBar ? bar.Right : workPx.Left;
			x = anchor + g;
			y = workPx.Bottom - e - h;
			maxH = workPx.Height - 2 * e;
			toward = new System.Windows.Vector(-1.0, 0.0);
			break;
		}
		case "Right":
		{
			int anchor = hasBar ? bar.Left : workPx.Right;
			x = anchor - g - w;
			y = workPx.Bottom - e - h;
			maxH = workPx.Height - 2 * e;
			toward = new System.Windows.Vector(1.0, 0.0);
			break;
		}
		default:
		{
			int anchor = hasBar ? bar.Top : workPx.Bottom;
			x = workPx.Right - e - w;
			y = anchor - g - h;
			maxH = anchor - g - (workPx.Top + e);
			toward = new System.Windows.Vector(0.0, 1.0);
			break;
		}
		}
		Rectangle mon = monitorPx is { IsEmpty: false } ? monitorPx.Value : (hasBar ? Rectangle.Union(workPx, bar) : workPx);
		x = ClampInto(x, mon.Left + e, mon.Right - e - w);
		y = ClampInto(y, mon.Top + e, mon.Bottom - e - h);
		return new ActionCenterPlacement(new Rectangle(x, y, w, h), toward, Math.Max(0, maxH));
	}

	// value inside [lo, hi]; lo wins when the range is empty (a panel larger than the monitor keeps its top/left edge on it).
	private static int ClampInto(int value, int lo, int hi)
	{
		if (hi < lo)
		{
			return lo;
		}
		return Math.Clamp(value, lo, hi);
	}
}
