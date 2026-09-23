#if METRO_CONTROL_PROBE
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Xml.Linq;
using Path = System.IO.Path;

namespace Win81Layer;

// Compiled ONLY by tests/MetroControlProbe: renders detached WPF trees, never shows a window.
internal static class MetroControlProbe
{
    private static readonly Dictionary<string, bool> Checks = new();
    private static readonly List<string> Errors = new();
    private static readonly List<string> Images = new();
    private static Application app;
    private static string output;

    [STAThread]
    public static int Main(string[] args)
    {
        output = Path.GetFullPath(args.Length > 0 ? args[0] : Path.Combine(AppContext.BaseDirectory, "screenshots"));
        Directory.CreateDirectory(output);
        app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        try
        {
            app.Resources.MergedDictionaries.Add(MetroControlStyles81.CreateDictionary());
            app.Resources["MenuAccent"] = new SolidColorBrush(Color.FromRgb(0, 138, 230));
            VerifyBehavior();
            VerifyListViews();
            VerifyAppBarContrast();
            VerifyPanningIndicator();
            foreach (int dpi in new[] { 96, 144, 192 }) Save(BuildAtlas(dpi), $"controls-{dpi}dpi.png", 1180, 980, dpi);
            Save(BuildStates(), "states-96dpi.png", 1180, 720, 96);
            app.Resources["MenuAccent"] = new SolidColorBrush(Color.FromRgb(0, 138, 64));
            Save(BuildAtlas(), "controls-green-96dpi.png", 1180, 980, 96);
        }
        catch (Exception ex) { Errors.Add(ex.ToString()); }
        bool passed = Errors.Count == 0 && Checks.Count > 0 && Checks.Values.All(v => v);
        File.WriteAllText(Path.Combine(output, "report.json"), JsonSerializer.Serialize(new
        {
            Passed = passed, Isolation = "Detached WPF visuals and an unshown managed Window for application resource invalidation; no native windows, App.cs, hooks, input injection, settings, services, or deployment.",
            Checks, Errors, Images,
            Limits = new[] { "Forced state screenshots are labeled; they do not prove OS focus or pointer delivery.", "Popup children are rendered detached without opening native popups.", "Horizontal slider only; appbar/flip/semantic zoom are command chrome, not navigation engines." }
        }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"Metro controls: {(passed ? "PASS" : "FAIL")}; {Checks.Count(c => c.Value)}/{Checks.Count} checks; {output}");
        foreach (var check in Checks.Where(c => !c.Value)) Console.WriteLine("FAIL " + check.Key);
        foreach (string error in Errors) Console.WriteLine(error);
        app.Shutdown(passed ? 0 : 2);
        return passed ? 0 : 2;
    }

    private static T Styled<T>(T control, string key) where T : FrameworkElement
    {
        control.Style = (Style)app.FindResource("Metro81." + key);
        return control;
    }

    private static TextBlock Text(string value, int size = 15) => new()
    { Text = value, FontFamily = new FontFamily("Segoe UI"), FontSize = size, Foreground = Brushes.Black, TextWrapping = TextWrapping.Wrap };

    private static StackPanel Section(Panel parent, string title)
    {
        var section = new StackPanel { Margin = new Thickness(0, 0, 24, 22) };
        var heading = Text(title, 22); heading.Margin = new Thickness(0, 0, 0, 10);
        section.Children.Add(heading); parent.Children.Add(section); return section;
    }

    private static StackPanel Row(params UIElement[] children)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (UIElement child in children) { if (child is FrameworkElement element) element.Margin = new Thickness(0, 0, 14, 0); row.Children.Add(child); }
        return row;
    }

    private static FrameworkElement BuildAtlas(int dpi = 96)
    {
        var root = new DockPanel { Background = new SolidColorBrush(Color.FromRgb(242, 242, 242)), LastChildFill = true };
        var heading = Text("Windows 8 / 8.1 common Metro controls", 34); heading.Margin = new Thickness(28, 20, 28, 8); DockPanel.SetDock(heading, Dock.Top); root.Children.Add(heading);
        var note = Text("ISOLATED WPF PROBE  |  synthetic content  |  100% / 150% / 200% raster validation", 13); note.Margin = new Thickness(28, 0, 28, 22); DockPanel.SetDock(note, Dock.Top); root.Children.Add(note);
        var appbar = Styled(new Border(), "AppBar"); DockPanel.SetDock(appbar, Dock.Bottom);
        var commands = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var (glyph, label) in new[] { ("\u2302", "Home"), ("+", "Add"), ("\u25a6", "Schedule"), ("?", "Help") })
        {
            var command = Styled(new Button { Content = new TextBlock { Text = glyph, FontSize = 23 }, Width = 88 }, "AppBarButton");
            MetroControlStyles81.SetLabel(command, label); commands.Children.Add(command);
        }
        appbar.Child = commands; root.Children.Add(appbar);
        var columns = new Grid { Margin = new Thickness(28, 0, 4, 0) };
        for (int i = 0; i < 3; i++) columns.ColumnDefinitions.Add(new ColumnDefinition());
        var left = new StackPanel(); var middle = new StackPanel(); var right = new StackPanel();
        Grid.SetColumn(middle, 1); Grid.SetColumn(right, 2); columns.Children.Add(left); columns.Children.Add(middle); columns.Children.Add(right); root.Children.Add(columns);

        Section(left, "Button").Children.Add(Row(Styled(new Button { Content = "OK" }, "Button"), Styled(new Button { Content = "Submit query" }, "AccentButton")));
        Section(left, "Checkbox").Children.Add(Row(Styled(new CheckBox { Content = "Off" }, "CheckBox"), Styled(new CheckBox { Content = "On", IsChecked = true }, "CheckBox"), Styled(new CheckBox { Content = "Mixed", IsThreeState = true, IsChecked = null }, "CheckBox")));
        Section(left, "Radio button").Children.Add(Row(Styled(new RadioButton { Content = "First", GroupName = "atlas" }, "RadioButton"), Styled(new RadioButton { Content = "Second", IsChecked = true, GroupName = "atlas" }, "RadioButton")));
        Section(left, "Combo box").Children.Add(Styled(new ComboBox { ItemsSource = new[] { "California", "Washington", "New York" }, SelectedIndex = 0 }, "ComboBox"));
        Section(left, "Toggle switch").Children.Add(Row(Styled(new ToggleButton { Content = "Off", IsChecked = false }, "ToggleSwitch"), Styled(new ToggleButton { Content = "On", IsChecked = true }, "ToggleSwitch")));
        var list = Styled(new ListBox { ItemsSource = new[] { "Apple", "Banana", "Grape", "Orange", "Watermelon", "Pear", "Peach" }, SelectedIndex = 2, Height = 156 }, "ListBox");
        Section(left, "List box / scroll").Children.Add(list);

        var textbox = Styled(new TextBox { Text = "Hello //BUILD/ attendees!" }, "TextBox");
        Section(middle, "Text box / clear").Children.Add(textbox);
        var password = Styled(new PasswordBox { Password = "Example only" }, "PasswordBox");
        Section(middle, "Password / hold to reveal").Children.Add(password);
        Section(middle, "Editable combo box").Children.Add(Styled(new ComboBox { ItemsSource = new[] { "Redmond, WA", "Seattle, WA" }, Text = "Redmond, WA", IsEditable = true }, "ComboBox"));
        var progress = Section(middle, "Progress"); progress.Children.Add(Styled(new ProgressBar { Value = 65, Margin = new Thickness(0, 8, 0, 18) }, "ProgressBar"));
        progress.Children.Add(Row(Styled(new ProgressBar(), "ProgressRing"), Styled(new ProgressBar { IsIndeterminate = true, Width = 210, Margin = new Thickness(0, 18, 0, 0) }, "ProgressBar")));
        Section(middle, "Slider").Children.Add(Styled(new Slider { Value = 75, Maximum = 100, AutoToolTipPlacement = AutoToolTipPlacement.TopLeft }, "Slider"));
        Section(middle, "Horizontal scroll bar").Children.Add(Styled(new ScrollBar { Orientation = Orientation.Horizontal, Maximum = 100, Value = 45, ViewportSize = 50 }, "ScrollBar"));
        var tooltip = Styled(new ToolTip { Content = "Microsoft Corporation\nwww.microsoft.com" }, "ToolTip");
        Section(middle, "Tooltip (detached content)").Children.Add(new Image { Source = Render(tooltip, 326, 64, dpi), Width = 326, Height = 64, HorizontalAlignment = HorizontalAlignment.Left });

        var menu = new StackPanel(); menu.Children.Add(Styled(new MenuItem { Header = "Cut", InputGestureText = "Ctrl+X" }, "MenuItem"));
        var selected = Styled(new MenuItem { Header = "Copy", InputGestureText = "Ctrl+C" }, "MenuItem"); Force(selected, UIElement.IsMouseOverProperty, true); Force(selected, MenuItem.IsHighlightedProperty, true); menu.Children.Add(selected);
        menu.Children.Add(Styled(new MenuItem { Header = "Paste", IsEnabled = false }, "MenuItem"));
        menu.Children.Add(Styled(new MenuItem { Header = "Show details", IsCheckable = true, IsChecked = true }, "MenuItem"));
        Section(right, "Context menu / inverse highlight").Children.Add(new Border { BorderBrush = Brushes.Black, BorderThickness = new Thickness(2), Child = menu });
        var flyout = Styled(new Border(), "Flyout"); var flyoutBody = new StackPanel(); flyoutBody.Children.Add(Text("Enter a city"));
        flyoutBody.Children.Add(Styled(new TextBox { Text = "Redmond, WA", Margin = new Thickness(0, 10, 0, 16) }, "TextBox"));
        flyoutBody.Children.Add(Styled(new Button { Content = "Add", HorizontalAlignment = HorizontalAlignment.Right }, "AccentButton")); flyout.Child = flyoutBody;
        Section(right, "Flyout content").Children.Add(flyout);
        Section(right, "Flip / semantic zoom commands").Children.Add(Row(Styled(new Button { Content = "\u276e", ToolTip = "Previous" }, "FlipButton"), Styled(new Button { Content = "\u276f", ToolTip = "Next" }, "FlipButton"), Styled(new Button(), "SemanticZoomButton")));
        var hyperlink = Styled(new System.Windows.Documents.Hyperlink(new System.Windows.Documents.Run("www.buildwindows.com")), "Hyperlink");
        var linkText = Text(""); linkText.Inlines.Add(hyperlink); Section(right, "Hyperlink").Children.Add(linkText);
        return root;
    }

    private static System.Windows.Documents.Hyperlink Styled(System.Windows.Documents.Hyperlink link, string key)
    { link.Style = (Style)app.FindResource("Metro81." + key); return link; }

    private static FrameworkElement BuildStates()
    {
        var root = new StackPanel { Background = Brushes.White, Margin = new Thickness(0) };
        var title = Text("State matrix: forced WPF properties, no live input", 28); title.Margin = new Thickness(24); root.Children.Add(title);
        var grid = new Grid { Margin = new Thickness(24, 0, 24, 0) }; root.Children.Add(grid);
        foreach (double width in new[] { 140d, 188, 188, 188, 188, 188 }) grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(width) });
        string[] states = { "Normal", "Hover", "Pressed", "Focus within", "Disabled" };
        for (int i = 0; i < 9; i++) grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(62) });
        for (int i = 0; i < states.Length; i++) { var label = Text(states[i], 17); Grid.SetColumn(label, i + 1); grid.Children.Add(label); }
        (string Label, Func<Control> Make)[] controls =
        {
            ("Button", () => Styled(new Button { Content = "OK" }, "Button")),
            ("Accent", () => Styled(new Button { Content = "Submit" }, "AccentButton")),
            ("Checkbox", () => Styled(new CheckBox { Content = "Selected", IsChecked = true }, "CheckBox")),
            ("Radio", () => Styled(new RadioButton { Content = "Selected", IsChecked = true, GroupName = Guid.NewGuid().ToString() }, "RadioButton")),
            ("Toggle", () => Styled(new ToggleButton { Content = "On", IsChecked = true }, "ToggleSwitch")),
            ("Text", () => Styled(new TextBox { Text = "Editable" }, "TextBox")),
            ("Password", () => Styled(new PasswordBox { Password = "Example" }, "PasswordBox")),
            ("Combo", () => Styled(new ComboBox { ItemsSource = new[] { "California" }, SelectedIndex = 0 }, "ComboBox"))
        };
        for (int row = 0; row < controls.Length; row++)
        {
            var label = Text(controls[row].Label); Grid.SetRow(label, row + 1); grid.Children.Add(label);
            for (int column = 0; column < states.Length; column++)
            {
                Control control = controls[row].Make(); control.Margin = new Thickness(0, 0, 20, 16); control.VerticalAlignment = VerticalAlignment.Center;
                Grid.SetRow(control, row + 1); Grid.SetColumn(control, column + 1); grid.Children.Add(control);
                if (column == 1) Force(control, UIElement.IsMouseOverProperty, true);
                if (column == 2 && control is ButtonBase) Force(control, ButtonBase.IsPressedProperty, true);
                if (column == 3) Force(control, UIElement.IsKeyboardFocusWithinProperty, true);
                if (column == 4) control.IsEnabled = false;
            }
        }
        return root;
    }

    private static void VerifyBehavior()
    {
        var root = new StackPanel();
        var resourceOwner = new Window { Content = root };
        MetroPatternTheme.Apply(root); MetroPatternTheme.Apply(root); MetroPatternTheme.Install(app);
        Check("Scope applies idempotently", root.Resources[typeof(Button)] is Style && root.Resources.MergedDictionaries.Count == 0);
        Check("No application-wide implicit form styles", !app.Resources.Contains(typeof(Button)) && !app.Resources.Contains(typeof(CheckBox)));
        var original = new Style(typeof(Button)); var legacyRoot = new Grid(); legacyRoot.Resources[typeof(Button)] = original; MetroControlStyles81.ApplyTo(legacyRoot);
        Check("Existing local styles preserved", ReferenceEquals(original, legacyRoot.Resources[typeof(Button)]));

        int changed = 0; var toggle = MetroControlStyles81.CreateToggle(false, _ => changed++, "Notifications"); root.Children.Add(toggle); Layout(root, 400, 500);
        var togglePeer = new ToggleButtonAutomationPeer(toggle); ((IToggleProvider)togglePeer.GetPattern(PatternInterface.Toggle)).Toggle();
        Check("Toggle automation invokes callback once", toggle.IsChecked == true && changed == 1 && Equals(toggle.Content, "On"));
        Check("Toggle accessible name", togglePeer.GetName() == "Notifications");
        Check("Toggle hit target", toggle.ActualHeight >= 40 && toggle.ActualWidth >= 72);
        Layout(root, 400, 500);
        double onRailX = Part<Grid>(toggle, "Switch").TranslatePoint(new Point(), toggle).X;
        var keyboardSource = new DetachedSource { RootVisual = root };
        Force(toggle, UIElement.IsKeyboardFocusedProperty, true);
        SendKey(toggle, keyboardSource, Key.Space, true); SendKey(toggle, keyboardSource, Key.Space, false);
        Check("Toggle Space key invokes callback once", toggle.IsChecked == false && changed == 2);
        Layout(root, 400, 500);
        Check("On-Off label keeps rail position stable", Math.Abs(onRailX - Part<Grid>(toggle, "Switch").TranslatePoint(new Point(), toggle).X) < 0.01);
        toggle.IsChecked = true;
        var fill = Part<Border>(toggle, "Fill");
        Check("Initial accent", ((SolidColorBrush)fill.Background).Color == Color.FromRgb(0, 138, 230));
        app.Resources["MenuAccent"] = Brushes.Crimson; Layout(root, 400, 500);
        Check("Existing toggle responds to accent replacement", ((SolidColorBrush)fill.Background).Color == Colors.Crimson);
        app.Resources["MenuAccent"] = new SolidColorBrush(Color.FromRgb(0, 138, 230));

        var model = new TextModel { Value = "Clear me" }; var field = Styled(new TextBox(), "TextBox"); root.Children.Add(field);
        field.SetBinding(TextBox.TextProperty, new Binding(nameof(TextModel.Value)) { Source = model, Mode = BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged });
        Layout(root, 400, 500);
        var clear = Part<Button>(field, "Clear");
        Check("Clear button command targets editor", ReferenceEquals(clear.CommandTarget, field) && clear.ActualWidth >= 32);
        Check("Clear command enabled", MetroControlStyles81.ClearTextCommand.CanExecute(null, field));
        MetroControlStyles81.ClearTextCommand.Execute(null, field);
        Check("Clear updates source without detaching binding", field.Text == "" && model.Value == "" && BindingOperations.IsDataBound(field, TextBox.TextProperty));
        field.Text = "Read only"; field.IsReadOnly = true; Layout(root, 400, 500);
        Check("Read-only clear unavailable", !MetroControlStyles81.ClearTextCommand.CanExecute(null, field) && clear.Visibility == Visibility.Collapsed);
        field.IsReadOnly = false; field.AcceptsReturn = true; Layout(root, 400, 500);
        Check("Multiline clear unavailable", !MetroControlStyles81.ClearTextCommand.CanExecute(null, field) && clear.Visibility == Visibility.Collapsed);

        var password = Styled(new PasswordBox { Password = "synthetic-password" }, "PasswordBox"); root.Children.Add(password); Layout(root, 400, 600);
        var reveal = Part<MetroPasswordRevealButton81>(password, "Reveal"); var revealed = Part<TextBlock>(password, "PART_RevealedText");
        Check("Password initially concealed", revealed.Text.Length == 0 && revealed.Visibility == Visibility.Collapsed);
        Force(reveal, ButtonBase.IsPressedProperty, true);
        Check("Held reveal shows synthetic password", revealed.Text == password.Password && revealed.Visibility == Visibility.Visible);
        Force(reveal, ButtonBase.IsPressedProperty, false);
        Check("Release erases reveal text", revealed.Text.Length == 0 && Part<FrameworkElement>(password, "PART_ContentHost").Opacity == 1);
        Force(reveal, ButtonBase.IsPressedProperty, true); password.Password = "changed";
        Check("Password change updates held reveal", revealed.Text == "changed");
        password.IsEnabled = false;
        Check("Disabling erases reveal text", revealed.Text.Length == 0 && revealed.Visibility == Visibility.Collapsed);
        Force(reveal, ButtonBase.IsPressedProperty, false); password.IsEnabled = true;
        Force(reveal, ButtonBase.IsPressedProperty, true); reveal.RaiseEvent(new RoutedEventArgs(FrameworkElement.UnloadedEvent));
        Check("Unloading erases reveal text", revealed.Text.Length == 0);
        Force(reveal, ButtonBase.IsPressedProperty, false);
        Force(reveal, UIElement.IsKeyboardFocusedProperty, true);
        SendKey(reveal, keyboardSource, Key.Space, true);
        Check("Password Space hold reveals", revealed.Text == password.Password);
        SendKey(reveal, keyboardSource, Key.Space, false);
        Check("Password Space release conceals", revealed.Text.Length == 0);
        Force(reveal, ButtonBase.IsPressedProperty, true);
        reveal.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, Environment.TickCount) { RoutedEvent = Mouse.LostMouseCaptureEvent });
        Check("Lost pointer capture erases reveal text", revealed.Text.Length == 0);
        Force(reveal, ButtonBase.IsPressedProperty, false);
        Force(reveal, ButtonBase.IsPressedProperty, true);
        reveal.RaiseEvent(new KeyboardFocusChangedEventArgs(Keyboard.PrimaryDevice, Environment.TickCount, reveal, field) { RoutedEvent = Keyboard.LostKeyboardFocusEvent });
        Check("Focus leaving reveal erases reveal text", revealed.Text.Length == 0);
        Force(reveal, ButtonBase.IsPressedProperty, false);

        var radioA = Styled(new RadioButton { GroupName = "behavior", IsChecked = true }, "RadioButton");
        var radioB = Styled(new RadioButton { GroupName = "behavior" }, "RadioButton"); root.Children.Add(radioA); root.Children.Add(radioB); Layout(root, 400, 800);
        ((ISelectionItemProvider)new RadioButtonAutomationPeer(radioB).GetPattern(PatternInterface.SelectionItem)).Select();
        Check("Radio mutual exclusion via automation", radioB.IsChecked == true && radioA.IsChecked == false);
        var check = Styled(new CheckBox { IsThreeState = true }, "CheckBox"); root.Children.Add(check); Layout(root, 400, 800);
        var checkProvider = (IToggleProvider)new CheckBoxAutomationPeer(check).GetPattern(PatternInterface.Toggle); checkProvider.Toggle(); checkProvider.Toggle();
        Check("Checkbox supports indeterminate", check.IsChecked == null && Part<System.Windows.Shapes.Rectangle>(check, "Mixed").Visibility == Visibility.Visible);
        Force(check, UIElement.IsKeyboardFocusedProperty, true); SendKey(check, keyboardSource, Key.Space, true); SendKey(check, keyboardSource, Key.Space, false);
        Check("Checkbox Space advances tri-state", check.IsChecked == false);
        var commandButton = Styled(new Button { Content = "OK" }, "Button"); int clicks = 0; commandButton.Click += (_, _) => clicks++;
        Force(commandButton, UIElement.IsKeyboardFocusedProperty, true); SendKey(commandButton, keyboardSource, Key.Space, true); SendKey(commandButton, keyboardSource, Key.Space, false);
        Check("Button Space invokes click", clicks == 1);
        Check("Keyboard focus style retained on form controls", toggle.FocusVisualStyle != null && check.FocusVisualStyle != null && commandButton.FocusVisualStyle != null && field.FocusVisualStyle != null);
        Save(new Control { Style = (Style)app.FindResource("Metro81.Focus") }, "focus-adorner-template.png", 160, 36, 96);

        var combo = Styled(new ComboBox { ItemsSource = new[] { "One", "Two" }, SelectedIndex = 1, IsEditable = true }, "ComboBox"); root.Children.Add(combo); Layout(root, 400, 900);
        Check("Editable combo retains native parts", Part<TextBox>(combo, "PART_EditableTextBox").Visibility == Visibility.Visible && Part<Popup>(combo, "PART_Popup") != null);
        Check("Editable combo internal editor avoids nested field style", Part<TextBox>(combo, "PART_EditableTextBox").Style == null);
        var comboItem = Styled(new ComboBoxItem { Content = "One" }, "ComboBoxItem"); Layout(comboItem, 240, 36);
        Save(new Border { Background = Brushes.White, Child = comboItem }, "combo-popup-item.png", 240, 36, 96);
        int comboChanges = 0;
        var factoryCombo = MetroPatternTheme.CreateComboBox(new[] { "First", "Second" }, 99, _ => comboChanges++, "Taskbar position");
        root.Children.Add(factoryCombo); Layout(root, 400, 950);
        Check("Combo factory clamps without initial callback", factoryCombo.SelectedIndex == 1 && comboChanges == 0);
        factoryCombo.SelectedIndex = 0;
        Check("Combo factory selection invokes one callback", comboChanges == 1);
        Check("Combo factory exposes accessible name", new ComboBoxAutomationPeer(factoryCombo).GetName() == "Taskbar position");
        Check("Empty combo factory has no selection", MetroPatternTheme.CreateComboBox(Array.Empty<string>(), 0, _ => { }, "Empty").SelectedIndex == -1);

        int appbarClicks = 0;
        var appbarButton = MetroPatternTheme.CreateAppBarButton(new TextBlock { Text = "+", FontSize = 20 }, "Open new window", () => appbarClicks++);
        Layout(appbarButton, 96, 88); double appbarWidth = appbarButton.ActualWidth;
        Force(appbarButton, UIElement.IsKeyboardFocusedProperty, true);
        SendKey(appbarButton, keyboardSource, Key.Space, true);
        Check("Appbar press never scales its geometry", appbarButton.RenderTransform.Value.IsIdentity && Part<Border>(appbarButton, "Ring").RenderTransform.Value.IsIdentity);
        SendKey(appbarButton, keyboardSource, Key.Space, false); Layout(appbarButton, 96, 88);
        Check("Appbar factory Space invokes one callback", appbarClicks == 1);
        Check("Appbar factory has stable width and accessible label", appbarButton.ActualWidth == appbarWidth && new ButtonAutomationPeer(appbarButton).GetName() == "Open new window");
        Force(appbarButton, ButtonBase.IsPressedProperty, false);
        Save(new Border { Background = Brushes.White, Child = appbarButton }, "appbar-factory-command.png", 96, 88, 96);

        foreach (var orientation in new[] { Orientation.Horizontal, Orientation.Vertical })
        {
            var scroll = Styled(new ScrollBar { Orientation = orientation, Maximum = 100, Value = 40, ViewportSize = 25 }, "ScrollBar");
            Layout(scroll, orientation == Orientation.Horizontal ? 300 : 18, orientation == Orientation.Horizontal ? 18 : 220);
            var track = Part<Track>(scroll, "PART_Track");
            Check(orientation + " scrollbar track", track.Thumb != null && track.Orientation == orientation && track.Value == 40);
            double start = scroll.Value;
            (orientation == Orientation.Horizontal ? ScrollBar.LineRightCommand : ScrollBar.LineDownCommand).Execute(null, scroll);
            Check(orientation + " scrollbar line command", scroll.Value > start);
            var thumbChrome = Part<Border>(track.Thumb, "Chrome");
            Check(orientation + " scrollbar gray thumb", thumbChrome.Background.ToString() == "#FFB8B8B8");
            Force(track.Thumb, UIElement.IsMouseOverProperty, true);
            Check(orientation + " scrollbar gray hover", thumbChrome.Background.ToString() == "#FF999999");
            Force(track.Thumb, Thumb.IsDraggingProperty, true);
            Check(orientation + " scrollbar gray drag", thumbChrome.Background.ToString() == "#FF7A7A7A");
            Force(track.Thumb, Thumb.IsDraggingProperty, false);
            Force(track.Thumb, UIElement.IsMouseOverProperty, false);
            Save(scroll, orientation + "-scrollbar.png", orientation == Orientation.Horizontal ? 300 : 18, orientation == Orientation.Horizontal ? 18 : 220, 96);
            if (orientation == Orientation.Horizontal) scroll.Height = 15; else scroll.Width = 15;
            Layout(scroll, orientation == Orientation.Horizontal ? 300 : 15, orientation == Orientation.Horizontal ? 15 : 220);
            Check(orientation + " thin scrollbar ignores native 17px minimum", orientation == Orientation.Horizontal ? scroll.ActualHeight == 15 && track.Thumb.ActualHeight <= 15 : scroll.ActualWidth == 15 && track.Thumb.ActualWidth <= 15);
        }
        var arrow = Styled(new RepeatButton { Content = "<" }, "ScrollArrow");
        var zoom = Styled(new Button { Content = "-" }, "ScrollZoomButton");
        Layout(arrow, 18, 15); Layout(zoom, 18, 15);
        Check("Scrollbar arrow and zoom share exact template", ReferenceEquals(arrow.Template, zoom.Template));
        foreach (var state in new[] { "normal", "hover", "pressed", "released" })
        {
            foreach (ButtonBase button in new ButtonBase[] { arrow, zoom })
            {
                Force(button, UIElement.IsMouseOverProperty, state is "hover" or "pressed");
                Force(button, ButtonBase.IsPressedProperty, state == "pressed");
            }
            string expected = state == "pressed" ? "#FFC4C4C4" : state == "hover" ? "#FFDCDCDC" : "#FFEEEEEE";
            Check("Scrollbar buttons matching " + state, Part<Border>(arrow, "Chrome").Background.ToString() == expected && Part<Border>(zoom, "Chrome").Background.ToString() == expected);
            Check("Scrollbar glyphs remain gray " + state, arrow.Foreground.ToString() == "#FF555555" && zoom.Foreground.ToString() == "#FF555555");
        }
        foreach (int dpi in new[] { 96, 144, 192 })
        {
            var strip = new StackPanel { Orientation = Orientation.Horizontal };
            strip.Children.Add(Styled(new RepeatButton { Content = "<", Width = 18, Height = 15 }, "ScrollArrow"));
            strip.Children.Add(Styled(new ScrollBar { Orientation = Orientation.Horizontal, Width = 400, Height = 15, Maximum = 100, ViewportSize = 30, Value = 40 }, "ScrollBar"));
            strip.Children.Add(Styled(new Button { Content = "-", Width = 18, Height = 15 }, "ScrollZoomButton"));
            Save(strip, $"scroll-buttons-{dpi}dpi.png", 436, 15, dpi);
        }
        var progress = Styled(new ProgressBar { Value = 65 }, "ProgressBar"); Layout(progress, 300, 8);
        Check("Progress respects current system animation preference", MetroControlStyles81.GetAnimationsEnabled(progress) == SystemParameters.ClientAreaAnimation);
        Check("Progress indicator tracks value", Math.Abs(Part<FrameworkElement>(progress, "PART_Indicator").ActualWidth - 195) < 1);
        progress.Value = 0; Layout(progress, 300, 8); Check("Zero progress has no fill", Part<FrameworkElement>(progress, "PART_Indicator").ActualWidth < 1);
        progress.Value = 100; Layout(progress, 300, 8); Check("Full progress fills track", Math.Abs(Part<FrameworkElement>(progress, "PART_Indicator").ActualWidth - 300) < 1);

        var slider = Styled(new Slider { Minimum = 0, Maximum = 100, Value = 25 }, "Slider"); Layout(slider, 300, 40);
        Slider.IncreaseLarge.Execute(null, slider);
        Check("Slider uses native range commands", slider.Value > 25 && Part<Track>(slider, "PART_Track").Thumb != null);
        var menuItem = Styled(new MenuItem { Header = "Submenu" }, "MenuItem"); menuItem.Items.Add(new MenuItem { Header = "Child" }); Layout(menuItem, 250, 40);
        Check("Menu retains submenu popup and arrow", Part<Popup>(menuItem, "PART_Popup") != null && Part<System.Windows.Shapes.Path>(menuItem, "Arrow").Visibility == Visibility.Visible);
        Force(menuItem, MenuItem.IsHighlightedProperty, true);
        Check("Menu highlight inverts to black and white", Part<Border>(menuItem, "Chrome").Background.ToString() == "#FF000000" && menuItem.Foreground.ToString() == "#FFFFFFFF");
        var menu = Styled(new ContextMenu(), "ContextMenu"); menu.Items.Add(new MenuItem { Header = "Copy" }); menu.Items.Add(new Separator()); menu.Items.Add(new MenuItem { Header = "Paste" });
        Save(menu, "context-menu-detached.png", 240, 110, 96);
        VerifyLegacyMenus();

        Check("No native window created or shown", !resourceOwner.IsVisible && new System.Windows.Interop.WindowInteropHelper(resourceOwner).Handle == IntPtr.Zero);
        resourceOwner.Content = null;
        resourceOwner.Close();
    }

    private static void VerifyLegacyMenus()
    {
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        XNamespace p = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        var source = XDocument.Load(Path.Combine(AppContext.BaseDirectory, "App.resources-source.xaml"));
        string[] keys = { "LegacyContextMenu", "{x:Type ContextMenu}", "{x:Type MenuItem}", "{x:Type Separator}" };
        var selected = source.Descendants(p + "Style").Where(e => keys.Contains((string)e.Attribute(x + "Key")));
        var xml = new XElement(p + "ResourceDictionary", new XAttribute(XNamespace.Xmlns + "x", x), selected);
        var resources = (ResourceDictionary)System.Windows.Markup.XamlReader.Parse(xml.ToString());
        var menu = new ContextMenu { Resources = resources, Style = (Style)resources[typeof(ContextMenu)] };
        var parent = new MenuItem { Header = "Legacy submenu", IsCheckable = true, IsChecked = true };
        parent.Items.Add(new MenuItem { Header = "Nested command" }); parent.Items.Add(new Separator());
        menu.Items.Add(parent); menu.Items.Add(new Separator()); menu.Items.Add(new MenuItem { Header = "Disabled", IsEnabled = false });
        Layout(menu, 280, 120);
        Check("Legacy implicit menu preserves submenu part", Part<Popup>(parent, "PART_Popup") != null);
        Check("Legacy implicit menu renders checks and submenu arrow", Part<System.Windows.Shapes.Path>(parent, "Check").Visibility == Visibility.Visible && Part<System.Windows.Shapes.Path>(parent, "SubmenuArrow").Visibility == Visibility.Visible);
        Check("Legacy menu keeps dark foreground contract", parent.Foreground.ToString() == "#FFFFFFFF");
        Save(menu, "legacy-menu-detached.png", 280, 120, 96);
    }

    private static ListView MakeFileList(int count = 200)
    {
        var view = new GridView { AllowsColumnReorder = true };
        foreach (var column in new[] { ("Name", "Name", 240d), ("Date modified", "Modified", 170d), ("Type", "Kind", 150d), ("Size", "Size", 90d) })
            view.Columns.Add(new GridViewColumn { Header = column.Item1, Width = column.Item3, DisplayMemberBinding = new Binding(column.Item2) });
        return Styled(new ListView
        {
            View = view,
            SelectionMode = SelectionMode.Extended,
            ItemsSource = Enumerable.Range(0, count).Select(i => new FileRow($"Document {i:000}.txt", "07/09/2026 12:00", "Text document", "24 KB")).ToArray()
        }, "ListView");
    }

    private static void VerifyListViews()
    {
        var list = MakeFileList(); var view = (GridView)list.View;
        Layout(list, 480, 250);
        var scroll = Part<ScrollViewer>(list, "PART_ScrollViewer");
        var row = (ListViewItem)list.ItemContainerGenerator.ContainerFromIndex(0);
        var headers = Descendants<GridViewColumnHeader>(list).Where(h => h.Role == GridViewColumnHeaderRole.Normal).ToArray();
        Check("GridView generates real column headers", headers.Length == 4 && headers.All(h => h.Style == app.FindResource("Metro81.GridViewColumnHeader")));
        Check("GridView virtualization remains active", VirtualizingPanel.GetIsVirtualizing(list) && VirtualizingPanel.GetVirtualizationMode(list) == VirtualizationMode.Recycling && list.ItemContainerGenerator.ContainerFromIndex(150) == null);
        double HeaderX() => Part<GridViewHeaderRowPresenter>(scroll, "Headers").TranslatePoint(new Point(), list).X;
        double RowX() => Part<GridViewRowPresenter>(row, "Cells").TranslatePoint(new Point(), list).X;
        Check("GridView headers align with cells", Math.Abs(HeaderX() - RowX()) < 0.01);
        scroll.ScrollToHorizontalOffset(120); Layout(list, 480, 250);
        Check("GridView horizontal scroll synchronizes header and cells", scroll.HorizontalOffset == 120 && Math.Abs(HeaderX() - RowX()) < 0.01);
        scroll.ScrollToHorizontalOffset(0); Layout(list, 480, 250);
        var firstHeader = headers.Single(h => ReferenceEquals(h.Column, view.Columns[0]));
        var grip = Part<Thumb>(firstHeader, "PART_HeaderGripper");
        double oldWidth = view.Columns[0].Width;
        grip.RaiseEvent(new DragDeltaEventArgs(28, 0) { RoutedEvent = Thumb.DragDeltaEvent }); Layout(list, 480, 250);
        Check("Native header gripper resizes its column", view.Columns[0].Width == oldWidth + 28 && grip.ActualWidth >= 8);
        string firstName = (string)view.Columns[0].Header;
        view.Columns.Move(0, 2); Layout(list, 480, 250);
        Check("Column reorder preserves shared column collection", Equals(view.Columns[2].Header, firstName) && ReferenceEquals(Part<GridViewRowPresenter>(row, "Cells").Columns, view.Columns));
        view.Columns.Move(2, 0); Layout(list, 480, 250);
        MetroControlStyles81.SetSortDirection(firstHeader, ListSortDirection.Descending);
        Check("Sort direction affordance renders", Part<System.Windows.Shapes.Path>(firstHeader, "SortArrow").Visibility == Visibility.Visible);
        firstHeader.Click += (_, _) => CollectionViewSource.GetDefaultView(list.ItemsSource).SortDescriptions.Add(new SortDescription(nameof(FileRow.Name), ListSortDirection.Descending));
        firstHeader.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); Layout(list, 480, 250);
        Check("Header click still reaches host sorting handler", ((FileRow)list.Items[0]).Name == "Document 199.txt");
        list.SelectedItems.Add(list.Items[0]); list.SelectedItems.Add(list.Items[1]); Layout(list, 480, 250);
        row = (ListViewItem)list.ItemContainerGenerator.ContainerFromIndex(0);
        Check("Extended native selection retained", list.SelectedItems.Count == 2 && UIElementAutomationPeer.CreatePeerForElement(list)?.GetPattern(PatternInterface.Selection) is ISelectionProvider);
        Check("Selected details row uses accent", Part<Border>(row, "Chrome").Background.ToString() == ((Brush)app.Resources["MenuAccent"]).ToString());
        var scope = new Grid(); scope.Resources[typeof(ListView)] = new Style(typeof(ListView)); var oldStyle = scope.Resources[typeof(ListView)];
        MetroPatternTheme.Apply(scope); Check("Existing implicit ListView style preserved", ReferenceEquals(scope.Resources[typeof(ListView)], oldStyle));

        for (int i = 0; i < 3; i++)
        {
            int dpi = new[] { 96, 144, 192 }[i];
            var sheet = new StackPanel { Background = Brushes.White };
            var title = Text("Native ListView / GridView: synthetic file details", 24); title.Margin = new Thickness(16); sheet.Children.Add(title);
            var sample = MakeFileList(40); sample.Height = 230; sample.SelectedItems.Add(sample.Items[1]); sample.SelectedItems.Add(sample.Items[3]); sheet.Children.Add(sample);
            var iconTitle = Text("Same native ListView without GridView", 20); iconTitle.Margin = new Thickness(16); sheet.Children.Add(iconTitle);
            var iconList = MakeFileList(20); iconList.View = null;
            iconList.ItemTemplate = (DataTemplate)System.Windows.Markup.XamlReader.Parse("<DataTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'><StackPanel Margin='4'><TextBlock Text='{Binding Name}' FontSize='16'/><TextBlock Text='{Binding Kind}' FontSize='12'/></StackPanel></DataTemplate>");
            iconList.Height = 230; iconList.SelectedIndex = 1; sheet.Children.Add(iconList);
            Save(sheet, $"listview-details-icons-{dpi}dpi.png", 760, 620, dpi);
        }
        list.View = null;
        list.ItemTemplate = (DataTemplate)System.Windows.Markup.XamlReader.Parse("<DataTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'><TextBlock Text='{Binding Name}'/></DataTemplate>");
        Layout(list, 480, 250); row = (ListViewItem)list.ItemContainerGenerator.ContainerFromIndex(0);
        Check("Switching to icon rows retains ItemTemplate", Part<ContentPresenter>(row, "PlainContent").Visibility == Visibility.Visible && Descendants<TextBlock>(row).Any(t => t.Text == "Document 199.txt"));
        list.View = view; list.ItemTemplate = null; Layout(list, 480, 250); row = (ListViewItem)list.ItemContainerGenerator.ContainerFromIndex(0);
        Check("Switching back to details restores columns", Part<GridViewRowPresenter>(row, "Cells").Visibility == Visibility.Visible && Descendants<GridViewColumnHeader>(list).Count(h => h.Role == GridViewColumnHeaderRole.Normal) == 4);
    }

    private static void VerifyAppBarContrast()
    {
        foreach (int dpi in new[] { 96, 144, 192 })
        {
            var sheet = new StackPanel { Background = Brushes.White };
            var title = Text("Appbar: local white glyphs / forced native button states", 22); title.Margin = new Thickness(16); sheet.Children.Add(title);
            foreach (bool dark in new[] { false, true })
            {
                var band = new StackPanel { Background = dark ? new SolidColorBrush(Color.FromRgb(38, 38, 38)) : Brushes.White };
                sheet.Children.Add(band);
                var line = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(16) }; band.Children.Add(line);
                foreach (string kind in new[] { "Text", "Path" })
                foreach (string state in new[] { "Normal", "Hover", "Pressed", "Disabled" })
                {
                    UIElement icon = kind == "Text"
                        ? new TextBlock { Text = "\ue104", FontFamily = new FontFamily("Segoe UI Symbol"), FontSize = 18, Foreground = Brushes.White }
                        : new System.Windows.Shapes.Path { Data = Geometry.Parse("M 0,8 L 16,8 M 8,0 L 8,16"), Stroke = Brushes.White, StrokeThickness = 3, Width = 19, Height = 19 };
                    var button = MetroPatternTheme.CreateAppBarButton(icon, kind + " " + state, () => { });
                    button.Foreground = dark ? Brushes.White : Brushes.Black;
                    line.Children.Add(button); Layout(button, 96, 88);
                    var ring = Part<Border>(button, "Ring"); var paint = Part<System.Windows.Shapes.Rectangle>(button, "AppBarIcon");
                    Size before = ring.RenderSize; Size iconBefore = icon.RenderSize;
                    if (state == "Hover") Force(button, UIElement.IsMouseOverProperty, true);
                    if (state == "Pressed") Force(button, ButtonBase.IsPressedProperty, true);
                    if (state == "Disabled") button.IsEnabled = false;
                    Layout(button, 96, 88);
                    string key = $"Appbar {kind} {state} {(dark ? "dark" : "light")} {dpi}dpi";
                    Check(key + " has exact unscaled bounds", ring.RenderSize == before && icon.RenderSize == iconBefore && button.RenderTransform.Value.IsIdentity && ring.RenderTransform.Value.IsIdentity && icon.RenderTransform.Value.IsIdentity && button.ActualWidth == 96);
                    Check(key + " preserves local source white", icon is TextBlock text ? ReferenceEquals(text.Foreground, Brushes.White) : ReferenceEquals(((System.Windows.Shapes.Path)icon).Stroke, Brushes.White));
                    if (state == "Pressed")
                    {
                        Check(key + " inverse brush contrast", ((SolidColorBrush)ring.Background).Color == (dark ? Colors.White : Colors.Black) && ((SolidColorBrush)paint.Fill).Color == (dark ? Colors.Black : Colors.White));
                        var pixels = Render(ring, 40, 40, dpi); byte[] buffer = new byte[pixels.PixelWidth * pixels.PixelHeight * 4]; pixels.CopyPixels(buffer, pixels.PixelWidth * 4, 0);
                        int darkPixels = 0, lightPixels = 0; int inset = (int)(10 * dpi / 96d);
                        for (int y = inset; y < pixels.PixelHeight - inset; y++)
                        for (int x = inset; x < pixels.PixelWidth - inset; x++)
                        {
                            int n = (y * pixels.PixelWidth + x) * 4;
                            if (buffer[n + 3] > 240 && buffer[n] < 40 && buffer[n + 1] < 40 && buffer[n + 2] < 40) darkPixels++;
                            if (buffer[n + 3] > 240 && buffer[n] > 215 && buffer[n + 1] > 215 && buffer[n + 2] > 215) lightPixels++;
                        }
                        Check(key + " paints real contrasting glyph pixels", darkPixels > 8 && lightPixels > 8);
                    }
                }
            }
            Save(sheet, $"appbar-contrast-states-{dpi}dpi.png", 810, 320, dpi);
        }
    }

    private static void VerifyPanningIndicator()
    {
        foreach (Orientation direction in new[] { Orientation.Horizontal, Orientation.Vertical })
        {
            var bar = Styled(new ScrollBar { Orientation = direction, Maximum = 200, ViewportSize = 100, Value = 50 }, "PanningIndicator");
            double width = direction == Orientation.Horizontal ? 300 : 4, height = direction == Orientation.Horizontal ? 4 : 300;
            Layout(bar, width, height); var track = Part<Track>(bar, "PART_Track");
            double length = direction == Orientation.Horizontal ? track.Thumb.ActualWidth : track.Thumb.ActualHeight;
            var point = track.Thumb.TranslatePoint(new Point(), bar);
            Check(direction + " panning indicator encodes viewport fraction", Math.Abs(length - 100) < 0.01);
            Check(direction + " panning indicator encodes offset", Math.Abs((direction == Orientation.Horizontal ? point.X : point.Y) - 50) < 0.01);
            Check(direction + " panning indicator is informational", !bar.Focusable && !bar.IsHitTestVisible && !bar.IsEnabled);
            Save(new Border { Background = Brushes.White, Child = bar }, $"panning-{direction}.png", width, height, 96);
            bar.Maximum = 0; Check(direction + " panning indicator hides when content fits", bar.Visibility == Visibility.Collapsed);
        }
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match) yield return match;
            foreach (var nested in Descendants<T>(child)) yield return nested;
        }
    }

    private sealed record FileRow(string Name, string Modified, string Kind, string Size);

    private static void SendKey(UIElement target, PresentationSource source, Key key, bool down) => target.RaiseEvent(
        new KeyEventArgs(Keyboard.PrimaryDevice, source, Environment.TickCount, key) { RoutedEvent = down ? Keyboard.KeyDownEvent : Keyboard.KeyUpEvent });

    private sealed class DetachedSource : PresentationSource
    {
        public override Visual RootVisual { get; set; }
        public override bool IsDisposed => false;
        protected override CompositionTarget GetCompositionTargetCore() => null;
    }

    private static T Part<T>(Control owner, string name) where T : class => owner.Template.FindName(name, owner) as T ?? throw new InvalidOperationException($"Missing {name} in {owner.GetType().Name}");
    private static void Check(string name, bool passed) => Checks[name] = passed;

    // Used only for labeled detached state captures, never to set state in the production utility.
    private static void Force(DependencyObject target, DependencyProperty property, object value)
    {
        typeof(DependencyObject).GetMethod("SetValue", BindingFlags.Instance | BindingFlags.NonPublic, null,
            new[] { typeof(DependencyProperty), typeof(object), typeof(bool) }, null)?.Invoke(target, new[] { property, value, (object)true });
        if (!Equals(target.GetValue(property), value))
        {
            for (Type type = target.GetType(); type != null; type = type.BaseType)
            {
                var key = type.GetFields(BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)
                    .Where(f => f.FieldType == typeof(DependencyPropertyKey)).Select(f => f.GetValue(null) as DependencyPropertyKey)
                    .FirstOrDefault(k => k?.DependencyProperty == property);
                if (key != null) { target.SetValue(key, value); return; }
            }
            throw new InvalidOperationException("Cannot force probe state " + property.Name);
        }
    }

    private static void Layout(FrameworkElement element, double width, double height)
    {
        element.Measure(new Size(width, height)); element.Arrange(new Rect(0, 0, width, height)); element.UpdateLayout();
        app.Dispatcher.Invoke(() => { }, DispatcherPriority.Render);
    }

    private static void Save(FrameworkElement element, string name, double width, double height, int dpi)
    {
        var bitmap = Render(element, width, height, dpi);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using (var file = File.Create(Path.Combine(output, name))) encoder.Save(file);
        byte[] pixels = new byte[bitmap.PixelWidth * bitmap.PixelHeight * 4]; bitmap.CopyPixels(pixels, bitmap.PixelWidth * 4, 0);
        int opaque = 0; var colors = new HashSet<int>();
        for (int i = 0; i < pixels.Length; i += 4)
        {
            if (pixels[i + 3] == 0) continue;
            opaque++; colors.Add((pixels[i + 2] << 16) | (pixels[i + 1] << 8) | pixels[i]);
        }
        Check(name + " nonblank pixels", opaque > 10 && colors.Count > 1); Images.Add(name);
    }

    private static RenderTargetBitmap Render(FrameworkElement element, double width, double height, int dpi)
    {
        Layout(element, width, height);
        var bitmap = new RenderTargetBitmap((int)(width * dpi / 96), (int)(height * dpi / 96), dpi, dpi, PixelFormats.Pbgra32);
        bitmap.Render(element);
        return bitmap;
    }

    private sealed class TextModel : INotifyPropertyChanged
    {
        private string value;
        public string Value { get => value; set { this.value = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Value))); } }
        public event PropertyChangedEventHandler PropertyChanged;
    }
}
#endif
