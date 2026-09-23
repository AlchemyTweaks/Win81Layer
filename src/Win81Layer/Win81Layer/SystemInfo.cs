using System;
using System.Globalization;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace Win81Layer;

internal static class SystemInfo
{
	public static string PcName => Environment.MachineName;

	public static string Edition
	{
		get
		{
			try
			{
				using RegistryKey k = Registry.LocalMachine.OpenSubKey("SOFTWARE\\Microsoft\\Windows NT\\CurrentVersion");
				string s = (k?.GetValue("ProductName") as string)?.Trim();
				return (s != null && s.Length > 0) ? s : "Windows";
			}
			catch
			{
				return "Windows";
			}
		}
	}

	public static string Processor
	{
		get
		{
			try
			{
				using RegistryKey k = Registry.LocalMachine.OpenSubKey("HARDWARE\\DESCRIPTION\\System\\CentralProcessor\\0");
				return (k?.GetValue("ProcessorNameString") as string)?.Trim() ?? "";
			}
			catch
			{
				return "";
			}
		}
	}

	public static string InstalledRam
	{
		get
		{
			try
			{
				if (GetPhysicallyInstalledSystemMemory(out var kb) && kb != 0)
				{
					return $"{(double)kb / 1024.0 / 1024.0:0.#} GB";
				}
			}
			catch
			{
			}
			return "";
		}
	}

	public static string SystemType
	{
		get
		{
			bool os64 = Environment.Is64BitOperatingSystem;
			bool flag;
			switch (Environment.GetEnvironmentVariable("PROCESSOR_ARCHITECTURE") ?? (os64 ? "AMD64" : "x86"))
			{
			case "AMD64":
			case "ARM64":
			case "IA64":
				flag = true;
				break;
			default:
				flag = false;
				break;
			}
			string cpuBits = (flag ? "x64-based processor" : "x86-based processor");
			return (os64 ? "64-bit" : "32-bit") + " operating system, " + cpuBits;
		}
	}

	public static string PenAndTouch
	{
		get
		{
			try
			{
				int d = GetSystemMetrics(94);
				int touches = GetSystemMetrics(95);
				if ((d & 0x80) != 0 && touches > 0)
				{
					return $"Touch support with {touches} touch points";
				}
				if ((d & 2) != 0 || (d & 1) != 0)
				{
					return "Pen support";
				}
				return "No pen or touch input is available for this display";
			}
			catch
			{
				return "No pen or touch input is available for this display";
			}
		}
	}

	[DllImport("kernel32.dll")]
	private static extern bool GetPhysicallyInstalledSystemMemory(out ulong totalKilobytes);

	[DllImport("user32.dll")]
	private static extern int GetSystemMetrics(int index);

	public static string? WindowsUpdateLastChecked()
	{
		try
		{
			using RegistryKey k = Registry.LocalMachine.OpenSubKey("SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\WindowsUpdate\\Auto Update\\Results\\Detect");
			if (k?.GetValue("LastSuccessTime") is string s && DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var dt))
			{
				return "Last checked: " + dt.ToLocalTime().ToString("MMM d, yyyy 'at' h:mm tt");
			}
		}
		catch
		{
		}
		return null;
	}
}
