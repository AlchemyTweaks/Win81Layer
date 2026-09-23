using System;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace Win81Layer;

// Small, reversible Explorer toggles exposed as search commands. Both flip an HKCU Advanced flag and broadcast a
// shell change so open Explorer windows refresh. Non-destructive (each is its own undo). Best-effort + guarded.
internal static class ShellCommands
{
	private const string AdvKey = "Software\\Microsoft\\Windows\\CurrentVersion\\Explorer\\Advanced";

	private const int SHCNE_ASSOCCHANGED = 0x08000000;

	[DllImport("shell32.dll")]
	private static extern void SHChangeNotify(int eventId, uint flags, nint item1, nint item2);

	// Hidden = 1 shows hidden files, 2 hides them.
	public static void ToggleHiddenFiles()
	{
		Toggle("Hidden", showValue: 1, hideValue: 2, defaultHidden: true, "hidden files");
	}

	// HideFileExt = 0 shows extensions, 1 hides them.
	public static void ToggleFileExtensions()
	{
		Toggle("HideFileExt", showValue: 0, hideValue: 1, defaultHidden: true, "file extensions");
	}

	private static void Toggle(string valueName, int showValue, int hideValue, bool defaultHidden, string label)
	{
		try
		{
			using RegistryKey key = Registry.CurrentUser.CreateSubKey(AdvKey);
			if (key == null)
			{
				return;
			}
			int cur = Convert.ToInt32(key.GetValue(valueName, defaultHidden ? hideValue : showValue));
			int next = (cur == showValue) ? hideValue : showValue;
			key.SetValue(valueName, next, RegistryValueKind.DWord);
			try { SHChangeNotify(SHCNE_ASSOCCHANGED, 0u, IntPtr.Zero, IntPtr.Zero); } catch { }
			Logger.Log($"ShellCommands: {label} now {(next == showValue ? "shown" : "hidden")}");
		}
		catch (Exception ex)
		{
			Logger.Log("ShellCommands toggle failed (" + label + "): " + ex.Message);
		}
	}
}
