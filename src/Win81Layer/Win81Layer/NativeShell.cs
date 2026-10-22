using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;

namespace Win81Layer;

public static class NativeShell
{
	private static readonly string[] Hosts = new string[3] { "StartMenuExperienceHost", "SearchApp", "SearchHost" };

	internal static string[] HostNames => Hosts;

	private const string StartHost = "StartMenuExperienceHost";

	private const uint PROCESS_SET_QUOTA = 256u;

	private const uint PROCESS_QUERY_INFORMATION = 1024u;

	private const uint PROCESS_SUSPEND_RESUME = 2048u;

	private const int MaxResumePasses = 16;

	private const string SuspensionEventName = "Local\\Win81Layer.NativeShellSuspended.v1";

	internal static readonly object SuspensionGate = new object();

	// Blocked (under SuspensionGate) by every resume path and unblocked by a successful claim: while blocked, the warden's
	// passes refuse to suspend anything, so a resume on exit can never be followed by a late re-suspend.
	internal static readonly SuspensionGuard Guard = new SuspensionGuard(SuspensionGate, SuspensionHeldLocked);

	private static EventWaitHandle? _suspensionEvent;

	private static bool _localSuspensionClaim;

	public static void TrimHosts()
	{
		PerfProbe.Refuse("NativeShell.TrimHosts");
		string[] hosts = Hosts;
		foreach (string name in hosts)
		{
			Process[] processesByName = Process.GetProcessesByName(name);
			foreach (Process p in processesByName)
			{
				try
				{
					nint h = OpenProcess(1280u, inherit: false, (uint)p.Id);
					if (h != IntPtr.Zero)
					{
						EmptyWorkingSet(h);
						CloseHandle(h);
					}
				}
				catch
				{
				}
				finally
				{
					p.Dispose();
				}
			}
		}
	}

	// Trim the launcher's OWN working set. Pages fault back in on next use, but idle RAM — decoded Start tile art,
	// flyout surfaces, GC segments freed after heavy UI — is returned to the OS, cutting the Task-Manager footprint.
	// Cheap; only call at natural idle points (Start/flyout close, slow idle tick), never per-frame.
	public static void TrimSelf()
	{
		try
		{
			// HeapOptimizeResources (Win 8.1+): decommit free pages in every process heap, lowering Private Bytes.
			HeapOptimizeResourcesInformation info = new HeapOptimizeResourcesInformation
			{
				Version = 1u,
				Flags = 0u
			};
			HeapSetInformation(IntPtr.Zero, HeapOptimizeResources, ref info, (nuint)Marshal.SizeOf<HeapOptimizeResourcesInformation>());
		}
		catch
		{
		}
		try
		{
			EmptyWorkingSet(GetCurrentProcess());
		}
		catch
		{
		}
	}

	public static void SuspendStartHost()
	{
		PerfProbe.Refuse("NativeShell.SuspendStartHost");
		ForEachStartHost(NtSuspendProcess, "suspended");
	}

	public static void ResumeStartHost()
	{
		PerfProbe.Refuse("NativeShell.ResumeStartHost");
		ForEachStartHost(NtResumeProcess, "resumed");
	}

	// Broaden supersession to the Search UI hosts too (Win10 SearchApp / Win11 SearchHost). Same reversible
	// NtSuspend/NtResume as Start; Hosts[] already lists all three. Foreground-guarded so a visible host is never frozen.
	public static void SuspendShellHosts()
	{
		PerfProbe.Refuse("NativeShell.SuspendShellHosts");
		if (!TryClaimSuspension())
		{
			Logger.Log("Native shell hosts already suspended by this launcher session; nesting avoided");
			return;
		}
		ForEachHost(Hosts, NtSuspendProcess, "suspended");
		TrimHosts();
		Logger.Log("Native shell hosts suspended and working sets trimmed; restart-loop termination avoided");
		NativeHostWarden.RequestPass("suspended");   // registers the exit watches that catch a respawn
	}

	// Taking the lock waits for a warden pass in flight; once blocked no later pass suspends anything.
	private static void BlockWardenPasses()
	{
		Guard.Block();
		try { NativeHostWarden.CancelWatches(); } catch { }
	}

	public static void ResumeShellHosts()
	{
		PerfProbe.Refuse("NativeShell.ResumeShellHosts");
		BlockWardenPasses();
		ForEachHost(Hosts, NtResumeProcess, "resumed");
		ReleaseSuspensionClaim();
	}

	// Unconditional resume of every host we ever suspend. Resume drains nested suspend counts left by an older build or
	// an unclean exit, while a running process remains unchanged.
	public static void ResumeAll()
	{
		PerfProbe.Refuse("NativeShell.ResumeAll");
		BlockWardenPasses();
		ForEachHost(Hosts, NtResumeProcess, "resumed(all)");
		ReleaseSuspensionClaim();
	}

	// Keep native hosts DORMANT over time — the real, churn-free way to minimise their resource use. If Windows has
	// respawned (or freshly spun up) a host since we suspended it, suspend the new instance and trim it back to ~0
	// working set. Deliberately NOT a kill: empirically, TERMINATING StartMenuExperienceHost/SearchHost makes Windows
	// proactively relaunch a fresh copy at full memory (a killed host is gone, so the OS restarts it), which is worse;
	// a SUSPENDED host still exists, so the OS never relaunches it and it sits at WS ~0. Only suspends hosts that are
	// not already suspended (no nested suspend counts) and never freezes a foreground host. One synchronous warden pass;
	// the shell itself schedules passes on a pool thread (NativeHostWarden).
	public static void ReassertSuspension()
	{
		PerfProbe.Refuse("NativeShell.ReassertSuspension");
		NativeHostWarden.RunPass("reassert");
	}

	// One warden pass (NativeHostWarden.RunPass holds its own pass lock). Under SuspensionGate: refuses while a resume is
	// pending or this session does not hold the suspension; reads one snapshot; suspends and trims each host that is
	// running and not in the foreground. Returns the pids to watch for exit (every host present), or null.
	internal static System.Collections.Generic.List<uint>? WardenPassLocked(SystemProcessSnapshot snap, SystemProcessSnapshot.HostInfo[] found, string reason)
	{
		if (PerfProbe.Active || !SettingsStore.FastSnapshot.SuspendNativeStart)
		{
			return null;
		}
		return Guard.Run(delegate
		{
			uint fgPid = 0u;
			nint fg = GetForegroundWindow();
			if (fg != IntPtr.Zero)
			{
				GetWindowThreadProcessId(fg, out fgPid);
			}
			if (!snap.Refresh())
			{
				return null!;
			}
			int n = snap.FindProcesses(Hosts, found);
			System.Collections.Generic.List<uint> watch = new System.Collections.Generic.List<uint>(n);
			for (int i = 0; i < n; i++)
			{
				SystemProcessSnapshot.HostInfo h = found[i];
				if (h.SuspendedThreads > 0)
				{
					watch.Add(h.Pid);   // already dormant: no second suspend (no nested counts) and no re-trim
					continue;
				}
				if (fgPid != 0u && h.Pid == fgPid)
				{
					continue;   // never freeze a host that currently owns the foreground window
				}
				nint hs = OpenProcess(PROCESS_SUSPEND_RESUME, inherit: false, h.Pid);
				if (hs == IntPtr.Zero)
				{
					continue;
				}
				NtSuspendProcess(hs);
				CloseHandle(hs);
				nint ht = OpenProcess(1280u, inherit: false, h.Pid);
				if (ht != IntPtr.Zero)
				{
					EmptyWorkingSet(ht);
					CloseHandle(ht);
				}
				Logger.Log($"Native host {Hosts[h.NameIndex]} re-suspended (respawn caught, pid {h.Pid}, {reason})");
				watch.Add(h.Pid);
			}
			return watch;
		});
	}

	private static bool SuspensionHeldLocked()
	{
		if (_localSuspensionClaim)
		{
			return true;
		}
		try
		{
			return SuspensionMarker().WaitOne(0);
		}
		catch
		{
			return false;
		}
	}

	private static void ForEachStartHost(Func<nint, uint> action, string verb)
	{
		ForEachHost(new string[1] { "StartMenuExperienceHost" }, action, verb);
	}

	private static void ForEachHost(string[] names, Func<nint, uint> action, string verb)
	{
		// Only the suspend direction needs the foreground guard (resume is always safe).
		uint fgPid = 0u;
		if (action == NtSuspendProcess)
		{
			nint fg = GetForegroundWindow();
			if (fg != IntPtr.Zero)
			{
				GetWindowThreadProcessId(fg, out fgPid);
			}
		}
		foreach (string name in names)
		{
			Process[] processesByName = Process.GetProcessesByName(name);
			foreach (Process p in processesByName)
			{
				try
				{
					if (action == NtSuspendProcess && fgPid != 0u && (uint)p.Id == fgPid)
					{
						continue;   // never freeze a host that currently owns the foreground window
					}
					nint h = OpenProcess(2048u, inherit: false, (uint)p.Id);
					if (h != IntPtr.Zero)
					{
						uint status = 0u;
						int passes = 1;
						if (action == NtResumeProcess)
						{
							passes = 0;
							do
							{
								status = NtResumeProcess(h);
								passes++;
								if (!HasSuspendedThreads(p.Id))
								{
									break;
								}
								Thread.Sleep(5);
							}
							while (passes < MaxResumePasses);
						}
						else
						{
							status = NtSuspendProcess(h);
						}
						CloseHandle(h);
						bool suspendedRemaining = HasSuspendedThreads(p.Id);
						Logger.Log($"Native host {name} {verb} (pid {p.Id}, passes={passes}, status=0x{status:X8}, suspendedRemaining={suspendedRemaining})");
					}
				}
				catch (Exception ex)
				{
					Logger.Log("Native host " + name + " " + verb + " failed: " + ex.Message);
				}
				finally
				{
					p.Dispose();
				}
			}
		}
	}

	private static bool TryClaimSuspension()
	{
		lock (SuspensionGate)
		{
			try
			{
				EventWaitHandle marker = SuspensionMarker();
				if (marker.WaitOne(0))
				{
					return false;
				}
				marker.Set();
				Guard.Unblock();
				return true;
			}
			catch (Exception ex)
			{
				Logger.Log("Native shell suspension marker failed; using process-local claim: " + ex.Message);
				if (_localSuspensionClaim)
				{
					return false;
				}
				_localSuspensionClaim = true;
				Guard.Unblock();
				return true;
			}
		}
	}

	private static void ReleaseSuspensionClaim()
	{
		lock (SuspensionGate)
		{
			_localSuspensionClaim = false;
			try { SuspensionMarker().Reset(); } catch { }
		}
	}

	private static EventWaitHandle SuspensionMarker()
	{
		return _suspensionEvent ??= new EventWaitHandle(false, EventResetMode.ManualReset, SuspensionEventName);
	}

	private static bool HasSuspendedThreads(int processId)
	{
		return CountSuspendedThreads(processId).suspended > 0;
	}

	private static (int total, int suspended) CountSuspendedThreads(int processId)
	{
		int total = 0;
		int suspended = 0;
		try
		{
			using Process process = Process.GetProcessById(processId);
			foreach (ProcessThread thread in process.Threads)
			{
				try
				{
					total++;
					if (thread.ThreadState == System.Diagnostics.ThreadState.Wait
						&& thread.WaitReason == ThreadWaitReason.Suspended)
					{
						suspended++;
					}
				}
				catch
				{
				}
			}
		}
		catch
		{
		}
		return (total, suspended);
	}

	private const int HeapOptimizeResources = 3;

	[StructLayout(LayoutKind.Sequential)]
	private struct HeapOptimizeResourcesInformation
	{
		public uint Version;

		public uint Flags;
	}

	[DllImport("kernel32.dll")]
	private static extern bool HeapSetInformation(nint heapHandle, int heapInformationClass, ref HeapOptimizeResourcesInformation heapInformation, nuint heapInformationLength);

	[DllImport("psapi.dll")]
	private static extern bool EmptyWorkingSet(nint hProcess);

	[DllImport("kernel32.dll")]
	private static extern nint GetCurrentProcess();

	[DllImport("kernel32.dll")]
	private static extern nint OpenProcess(uint access, bool inherit, uint pid);

	[DllImport("kernel32.dll")]
	private static extern bool CloseHandle(nint h);

	[DllImport("ntdll.dll")]
	private static extern uint NtSuspendProcess(nint h);

	[DllImport("ntdll.dll")]
	private static extern uint NtResumeProcess(nint h);

	[DllImport("user32.dll")]
	private static extern nint GetForegroundWindow();

	[DllImport("user32.dll")]
	private static extern uint GetWindowThreadProcessId(nint hWnd, out uint pid);
}
