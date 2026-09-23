using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;

namespace Win81Layer;

public static class TaskbarTheme
{
	public static readonly System.Windows.Media.Color Fallback = System.Windows.Media.Color.FromRgb(28, 28, 32);

	private static System.Windows.Media.Color? _cached;

	private static (double r, double g, double b, bool ok)? _avg;

	private const uint SPI_GETDESKWALLPAPER = 115u;

	public static System.Windows.Media.Color Current
	{
		get
		{
			System.Windows.Media.Color valueOrDefault = _cached.GetValueOrDefault();
			System.Windows.Media.Color result;
			if (!_cached.HasValue)
			{
				valueOrDefault = FromWallpaper();
				_cached = valueOrDefault;
				result = valueOrDefault;
			}
			else
			{
				result = valueOrDefault;
			}
			return result;
		}
	}

	public static void Invalidate()
	{
		_cached = null;
		_avg = null;
	}

	public static System.Windows.Media.Brush CurrentBackground()
	{
		try
		{
			AppSettings s = SettingsStore.Load();
			bool transparent = s.TaskbarTransparent || s.TaskbarColorMode == "Transparent";
			System.Windows.Media.Color baseColor = ((s.TaskbarColorMode == "Start") ? Darken(StartAccent.Color()) : Current);
			// 8.1 taskbar is translucent by default (wallpaper tint reads through). Keep the explicit Transparent
			// toggle's existing strength (140); only the previously-opaque default becomes translucent (205).
			byte a = (byte)(transparent ? 140 : 205);
			SolidColorBrush b = new SolidColorBrush(System.Windows.Media.Color.FromArgb(a, baseColor.R, baseColor.G, baseColor.B));
			((Freezable)b).Freeze();
			return b;
		}
		catch
		{
			SolidColorBrush b2 = new SolidColorBrush(Fallback);
			((Freezable)b2).Freeze();
			return b2;
		}
	}

	public static System.Windows.Media.Color ChromeColor()
	{
		try
		{
			AppSettings s = SettingsStore.Load();
			return (s.TaskbarColorMode == "Start") ? Darken(StartAccent.Color()) : Current;
		}
		catch
		{
			return Fallback;
		}
	}

	public static System.Windows.Media.Color SourceAccent()
	{
		try
		{
			return (SettingsStore.Load().TaskbarColorMode == "Start") ? StartAccent.Color() : WallpaperAccent();
		}
		catch
		{
			return System.Windows.Media.Color.FromRgb(58, 110, 165);
		}
	}

	public static System.Windows.Media.Color SelectionColor()
	{
		System.Windows.Media.Color c = SourceAccent();
		RgbToHsl((double)(int)c.R / 255.0, (double)(int)c.G / 255.0, (double)(int)c.B / 255.0, out var h, out var sat, out var _);
		HslToRgb(h, Math.Clamp(sat * 0.9, 0.0, 1.0), 0.4, out var r, out var g, out var b);
		return System.Windows.Media.Color.FromRgb((byte)(r * 255.0), (byte)(g * 255.0), (byte)(b * 255.0));
	}

	public static System.Windows.Media.Color LinkColor()
	{
		System.Windows.Media.Color c = SourceAccent();
		RgbToHsl((double)(int)c.R / 255.0, (double)(int)c.G / 255.0, (double)(int)c.B / 255.0, out var h, out var sat, out var _);
		HslToRgb(h, Math.Clamp(sat * 0.85, 0.0, 1.0), 0.72, out var r, out var g, out var b);
		return System.Windows.Media.Color.FromRgb((byte)(r * 255.0), (byte)(g * 255.0), (byte)(b * 255.0));
	}

	private static System.Windows.Media.Color WallpaperAccent()
	{
		(double r, double g, double b, bool ok) tuple = WallpaperAvg();
		var (ar, ag, ab, _) = tuple;
		if (!tuple.ok)
		{
			return System.Windows.Media.Color.FromRgb(58, 110, 165);
		}
		RgbToHsl(ar / 255.0, ag / 255.0, ab / 255.0, out var h, out var s, out var l);
		s = Math.Clamp(s * 1.3, 0.0, 1.0);
		l = Math.Clamp(l, 0.4, 0.5);
		HslToRgb(h, s, l, out var or, out var og, out var ob);
		return System.Windows.Media.Color.FromRgb((byte)(or * 255.0), (byte)(og * 255.0), (byte)(ob * 255.0));
	}

	private static System.Windows.Media.Color Darken(System.Windows.Media.Color accent)
	{
		RgbToHsl((double)(int)accent.R / 255.0, (double)(int)accent.G / 255.0, (double)(int)accent.B / 255.0, out var h, out var s, out var _);
		s = Math.Clamp(s * 1.1, 0.0, 1.0);
		HslToRgb(h, s, 0.13, out var r, out var g, out var b);
		return System.Windows.Media.Color.FromRgb((byte)(r * 255.0), (byte)(g * 255.0), (byte)(b * 255.0));
	}

	[DllImport("user32.dll", CharSet = CharSet.Unicode)]
	private static extern bool SystemParametersInfo(uint action, uint uParam, StringBuilder lpvParam, uint fWinIni);

	public static string? WallpaperPath()
	{
		StringBuilder sb = new StringBuilder(1024);
		if (SystemParametersInfo(115u, (uint)sb.Capacity, sb, 0u) && sb.Length > 0)
		{
			string p = sb.ToString();
			if (File.Exists(p))
			{
				return p;
			}
		}
		try
		{
			using RegistryKey k = Registry.CurrentUser.OpenSubKey("Control Panel\\Desktop");
			if (k?.GetValue("WallPaper") is string wp && File.Exists(wp))
			{
				return wp;
			}
		}
		catch
		{
		}
		return null;
	}

	public static System.Windows.Media.Color FromWallpaper()
	{
		(double r, double g, double b, bool ok) tuple = WallpaperAvg();
		var (r, gg, b, _) = tuple;
		if (!tuple.ok)
		{
			return Fallback;
		}
		RgbToHsl(r / 255.0, gg / 255.0, b / 255.0, out var h, out var s, out var l);
		s = Math.Clamp(s * 1.35, 0.0, 1.0);
		l = 0.11 + l * 0.06;
		HslToRgb(h, s, l, out var or, out var og, out var ob);
		return System.Windows.Media.Color.FromRgb((byte)(or * 255.0), (byte)(og * 255.0), (byte)(ob * 255.0));
	}

	private static (double r, double g, double b, bool ok) WallpaperAvg()
	{
		(double, double, double, bool)? avg = _avg;
		if (avg.HasValue)
		{
			(double, double, double, bool) cached = avg.GetValueOrDefault();
			if (true)
			{
				return cached;
			}
		}
		try
		{
			string path = WallpaperPath();
			if (path == null)
			{
				avg = (_avg = (0.0, 0.0, 0.0, false));
				return avg.Value;
			}
			using Bitmap src = (Bitmap)Image.FromFile(path);
			using Bitmap small = new Bitmap(24, 24);
			using (Graphics g = Graphics.FromImage(small))
			{
				g.InterpolationMode = InterpolationMode.HighQualityBicubic;
				g.DrawImage(src, 0, 0, 24, 24);
			}
			double r = 0.0;
			double gg = 0.0;
			double b = 0.0;
			int n = 0;
			for (int y = 0; y < 24; y++)
			{
				for (int x = 0; x < 24; x++)
				{
					System.Drawing.Color p = small.GetPixel(x, y);
					r += (double)(int)p.R;
					gg += (double)(int)p.G;
					b += (double)(int)p.B;
					n++;
				}
			}
			if (n == 0)
			{
				avg = (_avg = (0.0, 0.0, 0.0, false));
				return avg.Value;
			}
			avg = (_avg = (r / (double)n, gg / (double)n, b / (double)n, true));
			return avg.Value;
		}
		catch
		{
			avg = (_avg = (0.0, 0.0, 0.0, false));
			return avg.Value;
		}
	}

	private static void RgbToHsl(double r, double g, double b, out double h, out double s, out double l)
	{
		ColorMath.RgbToHsl(r, g, b, out h, out s, out l);
	}

	private static void HslToRgb(double h, double s, double l, out double r, out double g, out double b)
	{
		ColorMath.HslToRgb(h, s, l, out r, out g, out b);
	}
}
