using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace Win81Layer;

// Renders the tray NotifyIcon's WinForms ContextMenuStrip as the Windows 8 "Content Menu" pattern, matching the
// WPF Win81Menu palette and metrics (TaskbarContextMenu.GetThemePalette non-glass branch, Win81Menu.xaml): 14 px Segoe UI
// in 32 px rows, a 7 px separator inset 8 px, the border 2 px in light and 1 px otherwise, #DEDEDE hover, the inverted
// pressed row, the shortcut column in M.Gesture, and the shared stroke chevron and tick (MenuGlyphs).
internal static class TrayMetro
{
	// Theme-aware: read at paint time (the color table + renderer resolve these per draw) so the tray menu follows
	// light/dark. LIGHT values are the authentic white Content Menu; DARK mirrors TaskbarContextMenu's dark M.* set.
	private static bool Dark => ShellTheme.IsDark;
	// Dark rest mirrors TaskbarContextMenu's Dark rest M.* set (near-black, soft ink, accent keyboard row).
	private static bool Rest => DarkRest.Visuals;
	internal static Color Bg => Rest ? Color.FromArgb(0x0F, 0x0F, 0x0F) : Dark ? Color.FromArgb(0x2B, 0x2B, 0x2B) : Color.White;
	internal static Color BorderC => Rest ? Color.FromArgb(0x4D, 0x4D, 0x4D) : Dark ? Color.FromArgb(0x5A, 0x5A, 0x5A) : Color.Black;
	internal static Color SepC => Rest ? Color.FromArgb(0x3A, 0x3A, 0x3A) : Dark ? Color.FromArgb(0x3F, 0x3F, 0x3F) : Color.FromArgb(0xC8, 0xC8, 0xC8);
	internal static Color Fg => Rest ? Color.FromArgb(0xDC, 0xDC, 0xDC) : Dark ? Color.FromArgb(0xF2, 0xF2, 0xF2) : Color.Black;
	internal static Color Dis => Rest ? Color.FromArgb(0x88, 0x88, 0x88) : Dark ? Color.FromArgb(0x8A, 0x8A, 0x8A) : Color.FromArgb(0x76, 0x76, 0x76);
	internal static Color Arrow => Rest ? Color.FromArgb(0xDC, 0xDC, 0xDC) : Dark ? Color.FromArgb(0xDD, 0xDD, 0xDD) : Color.Black;
	internal static Color Hover => Rest ? Color.FromArgb(0x32, 0x32, 0x32) : Dark ? Color.FromArgb(0x3F, 0x3F, 0x3F) : Color.FromArgb(0xDE, 0xDE, 0xDE);
	// Keyboard/pressed-highlight row inverts in both themes: black bar + white text in light, white bar + black text in dark.
	internal static Color KbBg => Rest ? Color.FromArgb(0x4E, 0x71, 0x79) : Dark ? Color.White : Color.Black;
	internal static Color KbFg => Rest ? Color.FromArgb(0xEE, 0xEE, 0xEE) : Dark ? Color.Black : Color.White;
	// The shortcut column (M.Gesture) and its colour on the pressed row (M.KbGesture).
	internal static Color Gesture => Rest ? Color.FromArgb(0xAA, 0xAA, 0xAA) : Dark ? Color.FromArgb(0xAA, 0xAA, 0xAA) : Color.FromArgb(0x61, 0x61, 0x61);
	internal static Color KbGesture => Rest ? Color.FromArgb(0xEE, 0xEE, 0xEE) : Dark ? Color.FromArgb(0x3A, 0x3A, 0x3A) : Color.FromArgb(0xC8, 0xC8, 0xC8);
	// The border rule of M.BorderThickness: 2 only for the light Content Menu.
	internal static int BorderWidth => (Rest || Dark) ? 1 : 2;

	internal static Color Accent()
	{
		try
		{
			System.Windows.Media.Color m = StartAccent.Color();
			return Color.FromArgb(m.R, m.G, m.B);
		}
		catch
		{
			return Color.FromArgb(0x2A, 0x7D, 0xE1);
		}
	}
}

// The production metrics of the tray menu: one place for the font, the 32 px rows and the 7 px separators, used by the
// menu build in App and by the perfprobe unit check (which builds a throwaway strip with it).
internal static class TrayMenuMetrics
{
	internal const float FontPoints = 10.5f;

	internal const int RowHeight = 32;

	internal const int SeparatorHeight = 7;

	internal static Font CreateFont()
	{
		return new Font("Segoe UI", FontPoints);
	}

	// Applies the font to the strip and its drop-downs and sizes every row and separator, recursively, for the strip's
	// DPI. Rows get top and bottom padding until their preferred height is 32 px at 96 dpi (about Padding(0,6,0,6)),
	// 40 at 120 and 48 at 144. Called when the menu is built and again on every Opening, so a menu that moved to a
	// monitor with another scale is re-sized before it shows (cheap: a dozen items).
	internal static void Apply(ToolStrip strip)
	{
		if (strip == null)
		{
			return;
		}
		if (!IsMenuFont(strip.Font))
		{
			strip.Font = CreateFont();
		}
		int dpi = DpiOf(strip);
		foreach (ToolStripItem item in strip.Items)
		{
			ApplyItem(item, strip.Font, dpi);
		}
	}

	// The strip's DPI: DeviceDpi is valid before the handle exists (the system DPI then, the monitor's once shown).
	internal static int DpiOf(ToolStrip strip)
	{
		try
		{
			int dpi = strip.DeviceDpi;
			return dpi > 0 ? dpi : 96;
		}
		catch
		{
			return 96;
		}
	}

	internal static int Scale(int px, int dpi)
	{
		return (int)Math.Round(px * dpi / 96.0);
	}

	private static bool IsMenuFont(Font? font)
	{
		return font != null && font.Name == "Segoe UI" && Math.Abs(font.SizeInPoints - FontPoints) < 0.01;
	}

	private static void ApplyItem(ToolStripItem item, Font font, int dpi)
	{
		if (item is ToolStripSeparator sep)
		{
			sep.AutoSize = false;
			sep.Height = Scale(SeparatorHeight, dpi);
			return;
		}
		if (item is ToolStripMenuItem mi)
		{
			int scaledRow = Scale(RowHeight, dpi);
			mi.Padding = new Padding(mi.Padding.Left, 0, mi.Padding.Right, 0);
			int h0 = mi.GetPreferredSize(Size.Empty).Height;
			int extra = Math.Max(0, scaledRow - h0);
			int top = extra / 2;
			mi.Padding = new Padding(mi.Padding.Left, top, mi.Padding.Right, extra - top);
			if (mi.HasDropDownItems)
			{
				if (!ReferenceEquals(mi.DropDown.Font, font))
				{
					mi.DropDown.Font = font;
				}
				foreach (ToolStripItem child in mi.DropDownItems)
				{
					ApplyItem(child, font, dpi);
				}
			}
		}
	}

	// A radio item (one of a mutually exclusive set) is marked by its accessible role; the renderer draws a bullet.
	internal static void MarkRadio(ToolStripMenuItem item)
	{
		item.AccessibleRole = AccessibleRole.RadioButton;
	}
}

internal sealed class Win81TrayMenuColors : ProfessionalColorTable
{
	public Win81TrayMenuColors() { UseSystemColors = false; }
	public override Color ToolStripDropDownBackground => TrayMetro.Bg;
	public override Color MenuBorder => TrayMetro.BorderC;
	public override Color MenuItemBorder => TrayMetro.Hover;
	public override Color MenuItemSelected => TrayMetro.Hover;
	public override Color MenuItemSelectedGradientBegin => TrayMetro.Hover;
	public override Color MenuItemSelectedGradientEnd => TrayMetro.Hover;
	public override Color MenuItemPressedGradientBegin => TrayMetro.Bg;
	public override Color MenuItemPressedGradientEnd => TrayMetro.Bg;
	public override Color ImageMarginGradientBegin => TrayMetro.Bg;
	public override Color ImageMarginGradientMiddle => TrayMetro.Bg;
	public override Color ImageMarginGradientEnd => TrayMetro.Bg;
	public override Color SeparatorDark => TrayMetro.SepC;
	public override Color SeparatorLight => TrayMetro.SepC;
	public override Color CheckBackground => TrayMetro.Bg;
	public override Color CheckSelectedBackground => TrayMetro.Hover;
	public override Color CheckPressedBackground => TrayMetro.KbBg;
}

internal sealed class Win81TrayMenuRenderer : ToolStripProfessionalRenderer
{
	public Win81TrayMenuRenderer()
		: base(new Win81TrayMenuColors())
	{
		RoundedEdges = false;
	}

	private static float Dpi(Graphics g)
	{
		float d = g.DpiX;
		return d > 0f ? d : 96f;
	}

	protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e)
	{
		using SolidBrush b = new SolidBrush(TrayMetro.Bg);
		e.Graphics.FillRectangle(b, e.AffectedBounds);
	}

	protected override void OnRenderImageMargin(ToolStripRenderEventArgs e)
	{
		using SolidBrush b = new SolidBrush(TrayMetro.Bg);
		e.Graphics.FillRectangle(b, e.AffectedBounds);
	}

	protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
	{
		Rectangle rect = new Rectangle(Point.Empty, e.Item.Size);
		bool pressed = e.Item.Pressed && e.Item.Enabled;   // pattern 7: the pressed Content-Menu row inverts (theme-aware)
		bool sel = e.Item.Selected && e.Item.Enabled;
		using SolidBrush b = new SolidBrush(pressed ? TrayMetro.KbBg : sel ? TrayMetro.Hover : TrayMetro.Bg);
		e.Graphics.FillRectangle(b, rect);
	}

	protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
	{
		bool pressed = e.Item.Pressed && e.Item.Enabled;
		bool shortcut = e.Item is ToolStripMenuItem mi && !string.IsNullOrEmpty(mi.ShortcutKeyDisplayString) && e.Text == mi.ShortcutKeyDisplayString;
		if (shortcut)
		{
			// The shortcut column, as in the WPF menus: 12 px in M.Gesture (M.KbGesture on the pressed row), left-aligned
			// in one column per drop-down whose longest entry ends 10 px inside the row (where the chevron ends too).
			Color color = !e.Item.Enabled ? TrayMetro.Dis : (pressed ? TrayMetro.KbGesture : TrayMetro.Gesture);
			using Font small = new Font(e.TextFont.FontFamily, 9f, e.TextFont.Style);
			int inset = (int)Math.Round(10f * Dpi(e.Graphics) / 96f);
			int column = ShortcutColumnWidth(e.Item.Owner, small);
			int x = Math.Max(e.TextRectangle.Left, e.Item.Width - inset - column);
			Rectangle r = new Rectangle(x, 0, Math.Max(1, e.Item.Width - inset - x), e.Item.Height);
			TextRenderer.DrawText(e.Graphics, e.Text, small, r, color,
				TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding);
			return;
		}
		e.TextColor = !e.Item.Enabled ? TrayMetro.Dis : (pressed ? TrayMetro.KbFg : TrayMetro.Fg);
		// The row's height comes from its top/bottom padding, which the stock text box does not centre on: the label is
		// centred on the whole row instead, on the same line as the tick and the chevron.
		Rectangle tr = e.TextRectangle;
		e.TextRectangle = new Rectangle(tr.Left, 0, tr.Width, e.Item.Height);
		e.TextFormat = (e.TextFormat & ~TextFormatFlags.Bottom) | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine;
		base.OnRenderItemText(e);
	}

	// The widest shortcut of one drop-down at the shortcut font, so every shortcut there starts at the same x.
	private static int ShortcutColumnWidth(ToolStrip? owner, Font font)
	{
		int widest = 0;
		if (owner == null)
		{
			return widest;
		}
		foreach (ToolStripItem item in owner.Items)
		{
			if (item is ToolStripMenuItem mi && item.Available && !string.IsNullOrEmpty(mi.ShortcutKeyDisplayString))
			{
				Size s = TextRenderer.MeasureText(mi.ShortcutKeyDisplayString, font, Size.Empty, TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding);
				widest = Math.Max(widest, s.Width);
			}
		}
		return widest;
	}

	protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
	{
		// 7 px tall, a 1 px line inset 8 px on each side (item coordinates)
		Size s = e.Item.Size;
		int inset = (int)Math.Round(8f * Dpi(e.Graphics) / 96f);
		int y = s.Height / 2;
		using SolidBrush b = new SolidBrush(TrayMetro.SepC);
		e.Graphics.FillRectangle(b, inset, y, Math.Max(0, s.Width - 2 * inset), 1);
	}

	protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
	{
		Rectangle r = e.AffectedBounds;
		int w = TrayMetro.BorderWidth;
		using SolidBrush b = new SolidBrush(TrayMetro.BorderC);
		Graphics g = e.Graphics;
		g.FillRectangle(b, r.Left, r.Top, r.Width, w);
		g.FillRectangle(b, r.Left, r.Bottom - w, r.Width, w);
		g.FillRectangle(b, r.Left, r.Top, w, r.Height);
		g.FillRectangle(b, r.Right - w, r.Top, w, r.Height);
	}

	protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e)
	{
		// The shared tick (MenuGlyphs.Tick, 12x10, 1.5 stroke, round caps) in the row ink, no fill; a radio item gets a
		// 6 px bullet instead.
		Graphics g = e.Graphics;
		bool pressed = e.Item.Pressed && e.Item.Enabled;
		Color c = !e.Item.Enabled ? TrayMetro.Dis : (pressed ? TrayMetro.KbFg : TrayMetro.Fg);
		float k = Dpi(g) / 96f;
		Rectangle r = e.ImageRectangle;
		float cx = (float)Math.Round(r.Left + r.Width / 2f);
		float cy = (float)Math.Round(r.Top + r.Height / 2f);
		SmoothingMode oldS = g.SmoothingMode;
		PixelOffsetMode oldP = g.PixelOffsetMode;
		g.SmoothingMode = SmoothingMode.AntiAlias;
		g.PixelOffsetMode = PixelOffsetMode.HighQuality;
		if (e.Item.AccessibleRole == AccessibleRole.RadioButton)
		{
			float d = 6f * k;
			using SolidBrush b = new SolidBrush(c);
			g.FillEllipse(b, cx - d / 2f, cy - d / 2f, d, d);
		}
		else
		{
			// "M1,5 L4.5,8.5 L11,2" in a 12x10 box centred on the image rectangle
			float ox = cx - 6f * k;
			float oy = cy - 5f * k;
			using Pen pen = new Pen(c, 1.5f * k) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
			g.DrawLines(pen, new PointF[3]
			{
				new PointF(ox + 1f * k, oy + 5f * k),
				new PointF(ox + 4.5f * k, oy + 8.5f * k),
				new PointF(ox + 11f * k, oy + 2f * k)
			});
		}
		g.SmoothingMode = oldS;
		g.PixelOffsetMode = oldP;
	}

	protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e)
	{
		// The shared submenu chevron (MenuGlyphs.Chevron): tip at (cx+2, cy), ends at (cx-2, cy-4) and (cx-2, cy+4),
		// 1.5 px pen scaled by dpi, round caps; cx = the row's right edge - 12, cy = the row's centre.
		Graphics g = e.Graphics;
		SmoothingMode oldS = g.SmoothingMode;
		PixelOffsetMode oldP = g.PixelOffsetMode;
		g.SmoothingMode = SmoothingMode.AntiAlias;
		g.PixelOffsetMode = PixelOffsetMode.HighQuality;
		float k = Dpi(g) / 96f;
		Rectangle r = e.ArrowRectangle;
		bool enabled = e.Item == null || e.Item.Enabled;
		Color c = !enabled ? TrayMetro.Dis : ((e.Item != null && e.Item.Pressed) ? TrayMetro.KbFg : TrayMetro.Arrow);
		// the arrow area ends short of the row's edge, so the row width puts the ink 10 px inside it as in the WPF menus
		float right = e.Item != null ? Math.Max(r.Right, e.Item.Width) : r.Right;
		float cx = (float)Math.Round(right - 12f * k);
		float rowH = e.Item != null ? e.Item.Height : r.Height;
		float cy = (float)Math.Round(e.Item != null ? rowH / 2f : r.Top + r.Height / 2f);
		using (Pen pen = new Pen(c, 1.5f * k) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round })
		{
			g.DrawLines(pen, new PointF[3]
			{
				new PointF(cx - 2f * k, cy - 4f * k),
				new PointF(cx + 2f * k, cy),
				new PointF(cx - 2f * k, cy + 4f * k)
			});
		}
		g.SmoothingMode = oldS;
		g.PixelOffsetMode = oldP;
	}
}
