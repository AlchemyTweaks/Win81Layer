using System;
using System.Threading;

namespace Win81Layer;

// The one gate in front of every code path that changes a Windows setting (pointer speed, power timeouts, brightness,
// audio devices, radios, sticky keys, sound schemes, shell icons, the logon task...). While the process runs read-only
// diagnostics (every --*test switch and the perf probe set SettingsStore.ReadOnlyDiagnostics first) a real write is
// refused with an exception, so no QA run can ever change the user's machine. The live shell never sets the flag, so
// the check costs one bool read there.
internal static class SettingsGuard
{
	private static int _refusals;

	// How many real writes were refused in this process (QA reports it).
	internal static int Refusals => Volatile.Read(ref _refusals);

	internal static void ThrowIfReadOnly(string what)
	{
		if (SettingsStore.ReadOnlyDiagnostics)
		{
			Interlocked.Increment(ref _refusals);
			throw new InvalidOperationException("read-only diagnostics refused real write: " + what);
		}
	}
}
