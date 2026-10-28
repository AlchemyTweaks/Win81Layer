using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

#nullable enable annotations

namespace Win81Layer;

// The Windows pages of the hub: Home, Account, Wi-Fi & network, Bluetooth & devices, Sound, Displays, Power & sleep,
// Mouse, Keyboard, Touchpad & pen. Every native control reads Windows' value first (slow reads off the UI thread, "…"
// until they arrive), applies a change live, persists it once and journals it (Undo). Brightness, volumes and app volumes
// are applied live and not journaled. Each page ends with its "More" links.
public sealed partial class PcSettingsWindow
{
	// Turn off screen / sleep after: seconds (Never = 0); the current custom value is added by the row.
	private static readonly (string Value, string Label)[] TimeoutChoices = new (string, string)[]
	{
		("60", "1 minute"), ("120", "2 minutes"), ("180", "3 minutes"), ("300", "5 minutes"), ("600", "10 minutes"), ("900", "15 minutes"),
		("1200", "20 minutes"), ("1500", "25 minutes"), ("1800", "30 minutes"), ("2700", "45 minutes"), ("3600", "1 hour"), ("7200", "2 hours"),
		("10800", "3 hours"), ("14400", "4 hours"), ("18000", "5 hours"), ("0", "Never")
	};

	// "Some Windows Settings pages are hidden on this PC by a policy" under the pages whose links it changed.
	private void PolicyFootnote(Pg pg)
	{
		if (_caps != null && _caps.PolicyHidesPages)
		{
			pg.Footnote("Some Windows Settings pages are hidden on this PC by a policy; their Control Panel equivalents open instead where there is one.");
		}
	}

	// A card built after a slow read (a monitor, an app list): the same look as the page builder's cards.
	private Border LooseCard(UIElement content, double top = 12.0)
	{
		return new Border
		{
			Background = _pal.Card,
			BorderBrush = _pal.CardBorder,
			BorderThickness = new Thickness(1.0),
			SnapsToDevicePixels = true,
			Child = content,
			Margin = new Thickness(0.0, top, 0.0, 0.0)
		};
	}

	private Border Separator()
	{
		return new Border { Height = 1.0, Background = _pal.Separator, Margin = new Thickness(12.0, 0.0, 0.0, 0.0), SnapsToDevicePixels = true, IsHitTestVisible = false };
	}

	private TextBlock LooseHeader(string text)
	{
		return new TextBlock
		{
			Text = text,
			FontFamily = SegoeUiSemibold,
			FontSize = 12.0,
			Foreground = _pal.Ink2,
			Margin = new Thickness(12.0, 20.0, 0.0, 6.0),
			TextTrimming = TextTrimming.CharacterEllipsis
		};
	}

	// A value applied live and not journaled (volume, brightness, an app's volume): read off the UI thread first, then
	// written on the worker at most every 60 ms while it moves, the final value once more when it is let go.
	private FrameworkElement TransientSlider(string id, string title, Func<int> read, Action<int> write, string throttleKey, string? help = null, string? low = null, string? high = null, FrameworkElement? leading = null, Action<int>? arrived = null, string unavailable = "Not available", Action? onUnavailable = null)
	{
		Slider s = new Slider
		{
			Minimum = 0.0,
			Maximum = 100.0,
			Value = 0.0,
			Width = (low != null && high != null) || leading != null ? 160.0 : 200.0,
			IsSnapToTickEnabled = true,
			TickFrequency = 1.0,
			SmallChange = 1.0,
			LargeChange = 10.0,
			VerticalAlignment = VerticalAlignment.Center,
			MinHeight = 28.0,
			IsEnabled = false
		};
		s.SetResourceReference(FrameworkElement.StyleProperty, "Metro81.Slider");
		StackPanel control = new StackPanel { Orientation = Orientation.Horizontal };
		if (leading != null)
		{
			control.Children.Add(leading);
		}
		if (low != null)
		{
			control.Children.Add(EndLabel(low, new Thickness(0.0, 0.0, 6.0, 0.0)));
		}
		control.Children.Add(s);
		if (high != null)
		{
			control.Children.Add(EndLabel(high, new Thickness(6.0, 0.0, 0.0, 0.0)));
		}
		Grid row = RowShell(title, "…", help, control, out _);
		AutomationProperties.SetName(s, title);
		TextBlock? value = FindSecondary(row);
		bool ready = false;
		s.ValueChanged += delegate
		{
			int v = (int)Math.Round(s.Value);
			if (value != null)
			{
				value.Text = v + "%";
			}
			if (ready)
			{
				SettingsWorker.Throttle(throttleKey, delegate { write(v); });
			}
		};
		s.AddHandler(System.Windows.Controls.Primitives.Thumb.DragCompletedEvent, (DragCompletedEventHandler)delegate
		{
			int v = (int)Math.Round(s.Value);
			if (ready)
			{
				SettingsWorker.Throttle(throttleKey, delegate { write(v); });
			}
		});
		ReadAsync(read, delegate(int v)
		{
			if (v < 0)
			{
				if (value != null)
				{
					value.Text = unavailable;
				}
				s.Visibility = Visibility.Collapsed;
				control.Visibility = Visibility.Collapsed;
				onUnavailable?.Invoke();
				return;
			}
			s.Value = v;
			if (value != null)
			{
				value.Text = v + "%";
			}
			s.IsEnabled = true;
			ready = true;
			arrived?.Invoke(v);
		});
		row.Tag = s;
		Register(id, row);
		return row;
	}

	// A small square glyph button (mute) in front of a volume slider.
	private ToggleButton MuteButton(string name, Func<bool?> read, Action<bool> write)
	{
		TextBlock glyph = new TextBlock { Text = "", FontFamily = Glyphs, FontSize = 14.0, Foreground = _pal.Ink, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
		ToggleButton t = new ToggleButton
		{
			Style = BareButton,
			Width = 28.0,
			Height = 28.0,
			Margin = new Thickness(0.0, 0.0, 6.0, 0.0),
			Content = glyph,
			IsEnabled = false,
			Cursor = Cursors.Hand
		};
		t.Template = MuteTemplate();
		t.SetResourceReference(FrameworkElement.FocusVisualStyleProperty, "Metro81.Focus");
		AutomationProperties.SetName(t, name);
		bool quiet = true;
		void Paint(bool muted)
		{
			glyph.Text = muted ? "" : "";
		}
		t.Checked += delegate
		{
			Paint(true);
			if (!quiet)
			{
				SettingsWorker.Run(delegate { write(true); });
			}
		};
		t.Unchecked += delegate
		{
			Paint(false);
			if (!quiet)
			{
				SettingsWorker.Run(delegate { write(false); });
			}
		};
		ReadAsync(read, delegate(bool? muted)
		{
			if (muted == null)
			{
				return;
			}
			t.IsChecked = muted.Value;
			Paint(muted.Value);
			t.IsEnabled = true;
			quiet = false;
		});
		return t;
	}

	private ControlTemplate? _muteTemplate;

	private ControlTemplate MuteTemplate()
	{
		if (_muteTemplate != null)
		{
			return _muteTemplate;
		}
		ControlTemplate t = new ControlTemplate(typeof(ToggleButton));
		FrameworkElementFactory bd = new FrameworkElementFactory(typeof(Border), "Bd");
		bd.SetValue(Border.BackgroundProperty, Brushes.Transparent);
		FrameworkElementFactory cp = new FrameworkElementFactory(typeof(ContentPresenter));
		cp.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
		cp.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
		bd.AppendChild(cp);
		t.VisualTree = bd;
		Trigger hover = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
		hover.Setters.Add(new Setter(Border.BackgroundProperty, _pal.RowHover, "Bd"));
		t.Triggers.Add(hover);
		Trigger off = new Trigger { Property = UIElement.IsEnabledProperty, Value = false };
		off.Setters.Add(new Setter(UIElement.OpacityProperty, 0.45));
		t.Triggers.Add(off);
		t.Seal();
		_muteTemplate = t;
		return t;
	}

	// ===== Home =====

	private ScrollViewer HomePage()
	{
		Pg pg = new Pg(this, "home");
		ISettingsWorld world = SettingsWorld.Current;
		pg.Block(Hero("#0072C6", '', "PC settings", "Windows and launcher settings, gathered in one place"));
		pg.Header("Quick settings");
		pg.Add(OutputChoice("home.output"));
		pg.Add(VolumeRow("home.volume", capture: false, "Volume"));
		StackPanel brightness = new StackPanel();
		pg.Add(CustomRow("home.brightness", brightness, new Thickness(0.0)));
		BrightnessRows(brightness, "home.brightness", quick: true);
		pg.Add(NSlider("home.speed", 1, 20, "Slow", "Fast", null, "Pointer speed", HubKnobs.Get("mouse.speed"), large: 5, page: "home"));
		AppSettings s = SettingsStore.FastSnapshot;
		string active = SeasonalTheme.NormalizeId(s.SeasonalActive);
		List<(string Id, string Label)> looks = SeasonalTheme.Choices.ToList();
		int lookIndex = Math.Max(0, looks.FindIndex(((string Id, string Label) c) => c.Id == SeasonalTheme.Normal ? active.Length == 0 : c.Id == active));
		pg.Add(SegmentedRow("home.look", looks.Select(((string Id, string Label) c) => c.Label).ToArray(), lookIndex, delegate(int i)
		{
			ApplySeason(looks[i].Id);
		}, "Normal brings back exactly what you had before a season or Dark rest."));
		pg.Add(NChoice("home.screen", (string? v) => TimeoutChoices, "When plugged in, the screen turns off after this long without input.", title: "Turn off screen after", knob: HubKnobs.Get("power.screen"), page: "home"));
		if (_caps != null && _caps.HasWifi)
		{
			pg.Add(NToggle("home.wifi", knob: HubKnobs.Get("network.wifitoggle"), title: "Wi-Fi", page: "home"));
		}
		FavoritesCard(pg);
		SetupsCard(pg);
		List<JournalEntry> recent = SettingsJournal.Snapshot();
		if (recent.Count > 0)
		{
			pg.Header("Recent changes");
			foreach (JournalEntry e in recent.Take(5))
			{
				JournalEntry target = e;
				Button undo = HubButton("Undo", delegate { UndoEntry(target); });
				Grid row = RowShell(e.Group != null ? (e.GroupTitle ?? e.Title) : e.Title, e.BeforeText + " → " + e.AfterText + "  ·  " + e.Utc.ToLocalTime().ToString("t", CultureInfo.CurrentCulture), null, undo, out _);
				AutomationProperties.SetName(undo, "Undo " + e.Title);
				pg.Add(row);
			}
			pg.Add(DrillRow("home.recent", recent.Count + (recent.Count == 1 ? " change" : " changes"), delegate { Navigate("recent", null, push: true); }, title: "All changes"));
		}
		FixChips(pg);
		pg.Header("Common");
		foreach ((string page, string line) in new[] { ("sound", "Output, input and app volume"), ("mouse", "Speed, buttons and wheel"), ("keyboard", "Repeat, cursor and languages"), ("displays", "Brightness, arrangement and night light"), ("power", "Plan, screen and sleep") })
		{
			if (HubCatalog.Page(page) != null)
			{
				string target = page;
				pg.Add(ClickRow(PageTitle(page), line, MenuGlyphs.ChevronPath(_pal.Ink2), delegate { Navigate(target, null, push: true); }));
			}
		}
		pg.Header("More");
		pg.Add(LinkRow("home.windows"));
		pg.Add(LinkRow("home.controlpanel"));
		return pg.Done();
	}

	// Home's Quick card: one brightness slider per monitor that answers over DDC/CI (the others are left out there; the
	// Displays page says "Brightness is set on the monitor" for them). Nothing shows when no monitor answers.
	private void BrightnessRows(StackPanel host, string firstId, bool quick)
	{
		ISettingsWorld world = SettingsWorld.Current;
		host.Children.Add(InfoRowDetached("Brightness", "…"));
		ReadAsync(world.ReadMonitors, delegate(MonitorInfo[] list)
		{
			host.Children.Clear();
			int n = 0;
			int hidden = 0;
			void Unavailable()
			{
				if (++hidden == list.Length && host.Parent is FrameworkElement row)
				{
					// no monitor answers: the row and the separator above it go
					row.Visibility = Visibility.Collapsed;
					if (row.Parent is Panel card)
					{
						int at = card.Children.IndexOf(row);
						if (at > 0)
						{
							card.Children[at - 1].Visibility = Visibility.Collapsed;
						}
					}
				}
			}
			foreach (MonitorInfo m in list)
			{
				Border? sep = n > 0 ? Separator() : null;
				if (sep != null)
				{
					host.Children.Add(sep);
				}
				string device = m.Device.Length > 0 ? m.Device : m.Name;
				string id = n == 0 ? firstId : firstId + ":" + m.Name;
				string title = list.Length > 1 ? "Brightness · " + m.Title : "Brightness";
				FrameworkElement? slider = null;
				slider = BrightnessSlider(id, title, device, quick ? delegate
				{
					slider!.Visibility = Visibility.Collapsed;
					if (sep != null)
					{
						sep.Visibility = Visibility.Collapsed;
					}
					Unavailable();
				} : null);
				host.Children.Add(slider);
				n++;
			}
			if (list.Length == 0)
			{
				host.Children.Add(InfoRowDetached("Brightness", "Brightness is set on the monitor"));
			}
		});
	}

	private FrameworkElement BrightnessSlider(string id, string title, string device, Action? onUnavailable = null)
	{
		ISettingsWorld world = SettingsWorld.Current;
		return TransientSlider(id, title, () => world.ReadMonitorBrightness(device), delegate(int v)
		{
			world.SetDdcBrightness(device, v);
		}, "ddc:" + device, "Changes the monitor's own brightness over DDC/CI.", "Dim", "Bright", unavailable: "Brightness is set on the monitor", onUnavailable: onUnavailable);
	}

	// The default output device as a drop-down (Home's Quick card).
	private FrameworkElement OutputChoice(string rowId)
	{
		AudioDefaultKnob k = (AudioDefaultKnob)HubKnobs.Get("sound.output")!;
		return NChoice(rowId, delegate(string? v)
		{
			// the devices of the latest read only (a device unplugged since is not offered)
			return k.Devices.Select((AudioEndpointInfo d) => (d.Id, d.Name)).ToArray();
		}, "Where sound plays. Calls keep their own device.", title: "Output device", knob: k, page: "home", width: 220.0);
	}

	private FrameworkElement VolumeRow(string id, bool capture, string title)
	{
		ISettingsWorld world = SettingsWorld.Current;
		ToggleButton mute = MuteButton(capture ? "Mute the microphone" : "Mute", delegate
		{
			(int Volume, bool Muted)? v = world.ReadEndpointVolume(capture);
			return v?.Muted;
		}, delegate(bool m) { world.SetEndpointMute(capture, m); });
		return TransientSlider(id, title, delegate
		{
			(int Volume, bool Muted)? v = world.ReadEndpointVolume(capture);
			return v == null ? -1 : v.Value.Volume;
		}, delegate(int v) { world.SetEndpointVolume(capture, v); }, (capture ? "in:" : "out:") + "volume", leading: mute);
	}

	// ===== Account =====

	private ScrollViewer AccountPage()
	{
		Pg pg = new Pg(this, "account");
		Border pic = new Border { Width = 32.0, Height = 32.0, Background = _pal.MiniTile("#0072C6") };
		pic.Child = new TextBlock { Text = "", FontFamily = Glyphs, FontSize = 18.0, Foreground = _pal.MiniGlyph, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
		if (_accountLoaded?.Picture is ImageSource img)
		{
			pic.Child = new Image { Source = img, Stretch = Stretch.UniformToFill };
		}
		string name = _accountLoaded?.Name is string n && n.Length > 0 ? n : SafeUserName();
		pg.Block(Hero("#0072C6", '', name, AccountKind(), pic));
		pg.Header("Account");
		pg.Add(LinkRow("account.picture"));
		pg.Add(LinkRow("account.yourinfo"));
		pg.Add(LinkRow("account.signin"));
		pg.Add(LinkRow("account.otherusers"));
		pg.Add(LinkRow("account.netplwiz", "Users, groups and sign-in without a password"));
		pg.Add(LinkRow("account.msa", "account.microsoft.com"));
		PolicyFootnote(pg);
		return pg.Done();
	}

	// ===== Wi-Fi & network =====

	private ScrollViewer NetworkPage()
	{
		Pg pg = new Pg(this, "network");
		ISettingsWorld world = SettingsWorld.Current;
		pg.Header("Status");
		FrameworkElement status = InfoRow("network.status", null);
		pg.Add(status);
		Button copy = HubButton("Copy IP", delegate { });
		copy.IsEnabled = false;
		Grid ipRow = RowShell(HubCatalog.Title("network.ip"), "…", null, copy, out _);
		Register("network.ip", ipRow);
		pg.Add(ipRow);
		ReadAsync(world.ReadNetwork, delegate(HubNetInfo net)
		{
			SetInfo(status, net.Name.Length > 0 && net.Name != net.Kind ? net.Kind + " · " + net.Name : net.Kind);
			TextBlock? second = FindSecondary(ipRow);
			if (net.Ipv4 == null)
			{
				if (second != null)
				{
					second.Text = net.Status.Length > 0 ? net.Status : "Not connected";
				}
				return;
			}
			if (second != null)
			{
				second.Text = net.Ipv4 + (net.Gateway != null ? " · gateway " + net.Gateway : "") + (net.Dns != null ? " · DNS " + net.Dns : "");
			}
			string text = "IPv4: " + net.Ipv4 + Environment.NewLine + "Gateway: " + (net.Gateway ?? "") + Environment.NewLine + "DNS: " + (net.Dns ?? "");
			copy.IsEnabled = true;
			copy.Click += delegate
			{
				try
				{
					Clipboard.SetText(text);
					copy.Content = "Copied";
				}
				catch (Exception ex)
				{
					Logger.Log("PC settings: copy IP: " + ex.Message);
				}
			};
		});
		FrameworkElement metered = InfoRow("network.metered", null);
		pg.Add(metered);
		ReadAsync(world.ReadMetered, delegate(string v) { SetInfo(metered, v); });
		if (_caps == null || _caps.HasWifi || _caps.HasRadios)
		{
			pg.Header("Wireless");
			if (_caps != null && _caps.HasWifi)
			{
				pg.Add(NToggle("network.wifitoggle"));
			}
			if (_caps != null && _caps.HasRadios)
			{
				pg.Add(NToggle("network.allwireless", "Turns every radio (Wi-Fi, Bluetooth, mobile) off. Turning it off again (or Undo) brings back the radios that were on. Windows' own Airplane mode is changed in Windows.", isOn: RadioKnob.AllOff));
			}
			if (_caps == null || _caps.HasWifi)
			{
				pg.Add(LinkRow("network.wifi"));
			}
		}
		pg.Header("More");
		FrameworkElement? proxy = ValueLinkRow("network.proxy", null);
		pg.Add(proxy);
		ReadAsync(world.ReadProxy, delegate(string v)
		{
			if (proxy?.Tag is TextBlock t)
			{
				t.Text = v;
			}
			else
			{
				SetInfo(proxy, v);
			}
		});
		pg.Add(LinkRow("network.ethernet"));
		pg.Add(LinkRow("network.vpn"));
		pg.Add(LinkRow("network.advanced"));
		pg.Add(LinkRow("network.adapters"));
		pg.Add(LinkRow("network.datausage"));
		if (_caps != null && (_caps.HasWifi || _caps.HasRadios))
		{
			ResetCard(pg, "network", defaults: false);
		}
		PolicyFootnote(pg);
		return pg.Done();
	}

	// ===== Bluetooth & devices =====

	private ScrollViewer DevicesPage()
	{
		Pg pg = new Pg(this, "devices");
		ISettingsWorld world = SettingsWorld.Current;
		if (_caps != null && _caps.HasBluetooth)
		{
			pg.Header("Bluetooth");
			pg.Add(NToggle("devices.bttoggle"));
			FrameworkElement paired = InfoRow("devices.paired", null, "…");
			pg.Add(paired);
			ReadAsync(world.ReadPairedBluetooth, delegate(string[] names)
			{
				SetInfo(paired, names.Length == 0 ? "None" : names.Length == 1 ? "1 device" : names.Length + " devices");
				if (paired is Grid g && FindSecondary(g) is TextBlock line)
				{
					line.Text = names.Length == 0 ? "No paired devices" : string.Join(" · ", names);
				}
			});
			pg.Add(LinkRow("devices.bluetooth", "Pair and manage Bluetooth devices"));
		}
		pg.Header("Devices");
		pg.Add(LinkRow("devices.printers"));
		pg.Add(LinkRow("devices.autoplay"));
		pg.Add(LinkRow("devices.usb"));
		pg.Add(LinkRow("devices.manager", "Drivers and hardware"));
		pg.Add(LinkRow("devices.devicesprinters"));
		if (_caps != null && _caps.Digitizer)
		{
			pg.Header("Pen & touch");
			pg.Add(LinkRow("devices.pen"));
		}
		if (_caps != null && _caps.HasBluetooth)
		{
			ResetCard(pg, "devices", defaults: false);
		}
		PolicyFootnote(pg);
		return pg.Done();
	}

	// ===== Sound =====

	private DispatcherTimer? _meterTimer;

	private FrameworkElement? _meterHost;

	private Border? _meterFill;

	// The audio subscriptions of the Sound pages on screen (one per page while it is loaded).
	private readonly HashSet<IDisposable> _liveSubs = new HashSet<IDisposable>();

	internal bool QaTreatAsActive { get; set; }

	internal bool QaMeterRunning => _meterTimer?.IsEnabled ?? false;

	internal int QaAudioListUpdates { get; private set; }

	private ScrollViewer SoundPage()
	{
		Pg pg = new Pg(this, "sound");
		ISettingsWorld world = SettingsWorld.Current;
		FrameworkElement hero = Hero("#D24726", '', "Sound", "…");
		pg.Block(hero);
		TextBlock? heroLine = Descendants<TextBlock>(hero).Skip(2).FirstOrDefault();
		pg.Header("Output");
		(FrameworkElement outputs, Action refreshOut) = DeviceList("sound.output", capture: false, delegate(AudioEndpointInfo[] list)
		{
			AudioEndpointInfo? d = list.FirstOrDefault((AudioEndpointInfo e) => e.IsDefault);
			if (heroLine != null)
			{
				heroLine.Text = d != null ? "Output: " + d.Name : "No output device";
			}
		});
		pg.Add(outputs);
		pg.Add(CallsToggle());
		pg.Add(VolumeRow("sound.volume", capture: false, "Output volume"));
		pg.Add(ButtonRow("sound.test", "Play", delegate
		{
			try
			{
				world.PlayTestSound();
			}
			catch (Exception ex)
			{
				Logger.Log("PC settings: test sound: " + ex.Message);
			}
		}, "On the output device above"));
		pg.Add(LinkRow("sound.playback", "Default device, format and enhancements"));
		pg.Header("Input");
		(FrameworkElement inputs, Action refreshIn) = DeviceList("sound.input", capture: true, null);
		pg.Add(inputs);
		pg.Add(VolumeRow("sound.inputvolume", capture: true, "Input volume"));
		pg.Add(MeterRow());
		pg.Add(LinkRow("sound.recording", "Levels and enhancements"));
		pg.Header("Apps");
		StackPanel apps = new StackPanel();
		pg.Add(CustomRow("sound.appvolume", apps, new Thickness(0.0)));
		AppVolumes(apps, 4);
		pg.Header("Sounds");
		pg.Add(ToggleRow("sound.win81", SettingsStore.FastSnapshot.UseWin81Sounds, SetWin81Sounds, "The authentic Windows 8.1 system sounds. Turn off to get your own sound scheme back."));
		pg.Add(LinkRow("sound.system", "Sound scheme and program events"));
		pg.Header("More");
		pg.Add(LinkRow("sound.settings", "Device, volume and balance"));
		pg.Add(LinkRow("sound.devices"));
		pg.Add(LinkRow("sound.apps", "Per-app volume and output device"));
		pg.Add(LinkRow("sound.panel"));
		pg.Add(LinkRow("sound.comms", "Lower other sounds during calls"));
		pg.Add(LinkRow("sound.mono"));
		ResetCard(pg, "sound", defaults: false);
		PolicyFootnote(pg);
		ScrollViewer sv = pg.Done();
		// device changes while the page shows (devices only, not every volume step): the lists follow without building the
		// page again; a burst of events is one refresh
		IDisposable? sub = null;
		int queued = 0;
		sv.Loaded += delegate
		{
			if (sub != null)
			{
				return;
			}
			sub = world.SubscribeAudio(delegate
			{
				if (System.Threading.Interlocked.Exchange(ref queued, 1) == 1)
				{
					return;
				}
				try
				{
					base.Dispatcher.BeginInvoke(DispatcherPriority.Background, (Action)delegate
					{
						System.Threading.Interlocked.Exchange(ref queued, 0);
						if (!_closed && sv.IsLoaded)
						{
							QaAudioListUpdates++;
							refreshOut();
							refreshIn();
						}
					});
				}
				catch
				{
					System.Threading.Interlocked.Exchange(ref queued, 0);
				}
			});
			_liveSubs.Add(sub);
		};
		sv.Unloaded += delegate
		{
			if (sub != null)
			{
				_liveSubs.Remove(sub);
				sub.Dispose();
				sub = null;
			}
		};
		return sv;
	}

	// The device list of one direction: a radio row per device, the default checked; a pick sets it (console and
	// multimedia roles). refresh() reads the devices again (a device came or went) and updates the rows in place.
	private (FrameworkElement Row, Action Refresh) DeviceList(string rowId, bool capture, Action<AudioEndpointInfo[]>? arrived)
	{
		ISettingsWorld world = SettingsWorld.Current;
		AudioDefaultKnob k = (AudioDefaultKnob)HubKnobs.Get(rowId)!;
		KnobBinding b = Bind(rowId, k, null, "sound");
		StackPanel list = new StackPanel();
		AudioEndpointInfo[] devices = Array.Empty<AudioEndpointInfo>();
		string group = "dev-" + rowId + "-" + _buildGen;
		bool quiet = false;
		List<(string Id, string Name, RadioButton Button)> shown = new List<(string, string, RadioButton)>();
		void Build(string? selected)
		{
			quiet = true;
			try
			{
				// the same devices as shown: only the checked one changes (keyboard focus stays on its row)
				if (devices.Length > 0 && devices.Length == shown.Count && devices.Select((AudioEndpointInfo d, int i) => d.Id == shown[i].Id && d.Name == shown[i].Name).All((bool x) => x))
				{
					foreach ((string id, string _, RadioButton rb) in shown)
					{
						bool on = k.Same(id, selected);
						if (rb.IsChecked != on)
						{
							rb.IsChecked = on;
						}
					}
					return;
				}
				shown.Clear();
				list.Children.Clear();
				if (devices.Length == 0)
				{
					list.Children.Add(InfoRowDetached(b.Title, selected == null ? "…" : "No device"));
					return;
				}
				int i = 0;
				foreach (AudioEndpointInfo d in devices)
				{
					if (i++ > 0)
					{
						list.Children.Add(Separator());
					}
					string id = d.Id;
					RadioButton rb = DeviceRadio(d.Name, group, k.Same(id, selected), capture);
					rb.Checked += delegate
					{
						if (!quiet && b.Committed != null)
						{
							Commit(b, id);
						}
					};
					shown.Add((id, d.Name, rb));
					list.Children.Add(rb);
				}
			}
			finally
			{
				quiet = false;
			}
		}
		b.Show = delegate(string? v) { Build(v); };
		b.Enable = delegate(bool e) { list.IsEnabled = e; };
		FrameworkElement wrap = Wrap(b, list);
		Func<string?> read = delegate
		{
			AudioEndpointInfo[] all = world.ReadEndpoints(capture);
			k.Remember(all);
			devices = all;
			return all.FirstOrDefault((AudioEndpointInfo e) => e.IsDefault)?.Id ?? "";
		};
		void Refresh()
		{
			ReadAsync(read, delegate(string? v)
			{
				Arrive(b, v);
				arrived?.Invoke(devices);
				if (!capture)
				{
					RefreshCalls();
				}
			});
		}
		Build(null);
		Refresh();
		return (wrap, Refresh);
	}

	private RadioButton DeviceRadio(string name, string group, bool isChecked, bool capture)
	{
		Grid g = new Grid { Height = RowH };
		g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.0, GridUnitType.Star) });
		g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
		TextBlock label = Label(name);
		label.Margin = new Thickness(12.0, 0.0, 12.0, 0.0);
		g.Children.Add(label);
		TextBlock check = new TextBlock { Text = "", FontFamily = Glyphs, FontSize = 14.0, Foreground = _pal.Link, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0.0, 0.0, 14.0, 0.0) };
		Grid.SetColumn(check, 1);
		g.Children.Add(check);
		RadioButton rb = new RadioButton
		{
			Style = BareButton,
			GroupName = group,
			Content = g,
			IsChecked = isChecked,
			HorizontalContentAlignment = HorizontalAlignment.Stretch,
			Template = DeviceTemplate(),
			Cursor = Cursors.Hand
		};
		check.Visibility = isChecked ? Visibility.Visible : Visibility.Hidden;
		rb.Checked += delegate { check.Visibility = Visibility.Visible; };
		rb.Unchecked += delegate { check.Visibility = Visibility.Hidden; };
		rb.SetResourceReference(FrameworkElement.FocusVisualStyleProperty, "Metro81.Focus");
		AutomationProperties.SetName(rb, (capture ? "Input device: " : "Output device: ") + name);
		return rb;
	}

	private ControlTemplate? _deviceTemplate;

	private ControlTemplate DeviceTemplate()
	{
		if (_deviceTemplate != null)
		{
			return _deviceTemplate;
		}
		ControlTemplate t = new ControlTemplate(typeof(RadioButton));
		FrameworkElementFactory bd = new FrameworkElementFactory(typeof(Border), "Bd");
		bd.SetValue(Border.BackgroundProperty, Brushes.Transparent);
		FrameworkElementFactory cp = new FrameworkElementFactory(typeof(ContentPresenter));
		cp.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Stretch);
		bd.AppendChild(cp);
		t.VisualTree = bd;
		Trigger hover = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
		hover.Setters.Add(new Setter(Border.BackgroundProperty, _pal.RowHover, "Bd"));
		t.Triggers.Add(hover);
		Trigger off = new Trigger { Property = UIElement.IsEnabledProperty, Value = false };
		off.Setters.Add(new Setter(UIElement.OpacityProperty, 0.55));
		t.Triggers.Add(off);
		t.Seal();
		_deviceTemplate = t;
		return t;
	}

	// "Also use for calls": on when the communications device is the output device. Turning it on sets the
	// communications role to the output device; off puts back the calls device it had before (or says where to pick one).
	private string? _callsBefore;

	private FrameworkElement CallsToggle()
	{
		FrameworkElement row = NToggle("sound.calls", "Calls and voice chat use this device too. Off keeps a separate calls device.", isOn: delegate(string? v)
		{
			string? output = _bindings.TryGetValue("sound.output", out KnobBinding? o) ? o.Committed : null;
			return v != null && output != null && v == output;
		}, valueFor: delegate(bool on, string? cur)
		{
			string? output = _bindings.TryGetValue("sound.output", out KnobBinding? o) ? o.Committed : null;
			if (on)
			{
				if (cur != null && cur != output)
				{
					_callsBefore = cur;
				}
				return output ?? cur ?? "";
			}
			if (_callsBefore != null && _callsBefore != output)
			{
				return _callsBefore;
			}
			if (_bindings.TryGetValue("sound.calls", out KnobBinding? c))
			{
				SetNote(c, "Choose the calls device in Sound Control Panel › Communications", error: false);
				base.Dispatcher.BeginInvoke(DispatcherPriority.Background, (Action)delegate { c.Show(c.Committed); });
			}
			return cur ?? "";
		}, read: false);
		RefreshCalls();
		return row;
	}

	private void RefreshCalls()
	{
		if (!_bindings.TryGetValue("sound.calls", out KnobBinding? b))
		{
			return;
		}
		ISettingsWorld w = SettingsWorld.Current;
		ReadAsync(() => b.Knob.Read(w), delegate(string? v) { Arrive(b, v); });
	}

	// The microphone's live level: polled at 15 Hz only while the Sound page shows and the window is active.
	private FrameworkElement MeterRow()
	{
		Grid bar = new Grid { Width = 200.0, Height = 6.0, Background = _pal.Separator, VerticalAlignment = VerticalAlignment.Center };
		_meterFill = new Border { Width = 0.0, HorizontalAlignment = HorizontalAlignment.Left, Background = _pal.ToggleOn };
		bar.Children.Add(_meterFill);
		Grid row = RowShell(HubCatalog.Title("sound.meter"), "Speak to test the microphone", null, bar, out _);
		AutomationProperties.SetName(bar, "Input level");
		Register("sound.meter", row);
		_meterHost = row;
		row.Loaded += delegate { UpdateMeter(); };
		row.Unloaded += delegate { UpdateMeter(); };
		row.IsVisibleChanged += delegate { UpdateMeter(); };
		return row;
	}

	private void UpdateMeter()
	{
		bool run = !_closed && _meterHost != null && _meterHost.IsLoaded && _meterHost.IsVisible && (base.IsActive || QaTreatAsActive);
		if (!run)
		{
			_meterTimer?.Stop();
			return;
		}
		if (_meterTimer == null)
		{
			_meterTimer = new DispatcherTimer(DispatcherPriority.Background, base.Dispatcher)
			{
				Interval = TimeSpan.FromMilliseconds(1000.0 / 15.0)
			};
			_meterTimer.Tick += delegate
			{
				if (_meterHost == null || !_meterHost.IsVisible || _meterFill == null)
				{
					_meterTimer!.Stop();
					return;
				}
				float peak = 0f;
				try
				{
					peak = SettingsWorld.Current.ReadPeak(capture: true);
				}
				catch
				{
				}
				_meterFill.Width = 200.0 * Math.Clamp(peak, 0f, 1f);
			};
		}
		_meterTimer.Start();
	}

	// App volumes: the first `max` sessions (all with max 0), each a live slider with its mute; "All apps ›" for more.
	private void AppVolumes(StackPanel host, int max)
	{
		ISettingsWorld world = SettingsWorld.Current;
		host.Children.Add(InfoRowDetached("App volume", "…"));
		ReadAsync(world.ReadSessions, delegate(AppVolumeInfo[] list)
		{
			host.Children.Clear();
			if (list.Length == 0)
			{
				host.Children.Add(InfoRowDetached("App volume", "No app is playing sound"));
				return;
			}
			int shown = 0;
			foreach (AppVolumeInfo a in list)
			{
				if (max > 0 && shown >= max)
				{
					break;
				}
				if (shown > 0)
				{
					host.Children.Add(Separator());
				}
				string key = a.Key;
				ToggleButton mute = MuteButton("Mute " + a.Name, () => a.Muted, delegate(bool m) { world.SetSessionMute(key, m); });
				host.Children.Add(TransientSlider(shown == 0 ? "sound.app0" : "sound.app" + shown, a.Name, () => a.Volume, delegate(int v) { world.SetSessionVolume(key, v); }, "app:" + key, leading: mute));
				shown++;
			}
			if (max > 0 && list.Length > max)
			{
				host.Children.Add(Separator());
				host.Children.Add(DrillRow("sound.allapps", list.Length + " apps", delegate { Navigate("sound/apps", null, push: true); }));
			}
		});
	}

	private ScrollViewer SoundAppsPage()
	{
		Pg pg = new Pg(this, "sound/apps");
		pg.Header("App volume");
		StackPanel apps = new StackPanel();
		pg.Add(CustomRow("sound.appvolume", apps, new Thickness(0.0)));
		AppVolumes(apps, 0);
		pg.Footnote("App volumes are relative to the output volume and are not kept in Recent changes.");
		return pg.Done();
	}

	// ===== Displays =====

	private ScrollViewer DisplaysPage()
	{
		Pg pg = new Pg(this, "displays");
		ISettingsWorld world = SettingsWorld.Current;
		StackPanel monitors = new StackPanel();
		pg.Block(monitors);
		monitors.Children.Add(LooseHeader("Monitors"));
		monitors.Children.Add(LooseCard(InfoRowDetached("Display", "…"), 0.0));
		ReadAsync(world.ReadMonitors, delegate(MonitorInfo[] list)
		{
			monitors.Children.Clear();
			int n = 0;
			foreach (MonitorInfo m in list)
			{
				// the monitor's name once (in the header), its mode once (in the card)
				monitors.Children.Add(LooseHeader(m.Title + (m.Primary ? " · main" : "")));
				StackPanel card = new StackPanel();
				FrameworkElement head = InfoRowDetached("Resolution", m.ModeText);
				Register(n == 0 ? "displays.monitor" : "displays.monitor:" + m.Name, head);
				card.Children.Add(head);
				card.Children.Add(Separator());
				string device = m.Device.Length > 0 ? m.Device : m.Name;
				card.Children.Add(BrightnessSlider(n == 0 ? "displays.brightness" : "displays.brightness:" + m.Name, "Brightness", device));
				monitors.Children.Add(LooseCard(card, 0.0));
				n++;
			}
		});
		pg.Header("Arrangement");
		pg.Add(LinkRow("displays.settings", "Resolution, scale, orientation and multiple displays"));
		pg.Add(LinkRow("displays.nightlight"));
		pg.Header("More");
		pg.Add(LinkRow("displays.advanced"));
		pg.Add(LinkRow("displays.graphics"));
		pg.Add(LinkRow("displays.textsize"));
		pg.Add(LinkRow("displays.cleartype"));
		pg.Add(LinkRow("displays.color"));
		pg.Footnote("Brightness is changed over DDC/CI on monitors that support it; resolution, scale and refresh rate open Windows.");
		PolicyFootnote(pg);
		return pg.Done();
	}

	// A label and value row that is not registered (lists inside a card).
	private FrameworkElement InfoRowDetached(string title, string value)
	{
		return ValueShell(title, value);
	}

	// ===== Power & sleep =====

	private ScrollViewer PowerPage()
	{
		Pg pg = new Pg(this, "power");
		ISettingsWorld world = SettingsWorld.Current;
		PowerPlanKnob plan = (PowerPlanKnob)HubKnobs.Get("power.plan")!;
		pg.Header("Plan");
		PowerPlanInfo[] plans = Array.Empty<PowerPlanInfo>();
		FrameworkElement planRow = NChoice("power.plan", (string? cur) => PlanChoices(plan, plans, cur), "Balanced, High performance and your own plans", knob: plan, width: 220.0, special: delegate { Navigate("power/plans", null, push: true); }, read: false);
		pg.Add(planRow);
		KnobBinding pb = _bindings["power.plan"];
		ReadInto(pb, delegate
		{
			plans = world.ReadPowerPlans();
			foreach (PowerPlanInfo p in plans)
			{
				plan.Names[p.Id] = p.Name;
			}
			return plan.Read(world);
		});
		pg.Header("Screen & sleep");
		// powersleep may be hidden by policy: the rows stay as long as the Control Panel equivalent can be opened.
		bool editable = _caps == null || _caps.PageAvailable("powersleep") || _caps.CplAllowed("powercfg.cpl");
		bool battery = _caps != null && _caps.HasBattery;
		bool sleep = _caps == null || _caps.SleepAllowed;
		if (editable)
		{
			pg.Add(PowerChoice("power.screen", battery ? "Plugged in, turn off screen after" : "Turn off screen after", TimeoutChoices, "When plugged in, the screen turns off after this long without input."));
			if (battery)
			{
				pg.Add(PowerChoice("power.screen.dc", null, TimeoutChoices, "On battery power, the screen turns off after this long without input."));
			}
			if (sleep)
			{
				pg.Add(PowerChoice("power.sleep", battery ? "Plugged in, sleep after" : "Sleep after", TimeoutChoices, "When plugged in, the PC goes to sleep after this long without input."));
				if (battery)
				{
					pg.Add(PowerChoice("power.sleep.dc", null, TimeoutChoices, "On battery power, the PC goes to sleep after this long without input."));
				}
			}
			else
			{
				pg.Add(InfoRow("power.sleep", "Not available", "Sleep isn't available on this PC"));
			}
		}
		if (_caps != null && (_caps.PowerButtonPresent || _caps.LidPresent || _caps.SleepButtonPresent) && editable)
		{
			List<(string Value, string Label)> actions = new List<(string, string)> { ("0", "Do nothing") };
			if (_caps.SleepAllowed)
			{
				actions.Add(("1", "Sleep"));
			}
			if (_caps.HibernateAllowed)
			{
				actions.Add(("2", "Hibernate"));
			}
			actions.Add(("3", "Shut down"));
			(string, string)[] acts = actions.ToArray();
			pg.Header("Buttons");
			if (_caps.PowerButtonPresent)
			{
				pg.Add(PowerChoice("power.button", null, acts, "What pressing the power button does.", hideUnreadable: true));
			}
			if (_caps.SleepButtonPresent)
			{
				pg.Add(PowerChoice("power.sleepbutton", null, acts, "What pressing the sleep button does.", hideUnreadable: true));
			}
			if (_caps.LidPresent)
			{
				pg.Add(PowerChoice("power.lid", null, acts, "What closing the lid does.", hideUnreadable: true));
			}
		}
		pg.Header("Launcher");
		AppSettings s = SettingsStore.FastSnapshot;
		string[] lockOpts = new string[4] { "Off", "After 5 minutes", "After 10 minutes", "After 15 minutes" };
		int[] lockVals = new int[4] { 0, 5, 10, 15 };
		pg.Add(ChoiceRow("power.autolock", lockOpts, Math.Max(0, Array.IndexOf(lockVals, s.AutoLockMinutes)), delegate(int i)
		{
			int v = lockVals[i];
			SettingsStore.Update(delegate(AppSettings x) { x.AutoLockMinutes = v; });
			HostApp?.ApplyAutoLock(v);
		}, "The launcher locks the PC after this long without input."));
		if (_caps != null)
		{
			pg.Header("Info");
			pg.Add(InfoRow("power.faststartup", _caps.FastStartup == null ? "Unknown" : (_caps.FastStartup.Value ? "On" : "Off"), help: "Shown only. Fast startup is changed in Choose what the power buttons do."));
			pg.Add(InfoRow("power.hibernate", _caps.HibernateAllowed ? "Available" : "Not available"));
		}
		pg.Header("More");
		pg.Add(LinkRow("power.options"));
		pg.Add(LinkRow("power.buttons"));
		pg.Add(LinkRow("power.planedit"));
		ResetCard(pg, "power", defaults: false);
		PolicyFootnote(pg);
		return pg.Done();
	}

	// The plan drop-down: the active plan, the built-in plans this PC has, the last three plans from Recent changes and
	// "All plans… (n)" (a 63-plan list is a page of its own).
	private static (string Value, string Label)[] PlanChoices(PowerPlanKnob k, PowerPlanInfo[] plans, string? current)
	{
		List<(string, string)> list = new List<(string, string)>();
		HashSet<Guid> seen = new HashSet<Guid>();
		void Add(Guid g)
		{
			if (seen.Add(g) && plans.Any((PowerPlanInfo p) => p.Id == g))
			{
				list.Add((g.ToString("D"), k.Format(g.ToString("D"))));
			}
		}
		if (Guid.TryParse(current, out Guid cur))
		{
			Add(cur);
		}
		foreach (Guid g in new[] { PowerPlanKnob.Balanced, PowerPlanKnob.High, PowerPlanKnob.Saver, PowerPlanKnob.Ultimate })
		{
			Add(g);
		}
		int recent = 0;
		foreach (JournalEntry e in SettingsJournal.Snapshot().Where((JournalEntry x) => x.Id == "power.plan"))
		{
			foreach (string v in new[] { e.After, e.Before })
			{
				if (recent < 3 && Guid.TryParse(v, out Guid g) && !seen.Contains(g) && plans.Any((PowerPlanInfo p) => p.Id == g))
				{
					Add(g);
					recent++;
				}
			}
		}
		if (plans.Length > 0)
		{
			list.Add(("special:all", "All plans… (" + plans.Length + ")"));
		}
		return list.ToArray();
	}

	// A power value of the active plan (read with its policy state off the UI thread): read-only with "Managed by
	// policy" when a policy sets it; left out when it cannot be read and hideUnreadable is set.
	private FrameworkElement PowerChoice(string rowId, string? title, (string Value, string Label)[] options, string help, bool hideUnreadable = false)
	{
		ISettingsWorld world = SettingsWorld.Current;
		PowerKnob k = (PowerKnob)HubKnobs.Get(rowId)!;
		FrameworkElement row = NChoice(rowId, (string? v) => options, help, title: title, knob: k, read: false);
		KnobBinding b = _bindings[rowId];
		b.Enable(false);
		ReadAsync(() => (k.Read(world), world.PowerManagedByPolicy(k.Setting)), delegate((string? Value, bool Managed) r)
		{
			if (r.Managed)
			{
				b.ReadOnly = true;
				SetNote(b, "Managed by policy", error: false);
			}
			Arrive(b, r.Value);
			if (r.Value == null && hideUnreadable)
			{
				b.Row.Visibility = Visibility.Collapsed;
			}
		});
		return row;
	}

	// All the power plans, with a filter; a click makes a plan active (journaled with its GUID).
	private ScrollViewer PowerPlansPage()
	{
		Pg pg = new Pg(this, "power/plans");
		ISettingsWorld world = SettingsWorld.Current;
		PowerPlanKnob k = (PowerPlanKnob)HubKnobs.Get("power.plan")!;
		KnobBinding b = Bind("power.plan", k, "Power plan", "power/plans");
		TextBox filter = new TextBox { Width = 220.0, Height = 28.0, VerticalContentAlignment = VerticalAlignment.Center, FontSize = 13.5 };
		filter.SetResourceReference(FrameworkElement.StyleProperty, "Metro81.TextBox");
		filter.SetResourceReference(FrameworkElement.FocusVisualStyleProperty, "Metro81.Focus");
		pg.Header("Find a plan");
		Grid filterRow = RowShell("Filter", null, null, filter, out _);
		AutomationProperties.SetName(filter, "Filter plans");
		Register("power.plans.filter", filterRow);
		pg.Add(filterRow);
		pg.Header("Plans");
		StackPanel list = new StackPanel();
		FrameworkElement wrap = Wrap(b, list);
		pg.Add(wrap);
		PowerPlanInfo[] plans = Array.Empty<PowerPlanInfo>();
		string group = "plans-" + _buildGen;
		void Build()
		{
			list.Children.Clear();
			string q = filter.Text.Trim();
			int i = 0;
			foreach (PowerPlanInfo p in plans.Where((PowerPlanInfo x) => q.Length == 0 || x.Name.Contains(q, StringComparison.CurrentCultureIgnoreCase)))
			{
				if (i++ > 0)
				{
					list.Children.Add(Separator());
				}
				string id = p.Id.ToString("D");
				RadioButton rb = DeviceRadio(p.Name, group, k.Same(id, b.Committed), capture: false);
				AutomationProperties.SetName(rb, "Power plan: " + p.Name);
				rb.Checked += delegate
				{
					if (b.Committed != null && !k.Same(id, b.Committed))
					{
						Commit(b, id);
					}
				};
				list.Children.Add(rb);
			}
			if (i == 0)
			{
				list.Children.Add(InfoRowDetached("Plans", plans.Length == 0 ? "…" : "No plan matches"));
			}
		}
		b.Show = delegate { Build(); };
		b.Enable = delegate(bool e) { list.IsEnabled = e; };
		filter.TextChanged += delegate { Build(); };
		ReadInto(b, delegate
		{
			plans = world.ReadPowerPlans().OrderBy((PowerPlanInfo p) => p.Name, StringComparer.CurrentCultureIgnoreCase).ToArray();
			foreach (PowerPlanInfo p in plans)
			{
				k.Names[p.Id] = p.Name;
			}
			return k.Read(world);
		});
		pg.Footnote("Plans are switched only; creating, editing or deleting plans opens Power Options.");
		pg.Card();
		pg.Add(LinkRow("power.options"));
		return pg.Done();
	}

	// QA: the plan drop-down's items.
	internal string[] QaChoiceItems(string rowId)
	{
		if (_bindings.TryGetValue(rowId, out KnobBinding? b) && Descendants<ComboBox>(b.Row).FirstOrDefault() is ComboBox c && c.ItemsSource is string[] items)
		{
			return items;
		}
		return Array.Empty<string>();
	}

	// ===== Mouse =====

	private static string OnOff(int? v) => v == null ? "…" : (v.Value != 0 ? "On" : "Off");

	private ScrollViewer MousePage()
	{
		Pg pg = new Pg(this, "mouse");
		pg.Header("Pointer");
		pg.Add(KnobRow("mouse.speed"));
		pg.Add(KnobRow("mouse.precision"));
		pg.Add(KnobRow("mouse.primary"));
		pg.Header("Clicking");
		pg.Add(DoubleClickRow());
		pg.Add(KnobRow("mouse.clicklock"));
		pg.Add(KnobRow("mouse.clicklocktime"));
		pg.Header("Scrolling");
		WheelModeKnob wheel = (WheelModeKnob)HubKnobs.Get("mouse.wheel")!;
		FrameworkElement lines = KnobRow("mouse.lines")!;
		pg.Add(KnobRow("mouse.wheel"));
		pg.Add(lines);
		if (_bindings.TryGetValue("mouse.wheel", out KnobBinding? wb))
		{
			Action<string?> show = wb.Show;
			wb.Show = delegate(string? v)
			{
				show(v);
				lines.Visibility = v == "page" ? Visibility.Collapsed : Visibility.Visible;
			};
			wb.Show(wb.Committed);
		}
		if (_bindings.TryGetValue("mouse.lines", out KnobBinding? lb))
		{
			Action<string?> show = lb.Show;
			lb.Show = delegate(string? v)
			{
				if (int.TryParse(v, out int n) && n > 0)
				{
					wheel.LastLines = n;
				}
				show(v);
			};
		}
		pg.Add(KnobRow("mouse.hchars"));
		pg.Add(LinkRow("mouse.inactive", "Windows Settings › Mouse"));
		pg.Header("Visibility");
		pg.Add(KnobRow("mouse.ctrl"));
		pg.Add(KnobRow("mouse.hidetyping"));
		pg.Add(KnobRow("mouse.trails"));
		pg.Add(KnobRow("mouse.snap"));
		pg.Header("Style");
		pg.Add(DrillRow("mouse.style", PointerChoiceLabel(SettingsStore.FastSnapshot), delegate { Navigate("appearance/pointers", null, push: true); }));
		pg.Header("More");
		pg.Add(LinkRow("mouse.properties"));
		pg.Add(LinkRow("mouse.size"));
		pg.Add(LinkRow("mouse.keys"));
		ResetCard(pg, "mouse", defaults: true);
		return pg.Done();
	}

	// Segments bound to a knob (Left / Right).
	private FrameworkElement NSegmented(string rowId, (string Value, string Label)[] options, string? help = null, string? title = null, HubKnob? knob = null, string? page = null)
	{
		HubKnob k = knob ?? HubKnobs.Get(rowId)!;
		KnobBinding b = Bind(rowId, k, title, page ?? PageOfRow(rowId));
		StackPanel strip = new StackPanel { Orientation = Orientation.Horizontal };
		string group = "nseg-" + rowId + "-" + _buildGen;
		bool quiet = true;
		List<RadioButton> buttons = new List<RadioButton>();
		for (int i = 0; i < options.Length; i++)
		{
			string value = options[i].Value;
			RadioButton rb = new RadioButton
			{
				Style = BareButton,
				GroupName = group,
				Content = options[i].Label,
				MinWidth = 72.0,
				Height = 28.0,
				Template = SegmentTemplate(),
				FontFamily = SegoeUi,
				FontSize = 12.0,
				Margin = new Thickness(i == 0 ? 0.0 : -1.0, 0.0, 0.0, 0.0)
			};
			rb.SetResourceReference(FrameworkElement.FocusVisualStyleProperty, "Metro81.Focus");
			AutomationProperties.SetName(rb, b.Title + ": " + options[i].Label);
			rb.Checked += delegate
			{
				if (!quiet && b.Committed != null)
				{
					Commit(b, value);
				}
			};
			buttons.Add(rb);
			strip.Children.Add(rb);
		}
		Grid row = RowShell(b.Title, null, help, strip, out _);
		row.ColumnDefinitions[1].MaxWidth = 360.0;
		b.Show = delegate(string? v)
		{
			quiet = true;
			for (int i = 0; i < options.Length; i++)
			{
				buttons[i].IsChecked = v != null && k.Same(options[i].Value, v);
			}
			quiet = false;
		};
		b.Enable = delegate(bool e) { strip.IsEnabled = e; };
		FrameworkElement wrap = Wrap(b, row);
		ReadInto(b);
		return wrap;
	}

	// Double-click speed (200..900 ms, the range grows to a value outside it) with a folder to test it on: it opens and
	// closes on a double-click at the speed Windows now uses.
	private FrameworkElement DoubleClickRow(string? page = null)
	{
		FrameworkElement slider = KnobRow("mouse.doubleclick", page)!;
		TextBlock folder = new TextBlock { Text = "", FontFamily = Glyphs, FontSize = 26.0, Foreground = _pal.Link, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
		Button target = new Button
		{
			Style = BareButton,
			Template = _pal.PlainTemplate,
			Width = 40.0,
			Height = 40.0,
			Content = folder,
			Cursor = Cursors.Hand
		};
		target.SetResourceReference(FrameworkElement.FocusVisualStyleProperty, "Metro81.Focus");
		AutomationProperties.SetName(target, "Double-click test folder");
		TextBlock hint = new TextBlock { Text = "Double-click the folder to test", FontFamily = SegoeUi, FontSize = 12.0, Foreground = _pal.Ink2, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10.0, 0.0, 0.0, 0.0) };
		bool open = false;
		target.PreviewMouseLeftButtonDown += delegate(object s, MouseButtonEventArgs e)
		{
			if (e.ClickCount == 2)
			{
				open = !open;
				folder.Text = open ? "" : "";
				hint.Text = open ? "Opened. Double-click again to close" : "Closed. Double-click again to open";
			}
		};
		StackPanel line = new StackPanel { Orientation = Orientation.Horizontal };
		line.Children.Add(target);
		line.Children.Add(hint);
		if (slider is StackPanel wrap)
		{
			Border test = new Border { Padding = new Thickness(12.0, 0.0, 12.0, 8.0), Child = line };
			wrap.Children.Insert(1, test);
		}
		return slider;
	}

	// ===== Keyboard =====

	private TextBox? _repeatTest;

	private ScrollViewer KeyboardPage()
	{
		Pg pg = new Pg(this, "keyboard");
		ISettingsWorld w = SettingsWorld.Current;
		pg.Header("Repeat");
		pg.Add(KnobRow("keyboard.delay"));
		pg.Add(KnobRow("keyboard.rate"));
		_repeatTest = new TextBox { Width = 220.0, Height = 28.0, VerticalContentAlignment = VerticalAlignment.Center, FontSize = 13.5 };
		_repeatTest.SetResourceReference(FrameworkElement.StyleProperty, "Metro81.TextBox");
		_repeatTest.SetResourceReference(FrameworkElement.FocusVisualStyleProperty, "Metro81.Focus");
		Grid test = RowShell("Test", "Hold a key here", null, _repeatTest, out _);
		AutomationProperties.SetName(_repeatTest, "Test the repeat");
		Register("keyboard.test", test);
		pg.Add(test);
		pg.Header("Text cursor");
		Border caret = new Border { Width = 1.0, Height = 18.0, Background = _pal.Ink, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center };
		FrameworkElement width = KnobRow("keyboard.caretwidth")!;
		if (_bindings.TryGetValue("keyboard.caretwidth", out KnobBinding? cb))
		{
			Action<string?> show = cb.Show;
			cb.Show = delegate(string? v)
			{
				show(v);
				if (int.TryParse(v, out int px))
				{
					caret.Width = Math.Clamp(px, 1, 20);
				}
			};
			cb.Show(cb.Committed);
			if (cb.Slider != null)
			{
				cb.Slider.ValueChanged += delegate { caret.Width = Math.Clamp(cb.Slider.Value, 1.0, 20.0); };
			}
		}
		if (width is StackPanel wrap)
		{
			StackPanel preview = new StackPanel { Orientation = Orientation.Horizontal };
			preview.Children.Add(new TextBlock { Text = "Preview", FontFamily = SegoeUi, FontSize = 12.0, Foreground = _pal.Ink2, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0.0, 0.0, 10.0, 0.0) });
			preview.Children.Add(caret);
			wrap.Children.Insert(1, new Border { Padding = new Thickness(12.0, 0.0, 12.0, 10.0), Child = preview });
		}
		pg.Add(width);
		int blink = w.GetCaretBlinkTime();
		pg.Add(ValueLinkRow("keyboard.blink", blink == -1 || blink <= 0 ? "Not blinking" : blink + " ms", "Keyboard Properties › Speed"));
		pg.Header("Input languages");
		FrameworkElement languages = InfoRow("keyboard.languages", null, "…");
		pg.Add(languages);
		ReadAsync(w.ReadKeyboardLayouts, delegate(string[] list)
		{
			SetInfo(languages, list.Length == 1 ? "1 layout" : list.Length + " layouts");
			if (languages is Grid g && FindSecondary(g) is TextBlock line)
			{
				line.Text = list.Length == 0 ? "None found" : string.Join(" · ", list);
			}
		});
		FrameworkElement hotkey = InfoRow("keyboard.switch", null);
		pg.Add(hotkey);
		ReadAsync(w.ReadLayoutHotkey, delegate(string v) { SetInfo(hotkey, v); });
		pg.Add(LinkRow("keyboard.region"));
		pg.Add(LinkRow("keyboard.shortcut", null, new LinkSpec(null, "rundll32.exe", "Shell32.dll,Control_RunDLL input.dll,,{C07337D3-DB2C-4D0B-9A93-B722A6C106E2}{HOTKSET}"), "Change the switch shortcut"));
		pg.Header("Assistance");
		pg.Add(KnobRow("keyboard.access"));
		pg.Add(DrillRow("keyboard.sticky", null, delegate { Navigate("accessibility", "access.sticky", push: true); }));
		pg.Add(ButtonRow("keyboard.osk", "Open", delegate { w.Launch("osk.exe", null, uri: false, elevate: false); }, "Type with the mouse or touch"));
		pg.Header("More");
		pg.Add(LinkRow("keyboard.typing"));
		pg.Add(LinkRow("keyboard.properties"));
		ResetCard(pg, "keyboard", defaults: true);
		PolicyFootnote(pg);
		ScrollViewer sv = pg.Done();
		// the test field is cleared when the page is left
		TextBox field = _repeatTest;
		sv.Unloaded += delegate { field.Text = string.Empty; };
		return sv;
	}

	// ===== Touchpad & pen =====

	private ScrollViewer TouchpadPage()
	{
		Pg pg = new Pg(this, "touchpad");
		pg.Header("Touchpad & pen");
		pg.Add(LinkRow("touchpad.settings"));
		pg.Add(LinkRow("touchpad.pen"));
		pg.Add(InfoRow("touchpad.status", SystemInfo.PenAndTouch, title: "Pen and touch"));
		PolicyFootnote(pg);
		return pg.Done();
	}
}
