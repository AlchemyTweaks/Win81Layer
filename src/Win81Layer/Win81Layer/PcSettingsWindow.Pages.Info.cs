using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;

#nullable enable annotations

namespace Win81Layer;

// Notifications, Accessibility, Time & language, Apps, Privacy & security, About this PC and Control Panel & tools.
// Notifications, privacy, security and Windows Update are never written: their rows show the state and open Windows.
public sealed partial class PcSettingsWindow
{
	// ===== Notifications =====

	private ScrollViewer NotificationsPage()
	{
		Pg pg = new Pg(this, "notifications");
		ISettingsWorld w = SettingsWorld.Current;
		pg.Header("Windows notifications");
		FrameworkElement state = InfoRow("notifications.state", null);
		pg.Add(state);
		ReadAsync(w.ReadToastsEnabled, delegate(bool on) { SetInfo(state, on ? "On" : "Off"); });
		pg.Add(ButtonRow("notifications.center", "Open", delegate
		{
			bool sent = false;
			try
			{
				sent = w.SendChord(91, 78);
			}
			catch (Exception ex)
			{
				Logger.Log("PC settings: Win+N: " + ex.Message);
			}
			if (!sent)
			{
				SettingsLink.Open(LinkSpec.Page("ms-settings:notifications"), _caps);
			}
		}, "Win+N"));
		pg.Add(LinkRow("notifications.settings", "Apps, senders and Do not disturb"));
		int? seconds = w.SpiGet(0x2016);
		pg.Add(DrillRow("notifications.duration", seconds == null ? null : seconds.Value + " seconds", delegate { Navigate("accessibility", "access.duration", push: true); }));
		pg.Footnote("Notifications are Windows' own: the launcher shows the state and opens Windows, it never changes these settings.");
		return pg.Done();
	}

	// ===== Accessibility =====

	private ScrollViewer AccessibilityPage()
	{
		Pg pg = new Pg(this, "accessibility");
		ISettingsWorld w = SettingsWorld.Current;
		pg.Header("Vision");
		pg.Add(LinkRow("access.textsize"));
		pg.Add(LinkRow("access.colorfilters"));
		pg.Add(LinkRow("access.highcontrast"));
		pg.Add(ButtonRow("access.magnifier", "Start", delegate { w.Launch("magnify.exe", null, uri: false, elevate: false); }, "Stop it with Win+Esc"));
		pg.Add(KnobRow("access.animations"));
		pg.Add(ReduceMotionRow());
		pg.Header("Hearing");
		pg.Add(LinkRow("access.mono"));
		pg.Add(KnobRow("access.duration"));
		pg.Header("Interaction");
		pg.Add(KnobRow("access.sticky"));
		pg.Add(KnobRow("access.filter"));
		pg.Add(KnobRow("access.toggle"));
		int? caret = w.SpiGet(0x2006);
		pg.Add(DrillRow("access.caret", caret == null ? null : caret.Value + " px", delegate { Navigate("keyboard", "keyboard.caretwidth", push: true); }));
		pg.Add(ButtonRow("access.narrator", "Start", delegate { w.Launch("narrator.exe", null, uri: false, elevate: false); }, "Stop it with Win+Ctrl+Enter"));
		pg.Header("More");
		pg.Add(LinkRow("access.center"));
		ResetCard(pg, "accessibility", defaults: true);
		PolicyFootnote(pg);
		return pg.Done();
	}

	// One row for the launcher's motion and Windows' animation effects: On sets the launcher to Reduced and turns the
	// animation effects off, as one journal group (one Undo).
	private FrameworkElement ReduceMotionRow()
	{
		ISettingsWorld w = SettingsWorld.Current;
		HubKnob motion = HubKnobs.Get("appearance.motion")!;
		HubKnob anim = HubKnobs.Get("access.animations")!;
		string? motionNow = motion.Read(w);
		string? animNow = anim.Read(w);
		bool on = motionNow == "Reduced" && animNow == "0";
		ToggleButton t = CompactToggle(on, delegate(bool v)
		{
			string? m = motion.Read(w);
			KnobBinding? ab = _bindings.TryGetValue("access.animations", out KnobBinding? x) ? x : null;
			string? a = ab?.Committed ?? anim.Read(w);
			if (m == null || a == null)
			{
				return;
			}
			List<Change> changes = new List<Change>();
			if (v)
			{
				_sessionBefore.TryAdd("appearance.motion", (motion, m, "accessibility"));
				if (m != "Reduced")
				{
					changes.Add(new Change { K = motion, Before = m, After = "Reduced" });
				}
				if (a != "0")
				{
					changes.Add(new Change { K = anim, B = ab, Before = a, After = "0" });
				}
			}
			else
			{
				string motionBack = _sessionBefore.TryGetValue("appearance.motion", out (HubKnob Knob, string Before, string Page) sb) && sb.Before != "Reduced" ? sb.Before : "Authentic";
				if (m != motionBack)
				{
					changes.Add(new Change { K = motion, Before = m, After = motionBack });
				}
				if (a != "1")
				{
					changes.Add(new Change { K = anim, B = ab, Before = a, After = "1" });
				}
			}
			ApplyChanges(changes, "reducemotion:" + Environment.TickCount64, "Reduce motion everywhere", journal: true);
		});
		t.IsEnabled = motionNow != null && animNow != null;
		Grid row = RowShell(HubCatalog.Title("access.reducemotion"), "Launcher motion Reduced and Windows animations off", null, t, out _);
		Register("access.reducemotion", row);
		return row;
	}

	// A write of the settings world: errors (including a refused write under QA) are logged, never thrown into the UI.
	private static void RunWrite(Action write)
	{
		try
		{
			write();
		}
		catch (Exception ex)
		{
			Logger.Log("PC settings: " + ex.Message);
		}
	}

	// ===== Time & language =====

	private ScrollViewer TimePage()
	{
		Pg pg = new Pg(this, "time");
		ISettingsWorld w = SettingsWorld.Current;
		CultureInfo culture = CultureInfo.CurrentCulture;
		DateTimeFormatInfo f = culture.DateTimeFormat;
		DateTime now = DateTime.Now;
		pg.Header("Clock");
		pg.Add(ValueLinkRow("time.now", now.ToString(f.ShortDatePattern, culture) + " " + now.ToString(f.ShortTimePattern, culture)));
		pg.Add(KnobRow("time.24h"));
		pg.Add(KnobRow("time.shortdate"));
		pg.Add(KnobRow("time.firstday"));
		pg.Add(KnobRow("time.measure"));
		pg.Header("Time zone");
		pg.Add(ValueLinkRow("time.zone", TimeZoneInfo.Local.DisplayName));
		pg.Add(LinkRow("time.clocks"));
		pg.Header("Language");
		pg.Add(InfoRow("time.display", CultureInfo.CurrentUICulture.DisplayName));
		pg.Add(LinkRow("time.region"));
		pg.Add(LinkRow("time.admin", "Language for non-Unicode programs"));
		pg.Add(LinkRow("time.speech"));
		ResetCard(pg, "time", defaults: false);
		pg.Footnote("Formats change for your account at once; the time zone and the display language open Windows.");
		return pg.Done();
	}

	// "dd-MMM-yy  (28-Oct-26)"
	private static string DatePreview(string pattern)
	{
		try
		{
			return pattern + "  (" + DateTime.Today.ToString(pattern, CultureInfo.CurrentCulture) + ")";
		}
		catch
		{
			return pattern;
		}
	}

	// ===== Apps =====

	private ScrollViewer AppsPage()
	{
		Pg pg = new Pg(this, "apps");
		ISettingsWorld w = SettingsWorld.Current;
		pg.Header("Default apps");
		string[] ids = new string[6] { "apps.default.browser", "apps.default.email", "apps.default.pdf", "apps.default.photos", "apps.default.music", "apps.default.video" };
		List<FrameworkElement?> rows = new List<FrameworkElement?>();
		foreach (string id in ids)
		{
			FrameworkElement? r = ValueLinkRow(id, null);
			rows.Add(r);
			pg.Add(r);
		}
		ReadAsync(w.ReadDefaultApps, delegate((string Kind, string App)[] apps)
		{
			for (int i = 0; i < ids.Length && i < apps.Length; i++)
			{
				if (rows[i]?.Tag is TextBlock t)
				{
					t.Text = apps[i].App;
				}
				else
				{
					SetInfo(rows[i], apps[i].App);
				}
			}
		});
		pg.Add(LinkRow("apps.defaults", "Change the apps that open each kind of file"));
		pg.Add(LinkRow("apps.defaultprograms"));
		pg.Header("Startup");
		FrameworkElement startup = InfoRow("apps.startuplist", null, "…");
		pg.Add(startup);
		ReadAsync(w.ReadStartupApps, delegate(StartupAppInfo[] apps)
		{
			int on = apps.Count((StartupAppInfo a) => a.Enabled);
			SetInfo(startup, apps.Length == 0 ? "None" : on + " of " + apps.Length + " on");
			if (startup is Grid g && FindSecondary(g) is TextBlock line)
			{
				line.Text = apps.Length == 0 ? "No startup entries" : string.Join(" · ", apps.Select((StartupAppInfo a) => a.Enabled ? a.Name : a.Name + " (off)"));
			}
		});
		pg.Add(LinkRow("apps.startup", "Turn startup apps on or off"));
		pg.Header("Apps");
		pg.Add(LinkRow("apps.installed", "Uninstall or change programs"));
		pg.Add(LinkRow("apps.optional"));
		pg.Header("Files & Explorer");
		pg.Add(KnobRow("apps.extensions"));
		pg.Add(KnobRow("apps.hidden"));
		ResetCard(pg, "apps", defaults: true);
		PolicyFootnote(pg);
		return pg.Done();
	}

	// ===== Privacy & security =====

	// The privacy pages this PC shows (from the system index once it is there, otherwise the common ones the policy
	// allows), as (title, uri).
	private List<(string Title, string Uri)> PrivacyPages()
	{
		List<(string, string)> list = new List<(string, string)>();
		SystemSearchIndex? index = SystemSearchIndex.Current;
		if (index != null)
		{
			foreach (SystemHit h in index.Hits)
			{
				if (h.Kind == SystemHitKind.SettingsPage && h.Launch == SystemLaunchKind.Uri && h.Target.StartsWith("ms-settings:privacy", StringComparison.OrdinalIgnoreCase)
					&& (_caps == null || _caps.PageAvailable(SettingsLink.SettingsPageName(h.Target) ?? "")))
				{
					if (!list.Any(((string, string) x) => string.Equals(x.Item2, h.Target, StringComparison.OrdinalIgnoreCase)))
					{
						list.Add((h.Name, h.Target));
					}
				}
			}
			return list.OrderBy(((string, string) x) => x.Item1, StringComparer.CurrentCultureIgnoreCase).ToList();
		}
		foreach ((string page, string title) in CommonPrivacyPages)
		{
			if (_caps == null || _caps.PageAvailable(page))
			{
				list.Add((title, "ms-settings:" + page));
			}
		}
		return list;
	}

	private static readonly (string Page, string Title)[] CommonPrivacyPages = new (string, string)[]
	{
		("privacy-general", "General"), ("privacy-speech", "Speech"), ("privacy-activityhistory", "Activity history"),
		("privacy-location", "Location"), ("privacy-webcam", "Camera"), ("privacy-microphone", "Microphone"),
		("privacy-notifications", "Notifications"), ("privacy-accountinfo", "Account info"), ("privacy-contacts", "Contacts"),
		("privacy-calendar", "Calendar"), ("privacy-phonecalls", "Phone calls"), ("privacy-callhistory", "Call history"),
		("privacy-email", "Email"), ("privacy-tasks", "Tasks"), ("privacy-messaging", "Messaging"), ("privacy-radios", "Radios"),
		("privacy-backgroundapps", "Background apps"), ("privacy-appdiagnostics", "App diagnostics"), ("privacy-documents", "Documents"),
		("privacy-pictures", "Pictures"), ("privacy-videos", "Videos"), ("privacy-musiclibrary", "Music library"),
		("privacy-downloadsfolder", "Downloads folder"), ("privacy-broadfilesystemaccess", "File system")
	};

	private bool SecurityLinkAvailable()
	{
		LinkSpec? spec = HubCatalog.Row("privacy.security")?.Link;
		return spec != null && SettingsLink.Available(spec, _caps);
	}

	private ScrollViewer PrivacyPage()
	{
		Pg pg = new Pg(this, "privacy");
		List<(string Title, string Uri)> pages = PrivacyPages();
		if (pages.Count > 0)
		{
			pg.Header("App permissions");
			foreach ((string title, string uri) in pages)
			{
				pg.Add(LinkRow("privacy." + uri.Substring(12), null, LinkSpec.Page(uri), title));
			}
		}
		if (SecurityLinkAvailable())
		{
			pg.Header("Security");
			pg.Add(LinkRow("privacy.security", "Virus protection, firewall and device security"));
		}
		pg.Footnote("The launcher never changes privacy or security settings; every row opens Windows.");
		PolicyFootnote(pg);
		return pg.Done();
	}

	// ===== About this PC =====

	private ScrollViewer AboutPage()
	{
		Pg pg = new Pg(this, "about");
		ISettingsWorld w = SettingsWorld.Current;
		pg.Block(Hero("#4A5459", '', SystemInfo.PcName, SystemInfo.Edition));
		pg.Header("Device");
		string version = WindowsVersion();
		pg.Add(InfoRow("about.device", version, title: "Windows version"));
		FrameworkElement cpu = InfoRow("about.cpu", null, title: "Processor");
		FrameworkElement ram = InfoRow("about.ram", null, title: "Installed RAM");
		pg.Add(cpu);
		pg.Add(ram);
		FrameworkElement gpu = InfoRow("about.gpu", null);
		FrameworkElement displays = InfoRow("about.displays", null);
		pg.Add(gpu);
		pg.Add(displays);
		pg.Add(InfoRow("about.type", SystemInfo.SystemType, title: "System type"));
		pg.Add(InfoRow("about.uptime", Uptime(), title: "Up time"));
		string[] specs = new string[4];
		ReadAsync(() => (SystemInfo.Processor, SystemInfo.InstalledRam, w.ReadGpu()), delegate((string Cpu, string Ram, string Gpu) v)
		{
			SetInfo(cpu, v.Cpu);
			SetInfo(ram, v.Ram);
			SetInfo(gpu, v.Gpu);
			specs[0] = v.Cpu;
			specs[1] = v.Ram;
			specs[2] = v.Gpu;
		});
		ReadAsync(w.ReadMonitors, delegate(MonitorInfo[] list)
		{
			string text = string.Join(", ", list.Select((MonitorInfo m) => m.ModeText));
			SetInfo(displays, list.Length == 0 ? "None" : text);
			specs[3] = text;
		});
		pg.Add(ButtonRow("about.copy", "Copy", delegate
		{
			StringBuilder sb = new StringBuilder();
			sb.AppendLine("Device name: " + SystemInfo.PcName);
			sb.AppendLine("Edition: " + SystemInfo.Edition);
			sb.AppendLine("Version: " + version);
			sb.AppendLine("Processor: " + (specs[0] ?? SystemInfo.Processor));
			sb.AppendLine("Installed RAM: " + (specs[1] ?? SystemInfo.InstalledRam));
			sb.AppendLine("Graphics: " + (specs[2] ?? ""));
			sb.AppendLine("Displays: " + (specs[3] ?? ""));
			sb.AppendLine("Up time: " + Uptime());
			sb.AppendLine("System type: " + SystemInfo.SystemType);
			sb.AppendLine("Pen and touch: " + SystemInfo.PenAndTouch);
			try
			{
				Clipboard.SetText(sb.ToString());
			}
			catch (Exception ex)
			{
				Logger.Log("PC settings: copy specs: " + ex.Message);
			}
		}, "For support requests"));
		pg.Add(LinkRow("about.rename"));
		pg.Header("Storage");
		StackPanel drives = new StackPanel();
		pg.Add(CustomRow("about.storage", drives, new Thickness(0.0)));
		drives.Children.Add(InfoRowDetached("Drives", "…"));
		ReadAsync(w.ReadDrives, delegate(DriveSpace[] list)
		{
			drives.Children.Clear();
			foreach (DriveSpace d in list)
			{
				if (drives.Children.Count > 0)
				{
					drives.Children.Add(new Border { Height = 1.0, Background = _pal.Separator, Margin = new Thickness(12.0, 0.0, 0.0, 0.0) });
				}
				drives.Children.Add(DriveRow(d));
			}
			if (list.Length == 0)
			{
				drives.Children.Add(InfoRowDetached("Drives", "None found"));
			}
		});
		FrameworkElement bin = InfoRow("about.recycle", null);
		pg.Add(bin);
		ReadAsync(w.ReadRecycleBinBytes, delegate(long bytes) { SetInfo(bin, bytes < 0 ? "Unknown" : bytes < 1048576 ? "Empty or under 1 MB" : (bytes / 1048576.0).ToString(bytes >= 10485760 ? "0" : "0.#", CultureInfo.CurrentCulture) + " MB"); });
		pg.Add(LinkRow("about.storage", "Storage Sense and what uses space", title: "Storage settings"));
		pg.Add(LinkRow("about.cleanup"));
		pg.Add(LinkRow("about.recyclebin"));
		pg.Add(LinkRow("about.diskmgmt"));
		pg.Header("Windows Update");
		// one row: Windows Update with its status on the second line (read-only: managed by policy, restart pending)
		FrameworkElement? updateLink = LinkRow("about.update", "…");
		FrameworkElement update = updateLink ?? InfoRow("about.update", null, null, "Status");
		pg.Add(update);
		ReadAsync(w.ReadUpdateStatus, delegate(string v)
		{
			string status = v.Length > 0 ? v : "Open Windows Update to check for updates";
			if (updateLink is Button { Content: Grid g } && FindSecondary(g) is TextBlock line)
			{
				line.Text = status;
				AutomationProperties.SetHelpText(updateLink, status);
			}
			else
			{
				SetInfo(update, status);
			}
		});
		pg.Add(LinkRow("about.updatehistory"));
		pg.Header("Recovery");
		pg.Add(LinkRow("about.recovery", "Reset this PC, advanced startup"));
		pg.Add(LinkRow("about.protection", "Restore points"));
		pg.Add(LinkRow("about.activation"));
		PolicyFootnote(pg);
		return pg.Done();
	}

	private FrameworkElement DriveRow(DriveSpace d)
	{
		double used = d.Total <= 0 ? 0.0 : Math.Clamp(1.0 - (double)d.Free / d.Total, 0.0, 1.0);
		Grid bar = new Grid { Width = 160.0, Height = 6.0, Background = _pal.Separator, VerticalAlignment = VerticalAlignment.Center };
		bar.Children.Add(new Border { Width = 160.0 * used, HorizontalAlignment = HorizontalAlignment.Left, Background = _pal.ToggleOn });
		string title = d.Name + (d.Label.Length > 0 ? " " + d.Label : "");
		return RowShell(title, GB(d.Free) + " free of " + GB(d.Total), null, bar, out _);
	}

	private static string GB(long bytes) => (bytes / 1073741824.0).ToString(bytes >= 107374182400L ? "0" : "0.#", CultureInfo.CurrentCulture) + " GB";

	private static string WindowsVersion()
	{
		try
		{
			using RegistryKey? k = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
			string display = k?.GetValue("DisplayVersion") as string ?? "";
			string build = k?.GetValue("CurrentBuild") as string ?? "";
			string ubr = k?.GetValue("UBR") is int u ? "." + u : "";
			return (display.Length > 0 ? display + " · " : "") + "build " + build + ubr;
		}
		catch
		{
			return Environment.OSVersion.VersionString;
		}
	}

	private static string Uptime()
	{
		TimeSpan t = TimeSpan.FromMilliseconds(Environment.TickCount64);
		return t.Days > 0 ? $"{t.Days} d {t.Hours} h" : $"{t.Hours} h {t.Minutes} min";
	}

	// ===== Control Panel & tools =====

	private readonly List<(string Key, Image Img, string Path, string Name)> _toolIcons = new List<(string, Image, string, string)>();

	private bool _toolIconsSubscribed;

	private ScrollViewer ToolsPage()
	{
		Pg pg = new Pg(this, "tools");
		ReleaseToolsIcons();
		SystemSearchIndex? index = SystemSearchIndex.Current;
		if (index == null)
		{
			pg.Header("Control Panel");
			pg.Add(InfoRow("tools.controlpanel", "…", "Reading the Control Panel items"));
		}
		else
		{
			List<SystemHit> cpl = index.Hits.Where((SystemHit h) => h.Kind == SystemHitKind.ControlPanelItem).GroupBy((SystemHit h) => h.Name, StringComparer.CurrentCultureIgnoreCase).Select((IGrouping<string, SystemHit> g) => g.First()).OrderBy((SystemHit h) => h.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
			List<SystemHit> tools = index.Hits.Where((SystemHit h) => h.Kind == SystemHitKind.Tool).GroupBy((SystemHit h) => h.Name, StringComparer.CurrentCultureIgnoreCase).Select((IGrouping<string, SystemHit> g) => g.First()).OrderBy((SystemHit h) => h.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
			pg.Header("Control Panel items");
			bool first = true;
			foreach (SystemHit h in cpl)
			{
				pg.Add(ToolRow(h, first ? "tools.controlpanel" : null));
				first = false;
			}
			pg.Header("Administrative tools");
			first = true;
			foreach (SystemHit h in tools)
			{
				pg.Add(ToolRow(h, first ? "tools.admin" : null));
				first = false;
			}
		}
		pg.Header("Performance tools");
		PerfTools(pg);
		return pg.Done();
	}

	// A 32 DIP row with the entry's own icon (loaded through the Windows 7 Start menu's shared icon cache).
	private FrameworkElement ToolRow(SystemHit h, string? id)
	{
		Grid g = new Grid { Height = 32.0 };
		g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(36.0) });
		g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.0, GridUnitType.Star) });
		g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
		Image img = new Image { Width = 16.0, Height = 16.0, Margin = new Thickness(12.0, 0.0, 0.0, 0.0), HorizontalAlignment = HorizontalAlignment.Left };
		g.Children.Add(img);
		TextBlock name = Label(h.Name);
		Grid.SetColumn(name, 1);
		g.Children.Add(name);
		TextBlock glyph = TrailingGlyph("");
		glyph.Margin = new Thickness(0.0, 0.0, 14.0, 0.0);
		glyph.VerticalAlignment = VerticalAlignment.Center;
		Grid.SetColumn(glyph, 2);
		g.Children.Add(glyph);
		Button b = new Button
		{
			Template = _pal.RowTemplate,
			Style = BareButton,
			Content = g,
			HorizontalContentAlignment = HorizontalAlignment.Stretch,
			Cursor = System.Windows.Input.Cursors.Hand,
			ToolTip = h.Describe()
		};
		b.SetResourceReference(FrameworkElement.FocusVisualStyleProperty, "Metro81.Focus");
		AutomationProperties.SetName(b, h.Name);
		b.Click += delegate { SettingsLink.Run(h, h.CanElevate && h.Elevate); };
		if (!string.IsNullOrEmpty(h.Icon))
		{
			BindToolIcon(img, h.Icon!, h.Name);
		}
		Register(id, b);
		return b;
	}

	private void BindToolIcon(Image img, string path, string name)
	{
		if (!SettingsWorld.IsReal)
		{
			return;
		}
		int px = DevicePixels(16.0);
		try
		{
			if (W7Icons.TryGet(path, 16, px, out W7Icons.Entry e) && e.Img != null)
			{
				img.Source = e.Img;
				return;
			}
			W7Icons.Request(path, 16, px, name);
			if (W7Icons.TryGet(path, 16, px, out e) && e.Img != null)
			{
				img.Source = e.Img;
				return;
			}
			_toolIcons.Add((W7Icons.Key(path, 16, px), img, path, name));
			if (!_toolIconsSubscribed)
			{
				_toolIconsSubscribed = true;
				W7Icons.Loaded += OnToolIconLoaded;
			}
		}
		catch
		{
		}
	}

	private void OnToolIconLoaded(string key)
	{
		base.Dispatcher.BeginInvoke(DispatcherPriority.Background, (Action)delegate
		{
			for (int i = _toolIcons.Count - 1; i >= 0; i--)
			{
				if (_toolIcons[i].Key == key && W7Icons.TryGetKey(key, out W7Icons.Entry e) && e.Img != null)
				{
					_toolIcons[i].Img.Source = e.Img;
					_toolIcons.RemoveAt(i);
				}
			}
			if (_toolIcons.Count == 0)
			{
				ReleaseToolsIcons();
			}
		});
	}

	private void ReleaseToolsIcons()
	{
		_toolIcons.Clear();
		if (_toolIconsSubscribed)
		{
			_toolIconsSubscribed = false;
			W7Icons.Loaded -= OnToolIconLoaded;
		}
	}

	// The performance tools: found on disk off the UI thread (the PATH scan), installed with winget in a visible
	// terminal when missing.
	private void PerfTools(Pg pg)
	{
		(string Title, string Desc, string[] Exe, string Winget, string? Url)[] tools = new (string, string, string[], string, string?)[]
		{
			("Task Manager", "Processes, performance, startup and services", new[] { "Taskmgr.exe" }, "", null),
			("Resource Monitor", "Live CPU, memory, disk and network usage", new[] { "resmon.exe" }, "", null),
			("Performance Monitor", "Data collector sets and counters", new[] { "perfmon.exe" }, "", null),
			("System Configuration", "Boot options, services and startup", new[] { "msconfig.exe" }, "", null),
			("Windows Performance Recorder", "Record ETW traces", new[] { "wprui.exe", "wpr.exe" }, "Microsoft.WindowsWDK.10.0", "https://learn.microsoft.com/windows-hardware/test/wpt/"),
			("Windows Performance Analyzer", "Analyse recorded ETW traces", new[] { "wpa.exe" }, "Microsoft.WindowsWDK.10.0", "https://learn.microsoft.com/windows-hardware/test/wpt/"),
			("GPUView", "GPU and CPU scheduling from an ETW trace", new[] { "GPUView\\GPUView.exe", "gpuview.exe" }, "Microsoft.WindowsWDK.10.0", "https://learn.microsoft.com/windows-hardware/drivers/display/using-gpuview"),
			("Process Explorer", "A powerful Task Manager replacement", new[] { "procexp64.exe", "procexp.exe" }, "Microsoft.Sysinternals.ProcessExplorer", "https://learn.microsoft.com/sysinternals/downloads/process-explorer"),
			("Process Monitor", "Live file, registry and process activity", new[] { "Procmon64.exe", "Procmon.exe" }, "Microsoft.Sysinternals.ProcessMonitor", "https://learn.microsoft.com/sysinternals/downloads/procmon"),
			("LatencyMon", "Real-time DPC/ISR latency monitoring", new[] { "LatMon.exe", "LatencyMon.exe" }, "Resplendence.LatencyMon", "https://www.resplendence.com/latencymon")
		};
		bool first = true;
		foreach ((string title, string desc, string[] exe, string winget, string? url) in tools)
		{
			string t = title;
			string? found = null;
			bool done = false;
			Button row = ClickRow(title, desc + " · …", TrailingGlyph(""), delegate
			{
				if (!done)
				{
					return;
				}
				if (found != null)
				{
					SettingsLink.Shell(found);
				}
				else
				{
					OfferInstall(t, winget, url);
				}
			});
			if (!string.IsNullOrEmpty(winget))
			{
				row.ToolTip = WingetCommand(winget);
			}
			pg.Add(row);
			Register(first ? "tools.performance" : null, row);
			first = false;
			TextBlock? line = row.Content is Grid g ? FindSecondary(g) : null;
			ReadAsync(() => FindTool(exe), delegate(string? path)
			{
				found = path;
				done = true;
				if (line != null)
				{
					line.Text = desc + " · " + (path != null ? "Installed" : "Not installed — click to install");
				}
			});
		}
	}

	private static string? FindTool(string[] candidates)
	{
		foreach (string c in candidates)
		{
			try
			{
				if (Path.IsPathRooted(c))
				{
					if (File.Exists(c))
					{
						return c;
					}
					continue;
				}
				string sys = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), c);
				if (File.Exists(sys))
				{
					return sys;
				}
				foreach (string pf in new[] { Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles) })
				{
					if (!string.IsNullOrEmpty(pf))
					{
						string wpt = Path.Combine(pf, "Windows Kits", "10", "Windows Performance Toolkit", c);
						if (File.Exists(wpt))
						{
							return wpt;
						}
					}
				}
				foreach (string dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';'))
				{
					if (string.IsNullOrWhiteSpace(dir))
					{
						continue;
					}
					try
					{
						string p = Path.Combine(dir.Trim(), c);
						if (File.Exists(p))
						{
							return p;
						}
					}
					catch
					{
					}
				}
			}
			catch
			{
			}
		}
		return null;
	}

	// The exact command an install runs: the tooltip, the confirmation and the terminal show the same text. No auto-accept
	// flags: winget asks for the package and source agreements itself, in the visible terminal.
	internal static string WingetCommand(string id) => "winget install --id " + id + " -e";

	// winget in a visible terminal (the user sees the exact command), or the download page.
	private void OfferInstall(string title, string wingetId, string? url)
	{
		try
		{
			bool hasWinget = !string.IsNullOrEmpty(wingetId);
			string msg = "\"" + title + "\" is not installed.\n\n" + (hasWinget ? "Install it now with winget? It runs in a window you can see; winget asks you there to accept the package's terms.\n\n" + WingetCommand(wingetId) : "Open the download page?");
			if (MessageBox.Show(this, msg, "Install " + title, MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
			{
				return;
			}
			if (hasWinget)
			{
				SettingsLink.Command("cmd.exe", "/k " + WingetCommand(wingetId));
			}
			else if (!string.IsNullOrEmpty(url))
			{
				SettingsLink.Shell(url!);
			}
		}
		catch (Exception ex)
		{
			Logger.Log("PC settings install '" + title + "': " + ex.Message);
		}
	}
}
