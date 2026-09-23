using System;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace Win81Layer;

public sealed class SnapWatcher : IDisposable
{
	private struct MONITORINFO
	{
		public int cbSize;

		public SnapRect rcMonitor;

		public SnapRect rcWork;

		public uint dwFlags;
	}

	private delegate void WinEventProc(nint hHook, uint ev, nint hwnd, int idObject, int idChild, uint thread, uint time);

	private const uint EVENT_SYSTEM_MOVESIZEEND = 11u;

	private const uint WINEVENT_OUTOFCONTEXT = 0u;

	private readonly WinEventProc _proc;

	private readonly SnapAssist _assist;

	private readonly uint _ownPid = (uint)Environment.ProcessId;

	private nint _hook;

	private int? _origSnapAssist;

	private const string AdvKey = "Software\\Microsoft\\Windows\\CurrentVersion\\Explorer\\Advanced";

	private static readonly string StatePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Win81Layer", "snapassist-original.txt");

	[DllImport("user32.dll")]
	private static extern nint SetWinEventHook(uint min, uint max, nint mod, WinEventProc cb, uint pid, uint thread, uint flags);

	[DllImport("user32.dll")]
	private static extern bool UnhookWinEvent(nint h);

	[DllImport("user32.dll")]
	private static extern nint GetForegroundWindow();

	[DllImport("user32.dll")]
	private static extern bool GetWindowRect(nint h, out SnapRect r);

	[DllImport("user32.dll")]
	private static extern nint MonitorFromWindow(nint h, uint flags);

	[DllImport("user32.dll")]
	private static extern bool GetMonitorInfo(nint h, ref MONITORINFO mi);

	[DllImport("user32.dll")]
	private static extern bool IsIconic(nint h);

	[DllImport("user32.dll")]
	private static extern bool IsZoomed(nint h);

	[DllImport("user32.dll")]
	private static extern uint GetWindowThreadProcessId(nint h, out uint pid);

	[DllImport("user32.dll")]
	private static extern uint GetDpiForWindow(nint h);

	public SnapWatcher(SnapAssist assist)
	{
		_assist = assist;
		_proc = OnMoveSizeEnd;
		_hook = SetWinEventHook(11u, 11u, IntPtr.Zero, _proc, 0u, 0u, 0u);
		DisableNativeSnapAssist();
		Logger.Log($"SnapWatcher installed (hook={_hook != IntPtr.Zero})");
	}

	private void OnMoveSizeEnd(nint hHook, uint ev, nint hwnd, int idObject, int idChild, uint thread, uint time)
	{
		try
		{
			if (hwnd == IntPtr.Zero)
			{
				hwnd = GetForegroundWindow();
			}
			GetWindowThreadProcessId(hwnd, out var pid);
			if (pid != _ownPid)
			{
				if (!IsSnappedHalf(hwnd, out var emptyPx, out var occupiedPx))
				{
					_assist.HideAssist();
					return;
				}
				uint dpi = GetDpiForWindow(hwnd);
				double sc = ((dpi == 0) ? 1.0 : ((double)dpi / 96.0));
				SnapManager.SetScale(sc, sc);
				SnapManager.Record(hwnd, new Rectangle(occupiedPx.Left, occupiedPx.Top, occupiedPx.Right - occupiedPx.Left, occupiedPx.Bottom - occupiedPx.Top));
				_assist.OfferFor(emptyPx, hwnd);
			}
		}
		catch (Exception ex)
		{
			Logger.Log("SnapWatcher: " + ex.Message);
		}
	}

	private bool IsSnappedHalf(nint hwnd, out SnapRect emptyPx, out SnapRect occupiedPx)
	{
		emptyPx = default(SnapRect);
		occupiedPx = default(SnapRect);
		if (hwnd == IntPtr.Zero || IsIconic(hwnd) || IsZoomed(hwnd) || !GetWindowRect(hwnd, out var wr))
		{
			return false;
		}
		nint mon = MonitorFromWindow(hwnd, 2u);
		MONITORINFO mi = new MONITORINFO
		{
			cbSize = Marshal.SizeOf<MONITORINFO>()
		};
		if (!GetMonitorInfo(mon, ref mi))
		{
			return false;
		}
		SnapRect wa = mi.rcWork;
		int waW = wa.Right - wa.Left;
		int halfW = waW / 2;
		bool fullH = Math.Abs(wr.Top - wa.Top) <= 24 && Math.Abs(wr.Bottom - wa.Bottom) <= 24;
		bool halfWide = Math.Abs(wr.Right - wr.Left - halfW) <= 24;
		if (!fullH || !halfWide)
		{
			return false;
		}
		if (Math.Abs(wr.Left - wa.Left) <= 24 && (double)wr.Right < (double)wa.Left + (double)waW * 0.62)
		{
			emptyPx = new SnapRect
			{
				Left = wa.Left + halfW,
				Top = wa.Top,
				Right = wa.Right,
				Bottom = wa.Bottom
			};
			occupiedPx = new SnapRect
			{
				Left = wa.Left,
				Top = wa.Top,
				Right = wa.Left + halfW,
				Bottom = wa.Bottom
			};
			return true;
		}
		if (Math.Abs(wr.Right - wa.Right) <= 24 && (double)wr.Left > (double)wa.Left + (double)waW * 0.38)
		{
			emptyPx = new SnapRect
			{
				Left = wa.Left,
				Top = wa.Top,
				Right = wa.Left + halfW,
				Bottom = wa.Bottom
			};
			occupiedPx = new SnapRect
			{
				Left = wa.Left + halfW,
				Top = wa.Top,
				Right = wa.Right,
				Bottom = wa.Bottom
			};
			return true;
		}
		return false;
	}

	private void DisableNativeSnapAssist()
	{
		try
		{
			using RegistryKey k = Registry.CurrentUser.OpenSubKey("Software\\Microsoft\\Windows\\CurrentVersion\\Explorer\\Advanced", writable: true);
			if (k != null)
			{
				if (TryLoadOriginal(out var saved))
				{
					_origSnapAssist = saved;
				}
				else
				{
					_origSnapAssist = k.GetValue("SnapAssist") as int?;
					PersistOriginal(_origSnapAssist);
				}
				k.SetValue("SnapAssist", 0, RegistryValueKind.DWord);
			}
		}
		catch (Exception ex)
		{
			Logger.Log("Disable native Snap Assist: " + ex.Message);
		}
	}

	private void RestoreNativeSnapAssist()
	{
		try
		{
			using RegistryKey k = Registry.CurrentUser.OpenSubKey("Software\\Microsoft\\Windows\\CurrentVersion\\Explorer\\Advanced", writable: true);
			if (k == null)
			{
				return;
			}
			int? origSnapAssist = _origSnapAssist;
			if (!origSnapAssist.HasValue)
			{
				goto IL_0053;
			}
			int v = origSnapAssist.GetValueOrDefault();
			if (1 == 0)
			{
				goto IL_0053;
			}
			k.SetValue("SnapAssist", v, RegistryValueKind.DWord);
			goto end_IL_0013;
			IL_0053:
			k.DeleteValue("SnapAssist", throwOnMissingValue: false);
			end_IL_0013:;
		}
		catch (Exception ex)
		{
			Logger.Log("Restore native Snap Assist: " + ex.Message);
		}
		ClearOriginal();
	}

	private static bool TryLoadOriginal(out int? value)
	{
		value = null;
		try
		{
			if (!File.Exists(StatePath))
			{
				return false;
			}
			string s = File.ReadAllText(StatePath).Trim();
			if (s.Length != 0 && int.TryParse(s, out var v))
			{
				value = v;
			}
			return true;
		}
		catch
		{
			return false;
		}
	}

	private static void PersistOriginal(int? v)
	{
		try
		{
			Directory.CreateDirectory(Path.GetDirectoryName(StatePath));
			File.WriteAllText(StatePath, v?.ToString() ?? "");
		}
		catch (Exception ex)
		{
			Logger.Log("Persist Snap Assist original: " + ex.Message);
		}
	}

	private static void ClearOriginal()
	{
		try
		{
			if (File.Exists(StatePath))
			{
				File.Delete(StatePath);
			}
		}
		catch
		{
		}
	}

	// 2026-09-07 (user): the custom Snap system is disabled (the launcher must not overlay/manage other apps'
	// windows). This static restore is called at startup instead of installing the watcher, so any SnapAssist=0
	// override a previous run left is undone and Windows' OWN native snap assist is active again.
	public static void EnsureNativeSnapAssistRestored()
	{
		try
		{
			using RegistryKey k = Registry.CurrentUser.OpenSubKey(AdvKey, writable: true);
			if (k == null) return;
			// Return to the Windows DEFAULT by REMOVING the value, not by restoring a persisted "original": the
			// launcher's own forced 0 may have been recorded as the original in an earlier run, and restoring that
			// would keep native snap assist disabled. Absent = Windows default = snap assist ON = the native
			// behaviour the user asked to keep.
			k.DeleteValue("SnapAssist", throwOnMissingValue: false);
			ClearOriginal();
		}
		catch (Exception ex)
		{
			Logger.Log("EnsureNativeSnapAssistRestored: " + ex.Message);
		}
	}

	public void Dispose()
	{
		if (_hook != IntPtr.Zero)
		{
			UnhookWinEvent(_hook);
			_hook = IntPtr.Zero;
		}
		RestoreNativeSnapAssist();
	}
}
