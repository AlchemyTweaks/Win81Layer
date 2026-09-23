using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;

namespace Win81Layer;

// Windows 11 (build >= 22000) notification-area reader.
//
// On Win11 the classic Shell_TrayWnd -> TrayNotifyWnd -> SysPager -> ToolbarWindow32 chain that the legacy TrayReader
// walks no longer holds the icons (SysPager/ToolbarWindow32 are gone — verified live on build 26200), so the legacy
// reader returns EMPTY and the launcher's taskbar shows no tray icons. Win11 instead keeps per-icon state under
// HKCU\Control Panel\NotifyIconSettings: ExecutablePath, UID, InitialTooltip, IsPromoted (1 = on the bar, 0 = overflow)
// and an optional IconSnapshot — a PNG of the last-drawn icon. We render from that, filtered to currently-running
// executables (the registry retains many historical entries), falling back to the exe's own icon when no snapshot
// exists. This is a RENDER path: real click-forwarding is not possible from the registry (no owner HWND / callback
// message), so the consumer activates/launches the owning executable instead.
public static class Win11TrayReader
{
	private const string KeyPath = "Control Panel\\NotifyIconSettings";

	// RtlGetVersion-independent lower bound is fine here: 22000 is the first Win11 build and every Win11 build is >= it.
	public static bool IsWin11 => Environment.OSVersion.Version.Build >= 22000;

	public static List<TrayIconInfo> Enumerate()
	{
		List<TrayIconInfo> result = new List<TrayIconInfo>();
		HashSet<string> running = RunningExeNames();
		try
		{
			using RegistryKey root = Registry.CurrentUser.OpenSubKey(KeyPath);
			if (root == null)
			{
				return result;
			}
			foreach (string subName in root.GetSubKeyNames())
			{
				try
				{
					using RegistryKey k = root.OpenSubKey(subName);
					if (k == null)
					{
						continue;
					}
					string exeRaw = k.GetValue("ExecutablePath") as string;
					if (string.IsNullOrWhiteSpace(exeRaw))
					{
						continue;
					}
					string exe = ResolveExe(exeRaw);
					string baseName = SafeBaseName(exe);
					if (string.IsNullOrEmpty(baseName))
					{
						continue;
					}
					if (baseName.Equals("explorer", StringComparison.OrdinalIgnoreCase))
					{
						continue;   // the shell's own tray host, not an app notification icon
					}
					if (!running.Contains(baseName))
					{
						continue;   // drop historical / not-running entries (the registry keeps dozens)
					}
					uint uid = ReadUint(k, "UID");
					int promoted = ReadInt(k, "IsPromoted", 0);
					string tip = (k.GetValue("InitialTooltip") as string) ?? "";
					ImageSource img = DecodeSnapshot(k.GetValue("IconSnapshot") as byte[]) ?? ExtractExeIcon(exe);
					if (img == null)
					{
						continue;   // nothing renderable — skip rather than show a blank
					}
					result.Add(new TrayIconInfo
					{
						OwnerHwnd = IntPtr.Zero,
						Id = StableId(subName),
						CallbackMessage = 0u,
						HIcon = IntPtr.Zero,
						Tooltip = (string.IsNullOrWhiteSpace(tip) ? baseName : tip),
						Hidden = false,
						FromOverflow = promoted == 0,
						Image = img,
						ExecutablePath = exe,
						StableId = baseName + ":" + uid
					});
				}
				catch
				{
				}
			}
		}
		catch (Exception ex)
		{
			Logger.Log("Win11TrayReader failed: " + ex.Message);
		}
		return result;
	}

	private static HashSet<string> RunningExeNames()
	{
		HashSet<string> set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		try
		{
			Process[] all = Process.GetProcesses();
			foreach (Process p in all)
			{
				try { set.Add(p.ProcessName); } catch { }
				finally { p.Dispose(); }
			}
		}
		catch
		{
		}
		return set;
	}

	private static string SafeBaseName(string path)
	{
		try { return Path.GetFileNameWithoutExtension(path); } catch { return ""; }
	}

	private static string ResolveExe(string raw)
	{
		try
		{
			raw = Environment.ExpandEnvironmentVariables(raw.Trim());
			// Some entries are stored as "{KnownFolderGUID}\relative\path.exe" — resolve the leading known folder.
			if (raw.StartsWith("{", StringComparison.Ordinal))
			{
				int close = raw.IndexOf('}');
				if (close > 1 && Guid.TryParse(raw.Substring(1, close - 1), out Guid fid))
				{
					string folder = KnownFolder(fid);
					if (!string.IsNullOrEmpty(folder))
					{
						return Path.Combine(folder, raw.Substring(close + 1).TrimStart('\\'));
					}
				}
			}
		}
		catch
		{
		}
		return raw;
	}

	private static string KnownFolder(Guid id)
	{
		try
		{
			if (SHGetKnownFolderPath(ref id, 0u, IntPtr.Zero, out nint p) == 0 && p != IntPtr.Zero)
			{
				try { return Marshal.PtrToStringUni(p); }
				finally { CoTaskMemFree(p); }
			}
		}
		catch
		{
		}
		return null;
	}

	private static uint ReadUint(RegistryKey k, string name)
	{
		try
		{
			object v = k.GetValue(name);
			return v == null ? 0u : (uint)Convert.ToInt64(v);
		}
		catch { return 0u; }
	}

	private static int ReadInt(RegistryKey k, string name, int fallback)
	{
		try
		{
			object v = k.GetValue(name);
			return v == null ? fallback : Convert.ToInt32(v);
		}
		catch { return fallback; }
	}

	// Deterministic FNV-1a hash so an icon keeps the SAME synthetic Id across scans/restarts (stable de-dup + ordering).
	private static uint StableId(string s)
	{
		uint hash = 2166136261u;
		foreach (char c in s)
		{
			hash = (hash ^ c) * 16777619u;
		}
		return hash;
	}

	private static ImageSource DecodeSnapshot(byte[] png)
	{
		if (png == null || png.Length < 8 || png[0] != 0x89 || png[1] != 0x50)
		{
			return null;   // not a PNG snapshot
		}
		try
		{
			using MemoryStream ms = new MemoryStream(png, writable: false);
			PngBitmapDecoder dec = new PngBitmapDecoder(ms, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
			if (dec.Frames.Count == 0)
			{
				return null;
			}
			BitmapSource frame = dec.Frames[0];
			frame.Freeze();
			return frame;
		}
		catch
		{
			return null;
		}
	}

	private static ImageSource ExtractExeIcon(string exe)
	{
		nint[] icons = new nint[1];
		try
		{
			if (string.IsNullOrEmpty(exe) || !File.Exists(exe))
			{
				return null;
			}
			if (PrivateExtractIcons(exe, 0, 32, 32, icons, IntPtr.Zero, 1u, 0u) == 0 || icons[0] == IntPtr.Zero)
			{
				return null;
			}
			ImageSource src = Imaging.CreateBitmapSourceFromHIcon(icons[0], Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
			src.Freeze();
			// A blank exe icon falls through so the tray assignment's EnsureNonBlank supplies a letter tile.
			return IconResolver.IsBlank(src) ? null : src;
		}
		catch
		{
			return null;
		}
		finally
		{
			if (icons[0] != IntPtr.Zero)
			{
				DestroyIcon(icons[0]);
			}
		}
	}

	[DllImport("shell32.dll")]
	private static extern int SHGetKnownFolderPath(ref Guid id, uint flags, nint token, out nint path);

	[DllImport("ole32.dll")]
	private static extern void CoTaskMemFree(nint p);

	[DllImport("user32.dll", CharSet = CharSet.Unicode)]
	private static extern uint PrivateExtractIcons(string file, int index, int cx, int cy, nint[] icons, nint ids, uint count, uint flags);

	[DllImport("user32.dll")]
	private static extern bool DestroyIcon(nint h);
}
