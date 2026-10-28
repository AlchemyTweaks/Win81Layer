using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;

#nullable enable annotations

namespace Win81Layer;

// The fake world's native controls (stage 2): SPI values with the fake's own setter table (from the WinUser
// documentation, independent of NativeSettingsTable), power schemes and values, audio endpoints and sessions, DDC
// monitors, radios, locale values, shell flags. It can refuse a value (accepted but not taken), deny power writes (5),
// mark power settings as set by policy, and simulate a device change.
internal sealed partial class FakeSettingsWorld
{
	// set action -> (get action, value in uiParam?) ; 0x21 and 0x20 are read through GetSystemMetrics / GetDoubleClickTime
	internal static readonly Dictionary<uint, (uint Get, bool Ui)> SetTable = new Dictionary<uint, (uint, bool)>
	{
		[0x0071] = (0x0070, false),
		[0x0021] = (0xFFFF, true),
		[0x0020] = (0xFFFE, true),
		[0x0069] = (0x0068, true),
		[0x006D] = (0x006C, true),
		[0x1021] = (0x1020, false),
		[0x101D] = (0x101C, false),
		[0x005D] = (0x005E, true),
		[0x0060] = (0x005F, true),
		[0x101F] = (0x101E, false),
		[0x2009] = (0x2008, false),
		[0x0017] = (0x0016, true),
		[0x000B] = (0x000A, true),
		[0x2007] = (0x2006, false),
		[0x100B] = (0x100A, false),
		[0x1043] = (0x1042, false),
		[0x2017] = (0x2016, false)
	};

	internal readonly HashSet<uint> RefuseSpi = new HashSet<uint>();

	internal int SpiPersists;

	internal readonly List<PowerPlanInfo> Plans = new List<PowerPlanInfo>();

	internal Guid ActivePlan;

	internal readonly Dictionary<(Guid Scheme, Guid Setting, bool Ac), int> PowerValues = new Dictionary<(Guid, Guid, bool), int>();

	internal readonly HashSet<Guid> PolicyManaged = new HashSet<Guid>();

	internal bool DenyPowerWrites;

	internal readonly List<AudioEndpointInfo> Render = new List<AudioEndpointInfo>();

	internal readonly List<AudioEndpointInfo> CaptureDevices = new List<AudioEndpointInfo>();

	internal int Volume = 40;

	internal bool Muted;

	internal int InputVolume = 70;

	internal bool InputMuted;

	internal readonly List<AppVolumeInfo> Sessions = new List<AppVolumeInfo>();

	private readonly List<Action> _audioSubscribers = new List<Action>();

	internal int AudioSubscriptions;

	internal readonly Dictionary<string, int> Ddc = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

	// When each DDC write happened (ms since the world was made).
	internal readonly List<(double Ms, int Value)> DdcWrites = new List<(double, int)>();

	private readonly Stopwatch _clock = Stopwatch.StartNew();

	internal bool? Wifi;

	internal bool? Bluetooth;

	internal readonly Dictionary<uint, string> Locale = new Dictionary<uint, string>();

	internal bool ExtShown = true;

	internal bool HiddenShown;

	internal static readonly Guid PlanSeven = new Guid(7, 0, 0, new byte[8]);

	private void InitStage2(string profile)
	{
		// the user's own tuned value (with the 8 others of the fixture: 9 rows differ from Windows' defaults)
		Spi[0x101C] = 1;
		Spi[0x2008] = 1200;
		Plans.Add(new PowerPlanInfo(PowerPlanKnob.Balanced, "Balanced"));
		Plans.Add(new PowerPlanInfo(PowerPlanKnob.High, "High performance"));
		Plans.Add(new PowerPlanInfo(PowerPlanKnob.Saver, "Power saver"));
		Plans.Add(new PowerPlanInfo(PowerPlanKnob.Ultimate, "Ultimate Performance"));
		if (profile != "laptop")
		{
			// this PC has 63 plans
			for (int i = 1; i <= 59; i++)
			{
				Plans.Add(new PowerPlanInfo(new Guid(i, 0, 0, new byte[8]), "Plan " + i.ToString(CultureInfo.InvariantCulture)));
			}
		}
		ActivePlan = PowerPlanKnob.Balanced;
		foreach (PowerPlanInfo p in Plans)
		{
			PowerValues[(p.Id, PowerKnob.VideoIdle, true)] = 900;
			PowerValues[(p.Id, PowerKnob.VideoIdle, false)] = 300;
			PowerValues[(p.Id, PowerKnob.StandbyIdle, true)] = 1800;
			PowerValues[(p.Id, PowerKnob.StandbyIdle, false)] = 900;
			if (profile == "laptop")
			{
				PowerValues[(p.Id, PowerKnob.PowerButton, true)] = 1;
				PowerValues[(p.Id, PowerKnob.LidAction, true)] = 1;
			}
		}
		if (profile == "locked")
		{
			PolicyManaged.Add(PowerKnob.VideoIdle);
			PolicyManaged.Add(PowerKnob.StandbyIdle);
		}
		Render.Add(new AudioEndpointInfo("{render-1}", "Speakers (Realtek(R) Audio)", true, true));
		Render.Add(new AudioEndpointInfo("{render-2}", "Headphones (USB Audio)", false, false));
		CaptureDevices.Add(new AudioEndpointInfo("{capture-1}", "Microphone (USB Audio)", true, true));
		Sessions.Add(new AppVolumeInfo("system", "System sounds", 100, false));
		Sessions.Add(new AppVolumeInfo("pid:11", "Spotify", 60, false));
		Sessions.Add(new AppVolumeInfo("pid:12", "Firefox", 80, false));
		Sessions.Add(new AppVolumeInfo("pid:13", "Discord", 50, false));
		Sessions.Add(new AppVolumeInfo("pid:14", "Game", 30, true));
		Ddc["DISPLAY1"] = 70;
		Ddc["DISPLAY2"] = -1;
		Monitors = Monitors.Select((MonitorInfo m) => m with { Friendly = m.Name == "DISPLAY1" ? (profile == "laptop" ? "Built-in display" : "LG ULTRAGEAR") : "DELL P2419H" }).ToArray();
		if (profile == "laptop")
		{
			Wifi = true;
			Bluetooth = true;
			Ddc["DISPLAY1"] = 55;
		}
		Locale[LocaleKnob.STIMEFORMAT] = "HH:mm:ss";
		Locale[LocaleKnob.SSHORTTIME] = "HH:mm";
		Locale[LocaleKnob.SSHORTDATE] = "dd-MMM-yy";
		Locale[LocaleKnob.IFIRSTDAYOFWEEK] = "6";
		Locale[LocaleKnob.IMEASURE] = "0";
	}

	// ---- SPI ----------------------------------------------------------------------------------------------------------

	public bool SpiSet(uint action, uint ui, nint pv, uint ini)
	{
		Log((ini == 0 ? "set" : "persist") + " spi 0x" + action.ToString("x", CultureInfo.InvariantCulture) + " ui=" + unchecked((int)ui).ToString(CultureInfo.InvariantCulture) + " pv=" + ((long)pv).ToString(CultureInfo.InvariantCulture) + " ini=" + ini);
		if (ini != 0)
		{
			Interlocked.Increment(ref SpiPersists);
		}
		if (RefuseSpi.Contains(action))
		{
			return true;
		}
		if (!SetTable.TryGetValue(action, out (uint Get, bool Ui) m))
		{
			return false;
		}
		int v = m.Ui ? unchecked((int)ui) : (int)pv;
		switch (action)
		{
		case 0x0021:
			Swap = v;
			break;
		case 0x0020:
			DoubleClick = v;
			break;
		default:
			Spi[m.Get] = v;
			break;
		}
		return true;
	}

	public bool SpiSetMouse(int[] values, uint ini)
	{
		Log((ini == 0 ? "set" : "persist") + " spi 0x4 [" + string.Join(",", values) + "] ini=" + ini);
		if (ini != 0)
		{
			Interlocked.Increment(ref SpiPersists);
		}
		if (RefuseSpi.Contains(0x0004))
		{
			return true;
		}
		Mouse = (int[])values.Clone();
		return true;
	}

	// ---- Power --------------------------------------------------------------------------------------------------------

	public PowerPlanInfo[] ReadPowerPlans()
	{
		Slow("power plans");
		Log("get power plans");
		return Plans.ToArray();
	}

	public Guid? ReadActivePlan()
	{
		Slow("power active");
		Log("get power active");
		return ActivePlan;
	}

	public int? ReadPowerValue(Guid scheme, Guid sub, Guid setting, bool ac)
	{
		Slow("power value");
		Log("get power " + setting.ToString("D").Substring(0, 8) + (ac ? " ac" : " dc"));
		return PowerValues.TryGetValue((scheme, setting, ac), out int v) ? v : null;
	}

	public int WritePowerValue(Guid scheme, Guid sub, Guid setting, bool ac, int value)
	{
		Log("set power " + setting.ToString("D").Substring(0, 8) + (ac ? " ac " : " dc ") + value + " scheme=" + scheme.ToString("D"));
		if (DenyPowerWrites)
		{
			return 5;
		}
		PowerValues[(scheme, setting, ac)] = value;
		return 0;
	}

	public int SetActivePlan(Guid scheme)
	{
		Log("set power active " + scheme.ToString("D"));
		if (!Plans.Any((PowerPlanInfo p) => p.Id == scheme))
		{
			return 2;
		}
		ActivePlan = scheme;
		return 0;
	}

	public bool PowerManagedByPolicy(Guid setting)
	{
		Slow("power policy");
		Log("get power policy " + setting.ToString("D").Substring(0, 8));
		return PolicyManaged.Contains(setting);
	}

	// ---- Audio --------------------------------------------------------------------------------------------------------

	public AudioEndpointInfo[] ReadEndpoints(bool capture)
	{
		Slow("audio endpoints");
		Log("get audio endpoints " + (capture ? "capture" : "render"));
		lock (Render)
		{
			return (capture ? CaptureDevices : Render).ToArray();
		}
	}

	public void SetAudioDefault(string id, bool communications)
	{
		Log("set audio default " + id + " roles=" + (communications ? "communications" : "console,multimedia"));
		lock (Render)
		{
			List<AudioEndpointInfo> list = Render.Any((AudioEndpointInfo e) => e.Id == id) ? Render : CaptureDevices;
			for (int i = 0; i < list.Count; i++)
			{
				AudioEndpointInfo e = list[i];
				list[i] = communications ? e with { IsDefaultComms = e.Id == id } : e with { IsDefault = e.Id == id };
			}
		}
	}

	public (int Volume, bool Muted)? ReadEndpointVolume(bool capture)
	{
		Slow("audio volume");
		Log("get audio volume " + (capture ? "capture" : "render"));
		return capture ? (InputVolume, InputMuted) : (Volume, Muted);
	}

	public void SetEndpointVolume(bool capture, int percent)
	{
		Log("set audio volume " + (capture ? "capture " : "render ") + percent);
		if (capture)
		{
			InputVolume = percent;
		}
		else
		{
			Volume = percent;
		}
	}

	public void SetEndpointMute(bool capture, bool mute)
	{
		Log("set audio mute " + (capture ? "capture " : "render ") + mute);
		if (capture)
		{
			InputMuted = mute;
		}
		else
		{
			Muted = mute;
		}
	}

	public AppVolumeInfo[] ReadSessions()
	{
		Slow("audio sessions");
		Log("get audio sessions");
		return Sessions.ToArray();
	}

	public void SetSessionVolume(string key, int percent)
	{
		Log("set app volume " + key + " " + percent);
	}

	public void SetSessionMute(string key, bool mute)
	{
		Log("set app mute " + key + " " + mute);
	}

	public float ReadPeak(bool capture)
	{
		return 0.3f;
	}

	public IDisposable SubscribeAudio(Action changed)
	{
		Log("subscribe audio");
		lock (_audioSubscribers)
		{
			_audioSubscribers.Add(changed);
			AudioSubscriptions++;
		}
		return new Unsub(this, changed);
	}

	private sealed class Unsub : IDisposable
	{
		private FakeSettingsWorld? _w;

		private readonly Action _a;

		internal Unsub(FakeSettingsWorld w, Action a)
		{
			_w = w;
			_a = a;
		}

		public void Dispose()
		{
			FakeSettingsWorld? w = Interlocked.Exchange(ref _w, null);
			if (w == null)
			{
				return;
			}
			lock (w._audioSubscribers)
			{
				w._audioSubscribers.Remove(_a);
				w.AudioSubscriptions--;
			}
			w.Log("unsubscribe audio");
		}
	}

	// A device came: the subscribers hear about it (on this thread, like a Core Audio thread would call them).
	internal void AddDeviceAndNotify(AudioEndpointInfo device)
	{
		lock (Render)
		{
			Render.Add(device);
		}
		Action[] subs;
		lock (_audioSubscribers)
		{
			subs = _audioSubscribers.ToArray();
		}
		foreach (Action a in subs)
		{
			a();
		}
	}

	public void PlayTestSound()
	{
		Log("play test sound");
	}

	// ---- Monitors -----------------------------------------------------------------------------------------------------

	public int ReadMonitorBrightness(string device)
	{
		Slow("ddc " + device);
		Log("get ddc " + device);
		return Ddc.TryGetValue(device, out int v) ? v : -1;
	}

	public void SetDdcBrightness(string device, int percent)
	{
		Log("set ddc " + device + " " + percent);
		lock (DdcWrites)
		{
			DdcWrites.Add((_clock.Elapsed.TotalMilliseconds, percent));
		}
		Ddc[device] = percent;
	}

	// ---- Radios -------------------------------------------------------------------------------------------------------

	public bool? ReadRadio(string kind)
	{
		Slow("radio");
		Log("get radio " + kind);
		return kind == "wifi" ? Wifi : kind == "bluetooth" ? Bluetooth : null;
	}

	public bool SetRadio(string kind, bool on)
	{
		Log("set radio " + kind + " " + on);
		if (kind == "wifi" && Wifi != null)
		{
			Wifi = on;
			return true;
		}
		if (kind == "bluetooth" && Bluetooth != null)
		{
			Bluetooth = on;
			return true;
		}
		return false;
	}

	public bool SetAllWireless(bool off)
	{
		Log("set radios all off=" + off);
		if (Wifi != null)
		{
			Wifi = !off;
		}
		if (Bluetooth != null)
		{
			Bluetooth = !off;
		}
		return Wifi != null || Bluetooth != null;
	}

	public string[] ReadPairedBluetooth()
	{
		Slow("bluetooth devices");
		Log("get bluetooth paired");
		return Bluetooth != null ? new[] { "MX Master 3", "WH-1000XM4" } : Array.Empty<string>();
	}

	// ---- Locale and shell ---------------------------------------------------------------------------------------------

	public string? GetLocale(uint lctype)
	{
		Log("get locale 0x" + lctype.ToString("x", CultureInfo.InvariantCulture));
		return Locale.TryGetValue(lctype, out string? v) ? v : null;
	}

	public bool SetLocale(uint lctype, string value)
	{
		Log("set locale 0x" + lctype.ToString("x", CultureInfo.InvariantCulture) + " " + value);
		Locale[lctype] = value;
		return true;
	}

	public string[] ReadShortDateFormats()
	{
		Log("get locale shortdate formats");
		return new[] { "d/M/yyyy", "dd/MM/yy", "dd/MM/yyyy", "yyyy-MM-dd" };
	}

	public void Broadcast(string area)
	{
		Log("broadcast " + area);
	}

	public void SetExplorerFlag(string flag, bool on)
	{
		Log("set shellflag " + flag + " " + (on ? 1 : 0));
		if (flag == "hidden")
		{
			HiddenShown = on;
		}
		else
		{
			ExtShown = on;
		}
	}

	public string ReadMetered()
	{
		Slow("metered");
		Log("get metered");
		return "Not metered";
	}

	public string ReadProxy()
	{
		Slow("proxy");
		Log("get proxy");
		return "Off";
	}

	public (string Kind, string App)[] ReadDefaultApps()
	{
		Log("get default apps");
		return new (string, string)[] { ("Web browser", "MetroBrowser"), ("Email", "Mail"), ("PDF", "Microsoft Edge"), ("Photos", "Photos"), ("Music", "Media Player"), ("Video", "Media Player") };
	}

	public StartupAppInfo[] ReadStartupApps()
	{
		Log("get startup apps");
		return new[] { new StartupAppInfo("Discord", "Registry (user)", true), new StartupAppInfo("Steam", "Registry (user)", false) };
	}

	public string ReadGpu()
	{
		Log("get gpu");
		return "AMD Radeon RX 5700 XT (8 GB)";
	}

	public long ReadRecycleBinBytes()
	{
		Log("get recycle bin");
		return 1288490188L;
	}
}

internal static partial class PcHubDiagnostics
{
	private static FakeSettingsWorld _s2 = null!;

	private static string[] Lines(int from)
	{
		lock (_s2.Calls)
		{
			return _s2.Calls.Skip(from).ToArray();
		}
	}

	private static int Mark()
	{
		lock (_s2.Calls)
		{
			return _s2.Calls.Count;
		}
	}

	private static PcSettingsWindow Stage2Window(string page)
	{
		PcSettingsWindow w = NewWindow();
		w.QaUseCaps(_s2.Caps);
		Show(w);
		w.QaRender(Path.Combine(_out, "s2-" + page.Replace('/', '-') + ".png"), page, 980.0, 760.0);
		w.QaSettle();
		return w;
	}

	// ---- Stage 2: native controls against the fake world --------------------------------------------------------------

	private static void Stage2Checks()
	{
		FakeSettingsWorld stage1 = _world;
		_s2 = new FakeSettingsWorld("this-pc");
		_world = _s2;
		SettingsWorld.Current = _s2;
		SettingsJournal.QaReset();
		string motionBefore = Motion.Mode.ToString();
		try
		{
			ParamKind();
			ReadFirst();
			DragAndJournal();
			ReadbackMismatch();
			WindowsDefaults();
			KeepGuard();
			Transient();
			Power();
			Displays();
			Sound();
			Radios();
			LocaleAndShell();
			ReduceMotion();
			UriVerify();
			AudioServiceCached();
			Stage2Lifetime();
		}
		catch (Exception ex)
		{
			Check("stage2", false, ex.ToString());
		}
		finally
		{
			SettingsWorker.Flush(3000);
			SettingsJournal.QaReset();
			_world = stage1;
			SettingsWorld.Current = stage1;
			try
			{
				Motion.Mode = Motion.Parse(motionBefore);
			}
			catch
			{
			}
		}
	}

	// Every SPI row's setter, through its knob, passes the value in the parameter the fake's own table (WinUser) expects.
	private static void ParamKind()
	{
		List<string> bad = new List<string>();
		int n = 0;
		foreach (SpiEntry e in NativeSettingsTable.Spi)
		{
			HubKnob k = HubKnobs.Get(e.RowId)!;
			int value = Math.Min(e.Max, e.Min + 1);
			int mark = Mark();
			k.Apply(_s2, value.ToString(CultureInfo.InvariantCulture), persist: false);
			string line = Lines(mark).LastOrDefault((string l) => l.StartsWith("set spi", StringComparison.Ordinal)) ?? "";
			n++;
			if (!FakeSettingsWorld.SetTable.TryGetValue(e.Set, out (uint Get, bool Ui) m))
			{
				bad.Add(e.RowId + ": setter 0x" + e.Set.ToString("x") + " is not in the documented table");
				continue;
			}
			string want = m.Ui ? " ui=" + value + " pv=0 " : " ui=0 pv=" + value + " ";
			if (!line.Contains(want, StringComparison.Ordinal) || !line.Contains("spi 0x" + e.Set.ToString("x", CultureInfo.InvariantCulture) + " ", StringComparison.Ordinal))
			{
				bad.Add(e.RowId + ": '" + line + "' expected" + want);
			}
			string? back = k.Read(_s2);
			if (back != value.ToString(CultureInfo.InvariantCulture))
			{
				bad.Add(e.RowId + ": read back " + back + " for " + value);
			}
		}
		// SPI_SETMOUSE takes the three values in pvParam (an array)
		int mm = Mark();
		HubKnobs.Get("mouse.precision")!.Apply(_s2, "6,10,1", persist: false);
		bool mouseOk = Lines(mm).Any((string l) => l.StartsWith("set spi 0x4 [6,10,1]", StringComparison.Ordinal));
		Check("param-kind", bad.Count == 0 && mouseOk && n == NativeSettingsTable.Spi.Length, n + " SPI rows + SPI_SETMOUSE" + (bad.Count == 0 ? "" : "; " + string.Join("; ", bad)));
		// back to the fixture
		_s2 = new FakeSettingsWorld("this-pc");
		_world = _s2;
		SettingsWorld.Current = _s2;
	}

	// Opening Mouse, Keyboard, Power, Sound and Time reads only; the rows show the fixture's values.
	private static void ReadFirst()
	{
		int mark = Mark();
		List<string> got = new List<string>();
		bool ok = true;
		foreach (string page in new[] { "mouse", "keyboard", "power", "sound", "time", "accessibility" })
		{
			PcSettingsWindow w = Stage2Window(page);
			try
			{
				string V(string id) => w.QaBinding(id)?.Committed ?? "null";
				switch (page)
				{
				case "mouse":
					ok &= V("mouse.speed") == "20" && !MouseAccelKnob.IsOn(V("mouse.precision")) && V("mouse.doubleclick") == "620" && V("mouse.lines") == "6";
					got.Add($"speed {V("mouse.speed")} precision {V("mouse.precision")} double-click {V("mouse.doubleclick")} lines {V("mouse.lines")}");
					break;
				case "keyboard":
				{
					string blink = (w.QaRowElement("keyboard.blink") as Button)?.Tag is TextBlock tb ? tb.Text : "?";
					ok &= V("keyboard.delay") == "0" && V("keyboard.rate") == "31" && blink == "Not blinking";
					got.Add($"delay {V("keyboard.delay")} rate {V("keyboard.rate")} blink '{blink}'");
					break;
				}
				case "power":
					ok &= V("power.screen") == "900" && V("power.plan") == PowerPlanKnob.Balanced.ToString("D");
					got.Add($"screen {V("power.screen")} s plan {V("power.plan")}");
					break;
				case "sound":
					ok &= V("sound.output") == "{render-1}";
					got.Add($"output {V("sound.output")}");
					break;
				case "time":
					ok &= TimeFormatKnob.Is24(V("time.24h")) && V("time.shortdate") == "dd-MMM-yy" && V("time.firstday") == "6";
					got.Add($"24h {TimeFormatKnob.Is24(V("time.24h"))} short date {V("time.shortdate")} first day {HubKnobs.Get("time.firstday")!.Format(V("time.firstday"))}");
					break;
				case "accessibility":
					ok &= V("access.duration") == "300";
					got.Add($"duration {V("access.duration")} s");
					break;
				}
			}
			finally
			{
				w.Close();
			}
		}
		string[] lines = Lines(mark);
		List<string> writes = lines.Where((string l) => !l.StartsWith("get ", StringComparison.Ordinal) && !l.StartsWith("subscribe", StringComparison.Ordinal) && !l.StartsWith("unsubscribe", StringComparison.Ordinal)).ToList();
		Check("read-first", ok && writes.Count == 0, string.Join("; ", got) + $"; {lines.Length} calls, non-reads: " + (writes.Count == 0 ? "none" : string.Join(", ", writes.Take(6))));
	}

	// 10 drag ticks: live writes only; the release persists once and reads back once. Then the journal: 20 -> 14 and Undo
	// gives 20; changes to one row within 10 s merge; an external change asks before undoing.
	private static void DragAndJournal()
	{
		PcSettingsWindow w = Stage2Window("mouse");
		try
		{
			int mark = Mark();
			int persists0 = _s2.SpiPersists;
			foreach (int v in new[] { 19, 18, 17, 16, 15, 14, 15, 14, 15, 14 })
			{
				w.QaDrag("mouse.speed", v);
				Pump();
			}
			SettingsWorker.Flush(3000);
			string[] drag = Lines(mark);
			int live = drag.Count((string l) => l.StartsWith("set spi 0x71 ", StringComparison.Ordinal) && l.EndsWith("ini=0", StringComparison.Ordinal));
			int persistDuring = drag.Count((string l) => l.StartsWith("persist", StringComparison.Ordinal));
			int broadcastDuring = drag.Count((string l) => l.StartsWith("broadcast", StringComparison.Ordinal));
			int mark2 = Mark();
			w.QaRelease("mouse.speed");
			w.QaSettle();
			string[] rel = Lines(mark2);
			int persistAfter = rel.Count((string l) => l.StartsWith("persist spi 0x71 ", StringComparison.Ordinal) && l.Contains(" pv=14 ", StringComparison.Ordinal) && l.EndsWith("ini=3", StringComparison.Ordinal));
			int persistIdx = Array.FindIndex(rel, (string l) => l.StartsWith("persist spi 0x71", StringComparison.Ordinal));
			int readBack = persistIdx < 0 ? 0 : rel.Skip(persistIdx + 1).Count((string l) => l == "get spi 0x70");
			Check("drag-no-broadcast", live >= 1 && persistDuring == 0 && broadcastDuring == 0 && persistAfter == 1 && readBack == 1 && _s2.Spi[0x70] == 14,
				$"10 ticks: {live} live writes (60 ms throttle), {persistDuring} persists, {broadcastDuring} broadcasts; release: {persistAfter} persist ini=3, {readBack} read-back; value {_s2.Spi[0x70]}");
			// journal: one entry 20 -> 14, Undo gives 20
			List<JournalEntry> j = SettingsJournal.Snapshot();
			bool one = j.Count == 1 && j[0].Id == "mouse.speed" && j[0].Before == "20" && j[0].After == "14";
			w.UndoLast();
			w.QaSettle();
			bool undone = _s2.Spi[0x70] == 20 && SettingsJournal.Count == 0 && w.QaBinding("mouse.speed")?.Committed == "20";
			// merge: two changes of one row within 10 s are one entry; after 11 s a new one
			w.QaChange("mouse.lines", "5");
			w.QaSettle();
			w.QaChange("mouse.lines", "4");
			w.QaSettle();
			bool merged = SettingsJournal.Count == 1 && SettingsJournal.Last()!.Before == "6" && SettingsJournal.Last()!.After == "4";
			_s2.Now = _s2.Now.AddSeconds(11.0);
			w.QaChange("mouse.lines", "8");
			w.QaSettle();
			bool separate = SettingsJournal.Count == 2;
			w.UndoLast();
			w.QaSettle();
			w.UndoLast();
			w.QaSettle();
			bool linesBack = _s2.Spi[0x68] == 6 && SettingsJournal.Count == 0;
			// an external change since: Undo asks first, Set back does it
			w.QaChange("mouse.hchars", "5");
			w.QaSettle();
			_s2.Spi[0x6C] = 7;
			w.UndoLast();
			w.QaSettle();
			string asked = w.QaUndoState;
			string bar = w.QaUndoBarText;
			bool stillSeven = _s2.Spi[0x6C] == 7;
			JournalEntry? last = SettingsJournal.Last();
			if (last != null)
			{
				w.UndoEntry(last, force: true);
			}
			w.QaSettle();
			bool forced = _s2.Spi[0x6C] == 3 && SettingsJournal.Count == 0;
			Check("journal", one && undone && merged && separate && linesBack && asked == "confirm" && stillSeven && forced,
				$"20->14 one entry={one} undo->20={undone} merge within 10 s={merged} new after 11 s={separate} undone={linesBack} external change: state={asked} bar='{bar}' kept={stillSeven} set back={forced}");
		}
		finally
		{
			w.Close();
		}
	}

	// A value Windows does not take: the row goes back, says so, and nothing is journaled.
	private static void ReadbackMismatch()
	{
		PcSettingsWindow w = Stage2Window("mouse");
		try
		{
			int before = SettingsJournal.Count;
			_s2.RefuseSpi.Add(0x1021);
			w.QaChange("mouse.hidetyping", "1");
			w.QaSettle();
			string? note = w.QaNote("mouse.hidetyping");
			string? shown = w.QaBinding("mouse.hidetyping")?.Committed;
			bool toggleOff = w.QaRowElement("mouse.hidetyping") is FrameworkElement row && PcSettingsWindow.Descendants<ToggleButton>(row).FirstOrDefault() is ToggleButton t && t.IsChecked != true;
			_s2.RefuseSpi.Clear();
			Check("readback-mismatch", shown == "0" && toggleOff && note == "Windows did not accept this value" && SettingsJournal.Count == before,
				$"row value {shown} toggle off={toggleOff} note='{note}' journal {before}->{SettingsJournal.Count}");
		}
		finally
		{
			w.Close();
		}
	}

	// The preview lists exactly the fixture's differences (9 rows over Mouse, Keyboard, Accessibility, Files & Explorer);
	// applying is one group and one Undo restores everything.
	private static void WindowsDefaults()
	{
		List<string> diffs = new List<string>();
		foreach (string page in new[] { "mouse", "keyboard", "accessibility", "apps" })
		{
			PcSettingsWindow pw = Stage2Window(page);
			try
			{
				foreach ((PcSettingsWindow.KnobBinding b, string to) in pw.DefaultsDiff(page))
				{
					diffs.Add(b.RowId + " " + b.Display(b.Committed) + "→" + b.Display(to));
				}
				if (page == "mouse")
				{
					pw.QaRender(Path.Combine(_out, "s2-mouse-defaults.png"), "mouse", 980.0, 760.0);
				}
			}
			finally
			{
				pw.Close();
			}
		}
		string[] expected = new[] { "mouse.speed", "mouse.precision", "mouse.doubleclick", "mouse.lines", "mouse.ctrl", "mouse.hidetyping", "keyboard.delay", "access.duration", "apps.extensions" };
		bool exact = diffs.Count == expected.Length && expected.All((string id) => diffs.Any((string d) => d.StartsWith(id + " ", StringComparison.Ordinal)));
		PcSettingsWindow w = Stage2Window("mouse");
		try
		{
			int mark = Mark();
			w.ApplyDefaults("mouse");
			w.QaSettle();
			List<JournalEntry> j = SettingsJournal.Snapshot();
			bool oneGroup = j.Count == 6 && j.Select((JournalEntry e) => e.Group).Distinct().Count() == 1 && j[0].Group != null;
			bool applied = _s2.Spi[0x70] == 10 && _s2.DoubleClick == 500 && _s2.Spi[0x68] == 3 && _s2.Mouse[2] == 1 && _s2.Spi[0x1020] == 1 && _s2.Spi[0x101C] == 0;
			int persists = Lines(mark).Count((string l) => l.StartsWith("persist", StringComparison.Ordinal));
			w.UndoLast();
			w.QaSettle();
			bool restored = _s2.Spi[0x70] == 20 && _s2.DoubleClick == 620 && _s2.Spi[0x68] == 6 && _s2.Mouse[2] == 0 && _s2.Spi[0x1020] == 0 && _s2.Spi[0x101C] == 1 && SettingsJournal.Count == 0;
			Check("windows-defaults", exact && oneGroup && applied && restored,
				$"{diffs.Count} differences: {string.Join(", ", diffs)}; apply: one group={oneGroup} applied={applied} ({persists} persists); one undo restored={restored}");
		}
		finally
		{
			w.Close();
		}
	}

	// Swapping the buttons asks to keep the change: with the clock 10 s on and no Keep it goes back; with Keep it stays.
	private static void KeepGuard()
	{
		PcSettingsWindow w = Stage2Window("mouse");
		try
		{
			w.QaChange("mouse.primary", "1");
			w.QaSettle();
			bool shown = w.QaGuardShown && _s2.Swap == 1;
			w.QaSnapshot(Path.Combine(_out, "s2-keep-guard.png"), 980.0, 760.0);
			_s2.Now = _s2.Now.AddSeconds(5.0);
			w.GuardTick();
			bool stillOn = w.QaGuardShown && _s2.Swap == 1;
			_s2.Now = _s2.Now.AddSeconds(5.1);
			w.GuardTick();
			w.QaSettle();
			bool reverted = !w.QaGuardShown && _s2.Swap == 0 && SettingsJournal.Count == 0 && w.QaBinding("mouse.primary")?.Committed == "0";
			_s2.Now = _s2.Now.AddSeconds(20.0);
			w.QaChange("mouse.primary", "1");
			w.QaSettle();
			w.GuardKeep();
			_s2.Now = _s2.Now.AddSeconds(30.0);
			w.GuardTick();
			w.QaSettle();
			bool kept = !w.QaGuardShown && _s2.Swap == 1 && SettingsJournal.Count == 1;
			w.UndoLast();
			w.QaSettle();
			bool back = _s2.Swap == 0 && !w.QaGuardShown;
			Check("keep-guard", shown && stillOn && reverted && kept && back, $"banner={shown} after 5 s still={stillOn} after 10 s reverted={reverted} keep={kept} undo={back}");
		}
		finally
		{
			w.Close();
		}
	}

	private static Slider? SliderOf(PcSettingsWindow w, string id) => w.QaRowElement(id) is FrameworkElement row ? PcSettingsWindow.Descendants<Slider>(row).FirstOrDefault() : null;

	// Volume, mute, brightness and app volume: applied, never journaled.
	private static void Transient()
	{
		int before = SettingsJournal.Count;
		int mark = Mark();
		PcSettingsWindow w = Stage2Window("sound");
		try
		{
			Slider? vol = SliderOf(w, "sound.volume");
			if (vol != null)
			{
				vol.Value = 55;
			}
			ToggleButton? mute = w.QaRowElement("sound.volume") is FrameworkElement r ? PcSettingsWindow.Descendants<ToggleButton>(r).FirstOrDefault() : null;
			if (mute != null)
			{
				mute.IsChecked = true;
			}
			Slider? app = SliderOf(w, "sound.app0");
			if (app != null)
			{
				app.Value = 25;
			}
			w.QaSettle();
		}
		finally
		{
			w.Close();
		}
		PcSettingsWindow d = Stage2Window("displays");
		try
		{
			Slider? b = SliderOf(d, "displays.brightness");
			if (b != null)
			{
				b.Value = 40;
			}
			d.QaSettle();
		}
		finally
		{
			d.Close();
		}
		string[] lines = Lines(mark);
		bool wrote = lines.Any((string l) => l == "set audio volume render 55") && lines.Any((string l) => l == "set audio mute render True") && lines.Any((string l) => l.StartsWith("set app volume", StringComparison.Ordinal)) && lines.Any((string l) => l == "set ddc DISPLAY1 40");
		Check("transient", wrote && SettingsJournal.Count == before, $"writes seen={wrote} journal {before}->{SettingsJournal.Count}");
	}

	private static void UseFixture(string profile)
	{
		_s2 = new FakeSettingsWorld(profile);
		_world = _s2;
		SettingsWorld.Current = _s2;
	}

	private static void Power()
	{
		// this PC: no sleep (an info row instead), no battery rows, no buttons card
		PcSettingsWindow w = Stage2Window("power");
		bool thisPc;
		string detail;
		try
		{
			bool sleepInfo = w.QaRowElement("power.sleep") is Grid && w.QaBinding("power.sleep") == null;
			bool noDc = w.QaBinding("power.screen.dc") == null && w.QaBinding("power.sleep.dc") == null;
			bool noButtons = w.QaBinding("power.button") == null && w.QaBinding("power.lid") == null && w.QaBinding("power.sleepbutton") == null;
			thisPc = sleepInfo && noDc && noButtons && w.QaBinding("power.screen")?.Committed == "900";
			detail = $"this-pc: sleep info={sleepInfo} no DC={noDc} no buttons={noButtons}";
			// 63 plans: the drop-down stays short
			string[] items = w.QaChoiceItems("power.plan");
			bool shortList = items.Length <= 9 && items.Length >= 2 && items[^1] == "All plans… (63)";
			detail += $"; plan items {items.Length} ({string.Join(" | ", items)})";
			// switching is journaled with the GUID
			w.QaChange("power.plan", FakeSettingsWorld.PlanSeven.ToString("D"));
			w.QaSettle();
			JournalEntry? e = SettingsJournal.Last();
			bool journaled = e != null && e.Id == "power.plan" && e.After == FakeSettingsWorld.PlanSeven.ToString("D") && e.Before == PowerPlanKnob.Balanced.ToString("D") && _s2.ActivePlan == FakeSettingsWorld.PlanSeven;
			detail += $"; switch journaled={journaled}";
			// the plans page filters the 63
			w.OpenCategory("power/plans");
			w.QaSettle();
			TextBox? filter = w.QaPageElement is DependencyObject root ? PcSettingsWindow.Descendants<TextBox>(root).FirstOrDefault() : null;
			int all = w.QaPageElement is DependencyObject r0 ? PcSettingsWindow.Descendants<RadioButton>(r0).Count() : 0;
			if (filter != null)
			{
				filter.Text = "Plan 5";
			}
			w.QaSettle();
			int filtered = w.QaPageElement is DependencyObject r1 ? PcSettingsWindow.Descendants<RadioButton>(r1).Count() : 0;
			w.QaSnapshot(Path.Combine(_out, "s2-power-plans.png"), 980.0, 760.0);
			bool filterOk = all == 63 && filtered == 11;
			detail += $"; plans page {all} -> filter 'Plan 5' {filtered}";
			w.UndoLast();
			w.QaSettle();
			thisPc &= shortList && journaled && filterOk && _s2.ActivePlan == PowerPlanKnob.Balanced;
		}
		finally
		{
			w.Close();
		}
		// a write refused with 5: the row turns read-only
		_s2.DenyPowerWrites = true;
		PcSettingsWindow d = Stage2Window("power");
		bool denied;
		try
		{
			d.QaChange("power.screen", "600");
			d.QaSettle();
			denied = d.QaBinding("power.screen")?.ReadOnly == true && d.QaNote("power.screen") == "Managed by your organisation" && SettingsJournal.Count == 0;
			detail += $"; write 5 -> read-only={denied}";
		}
		finally
		{
			d.Close();
			_s2.DenyPowerWrites = false;
		}
		FakeSettingsWorld thisPcWorld = _s2;
		// laptop: AC and DC rows, sleep, lid
		UseFixture("laptop");
		PcSettingsWindow l = Stage2Window("power");
		bool laptop;
		try
		{
			laptop = new[] { "power.screen", "power.screen.dc", "power.sleep", "power.sleep.dc", "power.lid", "power.button" }.All((string id) => l.QaBinding(id)?.Committed != null);
			detail += $"; laptop rows={laptop}";
		}
		finally
		{
			l.Close();
		}
		// locked: policy-set rows are read-only
		UseFixture("locked");
		PcSettingsWindow k = Stage2Window("power");
		bool locked;
		try
		{
			locked = k.QaBinding("power.screen")?.ReadOnly == true && k.QaNote("power.screen") == "Managed by policy";
			detail += $"; locked={locked}";
		}
		finally
		{
			k.Close();
		}
		_s2 = thisPcWorld;
		_world = _s2;
		SettingsWorld.Current = _s2;
		Check("power", thisPc && denied && laptop && locked, detail);
	}

	// A slider for DISPLAY1, "set on the monitor" for DISPLAY2; a drag writes at most once per 60 ms and the final value
	// once more after it is let go.
	private static void Displays()
	{
		PcSettingsWindow w = Stage2Window("displays");
		try
		{
			int fixture = _s2.Ddc["DISPLAY1"];
			Slider? one = SliderOf(w, "displays.brightness");
			FrameworkElement? two = w.QaRowElement("displays.brightness:DISPLAY2");
			Slider? twoSlider = two != null ? PcSettingsWindow.Descendants<Slider>(two).FirstOrDefault() : null;
			string twoText = two is Grid g && g.Children.Count > 0 && g.Children[0] is StackPanel sp && sp.Children.Count > 1 && sp.Children[1] is TextBlock tb ? tb.Text : "?";
			bool rows = one != null && one.IsVisible && one.IsEnabled && one.Value == fixture && twoSlider != null && !twoSlider.IsVisible && twoText == "Brightness is set on the monitor";
			lock (_s2.DdcWrites)
			{
				_s2.DdcWrites.Clear();
			}
			if (one != null)
			{
				one.RaiseEvent(new DragStartedEventArgs(0.0, 0.0) { RoutedEvent = Thumb.DragStartedEvent });
				for (int i = 0; i < 12; i++)
				{
					one.Value = 60 - i * 2;
					Thread.Sleep(15);
					Pump();
				}
				one.RaiseEvent(new DragCompletedEventArgs(0.0, 0.0, false) { RoutedEvent = Thumb.DragCompletedEvent });
			}
			w.QaSettle();
			(double Ms, int Value)[] writes;
			lock (_s2.DdcWrites)
			{
				writes = _s2.DdcWrites.ToArray();
			}
			double minGap = double.MaxValue;
			for (int i = 1; i < writes.Length; i++)
			{
				minGap = Math.Min(minGap, writes[i].Ms - writes[i - 1].Ms);
			}
			bool throttled = writes.Length >= 2 && writes.Length < 12 && minGap >= 55.0 && writes[^1].Value == 38;
			Time("ddc-min-gap", writes.Length > 1 ? minGap : -1);
			Check("displays", rows && throttled, $"DISPLAY1 slider={one?.Value} DISPLAY2 '{twoText}'; 12 drag steps -> {writes.Length} writes, min gap {(writes.Length > 1 ? minGap : 0):0.0} ms, last {(writes.Length > 0 ? writes[^1].Value : -1)}");
		}
		finally
		{
			w.Close();
		}
	}

	private static void Sound()
	{
		PcSettingsWindow w = NewWindow();
		w.QaUseCaps(_s2.Caps);
		w.QaTreatAsActive = true;
		string detail = "";
		bool ok = true;
		try
		{
			Show(w);
			w.QaRender(Path.Combine(_out, "s2-sound.png"), "sound", 980.0, 760.0);
			w.QaSettle();
			Pump();
			bool meterOn = w.QaMeterRunning;
			int mark = Mark();
			w.QaChange("sound.output", "{render-2}");
			w.QaSettle();
			string[] l1 = Lines(mark);
			bool roles = l1.Any((string l) => l == "set audio default {render-2} roles=console,multimedia") && !l1.Any((string l) => l.Contains("roles=communications", StringComparison.Ordinal));
			w.QaSettle();
			ToggleButton? calls = w.QaRowElement("sound.calls") is FrameworkElement cr ? PcSettingsWindow.Descendants<ToggleButton>(cr).FirstOrDefault() : null;
			bool callsOff = calls != null && calls.IsChecked != true;
			int mark2 = Mark();
			if (calls != null)
			{
				calls.IsChecked = true;
			}
			w.QaSettle();
			bool comms = Lines(mark2).Any((string l) => l == "set audio default {render-2} roles=communications");
			detail += $"output switch roles console+multimedia only={roles}; calls off after switch={callsOff}, on -> communications={comms}";
			ok &= roles && callsOff && comms;
			// a device arrives: the list follows, the page is not built again
			int builds = w.QaPageBuilds;
			int updates = w.QaAudioListUpdates;
			_s2.AddDeviceAndNotify(new AudioEndpointInfo("{render-3}", "HDMI (NVIDIA High Definition Audio)", false, false));
			w.QaSettle();
			Pump();
			w.QaSettle();
			int radios = w.QaRowElement("sound.output") is FrameworkElement or ? PcSettingsWindow.Descendants<RadioButton>(or).Count() : 0;
			bool followed = w.QaPageBuilds == builds && w.QaAudioListUpdates > updates && radios == 3;
			detail += $"; device change: {radios} outputs, rebuilds {w.QaPageBuilds - builds}, list updates {w.QaAudioListUpdates - updates}";
			ok &= followed;
			w.QaSnapshot(Path.Combine(_out, "s2-sound-after.png"), 980.0, 760.0);
			// the meter runs only while the page shows
			w.OpenCategory("mouse");
			w.QaSettle();
			Pump();
			bool meterOffAway = !w.QaMeterRunning;
			w.OpenCategory("sound");
			w.QaSettle();
			Pump();
			bool meterBack = w.QaMeterRunning;
			detail += $"; meter on Sound={meterOn} away={!meterOffAway} back={meterBack}";
			ok &= meterOn && meterOffAway && meterBack;
			w.UndoLast();
			w.QaSettle();
			w.UndoLast();
			w.QaSettle();
		}
		finally
		{
			w.Close();
			Pump();
		}
		bool meterClosed = !w.QaMeterRunning;
		detail += $"; after close meter off={meterClosed} audio subscriptions {_s2.AudioSubscriptions}";
		Check("sound", ok && meterClosed && _s2.AudioSubscriptions == 0, detail);
	}

	private static void Radios()
	{
		PcSettingsWindow n = Stage2Window("network");
		bool none;
		try
		{
			n.OpenCategory("devices");
			n.QaSettle();
			none = n.QaBinding("devices.bttoggle") == null;
			n.OpenCategory("network");
			n.QaSettle();
			none &= n.QaBinding("network.wifitoggle") == null && n.QaBinding("network.allwireless") == null;
		}
		finally
		{
			n.Close();
		}
		FakeSettingsWorld keep = _s2;
		UseFixture("laptop");
		PcSettingsWindow l = Stage2Window("network");
		bool present;
		bool toggled;
		try
		{
			present = l.QaBinding("network.wifitoggle")?.Committed == "1" && l.QaBinding("network.allwireless") != null;
			l.QaChange("network.wifitoggle", "0");
			l.QaSettle();
			toggled = _s2.Wifi == false && SettingsJournal.Last()?.Id == "network.wifitoggle";
			l.UndoLast();
			l.QaSettle();
			toggled &= _s2.Wifi == true;
			l.OpenCategory("devices");
			l.QaSettle();
			present &= l.QaBinding("devices.bttoggle")?.Committed == "1";
			l.QaSnapshot(Path.Combine(_out, "s2-laptop-devices.png"), 980.0, 760.0);
		}
		finally
		{
			l.Close();
		}
		_s2 = keep;
		_world = _s2;
		SettingsWorld.Current = _s2;
		Check("radios", none && present && toggled, $"this-pc toggles absent={none}; laptop present={present} wifi off journaled and undone={toggled}");
	}

	private static void LocaleAndShell()
	{
		PcSettingsWindow w = Stage2Window("time");
		try
		{
			int mark = Mark();
			w.QaChange("time.24h", TimeFormatKnob.For(false));
			w.QaSettle();
			string[] l = Lines(mark);
			bool both = l.Any((string x) => x == "set locale 0x1003 h:mm:ss tt") && l.Any((string x) => x == "set locale 0x79 h:mm tt");
			int broadcasts = l.Count((string x) => x == "broadcast intl");
			bool oneEntry = SettingsJournal.Count == 1 && SettingsJournal.Last()!.Id == "time.24h";
			w.UndoLast();
			w.QaSettle();
			bool back = _s2.Locale[LocaleKnob.STIMEFORMAT] == "HH:mm:ss" && _s2.Locale[LocaleKnob.SSHORTTIME] == "HH:mm";
			Check("locale", both && broadcasts == 1 && oneEntry && back, $"STIMEFORMAT+SSHORTTIME={both} intl broadcasts={broadcasts} one entry={oneEntry} undone={back}");
		}
		finally
		{
			w.Close();
		}
		PcSettingsWindow a = Stage2Window("apps");
		try
		{
			int mark = Mark();
			a.QaChange("apps.extensions", "0");
			a.QaSettle();
			string[] l = Lines(mark);
			bool set = l.Any((string x) => x == "set shellflag extensions 0") && !_s2.ExtShown;
			int notify = l.Count((string x) => x == "broadcast assoc");
			a.UndoLast();
			a.QaSettle();
			Check("shell-flags", set && notify == 1 && _s2.ExtShown, $"flag set={set} change notifications={notify} undone={_s2.ExtShown}");
		}
		finally
		{
			a.Close();
		}
	}

	// Reduce motion everywhere: the launcher's motion and Windows' animation effects in one journal group, one Undo.
	private static void ReduceMotion()
	{
		// the QA override (never the real settings) starts at Authentic, so both halves change
		string? real = SettingsStore.DiagnosticsOverride?.MotionMode;
		if (SettingsStore.DiagnosticsOverride != null)
		{
			SettingsStore.DiagnosticsOverride.MotionMode = "Authentic";
		}
		PcSettingsWindow w = Stage2Window("accessibility");
		try
		{
			string motion0 = SettingsStore.FastSnapshot.MotionMode ?? "";
			ToggleButton? t = w.QaRowElement("access.reducemotion") is FrameworkElement row ? PcSettingsWindow.Descendants<ToggleButton>(row).FirstOrDefault() : null;
			if (t != null)
			{
				t.IsChecked = true;
			}
			w.QaSettle();
			List<JournalEntry> j = SettingsJournal.Snapshot();
			bool group = j.Count == 2 && j.Select((JournalEntry e) => e.Group).Distinct().Count() == 1 && j[0].Group != null && j.Any((JournalEntry e) => e.Id == "appearance.motion") && j.Any((JournalEntry e) => e.Id == "access.animations");
			bool on = SettingsStore.FastSnapshot.MotionMode == "Reduced" && _s2.Spi[0x1042] == 0;
			w.UndoLast();
			w.QaSettle();
			bool back = SettingsStore.FastSnapshot.MotionMode == motion0 && _s2.Spi[0x1042] == 1 && SettingsJournal.Count == 0;
			Check("reduce-motion", t != null && group && on && back, $"group of 2={group} applied={on} one undo restored={back} (motion '{motion0}')");
		}
		finally
		{
			w.Close();
			if (SettingsStore.DiagnosticsOverride != null && real != null)
			{
				SettingsStore.DiagnosticsOverride.MotionMode = real;
			}
		}
	}

	private static void UriVerify()
	{
		SystemSearchIndex? index = SystemSearchIndex.Current;
		bool complete = index != null && index.Complete;
		ResolvedLink? missing = SettingsLink.Resolve(LinkSpec.Page("ms-settings:qa-not-a-page", parent: "ms-settings:display"), _s2.Caps);
		ResolvedLink? present = SettingsLink.Resolve(LinkSpec.Page("ms-settings:display", parent: "ms-settings:about"), _s2.Caps);
		bool ok = !complete || (missing?.Target == "ms-settings:display" && present?.Target == "ms-settings:display");
		Check("uri-verify", ok && complete, $"index complete={complete}; missing page -> {missing?.Describe()}; listed page -> {present?.Describe()}");
	}

	// The shared audio service (real Core Audio, reads only): once someone listens (the live tray always does), the level
	// and mute are cached from the endpoint's callback, so a tray tick costs no COM call. Without a subscriber it reads.
	private static void AudioServiceCached()
	{
		AudioEndpointService a = AudioEndpointService.Shared;
		IDisposable sub = a.Subscribe(delegate { });
		try
		{
			bool has = a.HasEndpoint(capture: false);
			long r0 = AudioEndpointService.ComReads;
			for (int i = 0; i < 20; i++)
			{
				a.GetVolume();
				a.GetMute();
			}
			long cached = AudioEndpointService.ComReads - r0;
			sub.Dispose();
			long r1 = AudioEndpointService.ComReads;
			a.GetVolume();
			long unsubscribed = AudioEndpointService.ComReads - r1;
			Check("audio-service-cached", !has || (cached == 0 && unsubscribed >= 1 && a.Subscribers == 0), has ? $"subscribed: {cached} COM reads for 40 tray reads; unsubscribed: {unsubscribed} for 1; subscribers {a.Subscribers}" : "no output device on this PC: nothing to read");
		}
		catch (Exception ex)
		{
			sub.Dispose();
			Check("audio-service-cached", false, ex.Message);
		}
	}

	// After the stage 2 windows closed: no audio subscription, no window timer left on.
	private static void Stage2Lifetime()
	{
		PcSettingsWindow w = NewWindow();
		w.QaUseCaps(_s2.Caps);
		w.QaTreatAsActive = true;
		Show(w);
		foreach (string page in new[] { "home", "sound", "mouse", "keyboard", "power", "displays", "time", "recent", "sound/apps", "power/plans" })
		{
			w.QaRender(Path.Combine(_out, "s2-life.png"), page, 980.0, 760.0);
		}
		w.QaChange("keyboard.caretwidth", "2");
		w.QaSettle();
		w.Close();
		Pump();
		bool timersOff = true;
		foreach (FieldInfo f in typeof(PcSettingsWindow).GetFields(BindingFlags.Instance | BindingFlags.NonPublic))
		{
			if (f.GetValue(w) is DispatcherTimer t && t.IsEnabled)
			{
				timersOff = false;
			}
		}
		SettingsJournal.QaReset();
		Check("stage2-lifetime", timersOff && _s2.AudioSubscriptions == 0, $"timers off={timersOff} audio subscriptions={_s2.AudioSubscriptions}");
	}

	// Renders of the native pages in one look (light, dark, rest) into <out>\stage2\<look>-<page>.png.
	private static void Stage2Renders()
	{
		string dir = Path.Combine(_out, "stage2");
		Directory.CreateDirectory(dir);
		FakeSettingsWorld stage1 = _world;
		_s2 = new FakeSettingsWorld("this-pc");
		_world = _s2;
		SettingsWorld.Current = _s2;
		try
		{
			foreach (string look in new[] { "light", "dark", "rest" })
			{
				UseLook(look);
				PcSettingsWindow w = NewWindow();
				w.QaUseCaps(_s2.Caps);
				try
				{
					Show(w);
					foreach (string page in new[] { "home", "sound", "displays", "power", "power/plans", "mouse", "keyboard", "accessibility", "time", "apps", "network", "devices", "about", "sound/apps" })
					{
						w.QaRender(Path.Combine(dir, look + "-" + page.Replace('/', '-') + ".png"), page, 980.0, 760.0);
						w.QaRenderTall(Path.Combine(dir, look + "-" + page.Replace('/', '-') + "-tall.png"), page, 748.0);
					}
					if (look == "light")
					{
						w.QaRender(Path.Combine(dir, look + "-mouse-before-change.png"), "mouse", 980.0, 760.0);
						w.QaChange("mouse.speed", "14");
						w.QaSettle();
						w.QaSnapshot(Path.Combine(dir, look + "-undo-bar.png"), 980.0, 760.0);
						w.QaRender(Path.Combine(dir, look + "-recent.png"), "recent", 980.0, 760.0);
						w.QaRender(Path.Combine(dir, look + "-home-recent.png"), "home", 980.0, 760.0);
						w.UndoLast();
						w.QaSettle();
					}
					// the Windows defaults preview, opened at the end of the Mouse page
					w.QaRender(Path.Combine(dir, look + "-mouse-defaults-closed.png"), "mouse", 980.0, 760.0);
					if (w.QaRowElement("mouse.reset.defaults") is Button open)
					{
						open.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
						w.QaSettle();
						if (w.QaPageElement is ScrollViewer sv)
						{
							sv.UpdateLayout();
							sv.ScrollToEnd();
							sv.UpdateLayout();
						}
						w.QaSnapshot(Path.Combine(dir, look + "-mouse-defaults-preview.png"), 980.0, 760.0);
					}
					if (look != "light")
					{
						w.QaRender(Path.Combine(dir, look + "-mouse-before-guard.png"), "mouse", 980.0, 760.0);
						w.QaChange("mouse.primary", "1");
						w.QaSettle();
						w.QaSnapshot(Path.Combine(dir, look + "-keep-guard.png"), 980.0, 760.0);
						w.GuardRevert();
						w.QaSettle();
					}
				}
				finally
				{
					w.Close();
				}
			}
			UseFixture("laptop");
			UseLook("light");
			PcSettingsWindow l = NewWindow();
			l.QaUseCaps(_s2.Caps);
			try
			{
				Show(l);
				foreach (string page in new[] { "power", "network", "devices", "home" })
				{
					l.QaRender(Path.Combine(dir, "laptop-" + page + ".png"), page, 980.0, 760.0);
				}
			}
			finally
			{
				l.Close();
			}
			Check("stage2-renders", true, Directory.GetFiles(dir, "*.png").Length + " PNGs in " + dir);
		}
		catch (Exception ex)
		{
			Check("stage2-renders", false, ex.Message);
		}
		finally
		{
			SettingsWorker.Flush(3000);
			SettingsJournal.QaReset();
			UseLook("light");
			_world = stage1;
			SettingsWorld.Current = stage1;
		}
	}
}
