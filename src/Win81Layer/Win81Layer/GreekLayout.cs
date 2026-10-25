using System.Collections.Generic;
using System.Text;

namespace Win81Layer;

// Greek↔English keyboard-layout recovery for search (spec §8). If the user types an English name while
// the Greek layout is active, e.g. "chrome" comes out as "ψηρομε"; ToLatinKeys maps each Greek letter to
// the English (QWERTY) letter on the SAME physical key of the standard Greek layout, recovering "chrome".
// Used ADDITIVELY (an extra search term) — the original query is always kept too, so it never silently
// transforms a genuine Greek query (spec §8: preserve original + only add when confident).
internal static class GreekLayout
{
    private static readonly Dictionary<char, char> ToLatin = new Dictionary<char, char>
    {
        { 'α', 'a' }, { 'β', 'b' }, { 'γ', 'g' }, { 'δ', 'd' }, { 'ε', 'e' }, { 'ζ', 'z' }, { 'η', 'h' },
        { 'θ', 'u' }, { 'ι', 'i' }, { 'κ', 'k' }, { 'λ', 'l' }, { 'μ', 'm' }, { 'ν', 'n' }, { 'ξ', 'j' },
        { 'ο', 'o' }, { 'π', 'p' }, { 'ρ', 'r' }, { 'σ', 's' }, { 'ς', 'w' }, { 'τ', 't' }, { 'υ', 'y' },
        { 'φ', 'f' }, { 'χ', 'x' }, { 'ψ', 'c' }, { 'ω', 'v' }
    };

    private static readonly Dictionary<char, char> Deaccent = new Dictionary<char, char>
    {
        { 'ά', 'α' }, { 'έ', 'ε' }, { 'ή', 'η' }, { 'ί', 'ι' }, { 'ό', 'ο' }, { 'ύ', 'υ' }, { 'ώ', 'ω' },
        { 'ϊ', 'ι' }, { 'ϋ', 'υ' }, { 'ΐ', 'ι' }, { 'ΰ', 'υ' }
    };

    // Returns the physical-key Latin transcription, or "" if the input had no Greek letters.
    public static string ToLatinKeys(string s)
    {
        if (string.IsNullOrEmpty(s))
        {
            return "";
        }
        bool hadGreek = false;
        StringBuilder sb = new StringBuilder(s.Length);
        foreach (char c0 in s)
        {
            char c = c0;
            if (Deaccent.TryGetValue(c, out char b))
            {
                c = b;
            }
            if (ToLatin.TryGetValue(c, out char en))
            {
                sb.Append(en);
                hadGreek = true;
            }
            else
            {
                sb.Append(c0);
            }
        }
        return hadGreek ? sb.ToString() : "";
    }

    private static Dictionary<char, char> _toGreek;

    // The reverse: the Greek letter on the same physical key as each Latin letter ("pontiki" -> "ποντικι"), for a Greek
    // word typed while the English layout was active. Returns "" if the input had no Latin letters. Used only to look up
    // the search synonym table, never as a search term of its own.
    public static string ToGreekKeys(string s)
    {
        if (string.IsNullOrEmpty(s))
        {
            return "";
        }
        Dictionary<char, char> map = _toGreek ??= BuildToGreek();
        bool hadLatin = false;
        StringBuilder sb = new StringBuilder(s.Length);
        foreach (char c0 in s)
        {
            char c = char.ToLowerInvariant(c0);
            if (map.TryGetValue(c, out char gr))
            {
                sb.Append(gr);
                hadLatin = true;
            }
            else
            {
                sb.Append(c0);
            }
        }
        return hadLatin ? sb.ToString() : "";
    }

    private static Dictionary<char, char> BuildToGreek()
    {
        Dictionary<char, char> map = new Dictionary<char, char>();
        foreach (KeyValuePair<char, char> kv in ToLatin)
        {
            // Final sigma sits on 'w'; a word typed that way folds to sigma anyway.
            map[kv.Value] = kv.Key;
        }
        return map;
    }
}
