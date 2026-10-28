using System;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace Win81Layer;

internal static class MonitorBrightness
{
	[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
	private struct PHYSICAL_MONITOR
	{
		public nint h;

		[MarshalAs(UnmanagedType.ByValArray, SizeConst = 128)]
		public char[] desc;
	}

	private static int _target = -1;

	private static bool _busy;

	private static readonly object _throttleLock = new object();

	[DllImport("user32.dll")]
	private static extern nint GetDesktopWindow();

	[DllImport("user32.dll")]
	private static extern nint MonitorFromWindow(nint hwnd, uint flags);

	[DllImport("dxva2.dll")]
	private static extern bool GetNumberOfPhysicalMonitorsFromHMONITOR(nint h, out uint n);

	[DllImport("dxva2.dll")]
	private static extern bool GetPhysicalMonitorsFromHMONITOR(nint h, uint n, [Out] PHYSICAL_MONITOR[] arr);

	[DllImport("dxva2.dll")]
	private static extern bool GetMonitorBrightness(nint h, out uint min, out uint cur, out uint max);

	[DllImport("dxva2.dll")]
	private static extern bool SetMonitorBrightness(nint h, uint value);

	[DllImport("dxva2.dll")]
	private static extern bool DestroyPhysicalMonitors(uint n, [In] PHYSICAL_MONITOR[] arr);

	private static bool TryOpen(out PHYSICAL_MONITOR[] arr, out uint n)
	{
		arr = Array.Empty<PHYSICAL_MONITOR>();
		n = 0u;
		nint hmon = MonitorFromWindow(GetDesktopWindow(), 2u);
		if (hmon == IntPtr.Zero || !GetNumberOfPhysicalMonitorsFromHMONITOR(hmon, out n) || n == 0)
		{
			return false;
		}
		arr = new PHYSICAL_MONITOR[n];
		return GetPhysicalMonitorsFromHMONITOR(hmon, n, arr);
	}

	public static int Get()
	{
		try
		{
			if (!TryOpen(out PHYSICAL_MONITOR[] arr, out uint n))
			{
				return -1;
			}
			try
			{
				if (GetMonitorBrightness(arr[0].h, out var _, out var cur, out var max) && max != 0)
				{
					return (int)Math.Round((double)cur * 100.0 / (double)max);
				}
				return -1;
			}
			finally
			{
				DestroyPhysicalMonitors(n, arr);
			}
		}
		catch
		{
			return -1;
		}
	}

	public static void SetThrottled(int percent)
	{
		SettingsGuard.ThrowIfReadOnly("brightness");
		percent = Math.Clamp(percent, 0, 100);
		lock (_throttleLock)
		{
			_target = percent;
			if (_busy)
			{
				return;
			}
			_busy = true;
		}
		Task.Run(delegate
		{
			try
			{
				if (!TryOpen(out PHYSICAL_MONITOR[] arr, out uint n))
				{
					lock (_throttleLock)
					{
						_busy = false;
						return;
					}
				}
				try
				{
					if (!GetMonitorBrightness(arr[0].h, out var min, out var _, out var max) || max <= min)
					{
						lock (_throttleLock)
						{
							_busy = false;
							return;
						}
					}
					int num = -1;
					while (true)
					{
						int target;
						lock (_throttleLock)
						{
							target = _target;
							if (target == num)
							{
								_busy = false;
								break;
							}
						}
						num = target;
						try
						{
							SetMonitorBrightness(arr[0].h, (uint)((double)min + (double)((max - min) * target) / 100.0));
						}
						catch
						{
						}
					}
				}
				finally
				{
					DestroyPhysicalMonitors(n, arr);
				}
			}
			catch
			{
				lock (_throttleLock)
				{
					_busy = false;
				}
			}
		});
	}

	public static void Set(int percent)
	{
		SettingsGuard.ThrowIfReadOnly("brightness");
		percent = Math.Clamp(percent, 0, 100);
		try
		{
			if (!TryOpen(out PHYSICAL_MONITOR[] arr, out uint n))
			{
				return;
			}
			try
			{
				if (GetMonitorBrightness(arr[0].h, out var min, out var _, out var max) && max > min)
				{
					SetMonitorBrightness(arr[0].h, (uint)((double)min + (double)((max - min) * percent) / 100.0));
				}
			}
			finally
			{
				DestroyPhysicalMonitors(n, arr);
			}
		}
		catch
		{
		}
	}

	// ---- Per monitor (PC Settings' Displays page): the GDI device name ("\.\DISPLAY1") picks the monitor ----

	[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
	private struct MONITORINFOEX
	{
		public int cbSize;
		public RECT rcMonitor;
		public RECT rcWork;
		public uint dwFlags;
		[MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
		public string szDevice;
	}

	[StructLayout(LayoutKind.Sequential)]
	private struct RECT
	{
		public int left;
		public int top;
		public int right;
		public int bottom;
	}

	private delegate bool MonitorEnumProc(nint hMonitor, nint hdc, nint rect, nint data);

	[DllImport("user32.dll")]
	private static extern bool EnumDisplayMonitors(nint hdc, nint clip, MonitorEnumProc proc, nint data);

	[DllImport("user32.dll", CharSet = CharSet.Unicode)]
	private static extern bool GetMonitorInfo(nint hMonitor, ref MONITORINFOEX info);

	private static nint MonitorOf(string device)
	{
		nint found = IntPtr.Zero;
		try
		{
			EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, delegate(nint h, nint hdc, nint rect, nint data)
			{
				MONITORINFOEX mi = new MONITORINFOEX { cbSize = Marshal.SizeOf<MONITORINFOEX>() };
				if (GetMonitorInfo(h, ref mi) && string.Equals(mi.szDevice, device, StringComparison.OrdinalIgnoreCase))
				{
					found = h;
					return false;
				}
				return true;
			}, IntPtr.Zero);
		}
		catch
		{
		}
		return found;
	}

	private static bool TryOpen(string device, out PHYSICAL_MONITOR[] arr, out uint n)
	{
		arr = Array.Empty<PHYSICAL_MONITOR>();
		n = 0u;
		nint hmon = MonitorOf(device);
		if (hmon == IntPtr.Zero || !GetNumberOfPhysicalMonitorsFromHMONITOR(hmon, out n) || n == 0)
		{
			return false;
		}
		arr = new PHYSICAL_MONITOR[n];
		return GetPhysicalMonitorsFromHMONITOR(hmon, n, arr);
	}

	// 0..100 mapped from the monitor's own min..max; -1 when the monitor does not answer over DDC/CI.
	public static int Get(string device)
	{
		try
		{
			if (!TryOpen(device, out PHYSICAL_MONITOR[] arr, out uint n))
			{
				return -1;
			}
			try
			{
				if (GetMonitorBrightness(arr[0].h, out uint min, out uint cur, out uint max) && max > min)
				{
					return (int)Math.Round((double)(cur - min) * 100.0 / (double)(max - min));
				}
				return -1;
			}
			finally
			{
				DestroyPhysicalMonitors(n, arr);
			}
		}
		catch
		{
			return -1;
		}
	}

	// The monitor a drag writes to: opened (and its range read) on the first write, kept while writes keep coming, closed
	// after a short pause, so a drag is one open, one DDC/CI range read and then writes only.
	private sealed class OpenMonitor
	{
		internal string Device = "";
		internal PHYSICAL_MONITOR[] Arr = Array.Empty<PHYSICAL_MONITOR>();
		internal uint N;
		internal uint Min;
		internal uint Max;
		internal long LastUse;
	}

	private static OpenMonitor _open;

	private static readonly object _openLock = new object();

	private static System.Threading.Timer _closeTimer;

	private const int KeepOpenMs = 1500;

	// One write (the caller throttles: PC Settings' worker spaces them 60 ms apart and writes the final value on release).
	public static bool Set(string device, int percent)
	{
		SettingsGuard.ThrowIfReadOnly("brightness " + device);
		percent = Math.Clamp(percent, 0, 100);
		try
		{
			lock (_openLock)
			{
				for (int attempt = 0; attempt < 2; attempt++)
				{
					OpenMonitor m = Opened(device);
					if (m == null)
					{
						return false;
					}
					m.LastUse = Environment.TickCount64;
					if (SetMonitorBrightness(m.Arr[0].h, (uint)((double)m.Min + (double)((m.Max - m.Min) * percent) / 100.0)))
					{
						ScheduleClose();
						return true;
					}
					// the handle went stale (the monitor was switched or re-enumerated): open it again once
					CloseOpen();
				}
				return false;
			}
		}
		catch
		{
			lock (_openLock)
			{
				CloseOpen();
			}
			return false;
		}
	}

	// Under _openLock: the open monitor of this device (opening it and reading its range when needed).
	private static OpenMonitor Opened(string device)
	{
		if (_open != null && string.Equals(_open.Device, device, StringComparison.OrdinalIgnoreCase))
		{
			return _open;
		}
		CloseOpen();
		if (!TryOpen(device, out PHYSICAL_MONITOR[] arr, out uint n))
		{
			return null;
		}
		if (!GetMonitorBrightness(arr[0].h, out uint min, out uint _, out uint max) || max <= min)
		{
			DestroyPhysicalMonitors(n, arr);
			return null;
		}
		_open = new OpenMonitor { Device = device, Arr = arr, N = n, Min = min, Max = max, LastUse = Environment.TickCount64 };
		return _open;
	}

	private static void CloseOpen()
	{
		if (_open != null)
		{
			try
			{
				DestroyPhysicalMonitors(_open.N, _open.Arr);
			}
			catch
			{
			}
			_open = null;
		}
	}

	private static void ScheduleClose()
	{
		_closeTimer ??= new System.Threading.Timer(delegate
		{
			lock (_openLock)
			{
				if (_open == null)
				{
					return;
				}
				long idle = Environment.TickCount64 - _open.LastUse;
				if (idle >= KeepOpenMs)
				{
					CloseOpen();
				}
				else
				{
					_closeTimer?.Change(KeepOpenMs - idle + 50, System.Threading.Timeout.Infinite);
				}
			}
		}, null, System.Threading.Timeout.Infinite, System.Threading.Timeout.Infinite);
		_closeTimer.Change(KeepOpenMs + 50, System.Threading.Timeout.Infinite);
	}
}
