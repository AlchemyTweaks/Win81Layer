using System;
using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;

namespace Win81Layer;

public static class MetroPatternTheme
{
    public static void Install(ResourceDictionary resources)
    {
        ArgumentNullException.ThrowIfNull(resources);
        if (!resources.Contains("Metro81.Button")) resources.MergedDictionaries.Add(MetroControlStyles81.CreateDictionary());
    }

    public static void Install(Application application)
    {
        ArgumentNullException.ThrowIfNull(application);
        application.Dispatcher.VerifyAccess();
        Install(application.Resources);
    }

    public static void Apply(FrameworkElement metroRoot) => MetroControlStyles81.ApplyTo(metroRoot);
    public static void Apply(Window metroWindow) => MetroControlStyles81.ApplyTo(metroWindow);

    public static ToggleButton CreateToggle(bool value, Action<bool> changed, string accessibleName) =>
        MetroControlStyles81.CreateToggle(value, changed, accessibleName);

    public static ComboBox CreateComboBox(string[] options, int selectedIndex, Action<int> changed, string accessibleName)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(changed);
        var combo = new ComboBox
        {
            ItemsSource = (string[])options.Clone(),
            SelectedIndex = options.Length == 0 ? -1 : Math.Clamp(selectedIndex, 0, options.Length - 1)
        };
        combo.SetResourceReference(FrameworkElement.StyleProperty, "Metro81.ComboBox");
        AutomationProperties.SetName(combo, accessibleName ?? "Options");
        combo.SelectionChanged += (_, e) =>
        {
            if (ReferenceEquals(e.OriginalSource, combo) && combo.SelectedIndex >= 0) changed(combo.SelectedIndex);
        };
        return combo;
    }

    public static Button CreateAppBarButton(object icon, string label, Action onClick)
    {
        ArgumentNullException.ThrowIfNull(onClick);
        var button = new Button
        {
            Content = icon is ImageSource image ? new Image { Source = image, Width = 20, Height = 20 }
                : icon is Visual ? icon : new TextBlock { Text = icon?.ToString() ?? "", FontSize = 20 },
            Width = 96,
            MinHeight = 88,
            ToolTip = label
        };
        button.SetResourceReference(FrameworkElement.StyleProperty, "Metro81.AppBarButton");
        MetroControlStyles81.SetLabel(button, label ?? "");
        button.Click += (_, _) => onClick();
        return button;
    }
}

public static class MetroControlStyles81
{
    public static readonly RoutedUICommand ClearTextCommand = new("Clear text", "ClearText", typeof(MetroControlStyles81));
    public static readonly DependencyProperty LabelProperty = DependencyProperty.RegisterAttached(
        "Label", typeof(string), typeof(MetroControlStyles81), new FrameworkPropertyMetadata("", LabelChanged));
    public static readonly DependencyProperty AnimationsEnabledProperty = DependencyProperty.RegisterAttached(
        "AnimationsEnabled", typeof(bool), typeof(MetroControlStyles81), new FrameworkPropertyMetadata(true));
    public static readonly DependencyProperty SortDirectionProperty = DependencyProperty.RegisterAttached(
        "SortDirection", typeof(ListSortDirection?), typeof(MetroControlStyles81), new FrameworkPropertyMetadata(null));
    public static ListSortDirection? GetSortDirection(DependencyObject target) => (ListSortDirection?)target.GetValue(SortDirectionProperty);
    public static void SetSortDirection(DependencyObject target, ListSortDirection? value) => target.SetValue(SortDirectionProperty, value);
    public static bool GetAnimationsEnabled(DependencyObject target) => (bool)target.GetValue(AnimationsEnabledProperty);
    public static void SetAnimationsEnabled(DependencyObject target, bool value) => target.SetValue(AnimationsEnabledProperty, value);

    static MetroControlStyles81()
    {
        CommandManager.RegisterClassCommandBinding(typeof(TextBox), new CommandBinding(ClearTextCommand,
            (_, e) =>
            {
                if (e.OriginalSource is TextBox box && CanClear(box))
                {
                    box.Clear();
                    box.Focus();
                    e.Handled = true;
                }
            },
            (_, e) => { e.CanExecute = e.OriginalSource is TextBox box && CanClear(box); e.Handled = true; }));
    }

    private static bool CanClear(TextBox box) => box.IsEnabled && !box.IsReadOnly && !box.AcceptsReturn && box.Text.Length > 0;
    public static string GetLabel(DependencyObject target) => (string)target.GetValue(LabelProperty);
    public static void SetLabel(DependencyObject target, string value) => target.SetValue(LabelProperty, value);
    private static void LabelChanged(DependencyObject target, DependencyPropertyChangedEventArgs e)
    {
        if (target.ReadLocalValue(AutomationProperties.NameProperty) == DependencyProperty.UnsetValue)
            System.Windows.Data.BindingOperations.SetBinding(target, AutomationProperties.NameProperty,
                new System.Windows.Data.Binding { Source = target, Path = new PropertyPath(LabelProperty) });
    }

    // Only call on a Metro form root. Local templates and explicit Win7 styles keep precedence.
    public static void ApplyTo(FrameworkElement metroRoot)
    {
        ArgumentNullException.ThrowIfNull(metroRoot);
        metroRoot.Dispatcher.VerifyAccess();
        if (metroRoot.TryFindResource("Metro81.Button") is not Style)
            metroRoot.Resources.MergedDictionaries.Add(CreateDictionary());
        foreach (var (type, key) in new (Type, string)[]
        {
            (typeof(Button), "Button"), (typeof(CheckBox), "CheckBox"), (typeof(RadioButton), "RadioButton"),
            (typeof(TextBox), "TextBox"), (typeof(PasswordBox), "PasswordBox"), (typeof(ComboBox), "ComboBox"),
            (typeof(ToolTip), "ToolTip"), (typeof(ScrollBar), "ScrollBar"), (typeof(ListBox), "ListBox"), (typeof(ListView), "ListView"),
            (typeof(ProgressBar), "ProgressBar"), (typeof(ContextMenu), "ContextMenu"), (typeof(Hyperlink), "Hyperlink"),
            (typeof(Slider), "Slider")
        })
        {
            if (!metroRoot.Resources.Contains(type))
                metroRoot.Resources[type] = new Style(type, (Style)metroRoot.FindResource("Metro81." + key));
        }
    }

    public static ResourceDictionary CreateDictionary() => new()
    {
        Source = new Uri($"/{typeof(MetroControlStyles81).Assembly.GetName().Name};component/Themes/Metro81.Controls.xaml", UriKind.Relative)
    };

    public static ToggleButton CreateToggle(bool value, Action<bool> changed, string accessibleName)
    {
        ArgumentNullException.ThrowIfNull(changed);
        var toggle = new ToggleButton { IsChecked = value };
        toggle.SetResourceReference(FrameworkElement.StyleProperty, "Metro81.ToggleSwitch");
        AutomationProperties.SetName(toggle, accessibleName ?? "Toggle switch");
        void UpdateLabel() => toggle.SetCurrentValue(ContentControl.ContentProperty, toggle.IsChecked == true ? "On" : "Off");
        UpdateLabel();
        toggle.Checked += (_, _) => { UpdateLabel(); changed(true); };
        toggle.Unchecked += (_, _) => { UpdateLabel(); changed(false); };
        return toggle;
    }
}

// A real Button keeps Space, focus, capture and automation semantics. Plaintext exists only while held.
public class MetroPasswordRevealButton81 : Button
{
    private PasswordBox owner;
    private TextBlock revealed;
    private FrameworkElement contentHost;

    public MetroPasswordRevealButton81()
    {
        Loaded += (_, _) => Connect();
        Unloaded += (_, _) => Disconnect();
        LostMouseCapture += (_, _) => Conceal();
        LostKeyboardFocus += (_, _) => Conceal();
        IsEnabledChanged += (_, _) => { if (!IsEnabled) Conceal(); };
    }

    private void Connect()
    {
        if (owner != null) return;
        owner = TemplatedParent as PasswordBox;
        if (owner == null) return;
        revealed = owner.Template.FindName("PART_RevealedText", owner) as TextBlock;
        contentHost = owner.Template.FindName("PART_ContentHost", owner) as FrameworkElement;
        owner.PasswordChanged += PasswordChanged;
        owner.IsKeyboardFocusWithinChanged += FocusChanged;
    }

    private void Disconnect()
    {
        Conceal();
        if (owner != null)
        {
            owner.PasswordChanged -= PasswordChanged;
            owner.IsKeyboardFocusWithinChanged -= FocusChanged;
        }
        owner = null;
        revealed = null;
        contentHost = null;
    }

    private void PasswordChanged(object sender, RoutedEventArgs e) { if (IsPressed) Reveal(); else Conceal(); }
    private void FocusChanged(object sender, DependencyPropertyChangedEventArgs e) { if (!(bool)e.NewValue) Conceal(); }

    protected override void OnPropertyChanged(DependencyPropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.Property == IsPressedProperty) { if ((bool)e.NewValue) Reveal(); else Conceal(); }
        if (e.Property == IsVisibleProperty && !(bool)e.NewValue) Conceal();
    }

    protected override void OnKeyUp(KeyEventArgs e)
    {
        base.OnKeyUp(e);
        if (e.Key == Key.Space || e.Key == Key.Enter || e.Key == Key.Escape) Conceal();
    }

    private void Reveal()
    {
        Connect();
        if (owner?.IsEnabled != true || !IsEnabled || revealed == null || contentHost == null) return;
        revealed.Text = owner.Password;
        revealed.Visibility = Visibility.Visible;
        contentHost.Opacity = 0;
    }

    private void Conceal()
    {
        if (revealed != null) { revealed.Text = ""; revealed.Visibility = Visibility.Collapsed; }
        if (contentHost != null) contentHost.Opacity = 1;
    }
}

public sealed class MetroMenuItemStyleSelector81 : StyleSelector
{
    public override Style SelectStyle(object item, DependencyObject container) => container is FrameworkElement element
        ? element.TryFindResource(container is Separator ? "Metro81.MenuSeparator" : "Metro81.MenuItem") as Style
        : null;
}

public sealed class MetroInverseInk81 : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        Color color = value is SolidColorBrush brush ? brush.Color : Colors.Black;
        double Linear(byte channel) { double s = channel / 255d; return s <= 0.04045 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4); }
        double luminance = 0.2126 * Linear(color.R) + 0.7152 * Linear(color.G) + 0.0722 * Linear(color.B);
        return luminance > 0.179 ? Brushes.Black : Brushes.White;
    }
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}
