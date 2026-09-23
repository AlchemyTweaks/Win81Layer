#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace Win81Layer;

public sealed class MetroPlaceSearchWindow : Win81Window
{
    private static WeakReference<MetroPlaceSearchWindow>? _current;
    private readonly MetroPlaceSearchView _view;
    private string _requestedQuery;
    private bool _ready;
    private bool _closed;

    public static void ShowFor(string query)
    {
        Application? app = Application.Current;
        if (app == null) return;
        if (!app.Dispatcher.CheckAccess()) { app.Dispatcher.BeginInvoke(() => ShowFor(query)); return; }
        if (_current != null && _current.TryGetTarget(out MetroPlaceSearchWindow? open) && !open._closed)
        {
            if (open.WindowState == WindowState.Minimized) open.WindowState = WindowState.Normal;
            open.Activate();
            open._requestedQuery = query;
            if (open._ready) _ = open._view.SearchAsync(query);
            return;
        }
        MetroPlaceSearchWindow window = new(query);
        _current = new(window);
        window.Show();
    }

    public MetroPlaceSearchWindow(string query = "", bool? defaultGoogleMaps = null)
    {
        _requestedQuery = query;
        Title = "Places";
        Width = Math.Min(1440, SystemParameters.WorkArea.Width);
        Height = Math.Min(880, SystemParameters.WorkArea.Height);
        MinWidth = 420; MinHeight = 400;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        _view = new MetroPlaceSearchView(PlaceSearchService.Shared, OpenUrl, Color.FromRgb(166, 45, 20),
            useSettingsAppearance: true);
        SetBody(_view);
        Loaded += async (_, _) =>
        {
            await _view.InitializeAsync(defaultGoogleMaps);
            if (_closed) return;
            _ready = true;
            await _view.SearchAsync(_requestedQuery);
        };
        Closed += (_, _) =>
        {
            _closed = true; _view.Dispose();
            if (_current != null && _current.TryGetTarget(out MetroPlaceSearchWindow? current) && current == this) _current = null;
        };
        Activated += async (_, _) => { if (IsLoaded) await _view.InitializeAsync(defaultGoogleMaps); };
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) { Close(); e.Handled = true; }
            else if (e.Key == Key.L && Keyboard.Modifiers == ModifierKeys.Control) { _view.FocusSearch(); e.Handled = true; }
        };
    }

    private static bool OpenUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) || uri.Scheme != "https") return false;
        return AppLauncher.TryLaunch(url, null, null, false, out _);
    }
}

// The complete body is also renderable without creating an HWND or starting the shell.
internal sealed class MetroPlaceSearchView : Grid, IDisposable
{
    internal const int HeroPhotoMaxEdge = 1280;
    internal const int NearbyPhotoMaxEdge = 256;
    private static readonly Brush Dark = Frozen(Color.FromRgb(22, 24, 25));
    private static readonly Brush White = Brushes.White;
    private static readonly Brush Ink = Frozen(Color.FromRgb(31, 31, 31));
    private readonly Brush _accent;
    private readonly Brush _accentInk;
    private readonly bool _useSettingsAppearance;
    private bool _unitsInitialized;
    private readonly PlaceSearchService _service;
    private readonly Func<string, bool> _open;
    private readonly TextBox _query = new() { MaxLength = 200, MinWidth = 60, FontSize = 20,
        Padding = new Thickness(10, 6, 10, 6), Foreground = Ink, Background = White, BorderThickness = new Thickness(0) };
    private readonly ComboBox _matches = new() { MinWidth = 70, FontSize = 15, DisplayMemberPath = nameof(PlaceCandidate.DisplayName),
        HorizontalContentAlignment = HorizontalAlignment.Stretch, Foreground = Ink, Background = White };
    private readonly TextBlock _status = Text("", 13);
    private readonly ComboBox _engine = new() { ItemsSource = new[] { "Bing", "Google" }, SelectedIndex = 0,
        Width = 100, Height = 34, Foreground = Ink, Background = White, VerticalContentAlignment = VerticalAlignment.Center };
    private readonly ScrollViewer _panorama = new() { HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
        VerticalScrollBarVisibility = ScrollBarVisibility.Disabled, CanContentScroll = false, PanningMode = PanningMode.HorizontalOnly };
    private readonly StackPanel _bands = new() { Orientation = Orientation.Horizontal };
    private readonly Grid _hero = new() { Background = Dark, ClipToBounds = true };
    private readonly Image _heroPhoto = new() { Stretch = Stretch.UniformToFill, HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _heroFallback = Text("Places", 46);
    private readonly StackPanel _heroCaption = new();
    private readonly StackPanel _heroCredits = new();
    private readonly TextBlock _photoStatus = Text("Photo unavailable", 13);
    private readonly StackPanel _facts = new();
    private readonly StackPanel _nearby = new();
    private readonly StackPanel _web = new();
    private readonly Border _nearbyBand;
    private readonly Border _factsBand;
    private readonly Border _webBand;
    private readonly Action _resizeBands;
    private readonly TextBlock _clock = Text("", 29);
    private readonly DispatcherTimer _ticker = new() { Interval = TimeSpan.FromSeconds(30) };
    private CancellationTokenSource? _pending;
    private int _generation;
    private bool _disposed;
    private bool _changingMatches;
    private bool _changingEngine;
    private int _engineRevision;
    private bool _fahrenheit;
    private bool _defaultGoogleMaps = true;
    private string _selectedEngine = "Bing";
    private Task _engineSave = Task.CompletedTask;
    private PlaceDetails? _details;
    private readonly List<(Image Target, PlacePhoto Photo)> _photos = new();
    internal PlaceDetails? DisplayedDetails => _details;
    internal string StatusText => _status.Text;
    internal int PhotoCount { get; private set; }
    internal bool HeroPhotoLoaded => _heroPhoto.Source != null;
    internal PlaceSource<PlaceCandidate[]>? SearchSource { get; private set; }
    internal string SelectedEngine => _selectedEngine;
    internal int ExpectedPhotoCount => _photos.Count;
    internal List<string> PhotoErrors { get; } = new();
    internal ScrollViewer Panorama => _panorama;

    internal MetroPlaceSearchView(PlaceSearchService service, Func<string, bool> open, Color accent, bool fahrenheit = false,
        bool useSettingsAppearance = false)
    {
        _service = service; _open = open; _accent = new SolidColorBrush(accent); _fahrenheit = fahrenheit;
        _useSettingsAppearance = useSettingsAppearance;
        _accentInk = new SolidColorBrush(AccentForeground(accent));
        Background = Dark; UseLayoutRounding = true;
        MetroPatternTheme.Apply(this);
        TextElement.SetFontFamily(this, new FontFamily("Segoe UI"));
        TextElement.SetForeground(this, White);
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RowDefinitions.Add(new RowDefinition());
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        Grid header = new() { Margin = new Thickness(18, 14, 18, 8), HorizontalAlignment = HorizontalAlignment.Left,
            Background = _accent };
        header.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        header.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition());
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.Children.Add(_query);
        AutomationProperties.SetName(_query, "Search cities or postal codes");
        Button search = Icon("\ue721", "Search places");
        Grid.SetColumn(search, 1); header.Children.Add(search);
        Button refresh = Icon("\ue72c", "Refresh place sources");
        Grid.SetColumn(refresh, 2); header.Children.Add(refresh);
        _engine.Margin = new Thickness(12, 0, 0, 0);
        AutomationProperties.SetName(_engine, "Web search engine");
        Grid.SetColumn(_engine, 3); header.Children.Add(_engine);
        Grid heroControls = new();
        heroControls.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        heroControls.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        heroControls.Children.Add(header);

        Grid selection = new() { Margin = new Thickness(18, 0, 18, 10), HorizontalAlignment = HorizontalAlignment.Left,
            Background = Frozen(Color.FromArgb(220, 16, 18, 19)) };
        selection.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        selection.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _matches.Visibility = Visibility.Collapsed;
        _matches.MaxDropDownHeight = 280;
        AutomationProperties.SetName(_matches, "Matching locations: city, region and country");
        selection.Children.Add(_matches);
        _status.Margin = new Thickness(0, 6, 0, 0);
        AutomationProperties.SetLiveSetting(_status, AutomationLiveSetting.Polite);
        Grid.SetRow(_status, 1); selection.Children.Add(_status);
        Grid.SetRow(selection, 1); heroControls.Children.Add(selection);

        _hero.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _hero.RowDefinitions.Add(new RowDefinition());
        _hero.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Grid.SetRowSpan(_heroPhoto, 3);
        _hero.Children.Add(_heroPhoto);
        Grid.SetRow(_heroFallback, 1);
        _heroFallback.Margin = new Thickness(28); _heroFallback.VerticalAlignment = VerticalAlignment.Top;
        _hero.Children.Add(_heroFallback);
        ScrollViewer captionScroll = new() { Content = _heroCaption, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, VerticalAlignment = VerticalAlignment.Bottom,
            HorizontalAlignment = HorizontalAlignment.Right, Background = _accent, Padding = new Thickness(22, 16, 22, 16) };
        Grid.SetRow(captionScroll, 1); _hero.Children.Add(captionScroll);
        ScrollViewer creditsScroll = new() { Content = _heroCredits, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, VerticalAlignment = VerticalAlignment.Bottom,
            Background = Frozen(Color.FromArgb(200, 16, 18, 19)), Padding = new Thickness(18, 4, 18, 4) };
        Grid.SetRow(creditsScroll, 2); _hero.Children.Add(creditsScroll);
        _hero.Children.Add(heroControls);
        _bands.Children.Add(_hero);
        _factsBand = Band(_facts, Dark, White); _bands.Children.Add(_factsBand);
        _nearbyBand = Band(_nearby, _accent, _accentInk); _bands.Children.Add(_nearbyBand);
        _webBand = Band(_web, White, Ink); _bands.Children.Add(_webBand);
        _panorama.Content = _bands;
        Grid.SetRow(_panorama, 0); Grid.SetRowSpan(_panorama, 3); Children.Add(_panorama);

        DockPanel footer = new() { Margin = new Thickness(12, 4, 12, 4), LastChildFill = false };
        Button left = Arrow(MetroArrowDirection81.Left, "Previous panorama section"),
            right = Arrow(MetroArrowDirection81.Right, "Next panorama section");
        left.Click += (_, _) => _panorama.ScrollToHorizontalOffset(_panorama.HorizontalOffset - Math.Max(260, _panorama.ViewportWidth * .8));
        right.Click += (_, _) => _panorama.ScrollToHorizontalOffset(_panorama.HorizontalOffset + Math.Max(260, _panorama.ViewportWidth * .8));
        DockPanel.SetDock(right, Dock.Right); footer.Children.Add(right); footer.Children.Add(left);
        Grid.SetRow(footer, 3); Children.Add(footer);
        _resizeBands = () =>
        {
            double width = Math.Max(360, ActualWidth), height = Math.Max(100, _panorama.ActualHeight - SystemParameters.HorizontalScrollBarHeight);
            _hero.Width = Math.Clamp(width * .39, Math.Min(350, width - 48), 900);
            _hero.Height = height;
            _heroPhoto.Width = _hero.Width; _heroPhoto.Height = height;
            header.Width = selection.Width = _hero.Width - 36;
            bool narrow = _hero.Width < 500;
            Grid.SetColumnSpan(_query, narrow ? 4 : 1);
            Grid.SetRow(search, narrow ? 1 : 0); Grid.SetColumn(search, narrow ? 2 : 1);
            Grid.SetRow(refresh, narrow ? 1 : 0); Grid.SetColumn(refresh, narrow ? 3 : 2);
            Grid.SetRow(_engine, narrow ? 1 : 0); Grid.SetColumn(_engine, narrow ? 0 : 3);
            _engine.HorizontalAlignment = narrow ? HorizontalAlignment.Left : HorizontalAlignment.Stretch;
            _engine.Margin = new Thickness(narrow ? 0 : 12, 0, 0, 0);
            captionScroll.MaxWidth = _hero.Width - 24;
            captionScroll.MaxHeight = Math.Max(70, height * .44);
            captionScroll.Margin = new Thickness(24, 0, 0, Math.Max(16, height * .07));
            creditsScroll.MaxHeight = Math.Max(65, height * .16);
            _heroFallback.Margin = new Thickness(28, 24, 28, 0);
            _factsBand.Width = Math.Clamp(width * .17, 260, 330);
            _nearbyBand.Width = Math.Clamp(width * .17, 275, 340);
            _webBand.Width = Math.Max(Math.Clamp(width * .28, 330, 560), width - _hero.Width - _factsBand.Width
                - (_nearbyBand.Visibility == Visibility.Visible ? _nearbyBand.Width : 0));
            double top = Math.Clamp(height * .20, 40, 190);
            _facts.Margin = _nearby.Margin = _web.Margin = new Thickness(24, top, 24, 24);
            foreach (Border band in new[] { _factsBand, _nearbyBand, _webBand }) band.Height = height;
        };
        SizeChanged += (_, _) => _resizeBands();
        _panorama.PreviewMouseWheel += (_, e) =>
        {
            // Preserve vertical scrolling in a tall band; the horizontal bar and arrows always work.
            DependencyObject? node = e.OriginalSource as DependencyObject;
            while (node != null && node != _panorama)
            {
                if (node is ScrollViewer v && v.ScrollableHeight > 0 && (Keyboard.Modifiers & ModifierKeys.Shift) == 0) return;
                node = VisualTreeHelper.GetParent(node);
            }
            _panorama.ScrollToHorizontalOffset(_panorama.HorizontalOffset - e.Delta);
            e.Handled = true;
        };
        search.Click += (_, _) => { _ = SearchAsync(_query.Text); };
        refresh.Click += (_, _) => { _ = _details == null ? SearchAsync(_query.Text, true) : SelectAsync(_details.Place, true); };
        _query.KeyDown += (_, e) => { if (e.Key == Key.Enter) { _ = SearchAsync(_query.Text); e.Handled = true; } };
        _matches.SelectionChanged += (_, _) =>
        {
            if (!_changingMatches && _matches.SelectedItem is PlaceCandidate place) _ = SelectAsync(place);
        };
        _engine.SelectionChanged += async (_, _) =>
        {
            _selectedEngine = _engine.SelectedItem as string ?? "Bing";
            if (_changingEngine) return;
            _engineRevision++;
            string selected = _selectedEngine;
            // Chain writes in input order; SettingsStore can perform disk I/O under its gate.
            Task previous = _engineSave;
            _engineSave = Task.Run(async () =>
            {
                try { await previous.ConfigureAwait(false); } catch { }
                SettingsStore.Update(settings => settings.PlaceSearchEngine = selected);
            });
            try { await _engineSave; }
            catch { if (!_disposed) _status.Text = "Search engine changed for this session; preference could not be saved."; }
        };
        _ticker.Tick += (_, _) => UpdateClock();
        RenderEmpty("Search for a city", "");
    }

    internal async Task InitializeAsync(bool? defaultGoogleMaps = null)
    {
        try { await _engineSave; } catch { }
        int revision = _engineRevision;
        var settings = await Task.Run(() => SettingsStore.FastSnapshot);
        if (_disposed || revision != _engineRevision) return;
        if (_useSettingsAppearance)
        {
            Color accent;
            try { accent = (Color)ColorConverter.ConvertFromString(settings.StartAccentColor); }
            catch { accent = Color.FromRgb(166, 45, 20); }
            ((SolidColorBrush)_accent).Color = accent;
            ((SolidColorBrush)_accentInk).Color = AccentForeground(accent);
            if (!_unitsInitialized) { _fahrenheit = settings.WeatherUnits == "F"; _unitsInitialized = true; }
        }
        _defaultGoogleMaps = defaultGoogleMaps ?? true;
        _changingEngine = true;
        _selectedEngine = settings.PlaceSearchEngine == "Google" ? "Google" : "Bing";
        _engine.SelectedItem = _selectedEngine;
        _changingEngine = false;
    }
    internal void FocusSearch() { _panorama.ScrollToHome(); _query.Focus(); _query.SelectAll(); }
    internal async Task SelectEngineForDiagnosticsAsync(string engine)
    {
        _engine.SelectedItem = engine;
        try { await _engineSave; } catch { }
    }
    internal void LabelDiagnostic(string label)
    {
        _query.Text = label; _status.Text = label; _ticker.Stop();
    }
    private (int Generation, CancellationToken Token) BeginOperation(CancellationToken cancellation = default)
    {
        _pending?.Cancel(); _pending?.Dispose();
        _pending = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        return (++_generation, _pending.Token);
    }
    private bool Current(int generation) => !_disposed && generation == _generation;

    internal async Task SearchAsync(string query, bool force = false, CancellationToken cancellation = default)
    {
        if (_disposed) return;
        var operation = BeginOperation(cancellation);
        query = PlaceSearchService.NormalizeQuery(query); _query.Text = query;
        _details = null; SearchSource = null; _ticker.Stop();
        _changingMatches = true; _matches.ItemsSource = null; _matches.Visibility = Visibility.Collapsed; _changingMatches = false;
        RenderEmpty(query.Length < 2 ? "Search for a city" : query, query.Length < 2 ? "" : "Finding locations...");
        if (query.Length < 2) { _status.Text = "Enter at least two characters."; return; }
        try
        {
            PlaceSource<PlaceCandidate[]> found = await _service.SearchAsync(query, operation.Token, force);
            if (!Current(operation.Generation)) return;
            SearchSource = found;
            if (found.Data == null || found.Data.Length == 0)
            {
                _status.Text = found.Error != null ? "Location search unavailable. Check your connection and retry."
                    : "No matching locations. Try a city name or add a country.";
                return;
            }
            _changingMatches = true;
            _matches.ItemsSource = found.Data; _matches.SelectedIndex = 0; _matches.Visibility = Visibility.Visible;
            _changingMatches = false;
            _matches.ToolTip = SourceStamp(found) + " | Open-Meteo / GeoNames";
            await SelectAsync(found.Data[0], force, found.Stale ? "Cached location matches. " : "", cancellation);
        }
        catch (OperationCanceledException) { }
        catch (Exception) { if (Current(operation.Generation)) _status.Text = "Location search unavailable. Retry the search."; }
    }

    internal async Task SelectAsync(PlaceCandidate place, bool force = false, string statusPrefix = "", CancellationToken cancellation = default)
    {
        if (_disposed) return;
        var operation = BeginOperation(cancellation);
        _details = null; _ticker.Stop();
        RenderEmpty(place.Name, "Loading " + place.DisplayName + "...");
        RenderBasicFacts(place);
        _panorama.ScrollToHome();
        try
        {
            PlaceDetails details = await _service.GetDetailsAsync(place, operation.Token, force);
            if (!Current(operation.Generation)) return;
            Render(details);
            _status.Text = statusPrefix + BuildStatus(details);
            await LoadPhotosAsync(operation.Generation, operation.Token);
        }
        catch (OperationCanceledException) { }
        catch (Exception) { if (Current(operation.Generation)) _status.Text = "Some place sources are unavailable. Retry to refresh."; }
    }

    private void RenderEmpty(string title, string status)
    {
        _photos.Clear(); PhotoErrors.Clear(); PhotoCount = 0; _heroPhoto.Source = null;
        _heroFallback.Text = title; _heroFallback.FontSize = 32; _heroFallback.Visibility = Visibility.Visible;
        _heroCaption.Children.Clear(); _heroCredits.Children.Clear(); _facts.Children.Clear(); _nearby.Children.Clear(); _web.Children.Clear();
        _nearbyBand.Visibility = Visibility.Collapsed;
        _resizeBands();
        _status.Text = status;
        _facts.Children.Add(Text("Places", 28));
        RenderWebActions(_query.Text);
    }

    internal void Render(PlaceDetails details)
    {
        _details = details;
        if (string.IsNullOrWhiteSpace(_query.Text)) _query.Text = details.Place.DisplayName;
        _photos.Clear(); PhotoErrors.Clear(); PhotoCount = 0; _heroPhoto.Source = null;
        _heroFallback.Visibility = Visibility.Collapsed;
        _heroCaption.Children.Clear(); _heroCredits.Children.Clear(); _facts.Children.Clear(); _nearby.Children.Clear(); _web.Children.Clear();
        PlaceArticle? article = details.Article.Data;
        _photoStatus.Text = article?.Photo == null ? "Photo unavailable" : "Loading photo...";
        _photoStatus.Visibility = Visibility.Visible; _heroCredits.Children.Add(_photoStatus);
        TextBlock name = Text(details.Place.Name, 42, _accentInk); name.FontFamily = new FontFamily("Segoe UI Light");
        _heroCaption.Children.Add(name);
        _heroCaption.Children.Add(Text(details.Place.DisplayName, 14, _accentInk));
        if (article?.Photo is PlacePhoto photo)
        {
            _photos.Add((_heroPhoto, photo));
            Button credit = Link("Photo: " + photo.Artist + " | " + photo.License, photo.SourceUrl, White, 11);
            credit.ToolTip = photo.Title + "\n" + photo.Artist + "\n" + photo.License + "\n" + photo.SourceUrl;
            _heroCredits.Children.Add(credit);
            _heroCredits.Children.Add(Link("Photo license", photo.LicenseUrl, White, 11));
        }
        RenderBasicFacts(details.Place);
        if (details.Weather.Data is PlaceWeather weather)
        {
            _facts.Children.Insert(0, Text(PlaceSearchService.Temperature(weather.TemperatureC, _fahrenheit), 48));
            _facts.Children.Insert(1, Text(PlaceSearchService.WeatherDescription(weather.WeatherCode), 17));
            StackPanel units = new() { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 8) };
            foreach (bool f in new[] { false, true })
            {
                RadioButton unit = new() { Content = f ? "\u00b0F" : "\u00b0C", IsChecked = _fahrenheit == f,
                    Foreground = White, Margin = new Thickness(0, 0, 18, 0), GroupName = "PlaceTemperature" };
                unit.Checked += (_, _) =>
                {
                    _fahrenheit = f;
                    if (_facts.Children[0] is TextBlock temperature)
                        temperature.Text = PlaceSearchService.Temperature(weather.TemperatureC, f);
                };
                units.Children.Add(unit);
            }
            _facts.Children.Insert(2, units);
            if (weather.WindKmh.HasValue) _facts.Children.Insert(3, Text("Wind " + weather.WindKmh.Value.ToString("0.#") + " km/h", 14));
            DateTimeOffset observed = PlaceSearchService.LocalTime(details.Place, weather.ObservedUtc) ?? weather.ObservedUtc;
            _facts.Children.Add(Text("Weather at " + observed.ToString("d MMM, HH:mm zzz"), 12));
            _facts.Children.Add(Text(SourceStamp(details.Weather), 12));
        }
        else _facts.Children.Insert(0, Text("Weather unavailable", 22));
        _facts.Children.Add(Link("Weather: Open-Meteo (CC BY 4.0)", PlaceSearchService.WeatherSource, White, 12));
        _facts.Children.Add(Link("Locations: GeoNames / Open-Meteo", PlaceSearchService.GeocodingSource, White, 12));

        PlaceArticle[] nearby = details.Nearby.Data ?? Array.Empty<PlaceArticle>();
        _nearbyBand.Visibility = nearby.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        _resizeBands();
        if (nearby.Length > 0)
        {
            _nearby.Children.Add(Text("Nearby places", 27, _accentInk));
            _nearby.Children.Add(Text(SourceStamp(details.Nearby), 11, _accentInk));
            foreach (PlaceArticle page in nearby)
            {
                Grid item = new() { Margin = new Thickness(0, 8, 0, 0) };
                item.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(76) });
                item.ColumnDefinitions.Add(new ColumnDefinition());
                Image thumbnail = new() { Width = 66, Height = 66, Stretch = Stretch.UniformToFill };
                item.Children.Add(thumbnail);
                if (page.Photo != null) _photos.Add((thumbnail, page.Photo));
                StackPanel label = new();
                label.Children.Add(Text(page.Title, 16, _accentInk));
                double distance = PlaceSearchService.DistanceKm(details.Place.Latitude, details.Place.Longitude, page.Latitude, page.Longitude);
                label.Children.Add(Text((distance < 1 ? (distance * 1000).ToString("0") + " m" : distance.ToString("0.#") + " km")
                    + " from centre", 11, _accentInk));
                Grid.SetColumn(label, 1); item.Children.Add(label);
                Button open = Command(item, page.Title, _accentInk); open.Click += (_, _) => Launch(page.Url);
                _nearby.Children.Add(open);
                if (page.Photo is PlacePhoto credit)
                {
                    Button attribution = Link(credit.Artist + " | " + credit.License, credit.SourceUrl, _accentInk, 10);
                    attribution.Margin = new Thickness(0, 0, 0, 4);
                    _nearby.Children.Add(attribution);
                }
            }
            _nearby.Children.Add(Link("Wikipedia | CC BY-SA 4.0", PlaceSearchService.TextLicense, _accentInk, 12));
        }
        RenderWebActions(details.Place.DisplayName);
        if (article != null) AddArticle(_web, article, details.Article);
        foreach (PlaceArticle related in nearby.Take(2)) AddArticle(_web, related, details.Nearby);
        if (article == null) _web.Children.Add(Text("No verified place article available.", 15, Ink));
        _web.Children.Add(Link("Wikipedia text | CC BY-SA 4.0", PlaceSearchService.TextLicense, Ink, 12));
        _status.Text = BuildStatus(details);
        UpdateClock();
        _ticker.Start();
    }

    private void RenderBasicFacts(PlaceCandidate place)
    {
        _facts.Children.Clear();
        _facts.Children.Add(Text("Local time", 13));
        if (_clock.Parent is Panel parent) parent.Children.Remove(_clock);
        _clock.Text = PlaceSearchService.LocalTime(place, DateTimeOffset.UtcNow)?.ToString("HH:mm") ?? "Unavailable";
        _facts.Children.Add(_clock);
        _facts.Children.Add(Text(place.TimeZone, 12));
        _facts.Children.Add(Text(place.Country, 22));
        if (!string.IsNullOrEmpty(place.Region) && place.Region != place.Name) _facts.Children.Add(Text(place.Region, 14));
        _facts.Children.Add(Text(place.Latitude.ToString("0.####", CultureInfo.InvariantCulture) + ", "
            + place.Longitude.ToString("0.####", CultureInfo.InvariantCulture), 12));
        _facts.Children.Add(Action("\ue707", "View on map", () => Launch(PlaceSearchService.MapsUrl(place, _defaultGoogleMaps)), White));
        _facts.Children.Add(ExploreAction(() => Launch(PlaceSearchService.BuildWebSearchUrl(place.DisplayName, _selectedEngine))));
    }
    private void UpdateClock()
    {
        if (_details == null) return;
        DateTimeOffset? local = PlaceSearchService.LocalTime(_details.Place, DateTimeOffset.UtcNow);
        _clock.Text = local?.ToString("HH:mm") ?? "Unavailable";
        _clock.ToolTip = local?.ToString("dddd, d MMMM yyyy zzz");
    }
    private void RenderWebActions(string query)
    {
        _web.Children.Add(Text("On the web", 30, Ink));
        foreach (string engine in new[] { "Bing", "Google" })
        {
            Button button = Action("\ue8a7", "Search " + engine,
                () => Launch(PlaceSearchService.BuildWebSearchUrl(query, engine)), Ink);
            button.IsEnabled = !string.IsNullOrWhiteSpace(query);
            _web.Children.Add(button);
        }
    }
    private void AddArticle<T>(Panel panel, PlaceArticle article, PlaceSource<T> source) where T : class
    {
        panel.Children.Add(Link(article.Title, article.Url, Ink, 24));
        panel.Children.Add(Text("en.wikipedia.org", 12, Frozen(Color.FromRgb(136, 40, 20))));
        panel.Children.Add(Text(article.Description, 15, Ink));
        panel.Children.Add(Text("Wikipedia | " + SourceStamp(source), 11, Ink));
    }
    internal async Task LoadPhotosAsync(int? generation = null, CancellationToken cancellation = default)
    {
        int expected = generation ?? _generation;
        var photos = _photos.ToArray();
        await Task.WhenAll(photos.Select(async entry =>
        {
            PlaceSource<byte[]> source = await _service.GetImageAsync(entry.Photo, cancellation);
            if (!Current(expected)) return;
            int maximumEdge = entry.Target == _heroPhoto ? HeroPhotoMaxEdge : NearbyPhotoMaxEdge;
            BitmapSource? bitmap = source.Data == null ? null : await Task.Run(() => Decode(source.Data, maximumEdge), cancellation);
            if (!Current(expected)) return;
            if (source.Error != null || bitmap == null)
            {
                PhotoErrors.Add(source.Error ?? "Photo could not be decoded");
                _status.Text = "Some photographs are unavailable or cached; retry to refresh.";
            }
            if (bitmap == null)
            {
                if (entry.Target == _heroPhoto) _photoStatus.Text = "Photo unavailable";
                return;
            }
            entry.Target.Source = bitmap; PhotoCount++;
            if (entry.Target == _heroPhoto) _photoStatus.Visibility = Visibility.Collapsed;
        }));
    }
    internal static BitmapSource? Decode(byte[] data, int maximumEdge = HeroPhotoMaxEdge)
    {
        if (maximumEdge is < 1 or > HeroPhotoMaxEdge) return null;
        try
        {
            using MemoryStream stream = new(data, false);
            BitmapDecoder header = BitmapDecoder.Create(stream, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
            BitmapFrame frame = header.Frames[0];
            int width = frame.PixelWidth, height = frame.PixelHeight;
            if (width <= 0 || height <= 0 || width > 32768 || height > 32768 || (long)width * height > 64_000_000) return null;
            stream.Position = 0;
            BitmapImage bitmap = new(); bitmap.BeginInit(); bitmap.CacheOption = BitmapCacheOption.OnLoad;
            // Limit the longest edge, including portraits, and never enlarge a small source.
            if (width >= height) bitmap.DecodePixelWidth = Math.Min(maximumEdge, width);
            else bitmap.DecodePixelHeight = Math.Min(maximumEdge, height);
            bitmap.StreamSource = stream; bitmap.EndInit(); bitmap.Freeze();
            return bitmap;
        }
        catch (Exception ex) when (ex is NotSupportedException or FileFormatException or ArgumentException or IOException) { return null; }
    }
    private void Launch(string url)
    {
        try { if (!_open(url)) _status.Text = "Could not open the browser."; }
        catch { _status.Text = "Could not open the browser."; }
    }
    private static string BuildStatus(PlaceDetails details)
    {
        bool stale = details.Weather.Stale || details.Article.Stale || details.Nearby.Stale;
        bool unavailable = details.Weather.Data == null || details.Article.Data == null || details.Nearby.Data == null;
        string? issue = new[] { details.Weather.Error, details.Article.Error, details.Nearby.Error }
            .FirstOrDefault(e => e != null);
        return details.Place.DisplayName + (stale ? " | Cached data; some sources could not refresh." : unavailable || issue != null
            ? " | " + (issue ?? "Some sources unavailable.") : " | Sources loaded.");
    }
    private static string SourceStamp<T>(PlaceSource<T> source) where T : class =>
        (source.Stale ? "Offline cache" : source.Cached ? "Cached" : "Fetched")
        + (source.FetchedUtc.HasValue ? " " + source.FetchedUtc.Value.ToLocalTime().ToString("d MMM yyyy, HH:mm") : "");
    private static Border Band(StackPanel panel, Brush background, Brush foreground)
    {
        panel.Margin = new Thickness(24, 28, 24, 24);
        TextElement.SetForeground(panel, foreground);
        return new Border { Background = background, Child = new ScrollViewer { Content = panel,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            PanningMode = PanningMode.VerticalOnly } };
    }
    private static Brush Frozen(Color color) { SolidColorBrush brush = new(color); brush.Freeze(); return brush; }
    private static Color AccentForeground(Color accent) => accent.R * .2126 + accent.G * .7152 + accent.B * .0722 > 155
        ? Color.FromRgb(31, 31, 31) : Colors.White;
    private static TextBlock Text(string text, double size, Brush? ink = null) => new() { Text = text,
        FontSize = size, Foreground = ink ?? White, TextWrapping = TextWrapping.Wrap,
        Margin = new Thickness(0, 0, 0, 8), FontFamily = new FontFamily("Segoe UI") };
    private static Button Command(object content, string name, Brush ink)
    {
        Button button = new() { Content = content, Foreground = ink, Background = Brushes.Transparent,
            BorderBrush = Brushes.Transparent, BorderThickness = new Thickness(0), Padding = new Thickness(4),
            MinWidth = 0, MinHeight = 0,
            HorizontalContentAlignment = HorizontalAlignment.Stretch, Cursor = Cursors.Hand, ToolTip = name };
        button.SetResourceReference(StyleProperty, "Metro81.CommandButton");
        AutomationProperties.SetName(button, name);
        return button;
    }
    private static Button Icon(string glyph, string label)
    {
        Button button = Command(new TextBlock { Text = glyph, FontFamily = new FontFamily("Segoe MDL2 Assets"), FontSize = 19,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center }, label, White);
        button.Width = 38; button.Height = 38; button.Margin = new Thickness(4, 0, 0, 0);
        return button;
    }
    private MetroDirectionalArrow81 Arrow(MetroArrowDirection81 direction, string label)
    {
        MetroDirectionalArrow81 button = new() { Direction = direction, GlyphSize = 32, Width = 38, Height = 38,
            AccentBrush = _accent, ToolTip = label };
        button.SetResourceReference(StyleProperty, "Metro81.CommandButton");
        button.SetResourceReference(Control.FocusVisualStyleProperty, "Metro81.Focus");
        AutomationProperties.SetName(button, label);
        return button;
    }
    private FrameworkElement ExploreAction(Action action)
    {
        Grid row = new() { Background = Brushes.Transparent, Margin = new Thickness(0, 10, 0, 8), Cursor = Cursors.Hand };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(44) });
        row.ColumnDefinitions.Add(new ColumnDefinition());
        MetroDirectionalArrow81 arrow = Arrow(MetroArrowDirection81.Right, "Explore");
        arrow.Click += (_, _) => action();
        row.Children.Add(arrow);
        TextBlock label = Text("Explore", 17); label.Margin = new Thickness(0); label.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(label, 1); row.Children.Add(label);
        // The label is a sibling, not another/nested button. Unhandled label-area clicks
        // forward to the same accessible button route; icon clicks are already handled.
        row.MouseLeftButtonUp += (_, e) =>
        {
            if (arrow.IsEnabled) arrow.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            e.Handled = true;
        };
        return row;
    }
    private Button Action(string glyph, string label, Action action, Brush ink)
    {
        bool map = glyph == "\ue707";
        Grid body = new(); body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(map ? 40 : 34) }); body.ColumnDefinitions.Add(new ColumnDefinition());
        TextBlock icon = new() { Text = glyph, FontFamily = new FontFamily("Segoe MDL2 Assets"), FontSize = map ? 18 : 21,
            Foreground = ink, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
        body.Children.Add(map ? new Border { Width = 32, Height = 32, CornerRadius = new CornerRadius(16),
            BorderThickness = new Thickness(2), BorderBrush = ink, HorizontalAlignment = HorizontalAlignment.Left, Child = icon } : icon);
        TextBlock text = Text(label, 17, ink); text.Margin = new Thickness(0); Grid.SetColumn(text, 1); body.Children.Add(text);
        Button button = Command(body, label, ink); button.Margin = new Thickness(0, 10, 0, 8); button.Click += (_, _) => action();
        return button;
    }
    private Button Link(string label, string url, Brush ink, double size)
    {
        TextBlock text = Text(label, size, ink); text.Margin = new Thickness(0);
        Button button = Command(text, label, ink); button.Margin = new Thickness(0, 10, 0, 4); button.ToolTip = url;
        button.Click += (_, _) => Launch(url); return button;
    }
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true; _generation++; _pending?.Cancel(); _pending?.Dispose(); _pending = null;
        _ticker.Stop(); _photos.Clear(); _heroPhoto.Source = null;
    }
}

public static class MetroPlaceSearchProbe
{
    // Parent calls this before normal shell startup for --place-search-test.
    // It creates no visible window and never starts/restarts/deploys the desktop shell.
    public static void Begin(Application app, string query = "San Francisco", string? outputDirectory = null)
    {
        SettingsStore.ReadOnlyDiagnostics = true;
        string directory = outputDirectory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Win81Layer", "qa", "place-search");
        app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        app.Dispatcher.BeginInvoke((Action)(async () =>
        {
            try
            {
                bool passed = await RunAsync(query, directory);
                app.Shutdown(passed ? 0 : 2);
            }
            catch (Exception ex)
            {
                try { Directory.CreateDirectory(directory); await WriteReportAsync(directory, new { passed = false, error = ex.ToString() }); }
                finally { app.Shutdown(1); }
            }
        }));
    }
    public static async Task<bool> RunAsync(string query, string outputDirectory, CancellationToken cancellation = default)
    {
        PlaceSearchService service = new(cacheDirectory: Path.Combine(outputDirectory, "provider-cache"));
        return await RunCoreAsync(query, outputDirectory, service, true, cancellation);
    }
    internal static async Task<bool> RunCoreAsync(string query, string outputDirectory, PlaceSearchService service,
        bool capture, CancellationToken cancellation = default)
    {
        Directory.CreateDirectory(outputDirectory);
        await WriteReportAsync(outputDirectory, new { passed = false, status = "Running" });
        if (!SettingsStore.ReadOnlyDiagnostics) throw new InvalidOperationException("Run place diagnostics only in read-only settings mode.");
        (bool settingsPassed, object settingsReport) = await CheckSettingsAsync(service);
        using MetroPlaceSearchView view = new(service, _ => true, Color.FromRgb(166, 45, 20));
        await view.InitializeAsync();
        await view.SearchAsync(query, force: true, cancellation: cancellation);
        cancellation.ThrowIfCancellationRequested();
        List<object> checks = new();
        bool realNetworkPassed = LiveSourcesPassed(view.SearchSource, view.DisplayedDetails, view.HeroPhotoLoaded,
            view.ExpectedPhotoCount, view.PhotoCount, view.PhotoErrors.Count);
        bool passed = realNetworkPassed && settingsPassed;
        var viewports = new[] { (1920, 1080, 96d), (1366, 768, 96d), (480, 720, 96d), (960, 640, 144d), (640, 480, 192d) };
        foreach ((int width, int height, double dpi) in capture ? viewports : Array.Empty<(int, int, double)>())
        {
            string file = "PLACE-" + width + "x" + height + "-" + (int)dpi + "DPI-FETCHED.png";
            BitmapSource bitmap = Render(view, width, height, dpi);
            Save(bitmap, Path.Combine(outputDirectory, file));
            passed &= Nonblank(bitmap);
            checks.Add(new { file, width, height, dpi, photoCount = view.PhotoCount,
                horizontalOverflow = view.Panorama.ScrollableWidth, heroPhotoLoaded = view.HeroPhotoLoaded, nonblank = Nonblank(bitmap) });
            view.Panorama.ScrollToRightEnd(); view.UpdateLayout();
            Save(Render(view, width, height, dpi), Path.Combine(outputDirectory, "WEB-" + file));
            view.Panorama.ScrollToHome();
        }
        // These fixtures are reachable only through the explicit diagnostic entry point.
        // No synthetic photo or fixture can enter the live service or normal ShowFor flow.
        using MetroPlaceSearchView fixture = new(service, _ => true, Color.FromRgb(166, 45, 20));
        fixture.Render(SyntheticDetails());
        fixture.LabelDiagnostic("TEST DATA - synthetic layout and offline-cache fixture");
        foreach ((int width, int height, double dpi) in capture ? viewports : Array.Empty<(int, int, double)>())
        {
            cancellation.ThrowIfCancellationRequested();
            string file = "TEST-DATA-" + width + "x" + height + "-" + (int)dpi + "DPI.png";
            BitmapSource bitmap = Render(fixture, width, height, dpi);
            Save(bitmap, Path.Combine(outputDirectory, file));
            bool nonblank = Nonblank(bitmap); passed &= nonblank;
            checks.Add(new { file, width, height, dpi, synthetic = true, nonblank,
                horizontalOverflow = fixture.Panorama.ScrollableWidth });
            fixture.Panorama.ScrollToRightEnd(); fixture.UpdateLayout();
            Save(Render(fixture, width, height, dpi), Path.Combine(outputDirectory, "WEB-" + file));
            fixture.Panorama.ScrollToHome();
        }
        string assemblyPath = typeof(MetroPlaceSearchProbe).Assembly.Location;
        string launcherDllSha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(await File.ReadAllBytesAsync(assemblyPath, cancellation)));
        await WriteReportAsync(outputDirectory, new { query, passed, launcherDllSha256, realNetworkPassed, settingsPassed, settings = settingsReport,
            status = view.StatusText, geocoding = view.SearchSource, details = view.DisplayedDetails,
            expectedPhotos = view.ExpectedPhotoCount, loadedPhotos = view.PhotoCount, photoErrors = view.PhotoErrors, checks });
        return passed;
    }
    private static Task WriteReportAsync(string directory, object report) => File.WriteAllTextAsync(
        Path.Combine(directory, "place-search-probe.json"), System.Text.Json.JsonSerializer.Serialize(report,
            new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
    internal static bool LiveSourcesPassed(PlaceSource<PlaceCandidate[]>? geocoding, PlaceDetails? details,
        bool heroLoaded, int expectedPhotos, int loadedPhotos, int photoErrors) =>
        geocoding is { Data.Length: > 0, Cached: false, Stale: false, Error: null, FetchedUtc: not null }
        && details?.Weather is { Data: not null, Cached: false, Stale: false, Error: null, FetchedUtc: not null }
        && details.Article is { Data.Photo: not null, Cached: false, Stale: false, Error: null, FetchedUtc: not null }
        && details.Nearby is { Data: not null, Cached: false, Stale: false, Error: null, FetchedUtc: not null }
        && heroLoaded && expectedPhotos > 0 && expectedPhotos == loadedPhotos && photoErrors == 0;

    private static async Task<(bool Passed, object Report)> CheckSettingsAsync(PlaceSearchService service)
    {
        string loaded = await Task.Run(() => SettingsStore.Current.PlaceSearchEngine);
        List<object> choices = new();
        List<string> opened = new();
        using MetroPlaceSearchView view = new(service, url => { opened.Add(url); return true; }, Color.FromRgb(166, 45, 20));
        await view.InitializeAsync();
        bool passed = view.SelectedEngine == loaded;
        PlaceDetails fixture = SyntheticDetails();
        view.Render(fixture); view.LabelDiagnostic("TEST DATA - read-only engine routing fixture");
        Render(view, 1366, 768);
        Button explore = Descendants(view).OfType<Button>().First(b => AutomationProperties.GetName(b) == "Explore");
        // Both transitions go through the actual picker handler and SettingsStore.Update.
        // The diagnostic guard must reject persistence, while session routing still works.
        foreach (string engine in new[] { loaded == "Google" ? "Bing" : "Google", loaded })
        {
            await view.SelectEngineForDiagnosticsAsync(engine);
            await Dispatcher.Yield(DispatcherPriority.Background);
            explore.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            bool routing = opened.LastOrDefault() == PlaceSearchService.BuildWebSearchUrl(fixture.Place.DisplayName, engine);
            bool rejected = view.StatusText.Contains("could not be saved", StringComparison.Ordinal);
            bool unchanged = await Task.Run(() => SettingsStore.Current.PlaceSearchEngine == loaded);
            passed &= routing && rejected && unchanged && view.SelectedEngine == engine;
            choices.Add(new { engine, routing, writeRejected = rejected, durableSettingUnchanged = unchanged });
        }
        await view.InitializeAsync();
        passed &= view.SelectedEngine == loaded;
        return (passed, new { loadedEngine = loaded, settingsAssembly = typeof(SettingsStore).Assembly.GetName().Name,
            readOnly = SettingsStore.ReadOnlyDiagnostics, choices });
    }
    private static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(parent, i);
            yield return child;
            foreach (DependencyObject descendant in Descendants(child)) yield return descendant;
        }
    }
    private static PlaceDetails SyntheticDetails()
    {
        DateTimeOffset date = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
        PlaceCandidate place = new(-1, "TEST DATA - A very long demonstration city name", "TEST DATA region",
            "TEST DATA country", "", 0, 0, "Etc/UTC");
        PlaceArticle article = new(-1, "TEST DATA - Synthetic article title for wrapping checks",
            "https://example.invalid/test-data", "TEST DATA. This is a synthetic layout fixture, not a real place or search result. "
            + "It exercises long titles, unavailable photographs, wrapped article text, offline labels and scrollable bands.", 0, 0, "");
        PlaceArticle[] nearby = Enumerable.Range(1, 5).Select(i => article with { PageId = -i - 1,
            Title = "TEST DATA " + i + " - Long nearby-place label for a narrow panorama", Latitude = i / 1000d }).ToArray();
        return new PlaceDetails(place,
            new PlaceSource<PlaceWeather>(new PlaceWeather(-12.7, 17.4, 3, date), date, true, true, "Synthetic offline fixture"),
            new PlaceSource<PlaceArticle>(article, date, true, true), new PlaceSource<PlaceArticle[]>(nearby, date, true, true));
    }
    internal static BitmapSource Render(FrameworkElement view, int width, int height, double dpi = 96)
    {
        view.Width = width; view.Height = height;
        view.Measure(new Size(width, height)); view.Arrange(new Rect(0, 0, width, height)); view.UpdateLayout();
        // SizeChanged updates band dimensions; remeasure once for the final scroll extents.
        view.Measure(new Size(width, height)); view.Arrange(new Rect(0, 0, width, height)); view.UpdateLayout();
        RenderTargetBitmap bitmap = new((int)(width * dpi / 96), (int)(height * dpi / 96), dpi, dpi, PixelFormats.Pbgra32);
        bitmap.Render(view); bitmap.Freeze(); return bitmap;
    }
    internal static void Save(BitmapSource bitmap, string path)
    {
        PngBitmapEncoder encoder = new(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using FileStream file = File.Create(path); encoder.Save(file);
    }
    internal static bool Nonblank(BitmapSource bitmap)
    {
        byte[] pixels = new byte[bitmap.PixelWidth * bitmap.PixelHeight * 4];
        bitmap.CopyPixels(pixels, bitmap.PixelWidth * 4, 0);
        int min = 255, max = 0;
        for (int i = 0; i < pixels.Length; i += 64) { min = Math.Min(min, pixels[i]); max = Math.Max(max, pixels[i]); }
        return max - min > 80;
    }
}
