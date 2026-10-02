using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace Win81Layer;

public static class PowerActions
{
	// Best-effort: re-assert Fast Startup OFF so "Shut down" is always a REAL full power-off (S5), not a hybrid
	// shutdown that behaves like sleep/idle on this machine. `shutdown /s` honours Fast Startup (HiberbootEnabled),
	// and there is no per-invocation shutdown.exe flag to skip it — the only fix is this HKLM value = 0. Writing HKLM
	// needs elevation: the logon Scheduled Task runs HighestAvailable (elevated for an admin user), so this succeeds
	// at boot; a manual non-elevated launch just no-ops (caught). Only writes when the value isn't already 0 (no
	// per-boot churn). Reversible: HiberbootEnabled=1 (or `powercfg /h on`) restores Fast Startup.
	public static void EnsureFastStartupDisabled()
	{
		try
		{
			using RegistryKey k = Registry.LocalMachine.OpenSubKey("SYSTEM\\CurrentControlSet\\Control\\Session Manager\\Power", writable: true);
			if (k == null)
			{
				return;
			}
			object cur = k.GetValue("HiberbootEnabled");
			int val = (cur is int i) ? i : -1;
			if (val != 0)
			{
				k.SetValue("HiberbootEnabled", 0, RegistryValueKind.DWord);
				Logger.Log("PowerActions: Fast Startup re-asserted OFF (HiberbootEnabled " + val + "->0) so Shut down is a full power-off.");
			}
		}
		catch (Exception ex)
		{
			Logger.Log("PowerActions: EnsureFastStartupDisabled skipped (" + ex.Message + ")");
		}
	}

	public static void Sleep()
	{
		try
		{
			SetSuspendState(hibernate: false, forceCritical: false, disableWakeEvent: false);
		}
		catch (Exception ex)
		{
			Logger.Log("Sleep failed: " + ex.Message);
		}
	}

	public static void ShutDown()
	{
		SignalIntentionalExit("Shut down");
		Run("/s /t 0");
	}

	public static void Restart()
	{
		SignalIntentionalExit("Restart");
		Run("/r /t 0");
	}

	public static void SignOut()
	{
		SignalIntentionalExit("Sign out");
		Run("/l");
	}

	// Advanced startup: reboot straight into the Windows recovery / boot-options menu (UEFI firmware settings,
	// Startup Settings, etc.). '/r /o' = restart to the advanced boot options; '/t 0' = no delay.
	public static void AdvancedStartup()
	{
		SignalIntentionalExit("Advanced startup");
		Run("/r /o /t 0");
	}

	// A user-initiated power action is an INTENTIONAL exit: log it (so a restart is visible in log.txt instead of
	// looking like a mystery lifecycle event) and drop the clean-exit marker so the watchdog does NOT try to relaunch
	// the shell while the machine is shutting down. Settings/Profile/pins already persist atomically on each change,
	// so nothing is lost. The marker is deleted at the next startup.
	private static void SignalIntentionalExit(string action)
	{
		try
		{
			Logger.Log("PowerActions: '" + action + "' requested — writing clean-exit marker, watchdog stands down.");
			string marker = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Win81Layer", "clean-exit.marker");
			System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(marker));
			System.IO.File.WriteAllText(marker, DateTime.Now.ToString("o"));
		}
		catch
		{
		}
	}

	public static void Lock()
	{
		try
		{
			LockWorkStation();
		}
		catch (Exception ex)
		{
			Logger.Log("Lock failed: " + ex.Message);
		}
	}

	// Switch user: disconnects the console session, which shows the sign-in screen with the other accounts. When the
	// call fails (it can vary by edition) the error is logged and the workstation is locked instead.
	public static void SwitchUser()
	{
		try
		{
			if (WTSDisconnectSession(IntPtr.Zero, -1, bWait: false))
			{
				return;
			}
			Logger.Log("SwitchUser: WTSDisconnectSession failed (error " + Marshal.GetLastWin32Error() + "); locking instead.");
		}
		catch (Exception ex)
		{
			Logger.Log("SwitchUser failed: " + ex.Message + "; locking instead.");
		}
		Lock();
	}

	public static void Hibernate()
	{
		try
		{
			SetSuspendState(hibernate: true, forceCritical: false, disableWakeEvent: false);
		}
		catch (Exception ex)
		{
			Logger.Log("Hibernate failed: " + ex.Message);
		}
	}

	private static readonly Lazy<(bool Ok, bool Sleep, bool Hibernate, string Raw)> _capabilities = new Lazy<(bool Ok, bool Sleep, bool Hibernate, string Raw)>(ReadCapabilities);

	// Which power states the machine reports, read once through GetPwrCapabilities. Sleep is hidden only when the call
	// succeeded and reported none of S1, S2, S3 or Modern Standby; Hibernate needs S4 and a hibernation file. Raw holds
	// the bits that were read, for the logs.
	internal static (bool Ok, bool Sleep, bool Hibernate, string Raw) Capabilities => _capabilities.Value;

	private static (bool Ok, bool Sleep, bool Hibernate, string Raw) ReadCapabilities()
	{
		// SYSTEM_POWER_CAPABILITIES byte offsets: SystemS1..S4 = 3..6, HiberFilePresent = 8, AoAc = 20.
		byte[] buf = new byte[256];
		bool ok;
		try
		{
			ok = GetPwrCapabilities(buf);
		}
		catch
		{
			ok = false;
		}
		bool s1 = ok && buf[3] != 0;
		bool s2 = ok && buf[4] != 0;
		bool s3 = ok && buf[5] != 0;
		bool s4 = ok && buf[6] != 0;
		bool hfp = ok && buf[8] != 0;
		bool aoac = ok && buf[20] != 0;
		static int D(bool b) => b ? 1 : 0;
		string raw = $"ok={D(ok)} S1={D(s1)} S2={D(s2)} S3={D(s3)} S4={D(s4)} HFP={D(hfp)} AoAc={D(aoac)}";
		return (ok, !ok || s1 || s2 || s3 || aoac, ok && s4 && hfp, raw);
	}

	public static void AccountSettings()
	{
		Shell("ms-settings:accounts");
	}

	private static void Shell(string target)
	{
		ShellLaunch.Run(delegate
		{
			try
			{
				Process.Start(new ProcessStartInfo(target)
				{
					UseShellExecute = true
				})?.Dispose();
			}
			catch (Exception ex)
			{
				Logger.Log("Shell '" + target + "' failed: " + ex.Message);
			}
		});
	}

	private static void Run(string args)
	{
		try
		{
			Process.Start(new ProcessStartInfo("shutdown", args)
			{
				UseShellExecute = true,
				CreateNoWindow = true
			});
		}
		catch (Exception ex)
		{
			Logger.Log("shutdown " + args + " failed: " + ex.Message);
		}
	}

	[DllImport("powrprof.dll", SetLastError = true)]
	private static extern bool SetSuspendState(bool hibernate, bool forceCritical, bool disableWakeEvent);

	[DllImport("user32.dll", SetLastError = true)]
	private static extern bool LockWorkStation();

	[DllImport("wtsapi32.dll", SetLastError = true)]
	private static extern bool WTSDisconnectSession(IntPtr hServer, int sessionId, bool bWait);

	[DllImport("powrprof.dll", SetLastError = true)]
	[return: MarshalAs(UnmanagedType.U1)]
	private static extern bool GetPwrCapabilities([Out] byte[] lpspc);
}
