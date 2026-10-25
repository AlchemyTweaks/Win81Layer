using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;

#nullable enable annotations

namespace Win81Layer;

// Windows 7 Start menu: search. Every keystroke ranks the programs (SearchScore over the inventory, as before) and the
// system index (Settings pages, Windows Security, Control Panel items and tasks, system tools) and shows them grouped
// under Windows 7 headings: Programs, Settings, Control Panel, each "Name (n)" with n the group's full match count. The
// view never scrolls: every shown group gets up to three rows first, the rest of the viewport goes to the groups in
// display order (a group that only matched by keyword grows only when the stronger groups are fully shown). Programs
// come first unless another group has a strictly better match of at least prefix quality (an exact Control Panel 'Sound'
// beats the 'Sound Recorder' prefix, a whole-word 'Power Options' beats 'PowerISO'). An uninstaller program found only by
// the word 'uninstall' is a weak match: it never holds a group back and is never the automatic first row. Until the
// system index is ready only Programs show (with "Searching..." when nothing matched yet), and the view fills in place
// when it arrives, unless the user has moved the highlight or opened an item menu (the next keystroke shows it then).
public sealed partial class Win7StartMenu
{
    internal readonly record struct Ranked(AppEntry App, int Score, int Match);

    // A row of a search group: a program (App) or a system entry (Hit), with its score (match + extras) and match tier;
    // Weak: an uninstaller matched only by the uninstall words.
    internal readonly record struct SearchResult(AppEntry? App, SystemHit? Hit, int Score, int Match, string Name, bool Weak = false);

    // One shown group of the last search: its full match count, the rows it got, its best match tier and its header.
    internal sealed record SearchGroupInfo(SearchGroup Group, int Count, int Rows, int Best, W7Row Header);

    // Programs-group matches of the last search (programs and tools), how long ranking took (programs, system entries).
    private int _searchMatches;

    private double _lastRankMs;

    private double _lastSystemMs;

    // The first keystroke of an open is traced, later ones only when they are slow.
    private bool _keystrokeTraced;

    private readonly List<SearchGroupInfo> _searchGroups = new List<SearchGroupInfo>();

    // Every match of the last search, by group (the shown rows are the first ones of each).
    private Dictionary<SearchGroup, List<SearchResult>> _searchResults = new Dictionary<SearchGroup, List<SearchResult>>();

    private const int FirstRowsPerGroup = 3;

    // Below this a system match is a typo match (SearchScore's fuzzy tiers, penalties included).
    private const int FuzzyCeiling = 60;

    // A prefix match, whatever penalty its alternative carried (a Greek word rewritten to 'display' scores 497).
    internal const int PromoteTier = SystemSearchIndex.PrefixFloor;

    // A setting on a page that matched at prefix level or better: below the page's own Prior, above unrelated entries of
    // its tier (the Greek word for screen: the display page's resolution and brightness settings before 'Lock screen').
    private const int PageCohesion = 4;

    // The rows of the last search that are weak matches (never the automatic first row, skipped by Enter).
    private readonly HashSet<W7Row> _weakRows = new HashSet<W7Row>();

    internal static string GroupTitle(SearchGroup g)
    {
        return g switch
        {
            SearchGroup.Programs => "Programs",
            SearchGroup.Settings => "Settings",
            _ => "Control Panel"
        };
    }

    // ---- The system index -----------------------------------------------------------------------------------------

    // QA: the index is held back (as before the background load finishes).
    private bool _qaSystemIndexOff;

    private SystemSearchIndex? SystemIndex()
    {
        return _qa && _qaSystemIndexOff ? null : SystemSearchIndex.Current;
    }

    private void EnsureSystemIndex()
    {
        if (_qa && _qaSystemIndexOff)
        {
            return;
        }
        SystemSearchIndex.Ensure(synchronous: _qa, Dispatcher, OnSystemIndexReady);
    }

    // The index arrived (or a refresh changed it) while the menu shows search results: they are ranked again in place.
    // Not while an item menu is open or the user has moved the highlight (keyboard or pointer): the rows under them would
    // change and Enter or a click would start something else. The next keystroke ranks with the new index.
    private void OnSystemIndexReady()
    {
        if (_closed)
        {
            return;
        }
        SystemSearchIndex? idx = SystemSearchIndex.Current;
        Trace($"Win7 Start: system index ready ({idx?.Hits.Count ?? 0} entries from {idx?.Source ?? "none"}, {idx?.LoadMs ?? 0.0:0} ms)");
        if (IsVisible && !_dismissing && !_hidePending && _view == View.Search)
        {
            if (_childMenuOpen || (_sel is W7Row && _zone == Zone.Left && !_selAuto))
            {
                Trace("Win7 Start: system index ready, refresh deferred to the next keystroke (" + (_childMenuOpen ? "item menu open" : "highlight moved") + ")");
                return;
            }
            RefreshInPlace();
        }
    }

    // ---- Search view ---------------------------------------------------------------------------------------------------

    // Ranked results, rebuilt per keystroke: the groups with their headers and rows, the first row selected, and the
    // command row turned into See more results. No match anywhere: a message row instead ("Searching..." while the
    // system index is still loading).
    private void RefreshSearch(string raw)
    {
        Stopwatch sw = Stopwatch.StartNew();
        ClearRows(_searchList);
        _searchGroups.Clear();
        _weakRows.Clear();
        _searchResults = new Dictionary<SearchGroup, List<SearchResult>>();
        List<AppEntry> apps = LoadApps();
        if (apps.Count == 0)
        {
            _searchMatches = 0;
            _searchList.Children.Add(MessageRow(LoadingText));
            _progScroll.ScrollToTop();
            return;
        }
        EnsureShortcutIndex();
        EnsureSystemIndex();
        SystemSearchIndex? index = SystemIndex();
        Stopwatch rank = Stopwatch.StartNew();
        SearchQuery query = SearchQuery.Build(raw, index?.UiGreek ?? false);
        List<Ranked> ranked = RankSearch(apps, query, index);
        _lastRankMs = rank.Elapsed.TotalMilliseconds;
        rank.Restart();
        Dictionary<SearchGroup, List<SearchResult>> groups = RankGroups(apps, ranked, query, index);
        _lastSystemMs = rank.Elapsed.TotalMilliseconds;
        _searchResults = groups;
        _searchMatches = groups.TryGetValue(SearchGroup.Programs, out List<SearchResult>? progs) ? progs.Count : 0;
        List<SearchGroup> order = OrderGroups(groups);
        if (order.Count == 0)
        {
            bool loading = index == null && (_qa ? _qaSystemIndexOff : SystemSearchIndex.Loading);
            _searchList.Children.Add(MessageRow(loading ? SearchingText : NoMatchText));
        }
        else
        {
            int[] alloc = AllocateRows(order.Select((SearchGroup g) => groups[g].Count).ToArray(), order.Select((SearchGroup g) => groups[g].Max((SearchResult r) => r.Match)).ToArray(), _layout.V);
            W7Row? first = null;
            for (int i = 0; i < order.Count; i++)
            {
                if (alloc[i] <= 0)
                {
                    continue;
                }
                List<SearchResult> list = groups[order[i]];
                W7Row header = new W7Row(W7Row.Kind.Header24, onGlass: false)
                {
                    Tag = "w7:header"
                };
                header.SetText($"{GroupTitle(order[i])} ({list.Count})");
                _searchList.Children.Add(header);
                _searchGroups.Add(new SearchGroupInfo(order[i], list.Count, alloc[i], list.Max((SearchResult r) => r.Match), header));
                List<SearchResult> shown = list.Take(alloc[i]).ToList();
                // Two shown rows of a group with the same name (one setting title on two Settings pages) tell themselves
                // apart by their detail.
                HashSet<string> dup = new HashSet<string>(shown.Where((SearchResult r) => r.Hit != null).GroupBy((SearchResult r) => r.Hit!.FoldName).Where((IGrouping<string, SearchResult> g) => g.Count() > 1).Select((IGrouping<string, SearchResult> g) => g.Key), StringComparer.Ordinal);
                foreach (SearchResult r in shown)
                {
                    W7Row row = r.App != null ? ProgramRow(r.App, W7Row.Kind.Program22, isRecent: false) : HitRow(r.Hit!, order[i], dup.Contains(r.Hit!.FoldName));
                    if (r.Weak)
                    {
                        _weakRows.Add(row);
                    }
                    else
                    {
                        first ??= row;
                    }
                    _searchList.Children.Add(row);
                }
            }
            if (first != null)
            {
                // The first result is the virtual cursor's position: Enter launches it, Down moves on from it. It is an
                // automatic selection, so the caret keys still edit the query (SearchBoxKeepsKey). A weak match never is.
                Select(Zone.Left, first, byKeyboard: true, animate: false);
                _selAuto = true;
            }
        }
        _progScroll.ScrollToTop();
        double ms = sw.Elapsed.TotalMilliseconds;
        if (!_keystrokeTraced || ms >= 8.0)
        {
            _keystrokeTraced = true;
            string counts = string.Join("/", new[] { SearchGroup.Programs, SearchGroup.Settings, SearchGroup.ControlPanel }.Select((SearchGroup g) => groups.TryGetValue(g, out List<SearchResult>? l) ? l.Count : 0));
            Trace($"Win7 Start: search {raw.Length} chars -> {counts} (programs/settings/control panel) in {ms:0.0}ms (programs {_lastRankMs:0.00}ms, system {_lastSystemMs:0.00}ms{(index == null ? ", system index not ready" : string.Empty)})");
        }
    }

    // Programs (and the tools the inventory has no program for), Settings and Control Panel, each sorted by score, then
    // kind (pages and applets first), then name. Score = match + usage, plus the kind's Prior at prefix level and better,
    // plus PageCohesion for a setting whose page matched at that level. A Control Panel entry with the same name as a
    // program result is left out, and of two Settings pages that open the same deep link only the better one is kept.
    private Dictionary<SearchGroup, List<SearchResult>> RankGroups(List<AppEntry> apps, List<Ranked> ranked, SearchQuery query, SystemSearchIndex? index)
    {
        Dictionary<SearchGroup, List<SearchResult>> groups = new Dictionary<SearchGroup, List<SearchResult>>();
        bool uninstallQuery = IsUninstallQuery(query);
        Dictionary<string, string>? targets = uninstallQuery ? TargetNamesMap : null;
        List<SearchResult> programs = ranked.Select((Ranked r) =>
        {
            // An uninstaller found only by the uninstall words ('Uninstall Node.js' for 'uninstall') drops to the
            // substring tier: it holds no group back and is never the automatic first row.
            if (uninstallQuery && IsUninstaller(r.App, targets))
            {
                return new SearchResult(r.App, null, SearchScore.Substring + (r.Score - r.Match), SearchScore.Substring, r.App.Name, Weak: true);
            }
            return new SearchResult(r.App, null, r.Score, r.Match, r.App.Name);
        }).ToList();
        List<SearchResult> settings = new List<SearchResult>();
        List<SearchResult> control = new List<SearchResult>();
        if (index != null && query.Alts.Count > 0)
        {
            HashSet<string> hiddenTools = HiddenTools(apps, index);
            List<(SystemHit Hit, int Match)> hits = index.Rank(query);
            HashSet<string> strongPages = new HashSet<string>(hits.Where(((SystemHit Hit, int Match) x) => x.Hit.Kind == SystemHitKind.SettingsPage && x.Match >= PromoteTier).Select(((SystemHit Hit, int Match) x) => x.Hit.Target), StringComparer.OrdinalIgnoreCase);
            foreach ((SystemHit h, int m) in hits)
            {
                if (h.Kind == SystemHitKind.Tool && hiddenTools.Contains(h.Id))
                {
                    continue;
                }
                int extra = (m >= PromoteTier ? h.Prior : 0) + (h.Kind == SystemHitKind.Setting && strongPages.Contains(h.Target) ? PageCohesion : 0);
                SearchResult r = new SearchResult(null, h, m + extra + UsageBonusOf(SearchUsageKey(h)), m, h.Name);
                switch (h.Group)
                {
                case SearchGroup.Programs:
                    programs.Add(r);
                    break;
                case SearchGroup.Settings:
                    settings.Add(r);
                    break;
                default:
                    control.Add(r);
                    break;
                }
            }
            if (control.Count > 0 && programs.Count > 0)
            {
                HashSet<string> programNames = new HashSet<string>(programs.Select((SearchResult p) => SearchText.Fold(p.Name)), StringComparer.Ordinal);
                control.RemoveAll((SearchResult c) => programNames.Contains(c.Hit!.FoldName));
            }
            // Typo matches only help when nothing else matched: 'printer' must not list 'Pointer indicator', and a Greek
            // word must not list a program its keyboard-layout spelling happens to resemble.
            if (programs.Any((SearchResult r) => r.Match >= FuzzyCeiling) || settings.Any((SearchResult r) => r.Match >= FuzzyCeiling) || control.Any((SearchResult r) => r.Match >= FuzzyCeiling))
            {
                programs.RemoveAll((SearchResult r) => r.Match < FuzzyCeiling);
                settings.RemoveAll((SearchResult r) => r.Match < FuzzyCeiling);
                control.RemoveAll((SearchResult r) => r.Match < FuzzyCeiling);
            }
        }
        StringComparer byName = StringComparer.CurrentCultureIgnoreCase;
        // A program of the inventory comes before a tool of the same score.
        programs.Sort((SearchResult x, SearchResult y) =>
        {
            int c = y.Score.CompareTo(x.Score);
            if (c != 0)
            {
                return c;
            }
            c = (x.App != null ? 0 : 1).CompareTo(y.App != null ? 0 : 1);
            return c != 0 ? c : byName.Compare(x.Name, y.Name);
        });
        Comparison<SearchResult> bySystem = (SearchResult x, SearchResult y) =>
        {
            int c = y.Score.CompareTo(x.Score);
            if (c != 0)
            {
                return c;
            }
            c = x.Hit!.KindOrder.CompareTo(y.Hit!.KindOrder);
            if (c != 0)
            {
                return c;
            }
            c = x.Name.Length.CompareTo(y.Name.Length);
            return c != 0 ? c : byName.Compare(x.Name, y.Name);
        };
        settings.Sort(bySystem);
        control.Sort(bySystem);
        // 'Bluetooth settings' and 'Bluetooth and other devices settings' both open ms-settings:bluetooth: one row.
        HashSet<string> pageLinks = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        settings.RemoveAll((SearchResult r) => r.Hit!.Kind == SystemHitKind.SettingsPage && !pageLinks.Add(r.Hit.Describe()));
        if (programs.Count > 0)
        {
            groups[SearchGroup.Programs] = programs;
        }
        if (settings.Count > 0)
        {
            groups[SearchGroup.Settings] = settings;
        }
        if (control.Count > 0)
        {
            groups[SearchGroup.ControlPanel] = control;
        }
        return groups;
    }

    // Default order Programs, Settings, Control Panel. A group moves before Programs only when its best match tier is
    // strictly higher than the best program's and at least a prefix match (any higher tier when the programs only matched
    // by substring or typo); the other groups follow by best tier.
    internal static List<SearchGroup> OrderGroups(Dictionary<SearchGroup, List<SearchResult>> groups)
    {
        SearchGroup[] defaults = { SearchGroup.Programs, SearchGroup.Settings, SearchGroup.ControlPanel };
        List<SearchGroup> present = defaults.Where((SearchGroup g) => groups.TryGetValue(g, out List<SearchResult>? l) && l.Count > 0).ToList();
        int Best(SearchGroup g) => groups[g].Max((SearchResult r) => r.Match);
        List<SearchGroup> others = present.Where((SearchGroup g) => g != SearchGroup.Programs)
            .OrderByDescending(Best).ThenBy((SearchGroup g) => Array.IndexOf(defaults, g)).ToList();
        if (!present.Contains(SearchGroup.Programs))
        {
            return others;
        }
        int pb = Best(SearchGroup.Programs);
        // Programs that only matched by substring or typo hold no group back.
        bool weak = pb <= SearchScore.Substring;
        List<SearchGroup> ahead = others.Where((SearchGroup g) => Best(g) > pb && (weak || Best(g) >= PromoteTier)).ToList();
        List<SearchGroup> order = new List<SearchGroup>(ahead) { SearchGroup.Programs };
        order.AddRange(others.Where((SearchGroup g) => !ahead.Contains(g)));
        return order;
    }

    // Rows per group (in display order) so that headers (24) and rows (22) fit the viewport: trailing groups are dropped
    // until each shown group can have a row; each gets up to three, taken back from the last group first when even that
    // overflows; the rest of the room is shared out a row at a time in display order.
    internal static int[] AllocateRows(int[] counts, double viewport)
    {
        return AllocateRows(counts, null, viewport);
    }

    // best: each group's best match tier. A group that matched only by keyword, substring or typo (below the WordStart
    // tier) keeps its first three rows but grows only once the stronger groups show all of theirs ('notifications': the
    // Settings name matches get the room, not seven Control Panel keyword rows).
    internal static int[] AllocateRows(int[] counts, int[]? best, double viewport)
    {
        int[] alloc = new int[counts.Length];
        int k = counts.Length;
        while (k > 0 && (int)Math.Floor((viewport - 24.0 * k) / 22.0) < k)
        {
            k--;
        }
        if (k == 0)
        {
            return alloc;
        }
        int room = (int)Math.Floor((viewport - 24.0 * k) / 22.0);
        int used = 0;
        for (int i = 0; i < k; i++)
        {
            alloc[i] = Math.Min(counts[i], FirstRowsPerGroup);
            used += alloc[i];
        }
        for (int i = k - 1; i >= 0 && used > room; i--)
        {
            while (alloc[i] > 1 && used > room)
            {
                alloc[i]--;
                used--;
            }
        }
        // The rest goes round the groups one row at a time, the first group first, so a second group with many matches
        // ('mouse': Control Panel, then Settings) is not cut to three rows; the strong groups first, then the weak ones.
        for (int pass = 0; pass < 2; pass++)
        {
            bool grew = true;
            while (used < room && grew)
            {
                grew = false;
                for (int i = 0; i < k && used < room; i++)
                {
                    bool weak = best != null && i < best.Length && best[i] < SystemSearchIndex.NameFloor;
                    if ((pass == 0 && weak) || alloc[i] >= counts[i])
                    {
                        continue;
                    }
                    alloc[i]++;
                    used++;
                    grew = true;
                }
            }
        }
        return alloc;
    }

    // ---- Program ranking -------------------------------------------------------------------------------------------

    // score = max(name match, Greek-layout recovery - 1, shortcut target match (TargetScore)) + usage bonus. A query
    // with Greek letters also matches the folded name (no accents), the synonym rewrites (penalised) and the Greek names
    // Windows gives the Start menu's own shortcuts (the Greek 'Services' -> Services). A program needs a match; the usage bonus only
    // orders programs within a tier. Ordered by score, then by name.
    private List<Ranked> RankSearch(List<AppEntry> apps, string raw)
    {
        SystemSearchIndex? index = SystemIndex();
        return RankSearch(apps, SearchQuery.Build(raw ?? string.Empty, index?.UiGreek ?? false), index);
    }

    private List<Ranked> RankSearch(List<AppEntry> apps, SearchQuery query, SystemSearchIndex? index)
    {
        List<Ranked> res = new List<Ranked>();
        string q = (query.Raw ?? string.Empty).Trim().ToLowerInvariant();
        if (q.Length == 0)
        {
            return res;
        }
        string qg = GreekLayout.ToLatinKeys(q);
        Dictionary<string, string>? targets = TargetNamesMap;
        bool greek = query.HasGreek;
        foreach (AppEntry a in apps)
        {
            int m = NameScore(a.Name, q);
            if (qg.Length > 0)
            {
                m = Math.Max(m, NameScore(a.Name, qg) - 1);
            }
            if (targets != null && targets.TryGetValue(a.Name, out string? target))
            {
                m = Math.Max(m, TargetScore(target, q));
            }
            if (greek)
            {
                string folded = FoldedName(a.Name);
                m = Math.Max(m, NameScore(folded, query.Folded));
                SearchName? sn = null;
                foreach (SearchQuery.Alt alt in query.Alts)
                {
                    if (alt.Synonym)
                    {
                        // Whole words of the rewrite (the Greek 'calculator' -> 'calculator' finds Calculator).
                        sn ??= ProgramSearchName(a.Name);
                        int s = SystemSearchIndex.NameTier(sn, alt, query.Folded.Length);
                        if (s >= SystemSearchIndex.MinTier(query.Folded.Length))
                        {
                            m = Math.Max(m, s + alt.Penalty);
                        }
                    }
                }
                if (index != null && index.AppAlt.TryGetValue(folded, out SearchName[]? altNames))
                {
                    m = Math.Max(m, SystemSearchIndex.AltNameMatch(altNames, query));
                }
            }
            if (m <= 0)
            {
                continue;
            }
            res.Add(new Ranked(a, m + UsageBonusOf(a.LaunchPath), m));
        }
        StringComparer byName = StringComparer.CurrentCultureIgnoreCase;
        res.Sort((Ranked x, Ranked y) =>
        {
            int c = y.Score.CompareTo(x.Score);
            return c != 0 ? c : byName.Compare(x.App.Name, y.App.Name);
        });
        return res;
    }

    private static readonly Dictionary<string, SearchName> s_programNames = new Dictionary<string, SearchName>(StringComparer.Ordinal);

    private static SearchName ProgramSearchName(string name)
    {
        if (!s_programNames.TryGetValue(name, out SearchName? sn))
        {
            if (s_programNames.Count > 4096)
            {
                s_programNames.Clear();
            }
            sn = new SearchName(name, alt: false);
            s_programNames[name] = sn;
        }
        return sn;
    }

    private static readonly Dictionary<string, string> s_foldedNames = new Dictionary<string, string>(StringComparer.Ordinal);

    private static string FoldedName(string name)
    {
        if (!s_foldedNames.TryGetValue(name, out string? f))
        {
            if (s_foldedNames.Count > 4096)
            {
                s_foldedNames.Clear();
            }
            f = SearchText.Fold(name);
            s_foldedNames[name] = f;
        }
        return f;
    }

    // SearchScore's name tiers plus the whole-word bonus on a prefix or word-start match ('Power Options' for 'power', not
    // 'PowerISO'), the same bonus the system entries get.
    internal static int NameScore(string name, string q)
    {
        int m = SearchScore.Name(name, q);
        if (m != SearchScore.Prefix && m != SearchScore.WordStart)
        {
            return m;
        }
        string n = name.ToLowerInvariant();
        if (m == SearchScore.Prefix)
        {
            return WholeAtName(n, q.Length) ? m + SystemSearchIndex.WholeWordBonus : m;
        }
        for (int i = n.IndexOf(q, StringComparison.Ordinal); i >= 0; i = n.IndexOf(q, i + 1, StringComparison.Ordinal))
        {
            if ((i == 0 || !char.IsLetterOrDigit(n[i - 1])) && WholeAtName(n, i + q.Length))
            {
                return m + SystemSearchIndex.WholeWordBonus;
            }
        }
        return m;
    }

    // A lower-case program name: the match ends there with the name, a separator or an English plural ending.
    private static bool WholeAtName(string n, int end)
    {
        return end >= n.Length || !char.IsLetterOrDigit(n[end]) || SystemSearchIndex.WholeAt(n, end);
    }

    // A command typed in full: one point above a whole-word WordStart name match.
    internal const int TargetExact = SearchScore.WordStart + SystemSearchIndex.WholeWordBonus + 1;

    // A typed command matched against a shortcut's target name, around the WordStart tier and far below the name tiers
    // above it. Only exact, prefix and word-start matches count (acronym, substring and typo tiers mean nothing for a file
    // name). The whole command ('cmd', 'regedit') scores TargetExact, so it comes before programs that only have it as a
    // word of their name (Git CMD, whole-word bonus included); part of a command scores one point below WordStart, so a
    // program whose own name matches at WordStart comes first.
    internal static int TargetScore(string target, string q)
    {
        int t = SearchScore.Name(target, q);
        if (t == SearchScore.Exact)
        {
            return TargetExact;
        }
        return (t == SearchScore.Prefix || t == SearchScore.WordStart) ? SearchScore.WordStart - 1 : 0;
    }

    // ---- Uninstallers ----------------------------------------------------------------------------------------------

    private static string[]? s_uninstallWords;

    // The query only says 'uninstall' (in English or Greek, possibly with 'a program', still being typed): every word is
    // the start of a word of the uninstall intent's terms, and one of at least two letters starts a verb of them.
    private static bool IsUninstallQuery(SearchQuery query)
    {
        string[] tokens = SearchText.Words(query.Folded);
        if (tokens.Length == 0)
        {
            return false;
        }
        s_uninstallWords ??= SearchSynonyms.Intents.Where((SearchSynonyms.Intent i) => i.Name == "uninstall").SelectMany((SearchSynonyms.Intent i) => i.Terms).SelectMany(SearchText.Words).Distinct(StringComparer.Ordinal).ToArray();
        string[] verbs = { "uninstall", "remove", "\u03B1\u03C0\u03B5\u03B3\u03BA\u03B1\u03C4\u03B1\u03C3\u03C4\u03B1\u03C3\u03B7", "\u03BA\u03B1\u03C4\u03B1\u03C1\u03B3\u03B7\u03C3\u03B7", "\u03C0\u03C1\u03BF\u03C3\u03B8\u03B1\u03C6\u03B1\u03B9\u03C1\u03B5\u03C3\u03B7" };
        bool verb = false;
        foreach (string t in tokens)
        {
            if (!s_uninstallWords.Any((string w) => w.StartsWith(t, StringComparison.Ordinal)))
            {
                return false;
            }
            verb |= t.Length >= 2 && verbs.Any((string v) => v.StartsWith(t, StringComparison.Ordinal));
        }
        return verb;
    }

    // A program that uninstalls something: its name starts with 'Uninstall' (or the Greek words), or its shortcut or
    // launch target is an uninstaller (unins000.exe, uninstall.exe).
    private static bool IsUninstaller(AppEntry a, Dictionary<string, string>? targets)
    {
        string f = FoldedName(a.Name);
        if (f.StartsWith("uninstall", StringComparison.Ordinal) || f.StartsWith("\u03B1\u03C0\u03B5\u03B3\u03BA\u03B1\u03C4\u03B1\u03C3\u03C4\u03B1\u03C3\u03B7", StringComparison.Ordinal) || f.StartsWith("\u03BA\u03B1\u03C4\u03B1\u03C1\u03B3\u03B7\u03C3\u03B7 \u03B5\u03B3\u03BA\u03B1\u03C4\u03B1\u03C3\u03C4\u03B1\u03C3\u03B7\u03C3", StringComparison.Ordinal))
        {
            return true;
        }
        if (targets != null && targets.TryGetValue(a.Name, out string? t) && t.StartsWith("unins", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }
        string file = a.LaunchPath.Substring(a.LaunchPath.LastIndexOf('\\') + 1);
        return file.StartsWith("unins", StringComparison.OrdinalIgnoreCase) && file.EndsWith(".exe", StringComparison.OrdinalIgnoreCase);
    }

    // SearchScore.UsageBonus applied to this menu's usage source (injected in QA): frequency up to +15 and recency up to
    // +5, always below the gap between two match tiers.
    private int UsageBonusOf(string path)
    {
        (int count, long last) = UsageOf(path);
        int freq = count <= 0 ? 0 : Math.Min(15, (int)Math.Round(5.0 * Math.Log(count + 1.0)));
        int rec = 0;
        if (last > 0)
        {
            double days = (DateTime.UtcNow.Ticks - last) / (double)TimeSpan.TicksPerDay;
            rec = days < 1.0 ? 5 : (days < 7.0 ? 3 : (days < 30.0 ? 1 : 0));
        }
        return freq + rec;
    }

    internal static string SearchUsageKey(SystemHit h)
    {
        return "w7search:" + h.Id;
    }

    // ---- Tools the inventory already has --------------------------------------------------------------------------

    private object? _hiddenToolsSource;

    private int _hiddenToolsCount = -1;

    private object? _hiddenToolsShortcuts;

    private int _hiddenToolsRevision = -1;

    private HashSet<string> _hiddenTools = new HashSet<string>(StringComparer.Ordinal);

    // A tool is left out while a program of the inventory is the same thing: the same name (in any index language), the
    // same target file, or a Start menu shortcut to it. Computed once per inventory, shortcut index and system index.
    private HashSet<string> HiddenTools(List<AppEntry> apps, SystemSearchIndex index)
    {
        object? src = SourceApps();
        ShortcutIndex? sci = System.Threading.Volatile.Read(ref s_shortcuts);
        int rev = SystemSearchIndex.Revision;
        if (ReferenceEquals(src, _hiddenToolsSource) && apps.Count == _hiddenToolsCount && ReferenceEquals(sci, _hiddenToolsShortcuts) && rev == _hiddenToolsRevision)
        {
            return _hiddenTools;
        }
        HashSet<string> names = new HashSet<string>(apps.Select((AppEntry a) => FoldedName(a.Name)), StringComparer.Ordinal);
        HashSet<string> targets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (AppEntry a in apps)
        {
            string? t = AppsFolderTarget(a.LaunchPath);
            if (t == null && a.LaunchPath.Length > 3 && a.LaunchPath[1] == ':' && !a.LaunchPath.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase))
            {
                t = NormalizeTarget(a.LaunchPath);
            }
            if (t != null)
            {
                targets.Add(t);
            }
        }
        HashSet<string> hidden = new HashSet<string>(StringComparer.Ordinal);
        foreach (SystemHit h in index.Hits)
        {
            if (h.Kind != SystemHitKind.Tool)
            {
                continue;
            }
            string? path = NormalizeTarget(h.Target);
            bool same = h.Names.Any((SearchName n) => names.Contains(n.Fold))
                || (path != null && (targets.Contains(path) || (sci != null && sci.FolderByTarget.ContainsKey(path))));
            if (same)
            {
                hidden.Add(h.Id);
            }
        }
        _hiddenTools = hidden;
        _hiddenToolsSource = src;
        _hiddenToolsCount = apps.Count;
        _hiddenToolsShortcuts = sci;
        _hiddenToolsRevision = rev;
        return hidden;
    }

    // ---- Rows ------------------------------------------------------------------------------------------------------

    private W7Row HitRow(SystemHit h, SearchGroup group, bool showDetail)
    {
        W7Row row = new W7Row(W7Row.Kind.Program22, onGlass: false)
        {
            Tag = h,
            Payload = h,
            ToolTipFactory = W7ToolTip,
            FixedTip = h.Name + "\n" + GroupTitle(group) + (string.IsNullOrEmpty(h.Detail) ? string.Empty : " " + (char)0x203A + " " + h.Detail)
        };
        row.SetText(h.Name, showDetail ? h.Detail : null);
        BindHitIcon(row, h);
        WireRow(row);
        row.MouseRightButtonUp += OnHitRightUp;
        return row;
    }

    private const string SettingsAppPath = "shell:AppsFolder\\windows.immersivecontrolpanel_cw5n1h2txyewy!microsoft.windows.immersivecontrolpanel";

    private const string SecurityAppPath = "shell:AppsFolder\\Microsoft.SecHealthUI_8wekyb3d8bbwe!SecHealthUI";

    // Settings rows show the Settings app's icon, Windows Security rows that app's (the inventory's own entry when it has
    // one, so the row shares its cached icon); the others their own icon location or shell name.
    private void BindHitIcon(W7Row row, SystemHit h)
    {
        if (h.Icon == null)
        {
            string path = h.Kind == SystemHitKind.Security ? SecurityAppPath : SettingsAppPath;
            string suffix = path.Substring(path.IndexOf('!'));
            AppEntry? app = LoadApps().FirstOrDefault((AppEntry a) => a.LaunchPath.EndsWith(suffix, StringComparison.OrdinalIgnoreCase));
            if (app != null)
            {
                BindIcon(row, app);
                return;
            }
            BindPathIcon(row, path, h.Name);
            return;
        }
        BindPathIcon(row, h.Icon, h.Name);
    }

    // ---- Launch ----------------------------------------------------------------------------------------------------

    private void LaunchHit(SystemHit h, bool asAdmin)
    {
        LaunchAndClose((asAdmin ? "admin " : string.Empty) + h.Name, delegate
        {
            LaunchHitCore(h, asAdmin);
        });
    }

    // What the entry starts goes through Act (QA records "[runas ]target args"); usage is recorded only when it runs.
    // Elevation only for programs: a task Windows marks needsElevation, or Run as administrator.
    private void LaunchHitCore(SystemHit h, bool asAdmin)
    {
        bool elevate = h.CanElevate && (asAdmin || h.Elevate);
        Act((elevate ? "runas " : string.Empty) + h.Describe(), delegate
        {
            RunHit(h, elevate);
            try
            {
                UsageStore.RecordLaunch(SearchUsageKey(h), DateTime.UtcNow.Ticks);
            }
            catch
            {
            }
        });
    }

    // On a launch worker, never on the UI thread: pages and shell names through the shell as they are, programs with
    // their arguments (an .msc or .cpl opens with its own program).
    private static void RunHit(SystemHit h, bool elevate)
    {
        ShellLaunch.Run(delegate
        {
            try
            {
                ProcessStartInfo psi;
                if (h.Launch == SystemLaunchKind.Uri)
                {
                    psi = new ProcessStartInfo(h.Target)
                    {
                        UseShellExecute = true
                    };
                }
                else
                {
                    string file = Environment.ExpandEnvironmentVariables(h.Target);
                    psi = new ProcessStartInfo(file)
                    {
                        UseShellExecute = true,
                        WorkingDirectory = Path.GetDirectoryName(file) ?? Environment.SystemDirectory
                    };
                    if (!string.IsNullOrEmpty(h.Args))
                    {
                        psi.Arguments = Environment.ExpandEnvironmentVariables(h.Args);
                    }
                }
                if (elevate)
                {
                    psi.Verb = "runas";
                }
                Process.Start(psi)?.Dispose();
            }
            catch (Exception ex)
            {
                Logger.Log("Win7Start system launch " + h.Describe() + " (or cancelled): " + ex.Message);
            }
        });
    }

    // The program file behind an entry, for 'Open file location' (not for hosts such as control.exe).
    private static string? HitFile(SystemHit h)
    {
        if (h.Launch != SystemLaunchKind.Command || SystemSearchBuilder.IsHost(h.Target))
        {
            return null;
        }
        string file = Environment.ExpandEnvironmentVariables(h.Target);
        return File.Exists(file) ? file : null;
    }

    // ---- Item menu -------------------------------------------------------------------------------------------------

    private void OnHitRightUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is not W7Row row || row.Payload is not SystemHit h)
        {
            return;
        }
        e.Handled = true;
        ShowHitMenu(h, row, keyboard: false);
    }

    private void ShowHitMenu(SystemHit h, W7Row row, bool keyboard)
    {
        ContextMenu cm = BuildHitMenu(h);
        _menuRow = row;
        _menuRowMenu = cm;
        if (keyboard)
        {
            cm.HorizontalOffset = 8.0;
            OpenChildMenu(cm, row, PlacementMode.Bottom);
        }
        else
        {
            OpenChildMenu(cm, row, PlacementMode.MousePoint);
        }
        if (!_childMenuOpen || !ReferenceEquals(_openChild, cm))
        {
            _menuRow = null;
            _menuRowMenu = null;
        }
    }

    // Open (bold), Run as administrator (programs only), Open file location (when there is a program file).
    private ContextMenu BuildHitMenu(SystemHit h)
    {
        ContextMenu cm = NewMenu();
        cm.Items.Add(CloseItem("Open", delegate
        {
            LaunchHitCore(h, asAdmin: false);
        }, bold: true));
        if (h.CanElevate)
        {
            cm.Items.Add(CloseItem("Run as administrator", delegate
            {
                LaunchHitCore(h, asAdmin: true);
            }));
        }
        string? file = HitFile(h);
        if (file != null)
        {
            cm.Items.Add(W7Sep());
            cm.Items.Add(CloseItem("Open file location", delegate
            {
                Act("open location " + file, delegate
                {
                    FileShell.OpenLocation(file);
                });
            }));
        }
        CollapseSeparators(cm);
        return cm;
    }
}
