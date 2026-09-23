using System;
using Microsoft.Win32;

namespace Win81Layer;

public static class Autostart
{
	private const string RunKey = "Software\\Microsoft\\Windows\\CurrentVersion\\Run";

	private const string ValueName = "Win81Layer";

	public static bool IsEnabled()
	{
		try
		{
			using RegistryKey key = Registry.CurrentUser.OpenSubKey("Software\\Microsoft\\Windows\\CurrentVersion\\Run");
			return key?.GetValue("Win81Layer") != null;
		}
		catch
		{
			return false;
		}
	}

	public static void SetEnabled(bool enabled)
	{
		try
		{
			using RegistryKey key = Registry.CurrentUser.CreateSubKey("Software\\Microsoft\\Windows\\CurrentVersion\\Run");
			if (enabled)
			{
				key.SetValue("Win81Layer", "\"" + Environment.ProcessPath + "\" --autostart");
			}
			else
			{
				key.DeleteValue("Win81Layer", throwOnMissingValue: false);
			}
			Logger.Log("Autostart " + (enabled ? "enabled" : "removed"));
		}
		catch (Exception ex)
		{
			Logger.Log("Autostart change failed: " + ex.Message);
		}
	}
}
