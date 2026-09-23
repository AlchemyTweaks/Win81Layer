using System;
using System.Collections.Generic;

namespace Win81Layer;

internal sealed class HeuristicIntentRouter : IIntentRouter
{
	private static readonly HashSet<string> Gazetteer = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
	{
		"paris", "london", "berlin", "madrid", "rome", "athens", "thessaloniki", "tokyo", "kyoto", "osaka",
		"beijing", "shanghai", "moscow", "dubai", "cairo", "sydney", "melbourne", "toronto", "vancouver", "chicago",
		"boston", "seattle", "miami", "dallas", "houston", "denver", "atlanta", "phoenix", "las vegas", "san diego",
		"san francisco", "los angeles", "new york", "washington", "amsterdam", "brussels", "vienna", "prague", "budapest", "warsaw",
		"lisbon", "barcelona", "munich", "hamburg", "milan", "venice", "florence", "naples", "zurich", "geneva",
		"oslo", "stockholm", "helsinki", "copenhagen", "dublin", "edinburgh", "istanbul", "singapore", "hong kong", "bangkok",
		"seoul", "mumbai", "delhi", "cape town", "nairobi", "lagos", "rio de janeiro", "mexico city", "buenos aires", "greece",
		"france", "italy", "spain", "germany", "japan", "china", "india", "brazil", "canada", "australia",
		"usa", "uk", "russia", "egypt", "turkey", "mexico", "argentina", "thailand", "vietnam", "portugal",
		"norway", "sweden", "finland", "denmark", "ireland", "netherlands", "belgium", "austria", "switzerland", "poland"
	};

	private static readonly string[] Prefixes = new string[5] { "weather ", "maps ", "map ", "directions to ", "directions " };

	public Intent Classify(Query q)
	{
		if (q.IsEmpty || q.Normalized.Length < 3)
		{
			return new Intent(IntentKind.Unknown, 0.0, null);
		}
		string n = q.Normalized;
		if (LooksLikeMath(n))
		{
			return new Intent(IntentKind.Calc, 0.95, q.Raw);
		}
		string[] prefixes = Prefixes;
		foreach (string p in prefixes)
		{
			if (n.StartsWith(p, StringComparison.Ordinal))
			{
				string hint = q.Raw.Substring(p.Length).Trim();
				if (hint.Length >= 2)
				{
					return new Intent(IntentKind.Place, 0.9, hint);
				}
			}
		}
		if (Gazetteer.Contains(n))
		{
			return new Intent(IntentKind.Place, 0.8, q.Raw);
		}
		if (n.Contains(',') && n.Length >= 4)
		{
			return new Intent(IntentKind.Place, 0.55, q.Raw);
		}
		return new Intent(IntentKind.Unknown, 0.0, null);
	}

	// Cheap gate: has a digit, contains only math chars (allowing the word "of" surrounded by spaces),
	// and has at least one operator. CalcEngine.TryEvaluate is the real validator (returns null → falls through).
	private static bool LooksLikeMath(string n)
	{
		bool hasDigit = false;
		foreach (char c in n)
		{
			if (c >= '0' && c <= '9')
			{
				hasDigit = true;
				break;
			}
		}
		if (!hasDigit)
		{
			return false;
		}
		string s = n.Replace(" of ", " ");   // treat the 'of' keyword as a separator for the char check
		bool hasOp = false;
		foreach (char c in s)
		{
			if (char.IsWhiteSpace(c) || (c >= '0' && c <= '9') || c == '.')
			{
				continue;
			}
			if (c == '+' || c == '-' || c == '*' || c == '/' || c == '^' || c == '%' || c == '(' || c == ')')
			{
				hasOp = true;
				continue;
			}
			return false;   // any stray letter/symbol → not a pure arithmetic expression
		}
		return hasOp;
	}
}
