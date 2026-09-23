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
}
