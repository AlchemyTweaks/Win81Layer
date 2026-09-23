using System;
using System.IO;
using Microsoft.Win32;

namespace Win81Layer;

internal static class NativeBanner
{
	private const string Key = "Software\\Microsoft\\Windows\\CurrentVersion\\PushNotifications";

	private static bool _applied;

	private static string SnapPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Win81Layer", "toast-enabled.snap");

	internal static bool IsSuppressed()
	{
		try
		{
			using RegistryKey k = Registry.CurrentUser.OpenSubKey(Key);
			return k?.GetValue("ToastEnabled") is int value && value == 0 && File.Exists(SnapPath);
		}
		catch
		{
			return false;
		}
	}

	internal static void Suppress()
	{
		try
		{
			using RegistryKey k = Registry.CurrentUser.CreateSubKey("Software\\Microsoft\\Windows\\CurrentVersion\\PushNotifications");
			if (k != null)
			{
				if (!File.Exists(SnapPath))
				{
					int? cur = k.GetValue("ToastEnabled") as int?;
					Directory.CreateDirectory(Path.GetDirectoryName(SnapPath));
					File.WriteAllText(SnapPath, cur?.ToString() ?? "unset");
				}
				k.SetValue("ToastEnabled", 0, RegistryValueKind.DWord);
				_applied = true;
				Logger.Log("[notif] native banners suppressed (ToastEnabled=0)");
			}
		}
		catch (Exception ex)
		{
			Logger.Log("[notif] banner suppress failed: " + ex.Message);
		}
	}

	internal static void Restore()
	{
		if (!_applied && !File.Exists(SnapPath))
		{
			return;
		}
		_applied = false;
		try
		{
			using RegistryKey k = Registry.CurrentUser.CreateSubKey("Software\\Microsoft\\Windows\\CurrentVersion\\PushNotifications");
			if (k != null)
			{
				string snap = (File.Exists(SnapPath) ? File.ReadAllText(SnapPath).Trim() : "1");
				int v;
				if (snap == "unset")
				{
					k.DeleteValue("ToastEnabled", throwOnMissingValue: false);
				}
				else if (int.TryParse(snap, out v))
				{
					k.SetValue("ToastEnabled", v, RegistryValueKind.DWord);
				}
				else
				{
					k.SetValue("ToastEnabled", 1, RegistryValueKind.DWord);
				}
				try
				{
					File.Delete(SnapPath);
				}
				catch
				{
				}
				Logger.Log("[notif] native banners restored");
			}
		}
		catch (Exception ex)
		{
			Logger.Log("[notif] banner restore failed: " + ex.Message);
		}
	}
}
