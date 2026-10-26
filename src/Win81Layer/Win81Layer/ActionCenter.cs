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
using System.Windows.Threading;
using Windows.Devices.Radios;

namespace Win81Layer;

// The Action Center: a compact Quick settings panel (toggles, volume and brightness, All settings and Power) anchored at
// the tray end of the taskbar it was opened from. Notifications are entirely Windows' own: this panel never reads, lists,
// counts or dismisses them (the Notifications tile opens Windows' own notification center).
public sealed partial class ActionCenter : Window
{
	private static ActionCenter? _instance;

	private Screen? _screen;

	// The edge of the taskbar the panel opens from ("Bottom", "Top", "Left", "Right") and that bar's physical rect when
	// known (null: the work area edge is the anchor).
	private string _edge = "Bottom";

	private Rectangle? _barPx;

	// "taskbar" or "Win+A", for the show log line.
	private string _via = "taskbar";

	// The content glides inside the fixed-position window (clipped to the frame) instead of animating Window.Left/Top
	// across the virtual desktop, so a panel never appears to come from a neighbouring monitor.
	private readonly TranslateTransform _slide = new TranslateTransform();

	private bool _menuOpen;

	private Slider? _brightness;

	private Slider? _volume;

	private readonly AudioController _audio = new AudioController();

	private bool _syncingSliders;

	private double _scale = 1.0;

	private bool _dismissing;

	// True only while ShowMetroPanel is inside its own Show()+ForceForeground (see there). A Deactivated raised in that window is
	// our own foreground hand-over bouncing, never the user leaving the panel.
	private bool _showing;

	// Monotonic ms of the last ShowMetroPanel. The light-dismiss hook marshals the OPENING click's mouse-DOWN through
	// the threadpool + dispatcher; on a cold/idle threadpool (first open, or after long idle) that hop can deliver the
	// down-event AFTER the panel is visible, so the click that OPENED the panel reads as an outside click and closes it
	// on the first tap. Outside clicks within a short grace window after a show are ignored (native flyout behaviour).
	private long _shownTick;

	// Stopwatch timestamp of the same show, for the open latency in the log (TickCount64 is quantized to ~15.6 ms).
	private long _shownStamp;

	private bool _airplaneOn;

	private bool _btOn;

	private bool _wifiOn;

	[DllImport("user32.dll")]
	private static extern int GetMessageTime();

	// This mouse's switch chatter turns one press into two Clicks 33-53ms apart (log: identical taskbar Click pairs, then a clean
	// gap to >=150ms). Unguarded, the twin closes the panel its first half just opened (Motion Off: re-opens one just closed).
	// Compared on the presses' own message times, so a UI-thread stall that delivers both back to back can neither fake nor hide
	// the gap. Same idea as App.ToggleStart's debounce, but sized to the measured chatter so a deliberate second press closes.
	private const int DuplicatePressMs = 120;

	private static int _lastTogglePressTime;

	private static bool _hasToggled;

	// screen/edge/barPx: the monitor and taskbar the panel opens from (the taskbar button passes its own; Win+A passes the
	// bar on the monitor under the cursor). fromKeyboard only labels the show log line.
	public static void Toggle(Screen? screen = null, string edge = "Bottom", Rectangle? barPx = null, bool fromKeyboard = false)
	{
		ShellActivity.Stamp();
		// Called synchronously from the taskbar button's Click this is the WM_LBUTTONUP's own time; from Win+A, the dispatch time.
		int pressTime = GetMessageTime();
		int sinceLast = unchecked(pressTime - _lastTogglePressTime);
		if (_hasToggled && sinceLast >= 0 && sinceLast < DuplicatePressMs)
		{
			Logger.Log($"Action Center: toggle ignored (duplicate press {sinceLast}ms after the previous one)");
			return;
		}
		_hasToggled = true;
		_lastTogglePressTime = pressTime;
		ActionCenter instance = _instance;
		if (instance != null && instance.IsVisible && !instance._dismissing)   // mid-close counts as closed: re-open it
		{
			_instance.Dismiss("toggle");
			return;
		}
		ActionCenter ac = _instance ?? (_instance = new ActionCenter());
		ac._screen = screen;
		ac._edge = NormalizeEdge(edge);
		ac._barPx = barPx is { IsEmpty: false } ? barPx : null;
		ac._via = fromKeyboard || screen == null ? "Win+A" : "taskbar";
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
		// The Metro surface is always opaque (ApplyMetroPalette), so a plain HWND: a layered one cost a GPU->CPU readback of
		// the whole panel on every animation/scroll frame.
		base.AllowsTransparency = false;
		base.WindowStartupLocation = WindowStartupLocation.Manual;
		base.Background = SettingsPane.PaneBg();
		base.Title = "Action Center";
		base.Deactivated += delegate
		{
			if (_menuOpen)   // keep the panel up while its Power sub-menu is open
			{
				return;
			}
			if (_showing)
			{
				// Self-inflicted bounce inside our own Show()+ForceForeground (see ShowMetroPanel). Dismissing here latched
				// _dismissing mid-show and the 800ms fallback then hid the panel that had just opened.
				Logger.Log("Action Center: deactivate ignored (show in progress)");
				return;
			}
			// Decide on the next dispatcher turn, after any re-activation already sent to us has been handled: a bounce that
			// leaves us active again is ignored, a real switch to another window still light-dismisses.
			Dispatcher.BeginInvoke(DispatcherPriority.Input, (Action)delegate
			{
				if (IsVisible && !IsActive && !_menuOpen && !_dismissing)
				{
					Dismiss("Deactivated fg=" + ForegroundDescription());
				}
			});
		};
		base.PreviewKeyDown += delegate(object _, System.Windows.Input.KeyEventArgs e)
		{
			// Escape closes the panel; Enter closes it too unless it activates the focused tile or footer button.
			if (e.Key == Key.Escape || (e.Key == Key.Enter && !IsActionableFocus()))
			{
				Dismiss("key " + e.Key);
			}
		};
		base.SourceInitialized += delegate
		{
			ConfigureNativeSurface();
		};
		BuildMetroSurface();
	}

	private void ShowPanel()
	{
		ShowMetroPanel();
	}

	private void Dismiss(string cause = "action")
	{
		DismissMetroPanel(cause);
	}

	[DllImport("user32.dll")]
	private static extern nint GetForegroundWindow();

	[DllImport("user32.dll")]
	private static extern uint GetWindowThreadProcessId(nint hWnd, out uint pid);

	[DllImport("user32.dll", CharSet = CharSet.Unicode)]
	private static extern int GetClassName(nint hWnd, System.Text.StringBuilder className, int maxCount);

	// "WindowClass/process" of the current foreground window, for the show/hide log lines (runs only on show and dismiss).
	private static string ForegroundDescription()
	{
		try
		{
			nint fg = GetForegroundWindow();
			if (fg == IntPtr.Zero)
			{
				return "none";
			}
			System.Text.StringBuilder cls = new System.Text.StringBuilder(64);
			GetClassName(fg, cls, cls.Capacity);
			GetWindowThreadProcessId(fg, out uint pid);
			if (pid == (uint)Environment.ProcessId)
			{
				return cls + "/self";
			}
			using System.Diagnostics.Process proc = System.Diagnostics.Process.GetProcessById((int)pid);
			return cls + "/" + proc.ProcessName;
		}
		catch
		{
			return "?";
		}
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

	private const int DWMWA_TRANSITIONS_FORCEDISABLED = 3;

	private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;

	private const int DWMWA_BORDER_COLOR = 34;

	private const int DWMWCP_DONOTROUND = 1;

	private const int DWM_COLOR_NONE = -2;

	private const int GWL_STYLE = -16;

	private const int NATIVE_FRAME_STYLES = 0x00C40000;   // WS_CAPTION | WS_THICKFRAME

	private const uint SWP_FRAME_REFRESH = 0x0037u;

	private const uint SWP_NOZORDER_NOACTIVATE = 0x0014u;

	// Same treatment as AppSwitcher.ConfigureNativeSurface: as a non-layered HWND the panel would otherwise pick up the
	// Win11 DWM border, rounded corners and show/hide fade. Affects only this launcher-owned HWND.
	private void ConfigureNativeSurface()
	{
		nint hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
		if (hwnd == IntPtr.Zero)
		{
			return;
		}
		try
		{
			int style = GetWindowLong(hwnd, GWL_STYLE);
			int framelessStyle = style & ~NATIVE_FRAME_STYLES;
			if (style != framelessStyle)
			{
				SetWindowLong(hwnd, GWL_STYLE, framelessStyle);
				SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0, SWP_FRAME_REFRESH);
			}
			int transitionsDisabled = 1;
			DwmSetWindowAttribute(hwnd, DWMWA_TRANSITIONS_FORCEDISABLED, ref transitionsDisabled, sizeof(int));
			int cornerPreference = DWMWCP_DONOTROUND;
			DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref cornerPreference, sizeof(int));
			int borderColor = DWM_COLOR_NONE;
			DwmSetWindowAttribute(hwnd, DWMWA_BORDER_COLOR, ref borderColor, sizeof(int));
		}
		catch
		{
			// Older Windows 10 builds can reject the Win11-only color/corner attributes.
		}
		SyncNativeClearColor();
	}

	// The render target clears to the panel's body colour (its first stop), so a frame shown before WPF's first render is
	// never black.
	private void SyncNativeClearColor()
	{
		try
		{
			nint hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
			if (hwnd == IntPtr.Zero || _themeBackground == null || _themeBackground.GradientStops.Count == 0)
			{
				return;
			}
			if (System.Windows.Interop.HwndSource.FromHwnd(hwnd)?.CompositionTarget is System.Windows.Interop.HwndTarget target)
			{
				System.Windows.Media.Color top = _themeBackground.GradientStops[0].Color;
				target.BackgroundColor = System.Windows.Media.Color.FromRgb(top.R, top.G, top.B);
			}
		}
		catch
		{
		}
	}

	[DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
	private static extern int GetWindowLong(nint hwnd, int index);

	[DllImport("user32.dll", EntryPoint = "SetWindowLongW")]
	private static extern int SetWindowLong(nint hwnd, int index, int value);

	[DllImport("user32.dll", SetLastError = true)]
	private static extern bool SetWindowPos(nint hwnd, nint insertAfter, int x, int y, int width, int height, uint flags);

	[DllImport("dwmapi.dll")]
	private static extern int DwmSetWindowAttribute(nint hwnd, int attribute, ref int value, int valueSize);

	// Light dismiss: any click OUTSIDE the open panel closes it (x,y are physical pixels from the global mouse hook).
	// This is the reliable path — Deactivated alone doesn't fire consistently for clicks on the shell desktop. Runs on the
	// UI thread (App marshals the hook callback). A click inside the panel, or while the Power sub-menu is up, is ignored.
	// Set once by App: true when a physical screen point lies on ANY taskbar's Action Center button. Presses on that button
	// belong to Toggle (its mouse-up Click), never to light-dismiss: otherwise the opening press, delivered late through the
	// threadpool + dispatcher, reads as an outside click and closes the panel it just opened - and, with Motion Off or a long
	// press, a closing press hides on mouse-down and Toggle then re-opens on mouse-up.
	internal static Func<int, int, bool>? IsToggleButtonAt;

	// downTime = the hook event's own timestamp (MSLLHOOKSTRUCT.time, GetTickCount clock), not when it reached this thread.
	internal static void CloseOnOutsideClick(int x, int y, int downTime, bool leftButton)
	{
		ActionCenter ac = _instance;
		// NOTE: intentionally does NOT bail when ac._dismissing - a second outside click while a close is
		// mid-flight (or stuck) must reach Dismiss() so it can force the hide, instead of being swallowed.
		if (ac == null || !ac.IsVisible || ac._menuOpen)
		{
			return;
		}
		// A press that physically happened before this show can never dismiss it, however late the hop delivered it.
		// (int)TickCount64 is GetTickCount, the same clock as the hook timestamp; unchecked handles the 49.7-day wrap.
		if (unchecked(downTime - (int)ac._shownTick) <= 0)
		{
			return;
		}
		if (System.Environment.TickCount64 - ac._shownTick < 350L)
		{
			return;   // backstop grace from 2026-10-01; the timestamp check above is the deterministic guard
		}
		if (leftButton && IsToggleButtonAt != null && IsToggleButtonAt(x, y))   // only a LEFT press becomes Toggle's Click
		{
			return;   // the Action Center button itself: Toggle owns it
		}
		try
		{
			nint hwnd = new System.Windows.Interop.WindowInteropHelper(ac).Handle;
			if (hwnd != nint.Zero && GetWindowRect(hwnd, out RECT r) && x >= r.Left && x < r.Right && y >= r.Top && y < r.Bottom)
			{
				return;   // click landed inside the panel — keep it open
			}
			ac.Dismiss($"outside-click at ({x},{y}) lag={unchecked(System.Environment.TickCount - downTime)}ms");
		}
		catch (Exception ex)
		{
			Logger.Log("Action Center outside-click dismiss failed: " + ex.Message);
		}
	}

	// Sizes the panel to its content and anchors it at the tray end of the taskbar it opens from (PlacePanel), on the monitor
	// passed by the invoking taskbar (or the one under the cursor; never blindly the primary).
	private void PlaceOnEdge()
	{
		Screen screen = _screen ?? Screen.FromPoint(System.Windows.Forms.Cursor.Position) ?? Screen.PrimaryScreen;
		Rectangle work = TaskbarWorkArea.Current(screen);
		if (work.IsEmpty)
		{
			work = screen.WorkingArea;
		}
		_scale = MonitorDpi.ScaleFor(work);
		double width = PanelWidthFor(work.Width / _scale);
		ActionCenterPlacement probe = PlacePanel(work, _barPx, _edge, _scale, width, 1.0, screen.Bounds);
		double height = LayoutPanel(width, probe.MaxHeightPx / _scale);
		ActionCenterPlacement p = PlacePanel(work, _barPx, _edge, _scale, width, height, screen.Bounds);
		_toward = p.Toward;
		_placedPx = p.Px;
		_openDevice = screen.DeviceName;
		if (base.IsVisible)
		{
			// Already on screen (a re-placement after the state snapshot): top and height change together.
			ApplyPlacementPx();
			return;
		}
		// Hidden: WPF converts these with the DPI of the monitor the window was last on; ShowMetroPanel places the shown
		// window again in physical pixels (ApplyPlacementPx), which corrects a monitor of another DPI.
		base.Width = p.Px.Width / _scale;
		base.Height = p.Px.Height / _scale;
		base.Left = p.Px.Left / _scale;
		base.Top = p.Px.Top / _scale;
	}

	// Moves and sizes the visible HWND to the physical placement in one call, then lets WPF's values follow with the DPI WPF
	// now uses for this window (after a move to a monitor of another DPI, WM_DPICHANGED has updated it inside that call).
	private void ApplyPlacementPx()
	{
		nint hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
		Rectangle px = _placedPx;
		if (hwnd == nint.Zero || px.Width <= 0 || px.Height <= 0)
		{
			return;
		}
		SetWindowPos(hwnd, IntPtr.Zero, px.Left, px.Top, px.Width, px.Height, SWP_NOZORDER_NOACTIVATE);
		double s = _scale;
		try
		{
			double wpf = VisualTreeHelper.GetDpi(this).DpiScaleX;
			if (wpf > 0.0 && !double.IsNaN(wpf))
			{
				s = wpf;
			}
		}
		catch
		{
		}
		base.Width = px.Width / s;
		base.Height = px.Height / s;
		base.Left = px.Left / s;
		base.Top = px.Top / s;
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
		if (QaChords == null)
		{
			await Task.Delay(40);
		}
		if (SendChord(91, 75))
		{
			Logger.Log("Action Center 'Connect': Win+K dispatched");
			return;
		}
		Logger.Log("Action Center 'Connect': Win+K could not be sent; opening Settings instead");
		await OpenExternal("Connect", ConnectedDevicesTarget, ProjectSettingsTarget);
	}

	// The Notifications tile: Windows' own notification center (Win+N), exactly like Connect hands over to Win+K. The
	// launcher shows no notifications of its own.
	private async Task OpenNotificationCenter()
	{
		HardHide();
		if (QaChords == null)
		{
			await Task.Delay(40);
		}
		if (SendChord(91, 78))
		{
			Logger.Log("Action Center 'notifications': Win+N dispatched (native notification center)");
			return;
		}
		Logger.Log("Action Center 'notifications': Win+N could not be sent; opening Settings > Notifications instead");
		await OpenExternal("Notifications", NotificationsPageTarget);
	}

	// QA seams: when QaChords is set, a chord is recorded there instead of being sent as real input (QaChordFails makes it
	// report a failure); when QaLaunches is set, an external target is recorded there instead of being opened.
	internal List<byte[]>? QaChords;

	internal bool QaChordFails;

	internal List<string>? QaLaunches;

	private bool SendChord(params byte[] vks)
	{
		if (QaChords != null)
		{
			QaChords.Add(vks);
			return !QaChordFails;
		}
		return Keystroke.Chord(vks);
	}

	private async Task OpenExternal(string label, string primary, string? fallback = null)
	{
		if (QaLaunches != null)
		{
			QaLaunches.Add(primary);
			return;
		}
		// Settings activation can block for over a second, so launch on a ShellLaunch worker and let the close
		// animation run meanwhile. A failure is logged (Windows' own surfaces carry any user-facing message).
		ShellLaunch.AllowForeground();
		Dismiss();
		ShellLaunch.Run(delegate
		{
			bool opened = TryStart(primary) || (!string.IsNullOrWhiteSpace(fallback) && TryStart(fallback));
			Logger.Log("Action Center '" + label + "': " + (opened ? "opened" : "failed") + " (" + primary + ")");
		});
		await Task.CompletedTask;
	}

	private static bool TryStart(string target)
	{
		try
		{
			ShellLaunch.AllowForeground();
			Process.Start(CreateExternalStartInfo(target))?.Dispose();
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

	// Power (footer) → a small boot/shutdown menu (Sign out / Sleep / Restart / Shut down / Advanced startup) styled like
	// the rest of the shell, instead of opening the Power Options (power PLANS) control panel. Advanced startup reboots
	// into the Windows recovery / boot-options page (UEFI firmware, startup settings, …). The _menuOpen guard keeps
	// the panel from dismissing itself while the sub-menu takes activation.
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
		// Sleep, as in the other two power menus (Settings charm, Win7 Start)
		ctx.Items.Add(TaskbarContextMenu.Leaf("Sleep", delegate { PowerActions.Sleep(); }));
		ctx.Items.Add(TaskbarContextMenu.Leaf("Restart", delegate { PowerActions.Restart(); }));
		ctx.Items.Add(TaskbarContextMenu.Leaf("Shut down", delegate { PowerActions.ShutDown(); }));
		ctx.Items.Add(TaskbarContextMenu.Leaf("Advanced startup", delegate { PowerActions.AdvancedStartup(); }));
		ctx.PlacementTarget = _powerButton;   // anchor the popup to THIS window's presentation source
		ctx.Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint;
		ctx.Closed += delegate
		{
			_menuOpen = false;
			base.Topmost = true;
			Dismiss("power-menu closed");
		};
		_menuOpen = true;
		base.Topmost = false;   // let the menu popup render above this (otherwise a Topmost window can cover it)
		ctx.IsOpen = true;
	}

	// --uxpaneltest: the panel as the laptop sees it (every tile, both sliders); returns the number of defined tiles.
	internal static int QaRenderPanel(string outPath)
	{
		return QaRenderMetro(outPath, ActionCenterQaDevice.Laptop).DefinedTiles;
	}
}
