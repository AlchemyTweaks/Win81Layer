#nullable enable

using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace Win81Layer;

// Zero-cost exit notification for one process: a thread-pool wait on its process handle (no polling), which also
// yields the exact exit time and exit code. Used by the supervisor for the shell it launched, by the shell for its
// supervisor, and by DwmWatch for dwm.exe. Read-only: it only ever asks for SYNCHRONIZE + limited query access.
internal sealed class ProcessExitWatch : IDisposable
{
	private const uint Synchronize = 0x00100000u;

	private const uint ProcessQueryLimitedInformation = 0x1000u;

	private sealed class ProcessWaitHandle : WaitHandle
	{
		public ProcessWaitHandle(nint handle)
		{
			SafeWaitHandle = new SafeWaitHandle(handle, ownsHandle: false);
		}
	}

	private readonly object _gate = new object();

	private nint _handle;

	private ProcessWaitHandle? _wait;

	private RegisteredWaitHandle? _registration;

	private Action<ProcessExitWatch>? _onExit;

	private int _fired;

	public int Pid { get; }

	public uint ExitCode { get; private set; }

	public DateTime ExitUtc { get; private set; }

	public DateTime StartUtc { get; private set; }

	public bool IsFinished => Volatile.Read(ref _fired) != 0;

	private ProcessExitWatch(nint handle, int pid, Action<ProcessExitWatch> onExit)
	{
		_handle = handle;
		Pid = pid;
		_onExit = onExit;
	}

	// Takes ownership of processHandle (it must carry SYNCHRONIZE and query access); closed when the watch ends.
	internal static ProcessExitWatch? Attach(nint processHandle, int pid, Action<ProcessExitWatch> onExit)
	{
		if (processHandle == IntPtr.Zero)
		{
			return null;
		}
		ProcessExitWatch w = new ProcessExitWatch(processHandle, pid, onExit);
		try
		{
			if (GetProcessTimes(processHandle, out long created, out _, out _, out _) && created > 0)
			{
				w.StartUtc = DateTime.FromFileTimeUtc(created);
			}
			w._wait = new ProcessWaitHandle(processHandle);
			w._registration = ThreadPool.RegisterWaitForSingleObject(w._wait, delegate { w.Fire(); }, null, Timeout.Infinite, executeOnlyOnce: true);
			return w;
		}
		catch
		{
			w.Dispose();
			return null;
		}
	}

	internal static ProcessExitWatch? Open(int pid, Action<ProcessExitWatch> onExit, out string error)
	{
		error = "";
		if (pid <= 0)
		{
			error = "no pid";
			return null;
		}
		nint h = OpenProcess(Synchronize | ProcessQueryLimitedInformation, false, (uint)pid);
		if (h == IntPtr.Zero)
		{
			error = new Win32Exception(Marshal.GetLastWin32Error()).Message;
			return null;
		}
		ProcessExitWatch? w = Attach(h, pid, onExit);
		if (w == null)
		{
			error = "wait registration failed";
		}
		return w;
	}

	private void Fire()
	{
		if (Interlocked.Exchange(ref _fired, 1) != 0)
		{
			return;
		}
		Action<ProcessExitWatch>? cb;
		lock (_gate)
		{
			nint h = _handle;
			if (h != IntPtr.Zero)
			{
				if (GetExitCodeProcess(h, out uint code))
				{
					ExitCode = code;
				}
				ExitUtc = GetProcessTimes(h, out _, out long exited, out _, out _) && exited > 0 ? DateTime.FromFileTimeUtc(exited) : DateTime.UtcNow;
			}
			cb = _onExit;
		}
		try
		{
			cb?.Invoke(this);
		}
		catch
		{
		}
		Dispose();
	}

	public void Dispose()
	{
		lock (_gate)
		{
			_onExit = null;
			try { _registration?.Unregister(null); } catch { }
			_registration = null;
			try { _wait?.Dispose(); } catch { }
			_wait = null;
			if (_handle != IntPtr.Zero)
			{
				CloseHandle(_handle);
				_handle = IntPtr.Zero;
			}
		}
	}

	[DllImport("kernel32.dll", SetLastError = true)]
	private static extern nint OpenProcess(uint access, bool inherit, uint pid);

	[DllImport("kernel32.dll")]
	private static extern bool GetExitCodeProcess(nint h, out uint code);

	[DllImport("kernel32.dll")]
	private static extern bool GetProcessTimes(nint h, out long creation, out long exit, out long kernel, out long user);

	[DllImport("kernel32.dll")]
	private static extern bool CloseHandle(nint h);
}

// Logs DWM restarts: the exit (exact time + exit code) of this session's dwm.exe through a process-handle wait, then
// the new dwm.exe. WM_DWMCOMPOSITIONCHANGED (from SessionWatch) triggers a re-check too, which is the only signal
// left if the dwm.exe handle cannot be opened. No timers while DWM is healthy.
internal sealed class DwmWatch : IDisposable
{
	private readonly object _gate = new object();

	private readonly Func<int> _find;

	private readonly Action<string> _log;

	private readonly Action<string, string>? _critical;

	private ProcessExitWatch? _watch;

	private Timer? _poll;

	private int _pid;

	private int _exitedPid;

	private long _exitTick;

	private int _attempts;

	private bool _disposed;

	public event Action<int, int>? Restarted;   // old pid, new pid

	public int Pid => _pid;

	public bool Watching => _watch != null;

	public int RestartsSeen;

	public int PollIntervalMs = 500;

	public int MaxPolls = 120;

	public DwmWatch(Func<int>? find = null, Action<string>? log = null, Action<string, string>? critical = null)
	{
		_find = find ?? FindDwmInSession;
		_log = log ?? ExitForensics.Log;
		_critical = critical;
	}

	public void Start()
	{
		lock (_gate)
		{
			int pid = SafeFind();
			if (pid <= 0)
			{
				_log("DWM process not found in this session");
				return;
			}
			Arm(pid, announce: true);
		}
	}

	// Composition change (or any other hint): if dwm.exe changed while we were not watching it, log the restart.
	public void Check(string why)
	{
		lock (_gate)
		{
			if (_disposed || _poll != null)
			{
				return;   // an exit is already being followed up
			}
			int pid = SafeFind();
			if (pid <= 0 || (pid == _pid && _watch != null))
			{
				return;
			}
			int old = _pid;
			if (old != 0 && pid != old)
			{
				RestartsSeen++;
				_log($"DWM restarted: pid {old} -> {pid} (noticed via {why})");
				Arm(pid, announce: false);
				RaiseRestarted(old, pid);
				return;
			}
			Arm(pid, announce: false);
		}
	}

	private int SafeFind()
	{
		try
		{
			return _find();
		}
		catch
		{
			return 0;
		}
	}

	private void Arm(int pid, bool announce)
	{
		_watch?.Dispose();
		_watch = null;
		_pid = pid;
		ProcessExitWatch? w = ProcessExitWatch.Open(pid, OnExit, out string err);
		if (w == null)
		{
			_log($"DWM pid {pid}: exit watch unavailable ({err}); restarts are noticed through WM_DWMCOMPOSITIONCHANGED only");
			return;
		}
		_watch = w;
		if (announce)
		{
			_log($"watching DWM pid {pid}");
		}
	}

	private void OnExit(ProcessExitWatch w)
	{
		lock (_gate)
		{
			if (_disposed || w != _watch)
			{
				return;
			}
			_watch = null;
			_exitedPid = w.Pid;
			_exitTick = Environment.TickCount64;
			_attempts = 0;
			string detail = $"DWM pid {w.Pid} exited at {w.ExitUtc.ToLocalTime():HH:mm:ss.fff} with {ExitForensics.DescribeExitCode(w.ExitCode)}";
			if (_critical != null)
			{
				_critical("dwm-exited", detail);
			}
			else
			{
				_log(detail);
			}
			_poll = new Timer(Poll, null, PollIntervalMs, Timeout.Infinite);
		}
	}

	private void Poll(object? state)
	{
		int old = 0;
		int now = 0;
		lock (_gate)
		{
			if (_disposed || _poll == null)
			{
				return;
			}
			int pid = SafeFind();
			if (pid > 0 && pid != _exitedPid)
			{
				_poll.Dispose();
				_poll = null;
				RestartsSeen++;
				_log($"DWM restarted: new pid {pid}, {Environment.TickCount64 - _exitTick} ms after pid {_exitedPid} exited");
				old = _exitedPid;
				now = pid;
				Arm(pid, announce: false);
			}
			else if (++_attempts >= MaxPolls)
			{
				_poll.Dispose();
				_poll = null;
				_log($"no new DWM process {(long)MaxPolls * PollIntervalMs / 1000} s after pid {_exitedPid} exited; WM_DWMCOMPOSITIONCHANGED will re-arm the watch");
				_pid = 0;
			}
			else
			{
				_poll.Change(PollIntervalMs, Timeout.Infinite);
			}
		}
		if (now != 0)
		{
			RaiseRestarted(old, now);
		}
	}

	private void RaiseRestarted(int old, int now)
	{
		try
		{
			Restarted?.Invoke(old, now);
		}
		catch
		{
		}
	}

	internal static int FindDwmInSession()
	{
		int session;
		try
		{
			session = Process.GetCurrentProcess().SessionId;
		}
		catch
		{
			return 0;
		}
		int found = 0;
		Process[] all = Process.GetProcessesByName("dwm");
		foreach (Process p in all)
		{
			try
			{
				if (found == 0 && p.SessionId == session)
				{
					found = p.Id;
				}
			}
			catch
			{
			}
			p.Dispose();
		}
		return found;
	}

	public void Dispose()
	{
		lock (_gate)
		{
			_disposed = true;
			_watch?.Dispose();
			_watch = null;
			_poll?.Dispose();
			_poll = null;
		}
	}
}
