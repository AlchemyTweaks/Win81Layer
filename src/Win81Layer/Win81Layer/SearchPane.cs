using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Forms;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace Win81Layer;

public sealed class SearchPane : Window
{
	private enum Scope
	{
		Everywhere,
		Settings,
		Files,
		Web,
		Places
	}

	private readonly record struct SettingEntry(string Label, string Uri, string Keywords);

	private const double PaneWidth = 320.0;

	private const string Mdl2 = "Segoe MDL2 Assets";

	private readonly Func<IReadOnlyList<AppEntry>> _appsProvider;

	private System.Windows.Controls.ContextMenu? _appMenu;

	private readonly List<System.Windows.Controls.Button> _navRows = new List<System.Windows.Controls.Button>();

	private int _selIndex = -1;

	private static readonly System.Windows.Media.SolidColorBrush SelBrush = CreateSelBrush();

	private readonly Action<AppEntry> _launch;

	// Optional workspace hooks (App wires them to StartScreen). Null in the benchmark/QA panes -> no workspace results there.
	private readonly Func<IReadOnlyList<string>>? _workspaceNames;

	private readonly Action<string>? _launchWorkspace;

	private readonly Border _root;

	private readonly TranslateTransform _slide = new TranslateTransform(320.0, 0.0);

	private readonly System.Windows.Controls.TextBox _box;

	private readonly System.Windows.Controls.Button _searchBtn;

	private readonly StackPanel _results;

	private readonly TextBlock _scopeLabel;

	private readonly TextBlock _watermark;

	private Scope _scope = Scope.Everywhere;

	private Popup? _scopePopup;

	private bool _hiding;

	private readonly Border _cardHost = new Border
	{
		Visibility = Visibility.Collapsed
	};

	private readonly IIntentRouter _router = new HeuristicIntentRouter();

	private DispatcherTimer? _entityDebounce;

	private CancellationTokenSource? _entityCts;

	private static readonly SettingEntry[] SettingsIndex = new SettingEntry[95]
	{
		new SettingEntry("Change PC settings", "ms-settings:", "settings pc control"),
		new SettingEntry("Personalization", "ms-settings:personalization", "theme wallpaper appearance customize colour"),
		new SettingEntry("Background", "ms-settings:personalization-background", "wallpaper desktop image picture background"),
		new SettingEntry("Colours", "ms-settings:colors", "accent colour color theme dark light title bar"),
		new SettingEntry("Lock screen", "ms-settings:lockscreen", "lock screen"),
		new SettingEntry("Themes", "ms-settings:themes", "theme"),
		new SettingEntry("Start", "ms-settings:personalization-start", "start menu"),
		new SettingEntry("Taskbar", "ms-settings:taskbar", "taskbar tray notification area"),
		new SettingEntry("Display", "ms-settings:display", "screen resolution monitor brightness scale display"),
		new SettingEntry("Night light", "ms-settings:nightlight", "night light blue"),
		new SettingEntry("Sound", "ms-settings:sound", "audio volume speakers microphone playback recording sound"),
		new SettingEntry("Network & internet", "ms-settings:network-status", "network internet wifi ethernet status"),
		new SettingEntry("Wi-Fi", "ms-settings:network-wifi", "wifi wireless wlan"),
		new SettingEntry("Ethernet", "ms-settings:network-ethernet", "ethernet lan wired"),
		new SettingEntry("VPN", "ms-settings:network-vpn", "vpn"),
		new SettingEntry("Proxy", "ms-settings:network-proxy", "proxy"),
		new SettingEntry("Airplane mode", "ms-settings:network-airplanemode", "airplane flight"),
		new SettingEntry("Mobile hotspot", "ms-settings:network-mobilehotspot", "hotspot tether"),
		new SettingEntry("Network connections", "ncpa.cpl", "adapter connections network control panel"),
		new SettingEntry("Bluetooth & devices", "ms-settings:bluetooth", "bluetooth devices"),
		new SettingEntry("Printers & scanners", "ms-settings:printers", "printer scanner print"),
		new SettingEntry("Mouse", "ms-settings:mousetouchpad", "mouse pointer cursor click"),
		new SettingEntry("Mouse properties", "main.cpl", "mouse pointer cursor buttons speed control panel"),
		new SettingEntry("Touchpad", "ms-settings:devices-touchpad", "touchpad trackpad"),
		new SettingEntry("Typing", "ms-settings:typing", "keyboard typing autocorrect"),
		new SettingEntry("Pen & Windows Ink", "ms-settings:pen", "pen stylus ink"),
		new SettingEntry("AutoPlay", "ms-settings:autoplay", "autoplay usb removable"),
		new SettingEntry("Power & sleep", "ms-settings:powersleep", "power sleep battery screen off"),
		new SettingEntry("Power options", "powercfg.cpl", "power plan high performance balanced control panel"),
		new SettingEntry("Battery", "ms-settings:batterysaver", "battery saver power"),
		new SettingEntry("Apps & features", "ms-settings:appsfeatures", "apps programs uninstall install features"),
		new SettingEntry("Programs and Features", "appwiz.cpl", "uninstall program remove software control panel"),
		new SettingEntry("Default apps", "ms-settings:defaultapps", "default apps browser mail associations"),
		new SettingEntry("Startup apps", "ms-settings:startupapps", "startup boot launch"),
		new SettingEntry("Accounts", "ms-settings:accounts", "account user profile"),
		new SettingEntry("Sign-in options", "ms-settings:signinoptions", "sign in password pin hello fingerprint face"),
		new SettingEntry("Other users", "ms-settings:otherusers", "users add account family"),
		new SettingEntry("User Accounts", "netplwiz", "users account control autologon"),
		new SettingEntry("Date & time", "ms-settings:dateandtime", "date time clock timezone"),
		new SettingEntry("Region", "ms-settings:regionformat", "region format locale country"),
		new SettingEntry("Language", "ms-settings:regionlanguage", "language input keyboard layout"),
		new SettingEntry("Ease of Access", "ms-settings:easeofaccess", "accessibility ease of access"),
		new SettingEntry("Narrator", "ms-settings:easeofaccess-narrator", "narrator screen reader"),
		new SettingEntry("Magnifier", "ms-settings:easeofaccess-magnifier", "magnifier zoom"),
		new SettingEntry("High contrast", "ms-settings:easeofaccess-highcontrast", "high contrast"),
		new SettingEntry("Privacy", "ms-settings:privacy", "privacy permissions"),
		new SettingEntry("Location", "ms-settings:privacy-location", "location gps"),
		new SettingEntry("Notifications", "ms-settings:notifications", "notifications action center"),
		new SettingEntry("Windows Update", "ms-settings:windowsupdate", "update windows upgrade"),
		new SettingEntry("Recovery", "ms-settings:recovery", "recovery reset reinstall"),
		new SettingEntry("Activation", "ms-settings:activation", "activation license product key"),
		new SettingEntry("Windows Security", "windowsdefender:", "defender antivirus security virus protection"),
		new SettingEntry("Windows Firewall", "firewall.cpl", "firewall network protection control panel"),
		new SettingEntry("About this PC", "ms-settings:about", "about system pc info specs device name"),
		new SettingEntry("Storage", "ms-settings:storagesense", "storage disk space cleanup"),
		new SettingEntry("Remote Desktop", "ms-settings:remotedesktop", "remote desktop rdp"),
		new SettingEntry("System properties", "sysdm.cpl", "system advanced environment variables performance control"),
		new SettingEntry("Control Panel", "control", "control panel classic"),
		new SettingEntry("Task Manager", "taskmgr", "task manager processes performance"),
		new SettingEntry("Device Manager", "devmgmt.msc", "device manager drivers hardware"),
		new SettingEntry("Services", "services.msc", "services background"),
		new SettingEntry("Disk Management", "diskmgmt.msc", "disk partition volume format management"),
		new SettingEntry("Event Viewer", "eventvwr.msc", "event viewer logs"),
		new SettingEntry("System Configuration", "msconfig", "msconfig boot startup services"),
		new SettingEntry("Registry Editor", "regedit", "registry editor regedit"),
		new SettingEntry("Character Map", "charmap", "character map special symbols"),
		new SettingEntry("On-Screen Keyboard", "osk", "on screen keyboard osk accessibility"),
		new SettingEntry("Fonts", "shell:Fonts", "fonts typeface"),
		new SettingEntry("Task Scheduler", "taskschd.msc", "task scheduler schedule automate tasks"),
		new SettingEntry("Resource Monitor", "resmon", "resource monitor resmon cpu memory disk network usage"),
		new SettingEntry("Performance Monitor", "perfmon", "performance monitor perfmon counters"),
		new SettingEntry("Computer Management", "compmgmt.msc", "computer management console"),
		new SettingEntry("System Information", "msinfo32", "system information msinfo specs hardware"),
		new SettingEntry("DirectX Diagnostic Tool", "dxdiag", "dxdiag directx graphics sound diagnostic"),
		new SettingEntry("Disk Cleanup", "cleanmgr", "disk cleanup free space temporary files clean"),
		new SettingEntry("Windows Features", "optionalfeatures", "windows features turn on off optional install"),
		new SettingEntry("Local Group Policy Editor", "gpedit.msc", "group policy gpedit"),
		new SettingEntry("Local Security Policy", "secpol.msc", "local security policy secpol"),
		new SettingEntry("Defragment and Optimize Drives", "dfrgui", "defragment optimize drives defrag ssd trim"),
		new SettingEntry("Windows Memory Diagnostic", "mdsched", "memory diagnostic ram test"),
		new SettingEntry("Steps Recorder", "psr", "steps recorder problem steps record repro"),
		new SettingEntry("Print Management", "printmanagement.msc", "print management printers spooler"),
		new SettingEntry("Advanced display", "ms-settings:display-advanced", "refresh rate hz hertz 144 120 60 advanced display change screen hz"),
		new SettingEntry("Text size", "ms-settings:easeofaccess-display", "text size make text bigger bigger text larger font accessibility"),
		new SettingEntry("Microphone privacy", "ms-settings:privacy-microphone", "microphone mic privacy permissions"),
		new SettingEntry("Camera privacy", "ms-settings:privacy-webcam", "camera webcam privacy permissions"),
		new SettingEntry("Clipboard", "ms-settings:clipboard", "clipboard history sync paste"),
		new SettingEntry("Focus assist", "ms-settings:quiethours", "focus assist quiet hours do not disturb dnd notifications"),
		new SettingEntry("Multitasking", "ms-settings:multitasking", "multitasking snap windows virtual desktops"),
		new SettingEntry("App volume and device preferences", "ms-settings:apps-volume", "app volume mixer per app sound"),
		new SettingEntry("Backup", "ms-settings:backup", "backup file history restore"),
		new SettingEntry("For developers", "ms-settings:developers", "developer mode developers"),
		new SettingEntry("Optional features", "ms-settings:optionalfeatures", "optional features add remove language"),
		new SettingEntry("Graphics settings", "ms-settings:display-advancedgraphics", "graphics gpu performance preference"),
		new SettingEntry("Projecting to this PC", "ms-settings:project", "project cast wireless display connect")
	};

	private static readonly (string Alias, string Target)[] AppAliases = new(string, string)[37]
	{
		("cmd", "command prompt"),
		("command prompt", "command prompt"),
		("powershell", "powershell"),
		("ps", "powershell"),
		("pwsh", "powershell"),
		("terminal", "terminal"),
		("calc", "calculator"),
		("regedit", "registry editor"),
		("registry", "registry editor"),
		("notepad", "notepad"),
		("paint", "paint"),
		("mspaint", "paint"),
		("explorer", "file explorer"),
		("files", "file explorer"),
		("file explorer", "file explorer"),
		("taskmgr", "task manager"),
		("task manager", "task manager"),
		("code", "visual studio code"),
		("vscode", "visual studio code"),
		("vs code", "visual studio code"),
		("word", "word"),
		("excel", "excel"),
		("ppt", "powerpoint"),
		("powerpoint", "powerpoint"),
		("outlook", "outlook"),
		("onenote", "onenote"),
		("chrome", "chrome"),
		("firefox", "firefox"),
		("edge", "edge"),
		("photos", "photos"),
		("pics", "photos"),
		("snip", "snipping"),
		("screenshot", "snipping"),
		("snipping", "snipping"),
		("spotify", "spotify"),
		("discord", "discord"),
		("steam", "steam")
	};

	// Greek → English search term (spec §9). Both accented + unaccented forms so either typing works.
	private static readonly (string Greek, string Target)[] GreekAliases = new(string, string)[46]
	{
		("ρυθμίσεις", "settings"), ("ρυθμισεις", "settings"),
		("διαχείριση εργασιών", "task manager"), ("διαχειριση εργασιων", "task manager"),
		("πίνακας ελέγχου", "control panel"), ("πινακας ελεγχου", "control panel"),
		("ήχος", "sound"), ("ηχος", "sound"),
		("οθόνη", "display"), ("οθονη", "display"),
		("δίκτυο", "network"), ("δικτυο", "network"),
		("συσκευές", "devices"), ("συσκευες", "devices"),
		("εκτυπωτές", "printers"), ("εκτυπωτες", "printers"),
		("πληκτρολόγιο", "keyboard"), ("πληκτρολογιο", "keyboard"),
		("ποντίκι", "mouse"), ("ποντικι", "mouse"),
		("φωτεινότητα", "brightness"), ("φωτεινοτητα", "brightness"),
		("μπλουτουθ", "bluetooth"), ("μπλουτούθ", "bluetooth"),
		("αριθμομηχανή", "calculator"), ("αριθμομηχανη", "calculator"),
		("σημειωματάριο", "notepad"), ("σημειωματαριο", "notepad"),
		("ζωγραφική", "paint"), ("ζωγραφικη", "paint"),
		("εξερεύνηση", "file explorer"), ("εξερευνηση", "file explorer"),
		("χρώμα", "color"), ("χρωμα", "color"),
		("γλώσσα", "language"), ("γλωσσα", "language"),
		("ενημέρωση", "update"), ("ενημερωση", "update"),
		("μπαταρία", "battery"), ("μπαταρια", "battery"),
		("φόντο", "background"), ("φοντο", "background"),
		("προσωποποίηση", "personalization"), ("προσωποποιηση", "personalization"),
		("ασφάλεια", "security"), ("ασφαλεια", "security")
	};

	// Expand a lowercased query into scoring terms: the raw query, the Greek→Latin keyboard-layout
	// recovery (when Greek letters are present), plus any Greek-alias / app-alias targets (prefix-matched).
	private static List<string> ExpandTerms(string ql)
	{
		List<string> terms = new List<string> { ql };
		string latin = GreekLayout.ToLatinKeys(ql);
		if (latin.Length > 0 && !terms.Contains(latin))
		{
			terms.Add(latin);
		}
		(string, string)[] greek = GreekAliases;
		for (int i = 0; i < greek.Length; i++)
		{
			var (gr, target) = greek[i];
			if ((ql.StartsWith(gr) || gr.StartsWith(ql)) && !terms.Contains(target))
			{
				terms.Add(target);
			}
		}
		(string, string)[] app = AppAliases;
		for (int j = 0; j < app.Length; j++)
		{
			var (alias, target2) = app[j];
			if ((alias.StartsWith(ql) || ql.StartsWith(alias)) && !terms.Contains(target2))
			{
				terms.Add(target2);
			}
		}
		return terms;
	}

	private static string G(int cp)
	{
		return ((char)cp).ToString();
	}

	public SearchPane(Func<IReadOnlyList<AppEntry>> appsProvider, Action<AppEntry> launch, Func<IReadOnlyList<string>>? workspaceNames = null, Action<string>? launchWorkspace = null)
	{
		_appsProvider = appsProvider;
		_launch = launch;
		_workspaceNames = workspaceNames;
		_launchWorkspace = launchWorkspace;
		base.WindowStyle = WindowStyle.None;
		base.ResizeMode = ResizeMode.NoResize;
		base.ShowInTaskbar = false;
		base.AllowsTransparency = true;
		base.Background = System.Windows.Media.Brushes.Transparent;
		base.Topmost = true;
		base.Width = 320.0;
		base.Title = "Search";
		_root = new Border
		{
			Background = PaneBg(),
			RenderTransform = _slide
		};
		Grid grid = new Grid
		{
			Margin = new Thickness(20.0, 20.0, 20.0, 18.0)
		};
		grid.RowDefinitions.Add(new RowDefinition
		{
			Height = GridLength.Auto
		});
		grid.RowDefinitions.Add(new RowDefinition
		{
			Height = GridLength.Auto
		});
		grid.RowDefinitions.Add(new RowDefinition
		{
			Height = GridLength.Auto
		});
		grid.RowDefinitions.Add(new RowDefinition
		{
			Height = GridLength.Auto
		});
		grid.RowDefinitions.Add(new RowDefinition
		{
			Height = new GridLength(1.0, GridUnitType.Star)
		});
		TextBlock header = new TextBlock
		{
			Text = "Search",
			Foreground = System.Windows.Media.Brushes.White,
			FontFamily = new System.Windows.Media.FontFamily("Segoe UI Light"),
			FontSize = 34.0,
			Margin = new Thickness(0.0, 0.0, 0.0, 10.0)
		};
		Grid.SetRow(header, 0);
		grid.Children.Add(header);
		_scopeLabel = new TextBlock
		{
			Text = "Everywhere",
			Foreground = System.Windows.Media.Brushes.White,
			FontFamily = new System.Windows.Media.FontFamily("Segoe UI"),
			FontSize = 13.0,
			VerticalAlignment = VerticalAlignment.Center
		};
		StackPanel scopeRow = new StackPanel
		{
			Orientation = System.Windows.Controls.Orientation.Horizontal
		};
		scopeRow.Children.Add(_scopeLabel);
		scopeRow.Children.Add(new TextBlock
		{
			Text = G(59149),
			FontFamily = new System.Windows.Media.FontFamily("Segoe MDL2 Assets"),
			FontSize = 9.0,
			Foreground = new SolidColorBrush(System.Windows.Media.Color.FromArgb(204, byte.MaxValue, byte.MaxValue, byte.MaxValue)),
			VerticalAlignment = VerticalAlignment.Center,
			Margin = new Thickness(7.0, 2.0, 0.0, 0.0)
		});
		System.Windows.Controls.Button scopeBtn = new System.Windows.Controls.Button
		{
			Content = scopeRow,
			Background = System.Windows.Media.Brushes.Transparent,
			BorderThickness = new Thickness(0.0),
			Cursor = System.Windows.Input.Cursors.Hand,
			FocusVisualStyle = null,
			HorizontalAlignment = System.Windows.HorizontalAlignment.Left,
			Padding = new Thickness(0.0, 2.0, 0.0, 10.0),
			Template = TransparentButtonTemplate()
		};
		scopeBtn.Click += delegate
		{
			ToggleScopePopup(scopeBtn);
		};
		Grid.SetRow(scopeBtn, 1);
		grid.Children.Add(scopeBtn);
		Border boxOuter = new Border
		{
			Background = System.Windows.Media.Brushes.White,
			Height = 36.0,
			Margin = new Thickness(0.0, 5.0, 0.0, 0.0)
		};
		Grid boxGrid = new Grid
		{
			ColumnDefinitions = 
			{
				new ColumnDefinition(),
				new ColumnDefinition
				{
					Width = GridLength.Auto
				}
			}
		};
		_box = new System.Windows.Controls.TextBox
		{
			Background = System.Windows.Media.Brushes.Transparent,
			Foreground = System.Windows.Media.Brushes.Black,
			CaretBrush = System.Windows.Media.Brushes.Black,
			BorderThickness = new Thickness(0.0),
			FontFamily = new System.Windows.Media.FontFamily("Segoe UI"),
			FontSize = 15.0,
			Padding = new Thickness(2.0, 0.0, 4.0, 0.0),
			VerticalContentAlignment = VerticalAlignment.Center
		};
		_box.TextChanged += delegate
		{
			// Hide the placeholder INSTANTLY (before the debounced rebuild) so it never overlaps typed text.
			_watermark.Visibility = ((_box.Text.Length != 0) ? Visibility.Collapsed : Visibility.Visible);
			// Debounce the O(apps x terms) match + full results-panel rebuild + card classifiers: only the last
			// keystroke of a fast burst does the work. Keyboard nav (OnBoxKey) flushes this first so Up/Down/Enter
			// always act on the current results.
			if (_rebuildDebounce == null)
			{
				_rebuildDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(110L) };
				_rebuildDebounce.Tick += delegate
				{
					_rebuildDebounce!.Stop();
					RunSearchRebuild();
				};
			}
			_rebuildDebounce.Stop();
			_rebuildDebounce.Start();
		};
		_box.SetResourceReference(FrameworkElement.StyleProperty, "Metro81.TextBox");
		System.Windows.Automation.AutomationProperties.SetName(_box, "Search launcher");
		_box.PreviewKeyDown += OnBoxKey;
		Grid.SetColumn(_box, 0);
		_watermark = new TextBlock
		{
			Text = "Search",
			Foreground = new SolidColorBrush(System.Windows.Media.Color.FromArgb(153, 0, 0, 0)),
			FontFamily = new System.Windows.Media.FontFamily("Segoe UI"),
			FontSize = 15.0,
			IsHitTestVisible = false,
			VerticalAlignment = VerticalAlignment.Center,
			Margin = new Thickness(8.0, 0.0, 0.0, 0.0)
		};
		Grid.SetColumn(_watermark, 0);
		_searchBtn = new System.Windows.Controls.Button
		{
			Width = 28.0,
			Height = 28.0,
			Margin = new Thickness(0.0, 0.0, 3.0, 0.0),
			VerticalAlignment = VerticalAlignment.Center,
			Background = new SolidColorBrush(SettingsPane.AccentToneColor(0.46)),
			BorderThickness = new Thickness(0.0),
			Cursor = System.Windows.Input.Cursors.Hand,
			FocusVisualStyle = null,
			Content = new TextBlock
			{
				Text = G(57626),
				FontFamily = new System.Windows.Media.FontFamily("Segoe UI Symbol"),
				FontSize = 16.0,
				Foreground = System.Windows.Media.Brushes.White,
				HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
				VerticalAlignment = VerticalAlignment.Center
			},
			Template = AccentButtonTemplate()
		};
		_searchBtn.Click += delegate
		{
			DoSearch();
		};
		_searchBtn.ClearValue(System.Windows.Controls.Control.TemplateProperty);
		_searchBtn.ClearValue(System.Windows.Controls.Control.FocusVisualStyleProperty);
		_searchBtn.SetResourceReference(FrameworkElement.StyleProperty, "Metro81.CommandButton");
		System.Windows.Automation.AutomationProperties.SetName(_searchBtn, "Search");
		Grid.SetColumn(_searchBtn, 1);
		boxGrid.Children.Add(_box);
		boxGrid.Children.Add(_watermark);
		boxGrid.Children.Add(_searchBtn);
		boxOuter.Child = boxGrid;
		Grid.SetRow(boxOuter, 2);
		grid.Children.Add(boxOuter);
		TextBlock helper = new TextBlock
		{
			Text = "Type to search apps, settings, files and the web.",
			Foreground = new SolidColorBrush(System.Windows.Media.Color.FromArgb(153, byte.MaxValue, byte.MaxValue, byte.MaxValue)),
			FontFamily = new System.Windows.Media.FontFamily("Segoe UI"),
			FontSize = 12.0,
			TextWrapping = TextWrapping.Wrap,
			Margin = new Thickness(0.0, 16.0, 0.0, 8.0)
		};
		Grid.SetRow(helper, 3);
		grid.Children.Add(helper);
		ScrollViewer scroller = new ScrollViewer
		{
			VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
			HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
		};
		try { if (System.Windows.Application.Current.TryFindResource("Metro81.ScrollBarDark") is Style sbDark) scroller.Resources[typeof(System.Windows.Controls.Primitives.ScrollBar)] = new Style(typeof(System.Windows.Controls.Primitives.ScrollBar), sbDark); } catch { }   // dark pane - the light pattern rail is for white surfaces
		_results = new StackPanel();
		scroller.Content = new StackPanel
		{
			Children = 
			{
				(UIElement)_cardHost,
				(UIElement)_results
			}
		};
		Grid.SetRow(scroller, 4);
		grid.Children.Add(scroller);
		_root.Child = grid;
		base.Content = _root;
		base.Deactivated += delegate
		{
			Popup? scopePopup = _scopePopup;
			if ((scopePopup == null || !scopePopup.IsOpen) && (_appMenu == null || !_appMenu.IsOpen))
			{
				HidePane();
			}
		};
		// Bind keyboard focus to the box every time the pane truly becomes the active window. This is the
		// reliable moment (after ForceForeground's cross-process race resolves) — an earlier one-shot focus in
		// ShowPane can land while the box only holds LOGICAL focus, which is why Backspace/Delete were lost.
		base.Activated += delegate
		{
			if (_hiding || !base.IsVisible)
			{
				return;
			}
			if ((_scopePopup != null && _scopePopup.IsOpen) || (_appMenu != null && _appMenu.IsOpen))
			{
				return;
			}
			((DispatcherObject)this).Dispatcher.BeginInvoke((Delegate)(Action)delegate
			{
				if (base.IsVisible && !_hiding)
				{
					_box.Focus();
					Keyboard.Focus(_box);
				}
			}, (DispatcherPriority)5, Array.Empty<object>());
		};
		base.PreviewKeyDown += delegate(object _, System.Windows.Input.KeyEventArgs e)
		{
			//IL_0002: Unknown result type (might be due to invalid IL or missing references)
			//IL_0009: Invalid comparison between Unknown and I4
			if ((int)e.Key == 13)
			{
				HidePane();
				return;
			}
			// ALWAYS own Back/Delete here: the box's focus is often logical-only (chars fed externally) and even a
			// truly-focused box's split-focus state is unreliable. Editing the text directly + e.Handled=true stops
			// the tunnel, so a properly-focused box never double-deletes; then re-grab keyboard focus for later keys.
			if (e.Key == System.Windows.Input.Key.Back || e.Key == System.Windows.Input.Key.Delete)
			{
				string t = _box.Text;
				int ci = System.Math.Clamp(_box.CaretIndex, 0, t.Length);
				if (_box.SelectionLength > 0)
				{
					int st = _box.SelectionStart;
					_box.Text = t.Remove(st, _box.SelectionLength);
					_box.CaretIndex = st;
				}
				else if (e.Key == System.Windows.Input.Key.Back && ci > 0)
				{
					_box.Text = t.Remove(ci - 1, 1);
					_box.CaretIndex = ci - 1;
				}
				else if (e.Key == System.Windows.Input.Key.Delete && ci < t.Length)
				{
					_box.Text = t.Remove(ci, 1);
					_box.CaretIndex = ci;
				}
				e.Handled = true;
				_box.Focus();
				Keyboard.Focus(_box);
			}
		};
	}

	private static System.Windows.Media.Brush PaneBg()
	{
		return SettingsPane.PaneBg();
	}

	private void QueueEntityLookup()
	{
		//IL_000a: Unknown result type (might be due to invalid IL or missing references)
		//IL_000f: Unknown result type (might be due to invalid IL or missing references)
		//IL_0026: Expected O, but got Unknown
		if (_entityDebounce == null)
		{
			_entityDebounce = new DispatcherTimer
			{
				Interval = TimeSpan.FromMilliseconds(300L)
			};
		}
		_entityDebounce.Tick -= OnEntityTick;
		_entityDebounce.Tick += OnEntityTick;
		_entityDebounce.Stop();
		_entityDebounce.Start();
	}

	private void ClearEntityCard()
	{
		_entityCts?.Cancel();
		_cardHost.Child = null;
		_cardHost.Visibility = Visibility.Collapsed;
	}

	// Instant, synchronous calculator card (no debounce, no network). Returns true if it showed a result,
	// in which case the async place lookup is skipped. Classifies first so settings only load for math queries.
	private bool TryShowCalcCard()
	{
		try
		{
			string q = _box.Text.Trim();
			if (q.Length == 0)
			{
				return false;
			}
			if (_router.Classify(Query.Of(q)).Kind != IntentKind.Calc)
			{
				return false;
			}
			if (!SettingsStore.Load().SearchEntityCards)
			{
				return false;
			}
			EntityCard card = CalcEngine.TryBuild(q);
			if ((object)card == null)
			{
				return false;
			}
			_entityCts?.Cancel();       // cancel any in-flight place lookup
			_entityDebounce?.Stop();    // and don't let a queued place tick clobber this card
			_cardHost.Child = MetroComposer.BuildCalcCard(card);
			_cardHost.Visibility = Visibility.Visible;
			return true;
		}
		catch
		{
			return false;
		}
	}

	// Instant OFFLINE unit conversion card ("10 km to miles", "5 kg in lb", "100 f to c"). Mirrors TryShowCalcCard:
	// synchronous, no debounce, cancels any in-flight place lookup, reuses the calc card renderer. Cheap connector
	// gate first; UnitConvert.TryBuild is the authority (returns null → the normal result list is used instead).
	private bool TryShowConvertCard()
	{
		try
		{
			string q = _box.Text.Trim();
			if (q.Length < 3)
			{
				return false;
			}
			if (q.IndexOf(" to ", StringComparison.OrdinalIgnoreCase) < 0
				&& q.IndexOf(" in ", StringComparison.OrdinalIgnoreCase) < 0
				&& q.IndexOf(" into ", StringComparison.OrdinalIgnoreCase) < 0
				&& q.IndexOf("->", StringComparison.Ordinal) < 0)
			{
				return false;
			}
			if (!SettingsStore.Load().SearchEntityCards)
			{
				return false;
			}
			EntityCard card = UnitConvert.TryBuild(q);
			if ((object)card == null)
			{
				return false;
			}
			_entityCts?.Cancel();       // cancel any in-flight place lookup
			_entityDebounce?.Stop();    // and don't let a queued place tick clobber this card
			_cardHost.Child = MetroComposer.BuildCalcCard(card);
			_cardHost.Visibility = Visibility.Visible;
			return true;
		}
		catch
		{
			return false;
		}
	}

	// Instant OFFLINE date card ("days until 2027-01-01", "days since 2020-03-01", "days between A and B"). Mirrors the
	// calc/convert cards: synchronous, cheap "day" prefix gate, DateEngine.TryBuild is the authority, reuses the renderer.
	private bool TryShowDateCard()
	{
		try
		{
			string q = _box.Text.Trim();
			if (q.Length < 8 || !q.StartsWith("day", StringComparison.OrdinalIgnoreCase))
			{
				return false;
			}
			if (!SettingsStore.Load().SearchEntityCards)
			{
				return false;
			}
			EntityCard card = DateEngine.TryBuild(q);
			if ((object)card == null)
			{
				return false;
			}
			_entityCts?.Cancel();
			_entityDebounce?.Stop();
			_cardHost.Child = MetroComposer.BuildCalcCard(card);
			_cardHost.Visibility = Visibility.Visible;
			return true;
		}
		catch
		{
			return false;
		}
	}

	// Instant OFFLINE number-base card ("255 to hex", "0xFF to dec", "1010 to binary"). Mirrors the other cards:
	// synchronous, cheap connector+base-word gate, BaseEngine.TryBuild is the authority, reuses the calc renderer.
	private bool TryShowBaseCard()
	{
		try
		{
			string q = _box.Text.Trim();
			if (q.Length < 5)
			{
				return false;
			}
			bool hasConn = q.IndexOf(" to ", StringComparison.OrdinalIgnoreCase) >= 0
				|| q.IndexOf(" in ", StringComparison.OrdinalIgnoreCase) >= 0
				|| q.IndexOf(" as ", StringComparison.OrdinalIgnoreCase) >= 0
				|| q.IndexOf("->", StringComparison.Ordinal) >= 0;
			bool hasBase = q.IndexOf("hex", StringComparison.OrdinalIgnoreCase) >= 0
				|| q.IndexOf("bin", StringComparison.OrdinalIgnoreCase) >= 0
				|| q.IndexOf("oct", StringComparison.OrdinalIgnoreCase) >= 0
				|| q.IndexOf("dec", StringComparison.OrdinalIgnoreCase) >= 0;
			if (!hasConn || !hasBase)
			{
				return false;
			}
			if (!SettingsStore.Load().SearchEntityCards)
			{
				return false;
			}
			EntityCard card = BaseEngine.TryBuild(q);
			if ((object)card == null)
			{
				return false;
			}
			_entityCts?.Cancel();
			_entityDebounce?.Stop();
			_cardHost.Child = MetroComposer.BuildCalcCard(card);
			_cardHost.Visibility = Visibility.Visible;
			return true;
		}
		catch
		{
			return false;
		}
	}

	// Instant OFFLINE color card ("#2672EC", "rgb(38,114,236)"). Mirrors the other cards: synchronous, cheap
	// "#"/"rgb" gate, ColorEngine.TryBuild is the authority, uses the dedicated swatch renderer BuildColorCard.
	private bool TryShowColorCard()
	{
		try
		{
			string q = _box.Text.Trim();
			if (q.Length < 4)
			{
				return false;
			}
			if (!q.StartsWith("#") && !q.StartsWith("rgb", StringComparison.OrdinalIgnoreCase))
			{
				return false;
			}
			if (!SettingsStore.Load().SearchEntityCards)
			{
				return false;
			}
			EntityCard card = ColorEngine.TryBuild(q);
			if ((object)card == null)
			{
				return false;
			}
			_entityCts?.Cancel();
			_entityDebounce?.Stop();
			_cardHost.Child = MetroComposer.BuildColorCard(card);
			_cardHost.Visibility = Visibility.Visible;
			return true;
		}
		catch
		{
			return false;
		}
	}

	private void OnEntityTick(object? sender, EventArgs e)
	{
		DispatcherTimer? entityDebounce = _entityDebounce;
		if (entityDebounce != null)
		{
			entityDebounce.Stop();
		}
		if (_scope != Scope.Everywhere || !SettingsStore.FastSnapshot.SearchEntityCards)
		{
			ClearEntityCard();
			return;
		}
		string q = _box.Text.Trim();
		if (q.Length == 0)
		{
			ClearEntityCard();
			return;
		}
		Query query = Query.Of(q);
		Intent intent = _router.Classify(query);
		if (intent.Kind != IntentKind.Place)
		{
			_cardHost.Child = null;
			_cardHost.Visibility = Visibility.Collapsed;
			return;
		}
		// Fetch a verified place only after explicit navigation, not on every typed query.
		string placeQuery = intent.EntityHint ?? q;
		_cardHost.Child = ActionRow(G(57657), "Explore " + placeQuery, () => ExplorePlace(placeQuery));
		_cardHost.Visibility = Visibility.Visible;
	}

	// Open Search pre-filled with a query (used by the launcher://search action route). Text change drives Rebuild.
	public void ShowQuery(string q)
	{
		ShowPane();
		try
		{
			_box.Text = q ?? "";
			_box.CaretIndex = _box.Text.Length;
			_box.Focus();
		}
		catch (Exception ex)
		{
			Logger.Log("SearchPane.ShowQuery: " + ex.Message);
		}
	}

	public void ShowPane()
	{
		//IL_009c: Unknown result type (might be due to invalid IL or missing references)
		//IL_00a1: Unknown result type (might be due to invalid IL or missing references)
		//IL_00e7: Unknown result type (might be due to invalid IL or missing references)
		//IL_00ec: Unknown result type (might be due to invalid IL or missing references)
		_hiding = false;
		if (_appMenu != null)
		{
			_appMenu.IsOpen = false;
			_appMenu = null;
		}
		_root.Background = PaneBg();
		_searchBtn.Background = new SolidColorBrush(SettingsPane.AccentToneColor(0.46));
		_box.Text = string.Empty;
		_scope = Scope.Everywhere;
		_scopeLabel.Text = "Everywhere";
		Rebuild();
		ClearEntityCard();
		Show();
		ShellSkin.ApplyGlass(this);   // defensively clear any stale accent region; glass look comes from PaneBg alpha
		Screen screen = Screen.FromPoint(System.Windows.Forms.Cursor.Position);
		Rectangle b = screen.Bounds;
		PresentationSource src = PresentationSource.FromVisual(this);
		double? num;
		Matrix transformToDevice;
		if (src == null)
		{
			num = null;
		}
		else
		{
			CompositionTarget compositionTarget = src.CompositionTarget;
			if (compositionTarget == null)
			{
				num = null;
			}
			else
			{
				transformToDevice = compositionTarget.TransformToDevice;
				num = transformToDevice.M11;
			}
		}
		double sx = num ?? 1.0;
		double? num2;
		if (src == null)
		{
			num2 = null;
		}
		else
		{
			CompositionTarget compositionTarget2 = src.CompositionTarget;
			if (compositionTarget2 == null)
			{
				num2 = null;
			}
			else
			{
				transformToDevice = compositionTarget2.TransformToDevice;
				num2 = transformToDevice.M22;
			}
		}
		double sy = num2 ?? 1.0;
		if (sx <= 0.0)
		{
			sx = 1.0;
		}
		if (sy <= 0.0)
		{
			sy = 1.0;
		}
		sx = MonitorDpi.ScaleFor(b); if (sx <= 0.0) sx = 1.0; sy = sx;   // target-monitor DPI (mixed-DPI dual-monitor fix)
		base.Height = (double)b.Height / sy;
		base.Top = (double)b.Top / sy;
		base.Left = (double)b.Right / sx - base.Width;
		WindowUtil.ForceForeground(this);
		_slide.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(0.0, Motion.Dur(Motion.Cat.EdgeEnter))
		{
			EasingFunction = Motion.Ease(Motion.Cat.EdgeEnter)
		});
		((DispatcherObject)this).Dispatcher.BeginInvoke((Delegate)(Action)delegate
		{
			Activate();                 // make this the active WPF input site (after ForceForeground won the cross-process race)
			Keyboard.Focus(_box);       // bind the KeyboardDevice to _box so Backspace/Delete/arrows/selection route to it
		}, (DispatcherPriority)5, Array.Empty<object>());
	}

	public void HidePane()
	{
		if (_hiding || !base.IsVisible)
		{
			return;
		}
		_hiding = true;
		if (_appMenu != null)
		{
			_appMenu.IsOpen = false;
			_appMenu = null;
		}
		_entityCts?.Cancel();
		DispatcherTimer? entityDebounce = _entityDebounce;
		if (entityDebounce != null)
		{
			entityDebounce.Stop();
		}
		if (_scopePopup != null)
		{
			_scopePopup.IsOpen = false;
		}
		DoubleAnimation slide = new DoubleAnimation(320.0, Motion.Dur(Motion.Cat.EdgeExit))
		{
			EasingFunction = Motion.Ease(Motion.Cat.EdgeExit)
		};
		slide.Completed += delegate
		{
			if (_hiding)
			{
				Hide();
				_hiding = false;
			}
		};
		_slide.BeginAnimation(TranslateTransform.XProperty, slide);
	}

	private DispatcherTimer? _rebuildDebounce;

	// The full local rebuild: results-panel match + the instant card classifiers, falling through to the debounced
	// place/entity lookup. Called by the debounce tick and by FlushRebuild.
	private void RunSearchRebuild()
	{
		Rebuild();
		if (!TryShowCalcCard() && !TryShowConvertCard() && !TryShowDateCard() && !TryShowBaseCard() && !TryShowColorCard())
		{
			QueueEntityLookup();
		}
	}

	// If a debounced rebuild is still pending, run it NOW so keyboard navigation sees the current results.
	private void FlushRebuild()
	{
		if (_rebuildDebounce != null && _rebuildDebounce.IsEnabled)
		{
			_rebuildDebounce.Stop();
			RunSearchRebuild();
		}
	}

	private void OnBoxKey(object? sender, System.Windows.Input.KeyEventArgs e)
	{
		switch (e.Key)
		{
		case System.Windows.Input.Key.Down:
			FlushRebuild();
			MoveSelection(1);
			e.Handled = true;
			break;
		case System.Windows.Input.Key.Up:
			FlushRebuild();
			MoveSelection(-1);
			e.Handled = true;
			break;
		case System.Windows.Input.Key.Return:
			FlushRebuild();
			// Enter runs the selected row (Best Match by default); falls back to launch-top-or-web
			if (_selIndex >= 0 && _selIndex < _navRows.Count)
			{
				_navRows[_selIndex].RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
			}
			else
			{
				DoSearch();
			}
			e.Handled = true;
			break;
		}
	}

	private static System.Windows.Media.SolidColorBrush CreateSelBrush()
	{
		System.Windows.Media.SolidColorBrush b = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(64, byte.MaxValue, byte.MaxValue, byte.MaxValue));
		b.Freeze();
		return b;
	}

	// Rebuild the keyboard-nav row list from the current results; Best Match = the first (top-ranked) row.
	private void RefreshNav()
	{
		_navRows.Clear();
		foreach (UIElement child in _results.Children)
		{
			if (child is System.Windows.Controls.Button btn)
			{
				_navRows.Add(btn);
			}
		}
		_selIndex = ((_navRows.Count > 0) ? 0 : (-1));
		ApplySelection();
	}

	private void MoveSelection(int delta)
	{
		if (_navRows.Count != 0)
		{
			_selIndex = ((_selIndex < 0) ? ((delta > 0) ? 0 : (_navRows.Count - 1)) : Math.Clamp(_selIndex + delta, 0, _navRows.Count - 1));
			ApplySelection();
		}
	}

	private void ApplySelection()
	{
		// Pattern 10/13: the keyboard-selected row carries the ACCENT, not a faint white wash. Fetched live so an
		// accent change mid-session is picked up on the next arrow-key move.
		System.Windows.Media.Brush sel = SelBrush;
		try { if (System.Windows.Application.Current.Resources["MenuAccent"] is System.Windows.Media.Brush mb) sel = mb; } catch { }
		for (int i = 0; i < _navRows.Count; i++)
		{
			_navRows[i].Background = ((i == _selIndex) ? sel : System.Windows.Media.Brushes.Transparent);
		}
		if (_selIndex >= 0 && _selIndex < _navRows.Count)
		{
			_navRows[_selIndex].BringIntoView();
		}
	}

	private void DoSearch()
	{
		string q = _box.Text.Trim();
		if (q.Length == 0)
		{
			_box.Focus();
			return;
		}
		if (_scope == Scope.Places) { ExplorePlace(q); return; }
		if (_scope == Scope.Web) { WebSearch(q); return; }
		if (_scope == Scope.Files) { FileSearch(q); return; }
		if (_scope == Scope.Settings)
		{
			var setting = MatchSettings(q, 1);
			if (setting.Count > 0) { HidePane(); Launch(setting[0].Uri); }
			return;
		}
		List<AppEntry> app = MatchApps(q, 1);
		if (app.Count > 0)
		{
			LaunchApp(app[0]);
		}
		else
		{
			Intent intent = _router.Classify(Query.Of(q));
			if (intent.Kind == IntentKind.Place) ExplorePlace(intent.EntityHint ?? q);
			else WebSearch(q);
		}
	}

	private void Rebuild()
	{
		_results.Children.Clear();
		string q = _box.Text.Trim();
		_watermark.Visibility = ((q.Length != 0) ? Visibility.Collapsed : Visibility.Visible);
		if (q.Length == 0)
		{
			RefreshNav();
			return;
		}
		bool any = false;
		if (_scope == Scope.Everywhere)
		{
			if (TryAddNaturalCommand(q))
			{
				any = true;
			}
			if (TryAddWorkspace(q))
			{
				any = true;
			}
			List<AppEntry> apps = MatchApps(q, 8);
			if (apps.Count > 0)
			{
				any = true;
				_results.Children.Add(SectionHeader("Apps"));
				foreach (AppEntry a in apps)
				{
					_results.Children.Add(AppRow(a));
				}
			}
			List<CommandEntry> cmds = MatchCommands(q, 4);
			if (cmds.Count > 0)
			{
				any = true;
				_results.Children.Add(SectionHeader("Commands"));
				foreach (CommandEntry c in cmds)
				{
					_results.Children.Add(CommandRow(c));
				}
			}
			List<SettingEntry> settings = MatchSettings(q, 6);
			if (settings.Count > 0)
			{
				any = true;
				_results.Children.Add(SectionHeader("Settings"));
				foreach (SettingEntry s in settings)
				{
					_results.Children.Add(UriRow(G(57621), s.Label, s.Uri));
				}
			}
		}
		else if (_scope == Scope.Settings)
		{
			List<SettingEntry> settings2 = MatchSettings(q, 20);
			if (settings2.Count > 0)
			{
				any = true;
				_results.Children.Add(SectionHeader("Settings"));
				foreach (SettingEntry s2 in settings2)
				{
					_results.Children.Add(UriRow(G(57621), s2.Label, s2.Uri));
				}
			}
		}
		Scope scope = _scope;
		if (scope is Scope.Everywhere or Scope.Places)
		{
			_results.Children.Add(SectionHeader("Places"));
			_results.Children.Add(ActionRow(G(57657), "Explore \u201c" + q + "\u201d", () => ExplorePlace(q)));
			any = true;
		}
		if ((scope == Scope.Everywhere || scope == Scope.Web) ? true : false)
		{
			{
					List<BrowserData.Entry> browserHits = BrowserData.Search(q, 6);
					if (browserHits.Count > 0)
					{
						_results.Children.Add(SectionHeader("Browser"));
						foreach (BrowserData.Entry h in browserHits)
						{
							string bu = h.Url;
							_results.Children.Add(ActionRow(G(h.Bookmark ? 59188 : 59420), h.Title + "  -  " + BrowserData.Host(bu), delegate
							{
								HidePane();
								WebOpen.Url(bu);
							}));
						}
					}
				}
				_results.Children.Add(SectionHeader("Web"));
			_results.Children.Add(ActionRow(G(57626), "Search the web for “" + q + "”", delegate
			{
				WebSearch(q);
			}));
			any = true;
		}
		scope = _scope;
		if ((scope == Scope.Everywhere || scope == Scope.Files) ? true : false)
		{
			_results.Children.Add(SectionHeader("Files"));
			_results.Children.Add(ActionRow(G(57736), "Search files for “" + q + "”", delegate
			{
				FileSearch(q);
			}));
			any = true;
		}
		if (!any)
		{
			_results.Children.Add(Hint("No results for “" + q + "”."));
		}
		RefreshNav();
	}

	private List<AppEntry> MatchApps(string q, int max)
	{
		string ql = q.ToLowerInvariant();
		try
		{
			List<string> terms = ExpandTerms(ql);
			List<(AppEntry, int)> scored = new List<(AppEntry, int)>();
			foreach (AppEntry a in _appsProvider())
			{
				int best = 0;
				foreach (string term in terms)
				{
					int s = SearchScore.Name(a.Name, term);
					if (s > best)
					{
						best = s;
					}
				}
				if (best > 0)
				{
					scored.Add((a, best + SearchScore.UsageBonus(a.LaunchPath)));
				}
			}
			scored.Sort(((AppEntry a, int s) x, (AppEntry a, int s) y) => (x.s != y.s) ? (y.s - x.s) : string.Compare(x.a.Name, y.a.Name, StringComparison.CurrentCultureIgnoreCase));
			return (from x in scored.Take(max)
				select x.Item1).ToList();
		}
		catch
		{
			return new List<AppEntry>();
		}
	}


	private static List<SettingEntry> MatchSettings(string q, int max)
	{
		string ql = q.ToLowerInvariant();
		List<string> terms = ExpandTerms(ql);
		List<(SettingEntry, int)> scored = new List<(SettingEntry, int)>();
		SettingEntry[] settingsIndex = SettingsIndex;
		foreach (SettingEntry e in settingsIndex)
		{
			int s = 0;
			foreach (string term in terms)
			{
				int t = ScoreSetting(e, term);
				if (t > s)
				{
					s = t;
				}
			}
			if (s > 0)
			{
				scored.Add((e, s));
			}
		}
		scored.Sort(((SettingEntry e, int s) a, (SettingEntry e, int s) b) => (a.s != b.s) ? (b.s - a.s) : string.Compare(a.e.Label, b.e.Label, StringComparison.CurrentCultureIgnoreCase));
		return (from x in scored.Take(max)
			select x.Item1).ToList();
	}

	private static readonly HashSet<string> StopWords = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
	{
		"the", "and", "for", "with", "that", "this", "when", "from", "into", "your", "are", "was",
		"you", "can", "how", "get", "not", "all", "any", "use", "using", "want", "please", "than", "then"
	};

	private static readonly char[] TokSep = new char[8] { ' ', ',', '-', '.', '_', ':', '&', '/' };

	private static int TokenOverlap(string query, string target)
	{
		HashSet<string> set = new HashSet<string>(target.ToLowerInvariant().Split(TokSep, StringSplitOptions.RemoveEmptyEntries), StringComparer.Ordinal);
		int n = 0;
		string[] qt = query.ToLowerInvariant().Split(TokSep, StringSplitOptions.RemoveEmptyEntries);
		foreach (string w in qt)
		{
			if (w.Length >= 3 && !StopWords.Contains(w) && set.Contains(w))
			{
				n++;
			}
		}
		return n;
	}

	private static int ScoreSetting(SettingEntry e, string q)
	{
		int s = SearchScore.Name(e.Label, q);
		// Evaluate the keyword concept layer (spec §14) whenever the label match is weaker than a keyword
		// could give (< WordStart) — so a weak FUZZY label hit can't suppress a strong keyword PREFIX match.
		if (s < SearchScore.WordStart && !string.IsNullOrEmpty(e.Keywords))
		{
			string kw = e.Keywords.ToLowerInvariant();
			int ks = 0;
			string[] parts = kw.Split(' ', ',');
			foreach (string w in parts)
			{
				if (w.Length > 0 && w.StartsWith(q, StringComparison.Ordinal))
				{
					ks = SearchScore.WordStart;
					break;
				}
			}
			if (ks == 0 && kw.Contains(q, StringComparison.Ordinal))
			{
				ks = SearchScore.Substring;
			}
			if (ks > s)
			{
				s = ks;
			}
		}
		// Natural-phrase concept match (spec §12/§36): "make text bigger" -> Text size, "stop apps opening at
		// startup" -> Startup apps. Only for multi-word queries + >=2 shared non-stopword tokens; scored below a
		// real substring so it never outranks deterministic matches.
		if (s < SearchScore.Substring && q.IndexOf(' ') >= 0 && TokenOverlap(q, e.Label + " " + e.Keywords) >= 2)
		{
			s = SearchScore.Substring - 20;
		}
		return s;
	}

	private static string StripVerb(string s, string[] prefixes)
	{
		foreach (string p in prefixes)
		{
			if (s.StartsWith(p, StringComparison.Ordinal))
			{
				return s.Substring(p.Length).Trim();
			}
		}
		return null;
	}

	// Natural-language intent (spec §12): "uninstall <app>", "turn on/off bluetooth|wi-fi|airplane" (EN+GR).
	// Adds an "Action" row at the TOP of results. Returns true if it matched.
	// Command-Palette workspace launch (directive §26/§27): "<group> workspace", "launch <group>", or (for a plain query)
	// a prefix match to a group's name -> an action row that restores that group's saved layout (or launches all its apps).
	private bool TryAddWorkspace(string q)
	{
		if (_workspaceNames == null || _launchWorkspace == null)
		{
			return false;
		}
		string ql = q.ToLowerInvariant().Trim();
		string core = ql;
		bool hadVerb = false;
		foreach (string v in new string[5] { "launch ", "restore ", "open ", "εκκινηση ", "εκκίνηση " })
		{
			if (core.StartsWith(v, StringComparison.Ordinal)) { core = core.Substring(v.Length).Trim(); hadVerb = true; break; }
		}
		bool hadWord = false;
		if (core.EndsWith(" workspace", StringComparison.Ordinal)) { core = core.Substring(0, core.Length - 10).Trim(); hadWord = true; }
		else if (core == "workspace") { core = ""; hadWord = true; }
		bool plain = !hadVerb && !hadWord;
		if (plain && core.Length < 3)
		{
			return false;   // don't surface workspaces for tiny plain queries
		}
		IReadOnlyList<string> names;
		try { names = _workspaceNames(); }
		catch { return false; }
		if (names == null || names.Count == 0)
		{
			return false;
		}
		List<string> hits = new List<string>();
		foreach (string n in names)
		{
			if (string.IsNullOrWhiteSpace(n))
			{
				continue;
			}
			bool match;
			if (core.Length == 0)
			{
				match = hadWord;   // bare "workspace" -> list them all
			}
			else
			{
				int sc = SearchScore.Name(n, core);
				match = plain ? (sc >= SearchScore.Prefix) : (sc >= SearchScore.WordStart || n.ToLowerInvariant().Contains(core));
			}
			if (match)
			{
				hits.Add(n);
			}
			if (hits.Count >= 5)
			{
				break;
			}
		}
		if (hits.Count == 0)
		{
			return false;
		}
		_results.Children.Add(SectionHeader("Workspace"));
		foreach (string n in hits)
		{
			string name = n;
			_results.Children.Add(ActionRow(G(57609), "Launch “" + name + "” workspace", delegate
			{
				HidePane();
				try { _launchWorkspace(name); } catch (Exception ex) { Logger.Log("search workspace: " + ex.Message); }
			}));
		}
		return true;
	}

	private bool TryAddNaturalCommand(string q)
	{
		string ql = q.ToLowerInvariant().Trim();
		// Volume: mute / unmute / "volume NN" -> AudioController (reuses the same COM the tray/Action Center use).
		if (ql == "mute" || ql == "μουτ" || ql == "σιγαση" || ql == "σίγαση")
		{
			_results.Children.Add(SectionHeader("Action"));
			_results.Children.Add(ActionRow(G(59215), "Mute volume", delegate { HidePane(); try { new AudioController().SetMute(mute: true); } catch (Exception ex) { Logger.Log("search mute: " + ex.Message); } }));
			return true;
		}
		if (ql == "unmute" || ql == "αρση σιγασης" || ql == "άρση σίγασης")
		{
			_results.Children.Add(SectionHeader("Action"));
			_results.Children.Add(ActionRow(G(59797), "Unmute volume", delegate { HidePane(); try { new AudioController().SetMute(mute: false); } catch (Exception ex) { Logger.Log("search unmute: " + ex.Message); } }));
			return true;
		}
		int volPct = ParseTrailingPercent(ql, new string[4] { "volume ", "ενταση ", "ένταση ", "vol " });
		if (volPct >= 0)
		{
			int p = Math.Clamp(volPct, 0, 100);
			_results.Children.Add(SectionHeader("Action"));
			_results.Children.Add(ActionRow(G(59797), $"Set volume to {p}%", delegate { HidePane(); try { new AudioController().SetVolume((float)p / 100f); } catch (Exception ex) { Logger.Log("search volume: " + ex.Message); } }));
			return true;
		}
		// Brightness: "brightness NN" -> MonitorBrightness (DDC/CI or WMI).
		int brPct = ParseTrailingPercent(ql, new string[3] { "brightness ", "φωτεινοτητα ", "φωτεινότητα " });
		if (brPct >= 0)
		{
			int p = Math.Clamp(brPct, 0, 100);
			_results.Children.Add(SectionHeader("Action"));
			_results.Children.Add(ActionRow(G(59142), $"Set brightness to {p}%", delegate { HidePane(); try { MonitorBrightness.Set(p); } catch (Exception ex) { Logger.Log("search brightness: " + ex.Message); } }));
			return true;
		}
		// Display projection (Win+P equivalents) via the documented DisplaySwitch.exe — no native hacks, always reliable.
		string dispMode = null;
		string dispLabel = null;
		bool dispWords = ql.Contains("display") || ql.Contains("displays") || ql.Contains("screen") || ql.Contains("monitor") || ql.Contains("projector") || ql.Contains("οθον");
		if (dispWords && ql.Contains("extend")) { dispMode = "/extend"; dispLabel = "Extend these displays"; }
		else if (dispWords && ql.Contains("duplicate")) { dispMode = "/clone"; dispLabel = "Duplicate these displays"; }
		else if (ql.Contains("second screen only") || ql.Contains("projector only") || ql.Contains("external only")) { dispMode = "/external"; dispLabel = "Second screen only"; }
		else if (ql.Contains("pc screen only") || ql.Contains("first screen only") || ql.Contains("internal only")) { dispMode = "/internal"; dispLabel = "PC screen only"; }
		if (dispMode != null)
		{
			string m = dispMode;
			_results.Children.Add(SectionHeader("Action"));
			_results.Children.Add(ActionRow(G(59380), dispLabel, delegate { HidePane(); try { Process.Start(new ProcessStartInfo("DisplaySwitch.exe", m) { UseShellExecute = true }); } catch (Exception ex) { Logger.Log("search display: " + ex.Message); } }));
			return true;
		}
		if (ql == "project" || ql == "projection" || ql == "connect display" || ql == "wireless display" || ql == "second screen")
		{
			_results.Children.Add(SectionHeader("Action"));
			_results.Children.Add(ActionRow(G(59380), "Project (choose display mode)", delegate { HidePane(); try { Process.Start(new ProcessStartInfo("DisplaySwitch.exe") { UseShellExecute = true }); } catch (Exception ex) { Logger.Log("search project: " + ex.Message); } }));
			return true;
		}
		// IP address / URL / UNC path typed into search -> open in browser / File Explorer.
		string qtrim = q.Trim();
		if (LooksLikeUnc(qtrim))
		{
			_results.Children.Add(SectionHeader("Action"));
			_results.Children.Add(ActionRow(G(57736), "Open " + qtrim + " in File Explorer", delegate { HidePane(); try { Process.Start(new ProcessStartInfo("explorer.exe", "\"" + qtrim + "\"") { UseShellExecute = true }); } catch (Exception ex) { Logger.Log("search unc: " + ex.Message); } }));
			return true;
		}
		if (LooksLikeIpOrUrl(qtrim))
		{
			string url = (qtrim.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || qtrim.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) ? qtrim : ("http://" + qtrim);
			_results.Children.Add(SectionHeader("Action"));
			_results.Children.Add(ActionRow(G(59252), "Open " + qtrim + " in browser", delegate { HidePane(); try { WebOpen.Url(url); } catch (Exception ex) { Logger.Log("search url: " + ex.Message); } }));
			return true;
		}
		string tgt = StripVerb(ql, new string[5] { "uninstall ", "remove ", "απεγκατάσταση ", "απεγκαταστησε ", "απεγκατασταση " });
		if (tgt != null && tgt.Length >= 2)
		{
			List<AppEntry> m = MatchApps(tgt, 1);
			string label = ((m.Count > 0) ? ("Uninstall " + m[0].Name) : "Uninstall a program");
			_results.Children.Add(SectionHeader("Action"));
			_results.Children.Add(ActionRow(G(59213), label, delegate
			{
				HidePane();
				Launch("appwiz.cpl");
			}));
			return true;
		}
		bool? on = null;
		string rest = StripVerb(ql, new string[4] { "turn on ", "enable ", "ενεργοποίηση ", "ενεργοποιηση " });
		if (rest != null)
		{
			on = true;
		}
		else
		{
			rest = StripVerb(ql, new string[4] { "turn off ", "disable ", "απενεργοποίηση ", "απενεργοποιηση " });
			if (rest != null)
			{
				on = false;
			}
		}
		if (on.HasValue && rest != null)
		{
			bool v = on.Value;
			int glyph = 0;
			string disp = null;
			Action act = null;
			if (rest.Contains("bluetooth") || rest.Contains("μπλουτουθ"))
			{
				disp = "Bluetooth";
				glyph = 59138;
				act = delegate { _ = RadioQuick.SetKind(Windows.Devices.Radios.RadioKind.Bluetooth, v); };
			}
			else if (rest.Contains("wifi") || rest.Contains("wi-fi") || rest.Contains("wireless"))
			{
				disp = "Wi-Fi";
				glyph = 59137;
				act = delegate { _ = RadioQuick.SetKind(Windows.Devices.Radios.RadioKind.WiFi, v); };
			}
			else if (rest.Contains("airplane") || rest.Contains("flight"))
			{
				disp = "Airplane mode";
				glyph = 59145;
				act = delegate { _ = RadioQuick.SetAirplane(v); };
			}
			if (act != null)
			{
				string verb = (v ? "Turn on " : "Turn off ");
				_results.Children.Add(SectionHeader("Action"));
				_results.Children.Add(ActionRow(G(glyph), verb + disp, delegate
				{
					HidePane();
					act();
				}));
				return true;
			}
		}
		return false;
	}

	// Parses a trailing 0-100 number after any of the given prefixes ("volume 30", "brightness 50%"); -1 if none.
	private static int ParseTrailingPercent(string ql, string[] prefixes)
	{
		foreach (string p in prefixes)
		{
			if (ql.StartsWith(p, StringComparison.Ordinal))
			{
				string num = ql.Substring(p.Length).Trim().TrimEnd('%').Trim();
				if (int.TryParse(num, out int v))
				{
					return v;
				}
			}
		}
		return -1;
	}

	private static bool LooksLikeUnc(string s)
	{
		return s.StartsWith("\\\\", StringComparison.Ordinal) && s.Length > 3 && !s.Contains(' ');
	}

	// IPv4 dotted-quad, or an http(s):///www. URL. Deliberately conservative so plain words never trip it.
	private static bool LooksLikeIpOrUrl(string s)
	{
		if (string.IsNullOrEmpty(s) || s.Contains(' '))
		{
			return false;
		}
		if (s.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || s.StartsWith("https://", StringComparison.OrdinalIgnoreCase) || s.StartsWith("www.", StringComparison.OrdinalIgnoreCase))
		{
			return true;
		}
		string[] parts = s.Split('.');
		if (parts.Length == 4)
		{
			foreach (string p in parts)
			{
				if (!int.TryParse(p, out int o) || o < 0 || o > 255)
				{
					return false;
				}
			}
			return true;
		}
		return false;
	}

	private sealed record CommandEntry(string Name, string Keywords, int Glyph, Action Run, bool Confirm);

	// System/power commands surfaced in search (spec §13/§26 "Commands"). Greek keywords included so a
	// Greek query resolves directly; ExpandTerms also adds wrong-layout recovery. Destructive ones confirm.
	private static readonly CommandEntry[] Commands = new CommandEntry[7]
	{
		new CommandEntry("Lock", "lock κλειδωμα κλείδωμα", 59182, PowerActions.Lock, false),
		new CommandEntry("Sleep", "sleep αναστολη αναστολή υπνος ύπνος", 60486, PowerActions.Sleep, false),
		new CommandEntry("Sign out", "sign out signout logoff logout αποσυνδεση αποσύνδεση", 59259, PowerActions.SignOut, true),
		new CommandEntry("Restart", "restart reboot επανεκκινηση επανεκκίνηση", 59255, PowerActions.Restart, true),
		new CommandEntry("Shut down", "shut down shutdown power off τερματισμος τερματισμός σβησιμο σβήσιμο", 59368, PowerActions.ShutDown, true),
		new CommandEntry("Show/hide hidden files", "hidden files show hide toggle κρυφα αρχεια κρυφά αρχεία εμφανιση απόκρυψη", 59536, ShellCommands.ToggleHiddenFiles, false),
		new CommandEntry("Show/hide file extensions", "file extensions show hide toggle καταληξεις επεκτασεις προεκτασεις αρχειων επεκτάσεις προεκτάσεις", 59557, ShellCommands.ToggleFileExtensions, false)
	};

	private static List<CommandEntry> MatchCommands(string q, int max)
	{
		string ql = q.ToLowerInvariant();
		if (ql.Length < 3)
		{
			return new List<CommandEntry>();   // commands are intent-heavy; never fire on 1-2 char queries
		}
		List<string> terms = ExpandTerms(ql);
		List<(CommandEntry, int)> scored = new List<(CommandEntry, int)>();
		foreach (CommandEntry c in Commands)
		{
			int best = 0;
			foreach (string term in terms)
			{
				int s = SearchScore.Name(c.Name, term);
				if (s < SearchScore.WordStart)
				{
					string[] kws = c.Keywords.Split(' ');
					foreach (string w in kws)
					{
						if (w.Length > 0 && w.StartsWith(term, StringComparison.Ordinal))
						{
							s = SearchScore.WordStart;
							break;
						}
					}
				}
				if (s > best)
				{
					best = s;
				}
			}
			if (best >= SearchScore.WordStart)   // deterministic prefix/word/keyword only — no substring/fuzzy commands
			{
				scored.Add((c, best));
			}
		}
		scored.Sort(((CommandEntry c, int s) a, (CommandEntry c, int s) b) => b.s - a.s);
		return (from x in scored.Take(max)
			select x.Item1).ToList();
	}

	private System.Windows.Controls.Button CommandRow(CommandEntry c)
	{
		return ActionRow(G(c.Glyph), c.Name, delegate
		{
			HidePane();
			if (c.Confirm)
			{
				System.Windows.MessageBoxResult r = System.Windows.MessageBox.Show("Are you sure you want to " + c.Name.ToLowerInvariant() + "?", c.Name, System.Windows.MessageBoxButton.OKCancel, System.Windows.MessageBoxImage.Warning);
				if (r != System.Windows.MessageBoxResult.OK)
				{
					return;
				}
			}
			c.Run();
		});
	}

	private static TextBlock SectionHeader(string text)
	{
		return new TextBlock
		{
			Text = text,
			Foreground = new SolidColorBrush(System.Windows.Media.Color.FromArgb(176, byte.MaxValue, byte.MaxValue, byte.MaxValue)),
			FontFamily = new System.Windows.Media.FontFamily("Segoe UI Semilight"),
			FontSize = 16.0,
			Margin = new Thickness(0.0, 12.0, 0.0, 6.0)
		};
	}

	private static TextBlock Hint(string text)
	{
		return new TextBlock
		{
			Text = text,
			Foreground = new SolidColorBrush(System.Windows.Media.Color.FromArgb(153, byte.MaxValue, byte.MaxValue, byte.MaxValue)),
			FontFamily = new System.Windows.Media.FontFamily("Segoe UI"),
			FontSize = 13.0,
			TextWrapping = TextWrapping.Wrap,
			Margin = new Thickness(0.0, 8.0, 0.0, 0.0)
		};
	}

	private System.Windows.Controls.Button AppRow(AppEntry a)
	{
		StackPanel sp = new StackPanel
		{
			Orientation = System.Windows.Controls.Orientation.Horizontal
		};
		System.Windows.Controls.Image img = new System.Windows.Controls.Image
		{
			Width = 24.0,
			Height = 24.0,
			VerticalAlignment = VerticalAlignment.Center
		};
		RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);
		img.SetBinding(System.Windows.Controls.Image.SourceProperty, new System.Windows.Data.Binding("Icon")
		{
			Source = a,
			// Show the app's letter tile until (or unless) its real icon resolves, never an empty slot.
			TargetNullValue = IconResolver.LetterTile(a.Name, IconResolver.AccentFor(a.Name))
		});
		sp.Children.Add(img);
		sp.Children.Add(new TextBlock
		{
			Text = a.Name,
			Foreground = System.Windows.Media.Brushes.White,
			FontFamily = new System.Windows.Media.FontFamily("Segoe UI"),
			FontSize = 14.0,
			VerticalAlignment = VerticalAlignment.Center,
			Margin = new Thickness(12.0, 0.0, 0.0, 0.0),
			TextTrimming = TextTrimming.CharacterEllipsis
		});
		System.Windows.Controls.Button btn = RowButton(sp, delegate
		{
			LaunchApp(a);
		});
		btn.PreviewMouseRightButtonUp += delegate(object _, MouseButtonEventArgs e)
		{
			ShowAppMenu(a, btn);
			e.Handled = true;
		};
		return btn;
	}

	// Direct-action menu on an app search result (spec §13) — reuses the Win81 context-menu factory + FileShell.
	private void ShowAppMenu(AppEntry a, UIElement target)
	{
		try
		{
			string path = a.LaunchPath ?? "";
			bool isExe = path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase);
			bool isFile = isExe || (System.IO.Path.IsPathRooted(path) && !path.StartsWith("shell:", StringComparison.OrdinalIgnoreCase));
			System.Windows.Controls.ContextMenu menu = new System.Windows.Controls.ContextMenu
			{
				Style = (Style)System.Windows.Application.Current.Resources["Win81ContextMenu"]
			};
			TaskbarContextMenu.ApplyTheme(menu);
			menu.Items.Add(TaskbarContextMenu.Leaf("Open", 59621, delegate
			{
				HidePane();
				LaunchApp(a);
			}));
			if (isFile)
			{
				menu.Items.Add(TaskbarContextMenu.Leaf("Run as administrator", 57767, delegate
				{
					HidePane();
					FileShell.RunAsAdmin(path);
				}));
				menu.Items.Add(TaskbarContextMenu.Leaf("Open file location", 57736, delegate
				{
					HidePane();
					FileShell.OpenLocation(path);
				}));
			}
			if (FileContextMenu.PinToStart != null)
			{
				menu.Items.Add(TaskbarContextMenu.Leaf("Pin to Start", 57665, delegate
				{
					HidePane();
					FileContextMenu.PinToStart(path);
				}));
			}
			if (isExe && FileContextMenu.PinToTaskbar != null)
			{
				menu.Items.Add(TaskbarContextMenu.Leaf("Pin to taskbar", 57665, delegate
				{
					HidePane();
					FileContextMenu.PinToTaskbar(path);
				}));
			}
			menu.Items.Add(TaskbarContextMenu.Sep());
			menu.Items.Add(TaskbarContextMenu.Leaf("Uninstall", 59213, delegate
			{
				HidePane();
				// Real registry-based uninstall for desktop apps: match the vendor uninstaller by EXE PATH (never fuzzy
				// name), confirm, then launch it (its own UI confirms too). Store/UWP apps or no confident match → the
				// safe Apps & features fallback.
				string exe = isExe ? path : "";
				if (exe.Length > 0 && AppUninstall.TryFind(exe, out string dn, out string ucmd))
				{
					if (System.Windows.MessageBox.Show("Uninstall " + dn + "?\n\nThis launches its uninstaller.", "Uninstall", System.Windows.MessageBoxButton.OKCancel, System.Windows.MessageBoxImage.Warning) == System.Windows.MessageBoxResult.OK)
					{
						AppUninstall.Run(ucmd);
					}
				}
				else
				{
					Launch("ms-settings:appsfeatures");
				}
			}));
			if (isFile)
			{
				menu.Items.Add(TaskbarContextMenu.Leaf("Properties", 59718, delegate
				{
					HidePane();
					FileShell.Properties(path);
				}));
			}
			menu.Closed += delegate
			{
				if (_appMenu == menu)
				{
					_appMenu = null;
				}
				// re-activate so a later click outside the pane deactivates+closes it normally (mirrors StartScreen);
				// but not if an action already started hiding the pane
				if (base.IsVisible && !_hiding)
				{
					Activate();
				}
			};
			menu.PlacementTarget = target;
			menu.Placement = PlacementMode.Bottom;
			// close any previous menu first (re-entrant-safe), open this one, then track it ONLY on success
			System.Windows.Controls.ContextMenu? prev = _appMenu;
			if (prev != null)
			{
				prev.IsOpen = false;
			}
			menu.IsOpen = true;
			_appMenu = menu;
		}
		catch (Exception ex)
		{
			Logger.Log("Search app menu: " + ex.Message);
		}
	}

	private System.Windows.Controls.Button UriRow(string glyph, string label, string uri)
	{
		return RowButton(GlyphRow(glyph, label), delegate
		{
			HidePane();
			Launch(uri);
		});
	}

	private System.Windows.Controls.Button ActionRow(string glyph, string label, Action act)
	{
		return RowButton(GlyphRow(glyph, label), delegate
		{
			HidePane();
			act();
		});
	}

	private static StackPanel GlyphRow(string glyph, string label)
	{
		StackPanel sp = new StackPanel
		{
			Orientation = System.Windows.Controls.Orientation.Horizontal
		};
		if (glyph.Length == 1 && ((int)glyph[0] == 57621 || (int)glyph[0] == 59155))   // Settings gear (E115/E713) -> the ONE canonical launcher gear vector (white)
		{
			sp.Children.Add(new System.Windows.Controls.Border
			{
				Width = 26.0,
				VerticalAlignment = VerticalAlignment.Center,
				Child = new System.Windows.Controls.Image
				{
					Source = SettingsGlyph.Gear(System.Windows.Media.Colors.White),
					Width = 20.0,
					Height = 20.0,
					HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
					VerticalAlignment = VerticalAlignment.Center
				}
			});
		}
		else
		{
			sp.Children.Add(new TextBlock
			{
				Text = glyph,
				FontFamily = new System.Windows.Media.FontFamily((glyph.Length > 0 && glyph[0] >= '\ue100' && glyph[0] <= '\ue1ff') ? "Segoe UI Symbol" : "Segoe MDL2 Assets"),
				FontSize = 20.0,
				Foreground = System.Windows.Media.Brushes.White,
				Width = 26.0,
				VerticalAlignment = VerticalAlignment.Center
			});
		}
		sp.Children.Add(new TextBlock
		{
			Text = label,
			Foreground = System.Windows.Media.Brushes.White,
			FontFamily = new System.Windows.Media.FontFamily("Segoe UI"),
			FontSize = 14.0,
			VerticalAlignment = VerticalAlignment.Center,
			Margin = new Thickness(6.0, 0.0, 0.0, 0.0),
			TextTrimming = TextTrimming.CharacterEllipsis
		});
		return sp;
	}

	private static System.Windows.Controls.Button RowButton(UIElement content, Action onClick)
	{
		System.Windows.Controls.Button b = new System.Windows.Controls.Button
		{
			Content = content,
			Background = System.Windows.Media.Brushes.Transparent,
			BorderThickness = new Thickness(0.0),
			Cursor = System.Windows.Input.Cursors.Hand,
			FocusVisualStyle = null,
			Padding = new Thickness(6.0, 8.0, 6.0, 8.0),
			HorizontalContentAlignment = System.Windows.HorizontalAlignment.Left,
			Template = HoverRowTemplate()
		};
		b.Click += delegate
		{
			onClick();
		};
		return b;
	}

	private void ToggleScopePopup(UIElement anchor)
	{
		if (_scopePopup == null)
		{
			_scopePopup = new Popup
			{
				Placement = PlacementMode.Bottom,
				StaysOpen = false,
				AllowsTransparency = true,
				PopupAnimation = PopupAnimation.Fade
			};
		}
		if (_scopePopup.IsOpen)
		{
			_scopePopup.IsOpen = false;
			return;
		}
		StackPanel panel = new StackPanel();
		Scope[] scopes = new Scope[5]
		{
			Scope.Everywhere,
			Scope.Settings,
			Scope.Files,
			Scope.Web,
			Scope.Places
		};
		for (int i = 0; i < scopes.Length; i++)
		{
			Scope s = scopes[i];
			System.Windows.Controls.Button item = RowButton(new TextBlock
			{
				Text = s.ToString(),
				Foreground = System.Windows.Media.Brushes.White,
				FontFamily = new System.Windows.Media.FontFamily("Segoe UI"),
				FontSize = 15.0
			}, delegate
			{
				_scope = s;
				_scopeLabel.Text = s.ToString();
				_scopePopup.IsOpen = false;
				ClearEntityCard();
				Rebuild();
			});
			item.Padding = new Thickness(16.0, 10.0, 16.0, 10.0);
			panel.Children.Add(item);
			if (i < scopes.Length - 1)
			{
				panel.Children.Add(new Border
				{
					Height = 1.0,
					Background = new SolidColorBrush(System.Windows.Media.Color.FromArgb(18, byte.MaxValue, byte.MaxValue, byte.MaxValue)),
					Margin = new Thickness(12.0, 0.0, 12.0, 0.0)
				});
			}
		}
		Border wrap = new Border
		{
			Background = ShellSkin.CardBg(),
			BorderBrush = new SolidColorBrush(System.Windows.Media.Color.FromArgb(38, byte.MaxValue, byte.MaxValue, byte.MaxValue)),
			BorderThickness = new Thickness(1.0),
			Child = panel,
			MinWidth = 190.0,
			Effect = new DropShadowEffect
			{
				BlurRadius = 10.0,
				ShadowDepth = 2.0,
				Direction = 290.0,
				Opacity = 0.4,
				Color = Colors.Black
			}
		};
		_scopePopup.Child = wrap;
		_scopePopup.PlacementTarget = anchor;
		_scopePopup.IsOpen = true;
	}

	private void LaunchApp(AppEntry a)
	{
		HidePane();
		try
		{
			_launch(a);
		}
		catch (Exception ex)
		{
			Logger.Log("Search launch: " + ex.Message);
		}
	}

	private static void WebSearch(string q)
	{
		Launch(PlaceSearchService.BuildWebSearchUrl(q, SettingsStore.FastSnapshot.PlaceSearchEngine));
	}

	private void ExplorePlace(string query)
	{
		_entityCts?.Cancel();
		HidePane();
		MetroPlaceSearchWindow.ShowFor(query);
	}

	private static void FileSearch(string q)
	{
		Launch("search-ms:query=" + Uri.EscapeDataString(q));
	}

	private static void Launch(string cmd)
	{
		try
		{
			if (WebOpen.IsWeb(cmd))
			{
				WebOpen.Url(cmd);   // web search / web result -> preferred browser (MetroBrowser)
				return;
			}
			Process.Start(new ProcessStartInfo(cmd)
			{
				UseShellExecute = true
			});
		}
		catch (Exception ex)
		{
			Logger.Log("Search open '" + cmd + "': " + ex.Message);
		}
	}

	private static ControlTemplate HoverRowTemplate()
	{
		ControlTemplate t = new ControlTemplate(typeof(System.Windows.Controls.Button));
		FrameworkElementFactory border = new FrameworkElementFactory(typeof(Border))
		{
			Name = "bd"
		};
		// bind to the Button's Background so keyboard selection (Background=SelBrush) shows; hover trigger still wins on mouse-over
		border.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(System.Windows.Controls.Control.BackgroundProperty));
		FrameworkElementFactory cp = new FrameworkElementFactory(typeof(ContentPresenter));
		cp.SetValue(FrameworkElement.MarginProperty, new Thickness(6.0, 8.0, 6.0, 8.0));
		cp.SetValue(FrameworkElement.HorizontalAlignmentProperty, System.Windows.HorizontalAlignment.Left);
		cp.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
		border.AppendChild(cp);
		t.VisualTree = border;
		Trigger hover = new Trigger
		{
			Property = UIElement.IsMouseOverProperty,
			Value = true
		};
		hover.Setters.Add(new Setter(Border.BackgroundProperty, new SolidColorBrush(System.Windows.Media.Color.FromArgb(20, byte.MaxValue, byte.MaxValue, byte.MaxValue)), "bd"));
		t.Triggers.Add(hover);
		return t;
	}

	private static ControlTemplate TransparentButtonTemplate()
	{
		ControlTemplate t = new ControlTemplate(typeof(System.Windows.Controls.Button));
		FrameworkElementFactory cp = new FrameworkElementFactory(typeof(ContentPresenter));
		cp.SetValue(FrameworkElement.HorizontalAlignmentProperty, System.Windows.HorizontalAlignment.Left);
		cp.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
		t.VisualTree = cp;
		return t;
	}

	private static ControlTemplate AccentButtonTemplate()
	{
		ControlTemplate t = new ControlTemplate(typeof(System.Windows.Controls.Button));
		FrameworkElementFactory bd = new FrameworkElementFactory(typeof(Grid));
		FrameworkElementFactory back = new FrameworkElementFactory(typeof(Border));
		back.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(System.Windows.Controls.Control.BackgroundProperty));
		FrameworkElementFactory wash = new FrameworkElementFactory(typeof(Border))
		{
			Name = "wash"
		};
		wash.SetValue(Border.BackgroundProperty, System.Windows.Media.Brushes.Transparent);
		FrameworkElementFactory cp = new FrameworkElementFactory(typeof(ContentPresenter));
		cp.SetValue(FrameworkElement.HorizontalAlignmentProperty, System.Windows.HorizontalAlignment.Center);
		cp.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
		bd.AppendChild(back);
		bd.AppendChild(wash);
		bd.AppendChild(cp);
		t.VisualTree = bd;
		Trigger hover = new Trigger
		{
			Property = UIElement.IsMouseOverProperty,
			Value = true
		};
		hover.Setters.Add(new Setter(Border.BackgroundProperty, new SolidColorBrush(System.Windows.Media.Color.FromArgb(36, byte.MaxValue, byte.MaxValue, byte.MaxValue)), "wash"));
		t.Triggers.Add(hover);
		return t;
	}

	// Lightweight regression benchmark (spec §40/§43/§44): runs the query suite, records Top-1 app + setting
	// and the local match latency per query. Re-run to spot ranking regressions / latency spikes.
	public string RunBenchmark()
	{
		string[] queries = new string[24]
		{
			"discord", "notepad", "settings", "task man", "dev man", "visual stu", "discrod", "bluetoth",
			"devcie manager", "tm", "cmd", "regedit", "services", "refresh rate", "mic privacy", "night light",
			"control", "chrome", "restart", "calc", "vsc", "disk", "uninstall discord", "task"
		};
		System.Text.StringBuilder sb = new System.Text.StringBuilder();
		sb.AppendLine("query\ttopApp\ttopSetting\tmatch_ms");
		double worst = 0.0;
		foreach (string q in queries)
		{
			System.Diagnostics.Stopwatch sw = System.Diagnostics.Stopwatch.StartNew();
			List<AppEntry> apps = MatchApps(q, 1);
			List<SettingEntry> setts = MatchSettings(q, 1);
			sw.Stop();
			double ms = sw.Elapsed.TotalMilliseconds;
			if (ms > worst)
			{
				worst = ms;
			}
			string ta = ((apps.Count > 0) ? apps[0].Name : "-");
			string ts = ((setts.Count > 0) ? setts[0].Label : "-");
			sb.AppendLine(q + "\t" + ta + "\t" + ts + "\t" + ms.ToString("F2"));
		}
		sb.AppendLine("worst_local_match_ms\t" + worst.ToString("F2") + "\t(spec §41 target: local set ~<100ms)");
		return sb.ToString();
	}

	public void QaRender(string outPath, string query, double height = 920.0)
	{
		//IL_01fb: Unknown result type (might be due to invalid IL or missing references)
		//IL_0228: Unknown result type (might be due to invalid IL or missing references)
		_root.Background = PaneBg();
		_box.Text = query;
		Rebuild();
		// Mirror the live TextChanged priority: the synchronous offline cards (calc/convert/date/base/color)
		// win first; the Place sample is the fallback (matches TryShowXCard() running before QueueEntityLookup).
		if (_router.Classify(Query.Of(query)).Kind == IntentKind.Calc && CalcEngine.TryBuild(query) is EntityCard calc)
		{
			_cardHost.Child = MetroComposer.BuildCalcCard(calc);
			_cardHost.Visibility = Visibility.Visible;
		}
		else if (UnitConvert.TryBuild(query) is EntityCard conv)
		{
			_cardHost.Child = MetroComposer.BuildCalcCard(conv);
			_cardHost.Visibility = Visibility.Visible;
		}
		else if (DateEngine.TryBuild(query) is EntityCard dt)
		{
			_cardHost.Child = MetroComposer.BuildCalcCard(dt);
			_cardHost.Visibility = Visibility.Visible;
		}
		else if (BaseEngine.TryBuild(query) is EntityCard nb)
		{
			_cardHost.Child = MetroComposer.BuildCalcCard(nb);
			_cardHost.Visibility = Visibility.Visible;
		}
		else if (ColorEngine.TryBuild(query) is EntityCard cc)
		{
			_cardHost.Child = MetroComposer.BuildColorCard(cc);
			_cardHost.Visibility = Visibility.Visible;
		}
		else if (_router.Classify(Query.Of(query)).Kind == IntentKind.Place)
		{
			EntityCard sample = new EntityCard(IntentKind.Place, query, "Place", null, new Fact[4]
			{
				new Fact("", "A sample place summary — the real card fills in a live Wikipedia extract, weather and a hero image from free no-key services."),
				new Fact("Weather", "18°C · Partly cloudy", ""),
				new Fact("Country", "France", ""),
				new Fact("Type", "City", "")
			}, new LauncherAction[4]
			{
				new LauncherAction("maps", "Open in Maps", "", () => Task.CompletedTask),
				new LauncherAction("directions", "Directions", "", () => Task.CompletedTask),
				new LauncherAction("web", "Search the web", "", () => Task.CompletedTask),
				new LauncherAction("wiki", "Wikipedia", "", () => Task.CompletedTask)
			});
			_cardHost.Child = MetroComposer.BuildPlaceCard(sample, (string? _) => (ImageSource?)null);
			_cardHost.Visibility = Visibility.Visible;
		}
		_slide.X = 0.0;
		_root.Measure(new System.Windows.Size(320.0, height));
		_root.Arrange(new Rect(0.0, 0.0, 320.0, height));
		_root.UpdateLayout();
		RenderTargetBitmap rtb = new RenderTargetBitmap(320, (int)height, 96.0, 96.0, PixelFormats.Pbgra32);
		rtb.Render(_root);
		PngBitmapEncoder enc = new PngBitmapEncoder();
		enc.Frames.Add(BitmapFrame.Create(rtb));
		using FileStream fs = File.Create(outPath);
		enc.Save(fs);
		Logger.Log("QaRender search pane -> " + outPath);
	}
}
