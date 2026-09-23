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
	internal static readonly Color Bg = Color.White;
	internal static readonly Color BorderC = Color.Black;
	internal static readonly Color SepC = Color.FromArgb(0xC8, 0xC8, 0xC8);
	internal static readonly Color Fg = Color.Black;
	internal static readonly Color Dis = Color.FromArgb(0x76, 0x76, 0x76);
	internal static readonly Color Arrow = Color.Black;
	internal static readonly Color Hover = Color.FromArgb(0xDE, 0xDE, 0xDE);

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
		bool pressed = e.Item.Pressed && e.Item.Enabled;   // pattern 7: the pressed Content-Menu row inverts to black
		bool sel = e.Item.Selected && e.Item.Enabled;
		using SolidBrush b = new SolidBrush(pressed ? Color.Black : sel ? TrayMetro.Hover : TrayMetro.Bg);
		e.Graphics.FillRectangle(b, rect);
	}

	protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
	{
		e.TextColor = !e.Item.Enabled ? TrayMetro.Dis : (e.Item.Pressed ? Color.White : TrayMetro.Fg);
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
		Color c = (e.Item != null && e.Item.Pressed) ? Color.White : TrayMetro.Arrow;
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
