using System;

namespace Win81Layer;

// Lexical relevance engine for launcher search. Query is pre-lowercased+trimmed.
// Tiers are FAR apart so a small usage bonus (added by the caller) breaks ties WITHIN a tier
// but can never lift a weaker match across a tier — i.e. exact/prefix always beat fuzzy/semantic
// (spec §5/§45). Fuzzy is a strict, first-letter-anchored, length-gated FALLBACK only when nothing
// deterministic matched (spec §7: shorter query ⇒ stricter). 0 = no match.
internal static class SearchScore
{
    public const int Exact = 1000;
    public const int Prefix = 500;
    public const int WordSeq = 400;
    public const int AcronymExact = 350;
    public const int AcronymPrefix = 300;
    public const int WordStart = 200;
    public const int Substring = 100;

    private static readonly char[] WordSep = new char[6] { ' ', '-', '.', '_', ':', '&' };

    public static int Name(string? name, string query)
    {
        if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(query))
        {
            return 0;
        }
        string n = name.ToLowerInvariant();
        if (n == query)
        {
            return Exact;
        }
        if (n.StartsWith(query, StringComparison.Ordinal))
        {
            return Prefix;
        }
        string[] words = n.Split(WordSep, StringSplitOptions.RemoveEmptyEntries);
        if (WordSequenceMatch(words, query))
        {
            return WordSeq;
        }
        int acr = AcronymScore(words, query);
        if (acr > 0)
        {
            return acr;
        }
        foreach (string w in words)
        {
            if (w.StartsWith(query, StringComparison.Ordinal))
            {
                return WordStart;
            }
        }
        if (n.Contains(query, StringComparison.Ordinal))
        {
            return Substring;
        }
        return FuzzyScore(n, words, query);
    }

    // "dev man" -> "device manager": each space-separated query word is a prefix of the
    // corresponding name word, in order, starting at some name word.
    private static bool WordSequenceMatch(string[] words, string query)
    {
        int sp = query.IndexOf(' ');
        if (sp < 0)
        {
            return false;   // single-word queries are handled by prefix/word-start
        }
        string[] qw = query.Split(WordSep, StringSplitOptions.RemoveEmptyEntries);
        if (qw.Length < 2 || qw.Length > words.Length)
        {
            return false;
        }
        for (int start = 0; start + qw.Length <= words.Length; start++)
        {
            bool ok = true;
            for (int k = 0; k < qw.Length; k++)
            {
                if (!words[start + k].StartsWith(qw[k], StringComparison.Ordinal))
                {
                    ok = false;
                    break;
                }
            }
            if (ok)
            {
                return true;
            }
        }
        return false;
    }

    // "tm" -> Task Manager, "vsc" -> Visual Studio Code. Only for multi-word names + query length >= 2.
    private static int AcronymScore(string[] words, string query)
    {
        if (query.Length < 2 || words.Length < 2 || query.IndexOf(' ') >= 0)
        {
            return 0;
        }
        Span<char> acr = (words.Length <= 16) ? stackalloc char[words.Length] : new char[words.Length];
        int len = 0;
        foreach (string w in words)
        {
            if (w.Length > 0)
            {
                acr[len++] = w[0];
            }
        }
        if (len < 2)
        {
            return 0;
        }
        ReadOnlySpan<char> a = acr.Slice(0, len);
        if (a.SequenceEqual(query))
        {
            return AcronymExact;
        }
        if (query.Length >= 2 && query.Length < len && a.StartsWith(query))
        {
            return AcronymPrefix;
        }
        return 0;
    }

    // Strict typo fallback: first letter must match, lengths must be close, bounded Damerau distance.
    private static int FuzzyScore(string name, string[] words, string query)
    {
        if (query.Length < 4)
        {
            return 0;   // too short to fuzz safely
        }
        int maxDist = (query.Length <= 6) ? 1 : 2;
        int best = int.MaxValue;
        if (name.Length > 0 && name[0] == query[0] && Math.Abs(name.Length - query.Length) <= maxDist)
        {
            best = Math.Min(best, Damerau(name, query, maxDist));
        }
        foreach (string w in words)
        {
            if (w.Length >= 3 && w[0] == query[0] && Math.Abs(w.Length - query.Length) <= maxDist)
            {
                int d = Damerau(w, query, maxDist);
                if (d < best)
                {
                    best = d;
                }
            }
        }
        if (best > maxDist)
        {
            return 0;
        }
        return (best <= 1) ? 40 : 18;   // dist-2 kept >20 below dist-1 so UsageBonus can't reorder them; both << Substring(100)
    }

    // Optimal string alignment (Damerau-Levenshtein w/ adjacent transpositions), bounded: returns
    // maxDist+1 as soon as the whole row exceeds maxDist (cheap early-out).
    public static int Damerau(string a, string b, int maxDist)
    {
        int la = a.Length;
        int lb = b.Length;
        if (Math.Abs(la - lb) > maxDist)
        {
            return maxDist + 1;
        }
        int[] prev2 = new int[lb + 1];
        int[] prev = new int[lb + 1];
        int[] cur = new int[lb + 1];
        for (int j = 0; j <= lb; j++)
        {
            prev[j] = j;
        }
        for (int i = 1; i <= la; i++)
        {
            cur[0] = i;
            int rowMin = cur[0];
            for (int j = 1; j <= lb; j++)
            {
                int cost = (a[i - 1] == b[j - 1]) ? 0 : 1;
                int v = Math.Min(Math.Min(prev[j] + 1, cur[j - 1] + 1), prev[j - 1] + cost);
                if (i > 1 && j > 1 && a[i - 1] == b[j - 2] && a[i - 2] == b[j - 1])
                {
                    v = Math.Min(v, prev2[j - 2] + 1);
                }
                cur[j] = v;
                if (v < rowMin)
                {
                    rowMin = v;
                }
            }
            if (rowMin > maxDist)
            {
                return maxDist + 1;
            }
            int[] t = prev2;
            prev2 = prev;
            prev = cur;
            cur = t;
        }
        return prev[lb];
    }

    // Small, tie-level personalization bonus (spec §20: nudges ambiguous rankings, never overrides
    // clear intent). Bounded to +20 (freq 15 + rec 5). INVARIANT: this cap MUST stay strictly below
    // the smallest gap between deterministic tiers, which is 50 (WordSeq 400 → AcronymExact 350 →
    // AcronymPrefix 300). +20 < 50, so a usage bonus can only break ties within a tier, never cross one.
    public static int UsageBonus(string launchPath)
    {
        try
        {
            int count = UsageStore.Count(launchPath);
            int freq = (count <= 0) ? 0 : Math.Min(15, (int)Math.Round(5.0 * Math.Log(count + 1.0)));
            int rec = 0;
            long last = UsageStore.LastUsed(launchPath);
            if (last > 0)
            {
                double days = (DateTime.UtcNow.Ticks - last) / (double)TimeSpan.FromDays(1L).Ticks;
                rec = (days < 1.0) ? 5 : ((days < 7.0) ? 3 : ((days < 30.0) ? 1 : 0));
            }
            return freq + rec;
        }
        catch
        {
            return 0;
        }
    }
}
