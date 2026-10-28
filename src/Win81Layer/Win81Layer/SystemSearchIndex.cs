using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Windows.Threading;
using Microsoft.Win32;

#nullable enable annotations

namespace Win81Layer;

internal enum SystemHitKind
{
    SettingsPage,
    Setting,
    Security,
    ControlPanelItem,
    ControlPanelTask,
    Tool
}

internal enum SystemLaunchKind
{
    // Opened through the shell as it is: ms-settings:, windowsdefender:, shell:::{CLSID}, shell: paths.
    Uri,
    // A program (or a document the shell opens with its program: .msc, .cpl) with arguments.
    Command
}

// The Windows 7 Start menu's search groups, in their default order. PcSettings: the launcher's own PC settings rows
// (runtime hits from HubCatalog, never part of this index or its cache), always shown last.
internal enum SearchGroup
{
    Programs,
    Settings,
    ControlPanel,
    PcSettings
}

// One searchable system entry: a Settings page or setting, a Windows Security page, a Control Panel item or task, or a
// system tool. Name is in the interface language; AltNames are the other index languages' names; Keywords hold every
// language's keywords. The launch is described by Launch, Target and Args (Describe() is what QA records).
internal sealed class SystemHit
{
    public string Id { get; set; } = string.Empty;

    public SystemHitKind Kind { get; set; }

    public string Name { get; set; } = string.Empty;

    public string[] AltNames { get; set; } = Array.Empty<string>();

    public string? Detail { get; set; }

    public string[] Keywords { get; set; } = Array.Empty<string>();

    public SystemLaunchKind Launch { get; set; }

    public string Target { get; set; } = string.Empty;

    public string? Args { get; set; }

    // An icon location ("file,index") or a shell parsing name; null: the group's icon (Settings, Windows Security).
    public string? Icon { get; set; }

    // A task Windows marks needsElevation: started with the runas verb.
    public bool Elevate { get; set; }

    // 'Run as administrator' means something for it (a program, not a page or a control.exe applet).
    public bool CanElevate { get; set; }

    [JsonIgnore]
    internal SearchGroup Group => Kind switch
    {
        SystemHitKind.Tool => SearchGroup.Programs,
        SystemHitKind.ControlPanelItem or SystemHitKind.ControlPanelTask => SearchGroup.ControlPanel,
        _ => SearchGroup.Settings
    };

    // Within a match tier: pages and applets before single settings and tasks (always below the 30-point tier gap
    // together with the usage bonus).
    [JsonIgnore]
    internal int Prior => Kind switch
    {
        SystemHitKind.SettingsPage => 6,
        SystemHitKind.ControlPanelItem => 6,
        SystemHitKind.Security => 3,
        // Tools share the Programs group: no edge over a program of the same tier (the program comes first).
        _ => 0
    };

    [JsonIgnore]
    internal int KindOrder => Kind switch
    {
        SystemHitKind.SettingsPage => 0,
        SystemHitKind.ControlPanelItem => 0,
        SystemHitKind.Tool => 1,
        SystemHitKind.Security => 2,
        SystemHitKind.Setting => 3,
        _ => 4
    };

    // Precomputed match data (built when the index is loaded).
    [JsonIgnore]
    internal SearchName[] Names = Array.Empty<SearchName>();

    [JsonIgnore]
    internal string[] KwWords = Array.Empty<string>();

    [JsonIgnore]
    internal string[] KwStems = Array.Empty<string>();

    [JsonIgnore]
    internal string FoldName = string.Empty;

    internal string Describe()
    {
        return string.IsNullOrEmpty(Args) ? Target : Target + " " + Args;
    }
}

// A folded name with its words and their stems; Alt names score one point below the interface-language name.
internal sealed class SearchName
{
    internal SearchName(string raw, bool alt, Func<string, string>? intern = null)
    {
        Fold = SearchText.Fold(raw);
        List<string> words = new List<string>(SearchText.Words(Fold));
        foreach (string part in SearchText.HyphenParts(raw))
        {
            if (!words.Contains(part))
            {
                words.Add(part);
            }
        }
        Words = intern == null ? words.ToArray() : words.Select(intern).ToArray();
        Stems = intern == null ? Words.Select(SearchText.Stem).ToArray() : Words.Select((string w) => intern(SearchText.Stem(w))).ToArray();
        Alt = alt;
    }

    internal string Fold { get; }

    internal string[] Words { get; }

    internal string[] Stems { get; }

    internal bool Alt { get; }
}

// What the disk cache holds (text only: no PIDLs, no images).
internal sealed class SystemSearchData
{
    internal const int CurrentSchema = 4;

    // Why a build is not the full index (the Control Panel enumeration timed out, strings could not be resolved, ...);
    // null when it is. Never stored: an incomplete build is not cached as a good one.
    [JsonIgnore]
    public string? IncompleteReason { get; set; }

    public int Schema { get; set; }

    public string Key { get; set; } = string.Empty;

    public string UiLanguage { get; set; } = string.Empty;

    public string[] Languages { get; set; } = Array.Empty<string>();

    public DateTime BuiltUtc { get; set; }

    public double BuildMs { get; set; }

    public List<SystemHit> Hits { get; set; } = new List<SystemHit>();

    // Folded interface-language name of a Start menu shortcut -> its names in the other index languages.
    public Dictionary<string, string[]> AppAltNames { get; set; } = new Dictionary<string, string[]>();

    public List<string> Notes { get; set; } = new List<string>();
}

// One typed query, folded once per keystroke, with its alternatives: the query itself, the Greek-layout recovery
// (penalty -1) and the synonym rewrites (-3 / -6).
internal sealed class SearchQuery
{
    internal sealed class Alt
    {
        internal Alt(string text, int penalty, bool synonym)
        {
            Text = text;
            Tokens = SearchText.Words(text);
            Stems = Tokens.Select(SearchText.Stem).ToArray();
            Stop = Tokens.Select((string t) => StopWords.Contains(t)).ToArray();
            NonStop = Stop.Count((bool s) => !s);
            if (NonStop == 0)
            {
                // A query of stop words only ('settings') searches for them.
                Stop = new bool[Tokens.Length];
                NonStop = Tokens.Length;
            }
            Penalty = penalty;
            Synonym = synonym;
        }

        internal string Text { get; }

        internal string[] Tokens { get; }

        internal string[] Stems { get; }

        internal bool[] Stop { get; }

        internal int NonStop { get; }

        internal int Penalty { get; }

        internal bool Synonym { get; }
    }

    // Words a multi-word query may leave unmatched (folded).
    internal static readonly HashSet<string> StopWords = new HashSet<string>(StringComparer.Ordinal)
    {
        "settings", "setting", "change", "the", "to", "how", "of", "and", "a", "an", "your", "my", "for", "on", "in", "windows",
        "ρυθμισεισ", "ρυθμιση", "αλλαγη", "να", "το", "τα", "του", "τησ", "των", "και", "για", "σε", "η", "ο", "οι"
    };

    private SearchQuery(string raw, string folded, List<Alt> alts)
    {
        Raw = raw;
        Folded = folded;
        Alts = alts;
        HasGreek = SearchText.HasGreek(folded);
    }

    internal string Raw { get; }

    internal string Folded { get; }

    internal List<Alt> Alts { get; }

    internal bool HasGreek { get; }

    internal const int MaxAlts = 8;

    internal static SearchQuery Build(string raw, bool uiGreek)
    {
        string folded = SearchText.Fold(raw);
        List<Alt> alts = new List<Alt>();
        if (folded.Length == 0)
        {
            return new SearchQuery(raw, folded, alts);
        }
        alts.Add(new Alt(folded, 0, synonym: false));
        string layout = SearchText.Fold(GreekLayout.ToLatinKeys((raw ?? string.Empty).Trim().ToLowerInvariant()));
        if (layout.Length > 0 && !string.Equals(layout, folded, StringComparison.Ordinal))
        {
            alts.Add(new Alt(layout, -1, synonym: false));
        }
        foreach ((string text, int penalty) in SearchSynonyms.Expand(folded, uiGreek))
        {
            if (alts.Count >= MaxAlts)
            {
                break;
            }
            if (!alts.Any((Alt a) => string.Equals(a.Text, text, StringComparison.Ordinal)))
            {
                alts.Add(new Alt(text, penalty, synonym: true));
            }
        }
        return new SearchQuery(raw ?? string.Empty, folded, alts);
    }
}

// The system search index: built once per process off the UI thread (or loaded from the disk cache, which is keyed by
// the OS build, the languages, the source files, the installed Control Panel items, the Start menu names and the
// hardware, and whose launches are checked against Windows' own sources on load), swapped in atomically, and ranked per
// keystroke on the UI thread in a few milliseconds (no allocation-heavy work per entry: names and keywords are folded and
// split at load). An incomplete build is used but never cached as a good one.
internal sealed class SystemSearchIndex
{
    // Match tiers of system entries: SearchScore's name tiers, then the keyword tiers below WordStart. Any two tiers are at
    // least 30 apart, more than the largest extra (whole word 8 + Prior 6 + usage 20, or cohesion 4 instead of Prior).
    internal const int KeywordExact = 160;

    internal const int KeywordPrefix = 130;

    // A prefix or word-start match on a whole word ('power' in 'Power Options', not in 'PowerISO'; 'display' in 'Rearrange
    // multiple displays', not in 'displayed' twice over) scores this much more within its tier. Programs get it too.
    internal const int WholeWordBonus = 8;

    // What an intent (SearchSynonyms.Intents) lifts its entries to: a whole-word prefix match.
    internal const int IntentTier = SearchScore.Prefix + WholeWordBonus;

    // Tier windows (penalties of -1 to -6 and the whole-word bonus included).
    internal const int PrefixFloor = SearchScore.Prefix - 10;

    internal const int NameFloor = SearchScore.WordStart - 10;

    // A keyword word carried by more than this share of the entries says nothing ('change', 'adjust').
    private const double CommonKeywordShare = 0.08;

    // hashNow: the content hash is computed now, before compaction drops the raw keywords (only when a refresh will
    // compare it); an index that keeps its keywords computes it when first asked.
    private SystemSearchIndex(SystemSearchData data, string source, double loadMs, bool compact, bool hashNow = false)
    {
        Data = data;
        Source = source;
        LoadMs = loadMs;
        UiGreek = data.UiLanguage.StartsWith("el", StringComparison.OrdinalIgnoreCase);
        if (hashNow)
        {
            _contentHash = HashOf(data);
        }
        Prepare();
        if (compact)
        {
            // Ranking reads the folded word arrays only; the raw keyword strings were for the cache, already written.
            foreach (SystemHit h in Hits)
            {
                h.Keywords = Array.Empty<string>();
            }
            Compacted = true;
        }
    }

    // The raw keyword strings were dropped after loading (the live menu; QA keeps them for its cache round trip).
    internal bool Compacted { get; }

    // False while the build behind it is incomplete (it is used, retried later, never cached as a good one).
    internal bool Complete => Data.IncompleteReason == null;

    // Every field of every entry, hashed before compaction: a refresh with the same content changes nothing. Computed only
    // when a refresh is due (or on demand while the keywords are still there); 0 for a compacted index nobody hashed.
    private ulong? _contentHash;

    internal ulong ContentHash => _contentHash ??= Compacted ? 0UL : HashOf(Data);

    internal SystemSearchData Data { get; }

    internal List<SystemHit> Hits => Data.Hits;

    // "built", "cache" or "qa".
    internal string Source { get; }

    internal double LoadMs { get; }

    internal bool UiGreek { get; }

    internal int DroppedCommonKeywords { get; private set; }

    // Folded interface-language program name -> alt names, ready to score.
    internal Dictionary<string, SearchName[]> AppAlt { get; } = new Dictionary<string, SearchName[]>(StringComparer.Ordinal);

    private void Prepare()
    {
        // One string instance per distinct word (thousands of entries share their keywords).
        Dictionary<string, string> pool = new Dictionary<string, string>(StringComparer.Ordinal);
        string Intern(string s)
        {
            if (pool.TryGetValue(s, out string? v))
            {
                return v;
            }
            pool[s] = s;
            return s;
        }
        Dictionary<string, int> freq = new Dictionary<string, int>(StringComparer.Ordinal);
        List<HashSet<string>> perHit = new List<HashSet<string>>(Hits.Count);
        foreach (SystemHit h in Hits)
        {
            h.FoldName = SearchText.Fold(h.Name);
            List<SearchName> names = new List<SearchName> { new SearchName(h.Name, alt: false, Intern) };
            foreach (string a in h.AltNames ?? Array.Empty<string>())
            {
                names.Add(new SearchName(a, alt: true, Intern));
            }
            h.Names = names.ToArray();
            HashSet<string> words = new HashSet<string>(StringComparer.Ordinal);
            foreach (string k in h.Keywords ?? Array.Empty<string>())
            {
                foreach (string w in SearchText.Words(SearchText.Fold(k)))
                {
                    words.Add(Intern(w));
                }
            }
            perHit.Add(words);
            foreach (string w in words)
            {
                freq[w] = freq.TryGetValue(w, out int c) ? c + 1 : 1;
            }
        }
        int limit = Math.Max(12, (int)(Hits.Count * CommonKeywordShare));
        HashSet<string> common = new HashSet<string>(freq.Where((KeyValuePair<string, int> kv) => kv.Value > limit).Select((KeyValuePair<string, int> kv) => kv.Key), StringComparer.Ordinal);
        DroppedCommonKeywords = common.Count;
        for (int i = 0; i < Hits.Count; i++)
        {
            string[] kw = perHit[i].Where((string w) => !common.Contains(w) && w.Length >= 2).ToArray();
            Hits[i].KwWords = kw;
            Hits[i].KwStems = kw.Select((string w) => Intern(SearchText.Stem(w))).ToArray();
        }
        foreach (KeyValuePair<string, string[]> kv in Data.AppAltNames ?? new Dictionary<string, string[]>())
        {
            AppAlt[kv.Key] = kv.Value.Select((string n) => new SearchName(n, alt: true)).ToArray();
        }
        Dictionary<string, List<int>> byTarget = new Dictionary<string, List<int>>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < Hits.Count; i++)
        {
            SystemHit h = Hits[i];
            if (h.Kind == SystemHitKind.SettingsPage)
            {
                _pageTargets.Add(h.Target);
            }
            string key = TargetKey(h);
            if (!byTarget.TryGetValue(key, out List<int>? l))
            {
                l = new List<int>();
                byTarget[key] = l;
            }
            l.Add(i);
        }
        _byTarget = byTarget.ToDictionary((KeyValuePair<string, List<int>> kv) => kv.Key, (KeyValuePair<string, List<int>> kv) => kv.Value.ToArray(), StringComparer.OrdinalIgnoreCase);
        BuildVocabulary();
    }

    // The deep links that have a Settings page entry of their own.
    private readonly HashSet<string> _pageTargets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    // Entries by launch: an address as it is, a program by its file name (control.exe, sndvol.exe).
    private Dictionary<string, int[]> _byTarget = new Dictionary<string, int[]>(StringComparer.OrdinalIgnoreCase);

    private static string TargetKey(SystemHit h)
    {
        return h.Launch == SystemLaunchKind.Command ? Path.GetFileName(h.Target) : h.Target;
    }

    private static ulong HashOf(SystemSearchData d)
    {
        ulong hash = 14695981039346656037UL;
        void Add(string? s)
        {
            if (s != null)
            {
                foreach (char c in s)
                {
                    hash = (hash ^ c) * 1099511628211UL;
                }
            }
            hash = (hash ^ 0xFFFF) * 1099511628211UL;
        }
        foreach (SystemHit h in d.Hits)
        {
            Add(h.Id);
            Add(((int)h.Kind).ToString(System.Globalization.CultureInfo.InvariantCulture));
            Add(h.Name);
            foreach (string a in h.AltNames ?? Array.Empty<string>())
            {
                Add(a);
            }
            Add(h.Detail);
            foreach (string k in h.Keywords ?? Array.Empty<string>())
            {
                Add(k);
            }
            Add(((int)h.Launch).ToString(System.Globalization.CultureInfo.InvariantCulture));
            Add(h.Target);
            Add(h.Args);
            Add(h.Icon);
            Add(h.Elevate ? "e" : "-");
            Add(h.CanElevate ? "c" : "-");
        }
        foreach (KeyValuePair<string, string[]> kv in (d.AppAltNames ?? new Dictionary<string, string[]>()).OrderBy((KeyValuePair<string, string[]> x) => x.Key, StringComparer.Ordinal))
        {
            Add(kv.Key);
            foreach (string v in kv.Value)
            {
                Add(v);
            }
        }
        return hash;
    }

    // Every name and keyword word, sorted, with the entries that carry it, and the Greek stems with theirs: a query word
    // finds its candidate entries by a binary search instead of a scan of every entry.
    private string[] _vocab = Array.Empty<string>();

    private int[][] _postings = Array.Empty<int[]>();

    private Dictionary<string, int[]> _stemPostings = new Dictionary<string, int[]>(StringComparer.Ordinal);

    private void BuildVocabulary()
    {
        Dictionary<string, List<int>> map = new Dictionary<string, List<int>>(StringComparer.Ordinal);
        void Add(string w, int i)
        {
            if (!map.TryGetValue(w, out List<int>? l))
            {
                l = new List<int>();
                map[w] = l;
            }
            if (l.Count == 0 || l[l.Count - 1] != i)
            {
                l.Add(i);
            }
        }
        for (int i = 0; i < Hits.Count; i++)
        {
            SystemHit h = Hits[i];
            foreach (SearchName n in h.Names)
            {
                foreach (string w in n.Words)
                {
                    Add(w, i);
                }
            }
            foreach (string w in h.KwWords)
            {
                Add(w, i);
            }
        }
        _vocab = map.Keys.OrderBy((string w) => w, StringComparer.Ordinal).ToArray();
        _postings = _vocab.Select((string w) => map[w].Distinct().ToArray()).ToArray();
        Dictionary<string, HashSet<int>> stems = new Dictionary<string, HashSet<int>>(StringComparer.Ordinal);
        for (int v = 0; v < _vocab.Length; v++)
        {
            string s = SearchText.Stem(_vocab[v]);
            if (ReferenceEquals(s, _vocab[v]) || string.Equals(s, _vocab[v], StringComparison.Ordinal))
            {
                continue;
            }
            if (!stems.TryGetValue(s, out HashSet<int>? set))
            {
                set = new HashSet<int>();
                stems[s] = set;
            }
            set.UnionWith(_postings[v]);
        }
        _stemPostings = stems.ToDictionary((KeyValuePair<string, HashSet<int>> kv) => kv.Key, (KeyValuePair<string, HashSet<int>> kv) => kv.Value.ToArray(), StringComparer.Ordinal);
    }

    private int LowerBound(string prefix)
    {
        int lo = 0;
        int hi = _vocab.Length;
        while (lo < hi)
        {
            int mid = (lo + hi) >> 1;
            if (string.CompareOrdinal(_vocab[mid], prefix) < 0)
            {
                lo = mid + 1;
            }
            else
            {
                hi = mid;
            }
        }
        return lo;
    }

    private void MarkPrefix(string prefix, bool[] mark, List<int> found, int maxExtra = int.MaxValue)
    {
        for (int v = LowerBound(prefix); v < _vocab.Length && _vocab[v].StartsWith(prefix, StringComparison.Ordinal); v++)
        {
            if (_vocab[v].Length - prefix.Length > maxExtra)
            {
                continue;
            }
            foreach (int i in _postings[v])
            {
                if (!mark[i])
                {
                    mark[i] = true;
                    found.Add(i);
                }
            }
        }
    }

    // The entries an alternative can match at all: those with a word that the query's first significant word starts,
    // is the stem of or (Greek) inflects; plus, for longer queries, names that contain the query and names with a word
    // one or two typos away from it.
    private List<int> Candidates(SearchQuery.Alt a, int queryLength, bool[] mark)
    {
        Array.Clear(mark);
        List<int> found = new List<int>();
        int ti = Array.IndexOf(a.Stop, false);
        if (ti < 0)
        {
            ti = 0;
        }
        string t = a.Tokens[ti];
        string ts = a.Stems[ti];
        MarkPrefix(t, mark, found);
        if (!string.Equals(ts, t, StringComparison.Ordinal) && ts.Length >= 3)
        {
            if (_stemPostings.TryGetValue(ts, out int[]? sp))
            {
                foreach (int i in sp)
                {
                    if (!mark[i])
                    {
                        mark[i] = true;
                        found.Add(i);
                    }
                }
            }
            if (ts.Length >= 4)
            {
                MarkPrefix(ts, mark, found, maxExtra: 4);
            }
        }
        bool substring = !a.Synonym && a.Text.Length >= 4;
        bool fuzzy = !a.Synonym && queryLength >= 5 && a.Tokens.Length == 1;
        if (substring || fuzzy)
        {
            int maxDist = a.Text.Length <= 6 ? 1 : 2;
            for (int i = 0; i < Hits.Count; i++)
            {
                if (mark[i])
                {
                    continue;
                }
                foreach (SearchName n in Hits[i].Names)
                {
                    bool hit = substring && n.Fold.Contains(a.Text, StringComparison.Ordinal);
                    if (!hit && fuzzy)
                    {
                        foreach (string w in n.Words)
                        {
                            if (w.Length >= 4 && w[0] == a.Text[0] && Math.Abs(w.Length - a.Text.Length) <= maxDist)
                            {
                                hit = true;
                                break;
                            }
                        }
                    }
                    if (hit)
                    {
                        mark[i] = true;
                        found.Add(i);
                        break;
                    }
                }
            }
        }
        return found;
    }

    // Every entry that matches the query, with its best match tier (penalties and whole-word bonus applied, settings of
    // another topic moved down a tier, intent answers lifted).
    internal List<(SystemHit Hit, int Match)> Rank(SearchQuery q)
    {
        List<(SystemHit Hit, int Match)> result = new List<(SystemHit Hit, int Match)>();
        if (q.Alts.Count == 0 || Hits.Count == 0)
        {
            return result;
        }
        int min = MinTier(q.Folded.Length);
        int[] best = new int[Hits.Count];
        bool[] mark = new bool[Hits.Count];
        List<int> touched = new List<int>();
        foreach (SearchQuery.Alt a in q.Alts)
        {
            if (a.Tokens.Length == 0)
            {
                continue;
            }
            foreach (int i in Candidates(a, q.Folded.Length, mark))
            {
                SystemHit h = Hits[i];
                int tier = Tier(h.Names, h.KwWords, h.KwStems, a, q.Folded.Length);
                if (tier >= min && tier + a.Penalty > best[i])
                {
                    if (best[i] == 0)
                    {
                        touched.Add(i);
                    }
                    best[i] = tier + a.Penalty;
                }
            }
        }
        // A setting that matched by prefix while its own page (an entry of the index) does not match by name is a word of
        // another topic ('Display battery capacity' on the power page for 'display'): it drops to the WordStart tier.
        HashSet<string>? namedPages = null;
        foreach (int i in touched)
        {
            if (Hits[i].Kind == SystemHitKind.SettingsPage && best[i] >= NameFloor)
            {
                (namedPages ??= new HashSet<string>(StringComparer.OrdinalIgnoreCase)).Add(Hits[i].Target);
            }
        }
        foreach (int i in touched)
        {
            SystemHit h = Hits[i];
            if (h.Kind == SystemHitKind.Setting && best[i] >= PrefixFloor && best[i] < SearchScore.Prefix + 30 && _pageTargets.Contains(h.Target) && (namedPages == null || !namedPages.Contains(h.Target)))
            {
                best[i] -= SearchScore.Prefix - SearchScore.WordStart;
            }
        }
        // A query that asks for one well-known page lifts the entries that answer it.
        foreach (SearchSynonyms.Intent intent in SearchSynonyms.Intents)
        {
            if (!intent.Matches(q.Folded))
            {
                continue;
            }
            foreach ((SystemHitKind kind, string target, string? args, bool anyMatch) in intent.Lifts)
            {
                if (!_byTarget.TryGetValue(target, out int[]? idxs))
                {
                    continue;
                }
                foreach (int i in idxs)
                {
                    SystemHit h = Hits[i];
                    if (h.Kind != kind || (args != null && !string.Equals(h.Args, args, StringComparison.OrdinalIgnoreCase)))
                    {
                        continue;
                    }
                    bool add = kind == SystemHitKind.SettingsPage || kind == SystemHitKind.ControlPanelItem;
                    if ((!add && (best[i] == 0 || (!anyMatch && best[i] < NameFloor))) || best[i] >= IntentTier)
                    {
                        continue;
                    }
                    if (best[i] == 0)
                    {
                        touched.Add(i);
                    }
                    best[i] = IntentTier;
                }
            }
        }
        touched.Sort();
        foreach (int i in touched)
        {
            result.Add((Hits[i], best[i]));
        }
        return result;
    }

    // ---- Lifetime ----------------------------------------------------------------------------------------------------

    private static SystemSearchIndex? s_current;

    private static int s_started;

    private static long s_failedTick;

    private static int s_revision;

    // Incomplete builds of this process: retried a minute apart, the third one is kept (and cached for a refresh at the
    // next start), so a machine that hides Control Panel by policy does not rebuild on every open.
    private static int s_incompleteRuns;

    internal const int IncompleteRetries = 3;

    // How old a cache may get before a start refreshes it in the background (the key already covers the OS build, the
    // languages, the source files, the installed Control Panel items, the Start menu names and the hardware).
    internal static readonly TimeSpan RefreshAge = TimeSpan.FromDays(7);

    internal static SystemSearchIndex? Current => Volatile.Read(ref s_current);

    // The first load is running (no index yet): the menu says it is searching rather than that nothing matched.
    internal static bool Loading => Volatile.Read(ref s_current) == null && Volatile.Read(ref s_started) == 1;

    // Bumped on every swap (the menu keys its per-index caches on it).
    internal static int Revision => Volatile.Read(ref s_revision);

    internal static string? LastError { get; private set; }

    internal static string CachePath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Win81Layer", "cache", "win7-search-index.json");

    private static void Swap(SystemSearchIndex index)
    {
        Volatile.Write(ref s_current, index);
        Interlocked.Increment(ref s_revision);
    }

    // Starts the load of the index: the disk cache when its key matches and its entries check out, otherwise a build.
    // synchronous (QA): built on the calling thread, never read from or written to the user's cache. onReady is posted to
    // the dispatcher when a background load (or a refresh that changed something) is done. An incomplete build is used
    // until a later call (a minute on) builds again.
    internal static void Ensure(bool synchronous, Dispatcher? dispatcher = null, Action? onReady = null)
    {
        Interlocked.Exchange(ref s_lastUseMs, Environment.TickCount64);
        SystemSearchIndex? current = Volatile.Read(ref s_current);
        if (current != null && current.Complete)
        {
            return;
        }
        long failed = Interlocked.Read(ref s_failedTick);
        if (failed != 0 && Environment.TickCount64 - failed < 60000)
        {
            return;
        }
        if (Interlocked.CompareExchange(ref s_started, 1, 0) != 0)
        {
            return;
        }
        if (synchronous)
        {
            try
            {
                SystemSearchIndex index = BuildNow("qa", compact: false);
                Swap(index);
                if (!index.Complete)
                {
                    Incomplete(index);
                }
            }
            catch (Exception ex)
            {
                Fail(ex);
            }
            return;
        }
        Interlocked.Exchange(ref s_working, 1);
        Thread t = new Thread(delegate ()
        {
            try
            {
                LoadInBackground(dispatcher, onReady);
            }
            finally
            {
                Interlocked.Exchange(ref s_working, 0);
            }
        })
        {
            IsBackground = true,
            Priority = ThreadPriority.BelowNormal,
            Name = "Win7 Start system index"
        };
        t.Start();
    }

    // A background load (or build) is running, including the refresh of an old cache after it.
    private static int s_working;

    // When the index was last asked for: an open or a keystroke of the Windows 7 menu, an open of PC settings.
    private static long s_lastUseMs;

    internal const long ReleaseAfterMs = 300000L;

    // Nothing asked for the index for 5 minutes and neither the Windows 7 menu nor PC settings is open (inUse): it is given
    // back (a couple of MB of managed memory while the shell idles). The next open loads the disk cache again off the UI
    // thread and the menu fills in place when it arrives. true when it was released.
    internal static bool MaybeRelease(long nowMs, bool inUse)
    {
        if (inUse || Volatile.Read(ref s_working) != 0 || Volatile.Read(ref s_current) == null)
        {
            return false;
        }
        if (nowMs - Interlocked.Read(ref s_lastUseMs) < ReleaseAfterMs)
        {
            return false;
        }
        Volatile.Write(ref s_current, null);
        Interlocked.Increment(ref s_revision);
        Volatile.Write(ref s_started, 0);
        Logger.Log("Win7 Start: system index released after 5 min unused");
        return true;
    }

    private static void LoadInBackground(Dispatcher? dispatcher, Action? onReady)
    {
        bool refresh = false;
        SystemSearchIndex index;
        try
        {
            index = LoadOrBuild(out refresh);
            Swap(index);
            Logger.Log($"Win7 Start: system index {index.Source} ({index.Hits.Count} entries, {index.LoadMs:0} ms){(refresh ? "; refreshing an old cache" : string.Empty)}");
            Post(dispatcher, onReady);
        }
        catch (Exception ex)
        {
            Fail(ex);
            return;
        }
        if (!index.Complete)
        {
            Incomplete(index);
            return;
        }
        if (refresh)
        {
            try
            {
                Stopwatch sw = Stopwatch.StartNew();
                SystemSearchData data = BuildData();
                if (data.IncompleteReason != null)
                {
                    // The cached index stays: an incomplete build never replaces a good one.
                    Logger.Log("Win7 Start: system index refresh incomplete (" + data.IncompleteReason + "); the cached index stays");
                    return;
                }
                SaveCache(data);
                SystemSearchIndex fresh = new SystemSearchIndex(data, "built", sw.Elapsed.TotalMilliseconds, compact: true, hashNow: true);
                if (fresh.ContentHash == index.ContentHash)
                {
                    Logger.Log("Win7 Start: system index refresh found no change");
                    return;
                }
                Swap(fresh);
                Post(dispatcher, onReady);
            }
            catch (Exception ex)
            {
                Logger.Log("Win7 Start: system index refresh failed: " + ex.Message);
            }
        }
    }

    private static void Fail(Exception ex)
    {
        LastError = ex.GetType().Name + ": " + ex.Message;
        Logger.Log("Win7 Start: system index failed: " + LastError);
        Interlocked.Exchange(ref s_failedTick, Environment.TickCount64);
        Volatile.Write(ref s_started, 0);
    }

    // An incomplete index is in use: another build may start a minute from now, up to IncompleteRetries builds.
    private static void Incomplete(SystemSearchIndex index)
    {
        LastError = "incomplete: " + index.Data.IncompleteReason;
        int runs = Interlocked.Increment(ref s_incompleteRuns);
        Logger.Log($"Win7 Start: system index incomplete ({index.Data.IncompleteReason}), build {runs} of {IncompleteRetries}");
        if (runs < IncompleteRetries)
        {
            Interlocked.Exchange(ref s_failedTick, Environment.TickCount64);
            Volatile.Write(ref s_started, 0);
        }
    }

    private static void Post(Dispatcher? dispatcher, Action? onReady)
    {
        if (dispatcher == null || onReady == null)
        {
            return;
        }
        try
        {
            dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, onReady);
        }
        catch
        {
        }
    }

    private static SystemSearchIndex LoadOrBuild(out bool refresh)
    {
        refresh = false;
        (string ui, string[] langs) = SystemSearchBuilder.Languages();
        string key = Key(ui, langs);
        Stopwatch sw = Stopwatch.StartNew();
        SystemSearchData? cached = LoadFrom(CachePath);
        if (cached != null && cached.Schema == SystemSearchData.CurrentSchema && string.Equals(cached.Key, key, StringComparison.Ordinal) && cached.Hits.Count > 0)
        {
            // Any program of the user can rewrite the file: what it would launch is checked against Windows' own sources.
            if (SystemSearchBuilder.VerifyCached(cached, out string why))
            {
                refresh = DateTime.UtcNow - cached.BuiltUtc > RefreshAge;
                return new SystemSearchIndex(cached, "cache", sw.Elapsed.TotalMilliseconds, compact: true, hashNow: refresh);
            }
            Logger.Log("Win7 Start: system index cache rejected (" + why + "); building it again");
        }
        SystemSearchData data = BuildData();
        if (data.IncompleteReason == null)
        {
            SaveCache(data);
        }
        else if (Volatile.Read(ref s_incompleteRuns) + 1 >= IncompleteRetries)
        {
            // The last try of this process is kept: cached as already old, so the next start refreshes it.
            data.BuiltUtc = DateTime.MinValue;
            SaveCache(data);
        }
        return new SystemSearchIndex(data, "built", sw.Elapsed.TotalMilliseconds, compact: true);
    }

    private static string Key(string ui, string[] langs)
    {
        return "v" + SystemSearchData.CurrentSchema + "|" + SystemSearchBuilder.SourceStamp(ui, langs);
    }

    // A fresh build from the Windows sources (never touches the cache).
    internal static SystemSearchData BuildData()
    {
        (string ui, string[] langs) = SystemSearchBuilder.Languages();
        SystemSearchData data = SystemSearchBuilder.Build(ui, langs, workers: 4);
        data.Key = Key(ui, langs);
        return data;
    }

    internal static SystemSearchIndex BuildNow(string source, bool compact)
    {
        Stopwatch sw = Stopwatch.StartNew();
        SystemSearchData data = BuildData();
        return new SystemSearchIndex(data, source, sw.Elapsed.TotalMilliseconds, compact);
    }

    internal static SystemSearchIndex FromData(SystemSearchData data, string source, double ms, bool compact = false)
    {
        return new SystemSearchIndex(data, source, ms, compact);
    }

    // QA: puts an index in place (or clears it) without building.
    internal static void QaSet(SystemSearchIndex? index)
    {
        Volatile.Write(ref s_current, index);
        Interlocked.Increment(ref s_revision);
        Volatile.Write(ref s_started, index != null ? 1 : 0);
    }

    private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions { WriteIndented = false };

    internal static SystemSearchData? LoadFrom(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }
            using FileStream fs = File.OpenRead(path);
            return JsonSerializer.Deserialize<SystemSearchData>(fs, JsonOptions);
        }
        catch
        {
            return null;
        }
    }

    // Written atomically (temp file, then replace). Never while the process runs read-only diagnostics.
    internal static void SaveTo(string path, SystemSearchData data)
    {
        string dir = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(dir);
        string tmp = path + ".tmp";
        using (FileStream fs = File.Create(tmp))
        {
            JsonSerializer.Serialize(fs, data, JsonOptions);
        }
        File.Move(tmp, path, overwrite: true);
    }

    private static void SaveCache(SystemSearchData data)
    {
        if (SettingsStore.ReadOnlyDiagnostics)
        {
            return;
        }
        try
        {
            SaveTo(CachePath, data);
        }
        catch (Exception ex)
        {
            Logger.Log("Win7 Start: system index cache write failed: " + ex.Message);
        }
    }

    // ---- Ranking -----------------------------------------------------------------------------------------------------

    // The lowest tier a query of this length shows: one letter needs a name that starts with it, two a name word that
    // does, three letters also keyword matches, four or more also substrings and typos.
    internal static int MinTier(int foldedLength)
    {
        return foldedLength <= 1 ? SearchScore.Prefix : (foldedLength == 2 ? SearchScore.WordStart : (foldedLength == 3 ? KeywordPrefix : 1));
    }

    // A program's alt names (a Greek query on an English inventory): the best name tier, one point below the name.
    internal static int AltNameMatch(SearchName[] names, SearchQuery q)
    {
        int min = MinTier(q.Folded.Length);
        int best = 0;
        foreach (SearchQuery.Alt a in q.Alts)
        {
            foreach (SearchName n in names)
            {
                int t = NameTier(n, a, q.Folded.Length) - 1;
                if (t >= min && t + a.Penalty > best)
                {
                    best = t + a.Penalty;
                }
            }
        }
        return best;
    }

    private static int Tier(SearchName[] names, string[] kwWords, string[] kwStems, SearchQuery.Alt a, int queryLength)
    {
        int best = 0;
        foreach (SearchName n in names)
        {
            int t = NameTier(n, a, queryLength);
            if (t > 0 && n.Alt)
            {
                t--;
            }
            if (t > best)
            {
                best = t;
            }
        }
        if (best >= SearchScore.WordStart || queryLength < 3)
        {
            return best;
        }
        return Math.Max(best, KeywordTier(names, kwWords, kwStems, a));
    }

    internal static int NameTier(SearchName n, SearchQuery.Alt a, int queryLength)
    {
        string f = n.Fold;
        string q = a.Text;
        if (f.Length == 0 || q.Length == 0)
        {
            return 0;
        }
        if (string.Equals(f, q, StringComparison.Ordinal))
        {
            return SearchScore.Exact;
        }
        // A synonym is a whole word: 'screen' finds 'Screen saver', never 'Screenshots'.
        bool whole = a.Synonym;
        if (f.StartsWith(q, StringComparison.Ordinal) && (!whole || f[q.Length] == ' '))
        {
            return SearchScore.Prefix + (WholeAt(f, q.Length) ? WholeWordBonus : 0);
        }
        if (a.Tokens.Length >= 2 && WordSequence(n.Words, a.Tokens, whole))
        {
            return SearchScore.WordSeq;
        }
        if (AllTokens(n.Words, n.Stems, a, null, null, kwPrefix: false))
        {
            return SearchScore.WordStart + (WholeTokens(n.Words, a) ? WholeWordBonus : 0);
        }
        // Substrings and typos only for what was typed (a synonym is a whole word from the table).
        if (a.Synonym)
        {
            return 0;
        }
        if (q.Length >= 4 && f.Contains(q, StringComparison.Ordinal))
        {
            return SearchScore.Substring;
        }
        if (queryLength >= 5 && a.Tokens.Length == 1)
        {
            return Fuzzy(n.Words, q);
        }
        return 0;
    }

    // The text matched up to end is a whole word there: the text ends or a space follows, or an English plural ending
    // ('printer' in 'printers', 'update' in 'updates'). Not an inflection or a longer word ('PowerISO', 'displayed').
    internal static bool WholeAt(string text, int end)
    {
        if (end >= text.Length || text[end] == ' ')
        {
            return true;
        }
        if (end == 0)
        {
            return false;
        }
        char last = text[end - 1];
        if (last > 'z' || last < 'a')
        {
            return false;
        }
        if (text[end] == 's')
        {
            return end + 1 == text.Length || text[end + 1] == ' ';
        }
        return text[end] == 'e' && end + 1 < text.Length && text[end + 1] == 's' && (end + 2 == text.Length || text[end + 2] == ' ');
    }

    // Every significant word of the query is a whole word of the name.
    private static bool WholeTokens(string[] words, SearchQuery.Alt a)
    {
        bool any = false;
        for (int i = 0; i < a.Tokens.Length; i++)
        {
            if (a.Stop[i])
            {
                continue;
            }
            string t = a.Tokens[i];
            bool found = false;
            foreach (string w in words)
            {
                if (w.StartsWith(t, StringComparison.Ordinal) && WholeAt(w, t.Length))
                {
                    found = true;
                    break;
                }
            }
            if (!found)
            {
                return false;
            }
            any = true;
        }
        return any;
    }

    private static bool WordSequence(string[] words, string[] tokens, bool whole)
    {
        for (int start = 0; start + tokens.Length <= words.Length; start++)
        {
            bool ok = true;
            for (int k = 0; k < tokens.Length && ok; k++)
            {
                ok = whole ? string.Equals(words[start + k], tokens[k], StringComparison.Ordinal) : words[start + k].StartsWith(tokens[k], StringComparison.Ordinal);
            }
            if (ok)
            {
                return true;
            }
        }
        return false;
    }

    // Every word of the query (stop words may be left out) found among the name words, or with kwWords among the name
    // and keyword words (keyword prefixes for words of three letters or more when kwPrefix, exact keywords otherwise).
    private static bool AllTokens(string[] words, string[] stems, SearchQuery.Alt a, string[]? kwWords, string[]? kwStems, bool kwPrefix)
    {
        int matched = 0;
        bool namePrefix = !a.Synonym;
        for (int i = 0; i < a.Tokens.Length; i++)
        {
            string t = a.Tokens[i];
            string ts = a.Stems[i];
            bool found = false;
            for (int j = 0; j < words.Length && !found; j++)
            {
                found = SearchText.WordMatches(words[j], stems[j], t, ts, allowPrefix: namePrefix);
            }
            if (!found && kwWords != null && kwStems != null)
            {
                bool prefix = kwPrefix && namePrefix && t.Length >= 3;
                for (int j = 0; j < kwWords.Length && !found; j++)
                {
                    found = SearchText.WordMatches(kwWords[j], kwStems[j], t, ts, allowPrefix: prefix);
                }
            }
            if (found)
            {
                if (!a.Stop[i])
                {
                    matched++;
                }
            }
            else if (!a.Stop[i])
            {
                return false;
            }
        }
        return matched > 0;
    }

    private static int KeywordTier(SearchName[] names, string[] kwWords, string[] kwStems, SearchQuery.Alt a)
    {
        if (kwWords.Length == 0)
        {
            return 0;
        }
        // Every word exactly among the keywords (or the name): the strongest keyword match.
        bool exact = true;
        int matched = 0;
        for (int i = 0; i < a.Tokens.Length && exact; i++)
        {
            bool found = false;
            for (int j = 0; j < kwWords.Length && !found; j++)
            {
                found = SearchText.WordMatches(kwWords[j], kwStems[j], a.Tokens[i], a.Stems[i], allowPrefix: false);
            }
            if (found)
            {
                if (!a.Stop[i])
                {
                    matched++;
                }
            }
            else if (!a.Stop[i])
            {
                exact = false;
            }
        }
        if (exact && matched > 0)
        {
            return KeywordExact;
        }
        foreach (SearchName n in names)
        {
            if (AllTokens(n.Words, n.Stems, a, kwWords, kwStems, kwPrefix: true))
            {
                return KeywordPrefix;
            }
        }
        return 0;
    }

    private static int Fuzzy(string[] words, string q)
    {
        int maxDist = q.Length <= 6 ? 1 : 2;
        int best = int.MaxValue;
        foreach (string w in words)
        {
            if (w.Length >= 4 && w[0] == q[0] && Math.Abs(w.Length - q.Length) <= maxDist)
            {
                best = Math.Min(best, SearchScore.Damerau(w, q, maxDist));
            }
        }
        return best > maxDist ? 0 : (best <= 1 ? 40 : 18);
    }

    // ---- Targets (checked, never started) -----------------------------------------------------------------------------

    // The canonical names of the registered Control Panel items, read once per build (Build resets them, so a build later
    // in the process sees the items installed since).
    private static HashSet<string>? s_canonNames;

    internal static void ResetCanonicalNames()
    {
        Volatile.Write(ref s_canonNames, null);
    }

    private static HashSet<string> CanonicalNameSet()
    {
        HashSet<string>? set = Volatile.Read(ref s_canonNames);
        if (set == null)
        {
            set = new HashSet<string>(SystemSearchBuilder.CanonicalNames().Values, StringComparer.OrdinalIgnoreCase);
            Volatile.Write(ref s_canonNames, set);
        }
        return set;
    }

    // True when what a hit would start exists: a registered protocol, a parsable shell name, an existing program (and
    // for control.exe /name a registered Control Panel item, for rundll32 the DLL and applet, for mmc the console).
    // Nothing is executed or invoked.
    internal static bool TargetExists(SystemLaunchKind kind, string target, string? args, out string why)
    {
        try
        {
            if (kind == SystemLaunchKind.Uri)
            {
                if (target.StartsWith("shell:", StringComparison.OrdinalIgnoreCase))
                {
                    int hr = SystemSearchBuilder.Native.SHParseDisplayName(target, IntPtr.Zero, out nint pidl, 0, out _);
                    if (pidl != IntPtr.Zero)
                    {
                        SystemSearchBuilder.Native.CoTaskMemFree(pidl);
                    }
                    why = hr == 0 ? "shell name parses" : $"shell name does not parse (0x{hr:X8})";
                    return hr == 0;
                }
                int colon = target.IndexOf(':');
                string scheme = colon > 0 ? target.Substring(0, colon) : target;
                using RegistryKey? k = Registry.ClassesRoot.OpenSubKey(scheme);
                bool ok = k?.GetValue("URL Protocol") != null;
                why = ok ? "protocol " + scheme + " registered" : "protocol " + scheme + " not registered";
                return ok;
            }
            string exe = Environment.ExpandEnvironmentVariables(target);
            if (!File.Exists(exe))
            {
                why = "missing " + exe;
                return false;
            }
            string name = Path.GetFileName(exe).ToLowerInvariant();
            string a = args ?? string.Empty;
            if (name == "control.exe" && a.StartsWith("/name ", StringComparison.OrdinalIgnoreCase))
            {
                string canon = a.Substring(6).Split(' ')[0];
                bool ok = CanonicalNameSet().Contains(canon);
                why = ok ? "control panel item " + canon + " registered" : "control panel item " + canon + " not registered";
                return ok;
            }
            if (name == "rundll32.exe")
            {
                string dll = a.Split(',')[0].Trim().Trim('"');
                string dllPath = Path.IsPathRooted(dll) ? Environment.ExpandEnvironmentVariables(dll) : Path.Combine(Environment.SystemDirectory, dll);
                if (!File.Exists(dllPath) && !Path.HasExtension(dllPath))
                {
                    dllPath += ".dll";
                }
                if (!File.Exists(dllPath))
                {
                    why = "missing " + dllPath;
                    return false;
                }
                int cr = a.IndexOf("Control_RunDLL", StringComparison.OrdinalIgnoreCase);
                if (cr >= 0)
                {
                    string cpl = a.Substring(cr + 14).Trim().Split(new[] { ',', ' ' }, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? string.Empty;
                    cpl = Environment.ExpandEnvironmentVariables(cpl.Trim('"'));
                    string cplPath = Path.IsPathRooted(cpl) ? cpl : Path.Combine(Environment.SystemDirectory, cpl);
                    if (cpl.Length > 0 && !File.Exists(cplPath))
                    {
                        why = "missing " + cplPath;
                        return false;
                    }
                }
                why = "rundll32 target exists";
                return true;
            }
            if (name == "mmc.exe" && a.Length > 0)
            {
                string msc = Environment.ExpandEnvironmentVariables(a.Split(' ')[0].Trim('"'));
                bool ok = File.Exists(Path.IsPathRooted(msc) ? msc : Path.Combine(Environment.SystemDirectory, msc));
                why = ok ? "console exists" : "missing console " + msc;
                return ok;
            }
            why = "program exists";
            return true;
        }
        catch (Exception ex)
        {
            why = "check failed: " + ex.Message;
            return false;
        }
    }

    internal static bool TargetExists(SystemHit h, out string why)
    {
        return TargetExists(h.Launch, h.Target, h.Args, out why);
    }
}
