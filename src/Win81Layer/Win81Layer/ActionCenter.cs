using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Forms;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Windows.Devices.Radios;
using Windows.UI.Notifications;
using Windows.UI.Notifications.Management;

namespace Win81Layer;

public sealed partial class ActionCenter : Window
{
	private struct POINT
	{
		public int X;

		public int Y;
	}

	private static ActionCenter? _instance;

	private Screen? _screen;

	private bool _dockLeft;

	private double _offScreen;

	// Slide the CONTENT inside the fixed-position window (clipped to its 360px footprint) instead of animating
	// Window.Left across the virtual desktop — the latter made the off-screen start land on the NEIGHBOURING monitor,
	// so opening from the primary looked like it came from the secondary. Mirrors the working CharmsBar slide.
	private readonly TranslateTransform _slide = new TranslateTransform();

	private bool _menuOpen;

	private const double PanelW = 440.0;

	private StackPanel _notifs = null!;

	private TextBlock _notifEmpty = null!;

	private Grid _tiles = null!;

	private Slider? _brightness;

	private Slider? _volume;

	private readonly AudioController _audio = new AudioController();

	private bool _syncingSliders;

	private bool _quiet;

	private double _scale = 1.0;

	private bool _dismissing;

	private double _finalLeft;

	private readonly List<(Border tile, Func<bool> isOn)> _toggleTiles = new List<(Border, Func<bool>)>();

	private static readonly DependencyProperty OnProp;

	private static readonly SolidColorBrush Hover;

	private bool _airplaneOn;

	private bool _btOn;

	private bool _wifiOn;

	[DllImport("user32.dll", CharSet = CharSet.Auto)]
	private static extern nint SendMessageTimeout(nint hWnd, uint Msg, nint wParam, string lParam, uint fuFlags, uint uTimeout, out nint lpdwResult);

	public static void Toggle(Screen? screen = null, string edge = "Bottom")
	{
		ActionCenter instance = _instance;
		if (instance != null && instance.IsVisible)
		{
			_instance.Dismiss();
			return;
		}
		ActionCenter ac = _instance ?? (_instance = new ActionCenter());
		ac._screen = screen;
		ac._dockLeft = edge == "Left";
		ac.ShowPanel();
	}

	internal static void Prewarm()
	{
		try
		{
			_instance ??= new ActionCenter();
		}
		catch (Exception ex)
		{
			Logger.Log("Action Center idle prewarm failed: " + ex.Message);
		}
	}

	private ActionCenter()
	{
		base.WindowStyle = WindowStyle.None;
		base.ResizeMode = ResizeMode.NoResize;
		base.ShowInTaskbar = false;
		base.Topmost = true;
		base.AllowsTransparency = true;   // enables the Win7-Aero acrylic glass backdrop (ShellSkin) to show through
		base.WindowStartupLocation = WindowStartupLocation.Manual;
		base.Background = SettingsPane.PaneBg();
		base.Title = "Action Center";
		base.Deactivated += delegate
		{
			if (!_menuOpen)   // keep the flyout up while its Power sub-menu is open
			{
				Dismiss();
			}
		};
		base.PreviewKeyDown += delegate(object _, System.Windows.Input.KeyEventArgs e)
		{
			//IL_0002: Unknown result type (might be due to invalid IL or missing references)
			//IL_0009: Invalid comparison between Unknown and I4
			if ((int)e.Key == 13 || (int)e.Key == 27)   // Enter or Escape closes the panel
			{
				Dismiss();
			}
		};
		BuildMetroSurface();
	}

	private void ShowPanel()
	{
		ShowMetroPanel();
	}

	private void Dismiss()
	{
		DismissMetroPanel();
	}

	[System.Runtime.InteropServices.DllImport("user32.dll")]
	private static extern bool GetWindowRect(nint hWnd, out RECT lpRect);

	[System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
	private struct RECT
	{
		public int Left;
		public int Top;
		public int Right;
		public int Bottom;
	}

	// Light dismiss: any click OUTSIDE the open panel closes it (x,y are physical pixels from the global mouse hook).
	// This is the reliable path — Deactivated alone doesn't fire consistently for clicks on the shell desktop. Runs on the
	// UI thread (App marshals the hook callback). A click inside the panel, or while the Power sub-menu is up, is ignored.
	internal static void CloseOnOutsideClick(int x, int y)
	{
		ActionCenter ac = _instance;
		// NOTE: intentionally does NOT bail when ac._dismissing - a second outside click while a close is
		// mid-flight (or stuck) must reach Dismiss() so it can force the hide, instead of being swallowed.
		if (ac == null || !ac.IsVisible || ac._menuOpen)
		{
			return;
		}
		try
		{
			nint hwnd = new System.Windows.Interop.WindowInteropHelper(ac).Handle;
			if (hwnd != nint.Zero && GetWindowRect(hwnd, out RECT r) && x >= r.Left && x < r.Right && y >= r.Top && y < r.Bottom)
			{
				return;   // click landed inside the panel — keep it open
			}
			ac.Dismiss();
		}
		catch (Exception ex)
		{
			Logger.Log("Action Center outside-click dismiss failed: " + ex.Message);
		}
	}

	private void PlaceOnEdge()
	{
		// Prefer the monitor passed by the invoking taskbar; fall back to the monitor under the cursor
		// (never blindly PrimaryScreen), matching CharmsBar's target-monitor logic.
		Screen screen = _screen ?? Screen.FromPoint(System.Windows.Forms.Cursor.Position) ?? Screen.PrimaryScreen;
		Rectangle b = TaskbarWorkArea.Current(screen);
		_scale = ScaleFor(b);
		double availableWidth = (double)b.Width / _scale;
		double panelWidth = Math.Min(PanelW, Math.Max(360.0, availableWidth * 0.43));
		panelWidth = Math.Min(panelWidth, availableWidth);
		base.Width = panelWidth;
		base.Height = (double)b.Height / _scale;
		base.Top = (double)b.Top / _scale;
		if (_dockLeft)
		{
			base.Left = (_finalLeft = (double)b.Left / _scale);
			_offScreen = -panelWidth;
		}
		else
		{
			base.Left = (_finalLeft = (double)b.Right / _scale - panelWidth);
			_offScreen = panelWidth;
		}
		ConfigureMetroLayout(panelWidth, base.Height);
	}

	private void BuildBrightness(StackPanel host)
	{
		int cur = SafeGetBrightness();
		if (cur < 0)
		{
			return;
		}
		host.Children.Add(new TextBlock
		{
			Text = "Brightness",
			Foreground = new SolidColorBrush(System.Windows.Media.Color.FromArgb(204, byte.MaxValue, byte.MaxValue, byte.MaxValue)),
			FontFamily = new System.Windows.Media.FontFamily("Segoe UI"),
			FontSize = 12.0,
			Margin = new Thickness(0.0, 0.0, 0.0, 2.0)
		});
		_brightness = new Slider
		{
			Minimum = 0.0,
			Maximum = 100.0,
			Value = cur,
			SmallChange = 5.0,
			LargeChange = 10.0
		};
		_brightness.ValueChanged += delegate(object _, RoutedPropertyChangedEventArgs<double> e)
		{
			if (_syncingSliders)
			{
				return;
			}
			try
			{
				MonitorBrightness.SetThrottled((int)e.NewValue);
				OsdService.ShowBrightness((int)e.NewValue);
			}
			catch
			{
			}
		};
		host.Children.Add(_brightness);
	}

	private void SyncBrightness()
	{
		// The DDC/CI brightness read is a slow I2C round-trip (50-300ms, worse on external monitors). ShowPanel calls
		// this BEFORE Show()+the slide, so reading it synchronously stalls every Action Center open. Read it off the
		// UI thread and settle the slider a beat later — the panel now slides open immediately.
		if (_brightness == null)
		{
			return;
		}
		System.Threading.Tasks.Task.Run(delegate
		{
			int cur = SafeGetBrightness();
			if (cur < 0)
			{
				return;
			}
			((DispatcherObject)this).Dispatcher.BeginInvoke((Delegate)(Action)delegate
			{
				if (_brightness != null)
				{
					_syncingSliders = true;
					_brightness.Value = cur;
					_syncingSliders = false;
				}
			}, Array.Empty<object>());
		});
	}

	private void BuildVolume(StackPanel host)
	{
		float v;
		try
		{
			v = _audio.GetVolume();
		}
		catch
		{
			return;
		}
		host.Children.Add(new TextBlock
		{
			Text = "Volume",
			Foreground = new SolidColorBrush(System.Windows.Media.Color.FromArgb(204, byte.MaxValue, byte.MaxValue, byte.MaxValue)),
			FontFamily = new System.Windows.Media.FontFamily("Segoe UI"),
			FontSize = 12.0,
			Margin = new Thickness(0.0, 10.0, 0.0, 2.0)
		});
		_volume = new Slider
		{
			Minimum = 0.0,
			Maximum = 100.0,
			Value = Math.Round(v * 100f),
			SmallChange = 5.0,
			LargeChange = 10.0
		};
		_volume.ValueChanged += delegate(object _, RoutedPropertyChangedEventArgs<double> e)
		{
			if (_syncingSliders)
			{
				return;
			}
			try
			{
				_audio.SetVolume((float)(e.NewValue / 100.0));
				OsdService.ShowVolume((int)e.NewValue, muted: false);
			}
			catch
			{
			}
		};
		host.Children.Add(_volume);
	}

	private void SyncVolume()
	{
		if (_volume != null)
		{
			try
			{
				_syncingSliders = true;
				_volume.Value = Math.Round(_audio.GetVolume() * 100f);
				_syncingSliders = false;
			}
			catch
			{
				_syncingSliders = false;
			}
		}
	}

	private static int SafeGetBrightness()
	{
		try
		{
			return MonitorBrightness.Get();
		}
		catch
		{
			return -1;
		}
	}

	private void BuildTiles()
	{
		BuildMetroTiles();
	}

	private Border QuickTile(int glyph, string label, Func<Task> click, bool toggle)
	{
		StackPanel host = new StackPanel
		{
			Orientation = System.Windows.Controls.Orientation.Vertical,
			HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
			VerticalAlignment = VerticalAlignment.Center
		};
		host.Children.Add(new TextBlock
		{
			Text = char.ConvertFromUtf32(glyph),
			FontFamily = new System.Windows.Media.FontFamily("Segoe MDL2 Assets"),
			FontSize = 22.0,
			Foreground = System.Windows.Media.Brushes.White,
			HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
			Margin = new Thickness(0.0, 0.0, 0.0, 4.0)
		});
		host.Children.Add(new TextBlock
		{
			Text = label,
			FontFamily = new System.Windows.Media.FontFamily("Segoe UI"),
			FontSize = 11.0,
			Foreground = new SolidColorBrush(System.Windows.Media.Color.FromArgb(221, byte.MaxValue, byte.MaxValue, byte.MaxValue)),
			TextAlignment = TextAlignment.Center,
			TextWrapping = TextWrapping.Wrap,
			HorizontalAlignment = System.Windows.HorizontalAlignment.Center
		});
		Border b = new Border
		{
			Height = 74.0,
			Margin = new Thickness(4.0),
			CornerRadius = new CornerRadius(2.0),
			Cursor = System.Windows.Input.Cursors.Hand,
			Background = TileBrush(on: false),
			Child = host,
			Tag = toggle
		};
		b.MouseEnter += delegate
		{
			if (!IsOn((DependencyObject)(object)b))
			{
				b.Background = Hover;
			}
		};
		b.MouseLeave += delegate
		{
			b.Background = TileBrush(IsOn((DependencyObject)(object)b));
		};
		b.MouseLeftButtonUp += async delegate
		{
			if (!b.IsHitTestVisible)
			{
				return;
			}
			b.IsHitTestVisible = false;
			b.Opacity = 0.72;
			try
			{
				await click();
			}
			catch (Exception ex)
			{
				Logger.Log("Action Center '" + label.Replace("\n", " ") + "' failed: " + ex.Message);
				ToastService.Show(null, "Action center", label.Replace("\n", " "), "Windows could not complete this action. The corresponding Settings page will be used when available.");
			}
			finally
			{
				b.Opacity = 1.0;
				b.IsHitTestVisible = true;
				RefreshTileStates();
			}
		};
		return b;
	}

	private void RefreshTileStates()
	{
		RefreshMetroTileStates(animate: false);
	}

	private static bool IsOn(DependencyObject d)
	{
		return (bool)d.GetValue(OnProp);
	}

	private static SolidColorBrush TileBrush(bool on)
	{
		if (on)
		{
			return Frozen(StartAccent.Color());
		}
		// Flat skin: slightly more opaque off-cells so they read as solid tiles; glass: fainter over the translucent pane.
		byte off = (byte)(ShellSkin.GlassOn ? 24 : 40);
		return Frozen(System.Windows.Media.Color.FromArgb(off, byte.MaxValue, byte.MaxValue, byte.MaxValue));
	}

	private void ToggleQuiet()
	{
		_quiet = !_quiet;
		try
		{
			if (_quiet)
			{
				NativeBanner.Suppress();
			}
			else
			{
				NativeBanner.Restore();
			}
		}
		catch
		{
		}
	}

	private async Task ToggleAirplane()
	{
		try
		{
			if (await Radio.RequestAccessAsync() != RadioAccessStatus.Allowed)
			{
				await OpenExternal("Airplane mode", AirplaneSettingsTarget);
				return;
			}
			IReadOnlyList<Radio> radios = await Radio.GetRadiosAsync();
			if (radios.Count == 0)
			{
				await OpenExternal("Airplane mode", AirplaneSettingsTarget);
				return;
			}
			bool anyOn = radios.Any((Radio x) => x.State == RadioState.On);
			bool accepted = true;
			foreach (Radio radio in radios)
			{
				try
				{
					accepted &= await radio.SetStateAsync((!anyOn) ? RadioState.On : RadioState.Off) == RadioAccessStatus.Allowed;
				}
				catch
				{
					accepted = false;
				}
			}
			await Task.Delay(120);
			await RefreshRadioStates();
			if (!accepted)
			{
				await OpenExternal("Airplane mode", AirplaneSettingsTarget);
			}
		}
		catch (Exception ex)
		{
			Logger.Log("Airplane mode direct toggle failed: " + ex.Message);
			await OpenExternal("Airplane mode", AirplaneSettingsTarget);
		}
	}

	private async Task ToggleBluetooth()
	{
		try
		{
			if (await Radio.RequestAccessAsync() != RadioAccessStatus.Allowed)
			{
				await OpenExternal("Bluetooth", BluetoothSettingsTarget);
				return;
			}
			Radio bt = (await Radio.GetRadiosAsync()).FirstOrDefault((Radio x) => x.Kind == RadioKind.Bluetooth);
			if ((object)bt == null)
			{
				await OpenExternal("Bluetooth", BluetoothSettingsTarget);
				return;
			}
			RadioAccessStatus accepted = await bt.SetStateAsync((bt.State != RadioState.On) ? RadioState.On : RadioState.Off);
			await Task.Delay(120);
			await RefreshRadioStates();
			if (accepted != RadioAccessStatus.Allowed)
			{
				await OpenExternal("Bluetooth", BluetoothSettingsTarget);
			}
		}
		catch (Exception ex)
		{
			Logger.Log("Bluetooth direct toggle failed: " + ex.Message);
			await OpenExternal("Bluetooth", BluetoothSettingsTarget);
		}
	}

	private async Task RefreshRadioStates()
	{
		try
		{
			if (await Radio.RequestAccessAsync() == RadioAccessStatus.Allowed)
			{
				IReadOnlyList<Radio> radios = await Radio.GetRadiosAsync();
				_airplaneOn = radios.Count > 0 && radios.All((Radio x) => x.State != RadioState.On);
				Radio bt = radios.FirstOrDefault((Radio x) => x.Kind == RadioKind.Bluetooth);
				_btOn = (object)bt != null && bt.State == RadioState.On;
				Radio wifi = radios.FirstOrDefault((Radio x) => x.Kind == RadioKind.WiFi);
				_wifiOn = (object)wifi != null && wifi.State == RadioState.On;
				((DispatcherObject)this).Dispatcher.Invoke((Action)delegate
				{
					_wifiStatus = _wifiOn ? _wifiStatus : "Off";
					_bluetoothStatus = _btOn ? "On" : "Off";
					_airplaneStatus = _airplaneOn ? "On" : "Off";
					RefreshMetroTileStates(animate: true);
				});
			}
		}
		catch
		{
		}
	}

	private async Task ToggleWifi()
	{
		try
		{
			if (await Radio.RequestAccessAsync() != RadioAccessStatus.Allowed)
			{
				await OpenExternal("Wi-Fi", WifiSettingsTarget, NetworkStatusTarget);
				return;
			}
			Radio wifi = (await Radio.GetRadiosAsync()).FirstOrDefault((Radio x) => x.Kind == RadioKind.WiFi);
			if ((object)wifi == null)
			{
				await OpenExternal("Wi-Fi", WifiSettingsTarget, NetworkStatusTarget);
				return;
			}
			RadioAccessStatus accepted = await wifi.SetStateAsync((wifi.State != RadioState.On) ? RadioState.On : RadioState.Off);
			await Task.Delay(120);
			await RefreshRadioStates();
			if (accepted != RadioAccessStatus.Allowed)
			{
				await OpenExternal("Wi-Fi", WifiSettingsTarget, NetworkStatusTarget);
			}
		}
		catch (Exception ex)
		{
			Logger.Log("Wi-Fi direct toggle failed: " + ex.Message);
			await OpenExternal("Wi-Fi", WifiSettingsTarget, NetworkStatusTarget);
		}
	}

	// Dark/Light system theme — documented HKCU keys + WM_SETTINGCHANGE broadcast so Explorer/UWP repaint live.
	private static bool ThemeIsDark()
	{
		return ShellTheme.IsDark;   // single source of truth (cached + change-signalled)
	}

	private void ToggleTheme()
	{
		// Flip through the single authority: it writes the registry, broadcasts to Explorer/UWP, rebuilds the menu
		// palette and repaints the launcher's own open surfaces live (via ApplyResourceTokens + the Changed event).
		ShellTheme.Toggle();
	}

	private static Task RunSync(Action action)
	{
		action();
		return Task.CompletedTask;
	}

	private async Task OpenProject()
	{
		string displaySwitch = System.IO.Path.Combine(Environment.SystemDirectory, "DisplaySwitch.exe");
		if (!TryStart(displaySwitch))
		{
			await OpenExternal("Project", DisplaySettingsTarget, ProjectSettingsTarget);
			return;
		}
		Logger.Log("Action Center 'Project': DisplaySwitch.exe opened");
		Dismiss();
	}

	private async Task OpenConnect()
	{
		// Win+K is the supported Windows surface for wireless displays/audio. Hide our topmost pane first so the
		// system flyout can own foreground focus; the native Start/Search hosts remain suspended and cannot interfere.
		// HardHide (not a raw Hide) so refresh timers stop and the open/close state stays in sync with visibility.
		HardHide();
		await Task.Delay(40);
		try
		{
			Keystroke.Chord(91, 75);
			Logger.Log("Action Center 'Connect': Win+K dispatched");
		}
		catch (Exception ex)
		{
			Logger.Log("Connect flyout failed: " + ex.Message);
			await OpenExternal("Connect", ConnectedDevicesTarget, ProjectSettingsTarget);
		}
	}

	private async Task OpenExternal(string label, string primary, string? fallback = null)
	{
		bool opened = TryStart(primary);
		if (!opened && !string.IsNullOrWhiteSpace(fallback))
		{
			opened = TryStart(fallback);
		}
		Logger.Log("Action Center '" + label + "': " + (opened ? "opened" : "failed") + " (" + primary + ")");
		if (opened)
		{
			Dismiss();
		}
		else
		{
			ToastService.Show(null, "Action center", label, "Windows could not open the corresponding system control.");
		}
		await Task.CompletedTask;
	}

	private static bool TryStart(string target)
	{
		try
		{
			ShellLaunch.AllowForeground();
			Process.Start(CreateExternalStartInfo(target));
			return true;
		}
		catch (Exception ex)
		{
			Logger.Log("Action Center launch '" + target + "' failed: " + ex.Message);
			return false;
		}
	}

	private static ProcessStartInfo CreateExternalStartInfo(string target)
	{
		return new ProcessStartInfo(target)
		{
			UseShellExecute = true
		};
	}

	private static void BroadcastSettingChange(string section)
	{
		// off the UI thread: a hung top-level window can make the broadcast block up to ~200ms
		Task.Run(delegate
		{
			try
			{
				SendMessageTimeout((nint)65535, 26u, (nint)0, section, 2u, 200u, out var _);
			}
			catch
			{
			}
		});
	}

	private async void LoadNotifications()
	{
		try
		{
			UserNotificationListener listener = UserNotificationListener.Current;
			if (await listener.RequestAccessAsync() != UserNotificationListenerAccessStatus.Allowed)
			{
				ShowNotifs(null);
				return;
			}
			IReadOnlyList<UserNotification> list = await listener.GetNotificationsAsync(NotificationKinds.Toast);
			List<(uint id, string app, string title, string body, string aumid, ImageSource? icon)> rows = new List<(uint, string, string, string, string, ImageSource?)>();
			foreach (UserNotification n in list)
			{
				string app = "";
				string aumid = "";
				try
				{
					app = n.AppInfo?.DisplayInfo?.DisplayName ?? "";
					aumid = n.AppInfo?.AppUserModelId ?? "";
				}
				catch
				{
				}
				string title = "";
				string body = "";
				try
				{
					IReadOnlyList<AdaptiveNotificationText> texts = n.Notification.Visual.GetBinding(KnownNotificationBindings.ToastGeneric)?.GetTextElements();
					if (texts != null && texts.Count > 0)
					{
						title = texts[0].Text;
						if (texts.Count > 1)
						{
							body = texts[1].Text;
						}
					}
				}
				catch
				{
				}
				rows.Add((n.Id, app, title, body, aumid, ResolveNotifIcon(aumid)));
			}
			((DispatcherObject)this).Dispatcher.Invoke((Action)delegate
			{
				ShowNotifs(rows);
			});
		}
		catch
		{
			((DispatcherObject)this).Dispatcher.Invoke((Action)delegate
			{
				ShowNotifs(null);
			});
		}
	}

	private static ImageSource? ResolveNotifIcon(string aumid)
	{
		if (string.IsNullOrEmpty(aumid))
		{
			return null;
		}
		try
		{
			ImageSource ov = AppIconOverrides.ResolveByAumid(aumid);
			if (ov != null)
			{
				return ov;
			}
			return AppInventory.LoadIcon("shell:AppsFolder\\" + aumid);
		}
		catch
		{
			return null;
		}
	}

	private void ShowNotifs(List<(uint id, string app, string title, string body, string aumid, ImageSource? icon)>? rows)
	{
		ShowMetroNotifications(rows);
	}

	private async void DismissOne(uint id, Border card)
	{
		await DismissMetroNotificationAsync(id, card);
	}

	private async void ClearAllNotifications()
	{
		await ClearMetroNotificationsAsync();
	}

	// Power tile → a small boot/shutdown menu (Sign out / Restart / Shut down / Advanced startup) styled like the
	// rest of the shell, instead of opening the Power Options (power PLANS) control panel. Advanced startup reboots
	// into the Windows recovery / boot-options page (UEFI firmware, startup settings, …). The _menuOpen guard keeps
	// the flyout from dismissing itself while the sub-menu takes activation.
	private void ShowPowerMenu()
	{
		System.Windows.Controls.ContextMenu ctx = new System.Windows.Controls.ContextMenu
		{
			Style = (Style)System.Windows.Application.Current.Resources["Win81ContextMenu"]
		};
		TaskbarContextMenu.ApplyTheme(ctx);
		// NOTE: ApplyTheme sets a MenuItem-only item-container style, so a raw Separator throws
		// ("A style intended for type 'MenuItem' cannot be applied to type 'Separator'"). Use MenuItems only.
		ctx.Items.Add(TaskbarContextMenu.Leaf("Sign out", delegate { PowerActions.SignOut(); }));
		ctx.Items.Add(TaskbarContextMenu.Leaf("Restart", delegate { PowerActions.Restart(); }));
		ctx.Items.Add(TaskbarContextMenu.Leaf("Shut down", delegate { PowerActions.ShutDown(); }));
		ctx.Items.Add(TaskbarContextMenu.Leaf("Advanced startup", delegate { PowerActions.AdvancedStartup(); }));
		ctx.PlacementTarget = _tiles;   // anchor the popup to THIS window's presentation source
		ctx.Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint;
		ctx.Closed += delegate
		{
			_menuOpen = false;
			base.Topmost = true;
			Dismiss();
		};
		_menuOpen = true;
		base.Topmost = false;   // let the menu popup render above this (otherwise a Topmost window can cover it)
		ctx.IsOpen = true;
	}

	private static void Launch(string cmd)
	{
		try
		{
			CharmListPane.Launch(cmd);
		}
		catch
		{
		}
	}

	private static System.Windows.Controls.Button LinkButton(string text)
	{
		System.Windows.Controls.Button b = new System.Windows.Controls.Button
		{
			Content = text,
			Cursor = System.Windows.Input.Cursors.Hand,
			FocusVisualStyle = null,
			Background = System.Windows.Media.Brushes.Transparent,
			BorderThickness = new Thickness(0.0),
			FontFamily = new System.Windows.Media.FontFamily("Segoe UI"),
			FontSize = 12.0
		};
		b.SetResourceReference(System.Windows.Controls.Control.ForegroundProperty, "MenuAccentLight");   // pattern 5: hyperlinks carry the accent, live
		b.Template = LinkTemplate();
		return b;
	}

	private static ControlTemplate LinkTemplate()
	{
		ControlTemplate t = new ControlTemplate(typeof(System.Windows.Controls.Button));
		FrameworkElementFactory cp = new FrameworkElementFactory(typeof(ContentPresenter));
		cp.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
		t.VisualTree = cp;
		return t;
	}

	private static SolidColorBrush Frozen(System.Windows.Media.Color c)
	{
		SolidColorBrush b = new SolidColorBrush(c);
		((Freezable)b).Freeze();
		return b;
	}

	private double ScaleFor(Rectangle b)
	{
		try
		{
			POINT c = new POINT
			{
				X = b.Left + b.Width / 2,
				Y = b.Top + b.Height / 2
			};
			nint mon = MonitorFromPoint(c, 2u);
			if (mon != IntPtr.Zero && GetDpiForMonitor(mon, 0u, out var dx, out var _) == 0 && dx != 0)
			{
				return (double)dx / 96.0;
			}
		}
		catch
		{
		}
		return 1.0;
	}

	internal static int QaRenderPanel(string outPath, double width = 468.0, double height = 900.0)
	{
		return QaRenderMetro(outPath, Math.Max(width, 420.0), height, compact: height < 800.0, withNotifications: false).DefinedTiles;
	}

	[DllImport("user32.dll")]
	private static extern nint MonitorFromPoint(POINT p, uint flags);

	[DllImport("shcore.dll")]
	private static extern int GetDpiForMonitor(nint mon, uint type, out uint dx, out uint dy);

	static ActionCenter()
	{
		//IL_001f: Unknown result type (might be due to invalid IL or missing references)
		//IL_0029: Expected O, but got Unknown
		OnProp = DependencyProperty.RegisterAttached("AcOn", typeof(bool), typeof(ActionCenter), new PropertyMetadata((object)false));
		Hover = Frozen(System.Windows.Media.Color.FromArgb(42, byte.MaxValue, byte.MaxValue, byte.MaxValue));
	}
}
