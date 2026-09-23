using System;
using System.Diagnostics;
using System.IO;
using Microsoft.Win32;

namespace Win81Layer;

// Finds a desktop app's vendor uninstaller from the Windows Uninstall registry, matched CONFIDENTLY by the app's EXE
// PATH (InstallLocation contains the exe, or DisplayIcon == the exe) — never by fuzzy name, so it can never launch the
// wrong uninstaller. Run() launches the vendor uninstaller (which shows its OWN UI); the caller confirms first. When
// there is no confident match (Store/UWP apps, or an app with no registry entry) the caller falls back to ms-settings.
internal static class AppUninstall
{
	private static readonly string[] Roots =
	{
		"SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Uninstall",
		"SOFTWARE\\WOW6432Node\\Microsoft\\Windows\\CurrentVersion\\Uninstall"
	};

	public static bool TryFind(string exePath, out string displayName, out string uninstallCmd)
	{
		displayName = "";
		uninstallCmd = "";
		try
		{
			if (string.IsNullOrEmpty(exePath) || !Path.IsPathRooted(exePath))
			{
				return false;
			}
			string exeFull;
			try { exeFull = Path.GetFullPath(exePath); } catch { exeFull = exePath; }
			string exeDir;
			try { exeDir = Path.GetDirectoryName(exeFull) ?? ""; } catch { exeDir = ""; }
			if (exeDir.Length == 0)
			{
				return false;
			}

			foreach (RegistryKey hive in new[] { Registry.LocalMachine, Registry.CurrentUser })
			{
				foreach (string root in Roots)
				{
					using RegistryKey rk = hive.OpenSubKey(root);
					if (rk == null)
					{
						continue;
					}
					foreach (string sub in rk.GetSubKeyNames())
					{
						try
						{
							using RegistryKey k = rk.OpenSubKey(sub);
							if (k == null)
							{
								continue;
							}
							if ((k.GetValue("SystemComponent") as int? ?? 0) == 1)
							{
								continue;
							}
							string uCmd = (k.GetValue("UninstallString") as string ?? "").Trim();
							if (string.IsNullOrWhiteSpace(uCmd))
							{
								continue;
							}
							string name = k.GetValue("DisplayName") as string ?? sub;
							string loc = (k.GetValue("InstallLocation") as string ?? "").Trim().Trim('"');
							string icon = (k.GetValue("DisplayIcon") as string ?? "").Trim().Trim('"');

							bool match = false;
							if (loc.Length > 0 && StartsWithDir(exeFull, loc))
							{
								match = true;
							}
							if (!match && icon.Length > 0)
							{
								string iconPath = icon;
								int comma = iconPath.LastIndexOf(',');
								if (comma > 1)
								{
									iconPath = iconPath.Substring(0, comma);
								}
								try
								{
									if (string.Equals(Path.GetFullPath(iconPath), exeFull, StringComparison.OrdinalIgnoreCase))
									{
										match = true;
									}
								}
								catch
								{
								}
							}
							if (match)
							{
								displayName = name;
								uninstallCmd = uCmd;
								return true;
							}
						}
						catch
						{
						}
					}
				}
			}
		}
		catch
		{
		}
		return false;
	}

	private static bool StartsWithDir(string full, string dir)
	{
		try
		{
			string d = Path.GetFullPath(dir).TrimEnd('\\', '/');
			return d.Length > 0 && full.StartsWith(d + "\\", StringComparison.OrdinalIgnoreCase);
		}
		catch
		{
			return false;
		}
	}

	public static void Run(string uninstallCmd)
	{
		try
		{
			string cmd = uninstallCmd.Trim();
			string file;
			string args = "";
			if (cmd.StartsWith("\""))
			{
				int end = cmd.IndexOf('"', 1);
				if (end > 0)
				{
					file = cmd.Substring(1, end - 1);
					args = cmd.Substring(end + 1).Trim();
				}
				else
				{
					file = cmd.Trim('"');
				}
			}
			else
			{
				int sp = cmd.IndexOf(' ');
				if (sp > 0)
				{
					file = cmd.Substring(0, sp);
					args = cmd.Substring(sp + 1).Trim();
				}
				else
				{
					file = cmd;
				}
			}
			Process.Start(new ProcessStartInfo(file, args) { UseShellExecute = true });
		}
		catch (Exception ex)
		{
			Logger.Log("AppUninstall.Run: " + ex.Message);
		}
	}
}
