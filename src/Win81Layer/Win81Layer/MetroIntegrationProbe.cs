#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace Win81Layer;

// Runs only from App's early diagnostic branch. No Show, input, consent, or shell commands.
internal static class MetroIntegrationProbe
{
    private const BindingFlags InstancePrivate = BindingFlags.Instance | BindingFlags.NonPublic;
    private const BindingFlags StaticAny = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
    private static int _begun;
    private sealed record Check(string Name, bool Passed, string Detail);
    private sealed record Fingerprint(string Path, long Length, string Sha256, long LastWriteUtcTicks);
    private sealed record ControlAudit(string Scene, string Type, string Name, string Style, bool LocalTemplate,
        bool KeyboardFocusable, bool HasFocusStyle, double Width, double Height);
    private sealed record ImageAudit(string File, int Width, int Height, string Sha256, long InkPixels, long RightInkPixels);

    public static void Begin(Application app)
    {
        ArgumentNullException.ThrowIfNull(app);
        app.Dispatcher.VerifyAccess();
        if (!SettingsStore.ReadOnlyDiagnostics || !Environment.GetCommandLineArgs().Contains("--metro-integration-test"))
            throw new InvalidOperationException("Use only the early --metro-integration-test startup branch.");
        if (Interlocked.Exchange(ref _begun, 1) != 0) return;
        app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        app.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() => Run(app)));
    }

    private static void Run(Application app)
    {
        List<Check> checks = new();
        List<string> errors = new();
        List<ControlAudit> controls = new();
        List<ImageAudit> images = new();
        List<Window> windows = new();
        List<Fingerprint> before = new(), after = new();
        object? chrome = null;
        string output = "", dllHash = "";
        string dll = typeof(MetroIntegrationProbe).Assembly.Location;
        bool capturedBefore = false;
        DateTime started = DateTime.UtcNow;
        try
        {
            output = OutputDirectory();
            Directory.CreateDirectory(output);
            dllHash = Hash(dll);
            before = StateFingerprints(); capturedBefore = true;
            chrome = CompareChromeTrees();
            Require(app.Windows.Count == 0, "Probe must run before any launcher windows exist.");
            // These opt-in markers are supplied only after each owner audits its read-only branches.
            // Missing support fails before constructors can reach recovery, migration, or logger I/O.
            foreach (Type type in new[] { typeof(DurableStateStore), typeof(ShellProfileManager), typeof(Logger) })
            {
                PropertyInfo? supported = type.GetProperty("ReadOnlyDiagnosticsSupported", StaticAny);
                Require(supported != null && supported.PropertyType == typeof(bool) && Equals(supported.GetValue(null), true),
                    type.Name + " needs its audited ReadOnlyDiagnosticsSupported marker before this probe can run.");
            }

            // Never pump queued Loaded handlers, entity debounces, or other dispatcher work between construction and cleanup.
            using (app.Dispatcher.DisableProcessing())
            {
                try
                {
                    StartScreen start = new(transitionDiagnostics: true); windows.Add(start);
                    bool metro = !ShellSkin.GlassOn;
                    checks.Add(new("Metro profile active", metro, "No persisted profile or theme setting is changed by QA."));
                    PcSettingsWindow pc = new(start); windows.Add(pc);
                    Invoke(pc, "Nav", "Launcher");
                    FrameworkElement pcRoot = (FrameworkElement)pc.Content;
                    StopAnimations(pcRoot);
                    SaveScene(pcRoot, "pc-launcher-1024", 1024, 768, output, images, checks);
                    Audit(pcRoot, "pc-launcher-1024", controls);
                    SaveScene(pcRoot, "pc-launcher-normal", 1366, 768, output, images, checks);
                    Audit(pcRoot, "pc-launcher-normal", controls);
                    CheckLauncher(pcRoot, checks, metro);

                    // QaRender calls Show(). Build the same real Launcher page and keep WPF resource inheritance instead.
                    ScrollViewer tall = (ScrollViewer)Invoke(pc, "BuildPage", "Launcher")!;
                    pc.Content = tall;
                    tall.Background = Brushes.White;
                    tall.VerticalScrollBarVisibility = ScrollBarVisibility.Disabled;
                    tall.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
                    tall.Measure(new Size(900, double.PositiveInfinity));
                    double naturalHeight = tall.DesiredSize.Height;
                    double tallHeight = Math.Clamp(Math.Ceiling(naturalHeight), 400, 12000);
                    checks.Add(new("Launcher tall body within raster limit", naturalHeight <= 12000, naturalHeight.ToString("F0")));
                    SaveScene(tall, "pc-launcher-tall", 900, (int)tallHeight, output, images, checks);
                    Audit(tall, "pc-launcher-tall", controls);

                    SearchPane search = new(() => Array.Empty<AppEntry>(), _ => throw new InvalidOperationException("QA must not launch an entry."));
                    windows.Add(search);
                    FieldInfo scope = Field(search, "_scope");
                    scope.SetValue(search, Enum.Parse(scope.FieldType, "Places"));
                    ((TextBlock)Field(search, "_scopeLabel").GetValue(search)!).Text = "Places";
                    string searchPath = Path.Combine(output, "search-places.png");
                    try { search.QaRender(searchPath, "Paris", 920); }
                    finally { StopTimers(search); }
                    FrameworkElement searchRoot = (FrameworkElement)search.Content;
                    // Measure the helper's actual output, not a replacement sample constructed by this probe.
                    ImageAudit searchImage = InspectImage(searchPath); images.Add(searchImage);
                    checks.Add(new("Search nonblank pixels", searchImage.InkPixels > 100, "Real helper PNG."));
                    Audit(searchRoot, "search-places", controls);
                    TextBox searchBox = Tree(searchRoot).OfType<TextBox>().Single();
                    checks.Add(new("Search input canonical style", StyleKey(searchBox) == "Metro81.TextBox" && searchBox.ReadLocalValue(Control.TemplateProperty) == DependencyProperty.UnsetValue,
                        "Old local search templates remain visible in the audit, not overridden."));
                    checks.Add(new("Search input accessibility", AccessibleName(searchBox).Length > 0 && searchBox.Focusable && searchBox.FocusVisualStyle != null,
                        "Managed name and keyboard focus treatment, no native focus claim."));
                    checks.Add(new("Places query and action rendered", Tree(searchRoot).OfType<TextBlock>().Any(t => t.Text == "Places") &&
                        Tree(searchRoot).OfType<TextBlock>().Any(t => t.Text.Contains("Explore", StringComparison.Ordinal) && t.Text.Contains("Paris", StringComparison.Ordinal)), "Real SearchPane scope and result builder; sample place card is offline QA content."));
                    checks.Add(new("Search debounce stopped before dispatch", !Timers(search).Any(t => t.IsEnabled), "No network lookup callback was dispatched."));

                    RenderStart(start, output, images, controls, checks);
                    foreach (Window window in windows)
                        checks.Add(new(window.GetType().Name + " has no native window", !window.IsVisible && new WindowInteropHelper(window).Handle == IntPtr.Zero,
                            "No Show or EnsureHandle call."));
                }
                finally
                {
                    foreach (Window window in windows.Concat(app.Windows.Cast<Window>()).Distinct().Reverse().ToArray())
                    {
                        try
                        {
                            try
                            {
                                StopTimers(window);
                                if (window.Content is FrameworkElement content) StopAnimations(content);
                            }
                            finally
                            {
                                window.Content = null;
                                try { window.Close(); }
                                finally { foreach (CancellationTokenSource cancellation in Cancellations(window)) cancellation.Dispose(); }
                            }
                            checks.Add(new(window.GetType().Name + " timers stopped", !Timers(window).Any(t => t.IsEnabled), "Cleanup completed before dispatcher processing resumed."));
                        }
                        catch (Exception ex) { errors.Add("Cleanup " + window.GetType().Name + ": " + ex.GetType().Name + ": " + ex.Message); }
                    }
                    checks.Add(new("Detached windows closed", app.Windows.Count == 0, "No diagnostic window retained in Application.Windows."));
                }
            }
        }
        catch (Exception ex) { errors.Add(ex.GetType().Name + ": " + ex.Message); }
        finally
        {
            if (capturedBefore)
            {
                try
                {
                    after = StateFingerprints();
                    checks.Add(new("User state fingerprints unchanged", before.SequenceEqual(after),
                        "SHA256, length, last-write time, additions and removals; concurrent launcher changes cannot be attributed to this process."));
                }
                catch (Exception ex) { errors.Add("State fingerprint: " + ex.GetType().Name + ": " + ex.Message); }
            }
            bool passed = capturedBefore && errors.Count == 0 && checks.Count > 0 && checks.All(c => c.Passed);
            try
            {
                if (output.Length != 0)
                    File.WriteAllText(Path.Combine(output, "report.json"), JsonSerializer.Serialize(new
                    {
                        Passed = passed, StartedUtc = started, FinishedUtc = DateTime.UtcNow,
                        Assembly = new { Path = dll, Sha256 = dllHash, Version = typeof(MetroIntegrationProbe).Assembly.GetName().Version?.ToString(), ModuleVersionId = typeof(MetroIntegrationProbe).Module.ModuleVersionId },
                        Checks = checks, Errors = errors, Controls = controls, Images = images,
                        StateBefore = before, StateAfter = after, ChromeComparison = chrome,
                        Limits = new[] {
                            "Detached real WPF trees, no HWND, native focus, pointer delivery, shell startup or user consent.",
                            "Search place card comes from SearchPane.QaRender's synthetic offline sample; no live Places/API validation.",
                            "Launcher reads existing settings, profile status, registry and a read-only schtasks query; controls are never invoked.",
                            "Aero profile is not overridden; a non-Metro profile is reported as a failed Metro precondition.",
                            "No Google deferred-result integration test is executed by this probe.",
                            "State checks end before App.OnExit; the early diagnostic shutdown guard is required." }
                    }, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch { passed = false; }
            app.Shutdown(passed ? 0 : 2);
        }
    }

    private static void RenderStart(StartScreen start, string output, List<ImageAudit> images, List<ControlAudit> controls, List<Check> checks)
    {
        // A two-tile selection builds the real appbar without consulting or migrating TaskbarPins.
        List<TileVm> selection = Enumerable.Range(0, 2).Select(i => new TileVm
        { Entry = new AppEntry { Name = "QA selection " + i, LaunchPath = "qa:selection:" + i, TileBrush = Brushes.DodgerBlue }, Size = TileSize.Medium }).ToList();
        Invoke(start, "BuildTileAppBarCommands", selection);
        Border bar = (Border)start.FindName("TileAppBar");
        bar.Visibility = Visibility.Visible;
        if (bar.RenderTransform is TranslateTransform slide) { slide.BeginAnimation(TranslateTransform.YProperty, null); slide.Y = 0; }
        FrameworkElement root = (FrameworkElement)start.Content;
        SaveScene(root, "start-appbar", 1366, 768, output, images, checks);
        Audit(bar, "start-appbar", controls);
        Button[] commands = Tree(bar).OfType<Button>().Where(b => b.TemplatedParent == null).ToArray();
        checks.Add(new("Start appbar canonical commands", commands.Length >= 5 && commands.All(b => StyleKey(b) == "Metro81.AppBarButton" &&
            b.ReadLocalValue(Control.TemplateProperty) == DependencyProperty.UnsetValue), "Real BuildTileAppBarCommands, no command activation."));
        checks.Add(new("Start appbar accessible labels and focus style", commands.Length > 0 && commands.All(b => AccessibleName(b).Length > 0 && b.FocusVisualStyle != null), "Managed automation names only; no native focus test."));

        ScrollBar scroll = (ScrollBar)start.FindName("BottomScroll");
        scroll.Visibility = Visibility.Visible;
        scroll.BeginAnimation(UIElement.OpacityProperty, null); scroll.Opacity = 1;
        scroll.Minimum = 0; scroll.Maximum = 1000; scroll.ViewportSize = 500; scroll.Value = 250;
        // Render the actual scrollbar in isolation, keeping the original style and constructor template.
        scroll.Measure(new Size(1000, 24)); scroll.Arrange(new Rect(0, 0, 1000, 24)); scroll.UpdateLayout();
        SaveScene(scroll, "start-scrollbar", 1000, 24, output, images, checks);
        Audit(scroll, "start-scrollbar", controls);
        checks.Add(new("Start scrollbar uses canonical template", StyleKey(scroll) == "Metro81.ScrollBar" &&
            scroll.ReadLocalValue(Control.TemplateProperty) == DependencyProperty.UnsetValue, "An old code-assigned BottomScroll template is reported, never replaced by the probe."));
        checks.Add(new("Start scrollbar track and thumb realized", scroll.Template?.FindName("PART_Track", scroll) is Track track && track.Thumb is Thumb thumb && thumb.ActualWidth >= 16, "Real template parts, synthetic scroll range; no routed Scroll event."));
        Button zoom = (Button)start.FindName("ZoomOutBtn");
        zoom.Visibility = Visibility.Visible;
        zoom.Measure(new Size(18, 15)); zoom.Arrange(new Rect(0, 0, 18, 15)); zoom.UpdateLayout();
        RepeatButton[] arrows = Tree(scroll).OfType<RepeatButton>().Where(b => b.Style == appStyle("Metro81.ScrollArrow")).ToArray();
        checks.Add(new("Start zoom matches scrollbar buttons", arrows.Length == 2 && arrows.All(a => ReferenceEquals(a.Template, zoom.Template) && a.ActualWidth == zoom.ActualWidth && a.ActualHeight == zoom.ActualHeight), $"Zoom={zoom.ActualWidth}x{zoom.ActualHeight}; scroll={scroll.ActualHeight}; arrows={string.Join(",", arrows.Select(a => $"{a.ActualWidth}x{a.ActualHeight}"))}; shared template required."));
        checks.Add(new("Start zoom reserved edge width", scroll.Margin.Right == zoom.Width && zoom.Height == scroll.Height, "No gap or overlapping hit targets."));
        checks.Add(new("Start zoom canonical accessible button", StyleKey(zoom) == "Metro81.ScrollZoomButton" && AccessibleName(zoom).Length > 0 && zoom.Focusable && zoom.FocusVisualStyle != null, "Existing OnZoomOutButton handler retained."));
        FrameworkElement plus = (FrameworkElement)start.FindName("ZoomVBar");
        plus.Visibility = Visibility.Collapsed;
        scroll.Opacity = 0.35;
        checks.Add(new("Start minus follows rail opacity", Math.Abs(zoom.Opacity - 0.35) < 0.001, "Actual bound opacity, no independent animation clock."));
        plus.Visibility = Visibility.Visible;
        scroll.Opacity = 0;
        checks.Add(new("Start plus remains available to exit zoom", zoom.Opacity == 1, "Zoom return remains visible even without a scrollable rail."));
        plus.Visibility = Visibility.Collapsed;
        checks.Add(new("Start minus hides with rail after zoom", zoom.Opacity == 0, "No isolated opaque minus button."));
        scroll.Opacity = 1;
        Grid strip = new() { Width = 1000, Height = 15 };
        strip.ColumnDefinitions.Add(new ColumnDefinition()); strip.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(18) });
        strip.Children.Add(new Border { Background = new VisualBrush(scroll) });
        Border zoomImage = new() { Background = new VisualBrush(zoom) }; Grid.SetColumn(zoomImage, 1); strip.Children.Add(zoomImage);
        SaveScene(strip, "start-scrollbar-with-zoom", 1000, 15, output, images, checks);

        static Style? appStyle(string key) => Application.Current.TryFindResource(key) as Style;
    }

    private static void CheckLauncher(FrameworkElement root, List<Check> checks, bool metro)
    {
        var controls = Tree(root).OfType<Control>().Where(c => c.TemplatedParent == null).ToArray();
        var toggles = controls.OfType<ToggleButton>().Where(c => c is not CheckBox && c is not RadioButton).ToArray();
        var combos = controls.OfType<ComboBox>().ToArray();
        checks.Add(new("Launcher toggle adoption", metro && toggles.Length >= 10 && toggles.All(c => StyleKey(c) == "Metro81.ToggleSwitch"), "Actual Launcher controls, no diagnostic style substitutions."));
        checks.Add(new("Launcher combo adoption", metro && combos.Length >= 5 && combos.All(c => StyleKey(c) == "Metro81.ComboBox"), "Includes live-tile settings controls."));
        checks.Add(new("Launcher toggle accessibility", toggles.Length > 0 && toggles.All(c => AccessibleName(c).Length > 0 && c.Focusable && c.FocusVisualStyle != null), "Name, keyboard focusability and shared focus treatment."));
        checks.Add(new("Launcher scrollbar adoption", Tree(root).OfType<ScrollBar>().Any(c => StyleKey(c) == "Metro81.ScrollBar" && c.Template != null), "Real ScrollViewer scrollbar."));
    }

    private static void SaveScene(FrameworkElement root, string name, int width, int height, string output, List<ImageAudit> images, List<Check> checks)
    {
        root.ApplyTemplate(); root.Measure(new Size(width, height)); root.Arrange(new Rect(0, 0, width, height)); root.UpdateLayout();
        RenderTargetBitmap bitmap = new(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(root);
        string path = Path.Combine(output, name + ".png");
        PngBitmapEncoder encoder = new(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using (FileStream stream = File.Create(path)) encoder.Save(stream);
        ImageAudit audit = InspectImage(path); images.Add(audit);
        checks.Add(new(name + " nonblank pixels", audit.InkPixels > 32, "Opaque nonwhite pixels=" + audit.InkPixels));
        if (name.StartsWith("pc-launcher-", StringComparison.Ordinal))
            checks.Add(new(name + " right-side content pixels", audit.RightInkPixels > 100, "Reject false-white detached ScrollViewer renders; inspect PNG too."));
    }

    private static ImageAudit InspectImage(string path)
    {
        BitmapSource bitmap;
        using (FileStream stream = File.OpenRead(path)) bitmap = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad).Frames[0];
        FormatConvertedBitmap converted = new(bitmap, PixelFormats.Bgra32, null, 0);
        int stride = converted.PixelWidth * 4;
        byte[] pixels = new byte[stride * converted.PixelHeight]; converted.CopyPixels(pixels, stride, 0);
        long ink = 0, rightInk = 0;
        for (int y = 0; y < converted.PixelHeight; y++)
            for (int x = 0; x < converted.PixelWidth; x++)
            {
                int i = y * stride + x * 4;
                if (pixels[i + 3] > 128 && (pixels[i] < 200 || pixels[i + 1] < 200 || pixels[i + 2] < 200))
                {
                    ink++;
                    if (x >= converted.PixelWidth / 2 && x < converted.PixelWidth - 48 && y > 32 && y < converted.PixelHeight - 24) rightInk++;
                }
            }
        return new(Path.GetFileName(path), converted.PixelWidth, converted.PixelHeight, Hash(path), ink, rightInk);
    }

    private static void Audit(FrameworkElement root, string scene, List<ControlAudit> result)
    {
        foreach (Control c in Tree(root).OfType<Control>().Where(c => c is ButtonBase or TextBox or ComboBox or ScrollBar).Where(c => c.TemplatedParent == null || c is ScrollBar))
            result.Add(new(scene, c.GetType().Name, AccessibleName(c), StyleKey(c), c.ReadLocalValue(Control.TemplateProperty) != DependencyProperty.UnsetValue,
                c.Focusable, c.FocusVisualStyle != null, c.ActualWidth, c.ActualHeight));
    }

    private static string AccessibleName(FrameworkElement element)
    {
        try { return UIElementAutomationPeer.CreatePeerForElement(element)?.GetName() ?? AutomationProperties.GetName(element); }
        catch { return AutomationProperties.GetName(element); }
    }

    private static string StyleKey(FrameworkElement element)
    {
        foreach (string name in new[] { "AppBarButton", "AccentButton", "CommandButton", "ToggleSwitch", "ComboBox", "TextBox", "ScrollBar", "ScrollZoomButton", "Button", "NavButton", "LinkButton" })
        {
            if (element.TryFindResource("Metro81." + name) is not Style canonical) continue;
            for (Style? style = element.Style; style != null; style = style.BasedOn)
                if (ReferenceEquals(style, canonical)) return "Metro81." + name;
        }
        return "not-canonical";
    }

    private static IEnumerable<DependencyObject> Tree(DependencyObject root)
    {
        HashSet<DependencyObject> seen = new(); Stack<DependencyObject> pending = new(); pending.Push(root);
        while (pending.Count > 0)
        {
            DependencyObject next = pending.Pop(); if (!seen.Add(next)) continue; yield return next;
            foreach (object child in LogicalTreeHelper.GetChildren(next)) if (child is DependencyObject dependency) pending.Push(dependency);
            if (next is Visual) for (int i = 0; i < VisualTreeHelper.GetChildrenCount(next); i++) pending.Push(VisualTreeHelper.GetChild(next, i));
        }
    }

    private static IEnumerable<DispatcherTimer> Timers(object owner) => owner.GetType().GetFields(InstancePrivate).Select(f => f.GetValue(owner)).OfType<DispatcherTimer>();
    private static IEnumerable<CancellationTokenSource> Cancellations(object owner) => owner.GetType().GetFields(InstancePrivate).Select(f => f.GetValue(owner)).OfType<CancellationTokenSource>();
    private static void StopTimers(object owner)
    {
        foreach (DispatcherTimer timer in Timers(owner)) timer.Stop();
        foreach (CancellationTokenSource cancellation in Cancellations(owner))
            try { cancellation.Cancel(); } catch (ObjectDisposedException) { }
    }
    private static void StopAnimations(DependencyObject root)
    {
        foreach (DependencyObject element in Tree(root).ToArray())
        {
            if (element is UIElement ui)
            {
                ui.BeginAnimation(UIElement.OpacityProperty, null);
                if (ui.RenderTransform is TranslateTransform { IsFrozen: false } move) { move.BeginAnimation(TranslateTransform.YProperty, null); move.BeginAnimation(TranslateTransform.XProperty, null); }
            }
        }
        // PcSettings.PageHost's navigation transition starts invisible until an animation tick.
        foreach (Border host in Tree(root).OfType<Border>().Where(b => b.GetType().Name == "PageHost"))
            if (host.Child is UIElement child) { child.Opacity = 1; if (child.RenderTransform is TranslateTransform { IsFrozen: false } move) { move.X = 0; move.Y = 0; } }
    }
    private static FieldInfo Field(object target, string name) => target.GetType().GetField(name, InstancePrivate) ?? throw new MissingFieldException(target.GetType().Name, name);
    private static object? Invoke(object target, string name, params object[] arguments) =>
        (target.GetType().GetMethod(name, InstancePrivate) ?? throw new MissingMethodException(target.GetType().Name, name)).Invoke(target, arguments);
    private static void Require(bool value, string error) { if (!value) throw new InvalidOperationException(error); }
    private static string Hash(string path) { using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete); return Convert.ToHexString(SHA256.HashData(stream)); }
    private static string Option(string name, string fallback) => Environment.GetCommandLineArgs().FirstOrDefault(a => a.StartsWith(name + "=", StringComparison.Ordinal))?[(name.Length + 1)..] ?? fallback;
    private static string StateRoot => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Win81Layer");

    private static string OutputDirectory()
    {
        string path = Path.GetFullPath(Option("--metro-integration-output", Path.Combine(Path.GetTempPath(), "Win81MetroIntegration", Guid.NewGuid().ToString("N"))));
        Require(!Inside(path, StateRoot), "Diagnostic output must not be placed under live user state.");
        Require(!Inside(path, SourceChromeRoot) && !Inside(path, ReadyChromeRoot), "Diagnostic output must not modify either ChromeExtension tree.");
        Require(!Directory.Exists(path), "Use a new diagnostic output directory; existing evidence is never overwritten.");
        for (DirectoryInfo? dir = new(path); dir != null; dir = dir.Parent)
            Require(!dir.Exists || (dir.Attributes & FileAttributes.ReparsePoint) == 0, "Diagnostic output must not traverse directory links.");
        return path;
    }
    private static bool Inside(string path, string root) => string.Equals(Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar), Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase) ||
        Path.GetFullPath(path).StartsWith(Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    private static string SourceChromeRoot => Path.GetFullPath(Option("--metro-source-chrome", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "Win81Metro-Source", "ChromeExtension")));
    private static string ReadyChromeRoot => Path.GetFullPath(Option("--metro-ready-chrome", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "Win81Metro-Ready", "ChromeExtension")));

    private static List<Fingerprint> StateFingerprints()
    {
        if (!Directory.Exists(StateRoot)) return new();
        Require((File.GetAttributes(StateRoot) & FileAttributes.ReparsePoint) == 0, "State root must not be a directory link.");
        List<string> paths = Directory.EnumerateFiles(StateRoot).Where(p =>
            Path.GetFileName(p).Contains(".json", StringComparison.OrdinalIgnoreCase) || Path.GetFileName(p).StartsWith("google", StringComparison.OrdinalIgnoreCase)).ToList();
        List<Fingerprint> directories = new() { new("./", 0, "directory", 0) };
        foreach (string name in new[] { "state", "profiles" })
        {
            string dir = Path.Combine(StateRoot, name);
            if (Directory.Exists(dir)) { paths.AddRange(Files(dir)); directories.Add(new(name + "/", 0, "directory", 0)); }
        }
        return directories.Concat(paths.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(p => p, StringComparer.OrdinalIgnoreCase).Select(p =>
        {
            Require((File.GetAttributes(p) & FileAttributes.ReparsePoint) == 0, "State fingerprint refuses linked files.");
            FileInfo info = new(p); long length = info.Length, stamp = info.LastWriteTimeUtc.Ticks;
            string hash = Hash(p); info.Refresh();
            Require(info.Length == length && info.LastWriteTimeUtc.Ticks == stamp, "State changed while hashing: " + Path.GetFileName(p));
            return new Fingerprint(Path.GetRelativePath(StateRoot, p), length, hash, stamp);
        })).ToList();
    }

    private static IEnumerable<string> Files(string root)
    {
        Require((File.GetAttributes(root) & FileAttributes.ReparsePoint) == 0, "Fingerprint root must not be a directory link.");
        Stack<string> pending = new(); pending.Push(root); int count = 0;
        while (pending.Count > 0)
            foreach (string path in Directory.EnumerateFileSystemEntries(pending.Pop()))
            {
                FileAttributes attributes = File.GetAttributes(path);
                Require((attributes & FileAttributes.ReparsePoint) == 0, "Fingerprint refuses linked entries: " + Path.GetFileName(path));
                if ((attributes & FileAttributes.Directory) != 0) pending.Push(path);
                else { Require(++count <= 20000, "Fingerprint file-count limit exceeded."); yield return path; }
            }
    }

    private static object CompareChromeTrees()
    {
        string source = SourceChromeRoot, ready = ReadyChromeRoot;
        Require(Directory.Exists(source) && Directory.Exists(ready), "Both ChromeExtension roots must exist; pass explicit paths when not under Desktop.");
        Dictionary<string, string> Hashes(string root) => Files(root).ToDictionary(p => Path.GetRelativePath(root, p), Hash, StringComparer.OrdinalIgnoreCase);
        var left = Hashes(source); var right = Hashes(ready);
        var differences = left.Keys.Union(right.Keys, StringComparer.OrdinalIgnoreCase).OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
            .Where(p => !left.TryGetValue(p, out string? l) || !right.TryGetValue(p, out string? r) || l != r)
            .Select(p => new { Path = p, SourceSha256 = left.GetValueOrDefault(p), ReadySha256 = right.GetValueOrDefault(p),
                Status = !left.ContainsKey(p) ? "ready-only" : !right.ContainsKey(p) ? "source-only" : "different" }).ToArray();
        return new { Source = source, Ready = ready, SourceFileCount = left.Count, ReadyFileCount = right.Count, Equal = differences.Length == 0, Differences = differences };
    }
}
