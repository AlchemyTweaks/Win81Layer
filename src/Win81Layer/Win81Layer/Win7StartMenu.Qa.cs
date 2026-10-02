using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

#nullable enable annotations

namespace Win81Layer;

// Windows 7 Start menu: offscreen QA harness (--win7starttest). The harness window stays at (-4000,-4000), is shown
// without activation and is never placed, re-targeted or brought to the foreground. All user data the menu reads
// (pins, hidden list, taskbar pins) is injected, and every side effect (launches, shell actions, child menus) is
// recorded instead of performed. Outputs per tag: PNG renders, win7start-<tag>-checks.txt (PASS/FAIL/METRIC/INFO),
// win7start-<tag>-trace.txt and win7start-<tag>-actions.txt.
public sealed partial class Win7StartMenu
{
    private ModifierKeys _qaMods;

    private bool? _qaMotionOff;

    private TimeSpan? _qaDur;

    private bool _qaForceChildOpenFail;

    private bool _qaSuppressCompleted;   // the close fade's Completed does nothing (the fallback must finish the hide)

    private bool _qaTrackProgs = true;   // simulated Start_TrackProgs

    private readonly List<string> _qaPins = new List<string>();

    private readonly List<string> _qaHidden = new List<string>();

    private readonly HashSet<string> _qaTaskbarPinned = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    private readonly List<AppEntry> _qaExtraApps = new List<AppEntry>();

    private readonly List<string> _qaActions = new List<string>();

    private readonly List<string> _qaTrace = new List<string>();

    private readonly List<(ContextMenu Menu, UIElement Target, PlacementMode Mode, bool FocusFirst)> _qaChildRequests = new List<(ContextMenu Menu, UIElement Target, PlacementMode Mode, bool FocusFirst)>();

    // The pointer position CursorNow reports in QA (screen pixels); null means unknown, so hover is never guarded.
    private (int X, int Y)? _qaCursor;

    private readonly List<(string Name, bool AsAdmin)> _qaLaunches = new List<(string Name, bool AsAdmin)>();

    private readonly List<string> _qaChecks = new List<string>();

    private int _qaActivations;     // the harness window must never be activated

    private readonly List<string> _qaActivationNotes = new List<string>();

    private string _qaCurrentCheck = "render";

    private bool _qaMovedOnScreen;  // ... nor ever be moved onto a monitor

    // PrepareForShow time of every QA show, in order, with the icons it loaded on the UI thread (the harness loads
    // synchronously; the live menu never does).
    private readonly List<(double Ms, int IconLoads)> _qaPrepMs = new List<(double Ms, int IconLoads)>();

    // Whole-ShowMenu time of every QA show, in order.
    private readonly List<double> _qaShowMs = new List<double>();

    // The provider reports an empty inventory (the Start screen has not delivered one yet).
    private bool _qaEmptyInventory;

    private RenderTargetBitmap? _qaDefaultSnap;   // the 'default' render of the current tag (onclosing-cancel compares)

    private RenderTargetBitmap? _qaWhiteSnap;     // the same state over a white backdrop (rendered contrast)

    // Injected usage (path -> launch count, last use in UTC ticks), account and glass source.
    private readonly Dictionary<string, (int Count, long LastTicks)> _qaUsage = new Dictionary<string, (int Count, long LastTicks)>(StringComparer.OrdinalIgnoreCase);

    private ImageSource? _qaPicture;

    private string? _qaName;

    private Color? _qaGlassSource;

    private string _qaOutDir = string.Empty;

    private string _qaTag = string.Empty;

    private readonly List<(string State, BitmapSource Bmp)> _qaSheet = new List<(string State, BitmapSource Bmp)>();

    private readonly List<string> _qaDefaultNames = new List<string>();

    // Recent programs of the 'default' state, in this order when the inventory has them; the first inventory entries
    // fill up to 8. They include icons that used to render as dots and a win81 glyph entry (live list only).
    private static readonly string[] QaPreferredRecent =
    {
        "7-Zip File Manager", "Send to OneNote", "Spreadsheet Compare", "PC settings", "Calculator", "Character Map", "Notepad",
        "Paint", "Command Prompt", "Snipping Tool", "Settings", "File Explorer"
    };

    private const string QaLongName = "A very long program name that must end with an ellipsis before the edge";

    private const string QaLongPath = "qa:longname";

    // Synthetic window size of the stage-1 placement checks (pure maths, independent of the current layout).
    private const double QaPlaceW = 540.0;

    private const double QaPlaceH = 620.0;

    protected override void OnActivated(EventArgs e)
    {
        if (_qa)
        {
            _qaActivations++;
            _qaActivationNotes.Add(_qaCurrentCheck);
        }
        base.OnActivated(e);
    }

    protected override void OnLocationChanged(EventArgs e)
    {
        if (_qa && (Left > -3000.0 || Top > -3000.0))
        {
            _qaMovedOnScreen = true;
        }
        base.OnLocationChanged(e);
    }

    // Puts this instance in harness mode: never activated, never placed, side effects recorded.
    internal void QaInit()
    {
        _qa = true;
        ShowActivated = false;
        Left = -4000.0;
        Top = -4000.0;
        _qaGlassSource = QaGlassSourceFixed;
        W7Icons.Synchronous = true;
        // A visible topmost window, even offscreen, is a candidate when Windows picks a window to activate after the
        // user's foreground window closes or another window of this process goes away. WS_EX_NOACTIVATE takes the
        // harness window out of that choice, so a QA run can never take the focus from the user.
        try
        {
            nint hwnd = new WindowInteropHelper(this).EnsureHandle();
            int ex = QaGetWindowLong(hwnd, GWL_EXSTYLE);
            if ((ex & WS_EX_NOACTIVATE) == 0)
            {
                QaSetWindowLong(hwnd, GWL_EXSTYLE, ex | WS_EX_NOACTIVATE);
            }
        }
        catch
        {
        }
    }

    private const int GWL_EXSTYLE = -20;

    private const int WS_EX_NOACTIVATE = 0x08000000;

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static extern int QaGetWindowLong(nint hwnd, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW")]
    private static extern int QaSetWindowLong(nint hwnd, int index, int value);

    // The harness's launch delegate lands here (programs are recorded, never started). Launches also go into
    // _qaActions at the moment they happen, so the actions file stays in the same order as the trace.
    internal void QaRecordLaunch(AppEntry a, bool asAdmin)
    {
        _qaLaunches.Add((a.Name, asAdmin));
        _qaActions.Add("launch " + a.Name + (asAdmin ? " (as administrator)" : string.Empty));
    }

    // Clears every injected input so each check starts from the same state.
    private void QaReset()
    {
        _qaMods = ModifierKeys.None;
        // Deterministic by default: no fades. The motion checks switch real fades on (_qaMotionOff = false).
        _qaMotionOff = true;
        _qaDur = null;
        _qaSuppressCompleted = false;
        _qaTrackProgs = true;
        _qaPointer = null;
        _qaCursor = null;
        _hoverAnchor = null;
        _qaForceChildOpenFail = false;
        _qaPins.Clear();
        _qaHidden.Clear();
        _qaTaskbarPinned.Clear();
        _qaExtraApps.Clear();
        _qaUsage.Clear();
        _qaPicture = null;
        _qaName = null;
        _qaGlassSource = QaGlassSourceFixed;
        _qaEmptyInventory = false;
    }

    // The injected extra entries, as part of the All Programs revision (the provider's list itself never changes in QA).
    private string QaExtraSignature()
    {
        if (_qaEmptyInventory || _qaExtraApps.Count == 0)
        {
            return _qaEmptyInventory ? "empty" : string.Empty;
        }
        return string.Join("|", _qaExtraApps.Select((AppEntry a) => a.LaunchPath + ":" + a.Category));
    }

    // Opens every folder of All Programs in place (no scrolling), so all of its rows are laid out.
    private void QaExpandAll()
    {
        foreach (TreeNode node in _allNodes.ToList())
        {
            if (node.Folder != null)
            {
                ToggleFolder(node, expand: true, bringIntoView: false);
            }
        }
        UpdateLayout();
    }

    // The 'default' state's recent list: 8 inventory programs used 0 to 20 days ago, most used first.
    private void QaInjectDefault()
    {
        _qaUsage.Clear();
        _qaDefaultNames.Clear();
        List<AppEntry> apps = LoadApps();
        List<AppEntry> pick = new List<AppEntry>();
        foreach (string name in QaPreferredRecent)
        {
            AppEntry? a = apps.FirstOrDefault((AppEntry x) => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase));
            if (a != null && !pick.Contains(a))
            {
                pick.Add(a);
            }
            if (pick.Count >= 8)
            {
                break;
            }
        }
        foreach (AppEntry a in apps)
        {
            if (pick.Count >= 8)
            {
                break;
            }
            if (!pick.Contains(a))
            {
                pick.Add(a);
            }
        }
        long now = DateTime.UtcNow.Ticks;
        for (int i = 0; i < pick.Count; i++)
        {
            double days = i * 20.0 / 7.0;
            _qaUsage[pick[i].LaunchPath] = (40 - i * 4, now - TimeSpan.FromDays(days).Ticks);
            _qaDefaultNames.Add(pick[i].Name);
        }
    }

    // A synthetic program with a name too long for the row, used most, so it is the first recent row.
    private void QaInjectLongName()
    {
        _qaExtraApps.RemoveAll((AppEntry a) => a.LaunchPath == QaLongPath);
        _qaExtraApps.Add(new AppEntry { Name = QaLongName, LaunchPath = QaLongPath, TileBrush = Brushes.Transparent });
        _qaUsage[QaLongPath] = (999, DateTime.UtcNow.Ticks);
    }

    // A 208 x 208 synthetic account picture (gradient with a light disc), like the size UserAccount returns.
    private static ImageSource QaSyntheticPicture()
    {
        LinearGradientBrush bg = new LinearGradientBrush(Color.FromRgb(0xF2, 0x9B, 0x38), Color.FromRgb(0x3A, 0x6E, 0xA5), 45.0);
        SolidColorBrush disc = new SolidColorBrush(Color.FromArgb(0xB0, 0xFF, 0xFF, 0xFF));
        DrawingGroup g = new DrawingGroup();
        g.Children.Add(new GeometryDrawing(bg, null, new RectangleGeometry(new Rect(0.0, 0.0, 208.0, 208.0))));
        g.Children.Add(new GeometryDrawing(disc, null, new EllipseGeometry(new Point(104.0, 84.0), 44.0, 44.0)));
        DrawingImage img = new DrawingImage(g);
        img.Freeze();
        return img;
    }

    internal void QaRender(string outDir, string tag)
    {
        QaInit();
        QaReset();
        _qaActions.Clear();
        _qaTrace.Clear();
        _qaChildRequests.Clear();
        _qaLaunches.Clear();
        _qaChecks.Clear();
        _qaPrepMs.Clear();
        _qaShowMs.Clear();
        _qaDefaultSnap = null;
        _qaWhiteSnap = null;
        _qaSheet.Clear();
        _qaOutDir = outDir;
        _qaTag = tag;
        // ForceForTest raised ShellTheme.Changed while this window was hidden, which only marked the palette dirty.
        EnsurePalette();
        // The shortcut index is built once per process, here before any timing is taken.
        EnsureShortcutIndex(synchronous: true);
        ShortcutIndex? sci = System.Threading.Volatile.Read(ref s_shortcuts);
        Info($"tag={tag} apps={LoadApps().Count} palette={_paletteSig} rw={_rw:0.##} shortcut-files={sci?.Files ?? -1} ({sci?.BuildMs ?? 0.0:0} ms) target-names={sci?.TargetNames.Count ?? -1} folder-keys={sci?.FolderByName.Count ?? -1}/{sci?.FolderByTarget.Count ?? -1}");
        List<(string Name, string Hash)> metroHashes = QaMetroIconHashes();
        foreach ((string name, string hash) in metroHashes)
        {
            Info("metro-icon-hash " + name + "\t" + hash);
        }
        try
        {
            Color real = TaskbarTheme.SourceAccent();
            Info($"glass-source fixed=#3A6EA5 real=#{real.R:X2}{real.G:X2}{real.B:X2} enableTransparency={ReadEnableTransparency()}");
        }
        catch (Exception ex)
        {
            Info("glass-source real unreadable: " + ex.Message);
        }
        try
        {
            // Read-only: the renders inject empty pins, so a user with real pins sees fewer rows than an old baseline.
            int realPins = SettingsStore.FastSnapshot.Win7StartMenuPins?.Count ?? 0;
            Info($"real-win7-pins count={realPins}");
            if (realPins > 0)
            {
                Info("baseline-had-real-pins");
            }
        }
        catch (Exception ex)
        {
            Info("real-win7-pins unreadable: " + ex.Message);
        }

        // Phase 1: every render.
        QaInjectDefault();
        Info("default recent: " + string.Join(", ", _qaDefaultNames));
        ShowMenu("qa");
        _qaDefaultSnap = Snap("default");
        _qaWhiteSnap = Snap("default-white", Colors.White);
        _qaGlassSource = null;   // the user's real taskbar source colour, for visual review
        ShowMenu("qa");
        Info("default-usersource palette=" + _paletteSig);
        Snap("default-usersource");
        _qaGlassSource = QaGlassSourceFixed;
        ShowMenu("qa");
        ToggleAllPrograms();
        Snap("allprograms");
        // Review aid: All Programs scrolled to where the root programs end and the folders begin.
        W7Row? firstFolder = QaListRows().FirstOrDefault((W7Row r) => r.Payload is TreeNode);
        if (firstFolder != null)
        {
            UpdateLayout();
            _progScroll.ScrollToVerticalOffset(Math.Max(0.0, QaBounds(firstFolder, _progList).Top - 66.0));
            Snap("allprograms-folders");
            Info("allprograms-folders: first folder '" + firstFolder.Text + "'");
        }
        // Review aid: All Programs with every folder open, scrolled to the first plated program icon (if any).
        QaExpandAll();
        W7Row? platedRow = QaListRows().FirstOrDefault((W7Row r) => r.PlateShown && r.Payload is AppEntry e && !IsGlyphEntry(e))
            ?? QaListRows().FirstOrDefault((W7Row r) => r.PlateShown);
        if (platedRow != null)
        {
            UpdateLayout();
            _progScroll.ScrollToVerticalOffset(Math.Max(0.0, QaBounds(platedRow, _progList).Top - 66.0));
            Snap("allprograms-plates");
            Info("allprograms-plates shows '" + platedRow.Text + "'");
        }
        QaLiveFlipPlates();
        ToggleAllPrograms();
        _search.Text = "s";
        Snap("search");
        QaInjectLongName();
        ShowMenu("qa");
        Snap("longname");
        _qaExtraApps.RemoveAll((AppEntry a) => a.LaunchPath == QaLongPath);
        _qaUsage.Remove(QaLongPath);
        _qaPicture = QaSyntheticPicture();
        ShowMenu("qa");
        Snap("picture");
        _qaPicture = null;
        ShowMenu("qa");
        bool dark = ShellTheme.IsDark;
        try
        {
            // A theme flip while the menu is open repaints it in place.
            ShellTheme.ForceForTest(!dark);
            Snap("liveflip");
        }
        finally
        {
            ShellTheme.ForceForTest(dark);
        }
        QaStage3Renders();
        QaStage4Renders();
        QaSnapTooltip();
        QaWriteSheet();

        // Phase 2: every check, each starting from ShowMenu("qa").
        QaRunChecks(metroHashes);

        // Phase 3: outputs.
        Dismiss("qa done", instant: true);
        QaReset();
        QaWriteOutputs(outDir, tag);
        Logger.Log("Win7StartMenu QaRender -> " + outDir + " (" + tag + ")");
    }

    // Disposes this harness menu exactly like App does (DisposeMenu) and records the result in the tag's checks file.
    // The file is rewritten so the dispose-self line sits before the summary and is counted in it.
    internal void QaDispose(string outDir, string tag)
    {
        bool closedFired = false;
        Closed += delegate
        {
            closedFired = true;
        };
        int before = StaticSubscriptions;
        DisposeMenu();
        QaPump(50);
        bool stillHooked = QaThemeHandlerHolds(this);
        bool ok = closedFired && _closed && before > 0 && StaticSubscriptions == 0 && !stillHooked && !IsLoaded && !IsVisible && _renderHook == null && _closeFallback == null;
        string line = (ok ? "PASS" : "FAIL") + $" dispose-self closed={closedFired} subs={before}->{StaticSubscriptions} themeHandlerLeft={stillHooked} loaded={IsLoaded} visible={IsVisible} renderHook={_renderHook != null} fallback={_closeFallback != null}";
        string path = Path.Combine(outDir, "win7start-" + tag + "-checks.txt");
        try
        {
            List<string> lines = File.Exists(path) ? new List<string>(File.ReadAllLines(path)) : new List<string>();
            lines.Add(line);
            QaWriteChecks(path, lines);
        }
        catch
        {
        }
    }

    // Writes the checks file with one summary line at the end that counts every PASS and FAIL line in it.
    private static void QaWriteChecks(string path, IEnumerable<string> lines)
    {
        List<string> body = lines.Where((string l) => !l.StartsWith("INFO summary ", StringComparison.Ordinal)).ToList();
        int pass = body.Count((string l) => l.StartsWith("PASS ", StringComparison.Ordinal));
        int fail = body.Count((string l) => l.StartsWith("FAIL ", StringComparison.Ordinal));
        body.Add($"INFO summary pass={pass} fail={fail}");
        File.WriteAllLines(path, body);
    }

    // True when a static event the menu subscribes to (theme, accent, icon loads) still has a handler whose target is
    // the given menu (a forgotten -= keeps it alive).
    private static bool QaThemeHandlerHolds(Win7StartMenu menu)
    {
        return QaStaticHandlerHolds(typeof(ShellTheme), "Changed", menu)
            || QaStaticHandlerHolds(typeof(TaskbarWindow), "AccentChanged", menu)
            || QaStaticHandlerHolds(typeof(W7Icons), "Loaded", menu)
            || QaStaticHandlerHolds(typeof(StartScreen), "AppsReloaded", menu);
    }

    private static bool QaStaticHandlerHolds(Type owner, string eventName, Win7StartMenu menu)
    {
        System.Reflection.FieldInfo? f = owner.GetField(eventName, System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
        if (f?.GetValue(null) is not Delegate d)
        {
            return false;
        }
        return d.GetInvocationList().Any((Delegate h) => ReferenceEquals(h.Target, menu));
    }

    private void QaEnsureShown()
    {
        if (!IsVisible)
        {
            Left = -4000.0;
            Top = -4000.0;
            Show();
        }
        _root.BeginAnimation(OpacityProperty, null);
        _root.Opacity = 1.0;
    }

    // Lets the dispatcher (and the render loop) run for about ms milliseconds without any timer object.
    private void QaPump(int ms)
    {
        Stopwatch sw = Stopwatch.StartNew();
        do
        {
            DispatcherFrame frame = new DispatcherFrame();
            Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, (Action)delegate
            {
                frame.Continue = false;
            });
            Dispatcher.PushFrame(frame);
            System.Threading.Thread.Sleep(4);
        }
        while (sw.ElapsedMilliseconds < ms);
    }

    // Raises a real PreviewKeyDown on the search box (the route passes the window's handler first).
    private bool QaKey(Key k)
    {
        PresentationSource? src = PresentationSource.FromVisual(_search);
        if (src == null)
        {
            return false;
        }
        KeyEventArgs args = new KeyEventArgs(Keyboard.PrimaryDevice, src, Environment.TickCount, k)
        {
            RoutedEvent = Keyboard.PreviewKeyDownEvent
        };
        _search.RaiseEvent(args);
        return args.Handled;
    }

    // Raises a mouse-invoked ContextMenuOpening on the element, the way WPF does for a right-click. The event args have
    // no public constructor, so the harness creates them by reflection; returns false when that is not possible.
    private static bool QaRaiseContextMenuOpening(UIElement target)
    {
        foreach (System.Reflection.ConstructorInfo ctor in typeof(ContextMenuEventArgs).GetConstructors(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic))
        {
            System.Reflection.ParameterInfo[] ps = ctor.GetParameters();
            if (ps.Length == 4 && ps[0].ParameterType == typeof(object) && ps[1].ParameterType == typeof(bool) && ps[2].ParameterType == typeof(double) && ps[3].ParameterType == typeof(double))
            {
                ContextMenuEventArgs args = (ContextMenuEventArgs)ctor.Invoke(new object[] { target, true, 4.0, 4.0 });
                target.RaiseEvent(args);
                return true;
            }
        }
        return false;
    }

    // Renders the current state to win7start-<tag>-<state>.png (and the contact sheet) over the wallpaper-like backdrop,
    // or over a solid colour. A popup (a child menu laid out detached) is drawn at its window position.
    private RenderTargetBitmap Snap(string state, Color? backdrop = null, (FrameworkElement El, Point At)? popup = null)
    {
        QaEnsureShown();
        RenderTargetBitmap rtb = popup.HasValue ? SnapComposite(popup.Value.El, popup.Value.At, backdrop) : SnapBitmap(backdrop: true, solid: backdrop);
        PngBitmapEncoder enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(rtb));
        using (FileStream fs = File.Create(Path.Combine(_qaOutDir, "win7start-" + _qaTag + "-" + state + ".png")))
        {
            enc.Save(fs);
        }
        _qaSheet.Add((state, rtb));
        return rtb;
    }

    // Renders the window over a fixed wallpaper-like backdrop, so translucency and contrast read as on a desktop.
    // Without the backdrop only the menu's own pixels are drawn (a blank or transparent menu then stays empty).
    private RenderTargetBitmap SnapBitmap(bool backdrop = true, Color? solid = null)
    {
        UpdateLayout();
        int w = (int)Math.Ceiling(Math.Max(ActualWidth, 1.0));
        int h = (int)Math.Ceiling(Math.Max(ActualHeight, 1.0));
        RenderTargetBitmap rtb = new RenderTargetBitmap(w, h, 96.0, 96.0, PixelFormats.Pbgra32);
        if (backdrop)
        {
            DrawingVisual wallVisual = new DrawingVisual();
            using (DrawingContext dc = wallVisual.RenderOpen())
            {
                Brush wall = solid.HasValue
                    ? new SolidColorBrush(solid.Value)
                    : new LinearGradientBrush(Color.FromRgb(0x1E, 0x4E, 0x7A), Color.FromRgb(0x6A, 0x9C, 0x5A), 35.0);
                dc.DrawRectangle(wall, null, new Rect(0.0, 0.0, w, h));
            }
            rtb.Render(wallVisual);
        }
        if (Content is Visual root)
        {
            rtb.Render(root);
        }
        return rtb;
    }

    private void Check(string name, bool ok, string detail)
    {
        _qaChecks.Add((ok ? "PASS " : "FAIL ") + name + " " + detail);
    }

    private void Metric(string name, double v)
    {
        _qaChecks.Add($"METRIC {name} {v:0.###}");
    }

    private void Info(string s)
    {
        _qaChecks.Add("INFO " + s);
    }

    private void QaTry(string name, Func<(bool Ok, string Detail)> body)
    {
        _qaCurrentCheck = name;
        try
        {
            (bool ok, string detail) = body();
            Check(name, ok, detail);
        }
        catch (Exception ex)
        {
            Check(name, false, "exception " + ex.GetType().Name + ": " + ex.Message);
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

    private bool TraceSince(int from, string needle)
    {
        for (int i = Math.Max(0, from); i < _qaTrace.Count; i++)
        {
            if (_qaTrace[i].Contains(needle, StringComparison.Ordinal))
            {
                return true;
            }
        }
        return false;
    }

    private void QaRunChecks(List<(string Name, string Hash)> metroHashesAtStart)
    {
        QaPlacementChecks();

        QaTry("enter-empty-noop", delegate
        {
            ShowMenu("qa");
            int n = _qaLaunches.Count;
            bool handled = QaKey(Key.Enter);
            _search.Text = "   ";
            bool handledWs = QaKey(Key.Enter);
            bool ok = _qaLaunches.Count == n && IsVisible && IsOpen;
            return (ok, $"launches {n}->{_qaLaunches.Count} handled={handled}/{handledWs} visible={IsVisible}");
        });

        QaTry("enter-first", delegate
        {
            ShowMenu("qa");
            _search.Text = "s";
            AppEntry? first = FirstProgramRow();
            int n = _qaLaunches.Count;
            int t = _qaTrace.Count;
            QaKey(Key.Enter);
            bool one = _qaLaunches.Count == n + 1;
            string got = one ? _qaLaunches[^1].Name : "(none)";
            bool ok = first != null && one && got == first.Name && !_qaLaunches[^1].AsAdmin && !IsVisible && TraceSince(t, "hide cause = launch ");
            return (ok, $"first row='{first?.Name}' launched='{got}' launches {n}->{_qaLaunches.Count} visible={IsVisible}");
        });

        QaTry("esc-stack", delegate
        {
            ShowMenu("qa");
            _search.Text = "x";
            QaKey(Key.Escape);
            bool a1 = _search.Text.Length == 0 && IsVisible;
            ToggleAllPrograms();
            bool inAll = _allMode;
            QaKey(Key.Escape);
            bool a2 = inAll && !_allMode && IsVisible && _cmdRow.Text == "All Programs";
            int t = _qaTrace.Count;
            QaKey(Key.Escape);
            bool a3 = TraceSince(t, "hide cause = esc") && !IsVisible;
            return (a1 && a2 && a3, $"text-cleared={a1} allprograms-back={a2} closed={a3}");
        });

        QaTry("tab-handled", delegate
        {
            ShowMenu("qa");
            bool h1 = QaKey(Key.Tab);
            _qaMods = ModifierKeys.Shift;
            bool h2 = QaKey(Key.Tab);
            IInputElement? kf = Keyboard.FocusedElement;
            IInputElement? lf = FocusManager.GetFocusedElement(this);
            bool kfOk = kf == null || ReferenceEquals(kf, _search) || !(kf is DependencyObject kd && (ReferenceEquals(kd, this) || IsAncestorOf(kd)));
            bool lfOk = lf == null || ReferenceEquals(lf, _search);
            return (h1 && h2 && kfOk && lfOk, $"handled={h1}/{h2} keyboardFocus={Describe(kf)} logicalFocus={Describe(lf)}");
        });

        QaTry("not-focusable", delegate
        {
            ShowMenu("qa");
            ToggleAllPrograms();   // the long list shows the scrollbar, so its parts are in the tree too
            UpdateLayout();
            List<string> bad = new List<string>();
            int buttons = 0;
            int rows = 0;
            QaWalk(_root, delegate (DependencyObject d)
            {
                if (d is ButtonBase)
                {
                    buttons++;
                }
                if (d is FrameworkElement fe && (fe.Tag is AppEntry || (fe.Tag is string tg && tg.StartsWith("w7:", StringComparison.Ordinal))))
                {
                    rows++;
                }
                if (d is UIElement u && u.Focusable && !ReferenceEquals(u, _search) && !_search.IsAncestorOf(u))
                {
                    bad.Add(Describe(u));
                }
                if (d is Control c && c.IsTabStop && (c is Button || ReferenceEquals(c, _progScroll)))
                {
                    bad.Add(Describe(c) + "(tabstop)");
                }
            });
            return (bad.Count == 0 && buttons >= 2 && rows > 0, $"buttons={buttons} rows={rows} focusable-other-than-search={bad.Count}" + (bad.Count > 0 ? " [" + string.Join(", ", bad.Distinct().Take(12)) + "]" : string.Empty));
        });

        QaTry("onclosing-cancel", delegate
        {
            QaInjectDefault();
            ShowMenu("qa");
            int t = _qaTrace.Count;
            Close();
            bool notClosed = !_closed;
            bool hidden = !IsVisible;
            bool traced = TraceSince(t, "hide cause = close request");
            QaEnsureShown();
            // The menu's own pixels only (no backdrop): a menu left blank or transparent by the cancelled close fails here.
            // Counted inside the glass (the overhang and the shadow band are transparent by design).
            RenderTargetBitmap own = SnapBitmap(backdrop: false);
            Int32Rect glassRect = new Int32Rect(0, (int)Overhang, (int)Math.Round(_layout.Gw), (int)Math.Round(_layout.Gh));
            int opaque = QaCountOpaque(own, glassRect);
            int area = glassRect.Width * glassRect.Height;
            bool drawn = opaque >= area * 0.95;
            // ... and it must look exactly like the 'default' render of this tag (same state, same process).
            (int diffPx, int maxDelta) = QaDiff(_qaDefaultSnap, SnapBitmap());
            bool same = _qaDefaultSnap != null && diffPx == 0;
            bool ok = notClosed && hidden && traced && IsVisible && drawn && same;
            return (ok, $"closed={_closed} hiddenAfterClose={hidden} traced={traced} reshown={IsVisible} ownOpaque={opaque}/{area} vsDefault={(_qaDefaultSnap == null ? "missing" : diffPx + "px maxdelta=" + maxDelta)}");
        });

        QaTry("clone-deep", delegate
        {
            AppSettings orig = new AppSettings
            {
                Win7StartMenuPins = new List<string> { "a" },
                Win7StartMenuMruHidden = new List<string> { "b" }
            };
            AppSettings c = orig.Clone();
            bool distinct = !ReferenceEquals(c.Win7StartMenuPins, orig.Win7StartMenuPins) && !ReferenceEquals(c.Win7StartMenuMruHidden, orig.Win7StartMenuMruHidden);
            c.Win7StartMenuPins.Add("x");
            c.Win7StartMenuMruHidden.Add("y");
            bool isolated = orig.Win7StartMenuPins.Count == 1 && orig.Win7StartMenuMruHidden.Count == 1;
            bool copied = c.Win7StartMenuPins[0] == "a" && c.Win7StartMenuMruHidden[0] == "b";
            return (distinct && isolated && copied, $"distinct={distinct} isolated={isolated} copied={copied}");
        });

        QaTry("filter-win81", delegate
        {
            BitmapSource dot = BitmapSource.Create(1, 1, 96.0, 96.0, PixelFormats.Bgra32, null, new byte[4], 4);
            _qaExtraApps.Add(new AppEntry { Name = "0 Desktop QA", LaunchPath = "win81:desktop", TileBrush = Brushes.Transparent, Icon = dot });
            _qaExtraApps.Add(new AppEntry { Name = "0 News QA", LaunchPath = "win81:news", TileBrush = Brushes.Transparent, Icon = dot });
            _qaExtraApps.Add(new AppEntry { Name = "0 Control QA", LaunchPath = "qa:control", TileBrush = Brushes.Transparent, Icon = dot });
            _qaPins.Add("win81:desktop");
            _qaPins.Add("win81:news");
            _qaPins.Add("qa:control");
            ShowMenu("qa");
            bool main = !QaRowsHavePseudo();
            bool mainControl = QaRowsHave("qa:control");
            ToggleAllPrograms();
            bool all = !QaRowsHavePseudo();
            bool allControl = QaRowsHave("qa:control");
            ToggleAllPrograms();
            _search.Text = "QA";
            bool search = !QaRowsHavePseudo();
            bool searchControl = QaRowsHave("qa:control");
            bool ok = main && all && search && mainControl && allControl && searchControl;
            return (ok, $"hidden in main={main} allprograms={all} search={search}; control entry shown {mainControl}/{allControl}/{searchControl}");
        });

        QaTry("child-menu-reset", delegate
        {
            ShowMenu("qa");
            _qaForceChildOpenFail = true;
            ContextMenu cm = new ContextMenu();
            cm.Items.Add(new MenuItem { Header = "QA" });
            OpenChildMenu(cm, _search, PlacementMode.Bottom);
            bool ok = !_childMenuOpen && Topmost && _openChild == null && !cm.IsOpen;
            return (ok, $"childMenuOpen={_childMenuOpen} topmost={Topmost} openChild={(_openChild != null)} popupOpen={cm.IsOpen}");
        });

        QaTry("child-request-recorded", delegate
        {
            ShowMenu("qa");
            int n = _qaChildRequests.Count;
            _powerArrow.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent, _powerArrow));
            bool one = _qaChildRequests.Count == n + 1 && ReferenceEquals(_qaChildRequests[^1].Menu, _powerMenu);
            bool ok = one && !_powerMenu.IsOpen && !_childMenuOpen && Topmost && IsOpen;
            return (ok, $"requests {n}->{_qaChildRequests.Count} popupOpen={_powerMenu.IsOpen} childMenuOpen={_childMenuOpen} topmost={Topmost}");
        });

        QaTry("places-recorded", delegate
        {
            ShowMenu("qa");
            int n = _qaActions.Count;
            int t = _qaTrace.Count;
            QaClick(_placeLinks["Documents"]);
            bool one = _qaActions.Count == n + 1 && _qaActions[^1] == "shell:Personal";
            bool ok = one && !IsVisible && TraceSince(t, "hide cause = launch Documents");
            return (ok, $"actions {n}->{_qaActions.Count} last='{(_qaActions.Count > 0 ? _qaActions[^1] : "")}' visible={IsVisible}");
        });

        QaTry("settings-writes-injected", delegate
        {
            ShowMenu("qa");
            ToggleStartPin("qa:pin", pin: true);
            bool pinned = _qaPins.Contains("qa:pin");
            ToggleStartPin("qa:pin", pin: false);
            bool unpinned = !_qaPins.Contains("qa:pin");
            RemoveFromMru("qa:hidden");
            bool hiddenOk = _qaHidden.Contains("qa:hidden");
            return (pinned && unpinned && hiddenOk, $"pin={pinned} unpin={unpinned} hidden={hiddenOk} (a real settings write would throw in this read-only process)");
        });

        QaTry("dispose-unsubscribes", delegate
        {
            Win7StartMenu m2 = new Win7StartMenu(_appsProvider, _launch, OpenSettings);
            m2.QaInit();
            bool closedFired = false;
            m2.Closed += delegate
            {
                closedFired = true;
            };
            m2.ShowMenu("qa");
            bool wasLoaded = m2.IsLoaded;
            int before = m2.StaticSubscriptions;
            bool hookedBefore = QaThemeHandlerHolds(m2);
            m2.DisposeMenu();
            QaPump(50);
            bool hookedAfter = QaThemeHandlerHolds(m2);
            // The menu really subscribes (before > 0, the handler is in ShellTheme.Changed) and disposal really removes it.
            bool ok = closedFired && before > 0 && hookedBefore && m2.StaticSubscriptions == 0 && !hookedAfter && !m2.IsLoaded && !m2.IsVisible && m2._qaActivations == 0;
            return (ok, $"subscriptions {before}->{m2.StaticSubscriptions} themeHandler {hookedBefore}->{hookedAfter} loaded {wasLoaded}->{m2.IsLoaded} closed={closedFired} visible={m2.IsVisible} activations={m2._qaActivations}");
        });

        QaTry("edit-menu-routed", delegate
        {
            // A right-click in the search box: the TextBox's built-in menu must stay closed and the request must go
            // through OpenChildMenu (recorded, never opened, in QA).
            ShowMenu("qa");
            _search.Text = "qa";
            _search.SelectAll();
            int n = _qaChildRequests.Count;
            bool raised = QaRaiseContextMenuOpening(_search);
            bool one = _qaChildRequests.Count == n + 1 && ReferenceEquals(_qaChildRequests[^1].Target, _search);
            string[] items = one ? _qaChildRequests[^1].Menu.Items.OfType<MenuItem>().Select((MenuItem m) => (string)m.Header).ToArray() : Array.Empty<string>();
            bool itemsOk = items.SequenceEqual(new[] { "Cut", "Copy", "Paste", "Select all" });
            bool builtInClosed = _search.ContextMenu == null && !_childMenuOpen && Topmost && IsOpen;
            bool ok = raised && one && itemsOk && builtInClosed;
            return (ok, $"raised={raised} requests {n}->{_qaChildRequests.Count} items=[{string.Join(", ", items)}] builtInMenu={(_search.ContextMenu == null ? "none" : "set")} childMenuOpen={_childMenuOpen} open={IsOpen}");
        });

        QaTry("clean-frame-hide", delegate
        {
            ShowMenu("qa");
            int t = _qaTrace.Count;
            HardHide(cleanFrame: true);
            bool pending = _hidePending && IsVisible && !IsOpen && _root.Opacity == 0.0 && _renderHook != null && _closeFallback != null;
            QaPump(300);
            bool done = !IsVisible && !_hidePending && _renderHook == null && _closeFallback == null && _root.Opacity == 1.0;
            string by = TraceSince(t, "hide completed by fallback") ? "fallback" : "frame";
            return (pending && done, $"pending={pending} done={done} completedBy={by}");
        });

        QaTry("reopen-during-hide", delegate
        {
            ShowMenu("qa");
            HardHide(cleanFrame: true);
            bool pending = _hidePending;
            ShowMenu("qa");
            bool reopened = IsVisible && IsOpen && !_hidePending && _renderHook == null && _closeFallback == null && _root.Opacity == 1.0;
            QaPump(300);
            bool stays = IsVisible && IsOpen;
            return (pending && reopened && stays, $"pending={pending} reopened={reopened} staysOpen={stays}");
        });

        QaTry("idle-after-dismiss", delegate
        {
            ShowMenu("qa");
            Dismiss("qa idle", instant: true);
            bool ok = !IsVisible && _closeFallback == null && _renderHook == null && !_hidePending && !_childMenuOpen && Topmost;
            return (ok, $"visible={IsVisible} fallback={_closeFallback != null} renderHook={_renderHook != null} childMenuOpen={_childMenuOpen}");
        });

        QaTry("qa-invisible", delegate
        {
            // Never activated, never moved onto a monitor, never the foreground window.
            ShowMenu("qa");
            bool onScreen = false;
            nint hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
            if (hwnd != IntPtr.Zero && GetWindowRect(hwnd, out NativeRect r))
            {
                System.Drawing.Rectangle wr = System.Drawing.Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom);
                onScreen = System.Windows.Forms.Screen.AllScreens.Any((System.Windows.Forms.Screen s) => s.Bounds.IntersectsWith(wr));
            }
            bool fgSelf = WindowUtil.ForegroundDescription().EndsWith("/self", StringComparison.Ordinal);
            bool ok = _qaActivations == 0 && !_qaMovedOnScreen && !onScreen && !IsActive && !fgSelf;
            return (ok, $"activations={_qaActivations}" + (_qaActivationNotes.Count > 0 ? " during [" + string.Join(", ", _qaActivationNotes) + "]" : string.Empty) + $" movedOnScreen={_qaMovedOnScreen} rectOnAMonitor={onScreen} foreground={(fgSelf ? "self" : "other")}");
        });

        QaTry("qa-read-only", delegate
        {
            bool ro = SettingsStore.ReadOnlyDiagnostics;
            bool threw = false;
            if (ro)
            {
                try
                {
                    SettingsStore.Update(delegate (AppSettings _)
                    {
                    });
                }
                catch (InvalidOperationException)
                {
                    threw = true;
                }
            }
            return (ro && threw, $"ReadOnlyDiagnostics={ro} settingsWriteThrows={threw}");
        });

        QaStage2Checks(metroHashesAtStart);

        QaStage3Checks();

        QaStage4Checks();

        QaPerfMetrics();

        QaTry("trace-show-hide", delegate
        {
            bool show = _qaTrace.Any((string l) => l.StartsWith("Win7 Start: show via qa", StringComparison.Ordinal));
            bool hide = _qaTrace.Any((string l) => l.Contains("hide cause =", StringComparison.Ordinal));
            return (show && hide, $"show-line={show} hide-line={hide} lines={_qaTrace.Count}");
        });
    }

    private void QaPlacementChecks()
    {
        System.Drawing.Rectangle mon = System.Drawing.Rectangle.FromLTRB(0, 0, 1920, 1080);
        System.Drawing.Rectangle work40 = System.Drawing.Rectangle.FromLTRB(0, 0, 1920, 1040);
        void P(string name, System.Drawing.Rectangle m, System.Drawing.Rectangle work, System.Drawing.Rectangle? btn, double scale, int ex, int ey, int? eh)
        {
            try
            {
                var (x, y, w, h) = Win7Placement.Compute(new Win7Placement.Input(m, work, "Bottom", btn, scale, QaPlaceW, QaPlaceH, 0.0, 0.0));
                bool ok = x == ex && y == ey && (!eh.HasValue || h == eh.Value);
                Check("placement-" + name, ok, $"-> ({x},{y}) {w}x{h} expected ({ex},{ey})" + (eh.HasValue ? $" h={eh.Value}" : string.Empty));
            }
            catch (Exception exn)
            {
                Check("placement-" + name, false, "exception " + exn.Message);
            }
        }
        P("b40-left", mon, work40, System.Drawing.Rectangle.FromLTRB(0, 1040, 48, 1080), 1.0, 0, 420, null);
        P("b40-centred", mon, work40, System.Drawing.Rectangle.FromLTRB(936, 1040, 984, 1080), 1.0, 936, 420, null);
        P("b40-clamp", mon, work40, System.Drawing.Rectangle.FromLTRB(1900, 1040, 1948, 1080), 1.0, 1380, 420, null);
        P("b30", mon, System.Drawing.Rectangle.FromLTRB(0, 0, 1920, 1050), System.Drawing.Rectangle.FromLTRB(0, 1050, 48, 1080), 1.0, 0, 430, null);
        P("b48", mon, System.Drawing.Rectangle.FromLTRB(0, 0, 1920, 1032), System.Drawing.Rectangle.FromLTRB(0, 1032, 48, 1080), 1.0, 0, 412, null);
        P("s150", System.Drawing.Rectangle.FromLTRB(0, 0, 2880, 1620), System.Drawing.Rectangle.FromLTRB(0, 0, 2880, 1560), null, 1.5, 0, 630, 930);
        P("s175", mon, work40, System.Drawing.Rectangle.FromLTRB(0, 1040, 48, 1080), 1.75, 0, 0, 1085);
        double maxDip = Win7Placement.MaxWindowDip(work40, 1.75);
        Check("placement-s175-maxdip", Math.Abs(maxDip - 590.29) <= 0.01, $"MaxWindowDip={maxDip:0.0000} expected 590.29");
        string eb = Win7Placement.InferEdge(mon, work40);
        string et = Win7Placement.InferEdge(mon, System.Drawing.Rectangle.FromLTRB(0, 40, 1920, 1080));
        string el = Win7Placement.InferEdge(mon, System.Drawing.Rectangle.FromLTRB(48, 0, 1920, 1080));
        string er = Win7Placement.InferEdge(mon, System.Drawing.Rectangle.FromLTRB(0, 0, 1872, 1080));
        Check("placement-infer-edge", eb == "Bottom" && et == "Top" && el == "Left" && er == "Right", $"-> {eb}/{et}/{el}/{er}");
        // The real menu window with its 18 px overhang and 8 px shadow band, flush on a 40 px bottom bar.
        try
        {
            LayoutInfo l = LayoutFor(0, 10, Win7Placement.MaxWindowDip(work40, 1.0), _rw);
            var (x, y, w, h) = Win7Placement.Compute(new Win7Placement.Input(mon, work40, "Bottom", System.Drawing.Rectangle.FromLTRB(0, 1040, 48, 1080), 1.0, l.W, l.H, Overhang, ShadowBand));
            bool ok = x == 0 && y + h == 1040 && w == (int)Math.Round(l.W, MidpointRounding.AwayFromZero) && h == (int)Math.Round(l.H, MidpointRounding.AwayFromZero);
            Check("placement-w7-window", ok, $"-> ({x},{y}) {w}x{h} V={l.V} expected bottom 1040 and {l.W}x{l.H}");
        }
        catch (Exception exn)
        {
            Check("placement-w7-window", false, "exception " + exn.Message);
        }
    }

    // ---- Stage 2: theme, chrome, icons ---------------------------------------------------------------------------------

    private static Rect QaBounds(FrameworkElement e, Visual ancestor)
    {
        return e.TransformToAncestor(ancestor).TransformBounds(new Rect(0.0, 0.0, e.ActualWidth, e.ActualHeight));
    }

    private static bool Near(double a, double b)
    {
        return Math.Abs(a - b) <= 0.5;
    }

    private static string R4(Rect r)
    {
        return $"({r.Left:0.##},{r.Top:0.##})-({r.Right:0.##},{r.Bottom:0.##})";
    }

    // The rows the current view shows (children of closed folders are left out).
    private List<W7Row> QaListRows()
    {
        return _progList.Children.OfType<W7Row>().Where((W7Row r) => r.Visibility == Visibility.Visible).ToList();
    }

    // Pixel of a Pbgra32 render (the backdrop makes it opaque).
    private static Color QaPixel(BitmapSource bmp, int x, int y)
    {
        x = Math.Clamp(x, 0, bmp.PixelWidth - 1);
        y = Math.Clamp(y, 0, bmp.PixelHeight - 1);
        byte[] px = new byte[4];
        bmp.CopyPixels(new Int32Rect(x, y, 1, 1), px, 4, 0);
        return Color.FromArgb(px[3], px[2], px[1], px[0]);
    }

    private static double Luma(Color c)
    {
        return 0.299 * c.R + 0.587 * c.G + 0.114 * c.B;
    }

    private static string Hex(Color c)
    {
        return $"#{c.R:X2}{c.G:X2}{c.B:X2}";
    }

    private void QaStage2Checks(List<(string Name, string Hash)> metroHashesAtStart)
    {
        QaTry("templates-parsed", delegate
        {
            bool sb = ScrollBarStyle.Value != null;
            bool pw = PowerPartTemplate.Value != null;
            bool tt = ToolTipStyle.Value != null;
            bool scoped = _progScroll.Resources[typeof(ScrollBar)] is Style s && ReferenceEquals(s, ScrollBarStyle.Value);
            return (sb && pw && tt && scoped, $"scrollbar={sb} (scoped to the list={scoped}) power={pw} tooltip={tt}");
        });
        QaTry("tooltip-style", () => _qaTooltipResult);
        QaTry("liveflip-plates", () => _qaLiveflipPlatesResult);
        QaTry("light-dismiss-inside", delegate
        {
            // A 408 x 495 window at (0, 545) on a 100% monitor: glass (0, 563)-(400, 1040), frame x 297..361, y 545..609.
            ShowMenu("qa");
            double savedScale = _placedScale;
            _placedScale = 1.0;
            try
            {
                int w = (int)Math.Round(265.0 + _rw);
                NativeRect r = new NativeRect { Left = 0, Top = 545, Right = w, Bottom = 1040 };
                int picX = (int)(Rx + Math.Round((_rw - 64.0) / 2.0, MidpointRounding.AwayFromZero));
                (string Name, int X, int Y, bool Inside)[] probes =
                {
                    ("list", 100, 700, true),
                    ("glass-top-left", 2, 563, true),
                    ("overhang", 20, 550, false),
                    ("picture-frame", picX + 10, 550, true),
                    ("beside-frame", picX + 70, 550, false),
                    ("shadow-band", w - 3, 700, false),
                    ("glass-right-edge", w - 9, 700, true)
                };
                List<string> bad = new List<string>();
                foreach ((string name, int x, int y, bool inside) in probes)
                {
                    if (InsideMenu(r, x, y) != inside)
                    {
                        bad.Add(name);
                    }
                }
                return (bad.Count == 0, $"probes={probes.Length} wrong=[{string.Join(", ", bad)}]");
            }
            finally
            {
                _placedScale = savedScale;
            }
        });
        QaTry("icons-worker", delegate
        {
            // The live path: one background STA worker loads the icon, Loaded fires on this dispatcher, the worker exits
            // after 1.5 s with an empty queue.
            AppEntry? a = LoadApps().FirstOrDefault((AppEntry x) => !IsGlyphEntry(x));
            if (a == null)
            {
                return (false, "no inventory entry");
            }
            // A size no earlier tag of this process requested, so the request really goes to the worker.
            int px = 48 + 2 * s_qaWorkerRuns++;
            string key = W7Icons.Key(a.LaunchPath, 32, px);
            int shutdownsBefore = W7Icons.DispatcherShutdowns;
            int raised = 0;
            Action<string> onLoaded = delegate (string k)
            {
                if (k == key)
                {
                    raised++;
                }
            };
            W7Icons.Synchronous = false;
            W7Icons.Loaded += onLoaded;
            try
            {
                W7Icons.Request(a.LaunchPath, 32, px, a.Name);
                bool started = W7Icons.WorkerAlive;
                Stopwatch sw = Stopwatch.StartNew();
                while (raised == 0 && sw.ElapsedMilliseconds < 4000)
                {
                    QaPump(20);
                }
                double loadMs = sw.Elapsed.TotalMilliseconds;
                bool got = W7Icons.TryGet(a.LaunchPath, 32, px, out W7Icons.Entry e) && e.Img is BitmapSource b && b.PixelWidth == px && b.PixelHeight == px;
                QaPump(2100);
                bool exited = !W7Icons.WorkerAlive;
                // The exiting worker shuts its own Dispatcher down (its message window and render state go with it).
                Stopwatch sd = Stopwatch.StartNew();
                while (W7Icons.DispatcherShutdowns == shutdownsBefore && sd.ElapsedMilliseconds < 1000)
                {
                    QaPump(20);
                }
                bool shutDown = W7Icons.DispatcherShutdowns > shutdownsBefore;
                return (started && raised == 1 && got && exited && shutDown, $"'{a.Name}' started={started} loaded={raised} in {loadMs:0}ms exact={got} exitedWhenIdle={exited} dispatcherShutDown={shutDown}");
            }
            finally
            {
                W7Icons.Loaded -= onLoaded;
                W7Icons.Synchronous = true;
            }
        });
        QaTry("icons-letter-retry", delegate
        {
            // A letter tile (no icon) is reused while fresh and loaded again once it is older than LetterRetryMs. A blank
            // path is one the loader always rejects.
            const string path = " ";
            W7Icons.Request(path, 16, 16, "Q");
            bool first = W7Icons.TryGet(path, 16, 16, out W7Icons.Entry e1) && e1.IsLetter;
            W7Icons.Request(path, 16, 16, "Q");
            bool reused = W7Icons.TryGet(path, 16, 16, out W7Icons.Entry e2) && ReferenceEquals(e2.Img, e1.Img);
            long saved = W7Icons.LetterRetryMs;
            bool retried;
            try
            {
                W7Icons.LetterRetryMs = 0;
                W7Icons.Request(path, 16, 16, "Q");
                retried = W7Icons.TryGet(path, 16, 16, out W7Icons.Entry e3) && !ReferenceEquals(e3.Img, e1.Img);
            }
            finally
            {
                W7Icons.LetterRetryMs = saved;
            }
            // A win81 entry whose glyph was released never goes to the shell: it shows a letter tile instead.
            AppEntry noGlyph = new AppEntry { Name = "QA glyph", LaunchPath = "win81:qa-noglyph", TileBrush = Brushes.Transparent };
            W7Row row = new W7Row(W7Row.Kind.Program22, onGlass: false);
            BindIcon(row, noGlyph);
            bool letterShown = row.IconSource is BitmapSource ls && ls.PixelWidth == 16;
            bool noShell = !W7Icons.TryGet(noGlyph.LaunchPath, 16, 16, out _);
            bool ok = first && reused && retried && letterShown && noShell;
            return (ok, $"letter={first} reusedWhileFresh={reused} reloadedWhenStale={retried} releasedGlyph: letter={letterShown} noShellRequest={noShell}");
        });
        QaTry("search-caret", delegate
        {
            // The caret (character 0) must sit clearly before the hint, and typed text must start where the hint did.
            ShowMenu("qa");
            UpdateLayout();
            Rect caret = _search.GetRectFromCharacterIndex(0);
            double caretX = _search.TransformToAncestor(_searchBox).Transform(caret.TopLeft).X;
            double cueX = QaBounds(_searchCue, _searchBox).Left;
            Rect box = QaBounds(_searchBox, this);
            int cueInk = QaFirstInk(SnapBitmap(), box);
            _search.Text = "s";
            UpdateLayout();
            int typedInk = QaFirstInk(SnapBitmap(), box);
            bool ok = cueX - caretX >= 2.0 && cueInk >= 0 && typedInk >= 0 && Math.Abs(typedInk - cueInk) <= 3;
            return (ok, $"caret x={caretX:0.##} hint x={cueX:0.##} gap={cueX - caretX:0.##} (>= 2); first ink column: hint {cueInk}, typed 's' {typedInk} (|diff| <= 3)");
        });
        QaTry("shutdown-template", delegate
        {
            ShowMenu("qa");
            UpdateLayout();
            // Both parts use the menu's own template (no Aero2 triggers) and paint from the palette.
            bool ok = true;
            List<string> parts = new List<string>();
            foreach (Button b in new[] { _powerMain, _powerArrow })
            {
                Border? fill = b.Template?.FindName("Fill", b) as Border;
                Border? hot = b.Template?.FindName("HotLayer", b) as Border;
                bool own = b.OverridesDefaultStyle && ReferenceEquals(b.Template, PowerPartTemplate.Value);
                bool fromPalette = fill != null && hot != null && ReferenceEquals(fill.Background, _paletteDict!["W7.PowerFill"]) && ReferenceEquals(hot.Background, _paletteDict["W7.PowerHotFill"]) && hot.Opacity == 0.0;
                ok &= own && fromPalette;
                parts.Add($"{b.Tag}: ownTemplate={own} paletteBrushes={fromPalette}");
            }
            return (ok, string.Join("; ", parts));
        });

        QaTry("geometry-window", delegate
        {
            QaInjectDefault();
            ShowMenu("qa");
            UpdateLayout();
            LayoutInfo l = _layout;
            bool ok = Near(ActualWidth, 265.0 + _rw) && Near(ActualHeight, l.V + 119.0) && Near(Width, l.W) && Near(Height, l.H);
            Rect g = QaBounds(_glass, this);
            ok &= Near(g.Left, 0.0) && Near(g.Top, 18.0) && Near(g.Width, 257.0 + _rw) && Near(g.Height, l.V + 101.0);
            return (ok, $"window {ActualWidth:0.##}x{ActualHeight:0.##} expected {265.0 + _rw:0.##}x{l.V + 119.0:0.##} (Rw={_rw:0.##} V={l.V:0.##}); glass {R4(g)}");
        });
        QaTry("geometry-list-pane", delegate
        {
            QaInjectDefault();
            ShowMenu("qa");
            UpdateLayout();
            Rect r = QaBounds(_pane, this);
            double v = _layout.V;
            bool ok = Near(r.Left, 7.0) && Near(r.Top, 25.0) && Near(r.Right, 257.0) && Near(r.Bottom, 25.0 + v + 48.0);
            Rect sv = QaBounds(_progScroll, this);
            ok &= Near(sv.Left, 8.0) && Near(sv.Top, 30.0) && Near(sv.Height, v);
            return (ok, $"pane {R4(r)} expected (7,25)-(257,{25.0 + v + 48.0:0.##}); viewport {R4(sv)}");
        });
        QaTry("geometry-search-box", delegate
        {
            ShowMenu("qa");
            UpdateLayout();
            Rect r = QaBounds(_searchBox, this);
            bool ok = Near(r.Left, 13.0) && Near(r.Right, 251.0) && Near(r.Bottom, ActualHeight - 11.0) && Near(r.Height, 24.0);
            return (ok, $"search {R4(r)} expected x 13..251, bottom {ActualHeight - 11.0:0.##}");
        });
        QaTry("geometry-shutdown", delegate
        {
            ShowMenu("qa");
            UpdateLayout();
            Rect r = QaBounds(_powerMain, this);
            Rect a = QaBounds(_powerArrow, this);
            Rect s = QaBounds(_searchBox, this);
            bool ok = Near(r.Left, 264.0) && Near(r.Right, 340.0) && Near(r.Bottom, ActualHeight - 11.0) && Near(a.Left, 340.0) && Near(a.Right, 362.0) && Near(r.Top, s.Top);
            return (ok, $"main {R4(r)} arrow {R4(a)} expected main x 264..340, bottom {ActualHeight - 11.0:0.##}, level with the search box top {s.Top:0.##}");
        });
        QaTry("geometry-picture-frame", delegate
        {
            ShowMenu("qa");
            UpdateLayout();
            Rect r = QaBounds(_picFrame, this);
            double x = 257.0 + Math.Round((_rw - 64.0) / 2.0, MidpointRounding.AwayFromZero);
            bool ok = Near(r.Left, x) && Near(r.Top, 0.0) && Near(r.Bottom, 64.0) && Near(r.Width, 64.0);
            return (ok, $"frame {R4(r)} expected x {x:0.##}, y 0..64 (sticks out {18.0 - r.Top:0.##} above the glass)");
        });
        QaTry("geometry-row36", delegate
        {
            QaInjectDefault();
            ShowMenu("qa");
            UpdateLayout();
            W7Row? row = QaListRows().FirstOrDefault((W7Row r) => r.RowKind == W7Row.Kind.Program36);
            if (row == null)
            {
                return (false, "no Program36 row");
            }
            Rect r = QaBounds(row, _progList);
            Rect ib = row.IconImage != null ? QaBounds(row.IconImage, row) : Rect.Empty;
            Rect tb = QaBounds(row.TextBlock, row);
            bool ok = Near(r.Height, 36.0) && Near(ib.Left, row.PlateShown ? 12.0 : 8.0) && Near(ib.Top, row.PlateShown ? 6.0 : 2.0) && Near(tb.Left, 48.0);
            return (ok, $"row '{row.Text}' {R4(r)} icon {R4(ib)} text x {tb.Left:0.##}");
        });
        QaTry("geometry-command-row", delegate
        {
            ShowMenu("qa");
            UpdateLayout();
            Rect r = QaBounds(_cmdRow, this);
            double v = _layout.V;
            bool ok = Near(r.Height, 30.0) && Near(r.Left, 11.0) && Near(r.Width, 242.0) && Near(r.Top, 18.0 + v + 21.0);
            Rect sep = QaBounds(_bottomSep, this);
            ok &= Near(sep.Top, 18.0 + v + 16.0) && Near(sep.Left, 15.0) && Near(sep.Right, 249.0);
            return (ok, $"command {R4(r)} separator {R4(sep)}");
        });
        QaTry("geometry-right-column", delegate
        {
            ShowMenu("qa");
            UpdateLayout();
            List<string> rows = new List<string>();
            bool ok = true;
            double expectTop = 52.0;
            foreach (UIElement child in _rightLinks.Children)
            {
                if (child is not FrameworkElement fe || fe.Visibility != Visibility.Visible)
                {
                    continue;
                }
                Rect r = QaBounds(fe, _glass);
                ok &= Near(r.Top, expectTop);
                if (fe is W7Row row)
                {
                    ok &= Near(r.Height, 31.0) && Near(r.Left, Rx + 6.0) && Near(r.Right, Rx + _rw - 7.0);
                    rows.Add($"{row.Text}@{r.Top:0}");
                }
                expectTop += fe.ActualHeight;
            }
            ok &= Near(expectTop, 419.0);
            return (ok, $"last row ends at G y {expectTop:0.##} (expected 419); " + string.Join(" ", rows));
        });

        QaTry("ellipsis", delegate
        {
            QaInjectDefault();
            QaInjectLongName();
            ShowMenu("qa");
            UpdateLayout();
            W7Row? row = QaListRows().FirstOrDefault();
            if (row == null || row.Text != QaLongName)
            {
                return (false, "first row is '" + row?.Text + "', not the long name");
            }
            double cw = _progList.ActualWidth;
            Rect tb = QaBounds(row.TextBlock, _progList);
            bool ok = row.IsTrimmed && tb.Right <= cw - 8.0 + 0.5 && row.ToolTip is ToolTip;
            return (ok, $"trimmed={row.IsTrimmed} text right={tb.Right:0.##} cw={cw:0.##} limit={cw - 8.0:0.##} tooltip={(row.ToolTip is ToolTip ? "W7" : "none")}");
        });

        QaTry("tooltip-relabel", delegate
        {
            // A label that stays cut off while its text changes (the account name link) gets a tooltip with the new text.
            ShowMenu("qa");
            UpdateLayout();
            const string first = "First very long account name that cannot fit the link";
            const string second = "Second very long account name that cannot fit the link";
            _nameRow.SetText(first);
            UpdateLayout();
            string? t1 = (_nameRow.ToolTip as ToolTip)?.Content as string;
            _nameRow.SetText(second);
            UpdateLayout();
            string? t2 = (_nameRow.ToolTip as ToolTip)?.Content as string;
            bool ok = _nameRow.IsTrimmed && t1 == first && t2 == second;
            return (ok, $"trimmed={_nameRow.IsTrimmed} tooltip after the first name='{t1}' after the second='{t2}'");
        });

        QaTry("no-mdl2", delegate
        {
            QaInjectDefault();
            ShowMenu("qa");
            List<string> bad = new List<string>();
            int texts = 0;
            void Scan()
            {
                UpdateLayout();
                QaWalk(_root, delegate (DependencyObject d)
                {
                    if (d is TextBlock tb)
                    {
                        texts++;
                        if (tb.FontFamily != null && tb.FontFamily.Source.Contains("MDL2", StringComparison.OrdinalIgnoreCase))
                        {
                            bad.Add(Describe(tb) + "'" + tb.Text + "'");
                        }
                    }
                });
            }
            Scan();
            ToggleAllPrograms();
            Scan();
            return (bad.Count == 0 && texts > 0, $"textblocks={texts} mdl2={bad.Count}" + (bad.Count > 0 ? " [" + string.Join(", ", bad.Take(8)) + "]" : string.Empty));
        });

        QaTry("cursor-arrow", delegate
        {
            QaInjectDefault();
            ShowMenu("qa");
            List<string> bad = new List<string>();
            void Scan()
            {
                UpdateLayout();
                QaWalk(_root, delegate (DependencyObject d)
                {
                    if (d is FrameworkElement fe && fe.Cursor == Cursors.Hand)
                    {
                        bad.Add(Describe(fe));
                    }
                });
            }
            Scan();
            ToggleAllPrograms();
            Scan();
            bool rootArrow = _root.Cursor == Cursors.Arrow;
            return (bad.Count == 0 && rootArrow, $"root={_root.Cursor} hand={bad.Count}" + (bad.Count > 0 ? " [" + string.Join(", ", bad.Distinct().Take(8)) + "]" : string.Empty));
        });

        // Live flip: the same root, one merged dictionary swapped, every sampled brush resolved to the new entry.
        QaTry("flip-no-rebuild", delegate
        {
            QaInjectDefault();
            ShowMenu("qa");
            UpdateLayout();
            FrameworkElement rootBefore = _root;
            ResourceDictionary? before = _paletteDict;
            bool dark = ShellTheme.IsDark;
            ResourceDictionary? after;
            bool sameRoot;
            bool swapped;
            bool paneOk;
            List<string> bad = new List<string>();
            int traceAt = _qaTrace.Count;
            try
            {
                ShellTheme.ForceForTest(!dark);
                UpdateLayout();
                after = _paletteDict;
                sameRoot = ReferenceEquals(rootBefore, _root) && ReferenceEquals(Content, _root);
                swapped = after != null && !ReferenceEquals(before, after) && Resources.MergedDictionaries.Count == 1 && ReferenceEquals(Resources.MergedDictionaries[0], after);
                paneOk = after != null && ReferenceEquals(_pane.Background, after["W7.PaneBg"]);
                W7Row? row = QaListRows().FirstOrDefault();
                (string Key, object? Value)[] samples =
                {
                    ("W7.PaneBg", _pane.Background),
                    ("W7.Ink", row?.TextBlock.Foreground),
                    ("W7.SearchBg", _searchBox.Background),
                    ("W7.GlassTint", _glassOuter.Background)
                };
                foreach ((string key, object? value) in samples)
                {
                    if (after == null || value == null || !ReferenceEquals(value, after[key]))
                    {
                        bad.Add(key);
                    }
                }
                Check("flip-brushes", bad.Count == 0, $"keys W7.PaneBg, W7.Ink, W7.SearchBg, W7.GlassTint resolved to the new dictionary; mismatched=[{string.Join(", ", bad)}] isDark={ShellTheme.IsDark}");
            }
            finally
            {
                ShellTheme.ForceForTest(dark);
            }
            bool cacheReused = ReferenceEquals(_paletteDict, before) && TraceSince(traceAt, "(cached)");
            Check("palette-cache", cacheReused, $"flip back reused the cached dictionary={ReferenceEquals(_paletteDict, before)}");
            return (sameRoot && swapped && paneOk, $"sameRoot={sameRoot} swapped={swapped} paneBg=newDict:{paneOk} merged={Resources.MergedDictionaries.Count}");
        });

        // Glass translucency, sheen and streaks, measured on the renders of this tag.
        QaTry("glass-translucent", delegate
        {
            if (_qaDefaultSnap == null || _qaWhiteSnap == null)
            {
                return (false, "renders missing");
            }
            double sum = 0.0;
            int n = 0;
            for (int y = (int)(Overhang + 52.0); y < (int)(Overhang + 419.0); y += 3)
            {
                for (int x = (int)(Rx + 6.0); x < (int)(Rx + _rw - 7.0); x += 3)
                {
                    Color a = QaPixel(_qaDefaultSnap, x, y);
                    Color b = QaPixel(_qaWhiteSnap, x, y);
                    sum += Math.Max(Math.Abs(a.R - b.R), Math.Max(Math.Abs(a.G - b.G), Math.Abs(a.B - b.B)));
                    n++;
                }
            }
            double mean = n > 0 ? sum / n : 0.0;
            return (mean > 10.0, $"right column mean max-channel difference default vs default-white = {mean:0.0} levels (> 10)");
        });
        QaTry("glass-sheen-streaks", delegate
        {
            if (_qaWhiteSnap == null)
            {
                return (false, "render missing");
            }
            // Sheen: the top 48 DIP of the glass is lighter than just below it (sampled left of the list pane).
            double top = Luma(QaPixel(_qaWhiteSnap, 3, (int)Overhang + 4));
            double below = Luma(QaPixel(_qaWhiteSnap, 3, (int)Overhang + 60));
            // Streaks: along the bottom glass line (below the search box) only the diagonal streaks vary the colour.
            int yLine = (int)(Overhang + _layout.Gh - 5.0);
            double min = 999.0;
            double max = -1.0;
            for (int x = 3; x < (int)(_layout.Gw - 3.0); x++)
            {
                double lv = Luma(QaPixel(_qaWhiteSnap, x, yLine));
                min = Math.Min(min, lv);
                max = Math.Max(max, lv);
            }
            bool ok = top - below >= 8.0 && max - min >= 4.0;
            return (ok, $"sheen top-vs-60 = {top - below:0.0} levels (>= 8); streak range on the bottom line = {max - min:0.0} levels (>= 4)");
        });

        // Rendered contrast of white glass text over a white wallpaper (E10): sampled right of every link's text.
        QaTry("contrast-glass-rendered", delegate
        {
            ShowMenu("qa");
            UpdateLayout();
            if (_qaWhiteSnap == null)
            {
                return (false, "default-white render missing");
            }
            double target = _paletteIsDark ? 7.0 : 4.5;
            List<string> rows = new List<string>();
            bool ok = true;
            foreach (W7Row row in _placeRows)
            {
                if (row.Visibility != Visibility.Visible)
                {
                    continue;
                }
                double rowTop = QaBounds(row, _glass).Top;
                int x = (int)Math.Round(257.0 + _rw - 8.0, MidpointRounding.AwayFromZero);
                int y = (int)Math.Round(18.0 + rowTop + 15.0, MidpointRounding.AwayFromZero);
                Color c = QaPixel(_qaWhiteSnap, x, y);
                double cr = Win7Palette.Contrast(Colors.White, c);
                ok &= cr >= target;
                rows.Add($"{row.Text}({x},{y}){Hex(c)}={cr:0.00}");
            }
            return (ok && rows.Count >= 11, $"target>={target} rows={rows.Count}: " + string.Join("; ", rows));
        });

        // Palette targets computed from the active (fixed-source) dictionary.
        try
        {
            ShowMenu("qa");
            ResourceDictionary d = _paletteDict!;
            bool dark = _paletteIsDark;
            Color C(string k) => Win7Palette.StartColor(d[k]);
            Color Mid(string k)
            {
                Color a = Win7Palette.StartColor(d[k]);
                Color b = Win7Palette.EndColor(d[k]);
                return Color.FromRgb((byte)((a.R + b.R + 1) / 2), (byte)((a.G + b.G + 1) / 2), (byte)((a.B + b.B + 1) / 2));
            }
            void Target(string name, Color fg, Color bg, double min)
            {
                double cr = Win7Palette.Contrast(fg, bg);
                Check("contrast-palette-" + name, cr >= min, $"{Hex(fg)} on {Hex(bg)} = {cr:0.00} (>= {min})");
            }
            Target("ink-pane", C("W7.Ink"), C("W7.PaneBg"), 7.0);
            Target("ink-hot", C("W7.Ink"), Mid("W7.HotFill"), 7.0);
            Target("search-hint", C("W7.SearchHint"), C("W7.SearchBg"), 4.5);
            Target("search-ink", C("W7.SearchInk"), C("W7.SearchBg"), 7.0);
            Target("menu-ink", C("W7.MenuInk"), C("W7.MenuBg"), 7.0);
            Target("tip-ink", C("W7.TipInk"), Win7Palette.EndColor(d["W7.TipBg"]), 4.5);
            Color glassStop1 = Win7Palette.EndColor(d["W7.GlassTint"]);
            Color powerHot = Win7Palette.Over(Win7Palette.EndColor(d["W7.PowerHotFill"]), Win7Palette.Over(glassStop1, Colors.White));
            Target("power-hot", C("W7.PowerInk"), powerHot, 4.5);
            Win7Palette.GlassStopColors(QaGlassSourceFixed, dark, true, out int steps, out double guard);
            Check("contrast-palette-glass-guard", guard >= (dark ? 7.0 : 4.5), $"white on stop 0 + streak peak over white = {guard:0.00} (>= {(dark ? 7.0 : 4.5)}), darkened {steps} step(s)");
            Info($"glass stops {string.Join(" -> ", ((LinearGradientBrush)d["W7.GlassTint"]).GradientStops.Select((GradientStop g) => g.Color.ToString()))}");
        }
        catch (Exception ex)
        {
            Check("contrast-palette", false, "exception " + ex.Message);
        }
        finally
        {
            if (IsVisible)
            {
                Dismiss("qa reset", instant: true);
            }
        }

        QaIconChecks();

        QaTry("metro-icons-unchanged", delegate
        {
            List<(string Name, string Hash)> now = QaMetroIconHashes();
            // The reference captured before the Win7 icon work: the nearest metro-icon-hashes-before.txt in the output
            // folder or any folder above it.
            string? refPath = null;
            for (DirectoryInfo? dir = new DirectoryInfo(Path.GetFullPath(_qaOutDir)); dir != null; dir = dir.Parent)
            {
                string p = Path.Combine(dir.FullName, "metro-icon-hashes-before.txt");
                if (File.Exists(p))
                {
                    refPath = p;
                    break;
                }
            }
            bool sameAsStart = now.Count == metroHashesAtStart.Count && now.Zip(metroHashesAtStart).All(p => p.First == p.Second);
            string nowText = string.Join("; ", now.Select(((string Name, string Hash) h) => h.Name + " " + h.Hash));
            if (refPath == null)
            {
                Info("metro-icons-unchanged: no metro-icon-hashes-before.txt in the output folder or above it; only the within-run comparison applies");
                return (sameAsStart && now.Count > 0, $"LoadIcon(path, 64) unchanged within this run={sameAsStart}; now={nowText}");
            }
            Info("metro-icons-unchanged reference " + refPath);
            Dictionary<string, string> before = File.ReadAllLines(refPath)
                .Select((string l) => l.Split('\t'))
                .Where((string[] p) => p.Length == 2)
                .ToDictionary((string[] p) => p[0], (string[] p) => p[1], StringComparer.Ordinal);
            List<string> parts = new List<string>();
            bool ok = now.Count == 3 && sameAsStart;
            foreach ((string name, string hash) in now)
            {
                bool match = before.TryGetValue(name, out string? b) && b == hash;
                ok &= match;
                parts.Add($"{name} {(match ? "same" : "DIFFERENT " + b + " -> " + hash)}");
            }
            return (ok, $"LoadIcon(path, 64) vs before the change: " + string.Join("; ", parts) + $"; unchanged within this run={sameAsStart}");
        });
    }

    // icons-no-dots, icons-size and icons-visible over the main view (32 px) and All Programs (16 px) of this inventory;
    // win81-icon for the live inventory's PC settings glyph.
    private void QaIconChecks()
    {
        try
        {
            QaInjectDefault();
            ShowMenu("qa");
            UpdateLayout();
            List<W7Row> rows = QaListRows();
            // All Programs with every folder open: every program of the inventory at 16 px, plus the folder icons.
            ToggleAllPrograms();
            QaExpandAll();
            rows.AddRange(QaListRows());
            Color pane = Win7Palette.StartColor(_paletteDict!["W7.PaneBg"]);
            List<string> dots = new List<string>();
            List<string> sizes = new List<string>();
            List<string> invisible = new List<string>();
            List<string> plated = new List<string>();
            List<string> notNeeded = new List<string>();
            List<string> watched = new List<string>();
            List<string> darkBodies = new List<string>();
            bool dark = _paletteIsDark;
            int count = 0;
            int folderRows = 0;
            foreach (W7Row row in rows)
            {
                string name;
                if (row.Payload is AppEntry a)
                {
                    name = a.Name;
                }
                else if (row.Payload is TreeNode folder)
                {
                    name = "folder " + folder.Name;
                    folderRows++;
                }
                else
                {
                    continue;
                }
                count++;
                int px = row.IconBox;
                if (row.IconSource is not BitmapSource src)
                {
                    sizes.Add(name + "(no source)");
                    dots.Add(name + "(no source)");
                    continue;
                }
                if (src.PixelWidth != px || src.PixelHeight != px)
                {
                    sizes.Add($"{name}({src.PixelWidth}x{src.PixelHeight}/{px})");
                }
                int inset = W7Icons.PlatedPx(px, px);
                if (row.PlateShown && row.DisplayedSource is BitmapSource shown && (shown.PixelWidth != inset || shown.PixelHeight != inset))
                {
                    sizes.Add($"{name}(plated {shown.PixelWidth}x{shown.PixelHeight}/{inset})");
                }
                (int bw, int bh, Color mean, int opaque) = QaIconStats(src);
                if (bw < 0.45 * px || bh < 0.45 * px)
                {
                    dots.Add($"{name}({bw}x{bh}/{px})");
                }
                if (QaWatchedIcon(name))
                {
                    watched.Add($"{name}@{px}:{bw}x{bh}");
                }
                // Visibility is measured on what is drawn, independently of the classifier: the most contrasting tenth of the
                // icon's opaque pixels, composited over the plate or the pane, against that background.
                Color bg = pane;
                BitmapSource drawn = src;
                double coverage = opaque / (double)(src.PixelWidth * src.PixelHeight);
                if (row.PlateShown)
                {
                    bg = Win7Palette.StartColor(row.PlateBorder!.Background);
                    drawn = row.DisplayedSource as BitmapSource ?? src;
                    plated.Add($"{name}@{px}({(row.AlwaysPlate ? "tile" : row.PlateKind.ToString())})");
                    // A plate on an icon that already stands out from the pane (an outline or a dark part at 3:1) is not
                    // needed. A DarkFill icon is plated for its body, not its edge: its plate is needed while the median
                    // pixel is below 1.5:1 on the pane.
                    double onPane = QaEdgeContrast(src, pane);
                    double medianOnPane = QaEdgeContrast(src, pane, 0.5);
                    if (!row.AlwaysPlate && (row.PlateKind == IconPlate.DarkFill ? medianOnPane >= 1.5 : onPane >= 3.0))
                    {
                        notNeeded.Add($"{name}@{px}({row.PlateKind} edge {onPane:0.00} median {medianOnPane:0.00})");
                    }
                }
                double cr = QaEdgeContrast(drawn, bg);
                if (cr < 1.5)
                {
                    invisible.Add($"{name}@{px}(edge {cr:0.00} on {Hex(bg)}, mean {Hex(mean)})");
                }
                // A mostly opaque icon must not be a hole in the dark pane: its median pixel reaches 1.3:1 on what is
                // behind it (the pane, or its plate), or at least a quarter of it reaches 1.5:1 (a black console or
                // archive icon with a light frame and lettering reads well, as in the Windows dark theme).
                if (dark && coverage >= 0.5)
                {
                    double med = QaEdgeContrast(drawn, bg, 0.5);
                    double p75 = QaEdgeContrast(drawn, bg, 0.75);
                    if (med < 1.3 && p75 < 1.5)
                    {
                        invisible.Add($"{name}@{px}(median {med:0.00} p75 {p75:0.00} on {Hex(bg)}, coverage {coverage:0.00})");
                    }
                    double paneMed = QaEdgeContrast(src, pane, 0.5);
                    if (paneMed < 1.5)
                    {
                        darkBodies.Add($"{name}@{px}(coverage {coverage:0.00} on pane p50 {paneMed:0.00} p75 {QaEdgeContrast(src, pane, 0.75):0.00} p90 {QaEdgeContrast(src, pane):0.00}, class {row.PlateKind}, plate {row.PlateShown})");
                    }
                }
            }
            Check("icons-no-dots", dots.Count == 0 && count > 0, $"rows={count} (folders {folderRows}) dots={dots.Count}" + (dots.Count > 0 ? " [" + string.Join(", ", dots.Take(20)) + "]" : string.Empty) + "; watched: " + (watched.Count > 0 ? string.Join(", ", watched) : "none present"));
            Check("icons-size", sizes.Count == 0 && count > 0, $"rows={count} wrong-size={sizes.Count}" + (sizes.Count > 0 ? " [" + string.Join(", ", sizes.Take(20)) + "]" : string.Empty));
            Check("icons-visible", invisible.Count == 0 && count > 0, $"rows={count} top-decile contrast < 1.5" + (dark ? " or (coverage >= 50%) median < 1.3 and 75th percentile < 1.5" : string.Empty) + $": {invisible.Count}" + (invisible.Count > 0 ? " [" + string.Join(", ", invisible.Take(20)) + "]" : string.Empty));
            if (dark)
            {
                Info($"icons dark-bodied (coverage >= 50%, median < 1.5 on the pane) count={darkBodies.Count}" + (darkBodies.Count > 0 ? ": " + string.Join(", ", darkBodies.Distinct()) : string.Empty));
            }
            Check("icons-plate-needed", notNeeded.Count == 0, $"plated={plated.Count} plated although the icon already reaches 3:1 on the pane: {notNeeded.Count}" + (notNeeded.Count > 0 ? " [" + string.Join(", ", notNeeded.Take(20)) + "]" : string.Empty));
            Info($"icons plated={plated.Count}: " + string.Join(", ", plated));
            // Rows whose final icon is a generated letter tile (no shell icon was found). A letter tile is full size and
            // high contrast, so the checks above pass on it; this line keeps them tracked per inventory.
            List<string> letters = rows
                .Where((W7Row r) => r.Payload is AppEntry la && !IsGlyphEntry(la)
                    && W7Icons.TryGet(la.LaunchPath, r.IconBox, Math.Max(16, (int)Math.Round(r.IconBox * _iconScale, MidpointRounding.AwayFromZero)), out W7Icons.Entry le) && le.IsLetter)
                .Select((W7Row r) => ((AppEntry)r.Payload!).Name + "@" + r.IconBox + " (" + ((AppEntry)r.Payload!).LaunchPath + ")")
                .Distinct()
                .ToList();
            Info($"letter-tiles count={letters.Count}" + (letters.Count > 0 ? ": " + string.Join(", ", letters) : string.Empty));

            W7Row? pc = rows.FirstOrDefault((W7Row r) => r.Payload is AppEntry e && string.Equals(e.LaunchPath, "win81:pcsettings", StringComparison.OrdinalIgnoreCase));
            if (pc == null)
            {
                Info("win81-icon not applicable: this inventory has no PC settings entry");
            }
            else
            {
                BitmapSource? s = pc.IconSource as BitmapSource;
                (int bw, int bh, Color mean, int opaque) = s != null ? QaIconStats(s) : (0, 0, Colors.Transparent, 0);
                double coverage = s != null ? opaque / (double)(s.PixelWidth * s.PixelHeight) : 0.0;
                AppEntry e = (AppEntry)pc.Payload!;
                bool onTile = pc.PlateShown && ReferenceEquals(pc.PlateBorder!.Background, e.TileBrush);
                // A letter tile is an opaque square; the gear glyph covers well under that.
                bool notLetter = s != null && coverage < 0.9;
                Check("win81-icon", onTile && notLetter, $"plate={pc.PlateShown} plateIsTileBrush={onTile} coverage={coverage:0.00} glyph-not-letter={notLetter} box={pc.IconBox}");
            }
        }
        catch (Exception ex)
        {
            Check("icons", false, "exception " + ex.Message);
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

    private static bool QaWatchedIcon(string name)
    {
        return name.Equals("7-Zip File Manager", StringComparison.OrdinalIgnoreCase)
            || name.Equals("Send to OneNote", StringComparison.OrdinalIgnoreCase)
            || name.Equals("Spreadsheet Compare", StringComparison.OrdinalIgnoreCase);
    }

    // WCAG contrast against bg of the most contrasting tenth (90th percentile, or another percentile) of the pixels with
    // alpha >= 0x80, each composited over bg. 0 when the icon has no such pixel.
    private static double QaEdgeContrast(BitmapSource src, Color bg, double percentile = 0.90)
    {
        BitmapSource b = src.Format == PixelFormats.Bgra32 ? src : new FormatConvertedBitmap(src, PixelFormats.Bgra32, null, 0.0);
        int stride = b.PixelWidth * 4;
        byte[] px = new byte[stride * b.PixelHeight];
        b.CopyPixels(px, stride, 0);
        List<double> cr = new List<double>();
        for (int o = 0; o < px.Length; o += 4)
        {
            if (px[o + 3] < 0x80)
            {
                continue;
            }
            Color c = Win7Palette.Over(Color.FromArgb(px[o + 3], px[o + 2], px[o + 1], px[o]), bg);
            cr.Add(Win7Palette.Contrast(c, bg));
        }
        if (cr.Count == 0)
        {
            return 0.0;
        }
        cr.Sort();
        double at = percentile * (cr.Count - 1);
        return cr[(int)(percentile >= 0.9 ? Math.Ceiling(at) : Math.Floor(at))];
    }

    // First column inside the search box (window coordinates) that carries text ink: a pixel that differs from the box
    // background by more than 40 luma levels, between 5 px below the top and 5 px above the bottom. -1 when none.
    private static int QaFirstInk(BitmapSource bmp, Rect box)
    {
        int left = (int)Math.Round(box.Left, MidpointRounding.AwayFromZero);
        int top = (int)Math.Round(box.Top, MidpointRounding.AwayFromZero);
        int bottom = (int)Math.Round(box.Bottom, MidpointRounding.AwayFromZero);
        double bg = Luma(QaPixel(bmp, left + 120, top + 3));
        for (int x = left + 2; x < left + 90; x++)
        {
            for (int y = top + 5; y < bottom - 5; y++)
            {
                if (Math.Abs(Luma(QaPixel(bmp, x, y)) - bg) > 40.0)
                {
                    return x;
                }
            }
        }
        return -1;
    }

    private (bool Ok, string Detail) _qaLiveflipPlatesResult = (false, "not rendered");

    // Whether a row's plate should show in a theme, from its icon class (the rule W7Row.UpdatePlate applies).
    private static bool QaExpectPlate(W7Row r, bool dark)
    {
        if (r.IconSource == null)
        {
            return false;
        }
        IconPlate k = r.PlateKind;
        return r.AlwaysPlate
            || (!dark && (k == IconPlate.LightMono || k == IconPlate.PaleOnLight))
            || (dark && (k == IconPlate.DarkMono || k == IconPlate.PaleOnDark || k == IconPlate.DarkFill));
    }

    // liveflip-plates: All Programs scrolled to the first program whose plate depends on the theme, flipped while visible
    // (win7start-<tag>-liveflip-plates.png). Every row's plate must follow the rule for the new theme and match a fresh
    // All Programs view built in that theme at the same offset, row for row and pixel for pixel.
    private void QaLiveFlipPlates()
    {
        try
        {
            UpdateLayout();
            W7Row? target = QaListRows().FirstOrDefault((W7Row r) => r.PlateKind != IconPlate.None && !r.AlwaysPlate);
            double offset = target != null ? Math.Max(0.0, QaBounds(target, _progList).Top - 66.0) : 0.0;
            _progScroll.ScrollToVerticalOffset(offset);
            UpdateLayout();
            bool dark = ShellTheme.IsDark;
            List<string> flipped;
            List<string> fresh;
            List<string> wrong = new List<string>();
            BitmapSource flipSnap;
            BitmapSource freshSnap;
            int themed = 0;
            try
            {
                ShellTheme.ForceForTest(!dark);
                flipSnap = Snap("liveflip-plates");
                flipped = QaListRows().Select((W7Row r) => r.Text + "=" + r.PlateShown).ToList();
                foreach (W7Row r in QaListRows())
                {
                    if (r.PlateKind != IconPlate.None && !r.AlwaysPlate)
                    {
                        themed++;
                    }
                    if (QaExpectPlate(r, !dark) != r.PlateShown)
                    {
                        wrong.Add(r.Text);
                    }
                }
                // A fresh view: All Programs really rebuilt in the new theme, with the same folders open.
                ShowMenu("qa");
                _allStale = true;
                ToggleAllPrograms();
                QaExpandAll();
                _progScroll.ScrollToVerticalOffset(offset);
                freshSnap = SnapBitmap();
                fresh = QaListRows().Select((W7Row r) => r.Text + "=" + r.PlateShown).ToList();
            }
            finally
            {
                ShellTheme.ForceForTest(dark);
            }
            (int diffPx, int maxDelta) = QaDiff(freshSnap, flipSnap);
            bool same = flipped.SequenceEqual(fresh);
            bool ok = same && wrong.Count == 0 && diffPx == 0;
            _qaLiveflipPlatesResult = (ok, $"flipped to {(!dark ? "dark" : "light")} at offset {offset:0}: rows={flipped.Count} theme-dependent={themed} (first '{target?.Text ?? "none"}') wrongPlate={wrong.Count}" + (wrong.Count > 0 ? " [" + string.Join(", ", wrong.Take(8)) + "]" : string.Empty) + $" sameAsFresh={same} vsFresh={diffPx}px maxdelta={maxDelta}");
        }
        catch (Exception ex)
        {
            _qaLiveflipPlatesResult = (false, "exception " + ex.Message);
        }
    }

    // Opaque bounding box (alpha > 0x60) and the mean colour of the pixels with alpha >= 0x80.
    private static (int W, int H, Color Mean, int Opaque) QaIconStats(BitmapSource src)
    {
        BitmapSource b = src.Format == PixelFormats.Bgra32 ? src : new FormatConvertedBitmap(src, PixelFormats.Bgra32, null, 0.0);
        int w = b.PixelWidth;
        int h = b.PixelHeight;
        int stride = w * 4;
        byte[] px = new byte[stride * h];
        b.CopyPixels(px, stride, 0);
        int minX = w;
        int minY = h;
        int maxX = -1;
        int maxY = -1;
        long r = 0;
        long g = 0;
        long bl = 0;
        int n = 0;
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                int o = y * stride + x * 4;
                byte a = px[o + 3];
                if (a > 0x60)
                {
                    minX = Math.Min(minX, x);
                    maxX = Math.Max(maxX, x);
                    minY = Math.Min(minY, y);
                    maxY = Math.Max(maxY, y);
                }
                if (a >= 0x80)
                {
                    bl += px[o];
                    g += px[o + 1];
                    r += px[o + 2];
                    n++;
                }
            }
        }
        int bw = maxX >= minX ? maxX - minX + 1 : 0;
        int bh = maxY >= minY ? maxY - minY + 1 : 0;
        Color mean = n > 0 ? Color.FromRgb((byte)(r / n), (byte)(g / n), (byte)(bl / n)) : Colors.Transparent;
        return (bw, bh, mean, n);
    }

    // The Win7 tooltip, rendered detached over a pane-coloured square (tooltips open in a popup, which the harness never
    // shows): win7start-<tag>-tooltip.png.
    private (bool Ok, string Detail) _qaTooltipResult = (false, "not rendered");

    private static int s_qaWorkerRuns;

    private void QaSnapTooltip()
    {
        try
        {
            ToolTip tip = W7ToolTip(QaLongName);
            tip.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            tip.Arrange(new Rect(tip.DesiredSize));
            tip.UpdateLayout();
            int tw = Math.Max(1, (int)Math.Ceiling(tip.ActualWidth));
            int th = Math.Max(1, (int)Math.Ceiling(tip.ActualHeight));
            RenderTargetBitmap tipBmp = new RenderTargetBitmap(tw, th, 96.0, 96.0, PixelFormats.Pbgra32);
            tipBmp.Render(tip);
            int w = tw + 32;
            int h = th + 32;
            DrawingVisual dv = new DrawingVisual();
            using (DrawingContext dc = dv.RenderOpen())
            {
                dc.DrawRectangle(new SolidColorBrush(Win7Palette.StartColor(_paletteDict?["W7.PaneBg"])), null, new Rect(0.0, 0.0, w, h));
                dc.DrawImage(tipBmp, new Rect(16.0, 16.0, tw, th));
            }
            RenderTargetBitmap rtb = new RenderTargetBitmap(w, h, 96.0, 96.0, PixelFormats.Pbgra32);
            rtb.Render(dv);
            PngBitmapEncoder enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(rtb));
            using (FileStream fs = File.Create(Path.Combine(_qaOutDir, "win7start-" + _qaTag + "-tooltip.png")))
            {
                enc.Save(fs);
            }
            _qaSheet.Add(("tooltip", rtb));
            int opaque = QaCountOpaque(tipBmp, new Int32Rect(0, 0, tw, th));
            bool styled = ReferenceEquals(tip.Style, ToolTipStyle.Value) && tip.Resources.Contains("W7.TipBg") && !tip.HasDropShadow;
            _qaTooltipResult = (styled && opaque > tw * th / 2, $"W7 style={ReferenceEquals(tip.Style, ToolTipStyle.Value)} keys={tip.Resources.Contains("W7.TipBg")} dropShadow={tip.HasDropShadow} size={tw}x{th} drawn={opaque}px");
        }
        catch (Exception ex)
        {
            _qaTooltipResult = (false, "exception " + ex.Message);
        }
    }

    // Contact sheet of every render of this tag: 4 per row, 8 px gutter, the state name in 12 px above each.
    private void QaWriteSheet()
    {
        if (_qaSheet.Count == 0)
        {
            return;
        }
        const int gutter = 8;
        const int label = 18;
        const int cols = 4;
        int cw = _qaSheet.Max(((string State, BitmapSource Bmp) s) => s.Bmp.PixelWidth);
        int ch = _qaSheet.Max(((string State, BitmapSource Bmp) s) => s.Bmp.PixelHeight);
        int rows = (_qaSheet.Count + cols - 1) / cols;
        int w = gutter + cols * (cw + gutter);
        int h = gutter + rows * (label + ch + gutter);
        DrawingVisual dv = new DrawingVisual();
        using (DrawingContext dc = dv.RenderOpen())
        {
            dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(0x30, 0x30, 0x30)), null, new Rect(0.0, 0.0, w, h));
            Typeface tf = new Typeface(W7Row.UiFont, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
            for (int i = 0; i < _qaSheet.Count; i++)
            {
                int x = gutter + (i % cols) * (cw + gutter);
                int y = gutter + (i / cols) * (label + ch + gutter);
                FormattedText ft = new FormattedText(_qaTag + "  " + _qaSheet[i].State, System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight, tf, 12.0, Brushes.White, 1.0);
                dc.DrawText(ft, new Point(x, y + 1));
                BitmapSource bmp = _qaSheet[i].Bmp;
                dc.DrawImage(bmp, new Rect(x, y + label, bmp.PixelWidth, bmp.PixelHeight));
            }
        }
        RenderTargetBitmap rtb = new RenderTargetBitmap(w, h, 96.0, 96.0, PixelFormats.Pbgra32);
        rtb.Render(dv);
        PngBitmapEncoder enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(rtb));
        using FileStream fs = File.Create(Path.Combine(_qaOutDir, "win7start-" + _qaTag + "-sheet.png"));
        enc.Save(fs);
    }

    private bool QaRowsHavePseudo()
    {
        return QaRowsHave("win81:desktop") || QaRowsHave("win81:news");
    }

    private bool QaRowsHave(string launchPath)
    {
        foreach (UIElement child in _progList.Children)
        {
            if (child is FrameworkElement fe && fe.Tag is AppEntry a && string.Equals(a.LaunchPath, launchPath, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    private static void QaWalk(DependencyObject d, Action<DependencyObject> visit)
    {
        visit(d);
        int n = VisualTreeHelper.GetChildrenCount(d);
        for (int i = 0; i < n; i++)
        {
            QaWalk(VisualTreeHelper.GetChild(d, i), visit);
        }
    }

    // Pixels whose channels differ by more than 2 levels (the tolerance absorbs icon anti-aliasing noise), and the
    // largest channel difference seen. A missing or differently sized reference counts as fully different.
    private static (int DiffPx, int MaxDelta) QaDiff(BitmapSource? a, BitmapSource b)
    {
        if (a == null || a.PixelWidth != b.PixelWidth || a.PixelHeight != b.PixelHeight)
        {
            return (b.PixelWidth * b.PixelHeight, 255);
        }
        int stride = a.PixelWidth * 4;
        byte[] pa = new byte[stride * a.PixelHeight];
        byte[] pb = new byte[stride * b.PixelHeight];
        a.CopyPixels(pa, stride, 0);
        b.CopyPixels(pb, stride, 0);
        int diff = 0;
        int max = 0;
        for (int i = 0; i < pa.Length; i += 4)
        {
            int d = 0;
            for (int c = 0; c < 4; c++)
            {
                d = Math.Max(d, Math.Abs(pa[i + c] - pb[i + c]));
            }
            max = Math.Max(max, d);
            if (d > 2)
            {
                diff++;
            }
        }
        return (diff, max);
    }

    // The Metro surfaces load icons through AppInventory.LoadIcon. Three sample shortcuts are hashed at 64 px, so any
    // change to the shared loaders shows up as a difference against the reference captured before the Win7 icon work.
    private static List<(string Name, string Hash)> QaMetroIconHashes()
    {
        List<(string Name, string Hash)> res = new List<(string Name, string Hash)>();
        foreach (AppEntry a in AppInventory.LoadFromStartMenuLinks())
        {
            if (res.Count >= 3)
            {
                break;
            }
            if (AppInventory.LoadIcon(a.LaunchPath, 64) is BitmapSource bs)
            {
                res.Add((a.Name, QaHash(bs)));
            }
        }
        return res;
    }

    private static string QaHash(BitmapSource bs)
    {
        BitmapSource b = bs.Format == PixelFormats.Bgra32 ? bs : new FormatConvertedBitmap(bs, PixelFormats.Bgra32, null, 0.0);
        int stride = b.PixelWidth * 4;
        byte[] px = new byte[stride * b.PixelHeight];
        b.CopyPixels(px, stride, 0);
        byte[] hash = System.Security.Cryptography.SHA256.HashData(px);
        return b.PixelWidth + "x" + b.PixelHeight + ":" + Convert.ToHexString(hash, 0, 12);
    }

    private static int QaCountOpaque(BitmapSource bmp, Int32Rect r)
    {
        int stride = bmp.PixelWidth * 4;
        byte[] px = new byte[stride * bmp.PixelHeight];
        bmp.CopyPixels(px, stride, 0);
        int n = 0;
        for (int y = Math.Max(0, r.Y); y < Math.Min(bmp.PixelHeight, r.Y + r.Height); y++)
        {
            for (int x = Math.Max(0, r.X); x < Math.Min(bmp.PixelWidth, r.X + r.Width); x++)
            {
                if (px[y * stride + x * 4 + 3] != 0)
                {
                    n++;
                }
            }
        }
        return n;
    }

    private static string Describe(object? o)
    {
        if (o == null)
        {
            return "none";
        }
        string name = (o as FrameworkElement)?.Name ?? string.Empty;
        string tag = (o as FrameworkElement)?.Tag switch
        {
            AppEntry a => "row:" + a.Name,
            string s => s,
            _ => string.Empty
        };
        return o.GetType().Name + (name.Length > 0 ? "#" + name : string.Empty) + (tag.Length > 0 ? "[" + tag + "]" : string.Empty);
    }

    private void QaWriteOutputs(string outDir, string tag)
    {
        // Open-time metrics over every show of the run (recorded, not gated; the warm figures come from QaPerfMetrics).
        // The first show of a tag is a cold one when the icon cache is still empty (the first tag of a process). A later
        // show that brought new rows (other pins, other usage) loads their icons on the UI thread here, which the live
        // menu does off-thread, so those shows are reported on their own.
        if (_qaPrepMs.Count > 0)
        {
            Metric("prepare-first-ms", _qaPrepMs[0].Ms);
            List<(double Ms, int IconLoads)> loading = _qaPrepMs.Skip(1).Where(((double Ms, int IconLoads) p) => p.IconLoads > 0).ToList();
            if (loading.Count > 0)
            {
                Metric("prepare-with-qa-icon-loads-max-ms", loading.Max(((double Ms, int IconLoads) p) => p.Ms));
                Metric("prepare-with-qa-icon-loads-shows", loading.Count);
            }
            Metric("prepare-shows", _qaPrepMs.Count);
        }
        QaWriteChecks(Path.Combine(outDir, "win7start-" + tag + "-checks.txt"), _qaChecks);
        File.WriteAllLines(Path.Combine(outDir, "win7start-" + tag + "-trace.txt"), _qaTrace);
        File.WriteAllLines(Path.Combine(outDir, "win7start-" + tag + "-actions.txt"), _qaActions);
    }
}

// Windows 7 Start menu QA, stage 3: interaction states, keyboard model, motion, Win7 menus and the recent-list rules.
// Menus are never opened as real popups: they are laid out detached and composited into the render at the position
// their placement would give them.
public sealed partial class Win7StartMenu
{
    private readonly List<string> _qaRenderFailures = new List<string>();

    private readonly List<string> _qaRendered = new List<string>();

    // Filled by the 'editmenu-empty' render: Cut and Copy disabled and drawn in W7.MenuInkDisabled.
    private (bool Ok, string Detail) _qaDisabledMenuResult = (false, "not rendered");

    // Programs the 'mru' render pins when the inventory has them; otherwise the first unused programs are pinned.
    private static readonly string[] QaPinPreferred =
    {
        "Google Chrome", "Microsoft Edge", "Firefox", "Word", "Excel", "Outlook", "Visual Studio Code", "Notepad++"
    };

    // Raises a synthetic mouse event on the element; _qaPointer says where it lands (in the element's coordinates).
    private void QaMouse(UIElement target, RoutedEvent ev, Point at)
    {
        _qaPointer = at;
        MouseEventArgs args = ReferenceEquals(ev, UIElement.MouseMoveEvent)
            ? new MouseEventArgs(Mouse.PrimaryDevice, Environment.TickCount)
            : new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left);
        args.RoutedEvent = ev;
        args.Source = target;
        target.RaiseEvent(args);
    }

    // A left click: press and release inside the element without moving.
    private void QaClick(UIElement target)
    {
        QaMouse(target, UIElement.MouseLeftButtonDownEvent, new Point(5.0, 5.0));
        QaMouse(target, UIElement.MouseLeftButtonUpEvent, new Point(6.0, 5.0));
        _qaPointer = null;
    }

    private static string QaHeaders(ContextMenu cm)
    {
        return string.Join(", ", cm.Items.Cast<object>().Select((object i) => i switch
        {
            Separator => "|",
            MenuItem m => (m.Header as string ?? "?") + (m.FontWeight == FontWeights.Bold ? "*" : string.Empty),
            _ => "?"
        }));
    }

    // Lays a menu out the way it will open: palette keys and styles applied, template, measure and arrange.
    private void QaLayoutDetached(ContextMenu cm)
    {
        PrepareChildMenu(cm);
        cm.ApplyTemplate();
        cm.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        cm.Arrange(new Rect(cm.DesiredSize));
        cm.UpdateLayout();
    }

    // The menu's own pixels, laid out detached. A ContextMenu cannot be hosted anywhere but in its own popup (WPF throws
    // for any other parent), and a real popup would be placed on a monitor, so there is no second way to draw it: an
    // empty result is a render failure (the state is reported as failed, its check FAILs) rather than a blank render.
    private BitmapSource RenderDetached(ContextMenu cm)
    {
        QaLayoutDetached(cm);
        int w = Math.Max(1, (int)Math.Ceiling(cm.DesiredSize.Width));
        int h = Math.Max(1, (int)Math.Ceiling(cm.DesiredSize.Height));
        RenderTargetBitmap rtb = new RenderTargetBitmap(w, h, 96.0, 96.0, PixelFormats.Pbgra32);
        rtb.Render(cm);
        if (QaCountOpaque(rtb, new Int32Rect(0, 0, w, h)) == 0)
        {
            throw new InvalidOperationException($"the detached menu rendered empty ({w}x{h}, items=[{QaHeaders(cm)}])");
        }
        return rtb;
    }

    // Live, the box had keyboard focus (its selection is the thread's focused selection) and the edit menu then takes the
    // focus; WPF re-evaluates the highlight on that focus change (TextSelection.UpdateCaretAndHighlight) and, with
    // IsInactiveSelectionHighlightEnabled, keeps drawing it. The harness box never has focus, so the harness marks its
    // selection as the focused one and runs that same update (on), or undoes both and lets WPF remove the highlight
    // again (off), so later renders are unaffected. False when WPF's internals are not there.
    private bool QaSelectionHighlight(bool on)
    {
        try
        {
            const System.Reflection.BindingFlags any = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic;
            object? editor = typeof(TextBoxBase).GetProperty("TextEditor", any)?.GetValue(_search);
            object? selection = editor?.GetType().GetProperty("Selection", any)?.GetValue(editor);
            System.Reflection.MethodInfo? update = typeof(TextBoxBase).Assembly.GetType("System.Windows.Documents.ITextSelection")?.GetMethod("UpdateCaretAndHighlight");
            System.Reflection.MethodInfo? thread = selection?.GetType().GetMethod(on ? "SetThreadSelection" : "ClearThreadSelection", any);
            if (selection == null || update == null || thread == null)
            {
                return false;
            }
            thread.Invoke(selection, null);
            if (!on)
            {
                _search.Select(0, 0);
            }
            update.Invoke(selection, null);
            return true;
        }
        catch
        {
            return false;
        }
    }

    // MenuItem.IsHighlighted has a protected setter; the harness sets it the way keyboard focus would, so the template's
    // own IsHighlighted trigger draws the highlight.
    private static void QaSetHighlighted(MenuItem mi, bool on)
    {
        System.Reflection.MethodInfo? setter = typeof(MenuItem).GetProperty(nameof(MenuItem.IsHighlighted))?.GetSetMethod(nonPublic: true);
        setter?.Invoke(mi, new object[] { on });
    }

    // The window plus a detached popup drawn at its window position, on a canvas that holds both. The backdrop covers the
    // whole canvas.
    private RenderTargetBitmap SnapComposite(FrameworkElement popup, Point at, Color? solid)
    {
        BitmapSource pop = popup is ContextMenu cm ? RenderDetached(cm) : SnapElement(popup);
        RenderTargetBitmap win = SnapBitmap(backdrop: false);
        double left = Math.Min(0.0, at.X);
        double top = Math.Min(0.0, at.Y);
        double right = Math.Max(win.PixelWidth, at.X + pop.PixelWidth);
        double bottom = Math.Max(win.PixelHeight, at.Y + pop.PixelHeight);
        int w = (int)Math.Ceiling(right - left);
        int h = (int)Math.Ceiling(bottom - top);
        DrawingVisual dv = new DrawingVisual();
        using (DrawingContext dc = dv.RenderOpen())
        {
            Brush wall = solid.HasValue
                ? new SolidColorBrush(solid.Value)
                : new LinearGradientBrush(Color.FromRgb(0x1E, 0x4E, 0x7A), Color.FromRgb(0x6A, 0x9C, 0x5A), 35.0);
            dc.DrawRectangle(wall, null, new Rect(0.0, 0.0, w, h));
            dc.DrawImage(win, new Rect(-left, -top, win.PixelWidth, win.PixelHeight));
            dc.DrawImage(pop, new Rect(Math.Round(at.X - left), Math.Round(at.Y - top), pop.PixelWidth, pop.PixelHeight));
        }
        RenderTargetBitmap rtb = new RenderTargetBitmap(w, h, 96.0, 96.0, PixelFormats.Pbgra32);
        rtb.Render(dv);
        return rtb;
    }

    private static BitmapSource SnapElement(FrameworkElement e)
    {
        e.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        e.Arrange(new Rect(e.DesiredSize));
        e.UpdateLayout();
        RenderTargetBitmap rtb = new RenderTargetBitmap(Math.Max(1, (int)Math.Ceiling(e.ActualWidth)), Math.Max(1, (int)Math.Ceiling(e.ActualHeight)), 96.0, 96.0, PixelFormats.Pbgra32);
        rtb.Render(e);
        return rtb;
    }

    // Where a custom-placed menu opens, in window coordinates: the first placement the callback offers.
    private Point QaCustomPlacement(ContextMenu cm, FrameworkElement target, CustomPopupPlacementCallback cb)
    {
        QaLayoutDetached(cm);
        UpdateLayout();
        CustomPopupPlacement first = cb(cm.DesiredSize, new Size(target.ActualWidth, target.ActualHeight), new Point())[0];
        Point origin = target.TransformToAncestor(this).Transform(new Point());
        return new Point(origin.X + first.Point.X, origin.Y + first.Point.Y);
    }

    // A desktop program for the item menu: the first entry without '!' that has a Start menu shortcut (and may be a
    // recent row).
    private AppEntry? QaDesktopEntry()
    {
        foreach (AppEntry a in LoadApps())
        {
            if (IsGlyphEntry(a) || a.LaunchPath.Contains('!') || IsExcludedFromRecent(a.Name))
            {
                continue;
            }
            if (ResolveItemPaths(a).Lnk != null)
            {
                return a;
            }
        }
        return null;
    }

    // A packaged app: the first entry with '!' that has no file behind it.
    private AppEntry? QaPackagedEntry()
    {
        foreach (AppEntry a in LoadApps())
        {
            if (a.LaunchPath.Contains('!') && !IsExcludedFromRecent(a.Name) && ResolveItemPaths(a).Packaged)
            {
                return a;
            }
        }
        return null;
    }

    // 'mru': two pins, the separator and exactly 8 recent rows. The seeds present in the inventory are among the 8 used
    // programs, so no seed is appended.
    private void QaInjectMru()
    {
        _qaUsage.Clear();
        _qaPins.Clear();
        List<AppEntry> apps = LoadApps();
        List<AppEntry> used = new List<AppEntry>();
        foreach (string seed in RecentSeeds)
        {
            AppEntry? s = apps.FirstOrDefault((AppEntry a) => string.Equals(a.Name, seed, StringComparison.OrdinalIgnoreCase));
            if (s != null && !used.Contains(s) && used.Count < 8)
            {
                used.Add(s);
            }
        }
        foreach (AppEntry a in apps)
        {
            if (used.Count >= 8)
            {
                break;
            }
            if (!used.Contains(a) && !IsExcludedFromRecent(a.Name) && !IsGlyphEntry(a))
            {
                used.Add(a);
            }
        }
        List<AppEntry> pins = new List<AppEntry>();
        foreach (string name in QaPinPreferred)
        {
            AppEntry? p = apps.FirstOrDefault((AppEntry a) => string.Equals(a.Name, name, StringComparison.OrdinalIgnoreCase));
            if (p != null && !used.Contains(p) && pins.Count < 2)
            {
                pins.Add(p);
            }
        }
        foreach (AppEntry a in apps)
        {
            if (pins.Count >= 2)
            {
                break;
            }
            if (!used.Contains(a) && !pins.Contains(a))
            {
                pins.Add(a);
            }
        }
        long now = DateTime.UtcNow.Ticks;
        for (int i = 0; i < used.Count; i++)
        {
            _qaUsage[used[i].LaunchPath] = (30 - i * 3, now - TimeSpan.FromDays(i * 2.0).Ticks);
        }
        foreach (AppEntry p in pins)
        {
            _qaPins.Add(p.LaunchPath);
        }
    }

    private void QaRender3(string state, Action body)
    {
        try
        {
            // Every state starts from the same injected data: the default recent list, nothing left over from the
            // previous state.
            QaReset();
            QaInjectDefault();
            body();
            _qaRendered.Add(state);
        }
        catch (Exception ex)
        {
            _qaRenderFailures.Add(state + ": " + ex.GetType().Name + " " + ex.Message);
        }
        finally
        {
            _qaPointer = null;
        }
    }

    private void QaStage3Renders()
    {
        _qaRendered.Clear();
        _qaRenderFailures.Clear();
        _qaDisabledMenuResult = (false, "not rendered");
        QaRender3("hover", delegate
        {
            QaInjectDefault();
            ShowMenu("qa");
            List<W7Row> rows = ListRows();
            Select(Zone.Left, rows[Math.Min(2, rows.Count - 1)], byKeyboard: false);
            W7Row docs = (W7Row)_placeLinks["Documents"];
            docs.SetHot(on: true, animate: false);
            Snap("hover");
            docs.SetHot(on: false, animate: false);
        });
        QaRender3("pressed", delegate
        {
            ShowMenu("qa");
            BeginPress(ListRows()[0], new Point(10.0, 10.0));
            Snap("pressed");
            CancelPress();
        });
        QaRender3("kbselect", delegate
        {
            ShowMenu("qa");
            QaKey(Key.Down);
            QaKey(Key.Down);
            QaKey(Key.Down);
            Snap("kbselect");
            Info("kbselect selection='" + (_sel as W7Row)?.Text + "' zone=" + _zone);
        });
        QaRender3("kbselect-right", delegate
        {
            ShowMenu("qa");
            QaKey(Key.Down);
            QaKey(Key.Tab);
            QaKey(Key.Down);
            Snap("kbselect-right");
            Info("kbselect-right selection='" + (_sel as W7Row)?.Text + "' zone=" + _zone);
        });
        QaRender3("shutdown-hot", delegate
        {
            ShowMenu("qa");
            Select(Zone.PowerMain, _powerMain, byKeyboard: false);
            Snap("shutdown-hot");
        });
        QaRender3("powermenu", delegate
        {
            ShowMenu("qa");
            Select(Zone.PowerArrow, _powerArrow, byKeyboard: false);
            Point at = QaCustomPlacement(_powerMenu, _powerArrow, PlacePowerMenu);
            Snap("powermenu", popup: (_powerMenu, at));
            Info($"powermenu at ({at.X:0.##},{at.Y:0.##}) size {_powerMenu.DesiredSize.Width:0.##}x{_powerMenu.DesiredSize.Height:0.##} items=[{QaHeaders(_powerMenu)}]");
        });
        QaRender3("powermenu-kb", delegate
        {
            // Opened from the keyboard: Enter on the arrow asks for the flyout with its first item highlighted, and the
            // menu's real Opened handler does the highlighting (the IsHighlighted trigger draws it). The popup itself is
            // never opened, so the handler runs with the flyout as the open child for that moment.
            ShowMenu("qa");
            Select(Zone.PowerArrow, _powerArrow, byKeyboard: true);
            int n = _qaChildRequests.Count;
            QaKey(Key.Enter);
            if (_qaChildRequests.Count != n + 1 || !ReferenceEquals(_qaChildRequests[^1].Menu, _powerMenu) || !_qaChildRequests[^1].FocusFirst)
            {
                throw new InvalidOperationException("Enter on the arrow did not request the flyout with its first item highlighted");
            }
            Point at = QaCustomPlacement(_powerMenu, _powerArrow, PlacePowerMenu);
            MenuItem first = _powerMenu.Items.OfType<MenuItem>().First();
            _openChild = _powerMenu;
            _childMenuOpen = true;
            _focusFirstOnOpen = _qaChildRequests[^1].FocusFirst;
            try
            {
                OnChildOpened(_powerMenu, new RoutedEventArgs(ContextMenu.OpenedEvent, _powerMenu));
                if (!first.IsHighlighted)
                {
                    throw new InvalidOperationException("the Opened handler did not highlight '" + first.Header + "'");
                }
                Snap("powermenu-kb", popup: (_powerMenu, at));
                Info("powermenu-kb: Enter on the arrow -> flyout requested with focusFirst; Opened highlighted '" + first.Header + "'");
            }
            finally
            {
                QaSetHighlighted(first, on: false);
                _openChild = null;
                _childMenuOpen = false;
                _focusFirstOnOpen = false;
            }
        });
        QaRender3("pressed-glass", delegate
        {
            // The pressed looks of the glass: a place link and the Shut down main part.
            ShowMenu("qa");
            W7Row docs = (W7Row)_placeLinks["Documents"];
            BeginPress(docs, new Point(10.0, 10.0));
            SetPowerLayer(_powerMain, "PressLayer", 1.0);
            try
            {
                Snap("pressed-glass");
            }
            finally
            {
                CancelPress();
                SetPowerLayer(_powerMain, "PressLayer", 0.0);
            }
        });
        QaRender3("allprograms-row-hot", delegate
        {
            ShowMenu("qa");
            Select(Zone.Left, _cmdRow, byKeyboard: false);
            Snap("allprograms-row-hot");
        });
        QaRender3("editmenu", delegate
        {
            ShowMenu("qa");
            _search.Text = "co";
            _search.SelectAll();
            // The menu has the focus now: the selection it acts on stays highlighted.
            bool highlighted = QaSelectionHighlight(on: true);
            try
            {
                QaPump(60);
                UpdateLayout();
                ContextMenu em = BuildEditMenu();
                QaLayoutDetached(em);
                Rect box = QaBounds(_searchBox, this);
                // A right-click in the box: the box sits on the taskbar, so the menu opens above the pointer.
                Point at = new Point(box.Left + 40.0, box.Top + 12.0 - em.DesiredSize.Height);
                Snap("editmenu", popup: (em, at));
                Info("editmenu items=[" + QaHeaders(em) + "] enabled=[" + string.Join(", ", em.Items.OfType<MenuItem>().Select((MenuItem m) => m.IsEnabled ? "1" : "0")) + "] inactive selection drawn=" + highlighted);
            }
            finally
            {
                QaSelectionHighlight(on: false);
            }
        });
        QaRender3("editmenu-empty", delegate
        {
            // An empty box with nothing selected: Cut and Copy are disabled by the box's own command rules and draw in
            // W7.MenuInkDisabled.
            ShowMenu("qa");
            UpdateLayout();
            ContextMenu em = BuildEditMenu();
            CommandManager.InvalidateRequerySuggested();
            QaPump(30);
            QaLayoutDetached(em);
            Rect box = QaBounds(_searchBox, this);
            Point at = new Point(box.Left + 40.0, box.Top + 12.0 - em.DesiredSize.Height);
            Snap("editmenu-empty", popup: (em, at));
            Color disabled = Win7Palette.StartColor(_paletteDict!["W7.MenuInkDisabled"]);
            List<MenuItem> items = em.Items.OfType<MenuItem>().ToList();
            string Ink(MenuItem m) => m.Foreground is SolidColorBrush b ? Hex(b.Color) : (m.Foreground?.GetType().Name ?? "null");
            List<MenuItem> off = items.Where((MenuItem m) => !m.IsEnabled).ToList();
            bool cutCopyOff = items.Where((MenuItem m) => (string)m.Header == "Cut" || (string)m.Header == "Copy").All((MenuItem m) => !m.IsEnabled);
            bool inkOk = off.Count > 0 && off.All((MenuItem m) => m.Foreground is SolidColorBrush b && QaSame(b.Color, disabled));
            _qaDisabledMenuResult = (cutCopyOff && inkOk, $"enabled=[{string.Join(", ", items.Select((MenuItem m) => m.Header + "=" + (m.IsEnabled ? "1" : "0")))}] disabled ink=[{string.Join(", ", off.Select(Ink))}] (W7.MenuInkDisabled {Hex(disabled)})");
        });
        QaRender3("mru", delegate
        {
            QaInjectMru();
            ShowMenu("qa");
            Snap("mru");
            Info($"mru pins={_mainPins} recent={_mainRecent} rows=[{string.Join(", ", ListRows().Select((W7Row r) => r.Text))}]");
        });
        AppEntry? desk = QaDesktopEntry();
        if (desk != null)
        {
            QaRender3("itemmenu", delegate
            {
                QaItemMenuRender("itemmenu", desk);
            });
        }
        else
        {
            _qaRenderFailures.Add("itemmenu: no desktop entry with a Start menu shortcut");
        }
        AppEntry? pkg = QaPackagedEntry();
        if (pkg != null)
        {
            QaRender3("itemmenu-packaged", delegate
            {
                QaItemMenuRender("itemmenu-packaged", pkg);
            });
        }
        else
        {
            Info("itemmenu-packaged not applicable: this inventory has no packaged app");
        }
        if (IsVisible)
        {
            Dismiss("qa renders", instant: true);
        }
        QaReset();
    }

    // The item menu of a recent row (made the top recent row), composited at a right-click point on the row.
    private void QaItemMenuRender(string state, AppEntry a)
    {
        QaInjectDefault();
        _qaUsage[a.LaunchPath] = (999, DateTime.UtcNow.Ticks);
        ShowMenu("qa");
        W7Row? row = ListRows().FirstOrDefault((W7Row r) => ReferenceEquals(r.Payload, a));
        if (row == null)
        {
            throw new InvalidOperationException("'" + a.Name + "' is not a row of the main view");
        }
        Select(Zone.Left, row, byKeyboard: false);
        ContextMenu cm = BuildItemMenu(a, row.IsRecent);
        QaLayoutDetached(cm);
        Point origin = row.TransformToAncestor(this).Transform(new Point());
        Point at = new Point(origin.X + 96.0, origin.Y + 20.0);
        Snap(state, popup: (cm, at));
        Info($"{state} '{a.Name}' ({a.LaunchPath}) items=[{QaHeaders(cm)}]");
    }

    private void QaStage3Checks()
    {
        QaTry("renders-stage3", delegate
        {
            return (_qaRenderFailures.Count == 0 && _qaRendered.Count >= 10, $"rendered=[{string.Join(", ", _qaRendered)}]" + (_qaRenderFailures.Count > 0 ? " failed=[" + string.Join("; ", _qaRenderFailures) + "]" : string.Empty));
        });

        QaTry("menu-disabled-ink", () => _qaDisabledMenuResult);

        QaTry("hover-needs-real-move", delegate
        {
            // Rows and Shut down that arrive under a resting pointer (open, keyboard scroll, list rebuild) are not
            // selected; the first real move is. Enter then acts on what the user typed or moved to.
            QaInjectDefault();
            List<string> steps = new List<string>();
            bool all = true;
            void Step(string name, bool ok)
            {
                all &= ok;
                steps.Add(name + (ok ? " ok" : " FAIL"));
            }
            void Enter(UIElement el) => el.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, Environment.TickCount) { RoutedEvent = Mouse.MouseEnterEvent });
            void Move(UIElement el) => el.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, Environment.TickCount) { RoutedEvent = Mouse.MouseMoveEvent });
            _qaCursor = (500, 500);
            ShowMenu("qa");
            List<W7Row> rows = ListRows();
            Enter(rows[2]);
            Step("open under a resting pointer: row not selected", _sel == null);
            Enter(_powerMain);
            Step("Shut down under a resting pointer: not selected", _sel == null);
            _qaCursor = (501, 500);
            Move(rows[2]);
            Step("first real move selects the row", ReferenceEquals(_sel, rows[2]) && !_selByKeyboard);
            QaKey(Key.Down);
            W7Row kb = (W7Row)_sel!;
            Enter(rows[0]);
            Step("row scrolled under the still pointer after Down: keyboard selection kept", ReferenceEquals(_sel, kb) && _selByKeyboard);
            _search.Text = "s";
            AppEntry? firstResult = FirstProgramRow();
            List<W7Row> results = ListRows();
            if (results.Count > 1)
            {
                Enter(results[1]);
            }
            Step("results rebuilt under the still pointer: the pre-selected first result is kept", results.Count > 0 && ReferenceEquals(_sel, results[0]) && _selByKeyboard);
            int n = _qaLaunches.Count;
            QaKey(Key.Enter);
            Step("Enter launches the first result", firstResult != null && _qaLaunches.Count == n + 1 && _qaLaunches[^1].Name == firstResult.Name);
            ShowMenu("qa");
            _qaCursor = (510, 500);
            Move(_powerMain);
            bool hovered = ReferenceEquals(_sel, _powerMain) && !_selByKeyboard;
            int actions = _qaActions.Count;
            QaKey(Key.Enter);
            Step("pointer on Shut down + Enter (empty box): no shutdown", hovered && !_qaActions.Skip(actions).Any((string s) => s.Contains("Shut down", StringComparison.Ordinal)) && IsVisible);
            return (all, string.Join("; ", steps));
        });

        QaTry("main-after-other-view", delegate
        {
            // A pin made in All Programs, then Back: the main view keeps the session's height (the recent list gives
            // way), so nothing overflows into a scrollbar.
            List<AppEntry> apps = LoadApps().Where((AppEntry a) => !IsGlyphEntry(a) && !IsExcludedFromRecent(a.Name)).ToList();
            long now = DateTime.UtcNow.Ticks;
            for (int i = 0; i < Math.Min(10, apps.Count); i++)
            {
                _qaUsage[apps[i].LaunchPath] = (50 - i, now);
            }
            ShowMenu("qa");
            double v = _layout.V;
            int recentBefore = _mainRecent;
            AppEntry pin = apps.Count > 10 ? apps[10] : apps[^1];
            ToggleAllPrograms();
            ToggleStartPin(pin.LaunchPath, pin: true);
            ToggleAllPrograms();
            UpdateLayout();
            double content = _mainPins * 36.0 + ((_mainPins > 0 && _mainRecent > 0) ? 9.0 : 0.0) + _mainRecent * 36.0;
            bool pinnedFirst = ListRows().FirstOrDefault()?.Text == pin.Name;
            bool fits = content <= v + 0.5 && _layout.V == v && _progScroll.ScrollableHeight < 0.5;
            return (pinnedFirst && fits, $"V={v} recent {recentBefore}->{_mainRecent} pins={_mainPins} content={content} scrollable={_progScroll.ScrollableHeight:0.#} pinned first={pinnedFirst}");
        });

        QaTry("mid-show-cancel", delegate
        {
            // A dismiss that ran inside Show() and is cancelled as an activation bounce leaves a normal open menu: no
            // fade holding the root at 0, hit-testing back on, no fallback timer left.
            _qaMotionOff = false;
            _qaDur = TimeSpan.FromMilliseconds(200.0);
            ShowMenu("qa");
            QaPump(250);
            Dismiss("qa mid-show");
            bool fading = _dismissing && !_root.IsHitTestVisible && _closeFallback != null;
            CancelMidShowDismiss();
            RaiseOpen(true);
            QaPump(450);
            bool fadeOk = IsVisible && IsOpen && !_dismissing && _closeFallback == null && _root.Opacity == 1.0 && _root.IsHitTestVisible;
            bool keys = QaKey(Key.Down) && _sel != null;
            HardHide(cleanFrame: true);
            bool pending = _hidePending;
            CancelMidShowDismiss();
            QaPump(300);
            bool hideOk = pending && IsVisible && IsOpen && !_hidePending && _renderHook == null && _closeFallback == null && _root.Opacity == 1.0;
            return (fading && fadeOk && keys && hideOk, $"close fade cancelled: in flight={fading} restored={fadeOk} keys act={keys}; clean-frame hide cancelled: pending={pending} restored={hideOk}");
        });

        QaTry("search-selection-inactive", delegate
        {
            // Without keyboard focus (as while the edit menu is open) the selected text keeps its highlight, in the
            // palette's selection colour.
            ShowMenu("qa");
            _search.Text = "co";
            _search.SelectAll();
            bool updated = QaSelectionHighlight(on: true);
            try
            {
                QaPump(60);
                UpdateLayout();
                RenderTargetBitmap bmp = SnapBitmap();
                Rect c0 = _search.GetRectFromCharacterIndex(0);
                // Near the top of the line box: inside the selection band, above the x-height of "co".
                Point p0 = _search.TranslatePoint(new Point(c0.Left + 2.0, c0.Top + 3.0), this);
                Rect end = _search.GetRectFromCharacterIndex(_search.Text.Length);
                Point pe = _search.TranslatePoint(new Point(end.Left + 12.0, end.Top + 3.0), this);
                Color sel = QaPixel(bmp, (int)Math.Round(p0.X), (int)Math.Round(p0.Y));
                Color bg = QaPixel(bmp, (int)Math.Round(pe.X), (int)Math.Round(pe.Y));
                Color tint = Win7Palette.StartColor(_paletteDict!["W7.SearchSelection"]);
                double a = _search.SelectionOpacity;
                Color want = Color.FromRgb((byte)Math.Round(tint.R * a + bg.R * (1.0 - a)), (byte)Math.Round(tint.G * a + bg.G * (1.0 - a)), (byte)Math.Round(tint.B * a + bg.B * (1.0 - a)));
                bool shown = QaSame(sel, want) && !QaSame(sel, bg);
                bool brush = _search.Resources[SystemColors.InactiveSelectionHighlightBrushKey] is Brush b && ReferenceEquals(b, _paletteDict["W7.SearchSelection"]);
                bool ok = updated && shown && _search.IsInactiveSelectionHighlightEnabled && !_search.IsKeyboardFocused && brush;
                return (ok, $"keyboard focus={_search.IsKeyboardFocused} focus-loss update run={updated} selected {Hex(sel)} (want {Hex(want)}: W7.SearchSelection at {a:0.##}) vs box {Hex(bg)} inactive brush=palette:{brush}");
            }
            finally
            {
                QaSelectionHighlight(on: false);
            }
        });

        QaTry("shutdown-divider-etch", delegate
        {
            // The split is an etched groove: the dark line darker than the button, the light line only a little lighter
            // (no bright seam from the parts' inner highlight).
            ShowMenu("qa");
            UpdateLayout();
            RenderTargetBitmap bmp = SnapBitmap();
            Rect dark = QaBounds(_powerDivDark, this);
            Rect light = QaBounds(_powerDivLight, this);
            int y = (int)Math.Round(dark.Top + dark.Height / 2.0);
            int xd = (int)Math.Round(dark.Left);
            int xl = (int)Math.Round(light.Left);
            Color fillL = QaPixel(bmp, xd - 4, y);
            Color d = QaPixel(bmp, xd, y);
            Color l = QaPixel(bmp, xl, y);
            Color fillR = QaPixel(bmp, xl + 3, y);
            // Each divider column must be exactly its line over the button fill: nothing else (such as a part's inner
            // highlight) may add to it.
            static Color Over(Color top, Color under)
            {
                double a = top.A / 255.0;
                return Color.FromRgb((byte)Math.Round(top.R * a + under.R * (1.0 - a)), (byte)Math.Round(top.G * a + under.G * (1.0 - a)), (byte)Math.Round(top.B * a + under.B * (1.0 - a)));
            }
            Color wantD = Over(Win7Palette.StartColor(_paletteDict!["W7.PowerDivDark"]), fillL);
            Color wantL = Over(Win7Palette.StartColor(_paletteDict["W7.PowerDivLight"]), fillR);
            bool exact = QaSame(d, wantD) && QaSame(l, wantL);
            // Luma on the 0..255 scale: a groove, darker line then a lighter one.
            bool groove = Luma(d) < Luma(fillL) && Luma(l) > Luma(d);
            return (exact && groove, $"y={y} fill {Hex(fillL)}/{Hex(fillR)} dark x{xd} {Hex(d)} (want {Hex(wantD)}) light x{xl} {Hex(l)} (want {Hex(wantL)})");
        });

        QaTry("keyboard-script", delegate
        {
            QaInjectDefault();
            ShowMenu("qa");
            List<string> steps = new List<string>();
            bool all = true;
            void Step(string name, bool ok)
            {
                all &= ok;
                steps.Add(name + (ok ? " ok" : " FAIL"));
            }
            List<W7Row> list = ListRows();
            W7Row first = list[0];
            QaKey(Key.Down);
            Step("Down->first row", _zone == Zone.Left && ReferenceEquals(_sel, first) && _selByKeyboard && first.HotLayer!.Opacity == 1.0);
            QaKey(Key.Up);
            Step("Up(first)->command row", _zone == Zone.Left && ReferenceEquals(_sel, _cmdRow) && _cmdRow.HotLayer!.Opacity == 1.0 && first.HotLayer!.Opacity == 0.0);
            QaKey(Key.Down);
            Step("Down(command)->first row", ReferenceEquals(_sel, first));
            QaKey(Key.Down);
            W7Row recent = (W7Row)_sel!;
            bool rightHandled = QaKey(Key.Right);
            Step("Right(recent row '" + recent.Text + "')->Name", recent.IsRecent && rightHandled && _zone == Zone.Right && ReferenceEquals(_sel, _nameRow) && recent.HotLayer!.Opacity == 0.0);
            ClearSelection(animate: false);
            (Zone Z, FrameworkElement? El)[] tabs = { (Zone.Left, first), (Zone.Right, _nameRow), (Zone.PowerMain, _powerMain), (Zone.PowerArrow, _powerArrow), (Zone.None, null) };
            List<string> cycle = new List<string>();
            bool tabOk = true;
            foreach ((Zone z, FrameworkElement? el) in tabs)
            {
                bool h = QaKey(Key.Tab);
                cycle.Add(_zone.ToString());
                tabOk &= h && _zone == z && ReferenceEquals(_sel, el);
            }
            Step("Tab cycle " + string.Join(">", cycle), tabOk);
            _qaMods = ModifierKeys.Shift;
            List<string> back = new List<string>();
            bool backOk = true;
            foreach ((Zone z, FrameworkElement? el) in new (Zone, FrameworkElement?)[] { (Zone.PowerArrow, _powerArrow), (Zone.PowerMain, _powerMain), (Zone.Right, _nameRow), (Zone.Left, first), (Zone.None, null) })
            {
                QaKey(Key.Tab);
                back.Add(_zone.ToString());
                backOk &= _zone == z && ReferenceEquals(_sel, el);
            }
            _qaMods = ModifierKeys.None;
            Step("Shift+Tab cycle " + string.Join(">", back), backOk);
            Select(Zone.PowerMain, _powerMain, byKeyboard: true);
            QaKey(Key.Right);
            Step("Right(PM)->PA", _zone == Zone.PowerArrow && ReferenceEquals(_sel, _powerArrow));
            int n = _qaChildRequests.Count;
            QaKey(Key.Enter);
            bool power = _qaChildRequests.Count == n + 1 && ReferenceEquals(_qaChildRequests[^1].Menu, _powerMenu) && ReferenceEquals(_qaChildRequests[^1].Target, _powerArrow) && _qaChildRequests[^1].Mode == PlacementMode.Custom;
            Step("Enter(PA)->one power-menu request (Custom placement)", power && IsOpen);
            _search.Text = "x";
            QaKey(Key.Escape);
            Step("Esc(text 'x')->cleared", _search.Text.Length == 0 && IsVisible && IsOpen);
            QaKey(Key.Down);
            W7Row adminRow = (W7Row)_sel!;
            int launches = _qaLaunches.Count;
            _qaMods = ModifierKeys.Control | ModifierKeys.Shift;
            QaKey(Key.Enter);
            _qaMods = ModifierKeys.None;
            bool admin = _qaLaunches.Count == launches + 1 && _qaLaunches[^1].AsAdmin && _qaLaunches[^1].Name == ((AppEntry)adminRow.Payload!).Name && !IsVisible;
            Step("Ctrl+Shift+Enter(row '" + adminRow.Text + "')->asAdmin=true", admin);
            ShowMenu("qa");
            QaKey(Key.Down);
            W7Row appsRow = (W7Row)_sel!;
            n = _qaChildRequests.Count;
            bool appsHandled = QaKey(Key.Apps);
            bool apps = appsHandled && _qaChildRequests.Count == n + 1 && _qaChildRequests[^1].Mode == PlacementMode.Bottom && ReferenceEquals(_qaChildRequests[^1].Target, appsRow) && _qaChildRequests[^1].Menu.HorizontalOffset == 8.0 && QaHeaders(_qaChildRequests[^1].Menu).StartsWith("Open*", StringComparison.Ordinal);
            Step("Apps->item menu, Placement Bottom, offset 8", apps);
            n = _qaChildRequests.Count;
            _qaMods = ModifierKeys.Shift;
            bool f10 = QaKey(Key.F10);
            _qaMods = ModifierKeys.None;
            Step("Shift+F10->item menu", f10 && _qaChildRequests.Count == n + 1 && _qaChildRequests[^1].Mode == PlacementMode.Bottom);
            ClearSelection(animate: false);
            n = _qaChildRequests.Count;
            bool appsNone = QaKey(Key.Apps);
            Step("Apps(nothing selected)->left to the search box", !appsNone && _qaChildRequests.Count == n);
            return (all, string.Join("; ", steps));
        });

        QaTry("keyboard-more", delegate
        {
            QaInjectDefault();
            ShowMenu("qa");
            List<string> steps = new List<string>();
            bool all = true;
            void Step(string name, bool ok)
            {
                all &= ok;
                steps.Add(name + (ok ? " ok" : " FAIL"));
            }
            Step("Left/Right with nothing selected->caret", !QaKey(Key.Left) && !QaKey(Key.Right) && _sel == null);
            int launches = _qaLaunches.Count;
            QaKey(Key.Enter);
            Step("Enter(empty, nothing selected)->no-op", _qaLaunches.Count == launches && IsOpen);
            QaKey(Key.Up);
            Step("Up(nothing)->command row", ReferenceEquals(_sel, _cmdRow));
            QaKey(Key.Right);
            List<W7Row> all1 = ListRows();
            Step("Right(All Programs)->All Programs, first row", _allMode && all1.Count > 0 && ReferenceEquals(_sel, all1[0]) && _cmdRow.Text == "Back");
            QaKey(Key.End);
            Step("End->last row", ReferenceEquals(_sel, all1[^1]));
            QaKey(Key.Home);
            Step("Home->first row", ReferenceEquals(_sel, all1[0]));
            QaKey(Key.PageDown);
            int page = (int)Math.Floor(_layout.V / 22.0);
            Step("PageDown->+" + page + " rows", ReferenceEquals(_sel, all1[Math.Min(all1.Count - 1, page)]));
            QaKey(Key.PageUp);
            Step("PageUp->back to the first row", ReferenceEquals(_sel, all1[0]));
            QaKey(Key.Up);
            QaKey(Key.Left);
            Step("Left(Back)->main view, command row", !_allMode && ReferenceEquals(_sel, _cmdRow) && _cmdRow.Text == "All Programs");
            QaKey(Key.Down);
            QaKey(Key.Down);
            W7Row second = (W7Row)_sel!;
            QaKey(Key.Right);
            QaKey(Key.Left);
            Step("Left(R)->the last left selection", ReferenceEquals(_sel, second));
            Select(Zone.Right, RightRows()[^1], byKeyboard: true);
            QaKey(Key.Down);
            Step("Down(Help and Support)->Shut down", _zone == Zone.PowerMain);
            QaKey(Key.Up);
            Step("Up(Shut down)->Help and Support", ReferenceEquals(_sel, RightRows()[^1]));
            Select(Zone.PowerArrow, _powerArrow, byKeyboard: true);
            QaKey(Key.Left);
            Step("Left(PA)->PM", _zone == Zone.PowerMain);
            QaKey(Key.Left);
            Step("Left(PM)->L", _zone == Zone.Left);
            ClearSelection(animate: false);
            QaKey(Key.Down);
            _search.Text = "a";
            List<W7Row> typed = ListRows();
            Step("typing->first result pre-selected", typed.Count > 0 && ReferenceEquals(_sel, typed[0]) && _zone == Zone.Left && _selByKeyboard);
            _search.Clear();
            Step("clearing->main view, nothing selected", _view == View.Main && _sel == null && _zone == Zone.None);
            Select(Zone.Right, _placeLinks["Documents"], byKeyboard: true);
            int actions = _qaActions.Count;
            QaKey(Key.Enter);
            Step("Enter(Documents)->shell:Personal", _qaActions.Count == actions + 1 && _qaActions[^1] == "shell:Personal" && !IsVisible);
            ShowMenu("qa");
            QaKey(Key.Up);
            QaKey(Key.Enter);
            List<W7Row> all2 = ListRows();
            Step("Enter(All Programs)->All Programs, first row", _allMode && all2.Count > 0 && ReferenceEquals(_sel, all2[0]));
            return (all, string.Join("; ", steps));
        });

        QaTry("motion-off-no-clocks", delegate
        {
            _qaMotionOff = true;
            QaInjectDefault();
            ShowMenu("qa");
            bool rootAfterShow = !_root.HasAnimatedProperties;
            W7Row row = ListRows()[0];
            row.SetHot(on: true, animate: true);
            row.SetHot(on: false, animate: true);
            Select(Zone.PowerMain, _powerMain, byKeyboard: false);
            ClearSelection(animate: true);
            ToggleAllPrograms();
            ToggleAllPrograms();
            UIElement? powerHot = _powerMain.Template?.FindName("HotLayer", _powerMain) as UIElement;
            bool rowClean = !row.HotLayer!.HasAnimatedProperties;
            bool viewClean = !_progScroll.HasAnimatedProperties;
            bool powerClean = powerHot != null && !powerHot.HasAnimatedProperties;
            Dismiss("qa motion off");
            bool closeClean = !_root.HasAnimatedProperties && _closeFallback == null && !IsVisible;
            bool ok = rootAfterShow && rowClean && viewClean && powerClean && closeClean;
            return (ok, $"root after show={(rootAfterShow ? "no clock" : "ANIMATED")} row={(rowClean ? "no clock" : "ANIMATED")} view swap={(viewClean ? "no clock" : "ANIMATED")} shut down={(powerClean ? "no clock" : "ANIMATED")} dismiss: root={(_root.HasAnimatedProperties ? "ANIMATED" : "no clock")} fallbackTimer={_closeFallback != null} visible={IsVisible}");
        });

        QaTry("open-fade", delegate
        {
            _qaMotionOff = false;
            _qaDur = TimeSpan.FromMilliseconds(150.0);
            ShowMenu("qa");
            bool animating = _root.HasAnimatedProperties;
            object local = _root.ReadLocalValue(OpacityProperty);
            bool baseOne = local is double b && b == 1.0;
            QaPump(350);
            bool settled = _root.Opacity == 1.0 && _root.ReadLocalValue(OpacityProperty) is double b2 && b2 == 1.0 && IsOpen;
            return (animating && baseOne && settled, $"fade started before Show={animating} base value={local} after 350 ms opacity={_root.Opacity:0.###} open={IsOpen}");
        });

        QaTry("close-fade", delegate
        {
            _qaMotionOff = false;
            _qaDur = TimeSpan.FromMilliseconds(120.0);
            ShowMenu("qa");
            QaPump(250);
            int t = _qaTrace.Count;
            Dismiss("qa fade");
            bool fading = _dismissing && IsVisible && !IsOpen && _closeFallback != null && !_root.IsHitTestVisible && !_openRaised && _root.HasAnimatedProperties;
            bool keysSwallowed = QaKey(Key.Enter);
            QaPump(400);
            bool done = !IsVisible && !_dismissing && _closeFallback == null && _renderHook == null && _root.Opacity == 1.0 && _root.IsHitTestVisible && !TraceSince(t, "hide completed by fallback");
            return (fading && keysSwallowed && done, $"fading={fading} (orb released, click-through, fallback armed) keys swallowed while closing={keysSwallowed} hidden by Completed={done}");
        });

        QaTry("reopen-during-close", delegate
        {
            _qaMotionOff = false;
            _qaDur = TimeSpan.FromMilliseconds(200.0);
            ShowMenu("qa");
            QaPump(300);
            Dismiss("t");
            QaPump(60);
            bool closing = _dismissing && IsVisible && !IsOpen;
            double mid = _root.Opacity;
            ShowMenu("qa");
            double reopenedAt = _root.Opacity;
            QaPump(400);
            bool ok = closing && IsVisible && IsOpen && !_dismissing && _closeFallback == null && _root.Opacity == 1.0;
            return (ok, $"closing={closing} opacity mid-close={mid:0.###} right after the reopen={reopenedAt:0.###} after 400 ms: visible={IsVisible} open={IsOpen} dismissing={_dismissing} fallback={_closeFallback != null} opacity={_root.Opacity:0.###}");
        });

        QaTry("fallback-hide", delegate
        {
            _qaMotionOff = false;
            _qaDur = TimeSpan.FromMilliseconds(200.0);
            _qaSuppressCompleted = true;
            ShowMenu("qa");
            int t = _qaTrace.Count;
            Dismiss("qa fallback");
            QaPump(600);
            bool ok = !IsVisible && TraceSince(t, "hide completed by fallback") && _closeFallback == null && !_dismissing;
            return (ok, $"visible={IsVisible} traced={TraceSince(t, "hide completed by fallback")} fallbackTimer={_closeFallback != null}");
        });

        QaTry("hover-fades", delegate
        {
            _qaMotionOff = false;
            _qaDur = TimeSpan.FromMilliseconds(100.0);
            QaInjectDefault();
            ShowMenu("qa");
            QaPump(200);
            List<W7Row> rows = ListRows();
            Select(Zone.Left, rows[0], byKeyboard: false);
            bool inInstant = rows[0].HotLayer!.Opacity == 1.0 && !rows[0].HotLayer!.HasAnimatedProperties;
            Select(Zone.Left, rows[1], byKeyboard: false);
            bool outFading = rows[0].HotLayer!.HasAnimatedProperties && rows[1].HotLayer!.Opacity == 1.0;
            UIElement hot = (UIElement)_powerMain.Template.FindName("HotLayer", _powerMain);
            Select(Zone.PowerMain, _powerMain, byKeyboard: false);
            bool powerIn = hot.HasAnimatedProperties;
            QaPump(250);
            bool settled = rows[0].HotLayer!.Opacity == 0.0 && hot.Opacity == 1.0 && rows[1].HotLayer!.Opacity == 0.0;
            ClearSelection(animate: true);
            QaPump(250);
            bool powerOut = hot.Opacity == 0.0;
            bool ok = inInstant && outFading && powerIn && settled && powerOut;
            return (ok, $"row hover-in instant={inInstant} hover-out fades={outFading} shut down fades in={powerIn} settled={settled} shut down faded out={powerOut}");
        });

        QaTry("view-swap-fade", delegate
        {
            _qaMotionOff = false;
            _qaDur = TimeSpan.FromMilliseconds(100.0);
            ShowMenu("qa");
            QaPump(200);
            ToggleAllPrograms();
            bool fading = _progScroll.HasAnimatedProperties;
            QaPump(250);
            bool settled = _progScroll.Opacity == 1.0;
            return (fading && settled, $"incoming All Programs fades in={fading} settled at 1={settled}");
        });

        QaTry("press-tracking", delegate
        {
            QaInjectDefault();
            ShowMenu("qa");
            UpdateLayout();   // a real click lands on rows that have been laid out
            List<W7Row> rows = ListRows();
            W7Row row = rows[0];
            int n = _qaLaunches.Count;
            QaMouse(row, UIElement.MouseLeftButtonDownEvent, new Point(10.0, 10.0));
            bool pressedShown = ReferenceEquals(_pressed, row) && row.PressLayer!.Opacity == 1.0 && ReferenceEquals(_sel, row);
            QaMouse(row, UIElement.MouseLeftButtonUpEvent, new Point(10.0, 200.0));
            bool outside = _qaLaunches.Count == n && IsOpen && _pressed == null && row.PressLayer!.Opacity == 0.0;
            QaMouse(row, UIElement.MouseLeftButtonDownEvent, new Point(10.0, 10.0));
            QaMouse(row, UIElement.MouseMoveEvent, new Point(10.0 + SystemParameters.MinimumHorizontalDragDistance + 2.0, 10.0));
            bool dragCancelled = _pressed == null && row.PressLayer!.Opacity == 0.0;
            QaMouse(row, UIElement.MouseLeftButtonUpEvent, new Point(10.0, 10.0));
            bool dragNoLaunch = _qaLaunches.Count == n && IsOpen;
            QaMouse(row, UIElement.MouseLeftButtonDownEvent, new Point(10.0, 10.0));
            QaMouse(rows[1], UIElement.MouseLeftButtonUpEvent, new Point(10.0, 10.0));
            bool otherRow = _qaLaunches.Count == n && IsOpen;
            CancelPress();
            QaMouse(row, UIElement.MouseLeftButtonDownEvent, new Point(10.0, 10.0));
            QaMouse(row, UIElement.MouseLeftButtonUpEvent, new Point(12.0, 11.0));
            bool launched = _qaLaunches.Count == n + 1 && _qaLaunches[^1].Name == row.Text && !_qaLaunches[^1].AsAdmin && !IsVisible;
            bool ok = pressedShown && outside && dragCancelled && dragNoLaunch && otherRow && launched;
            return (ok, $"pressed visual={pressedShown} release outside->nothing={outside} drag->cancelled={dragCancelled}/{dragNoLaunch} release on another row->nothing={otherRow} release on the row->launch={launched}");
        });

        QaTry("menu-order", delegate
        {
            QaInjectDefault();
            ShowMenu("qa");
            List<string> parts = new List<string>();
            bool ok = true;
            void Expect(string name, ContextMenu cm, string[] expected)
            {
                string got = QaHeaders(cm);
                string want = string.Join(", ", expected);
                bool m = got == want;
                ok &= m;
                parts.Add(name + " [" + got + "]" + (m ? string.Empty : " EXPECTED [" + want + "]"));
            }
            (bool _, bool sleep, bool hibernate, string raw) = PowerActions.Capabilities;
            Info($"power capabilities {raw} -> sleep={sleep} hibernate={hibernate}");
            List<string> power = new List<string> { "Switch user", "Log off", "Lock", "|", "Restart" };
            if (sleep)
            {
                power.Add("Sleep");
            }
            if (hibernate)
            {
                power.Add("Hibernate");
            }
            Expect("power", _powerMenu, power.ToArray());
            AppEntry? desk = QaDesktopEntry();
            if (desk != null)
            {
                Expect("desktop recent '" + desk.Name + "'", BuildItemMenu(desk, isRecent: true), new[] { "Open*", "Run as administrator", "Open file location", "|", "Pin to Taskbar", "Pin to Start Menu", "|", "Remove from this list", "Properties" });
                Expect("desktop not recent", BuildItemMenu(desk, isRecent: false), new[] { "Open*", "Run as administrator", "Open file location", "|", "Pin to Taskbar", "Pin to Start Menu", "|", "Properties" });
                _qaPins.Add(desk.LaunchPath);
                _qaTaskbarPinned.Add(desk.LaunchPath);
                Expect("desktop pinned", BuildItemMenu(desk, isRecent: false), new[] { "Open*", "Run as administrator", "Open file location", "|", "Unpin from Taskbar", "Unpin from Start Menu", "|", "Properties" });
                _qaPins.Clear();
                _qaTaskbarPinned.Clear();
            }
            else
            {
                ok = false;
                parts.Add("no desktop entry");
            }
            AppEntry? pkg = QaPackagedEntry();
            if (pkg != null)
            {
                Expect("packaged recent '" + pkg.Name + "'", BuildItemMenu(pkg, isRecent: true), new[] { "Open*", "|", "Pin to Taskbar", "Pin to Start Menu", "|", "Remove from this list" });
            }
            else
            {
                Info("menu-order: no packaged app in this inventory");
            }
            AppEntry? glyph = LoadApps().FirstOrDefault(IsGlyphEntry);
            if (glyph != null)
            {
                Expect("launcher entry '" + glyph.Name + "'", BuildItemMenu(glyph, isRecent: false), new[] { "Open*", "|", "Pin to Taskbar", "Pin to Start Menu" });
            }
            Expect("empty area", BuildEmptyAreaMenu(), new[] { "Properties" });
            Expect("edit", BuildEditMenu(), new[] { "Cut", "Copy", "Paste", "|", "Select all" });
            ContextMenu synthetic = NewMenu();
            synthetic.Items.Add(W7Sep());
            synthetic.Items.Add(W7Item("a", delegate { }));
            synthetic.Items.Add(W7Sep());
            synthetic.Items.Add(W7Sep());
            synthetic.Items.Add(W7Item("b", delegate { }));
            synthetic.Items.Add(W7Sep());
            CollapseSeparators(synthetic);
            Expect("collapse |a||b|", synthetic, new[] { "a", "|", "b" });
            return (ok, string.Join("; ", parts));
        });

        QaTry("menu-style", delegate
        {
            QaInjectDefault();
            ShowMenu("qa");
            W7Row row = ListRows()[0];
            bool parsed = W7MenuStyle.Value != null && W7MenuItemStyle.Value != null && W7SeparatorStyle.Value != null;
            List<string> parts = new List<string>();
            bool ok = parsed;
            (string Name, ContextMenu Menu, UIElement Target, PlacementMode Mode)[] menus =
            {
                ("power", _powerMenu, _powerArrow, PlacementMode.Custom),
                ("item", BuildItemMenu((AppEntry)row.Payload!, row.IsRecent), row, PlacementMode.MousePoint),
                ("empty", BuildEmptyAreaMenu(), _root, PlacementMode.MousePoint),
                ("edit", BuildEditMenu(), _search, PlacementMode.MousePoint)
            };
            foreach ((string name, ContextMenu cm, UIElement target, PlacementMode mode) in menus)
            {
                OpenChildMenu(cm, target, mode, mode == PlacementMode.Custom ? PlacePowerMenu : null);
                bool menuStyled = ReferenceEquals(cm.Style, W7MenuStyle.Value) && !cm.HasDropShadow && cm.OverridesDefaultStyle;
                bool itemsStyled = cm.Items.Cast<object>().All((object i) => i switch
                {
                    MenuItem m => ReferenceEquals(m.Style, W7MenuItemStyle.Value),
                    Separator s => ReferenceEquals(s.Style, W7SeparatorStyle.Value),
                    _ => false
                });
                bool keys = cm.Resources.Contains("W7.MenuBg") && ReferenceEquals(cm.Resources["W7.MenuBg"], _paletteDict!["W7.MenuBg"]);
                ok &= menuStyled && itemsStyled && keys;
                parts.Add($"{name}: menu={menuStyled} items={itemsStyled} keys={keys}");
            }
            // Rendered look: 22 px rows, the menu colour and the gutter, from the palette.
            BitmapSource bmp = RenderDetached(_powerMenu);
            List<MenuItem> items = _powerMenu.Items.OfType<MenuItem>().ToList();
            bool rows22 = items.All((MenuItem m) => Math.Abs(m.ActualHeight - 22.0) < 0.01);
            Separator? sep = _powerMenu.Items.OfType<Separator>().FirstOrDefault();
            double sepH = sep != null ? sep.ActualHeight + sep.Margin.Top + sep.Margin.Bottom : 0.0;
            Color bg = Win7Palette.StartColor(_paletteDict!["W7.MenuBg"]);
            Color gutter = Win7Palette.StartColor(_paletteDict["W7.MenuGutter"]);
            int midY = (int)(1 + 2 + 11);
            Color pxGutter = QaPixel(bmp, 1 + 2 + 13, midY);
            Color pxBg = QaPixel(bmp, (int)(_powerMenu.DesiredSize.Width - 4 - 1 - 2 - 4), midY);
            bool colours = QaSame(pxGutter, gutter) && QaSame(pxBg, bg);
            ok &= rows22 && colours && Math.Abs(sepH - 8.0) < 0.01;
            parts.Add($"power rows={string.Join("/", items.Select((MenuItem m) => m.ActualHeight.ToString("0.#")))} separator={sepH:0.#} gutter {Hex(pxGutter)} (W7.MenuGutter {Hex(gutter)}) background {Hex(pxBg)} (W7.MenuBg {Hex(bg)})");
            // A theme flip while a menu is open re-copies its keys.
            ContextMenu open = menus[1].Menu;
            ContextMenu? savedChild = _openChild;
            bool dark = ShellTheme.IsDark;
            bool recopied;
            try
            {
                _openChild = open;
                ShellTheme.ForceForTest(!dark);
                recopied = ReferenceEquals(open.Resources["W7.MenuBg"], _paletteDict!["W7.MenuBg"]);
            }
            finally
            {
                ShellTheme.ForceForTest(dark);
                _openChild = savedChild;
            }
            ok &= recopied;
            parts.Add("open menu follows a theme flip=" + recopied);
            return (ok, (parsed ? "styles parsed; " : "STYLE PARSE FAILED; ") + string.Join("; ", parts));
        });

        QaTry("mru-rules", delegate
        {
            List<string> parts = new List<string>();
            bool ok = true;
            long now = DateTime.UtcNow.Ticks;
            long Ago(double days) => now - TimeSpan.FromDays(days).Ticks;
            AppEntry Extra(string name, string path)
            {
                AppEntry e = new AppEntry { Name = name, LaunchPath = path, TileBrush = Brushes.Transparent };
                _qaExtraApps.Add(e);
                return e;
            }
            List<string> RecentNames() => ListRows().Where((W7Row r) => r.IsRecent).Select((W7Row r) => r.Text).ToList();
            Extra("QA Tool Uninstall", "qa:uninstall");
            AppEntry alpha = Extra("QA Alpha", "qa:alpha");
            Extra("QA Beta", "qa:beta");
            Extra("QA Gamma", "qa:gamma");
            _qaUsage["qa:uninstall"] = (500, now);
            _qaUsage["qa:alpha"] = (10, Ago(0.0));    // 10
            _qaUsage["qa:beta"] = (30, Ago(28.0));    // 30 x 0.25 = 7.5
            _qaUsage["qa:gamma"] = (8, Ago(7.0));     // 8 x 0.707 = 5.66
            ShowMenu("qa");
            List<string> names = RecentNames();
            bool excluded = !names.Contains("QA Tool Uninstall");
            bool order = names.Take(3).SequenceEqual(new[] { "QA Alpha", "QA Beta", "QA Gamma" });
            ok &= excluded && order;
            parts.Add($"'QA Tool Uninstall' (count 500) excluded={excluded}; frecency order [{string.Join(", ", names.Take(3))}] (count-only order would be Beta, Alpha, Gamma) ok={order}");
            (string Name, bool Excluded)[] tokens =
            {
                ("Uninstall Foo", true), ("Foo Read Me", true), ("Readme", true), ("What's New in Foo", true), ("Foo Help", true),
                ("Foo Setup", true), ("Release Notes", true), ("Foo Website", true), ("Helper Tool", false), ("Windows Installer", false),
                ("Supportive", false), ("Paint", false)
            };
            List<string> wrongTokens = tokens.Where(((string Name, bool Excluded) t) => IsExcludedFromRecent(t.Name) != t.Excluded).Select(((string Name, bool Excluded) t) => t.Name).ToList();
            ok &= wrongTokens.Count == 0;
            parts.Add($"whole-word tokens wrong=[{string.Join(", ", wrongTokens)}]");
            _qaTrackProgs = false;
            _qaPins.Add("qa:beta");
            ShowMenu("qa");
            bool privacy = RecentNames().Count == 0 && ListRows().Count == 1 && ListRows()[0].Text == "QA Beta";
            ok &= privacy;
            parts.Add($"Start_TrackProgs=0 -> recent={RecentNames().Count} pins shown={ListRows().Count(r => !r.IsRecent)} ok={privacy}");
            _qaTrackProgs = true;
            _qaPins.Clear();
            _qaUsage.Clear();
            _qaExtraApps.Clear();
            ShowMenu("qa");
            List<AppEntry> apps = LoadApps();
            List<string> seedsPresent = RecentSeeds.Where((string s) => apps.Any((AppEntry a) => string.Equals(a.Name, s, StringComparison.OrdinalIgnoreCase))).Take(_recentMax).ToList();
            names = RecentNames();
            bool seedsOnly = names.Count == seedsPresent.Count && names.All((string n) => RecentSeeds.Contains(n, StringComparer.OrdinalIgnoreCase));
            ok &= seedsOnly;
            parts.Add($"empty usage -> [{string.Join(", ", names)}] seeds only={seedsOnly}");
            Extra("QA Alpha", "qa:alpha");
            alpha = _qaExtraApps[^1];
            _qaUsage["qa:alpha"] = (50, now);
            _qaHidden.Add("qa:alpha");
            ShowMenu("qa");
            bool hiddenAbsent = !RecentNames().Contains("QA Alpha");
            LaunchProgram(alpha, asAdmin: false);
            bool unhidden = !_qaHidden.Contains("qa:alpha") && _qaLaunches.Count > 0 && _qaLaunches[^1].Name == "QA Alpha";
            ShowMenu("qa");
            bool back = RecentNames().FirstOrDefault() == "QA Alpha";
            ok &= hiddenAbsent && unhidden && back;
            parts.Add($"hidden path absent={hiddenAbsent}; launching it removed it from the hidden list={unhidden}; back in the list={back}");
            _qaHidden.Clear();
            return (ok, string.Join("; ", parts));
        });

        QaTry("in-place-refresh", delegate
        {
            // Pin to Start Menu while the menu stays open: the main view is rebuilt with the same window height.
            QaInjectDefault();
            ShowMenu("qa");
            double v = _layout.V;
            double h = Height;
            W7Row row = ListRows()[2];
            AppEntry a = (AppEntry)row.Payload!;
            ToggleStartPin(a.LaunchPath, pin: true);
            List<W7Row> rows = ListRows();
            bool pinnedFirst = rows.Count > 0 && rows[0].Text == a.Name && !rows[0].IsRecent;
            bool sameHeight = _layout.V == v && Height == h;
            double content = _mainPins * 36.0 + ((_mainPins > 0 && _mainRecent > 0) ? 9.0 : 0.0) + _mainRecent * 36.0;
            bool fits = content <= v + 0.5;
            RemoveFromMru(ListRows().First((W7Row r) => r.IsRecent).Payload is AppEntry r1 ? r1.LaunchPath : string.Empty);
            bool removed = _qaHidden.Count == 1 && !ListRows().Any((W7Row r) => r.IsRecent && r.Payload is AppEntry e && _qaHidden.Contains(e.LaunchPath));
            bool ok = pinnedFirst && sameHeight && fits && removed && IsOpen;
            return (ok, $"pinned '{a.Name}' first={pinnedFirst} V {v}->{_layout.V} height {h}->{Height} content={content} fits={fits} removed from list={removed} open={IsOpen}");
        });

        QaTry("lnkpath-merge", delegate
        {
            string root = Path.Combine(_qaOutDir, "qa-lnktree-" + _qaTag);
            try
            {
                string a = Path.Combine(root, "A", "Programs");
                string b = Path.Combine(root, "B", "Programs");
                Directory.CreateDirectory(Path.Combine(a, "Tools"));
                Directory.CreateDirectory(Path.Combine(a, "Office"));
                Directory.CreateDirectory(b);
                File.WriteAllBytes(Path.Combine(a, "Same.lnk"), Array.Empty<byte>());
                File.WriteAllBytes(Path.Combine(a, "Tools", "Same.lnk"), Array.Empty<byte>());
                File.WriteAllBytes(Path.Combine(a, "Office", "Other.lnk"), Array.Empty<byte>());
                File.WriteAllBytes(Path.Combine(b, "Other.lnk"), Array.Empty<byte>());
                Dictionary<string, StartMenuIndex.Info> map = new Dictionary<string, StartMenuIndex.Info>(StringComparer.OrdinalIgnoreCase);
                StartMenuIndex.Scan(a, a, map);
                StartMenuIndex.Scan(b, b, map);
                StartMenuIndex.Info same = map["Same"];
                StartMenuIndex.Info other = map["Other"];
                bool s1 = same.Category == "Tools" && string.Equals(same.LnkPath, Path.Combine(a, "Tools", "Same.lnk"), StringComparison.OrdinalIgnoreCase);
                bool s2 = other.Category == "Office" && string.Equals(other.LnkPath, Path.Combine(a, "Office", "Other.lnk"), StringComparison.OrdinalIgnoreCase);
                string rel(string? p) => p == null ? "null" : Path.GetRelativePath(root, p);
                return (s1 && s2, $"root then folder: Same -> {same.Category} {rel(same.LnkPath)} ok={s1}; folder then a later root: Other -> {other.Category} {rel(other.LnkPath)} ok={s2}");
            }
            finally
            {
                try
                {
                    Directory.Delete(root, recursive: true);
                }
                catch
                {
                }
            }
        });

        QaTry("power-placement", delegate
        {
            ShowMenu("qa");
            UpdateLayout();
            QaLayoutDetached(_powerMenu);
            Size ps = _powerMenu.DesiredSize;
            Size ts = new Size(_powerArrow.ActualWidth, _powerArrow.ActualHeight);
            CustomPopupPlacement[] p = PlacePowerMenu(ps, ts, new Point());
            double mainLeft = _powerMain.TranslatePoint(new Point(), _powerArrow).X;
            bool right = p.Length == 2 && Near(p[0].Point.X, ts.Width + 1.0) && Near(p[0].Point.Y + ps.Height - 4.0, ts.Height);
            bool left = p.Length == 2 && Near(p[1].Point.X + ps.Width - 4.0, mainLeft - 1.0) && Near(p[1].Point.Y, p[0].Point.Y);
            // At 1.25x the callback gets device pixels: the constants scale with the target.
            CustomPopupPlacement[] p125 = PlacePowerMenu(new Size(ps.Width * 1.25, ps.Height * 1.25), new Size(ts.Width * 1.25, ts.Height * 1.25), new Point());
            bool scaled = Near(p125[0].Point.X, (ts.Width + 1.0) * 1.25) && Near(p125[0].Point.Y + ps.Height * 1.25 - 5.0, ts.Height * 1.25);
            return (right && left && scaled, $"menu {ps.Width:0.##}x{ps.Height:0.##}, arrow {ts.Width}x{ts.Height}: right of the arrow ({p[0].Point.X:0.##},{p[0].Point.Y:0.##}) visible bottom level={right}; fallback ({p[1].Point.X:0.##},{p[1].Point.Y:0.##}) left of the split button (x {mainLeft:0.##})={left}; scaled={scaled}");
        });

        QaTry("idle-after-hide", delegate
        {
            ShowMenu("qa");
            ToggleAllPrograms();
            UpdateLayout();
            SmoothScroll.ByVertical(_progScroll, 200.0);
            bool scrolling = SmoothScroll.IsVerticalActive(_progScroll);
            HardHide(cleanFrame: false);
            bool ok = scrolling && !IsVisible && _closeFallback == null && _renderHook == null && !SmoothScroll.IsVerticalActive(_progScroll) && _pressed == null && _sel == null;
            return (ok, $"scrolling before={scrolling} after HardHide: fallbackTimer={_closeFallback != null} renderHook={_renderHook != null} smoothScroll={SmoothScroll.IsVerticalActive(_progScroll)} selection={(_sel == null ? "none" : "kept")}");
        });

        QaTry("empty-area-menu", delegate
        {
            ShowMenu("qa");
            int n = _qaChildRequests.Count;
            QaRightUp(_cmdRow);
            bool one = _qaChildRequests.Count == n + 1 && QaHeaders(_qaChildRequests[^1].Menu) == "Properties";
            // The search box keeps its own menu: a right-click there does not open the empty-area one.
            QaRightUp(_searchCue);
            bool boxSkipped = _qaChildRequests.Count == n + 1;
            // A program row opens its item menu instead.
            W7Row row = ListRows()[0];
            QaRightUp(row);
            bool rowMenu = _qaChildRequests.Count == n + 2 && QaHeaders(_qaChildRequests[^1].Menu).StartsWith("Open*", StringComparison.Ordinal) && _qaChildRequests[^1].Mode == PlacementMode.MousePoint;
            int actions = _qaActions.Count;
            ((MenuItem)_qaChildRequests[n].Menu.Items[0]).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            QaPump(30);
            bool settings = _qaActions.Count == actions + 1 && _qaActions[^1] == "settings" && !IsVisible;
            return (one && boxSkipped && rowMenu && settings, $"command row->Properties menu={one} search box skipped={boxSkipped} program row->item menu={rowMenu} Properties->settings and close={settings}");
        });
    }

    // A right-button release on the element: the bubbling MouseUp, which every element on the way re-raises as its
    // MouseRightButtonUp (the handled state carries along, as with a real click).
    private static void QaRightUp(UIElement source)
    {
        MouseButtonEventArgs args = new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Right)
        {
            RoutedEvent = UIElement.MouseUpEvent,
            Source = source
        };
        source.RaiseEvent(args);
    }

    private static bool QaSame(Color a, Color b)
    {
        return Math.Abs(a.R - b.R) <= 2 && Math.Abs(a.G - b.G) <= 2 && Math.Abs(a.B - b.B) <= 2;
    }
}

// Windows 7 Start menu QA, stage 4: the All Programs tree, search ranking, inventory reloads, prewarm, idle cleanliness
// and the timing metrics.
public sealed partial class Win7StartMenu
{
    // "calc" typed with the Greek keyboard layout.
    private const string QaGreekCalc = "\u03C8\u03B1\u03BB\u03C8";

    // Like QaTry, but the body may report that the check does not apply to this inventory (null): an INFO SKIP line.
    private void QaTrySkippable(string name, Func<(bool? Ok, string Detail)> body)
    {
        _qaCurrentCheck = name;
        try
        {
            (bool? ok, string detail) = body();
            if (ok.HasValue)
            {
                Check(name, ok.Value, detail);
            }
            else
            {
                Info(name + " SKIP " + detail);
            }
        }
        catch (Exception ex)
        {
            Check(name, false, "exception " + ex.GetType().Name + ": " + ex.Message);
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

    private static double Median(List<double> values)
    {
        if (values.Count == 0)
        {
            return 0.0;
        }
        List<double> v = values.OrderBy((double x) => x).ToList();
        return v.Count % 2 == 1 ? v[v.Count / 2] : (v[v.Count / 2 - 1] + v[v.Count / 2]) / 2.0;
    }

    // Types a fresh query (the box is cleared first, so the same query twice still rebuilds) and returns the first result.
    private AppEntry? QaTopResult(string q)
    {
        _search.Clear();
        _search.Text = q;
        return FirstProgramRow();
    }

    private string QaSearchSummary()
    {
        List<string> parts = new List<string>();
        foreach (UIElement child in _progList.Children)
        {
            if (child is W7Row r)
            {
                parts.Add(r.RowKind == W7Row.Kind.Header24 ? "[" + r.Text + "]" : (r.RowKind == W7Row.Kind.Message22 ? "(" + r.Text + ")" : r.Text + (ReferenceEquals(r, _sel) ? "*" : string.Empty)));
            }
        }
        return $"'{_search.Text}' matches={_searchMatches} rows: {string.Join(", ", parts)}; command row '{_cmdRow.Text}'";
    }

    private TreeNode? QaFirstFolder(int minChildren)
    {
        return _allNodes.FirstOrDefault((TreeNode n) => n.Folder != null && n.Children != null && n.Children.Count >= minChildren);
    }

    // The text the Greek keyboard layout produces for a Latin query typed on the same keys (null when a key has no Greek
    // letter, such as q).
    private static string? QaGreekKeys(string latin)
    {
        System.Text.StringBuilder sb = new System.Text.StringBuilder(latin.Length);
        foreach (char c in latin)
        {
            char found = '\0';
            for (char g = '\u03B1'; g <= '\u03C9'; g++)
            {
                if (GreekLayout.ToLatinKeys(g.ToString()) == c.ToString())
                {
                    found = g;
                    break;
                }
            }
            if (found == '\0')
            {
                return null;
            }
            sb.Append(found);
        }
        return sb.ToString();
    }

    // The Greek-layout case of the renders and the greek check: 'calc' (Calculator) when something matches it, otherwise
    // the first of a few other queries that matches something, typed on the Greek layout the same way. Null when none
    // matches.
    private static readonly string[] QaGreekCandidates = { "calc", "note", "paint", "char", "comm", "word", "exce" };

    private (string Latin, string Greek)? QaGreekCase()
    {
        List<AppEntry> apps = LoadApps();
        foreach (string latin in QaGreekCandidates)
        {
            string? greek = QaGreekKeys(latin);
            if (greek != null && RankSearch(apps, latin).Count > 0)
            {
                return (latin, greek);
            }
        }
        return null;
    }

    // The focused search box as every live open shows it, without taking keyboard focus: the border the focus handler
    // applies (ApplySearchFocusLook) and a 1 px caret in W7.SearchCaret at a character, drawn as an overlay while body
    // runs. body gets the caret rect in the coordinates of the box's content grid.
    private void QaWithFocusedSearch(int caretIndex, Action<Rect> body)
    {
        Grid host = (Grid)_searchBox.Child;
        ApplySearchFocusLook(true);
        System.Windows.Shapes.Rectangle? caret = null;
        try
        {
            UpdateLayout();
            Rect r = _search.GetRectFromCharacterIndex(caretIndex, caretIndex > 0);
            Point at = _search.TransformToAncestor(host).Transform(r.TopLeft);
            Rect cr = new Rect(Math.Round(at.X), Math.Round(at.Y), Math.Max(1.0, SystemParameters.CaretWidth), Math.Round(r.Height));
            caret = new System.Windows.Shapes.Rectangle
            {
                Width = cr.Width,
                Height = cr.Height,
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(cr.X, cr.Y, 0.0, 0.0),
                IsHitTestVisible = false,
                SnapsToDevicePixels = true
            };
            caret.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, "W7.SearchCaret");
            host.Children.Add(caret);
            UpdateLayout();
            body(cr);
        }
        finally
        {
            if (caret != null)
            {
                host.Children.Remove(caret);
            }
            ApplySearchFocusLook(false);
        }
    }

    private bool QaOnlyMessage(string text)
    {
        List<W7Row> rows = _progList.Children.OfType<W7Row>().ToList();
        return rows.Count == 1 && rows[0].RowKind == W7Row.Kind.Message22 && rows[0].Text == text && ListRows().Count == 0;
    }

    private void QaStage4Renders()
    {
        QaRender3("allprograms-expanded", delegate
        {
            ShowMenu("qa");
            ToggleAllPrograms();
            UpdateLayout();
            TreeNode folder = QaFirstFolder(3) ?? throw new InvalidOperationException("no folder with at least 3 programs");
            ToggleFolder(folder, expand: true, bringIntoView: true);
            UpdateLayout();
            W7Row second = folder.Children![1].Row!;
            Select(Zone.Left, second, byKeyboard: false);
            Snap("allprograms-expanded");
            Info($"allprograms-expanded folder='{folder.Name}' children={folder.Children.Count} hot='{second.Text}' offset={_progScroll.VerticalOffset:0}");
        });
        QaRender3("search-ranked", delegate
        {
            ShowMenu("qa");
            _search.Text = "co";
            Snap("search-ranked");
            Info("search-ranked " + QaSearchSummary());
        });
        QaRender3("search-greek", delegate
        {
            // The Greek-layout case the greek check uses: 'calc' when something matches it, otherwise its fallback query,
            // so the render shows a query being recovered.
            ShowMenu("qa");
            (string Latin, string Greek)? gc = QaGreekCase();
            if (gc != null && gc.Value.Latin != "calc")
            {
                Info($"search-greek fallback query used: {gc.Value.Greek} ('{gc.Value.Latin}'; nothing matches 'calc', Calculator absent)");
            }
            _search.Text = gc?.Greek ?? QaGreekCalc;
            Snap("search-greek");
            Info("search-greek " + QaSearchSummary());
        });
        QaRender3("search-focused", delegate
        {
            // The state every live open shows: the caret in the empty box, before the hint, and the focus border.
            ShowMenu("qa");
            QaWithFocusedSearch(0, delegate (Rect caret)
            {
                Snap("search-focused");
                Info($"search-focused border={Hex(Win7Palette.StartColor(_searchBox.BorderBrush))} caret x={caret.X:0.##} y={caret.Y:0.##} h={caret.Height:0.##} hint x={QaBounds(_searchCue, (Visual)_searchBox.Child).Left:0.##}");
            });
        });
        QaRender3("search-focused-typed", delegate
        {
            ShowMenu("qa");
            _search.Text = "note";
            QaWithFocusedSearch(_search.Text.Length, delegate (Rect caret)
            {
                Snap("search-focused-typed");
                Info($"search-focused-typed caret x={caret.X:0.##} after '{_search.Text}'");
            });
        });
        QaRender3("search-none", delegate
        {
            ShowMenu("qa");
            _search.Text = "zzqx";
            Snap("search-none");
            Info("search-none " + QaSearchSummary());
        });
        QaRender3("search-x", delegate
        {
            ShowMenu("qa");
            _search.Text = "note";
            Snap("search-x");
            Info($"search-x clear glyph visible={_searchClear.Visibility == Visibility.Visible} magnifier hidden={_searchMag.Visibility != Visibility.Visible}");
        });
        QaRender3("empty-inventory", delegate
        {
            _qaEmptyInventory = true;
            ShowMenu("qa");
            Snap("empty-inventory");
            Info("empty-inventory rows: " + string.Join(", ", QaListRows().Select((W7Row r) => r.Text)));
        });
        if (IsVisible)
        {
            Dismiss("qa renders", instant: true);
        }
        QaReset();
    }

    private void QaStage4Checks()
    {
        QaTry("renders-stage4", delegate
        {
            string[] want = { "allprograms", "allprograms-expanded", "search-ranked", "search-greek", "search-none", "search-x", "search-focused", "search-focused-typed", "empty-inventory" };
            List<string> missing = want.Where((string s) => !_qaSheet.Any(((string State, BitmapSource Bmp) x) => x.State == s)).ToList();
            return (missing.Count == 0, $"rendered [{string.Join(", ", want.Except(missing))}]" + (missing.Count > 0 ? " missing [" + string.Join(", ", missing) + "]" : string.Empty));
        });

        QaTry("tree-counts", delegate
        {
            ShowMenu("qa");
            ToggleAllPrograms();
            IReadOnlyList<AppEntry> src = SourceApps() ?? Array.Empty<AppEntry>();
            List<AppEntry> shown = src.Where((AppEntry a) => a != null && IsMenuEntry(a)).ToList();
            int filtered = src.Count((AppEntry a) => a != null && !IsMenuEntry(a));
            // The folder of each entry is its Start menu shortcut's folder (TreeFolder), or its Category when the shortcut
            // index does not know it; folder-placement checks that folder against the files on disk.
            Dictionary<string, int> expected = shown.Where((AppEntry a) => TreeFolder(a).Length > 0)
                .GroupBy((AppEntry a) => TreeFolder(a), StringComparer.OrdinalIgnoreCase)
                .ToDictionary((IGrouping<string, AppEntry> g) => g.Key, (IGrouping<string, AppEntry> g) => g.Count(), StringComparer.OrdinalIgnoreCase);
            int expectedRoots = shown.Count((AppEntry a) => TreeFolder(a).Length == 0);
            int moved = shown.Count((AppEntry a) => !string.Equals(TreeFolder(a), a.Category ?? string.Empty, StringComparison.OrdinalIgnoreCase));
            List<TreeNode> roots = _allNodes.Where((TreeNode n) => n.App != null).ToList();
            List<TreeNode> folders = _allNodes.Where((TreeNode n) => n.Folder != null).ToList();
            List<string> bad = new List<string>();
            foreach (TreeNode f in folders)
            {
                int want = expected.TryGetValue(f.Folder!, out int c) ? c : -1;
                if (f.Children == null || f.Children.Count != want)
                {
                    bad.Add($"{f.Folder}={f.Children?.Count ?? 0}/{want}");
                }
            }
            // Display order: every root program before every folder, each part sorted by name; the list shows exactly these
            // rows while all folders are closed.
            StringComparer byName = StringComparer.CurrentCultureIgnoreCase;
            int lastRoot = _allNodes.FindLastIndex((TreeNode n) => n.App != null);
            int firstFolder = _allNodes.FindIndex((TreeNode n) => n.Folder != null);
            bool order = (lastRoot < 0 || firstFolder < 0 || lastRoot < firstFolder)
                && roots.Select((TreeNode n) => n.Name).SequenceEqual(roots.Select((TreeNode n) => n.Name).OrderBy((string n) => n, byName))
                && folders.Select((TreeNode n) => n.Name).SequenceEqual(folders.Select((TreeNode n) => n.Name).OrderBy((string n) => n, byName));
            List<W7Row> shownRows = QaListRows();
            bool rowsMatch = shownRows.Count == _allNodes.Count && shownRows.Select((W7Row r) => r.Text).SequenceEqual(_allNodes.Select((TreeNode n) => n.Name));
            bool noPseudo = !_allNodes.SelectMany((TreeNode n) => n.Children ?? new List<TreeNode> { n }).Any((TreeNode n) => n.App != null && !IsMenuEntry(n.App));
            bool ok = bad.Count == 0 && folders.Count == expected.Count && roots.Count == expectedRoots && order && rowsMatch && noPseudo && folders.Count > 0;
            string sample = string.Join(", ", folders.Take(6).Select((TreeNode f) => f.Folder + "=" + f.Children!.Count));
            return (ok, $"roots={roots.Count}/{expectedRoots} folders={folders.Count}/{expected.Count} win81 filtered={filtered} placed by the shortcut index unlike their Category={moved} mismatched=[{string.Join(", ", bad)}] order(roots then folders, sorted)={order} rows=nodes:{rowsMatch} sample: {sample}");
        });

        QaTry("keyboard-tree", delegate
        {
            ShowMenu("qa");
            ToggleAllPrograms();
            UpdateLayout();
            TreeNode folder = QaFirstFolder(3) ?? QaFirstFolder(2) ?? throw new InvalidOperationException("no folder with at least 2 programs");
            List<string> steps = new List<string>();
            bool all = true;
            void Step(string name, bool ok)
            {
                all &= ok;
                steps.Add(name + (ok ? " ok" : " FAIL"));
            }
            W7Row fr = folder.Row!;
            Select(Zone.Left, fr, byKeyboard: true);
            bool rightHandled = QaKey(Key.Right);
            Step("Right(closed folder)->expanded, folder still selected", rightHandled && folder.IsExpanded && ReferenceEquals(_sel, fr) && folder.Children!.All((TreeNode c) => c.Row != null && c.Row.Visibility == Visibility.Visible));
            QaKey(Key.Right);
            W7Row c0 = folder.Children![0].Row!;
            W7Row c1 = folder.Children[1].Row!;
            Step("Right(open folder)->first child", ReferenceEquals(_sel, c0) && c0.Depth == 1 && ReferenceEquals(c0.ParentRow, fr));
            QaKey(Key.Down);
            Step("Down->second child", ReferenceEquals(_sel, c1));
            QaKey(Key.Left);
            Step("Left(child)->its folder, still open", ReferenceEquals(_sel, fr) && folder.IsExpanded);
            QaKey(Key.Left);
            Step("Left(open folder)->collapsed", ReferenceEquals(_sel, fr) && !folder.IsExpanded && c0.Visibility == Visibility.Collapsed && ListRows().Count == _allNodes.Count);
            bool leftAgain = QaKey(Key.Left);
            Step("Left(closed folder)->left to the caret", !leftAgain && ReferenceEquals(_sel, fr));
            int launches = _qaLaunches.Count;
            QaKey(Key.Enter);
            Step("Enter(folder)->expands, menu stays open", folder.IsExpanded && IsOpen && _qaLaunches.Count == launches);
            QaKey(Key.Enter);
            Step("Enter(folder) again->collapses", !folder.IsExpanded && IsOpen);
            QaClick(fr);
            Step("click(folder)->expands, menu stays open", folder.IsExpanded && IsOpen && _qaLaunches.Count == launches);
            QaKey(Key.Right);
            QaKey(Key.Down);
            QaKey(Key.Enter);
            Step("Enter(child)->launches it", _qaLaunches.Count == launches + 1 && _qaLaunches[^1].Name == c1.Text && !IsVisible);
            ShowMenu("qa");
            ToggleAllPrograms();
            Step("next open->every folder closed", _allNodes.All((TreeNode n) => !n.IsExpanded) && ListRows().Count == _allNodes.Count);
            return (all, $"folder '{folder.Name}' ({folder.Children.Count}): " + string.Join("; ", steps));
        });

        QaTry("typing-from-allprograms", delegate
        {
            ShowMenu("qa");
            ToggleAllPrograms();
            bool inAll = _view == View.AllPrograms && ReferenceEquals(_progList, _allList);
            _search.Text = "c";
            List<W7Row> results = ListRows();
            bool search = _view == View.Search && !_allMode && ReferenceEquals(_progList, _searchList) && _cmdRow.Text == "See more results"
                && results.Count > 0 && ReferenceEquals(_sel, results[0]) && _selByKeyboard;
            _search.Clear();
            bool main = _view == View.Main && !_allMode && ReferenceEquals(_progList, _mainList) && _cmdRow.Text == "All Programs" && _sel == null
                && _allList.Visibility == Visibility.Collapsed && _searchList.Visibility == Visibility.Collapsed;
            return (inAll && search && main, $"in All Programs={inAll}; typed 'c'->search view, first result selected={search}; cleared->main view={main}");
        });

        QaTry("esc-stack-full", delegate
        {
            ShowMenu("qa");
            ToggleAllPrograms();
            TreeNode? folder = QaFirstFolder(1);
            if (folder != null)
            {
                ToggleFolder(folder, expand: true, bringIntoView: false);
            }
            _search.Text = "x";
            bool typed = _view == View.Search;
            QaKey(Key.Escape);
            bool cleared = _search.Text.Length == 0 && _view == View.Main && IsVisible && IsOpen;
            ToggleAllPrograms();
            QaKey(Key.Escape);
            bool back = _view == View.Main && !_allMode && _cmdRow.Text == "All Programs" && IsOpen;
            int t = _qaTrace.Count;
            QaKey(Key.Escape);
            bool closed = !IsVisible && TraceSince(t, "hide cause = esc");
            return (typed && cleared && back && closed, $"search from All Programs={typed}; Esc: text cleared->main view={cleared}; All Programs->Back={back}; then closed={closed}");
        });

        QaTrySkippable("ranking-calc", delegate
        {
            ShowMenu("qa");
            bool present = LoadApps().Any((AppEntry a) => string.Equals(a.Name, "Calculator", StringComparison.OrdinalIgnoreCase));
            AppEntry? top = QaTopResult("calc");
            if (!present)
            {
                return (null, $"Calculator is not in this inventory (top for 'calc' = '{top?.Name}')");
            }
            return (top != null && top.Name.Equals("Calculator", StringComparison.OrdinalIgnoreCase), "calc: " + QaSearchSummary());
        });

        QaTrySkippable("ranking-cm", delegate
        {
            ShowMenu("qa");
            if (!LoadApps().Any((AppEntry a) => string.Equals(a.Name, "Character Map", StringComparison.OrdinalIgnoreCase)))
            {
                return (null, "Character Map is not in this inventory");
            }
            // A plain substring match for 'cm' with the largest usage bonus (+20): the bonus must not lift it over the
            // acronym match.
            _qaExtraApps.Add(new AppEntry { Name = "QA Acme Tool", LaunchPath = "qa:acme", TileBrush = Brushes.Transparent });
            _qaUsage["qa:acme"] = (5000, DateTime.UtcNow.Ticks);
            List<AppEntry> apps = LoadApps();
            List<Ranked> ranked = RankSearch(apps, "cm");
            int idx = ranked.FindIndex((Ranked r) => string.Equals(r.App.Name, "Character Map", StringComparison.OrdinalIgnoreCase));
            if (idx < 0)
            {
                return (false, "Character Map does not match 'cm'");
            }
            Ranked cm = ranked[idx];
            List<Ranked> substrings = ranked.Where((Ranked r) => r.Match == SearchScore.Substring).ToList();
            bool above = substrings.Count > 0 && substrings.All((Ranked r) => r.Score < cm.Score && ranked.IndexOf(r) > idx);
            // The view shows the same order.
            QaTopResult("cm");
            List<string> view = ListRows().Select((W7Row r) => r.Text).ToList();
            int viewIdx = view.FindIndex((string n) => string.Equals(n, "Character Map", StringComparison.OrdinalIgnoreCase));
            bool viewOk = viewIdx >= 0 && substrings.All((Ranked r) =>
            {
                int i = view.IndexOf(r.App.Name);
                return i < 0 || i > viewIdx;
            });
            string top = string.Join(", ", ranked.Take(5).Select((Ranked r) => r.App.Name + "(" + r.Match + "+" + (r.Score - r.Match) + ")"));
            return (above && viewOk, $"Character Map rank {idx + 1}, match tier {cm.Match} (acronym exact {SearchScore.AcronymExact}), score {cm.Score}; plain substring matches={substrings.Count} (best score {(substrings.Count > 0 ? substrings.Max((Ranked r) => r.Score) : 0)}) all below={above}; view order agrees={viewOk}; top: {top}");
        });

        QaTrySkippable("ranking-regedit", delegate
        {
            ShowMenu("qa");
            AppEntry? reg = LoadApps().FirstOrDefault((AppEntry a) => string.Equals(a.Name, "Registry Editor", StringComparison.OrdinalIgnoreCase));
            if (reg == null)
            {
                return (null, "Registry Editor is not in this inventory");
            }
            Dictionary<string, string>? map = TargetNamesMap;
            string target = map != null && map.TryGetValue(reg.Name, out string? t) ? t : "(none)";
            AppEntry? top = QaTopResult("regedit");
            return (top != null && ReferenceEquals(top, reg), $"target name of Registry Editor='{target}' (map of {map?.Count ?? 0}); regedit: " + QaSearchSummary());
        });

        QaTrySkippable("greek", delegate
        {
            // The spec's case is 'calc' (Calculator). An inventory without a match for it (the shortcut list has no packaged
            // Calculator) uses the first of a few other queries that match something, typed on the Greek layout the same way.
            ShowMenu("qa");
            (string Latin, string Greek)? gc = QaGreekCase();
            if (gc == null)
            {
                return (null, "none of [" + string.Join(", ", QaGreekCandidates) + "] matches anything in this inventory");
            }
            (string latin, string greekQuery) = gc.Value;
            AppEntry? plain = QaTopResult(latin);
            AppEntry? greek = QaTopResult(greekQuery);
            string summary = QaSearchSummary();
            string back = GreekLayout.ToLatinKeys(greekQuery);
            bool ok = plain != null && greek != null && ReferenceEquals(greek, plain) && back == latin && (latin != "calc" || greekQuery == QaGreekCalc);
            return (ok, $"Greek-layout query for '{latin}' -> '{back}': top '{greek?.Name}' vs '{latin}' top '{plain?.Name}'; {summary}");
        });

        QaTry("enter-none", delegate
        {
            ShowMenu("qa");
            _search.Text = "zzqx";
            bool message = ListRows().Count == 0 && _progList.Children.OfType<W7Row>().Any((W7Row r) => r.RowKind == W7Row.Kind.Message22 && r.Text == NoMatchText)
                && _cmdRow.Text == "See more results" && _sel == null;
            int launches = _qaLaunches.Count;
            int actions = _qaActions.Count;
            QaKey(Key.Enter);
            bool searched = _qaActions.Count == actions + 1 && _qaActions[^1] == "search-ms:query=zzqx";
            bool ok = message && searched && _qaLaunches.Count == launches && !IsVisible;
            return (ok, $"message row + See more results={message}; Enter -> '{(_qaActions.Count > actions ? _qaActions[^1] : "(none)")}' launches {launches}->{_qaLaunches.Count} visible={IsVisible}");
        });

        QaTry("see-more-click", delegate
        {
            // The command row of the search view, clicked: the full search for the typed text, the menu closes.
            ShowMenu("qa");
            _search.Text = "note pad";
            int actions = _qaActions.Count;
            QaClick(_cmdRow);
            bool ok = _qaActions.Count == actions + 1 && _qaActions[^1] == "search-ms:query=note%20pad" && !IsVisible;
            return (ok, $"action='{(_qaActions.Count > actions ? _qaActions[^1] : "(none)")}' visible={IsVisible}");
        });

        QaTry("rebuild-allprograms-once", delegate
        {
            ShowMenu("qa");
            ToggleAllPrograms();
            ToggleAllPrograms();
            int before = _allBuilds;
            ToggleAllPrograms();
            ToggleAllPrograms();
            ShowMenu("qa");
            ToggleAllPrograms();
            ToggleAllPrograms();
            int delta = _allBuilds - before;
            // A changed inventory is picked up on the next use.
            _qaExtraApps.Add(new AppEntry { Name = "QA Added Program", LaunchPath = "qa:added", TileBrush = Brushes.Transparent });
            ToggleAllPrograms();
            int deltaChanged = _allBuilds - before;
            bool hasNew = QaRowsHave("qa:added");
            return (delta == 0 && deltaChanged == 1 && hasNew, $"build counter delta over two toggles and a reopen={delta} (0); after an inventory change={deltaChanged} (1), new entry shown={hasNew}");
        });

        QaTry("apps-reloaded", delegate
        {
            List<string> parts = new List<string>();
            bool ok = true;
            // Open, All Programs, a folder open, scrolled: a reload rebuilds the tree in place.
            ShowMenu("qa");
            ToggleAllPrograms();
            UpdateLayout();
            TreeNode? folder = QaFirstFolder(1);
            if (folder != null)
            {
                ToggleFolder(folder, expand: true, bringIntoView: false);
            }
            UpdateLayout();
            _progScroll.ScrollToVerticalOffset(Math.Min(200.0, _progScroll.ScrollableHeight));
            UpdateLayout();
            double offset = _progScroll.VerticalOffset;
            int builds = _allBuilds;
            _qaExtraApps.Add(new AppEntry { Name = "QA Reloaded Program", LaunchPath = "qa:reloaded", TileBrush = Brushes.Transparent });
            StartScreen.RaiseAppsReloaded();
            UpdateLayout();
            bool keptOffset = Math.Abs(_progScroll.VerticalOffset - offset) <= 1.0;
            bool rebuilt = _allBuilds == builds + 1 && QaRowsHave("qa:reloaded") && _view == View.AllPrograms && IsOpen;
            bool keptOpen = folder == null || _allNodes.Any((TreeNode n) => n.Folder == folder.Folder && n.IsExpanded);
            ok &= keptOffset && rebuilt && keptOpen && offset > 0.0;
            parts.Add($"visible All Programs: offset {offset:0.#}->{_progScroll.VerticalOffset:0.#} (within 1)={keptOffset} rebuilt with the new entry={rebuilt} folder kept open={keptOpen}");
            // The main view refreshes in place too.
            ShowMenu("qa");
            int rows = ListRows().Count;
            StartScreen.RaiseAppsReloaded();
            bool mainOk = IsOpen && _view == View.Main && ListRows().Count == rows;
            ok &= mainOk;
            parts.Add($"visible main view refreshed in place={mainOk}");
            // Hidden: nothing is built, the tree is only marked stale; the next use rebuilds.
            Dismiss("qa hidden reload", instant: true);
            int hiddenBuilds = _allBuilds;
            StartScreen.RaiseAppsReloaded();
            bool idle = _allBuilds == hiddenBuilds && _allStale && !IsVisible;
            ShowMenu("qa");
            ToggleAllPrograms();
            bool next = _allBuilds == hiddenBuilds + 1 && !_allStale;
            ok &= idle && next;
            parts.Add($"hidden: no work, marked stale={idle}; next use rebuilt={next}");
            return (ok, string.Join("; ", parts));
        });

        QaTry("loading-row", delegate
        {
            _qaEmptyInventory = true;
            ShowMenu("qa");
            bool main = QaOnlyMessage(LoadingText);
            ToggleAllPrograms();
            bool allPrograms = QaOnlyMessage(LoadingText);
            _search.Text = "a";
            bool search = QaOnlyMessage(LoadingText);
            int launches = _qaLaunches.Count;
            _search.Clear();
            QaKey(Key.Enter);
            bool ok = main && allPrograms && search && _qaLaunches.Count == launches && IsOpen;
            return (ok, $"loading row in main={main} all programs={allPrograms} search={search}; Enter does nothing={_qaLaunches.Count == launches}");
        });

        QaTry("display-change", delegate
        {
            ShowMenu("qa");
            int t = _qaTrace.Count;
            DismissForDisplayChange();
            bool closed = !IsVisible && TraceSince(t, "hide cause = display change");
            DismissForDisplayChange();
            return (closed && !IsVisible, $"open menu closed at once={closed}");
        });

        QaTry("geometry-tree", delegate
        {
            ShowMenu("qa");
            ToggleAllPrograms();
            UpdateLayout();
            TreeNode folder = QaFirstFolder(1) ?? throw new InvalidOperationException("no folder");
            ToggleFolder(folder, expand: true, bringIntoView: false);
            UpdateLayout();
            W7Row root = _allNodes.First((TreeNode n) => n.App != null).Row!;
            W7Row fr = folder.Row!;
            W7Row child = folder.Children![0].Row!;
            string RowGeometry(W7Row r, double iconX, double textX, out bool rowOk)
            {
                Rect rb = QaBounds(r, _progList);
                Rect ib = r.IconImage != null ? QaBounds(r.IconImage, r) : Rect.Empty;
                Rect tb = QaBounds(r.TextBlock, r);
                rowOk = Near(rb.Height, 22.0) && Near(ib.Left, iconX) && Near(ib.Top, 3.0) && Near(ib.Width, 16.0) && Near(tb.Left, textX);
                return $"'{r.Text}' h={rb.Height:0.##} icon ({ib.Left:0.##},{ib.Top:0.##}) {ib.Width:0.##}px text x {tb.Left:0.##}";
            }
            string a = RowGeometry(root, 8.0, 30.0, out bool okRoot);
            string b = RowGeometry(fr, 8.0, 30.0, out bool okFolder);
            string c = RowGeometry(child, 24.0, 46.0, out bool okChild);
            bool below = Near(QaBounds(child, _progList).Top, QaBounds(fr, _progList).Bottom);
            // The expanded folder shows the open icon, and the open icon must really look different from the closed one
            // (on recent Windows the shell's open-folder icon is pixel-identical to the closed one).
            bool openIcon = fr.IconSource is BitmapSource openBmp && ReferenceEquals(fr.IconSource, s_folderOpen)
                && openBmp.PixelWidth == 16 && openBmp.PixelHeight == 16 && !W7Icons.SamePixels(s_folderOpen, s_folderClosed);
            (int diffPx, int maxDelta) = (s_folderOpen is BitmapSource o && s_folderClosed is BitmapSource cl) ? QaDiff(cl, o) : (0, 0);
            ToggleFolder(folder, expand: false, bringIntoView: false);
            bool closedIcon = s_folderClosed != null && ReferenceEquals(fr.IconSource, s_folderClosed);
            bool ok = okRoot && okFolder && okChild && below && openIcon && closedIcon && diffPx >= 20;
            return (ok, $"root {a}; folder {b}; child {c} directly below={below}; open-folder icon shown and different from the closed one={openIcon} ({diffPx} px differ, max delta {maxDelta}, drawn in code={s_folderOpenDrawn}); closed again->closed icon={closedIcon}");
        });

        QaTry("geometry-search", delegate
        {
            ShowMenu("qa");
            _search.Text = "co";
            UpdateLayout();
            W7Row header = _progList.Children.OfType<W7Row>().First();
            List<W7Row> rows = ListRows();
            if (header.RowKind != W7Row.Kind.Header24 || header.Rule == null || rows.Count == 0)
            {
                return (false, "no header or no results: " + QaSearchSummary());
            }
            Rect hb = QaBounds(header, _progList);
            Rect tb = QaBounds(header.TextBlock, header);
            Rect rule = QaBounds(header.Rule, header);
            bool headerOk = Near(hb.Top, 0.0) && Near(hb.Height, 24.0) && Near(tb.Left, 8.0) && Near(rule.Left, tb.Right + 6.0) && Near(rule.Right, hb.Width - 8.0) && Near(rule.Top, 12.0) && Near(rule.Height, 1.0)
                && header.Text == $"Programs ({_searchMatches})"
                && ReferenceEquals(header.TextBlock.Foreground, _paletteDict!["W7.GroupHeaderInk"]) && ReferenceEquals(header.Rule.Background, _paletteDict["W7.GroupRule"]) && header.TextBlock.FontWeight == FontWeights.SemiBold;
            Rect fb = QaBounds(rows[0], _progList);
            Rect fi = rows[0].IconImage != null ? QaBounds(rows[0].IconImage!, rows[0]) : Rect.Empty;
            Rect ft = QaBounds(rows[0].TextBlock, rows[0]);
            bool firstOk = Near(fb.Top, 24.0) && Near(fb.Height, 22.0) && Near(fi.Left, 8.0) && Near(fi.Top, 3.0) && Near(ft.Left, 30.0) && ReferenceEquals(_sel, rows[0]);
            int budget = (int)Math.Floor((_layout.V - 24.0) / 22.0);
            bool budgetOk = rows.Count == Math.Min(_searchMatches, budget) && _progScroll.ScrollableHeight < 0.5;
            Rect mag = QaBounds(_cmdMagnifier, this);
            Rect lbl = QaBounds(_cmdRow.TextBlock, this);
            bool cmdOk = _cmdRow.Text == "See more results" && _cmdMagnifier.Visibility == Visibility.Visible && _cmdTriangle.Visibility == Visibility.Collapsed
                && Near(mag.Left, 7.0 + 10.0) && Near(mag.Width, 12.0) && Near(lbl.Left, 7.0 + 28.0);
            bool ok = headerOk && firstOk && budgetOk && cmdOk;
            return (ok, $"header '{header.Text}' {R4(hb)} text {R4(tb)} rule {R4(rule)} (from text end + 6 to width - 8, y 12) ok={headerOk}; first result {R4(fb)} icon {R4(fi)} text x {ft.Left:0.##} selected={ReferenceEquals(_sel, rows[0])} ok={firstOk}; rows {rows.Count} of {_searchMatches} (budget {budget}) scrollable={_progScroll.ScrollableHeight:0.#} ok={budgetOk}; command '{_cmdRow.Text}' magnifier x {mag.Left:0.##} ({mag.Width:0.##} px) label x {lbl.Left:0.##} ok={cmdOk}");
        });

        QaTry("geometry-message", delegate
        {
            ShowMenu("qa");
            _search.Text = "zzqx";
            UpdateLayout();
            W7Row msg = _progList.Children.OfType<W7Row>().First();
            Rect mb = QaBounds(msg, _progList);
            Rect tb = QaBounds(msg.TextBlock, msg);
            bool ink = ReferenceEquals(msg.TextBlock.Foreground, _paletteDict!["W7.Ink2"]);
            bool ok = msg.RowKind == W7Row.Kind.Message22 && msg.Text == NoMatchText && Near(mb.Height, 22.0) && Near(tb.Left, 8.0) && ink && !msg.IsSelectable;
            return (ok, $"'{msg.Text}' {R4(mb)} text x {tb.Left:0.##} ink=W7.Ink2:{ink}");
        });

        QaTry("search-focused", delegate
        {
            // The focus look the live menu shows on every open: the border resolves to the active palette's
            // W7.SearchBorderFocus and renders in that colour, and the caret (W7.SearchCaret) sits before the hint.
            ShowMenu("qa");
            bool ok = false;
            string detail = string.Empty;
            QaWithFocusedSearch(0, delegate (Rect caret)
            {
                object want = _paletteDict!["W7.SearchBorderFocus"];
                bool border = ReferenceEquals(_searchBox.BorderBrush, want);
                RenderTargetBitmap bmp = SnapBitmap();
                Rect box = QaBounds(_searchBox, this);
                Color edge = QaPixel(bmp, (int)Math.Floor(box.Left), (int)Math.Floor(box.Top + box.Height / 2.0));
                Color wantEdge = Win7Palette.StartColor(want);
                bool rendered = QaSame(edge, wantEdge);
                Color caretPx = QaPixel(bmp, (int)Math.Floor(box.Left + 1.0 + caret.X), (int)Math.Floor(box.Top + 1.0 + caret.Y + caret.Height / 2.0));
                Color wantCaret = Win7Palette.StartColor(_paletteDict["W7.SearchCaret"]);
                bool caretOk = QaSame(caretPx, wantCaret);
                double hintX = QaBounds(_searchCue, (Visual)_searchBox.Child).Left;
                bool before = hintX - caret.X >= 2.0;
                ok = border && rendered && caretOk && before;
                detail = $"border=W7.SearchBorderFocus:{border} rendered {Hex(edge)} (want {Hex(wantEdge)})={rendered}; caret {Hex(caretPx)} (want {Hex(wantCaret)})={caretOk} at x {caret.X:0.##}, hint x {hintX:0.##} (caret before the hint)={before}";
            });
            bool restored = ReferenceEquals(_searchBox.BorderBrush, _paletteDict!["W7.SearchBorder"]);
            return (ok && restored, detail + $"; unfocused border restored={restored}");
        });

        QaTry("search-keys-caret", delegate
        {
            // While the search view shows, the keys that edit the query stay with the box (Windows 7): Left, Right, Home
            // and End always, PageUp, PageDown, Apps and Shift+F10 while the highlight is the automatic first result, and
            // any of them with Shift or Ctrl. Up and Down move the highlight; Enter launches it.
            QaInjectDefault();
            ShowMenu("qa");
            List<string> steps = new List<string>();
            bool all = true;
            void Step(string name, bool ok)
            {
                all &= ok;
                steps.Add(name + (ok ? " ok" : " FAIL"));
            }
            _search.Text = "s";
            List<W7Row> rows = ListRows();
            if (rows.Count < 2)
            {
                return (false, "two results needed for 's': " + QaSearchSummary());
            }
            W7Row first = rows[0];
            Step("typed->first result pre-selected, automatic", ReferenceEquals(_sel, first) && _selAuto && _selByKeyboard);
            int n = _qaChildRequests.Count;
            List<string> kept = new List<string>();
            bool keysOk = true;
            foreach (Key k in new[] { Key.Right, Key.Left, Key.Home, Key.End, Key.PageUp, Key.PageDown })
            {
                bool h = QaKey(k);
                keysOk &= !h && ReferenceEquals(_sel, first) && _view == View.Search;
                kept.Add(k + (h ? "(taken)" : string.Empty));
            }
            Step("Right/Left/Home/End/PageUp/PageDown->left to the box [" + string.Join(" ", kept) + "]", keysOk);
            bool modsOk = true;
            foreach (ModifierKeys m in new[] { ModifierKeys.Shift, ModifierKeys.Control, ModifierKeys.Shift | ModifierKeys.Control })
            {
                _qaMods = m;
                bool taken = QaKey(Key.Home) | QaKey(Key.End) | QaKey(Key.Right) | QaKey(Key.Left);
                _qaMods = ModifierKeys.None;
                modsOk &= !taken && ReferenceEquals(_sel, first);
            }
            Step("Shift/Ctrl + Home/End/Right/Left->left to the box", modsOk);
            bool apps = QaKey(Key.Apps);
            _qaMods = ModifierKeys.Shift;
            bool f10 = QaKey(Key.F10);
            _qaMods = ModifierKeys.None;
            Step("Apps/Shift+F10 on the automatic result->left to the box (its edit menu)", !apps && !f10 && _qaChildRequests.Count == n);
            bool down = QaKey(Key.Down);
            Step("Down->second result", down && ReferenceEquals(_sel, rows[1]) && !_selAuto);
            bool caretAfterMove = !QaKey(Key.Right) && !QaKey(Key.Home) && !QaKey(Key.End) && ReferenceEquals(_sel, rows[1]);
            Step("Right/Home/End after Down->still the box's", caretAfterMove);
            bool pageUp = QaKey(Key.PageUp);
            Step("PageUp after a move->first result", pageUp && ReferenceEquals(_sel, first));
            n = _qaChildRequests.Count;
            bool appsMoved = QaKey(Key.Apps);
            Step("Apps after a move->item menu of the result", appsMoved && _qaChildRequests.Count == n + 1 && ReferenceEquals(_qaChildRequests[^1].Target, first));
            _search.Clear();
            _search.Text = "s";
            Step("typing again->automatic again", _selAuto && ReferenceEquals(_sel, ListRows()[0]));
            int launches = _qaLaunches.Count;
            string top = ListRows()[0].Text;
            QaKey(Key.Enter);
            Step("Enter->launches the pre-selected result", _qaLaunches.Count == launches + 1 && _qaLaunches[^1].Name == top && !IsVisible);
            // Outside the search view the list keys stay list keys, but not with Shift or Ctrl.
            ShowMenu("qa");
            ToggleAllPrograms();
            List<W7Row> tree = ListRows();
            Select(Zone.Left, tree[0], byKeyboard: true);
            _qaMods = ModifierKeys.Shift;
            bool shiftEnd = QaKey(Key.End);
            _qaMods = ModifierKeys.None;
            Step("All Programs: Shift+End->left to the box", !shiftEnd && ReferenceEquals(_sel, tree[0]));
            bool end = QaKey(Key.End);
            Step("All Programs: End->last row", end && ReferenceEquals(_sel, tree[^1]));
            return (all, string.Join("; ", steps));
        });

        QaTry("letter-retry-kept-rows", delegate
        {
            // All Programs rows are kept between opens. A row built while its icon was missing (a letter tile, as after a
            // cold logon) gets the real icon on a later visit once the letter is older than LetterRetryMs: root rows when
            // the tree is shown again, a folder's children when the folder is opened again.
            ShowMenu("qa");
            ToggleAllPrograms();
            UpdateLayout();
            const int px = 16;
            bool Real(AppEntry a) => !IsGlyphEntry(a) && W7Icons.TryGet(a.LaunchPath, 16, px, out W7Icons.Entry e) && !e.IsLetter;
            TreeNode? rootNode = _allNodes.FirstOrDefault((TreeNode n) => n.App != null && Real(n.App));
            TreeNode? folder = null;
            foreach (TreeNode f in _allNodes.Where((TreeNode n) => n.Folder != null))
            {
                ToggleFolder(f, expand: true, bringIntoView: false);
                bool has = f.Children!.Any((TreeNode c) => Real(c.App!));
                ToggleFolder(f, expand: false, bringIntoView: false);
                if (has)
                {
                    folder = f;
                    break;
                }
            }
            if (rootNode == null || folder == null)
            {
                return (false, "no root program or folder child with a real icon");
            }
            AppEntry rootApp = rootNode.App!;
            AppEntry childApp = folder.Children!.First((TreeNode c) => Real(c.App!)).App!;
            string folderName = folder.Folder!;
            W7Icons.QaPutLetter(rootApp.LaunchPath, 16, px, rootApp.Name);
            W7Icons.QaPutLetter(childApp.LaunchPath, 16, px, childApp.Name);
            // Rebuilt now (as at a cold logon): both rows show their letter.
            _allStale = true;
            ToggleAllPrograms();
            ToggleAllPrograms();
            int builds = _allBuilds;
            W7Row RowOf(AppEntry a) => _allNodes.SelectMany((TreeNode n) => n.Children ?? new List<TreeNode> { n }).First((TreeNode n) => ReferenceEquals(n.App, a)).Row!;
            TreeNode f2 = _allNodes.First((TreeNode n) => string.Equals(n.Folder, folderName, StringComparison.OrdinalIgnoreCase));
            ToggleFolder(f2, expand: true, bringIntoView: false);
            ToggleFolder(f2, expand: false, bringIntoView: false);
            W7Row rootRow = RowOf(rootApp);
            W7Row childRow = RowOf(childApp);
            bool Letter(W7Row r, AppEntry a) => W7Icons.TryGet(a.LaunchPath, 16, px, out W7Icons.Entry e) && e.IsLetter && ReferenceEquals(r.IconSource, e.Img) && _letterRows.Contains(r);
            bool lettersShown = Letter(rootRow, rootApp) && Letter(childRow, childApp);
            // While fresh, a later visit keeps the letter and loads nothing.
            int loads = W7Icons.SyncLoads;
            ToggleAllPrograms();
            ToggleAllPrograms();
            bool freshKept = Letter(rootRow, rootApp) && W7Icons.SyncLoads == loads;
            long saved = W7Icons.LetterRetryMs;
            bool rootFixed;
            bool childFixed;
            bool childWaited;
            try
            {
                W7Icons.LetterRetryMs = 0;
                ToggleAllPrograms();
                ToggleAllPrograms();
                rootFixed = Real(rootApp) && W7Icons.TryGet(rootApp.LaunchPath, 16, px, out W7Icons.Entry re) && ReferenceEquals(rootRow.IconSource, re.Img) && !_letterRows.Contains(rootRow);
                childWaited = Letter(childRow, childApp);
                ToggleFolder(f2, expand: true, bringIntoView: false);
                childFixed = Real(childApp) && W7Icons.TryGet(childApp.LaunchPath, 16, px, out W7Icons.Entry ce) && ReferenceEquals(childRow.IconSource, ce.Img) && !_letterRows.Contains(childRow);
            }
            finally
            {
                W7Icons.LetterRetryMs = saved;
            }
            bool kept = _allBuilds == builds && ReferenceEquals(RowOf(rootApp), rootRow);
            bool ok = lettersShown && freshKept && rootFixed && childWaited && childFixed && kept;
            return (ok, $"root '{rootApp.Name}', child '{childApp.Name}' of '{folderName}': letters shown after a cold build={lettersShown}; fresh letter kept, no load={freshKept}; stale: root row got its icon on the next visit={rootFixed}, closed folder's child untouched={childWaited}, child got its icon when the folder opened={childFixed}; rows kept (no rebuild)={kept}");
        });

        QaTry("folder-placement", delegate
        {
            // Independent of the tree builder: every *.lnk and *.url in the Start menu (Startup skipped), by file name, by
            // the shell's display name and by target. A root entry of All Programs backed by a shortcut that sits in a
            // folder is misplaced (a folder copy wins over a root copy of the same name).
            ShowMenu("qa");
            ToggleAllPrograms();
            HashSet<string> subNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            HashSet<string> rootNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            HashSet<string> subTargets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            Dictionary<string, string> lnkTargets = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            List<string> skip = new[] { Environment.SpecialFolder.Startup, Environment.SpecialFolder.CommonStartup }
                .Select((Environment.SpecialFolder sf) => Environment.GetFolderPath(sf)).Where((string s) => s.Length > 0).Select((string s) => s.TrimEnd('\\') + "\\").ToList();
            int files = 0;
            foreach (Environment.SpecialFolder sf in new[] { Environment.SpecialFolder.CommonStartMenu, Environment.SpecialFolder.StartMenu })
            {
                string programs = Path.Combine(Environment.GetFolderPath(sf), "Programs");
                if (!Directory.Exists(programs))
                {
                    continue;
                }
                foreach (string file in Directory.EnumerateFiles(programs, "*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true }))
                {
                    string ext = Path.GetExtension(file).ToLowerInvariant();
                    if ((ext != ".lnk" && ext != ".url") || skip.Any((string s) => file.StartsWith(s, StringComparison.OrdinalIgnoreCase)))
                    {
                        continue;
                    }
                    files++;
                    bool inFolder = Path.GetRelativePath(programs, Path.GetDirectoryName(file)!) != ".";
                    HashSet<string> names = inFolder ? subNames : rootNames;
                    names.Add(Path.GetFileNameWithoutExtension(file));
                    if (ShellDisplayName(file) is string display)
                    {
                        names.Add(display);
                    }
                    string? target = null;
                    try
                    {
                        if (ext == ".lnk")
                        {
                            byte[] bytes = File.ReadAllBytes(file);
                            target = NormalizeTarget(ShellLink.GetTargetPath(bytes) ?? EnvironmentTarget(bytes));
                            if (target != null)
                            {
                                lnkTargets[file] = target;
                            }
                        }
                        else
                        {
                            target = NormalizeTarget(UrlOf(file));
                        }
                    }
                    catch
                    {
                    }
                    if (inFolder && target != null)
                    {
                        subTargets.Add(target);
                    }
                }
            }
            List<string> misplaced = new List<string>();
            int roots = 0;
            foreach (TreeNode n in _allNodes.Where((TreeNode x) => x.App != null))
            {
                roots++;
                AppEntry a = n.App!;
                string? target = AppsFolderTarget(a.LaunchPath) ?? (lnkTargets.TryGetValue(a.LaunchPath, out string? lt) ? lt : null);
                if (subNames.Contains(a.Name) || (!rootNames.Contains(a.Name) && target != null && subTargets.Contains(target)))
                {
                    misplaced.Add(a.Name);
                }
            }
            return (misplaced.Count == 0 && files > 0, $"shortcut files={files} (in folders: {subNames.Count} names, {subTargets.Count} targets); root entries={roots}, backed by a shortcut in a folder={misplaced.Count}" + (misplaced.Count > 0 ? " [" + string.Join(", ", misplaced) + "]" : string.Empty));
        });

        QaTry("ranking-targets", delegate
        {
            // A program found only through its shortcut's target name: the file name matched exactly (one point above the
            // WordStart tier), as a prefix or at a word start (one point below it), never by substring or typo. Hosts
            // started with arguments and targets shared by several programs give no name: 'co' never finds Administrative
            // Tools (control.exe), 'cm' never finds a developer prompt (cmd.exe /k ...), 'cmd' finds Command Prompt first.
            ShowMenu("qa");
            Dictionary<string, string>? map = TargetNamesMap;
            if (map == null)
            {
                return (false, "the shortcut index is not ready");
            }
            List<AppEntry> apps = LoadApps();
            List<string> bad = new List<string>();
            List<string> targetOnly = new List<string>();
            foreach (string q in new[] { "co", "con", "note", "cm", "cmd", "chrome", "regedit", "pad", "msc", "task" })
            {
                foreach (Ranked r in RankSearch(apps, q))
                {
                    string qg = GreekLayout.ToLatinKeys(q);
                    int byName = Math.Max(SearchScore.Name(r.App.Name, q), qg.Length > 0 ? SearchScore.Name(r.App.Name, qg) - 1 : 0);
                    if (byName > 0)
                    {
                        continue;
                    }
                    string t = map.TryGetValue(r.App.Name, out string? tv) ? tv : "(none)";
                    int tier = SearchScore.Name(t, q);
                    targetOnly.Add($"{q}->{r.App.Name}({t}, {r.Match})");
                    int want = tier == SearchScore.Exact ? SearchScore.WordStart + 1 : SearchScore.WordStart - 1;
                    if (r.Match != want || !(tier == SearchScore.Exact || tier == SearchScore.Prefix || tier == SearchScore.WordStart))
                    {
                        bad.Add($"{q}->{r.App.Name}({t}, tier {tier}, match {r.Match})");
                    }
                }
            }
            List<string> notes = new List<string>();
            bool hostsOk = true;
            if (apps.Any((AppEntry a) => a.Name.Equals("Administrative Tools", StringComparison.OrdinalIgnoreCase)))
            {
                bool admin = RankSearch(apps, "co").Any((Ranked r) => r.App.Name.Equals("Administrative Tools", StringComparison.OrdinalIgnoreCase));
                hostsOk &= !admin;
                notes.Add($"'co' finds Administrative Tools={admin}");
            }
            List<string> devPrompts = RankSearch(apps, "cm").Where((Ranked r) => r.App.Name.Contains("Command Prompt for VS", StringComparison.OrdinalIgnoreCase)).Select((Ranked r) => r.App.Name).ToList();
            hostsOk &= devPrompts.Count == 0;
            notes.Add($"'cm' finds developer prompts={devPrompts.Count}");
            // 'note' never reaches a program through a typo of its target (a .txt target 'Does not start' once did).
            List<string> typos = RankSearch(apps, "note")
                .Where((Ranked r) => SearchScore.Name(r.App.Name, "note") == 0 && !(map.TryGetValue(r.App.Name, out string? nt) && SearchScore.Name(nt, "note") >= SearchScore.WordStart))
                .Select((Ranked r) => r.App.Name).ToList();
            hostsOk &= typos.Count == 0;
            notes.Add($"'note' results through a target typo={typos.Count}" + (typos.Count > 0 ? " [" + string.Join(", ", typos) + "]" : string.Empty));
            if (apps.Any((AppEntry a) => a.Name.Equals("Command Prompt", StringComparison.OrdinalIgnoreCase)) && map.TryGetValue("Command Prompt", out string? cp) && cp == "cmd")
            {
                List<Ranked> cmd = RankSearch(apps, "cmd");
                bool first = cmd.Count > 0 && cmd[0].App.Name.Equals("Command Prompt", StringComparison.OrdinalIgnoreCase);
                hostsOk &= first;
                notes.Add($"'cmd' -> Command Prompt first={first} ({string.Join(", ", cmd.Take(3).Select((Ranked r) => r.App.Name + "(" + r.Score + ")"))})");
            }
            else
            {
                notes.Add("'cmd' case not applicable (no Command Prompt shortcut to cmd.exe)");
            }
            bool ok = bad.Count == 0 && hostsOk;
            return (ok, $"target names={map.Count}; target-only matches={targetOnly.Count} [{string.Join(", ", targetOnly)}] wrong tier={bad.Count}" + (bad.Count > 0 ? " [" + string.Join(", ", bad) + "]" : string.Empty) + "; " + string.Join("; ", notes));
        });

        QaTry("idle-clean", delegate
        {
            ShowMenu("qa");
            ToggleAllPrograms();
            UpdateLayout();
            TreeNode? folder = QaFirstFolder(1);
            if (folder != null)
            {
                ToggleFolder(folder, expand: true, bringIntoView: false);
            }
            UpdateLayout();
            SmoothScroll.ByVertical(_progScroll, 120.0);
            HardHide(cleanFrame: true);
            QaPump(300);
            // Every DispatcherTimer field of the menu (whatever its name) must be empty once the hide is done.
            List<System.Reflection.FieldInfo> timers = typeof(Win7StartMenu)
                .GetFields(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public)
                .Where((System.Reflection.FieldInfo f) => typeof(DispatcherTimer).IsAssignableFrom(f.FieldType))
                .ToList();
            List<string> live = timers.Where((System.Reflection.FieldInfo f) => f.GetValue(this) != null).Select((System.Reflection.FieldInfo f) => f.Name).ToList();
            bool hidden = !IsVisible && !_hidePending && _renderHook == null && live.Count == 0 && !SmoothScroll.IsVerticalActive(_progScroll)
                && !_root.HasAnimatedProperties && !_progScroll.HasAnimatedProperties && _allNodes.All((TreeNode n) => !n.IsExpanded);
            // The icon worker: one live request loads off the UI thread, and the worker exits once its queue stays empty.
            AppEntry? a = LoadApps().FirstOrDefault((AppEntry x) => !IsGlyphEntry(x));
            if (a == null)
            {
                return (false, "no inventory entry");
            }
            int px = 49 + 2 * s_qaWorkerRuns++;
            string key = W7Icons.Key(a.LaunchPath, 32, px);
            int raised = 0;
            Action<string> onLoaded = delegate (string k)
            {
                if (k == key)
                {
                    raised++;
                }
            };
            W7Icons.Synchronous = false;
            W7Icons.Loaded += onLoaded;
            bool exited;
            try
            {
                W7Icons.Request(a.LaunchPath, 32, px, a.Name);
                Stopwatch sw = Stopwatch.StartNew();
                while (raised == 0 && sw.ElapsedMilliseconds < 4000)
                {
                    QaPump(20);
                }
                QaPump(2100);
                exited = !W7Icons.WorkerAlive;
            }
            finally
            {
                W7Icons.Loaded -= onLoaded;
                W7Icons.Synchronous = true;
            }
            bool worker = raised == 1 && exited;
            return (hidden && worker, $"after HardHide: visible={IsVisible} renderHook={_renderHook != null} timer fields={timers.Count} non-null=[{string.Join(", ", live)}] smoothScroll={SmoothScroll.IsVerticalActive(_progScroll)} folders closed={_allNodes.All((TreeNode n) => !n.IsExpanded)}; icon worker loaded={raised} exited after 2.1 s idle={exited}");
        });

        QaTry("prewarm-offscreen", delegate
        {
            // A second menu prewarmed the way App does at idle: everything is built, nothing is shown or activated, and the
            // first open after it neither rebuilds All Programs nor loads icons on the UI thread.
            Win7StartMenu m2 = new Win7StartMenu(_appsProvider, _launch, OpenSettings);
            m2.QaInit();
            m2.QaReset();
            try
            {
                int loads = W7Icons.SyncLoads;
                m2.Prewarm();
                // The root is laid out at its own size inside the window size (the shadow band paints 6 of its 8 px).
                bool laidOut = m2._root.IsMeasureValid && m2._root.IsArrangeValid && m2._root.RenderSize.Width > 0.0 && m2._root.RenderSize.Width <= m2._layout.W
                    && Near(m2._root.RenderSize.Height, m2._layout.H);
                bool built = m2._prewarmed && !m2.IsVisible && m2._qaActivations == 0 && m2._allBuilds == 1 && m2._mainList.Children.Count > 0
                    && laidOut && m2._qaTrace.Any((string l) => l.StartsWith("Win7 Start: prewarmed", StringComparison.Ordinal));
                int afterPrewarm = W7Icons.SyncLoads;
                m2.ShowMenu("qa");
                m2.ToggleAllPrograms();
                bool reused = m2._allBuilds == 1 && W7Icons.SyncLoads == afterPrewarm && m2._qaActivations == 0;
                double prep = m2._qaPrepMs.Count > 0 ? m2._qaPrepMs[^1].Ms : -1.0;
                Info($"prewarm-offscreen: prewarm loaded {afterPrewarm - loads} icons; first open prepare {prep:0.00}ms");
                string line = m2._qaTrace.FirstOrDefault((string l) => l.StartsWith("Win7 Start: prewarmed", StringComparison.Ordinal)) ?? "(no prewarm trace)";
                return (built && reused, $"prewarmed hidden, not activated, main + All Programs built, laid out {m2._root.RenderSize.Width:0.##}x{m2._root.RenderSize.Height:0.##} in a {m2._layout.W:0.##}x{m2._layout.H:0.##} window={laidOut}; all={built}; first open reused the tree and loaded no icon={reused}; trace: {line}");
            }
            finally
            {
                m2.DisposeMenu();
            }
        });

        QaTry("index-late-rebuild", delegate
        {
            // The live menu is prewarmed at logon, usually before the background shortcut index is ready: that tree uses
            // the inventory's own folders and is built again once, with the shortcut folders, when the index arrives.
            ShortcutIndex? ready = System.Threading.Volatile.Read(ref s_shortcuts);
            if (ready == null)
            {
                return (false, "the shortcut index is not ready");
            }
            Win7StartMenu m2 = new Win7StartMenu(_appsProvider, _launch, OpenSettings);
            m2.QaInit();
            m2.QaReset();
            try
            {
                System.Threading.Volatile.Write(ref s_shortcuts, null);
                m2.Prewarm();
                int before = m2._allBuilds;
                bool builtWithout = before == 1 && m2._allRevIndex == null;
                System.Threading.Volatile.Write(ref s_shortcuts, ready);
                int t = m2._qaTrace.Count;
                m2.OnShortcutIndexReady();
                bool traced = m2.TraceSince(t, "shortcut index ready");
                m2.ShowMenu("qa");
                m2.ToggleAllPrograms();
                bool rebuilt = m2._allBuilds == before + 1 && ReferenceEquals(m2._allRevIndex, ready);
                m2.ToggleAllPrograms();
                m2.ToggleAllPrograms();
                bool once = m2._allBuilds == before + 1;
                bool hidden = m2._qaActivations == 0;
                return (builtWithout && traced && rebuilt && once && hidden, $"prewarm before the index: built without it={builtWithout}; index ready traced={traced}; next use rebuilt with the index={rebuilt}; later uses kept it={once}; never activated={hidden}");
            }
            finally
            {
                System.Threading.Volatile.Write(ref s_shortcuts, ready);
                m2.DisposeMenu();
            }
        });
    }

    // Timing metrics, recorded and not gated (real-time scanning on the test machine makes them noisy).
    private void QaPerfMetrics()
    {
        _qaCurrentCheck = "perf";
        try
        {
            // The checks before leave garbage behind (whole second menus among it); the timings start from a settled
            // heap, as the live menu's would.
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            QaReset();
            QaInjectDefault();
            ShowMenu("qa");
            Dismiss("qa perf", instant: true);
            List<double> prep = new List<double>();
            List<double> show = new List<double>();
            for (int i = 0; i < 20; i++)
            {
                ShowMenu("qa");
                prep.Add(_qaPrepMs[^1].Ms);
                show.Add(_qaShowMs[^1]);
                Dismiss("qa perf", instant: true);
            }
            Metric("prepare-warm-median-ms", Median(prep));
            Metric("prepare-warm-max-ms", prep.Max());
            Metric("show-warm-median-ms", Median(show));
            Metric("show-warm-max-ms", show.Max());

            ShowMenu("qa");
            _allStale = true;
            Stopwatch build = Stopwatch.StartNew();
            ToggleAllPrograms();
            UpdateLayout();
            Metric("allprograms-build-ms", build.Elapsed.TotalMilliseconds);
            ToggleAllPrograms();
            UpdateLayout();
            List<double> toggles = new List<double>();
            for (int i = 0; i < 5; i++)
            {
                Stopwatch sw = Stopwatch.StartNew();
                ToggleAllPrograms();
                UpdateLayout();
                toggles.Add(sw.Elapsed.TotalMilliseconds);
                ToggleAllPrograms();
                UpdateLayout();
            }
            Metric("allprograms-toggle-ms", Median(toggles));

            List<double> keys = new List<double>();
            List<double> keyBuild = new List<double>();
            for (int i = 0; i < 7; i++)
            {
                _search.Text = "c";
                UpdateLayout();
                Stopwatch sw = Stopwatch.StartNew();
                _search.Text = "co";
                double built = sw.Elapsed.TotalMilliseconds;
                UpdateLayout();
                keys.Add(sw.Elapsed.TotalMilliseconds);
                keyBuild.Add(built);
                _search.Clear();
                UpdateLayout();
            }
            Metric("keystroke-co-ms", Median(keys));
            Metric("keystroke-co-rows-ms", Median(keyBuild));

            List<AppEntry> apps = LoadApps();
            List<double> ranks = new List<double>();
            int n = 0;
            for (int i = 0; i < 20; i++)
            {
                Stopwatch sw = Stopwatch.StartNew();
                n = RankSearch(apps, "co").Count;
                ranks.Add(sw.Elapsed.TotalMilliseconds);
            }
            Metric("ranking-co-ms", Median(ranks));
            Info($"perf: {apps.Count} programs, 'co' matches {n}; warm prepare over 20 opens, toggles over 5, keystrokes over 7 (median; rows = ranking and row building before layout), ranking over 20 (median)");
        }
        catch (Exception ex)
        {
            Info("perf metrics failed: " + ex.GetType().Name + " " + ex.Message);
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
