using System;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Win81Layer;

public static class StartAccent
{
	private static Color? _cached;

	private static string _cachedSig = "";

	public static Color FastColor()
	{
		try
		{
			AppSettings settings = SettingsStore.FastSnapshot;
			if (!string.IsNullOrWhiteSpace(settings.StartAccentColor))
			{
				return (Color)ColorConverter.ConvertFromString(settings.StartAccentColor);
			}
			if (_cached.HasValue)
			{
				return _cached.Value;
			}
		}
		catch
		{
		}
		return Color();
	}

	public static Color Tint(double towardWhite)
	{
		Color c = Color();
		return System.Windows.Media.Color.FromRgb(L(c.R), L(c.G), L(c.B));
		byte L(byte v)
		{
			return (byte)Math.Clamp((double)(int)v + (double)(255 - v) * towardWhite, 0.0, 255.0);
		}
	}

	public static Color Color()
	{
		try
		{
			AppSettings s = SettingsStore.Load();
			string sig = $"{s.StartAccentColor}|{s.StartBgMode}|{s.StartBgCustomPath}|{s.StartBgColor}|{s.StartPattern}|" + ((s.StartBgMode == "desktop") ? TaskbarTheme.WallpaperPath() : "");
			Color? cached = _cached;
			if (cached.HasValue)
			{
				Color hit = cached.GetValueOrDefault();
				if (sig == _cachedSig)
				{
					return hit;
				}
			}
			Color color = (string.IsNullOrEmpty(s.StartAccentColor) ? AutoColor() : ((Color)ColorConverter.ConvertFromString(s.StartAccentColor)));
			_cached = color;
			_cachedSig = sig;
			return color;
		}
		catch
		{
			return System.Windows.Media.Color.FromRgb(106, 44, 145);
		}
	}

	public static Color AutoColor()
	{
		try
		{
			AppSettings s = SettingsStore.Load();
			if (s.StartBgMode == "pattern" && !string.IsNullOrEmpty(s.StartBgColor))
			{
				Color bc = (Color)ColorConverter.ConvertFromString(s.StartBgColor);
				return Boost(bc.R, bc.G, bc.B);
			}
			BitmapSource src = LoadStartBg();
			if (src != null)
			{
				return Dominant(src);
			}
		}
		catch
		{
		}
		return System.Windows.Media.Color.FromRgb(106, 44, 145);
	}

	private static BitmapSource? LoadStartBg()
	{
		AppSettings s = SettingsStore.Load();
		string startBgMode = s.StartBgMode;
		if (1 == 0)
		{
		}
		string text;
		if (!(startBgMode == "custom"))
		{
			if (startBgMode == "desktop")
			{
				string wp = TaskbarTheme.WallpaperPath();
				if (wp != null)
				{
					text = wp;
					goto IL_005f;
				}
			}
		}
		else if (File.Exists(s.StartBgCustomPath))
		{
			text = s.StartBgCustomPath;
			goto IL_005f;
		}
		text = null;
		goto IL_005f;
		IL_005f:
		if (1 == 0)
		{
		}
		string path = text;
		BitmapImage bmp = new BitmapImage();
		bmp.BeginInit();
		bmp.UriSource = ((path != null) ? new Uri(path) : new Uri("pack://application:,,,/Assets/start-bg.png"));
		bmp.CacheOption = BitmapCacheOption.OnLoad;
		bmp.DecodePixelWidth = 32;
		bmp.EndInit();
		((Freezable)bmp).Freeze();
		return bmp;
	}

	private static Color Dominant(BitmapSource src)
	{
		FormatConvertedBitmap conv = new FormatConvertedBitmap(src, PixelFormats.Bgra32, null, 0.0);
		int w = conv.PixelWidth;
		int h = conv.PixelHeight;
		int stride = w * 4;
		byte[] px = new byte[h * stride];
		conv.CopyPixels(px, stride, 0);
		long r = 0L;
		long g = 0L;
		long b = 0L;
		int n = 0;
		for (int i = 0; i < px.Length; i += 4)
		{
			if (px[i + 3] >= 128)
			{
				b += px[i];
				g += px[i + 1];
				r += px[i + 2];
				n++;
			}
		}
		if (n == 0)
		{
			return System.Windows.Media.Color.FromRgb(106, 44, 145);
		}
		byte ar = (byte)(r / n);
		byte ag = (byte)(g / n);
		byte ab = (byte)(b / n);
		return Boost(ar, ag, ab);
	}

	private static Color Boost(byte r, byte g, byte b)
	{
		double R = (double)(int)r / 255.0;
		double G = (double)(int)g / 255.0;
		double B = (double)(int)b / 255.0;
		double max = Math.Max(R, Math.Max(G, B));
		double min = Math.Min(R, Math.Min(G, B));
		double l = (max + min) / 2.0;
		double d = max - min;
		double hue;
		double s;
		if (d == 0.0)
		{
			hue = 0.0;
			s = 0.0;
		}
		else
		{
			s = ((l > 0.5) ? (d / (2.0 - max - min)) : (d / (max + min)));
			hue = ((max == R) ? ((G - B) / d + (double)((G < B) ? 6 : 0)) : ((max != G) ? ((R - G) / d + 4.0) : ((B - R) / d + 2.0)));
			hue /= 6.0;
		}
		s = Math.Min(1.0, s * 1.4 + 0.08);
		l = Math.Clamp(l, 0.32, 0.5);
		return FromHsl(hue, s, l);
	}

	private static Color FromHsl(double h, double s, double l)
	{
		double r;
		double g;
		double b;
		if (s == 0.0)
		{
			r = (g = (b = l));
		}
		else
		{
			double q = ((l < 0.5) ? (l * (1.0 + s)) : (l + s - l * s));
			double p = 2.0 * l - q;
			r = Hue(p, q, h + 1.0 / 3.0);
			g = Hue(p, q, h);
			b = Hue(p, q, h - 1.0 / 3.0);
		}
		return System.Windows.Media.Color.FromRgb((byte)(r * 255.0), (byte)(g * 255.0), (byte)(b * 255.0));
	}

	private static double Hue(double p, double q, double t)
	{
		if (t < 0.0)
		{
			t++;
		}
		if (t > 1.0)
		{
			t--;
		}
		if (t < 1.0 / 6.0)
		{
			return p + (q - p) * 6.0 * t;
		}
		if (t < 0.5)
		{
			return q;
		}
		if (t < 2.0 / 3.0)
		{
			return p + (q - p) * (2.0 / 3.0 - t) * 6.0;
		}
		return p;
	}
}
