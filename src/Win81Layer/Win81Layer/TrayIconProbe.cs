using System;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace Win81Layer;

// --trayicontest QA probe: renders the tray NETWORK + VOLUME icons via BOTH paths — the authentic extracted RASTER asset
// (Win81AssetResolver, what the tray uses today) and the resolution-independent VECTOR path (NetIcons81 DrawingImage /
// Segoe MDL2 glyph) — at the real tray box sizes, each with an 8x nearest-neighbour magnification so raster softness vs
// vector crispness is directly visible. Writes an atlas PNG + exits. No mutable state touched.
internal static class TrayIconProbe
{
	private static readonly int[] Sizes = { 13, 15, 18, 20, 24, 27 };   // Small/Normal/Large glyph + net 1.5x boxes
	private const int Mag = 8;                                          // nearest-neighbour zoom for the 15px column
	private static readonly Color Bg = Color.FromRgb(0x1F, 0x1F, 0x1F); // taskbar-ish dark ground

	public static void Begin(Application app)
	{
		app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
		app.Dispatcher.BeginInvoke((Action)delegate
		{
			string qaRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Win81Layer", "qa");
			Directory.CreateDirectory(qaRoot);
			string atlasPath = Path.Combine(qaRoot, "TRAY-ICON-PROBE.png");
			try
			{
				NetState81 wifi = new NetState81(NetKind.Wifi, "Wi-Fi", 4, true);
				NetState81 eth = new NetState81(NetKind.Ethernet, "Ethernet", 0, true);

				(string label, Func<int, ImageSource> src)[] rows =
				{
					("Wi-Fi  asset",   px => Win81AssetResolver.NetworkImage(wifi, px)),
					("Wi-Fi  vector",  px => NetIcons81.For(wifi, px, Colors.White)),
					("Ether  asset",   px => Win81AssetResolver.NetworkImage(eth, px)),
					("Ether  vector",  px => NetIcons81.For(eth, px, Colors.White)),
					("Vol    asset",   px => Win81AssetResolver.GetAsset("Volume.High", px)),
					("Vol    glyph",   px => GlyphImage("", px)),   // MDL2 high-volume speaker (crisp font vector)
				};

				double colW = 84.0;
				double magW = Sizes[1] * Mag + 24.0;
				double left = 150.0;
				double rowH = 92.0;
				int width = (int)(left + Sizes.Length * colW + magW + 30.0);
				int height = (int)(70.0 + rows.Length * rowH + 150.0);

				DrawingVisual visual = new DrawingVisual();
				using (DrawingContext dc = visual.RenderOpen())
				{
					dc.DrawRectangle(FrozenBrush(Color.FromRgb(0x2B, 0x2B, 0x2B)), null, new Rect(0, 0, width, height));
					dc.DrawText(Text("Tray icon sharpness: authentic RASTER asset vs VECTOR, at tray sizes", 17.0, Brushes.White), new Point(20, 18));
					dc.DrawText(Text("(each icon on the taskbar-dark ground; last column = 15px magnified 8x, nearest-neighbour)", 11.5, FrozenBrush(Color.FromRgb(180, 180, 180))), new Point(20, 44));

					double y0 = 70.0;
					// size headers
					for (int c = 0; c < Sizes.Length; c++)
					{
						dc.DrawText(Text(Sizes[c] + "px", 11.0, FrozenBrush(Color.FromRgb(170, 170, 170))), new Point(left + c * colW + 6, y0 - 20));
					}
					dc.DrawText(Text("15px  8x", 11.0, FrozenBrush(Color.FromRgb(170, 170, 170))), new Point(left + Sizes.Length * colW + 6, y0 - 20));

					for (int r = 0; r < rows.Length; r++)
					{
						double y = y0 + r * rowH;
						dc.DrawText(Text(rows[r].label, 13.0, Brushes.White), new Point(16, y + rowH / 2 - 20));
						for (int c = 0; c < Sizes.Length; c++)
						{
							int size = Sizes[c];
							double cx = left + c * colW;
							dc.DrawRectangle(FrozenBrush(Bg), null, new Rect(cx, y, colW - 8, rowH - 8));
							ImageSource img = null;
							try { img = rows[r].src(size); } catch { }
							if (img != null)
							{
								double ox = cx + (colW - 8 - size) / 2.0;
								double oy = y + (rowH - 8 - size) / 2.0;
								dc.DrawImage(img, new Rect(ox, oy, size, size));
							}
							else
							{
								dc.DrawText(Text("(null)", 10.0, FrozenBrush(Color.FromRgb(120, 120, 120))), new Point(cx + 10, y + rowH / 2 - 8));
							}
						}
						// magnified 15px cell (nearest-neighbour = true pixels)
						double mx = left + Sizes.Length * colW;
						dc.DrawRectangle(FrozenBrush(Bg), null, new Rect(mx, y, magW - 8, rowH - 8));
						ImageSource small = null;
						try { small = rows[r].src(15); } catch { }
						if (small != null)
						{
							BitmapSource nn = MagnifyNearest(small, 15, Mag);
							if (nn != null)
							{
								dc.DrawImage(nn, new Rect(mx + 8, y + (rowH - 8 - 15 * Mag) / 2.0, 15 * Mag, 15 * Mag));
							}
						}
					}

					// Volume FLYOUT HEADER scenario (~44px). Compare: current asset request (px=24 -> 24px frame upscaled),
					// best raster (px=44 -> 32px frame upscaled), and the NEW crisp vector glyph. This is the icon the user
					// flagged as blurry in the flyout.
					double fy = y0 + rows.Length * rowH + 20.0;
					int box = 44;
					dc.DrawText(Text("Volume flyout header @44px:", 14.0, Brushes.White), new Point(16, fy - 2));
					(string lbl, ImageSource im)[] cells =
					{
						("asset px=24 (was live)", Safe(() => Win81AssetResolver.VolumeImage(100, false, 24))),
						("asset px=44 (best raster)", Safe(() => Win81AssetResolver.VolumeImage(100, false, 44))),
						("glyph vector (NEW)", Safe(() => GlyphImage81.Get("Segoe MDL2 Assets", 59797, Colors.White, 0.72))),
					};
					for (int i = 0; i < cells.Length; i++)
					{
						double cx = 150.0 + i * 220.0;
						dc.DrawRectangle(FrozenBrush(Bg), null, new Rect(cx, fy + 22, 200, 70));
						if (cells[i].im != null)
						{
							dc.DrawImage(cells[i].im, new Rect(cx + (200 - box) / 2.0, fy + 22 + (70 - box) / 2.0, box, box));
						}
						dc.DrawText(Text(cells[i].lbl, 11.0, FrozenBrush(Color.FromRgb(180, 180, 180))), new Point(cx, fy + 94));
					}
				}

				RenderTargetBitmap bmp = new RenderTargetBitmap(width, height, 96.0, 96.0, PixelFormats.Pbgra32);
				bmp.Render(visual);
				PngBitmapEncoder enc = new PngBitmapEncoder();
				enc.Frames.Add(BitmapFrame.Create(bmp));
				using (FileStream fs = File.Create(atlasPath)) { enc.Save(fs); }
				Logger.Log("TRAY-ICON-PROBE -> " + atlasPath);
				app.Shutdown(0);
			}
			catch (Exception ex)
			{
				Logger.Log("TRAY-ICON-PROBE failed: " + ex);
				app.Shutdown(1);
			}
		}, DispatcherPriority.Loaded);
	}

	// Renders an ImageSource to a size x size Pbgra32 buffer, then replicates each pixel Mag x Mag (true-pixel zoom).
	private static BitmapSource MagnifyNearest(ImageSource image, int size, int mag)
	{
		try
		{
			DrawingVisual v = new DrawingVisual();
			using (DrawingContext c = v.RenderOpen()) { c.DrawImage(image, new Rect(0, 0, size, size)); }
			RenderTargetBitmap rtb = new RenderTargetBitmap(size, size, 96.0, 96.0, PixelFormats.Pbgra32);
			rtb.Render(v);
			int stride = size * 4;
			byte[] px = new byte[size * stride];
			rtb.CopyPixels(px, stride, 0);
			int nw = size * mag, nh = size * mag, nstride = nw * 4;
			byte[] outPx = new byte[nh * nstride];
			for (int y = 0; y < nh; y++)
			{
				int sy = y / mag;
				for (int x = 0; x < nw; x++)
				{
					int sx = x / mag;
					int s = (sy * size + sx) * 4, d = (y * nw + x) * 4;
					outPx[d] = px[s]; outPx[d + 1] = px[s + 1]; outPx[d + 2] = px[s + 2]; outPx[d + 3] = px[s + 3];
				}
			}
			BitmapSource bs = BitmapSource.Create(nw, nh, 96.0, 96.0, PixelFormats.Pbgra32, null, outPx, nstride);
			bs.Freeze();
			return bs;
		}
		catch { return null; }
	}

	private static ImageSource GlyphImage(string glyph, int size)
	{
		FormattedText ft = new FormattedText(glyph, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
			new Typeface(new FontFamily("Segoe MDL2 Assets"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal),
			size, Brushes.White, 1.0);
		DrawingVisual v = new DrawingVisual();
		using (DrawingContext c = v.RenderOpen())
		{
			c.DrawText(ft, new Point((size - ft.Width) / 2.0, (size - ft.Height) / 2.0));
		}
		RenderTargetBitmap rtb = new RenderTargetBitmap(size, size, 96.0, 96.0, PixelFormats.Pbgra32);
		rtb.Render(v);
		rtb.Freeze();
		return rtb;
	}

	private static ImageSource Safe(Func<ImageSource> f) { try { return f(); } catch { return null; } }

	private static FormattedText Text(string s, double size, Brush brush)
	{
		return new FormattedText(s, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
			new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal),
			size, brush, 1.0);
	}

	private static SolidColorBrush FrozenBrush(Color c) { SolidColorBrush b = new SolidColorBrush(c); b.Freeze(); return b; }
}
