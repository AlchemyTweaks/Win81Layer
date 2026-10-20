using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace Win81Layer;

// Renders the tray NotifyIcon's WinForms ContextMenuStrip as the Windows 8 "Content Menu" pattern, matching the
// WPF Win81Menu palette (TaskbarContextMenu.GetThemePalette non-glass branch): WHITE surface, black text, 2px black
// border, #C8C8C8 separators, #DEDEDE hover, black-inverted pressed row, and an 8.1 chevron submenu arrow.
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
	public override Color CheckBackground => TrayMetro.Accent();
	public override Color CheckSelectedBackground => TrayMetro.Accent();
	public override Color CheckPressedBackground => TrayMetro.Accent();
}

internal sealed class Win81TrayMenuRenderer : ToolStripProfessionalRenderer
{
	public Win81TrayMenuRenderer()
		: base(new Win81TrayMenuColors())
	{
		RoundedEdges = false;
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
		e.TextColor = !e.Item.Enabled ? TrayMetro.Dis : (e.Item.Pressed ? TrayMetro.KbFg : TrayMetro.Fg);
		base.OnRenderItemText(e);
	}

	protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
	{
		Rectangle r = e.Item.Bounds;
		int y = r.Height / 2;
		using Pen pen = new Pen(TrayMetro.SepC, 1f);
		e.Graphics.DrawLine(pen, r.Left + 4, y, r.Right - 4, y);
	}

	protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
	{
		Rectangle r = e.AffectedBounds;
		using Pen pen = new Pen(TrayMetro.BorderC, 2f);
		e.Graphics.DrawRectangle(pen, r.Left + 1, r.Top + 1, r.Width - 2, r.Height - 2);
	}

	protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e)
	{
		// Authentic 8.1-style submenu chevron (a thin right-pointing ">"), accent-contrast white on hover.
		Graphics g = e.Graphics;
		SmoothingMode old = g.SmoothingMode;
		g.SmoothingMode = SmoothingMode.AntiAlias;
		Rectangle r = e.ArrowRectangle;
		Color c = (e.Item != null && e.Item.Pressed) ? TrayMetro.KbFg : TrayMetro.Arrow;
		float cx = r.Left + r.Width * 0.5f;
		float cy = r.Top + r.Height * 0.5f;
		float s = 3.4f;
		using (Pen pen = new Pen(c, 1.6f) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round })
		{
			g.DrawLines(pen, new PointF[3]
			{
				new PointF(cx - s * 0.5f, cy - s),
				new PointF(cx + s * 0.7f, cy),
				new PointF(cx - s * 0.5f, cy + s)
			});
		}
		g.SmoothingMode = old;
	}
}
