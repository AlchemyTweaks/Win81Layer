using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Win81Layer;

public static class WindowList
{
	private delegate bool EnumProc(nint hwnd, nint l);

	private const uint SMTO_ABORTIFHUNG = 2u;

	private const uint SMTO_BLOCK = 1u;

	private const uint PROCESS_QUERY_LIMITED_INFORMATION = 4096u;

	private const int GWL_EXSTYLE = -20;

	private const int WS_EX_TOOLWINDOW = 128;

	private const uint GA_ROOTOWNER = 3u;

	private const int DWMWA_CLOAKED = 14;

	private const int SW_MINIMIZE = 6;

	private const int SW_RESTORE = 9;

	private const int SW_MAXIMIZE = 3;

	private const int WM_GETICON = 127;

	private const int ICON_SMALL2 = 2;

	private const int ICON_BIG = 1;

	private const int GCLP_HICON = -14;

	private const int WM_CLOSE = 16;

	public static List<(nint Hwnd, string Title)> Enumerate(nint ownHwnd, nint includeOwnHwnd = default)
	{
		List<(nint, string)> result = new List<(nint, string)>();
		nint shell = GetShellWindow();
		uint ownPid = (uint)System.Environment.ProcessId;
		EnumWindows(delegate(nint hwnd, nint _)
		{
			if (hwnd == ownHwnd || hwnd == shell)
			{
				return true;
			}
			// Never list the launcher's OWN windows (Start screen, taskbars, charms/search/settings panels, etc.) as
			// task buttons — the Start overlay was showing up as a "Start" taskbar item with a close (X) button.
			GetWindowThreadProcessId(hwnd, out var wpid);
			if (wpid == ownPid && hwnd != includeOwnHwnd && !Win81Window.IsTaskable(hwnd))
			{
				return true;   // skip our own overlays (Start/taskbars/panels); keep real Win81Window app windows
			}
			if (!IsAltTabWindow(hwnd))
			{
				return true;
			}
			StringBuilder stringBuilder = new StringBuilder(256);
			GetWindowText(hwnd, stringBuilder, stringBuilder.Capacity);
			string text = stringBuilder.ToString();
			if (string.IsNullOrWhiteSpace(text))
			{
				return true;
			}
			result.Add((hwnd, text));
			return true;
		}, IntPtr.Zero);
		return result;
	}

	private static bool IsAltTabWindow(nint hwnd)
	{
		if (!IsWindowVisible(hwnd))
		{
			return false;
		}
		if (DwmGetWindowAttribute(hwnd, 14, out var cloaked, 4) == 0 && cloaked != 0)
		{
			return false;
		}
		int ex = GetWindowLong(hwnd, -20);
		if ((ex & 0x80) != 0)
		{
			return false;
		}
		nint root = GetAncestor(hwnd, 3u);
		return LastVisiblePopup(root) == hwnd;
	}

	private static nint LastVisiblePopup(nint root)
	{
		nint hwnd = root;
		nint next;
		while (true)
		{
			next = GetLastActivePopup(hwnd);
			if (next == hwnd || IsWindowVisible(next))
			{
				break;
			}
			hwnd = next;
		}
		return next;
	}

	public static void ActivateOrMinimize(nint hwnd)
	{
		if (GetForegroundWindow() == hwnd)
		{
			ShowWindow(hwnd, 6);
		}
		else
		{
			Activate(hwnd);
		}
	}

	public static void Activate(nint hwnd)
	{
		TryActivate(hwnd);
	}

	public static bool IsWindowAlive(nint hwnd)
	{
		return hwnd != IntPtr.Zero && IsWindow(hwnd);
	}

	public static bool TryActivate(nint hwnd)
	{
		if (!IsWindowAlive(hwnd))
		{
			return false;
		}
		if (IsIconic(hwnd))
		{
			ShowWindow(hwnd, 9);
		}
		ShellLaunch.AllowForeground();
		uint thisThread = GetCurrentThreadId();
		uint fg = GetWindowThreadProcessId(GetForegroundWindow(), out var _);
		bool att = fg != 0 && fg != thisThread && AttachThreadInput(fg, thisThread, f: true);
		BringWindowToTop(hwnd);
		bool foregrounded = SetForegroundWindow(hwnd);
		if (att)
		{
			AttachThreadInput(fg, thisThread, f: false);
		}
		return foregrounded || GetForegroundWindow() == hwnd;
	}

	public static bool IsForeground(nint hwnd)
	{
		return hwnd != IntPtr.Zero && GetForegroundWindow() == hwnd;
	}

	public static void Minimize(nint hwnd)
	{
		ShowWindow(hwnd, 6);
	}

	public static bool IsMaximized(nint hwnd)
	{
		return IsZoomed(hwnd);
	}

	public static void ToggleMaximize(nint hwnd)
	{
		ShowWindow(hwnd, IsZoomed(hwnd) ? 9 : 3);
	}

	public static void NewWindow(nint hwnd)
	{
		string exe = GetExePath(hwnd);
		if (string.IsNullOrEmpty(exe))
		{
			return;
		}
		try
		{
			Process.Start(new ProcessStartInfo(exe)
			{
				UseShellExecute = true
			});
		}
		catch
		{
		}
	}

	public static string GetTitle(nint hwnd)
	{
		StringBuilder sb = new StringBuilder(256);
		GetWindowText(hwnd, sb, sb.Capacity);
		return sb.ToString();
	}

	public static void Close(nint hwnd)
	{
		PostMessage(hwnd, 16, IntPtr.Zero, IntPtr.Zero);
	}

	public static ImageSource? GetIcon(nint hwnd, bool preferLarge = true)
	{
		//IL_0053: Unknown result type (might be due to invalid IL or missing references)
		try
		{
			nint icon = IconMessage(hwnd, preferLarge ? 1 : 2);
			if (icon == IntPtr.Zero)
			{
				icon = IconMessage(hwnd, (!preferLarge) ? 1 : 2);
			}
			if (icon == IntPtr.Zero)
			{
				icon = GetClassLongPtr(hwnd, -14);
			}
			if (icon == IntPtr.Zero)
			{
				return null;
			}
			BitmapSource img = Imaging.CreateBitmapSourceFromHIcon(icon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
			((Freezable)img).Freeze();
			// A blank/white/transparent window handle counts as "no icon" so callers fall through to a better source
			// (exe extraction) or a letter tile, instead of surfacing a white circle.
			if (IconResolver.IsBlank(img))
			{
				return null;
			}
			return img;
		}
		catch
		{
			return null;
		}
	}

	private static nint IconMessage(nint hwnd, int which)
	{
		nint res;
		return (SendMessageTimeout(hwnd, 127, which, IntPtr.Zero, 3u, 150u, out res) == IntPtr.Zero) ? IntPtr.Zero : res;
	}

	public static string? GetExePath(nint hwnd)
	{
		try
		{
			GetWindowThreadProcessId(hwnd, out var pid);
			if (pid == 0)
			{
				return null;
			}
			nint h = OpenProcess(4096u, inherit: false, pid);
			if (h == IntPtr.Zero)
			{
				return null;
			}
			try
			{
				StringBuilder sb = new StringBuilder(1024);
				int cap = sb.Capacity;
				return QueryFullProcessImageName(h, 0, sb, ref cap) ? sb.ToString() : null;
			}
			finally
			{
				CloseHandle(h);
			}
		}
		catch
		{
			return null;
		}
	}

	[DllImport("kernel32.dll")]
	private static extern nint OpenProcess(uint access, bool inherit, uint pid);

	[DllImport("kernel32.dll")]
	private static extern bool CloseHandle(nint h);

	[DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
	private static extern bool QueryFullProcessImageName(nint h, int flags, StringBuilder buf, ref int size);

	[DllImport("user32.dll")]
	private static extern bool EnumWindows(EnumProc cb, nint l);

	[DllImport("user32.dll")]
	private static extern bool IsWindowVisible(nint h);

	[DllImport("user32.dll")]
	private static extern bool IsWindow(nint h);

	[DllImport("user32.dll")]
	private static extern bool IsIconic(nint h);

	[DllImport("user32.dll")]
	private static extern bool IsZoomed(nint h);

	[DllImport("user32.dll", CharSet = CharSet.Unicode)]
	private static extern int GetWindowText(nint h, StringBuilder s, int m);

	[DllImport("user32.dll")]
	private static extern int GetWindowLong(nint h, int i);

	[DllImport("user32.dll")]
	private static extern nint GetAncestor(nint h, uint f);

	[DllImport("user32.dll")]
	private static extern nint GetLastActivePopup(nint h);

	[DllImport("user32.dll")]
	private static extern nint GetShellWindow();

	[DllImport("user32.dll")]
	private static extern nint GetForegroundWindow();

	[DllImport("user32.dll")]
	private static extern bool ShowWindow(nint h, int n);

	[DllImport("user32.dll")]
	private static extern bool BringWindowToTop(nint h);

	[DllImport("user32.dll")]
	private static extern bool SetForegroundWindow(nint h);

	[DllImport("user32.dll")]
	private static extern bool AttachThreadInput(uint a, uint b, bool f);

	[DllImport("user32.dll")]
	private static extern uint GetWindowThreadProcessId(nint h, out uint pid);

	[DllImport("kernel32.dll")]
	private static extern uint GetCurrentThreadId();

	[DllImport("user32.dll")]
	private static extern nint SendMessageTimeout(nint h, int msg, nint w, nint l, uint flags, uint timeout, out nint result);

	[DllImport("user32.dll", EntryPoint = "GetClassLongPtrW")]
	private static extern nint GetClassLongPtr(nint h, int i);

	[DllImport("dwmapi.dll")]
	private static extern int DwmGetWindowAttribute(nint h, int attr, out int val, int size);

	[DllImport("user32.dll")]
	private static extern bool PostMessage(nint h, int msg, nint w, nint l);
}
