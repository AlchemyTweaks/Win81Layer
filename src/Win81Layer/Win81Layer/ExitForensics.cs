#nullable enable

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Win81Layer;

// Exit forensics: makes the next silent exit of the shell or the supervisor explain itself.
//
// Each process keeps a tiny "last breath" record (forensics\<role>.breath: pid, session, process start time, state,
// last event) that is written synchronously and write-through at start, on every session event (WM_QUERYENDSESSION /
// WM_ENDSESSION / console control), on DWM exits, on unhandled exceptions and on every exit path. A process that is
// terminated from outside never gets to write "exited", so the NEXT instance reads the record and names what happened:
// a clean exit, a session teardown (the record's session no longer exists, or its number now belongs to a newer logon,
// which is what a winlogon crash or a logoff leaves), a reboot since, an announced session end, an unhandled exception, or a kill inside the same session.
// Nothing here runs periodically: every write is event-driven, so the idle cost is zero.
internal static class ExitForensics
{
	internal const string RoleShell = "shell";

	internal const string RoleSupervisor = "supervisor";

	internal const string StateRunning = "running";

	internal const string StateSessionEnding = "session-ending";

	internal const string StateExiting = "exiting";

	internal const string StateExited = "exited";

	internal const string StateCrashing = "crashing";

	internal enum AliveState
	{
		Dead,
		Alive,
		ReusedPid,
		Unknown
	}

	internal sealed class Breath
	{
		public string Role = "";

		public int Pid;

		public int Session = -1;

		// UTC ticks of the WTS logon time of that session, 0 if unknown. Session numbers are reused: a new logon can get
		// the number of a session that was torn down, so the number alone cannot tell "same logon" from "new logon".
		public long SessionLogonTicks;

		public long ProcessStartTicks;   // UTC ticks of the process creation time (GetProcessTimes), 0 if unknown

		public string State = "";

		public string Event = "";

		public long EventTicks;   // UTC ticks

		public string Detail = "";

		public string Build = "";

		public string Serialize()
		{
			StringBuilder sb = new StringBuilder();
			sb.Append("role=").Append(Clean(Role)).Append("\r\n");
			sb.Append("pid=").Append(Pid.ToString(CultureInfo.InvariantCulture)).Append("\r\n");
			sb.Append("session=").Append(Session.ToString(CultureInfo.InvariantCulture)).Append("\r\n");
			sb.Append("sessionLogon=").Append(SessionLogonTicks.ToString(CultureInfo.InvariantCulture)).Append("\r\n");
			sb.Append("processStart=").Append(ProcessStartTicks.ToString(CultureInfo.InvariantCulture)).Append("\r\n");
			sb.Append("state=").Append(Clean(State)).Append("\r\n");
			sb.Append("event=").Append(Clean(Event)).Append("\r\n");
			sb.Append("eventTime=").Append(EventTicks.ToString(CultureInfo.InvariantCulture)).Append("\r\n");
			sb.Append("detail=").Append(Clean(Detail)).Append("\r\n");
			sb.Append("build=").Append(Clean(Build)).Append("\r\n");
			// Human-readable copies for anyone opening the file by hand.
			sb.Append("sessionLogonUtc=").Append(Utc(SessionLogonTicks)).Append("\r\n");
			sb.Append("processStartUtc=").Append(Utc(ProcessStartTicks)).Append("\r\n");
			sb.Append("eventUtc=").Append(Utc(EventTicks)).Append("\r\n");
			return sb.ToString();
		}

		public static Breath? Parse(string? text)
		{
			if (string.IsNullOrEmpty(text))
			{
				return null;
			}
			Breath b = new Breath();
			bool any = false;
			foreach (string raw in text.Split('\n'))
			{
				string line = raw.TrimEnd('\r');
				int eq = line.IndexOf('=');
				if (eq <= 0)
				{
					continue;
				}
				string key = line.Substring(0, eq);
				string value = line.Substring(eq + 1);
				switch (key)
				{
				case "role": b.Role = value; any = true; break;
				case "pid": int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out b.Pid); break;
				case "session": int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out b.Session); break;
				case "sessionLogon": long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out b.SessionLogonTicks); break;
				case "processStart": long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out b.ProcessStartTicks); break;
				case "state": b.State = value; break;
				case "event": b.Event = value; break;
				case "eventTime": long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out b.EventTicks); break;
				case "detail": b.Detail = value; break;
				case "build": b.Build = value; break;
				}
			}
			return any && b.Pid > 0 ? b : null;
		}
	}

	private static readonly object Gate = new object();

	private static string? _role;

	private static string? _dirOverride;

	private static Action<string>? _sink;

	private static long _processStartTicks;

	private static int _sessionId = -1;

	private static long _sessionLogonTicks;

	private static int _sessionLogonQueried;

	private static string _build = "";

	private static string _lastState = "";

	private static int _writes;

	internal static string DefaultDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Win81Layer", "forensics");

	internal static string Dir => _dirOverride ?? DefaultDir;

	internal static string Role => _role ?? "process";

	internal static bool Configured => _role != null;

	internal static string LastState => _lastState;

	internal static int SessionId
	{
		get
		{
			if (_sessionId < 0)
			{
				try { _sessionId = Process.GetCurrentProcess().SessionId; } catch { }
			}
			return _sessionId;
		}
	}

	internal static long ProcessStartTicks => _processStartTicks;

	// The logon time of this process's session, queried once per process (first breath write, which runs off the UI
	// thread) and cached, 0 if Windows would not say.
	internal static long SessionLogonTicks
	{
		get
		{
			if (Interlocked.Exchange(ref _sessionLogonQueried, 1) == 0)
			{
				Interlocked.Exchange(ref _sessionLogonTicks, SessionLogonUtcTicks(SessionId));
			}
			return Interlocked.Read(ref _sessionLogonTicks);
		}
	}

	// A read-only diagnostics process may only write into a folder it was given, never the live forensics folder.
	private static bool CanWrite => _role != null && (_dirOverride != null || !SettingsStore.ReadOnlyDiagnostics);

	internal static void Configure(string role, string? dirOverride = null, Action<string>? sink = null)
	{
		lock (Gate)
		{
			_role = role;
			_dirOverride = dirOverride;
			_sink = sink;
			_lastState = "";
			_writes = 0;
			_processStartTicks = ProcessStartUtcTicks(Environment.ProcessId);
			try
			{
				string? exe = Environment.ProcessPath;
				_build = exe != null ? File.GetLastWriteTimeUtc(exe).ToString("yyyy-MM-dd HH:mm:ss'Z'", CultureInfo.InvariantCulture) : "";
			}
			catch
			{
				_build = "";
			}
		}
	}

	internal static string Tag => "[forensics:" + Role + "] ";

	internal static void Log(string line)
	{
		try
		{
			Action<string>? sink = _sink;
			if (sink != null)
			{
				sink(Tag + line);
			}
			else
			{
				Logger.Log(Tag + line);
			}
		}
		catch
		{
		}
	}

	internal static string BreathPath(string role) => Path.Combine(Dir, role + ".breath");

	internal static Breath? ReadBreath(string role) => ReadBreathFile(BreathPath(role));

	internal static Breath? ReadBreathFile(string path)
	{
		try
		{
			if (!File.Exists(path))
			{
				return null;
			}
			using FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
			using StreamReader sr = new StreamReader(fs, Encoding.UTF8);
			return Breath.Parse(sr.ReadToEnd());
		}
		catch
		{
			return null;
		}
	}

	// Synchronous, write-through, atomic (temp + replace). Never throws, never writes over the record of a NEWER live
	// instance of the same role (a --healed restart overlaps its predecessor for a moment).
	internal static bool WriteBreath(string state, string evt, string detail = "", bool onlyIfFirst = false)
	{
		if (!CanWrite)
		{
			return false;
		}
		lock (Gate)
		{
			try
			{
				if (onlyIfFirst && _writes > 0)
				{
					return false;   // a later event (an early exit) already wrote the newer truth
				}
				string path = BreathPath(Role);
				int me = Environment.ProcessId;
				Breath? current = ReadBreathFile(path);
				if (current != null && current.Pid != me && current.ProcessStartTicks > _processStartTicks && _processStartTicks > 0
					&& ProcessAliveWithStart(current.Pid, current.ProcessStartTicks) == AliveState.Alive)
				{
					return false;
				}
				Breath b = new Breath
				{
					Role = Role,
					Pid = me,
					Session = SessionId,
					SessionLogonTicks = SessionLogonTicks,
					ProcessStartTicks = _processStartTicks,
					State = state,
					Event = evt,
					EventTicks = DateTime.UtcNow.Ticks,
					Detail = detail,
					Build = _build
				};
				Directory.CreateDirectory(Dir);
				string tmp = path + "." + me.ToString(CultureInfo.InvariantCulture) + ".tmp";
				byte[] bytes = Encoding.UTF8.GetBytes(b.Serialize());
				using (FileStream fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
				{
					fs.Write(bytes, 0, bytes.Length);
					fs.Flush(flushToDisk: true);
				}
				File.Move(tmp, path, overwrite: true);
				_lastState = state;
				_writes++;
				return true;
			}
			catch
			{
				return false;
			}
		}
	}

	internal static int Writes => _writes;

	// A session/exit event that may be the last thing this process does: optional log line, the breath record, then a
	// synchronous log flush (the async logger would otherwise lose the line with the process).
	internal static void Critical(string evt, string detail, string state, bool logLine)
	{
		try
		{
			if (logLine)
			{
				Log(detail.Length > 0 ? detail : evt);
			}
			WriteBreath(state, evt, detail);
		}
		catch
		{
		}
		try { Logger.FlushSync(); } catch { }
	}

	// AppDomain.ProcessExit: keep an exit reason written by Cleanup, otherwise record the exit code.
	internal static void NoteProcessExit(int exitCode, string extra)
	{
		try
		{
			Log($"ProcessExit: exit code {exitCode}{extra}");
			if (_lastState != StateExited)
			{
				WriteBreath(StateExited, "process exit", $"exit code {exitCode}{extra}");
			}
		}
		catch
		{
		}
		try { Logger.FlushSync(); } catch { }
	}

	// The previous instance of this role, judged against the live system. Reads the record BEFORE this process writes
	// its own; logs one line and returns the verdict.
	internal static (string Verdict, string Text) ReportPrevious(Breath? previous)
	{
		(string, string) r;
		try
		{
			if (previous != null && previous.Pid == Environment.ProcessId && previous.ProcessStartTicks == _processStartTicks)
			{
				return ("self", "record already belongs to this process");
			}
			r = AnalyzePrevious(previous, SessionId, BootTicksUtc(), SessionExists, ProcessAliveWithStart, SessionLogonUtcTicks);
		}
		catch (Exception ex)
		{
			r = ("error", "previous-instance analysis failed: " + ex.Message);
		}
		if (r.Item1 != "none")
		{
			Log(r.Item2);
		}
		return r;
	}

	// sessionLogon: the current WTS logon time of a session number (UTC ticks, 0 = unknown); null skips the reuse check.
	internal static (string Verdict, string Text) AnalyzePrevious(Breath? prev, int currentSession, long bootTicksUtc, Func<int, bool?> sessionExists, Func<int, long, AliveState> alive, Func<int, long>? sessionLogon = null)
	{
		if (prev == null)
		{
			return ("none", "no previous record");
		}
		string who = $"previous {(prev.Role.Length > 0 ? prev.Role : "instance")} pid {prev.Pid} (session {prev.Session}, started {Utc(prev.ProcessStartTicks)}, build {(prev.Build.Length > 0 ? prev.Build : "?")})";
		string last = $"last event '{prev.Event}' at {Utc(prev.EventTicks)}" + (prev.Detail.Length > 0 ? " [" + prev.Detail + "]" : "");
		if (prev.State == StateExited)
		{
			return ("clean", $"{who} exited cleanly: {last}.");
		}
		AliveState a = alive(prev.Pid, prev.ProcessStartTicks);
		if (a == AliveState.Alive)
		{
			return ("alive", $"{who} is still running; {last}.");
		}
		if (prev.State == StateCrashing)
		{
			return ("crashed", $"{who} died after an unhandled .NET exception: {last}.");
		}
		if (bootTicksUtc > 0 && prev.ProcessStartTicks > 0 && prev.ProcessStartTicks < bootTicksUtc - TimeSpan.TicksPerMinute)
		{
			string how = prev.State == StateSessionEnding ? "after Windows announced the end of the session (normal shutdown/restart)" : "without any session-end notice (power loss, a system crash, or a shutdown that never delivered WM_ENDSESSION)";
			return ("previous-boot", $"{who} ended with the previous boot {how}; Windows started again at {Utc(bootTicksUtc)}. {Capital(last)}.");
		}
		bool? exists = prev.Session >= 0 ? sessionExists(prev.Session) : null;
		if (exists == false)
		{
			return ("session-gone", $"{who} ended without an exit record; {last}. Session {prev.Session} no longer exists: the whole Windows logon session was torn down (logoff, or its winlogon ended) and Windows terminated every process in it. Not a launcher crash.");
		}
		// The number exists again, but it may belong to a NEW logon: Windows reuses session numbers once a torn-down
		// session is gone. Compare the logon time recorded with the record against the session's logon time now; a
		// record without one still tells when its process started, and a process cannot predate the logon it ran in.
		long nowLogon = 0L;
		if (exists == true && sessionLogon != null)
		{
			try { nowLogon = sessionLogon(prev.Session); } catch { nowLogon = 0L; }
		}
		if (nowLogon > 0L)
		{
			string? relogon = null;
			if (prev.SessionLogonTicks > 0L)
			{
				if (Math.Abs(nowLogon - prev.SessionLogonTicks) > TimeSpan.TicksPerSecond)
				{
					relogon = $"its logon was at {Utc(prev.SessionLogonTicks)}, the logon now holding that number was at {Utc(nowLogon)}";
				}
			}
			else if (prev.ProcessStartTicks > 0L && nowLogon > prev.ProcessStartTicks + TimeSpan.TicksPerSecond)
			{
				relogon = $"the logon now holding that number was at {Utc(nowLogon)}, after the previous instance started";
			}
			if (relogon != null)
			{
				return ("session-gone", $"{who} ended without an exit record; {last}. Session {prev.Session} exists again, but as a new Windows logon ({relogon}): the logon session it ran in was torn down (logoff, or its winlogon ended), Windows terminated every process in it and then reused the session number. Not a launcher crash.");
			}
		}
		if (prev.State == StateSessionEnding)
		{
			return ("session-ending", $"{who} ended after Windows announced the end of the session: {last}.");
		}
		if (exists == true && prev.Session != currentSession)
		{
			return ("other-session", $"{who} ended without an exit record; {last}. Its session {prev.Session} still exists, but this instance runs in session {currentSession} (user switch or a new logon).");
		}
		return ("killed-in-session", $"{who} ended without an exit record in this session; {last}. The launcher logged no exit, so it was terminated from outside (TerminateProcess, an injected ExitProcess such as a Git-Bash timeout/kill process-tree walk, a Task Scheduler stop) or died natively; check the Application log (Application Error / WER) at that time and the supervisor's 'shell exited' line.");
	}

	internal static string DescribeExitCode(uint code)
	{
		string num = $"exit code {code} (0x{code:X})";
		string meaning;
		switch (code)
		{
		case 0u: meaning = "normal exit"; break;
		case 1u: meaning = "generic failure; also what TerminateProcess from the supervisor or Task Manager's End task uses"; break;
		case 259u: meaning = "STILL_ACTIVE: the process had not exited"; break;
		case 0xFFFFFFFFu: meaning = "-1: terminated by a kill that passes -1 (for example .NET Process.Kill)"; break;
		case 0xC0000005u: meaning = "access violation: a native crash"; break;
		case 0xC00000FDu: meaning = "stack overflow"; break;
		case 0xC0000409u: meaning = "fail-fast / stack buffer overrun (Environment.FailFast or a security check)"; break;
		case 0xC0000602u: meaning = "fail-fast exception"; break;
		case 0xC0000374u: meaning = "heap corruption"; break;
		case 0xC0000142u: meaning = "DLL initialization failed (often an exhausted desktop heap or a session that is ending)"; break;
		case 0xC000013Au: meaning = "STATUS_CONTROL_C_EXIT: ended by a console control event or by logoff"; break;
		case 0x40010004u: meaning = "DBG_TERMINATE_PROCESS: terminated by a debugger or by Windows tearing the session down"; break;
		case 0x40000015u: meaning = "STATUS_FATAL_APP_EXIT (abort)"; break;
		case 0xE0434352u: meaning = ".NET unhandled exception"; break;
		case 0x80131506u: meaning = ".NET runtime internal error"; break;
		case 0x00041306u: meaning = "SCHED_S_TASK_TERMINATED: Task Scheduler ended the task"; break;
		default:
			if (code > 128u && code <= 128u + 31u)
			{
				uint sig = code - 128u;
				meaning = $"128+{SignalName(sig)}: ended by a POSIX-style signal; from Git-Bash this is an injected ExitProcess by an MSYS2 timeout/kill process-tree walk";
			}
			else if ((code & 0xC0000000u) == 0xC0000000u)
			{
				meaning = "NTSTATUS error: a native crash or a fatal system status";
			}
			else
			{
				meaning = "application-defined";
			}
			break;
		}
		return num + " = " + meaning;
	}

	private static string SignalName(uint sig)
	{
		return sig switch
		{
			1u => "SIGHUP",
			2u => "SIGINT",
			3u => "SIGQUIT",
			6u => "SIGABRT",
			9u => "SIGKILL",
			15u => "SIGTERM",
			_ => "signal " + sig.ToString(CultureInfo.InvariantCulture)
		};
	}

	// The supervisor's line when the shell it launched exits (its exit code is read from the kept process handle).
	internal static string FormatShellExit(int pid, uint exitCode, DateTime exitUtc, DateTime startUtc, double heartbeatAgeSec, Breath? shellBreath)
	{
		string uptime = startUtc > DateTime.MinValue && exitUtc >= startUtc ? FormatSpan(exitUtc - startUtc) : "?";
		string beat = heartbeatAgeSec >= 0.0 ? heartbeatAgeSec.ToString("0.0", CultureInfo.InvariantCulture) + " s before" : "none";
		string says;
		if (shellBreath != null && shellBreath.Pid == pid)
		{
			says = $"{shellBreath.State} / '{shellBreath.Event}' at {Utc(shellBreath.EventTicks)}" + (shellBreath.Detail.Length > 0 ? " [" + shellBreath.Detail + "]" : "");
		}
		else
		{
			says = "no record from this pid";
		}
		return $"shell pid {pid} exited at {exitUtc.ToLocalTime().ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture)} after {uptime} with {DescribeExitCode(exitCode)}; last heartbeat {beat}; the shell's own last breath: {says}";
	}

	internal static string FormatSpan(TimeSpan t)
	{
		if (t.TotalSeconds < 120.0)
		{
			return t.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture) + " s";
		}
		if (t.TotalHours < 2.0)
		{
			return t.TotalMinutes.ToString("0.0", CultureInfo.InvariantCulture) + " min";
		}
		return t.TotalHours.ToString("0.0", CultureInfo.InvariantCulture) + " h";
	}

	internal static string Utc(long ticks)
	{
		if (ticks <= 0)
		{
			return "?";
		}
		try
		{
			return new DateTime(ticks, DateTimeKind.Utc).ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture);
		}
		catch
		{
			return "?";
		}
	}

	private static string Capital(string s) => s.Length > 0 ? char.ToUpperInvariant(s[0]) + s.Substring(1) : s;

	private static string Clean(string s) => (s ?? "").Replace("\r\n", " | ").Replace('\r', ' ').Replace('\n', ' ');

	internal static long BootTicksUtc()
	{
		try
		{
			return DateTime.UtcNow.Ticks - Environment.TickCount64 * TimeSpan.TicksPerMillisecond;
		}
		catch
		{
			return 0L;
		}
	}

	// ---------------------------------------------------------------- live system probes (all read-only)

	internal static long ProcessStartUtcTicks(int pid)
	{
		nint h = pid == Environment.ProcessId ? GetCurrentProcess() : OpenProcess(ProcessQueryLimitedInformation, false, (uint)pid);
		if (h == IntPtr.Zero)
		{
			return 0L;
		}
		try
		{
			if (GetProcessTimes(h, out long created, out _, out _, out _) && created > 0)
			{
				return DateTime.FromFileTimeUtc(created).Ticks;
			}
			return 0L;
		}
		finally
		{
			if (pid != Environment.ProcessId)
			{
				CloseHandle(h);
			}
		}
	}

	// Alive only when the pid is running AND was created at the recorded time (pid reuse is common after a teardown).
	internal static AliveState ProcessAliveWithStart(int pid, long startTicksUtc)
	{
		if (pid <= 0)
		{
			return AliveState.Dead;
		}
		nint h = OpenProcess(ProcessQueryLimitedInformation, false, (uint)pid);
		if (h == IntPtr.Zero)
		{
			int err = Marshal.GetLastWin32Error();
			return err == 5 ? AliveState.Unknown : AliveState.Dead;   // access denied: exists, identity unverifiable
		}
		try
		{
			if (!GetExitCodeProcess(h, out uint code) || code != 259u)
			{
				return AliveState.Dead;
			}
			if (startTicksUtc <= 0)
			{
				return AliveState.Unknown;
			}
			if (GetProcessTimes(h, out long created, out _, out _, out _) && created > 0)
			{
				long actual = DateTime.FromFileTimeUtc(created).Ticks;
				return Math.Abs(actual - startTicksUtc) <= TimeSpan.TicksPerMillisecond * 20 ? AliveState.Alive : AliveState.ReusedPid;
			}
			return AliveState.Unknown;
		}
		finally
		{
			CloseHandle(h);
		}
	}

	// true / false, or null when the session list cannot be read.
	internal static bool? SessionExists(int sessionId)
	{
		nint list = IntPtr.Zero;
		try
		{
			if (!WTSEnumerateSessionsW(IntPtr.Zero, 0, 1, out list, out int count) || list == IntPtr.Zero)
			{
				return null;
			}
			int size = Marshal.SizeOf<WTS_SESSION_INFOW>();
			for (int i = 0; i < count; i++)
			{
				WTS_SESSION_INFOW info = Marshal.PtrToStructure<WTS_SESSION_INFOW>(list + i * size);
				if (info.SessionId == sessionId)
				{
					return true;
				}
			}
			return false;
		}
		catch
		{
			return null;
		}
		finally
		{
			if (list != IntPtr.Zero)
			{
				WTSFreeMemory(list);
			}
		}
	}

	// The WTS logon time of a session (UTC ticks), 0 when unknown (no such session, nobody logged on, or the query
	// failed). WTSSessionInfo returns a WTSINFOW: 8 DWORDs, WinStationName[32], Domain[17], UserName[21] (WCHARs), then
	// the LARGE_INTEGER times ConnectTime, DisconnectTime, LastInputTime, LogonTime (offset 200), CurrentTime: 216 bytes.
	internal static long SessionLogonUtcTicks(int sessionId)
	{
		if (sessionId < 0)
		{
			return 0L;
		}
		nint buffer = IntPtr.Zero;
		try
		{
			if (!WTSQuerySessionInformationW(IntPtr.Zero, sessionId, WTSSessionInfo, out buffer, out int bytes) || buffer == IntPtr.Zero || bytes < WtsInfoSize)
			{
				return 0L;
			}
			long logon = Marshal.ReadInt64(buffer, WtsInfoLogonTimeOffset);
			return logon > 0L ? DateTime.FromFileTimeUtc(logon).Ticks : 0L;
		}
		catch
		{
			return 0L;
		}
		finally
		{
			if (buffer != IntPtr.Zero)
			{
				WTSFreeMemory(buffer);
			}
		}
	}

	private const int WTSSessionInfo = 24;

	private const int WtsInfoSize = 216;

	private const int WtsInfoLogonTimeOffset = 200;

	// Which job this process is in and what it allows: a Task Scheduler stop terminates the task's job, so this line
	// tells the next incident whether the supervisor, the shell and the apps it started would die together.
	internal static string JobSummary()
	{
		try
		{
			if (!IsProcessInJob(GetCurrentProcess(), IntPtr.Zero, out bool inJob))
			{
				return "job: unknown (IsProcessInJob error " + Marshal.GetLastWin32Error() + ")";
			}
			if (!inJob)
			{
				return "job: none";
			}
			string limits;
			JOBOBJECT_EXTENDED_LIMIT_INFORMATION ext = default;
			if (QueryInformationJobObject(IntPtr.Zero, 9, ref ext, Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>(), out _))
			{
				limits = "limits=" + DescribeJobLimits(ext.BasicLimitInformation.LimitFlags);
			}
			else
			{
				limits = "limits unreadable (error " + Marshal.GetLastWin32Error() + ")";
			}
			string procs = "";
			JOBOBJECT_BASIC_ACCOUNTING_INFORMATION acc = default;
			if (QueryInformationJobObject(IntPtr.Zero, 1, ref acc, Marshal.SizeOf<JOBOBJECT_BASIC_ACCOUNTING_INFORMATION>(), out _))
			{
				procs = $", active processes={acc.ActiveProcesses}, total={acc.TotalProcesses}";
			}
			return $"job: yes ({limits}{procs})";
		}
		catch (Exception ex)
		{
			return "job: unknown (" + ex.Message + ")";
		}
	}

	internal static string DescribeJobLimits(uint flags)
	{
		if (flags == 0)
		{
			return "none";
		}
		List<string> parts = new List<string>();
		void Add(uint bit, string name)
		{
			if ((flags & bit) != 0)
			{
				parts.Add(name);
				flags &= ~bit;
			}
		}
		Add(0x2000u, "KILL_ON_JOB_CLOSE");
		Add(0x0800u, "BREAKAWAY_OK");
		Add(0x1000u, "SILENT_BREAKAWAY_OK");
		Add(0x0400u, "DIE_ON_UNHANDLED_EXCEPTION");
		Add(0x0008u, "ACTIVE_PROCESS");
		Add(0x0004u, "JOB_TIME");
		Add(0x0002u, "PROCESS_TIME");
		Add(0x0100u, "PROCESS_MEMORY");
		Add(0x0200u, "JOB_MEMORY");
		Add(0x0001u, "WORKINGSET");
		Add(0x0010u, "AFFINITY");
		Add(0x0020u, "PRIORITY_CLASS");
		Add(0x0040u, "PRESERVE_JOB_TIME");
		Add(0x0080u, "SCHEDULING_CLASS");
		if (flags != 0)
		{
			parts.Add("0x" + flags.ToString("X", CultureInfo.InvariantCulture));
		}
		return string.Join("|", parts);
	}

	internal static int ExtendedLimitInfoSize => Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>();

	internal static int BasicAccountingInfoSize => Marshal.SizeOf<JOBOBJECT_BASIC_ACCOUNTING_INFORMATION>();

	// ---------------------------------------------------------------- process-wide handlers

	private delegate bool ConsoleCtrlDelegate(uint ctrlType);

	private static ConsoleCtrlDelegate? _ctrlHandler;   // rooted for the process lifetime

	// A GUI process has no console, but a console control routine can still be injected into it (Git-Bash's kill/timeout
	// does exactly that for SIGINT/SIGQUIT). Log it, and survive Ctrl+C / Ctrl+Break: nothing legitimate stops the shell
	// that way. CLOSE/LOGOFF/SHUTDOWN keep the default handling.
	internal static bool InstallConsoleControlHandler()
	{
		try
		{
			if (_ctrlHandler != null)
			{
				return true;
			}
			ConsoleCtrlDelegate handler = OnConsoleControl;
			if (SetConsoleCtrlHandler(handler, true))
			{
				_ctrlHandler = handler;
				return true;
			}
		}
		catch
		{
		}
		return false;
	}

	internal static void RemoveConsoleControlHandlerForTest()
	{
		try
		{
			if (_ctrlHandler != null)
			{
				SetConsoleCtrlHandler(_ctrlHandler, false);
				_ctrlHandler = null;
			}
		}
		catch
		{
		}
	}

	internal static bool OnConsoleControl(uint ctrlType)
	{
		string name = ctrlType switch
		{
			0u => "CTRL_C_EVENT",
			1u => "CTRL_BREAK_EVENT",
			2u => "CTRL_CLOSE_EVENT",
			5u => "CTRL_LOGOFF_EVENT",
			6u => "CTRL_SHUTDOWN_EVENT",
			_ => "console control " + ctrlType.ToString(CultureInfo.InvariantCulture)
		};
		bool swallow = ctrlType <= 1u;
		Critical("console-control", name + (swallow ? " received - ignored, staying up" : " received - default handling, the process will end"), swallow ? StateRunning : StateExiting, logLine: true);
		return swallow;
	}

	private static int _handlersInstalled;

	// Supervisor only: the shell registers its own AppDomain handlers in OnStartup and calls into this class from there.
	internal static void InstallProcessHandlers()
	{
		if (Interlocked.Exchange(ref _handlersInstalled, 1) != 0)
		{
			return;
		}
		AppDomain.CurrentDomain.UnhandledException += delegate(object _, UnhandledExceptionEventArgs args)
		{
			string what = args.ExceptionObject is Exception ex ? ex.GetType().Name + ": " + ex.Message : Convert.ToString(args.ExceptionObject, CultureInfo.InvariantCulture) ?? "?";
			try { Logger.Log("Unhandled exception (" + Role + "): " + args.ExceptionObject); } catch { }
			Critical("unhandled-exception", what, args.IsTerminating ? StateCrashing : StateRunning, logLine: false);
		};
		AppDomain.CurrentDomain.ProcessExit += delegate
		{
			NoteProcessExit(Environment.ExitCode, "");
		};
		TaskScheduler.UnobservedTaskException += delegate(object? _, UnobservedTaskExceptionEventArgs args)
		{
			try { Log("unobserved task exception (non-fatal): " + args.Exception?.GetBaseException().Message); } catch { }
			args.SetObserved();
		};
	}

	// ---------------------------------------------------------------- interop

	private const uint ProcessQueryLimitedInformation = 0x1000u;

	[StructLayout(LayoutKind.Sequential)]
	private struct WTS_SESSION_INFOW
	{
		public int SessionId;

		public nint pWinStationName;

		public int State;
	}

	[StructLayout(LayoutKind.Sequential)]
	private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
	{
		public long PerProcessUserTimeLimit;

		public long PerJobUserTimeLimit;

		public uint LimitFlags;

		public nuint MinimumWorkingSetSize;

		public nuint MaximumWorkingSetSize;

		public uint ActiveProcessLimit;

		public nuint Affinity;

		public uint PriorityClass;

		public uint SchedulingClass;
	}

	[StructLayout(LayoutKind.Sequential)]
	private struct IO_COUNTERS
	{
		public ulong ReadOperationCount;

		public ulong WriteOperationCount;

		public ulong OtherOperationCount;

		public ulong ReadTransferCount;

		public ulong WriteTransferCount;

		public ulong OtherTransferCount;
	}

	[StructLayout(LayoutKind.Sequential)]
	private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
	{
		public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;

		public IO_COUNTERS IoInfo;

		public nuint ProcessMemoryLimit;

		public nuint JobMemoryLimit;

		public nuint PeakProcessMemoryUsed;

		public nuint PeakJobMemoryUsed;
	}

	[StructLayout(LayoutKind.Sequential)]
	private struct JOBOBJECT_BASIC_ACCOUNTING_INFORMATION
	{
		public long TotalUserTime;

		public long TotalKernelTime;

		public long ThisPeriodTotalUserTime;

		public long ThisPeriodTotalKernelTime;

		public uint TotalPageFaultCount;

		public uint TotalProcesses;

		public uint ActiveProcesses;

		public uint TotalTerminatedProcesses;
	}

	[DllImport("kernel32.dll", SetLastError = true)]
	private static extern nint OpenProcess(uint access, bool inherit, uint pid);

	[DllImport("kernel32.dll")]
	private static extern nint GetCurrentProcess();

	[DllImport("kernel32.dll", SetLastError = true)]
	private static extern bool GetProcessTimes(nint h, out long creation, out long exit, out long kernel, out long user);

	[DllImport("kernel32.dll")]
	private static extern bool GetExitCodeProcess(nint h, out uint code);

	[DllImport("kernel32.dll")]
	private static extern bool CloseHandle(nint h);

	[DllImport("kernel32.dll", SetLastError = true)]
	private static extern bool IsProcessInJob(nint process, nint job, out bool result);

	[DllImport("kernel32.dll", SetLastError = true)]
	private static extern bool QueryInformationJobObject(nint job, int infoClass, ref JOBOBJECT_EXTENDED_LIMIT_INFORMATION info, int length, out int returned);

	[DllImport("kernel32.dll", SetLastError = true)]
	private static extern bool QueryInformationJobObject(nint job, int infoClass, ref JOBOBJECT_BASIC_ACCOUNTING_INFORMATION info, int length, out int returned);

	[DllImport("kernel32.dll", SetLastError = true)]
	private static extern bool SetConsoleCtrlHandler(ConsoleCtrlDelegate handler, bool add);

	[DllImport("wtsapi32.dll", SetLastError = true)]
	private static extern bool WTSEnumerateSessionsW(nint server, int reserved, int version, out nint sessionInfo, out int count);

	[DllImport("wtsapi32.dll")]
	private static extern void WTSFreeMemory(nint memory);

	[DllImport("wtsapi32.dll", SetLastError = true)]
	private static extern bool WTSQuerySessionInformationW(nint server, int sessionId, int infoClass, out nint buffer, out int bytes);
}
