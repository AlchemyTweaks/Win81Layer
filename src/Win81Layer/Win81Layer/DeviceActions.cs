using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

namespace Win81Layer;

// Device Center actions built on documented, reliable mechanisms (DisplaySwitch.exe for projection; DriveInfo + Shell verbs
// for removable media). Radio (Bluetooth) toggling reuses RadioQuick; audio/settings reuse ms-settings URIs. No native hacks.
internal static class DeviceActions
{
	// mode = "/clone" | "/extend" | "/external" | "/internal" (Win+P equivalents).
	public static void Display(string mode)
	{
		ShellLaunch.Run(delegate
		{
			try { Process.Start(new ProcessStartInfo("DisplaySwitch.exe", mode) { UseShellExecute = true }); }
			catch (Exception ex) { Logger.Log("device display: " + ex.Message); }
		});
	}

	public static List<string> RemovableDrives()
	{
		List<string> list = new List<string>();
		try
		{
			foreach (DriveInfo d in DriveInfo.GetDrives())
			{
				try { if (d.DriveType == DriveType.Removable && d.IsReady) { list.Add(d.Name); } }
				catch { }
			}
		}
		catch { }
		return list;
	}

	public static void Open(string drive)
	{
		ShellLaunch.Run(delegate
		{
			try { Process.Start(new ProcessStartInfo("explorer.exe", "\"" + drive + "\"") { UseShellExecute = true }); }
			catch (Exception ex) { Logger.Log("device open: " + ex.Message); }
		});
	}

	// Safely-remove via the shell "Eject" verb. Best-effort: if the verb name differs by locale it is a silent no-op (logged),
	// never a crash — the robust-but-heavy SetupAPI CM_Request_Device_Eject path is intentionally not used here.
	public static void Eject(string drive)
	{
		ShellLaunch.Run(delegate
		{
			try
			{
				Type t = Type.GetTypeFromProgID("Shell.Application");
				if (t == null)
				{
					return;
				}
				dynamic shell = Activator.CreateInstance(t);
				if (shell == null)
				{
					return;
				}
				try
				{
					dynamic ns = shell.NameSpace(17);   // ssfDRIVES
					dynamic item = ns?.ParseName(drive.TrimEnd('\\'));
					if (item != null)
					{
						item.InvokeVerb("Eject");
					}
				}
				finally
				{
					try { Marshal.FinalReleaseComObject(shell); } catch { }
				}
			}
			catch (Exception ex) { Logger.Log("device eject: " + ex.Message); }
		});
	}
}
