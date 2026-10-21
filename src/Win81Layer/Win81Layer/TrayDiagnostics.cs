#nullable enable

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Windows;
using System.Windows.Threading;

namespace Win81Layer;

// --traytest <outDir>: offscreen QA of the tray protocol, the tray table and the no-hang guarantees.
//
// Read-only and self-contained by construction:
//   - It never creates or registers a Shell_TrayWnd: the tray table is fed constructed WM_COPYDATA payloads directly.
//   - Every window it sends to is a message-only window it created itself (never visible, not enumerable, cannot be
//     activated); the hung windows belong to a test thread that never pumps and destroys them without pumping.
//   - It never grants, takes or moves the foreground (DiagnosticsNoForeground), never calls ForceForeground and never
//     touches settings, state or the registry. Results go to outDir only.
//
// What it proves:
//   1. TrayProtocol matches an independent table of what Explorer sends, per version (0 / 3 / 4) and gesture
//      (double clicks split at the press; the unknown-version sequence of the legacy toolbar source).
//   2. TrayReader.Forward delivers exactly that (message, wParam, lParam) sequence to a real window.
//   3. The host table keeps a working callback through real framework sequences (Electron incl. the lost NIM_ADD,
//      re-registration and restart, Chromium, WinForms, Qt / Win32 version 4, guid icons, phantoms and their
//      restoring, re-created windows across a rescan, hidden icons), repairs every icon without a callback (targeted
//      TaskbarCreated, throttled; Electron / Chromium guess; a deleted icon's callback and version), and passes tray
//      commands on to Explorer's taskbar (a test window here).
//   4. No click-path call blocks more than ~200 ms when the target belongs to a thread that stopped answering, the
//      foreground helper still serves a busy (not hung) app, and queued window actions are made even for a hung one.
internal static class TrayDiagnostics
{
	private static readonly List<(string Name, bool Pass, string Detail)> Checks = new List<(string, bool, string)>();

	private static readonly Dictionary<string, object> Metrics = new Dictionary<string, object>();

	private static readonly string DataRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Win81Layer");

	private const uint CB = 0x8123u;

	private const uint ElectronCB = 0x8001u;

	private static long _clock = 1_000_000L;

	public static void Begin(Application app, string[] args)
	{
		app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
		int idx = Array.IndexOf(args, "--traytest");
		string outDir = (idx >= 0 && idx + 1 < args.Length && !args[idx + 1].StartsWith("--", StringComparison.Ordinal))
			? args[idx + 1]
			: Path.Combine(DataRoot, "qa", "tray");
		app.Dispatcher.BeginInvoke((Action)delegate
		{
			int code = 1;
			try
			{
				code = Run(outDir) ? 0 : 1;
			}
			catch (Exception ex)
			{
				try
				{
					Directory.CreateDirectory(outDir);
					File.WriteAllText(Path.Combine(outDir, "traytest-error.txt"), ex.ToString());
				}
				catch
				{
				}
			}
			app.Shutdown(code);
		}, DispatcherPriority.Loaded);
	}

	private static bool Run(string outDir)
	{
		Directory.CreateDirectory(outDir);
		string before = OutsideStamp();
		nint fgBefore = GetForegroundWindow();
		TrayReader.DiagnosticsNoForeground = true;
		WindowList.DiagnosticsNoForeground = true;
		TrayHostService.TestAllowOwnProcess = true;
		TrayHostService.ClockForTest = () => _clock;
		TrayHostService.ResetForTest();
		Stopwatch total = Stopwatch.StartNew();
		try
		{
			using Recorder rec = new Recorder();
			Check("recorder-ready", rec.Ready, rec.Ready ? "message-only test windows on their own pumping thread" : "recorder thread did not start");
			ProtocolChecks();
			DeliveryChecks(rec);
			FrameworkChecks(rec);
			HostTableChecks(rec);
			RecoveryChecks(rec);
			IconRectChecks(rec);
			PolicyChecks();
			HangChecks(rec);
			BreadcrumbChecks();
		}
		catch (Exception ex)
		{
			Check("harness-completed", false, ex.ToString());
		}
		finally
		{
			TrayReader.DiagnosticsNoForeground = false;
			WindowList.DiagnosticsNoForeground = false;
			TrayHostService.TestAllowOwnProcess = false;
			TrayHostService.ClockForTest = null;
			TrayHostService.IconRectProvider = null;
			TrayHostService.CommandTargetForTest = null;
			TrayHostService.ResetForTest();
			Breadcrumbs.StallMs = 3000;
		}
		Check("safety-no-tray-host", !TrayHostService.Active && !OwnShellTrayWnd(), "no Shell_TrayWnd was created or registered by the test");
		string after = OutsideStamp();
		Check("safety-data-folder-unchanged", before == after, before == after ? "settings and data folder unchanged" : $"before={before} after={after}");
		Metrics["ForegroundUnchanged"] = fgBefore == GetForegroundWindow();
		Metrics["TotalMs"] = total.ElapsedMilliseconds;

		bool passed = Checks.All(c => c.Pass);
		StringBuilder txt = new StringBuilder();
		foreach ((string name, bool pass, string detail) in Checks)
		{
			txt.AppendLine(pass ? "PASS " + name + (detail.Length > 0 && detail.Length < 200 ? "  (" + detail + ")" : "") : "FAIL " + name + ": " + detail);
		}
		txt.AppendLine($"{Checks.Count(c => c.Pass)}/{Checks.Count} passed");
		File.WriteAllText(Path.Combine(outDir, "traytest-results.txt"), txt.ToString());
		var report = new
		{
			SchemaVersion = 1,
			GeneratedUtc = DateTime.UtcNow,
			Passed = passed,
			Checks = Checks.Select(c => new { c.Name, c.Pass, c.Detail }).ToArray(),
			Metrics
		};
		File.WriteAllText(Path.Combine(outDir, "traytest-report.json"), JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
		return passed;
	}

	private static void Check(string name, bool pass, string detail = "")
	{
		Checks.Add((name, pass, detail));
	}

	// ===================================================================== 1. protocol table

	// What Explorer sends, written out independently of TrayProtocol (Shell_NotifyIcon / NOTIFYICONDATA docs; Explorer
	// behaviour as RetroBar / ManagedShell, Qt, Hardcodet and Electron rely on it).
	private static uint[] Spec(uint version, TrayEvent ev)
	{
		bool v3 = version >= 3;
		switch (ev)
		{
		case TrayEvent.LeftClick:
			return v3 ? new uint[] { 0x200, 0x201, 0x202, 0x400 } : new uint[] { 0x200, 0x201, 0x202 };
		case TrayEvent.LeftDoubleClick:
			return v3 ? new uint[] { 0x203, 0x202, 0x400 } : new uint[] { 0x203, 0x202 };
		case TrayEvent.RightClick:
			return v3 ? new uint[] { 0x200, 0x204, 0x205, 0x7B } : new uint[] { 0x200, 0x204, 0x205 };
		case TrayEvent.MiddleClick:
			return new uint[] { 0x200, 0x207, 0x208 };
		case TrayEvent.Hover:
			return new uint[] { 0x200 };
		case TrayEvent.KeySelect:
			return v3 ? new uint[] { 0x401 } : new uint[] { 0x204, 0x205 };
		case TrayEvent.KeyContextMenu:
			return v3 ? new uint[] { 0x7B } : new uint[] { 0x204, 0x205 };
		case TrayEvent.LeftDoubleClickPress:
			return new uint[] { 0x203 };
		case TrayEvent.LeftDoubleClickRelease:
			return v3 ? new uint[] { 0x202, 0x400 } : new uint[] { 0x202 };
		case TrayEvent.RightDoubleClick:
			return v3 ? new uint[] { 0x206, 0x205, 0x7B } : new uint[] { 0x206, 0x205 };
		case TrayEvent.MiddleDoubleClick:
			return new uint[] { 0x209, 0x208 };
		}
		return Array.Empty<uint>();
	}

	private static readonly TrayEvent[] AllEvents = (TrayEvent[])Enum.GetValues(typeof(TrayEvent));

	private static readonly uint[] Versions = new uint[3] { 0, 3, 4 };

	private static void ProtocolChecks()
	{
		List<string> bad = new List<string>();
		int n = 0;
		foreach (uint v in Versions)
		{
			foreach (TrayEvent ev in AllEvents)
			{
				n++;
				uint[] want = Spec(v, ev);
				uint[] got = TrayProtocol.Events(v, ev).ToArray();
				if (!want.SequenceEqual(got))
				{
					bad.Add($"v{v} {ev}: want [{Names(want)}] got [{Names(got)}]");
				}
			}
		}
		Check("protocol-matches-explorer-table", bad.Count == 0, bad.Count == 0 ? $"{n} version x gesture sequences" : string.Join("; ", bad));
		Check("protocol-v0-right-click-has-no-contextmenu", !TrayProtocol.Events(0, TrayEvent.RightClick).Contains(0x7Bu), "version 0 gets WM_RBUTTONDOWN / WM_RBUTTONUP only");
		Check("protocol-v4-clicks-carry-mouse-messages", TrayProtocol.Events(4, TrayEvent.LeftClick).Contains(0x202u) && TrayProtocol.Events(4, TrayEvent.RightClick).Contains(0x205u), "Hardcodet acts on WM_LBUTTONUP / WM_RBUTTONUP");
		// Packing: zero-extended halves, uID in the high word for version 4.
		TrayMessage m4 = TrayProtocol.Pack(4, 7, 0x7B, -5, 1079);
		TrayMessage m0 = TrayProtocol.Pack(0, 0x9000, 0x205, 0, 0);
		bool pack = (long)m4.WParam == 0x0437FFFBL && (long)m4.LParam == 0x0007007BL && (long)m0.WParam == 0x9000L && (long)m0.LParam == 0x205L;
		Check("protocol-packing", pack, $"v4 w=0x{(long)m4.WParam:X} l=0x{(long)m4.LParam:X}; v0 w=0x{(long)m0.WParam:X} l=0x{(long)m0.LParam:X}");
		Check("protocol-double-click-press-parity", !TrayProtocol.IsDoubleClickPress(1) && TrayProtocol.IsDoubleClickPress(2) && !TrayProtocol.IsDoubleClickPress(3) && TrayProtocol.IsDoubleClickPress(4), "Windows: DOWN, DBLCLK, DOWN, DBLCLK");
		// The double click's second half is split at the press, as Explorer sends it: DBLCLK when the button goes down,
		// WM_LBUTTONUP (+ NIN_SELECT) on release; together they are the whole second half.
		bool split = true;
		foreach (uint v in Versions)
		{
			List<uint> joined = TrayProtocol.Events(v, TrayEvent.LeftDoubleClickPress).Concat(TrayProtocol.Events(v, TrayEvent.LeftDoubleClickRelease)).ToList();
			split &= joined.SequenceEqual(TrayProtocol.Events(v, TrayEvent.LeftDoubleClick)) && TrayProtocol.Events(v, TrayEvent.LeftDoubleClickPress).SequenceEqual(new uint[] { 0x203 });
		}
		Check("protocol-double-click-dblclk-at-press", split, "press = WM_LBUTTONDBLCLK only; press + release = the whole second half, every version");
		// Unknown version (the legacy toolbar source): version 0 messages, then the version 3 notification, except for the
		// Chromium family (version 0, menu on WM_CONTEXTMENU too).
		bool unknown = TrayProtocol.EventsUnknownVersion(TrayEvent.RightClick, chromiumFamily: false).SequenceEqual(new uint[] { 0x200, 0x204, 0x205, 0x7B })
			&& TrayProtocol.EventsUnknownVersion(TrayEvent.LeftClick, chromiumFamily: false).SequenceEqual(new uint[] { 0x200, 0x201, 0x202, 0x400 })
			&& TrayProtocol.EventsUnknownVersion(TrayEvent.KeySelect, chromiumFamily: false).SequenceEqual(new uint[] { 0x204, 0x205, 0x401 })
			&& TrayProtocol.EventsUnknownVersion(TrayEvent.RightClick, chromiumFamily: true).SequenceEqual(new uint[] { 0x200, 0x204, 0x205 })
			&& TrayProtocol.EventsUnknownVersion(TrayEvent.LeftClick, chromiumFamily: true).SequenceEqual(new uint[] { 0x200, 0x201, 0x202 });
		Check("protocol-unknown-version", unknown, "version 0 sequence + NIN_SELECT / NIN_KEYSELECT / WM_CONTEXTMENU, none of them for Electron / Chromium");
	}

	// ===================================================================== 2. delivery

	private static void DeliveryChecks(Recorder rec)
	{
		nint w = rec.Create(Recorder.GenericClass);
		List<string> bad = new List<string>();
		int n = 0;
		foreach (uint v in Versions)
		{
			foreach (TrayEvent ev in AllEvents)
			{
				n++;
				int x = -5, y = 1079;
				int sent = TrayReader.Forward(w, CB, 7u, v, ev, x, y);
				List<Recorder.Msg> got = rec.Drain(w);
				List<Recorder.Msg> want = Spec(v, ev).Select(e => Expected(v, 7u, e, x, y)).ToList();
				if (sent != want.Count || !got.SequenceEqual(want))
				{
					bad.Add($"v{v} {ev}: want [{string.Join(", ", want)}] got [{string.Join(", ", got)}] sent={sent}");
				}
			}
		}
		Check("delivery-exact-sequences", bad.Count == 0, bad.Count == 0 ? $"{n} sequences delivered byte-exact to a test window" : string.Join(" | ", bad));
		int none = TrayReader.Forward(w, 0u, 7u, 0u, TrayEvent.RightClick, 0, 0);
		Check("delivery-no-callback-sends-nothing", none == 0 && rec.Drain(w).Count == 0, "callback 0: nothing is posted (it would be WM_NULL)");
		// Unknown version (legacy toolbar source): a generic owner gets the version 3 notification after the version 0
		// messages (a Qt / Win32 version 4 icon opens its menu on WM_CONTEXTMENU only); an Electron owner does not.
		TrayReader.Forward(w, CB, 7u, 0u, TrayEvent.RightClick, 1, 2, versionKnown: false);
		List<Recorder.Msg> gen = rec.Drain(w);
		nint el = rec.Create(Recorder.ElectronClass);
		TrayReader.Forward(el, CB, 7u, 0u, TrayEvent.RightClick, 1, 2, versionKnown: false);
		List<Recorder.Msg> ele = rec.Drain(el);
		bool genOk = gen.SequenceEqual(new[] { new Recorder.Msg(CB, 7, 0x200), new Recorder.Msg(CB, 7, 0x204), new Recorder.Msg(CB, 7, 0x205), new Recorder.Msg(CB, 7, 0x7B) });
		bool eleOk = ele.SequenceEqual(new[] { new Recorder.Msg(CB, 7, 0x200), new Recorder.Msg(CB, 7, 0x204), new Recorder.Msg(CB, 7, 0x205) });
		Check("delivery-unknown-version", genOk && eleOk, $"generic: {string.Join(", ", gen)} | Electron: {string.Join(", ", ele)}");
		rec.Destroy(el);
		rec.Destroy(w);
	}

	private static Recorder.Msg Expected(uint version, uint id, uint evt, int x, int y)
	{
		if (version >= 4)
		{
			return new Recorder.Msg(CB, (nint)(long)(((uint)y << 16) | ((uint)x & 0xFFFF)), (nint)(long)((id << 16) | (evt & 0xFFFF)));
		}
		return new Recorder.Msg(CB, (nint)(long)id, (nint)(long)evt);
	}

	// What each framework's tray code does with what we send (its documented / source-level handling), so the user-visible
	// effect is checked, not only the bytes.
	private static void FrameworkChecks(Recorder rec)
	{
		nint w = rec.Create(Recorder.GenericClass);
		// Electron / Chromium (version 0): every WM_LBUTTONDOWN, WM_RBUTTONDOWN, WM_MBUTTONDOWN, the DBLCLKs and
		// WM_CONTEXTMENU is one click event; a right click must open the menu exactly once.
		TrayReader.Forward(w, CB, 3u, 0u, TrayEvent.RightClick, 10, 10);
		int electronRight = rec.Drain(w).Count(m => ElectronClick((uint)(long)m.L));
		TrayReader.Forward(w, CB, 3u, 0u, TrayEvent.LeftClick, 10, 10);
		TrayReader.Forward(w, CB, 3u, 0u, TrayEvent.LeftDoubleClick, 10, 10);
		List<Recorder.Msg> dbl = rec.Drain(w);
		int electronDblClicks = dbl.Count(m => ElectronClick((uint)(long)m.L));
		bool electronDbl = dbl.Any(m => (uint)(long)m.L == 0x203u);
		Check("framework-electron-right-click-one-menu", electronRight == 1, $"{electronRight} menu pop(s) for one right click (was 2 with the extra WM_CONTEXTMENU)");
		Check("framework-electron-double-click", electronDblClicks == 2 && electronDbl, $"click + double-click events = {electronDblClicks} (Explorer: 2)");
		// WinForms NotifyIcon (version 0): Click on WM_LBUTTONUP, DoubleClick on WM_LBUTTONDBLCLK, menu on WM_RBUTTONUP.
		TrayReader.Forward(w, CB, 1u, 0u, TrayEvent.LeftClick, 10, 10);
		TrayReader.Forward(w, CB, 1u, 0u, TrayEvent.LeftDoubleClick, 10, 10);
		List<Recorder.Msg> wf = rec.Drain(w);
		int ups = wf.Count(m => (uint)(long)m.L == 0x202u);
		int dbls = wf.Count(m => (uint)(long)m.L == 0x203u);
		TrayReader.Forward(w, CB, 1u, 0u, TrayEvent.RightClick, 10, 10);
		int rups = rec.Drain(w).Count(m => (uint)(long)m.L == 0x205u);
		Check("framework-winforms", ups == 2 && dbls == 1 && rups == 1, $"double click: {ups} WM_LBUTTONUP + {dbls} WM_LBUTTONDBLCLK (Explorer: 2 + 1, was 4 + 1); right click: {rups} WM_RBUTTONUP");
		// Hardcodet WPF (version 4): acts only on WM_LBUTTONUP / WM_RBUTTONUP / WM_LBUTTONDBLCLK in the low word.
		TrayReader.Forward(w, CB, 0u, 4u, TrayEvent.LeftClick, 10, 10);
		bool hcLeft = rec.Drain(w).Any(m => ((long)m.L & 0xFFFF) == 0x202);
		TrayReader.Forward(w, CB, 0u, 4u, TrayEvent.RightClick, 10, 10);
		bool hcRight = rec.Drain(w).Any(m => ((long)m.L & 0xFFFF) == 0x205);
		Check("framework-hardcodet-v4", hcLeft && hcRight, "left and right click reach WM_LBUTTONUP / WM_RBUTTONUP handlers (were NIN_SELECT / WM_CONTEXTMENU only)");
		// Qt (version 4): activated(DoubleClick) on WM_LBUTTONDBLCLK, then ignores the NIN_SELECT that follows it.
		TrayReader.Forward(w, CB, 0u, 4u, TrayEvent.LeftClick, 10, 10);
		TrayReader.Forward(w, CB, 0u, 4u, TrayEvent.LeftDoubleClick, 10, 10);
		List<uint> qt = rec.Drain(w).Select(m => (uint)((long)m.L & 0xFFFF)).ToList();
		int triggers = 0, doubles = 0;
		bool ignoreNext = false;
		foreach (uint e in qt)
		{
			if (e == 0x400 || e == 0x401)
			{
				if (ignoreNext)
				{
					ignoreNext = false;
				}
				else
				{
					triggers++;
				}
			}
			else if (e == 0x203)
			{
				doubles++;
				ignoreNext = true;
			}
		}
		Check("framework-qt-v4", triggers == 1 && doubles == 1, $"Trigger={triggers} DoubleClick={doubles} (Explorer: 1 and 1; was 3 Triggers and no DoubleClick)");
		// The same double click as the bar sends it: press then release.
		TrayReader.Forward(w, CB, 0u, 4u, TrayEvent.LeftClick, 10, 10);
		TrayReader.Forward(w, CB, 0u, 4u, TrayEvent.LeftDoubleClickPress, 10, 10);
		TrayReader.Forward(w, CB, 0u, 4u, TrayEvent.LeftDoubleClickRelease, 10, 10);
		List<uint> qt2 = rec.Drain(w).Select(m => (uint)((long)m.L & 0xFFFF)).ToList();
		Check("framework-qt-v4-split-double", qt2.SequenceEqual(qt), $"[{Names(qt2)}] = the whole-gesture sequence");
		// Electron (version 0): a second right click within the double-click time arrives as WM_RBUTTONDBLCLK, which
		// Electron ignores (HandleClickEvent: no right double click), so one right double click opens the menu once
		// (the first click), not twice.
		TrayReader.Forward(w, CB, 3u, 0u, TrayEvent.RightClick, 10, 10);
		TrayReader.Forward(w, CB, 3u, 0u, TrayEvent.RightDoubleClick, 10, 10);
		int electronMenus = rec.Drain(w).Count(m => (uint)(long)m.L == 0x204u || (uint)(long)m.L == 0x7Bu);
		Check("framework-electron-right-double-click", electronMenus == 1, $"{electronMenus} menu pop(s) for a right double click (was 2)");
		rec.Destroy(w);
	}

	private static bool ElectronClick(uint evt)
	{
		return evt == 0x201 || evt == 0x204 || evt == 0x207 || evt == 0x203 || evt == 0x206 || evt == 0x209 || evt == 0x7B;
	}

	// ===================================================================== 3. host table

	private const uint NIF_MESSAGE = 0x01, NIF_ICON = 0x02, NIF_TIP = 0x04, NIF_STATE = 0x08, NIF_GUID = 0x20, NIF_SHOWTIP = 0x80;

	private const uint NIM_ADD = 0, NIM_MODIFY = 1, NIM_DELETE = 2, NIM_SETVERSION = 4;

	private static byte[] Payload(uint nim, nint hwnd, uint uid, uint flags, uint cb = 0, uint icon = 0, string? tip = null, uint version = 0, Guid guid = default, uint state = 0, uint stateMask = 0, int truncateNidTo = -1)
	{
		const int NidSize = 956;
		byte[] b = new byte[8 + NidSize];
		Put(b, 0, 0x34753423u);
		Put(b, 4, nim);
		int o = 8;
		Put(b, o + 0, NidSize);
		Put(b, o + 4, (uint)(long)hwnd);
		Put(b, o + 8, uid);
		Put(b, o + 12, flags);
		Put(b, o + 16, cb);
		Put(b, o + 20, icon);
		if (tip != null)
		{
			byte[] t = Encoding.Unicode.GetBytes(tip.Length > 127 ? tip.Substring(0, 127) : tip);
			Buffer.BlockCopy(t, 0, b, o + 24, t.Length);
		}
		Put(b, o + 280, state);
		Put(b, o + 284, stateMask);
		Put(b, o + 800, version);
		if (guid != Guid.Empty)
		{
			Buffer.BlockCopy(guid.ToByteArray(), 0, b, o + 936, 16);
		}
		if (truncateNidTo > 0)
		{
			Array.Resize(ref b, 8 + truncateNidTo);
			Put(b, o + 0, (uint)truncateNidTo);
		}
		return b;
	}

	private static void Put(byte[] b, int at, uint v)
	{
		BitConverter.GetBytes(v).CopyTo(b, at);
	}

	private static void Put(byte[] b, int at, int v)
	{
		BitConverter.GetBytes(v).CopyTo(b, at);
	}

	private static bool Send(byte[] payload)
	{
		return TrayHostService.HandleTrayPayload(payload, payload.Length);
	}

	private static TrayIconInfo? Icon(nint owner, uint id)
	{
		return TrayHostService.Snapshot().FirstOrDefault(i => i.OwnerHwnd == owner && i.Id == id);
	}

	private static string Describe(TrayIconInfo? i)
	{
		return i == null ? "absent" : $"owner=0x{(long)i.OwnerHwnd:X} id={i.Id} cb=0x{i.CallbackMessage:X} v={i.Version} icon=0x{(long)i.HIcon:X} tip='{i.Tooltip}' orphan={i.Orphan}";
	}

	private static void Fresh()
	{
		TrayHostService.ResetForTest();
		_clock += 600_000L;   // past every throttle window of the previous scenario
	}

	private static void HostTableChecks(Recorder rec)
	{
		uint tc = TrayHostService.TaskbarCreatedMessage();
		// --- Electron (Discord): NIM_ADD with NIF_MESSAGE only, then the icon, then the tooltip.
		Fresh();
		nint e1 = rec.Create(Recorder.ElectronClass);
		bool a1 = Send(Payload(NIM_ADD, e1, 3, NIF_MESSAGE, cb: ElectronCB));
		TrayIconInfo? s1 = Icon(e1, 3);
		bool m1 = Send(Payload(NIM_MODIFY, e1, 3, NIF_ICON, icon: 0x1234));
		bool m2 = Send(Payload(NIM_MODIFY, e1, 3, NIF_TIP, tip: "Discord"));
		TrayIconInfo? s2 = Icon(e1, 3);
		Check("electron-register", a1 && m1 && m2 && s1?.CallbackMessage == ElectronCB && s2?.CallbackMessage == ElectronCB && s2.HIcon == (nint)0x1234 && s2.Tooltip == "Discord" && s2.Version == 0 && s2.VersionKnown && rec.Drain(e1).Count == 0, Describe(s2));
		// TaskbarCreated: Electron's ResetIcon = NIM_DELETE, then NIM_ADD with NIF_MESSAGE | NIF_ICON (no tooltip).
		bool d1 = Send(Payload(NIM_DELETE, e1, 3, 0));
		int afterDelete = TrayHostService.Snapshot().Count;
		bool a2 = Send(Payload(NIM_ADD, e1, 3, NIF_MESSAGE | NIF_ICON, cb: ElectronCB, icon: 0x1235));
		TrayIconInfo? s3 = Icon(e1, 3);
		Check("electron-reregister-after-taskbarcreated", d1 && afterDelete == 0 && a2 && s3?.CallbackMessage == ElectronCB && s3.Tooltip == "Discord" && !s3.Orphan, Describe(s3) + " (tooltip restored from the deleted icon)");
		// Restart: the old host window dies, the new process registers from a new window.
		rec.Destroy(e1);
		nint e2 = rec.Create(Recorder.ElectronClass);
		Send(Payload(NIM_ADD, e2, 3, NIF_MESSAGE, cb: ElectronCB));
		Send(Payload(NIM_MODIFY, e2, 3, NIF_ICON | NIF_TIP, icon: 0x2000, tip: "Discord"));
		List<TrayIconInfo> snap = TrayHostService.Snapshot();
		Check("electron-restart", snap.Count == 1 && snap[0].OwnerHwnd == e2 && snap[0].CallbackMessage == ElectronCB, $"{snap.Count} icon(s): {string.Join("; ", snap.Select(Describe))}");
		// Two trays on one Electron host window (ids 3 and 4), both with empty tooltips after a re-registration.
		Send(Payload(NIM_ADD, e2, 4, NIF_MESSAGE | NIF_ICON, cb: ElectronCB, icon: 0x2001));
		Send(Payload(NIM_MODIFY, e2, 4, NIF_TIP, tip: "Second"));
		_clock += 5000;
		Send(Payload(NIM_DELETE, e2, 3, 0));
		Send(Payload(NIM_DELETE, e2, 4, 0));
		_clock += 10_000;   // past the tooltip memory: both come back without tooltips
		Send(Payload(NIM_ADD, e2, 3, NIF_MESSAGE | NIF_ICON, cb: ElectronCB, icon: 0x2000));
		Send(Payload(NIM_ADD, e2, 4, NIF_MESSAGE | NIF_ICON, cb: ElectronCB, icon: 0x2001));
		_clock += 5000;
		Send(Payload(NIM_MODIFY, e2, 3, NIF_ICON, icon: 0x2002));
		TrayHostService.RunDeferredCollapse();
		snap = TrayHostService.Snapshot();
		Check("electron-two-trays-one-window", snap.Count == 2 && snap.All(i => i.CallbackMessage == ElectronCB), $"{snap.Count} icon(s) (empty tooltips never merge icons)");
		rec.Destroy(e2);

		// --- The lost NIM_ADD (the Discord case): the first message we see is a NIM_MODIFY.
		Fresh();
		nint e3 = rec.Create(Recorder.ElectronClass);
		rec.Drain(e3);
		bool mm = Send(Payload(NIM_MODIFY, e3, 3, NIF_ICON, icon: 0x3000));
		TrayIconInfo? o1 = Icon(e3, 3);
		List<Recorder.Msg> nudges = rec.Drain(e3);
		Check("lost-add-modify-answers-false", !mm, "NIM_MODIFY for an unknown icon returns FALSE, as Explorer does");
		Check("lost-add-icon-still-shown", o1 != null && o1.Orphan && o1.HIcon == (nint)0x3000, Describe(o1));
		Check("lost-add-owner-nudged", nudges.Count == 1 && nudges[0].Message == tc, $"{nudges.Count} message(s) to the owner: {string.Join(", ", nudges)} (want one TaskbarCreated 0x{tc:X})");
		bool mm2 = Send(Payload(NIM_MODIFY, e3, 3, NIF_TIP, tip: "Discord"));
		Check("lost-add-later-modify-ok", mm2 && rec.Drain(e3).Count == 0, "the orphan now exists: later MODIFYs succeed and do not nudge again");
		// Electron answers TaskbarCreated: NIM_DELETE, then NIM_ADD with NIF_MESSAGE.
		Send(Payload(NIM_DELETE, e3, 3, 0));
		Send(Payload(NIM_ADD, e3, 3, NIF_MESSAGE | NIF_ICON, cb: ElectronCB, icon: 0x3001));
		TrayIconInfo? o2 = Icon(e3, 3);
		Check("lost-add-repaired", o2 != null && o2.CallbackMessage == ElectronCB && !o2.Orphan && o2.Tooltip == "Discord", Describe(o2));
		// Throttle: one nudge per window per 10 s, at most 3 per 5 minutes.
		Send(Payload(NIM_MODIFY, e3, 9, NIF_ICON, icon: 0x3002));
		int within10s = rec.Drain(e3).Count(m => m.Message == tc);
		int[] later = new int[3];
		for (int i = 0; i < 3; i++)
		{
			_clock += 11_000;
			Send(Payload(NIM_MODIFY, e3, 10u + (uint)i, NIF_ICON, icon: 0x3003));
			later[i] = rec.Drain(e3).Count(m => m.Message == tc);
		}
		Check("nudge-throttled", within10s == 0 && later[0] == 1 && later[1] == 1 && later[2] == 0, $"within 10 s: {within10s}; then {string.Join(",", later)} (max 3 per 5 min)");
		rec.Destroy(e3);

		// --- Chromium status tray (Chrome background apps, CEF): one callback, version 0.
		Fresh();
		nint c1 = rec.Create(Recorder.ChromeClass);
		Send(Payload(NIM_ADD, c1, 2, NIF_MESSAGE | NIF_ICON | NIF_TIP, cb: ElectronCB, icon: 0x4000, tip: "Google Chrome"));
		TrayIconInfo? ci = Icon(c1, 2);
		Check("chromium-register", ci?.CallbackMessage == ElectronCB && ci.Version == 0 && ci.Tooltip == "Google Chrome", Describe(ci));
		rec.Destroy(c1);

		// --- WinForms NotifyIcon (version 0) sending a shorter NOTIFYICONDATA.
		Fresh();
		nint wf = rec.Create(Recorder.GenericClass);
		bool wfa = Send(Payload(NIM_ADD, wf, 1, NIF_MESSAGE | NIF_ICON | NIF_TIP, cb: 0x0400 + 0x0400, icon: 0x5000, tip: "WinForms app", truncateNidTo: 152));
		TrayIconInfo? wfi = Icon(wf, 1);
		Check("winforms-short-payload", wfa && wfi?.CallbackMessage == 0x800u && wfi.Tooltip == "WinForms app" && wfi.Version == 0, Describe(wfi));
		rec.Destroy(wf);

		// --- Qt / Win32 version 4: NIM_ADD, then NIM_SETVERSION 4, which must reach the bar.
		Fresh();
		nint qt = rec.Create(Recorder.GenericClass);
		Send(Payload(NIM_ADD, qt, 0, NIF_MESSAGE | NIF_ICON | NIF_TIP | NIF_SHOWTIP, cb: CB, icon: 0x6000, tip: "Qt app"));
		int signalsBefore = TrayHostService.SignalCount;
		bool sv = Send(Payload(NIM_SETVERSION, qt, 0, 0, version: 4));
		TrayIconInfo? qi = Icon(qt, 0);
		Check("v4-setversion", sv && qi?.Version == 4 && TrayHostService.SignalCount > signalsBefore, Describe(qi) + " and the change is signalled to the bar");
		// A SETVERSION whose NIM_ADD never reached us: TRUE, kept on a provisional entry, owner nudged.
		rec.Drain(qt);
		bool sv2 = Send(Payload(NIM_SETVERSION, qt, 5, 0, version: 4));
		TrayIconInfo? qi2 = Icon(qt, 5);
		List<Recorder.Msg> qn = rec.Drain(qt);
		Check("setversion-miss-recovers", sv2 && qi2 != null && qi2.Version == 4 && qi2.Orphan && qn.Count(m => m.Message == tc) == 1, Describe(qi2) + $"; nudges={qn.Count(m => m.Message == tc)}");
		rec.Destroy(qt);

		// --- GUID icons: identity is the guid; the window can change; addressing by (window, id) still finds it.
		Fresh();
		Guid g = new Guid("8b2b4d7e-35c2-4a51-9f0d-6a1c2e9b7f10");
		nint g1 = rec.Create(Recorder.GenericClass);
		nint g2 = rec.Create(Recorder.GenericClass);
		Send(Payload(NIM_ADD, g1, 0, NIF_MESSAGE | NIF_ICON | NIF_GUID, cb: CB, icon: 0x7000, guid: g));
		Send(Payload(NIM_MODIFY, g2, 0, NIF_TIP | NIF_GUID, tip: "Guid app", guid: g));
		Send(Payload(NIM_MODIFY, g2, 0, NIF_ICON, icon: 0x7001));
		snap = TrayHostService.Snapshot();
		Check("guid-icon-identity", snap.Count == 1 && snap[0].OwnerHwnd == g2 && snap[0].Guid == g && snap[0].CallbackMessage == CB && snap[0].HIcon == (nint)0x7001, $"{snap.Count} icon(s): {string.Join("; ", snap.Select(Describe))}");
		Guid g2id = new Guid("8b2b4d7e-35c2-4a51-9f0d-6a1c2e9b7f11");
		Send(Payload(NIM_ADD, g2, 0, NIF_MESSAGE | NIF_ICON | NIF_GUID, cb: CB, icon: 0x7002, guid: g2id));
		snap = TrayHostService.Snapshot();
		bool distinctKeys = snap.Count == 2 && TrayAppIcon.KeyOf(snap[0].OwnerHwnd, snap[0].Id, snap[0].Guid) != TrayAppIcon.KeyOf(snap[1].OwnerHwnd, snap[1].Id, snap[1].Guid);
		Check("guid-two-icons-one-window", distinctKeys, $"{snap.Count} icon(s) with distinct bar keys");
		bool gd = Send(Payload(NIM_DELETE, g2, 0, NIF_GUID, guid: g));
		Check("guid-delete", gd && TrayHostService.Snapshot().Count == 1, "deleted by guid");
		rec.Destroy(g1);
		rec.Destroy(g2);

		// --- Two icons of one process on two windows with the same id (two Hardcodet icons, uID 0 each).
		Fresh();
		nint h1 = rec.Create(Recorder.GenericClass);
		nint h2 = rec.Create(Recorder.GenericClass);
		Send(Payload(NIM_ADD, h1, 0, NIF_MESSAGE | NIF_ICON | NIF_TIP, cb: CB, icon: 0x8000, tip: "Monitor"));
		Send(Payload(NIM_ADD, h2, 0, NIF_MESSAGE | NIF_ICON | NIF_TIP, cb: CB + 1, icon: 0x8001, tip: "Sync"));
		TrayIconInfo? hi1 = Icon(h1, 0), hi2 = Icon(h2, 0);
		Check("same-process-same-id-two-windows", hi1?.CallbackMessage == CB && hi2?.CallbackMessage == CB + 1, $"{Describe(hi1)} | {Describe(hi2)} (one shared entry before)");
		// The window is re-created: the old one dies, the same process and id continue from a new window. The bar rescans
		// in between (any app's tooltip update does): the dead window's icon leaves the bar at once but stays adoptable.
		rec.Destroy(h1);
		List<TrayIconInfo> between = TrayHostService.Snapshot();
		_clock += 2000;
		nint h3 = rec.Create(Recorder.GenericClass);
		rec.Drain(h3);
		bool adopt = Send(Payload(NIM_MODIFY, h3, 0, NIF_TIP, tip: "Monitor 2"));
		TrayIconInfo? hi3 = Icon(h3, 0);
		Check("recreated-window-adopted", between.Count == 1 && adopt && hi3?.CallbackMessage == CB && hi3.Tooltip == "Monitor 2" && !hi3.Orphan && rec.Drain(h3).Count == 0, $"{Describe(hi3)}; icons while the window was gone: {between.Count}");
		rec.Destroy(h2);
		rec.Destroy(h3);

		// --- Phantoms: Task Manager adds a new id on the same window and never deletes the old one.
		Fresh();
		nint tm = rec.Create(Recorder.GenericClass);
		Send(Payload(NIM_ADD, tm, 1, NIF_MESSAGE | NIF_ICON | NIF_TIP, cb: CB, icon: 0x9000, tip: "Task Manager"));
		_clock += 4000;
		Send(Payload(NIM_ADD, tm, 2, NIF_MESSAGE | NIF_ICON | NIF_TIP, cb: CB, icon: 0x9001, tip: "Task Manager"));
		snap = TrayHostService.Snapshot();
		bool folded = snap.Count == 1 && snap[0].Id == 2;
		rec.Drain(tm);
		bool pm = Send(Payload(NIM_MODIFY, tm, 1, NIF_TIP, tip: "Task Manager"));
		bool pd = Send(Payload(NIM_DELETE, tm, 1, 0));
		snap = TrayHostService.Snapshot();
		Check("phantom-folded", folded && pm && pd && snap.Count == 1 && snap[0].Id == 2 && rec.Drain(tm).Count == 0, "the idle duplicate is folded; a later MODIFY restores it (no orphan, no nudge) and its DELETE removes only it; the survivor stays");
		// At Task Manager's pace (a new id every second) each new id replaces the previous one at once: no duplicates.
		int[] counts = new int[3];
		for (int i = 0; i < 3; i++)
		{
			_clock += 1000;
			Send(Payload(NIM_ADD, tm, 3u + (uint)i, NIF_MESSAGE | NIF_ICON | NIF_TIP, cb: CB, icon: 0x9002, tip: "Task Manager"));
			counts[i] = TrayHostService.Snapshot().Count;
		}
		Check("phantom-task-manager-pace", counts.All(c => c == 1) && Icon(tm, 5) != null, $"icons after each new id: {string.Join(",", counts)}");
		// Icons that update together with the same tooltip (one per CPU core) are never merged.
		Fresh();
		nint ct = rec.Create(Recorder.GenericClass);
		for (uint i = 1; i <= 3; i++)
		{
			Send(Payload(NIM_ADD, ct, i, NIF_MESSAGE | NIF_ICON | NIF_TIP, cb: CB, icon: 0xA000 + i, tip: "CPU 45 C"));
		}
		for (int round = 0; round < 5; round++)
		{
			_clock += 1000;
			for (uint i = 1; i <= 3; i++)
			{
				Send(Payload(NIM_MODIFY, ct, i, NIF_ICON | NIF_TIP, icon: 0xA100 + i, tip: "CPU 46 C"));
			}
		}
		TrayHostService.RunDeferredCollapse();
		int afterRounds = TrayHostService.Snapshot().Count;
		// A long quiet period, then one of them speaks: still three (they were registered together).
		_clock += 20_000;
		Send(Payload(NIM_MODIFY, ct, 2, NIF_ICON, icon: 0xA200));
		TrayHostService.RunDeferredCollapse();
		int afterQuiet = TrayHostService.Snapshot().Count;
		Check("same-tooltip-live-icons-kept", afterRounds == 3 && afterQuiet == 3, $"{afterRounds} and {afterQuiet} icon(s) of 3 (updating together; after a quiet period)");
		// A re-created window whose old window stays alive: the old icon is folded once it has been quiet for 3 s, by
		// the deferred pass (no further message has to arrive).
		Fresh();
		nint dp = rec.Create(Recorder.GenericClass);
		nint dp2 = rec.Create(Recorder.GenericClass);
		Send(Payload(NIM_ADD, dp, 1, NIF_MESSAGE | NIF_TIP, cb: CB, tip: "Agent"));
		_clock += 1000;
		Send(Payload(NIM_ADD, dp2, 1, NIF_MESSAGE | NIF_TIP, cb: CB, tip: "Agent"));
		int beforeDeferred = TrayHostService.Snapshot().Count;
		_clock += 4000;
		TrayHostService.RunDeferredCollapse();
		snap = TrayHostService.Snapshot();
		Check("phantom-deferred-fold", beforeDeferred == 2 && snap.Count == 1 && snap[0].OwnerHwnd == dp2, $"{beforeDeferred} -> {snap.Count} (the new window's icon stays)");
		// The old window speaks after all: it is restored on its own window, and the survivor keeps its window and key (it
		// is not moved onto the old window), so the new window's next MODIFY still finds it (no orphan, no nudge).
		rec.Drain(dp);
		rec.Drain(dp2);
		bool oldBack = Send(Payload(NIM_MODIFY, dp, 1, NIF_TIP, tip: "Agent"));
		TrayIconInfo? oldIcon = Icon(dp, 1), newIcon = Icon(dp2, 1);
		bool newModify = Send(Payload(NIM_MODIFY, dp2, 1, NIF_ICON, icon: 0xA300));
		TrayIconInfo? newAfter = Icon(dp2, 1);
		_clock += 10_000;
		TrayHostService.RunDeferredCollapse();
		int keptApart = TrayHostService.Snapshot().Count;
		Check("phantom-unfold-recreated-window", oldBack && oldIcon?.CallbackMessage == CB && newIcon?.CallbackMessage == CB && newModify && newAfter != null && !newAfter.Orphan && newAfter.HIcon == (nint)0xA300 && keptApart == 2 && rec.Drain(dp).Count == 0 && rec.Drain(dp2).Count == 0, $"old: {Describe(oldIcon)} | new: {Describe(newAfter)} | after a quiet period: {keptApart} icon(s)");
		// Same-window fold: a folded id that is re-added (WinForms / Qt answer TaskbarCreated with NIM_ADD, no DELETE) or
		// modified comes back as its own icon; the survivor's image and tooltip are never overwritten by it.
		Fresh();
		nint sw = rec.Create(Recorder.GenericClass);
		Send(Payload(NIM_ADD, sw, 1, NIF_MESSAGE | NIF_ICON | NIF_TIP, cb: CB, icon: 0xD001, tip: "Sync"));
		_clock += 4000;
		Send(Payload(NIM_ADD, sw, 2, NIF_MESSAGE | NIF_ICON | NIF_TIP, cb: CB + 1, icon: 0xD002, tip: "Sync"));
		int foldedCount = TrayHostService.Snapshot().Count;
		rec.Drain(sw);
		bool readd = Send(Payload(NIM_ADD, sw, 1, NIF_MESSAGE | NIF_ICON | NIF_TIP, cb: CB, icon: 0xD003, tip: "Sync"));
		TrayIconInfo? r1 = Icon(sw, 1), r2 = Icon(sw, 2);
		_clock += 4000;
		Send(Payload(NIM_MODIFY, sw, 2, NIF_ICON, icon: 0xD004));
		_clock += 4000;
		Send(Payload(NIM_MODIFY, sw, 1, NIF_ICON, icon: 0xD005));
		TrayHostService.RunDeferredCollapse();
		TrayIconInfo? r1b = Icon(sw, 1), r2b = Icon(sw, 2);
		Check("phantom-unfold-same-window", foldedCount == 1 && readd && r1?.CallbackMessage == CB && r1.HIcon == (nint)0xD003 && r2?.HIcon == (nint)0xD002 && r2.CallbackMessage == CB + 1
			&& r1b?.HIcon == (nint)0xD005 && r2b?.HIcon == (nint)0xD004 && TrayHostService.Snapshot().Count == 2 && rec.Drain(sw).Count == 0,
			$"folded to {foldedCount}; re-added: {Describe(r1)} | survivor {Describe(r2)}; later: {Describe(r1b)} | {Describe(r2b)} (kept apart, no ping-pong)");
		rec.Destroy(tm);
		rec.Destroy(ct);
		rec.Destroy(dp);
		rec.Destroy(dp2);
		rec.Destroy(sw);

		// --- NIS_HIDDEN hides an icon, clearing it shows it again.
		Fresh();
		nint hd = rec.Create(Recorder.GenericClass);
		Send(Payload(NIM_ADD, hd, 1, NIF_MESSAGE | NIF_ICON, cb: CB, icon: 0xB000));
		Send(Payload(NIM_MODIFY, hd, 1, NIF_STATE, state: 1, stateMask: 1));
		bool hidden = Icon(hd, 1) == null;
		Send(Payload(NIM_MODIFY, hd, 1, NIF_STATE, state: 0, stateMask: 1));
		Check("nis-hidden", hidden && Icon(hd, 1) != null, "hidden while NIS_HIDDEN, shown again when cleared");
		// DELETE of an unknown icon answers FALSE.
		Check("delete-unknown-false", !Send(Payload(NIM_DELETE, hd, 77, 0)), "as Explorer");
		rec.Destroy(hd);
		int hiddenAtOnce = TrayHostService.Snapshot().Count;
		int keptForAdoption = TrayHostService.CountForTest();
		_clock += 11_000;
		TrayHostService.Snapshot();
		int afterGrace = TrayHostService.CountForTest();
		Check("dead-owner-pruned", hiddenAtOnce == 0 && keptForAdoption == 1 && afterGrace == 0, $"bar icons at once: {hiddenAtOnce}; table: {keptForAdoption} (adoptable), then {afterGrace} after the grace period");

		// --- A version 4 icon deleted and added again while Explorer held FindWindow: its NIM_ADD and NIM_SETVERSION went
		// there, and only a later NIM_MODIFY reaches us. The orphan keeps the deleted icon's callback and version.
		Fresh();
		nint v4 = rec.Create(Recorder.GenericClass);
		Send(Payload(NIM_ADD, v4, 1, NIF_MESSAGE | NIF_ICON | NIF_TIP, cb: CB, icon: 0xE000, tip: "Qt app"));
		Send(Payload(NIM_SETVERSION, v4, 1, 0, version: 4));
		Send(Payload(NIM_DELETE, v4, 1, 0));
		_clock += 20_000;
		rec.Drain(v4);
		bool om = Send(Payload(NIM_MODIFY, v4, 1, NIF_ICON, icon: 0xE001));
		TrayIconInfo? orphan = Icon(v4, 1);
		int orphanNudges = rec.Drain(v4).Count(m => m.Message == tc);
		Check("orphan-keeps-deleted-callback-and-version", !om && orphan != null && orphan.Orphan && orphan.CallbackMessage == CB && orphan.Version == 4 && orphan.Tooltip == "Qt app" && orphanNudges == 1, Describe(orphan) + $"; nudges={orphanNudges}");
		rec.Destroy(v4);

		// --- The bar's merge of a later snapshot: a callback is never traded for none, and its version goes with it.
		TrayAppIcon vm = new TrayAppIcon { CallbackMessage = CB, Version = 4, VersionKnown = true };
		TaskbarWindow.ApplyCallbackAndVersion(vm, new TrayIconInfo { CallbackMessage = 0, Version = 0, VersionKnown = true });
		bool keptBoth = vm.CallbackMessage == CB && vm.Version == 4;
		TaskbarWindow.ApplyCallbackAndVersion(vm, new TrayIconInfo { CallbackMessage = 0, Version = 3, VersionKnown = true });
		bool orphanVersion = vm.CallbackMessage == CB && vm.Version == 3;
		TaskbarWindow.ApplyCallbackAndVersion(vm, new TrayIconInfo { CallbackMessage = CB + 1, Version = 0, VersionKnown = true });
		bool reRegistered = vm.CallbackMessage == CB + 1 && vm.Version == 0;
		TrayAppIcon legacy = new TrayAppIcon();
		TaskbarWindow.ApplyCallbackAndVersion(legacy, new TrayIconInfo { CallbackMessage = CB, Version = 0, VersionKnown = false });
		Check("bar-callback-version-sticky", keptBoth && orphanVersion && reRegistered && legacy.CallbackMessage == CB && !legacy.VersionKnown, "orphan snapshot (callback 0, version 0) keeps callback + version 4; an orphan's own version is taken; a real re-registration replaces both; legacy source marks the version unknown");

		// --- Tray commands posted to our Shell_TrayWnd (Shell.Application MinimizeAll = 415) go on to Explorer's taskbar,
		// here a test window standing in for it.
		nint cmd = rec.Create(Recorder.GenericClass);
		TrayHostService.CommandTargetForTest = () => cmd;
		bool fwd = TrayHostService.ForwardTrayCommand(415, IntPtr.Zero);
		List<Recorder.Msg> cmds = rec.Drain(cmd);
		TrayHostService.CommandTargetForTest = () => IntPtr.Zero;
		bool noTarget = !TrayHostService.ForwardTrayCommand(415, IntPtr.Zero);
		TrayHostService.CommandTargetForTest = null;
		Check("tray-command-forwarded", fwd && cmds.SequenceEqual(new[] { new Recorder.Msg(0x0111, 415, 0) }) && noTarget, $"{string.Join(", ", cmds)}; without Explorer's taskbar nothing is sent");
		rec.Destroy(cmd);
	}

	// ===================================================================== recovery on click

	private static void RecoveryChecks(Recorder rec)
	{
		uint tc = TrayHostService.TaskbarCreatedMessage();
		Fresh();
		// Electron icon without a callback: the click goes out with the Electron callback (version 0) and the owner is
		// asked to register again.
		nint e = rec.Create(Recorder.ElectronClass);
		TrayReader.Delivery d = TrayReader.Deliver(e, 0u, 3u, 0u, TrayEvent.RightClick, 100, 200);
		List<Recorder.Msg> got = rec.Drain(e);
		List<Recorder.Msg> clicks = got.Where(m => m.Message == ElectronCB).ToList();
		bool exact = clicks.SequenceEqual(new[] { new Recorder.Msg(ElectronCB, 3, 0x200), new Recorder.Msg(ElectronCB, 3, 0x204), new Recorder.Msg(ElectronCB, 3, 0x205) });
		Check("recovery-electron-guess", d == TrayReader.Delivery.Guessed && exact && got.Count(m => m.Message == tc) == 1, $"{d}: {string.Join(", ", got)}");
		// The guess is checked against the real callback in the window's next NIM_ADD.
		Send(Payload(NIM_ADD, e, 3, NIF_MESSAGE, cb: ElectronCB));
		bool stillOn = !TrayHostService.IsGuessDisabled(Recorder.ElectronClass);
		TrayHostService.GuessCallback(e, out _);
		Send(Payload(NIM_ADD, e, 4, NIF_MESSAGE, cb: 0x8005));
		bool turnedOff = TrayHostService.IsGuessDisabled(Recorder.ElectronClass) && TrayHostService.GuessCallback(e, out _) == 0;
		Check("recovery-guess-self-check", stillOn && turnedOff, "confirmed by a matching NIM_ADD; turned off for the class by a mismatching one");
		rec.Destroy(e);
		// Any other framework: no guess, the owner is nudged and the click is held for the re-registered icon.
		Fresh();
		nint w = rec.Create(Recorder.GenericClass);
		TrayReader.Delivery d2 = TrayReader.Deliver(w, 0u, 1u, 0u, TrayEvent.LeftClick, 5, 5);
		List<Recorder.Msg> got2 = rec.Drain(w);
		Check("recovery-generic-nudge", d2 == TrayReader.Delivery.Recovering && got2.Count == 1 && got2[0].Message == tc, $"{d2}: {string.Join(", ", got2)}");
		TrayReader.Delivery d3 = TrayReader.Deliver(w, 0u, 1u, 0u, TrayEvent.Hover, 5, 5);
		Check("recovery-hover-ignored", d3 == TrayReader.Delivery.Dropped && rec.Drain(w).Count == 0, "a hover never nudges");
		TrayReader.Delivery d4 = TrayReader.Deliver(w, CB, 1u, 0u, TrayEvent.LeftClick, 5, 5);
		Check("recovery-normal-path", d4 == TrayReader.Delivery.Sent && rec.Drain(w).Count == 3, "an icon with a callback is sent as usual");
		rec.Destroy(w);
	}

	// ===================================================================== Shell_NotifyIconGetRect

	private static void IconRectChecks(Recorder rec)
	{
		Fresh();
		nint w = rec.Create(Recorder.GenericClass);
		Send(Payload(NIM_ADD, w, 3, NIF_MESSAGE | NIF_ICON, cb: CB, icon: 0xC000));
		TrayHostService.IconRectProvider = (owner, id, guid) => (owner == w && id == 3) ? (10, 1040, 34, 1080) : null;
		byte[] tlReq = RectPayload(1, w, 3, wide: false);
		byte[] brReq = RectPayload(2, w, 3, wide: false);
		byte[] tlWide = RectPayload(1, w, 3, wide: true);
		byte[] unkReq = RectPayload(1, w, 4, wide: false);
		nint tl = TrayHostService.HandleIconRectPayload(tlReq, tlReq.Length);
		nint br = TrayHostService.HandleIconRectPayload(brReq, brReq.Length);
		nint tlW = TrayHostService.HandleIconRectPayload(tlWide, tlWide.Length);
		nint unknown = TrayHostService.HandleIconRectPayload(unkReq, unkReq.Length);
		TrayHostService.IconRectProvider = (owner, id, guid) => null;
		nint offscreen = TrayHostService.HandleIconRectPayload(tlReq, tlReq.Length);
		TrayHostService.IconRectProvider = null;
		bool ok = (long)tl == ((1040L << 16) | 10) && (long)br == ((1080L << 16) | 34) && tlW == tl && unknown == IntPtr.Zero && offscreen == IntPtr.Zero;
		Check("getrect-answered", ok, $"top-left=0x{(long)tl:X} bottom-right=0x{(long)br:X} (64-bit handle layout 0x{(long)tlW:X}) unknown={unknown} not-on-screen={offscreen}");
		rec.Destroy(w);
	}

	// NOTIFYICONIDENTIFIER request: 40 bytes with a 32-bit hWnd, 48 with a 64-bit one (after the alignment padding).
	private static byte[] RectPayload(uint which, nint hwnd, uint uid, bool wide)
	{
		byte[] b = new byte[wide ? 48 : 40];
		Put(b, 0, 0x34753423u);
		Put(b, 4, which);
		Put(b, 8, wide ? 40 : 28);
		Put(b, 16, (uint)(long)hwnd);
		Put(b, wide ? 24 : 20, uid);
		return b;
	}

	// ===================================================================== taskbar policy

	private static void PolicyChecks()
	{
		int own = Environment.ProcessId;
		bool shell = TaskbarWindow.ClassifyTaskProcess(own, own, 4242, @"C:\Apps\Win81Layer.exe") == TaskbarWindow.TaskProcessKind.Shell;
		bool explorerByPid = TaskbarWindow.ClassifyTaskProcess(4242, own, 4242, @"C:\Windows\explorer.exe") == TaskbarWindow.TaskProcessKind.SharedHost;
		bool explorerByName = TaskbarWindow.ClassifyTaskProcess(5000, own, 4242, @"C:\Windows\explorer.exe") == TaskbarWindow.TaskProcessKind.SharedHost;
		bool frameHost = TaskbarWindow.ClassifyTaskProcess(5001, own, 4242, @"C:\Windows\System32\ApplicationFrameHost.exe") == TaskbarWindow.TaskProcessKind.SharedHost;
		bool app = TaskbarWindow.ClassifyTaskProcess(5002, own, 4242, @"C:\Program Files\App\app.exe") == TaskbarWindow.TaskProcessKind.App;
		Check("taskmenu-protects-shared-hosts", shell && explorerByPid && explorerByName && frameHost && app, "no End task / Priority / Run as administrator for the shell, explorer.exe or ApplicationFrameHost.exe");
		double t = TaskbarWindow.PinDragThreshold;
		bool jitter = !TaskbarWindow.PinDragShouldStart(4.0, 2) && !TaskbarWindow.PinDragShouldStart(0.0, 2);
		bool real = TaskbarWindow.PinDragShouldStart(t + 1.0, 2) && TaskbarWindow.PinDragShouldStart(-(t + 1.0), 2);
		bool overflow = !TaskbarWindow.PinDragShouldStart(t + 20.0, -1);
		Check("pin-drag-threshold", jitter && real && overflow && t >= 10.0, $"reorder starts after {t:F0} px of horizontal travel; 4 px wobble and vertical travel stay a click; overflow pins never drag");
	}

	// ===================================================================== no-hang guarantees

	private static void HangChecks(Recorder rec)
	{
		using HungWindow hung = new HungWindow();
		if (hung.Hwnd == IntPtr.Zero)
		{
			Check("hang-fixture", false, "hung test window not created");
			return;
		}
		// Control: the thread really does not answer (a synchronous send waits for the whole timeout).
		Stopwatch sw = Stopwatch.StartNew();
		nint r = SendMessageTimeout(hung.Hwnd, 0, IntPtr.Zero, IntPtr.Zero, 0u, 300u, out _);
		long controlMs = sw.ElapsedMilliseconds;
		Check("hang-control-thread-unresponsive", r == IntPtr.Zero && controlMs >= 250, $"a synchronous send waited {controlMs} ms (timeout 300)");
		Dictionary<string, long> times = new Dictionary<string, long>();
		times["tray-click-right"] = Time(() => TrayReader.Forward(hung.Hwnd, CB, 3u, 0u, TrayEvent.RightClick, 1, 1));
		times["tray-click-left-v4"] = Time(() => TrayReader.Forward(hung.Hwnd, CB, 3u, 4u, TrayEvent.LeftClick, 1, 1));
		times["tray-recovery"] = Time(() => TrayReader.Deliver(hung.Hwnd, 0u, 3u, 0u, TrayEvent.LeftClick, 1, 1));
		times["activate"] = Time(() => WindowList.TryActivate(hung.Hwnd));
		times["minimize"] = Time(() => WindowList.Minimize(hung.Hwnd));
		times["restore"] = Time(() => WindowList.Restore(hung.Hwnd));
		times["maximize"] = Time(() => WindowList.ToggleMaximize(hung.Hwnd));
		times["snap"] = Time(() => SnapZones.Apply(hung.Hwnd, new System.Drawing.Rectangle(-20000, -20000, 10, 10)));
		times["icon-fetch"] = Time(() => WindowList.GetIcon(hung.Hwnd, preferLarge: false));
		// Another icon of the same thread right after: not asked again (a list of N windows of one busy app waits once).
		bool flaggedAtSecondFetch = WindowList.IsHung(hung.Hwnd);
		times["icon-fetch-same-thread-again"] = Time(() => WindowList.GetIcon(hung.Hwnd, preferLarge: true));
		bool probeSaysHung = !ForegroundBroker.IsResponsive(hung.Hwnd, 50);
		// The foreground helper never attaches to a window that does not answer. Only the helper waits for its check
		// (ProbeMs); the caller is back after TimeoutMs, and the action is never run.
		int skippedRuns = 0;
		times["foreground-helper-skips-unresponsive"] = Time(() => ForegroundBroker.Run(hung.ThreadId, hung.Hwnd, () => Interlocked.Increment(ref skippedRuns), ForegroundBroker.TimeoutMs));
		bool skipEnded = ForegroundBroker.WaitForHelperForTest(hung.ThreadId, ForegroundBroker.ProbeMs + 3000);
		string skipOutcome = ForegroundBroker.LastHelperOutcome;
		// A helper that does get stuck on the app costs the caller at most the timeout; no second one is started for that
		// app, but a request whose foreground is another (answering) app still gets its helper.
		times["foreground-helper-stuck"] = Time(() => ForegroundBroker.Run(hung.ThreadId, IntPtr.Zero, () => SendMessage(hung.Hwnd, 0, IntPtr.Zero, IntPtr.Zero), ForegroundBroker.TimeoutMs));
		string stuckOutcome = ForegroundBroker.LastOutcome;
		times["foreground-helper-while-stuck"] = Time(() => ForegroundBroker.Run(hung.ThreadId, IntPtr.Zero, () => { }, ForegroundBroker.TimeoutMs));
		string whileStuckOutcome = ForegroundBroker.LastOutcome;
		nint other = rec.Create(Recorder.GenericClass);
		bool otherRan = false;
		bool otherDone = ForegroundBroker.Run(rec.ThreadId, other, () => otherRan = true, 2000);
		string otherOutcome = ForegroundBroker.LastOutcome;
		rec.Destroy(other);
		Metrics["HangMs"] = times;
		List<string> slow = times.Where(kv => kv.Value > 200).Select(kv => $"{kv.Key}={kv.Value} ms").ToList();
		Check("hang-no-call-blocks", slow.Count == 0, slow.Count == 0 ? string.Join(", ", times.Select(kv => $"{kv.Key}={kv.Value}")) + " ms" : string.Join(", ", slow));
		Check("hang-icon-fetch-once-per-thread", times["icon-fetch-same-thread-again"] < 50, $"first {times["icon-fetch"]} ms, then {times["icon-fetch-same-thread-again"]} ms for the same thread (flagged hung by then: {flaggedAtSecondFetch})");
		Check("hang-foreground-helper", probeSaysHung && skipEnded && skipOutcome.Contains("not responding") && skippedRuns == 0 && stuckOutcome.Contains("still waiting") && whileStuckOutcome.StartsWith("skipped") && otherDone && otherRan && otherOutcome == "done",
			$"probe hung={probeSaysHung}; helper: '{skipOutcome}' (action runs={skippedRuns}); '{stuckOutcome}'; same app while stuck: '{whileStuckOutcome}'; another app while stuck: '{otherOutcome}'");
		BusyForegroundChecks();
		// Later Windows itself flags the window as hung. Nothing waits on it then either, and the queued actions are still
		// made (a busy moment never throws a click away); TryActivate still reports the live window, so a click loop stops
		// at it and no second instance is launched over it.
		Thread.Sleep(5600);
		bool flagged = WindowList.IsHung(hung.Hwnd);
		bool activateAlive = WindowList.TryActivate(hung.Hwnd);
		long iconMs = Time(() => WindowList.GetIcon(hung.Hwnd, preferLarge: true));
		long queuedMs = Time(delegate
		{
			WindowList.Minimize(hung.Hwnd);
			WindowList.Restore(hung.Hwnd);
			WindowList.ToggleMaximize(hung.Hwnd);
			SnapZones.Apply(hung.Hwnd, new System.Drawing.Rectangle(-20000, -20000, 10, 10));
		});
		Check("hang-flagged-window", flagged && activateAlive && iconMs < 50 && queuedMs < 50, $"IsHungAppWindow={flagged}; TryActivate={activateAlive} (live: the loop stops here); icon fetch {iconMs} ms; minimize + restore + maximize + snap queued in {queuedMs} ms");
	}

	// The foreground helper and an app that is busy (not hung): the caller is back after TimeoutMs, and the helper still
	// hands the foreground over once the app answers, when the request is still wanted; a stale request is dropped.
	private static void BusyForegroundChecks()
	{
		using BusyWindow busy = new BusyWindow(450);
		if (busy.Hwnd == IntPtr.Zero)
		{
			Check("busy-fixture", false, "busy test window not created");
			return;
		}
		int ran = 0;
		long callerMs = Time(() => ForegroundBroker.Run(busy.ThreadId, busy.Hwnd, () => Interlocked.Increment(ref ran), ForegroundBroker.TimeoutMs));
		string callerOutcome = ForegroundBroker.LastOutcome;
		bool ended = ForegroundBroker.WaitForHelperForTest(busy.ThreadId, ForegroundBroker.ProbeMs + 3000);
		string helperOutcome = ForegroundBroker.LastHelperOutcome;
		int stale = 0;
		bool staleFinished = ForegroundBroker.Run(busy.ThreadId, busy.Hwnd, () => Interlocked.Increment(ref stale), 2000, () => false);
		string staleOutcome = ForegroundBroker.LastOutcome;
		Check("foreground-helper-busy-app", callerMs <= 200 && callerOutcome.Contains("still waiting") && ended && helperOutcome == "done" && ran == 1 && staleFinished && staleOutcome.StartsWith("no longer wanted") && stale == 0,
			$"caller {callerMs} ms ('{callerOutcome}'); helper '{helperOutcome}' after the app answered (action runs={ran}); a request no longer wanted: '{staleOutcome}' (runs={stale})");
	}

	private static long Time(Action a)
	{
		Stopwatch sw = Stopwatch.StartNew();
		try
		{
			a();
		}
		catch
		{
		}
		return sw.ElapsedMilliseconds;
	}

	private static void BreadcrumbChecks()
	{
		Breadcrumbs.StallMs = 150;
		Breadcrumbs.LastStall = null;
		using (Breadcrumbs.Enter("traytest-fast", IntPtr.Zero))
		{
		}
		Thread.Sleep(300);
		bool quiet = Breadcrumbs.LastStall == null;
		using (Breadcrumbs.Enter("traytest-slow", IntPtr.Zero))
		{
			Thread.Sleep(450);
		}
		string? report = Breadcrumbs.LastStall;
		Breadcrumbs.StallMs = 3000;
		Check("breadcrumb-names-a-stall", quiet && report != null && report.Contains("traytest-slow"), report ?? "no report");
	}

	// ===================================================================== helpers

	private static string Names(IEnumerable<uint> evs)
	{
		return string.Join(",", evs.Select(TrayProtocol.EventName));
	}

	private static bool OwnShellTrayWnd()
	{
		nint h = IntPtr.Zero;
		while ((h = FindWindowEx(IntPtr.Zero, h, "Shell_TrayWnd", null)) != IntPtr.Zero)
		{
			GetWindowThreadProcessId(h, out uint pid);
			if (pid == (uint)Environment.ProcessId)
			{
				return true;
			}
		}
		return false;
	}

	private static string OutsideStamp()
	{
		StringBuilder sb = new StringBuilder();
		if (Directory.Exists(DataRoot))
		{
			foreach (string f in Directory.GetFileSystemEntries(DataRoot).Select(p => Path.GetFileName(p) ?? "").Where(n => !n.StartsWith("log", StringComparison.OrdinalIgnoreCase) && !n.Equals("heartbeat.dat", StringComparison.OrdinalIgnoreCase)).OrderBy(n => n, StringComparer.OrdinalIgnoreCase))
			{
				sb.Append(f).Append(';');
			}
		}
		string settings = Path.Combine(DataRoot, "settings.json");
		try
		{
			sb.Append('|').Append(File.Exists(settings) ? Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(settings)))[..16] : "-");
		}
		catch (Exception ex)
		{
			sb.Append("|unreadable:").Append(ex.GetType().Name);
		}
		return sb.ToString();
	}

	// Message-only windows (never visible, not enumerable, cannot be activated) on their own pumping thread; records
	// every message >= WM_USER they receive (tray callbacks, TaskbarCreated) and WM_COMMAND (forwarded tray commands).
	private sealed class Recorder : IDisposable
	{
		internal readonly struct Msg : IEquatable<Msg>
		{
			public readonly uint Message;

			public readonly nint W;

			public readonly nint L;

			public Msg(uint message, nint w, nint l)
			{
				Message = message;
				W = w;
				L = l;
			}

			public bool Equals(Msg o)
			{
				return Message == o.Message && W == o.W && L == o.L;
			}

			public override bool Equals(object? obj)
			{
				return obj is Msg m && Equals(m);
			}

			public override int GetHashCode()
			{
				return HashCode.Combine(Message, W, L);
			}

			public override string ToString()
			{
				return $"(0x{Message:X} w=0x{(long)W:X} l=0x{(long)L:X} {TrayProtocol.EventName((uint)((long)L & 0xFFFF))})";
			}
		}

		internal const string GenericClass = "Win81TrayTestWindow";

		internal const string ElectronClass = "Electron_NotifyIconHostWindow";

		internal const string ChromeClass = "Chrome_StatusTrayWindow";

		private const uint Sentinel = 0x8000u + 0x3F0u;

		private const uint ReqCreate = 0x8000u + 0x3F1u;

		private const uint ReqDestroy = 0x8000u + 0x3F2u;

		private const uint ReqQuit = 0x8000u + 0x3F3u;

		private readonly Thread _thread;

		private readonly ManualResetEvent _started = new ManualResetEvent(initialState: false);

		private readonly AutoResetEvent _done = new AutoResetEvent(initialState: false);

		private readonly object _gate = new object();

		private readonly Dictionary<nint, List<Msg>> _log = new Dictionary<nint, List<Msg>>();

		private readonly HashSet<nint> _mine = new HashSet<nint>();

		private WndProcDelegate? _proc;

		private uint _tid;

		private string _createClass = GenericClass;

		private nint _created;

		public bool Ready { get; private set; }

		public uint ThreadId => _tid;

		public Recorder()
		{
			_thread = new Thread(Main)
			{
				IsBackground = true,
				Name = "TrayTestRecorder"
			};
			_thread.SetApartmentState(ApartmentState.MTA);
			_thread.Start();
			Ready = _started.WaitOne(5000);
		}

		private void Main()
		{
			_tid = GetCurrentThreadId();
			_proc = Proc;
			nint hInst = GetModuleHandleW(null);
			foreach (string c in new string[3] { GenericClass, ElectronClass, ChromeClass })
			{
				WNDCLASS wc = new WNDCLASS
				{
					lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_proc),
					hInstance = hInst,
					lpszClassName = c
				};
				RegisterClassW(ref wc);
			}
			PeekMessageW(out _, IntPtr.Zero, 0, 0, 0);   // create the message queue before anyone posts to it
			_started.Set();
			while (GetMessageW(out MSG m, IntPtr.Zero, 0, 0) > 0)
			{
				if (m.hwnd == IntPtr.Zero)
				{
					if (m.message == ReqCreate)
					{
						_created = CreateWindowExW(0, _createClass, "", 0, 0, 0, 0, 0, HWND_MESSAGE, IntPtr.Zero, hInst, IntPtr.Zero);
						_done.Set();
						continue;
					}
					if (m.message == ReqDestroy)
					{
						DestroyWindow(m.wParam);
						_done.Set();
						continue;
					}
					if (m.message == ReqQuit)
					{
						break;
					}
				}
				TranslateMessage(ref m);
				DispatchMessageW(ref m);
			}
			lock (_gate)
			{
				foreach (nint h in _mine)
				{
					DestroyWindow(h);
				}
			}
		}

		private nint Proc(nint h, uint msg, nint w, nint l)
		{
			if (msg == Sentinel)
			{
				_done.Set();
				return IntPtr.Zero;
			}
			if (msg >= 0x0400u || msg == 0x0111u)
			{
				lock (_gate)
				{
					if (!_log.TryGetValue(h, out List<Msg>? list))
					{
						list = new List<Msg>();
						_log[h] = list;
					}
					list.Add(new Msg(msg, w, l));
				}
				return IntPtr.Zero;
			}
			return DefWindowProcW(h, msg, w, l);
		}

		public nint Create(string cls)
		{
			_createClass = cls;
			PostThreadMessageW(_tid, ReqCreate, IntPtr.Zero, IntPtr.Zero);
			_done.WaitOne(5000);
			nint h = _created;
			lock (_gate)
			{
				_mine.Add(h);
			}
			return h;
		}

		public void Destroy(nint h)
		{
			PostThreadMessageW(_tid, ReqDestroy, h, IntPtr.Zero);
			_done.WaitOne(5000);
			lock (_gate)
			{
				_mine.Remove(h);
				_log.Remove(h);
			}
		}

		// Everything delivered so far: a posted sentinel is handled only after every earlier sent-notify and posted
		// message to the window.
		public List<Msg> Drain(nint h)
		{
			PostMessageW(h, Sentinel, IntPtr.Zero, IntPtr.Zero);
			_done.WaitOne(5000);
			lock (_gate)
			{
				if (!_log.TryGetValue(h, out List<Msg>? list))
				{
					return new List<Msg>();
				}
				List<Msg> copy = new List<Msg>(list);
				list.Clear();
				return copy;
			}
		}

		public void Dispose()
		{
			PostThreadMessageW(_tid, ReqQuit, IntPtr.Zero, IntPtr.Zero);
			_thread.Join(3000);
		}
	}

	// A message-only window whose thread stops answering right after creating it (it waits without pumping) and, when
	// released, destroys the window and exits without ever pumping: nothing queued for it ever runs.
	private sealed class HungWindow : IDisposable
	{
		private readonly ManualResetEvent _release = new ManualResetEvent(initialState: false);

		private readonly ManualResetEvent _created = new ManualResetEvent(initialState: false);

		private readonly Thread _thread;

		private WndProcDelegate? _proc;

		public nint Hwnd { get; private set; }

		public uint ThreadId { get; private set; }

		public HungWindow()
		{
			_thread = new Thread(Main)
			{
				IsBackground = true,
				Name = "TrayTestHung"
			};
			_thread.SetApartmentState(ApartmentState.MTA);
			_thread.Start();
			_created.WaitOne(5000);
		}

		private void Main()
		{
			ThreadId = GetCurrentThreadId();
			_proc = (h, m, w, l) => DefWindowProcW(h, m, w, l);
			nint hInst = GetModuleHandleW(null);
			WNDCLASS wc = new WNDCLASS
			{
				lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_proc),
				hInstance = hInst,
				lpszClassName = "Win81TrayTestHung"
			};
			RegisterClassW(ref wc);
			Hwnd = CreateWindowExW(0, "Win81TrayTestHung", "", 0, 0, 0, 0, 0, HWND_MESSAGE, IntPtr.Zero, hInst, IntPtr.Zero);
			_created.Set();
			_release.WaitOne();
			if (Hwnd != IntPtr.Zero)
			{
				DestroyWindow(Hwnd);
			}
		}

		public void Dispose()
		{
			_release.Set();
			_thread.Join(5000);
		}
	}

	// A message-only window whose thread does not pump for busyMs right after creating it (an app in the middle of a
	// long piece of work), then pumps normally until disposed.
	private sealed class BusyWindow : IDisposable
	{
		private const uint ReqQuit = 0x8000u + 0x3F4u;

		private readonly ManualResetEvent _created = new ManualResetEvent(initialState: false);

		private readonly Thread _thread;

		private readonly int _busyMs;

		private WndProcDelegate? _proc;

		public nint Hwnd { get; private set; }

		public uint ThreadId { get; private set; }

		public BusyWindow(int busyMs)
		{
			_busyMs = busyMs;
			_thread = new Thread(Main)
			{
				IsBackground = true,
				Name = "TrayTestBusy"
			};
			_thread.SetApartmentState(ApartmentState.MTA);
			_thread.Start();
			_created.WaitOne(5000);
		}

		private void Main()
		{
			ThreadId = GetCurrentThreadId();
			_proc = (h, m, w, l) => DefWindowProcW(h, m, w, l);
			nint hInst = GetModuleHandleW(null);
			WNDCLASS wc = new WNDCLASS
			{
				lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_proc),
				hInstance = hInst,
				lpszClassName = "Win81TrayTestBusy"
			};
			RegisterClassW(ref wc);
			Hwnd = CreateWindowExW(0, "Win81TrayTestBusy", "", 0, 0, 0, 0, 0, HWND_MESSAGE, IntPtr.Zero, hInst, IntPtr.Zero);
			_created.Set();
			Thread.Sleep(_busyMs);
			while (GetMessageW(out MSG m, IntPtr.Zero, 0, 0) > 0)
			{
				if (m.hwnd == IntPtr.Zero && m.message == ReqQuit)
				{
					break;
				}
				TranslateMessage(ref m);
				DispatchMessageW(ref m);
			}
			if (Hwnd != IntPtr.Zero)
			{
				DestroyWindow(Hwnd);
			}
		}

		public void Dispose()
		{
			PostThreadMessageW(ThreadId, ReqQuit, IntPtr.Zero, IntPtr.Zero);
			_thread.Join(3000);
		}
	}

	// ---- interop ----

	private delegate nint WndProcDelegate(nint hWnd, uint msg, nint wParam, nint lParam);

	private static readonly nint HWND_MESSAGE = new nint(-3);

	[StructLayout(LayoutKind.Sequential)]
	private struct WNDCLASS
	{
		public uint style;
		public nint lpfnWndProc;
		public int cbClsExtra;
		public int cbWndExtra;
		public nint hInstance;
		public nint hIcon;
		public nint hCursor;
		public nint hbrBackground;
		[MarshalAs(UnmanagedType.LPWStr)] public string? lpszMenuName;
		[MarshalAs(UnmanagedType.LPWStr)] public string? lpszClassName;
	}

	[StructLayout(LayoutKind.Sequential)]
	private struct MSG
	{
		public nint hwnd;
		public uint message;
		public nint wParam;
		public nint lParam;
		public uint time;
		public int ptX;
		public int ptY;
	}

	[DllImport("user32.dll", SetLastError = true)] private static extern ushort RegisterClassW(ref WNDCLASS c);
	[DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
	private static extern nint CreateWindowExW(uint exStyle, string cls, string name, uint style, int x, int y, int w, int h, nint parent, nint menu, nint inst, nint prm);
	[DllImport("user32.dll")] private static extern nint DefWindowProcW(nint h, uint m, nint w, nint l);
	[DllImport("user32.dll")] private static extern bool DestroyWindow(nint h);
	[DllImport("user32.dll")] private static extern int GetMessageW(out MSG msg, nint h, uint min, uint max);
	[DllImport("user32.dll")] private static extern bool PeekMessageW(out MSG msg, nint h, uint min, uint max, uint remove);
	[DllImport("user32.dll")] private static extern bool TranslateMessage(ref MSG msg);
	[DllImport("user32.dll")] private static extern nint DispatchMessageW(ref MSG msg);
	[DllImport("user32.dll")] private static extern bool PostMessageW(nint h, uint m, nint w, nint l);
	[DllImport("user32.dll")] private static extern bool PostThreadMessageW(uint tid, uint m, nint w, nint l);
	[DllImport("user32.dll")] private static extern nint SendMessage(nint h, uint m, nint w, nint l);
	[DllImport("user32.dll")] private static extern nint SendMessageTimeout(nint h, uint m, nint w, nint l, uint flags, uint timeout, out nint result);
	[DllImport("user32.dll")] private static extern nint GetForegroundWindow();
	[DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern nint FindWindowEx(nint parent, nint after, string? cls, string? title);
	[DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint h, out uint pid);
	[DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
	[DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern nint GetModuleHandleW(string? n);
}
