using System;
using Microsoft.Win32;

namespace Win81Layer;

internal static class NotificationSettings
{
	private const string PushKey = "Software\\Microsoft\\Windows\\CurrentVersion\\PushNotifications";

	public static bool ToastEnabled => ReadBool("Software\\Microsoft\\Windows\\CurrentVersion\\PushNotifications", "ToastEnabled", fallback: true);

	public static bool LockScreenToastEnabled => ReadBool("Software\\Microsoft\\Windows\\CurrentVersion\\PushNotifications", "LockScreenToastEnabled", fallback: true);

	public static void SetToastEnabled(bool on)
	{
		WriteBool("Software\\Microsoft\\Windows\\CurrentVersion\\PushNotifications", "ToastEnabled", on);
	}

	public static void SetLockScreenToastEnabled(bool on)
	{
		WriteBool("Software\\Microsoft\\Windows\\CurrentVersion\\PushNotifications", "LockScreenToastEnabled", on);
	}

	private static bool ReadBool(string subKey, string name, bool fallback)
	{
		try
		{
			using RegistryKey k = Registry.CurrentUser.OpenSubKey(subKey);
			return (k?.GetValue(name) is int v) ? (v != 0) : fallback;
		}
		catch
		{
			return fallback;
		}
	}

	private static void WriteBool(string subKey, string name, bool on)
	{
		try
		{
			using RegistryKey k = Registry.CurrentUser.CreateSubKey(subKey);
			k?.SetValue(name, on ? 1 : 0, RegistryValueKind.DWord);
		}
		catch (Exception ex)
		{
			Logger.Log("Notification setting '" + name + "' write failed: " + ex.Message);
		}
	}
}
