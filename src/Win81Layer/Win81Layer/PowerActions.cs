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

	public static void AccountSettings()
	{
		Shell("ms-settings:accounts");
	}

	private static void Shell(string target)
	{
		try
		{
			Process.Start(new ProcessStartInfo(target)
			{
				UseShellExecute = true
			});
		}
		catch (Exception ex)
		{
			Logger.Log("Shell '" + target + "' failed: " + ex.Message);
		}
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
}
