using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

namespace Win81Layer;

// Owns monitor work areas while the replacement taskbar is active. This is deliberately the only
// reservation mechanism: mixing SPI_SETWORKAREA with a registered AppBar leaves two independent
// owners and caused stale left/bottom strips after edge and display changes.
public static class TaskbarWorkArea
{
	private struct RECT
	{
		public int Left;
		public int Top;
		public int Right;
		public int Bottom;
	}

	private struct MONITORINFO
	{
		public int cbSize;
		public RECT rcMonitor;
		public RECT rcWork;
		public uint dwFlags;
	}

	private struct POINT
	{
		public int X;
		public int Y;
	}

	private delegate bool EnumProc(nint h, nint l);

	private const uint SPI_SETWORKAREA = 47u;
	private const uint WM_SETTINGCHANGE = 0x001Au;
	private const uint SWP_NOSIZE = 0x0001u;
	private const uint SWP_NOMOVE = 0x0002u;
	private const uint SWP_NOZORDER = 0x0004u;
	private const uint SWP_NOACTIVATE = 0x0010u;
	private const uint SWP_FRAMECHANGED = 0x0020u;
	private const uint SWP_ASYNCWINDOWPOS = 0x4000u;
	private const uint MONITOR_DEFAULTTONEAREST = 2u;
	private static readonly object Gate = new object();
	private static readonly Dictionary<string, RECT> Originals = new Dictionary<string, RECT>(StringComparer.OrdinalIgnoreCase);
	private static readonly string StatePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Win81Layer", "workarea-original.txt");

	[DllImport("user32.dll", SetLastError = true)]
	private static extern bool SystemParametersInfo(uint action, uint param, ref RECT rc, uint winIni);

	[DllImport("user32.dll")]
	private static extern nint MonitorFromPoint(POINT pt, uint flags);

	[DllImport("user32.dll")]
	private static extern bool GetMonitorInfo(nint hMonitor, ref MONITORINFO mi);

	// Capture the native areas before Explorer's taskbars are hidden. Persisted values win because
	// they are the rollback baseline after a crash and must not be replaced by a stale custom area.
	public static void BeginSession(IEnumerable<Screen> screens)
	{
		lock (Gate)
		{
			if (Originals.Count == 0 && TryLoadPersisted(out Dictionary<string, RECT> persisted))
			{
				foreach (KeyValuePair<string, RECT> item in persisted)
				{
					Originals[item.Key] = item.Value;
				}
			}

			bool changed = false;
			foreach (Screen screen in screens)
			{
				if (!Originals.ContainsKey(screen.DeviceName) && TryGetWorkArea(screen, out RECT work))
				{
					Originals[screen.DeviceName] = work;
					changed = true;
				}
			}
			if (changed || !File.Exists(StatePath))
			{
				PersistAll();
			}
		}
	}

	public static bool Reserve(Screen screen, string position, int thicknessPx, bool broadcast = true)
	{
		lock (Gate)
		{
			BeginSession(new[] { screen });
			RECT desired = Desired(screen.Bounds, position, thicknessPx);
			if (TryGetWorkArea(screen, out RECT current) && Equal(current, desired))
			{
				return true;
			}
			if (!SetWorkArea(ref desired, broadcast))
			{
				Logger.Log($"Work-area reserve failed for {screen.DeviceName}: win32={Marshal.GetLastWin32Error()}");
				return false;
			}
			return true;
		}
	}

	public static bool EnsureReserved(Screen screen, string position, int thicknessPx)
	{
		return Reserve(screen, position, thicknessPx, broadcast: false);
	}

	public static bool Release(Screen screen, bool broadcast = true)
	{
		return Reserve(screen, "Bottom", 0, broadcast);
	}

	public static Rectangle Expected(Screen screen, string position, int thicknessPx)
	{
		RECT rect = Desired(screen.Bounds, position, thicknessPx);
		return Rectangle.FromLTRB(rect.Left, rect.Top, rect.Right, rect.Bottom);
	}

	public static bool Matches(Screen screen, string position, int thicknessPx)
	{
		RECT desired = Desired(screen.Bounds, position, thicknessPx);
		return TryGetWorkArea(screen, out RECT current) && Equal(current, desired);
	}

	private static RECT Desired(Rectangle bounds, string position, int thicknessPx)
	{
		bool vertical = string.Equals(position, "Left", StringComparison.OrdinalIgnoreCase)
			|| string.Equals(position, "Right", StringComparison.OrdinalIgnoreCase);
		int thickness = Math.Clamp(thicknessPx, 0, Math.Max(0, vertical ? bounds.Width : bounds.Height));
		RECT result = new RECT
		{
			Left = bounds.Left,
			Top = bounds.Top,
			Right = bounds.Right,
			Bottom = bounds.Bottom
		};
		switch (position)
		{
		case "Top":
			result.Top += thickness;
			break;
		case "Left":
			result.Left += thickness;
			break;
		case "Right":
			result.Right -= thickness;
			break;
		default:
			result.Bottom -= thickness;
			break;
		}
		return result;
	}

	private static bool TryGetWorkArea(Screen screen, out RECT work)
	{
		work = default;
		Rectangle bounds = screen.Bounds;
		POINT center = new POINT
		{
			X = bounds.Left + bounds.Width / 2,
			Y = bounds.Top + bounds.Height / 2
		};
		nint monitor = MonitorFromPoint(center, MONITOR_DEFAULTTONEAREST);
		MONITORINFO info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
		if (!GetMonitorInfo(monitor, ref info))
		{
			return false;
		}
		work = info.rcWork;
		return true;
	}

	private static bool Equal(RECT a, RECT b)
	{
		return a.Left == b.Left && a.Top == b.Top && a.Right == b.Right && a.Bottom == b.Bottom;
	}

	private static bool SetWorkArea(ref RECT rectangle, bool notify)
	{
		// SPIF_SENDCHANGE performs a synchronous HWND_BROADCAST. One hung application can therefore
		// freeze the replacement shell during an edge/display transition. Commit first, then notify
		// other top-level windows without waiting for their UI threads.
		if (!SystemParametersInfo(SPI_SETWORKAREA, 0u, ref rectangle, 0u))
		{
			return false;
		}
		if (notify)
		{
			NotifyWorkAreaChanged();
		}
		return true;
	}

	internal static void NotifyWorkAreaChanged()
	{
		// A broadcast also wakes Explorer's hidden AppBar, which immediately reasserts its old
		// 48px reservation. Post to every other top-level window so applications refresh without
		// letting the suppressed native taskbar race the replacement shell.
		EnumWindows(delegate(nint window, nint _)
		{
			StringBuilder className = new StringBuilder(96);
			GetClassName(window, className, className.Capacity);
			string value = className.ToString();
			if (!string.Equals(value, "Shell_TrayWnd", StringComparison.Ordinal)
				&& !string.Equals(value, "Shell_SecondaryTrayWnd", StringComparison.Ordinal))
			{
				PostMessage(window, WM_SETTINGCHANGE, SPI_SETWORKAREA, nint.Zero);
			}
			return true;
		}, nint.Zero);
	}

	internal static Rectangle Current(Screen screen)
	{
		return TryGetWorkArea(screen, out RECT work)
			? Rectangle.FromLTRB(work.Left, work.Top, work.Right, work.Bottom)
			: Rectangle.Empty;
	}

	public static void NudgeWindows()
	{
		try
		{
			EnumWindows(delegate(nint window, nint _)
			{
				if (!IsWindowVisible(window) || !IsZoomed(window))
				{
					return true;
				}
				// Queue the non-client recalculation on the owner's thread. Calling SetWindowPlacement
				// synchronously across every process made the shell depend on every app being responsive.
				SetWindowPos(window, nint.Zero, 0, 0, 0, 0,
					SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE | SWP_FRAMECHANGED | SWP_ASYNCWINDOWPOS);
				return true;
			}, IntPtr.Zero);
		}
		catch (Exception ex)
		{
			Logger.Log("Work-area nudge failed: " + ex.Message);
		}
	}

	[DllImport("user32.dll")]
	private static extern bool EnumWindows(EnumProc callback, nint parameter);

	[DllImport("user32.dll", CharSet = CharSet.Unicode)]
	private static extern int GetClassName(nint window, StringBuilder className, int capacity);

	[DllImport("user32.dll", SetLastError = true)]
	private static extern bool PostMessage(nint window, uint message, nuint wParam, nint lParam);

	[DllImport("user32.dll")]
	private static extern bool IsWindowVisible(nint window);

	[DllImport("user32.dll")]
	private static extern bool IsZoomed(nint window);

	[DllImport("user32.dll", SetLastError = true)]
	private static extern bool SetWindowPos(nint window, nint insertAfter, int x, int y, int width, int height, uint flags);

	public static void Restore()
	{
		lock (Gate)
		{
			try
			{
				if (Originals.Count == 0 && TryLoadPersisted(out Dictionary<string, RECT> persisted))
				{
					foreach (KeyValuePair<string, RECT> item in persisted)
					{
						Originals[item.Key] = item.Value;
					}
				}
				Dictionary<string, Screen> connected = Screen.AllScreens.ToDictionary(screen => screen.DeviceName, StringComparer.OrdinalIgnoreCase);
				bool changed = false;
				foreach (KeyValuePair<string, RECT> original in Originals)
				{
					if (!connected.ContainsKey(original.Key))
					{
						continue;
					}
					RECT rect = original.Value;
					changed |= SetWorkArea(ref rect, notify: false);
				}
				if (changed)
				{
					NotifyWorkAreaChanged();
				}
			}
			catch (Exception ex)
			{
				Logger.Log("Work-area restore failed: " + ex.Message);
			}
			finally
			{
				Originals.Clear();
				ClearPersisted();
			}
		}
	}

	public static void RecoverFromUncleanExit()
	{
		lock (Gate)
		{
			if (!TryLoadPersisted(out Dictionary<string, RECT> persisted))
			{
				return;
			}
			try
			{
				Dictionary<string, Screen> connected = Screen.AllScreens.ToDictionary(screen => screen.DeviceName, StringComparer.OrdinalIgnoreCase);
				int restored = 0;
				foreach (KeyValuePair<string, RECT> item in persisted)
				{
					if (!connected.ContainsKey(item.Key))
					{
						continue;
					}
					RECT rect = item.Value;
					if (SetWorkArea(ref rect, notify: false))
					{
						restored++;
					}
				}
				if (restored > 0)
				{
					NotifyWorkAreaChanged();
				}
				Logger.Log($"Work-area recovered and recovery journal cleared ({restored} connected monitor(s))");
			}
			catch (Exception ex)
			{
				Logger.Log("Work-area recover failed: " + ex.Message);
			}
			finally
			{
				Originals.Clear();
				ClearPersisted();
			}
		}
	}

	private static void PersistAll()
	{
		try
		{
			Directory.CreateDirectory(Path.GetDirectoryName(StatePath)!);
			IEnumerable<string> lines = Originals.Select(item => $"{item.Key}|{item.Value.Left},{item.Value.Top},{item.Value.Right},{item.Value.Bottom}");
			string temp = StatePath + ".tmp";
			File.WriteAllText(temp, string.Join("\n", lines));
			File.Move(temp, StatePath, overwrite: true);
		}
		catch (Exception ex)
		{
			Logger.Log("Work-area persist failed: " + ex.Message);
		}
	}

	private static bool TryLoadPersisted(out Dictionary<string, RECT> all)
	{
		all = new Dictionary<string, RECT>(StringComparer.OrdinalIgnoreCase);
		try
		{
			if (!File.Exists(StatePath))
			{
				return false;
			}
			foreach (string raw in File.ReadAllLines(StatePath))
			{
				string line = raw.Trim();
				if (line.Length == 0)
				{
					continue;
				}
				int separator = line.IndexOf('|');
				string device = separator >= 0 ? line[..separator] : Screen.PrimaryScreen?.DeviceName ?? "PRIMARY";
				string coordinates = separator >= 0 ? line[(separator + 1)..] : line;
				string[] parts = coordinates.Split(',');
				if (parts.Length == 4
					&& int.TryParse(parts[0], out int left)
					&& int.TryParse(parts[1], out int top)
					&& int.TryParse(parts[2], out int right)
					&& int.TryParse(parts[3], out int bottom)
					&& right > left && bottom > top)
				{
					all[device] = new RECT { Left = left, Top = top, Right = right, Bottom = bottom };
				}
			}
			return all.Count > 0;
		}
		catch (Exception ex)
		{
			Logger.Log("Work-area load failed: " + ex.Message);
			return false;
		}
	}

	private static void ClearPersisted()
	{
		try
		{
			if (File.Exists(StatePath))
			{
				File.Delete(StatePath);
			}
			string temp = StatePath + ".tmp";
			if (File.Exists(temp))
			{
				File.Delete(temp);
			}
		}
		catch
		{
		}
	}
}
