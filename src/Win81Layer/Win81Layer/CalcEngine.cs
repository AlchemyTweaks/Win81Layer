using System;
using System.Globalization;
using System.Threading.Tasks;

namespace Win81Layer;

// Instant in-search calculator: a safe recursive-descent arithmetic evaluator (NO eval/reflection).
// Supports + - * / ^, parentheses, unary minus, decimals, a postfix % (n% = n/100), and the word
// "of" as multiply (so "15% of 200" = 30). Returns null on any parse error so the search list is used instead.
internal static class CalcEngine
{
    public static EntityCard? TryBuild(string input)
    {
        double? r = TryEvaluate(input);
        if (!r.HasValue || double.IsNaN(r.Value) || double.IsInfinity(r.Value))
        {
            return null;
        }
        string result = Format(r.Value);
        string expr = (input ?? "").Trim();
        return new EntityCard(IntentKind.Calc, result, expr, null, Array.Empty<Fact>(), new LauncherAction[1]
        {
            new LauncherAction("copy", "Copy result", "", delegate
            {
                try
                {
                    System.Windows.Clipboard.SetText(result);
                }
                catch
                {
                }
                return Task.CompletedTask;
            })
        });
    }

    public static double? TryEvaluate(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return null;
        }
        try
        {
            Parser p = new Parser(input);
            double v = p.ParseExpression();
            p.SkipSpaces();
            if (!p.AtEnd || !p.HasOperator)
            {
                return null;   // leftover junk, or a lone number (not a computation)
            }
            return v;
        }
        catch
        {
            return null;
        }
    }

    private static string Format(double v)
    {
        if (Math.Abs(v) < 1E-12)
        {
            v = 0.0;
        }
        double rounded = Math.Round(v, 10, MidpointRounding.AwayFromZero);
        return rounded.ToString("0.##########", CultureInfo.InvariantCulture);
    }

    private sealed class Parser
    {
        private readonly string _s;

        private int _i;

        public bool HasOperator { get; private set; }

        public bool AtEnd => _i >= _s.Length;

        public Parser(string s)
        {
            _s = s;
            _i = 0;
        }

        public void SkipSpaces()
        {
            while (_i < _s.Length && char.IsWhiteSpace(_s[_i]))
            {
                _i++;
            }
        }

        // Expr := Term (('+'|'-') Term)*
        public double ParseExpression()
        {
            double v = ParseTerm();
            while (true)
            {
                if (Match('+'))
                {
                    HasOperator = true;
                    v += ParseTerm();
                }
                else if (Match('-'))
                {
                    HasOperator = true;
                    v -= ParseTerm();
                }
                else
                {
                    break;
                }
            }
            return v;
        }

        // Term := Power (('*'|'/'|'of') Power)*
        private double ParseTerm()
        {
            double v = ParsePower();
            while (true)
            {
                if (Match('*'))
                {
                    HasOperator = true;
                    v *= ParsePower();
                }
                else if (Match('/'))
                {
                    HasOperator = true;
                    v /= ParsePower();
                }
                else if (MatchWord("of"))
                {
                    HasOperator = true;
                    v *= ParsePower();
                }
                else
                {
                    break;
                }
            }
            return v;
        }

        // Power := Unary ('^' Power)?   (right-associative)
        private double ParsePower()
        {
            double b = ParseUnary();
            if (Match('^'))
            {
                HasOperator = true;
                return Math.Pow(b, ParsePower());
            }
            return b;
        }

        // Unary := ('-'|'+')? Postfix   (unary sign does NOT count as a computation)
        private double ParseUnary()
        {
            if (Match('-'))
            {
                return 0.0 - ParseUnary();
            }
            if (Match('+'))
            {
                return ParseUnary();
            }
            return ParsePostfix();
        }

        // Postfix := Primary ('%')*   (n% = n/100)
        private double ParsePostfix()
        {
            double v = ParsePrimary();
            while (Match('%'))
            {
                HasOperator = true;
                v /= 100.0;
            }
            return v;
        }

        // Primary := number | '(' Expr ')'
        private double ParsePrimary()
        {
            if (Match('('))
            {
                double v = ParseExpression();
                if (!Match(')'))
                {
                    throw new FormatException();
                }
                return v;
            }
            return ParseNumber();
        }

        private double ParseNumber()
        {
            SkipSpaces();
            int start = _i;
            bool dot = false;
            while (_i < _s.Length && ((_s[_i] >= '0' && _s[_i] <= '9') || (_s[_i] == '.' && !dot)))
            {
                if (_s[_i] == '.')
                {
                    dot = true;
                }
                _i++;
            }
            if (_i == start)
            {
                throw new FormatException();
            }
            if (!double.TryParse(_s.Substring(start, _i - start), NumberStyles.Float, CultureInfo.InvariantCulture, out var d))
            {
                throw new FormatException();
            }
            return d;
        }

        private bool Match(char c)
        {
            SkipSpaces();
            if (_i < _s.Length && _s[_i] == c)
            {
                _i++;
                return true;
            }
            return false;
        }

        private bool MatchWord(string w)
        {
            SkipSpaces();
            if (_i + w.Length > _s.Length)
            {
                return false;
            }
            if (string.Compare(_s, _i, w, 0, w.Length, StringComparison.OrdinalIgnoreCase) != 0)
            {
                return false;
            }
            int after = _i + w.Length;
            if (after < _s.Length && char.IsLetter(_s[after]))
            {
                return false;   // word-boundary guard: "off" must not match "of"
            }
            _i = after;
            return true;
        }
    }
}
