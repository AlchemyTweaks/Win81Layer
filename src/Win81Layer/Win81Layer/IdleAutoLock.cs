using System;
using System.Runtime.InteropServices;
using System.Windows.Threading;

namespace Win81Layer;

internal sealed class IdleAutoLock
{
	private struct LASTINPUTINFO
	{
		public uint cbSize;

		public uint dwTime;
	}

	private readonly DispatcherTimer _timer;

	private long _thresholdMs;

	public IdleAutoLock()
	{
		//IL_000a: Unknown result type (might be due to invalid IL or missing references)
		//IL_000f: Unknown result type (might be due to invalid IL or missing references)
		//IL_0023: Expected O, but got Unknown
		_timer = new DispatcherTimer((DispatcherPriority)4)
		{
			Interval = TimeSpan.FromSeconds(15L)
		};
		_timer.Tick += delegate
		{
			Check();
		};
	}

	public void SetMinutes(int minutes)
	{
		_thresholdMs = (long)Math.Max(0, minutes) * 60000L;
		if (_thresholdMs > 0)
		{
			_timer.Start();
		}
		else
		{
			_timer.Stop();
		}
	}

	private void Check()
	{
		try
		{
			if (_thresholdMs > 0 && IdleMs() >= _thresholdMs)
			{
				LockScreen.Show();
			}
		}
		catch (Exception ex)
		{
			Logger.Log("IdleAutoLock: " + ex.Message);
		}
	}

	[DllImport("user32.dll")]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static extern bool GetLastInputInfo(ref LASTINPUTINFO plii);

	[DllImport("kernel32.dll")]
	private static extern uint GetTickCount();

	private static long IdleMs()
	{
		LASTINPUTINFO lii = new LASTINPUTINFO
		{
			cbSize = (uint)Marshal.SizeOf<LASTINPUTINFO>()
		};
		if (!GetLastInputInfo(ref lii))
		{
			return 0L;
		}
		return GetTickCount() - lii.dwTime;
	}
}
