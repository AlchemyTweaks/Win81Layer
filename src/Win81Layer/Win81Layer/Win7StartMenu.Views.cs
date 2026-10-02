using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Microsoft.Win32;

#nullable enable annotations

namespace Win81Layer;

// Windows 7 Start menu: left-pane views and their data. Three containers share the list viewport and only one is shown:
// - main: pinned programs, then the recent list; rebuilt on every open (about 20 cheap rows);
// - All Programs: a one-level folder tree (root programs, then folders), built once per inventory revision and kept;
//   folder children are created on their first expand;
// - search: programs ranked with SearchScore, rebuilt per keystroke within a row budget, so it never scrolls.
// The pin and hidden lists, usage and taskbar-pin state are read here (injected in QA).
public sealed partial class Win7StartMenu
{
    // Pins and recent rows the main view shows (the layout follows them).
    private int _mainPins;

    private int _mainRecent;

    // Recent rows that fit under the pins on the target monitor.
    private int _recentMax = 10;

    // Icon size factor of the target monitor (icons are loaded at box x scale pixels).
    private double _iconScale = 1.0;

    // Rows waiting for an icon, by W7Icons cache key.
    private readonly Dictionary<string, List<(W7Row Row, string Path, int Px)>> _iconWaiters = new Dictionary<string, List<(W7Row Row, string Path, int Px)>>(StringComparer.OrdinalIgnoreCase);

    // The three list containers (built in BuildRoot); _progList is the one shown.
    private StackPanel _mainList = null!;

    private StackPanel _allList = null!;

    private StackPanel _searchList = null!;

    private const string LoadingText = "Loading programs\u2026";

    private const string NoMatchText = "No items match your search.";

    // Recent-list seeds, by exact name, used when fewer recent programs remain than the list has room for.
    private static readonly string[] RecentSeeds =
    {
        "File Explorer", "Notepad", "Paint", "Calculator", "Snipping Tool", "Sticky Notes", "Command Prompt", "Character Map"
    };

    // Names that never enter the recent list (the Windows 7 rule): any of these whole words, ignoring case.
    private static readonly Regex RecentExcluded = new Regex(
        @"(?<![\p{L}\p{N}])(?:uninstall|uninstaller|readme|read\s+me|read\s+first|help|documentation|manual|faq|setup|install|license|release\s+notes|what['\u2019]s\s+new|website|support)(?![\p{L}\p{N}])",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    // Usage halves every 14 days.
    private const double RecentHalfLifeDays = 14.0;

    private enum View
    {
        Main,
        AllPrograms,
        Search
    }

    private View _view = View.Main;

    // Start_TrackProgs, read once per show: when it is 0 the recent list stays empty and only pins show.
    private bool _trackProgs = true;

    private static readonly ScaleTransform MirrorX = CreateMirror();

    private static ScaleTransform CreateMirror()
    {
        ScaleTransform t = new ScaleTransform(-1.0, 1.0);
        t.Freeze();
        return t;
    }

    private void ToggleAllPrograms()
    {
        _allMode = !_allMode;
        RefreshList();
    }

    // All Programs (triangle), Back (mirrored triangle) or, in the search view, See more results (magnifier).
    private void UpdateCommandRow()
    {
        if (_view == View.Search)
        {
            _cmdRow.SetText("See more results");
            _cmdTriangle.Visibility = Visibility.Collapsed;
            _cmdMagnifier.Visibility = Visibility.Visible;
            return;
        }
        _cmdRow.SetText(_allMode ? "Back" : "All Programs");
        _cmdTriangle.RenderTransform = _allMode ? MirrorX : Transform.Identity;
        _cmdTriangle.Visibility = Visibility.Visible;
        _cmdMagnifier.Visibility = Visibility.Collapsed;
    }

    // The inventory as the provider hands it over (its reference and count are the All Programs revision).
    private IReadOnlyList<AppEntry>? SourceApps()
    {
        if (_qa && _qaEmptyInventory)
        {
            return Array.Empty<AppEntry>();
        }
        try
        {
            return _appsProvider();
        }
        catch
        {
            return null;
        }
    }

    // The inventory without the entries this menu never shows: the Desktop and News pseudo-apps belong to Metro Start.
    private List<AppEntry> LoadApps()
    {
        IReadOnlyList<AppEntry>? source = SourceApps();
        bool extras = _qa && !_qaEmptyInventory;
        List<AppEntry> apps = new List<AppEntry>((source?.Count ?? 0) + (extras ? _qaExtraApps.Count : 0));
        if (source != null)
        {
            apps.AddRange(source.Where((AppEntry a) => a != null && IsMenuEntry(a)));
        }
        if (extras)
        {
            apps.AddRange(_qaExtraApps.Where(IsMenuEntry));
        }
        return apps;
    }

    private static bool IsMenuEntry(AppEntry a)
    {
        return !string.Equals(a.LaunchPath, "win81:desktop", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(a.LaunchPath, "win81:news", StringComparison.OrdinalIgnoreCase);
    }

    // Shows the container of a view. A highlight or press left on a row of the outgoing list is cleared (All Programs rows
    // persist, so they must not come back lit).
    private void ShowList(View v)
    {
        StackPanel next = v == View.AllPrograms ? _allList : (v == View.Search ? _searchList : _mainList);
        if (ReferenceEquals(next, _progList))
        {
            return;
        }
        if (_sel is W7Row row && _zone == Zone.Left && !ReferenceEquals(row, _cmdRow))
        {
            ClearSelection(animate: false);
        }
        if (_pressed != null && !ReferenceEquals(_pressed, _cmdRow) && ZoneOf(_pressed) == Zone.Left)
        {
            CancelPress();
        }
        _progList.Visibility = Visibility.Collapsed;
        next.Visibility = Visibility.Visible;
        _progList = next;
        if (_lastLeft != null && !ReferenceEquals(_lastLeft, _cmdRow) && !InCurrentList(_lastLeft))
        {
            _lastLeft = null;
        }
    }

    // Shows the view the box and the All Programs flag ask for: search when text is present, otherwise All Programs or the
    // main view. A change of view fades the incoming list in. The window height is fixed for the session once it is shown,
    // so a main view rebuilt here (back from All Programs or search, perhaps after a pin made there) fits the recent list
    // to that height, as an in-place rebuild does. Only PrepareForShow builds the main view before the layout is chosen.
    private void RefreshList()
    {
        string q = _search.Text?.Trim() ?? string.Empty;
        View next = q.Length > 0 ? View.Search : (_allMode ? View.AllPrograms : View.Main);
        bool changed = next != _view;
        _view = next;
        ShowList(next);
        UpdateCommandRow();
        if (next == View.Search)
        {
            RefreshSearch(q);
        }
        else if (next == View.AllPrograms)
        {
            Stopwatch sw = Stopwatch.StartNew();
            bool built = EnsureAllPrograms();
            if (!built)
            {
                // Kept rows: a letter tile that has waited long enough is requested again.
                RetryLetterRows();
            }
            if (changed)
            {
                _progScroll.ScrollToTop();
            }
            Trace($"Win7 Start: all programs {sw.Elapsed.TotalMilliseconds:0.0}ms ({(built ? "built" : "kept")}, {_allNodes.Count} entries)");
        }
        else
        {
            RefreshMain(keepHeight: IsVisible);
        }
        UpdateScrollLane();
        if (changed)
        {
            FadeInView();
        }
        // New rows under a resting pointer are not a hover.
        ArmHoverGuard();
    }

    // The scrollbar lane is Auto, except while All Programs is known to overflow the viewport: then it is reserved up
    // front, so the kept rows are laid out once at the narrow width instead of twice (first without the lane, then with
    // it) on every switch into the tree.
    private void UpdateScrollLane()
    {
        ScrollBarVisibility want = ScrollBarVisibility.Auto;
        if (_view == View.AllPrograms)
        {
            int rows = 0;
            foreach (UIElement child in _allList.Children)
            {
                if (child.Visibility == Visibility.Visible)
                {
                    rows++;
                }
            }
            if (rows * 22.0 > _layout.V + 0.5)
            {
                want = ScrollBarVisibility.Visible;
            }
        }
        if (_progScroll.VerticalScrollBarVisibility != want)
        {
            _progScroll.VerticalScrollBarVisibility = want;
        }
    }

    // View swap: the outgoing rows are already gone, the incoming list fades in over Cat.Hover.
    private void FadeInView()
    {
        _progScroll.BeginAnimation(OpacityProperty, null);
        _progScroll.Opacity = 1.0;
        Duration? d = FadeDur(Motion.Cat.Hover);
        if (!d.HasValue)
        {
            return;
        }
        DoubleAnimation fade = new DoubleAnimation(0.0, 1.0, d.Value)
        {
            EasingFunction = Motion.Ease(Motion.Cat.Hover),
            FillBehavior = FillBehavior.Stop
        };
        fade.Freeze();
        _progScroll.BeginAnimation(OpacityProperty, fade);
    }

    // Empties one container. Rows of that container stop waiting for icons and leave the letter-tile set; the other
    // containers keep theirs.
    private void ClearRows(StackPanel panel)
    {
        if (_iconWaiters.Count > 0)
        {
            List<string>? done = null;
            foreach (KeyValuePair<string, List<(W7Row Row, string Path, int Px)>> kv in _iconWaiters)
            {
                kv.Value.RemoveAll(((W7Row Row, string Path, int Px) w) => ReferenceEquals(w.Row.Parent, panel));
                if (kv.Value.Count == 0)
                {
                    (done ??= new List<string>()).Add(kv.Key);
                }
            }
            if (done != null)
            {
                foreach (string k in done)
                {
                    _iconWaiters.Remove(k);
                }
            }
        }
        if (_letterRows.Count > 0)
        {
            _letterRows.RemoveWhere((W7Row r) => ReferenceEquals(r.Parent, panel));
        }
        panel.Children.Clear();
        DropDetachedSelection();
    }

    // A grey message row (no matches, or the inventory is still loading).
    private static W7Row MessageRow(string text)
    {
        W7Row row = new W7Row(W7Row.Kind.Message22, onGlass: false)
        {
            Tag = "w7:message"
        };
        row.SetText(text);
        return row;
    }

    // Authentic Win7 left column: user-pinned programs on top, a separator, then the recent list. keepHeight: rebuilt
    // while the menu stays open (a pin or a removal), so the window keeps its height and the recent list gives way to the
    // pins, as Windows 7 did.
    private void RefreshMain(bool keepHeight = false)
    {
        ClearRows(_mainList);
        List<AppEntry> apps = LoadApps();
        if (apps.Count == 0)
        {
            // The Start screen has not delivered its inventory yet (boot); AppsReloaded refreshes this view.
            _mainList.Children.Add(MessageRow(LoadingText));
            _mainPins = 0;
            _mainRecent = 0;
            _progScroll.ScrollToTop();
            return;
        }
        AppSettings? snap = SnapshotForRead();
        List<string> pinList = PinsRead(snap);
        HashSet<string> pinPaths = new HashSet<string>(pinList, StringComparer.OrdinalIgnoreCase);
        HashSet<string> hidden = new HashSet<string>(HiddenRead(snap), StringComparer.OrdinalIgnoreCase);
        List<AppEntry> pinned = pinList
            .Select((string p) => apps.FirstOrDefault((AppEntry a) => string.Equals(a.LaunchPath, p, StringComparison.OrdinalIgnoreCase)))
            .Where((AppEntry? a) => a != null).Select((AppEntry? a) => a!).ToList();
        _recentMax = RecentMaxFor(pinned.Count, _maxWindowDip);
        if (keepHeight)
        {
            double pinsBlock = pinned.Count * 36.0 + (pinned.Count > 0 ? 9.0 : 0.0);
            _recentMax = Math.Min(_recentMax, Math.Max(0, (int)Math.Floor((_layout.V - pinsBlock) / 36.0)));
        }
        List<AppEntry> recent = RecentPrograms(apps, pinPaths, hidden, _recentMax);
        foreach (AppEntry a in pinned)
        {
            _mainList.Children.Add(ProgramRow(a, W7Row.Kind.Program36, isRecent: false));
        }
        if (pinned.Count > 0 && recent.Count > 0)
        {
            _mainList.Children.Add(PinSeparator());
        }
        foreach (AppEntry a in recent)
        {
            _mainList.Children.Add(ProgramRow(a, W7Row.Kind.Program36, isRecent: true));
        }
        _mainPins = pinned.Count;
        _mainRecent = recent.Count;
        _progScroll.ScrollToTop();
    }

    // The recent list: programs launched before (not pinned, not removed, not a help, setup or uninstall entry), ranked
    // by launch count halved every 14 days since the last launch, then by last use and name. When fewer remain than the
    // list has room for, the seed programs present in the inventory fill up; there is never an alphabetical filler.
    // Empty while Start_TrackProgs is 0.
    private List<AppEntry> RecentPrograms(List<AppEntry> apps, HashSet<string> pinPaths, HashSet<string> hidden, int max)
    {
        List<AppEntry> recent = new List<AppEntry>(Math.Max(0, max));
        if (max <= 0 || !_trackProgs)
        {
            return recent;
        }
        long now = DateTime.UtcNow.Ticks;
        List<(AppEntry App, double Score, long Last)> used = new List<(AppEntry App, double Score, long Last)>();
        foreach (AppEntry a in apps)
        {
            if (pinPaths.Contains(a.LaunchPath) || hidden.Contains(a.LaunchPath))
            {
                continue;
            }
            (int count, long last) = UsageOf(a.LaunchPath);
            if (count <= 0 || IsExcludedFromRecent(a.Name))
            {
                continue;
            }
            used.Add((a, RecentScore(count, last, now), last));
        }
        recent.AddRange(used
            .OrderByDescending(((AppEntry App, double Score, long Last) u) => u.Score)
            .ThenByDescending(((AppEntry App, double Score, long Last) u) => u.Last)
            .ThenBy(((AppEntry App, double Score, long Last) u) => u.App.Name, StringComparer.CurrentCultureIgnoreCase)
            .Take(max)
            .Select(((AppEntry App, double Score, long Last) u) => u.App));
        foreach (string seed in RecentSeeds)
        {
            if (recent.Count >= max)
            {
                break;
            }
            AppEntry? s = apps.FirstOrDefault((AppEntry a) => string.Equals(a.Name, seed, StringComparison.OrdinalIgnoreCase));
            if (s != null && !pinPaths.Contains(s.LaunchPath) && !hidden.Contains(s.LaunchPath) && !recent.Contains(s))
            {
                recent.Add(s);
            }
        }
        return recent;
    }

    internal static double RecentScore(int count, long lastTicks, long nowTicks)
    {
        double days = Math.Max(0.0, (nowTicks - lastTicks) / (double)TimeSpan.TicksPerDay);
        return count * Math.Pow(0.5, days / RecentHalfLifeDays);
    }

    internal static bool IsExcludedFromRecent(string name)
    {
        return !string.IsNullOrEmpty(name) && RecentExcluded.IsMatch(name);
    }

    // HKCU ...\Explorer\Advanced Start_TrackProgs; a missing value means on.
    internal static bool ReadTrackProgs()
    {
        try
        {
            using RegistryKey? k = Registry.CurrentUser.OpenSubKey("Software\\Microsoft\\Windows\\CurrentVersion\\Explorer\\Advanced");
            object? v = k?.GetValue("Start_TrackProgs");
            return v is not int i || i != 0;
        }
        catch
        {
            return true;
        }
    }

    // After a pin or 'Remove from this list' while the menu stays open: the main view is rebuilt in place, with the same
    // height and scroll position. Other views pick the change up the next time the main view is built.
    private void RefreshAfterListChange()
    {
        if (!IsVisible || _hidePending || _dismissing || _view != View.Main)
        {
            return;
        }
        double offset = _progScroll.VerticalOffset;
        RefreshMain(keepHeight: true);
        _progScroll.ScrollToVerticalOffset(offset);
        ArmHoverGuard();
    }

    // A program the user removed from the recent list comes back after it is launched from this menu.
    private void UnhideOnLaunch(string path)
    {
        try
        {
            if (!HiddenRead(SnapshotForRead()).Any((string p) => string.Equals(p, path, StringComparison.OrdinalIgnoreCase)))
            {
                return;
            }
            HiddenUpdate(delegate (List<string> list)
            {
                list.RemoveAll((string p) => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
            });
        }
        catch (Exception ex)
        {
            Trace("Win7 Start: hidden-list update failed: " + ex.Message);
        }
    }

    // 'See more results': the shell's own search window for the typed text; the menu closes.
    private void SeeMoreResults(string q)
    {
        string target = "search-ms:query=" + Uri.EscapeDataString(q);
        LaunchAndClose("see more results", delegate
        {
            Act(target, delegate
            {
                FileShell.Open(target);
            });
        });
    }

    // ---- All Programs -----------------------------------------------------------------------------------------------

    // One entry of the All Programs tree: a program (at the root, or a folder's child at depth 1) or a folder.
    private sealed class TreeNode
    {
        internal TreeNode(AppEntry? app, string? folder, int depth)
        {
            App = app;
            Folder = folder;
            Depth = depth;
        }

        internal AppEntry? App { get; }

        internal string? Folder { get; }

        internal int Depth { get; }

        internal bool IsExpanded { get; set; }

        // A folder's programs, sorted by name.
        internal List<TreeNode>? Children { get; set; }

        internal W7Row? Row { get; set; }

        internal bool ChildRowsBuilt { get; set; }

        internal string Name => App?.Name ?? Folder ?? string.Empty;
    }

    // The All Programs model in display order: root programs, then folders (their children hang off them).
    private readonly List<TreeNode> _allNodes = new List<TreeNode>();

    // The inventory revision All Programs was built for: the provider's list reference, its count, the icon scale and the
    // shortcut index (in QA also the injected extra entries). _allStale forces the next use to rebuild (the inventory was
    // reloaded).
    private bool _allBuilt;

    private object? _allRevSource;

    private int _allRevCount = -1;

    private double _allRevScale;

    private string _allRevExtra = string.Empty;

    // The shortcut index the tree's folders came from (null: built before the index was ready).
    private object? _allRevIndex;

    private bool _allStale;

    // How many times the tree was built (QA reads it).
    private int _allBuilds;

    // Builds All Programs when the inventory revision changed; otherwise the kept rows are reused as they are. Returns
    // true when it built.
    private bool EnsureAllPrograms()
    {
        IReadOnlyList<AppEntry>? src = SourceApps();
        int count = src?.Count ?? 0;
        string extra = _qa ? QaExtraSignature() : string.Empty;
        ShortcutIndex? index = Volatile.Read(ref s_shortcuts);
        if (_allBuilt && !_allStale && ReferenceEquals(src, _allRevSource) && count == _allRevCount && _allRevScale == _iconScale && string.Equals(extra, _allRevExtra, StringComparison.Ordinal)
            && ReferenceEquals(index, _allRevIndex))
        {
            return false;
        }
        BuildAllPrograms();
        _allBuilt = true;
        _allStale = false;
        _allRevSource = src;
        _allRevCount = count;
        _allRevScale = _iconScale;
        _allRevExtra = extra;
        _allRevIndex = index;
        _allBuilds++;
        return true;
    }

    // Root programs (no Start menu folder) first, then one folder per Start menu folder (TreeFolder: the folder of the
    // program's shortcut, or its Category), both by name. Only the root rows and the folder rows are created here; a
    // folder's programs get their rows on its first expand.
    private void BuildAllPrograms()
    {
        ClearRows(_allList);
        _allNodes.Clear();
        List<AppEntry> apps = LoadApps();
        if (apps.Count == 0)
        {
            _allList.Children.Add(MessageRow(LoadingText));
            return;
        }
        StringComparer byName = StringComparer.CurrentCultureIgnoreCase;
        List<(AppEntry App, string Folder)> placed = apps.Select((AppEntry a) => (a, TreeFolder(a))).ToList();
        foreach ((AppEntry a, string _) in placed.Where(((AppEntry App, string Folder) x) => x.Folder.Length == 0).OrderBy(((AppEntry App, string Folder) x) => x.App.Name, byName))
        {
            TreeNode node = new TreeNode(a, null, 0);
            node.Row = ProgramRow(a, W7Row.Kind.Program22, isRecent: false);
            _allNodes.Add(node);
            _allList.Children.Add(node.Row);
        }
        IEnumerable<IGrouping<string, AppEntry>> folders = placed
            .Where(((AppEntry App, string Folder) x) => x.Folder.Length > 0)
            .GroupBy(((AppEntry App, string Folder) x) => x.Folder, ((AppEntry App, string Folder) x) => x.App, StringComparer.OrdinalIgnoreCase)
            .OrderBy((IGrouping<string, AppEntry> g) => g.Key, byName);
        foreach (IGrouping<string, AppEntry> g in folders)
        {
            TreeNode folder = new TreeNode(null, g.Key, 0)
            {
                Children = g.OrderBy((AppEntry x) => x.Name, byName).Select((AppEntry x) => new TreeNode(x, null, 1)).ToList()
            };
            folder.Row = FolderRow(folder);
            _allNodes.Add(folder);
            _allList.Children.Add(folder.Row);
        }
    }

    private W7Row FolderRow(TreeNode folder)
    {
        W7Row row = new W7Row(W7Row.Kind.Folder22, onGlass: false)
        {
            Tag = "w7:folder",
            Payload = folder,
            ToolTipFactory = W7ToolTip
        };
        row.SetText(folder.Name);
        SetFolderIcon(row, open: false);
        WireRow(row);
        return row;
    }

    // The shell's folder icons (closed, and SIID_FOLDEROPEN when expanded), fitted to the 16 px box at the monitor scale.
    // Recent Windows versions draw SIID_FOLDEROPEN exactly like the closed folder; then an open folder is drawn in code
    // from the closed icon's colours (W7Icons.OpenFolder), so an expanded folder still reads as open.
    private static ImageSource? s_folderClosed;

    private static ImageSource? s_folderOpen;

    private static int s_folderPx;

    // True when the open icon is the one drawn in code (QA reads it).
    private static bool s_folderOpenDrawn;

    private void SetFolderIcon(W7Row row, bool open)
    {
        int px = Math.Max(16, (int)Math.Round(16.0 * _iconScale, MidpointRounding.AwayFromZero));
        if (s_folderPx != px)
        {
            s_folderPx = px;
            s_folderClosed = null;
            s_folderOpen = null;
        }
        if (s_folderClosed == null && TaskbarContextMenu.ShellFolderIcon() is ImageSource closed)
        {
            s_folderClosed = W7Icons.Fit(closed, px);
        }
        if (s_folderOpen == null)
        {
            ImageSource? stock = TaskbarContextMenu.StockIcon(4) is ImageSource opened ? W7Icons.Fit(opened, px) : null;
            s_folderOpenDrawn = stock == null || W7Icons.SamePixels(stock, s_folderClosed);
            s_folderOpen = s_folderOpenDrawn ? W7Icons.OpenFolder(s_folderClosed, px) : stock;
        }
        ImageSource? img = open ? (s_folderOpen ?? s_folderClosed) : s_folderClosed;
        row.SetIcon(img, null, IconPlate.None, null, alwaysPlate: false, _paletteIsDark);
    }

    // Opens or closes a folder in place. Its programs are inserted right after it at depth 1 (created on the first
    // expand, shown and hidden afterwards). After expanding, the last child and then the folder row are brought into view,
    // so as many children as fit are visible without the folder scrolling away.
    private void ToggleFolder(TreeNode folder, bool? expand, bool bringIntoView)
    {
        if (folder.Row == null || folder.Children == null)
        {
            return;
        }
        bool open = expand ?? !folder.IsExpanded;
        if (open == folder.IsExpanded)
        {
            return;
        }
        if (open)
        {
            if (!folder.ChildRowsBuilt)
            {
                int at = _allList.Children.IndexOf(folder.Row) + 1;
                foreach (TreeNode child in folder.Children)
                {
                    W7Row row = ProgramRow(child.App!, W7Row.Kind.Program22, isRecent: false, depth: 1);
                    row.ParentRow = folder.Row;
                    child.Row = row;
                    _allList.Children.Insert(at++, row);
                }
                folder.ChildRowsBuilt = true;
            }
            else
            {
                foreach (TreeNode child in folder.Children)
                {
                    if (child.Row != null)
                    {
                        child.Row.Visibility = Visibility.Visible;
                    }
                }
                RetryLetterRows();
            }
            folder.IsExpanded = true;
            SetFolderIcon(folder.Row, open: true);
            UpdateScrollLane();
            if (bringIntoView && IsVisible && folder.Children.Count > 0 && folder.Children[^1].Row is W7Row last)
            {
                _progScroll.UpdateLayout();
                last.BringIntoView();
                _progScroll.UpdateLayout();
                folder.Row.BringIntoView();
            }
        }
        else
        {
            foreach (TreeNode child in folder.Children)
            {
                if (child.Row == null)
                {
                    continue;
                }
                if (ReferenceEquals(_sel, child.Row))
                {
                    Select(Zone.Left, folder.Row, _selByKeyboard, animate: false);
                }
                if (ReferenceEquals(_pressed, child.Row))
                {
                    CancelPress();
                }
                child.Row.SetHot(on: false, animate: false);
                child.Row.Visibility = Visibility.Collapsed;
            }
            folder.IsExpanded = false;
            SetFolderIcon(folder.Row, open: false);
            UpdateScrollLane();
            if (_lastLeft != null && !InCurrentList(_lastLeft))
            {
                _lastLeft = null;
            }
        }
        // Rows that slid under a resting pointer are not a hover.
        ArmHoverGuard();
    }

    // Every open of the menu starts with all folders closed.
    private void CollapseAllFolders()
    {
        foreach (TreeNode node in _allNodes)
        {
            if (node.IsExpanded)
            {
                ToggleFolder(node, expand: false, bringIntoView: false);
            }
        }
    }

    // ---- Search -----------------------------------------------------------------------------------------------------

    internal readonly record struct Ranked(AppEntry App, int Score, int Match);

    // Matches of the last search (the header shows them all, the list only the row budget) and how long ranking took.
    private int _searchMatches;

    private double _lastRankMs;

    // The first keystroke of an open is traced, later ones only when they are slow.
    private bool _keystrokeTraced;

    // Ranked results, rebuilt per keystroke: 'Programs (n)', at most floor((V - 24) / 22) program rows with the first one
    // selected, and the command row turned into See more results. No match: a message row instead.
    private void RefreshSearch(string raw)
    {
        Stopwatch sw = Stopwatch.StartNew();
        ClearRows(_searchList);
        List<AppEntry> apps = LoadApps();
        if (apps.Count == 0)
        {
            _searchMatches = 0;
            _searchList.Children.Add(MessageRow(LoadingText));
            _progScroll.ScrollToTop();
            return;
        }
        EnsureShortcutIndex();
        Stopwatch rank = Stopwatch.StartNew();
        List<Ranked> ranked = RankSearch(apps, raw);
        _lastRankMs = rank.Elapsed.TotalMilliseconds;
        _searchMatches = ranked.Count;
        if (ranked.Count == 0)
        {
            _searchList.Children.Add(MessageRow(NoMatchText));
        }
        else
        {
            W7Row header = new W7Row(W7Row.Kind.Header24, onGlass: false)
            {
                Tag = "w7:header"
            };
            header.SetText($"Programs ({ranked.Count})");
            _searchList.Children.Add(header);
            int budget = Math.Max(1, (int)Math.Floor((_layout.V - 24.0) / 22.0));
            W7Row? first = null;
            foreach (Ranked r in ranked.Take(budget))
            {
                W7Row row = ProgramRow(r.App, W7Row.Kind.Program22, isRecent: false);
                first ??= row;
                _searchList.Children.Add(row);
            }
            if (first != null)
            {
                // The first result is the virtual cursor's position: Enter launches it, Down moves on from it. It is an
                // automatic selection, so the caret keys still edit the query (SearchBoxKeepsKey).
                Select(Zone.Left, first, byKeyboard: true, animate: false);
                _selAuto = true;
            }
        }
        _progScroll.ScrollToTop();
        double ms = sw.Elapsed.TotalMilliseconds;
        if (!_keystrokeTraced || ms >= 8.0)
        {
            _keystrokeTraced = true;
            Trace($"Win7 Start: search {raw.Length} chars -> {ranked.Count} matches in {ms:0.0}ms (ranking {_lastRankMs:0.00}ms)");
        }
    }

    // score = max(name match, Greek-layout recovery - 1, shortcut target match (TargetScore)) + usage bonus. A program
    // needs a match; the usage bonus only orders programs within a tier. Ordered by score, then by name.
    private List<Ranked> RankSearch(List<AppEntry> apps, string raw)
    {
        List<Ranked> res = new List<Ranked>();
        string q = (raw ?? string.Empty).Trim().ToLowerInvariant();
        if (q.Length == 0)
        {
            return res;
        }
        string qg = GreekLayout.ToLatinKeys(q);
        Dictionary<string, string>? targets = TargetNamesMap;
        foreach (AppEntry a in apps)
        {
            int m = SearchScore.Name(a.Name, q);
            if (qg.Length > 0)
            {
                m = Math.Max(m, SearchScore.Name(a.Name, qg) - 1);
            }
            if (targets != null && targets.TryGetValue(a.Name, out string? target))
            {
                m = Math.Max(m, TargetScore(target, q));
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

    // A typed command matched against a shortcut's target name, around the WordStart tier and far below the name tiers
    // above it. Only exact, prefix and word-start matches count (acronym, substring and typo tiers mean nothing for a file
    // name). The whole command ('cmd', 'regedit') scores one point above WordStart, so it comes before programs that only
    // have it as a word of their name (Git CMD); part of a command scores one point below, so a program whose own name
    // matches at WordStart comes first.
    internal static int TargetScore(string target, string q)
    {
        int t = SearchScore.Name(target, q);
        if (t == SearchScore.Exact)
        {
            return SearchScore.WordStart + 1;
        }
        return (t == SearchScore.Prefix || t == SearchScore.WordStart) ? SearchScore.WordStart - 1 : 0;
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

    // ---- Start menu shortcuts --------------------------------------------------------------------------------------

    // What the Start menu's own shortcut files say about the programs. Read once per process from every *.lnk and *.url
    // under both Programs folders (the Startup folders are skipped) on a background task, synchronously in QA:
    // - FolderByName / FolderByTarget: the All Programs folder of a shortcut (its first-level folder, empty at the root),
    //   by file name, by the shell's display name of the file (which applies desktop.ini LocalizedFileNames, so
    //   VoiceAccess.lnk is found as 'Voice access') and by its target path or web address (an AppsFolder entry's parsing
    //   name is that path or address). When a key exists both at the root and in a folder, the folder wins.
    // - TargetNames: the target file name a typed command finds a program by ('regedit' -> Registry Editor).
    // StartMenuIndex, which the Start screen shares, is not used or changed here. Lookups ignore the index until it is
    // ready.
    private sealed class ShortcutIndex
    {
        internal readonly Dictionary<string, string> FolderByName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        internal readonly Dictionary<string, string> FolderByTarget = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        internal readonly Dictionary<string, string> TargetNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        internal int Files;

        internal double BuildMs;
    }

    private static ShortcutIndex? s_shortcuts;

    private static int s_shortcutsStarted;

    // Programs that only host what their arguments name: started with arguments they are a prompt, an applet, a script, a
    // web app or a document, never the program a typed command means. Started without arguments they are the program
    // itself (Command Prompt is cmd.exe), so the target name is kept then.
    private static readonly HashSet<string> HostTargets = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "cmd", "conhost", "control", "mmc", "rundll32", "explorer", "msiexec", "notepad", "wscript", "cscript", "powershell",
        "pwsh", "wt", "java", "javaw", "python", "pythonw", "chrome_proxy", "msedge_proxy", "appvlp", "electron", "dllhost"
    };

    // Starts the one build of the index. onReady is posted to the dispatcher when a background build is done (only for
    // the caller that started it).
    internal static void EnsureShortcutIndex(bool synchronous, Dispatcher? dispatcher = null, Action? onReady = null)
    {
        if (Interlocked.CompareExchange(ref s_shortcutsStarted, 1, 0) != 0)
        {
            return;
        }
        if (synchronous)
        {
            Volatile.Write(ref s_shortcuts, BuildShortcutIndex());
            return;
        }
        Task.Run(delegate
        {
            Volatile.Write(ref s_shortcuts, BuildShortcutIndex());
            if (dispatcher != null && onReady != null)
            {
                try
                {
                    dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, onReady);
                }
                catch
                {
                }
            }
        });
    }

    private void EnsureShortcutIndex()
    {
        EnsureShortcutIndex(synchronous: _qa, Dispatcher, OnShortcutIndexReady);
    }

    // The index arrived after All Programs was built from the inventory's own folders (the prewarm right after logon): the
    // tree is built again with the shortcut folders, at idle while the menu is hidden, or in place while it shows the tree.
    private void OnShortcutIndexReady()
    {
        if (_closed)
        {
            return;
        }
        ShortcutIndex? idx = Volatile.Read(ref s_shortcuts);
        Trace($"Win7 Start: shortcut index ready ({idx?.Files ?? 0} files, {idx?.BuildMs ?? 0.0:0} ms on a background thread)");
        if (!_allBuilt)
        {
            return;
        }
        if (IsVisible && !_dismissing && !_hidePending)
        {
            if (_view == View.AllPrograms)
            {
                RefreshInPlace();
            }
            return;
        }
        if (_prewarmed)
        {
            QueuePrewarm();
        }
    }

    private static ShortcutIndex BuildShortcutIndex()
    {
        Stopwatch sw = Stopwatch.StartNew();
        ShortcutIndex idx = new ShortcutIndex();
        // The display names come from the shell, which needs COM on this thread (a pool thread joins the MTA; the QA
        // harness runs on its STA thread, where this call fails harmlessly and nothing is uninitialised).
        int com = CoInitializeEx(IntPtr.Zero, 0);
        try
        {
            List<string> skip = new List<string>();
            foreach (Environment.SpecialFolder sf in new[] { Environment.SpecialFolder.Startup, Environment.SpecialFolder.CommonStartup })
            {
                string s = SafeFolderPath(sf);
                if (s.Length > 0)
                {
                    skip.Add(s.TrimEnd('\\') + "\\");
                }
            }
            List<(string Name, string Stem, string Target, bool Args)> links = new List<(string Name, string Stem, string Target, bool Args)>();
            // Folders with a desktop.ini (the only place LocalizedFileNames can rename a shortcut); the shell is asked for
            // display names only there.
            Dictionary<string, bool> localized = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            foreach (Environment.SpecialFolder sf in new[] { Environment.SpecialFolder.CommonStartMenu, Environment.SpecialFolder.StartMenu })
            {
                string root = SafeFolderPath(sf);
                if (root.Length == 0)
                {
                    continue;
                }
                string programs = Path.Combine(root, "Programs");
                if (!Directory.Exists(programs))
                {
                    continue;
                }
                try
                {
                    EnumerationOptions opts = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true };
                    foreach (string file in Directory.EnumerateFiles(programs, "*", opts))
                    {
                        if (skip.Any((string s) => file.StartsWith(s, StringComparison.OrdinalIgnoreCase)))
                        {
                            continue;
                        }
                        try
                        {
                            AddShortcut(idx, links, programs, file, localized);
                        }
                        catch
                        {
                        }
                    }
                }
                catch
                {
                }
            }
            BuildTargetNames(idx, links);
        }
        catch
        {
        }
        finally
        {
            if (com >= 0)
            {
                CoUninitialize();
            }
        }
        idx.BuildMs = sw.Elapsed.TotalMilliseconds;
        return idx;
    }

    private static string SafeFolderPath(Environment.SpecialFolder sf)
    {
        try
        {
            return Environment.GetFolderPath(sf) ?? string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static void AddShortcut(ShortcutIndex idx, List<(string Name, string Stem, string Target, bool Args)> links, string programs, string file, Dictionary<string, bool> localized)
    {
        string ext = Path.GetExtension(file);
        bool lnk = ext.Equals(".lnk", StringComparison.OrdinalIgnoreCase);
        if (!lnk && !ext.Equals(".url", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }
        string dir = Path.GetDirectoryName(file) ?? programs;
        string rel = Path.GetRelativePath(programs, dir);
        string folder = (rel == "." || rel.Length == 0) ? string.Empty : rel.Split(Path.DirectorySeparatorChar)[0];
        string stem = Path.GetFileNameWithoutExtension(file);
        if (!localized.TryGetValue(dir, out bool hasIni))
        {
            hasIni = File.Exists(Path.Combine(dir, "desktop.ini"));
            localized[dir] = hasIni;
        }
        string? display = hasIni ? ShellDisplayName(file) : null;
        idx.Files++;
        PutFolder(idx.FolderByName, stem, folder);
        if (!string.IsNullOrWhiteSpace(display))
        {
            PutFolder(idx.FolderByName, display, folder);
        }
        string? target;
        bool args = false;
        if (lnk)
        {
            byte[] bytes = File.ReadAllBytes(file);
            // Windows' own tool shortcuts (Registry Editor, System Configuration, ...) carry no LinkInfo, only an
            // environment-variable target such as %windir%\regedit.exe.
            target = ShellLink.GetTargetPath(bytes) ?? EnvironmentTarget(bytes);
            args = HasArguments(bytes);
        }
        else
        {
            target = UrlOf(file);
        }
        string? key = NormalizeTarget(target);
        if (key == null)
        {
            return;
        }
        PutFolder(idx.FolderByTarget, key, folder);
        if (lnk)
        {
            links.Add((string.IsNullOrWhiteSpace(display) ? stem : display!, stem, key, args));
        }
    }

    private static void PutFolder(Dictionary<string, string> map, string key, string folder)
    {
        if (!map.TryGetValue(key, out string? cur) || (cur.Length == 0 && folder.Length > 0))
        {
            map[key] = folder;
        }
    }

    // Typed-command names: the target file name of a program's shortcut, when that names the program and nothing else.
    // - only .exe targets with a name of at least 3 characters (a document or a web page is not a command);
    // - a host program (HostTargets) started with arguments is something else and gets no name;
    // - a target shared by several programs belongs to the one that starts it without arguments, and to nobody when none
    //   or several do. So 'cmd' finds Command Prompt but not the developer prompts that run cmd.exe with a script, and
    //   'chrome' never finds the Chrome web apps through chrome_proxy.
    private static void BuildTargetNames(ShortcutIndex idx, List<(string Name, string Stem, string Target, bool Args)> links)
    {
        foreach (IGrouping<string, (string Name, string Stem, string Target, bool Args)> g in links
            .Where(((string Name, string Stem, string Target, bool Args) l) => l.Target.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            .GroupBy(((string Name, string Stem, string Target, bool Args) l) => Path.GetFileNameWithoutExtension(l.Target).ToLowerInvariant()))
        {
            string cmd = g.Key;
            if (cmd.Length < 3)
            {
                continue;
            }
            List<(string Name, string Stem, string Target, bool Args)> owners = g.Where(((string Name, string Stem, string Target, bool Args) l) => !(l.Args && HostTargets.Contains(cmd))).ToList();
            List<string> names = owners.Select(((string Name, string Stem, string Target, bool Args) l) => l.Name).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (names.Count > 1)
            {
                owners = owners.Where(((string Name, string Stem, string Target, bool Args) l) => !l.Args).ToList();
                names = owners.Select(((string Name, string Stem, string Target, bool Args) l) => l.Name).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            }
            if (names.Count != 1)
            {
                continue;
            }
            foreach ((string name, string stem, string _, bool _) in owners)
            {
                idx.TargetNames[name] = cmd;
                idx.TargetNames[stem] = cmd;
            }
        }
    }

    // The target names, or null until the index is ready.
    internal static Dictionary<string, string>? TargetNamesMap => Volatile.Read(ref s_shortcuts)?.TargetNames;

    // The All Programs folder of a program: the folder of its Start menu shortcut when the index knows it (by name, then
    // by target), otherwise the inventory's own Category. Empty means the root.
    private static string TreeFolder(AppEntry a)
    {
        ShortcutIndex? idx = Volatile.Read(ref s_shortcuts);
        if (idx != null)
        {
            if (!string.IsNullOrEmpty(a.Name) && idx.FolderByName.TryGetValue(a.Name, out string? byName))
            {
                return byName;
            }
            string? t = AppsFolderTarget(a.LaunchPath);
            if (t != null && idx.FolderByTarget.TryGetValue(t, out string? byTarget))
            {
                return byTarget;
            }
        }
        return a.Category ?? string.Empty;
    }

    // The path or web address an AppsFolder entry stands for. Its parsing name is either a web address (a .url shortcut)
    // or a known-folder id and a relative path ('{1AC14E77-...}\magnify.exe'). Null for anything else.
    internal static string? AppsFolderTarget(string? launchPath)
    {
        const string prefix = "shell:AppsFolder\\";
        if (string.IsNullOrEmpty(launchPath) || !launchPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }
        string parse = launchPath.Substring(prefix.Length);
        if (parse.Contains("://", StringComparison.Ordinal))
        {
            return NormalizeTarget(parse);
        }
        int slash = parse.IndexOf('\\');
        if (slash <= 0 || slash >= parse.Length - 1 || !Guid.TryParse(parse.AsSpan(0, slash), out Guid id))
        {
            return null;
        }
        string? root = KnownFolderPath(id);
        return root == null ? null : NormalizeTarget(Path.Combine(root, parse.Substring(slash + 1)));
    }

    // A web address without its trailing slash, or a full file path; compared ignoring case. Null when empty.
    internal static string? NormalizeTarget(string? target)
    {
        if (string.IsNullOrWhiteSpace(target))
        {
            return null;
        }
        string t = target.Trim().Trim('"');
        if (t.Contains("://", StringComparison.Ordinal))
        {
            return t.TrimEnd('/');
        }
        try
        {
            return Path.GetFullPath(Environment.ExpandEnvironmentVariables(t));
        }
        catch
        {
            return t;
        }
    }

    private static readonly Dictionary<Guid, string?> s_knownFolders = new Dictionary<Guid, string?>();

    private static string? KnownFolderPath(Guid id)
    {
        lock (s_knownFolders)
        {
            if (s_knownFolders.TryGetValue(id, out string? cached))
            {
                return cached;
            }
        }
        string? path = null;
        nint p = IntPtr.Zero;
        try
        {
            if (SHGetKnownFolderPath(ref id, 0u, IntPtr.Zero, out p) == 0 && p != IntPtr.Zero)
            {
                path = System.Runtime.InteropServices.Marshal.PtrToStringUni(p);
            }
        }
        catch
        {
        }
        finally
        {
            if (p != IntPtr.Zero)
            {
                System.Runtime.InteropServices.Marshal.FreeCoTaskMem(p);
            }
        }
        lock (s_knownFolders)
        {
            s_knownFolders[id] = path;
        }
        return path;
    }

    // The address of a .url file (URL= in its [InternetShortcut] section).
    private static string? UrlOf(string file)
    {
        bool section = false;
        foreach (string raw in File.ReadLines(file))
        {
            string line = raw.Trim();
            if (line.StartsWith("[", StringComparison.Ordinal))
            {
                section = line.Equals("[InternetShortcut]", StringComparison.OrdinalIgnoreCase);
                continue;
            }
            if (section && line.StartsWith("URL=", StringComparison.OrdinalIgnoreCase))
            {
                return line.Substring(4).Trim();
            }
        }
        return null;
    }

    // The name the shell shows for a file (no extension, desktop.ini LocalizedFileNames applied). Null on failure.
    internal static string? ShellDisplayName(string file)
    {
        try
        {
            SHFILEINFOW info = default;
            nint ok = SHGetFileInfoW(file, 0u, ref info, (uint)System.Runtime.InteropServices.Marshal.SizeOf<SHFILEINFOW>(), SHGFI_DISPLAYNAME);
            return ok != IntPtr.Zero && !string.IsNullOrWhiteSpace(info.szDisplayName) ? info.szDisplayName : null;
        }
        catch
        {
            return null;
        }
    }

    // Whether a shortcut passes command-line arguments (the StringData COMMAND_LINE_ARGUMENTS of the .lnk format), not
    // counting blank ones.
    internal static bool HasArguments(byte[] lnk)
    {
        try
        {
            if (lnk.Length < 76 || BitConverter.ToUInt32(lnk, 0) != 76)
            {
                return false;
            }
            uint flags = BitConverter.ToUInt32(lnk, 20);
            if ((flags & 0x20) == 0)
            {
                return false;
            }
            bool unicode = (flags & 0x80) != 0;
            int pos = 76;
            if ((flags & 0x01) != 0)
            {
                pos += 2 + BitConverter.ToUInt16(lnk, pos);
            }
            if ((flags & 0x02) != 0)
            {
                pos += (int)BitConverter.ToUInt32(lnk, pos);
            }
            // NAME_STRING, RELATIVE_PATH and WORKING_DIR come first, each a character count and the characters.
            foreach (uint f in new uint[] { 0x04, 0x08, 0x10 })
            {
                if ((flags & f) != 0)
                {
                    pos += 2 + BitConverter.ToUInt16(lnk, pos) * (unicode ? 2 : 1);
                }
            }
            int count = BitConverter.ToUInt16(lnk, pos);
            int bytes = count * (unicode ? 2 : 1);
            if (pos + 2 + bytes > lnk.Length)
            {
                return false;
            }
            string args = unicode ? System.Text.Encoding.Unicode.GetString(lnk, pos + 2, bytes) : System.Text.Encoding.Default.GetString(lnk, pos + 2, bytes);
            return !string.IsNullOrWhiteSpace(args);
        }
        catch
        {
            return false;
        }
    }

    private const uint SHGFI_DISPLAYNAME = 0x200;

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential, CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private struct SHFILEINFOW
    {
        public nint hIcon;

        public int iIcon;

        public uint dwAttributes;

        [System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szDisplayName;

        [System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.ByValTStr, SizeConst = 80)]
        public string szTypeName;
    }

    [System.Runtime.InteropServices.DllImport("shell32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern nint SHGetFileInfoW(string pszPath, uint dwFileAttributes, ref SHFILEINFOW psfi, uint cbSizeFileInfo, uint uFlags);

    [System.Runtime.InteropServices.DllImport("shell32.dll")]
    private static extern int SHGetKnownFolderPath(ref Guid rfid, uint dwFlags, nint hToken, out nint ppszPath);

    [System.Runtime.InteropServices.DllImport("ole32.dll")]
    private static extern int CoInitializeEx(nint pvReserved, uint dwCoInit);

    [System.Runtime.InteropServices.DllImport("ole32.dll")]
    private static extern void CoUninitialize();

    // The target of a shortcut's EnvironmentVariableDataBlock (block size 0x314, signature 0xA0000001): a 260-byte ANSI
    // path followed by a 520-byte Unicode one. The Unicode copy wins when present. Null when there is no such block.
    internal static string? EnvironmentTarget(byte[] lnk)
    {
        ReadOnlySpan<byte> head = stackalloc byte[8] { 0x14, 0x03, 0x00, 0x00, 0x01, 0x00, 0x00, 0xA0 };
        int at = lnk.AsSpan().IndexOf(head);
        if (at < 0 || at + 0x314 > lnk.Length)
        {
            return null;
        }
        string unicode = ReadZeroTerminated(lnk, at + 268, 520, unicode: true);
        string target = unicode.Length > 0 ? unicode : ReadZeroTerminated(lnk, at + 8, 260, unicode: false);
        return target.Length > 0 ? Environment.ExpandEnvironmentVariables(target) : null;
    }

    private static string ReadZeroTerminated(byte[] b, int start, int max, bool unicode)
    {
        int end = start;
        int limit = Math.Min(b.Length, start + max);
        if (unicode)
        {
            while (end + 1 < limit && (b[end] != 0 || b[end + 1] != 0))
            {
                end += 2;
            }
            return System.Text.Encoding.Unicode.GetString(b, start, end - start);
        }
        while (end < limit && b[end] != 0)
        {
            end++;
        }
        return System.Text.Encoding.Default.GetString(b, start, end - start);
    }

    // ---- Rows and icons ---------------------------------------------------------------------------------------------

    private W7Row ProgramRow(AppEntry a, W7Row.Kind kind, bool isRecent, int depth = 0)
    {
        W7Row row = new W7Row(kind, onGlass: false, depth)
        {
            Tag = a,
            Payload = a,
            IsRecent = isRecent,
            ToolTipFactory = W7ToolTip
        };
        row.SetText(a.Name);
        BindIcon(row, a);
        WireRow(row);
        row.MouseRightButtonUp += OnProgramRightUp;
        return row;
    }

    // The 9 px block between pinned and recent rows, with a faded 1 px line at y 4.
    private static Border PinSeparator()
    {
        Border line = new Border
        {
            Height = 1.0,
            Margin = new Thickness(12.0, 4.0, 12.0, 4.0)
        };
        line.SetResourceReference(Border.BackgroundProperty, "W7.Sep");
        return new Border
        {
            Height = 9.0,
            Child = line,
            IsHitTestVisible = false,
            Tag = "w7:sep"
        };
    }

    // win81:* and launcher:// entries carry a vector glyph made for a coloured tile; it is shown on a plate.
    private static bool IsGlyphEntry(AppEntry a)
    {
        return a.LaunchPath.StartsWith("win81:", StringComparison.OrdinalIgnoreCase)
            || a.LaunchPath.StartsWith(ActionRouter.Scheme, StringComparison.OrdinalIgnoreCase);
    }

    // Icons at exact size from the W7Icons cache. A missing icon is requested from the worker and the box stays empty at
    // its fixed size until it arrives, so nothing shifts. A cached letter tile is shown at once and, once it is older
    // than W7Icons.LetterRetryMs, requested again; the real icon replaces it when it arrives. Rows left with a letter
    // tile are tracked (_letterRows), so kept All Programs rows are bound again later (RetryLetterRows).
    private void BindIcon(W7Row row, AppEntry a)
    {
        int box = row.IconBox;
        if (box <= 0)
        {
            return;
        }
        int px = Math.Max(16, (int)Math.Round(box * _iconScale, MidpointRounding.AwayFromZero));
        if (IsGlyphEntry(a))
        {
            // These paths have no shell icon. While the Start screen has released the glyph (idle trim), the copy fitted
            // earlier is used, or a letter tile when there is none yet.
            string gkey = "glyph:" + a.LaunchPath + "|" + box + "|" + px;
            W7Icons.Entry ge;
            if (a.Icon != null)
            {
                ge = W7Icons.GetOrAddGlyph(gkey, a.Icon, box, px);
            }
            else if (!W7Icons.TryGetKey(gkey, out ge))
            {
                row.SetIcon(W7Icons.Letter(a.Name, px).Img, null, IconPlate.None, null, alwaysPlate: false, _paletteIsDark);
                NoteLetter(row, isLetter: true);
                return;
            }
            Brush? tile = a.TileBrush;
            if (tile is SolidColorBrush sc && sc.Color.A == 0)
            {
                tile = null;
            }
            row.SetIcon(ge.Img, ge.Plated, IconPlate.None, tile, alwaysPlate: true, _paletteIsDark);
            NoteLetter(row, isLetter: false);
            return;
        }
        if (W7Icons.TryGet(a.LaunchPath, box, px, out W7Icons.Entry e))
        {
            row.SetIcon(e.Img, e.Plated, e.Plate, null, alwaysPlate: false, _paletteIsDark);
            NoteLetter(row, e.IsLetter);
            if (!W7Icons.IsStale(e))
            {
                return;
            }
        }
        W7Icons.Request(a.LaunchPath, box, px, a.Name);
        if (W7Icons.TryGet(a.LaunchPath, box, px, out e) && !W7Icons.IsStale(e))
        {
            row.SetIcon(e.Img, e.Plated, e.Plate, null, alwaysPlate: false, _paletteIsDark);
            NoteLetter(row, e.IsLetter);
            return;
        }
        string key = W7Icons.Key(a.LaunchPath, box, px);
        if (!_iconWaiters.TryGetValue(key, out List<(W7Row Row, string Path, int Px)>? list))
        {
            list = new List<(W7Row Row, string Path, int Px)>();
            _iconWaiters[key] = list;
        }
        if (!list.Exists(((W7Row Row, string Path, int Px) w) => ReferenceEquals(w.Row, row)))
        {
            list.Add((row, a.LaunchPath, px));
        }
    }

    private void OnIconLoaded(string key)
    {
        if (_closed || !_iconWaiters.Remove(key, out List<(W7Row Row, string Path, int Px)>? list))
        {
            return;
        }
        foreach ((W7Row row, string path, int px) in list)
        {
            if (W7Icons.TryGet(path, row.IconBox, px, out W7Icons.Entry e))
            {
                row.SetIcon(e.Img, e.Plated, e.Plate, null, alwaysPlate: false, _paletteIsDark);
                NoteLetter(row, e.IsLetter);
            }
        }
    }

    // Rows that show a letter tile: no icon was found (a cold shell namespace after logon, an app being updated) or a
    // glyph entry's glyph was released. Main and search rows are rebuilt on every use, but All Programs rows are kept
    // and BindIcon only runs when a row is created, so these rows are bound again when they are shown (RetryLetterRows).
    private readonly HashSet<W7Row> _letterRows = new HashSet<W7Row>();

    private void NoteLetter(W7Row row, bool isLetter)
    {
        if (isLetter)
        {
            _letterRows.Add(row);
        }
        else
        {
            _letterRows.Remove(row);
        }
    }

    // Binds the shown rows that still carry a letter tile again: a letter older than W7Icons.LetterRetryMs is requested
    // once more and the real icon replaces it when it arrives; a glyph entry picks its glyph up again. Only rows of the
    // current list that are visible, so the work is bounded and only happens while the menu is in use.
    private void RetryLetterRows()
    {
        if (_letterRows.Count == 0)
        {
            return;
        }
        _letterRows.RemoveWhere((W7Row r) => r.Parent == null);
        List<W7Row>? due = null;
        foreach (W7Row row in _letterRows)
        {
            if (row.Payload is AppEntry && InCurrentList(row))
            {
                (due ??= new List<W7Row>()).Add(row);
            }
        }
        if (due == null)
        {
            return;
        }
        foreach (W7Row row in due)
        {
            BindIcon(row, (AppEntry)row.Payload!);
        }
    }

    // Plates depend on the theme: re-evaluated in place after a palette swap, in every container (All Programs rows and
    // the children of closed folders included, since they are kept).
    private void RefreshPlates()
    {
        foreach (StackPanel? panel in new[] { _mainList, _allList, _searchList })
        {
            if (panel == null)
            {
                continue;
            }
            foreach (UIElement child in panel.Children)
            {
                if (child is W7Row row)
                {
                    row.UpdatePlate(_paletteIsDark);
                }
            }
        }
    }

    // ---- Inventory reloads ------------------------------------------------------------------------------------------

    // StartScreen loaded or changed its inventory. An open menu refreshes the view it shows in place and keeps the scroll
    // offset; a hidden one only marks All Programs stale (the main view is rebuilt on every open anyway). A menu that was
    // prewarmed before the inventory arrived (the usual case at logon) is prewarmed again at idle, so its first open does
    // not wait for icons.
    private void OnAppsReloaded()
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke((Action)OnAppsReloaded);
            return;
        }
        if (_closed)
        {
            return;
        }
        _allStale = true;
        if (IsVisible && !_dismissing && !_hidePending)
        {
            RefreshInPlace();
            return;
        }
        if (_prewarmed)
        {
            QueuePrewarm();
        }
    }

    private void RefreshInPlace()
    {
        double offset = _progScroll.VerticalOffset;
        switch (_view)
        {
        case View.AllPrograms:
        {
            HashSet<string> open = new HashSet<string>(_allNodes.Where((TreeNode n) => n.IsExpanded).Select((TreeNode n) => n.Name), StringComparer.OrdinalIgnoreCase);
            EnsureAllPrograms();
            foreach (TreeNode node in _allNodes)
            {
                if (node.Folder != null && open.Contains(node.Folder))
                {
                    ToggleFolder(node, expand: true, bringIntoView: false);
                }
            }
            break;
        }
        case View.Search:
            RefreshSearch(_search.Text?.Trim() ?? string.Empty);
            break;
        default:
            RefreshMain(keepHeight: true);
            break;
        }
        UpdateScrollLane();
        _progScroll.ScrollToVerticalOffset(offset);
        ArmHoverGuard();
        Trace("Win7 Start: inventory reloaded while open; " + _view + " refreshed in place");
    }

    // ---- Data sources (injected in QA) ------------------------------------------------------------------------------

    // Launch count and last use of a path: injected in QA, UsageStore otherwise.
    private (int Count, long LastTicks) UsageOf(string path)
    {
        if (_qa)
        {
            return _qaUsage.TryGetValue(path, out (int Count, long LastTicks) u) ? u : (0, 0L);
        }
        return (UsageStore.Count(path), UsageStore.LastUsed(path));
    }

    // One settings snapshot per refresh or menu build (no disk probe on the input path). QA never reads user settings.
    private AppSettings? SnapshotForRead()
    {
        return _qa ? null : SettingsStore.FastSnapshot;
    }

    private List<string> PinsRead(AppSettings? snap)
    {
        if (_qa)
        {
            return new List<string>(_qaPins);
        }
        return new List<string>(snap?.Win7StartMenuPins ?? new List<string>());
    }

    private List<string> HiddenRead(AppSettings? snap)
    {
        if (_qa)
        {
            return new List<string>(_qaHidden);
        }
        return new List<string>(snap?.Win7StartMenuMruHidden ?? new List<string>());
    }

    // Atomic read-modify-write of the persisted lists (QA mutates its own injected copies instead).
    private void PinsUpdate(Action<List<string>> mutate)
    {
        if (_qa)
        {
            mutate(_qaPins);
            return;
        }
        SettingsStore.Update(delegate (AppSettings x)
        {
            x.Win7StartMenuPins ??= new List<string>();
            mutate(x.Win7StartMenuPins);
        });
    }

    private void HiddenUpdate(Action<List<string>> mutate)
    {
        if (_qa)
        {
            mutate(_qaHidden);
            return;
        }
        SettingsStore.Update(delegate (AppSettings x)
        {
            x.Win7StartMenuMruHidden ??= new List<string>();
            mutate(x.Win7StartMenuMruHidden);
        });
    }

    private bool IsTaskbarPinned(string path)
    {
        if (_qa)
        {
            return _qaTaskbarPinned.Contains(path);
        }
        return TaskbarPins.Load().Any((PinnedApp p) => string.Equals(p.LaunchPath, path, StringComparison.OrdinalIgnoreCase));
    }

    private void ToggleStartPin(string path, bool pin)
    {
        try
        {
            PinsUpdate(delegate (List<string> list)
            {
                list.RemoveAll((string p) => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
                if (pin)
                {
                    list.Add(path);
                }
            });
        }
        catch (Exception ex)
        {
            Trace("Win7 Start: pin update failed: " + ex.Message);
        }
        RefreshAfterListChange();
    }

    private void ToggleTaskbarPin(AppEntry a, bool pin)
    {
        Act((pin ? "taskbar-pin " : "taskbar-unpin ") + a.Name, delegate
        {
            List<PinnedApp> pins = TaskbarPins.Load();
            bool exists = pins.Any((PinnedApp p) => string.Equals(p.LaunchPath, a.LaunchPath, StringComparison.OrdinalIgnoreCase));
            if (pin && !exists)
            {
                pins.Add(new PinnedApp { Name = a.Name, LaunchPath = a.LaunchPath, Aumid = a.AppId });
            }
            else if (!pin)
            {
                pins.RemoveAll((PinnedApp p) => string.Equals(p.LaunchPath, a.LaunchPath, StringComparison.OrdinalIgnoreCase));
            }
            else
            {
                return;
            }
            TaskbarPins.Save(pins);
            TaskbarWindow.ReloadPins();
        });
    }

    private void RemoveFromMru(string path)
    {
        try
        {
            HiddenUpdate(delegate (List<string> list)
            {
                if (!list.Any((string p) => string.Equals(p, path, StringComparison.OrdinalIgnoreCase)))
                {
                    list.Add(path);
                }
            });
        }
        catch (Exception ex)
        {
            Trace("Win7 Start: hidden-list update failed: " + ex.Message);
        }
        RefreshAfterListChange();
    }
}
