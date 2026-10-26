using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Win81Layer;

// --perfprobe unit: the motion tokens and curves (pure logic) and the tray menu's production metrics (a throwaway
// ContextMenuStrip that is never shown; no NotifyIcon).
internal static partial class PerfProbeDiagnostics
{
	private static void UnitMotionTokens()
	{
		// the three curves: the table + bracket solve within 0.002 of the analytic solve over 1000 samples, monotonic,
		// with exact ends
		foreach ((string name, CubicBezierEase ease) in new (string, CubicBezierEase)[3] { ("decelerate", Motion.Decelerate), ("soft-decelerate", Motion.SoftDecelerate), ("accelerate", Motion.Accelerate) })
		{
			double worst = 0.0;
			bool monotonic = true;
			double prev = 0.0;
			for (int i = 0; i <= 1000; i++)
			{
				double p = i / 1000.0;
				double v = ease.Ease(p);
				worst = Math.Max(worst, Math.Abs(v - ease.Analytic(p)));
				if (v < prev - 1e-9)
				{
					monotonic = false;
				}
				prev = v;
			}
			bool ends = ease.Ease(0.0) == 0.0 && ease.Ease(1.0) == 1.0;
			UnitCheck("motion-curve-" + name, worst <= 0.002 && monotonic && ends, $"max error {worst:0.00000} monotonic={monotonic} ends={ends}");
		}
		Motion.Tok[] toks = (Motion.Tok[])Enum.GetValues(typeof(Motion.Tok));
		List<string> shorter = toks.Where(t => Motion.TokMs(t, MotionMode.Reduced) < Motion.TokMs(t, MotionMode.Authentic)).Select(t => t.ToString()).ToList();
		UnitCheck("motion-reduced-never-shorter", shorter.Count == 0, shorter.Count == 0 ? $"{toks.Length} tokens: Reduced >= Authentic" : "shorter: " + string.Join(",", shorter));
		List<string> fastBad = toks.Where(t => Math.Abs(Motion.TokMs(t, MotionMode.Fast) - 0.8 * Motion.TokMs(t, MotionMode.Authentic)) > 1e-9).Select(t => t.ToString()).ToList();
		UnitCheck("motion-fast-0.8x", fastBad.Count == 0, fastBad.Count == 0 ? "every token at 0.8x" : string.Join(",", fastBad));
		double[] distances = new double[11] { 2, 4, 5, 6, 8, 12, 24, 40, 48, 86, 320 };
		List<string> distBad = new List<string>();
		foreach (double d in distances)
		{
			double want = Math.Min(d * 0.5, 24.0);
			if (want < 3.0)
			{
				want = 0.0;
			}
			double got = Motion.DistanceFor(d, MotionMode.Reduced);
			if (Math.Abs(got - want) > 1e-9 || Math.Abs(Motion.DistanceFor(-d, MotionMode.Reduced) + want) > 1e-9 || Motion.DistanceFor(d, MotionMode.Authentic) != d || Motion.DistanceFor(d, MotionMode.Fast) != d)
			{
				distBad.Add($"{d}->{got} (want {want})");
			}
		}
		UnitCheck("motion-reduced-distance", distBad.Count == 0, distBad.Count == 0 ? "min(d/2, 24), 0 under 3 DIP; Authentic and Fast unchanged" : string.Join(" ", distBad));
		bool offInstant = toks.All(t => Motion.TokMs(t, MotionMode.Off) == 0.0) && distances.All(d => Motion.DistanceFor(d, MotionMode.Off) == 0.0);
		UnitCheck("motion-off-instant", offInstant, "Off: 0 ms, no travel");
		MotionMode saved = Motion.UserMode;
		bool savedCalm = Motion.CalmOverride;
		try
		{
			Motion.Mode = MotionMode.Authentic;
			Motion.CalmOverride = true;
			bool calmAuthentic = Motion.Mode == MotionMode.Reduced && !Motion.AllowScale && !Motion.AllowStagger;
			Motion.Mode = MotionMode.Fast;
			bool calmFast = Motion.Mode == MotionMode.Reduced;
			Motion.Mode = MotionMode.Off;
			bool calmOff = Motion.Mode == MotionMode.Off;
			UnitCheck("motion-calm-is-reduced", calmAuthentic && calmFast && calmOff, $"calm+Authentic={calmAuthentic} calm+Fast={calmFast} calm+Off stays Off={calmOff}");
		}
		finally
		{
			Motion.CalmOverride = savedCalm;
			Motion.Mode = saved;
		}
	}

	// The first 144 Hz frame (6.94 ms) of each entrance, in Authentic: short travel settles visibly but starts softly.
	private static void UnitMotionFirstFrame()
	{
		const double frame = 1000.0 / 144.0;
		double Moved(double px, Motion.Tok tok) => Math.Abs(px) * Motion.Ease(tok).Ease(frame / Motion.TokMs(tok, MotionMode.Authentic));
		double menu = Moved(MenuMotion.Distance, Motion.Move(MenuMotion.Distance));
		double flyout = Moved(12.0, Motion.Move(12.0));
		// the Action Center rises 12 DIP like the tray flyouts (it is one of them now)
		double ac = Moved(12.0, Motion.Move(12.0));
		double acOpacity = Motion.Ease(Motion.Tok.FadeIn).Ease(frame / Motion.TokMs(Motion.Tok.FadeIn, MotionMode.Authentic));
		double bar = Moved(86.0, Motion.Move(86.0));
		double pane = Moved(346.0, Motion.Move(346.0));
		bool ok = menu <= 2.0 && flyout <= 3.0 && ac <= 3.0 && acOpacity <= 0.10 && bar <= 1.5 && pane <= 1.5
			&& Motion.Move(86.0) == Motion.Tok.MoveLong && Motion.Move(346.0) == Motion.Tok.MovePanel;
		UnitCheck("motion-first-frame", ok, $"menu 4->{menu:0.00} flyout 12->{flyout:0.00} AC 12->{ac:0.00} (opacity {acOpacity:0.000}) bar 86->{bar:0.00} pane 346->{pane:0.00} px");
	}

	// Notifications are entirely Windows' own: the native hosts the launcher suspends never include the one that shows
	// toasts, the notification center and quick settings (ShellExperienceHost).
	private static void UnitNativeHostsExcludeNotificationHost()
	{
		string[] hosts = NativeShell.HostNames;
		bool ok = hosts.Length > 0 && !hosts.Any(h => string.Equals(h, "ShellExperienceHost", StringComparison.OrdinalIgnoreCase));
		UnitCheck("native-hosts-exclude-notification-host", ok, string.Join(",", hosts));
	}

	// The tray menu built with the production metrics and renderer: 10.5 pt, 32 px rows, 7 px separators.
	private static void UnitTrayMenuMetrics()
	{
		using System.Windows.Forms.ContextMenuStrip strip = new System.Windows.Forms.ContextMenuStrip();
		Win81TrayMenuRenderer renderer = new Win81TrayMenuRenderer();
		strip.Renderer = renderer;
		strip.ShowImageMargin = true;
		strip.Items.Add(new System.Windows.Forms.ToolStripMenuItem("Open Start") { ShortcutKeyDisplayString = "Win" });
		strip.Items.Add(new System.Windows.Forms.ToolStripMenuItem("Charms") { ShortcutKeyDisplayString = "Win+C" });
		strip.Items.Add(new System.Windows.Forms.ToolStripSeparator());
		System.Windows.Forms.ToolStripMenuItem look = new System.Windows.Forms.ToolStripMenuItem("Look");
		System.Windows.Forms.ToolStripMenuItem normal = new System.Windows.Forms.ToolStripMenuItem("Normal") { Checked = true };
		TrayMenuMetrics.MarkRadio(normal);
		look.DropDownItems.Add(normal);
		look.DropDownItems.Add(new System.Windows.Forms.ToolStripMenuItem("Dark rest"));
		strip.Items.Add(look);
		strip.Items.Add(new System.Windows.Forms.ToolStripMenuItem("Hot corners") { Checked = true, CheckOnClick = true });
		strip.Items.Add(new System.Windows.Forms.ToolStripSeparator());
		strip.Items.Add(new System.Windows.Forms.ToolStripMenuItem("Exit"));
		TrayMenuMetrics.Apply(strip);
		System.Drawing.Size size = strip.GetPreferredSize(System.Drawing.Size.Empty);
		strip.Size = size;
		strip.PerformLayout();
		int dpi = TrayMenuMetrics.DpiOf(strip);
		int rowWant = TrayMenuMetrics.Scale(TrayMenuMetrics.RowHeight, dpi);
		int sepWant = TrayMenuMetrics.Scale(TrayMenuMetrics.SeparatorHeight, dpi);
		List<int> rows = strip.Items.OfType<System.Windows.Forms.ToolStripMenuItem>().Select(i => i.Bounds.Height).ToList();
		List<int> seps = strip.Items.OfType<System.Windows.Forms.ToolStripSeparator>().Select(i => i.Bounds.Height).ToList();
		List<string> pads = strip.Items.OfType<System.Windows.Forms.ToolStripMenuItem>().Select(i => $"{i.Padding.Top}/{i.Padding.Bottom}").Distinct().ToList();
		bool font = Math.Abs(strip.Font.SizeInPoints - TrayMenuMetrics.FontPoints) < 0.01 && strip.Font.Name == "Segoe UI";
		bool ok = font && rows.Count > 0 && rows.All(h => h == rowWant) && seps.All(h => h == sepWant);
		UnitCheck("tray-menu-metrics", ok, $"font {strip.Font.Name} {strip.Font.SizeInPoints}pt at {dpi} dpi rows=[{string.Join(",", rows)}] (want {rowWant}) separators=[{string.Join(",", seps)}] (want {sepWant}) padding top/bottom {string.Join(" ", pads)} size {size.Width}x{size.Height}");
		// the drawn strip, for the visual review, and the label's line: on the "Look" row (no descenders) the label's ink
		// and the chevron's ink are both centred on the row (+-1.5 px), as in the WPF menus
		try
		{
			using System.Drawing.Bitmap bmp = new System.Drawing.Bitmap(Math.Max(1, size.Width), Math.Max(1, size.Height));
			strip.DrawToBitmap(bmp, new System.Drawing.Rectangle(0, 0, bmp.Width, bmp.Height));
			if (!string.IsNullOrEmpty(_outDir))
			{
				bmp.Save(Path.Combine(_outDir, "tray-menu-unit.png"), System.Drawing.Imaging.ImageFormat.Png);
			}
			System.Drawing.Rectangle row = look.Bounds;
			double rowCentre = row.Top + row.Height / 2.0;
			(double labelCentre, int labelInk) = InkCentreY(bmp, row.Left + 20, row.Left + row.Width / 2, row.Top, row.Bottom, TrayMetro.Bg);
			(double chevronCentre, int chevronInk) = InkCentreY(bmp, row.Right - 24, row.Right - 4, row.Top, row.Bottom, TrayMetro.Bg);
			bool centred = labelInk > 0 && chevronInk > 0 && Math.Abs(labelCentre - rowCentre) <= 1.5 && Math.Abs(chevronCentre - rowCentre) <= 1.5;
			UnitCheck("tray-menu-label-centred", centred, $"row {row.Top}-{row.Bottom} centre {rowCentre:0.0}: label ink centre {labelCentre:0.0} ({labelInk} px), chevron ink centre {chevronCentre:0.0} ({chevronInk} px)");
		}
		catch (Exception ex)
		{
			Note("tray menu bitmap: " + ex.Message);
		}
	}

	// The vertical centre of the pixels in a box that clearly differ from the background (ink), and how many there are.
	private static (double Centre, int Count) InkCentreY(System.Drawing.Bitmap bmp, int x0, int x1, int y0, int y1, System.Drawing.Color bg)
	{
		int top = int.MaxValue;
		int bottom = int.MinValue;
		int count = 0;
		for (int y = Math.Max(0, y0); y < Math.Min(bmp.Height, y1); y++)
		{
			for (int x = Math.Max(0, x0); x < Math.Min(bmp.Width, x1); x++)
			{
				System.Drawing.Color c = bmp.GetPixel(x, y);
				int d = Math.Max(Math.Abs(c.R - bg.R), Math.Max(Math.Abs(c.G - bg.G), Math.Abs(c.B - bg.B)));
				if (d > 96)
				{
					top = Math.Min(top, y);
					bottom = Math.Max(bottom, y);
					count++;
				}
			}
		}
		return count == 0 ? (0.0, 0) : ((top + bottom + 1) / 2.0, count);
	}
}
