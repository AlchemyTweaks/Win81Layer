using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;

namespace Win81Layer;

// Takes the foreground for one of our windows when a plain SetForegroundWindow was refused, without ever attaching
// the UI thread's input queue to another app. While two queues are attached the activation hand-over is synchronous:
// the old foreground window must answer WM_NCACTIVATE / WM_ACTIVATE first, and a busy or hung app then freezes the
// attached thread with no timeout (the shell's UI thread also writes the heartbeat, so that ends in a restart).
//
// Here a short-lived helper thread first checks that the foreground window answers at all, then does the attach and
// the SetForegroundWindow. The caller waits for it for a bounded time (TimeoutMs) and carries on either way. Only the
// helper waits for the app, so its check can be long (ProbeMs): an app that is merely busy (a build, a level load)
// still hands the foreground over once it answers, after the caller has moved on; the caller's action decides at that
// point whether it is still wanted. A window Windows flags as hung, or one silent for the whole probe, is skipped.
// A helper stuck on an app that stopped answering after the check is abandoned (it finishes when that app answers or
// exits); while it is stuck, only requests whose foreground is that same app are skipped.
internal static class ForegroundBroker
{
	private const uint WM_NULL = 0x0000;

	private const uint SMTO_BLOCK = 0x0001;

	private const uint SMTO_ABORTIFHUNG = 0x0002;

	// How long the caller waits for the helper.
	internal static int TimeoutMs = 150;

	// How long the helper waits for the foreground window to answer before it attaches (only the helper waits).
	internal static int ProbeMs = 1500;

	// Helpers alive at once, at most (each is a thread that may be waiting on an app).
	private const int MaxHelpers = 4;

	private static readonly object Gate = new object();

	// Foreground thread -> a helper is alive for it (checking, attached or stuck).
	private static readonly HashSet<uint> Busy = new HashSet<uint>();

	private static int _live;

	// What the last Run saw on the caller's side ("done", "helper still waiting", "skipped: ..."), for the log and
	// --traytest.
	internal static string LastOutcome = "";

	// How the last helper ended, possibly after its caller stopped waiting. Read by --traytest.
	internal static volatile string LastHelperOutcome = "";

	// True when the window's thread answers a WM_NULL within probeMs (and Windows does not already flag it as hung).
	public static bool IsResponsive(nint hwnd, int probeMs)
	{
		if (hwnd == IntPtr.Zero || IsHungAppWindow(hwnd))
		{
			return false;
		}
		return SendMessageTimeout(hwnd, WM_NULL, IntPtr.Zero, IntPtr.Zero, SMTO_BLOCK | SMTO_ABORTIFHUNG, (uint)Math.Max(1, probeMs), out _) != IntPtr.Zero;
	}

	// Runs whileAttached on a helper thread whose input is attached to foregroundThread, after checking that
	// foregroundWindow answers and that stillWanted (when given) still holds. Returns true when the helper finished
	// within timeoutMs; a helper that finishes later still runs whileAttached, so the action must check for itself
	// that it is still current.
	public static bool Run(uint foregroundThread, nint foregroundWindow, Action whileAttached, int timeoutMs, Func<bool> stillWanted = null)
	{
		if (foregroundThread == 0)
		{
			LastOutcome = "no foreground thread";
			return false;
		}
		lock (Gate)
		{
			if (Busy.Contains(foregroundThread))
			{
				LastOutcome = "skipped: an earlier helper is still waiting on this app";
				return false;
			}
			if (_live >= MaxHelpers)
			{
				LastOutcome = "skipped: " + _live + " helpers are still waiting";
				return false;
			}
			Busy.Add(foregroundThread);
			_live++;
		}
		ManualResetEvent done = new ManualResetEvent(initialState: false);
		string outcome = "";
		Thread t = new Thread((ThreadStart)delegate
		{
			bool attached = false;
			uint me = GetCurrentThreadId();
			string result = "";
			try
			{
				if (foregroundWindow != IntPtr.Zero && !IsResponsive(foregroundWindow, ProbeMs))
				{
					result = "foreground window not responding: no attach";
				}
				else if (stillWanted != null && !stillWanted())
				{
					result = "no longer wanted: no attach";
				}
				else
				{
					attached = AttachThreadInput(foregroundThread, me, fAttach: true);
					whileAttached?.Invoke();
					result = attached ? "done" : "done without attach";
				}
			}
			catch (Exception ex)
			{
				result = "failed: " + ex.Message;
			}
			finally
			{
				if (attached)
				{
					AttachThreadInput(foregroundThread, me, fAttach: false);
				}
				outcome = result;
				LastHelperOutcome = result;
				lock (Gate)
				{
					Busy.Remove(foregroundThread);
					_live--;
				}
			}
			try
			{
				done.Set();
			}
			catch
			{
			}
		})
		{
			IsBackground = true,
			Name = "ForegroundHelper"
		};
		try
		{
			t.Start();
		}
		catch (Exception ex)
		{
			lock (Gate)
			{
				Busy.Remove(foregroundThread);
				_live--;
			}
			LastOutcome = "failed to start: " + ex.Message;
			return false;
		}
		bool finished;
		try
		{
			finished = done.WaitOne(Math.Max(1, timeoutMs));
		}
		catch
		{
			finished = false;
		}
		LastOutcome = finished ? outcome : "helper still waiting after " + timeoutMs + " ms";
		if (finished)
		{
			done.Dispose();
		}
		return finished;
	}

	// --traytest: waits until no helper is alive for foregroundThread. False on timeout.
	internal static bool WaitForHelperForTest(uint foregroundThread, int timeoutMs)
	{
		long until = Environment.TickCount64 + timeoutMs;
		while (true)
		{
			lock (Gate)
			{
				if (!Busy.Contains(foregroundThread))
				{
					return true;
				}
			}
			if (Environment.TickCount64 >= until)
			{
				return false;
			}
			Thread.Sleep(10);
		}
	}

	[DllImport("user32.dll")]
	private static extern bool IsHungAppWindow(nint hWnd);

	[DllImport("user32.dll", SetLastError = true)]
	private static extern nint SendMessageTimeout(nint hWnd, uint msg, nint wParam, nint lParam, uint flags, uint timeout, out nint result);

	[DllImport("user32.dll")]
	private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);

	[DllImport("kernel32.dll")]
	private static extern uint GetCurrentThreadId();
}
