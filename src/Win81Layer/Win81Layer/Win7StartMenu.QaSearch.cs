using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media.Imaging;

#nullable enable annotations

namespace Win81Layer;

// Windows 7 Start menu QA, stage 5: system search (Settings, Windows Security, Control Panel items and tasks, tools)
// with the real index of this machine, built synchronously and read-only: the user's cache is never read or written,
// nothing found is ever started (targets are checked for existence only and every launch is recorded through Act), and
// the index's own cache code is exercised against a file in the output folder.
public sealed partial class Win7StartMenu
{
    // Sample queries of the dump, the renders and the checks.
    private static readonly string[] QaSearchSamples =
    {
        "mouse", "ποντίκι", "ποντικι", "ΠΟΝΤΙΚΙ", "ποντικιού", "pontiki", "bluetooth", "μπλουτουθ", "ήχος", "ηχος", "sound", "display",
        "οθόνη", "wifi", "wi-fi", "printer", "εκτυπωτής", "device manager", "διαχείριση συσκευών", "devmgmt", "services", "υπηρεσίες",
        "disk management", "diskmgmt", "event viewer", "task scheduler", "system information", "registry editor", "regedit",
        "ύπνος", "οδηγοί", "σκοτεινό", "ταπετσαρία", "firewall", "environment variables", "ncpa.cpl", "ποντικι ταχυτητα",
        "control panel", "πίνακας ελέγχου", "s", "co", "calc", "zzqx",
        // Review cases: intents, whole words, page cohesion, page labels, duplicate pages, keyword-only groups.
        "uninstall", "απεγκατάσταση", "update", "ενημέρωση", "power", "brightness", "φωτεινότητα", "volume", "ένταση", "date and time",
        "notifications", "keyboard", "resolution", "storage", "touchpad", "recall"
    };

    private DateTime? _qaCacheStampBefore;

    private void QaType(string q)
    {
        _search.Clear();
        _search.Text = q;
    }

    private List<SearchResult> QaGroupResults(SearchGroup g)
    {
        return _searchResults.TryGetValue(g, out List<SearchResult>? l) ? l : new List<SearchResult>();
    }

    private bool QaGroupHas(SearchGroup g, string targetPart)
    {
        return QaGroupResults(g).Any((SearchResult r) => r.Hit != null && r.Hit.Describe().Contains(targetPart, StringComparison.OrdinalIgnoreCase));
    }

    // The first entry of a group whose launch contains targetPart: its rank in the group (1-based), 0 when absent.
    private int QaRankOf(SearchGroup g, string targetPart)
    {
        List<SearchResult> l = QaGroupResults(g);
        int i = l.FindIndex((SearchResult r) => r.Hit != null && r.Hit.Describe().Contains(targetPart, StringComparison.OrdinalIgnoreCase));
        return i + 1;
    }

    private string QaGroupsSummary()
    {
        return string.Join("; ", _searchGroups.Select((SearchGroupInfo g) => $"{GroupTitle(g.Group)} {g.Rows}/{g.Count} best {g.Best}"));
    }

    private string QaTop(SearchGroup g, int n = 3)
    {
        return string.Join(" | ", QaGroupResults(g).Take(n).Select((SearchResult r) => r.Name + "(" + r.Match + ")"));
    }

    // ---- Renders -----------------------------------------------------------------------------------------------------

    private void QaSearchRenders()
    {
        void Render(string state, string q, Action? after = null)
        {
            QaRender3(state, delegate
            {
                ShowMenu("qa");
                QaType(q);
                after?.Invoke();
                Snap(state);
                Info(state + " " + QaSearchSummary() + " groups: " + QaGroupsSummary());
            });
        }
        Render("search-sys-mouse", "mouse");
        Render("search-sys-greek-mouse", "ποντίκι");
        Render("search-sys-greek-sound", "ήχος");
        Render("search-sys-greek-display", "οθόνη");
        Render("search-sys-bluetooth", "bluetooth");
        Render("search-sys-printer", "printer");
        Render("search-sys-devmgr", "device manager");
        Render("search-sys-disk", "disk management");
        Render("search-sys-uninstall", "uninstall");
        Render("search-sys-power", "power");
        Render("search-sys-hit-selected", "mouse", delegate
        {
            // The highlight moved down onto a system row, its tooltip shown as text in the info line.
            for (int i = 0; i < 4; i++)
            {
                QaKey(Key.Down);
            }
            Info("search-sys-hit-selected tooltip: " + ((_sel as W7Row)?.FixedTip ?? "(none)").Replace("\n", " / "));
        });
        // The same query while the index is still loading: Programs only (or nothing).
        QaRender3("search-sys-loading", delegate
        {
            _qaSystemIndexOff = true;
            ShowMenu("qa");
            QaType("mouse");
            Snap("search-sys-loading");
            Info("search-sys-loading " + QaSearchSummary());
        });
        // Dark rest: the black palette, for the two main cases.
        AppSettings? saved = SettingsStore.DiagnosticsOverride;
        bool dark = ShellTheme.IsDark;
        try
        {
            AppSettings rest = SettingsStore.FastSnapshot.Clone();
            rest.SeasonalActive = SeasonalTheme.Rest;
            SettingsStore.DiagnosticsOverride = rest;
            ShellTheme.ForceForTest(true);
            Render("search-sys-mouse-rest", "mouse");
            Render("search-sys-greek-rest", "ποντίκι");
            Info("search-sys-rest palette=" + _paletteSig);
        }
        finally
        {
            SettingsStore.DiagnosticsOverride = saved;
            ShellTheme.ForceForTest(dark);
            MarkPaletteDirty();
        }
        if (IsVisible)
        {
            Dismiss("qa renders", instant: true);
        }
        QaReset();
    }

    // ---- Checks ------------------------------------------------------------------------------------------------------

    private void QaSearchChecks()
    {
        QaTry("renders-search", delegate
        {
            string[] want = { "search-sys-mouse", "search-sys-greek-mouse", "search-sys-greek-sound", "search-sys-greek-display", "search-sys-bluetooth", "search-sys-printer", "search-sys-devmgr", "search-sys-disk", "search-sys-uninstall", "search-sys-power", "search-sys-hit-selected", "search-sys-loading", "search-sys-mouse-rest", "search-sys-greek-rest" };
            List<string> missing = want.Where((string s) => !_qaSheet.Any(((string State, BitmapSource Bmp) x) => x.State == s)).ToList();
            bool rest = _qaChecks.Any((string l) => l.StartsWith("INFO search-sys-rest palette=", StringComparison.Ordinal) && l.Contains("|rest", StringComparison.Ordinal));
            return (missing.Count == 0 && rest, $"rendered {want.Length - missing.Count}/{want.Length}" + (missing.Count > 0 ? " missing [" + string.Join(", ", missing) + "]" : string.Empty) + $"; Dark rest palette used={rest}");
        });

        QaTry("system-index-built", delegate
        {
            SystemSearchIndex? idx = SystemSearchIndex.Current;
            if (idx == null)
            {
                return (false, "no index: " + (SystemSearchIndex.LastError ?? "not built"));
            }
            Dictionary<SystemHitKind, int> kinds = Enum.GetValues<SystemHitKind>().ToDictionary((SystemHitKind k) => k, (SystemHitKind k) => idx.Hits.Count((SystemHit h) => h.Kind == k));
            foreach (string note in idx.Data.Notes)
            {
                Info("system-index " + note);
            }
            Metric("system-index-build-ms", idx.LoadMs);
            Metric("system-index-entries", idx.Hits.Count);
            bool named = idx.Hits.All((SystemHit h) => !string.IsNullOrWhiteSpace(h.Name) && !h.Name.StartsWith("@", StringComparison.Ordinal) && !string.IsNullOrWhiteSpace(h.Target));
            bool counts = kinds[SystemHitKind.SettingsPage] >= 40 && kinds[SystemHitKind.Setting] >= 200 && kinds[SystemHitKind.ControlPanelItem] >= 20
                && kinds[SystemHitKind.ControlPanelTask] >= 100 && kinds[SystemHitKind.Tool] >= 5;
            bool unique = idx.Hits.Select((SystemHit h) => h.Id).Distinct().Count() == idx.Hits.Count;
            string langs = string.Join(",", idx.Data.Languages);
            bool greekPack = idx.Data.Languages.Any((string l) => l.StartsWith("el", StringComparison.OrdinalIgnoreCase));
            int greekNamed = idx.Hits.Count((SystemHit h) => h.AltNames.Any(SearchText.HasGreek) || SearchText.HasGreek(h.Name));
            bool greek = !greekPack || greekNamed >= idx.Hits.Count * 0.8;
            // The page label of a setting is its own page's title ('Change brightness level' is on Display settings, not on
            // the Graphics page listed under it).
            SystemHit? bright = idx.Hits.FirstOrDefault((SystemHit h) => h.Kind == SystemHitKind.Setting && h.Target == "ms-settings:display" && h.Id.Contains("Brightness", StringComparison.OrdinalIgnoreCase));
            SystemHit? displayPage = idx.Hits.FirstOrDefault((SystemHit h) => h.Id == "ms:AAA_SettingsPagePCSystemDisplay");
            bool label = bright == null || displayPage == null || bright.Detail == displayPage.Name;
            return (named && counts && unique && greek && label && idx.Complete && idx.Source == "qa", $"source={idx.Source} entries={idx.Hits.Count} [{string.Join(", ", kinds.Select((KeyValuePair<SystemHitKind, int> kv) => kv.Key + "=" + kv.Value))}] languages={langs} Greek names={greekNamed} named={named} unique ids={unique} complete={idx.Complete}{(idx.Complete ? string.Empty : " (" + idx.Data.IncompleteReason + ")")} page label of '{bright?.Name}'='{bright?.Detail}' ok={label} built in {idx.LoadMs:0} ms");
        });

        QaTry("system-index-targets", delegate
        {
            SystemSearchIndex? idx = SystemSearchIndex.Current;
            if (idx == null)
            {
                return (false, "no index");
            }
            int actions = _qaActions.Count;
            int launches = _qaLaunches.Count;
            List<string> bad = new List<string>();
            foreach (SystemHit h in idx.Hits)
            {
                if (!SystemSearchIndex.TargetExists(h, out string why))
                {
                    bad.Add(h.Name + " -> " + h.Describe() + " (" + why + ")");
                }
            }
            bool quiet = _qaActions.Count == actions && _qaLaunches.Count == launches;
            int settingsUris = idx.Hits.Count((SystemHit h) => h.Target.StartsWith("ms-settings:", StringComparison.Ordinal));
            // Pages for hardware this PC lacks are left out at build time (the check above can only see that ms-settings
            // is a registered protocol, not whether a page exists).
            SystemSearchBuilder.PcCaps caps = SystemSearchBuilder.PcCaps.Read();
            List<string> absent = idx.Hits.Where((SystemHit h) => h.Target.StartsWith("ms-settings:", StringComparison.Ordinal) && !caps.PageAvailable(h.Target.Substring(12))).Select((SystemHit h) => h.Name).ToList();
            bool filtered = absent.Count == 0;
            return (bad.Count == 0 && quiet && filtered, $"checked {idx.Hits.Count} launch targets without starting anything: missing={bad.Count}" + (bad.Count > 0 ? " [" + string.Join("; ", bad.Take(10)) + "]" : string.Empty)
                + $"; ms-settings entries={settingsUris} (their names are checked for protocol registration only); pages this PC lacks ({caps}) left in the index={absent.Count}" + (absent.Count > 0 ? " [" + string.Join(", ", absent.Take(8)) + "]" : string.Empty) + $"; actions/launches unchanged={quiet}");
        });

        QaTry("system-index-cache", delegate
        {
            SystemSearchIndex? idx = SystemSearchIndex.Current;
            if (idx == null)
            {
                return (false, "no index");
            }
            string path = Path.Combine(_qaOutDir, "win7start-" + _qaTag + "-index.json");
            Stopwatch sw = Stopwatch.StartNew();
            SystemSearchIndex.SaveTo(path, idx.Data);
            double save = sw.Elapsed.TotalMilliseconds;
            sw.Restart();
            SystemSearchData? back = SystemSearchIndex.LoadFrom(path);
            SystemSearchIndex? again = back != null ? SystemSearchIndex.FromData(back, "cache", sw.Elapsed.TotalMilliseconds) : null;
            double load = sw.Elapsed.TotalMilliseconds;
            Metric("system-index-cache-load-ms", load);
            bool same = again != null && again.Hits.Count == idx.Hits.Count && back!.Key == idx.Data.Key;
            // The live menu loads the cache compacted (raw keyword strings dropped once folded): the same rankings.
            SystemSearchData? back2 = SystemSearchIndex.LoadFrom(path);
            SystemSearchIndex? compact = back2 != null ? SystemSearchIndex.FromData(back2, "cache", 0.0, compact: true) : null;
            bool compacted = compact != null && compact.Compacted && compact.Hits.All((SystemHit h) => h.Keywords.Length == 0);
            foreach (string q in new[] { "mouse", "ποντίκι", "services", "devmgmt", "οθόνη" })
            {
                SearchQuery sq = SearchQuery.Build(q, idx.UiGreek);
                string a = string.Join(",", idx.Rank(sq).Select(((SystemHit Hit, int Match) r) => r.Hit.Id + ":" + r.Match));
                string b = again == null ? string.Empty : string.Join(",", again.Rank(sq).Select(((SystemHit Hit, int Match) r) => r.Hit.Id + ":" + r.Match));
                string c = compact == null ? string.Empty : string.Join(",", compact.Rank(sq).Select(((SystemHit Hit, int Match) r) => r.Hit.Id + ":" + r.Match));
                same &= a == b && a == c;
            }
            same &= compacted;
            // A refresh compares content hashes: the round trip keeps the hash.
            bool hash = again != null && again.ContentHash == idx.ContentHash;
            // What the cache would launch is checked on load: the real file passes, rewritten launches do not.
            Stopwatch vw = Stopwatch.StartNew();
            string verifyWhy = "no file";
            bool verified = back != null && SystemSearchBuilder.VerifyCached(back, out verifyWhy);
            double verifyMs = vw.Elapsed.TotalMilliseconds;
            Metric("system-index-cache-verify-ms", verifyMs);
            List<string> poison = new List<string>();
            (string Label, Func<SystemHit, bool> Pick, Action<SystemHit> Edit)[] cases =
            {
                ("task target -> cmd.exe", (SystemHit h) => h.Kind == SystemHitKind.ControlPanelTask && h.Launch == SystemLaunchKind.Command, (SystemHit h) => { h.Target = Path.Combine(Environment.SystemDirectory, "cmd.exe"); h.Args = "/c echo"; }),
                ("elevation added to a task", (SystemHit h) => h.Kind == SystemHitKind.ControlPanelTask && h.Launch == SystemLaunchKind.Command && !h.Elevate && h.CanElevate, (SystemHit h) => h.Elevate = true),
                ("tool path moved", (SystemHit h) => h.Kind == SystemHitKind.Tool, (SystemHit h) => h.Target = Path.Combine(Path.GetTempPath(), Path.GetFileName(h.Target))),
                ("Control Panel item renamed", (SystemHit h) => h.Kind == SystemHitKind.ControlPanelItem && h.Launch == SystemLaunchKind.Command, (SystemHit h) => h.Args = "/name Microsoft.Other"),
                ("Settings page with arguments", (SystemHit h) => h.Kind == SystemHitKind.SettingsPage, (SystemHit h) => { h.Launch = SystemLaunchKind.Command; h.Target = Path.Combine(Environment.SystemDirectory, "cmd.exe"); }),
                ("unknown entry", (SystemHit h) => h.Kind == SystemHitKind.Setting, (SystemHit h) => h.Id = "x:" + h.Id),
            };
            foreach ((string label, Func<SystemHit, bool> pick, Action<SystemHit> edit) in cases)
            {
                SystemSearchData? copy = SystemSearchIndex.LoadFrom(path);
                SystemHit? victim = copy?.Hits.FirstOrDefault(pick);
                if (copy == null || victim == null)
                {
                    poison.Add(label + ": no entry to rewrite");
                    continue;
                }
                edit(victim);
                if (SystemSearchBuilder.VerifyCached(copy, out string why))
                {
                    poison.Add(label + ": accepted");
                }
                else
                {
                    Info("system-index-cache rejected " + label + ": " + why);
                }
            }
            // The run never touches the user's own cache.
            DateTime? stampNow = File.Exists(SystemSearchIndex.CachePath) ? File.GetLastWriteTimeUtc(SystemSearchIndex.CachePath) : null;
            bool untouched = stampNow == _qaCacheStampBefore;
            long size = File.Exists(path) ? new FileInfo(path).Length : 0;
            bool ok = same && hash && verified && poison.Count == 0 && untouched;
            return (ok, $"round trip through {Path.GetFileName(path)} ({size / 1024} KB, save {save:0} ms, load {load:0} ms): same entries and rankings={same}, same content hash={hash}; launches verified on load={verified} ({verifyWhy}, {verifyMs:0} ms), rewritten launches rejected={cases.Length - poison.Count}/{cases.Length}" + (poison.Count > 0 ? " [" + string.Join("; ", poison) + "]" : string.Empty) + $"; user cache {(stampNow == null ? "absent" : "present")} and untouched={untouched}");
        });

        QaTry("search-mouse", delegate
        {
            ShowMenu("qa");
            QaType("mouse");
            bool settings = QaGroupHas(SearchGroup.Settings, "ms-settings:mousetouchpad");
            bool access = QaGroupHas(SearchGroup.Settings, "ms-settings:easeofaccess-mouse");
            bool applet = QaRankOf(SearchGroup.ControlPanel, "/name Microsoft.Mouse") == 1;
            bool task = QaGroupResults(SearchGroup.ControlPanel).Any((SearchResult r) => r.Hit?.Kind == SystemHitKind.ControlPanelTask && r.Name.Equals("Change mouse settings", StringComparison.OrdinalIgnoreCase));
            bool pointer = QaGroupHas(SearchGroup.ControlPanel, "/name Microsoft.Mouse /page 1");
            bool headers = _searchGroups.All((SearchGroupInfo g) => g.Header.Text == $"{GroupTitle(g.Group)} ({g.Count})");
            bool selected = _sel is W7Row s && ReferenceEquals(s, ListRows().FirstOrDefault()) && _selAuto;
            bool ok = settings && access && applet && task && pointer && headers && selected && _searchGroups.Count >= 2;
            return (ok, $"Settings: mousetouchpad={settings} accessibility mouse={access}; Control Panel: Mouse applet first={applet} 'Change mouse settings'={task} pointer page={pointer}; headers={headers} first row selected={selected}; {QaGroupsSummary()}; settings top: {QaTop(SearchGroup.Settings)}; control panel top: {QaTop(SearchGroup.ControlPanel)}");
        });

        QaTry("search-greek", delegate
        {
            // Greek, unaccented, upper case, inflected, greeklish, and English typed on the Greek layout: the same pages.
            ShowMenu("qa");
            List<string> parts = new List<string>();
            bool all = true;
            string layout = QaGreekKeys("mouse") ?? "μοθσε";
            foreach (string q in new[] { "ποντίκι", "ποντικι", "ΠΟΝΤΙΚΙ", "ποντικιού", "pontiki", layout })
            {
                QaType(q);
                bool s = QaRankOf(SearchGroup.Settings, "ms-settings:mousetouchpad") is int r1 && r1 >= 1 && r1 <= 3;
                bool c = QaRankOf(SearchGroup.ControlPanel, "/name Microsoft.Mouse") == 1;
                all &= s && c;
                parts.Add($"'{q}': settings page rank {QaRankOf(SearchGroup.Settings, "ms-settings:mousetouchpad")}, Mouse applet rank {QaRankOf(SearchGroup.ControlPanel, "/name Microsoft.Mouse")}");
            }
            return (all, string.Join("; ", parts));
        });

        QaTry("search-greek-concepts", delegate
        {
            ShowMenu("qa");
            // A page that the SettingsPageVisibility policy hides on this PC is not in the index: its Control Panel twin then.
            bool sleepPage = SystemSearchBuilder.PcCaps.Read().PageAvailable("powersleep");
            (string Q, SearchGroup G, string Target, int MaxRank)[] cases =
            {
                ("ήχος", SearchGroup.Settings, "ms-settings:sound", 2),
                ("ηχος", SearchGroup.ControlPanel, "/name Microsoft.Sound", 1),
                ("οθόνη", SearchGroup.Settings, "ms-settings:display", 1),
                ("εκτυπωτής", SearchGroup.Settings, "ms-settings:printers", 1),
                ("μπλουτουθ", SearchGroup.Settings, "ms-settings:bluetooth", 2),
                sleepPage ? ("ύπνος", SearchGroup.Settings, "ms-settings:powersleep", 3) : ("ύπνος", SearchGroup.ControlPanel, "/name Microsoft.PowerOptions", 3),
                ("σκοτεινό", SearchGroup.Settings, "ms-settings:colors", 2),
                ("υπηρεσίες", SearchGroup.ControlPanel, "services.msc", 3),
                ("διαχείριση συσκευών", SearchGroup.ControlPanel, "/name Microsoft.DeviceManager", 1),
                ("πίνακας ελέγχου", SearchGroup.ControlPanel, "control.exe", 3),
                ("ποντικι ταχυτητα", SearchGroup.ControlPanel, "Microsoft.Mouse", 3),
            };
            List<string> parts = new List<string>();
            bool all = true;
            foreach ((string q, SearchGroup g, string target, int max) in cases)
            {
                QaType(q);
                int rank = QaRankOf(g, target);
                bool ok = rank >= 1 && rank <= max;
                // υπηρεσίες: the Services program (an inventory entry found through its Greek name) also counts.
                if (!ok && q == "υπηρεσίες")
                {
                    ok = QaGroupResults(SearchGroup.Programs).Take(2).Any((SearchResult r) => r.Name.Equals("Services", StringComparison.OrdinalIgnoreCase));
                }
                if (!ok && q == "πίνακας ελέγχου")
                {
                    ok = QaGroupResults(SearchGroup.Programs).Take(2).Any((SearchResult r) => r.Name.Equals("Control Panel", StringComparison.OrdinalIgnoreCase));
                }
                all &= ok;
                parts.Add($"'{q}' {GroupTitle(g)} {target} rank {rank} (<= {max})={ok}");
            }
            return (all, string.Join("; ", parts));
        });

        QaTry("search-english-samples", delegate
        {
            ShowMenu("qa");
            (string Q, SearchGroup G, string Target, int MaxRank)[] cases =
            {
                ("bluetooth", SearchGroup.Settings, "ms-settings:bluetooth", 1),
                ("display", SearchGroup.Settings, "ms-settings:display", 1),
                ("wifi", SearchGroup.Settings, "ms-settings:network-wifi", 1),
                ("wi-fi", SearchGroup.Settings, "ms-settings:network-wifi", 1),
                ("printer", SearchGroup.Settings, "ms-settings:printers", 1),
                ("printer", SearchGroup.ControlPanel, "/name Microsoft.DevicesAndPrinters", 2),
                ("sound", SearchGroup.ControlPanel, "/name Microsoft.Sound", 1),
                ("firewall", SearchGroup.ControlPanel, "/name Microsoft.WindowsFirewall", 1),
                ("environment variables", SearchGroup.ControlPanel, "SystemPropertiesAdvanced", 2),
                ("device manager", SearchGroup.ControlPanel, "/name Microsoft.DeviceManager", 1),
                ("devmgmt", SearchGroup.ControlPanel, "/name Microsoft.DeviceManager", 1),
            };
            List<string> parts = new List<string>();
            bool all = true;
            foreach ((string q, SearchGroup g, string target, int max) in cases)
            {
                QaType(q);
                int rank = QaRankOf(g, target);
                bool ok = rank >= 1 && rank <= max;
                all &= ok;
                parts.Add($"'{q}' {GroupTitle(g)} {target} rank {rank}={ok}");
            }
            // 'printer' must not list typo matches ('Pointer') next to the real ones.
            QaType("printer");
            bool noTypos = _searchResults.Values.SelectMany((List<SearchResult> l) => l).All((SearchResult r) => r.Hit == null || r.Match >= FuzzyCeiling);
            all &= noTypos;
            parts.Add("printer without typo matches=" + noTypos);
            return (all, string.Join("; ", parts));
        });

        QaTry("search-admin-tools", delegate
        {
            ShowMenu("qa");
            List<AppEntry> apps = LoadApps();
            List<string> parts = new List<string>();
            bool all = true;
            // Disk Management has no Start menu shortcut: a tool row in Programs, launched as diskmgmt.msc.
            QaType("disk management");
            bool disk = QaGroupResults(SearchGroup.Programs).FirstOrDefault().Hit is SystemHit dh && dh.Target.EndsWith("diskmgmt.msc", StringComparison.OrdinalIgnoreCase);
            all &= disk;
            parts.Add($"'disk management' -> Programs first is diskmgmt.msc={disk}; {QaGroupsSummary()}");
            QaType("diskmgmt");
            bool cmd = QaGroupHas(SearchGroup.Programs, "diskmgmt.msc");
            all &= cmd;
            parts.Add($"'diskmgmt' finds it={cmd}");
            // Tools the inventory has as programs show once, as the program.
            foreach ((string q, string program) in new[] { ("services", "Services"), ("event viewer", "Event Viewer"), ("task scheduler", "Task Scheduler"), ("system information", "System Information"), ("registry editor", "Registry Editor"), ("regedit", "Registry Editor") })
            {
                QaType(q);
                List<SearchResult> progs = QaGroupResults(SearchGroup.Programs);
                if (!apps.Any((AppEntry a) => a.Name.Equals(program, StringComparison.OrdinalIgnoreCase)))
                {
                    bool tool = progs.Any((SearchResult r) => r.Hit != null && r.Name.Equals(program, StringComparison.OrdinalIgnoreCase));
                    all &= tool;
                    parts.Add($"'{q}': no '{program}' program, tool row={tool}");
                    continue;
                }
                int rows = progs.Count((SearchResult r) => r.Name.Equals(program, StringComparison.OrdinalIgnoreCase));
                bool first = progs.Count > 0 && progs[0].App != null && progs[0].Name.Equals(program, StringComparison.OrdinalIgnoreCase);
                all &= rows == 1 && first;
                parts.Add($"'{q}': '{program}' rows={rows} first program={first}");
            }
            return (all, string.Join("; ", parts));
        });

        QaTry("search-group-order", delegate
        {
            ShowMenu("qa");
            List<string> parts = new List<string>();
            bool all = true;
            foreach (string q in new[] { "s", "co", "calc", "regedit", "note", "sound", "mouse", "ποντίκι", "bluetooth", "οθόνη" })
            {
                QaType(q);
                if (_searchGroups.Count == 0)
                {
                    parts.Add($"'{q}': no groups");
                    continue;
                }
                SearchGroupInfo firstGroup = _searchGroups[0];
                SearchGroupInfo? programs = _searchGroups.FirstOrDefault((SearchGroupInfo g) => g.Group == SearchGroup.Programs);
                bool ok;
                if (programs == null || ReferenceEquals(programs, firstGroup))
                {
                    ok = true;
                }
                else
                {
                    // Only a strictly better match of at least prefix quality comes before Programs (any better one when
                    // the programs only matched by substring or typo).
                    ok = firstGroup.Best > programs.Best && (firstGroup.Best >= PromoteTier || programs.Best <= SearchScore.Substring);
                }
                if (q is "s" or "co" or "calc" or "regedit" or "note" && programs != null)
                {
                    ok &= ReferenceEquals(programs, firstGroup);
                }
                // A Greek word rewritten to a Settings page title ('οθόνη' -> Display settings) leads with Settings.
                if (q == "οθόνη")
                {
                    ok &= firstGroup.Group == SearchGroup.Settings;
                }
                all &= ok;
                parts.Add($"'{q}': {string.Join(" > ", _searchGroups.Select((SearchGroupInfo g) => GroupTitle(g.Group) + "(" + g.Best + ")"))}={ok}");
            }
            return (all, string.Join("; ", parts));
        });

        QaTry("search-uninstall", delegate
        {
            // 'uninstall' asks for Windows' uninstall pages: the first row is Installed apps or Uninstall a program, never an
            // uninstaller program (which stays listed, as a weak match, and is skipped by Enter).
            ShowMenu("qa");
            List<string> parts = new List<string>();
            bool all = true;
            foreach (string q in new[] { "uninstall", "απεγκατάσταση", "uninst" })
            {
                QaType(q);
                W7Row? first = FirstResultRow();
                SystemHit? fh = first?.Payload as SystemHit;
                bool page = fh != null && (fh.Target == "ms-settings:installed-apps" || (fh.Args ?? string.Empty).StartsWith("/name Microsoft.ProgramsAndFeatures", StringComparison.Ordinal));
                bool selected = ReferenceEquals(_sel, first) && _selAuto;
                List<SearchResult> weak = QaGroupResults(SearchGroup.Programs).Where((SearchResult r) => r.Weak).ToList();
                bool weakOk = weak.All((SearchResult r) => r.Match <= SearchScore.Substring) && !_weakRows.Contains(_sel as W7Row ?? _cmdRow);
                bool installed = QaRankOf(SearchGroup.Settings, "ms-settings:installed-apps") == 1;
                all &= page && selected && weakOk && installed;
                parts.Add($"'{q}': first '{first?.Text}' ({fh?.Describe() ?? "program"}) is an uninstall page={page} selected={selected}; Installed apps first in Settings={installed}; weak uninstallers {weak.Count} [{string.Join(", ", weak.Take(3).Select((SearchResult r) => r.Name))}] ok={weakOk}; {QaGroupsSummary()}");
            }
            // A query that names the program is not weak: 'uninstall node' (when such an uninstaller exists) finds it first.
            AppEntry? node = LoadApps().FirstOrDefault((AppEntry a) => a.Name.StartsWith("Uninstall ", StringComparison.OrdinalIgnoreCase) && a.Name.Length > 10);
            if (node != null)
            {
                string q2 = node.Name.Substring(0, Math.Min(node.Name.Length, 14)).ToLowerInvariant();
                QaType(q2);
                bool named = FirstResultRow()?.Payload is AppEntry a2 && ReferenceEquals(a2, node) && !_weakRows.Any();
                all &= named;
                parts.Add($"'{q2}' -> '{FirstResultRow()?.Text}' first and not weak={named}");
            }
            return (all, string.Join("; ", parts));
        });

        QaTry("search-review-ranking", delegate
        {
            // Whole words, intents, page cohesion, duplicate pages and the '&' fold (the review's cases), by launch target.
            ShowMenu("qa");
            List<string> parts = new List<string>();
            bool all = true;
            void Expect(string label, bool ok, string detail)
            {
                all &= ok;
                parts.Add($"{label}={ok} ({detail})");
            }
            QaType("update");
            Expect("'update' Windows Update first", QaRankOf(SearchGroup.Settings, "ms-settings:windowsupdate") == 1, QaTop(SearchGroup.Settings));
            QaType("ενημέρωση");
            Expect("'ενημέρωση' Windows Update first", QaRankOf(SearchGroup.Settings, "ms-settings:windowsupdate") == 1, QaTop(SearchGroup.Settings));
            QaType("power");
            SystemHit? pf = FirstResultRow()?.Payload as SystemHit;
            Expect("'power' first row is Power Options or Power, sleep and battery", pf != null && (pf.Target == "ms-settings:powersleep" || (pf.Args ?? string.Empty) == "/name Microsoft.PowerOptions"), $"first '{FirstResultRow()?.Text}'; {QaGroupsSummary()}");
            List<SearchResult> ps = QaGroupResults(SearchGroup.Settings);
            Expect("'power' PowerShell Developer Settings below the power settings", ps.FindIndex((SearchResult r) => r.Name.StartsWith("PowerShell", StringComparison.Ordinal)) is int psi && (psi < 0 || psi > ps.FindIndex((SearchResult r) => r.Hit!.Target == "ms-settings:powersleep" && r.Hit.Kind == SystemHitKind.Setting)), QaTop(SearchGroup.Settings, 4));
            QaType("sound");
            List<SearchResult> ss = QaGroupResults(SearchGroup.Settings);
            int scape = ss.FindIndex((SearchResult r) => r.Name.StartsWith("Soundscape", StringComparison.OrdinalIgnoreCase));
            Expect("'sound' Soundscape after the whole-word matches", scape < 0 || scape >= 2, QaTop(SearchGroup.Settings, 4));
            foreach (string q in new[] { "οθόνη", "display" })
            {
                QaType(q);
                List<SearchResult> ds = QaGroupResults(SearchGroup.Settings).Take(4).ToList();
                int onPage = ds.Count((SearchResult r) => r.Hit!.Target == "ms-settings:display");
                bool battery = ds.Take(3).Any((SearchResult r) => r.Hit!.Target == "ms-settings:powersleep");
                Expect($"'{q}' display page and its settings lead", ds.Count > 0 && ds[0].Hit!.Target == "ms-settings:display" && onPage >= 3 && !battery, string.Join(" | ", ds.Select((SearchResult r) => r.Name + "(" + r.Hit!.Target.Substring(12) + ")")));
            }
            foreach (string q in new[] { "brightness", "φωτεινότητα" })
            {
                QaType(q);
                SearchResult b = QaGroupResults(SearchGroup.Settings).FirstOrDefault();
                Expect($"'{q}' a display brightness setting first", b.Hit != null && b.Hit.Target == "ms-settings:display", QaTop(SearchGroup.Settings));
            }
            foreach (string q in new[] { "volume", "ένταση" })
            {
                QaType(q);
                Expect($"'{q}' Sound settings first", QaRankOf(SearchGroup.Settings, "ms-settings:sound") == 1, QaTop(SearchGroup.Settings));
            }
            QaType("date and time");
            Expect("'date and time' Date & time settings first", QaRankOf(SearchGroup.Settings, "ms-settings:dateandtime") == 1 && QaGroupResults(SearchGroup.Settings)[0].Hit!.Kind == SystemHitKind.SettingsPage, QaTop(SearchGroup.Settings));
            foreach (string q in new[] { "bluetooth", "storage", "update" })
            {
                QaType(q);
                List<string> links = QaGroupResults(SearchGroup.Settings).Where((SearchResult r) => r.Hit!.Kind == SystemHitKind.SettingsPage).Select((SearchResult r) => r.Hit!.Describe()).ToList();
                Expect($"'{q}' one page row per deep link", links.Count == links.Distinct(StringComparer.OrdinalIgnoreCase).Count(), QaTop(SearchGroup.Settings, 4));
            }
            // Programs still lead when they match well.
            foreach (string q in new[] { "calc", "note", "co" })
            {
                QaType(q);
                bool hasPrograms = _searchGroups.Any((SearchGroupInfo g) => g.Group == SearchGroup.Programs);
                Expect($"'{q}' Programs first", !hasPrograms || _searchGroups[0].Group == SearchGroup.Programs, QaGroupsSummary());
            }
            return (all, string.Join("; ", parts));
        });

        QaTry("search-budget", delegate
        {
            // Unit cases of the allocation, then the view for real queries: no scrolling, every shown group has a row, the
            // rows per group are the allocation, the headers count every match.
            bool unit = AllocateRows(new[] { 20, 50, 30 }, 376.0).SequenceEqual(new[] { 5, 4, 4 })
                && AllocateRows(new[] { 1, 50, 30 }, 376.0).SequenceEqual(new[] { 1, 6, 6 })
                && AllocateRows(new[] { 17, 41 }, 376.0).SequenceEqual(new[] { 7, 7 })
                && AllocateRows(new[] { 2, 41 }, 376.0).SequenceEqual(new[] { 2, 12 })
                && AllocateRows(new[] { 2 }, 376.0).SequenceEqual(new[] { 2 })
                && AllocateRows(new[] { 40 }, 376.0).SequenceEqual(new[] { 16 })
                && AllocateRows(new[] { 5, 5, 5 }, 60.0).SequenceEqual(new[] { 1, 0, 0 })
                && AllocateRows(new[] { 9, 9, 9 }, 150.0).SequenceEqual(new[] { 1, 1, 1 })
                // A keyword-only group keeps three rows while a stronger one has more to show, then takes what is left.
                && AllocateRows(new[] { 8, 21 }, new[] { 160, 508 }, 376.0).SequenceEqual(new[] { 3, 11 })
                && AllocateRows(new[] { 8, 5 }, new[] { 160, 508 }, 376.0).SequenceEqual(new[] { 8, 5 })
                && AllocateRows(new[] { 17, 41 }, new[] { 1000, 500 }, 376.0).SequenceEqual(new[] { 7, 7 });
            ShowMenu("qa");
            List<string> parts = new List<string>();
            bool all = unit;
            foreach (string q in new[] { "mouse", "s", "co", "ποντίκι", "display", "disk management", "notifications" })
            {
                QaType(q);
                UpdateLayout();
                int[] counts = _searchGroups.Select((SearchGroupInfo g) => g.Count).ToArray();
                List<W7Row> children = _progList.Children.OfType<W7Row>().ToList();
                bool rowsOk = true;
                int total = 0;
                for (int i = 0; i < _searchGroups.Count; i++)
                {
                    int at = children.IndexOf(_searchGroups[i].Header);
                    int next = i + 1 < _searchGroups.Count ? children.IndexOf(_searchGroups[i + 1].Header) : children.Count;
                    int rows = next - at - 1;
                    rowsOk &= rows == _searchGroups[i].Rows && rows >= 1;
                    total += rows;
                }
                double height = _searchGroups.Count * 24.0 + total * 22.0;
                bool fits = height <= _layout.V + 0.5 && _progScroll.ScrollableHeight < 0.5;
                // A keyword-only group never has more than three rows while a stronger group is cut short.
                bool weakCapped = !_searchGroups.Any((SearchGroupInfo g) => g.Best < SystemSearchIndex.NameFloor && g.Rows > FirstRowsPerGroup)
                    || _searchGroups.All((SearchGroupInfo g) => g.Best < SystemSearchIndex.NameFloor || g.Rows == g.Count);
                all &= rowsOk && fits && weakCapped;
                parts.Add($"'{q}': [{string.Join(",", _searchGroups.Select((SearchGroupInfo g) => g.Rows + "/" + g.Count + "@" + g.Best))}] height {height:0}/{_layout.V:0} rows ok={rowsOk} fits={fits} keyword groups capped={weakCapped}");
            }
            return (all, $"allocation unit cases={unit}; " + string.Join("; ", parts));
        });

        QaTry("search-keyboard-groups", delegate
        {
            ShowMenu("qa");
            QaType("mouse");
            List<string> steps = new List<string>();
            bool all = true;
            void Step(string name, bool ok)
            {
                all &= ok;
                steps.Add(name + (ok ? " ok" : " FAIL"));
            }
            if (_searchGroups.Count < 2)
            {
                return (false, "two groups needed: " + QaGroupsSummary());
            }
            List<W7Row> rows = ListRows();
            int firstGroupRows = _searchGroups[0].Rows;
            List<W7Row> children = _progList.Children.OfType<W7Row>().ToList();
            W7Row secondFirst = children[children.IndexOf(_searchGroups[1].Header) + 1];
            Step("first row selected", ReferenceEquals(_sel, rows[0]) && _selAuto);
            for (int i = 0; i < firstGroupRows; i++)
            {
                QaKey(Key.Down);
            }
            Step($"Down x{firstGroupRows} -> first row of the second group, over its header", ReferenceEquals(_sel, secondFirst));
            QaKey(Key.Up);
            Step("Up -> back to the last row of the first group", ReferenceEquals(_sel, rows[firstGroupRows - 1]));
            QaKey(Key.Down);
            SystemHit? hit = secondFirst.Payload as SystemHit;
            int actions = _qaActions.Count;
            int launches = _qaLaunches.Count;
            int t = _qaTrace.Count;
            QaKey(Key.Enter);
            string expected = hit?.Describe() ?? "(no hit)";
            bool entered = hit != null && _qaActions.Count == actions + 1 && _qaActions[^1] == (hit.CanElevate && hit.Elevate ? "runas " : string.Empty) + expected
                && _qaLaunches.Count == launches && !IsVisible && TraceSince(t, "hide cause = launch ");
            Step($"Enter on '{hit?.Name}' -> recorded '{(_qaActions.Count > actions ? _qaActions[^1] : "(none)")}', closed, nothing launched", entered);
            return (all, string.Join("; ", steps));
        });

        QaTry("search-enter-first", delegate
        {
            // With the selection taken away (Tab cycles it off), Enter still starts the first result of the first group.
            ShowMenu("qa");
            QaType("mouse");
            W7Row? first = FirstResultRow();
            ClearSelection(animate: false);
            int actions = _qaActions.Count;
            int launches = _qaLaunches.Count;
            QaKey(Key.Enter);
            bool ok = first?.Payload switch
            {
                SystemHit h => _qaActions.Count == actions + 1 && _qaActions[^1].EndsWith(h.Describe(), StringComparison.Ordinal),
                AppEntry a => _qaLaunches.Count == launches + 1 && _qaLaunches[^1].Name == a.Name,
                _ => false
            };
            return (ok && !IsVisible, $"first result '{first?.Text}' -> '{(_qaActions.Count > actions ? _qaActions[^1] : (_qaLaunches.Count > launches ? "launch " + _qaLaunches[^1].Name : "(none)"))}' closed={!IsVisible}");
        });

        QaTry("search-click-hit", delegate
        {
            ShowMenu("qa");
            QaType("bluetooth");
            W7Row? row = ListRows().FirstOrDefault((W7Row r) => r.Payload is SystemHit h && h.Target.StartsWith("ms-settings:", StringComparison.Ordinal));
            if (row == null)
            {
                return (false, "no Settings row: " + QaSearchSummary());
            }
            SystemHit hit = (SystemHit)row.Payload!;
            int actions = _qaActions.Count;
            UpdateLayout();
            QaClick(row);
            bool ok = _qaActions.Count == actions + 1 && _qaActions[^1] == hit.Describe() && !IsVisible;
            return (ok, $"clicked '{hit.Name}' -> '{(_qaActions.Count > actions ? _qaActions[^1] : "(none)")}' closed={!IsVisible}");
        });

        QaTry("search-elevation", delegate
        {
            // Ctrl+Shift+Enter: a page opens as it is (no runas), a program runs elevated.
            ShowMenu("qa");
            QaType("bluetooth");
            int actions = _qaActions.Count;
            _qaMods = ModifierKeys.Control | ModifierKeys.Shift;
            QaKey(Key.Enter);
            _qaMods = ModifierKeys.None;
            string page = _qaActions.Count > actions ? _qaActions[^1] : "(none)";
            bool pageOk = page.StartsWith("ms-settings:", StringComparison.Ordinal) || (_qaLaunches.Count > 0 && page == "(none)");
            ShowMenu("qa");
            QaType("disk management");
            W7Row? tool = ListRows().FirstOrDefault((W7Row r) => r.Payload is SystemHit h && h.Kind == SystemHitKind.Tool);
            string elevated = "(no tool row)";
            bool toolOk = false;
            if (tool != null)
            {
                Select(Zone.Left, tool, byKeyboard: true);
                actions = _qaActions.Count;
                _qaMods = ModifierKeys.Control | ModifierKeys.Shift;
                QaKey(Key.Enter);
                _qaMods = ModifierKeys.None;
                elevated = _qaActions.Count > actions ? _qaActions[^1] : "(none)";
                toolOk = elevated.StartsWith("runas ", StringComparison.Ordinal) && elevated.EndsWith(".msc", StringComparison.OrdinalIgnoreCase);
            }
            return (pageOk && toolOk, $"page -> '{page}' (no runas)={pageOk}; tool -> '{elevated}'={toolOk}");
        });

        QaTry("search-hit-menu", delegate
        {
            ShowMenu("qa");
            QaType("disk management");
            W7Row? tool = ListRows().FirstOrDefault((W7Row r) => r.Payload is SystemHit h && h.Kind == SystemHitKind.Tool);
            QaType("bluetooth");
            W7Row? page = ListRows().FirstOrDefault((W7Row r) => r.Payload is SystemHit);
            if (page == null)
            {
                return (false, "no system row");
            }
            Select(Zone.Left, page, byKeyboard: true);
            int n = _qaChildRequests.Count;
            bool apps = QaKey(Key.Apps);
            string pageMenu = _qaChildRequests.Count == n + 1 ? QaHeaders(_qaChildRequests[^1].Menu) : "(none)";
            bool pageOk = apps && pageMenu == "Open*";
            int actions = _qaActions.Count;
            if (_qaChildRequests.Count == n + 1)
            {
                ((MenuItem)_qaChildRequests[^1].Menu.Items[0]).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                QaPump(30);
            }
            bool opened = _qaActions.Count == actions + 1 && _qaActions[^1].StartsWith("ms-settings:", StringComparison.Ordinal) && !IsVisible;
            string toolMenu = "(no tool row)";
            bool toolOk = true;
            ShowMenu("qa");
            QaType("disk management");
            tool = ListRows().FirstOrDefault((W7Row r) => r.Payload is SystemHit h && h.Kind == SystemHitKind.Tool);
            if (tool != null)
            {
                n = _qaChildRequests.Count;
                QaRightUp(tool);
                toolMenu = _qaChildRequests.Count == n + 1 ? QaHeaders(_qaChildRequests[^1].Menu) : "(none)";
                toolOk = toolMenu == "Open*, Run as administrator, |, Open file location" && _qaChildRequests[^1].Mode == PlacementMode.MousePoint;
            }
            return (pageOk && opened && toolOk, $"Apps on a page row -> [{pageMenu}]={pageOk}, Open -> recorded and closed={opened}; right-click on a tool -> [{toolMenu}]={toolOk}");
        });

        QaTry("search-index-not-ready", delegate
        {
            ShowMenu("qa");
            _qaSystemIndexOff = true;
            QaType("mouse");
            bool programsOnly = _searchGroups.All((SearchGroupInfo g) => g.Group == SearchGroup.Programs);
            // Nothing matched yet: the menu says it is searching, not that nothing matches.
            bool searching = ListRows().Count > 0 || _progList.Children.OfType<W7Row>().Any((W7Row r) => r.RowKind == W7Row.Kind.Message22 && r.Text == SearchingText);
            string before = QaSearchSummary();
            _qaSystemIndexOff = false;
            int t = _qaTrace.Count;
            OnSystemIndexReady();
            bool filled = _searchGroups.Any((SearchGroupInfo g) => g.Group != SearchGroup.Programs) && TraceSince(t, "system index ready") && _view == View.Search && IsOpen;
            return (programsOnly && searching && filled, $"while loading: programs only={programsOnly}, '{SearchingText}' shown={searching} ({before}); index ready -> refreshed in place with system groups={filled} ({QaGroupsSummary()})");
        });

        QaTry("search-index-ready-deferred", delegate
        {
            // The index arriving never moves a highlight the user chose, nor the rows under an open item menu: the refresh
            // waits for the next keystroke.
            ShowMenu("qa");
            _qaSystemIndexOff = true;
            QaType("co");
            if (ListRows().Count < 2)
            {
                return (false, "'co' needs two program rows: " + QaSearchSummary());
            }
            QaKey(Key.Down);
            W7Row? chosen = _sel as W7Row;
            _qaSystemIndexOff = false;
            int t = _qaTrace.Count;
            OnSystemIndexReady();
            bool kept = chosen != null && ReferenceEquals(_sel, chosen) && InCurrentList(chosen) && _searchGroups.All((SearchGroupInfo g) => g.Group == SearchGroup.Programs) && TraceSince(t, "highlight moved");
            _qaSystemIndexOff = true;
            QaType("co");
            _qaSystemIndexOff = false;
            _childMenuOpen = true;
            t = _qaTrace.Count;
            OnSystemIndexReady();
            bool menu = _searchGroups.All((SearchGroupInfo g) => g.Group == SearchGroup.Programs) && TraceSince(t, "item menu open");
            _childMenuOpen = false;
            QaType("co");
            bool next = _searchGroups.Any((SearchGroupInfo g) => g.Group != SearchGroup.Programs);
            return (kept && menu && next, $"highlight moved to '{chosen?.Text}' -> kept, rows unchanged={kept}; item menu open -> rows unchanged={menu}; next keystroke -> system groups={next} ({QaGroupsSummary()})");
        });

        QaTry("search-icons", delegate
        {
            ShowMenu("qa");
            List<string> bad = new List<string>();
            int count = 0;
            foreach (string q in new[] { "mouse", "bluetooth", "firewall", "disk management", "sound" })
            {
                QaType(q);
                foreach (W7Row r in ListRows().Where((W7Row x) => x.Payload is SystemHit))
                {
                    count++;
                    SystemHit h = (SystemHit)r.Payload!;
                    if (r.IconSource is not BitmapSource src || src.PixelWidth != 16 || src.PixelHeight != 16)
                    {
                        bad.Add(h.Name + "(no 16 px icon)");
                        continue;
                    }
                    if (_letterRows.Contains(r))
                    {
                        bad.Add(h.Name + "(letter tile, " + (h.Icon ?? "group icon") + ")");
                    }
                }
            }
            return (bad.Count == 0 && count > 0, $"system rows={count} without a real 16 px icon={bad.Count}" + (bad.Count > 0 ? " [" + string.Join(", ", bad.Distinct().Take(12)) + "]" : string.Empty));
        });

        QaTry("search-tooltips", delegate
        {
            ShowMenu("qa");
            QaType("mouse");
            List<W7Row> hits = ListRows().Where((W7Row r) => r.Payload is SystemHit).ToList();
            bool all = hits.Count > 0 && hits.All((W7Row r) => r.FixedTip != null && r.FixedTip.StartsWith(((SystemHit)r.Payload!).Name + "\n", StringComparison.Ordinal) && r.ToolTip is ToolTip);
            return (all, $"system rows={hits.Count}, every row has its name and group as a tooltip={all}; e.g. '{hits.FirstOrDefault()?.FixedTip?.Replace("\n", " / ")}'");
        });

        QaTry("search-no-side-effects", delegate
        {
            // Typing every sample query starts nothing and writes nothing.
            ShowMenu("qa");
            int actions = _qaActions.Count;
            int launches = _qaLaunches.Count;
            int requests = _qaChildRequests.Count;
            foreach (string q in QaSearchSamples)
            {
                QaType(q);
            }
            bool quiet = _qaActions.Count == actions && _qaLaunches.Count == launches && _qaChildRequests.Count == requests && IsOpen;
            return (quiet, $"{QaSearchSamples.Length} queries typed: actions {actions}->{_qaActions.Count} launches {launches}->{_qaLaunches.Count} menus {requests}->{_qaChildRequests.Count} open={IsOpen}");
        });

        QaTry("search-perf", delegate
        {
            ShowMenu("qa");
            SystemSearchIndex? idx = SystemIndex();
            if (idx == null)
            {
                return (false, "no index");
            }
            List<AppEntry> apps = LoadApps();
            List<double> sys = new List<double>();
            List<double> keys = new List<double>();
            foreach (string q in new[] { "mouse", "ποντίκι", "οθόνη", "s", "device manager" })
            {
                for (int i = 0; i < 6; i++)
                {
                    Stopwatch sw = Stopwatch.StartNew();
                    SearchQuery query = SearchQuery.Build(q, idx.UiGreek);
                    RankGroups(apps, RankSearch(apps, query, idx), query, idx);
                    if (i > 0)
                    {
                        sys.Add(sw.Elapsed.TotalMilliseconds);
                    }
                }
            }
            for (int i = 0; i < 7; i++)
            {
                _search.Text = "mous";
                UpdateLayout();
                Stopwatch sw = Stopwatch.StartNew();
                _search.Text = "mouse";
                UpdateLayout();
                keys.Add(sw.Elapsed.TotalMilliseconds);
                _search.Clear();
                UpdateLayout();
            }
            double med = Median(sys);
            Metric("ranking-system-median-ms", med);
            Metric("ranking-system-max-ms", sys.Max());
            Metric("keystroke-mouse-ms", Median(keys));
            return (med < 10.0, $"programs + system ranking median {med:0.00} ms (max {sys.Max():0.00}) over 5 queries x 5; keystroke 'mouse' with rows and layout median {Median(keys):0.00} ms");
        });

        QaWriteSearchDump();
    }

    // win7start-<tag>-search-dump.txt: every sample query, its groups and rows in order, with launch and target check.
    private void QaWriteSearchDump()
    {
        try
        {
            ShowMenu("qa");
            StringBuilder sb = new StringBuilder();
            SystemSearchIndex? idx = SystemIndex();
            sb.AppendLine($"index: {idx?.Hits.Count ?? 0} entries, source {idx?.Source}, languages {string.Join(",", idx?.Data.Languages ?? Array.Empty<string>())}");
            foreach (string q in QaSearchSamples)
            {
                QaType(q);
                sb.AppendLine($"### '{q}'  {QaGroupsSummary()}");
                foreach (SearchGroupInfo g in _searchGroups)
                {
                    sb.AppendLine($"  [{GroupTitle(g.Group)} ({g.Count})] shown {g.Rows}");
                    foreach (SearchResult r in QaGroupResults(g.Group).Take(Math.Max(g.Rows, 5)))
                    {
                        if (r.Hit != null)
                        {
                            bool valid = SystemSearchIndex.TargetExists(r.Hit, out string why);
                            sb.AppendLine($"     {r.Match,5} {r.Hit.Kind,-16} {r.Name}  -> {r.Hit.Describe()}  [{(valid ? "ok" : "MISSING " + why)}]");
                        }
                        else
                        {
                            sb.AppendLine($"     {r.Match,5} Program          {r.Name}  -> {r.App!.LaunchPath}");
                        }
                    }
                }
            }
            File.WriteAllText(Path.Combine(_qaOutDir, "win7start-" + _qaTag + "-search-dump.txt"), sb.ToString(), new UTF8Encoding(false));
        }
        catch (Exception ex)
        {
            Info("search dump failed: " + ex.Message);
        }
        finally
        {
            QaReset();
            if (IsVisible)
            {
                Dismiss("qa reset", instant: true);
            }
        }
    }
}
