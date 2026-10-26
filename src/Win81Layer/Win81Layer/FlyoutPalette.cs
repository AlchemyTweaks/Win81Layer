using System;
using System.Linq;
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

	// The theme every tray-end panel (Action Center, clock, volume) is toned from: the Start accent, the Start background
	// colour when Start shows a pattern (else the accent again), and whether that pattern is a season's (a season keeps its
	// base hue for the whole body). One rule, so the three panels never disagree.
	internal static void ReadTheme(out Color themeBase, out Color accent, out bool seasonal)
	{
		accent = StartAccent.Color();
		themeBase = accent;
		seasonal = false;
		try
		{
			AppSettings settings = SettingsStore.Load();
			if (settings.StartBgMode == "pattern" && !string.IsNullOrWhiteSpace(settings.StartBgColor))
			{
				themeBase = StartBackgroundFactory.Parse(settings.StartBgColor, accent);
				seasonal = !string.IsNullOrEmpty(settings.SeasonalActive) || SeasonalArt.IsSeasonalPattern(settings.StartPattern);
			}
		}
		catch
		{
		}
	}

	// A translucent ink as it reads on an opaque ground (the colour actually drawn).
	internal static Color Flatten(Color ink, Color ground)
	{
		double a = ink.A / 255.0;
		return Color.FromRgb(
			(byte)Math.Round(ink.R * a + ground.R * (1.0 - a)),
			(byte)Math.Round(ink.G * a + ground.G * (1.0 - a)),
			(byte)Math.Round(ink.B * a + ground.B * (1.0 - a)));
	}

	// The selected day's number on a light selection fill (white cannot reach 4.5:1 there).
	internal static readonly Color DarkDayInk = Color.FromArgb(0xE6, 0, 0, 0);

	// The calendar's selected-day fill and the ink of its number. The fill is the accent at HSL lightness 0.50 (saturation
	// floor 0.62), raised by 0.02 (to 0.84 at most) until it stands at 3:1 or more against every body ground (the selected and
	// the today marks are a state, so they must read as one against the body, not only by hue) and its number reads at 4.5:1
	// or more in white or in the dark ink, whichever reads better. The body sits at HSL 0.17 to 0.26, so a fill that stands
	// out from it is too light for white: nearly every accent takes the dark ink.
	internal static (Color Fill, Color Ink) SelectionFill(Color accent, Color whiteInk, params Color[] body)
	{
		Color fill = Tone(accent, 0.0, 0.62, 0.50);
		Color ink = BetterInk(fill, whiteInk);
		for (double l = 0.52; l <= 0.84 + 1e-9 && !Readable(fill, ink); l += 0.02)
		{
			fill = Tone(accent, 0.0, 0.62, l);
			ink = BetterInk(fill, whiteInk);
		}
		return (fill, ink);

		bool Readable(Color f, Color i)
		{
			return Contrast(Flatten(i, f), f) >= 4.5 && body.All(g => Contrast(f, g) >= 3.0);
		}
	}

	// White when it reads at 4.5:1 on the fill, else whichever of white and the dark ink reads better.
	private static Color BetterInk(Color fill, Color whiteInk)
	{
		double white = Contrast(Flatten(whiteInk, fill), fill);
		return white >= 4.5 || white >= Contrast(Flatten(DarkDayInk, fill), fill) ? whiteInk : DarkDayInk;
	}

	// A translucent white ink at the given alpha, raised (8 at a time, to 0xF2 at most) until it reads at 4.5:1 or more on
	// every ground (a season's lighter body takes a brighter secondary ink than a dark one).
	internal static Color ReadableWhite(byte alpha, params Color[] grounds)
	{
		int a = alpha;
		Color ink = Color.FromArgb((byte)a, 255, 255, 255);
		while (a < 0xF2 && MinContrastOn(ink, grounds) < 4.5)
		{
			a = Math.Min(0xF2, a + 8);
			ink = Color.FromArgb((byte)a, 255, 255, 255);
		}
		return ink;
	}

	// The lowest contrast of a (translucent) ink over the given opaque grounds.
	internal static double MinContrastOn(Color ink, params Color[] grounds)
	{
		double min = double.MaxValue;
		foreach (Color g in grounds)
		{
			min = Math.Min(min, Contrast(Flatten(ink, g), g));
		}
		return min;
	}
}
