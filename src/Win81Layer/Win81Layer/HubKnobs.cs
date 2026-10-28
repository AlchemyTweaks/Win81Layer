using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Threading;

#nullable enable annotations

namespace Win81Layer;

internal enum KnobRisk
{
	None,
	// The change is applied, then reverted after 10 s unless the user keeps it (the primary mouse button).
	KeepGuard
}

// How an SPI setter takes its value: in uiParam, or in pvParam passed as the value itself.
internal enum SpiParam
{
	Ui,
	Pv
}

// Where an SPI row's current value is read: the SPI getter (pvParam points to the value), GetDoubleClickTime, or
// GetSystemMetrics(SM_SWAPBUTTON).
internal enum SpiRead
{
	Spi,
	DoubleClick,
	SwapMetric
}

// One SPI row: getter, setter, which parameter carries the value, the range and the documented Windows
// default (null when there is none).
internal sealed record SpiEntry(string RowId, uint Get, uint Set, SpiParam Param, int Min, int Max, int? Default, SpiRead Read = SpiRead.Spi);

// The SPI rows of PC Settings, from the WinUser documentation. The QA check "param-kind" compares every setter call with an
// independent table of its own.
internal static class NativeSettingsTable
{
	internal const uint SpiLive = 0u;

	internal const uint SpiPersist = 3u;

	internal static readonly SpiEntry[] Spi = new SpiEntry[]
	{
		new SpiEntry("mouse.speed", 0x0070, 0x0071, SpiParam.Pv, 1, 20, 10),
		new SpiEntry("mouse.primary", 0, 0x0021, SpiParam.Ui, 0, 1, 0, SpiRead.SwapMetric),
		new SpiEntry("mouse.doubleclick", 0, 0x0020, SpiParam.Ui, 200, 900, 500, SpiRead.DoubleClick),
		new SpiEntry("mouse.lines", 0x0068, 0x0069, SpiParam.Ui, 1, 100, 3),
		new SpiEntry("mouse.hchars", 0x006C, 0x006D, SpiParam.Ui, 1, 20, 3),
		new SpiEntry("mouse.hidetyping", 0x1020, 0x1021, SpiParam.Pv, 0, 1, 1),
		new SpiEntry("mouse.ctrl", 0x101C, 0x101D, SpiParam.Pv, 0, 1, 0),
		new SpiEntry("mouse.trails", 0x005E, 0x005D, SpiParam.Ui, 0, 20, 0),
		new SpiEntry("mouse.snap", 0x005F, 0x0060, SpiParam.Ui, 0, 1, 0),
		new SpiEntry("mouse.clicklock", 0x101E, 0x101F, SpiParam.Pv, 0, 1, 0),
		new SpiEntry("mouse.clicklocktime", 0x2008, 0x2009, SpiParam.Pv, 200, 2200, 1200),
		new SpiEntry("keyboard.delay", 0x0016, 0x0017, SpiParam.Ui, 0, 3, 1),
		new SpiEntry("keyboard.rate", 0x000A, 0x000B, SpiParam.Ui, 0, 31, 31),
		new SpiEntry("keyboard.caretwidth", 0x2006, 0x2007, SpiParam.Pv, 1, 20, 1),
		new SpiEntry("keyboard.access", 0x100A, 0x100B, SpiParam.Pv, 0, 1, 0),
		new SpiEntry("access.animations", 0x1042, 0x1043, SpiParam.Pv, 0, 1, 1),
		new SpiEntry("access.duration", 0x2016, 0x2017, SpiParam.Pv, 1, 3600, 5)
	};

	internal static SpiEntry? Entry(string rowId) => Spi.FirstOrDefault((SpiEntry e) => e.RowId == rowId);
}

// A setting PC Settings reads and changes: the value is a string (an integer, "6,10,1", a GUID, a device id, "HH:mm:ss|HH:mm"),
// so the journal can keep it. Read and Apply go through the settings world; Apply runs on the SettingsWorker thread.
internal abstract class HubKnob
{
	internal readonly string RowId;

	protected HubKnob(string rowId)
	{
		RowId = rowId;
	}

	// The documented Windows default (null when there is none: no "Windows defaults" entry for the row).
	internal string? WindowsDefault { get; init; }

	internal bool Journal { get; init; } = true;

	internal KnobRisk Risk { get; init; }

	// Reads that can take long (power, radios, audio) are made off the UI thread.
	internal virtual bool Slow => false;

	internal string Title => HubCatalog.Title(RowId);

	// The current value; null when it could not be read (the row stays disabled with "…").
	internal abstract string? Read(ISettingsWorld w);

	// 0 applied; 5 refused by policy (access denied); any other value refused. persist false: live (no ini file, no
	// broadcast); true: persisted and announced once.
	internal abstract int Apply(ISettingsWorld w, string value, bool persist);

	internal virtual string Format(string? value) => value ?? "…";

	internal virtual bool Same(string? a, string? b) => string.Equals(a, b, StringComparison.Ordinal);

	// The scheme GUID of a power value, the device of an audio default.
	internal virtual string? Context(ISettingsWorld w) => null;

	// The value in a given context (an undo of a power value goes to the scheme it was changed in, not the active one);
	// knobs without a context read and apply as usual.
	internal virtual string? ReadIn(ISettingsWorld w, string? context) => Read(w);

	internal virtual int ApplyIn(ISettingsWorld w, string value, bool persist, string? context) => Apply(w, value, persist);
}

internal sealed class SpiKnob : HubKnob
{
	internal readonly SpiEntry Entry;

	private readonly Func<string?, string>? _format;

	internal SpiKnob(SpiEntry e, Func<string?, string>? format = null) : base(e.RowId)
	{
		Entry = e;
		_format = format;
		WindowsDefault = e.Default?.ToString(CultureInfo.InvariantCulture);
		Risk = e.Read == SpiRead.SwapMetric ? KnobRisk.KeepGuard : KnobRisk.None;
	}

	internal override string? Read(ISettingsWorld w)
	{
		int? v = Entry.Read switch
		{
			SpiRead.DoubleClick => w.GetDoubleClickTime(),
			SpiRead.SwapMetric => w.GetSystemMetrics(23) != 0 ? 1 : 0,
			_ => w.SpiGet(Entry.Get)
		};
		return v?.ToString(CultureInfo.InvariantCulture);
	}

	internal override int Apply(ISettingsWorld w, string value, bool persist)
	{
		if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int v))
		{
			return 1;
		}
		uint ini = persist ? NativeSettingsTable.SpiPersist : NativeSettingsTable.SpiLive;
		bool ok = Entry.Param == SpiParam.Ui ? w.SpiSet(Entry.Set, unchecked((uint)v), IntPtr.Zero, ini) : w.SpiSet(Entry.Set, 0u, (nint)v, ini);
		return ok ? 0 : 1;
	}

	internal override string Format(string? value) => _format != null ? _format(value) : value ?? "…";

	// Pointer trails: 0 and 1 both mean off.
	internal override bool Same(string? a, string? b)
	{
		if (Entry.RowId == "mouse.trails" && int.TryParse(a, out int x) && int.TryParse(b, out int y))
		{
			return (x <= 1 && y <= 1) || x == y;
		}
		return base.Same(a, b);
	}
}

// Enhance pointer precision: the three SPI_GETMOUSE values ("6,10,1"). On keeps the user's own thresholds.
internal sealed class MouseAccelKnob : HubKnob
{
	internal MouseAccelKnob() : base("mouse.precision")
	{
		WindowsDefault = "6,10,1";
	}

	internal override string? Read(ISettingsWorld w)
	{
		int[]? a = w.SpiGetMouse();
		return a == null || a.Length < 3 ? null : string.Join(",", a.Take(3));
	}

	internal static int[] Parse(string? v)
	{
		int[] r = new int[3];
		string[] parts = (v ?? "").Split(',');
		for (int i = 0; i < 3 && i < parts.Length; i++)
		{
			int.TryParse(parts[i], NumberStyles.Integer, CultureInfo.InvariantCulture, out r[i]);
		}
		return r;
	}

	internal static bool IsOn(string? v) => Parse(v)[2] != 0;

	// The array for On or Off from the current one: On keeps custom thresholds (or uses 6 and 10), Off is 0,0,0.
	internal static string Toggle(string? current, bool on)
	{
		if (!on)
		{
			return "0,0,0";
		}
		int[] a = Parse(current);
		int t1 = a[0] != 0 ? a[0] : 6;
		int t2 = a[1] != 0 ? a[1] : 10;
		int speed = a[2] != 0 ? a[2] : 1;
		return t1 + "," + t2 + "," + speed;
	}

	internal override int Apply(ISettingsWorld w, string value, bool persist)
	{
		return w.SpiSetMouse(Parse(value), persist ? NativeSettingsTable.SpiPersist : NativeSettingsTable.SpiLive) ? 0 : 1;
	}

	internal override string Format(string? value) => value == null ? "…" : (IsOn(value) ? "On" : "Off");

	internal override bool Same(string? a, string? b) => a != null && b != null && Parse(a).SequenceEqual(Parse(b));
}

// Roll the wheel to scroll: "lines" or "page" (0xFFFFFFFF lines). Back to lines restores the last line count (or 3).
internal sealed class WheelModeKnob : HubKnob
{
	internal int LastLines = 3;

	internal WheelModeKnob() : base("mouse.wheel")
	{
		WindowsDefault = "lines";
	}

	internal override string? Read(ISettingsWorld w)
	{
		int? v = w.SpiGet(0x0068);
		if (v == null)
		{
			return null;
		}
		if (v.Value != -1 && v.Value > 0)
		{
			LastLines = v.Value;
		}
		return v.Value == -1 ? "page" : "lines";
	}

	internal override int Apply(ISettingsWorld w, string value, bool persist)
	{
		uint lines = value == "page" ? 0xFFFFFFFFu : (uint)Math.Max(1, LastLines);
		return w.SpiSet(0x0069, lines, IntPtr.Zero, persist ? NativeSettingsTable.SpiPersist : NativeSettingsTable.SpiLive) ? 0 : 1;
	}

	internal override string Format(string? value) => value == "page" ? "One screen at a time" : value == "lines" ? "Multiple lines at a time" : "…";
}

// Sticky, Filter and Toggle Keys through AccessibilityKeys behind the world (only the *_ON bit changes).
internal sealed class AccessKnob : HubKnob
{
	private readonly string _kind;

	internal AccessKnob(string rowId, string kind) : base(rowId)
	{
		_kind = kind;
		WindowsDefault = "0";
	}

	internal override string? Read(ISettingsWorld w)
	{
		bool on = _kind switch
		{
			"sticky" => w.ReadStickyKeys(),
			"filter" => w.ReadFilterKeys(),
			_ => w.ReadToggleKeys()
		};
		return on ? "1" : "0";
	}

	internal override int Apply(ISettingsWorld w, string value, bool persist)
	{
		bool on = value == "1";
		switch (_kind)
		{
		case "sticky":
			w.SetStickyKeys(on);
			break;
		case "filter":
			w.SetFilterKeys(on);
			break;
		default:
			w.SetToggleKeys(on);
			break;
		}
		return 0;
	}

	internal override string Format(string? value) => value == null ? "…" : value == "1" ? "On" : "Off";
}

// A power value of the active scheme (seconds or an action index), AC or DC; applied by making the scheme active again.
internal sealed class PowerKnob : HubKnob
{
	internal static readonly Guid SubVideo = new Guid("7516b95f-f776-4464-8c53-06167f40cc99");

	internal static readonly Guid VideoIdle = new Guid("3c0bc021-c8a8-4e07-a973-6b14cbcb2b7e");

	internal static readonly Guid SubSleep = new Guid("238c9fa8-0aad-41ed-83f4-97be242c8f20");

	internal static readonly Guid StandbyIdle = new Guid("29f6c1db-86da-48c5-9fdb-f2b67b1f44da");

	internal static readonly Guid SubButtons = new Guid("4f971e89-eebd-4455-a3de-9e5c9cbb4fa6");

	internal static readonly Guid PowerButton = new Guid("7648efa3-dd9c-4e3e-b566-50f929386280");

	internal static readonly Guid SleepButton = new Guid("96996bc0-ad50-47ec-923b-6f41874dd9eb");

	internal static readonly Guid LidAction = new Guid("5ca83367-6e45-459f-a27b-476b1d01c936");

	internal readonly Guid Sub;

	internal readonly Guid Setting;

	internal readonly bool Ac;

	internal readonly bool IsAction;

	internal PowerKnob(string rowId, Guid sub, Guid setting, bool ac, bool action = false) : base(rowId)
	{
		Sub = sub;
		Setting = setting;
		Ac = ac;
		IsAction = action;
	}

	internal override bool Slow => true;

	internal override string? Read(ISettingsWorld w)
	{
		Guid? scheme = w.ReadActivePlan();
		if (scheme == null)
		{
			return null;
		}
		return w.ReadPowerValue(scheme.Value, Sub, Setting, Ac)?.ToString(CultureInfo.InvariantCulture);
	}

	internal override int Apply(ISettingsWorld w, string value, bool persist)
	{
		Guid? scheme = w.ReadActivePlan();
		if (scheme == null || !int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int v))
		{
			return 1;
		}
		int r = w.WritePowerValue(scheme.Value, Sub, Setting, Ac, v);
		if (r != 0)
		{
			return r;
		}
		return w.SetActivePlan(scheme.Value) == 0 ? 0 : 1;
	}

	internal override string? Context(ISettingsWorld w) => w.ReadActivePlan()?.ToString("D");

	// The value in the scheme the journal recorded (the active one when there is none).
	internal override string? ReadIn(ISettingsWorld w, string? context)
	{
		if (!Guid.TryParse(context, out Guid scheme))
		{
			return Read(w);
		}
		return w.ReadPowerValue(scheme, Sub, Setting, Ac)?.ToString(CultureInfo.InvariantCulture);
	}

	// Writes the value to that scheme; the scheme is made active again (to apply it) only when it is the active one.
	internal override int ApplyIn(ISettingsWorld w, string value, bool persist, string? context)
	{
		if (!Guid.TryParse(context, out Guid scheme))
		{
			return Apply(w, value, persist);
		}
		if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int v))
		{
			return 1;
		}
		int r = w.WritePowerValue(scheme, Sub, Setting, Ac, v);
		if (r != 0)
		{
			return r;
		}
		return w.ReadActivePlan() == scheme ? (w.SetActivePlan(scheme) == 0 ? 0 : 1) : 0;
	}

	internal override string Format(string? value)
	{
		if (value == null || !int.TryParse(value, out int v))
		{
			return "…";
		}
		if (IsAction)
		{
			return ActionName(v);
		}
		return Duration(v);
	}

	internal static string ActionName(int v) => v switch
	{
		0 => "Do nothing",
		1 => "Sleep",
		2 => "Hibernate",
		3 => "Shut down",
		4 => "Turn off the display",
		_ => "Action " + v
	};

	internal static string Duration(int seconds)
	{
		if (seconds <= 0)
		{
			return "Never";
		}
		if (seconds % 3600 == 0)
		{
			int h = seconds / 3600;
			return h == 1 ? "1 hour" : h + " hours";
		}
		if (seconds % 60 == 0)
		{
			int m = seconds / 60;
			return m == 1 ? "1 minute" : m + " minutes";
		}
		return seconds + " seconds";
	}
}

// The active power plan (its GUID).
internal sealed class PowerPlanKnob : HubKnob
{
	internal static readonly Guid Balanced = new Guid("381b4222-f694-41f0-9685-ff5bb260df2e");

	internal static readonly Guid High = new Guid("8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c");

	internal static readonly Guid Saver = new Guid("a1841308-3541-4fab-bc81-f71556f20b4a");

	internal static readonly Guid Ultimate = new Guid("e9a42b02-d5df-448d-aa00-03f14749eb61");

	// Plan names (filled by reads on worker threads, read by the page on the UI thread).
	internal readonly ConcurrentDictionary<Guid, string> Names = new ConcurrentDictionary<Guid, string>();

	internal PowerPlanKnob() : base("power.plan")
	{
	}

	internal override bool Slow => true;

	internal override string? Read(ISettingsWorld w) => w.ReadActivePlan()?.ToString("D");

	internal override int Apply(ISettingsWorld w, string value, bool persist)
	{
		return Guid.TryParse(value, out Guid g) ? (w.SetActivePlan(g) == 0 ? 0 : 1) : 1;
	}

	internal override string Format(string? value)
	{
		if (value != null && Guid.TryParse(value, out Guid g) && Names.TryGetValue(g, out string? n))
		{
			return n;
		}
		return value ?? "…";
	}

	internal override bool Same(string? a, string? b) => Guid.TryParse(a, out Guid x) && Guid.TryParse(b, out Guid y) ? x == y : base.Same(a, b);
}

// One locale value of the user default (a format, the first day, the measurement system), announced with one "intl"
// broadcast when persisted.
internal sealed class LocaleKnob : HubKnob
{
	internal const uint STIMEFORMAT = 0x1003;

	internal const uint SSHORTTIME = 0x0079;

	internal const uint SSHORTDATE = 0x001F;

	internal const uint IFIRSTDAYOFWEEK = 0x100C;

	internal const uint IMEASURE = 0x000D;

	internal readonly uint LcType;

	private readonly Func<string?, string>? _format;

	internal LocaleKnob(string rowId, uint lctype, Func<string?, string>? format = null) : base(rowId)
	{
		LcType = lctype;
		_format = format;
	}

	internal override string? Read(ISettingsWorld w) => w.GetLocale(LcType);

	internal override int Apply(ISettingsWorld w, string value, bool persist)
	{
		if (!w.SetLocale(LcType, value))
		{
			return 1;
		}
		w.Broadcast("intl");
		return 0;
	}

	internal override string Format(string? value) => _format != null ? _format(value) : value ?? "…";
}

// 24-hour clock: the long and the short time format together ("HH:mm:ss|HH:mm"), one journal entry, one broadcast.
internal sealed class TimeFormatKnob : HubKnob
{
	internal TimeFormatKnob() : base("time.24h")
	{
	}

	internal override string? Read(ISettingsWorld w)
	{
		string? l = w.GetLocale(LocaleKnob.STIMEFORMAT);
		string? s = w.GetLocale(LocaleKnob.SSHORTTIME);
		return l == null || s == null ? null : l + "|" + s;
	}

	internal static bool Is24(string? v) => v != null && v.Split('|')[0].Contains('H');

	internal static string For(bool h24) => h24 ? "HH:mm:ss|HH:mm" : "h:mm:ss tt|h:mm tt";

	internal override int Apply(ISettingsWorld w, string value, bool persist)
	{
		string[] p = value.Split('|');
		if (p.Length != 2 || !w.SetLocale(LocaleKnob.STIMEFORMAT, p[0]) || !w.SetLocale(LocaleKnob.SSHORTTIME, p[1]))
		{
			return 1;
		}
		w.Broadcast("intl");
		return 0;
	}

	internal override string Format(string? value) => value == null ? "…" : (Is24(value) ? "On" : "Off");
}

// File Explorer: show file name extensions, show hidden files (the shell state flags), then one change notification.
internal sealed class ShellFlagKnob : HubKnob
{
	private readonly string _flag;

	internal ShellFlagKnob(string rowId, string flag) : base(rowId)
	{
		_flag = flag;
		WindowsDefault = "0";
	}

	internal override string? Read(ISettingsWorld w)
	{
		(bool ext, bool hidden) = w.ReadExplorerFlags();
		return (_flag == "hidden" ? hidden : ext) ? "1" : "0";
	}

	internal override int Apply(ISettingsWorld w, string value, bool persist)
	{
		w.SetExplorerFlag(_flag, value == "1");
		w.Broadcast("assoc");
		return 0;
	}

	internal override string Format(string? value) => value == null ? "…" : value == "1" ? "On" : "Off";
}

// The default audio device (console and multimedia), or the communications device alone. Value: the device id.
internal sealed class AudioDefaultKnob : HubKnob
{
	internal readonly bool Capture;

	internal readonly bool Communications;

	// The names of every device seen (reads run on worker threads, the pages read it on the UI thread): the journal's
	// text for a device that is gone. Thread-safe.
	internal readonly ConcurrentDictionary<string, string> Names = new ConcurrentDictionary<string, string>(StringComparer.Ordinal);

	private AudioEndpointInfo[] _devices = Array.Empty<AudioEndpointInfo>();

	// The active devices of the latest read: an immutable snapshot that each read replaces (an unplugged headset is not
	// offered as a choice any more).
	internal AudioEndpointInfo[] Devices => Volatile.Read(ref _devices);

	internal AudioDefaultKnob(string rowId, bool capture, bool communications) : base(rowId)
	{
		Capture = capture;
		Communications = communications;
	}

	internal override bool Slow => true;

	internal override string? Read(ISettingsWorld w)
	{
		AudioEndpointInfo[] list = w.ReadEndpoints(Capture);
		Remember(list);
		AudioEndpointInfo? d = list.FirstOrDefault((AudioEndpointInfo e) => Communications ? e.IsDefaultComms : e.IsDefault);
		return d?.Id ?? "";
	}

	internal void Remember(AudioEndpointInfo[] list)
	{
		foreach (AudioEndpointInfo e in list)
		{
			Names[e.Id] = e.Name;
		}
		Volatile.Write(ref _devices, list.ToArray());
	}

	internal override int Apply(ISettingsWorld w, string value, bool persist)
	{
		if (string.IsNullOrEmpty(value))
		{
			return 1;
		}
		w.SetAudioDefault(value, Communications);
		return 0;
	}

	internal override string Format(string? value) => value != null && Names.TryGetValue(value, out string? n) ? n : string.IsNullOrEmpty(value) ? "None" : value ?? "…";

	internal override string? Context(ISettingsWorld w) => null;
}

// A radio (Wi-Fi, Bluetooth) on or off, or every radio off ("all").
internal sealed class RadioKnob : HubKnob
{
	private readonly string _kind;

	internal RadioKnob(string rowId, string kind) : base(rowId)
	{
		_kind = kind;
	}

	internal override bool Slow => true;

	// "Turn off all wireless" remembers which radios were on when it turned them off (the last such record of this
	// process), so turning it off again or an Undo brings back exactly those, not every radio.
	private string? _lastOn;

	private static readonly string[] Kinds = new string[2] { "wifi", "bluetooth" };

	// "all": "1" when no radio is on; "0|wifi=1;bluetooth=0" (which radios are on) otherwise. Other kinds: "1" or "0".
	internal override string? Read(ISettingsWorld w)
	{
		if (_kind == "all")
		{
			List<string> states = new List<string>();
			bool any = false;
			bool anyRead = false;
			foreach (string k in Kinds)
			{
				bool? on = w.ReadRadio(k);
				if (on == null)
				{
					continue;
				}
				anyRead = true;
				any |= on.Value;
				states.Add(k + "=" + (on.Value ? "1" : "0"));
			}
			if (!anyRead)
			{
				return null;
			}
			return any ? "0|" + string.Join(";", states) : "1";
		}
		bool? r = w.ReadRadio(_kind);
		return r == null ? null : r.Value ? "1" : "0";
	}

	internal static bool AllOff(string? v) => v != null && v.Split('|')[0] == "1";

	internal override int Apply(ISettingsWorld w, string value, bool persist)
	{
		if (_kind != "all")
		{
			return w.SetRadio(_kind, value == "1") ? 0 : 1;
		}
		if (AllOff(value))
		{
			// what was on before everything goes off
			string? now = Read(w);
			if (now != null && !AllOff(now))
			{
				_lastOn = now;
			}
			return w.SetAllWireless(true) ? 0 : 1;
		}
		string? record = value.Contains('|') ? value : _lastOn;
		if (record == null || !record.Contains('|'))
		{
			// nothing recorded: Wi-Fi comes back (every radio only when there is no Wi-Fi radio)
			return w.ReadRadio("wifi") != null ? (w.SetRadio("wifi", true) ? 0 : 1) : (w.SetAllWireless(false) ? 0 : 1);
		}
		bool ok = true;
		bool anyOn = false;
		foreach (string part in record.Substring(record.IndexOf('|') + 1).Split(';', StringSplitOptions.RemoveEmptyEntries))
		{
			int eq = part.IndexOf('=');
			if (eq <= 0)
			{
				continue;
			}
			bool on = part.Substring(eq + 1) == "1";
			anyOn |= on;
			ok &= w.SetRadio(part.Substring(0, eq), on);
		}
		if (!anyOn)
		{
			ok &= w.ReadRadio("wifi") == null || w.SetRadio("wifi", true);
		}
		return ok ? 0 : 1;
	}

	// "all" compares the on/off part only (which radios were on is the record, not the setting).
	internal override bool Same(string? a, string? b) => _kind == "all" ? a != null && b != null && AllOff(a) == AllOff(b) : base.Same(a, b);

	internal override string Format(string? value) => value == null ? "…" : (_kind == "all" ? AllOff(value) : value == "1") ? "On" : "Off";
}

// The output volume of the default device as a journaled knob: the volume slider itself stays transient (not journaled);
// a fix ("Set the volume to 40%") and a Setup go through this knob, so they are undone like any change.
internal sealed class EndpointVolumeKnob : HubKnob
{
	internal EndpointVolumeKnob() : base("sound.volume")
	{
	}

	internal override bool Slow => true;

	internal override string? Read(ISettingsWorld w) => w.ReadEndpointVolume(false)?.Volume.ToString(CultureInfo.InvariantCulture);

	internal override int Apply(ISettingsWorld w, string value, bool persist)
	{
		if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int v))
		{
			return 1;
		}
		w.SetEndpointVolume(false, Math.Clamp(v, 0, 100));
		return 0;
	}

	internal override string Format(string? value) => value == null ? "…" : value + "%";
}

// Mute of the default output device ("1" muted), journaled for the same reason.
internal sealed class EndpointMuteKnob : HubKnob
{
	internal EndpointMuteKnob() : base("sound.mute")
	{
	}

	internal override bool Slow => true;

	internal override string? Read(ISettingsWorld w)
	{
		(int Volume, bool Muted)? v = w.ReadEndpointVolume(false);
		return v == null ? null : v.Value.Muted ? "1" : "0";
	}

	internal override int Apply(ISettingsWorld w, string value, bool persist)
	{
		w.SetEndpointMute(false, value == "1");
		return 0;
	}

	internal override string Format(string? value) => value == null ? "…" : value == "1" ? "On" : "Off";
}

// A launcher setting (an AppSettings property) with its live-apply hook: the same path as the launcher's own row, so it
// is journaled and undone like a Windows setting. Under read-only diagnostics it writes the diagnostics override.
internal sealed class LauncherKnob : HubKnob
{
	private readonly PropertyInfo _prop;

	private readonly Action<string>? _apply;

	internal LauncherKnob(string rowId, string property, Action<string>? apply) : base(rowId)
	{
		_prop = typeof(AppSettings).GetProperty(property) ?? throw new ArgumentException("no AppSettings property " + property);
		_apply = apply;
	}

	internal override string? Read(ISettingsWorld w) => Convert.ToString(_prop.GetValue(SettingsStore.FastSnapshot), CultureInfo.InvariantCulture);

	internal override int Apply(ISettingsWorld w, string value, bool persist)
	{
		object v = _prop.PropertyType == typeof(bool) ? value == "True" || value == "1" : _prop.PropertyType == typeof(int) ? int.Parse(value, CultureInfo.InvariantCulture) : value;
		HubSettings.Update(delegate(AppSettings s) { _prop.SetValue(s, v); });
		_apply?.Invoke(value);
		return 0;
	}
}

// Launcher settings writes: the settings store, or (read-only diagnostics) the in-memory override QA gave.
internal static class HubSettings
{
	internal static void Update(Action<AppSettings> mutate)
	{
		if (SettingsStore.ReadOnlyDiagnostics)
		{
			if (SettingsStore.DiagnosticsOverride != null)
			{
				mutate(SettingsStore.DiagnosticsOverride);
			}
			return;
		}
		SettingsStore.Update(mutate);
	}
}

// The knobs of the hub, built once (row id -> knob), so the journal can undo an entry after the window was closed.
internal static class HubKnobs
{
	private static Dictionary<string, HubKnob>? _all;

	private static readonly object Gate = new object();

	internal static readonly string[] Delays = new string[4] { "Short", "Medium short", "Medium long", "Long" };

	internal static HubKnob? Get(string rowId)
	{
		Ensure();
		return _all!.TryGetValue(rowId, out HubKnob? k) ? k : null;
	}

	internal static IEnumerable<HubKnob> All
	{
		get
		{
			Ensure();
			return _all!.Values;
		}
	}

	private static string Ms(string? v) => v == null ? "…" : v + " ms";

	private static string OnOff(string? v) => v == null ? "…" : v != "0" ? "On" : "Off";

	private static void Ensure()
	{
		if (_all != null)
		{
			return;
		}
		lock (Gate)
		{
			if (_all != null)
			{
				return;
			}
			Dictionary<string, HubKnob> d = new Dictionary<string, HubKnob>(StringComparer.OrdinalIgnoreCase);
			void Add(HubKnob k)
			{
				d[k.RowId] = k;
			}
			Func<string?, string> fmt(string id) => id switch
			{
				"mouse.speed" => (string? v) => v == null ? "…" : v,
				"mouse.primary" => (string? v) => v == null ? "…" : v == "1" ? "Right" : "Left",
				"mouse.doubleclick" => Ms,
				"mouse.clicklocktime" => Ms,
				"mouse.lines" => (string? v) => v == null ? "…" : v == "-1" ? "One screen" : v + (v == "1" ? " line" : " lines"),
				"mouse.hchars" => (string? v) => v == null ? "…" : v + (v == "1" ? " character" : " characters"),
				"mouse.trails" => (string? v) => v == null ? "…" : int.TryParse(v, out int t) && t <= 1 ? "Off" : t == 3 ? "Short" : t == 7 ? "Medium" : t == 12 ? "Long" : v,
				"keyboard.delay" => (string? v) => v == null ? "…" : int.TryParse(v, out int x) && x >= 0 && x <= 3 ? Delays[x] : v,
				"keyboard.rate" => (string? v) => v == null ? "…" : v + " of 31",
				"keyboard.caretwidth" => (string? v) => v == null ? "…" : v + " px",
				"access.duration" => (string? v) => v == null ? "…" : v + " seconds",
				_ => OnOff
			};
			foreach (SpiEntry e in NativeSettingsTable.Spi)
			{
				Add(new SpiKnob(e, fmt(e.RowId)));
			}
			Add(new MouseAccelKnob());
			Add(new WheelModeKnob());
			Add(new AccessKnob("access.sticky", "sticky"));
			Add(new AccessKnob("access.filter", "filter"));
			Add(new AccessKnob("access.toggle", "toggle"));
			Add(new PowerPlanKnob());
			Add(new PowerKnob("power.screen", PowerKnob.SubVideo, PowerKnob.VideoIdle, ac: true));
			Add(new PowerKnob("power.screen.dc", PowerKnob.SubVideo, PowerKnob.VideoIdle, ac: false));
			Add(new PowerKnob("power.sleep", PowerKnob.SubSleep, PowerKnob.StandbyIdle, ac: true));
			Add(new PowerKnob("power.sleep.dc", PowerKnob.SubSleep, PowerKnob.StandbyIdle, ac: false));
			Add(new PowerKnob("power.button", PowerKnob.SubButtons, PowerKnob.PowerButton, ac: true, action: true));
			Add(new PowerKnob("power.sleepbutton", PowerKnob.SubButtons, PowerKnob.SleepButton, ac: true, action: true));
			Add(new PowerKnob("power.lid", PowerKnob.SubButtons, PowerKnob.LidAction, ac: true, action: true));
			Add(new TimeFormatKnob());
			Add(new LocaleKnob("time.shortdate", LocaleKnob.SSHORTDATE));
			Add(new LocaleKnob("time.firstday", LocaleKnob.IFIRSTDAYOFWEEK, (string? v) => v != null && int.TryParse(v, out int i) && i >= 0 && i <= 6 ? CultureInfo.InvariantCulture.DateTimeFormat.DayNames[(i + 1) % 7] : v ?? "…"));
			Add(new LocaleKnob("time.measure", LocaleKnob.IMEASURE, (string? v) => v == null ? "…" : v == "1" ? "US" : "Metric"));
			Add(new ShellFlagKnob("apps.extensions", "extensions"));
			Add(new ShellFlagKnob("apps.hidden", "hidden"));
			Add(new AudioDefaultKnob("sound.output", capture: false, communications: false));
			Add(new AudioDefaultKnob("sound.calls", capture: false, communications: true));
			Add(new AudioDefaultKnob("sound.input", capture: true, communications: false));
			Add(new RadioKnob("network.wifitoggle", "wifi"));
			Add(new RadioKnob("network.allwireless", "all"));
			Add(new RadioKnob("devices.bttoggle", "bluetooth"));
			Add(new EndpointVolumeKnob());
			Add(new EndpointMuteKnob());
			Add(new LauncherKnob("appearance.motion", "MotionMode", delegate(string v) { Motion.Mode = Motion.Parse(v); }));
			_all = d;
		}
	}
}
