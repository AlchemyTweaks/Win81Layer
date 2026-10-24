#nullable enable

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;

namespace Win81Layer;

// --exitforensicstest <outDir>
//
// Offscreen QA of the exit forensics and the session-end resilience. Read-only by construction: App sets
// ReadOnlyDiagnostics before this runs (settings/state writes throw, the logger is silent), the last-breath records go
// to outDir\forensics-fixture only (the live forensics folder is refused), every message is sent to a window this
// process created itself (two SessionWatch windows and their threads, the WPF parking window, two offscreen WPF
// windows), the only processes it starts are cmd.exe / PING.EXE children that exit or that it ends itself by pid
// (single processes, no tree walk), and the only things it reads from the live system are the session list, this
// session's logon time, its own job and the pid of this session's dwm.exe (opened for SYNCHRONIZE + limited query
// only, then closed).
// Results: exitforensicstest-results.txt / -transcript.txt / -report.json in outDir. Exits by itself (hard deadline).
internal static class ExitForensicsDiagnostics
{
	private const int HardDeadlineMs = 120000;

	private static readonly List<(string Name, bool Pass, string Detail)> Checks = new List<(string, bool, string)>();

	private static readonly List<string> Transcript = new List<string>();

	private static readonly List<string> Sink = new List<string>();

	private static readonly List<Process> Children = new List<Process>();

	private static readonly long StartTick = Environment.TickCount64;

	private static string _outDir = "";

	private static string _fixture = "";

	private static volatile int _fakeDwm;

	public static void Begin(System.Windows.Application app, string[] args)
	{
		app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
		_outDir = ArgAfter(args, "--exitforensicstest") ?? Path.Combine(Path.GetTempPath(), "win81layer-exitforensicstest");
		Directory.CreateDirectory(_outDir);
		Thread deadline = new Thread(delegate ()
		{
			Thread.Sleep(HardDeadlineMs);
			try { File.AppendAllText(Path.Combine(_outDir, "exitforensicstest-hardexit.txt"), DateTime.Now.ToString("O") + " hard deadline\r\n"); } catch { }
			KillChildren();
			try { WriteResults(); } catch { }
			Environment.Exit(3);
		})
		{
			IsBackground = true,
			Name = "ExitForensicsTest.Deadline"
		};
		deadline.Start();
		app.Dispatcher.BeginInvoke((Action)delegate
		{
			Thread runner = new Thread(delegate ()
			{
				int code = 1;
				try
				{
					Run(app);
					code = Checks.Count > 0 && Checks.All(c => c.Pass) ? 0 : 1;
				}
				catch (Exception ex)
				{
					Check("harness.no-exception", false, ex.ToString());
				}
				finally
				{
					KillChildren();
					try { WriteResults(); } catch { }
				}
				app.Dispatcher.BeginInvoke((Action)(() => app.Shutdown(code)));
			})
			{
				IsBackground = true,
				Name = "ExitForensicsTest"
			};
			runner.SetApartmentState(ApartmentState.STA);
			runner.Start();
		}, DispatcherPriority.Loaded);
	}

	private static string? ArgAfter(string[] args, string name)
	{
		int i = Array.IndexOf(args, name);
		return i >= 0 && i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal) ? args[i + 1] : null;
	}

	private static void Check(string name, bool pass, string detail = "")
	{
		lock (Checks)
		{
			Checks.Add((name, pass, detail));
		}
		Trace((pass ? "PASS " : "FAIL ") + name + (detail.Length > 0 ? "  (" + detail + ")" : ""));
	}

	private static void Trace(string line)
	{
		string stamped = $"{(Environment.TickCount64 - StartTick) / 1000.0,7:0.000}s {line}";
		lock (Transcript)
		{
			Transcript.Add(stamped);
		}
	}

	private static void SinkAdd(string line)
	{
		lock (Sink)
		{
			Sink.Add(line);
		}
		Trace("  log: " + line);
	}

	private static bool SinkHas(string fragment)
	{
		lock (Sink)
		{
			return Sink.Any(s => s.IndexOf(fragment, StringComparison.Ordinal) >= 0);
		}
	}

	private static int SinkCount(string fragment)
	{
		lock (Sink)
		{
			return Sink.Count(s => s.IndexOf(fragment, StringComparison.Ordinal) >= 0);
		}
	}

	private static bool WaitFor(Func<bool> cond, int ms)
	{
		long end = Environment.TickCount64 + ms;
		while (Environment.TickCount64 < end)
		{
			if (cond())
			{
				return true;
			}
			Thread.Sleep(25);
		}
		return cond();
	}

	// A single long-running process (PING.EXE itself, no cmd.exe wrapper), so ending it by pid leaves nothing behind.
	private static Process StartWaiter() => StartChild("-n 40 127.0.0.1", "PING.EXE");

	private static Process StartChild(string cmdArgs, string exe = "cmd.exe")
	{
		Process p = Process.Start(new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, exe), cmdArgs)
		{
			UseShellExecute = false,
			CreateNoWindow = true,
			RedirectStandardOutput = false
		})!;
		lock (Children)
		{
			Children.Add(p);
		}
		return p;
	}

	// Our own children only, one pid each (no process-tree walk).
	private static void KillChildren()
	{
		lock (Children)
		{
			foreach (Process p in Children)
			{
				try
				{
					if (!p.HasExited)
					{
						p.Kill();
						p.WaitForExit(3000);
					}
				}
				catch
				{
				}
			}
		}
	}

	private static void Run(System.Windows.Application app)
	{
		_fixture = Path.Combine(_outDir, "forensics-fixture");
		try { if (Directory.Exists(_fixture)) { Directory.Delete(_fixture, recursive: true); } } catch { }
		Directory.CreateDirectory(_fixture);
		int me = Environment.ProcessId;
		int session = Process.GetCurrentProcess().SessionId;
		Trace($"pid {me}, session {session}, fixture {_fixture}");

		// ---------------------------------------------------------------- refusal of the live folder
		ExitForensics.Configure("shell-readonly-probe", null, SinkAdd);
		bool wroteLive = ExitForensics.WriteBreath(ExitForensics.StateRunning, "probe");
		Check("breath.live-folder-refused-in-readonly", !wroteLive && !File.Exists(Path.Combine(ExitForensics.DefaultDir, "shell-readonly-probe.breath")), "default dir " + ExitForensics.DefaultDir);

		// ---------------------------------------------------------------- breath round trip
		ExitForensics.Configure(ExitForensics.RoleShell, _fixture, SinkAdd);
		bool w1 = ExitForensics.WriteBreath(ExitForensics.StateRunning, "start", "fixture start\r\nsecond line");
		ExitForensics.Breath? b = ExitForensics.ReadBreath(ExitForensics.RoleShell);
		Check("breath.write", w1 && File.Exists(ExitForensics.BreathPath(ExitForensics.RoleShell)));
		Check("breath.roundtrip", b != null && b.Pid == me && b.Session == session && b.State == ExitForensics.StateRunning && b.Event == "start"
			&& b.ProcessStartTicks == ExitForensics.ProcessStartTicks && b.ProcessStartTicks > 0 && b.EventTicks > 0,
			b == null ? "null" : $"pid={b.Pid} session={b.Session} state={b.State} start={b.ProcessStartTicks}");
		Check("breath.session-logon-recorded", b != null && b.SessionLogonTicks > 0 && b.SessionLogonTicks == ExitForensics.SessionLogonTicks,
			b == null ? "null" : $"sessionLogon={ExitForensics.Utc(b.SessionLogonTicks)}");
		Check("breath.detail-single-line", b != null && b.Detail.IndexOf('\n') < 0 && b.Detail.Contains("second line"), b?.Detail ?? "");
		Check("breath.no-temp-files-left", Directory.GetFiles(_fixture, "*.tmp").Length == 0);

		// onlyIfFirst: an early exit record must not be overwritten by a late "start".
		ExitForensics.Configure(ExitForensics.RoleShell, _fixture, SinkAdd);
		ExitForensics.WriteBreath(ExitForensics.StateExited, "early exit");
		bool lateStart = ExitForensics.WriteBreath(ExitForensics.StateRunning, "start", onlyIfFirst: true);
		Check("breath.late-start-does-not-overwrite-exit", !lateStart && ExitForensics.ReadBreath(ExitForensics.RoleShell)?.State == ExitForensics.StateExited);

		// A newer live instance owns the record: an older process must not overwrite it.
		Process waiter = StartWaiter();
		Thread.Sleep(200);
		long waiterStart = ExitForensics.ProcessStartUtcTicks(waiter.Id);
		ExitForensics.Breath newer = new ExitForensics.Breath
		{
			Role = ExitForensics.RoleShell,
			Pid = waiter.Id,
			Session = session,
			ProcessStartTicks = waiterStart,
			State = ExitForensics.StateRunning,
			Event = "start",
			EventTicks = DateTime.UtcNow.Ticks
		};
		File.WriteAllText(ExitForensics.BreathPath(ExitForensics.RoleShell), newer.Serialize());
		bool overwrote = ExitForensics.WriteBreath(ExitForensics.StateExited, "older instance exiting");
		Check("breath.newer-live-instance-keeps-record", !overwrote && ExitForensics.ReadBreath(ExitForensics.RoleShell)?.Pid == waiter.Id, $"waiter pid {waiter.Id}");
		Check("probe.alive-with-start", ExitForensics.ProcessAliveWithStart(waiter.Id, waiterStart) == ExitForensics.AliveState.Alive);
		Check("probe.reused-pid-detected", ExitForensics.ProcessAliveWithStart(waiter.Id, waiterStart + TimeSpan.TicksPerSecond) == ExitForensics.AliveState.ReusedPid);
		Check("probe.self-alive", ExitForensics.ProcessAliveWithStart(me, ExitForensics.ProcessStartTicks) == ExitForensics.AliveState.Alive);

		// ---------------------------------------------------------------- process exit watch (exit codes)
		ProcessExitWatch? killedWatch = null;
		uint killedCode = 0;
		ManualResetEventSlim killedDone = new ManualResetEventSlim(false);
		killedWatch = ProcessExitWatch.Open(waiter.Id, delegate(ProcessExitWatch w)
		{
			killedCode = w.ExitCode;
			killedDone.Set();
		}, out string openErr);
		Check("exitwatch.open", killedWatch != null, openErr);
		waiter.Kill();
		Check("exitwatch.fires-on-kill", killedDone.Wait(5000) && killedCode == 0xFFFFFFFFu, $"code 0x{killedCode:X}");
		bool wroteAfterDeath = ExitForensics.WriteBreath(ExitForensics.StateExited, "after the newer instance died");
		Check("breath.dead-newer-instance-releases-record", wroteAfterDeath && ExitForensics.ReadBreath(ExitForensics.RoleShell)?.Pid == me);
		Check("probe.dead-pid", ExitForensics.ProcessAliveWithStart(waiter.Id, waiterStart) != ExitForensics.AliveState.Alive);

		foreach ((string cmd, uint expect, string fragment) in new[]
		{
			("/c exit 143", 143u, "SIGTERM"),
			("/c exit -1073741819", 0xC0000005u, "access violation"),
			("/c exit 0", 0u, "normal exit")
		})
		{
			Process p = StartChild(cmd);
			uint got = 1234567u;
			DateTime exitUtc = DateTime.MinValue;
			ManualResetEventSlim done = new ManualResetEventSlim(false);
			ProcessExitWatch? w = ProcessExitWatch.Open(p.Id, delegate(ProcessExitWatch x)
			{
				got = x.ExitCode;
				exitUtc = x.ExitUtc;
				done.Set();
			}, out string err);
			bool fired = w != null && done.Wait(10000);
			string described = ExitForensics.DescribeExitCode(got);
			Check("exitwatch.code " + cmd, fired && got == expect && described.Contains(fragment) && Math.Abs((DateTime.UtcNow - exitUtc).TotalSeconds) < 30.0, $"{described}; err={err}");
		}

		// ---------------------------------------------------------------- decoding and formatting
		Check("decode.143", ExitForensics.DescribeExitCode(143u).Contains("SIGTERM") && ExitForensics.DescribeExitCode(143u).Contains("MSYS2"));
		Check("decode.clr", ExitForensics.DescribeExitCode(0xE0434352u).Contains(".NET"));
		Check("decode.stack-overflow", ExitForensics.DescribeExitCode(0xC00000FDu).Contains("stack overflow"));
		Check("decode.task-scheduler", ExitForensics.DescribeExitCode(0x41306u).Contains("Task Scheduler"));
		Check("decode.one", ExitForensics.DescribeExitCode(1u).Contains("TerminateProcess"));
		ExitForensics.Breath shellRec = new ExitForensics.Breath { Role = "shell", Pid = 4242, Session = 1, State = ExitForensics.StateRunning, Event = "end-session-ignored", EventTicks = DateTime.UtcNow.Ticks };
		string formatted = ExitForensics.FormatShellExit(4242, 143u, DateTime.UtcNow, DateTime.UtcNow.AddMinutes(-5), 3.2, shellRec);
		Check("format.shell-exit", formatted.Contains("shell pid 4242") && formatted.Contains("SIGTERM") && formatted.Contains("end-session-ignored") && formatted.Contains("3.2 s before") && formatted.Contains("5.0 min"), formatted);
		Check("format.shell-exit-other-pid", ExitForensics.FormatShellExit(1, 0u, DateTime.UtcNow, DateTime.MinValue, -1.0, shellRec).Contains("no record from this pid"));

		// ---------------------------------------------------------------- previous-instance verdicts
		long now = DateTime.UtcNow.Ticks;
		long boot = now - TimeSpan.TicksPerHour;
		ExitForensics.Breath Rec(string state, int sess, long start) => new ExitForensics.Breath { Role = "supervisor", Pid = 999001, Session = sess, ProcessStartTicks = start, State = state, Event = "start", EventTicks = start };
		Func<int, bool?> exists = s => s == 1 || s == 2;
		Func<int, long, ExitForensics.AliveState> dead = (p, t) => ExitForensics.AliveState.Dead;
		Func<int, long, ExitForensics.AliveState> alive = (p, t) => ExitForensics.AliveState.Alive;
		long inBoot = now - TimeSpan.TicksPerMinute * 10;
		var cases = new (string Name, ExitForensics.Breath? Prev, Func<int, long, ExitForensics.AliveState> Alive, string Expect)[]
		{
			("none", null, dead, "none"),
			("clean", Rec(ExitForensics.StateExited, 2, inBoot), dead, "clean"),
			("alive", Rec(ExitForensics.StateRunning, 2, inBoot), alive, "alive"),
			("crashed", Rec(ExitForensics.StateCrashing, 2, inBoot), dead, "crashed"),
			("previous-boot", Rec(ExitForensics.StateRunning, 1, boot - TimeSpan.TicksPerDay), dead, "previous-boot"),
			("session-gone (the 2026-10-24 teardown)", Rec(ExitForensics.StateRunning, 7, inBoot), dead, "session-gone"),
			("session-ending", Rec(ExitForensics.StateSessionEnding, 2, inBoot), dead, "session-ending"),
			("other-session", Rec(ExitForensics.StateRunning, 1, inBoot), dead, "other-session"),
			("killed-in-session", Rec(ExitForensics.StateRunning, 2, inBoot), dead, "killed-in-session")
		};
		foreach (var c in cases)
		{
			(string verdict, string text) = ExitForensics.AnalyzePrevious(c.Prev, 2, boot, exists, c.Alive);
			Check("verdict." + c.Name, verdict == c.Expect, verdict + ": " + text);
		}
		var (_, goneText) = ExitForensics.AnalyzePrevious(Rec(ExitForensics.StateRunning, 7, inBoot), 2, boot, exists, dead);
		Check("verdict.session-gone-says-not-a-launcher-crash", goneText.Contains("no longer exists") && goneText.Contains("Not a launcher crash"), goneText);
		// Session numbers are reused: the record's session 2 exists again, but a newer logon holds it.
		long oldLogon = inBoot - TimeSpan.TicksPerMinute;
		long newLogon = now - TimeSpan.TicksPerMinute * 2;
		ExitForensics.Breath RecL(string state, long logon, long start)
		{
			ExitForensics.Breath r0 = Rec(state, 2, start);
			r0.SessionLogonTicks = logon;
			return r0;
		}
		var logonCases = new (string Name, ExitForensics.Breath Prev, long NowLogon, string Expect)[]
		{
			("session-gone (session number reused by a new logon)", RecL(ExitForensics.StateRunning, oldLogon, inBoot), newLogon, "session-gone"),
			("session-gone (reused number, after an announced end)", RecL(ExitForensics.StateSessionEnding, oldLogon, inBoot), newLogon, "session-gone"),
			("session-gone (reused number, record without a logon time)", RecL(ExitForensics.StateRunning, 0L, inBoot), newLogon, "session-gone"),
			("killed-in-session (same logon)", RecL(ExitForensics.StateRunning, oldLogon, inBoot), oldLogon, "killed-in-session"),
			("killed-in-session (no logon time, started after this logon)", RecL(ExitForensics.StateRunning, 0L, inBoot), oldLogon, "killed-in-session"),
			("killed-in-session (logon time unknown now)", RecL(ExitForensics.StateRunning, oldLogon, inBoot), 0L, "killed-in-session"),
			("session-ending (same logon)", RecL(ExitForensics.StateSessionEnding, oldLogon, inBoot), oldLogon, "session-ending")
		};
		foreach (var c in logonCases)
		{
			long nl = c.NowLogon;
			(string verdict, string text) = ExitForensics.AnalyzePrevious(c.Prev, 2, boot, exists, dead, s => s == 2 ? nl : 0L);
			Check("verdict." + c.Name, verdict == c.Expect, verdict + ": " + text);
		}
		var (_, reusedText) = ExitForensics.AnalyzePrevious(RecL(ExitForensics.StateRunning, oldLogon, inBoot), 2, boot, exists, dead, s => newLogon);
		Check("verdict.reused-session-says-new-logon", reusedText.Contains("exists again, but as a new Windows logon") && reusedText.Contains("Not a launcher crash"), reusedText);
		Check("live.session-exists", ExitForensics.SessionExists(session) == true);
		Check("live.session-missing", ExitForensics.SessionExists(987654) == false);
		long liveLogon = ExitForensics.SessionLogonUtcTicks(session);
		Check("live.session-logon-time", liveLogon > ExitForensics.BootTicksUtc() - TimeSpan.TicksPerMinute && liveLogon <= ExitForensics.ProcessStartTicks && ExitForensics.SessionLogonUtcTicks(987654) == 0L,
			$"session {session} logon {ExitForensics.Utc(liveLogon)}, boot {ExitForensics.Utc(ExitForensics.BootTicksUtc())}, this process started {ExitForensics.Utc(ExitForensics.ProcessStartTicks)}");
		Check("live.boot-time-sane", ExitForensics.BootTicksUtc() < now && ExitForensics.BootTicksUtc() > now - TimeSpan.TicksPerDay * 400);
		// The live analysis of a record from a vanished session, end to end through ReportPrevious.
		ExitForensics.Breath vanished = new ExitForensics.Breath { Role = "shell", Pid = waiter.Id, Session = 987654, ProcessStartTicks = waiterStart, State = ExitForensics.StateRunning, Event = "AppSwitcher show", EventTicks = DateTime.UtcNow.Ticks };
		(string liveVerdict, string liveText) = ExitForensics.ReportPrevious(vanished);
		Check("live.report-previous-session-gone", liveVerdict == "session-gone" && SinkHas("Session 987654 no longer exists"), liveText);

		// ---------------------------------------------------------------- session-end policy
		uint LOGOFF = SessionWatch.ENDSESSION_LOGOFF;
		uint CLOSEAPP = SessionWatch.ENDSESSION_CLOSEAPP;
		Check("policy.cancelled-continues", SessionEndPolicy.DecideShell(false, LOGOFF, true) == SessionEndPolicy.ShellAction.Continue);
		Check("policy.restart-manager-stays-up", SessionEndPolicy.DecideShell(true, CLOSEAPP, false) == SessionEndPolicy.ShellAction.StayUpRestartManager);
		Check("policy.closeapp-during-real-shutdown-exits", SessionEndPolicy.DecideShell(true, CLOSEAPP, true) == SessionEndPolicy.ShellAction.ExitSessionEnding
			&& SessionEndPolicy.DecideShell(true, CLOSEAPP | LOGOFF, true) == SessionEndPolicy.ShellAction.ExitSessionEnding);
		Check("policy.real-logoff-exits", SessionEndPolicy.DecideShell(true, LOGOFF, true) == SessionEndPolicy.ShellAction.ExitSessionEnding);
		Check("policy.real-shutdown-exits", SessionEndPolicy.DecideShell(true, 0u, true) == SessionEndPolicy.ShellAction.ExitSessionEnding);
		Check("policy.spurious-end-stays-up", SessionEndPolicy.DecideShell(true, LOGOFF, false) == SessionEndPolicy.ShellAction.StayUpNotShuttingDown
			&& SessionEndPolicy.DecideShell(true, 0u, false) == SessionEndPolicy.ShellAction.StayUpNotShuttingDown);
		Check("policy.live-not-shutting-down", !SessionEndPolicy.ShuttingDown());
		DateTime t0 = DateTime.UtcNow;
		SessionEndPolicy.SupervisorState st = new SessionEndPolicy.SupervisorState();
		Check("hold.none", SessionEndPolicy.Hold(st, t0, false) == null);
		SessionEndPolicy.NoteQuery(st, t0);
		Check("hold.query-pending", SessionEndPolicy.Hold(st, t0.AddSeconds(1), false)?.Contains("WM_QUERYENDSESSION") == true);
		Check("hold.query-expires", SessionEndPolicy.Hold(st, t0.AddSeconds(61), false) == null && SessionEndPolicy.Hold(st, t0.AddSeconds(1), false) == null);
		SessionEndPolicy.NoteQuery(st, t0);
		SessionEndPolicy.NoteEnd(st, false, LOGOFF, true, t0);
		Check("hold.cancel-clears", SessionEndPolicy.Hold(st, t0.AddSeconds(1), false) == null);
		SessionEndPolicy.NoteQuery(st, t0);
		SessionEndPolicy.NoteEnd(st, true, LOGOFF, true, t0);
		Check("hold.ending", SessionEndPolicy.Hold(st, t0.AddSeconds(5), false)?.Contains("WM_ENDSESSION") == true);
		Check("hold.ending-while-shutting-down-persists", SessionEndPolicy.Hold(st, t0.AddSeconds(300), true) != null);
		Check("hold.ending-expires-when-session-goes-on", SessionEndPolicy.Hold(st, t0.AddSeconds(31), false) == null && SessionEndPolicy.Hold(st, t0.AddSeconds(1), false) == null);
		SessionEndPolicy.NoteEnd(st, true, CLOSEAPP, false, t0);
		Check("hold.restart-manager", SessionEndPolicy.Hold(st, t0.AddSeconds(5), false)?.Contains("Restart Manager") == true && SessionEndPolicy.Hold(st, t0.AddSeconds(11), false) == null);
		SessionEndPolicy.NoteEnd(st, true, CLOSEAPP, false, t0);
		Check("hold.restart-manager-while-shutting-down-persists", SessionEndPolicy.Hold(st, t0.AddSeconds(300), true)?.Contains("Restart Manager") == true
			&& SessionEndPolicy.Hold(st, t0.AddSeconds(301), false) == null);
		SessionEndPolicy.NoteEnd(st, true, CLOSEAPP, true, t0);
		Check("hold.closeapp-during-real-shutdown-is-a-session-end", SessionEndPolicy.Hold(st, t0.AddSeconds(20), false)?.Contains("WM_ENDSESSION") == true
			&& SessionEndPolicy.Hold(st, t0.AddSeconds(300), true) != null && SessionEndPolicy.Hold(st, t0.AddSeconds(31), false) == null);

		// ---------------------------------------------------------------- SessionWatch window, fake messages
		List<uint> queries = new List<uint>();
		List<(bool, uint)> ends = new List<(bool, uint)>();
		int compositions = 0;
		SessionWatch watch = new SessionWatch(new SessionWatch.Options { Role = "test", RegisterWts = false, Log = SinkAdd });
		watch.QueryEndSession += f => { lock (queries) { queries.Add(f); } };
		watch.EndSession += (e, f) => { lock (ends) { ends.Add((e, f)); } };
		watch.CompositionChanged += () => Interlocked.Increment(ref compositions);
		bool started = watch.Start();
		nint wh = watch.Hwnd;
		Check("watch.window-created", started && wh != IntPtr.Zero && IsWindow(wh) && watch.ThreadId != 0, $"hwnd 0x{(long)wh:X}");
		nint r = Send(wh, SessionWatch.WM_QUERYENDSESSION, IntPtr.Zero, (nint)LOGOFF);
		Check("watch.query-answers-true", r == 1 && queries.Count == 1 && queries[0] == LOGOFF && SinkHas("WM_QUERYENDSESSION flags=0x80000000 LOGOFF"), $"result {r}");
		Send(wh, SessionWatch.WM_ENDSESSION, IntPtr.Zero, (nint)LOGOFF);
		Send(wh, SessionWatch.WM_ENDSESSION, (nint)1, (nint)CLOSEAPP);
		Check("watch.endsession-false-and-true", ends.Count == 2 && ends[0] == (false, LOGOFF) && ends[1] == (true, CLOSEAPP) && SinkHas("cancelled") && SinkHas("CLOSEAPP (Restart Manager)"));
		Send(wh, SessionWatch.WM_WTSSESSION_CHANGE, (nint)7, (nint)session);
		Check("watch.wts-lock-logged", SinkHas("session change: SESSION_LOCK"));
		Send(wh, SessionWatch.WM_DWMCOMPOSITIONCHANGED, IntPtr.Zero, IntPtr.Zero);
		Check("watch.composition-changed", compositions == 1 && SinkHas("WM_DWMCOMPOSITIONCHANGED"));
		nint lp = (nint)((1080 << 16) | 1920);
		Send(wh, SessionWatch.WM_DISPLAYCHANGE, (nint)32, lp);
		Send(wh, SessionWatch.WM_DISPLAYCHANGE, (nint)32, lp);
		bool coalesced = WaitFor(() => SinkHas("display changed: primary 1920x1080 32bpp"), 4000);
		Check("watch.display-change-coalesced", coalesced && SinkCount("display changed:") == 1 && SinkHas("(2 notifications)"));
		nint pr = Send(wh, SessionWatch.WM_POWERBROADCAST, (nint)4, IntPtr.Zero);
		Check("watch.power-suspend-logged", pr == 1 && SinkHas("power: suspending"));
		Check("watch.flags-text", SessionWatch.DescribeEndSessionFlags(0u).Contains("shutdown") && SessionWatch.DescribeEndSessionFlags(0xC0000000u).Contains("LOGOFF|CRITICAL"));
		int watchStops = 0;
		watch.Stopped += _ => Interlocked.Increment(ref watchStops);
		Send(wh, 0x0010u /*WM_CLOSE*/, IntPtr.Zero, IntPtr.Zero);
		Thread.Sleep(100);
		Check("watch.outside-wm-close-ignored", IsWindow(wh) && !watch.IsStopped && watchStops == 0 && SinkHas("WM_CLOSE from outside ignored"));

		// The shell's real WM_ENDSESSION handler behind a watch window: none of these may start a shutdown.
		App? shellApp = app as App;
		Check("shell.app-instance", shellApp != null);
		if (shellApp != null)
		{
			ExitForensics.Configure(ExitForensics.RoleShell, _fixture, SinkAdd);
			watch.EndSession += shellApp.OnWindowsEndSession;
			Send(wh, SessionWatch.WM_ENDSESSION, IntPtr.Zero, (nint)LOGOFF, 6000);
			ExitForensics.Breath? afterCancel = ExitForensics.ReadBreath(ExitForensics.RoleShell);
			Check("shell.cancelled-logoff-keeps-running", afterCancel?.Event == "end-session-cancelled" && afterCancel.State == ExitForensics.StateRunning && !shellApp.SessionEndingForTest && !app.Dispatcher.HasShutdownStarted && !WpfShuttingDown(), afterCancel?.Detail ?? "no record");
			Send(wh, SessionWatch.WM_ENDSESSION, (nint)1, (nint)CLOSEAPP, 6000);
			ExitForensics.Breath? afterRm = ExitForensics.ReadBreath(ExitForensics.RoleShell);
			Check("shell.restart-manager-keeps-running", afterRm?.Event == "restart-manager-close" && afterRm.Detail.Contains("flushing the Start layout") && !shellApp.SessionEndingForTest && !app.Dispatcher.HasShutdownStarted && !WpfShuttingDown(), afterRm?.Detail ?? "no record");
			Send(wh, SessionWatch.WM_ENDSESSION, (nint)1, (nint)LOGOFF, 6000);
			ExitForensics.Breath? afterSpurious = ExitForensics.ReadBreath(ExitForensics.RoleShell);
			Check("shell.spurious-endsession-keeps-running", afterSpurious?.Event == "end-session-ignored" && !shellApp.SessionEndingForTest && !app.Dispatcher.HasShutdownStarted && !WpfShuttingDown(), afterSpurious?.Detail ?? "no record");
			watch.EndSession -= shellApp.OnWindowsEndSession;
		}
		watch.Dispose();
		Check("watch.disposed", !IsWindow(wh) && watchStops == 0 && !watch.IsStopped);

		// ---------------------------------------------------------------- WPF end-session guard
		string guardDetail = "";
		bool installed = app.Dispatcher.Invoke(() => WpfSessionGuard.TryInstall(app, out guardDetail));
		nint parking = WpfSessionGuard.ParkingHwnd;
		nint wpfParking = app.Dispatcher.Invoke(() => (nint)(typeof(System.Windows.Application).GetProperty("ParkingHwnd", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(app) ?? IntPtr.Zero));
		Check("guard.installed", installed && parking != IntPtr.Zero && parking == wpfParking, guardDetail);
		int answered = WpfSessionGuard.QueriesAnswered;
		nint gr = Send(parking, SessionWatch.WM_QUERYENDSESSION, IntPtr.Zero, (nint)LOGOFF, 5000);
		Thread.Sleep(200);
		Check("guard.query-answered-true-without-shutdown", gr == 1 && WpfSessionGuard.QueriesAnswered == answered + 1 && App.SessionEndingCallsForTest == 0
			&& !WpfShuttingDown() && !app.Dispatcher.HasShutdownStarted && !(shellApp?.SessionEndingForTest ?? false), $"result {gr}");
		// Control: without the guard, WPF's own handler is reached (OnSessionEnding; uncancelled it calls Shutdown()).
		App.CancelSessionEndingForTest = true;
		bool removed = app.Dispatcher.Invoke(() => WpfSessionGuard.Remove());
		nint cr = Send(parking, SessionWatch.WM_QUERYENDSESSION, IntPtr.Zero, (nint)LOGOFF, 5000);
		Check("guard.control-wpf-handler-reached-without-guard", removed && App.SessionEndingCallsForTest == 1 && cr == 0 && !WpfShuttingDown(), $"result {cr}, OnSessionEnding calls {App.SessionEndingCallsForTest}");
		bool reinstalled = app.Dispatcher.Invoke(() => WpfSessionGuard.TryInstall(app, out guardDetail));
		Check("guard.reinstall", reinstalled);
		// The guard is only kept while a SessionWatch handles WM_ENDSESSION: when the shell's watch stops (here its loop is
		// ended with WM_QUIT posted to its own thread), the shell takes the guard out and WPF's own handler is reached again.
		if (shellApp != null)
		{
			SessionWatch lost = new SessionWatch(new SessionWatch.Options { Role = "test-lost", RegisterWts = false, Log = SinkAdd });
			lost.Stopped += shellApp.ShellSessionWatchLost;
			bool lostStarted = lost.Start();
			nint lostHwnd = lost.Hwnd;
			bool guardBefore = WpfSessionGuard.Installed;
			PostThreadMessageW(lost.ThreadId, 0x0012u /*WM_QUIT*/, IntPtr.Zero, IntPtr.Zero);
			bool fellBack = WaitFor(() => lost.IsStopped && !WpfSessionGuard.Installed, 5000);
			int callsBefore = App.SessionEndingCallsForTest;
			nint fr = Send(parking, SessionWatch.WM_QUERYENDSESSION, IntPtr.Zero, (nint)LOGOFF, 5000);
			Check("guard.removed-when-session-watch-stops", lostStarted && guardBefore && fellBack && SinkHas("session watch stopped unexpectedly") && SinkHas("WPF end-session guard removed")
				&& App.SessionEndingCallsForTest == callsBefore + 1 && fr == 0 && !WpfShuttingDown(), $"result {fr}, OnSessionEnding calls {App.SessionEndingCallsForTest}");
			lost.Dispose();
			Check("guard.stopped-watch-window-gone", !IsWindow(lostHwnd) || lostHwnd == IntPtr.Zero);
		}
		App.CancelSessionEndingForTest = false;

		// ---------------------------------------------------------------- console control events
		ExitForensics.Configure(ExitForensics.RoleShell, _fixture, SinkAdd);
		Check("ctrl.install", ExitForensics.InstallConsoleControlHandler());
		bool swallowed = ExitForensics.OnConsoleControl(0u);
		ExitForensics.Breath? afterC = ExitForensics.ReadBreath(ExitForensics.RoleShell);
		Check("ctrl.c-ignored", swallowed && afterC?.State == ExitForensics.StateRunning && afterC.Event == "console-control" && SinkHas("CTRL_C_EVENT received - ignored"));
		Check("ctrl.break-ignored", ExitForensics.OnConsoleControl(1u));
		bool closeHandled = ExitForensics.OnConsoleControl(2u);
		Check("ctrl.close-default", !closeHandled && ExitForensics.ReadBreath(ExitForensics.RoleShell)?.State == ExitForensics.StateExiting);
		ExitForensics.RemoveConsoleControlHandlerForTest();
		ExitForensics.NoteProcessExit(0, " (test)");
		Check("processexit.recorded", ExitForensics.ReadBreath(ExitForensics.RoleShell)?.State == ExitForensics.StateExited && SinkHas("ProcessExit: exit code 0 (test)"));

		// ---------------------------------------------------------------- job object
		string job = ExitForensics.JobSummary();
		Check("job.summary", job.StartsWith("job: ", StringComparison.Ordinal), job);
		Check("job.struct-sizes", ExitForensics.ExtendedLimitInfoSize == (IntPtr.Size == 8 ? 144 : 112) && ExitForensics.BasicAccountingInfoSize == 48, $"{ExitForensics.ExtendedLimitInfoSize}/{ExitForensics.BasicAccountingInfoSize}");
		Check("job.limits-text", ExitForensics.DescribeJobLimits(0x2000u | 0x800u) == "KILL_ON_JOB_CLOSE|BREAKAWAY_OK" && ExitForensics.DescribeJobLimits(0u) == "none");

		// ---------------------------------------------------------------- DWM watch
		int dwmPid = DwmWatch.FindDwmInSession();
		Check("dwm.found-in-session", dwmPid > 0, $"pid {dwmPid}");
		ProcessExitWatch? dwmProbe = ProcessExitWatch.Open(dwmPid, delegate { }, out string dwmErr);
		Trace($"INFO dwm.exit-watch-openable={(dwmProbe != null)} ({(dwmProbe != null ? "SYNCHRONIZE + limited query granted" : dwmErr)})");
		dwmProbe?.Dispose();
		Process fake1 = StartWaiter();
		Process fake2 = StartWaiter();
		Thread.Sleep(200);
		_fakeDwm = fake1.Id;
		List<(int, int)> restarts = new List<(int, int)>();
		DwmWatch dw = new DwmWatch(() => _fakeDwm, SinkAdd, (e, d) => SinkAdd(e + ": " + d))
		{
			PollIntervalMs = 100,
			MaxPolls = 50
		};
		dw.Restarted += (o, n) => { lock (restarts) { restarts.Add((o, n)); } };
		dw.Start();
		Check("dwmwatch.armed", dw.Watching && dw.Pid == fake1.Id && SinkHas($"watching DWM pid {fake1.Id}"));
		_fakeDwm = fake2.Id;
		fake1.Kill();
		bool restarted = WaitFor(() => { lock (restarts) { return restarts.Count == 1; } }, 5000);
		Check("dwmwatch.exit-and-restart", restarted && restarts[0] == (fake1.Id, fake2.Id) && SinkHas($"dwm-exited: DWM pid {fake1.Id} exited") && SinkHas($"DWM restarted: new pid {fake2.Id}") && dw.Watching && dw.Pid == fake2.Id);
		_fakeDwm = me;
		dw.Check("WM_DWMCOMPOSITIONCHANGED");
		Check("dwmwatch.check-notices-change", dw.RestartsSeen == 2 && SinkHas("noticed via WM_DWMCOMPOSITIONCHANGED") && dw.Pid == me);
		dw.Dispose();
		fake2.Kill();

		// ---------------------------------------------------------------- DWM thumbnails + accent clear (own windows only)
		Check("thumbs.reject-null", DwmThumbs.TryRegister(IntPtr.Zero, IntPtr.Zero, 0, 0, 10, 10) == IntPtr.Zero);
		(nint dest, nint src, Window? wDest, Window? wSrc) = app.Dispatcher.Invoke(() =>
		{
			Window MakeWindow(double left) => new Window
			{
				Left = left,
				Top = -30000,
				Width = 220,
				Height = 160,
				WindowStyle = WindowStyle.None,
				ResizeMode = ResizeMode.NoResize,
				ShowInTaskbar = false,
				ShowActivated = false,
				Topmost = false,
				Title = "Win81Layer exit forensics QA",
				Background = System.Windows.Media.Brushes.SteelBlue
			};
			Window a = MakeWindow(-30000);
			Window c = MakeWindow(-29000);
			a.Show();
			c.Show();
			return (new WindowInteropHelper(a).Handle, new WindowInteropHelper(c).Handle, (Window?)a, (Window?)c);
		});
		int rejected0 = DwmThumbs.Rejected;
		int failed0 = DwmThumbs.Failed;
		Check("thumbs.reject-non-window-source", DwmThumbs.TryRegister(dest, (nint)0x7FFF0F01, 0, 0, 100, 80) == IntPtr.Zero && DwmThumbs.Rejected == rejected0 + 1 && DwmThumbs.Failed == failed0);
		Check("thumbs.reject-empty-rect", DwmThumbs.TryRegister(dest, src, 10, 10, 10, 50) == IntPtr.Zero);
		nint thumb = app.Dispatcher.Invoke(() => DwmThumbs.TryRegister(dest, src, 0, 0, 100, 80));
		Check("thumbs.register-own-window", thumb != IntPtr.Zero && DwmThumbs.Failed == failed0, $"last hr 0x{DwmThumbs.LastHResult:X8}");
		app.Dispatcher.Invoke(() => DwmThumbs.Unregister(thumb));
		app.Dispatcher.Invoke(() => wSrc?.Hide());
		Check("thumbs.reject-hidden-source", app.Dispatcher.Invoke(() => DwmThumbs.TryRegister(dest, src, 0, 0, 100, 80)) == IntPtr.Zero);
		int calls0 = AcrylicGlass.Calls;
		app.Dispatcher.Invoke(() => { AcrylicGlass.Clear(dest); AcrylicGlass.Clear(dest); AcrylicGlass.Clear(src); });
		Check("accent.clear-once-per-hwnd", AcrylicGlass.Calls == calls0 + 2, $"{AcrylicGlass.Calls - calls0} calls");
		app.Dispatcher.Invoke(() => { wDest?.Close(); wSrc?.Close(); });

		// ---------------------------------------------------------------- logger
		bool flushOk = true;
		try { Logger.FlushSync(); } catch { flushOk = false; }
		Check("logger.flushsync-readonly-noop", flushOk);
	}

	private static bool WpfShuttingDown()
	{
		try
		{
			object? v = typeof(System.Windows.Application).GetProperty("IsApplicationObjectShuttingDown", BindingFlags.Static | BindingFlags.NonPublic)?.GetValue(null);
			return v is bool b && b;
		}
		catch
		{
			return false;
		}
	}

	private static nint Send(nint hwnd, uint msg, nint wParam, nint lParam, uint timeoutMs = 3000)
	{
		nint ok = SendMessageTimeoutW(hwnd, msg, wParam, lParam, 0x0002u /*SMTO_ABORTIFHUNG*/, timeoutMs, out nint result);
		if (ok == IntPtr.Zero)
		{
			Trace($"  send 0x{msg:X} to 0x{(long)hwnd:X} timed out or failed (error {Marshal.GetLastWin32Error()})");
			return -1;
		}
		return result;
	}

	private static void WriteResults()
	{
		List<(string Name, bool Pass, string Detail)> checks;
		lock (Checks)
		{
			checks = Checks.ToList();
		}
		int pass = checks.Count(c => c.Pass);
		int fail = checks.Count - pass;
		StringBuilder sb = new StringBuilder();
		foreach (var c in checks)
		{
			sb.Append(c.Pass ? "PASS " : "FAIL ").Append(c.Name);
			if (c.Detail.Length > 0)
			{
				sb.Append("  (").Append(c.Detail.Replace("\r", " ").Replace("\n", " ")).Append(')');
			}
			sb.Append("\r\n");
		}
		sb.Append($"SUMMARY pass={pass} fail={fail}\r\n");
		File.WriteAllText(Path.Combine(_outDir, "exitforensicstest-results.txt"), sb.ToString());
		lock (Transcript)
		{
			File.WriteAllLines(Path.Combine(_outDir, "exitforensicstest-transcript.txt"), Transcript);
		}
		Dictionary<string, object> report = new Dictionary<string, object>
		{
			["passed"] = pass,
			["failed"] = fail,
			["pid"] = Environment.ProcessId,
			["fixture"] = _fixture,
			["failures"] = checks.Where(c => !c.Pass).Select(c => c.Name + ": " + c.Detail).ToArray()
		};
		File.WriteAllText(Path.Combine(_outDir, "exitforensicstest-report.json"), JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
	}

	[DllImport("user32.dll", SetLastError = true)]
	private static extern nint SendMessageTimeoutW(nint hwnd, uint msg, nint wParam, nint lParam, uint flags, uint timeout, out nint result);

	[DllImport("user32.dll")]
	private static extern bool IsWindow(nint hwnd);

	[DllImport("user32.dll")]
	private static extern bool PostThreadMessageW(int threadId, uint msg, nint wParam, nint lParam);
}
