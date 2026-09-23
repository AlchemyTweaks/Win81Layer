using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Win32;

namespace Win81Layer;

internal static class SystemIcons81
{
	private const string ThisPc = "{20D04FE0-3AEA-1069-A2D8-08002B30309D}";

	private const string Recycle = "{645FF040-5081-101B-9F08-00AA002F954E}";

	private const string Network = "{F02C1A0D-BE21-4350-88B0-7367FC96EF3C}";

	private const string ControlPanel = "{5399E694-6CE5-4D6C-8FCE-1D8870FDCBA0}";

	private const string UsersFiles = "{59031a47-3f72-44a7-89c5-5595fe6b30ee}";

	// NOTE: the Recycle Bin unnamed (Default) value is NOT in this static table. It is what the desktop actually renders,
	// but it must track bin state, so it is written dynamically by SetRecycleDefault() (from Apply at startup and from
	// RecycleBinWatcher on every empty<->full transition). Keeping it out of the static table lets Apply's change-
	// detection stay stable across boots (a static default would mismatch the live state every boot and re-nuke the cache).
	private static readonly (string Clsid, string Value, string File)[] Overrides = new(string, string, string)[6]
	{
		// thispc81b = user-supplied authentic Win8.1 Computer tile (grey #808080 + white monitor/grid). NEW filename
		// vs thispc81a so Apply() sees a changed DefaultIcon PATH -> RefreshIcons() clears the shell icon cache and the
		// native desktop/Explorer This PC actually repaints (overwriting the .ico in place would show the stale cache).
		("{20D04FE0-3AEA-1069-A2D8-08002B30309D}", "", "thispc81b.ico"),
		("{645FF040-5081-101B-9F08-00AA002F954E}", "empty", "recyclebin-empty81a.ico"),
		("{645FF040-5081-101B-9F08-00AA002F954E}", "full", "recyclebin-full81a.ico"),
		("{F02C1A0D-BE21-4350-88B0-7367FC96EF3C}", "", "network81.ico"),
		("{5399E694-6CE5-4D6C-8FCE-1D8870FDCBA0}", "", "controlpanel81.ico"),
		("{59031a47-3f72-44a7-89c5-5595fe6b30ee}", "", "usersfiles81.ico")
	};

	// This PC library folders (Documents/Music/Pictures/Videos/Downloads) resolve their icon from a desktop.ini INSIDE the
	// physical folder (IconResource=), NOT from the per-user CLSID path above — so they are handled by ApplyKnownFolderIcons
	// (reversible: the original desktop.ini bytes are snapshotted exactly). Desktop is omitted (no confirmed 8.1 icon).
	private static (string Path, string File)[] KnownFolderTargets()
	{
		string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
		return new (string, string)[5]
		{
			(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "documents81.ico"),
			(Environment.GetFolderPath(Environment.SpecialFolder.MyMusic), "music81.ico"),
			(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "pictures81.ico"),
			(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "videos81.ico"),
			(Path.Combine(profile, "Downloads"), "downloads81.ico")
		};
	}

	private static string KnownFoldersSnapPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Win81Layer", "known-folders.snap");

	// -auth files = authentic Win8.1 icons extracted from the ISO (imageres.dll: This PC=109, Recycle empty=55, full=54);
	// the old thispc.ico/recyclebin-*.ico were recreations. Keep both old + new names in OwnFiles so IsOurs still recognizes
	// a previously-applied recreated value during Restore.
	private static readonly string[] OwnFiles = new string[19] { "thispc81b.ico", "thispc81a.ico", "recyclebin-empty81a.ico", "recyclebin-full81a.ico", "network81.ico", "controlpanel81.ico", "usersfiles81.ico", "thispc.ico", "recyclebin-empty.ico", "recyclebin-full.ico", "folder81b.ico", "folder-open81b.ico", "folder81.ico", "folder-open81.ico", "documents81.ico", "downloads81.ico", "music81.ico", "pictures81.ico", "videos81.ico" };

	// HKCU\...\Explorer\Shell Icons overrides the shell's default icon TABLE by numeric index — this is what retints icons
	// in NATIVE File Explorer (not just our CLSID desktop entries). Index 3 = generic closed folder, 4 = open folder. These
	// hit every folder shown in native Explorer + the desktop. Snapshotted/restored exactly like the CLSID overrides.
	private const string ShellIconsPath = "Software\\Microsoft\\Windows\\CurrentVersion\\Explorer\\Shell Icons";

	// folder81b/-open81b = the user-supplied authentic Win8.1 (build 7927) gold folder. NEW filenames vs folder81/-open81
	// so Apply() sees a changed Shell Icons path -> RefreshIcons() clears the shell icon cache and native Explorer folders
	// actually repaint (overwriting the .ico in place would keep showing the stale cached folder — the thispc81a->b lesson).
	private static readonly (string Index, string File)[] ShellIconOverrides = new (string, string)[2]
	{
		("3", "folder81b.ico"),
		("4", "folder-open81b.ico")
	};

	private const int SHCNE_ASSOCCHANGED = 134217728;

	private const uint SHCNF_IDLIST = 0u;

	private static string BundledDir => Path.Combine(AppContext.BaseDirectory, "Assets", "Win81Icons");

	private static string StableDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Win81Layer", "icons");

	private static string SnapPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Win81Layer", "system-icons.snap");

	private static string ShellIconsSnapPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Win81Layer", "shell-icons.snap");

	internal static bool IsApplied => File.Exists(SnapPath);

	private static string KeyPath(string clsid)
	{
		return "Software\\Microsoft\\Windows\\CurrentVersion\\Explorer\\CLSID\\" + clsid + "\\DefaultIcon";
	}

	internal static void Apply()
	{
		try
		{
			Directory.CreateDirectory(StableDir);
			string[] ownFiles = OwnFiles;
			foreach (string f in ownFiles)
			{
				string src = Path.Combine(BundledDir, f);
				string dst = Path.Combine(StableDir, f);
				try
				{
					if (File.Exists(src))
					{
						File.Copy(src, dst, overwrite: true);
					}
				}
				catch
				{
				}
			}
			Dictionary<string, string> snap = (File.Exists(SnapPath) ? (JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(SnapPath)) ?? new Dictionary<string, string>()) : new Dictionary<string, string>());
			bool snapChanged = false;
			(string, string, string)[] overrides = Overrides;
			for (int j = 0; j < overrides.Length; j++)
			{
				(string, string, string) tuple = overrides[j];
				string clsid = tuple.Item1;
				string value = tuple.Item2;
				string key = clsid + "|" + value;
				if (!snap.ContainsKey(key))
				{
					string cur = ReadValue(clsid, value);
					snap[key] = (IsOurs(cur) ? null : cur);
					snapChanged = true;
				}
			}
			if (snapChanged)
			{
				Directory.CreateDirectory(Path.GetDirectoryName(SnapPath));
				File.WriteAllText(SnapPath, JsonSerializer.Serialize(snap));
			}
			(string, string, string)[] overrides2 = Overrides;
			bool changed = false;
			for (int k = 0; k < overrides2.Length; k++)
			{
				(string, string, string) tuple2 = overrides2[k];
				string clsid2 = tuple2.Item1;
				string value2 = tuple2.Item2;
				string file = tuple2.Item3;
				string icon = Path.Combine(StableDir, file);
				// Only write (and thus flag a change) when the value is not already correct.
				if (File.Exists(icon) && !string.Equals(ReadValue(clsid2, value2), icon, StringComparison.OrdinalIgnoreCase))
				{
					WriteValue(clsid2, value2, icon);
					changed = true;
				}
			}
			// The rendered Recycle Bin (Default) tracks the live bin state (see SetRecycleDefault).
			if (SetRecycleDefault(RecycleBinIsEmpty()))
			{
				changed = true;
			}
			// NATIVE folder icon override (Shell Icons\3,4) — retints every folder in real File Explorer.
			if (ApplyShellIcons())
			{
				changed = true;
			}
			// NATIVE library folders (Documents/Music/Pictures/Videos/Downloads) via their own desktop.ini.
			if (ApplyKnownFolderIcons())
			{
				changed = true;
			}
			// CRITICAL: only clear the shell icon cache when a value ACTUALLY changed. Apply() runs on EVERY boot, and
			// RefreshIcons() calls ie4uinit -ClearIconCache — doing that unconditionally nukes the cache each boot and
			// makes This PC / Recycle Bin fail to load until a later refresh (the reported "icons don't load" bug).
			if (changed)
			{
				RefreshIcons();
			}
			else
			{
				SHChangeNotify(SHCNE_ASSOCCHANGED, SHCNF_IDLIST, IntPtr.Zero, IntPtr.Zero);   // light nudge, no cache wipe
			}
			Logger.Log($"[sysicons] applied Win8.1 This PC / Recycle Bin / native folder + library icons (changed={changed})");
		}
		catch (Exception ex)
		{
			Logger.Log("[sysicons] apply failed: " + ex.Message);
		}
	}

	// Writes the rendered Recycle Bin (Default) icon to match state. Returns true only if it actually changed. Called at
	// startup (Apply) and on every empty<->full transition (RecycleBinWatcher), so the desktop glyph always reflects the
	// real bin state. This is what actually fixes "stays full after emptying": we own the rendered value and swap it.
	internal static bool SetRecycleDefault(bool empty)
	{
		try
		{
			if (!IsApplied)
			{
				return false;
			}
			string icon = Path.Combine(StableDir, empty ? "recyclebin-empty81a.ico" : "recyclebin-full81a.ico");
			if (!File.Exists(icon) || string.Equals(ReadValue(Recycle, ""), icon, StringComparison.OrdinalIgnoreCase))
			{
				return false;
			}
			WriteValue(Recycle, "", icon);
			return true;
		}
		catch
		{
			return false;
		}
	}

	private static bool RecycleBinIsEmpty()
	{
		try
		{
			SHQUERYRBINFO info = default(SHQUERYRBINFO);
			info.cbSize = System.Runtime.InteropServices.Marshal.SizeOf<SHQUERYRBINFO>();
			return SHQueryRecycleBin(null, ref info) != 0 || info.i64NumItems <= 0;
		}
		catch
		{
			return true;
		}
	}

	[System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
	private struct SHQUERYRBINFO
	{
		public int cbSize;
		public long i64Size;
		public long i64NumItems;
	}

	[System.Runtime.InteropServices.DllImport("shell32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
	private static extern int SHQueryRecycleBin(string? pszRootPath, ref SHQUERYRBINFO pSHQueryRBInfo);

	internal static void Restore()
	{
		try
		{
			// Any revert path stops the live empty<->full watcher (no-op if it was never started).
			try { RecycleBinWatcher.Stop(); } catch { }
			// Revert the native folder-icon override (own snapshot; no-op if never applied). Done before the IsApplied guard
			// so folders return to native even if the CLSID snapshot is gone; the RefreshIcons at the end repaints them.
			RestoreShellIcons();
			// Revert the native library-folder desktop.ini icons (own snapshot; byte-exact original restored).
			RestoreKnownFolderIcons();
			if (!IsApplied)
			{
				return;   // already native — do NOT run RefreshIcons/ClearIconCache every boot (that blanks the icons)
			}
			Dictionary<string, string> snap = (File.Exists(SnapPath) ? (JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(SnapPath)) ?? new Dictionary<string, string>()) : new Dictionary<string, string>());
			// The recycle Bin unnamed (Default) is managed dynamically (SetRecycleDefault) and isn't in the Overrides
			// table, so remove it explicitly here to fully return to the native recycle icon.
			if (IsOurs(ReadValue(Recycle, "")))
			{
				DeleteValue(Recycle, "");
			}
			(string, string, string)[] overrides = Overrides;
			for (int i = 0; i < overrides.Length; i++)
			{
				(string, string, string) tuple = overrides[i];
				string clsid = tuple.Item1;
				string value = tuple.Item2;
				string orig = snap.GetValueOrDefault(clsid + "|" + value);
				if (orig == null || IsOurs(orig) || !OriginalFileResolves(orig))
				{
					DeleteValue(clsid, value);
				}
				else
				{
					WriteValue(clsid, value, orig);
				}
			}
			foreach (string clsid2 in Overrides.Select(((string Clsid, string Value, string File) o) => o.Clsid).Distinct())
			{
				RemoveIfEmpty(clsid2);
			}
			try
			{
				if (File.Exists(SnapPath))
				{
					File.Delete(SnapPath);
				}
			}
			catch
			{
			}
			RefreshIcons();
			Logger.Log("[sysicons] restored original system icons");
		}
		catch (Exception ex)
		{
			Logger.Log("[sysicons] restore failed: " + ex.Message);
		}
	}

	private static bool IsOurs(string? path)
	{
		if (string.IsNullOrWhiteSpace(path))
		{
			return false;
		}
		string p = path.Replace('/', '\\');
		if (p.Contains("\\Assets\\Win81Icons\\", StringComparison.OrdinalIgnoreCase))
		{
			return true;
		}
		if (p.Contains("\\Win81Layer\\icons\\", StringComparison.OrdinalIgnoreCase))
		{
			return true;
		}
		string[] ownFiles = OwnFiles;
		foreach (string f in ownFiles)
		{
			if (p.EndsWith("\\" + f, StringComparison.OrdinalIgnoreCase))
			{
				return true;
			}
		}
		return false;
	}

	private static bool OriginalFileResolves(string value)
	{
		try
		{
			string s = value.Trim();
			int comma = s.LastIndexOf(',');
			if (comma > 0)
			{
				string text = s;
				int num = comma + 1;
				if (int.TryParse(text.Substring(num, text.Length - num).Trim(), out var _))
				{
					s = s.Substring(0, comma);
				}
			}
			s = s.Trim().Trim('"');
			s = Environment.ExpandEnvironmentVariables(s);
			return File.Exists(s);
		}
		catch
		{
			return false;
		}
	}

	private static string? ReadValue(string clsid, string value)
	{
		using RegistryKey k = Registry.CurrentUser.OpenSubKey(KeyPath(clsid));
		return k?.GetValue(value) as string;
	}

	private static void WriteValue(string clsid, string value, string data)
	{
		using RegistryKey k = Registry.CurrentUser.CreateSubKey(KeyPath(clsid));
		k?.SetValue(value, data, RegistryValueKind.ExpandString);
	}

	private static void DeleteValue(string clsid, string value)
	{
		using RegistryKey k = Registry.CurrentUser.OpenSubKey(KeyPath(clsid), writable: true);
		try
		{
			k?.DeleteValue(value, throwOnMissingValue: false);
		}
		catch
		{
		}
	}

	private static string ReadShellIcon(string index)
	{
		using RegistryKey k = Registry.CurrentUser.OpenSubKey(ShellIconsPath);
		return k?.GetValue(index) as string;
	}

	private static void WriteShellIcon(string index, string data)
	{
		using RegistryKey k = Registry.CurrentUser.CreateSubKey(ShellIconsPath);
		k?.SetValue(index, data, RegistryValueKind.ExpandString);
	}

	private static void DeleteShellIcon(string index)
	{
		using RegistryKey k = Registry.CurrentUser.OpenSubKey(ShellIconsPath, writable: true);
		try
		{
			k?.DeleteValue(index, throwOnMissingValue: false);
		}
		catch
		{
		}
	}

	// Overrides the NATIVE shell folder icons (Shell Icons\3 closed, \4 open) reversibly, so every folder in real File
	// Explorer shows the authentic Win8.1 folder. Snapshots the true pre-Apply values once (recording null when the current
	// value is already ours, so Restore deletes back to native). Returns true if a registry value actually changed.
	private static bool ApplyShellIcons()
	{
		bool changed = false;
		try
		{
			Dictionary<string, string> snap = (File.Exists(ShellIconsSnapPath) ? (JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(ShellIconsSnapPath)) ?? new Dictionary<string, string>()) : new Dictionary<string, string>());
			bool snapChanged = false;
			(string, string)[] shellIcons = ShellIconOverrides;
			for (int i = 0; i < shellIcons.Length; i++)
			{
				string index = shellIcons[i].Item1;
				if (!snap.ContainsKey(index))
				{
					string cur = ReadShellIcon(index);
					snap[index] = (IsOurs(cur) ? null : cur);
					snapChanged = true;
				}
			}
			if (snapChanged)
			{
				Directory.CreateDirectory(Path.GetDirectoryName(ShellIconsSnapPath));
				File.WriteAllText(ShellIconsSnapPath, JsonSerializer.Serialize(snap));
			}
			for (int j = 0; j < shellIcons.Length; j++)
			{
				string index2 = shellIcons[j].Item1;
				string icon = Path.Combine(StableDir, shellIcons[j].Item2);
				if (File.Exists(icon) && !string.Equals(ReadShellIcon(index2), icon, StringComparison.OrdinalIgnoreCase))
				{
					WriteShellIcon(index2, icon);
					changed = true;
				}
			}
		}
		catch (Exception ex)
		{
			Logger.Log("[sysicons] shell-icons apply: " + ex.Message);
		}
		return changed;
	}

	private static void RestoreShellIcons()
	{
		try
		{
			if (!File.Exists(ShellIconsSnapPath))
			{
				return;
			}
			Dictionary<string, string> snap = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(ShellIconsSnapPath)) ?? new Dictionary<string, string>();
			(string, string)[] shellIcons = ShellIconOverrides;
			for (int i = 0; i < shellIcons.Length; i++)
			{
				string index = shellIcons[i].Item1;
				string orig = snap.GetValueOrDefault(index);
				if (orig == null || IsOurs(orig) || !OriginalFileResolves(orig))
				{
					DeleteShellIcon(index);
				}
				else
				{
					WriteShellIcon(index, orig);
				}
			}
			try
			{
				File.Delete(ShellIconsSnapPath);
			}
			catch
			{
			}
		}
		catch (Exception ex)
		{
			Logger.Log("[sysicons] shell-icons restore: " + ex.Message);
		}
	}

	// Applies the authentic Win8.1 icon to the This PC library folders (Documents/Music/Pictures/Videos/Downloads) by
	// rewriting the IconResource= line in each folder's own desktop.ini. Snapshots the EXACT original desktop.ini bytes once
	// (base64), so Restore puts them back byte-for-byte (or removes the file if there was none). Returns true if something
	// actually changed. Change-detected: no rewrite once our icon is already set, so no per-boot cache churn.
	private static bool ApplyKnownFolderIcons()
	{
		bool changed = false;
		try
		{
			Dictionary<string, string> snap = (File.Exists(KnownFoldersSnapPath) ? (JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(KnownFoldersSnapPath)) ?? new Dictionary<string, string>()) : new Dictionary<string, string>());
			bool snapChanged = false;
			(string, string)[] targets = KnownFolderTargets();
			for (int i = 0; i < targets.Length; i++)
			{
				string path = targets[i].Item1;
				string file = targets[i].Item2;
				if (string.IsNullOrEmpty(path) || !Directory.Exists(path))
				{
					continue;
				}
				string icon = Path.Combine(StableDir, file);
				if (!File.Exists(icon))
				{
					continue;
				}
				string ini = Path.Combine(path, "desktop.ini");
				string key = path.ToLowerInvariant();
				if (!snap.ContainsKey(key))
				{
					// Snapshot the true original once. Guard: if it already carries OUR icon (a prior apply whose snapshot
					// was lost), record ABSENT-of-ours so Restore strips our line rather than pinning it forever.
					if (File.Exists(ini) && IsOurs(ReadIniIconResource(ini)))
					{
						snap[key] = "\0OURS";
					}
					else
					{
						snap[key] = (File.Exists(ini) ? Convert.ToBase64String(File.ReadAllBytes(ini)) : "\0ABSENT");
					}
					snapChanged = true;
				}
				string want = icon + ",0";
				if (!string.Equals(ReadIniIconResource(ini), want, StringComparison.OrdinalIgnoreCase))
				{
					WriteMergedIni(ini, want);
					changed = true;
				}
			}
			if (snapChanged)
			{
				Directory.CreateDirectory(Path.GetDirectoryName(KnownFoldersSnapPath));
				File.WriteAllText(KnownFoldersSnapPath, JsonSerializer.Serialize(snap));
			}
		}
		catch (Exception ex)
		{
			Logger.Log("[sysicons] known-folder apply: " + ex.Message);
		}
		return changed;
	}

	private static void RestoreKnownFolderIcons()
	{
		try
		{
			if (!File.Exists(KnownFoldersSnapPath))
			{
				return;
			}
			Dictionary<string, string> snap = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(KnownFoldersSnapPath)) ?? new Dictionary<string, string>();
			(string, string)[] targets = KnownFolderTargets();
			for (int i = 0; i < targets.Length; i++)
			{
				string path = targets[i].Item1;
				if (string.IsNullOrEmpty(path))
				{
					continue;
				}
				string key = path.ToLowerInvariant();
				if (!snap.ContainsKey(key))
				{
					continue;
				}
				string ini = Path.Combine(path, "desktop.ini");
				string original = snap[key];
				try
				{
					if (original == "\0ABSENT")
					{
						if (File.Exists(ini)) { File.SetAttributes(ini, FileAttributes.Normal); File.Delete(ini); }
					}
					else if (original == "\0OURS")
					{
						// We can't recover the true original — just strip our IconResource so the shell falls back to native.
						WriteMergedIni(ini, null);
					}
					else
					{
						File.WriteAllBytes(ini, Convert.FromBase64String(original));
						try { File.SetAttributes(ini, FileAttributes.Hidden | FileAttributes.System); } catch { }
					}
				}
				catch
				{
				}
			}
			try { File.Delete(KnownFoldersSnapPath); } catch { }
		}
		catch (Exception ex)
		{
			Logger.Log("[sysicons] known-folder restore: " + ex.Message);
		}
	}

	// Reads the IconResource= value from a desktop.ini's [.ShellClassInfo] section (null if absent/unreadable).
	private static string ReadIniIconResource(string ini)
	{
		try
		{
			if (!File.Exists(ini))
			{
				return null;
			}
			string[] lines = File.ReadAllLines(ini);
			for (int i = 0; i < lines.Length; i++)
			{
				string line = lines[i].Trim();
				if (line.StartsWith("IconResource=", StringComparison.OrdinalIgnoreCase))
				{
					return line.Substring("IconResource=".Length).Trim();
				}
			}
		}
		catch
		{
		}
		return null;
	}

	// Rewrites desktop.ini so [.ShellClassInfo] IconResource = iconResource, PRESERVING every other line (e.g. the
	// LocalizedResourceName that gives Documents its localised name). Legacy IconFile/IconIndex are dropped so they can't win.
	// iconResource == null removes our IconResource line (used by restore when the true original was unrecoverable). Written
	// UTF-16 (shell-safe); exact original bytes live in the snapshot for byte-perfect restore.
	private static void WriteMergedIni(string ini, string iconResource)
	{
		List<string> lines = new List<string>();
		if (File.Exists(ini))
		{
			try { lines.AddRange(File.ReadAllLines(ini)); } catch { }
		}
		List<string> outLines = new List<string>();
		bool inShellClass = false;
		bool wroteIcon = (iconResource == null);
		bool sawSection = false;
		for (int i = 0; i < lines.Count; i++)
		{
			string line = lines[i];
			string t = line.Trim();
			if (t.StartsWith("[") && t.EndsWith("]"))
			{
				if (inShellClass && !wroteIcon)
				{
					outLines.Add("IconResource=" + iconResource);
					wroteIcon = true;
				}
				inShellClass = t.Equals("[.ShellClassInfo]", StringComparison.OrdinalIgnoreCase);
				if (inShellClass) { sawSection = true; }
				outLines.Add(line);
				continue;
			}
			if (inShellClass && (t.StartsWith("IconResource=", StringComparison.OrdinalIgnoreCase) || t.StartsWith("IconFile=", StringComparison.OrdinalIgnoreCase) || t.StartsWith("IconIndex=", StringComparison.OrdinalIgnoreCase)))
			{
				if (t.StartsWith("IconResource=", StringComparison.OrdinalIgnoreCase) && iconResource != null && !wroteIcon)
				{
					outLines.Add("IconResource=" + iconResource);
					wroteIcon = true;
				}
				continue;   // drop the old icon directive (replaced above, or removed when iconResource==null)
			}
			outLines.Add(line);
		}
		if (inShellClass && !wroteIcon)
		{
			outLines.Add("IconResource=" + iconResource);
			wroteIcon = true;
		}
		if (!sawSection && iconResource != null)
		{
			outLines.Insert(0, "[.ShellClassInfo]");
			outLines.Insert(1, "IconResource=" + iconResource);
		}
		try { if (File.Exists(ini)) { File.SetAttributes(ini, FileAttributes.Normal); } } catch { }
		File.WriteAllText(ini, string.Join("\r\n", outLines) + "\r\n", System.Text.Encoding.Unicode);
		try { File.SetAttributes(ini, FileAttributes.Hidden | FileAttributes.System); } catch { }
	}

	private static void RemoveIfEmpty(string clsid)
	{
		try
		{
			using (RegistryKey k = Registry.CurrentUser.OpenSubKey(KeyPath(clsid)))
			{
				if (k == null || k.ValueCount > 0 || k.SubKeyCount > 0)
				{
					return;
				}
			}
			Registry.CurrentUser.DeleteSubKey(KeyPath(clsid), throwOnMissingSubKey: false);
		}
		catch
		{
		}
	}

	[DllImport("shell32.dll")]
	private static extern void SHChangeNotify(int eventId, uint flags, nint item1, nint item2);

	private static void RefreshIcons()
	{
		SHChangeNotify(134217728, 0u, IntPtr.Zero, IntPtr.Zero);
		try
		{
			string ie4 = Path.Combine(Environment.SystemDirectory, "ie4uinit.exe");
			// Win11 caches shell icons aggressively: clear the per-user icon cache FIRST so This PC / Recycle Bin actually
			// re-read the (changed or reverted) DefaultIcon instead of showing the stale cached glyph.
			if (File.Exists(ie4)) { try { Process.Start(new ProcessStartInfo(ie4, "-ClearIconCache") { UseShellExecute = false, CreateNoWindow = true }).WaitForExit(2000); } catch { } }
			if (File.Exists(ie4))
			{
				Process.Start(new ProcessStartInfo(ie4, "-show")
				{
					UseShellExecute = false,
					CreateNoWindow = true
				});
			}
		}
		catch (Exception ex)
		{
			Logger.Log("[sysicons] ie4uinit refresh: " + ex.Message);
		}
		SHChangeNotify(134217728, 0u, IntPtr.Zero, IntPtr.Zero);
	}
}
