using System;
using System.Diagnostics;
using System.Threading.Tasks;

namespace Win81Layer;

public static class ShellRestart
{
	public static void RestartExplorer()
	{
		try
		{
			Process[] processesByName = Process.GetProcessesByName("explorer");
			foreach (Process p in processesByName)
			{
				try
				{
					p.Kill();
				}
				catch
				{
				}
			}
			Task.Run(async delegate
			{
				await Task.Delay(1500);
				if (Process.GetProcessesByName("explorer").Length == 0)
				{
					try
					{
						Process.Start(new ProcessStartInfo("explorer.exe")
						{
							UseShellExecute = true
						});
					}
					catch (Exception ex2)
					{
						Logger.Log("Explorer relaunch failed: " + ex2.Message);
					}
				}
			});
			Logger.Log("Explorer restart requested");
		}
		catch (Exception ex)
		{
			Logger.Log("Explorer restart failed: " + ex.Message);
		}
	}
}
