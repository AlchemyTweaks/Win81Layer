using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace Win81Layer;

public static class TrayIconFactory
{
	[DllImport("user32.dll")]
	private static extern bool DestroyIcon(nint hIcon);

	public static Icon Win81Flag(int size = 32, Color? color = null)
	{
		// The authentic Windows 2012 four-pane flag (perspective wave) in Windows cyan — matches the logo used on the
		// Start buttons / tiles / Chrome extension. Pane corners taken verbatim from Windows_(2012).svg (viewBox 999),
		// scaled to the requested icon size.
		Color c = color ?? Color.FromArgb(0, 174, 240);   // #00AEF0
		using Bitmap bmp = new Bitmap(size, size);
		using (Graphics g = Graphics.FromImage(bmp))
		{
			g.SmoothingMode = SmoothingMode.AntiAlias;
			g.Clear(Color.Transparent);
			float s = (float)size * 0.86f / 999f;   // 86% with a small inset so it isn't edge-to-edge in the tray
			float off = (float)size * 0.07f;
			PointF[][] panes = new PointF[4][]
			{
				new PointF[4] { new PointF(0f, 142f), new PointF(409f, 86f), new PointF(409f, 479f), new PointF(0f, 479f) },
				new PointF[4] { new PointF(458f, 79f), new PointF(999f, 0f), new PointF(999f, 479f), new PointF(458f, 479f) },
				new PointF[4] { new PointF(0f, 521f), new PointF(409f, 521f), new PointF(409f, 914f), new PointF(0f, 858f) },
				new PointF[4] { new PointF(458f, 521f), new PointF(999f, 521f), new PointF(999f, 999f), new PointF(458f, 921f) }
			};
			using SolidBrush brush = new SolidBrush(c);
			foreach (PointF[] pane in panes)
			{
				PointF[] scaled = new PointF[pane.Length];
				for (int i = 0; i < pane.Length; i++)
				{
					scaled[i] = new PointF(pane[i].X * s + off, pane[i].Y * s + off);
				}
				g.FillPolygon(brush, scaled);
			}
		}
		nint h = bmp.GetHicon();
		try
		{
			return (Icon)Icon.FromHandle(h).Clone();
		}
		finally
		{
			DestroyIcon(h);
		}
	}
}
