using System;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Win81Layer;

// App tray icons arrive at whatever size the app's HICON has (16px for most apps at 100% DPI). To match the system tray
// icons at every taskbar size (TaskbarMetrics.TrayAppIconPx) WITHOUT depending on WPF's render-path filtering (hardware
// HighQuality = mipmapped/trilinear, software = Fant), resample ONCE in software to the exact pixel box with an AREA (box)
// filter in premultiplied space, and draw the result 1:1. For a 16px icon at 18/22 this equals "pixel-double, then
// box-downscale": every source pixel stays a solid block with at most a 1px blended seam (crisp), not a soft bilinear
// stretch. Larger sources (32px HICONs, 128px letter tiles) get a clean area downscale. Same-size sources are returned
// untouched (exactly 1:1). Cheap (<= 64x64 two-pass), so per-second dynamic icons can run it on every HICON change.
internal static class TrayIconFit
{
	internal static ImageSource Fit(ImageSource src, int px)
	{
		if (src is not BitmapSource bs || px <= 0)
		{
			return src;
		}
		try
		{
			int sw = bs.PixelWidth;
			int sh = bs.PixelHeight;
			if (sw <= 0 || sh <= 0 || (sw == px && sh == px))
			{
				return src;
			}
			FormatConvertedBitmap fmt = new FormatConvertedBitmap(bs, PixelFormats.Pbgra32, null, 0.0);
			byte[] s = new byte[sw * sh * 4];
			fmt.CopyPixels(s, sw * 4, 0);
			// Uniform fit inside px x px, centred on whole pixels (non-square art keeps its aspect).
			double k = Math.Min((double)px / sw, (double)px / sh);
			int dw = Math.Clamp((int)Math.Round(sw * k), 1, px);
			int dh = Math.Clamp((int)Math.Round(sh * k), 1, px);
			int ox = (px - dw) / 2;
			int oy = (px - dh) / 2;
			double fx = (double)sw / dw;
			double fy = (double)sh / dh;
			// Horizontal pass: sw x sh -> dw x sh, each output column = coverage-weighted sum of the source columns.
			double[] t = new double[dw * sh * 4];
			for (int x = 0; x < dw; x++)
			{
				double x0 = x * fx;
				double x1 = x0 + fx;
				for (int sx = (int)x0; sx < sw && sx < x1; sx++)
				{
					double wgt = Math.Min(x1, sx + 1) - Math.Max(x0, sx);
					if (wgt <= 0.0)
					{
						continue;
					}
					for (int y = 0; y < sh; y++)
					{
						int si = (y * sw + sx) * 4;
						int ti = (y * dw + x) * 4;
						t[ti] += s[si] * wgt;
						t[ti + 1] += s[si + 1] * wgt;
						t[ti + 2] += s[si + 2] * wgt;
						t[ti + 3] += s[si + 3] * wgt;
					}
				}
			}
			// Vertical pass: dw x sh -> dw x dh, normalised by the footprint (fx * fy), into the centred px x px canvas.
			byte[] d = new byte[px * px * 4];
			double norm = 1.0 / (fx * fy);
			for (int y = 0; y < dh; y++)
			{
				double y0 = y * fy;
				double y1 = y0 + fy;
				for (int x = 0; x < dw; x++)
				{
					double b = 0.0, g = 0.0, r = 0.0, a = 0.0;
					for (int sy = (int)y0; sy < sh && sy < y1; sy++)
					{
						double wgt = Math.Min(y1, sy + 1) - Math.Max(y0, sy);
						if (wgt <= 0.0)
						{
							continue;
						}
						int ti = (sy * dw + x) * 4;
						b += t[ti] * wgt;
						g += t[ti + 1] * wgt;
						r += t[ti + 2] * wgt;
						a += t[ti + 3] * wgt;
					}
					int di = ((y + oy) * px + (x + ox)) * 4;
					byte alpha = ToByte(a * norm);
					d[di] = Math.Min(ToByte(b * norm), alpha);       // premultiplied: colour never exceeds alpha
					d[di + 1] = Math.Min(ToByte(g * norm), alpha);
					d[di + 2] = Math.Min(ToByte(r * norm), alpha);
					d[di + 3] = alpha;
				}
			}
			BitmapSource fit = BitmapSource.Create(px, px, 96.0, 96.0, PixelFormats.Pbgra32, null, d, px * 4);
			fit.Freeze();
			return fit;
		}
		catch (Exception ex)
		{
			Logger.Log("TrayIconFit: " + ex.Message);
			return src;
		}
	}

	private static byte ToByte(double v)
	{
		return (byte)Math.Clamp((int)Math.Round(v), 0, 255);
	}
}
