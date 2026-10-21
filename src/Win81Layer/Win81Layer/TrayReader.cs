using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace Win81Layer;

public static class TrayReader
{
	private struct POINT
	{
		public int X;

		public int Y;
	}

	private struct TBBUTTON
	{
		public int iBitmap;

		public int idCommand;

		public byte fsState;

		public byte fsStyle;

		public byte r0;

		public byte r1;

		public byte r2;

		public byte r3;

		public byte r4;

		public byte r5;

		public nint dwData;

		public nint iString;
	}

	private struct TRAYDATA
	{
		public nint hwnd;

		public uint uID;

		public uint uCallbackMessage;

		public uint Reserved0;

		public uint Reserved1;

		public nint hIcon;
	}

	private const uint WM_MOUSEMOVE = 512u;

	private const uint WM_LBUTTONDOWN = 513u;

	private const uint WM_LBUTTONUP = 514u;

	private const uint WM_RBUTTONDOWN = 516u;

	private const uint WM_RBUTTONUP = 517u;

	private const uint WM_CONTEXTMENU = 123u;

	private const uint NIN_SELECT = 1024u;

	private const uint SMTO_ABORTIFHUNG = 2u;

	private const uint SMTO_BLOCK = 1u;

	private const uint ASFW_ANY = uint.MaxValue;

	private const uint PROCESS_VM_OPERATION = 8u;

	private const uint PROCESS_VM_READ = 16u;

	private const uint PROCESS_VM_WRITE = 32u;

	private const uint PROCESS_QUERY_INFORMATION = 1024u;

	private const uint MEM_COMMIT = 4096u;

	private const uint MEM_RESERVE = 8192u;

	private const uint MEM_RELEASE = 32768u;

	private const uint PAGE_READWRITE = 4u;

	private const uint TB_BUTTONCOUNT = 1048u;

	private const uint TB_GETBUTTON = 1047u;

	private const uint TB_GETBUTTONTEXTW = 1099u;

	private const byte TBSTATE_HIDDEN = 8;

	[DllImport("user32.dll", CharSet = CharSet.Unicode)]
	private static extern nint FindWindow(string? cls, string? title);

	[DllImport("user32.dll", CharSet = CharSet.Unicode)]
	private static extern nint FindWindowEx(nint parent, nint after, string? cls, string? title);

	[DllImport("user32.dll")]
	private static extern uint GetWindowThreadProcessId(nint hWnd, out uint pid);

	[DllImport("user32.dll", CharSet = CharSet.Unicode)]
	private static extern nint SendMessage(nint hWnd, uint msg, nint wParam, nint lParam);

	[DllImport("user32.dll", CharSet = CharSet.Unicode)]
	private static extern nint SendMessageTimeout(nint hWnd, uint msg, nint wParam, nint lParam, uint flags, uint timeout, out nint result);

	[DllImport("user32.dll")]
	private static extern bool PostMessage(nint hWnd, uint msg, nint wParam, nint lParam);

	[DllImport("user32.dll")]
	private static extern bool SendNotifyMessage(nint hWnd, uint msg, nint wParam, nint lParam);

	[DllImport("user32.dll")]
	private static extern bool AllowSetForegroundWindow(uint pid);

	[DllImport("user32.dll")]
	private static extern bool IsWindow(nint hWnd);

	[DllImport("user32.dll")]
	private static extern bool GetCursorPos(out POINT pt);

	[DllImport("kernel32.dll")]
	private static extern uint GetCurrentThreadId();

	private static nint Query(nint hWnd, uint msg, nint wParam, nint lParam)
	{
		SendMessageTimeout(hWnd, msg, wParam, lParam, 3u, 300u, out var result);
		return result;
	}

	public static uint ShellProcessId()
	{
		// Explorer's taskbar, NOT the launcher's own Shell_TrayWnd tray host (Win11) — otherwise icon filtering would
		// treat our own process as "the shell" and hide the launcher's own tray icon.
		return ProcessIdOf(AppBar.PrimaryNativeTaskbar());
	}

	public static uint ProcessIdOf(nint hwnd)
	{
		if (hwnd == IntPtr.Zero)
		{
			return 0u;
		}
		GetWindowThreadProcessId(hwnd, out var pid);
		return pid;
	}

	public static string ProcessNameOf(nint hwnd)
	{
		try
		{
			uint pid = ProcessIdOf(hwnd);
			if (pid == 0)
			{
				return "";
			}
			using Process p = Process.GetProcessById((int)pid);
			return p.ProcessName ?? "";
		}
		catch
		{
			return "";
		}
	}

	// Set by --traytest: deliver without granting the foreground (its targets are test windows in this process).
	internal static bool DiagnosticsNoForeground;

	public static (int X, int Y) CursorPosition()
	{
		GetCursorPos(out var pt);
		return (pt.X, pt.Y);
	}

	// Sends one gesture to an icon's callback exactly as Explorer does for the icon's version (TrayProtocol), and
	// nothing else. Like Explorer, it only passes on the right to set the foreground (AllowSetForegroundWindow for the
	// owner's process, which we hold because the user just clicked our window) and lets the app call
	// SetForegroundWindow itself before it opens its menu. It never activates the owner (often an invisible window, and
	// toggle-style apps then see themselves as already in front and hide instead of show), never grants the right to
	// every process, and never attaches to another thread's input. Delivery never blocks: SendNotifyMessage returns at
	// once for a window of another thread (and is processed even inside the app's own modal loops, as Explorer's is);
	// a window of this thread gets PostMessage so its handler cannot run inside our click handler.
	// versionKnown: false when the icon came from the legacy toolbar source, which reads no version
	// (TrayProtocol.EventsUnknownVersion). Returns the number of messages sent.
	internal static int Forward(nint ownerHwnd, uint callbackMessage, uint id, uint version, TrayEvent ev, int x, int y, bool versionKnown = true)
	{
		if (callbackMessage == 0 || ownerHwnd == IntPtr.Zero || !IsWindow(ownerHwnd))
		{
			return 0;
		}
		List<TrayMessage> msgs = versionKnown
			? TrayProtocol.Build(version, id, ev, x, y)
			: TrayProtocol.BuildUnknownVersion(id, ev, TrayHostService.IsChromiumFamilyWindow(ownerHwnd));
		uint tid = GetWindowThreadProcessId(ownerHwnd, out var pid);
		if (ev != TrayEvent.Hover && pid != 0 && pid != (uint)Environment.ProcessId && !DiagnosticsNoForeground)
		{
			AllowSetForegroundWindow(pid);
		}
		bool sameThread = tid == GetCurrentThreadId();
		int sent = 0;
		foreach (TrayMessage m in msgs)
		{
			bool ok = sameThread
				? PostMessage(ownerHwnd, callbackMessage, m.WParam, m.LParam)
				: SendNotifyMessage(ownerHwnd, callbackMessage, m.WParam, m.LParam);
			if (ok)
			{
				sent++;
			}
		}
		return sent;
	}

	internal enum Delivery
	{
		// Sent with the icon's own callback message.
		Sent,
		// The icon had no callback; sent with the framework's known callback (Electron / Chromium) and repair requested.
		Guessed,
		// The icon had no callback; the owner was asked to re-register. The caller may retry once the icon is back.
		Recovering,
		// Nothing to do (hover without a callback, owner gone).
		Dropped
	}

	private static readonly Dictionary<nint, long> NoCallbackLogged = new Dictionary<nint, long>();

	// A gesture for an icon that may have lost its callback message (its NIM_ADD never reached us). Instead of a silent
	// no-op: ask the owner to re-register (a targeted TaskbarCreated, which every common framework answers by adding its
	// icons again with NIF_MESSAGE), and for the two frameworks whose callback is a fixed constant, deliver this click
	// with it so the click still works now.
	internal static Delivery Deliver(nint ownerHwnd, uint callbackMessage, uint id, uint version, TrayEvent ev, int x, int y, bool versionKnown = true)
	{
		if (callbackMessage != 0)
		{
			Forward(ownerHwnd, callbackMessage, id, version, ev, x, y, versionKnown);
			return Delivery.Sent;
		}
		if (ev == TrayEvent.Hover || ownerHwnd == IntPtr.Zero || !IsWindow(ownerHwnd))
		{
			return Delivery.Dropped;
		}
		bool nudged = TrayHostService.RequestNudge(ownerHwnd, TrayHostService.NudgeReason.Click);
		uint guess = TrayHostService.GuessCallback(ownerHwnd, out string cls);
		long now = Environment.TickCount64;
		bool log;
		lock (NoCallbackLogged)
		{
			log = !NoCallbackLogged.TryGetValue(ownerHwnd, out long last) || now - last > 60000;
			if (log)
			{
				NoCallbackLogged[ownerHwnd] = now;
			}
		}
		if (guess != 0)
		{
			// The framework's icons are version 0 (it never sends NIM_SETVERSION).
			Forward(ownerHwnd, guess, id, 0u, ev, x, y);
			if (log)
			{
				Logger.Log($"[tray] icon without callback (owner=0x{(long)ownerHwnd:X} class={cls} id={id}): sent {ev} with the {cls} callback 0x{guess:X}; re-registration requested={nudged}");
			}
			return Delivery.Guessed;
		}
		if (log)
		{
			Logger.Log($"[tray] icon without callback (owner=0x{(long)ownerHwnd:X} class={cls} id={id}): {ev} held until it re-registers; re-registration requested={nudged}");
		}
		return Delivery.Recovering;
	}

	[DllImport("kernel32.dll")]
	private static extern nint OpenProcess(uint access, bool inherit, uint pid);

	[DllImport("kernel32.dll")]
	private static extern bool CloseHandle(nint h);

	[DllImport("kernel32.dll")]
	private static extern nint VirtualAllocEx(nint h, nint addr, nint size, uint type, uint protect);

	[DllImport("kernel32.dll")]
	private static extern bool VirtualFreeEx(nint h, nint addr, nint size, uint type);

	[DllImport("kernel32.dll")]
	private static extern bool ReadProcessMemory(nint h, nint baseAddr, byte[] buffer, nint size, out nint read);

	private static nint FindToolbar(bool overflow)
	{
		if (overflow)
		{
			nint of = FindWindow("NotifyIconOverflowWindow", null);
			return (of == IntPtr.Zero) ? IntPtr.Zero : FindWindowEx(of, IntPtr.Zero, "ToolbarWindow32", null);
		}
		nint tray = FindWindow("Shell_TrayWnd", null);
		if (tray == IntPtr.Zero)
		{
			return IntPtr.Zero;
		}
		nint notify = FindWindowEx(tray, IntPtr.Zero, "TrayNotifyWnd", null);
		if (notify == IntPtr.Zero)
		{
			return IntPtr.Zero;
		}
		nint pager = FindWindowEx(notify, IntPtr.Zero, "SysPager", null);
		if (pager == IntPtr.Zero)
		{
			return IntPtr.Zero;
		}
		return FindWindowEx(pager, IntPtr.Zero, "ToolbarWindow32", null);
	}

	public static List<TrayIconInfo> Enumerate()
	{
		List<TrayIconInfo> result = new List<TrayIconInfo>();
		HashSet<string> seen = new HashSet<string>();
		ReadToolbar(FindToolbar(overflow: false), overflow: false, result, seen);
		ReadToolbar(FindToolbar(overflow: true), overflow: true, result, seen);
		return result;
	}

	private static void ReadToolbar(nint toolbar, bool overflow, List<TrayIconInfo> outList, HashSet<string> seen)
	{
		if (toolbar == IntPtr.Zero)
		{
			return;
		}
		GetWindowThreadProcessId(toolbar, out var pid);
		if (pid == 0)
		{
			return;
		}
		nint hProc = OpenProcess(1080u, inherit: false, pid);
		if (hProc == IntPtr.Zero)
		{
			return;
		}
		nint remote = VirtualAllocEx(hProc, IntPtr.Zero, 4096, 12288u, 4u);
		if (remote == IntPtr.Zero)
		{
			CloseHandle(hProc);
			return;
		}
		try
		{
			int count = (int)Query(toolbar, 1048u, IntPtr.Zero, IntPtr.Zero);
			if (count <= 0)
			{
				return;
			}
			count = Math.Min(count, 64);
			int tbSize = Marshal.SizeOf<TBBUTTON>();
			int tdSize = Marshal.SizeOf<TRAYDATA>();
			for (int i = 0; i < count; i++)
			{
				if (Query(toolbar, 1047u, i, remote) == IntPtr.Zero || !ReadRemote(hProc, remote, tbSize, out byte[] tbBuf))
				{
					continue;
				}
				TBBUTTON tb = BytesToStruct<TBBUTTON>(tbBuf);
				if (tb.dwData != IntPtr.Zero && (overflow || (tb.fsState & 8) == 0) && ReadRemote(hProc, tb.dwData, tdSize, out byte[] tdBuf))
				{
					TRAYDATA td = BytesToStruct<TRAYDATA>(tdBuf);
					if (td.hwnd != IntPtr.Zero && IsWindow(td.hwnd) && td.hIcon != IntPtr.Zero && seen.Add($"{((IntPtr)td.hwnd).ToInt64():X}:{td.uID}"))
					{
						outList.Add(new TrayIconInfo
						{
							OwnerHwnd = td.hwnd,
							Id = td.uID,
							CallbackMessage = td.uCallbackMessage,
							HIcon = td.hIcon,
							Hidden = false,
							FromOverflow = overflow,
							Tooltip = ReadTooltip(toolbar, hProc, remote, tb.idCommand)
						});
					}
				}
			}
		}
		finally
		{
			VirtualFreeEx(hProc, remote, IntPtr.Zero, 32768u);
			CloseHandle(hProc);
		}
	}

	private static string ReadTooltip(nint toolbar, nint hProc, nint remote, int idCommand)
	{
		int len = (int)Query(toolbar, 1099u, idCommand, remote);
		if (len <= 0)
		{
			return "";
		}
		int bytes = Math.Min(len * 2, 4094);
		if (!ReadRemote(hProc, remote, bytes, out byte[] buf))
		{
			return "";
		}
		return Encoding.Unicode.GetString(buf, 0, bytes).TrimEnd('\0');
	}

	private static bool ReadRemote(nint hProc, nint addr, int size, out byte[] buf)
	{
		buf = new byte[size];
		nint read;
		return ReadProcessMemory(hProc, addr, buf, size, out read);
	}

	private static T BytesToStruct<T>(byte[] bytes) where T : struct
	{
		GCHandle handle = GCHandle.Alloc(bytes, GCHandleType.Pinned);
		try
		{
			return Marshal.PtrToStructure<T>(handle.AddrOfPinnedObject());
		}
		finally
		{
			handle.Free();
		}
	}
}
