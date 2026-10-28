using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;

#nullable enable annotations

namespace Win81Layer;

// Everything PC Settings reads from and writes to Windows goes through one ISettingsWorld. The live shell uses the
// RealSettingsWorld (the only class of the hub that holds setter P/Invokes, every write wrapped in Mutate and refused
// under read-only diagnostics); QA swaps in the FakeSettingsWorld of PcHubDiagnostics, which only records what would
// have happened. Reads that can take long (power, DDC/CI brightness, radios, the capability probe) are called off the UI
// thread by the pages; SPI and metric reads take microseconds and are read when a page is built.
internal interface ISettingsWorld
{
	string Name { get; }

	DateTime UtcNow { get; }

	// ---- Reads -----------------------------------------------------------------------------------------------------

	// A SystemParametersInfo getter whose value comes back through pvParam as one int/uint/BOOL; null when it failed.
	int? SpiGet(uint action);

	// SPI_GETMOUSE: the three acceleration values (threshold 1, threshold 2, enhance pointer precision).
	int[]? SpiGetMouse();

	int GetSystemMetrics(int index);

	int GetDoubleClickTime();

	// INFINITE (-1) when the caret does not blink.
	int GetCaretBlinkTime();

	// Minutes; -1 when it could not be read.
	int ReadMonitorTimeoutAcMin();

	int ReadStandbyTimeoutAcMin();

	// The friendly name of the active power plan; "" when it could not be read.
	string ReadActivePowerPlan();

	// The first DDC/CI monitor's brightness 0..100; -1 when no monitor answers.
	int ReadBrightness();

	bool ReadStickyKeys();

	bool ReadFilterKeys();

	bool ReadToggleKeys();

	bool ReadToastsEnabled();

	// "English (United States) - US" per installed input language.
	string[] ReadKeyboardLayouts();

	// The input-language switch shortcut ("Left Alt+Shift", "Ctrl+Shift", "Not assigned", "Grave accent").
	string ReadLayoutHotkey();

	HubCaps ReadCaps();

	// The monitors (name, resolution, refresh rate) and the default audio endpoint's name ("" when none).
	MonitorInfo[] ReadMonitors();

	string ReadDefaultAudio(bool capture);

	// File Explorer: file name extensions shown, hidden files shown.
	(bool Extensions, bool Hidden) ReadExplorerFlags();

	HubNetInfo ReadNetwork();

	DriveSpace[] ReadDrives();

	// "Updates are managed by policy on this PC", "A restart is pending", "Last checked: ..." or "".
	string ReadUpdateStatus();

	// ---- Writes (each one refused under read-only diagnostics in the real world) -----------------------------------

	void WriteMonitorTimeoutAcMin(int minutes);

	void WriteStandbyTimeoutAcMin(int minutes);

	void SetBrightness(int percent);

	void SetStickyKeys(bool on);

	void SetFilterKeys(bool on);

	void SetToggleKeys(bool on);

	// The launcher's own Windows-touching options (sound scheme, shell icons, lock screen, autostart).
	void SetWin81Sounds(bool on);

	void SetWin81ShellIcons(bool on);

	Task SetLockScreenAsync(string path);

	// The logon task (schtasks: seconds, off the UI thread) and the HKCU Run entry.
	bool ReadLogonTask();

	bool ReadRunKey();

	bool SetLogonTask(bool on);

	void SetRunKey(bool on);

	// ---- Side effects ------------------------------------------------------------------------------------------------

	// Starts a page, shell name or program. uri: opened through the shell as it is; otherwise file + args.
	void Launch(string file, string? args, bool uri, bool elevate);

	// Sends a key chord (Win+N for the notification centre).
	bool SendChord(params byte[] vks);

	// ---- Native controls (stage 2): every setter below is refused under read-only diagnostics in the real world -----

	// SystemParametersInfo by value: uiParam and pvParam as the documentation of the action says (pv is passed as the
	// value itself, never as a pointer); ini = 0 (live, no broadcast) or 3 (SPIF_UPDATEINIFILE|SPIF_SENDCHANGE).
	bool SpiSet(uint action, uint ui, nint pv, uint ini);

	// SPI_SETMOUSE with the three acceleration values.
	bool SpiSetMouse(int[] values, uint ini);

	// Power (powrprof): every scheme, the active one, a value index of a scheme (seconds or an action index; null when
	// it could not be read), a write (the Win32 result: 0 ok, 5 access denied) and the active-scheme switch.
	PowerPlanInfo[] ReadPowerPlans();

	Guid? ReadActivePlan();

	int? ReadPowerValue(Guid scheme, Guid sub, Guid setting, bool ac);

	int WritePowerValue(Guid scheme, Guid sub, Guid setting, bool ac, int value);

	int SetActivePlan(Guid scheme);

	// HKLM\SOFTWARE\Policies\Microsoft\Power\PowerSettings\{setting} holds an ACSettingIndex or DCSettingIndex value.
	bool PowerManagedByPolicy(Guid setting);

	// Audio (Core Audio): endpoints with their console and communications defaults, the default switch (console and
	// multimedia, or communications alone), the default endpoint's volume and mute, the app sessions and the peak meter.
	AudioEndpointInfo[] ReadEndpoints(bool capture);

	void SetAudioDefault(string id, bool communications);

	(int Volume, bool Muted)? ReadEndpointVolume(bool capture);

	void SetEndpointVolume(bool capture, int percent);

	void SetEndpointMute(bool capture, bool mute);

	AppVolumeInfo[] ReadSessions();

	void SetSessionVolume(string key, int percent);

	void SetSessionMute(string key, bool mute);

	float ReadPeak(bool capture);

	// Device notifications (a device came or went, the default changed; not volume steps) while subscribed: the hub
	// subscribes only while the Sound page shows.
	IDisposable SubscribeAudio(Action changed);

	void PlayTestSound();

	// DDC/CI brightness of one monitor (device name "\\.\DISPLAY1"): 0..100, -1 when the monitor does not answer.
	int ReadMonitorBrightness(string device);

	void SetDdcBrightness(string device, int percent);

	// Radios (Windows.Devices.Radios): null when the radio does not exist.
	bool? ReadRadio(string kind);

	bool SetRadio(string kind, bool on);

	// Every radio off (true) or back on (false): "Turn off all wireless", never called Airplane mode.
	bool SetAllWireless(bool off);

	string[] ReadPairedBluetooth();

	// Locale (user default): GetLocaleInfoEx / SetLocaleInfoW, the short date formats Windows offers.
	string? GetLocale(uint lctype);

	bool SetLocale(uint lctype, string value);

	string[] ReadShortDateFormats();

	// "intl": one WM_SETTINGCHANGE broadcast; "assoc": SHChangeNotify(SHCNE_ASSOCCHANGED).
	void Broadcast(string area);

	// File Explorer flags through SHGetSetSettings ("extensions", "hidden").
	void SetExplorerFlag(string flag, bool on);

	// Read-only extras of the pages.
	string ReadMetered();

	string ReadProxy();

	(string Kind, string App)[] ReadDefaultApps();

	StartupAppInfo[] ReadStartupApps();

	string ReadGpu();

	long ReadRecycleBinBytes();
}

internal sealed record MonitorInfo(string Name, int Width, int Height, int Hz, bool Primary)
{
	// "\\.\DISPLAY1" (the GDI device the DDC/CI and the brightness rows use); the monitor's own name when Windows has it.
	internal string Device { get; init; } = "";

	internal string Friendly { get; init; } = "";

	// "1920 × 1080 · 144 Hz": the one way Displays, About and Copy specs write a monitor's mode.
	internal string ModeText => Width + " × " + Height + (Hz > 1 ? " · " + Hz + " Hz" : "");

	// "LG ULTRAGEAR" (Windows' monitor name) or "DISPLAY1" when Windows has none.
	internal string Title => Friendly.Length > 0 ? Friendly : Name.TrimStart('\\', '.');
}

internal sealed record PowerPlanInfo(Guid Id, string Name);

internal sealed record AudioEndpointInfo(string Id, string Name, bool IsDefault, bool IsDefaultComms);

internal sealed record AppVolumeInfo(string Key, string Name, int Volume, bool Muted);

internal sealed record StartupAppInfo(string Name, string Source, bool Enabled);

internal sealed record HubNetInfo(string Kind, string Name, string Status, string? Ipv4, string? Gateway, string? Dns);

internal sealed record DriveSpace(string Name, string Label, long Free, long Total);

internal static class SettingsWorld
{
	private static ISettingsWorld _current = RealSettingsWorld.Instance;

	internal static ISettingsWorld Current
	{
		get => Volatile.Read(ref _current);
		set
		{
			ArgumentNullException.ThrowIfNull(value);
			// Only QA swaps the world, and only while the process runs read-only diagnostics.
			if (!SwapAllowed(value, SettingsStore.ReadOnlyDiagnostics))
			{
				throw new InvalidOperationException("a non-real settings world is allowed only under read-only diagnostics");
			}
			Volatile.Write(ref _current, value);
		}
	}

	internal static bool IsReal => Current is RealSettingsWorld;

	// The rule of the setter (QA checks it with a local flag, never by turning read-only off).
	internal static bool SwapAllowed(ISettingsWorld value, bool readOnly) => value is RealSettingsWorld || readOnly;
}

// The only place of the hub with setter P/Invokes. Every mutating member is Mutate(id, ...), which refuses real writes
// while the process runs read-only diagnostics.
internal sealed partial class RealSettingsWorld : ISettingsWorld
{
	internal static readonly RealSettingsWorld Instance = new RealSettingsWorld();

	private static int _writeCount;

	// Real writes performed by this process (QA asserts 0).
	internal static int WriteCount => Volatile.Read(ref _writeCount);

	private RealSettingsWorld()
	{
	}

	public string Name => "real";

	public DateTime UtcNow => DateTime.UtcNow;

	internal static void Mutate(string id, Action write)
	{
		SettingsGuard.ThrowIfReadOnly(id);
		Interlocked.Increment(ref _writeCount);
		write();
	}

	// ---- Reads -----------------------------------------------------------------------------------------------------

	public int? SpiGet(uint action)
	{
		try
		{
			int v = 0;
			return SystemParametersInfoGet(action, 0u, ref v, 0u) ? v : null;
		}
		catch
		{
			return null;
		}
	}

	public int[]? SpiGetMouse()
	{
		try
		{
			int[] v = new int[3];
			return SystemParametersInfoArray(0x0003u, 0u, v, 0u) ? v : null;
		}
		catch
		{
			return null;
		}
	}

	public int GetSystemMetrics(int index)
	{
		try
		{
			return GetSystemMetricsNative(index);
		}
		catch
		{
			return 0;
		}
	}

	public int GetDoubleClickTime()
	{
		try
		{
			return (int)GetDoubleClickTimeNative();
		}
		catch
		{
			return 500;
		}
	}

	public int GetCaretBlinkTime()
	{
		try
		{
			return (int)GetCaretBlinkTimeNative();
		}
		catch
		{
			return 530;
		}
	}

	public int ReadMonitorTimeoutAcMin() => PowerConfig.MonitorTimeoutAcMin();

	public int ReadStandbyTimeoutAcMin() => PowerConfig.StandbyTimeoutAcMin();

	public string ReadActivePowerPlan()
	{
		nint active = IntPtr.Zero;
		try
		{
			if (PowerGetActiveScheme(IntPtr.Zero, out active) != 0 || active == IntPtr.Zero)
			{
				return string.Empty;
			}
			Guid scheme = Marshal.PtrToStructure<Guid>(active);
			uint size = 0;
			if (PowerReadFriendlyName(IntPtr.Zero, ref scheme, IntPtr.Zero, IntPtr.Zero, null, ref size) != 0 || size == 0 || size > 4096)
			{
				return string.Empty;
			}
			byte[] buf = new byte[size];
			if (PowerReadFriendlyName(IntPtr.Zero, ref scheme, IntPtr.Zero, IntPtr.Zero, buf, ref size) != 0)
			{
				return string.Empty;
			}
			return System.Text.Encoding.Unicode.GetString(buf, 0, (int)size).TrimEnd('\0');
		}
		catch
		{
			return string.Empty;
		}
		finally
		{
			if (active != IntPtr.Zero)
			{
				LocalFree(active);
			}
		}
	}

	public int ReadBrightness() => MonitorBrightness.Get();

	public bool ReadStickyKeys() => AccessibilityKeys.StickyOn();

	public bool ReadFilterKeys() => AccessibilityKeys.FilterOn();

	public bool ReadToggleKeys() => AccessibilityKeys.ToggleOn();

	public bool ReadToastsEnabled()
	{
		try
		{
			using RegistryKey? k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\PushNotifications");
			return k?.GetValue("ToastEnabled") is not int v || v != 0;
		}
		catch
		{
			return true;
		}
	}

	public string[] ReadKeyboardLayouts()
	{
		try
		{
			List<string> list = new List<string>();
			foreach (System.Windows.Forms.InputLanguage lang in System.Windows.Forms.InputLanguage.InstalledInputLanguages)
			{
				string culture = lang.Culture?.DisplayName ?? "";
				string layout = lang.LayoutName ?? "";
				list.Add(layout.Length > 0 ? culture + " – " + layout : culture);
			}
			return list.ToArray();
		}
		catch
		{
			return Array.Empty<string>();
		}
	}

	public string ReadLayoutHotkey()
	{
		try
		{
			using RegistryKey? k = Registry.CurrentUser.OpenSubKey(@"Keyboard Layout\Toggle");
			string v = (k?.GetValue("Language Hotkey") ?? k?.GetValue("Hotkey"))?.ToString() ?? "1";
			return HotkeyName(v);
		}
		catch
		{
			return HotkeyName("1");
		}
	}

	internal static string HotkeyName(string v)
	{
		return v.Trim() switch
		{
			"2" => "Ctrl+Shift",
			"3" => "Not assigned",
			"4" => "Grave accent (`)",
			_ => "Left Alt+Shift"
		};
	}

	public HubCaps ReadCaps() => HubCaps.ReadReal();

	public MonitorInfo[] ReadMonitors()
	{
		List<MonitorInfo> list = new List<MonitorInfo>();
		Dictionary<string, (string Friendly, int Hz)> names = DisplayNames.Read();
		try
		{
			int n = 0;
			foreach (System.Windows.Forms.Screen screen in System.Windows.Forms.Screen.AllScreens)
			{
				n++;
				DEVMODE dm = new DEVMODE { dmSize = (short)Marshal.SizeOf<DEVMODE>() };
				int hz = EnumDisplaySettings(screen.DeviceName, -1, ref dm) ? dm.dmDisplayFrequency : 0;
				string friendly = string.Empty;
				if (names.TryGetValue(screen.DeviceName, out (string Friendly, int Hz) dn))
				{
					friendly = dn.Friendly;
					if (dn.Hz > 1)
					{
						hz = dn.Hz;
					}
				}
				int w = dm.dmPelsWidth > 0 ? dm.dmPelsWidth : screen.Bounds.Width;
				int h = dm.dmPelsHeight > 0 ? dm.dmPelsHeight : screen.Bounds.Height;
				string name = screen.DeviceName.TrimStart('\\', '.');
				list.Add(new MonitorInfo(name.Length > 0 ? name : "DISPLAY" + n, w, h, hz, screen.Primary) { Device = screen.DeviceName, Friendly = friendly });
			}
		}
		catch
		{
		}
		return list.ToArray();
	}

	public string ReadDefaultAudio(bool capture)
	{
		try
		{
			return AudioDevices.Enumerate(capture).FirstOrDefault((AudioDevice d) => d.IsDefault)?.Name ?? string.Empty;
		}
		catch
		{
			return string.Empty;
		}
	}

	public (bool Extensions, bool Hidden) ReadExplorerFlags()
	{
		try
		{
			using RegistryKey? k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced");
			bool ext = k?.GetValue("HideFileExt") is not int h || h == 0;
			bool hidden = k?.GetValue("Hidden") is int v && v == 1;
			return (ext, hidden);
		}
		catch
		{
			return (false, false);
		}
	}

	public HubNetInfo ReadNetwork()
	{
		string kind = "No network";
		string name = string.Empty;
		string status = string.Empty;
		try
		{
			NetState81 st = NetState81.Read();
			kind = st.Kind switch
			{
				NetKind.Wifi => "Wi-Fi",
				NetKind.Ethernet => "Ethernet",
				NetKind.Cellular => "Cellular",
				NetKind.Airplane => "Airplane mode",
				_ => "No network"
			};
			name = st.Label ?? string.Empty;
			status = st.Status ?? string.Empty;
		}
		catch
		{
		}
		string? ip = null;
		string? gw = null;
		string? dns = null;
		try
		{
			foreach (System.Net.NetworkInformation.NetworkInterface ni in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
			{
				if (ni.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up || ni.NetworkInterfaceType == System.Net.NetworkInformation.NetworkInterfaceType.Loopback)
				{
					continue;
				}
				System.Net.NetworkInformation.IPInterfaceProperties props = ni.GetIPProperties();
				System.Net.IPAddress? g = props.GatewayAddresses.Select((System.Net.NetworkInformation.GatewayIPAddressInformation a) => a.Address).FirstOrDefault((System.Net.IPAddress a) => a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork && !a.Equals(System.Net.IPAddress.Any));
				if (g == null)
				{
					continue;
				}
				ip = props.UnicastAddresses.Select((System.Net.NetworkInformation.UnicastIPAddressInformation a) => a.Address).FirstOrDefault((System.Net.IPAddress a) => a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)?.ToString();
				gw = g.ToString();
				dns = props.DnsAddresses.FirstOrDefault((System.Net.IPAddress a) => a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)?.ToString();
				break;
			}
		}
		catch
		{
		}
		return new HubNetInfo(kind, name, status, ip, gw, dns);
	}

	public DriveSpace[] ReadDrives()
	{
		List<DriveSpace> list = new List<DriveSpace>();
		try
		{
			foreach (DriveInfo d in DriveInfo.GetDrives())
			{
				if (d.DriveType != DriveType.Fixed || !d.IsReady)
				{
					continue;
				}
				list.Add(new DriveSpace(d.Name.TrimEnd('\\'),d.VolumeLabel ?? string.Empty, d.AvailableFreeSpace, d.TotalSize));
			}
		}
		catch
		{
		}
		return list.ToArray();
	}

	public string ReadUpdateStatus()
	{
		try
		{
			using RegistryKey? pol = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate");
			using RegistryKey? au = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate\AU");
			if ((pol != null && pol.ValueCount > 0) || (au != null && au.ValueCount > 0))
			{
				return "Updates are managed by policy on this PC";
			}
			using RegistryKey? reboot = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update\RebootRequired");
			if (reboot != null)
			{
				return "A restart is pending";
			}
		}
		catch
		{
		}
		return SystemInfo.WindowsUpdateLastChecked() ?? string.Empty;
	}

	[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
	private struct DEVMODE
	{
		[MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
		public string dmDeviceName;
		public short dmSpecVersion;
		public short dmDriverVersion;
		public short dmSize;
		public short dmDriverExtra;
		public int dmFields;
		public int dmPositionX;
		public int dmPositionY;
		public int dmDisplayOrientation;
		public int dmDisplayFixedOutput;
		public short dmColor;
		public short dmDuplex;
		public short dmYResolution;
		public short dmTTOption;
		public short dmCollate;
		[MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
		public string dmFormName;
		public short dmLogPixels;
		public int dmBitsPerPel;
		public int dmPelsWidth;
		public int dmPelsHeight;
		public int dmDisplayFlags;
		public int dmDisplayFrequency;
		public int dmICMMethod;
		public int dmICMIntent;
		public int dmMediaType;
		public int dmDitherType;
		public int dmReserved1;
		public int dmReserved2;
		public int dmPanningWidth;
		public int dmPanningHeight;
	}

	[DllImport("user32.dll", CharSet = CharSet.Unicode)]
	private static extern bool EnumDisplaySettings(string deviceName, int modeNum, ref DEVMODE devMode);

	// ---- Writes ------------------------------------------------------------------------------------------------------

	public void WriteMonitorTimeoutAcMin(int minutes) => Mutate("power.screen.ac", () => PowerConfig.SetMonitorTimeoutAcMin(minutes));

	public void WriteStandbyTimeoutAcMin(int minutes) => Mutate("power.sleep.ac", () => PowerConfig.SetStandbyTimeoutAcMin(minutes));

	public void SetBrightness(int percent) => Mutate("display.brightness", () => MonitorBrightness.SetThrottled(percent));

	public void SetStickyKeys(bool on) => Mutate("keyboard.sticky", () => AccessibilityKeys.SetSticky(on));

	public void SetFilterKeys(bool on) => Mutate("keyboard.filter", () => AccessibilityKeys.SetFilter(on));

	public void SetToggleKeys(bool on) => Mutate("keyboard.toggle", () => AccessibilityKeys.SetToggle(on));

	public void SetWin81Sounds(bool on) => Mutate("sound.win81", delegate
	{
		if (on)
		{
			SoundScheme.Apply();
		}
		else
		{
			SoundScheme.Revert();
		}
	});

	public void SetWin81ShellIcons(bool on) => Mutate("appearance.shellicons", delegate
	{
		Task.Run(delegate
		{
			try
			{
				if (on)
				{
					SystemIcons81.Apply();
				}
				else
				{
					SystemIcons81.Restore();
				}
			}
			catch (Exception ex)
			{
				Logger.Log("Shell icons: " + ex.Message);
			}
		});
	});

	public async Task SetLockScreenAsync(string path)
	{
		SettingsGuard.ThrowIfReadOnly("appearance.lockscreen");
		Interlocked.Increment(ref _writeCount);
		Windows.Storage.StorageFile file = await Windows.Storage.StorageFile.GetFileFromPathAsync(path);
		await LockScreenSetter.SetAsync(file);
	}

	public bool ReadLogonTask() => LogonTask.IsEnabled();

	public bool ReadRunKey() => Autostart.IsEnabled();

	public bool SetLogonTask(bool on)
	{
		bool ok = false;
		Mutate("launcher.logon", delegate { ok = LogonTask.SetEnabled(on); });
		return ok;
	}

	public void SetRunKey(bool on) => Mutate("launcher.runkey", () => Autostart.SetEnabled(on));

	// ---- Side effects ------------------------------------------------------------------------------------------------

	public void Launch(string file, string? args, bool uri, bool elevate)
	{
		// Not a settings write, but QA must never open real windows: refused under read-only diagnostics too.
		SettingsGuard.ThrowIfReadOnly("launch " + file);
		SettingsLink.CountProcessStart();
		ShellLaunch.Run(delegate
		{
			try
			{
				ProcessStartInfo psi;
				if (uri)
				{
					psi = new ProcessStartInfo(file)
					{
						UseShellExecute = true
					};
				}
				else
				{
					string path = Environment.ExpandEnvironmentVariables(file);
					psi = new ProcessStartInfo(path)
					{
						UseShellExecute = true
					};
					string? dir = Path.IsPathRooted(path) ? Path.GetDirectoryName(path) : null;
					psi.WorkingDirectory = string.IsNullOrEmpty(dir) ? Environment.SystemDirectory : dir;
					if (!string.IsNullOrEmpty(args))
					{
						psi.Arguments = Environment.ExpandEnvironmentVariables(args);
					}
				}
				if (elevate)
				{
					psi.Verb = "runas";
				}
				Process.Start(psi)?.Dispose();
			}
			catch (Exception ex)
			{
				Logger.Log("PC settings launch '" + file + (string.IsNullOrEmpty(args) ? "" : " " + args) + "' (or cancelled): " + ex.Message);
			}
		});
	}

	public bool SendChord(params byte[] vks)
	{
		SettingsGuard.ThrowIfReadOnly("key chord");
		return Keystroke.Chord(vks);
	}

	// ---- Interop -----------------------------------------------------------------------------------------------------

	[DllImport("user32.dll", EntryPoint = "SystemParametersInfoW", SetLastError = true)]
	private static extern bool SystemParametersInfoGet(uint action, uint uiParam, ref int pvParam, uint winIni);

	[DllImport("user32.dll", EntryPoint = "SystemParametersInfoW", SetLastError = true)]
	private static extern bool SystemParametersInfoArray(uint action, uint uiParam, [In, Out] int[] pvParam, uint winIni);

	[DllImport("user32.dll", EntryPoint = "GetSystemMetrics")]
	private static extern int GetSystemMetricsNative(int index);

	[DllImport("user32.dll", EntryPoint = "GetDoubleClickTime")]
	private static extern uint GetDoubleClickTimeNative();

	[DllImport("user32.dll", EntryPoint = "GetCaretBlinkTime")]
	private static extern uint GetCaretBlinkTimeNative();

	[DllImport("powrprof.dll")]
	private static extern uint PowerGetActiveScheme(nint userRootPowerKey, out nint activePolicyGuid);

	[DllImport("powrprof.dll")]
	private static extern uint PowerReadFriendlyName(nint rootPowerKey, ref Guid schemeGuid, nint subGroup, nint setting, byte[]? buffer, ref uint bufferSize);

	[DllImport("kernel32.dll")]
	private static extern nint LocalFree(nint hMem);
}

// What this PC has and what policy allows, read once per PC Settings window (off the UI thread): the Settings page
// visibility policy and hardware probe of the system search (PcCaps), the Control Panel policies, the radios, the battery
// and the sleep states. Pages and links consult it so nothing is shown that leads nowhere.
internal sealed class HubCaps
{
	internal SystemSearchBuilder.PcCaps Pc = SystemSearchBuilder.PcCaps.FromPolicy(string.Empty, touchpad: false, touch: false);

	internal bool NoControlPanel;

	// Names in the DisallowCpl list (canonical names or file names), and the RestrictCpl allow-list (null: no list).
	internal HashSet<string> DisallowCpl = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

	internal HashSet<string>? RestrictCpl;

	internal bool HasWifi;

	internal bool HasBluetooth;

	internal bool HasRadios;

	internal bool HasBattery;

	internal bool SleepAllowed;

	internal bool HibernateAllowed;

	internal bool Digitizer;

	internal bool PowerButtonPresent;

	internal bool SleepButtonPresent;

	internal bool LidPresent;

	// HiberbootEnabled (Fast startup); null when it could not be read.
	internal bool? FastStartup;

	// The Settings page (ms-settings name without the scheme) exists here and is not hidden by policy.
	internal bool PageAvailable(string page)
	{
		return Pc.PageAvailable(page);
	}

	// A Control Panel item (canonical name such as Microsoft.PowerOptions, or a .cpl file name) may be opened.
	internal bool CplAllowed(string item)
	{
		if (NoControlPanel)
		{
			return false;
		}
		// the policy lists may name an item by its canonical name or by its .cpl file: both are checked
		string key = item.Trim();
		string? other = CplAlias(key);
		if (DisallowCpl.Contains(key) || (other != null && DisallowCpl.Contains(other)))
		{
			return false;
		}
		return RestrictCpl == null || RestrictCpl.Contains(key) || (other != null && RestrictCpl.Contains(other));
	}

	// The .cpl files of the Control Panel items PC settings opens and their canonical names (both directions).
	private static readonly (string File, string Name)[] CplNames = new (string, string)[]
	{
		("powercfg.cpl", "Microsoft.PowerOptions"),
		("mmsys.cpl", "Microsoft.Sound"),
		("main.cpl", "Microsoft.Mouse"),
		("intl.cpl", "Microsoft.RegionAndLanguage"),
		("timedate.cpl", "Microsoft.DateAndTime"),
		("inetcpl.cpl", "Microsoft.InternetOptions"),
		("appwiz.cpl", "Microsoft.ProgramsAndFeatures"),
		("ncpa.cpl", "Microsoft.NetworkConnections"),
		("sysdm.cpl", "Microsoft.System"),
		("desk.cpl", "Microsoft.Display"),
		("firewall.cpl", "Microsoft.WindowsFirewall"),
		("hdwwiz.cpl", "Microsoft.DeviceManager"),
		("joy.cpl", "Microsoft.GameControllers")
	};

	internal static string? CplAlias(string item)
	{
		foreach ((string file, string name) in CplNames)
		{
			if (string.Equals(item, file, StringComparison.OrdinalIgnoreCase))
			{
				return name;
			}
			if (string.Equals(item, name, StringComparison.OrdinalIgnoreCase))
			{
				return file;
			}
		}
		return null;
	}

	internal bool PolicyHidesPages => Pc.Policy.Length > 0;

	internal string Describe()
	{
		return $"{Pc} cpl={(NoControlPanel ? "off" : "on")} wifi={HasWifi} bt={HasBluetooth} radios={HasRadios} battery={HasBattery} sleep={SleepAllowed} hibernate={HibernateAllowed} digitizer={Digitizer}";
	}

	// Called on a worker thread (HID enumeration, radios and the registry can take tens of milliseconds).
	internal static HubCaps ReadReal()
	{
		HubCaps c = new HubCaps();
		try
		{
			c.Pc = SystemSearchBuilder.PcCaps.Read();
		}
		catch
		{
		}
		ReadCplPolicies(c);
		try
		{
			c.HasWifi = NetCaps.HasWifi;
			c.HasRadios = NetCaps.RadiosPresent;
		}
		catch
		{
		}
		try
		{
			Task<bool> bt = Task.Run(async delegate
			{
				IReadOnlyList<Windows.Devices.Radios.Radio> radios = await Windows.Devices.Radios.Radio.GetRadiosAsync();
				return radios.Any((Windows.Devices.Radios.Radio r) => r.Kind == Windows.Devices.Radios.RadioKind.Bluetooth);
			});
			c.HasBluetooth = bt.Wait(1500) && bt.Result;
			c.HasRadios |= c.HasBluetooth;
		}
		catch
		{
		}
		ReadPower(c);
		try
		{
			int digitizer = GetSystemMetrics(94);
			c.Digitizer = (digitizer & 0x0F) != 0;
		}
		catch
		{
		}
		return c;
	}

	private static void ReadCplPolicies(HubCaps c)
	{
		foreach (RegistryKey hive in new[] { Registry.CurrentUser, Registry.LocalMachine })
		{
			try
			{
				using RegistryKey? k = hive.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\Explorer");
				if (k == null)
				{
					continue;
				}
				if (k.GetValue("NoControlPanel") is int n && n != 0)
				{
					c.NoControlPanel = true;
				}
				if (k.GetValue("DisallowCpl") is int d && d != 0)
				{
					using RegistryKey? list = k.OpenSubKey("DisallowCpl");
					foreach (string name in list?.GetValueNames() ?? Array.Empty<string>())
					{
						if (list!.GetValue(name) is string item && item.Trim().Length > 0)
						{
							c.DisallowCpl.Add(item.Trim());
						}
					}
				}
				if (k.GetValue("RestrictCpl") is int r && r != 0)
				{
					c.RestrictCpl ??= new HashSet<string>(StringComparer.OrdinalIgnoreCase);
					using RegistryKey? list = k.OpenSubKey("RestrictCpl");
					foreach (string name in list?.GetValueNames() ?? Array.Empty<string>())
					{
						if (list!.GetValue(name) is string item && item.Trim().Length > 0)
						{
							c.RestrictCpl.Add(item.Trim());
						}
					}
				}
			}
			catch
			{
			}
		}
	}

	private static void ReadPower(HubCaps c)
	{
		try
		{
			byte[] caps = new byte[96];
			if (GetPwrCapabilities(caps))
			{
				c.PowerButtonPresent = caps[0] != 0;
				c.SleepButtonPresent = caps[1] != 0;
				c.LidPresent = caps[2] != 0;
				bool aoac = caps[20] != 0;
				c.HasBattery = caps[30] != 0;
				c.SleepAllowed = aoac;
			}
		}
		catch
		{
		}
		try
		{
			c.SleepAllowed |= IsPwrSuspendAllowed();
			c.HibernateAllowed = IsPwrHibernateAllowed();
		}
		catch
		{
		}
		try
		{
			using RegistryKey? k = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Session Manager\Power");
			if (k?.GetValue("HiberbootEnabled") is int v)
			{
				c.FastStartup = v != 0;
			}
		}
		catch
		{
		}
	}

	[DllImport("powrprof.dll")]
	private static extern bool GetPwrCapabilities([Out] byte[] capabilities);

	[DllImport("powrprof.dll")]
	private static extern bool IsPwrSuspendAllowed();

	[DllImport("powrprof.dll")]
	private static extern bool IsPwrHibernateAllowed();

	[DllImport("user32.dll")]
	private static extern int GetSystemMetrics(int index);
}

// The lock screen picture (Windows.System.UserProfile.LockScreen), behind the read-only guard like every other setter.
internal static class LockScreenSetter
{
	internal static async Task SetAsync(Windows.Storage.StorageFile file)
	{
		SettingsGuard.ThrowIfReadOnly("lock screen picture");
		await Windows.System.UserProfile.LockScreen.SetImageFileAsync(file);
	}
}
