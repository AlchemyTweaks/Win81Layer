using System;
using Color = System.Windows.Media.Color;

namespace Win81Layer;

// The Metro tone family of the tray flyouts (clock, volume, network) and the Action Center: one function, so a panel that
// asks for the same seed and the same parameters gets exactly the same colour. These are Metro identity tokens: they
// follow the Start accent and background, never the Windows light/dark mode.
internal static class FlyoutPalette
{
	// The seed's hue at the given HSL lightness, with a saturation floor (a grey seed with a floor of 0.4 or more takes
	// the Metro blue hue instead). rest = Dark rest: no floor, at most 0.20 saturation and dark tones a quarter darker.
	internal static Color Tone(Color seed, double hueShift, double saturationFloor, double lightness, bool rest = false)
	{
		if (rest)
		{
			saturationFloor = 0.0;
			if (lightness < 0.40)
			{
				lightness *= 0.75;
			}
		}
		ColorMath.RgbToHsl(seed.R / 255.0, seed.G / 255.0, seed.B / 255.0, out double h, out double s, out _);
		if (rest)
		{
			s = Math.Min(s, 0.20);
		}
		if (s < 0.06)
		{
			if (saturationFloor >= 0.4)
			{
				h = 0.58;
				s = saturationFloor;
			}
			else
			{
				s = 0.0;
			}
		}
		else
		{
			s = Math.Clamp(Math.Max(s, saturationFloor), 0.0, 0.84);
		}
		h = (h + hueShift) % 1.0;
		if (h < 0.0)
		{
			h += 1.0;
		}
		ColorMath.HslToRgb(h, s, Math.Clamp(lightness, 0.0, 1.0), out double r, out double g, out double b);
		return Color.FromRgb(
			(byte)Math.Clamp((int)Math.Round(r * 255.0), 0, 255),
			(byte)Math.Clamp((int)Math.Round(g * 255.0), 0, 255),
			(byte)Math.Clamp((int)Math.Round(b * 255.0), 0, 255));
	}

	// The accent lifted toward white: the slider fill and the active line of the taskbar (TB.Accent).
	internal static Color Brighten(Color c)
	{
		return Color.FromRgb(Up(c.R), Up(c.G), Up(c.B));
		static byte Up(byte v)
		{
			return (byte)Math.Min(255, v + 82 + (255 - v) * 22 / 100);
		}
	}

	// WCAG contrast ratio of two opaque colours.
	internal static double Contrast(Color a, Color b)
	{
		double la = ColorMath.RelLuminance(a);
		double lb = ColorMath.RelLuminance(b);
		return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
	}
}
