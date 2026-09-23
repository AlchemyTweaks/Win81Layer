using System;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace Win81Layer;

// Instant OFFLINE number-base converter card: "255 to hex", "0xFF to dec", "1010 to binary", "0b1010 to dec",
// "hex ff to decimal". Mirrors CalcEngine/UnitConvert/DateEngine (synchronous, no network). Source base = an explicit
// leading base word, or a 0x/0b/0o prefix, else decimal. Returns null unless it parses to a valid base conversion.
internal static class BaseEngine
{
	private static readonly Regex Rx = new Regex(
		"^\\s*(?:(hex|hexadecimal|bin|binary|oct|octal|dec|decimal)\\s+)?(0x[0-9a-fA-F]+|0b[01]+|0o[0-7]+|[0-9a-fA-F]+)\\s+(?:to|in|into|as|->|=)\\s+(hex|hexadecimal|bin|binary|oct|octal|dec|decimal)\\s*$",
		RegexOptions.IgnoreCase | RegexOptions.Compiled);

	public static EntityCard? TryBuild(string input)
	{
		try
		{
			Match m = Rx.Match((input ?? "").Trim());
			if (!m.Success)
			{
				return null;
			}
			string srcWord = m.Groups[1].Value.ToLowerInvariant();
			string token = m.Groups[2].Value.Trim();
			int tgt = BaseOf(m.Groups[3].Value);

			int srcBase;
			string digits = token;
			if (token.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) { srcBase = 16; digits = token.Substring(2); }
			else if (token.StartsWith("0b", StringComparison.OrdinalIgnoreCase)) { srcBase = 2; digits = token.Substring(2); }
			else if (token.StartsWith("0o", StringComparison.OrdinalIgnoreCase)) { srcBase = 8; digits = token.Substring(2); }
			else if (srcWord.Length > 0) { srcBase = BaseOf(srcWord); }
			else { srcBase = 10; }

			if (digits.Length == 0)
			{
				return null;
			}
			long val;
			try
			{
				val = Convert.ToInt64(digits, srcBase);   // supports base 2/8/10/16; throws on invalid digits / overflow
			}
			catch
			{
				return null;
			}

			string result = Render(val, tgt);
			string expr = Render(val, srcBase) + " to " + Word(tgt);
			return new EntityCard(IntentKind.Convert, result, expr, null, Array.Empty<Fact>(), new LauncherAction[1]
			{
				new LauncherAction("copy", "Copy result", "", delegate
				{
					try { System.Windows.Clipboard.SetText(result); } catch { }
					return Task.CompletedTask;
				})
			});
		}
		catch
		{
			return null;
		}
	}

	private static int BaseOf(string w)
	{
		w = w.ToLowerInvariant();
		if (w.StartsWith("hex")) return 16;
		if (w.StartsWith("bin")) return 2;
		if (w.StartsWith("oct")) return 8;
		return 10;
	}

	private static string Word(int b)
	{
		return b switch { 16 => "hex", 2 => "binary", 8 => "octal", _ => "decimal" };
	}

	private static string Render(long v, int b)
	{
		return b switch
		{
			16 => "0x" + Convert.ToString(v, 16).ToUpperInvariant(),
			2 => "0b" + Convert.ToString(v, 2),
			8 => "0o" + Convert.ToString(v, 8),
			_ => v.ToString(CultureInfo.InvariantCulture),
		};
	}
}
