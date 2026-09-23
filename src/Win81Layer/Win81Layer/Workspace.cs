using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace Win81Layer;

// WORKSPACES: capture the current on-screen window layout for a group's apps, and restore it later (reposition running
// windows; relaunch missing apps then position them). Built entirely on existing helpers — WindowList (enumerate/exe/
// activate/maximized), AppInventory.GetWindowAppId (per-window AUMID for packaged/shell apps), SnapZones.Apply (move+size),
// AppLauncher.TryLaunch (relaunch). No new grouping system: the layout hangs off the existing App Groups (GroupVm/Profile).
internal static class Workspace
{
	private const int SW_MINIMIZE = 6;
	private const int SW_MAXIMIZE = 3;

	[StructLayout(LayoutKind.Sequential)]
	private struct RECT { public int Left; public int Top; public int Right; public int Bottom; }

	[DllImport("user32.dll")]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static extern bool GetWindowRect(nint hWnd, out RECT lpRect);

	[DllImport("user32.dll")]
	private static extern bool ShowWindow(nint hWnd, int nCmdShow);

	[DllImport("user32.dll")]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static extern bool IsIconic(nint hWnd);

	private sealed record LiveWin(nint Hwnd, string? Exe, string? Aumid);

	// Snapshot every top-level app window once, tagged with its exe filename + per-window AUMID (the same identity the
	// taskbar grouping uses). Off the launcher's own windows are excluded by WindowList.Enumerate.
	private static List<LiveWin> LiveWindows()
	{
		List<LiveWin> list = new List<LiveWin>();
		try
		{
			foreach ((nint hwnd, string _) in WindowList.Enumerate(nint.Zero))
			{
				string? exe = null;
				try { string? p = WindowList.GetExePath(hwnd); exe = string.IsNullOrEmpty(p) ? null : Path.GetFileName(p); } catch { }
				string? aumid = null;
				try { aumid = AppInventory.GetWindowAppId(hwnd); } catch { }
				list.Add(new LiveWin(hwnd, exe, aumid));
			}
		}
		catch (Exception ex)
		{
			Logger.Log("[workspace] enumerate: " + ex.Message);
		}
		return list;
	}

	// (exe filename, aumid) an app should be matched by. LaunchPath is polymorphic (exe | shell:AppsFolder\AUMID | .lnk).
	private static (string? exe, string? aumid) MatchKey(string launchPath, string? appId)
	{
		string? exe = null;
		if (!string.IsNullOrEmpty(launchPath) && !launchPath.StartsWith("shell:", StringComparison.OrdinalIgnoreCase)
			&& launchPath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
		{
			try { exe = Path.GetFileName(launchPath); } catch { }
		}
		return (exe, string.IsNullOrEmpty(appId) ? null : appId);
	}

	private static bool Matches(LiveWin w, string? exe, string? aumid)
	{
		if (aumid != null && w.Aumid != null && string.Equals(w.Aumid, aumid, StringComparison.OrdinalIgnoreCase))
		{
			return true;
		}
		if (exe != null && w.Exe != null && string.Equals(w.Exe, exe, StringComparison.OrdinalIgnoreCase))
		{
			return true;
		}
		return false;
	}

	// CAPTURE: for each app (identity), find its first live window and record rect + state + monitor. Only running apps are
	// recorded (a snapshot of what's on screen now) — nothing is fabricated for closed apps.
	public static List<Profile.WindowLayoutRecord> Capture(IEnumerable<(string LaunchPath, string? AppId, string Name)> apps)
	{
		List<Profile.WindowLayoutRecord> outp = new List<Profile.WindowLayoutRecord>();
		List<LiveWin> live = LiveWindows();
		HashSet<nint> used = new HashSet<nint>();
		foreach ((string launchPath, string? appId, string name) in apps)
		{
			(string? exe, string? aumid) = MatchKey(launchPath, appId);
			if (exe == null && aumid == null)
			{
				continue;
			}
			LiveWin? w = live.FirstOrDefault(x => !used.Contains(x.Hwnd) && Matches(x, exe, aumid));
			if (w == null)
			{
				continue;
			}
			used.Add(w.Hwnd);
			string state = "Normal";
			int x = 0, y = 0, cw = 0, ch = 0;
			string monitor = "";
			try
			{
				if (IsIconic(w.Hwnd))
				{
					state = "Minimized";
				}
				else
				{
					if (WindowList.IsMaximized(w.Hwnd)) { state = "Maximized"; }
					if (GetWindowRect(w.Hwnd, out RECT r))
					{
						x = r.Left; y = r.Top; cw = Math.Max(0, r.Right - r.Left); ch = Math.Max(0, r.Bottom - r.Top);
						try { monitor = Screen.FromRectangle(new Rectangle(x, y, Math.Max(1, cw), Math.Max(1, ch))).DeviceName; } catch { }
					}
				}
			}
			catch { }
			outp.Add(new Profile.WindowLayoutRecord
			{
				LaunchPath = launchPath,
				AppId = appId,
				Name = name,
				X = x, Y = y, W = cw, H = ch,
				Monitor = monitor,
				State = state
			});
		}
		return outp;
	}

	// RESTORE: position any already-running windows immediately, then relaunch the missing apps on a background task and
	// position them as their windows appear (poll with timeout — never blocks the UI thread).
	public static void Restore(IReadOnlyList<Profile.WindowLayoutRecord> layout)
	{
		if (layout == null || layout.Count == 0)
		{
			return;
		}
		List<LiveWin> live = LiveWindows();
		HashSet<nint> used = new HashSet<nint>();
		List<Profile.WindowLayoutRecord> missing = new List<Profile.WindowLayoutRecord>();
		foreach (Profile.WindowLayoutRecord rec in layout)
		{
			(string? exe, string? aumid) = MatchKey(rec.LaunchPath, rec.AppId);
			LiveWin? w = live.FirstOrDefault(x => !used.Contains(x.Hwnd) && Matches(x, exe, aumid));
			if (w != null)
			{
				used.Add(w.Hwnd);
				ApplyTo(w.Hwnd, rec);
			}
			else if (!string.IsNullOrEmpty(rec.LaunchPath))
			{
				missing.Add(rec);
			}
		}
		if (missing.Count == 0)
		{
			return;
		}
		// Launch the missing apps (STA COM path handled by AppLauncher via ShellLaunch), then poll for their windows.
		foreach (Profile.WindowLayoutRecord rec in missing)
		{
			Profile.WindowLayoutRecord r = rec;
			ShellLaunch.Run(delegate
			{
				try { AppLauncher.TryLaunch(r.LaunchPath, null, r.AppId, asAdmin: false, out string _); }
				catch (Exception ex) { Logger.Log("[workspace] relaunch " + r.Name + ": " + ex.Message); }
			});
		}
		_ = PositionWhenReadyAsync(missing);
	}

	private static async Task PositionWhenReadyAsync(List<Profile.WindowLayoutRecord> pending)
	{
		List<Profile.WindowLayoutRecord> remaining = new List<Profile.WindowLayoutRecord>(pending);
		HashSet<nint> placed = new HashSet<nint>();
		for (int attempt = 0; attempt < 24 && remaining.Count > 0; attempt++)
		{
			await Task.Delay(250).ConfigureAwait(continueOnCapturedContext: false);
			List<LiveWin> live = LiveWindows();
			for (int i = remaining.Count - 1; i >= 0; i--)
			{
				Profile.WindowLayoutRecord rec = remaining[i];
				(string? exe, string? aumid) = MatchKey(rec.LaunchPath, rec.AppId);
				LiveWin? w = live.FirstOrDefault(x => !placed.Contains(x.Hwnd) && Matches(x, exe, aumid));
				if (w != null)
				{
					placed.Add(w.Hwnd);
					ApplyTo(w.Hwnd, rec);
					remaining.RemoveAt(i);
				}
			}
		}
	}

	// Position + state a single window. SetWindowPos/ShowWindow are thread-agnostic, so this is safe off the UI thread.
	private static void ApplyTo(nint hwnd, Profile.WindowLayoutRecord rec)
	{
		try
		{
			if (rec.State == "Minimized")
			{
				ShowWindow(hwnd, SW_MINIMIZE);
				return;
			}
			if (rec.W > 0 && rec.H > 0)
			{
				Rectangle target = ClampToScreens(new Rectangle(rec.X, rec.Y, rec.W, rec.H), rec.Monitor);
				SnapZones.Apply(hwnd, target);   // ShowWindow(SW_RESTORE) + SetWindowPos to the physical rect
			}
			if (rec.State == "Maximized")
			{
				ShowWindow(hwnd, SW_MAXIMIZE);
			}
		}
		catch (Exception ex)
		{
			Logger.Log("[workspace] apply " + rec.Name + ": " + ex.Message);
		}
	}

	// Keep a restored window on a real monitor: if its saved monitor is gone (or the rect is off every screen), clamp it
	// into the primary work area at the same size.
	private static Rectangle ClampToScreens(Rectangle r, string savedMonitor)
	{
		try
		{
			Screen[] screens = Screen.AllScreens;
			bool monitorPresent = string.IsNullOrEmpty(savedMonitor) || screens.Any(s => string.Equals(s.DeviceName, savedMonitor, StringComparison.OrdinalIgnoreCase));
			Point center = new Point(r.X + r.Width / 2, r.Y + r.Height / 2);
			bool onScreen = screens.Any(s => s.Bounds.Contains(center));
			if (monitorPresent && onScreen)
			{
				return r;
			}
			Rectangle wa = Screen.PrimaryScreen.WorkingArea;
			int w = Math.Min(r.Width, wa.Width);
			int h = Math.Min(r.Height, wa.Height);
			int x = Math.Max(wa.Left, Math.Min(r.X, wa.Right - w));
			int y = Math.Max(wa.Top, Math.Min(r.Y, wa.Bottom - h));
			return new Rectangle(x, y, Math.Max(240, w), Math.Max(160, h));
		}
		catch
		{
			return r;
		}
	}
}
