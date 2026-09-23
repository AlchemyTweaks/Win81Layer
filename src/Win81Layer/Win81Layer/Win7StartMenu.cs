using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;

namespace Win81Layer;

// The authentic Windows 7 "orb" Start menu — an OPT-IN alternative to the Metro Start screen, gated SOLELY by
// AppSettings.Win7StartMenuEnabled and routed from App.ToggleStart(). It is DELIBERATELY INDEPENDENT of the
// desktop-composition mode / ShellSkin: the Metro 8.1 Start stays the permanent default and this menu must NEVER be
// auto-driven by composition. Classic two-pane layout: white program list on the left (most-used +
// All Programs + search), a translucent glass "places" pane on the right (account, Documents/Pictures/Music/
// Computer/Control Panel/... and a Shut down split button). Plain WPF alpha only — no WCA, no full-screen
// transparency (small anchored window, safe on multi-monitor).
public sealed class Win7StartMenu : Window
{
    private readonly Func<List<AppEntry>> _appsProvider;

    private readonly Action<AppEntry> _launch;

    private StackPanel _progList = null!;

    private ScrollViewer _progScroll = null!;

    private TextBox _search = null!;

    private bool _childMenuOpen;   // any child menu (power submenu OR an item right-click menu) owns activation; keep Start open

    private TextBlock _allProgramsLabel = null!;

    private bool _allMode;

    private bool _dismissing;

    public Win7StartMenu(Func<List<AppEntry>> appsProvider, Action<AppEntry> launch)
    {
        _appsProvider = appsProvider;
        _launch = launch;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Topmost = true;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Width = 540.0;
        Height = 620.0;
        Title = "Start";
        Deactivated += delegate
        {
            if (!_childMenuOpen) Dismiss();
        };
        PreviewKeyDown += OnKey;
        Content = BuildRoot();
    }

    private void OnKey(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Dismiss();
        }
    }

    private static Color Accent()
    {
        try
        {
            return StartAccent.Color();
        }
        catch
        {
            return Color.FromRgb(58, 110, 165);
        }
    }

    private UIElement BuildRoot()
    {
        // Aero glass frame: rounded top, translucent dark glass tinted with the accent, thin light border.
        Color acc = Accent();
        LinearGradientBrush glass = new LinearGradientBrush
        {
            StartPoint = new Point(0.0, 0.0),
            EndPoint = new Point(0.0, 1.0)
        };
        glass.GradientStops.Add(new GradientStop(Color.FromArgb(0xF2, (byte)(acc.R / 5), (byte)(acc.G / 5 + 6), (byte)(acc.B / 4 + 10)), 0.0));
        glass.GradientStops.Add(new GradientStop(Color.FromArgb(0xF6, 12, 16, 22), 1.0));
        glass.Freeze();
        Border frame = new Border
        {
            Background = glass,
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x80, byte.MaxValue, byte.MaxValue, byte.MaxValue)),
            BorderThickness = new Thickness(1.0),
            CornerRadius = new CornerRadius(8.0, 8.0, 0.0, 0.0),
            SnapsToDevicePixels = true
        };
        Grid grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(330.0) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.0, GridUnitType.Star) });

        // LEFT: white program pane + search
        Grid left = new Grid { Margin = new Thickness(6.0, 6.0, 0.0, 6.0) };
        left.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1.0, GridUnitType.Star) });
        left.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        left.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Border leftBg = new Border
        {
            Background = Brushes.White,
            CornerRadius = new CornerRadius(3.0, 3.0, 3.0, 3.0)
        };
        Grid.SetRowSpan(leftBg, 3);
        left.Children.Add(leftBg);

        _progScroll = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Margin = new Thickness(4.0, 6.0, 4.0, 0.0)
        };
        _progList = new StackPanel();
        _progScroll.Content = _progList;
        Grid.SetRow(_progScroll, 0);
        left.Children.Add(_progScroll);

        // "All Programs" toggle row
        Border allRow = new Border
        {
            Background = Brushes.Transparent,
            Padding = new Thickness(10.0, 7.0, 10.0, 7.0),
            Margin = new Thickness(4.0, 2.0, 4.0, 2.0),
            Cursor = Cursors.Hand
        };
        StackPanel allSp = new StackPanel { Orientation = Orientation.Horizontal };
        allSp.Children.Add(new TextBlock
        {
            Text = "",
            FontFamily = new FontFamily("Segoe MDL2 Assets"),
            FontSize = 11.0,
            Foreground = new SolidColorBrush(Color.FromRgb(60, 90, 140)),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0.0, 0.0, 8.0, 0.0)
        });
        _allProgramsLabel = new TextBlock
        {
            Text = "All Programs",
            FontFamily = new FontFamily("Segoe UI"),
            FontSize = 13.0,
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Color.FromRgb(30, 57, 91)),
            VerticalAlignment = VerticalAlignment.Center
        };
        allSp.Children.Add(_allProgramsLabel);
        allRow.Child = allSp;
        allRow.MouseEnter += delegate { allRow.Background = new SolidColorBrush(Color.FromRgb(0xD6, 0xE6, 0xF7)); };
        allRow.MouseLeave += delegate { allRow.Background = Brushes.Transparent; };
        allRow.MouseLeftButtonUp += delegate { ToggleAllPrograms(); };
        Border allSep = new Border
        {
            Height = 1.0,
            Background = new SolidColorBrush(Color.FromRgb(0xCF, 0xD8, 0xE3)),
            Margin = new Thickness(8.0, 0.0, 8.0, 0.0)
        };
        StackPanel allWrap = new StackPanel();
        allWrap.Children.Add(allSep);
        allWrap.Children.Add(allRow);
        Grid.SetRow(allWrap, 1);
        left.Children.Add(allWrap);

        // Search box
        Border searchBox = new Border
        {
            Background = Brushes.White,
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x7A, 0x99, 0xB8)),
            BorderThickness = new Thickness(1.0),
            CornerRadius = new CornerRadius(2.0),
            Margin = new Thickness(8.0, 4.0, 8.0, 6.0)
        };
        Grid sGrid = new Grid();
        _search = new TextBox
        {
            BorderThickness = new Thickness(0.0),
            Background = Brushes.Transparent,
            FontFamily = new FontFamily("Segoe UI"),
            FontSize = 13.0,
            Padding = new Thickness(6.0, 5.0, 24.0, 5.0),
            VerticalContentAlignment = VerticalAlignment.Center
        };
        TextBlock placeholder = new TextBlock
        {
            Text = "Search programs and files",
            FontFamily = new FontFamily("Segoe UI"),
            FontSize = 13.0,
            Foreground = new SolidColorBrush(Color.FromRgb(0x8A, 0x8A, 0x8A)),
            Margin = new Thickness(6.0, 0.0, 0.0, 0.0),
            VerticalAlignment = VerticalAlignment.Center,
            IsHitTestVisible = false
        };
        _search.TextChanged += delegate
        {
            placeholder.Visibility = (string.IsNullOrEmpty(_search.Text) ? Visibility.Visible : Visibility.Collapsed);
            RefreshList();
        };
        _search.KeyDown += OnSearchEnter;
        TextBlock mag = new TextBlock
        {
            Text = "",
            FontFamily = new FontFamily("Segoe MDL2 Assets"),
            FontSize = 13.0,
            Foreground = new SolidColorBrush(Color.FromRgb(0x5A, 0x7A, 0x9A)),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0.0, 0.0, 8.0, 0.0)
        };
        sGrid.Children.Add(_search);
        sGrid.Children.Add(placeholder);
        sGrid.Children.Add(mag);
        searchBox.Child = sGrid;
        Grid.SetRow(searchBox, 2);
        left.Children.Add(searchBox);

        Grid.SetColumn(left, 0);
        grid.Children.Add(left);

        // RIGHT: glass places pane
        FrameworkElement right = BuildRightPane();
        Grid.SetColumn(right, 1);
        grid.Children.Add(right);

        frame.Child = grid;
        return frame;
    }

    private FrameworkElement BuildRightPane()
    {
        StackPanel right = new StackPanel { Margin = new Thickness(14.0, 14.0, 14.0, 12.0) };
        // Account tile
        Color acc = Accent();
        Border pic = new Border
        {
            Width = 48.0,
            Height = 48.0,
            CornerRadius = new CornerRadius(3.0),
            Background = new SolidColorBrush(ColorMath.Lighten(acc, 0.15)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x66, byte.MaxValue, byte.MaxValue, byte.MaxValue)),
            BorderThickness = new Thickness(1.0),
            HorizontalAlignment = HorizontalAlignment.Center
        };
        pic.Child = new TextBlock
        {
            Text = "",
            FontFamily = new FontFamily("Segoe MDL2 Assets"),
            FontSize = 26.0,
            Foreground = Brushes.White,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        right.Children.Add(pic);
        string user;
        try
        {
            user = Environment.UserName;
        }
        catch
        {
            user = "User";
        }
        TextBlock userTb = new TextBlock
        {
            Text = user,
            FontFamily = new FontFamily("Segoe UI"),
            FontSize = 14.0,
            Foreground = Brushes.White,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0.0, 5.0, 0.0, 10.0),
            Cursor = Cursors.Hand
        };
        userTb.MouseLeftButtonUp += delegate { Dismiss(); OpenShell("shell:UsersFilesFolder"); };
        right.Children.Add(userTb);
        right.Children.Add(Divider());
        right.Children.Add(PlaceLink("Documents", () => OpenShell("shell:Personal")));
        right.Children.Add(PlaceLink("Pictures", () => OpenShell("shell:My Pictures")));
        right.Children.Add(PlaceLink("Music", () => OpenShell("shell:My Music")));
        right.Children.Add(PlaceLink("Recent Items", () => OpenShell("shell:Recent")));
        right.Children.Add(Divider());
        right.Children.Add(PlaceLink("Computer", () => OpenShell("shell:MyComputerFolder")));
        right.Children.Add(PlaceLink("Network", () => OpenShell("shell:NetworkPlacesFolder")));
        right.Children.Add(PlaceLink("Control Panel", () => Run("control.exe", null)));
        right.Children.Add(PlaceLink("Devices and Printers", () => Run("control.exe", "printers")));
        right.Children.Add(PlaceLink("Default Programs", () => Run("control.exe", "/name Microsoft.DefaultPrograms")));
        right.Children.Add(PlaceLink("Help and Support", () => Run("helppane.exe", null)));

        // Shut down split button pinned to the bottom
        DockPanel host = new DockPanel { LastChildFill = false };
        DockPanel.SetDock(right, Dock.Top);
        host.Children.Add(right);
        Border shut = BuildShutDown();
        DockPanel.SetDock(shut, Dock.Bottom);
        host.Children.Add(shut);
        return host;
    }

    private Border Divider()
    {
        return new Border
        {
            Height = 1.0,
            Background = new SolidColorBrush(Color.FromArgb(0x33, byte.MaxValue, byte.MaxValue, byte.MaxValue)),
            Margin = new Thickness(2.0, 6.0, 2.0, 6.0)
        };
    }

    private Border PlaceLink(string text, Action onClick)
    {
        Border b = new Border
        {
            Background = Brushes.Transparent,
            Padding = new Thickness(8.0, 6.0, 8.0, 6.0),
            CornerRadius = new CornerRadius(2.0),
            Cursor = Cursors.Hand
        };
        b.Child = new TextBlock
        {
            Text = text,
            FontFamily = new FontFamily("Segoe UI"),
            FontSize = 13.0,
            Foreground = new SolidColorBrush(Color.FromRgb(0xEA, 0xF2, 0xFB))
        };
        b.MouseEnter += delegate { b.Background = new SolidColorBrush(Color.FromArgb(0x33, byte.MaxValue, byte.MaxValue, byte.MaxValue)); };
        b.MouseLeave += delegate { b.Background = Brushes.Transparent; };
        b.MouseLeftButtonUp += delegate
        {
            Dismiss();
            try
            {
                onClick();
            }
            catch (Exception ex)
            {
                Logger.Log("Win7Start place: " + ex.Message);
            }
        };
        return b;
    }

    private Border BuildShutDown()
    {
        Color acc = Accent();
        Border wrap = new Border
        {
            Margin = new Thickness(2.0, 8.0, 2.0, 0.0),
            Height = 32.0
        };
        Grid g = new Grid();
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.0, GridUnitType.Star) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Button main = new Button
        {
            Content = "Shut down",
            FontFamily = new FontFamily("Segoe UI"),
            FontSize = 13.0,
            Foreground = Brushes.White,
            Cursor = Cursors.Hand,
            FocusVisualStyle = null,
            BorderThickness = new Thickness(1.0),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x66, byte.MaxValue, byte.MaxValue, byte.MaxValue)),
            Background = new SolidColorBrush(ColorMath.Multiply(acc, 0.25))
        };
        main.Click += delegate
        {
            Dismiss();
            PowerActions.ShutDown();
        };
        Grid.SetColumn(main, 0);
        Button chevron = new Button
        {
            Content = "",
            FontFamily = new FontFamily("Segoe MDL2 Assets"),
            FontSize = 10.0,
            Width = 26.0,
            Foreground = Brushes.White,
            Cursor = Cursors.Hand,
            FocusVisualStyle = null,
            BorderThickness = new Thickness(1.0, 1.0, 1.0, 1.0),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x66, byte.MaxValue, byte.MaxValue, byte.MaxValue)),
            Background = new SolidColorBrush(ColorMath.Multiply(acc, 0.25))
        };
        ContextMenu cm = new ContextMenu();
        AddPower(cm, "Sleep", PowerActions.Sleep);
        AddPower(cm, "Restart", PowerActions.Restart);
        AddPower(cm, "Sign out", PowerActions.SignOut);
        AddPower(cm, "Lock", PowerActions.Lock);
        TaskbarContextMenu.ApplyTheme(cm);
        cm.Closed += delegate
        {
            _childMenuOpen = false;
            Topmost = true;
        };
        chevron.Click += delegate
        {
            _childMenuOpen = true;   // keep the Start menu open while its power submenu owns activation
            Topmost = false;
            cm.PlacementTarget = chevron;
            cm.IsOpen = true;
        };
        Grid.SetColumn(chevron, 1);
        g.Children.Add(main);
        g.Children.Add(chevron);
        wrap.Child = g;
        return wrap;
    }

    private void AddPower(ContextMenu cm, string text, Action act)
    {
        MenuItem mi = new MenuItem { Header = text };
        mi.Click += delegate
        {
            Dismiss();
            TaskbarContextMenu.QueueCommand(act);
        };
        cm.Items.Add(mi);
    }

    private void ToggleAllPrograms()
    {
        _allMode = !_allMode;
        _allProgramsLabel.Text = (_allMode ? "Back" : "All Programs");
        RefreshList();
    }

    private void RefreshList()
    {
        _progList.Children.Clear();
        List<AppEntry> apps;
        try
        {
            apps = _appsProvider() ?? new List<AppEntry>();
        }
        catch
        {
            apps = new List<AppEntry>();
        }
        string q = _search.Text?.Trim() ?? string.Empty;
        IEnumerable<AppEntry> show;
        if (!string.IsNullOrEmpty(q))
        {
            show = apps.Where((AppEntry a) => a.Name.Contains(q, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending((AppEntry a) => a.Name.StartsWith(q, StringComparison.OrdinalIgnoreCase))
                .ThenBy((AppEntry a) => a.Name)
                .Take(40);
        }
        else if (_allMode)
        {
            show = apps.OrderBy((AppEntry a) => a.Name, StringComparer.OrdinalIgnoreCase);
        }
        else
        {
            // Authentic Win7 left column: user-pinned programs on top, a divider, then the most-used (MRU) list.
            AppSettings st = SettingsStore.Current;
            HashSet<string> pinPaths = new HashSet<string>(st.Win7StartMenuPins ?? new List<string>(), StringComparer.OrdinalIgnoreCase);
            HashSet<string> hidden = new HashSet<string>(st.Win7StartMenuMruHidden ?? new List<string>(), StringComparer.OrdinalIgnoreCase);
            List<AppEntry> pinned = (st.Win7StartMenuPins ?? new List<string>())
                .Select((string p) => apps.FirstOrDefault((AppEntry a) => string.Equals(a.LaunchPath, p, StringComparison.OrdinalIgnoreCase)))
                .Where((AppEntry? a) => a != null).Select((AppEntry? a) => a!).ToList();
            foreach (AppEntry a in pinned)
            {
                _progList.Children.Add(ProgramRow(a, isMru: false));
            }
            if (pinned.Count > 0)
            {
                _progList.Children.Add(LeftSep());
            }
            IEnumerable<AppEntry> mru = apps
                .Where((AppEntry a) => !pinPaths.Contains(a.LaunchPath) && !hidden.Contains(a.LaunchPath))
                .OrderByDescending((AppEntry a) => UsageStore.Count(a.LaunchPath))
                .ThenByDescending((AppEntry a) => UsageStore.LastUsed(a.LaunchPath))
                .ThenBy((AppEntry a) => a.Name, StringComparer.OrdinalIgnoreCase)
                .Take(10);
            foreach (AppEntry a in mru)
            {
                _progList.Children.Add(ProgramRow(a, isMru: true));
            }
            _progScroll.ScrollToTop();
            return;
        }
        foreach (AppEntry a in show)
        {
            _progList.Children.Add(ProgramRow(a));
        }
        _progScroll.ScrollToTop();
    }

    private Border ProgramRow(AppEntry a, bool isMru = false)
    {
        Border row = new Border
        {
            Background = Brushes.Transparent,
            Padding = new Thickness(8.0, 5.0, 8.0, 5.0),
            Margin = new Thickness(2.0, 1.0, 2.0, 1.0),
            CornerRadius = new CornerRadius(2.0),
            Cursor = Cursors.Hand
        };
        StackPanel sp = new StackPanel { Orientation = Orientation.Horizontal };
        Image icon = new Image
        {
            Width = 24.0,
            Height = 24.0,
            Margin = new Thickness(0.0, 0.0, 10.0, 0.0),
            VerticalAlignment = VerticalAlignment.Center,
            Source = (a.Icon ?? AppInventory.LoadIcon(a.LaunchPath))
        };
        RenderOptions.SetBitmapScalingMode(icon, BitmapScalingMode.HighQuality);
        sp.Children.Add(icon);
        sp.Children.Add(new TextBlock
        {
            Text = a.Name,
            FontFamily = new FontFamily("Segoe UI"),
            FontSize = 13.0,
            Foreground = new SolidColorBrush(Color.FromRgb(0x1A, 0x2A, 0x3A)),
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis
        });
        row.Child = sp;
        row.MouseEnter += delegate { row.Background = new SolidColorBrush(Color.FromRgb(0xD6, 0xE6, 0xF7)); };
        row.MouseLeave += delegate { row.Background = Brushes.Transparent; };
        row.MouseLeftButtonUp += delegate
        {
            Dismiss();
            try
            {
                _launch(a);
            }
            catch (Exception ex)
            {
                Logger.Log("Win7Start launch: " + ex.Message);
            }
        };
        row.MouseRightButtonUp += (s, e) =>
        {
            e.Handled = true;
            ShowItemMenu(a, row, isMru);
        };
        return row;
    }

    private Border LeftSep() => new Border
    {
        Height = 1.0,
        Background = new SolidColorBrush(Color.FromRgb(0xCF, 0xD8, 0xE3)),
        Margin = new Thickness(8.0, 3.0, 8.0, 3.0)
    };

    // Win7 right-click on a program row. Uses the shared _childMenuOpen guard so opening it (which takes
    // activation) does NOT dismiss the Start menu. File-only actions are hidden for packaged (shell:AppsFolder) apps.
    private void ShowItemMenu(AppEntry a, UIElement target, bool isMru)
    {
        bool onDisk = !string.IsNullOrEmpty(a.LaunchPath) && System.IO.File.Exists(a.LaunchPath);
        bool isExe = onDisk && a.LaunchPath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase);
        AppSettings st = SettingsStore.Load();
        bool startPinned = (st.Win7StartMenuPins ?? new List<string>()).Any((string p) => string.Equals(p, a.LaunchPath, StringComparison.OrdinalIgnoreCase));
        bool tbPinned = TaskbarPins.Load().Any((PinnedApp p) => string.Equals(p.LaunchPath, a.LaunchPath, StringComparison.OrdinalIgnoreCase));
        ContextMenu cm = new ContextMenu();
        AddItem(cm, startPinned ? "Unpin from Start Menu" : "Pin to Start Menu", delegate { ToggleStartPin(a.LaunchPath, !startPinned); }, dismiss: false);
        AddItem(cm, tbPinned ? "Unpin from taskbar" : "Pin to taskbar", delegate { ToggleTaskbarPin(a, !tbPinned); }, dismiss: false);
        if (isExe)
        {
            AddItem(cm, "Run as administrator", delegate { LaunchAdmin(a); }, dismiss: true);
        }
        if (onDisk)
        {
            AddItem(cm, "Open file location", delegate { Run("explorer.exe", "/select,\"" + a.LaunchPath + "\""); }, dismiss: true);
            AddItem(cm, "Properties", delegate { FileShell.Properties(a.LaunchPath); }, dismiss: true);
        }
        if (isMru)
        {
            AddItem(cm, "Remove from this list", delegate { RemoveFromMru(a.LaunchPath); }, dismiss: false);
        }
        TaskbarContextMenu.ApplyTheme(cm);
        cm.Closed += delegate { _childMenuOpen = false; Topmost = true; };
        _childMenuOpen = true;
        Topmost = false;
        cm.PlacementTarget = target;
        cm.Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint;
        cm.IsOpen = true;
    }

    private void AddItem(ContextMenu cm, string text, Action act, bool dismiss)
    {
        MenuItem mi = new MenuItem { Header = text };
        mi.Click += delegate
        {
            if (dismiss)
            {
                Dismiss();
            }
            TaskbarContextMenu.QueueCommand(act);
        };
        cm.Items.Add(mi);
    }

    private void ToggleStartPin(string path, bool pin)
    {
        AppSettings st = SettingsStore.Load();
        st.Win7StartMenuPins ??= new List<string>();
        st.Win7StartMenuPins.RemoveAll((string p) => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
        if (pin)
        {
            st.Win7StartMenuPins.Add(path);
        }
        SettingsStore.Save(st);
        RefreshList();
    }

    private void ToggleTaskbarPin(AppEntry a, bool pin)
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
    }

    private void LaunchAdmin(AppEntry a)
    {
        ShellLaunch.Run(delegate { AppLauncher.TryLaunch(a.LaunchPath, null, a.AppId, asAdmin: true, out string? _); });
    }

    private void RemoveFromMru(string path)
    {
        AppSettings st = SettingsStore.Load();
        st.Win7StartMenuMruHidden ??= new List<string>();
        if (!st.Win7StartMenuMruHidden.Any((string p) => string.Equals(p, path, StringComparison.OrdinalIgnoreCase)))
        {
            st.Win7StartMenuMruHidden.Add(path);
        }
        SettingsStore.Save(st);
        RefreshList();
    }

    private void OnSearchEnter(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
        {
            return;
        }
        List<AppEntry> apps;
        try
        {
            apps = _appsProvider();
        }
        catch
        {
            return;
        }
        string q = _search.Text?.Trim() ?? string.Empty;
        AppEntry? hit = apps.Where((AppEntry a) => a.Name.Contains(q, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending((AppEntry a) => a.Name.StartsWith(q, StringComparison.OrdinalIgnoreCase))
            .ThenBy((AppEntry a) => a.Name).FirstOrDefault();
        if (hit != null)
        {
            Dismiss();
            _launch(hit);
        }
    }

    private static void OpenShell(string shellPath)
    {
        Run("explorer.exe", shellPath);
    }

    private static void Run(string file, string? args)
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
            Process.Start(psi);
        }
        catch (Exception ex)
        {
            Logger.Log("Win7Start run: " + ex.Message);
        }
    }

    public void ShowMenu()
    {
        _dismissing = false;
        _allMode = false;
        if (_allProgramsLabel != null)
        {
            _allProgramsLabel.Text = "All Programs";
        }
        if (_search != null)
        {
            _search.Text = string.Empty;
        }
        RefreshList();
        // Anchor bottom-left, just above the launcher taskbar.
        double taskbar = 48.0;
        try
        {
            System.Windows.Forms.Screen scr = System.Windows.Forms.Screen.FromPoint(System.Windows.Forms.Cursor.Position) ?? System.Windows.Forms.Screen.PrimaryScreen;
            double sx = MonitorDpi.ScaleFor(scr.Bounds);
            if (sx <= 0.0) sx = 1.0;
            double sy = sx;
            base.Left = (double)scr.Bounds.Left / sx + 2.0;
            base.Top = (double)scr.Bounds.Bottom / sy - Height - taskbar;
        }
        catch
        {
            base.Left = 2.0;
            base.Top = SystemParameters.PrimaryScreenHeight - Height - taskbar;
        }
        Show();
        WindowUtil.ForceForeground(this);
        _search?.Focus();
    }

    public void Dismiss()
    {
        if (_dismissing || !IsVisible)
        {
            return;
        }
        _dismissing = true;
        Hide();
    }
}
