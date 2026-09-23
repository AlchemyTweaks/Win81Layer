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

	public static void ForceForeground(Window window)
	{
		nint hwnd = new WindowInteropHelper(window).EnsureHandle();
		if (hwnd == IntPtr.Zero)
		{
			window.Activate();
			return;
		}
		uint thisThread = GetCurrentThreadId();
		uint fgThread = GetWindowThreadProcessId(GetForegroundWindow(), out var _);
		bool attached = false;
		try
		{
			if (fgThread != 0 && fgThread != thisThread)
			{
				attached = AttachThreadInput(fgThread, thisThread, fAttach: true);
			}
			BringWindowToTop(hwnd);
			SetForegroundWindow(hwnd);
			SetActiveWindow(hwnd);
			SetFocus(hwnd);
		}
		finally
		{
			if (attached)
			{
				AttachThreadInput(fgThread, thisThread, fAttach: false);
			}
		}
		window.Activate();
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
	private static extern uint GetWindowThreadProcessId(nint hWnd, out uint pid);

	[DllImport("kernel32.dll")]
	private static extern uint GetCurrentThreadId();

	[DllImport("user32.dll")]
	private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);

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
}
