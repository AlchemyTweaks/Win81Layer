using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Microsoft.Win32;

namespace Win81Layer;

public static class SoundScheme
{
	private const string AppsKey = "AppEvents\\Schemes\\Apps";

	private static string SoundsDir
	{
		get
		{
			if (Directory.Exists("C:\\Windows\\Sounds Windows 8.1"))
			{
				return "C:\\Windows\\Sounds Windows 8.1";
			}
			return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Win81Layer", "assets", "win81");
		}
	}

	private static string SnapshotPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Win81Layer", "sound-snapshot.json");

	public static bool IsApplied => File.Exists(SnapshotPath);

	private static string ActiveScheme()
	{
		try
		{
			using RegistryKey k = Registry.CurrentUser.OpenSubKey("AppEvents\\Schemes");
			if (k?.GetValue("") is string s && !string.IsNullOrWhiteSpace(s))
			{
				return s;
			}
		}
		catch
		{
		}
		return ".Default";
	}

	private static Dictionary<string, string> Win81Wavs()
	{
		Dictionary<string, string> map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
		if (!Directory.Exists(SoundsDir))
		{
			return map;
		}
		foreach (string wav in Directory.EnumerateFiles(SoundsDir, "*.wav", SearchOption.AllDirectories))
		{
			map[Path.GetFileName(wav)] = wav;
		}
		return map;
	}

	private static Dictionary<string, string> LoadSnapshot()
	{
		try
		{
			if (File.Exists(SnapshotPath))
			{
				return JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(SnapshotPath)) ?? new Dictionary<string, string>();
			}
		}
		catch
		{
		}
		return new Dictionary<string, string>();
	}

	public static void Apply()
	{
		try
		{
			Dictionary<string, string> wavs = Win81Wavs();
			if (wavs.Count == 0)
			{
				Logger.Log("Win8.1 sounds: no wav assets found");
				return;
			}
			string scheme = ActiveScheme();
			Dictionary<string, string> snapshot = LoadSnapshot();
			using RegistryKey apps = Registry.CurrentUser.OpenSubKey("AppEvents\\Schemes\\Apps", writable: true);
			if (apps == null)
			{
				return;
			}
			int swapped = 0;
			string[] subKeyNames = apps.GetSubKeyNames();
			foreach (string appName in subKeyNames)
			{
				using RegistryKey appKey = apps.OpenSubKey(appName, writable: true);
				if (appKey == null)
				{
					continue;
				}
				string[] subKeyNames2 = appKey.GetSubKeyNames();
				foreach (string evt in subKeyNames2)
				{
					using RegistryKey cur = appKey.OpenSubKey(evt + "\\.Current", writable: true);
					if (!(cur?.GetValue("") is string val) || string.IsNullOrWhiteSpace(val))
					{
						continue;
					}
					string fn = Path.GetFileName(Environment.ExpandEnvironmentVariables(val));
					if (wavs.TryGetValue(fn, out var win81))
					{
						string key = appName + "|" + evt;
						bool alreadyWin81 = string.Equals(val, win81, StringComparison.OrdinalIgnoreCase);
						if (!snapshot.ContainsKey(key) && !alreadyWin81)
						{
							snapshot[key] = val;
						}
						cur.SetValue("", win81);
						using RegistryKey sch = appKey.CreateSubKey(evt + "\\" + scheme);
						sch?.SetValue("", win81);
						swapped++;
					}
				}
			}
			Directory.CreateDirectory(Path.GetDirectoryName(SnapshotPath));
			File.WriteAllText(SnapshotPath, JsonSerializer.Serialize(snapshot));
			Logger.Log($"Win8.1 sounds applied (scheme={scheme}, {swapped} events written, {snapshot.Count} snapshotted)");
		}
		catch (Exception ex)
		{
			Logger.Log("Win8.1 sounds apply failed: " + ex.Message);
		}
	}

	public static void Reassert()
	{
		if (IsApplied)
		{
			Apply();
		}
	}

	public static void Revert()
	{
		try
		{
			if (!File.Exists(SnapshotPath))
			{
				return;
			}
			string scheme = ActiveScheme();
			Dictionary<string, string> snapshot = LoadSnapshot();
			foreach (KeyValuePair<string, string> item in snapshot)
			{
				item.Deconstruct(out var key, out var value);
				string key2 = key;
				string orig = value;
				int bar = key2.IndexOf('|');
				if (bar < 0)
				{
					continue;
				}
				string appName = key2.Substring(0, bar);
				value = key2;
				int num = bar + 1;
				string evt = value.Substring(num, value.Length - num);
				using RegistryKey cur = Registry.CurrentUser.OpenSubKey($"{"AppEvents\\Schemes\\Apps"}\\{appName}\\{evt}\\.Current", writable: true);
				cur?.SetValue("", orig);
				using RegistryKey sch = Registry.CurrentUser.OpenSubKey($"{"AppEvents\\Schemes\\Apps"}\\{appName}\\{evt}\\{scheme}", writable: true);
				sch?.SetValue("", orig);
			}
			File.Delete(SnapshotPath);
			Logger.Log($"Win8.1 sounds reverted ({snapshot.Count} events, scheme={scheme})");
		}
		catch (Exception ex)
		{
			Logger.Log("Win8.1 sounds revert failed: " + ex.Message);
		}
	}
}
