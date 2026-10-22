#nullable enable

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;

namespace Win81Layer;

// The suspension's pass gate: Run executes a pass only while not blocked and while the claim is held, all under the
// gate lock; Block takes the same lock, so it waits for a pass in flight and no pass can start after it. Kept as a
// small class so its protocol is testable with a private lock and a fake claim (--perfprobe unit).
internal sealed class SuspensionGuard
{
	private readonly object _gate;

	private readonly Func<bool> _held;

	private bool _blocked;

	internal SuspensionGuard(object gate, Func<bool> held)
	{
		_gate = gate;
		_held = held;
	}

	internal bool Blocked
	{
		get
		{
			lock (_gate)
			{
				return _blocked;
			}
		}
	}

	internal void Block()
	{
		lock (_gate)
		{
			_blocked = true;
		}
	}

	internal void Unblock()
	{
		lock (_gate)
		{
			_blocked = false;
		}
	}

	internal T? Run<T>(Func<T> pass) where T : class
	{
		lock (_gate)
		{
			if (_blocked || !_held())
			{
				return null;
			}
			return pass();
		}
	}
}

// Keeps the suspended native shell hosts dormant without touching the UI thread. Replaces the UI-thread
// ReassertSuspension on the taskbar's 45 s tick (3 by-name process enumerations + a thread list per host + a trim of
// every host every time). A pass is one NtQuerySystemInformation snapshot parsed in place on a pool thread: a host that
// is running (respawned or freshly spun up) and not in the foreground is suspended and trimmed once; hosts that are
// already suspended are left alone. Each suspended host's exit is watched (a SYNCHRONIZE handle), so a respawn is
// caught about 2 s after the old host goes away; the shell clock adds slow backstop passes. Every pass runs under
// NativeShell.SuspensionGate and only while this session holds the suspension and no resume is pending, so a resume on
// exit can never be followed by a late re-suspend. Never a kill (see NativeShell.ReassertSuspension's history).
internal static class NativeHostWarden
{
	private static readonly object PassGate = new object();

	private static readonly object WaitGate = new object();

	private static readonly SystemProcessSnapshot Snap = new SystemProcessSnapshot();

	private static readonly SystemProcessSnapshot.HostInfo[] Found = new SystemProcessSnapshot.HostInfo[16];

	// Each watch owns its process handle through a SafeWaitHandle: the registered wait holds a reference to it until the
	// wait thread has dropped the wait, so disposing it after Unregister never closes a handle that thread still waits on.
	private static readonly Dictionary<uint, (Microsoft.Win32.SafeHandles.SafeWaitHandle Handle, RegisteredWaitHandle Wait)> ExitWaits = new Dictionary<uint, (Microsoft.Win32.SafeHandles.SafeWaitHandle, RegisteredWaitHandle)>();

	private static int _queued;

	private static long _lastPassMs = long.MinValue / 2;

	internal static long LastPassMs => Interlocked.Read(ref _lastPassMs);

	internal static int PassCount;

	// Queue one background pass (coalesced: a pass already queued covers this request).
	internal static void RequestPass(string reason)
	{
		if (PerfProbe.Active)
		{
			return;   // the probe never acts on native hosts (it uses DryRun)
		}
		if (Interlocked.Exchange(ref _queued, 1) != 0)
		{
			return;
		}
		ThreadPool.UnsafeQueueUserWorkItem(delegate
		{
			Volatile.Write(ref _queued, 0);
			RunPass(reason);
		}, null);
	}

	// A pass after the given delay (respawn follow-ups).
	internal static void RequestPassAfter(int delayMs, string reason)
	{
		if (PerfProbe.Active)
		{
			return;
		}
		Timer? t = null;
		t = new Timer(delegate
		{
			try { t?.Dispose(); } catch { }
			RequestPass(reason);
		}, null, delayMs, Timeout.Infinite);
	}

	// Synchronous pass on the caller's thread (kept for callers that need the result before they continue).
	internal static void RunPass(string reason)
	{
		lock (PassGate)
		{
			Interlocked.Exchange(ref _lastPassMs, Environment.TickCount64);
			Interlocked.Increment(ref PassCount);
			try
			{
				List<uint>? watch = NativeShell.WardenPassLocked(Snap, Found, reason);
				if (watch != null)
				{
					foreach (uint pid in watch)
					{
						WatchExit(pid);
					}
				}
			}
			catch (Exception ex)
			{
				Logger.Log("Native host warden pass failed: " + ex.Message);
			}
		}
	}

	// Probe only: parse a snapshot and report which hosts a pass would act on. Opens no handle to any process.
	internal static (int Hosts, int Running, int Suspended, double Ms) DryRun()
	{
		long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
		int hosts = 0;
		int running = 0;
		int suspended = 0;
		lock (PassGate)
		{
			if (Snap.Refresh())
			{
				hosts = Snap.FindProcesses(NativeShell.HostNames, Found);
				for (int i = 0; i < hosts; i++)
				{
					if (Found[i].SuspendedThreads > 0)
					{
						suspended++;
					}
					else
					{
						running++;
					}
				}
			}
		}
		return (hosts, running, suspended, System.Diagnostics.Stopwatch.GetElapsedTime(t0).TotalMilliseconds);
	}

	private static void WatchExit(uint pid)
	{
		lock (WaitGate)
		{
			if (ExitWaits.ContainsKey(pid))
			{
				return;
			}
			nint h = OpenProcess(Synchronize, false, pid);
			if (h == IntPtr.Zero)
			{
				return;
			}
			Microsoft.Win32.SafeHandles.SafeWaitHandle safe = new Microsoft.Win32.SafeHandles.SafeWaitHandle(h, ownsHandle: true);
			try
			{
				ProcessWaitHandle wh = new ProcessWaitHandle(safe);
				RegisteredWaitHandle reg = ThreadPool.UnsafeRegisterWaitForSingleObject(wh, delegate
				{
					OnHostExited(pid);
				}, null, Timeout.Infinite, executeOnlyOnce: true);
				ExitWaits[pid] = (safe, reg);
			}
			catch
			{
				safe.Dispose();
			}
		}
	}

	private static void OnHostExited(uint pid)
	{
		ForgetWait(pid);
		// The OS usually brings a replacement up within a few seconds: look at 2 s and again at 10 s.
		RequestPassAfter(2000, "host exited");
		RequestPassAfter(10000, "host exited (+10s)");
	}

	private static void ForgetWait(uint pid)
	{
		lock (WaitGate)
		{
			if (ExitWaits.Remove(pid, out (Microsoft.Win32.SafeHandles.SafeWaitHandle Handle, RegisteredWaitHandle Wait) w))
			{
				Release(w);
			}
		}
	}

	// Resume path: drop every exit watch so nothing schedules a pass after the hosts were given back.
	internal static void CancelWatches()
	{
		lock (WaitGate)
		{
			foreach (KeyValuePair<uint, (Microsoft.Win32.SafeHandles.SafeWaitHandle Handle, RegisteredWaitHandle Wait)> kv in ExitWaits)
			{
				Release(kv.Value);
			}
			ExitWaits.Clear();
		}
	}

	// Unregister is asynchronous: the wait thread drops the wait later and only then releases its reference to the
	// handle. Dispose marks the handle for closing; the actual CloseHandle happens when the last reference is released.
	private static void Release((Microsoft.Win32.SafeHandles.SafeWaitHandle Handle, RegisteredWaitHandle Wait) w)
	{
		try { w.Wait.Unregister(null); } catch { }
		try { w.Handle.Dispose(); } catch { }
	}

	// A WaitHandle over the watch's SafeWaitHandle (which ExitWaits owns and disposes).
	private sealed class ProcessWaitHandle : WaitHandle
	{
		public ProcessWaitHandle(Microsoft.Win32.SafeHandles.SafeWaitHandle handle)
		{
			SafeWaitHandle = handle;
		}
	}

	private const uint Synchronize = 0x00100000u;

	[DllImport("kernel32.dll")]
	private static extern nint OpenProcess(uint access, bool inherit, uint pid);
}
