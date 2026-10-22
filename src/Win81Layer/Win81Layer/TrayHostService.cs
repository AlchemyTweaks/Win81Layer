using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace Win81Layer;

// Windows 11 real tray host.
//
// On Win11 the classic Shell_TrayWnd -> SysPager -> ToolbarWindow32 chain is gone (verified live on 26200), so the
// launcher had no way to read tray icons WITH their owner window + callback message, and could only render a
// non-functional registry snapshot (Win11TrayReader). To make the icons REAL — left/right-click showing each app's
// OWN menu — the launcher becomes the actual shell tray host, exactly like RetroBar/ManagedShell:
//
//   1. Create our own top-level window of class "Shell_TrayWnd", kept topmost so FindWindow("Shell_TrayWnd") returns
//      it (proven to win over explorer's on this build).
//   2. Broadcast "TaskbarCreated" so every app re-registers its notification icon.
//   3. Each Shell_NotifyIcon(NIM_ADD/MODIFY/DELETE/SETVERSION) arrives as WM_COPYDATA (dwData==1) carrying the shell
//      wire NOTIFYICONDATA — real hWnd, uID, uCallbackMessage, hIcon, tooltip. We keep that table.
//   4. TaskbarWindow renders the table and forwards clicks via TrayReader.Forward, so the owning app shows its own
//      real context menu / toggles its own window.
//
// Identity follows Explorer: an icon is its guidItem when NIF_GUID is set, otherwise its (hWnd, uID). An entry whose
// window was destroyed is adopted by the same process and id from its new window (Task Manager re-creates its window).
//
// An icon can still reach us without its NIM_ADD: shell32 sends to whichever Shell_TrayWnd FindWindow returns, and
// Explorer's hidden one can be above ours for a moment. Electron (Discord, Slack, ...) sets NIF_MESSAGE only on NIM_ADD,
// so such an icon would have no callback for the whole session. Every such miss now repairs itself: the entry is kept
// as an orphan (so the icon still shows), NIM_MODIFY answers FALSE as Explorer does, and the owner window gets a
// targeted, throttled TaskbarCreated, which every common framework answers by adding its icons again.
//
// On shutdown we destroy the window and broadcast TaskbarCreated again so explorer reclaims the tray (reversible).
// Runs on its own background thread with a Win32 message loop so the high-frequency tooltip MODIFY traffic never
// touches the UI thread; consumers get a single coalesced Changed event.
public static class TrayHostService
{
	private sealed class Entry
	{
		public uint HWnd;
		public uint UID;
		public Guid Guid;      // Guid.Empty unless the icon was registered with NIF_GUID
		public uint Pid;
		public uint Callback;
		public uint Version;   // NOTIFYICON version (from NIM_SETVERSION); 0 = legacy. Decides the click-forward format.
		public nint HIcon;
		public string Tip = "";
		public bool Hidden;    // NIS_HIDDEN (NIF_STATE): the app asked for its icon to be hidden
		public bool Orphan;    // made by a NIM_MODIFY / NIM_SETVERSION whose NIM_ADD never reached us
		public long Created;   // creation order: the bar keeps icons in the order they first appeared
		public long CreatedMs; // creation time (phantom rule: registered together vs. registered as a replacement)
		public long Seq;       // bumped on every message that touches this entry (freshest wins on collapse)
		public long LastTouchMs;
		public string Key = "";   // the _icons dictionary key
		public bool Dead;      // its window is gone: hidden from the bar, kept for DeadGraceMs so a new window can adopt it
		public long DeadSinceMs;
	}

	// A phantom folded into a newer icon (CollapsePhantomsLocked): the entry as it was, and the key of the survivor.
	private sealed class Fold
	{
		public string Survivor = "";
		public Entry Old;
	}

	// What a deleted icon leaves behind for a while (RestoreTombstoneLocked).
	private readonly struct Tomb
	{
		public readonly string Tip;
		public readonly uint Callback;
		public readonly uint Version;
		public readonly long At;

		public Tomb(string tip, uint callback, uint version, long at)
		{
			Tip = tip;
			Callback = callback;
			Version = version;
			At = at;
		}
	}

	public enum NudgeReason
	{
		Click,
		ModifyMiss,
		SetVersionMiss
	}

	private static long _seq;

	private static int _broadcastRound;

	private const int WM_COPYDATA = 0x004A;
	private const int WM_COMMAND = 0x0111;
	private const int WM_CLOSE = 0x0010;
	private const int WM_DESTROY = 0x0002;
	private const int WM_TIMER = 0x0113;
	private const uint WM_APP_NUDGE = 0x8000u + 0x2A1u;   // posted to the host window: wParam = owner window, lParam = reason
	private const uint REASSERT_TIMER = 1u;
	private const uint BROADCAST_TIMER = 2u;
	private const uint COLLAPSE_TIMER = 3u;
	private const int OFFSCREEN = -32000;
	private const uint WS_POPUP = 0x80000000u;
	private const uint WS_CHILD = 0x40000000u;
	private const uint WS_EX_TOPMOST = 0x00000008u;
	private const uint WS_EX_TOOLWINDOW = 0x00000080u;
	private const int SW_HIDE = 0;
	private const uint SWP_NOMOVE = 0x0002u, SWP_NOSIZE = 0x0001u, SWP_NOACTIVATE = 0x0010u;

	private const uint NIM_ADD = 0, NIM_MODIFY = 1, NIM_DELETE = 2, NIM_SETFOCUS = 3, NIM_SETVERSION = 4;

	// NOTIFYICONDATA uFlags — which fields in a MODIFY are actually present (the rest must not clobber our table).
	private const uint NIF_MESSAGE = 0x01u;
	private const uint NIF_ICON = 0x02u;
	private const uint NIF_TIP = 0x04u;
	private const uint NIF_STATE = 0x08u;
	private const uint NIF_GUID = 0x20u;   // guidItem is only valid when this flag is set; otherwise it is uninitialized
	private const uint NIS_HIDDEN = 0x01u;

	private const int HSHELL_WINDOWCREATED = 1;
	private const int HSHELL_WINDOWACTIVATED = 4;

	// Phantom rule (see IsPhantomPair): a new id counts as a replacement when it appeared at least PhantomGapMs after
	// the old id went quiet; an old window's icon is dropped once quiet for PhantomIdleMs.
	private const long PhantomGapMs = 500;
	private const long PhantomIdleMs = 3000;

	// A deleted icon's tooltip is put back when the same icon is re-added without NIF_TIP within this time (Electron's
	// re-registration after TaskbarCreated sends NIF_MESSAGE | NIF_ICON only).
	private const long TombstoneMs = 3000;

	// A deleted icon's callback, version and tooltip are put back on an orphan of the same icon made within this time:
	// the icon was deleted and added again, but the NIM_ADD / NIM_SETVERSION went to Explorer and only a later
	// NIM_MODIFY reached us.
	private const long OrphanTombstoneMs = 60000;

	// An entry whose window is gone stays (hidden) this long, so a re-created window that goes on with NIM_MODIFY only
	// is still adopted.
	private const long DeadGraceMs = 10000;

	// Throttle for targeted TaskbarCreated nudges, per owner window.
	private const long NudgeMinGapMs = 10000;
	private const long NudgeWindowMs = 300000;
	private const int NudgeMaxPerWindow = 3;

	// Electron (notify_icon_host.cc: kNotifyIconMessage = WM_APP + 1) and Chromium (status_tray_win.cc:
	// kStatusIconMessage = WM_APP + 1) use one fixed callback for all their icons, on one host window of a fixed class.
	internal const uint ChromiumFamilyCallback = 0x8001u;
	private static readonly string[] ChromiumFamilyClasses = new string[2] { "Electron_NotifyIconHostWindow", "Chrome_StatusTrayWindow" };

	private static readonly nint HWND_TOPMOST = new nint(-1);
	private static readonly nint HWND_BROADCAST = new nint(0xFFFF);

	private static readonly object _gate = new object();
	private static readonly Dictionary<string, Entry> _icons = new Dictionary<string, Entry>();
	// Window key of a folded phantom -> the folded entry and its survivor. Its NIM_DELETE just forgets it; any other
	// message proves the icon is live after all, and it is restored (UnfoldLocked) instead of touching the survivor.
	private static readonly Dictionary<string, Fold> _folds = new Dictionary<string, Fold>();
	// Pairs of icons proven live together (one spoke after it was folded into the other): never folded again.
	private static readonly HashSet<string> _keptApart = new HashSet<string>();
	private static readonly Dictionary<string, Tomb> _tombs = new Dictionary<string, Tomb>();
	private static readonly Dictionary<nint, List<long>> _nudges = new Dictionary<nint, List<long>>();
	private static readonly Dictionary<nint, string> _guessUsed = new Dictionary<nint, string>();
	private static readonly HashSet<string> _guessOff = new HashSet<string>();
	private static WndProc _proc;            // GC-root the delegate for the window's lifetime
	private static WndProc _childProc;
	private static Thread _thread;
	private static uint _hostThreadId;
	private static nint _hwndTray;
	private static uint _wmTaskbarCreated;
	private static uint _wmShellHook;
	private static volatile bool _active;
	private static Timer _coalesce;

	// Retargets so far (under _gate): a MODIFY that moved or revived an entry is a change for the bar.
	private static int _retargets;

	// The UI dispatcher while the host runs: Changed is then raised on the UI thread after a 150 ms debounce.
	private static System.Windows.Threading.Dispatcher _uiDispatcher;

	private static System.Windows.Threading.DispatcherTimer _uiCoalesce;
	private static bool _collapseArmed;
	private static bool _inDeferredCollapse;
	private static int _overtakes;
	private static long _lastOvertakeLog = long.MinValue;

	// Raised (coalesced, ~150ms) whenever the icon table changes. While the host runs it fires on the UI thread; without
	// a UI dispatcher (tests) on a threadpool thread. Consumers that are not on the UI thread marshal.
	public static event Action Changed;

	public static bool Active => _active;

	// The bar answers Shell_NotifyIconGetRect: (owner window, id, guid) -> the icon's rectangle in physical screen
	// pixels, or null when the icon is not on screen. Called on the tray-host thread.
	public static Func<nint, uint, Guid, (int Left, int Top, int Right, int Bottom)?> IconRectProvider;

	// --traytest seams. Its test windows live in this process; its clock is fake.
	internal static bool TestAllowOwnProcess;
	internal static Func<long> ClockForTest;
	internal static int NudgeCount;
	internal static int SignalCount;
	internal static string LastNudge = "";

	private static long Now => ClockForTest?.Invoke() ?? Environment.TickCount64;

	public static void Start()
	{
		PerfProbe.Refuse("TrayHostService.Start");
		// Diagnostic kill-switch: WIN81_NO_TRAYHOST=1 disables the tray host takeover (used to isolate its effect on
		// taskbar layout). Normal runs leave it unset.
		if (Environment.GetEnvironmentVariable("WIN81_NO_TRAYHOST") == "1")
		{
			Logger.Log("TrayHost: disabled via WIN81_NO_TRAYHOST");
			return;
		}
		// Only needed on Win11, where the classic ToolbarWindow32 tray source no longer exists.
		if (!Win11TrayReader.IsWin11 || _active || _thread != null)
		{
			return;
		}
		_uiDispatcher = System.Windows.Application.Current?.Dispatcher;
		_thread = new Thread(ThreadMain)
		{
			IsBackground = true,
			Name = "TrayHost"
		};
		_thread.Start();
	}

	public static void Stop()
	{
		try
		{
			nint h = _hwndTray;
			if (h != IntPtr.Zero)
			{
				PostMessage(h, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
			}
		}
		catch
		{
		}
	}

	// Snapshot for the taskbar: real owner window + callback message => TrayReader.Forward shows the app's own menu.
	// Dead-owner entries are left out here (and dropped after a grace period); shell-owned entries are dropped by the
	// consumer (ApplyTrayInfos).
	public static List<TrayIconInfo> Snapshot()
	{
		List<TrayIconInfo> list = new List<TrayIconInfo>();
		lock (_gate)
		{
			PruneDeadLocked(Now);
			List<Entry> entries = new List<Entry>(_icons.Count);
			foreach (Entry e in _icons.Values)
			{
				if (!e.Dead)
				{
					entries.Add(e);
				}
			}
			entries.Sort((a, b) => a.Created.CompareTo(b.Created));
			// One icon per (window, id) when one of them is a guid icon addressed both ways: merge so the freshest
			// non-zero callback, the highest version and the freshest icon and tooltip win. (Find already routes both
			// ways to one entry; this only guards a guid icon re-targeted onto a window that also had a plain entry.)
			Dictionary<(uint, uint), Entry> guidByWindow = new Dictionary<(uint, uint), Entry>();
			foreach (Entry e in entries)
			{
				if (e.Guid != Guid.Empty)
				{
					guidByWindow.TryAdd((e.HWnd, e.UID), e);
				}
			}
			foreach (Entry e in entries)
			{
				if (e.Guid == Guid.Empty && guidByWindow.TryGetValue((e.HWnd, e.UID), out Entry g))
				{
					if (e.Callback != 0 && (g.Callback == 0 || e.Seq > g.Seq))
					{
						g.Callback = e.Callback;
					}
					g.Version = Math.Max(g.Version, e.Version);
					if (e.Seq > g.Seq)
					{
						if (e.HIcon != IntPtr.Zero)
						{
							g.HIcon = e.HIcon;
						}
						if (!string.IsNullOrEmpty(e.Tip))
						{
							g.Tip = e.Tip;
						}
					}
					continue;
				}
				if (e.Hidden)
				{
					continue;
				}
				list.Add(new TrayIconInfo
				{
					OwnerHwnd = WireHandle(e.HWnd),
					Id = e.UID,
					Guid = e.Guid,
					CallbackMessage = e.Callback,
					Version = e.Version,
					VersionKnown = true,      // every NIM_SETVERSION that reached the tray reached this table
					HIcon = e.HIcon,          // real (cross-process) HICON; UI-thread IconToSource converts + falls back
					Image = null,
					Tooltip = e.Tip ?? "",
					Hidden = false,
					Orphan = e.Orphan,
					FromOverflow = false,
					ExecutablePath = null,    // real forwarding path — no registry activate/launch fallback
					StableId = null
				});
			}
		}
		return list;
	}

	private static void ThreadMain()
	{
		try
		{
			_proc = TrayWndProc;
			_childProc = DefProc;
			nint hInst = GetModuleHandleW(null);
			_hostThreadId = GetCurrentThreadId();

			WNDCLASS wc = new WNDCLASS
			{
				lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_proc),
				hInstance = hInst,
				lpszClassName = "Shell_TrayWnd"
			};
			RegisterClassW(ref wc);   // ERROR_CLASS_ALREADY_EXISTS is harmless (per-process, first call wins)

			// Shown (never SW_HIDE) but zero-size and off-screen: invisible to the user, yet it stays in the visible
			// TOPMOST z-band so FindWindow("Shell_TrayWnd") keeps returning it. A hidden window loses that priority and
			// explorer reclaims the tray within ~1s (verified). WS_EX_TOOLWINDOW keeps it out of Alt+Tab.
			_hwndTray = CreateWindowExW(WS_EX_TOPMOST | WS_EX_TOOLWINDOW, "Shell_TrayWnd", "", WS_POPUP,
				OFFSCREEN, OFFSCREEN, 0, 0, IntPtr.Zero, IntPtr.Zero, hInst, IntPtr.Zero);
			if (_hwndTray == IntPtr.Zero)
			{
				Logger.Log("TrayHost: CreateWindow(Shell_TrayWnd) failed err=" + Marshal.GetLastWin32Error());
				return;
			}

			WNDCLASS nc = new WNDCLASS
			{
				lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_childProc),
				hInstance = hInst,
				lpszClassName = "TrayNotifyWnd"
			};
			RegisterClassW(ref nc);
			CreateWindowExW(0u, "TrayNotifyWnd", "", WS_CHILD, 0, 0, 0, 0, _hwndTray, IntPtr.Zero, hInst, IntPtr.Zero);

			SetWindowPos(_hwndTray, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
			// Shell hook: every foreground change re-checks that FindWindow still returns this window (Explorer re-asserts
			// its own taskbar on fullscreen, Start and display changes), so an app registering right then reaches us.
			_wmShellHook = RegisterWindowMessageW("SHELLHOOK");
			RegisterShellHookWindow(_hwndTray);
			// Re-assert topmost every second so we keep winning FindWindow
			// as explorer re-asserts its own taskbar and as apps register icons later; this makes the takeover durable.
			SetTimer(_hwndTray, REASSERT_TIMER, 1000u, IntPtr.Zero);

			_wmTaskbarCreated = RegisterWindowMessageW("TaskbarCreated");
			nint found = FindWindowW("Shell_TrayWnd", null);
			_active = true;
			// Existing Chromium windows cache auto-hide edges. Refresh their non-client layout
			// once the corrected Shell32 endpoint is ready, even if rcWork did not change.
			TaskbarWorkArea.NotifyWorkAreaChanged();
			Logger.Log($"TrayHost: Shell_TrayWnd=0x{_hwndTray.ToInt64():X} findWins={(found == _hwndTray)}; will broadcast TaskbarCreated shortly");
			// Delay the reclaim broadcast: the launcher's OWN taskbar also listens for TaskbarCreated (to re-hide the
			// native bar / re-place itself), and firing it during the taskbar's initial layout can race and collapse the
			// bar. Wait until the taskbar has settled, then broadcast so apps re-register their tray icons to us.
			_broadcastRound = 0; SetTimer(_hwndTray, BROADCAST_TIMER, 3000u, IntPtr.Zero);

			while (GetMessageW(out MSG msg, IntPtr.Zero, 0u, 0u) > 0)
			{
				TranslateMessage(ref msg);
				DispatchMessageW(ref msg);
			}
		}
		catch (Exception ex)
		{
			Logger.Log("TrayHost thread failed: " + ex.Message);
		}
		finally
		{
			_active = false;
			try
			{
				if (_hwndTray != IntPtr.Zero)
				{
					DestroyWindow(_hwndTray);
				}
			}
			catch
			{
			}
			_hwndTray = IntPtr.Zero;
			// hand the tray back: apps re-register to explorer's real Shell_TrayWnd
			try
			{
				if (_wmTaskbarCreated != 0)
				{
					SendNotifyMessageW(HWND_BROADCAST, _wmTaskbarCreated, IntPtr.Zero, IntPtr.Zero);
				}
			}
			catch
			{
			}
			lock (_gate)
			{
				_icons.Clear();
				_folds.Clear();
				_keptApart.Clear();
				_tombs.Clear();
			}
			_thread = null;
			Logger.Log("TrayHost: stopped; tray handed back to explorer (TaskbarCreated broadcast)");
		}
	}

	private static nint DefProc(nint h, uint m, nint w, nint l)
	{
		return DefWindowProcW(h, m, w, l);
	}

	private static nint TrayWndProc(nint hWnd, uint msg, nint wParam, nint lParam)
	{
		switch (msg)
		{
			case WM_COPYDATA:
				try
				{
					if (lParam == IntPtr.Zero) return IntPtr.Zero;
					COPYDATASTRUCT data = Marshal.PtrToStructure<COPYDATASTRUCT>(lParam);
					if (data.dwData == IntPtr.Zero)
					{
						// Shell32 also sends AppBar requests to Shell_TrayWnd. A generic TRUE here
						// becomes HWND(1) for GETAUTOHIDEBAR(EX), making Chromium reserve phantom
						// 2px edge strips. Let Explorer handle its protocol, including shared memory.
						return ForwardAppBarRequest(wParam, lParam, data);
					}
					if (data.dwData == new IntPtr(1))
					{
						byte[] payload = CopyPayload(data);
						return (payload != null && HandleTrayPayload(payload, payload.Length)) ? new nint(1) : IntPtr.Zero;
					}
					if (data.dwData == new IntPtr(3))
					{
						// Shell_NotifyIconGetRect: answered from the bar's layout.
						byte[] payload = CopyPayload(data);
						return (payload != null) ? HandleIconRectPayload(payload, payload.Length) : IntPtr.Zero;
					}
				}
				catch (Exception ex)
				{
					Logger.Log("TrayHost copydata error: " + ex.Message);
				}
				return IntPtr.Zero;
			case WM_TIMER:
				if ((uint)wParam == BROADCAST_TIMER)
				{
					_broadcastRound++;
						// A SINGLE broadcast misses apps that start slightly later, ignore the first TaskbarCreated, or lose the
						// FindWindow/z-order race at that instant — they then never register their tray icon for the whole session
						// (= "some apps don't show"). Re-broadcast a few more times at widening intervals, then stop. Rounds after
						// the first land well after the taskbar has settled, avoiding the initial-layout race the 3s delay guards.
						if (_broadcastRound >= 3) { KillTimer(_hwndTray, BROADCAST_TIMER); }
						else { SetTimer(_hwndTray, BROADCAST_TIMER, (_broadcastRound == 1) ? 4000u : 7000u, IntPtr.Zero); }
					Logger.Log($"TrayHost: broadcasting TaskbarCreated (round {_broadcastRound}) to reclaim tray icons");
					SendNotifyMessageW(HWND_BROADCAST, _wmTaskbarCreated, IntPtr.Zero, IntPtr.Zero);
				}
				else if ((uint)wParam == COLLAPSE_TIMER)
				{
					KillTimer(_hwndTray, COLLAPSE_TIMER);
					RunDeferredCollapse();
				}
				else
				{
					// Only re-raise when explorer's bar has overtaken ours in FindWindow order.
					EnsureTop("timer");
					SweepDeadOwners();
				}
				return IntPtr.Zero;
			case WM_APP_NUDGE:
				NudgeNow(wParam, (NudgeReason)(int)lParam);
				return IntPtr.Zero;
			case WM_COMMAND:
				ForwardTrayCommand(wParam, lParam);
				return IntPtr.Zero;
			case WM_CLOSE:
				DestroyWindow(hWnd);
				return IntPtr.Zero;
			case WM_DESTROY:
				PostQuitMessage(0);
				return IntPtr.Zero;
		}
		if (msg == _wmShellHook && _wmShellHook != 0)
		{
			int code = (int)((long)wParam & 0x7FFF);
			if (code == HSHELL_WINDOWACTIVATED || code == HSHELL_WINDOWCREATED)
			{
				EnsureTop("activate");
			}
			return IntPtr.Zero;
		}
		return DefWindowProcW(hWnd, msg, wParam, lParam);
	}

	// Explorer's taskbar comes back above ours whenever it re-asserts its z-order (fullscreen and rude-app changes,
	// display changes, Start). Any Shell_NotifyIcon call made in that gap goes to Explorer instead of us; count it so
	// the frequency is visible, and re-raise.
	private static void EnsureTop(string via)
	{
		nint h = _hwndTray;
		if (h == IntPtr.Zero)
		{
			return;
		}
		nint found = FindWindowW("Shell_TrayWnd", null);
		if (found == h)
		{
			return;
		}
		_overtakes++;
		SetWindowPos(h, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
		long now = Environment.TickCount64;
		if (_lastOvertakeLog == long.MinValue || now - _lastOvertakeLog > 60000)
		{
			_lastOvertakeLog = now;
			Logger.Log($"[trayhost] Shell_TrayWnd overtaken by 0x{(long)found:X} (seen via {via}); re-raised; overtakes so far={_overtakes}");
		}
	}

	// Tray commands: shell32 posts WM_COMMAND with a tray command id to FindWindow("Shell_TrayWnd"), which is this
	// window while we host the tray. Shell.Application's MinimizeAll / UndoMinimizeALL / ToggleDesktop /
	// CascadeWindows / TileHorizontally / TileVertically / FileRun / ShutdownWindows all work that way (the launcher's
	// own Show desktop and taskbar menu use them too). They are Explorer's taskbar commands: pass them on, posted.
	internal static Func<nint> CommandTargetForTest;

	internal static bool ForwardTrayCommand(nint wParam, nint lParam)
	{
		try
		{
			nint target = (CommandTargetForTest != null) ? CommandTargetForTest() : AppBar.PrimaryNativeTaskbar();
			if (target == IntPtr.Zero || target == _hwndTray)
			{
				return false;
			}
			return PostMessage(target, WM_COMMAND, wParam, lParam);
		}
		catch
		{
			return false;
		}
	}

	private static nint ForwardAppBarRequest(nint sender, nint payload, COPYDATASTRUCT data)
	{
		if (data.lpData == IntPtr.Zero || data.cbData == 0 || data.cbData > 65536) return IntPtr.Zero;
		nint native = AppBar.PrimaryNativeTaskbar();
		if (native == IntPtr.Zero || native == _hwndTray) return IntPtr.Zero;
		// COPYDATA must remain synchronous while the marshalled payload is alive. Bound the
		// wait on Explorer's UI thread; failure is FALSE, never a fabricated handle or state.
		return SendMessageTimeoutW(native, WM_COPYDATA, sender, payload, 0x0003u, 200u, out nint result) != IntPtr.Zero
			? result : IntPtr.Zero;
	}

	[DllImport("user32.dll", SetLastError = true)]
	private static extern nint SendMessageTimeoutW(nint window, uint message, nint wParam, nint lParam,
		uint flags, uint timeout, out nint result);

	private static byte[] CopyPayload(COPYDATASTRUCT cds)
	{
		if (cds.lpData == IntPtr.Zero || cds.cbData <= 0)
		{
			return null;
		}
		byte[] buf = new byte[Math.Min(cds.cbData, 8192)];
		Marshal.Copy(cds.lpData, buf, 0, buf.Length);
		return buf;
	}

	// Wire handles are 32-bit even on x64; Windows sign-extends them (HandleToLong / LongToHandle).
	private static nint WireHandle(uint h)
	{
		return (nint)(int)h;
	}

	private static string WinKey(uint hwnd, uint uid)
	{
		return "w:" + hwnd.ToString("X") + ":" + uid;
	}

	private static string GuidKey(Guid g)
	{
		return "g:" + g.ToString("N");
	}

	private static string KeyFor(Entry e)
	{
		return (e.Guid != Guid.Empty) ? GuidKey(e.Guid) : WinKey(e.HWnd, e.UID);
	}

	private static uint PidOf(uint hwnd)
	{
		if (hwnd == 0)
		{
			return 0;
		}
		try
		{
			GetWindowThreadProcessId(WireHandle(hwnd), out uint pid);
			return pid;
		}
		catch
		{
			return 0;
		}
	}

	private static WIRE_NID ParseNid(byte[] payload, int offset, int length)
	{
		// Some senders post a SMALLER NOTIFYICONDATA than the full wire struct (older/partial NOTIFYICONDATA versions).
		// Copy the available bytes into a zero-filled struct-sized buffer, so any missing trailing fields read as 0
		// (uFlags gating already stops zero fields being consumed) instead of over-reading past cbData.
		int structSize = Marshal.SizeOf<WIRE_NID>();
		byte[] wireBuf = new byte[structSize];
		Buffer.BlockCopy(payload, offset, wireBuf, 0, Math.Max(0, Math.Min(length, structSize)));
		GCHandle wireGch = GCHandle.Alloc(wireBuf, GCHandleType.Pinned);
		try
		{
			return Marshal.PtrToStructure<WIRE_NID>(wireGch.AddrOfPinnedObject());
		}
		finally
		{
			wireGch.Free();
		}
	}

	// One tray message (WM_COPYDATA dwData 1): [0] magic, [4] the NIM_ message, [8] the wire NOTIFYICONDATA.
	// Returns what Shell_NotifyIcon returns to the app. Also the --traytest entry point (constructed buffers).
	internal static bool HandleTrayPayload(byte[] payload, int length)
	{
		if (payload == null || length < 12)
		{
			return false;   // not the tray notification channel
		}
		uint dwMessage = BitConverter.ToUInt32(payload, 4);
		WIRE_NID nid = ParseNid(payload, 8, length - 8);
		bool hasGuid = (nid.uFlags & NIF_GUID) != 0 && nid.guidItem != Guid.Empty;
		long now = Now;
		bool changed = false;
		bool result = true;
		nint nudge = IntPtr.Zero;
		NudgeReason nudgeWhy = NudgeReason.ModifyMiss;
		List<string> notes = null;
		lock (_gate)
		{
			int unfoldsBefore = UnfoldCount;
			switch (dwMessage)
			{
				case NIM_ADD:
				{
					// An ADD for an icon we already hold merges into it and succeeds. Explorer would refuse it, but our
					// table can still hold an icon Explorer would have dropped (an orphan, a re-created window), and
					// refusing would leave the app without the re-registration that repairs it. An ADD for a folded
					// phantom brings it back (it is live).
					Entry e = FindOrUnfoldLocked(nid, hasGuid, ref notes) ?? Adopt(nid, hasGuid);
					bool fresh = e == null;
					if (fresh)
					{
						e = NewEntryLocked(nid, hasGuid);
					}
					else
					{
						Retarget(e, nid);
						e.Orphan = false;
					}
					Merge(e, nid, now);
					if (fresh && (nid.uFlags & NIF_TIP) == 0)
					{
						RestoreTombstoneLocked(e, now, orphan: false);
					}
					CheckGuessLocked(e, nid, ref notes);
					CollapsePhantomsLocked(e, now, ref notes);
					changed = true;
					break;
				}
				case NIM_MODIFY:
				{
					int retargetsBefore = _retargets;
					int iconsBefore = _icons.Count;
					int foldsBefore = _folds.Count;
					bool fresh = false;
					// A MODIFY for a folded phantom restores it: it never touches (or moves) the survivor.
					Entry e = FindOrUnfoldLocked(nid, hasGuid, ref notes) ?? Adopt(nid, hasGuid);
					if (e == null && nid.hWnd == 0)
					{
						result = false;   // no window: nothing to show or repair
						break;
					}
					if (e == null)
					{
						// Explorer refuses a MODIFY for an icon it does not know (apps that do "MODIFY, else ADD" then add
						// it with full data). Keep it as an orphan so it still shows, and ask the owner to register again.
						e = NewEntryLocked(nid, hasGuid);
						e.Orphan = true;
						RestoreTombstoneLocked(e, now, orphan: true);
						result = false;
						nudge = WireHandle(nid.hWnd);
						nudgeWhy = NudgeReason.ModifyMiss;
						fresh = true;
					}
					else
					{
						Retarget(e, nid);
					}
					bool fieldsChanged = Merge(e, nid, now);
					CheckGuessLocked(e, nid, ref notes);
					CollapsePhantomsLocked(e, now, ref notes);
					// A MODIFY that repeats what the entry already holds (apps re-send their icon and tooltip every few
					// seconds) changes nothing on the bar, so it is not signalled. New, moved, revived or folded entries
					// and twins (merged by freshness in Snapshot) still are.
					changed = fresh || fieldsChanged || _retargets != retargetsBefore || _icons.Count != iconsBefore || _folds.Count != foldsBefore || HasTwinLocked(e);
					break;
				}
				case NIM_DELETE:
				{
					Entry e = Find(nid, hasGuid, out bool viaAlias) ?? FindAdoptable(nid, hasGuid);
					if (e == null)
					{
						result = false;   // as Explorer: nothing to delete
						break;
					}
					if (viaAlias)
					{
						// The delete of a phantom we already folded into a live icon: the live icon stays.
						_folds.Remove(WinKey(nid.hWnd, nid.uID));
						break;
					}
					RemoveLocked(e, now, tombstone: true);
					changed = true;
					break;
				}
				case NIM_SETFOCUS:
					break;
				case NIM_SETVERSION:
				{
					// uVersion is valid ONLY on NIM_SETVERSION (it shares the union slot with uTimeout on ADD/MODIFY).
					Entry e = FindOrUnfoldLocked(nid, hasGuid, ref notes) ?? Adopt(nid, hasGuid);
					if (e == null && nid.hWnd == 0)
					{
						break;
					}
					if (e == null)
					{
						// The NIM_ADD went elsewhere: keep the version on a provisional entry and ask for a re-registration.
						e = NewEntryLocked(nid, hasGuid);
						e.Orphan = true;
						RestoreTombstoneLocked(e, now, orphan: true);
						nudge = WireHandle(nid.hWnd);
						nudgeWhy = NudgeReason.SetVersionMiss;
						changed = true;
					}
					if (e.Version != nid.uVersion)
					{
						e.Version = nid.uVersion;
						changed = true;   // the bar must re-read the version, or it keeps sending the legacy format
					}
					e.Seq = ++_seq;
					e.LastTouchMs = now;
					break;
				}
				default:
					break;
			}
			if (UnfoldCount != unfoldsBefore)
			{
				changed = true;   // a folded icon is back on the bar
			}
		}
		if (notes != null)
		{
			foreach (string n in notes)
			{
				Logger.Log(n);
			}
		}
		if (nudge != IntPtr.Zero)
		{
			NudgeNow(nudge, nudgeWhy);
		}
		if (changed)
		{
			SignalChanged();
		}
		return result;
	}

	// Explorer's identity: the guid when NIF_GUID is set, else (hWnd, uID). viaAlias: the message is for a phantom that
	// was folded into the returned entry.
	private static Entry Find(in WIRE_NID nid, bool hasGuid, out bool viaAlias)
	{
		viaAlias = false;
		if (hasGuid)
		{
			if (_icons.TryGetValue(GuidKey(nid.guidItem), out Entry g))
			{
				return g;
			}
			if (nid.hWnd == 0)
			{
				return null;
			}
			// Registered without a guid, now addressed with one: the same window and id.
			foreach (Entry c in _icons.Values)
			{
				if (c.Guid == Guid.Empty && c.HWnd == nid.hWnd && c.UID == nid.uID)
				{
					return c;
				}
			}
			return null;
		}
		if (nid.hWnd == 0)
		{
			return null;
		}
		if (_icons.TryGetValue(WinKey(nid.hWnd, nid.uID), out Entry w))
		{
			return w;
		}
		// A guid icon addressed by its window and id.
		foreach (Entry c in _icons.Values)
		{
			if (c.HWnd == nid.hWnd && c.UID == nid.uID)
			{
				return c;
			}
		}
		if (_folds.TryGetValue(WinKey(nid.hWnd, nid.uID), out Fold f) && _icons.TryGetValue(f.Survivor, out Entry s))
		{
			viaAlias = true;
			return s;
		}
		return null;
	}

	// Find for a message that shows the icon is live (ADD, MODIFY, SETVERSION): a folded phantom is restored and
	// returned, never its survivor.
	private static Entry FindOrUnfoldLocked(in WIRE_NID nid, bool hasGuid, ref List<string> notes)
	{
		Entry e = Find(nid, hasGuid, out bool viaAlias);
		return viaAlias ? UnfoldLocked(nid, ref notes) : e;
	}

	// The phantom rule folds only icons that never speak again; one that does is live after all (Explorer shows it).
	// Put it back as it was (its own callback, version, image, tooltip and place on the bar) and never fold that pair
	// again. Its survivor is left exactly as it is.
	internal static int UnfoldCount;

	private static Entry UnfoldLocked(in WIRE_NID nid, ref List<string> notes)
	{
		string key = WinKey(nid.hWnd, nid.uID);
		if (!_folds.TryGetValue(key, out Fold f) || f.Old == null)
		{
			return null;
		}
		_folds.Remove(key);
		Entry e = f.Old;
		if (_icons.TryGetValue(e.Key, out Entry already))
		{
			return already;
		}
		e.Dead = false;
		_icons[e.Key] = e;
		if (_keptApart.Count > 256)
		{
			_keptApart.Clear();
		}
		_keptApart.Add(PairKey(e.Key, f.Survivor));
		UnfoldCount++;
		(notes ??= new List<string>()).Add($"[trayhost] {e.Key} spoke after it was folded into {f.Survivor}: restored; the two are kept apart");
		return e;
	}

	private static string PairKey(string a, string b)
	{
		return (string.CompareOrdinal(a, b) < 0) ? (a + "|" + b) : (b + "|" + a);
	}

	// An entry of the same process and id whose window no longer exists (the app re-created its window).
	private static Entry FindAdoptable(in WIRE_NID nid, bool hasGuid)
	{
		if (hasGuid || nid.hWnd == 0)
		{
			return null;
		}
		uint pid = PidOf(nid.hWnd);
		if (pid == 0)
		{
			return null;
		}
		foreach (Entry c in _icons.Values)
		{
			if (c.Guid == Guid.Empty && c.Pid == pid && c.UID == nid.uID && c.HWnd != nid.hWnd && !IsWindow(WireHandle(c.HWnd)))
			{
				return c;
			}
		}
		return null;
	}

	private static Entry Adopt(in WIRE_NID nid, bool hasGuid)
	{
		Entry c = FindAdoptable(nid, hasGuid);
		if (c != null)
		{
			Retarget(c, nid);
		}
		return c;
	}

	// Point an entry at the window the message came from (a guid icon re-registered from a new window, or an adopted
	// entry), re-keying a window-keyed entry so later messages find it directly.
	private static void Retarget(Entry e, in WIRE_NID nid)
	{
		if (nid.hWnd == 0 || e.HWnd == nid.hWnd)
		{
			return;
		}
		_retargets++;
		e.HWnd = nid.hWnd;
		e.Pid = PidOf(nid.hWnd);
		e.Dead = false;
		if (e.Guid == Guid.Empty)
		{
			string newKey = WinKey(e.HWnd, e.UID);
			if (newKey != e.Key && !_icons.ContainsKey(newKey))
			{
				_icons.Remove(e.Key);
				e.Key = newKey;
				_icons[newKey] = e;
			}
		}
	}

	private static Entry NewEntryLocked(in WIRE_NID nid, bool hasGuid)
	{
		Entry e = new Entry
		{
			HWnd = nid.hWnd,
			UID = nid.uID,
			Guid = hasGuid ? nid.guidItem : Guid.Empty,
			Pid = PidOf(nid.hWnd),
			Created = ++_seq,
			CreatedMs = Now
		};
		e.Key = KeyFor(e);
		_icons[e.Key] = e;
		// A real icon on this window and id replaces any folded phantom of it.
		_folds.Remove(WinKey(nid.hWnd, nid.uID));
		return e;
	}

	// Merge only the fields the sender actually supplied (uFlags), so a tooltip-only MODIFY doesn't wipe the callback
	// message or icon we captured at ADD time.
	// Returns true when a field the bar shows (callback, icon handle, tooltip, hidden state) took a new value. The sequence
	// and touch time always advance (freshness rules depend on them).
	private static bool Merge(Entry e, in WIRE_NID nid, long now)
	{
		e.Seq = ++_seq;
		e.LastTouchMs = now;
		bool changed = false;
		if ((nid.uFlags & NIF_MESSAGE) != 0 && e.Callback != nid.uCallbackMessage)
		{
			e.Callback = nid.uCallbackMessage;
			changed = true;
		}
		if ((nid.uFlags & NIF_ICON) != 0 && nid.hIcon != 0)
		{
			nint icon = WireHandle(nid.hIcon);
			if (icon != e.HIcon)
			{
				e.HIcon = icon;
				changed = true;
			}
		}
		if ((nid.uFlags & NIF_TIP) != 0)
		{
			string tip = (nid.szTip ?? "").Replace("\0", " ").Trim();
			if (!string.Equals(tip, e.Tip, StringComparison.Ordinal))
			{
				e.Tip = tip;
				changed = true;
			}
		}
		if ((nid.uFlags & NIF_STATE) != 0 && (nid.dwStateMask & NIS_HIDDEN) != 0)
		{
			bool hidden = (nid.dwState & NIS_HIDDEN) != 0;
			if (hidden != e.Hidden)
			{
				e.Hidden = hidden;
				changed = true;
			}
		}
		return changed;
	}

	// An entry that shares its window and id with an entry of the other kind (a guid icon and a plain one): Snapshot
	// merges such twins by freshness, so any message to either can change what the bar shows.
	private static bool HasTwinLocked(Entry e)
	{
		foreach (Entry other in _icons.Values)
		{
			if (!ReferenceEquals(other, e) && other.HWnd == e.HWnd && other.UID == e.UID && (other.Guid == Guid.Empty) != (e.Guid == Guid.Empty))
			{
				return true;
			}
		}
		return false;
	}

	private static void RemoveLocked(Entry e, long now, bool tombstone)
	{
		_icons.Remove(e.Key);
		List<string> stale = null;
		foreach (KeyValuePair<string, Fold> a in _folds)
		{
			if (a.Value.Survivor == e.Key)
			{
				(stale ??= new List<string>()).Add(a.Key);
			}
		}
		if (stale != null)
		{
			foreach (string k in stale)
			{
				_folds.Remove(k);
			}
		}
		if (tombstone && (!string.IsNullOrEmpty(e.Tip) || e.Callback != 0))
		{
			if (_tombs.Count > 64)
			{
				_tombs.Clear();
			}
			_tombs[e.Key] = new Tomb(e.Tip, e.Callback, e.Version, now);
		}
	}

	// A deleted icon added again without NIF_TIP gets its tooltip back (TombstoneMs). An orphan of a deleted icon (its
	// re-registration went elsewhere) also gets the callback and version back (OrphanTombstoneMs): they are what that
	// icon used a moment ago, and without them a v4 icon would get v0 clicks, or none.
	private static void RestoreTombstoneLocked(Entry e, long now, bool orphan)
	{
		if (!_tombs.TryGetValue(e.Key, out Tomb t))
		{
			return;
		}
		long age = now - t.At;
		if (age > (orphan ? OrphanTombstoneMs : TombstoneMs))
		{
			_tombs.Remove(e.Key);
			return;
		}
		_tombs.Remove(e.Key);
		if (string.IsNullOrEmpty(e.Tip))
		{
			e.Tip = t.Tip ?? "";
		}
		if (orphan)
		{
			if (e.Callback == 0)
			{
				e.Callback = t.Callback;
			}
			if (e.Version == 0)
			{
				e.Version = t.Version;
			}
		}
	}

	// An entry whose window is gone is hidden at once (Snapshot leaves it out) but kept for DeadGraceMs: an app that
	// re-creates its window and goes on with NIM_MODIFY only is then still adopted (FindAdoptable needs the old entry)
	// instead of becoming an orphan without a callback. Then it is dropped. Returns true when an icon was hidden or shown
	// again (what Snapshot returns changed).
	private static bool PruneDeadLocked(long now)
	{
		bool changed = false;
		List<Entry> dead = null;
		foreach (Entry e in _icons.Values)
		{
			if (IsWindow(WireHandle(e.HWnd)))
			{
				if (e.Dead)
				{
					e.Dead = false;
					changed = true;
				}
				continue;
			}
			if (!e.Dead)
			{
				e.Dead = true;
				e.DeadSinceMs = now;
				changed = true;
			}
			else if (now - e.DeadSinceMs >= DeadGraceMs)
			{
				(dead ??= new List<Entry>()).Add(e);
			}
		}
		if (dead != null)
		{
			foreach (Entry e in dead)
			{
				RemoveLocked(e, now, tombstone: false);
			}
		}
		if (_folds.Count > 256)
		{
			_folds.Clear();
		}
		return changed;
	}

	// Phantoms: an old registration nobody deletes, replaced by a newer one with the same non-empty tooltip.
	//   - Task Manager: a new id on the same window while the old id never speaks again. Folded once the new icon
	//     appeared at least PhantomGapMs after the old one went quiet; icons registered together in one burst (one per
	//     CPU core, the same tooltip) are never folded, however long they stay quiet.
	//   - A re-created window (same process and id) whose old window is still alive: folded once the old one has been
	//     quiet for PhantomIdleMs.
	// Either way the old icon must not have spoken since the new one appeared, and an empty tooltip never matches. A
	// version is carried only between the same id. A folded icon that speaks again is restored (UnfoldLocked) and that
	// pair is never folded again.
	private static bool IsPhantomPair(Entry keep, Entry old)
	{
		if (ReferenceEquals(keep, old) || keep.Guid != Guid.Empty || old.Guid != Guid.Empty || keep.Dead || old.Dead)
		{
			return false;
		}
		if (string.IsNullOrEmpty(keep.Tip) || !string.Equals(keep.Tip, old.Tip, StringComparison.Ordinal))
		{
			return false;
		}
		if (_keptApart.Count > 0 && _keptApart.Contains(PairKey(keep.Key, old.Key)))
		{
			return false;   // proven live together
		}
		if (keep.HWnd == old.HWnd)
		{
			return keep.UID != old.UID;
		}
		return keep.UID == old.UID && keep.Pid != 0 && keep.Pid == old.Pid;
	}

	// 1 = fold now, 0 = never (both are live), -1 = not yet (re-check once the old one has been quiet long enough).
	private static int PhantomVerdict(Entry keep, Entry old, long now)
	{
		if (old.Seq > keep.Seq || old.LastTouchMs > keep.CreatedMs)
		{
			return 0;
		}
		if (keep.HWnd == old.HWnd)
		{
			return (keep.CreatedMs - old.LastTouchMs >= PhantomGapMs) ? 1 : 0;
		}
		return (now - old.LastTouchMs >= PhantomIdleMs) ? 1 : -1;
	}

	private static bool CollapsePhantomsLocked(Entry keep, long now, ref List<string> notes)
	{
		List<Entry> drop = null;
		bool pending = false;
		foreach (Entry old in _icons.Values)
		{
			if (!IsPhantomPair(keep, old))
			{
				continue;
			}
			int verdict = PhantomVerdict(keep, old, now);
			if (verdict < 0)
			{
				pending = true;
			}
			else if (verdict > 0)
			{
				(drop ??= new List<Entry>()).Add(old);
			}
		}
		if (pending)
		{
			ArmDeferredCollapseLocked();
		}
		if (drop == null)
		{
			return false;
		}
		foreach (Entry old in drop)
		{
			if (keep.Callback == 0 && old.Callback != 0)
			{
				keep.Callback = old.Callback;
			}
			if (keep.UID == old.UID && old.Version > keep.Version)
			{
				keep.Version = old.Version;
			}
			// Phantoms folded into this one earlier now belong to the new survivor (Task Manager's chain of ids).
			foreach (Fold f in _folds.Values)
			{
				if (f.Survivor == old.Key)
				{
					f.Survivor = keep.Key;
				}
			}
			RemoveLocked(old, now, tombstone: false);
			_folds[WinKey(old.HWnd, old.UID)] = new Fold { Survivor = keep.Key, Old = old };
			(notes ??= new List<string>()).Add($"[trayhost] folded phantom {old.Key} into {keep.Key} (tip='{keep.Tip}')");
		}
		return true;
	}

	private static void ArmDeferredCollapseLocked()
	{
		// The deferred pass never re-arms itself: icons that keep updating together stay pending forever, and they are
		// re-checked on their own next message anyway (no idle timer).
		if (_collapseArmed || _inDeferredCollapse || _hwndTray == IntPtr.Zero)
		{
			return;
		}
		_collapseArmed = true;
		SetTimer(_hwndTray, COLLAPSE_TIMER, (uint)(PhantomIdleMs + 250), IntPtr.Zero);
	}

	// Re-runs the phantom rule over the whole table once the candidates have had time to go idle (no message has to
	// arrive for a stale phantom to be folded). Host thread; also called by --traytest.
	internal static bool RunDeferredCollapse()
	{
		bool changed = false;
		List<string> notes = null;
		lock (_gate)
		{
			_collapseArmed = false;
			_inDeferredCollapse = true;
			try
			{
				long now = Now;
				List<Entry> all = new List<Entry>(_icons.Values);
				all.Sort((a, b) => b.Seq.CompareTo(a.Seq));   // freshest first, so the survivor is the freshest
				foreach (Entry keep in all)
				{
					if (_icons.TryGetValue(keep.Key, out Entry live) && ReferenceEquals(live, keep) && CollapsePhantomsLocked(keep, now, ref notes))
					{
						changed = true;
					}
				}
			}
			finally
			{
				_inDeferredCollapse = false;
			}
		}
		if (notes != null)
		{
			foreach (string n in notes)
			{
				Logger.Log(n);
			}
		}
		if (changed)
		{
			SignalChanged();
		}
		return changed;
	}

	// ---- recovery for icons without a callback ----

	// Asks the owner window to register its icons again with a targeted TaskbarCreated (Electron, Chromium, WinForms,
	// WPF Hardcodet, Qt and Java AWT all answer it on the window that owns the icon). Throttled per window; never sent to
	// this process or to Explorer. Any thread: runs on the tray-host thread, which first makes sure FindWindow returns
	// our window, so the re-registration reaches us.
	public static bool RequestNudge(nint owner, NudgeReason reason)
	{
		nint host = _hwndTray;
		if (host != IntPtr.Zero && _hostThreadId != 0 && _hostThreadId != GetCurrentThreadId())
		{
			return PostMessage(host, WM_APP_NUDGE, owner, (nint)(int)reason);
		}
		return NudgeNow(owner, reason);
	}

	private static bool NudgeNow(nint owner, NudgeReason reason)
	{
		try
		{
			if (owner == IntPtr.Zero || !IsWindow(owner))
			{
				return false;
			}
			GetWindowThreadProcessId(owner, out uint pid);
			if (pid == 0 || (pid == (uint)Environment.ProcessId && !TestAllowOwnProcess))
			{
				return false;
			}
			GetWindowThreadProcessId(GetShellWindow(), out uint shellPid);
			if (shellPid != 0 && pid == shellPid)
			{
				return false;
			}
			long now = Now;
			lock (_nudges)
			{
				if (!_nudges.TryGetValue(owner, out List<long> times))
				{
					if (_nudges.Count > 64)
					{
						List<nint> gone = new List<nint>();
						foreach (nint k in _nudges.Keys)
						{
							if (!IsWindow(k))
							{
								gone.Add(k);
							}
						}
						foreach (nint k in gone)
						{
							_nudges.Remove(k);
						}
					}
					times = new List<long>();
					_nudges[owner] = times;
				}
				times.RemoveAll(t => now - t > NudgeWindowMs);
				if (times.Count >= NudgeMaxPerWindow || (times.Count > 0 && now - times[times.Count - 1] < NudgeMinGapMs))
				{
					return false;
				}
				times.Add(now);
			}
			EnsureTop("nudge");
			if (_wmTaskbarCreated == 0)
			{
				_wmTaskbarCreated = RegisterWindowMessageW("TaskbarCreated");
			}
			bool ok = PostMessage(owner, _wmTaskbarCreated, IntPtr.Zero, IntPtr.Zero);
			// Some apps listen for TaskbarCreated on their main window rather than on the icon's window: tell the rest of
			// that process's top-level windows too (what a broadcast would reach, limited to this one app).
			int others = (pid == (uint)Environment.ProcessId) ? 0 : PostToProcessWindows(pid, owner, _wmTaskbarCreated);
			string text = $"[trayhost] nudge hwnd=0x{(long)owner:X} class={ClassOf(owner)} reason={reason} posted={ok} other-windows={others}";
			LastNudge = text;
			Interlocked.Increment(ref NudgeCount);
			Logger.Log(text);
			return ok;
		}
		catch
		{
			return false;
		}
	}

	private static int PostToProcessWindows(uint pid, nint except, uint msg)
	{
		int n = 0;
		try
		{
			EnumWindows(delegate(nint h, nint _)
			{
				if (h != except && GetWindowThreadProcessId(h, out uint p) != 0 && p == pid && PostMessage(h, msg, IntPtr.Zero, IntPtr.Zero))
				{
					n++;
				}
				return n < 32;
			}, IntPtr.Zero);
		}
		catch
		{
		}
		return n;
	}

	private delegate bool EnumWindowsProc(nint hwnd, nint lParam);

	[DllImport("user32.dll")]
	private static extern bool EnumWindows(EnumWindowsProc cb, nint lParam);

	internal static uint TaskbarCreatedMessage()
	{
		if (_wmTaskbarCreated == 0)
		{
			_wmTaskbarCreated = RegisterWindowMessageW("TaskbarCreated");
		}
		return _wmTaskbarCreated;
	}

	// For an icon with no callback: the fixed callback of the Electron / Chromium tray host window, when the owner is
	// one (by window class) and the guess was not disproved for that class. A wrong guess is harmless: both route the
	// message by wParam == icon id, and an unhandled WM_APP + 1 goes to DefWindowProc. The guess is checked against the
	// callback in that window's next NIM_ADD.
	internal static uint GuessCallback(nint owner, out string cls)
	{
		cls = ClassOf(owner);
		if (Array.IndexOf(ChromiumFamilyClasses, cls) < 0)
		{
			return 0;
		}
		lock (_gate)
		{
			if (_guessOff.Contains(cls))
			{
				return 0;
			}
			_guessUsed[owner] = cls;
		}
		return ChromiumFamilyCallback;
	}

	private static void CheckGuessLocked(Entry e, in WIRE_NID nid, ref List<string> notes)
	{
		if ((nid.uFlags & NIF_MESSAGE) == 0 || _guessUsed.Count == 0)
		{
			return;
		}
		nint owner = WireHandle(nid.hWnd);
		if (!_guessUsed.TryGetValue(owner, out string cls))
		{
			return;
		}
		_guessUsed.Remove(owner);
		if (nid.uCallbackMessage == ChromiumFamilyCallback)
		{
			(notes ??= new List<string>()).Add($"[trayhost] callback guess confirmed for {cls} (0x{ChromiumFamilyCallback:X})");
		}
		else
		{
			_guessOff.Add(cls);
			(notes ??= new List<string>()).Add($"[trayhost] callback guess WRONG for {cls}: real 0x{nid.uCallbackMessage:X}, guessed 0x{ChromiumFamilyCallback:X}; guessing turned off for this class");
		}
	}

	// The owner is an Electron / Chromium tray host window (version 0 icons that also open their menu on WM_CONTEXTMENU).
	internal static bool IsChromiumFamilyWindow(nint owner)
	{
		return owner != IntPtr.Zero && Array.IndexOf(ChromiumFamilyClasses, ClassOf(owner)) >= 0;
	}

	internal static bool IsGuessDisabled(string cls)
	{
		lock (_gate)
		{
			return _guessOff.Contains(cls);
		}
	}

	private static string ClassOf(nint hwnd)
	{
		try
		{
			StringBuilder sb = new StringBuilder(128);
			GetClassNameW(hwnd, sb, sb.Capacity);
			return sb.ToString();
		}
		catch
		{
			return "";
		}
	}

	// ---- Shell_NotifyIconGetRect (WM_COPYDATA dwData 3) ----

	// Wire NOTIFYICONIDENTIFIER request (as ManagedShell implements it): [0] magic, [4] 1 = top-left / 2 = bottom-right,
	// [8] cbSize, [12] padding, [16] hWnd, then uID and guidItem. The padding aligns hWnd for a pointer-sized field, so
	// both widths are accepted: a 40-byte request has a 32-bit hWnd ([20] uID, [24] guid), a longer one a 64-bit hWnd
	// ([24] uID, [28] guid); handles fit in the low 32 bits either way. Answered with MAKELONG(x, y) in physical screen
	// pixels, or 0 when the icon is unknown or not on screen.
	internal static nint HandleIconRectPayload(byte[] payload, int length)
	{
		if (payload == null || length < 24)
		{
			return IntPtr.Zero;
		}
		bool wideHandle = length >= 44;
		int uidAt = wideHandle ? 24 : 20;
		int guidAt = uidAt + 4;
		if (length < uidAt + 4)
		{
			return IntPtr.Zero;
		}
		uint which = BitConverter.ToUInt32(payload, 4);
		uint hwnd = BitConverter.ToUInt32(payload, 16);
		uint uid = BitConverter.ToUInt32(payload, uidAt);
		Guid guid = Guid.Empty;
		if (length >= guidAt + 16)
		{
			byte[] g = new byte[16];
			Buffer.BlockCopy(payload, guidAt, g, 0, 16);
			guid = new Guid(g);
		}
		nint owner;
		uint id;
		Guid entryGuid;
		lock (_gate)
		{
			WIRE_NID probe = new WIRE_NID { hWnd = hwnd, uID = uid, guidItem = guid, uFlags = (guid != Guid.Empty) ? NIF_GUID : 0u };
			Entry e = Find(probe, guid != Guid.Empty, out _);
			if (e == null)
			{
				return IntPtr.Zero;
			}
			owner = WireHandle(e.HWnd);
			id = e.UID;
			entryGuid = e.Guid;
		}
		(int Left, int Top, int Right, int Bottom)? r = null;
		try
		{
			r = IconRectProvider?.Invoke(owner, id, entryGuid);
		}
		catch
		{
		}
		if (!r.HasValue)
		{
			return IntPtr.Zero;
		}
		return which switch
		{
			1u => (nint)(int)(uint)TrayProtocol.MakeParam(r.Value.Left, r.Value.Top),
			2u => (nint)(int)(uint)TrayProtocol.MakeParam(r.Value.Right, r.Value.Bottom),
			_ => IntPtr.Zero
		};
	}

	// ---- --traytest ----

	internal static void SweepDeadOwnersForTest()
	{
		SweepDeadOwners();
	}

	internal static void ResetForTest()
	{
		lock (_gate)
		{
			_icons.Clear();
			_folds.Clear();
			_keptApart.Clear();
			_tombs.Clear();
			_guessUsed.Clear();
			_guessOff.Clear();
		}
		lock (_nudges)
		{
			_nudges.Clear();
		}
		NudgeCount = 0;
		SignalCount = 0;
		LastNudge = "";
	}

	internal static int CountForTest()
	{
		lock (_gate)
		{
			return _icons.Count;
		}
	}

	private static int _lastDiagCount = -1;
	private static void TrayTableDiag()
	{
		try
		{
			string dump;
			lock (_gate)
			{
				if (_icons.Count == _lastDiagCount)
				{
					return;
				}
				_lastDiagCount = _icons.Count;
				System.Text.StringBuilder sb = new System.Text.StringBuilder();
				foreach (KeyValuePair<string, Entry> kv in _icons)
				{
					sb.Append(kv.Key).Append("(hwnd=").Append(kv.Value.HWnd).Append(",tip=").Append(kv.Value.Tip).Append(") ");
				}
				dump = sb.ToString();
			}
			Logger.Log($"[trayhost] table={_lastDiagCount} :: {dump}");
		}
		catch
		{
		}
	}

	// Change signals are coalesced: Changed fires once the table has been quiet for CoalesceQuietMs, but never later than
	// CoalesceMaxWaitMs after the first pending signal. An icon that animates faster than the quiet time (a sync or scan
	// icon changing every frame) therefore cannot hold every other tray update back: one lands at least every 750 ms.
	// Only the first signal of a burst posts anything; later ones just stamp the time (no UI-thread work per signal).
	internal const long CoalesceQuietMs = 150L;

	internal const long CoalesceMaxWaitMs = 750L;

	private static long _signalFirstMs;

	private static long _signalLastMs;

	private static int _signalArmed;

	// Milliseconds still to wait before Changed may fire (<= 0: fire now). Pure (tested by --traytest).
	internal static long CoalesceWaitMs(long now, long firstSignalMs, long lastSignalMs)
	{
		long quiet = CoalesceQuietMs - (now - lastSignalMs);
		long cap = CoalesceMaxWaitMs - (now - firstSignalMs);
		return Math.Min(quiet, cap);
	}

	private static void SignalChanged()
	{
		Interlocked.Increment(ref SignalCount);
		long now = Environment.TickCount64;
		Volatile.Write(ref _signalLastMs, now);
		if (Interlocked.CompareExchange(ref _signalArmed, 1, 0) != 0)
		{
			return;   // a burst is already pending: its timer reads the new time stamp
		}
		Volatile.Write(ref _signalFirstMs, now);
		System.Windows.Threading.Dispatcher ui = _uiDispatcher;
		if (ui != null && !ui.HasShutdownStarted)
		{
			// One post to the UI thread arms the timer there; Changed then fires on the UI thread, where the bar reads the
			// in-memory table directly (no pool timer, no pool hop, no second dispatcher hop).
			try
			{
				ui.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, (Action)ArmUiCoalesce);
				return;
			}
			catch
			{
			}
		}
		try
		{
			if (_coalesce == null)
			{
				_coalesce = new Timer(delegate
				{
					long wait = CoalesceWaitMs(Environment.TickCount64, Volatile.Read(ref _signalFirstMs), Volatile.Read(ref _signalLastMs));
					if (wait > 0L)
					{
						_coalesce.Change(wait, Timeout.Infinite);
						return;
					}
					Volatile.Write(ref _signalArmed, 0);   // before Changed: a signal raised from here on starts a new burst
					try { Changed?.Invoke(); } catch { }
				}, null, Timeout.Infinite, Timeout.Infinite);
			}
			_coalesce.Change(CoalesceQuietMs, Timeout.Infinite);
		}
		catch
		{
			Volatile.Write(ref _signalArmed, 0);
		}
	}

	private static void ArmUiCoalesce()
	{
		if (_uiCoalesce == null)
		{
			_uiCoalesce = new System.Windows.Threading.DispatcherTimer(System.Windows.Threading.DispatcherPriority.Background);
			_uiCoalesce.Tick += delegate
			{
				long wait = CoalesceWaitMs(Environment.TickCount64, Volatile.Read(ref _signalFirstMs), Volatile.Read(ref _signalLastMs));
				if (wait > 0L)
				{
					_uiCoalesce.Interval = TimeSpan.FromMilliseconds(wait);   // still in a burst: tick again when due
					return;
				}
				_uiCoalesce.Stop();
				Volatile.Write(ref _signalArmed, 0);   // before Changed: a signal raised from here on starts a new burst
				try { Changed?.Invoke(); } catch { }
			};
		}
		_uiCoalesce.Interval = TimeSpan.FromMilliseconds(CoalesceQuietMs);
		_uiCoalesce.Start();
	}

	// Tray-host thread, on its 1 s timer: an icon whose owner window is gone (a crashed or killed tray-only app never
	// sends NIM_DELETE, and no shell window is destroyed) is hidden within about a second, as the bar's former 4 s scan
	// did. One IsWindow per icon; the bar hears about it only when an icon actually went (or came back).
	private static void SweepDeadOwners()
	{
		bool changed;
		lock (_gate)
		{
			changed = PruneDeadLocked(Now);
		}
		if (changed)
		{
			SignalChanged();
		}
	}

	// ---- interop ----

	private delegate nint WndProc(nint hWnd, uint msg, nint wParam, nint lParam);

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
		[MarshalAs(UnmanagedType.LPWStr)] public string lpszMenuName;
		[MarshalAs(UnmanagedType.LPWStr)] public string lpszClassName;
	}

	[StructLayout(LayoutKind.Sequential)]
	private struct COPYDATASTRUCT
	{
		public nint dwData;
		public int cbData;
		public nint lpData;
	}

	// Shell tray wire NOTIFYICONDATA. Handles are 32-bit on the wire even on x64 (USER/GDI handles fit in 32 bits).
	// Offsets: cbSize 0, hWnd 4, uID 8, uFlags 12, uCallbackMessage 16, hIcon 20, szTip 24 (128 chars), dwState 280,
	// dwStateMask 284, szInfo 288 (256 chars), uVersion 800, szInfoTitle 804 (64 chars), dwInfoFlags 932, guidItem 936.
	[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
	private struct WIRE_NID
	{
		public uint cbSize;
		public uint hWnd;
		public uint uID;
		public uint uFlags;
		public uint uCallbackMessage;
		public uint hIcon;
		[MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
		public uint dwState;
		public uint dwStateMask;
		[MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
		public uint uVersion;
		[MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
		public uint dwInfoFlags;
		public Guid guidItem;
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
	[DllImport("user32.dll")] private static extern bool SetWindowPos(nint h, nint after, int x, int y, int w, int hh, uint flags);
	[DllImport("user32.dll")] private static extern bool ShowWindow(nint h, int cmd);
	[DllImport("user32.dll")] private static extern nint SetTimer(nint h, nuint id, uint elapse, nint func);
	[DllImport("user32.dll")] private static extern bool KillTimer(nint h, nuint id);
	[DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern uint RegisterWindowMessageW(string s);
	[DllImport("user32.dll")] private static extern bool SendNotifyMessageW(nint h, uint m, nint w, nint l);
	[DllImport("user32.dll")] private static extern bool PostMessage(nint h, uint m, nint w, nint l);
	[DllImport("user32.dll")] private static extern bool RegisterShellHookWindow(nint h);
	[DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern nint FindWindowW(string cls, string title);
	[DllImport("user32.dll")] private static extern bool IsWindow(nint h);
	[DllImport("user32.dll")] private static extern nint GetShellWindow();
	[DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassNameW(nint h, StringBuilder sb, int max);

	[DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint hWnd, out uint pid);
	[DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
	[DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern nint GetModuleHandleW(string n);
	[DllImport("user32.dll")] private static extern int GetMessageW(out MSG msg, nint h, uint min, uint max);
	[DllImport("user32.dll")] private static extern bool TranslateMessage(ref MSG msg);
	[DllImport("user32.dll")] private static extern nint DispatchMessageW(ref MSG msg);
	[DllImport("user32.dll")] private static extern void PostQuitMessage(int code);
}
