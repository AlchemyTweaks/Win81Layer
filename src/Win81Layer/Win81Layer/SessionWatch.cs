#nullable enable

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Threading;

namespace Win81Layer;

// A hidden top-level window on its own thread that hears what Windows says to the whole session: WM_QUERYENDSESSION /
// WM_ENDSESSION (with the lParam flags), WTS session changes (lock, logoff, console connect...), DWM composition
// changes, display changes and suspend/resume. It logs them and raises events; it never blocks a query (always answers
// TRUE) and never touches another process's windows. A plain Win32 window rather than a WPF one, so it keeps answering
// even while the WPF dispatcher is busy or has shut down (message-only windows do not receive these broadcasts).
internal sealed class SessionWatch : IDisposable
{
	internal sealed class Options
	{
		public string Role = "process";

		public bool RegisterWts = true;

		public bool LogLock = true;

		public bool LogDisplay = true;

		public bool LogPower = true;

		public Action<string>? Log;
	}

	internal const uint WM_QUERYENDSESSION = 0x0011u;

	internal const uint WM_ENDSESSION = 0x0016u;

	internal const uint WM_DISPLAYCHANGE = 0x007Eu;

	internal const uint WM_POWERBROADCAST = 0x0218u;

	internal const uint WM_WTSSESSION_CHANGE = 0x02B1u;

	internal const uint WM_DWMCOMPOSITIONCHANGED = 0x031Eu;

	private const uint WM_CLOSE = 0x0010u;

	private const uint WM_DESTROY = 0x0002u;

	private const uint WM_TIMER = 0x0113u;

	private const nint DisplayTimerId = 0x5157;

	internal const uint ENDSESSION_CLOSEAPP = 0x00000001u;

	internal const uint ENDSESSION_CRITICAL = 0x40000000u;

	internal const uint ENDSESSION_LOGOFF = 0x80000000u;

	private static int _seq;

	private readonly Options _opts;

	private readonly ManualResetEventSlim _ready = new ManualResetEventSlim(false);

	private WndProcDelegate? _proc;   // rooted: the window class points at it

	private Thread? _thread;

	private string _className = "";

	private bool _wtsRegistered;

	private int _displayChanges;

	private string _lastDisplay = "";

	private volatile bool _disposing;

	private int _stopped;

	public event Action<uint>? QueryEndSession;

	public event Action<bool, uint>? EndSession;

	public event Action? CompositionChanged;

	// The watch could not start, or its loop ended without Dispose: nothing hears WM_ENDSESSION any more. Raised once,
	// on the watch thread.
	public event Action<string>? Stopped;

	public bool IsStopped => Volatile.Read(ref _stopped) != 0;

	public nint Hwnd { get; private set; }

	public int ThreadId { get; private set; }

	public int MessagesHandled;

	public SessionWatch(Options options)
	{
		_opts = options;
	}

	public bool Start(int timeoutMs = 3000)
	{
		_thread = new Thread(ThreadMain)
		{
			IsBackground = true,
			Name = "SessionWatch." + _opts.Role
		};
		_thread.Start();
		return WaitReady(timeoutMs);
	}

	public bool WaitReady(int timeoutMs) => _ready.Wait(timeoutMs) && Hwnd != IntPtr.Zero;

	private void Log(string line)
	{
		try
		{
			Action<string>? log = _opts.Log;
			if (log != null)
			{
				log(line);
			}
			else
			{
				Logger.Log("[forensics:" + _opts.Role + "] " + line);
			}
		}
		catch
		{
		}
	}

	private void ThreadMain()
	{
		try
		{
			_proc = WndProc;
			_className = "Win81Layer.SessionWatch." + Environment.ProcessId.ToString(CultureInfo.InvariantCulture) + "." + Interlocked.Increment(ref _seq).ToString(CultureInfo.InvariantCulture);
			nint hInstance = GetModuleHandleW(null);
			WNDCLASSEXW wc = new WNDCLASSEXW
			{
				cbSize = (uint)Marshal.SizeOf<WNDCLASSEXW>(),
				lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_proc),
				hInstance = hInstance,
				lpszClassName = _className
			};
			if (RegisterClassExW(ref wc) == 0)
			{
				Log("session watch: RegisterClassEx failed, error " + Marshal.GetLastWin32Error());
				return;
			}
			// WS_POPUP, never shown; WS_EX_TOOLWINDOW keeps it out of Alt+Tab and the taskbar even if something showed it.
			nint h = CreateWindowExW(0x00000080u | 0x08000000u, _className, "Win81Layer session watch", 0x80000000u, 0, 0, 0, 0, IntPtr.Zero, IntPtr.Zero, hInstance, IntPtr.Zero);
			if (h == IntPtr.Zero)
			{
				Log("session watch: CreateWindowEx failed, error " + Marshal.GetLastWin32Error());
				return;
			}
			ThreadId = GetCurrentThreadId();
			Hwnd = h;
			if (_opts.RegisterWts)
			{
				_wtsRegistered = WTSRegisterSessionNotification(h, 0);   // NOTIFY_FOR_THIS_SESSION
			}
		}
		catch (Exception ex)
		{
			Log("session watch failed to start: " + ex.Message);
		}
		finally
		{
			_ready.Set();
		}
		if (Hwnd == IntPtr.Zero)
		{
			RaiseStopped("the session watch window could not be created");
			return;
		}
		string why = "its message loop ended";
		try
		{
			while (GetMessageW(out MSG msg, IntPtr.Zero, 0, 0) > 0)
			{
				TranslateMessage(ref msg);
				DispatchMessageW(ref msg);
			}
		}
		catch (Exception ex)
		{
			why = "its message loop failed: " + ex.Message;
		}
		if (!_disposing)
		{
			Log("session watch stopped unexpectedly: " + why);
			RaiseStopped(why);
		}
	}

	private void RaiseStopped(string why)
	{
		if (_disposing || Interlocked.Exchange(ref _stopped, 1) != 0)
		{
			return;
		}
		try
		{
			Stopped?.Invoke(why);
		}
		catch (Exception ex)
		{
			Log("Stopped handler: " + ex.Message);
		}
	}

	private nint WndProc(nint hwnd, uint msg, nint wParam, nint lParam)
	{
		try
		{
			switch (msg)
			{
			case WM_QUERYENDSESSION:
			{
				MessagesHandled++;
				uint flags = unchecked((uint)(long)lParam);
				Log($"WM_QUERYENDSESSION flags={DescribeEndSessionFlags(flags)} SM_SHUTTINGDOWN={(SessionEndPolicy.ShuttingDown() ? 1 : 0)} - answering TRUE");
				try { QueryEndSession?.Invoke(flags); } catch (Exception ex) { Log("QueryEndSession handler: " + ex.Message); }
				return 1;
			}
			case WM_ENDSESSION:
			{
				MessagesHandled++;
				uint flags = unchecked((uint)(long)lParam);
				bool ending = wParam != IntPtr.Zero;
				Log($"WM_ENDSESSION({(ending ? "TRUE: the session is ending" : "FALSE: the end of the session was cancelled")}) flags={DescribeEndSessionFlags(flags)} SM_SHUTTINGDOWN={(SessionEndPolicy.ShuttingDown() ? 1 : 0)}");
				try { EndSession?.Invoke(ending, flags); } catch (Exception ex) { Log("EndSession handler: " + ex.Message); }
				return IntPtr.Zero;
			}
			case WM_WTSSESSION_CHANGE:
			{
				MessagesHandled++;
				int code = (int)(long)wParam;
				bool lockish = code == 7 || code == 8;
				if (!lockish || _opts.LogLock)
				{
					Log($"session change: {DescribeWts(code)} (session {(long)lParam})");
				}
				return IntPtr.Zero;
			}
			case WM_DWMCOMPOSITIONCHANGED:
			{
				MessagesHandled++;
				bool enabled = false;
				try { DwmIsCompositionEnabled(out enabled); } catch { }
				Log($"WM_DWMCOMPOSITIONCHANGED (composition enabled={enabled})");
				try { CompositionChanged?.Invoke(); } catch (Exception ex) { Log("CompositionChanged handler: " + ex.Message); }
				return IntPtr.Zero;
			}
			case WM_DISPLAYCHANGE:
			{
				MessagesHandled++;
				if (_opts.LogDisplay)
				{
					// Monitors waking or sleeping can send bursts: coalesce into one line 1.5 s after the last one.
					long lp = (long)lParam;
					_lastDisplay = $"{lp & 0xFFFF}x{(lp >> 16) & 0xFFFF} {(long)wParam}bpp";
					_displayChanges++;
					SetTimer(hwnd, DisplayTimerId, 1500u, IntPtr.Zero);
				}
				break;
			}
			case WM_TIMER:
				if (wParam == DisplayTimerId)
				{
					KillTimer(hwnd, DisplayTimerId);
					int n = _displayChanges;
					_displayChanges = 0;
					Log($"display changed: primary {_lastDisplay}, monitors={GetSystemMetrics(80)} ({n} notification{(n == 1 ? "" : "s")})");
					return IntPtr.Zero;
				}
				break;
			case WM_POWERBROADCAST:
			{
				MessagesHandled++;
				int pbt = (int)(long)wParam;
				if (_opts.LogPower && (pbt == 4 || pbt == 7 || pbt == 0x12))
				{
					Log(pbt == 4 ? "power: suspending" : (pbt == 7 ? "power: resumed (user)" : "power: resumed (automatic)"));
				}
				return 1;
			}
			case WM_CLOSE:
				if (!_disposing)
				{
					// Only Dispose may close the watch: a WM_CLOSE from outside (taskkill without /F closes every top-level
					// window of the process) would otherwise silently end the session-end handling.
					Log("WM_CLOSE from outside ignored: the session watch keeps running");
					return IntPtr.Zero;
				}
				DestroyWindow(hwnd);
				return IntPtr.Zero;
			case WM_DESTROY:
				if (_wtsRegistered)
				{
					try { WTSUnRegisterSessionNotification(hwnd); } catch { }
					_wtsRegistered = false;
				}
				PostQuitMessage(0);
				return IntPtr.Zero;
			}
		}
		catch
		{
		}
		return DefWindowProcW(hwnd, msg, wParam, lParam);
	}

	internal static string DescribeEndSessionFlags(uint flags)
	{
		if (flags == 0u)
		{
			return "0 (shutdown or restart)";
		}
		List<string> parts = new List<string>();
		if ((flags & ENDSESSION_LOGOFF) != 0)
		{
			parts.Add("LOGOFF");
		}
		if ((flags & ENDSESSION_CRITICAL) != 0)
		{
			parts.Add("CRITICAL");
		}
		if ((flags & ENDSESSION_CLOSEAPP) != 0)
		{
			parts.Add("CLOSEAPP (Restart Manager)");
		}
		uint rest = flags & ~(ENDSESSION_LOGOFF | ENDSESSION_CRITICAL | ENDSESSION_CLOSEAPP);
		if (rest != 0)
		{
			parts.Add("0x" + rest.ToString("X", CultureInfo.InvariantCulture));
		}
		return "0x" + flags.ToString("X", CultureInfo.InvariantCulture) + " " + string.Join("|", parts);
	}

	internal static string DescribeWts(int code)
	{
		return code switch
		{
			1 => "CONSOLE_CONNECT",
			2 => "CONSOLE_DISCONNECT",
			3 => "REMOTE_CONNECT",
			4 => "REMOTE_DISCONNECT",
			5 => "SESSION_LOGON",
			6 => "SESSION_LOGOFF",
			7 => "SESSION_LOCK",
			8 => "SESSION_UNLOCK",
			9 => "SESSION_REMOTE_CONTROL",
			10 => "SESSION_CREATE",
			11 => "SESSION_TERMINATE",
			_ => "code " + code.ToString(CultureInfo.InvariantCulture)
		};
	}

	public void Dispose()
	{
		try
		{
			_disposing = true;
			nint h = Hwnd;
			if (h != IntPtr.Zero)
			{
				PostMessageW(h, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
				_thread?.Join(2000);
				Hwnd = IntPtr.Zero;
			}
			if (_className.Length > 0)
			{
				UnregisterClassW(_className, GetModuleHandleW(null));
			}
		}
		catch
		{
		}
	}

	private delegate nint WndProcDelegate(nint hwnd, uint msg, nint wParam, nint lParam);

	[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
	private struct WNDCLASSEXW
	{
		public uint cbSize;

		public uint style;

		public nint lpfnWndProc;

		public int cbClsExtra;

		public int cbWndExtra;

		public nint hInstance;

		public nint hIcon;

		public nint hCursor;

		public nint hbrBackground;

		[MarshalAs(UnmanagedType.LPWStr)]
		public string? lpszMenuName;

		[MarshalAs(UnmanagedType.LPWStr)]
		public string lpszClassName;

		public nint hIconSm;
	}

	[StructLayout(LayoutKind.Sequential)]
	private struct MSG
	{
		public nint hwnd;

		public uint message;

		public nint wParam;

		public nint lParam;

		public uint time;

		public int ptX;

		public int ptY;
	}

	[DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
	private static extern ushort RegisterClassExW(ref WNDCLASSEXW wc);

	[DllImport("user32.dll", CharSet = CharSet.Unicode)]
	private static extern bool UnregisterClassW(string className, nint hInstance);

	[DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
	private static extern nint CreateWindowExW(uint exStyle, string className, string windowName, uint style, int x, int y, int w, int h, nint parent, nint menu, nint hInstance, nint param);

	[DllImport("user32.dll")]
	private static extern bool DestroyWindow(nint hwnd);

	[DllImport("user32.dll")]
	private static extern nint DefWindowProcW(nint hwnd, uint msg, nint wParam, nint lParam);

	[DllImport("user32.dll")]
	private static extern int GetMessageW(out MSG msg, nint hwnd, uint min, uint max);

	[DllImport("user32.dll")]
	private static extern bool TranslateMessage(ref MSG msg);

	[DllImport("user32.dll")]
	private static extern nint DispatchMessageW(ref MSG msg);

	[DllImport("user32.dll")]
	private static extern void PostQuitMessage(int code);

	[DllImport("user32.dll")]
	private static extern bool PostMessageW(nint hwnd, uint msg, nint wParam, nint lParam);

	[DllImport("user32.dll")]
	private static extern nint SetTimer(nint hwnd, nint id, uint ms, nint proc);

	[DllImport("user32.dll")]
	private static extern bool KillTimer(nint hwnd, nint id);

	[DllImport("user32.dll")]
	private static extern int GetSystemMetrics(int index);

	[DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
	private static extern nint GetModuleHandleW(string? name);

	[DllImport("kernel32.dll")]
	private static extern int GetCurrentThreadId();

	[DllImport("wtsapi32.dll", SetLastError = true)]
	private static extern bool WTSRegisterSessionNotification(nint hwnd, int flags);

	[DllImport("wtsapi32.dll")]
	private static extern bool WTSUnRegisterSessionNotification(nint hwnd);

	[DllImport("dwmapi.dll")]
	private static extern int DwmIsCompositionEnabled(out bool enabled);
}

// What the shell and the supervisor do with session-end notifications. Pure decisions, so the offscreen test can cover
// every combination without a real logoff.
internal static class SessionEndPolicy
{
	internal enum ShellAction
	{
		Continue,                 // WM_ENDSESSION(FALSE): the shutdown/logoff was cancelled, keep running
		StayUpRestartManager,     // CLOSEAPP: an installer's Restart Manager wants files freed; the session continues
		StayUpNotShuttingDown,    // WM_ENDSESSION(TRUE) while the session is not shutting down: treat as spurious
		ExitSessionEnding         // the session really ends: fast exit, native shell untouched (it is going away too)
	}

	internal static ShellAction DecideShell(bool ending, uint flags, bool shuttingDown)
	{
		if (!ending)
		{
			return ShellAction.Continue;
		}
		// A session that is really shutting down wins over CLOSEAPP (Windows also sets it when the system is being
		// serviced or resources are exhausted): staying up there would only skip the fast exit and its flush.
		if (shuttingDown)
		{
			return ShellAction.ExitSessionEnding;
		}
		return (flags & SessionWatch.ENDSESSION_CLOSEAPP) != 0 ? ShellAction.StayUpRestartManager : ShellAction.StayUpNotShuttingDown;
	}

	internal static bool ShuttingDown()
	{
		try
		{
			return GetSystemMetrics(0x2000) != 0;   // SM_SHUTTINGDOWN
		}
		catch
		{
			return false;
		}
	}

	// The supervisor's view of a pending session end (UTC ticks, 0 = none). Written on the watch thread, read by the loop.
	internal sealed class SupervisorState
	{
		public long QueryTicks;

		public long EndTicks;

		public long RestartManagerTicks;
	}

	internal static readonly TimeSpan QueryHold = TimeSpan.FromSeconds(60.0);

	internal static readonly TimeSpan EndHold = TimeSpan.FromSeconds(30.0);

	internal static readonly TimeSpan RestartManagerHold = TimeSpan.FromSeconds(10.0);

	internal static void NoteQuery(SupervisorState s, DateTime nowUtc)
	{
		Interlocked.Exchange(ref s.QueryTicks, nowUtc.Ticks);
	}

	// Classified like DecideShell: a real shutdown with CLOSEAPP is a session end, not a Restart Manager request.
	internal static void NoteEnd(SupervisorState s, bool ending, uint flags, bool shuttingDown, DateTime nowUtc)
	{
		Interlocked.Exchange(ref s.QueryTicks, 0L);
		if (!ending)
		{
			Interlocked.Exchange(ref s.EndTicks, 0L);
			Interlocked.Exchange(ref s.RestartManagerTicks, 0L);
			return;
		}
		if (!shuttingDown && (flags & SessionWatch.ENDSESSION_CLOSEAPP) != 0)
		{
			Interlocked.Exchange(ref s.RestartManagerTicks, nowUtc.Ticks);
		}
		else
		{
			Interlocked.Exchange(ref s.EndTicks, nowUtc.Ticks);
		}
	}

	// Why the supervisor must NOT relaunch a missing shell right now, or null. Every hold expires by itself, so a
	// notification that turns out to be spurious (the session goes on) can never stop supervision for good.
	internal static string? Hold(SupervisorState s, DateTime nowUtc, bool shuttingDown)
	{
		long end = Interlocked.Read(ref s.EndTicks);
		if (end != 0L)
		{
			if (shuttingDown || nowUtc.Ticks - end < EndHold.Ticks)
			{
				return "Windows is ending the session (WM_ENDSESSION)";
			}
			Interlocked.CompareExchange(ref s.EndTicks, 0L, end);
		}
		long query = Interlocked.Read(ref s.QueryTicks);
		if (query != 0L)
		{
			if (nowUtc.Ticks - query < QueryHold.Ticks)
			{
				return "Windows asked whether the session may end (WM_QUERYENDSESSION) and has not confirmed or cancelled yet";
			}
			Interlocked.CompareExchange(ref s.QueryTicks, 0L, query);
		}
		long rm = Interlocked.Read(ref s.RestartManagerTicks);
		if (rm != 0L)
		{
			if (shuttingDown || nowUtc.Ticks - rm < RestartManagerHold.Ticks)
			{
				return "Restart Manager asked processes to close (an installer is running)";
			}
			Interlocked.CompareExchange(ref s.RestartManagerTicks, 0L, rm);
		}
		return null;
	}

	[DllImport("user32.dll")]
	private static extern int GetSystemMetrics(int index);
}
