using System;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace Win81Layer;

// Instant OFFLINE date card: "days until <date>", "days since <date>", "days between <date> and <date>".
// Deterministic, no network (mirrors CalcEngine / UnitConvert). Returns null unless it parses to a valid date query,
// so the normal result list is used otherwise. Accepts ISO / d-M-yyyy / month-name dates + today/tomorrow/yesterday.
internal static class DateEngine
{
	private static readonly Regex Between = new Regex(
		"^\\s*days?\\s+between\\s+(.+?)\\s+and\\s+(.+?)\\s*$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

	private static readonly Regex Rel = new Regex(
		"^\\s*days?\\s+(until|till|to|since|from|after|before)\\s+(.+?)\\s*$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

	private static readonly string[] Formats =
	{
		"yyyy-MM-dd", "yyyy/MM/dd", "dd-MM-yyyy", "dd/MM/yyyy", "d/M/yyyy", "d-M-yyyy",
		"d MMMM yyyy", "MMMM d yyyy", "MMMM d, yyyy", "d MMM yyyy"
	};

	public static EntityCard? TryBuild(string input)
	{
		try
		{
			string q = (input ?? "").Trim();
			if (q.Length < 8)
			{
				return null;
			}

			Match b = Between.Match(q);
			if (b.Success)
			{
				if (!ParseDate(b.Groups[1].Value, out DateTime d1) || !ParseDate(b.Groups[2].Value, out DateTime d2))
				{
					return null;
				}
				long n = (long)Math.Abs((d2.Date - d1.Date).TotalDays);
				return Card(n.ToString(CultureInfo.InvariantCulture), "days between " + Fmt(d1) + " and " + Fmt(d2));
			}

			Match r = Rel.Match(q);
			if (r.Success)
			{
				if (!ParseDate(r.Groups[2].Value, out DateTime d))
				{
					return null;
				}
				string rel = r.Groups[1].Value.ToLowerInvariant();
				long delta = (long)(d.Date - DateTime.Today).TotalDays;   // + = the date is in the future
				bool wantFuture = rel == "until" || rel == "till" || rel == "to" || rel == "before";
				long signed = wantFuture ? delta : -delta;                // "since/from/after X" = days elapsed
				string word = (signed >= 0) ? (wantFuture ? "until" : "since") : (wantFuture ? "since" : "until");
				return Card(Math.Abs(signed).ToString(CultureInfo.InvariantCulture), "days " + word + " " + Fmt(d));
			}
			return null;
		}
		catch
		{
			return null;
		}
	}

	private static bool ParseDate(string s, out DateTime d)
	{
		s = (s ?? "").Trim();
		string sl = s.ToLowerInvariant();
		if (sl == "today") { d = DateTime.Today; return true; }
		if (sl == "tomorrow") { d = DateTime.Today.AddDays(1.0); return true; }
		if (sl == "yesterday") { d = DateTime.Today.AddDays(-1.0); return true; }
		if (DateTime.TryParseExact(s, Formats, CultureInfo.InvariantCulture, DateTimeStyles.None, out d)) return true;
		if (DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.None, out d)) return true;
		if (DateTime.TryParse(s, CultureInfo.CurrentCulture, DateTimeStyles.None, out d)) return true;
		d = default(DateTime);
		return false;
	}

	private static string Fmt(DateTime d)
	{
		return d.ToString("d MMM yyyy", CultureInfo.InvariantCulture);
	}

	private static EntityCard Card(string count, string subtitle)
	{
		return new EntityCard(IntentKind.Date, count, subtitle, null, Array.Empty<Fact>(), new LauncherAction[1]
		{
			new LauncherAction("copy", "Copy", "", delegate
			{
				try { System.Windows.Clipboard.SetText(count); } catch { }
				return Task.CompletedTask;
			})
		});
	}
}
