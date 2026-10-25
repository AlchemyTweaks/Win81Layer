using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

#nullable enable annotations

namespace Win81Layer;

// Text normalisation shared by the Windows 7 Start menu search (system index, synonyms, program alt names).
// Fold: lower case, accents and other combining marks removed (Greek tonos and dialytika included), final sigma folded
// to sigma, hyphens dropped ("Wi-Fi" = "wifi"), a free-standing '&' spelled out ("and", or "και" in Greek text, so
// "date and time" finds "Date & time"), every other separator turned into one space. Words splits a folded string;
// Stem strips common Greek inflection endings so "ποντίκι" / "ποντικιού" and "ήχος" / "ήχου" meet.
internal static class SearchText
{
    private const char FinalSigma = (char)0x03C2;

    private const char Sigma = (char)0x03C3;

    public static string Fold(string? s)
    {
        if (string.IsNullOrEmpty(s))
        {
            return string.Empty;
        }
        string d = s.Normalize(NormalizationForm.FormD);
        StringBuilder sb = new StringBuilder(d.Length);
        bool space = true;
        for (int i = 0; i < d.Length; i++)
        {
            char c0 = d[i];
            UnicodeCategory cat = CharUnicodeInfo.GetUnicodeCategory(c0);
            if (cat == UnicodeCategory.NonSpacingMark || cat == UnicodeCategory.EnclosingMark || cat == UnicodeCategory.Format)
            {
                continue;
            }
            if (c0 == '&' && (i == 0 || char.IsWhiteSpace(d[i - 1])) && (i == d.Length - 1 || char.IsWhiteSpace(d[i + 1])))
            {
                if (!space)
                {
                    sb.Append(' ');
                }
                sb.Append(HasGreek(s) ? "και" : "and");
                space = false;
                continue;
            }
            char c = char.ToLowerInvariant(c0);
            if (c == FinalSigma)
            {
                c = Sigma;
            }
            // Hyphen-minus, hyphen, non-breaking hyphen and soft hyphen join the word parts.
            if (c == '-' || c == (char)0x2010 || c == (char)0x2011 || c == (char)0x00AD)
            {
                continue;
            }
            if (char.IsLetterOrDigit(c))
            {
                sb.Append(c);
                space = false;
            }
            else if (!space)
            {
                sb.Append(' ');
                space = true;
            }
        }
        if (sb.Length > 0 && sb[sb.Length - 1] == ' ')
        {
            sb.Length--;
        }
        return sb.ToString();
    }

    // The extra words a hyphenated name contributes besides its joined form ("Wi-Fi" -> "wi", "fi"), folded.
    public static IEnumerable<string> HyphenParts(string? s)
    {
        if (string.IsNullOrEmpty(s) || s.IndexOf('-') < 0)
        {
            yield break;
        }
        foreach (string part in Fold(s.Replace('-', ' ')).Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            yield return part;
        }
    }

    private static readonly char[] Space = { ' ' };

    public static string[] Words(string folded)
    {
        return string.IsNullOrEmpty(folded) ? Array.Empty<string>() : folded.Split(Space, StringSplitOptions.RemoveEmptyEntries);
    }

    public static bool HasGreek(string? s)
    {
        if (string.IsNullOrEmpty(s))
        {
            return false;
        }
        foreach (char c in s)
        {
            // Greek and Coptic, Greek Extended.
            if ((c >= (char)0x0370 && c <= (char)0x03FF) || (c >= (char)0x1F00 && c <= (char)0x1FFF))
            {
                return true;
            }
        }
        return false;
    }

    public static bool HasLatinLetter(string? s)
    {
        if (string.IsNullOrEmpty(s))
        {
            return false;
        }
        foreach (char c in s)
        {
            if ((c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z'))
            {
                return true;
            }
        }
        return false;
    }

    // Folded Greek endings, longest first. A stem keeps at least three letters.
    private static readonly string[] GreekEndings =
    {
        "ιουσ", "ιου", "ιων", "ουσ", "εισ", "ου", "ων", "οσ", "ησ", "εσ", "ασ", "α", "η", "ι", "ε", "ο", "υ", "σ"
    };

    // A light Greek stemmer for folded words: words of four letters or more lose one inflection ending. Latin words are
    // returned as they are.
    public static string Stem(string folded)
    {
        if (folded.Length < 4 || !HasGreek(folded))
        {
            return folded;
        }
        foreach (string e in GreekEndings)
        {
            if (folded.Length - e.Length >= 3 && folded.EndsWith(e, StringComparison.Ordinal))
            {
                return folded.Substring(0, folded.Length - e.Length);
            }
        }
        return folded;
    }

    // Words a query word is matched with: the word itself and, for Greek words, its stem.
    public static bool WordMatches(string indexWord, string indexStem, string token, string tokenStem, bool allowPrefix)
    {
        if (allowPrefix ? indexWord.StartsWith(token, StringComparison.Ordinal) : string.Equals(indexWord, token, StringComparison.Ordinal))
        {
            return true;
        }
        if (!ReferenceEquals(tokenStem, token) && tokenStem.Length >= 3)
        {
            if (string.Equals(indexStem, tokenStem, StringComparison.Ordinal))
            {
                return true;
            }
            // A Greek word typed with a different ending than the index has ("ρυθμιση" / "ρυθμισεις").
            if (allowPrefix && tokenStem.Length >= 4 && indexWord.StartsWith(tokenStem, StringComparison.Ordinal) && indexWord.Length - tokenStem.Length <= 4)
            {
                return true;
            }
        }
        return false;
    }
}
