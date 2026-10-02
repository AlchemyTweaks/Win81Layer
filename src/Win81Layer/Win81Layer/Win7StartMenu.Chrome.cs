using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;

#nullable enable annotations

namespace Win81Layer;

// Windows 7 Start menu: window chrome. All sizes are DIP at 96 DPI. The window is W = 265 + Rw by H = V + 119: an 18 px
// transparent overhang on top (the picture frame sticks out into it), the painted glass G = (257 + Rw) x (V + 101) at
// (0, 18) and an 8 px shadow band on the right. Inside G: the list pane (7..257), the bottom command row, the search box
// on the glass level with Shut down, and the right column of place links. Every brush is a palette resource reference.
public sealed partial class Win7StartMenu
{
    internal readonly record struct LayoutInfo(double V, double Gw, double Gh, double Rw, double W, double H, bool CollapseLinks);

    private const double Rx = 257.0;

    private const double Overhang = 18.0;

    private const double ShadowBand = 8.0;

    private const double VMin = 376.0;

    // Left edge of the search hint inside the search box (see BuildSearchBox).
    private const double SearchCueX = 8.0;

    private ContextMenu _powerMenu = null!;

    private Button _powerArrow = null!;

    private Button _powerMain = null!;

    // Right-column links by label (the QA harness activates them through their real mouse handler).
    private readonly Dictionary<string, FrameworkElement> _placeLinks = new Dictionary<string, FrameworkElement>(StringComparer.Ordinal);

    private readonly List<W7Row> _placeRows = new List<W7Row>();

    private LayoutInfo _layout;

    private double _rw;

    private Grid _glass = null!;

    private Border _glassOuter = null!;

    // Painted shadow rings, innermost first.
    private readonly Border[] _shadows = new Border[Win7Palette.ShadowRings];

    private Border _paneGlow = null!;

    private Border _pane = null!;

    private Border _bottomSep = null!;

    private W7Row _cmdRow = null!;

    private Path _cmdTriangle = null!;

    private Path _cmdMagnifier = null!;

    private Border _searchBox = null!;

    private TextBlock _searchCue = null!;

    private FrameworkElement _searchMag = null!;

    private Border _searchClear = null!;

    private Border _powerDivDark = null!;

    private Border _powerDivLight = null!;

    private Grid _picFrame = null!;

    private Border _picFill = null!;

    private Path _picSilhouette = null!;

    private W7Row _nameRow = null!;

    private StackPanel _rightLinks = null!;

    private ToolTip? _openTip;

    private static readonly string[] FixedLinkLabels =
    {
        "Documents", "Pictures", "Music", "Recent Items", "Computer", "Network", "Control Panel", "Devices and Printers",
        "Default Programs", "Help and Support"
    };

    private static double _rwMeasured;

    // Right-column width: the widest fixed label (12 px, Display formatting) plus 34, at least 143. The account name is
    // not counted; it ends with an ellipsis instead.
    internal static double MeasureRw()
    {
        if (_rwMeasured > 0.0)
        {
            return _rwMeasured;
        }
        double max = 0.0;
        Typeface tf = new Typeface(W7Row.UiFont, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
        foreach (string label in FixedLinkLabels)
        {
            FormattedText ft = new FormattedText(label, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, tf, 12.0, Brushes.Black, null, TextFormattingMode.Display, 1.0);
            max = Math.Max(max, ft.WidthIncludingTrailingWhitespace);
        }
        _rwMeasured = Math.Max(143.0, Math.Ceiling(max) + 34.0);
        return _rwMeasured;
    }

    // The list viewport V follows the main view (pins, separator, recent rows), at least 376 so the right column always
    // fits, and at most what the work area allows with the overhang included.
    internal static LayoutInfo LayoutFor(int pins, int mru, double maxWindowDip, double rw)
    {
        double content = pins * 36.0 + ((pins > 0 && mru > 0) ? 9.0 : 0.0) + mru * 36.0;
        double vmax = Math.Floor(maxWindowDip) - Overhang - 101.0;
        bool collapse = vmax < VMin;
        double v = collapse ? Math.Max(120.0, vmax) : Math.Clamp(Math.Max(content, VMin), VMin, vmax);
        return new LayoutInfo(v, 257.0 + rw, v + 101.0, rw, 265.0 + rw, v + 119.0, collapse);
    }

    // How many recent rows can show under the pins without making the window taller than the work area.
    internal static int RecentMaxFor(int pins, double maxWindowDip)
    {
        double vmax = Math.Floor(maxWindowDip) - Overhang - 101.0;
        double pinsBlock = pins * 36.0 + (pins > 0 ? 9.0 : 0.0);
        return (int)Math.Clamp(Math.Floor((vmax - pinsBlock) / 36.0), 0.0, 10.0);
    }

    // Applies V, Gh, Rw, W and H and every size-dependent position.
    private void ApplyLayout(LayoutInfo l)
    {
        _layout = l;
        double v = l.V;
        double gh = l.Gh;
        Width = l.W;
        Height = l.H;
        _glass.Width = l.Gw;
        _glass.Height = gh;
        for (int i = 0; i < _shadows.Length; i++)
        {
            _shadows[i].Width = l.Gw + (i + 1);
            _shadows[i].Height = gh + (i + 1);
        }
        _paneGlow.Height = v + 50.0;
        _pane.Height = v + 48.0;
        _progScroll.Height = v;
        _bottomSep.Margin = new Thickness(15.0, v + 16.0, 0.0, 0.0);
        _cmdRow.Margin = new Thickness(11.0, v + 21.0, 0.0, 0.0);
        _searchBox.Margin = new Thickness(13.0, gh - 35.0, 0.0, 0.0);
        _powerMain.Margin = new Thickness(Rx + 7.0, gh - 35.0, 0.0, 0.0);
        _powerArrow.Margin = new Thickness(Rx + 83.0, gh - 35.0, 0.0, 0.0);
        _powerDivDark.Margin = new Thickness(Rx + 82.0, gh - 34.0, 0.0, 0.0);
        _powerDivLight.Margin = new Thickness(Rx + 83.0, gh - 34.0, 0.0, 0.0);
        Visibility collapsed = l.CollapseLinks ? Visibility.Collapsed : Visibility.Visible;
        if (_placeLinks.TryGetValue("Recent Items", out FrameworkElement? ri))
        {
            ri.Visibility = collapsed;
        }
        if (_placeLinks.TryGetValue("Network", out FrameworkElement? nw))
        {
            nw.Visibility = collapsed;
        }
        if (l.CollapseLinks)
        {
            Trace($"Win7 Start: work area too short (V={v:0}), Recent Items and Network collapsed");
        }
    }

    private UIElement BuildRoot()
    {
        _rw = MeasureRw();
        double rw = _rw;
        Grid root = new Grid
        {
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            UseLayoutRounding = true,
            SnapsToDevicePixels = true,
            Cursor = Cursors.Arrow
        };
        TextOptions.SetTextFormattingMode(root, TextFormattingMode.Display);

        // Painted shadow: six rings fading over 6 DIP on the top and right only (the glass sits flush on the screen edge
        // and the bar). Outermost first, so the inner rings draw on top.
        for (int i = _shadows.Length - 1; i >= 0; i--)
        {
            _shadows[i] = ShadowRing("W7.Shadow" + (i + 1), i + 1.0, 7.0 + i);
            root.Children.Add(_shadows[i]);
        }

        // Glass: nested Borders (tint + outer edge, streaks + inner edge, sheen), so no clip geometry is needed.
        _glass = new Grid
        {
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0.0, Overhang, 0.0, 0.0)
        };
        _glassOuter = new Border
        {
            CornerRadius = new CornerRadius(6.0, 6.0, 0.0, 0.0),
            BorderThickness = new Thickness(1.0, 1.0, 1.0, 0.0),
            SnapsToDevicePixels = true
        };
        _glassOuter.SetResourceReference(Border.BorderBrushProperty, "W7.FrameOuter");
        _glassOuter.SetResourceReference(Border.BackgroundProperty, "W7.GlassTint");
        Border streak = new Border
        {
            CornerRadius = new CornerRadius(5.0, 5.0, 0.0, 0.0),
            BorderThickness = new Thickness(1.0, 1.0, 1.0, 0.0),
            SnapsToDevicePixels = true
        };
        streak.SetResourceReference(Border.BorderBrushProperty, "W7.FrameInner");
        streak.SetResourceReference(Border.BackgroundProperty, "W7.GlassStreak");
        Border sheen = new Border
        {
            CornerRadius = new CornerRadius(5.0, 5.0, 0.0, 0.0)
        };
        sheen.SetResourceReference(Border.BackgroundProperty, "W7.GlassSheen");
        streak.Child = sheen;
        _glassOuter.Child = streak;
        _glass.Children.Add(_glassOuter);

        // List pane: a white (dark: #1F1F1F) pane with a thin border and, in light, a faint outer glow.
        _paneGlow = new Border
        {
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(6.0, 6.0, 0.0, 0.0),
            Width = 252.0,
            CornerRadius = new CornerRadius(4.0),
            BorderThickness = new Thickness(1.0),
            IsHitTestVisible = false,
            SnapsToDevicePixels = true
        };
        _paneGlow.SetResourceReference(Border.BorderBrushProperty, "W7.PaneGlow");
        _glass.Children.Add(_paneGlow);
        _pane = new Border
        {
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(7.0, 7.0, 0.0, 0.0),
            Width = 250.0,
            CornerRadius = new CornerRadius(3.0),
            BorderThickness = new Thickness(1.0),
            SnapsToDevicePixels = true
        };
        _pane.SetResourceReference(Border.BorderBrushProperty, "W7.PaneBorder");
        _pane.SetResourceReference(Border.BackgroundProperty, "W7.PaneBg");
        _glass.Children.Add(_pane);

        _progScroll = new ScrollViewer
        {
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(8.0, 12.0, 0.0, 0.0),
            Width = 248.0,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            CanContentScroll = false,
            Focusable = false
        };
        KeyboardNavigation.SetIsTabStop(_progScroll, false);
        if (!SystemParameters.HighContrast && ScrollBarStyle.Value is Style sbStyle)
        {
            _progScroll.Resources[typeof(ScrollBar)] = sbStyle;
        }
        _progScroll.PreviewMouseWheel += OnListWheel;
        // Three containers share the viewport and only one is shown: the main view (rebuilt on every open), All Programs
        // (kept for as long as the inventory does not change) and the search results (rebuilt per keystroke). They stay in
        // the tree while collapsed, so a theme swap repaints them in place and nothing is re-parented on a view switch.
        _mainList = new StackPanel();
        _allList = new StackPanel
        {
            Visibility = Visibility.Collapsed
        };
        _searchList = new StackPanel
        {
            Visibility = Visibility.Collapsed
        };
        Grid lists = new Grid();
        lists.Children.Add(_mainList);
        lists.Children.Add(_allList);
        lists.Children.Add(_searchList);
        _progList = _mainList;
        _progScroll.Content = lists;
        _glass.Children.Add(_progScroll);

        _bottomSep = new Border
        {
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            Width = 234.0,
            Height = 1.0,
            IsHitTestVisible = false
        };
        _bottomSep.SetResourceReference(Border.BackgroundProperty, "W7.Sep");
        _glass.Children.Add(_bottomSep);

        // All Programs / Back: a filled triangle at P+14, mirrored for Back. See more results (search view): a 12 px
        // magnifier at P+10 instead. The label sits at P+28.
        _cmdRow = new W7Row(W7Row.Kind.Command30, onGlass: false)
        {
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            Width = 242.0,
            Tag = "w7:allprograms",
            ToolTipFactory = W7ToolTip
        };
        _cmdTriangle = W7Glyphs.TriangleRight("W7.Arrow");
        _cmdTriangle.RenderTransformOrigin = new Point(0.5, 0.5);
        _cmdRow.SetGlyph(_cmdTriangle, 10.0);
        _cmdMagnifier = W7Glyphs.Magnifier("W7.SearchGlyph", 12.0);
        _cmdMagnifier.Visibility = Visibility.Collapsed;
        _cmdRow.SetGlyph(_cmdMagnifier, 6.0);
        _cmdRow.SetText("All Programs");
        WireRow(_cmdRow);
        _glass.Children.Add(_cmdRow);

        _glass.Children.Add(BuildSearchBox());
        BuildRightColumn(rw);
        BuildShutDown();
        root.Children.Add(_glass);

        _picFrame = BuildPictureFrame(rw);
        root.Children.Add(_picFrame);

        ApplyLayout(LayoutFor(0, 0, 1036.0, rw));
        root.MouseRightButtonUp += OnEmptyAreaRightUp;
        return root;
    }

    private static Border ShadowRing(string key, double d, double radius)
    {
        Border b = new Border
        {
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0.0, Overhang - d, 0.0, 0.0),
            BorderThickness = new Thickness(0.0, 1.0, 1.0, 0.0),
            CornerRadius = new CornerRadius(radius, radius, 0.0, 0.0),
            IsHitTestVisible = false,
            SnapsToDevicePixels = true
        };
        b.SetResourceReference(Border.BorderBrushProperty, key);
        return b;
    }

    private Border BuildSearchBox()
    {
        _searchBox = new Border
        {
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            Width = 238.0,
            Height = 24.0,
            CornerRadius = new CornerRadius(2.0),
            BorderThickness = new Thickness(1.0),
            SnapsToDevicePixels = true
        };
        _searchBox.SetResourceReference(Border.BackgroundProperty, "W7.SearchBg");
        _searchBox.SetResourceReference(Border.BorderBrushProperty, "W7.SearchBorder");
        Grid g = new Grid();
        _search = new TextBox
        {
            BorderThickness = new Thickness(0.0),
            Background = Brushes.Transparent,
            FontFamily = W7Row.UiFont,
            FontSize = 12.0,
            // The caret sits at Padding.Left + 2 (5); the hint starts at 8, so the caret blinks just before it and typed
            // text starts within 3 px of where the hint was.
            Padding = new Thickness(3.0, 0.0, 24.0, 0.0),
            VerticalContentAlignment = VerticalAlignment.Center,
            FocusVisualStyle = null
        };
        _search.SetResourceReference(Control.ForegroundProperty, "W7.SearchInk");
        _search.SetResourceReference(TextBoxBase.CaretBrushProperty, "W7.SearchCaret");
        _search.SetResourceReference(TextBoxBase.SelectionBrushProperty, "W7.SearchSelection");
        // The edit menu takes keyboard focus while it is open; the selection it acts on stays visible meanwhile (its
        // inactive brush is mapped to the same palette colour in ApplyPalette).
        _search.IsInactiveSelectionHighlightEnabled = true;
        // A locally null ContextMenu stops the TextBox from opening its built-in edit menu; the menu's own edit menu
        // opens through OpenChildMenu instead (OnSearchContextMenuOpening).
        _search.ContextMenu = null;
        _search.ContextMenuOpening += OnSearchContextMenuOpening;
        _searchBox.IsKeyboardFocusWithinChanged += delegate
        {
            ApplySearchFocusLook(_searchBox.IsKeyboardFocusWithin);
        };
        _searchCue = new TextBlock
        {
            Text = "Search programs and files",
            FontFamily = W7Row.UiFont,
            FontSize = 12.0,
            FontStyle = FontStyles.Italic,
            Margin = new Thickness(SearchCueX, 0.0, 0.0, 0.0),
            VerticalAlignment = VerticalAlignment.Center,
            IsHitTestVisible = false
        };
        _searchCue.SetResourceReference(TextBlock.ForegroundProperty, "W7.SearchHint");
        _searchMag = W7Glyphs.Magnifier("W7.SearchGlyph", 14.0);
        _searchMag.HorizontalAlignment = HorizontalAlignment.Right;
        _searchMag.VerticalAlignment = VerticalAlignment.Center;
        _searchMag.Margin = new Thickness(0.0, 0.0, 5.0, 0.0);
        _searchMag.IsHitTestVisible = false;
        // While text is present the magnifier becomes a clear (x) button: a 20 x 20 hit area that clears the box and
        // leaves the caret in it.
        _searchClear = new Border
        {
            Width = 20.0,
            Height = 20.0,
            Background = Brushes.Transparent,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0.0, 0.0, 2.0, 0.0),
            Visibility = Visibility.Collapsed,
            Focusable = false,
            Tag = "w7:clear"
        };
        Path x = W7Glyphs.Clear("W7.SearchGlyph");
        x.HorizontalAlignment = HorizontalAlignment.Center;
        x.VerticalAlignment = VerticalAlignment.Center;
        _searchClear.Child = x;
        _searchClear.MouseLeftButtonDown += delegate (object s, MouseButtonEventArgs e)
        {
            e.Handled = true;
            _search.Clear();
            if (!_qa)
            {
                _search.Focus();
            }
        };
        _search.TextChanged += delegate
        {
            bool empty = string.IsNullOrEmpty(_search.Text);
            _searchCue.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
            _searchMag.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
            _searchClear.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;
            if (!_resetting)
            {
                // Typing leaves All Programs for the search view; clearing the box returns to the main view, wherever the
                // search started.
                if (!string.IsNullOrWhiteSpace(_search.Text))
                {
                    _allMode = false;
                }
                // A result row of the previous keystroke is discarded right away, so its highlight is not faded.
                ClearSelection(animate: !(_sel is W7Row selRow && ReferenceEquals(selRow.Parent, _searchList)));
                RefreshList();
            }
        };
        g.Children.Add(_search);
        g.Children.Add(_searchCue);
        g.Children.Add(_searchMag);
        g.Children.Add(_searchClear);
        _searchBox.Child = g;
        return _searchBox;
    }

    // The search box border while the caret is in it (every open of the live menu) and while it is not.
    private void ApplySearchFocusLook(bool focused)
    {
        _searchBox.SetResourceReference(Border.BorderBrushProperty, focused ? "W7.SearchBorderFocus" : "W7.SearchBorder");
    }

    private void BuildRightColumn(double rw)
    {
        _rightLinks = new StackPanel
        {
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(Rx + 6.0, 52.0, 0.0, 0.0),
            Width = rw - 13.0
        };
        _nameRow = PlaceRow(SafeUserName(), delegate
        {
            Act("shell:UsersFilesFolder", delegate { OpenShell("shell:UsersFilesFolder"); });
        }, key: null, what: "user folder");
        _nameRow.TextMaxWidth = rw - 30.0;
        _rightLinks.Children.Add(_nameRow);
        _rightLinks.Children.Add(PlaceRow("Documents", () => Act("shell:Personal", () => OpenShell("shell:Personal"))));
        _rightLinks.Children.Add(PlaceRow("Pictures", () => Act("shell:My Pictures", () => OpenShell("shell:My Pictures"))));
        _rightLinks.Children.Add(PlaceRow("Music", () => Act("shell:My Music", () => OpenShell("shell:My Music"))));
        _rightLinks.Children.Add(PlaceRow("Recent Items", () => Act("shell:Recent", () => OpenShell("shell:Recent"))));
        _rightLinks.Children.Add(GlassSeparator());
        _rightLinks.Children.Add(PlaceRow("Computer", () => Act("shell:MyComputerFolder", () => OpenShell("shell:MyComputerFolder"))));
        _rightLinks.Children.Add(PlaceRow("Network", () => Act("shell:NetworkPlacesFolder", () => OpenShell("shell:NetworkPlacesFolder"))));
        _rightLinks.Children.Add(GlassSeparator());
        _rightLinks.Children.Add(PlaceRow("Control Panel", () => Act("control.exe", () => RunShell("control.exe", null))));
        _rightLinks.Children.Add(PlaceRow("Devices and Printers", () => Act("control.exe printers", () => RunShell("control.exe", "printers"))));
        _rightLinks.Children.Add(PlaceRow("Default Programs", () => Act("control.exe /name Microsoft.DefaultPrograms", () => RunShell("control.exe", "/name Microsoft.DefaultPrograms"))));
        _rightLinks.Children.Add(PlaceRow("Help and Support", delegate
        {
            string help = HelpTarget();
            Act(help, () => OpenHelp(help));
        }));
        _glass.Children.Add(_rightLinks);
    }

    private static string SafeUserName()
    {
        try
        {
            return Environment.UserName;
        }
        catch
        {
            return "User";
        }
    }

    private W7Row PlaceRow(string text, Action onClick, string? key = "", string? what = null)
    {
        W7Row row = new W7Row(W7Row.Kind.Place31, onGlass: true)
        {
            Width = _rw - 13.0,
            Tag = "w7:place",
            ToolTipFactory = W7ToolTip
        };
        row.SetText(text);
        row.OnActivate = delegate
        {
            LaunchAndClose(what ?? text, onClick);
        };
        WireRow(row);
        _placeRows.Add(row);
        if (key != null)
        {
            _placeLinks[key.Length == 0 ? text : key] = row;
        }
        return row;
    }

    // A 13 px block: a faded 1 px light line with a 1 px dark etch right below it.
    private static Grid GlassSeparator()
    {
        Grid g = new Grid
        {
            Height = 13.0,
            IsHitTestVisible = false
        };
        Border line = new Border
        {
            Height = 1.0,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(2.0, 6.0, 1.0, 0.0)
        };
        line.SetResourceReference(Border.BackgroundProperty, "W7.GlassSep");
        Border etch = new Border
        {
            Height = 1.0,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(2.0, 7.0, 1.0, 0.0)
        };
        etch.SetResourceReference(Border.BackgroundProperty, "W7.GlassSepEtch");
        g.Children.Add(line);
        g.Children.Add(etch);
        return g;
    }

    // The 64 px glossy frame that sticks out 18 px above the glass: the account picture, or a vector silhouette.
    private Grid BuildPictureFrame(double rw)
    {
        Grid frame = new Grid
        {
            Width = 64.0,
            Height = 64.0,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(Rx + Math.Round((rw - 64.0) / 2.0, MidpointRounding.AwayFromZero), 0.0, 0.0, 0.0),
            Tag = "w7:picture"
        };
        Border outer = new Border
        {
            CornerRadius = new CornerRadius(5.0),
            BorderThickness = new Thickness(1.0),
            SnapsToDevicePixels = true
        };
        outer.SetResourceReference(Border.BorderBrushProperty, "W7.PicFrameStroke");
        outer.SetResourceReference(Border.BackgroundProperty, "W7.PicFrameFill");
        Border inner = new Border
        {
            Margin = new Thickness(1.0),
            CornerRadius = new CornerRadius(4.0),
            BorderThickness = new Thickness(1.0),
            IsHitTestVisible = false,
            SnapsToDevicePixels = true
        };
        inner.SetResourceReference(Border.BorderBrushProperty, "W7.PicFrameInner");
        Grid pic = new Grid
        {
            Width = 48.0,
            Height = 48.0,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(8.0, 8.0, 0.0, 0.0),
            IsHitTestVisible = false
        };
        _picFill = new Border
        {
            CornerRadius = new CornerRadius(2.0),
            SnapsToDevicePixels = true
        };
        _picFill.SetResourceReference(Border.BackgroundProperty, "W7.PicFallbackBg");
        RenderOptions.SetBitmapScalingMode(_picFill, BitmapScalingMode.HighQuality);
        _picSilhouette = W7Glyphs.Silhouette();
        Border edge = new Border
        {
            CornerRadius = new CornerRadius(2.0),
            BorderThickness = new Thickness(1.0),
            SnapsToDevicePixels = true
        };
        edge.SetResourceReference(Border.BorderBrushProperty, "W7.PicInnerBorder");
        pic.Children.Add(_picFill);
        pic.Children.Add(_picSilhouette);
        pic.Children.Add(edge);
        frame.Children.Add(outer);
        frame.Children.Add(inner);
        frame.Children.Add(pic);
        frame.MouseLeftButtonUp += delegate
        {
            LaunchAndClose("account picture", delegate { Act("ms-settings:accounts", PowerActions.AccountSettings); });
        };
        return frame;
    }

    // Shut down split button: two template-driven parts (no Aero2 triggers) sharing one divider. Hover and keyboard
    // selection fade the hot layer (in over Cat.Hover, out over Cat.Micro); presses are instant.
    private void BuildShutDown()
    {
        Grid label = new Grid();
        TextBlock shadow = PowerText("W7.PowerInkShadow");
        shadow.Margin = new Thickness(0.0, 1.0, 0.0, -1.0);
        label.Children.Add(shadow);
        label.Children.Add(PowerText("W7.PowerInk"));
        Button main = PowerPart(label, new Thickness(1.0, 1.0, 0.0, 1.0), new CornerRadius(3.0, 0.0, 0.0, 3.0), new CornerRadius(2.0, 0.0, 0.0, 2.0), Zone.PowerMain);
        main.Width = 76.0;
        main.Tag = "w7:shutdown";
        main.Click += delegate
        {
            ShutDownCommand();
        };
        Button arrow = PowerPart(W7Glyphs.TriangleRight("W7.PowerInk"), new Thickness(0.0, 1.0, 1.0, 1.0), new CornerRadius(0.0, 3.0, 3.0, 0.0), new CornerRadius(0.0, 2.0, 2.0, 0.0), Zone.PowerArrow);
        arrow.Width = 22.0;
        arrow.Tag = "w7:power";
        // Built and laid out once, so the first open is instant; the palette keys are copied in on every open.
        _powerMenu = BuildPowerMenu();
        TaskbarContextMenu.PrepareForInstantOpen(_powerMenu);
        arrow.Click += delegate
        {
            OpenPowerMenu(keyboard: false);
        };
        _powerMain = main;
        _powerArrow = arrow;
        _powerDivDark = PowerDivider("W7.PowerDivDark");
        _powerDivLight = PowerDivider("W7.PowerDivLight");
        _glass.Children.Add(main);
        _glass.Children.Add(arrow);
        _glass.Children.Add(_powerDivDark);
        _glass.Children.Add(_powerDivLight);
    }

    private static TextBlock PowerText(string key)
    {
        TextBlock t = new TextBlock
        {
            Text = "Shut down",
            FontFamily = W7Row.UiFont,
            FontSize = 12.0,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        t.SetResourceReference(TextBlock.ForegroundProperty, key);
        return t;
    }

    private static Border PowerDivider(string key)
    {
        Border b = new Border
        {
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            Width = 1.0,
            Height = 22.0,
            IsHitTestVisible = false
        };
        b.SetResourceReference(Border.BackgroundProperty, key);
        return b;
    }

    private Button PowerPart(object content, Thickness border, CornerRadius outer, CornerRadius inner, Zone zone)
    {
        Button b = new Button
        {
            Content = content,
            BorderThickness = border,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            Height = 24.0,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            Padding = new Thickness(0.0),
            Focusable = false,
            IsTabStop = false,
            FocusVisualStyle = null
        };
        ControlTemplate? t = PowerPartTemplate.Value;
        if (t != null)
        {
            b.OverridesDefaultStyle = true;
            b.Template = t;
            b.ApplyTemplate();
            foreach (string part in new[] { "Fill", "HotLayer", "PressLayer" })
            {
                if (t.FindName(part, b) is Border pb)
                {
                    pb.CornerRadius = outer;
                }
            }
            if (t.FindName("Inner", b) is Border ib)
            {
                // The inner highlight leaves out the shared edge, which carries only the divider's dark and light lines
                // (an etched groove, not a bright seam between two pills).
                ib.CornerRadius = inner;
                ib.BorderThickness = border;
            }
        }
        // Hover selects only after a real pointer move (see ArmHoverGuard): Shut down appearing under a resting pointer is
        // not a hover.
        b.MouseEnter += delegate
        {
            if (_pressed == null && PointerMoved())
            {
                Select(zone, b, byKeyboard: false);
            }
        };
        b.MouseMove += delegate
        {
            if (_pressed == null && !ReferenceEquals(_sel, b) && PointerMoved())
            {
                Select(zone, b, byKeyboard: false);
            }
        };
        b.MouseLeave += delegate
        {
            // A keyboard selection survives the mouse leaving it.
            if (ReferenceEquals(_sel, b) && !_selByKeyboard)
            {
                ClearSelection(animate: true);
            }
            SetPowerLayer(b, "PressLayer", 0.0);
        };
        b.PreviewMouseLeftButtonDown += delegate
        {
            SetPowerLayer(b, "PressLayer", 1.0);
        };
        b.PreviewMouseLeftButtonUp += delegate
        {
            SetPowerLayer(b, "PressLayer", 0.0);
        };
        b.LostMouseCapture += delegate
        {
            SetPowerLayer(b, "PressLayer", 0.0);
        };
        return b;
    }

    private static void SetPowerLayer(Button b, string part, double opacity)
    {
        if (b.Template?.FindName(part, b) is UIElement e)
        {
            e.BeginAnimation(OpacityProperty, null);
            e.Opacity = opacity;
        }
    }

    private void OnListWheel(object sender, MouseWheelEventArgs e)
    {
        e.Handled = true;
        // The wheel is the user's own pointer action: the highlight follows the rows that scroll under the pointer.
        _hoverAnchor = null;
        double delta = -(e.Delta / 120.0) * 66.0;
        if (MotionOff || SystemParameters.HighContrast)
        {
            _progScroll.ScrollToVerticalOffset(_progScroll.VerticalOffset + delta);
        }
        else
        {
            SmoothScroll.ByVertical(_progScroll, delta);
        }
    }

    // A Win7 tooltip: the scoped style, with the palette keys copied in (tooltips live outside the window's resources).
    internal ToolTip W7ToolTip(string text)
    {
        ToolTip tip = new ToolTip
        {
            Content = text
        };
        if (ToolTipStyle.Value is Style st)
        {
            tip.Style = st;
        }
        Win7Palette.CopyPopupKeys(_paletteDict, tip.Resources);
        tip.Opened += delegate
        {
            _openTip = tip;
            Win7Palette.CopyPopupKeys(_paletteDict, tip.Resources);
        };
        tip.Closed += delegate
        {
            if (ReferenceEquals(_openTip, tip))
            {
                _openTip = null;
            }
        };
        return tip;
    }

    // ---- Templates, parsed once ------------------------------------------------------------------------------------

    private const string XamlNs = "xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\" xmlns:x=\"http://schemas.microsoft.com/winfx/2006/xaml\"";

    private static readonly Lazy<Style?> ScrollBarStyle = new Lazy<Style?>(() => ParseXaml<Style>(
        "<Style " + XamlNs + " TargetType=\"ScrollBar\">" +
        "<Setter Property=\"OverridesDefaultStyle\" Value=\"True\"/>" +
        "<Setter Property=\"SnapsToDevicePixels\" Value=\"True\"/>" +
        "<Setter Property=\"Focusable\" Value=\"False\"/>" +
        "<Setter Property=\"IsTabStop\" Value=\"False\"/>" +
        "<Setter Property=\"Width\" Value=\"12\"/>" +
        "<Setter Property=\"MinWidth\" Value=\"12\"/>" +
        "<Setter Property=\"Template\"><Setter.Value>" +
        "<ControlTemplate TargetType=\"ScrollBar\">" +
        "<Border x:Name=\"Lane\" Background=\"Transparent\">" +
        "<Track x:Name=\"PART_Track\" Orientation=\"Vertical\" IsDirectionReversed=\"True\" Focusable=\"False\">" +
        "<Track.DecreaseRepeatButton><RepeatButton Command=\"ScrollBar.PageUpCommand\" Focusable=\"False\" IsTabStop=\"False\" OverridesDefaultStyle=\"True\">" +
        "<RepeatButton.Template><ControlTemplate TargetType=\"RepeatButton\"><Border Background=\"Transparent\"/></ControlTemplate></RepeatButton.Template>" +
        "</RepeatButton></Track.DecreaseRepeatButton>" +
        "<Track.IncreaseRepeatButton><RepeatButton Command=\"ScrollBar.PageDownCommand\" Focusable=\"False\" IsTabStop=\"False\" OverridesDefaultStyle=\"True\">" +
        "<RepeatButton.Template><ControlTemplate TargetType=\"RepeatButton\"><Border Background=\"Transparent\"/></ControlTemplate></RepeatButton.Template>" +
        "</RepeatButton></Track.IncreaseRepeatButton>" +
        "<Track.Thumb><Thumb Focusable=\"False\" IsTabStop=\"False\" OverridesDefaultStyle=\"True\" MinHeight=\"28\">" +
        "<Thumb.Template><ControlTemplate TargetType=\"Thumb\">" +
        "<Border Background=\"Transparent\"><Border x:Name=\"Bar\" Width=\"6\" Margin=\"0,2\" CornerRadius=\"3\" HorizontalAlignment=\"Center\" Background=\"{DynamicResource W7.ScrollThumb}\"/></Border>" +
        "<ControlTemplate.Triggers>" +
        "<DataTrigger Binding=\"{Binding IsMouseOver, RelativeSource={RelativeSource AncestorType=ScrollBar}}\" Value=\"True\"><Setter TargetName=\"Bar\" Property=\"Width\" Value=\"8\"/></DataTrigger>" +
        "<Trigger Property=\"IsMouseOver\" Value=\"True\"><Setter TargetName=\"Bar\" Property=\"Background\" Value=\"{DynamicResource W7.ScrollThumbHot}\"/></Trigger>" +
        "<Trigger Property=\"IsDragging\" Value=\"True\"><Setter TargetName=\"Bar\" Property=\"Background\" Value=\"{DynamicResource W7.ScrollThumbDrag}\"/><Setter TargetName=\"Bar\" Property=\"Width\" Value=\"8\"/></Trigger>" +
        "</ControlTemplate.Triggers>" +
        "</ControlTemplate></Thumb.Template>" +
        "</Thumb></Track.Thumb>" +
        "</Track>" +
        "</Border>" +
        "<ControlTemplate.Triggers><Trigger Property=\"IsMouseOver\" Value=\"True\"><Setter TargetName=\"Lane\" Property=\"Background\" Value=\"{DynamicResource W7.ScrollTrackHot}\"/></Trigger></ControlTemplate.Triggers>" +
        "</ControlTemplate>" +
        "</Setter.Value></Setter>" +
        "</Style>"));

    private static readonly Lazy<ControlTemplate?> PowerPartTemplate = new Lazy<ControlTemplate?>(() => ParseXaml<ControlTemplate>(
        "<ControlTemplate " + XamlNs + " TargetType=\"Button\">" +
        "<Grid SnapsToDevicePixels=\"True\">" +
        "<Border x:Name=\"Fill\" Background=\"{DynamicResource W7.PowerFill}\" BorderBrush=\"{DynamicResource W7.PowerStroke}\" BorderThickness=\"{TemplateBinding BorderThickness}\"/>" +
        "<Border x:Name=\"HotLayer\" Opacity=\"0\" Background=\"{DynamicResource W7.PowerHotFill}\" BorderBrush=\"{DynamicResource W7.PowerHotStroke}\" BorderThickness=\"{TemplateBinding BorderThickness}\"/>" +
        "<Border x:Name=\"PressLayer\" Opacity=\"0\" Background=\"{DynamicResource W7.PowerPressFill}\" BorderBrush=\"{DynamicResource W7.PowerHotStroke}\" BorderThickness=\"{TemplateBinding BorderThickness}\"/>" +
        "<Border x:Name=\"Inner\" BorderBrush=\"{DynamicResource W7.PowerInner}\" BorderThickness=\"1\" Margin=\"{TemplateBinding BorderThickness}\"/>" +
        "<ContentPresenter HorizontalAlignment=\"{TemplateBinding HorizontalContentAlignment}\" VerticalAlignment=\"{TemplateBinding VerticalContentAlignment}\" Margin=\"{TemplateBinding Padding}\"/>" +
        "</Grid>" +
        "</ControlTemplate>"));

    private static readonly Lazy<Style?> ToolTipStyle = new Lazy<Style?>(() => ParseXaml<Style>(
        "<Style " + XamlNs + " TargetType=\"ToolTip\">" +
        "<Setter Property=\"OverridesDefaultStyle\" Value=\"True\"/>" +
        "<Setter Property=\"HasDropShadow\" Value=\"False\"/>" +
        "<Setter Property=\"FontFamily\" Value=\"Segoe UI\"/>" +
        "<Setter Property=\"FontSize\" Value=\"12\"/>" +
        "<Setter Property=\"Foreground\" Value=\"{DynamicResource W7.TipInk}\"/>" +
        "<Setter Property=\"Template\"><Setter.Value>" +
        "<ControlTemplate TargetType=\"ToolTip\">" +
        "<Grid SnapsToDevicePixels=\"True\" UseLayoutRounding=\"True\" TextOptions.TextFormattingMode=\"Display\">" +
        "<Border Margin=\"2,2,0,0\" CornerRadius=\"2\" Background=\"{DynamicResource W7.TipShadow}\"/>" +
        "<Border Margin=\"0,0,2,2\" CornerRadius=\"2\" BorderThickness=\"1\" BorderBrush=\"{DynamicResource W7.TipBorder}\" Background=\"{DynamicResource W7.TipBg}\" Padding=\"6,3\">" +
        "<ContentPresenter/>" +
        "</Border>" +
        "</Grid>" +
        "</ControlTemplate>" +
        "</Setter.Value></Setter>" +
        "</Style>"));

    // Parsed once; a parse failure leaves the element on its default style and is logged (the QA checks catch it).
    private static T? ParseXaml<T>(string xaml) where T : class
    {
        try
        {
            object o = XamlReader.Parse(xaml);
            if (o is Freezable f && f.CanFreeze)
            {
                f.Freeze();
            }
            if (o is Style s)
            {
                s.Seal();
            }
            if (o is FrameworkTemplate ft)
            {
                ft.Seal();
            }
            return o as T;
        }
        catch (Exception ex)
        {
            Logger.Log("Win7 Start: template parse failed (" + typeof(T).Name + "): " + ex.Message);
            return null;
        }
    }
}

// Vector glyphs of the Windows 7 menu, drawn in code. Each returns a Path whose brush is a palette resource reference.
internal static class W7Glyphs
{
    private static readonly Geometry TriangleGeometry = Frozen(Geometry.Parse("M0,0 L4,3.5 L0,7 Z"));

    private static readonly Geometry ClearGeometry = Frozen(Geometry.Parse("M0,0 L8,8 M8,0 L0,8"));

    private static readonly Dictionary<double, Geometry> MagnifierBySize = new Dictionary<double, Geometry>();

    private static Geometry? _silhouette;

    private static Geometry Frozen(Geometry g)
    {
        g.Freeze();
        return g;
    }

    // A 4 x 7 filled triangle pointing right.
    internal static Path TriangleRight(string brushKey)
    {
        Path p = new Path
        {
            Data = TriangleGeometry,
            Width = 4.0,
            Height = 7.0,
            Stretch = Stretch.None,
            SnapsToDevicePixels = false
        };
        p.SetResourceReference(Shape.FillProperty, brushKey);
        return p;
    }

    // Lens ring r = 4.5 at (5.5, 5.5) with a 1.6 stroke, handle (8.7, 8.7) to (13, 13) with a 1.8 round-capped stroke, in a
    // 14 box; both outlines are filled as one shape so the two stroke widths survive any size.
    internal static Path Magnifier(string key, double size)
    {
        if (!MagnifierBySize.TryGetValue(size, out Geometry? g))
        {
            Pen ringPen = new Pen(Brushes.Black, 1.6);
            Pen handlePen = new Pen(Brushes.Black, 1.8)
            {
                StartLineCap = PenLineCap.Round,
                EndLineCap = PenLineCap.Round
            };
            Geometry ring = new EllipseGeometry(new Point(5.5, 5.5), 4.5, 4.5).GetWidenedPathGeometry(ringPen);
            Geometry handle = new LineGeometry(new Point(8.7, 8.7), new Point(13.0, 13.0)).GetWidenedPathGeometry(handlePen);
            Geometry both = new CombinedGeometry(GeometryCombineMode.Union, ring, handle).GetFlattenedPathGeometry();
            if (Math.Abs(size - 14.0) > 0.01)
            {
                both = both.Clone();
                both.Transform = new ScaleTransform(size / 14.0, size / 14.0);
            }
            both.Freeze();
            g = both;
            MagnifierBySize[size] = g;
        }
        Path p = new Path
        {
            Data = g,
            Width = size,
            Height = size,
            Stretch = Stretch.None
        };
        p.SetResourceReference(Shape.FillProperty, key);
        return p;
    }

    // Two 8 x 8 diagonals, stroke 1.4.
    internal static Path Clear(string key)
    {
        Path p = new Path
        {
            Data = ClearGeometry,
            Width = 8.0,
            Height = 8.0,
            Stretch = Stretch.None,
            StrokeThickness = 1.4,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round
        };
        p.SetResourceReference(Shape.StrokeProperty, key);
        return p;
    }

    // Account silhouette in a 48 box: head circle r = 7 at (24, 18), shoulders a half circle from (11, 40) to (37, 40)
    // peaking at y 27.
    internal static Path Silhouette()
    {
        if (_silhouette == null)
        {
            GeometryGroup g = new GeometryGroup
            {
                FillRule = FillRule.Nonzero
            };
            g.Children.Add(new EllipseGeometry(new Point(24.0, 18.0), 7.0, 7.0));
            g.Children.Add(Geometry.Parse("M11,40 A13,13 0 0 1 37,40 Z"));
            g.Freeze();
            _silhouette = g;
        }
        Path p = new Path
        {
            Data = _silhouette,
            Width = 48.0,
            Height = 48.0,
            Stretch = Stretch.None,
            IsHitTestVisible = false
        };
        p.SetResourceReference(Shape.FillProperty, "W7.GlassInk");
        return p;
    }
}
