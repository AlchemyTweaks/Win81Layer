using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace Win81Layer;

// A cheap note of the cross-process call a thread is about to make (activate, minimize, foreground, tray click). If
// the call has not returned after StallMs, a one-shot timer writes the note to the log once, so a freeze names its own
// trigger instead of leaving a silent gap before the supervisor restarts the shell. Nothing runs while no call is in
// flight: Enter arms the timer, leaving the scope disarms it.
internal static class Breadcrumbs
{
	private sealed class Crumb
	{
		public int Token;

		public string Route = "";

		public nint Target;

		public nint Foreground;

		public int ThreadId;

		public long Since;
	}

	// Long enough that a normal busy moment never logs; far below the supervisor's 30 s restart.
	internal static int StallMs = 3000;

	// The last stall report (also logged). Read by --traytest.
	internal static volatile string LastStall;

	private static readonly object Gate = new object();

	private static readonly List<Crumb> Open = new List<Crumb>();

	private static Timer _timer;

	private static int _token;

	internal readonly struct Scope : IDisposable
	{
		private readonly int _token;

		public Scope(int token)
		{
			_token = token;
		}

		public void Dispose()
		{
			Exit(_token);
		}
	}

	internal static Scope Enter(string route, nint target)
	{
		try
		{
			Crumb c = new Crumb
			{
				Route = route,
				Target = target,
				Foreground = GetForegroundWindow(),
				ThreadId = Environment.CurrentManagedThreadId,
				Since = Environment.TickCount64
			};
			lock (Gate)
			{
				c.Token = ++_token;
				Open.Add(c);
				_timer ??= new Timer(OnStall, null, Timeout.Infinite, Timeout.Infinite);
				_timer.Change(StallMs, Timeout.Infinite);
			}
			return new Scope(c.Token);
		}
		catch
		{
			return new Scope(0);
		}
	}

	private static void Exit(int token)
	{
		if (token == 0)
		{
			return;
		}
		try
		{
			lock (Gate)
			{
				Open.RemoveAll(c => c.Token == token);
				if (Open.Count == 0)
				{
					_timer?.Change(Timeout.Infinite, Timeout.Infinite);
				}
				else
				{
					long oldest = long.MaxValue;
					foreach (Crumb c in Open)
					{
						oldest = Math.Min(oldest, c.Since);
					}
					long due = Math.Max(1L, StallMs - (Environment.TickCount64 - oldest));
					_timer?.Change(due, Timeout.Infinite);
				}
			}
		}
		catch
		{
		}
	}

	private static void OnStall(object _)
	{
		try
		{
			List<Crumb> stuck = new List<Crumb>();
			long now = Environment.TickCount64;
			lock (Gate)
			{
				foreach (Crumb c in Open)
				{
					if (now - c.Since >= StallMs - 50 && c.Token > 0)
					{
						stuck.Add(c);
					}
				}
				// Report each stuck call once: forget it (its scope's exit then finds nothing to remove).
				foreach (Crumb c in stuck)
				{
					Open.Remove(c);
				}
				if (Open.Count > 0)
				{
					_timer?.Change(Math.Max(1L, StallMs / 2), Timeout.Infinite);
				}
			}
			foreach (Crumb c in stuck)
			{
				// Only calls that send no window message: safe while the caller is blocked on another app.
				string text = $"[hang] thread {c.ThreadId} has been inside {c.Route} for {now - c.Since} ms: target={Describe(c.Target)} foreground-at-entry={Describe(c.Foreground)} foreground-now={Describe(GetForegroundWindow())}";
				LastStall = text;
				Logger.Log(text);
			}
		}
		catch
		{
		}
	}

	internal static string Describe(nint hwnd)
	{
		if (hwnd == IntPtr.Zero)
		{
			return "none";
		}
		try
		{
			uint tid = GetWindowThreadProcessId(hwnd, out uint pid);
			StringBuilder cls = new StringBuilder(64);
			GetClassName(hwnd, cls, cls.Capacity);
			string proc = "?";
			try
			{
				using System.Diagnostics.Process p = System.Diagnostics.Process.GetProcessById((int)pid);
				proc = p.ProcessName;
			}
			catch
			{
			}
			return $"0x{(long)hwnd:X}({cls}/{proc} pid={pid} tid={tid} hung={IsHungAppWindow(hwnd)})";
		}
		catch
		{
			return $"0x{(long)hwnd:X}";
		}
	}

	[DllImport("user32.dll")]
	private static extern nint GetForegroundWindow();

	[DllImport("user32.dll")]
	private static extern uint GetWindowThreadProcessId(nint hWnd, out uint pid);

	[DllImport("user32.dll", CharSet = CharSet.Unicode)]
	private static extern int GetClassName(nint hWnd, StringBuilder className, int maxCount);

	[DllImport("user32.dll")]
	private static extern bool IsHungAppWindow(nint hWnd);
}
