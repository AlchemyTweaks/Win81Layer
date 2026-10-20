#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;

namespace Win81Layer;

internal sealed class MonitorWallpaper
{
	public string MonitorId { get; set; } = "";

	public string Path { get; set; } = "";
}

// The user's desktop wallpaper exactly as it was, per monitor, so it can be put back later.
internal sealed class DesktopWallpaperSnapshot
{
	public string Api { get; set; } = "com";

	public List<MonitorWallpaper> Monitors { get; set; } = new List<MonitorWallpaper>();

	public int Position { get; set; }

	public uint BackgroundColor { get; set; }

	public string? RegWallPaper { get; set; }

	public string? RegWallpaperStyle { get; set; }

	public string? RegTileWallpaper { get; set; }

	public List<string?> History { get; set; } = new List<string?>();

	public string AppliedPath { get; set; } = "";

	public DesktopWallpaperSnapshot Clone()
	{
		DesktopWallpaperSnapshot c = (DesktopWallpaperSnapshot)MemberwiseClone();
		c.Monitors = Monitors.Select(m => new MonitorWallpaper { MonitorId = m.MonitorId, Path = m.Path }).ToList();
		c.History = new List<string?>(History);
		return c;
	}
}

// Desktop wallpaper through the documented IDesktopWallpaper COM interface (per-monitor), with a
// SystemParametersInfo fallback. Reads are always safe. Every setter refuses to run in read-only diagnostics.
internal static class DesktopWallpaperApi
{
	public const int PositionFill = 4;

	private const int DssSlideshow = 0x2;

	private const uint SPI_GETDESKWALLPAPER = 0x0073;

	private const uint SPI_SETDESKWALLPAPER = 0x0014;

	private const uint SPIF_UPDATEINIFILE = 0x01;

	private const uint SPIF_SENDCHANGE = 0x02;

	private const string DesktopKey = "Control Panel\\Desktop";

	private const string HistoryKey = "Software\\Microsoft\\Windows\\CurrentVersion\\Explorer\\Wallpapers";

	[StructLayout(LayoutKind.Sequential)]
	private struct RECT
	{
		public int Left;

		public int Top;

		public int Right;

		public int Bottom;
	}

	// Vtable order matters: it must match shobjidl_core.h exactly.
	[ComImport]
	[Guid("B92B56A9-8B55-4E14-9A89-0199BBB6F93B")]
	[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
	private interface IDesktopWallpaper
	{
		void SetWallpaper([MarshalAs(UnmanagedType.LPWStr)] string? monitorId, [MarshalAs(UnmanagedType.LPWStr)] string wallpaper);

		[return: MarshalAs(UnmanagedType.LPWStr)]
		string GetWallpaper([MarshalAs(UnmanagedType.LPWStr)] string? monitorId);

		[return: MarshalAs(UnmanagedType.LPWStr)]
		string GetMonitorDevicePathAt(uint monitorIndex);

		uint GetMonitorDevicePathCount();

		RECT GetMonitorRECT([MarshalAs(UnmanagedType.LPWStr)] string monitorId);

		void SetBackgroundColor(uint color);

		uint GetBackgroundColor();

		void SetPosition(int position);

		int GetPosition();

		void SetSlideshow(IntPtr items);

		IntPtr GetSlideshow();

		void SetSlideshowOptions(int options, uint slideshowTick);

		void GetSlideshowOptions(out int options, out uint slideshowTick);

		void AdvanceSlideshow([MarshalAs(UnmanagedType.LPWStr)] string? monitorId, int direction);

		int GetStatus();

		void Enable([MarshalAs(UnmanagedType.Bool)] bool enable);
	}

	private static readonly Guid DesktopWallpaperClsid = new Guid("C2CF3110-460E-4fc1-B9D0-8A1C0C9CC4BD");

	[DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "SystemParametersInfoW")]
	private static extern bool SystemParametersInfoGet(uint action, uint uParam, StringBuilder pvParam, uint fWinIni);

	[DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "SystemParametersInfoW")]
	private static extern bool SystemParametersInfoSet(uint action, uint uParam, string pvParam, uint fWinIni);

	private static IDesktopWallpaper Create()
	{
		Type type = Type.GetTypeFromCLSID(DesktopWallpaperClsid, throwOnError: true)!;
		return (IDesktopWallpaper)Activator.CreateInstance(type)!;
	}

	private static void Release(object? com)
	{
		try
		{
			if (com != null && Marshal.IsComObject(com))
			{
				Marshal.ReleaseComObject(com);
			}
		}
		catch
		{
		}
	}

	// Attached monitors only: GetMonitorRECT fails for monitor ids Explorer remembers but that are not connected.
	private static List<string> AttachedMonitors(IDesktopWallpaper dw)
	{
		List<string> ids = new List<string>();
		uint count = dw.GetMonitorDevicePathCount();
		for (uint i = 0; i < count; i++)
		{
			try
			{
				string id = dw.GetMonitorDevicePathAt(i);
				if (string.IsNullOrEmpty(id))
				{
					continue;
				}
				dw.GetMonitorRECT(id);
				ids.Add(id);
			}
			catch
			{
			}
		}
		return ids;
	}

	// Read-only snapshot of the current desktop wallpaper. skip is set when the desktop uses something that cannot be
	// restored exactly (a slideshow or Windows Spotlight); the snapshot is still returned for logging.
	public static DesktopWallpaperSnapshot Capture(out string? skip)
	{
		skip = null;
		DesktopWallpaperSnapshot snap = new DesktopWallpaperSnapshot();
		int backgroundType = -1;
		try
		{
			using RegistryKey? desk = Registry.CurrentUser.OpenSubKey(DesktopKey);
			snap.RegWallPaper = desk?.GetValue("WallPaper") as string;
			snap.RegWallpaperStyle = desk?.GetValue("WallpaperStyle") as string;
			snap.RegTileWallpaper = desk?.GetValue("TileWallpaper") as string;
		}
		catch
		{
		}
		try
		{
			using RegistryKey? hist = Registry.CurrentUser.OpenSubKey(HistoryKey);
			if (hist?.GetValue("BackgroundType") is int bt)
			{
				backgroundType = bt;
			}
			for (int i = 0; i < 5; i++)
			{
				snap.History.Add(hist?.GetValue("BackgroundHistoryPath" + i) as string);
			}
		}
		catch
		{
		}
		int status = 0;
		IDesktopWallpaper? dw = null;
		try
		{
			dw = Create();
			foreach (string id in AttachedMonitors(dw))
			{
				string path = "";
				try
				{
					path = dw.GetWallpaper(id) ?? "";
				}
				catch
				{
				}
				snap.Monitors.Add(new MonitorWallpaper { MonitorId = id, Path = path });
			}
			snap.Position = dw.GetPosition();
			snap.BackgroundColor = dw.GetBackgroundColor();
			try
			{
				status = dw.GetStatus();
			}
			catch
			{
			}
			snap.Api = "com";
		}
		catch (Exception ex)
		{
			Logger.Log("Seasonal: desktop wallpaper COM read failed, using SPI: " + ex.Message);
			snap.Api = "spi";
			snap.Monitors.Clear();
			snap.Monitors.Add(new MonitorWallpaper { MonitorId = "", Path = SpiWallpaper() });
			snap.Position = StyleToPosition(snap.RegWallpaperStyle, snap.RegTileWallpaper);
		}
		finally
		{
			Release(dw);
		}
		// Spotlight and slideshow markers are heuristics; the raw values are logged so they can be checked on a real machine.
		if ((status & DssSlideshow) != 0 || backgroundType == 2)
		{
			skip = "slideshow";
		}
		else if (backgroundType == 3 || snap.Monitors.Any(m => m.Path.IndexOf("\\IrisService\\", StringComparison.OrdinalIgnoreCase) >= 0))
		{
			skip = "Windows Spotlight";
		}
		Logger.Log($"Seasonal: wallpaper probe api={snap.Api} monitors={snap.Monitors.Count} position={snap.Position} status=0x{status:X} backgroundType={backgroundType} skip={skip ?? "none"}");
		return snap;
	}

	public static string CurrentPathOn(DesktopWallpaperSnapshot probe, string monitorId)
	{
		return probe.Monitors.FirstOrDefault(m => string.Equals(m.MonitorId, monitorId, StringComparison.OrdinalIgnoreCase))?.Path ?? "";
	}

	// Sets path on every monitor with the Fill position, then verifies every attached monitor shows it.
	public static bool SetAll(string path)
	{
		GuardWrites();
		// Read first, so a failed attempt can put back the picture position it changed on the way.
		(string? oldStyle, string? oldTile) = ReadStyleValues();
		int? oldPosition = null;
		IDesktopWallpaper? dw = null;
		try
		{
			dw = Create();
			try
			{
				oldPosition = dw.GetPosition();
			}
			catch
			{
			}
			dw.SetWallpaper(null, path);
			dw.SetPosition(PositionFill);
			List<string> ids = AttachedMonitors(dw);
			bool verified = ids.Count > 0;
			foreach (string id in ids)
			{
				string now = "";
				try
				{
					now = dw.GetWallpaper(id) ?? "";
				}
				catch
				{
				}
				if (!SamePath(now, path))
				{
					verified = false;
				}
			}
			if (verified)
			{
				return true;
			}
			Logger.Log("Seasonal: wallpaper set through COM did not verify; falling back to SPI.");
		}
		catch (Exception ex)
		{
			Logger.Log("Seasonal: wallpaper COM set failed, falling back to SPI: " + ex.Message);
		}
		finally
		{
			Release(dw);
		}
		bool ok = false;
		try
		{
			using (RegistryKey? desk = Registry.CurrentUser.OpenSubKey(DesktopKey, writable: true))
			{
				desk?.SetValue("WallpaperStyle", "10");
				desk?.SetValue("TileWallpaper", "0");
			}
			ok = SystemParametersInfoSet(SPI_SETDESKWALLPAPER, 0u, path, SPIF_UPDATEINIFILE | SPIF_SENDCHANGE) && SamePath(SpiWallpaper(), path);
		}
		catch (Exception ex)
		{
			Logger.Log("Seasonal: wallpaper SPI set failed: " + ex.Message);
		}
		if (!ok)
		{
			// Nothing was set: put back the position and style values the attempt changed, so a user with Fit, Center
			// or Tile does not end up with Fill. A display that did get the picture is restored by the rollback.
			RestorePositionAndStyle(oldPosition, oldStyle, oldTile);
		}
		return ok;
	}

	// Sets path on the given monitors only (COM), leaving the others and the picture position as they are, then verifies
	// those monitors show it. There is no SPI fallback, since SPI can only set every display.
	public static bool SetOn(IReadOnlyCollection<string> monitorIds, string path)
	{
		GuardWrites();
		if (monitorIds.Count == 0)
		{
			return false;
		}
		IDesktopWallpaper? dw = null;
		try
		{
			dw = Create();
			foreach (string id in monitorIds)
			{
				dw.SetWallpaper(id, path);
			}
			bool verified = true;
			foreach (string id in monitorIds)
			{
				string now = "";
				try
				{
					now = dw.GetWallpaper(id) ?? "";
				}
				catch
				{
				}
				if (!SamePath(now, path))
				{
					verified = false;
				}
			}
			if (!verified)
			{
				Logger.Log("Seasonal: wallpaper set on some displays did not verify.");
			}
			return verified;
		}
		catch (Exception ex)
		{
			Logger.Log("Seasonal: wallpaper set on some displays failed: " + ex.Message);
			return false;
		}
		finally
		{
			Release(dw);
		}
	}

	private static (string? Style, string? Tile) ReadStyleValues()
	{
		try
		{
			using RegistryKey? desk = Registry.CurrentUser.OpenSubKey(DesktopKey);
			return (desk?.GetValue("WallpaperStyle") as string, desk?.GetValue("TileWallpaper") as string);
		}
		catch
		{
			return (null, null);
		}
	}

	private static void RestorePositionAndStyle(int? position, string? style, string? tile)
	{
		if (position.HasValue)
		{
			IDesktopWallpaper? dw = null;
			try
			{
				dw = Create();
				dw.SetPosition(position.Value);
			}
			catch (Exception ex)
			{
				Logger.Log("Seasonal: wallpaper position restore skipped: " + ex.Message);
			}
			finally
			{
				Release(dw);
			}
		}
		try
		{
			using RegistryKey? desk = Registry.CurrentUser.OpenSubKey(DesktopKey, writable: true);
			if (desk == null)
			{
				return;
			}
			if (style == null)
			{
				desk.DeleteValue("WallpaperStyle", throwOnMissingValue: false);
			}
			else
			{
				desk.SetValue("WallpaperStyle", style);
			}
			if (tile == null)
			{
				desk.DeleteValue("TileWallpaper", throwOnMissingValue: false);
			}
			else
			{
				desk.SetValue("TileWallpaper", tile);
			}
		}
		catch (Exception ex)
		{
			Logger.Log("Seasonal: wallpaper style restore skipped: " + ex.Message);
		}
	}

	// Puts back the snapshot, but only on monitors that still show a file isOurs recognises: a wallpaper the user
	// picked outside the launcher in the meantime is kept. Returns restored | kept-user | partial | failed:reason.
	public static string RestoreIfOurs(DesktopWallpaperSnapshot snap, Func<string?, bool> isOurs)
	{
		GuardWrites();
		int restored = 0;
		int kept = 0;
		bool allSolid = snap.Monitors.Count > 0 && snap.Monitors.All(m => string.IsNullOrEmpty(m.Path));
		string firstPath = snap.Monitors.Select(m => m.Path).FirstOrDefault(p => !string.IsNullOrEmpty(p)) ?? "";
		bool allSame = snap.Monitors.Count > 0 && snap.Monitors.All(m => string.Equals(m.Path, snap.Monitors[0].Path, StringComparison.OrdinalIgnoreCase));
		try
		{
			if (snap.Api == "com")
			{
				IDesktopWallpaper? dw = null;
				bool anyOurs = false;
				try
				{
					dw = Create();
					List<string> attached = AttachedMonitors(dw);
					foreach (string id in attached)
					{
						string now = SafeGet(dw, id);
						MonitorWallpaper? saved = snap.Monitors.FirstOrDefault(m => string.Equals(m.MonitorId, id, StringComparison.OrdinalIgnoreCase));
						if (!isOurs(now))
						{
							if (saved != null)
							{
								kept++;
							}
							continue;
						}
						anyOurs = true;
						if (allSolid)
						{
							continue;
						}
						// A monitor connected after the snapshot (or one that had no picture of its own while others did)
						// gets the snapshot's first picture rather than keeping the seasonal one.
						string target = !string.IsNullOrEmpty(saved?.Path) ? saved!.Path : firstPath;
						if (!string.IsNullOrEmpty(target))
						{
							dw.SetWallpaper(id, target);
							restored++;
						}
					}
					if (restored > 0 || (allSolid && anyOurs))
					{
						dw.SetPosition(snap.Position);
						dw.SetBackgroundColor(snap.BackgroundColor);
					}
				}
				finally
				{
					Release(dw);
				}
				if (allSolid && anyOurs)
				{
					// The user had a solid colour: clear the picture (the colour was put back above).
					SystemParametersInfoSet(SPI_SETDESKWALLPAPER, 0u, "", SPIF_UPDATEINIFILE | SPIF_SENDCHANGE);
					restored++;
				}
			}
			else
			{
				string now = SpiWallpaper();
				if (isOurs(now))
				{
					SystemParametersInfoSet(SPI_SETDESKWALLPAPER, 0u, firstPath, SPIF_UPDATEINIFILE | SPIF_SENDCHANGE);
					restored++;
				}
				else
				{
					kept++;
				}
			}
			if (restored > 0)
			{
				RestoreStyle(snap, allSame ? firstPath : null);
				RestoreHistory(snap, isOurs);
			}
		}
		catch (Exception ex)
		{
			Logger.Log("Seasonal: wallpaper restore failed: " + ex.Message);
			return "failed:" + ex.Message;
		}
		if (restored == 0)
		{
			return "kept-user";
		}
		return kept > 0 ? "partial" : "restored";
	}

	private static string SafeGet(IDesktopWallpaper dw, string id)
	{
		try
		{
			return dw.GetWallpaper(id) ?? "";
		}
		catch
		{
			return "";
		}
	}

	// Style values written by SetPosition can differ in detail from the snapshot (for example TileWallpaper). When
	// every monitor shared one picture, write the exact snapshot values back and re-issue the change.
	private static void RestoreStyle(DesktopWallpaperSnapshot snap, string? samePath)
	{
		try
		{
			using RegistryKey? desk = Registry.CurrentUser.OpenSubKey(DesktopKey, writable: true);
			if (desk == null)
			{
				return;
			}
			string? style = desk.GetValue("WallpaperStyle") as string;
			string? tile = desk.GetValue("TileWallpaper") as string;
			bool differs = (snap.RegWallpaperStyle != null && !string.Equals(style, snap.RegWallpaperStyle, StringComparison.Ordinal))
				|| (snap.RegTileWallpaper != null && !string.Equals(tile, snap.RegTileWallpaper, StringComparison.Ordinal));
			if (!differs || samePath == null)
			{
				return;
			}
			if (snap.RegWallpaperStyle != null)
			{
				desk.SetValue("WallpaperStyle", snap.RegWallpaperStyle);
			}
			if (snap.RegTileWallpaper != null)
			{
				desk.SetValue("TileWallpaper", snap.RegTileWallpaper);
			}
			SystemParametersInfoSet(SPI_SETDESKWALLPAPER, 0u, samePath, SPIF_UPDATEINIFILE | SPIF_SENDCHANGE);
		}
		catch (Exception ex)
		{
			Logger.Log("Seasonal: wallpaper style restore skipped: " + ex.Message);
		}
	}

	// Cosmetic: the Settings app's recent-pictures row. Only touched when it now lists a seasonal file.
	private static void RestoreHistory(DesktopWallpaperSnapshot snap, Func<string?, bool> isOurs)
	{
		try
		{
			using RegistryKey? hist = Registry.CurrentUser.OpenSubKey(HistoryKey, writable: true);
			if (hist == null || snap.History.Count != 5)
			{
				return;
			}
			bool touched = false;
			for (int i = 0; i < 5; i++)
			{
				if (isOurs(hist.GetValue("BackgroundHistoryPath" + i) as string))
				{
					touched = true;
				}
			}
			if (!touched)
			{
				return;
			}
			for (int i = 0; i < 5; i++)
			{
				string name = "BackgroundHistoryPath" + i;
				if (snap.History[i] == null)
				{
					hist.DeleteValue(name, throwOnMissingValue: false);
				}
				else
				{
					hist.SetValue(name, snap.History[i]!);
				}
			}
		}
		catch (Exception ex)
		{
			Logger.Log("Seasonal: wallpaper history restore skipped: " + ex.Message);
		}
	}

	private static string SpiWallpaper()
	{
		try
		{
			StringBuilder sb = new StringBuilder(1024);
			if (SystemParametersInfoGet(SPI_GETDESKWALLPAPER, (uint)sb.Capacity, sb, 0u))
			{
				return sb.ToString();
			}
		}
		catch
		{
		}
		return "";
	}

	private static int StyleToPosition(string? style, string? tile)
	{
		if (tile == "1")
		{
			return 1;
		}
		return style switch
		{
			"0" => 0,
			"2" => 2,
			"6" => 3,
			"10" => 4,
			"22" => 5,
			_ => 4,
		};
	}

	private static bool SamePath(string? a, string? b)
	{
		if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b))
		{
			return string.IsNullOrEmpty(a) && string.IsNullOrEmpty(b);
		}
		try
		{
			return string.Equals(System.IO.Path.GetFullPath(a), System.IO.Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);
		}
		catch
		{
			return string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
		}
	}

	private static void GuardWrites()
	{
		if (SettingsStore.ReadOnlyDiagnostics)
		{
			throw new InvalidOperationException("Desktop wallpaper changes are disabled in read-only diagnostics mode.");
		}
	}
}
