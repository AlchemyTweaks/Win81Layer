#nullable enable
using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;

namespace Win81Layer;

public sealed class GoogleCalendarConnectWindow : Win81Window
{
	private static GoogleCalendarConnectWindow? _current;
	public static void ShowFor(Window? owner = null)
	{
		var dispatcher = Application.Current?.Dispatcher ?? owner?.Dispatcher;
		if (dispatcher == null || dispatcher.HasShutdownStarted) return;
		if (!dispatcher.CheckAccess()) { dispatcher.BeginInvoke(new Action(() => ShowFor(owner))); return; }
		if (_current != null)
		{
			if (_current.WindowState == WindowState.Minimized) _current.WindowState = WindowState.Normal;
			_current.Show(); _current.Activate(); return;
		}
		GoogleCalendarConnectWindow window = new();
		if (owner?.IsVisible == true) { window.Owner = owner; window.WindowStartupLocation = WindowStartupLocation.CenterOwner; }
		_current = window;
		window.Closed += (_, _) => { if (ReferenceEquals(_current, window)) _current = null; };
		window.Show(); window.Activate();
	}

	private const string ApiUrl = "https://console.cloud.google.com/apis/library/calendar-json.googleapis.com";
	private const string MailApiUrl = "https://console.cloud.google.com/apis/library/gmail.googleapis.com";
	private const string ConsentUrl = "https://console.cloud.google.com/auth/overview";
	private const string ClientsUrl = "https://console.cloud.google.com/auth/clients";
	private readonly GoogleAuthSession _auth;
	private readonly Func<CancellationToken, Task<GoogleServices.AgendaReadResult>> _readAgenda;
	private readonly TextBlock _connection = Copy("", 13);
	private readonly TextBlock _status = Copy("", 13);
	private readonly TextBlock _credentials = Copy("", 13);
	private readonly TextBlock _mailStatus = Copy("", 13);
	private readonly TextBlock _mailNotice = Copy("Signing out removes the local Google connection. Google's account permissions remain until you revoke them.", 12);
	private readonly StackPanel _events = new();
	private readonly Expander _setup = new() { Header = "Google Cloud setup", Margin = new Thickness(0, 16, 0, 12) };
	private readonly Button _import, _signIn, _connectMail, _cancel, _refresh, _signOut;
	private readonly ProgressBar _progress = new() { Height = 8, IsIndeterminate = true, Visibility = Visibility.Hidden };
	private CancellationTokenSource? _operation;
	private bool _closed;
	private string? _displayedKey;

	/// <summary>A verified agenda was read, including an empty calendar. Raised on this window's dispatcher.</summary>
	public event EventHandler? CalendarReady;
	public GoogleCalendarConnectWindow() : this(GoogleAuth.Session, GoogleServices.ReadAgendaAsync) { }

	internal GoogleCalendarConnectWindow(GoogleAuthSession auth, Func<CancellationToken, Task<GoogleServices.AgendaReadResult>> readAgenda)
	{
		_auth = auth; _readAgenda = readAgenda;
		Title = "Google Calendar";
		Width = Math.Min(560, SystemParameters.WorkArea.Width - 24);
		Height = Math.Min(680, SystemParameters.WorkArea.Height - 24);
		MinWidth = 350; MinHeight = 360;
		WindowStartupLocation = WindowStartupLocation.CenterScreen;
		Background = Brushes.White; Foreground = new SolidColorBrush(Color.FromRgb(30, 30, 30));
		FontFamily = new FontFamily("Segoe UI");
		Grid root = new() { Background = Brushes.White, Margin = new Thickness(24, 18, 24, 18) };
		MetroPatternTheme.Apply(root);
		root.RowDefinitions.Add(new() { Height = GridLength.Auto });
		root.RowDefinitions.Add(new());
		root.RowDefinitions.Add(new() { Height = GridLength.Auto });
		StackPanel heading = new() { Margin = new Thickness(0, 0, 0, 12) };
		DockPanel title = new();
		_refresh = Command("\ue72c", "Refresh calendar", iconOnly: true);
		DockPanel.SetDock(_refresh, Dock.Right); title.Children.Add(_refresh);
		title.Children.Add(new TextBlock { Text = "Google Calendar", FontSize = 28, FontFamily = new FontFamily("Segoe UI Light"), TextWrapping = TextWrapping.Wrap });
		heading.Children.Add(title); heading.Children.Add(_connection);
		root.Children.Add(heading);

		StackPanel body = new();
		_import = Command("\ue8e5", "Import Desktop OAuth JSON");
		_import.HorizontalAlignment = HorizontalAlignment.Left;
		_import.Margin = new Thickness(0, 10, 0, 14);
		StackPanel steps = new() { Margin = new Thickness(0, 10, 0, 0) };
		steps.Children.Add(_credentials); steps.Children.Add(_import);
		AddSetupStep(steps, "1. Create or select a Google Cloud project and enable Google Calendar API.", "Enable Calendar API", ApiUrl);
		AddSetupStep(steps, "2. Configure the OAuth consent screen. For an External app in Testing, add your Google account as a test user.", "Configure consent", ConsentUrl);
		AddSetupStep(steps, "3. Create an OAuth client with application type Desktop app. Download its JSON file, then import it here.", "Create Desktop client", ClientsUrl);
		steps.Children.Add(Copy("Testing projects can require sign-in again after 7 days. Your Google password is entered only in your browser.", 12));
		_setup.Content = steps; body.Children.Add(_setup);
		body.Children.Add(Copy("Calendar sign-in reads events on calendars you own. Gmail access is requested only when you choose Connect Mail.", 13));
		Expander mailOptions = new() { Header = "Mail (optional)", Margin = new Thickness(0, 12, 0, 0) };
		StackPanel mailBody = new() { Margin = new Thickness(0, 8, 0, 0) };
		mailBody.Children.Add(_mailStatus);
		mailBody.Children.Add(Copy("Connect Mail requests read-only Gmail and Calendar permission. Your selected Calendar account stays unchanged. Enable Gmail API in the same Google Cloud project first.", 13));
		WrapPanel mailActions = new();
		_connectMail = Command("\ue715", "Connect Mail");
		_connectMail.Margin = new Thickness(0, 8, 8, 0);
		_connectMail.ToolTip = "Sign in with read-only Gmail and Calendar permission";
		Button enableMail = Command("\ue8a7", "Enable Gmail API");
		enableMail.Margin = new Thickness(0, 8, 0, 0);
		enableMail.Click += (_, _) => OpenUrl(MailApiUrl);
		mailActions.Children.Add(_connectMail); mailActions.Children.Add(enableMail);
		mailBody.Children.Add(mailActions); mailOptions.Content = mailBody; body.Children.Add(mailOptions);
		Border line = new() { Height = 1, Background = new SolidColorBrush(Color.FromRgb(218, 218, 218)), Margin = new Thickness(0, 18, 0, 14) };
		body.Children.Add(line);
		body.Children.Add(Copy("Upcoming 7 days", 18));
		body.Children.Add(_events);
		ScrollViewer scroll = new() { Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Padding = new Thickness(0, 0, 8, 0) };
		Grid.SetRow(scroll, 1); root.Children.Add(scroll);

		StackPanel footer = new() { Margin = new Thickness(0, 12, 0, 0) };
		_status.MinHeight = 38;
		AutomationProperties.SetLiveSetting(_status, AutomationLiveSetting.Polite);
		footer.Children.Add(_status); footer.Children.Add(_progress);
		Grid actions = new() { Margin = new Thickness(0, 10, 0, 10) };
		actions.ColumnDefinitions.Add(new()); actions.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
		_signIn = Command("\ue77b", "Sign in to Google Calendar");
		_signIn.SetResourceReference(StyleProperty, "Metro81.AccentButton");
		_signIn.IsDefault = true;
		_cancel = Command("\ue711", "Cancel current operation", iconOnly: true);
		_cancel.Margin = new Thickness(8, 0, 0, 0);
		actions.Children.Add(_signIn); Grid.SetColumn(_cancel, 1); actions.Children.Add(_cancel);
		footer.Children.Add(actions);
		WrapPanel links = new();
		Button calendar = Command("\ue8a7", "Open Google Calendar");
		calendar.Margin = new Thickness(0, 0, 8, 8);
		calendar.Click += (_, _) => OpenUrl("https://calendar.google.com/");
		_signOut = Command("\ue8ac", "Sign out of Google");
		_signOut.Margin = new Thickness(0, 0, 0, 8);
		links.Children.Add(calendar); links.Children.Add(_signOut); footer.Children.Add(links);
		_mailNotice.Opacity = .75; footer.Children.Add(_mailNotice);
		Grid.SetRow(footer, 2); root.Children.Add(footer); SetBody(root);

		_import.Click += (_, _) => Import();
		_signIn.Click += async (_, _) => await RunAsync(signIn: true);
		_connectMail.Click += async (_, _) => await RunAsync(signIn: true, includeMail: true);
		_refresh.Click += async (_, _) => await RunAsync(signIn: false);
		_cancel.Click += (_, _) => _operation?.Cancel();
		_signOut.Click += (_, _) =>
		{
			try { _auth.SignOut(); _status.Text = "Signed out of Google on this device."; }
			catch (Exception ex) { _status.Text = ex.Message; }
			UpdateConnection();
		};
		PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) { if (_operation != null) _operation.Cancel(); else Close(); e.Handled = true; } };
		Loaded += async (_, _) => { if (_auth.IsCalendarConnected) await RunAsync(signIn: false); };
		_auth.ConnectionChanged += OnConnectionChanged;
		Closed += (_, _) => { _closed = true; _operation?.Cancel(); _auth.ConnectionChanged -= OnConnectionChanged; };
		UpdateConnection();
		_status.Text = _auth.IsCalendarConnected ? "Ready to refresh your calendar." : _auth.IsConfigured ? "Ready to connect." : "Import your Desktop OAuth JSON to begin.";
	}

	private void UpdateConnection()
	{
		bool configured = _auth.IsConfigured, calendar = _auth.IsCalendarConnected, mail = _auth.IsMailConnected, stored = _auth.HasStoredGrant;
		string? key = _auth.AgendaCacheKey;
		if (key != _displayedKey || !calendar)
		{
			_events.Children.Clear(); _displayedKey = key;
			_events.Children.Add(Copy(calendar ? "Refresh to load upcoming events." : "Calendar is not connected.", 13));
		}
		_connection.Text = calendar ? "Connected to Google Calendar" : "Calendar is not connected";
		_connection.Margin = new Thickness(0, 6, 0, 0);
		_credentials.Text = configured ? "Desktop OAuth credentials are ready." : "Desktop OAuth credentials are required.";
		_setup.IsExpanded = !configured;
		_mailNotice.Text = mail ? "Sign out of Google disconnects both Mail and Calendar on this device. Permissions can be revoked in your Google account." :
			"Signing out removes the local connection. Permissions can be revoked in your Google account.";
		bool busy = _operation != null;
		_import.IsEnabled = !busy; _signIn.IsEnabled = configured && !busy;
		_connectMail.IsEnabled = configured && !busy;
		_mailStatus.Text = mail ? "Mail connected." : "Mail is not connected.";
		SetCommandLabel(_connectMail, mail ? "Reconnect Mail" : "Connect Mail");
		_refresh.IsEnabled = calendar && !busy; _signOut.IsEnabled = stored && !busy;
		_signOut.Visibility = stored ? Visibility.Visible : Visibility.Collapsed;
		_mailNotice.Visibility = mail ? Visibility.Visible : Visibility.Collapsed;
		_cancel.IsEnabled = busy; _cancel.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
		_progress.Visibility = busy ? Visibility.Visible : Visibility.Hidden;
		SetCommandLabel(_signIn, calendar ? "Reconnect Google Calendar" : "Sign in to Google Calendar");
	}

	private void OnConnectionChanged(object? sender, EventArgs e)
	{
		if (_closed || Dispatcher.HasShutdownStarted) return;
		Dispatcher.BeginInvoke(new Action(() => { if (!_closed) UpdateConnection(); }));
	}

	private void Import()
	{
		OpenFileDialog picker = new() { Title = "Import Google Desktop OAuth credentials", Filter = "JSON files (*.json)|*.json", CheckFileExists = true, Multiselect = false };
		if (picker.ShowDialog(this) != true) return;
		GoogleAuthResult result = _auth.ImportDesktopCredentials(picker.FileName);
		_status.Text = result.Message; UpdateConnection();
	}

	private async Task RunAsync(bool signIn, bool includeMail = false)
	{
		if (_closed || _operation != null) return;
		using CancellationTokenSource cancellation = new();
		_operation = cancellation; UpdateConnection();
		try
		{
			if (signIn)
			{
				_status.Text = includeMail ? "Waiting for read-only Mail and Calendar consent in your browser..." : "Waiting for Google Calendar consent in your browser...";
				GoogleAuthResult result = await _auth.SignInAsync(calendarOnly: !includeMail, cancellation: cancellation.Token);
				if (_closed) return;
				_status.Text = result.Message;
				if (!result.Success) return;
				if (includeMail && !_auth.IsCalendarConnected)
				{
					_status.Text = "Mail connected. Sign in to Calendar to choose an agenda account.";
					return;
				}
			}
			cancellation.CancelAfter(TimeSpan.FromSeconds(25));
			_status.Text = "Loading your primary calendar...";
			GoogleServices.AgendaReadResult agenda = await _readAgenda(cancellation.Token);
			if (_closed) return;
			_status.Text = (includeMail ? "Mail connected. " : "") + agenda.Message;
			if (agenda.Snapshot is { } snapshot)
			{
				RenderAgenda(snapshot);
				CalendarReady?.Invoke(this, EventArgs.Empty);
			}
		}
		catch (OperationCanceledException) { if (!_closed) _status.Text = "Calendar refresh cancelled or timed out. You can retry."; }
		catch (Exception ex) { Logger.Log("Calendar window: " + ex.GetType().Name); if (!_closed) _status.Text = "Calendar could not be loaded. Try again."; }
		finally { _operation = null; if (!_closed) UpdateConnection(); }
	}

	internal void RenderAgenda(GoogleServices.AgendaSnapshot snapshot)
	{
		_events.Children.Clear(); _displayedKey = _auth.AgendaCacheKey;
		if (snapshot.Events.Length == 0) _events.Children.Add(Copy("No upcoming events in the next 7 days.", 13));
		foreach (GoogleServices.AgendaEvent entry in snapshot.Events)
		{
			StackPanel row = new() { Margin = new Thickness(0, 10, 0, 4) };
			TextBlock when = Copy(entry.Start.ToString("ddd d MMM") + "  " + (entry.AllDay ? "All day" : entry.Start.ToString("HH:mm")), 12);
			when.SetResourceReference(TextBlock.ForegroundProperty, "MenuAccent");
			row.Children.Add(when); row.Children.Add(Copy(entry.Title, 16));
			if (!string.IsNullOrWhiteSpace(entry.Location)) row.Children.Add(Copy(entry.Location, 12));
			_events.Children.Add(row);
		}
	}

	private void AddSetupStep(Panel parent, string text, string label, string url)
	{
		parent.Children.Add(Copy(text, 13));
		Button open = Command("\ue8a7", label); open.HorizontalAlignment = HorizontalAlignment.Left;
		open.Margin = new Thickness(0, 6, 0, 12); open.Click += (_, _) => OpenUrl(url); parent.Children.Add(open);
	}
	private void OpenUrl(string url)
	{
		try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
		catch { _status.Text = "The browser could not open. Check your default browser and retry."; }
	}
	private static TextBlock Copy(string text, double size) => new() { Text = text, FontSize = size, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 3, 0, 3) };
	private static void SetCommandLabel(Button button, string label)
	{
		AutomationProperties.SetName(button, label);
		((TextBlock)((Grid)button.Content).Children[1]).Text = label;
	}
	private static Button Command(string glyph, string label, bool iconOnly = false)
	{
		Grid content = new();
		content.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
		content.ColumnDefinitions.Add(new());
		content.Children.Add(new TextBlock { Text = glyph, FontFamily = new FontFamily("Segoe MDL2 Assets"), FontSize = 16, VerticalAlignment = VerticalAlignment.Center });
		if (!iconOnly)
		{
			TextBlock text = new() { Text = label, FontSize = 13, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
			Grid.SetColumn(text, 1); content.Children.Add(text);
		}
		Button button = new() { Content = content, MinHeight = 36, Padding = new Thickness(iconOnly ? 8 : 12, 7, iconOnly ? 8 : 12, 7), ToolTip = label };
		button.SetResourceReference(StyleProperty, iconOnly ? "Metro81.CommandButton" : "Metro81.Button");
		if (iconOnly) { button.MinWidth = 36; button.Width = 36; button.Height = 36; }
		AutomationProperties.SetName(button, label);
		return button;
	}
}
