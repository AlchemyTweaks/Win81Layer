using System;
using System.Runtime.InteropServices;

namespace Win81Layer;

internal static class AccessibilityKeys
{
	private struct STICKYKEYS
	{
		public uint cbSize;

		public uint dwFlags;
	}

	private struct TOGGLEKEYS
	{
		public uint cbSize;

		public uint dwFlags;
	}

	private struct FILTERKEYS
	{
		public uint cbSize;

		public uint dwFlags;

		public uint iWaitMSec;

		public uint iDelayMSec;

		public uint iRepeatMSec;

		public uint iBounceMSec;
	}

	private const uint SPI_GETSTICKYKEYS = 58u;

	private const uint SPI_SETSTICKYKEYS = 59u;

	private const uint SPI_GETTOGGLEKEYS = 52u;

	private const uint SPI_SETTOGGLEKEYS = 53u;

	private const uint SPI_GETFILTERKEYS = 50u;

	private const uint SPI_SETFILTERKEYS = 51u;

	private const uint FLAG_ON = 1u;

	private const uint FLAG_AVAILABLE = 2u;

	private const uint SPIF_SENDCHANGE = 3u;

	[DllImport("user32.dll")]
	private static extern bool SystemParametersInfo(uint act, uint param, ref STICKYKEYS pv, uint win);

	[DllImport("user32.dll")]
	private static extern bool SystemParametersInfo(uint act, uint param, ref TOGGLEKEYS pv, uint win);

	[DllImport("user32.dll")]
	private static extern bool SystemParametersInfo(uint act, uint param, ref FILTERKEYS pv, uint win);

	public static bool StickyOn()
	{
		STICKYKEYS s = new STICKYKEYS
		{
			cbSize = (uint)Marshal.SizeOf<STICKYKEYS>()
		};
		try
		{
			SystemParametersInfo(58u, s.cbSize, ref s, 0u);
		}
		catch
		{
		}
		return (s.dwFlags & 1) != 0;
	}

	public static void SetSticky(bool on)
	{
		STICKYKEYS s = new STICKYKEYS
		{
			cbSize = (uint)Marshal.SizeOf<STICKYKEYS>()
		};
		try
		{
			SystemParametersInfo(58u, s.cbSize, ref s, 0u);
			s.dwFlags = (on ? (s.dwFlags | 1 | 2) : (s.dwFlags & 0xFFFFFFFEu));
			SystemParametersInfo(59u, s.cbSize, ref s, 3u);
		}
		catch (Exception ex)
		{
			Logger.Log("StickyKeys set: " + ex.Message);
		}
	}

	public static bool ToggleOn()
	{
		TOGGLEKEYS s = new TOGGLEKEYS
		{
			cbSize = (uint)Marshal.SizeOf<TOGGLEKEYS>()
		};
		try
		{
			SystemParametersInfo(52u, s.cbSize, ref s, 0u);
		}
		catch
		{
		}
		return (s.dwFlags & 1) != 0;
	}

	public static void SetToggle(bool on)
	{
		TOGGLEKEYS s = new TOGGLEKEYS
		{
			cbSize = (uint)Marshal.SizeOf<TOGGLEKEYS>()
		};
		try
		{
			SystemParametersInfo(52u, s.cbSize, ref s, 0u);
			s.dwFlags = (on ? (s.dwFlags | 1 | 2) : (s.dwFlags & 0xFFFFFFFEu));
			SystemParametersInfo(53u, s.cbSize, ref s, 3u);
		}
		catch (Exception ex)
		{
			Logger.Log("ToggleKeys set: " + ex.Message);
		}
	}

	public static bool FilterOn()
	{
		FILTERKEYS s = new FILTERKEYS
		{
			cbSize = (uint)Marshal.SizeOf<FILTERKEYS>()
		};
		try
		{
			SystemParametersInfo(50u, s.cbSize, ref s, 0u);
		}
		catch
		{
		}
		return (s.dwFlags & 1) != 0;
	}

	public static void SetFilter(bool on)
	{
		FILTERKEYS s = new FILTERKEYS
		{
			cbSize = (uint)Marshal.SizeOf<FILTERKEYS>()
		};
		try
		{
			SystemParametersInfo(50u, s.cbSize, ref s, 0u);
			s.dwFlags = (on ? (s.dwFlags | 1 | 2) : (s.dwFlags & 0xFFFFFFFEu));
			SystemParametersInfo(51u, s.cbSize, ref s, 3u);
		}
		catch (Exception ex)
		{
			Logger.Log("FilterKeys set: " + ex.Message);
		}
	}
}
