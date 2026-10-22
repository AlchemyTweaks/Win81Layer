using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace Win81Layer;

internal static class WindowUtil
{
	private const byte VK_MENU = 18;

	private const uint KEYEVENTF_KEYUP = 2u;

	private const byte VK_LWIN = 91;

	private const byte VK_TAB = 9;

	// Bumped by every ForceForeground: a foreground helper that finishes late acts only for the latest request.
	private static int _foregroundRequest;

	// Brings one of our windows to the foreground (Start, Action Center, charms, panes, the lock screen). Never attaches
	// the calling (UI) thread to another app's input queue: with the queues attached the old foreground window must
	// answer the activation hand-over synchronously, so a busy or hung app froze the whole shell.
	//   1. A plain SetForegroundWindow, which works whenever this process received the last input or already owns the
	//      foreground, and never waits on another app.
	//   2. Only if that was refused: ForegroundBroker attaches a helper thread (not this one) to the foreground thread,
	//      after checking that the foreground window answers, and this thread waits for it for at most ~150 ms. A busy
	//      app may answer later: the helper then still hands the foreground over, but only for the latest request and
	//      only while the window is still shown (never for a surface that was closed or replaced meanwhile).
	//   3. The same-thread calls (raise, activate, focus) run here once no queue is attached.
	public static void ForceForeground(Window window)
	{
		nint hwnd = new WindowInteropHelper(window).EnsureHandle();
		if (hwnd == IntPtr.Zero)
		{
			window.Activate();
			return;
		}
		int request = System.Threading.Interlocked.Increment(ref _foregroundRequest);
		using (Breadcrumbs.Enter("ForceForeground", hwnd))
		{
			uint thisThread = GetCurrentThreadId();
			SetForegroundWindow(hwnd);
			nint fg = GetForegroundWindow();
			if (fg != hwnd && fg != IntPtr.Zero)
			{
				uint fgThread = GetWindowThreadProcessId(fg, out var _);
				if (fgThread != 0 && fgThread != thisThread)
				{
					Func<bool> current = () => System.Threading.Volatile.Read(ref _foregroundRequest) == request && IsWindowVisible(hwnd);
					ForegroundBroker.Run(fgThread, fg, delegate
					{
						if (current())
						{
							SetForegroundWindow(hwnd);
						}
					}, ForegroundBroker.TimeoutMs, current);
					if (GetForegroundWindow() != hwnd)
					{
						Logger.Log($"[foreground] could not take the foreground for 0x{(long)hwnd:X}: {ForegroundBroker.LastOutcome}; fg={Breadcrumbs.Describe(fg)}");
					}
				}
			}
			BringWindowToTop(hwnd);
			SetForegroundWindow(hwnd);
			SetActiveWindow(hwnd);
			SetFocus(hwnd);
		}
		window.Activate();
	}

	// The message time of the input currently being dispatched (GetTickCount clock). Read inside a synchronous input
	// handler, it is when the press happened, not when a later dispatcher hop runs.
	internal static int MessageTime()
	{
		return GetMessageTime();
	}

	// "WindowClass/process" of the current foreground window, for show/hide log lines.
	internal static string ForegroundDescription()
	{
		try
		{
			nint fg = GetForegroundWindow();
			if (fg == IntPtr.Zero)
			{
				return "none";
			}
			System.Text.StringBuilder cls = new System.Text.StringBuilder(64);
			GetClassName(fg, cls, cls.Capacity);
			GetWindowThreadProcessId(fg, out uint pid);
			if (pid == (uint)Environment.ProcessId)
			{
				return cls + "/self";
			}
			using Process proc = Process.GetProcessById((int)pid);
			return cls + "/" + proc.ProcessName;
		}
		catch
		{
			return "?";
		}
	}

	public static void OpenTaskView()
	{
		keybd_event(91, 0, 0u, UIntPtr.Zero);
		keybd_event(9, 0, 0u, UIntPtr.Zero);
		keybd_event(9, 0, 2u, UIntPtr.Zero);
		keybd_event(91, 0, 2u, UIntPtr.Zero);
	}

	public static void TrimMemory()
	{
		try
		{
			GC.Collect(GC.MaxGeneration, GCCollectionMode.Optimized, blocking: false, compacting: true);
			using Process proc = Process.GetCurrentProcess();
			EmptyWorkingSet(proc.Handle);
		}
		catch (Exception ex)
		{
			Logger.Log("TrimMemory failed: " + ex.Message);
		}
	}

	[DllImport("user32.dll")]
	private static extern bool SetForegroundWindow(nint hWnd);

	[DllImport("user32.dll")]
	private static extern nint GetForegroundWindow();

	[DllImport("user32.dll")]
	private static extern bool IsWindowVisible(nint hWnd);

	[DllImport("user32.dll")]
	private static extern uint GetWindowThreadProcessId(nint hWnd, out uint pid);

	[DllImport("kernel32.dll")]
	private static extern uint GetCurrentThreadId();

	[DllImport("user32.dll")]
	private static extern bool BringWindowToTop(nint hWnd);

	[DllImport("user32.dll")]
	private static extern nint SetActiveWindow(nint hWnd);

	[DllImport("user32.dll")]
	private static extern nint SetFocus(nint hWnd);

	[DllImport("user32.dll")]
	private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, nuint dwExtraInfo);

	[DllImport("psapi.dll")]
	private static extern bool EmptyWorkingSet(nint hProcess);

	[DllImport("user32.dll")]
	private static extern int GetMessageTime();

	[DllImport("user32.dll", CharSet = CharSet.Unicode)]
	private static extern int GetClassName(nint hWnd, System.Text.StringBuilder className, int maxCount);

	// True when the foreground window's input is hidden from our hooks (see WindowInputHiddenFromHooks), or when that
	// cannot be told: no foreground window (the secure desktop or the lock screen has the input).
	internal static bool ForegroundIsElevatedOrUnknown()
	{
		nint fg = GetForegroundWindow();
		return fg == IntPtr.Zero || WindowInputHiddenFromHooks(fg);
	}

	// The foreground window's input is hidden from our hooks; no foreground window is not counted.
	internal static bool ForegroundInputHiddenFromHooks()
	{
		nint fg = GetForegroundWindow();
		return fg != IntPtr.Zero && WindowInputHiddenFromHooks(fg);
	}

	// The top-level window under the given screen point (mouse input there goes to it) is one the hooks cannot see.
	internal static bool WindowAtInputHiddenFromHooks(int x, int y)
	{
		nint w = WindowFromPoint(new POINTSTRUCT { X = x, Y = y });
		if (w == IntPtr.Zero)
		{
			return false;
		}
		nint root = GetAncestor(w, 2u);   // GA_ROOT: one cache entry per top-level window, not one per child control
		return WindowInputHiddenFromHooks(root != IntPtr.Zero ? root : w);
	}

	internal static bool TryGetCursorPos(out int x, out int y)
	{
		if (GetCursorPos(out POINTSTRUCT p))
		{
			x = p.X;
			y = p.Y;
			return true;
		}
		x = 0;
		y = 0;
		return false;
	}

	// Input aimed at this window never reaches a low-level hook of this process: User Interface Privilege Isolation keeps
	// the hooks of a process from input aimed at a process of a higher integrity level (an elevated app such as Task
	// Manager or an admin terminal; a UIAccess or SYSTEM window). A process or token this shell may not open counts the
	// same. Nothing is hidden from a shell that itself runs at that level. The answers for the last 8 (window, process id)
	// pairs are kept: a process's integrity level never changes and a window belongs to one process for its whole life,
	// so the steady state is a window-to-process lookup only. UI thread.
	internal static bool WindowInputHiddenFromHooks(nint hwnd)
	{
		try
		{
			GetWindowThreadProcessId(hwnd, out uint pid);
			if (pid == 0u)
			{
				return true;
			}
			if (pid == (uint)Environment.ProcessId)
			{
				return false;
			}
			lock (LevelGate)
			{
				for (int i = 0; i < LevelHwnd.Length; i++)
				{
					if (LevelHwnd[i] == hwnd && LevelPid[i] == pid)
					{
						return LevelHidden[i];
					}
				}
			}
			int level = ProcessIntegrity(pid);
			bool hidden = level < 0 || level > OwnIntegrity;
			lock (LevelGate)
			{
				int slot = _levelNext;
				_levelNext = (slot + 1) % LevelHwnd.Length;
				LevelHwnd[slot] = hwnd;
				LevelPid[slot] = pid;
				LevelHidden[slot] = hidden;
			}
			return hidden;
		}
		catch
		{
			return true;
		}
	}

	private static readonly object LevelGate = new object();

	private static readonly nint[] LevelHwnd = new nint[8];

	private static readonly uint[] LevelPid = new uint[8];

	private static readonly bool[] LevelHidden = new bool[8];

	private static int _levelNext;

	private static int _ownIntegrity = -1;

	// This process's mandatory integrity level (0x2000 medium; 0x3000 when the shell runs elevated).
	private static int OwnIntegrity
	{
		get
		{
			int v = _ownIntegrity;
			if (v < 0)
			{
				v = TokenIntegrity(GetCurrentProcess());
				if (v < 0)
				{
					v = 0x2000;   // unreadable: assume the usual medium level
				}
				_ownIntegrity = v;
			}
			return v;
		}
	}

	// The mandatory integrity level of a process, or -1 when it cannot be read.
	private static int ProcessIntegrity(uint pid)
	{
		nint h = OpenProcess(0x1000u, false, pid);   // PROCESS_QUERY_LIMITED_INFORMATION
		if (h == IntPtr.Zero)
		{
			return -1;
		}
		try
		{
			return TokenIntegrity(h);
		}
		finally
		{
			CloseHandleUtil(h);
		}
	}

	// TokenIntegrityLevel: a TOKEN_MANDATORY_LABEL whose SID's last sub-authority is the level.
	private static int TokenIntegrity(nint process)
	{
		if (!OpenProcessToken(process, 0x0008u, out nint token))   // TOKEN_QUERY
		{
			return -1;
		}
		nint buf = Marshal.AllocHGlobal(128);
		try
		{
			if (!GetTokenInformationBuffer(token, 25, buf, 128, out _))
			{
				return -1;
			}
			nint sid = Marshal.ReadIntPtr(buf);
			if (sid == IntPtr.Zero)
			{
				return -1;
			}
			int count = Marshal.ReadByte(GetSidSubAuthorityCount(sid));
			if (count < 1)
			{
				return -1;
			}
			return Marshal.ReadInt32(GetSidSubAuthority(sid, (uint)(count - 1)));
		}
		finally
		{
			Marshal.FreeHGlobal(buf);
			CloseHandleUtil(token);
		}
	}

	// Remote-desktop, remote-control and virtual-machine clients capture the keyboard and mouse with their own low-level
	// hooks while they are in front (fullscreen mstsc, a VM with grabbed input): what they swallow never reaches ours, by
	// design, and re-installing ours would put it in front of theirs.
	private static readonly string[] InputCaptureClients = { "mstsc.exe", "msrdc.exe", "vmconnect.exe", "vmware.exe", "vmware-vmx.exe", "vmplayer.exe", "virtualboxvm.exe", "vncviewer.exe", "parsecd.exe", "teamviewer.exe", "anydesk.exe", "rustdesk.exe" };

	internal static bool ForegroundIsInputCaptureClient()
	{
		try
		{
			nint fg = GetForegroundWindow();
			if (fg == IntPtr.Zero)
			{
				return false;
			}
			GetWindowThreadProcessId(fg, out uint pid);
			if (pid == 0u)
			{
				return false;
			}
			nint h = OpenProcess(0x1000u, false, pid);
			if (h == IntPtr.Zero)
			{
				return false;
			}
			try
			{
				System.Text.StringBuilder sb = new System.Text.StringBuilder(1024);
				int len = sb.Capacity;
				if (!QueryFullProcessImageName(h, 0, sb, ref len))
				{
					return false;
				}
				string name = System.IO.Path.GetFileName(sb.ToString());
				return Array.Exists(InputCaptureClients, n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase));
			}
			finally
			{
				CloseHandleUtil(h);
			}
		}
		catch
		{
			return false;
		}
	}

	[StructLayout(LayoutKind.Sequential)]
	private struct POINTSTRUCT
	{
		public int X;
		public int Y;
	}

	[DllImport("user32.dll")]
	private static extern bool GetCursorPos(out POINTSTRUCT p);

	[DllImport("user32.dll")]
	private static extern nint WindowFromPoint(POINTSTRUCT p);

	[DllImport("kernel32.dll")]
	private static extern nint GetCurrentProcess();

	[DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
	private static extern bool QueryFullProcessImageName(nint process, int flags, System.Text.StringBuilder name, ref int size);

	[DllImport("kernel32.dll", EntryPoint = "OpenProcess")]
	private static extern nint OpenProcess(uint access, bool inherit, uint pid);

	[DllImport("kernel32.dll", EntryPoint = "CloseHandle")]
	private static extern bool CloseHandleUtil(nint h);

	[DllImport("advapi32.dll", SetLastError = true)]
	private static extern bool OpenProcessToken(nint process, uint access, out nint token);

	[DllImport("advapi32.dll", SetLastError = true, EntryPoint = "GetTokenInformation")]
	private static extern bool GetTokenInformationBuffer(nint token, int infoClass, nint info, int length, out int returned);

	[DllImport("advapi32.dll")]
	private static extern nint GetSidSubAuthorityCount(nint sid);

	[DllImport("advapi32.dll")]
	private static extern nint GetSidSubAuthority(nint sid, uint index);

	[DllImport("user32.dll")]
	private static extern nint GetAncestor(nint hwnd, uint flags);
}
