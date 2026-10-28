using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
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

// QA only: a settings world that records every call instead of touching Windows. Seeded from a fixture profile (this-pc,
// laptop, locked, policy-showonly, nocpl); every value, capability and launch is in memory.
internal sealed partial class FakeSettingsWorld : ISettingsWorld
{
	internal readonly string Profile;

	internal readonly List<string> Calls = new List<string>();

	internal readonly List<string> Launches = new List<string>();

	internal readonly Dictionary<uint, int> Spi = new Dictionary<uint, int>();

	internal int[] Mouse = new int[3];

	internal int Swap;

	internal int DoubleClick = 500;

	internal int Blink = 530;

	internal int MonitorAc = 15;

	internal int StandbyAc = 30;

	internal string Plan = "Balanced";

	internal int Brightness = 70;

	internal bool Sticky;

	internal bool Filter;

	internal bool Toggle;

	internal bool Toasts = true;

	internal bool LogonTaskOn = true;

	internal bool RunKeyOn;

	internal HubCaps Caps = new HubCaps();

	internal MonitorInfo[] Monitors = Array.Empty<MonitorInfo>();

	internal DateTime Now = new DateTime(2026, 10, 28, 12, 0, 0, DateTimeKind.Utc);

	internal int UiThreadId;

	// Slow reads (schtasks, DDC, power enumeration) made on the UI thread (QA asserts 0).
	internal int UiThreadSlowReads;

	private int _subscriptions;

	internal int Subscriptions => _subscriptions;

	// The SettingsPageVisibility policy of the user's PC (this-pc fixture).
	internal const string ThisPcPolicy = "hide:quiethours;powersleep;batterysaver;tabletmode;project;clipboard;devices-touchpad;mobile-devices;network-cellular;network-airplanemode;network-mobilehotspot;maps;appsforwebsites;videoplayback;yourinfo;emailandaccounts;signinoptions;workplace;otherusers;sync;gaming-gamebar;gaming-gamedvr;gaming-broadcasting;gaming-xboxnetworking;easeofaccess-magnifier;easeofaccess-highcontrast;easeofaccess-narrator;easeofaccess-closedcaptioning;easeofaccess-eyecontrol;search-permissions;cortana-windowssearch;search-moredetails;privacy;privacy-speech;privacy-speechtyping;privacy-feedback;privacy-activityhistory;privacy-location;privacy-webcam;privacy-microphone;privacy-voiceactivation;privacy-notifications;privacy-accountinfo;privacy-contacts;privacy-calendar;privacy-callhistory;privacy-email;privacy-eyetracker;privacy-tasks;privacy-messaging;privacy-radios;privacy-customdevices;privacy-backgroundapps;privacy-appdiagnostics;privacy-automaticfiledownloads;privacy-documents;privacy-pictures;privacy-documents;privacy-broadfilesystemaccess;windowsdefender;developers;windowsinsider;holographic-audio;privacy-holographic-environment;holographic-headset;holographic-management;";

	internal FakeSettingsWorld(string profile)
	{
		Profile = profile;
		UiThreadId = Environment.CurrentManagedThreadId;
		// the user's own tuned values (this-pc): speed 20, precision off, 620 ms, 6 lines, delay 0, no caret blink, 300 s
		Spi[0x0070] = 20;
		Mouse = new int[3] { 0, 0, 0 };
		DoubleClick = 620;
		Spi[0x0068] = 6;
		Spi[0x006C] = 3;
		Spi[0x0016] = 0;
		Spi[0x000A] = 31;
		Spi[0x2006] = 1;
		Spi[0x2016] = 300;
		Spi[0x1042] = 1;
		Spi[0x1020] = 0;
		Spi[0x101C] = 0;
		Spi[0x101E] = 0;
		Spi[0x005E] = 0;
		Spi[0x005F] = 0;
		Spi[0x100A] = 0;
		Blink = -1;
		Monitors = new[]
		{
			new MonitorInfo("DISPLAY1", 1920, 1080, 144, true),
			new MonitorInfo("DISPLAY2", 1920, 1080, 60, false)
		};
		string policy = ThisPcPolicy;
		bool cpl = true;
		switch (profile)
		{
		case "laptop":
			policy = string.Empty;
			Caps.HasWifi = true;
			Caps.HasBluetooth = true;
			Caps.HasRadios = true;
			Caps.HasBattery = true;
			Caps.SleepAllowed = true;
			Caps.HibernateAllowed = true;
			Caps.LidPresent = true;
			Caps.PowerButtonPresent = true;
			Caps.Digitizer = true;
			Monitors = new[] { new MonitorInfo("DISPLAY1", 1920, 1200, 60, true) };
			break;
		case "policy-showonly":
			policy = "showonly:display;sound;about;mousetouchpad";
			break;
		case "nocpl":
			policy = string.Empty;
			cpl = false;
			break;
		case "noprivacy":
			policy = "showonly:display;sound";
			break;
		}
		Caps.Pc = SystemSearchBuilder.PcCaps.FromPolicy(policy, touchpad: profile == "laptop", touch: false);
		Caps.NoControlPanel = !cpl;
		Caps.FastStartup = false;
		Caps.PowerButtonPresent |= profile == "laptop";
		InitStage2(profile);
	}

	private void Log(string line)
	{
		lock (Calls)
		{
			Calls.Add(line);
		}
	}

	private void Slow(string what)
	{
		if (Environment.CurrentManagedThreadId == UiThreadId)
		{
			Interlocked.Increment(ref UiThreadSlowReads);
			Log("UI-THREAD " + what);
		}
	}

	internal string[] TakeCalls()
	{
		lock (Calls)
		{
			string[] a = Calls.ToArray();
			Calls.Clear();
			return a;
		}
	}

	public string Name => "fake:" + Profile;

	public DateTime UtcNow => Now;

	public int? SpiGet(uint action)
	{
		Log("get spi 0x" + action.ToString("x", CultureInfo.InvariantCulture));
		return Spi.TryGetValue(action, out int v) ? v : null;
	}

	public int[]? SpiGetMouse()
	{
		Log("get spi 0x3");
		return (int[])Mouse.Clone();
	}

	public int GetSystemMetrics(int index)
	{
		Log("get metric " + index);
		return index == 23 ? Swap : 0;
	}

	public int GetDoubleClickTime()
	{
		Log("get doubleclick");
		return DoubleClick;
	}

	public int GetCaretBlinkTime()
	{
		Log("get caretblink");
		return Blink;
	}

	public int ReadMonitorTimeoutAcMin()
	{
		Slow("power read");
		Log("get power video ac");
		return MonitorAc;
	}

	public int ReadStandbyTimeoutAcMin()
	{
		Slow("power read");
		Log("get power sleep ac");
		return StandbyAc;
	}

	public string ReadActivePowerPlan()
	{
		Slow("power plan");
		Log("get power plan");
		return Plan;
	}

	public int ReadBrightness()
	{
		Slow("ddc");
		Log("get ddc DISPLAY1");
		return Brightness;
	}

	public bool ReadStickyKeys()
	{
		Log("get spi 0x3a");
		return Sticky;
	}

	public bool ReadFilterKeys()
	{
		Log("get spi 0x32");
		return Filter;
	}

	public bool ReadToggleKeys()
	{
		Log("get spi 0x34");
		return Toggle;
	}

	public bool ReadToastsEnabled()
	{
		Log("get toasts");
		return Toasts;
	}

	public string[] ReadKeyboardLayouts()
	{
		Log("get layouts");
		return new[] { "English (United States) – US", "Greek (Greece) – Greek" };
	}

	public string ReadLayoutHotkey()
	{
		Log("get layout hotkey");
		return RealSettingsWorld.HotkeyName("1");
	}

	public HubCaps ReadCaps()
	{
		Log("get caps");
		return Caps;
	}

	public MonitorInfo[] ReadMonitors()
	{
		Slow("monitors");
		Log("get monitors");
		return Monitors;
	}

	public string ReadDefaultAudio(bool capture)
	{
		Slow("audio");
		Log("get audio default " + (capture ? "capture" : "render"));
		return capture ? "Microphone (USB Audio)" : "Speakers (Realtek(R) Audio)";
	}

	public (bool Extensions, bool Hidden) ReadExplorerFlags()
	{
		Log("get shellflags");
		return (ExtShown, HiddenShown);
	}

	public HubNetInfo ReadNetwork()
	{
		Slow("network");
		Log("get network");
		return new HubNetInfo("Ethernet", "Network 2", "Connected, internet access", "192.168.1.20", "192.168.1.1", "192.168.1.1");
	}

	public DriveSpace[] ReadDrives()
	{
		Log("get drives");
		return new[] { new DriveSpace("C:", "Windows", 214748364800L, 511101108224L), new DriveSpace("D:", "Data", 805306368000L, 1000204886016L) };
	}

	public string ReadUpdateStatus()
	{
		Log("get wu");
		return "Updates are managed by policy on this PC";
	}

	public void WriteMonitorTimeoutAcMin(int minutes)
	{
		Log("set power video ac " + minutes * 60);
		MonitorAc = minutes;
	}

	public void WriteStandbyTimeoutAcMin(int minutes)
	{
		Log("set power sleep ac " + minutes * 60);
		StandbyAc = minutes;
	}

	public void SetBrightness(int percent)
	{
		Log("set ddc DISPLAY1 " + percent);
		Brightness = percent;
	}

	public void SetStickyKeys(bool on)
	{
		Log("set spi 0x3b on=" + (on ? 1 : 0) + " ini=3");
		Sticky = on;
	}

	public void SetFilterKeys(bool on)
	{
		Log("set spi 0x33 on=" + (on ? 1 : 0) + " ini=3");
		Filter = on;
	}

	public void SetToggleKeys(bool on)
	{
		Log("set spi 0x35 on=" + (on ? 1 : 0) + " ini=3");
		Toggle = on;
	}

	public void SetWin81Sounds(bool on)
	{
		Log("set soundscheme " + (on ? "win81" : "revert"));
	}

	public void SetWin81ShellIcons(bool on)
	{
		Log("set shellicons " + (on ? "win81" : "restore"));
	}

	public Task SetLockScreenAsync(string path)
	{
		Log("set lockscreen " + Path.GetFileName(path));
		return Task.CompletedTask;
	}

	public bool ReadLogonTask()
	{
		Slow("schtasks");
		Log("get logontask");
		return LogonTaskOn;
	}

	public bool ReadRunKey()
	{
		Log("get runkey");
		return RunKeyOn;
	}

	public bool SetLogonTask(bool on)
	{
		Log("set logontask " + on);
		LogonTaskOn = on;
		return true;
	}

	public void SetRunKey(bool on)
	{
		Log("set runkey " + on);
		RunKeyOn = on;
	}

	public void Launch(string file, string? args, bool uri, bool elevate)
	{
		string line = (elevate ? "runas " : "") + file + (string.IsNullOrEmpty(args) ? "" : " " + args);
		Log("launch " + line);
		lock (Launches)
		{
			Launches.Add(line);
		}
	}

	public bool SendChord(params byte[] vks)
	{
		Log("chord " + string.Join("+", vks));
		return true;
	}
}

// --pchubtest <outDir> [light|dark|rest|halloween|christmas|glass] [stage1|stage2|stage3|all]
// Offscreen QA of the PC Settings hub against the fake settings world. Read-only: SettingsStore.ReadOnlyDiagnostics is
// set before anything else (App.cs), settings come from a clone of the real ones (DiagnosticsOverride), the theme is
// forced for this process only, windows stay at (-6000,-6000) without activation, the Windows entries come from a
// read-only copy of the system index cache. Writes results.txt (PASS|FAIL name detail), trace.txt, timings.csv and PNGs
// into outDir; exits 0 (all pass), 1 (a failure), 2 (bad arguments or a refused folder), 3 (deadline).
internal static partial class PcHubDiagnostics
{
	private const int DeadlineMs = 180000;

	private static App _app = null!;

	private static string _out = "";

	private static readonly List<string> Results = new List<string>();

	private static readonly List<string> Trace = new List<string>();

	private static readonly List<string> Timings = new List<string>();

	private static FakeSettingsWorld _world = null!;

	private static string[] _looks = Array.Empty<string>();

	private static int _finished;

	// stage 2 checks (native controls, journal, undo) run unless only stage1 was asked for
	private static bool _stage2 = true;

	private static readonly string[] AllLooks = new string[6] { "light", "dark", "rest", "halloween", "christmas", "glass" };

	public static void Begin(App app, string[] args)
	{
		_app = app;
		app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
		bool ro = SettingsStore.ReadOnlyDiagnostics;
		int idx = Array.IndexOf(args, "--pchubtest");
		if (idx < 0)
		{
			idx = Array.IndexOf(args, "--pcsettingstest");
		}
		string[] keywords = AllLooks.Concat(new[] { "stage1", "stage2", "stage3", "all" }).ToArray();
		string? outDir = idx >= 0 && idx + 1 < args.Length && !args[idx + 1].StartsWith("--", StringComparison.Ordinal) && !keywords.Contains(args[idx + 1]) ? args[idx + 1] : null;
		string state = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Win81Layer");
		if (!ro || outDir == null || Inside(outDir, state))
		{
			app.Shutdown(2);
			return;
		}
		_out = Path.GetFullPath(outDir);
		try
		{
			Directory.CreateDirectory(_out);
		}
		catch
		{
			app.Shutdown(2);
			return;
		}
		_looks = AllLooks.Where(args.Contains).ToArray();
		bool anyStage = args.Any((string a) => a == "stage1" || a == "stage2" || a == "stage3" || a == "all");
		_stage2 = !anyStage || args.Contains("stage2") || args.Contains("stage3") || args.Contains("all");
		_stage3 = !anyStage || args.Contains("stage3") || args.Contains("all");
		if (_looks.Length == 0)
		{
			_looks = AllLooks;
		}
		Thread deadline = new Thread(delegate ()
		{
			Thread.Sleep(DeadlineMs);
			try
			{
				Flush("deadline");
			}
			catch
			{
			}
			Environment.Exit(3);
		})
		{
			IsBackground = true,
			Name = "PcHub.Deadline"
		};
		deadline.Start();
		app.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, (Action)delegate
		{
			int code = 1;
			try
			{
				code = Run(ro) ? 0 : 1;
			}
			catch (Exception ex)
			{
				Check("harness", false, ex.ToString().Replace("\r", " ").Replace("\n", " | "));
				code = 1;
			}
			Flush("done");
			app.Shutdown(code);
		});
	}

	private static bool Inside(string path, string root)
	{
		string p = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar);
		string r = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar);
		return string.Equals(p, r, StringComparison.OrdinalIgnoreCase) || p.StartsWith(r + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
	}

	private static void Check(string name, bool ok, string detail)
	{
		lock (Results)
		{
			Results.Add((ok ? "PASS" : "FAIL") + "|" + name + "|" + detail.Replace("\r", " ").Replace("\n", " "));
		}
		lock (Trace)
		{
			Trace.Add("== " + name);
			if (_world != null)
			{
				foreach (string c in _world.TakeCalls())
				{
					Trace.Add("   " + c);
				}
			}
		}
	}

	private static void Time(string name, double ms)
	{
		lock (Timings)
		{
			Timings.Add(name + "," + ms.ToString("0.00", CultureInfo.InvariantCulture));
		}
	}

	private static void Flush(string why)
	{
		if (Interlocked.Exchange(ref _finished, 1) != 0 && why == "deadline")
		{
			return;
		}
		lock (Results)
		{
			File.WriteAllLines(Path.Combine(_out, "results.txt"), Results.Concat(new[] { "INFO|end|" + why + " " + Results.Count((string r) => r.StartsWith("PASS", StringComparison.Ordinal)) + " pass, " + Results.Count((string r) => r.StartsWith("FAIL", StringComparison.Ordinal)) + " fail" }));
		}
		lock (Trace)
		{
			File.WriteAllLines(Path.Combine(_out, "trace.txt"), Trace);
		}
		lock (Timings)
		{
			File.WriteAllLines(Path.Combine(_out, "timings.csv"), new[] { "name,ms" }.Concat(Timings));
		}
	}

	// ---- the run --------------------------------------------------------------------------------------------------

	private static AppSettings _real = new AppSettings();

	private static bool Run(bool roAtEntry)
	{
		Check("ro-first", roAtEntry && Application.Current.Windows.Count == 0, $"ReadOnlyDiagnostics={roAtEntry} windows={Application.Current.Windows.Count}");
		Dictionary<string, DateTime> before = Fingerprints();
		try
		{
			_real = SettingsStore.Load().Clone();
		}
		catch
		{
			_real = new AppSettings();
		}
		_world = new FakeSettingsWorld("this-pc");
		SettingsWorld.Current = _world;
		SettingsStore.DiagnosticsOverride = NormalSettings();
		ShellTheme.ForceForTest(false);
		DarkRest.Sync();
		ShellTheme.SyncRest(force: true);
		LoadIndexFixture();
		SafetyChecks();
		ShellChecks();
		SearchChecks();
		PolicyChecks();
		LookChecks();
		PerfChecks();
		LifetimeChecks();
		if (_stage2)
		{
			Stage2Checks();
			Stage2Renders();
		}
		if (_stage3)
		{
			Stage3Checks();
			Stage3Renders();
		}
		Check("real-writes-zero", RealSettingsWorld.WriteCount == 0, $"real writes={RealSettingsWorld.WriteCount} refused={SettingsGuard.Refusals}");
		Check("launch-recorded", SettingsLink.ProcessStarts == 0 && _world.Launches.Count > 0, $"process starts={SettingsLink.ProcessStarts} recorded launches={_world.Launches.Count}");
		Dictionary<string, DateTime> after = Fingerprints();
		List<string> changed = before.Where((KeyValuePair<string, DateTime> kv) => !after.TryGetValue(kv.Key, out DateTime t) || t != kv.Value).Select((KeyValuePair<string, DateTime> kv) => Path.GetFileName(kv.Key)).Concat(after.Keys.Except(before.Keys).Select(Path.GetFileName)!).ToList();
		Check("files-unchanged", changed.Count == 0, changed.Count == 0 ? before.Count + " state files unchanged" : "changed: " + string.Join(", ", changed));
		return Results.All((string r) => !r.StartsWith("FAIL", StringComparison.Ordinal));
	}

	private static Dictionary<string, DateTime> Fingerprints()
	{
		Dictionary<string, DateTime> d = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
		string root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Win81Layer");
		void Add(string p)
		{
			try
			{
				if (File.Exists(p))
				{
					d[p] = File.GetLastWriteTimeUtc(p);
				}
			}
			catch
			{
			}
		}
		Add(SettingsStore.SettingsPath);
		Add(SystemSearchIndex.CachePath);
		Add(Path.Combine(root, "pc-settings-journal.json"));
		try
		{
			string stateDir = Path.Combine(root, "state");
			if (Directory.Exists(stateDir))
			{
				foreach (string f in Directory.EnumerateFiles(stateDir))
				{
					Add(f);
				}
			}
		}
		catch
		{
		}
		return d;
	}

	private static AppSettings NormalSettings()
	{
		AppSettings s = _real.Clone();
		s.SeasonalActive = "";
		s.DesktopCompositionMode = "windows81";
		s.PcSettingsBounds = "";
		return s;
	}

	private static AppSettings LookSettings(string look)
	{
		AppSettings s = NormalSettings();
		switch (look)
		{
		case "rest":
		case "halloween":
		case "christmas":
		{
			SeasonPreset? p = SeasonalTheme.Find(look == "rest" ? SeasonalTheme.Rest : look);
			if (p != null)
			{
				s.StartBgMode = "pattern";
				s.StartPattern = p.Pattern;
				s.StartBgColor = p.Base;
				s.StartAccentColor = p.Accent;
				s.SeasonalActive = p.Id;
			}
			break;
		}
		case "glass":
			s.DesktopCompositionMode = "windows7-aero";
			s.DeskCompTransparency = true;
			break;
		}
		return s;
	}

	private static void UseLook(string look)
	{
		SettingsStore.DiagnosticsOverride = LookSettings(look);
		ShellTheme.ForceForTest(look == "dark" || look == "rest");
		DarkRest.Sync();
		ShellTheme.SyncRest(force: true);
	}

	private static SystemSearchIndex? _index;

	private static void LoadIndexFixture()
	{
		try
		{
			string copy = Path.Combine(_out, "fixture-index.json");
			if (File.Exists(SystemSearchIndex.CachePath))
			{
				File.Copy(SystemSearchIndex.CachePath, copy, overwrite: true);
				File.SetAttributes(copy, FileAttributes.ReadOnly);
				SystemSearchData? data = SystemSearchIndex.LoadFrom(copy);
				if (data != null)
				{
					_index = SystemSearchIndex.FromData(data, "qa", 0.0, compact: true);
				}
				try
				{
					File.SetAttributes(copy, FileAttributes.Normal);
				}
				catch
				{
				}
			}
			if (_index == null)
			{
				SystemSearchIndex.Ensure(synchronous: true);
				_index = SystemSearchIndex.Current;
			}
			SystemSearchIndex.QaSet(_index);
			Check("index-fixture", _index != null && _index.Hits.Count > 100, _index == null ? "no index" : $"{_index.Hits.Count} entries ({_index.Source})");
		}
		catch (Exception ex)
		{
			Check("index-fixture", false, ex.Message);
		}
	}

	private static PcSettingsWindow NewWindow()
	{
		PcSettingsWindow w = new PcSettingsWindow(null!)
		{
			Left = -6000.0,
			Top = -6000.0,
			ShowActivated = false,
			ShowInTaskbar = false,
			Width = 980.0,
			Height = 760.0
		};
		w.QaUseCaps(_world.Caps);
		return w;
	}

	private static void Show(PcSettingsWindow w)
	{
		if (!w.IsVisible)
		{
			w.Show();
		}
		Pump();
	}

	private static void Pump()
	{
		DispatcherFrame frame = new DispatcherFrame();
		Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, (Action)delegate { frame.Continue = false; });
		Dispatcher.PushFrame(frame);
	}

	private static object? Invoke(object target, string name, params object[] args)
	{
		MethodInfo m = target.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public) ?? throw new MissingMethodException(name);
		return m.Invoke(target, args);
	}

	// ---- safety -----------------------------------------------------------------------------------------------------

	private static void SafetyChecks()
	{
		bool flag = false;
		bool threw = false;
		try
		{
			RealSettingsWorld.Mutate("probe", delegate { flag = true; });
		}
		catch (InvalidOperationException)
		{
			threw = true;
		}
		bool swapRule = !SettingsWorld.SwapAllowed(_world, readOnly: false) && SettingsWorld.SwapAllowed(_world, readOnly: true) && SettingsWorld.SwapAllowed(RealSettingsWorld.Instance, readOnly: false);
		Check("world-guard", threw && !flag && swapRule, $"mutate threw={threw} ran={flag} swap rule={swapRule}");
		// a link opened from a row lands in the fake world's launch log only
		int starts = SettingsLink.ProcessStarts;
		int n = _world.Launches.Count;
		bool opened = SettingsLink.Open(HubCatalog.Row("mouse.properties")!.Link!, _world.Caps);
		Check("launch-path", opened && _world.Launches.Count == n + 1 && SettingsLink.ProcessStarts == starts && _world.Launches[^1] == "control.exe main.cpl",
			$"opened={opened} last='{(_world.Launches.Count > 0 ? _world.Launches[^1] : "")}' process starts={SettingsLink.ProcessStarts}");
	}

	// ---- shell and compatibility ------------------------------------------------------------------------------------

	private static void ShellChecks()
	{
		// open-direct: the tray's "Launcher settings..." builds exactly one page
		PcSettingsWindow w = NewWindow();
		int b0 = w.QaPageBuilds;
		w.OpenCategory("Launcher");
		Check("open-direct", w.QaPageBuilds - b0 == 1 && w.CurrentPage == "launcher", $"builds={w.QaPageBuilds - b0} page={w.CurrentPage}");
		// alias-map
		(string Name, string Id)[] aliases = new (string, string)[]
		{
			("PC & devices", "home"), ("Launcher", "launcher"), ("Desktop composition", "appearance/frames"), ("Accounts", "account"),
			("OneDrive", "home"), ("Search and apps", "apps"), ("Privacy", "privacy"), ("Network", "network"), ("Time and language", "time"),
			("Ease of Access", "accessibility"), ("Update and recovery", "about"), ("Control Panel", "tools"), ("Performance tools", "tools"),
			("Experiments", "launcher/experiments"), ("Display", "displays"), ("Power & sleep", "power"), ("PC info", "about"),
			("Lock screen", "appearance"), ("Start screen", "start"), ("Account picture", "account")
		};
		List<string> bad = aliases.Where(((string Name, string Id) a) => HubCatalog.Resolve(a.Name) != a.Id).Select(((string Name, string Id) a) => a.Name + "->" + HubCatalog.Resolve(a.Name)).ToList();
		Check("alias-map", bad.Count == 0, bad.Count == 0 ? aliases.Length + " names" : string.Join("; ", bad));
		(string? up, string? ur) = HubCatalog.ParseUri("win81:pcsettings?page=mouse&row=mouse.speed");
		(string? rp, string? rr) = HubCatalog.ParseUri("win81:pcsettings?row=keyboard.delay");
		Check("deep-uri", up == "mouse" && ur == "mouse.speed" && rp == "keyboard" && rr == "keyboard.delay", $"{up}/{ur} {rp}/{rr}");
		// the QA API the other suites call
		try
		{
			string dir = Path.Combine(_out, "qa-api");
			Directory.CreateDirectory(dir);
			Show(w);
			w.QaRender(Path.Combine(dir, "render.png"), "PC & devices", 980.0, 760.0);
			w.QaRenderTall(Path.Combine(dir, "tall.png"), "Launcher");
			w.QaRenderPointers(Path.Combine(dir, "pointers.png"));
			w.QaRenderSeasonalPointers(Path.Combine(dir, "seasonal-pointers.png"));
			(int tiles, string sel) = w.QaSeasonTiles();
			bool blank = new[] { "render.png", "tall.png", "pointers.png", "seasonal-pointers.png" }.Any((string f) => !NonBlank(Path.Combine(dir, f)));
			Check("qa-api", !blank && tiles == 4, $"pngs nonblank={!blank} look tiles={tiles} selected='{sel}'");
		}
		catch (Exception ex)
		{
			Check("qa-api", false, ex.Message);
		}
		// null-start: no Start screen, opens Home
		try
		{
			PcSettingsWindow n = NewWindow();
			Show(n);
			n.QaRender(Path.Combine(_out, "null-start.png"), "home", 980.0, 760.0);
			Check("null-start", n.CurrentPage == "home", "page=" + n.CurrentPage);
			n.Close();
		}
		catch (Exception ex)
		{
			Check("null-start", false, ex.Message);
		}
		EscChain();
		Shortcuts();
		TabAndLayout(w);
		w.Close();
	}

	private static bool NonBlank(string path)
	{
		try
		{
			BitmapSource b = LoadPng(path);
			byte[] px = Pixels(b);
			HashSet<int> colours = new HashSet<int>();
			for (int i = 0; i < px.Length && colours.Count < 8; i += 4 * 97)
			{
				colours.Add(px[i] | (px[i + 1] << 8) | (px[i + 2] << 16));
			}
			return colours.Count >= 3;
		}
		catch
		{
			return false;
		}
	}

	private static KeyEventArgs EscKey(UIElement target, Key key)
	{
		PresentationSource src = PresentationSource.FromVisual(target) ?? throw new InvalidOperationException("no presentation source");
		return new KeyEventArgs(Keyboard.PrimaryDevice, src, 0, key) { RoutedEvent = Keyboard.KeyDownEvent };
	}

	private static void EscChain()
	{
		PcSettingsWindow w = NewWindow();
		try
		{
			Show(w);
			w.QaRender(Path.Combine(_out, "esc-0.png"), "launcher", 980.0, 760.0);
			// 1. an open combo box: Esc closes only the combo
			w.OpenCategory("taskbar");
			Pump();
			ComboBox? combo = PcSettingsWindow.Descendants<ComboBox>(w.QaPageElement!).FirstOrDefault();
			bool comboOnly = false;
			if (combo != null)
			{
				combo.IsDropDownOpen = true;
				Pump();
				combo.RaiseEvent(EscKey(combo, System.Windows.Input.Key.Escape));
				Pump();
				comboOnly = !combo.IsDropDownOpen && w.IsVisible && w.CurrentPage == "taskbar";
			}
			// 2. search text: Esc clears it and the page comes back
			w.QaSearchBox.Text = "mouse";
			w.RunSearch("mouse");
			bool searching = w.QaSearchActive;
			w.QaSearchBox.RaiseEvent(EscKey(w.QaSearchBox, System.Windows.Input.Key.Escape));
			Pump();
			bool cleared = searching && !w.QaSearchActive && w.QaSearchBox.Text.Length == 0 && w.CurrentPage == "taskbar";
			// 3. a drill-in: Esc goes back
			w.OpenCategory("appearance");
			w.OpenCategory("Pointers");
			bool drilled = w.CurrentPage == "appearance/pointers";
			w.RaiseEvent(EscKey(w, System.Windows.Input.Key.Escape));
			Pump();
			bool backed = w.CurrentPage == "appearance";
			// 4. then Esc closes
			w.RaiseEvent(EscKey(w, System.Windows.Input.Key.Escape));
			Pump();
			bool closed = !w.IsVisible;
			Check("esc-chain", comboOnly && cleared && drilled && backed && closed, $"combo={comboOnly} search={cleared} drill={drilled}->{backed} close={closed}");
		}
		catch (Exception ex)
		{
			Check("esc-chain", false, ex.Message);
		}
		finally
		{
			if (w.IsVisible)
			{
				w.Close();
			}
		}
	}

	private static void Shortcuts()
	{
		PcSettingsWindow w = NewWindow();
		try
		{
			Show(w);
			w.QaRender(Path.Combine(_out, "shortcuts-0.png"), "home", 980.0, 760.0);
			List<string> notes = new List<string>();
			bool ok = true;
			bool f = w.HandleShortcut(System.Windows.Input.Key.F, ModifierKeys.Control, inText: false);
			notes.Add("ctrl+f=" + f);
			ok &= f;
			w.OpenCategory("mouse");
			bool slash = w.HandleShortcut(System.Windows.Input.Key.OemQuestion, ModifierKeys.None, inText: false);
			bool slashInText = !w.HandleShortcut(System.Windows.Input.Key.OemQuestion, ModifierKeys.None, inText: true);
			notes.Add("slash=" + slash + "/" + slashInText);
			ok &= slash && slashInText;
			bool typed = w.TypeToSearch("m");
			notes.Add("type=" + typed + " text='" + w.QaSearchBox.Text + "'");
			ok &= typed && w.QaSearchBox.Text == "m";
			w.EscapeStep();
			w.OpenCategory("keyboard");
			w.OpenCategory("sound");
			w.HandleShortcut(System.Windows.Input.Key.Left, ModifierKeys.Alt, inText: false);
			bool back = w.CurrentPage == "keyboard";
			w.HandleShortcut(System.Windows.Input.Key.Right, ModifierKeys.Alt, inText: false);
			bool fwd = w.CurrentPage == "sound";
			notes.Add($"alt+left={back} alt+right={fwd}");
			ok &= back && fwd;
			w.HandleShortcut(System.Windows.Input.Key.D1, ModifierKeys.Control, inText: false);
			bool home = w.CurrentPage == "home";
			List<string> visible = w.VisibleEntries();
			w.HandleShortcut(System.Windows.Input.Key.D9, ModifierKeys.Control, inText: false);
			bool ninth = visible.Count >= 9 && w.CurrentPage == visible[8];
			notes.Add($"ctrl+1={home} ctrl+9={ninth} ({(visible.Count >= 9 ? visible[8] : "-")})");
			ok &= home && ninth;
			w.HandleShortcut(System.Windows.Input.Key.Back, ModifierKeys.None, inText: false);
			bool bs = w.CurrentPage == "home";
			notes.Add("backspace=" + bs);
			ok &= bs;
			Check("shortcuts", ok, string.Join(" ", notes));
		}
		catch (Exception ex)
		{
			Check("shortcuts", false, ex.Message);
		}
		finally
		{
			w.Close();
		}
	}

	// Every interactive control of every page is a tab stop with a name and the Metro focus visual; the layout holds at
	// the minimum size; rows are 40 / 52 DIP; the sidebar fits at 980x760.
	private static void TabAndLayout(PcSettingsWindow w)
	{
		List<string> bad = new List<string>();
		int controls = 0;
		Style? focus = Application.Current.TryFindResource("Metro81.Focus") as Style;
		foreach (string page in Pages())
		{
			w.QaRender(Path.Combine(_out, "tab-" + page.Replace('/', '-') + ".png"), page, 980.0, 760.0);
			if (w.QaPageElement is not DependencyObject root)
			{
				continue;
			}
			foreach (Control c in PcSettingsWindow.Descendants<Control>(root))
			{
				if (c.TemplatedParent != null || !(c is ButtonBase || c is ComboBox || c is Slider || c is TextBox) || !c.IsVisible)
				{
					continue;
				}
				controls++;
				string name = AutomationProperties.GetName(c);
				bool tab = c.Focusable && KeyboardNavigation.GetIsTabStop(c);
				if (string.IsNullOrWhiteSpace(name) || !tab || c.FocusVisualStyle == null)
				{
					bad.Add(page + ":" + c.GetType().Name + "'" + name + "' tab=" + tab + " focus=" + (c.FocusVisualStyle != null));
				}
			}
		}
		Check("tab-reach", bad.Count == 0 && controls > 50, $"{controls} interactive controls" + (bad.Count == 0 ? "" : "; " + string.Join("; ", bad.Take(12))));
		// layout at the minimum size and at the default
		w.QaRender(Path.Combine(_out, "layout-780x520.png"), "launcher", 780.0, 520.0);
		ScrollViewer? page780 = w.QaPageElement as ScrollViewer;
		bool noHorizontal = page780 != null && page780.ScrollableWidth <= 0.5 && page780.ComputedHorizontalScrollBarVisibility != Visibility.Visible;
		w.QaRender(Path.Combine(_out, "layout-980x760.png"), "mouse", 980.0, 760.0);
		ScrollViewer? navScroll = PcSettingsWindow.Descendants<ScrollViewer>(w.QaNav).FirstOrDefault();
		bool sidebarFits = navScroll != null && navScroll.ScrollableHeight <= 0.5;
		double column = 0.0;
		if (w.QaPageElement is ScrollViewer sv && sv.Content is Border { Child: StackPanel col })
		{
			column = col.ActualWidth;
		}
		FrameworkElement? one = w.QaRowElement("mouse.speed");
		w.QaRender(Path.Combine(_out, "layout-launcher.png"), "launcher", 980.0, 760.0);
		FrameworkElement? two = w.QaRowElement("launcher.lightrender");
		double h1 = one?.ActualHeight ?? -1;
		double h2 = two?.ActualHeight ?? -1;
		Check("layout", noHorizontal && sidebarFits && column <= 640.5 && column > 300 && Math.Abs(h1 - 52) < 0.5 && Math.Abs(h2 - 52) < 0.5 && RowHeightsOk(w),
			$"no horizontal at 780x520={noHorizontal} sidebar fits={sidebarFits} (scrollable {navScroll?.ScrollableHeight:0.#}) column={column:0.#} two-line rows {h1:0.#}/{h2:0.#} one-line={_oneLine}");
	}

	private static string _oneLine = "";

	private static bool RowHeightsOk(PcSettingsWindow w)
	{
		w.QaRender(Path.Combine(_out, "layout-taskbar.png"), "taskbar", 980.0, 760.0);
		FrameworkElement? row = w.QaRowElement("taskbar.autohide");
		double h = row?.ActualHeight ?? -1;
		_oneLine = h.ToString("0.#", CultureInfo.InvariantCulture);
		return Math.Abs(h - 40) < 0.5;
	}

	private static IEnumerable<string> Pages()
	{
		return HubCatalog.Pages.Where((HubPage p) => p.Id != "recent" && p.Id != "touchpad").Select((HubPage p) => p.Id);
	}

	// ---- search -----------------------------------------------------------------------------------------------------

	private static void SearchChecks()
	{
		PcSettingsWindow w = NewWindow();
		int revision = SystemSearchIndex.Revision;
		try
		{
			Show(w);
			w.QaRender(Path.Combine(_out, "search-0.png"), "home", 980.0, 760.0);
			string Top(string q)
			{
				List<PcSettingsWindow.HubResult> r = w.Rank(q);
				return r.Count == 0 ? "-" : r[0].Hit.Id;
			}
			List<string> TopN(string q, int n) => w.Rank(q).Take(n).Select((PcSettingsWindow.HubResult r) => r.Hit.Id).ToList();
			void Expect(string name, string q, Func<List<PcSettingsWindow.HubResult>, bool> ok)
			{
				List<PcSettingsWindow.HubResult> r = w.Rank(q);
				Check(name, ok(r), "'" + q + "' -> " + string.Join(", ", r.Take(5).Select((PcSettingsWindow.HubResult x) => x.Hit.Id + "(" + x.Score + ")")));
			}
			Expect("search-greek-speed", "ποντίκι ταχύτητα", (List<PcSettingsWindow.HubResult> r) => r.Count > 0 && r[0].Hit.Id == "hub:mouse.speed");
			string latin = GreekLayout.ToLatinKeys("ποντίκι");
			Expect("search-wrong-layout", latin, (List<PcSettingsWindow.HubResult> r) => r.Take(3).Any((PcSettingsWindow.HubResult x) => x.Hit.Id == "hub:mouse" || x.PageId == "mouse"));
			Expect("search-double-click", "double click", (List<PcSettingsWindow.HubResult> r) => r.Count > 0 && r[0].Hit.Id == "hub:mouse.doubleclick");
			Expect("search-double-click-el", "διπλό κλικ", (List<PcSettingsWindow.HubResult> r) => r.Count > 0 && r[0].Hit.Id == "hub:mouse.doubleclick");
			Expect("search-no-accent", "ηχος", (List<PcSettingsWindow.HubResult> r) => r.Count > 0 && r[0].Hit.Id == "hub:sound");
			Expect("search-keyboard-repeat", "keyboard repeat", (List<PcSettingsWindow.HubResult> r) => r.Count > 0 && r[0].Hit.Id == "hub:keyboard.delay");
			Expect("search-power-plan", "power plan", (List<PcSettingsWindow.HubResult> r) => r.Count > 0 && r[0].Hit.Id == "hub:power.plan");
			Expect("search-mmsys", "mmsys", (List<PcSettingsWindow.HubResult> r) => r.Take(2).Any((PcSettingsWindow.HubResult x) => x.Hit.Id == "hub:sound.panel" || (x.Group == PcSettingsWindow.HubResultGroup.ControlPanel && x.Hit.Name.Contains("Sound", StringComparison.OrdinalIgnoreCase))));
			// Device Manager is a Control Panel item in this PC's index; the administrative tools are Tool entries
			Expect("search-tool", "device manager", (List<PcSettingsWindow.HubResult> r) => r.Any((PcSettingsWindow.HubResult x) => x.Group != PcSettingsWindow.HubResultGroup.Hub && x.Hit.Name.Contains("Device Manager", StringComparison.OrdinalIgnoreCase)));
			Expect("search-tool-admin", "event viewer", (List<PcSettingsWindow.HubResult> r) => r.Any((PcSettingsWindow.HubResult x) => x.Group == PcSettingsWindow.HubResultGroup.Tools));
			// a page hidden by policy never shows
			HashSet<string> hidden = new HashSet<string>(new[] { "network-airplanemode", "powersleep", "otherusers", "signinoptions", "yourinfo" }, StringComparer.OrdinalIgnoreCase);
			List<string> leaks = new List<string>();
			foreach (string q in new[] { "airplane", "sleep", "other users", "sign-in options", "your info", "πτήσης" })
			{
				foreach (PcSettingsWindow.HubResult r in w.Rank(q))
				{
					if (r.Group != PcSettingsWindow.HubResultGroup.Hub && SettingsLink.SettingsPageName(r.Hit.Target) is string page && hidden.Contains(page))
					{
						leaks.Add(q + ":" + r.Hit.Target);
					}
				}
			}
			Check("search-hidden-pages", leaks.Count == 0, leaks.Count == 0 ? "no hidden page in the results" : string.Join(", ", leaks.Take(6)));
			// the Sound page row and ms-settings:sound are one row
			List<PcSettingsWindow.HubResult> sound = w.Rank("sound settings");
			bool merged = sound.Any((PcSettingsWindow.HubResult r) => r.RowId == "sound.settings") && !sound.Any((PcSettingsWindow.HubResult r) => r.Group != PcSettingsWindow.HubResultGroup.Hub && string.Equals(r.Hit.Target, "ms-settings:sound", StringComparison.OrdinalIgnoreCase));
			Check("search-merge", merged, string.Join(", ", sound.Take(6).Select((PcSettingsWindow.HubResult r) => r.Hit.Id)));
			// before the index is ready: hub rows only, then a requery
			SystemSearchIndex.QaSet(null);
			List<PcSettingsWindow.HubResult> early = w.Rank("mouse");
			bool hubOnly = early.Count > 0 && early.All((PcSettingsWindow.HubResult r) => r.Group == PcSettingsWindow.HubResultGroup.Hub || r.Group == PcSettingsWindow.HubResultGroup.Fix);
			w.RunSearch("mouse");
			SystemSearchIndex.QaSet(_index);
			w.RunSearch("mouse");
			bool requery = w.QaResults.Any((PcSettingsWindow.HubResult r) => r.Group != PcSettingsWindow.HubResultGroup.Hub);
			Check("search-index-not-ready", hubOnly && requery, $"hub only before={hubOnly} windows hits after={requery}");
			w.QaRender(Path.Combine(_out, "search-results.png"), "home", 980.0, 760.0);
			w.QaSearchBox.Text = "pointer speed";
			w.RunSearch("pointer speed");
			w.QaSnapshot(Path.Combine(_out, "search-pointer-speed.png"), 980.0, 760.0);
			// ranking time per keystroke
			List<double> ms = new List<double>();
			foreach (string q in new[] { "m", "mo", "mou", "mous", "mouse", "ποντίκι", "ηχος", "power plan", "device manager", "keyboard repeat", "sound", "display", "wifi" })
			{
				for (int i = 0; i < 3; i++)
				{
					Stopwatch sw = Stopwatch.StartNew();
					w.Rank(q);
					sw.Stop();
					ms.Add(sw.Elapsed.TotalMilliseconds);
				}
			}
			ms.Sort();
			double median = ms[ms.Count / 2];
			Time("rank-median", median);
			Time("rank-max", ms[^1]);
			Check("search-rank-time", median <= 16.0, $"median {median:0.00} ms, max {ms[^1]:0.00} ms over {ms.Count} queries");
			Check("search-no-second-index", SystemSearchIndex.Revision - revision <= 2 && ReferenceEquals(SystemSearchIndex.Current, _index), $"revision {revision}->{SystemSearchIndex.Revision} (2 = the QA swap and back)");
			w.EscapeStep();
		}
		catch (Exception ex)
		{
			Check("search", false, ex.ToString());
		}
		finally
		{
			SystemSearchIndex.QaSet(_index);
			if (w.IsVisible)
			{
				w.Close();
			}
		}
	}

	// ---- policy -----------------------------------------------------------------------------------------------------

	private static void PolicyChecks()
	{
		// this PC: no rendered row leads to a hidden page
		PolicyFixture("policy-this-pc", "this-pc", delegate(HubCaps caps, ResolvedLink? r, HubRow row)
		{
			string? page = r != null && r.Value.IsUri ? SettingsLink.SettingsPageName(r.Value.Target) : null;
			return page == null || page.Length == 0 || caps.PageAvailable(page);
		});
		// show only: only the allowed Windows pages
		PolicyFixture("policy-showonly", "policy-showonly", delegate(HubCaps caps, ResolvedLink? r, HubRow row)
		{
			string? page = r != null && r.Value.IsUri ? SettingsLink.SettingsPageName(r.Value.Target) : null;
			// the Settings home ("ms-settings:") stays reachable: it is the escape hatch to the pages the policy allows
			return page == null || page.Length == 0 || new[] { "display", "sound", "about", "mousetouchpad" }.Contains(page);
		});
		// no Control Panel: no applet fallback, no Control Panel & tools page
		PolicyFixture("policy-nocpl", "nocpl", delegate(HubCaps caps, ResolvedLink? r, HubRow row)
		{
			return r == null || r.Value.IsUri || SettingsLink.CplKey(r.Value.Target, r.Value.Args) == null;
		}, delegate(PcSettingsWindow w) { return !w.VisibleEntries().Contains("tools"); });
		// no privacy page at all: the entry is hidden
		FakeSettingsWorld none = new FakeSettingsWorld("noprivacy");
		PcSettingsWindow pw = NewWindow();
		SystemSearchIndex.QaSet(null);
		pw.QaUseCaps(none.Caps);
		bool hidden = !pw.VisibleEntries().Contains("privacy");
		SystemSearchIndex.QaSet(_index);
		pw.QaUseCaps(_world.Caps);
		bool shownHere = pw.VisibleEntries().Contains("privacy") || !_index!.Hits.Any((SystemHit h) => h.Target.StartsWith("ms-settings:privacy", StringComparison.OrdinalIgnoreCase));
		Check("policy-privacy", hidden && shownHere, $"hidden without privacy pages={hidden} shown on this PC={shownHere}");
		pw.Close();
		// sidebar on this PC: 19 entries (Recent changes and Touchpad hidden)
		PcSettingsWindow sw = NewWindow();
		List<string> entries = sw.VisibleEntries();
		Check("sidebar-entries", entries.Count >= 18 && entries.Count <= 19 && !entries.Contains("touchpad") && !entries.Contains("recent"), entries.Count + ": " + string.Join(", ", entries));
		sw.Close();
	}

	private static void PolicyFixture(string name, string profile, Func<HubCaps, ResolvedLink?, HubRow, bool> allowed, Func<PcSettingsWindow, bool>? extra = null)
	{
		FakeSettingsWorld fixture = new FakeSettingsWorld(profile);
		List<string> bad = new List<string>();
		int rendered = 0;
		PcSettingsWindow w = NewWindow();
		try
		{
			w.QaUseCaps(fixture.Caps);
			Show(w);
			foreach (string page in Pages())
			{
				w.QaRender(Path.Combine(_out, name + "-" + page.Replace('/', '-') + ".png"), page, 980.0, 760.0);
				foreach (HubRow row in HubCatalog.Rows.Where((HubRow r) => r.Page == page && r.Link != null))
				{
					if (w.QaRowElement(row.Id) is not FrameworkElement el || row.Kind != HubRowKind.Link && row.Kind != HubRowKind.Info && row.Kind != HubRowKind.Choice && row.Kind != HubRowKind.Slider && row.Kind != HubRowKind.Toggle)
					{
						continue;
					}
					if (el is not Button)
					{
						continue;
					}
					rendered++;
					ResolvedLink? r = SettingsLink.Resolve(row.Link!, fixture.Caps);
					if (r == null || !allowed(fixture.Caps, r, row))
					{
						bad.Add(row.Id + "->" + (r?.Describe() ?? "nothing"));
					}
				}
			}
			bool more = extra == null || extra(w);
			Check(name, bad.Count == 0 && more && rendered > 20, $"{rendered} link rows checked" + (bad.Count == 0 ? "" : "; " + string.Join("; ", bad.Take(10))) + (more ? "" : "; extra condition failed"));
		}
		catch (Exception ex)
		{
			Check(name, false, ex.Message);
		}
		finally
		{
			w.QaUseCaps(_world.Caps);
			w.Close();
		}
	}

	// ---- looks ------------------------------------------------------------------------------------------------------

	private static void LookChecks()
	{
		string dir = Path.Combine(_out, "looks");
		Directory.CreateDirectory(dir);
		foreach (string look in _looks)
		{
			UseLook(look);
			PcSettingsWindow w = NewWindow();
			try
			{
				Show(w);
				foreach (string page in Pages())
				{
					w.QaRender(Path.Combine(dir, look + "-" + page.Replace('/', '-') + ".png"), page, 980.0, 760.0);
				}
				foreach (string page in new[] { "home", "mouse", "sound", "appearance" })
				{
					w.QaRender(Path.Combine(dir, look + "-" + page + "-1366.png"), page, 1366.0, 768.0);
				}
				HubPalette p = w.QaPalette;
				Color card = p.CardColor;
				double ink = FlyoutPalette.Contrast(p.InkColor, card);
				double ink2 = FlyoutPalette.Contrast(p.Ink2Color, card);
				double rail = p.HighContrast || p.Glass ? 99.0 : FlyoutPalette.Contrast(p.RailInkColor, p.RailColor);
				double link = FlyoutPalette.Contrast(p.LinkColor, card);
				double toggle = FlyoutPalette.Contrast(p.ToggleOnColor, p.FieldColor);
				double tiles = HubCatalog.Pages.Where((HubPage x) => x.Group > 0).Min((HubPage x) => FlyoutPalette.Contrast(p.MiniTileColor(x.Tile), p.MiniGlyphColor));
				// the caption strips of the look and mode tiles (white 11 px text), and the sliders: the hub's own fill (not
				// the app-wide one) at 3:1 or more on their track
				double strip = FlyoutPalette.Contrast(PcSettingsWindow.TileStripColor(), Colors.White);
				Color sliderFill = (w.TryFindResource("Metro81.SliderFill") as SolidColorBrush)?.Color ?? Colors.Transparent;
				Color sliderTrack = (w.TryFindResource("Metro81.SliderTrack") as SolidColorBrush)?.Color ?? Colors.Transparent;
				double slider = FlyoutPalette.Contrast(sliderFill, sliderTrack);
				bool sliderOwn = sliderFill == p.SliderFillColor;
				bool ok = ink >= 4.5 && ink2 >= 4.5 && rail >= 4.5 && link >= 4.5 && toggle >= 3.0 && tiles >= 3.0 && strip >= 4.5 && slider >= 3.0 && sliderOwn;
				Check("look-" + look, ok, $"ink {ink:0.0} ink2 {ink2:0.0} rail {rail:0.0} link {link:0.0} toggle {toggle:0.0} tiles {tiles:0.0} tile strip {strip:0.0} slider {sliderFill} on {sliderTrack} {slider:0.0} hub fill={sliderOwn}");
				if (look == "dark")
				{
					Color page = p.PageColor;
					Color surface = (w.TryFindResource("Metro81.Surface") as SolidColorBrush)?.Color ?? Colors.White;
					Check("regression-dark-normal", ColorMath.RelLuminance(page) <= 0.05 && ColorMath.RelLuminance(surface) <= 0.2, $"page L={ColorMath.RelLuminance(page):0.000} surface {surface} L={ColorMath.RelLuminance(surface):0.000}");
				}
				if (look == "rest")
				{
					Color on = (w.TryFindResource("MenuAccent") as SolidColorBrush)?.Color ?? Colors.Transparent;
					Color field = (w.TryFindResource("Metro81.Surface") as SolidColorBrush)?.Color ?? Colors.Transparent;
					Check("rest-tokens", on == HubPalette.Hex("#4E7179") && field == HubPalette.Hex("#141414"), $"toggle on {on} field {field}");
				}
			}
			catch (Exception ex)
			{
				Check("look-" + look, false, ex.Message);
			}
			finally
			{
				w.Close();
			}
		}
		UseLook("light");
	}

	// ---- performance ------------------------------------------------------------------------------------------------

	private static void PerfChecks()
	{
		UseLook("light");
		List<double> opens = new List<double>();
		for (int i = 0; i < 6; i++)
		{
			Stopwatch sw = Stopwatch.StartNew();
			PcSettingsWindow w = NewWindow();
			w.OpenCategory("home");
			w.Show();
			w.UpdateLayout();
			bool rendered = false;
			void OnRender(object? s, EventArgs e)
			{
				rendered = true;
			}
			CompositionTarget.Rendering += OnRender;
			DateTime until = DateTime.UtcNow.AddMilliseconds(500);
			while (!rendered && DateTime.UtcNow < until)
			{
				DispatcherFrame frame = new DispatcherFrame();
				Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Render, (Action)delegate { frame.Continue = false; });
				Dispatcher.PushFrame(frame);
			}
			CompositionTarget.Rendering -= OnRender;
			sw.Stop();
			if (i > 0)
			{
				opens.Add(sw.Elapsed.TotalMilliseconds);
			}
			Time("open-" + i, sw.Elapsed.TotalMilliseconds);
			w.Close();
		}
		opens.Sort();
		double median = opens[opens.Count / 2];
		Check("perf-first-frame", median <= 120.0, $"warm median {median:0.0} ms ({string.Join(", ", opens.Select((double v) => v.ToString("0", CultureInfo.InvariantCulture)))})");
		PcSettingsWindow pw = NewWindow();
		Show(pw);
		List<string> slow = new List<string>();
		int slowReadsBefore = _world.UiThreadSlowReads;
		int startsBefore = SettingsLink.ProcessStarts;
		foreach (string pass in new[] { "cold", "warm" })
		{
			foreach (string page in Pages())
			{
				Stopwatch sw = Stopwatch.StartNew();
				Invoke(pw, "BuildPage", page);
				sw.Stop();
				double ms = sw.Elapsed.TotalMilliseconds;
				Time("build-" + pass + "-" + page, ms);
				double limit = page == "appearance" || page == "appearance/pointers" ? 90.0 : 40.0;
				if (pass == "warm" && ms > limit)
				{
					slow.Add(page + "=" + ms.ToString("0", CultureInfo.InvariantCulture));
				}
			}
		}
		Check("perf-page-builds", slow.Count == 0, slow.Count == 0 ? "every page within 40 ms (Appearance and Pointers 90)" : string.Join(", ", slow));
		pw.QaWaitReads(3000);
		Check("perf-no-ui-slow-reads", _world.UiThreadSlowReads == slowReadsBefore && SettingsLink.ProcessStarts == startsBefore, $"UI-thread slow reads {_world.UiThreadSlowReads - slowReadsBefore}, process starts {SettingsLink.ProcessStarts - startsBefore}");
		pw.Close();
	}

	// ---- lifetime ---------------------------------------------------------------------------------------------------

	private static int Subscribers(Type type, string name)
	{
		FieldInfo? f = type.GetField(name, BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
		return (f?.GetValue(null) as Delegate)?.GetInvocationList().Length ?? 0;
	}

	private static void LifetimeChecks()
	{
		int theme0 = Subscribers(typeof(ShellTheme), "Changed");
		int pointer0 = Subscribers(typeof(PointerScheme), "StateChanged");
		int icons0 = Subscribers(typeof(W7Icons), "Loaded");
		GC.Collect();
		GC.WaitForPendingFinalizers();
		GC.Collect();
		long mem0 = GC.GetTotalMemory(forceFullCollection: true);
		bool anyTimerOn = false;
		for (int round = 0; round < 3; round++)
		{
			PcSettingsWindow w = NewWindow();
			Show(w);
			foreach (string page in new[] { "home", "appearance", "appearance/pointers", "appearance/frames", "launcher/diagnostics", "tools", "sound", "displays" })
			{
				w.QaRender(Path.Combine(_out, "lifetime.png"), page, 980.0, 760.0);
			}
			w.QaSearchBox.Text = "mouse";
			w.RunSearch("mouse");
			w.Close();
			Pump();
			foreach (FieldInfo f in typeof(PcSettingsWindow).GetFields(BindingFlags.Instance | BindingFlags.NonPublic))
			{
				if (f.GetValue(w) is DispatcherTimer t && t.IsEnabled)
				{
					anyTimerOn = true;
				}
			}
		}
		Pump();
		GC.Collect();
		GC.WaitForPendingFinalizers();
		GC.Collect();
		long mem1 = GC.GetTotalMemory(forceFullCollection: true);
		int theme1 = Subscribers(typeof(ShellTheme), "Changed");
		int pointer1 = Subscribers(typeof(PointerScheme), "StateChanged");
		int icons1 = Subscribers(typeof(W7Icons), "Loaded");
		bool timersOff = !anyTimerOn;
		double deltaMb = (mem1 - mem0) / 1048576.0;
		Time("retained-mb", deltaMb);
		Check("lifetime", theme1 == theme0 && pointer1 == pointer0 && icons1 == icons0 && timersOff && deltaMb <= 1.0,
			$"ShellTheme.Changed {theme0}->{theme1} PointerScheme.StateChanged {pointer0}->{pointer1} W7Icons.Loaded {icons0}->{icons1} timers off={timersOff} retained {deltaMb:0.00} MB");
	}

	// ---- pixels -----------------------------------------------------------------------------------------------------

	private static BitmapSource LoadPng(string path)
	{
		using FileStream fs = File.OpenRead(path);
		return BitmapDecoder.Create(fs, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad).Frames[0];
	}

	private static byte[] Pixels(BitmapSource b)
	{
		FormatConvertedBitmap c = new FormatConvertedBitmap(b, PixelFormats.Bgra32, null, 0.0);
		int stride = c.PixelWidth * 4;
		byte[] px = new byte[stride * c.PixelHeight];
		c.CopyPixels(px, stride, 0);
		return px;
	}
}
