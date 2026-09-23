#nullable enable
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Win81Layer;

internal sealed class NewsSourcePicker : StackPanel
{
	internal event Action<string>? SourceSaved;
	internal readonly TextBox UrlBox;
	internal readonly TextBlock Status;
	internal NewsSourcePicker()
	{
		if (!ShellSkin.GlassOn) MetroPatternTheme.Apply(this);
		string current = SettingsStore.FastSnapshot.NewsFeedUrl;
		ComboBox sources = new() { ItemsSource = NewsFeedService.Sources.Select(s => s.Name).Append("Custom RSS / Atom").ToArray(), Height = 32, Margin = new Thickness(0, 0, 0, 8), Foreground = Brushes.Black };
		sources.SelectedIndex = Array.FindIndex(NewsFeedService.Sources, s => s.Url == current);
		if (sources.SelectedIndex < 0) sources.SelectedIndex = NewsFeedService.Sources.Length;
		AutomationProperties.SetName(sources, "News source");
		Children.Add(sources);
		Grid row = new();
		row.ColumnDefinitions.Add(new ColumnDefinition());
		row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
		UrlBox = new TextBox { Text = current, Height = 34, Padding = new Thickness(8, 4, 8, 4), VerticalContentAlignment = VerticalAlignment.Center, Foreground = Brushes.Black, Background = Brushes.White, MinWidth = 80 };
		AutomationProperties.SetName(UrlBox, "RSS or Atom feed URL");
		row.Children.Add(UrlBox);
		Button save = IconButton("\ue74e", "Save news source");
		Grid.SetColumn(save, 1); row.Children.Add(save);
		Status = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0), FontSize = 12 };
		void Save()
		{
			string? url = NewsFeedService.NormalizeUrl(UrlBox.Text);
			if (url == null) { Status.Text = "Enter a public HTTP(S) RSS or Atom URL."; return; }
			SettingsStore.Update(s => s.NewsFeedUrl = url);
			UrlBox.Text = url;
			(Application.Current as App)?.ApplyNewsSource(url);
			Status.Text = "Source saved";
			SourceSaved?.Invoke(url);
		}
		save.Click += (_, _) => Save();
		UrlBox.KeyDown += (_, e) => { if (e.Key == Key.Enter) { Save(); e.Handled = true; } };
		sources.SelectionChanged += (_, _) => { if (sources.SelectedIndex >= 0 && sources.SelectedIndex < NewsFeedService.Sources.Length) UrlBox.Text = NewsFeedService.Sources[sources.SelectedIndex].Url; };
		Children.Add(row); Children.Add(Status);
	}
	internal static Button IconButton(string glyph, string label)
	{
		Button button = new() { Content = new TextBlock { Text = glyph, FontFamily = new FontFamily("Segoe MDL2 Assets"), FontSize = 17 }, Width = 36, Height = 34, Margin = new Thickness(6, 0, 0, 0), ToolTip = label };
		AutomationProperties.SetName(button, label);
		if (!ShellSkin.GlassOn) button.SetResourceReference(FrameworkElement.StyleProperty, "Metro81.CommandButton");
		return button;
	}
}

internal sealed class NewsWindow : Win81Window
{
	private readonly StackPanel _articles = new();
	private readonly TextBlock _status = new() { FontSize = 13, Margin = new Thickness(0, 8, 0, 12), TextWrapping = TextWrapping.Wrap };
	private readonly Grid _root;
	private CancellationTokenSource? _fetch;
	private int _generation;
	private bool _closed;
	internal NewsWindow(bool diagnostics = false)
	{
		Title = "News";
		Width = Math.Min(710, SystemParameters.WorkArea.Width - 40);
		Height = Math.Min(730, SystemParameters.WorkArea.Height - 40);
		MinWidth = 360; MinHeight = 360;
		_root = new Grid { Margin = new Thickness(20) };
		if (!ShellSkin.GlassOn) MetroPatternTheme.Apply(_root);
		_root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
		_root.RowDefinitions.Add(new RowDefinition());
		StackPanel header = new();
		DockPanel title = new() { Margin = new Thickness(0, 0, 0, 14) };
		Button refresh = NewsSourcePicker.IconButton("\ue72c", "Refresh headlines");
		Button pin = NewsSourcePicker.IconButton("\ue718", "Pin News to Start");
		DockPanel.SetDock(refresh, Dock.Right); DockPanel.SetDock(pin, Dock.Right);
		title.Children.Add(refresh); title.Children.Add(pin);
		title.Children.Add(new TextBlock { Text = "News", FontFamily = new FontFamily("Segoe UI Light"), FontSize = 30 });
		header.Children.Add(title);
		NewsSourcePicker picker = new(); header.Children.Add(picker); header.Children.Add(_status);
		_root.Children.Add(header);
		ScrollViewer scroll = new() { Content = _articles, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, CanContentScroll = false };
		Grid.SetRow(scroll, 1); _root.Children.Add(scroll);
		SetBody(_root);
		refresh.Click += (_, _) => { _ = RefreshAsync(force: true); };
		pin.Click += (_, _) => { (Application.Current as App)?.PinNewsTile(); _status.Text = "News pinned to Start"; };
		if (!diagnostics) Loaded += (_, _) => { _ = RefreshAsync(); };
		Activated += (_, _) => ApplyTheme();
		Closed += (_, _) => { _closed = true; _generation++; _fetch?.Cancel(); };
		ApplyTheme();
	}
	private void ApplyTheme()
	{
		Color color;
		try { color = (Color)ColorConverter.ConvertFromString(SettingsStore.FastSnapshot.StartBgColor); }
		catch { color = Color.FromRgb(60, 30, 112); }
		Background = new SolidColorBrush(color);
		_root.Background = new SolidColorBrush(color);
		Foreground = color.R * .2126 + color.G * .7152 + color.B * .0722 > 170 ? Brushes.Black : Brushes.White;
	}
	internal async Task RefreshAsync(bool force = false)
	{
		if (_closed) return;
		int generation = ++_generation;
		_fetch?.Cancel();
		using CancellationTokenSource cancellation = new(TimeSpan.FromSeconds(20));
		_fetch = cancellation;
		string url = SettingsStore.FastSnapshot.NewsFeedUrl;
		_status.Text = "Loading headlines";
		_articles.Children.Clear();
		try
		{
			NewsFeedService.Feed? cached = await Task.Run(() => NewsFeedService.Cached(url));
			if (generation != _generation || _closed) return;
			if (cached != null) Render(cached);
			NewsFeedService.Feed feed = await NewsFeedService.GetAsync(url, force, cancellation.Token);
			if (generation == _generation && !_closed) Render(feed);
		}
		catch (OperationCanceledException) { if (generation == _generation && !_closed) _status.Text = "Refresh cancelled"; }
		catch (Exception ex) { if (generation == _generation && !_closed) _status.Text = ex.Message; }
		finally { if (generation == _generation) _fetch = null; }
	}
	internal void Render(NewsFeedService.Feed feed)
	{
		_articles.Children.Clear();
		_status.Text = feed.Title + "  |  " + (feed.Stale ? "Offline data" : "Updated " + feed.FetchedAt.ToLocalTime().ToString("HH:mm")) + (feed.Articles.Length == 0 ? "  |  No headlines" : "");
		foreach (NewsFeedService.Article article in feed.Articles)
		{
			StackPanel body = new() { Margin = new Thickness(0, 10, 12, 10) };
			body.Children.Add(new TextBlock { Text = article.Title, FontSize = 20, FontFamily = new FontFamily("Segoe UI Light"), TextWrapping = TextWrapping.Wrap });
			if (article.Published.HasValue) body.Children.Add(new TextBlock { Text = article.Published.Value.ToLocalTime().ToString("ddd d MMM, HH:mm"), FontSize = 11, Opacity = .8, Margin = new Thickness(0, 5, 0, 0) });
			Button open = new() { Content = body, HorizontalContentAlignment = HorizontalAlignment.Stretch, Background = Brushes.Transparent, Foreground = Foreground, BorderThickness = new Thickness(0, 0, 0, 1), BorderBrush = new SolidColorBrush(Color.FromArgb(45, 255, 255, 255)), Padding = new Thickness(0), ToolTip = article.Url };
			AutomationProperties.SetName(open, article.Title);
			if (!ShellSkin.GlassOn) open.SetResourceReference(FrameworkElement.StyleProperty, "Metro81.CommandButton");
			open.Click += (_, _) => AppLauncher.TryLaunch(article.Url, null, null, false, out _);
			_articles.Children.Add(open);
		}
	}
}
