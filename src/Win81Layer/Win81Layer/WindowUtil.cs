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
}
