#nullable enable

using System;
using System.Reflection;
using System.Threading;

namespace Win81Layer;

// WPF's Application answers every WM_QUERYENDSESSION on its hidden parking window by calling Shutdown() unless
// SessionEnding is cancelled (System.Windows.Application.WmQueryEndSession) - and cancelling would BLOCK the logoff.
// A query is only a question: a cancelled shutdown, a logoff another app vetoes, or a Restart Manager CLOSEAPP query
// all leave the session running, yet the shell had already exited (and, on the session-ending path, left the native
// taskbar hidden and the supervisor stood down). This guard puts a hook in front of WPF's on the parking window that
// answers the query TRUE ("may end") itself, so WPF never starts a shutdown on a question. The real end is handled on
// WM_ENDSESSION by SessionWatch. If the private members ever change, TryInstall fails and the caller falls back to
// WPF's behaviour with the stand-down marker withheld (see App.OnSessionEnding); the shell also removes the guard
// again when its SessionWatch is not running, so that fallback applies then too.
internal static class WpfSessionGuard
{
	private const int WM_QUERYENDSESSION = 0x0011;

	private static Delegate? _hook;   // rooted: HwndWrapper only keeps weak references to its hooks

	private static object? _parking;

	private static MethodInfo? _remove;

	internal static nint ParkingHwnd { get; private set; }

	internal static bool Installed => _hook != null;

	internal static int QueriesAnswered;

	internal static bool TryInstall(System.Windows.Application app, out string detail)
	{
		if (_hook != null)
		{
			detail = "already installed";
			return true;
		}
		try
		{
			FieldInfo? field = typeof(System.Windows.Application).GetField("_parkingHwnd", BindingFlags.Instance | BindingFlags.NonPublic);
			if (field == null)
			{
				detail = "System.Windows.Application._parkingHwnd not found";
				return false;
			}
			object? parking = field.GetValue(app);
			if (parking == null)
			{
				detail = "the WPF parking window does not exist yet";
				return false;
			}
			Type t = parking.GetType();
			MethodInfo? add = t.GetMethod("AddHook", BindingFlags.Instance | BindingFlags.Public);
			MethodInfo? remove = t.GetMethod("RemoveHook", BindingFlags.Instance | BindingFlags.Public);
			if (add == null || add.GetParameters().Length != 1)
			{
				detail = t.FullName + ".AddHook not found";
				return false;
			}
			Type hookType = add.GetParameters()[0].ParameterType;
			MethodInfo target = typeof(WpfSessionGuard).GetMethod(nameof(Hook), BindingFlags.Static | BindingFlags.NonPublic)!;
			Delegate hook = Delegate.CreateDelegate(hookType, target);
			add.Invoke(parking, new object[] { hook });   // AddHook inserts at the front: runs before WPF's AppFilterMessage
			_hook = hook;
			_parking = parking;
			_remove = remove;
			object? handle = t.GetProperty("Handle", BindingFlags.Instance | BindingFlags.Public)?.GetValue(parking);
			ParkingHwnd = handle is nint h ? h : IntPtr.Zero;
			detail = "installed on the WPF parking window 0x" + ((long)ParkingHwnd).ToString("X");
			return true;
		}
		catch (Exception ex)
		{
			detail = ex.GetType().Name + ": " + ex.Message;
			return false;
		}
	}

	// Signature of MS.Win32.HwndWrapperHook.
	private static nint Hook(nint hwnd, int msg, nint wParam, nint lParam, ref bool handled)
	{
		if (msg != WM_QUERYENDSESSION)
		{
			return IntPtr.Zero;
		}
		handled = true;
		Interlocked.Increment(ref QueriesAnswered);   // SessionWatch logs the query itself, with its flags
		return 1;   // TRUE: the session may end. Never blocks a logoff.
	}

	// Takes the guard out again, on the dispatcher thread. The shell does this when its SessionWatch is not running:
	// the guard is only safe while something handles WM_ENDSESSION, otherwise WPF's own exit (with the stand-down
	// marker withheld, see App.OnSessionEnding) is the better fallback. The QA suite uses it as its control case.
	internal static bool Remove()
	{
		try
		{
			if (_hook == null || _parking == null || _remove == null)
			{
				return false;
			}
			_remove.Invoke(_parking, new object[] { _hook });
			_hook = null;
			_parking = null;
			_remove = null;
			return true;
		}
		catch
		{
			return false;
		}
	}
}
