using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Threading;

namespace Win81Layer;

internal static class ShellSupervisor
{
	private const int RebootFailStreak = 3;

	private static readonly TimeSpan RestartCooldown = TimeSpan.FromSeconds(6L);

	private static readonly TimeSpan RebootLoopGuard = TimeSpan.FromMinutes(10L);

	private static int _explorerFailStreak;

	private static DateTime _lastRestartUtc = DateTime.MinValue;

	private static string StampPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Win81Layer", "last-force-reboot.txt");

	internal static void Tick(bool taskbarActive)
	{
		if (!taskbarActive)
		{
			_explorerFailStreak = 0;
			return;
		}
		try
		{
			if (IsExplorerRunning())
			{
				_explorerFailStreak = 0;
				AppBar.ReassertHidden();
			}
			else
			{
				if (DateTime.UtcNow - _lastRestartUtc < RestartCooldown)
				{
					return;
				}
				AppSettings s = SettingsStore.Load();
				if (!s.AutoRecoverShell)
				{
					Logger.Log("[supervisor] explorer.exe is down but AutoRecoverShell is off — leaving it.");
					return;
				}
				_explorerFailStreak++;
				Logger.Log($"[supervisor] native shell down (explorer.exe missing) — streak {_explorerFailStreak}");
				if (_explorerFailStreak >= 3)
				{
					_explorerFailStreak = 0;
					_lastRestartUtc = DateTime.UtcNow;
					if (s.ForceRebootOnShellFailure)
					{
						ForceRebootGuarded("Explorer did not recover after repeated restarts");
					}
					else
					{
						Logger.Log("[supervisor] explorer unrecoverable, but ForceRebootOnShellFailure is off — not rebooting.");
					}
				}
				else
				{
					ShellRestart.RestartExplorer();
					_lastRestartUtc = DateTime.UtcNow;
				}
			}
		}
		catch (Exception ex)
		{
			Logger.Log("[supervisor] tick failed: " + ex.Message);
		}
	}

	internal static void RecoverAfterOurExit()
	{
		try
		{
			NativeShell.ResumeShellHosts();
			AppBar.ShowNativeTaskbar();
			AppSettings s = SettingsStore.Load();
			if (!s.AutoRecoverShell)
			{
				return;
			}
			for (int i = 0; i < 3; i++)
			{
				if (IsExplorerRunning())
				{
					break;
				}
				Logger.Log($"[watchdog] explorer.exe down after our exit — restart attempt {i + 1}");
				try
				{
					Process.Start(new ProcessStartInfo("explorer.exe")
					{
						UseShellExecute = true
					});
				}
				catch (Exception ex)
				{
					Logger.Log("[watchdog] explorer relaunch failed: " + ex.Message);
				}
				Thread.Sleep(5000);
			}
			if (!IsExplorerRunning() && s.ForceRebootOnShellFailure)
			{
				ForceRebootGuarded("Explorer did not recover after our exit");
			}
		}
		catch (Exception ex2)
		{
			Logger.Log("[watchdog] recover failed: " + ex2.Message);
		}
	}

	private static bool IsExplorerRunning()
	{
		return Process.GetProcessesByName("explorer").Length != 0;
	}

	private static void ForceRebootGuarded(string reason)
	{
		try
		{
			if (File.Exists(StampPath) && DateTime.TryParse(File.ReadAllText(StampPath).Trim(), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var last) && DateTime.UtcNow - last < RebootLoopGuard)
			{
				Logger.Log($"[supervisor] force-reboot SUPPRESSED (last reboot {(DateTime.UtcNow - last).TotalMinutes:F1} min ago, anti-loop): {reason}");
			}
			else
			{
				Directory.CreateDirectory(Path.GetDirectoryName(StampPath));
				File.WriteAllText(StampPath, DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture));
				Logger.Log("[supervisor] FORCE REBOOT scheduled (30s, cancel with 'shutdown /a'): " + reason);
				Process.Start(new ProcessStartInfo("shutdown.exe", "/r /t 30 /c \"Win8.1 Layer: the Windows shell could not be recovered — restarting in 30s. Run 'shutdown /a' to cancel.\"")
				{
					UseShellExecute = false,
					CreateNoWindow = true
				});
			}
		}
		catch (Exception ex)
		{
			Logger.Log("[supervisor] force-reboot failed: " + ex.Message);
		}
	}
}
