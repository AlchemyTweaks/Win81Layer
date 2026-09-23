using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
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
//   4. TaskbarWindow renders the table and forwards clicks via the existing TrayReader.ForwardClick, so the owning
//      app shows its own real context menu / toggles its own window.
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
		public uint Callback;
		public uint Version;   // NOTIFYICON_VERSION (from NIM_SETVERSION); 0 = legacy. Decides click-forward convention.
		public nint HIcon;
		public string Tip = "";
		public long Seq;   // monotonically bumped on every message that touches this entry (freshest wins on collapse)
		public string Key = "";   // the _icons dictionary key, so Snapshot can prune this entry when it's the stale one
	}

	private static long _seq;

	private static int _broadcastRound;

	private const int WM_COPYDATA = 0x004A;
	private const int WM_CLOSE = 0x0010;
	private const int WM_DESTROY = 0x0002;
	private const int WM_TIMER = 0x0113;
	private const uint REASSERT_TIMER = 1u;
	private const uint BROADCAST_TIMER = 2u;
	private const int OFFSCREEN = -32000;
	private const uint WS_POPUP = 0x80000000u;
	private const uint WS_CHILD = 0x40000000u;
	private const uint WS_EX_TOPMOST = 0x00000008u;
	private const uint WS_EX_TOOLWINDOW = 0x00000080u;
	private const int SW_HIDE = 0;
	private const uint SWP_NOMOVE = 0x0002u, SWP_NOSIZE = 0x0001u, SWP_NOACTIVATE = 0x0010u;

	// NOTIFYICONDATA uFlags — which fields in a MODIFY are actually present (the rest must not clobber our table).
	private const uint NIF_MESSAGE = 0x01u;
	private const uint NIF_ICON = 0x02u;
	private const uint NIF_TIP = 0x04u;
	private const uint NIF_GUID = 0x20u;   // guidItem is only valid when this flag is set; otherwise it is uninitialized

	private static readonly nint HWND_TOPMOST = new nint(-1);
	private static readonly nint HWND_BROADCAST = new nint(0xFFFF);

	private static readonly object _gate = new object();
	private static readonly Dictionary<string, Entry> _icons = new Dictionary<string, Entry>();
	private static WndProc _proc;            // GC-root the delegate for the window's lifetime
	private static WndProc _childProc;
	private static Thread _thread;
	private static nint _hwndTray;
	private static uint _wmTaskbarCreated;
	private static volatile bool _active;
	private static Timer _coalesce;

	// Raised (coalesced, ~150ms) whenever the icon table changes. Fires on a threadpool thread; consumers marshal.
	public static event Action Changed;

	public static bool Active => _active;

	public static void Start()
	{
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

	// Snapshot for the taskbar: real owner window + callback message => the existing ForwardClick shows the app's own
	// menu. Dead-owner and shell-owned entries are dropped by the consumer (ApplyTrayInfos already filters shell pid).
	public static List<TrayIconInfo> Snapshot()
	{
		List<TrayIconInfo> list = new List<TrayIconInfo>();
		lock (_gate)
		{
			// Collapse phantom duplicates: some apps (Task Manager) register MANY tray icons on the SAME window with the
			// SAME tooltip and never delete the old ones. Keep only the freshest entry per (ownerWindow, tooltip), and
			// prune the losers + dead-window entries so the table can't grow unbounded and the bar shows one icon.
			Dictionary<string, Entry> best = new Dictionary<string, Entry>();
			List<string> drop = new List<string>();
			foreach (KeyValuePair<string, Entry> kv in _icons)
			{
				Entry e = kv.Value;
				if (!IsWindow((nint)(long)e.HWnd))
				{
					drop.Add(kv.Key);
					continue;
				}
				string ident = e.HWnd + "|" + (e.Tip ?? "");
				if (best.TryGetValue(ident, out Entry cur))
				{
					// The older of the two is stale — remember its key so we can prune it from _icons. Carry the version +
					// callback to the survivor so a v4 icon isn't downgraded to legacy (right-click menu lost) just because a
					// fresher re-registration (v0 until its own SETVERSION arrives) won the freshest-Seq contest.
					if (e.Seq >= cur.Seq)
					{
						if (cur.Version > e.Version) { e.Version = cur.Version; }
						if (e.Callback == 0 && cur.Callback != 0) { e.Callback = cur.Callback; }
						drop.Add(cur.Key);
						best[ident] = e;
					}
					else
					{
						if (e.Version > cur.Version) { cur.Version = e.Version; }
						if (cur.Callback == 0 && e.Callback != 0) { cur.Callback = e.Callback; }
						drop.Add(kv.Key);
					}
				}
				else
				{
					best[ident] = e;
				}
			}
			foreach (string k in drop)
			{
				_icons.Remove(k);
			}
			foreach (Entry e in best.Values)
			{
				nint owner = (nint)(long)e.HWnd;
				list.Add(new TrayIconInfo
				{
					OwnerHwnd = owner,
					Id = e.UID,
					CallbackMessage = e.Callback,
					Version = e.Version,
					HIcon = e.HIcon,          // real (cross-process) HICON; UI-thread IconToSource converts + falls back
					Image = null,
					Tooltip = e.Tip ?? "",
					Hidden = false,
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
			RegisterShellHookWindow(_hwndTray);
			// Re-assert topmost every second so we keep winning FindWindow as explorer re-asserts its own taskbar and as
			// apps register icons later; this is what makes the takeover durable rather than a one-shot at startup.
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
					if (data.dwData == new IntPtr(1) && HandleCopyData(lParam))
					{
						return new nint(1);
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
				else
				{
					SetWindowPos(_hwndTray, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
				}
				return IntPtr.Zero;
			case WM_CLOSE:
				DestroyWindow(hWnd);
				return IntPtr.Zero;
			case WM_DESTROY:
				PostQuitMessage(0);
				return IntPtr.Zero;
		}
		return DefWindowProcW(hWnd, msg, wParam, lParam);
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

	private static bool HandleCopyData(nint lParam)
	{
		COPYDATASTRUCT cds = Marshal.PtrToStructure<COPYDATASTRUCT>(lParam);
		if (cds.dwData.ToInt64() != 1 || cds.cbData < 8 || cds.lpData == IntPtr.Zero)
		{
			return false;   // not the tray notification channel
		}
		uint dwMessage = (uint)Marshal.ReadInt32(cds.lpData, 4);   // [0]=magic [4]=NIM_ message
		// Some senders post a SMALLER NOTIFYICONDATA than the full wire struct (older/partial NOTIFYICONDATA versions).
		// Marshalling the full struct straight off the buffer would over-read past cbData and fault (caught upstream, so
		// the icon is silently DROPPED = a missing-icon cause). Copy the available bytes into a zero-filled struct-sized
		// buffer first, so any missing trailing fields read as 0 (uFlags gating already stops zero fields being consumed).
		int structSize = Marshal.SizeOf<WIRE_NID>();
		int avail = (int)cds.cbData - 8;
		if (avail <= 0)
		{
			return false;
		}
		byte[] wireBuf = new byte[structSize];
		Marshal.Copy(cds.lpData + 8, wireBuf, 0, Math.Min(avail, structSize));
		WIRE_NID nid;
		GCHandle wireGch = GCHandle.Alloc(wireBuf, GCHandleType.Pinned);
		try
		{
			nid = Marshal.PtrToStructure<WIRE_NID>(wireGch.AddrOfPinnedObject());
		}
		finally
		{
			wireGch.Free();
		}
		// Identity: modern apps (Task Manager) recreate their notification window (new hWnd) while keeping the SAME icon.
		// Keying on hWnd makes every recreation a fresh duplicate. Prefer the stable guidItem when the wire carries it;
		// otherwise key on (processId, uID) — stable across window recreation for a given process — and only fall back
		// to (hWnd, uID) when the process id can't be resolved. This collapses Task Manager's re-registrations to ONE.
		string key;
		// CRITICAL: only trust guidItem when NIF_GUID is set. Apps that send a smaller NOTIFYICONDATA (e.g. WinForms
		// NotifyIcon, and the launcher's own icon) leave guidItem as uninitialized wire memory — reading it yields a
		// DIFFERENT garbage value every message, which created a fresh duplicate entry each time. Without a real guid,
		// key on (processId, uID), which is stable across a process's window recreations.
		if ((nid.uFlags & NIF_GUID) != 0 && nid.guidItem != Guid.Empty)
		{
			key = "g:" + nid.guidItem.ToString("N");
		}
		else
		{
			uint pid = 0;
			if (nid.hWnd != 0)
			{
				try { GetWindowThreadProcessId((nint)(long)nid.hWnd, out pid); } catch { }
			}
			key = (pid != 0) ? ("p:" + pid + ":" + nid.uID) : ("h:" + nid.hWnd + ":" + nid.uID);
		}
		bool structural = false;
		lock (_gate)
		{
			switch (dwMessage)
			{
				case 2: // NIM_DELETE
					structural = _icons.Remove(key);
					break;
				case 0: // NIM_ADD
				case 1: // NIM_MODIFY
				{
					if (!_icons.TryGetValue(key, out Entry e))
					{
						e = new Entry { HWnd = nid.hWnd, UID = nid.uID, Key = key };
						_icons[key] = e;
						structural = true;
					}
					else if (nid.hWnd != 0 && e.HWnd != nid.hWnd)
					{
						e.HWnd = nid.hWnd;   // GUID icon re-registered under a new owner window — retarget click forwarding
						structural = true;
					}
					e.Seq = ++_seq;   // freshest entry for a (window,tooltip) wins when Snapshot collapses phantoms
					// Merge only the fields the sender actually supplied (uFlags), so a tooltip-only MODIFY doesn't
					// wipe the callback message or icon we captured at ADD time.
					if ((nid.uFlags & NIF_MESSAGE) != 0)
					{
						e.Callback = nid.uCallbackMessage;
					}
					if ((nid.uFlags & NIF_ICON) != 0 && nid.hIcon != 0)
					{
						e.HIcon = (nint)(long)nid.hIcon;
						structural = true;   // icon changed → let the taskbar re-render
					}
					if ((nid.uFlags & NIF_TIP) != 0)
					{
						e.Tip = (nid.szTip ?? "").Replace("\0", " ").Trim();
					}
					// Collapse phantoms immediately: any OTHER entry on the SAME window with the SAME tooltip is a stale
					// re-registration (Task Manager adds a new uID every second without deleting the old one). Dropping
					// them here keeps the table from churning up toward N between snapshots.
					List<string> phantoms = null;
					foreach (KeyValuePair<string, Entry> other in _icons)
					{
						if (!ReferenceEquals(other.Value, e) && other.Value.HWnd == e.HWnd && (other.Value.Tip ?? "") == (e.Tip ?? ""))
						{
							// Don't lose state the stale duplicate captured: a v4 version or a callback message may have landed
							// on it (e.g. a SETVERSION that resolved to a different key) — carry it to the entry we keep, so a
							// dropped phantom can't downgrade a real v4 icon to legacy or leave it with no callback.
							if (other.Value.Version > e.Version) { e.Version = other.Value.Version; }
							if (e.Callback == 0 && other.Value.Callback != 0) { e.Callback = other.Value.Callback; }
							(phantoms ??= new List<string>()).Add(other.Key);
						}
					}
					if (phantoms != null)
					{
						foreach (string pk in phantoms)
						{
							_icons.Remove(pk);
						}
						structural = true;
					}
					break;
				}
				case 4: // NIM_SETVERSION — capture the icon's NOTIFYICON version so ForwardClick can send the ONE
					// convention the app actually registered (v4 => NIN_SELECT; legacy => a mouse click). uVersion is
					// valid ONLY on NIM_SETVERSION (it shares the union slot with uTimeout on ADD/MODIFY).
					if (_icons.TryGetValue(key, out Entry ev))
					{
						ev.Version = nid.uVersion;
					}
					else
					{
						// The SETVERSION key can differ from the ADD key (an app that set NIF_GUID on one message but not the
						// other), which would leave a real v4 icon recorded as version 0 and mis-forwarded as legacy (= its
						// right-click menu never shows). Reconcile by (hWnd,uID) so the version lands on the right entry.
						foreach (Entry cand in _icons.Values)
						{
							if (nid.hWnd != 0 && cand.HWnd == nid.hWnd && cand.UID == nid.uID)
							{
								cand.Version = nid.uVersion;
								break;
							}
						}
					}
					return true;
				default:
					// NIM_SETFOCUS (3): no table change we need to surface.
					return true;
			}
		}
		SignalChanged();
		return true;
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

	private static void SignalChanged()
	{
		try
		{
			if (_coalesce == null)
			{
				_coalesce = new Timer(delegate
				{
					try { Changed?.Invoke(); } catch { }
				}, null, Timeout.Infinite, Timeout.Infinite);
			}
			_coalesce.Change(150, Timeout.Infinite);
		}
		catch
		{
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

	[DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint hWnd, out uint pid);
	[DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern nint GetModuleHandleW(string n);
	[DllImport("user32.dll")] private static extern int GetMessageW(out MSG msg, nint h, uint min, uint max);
	[DllImport("user32.dll")] private static extern bool TranslateMessage(ref MSG msg);
	[DllImport("user32.dll")] private static extern nint DispatchMessageW(ref MSG msg);
	[DllImport("user32.dll")] private static extern void PostQuitMessage(int code);
}
