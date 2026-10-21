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

	// Task-button actions on other apps' windows. None of them waits on the target app: a synchronous ShowWindow or
	// SetWindowPos on a window of another thread sends WM_WINDOWPOSCHANGING and friends and waits, with no timeout, until
	// that app answers, so a busy or hung app froze the taskbar (and the shell). Explorer's taskbar works the same way:
	// ShowWindowAsync, a plain SetForegroundWindow (the click gave us the right to set the foreground), no
	// AttachThreadInput. The queued calls return at once even for a window Windows flags as hung, so they are made
	// regardless (the app acts on them when it answers again): a busy moment never throws the user's click away.

	// Set by --traytest: never grant or take the foreground (its target windows are test windows, the foreground is not).
	internal static bool DiagnosticsNoForeground;

	public static void ActivateOrMinimize(nint hwnd)
	{
		if (GetForegroundWindow() == hwnd)
		{
			Minimize(hwnd);
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

	// True when the window is alive, so a "try the next window" loop stops at the first live one (a busy or hung window
	// included: its activation is queued, and nothing else of the group is pulled in front of it). A refused
	// SetForegroundWindow still counts: Windows then flashes the app instead, and the window must not be dropped or
	// replaced by a new instance.
	public static bool TryActivate(nint hwnd)
	{
		if (!IsWindowAlive(hwnd))
		{
			return false;
		}
		using (Breadcrumbs.Enter("TryActivate", hwnd))
		{
			if (IsIconic(hwnd))
			{
				ShowWindowAsync(hwnd, SW_RESTORE);
			}
			// A window with a modal dialog up is disabled; the dialog is what must come forward (as Alt+Tab does).
			nint target = hwnd;
			if (!IsWindowEnabled(hwnd))
			{
				nint popup = GetLastActivePopup(hwnd);
				if (popup != IntPtr.Zero && popup != hwnd && IsWindowVisible(popup))
				{
					target = popup;
				}
			}
			if (!DiagnosticsNoForeground)
			{
				ShellLaunch.AllowForeground();
				SetForegroundWindow(target);
			}
			SetWindowPos(target, IntPtr.Zero, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_ASYNCWINDOWPOS);
			return IsWindow(hwnd);
		}
	}

	public static bool IsForeground(nint hwnd)
	{
		return hwnd != IntPtr.Zero && GetForegroundWindow() == hwnd;
	}

	public static bool IsHung(nint hwnd)
	{
		return hwnd != IntPtr.Zero && IsHungAppWindow(hwnd);
	}

	public static void Minimize(nint hwnd)
	{
		if (hwnd != IntPtr.Zero)
		{
			ShowWindowAsync(hwnd, SW_MINIMIZE);
		}
	}

	public static void Restore(nint hwnd)
	{
		if (hwnd != IntPtr.Zero)
		{
			ShowWindowAsync(hwnd, SW_RESTORE);
		}
	}

	public static bool IsMaximized(nint hwnd)
	{
		return IsZoomed(hwnd);
	}

	public static void ToggleMaximize(nint hwnd)
	{
		if (hwnd != IntPtr.Zero)
		{
			ShowWindowAsync(hwnd, IsZoomed(hwnd) ? SW_RESTORE : SW_MAXIMIZE);
		}
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

	// WM_GETICON is the one window message here: at most one 150 ms wait per call (a window that did not answer the
	// first ask is not asked again), none at all for a window Windows already flags as hung, and none for a few seconds
	// for any window of a thread that just timed out (a list of N windows of one busy app waits once, not N times; the
	// class icon is used meanwhile).
	public static ImageSource? GetIcon(nint hwnd, bool preferLarge = true)
	{
		//IL_0053: Unknown result type (might be due to invalid IL or missing references)
		try
		{
			nint icon = IntPtr.Zero;
			uint thread = GetWindowThreadProcessId(hwnd, out _);
			if (!IsHungAppWindow(hwnd) && !IconThreadRecentlySlow(thread))
			{
				icon = IconMessage(hwnd, preferLarge ? 1 : 2, out bool timedOut);
				if (icon == IntPtr.Zero && !timedOut)
				{
					icon = IconMessage(hwnd, (!preferLarge) ? 1 : 2, out timedOut);
				}
				if (timedOut)
				{
					MarkIconThreadSlow(thread);
				}
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

	// Threads whose window did not answer WM_GETICON in time, and when.
	private static readonly Dictionary<uint, long> SlowIconThreads = new Dictionary<uint, long>();

	internal const long SlowIconThreadMs = 3000;

	private static bool IconThreadRecentlySlow(uint thread)
	{
		if (thread == 0)
		{
			return false;
		}
		lock (SlowIconThreads)
		{
			if (!SlowIconThreads.TryGetValue(thread, out long at))
			{
				return false;
			}
			if (Environment.TickCount64 - at < SlowIconThreadMs)
			{
				return true;
			}
			SlowIconThreads.Remove(thread);
			return false;
		}
	}

	private static void MarkIconThreadSlow(uint thread)
	{
		if (thread == 0)
		{
			return;
		}
		lock (SlowIconThreads)
		{
			if (SlowIconThreads.Count > 64)
			{
				SlowIconThreads.Clear();
			}
			SlowIconThreads[thread] = Environment.TickCount64;
		}
	}

	private static nint IconMessage(nint hwnd, int which, out bool timedOut)
	{
		nint res;
		// SMTO_BLOCK | SMTO_ABORTIFHUNG | SMTO_ERRORONEXIT
		if (SendMessageTimeout(hwnd, 127, which, IntPtr.Zero, 0x23u, 150u, out res) == IntPtr.Zero)
		{
			// Timed out, hung or gone: asking again would only wait again.
			timedOut = true;
			return IntPtr.Zero;
		}
		timedOut = false;
		return res;
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
	private static extern bool ShowWindowAsync(nint h, int n);

	[DllImport("user32.dll")]
	private static extern bool IsHungAppWindow(nint h);

	[DllImport("user32.dll")]
	private static extern bool IsWindowEnabled(nint h);

	[DllImport("user32.dll")]
	private static extern bool SetWindowPos(nint h, nint after, int x, int y, int cx, int cy, uint flags);

	private const uint SWP_NOSIZE = 0x0001u;

	private const uint SWP_NOMOVE = 0x0002u;

	private const uint SWP_NOACTIVATE = 0x0010u;

	private const uint SWP_ASYNCWINDOWPOS = 0x4000u;

	[DllImport("user32.dll")]
	private static extern bool SetForegroundWindow(nint h);

	[DllImport("user32.dll")]
	private static extern uint GetWindowThreadProcessId(nint h, out uint pid);

	[DllImport("user32.dll")]
	private static extern nint SendMessageTimeout(nint h, int msg, nint w, nint l, uint flags, uint timeout, out nint result);

	[DllImport("user32.dll", EntryPoint = "GetClassLongPtrW")]
	private static extern nint GetClassLongPtr(nint h, int i);

	[DllImport("dwmapi.dll")]
	private static extern int DwmGetWindowAttribute(nint h, int attr, out int val, int size);

	[DllImport("user32.dll")]
	private static extern bool PostMessage(nint h, int msg, nint w, nint l);
}
