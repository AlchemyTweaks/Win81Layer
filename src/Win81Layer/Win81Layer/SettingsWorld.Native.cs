using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;

#nullable enable annotations

namespace Win81Layer;

// The native controls of PC Settings (stage 2) in the real world: documented APIs only (SystemParametersInfo, powrprof,
// Core Audio through the shared AudioEndpointService, dxva2 DDC/CI, Windows.Devices.Radios, the locale functions,
// SHGetSetSettings). Every write is Mutate(id, ...): refused under read-only diagnostics, counted in WriteCount.
internal sealed partial class RealSettingsWorld
{
	// ---- SystemParametersInfo -----------------------------------------------------------------------------------------

	public bool SpiSet(uint action, uint ui, nint pv, uint ini)
	{
		bool ok = false;
		Mutate("spi 0x" + action.ToString("x", CultureInfo.InvariantCulture), delegate { ok = SystemParametersInfoSet(action, ui, pv, ini); });
		return ok;
	}

	public bool SpiSetMouse(int[] values, uint ini)
	{
		bool ok = false;
		Mutate("spi 0x4", delegate
		{
			int[] v = (int[])values.Clone();
			ok = SystemParametersInfoArray(0x0004u, 0u, v, ini);
		});
		return ok;
	}

	[DllImport("user32.dll", EntryPoint = "SystemParametersInfoW", SetLastError = true)]
	private static extern bool SystemParametersInfoSet(uint action, uint uiParam, nint pvParam, uint winIni);

	// ---- Power --------------------------------------------------------------------------------------------------------

	public PowerPlanInfo[] ReadPowerPlans()
	{
		List<PowerPlanInfo> list = new List<PowerPlanInfo>();
		try
		{
			for (uint i = 0; i < 512; i++)
			{
				byte[] buf = new byte[16];
				uint size = 16;
				uint r = PowerEnumerate(IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 16u, i, buf, ref size);
				if (r != 0)
				{
					break;
				}
				Guid g = new Guid(buf);
				list.Add(new PowerPlanInfo(g, FriendlyName(g)));
			}
		}
		catch (Exception ex)
		{
			Logger.Log("PC settings: power plans: " + ex.Message);
		}
		return list.ToArray();
	}

	private static string FriendlyName(Guid scheme)
	{
		try
		{
			uint size = 0;
			if (PowerReadFriendlyName(IntPtr.Zero, ref scheme, IntPtr.Zero, IntPtr.Zero, null, ref size) != 0 || size == 0 || size > 4096)
			{
				return scheme.ToString();
			}
			byte[] buf = new byte[size];
			if (PowerReadFriendlyName(IntPtr.Zero, ref scheme, IntPtr.Zero, IntPtr.Zero, buf, ref size) != 0)
			{
				return scheme.ToString();
			}
			string name = Encoding.Unicode.GetString(buf, 0, (int)size).TrimEnd('\0');
			return name.Length > 0 ? name : scheme.ToString();
		}
		catch
		{
			return scheme.ToString();
		}
	}

	public Guid? ReadActivePlan()
	{
		nint active = IntPtr.Zero;
		try
		{
			if (PowerGetActiveScheme(IntPtr.Zero, out active) != 0 || active == IntPtr.Zero)
			{
				return null;
			}
			return Marshal.PtrToStructure<Guid>(active);
		}
		catch
		{
			return null;
		}
		finally
		{
			if (active != IntPtr.Zero)
			{
				LocalFree(active);
			}
		}
	}

	public int? ReadPowerValue(Guid scheme, Guid sub, Guid setting, bool ac)
	{
		try
		{
			uint v;
			uint r = ac ? PowerReadACValueIndex(IntPtr.Zero, ref scheme, ref sub, ref setting, out v) : PowerReadDCValueIndex(IntPtr.Zero, ref scheme, ref sub, ref setting, out v);
			return r == 0 ? (int)v : null;
		}
		catch
		{
			return null;
		}
	}

	public int WritePowerValue(Guid scheme, Guid sub, Guid setting, bool ac, int value)
	{
		uint r = 1;
		Mutate("power " + setting.ToString("D").Substring(0, 8) + (ac ? " ac" : " dc"), delegate
		{
			uint v = (uint)Math.Max(0, value);
			r = ac ? PowerWriteACValueIndex(IntPtr.Zero, ref scheme, ref sub, ref setting, v) : PowerWriteDCValueIndex(IntPtr.Zero, ref scheme, ref sub, ref setting, v);
		});
		return (int)r;
	}

	public int SetActivePlan(Guid scheme)
	{
		uint r = 1;
		Mutate("power plan", delegate { r = PowerSetActiveScheme(IntPtr.Zero, ref scheme); });
		return (int)r;
	}

	public bool PowerManagedByPolicy(Guid setting)
	{
		try
		{
			using RegistryKey? k = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Policies\Microsoft\Power\PowerSettings\" + setting.ToString("B"));
			if (k == null)
			{
				using RegistryKey? k2 = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Policies\Microsoft\Power\PowerSettings\" + setting.ToString("D"));
				return k2 != null && (k2.GetValue("ACSettingIndex") != null || k2.GetValue("DCSettingIndex") != null);
			}
			return k.GetValue("ACSettingIndex") != null || k.GetValue("DCSettingIndex") != null;
		}
		catch
		{
			return false;
		}
	}

	[DllImport("powrprof.dll")]
	private static extern uint PowerEnumerate(nint rootPowerKey, nint schemeGuid, nint subGroupGuid, uint accessFlags, uint index, [Out] byte[] buffer, ref uint bufferSize);

	[DllImport("powrprof.dll")]
	private static extern uint PowerReadACValueIndex(nint rootPowerKey, ref Guid schemeGuid, ref Guid subGroupGuid, ref Guid settingGuid, out uint value);

	[DllImport("powrprof.dll")]
	private static extern uint PowerReadDCValueIndex(nint rootPowerKey, ref Guid schemeGuid, ref Guid subGroupGuid, ref Guid settingGuid, out uint value);

	[DllImport("powrprof.dll")]
	private static extern uint PowerWriteACValueIndex(nint rootPowerKey, ref Guid schemeGuid, ref Guid subGroupGuid, ref Guid settingGuid, uint value);

	[DllImport("powrprof.dll")]
	private static extern uint PowerWriteDCValueIndex(nint rootPowerKey, ref Guid schemeGuid, ref Guid subGroupGuid, ref Guid settingGuid, uint value);

	[DllImport("powrprof.dll")]
	private static extern uint PowerSetActiveScheme(nint userRootPowerKey, ref Guid schemeGuid);

	// ---- Audio (the shared service) -----------------------------------------------------------------------------------

	public AudioEndpointInfo[] ReadEndpoints(bool capture) => AudioEndpointService.Shared.Endpoints(capture).ToArray();

	public void SetAudioDefault(string id, bool communications) => Mutate(communications ? "audio default comms" : "audio default", () => AudioEndpointService.Shared.SetDefault(id, communications));

	public (int Volume, bool Muted)? ReadEndpointVolume(bool capture)
	{
		AudioEndpointService a = AudioEndpointService.Shared;
		if (!a.HasEndpoint(capture))
		{
			return null;
		}
		return ((int)Math.Round(a.GetVolume(capture) * 100f), a.GetMute(capture));
	}

	public void SetEndpointVolume(bool capture, int percent) => Mutate(capture ? "input volume" : "volume", () => AudioEndpointService.Shared.SetVolume(Math.Clamp(percent, 0, 100) / 100f, capture));

	public void SetEndpointMute(bool capture, bool mute) => Mutate(capture ? "input mute" : "mute", () => AudioEndpointService.Shared.SetMute(mute, capture));

	public AppVolumeInfo[] ReadSessions() => AudioEndpointService.Shared.Sessions().ToArray();

	public void SetSessionVolume(string key, int percent) => Mutate("app volume", () => AudioEndpointService.Shared.SetSessionVolume(key, percent));

	public void SetSessionMute(string key, bool mute) => Mutate("app mute", () => AudioEndpointService.Shared.SetSessionMute(key, mute));

	public float ReadPeak(bool capture) => AudioEndpointService.Shared.GetPeak(capture);

	public IDisposable SubscribeAudio(Action changed) => AudioEndpointService.Shared.Subscribe(changed, devicesOnly: true);

	public void PlayTestSound()
	{
		SettingsGuard.ThrowIfReadOnly("test sound");
		string wav = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Media", "Windows Background.wav");
		if (!File.Exists(wav))
		{
			wav = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Media", "Windows Ding.wav");
		}
		if (!File.Exists(wav))
		{
			return;
		}
		Task.Run(delegate
		{
			try
			{
				using System.Media.SoundPlayer p = new System.Media.SoundPlayer(wav);
				p.PlaySync();
			}
			catch (Exception ex)
			{
				Logger.Log("PC settings: test sound: " + ex.Message);
			}
		});
	}

	// ---- Monitors -----------------------------------------------------------------------------------------------------

	public int ReadMonitorBrightness(string device) => MonitorBrightness.Get(device);

	public void SetDdcBrightness(string device, int percent) => Mutate("brightness " + device, () => MonitorBrightness.Set(device, percent));

	// ---- Radios -------------------------------------------------------------------------------------------------------

	private static Windows.Devices.Radios.RadioKind? KindOf(string kind) => kind switch
	{
		"wifi" => Windows.Devices.Radios.RadioKind.WiFi,
		"bluetooth" => Windows.Devices.Radios.RadioKind.Bluetooth,
		_ => null
	};

	public bool? ReadRadio(string kind)
	{
		Windows.Devices.Radios.RadioKind? k = KindOf(kind);
		if (k == null)
		{
			return null;
		}
		try
		{
			Task<bool?> t = Task.Run(async delegate
			{
				IReadOnlyList<Windows.Devices.Radios.Radio> radios = await Windows.Devices.Radios.Radio.GetRadiosAsync();
				Windows.Devices.Radios.Radio? r = radios.FirstOrDefault((Windows.Devices.Radios.Radio x) => x.Kind == k.Value);
				return r == null ? (bool?)null : r.State == Windows.Devices.Radios.RadioState.On;
			});
			return t.Wait(2000) ? t.Result : null;
		}
		catch
		{
			return null;
		}
	}

	public bool SetRadio(string kind, bool on)
	{
		Windows.Devices.Radios.RadioKind? k = KindOf(kind);
		bool ok = false;
		Mutate("radio " + kind, delegate
		{
			if (k == null)
			{
				return;
			}
			Task<bool> t = RadioQuick.SetKind(k.Value, on);
			ok = t.Wait(4000) && t.Result;
		});
		return ok;
	}

	public bool SetAllWireless(bool off)
	{
		bool ok = false;
		Mutate("radios all", delegate
		{
			Task<bool> t = RadioQuick.SetAirplane(off);
			ok = t.Wait(5000) && t.Result;
		});
		return ok;
	}

	public string[] ReadPairedBluetooth()
	{
		try
		{
			Task<string[]> t = Task.Run(async delegate
			{
				string selector = Windows.Devices.Bluetooth.BluetoothDevice.GetDeviceSelectorFromPairingState(true);
				Windows.Devices.Enumeration.DeviceInformationCollection all = await Windows.Devices.Enumeration.DeviceInformation.FindAllAsync(selector);
				return all.Select((Windows.Devices.Enumeration.DeviceInformation d) => d.Name).Where((string n) => !string.IsNullOrWhiteSpace(n)).Distinct().OrderBy((string n) => n, StringComparer.CurrentCultureIgnoreCase).ToArray();
			});
			return t.Wait(3000) ? t.Result : Array.Empty<string>();
		}
		catch
		{
			return Array.Empty<string>();
		}
	}

	// ---- Locale -------------------------------------------------------------------------------------------------------

	public string? GetLocale(uint lctype)
	{
		try
		{
			StringBuilder sb = new StringBuilder(128);
			int n = GetLocaleInfoEx(null, lctype, sb, sb.Capacity);
			return n > 0 ? sb.ToString() : null;
		}
		catch
		{
			return null;
		}
	}

	public bool SetLocale(uint lctype, string value)
	{
		bool ok = false;
		Mutate("locale 0x" + lctype.ToString("x", CultureInfo.InvariantCulture), delegate { ok = SetLocaleInfoW(0x0400u, lctype, value); });
		return ok;
	}

	public string[] ReadShortDateFormats()
	{
		List<string> list = new List<string>();
		try
		{
			EnumDateFormatsExEx(delegate(nint fmt, uint cal, nint lp)
			{
				string? s = Marshal.PtrToStringUni(fmt);
				if (!string.IsNullOrEmpty(s) && !list.Contains(s))
				{
					list.Add(s);
				}
				return true;
			}, null, 1u, IntPtr.Zero);
		}
		catch
		{
		}
		return list.ToArray();
	}

	public void Broadcast(string area)
	{
		SettingsGuard.ThrowIfReadOnly("broadcast " + area);
		if (area == "assoc")
		{
			SHChangeNotify(0x08000000, 0, IntPtr.Zero, IntPtr.Zero);
			return;
		}
		SendMessageTimeout((nint)0xFFFF, 0x001A, IntPtr.Zero, area, 0x0002u, 2000u, out _);
	}

	private delegate bool DateFmtEnumProcExEx(nint format, uint calendar, nint lparam);

	[DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
	private static extern int GetLocaleInfoEx(string? localeName, uint lctype, StringBuilder data, int cch);

	[DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "SetLocaleInfoW")]
	private static extern bool SetLocaleInfoW(uint locale, uint lctype, string data);

	[DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
	private static extern bool EnumDateFormatsExEx(DateFmtEnumProcExEx proc, string? localeName, uint flags, nint lparam);

	[DllImport("user32.dll", CharSet = CharSet.Unicode)]
	private static extern nint SendMessageTimeout(nint hwnd, uint msg, nint wParam, string lParam, uint flags, uint timeout, out nint result);

	[DllImport("shell32.dll")]
	private static extern void SHChangeNotify(int eventId, uint flags, nint item1, nint item2);

	// ---- File Explorer flags ------------------------------------------------------------------------------------------

	[StructLayout(LayoutKind.Sequential)]
	private struct SHELLSTATE
	{
		public uint Flags1;
		public uint Win95Unused;
		public uint Win95Unused2;
		public int ParamSort;
		public int SortDirection;
		public uint Version;
		public uint NotUsed;
		public uint Flags2;
	}

	[DllImport("shell32.dll")]
	private static extern void SHGetSetSettings(ref SHELLSTATE state, uint mask, [MarshalAs(UnmanagedType.Bool)] bool set);

	public void SetExplorerFlag(string flag, bool on)
	{
		Mutate("shell " + flag, delegate
		{
			// SSF_SHOWALLOBJECTS 0x1 (bit 0, fShowAllObjects), SSF_SHOWEXTENSIONS 0x2 (bit 1, fShowExtensions)
			uint mask = flag == "hidden" ? 0x1u : 0x2u;
			uint bit = flag == "hidden" ? 0x1u : 0x2u;
			SHELLSTATE st = default;
			SHGetSetSettings(ref st, mask, set: false);
			st.Flags1 = on ? (st.Flags1 | bit) : (st.Flags1 & ~bit);
			SHGetSetSettings(ref st, mask, set: true);
		});
	}

	// ---- Read-only extras ---------------------------------------------------------------------------------------------

	public string ReadMetered()
	{
		try
		{
			Windows.Networking.Connectivity.ConnectionProfile? p = Windows.Networking.Connectivity.NetworkInformation.GetInternetConnectionProfile();
			if (p == null)
			{
				return "No connection";
			}
			return p.GetConnectionCost().NetworkCostType switch
			{
				Windows.Networking.Connectivity.NetworkCostType.Unrestricted => "Not metered",
				Windows.Networking.Connectivity.NetworkCostType.Fixed => "Metered (data limit)",
				Windows.Networking.Connectivity.NetworkCostType.Variable => "Metered",
				_ => "Unknown"
			};
		}
		catch
		{
			return "Unknown";
		}
	}

	[StructLayout(LayoutKind.Sequential)]
	private struct WINHTTP_CURRENT_USER_IE_PROXY_CONFIG
	{
		[MarshalAs(UnmanagedType.Bool)]
		public bool AutoDetect;
		public nint AutoConfigUrl;
		public nint Proxy;
		public nint ProxyBypass;
	}

	[DllImport("winhttp.dll", SetLastError = true)]
	private static extern bool WinHttpGetIEProxyConfigForCurrentUser(ref WINHTTP_CURRENT_USER_IE_PROXY_CONFIG config);

	[DllImport("kernel32.dll")]
	private static extern nint GlobalFree(nint mem);

	public string ReadProxy()
	{
		WINHTTP_CURRENT_USER_IE_PROXY_CONFIG c = default;
		try
		{
			if (!WinHttpGetIEProxyConfigForCurrentUser(ref c))
			{
				return "Unknown";
			}
			string? proxy = c.Proxy != IntPtr.Zero ? Marshal.PtrToStringUni(c.Proxy) : null;
			string? script = c.AutoConfigUrl != IntPtr.Zero ? Marshal.PtrToStringUni(c.AutoConfigUrl) : null;
			if (!string.IsNullOrEmpty(proxy))
			{
				return "Manual: " + proxy;
			}
			if (!string.IsNullOrEmpty(script))
			{
				return "Setup script";
			}
			return c.AutoDetect ? "Automatically detect" : "Off";
		}
		catch
		{
			return "Unknown";
		}
		finally
		{
			foreach (nint p in new[] { c.AutoConfigUrl, c.Proxy, c.ProxyBypass })
			{
				if (p != IntPtr.Zero)
				{
					GlobalFree(p);
				}
			}
		}
	}

	[DllImport("shlwapi.dll", CharSet = CharSet.Unicode)]
	private static extern int AssocQueryStringW(uint flags, int str, string assoc, string? extra, StringBuilder? output, ref uint length);

	private static string Assoc(string assoc, bool protocol)
	{
		try
		{
			uint flags = protocol ? 0x1000u : 0u;
			uint len = 0;
			AssocQueryStringW(flags, 4, assoc, "open", null, ref len);
			if (len == 0 || len > 2048)
			{
				return "Not set";
			}
			StringBuilder sb = new StringBuilder((int)len);
			return AssocQueryStringW(flags, 4, assoc, "open", sb, ref len) == 0 && sb.Length > 0 ? sb.ToString() : "Not set";
		}
		catch
		{
			return "Not set";
		}
	}

	public (string Kind, string App)[] ReadDefaultApps()
	{
		return new (string, string)[]
		{
			("Web browser", Assoc("http", protocol: true)),
			("Email", Assoc("mailto", protocol: true)),
			("PDF", Assoc(".pdf", protocol: false)),
			("Photos", Assoc(".jpg", protocol: false)),
			("Music", Assoc(".mp3", protocol: false)),
			("Video", Assoc(".mp4", protocol: false))
		};
	}

	public StartupAppInfo[] ReadStartupApps()
	{
		List<StartupAppInfo> list = new List<StartupAppInfo>();
		HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		void FromKey(RegistryKey hive, string path, string approved, string source)
		{
			try
			{
				using RegistryKey? k = hive.OpenSubKey(path);
				using RegistryKey? a = hive.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\" + approved);
				foreach (string name in k?.GetValueNames() ?? Array.Empty<string>())
				{
					if (string.IsNullOrWhiteSpace(name) || !seen.Add(name))
					{
						continue;
					}
					bool enabled = a?.GetValue(name) is not byte[] b || b.Length == 0 || (b[0] & 1) == 0;
					list.Add(new StartupAppInfo(name, source, enabled));
				}
			}
			catch
			{
			}
		}
		FromKey(Registry.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\Run", "Run", "Registry (user)");
		FromKey(Registry.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", "Run", "Registry (all users)");
		FromKey(Registry.LocalMachine, @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Run", "Run32", "Registry (all users, 32-bit)");
		foreach ((Environment.SpecialFolder folder, string source) in new[] { (Environment.SpecialFolder.Startup, "Startup folder"), (Environment.SpecialFolder.CommonStartup, "Startup folder (all users)") })
		{
			try
			{
				string dir = Environment.GetFolderPath(folder);
				if (!Directory.Exists(dir))
				{
					continue;
				}
				using RegistryKey? a = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\StartupFolder");
				foreach (string f in Directory.EnumerateFiles(dir))
				{
					string file = Path.GetFileName(f);
					if (file.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase) || !seen.Add(file))
					{
						continue;
					}
					bool enabled = a?.GetValue(file) is not byte[] b || b.Length == 0 || (b[0] & 1) == 0;
					list.Add(new StartupAppInfo(Path.GetFileNameWithoutExtension(file), source, enabled));
				}
			}
			catch
			{
			}
		}
		return list.OrderBy((StartupAppInfo s) => s.Name, StringComparer.CurrentCultureIgnoreCase).ToArray();
	}

	public string ReadGpu()
	{
		try
		{
			Guid iid = typeof(IDXGIFactory1).GUID;
			if (CreateDXGIFactory1(ref iid, out object o) != 0 || o is not IDXGIFactory1 f)
			{
				return "Unknown";
			}
			List<string> names = new List<string>();
			for (uint i = 0; i < 8; i++)
			{
				if (f.EnumAdapters1(i, out IDXGIAdapter1 a) != 0 || a == null)
				{
					break;
				}
				if (a.GetDesc1(out DXGI_ADAPTER_DESC1 d) == 0 && (d.Flags & 2u) == 0 && !string.IsNullOrWhiteSpace(d.Description))
				{
					string vram = d.DedicatedVideoMemory > 0 ? " (" + Math.Round(d.DedicatedVideoMemory / 1073741824.0) + " GB)" : "";
					if (!names.Any((string n) => n.StartsWith(d.Description, StringComparison.Ordinal)))
					{
						names.Add(d.Description.Trim() + vram);
					}
				}
				Marshal.ReleaseComObject(a);
			}
			Marshal.ReleaseComObject(f);
			return names.Count == 0 ? "Unknown" : string.Join(", ", names);
		}
		catch
		{
			return "Unknown";
		}
	}

	[StructLayout(LayoutKind.Sequential)]
	private struct SHQUERYRBINFO
	{
		public int cbSize;
		public long i64Size;
		public long i64NumItems;
	}

	[DllImport("shell32.dll", CharSet = CharSet.Unicode)]
	private static extern int SHQueryRecycleBin(string? rootPath, ref SHQUERYRBINFO info);

	public long ReadRecycleBinBytes()
	{
		try
		{
			SHQUERYRBINFO info = new SHQUERYRBINFO { cbSize = Marshal.SizeOf<SHQUERYRBINFO>() };
			return SHQueryRecycleBin(null, ref info) == 0 ? info.i64Size : -1;
		}
		catch
		{
			return -1;
		}
	}

	[DllImport("dxgi.dll")]
	private static extern int CreateDXGIFactory1(ref Guid riid, [MarshalAs(UnmanagedType.IUnknown)] out object factory);
}

[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
internal struct DXGI_ADAPTER_DESC1
{
	[MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
	public string Description;
	public uint VendorId;
	public uint DeviceId;
	public uint SubSysId;
	public uint Revision;
	public nuint DedicatedVideoMemory;
	public nuint DedicatedSystemMemory;
	public nuint SharedSystemMemory;
	public uint LuidLow;
	public int LuidHigh;
	public uint Flags;
}

[ComImport]
[Guid("770aae78-f26f-4dba-a829-253c83d1b387")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IDXGIFactory1
{
	void SetPrivateData();
	void SetPrivateDataInterface();
	void GetPrivateData();
	void GetParent();
	void EnumAdapters();
	void MakeWindowAssociation();
	void GetWindowAssociation();
	void CreateSwapChain();
	void CreateSoftwareAdapter();

	[PreserveSig]
	int EnumAdapters1(uint index, out IDXGIAdapter1 adapter);
}

[ComImport]
[Guid("29038f61-3839-4626-91fd-086879011a05")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IDXGIAdapter1
{
	void SetPrivateData();
	void SetPrivateDataInterface();
	void GetPrivateData();
	void GetParent();
	void EnumOutputs();
	void GetDesc();
	void CheckInterfaceSupport();

	[PreserveSig]
	int GetDesc1(out DXGI_ADAPTER_DESC1 desc);
}

// The monitors' own names and exact refresh rates (QueryDisplayConfig), keyed by GDI device ("\\.\DISPLAY1"). Read-only.
internal static class DisplayNames
{
	[StructLayout(LayoutKind.Sequential)]
	private struct LUID
	{
		public uint Low;
		public int High;
	}

	[StructLayout(LayoutKind.Sequential)]
	private struct PATH_SOURCE
	{
		public LUID AdapterId;
		public uint Id;
		public uint ModeInfoIdx;
		public uint StatusFlags;
	}

	[StructLayout(LayoutKind.Sequential)]
	private struct PATH_TARGET
	{
		public LUID AdapterId;
		public uint Id;
		public uint ModeInfoIdx;
		public int OutputTechnology;
		public int Rotation;
		public int Scaling;
		public uint RefreshNum;
		public uint RefreshDen;
		public int ScanLineOrdering;
		public int TargetAvailable;
		public uint StatusFlags;
	}

	[StructLayout(LayoutKind.Sequential)]
	private struct PATH_INFO
	{
		public PATH_SOURCE Source;
		public PATH_TARGET Target;
		public uint Flags;
	}

	[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
	private struct SOURCE_NAME
	{
		public int Type;
		public int Size;
		public LUID AdapterId;
		public uint Id;
		[MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
		public string GdiName;
	}

	[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
	private struct TARGET_NAME
	{
		public int Type;
		public int Size;
		public LUID AdapterId;
		public uint Id;
		public uint Flags;
		public int OutputTechnology;
		public ushort EdidManufactureId;
		public ushort EdidProductCodeId;
		public uint ConnectorInstance;
		[MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
		public string FriendlyName;
		[MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
		public string DevicePath;
	}

	[DllImport("user32.dll")]
	private static extern int GetDisplayConfigBufferSizes(uint flags, out uint paths, out uint modes);

	[DllImport("user32.dll")]
	private static extern int QueryDisplayConfig(uint flags, ref uint paths, [Out] PATH_INFO[] pathArray, ref uint modes, [Out] byte[] modeArray, nint topology);

	[DllImport("user32.dll", EntryPoint = "DisplayConfigGetDeviceInfo")]
	private static extern int GetSourceName(ref SOURCE_NAME packet);

	[DllImport("user32.dll", EntryPoint = "DisplayConfigGetDeviceInfo")]
	private static extern int GetTargetName(ref TARGET_NAME packet);

	internal static Dictionary<string, (string Friendly, int Hz)> Read()
	{
		Dictionary<string, (string, int)> map = new Dictionary<string, (string, int)>(StringComparer.OrdinalIgnoreCase);
		try
		{
			if (GetDisplayConfigBufferSizes(2u, out uint np, out uint nm) != 0 || np == 0)
			{
				return map;
			}
			PATH_INFO[] paths = new PATH_INFO[np];
			byte[] modes = new byte[Math.Max(1u, nm) * 64];
			if (QueryDisplayConfig(2u, ref np, paths, ref nm, modes, IntPtr.Zero) != 0)
			{
				return map;
			}
			for (int i = 0; i < np; i++)
			{
				PATH_INFO p = paths[i];
				SOURCE_NAME s = new SOURCE_NAME { Type = 1, Size = Marshal.SizeOf<SOURCE_NAME>(), AdapterId = p.Source.AdapterId, Id = p.Source.Id };
				if (GetSourceName(ref s) != 0 || string.IsNullOrEmpty(s.GdiName))
				{
					continue;
				}
				TARGET_NAME t = new TARGET_NAME { Type = 2, Size = Marshal.SizeOf<TARGET_NAME>(), AdapterId = p.Target.AdapterId, Id = p.Target.Id };
				string friendly = GetTargetName(ref t) == 0 ? (t.FriendlyName ?? "") : "";
				int hz = p.Target.RefreshDen > 0 ? (int)Math.Round((double)p.Target.RefreshNum / p.Target.RefreshDen) : 0;
				if (!map.ContainsKey(s.GdiName))
				{
					map[s.GdiName] = (friendly, hz);
				}
			}
		}
		catch
		{
		}
		return map;
	}
}

// WM_SETTINGCHANGE "intl" (from PC Settings' own format rows or from Windows): the culture data is read again so the
// clock, the calendar flyout and the Time & language page show the new formats.
internal static class IntlWatch
{
	internal static event Action? Changed;

	internal static void OnSettingChange(nint lParam)
	{
		try
		{
			string? area = lParam != IntPtr.Zero ? Marshal.PtrToStringUni(lParam) : null;
			if (!string.Equals(area, "intl", StringComparison.OrdinalIgnoreCase))
			{
				return;
			}
			CultureInfo.CurrentCulture.ClearCachedData();
			CultureInfo.CurrentUICulture.ClearCachedData();
			Changed?.Invoke();
		}
		catch
		{
		}
	}
}
