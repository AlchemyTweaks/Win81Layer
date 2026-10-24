#nullable enable

using System;
using System.Threading;
using System.Windows.Threading;

namespace Win81Layer;

// Exit forensics + session-end resilience for both processes (see ExitForensics, SessionWatch, WpfSessionGuard).
//
// Job objects, decided: the supervisor launches the shell WITHOUT CREATE_BREAKAWAY_FROM_JOB, and stays in the task's
// job itself. (1) The 2026-10-24 loss was a session teardown (LSM reason 23): every process of the session died, so
// leaving the job would have saved nothing. (2) The only in-product job teardown is an explicit Task Scheduler stop
// (End / schtasks /End / logoff): the task has no time limit and IgnoreNew, so the scheduler never stops it on its
// own, and taking the shell down with an explicit stop matches that intent. (3) CreateProcessWithTokenW documents a
// fixed set of creation flags that does not include CREATE_BREAKAWAY_FROM_JOB, and the job assignment is made by the
// Secondary Logon service; whether breakaway would apply cannot be verified offscreen, so changing the live launch
// path would be an untestable risk with no demonstrated benefit. Instead both processes log their job and its limit
// flags at start (ExitForensics.JobSummary) and the supervisor logs the shell's exit code, so a future job stop is
// named (0x41306 / exit code 1 with a still-existing session) and the decision can be revisited with facts.
public partial class App
{
	// ---------------------------------------------------------------- supervisor

	private static readonly SessionEndPolicy.SupervisorState _supSession = new SessionEndPolicy.SupervisorState();

	private static SessionWatch? _supSessionWatch;

	private static DwmWatch? _supDwmWatch;

	private static ProcessExitWatch? _supShellWatch;

	private static int _supShellWatchPid;

	private static bool _supHoldLogged;

	private static void SupervisorForensicsStart()
	{
		try
		{
			// Nothing here may delay the first shell launch at logon: the record, the verdict and the watches are set up on
			// a worker; only the configuration and the process-wide handlers are in place before the loop starts.
			ExitForensics.Configure(ExitForensics.RoleSupervisor);
			ExitForensics.InstallProcessHandlers();
			bool ctrl = ExitForensics.InstallConsoleControlHandler();
			Thread init = new Thread(delegate ()
			{
				try
				{
					ExitForensics.Breath? previous = ExitForensics.ReadBreath(ExitForensics.RoleSupervisor);
					ExitForensics.WriteBreath(ExitForensics.StateRunning, "start", "supervisor started", onlyIfFirst: true);
					ExitForensics.ReportPrevious(previous);
					ExitForensics.Log($"supervisor pid {Environment.ProcessId}, session {ExitForensics.SessionId}; {ExitForensics.JobSummary()}; the shell is launched inside this job (no breakaway, see App.Forensics.cs); console control handler: {(ctrl ? "ok" : "unavailable")}");
					SupervisorWatchesStart();
				}
				catch (Exception ex)
				{
					ExitForensics.Log("init failed: " + ex.Message);
				}
			})
			{
				IsBackground = true,
				Name = "ForensicsInit"
			};
			init.Start();
		}
		catch (Exception ex)
		{
			try { Logger.Log("[forensics:supervisor] start failed: " + ex.Message); } catch { }
		}
	}

	private static void SupervisorWatchesStart()
	{
		try
		{
			SessionWatch watch = new SessionWatch(new SessionWatch.Options
			{
				Role = ExitForensics.RoleSupervisor,
				LogLock = false,
				LogDisplay = false,
				LogPower = false
			});
			watch.QueryEndSession += delegate(uint flags)
			{
				SessionEndPolicy.NoteQuery(_supSession, DateTime.UtcNow);
				ExitForensics.Critical("query-end-session", "WM_QUERYENDSESSION flags=" + SessionWatch.DescribeEndSessionFlags(flags), ExitForensics.StateRunning, logLine: false);
			};
			watch.EndSession += delegate(bool ending, uint flags)
			{
				bool shuttingDown = SessionEndPolicy.ShuttingDown();
				SessionEndPolicy.NoteEnd(_supSession, ending, flags, shuttingDown, DateTime.UtcNow);
				// Same reading as the shell's (SessionEndPolicy.DecideShell), so both records tell the same story.
				SessionEndPolicy.ShellAction meaning = SessionEndPolicy.DecideShell(ending, flags, shuttingDown);
				string state = meaning == SessionEndPolicy.ShellAction.ExitSessionEnding ? ExitForensics.StateSessionEnding : ExitForensics.StateRunning;
				string what = meaning switch
				{
					SessionEndPolicy.ShellAction.Continue => "end-session-cancelled",
					SessionEndPolicy.ShellAction.StayUpRestartManager => "restart-manager-close",
					SessionEndPolicy.ShellAction.StayUpNotShuttingDown => "end-session-ignored",
					_ => "end-session"
				};
				ExitForensics.Critical(what, $"WM_ENDSESSION({(ending ? "TRUE" : "FALSE")}) flags={SessionWatch.DescribeEndSessionFlags(flags)} -> {meaning}", state, logLine: false);
			};
			watch.CompositionChanged += delegate
			{
				_supDwmWatch?.Check("WM_DWMCOMPOSITIONCHANGED");
			};
			if (!watch.Start())
			{
				ExitForensics.Log("session watch window could not be created");
			}
			_supSessionWatch = watch;
			DwmWatch dwm = new DwmWatch(critical: delegate(string evt, string detail)
			{
				ExitForensics.Critical(evt, detail, ExitForensics.StateRunning, logLine: true);
			});
			dwm.Start();
			_supDwmWatch = dwm;
		}
		catch (Exception ex)
		{
			try { Logger.Log("[forensics:supervisor] start failed: " + ex.Message); } catch { }
		}
	}

	// true while a missing shell must NOT be relaunched because Windows is ending (or asked to end) the session. Every
	// hold expires by itself (SessionEndPolicy.Hold), so a spurious notification cannot stop supervision for good.
	private static bool SupervisorSessionHold()
	{
		string? hold = SessionEndPolicy.Hold(_supSession, DateTime.UtcNow, SessionEndPolicy.ShuttingDown());
		if (hold == null)
		{
			if (_supHoldLogged)
			{
				_supHoldLogged = false;
				Logger.Log("Supervisor: session-end hold is over - supervising again.");
			}
			return false;
		}
		if (!_supHoldLogged)
		{
			_supHoldLogged = true;
			Logger.Log("Supervisor: shell is down but " + hold + " - not relaunching for now.");
		}
		return true;
	}

	// Takes ownership of processHandle (zero = open by pid).
	private static void SupervisorWatchShell(nint processHandle, int pid)
	{
		try
		{
			ProcessExitWatch? old = Interlocked.Exchange(ref _supShellWatch, null);
			old?.Dispose();
			_supShellWatchPid = pid;
			ProcessExitWatch? w = processHandle != IntPtr.Zero
				? ProcessExitWatch.Attach(processHandle, pid, OnSupervisedShellExited)
				: ProcessExitWatch.Open(pid, OnSupervisedShellExited, out _);
			if (w != null && !w.IsFinished)
			{
				Interlocked.CompareExchange(ref _supShellWatch, w, null);
			}
		}
		catch
		{
		}
	}

	// A healthy shell the supervisor did not launch itself (a --healed self-restart) gets an exit watch too.
	private static void SupervisorEnsureShellWatch()
	{
		try
		{
			ProcessExitWatch? current = Volatile.Read(ref _supShellWatch);
			if (current != null && !current.IsFinished)
			{
				return;
			}
			int pid = Heartbeat.ReadSectionPid();
			if (pid > 0 && pid != _supShellWatchPid && pid != Environment.ProcessId)
			{
				SupervisorWatchShell(IntPtr.Zero, pid);
			}
		}
		catch
		{
		}
	}

	private static void OnSupervisedShellExited(ProcessExitWatch w)
	{
		Interlocked.CompareExchange(ref _supShellWatch, null, w);
		try
		{
			double beatAge = -1.0;
			long beat = ReadHeartbeat();
			if (beat > 0L)
			{
				try { beatAge = Math.Max(0.0, (w.ExitUtc - new DateTime(beat, DateTimeKind.Utc)).TotalSeconds); } catch { }
			}
			string line = ExitForensics.FormatShellExit(w.Pid, w.ExitCode, w.ExitUtc, w.StartUtc, beatAge, ExitForensics.ReadBreath(ExitForensics.RoleShell));
			string? hold = SessionEndPolicy.Hold(_supSession, DateTime.UtcNow, SessionEndPolicy.ShuttingDown());
			if (hold != null)
			{
				line += "; " + hold;
			}
			ExitForensics.Critical("shell-exited", line + "; " + ExitForensics.JobSummary(), ExitForensics.StateRunning, logLine: true);
		}
		catch
		{
		}
	}

	// ---------------------------------------------------------------- shell

	private SessionWatch? _sessionWatch;

	private DwmWatch? _dwmWatch;

	private ProcessExitWatch? _supervisorWatch;

	// Set only on the WPF fallback path (guard not installed): WPF is exiting the shell on a mere query, so the
	// clean-exit marker is withheld and the supervisor can bring the shell back if the session survives.
	private volatile bool _wpfSessionShutdown;

	private volatile string? _exitReason;

	private readonly ManualResetEventSlim _cleanupDone = new ManualResetEventSlim(false);

	private void ShellForensicsStart()
	{
		try
		{
			// On the UI thread only what must be in place before the first session message can arrive; nothing waits.
			ExitForensics.Configure(ExitForensics.RoleShell);
			string guard;
			bool guarded = WpfSessionGuard.TryInstall(this, out guard);
			bool ctrl = ExitForensics.InstallConsoleControlHandler();
			SessionWatch watch = new SessionWatch(new SessionWatch.Options { Role = ExitForensics.RoleShell });
			watch.QueryEndSession += OnWindowsQueryEndSession;
			watch.EndSession += OnWindowsEndSession;
			watch.Stopped += ShellSessionWatchLost;
			watch.CompositionChanged += delegate
			{
				_dwmWatch?.Check("WM_DWMCOMPOSITIONCHANGED");
			};
			watch.Start(0);
			_sessionWatch = watch;
			bool managed = _managed;
			// The rest touches the disk and the process table: off the UI thread (cold boot), never blocking first paint.
			Thread init = new Thread(delegate ()
			{
				try
				{
					// Read the previous shell's record before writing ours. If an early exit already wrote ours, the
					// read returns this process's own record (ReportPrevious skips it) and the "start" write is skipped.
					ExitForensics.Breath? previous = ExitForensics.ReadBreath(ExitForensics.RoleShell);
					ExitForensics.WriteBreath(ExitForensics.StateRunning, "start", managed ? "managed shell started" : "shell started", onlyIfFirst: true);
					ExitForensics.ReportPrevious(previous);
					bool watchOk = watch.WaitReady(3000) && !watch.IsStopped;
					if (!watchOk)
					{
						ShellSessionWatchLost("not ready 3 s after start");
					}
					ExitForensics.Log($"shell pid {Environment.ProcessId}, session {ExitForensics.SessionId}, managed={managed}; {ExitForensics.JobSummary()}; WPF end-session guard: {(guarded ? guard : "UNAVAILABLE (" + guard + ") - WPF exits on WM_QUERYENDSESSION, stand-down marker withheld on that path")}; session watch: {(watchOk ? "ok" : "unavailable (the guard is removed, so the WPF fallback applies)")}; console control handler: {(ctrl ? "ok" : "unavailable")}");
					DwmWatch dwm = new DwmWatch(critical: delegate(string evt, string detail)
					{
						ExitForensics.Critical(evt, detail, ExitForensics.StateRunning, logLine: true);
					});
					dwm.Start();
					_dwmWatch = dwm;
					if (managed)
					{
						WatchSupervisor();
					}
				}
				catch (Exception ex)
				{
					ExitForensics.Log("init failed: " + ex.Message);
				}
			})
			{
				IsBackground = true,
				Name = "ForensicsInit",
				Priority = ThreadPriority.BelowNormal
			};
			init.Start();
		}
		catch (Exception ex)
		{
			try { Logger.Log("[forensics:shell] start failed: " + ex.Message); } catch { }
		}
	}

	private void WatchSupervisor()
	{
		ExitForensics.Breath? sup = ExitForensics.ReadBreath(ExitForensics.RoleSupervisor);
		if (sup == null || sup.State == ExitForensics.StateExited)
		{
			ExitForensics.Log("no live supervisor record to watch");
			return;
		}
		ExitForensics.AliveState alive = ExitForensics.ProcessAliveWithStart(sup.Pid, sup.ProcessStartTicks);
		if (alive != ExitForensics.AliveState.Alive && alive != ExitForensics.AliveState.Unknown)
		{
			ExitForensics.Log($"supervisor pid {sup.Pid} from its record is not running ({alive})");
			return;
		}
		_supervisorWatch = ProcessExitWatch.Open(sup.Pid, delegate(ProcessExitWatch w)
		{
			ExitForensics.Critical("supervisor-exited", $"supervisor pid {w.Pid} exited at {w.ExitUtc.ToLocalTime():HH:mm:ss.fff} with {ExitForensics.DescribeExitCode(w.ExitCode)}; the shell keeps running, unsupervised until the next logon", ExitForensics.StateRunning, logLine: true);
		}, out string err);
		ExitForensics.Log(_supervisorWatch != null ? $"watching supervisor pid {sup.Pid}" : $"cannot watch supervisor pid {sup.Pid}: {err}");
	}

	private int _guardFallback;

	// The guard is only safe while the SessionWatch handles WM_ENDSESSION. Without it (the window could not be created,
	// or its loop ended) take the guard out so WPF's own exit applies again: it flushes Start in Cleanup and withholds
	// the stand-down marker (OnSessionEnding), so the supervisor brings the shell back if the session goes on.
	internal void ShellSessionWatchLost(string why)
	{
		if (Interlocked.Exchange(ref _guardFallback, 1) != 0)
		{
			return;
		}
		if (!WpfSessionGuard.Installed)
		{
			ExitForensics.Log($"session watch unavailable ({why}); the WPF end-session guard is not installed, so WPF's own end-session exit applies");
			return;
		}
		try
		{
			Dispatcher.BeginInvoke(new Action(delegate
			{
				bool removed = WpfSessionGuard.Remove();
				ExitForensics.Log(removed
					? $"session watch unavailable ({why}): WPF end-session guard removed, so WPF's own exit on WM_QUERYENDSESSION applies (Start flushed, stand-down marker withheld)"
					: $"session watch unavailable ({why}): the WPF end-session guard could not be removed");
			}));
		}
		catch (Exception ex)
		{
			ExitForensics.Log("guard fallback failed: " + ex.Message);
		}
	}

	private void OnWindowsQueryEndSession(uint flags)
	{
		string next = WpfSessionGuard.Installed ? "staying up until WM_ENDSESSION decides" : "WPF fallback: WPF exits the shell now";
		ExitForensics.Critical("query-end-session", "WM_QUERYENDSESSION flags=" + SessionWatch.DescribeEndSessionFlags(flags) + "; " + next, ExitForensics.StateRunning, logLine: false);
	}

	// On the SessionWatch thread. WM_ENDSESSION(TRUE) may be the last message this process handles: Windows can
	// terminate it as soon as this returns, so everything that must survive is done synchronously here (bounded).
	internal void OnWindowsEndSession(bool ending, uint flags)
	{
		bool shuttingDown = SessionEndPolicy.ShuttingDown();
		SessionEndPolicy.ShellAction action = SessionEndPolicy.DecideShell(ending, flags, shuttingDown);
		string detail = $"WM_ENDSESSION({(ending ? "TRUE" : "FALSE")}) flags={SessionWatch.DescribeEndSessionFlags(flags)} SM_SHUTTINGDOWN={(shuttingDown ? 1 : 0)} -> {action}";
		switch (action)
		{
		case SessionEndPolicy.ShellAction.Continue:
			ExitForensics.Critical("end-session-cancelled", detail + ": the session goes on, the shell keeps running", ExitForensics.StateRunning, logLine: true);
			break;
		case SessionEndPolicy.ShellAction.StayUpRestartManager:
			ExitForensics.Critical("restart-manager-close", detail + ": an installer asked running apps to close; flushing the Start layout, the shell stays up (if it is terminated, no stand-down marker exists and the supervisor relaunches it)", ExitForensics.StateRunning, logLine: true);
			FlushStateForSessionEnd(2000);   // Restart Manager may terminate the shell next: nothing pending may be lost
			break;
		case SessionEndPolicy.ShellAction.StayUpNotShuttingDown:
			ExitForensics.Critical("end-session-ignored", detail + ": this session is not shutting down, so it is not a real session end; flushing the Start layout, the shell keeps running", ExitForensics.StateRunning, logLine: true);
			FlushStateForSessionEnd(2000);
			break;
		case SessionEndPolicy.ShellAction.ExitSessionEnding:
			ExitForensics.Critical("end-session", detail + ": the session is ending - fast exit (native shell left alone, it is ending too)", ExitForensics.StateSessionEnding, logLine: true);
			_sessionEnding = true;
			if (_exitReason == null)
			{
				_exitReason = "Windows session ending";
			}
			try
			{
				Dispatcher.BeginInvoke(new Action(delegate { Shutdown(); }));
			}
			catch
			{
			}
			_cleanupDone.Wait(3500);
			break;
		}
	}

	private void FlushStateForSessionEnd(int timeoutMs)
	{
		try
		{
			DispatcherOperation op = Dispatcher.BeginInvoke(new Action(delegate
			{
				try { _startScreen?.CommitActiveGroupEditAndFlush(); } catch (Exception ex) { Logger.Log("session-end flush failed: " + ex.Message); }
			}));
			op.Wait(TimeSpan.FromMilliseconds(timeoutMs));
		}
		catch
		{
		}
	}

	// QA hook (--exitforensicstest): proves that without the guard WPF routes WM_QUERYENDSESSION into OnSessionEnding.
	internal static volatile bool CancelSessionEndingForTest;

	internal static int SessionEndingCallsForTest;

	internal bool SessionEndingForTest => _sessionEnding;
}
