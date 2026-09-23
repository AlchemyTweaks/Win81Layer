using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Win32;

namespace Win81Layer;

public static class WallpaperModule
{
	private sealed class Snapshot
	{
		public string? WallPaper { get; set; }

		public string? Style { get; set; }

		public string? Tile { get; set; }
	}

	private const uint SPI_SETDESKWALLPAPER = 20u;

	private const uint SPIF_UPDATEINIFILE = 1u;

	private const uint SPIF_SENDCHANGE = 2u;

	private static readonly string AssetPath;

	private static readonly string SnapshotPath;

	public static bool AssetAvailable => File.Exists(AssetPath);

	public static bool Apply()
	{
		if (!AssetAvailable)
		{
			Logger.Log("Wallpaper asset missing; cannot apply");
			return false;
		}
		try
		{
			if (!File.Exists(SnapshotPath))
			{
				using RegistryKey key = Registry.CurrentUser.OpenSubKey("Control Panel\\Desktop");
				Snapshot snapshot = new Snapshot();
				snapshot.WallPaper = key?.GetValue("WallPaper") as string;
				snapshot.Style = key?.GetValue("WallpaperStyle") as string;
				snapshot.Tile = key?.GetValue("TileWallpaper") as string;
				Snapshot snap = snapshot;
				Directory.CreateDirectory(Path.GetDirectoryName(SnapshotPath));
				File.WriteAllText(SnapshotPath, JsonSerializer.Serialize(snap));
			}
			// Force Fill so the 1920x1200 image covers any screen (a mismatched style can leave black borders).
			using (RegistryKey style = Registry.CurrentUser.OpenSubKey("Control Panel\\Desktop", writable: true))
			{
				if (style != null)
				{
					style.SetValue("WallpaperStyle", "10");   // 10 = Fill
					style.SetValue("TileWallpaper", "0");
				}
			}
			SystemParametersInfo(20u, 0u, AssetPath, 3u);
			Logger.Log("Win8.1 wallpaper applied (snapshot saved)");
			return true;
		}
		catch (Exception ex)
		{
			Logger.Log("Wallpaper apply failed: " + ex.Message);
			return false;
		}
	}

	public static bool Revert()
	{
		try
		{
			if (!File.Exists(SnapshotPath))
			{
				Logger.Log("No wallpaper snapshot to revert");
				return false;
			}
			Snapshot snap = JsonSerializer.Deserialize<Snapshot>(File.ReadAllText(SnapshotPath));
			if (snap == null)
			{
				return false;
			}
			using (RegistryKey key = Registry.CurrentUser.OpenSubKey("Control Panel\\Desktop", writable: true))
			{
				if (key != null)
				{
					if (snap.Style != null)
					{
						key.SetValue("WallpaperStyle", snap.Style);
					}
					if (snap.Tile != null)
					{
						key.SetValue("TileWallpaper", snap.Tile);
					}
				}
			}
			SystemParametersInfo(20u, 0u, snap.WallPaper ?? string.Empty, 3u);
			File.Delete(SnapshotPath);
			Logger.Log("Wallpaper reverted to snapshot");
			return true;
		}
		catch (Exception ex)
		{
			Logger.Log("Wallpaper revert failed: " + ex.Message);
			return false;
		}
	}

	[DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
	private static extern bool SystemParametersInfo(uint uiAction, uint uiParam, string pvParam, uint fWinIni);

	static WallpaperModule()
	{
		global::_003C_003Ey__InlineArray6<string> buffer = default(global::_003C_003Ey__InlineArray6<string>);
		buffer[0] = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
		buffer[1] = "Win81Layer";
		buffer[2] = "assets";
		buffer[3] = "win81";
		buffer[4] = "wallpaper";
		buffer[5] = "img0.jpg";
		AssetPath = Path.Combine(buffer);
		SnapshotPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Win81Layer", "snapshots", "wallpaper.json");
	}
}
