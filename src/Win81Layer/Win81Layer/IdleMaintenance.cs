#nullable enable

using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace Win81Layer;

// When the user last used a shell surface (Start, the Windows 7 menu, Action Center, a taskbar click, a surface closing).
// Idle maintenance waits for the shell to be unused before it trims, so it never evicts pages the user is about to touch.
internal static class ShellActivity
{
	private static long _lastMs = Environment.TickCount64;

	internal static long LastMs => Interlocked.Read(ref _lastMs);

	internal static void Stamp()
	{
		Interlocked.Exchange(ref _lastMs, Environment.TickCount64);
	}
}

// The trim decision, kept free of clocks and Win32 so it can be tested with a fake clock (--perfprobe unit).
// One trim per idle episode: an episode starts with each shell interaction. It trims when no shell surface is open and
// either the input has been idle for 2 minutes or the shell has not been used for 5 minutes, at most once per 10
// minutes. Lock and display-off trim once (2 minutes apart at most, so lock/unlock flapping cannot thrash). Real memory
// pressure trims at most once per 2 minutes.
internal sealed class IdleTrimPolicy
{
	internal const long InputIdleMs = 120000L;

	internal const long ShellIdleMs = 300000L;

	internal const long MinSpacingMs = 600000L;

	internal const long PressureSpacingMs = 120000L;

	internal const long AsleepSpacingMs = 120000L;

	internal enum Reason
	{
		None,
		Idle,
		Lock,
		DisplayOff,
		Pressure
	}

	private long _lastTrimMs = long.MinValue / 2;

	private long _episodeStamp = long.MinValue;

	private bool _episodeTrimmed;

	internal long LastTrimMs => _lastTrimMs;

	internal Reason Evaluate(long now, long inputIdleMs, long lastShellActivityMs, bool surfaceOpen, bool pressure)
	{
		if (lastShellActivityMs != _episodeStamp)
		{
			_episodeStamp = lastShellActivityMs;
			_episodeTrimmed = false;
		}
		if (surfaceOpen)
		{
			return Reason.None;
		}
		if (pressure && now - _lastTrimMs >= PressureSpacingMs)
		{
			return Reason.Pressure;
		}
		if (_episodeTrimmed || now - _lastTrimMs < MinSpacingMs)
		{
			return Reason.None;
		}
		if (inputIdleMs >= InputIdleMs || now - lastShellActivityMs >= ShellIdleMs)
		{
			return Reason.Idle;
		}
		return Reason.None;
	}

	internal Reason EvaluateAsleep(long now, bool surfaceOpen, Reason why)
	{
		if (surfaceOpen || now - _lastTrimMs < AsleepSpacingMs)
		{
			return Reason.None;
		}
		return why;
	}

	internal void Trimmed(long now)
	{
		_lastTrimMs = now;
		_episodeTrimmed = true;
	}
}

// Owns the launcher's own working-set trims (replaces the fixed 45 s EmptyWorkingSet on the taskbar tick, which blocked
// the UI thread 16-39 ms each time and made every Start/Action Center open after idle refault its pages). Driven by the
// shell clock on the UI thread; the trim itself (and an optional compacting GC) runs on the thread pool.
internal sealed class IdleMaintenance : IDisposable
{
	private readonly IdleTrimPolicy _policy = new IdleTrimPolicy();

	private readonly Func<bool> _surfaceOpen;

	private nint _lowMemory;

	private int _trimming;

	private int _logged;

	internal static int TrimCount;

	internal IdleMaintenance(Func<bool> surfaceOpen)
	{
		_surfaceOpen = surfaceOpen;
		try
		{
			_lowMemory = CreateMemoryResourceNotification(0);   // LowMemoryResourceNotification: signalled while memory is low
		}
		catch
		{
			_lowMemory = IntPtr.Zero;
		}
	}

	// Shell clock tick (UI thread).
	internal void Tick(long now, long inputIdleMs)
	{
		bool open;
		try
		{
			open = _surfaceOpen();
		}
		catch
		{
			open = true;
		}
		IdleTrimPolicy.Reason r = _policy.Evaluate(now, inputIdleMs, ShellActivity.LastMs, open, MemoryPressure());
		if (r != IdleTrimPolicy.Reason.None)
		{
			Trim(r, now, inputIdleMs);
		}
	}

	// Session lock or display off (UI thread): one trim, rate-limited.
	internal void OnAsleep(bool locked)
	{
		long now = Environment.TickCount64;
		bool open;
		try
		{
			open = _surfaceOpen();
		}
		catch
		{
			open = true;
		}
		IdleTrimPolicy.Reason r = _policy.EvaluateAsleep(now, open, locked ? IdleTrimPolicy.Reason.Lock : IdleTrimPolicy.Reason.DisplayOff);
		if (r != IdleTrimPolicy.Reason.None)
		{
			Trim(r, now, long.MaxValue);
		}
	}

	private void Trim(IdleTrimPolicy.Reason reason, long now, long inputIdleMs)
	{
		_policy.Trimmed(now);
		TaskbarWindow.StopStrayFrameHooks();
		// Item 11: one compacting collection per idle episode, only when nobody is at the keyboard or mouse (a blocking
		// GC also pauses the managed input hook) and the GC holds a fair amount of committed-but-free memory.
		bool compact = false;
		if (reason != IdleTrimPolicy.Reason.Pressure && inputIdleMs >= IdleTrimPolicy.InputIdleMs)
		{
			try
			{
				GCMemoryInfo gi = GC.GetGCMemoryInfo();
				compact = gi.TotalCommittedBytes - gi.HeapSizeBytes > 8L * 1024 * 1024;
			}
			catch
			{
			}
		}
		if (Interlocked.Exchange(ref _trimming, 1) != 0)
		{
			return;
		}
		Interlocked.Increment(ref TrimCount);
		if (_logged < 40)
		{
			_logged++;
			Logger.Log($"[idle] trim ({reason}{(compact ? ", compacting" : "")}) inputIdle={(inputIdleMs == long.MaxValue ? "-" : (inputIdleMs / 1000) + "s")}");
		}
		ThreadPool.UnsafeQueueUserWorkItem(delegate
		{
			try
			{
				if (compact)
				{
					GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true);
				}
				NativeShell.TrimSelf();
			}
			catch
			{
			}
			finally
			{
				Volatile.Write(ref _trimming, 0);
			}
		}, null);
	}

	private bool MemoryPressure()
	{
		try
		{
			if (_lowMemory != IntPtr.Zero && QueryMemoryResourceNotification(_lowMemory, out bool low) && low)
			{
				return true;
			}
			MEMORYSTATUSEX m = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
			return GlobalMemoryStatusEx(ref m) && m.dwMemoryLoad >= 85u;
		}
		catch
		{
			return false;
		}
	}

	public void Dispose()
	{
		if (_lowMemory != IntPtr.Zero)
		{
			CloseHandle(_lowMemory);
			_lowMemory = IntPtr.Zero;
		}
	}

	[StructLayout(LayoutKind.Sequential)]
	private struct MEMORYSTATUSEX
	{
		public uint dwLength;
		public uint dwMemoryLoad;
		public ulong ullTotalPhys;
		public ulong ullAvailPhys;
		public ulong ullTotalPageFile;
		public ulong ullAvailPageFile;
		public ulong ullTotalVirtual;
		public ulong ullAvailVirtual;
		public ulong ullAvailExtendedVirtual;
	}

	[DllImport("kernel32.dll")]
	private static extern nint CreateMemoryResourceNotification(int type);

	[DllImport("kernel32.dll")]
	private static extern bool QueryMemoryResourceNotification(nint handle, out bool state);

	[DllImport("kernel32.dll")]
	private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX buffer);

	[DllImport("kernel32.dll")]
	private static extern bool CloseHandle(nint handle);
}
