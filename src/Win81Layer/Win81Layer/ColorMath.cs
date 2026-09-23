using System;
using System.Windows;
using System.Windows.Media;

namespace Win81Layer;

public static class ColorMath
{
	public static Color Lighten(Color c, double amt)
	{
		return Color.FromRgb((byte)((double)(int)c.R + (double)(255 - c.R) * amt), (byte)((double)(int)c.G + (double)(255 - c.G) * amt), (byte)((double)(int)c.B + (double)(255 - c.B) * amt));
	}

	public static Color Multiply(Color c, double f)
	{
		return Color.FromRgb((byte)((double)(int)c.R * (1.0 - f)), (byte)((double)(int)c.G * (1.0 - f)), (byte)((double)(int)c.B * (1.0 - f)));
	}

	public static double RelLuminance(Color c)
	{
		return 0.2126 * Ch(c.R) + 0.7152 * Ch(c.G) + 0.0722 * Ch(c.B);
		static double Ch(byte v)
		{
			double s = (double)(int)v / 255.0;
			return (s <= 0.03928) ? (s / 12.92) : Math.Pow((s + 0.055) / 1.055, 2.4);
		}
	}

	public static Color ReadableInk(Color bar)
	{
		return (RelLuminance(bar) > 0.4) ? Color.FromRgb(58, 58, 58) : Color.FromRgb(242, 242, 242);
	}

	public static SolidColorBrush Frozen(Color c)
	{
		SolidColorBrush b = new SolidColorBrush(c);
		((Freezable)b).Freeze();
		return b;
	}

	public static void RgbToHsl(double r, double g, double b, out double h, out double s, out double l)
	{
		double max = Math.Max(r, Math.Max(g, b));
		double min = Math.Min(r, Math.Min(g, b));
		l = (max + min) / 2.0;
		h = 0.0;
		s = 0.0;
		double d = max - min;
		if (d > 1E-06)
		{
			s = ((l > 0.5) ? (d / (2.0 - max - min)) : (d / (max + min)));
			if (max == r)
			{
				h = (g - b) / d + (double)((g < b) ? 6 : 0);
			}
			else if (max == g)
			{
				h = (b - r) / d + 2.0;
			}
			else
			{
				h = (r - g) / d + 4.0;
			}
			h /= 6.0;
		}
	}

	public static void HslToRgb(double h, double s, double l, out double r, out double g, out double b)
	{
		if (s < 1E-06)
		{
			r = (g = (b = l));
			return;
		}
		double q = ((l < 0.5) ? (l * (1.0 + s)) : (l + s - l * s));
		double p = 2.0 * l - q;
		r = Hue(p, q, h + 1.0 / 3.0);
		g = Hue(p, q, h);
		b = Hue(p, q, h - 1.0 / 3.0);
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
