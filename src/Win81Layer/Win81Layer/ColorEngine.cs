using System;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace Win81Layer;

// Instant OFFLINE color card: "#2672EC", "#abc", "rgb(38,114,236)" -> a swatch + HEX/RGB/HSL (+ Copy HEX). Mirrors the
// other offline cards (synchronous, no network). Returns null unless the query parses to a valid color. WPF-free: it
// only produces (r,g,b) + strings; MetroComposer.BuildColorCard paints the swatch from the returned "#RRGGBB" Name.
internal static class ColorEngine
{
	private static readonly Regex RgbRx = new Regex(
		"^\\s*rgba?\\(\\s*(\\d{1,3})\\s*,\\s*(\\d{1,3})\\s*,\\s*(\\d{1,3})\\s*(?:,\\s*[0-9.]+\\s*)?\\)\\s*$",
		RegexOptions.IgnoreCase | RegexOptions.Compiled);

	private static readonly Regex HexRx = new Regex(
		"^\\s*#([0-9a-fA-F]{3}|[0-9a-fA-F]{6})\\s*$", RegexOptions.Compiled);

	public static EntityCard? TryBuild(string input)
	{
		try
		{
			string q = (input ?? "").Trim();
			if (q.Length < 4)
			{
				return null;
			}
			int r, g, b;
			Match m = RgbRx.Match(q);
			if (m.Success)
			{
				r = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
				g = int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
				b = int.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture);
				if (r > 255 || g > 255 || b > 255)
				{
					return null;
				}
			}
			else
			{
				// hex REQUIRES a leading # (so plain words/numbers like "abc" / "255" never become colors)
				Match h = HexRx.Match(q);
				if (!h.Success)
				{
					return null;
				}
				string hx = h.Groups[1].Value;
				if (hx.Length == 3)
				{
					hx = string.Concat(hx[0], hx[0], hx[1], hx[1], hx[2], hx[2]);
				}
				r = Convert.ToInt32(hx.Substring(0, 2), 16);
				g = Convert.ToInt32(hx.Substring(2, 2), 16);
				b = Convert.ToInt32(hx.Substring(4, 2), 16);
			}

			string hex = string.Format(CultureInfo.InvariantCulture, "#{0:X2}{1:X2}{2:X2}", r, g, b);
			string rgb = string.Format(CultureInfo.InvariantCulture, "rgb({0}, {1}, {2})", r, g, b);
			string hsl = ToHsl(r, g, b);
			return new EntityCard(IntentKind.Convert, hex, "Color", null, new Fact[3]
			{
				new Fact("HEX", hex),
				new Fact("RGB", rgb),
				new Fact("HSL", hsl)
			}, new LauncherAction[1]
			{
				new LauncherAction("copy", "Copy HEX", "", delegate
				{
					try { System.Windows.Clipboard.SetText(hex); } catch { }
					return Task.CompletedTask;
				})
			});
		}
		catch
		{
			return null;
		}
	}

	private static string ToHsl(int r, int g, int b)
	{
		double rr = r / 255.0, gg = g / 255.0, bb = b / 255.0;
		double max = Math.Max(rr, Math.Max(gg, bb));
		double min = Math.Min(rr, Math.Min(gg, bb));
		double l = (max + min) / 2.0;
		double d = max - min;
		double h;
		double s;
		if (d < 1e-9)
		{
			h = 0.0;
			s = 0.0;
		}
		else
		{
			s = (l > 0.5) ? (d / (2.0 - max - min)) : (d / (max + min));
			if (max == rr)
			{
				h = (gg - bb) / d + (gg < bb ? 6.0 : 0.0);
			}
			else if (max == gg)
			{
				h = (bb - rr) / d + 2.0;
			}
			else
			{
				h = (rr - gg) / d + 4.0;
			}
			h /= 6.0;
		}
		return string.Format(CultureInfo.InvariantCulture, "hsl({0}, {1}%, {2}%)",
			(int)Math.Round(h * 360.0), (int)Math.Round(s * 100.0), (int)Math.Round(l * 100.0));
	}
}
