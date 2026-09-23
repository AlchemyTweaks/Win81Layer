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
	private static extern bool SetForegroundWindow(nint hWnd);

	[DllImport("user32.dll")]
	private static extern bool AllowSetForegroundWindow(uint pid);

	[DllImport("user32.dll")]
	private static extern bool IsWindow(nint hWnd);

	[DllImport("user32.dll")]
	private static extern bool GetCursorPos(out POINT pt);

	[DllImport("user32.dll")]
	private static extern nint GetForegroundWindow();

	[DllImport("kernel32.dll")]
	private static extern uint GetCurrentThreadId();

	[DllImport("user32.dll")]
	private static extern bool AttachThreadInput(uint a, uint b, bool attach);

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

	public static void ForwardClick(nint ownerHwnd, uint callbackMessage, uint id, bool right, uint version = 0, bool dbl = false)
	{
		if (!IsWindow(ownerHwnd))
		{
			return;
		}
		if (callbackMessage == 0)
		{
			// The icon never registered NIF_MESSAGE (or the host lost it): PostMessage(hwnd, 0, ...) is WM_NULL — a silent
			// no-op, which is exactly the "right-click does nothing" symptom. Log it instead of failing invisibly.
			Logger.Log($"[tray] ForwardClick skipped: no callback message (owner=0x{ownerHwnd.ToInt64():X} id={id})");
			return;
		}
		// Bring the owner to the foreground first so the app is allowed to show its own window / menu.
		AllowSetForegroundWindow(uint.MaxValue);
		uint thisThread = GetCurrentThreadId();
		uint fgThread = GetWindowThreadProcessId(GetForegroundWindow(), out var _);
		bool att = fgThread != 0 && fgThread != thisThread && AttachThreadInput(fgThread, thisThread, attach: true);
		SetForegroundWindow(ownerHwnd);
		if (att)
		{
			AttachThreadInput(fgThread, thisThread, attach: false);
		}
		GetCursorPos(out var pt);
		// CRITICAL: send exactly ONE convention, chosen by the version the icon registered. Real Explorer never mixes
		// the two. Posting BOTH a legacy WM_LBUTTONUP and a v4 NIN_SELECT makes apps that keep both handlers wired to
		// the same "toggle window" action (MemReduct and other routine.dll / classic tray apps) toggle TWICE — the
		// window shows then immediately hides — so the click appears to do nothing.
		if (version >= 4u)
		{
			// NOTIFYICON_VERSION_4: wParam = anchor (x,y), lParam = MAKELONG(event, uID). A single activation message;
			// v4 has no separate double-click — a double-click still activates once.
			uint evt4 = (right ? 123u /*WM_CONTEXTMENU*/ : 1024u /*NIN_SELECT*/);
			PostMessage(ownerHwnd, callbackMessage, MakeLong(pt.X, pt.Y), MakeLong((int)evt4, (int)id));
		}
		else
		{
			// Legacy (< v4): wParam = uID, lParam = the raw mouse message. A single button sequence, no NIN_SELECT.
			uint down = (right ? 516u : 513u);   // WM_?BUTTONDOWN
			uint up = (right ? 517u : 514u);     // WM_?BUTTONUP
			PostMessage(ownerHwnd, callbackMessage, (nint)id, 512);        // WM_MOUSEMOVE
			PostMessage(ownerHwnd, callbackMessage, (nint)id, (nint)down);
			if (dbl && !right)
			{
				PostMessage(ownerHwnd, callbackMessage, (nint)id, 515);    // WM_LBUTTONDBLCLK (some legacy apps open only on this)
			}
			PostMessage(ownerHwnd, callbackMessage, (nint)id, (nint)up);
			if (right)
			{
				// Many icons show their context menu ONLY on WM_CONTEXTMENU, never on WM_RBUTTONUP: NOTIFYICON_VERSION 3
				// apps, and any v4 icon whose version we failed to capture (routed here as legacy). Post it too, RIGHT-CLICK
				// ONLY, so the menu actually appears — an app that already opened its menu on RBUTTONUP just ignores the
				// duplicate. Never do this for LEFT-click: a second message double-toggles window-toggling apps (MemReduct).
				PostMessage(ownerHwnd, callbackMessage, (nint)id, (nint)WM_CONTEXTMENU);
			}
		}
	}

	private static nint MakeLong(int lo, int hi)
	{
		return (hi << 16) | (lo & 0xFFFF);
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
