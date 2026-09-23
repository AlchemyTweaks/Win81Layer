using System;
using System.Threading;

namespace Win81Layer;

// A maintenance CLI is a second process and cannot reach the live App's private taskbar/hooks. The named auto-reset
// event asks the managed shell to re-read the already-committed settings and refresh itself on its next 1s tick.
internal static class ShellProfileChangeSignal
{
	private const string EventName = "Local\\Win81Layer.ShellProfileChanged.v1";
	private static readonly object Gate = new object();
	private static EventWaitHandle? _event;

	public static void Set()
	{
		try
		{
			Handle().Set();
		}
		catch (Exception ex)
		{
			Logger.Log("Shell profile refresh signal failed: " + ex.Message);
		}
	}

	public static bool TryConsume()
	{
		try
		{
			return Handle().WaitOne(0);
		}
		catch
		{
			return false;
		}
	}

	private static EventWaitHandle Handle()
	{
		lock (Gate)
		{
			_event ??= new EventWaitHandle(false, EventResetMode.AutoReset, EventName);
			return _event;
		}
	}
}
