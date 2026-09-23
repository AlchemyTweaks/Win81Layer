using System;
using System.Runtime.InteropServices;

namespace Win81Layer;

public static class AppBar
{
	private const int SW_HIDE = 0;

	private const int SW_SHOW = 5;

	public static void HideNativeTaskbar()
	{
		ForEachTaskbar(delegate(nint t)
		{
			ShowWindow(t, 0);
		});
	}

	public static bool ReassertHidden()
	{
		bool leaked = false;
		ForEachTaskbar(delegate(nint t)
		{
			if (IsWindowVisible(t))
			{
				ShowWindow(t, 0);
				leaked = true;
			}
		});
		if (leaked)
		{
			Logger.Log("[appbar] native taskbar leaked visible — re-hidden");
		}
		return leaked;
	}

	public static void ShowNativeTaskbar()
	{
		TaskbarWorkArea.Restore();
		ForEachTaskbar(delegate(nint t)
		{
			ShowWindow(t, 5);
		});
	}

	private static void ForEachTaskbar(Action<nint> action)
	{
		nint primary = PrimaryNativeTaskbar();
		if (primary != IntPtr.Zero)
		{
			action(primary);
		}
		nint sec = IntPtr.Zero;
		while ((sec = FindWindowEx(IntPtr.Zero, sec, "Shell_SecondaryTrayWnd", null)) != IntPtr.Zero)
		{
			action(sec);
		}
	}

	// EXPLORER's primary taskbar (class "Shell_TrayWnd"). We can't use a plain FindWindow("Shell_TrayWnd") anymore:
	// on Win11 the launcher hosts its OWN top-level "Shell_TrayWnd" (TrayHostService, the real tray host), and that
	// window is deliberately kept topmost so a naive FindWindow returns IT, not explorer's. Enumerate and skip any
	// window owned by this process so every "act on the native taskbar" path still targets explorer.
	public static nint PrimaryNativeTaskbar()
	{
		uint ownPid = (uint)Environment.ProcessId;
		GetWindowThreadProcessId(GetShellWindow(), out uint shellPid);
		nint h = IntPtr.Zero;
		while ((h = FindWindowEx(IntPtr.Zero, h, "Shell_TrayWnd", null)) != IntPtr.Zero)
		{
			GetWindowThreadProcessId(h, out uint pid);
			if (pid != ownPid && shellPid != 0 && pid == shellPid)
			{
				return h;
			}
		}
		return IntPtr.Zero;
	}

	[DllImport("user32.dll")]
	private static extern nint GetShellWindow();

	[DllImport("user32.dll")]
	private static extern uint GetWindowThreadProcessId(nint hWnd, out uint pid);

	[DllImport("user32.dll", CharSet = CharSet.Unicode)]
	private static extern nint FindWindow(string? cls, string? win);

	[DllImport("user32.dll", CharSet = CharSet.Unicode)]
	private static extern nint FindWindowEx(nint parent, nint after, string? cls, string? win);

	[DllImport("user32.dll")]
	private static extern bool ShowWindow(nint hWnd, int nCmdShow);

	[DllImport("user32.dll")]
	private static extern bool IsWindowVisible(nint hWnd);
}
