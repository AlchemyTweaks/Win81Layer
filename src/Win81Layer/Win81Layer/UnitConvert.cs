using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace Win81Layer;

// Instant, OFFLINE unit converter for the search box: "<number> <unit> to|in|into <unit>" -> a Convert entity card.
// Deterministic, no network (mirrors CalcEngine). Returns null unless it's a valid SAME-category conversion, so the
// normal result list is used otherwise. Temperature is affine (special-cased); every other category is factor-to-base.
internal static class UnitConvert
{
	private readonly struct U
	{
		public readonly string Cat;      // category — only same-category conversions are allowed
		public readonly double Factor;   // multiplier to the category base unit
		public readonly string Disp;     // display label
		public U(string cat, double factor, string disp) { Cat = cat; Factor = factor; Disp = disp; }
	}

	private static readonly Dictionary<string, U> Units = Build();

	// number, source-unit phrase, connector (->|into|to|in|=), target-unit phrase.
	private static readonly Regex Rx = new Regex(
		"^\\s*(?:convert\\s+)?(-?\\d+(?:\\.\\d+)?)\\s*([a-zA-Z0-9°/ ]+?)\\s+(?:->|into|to|in|=)\\s+([a-zA-Z0-9°/ ]+?)\\s*$",
		RegexOptions.IgnoreCase | RegexOptions.Compiled);

	private static readonly HashSet<string> TempC = new(StringComparer.OrdinalIgnoreCase) { "c", "°c", "celsius", "centigrade" };
	private static readonly HashSet<string> TempF = new(StringComparer.OrdinalIgnoreCase) { "f", "°f", "fahrenheit" };
	private static readonly HashSet<string> TempK = new(StringComparer.OrdinalIgnoreCase) { "k", "kelvin" };

	private static Dictionary<string, U> Build()
	{
		Dictionary<string, U> d = new(StringComparer.OrdinalIgnoreCase);
		void Add(string cat, double f, string disp, params string[] names)
		{
			foreach (string n in names) { d[n] = new U(cat, f, disp); }
		}

		// length (base = metre)
		Add("len", 0.001, "mm", "mm", "millimeter", "millimetre", "millimeters", "millimetres");
		Add("len", 0.01, "cm", "cm", "centimeter", "centimetre", "centimeters", "centimetres");
		Add("len", 1.0, "m", "m", "meter", "metre", "meters", "metres");
		Add("len", 1000.0, "km", "km", "kilometer", "kilometre", "kilometers", "kilometres");
		Add("len", 0.0254, "in", "in", "inch", "inches");
		Add("len", 0.3048, "ft", "ft", "foot", "feet");
		Add("len", 0.9144, "yd", "yd", "yard", "yards");
		Add("len", 1609.344, "mi", "mi", "mile", "miles");
		Add("len", 1852.0, "nmi", "nmi", "nauticalmile", "nauticalmiles");

		// mass (base = gram)
		Add("mass", 0.001, "mg", "mg", "milligram", "milligrams");
		Add("mass", 1.0, "g", "g", "gram", "grams");
		Add("mass", 1000.0, "kg", "kg", "kilogram", "kilograms");
		Add("mass", 1000000.0, "t", "t", "tonne", "tonnes");
		Add("mass", 28.349523125, "oz", "oz", "ounce", "ounces");
		Add("mass", 453.59237, "lb", "lb", "lbs", "pound", "pounds");
		Add("mass", 6350.29318, "st", "st", "stone", "stones");

		// volume (base = litre)
		Add("vol", 0.001, "ml", "ml", "milliliter", "millilitre", "milliliters", "millilitres");
		Add("vol", 1.0, "L", "l", "liter", "litre", "liters", "litres");
		Add("vol", 3.785411784, "gal", "gal", "gallon", "gallons");   // US
		Add("vol", 0.473176473, "pt", "pt", "pint", "pints");          // US
		Add("vol", 0.2365882365, "cup", "cup", "cups");                // US
		Add("vol", 0.0295735295625, "fl oz", "floz", "fluidounce", "fluidounces");

		// speed (base = m/s)
		Add("spd", 0.2777777778, "km/h", "kmh", "kph", "km/h", "kmph");
		Add("spd", 0.44704, "mph", "mph");
		Add("spd", 1.0, "m/s", "m/s", "mps");
		Add("spd", 0.5144444444, "kn", "kn", "kt", "knot", "knots");

		// data (base = byte)
		Add("data", 1.0, "B", "byte", "bytes");
		Add("data", 1024.0, "KB", "kb", "kilobyte", "kilobytes");
		Add("data", 1048576.0, "MB", "mb", "megabyte", "megabytes");
		Add("data", 1073741824.0, "GB", "gb", "gigabyte", "gigabytes");
		Add("data", 1099511627776.0, "TB", "tb", "terabyte", "terabytes");

		// time (base = second)
		Add("time", 1.0, "s", "s", "sec", "secs", "second", "seconds");
		Add("time", 60.0, "min", "min", "mins", "minute", "minutes");
		Add("time", 3600.0, "h", "h", "hr", "hrs", "hour", "hours");
		Add("time", 86400.0, "day", "day", "days");
		Add("time", 604800.0, "week", "week", "weeks");

		// area (base = m2)
		Add("area", 1.0, "m2", "m2", "sqm");
		Add("area", 1000000.0, "km2", "km2", "sqkm");
		Add("area", 0.09290304, "ft2", "ft2", "sqft");
		Add("area", 4046.8564224, "acre", "acre", "acres");
		Add("area", 10000.0, "ha", "ha", "hectare", "hectares");
		return d;
	}

	private static string Norm(string s)
	{
		return (s ?? "").Trim().Replace(" ", "").ToLowerInvariant();
	}

	public static EntityCard? TryBuild(string input)
	{
		try
		{
			Match m = Rx.Match(input ?? "");
			if (!m.Success)
			{
				return null;
			}
			if (!double.TryParse(m.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double val))
			{
				return null;
			}
			string su = Norm(m.Groups[2].Value);
			string tu = Norm(m.Groups[3].Value);
			if (su.Length == 0 || tu.Length == 0)
			{
				return null;
			}
			if (TempKind(su, out char sk) && TempKind(tu, out char tk))
			{
				double outT = FromKelvin(ToKelvin(val, sk), tk);
				return Card(val, TempDisp(sk), outT, TempDisp(tk));
			}
			if (Units.TryGetValue(su, out U a) && Units.TryGetValue(tu, out U b) && a.Cat == b.Cat)
			{
				double outv = val * a.Factor / b.Factor;
				return Card(val, a.Disp, outv, b.Disp);
			}
			return null;
		}
		catch
		{
			return null;
		}
	}

	private static bool TempKind(string u, out char k)
	{
		if (TempC.Contains(u)) { k = 'C'; return true; }
		if (TempF.Contains(u)) { k = 'F'; return true; }
		if (TempK.Contains(u)) { k = 'K'; return true; }
		k = '\0';
		return false;
	}

	private static double ToKelvin(double v, char k)
	{
		return k switch
		{
			'C' => v + 273.15,
			'F' => (v - 32.0) * 5.0 / 9.0 + 273.15,
			_ => v,
		};
	}

	private static double FromKelvin(double kv, char k)
	{
		return k switch
		{
			'C' => kv - 273.15,
			'F' => (kv - 273.15) * 9.0 / 5.0 + 32.0,
			_ => kv,
		};
	}

	private static string TempDisp(char k)
	{
		return k switch { 'C' => "°C", 'F' => "°F", _ => "K" };
	}

	private static EntityCard Card(double inv, string inDisp, double outv, string outDisp)
	{
		string result = Format(outv) + " " + outDisp;
		string expr = Format(inv) + " " + inDisp;
		return new EntityCard(IntentKind.Convert, result, expr, null, Array.Empty<Fact>(), new LauncherAction[1]
		{
			new LauncherAction("copy", "Copy result", "", delegate
			{
				try { System.Windows.Clipboard.SetText(Format(outv)); } catch { }
				return Task.CompletedTask;
			})
		});
	}

	private static string Format(double v)
	{
		if (Math.Abs(v) < 1E-12)
		{
			v = 0.0;
		}
		double r = Math.Round(v, 6, MidpointRounding.AwayFromZero);
		return r.ToString("0.######", CultureInfo.InvariantCulture);
	}
}
