using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Microsoft.Win32;

#nullable enable annotations

namespace Win81Layer;

// The Windows 7 Start menu: an opt-in alternative to the Metro Start screen, enabled only by
// AppSettings.Win7StartMenuEnabled and opened through App.ToggleStartAt. The Metro 8.1 Start screen stays the default and
// keeps its own code paths; nothing here is driven by the desktop composition mode.
//
// Layout: a painted Aero glass frame (no blur, no Microsoft image assets) with a list pane on the left (pinned and
// recent programs, All Programs as a one-level folder tree, ranked search results), the search box on the glass, and a
// right column of place links, the account picture frame and a Shut down split button. The window is small, layered and
// anchored flush on the taskbar of the monitor under the pointer.
//
// Architecture, by file:
// - this file: the lifecycle. One window is prewarmed at idle and reused; ShowMenu prepares the content, places the
//   window and shows it under an activation guard; Dismiss fades or hides it (a hide waits for a transparent frame so the
//   next show never flashes stale content); light dismiss comes from the global mouse hook; Alt+F4 only dismisses and
//   DisposeMenu is the one real close. Static events (theme, accent, icon loads, inventory reloads, display changes)
//   only set flags while the menu is hidden, and no timer or frame hook exists while it is closed.
// - Palette: one frozen W7.* brush dictionary per theme, swapped whole; every element reads it by resource reference.
// - Chrome: the frame, list pane, search band, right column and Shut down button, sized by LayoutFor/ApplyLayout.
// - Rows: the W7Row element, the virtual-cursor keyboard model (focus never leaves the search box) and press tracking.
// - Views: the three list containers (main, All Programs, search), the recent-list rules and search ranking.
// - Icons: exact-size icons loaded on a background worker into a strong cache, with plates for single-colour logos.
// - Menus: the Win7 context menus (item, power, edit, empty area), opened through one child-menu helper.
// - Qa: the offscreen harness (--win7starttest), which injects all user data and records side effects.
public sealed partial class Win7StartMenu : Window
{
    private readonly Func<IReadOnlyList<AppEntry>> _appsProvider;

    private readonly Action<AppEntry, bool> _launch;

    private StackPanel _progList = null!;

    private ScrollViewer _progScroll = null!;

    private TextBox _search = null!;

    private FrameworkElement _root = null!;

    private bool _allMode;

    // Lifecycle: Hidden -> Open -> Hidden. HidePending covers an instant close that waits for one transparent frame
    // before the HWND hides, so the reused window never shows stale content on the next open.
    private bool _showing;          // inside our own Show()+ForceForeground: a Deactivated raised there is a bounce

    private bool _dismissing;       // a close is in flight

    private bool _hidePending;      // transparent, waiting for the frame that lets the HWND hide

    private bool _qa;               // offscreen harness: never activates, places, launches or writes settings

    private bool _childMenuOpen;    // a child menu (power flyout or item menu) owns activation; keep Start open

    private bool _childCommandRan;  // a child-menu item ran; Start takes the foreground back when the menu closes

    private bool _paletteDirty;

    private bool _resetting;        // ResetViewState clears the box: TextChanged must not refresh twice

    private bool _openRaised;

    // Only DisposeMenu sets this. Any other close request (Alt+F4) is turned into a dismiss.
    internal bool AllowClose;

    private int _gen;

    private int _childClosedTick;

    private long _shownTick;

    private long _dismissStartTick;

    private System.Drawing.Rectangle _childRectPx;

    private ContextMenu? _openChild;

    private DispatcherTimer? _closeFallback;   // exists only while a close is in flight

    private EventHandler? _renderHook;         // exists only while a clean-frame hide is pending

    private string? _openDevice;

    // Monitor scale the open menu was placed with (light dismiss converts the glass and frame rects with it).
    private double _placedScale = 1.0;

    // Tallest window the target work area allows (DIP, overhang included).
    private double _maxWindowDip = 1036.0;

    private int _staticSubs;

    private bool _staticsHooked;

    private bool _closed;

    public Win7StartMenu(Func<IReadOnlyList<AppEntry>> apps, Action<AppEntry, bool> launch, Action? openSettings = null)
    {
        _appsProvider = apps;
        _launch = launch;
        OpenSettings = openSettings;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Topmost = true;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Title = "Start";
        Deactivated += OnDeactivated;
        PreviewKeyDown += OnPreviewKey;
        _root = (FrameworkElement)BuildRoot();
        Content = _root;
        SubscribeStatics();
    }

    // Compatibility overload for callers that still pass a List provider and a one-argument launch.
    public Win7StartMenu(Func<List<AppEntry>> apps, Action<AppEntry> launch)
        : this(() => apps(), (a, asAdmin) => launch(a))
    {
    }

    // Opens PC Settings (the empty-area 'Properties' item uses it).
    private Action? OpenSettings { get; }

    // Set by App: the physical-pixel rect of the launcher taskbar's Start button on a monitor, when one is shown there.
    internal Func<System.Windows.Forms.Screen, System.Drawing.Rectangle?>? StartButtonRectFor { get; set; }

    // Set once by App: true when a physical screen point lies on a Start button (the launcher's or the native one).
    // A left press there belongs to the toggle (its Click), never to light dismiss.
    internal static Func<int, int, bool>? IsStartButtonAt;

    public bool IsOpen => IsVisible && !_dismissing && !_hidePending;

    public event Action<bool>? OpenChanged;

    // Device name of the monitor the open menu sits on (null while closed).
    internal string? OpenDevice => _openDevice;

    // Static events this menu is subscribed to; OnClosed brings it back to 0.
    internal int StaticSubscriptions => _staticSubs;

    private bool MotionOff => _qaMotionOff ?? (Motion.Mode == MotionMode.Off);

    private Duration Dur(Motion.Cat c) => _qaDur.HasValue ? new Duration(_qaDur.Value) : Motion.Dur(c);

    private ModifierKeys Mods() => _qa ? _qaMods : Keyboard.Modifiers;

    // Marks the theme palette for a rebuild on the next show (a hidden menu does no theme work).
    internal void MarkPaletteDirty()
    {
        _paletteDirty = true;
    }

    public void ShowMenu(string route = "other")
    {
        Stopwatch total = Stopwatch.StartNew();
        // 1. Cancel any close in progress. A press while the menu is fading out reopens it from where the fade is.
        double reopenFrom = (IsVisible && _dismissing && !_hidePending) ? _root.Opacity : 0.0;
        _shownTick = Environment.TickCount64;
        ++_gen;
        if (_hidePending)
        {
            UnhookRender();
            _hidePending = false;
        }
        StopCloseFallback();
        _root.BeginAnimation(OpacityProperty, null);
        _root.Opacity = 1.0;
        _root.IsHitTestVisible = true;
        _dismissing = false;

        // 2. Target monitor.
        System.Windows.Forms.Screen? scr = null;
        System.Drawing.Rectangle work = System.Drawing.Rectangle.Empty;
        double scale = 1.0;
        double maxWindowDip = 1036.0;   // QA: a 1080 px monitor with a 40 px bar, minus the 4 DIP margin
        if (!_qa)
        {
            ResolveTarget(out scr, out work, out scale, out maxWindowDip);
        }
        _iconScale = scale;

        // 3. Content.
        int iconLoadsBefore = W7Icons.SyncLoads;
        Stopwatch prep = Stopwatch.StartNew();
        PrepareForShow(maxWindowDip);
        double prepMs = prep.Elapsed.TotalMilliseconds;
        if (_qa)
        {
            _qaPrepMs.Add((prepMs, W7Icons.SyncLoads - iconLoadsBefore));
        }

        // 4. Placement (live only: the QA window stays at -4000,-4000 and is never moved or re-targeted).
        string edge = "none";
        if (!_qa && scr != null)
        {
            try
            {
                edge = ApplyPlacement(scr, work, scale);
            }
            catch (Exception ex)
            {
                Trace("Win7 Start: placement failed: " + ex.Message);
            }
        }
        // A dismiss that runs inside Show() clears _openDevice; the backstop below restores it if that dismiss is cancelled.
        string? placedDevice = _openDevice;

        // 4b. The open fade starts before Show(), so the first composed frame is already transparent.
        if (!MotionOff)
        {
            BeginOpenFade(reopenFrom);
        }

        // The window appears under a resting pointer: that is not a hover (see ArmHoverGuard).
        ArmHoverGuard();

        // 5. Show and activate. Show()+ForceForeground can bounce activation; _showing tells OnDeactivated it is ours.
        _showing = true;
        try
        {
            if (!IsVisible)
            {
                Show();
            }
            if (!_qa)
            {
                WindowUtil.ForceForeground(this);
            }
        }
        finally
        {
            _showing = false;
        }
        if (!IsVisible)
        {
            return;
        }
        if (_dismissing || _hidePending)
        {
            // Backstop: a dismiss still ran inside the show. If we ended up active it was a bounce, so cancel it.
            if (!IsActive && !_qa)
            {
                return;
            }
            CancelMidShowDismiss();
            _openDevice = placedDevice;
            Trace("Win7 Start: mid-show dismiss cancelled (activation bounce)");
        }

        // 6. Keyboard focus lives in the search box (live only; the QA window never takes focus).
        if (!_qa)
        {
            if (IsActive)
            {
                _search.Focus();
            }
            else
            {
                Dispatcher.BeginInvoke(DispatcherPriority.Input, (Action)delegate
                {
                    if (IsVisible && !_dismissing && !_hidePending)
                    {
                        _showing = true;
                        try
                        {
                            WindowUtil.ForceForeground(this);
                        }
                        finally
                        {
                            _showing = false;
                        }
                        _search.Focus();
                    }
                });
            }
        }

        RaiseOpen(true);
        double tookMs = total.Elapsed.TotalMilliseconds;
        if (_qa)
        {
            _qaShowMs.Add(tookMs);
        }
        Trace($"Win7 Start: show via {route} screen={(_openDevice ?? "none")} edge={edge} active={IsActive} fg={(_qa ? "qa" : WindowUtil.ForegroundDescription())} prep={prepMs:0.0}ms took={tookMs:0.0}ms");
    }

    // Undoes a dismiss that ran inside Show(): a clean-frame hide still waiting for its frame, or a close fade in flight.
    // Either one left the root transparent (the fade also holds it at 0 and makes it click-through), so the root is
    // restored and, with motion on, faded in again from where it was.
    private void CancelMidShowDismiss()
    {
        UnhookRender();
        StopCloseFallback();
        _hidePending = false;
        double from = _root.Opacity;
        _root.BeginAnimation(OpacityProperty, null);
        _root.Opacity = 1.0;
        _root.IsHitTestVisible = true;
        _dismissing = false;
        ++_gen;
        if (!MotionOff)
        {
            BeginOpenFade(from);
        }
    }

    // Palette, account, a fresh main view and the layout that follows it. Icons for the new rows are requested here.
    private void PrepareForShow(double maxWindowDip)
    {
        _maxWindowDip = maxWindowDip;
        EnsurePalette();
        EnsureAccount();
        ResetViewState();
        _trackProgs = _qa ? _qaTrackProgs : ReadTrackProgs();
        RefreshMain();
        ApplyLayout(LayoutFor(_mainPins, _mainRecent, maxWindowDip, _rw));
        EnsureShortcutIndex();
    }

    // True once Prewarm has run (a later inventory reload then prewarms again at idle).
    private bool _prewarmed;

    private bool _prewarmQueued;

    // Builds everything an open needs while the menu is hidden, at idle, so the first open is as fast as a warm one: the
    // window handle, the palette, the main view and its layout for the primary monitor, All Programs, the icon requests
    // for all of them (loaded on the icon worker), the account and the shortcut index (search target names and the All
    // Programs folders; a tree built before the index is ready is built again when it arrives). Never shows the window.
    internal void Prewarm()
    {
        if (_closed || IsVisible)
        {
            return;
        }
        Stopwatch sw = Stopwatch.StartNew();
        try
        {
            if (!_qa)
            {
                new WindowInteropHelper(this).EnsureHandle();
            }
            double scale = 1.0;
            double maxWindowDip = 1036.0;
            if (!_qa)
            {
                PrimaryTarget(out scale, out maxWindowDip);
            }
            _iconScale = scale;
            PrepareForShow(maxWindowDip);
            EnsureAllPrograms();
            _root.Measure(new Size(_layout.W, _layout.H));
            _root.Arrange(new Rect(0.0, 0.0, _layout.W, _layout.H));
            _prewarmed = true;
            Trace($"Win7 Start: prewarmed in {sw.Elapsed.TotalMilliseconds:0.0}ms (main rows={_mainPins + _mainRecent}, all programs entries={_allNodes.Count}, V={_layout.V:0})");
        }
        catch (Exception ex)
        {
            Trace("Win7 Start: prewarm failed: " + ex.Message);
        }
    }

    // One queued idle prewarm at a time; nothing is queued while the menu is open or closed for good.
    private void QueuePrewarm()
    {
        if (_prewarmQueued || _closed || _qa)
        {
            return;
        }
        _prewarmQueued = true;
        Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, (Action)delegate
        {
            _prewarmQueued = false;
            Prewarm();
        });
    }

    private static void PrimaryTarget(out double scale, out double maxWindowDip)
    {
        System.Windows.Forms.Screen scr = System.Windows.Forms.Screen.PrimaryScreen!;
        scale = MonitorDpi.ScaleFor(scr.Bounds);
        if (scale <= 0.0)
        {
            scale = 1.0;
        }
        System.Drawing.Rectangle work = TaskbarWorkArea.Current(scr);
        if (work.IsEmpty)
        {
            work = scr.WorkingArea;
        }
        maxWindowDip = Win7Placement.MaxWindowDip(work, scale);
    }

    // Back to the main view: empty search box, All Programs off, list at the top. The guard keeps the Clear() from
    // triggering a second refresh through TextChanged.
    private void ResetViewState()
    {
        // Clear() on an empty box is not free: it still runs a change block and selection notifications (about 45 ms
        // once the text services are engaged), so it only runs when there is text.
        if (!string.IsNullOrEmpty(_search.Text))
        {
            _resetting = true;
            try
            {
                _search.Clear();
            }
            finally
            {
                _resetting = false;
            }
        }
        _allMode = false;
        _view = View.Main;
        CancelPress();
        ClearSelection(animate: false);
        ShowList(View.Main);
        UpdateCommandRow();
        UpdateScrollLane();
        // All Programs is kept between opens, but every open starts with its folders closed.
        CollapseAllFolders();
        _lastLeft = null;
        _keystrokeTraced = false;
        ResetPowerLayers();
        foreach (W7Row place in _placeRows)
        {
            place.SetHot(on: false, animate: false);
            place.SetPressed(on: false);
        }
        _cmdRow.SetHot(on: false, animate: false);
        _cmdRow.SetPressed(on: false);
        _progScroll.BeginAnimation(OpacityProperty, null);
        _progScroll.Opacity = 1.0;
        _progScroll.ScrollToTop();
    }

    private void ResolveTarget(out System.Windows.Forms.Screen scr, out System.Drawing.Rectangle work, out double scale, out double maxWindowDip)
    {
        scr = System.Windows.Forms.Screen.FromPoint(System.Windows.Forms.Cursor.Position) ?? System.Windows.Forms.Screen.PrimaryScreen!;
        scale = MonitorDpi.ScaleFor(scr.Bounds);
        if (scale <= 0.0)
        {
            scale = 1.0;
        }
        work = TaskbarWorkArea.Current(scr);
        if (work.IsEmpty)
        {
            work = scr.WorkingArea;
        }
        maxWindowDip = Win7Placement.MaxWindowDip(work, scale);
    }

    // Places the window flush on the taskbar of the target monitor, in physical pixels (safe under PerMonitorV2).
    private string ApplyPlacement(System.Windows.Forms.Screen scr, System.Drawing.Rectangle work, double scale)
    {
        AppSettings fs = SettingsStore.FastSnapshot;
        System.Drawing.Rectangle? btn = null;
        try
        {
            btn = StartButtonRectFor?.Invoke(scr);
        }
        catch (Exception ex)
        {
            Trace("Win7 Start: start button rect failed: " + ex.Message);
        }
        // The configured position describes the launcher's own bar, so it is trusted only while that bar is really up on
        // this monitor (its Start button rect is known). Otherwise (native taskbar, a bar that failed to start, a monitor
        // without a launcher bar) the edge is read from the work area.
        string edge = (fs.TaskbarEnabled && btn.HasValue)
            ? (string.IsNullOrEmpty(fs.TaskbarPosition) ? "Bottom" : fs.TaskbarPosition)
            : Win7Placement.InferEdge(scr.Bounds, work);
        var (x, y, _, _) = Win7Placement.Compute(new Win7Placement.Input(scr.Bounds, work, edge, btn, scale, _layout.W, _layout.H, Overhang, ShadowBand));
        nint hwnd = new WindowInteropHelper(this).EnsureHandle();
        if (!string.Equals(System.Windows.Forms.Screen.FromHandle(hwnd).DeviceName, scr.DeviceName, StringComparison.OrdinalIgnoreCase))
        {
            // Move to the target monitor first (no size, no z-order, no activation) so any WM_DPICHANGED is handled
            // before the DIP size and position below are applied.
            SetWindowPos(hwnd, IntPtr.Zero, x, y, 0, 0, SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);
        }
        Width = _layout.W;
        Height = _layout.H;
        Left = x / scale;
        Top = y / scale;
        // The secondary monitor renders in software when the user chose that, so the menu never creates a second
        // Direct3D device there. Set before the first frame on that monitor.
        if (PresentationSource.FromVisual(this) is HwndSource hs && hs.CompositionTarget is HwndTarget target)
        {
            RenderMode want = (!scr.Primary && fs.SecondaryMonitorSoftwareRender) ? RenderMode.SoftwareOnly : RenderMode.Default;
            if (target.RenderMode != want)
            {
                target.RenderMode = want;
            }
        }
        _openDevice = scr.DeviceName;
        _placedScale = scale;
        return btn.HasValue ? edge : edge + "(no-button)";
    }

    public void Dismiss(string cause = "action", bool instant = false)
    {
        if (!IsVisible || _hidePending)
        {
            return;
        }
        Trace($"Win7 Start: hide cause = {cause} at +{Environment.TickCount64 - _shownTick}ms");
        if (_dismissing)
        {
            // A close is already in flight: let it finish, unless it is stuck.
            if (Environment.TickCount64 - _dismissStartTick > 250)
            {
                HardHide(cleanFrame: false);
            }
            return;
        }
        CloseChildMenu();
        CancelPress();
        if (instant || MotionOff)
        {
            // HardHide raises OpenChanged(false) once IsOpen already reads false.
            HardHide(cleanFrame: !_qa);
            return;
        }
        BeginCloseFade();
    }

    // Open: the base value is 1 and the fade runs from 0 (or from where a close left it) with FillBehavior.Stop, so the
    // first frame is transparent and the end value can never fall back to 0.
    private void BeginOpenFade(double from)
    {
        _root.BeginAnimation(OpacityProperty, null);
        _root.Opacity = 1.0;
        if (from >= 0.999)
        {
            return;
        }
        DoubleAnimation fade = new DoubleAnimation(Math.Max(0.0, from), 1.0, Dur(Motion.Cat.ViewEnter))
        {
            EasingFunction = Motion.Ease(Motion.Cat.ViewEnter),
            FillBehavior = FillBehavior.Stop
        };
        fade.Freeze();
        _root.BeginAnimation(OpacityProperty, fade);
    }

    // Close: fade to 0 (CubicIn), then hide. The orb releases at the start of the fade (IsOpen is already false), the
    // menu stops taking clicks, and a 400 ms one-shot finishes the hide if the fade never completes. A newer show or
    // close (another generation) makes both ends do nothing. Completed is raised inside the animation tick, before that
    // tick's frame is drawn, so both ends hide through the clean-frame path: the layered window keeps the last frame it
    // drew, and hiding before the transparent one is drawn would flash the old content on the next open.
    private void BeginCloseFade()
    {
        _dismissing = true;
        _dismissStartTick = Environment.TickCount64;
        int g = ++_gen;
        RaiseOpen(false);
        _root.IsHitTestVisible = false;
        double from = _root.Opacity;
        DoubleAnimation fade = new DoubleAnimation(from, 0.0, Dur(Motion.Cat.ViewExit))
        {
            EasingFunction = Motion.Ease(Motion.Cat.EdgeExit),
            FillBehavior = FillBehavior.HoldEnd
        };
        fade.Completed += delegate
        {
            if (_qaSuppressCompleted)
            {
                return;
            }
            if (g == _gen && _dismissing)
            {
                HardHide(cleanFrame: !_qa);
            }
        };
        _root.BeginAnimation(OpacityProperty, fade);
        StopCloseFallback();
        DispatcherTimer fallback = new DispatcherTimer(DispatcherPriority.Normal)
        {
            Interval = TimeSpan.FromMilliseconds(400.0)
        };
        fallback.Tick += delegate
        {
            StopCloseFallback();
            if (g == _gen && _dismissing)
            {
                Trace("Win7 Start: hide completed by fallback");
                HardHide(cleanFrame: !_qa);
            }
        };
        _closeFallback = fallback;
        fallback.Start();
    }

    // The one idempotent "hide and fully reset" primitive. With cleanFrame the root goes transparent first and the HWND
    // hides only after two composed frames, so the next Show never flashes the previous content. The window is
    // click-through meanwhile and IsOpen is already false. A 250 ms one-shot forces the same end state if frames stop.
    private void HardHide(bool cleanFrame)
    {
        StopCloseFallback();
        try
        {
            SmoothScroll.Stop(_progScroll);
        }
        catch
        {
        }
        CancelPress();
        if (Mouse.Captured is DependencyObject cap && (ReferenceEquals(cap, this) || IsAncestorOf(cap)))
        {
            Mouse.Capture(null);
        }
        CloseChildMenu();
        _childMenuOpen = false;
        _openChild = null;
        _menuRow = null;
        _menuRowMenu = null;
        Topmost = true;
        _root.BeginAnimation(OpacityProperty, null);
        _root.Opacity = 0.0;
        _root.IsHitTestVisible = true;
        _dismissing = false;
        ResetViewState();
        UnhookRender();
        if (cleanFrame && !_closed)
        {
            _hidePending = true;
            int g = _gen;
            int ticks = 0;
            _renderHook = delegate
            {
                if (++ticks < 2)
                {
                    return;
                }
                FinishCleanHide(g, "frame");
            };
            CompositionTarget.Rendering += _renderHook;
            DispatcherTimer safety = new DispatcherTimer(DispatcherPriority.Normal)
            {
                Interval = TimeSpan.FromMilliseconds(250.0)
            };
            safety.Tick += delegate
            {
                FinishCleanHide(g, "fallback");
            };
            _closeFallback = safety;
            safety.Start();
        }
        else
        {
            _hidePending = false;
            if (!_closed)
            {
                Hide();
            }
            _root.Opacity = 1.0;
        }
        RaiseOpen(false);
        _openDevice = null;
    }

    private void FinishCleanHide(int g, string by)
    {
        UnhookRender();
        StopCloseFallback();
        if (_hidePending && g == _gen)
        {
            _hidePending = false;
            if (!_closed)
            {
                Hide();
            }
            _root.Opacity = 1.0;
            if (by != "frame")
            {
                Trace("Win7 Start: hide completed by " + by);
            }
        }
    }

    private void UnhookRender()
    {
        if (_renderHook != null)
        {
            CompositionTarget.Rendering -= _renderHook;
            _renderHook = null;
        }
    }

    private void StopCloseFallback()
    {
        if (_closeFallback != null)
        {
            _closeFallback.Stop();
            _closeFallback = null;
        }
    }

    private void RaiseOpen(bool open)
    {
        if (_openRaised == open)
        {
            return;
        }
        _openRaised = open;
        try
        {
            OpenChanged?.Invoke(open);
        }
        catch (Exception ex)
        {
            Trace("Win7 Start: OpenChanged handler failed: " + ex.Message);
        }
    }

    private void OnDeactivated(object? sender, EventArgs e)
    {
        if (_qa || _hidePending || _childMenuOpen)
        {
            return;   // a child menu re-checks when it closes (OnChildClosed)
        }
        if (_showing)
        {
            Trace("Win7 Start: deactivate ignored (show in progress)");
            return;
        }
        // Decide one Input turn later, after any re-activation already sent to us: a bounce that leaves us active is
        // ignored, a real switch to another window still closes the menu.
        Dispatcher.BeginInvoke(DispatcherPriority.Input, (Action)delegate
        {
            if (IsVisible && !IsActive && !_childMenuOpen && !_dismissing && !_hidePending)
            {
                Dismiss("Deactivated fg=" + WindowUtil.ForegroundDescription());
            }
        });
    }

    // Light dismiss from the global mouse hook (App marshals it to this thread). x,y are physical pixels; downTime is
    // the hook event's own timestamp (GetTickCount clock), so a press that happened before this show never closes it.
    internal void CloseOnOutsideClick(int x, int y, int downTime, bool leftButton)
    {
        if (_qa || !IsVisible || _hidePending)
        {
            return;
        }
        if (unchecked(downTime - (int)_shownTick) <= 0)
        {
            return;
        }
        if (Environment.TickCount64 - _shownTick < 350)
        {
            return;   // backstop grace; the timestamp check above is the deterministic guard
        }
        if (leftButton && IsStartButtonAt?.Invoke(x, y) == true)
        {
            return;   // the toggle owns Start-button presses
        }
        try
        {
            ContextMenu? child = _openChild;
            if (child != null && child.IsOpen)
            {
                if (PresentationSource.FromVisual(child) is HwndSource cs && GetWindowRect(cs.Handle, out NativeRect cr) && Contains(cr, x, y))
                {
                    return;   // inside the open child menu
                }
            }
            else if (unchecked(downTime - _childClosedTick) <= 0 && _childRectPx.Contains(x, y))
            {
                return;   // pressed inside the child menu while it was still open (it closed since)
            }
            nint hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd != IntPtr.Zero && GetWindowRect(hwnd, out NativeRect r) && InsideMenu(r, x, y))
            {
                return;   // inside the glass or the picture frame
            }
            CloseChildMenu();
            Dismiss($"outside-click at ({x},{y}) lag={unchecked(Environment.TickCount - downTime)}ms");
        }
        catch (Exception ex)
        {
            Trace("Win7 Start: outside-click dismiss failed: " + ex.Message);
        }
    }

    private static bool Contains(NativeRect r, int x, int y)
    {
        return x >= r.Left && x < r.Right && y >= r.Top && y < r.Bottom;
    }

    // The transparent overhang above the glass and the shadow band on its right count as outside; the picture frame
    // that sticks out into the overhang counts as inside.
    private bool InsideMenu(NativeRect r, int x, int y)
    {
        double s = _placedScale > 0.0 ? _placedScale : 1.0;
        int ov = (int)Math.Round(Overhang * s, MidpointRounding.AwayFromZero);
        int sh = (int)Math.Round(ShadowBand * s, MidpointRounding.AwayFromZero);
        if (x >= r.Left && x < r.Right - sh && y >= r.Top + ov && y < r.Bottom)
        {
            return true;
        }
        int picLeft = r.Left + (int)Math.Round((Rx + Math.Round((_rw - 64.0) / 2.0, MidpointRounding.AwayFromZero)) * s, MidpointRounding.AwayFromZero);
        int picSize = (int)Math.Round(64.0 * s, MidpointRounding.AwayFromZero);
        return x >= picLeft && x < picLeft + picSize && y >= r.Top && y < r.Top + picSize;
    }

    // The first program row the list currently shows: the list is the single source of truth for Enter.
    private AppEntry? FirstProgramRow()
    {
        foreach (UIElement child in _progList.Children)
        {
            if (child is FrameworkElement fe && fe.Tag is AppEntry a)
            {
                return a;
            }
        }
        return null;
    }

    private void LaunchProgram(AppEntry a, bool asAdmin)
    {
        LaunchAndClose((asAdmin ? "admin " : string.Empty) + a.Name, delegate
        {
            LaunchCore(a, asAdmin);
        });
    }

    // Starts a program. One the user removed from the recent list comes back once it is launched from this menu.
    private void LaunchCore(AppEntry a, bool asAdmin)
    {
        _launch(a, asAdmin);
        UnhideOnLaunch(a.LaunchPath);
    }

    // Grant the foreground while this window still owns it, close instantly, then run the action.
    private void LaunchAndClose(string what, Action act)
    {
        if (!_qa)
        {
            ShellLaunch.AllowForeground();
        }
        Dismiss("launch " + what, instant: true);
        try
        {
            act();
        }
        catch (Exception ex)
        {
            Trace("Win7 Start: launch " + what + " failed: " + ex.Message);
        }
    }

    // Every shell side effect goes through here: the QA harness records it instead of performing it.
    private void Act(string what, Action real)
    {
        if (_qa)
        {
            _qaActions.Add(what);
            return;
        }
        try
        {
            real();
        }
        catch (Exception ex)
        {
            Trace("Win7 Start: " + what + " failed: " + ex.Message);
        }
    }

    private void Trace(string line)
    {
        Logger.Log(line);
        if (_qa)
        {
            _qaTrace.Add(line);
        }
    }

    private static void OpenShell(string shellPath)
    {
        FileShell.Open(shellPath);
    }

    // Runs a program through the shell on a launch worker, never on the UI thread.
    private static void RunShell(string file, string? args)
    {
        ShellLaunch.Run(delegate
        {
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo(file)
                {
                    UseShellExecute = true
                };
                if (!string.IsNullOrEmpty(args))
                {
                    psi.Arguments = args;
                }
                Process.Start(psi)?.Dispose();
            }
            catch (Exception ex)
            {
                Logger.Log("Win7Start run: " + ex.Message);
            }
        });
    }

    // Help and Support: the Get Help app when its protocol is registered, otherwise the support site.
    private static string HelpTarget()
    {
        try
        {
            using RegistryKey? key = Registry.ClassesRoot.OpenSubKey("ms-contact-support");
            if (key != null)
            {
                return "ms-contact-support:";
            }
        }
        catch
        {
        }
        return "https://support.microsoft.com/windows";
    }

    // Both branches start the process on a launch worker, never on the UI thread.
    private static void OpenHelp(string target)
    {
        if (target.StartsWith("ms-contact-support:", StringComparison.OrdinalIgnoreCase))
        {
            FileShell.Open(target);
        }
        else
        {
            ShellLaunch.Run(delegate
            {
                WebOpen.Url(target);
            });
        }
    }

    // Alt+F4 (or any other close request) only dismisses: the window is reused. DisposeMenu is the only real close.
    protected override void OnClosing(CancelEventArgs e)
    {
        if (AllowClose)
        {
            base.OnClosing(e);
            return;
        }
        e.Cancel = true;
        Dismiss("close request", instant: true);
    }

    internal void DisposeMenu()
    {
        Dismiss("disposed", instant: true);
        AllowClose = true;
        Close();
    }

    protected override void OnClosed(EventArgs e)
    {
        _closed = true;
        UnhookRender();
        StopCloseFallback();
        _hidePending = false;
        UnsubscribeStatics();
        base.OnClosed(e);
    }

    // Static events hold the menu alive, so each += is counted here and undone in UnsubscribeStatics (OnClosed).
    // While the menu is hidden the handlers only set flags; the work happens on the next show.
    private void SubscribeStatics()
    {
        if (_staticsHooked)
        {
            return;
        }
        _staticsHooked = true;
        ShellTheme.Changed += OnThemeSignal;
        _staticSubs++;
        TaskbarWindow.AccentChanged += OnAccentSignal;
        _staticSubs++;
        W7Icons.Loaded += OnIconLoaded;
        _staticSubs++;
        StartScreen.AppsReloaded += OnAppsReloaded;
        _staticSubs++;
        SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
        _staticSubs++;
    }

    // Removes every static-event subscription this menu made, one -= per +=.
    private void UnsubscribeStatics()
    {
        if (!_staticsHooked)
        {
            return;
        }
        _staticsHooked = false;
        ShellTheme.Changed -= OnThemeSignal;
        _staticSubs--;
        TaskbarWindow.AccentChanged -= OnAccentSignal;
        _staticSubs--;
        W7Icons.Loaded -= OnIconLoaded;
        _staticSubs--;
        StartScreen.AppsReloaded -= OnAppsReloaded;
        _staticSubs--;
        SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
        _staticSubs--;
    }

    // A monitor was added, removed or changed resolution: an open menu would sit in the wrong place, so it closes at once.
    // The harness ignores the real event (the user's displays must not interfere with a run); it calls the core instead.
    private void OnDisplaySettingsChanged(object? sender, EventArgs e)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke((Action)delegate
            {
                OnDisplaySettingsChanged(sender, e);
            });
            return;
        }
        if (_qa || _closed)
        {
            return;
        }
        DismissForDisplayChange();
    }

    private void DismissForDisplayChange()
    {
        if (IsVisible && !_hidePending)
        {
            Dismiss("display change", instant: true);
        }
    }

    // ---- Account name and picture ------------------------------------------------------------------------------------
    // Loaded once per process through UserAccount.LoadAsync (never in QA) and applied on the UI thread. A local account
    // may legitimately report the same text as its account id.
    private static Task? s_accountLoad;

    private static string? s_accountName;

    private static ImageSource? s_accountPicture;

    // The account picture box inside the frame; the picture is decoded at this size x scale, at least 96 px.
    private const int PictureDip = 48;

    private ImageSource? _appliedPicture;

    private bool _pictureApplied;

    private void EnsureAccount()
    {
        if (_qa)
        {
            ApplyAccount(_qaName ?? "User", _qaPicture);
            return;
        }
        if (s_accountLoad == null)
        {
            s_accountLoad = LoadAccountAsync();
        }
        ApplyAccount(s_accountName ?? SafeUserName(), s_accountPicture);
    }

    private async Task LoadAccountAsync()
    {
        try
        {
            (ImageSource? pic, string name) = await UserAccount.LoadAsync();
            s_accountName = string.IsNullOrWhiteSpace(name) ? null : name;
            s_accountPicture = pic;
        }
        catch (Exception ex)
        {
            Trace("Win7 Start: account load failed: " + ex.Message);
        }
        if (!_closed)
        {
            await Dispatcher.InvokeAsync(delegate
            {
                if (!_closed && !_qa)
                {
                    ApplyAccount(s_accountName ?? SafeUserName(), s_accountPicture);
                }
            });
        }
    }

    private void ApplyAccount(string name, ImageSource? picture)
    {
        _nameRow.SetText(name);
        if (_pictureApplied && ReferenceEquals(picture, _appliedPicture))
        {
            return;
        }
        _pictureApplied = true;
        _appliedPicture = picture;
        if (picture != null)
        {
            int px = Math.Max(96, (int)Math.Round(PictureDip * _iconScale, MidpointRounding.AwayFromZero));
            ImageBrush brush = new ImageBrush(W7Icons.Fit(picture, px))
            {
                Stretch = Stretch.UniformToFill
            };
            brush.Freeze();
            _picFill.Background = brush;
            _picSilhouette.Visibility = Visibility.Collapsed;
        }
        else
        {
            _picFill.SetResourceReference(Border.BackgroundProperty, "W7.PicFallbackBg");
            _picSilhouette.Visibility = Visibility.Visible;
        }
    }

    private const uint SWP_NOSIZE = 0x0001;

    private const uint SWP_NOZORDER = 0x0004;

    private const uint SWP_NOACTIVATE = 0x0010;

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;

        public int Top;

        public int Right;

        public int Bottom;
    }

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(nint hWnd, out NativeRect lpRect);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(nint hWnd, nint hWndInsertAfter, int x, int y, int cx, int cy, uint flags);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;

        public int Y;
    }

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out NativePoint lpPoint);

    // The pointer in screen pixels, or null when it cannot be read. The harness injects its own (_qaCursor).
    private (int X, int Y)? CursorNow()
    {
        if (_qa)
        {
            return _qaCursor;
        }
        try
        {
            return GetCursorPos(out NativePoint p) ? (p.X, p.Y) : null;
        }
        catch
        {
            return null;
        }
    }
}
