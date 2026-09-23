using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;

namespace Win81Layer;

public static class NativeShell
{
	private static readonly string[] Hosts = new string[3] { "StartMenuExperienceHost", "SearchApp", "SearchHost" };

	private const string StartHost = "StartMenuExperienceHost";

	private const uint PROCESS_SET_QUOTA = 256u;

	private const uint PROCESS_QUERY_INFORMATION = 1024u;

	private const uint PROCESS_SUSPEND_RESUME = 2048u;

	private const int MaxResumePasses = 16;

	private const string SuspensionEventName = "Local\\Win81Layer.NativeShellSuspended.v1";

	private static readonly object SuspensionGate = new object();

	private static EventWaitHandle? _suspensionEvent;

	private static bool _localSuspensionClaim;

	public static void TrimHosts()
	{
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
			EmptyWorkingSet(GetCurrentProcess());
		}
		catch
		{
		}
	}

	public static void SuspendStartHost()
	{
		ForEachStartHost(NtSuspendProcess, "suspended");
	}

	public static void ResumeStartHost()
	{
		ForEachStartHost(NtResumeProcess, "resumed");
	}

	// Broaden supersession to the Search UI hosts too (Win10 SearchApp / Win11 SearchHost). Same reversible
	// NtSuspend/NtResume as Start; Hosts[] already lists all three. Foreground-guarded so a visible host is never frozen.
	public static void SuspendShellHosts()
	{
		if (!TryClaimSuspension())
		{
			Logger.Log("Native shell hosts already suspended by this launcher session; nesting avoided");
			return;
		}
		ForEachHost(Hosts, NtSuspendProcess, "suspended");
		TrimHosts();
		Logger.Log("Native shell hosts suspended and working sets trimmed; restart-loop termination avoided");
	}

	public static void ResumeShellHosts()
	{
		ForEachHost(Hosts, NtResumeProcess, "resumed");
		ReleaseSuspensionClaim();
	}

	// Unconditional resume of every host we ever suspend. Resume drains nested suspend counts left by an older build or
	// an unclean exit, while a running process remains unchanged.
	public static void ResumeAll()
	{
		ForEachHost(Hosts, NtResumeProcess, "resumed(all)");
		ReleaseSuspensionClaim();
	}

	// Keep native hosts DORMANT over time — the real, churn-free way to minimise their resource use. If Windows has
	// respawned (or freshly spun up) a host since we suspended it, suspend the new instance and trim it back to ~0
	// working set. Deliberately NOT a kill: empirically, TERMINATING StartMenuExperienceHost/SearchHost makes Windows
	// proactively relaunch a fresh copy at full memory (a killed host is gone, so the OS restarts it), which is worse;
	// a SUSPENDED host still exists, so the OS never relaunches it and it sits at WS ~0. Only suspends hosts that are
	// not already suspended (no nested suspend counts) and never freezes a foreground host. This is what makes the
	// pre-existing one-shot boot suspension robust against later respawns. Cheap enough for a ~45s idle tick.
	public static void ReassertSuspension()
	{
		uint fgPid = 0u;
		nint fg = GetForegroundWindow();
		if (fg != IntPtr.Zero)
		{
			GetWindowThreadProcessId(fg, out fgPid);
		}
		string[] hosts = Hosts;
		foreach (string name in hosts)
		{
			Process[] processesByName = Process.GetProcessesByName(name);
			foreach (Process p in processesByName)
			{
				try
				{
					if (fgPid != 0u && (uint)p.Id == fgPid)
					{
						continue;   // never freeze a host that currently owns the foreground window
					}
					if (!HasSuspendedThreads(p.Id))
					{
						nint hs = OpenProcess(PROCESS_SUSPEND_RESUME, inherit: false, (uint)p.Id);
						if (hs != IntPtr.Zero)
						{
							NtSuspendProcess(hs);
							CloseHandle(hs);
							Logger.Log($"Native host {name} re-suspended (respawn caught, pid {p.Id})");
						}
					}
					nint ht = OpenProcess(1280u, inherit: false, (uint)p.Id);
					if (ht != IntPtr.Zero)
					{
						EmptyWorkingSet(ht);
						CloseHandle(ht);
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
