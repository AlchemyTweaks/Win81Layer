using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;

#nullable enable annotations

namespace Win81Layer;

// One row of the Windows 7 Start menu: program rows (36 px with a 32 px icon, 22 px with a 16 px icon), folder rows,
// group headers, the bottom command row, right-column place links and message rows. A Grid with three columns
// [icon area | text | reserved, collapsed], so long names always end with an ellipsis. The hot and pressed layers
// exist from the start at Opacity 0; every brush is a resource reference into the menu's palette.
internal sealed class W7Row : Grid
{
    internal enum Kind
    {
        Program36,
        Program22,
        Folder22,
        Header24,
        Command30,
        Place31,
        Message22
    }

    internal static readonly FontFamily UiFont = new FontFamily("Segoe UI");

    private readonly Border? _hot;

    private readonly Border? _press;

    private readonly TextBlock _text;

    private readonly TextBlock? _shadow;

    private readonly Grid? _iconBox;

    private readonly Border? _plate;

    private readonly Image? _image;

    private readonly Border? _rule;

    private readonly int _box;

    private IconPlate _plateKind;

    private Brush? _plateBrush;

    private bool _alwaysPlate;

    private ImageSource? _src;

    private ImageSource? _platedSrc;

    private string _label = string.Empty;

    // The label the current tooltip was built for.
    private string? _tipLabel;

    internal W7Row(Kind kind, bool onGlass, int depth = 0)
    {
        RowKind = kind;
        OnGlass = onGlass;
        Depth = depth;
        Background = Brushes.Transparent;
        Focusable = false;
        KeyboardNavigation.SetIsTabStop(this, false);
        SnapsToDevicePixels = true;
        double height;
        double iconCol;
        double hotInset = 4.0;
        double iconX = 0.0;
        double iconY = 0.0;
        bool interactive = true;
        switch (kind)
        {
        case Kind.Program36:
            height = 36.0;
            _box = 32;
            iconX = 8.0;
            iconY = 2.0;
            iconCol = 48.0;
            break;
        case Kind.Program22:
        case Kind.Folder22:
            height = 22.0;
            _box = 16;
            iconX = 8.0 + 16.0 * depth;
            iconY = 3.0;
            iconCol = 30.0 + 16.0 * depth;
            break;
        case Kind.Header24:
            height = 24.0;
            iconCol = 8.0;
            interactive = false;
            break;
        case Kind.Command30:
            height = 30.0;
            iconCol = 24.0;
            hotInset = 0.0;
            break;
        case Kind.Place31:
            height = 31.0;
            iconCol = 10.0;
            hotInset = 0.0;
            break;
        default:
            height = 22.0;
            iconCol = 8.0;
            interactive = false;
            break;
        }
        Height = height;
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(iconCol) });
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.0, GridUnitType.Star) });
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(0.0) });   // reserved for a jump zone

        if (interactive)
        {
            _hot = Layer(onGlass ? "W7.GlassHotFill" : "W7.HotFill", onGlass ? "W7.GlassHotStroke" : "W7.HotStroke", hotInset);
            Border inner = new Border
            {
                CornerRadius = new CornerRadius(2.0),
                BorderThickness = new Thickness(1.0),
                SnapsToDevicePixels = true
            };
            inner.SetResourceReference(Border.BorderBrushProperty, onGlass ? "W7.GlassHotInner" : "W7.HotInner");
            _hot.Child = inner;
            _press = Layer(onGlass ? "W7.GlassPressFill" : "W7.PressFill", onGlass ? "W7.GlassPressStroke" : "W7.PressStroke", hotInset);
            Children.Add(_hot);
            Children.Add(_press);
        }

        if (_box > 0)
        {
            _iconBox = new Grid
            {
                Width = _box,
                Height = _box,
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(iconX, iconY, 0.0, 0.0),
                IsHitTestVisible = false
            };
            // In a 36 px row the 32 px box runs right up to the highlight's inner line at the top and bottom, so the plate
            // is inset by 1 px (30 x 30, the icon still centred at 24) and keeps a gap to it; the 22 px rows have room.
            _plate = new Border
            {
                CornerRadius = new CornerRadius(2.0),
                Margin = new Thickness(kind == Kind.Program36 ? 1.0 : 0.0),
                Visibility = Visibility.Collapsed,
                SnapsToDevicePixels = true
            };
            _image = new Image
            {
                Width = _box,
                Height = _box,
                Stretch = Stretch.None,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            RenderOptions.SetBitmapScalingMode(_image, BitmapScalingMode.HighQuality);
            _iconBox.Children.Add(_plate);
            _iconBox.Children.Add(_image);
            Grid.SetColumn(_iconBox, 0);
            Children.Add(_iconBox);
        }

        string ink = onGlass ? "W7.GlassInk" : (kind == Kind.Message22 ? "W7.Ink2" : (kind == Kind.Header24 ? "W7.GroupHeaderInk" : "W7.Ink"));
        if (onGlass)
        {
            // The text shadow is a second copy one pixel lower, not an Effect.
            _shadow = NewText(kind);
            _shadow.Margin = new Thickness(0.0, 1.0, 8.0, -1.0);
            _shadow.SetResourceReference(TextBlock.ForegroundProperty, "W7.GlassInkShadow");
            Grid.SetColumn(_shadow, 1);
            Children.Add(_shadow);
        }
        _text = NewText(kind);
        _text.SetResourceReference(TextBlock.ForegroundProperty, ink);
        if (kind == Kind.Header24)
        {
            // Group header: the label, then a 1 px rule from 6 px after the label to 8 px before the edge, at y 12.
            Grid header = new Grid
            {
                IsHitTestVisible = false
            };
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.0, GridUnitType.Star) });
            _text.Margin = new Thickness(0.0);
            header.Children.Add(_text);
            _rule = new Border
            {
                Height = 1.0,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(6.0, 12.0, 8.0, 0.0),
                SnapsToDevicePixels = true
            };
            _rule.SetResourceReference(Border.BackgroundProperty, "W7.GroupRule");
            Grid.SetColumn(_rule, 1);
            header.Children.Add(_rule);
            Grid.SetColumn(header, 1);
            Children.Add(header);
        }
        else
        {
            Grid.SetColumn(_text, 1);
            Children.Add(_text);
        }
        _text.SizeChanged += delegate
        {
            UpdateTrim();
        };
        ToolTipService.SetInitialShowDelay(this, 700);
    }

    internal Kind RowKind { get; }

    internal bool OnGlass { get; }

    internal int Depth { get; }

    internal object? Payload { get; set; }

    // The folder row a program row of the All Programs tree sits under (null at the root).
    internal W7Row? ParentRow { get; set; }

    // The group header's rule (Header24 only).
    internal Border? Rule => _rule;

    internal bool IsTrimmed { get; private set; }

    internal string Text => _label;

    internal TextBlock TextBlock => _text;

    internal int IconBox => _box;

    // The row icon at box size (without a plate); the Image shows PlatedSource instead while a plate is shown.
    internal ImageSource? IconSource => _src;

    internal ImageSource? PlatedSource => _platedSrc;

    internal ImageSource? DisplayedSource => _image?.Source;

    internal Image? IconImage => _image;

    internal IconPlate PlateKind => _plateKind;

    internal bool AlwaysPlate => _alwaysPlate;

    internal bool PlateShown => _plate != null && _plate.Visibility == Visibility.Visible;

    internal Border? PlateBorder => _plate;

    internal Border? HotLayer => _hot;

    internal Border? PressLayer => _press;

    // Rows with a highlight (program, folder, command and place rows) take part in the selection; headers and
    // messages do not.
    internal bool IsSelectable => _hot != null;

    // A recent-list row of the main view (its item menu offers 'Remove from this list').
    internal bool IsRecent { get; set; }

    // What the row does when it is activated (place links); program and command rows are handled by the menu.
    internal Action? OnActivate { get; set; }

    // The hover-out fade length for a motion category, or null when motion is off (then nothing is animated).
    internal Func<Motion.Cat, Duration?>? Fade { get; set; }

    // Builds the tooltip shown while the label is cut off (the menu supplies its Win7 tooltip).
    internal Func<string, ToolTip>? ToolTipFactory { get; set; }

    // Caps the label width (the account name link).
    internal double TextMaxWidth
    {
        set
        {
            _text.MaxWidth = value;
            if (_shadow != null)
            {
                _shadow.MaxWidth = value;
            }
            _text.HorizontalAlignment = HorizontalAlignment.Left;
            if (_shadow != null)
            {
                _shadow.HorizontalAlignment = HorizontalAlignment.Left;
            }
        }
    }

    private static TextBlock NewText(Kind kind)
    {
        TextBlock t = new TextBlock
        {
            FontFamily = UiFont,
            FontSize = 12.0,
            FontWeight = kind == Kind.Header24 ? FontWeights.SemiBold : FontWeights.Normal,
            TextTrimming = TextTrimming.CharacterEllipsis,
            TextWrapping = TextWrapping.NoWrap,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0.0, 0.0, 8.0, 0.0),
            IsHitTestVisible = false
        };
        return t;
    }

    private static Border Layer(string fillKey, string strokeKey, double inset)
    {
        Border b = new Border
        {
            CornerRadius = new CornerRadius(3.0),
            BorderThickness = new Thickness(1.0),
            Margin = new Thickness(inset, 0.0, inset, 0.0),
            Opacity = 0.0,
            IsHitTestVisible = false,
            SnapsToDevicePixels = true
        };
        b.SetResourceReference(Border.BackgroundProperty, fillKey);
        b.SetResourceReference(Border.BorderBrushProperty, strokeKey);
        Grid.SetColumnSpan(b, 3);
        return b;
    }

    internal void SetText(string text)
    {
        _label = text ?? string.Empty;
        _text.Text = _label;
        if (_shadow != null)
        {
            _shadow.Text = _label;
        }
        AutomationProperties.SetName(this, _label);
        UpdateTrim();
    }

    // A small vector glyph in the icon column (the command row's triangle, the See more results magnifier).
    internal void SetGlyph(FrameworkElement glyph, double x)
    {
        glyph.HorizontalAlignment = HorizontalAlignment.Left;
        glyph.VerticalAlignment = VerticalAlignment.Center;
        glyph.Margin = new Thickness(x, 0.0, 0.0, 0.0);
        glyph.IsHitTestVisible = false;
        Grid.SetColumn(glyph, 0);
        Children.Add(glyph);
    }

    // plateBrush: an explicit plate (a win81 entry's tile colour). alwaysPlate without a brush uses W7.IconPlate.
    // platedSrc: the icon drawn at the plate inset size, shown while a plate is shown (src is scaled down otherwise).
    internal void SetIcon(ImageSource? src, ImageSource? platedSrc, IconPlate plate, Brush? plateBrush, bool alwaysPlate, bool dark)
    {
        if (_image == null)
        {
            return;
        }
        _src = src;
        _platedSrc = platedSrc;
        _plateKind = plate;
        _plateBrush = plateBrush;
        _alwaysPlate = alwaysPlate || plateBrush != null;
        UpdatePlate(dark);
    }

    // Plates follow the theme: white single-colour logos on the light pane, black ones on the dark pane, and pale icons
    // with nothing that stands out from a pane on a neutral plate there.
    internal void UpdatePlate(bool dark)
    {
        if (_image == null || _plate == null)
        {
            return;
        }
        bool show = false;
        if (_src != null)
        {
            if (_alwaysPlate)
            {
                show = true;
                if (_plateBrush != null)
                {
                    _plate.Background = _plateBrush;
                }
                else
                {
                    _plate.SetResourceReference(Border.BackgroundProperty, "W7.IconPlate");
                }
            }
            else if (!dark && _plateKind == IconPlate.LightMono)
            {
                show = true;
                _plate.SetResourceReference(Border.BackgroundProperty, "W7.IconPlate");
            }
            else if (!dark && _plateKind == IconPlate.PaleOnLight)
            {
                show = true;
                _plate.SetResourceReference(Border.BackgroundProperty, "W7.IconPlateNeutral");
            }
            else if (dark && (_plateKind == IconPlate.DarkMono || _plateKind == IconPlate.PaleOnDark || _plateKind == IconPlate.DarkFill))
            {
                show = true;
                _plate.SetResourceReference(Border.BackgroundProperty, "W7.IconPlateLight");
            }
        }
        _plate.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        if (show)
        {
            double inset = W7Icons.InsetDip(_box);
            _image.Source = _platedSrc ?? _src;
            _image.Width = inset;
            _image.Height = inset;
            _image.Stretch = (_image.Source is BitmapSource p && p.PixelWidth == (int)inset && p.PixelHeight == (int)inset) ? Stretch.None : Stretch.Uniform;
        }
        else
        {
            _image.Source = _src;
            _image.Width = _box;
            _image.Height = _box;
            // A bitmap of exactly the box size maps 1:1; on a scaled monitor the larger bitmap fills the box.
            _image.Stretch = (_src is BitmapSource b && b.PixelWidth == _box && b.PixelHeight == _box) ? Stretch.None : Stretch.Uniform;
        }
    }

    // Hover in is instant. Hover out fades the highlight from where it is to 0 over Motion Cat.Micro (CubicOut); the base
    // value is set to 0 first and the fade stops itself, so nothing is left holding a value.
    internal void SetHot(bool on, bool animate)
    {
        if (_hot == null)
        {
            return;
        }
        if (on)
        {
            _hot.BeginAnimation(OpacityProperty, null);
            _hot.Opacity = 1.0;
            return;
        }
        double from = _hot.Opacity;
        _hot.BeginAnimation(OpacityProperty, null);
        _hot.Opacity = 0.0;
        Duration? d = animate ? Fade?.Invoke(Motion.Cat.Micro) : null;
        if (d.HasValue && from > 0.0)
        {
            DoubleAnimation fade = new DoubleAnimation(from, 0.0, d.Value)
            {
                EasingFunction = Motion.Ease(Motion.Cat.Micro),
                FillBehavior = FillBehavior.Stop
            };
            fade.Freeze();
            _hot.BeginAnimation(OpacityProperty, fade);
        }
    }

    internal void SetPressed(bool on)
    {
        if (_press == null)
        {
            return;
        }
        _press.BeginAnimation(OpacityProperty, null);
        _press.Opacity = on ? 1.0 : 0.0;
    }

    // Cut-off labels get the full name as a tooltip. Measured with the same typeface and formatting mode as the text.
    private void UpdateTrim()
    {
        double avail = _text.ActualWidth;
        bool trimmed = false;
        if (avail > 0.0 && _label.Length > 0)
        {
            double ppd = 1.0;
            try
            {
                ppd = VisualTreeHelper.GetDpi(this).PixelsPerDip;
            }
            catch
            {
            }
            FormattedText ft = new FormattedText(_label, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                new Typeface(_text.FontFamily, _text.FontStyle, _text.FontWeight, _text.FontStretch), _text.FontSize, Brushes.Black,
                null, TextOptions.GetTextFormattingMode(_text), ppd);
            trimmed = ft.WidthIncludingTrailingWhitespace > avail + 0.5;
        }
        // A row that stays trimmed while its label changes (the account name link) rebuilds its tooltip.
        if (trimmed == IsTrimmed && (!trimmed || (ToolTip != null && string.Equals(_tipLabel, _label, StringComparison.Ordinal))))
        {
            return;
        }
        IsTrimmed = trimmed;
        ToolTip = trimmed ? ToolTipFactory?.Invoke(_label) : null;
        _tipLabel = ToolTip != null ? _label : null;
    }
}

// Selection, keyboard and press tracking. There is one highlight: the row (or Shut down part) the mouse hovers or the
// keyboard moved to. Keyboard focus never leaves the search box, so the keyboard drives a virtual cursor instead: typing,
// IME and Greek input always reach the box, Tab never moves focus to an invisible button and Enter only acts on what is
// highlighted or on the first result of a typed search.
public sealed partial class Win7StartMenu
{
    internal enum Zone
    {
        None,
        Left,
        Right,
        PowerMain,
        PowerArrow
    }

    private Zone _zone;

    private FrameworkElement? _sel;

    private bool _selByKeyboard;

    // The selection is the search view's automatic first result (RefreshSearch), not one the user moved to. Any other
    // Select clears it.
    private bool _selAuto;

    // The last left-pane selection (Left from the right column or Shut down goes back to it).
    private W7Row? _lastLeft;

    // Press tracking: the row the left button went down on, and where.
    private W7Row? _pressed;

    private Point _pressAt;

    // The row whose item menu is open: it stays lit while the menu is up.
    private W7Row? _menuRow;

    // QA only: where a synthetic mouse event lands (the harness window is offscreen, so the real pointer never is).
    private Point? _qaPointer;

    // Where the pointer was when hover was last disarmed (screen pixels). WPF hit-tests a resting pointer again after
    // every layout pass, and Windows sends a mouse move when a window appears under it, so a row that is rebuilt, scrolled
    // or shown under a still pointer gets MouseEnter without the user doing anything. Hover only selects once the pointer
    // has left this position; it is recorded when the menu opens, on every key and after every list rebuild. This keeps
    // Enter on what the user typed or moved to, never on whatever happened to slide under the pointer.
    private (int X, int Y)? _hoverAnchor;

    private void ArmHoverGuard()
    {
        _hoverAnchor = CursorNow();
    }

    // True once the pointer has really moved since the guard was armed (or when its position cannot be read).
    private bool PointerMoved()
    {
        if (_hoverAnchor is not { } anchor)
        {
            return true;
        }
        if (CursorNow() is { } now && now == anchor)
        {
            return false;
        }
        _hoverAnchor = null;
        return true;
    }

    private Duration? FadeDur(Motion.Cat c)
    {
        return MotionOff ? null : Dur(c);
    }

    // Moves the highlight. The previous element fades out (rows and Shut down over Cat.Micro), the new one lights at once
    // (Shut down fades in over Cat.Hover). A keyboard move inside the list scrolls the row into view.
    private void Select(Zone zone, FrameworkElement? el, bool byKeyboard, bool animate = true)
    {
        if (el == null)
        {
            zone = Zone.None;
        }
        FrameworkElement? old = _sel;
        _sel = el;
        _zone = zone;
        _selByKeyboard = el != null && byKeyboard;
        _selAuto = false;
        if (!ReferenceEquals(old, el))
        {
            if (old != null)
            {
                Highlight(old, on: false, animate);
            }
            if (el != null)
            {
                Highlight(el, on: true, animate);
            }
        }
        if (zone == Zone.Left && el is W7Row row)
        {
            _lastLeft = row;
            if (byKeyboard && !ReferenceEquals(row, _cmdRow))
            {
                row.BringIntoView();
            }
        }
    }

    private void ClearSelection(bool animate)
    {
        Select(Zone.None, null, byKeyboard: false, animate);
    }

    private void Highlight(FrameworkElement el, bool on, bool animate)
    {
        if (el is W7Row row)
        {
            if (!on && ReferenceEquals(row, _menuRow) && _childMenuOpen)
            {
                return;   // its item menu is open
            }
            row.SetHot(on, animate);
        }
        else if (el is Button b)
        {
            SetPowerHot(b, on, animate);
        }
    }

    private static Zone ZoneOf(W7Row row)
    {
        return row.RowKind == W7Row.Kind.Place31 ? Zone.Right : Zone.Left;
    }

    // Selectable rows of the current left view, top to bottom (without the command row).
    private List<W7Row> ListRows()
    {
        List<W7Row> rows = new List<W7Row>(_progList.Children.Count);
        foreach (UIElement child in _progList.Children)
        {
            if (child is W7Row r && r.IsSelectable && r.Visibility == Visibility.Visible)
            {
                rows.Add(r);
            }
        }
        return rows;
    }

    // The L zone: the list rows, then the bottom command row.
    private List<W7Row> LeftRows()
    {
        List<W7Row> rows = ListRows();
        rows.Add(_cmdRow);
        return rows;
    }

    // The R zone: Name ... Help and Support (collapsed links are skipped).
    private List<W7Row> RightRows()
    {
        List<W7Row> rows = new List<W7Row>(_rightLinks.Children.Count);
        foreach (UIElement child in _rightLinks.Children)
        {
            if (child is W7Row r && r.Visibility == Visibility.Visible)
            {
                rows.Add(r);
            }
        }
        return rows;
    }

    private void SelectKb(Zone zone, FrameworkElement? el)
    {
        Select(zone, el, byKeyboard: true);
    }

    // A row of the list that is shown now (the current container, not inside a collapsed folder).
    private bool InCurrentList(W7Row row)
    {
        return ReferenceEquals(row.Parent, _progList) && row.Visibility == Visibility.Visible;
    }

    // The list was rebuilt or switched: a selection or press on a row that is no longer shown is dropped, and its
    // highlight goes with it (All Programs rows outlive the view, so they must not stay lit).
    private void DropDetachedSelection()
    {
        if (_sel is W7Row row && _zone == Zone.Left && !ReferenceEquals(row, _cmdRow) && !InCurrentList(row))
        {
            _sel = null;
            _zone = Zone.None;
            _selByKeyboard = false;
            _selAuto = false;
            row.SetHot(on: false, animate: false);
        }
        if (_lastLeft != null && !ReferenceEquals(_lastLeft, _cmdRow) && !InCurrentList(_lastLeft))
        {
            _lastLeft = null;
        }
        if (_pressed != null && !ReferenceEquals(_pressed, _cmdRow) && ZoneOf(_pressed) == Zone.Left && !InCurrentList(_pressed))
        {
            CancelPress();
        }
    }

    // ---- Keyboard ---------------------------------------------------------------------------------------------------

    private void OnPreviewKey(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.ImeProcessed || _childMenuOpen)
        {
            return;   // open child menus handle their own keys
        }
        if (_dismissing || _hidePending)
        {
            e.Handled = true;   // a closing menu acts on nothing
            return;
        }
        // The keyboard owns the highlight now: a row that scrolls under the resting pointer does not take it back.
        ArmHoverGuard();
        Key key = e.Key == Key.System ? e.SystemKey : e.Key;
        ModifierKeys mods = Mods();
        switch (key)
        {
        case Key.Escape:
            e.Handled = true;
            if (!string.IsNullOrEmpty(_search.Text))
            {
                _search.Clear();
            }
            else if (_allMode)
            {
                ToggleAllPrograms();
            }
            else
            {
                Dismiss("esc");
            }
            break;
        case Key.Down:
            e.Handled = KeyVertical(1);
            break;
        case Key.Up:
            e.Handled = KeyVertical(-1);
            break;
        case Key.Right:
            e.Handled = !SearchBoxKeepsKey(key) && KeyRight();
            break;
        case Key.Left:
            e.Handled = KeyLeft();
            break;
        case Key.Home:
        case Key.End:
        case Key.PageUp:
        case Key.PageDown:
            // With Shift or Ctrl these select or move in the box (Shift+End, Ctrl+Home, ...).
            if (_zone == Zone.Left && mods == ModifierKeys.None && !SearchBoxKeepsKey(key))
            {
                e.Handled = true;
                KeyJump(key);
            }
            break;
        case Key.Tab:
            e.Handled = true;   // keyboard focus never leaves the search box
            KeyTab(back: (mods & ModifierKeys.Shift) != 0);
            break;
        case Key.Enter:
            e.Handled = true;
            KeyEnter(asAdmin: (mods & (ModifierKeys.Control | ModifierKeys.Shift)) == (ModifierKeys.Control | ModifierKeys.Shift));
            break;
        case Key.Apps:
            e.Handled = !SearchBoxKeepsKey(key) && KeyItemMenu();
            break;
        case Key.F10:
            if ((mods & ModifierKeys.Shift) != 0 && (mods & (ModifierKeys.Control | ModifierKeys.Alt)) == 0)
            {
                e.Handled = !SearchBoxKeepsKey(key) && KeyItemMenu();
            }
            break;
        }
    }

    // While the search view shows, the keys that edit the typed query stay with the box, as in Windows 7: Right, Home and
    // End always (Left already does, a result row has nothing to its left), and PageUp, PageDown, Apps and Shift+F10 while
    // the highlight is still the automatic first result, so the box's edit menu stays reachable. Up and Down move the
    // highlight; once the user has moved it, PageUp, PageDown and the item menu act on the results again.
    private bool SearchBoxKeepsKey(Key key)
    {
        if (_view != View.Search || _zone != Zone.Left)
        {
            return false;
        }
        return key == Key.Right || key == Key.Home || key == Key.End || _selAuto;
    }

    // Down: nothing selected -> the first L row; in L the next row, then the command row, then around to the first; in R
    // the next link, then Shut down. Up mirrors it (nothing selected -> the command row).
    private bool KeyVertical(int dir)
    {
        switch (_zone)
        {
        case Zone.None:
        {
            List<W7Row> left = LeftRows();
            SelectKb(Zone.Left, dir > 0 ? left[0] : _cmdRow);
            return true;
        }
        case Zone.Left:
        {
            List<W7Row> left = LeftRows();
            int i = _sel is W7Row cur ? left.IndexOf(cur) : -1;
            int next = i < 0 ? (dir > 0 ? 0 : left.Count - 1) : (i + dir + left.Count) % left.Count;
            SelectKb(Zone.Left, left[next]);
            return true;
        }
        case Zone.Right:
        {
            List<W7Row> right = RightRows();
            int i = _sel is W7Row cur ? right.IndexOf(cur) : -1;
            if (dir > 0)
            {
                if (i + 1 < right.Count)
                {
                    SelectKb(Zone.Right, right[i + 1]);
                }
                else
                {
                    SelectKb(Zone.PowerMain, _powerMain);
                }
            }
            else if (i > 0)
            {
                SelectKb(Zone.Right, right[i - 1]);
            }
            return true;
        }
        default:
            if (dir < 0)
            {
                List<W7Row> right = RightRows();
                if (right.Count > 0)
                {
                    SelectKb(Zone.Right, right[^1]);
                }
            }
            return true;
        }
    }

    // Right: a collapsed folder expands, an expanded one moves to its first child; the All Programs row opens All Programs
    // and selects its first row; any other L row goes to the right column (Name); Shut down -> its arrow; the arrow opens
    // the power flyout with its first item highlighted. With nothing selected the caret moves.
    private bool KeyRight()
    {
        switch (_zone)
        {
        case Zone.None:
            return false;
        case Zone.Left:
            if (ReferenceEquals(_sel, _cmdRow) && _view == View.Main)
            {
                OpenAllProgramsByKeyboard();
                return true;
            }
            if (_sel is W7Row folderRow && folderRow.Payload is TreeNode folder)
            {
                if (!folder.IsExpanded)
                {
                    ToggleFolder(folder, expand: true, bringIntoView: true);
                }
                else if (folder.Children is { Count: > 0 } kids && kids[0].Row is W7Row first)
                {
                    SelectKb(Zone.Left, first);
                }
                return true;
            }
            {
                List<W7Row> right = RightRows();
                if (right.Count > 0)
                {
                    SelectKb(Zone.Right, right[0]);
                }
            }
            return true;
        case Zone.PowerMain:
            SelectKb(Zone.PowerArrow, _powerArrow);
            return true;
        case Zone.PowerArrow:
            OpenPowerMenu(keyboard: true);
            return true;
        default:
            return true;
        }
    }

    // Left: a folder's child goes to its folder, an expanded folder collapses; the right column and Shut down go back to
    // the last left selection (or the first L row); the arrow goes to Shut down; the Back row goes back. Other left rows
    // and no selection leave the key to the caret.
    private bool KeyLeft()
    {
        switch (_zone)
        {
        case Zone.Left:
            if (ReferenceEquals(_sel, _cmdRow) && _view == View.AllPrograms)
            {
                ToggleAllPrograms();
                SelectKb(Zone.Left, _cmdRow);
                return true;
            }
            if (_sel is W7Row row)
            {
                if (row.ParentRow is W7Row parent && InCurrentList(parent))
                {
                    SelectKb(Zone.Left, parent);
                    return true;
                }
                if (row.Payload is TreeNode folder && folder.IsExpanded)
                {
                    ToggleFolder(folder, expand: false, bringIntoView: false);
                    return true;
                }
            }
            return false;
        case Zone.Right:
        case Zone.PowerMain:
        {
            List<W7Row> left = LeftRows();
            W7Row back = (_lastLeft != null && left.Contains(_lastLeft)) ? _lastLeft : left[0];
            SelectKb(Zone.Left, back);
            return true;
        }
        case Zone.PowerArrow:
            SelectKb(Zone.PowerMain, _powerMain);
            return true;
        default:
            return false;
        }
    }

    // Home/End/PageUp/PageDown inside the list: the first or last row, or a viewport of rows up or down.
    private void KeyJump(Key key)
    {
        List<W7Row> rows = ListRows();
        if (rows.Count == 0)
        {
            return;
        }
        int i = _sel is W7Row cur ? rows.IndexOf(cur) : -1;
        if (i < 0)
        {
            i = rows.Count;   // the command row sits after the last list row
        }
        double rowH = rows[0].Height > 0.0 ? rows[0].Height : 36.0;
        int page = Math.Max(1, (int)Math.Floor(_layout.V / rowH));
        int next = key switch
        {
            Key.Home => 0,
            Key.End => rows.Count - 1,
            Key.PageUp => Math.Max(0, i - page),
            _ => Math.Min(rows.Count - 1, i + page)
        };
        SelectKb(Zone.Left, rows[next]);
    }

    // Tab cycles L (first row) -> R (Name) -> Shut down -> arrow -> nothing (caret in the box) -> L; Shift+Tab reverses.
    private void KeyTab(bool back)
    {
        Zone[] order = { Zone.None, Zone.Left, Zone.Right, Zone.PowerMain, Zone.PowerArrow };
        int i = Math.Max(0, Array.IndexOf(order, _zone));
        Zone next = order[(i + (back ? order.Length - 1 : 1)) % order.Length];
        switch (next)
        {
        case Zone.Left:
            SelectKb(Zone.Left, LeftRows()[0]);
            break;
        case Zone.Right:
        {
            List<W7Row> right = RightRows();
            if (right.Count > 0)
            {
                SelectKb(Zone.Right, right[0]);
            }
            else
            {
                SelectKb(Zone.PowerMain, _powerMain);
            }
            break;
        }
        case Zone.PowerMain:
            SelectKb(Zone.PowerMain, _powerMain);
            break;
        case Zone.PowerArrow:
            SelectKb(Zone.PowerArrow, _powerArrow);
            break;
        default:
            ClearSelection(animate: true);
            break;
        }
    }

    // Enter acts on the highlight. With nothing highlighted it launches the first result of a typed search (or opens the
    // full search when nothing matched); with an empty box it does nothing. Ctrl+Shift+Enter launches elevated. Shut down
    // only runs from Enter when the keyboard moved there: a pointer that merely rests on it never turns Enter into a
    // shutdown.
    private void KeyEnter(bool asAdmin)
    {
        if (_sel != null && !(ReferenceEquals(_sel, _powerMain) && !_selByKeyboard))
        {
            ActivateSelection(asAdmin);
            return;
        }
        string q = _search.Text?.Trim() ?? string.Empty;
        if (q.Length == 0)
        {
            return;
        }
        AppEntry? first = FirstProgramRow();
        if (first != null)
        {
            LaunchProgram(first, asAdmin);
        }
        else
        {
            SeeMoreResults(q);
        }
    }

    private void ActivateSelection(bool asAdmin)
    {
        switch (_sel)
        {
        case W7Row row when ReferenceEquals(row, _cmdRow):
            ActivateCommandRow(byKeyboard: true);
            break;
        case W7Row row when row.Payload is AppEntry a:
            LaunchProgram(a, asAdmin);
            break;
        case W7Row row when row.Payload is TreeNode folder:
            ToggleFolder(folder, expand: null, bringIntoView: true);
            break;
        case W7Row row:
            row.OnActivate?.Invoke();
            break;
        case Button b when ReferenceEquals(b, _powerMain):
            ShutDownCommand();
            break;
        case Button b when ReferenceEquals(b, _powerArrow):
            OpenPowerMenu(keyboard: true);
            break;
        }
    }

    private void OpenAllProgramsByKeyboard()
    {
        ToggleAllPrograms();
        List<W7Row> rows = ListRows();
        SelectKb(Zone.Left, rows.Count > 0 ? rows[0] : _cmdRow);
    }

    // The bottom command row does what its view says: All Programs opens the tree, Back returns to the main view and See
    // more results opens the shell's search window for the typed text.
    private void ActivateCommandRow(bool byKeyboard)
    {
        switch (_view)
        {
        case View.Search:
            SeeMoreResults(_search.Text?.Trim() ?? string.Empty);
            break;
        case View.AllPrograms:
            ToggleAllPrograms();
            if (byKeyboard)
            {
                SelectKb(Zone.Left, _cmdRow);
            }
            break;
        default:
            if (byKeyboard)
            {
                OpenAllProgramsByKeyboard();
            }
            else
            {
                ToggleAllPrograms();
            }
            break;
        }
    }

    // Apps key / Shift+F10: the item menu of the highlighted program, below the row. Otherwise the search box keeps the
    // key (its edit menu).
    private bool KeyItemMenu()
    {
        if (_sel is W7Row row && row.Payload is AppEntry a)
        {
            ShowItemMenu(a, row, row.IsRecent, keyboard: true);
            return true;
        }
        return false;
    }

    // ---- Mouse ------------------------------------------------------------------------------------------------------

    // Hover selects, a press is tracked with mouse capture and activation happens on release over the same row.
    private void WireRow(W7Row row)
    {
        row.Fade = FadeDur;
        row.MouseEnter += OnRowEnter;
        row.MouseLeave += OnRowLeave;
        row.MouseLeftButtonDown += OnRowDown;
        row.MouseMove += OnRowMove;
        row.MouseLeftButtonUp += OnRowUp;
        row.LostMouseCapture += OnRowLostCapture;
    }

    private void OnRowEnter(object sender, MouseEventArgs e)
    {
        if (sender is not W7Row row || (_pressed != null && !ReferenceEquals(_pressed, row)))
        {
            return;
        }
        if (!PointerMoved())
        {
            return;   // the row arrived under a resting pointer; the first real move selects it (OnRowMove)
        }
        Select(ZoneOf(row), row, byKeyboard: false);
    }

    private void OnRowLeave(object sender, MouseEventArgs e)
    {
        if (sender is not W7Row row || ReferenceEquals(_pressed, row))
        {
            return;
        }
        // A keyboard selection survives the mouse leaving it.
        if (ReferenceEquals(_sel, row) && !_selByKeyboard)
        {
            ClearSelection(animate: true);
        }
    }

    private Point PointerIn(W7Row row, MouseEventArgs e)
    {
        return (_qa && _qaPointer.HasValue) ? _qaPointer.Value : e.GetPosition(row);
    }

    private void OnRowDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not W7Row row)
        {
            return;
        }
        e.Handled = true;
        BeginPress(row, PointerIn(row, e));
    }

    private void BeginPress(W7Row row, Point at)
    {
        CancelPress();
        _pressed = row;
        _pressAt = at;
        Select(ZoneOf(row), row, byKeyboard: false);
        row.SetPressed(on: true);
        if (!_qa)
        {
            row.CaptureMouse();
        }
    }

    // Without a press, the first real move over a row selects it (a row that arrived under a resting pointer, or one the
    // keyboard moved away from). With a press, moving further than the drag distance cancels it (there is no drag and
    // drop to start).
    private void OnRowMove(object sender, MouseEventArgs e)
    {
        if (sender is not W7Row row)
        {
            return;
        }
        if (_pressed == null)
        {
            if (!ReferenceEquals(_sel, row) && PointerMoved())
            {
                Select(ZoneOf(row), row, byKeyboard: false);
            }
            return;
        }
        if (!ReferenceEquals(_pressed, row))
        {
            return;
        }
        Point p = PointerIn(row, e);
        double limit = SystemParameters.MinimumHorizontalDragDistance;
        if (Math.Abs(p.X - _pressAt.X) > limit || Math.Abs(p.Y - _pressAt.Y) > limit)
        {
            CancelPress();
        }
    }

    private void OnRowUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is not W7Row row || !ReferenceEquals(_pressed, row))
        {
            return;
        }
        e.Handled = true;
        Point p = PointerIn(row, e);
        bool inside = p.X >= 0.0 && p.Y >= 0.0 && p.X < row.ActualWidth && p.Y < row.ActualHeight;
        CancelPress();
        if (inside)
        {
            ActivateRow(row);
        }
    }

    private void OnRowLostCapture(object sender, MouseEventArgs e)
    {
        if (ReferenceEquals(_pressed, sender))
        {
            CancelPress();
        }
    }

    private void CancelPress()
    {
        W7Row? row = _pressed;
        if (row == null)
        {
            return;
        }
        _pressed = null;   // cleared first: releasing the capture raises LostMouseCapture
        row.SetPressed(on: false);
        if (row.IsMouseCaptured)
        {
            row.ReleaseMouseCapture();
        }
    }

    private void ActivateRow(W7Row row)
    {
        if (ReferenceEquals(row, _cmdRow))
        {
            ActivateCommandRow(byKeyboard: false);
            return;
        }
        if (row.Payload is AppEntry a)
        {
            LaunchProgram(a, asAdmin: false);
            return;
        }
        if (row.Payload is TreeNode folder)
        {
            // A folder opens or closes in place; the menu stays open.
            ToggleFolder(folder, expand: null, bringIntoView: true);
            return;
        }
        row.OnActivate?.Invoke();
    }

    private void OnProgramRightUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is not W7Row row || row.Payload is not AppEntry a)
        {
            return;
        }
        e.Handled = true;
        ShowItemMenu(a, row, row.IsRecent, keyboard: false);
    }

    // ---- Shut down parts --------------------------------------------------------------------------------------------

    // The hot layer fades in over Cat.Hover and out over Cat.Micro; the arrow stays lit while its flyout is open.
    private void SetPowerHot(Button b, bool on, bool animate)
    {
        if (!on && ReferenceEquals(b, _powerArrow) && PowerMenuOpen)
        {
            on = true;
        }
        FadeLayer(b, "HotLayer", on ? 1.0 : 0.0, animate ? (on ? Motion.Cat.Hover : Motion.Cat.Micro) : null);
    }

    private void FadeLayer(Button b, string part, double to, Motion.Cat? cat)
    {
        if (b.Template?.FindName(part, b) is not UIElement layer)
        {
            return;
        }
        double from = layer.Opacity;
        layer.BeginAnimation(OpacityProperty, null);
        layer.Opacity = to;
        Duration? d = cat.HasValue ? FadeDur(cat.Value) : null;
        if (cat.HasValue && d.HasValue && Math.Abs(from - to) > 0.001)
        {
            DoubleAnimation fade = new DoubleAnimation(from, to, d.Value)
            {
                EasingFunction = Motion.Ease(cat.Value),
                FillBehavior = FillBehavior.Stop
            };
            fade.Freeze();
            layer.BeginAnimation(OpacityProperty, fade);
        }
    }

    // Both Shut down parts back to their resting look, instantly (the menu is hidden or about to show).
    private void ResetPowerLayers()
    {
        foreach (Button? b in new[] { _powerMain, _powerArrow })
        {
            if (b != null)
            {
                FadeLayer(b, "HotLayer", 0.0, null);
                FadeLayer(b, "PressLayer", 0.0, null);
            }
        }
    }
}
