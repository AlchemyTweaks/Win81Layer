using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

#nullable enable annotations

namespace Win81Layer;

// Windows 7 Start menu: child menus (the power flyout, the program item menu, the empty-area menu and the search box's
// edit menu). They share one Windows 7 menu look (a square menu with an icon gutter and a glossy blue hot item), parsed
// once and painted from the palette keys copied into each menu, and they all open through OpenChildMenu.
public sealed partial class Win7StartMenu
{
    // Menus whose Opened/Closed handlers are already attached (the power menu is reused for every open).
    private readonly ConditionalWeakTable<ContextMenu, object> _childHooked = new ConditionalWeakTable<ContextMenu, object>();

    // A flyout opened from the keyboard highlights its first item once it is open.
    private bool _focusFirstOnOpen;

    // The menu the lit _menuRow belongs to.
    private ContextMenu? _menuRowMenu;

    private bool PowerMenuOpen => _childMenuOpen && ReferenceEquals(_openChild, _powerMenu);

    // ---- Items ------------------------------------------------------------------------------------------------------

    private static ContextMenu NewMenu()
    {
        ContextMenu cm = new ContextMenu();
        if (W7MenuStyle.Value is Style s)
        {
            cm.Style = s;
        }
        return cm;
    }

    internal static MenuItem W7Item(string header, Action act, bool bold = false, bool enabled = true)
    {
        MenuItem mi = new MenuItem
        {
            Header = header,
            IsEnabled = enabled
        };
        if (bold)
        {
            mi.FontWeight = FontWeights.Bold;
        }
        if (W7MenuItemStyle.Value is Style s)
        {
            mi.Style = s;
        }
        mi.Click += delegate
        {
            act();
        };
        return mi;
    }

    internal static Separator W7Sep()
    {
        Separator sep = new Separator();
        if (W7SeparatorStyle.Value is Style s)
        {
            sep.Style = s;
        }
        return sep;
    }

    // An item that closes Start: the foreground is granted, Start hides at once and the command runs after the menu has
    // closed (QueueCommand runs at Background priority).
    private MenuItem CloseItem(string header, Action act, bool bold = false)
    {
        return W7Item(header, delegate
        {
            _childCommandRan = true;
            LaunchAndClose(header, delegate
            {
                TaskbarContextMenu.QueueCommand(act);
            });
        }, bold);
    }

    // An item that keeps Start open: when the menu closes Start takes the foreground back and the caret returns to the
    // search box (OnChildClosed).
    private MenuItem StayItem(string header, Action act)
    {
        return W7Item(header, delegate
        {
            _childCommandRan = true;
            TaskbarContextMenu.QueueCommand(act);
        });
    }

    // Removes leading, trailing and doubled separators (items left out by gating leave them behind).
    internal static void CollapseSeparators(ContextMenu cm)
    {
        bool previousWasSeparator = true;
        int i = 0;
        while (i < cm.Items.Count)
        {
            bool isSeparator = cm.Items[i] is Separator;
            if (isSeparator && previousWasSeparator)
            {
                cm.Items.RemoveAt(i);
                continue;
            }
            previousWasSeparator = isSeparator;
            i++;
        }
        while (cm.Items.Count > 0 && cm.Items[cm.Items.Count - 1] is Separator)
        {
            cm.Items.RemoveAt(cm.Items.Count - 1);
        }
    }

    // ---- Power flyout -----------------------------------------------------------------------------------------------

    // Windows 7 order: Switch user, Log off, Lock | Restart, Sleep, Hibernate. Sleep and Hibernate only appear when the
    // machine reports them; the raw capability bits go to the log when the menu is built. No icons.
    private ContextMenu BuildPowerMenu()
    {
        (bool _, bool sleep, bool hibernate, string raw) = PowerActions.Capabilities;
        Trace("Win7 Start: power capabilities " + raw);
        ContextMenu cm = NewMenu();
        cm.Items.Add(PowerItem("Switch user", PowerActions.SwitchUser));
        cm.Items.Add(PowerItem("Log off", PowerActions.SignOut));
        cm.Items.Add(PowerItem("Lock", PowerActions.Lock));
        cm.Items.Add(W7Sep());
        cm.Items.Add(PowerItem("Restart", PowerActions.Restart));
        if (sleep)
        {
            cm.Items.Add(PowerItem("Sleep", PowerActions.Sleep));
        }
        if (hibernate)
        {
            cm.Items.Add(PowerItem("Hibernate", PowerActions.Hibernate));
        }
        CollapseSeparators(cm);
        return cm;
    }

    private MenuItem PowerItem(string header, Action act)
    {
        return W7Item(header, delegate
        {
            _childCommandRan = true;
            Dismiss("power " + header, instant: true);
            TaskbarContextMenu.QueueCommand(delegate
            {
                Act("power:" + header, act);
            });
        });
    }

    // The power arrow (click, Enter or Right): the flyout opens right of the arrow, bottom-aligned, and the arrow stays
    // lit while it is open. From the keyboard its first item is highlighted.
    private void OpenPowerMenu(bool keyboard)
    {
        OpenChildMenu(_powerMenu, _powerArrow, PlacementMode.Custom, PlacePowerMenu, focusFirst: keyboard);
        if (PowerMenuOpen)
        {
            SetPowerHot(_powerArrow, on: true, animate: true);
        }
    }

    // First choice: right of the arrow with the visible bottom level with the button (the 4 is the menu's shadow margin).
    // Fallback: left of the whole split button, also bottom-aligned. The callback may be given device pixels, so the DIP
    // constants follow the target's own scale.
    private CustomPopupPlacement[] PlacePowerMenu(Size popupSize, Size targetSize, Point offset)
    {
        double s = _powerArrow.ActualWidth > 0.0 ? targetSize.Width / _powerArrow.ActualWidth : 1.0;
        if (double.IsNaN(s) || s <= 0.0)
        {
            s = 1.0;
        }
        double shadow = 4.0 * s;
        double y = targetSize.Height - (popupSize.Height - shadow);
        double splitLeft = (_powerArrow.Margin.Left - _powerMain.Margin.Left) * s;
        return new CustomPopupPlacement[2]
        {
            new CustomPopupPlacement(new Point(targetSize.Width + 1.0 * s, y), PopupPrimaryAxis.Horizontal),
            new CustomPopupPlacement(new Point(-splitLeft - 1.0 * s - (popupSize.Width - shadow), y), PopupPrimaryAxis.Horizontal)
        };
    }

    // Shut down (the main part): Start closes at once, then the command runs.
    private void ShutDownCommand()
    {
        Dismiss("shut down", instant: true);
        TaskbarContextMenu.QueueCommand(delegate
        {
            Act("power:Shut down", PowerActions.ShutDown);
        });
    }

    // ---- Item menu --------------------------------------------------------------------------------------------------

    // Right-click (at the pointer) or the Apps key (below the row, 8 px in) on a program row. The row stays lit while its
    // menu is open.
    private void ShowItemMenu(AppEntry a, W7Row row, bool isRecent, bool keyboard)
    {
        ContextMenu cm = BuildItemMenu(a, isRecent);
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

    // Windows 7 order: Open (bold), Run as administrator, Open file location | Pin to Taskbar, Pin to Start Menu |
    // Remove from this list, Properties. File items need a file behind the entry (its Start menu shortcut, or the file a
    // shell:AppsFolder path resolves to). Packaged apps (an AppsFolder id with '!' and no file) cannot run elevated, and
    // neither can the launcher's own entries (win81:, launcher://).
    private ContextMenu BuildItemMenu(AppEntry a, bool isRecent)
    {
        AppSettings? snap = SnapshotForRead();
        (string? _, string? file, bool packaged) = ResolveItemPaths(a);
        bool startPinned = PinsRead(snap).Any((string p) => string.Equals(p, a.LaunchPath, StringComparison.OrdinalIgnoreCase));
        bool taskbarPinned = IsTaskbarPinned(a.LaunchPath);
        ContextMenu cm = NewMenu();
        cm.Items.Add(CloseItem("Open", delegate
        {
            LaunchCore(a, asAdmin: false);
        }, bold: true));
        if (!packaged && !IsGlyphEntry(a))
        {
            cm.Items.Add(CloseItem("Run as administrator", delegate
            {
                LaunchCore(a, asAdmin: true);
            }));
        }
        if (file != null)
        {
            cm.Items.Add(CloseItem("Open file location", delegate
            {
                Act("open location " + file, delegate
                {
                    FileShell.OpenLocation(file);
                });
            }));
        }
        cm.Items.Add(W7Sep());
        cm.Items.Add(StayItem(taskbarPinned ? "Unpin from Taskbar" : "Pin to Taskbar", delegate
        {
            ToggleTaskbarPin(a, !taskbarPinned);
        }));
        cm.Items.Add(StayItem(startPinned ? "Unpin from Start Menu" : "Pin to Start Menu", delegate
        {
            ToggleStartPin(a.LaunchPath, !startPinned);
        }));
        cm.Items.Add(W7Sep());
        if (isRecent)
        {
            cm.Items.Add(StayItem("Remove from this list", delegate
            {
                RemoveFromMru(a.LaunchPath);
            }));
        }
        if (file != null)
        {
            cm.Items.Add(CloseItem("Properties", delegate
            {
                Act("properties " + file, delegate
                {
                    FileShell.Properties(file);
                });
            }));
        }
        CollapseSeparators(cm);
        return cm;
    }

    // The shortcut behind an entry (StartMenuIndex keeps it in the folder its category names), the file the item menu's
    // file commands use (that shortcut, or the file a shell:AppsFolder path resolves to) and whether the entry is a
    // packaged app (an AppsFolder id with '!' and no file behind it).
    private (string? Lnk, string? File, bool Packaged) ResolveItemPaths(AppEntry a)
    {
        string? lnk = null;
        try
        {
            lnk = StartMenuIndex.Get().TryGetValue(a.Name, out StartMenuIndex.Info? info) ? info.LnkPath : null;
        }
        catch (Exception ex)
        {
            Trace("Win7 Start: start menu index failed: " + ex.Message);
        }
        string? file = lnk ?? AppInventory.ResolveFileBackedPath(a.LaunchPath);
        const string appsFolder = "shell:AppsFolder\\";
        bool packaged = file == null
            && a.LaunchPath.StartsWith(appsFolder, StringComparison.OrdinalIgnoreCase)
            && a.LaunchPath.IndexOf('!', appsFolder.Length) >= 0;
        return (lnk, file, packaged);
    }

    // ---- Empty area and search box ----------------------------------------------------------------------------------

    // Right-click on empty list space, the All Programs row, the glass or the picture: one item, Properties, which opens
    // the launcher's settings (Windows 7 opened the Start menu properties here). Program rows and the search box have
    // their own menus.
    private void OnEmptyAreaRightUp(object sender, MouseButtonEventArgs e)
    {
        if (e.Handled || IsInSearchBox(e.OriginalSource))
        {
            return;
        }
        e.Handled = true;
        if (!IsVisible || _dismissing || _hidePending)
        {
            return;
        }
        OpenChildMenu(BuildEmptyAreaMenu(), _root, PlacementMode.MousePoint);
    }

    private ContextMenu BuildEmptyAreaMenu()
    {
        ContextMenu cm = NewMenu();
        cm.Items.Add(CloseItem("Properties", delegate
        {
            Act("settings", delegate
            {
                OpenSettings?.Invoke();
            });
        }));
        return cm;
    }

    private bool IsInSearchBox(object? source)
    {
        DependencyObject? d = source as DependencyObject;
        while (d != null)
        {
            if (ReferenceEquals(d, _searchBox))
            {
                return true;
            }
            d = (d is Visual || d is System.Windows.Media.Media3D.Visual3D) ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);
        }
        return false;
    }

    // The search box's edit menu. The TextBox's built-in menu is switched off (ContextMenu = null), so right-click and
    // the Apps key land here and the menu opens through OpenChildMenu like every other child menu: Start stays open
    // while it is up, and a press on one of its items is never taken for an outside click.
    private void OnSearchContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        e.Handled = true;
        if (!IsVisible || _dismissing || _hidePending)
        {
            return;
        }
        // CursorLeft is negative when the menu was invoked from the keyboard: open it above the box, not at the mouse.
        OpenChildMenu(BuildEditMenu(), _search, (e.CursorLeft < 0.0) ? PlacementMode.Top : PlacementMode.MousePoint);
    }

    // Cut, Copy, Paste | Select all, as ApplicationCommands aimed at the search box, so the box's own rules decide what is
    // enabled.
    private ContextMenu BuildEditMenu()
    {
        ContextMenu cm = NewMenu();
        cm.Items.Add(EditItem("Cut", ApplicationCommands.Cut));
        cm.Items.Add(EditItem("Copy", ApplicationCommands.Copy));
        cm.Items.Add(EditItem("Paste", ApplicationCommands.Paste));
        cm.Items.Add(W7Sep());
        cm.Items.Add(EditItem("Select all", ApplicationCommands.SelectAll));
        return cm;
    }

    // In QA, Paste stays a plain enabled item: its command state would read the user's clipboard.
    private MenuItem EditItem(string header, RoutedUICommand command)
    {
        MenuItem mi = new MenuItem
        {
            Header = header
        };
        if (W7MenuItemStyle.Value is Style s)
        {
            mi.Style = s;
        }
        mi.Click += delegate
        {
            _childCommandRan = true;
        };
        if (!(_qa && command == ApplicationCommands.Paste))
        {
            mi.CommandTarget = _search;
            mi.Command = command;
        }
        return mi;
    }

    // ---- Opening and closing ----------------------------------------------------------------------------------------

    // Popups are outside the window's resource scope: the palette keys are copied into the menu, and every item gets its
    // Win7 style explicitly (a MenuItem-typed container style would throw on separators).
    private void PrepareChildMenu(ContextMenu cm)
    {
        Win7Palette.CopyPopupKeys(_paletteDict, cm.Resources);
        ApplyMenuStyles(cm);
    }

    private static void ApplyMenuStyles(ContextMenu cm)
    {
        if (W7MenuStyle.Value is Style ms && !ReferenceEquals(cm.Style, ms))
        {
            cm.Style = ms;
        }
        foreach (object item in cm.Items)
        {
            if (item is MenuItem mi)
            {
                if (W7MenuItemStyle.Value is Style s && !ReferenceEquals(mi.Style, s))
                {
                    mi.Style = s;
                }
            }
            else if (item is Separator sep)
            {
                if (W7SeparatorStyle.Value is Style s && !ReferenceEquals(sep.Style, s))
                {
                    sep.Style = s;
                }
            }
        }
    }

    // The single way a child menu opens. While it is open Start is not topmost (so the menu can sit above it) and
    // Deactivated is ignored; every exit path comes back through OnChildClosed, which restores both. focusFirst: opened
    // from the keyboard, so its first item is highlighted once it is open.
    private void OpenChildMenu(ContextMenu cm, UIElement target, PlacementMode mode, CustomPopupPlacementCallback? cb = null, bool focusFirst = false)
    {
        PrepareChildMenu(cm);
        if (_qa)
        {
            // The harness never opens real popups: it records the request (and can force the failure path).
            _qaChildRequests.Add((cm, target, mode, focusFirst));
            if (!_qaForceChildOpenFail)
            {
                return;
            }
        }
        if (!_childHooked.TryGetValue(cm, out _))
        {
            _childHooked.Add(cm, true);
            cm.Opened += OnChildOpened;
            cm.Closed += OnChildClosed;
        }
        // Closing the previous menu runs its OnChildClosed, which clears the flag; it is set for this menu afterwards.
        CloseChildMenu();
        _focusFirstOnOpen = focusFirst;
        cm.PlacementTarget = target;
        cm.Placement = mode;
        cm.CustomPopupPlacementCallback = cb;
        _openChild = cm;
        _childMenuOpen = true;
        _childCommandRan = false;
        Topmost = false;
        if (!_qa)
        {
            try
            {
                cm.IsOpen = true;
            }
            catch (Exception ex)
            {
                Trace("Win7 Start: child menu failed to open: " + ex.Message);
            }
        }
        if (!cm.IsOpen)
        {
            OnChildClosed(cm, null);
        }
    }

    private void OnChildOpened(object? sender, RoutedEventArgs e)
    {
        if (!ReferenceEquals(sender, _openChild) || sender is not ContextMenu cm)
        {
            return;
        }
        try
        {
            if (PresentationSource.FromVisual(cm) is HwndSource src && GetWindowRect(src.Handle, out NativeRect r))
            {
                _childRectPx = System.Drawing.Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom);
            }
        }
        catch
        {
        }
        if (_focusFirstOnOpen)
        {
            _focusFirstOnOpen = false;
            FocusFirstItem(cm);
        }
    }

    // The first enabled item takes keyboard focus, which highlights it (MenuItem.IsHighlighted, the same look as hover).
    // The harness cannot focus a menu that is laid out detached, so it sets that highlight state directly.
    private void FocusFirstItem(ContextMenu cm)
    {
        MenuItem? first = cm.Items.OfType<MenuItem>().FirstOrDefault((MenuItem m) => m.IsEnabled);
        if (first == null)
        {
            return;
        }
        if (_qa)
        {
            QaSetHighlighted(first, on: true);
            return;
        }
        // After the menu's own open handling has placed focus in the menu.
        Dispatcher.BeginInvoke(DispatcherPriority.Input, (Action)delegate
        {
            if (cm.IsOpen)
            {
                first.Focus();
            }
        });
    }

    private void OnChildClosed(object? sender, RoutedEventArgs? e)
    {
        if (!ReferenceEquals(sender, _openChild))
        {
            return;
        }
        bool wasPower = ReferenceEquals(sender, _powerMenu);
        W7Row? menuRow = null;
        if (ReferenceEquals(sender, _menuRowMenu))
        {
            menuRow = _menuRow;
            _menuRow = null;
            _menuRowMenu = null;
        }
        _openChild = null;
        _childMenuOpen = false;
        _focusFirstOnOpen = false;
        _childClosedTick = Environment.TickCount;
        Topmost = true;
        // The arrow and the item menu's row stop being lit, unless they are the selection or under the mouse.
        if (wasPower && !ReferenceEquals(_sel, _powerArrow) && !_powerArrow.IsMouseOver)
        {
            SetPowerHot(_powerArrow, on: false, animate: true);
        }
        if (menuRow != null && !ReferenceEquals(_sel, menuRow) && !menuRow.IsMouseOver)
        {
            menuRow.SetHot(on: false, animate: true);
        }
        if (_qa)
        {
            return;   // the harness window never takes the foreground
        }
        Dispatcher.BeginInvoke(DispatcherPriority.Input, (Action)delegate
        {
            // _childMenuOpen: a newer child menu opened after this one closed (OpenChildMenu closes the old menu first)
            // and now owns activation; its own OnChildClosed decides what happens next.
            if (!IsVisible || _dismissing || _hidePending || _childMenuOpen)
            {
                return;
            }
            if (_childCommandRan)
            {
                // A command that keeps Start open (pin, unpin, remove, edit): take the foreground back, caret in the box.
                if (!IsActive)
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
                }
                _search.Focus();
                return;
            }
            if (!IsActive)
            {
                Dismiss("child menu closed while inactive");
            }
        });
    }

    private void CloseChildMenu()
    {
        ContextMenu? cm = _openChild;
        if (cm != null && cm.IsOpen)
        {
            try
            {
                cm.IsOpen = false;
            }
            catch
            {
            }
        }
    }

    // ---- Styles, parsed once ----------------------------------------------------------------------------------------

    // Popup: a 4 px transparent margin on the right and bottom holds a two-step painted shadow (offsets 2 and 4); the menu
    // is W7.MenuBg with a 1 px border and 2 px padding, and a 26 px gutter with a two-tone line at x 26 and 27.
    private static readonly Lazy<Style?> W7MenuStyle = new Lazy<Style?>(() => ParseXaml<Style>(
        "<Style " + XamlNs + " TargetType=\"ContextMenu\">" +
        "<Setter Property=\"OverridesDefaultStyle\" Value=\"True\"/>" +
        "<Setter Property=\"HasDropShadow\" Value=\"False\"/>" +
        "<Setter Property=\"SnapsToDevicePixels\" Value=\"True\"/>" +
        "<Setter Property=\"UseLayoutRounding\" Value=\"True\"/>" +
        "<Setter Property=\"TextOptions.TextFormattingMode\" Value=\"Display\"/>" +
        "<Setter Property=\"FontFamily\" Value=\"Segoe UI\"/>" +
        "<Setter Property=\"FontSize\" Value=\"12\"/>" +
        "<Setter Property=\"Foreground\" Value=\"{DynamicResource W7.MenuInk}\"/>" +
        "<Setter Property=\"Grid.IsSharedSizeScope\" Value=\"True\"/>" +
        "<Setter Property=\"KeyboardNavigation.TabNavigation\" Value=\"Cycle\"/>" +
        "<Setter Property=\"KeyboardNavigation.DirectionalNavigation\" Value=\"Cycle\"/>" +
        "<Setter Property=\"Template\"><Setter.Value>" +
        "<ControlTemplate TargetType=\"ContextMenu\">" +
        "<Grid Margin=\"0,0,4,4\">" +
        "<Border Margin=\"4,4,-4,-4\" Background=\"{DynamicResource W7.MenuShadow2}\"/>" +
        "<Border Margin=\"2,2,-2,-2\" Background=\"{DynamicResource W7.MenuShadow1}\"/>" +
        "<Border Background=\"{DynamicResource W7.MenuBg}\" BorderBrush=\"{DynamicResource W7.MenuBorder}\" BorderThickness=\"1\" Padding=\"2\">" +
        "<Grid>" +
        "<Rectangle Width=\"26\" HorizontalAlignment=\"Left\" Fill=\"{DynamicResource W7.MenuGutter}\"/>" +
        "<Rectangle Width=\"1\" HorizontalAlignment=\"Left\" Margin=\"26,0,0,0\" Fill=\"{DynamicResource W7.MenuGutterLine}\"/>" +
        "<Rectangle Width=\"1\" HorizontalAlignment=\"Left\" Margin=\"27,0,0,0\" Fill=\"{DynamicResource W7.MenuGutterLine2}\"/>" +
        "<ItemsPresenter KeyboardNavigation.DirectionalNavigation=\"Cycle\"/>" +
        "</Grid>" +
        "</Border>" +
        "</Grid>" +
        "</ControlTemplate>" +
        "</Setter.Value></Setter>" +
        "</Style>"));

    // Item: at least 22 tall, an optional 16 px icon centred in the gutter, text at x 33 with 20 px on the right. The hot
    // rect (Margin 1, radius 2, glossy blue with an inner light line) shows for hover and keyboard highlight alike.
    private static readonly Lazy<Style?> W7MenuItemStyle = new Lazy<Style?>(() => ParseXaml<Style>(
        "<Style " + XamlNs + " TargetType=\"MenuItem\">" +
        "<Setter Property=\"OverridesDefaultStyle\" Value=\"True\"/>" +
        "<Setter Property=\"SnapsToDevicePixels\" Value=\"True\"/>" +
        "<Setter Property=\"Foreground\" Value=\"{DynamicResource W7.MenuInk}\"/>" +
        "<Setter Property=\"FontSize\" Value=\"12\"/>" +
        "<Setter Property=\"MinHeight\" Value=\"22\"/>" +
        "<Setter Property=\"Template\"><Setter.Value>" +
        "<ControlTemplate TargetType=\"MenuItem\">" +
        "<Grid Background=\"Transparent\" MinHeight=\"22\">" +
        "<Border x:Name=\"Hot\" Margin=\"1\" CornerRadius=\"2\" BorderThickness=\"1\" Opacity=\"0\" Background=\"{DynamicResource W7.MenuHotFill}\" BorderBrush=\"{DynamicResource W7.MenuHotStroke}\">" +
        "<Border CornerRadius=\"1\" BorderThickness=\"1\" BorderBrush=\"{DynamicResource W7.MenuHotInner}\"/>" +
        "</Border>" +
        "<ContentPresenter ContentSource=\"Icon\" Width=\"16\" Height=\"16\" Margin=\"5,0,0,0\" HorizontalAlignment=\"Left\" VerticalAlignment=\"Center\"/>" +
        "<ContentPresenter ContentSource=\"Header\" RecognizesAccessKey=\"True\" Margin=\"33,0,20,0\" VerticalAlignment=\"Center\"/>" +
        "</Grid>" +
        "<ControlTemplate.Triggers>" +
        "<Trigger Property=\"IsHighlighted\" Value=\"True\"><Setter TargetName=\"Hot\" Property=\"Opacity\" Value=\"1\"/></Trigger>" +
        "<Trigger Property=\"IsEnabled\" Value=\"False\"><Setter Property=\"Foreground\" Value=\"{DynamicResource W7.MenuInkDisabled}\"/></Trigger>" +
        "</ControlTemplate.Triggers>" +
        "</ControlTemplate>" +
        "</Setter.Value></Setter>" +
        "</Style>"));

    // Separator: from x 30 (just after the gutter) to 2 px before the edge, a dark line over a light one.
    private static readonly Lazy<Style?> W7SeparatorStyle = new Lazy<Style?>(() => ParseXaml<Style>(
        "<Style " + XamlNs + " TargetType=\"Separator\">" +
        "<Setter Property=\"OverridesDefaultStyle\" Value=\"True\"/>" +
        "<Setter Property=\"Focusable\" Value=\"False\"/>" +
        "<Setter Property=\"Margin\" Value=\"30,3,2,3\"/>" +
        "<Setter Property=\"Template\"><Setter.Value>" +
        "<ControlTemplate TargetType=\"Separator\">" +
        "<StackPanel SnapsToDevicePixels=\"True\">" +
        "<Rectangle Height=\"1\" Fill=\"{DynamicResource W7.MenuSep}\"/>" +
        "<Rectangle Height=\"1\" Fill=\"{DynamicResource W7.MenuSep2}\"/>" +
        "</StackPanel>" +
        "</ControlTemplate>" +
        "</Setter.Value></Setter>" +
        "</Style>"));
}
