using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Win81Layer;

public static class Wallpapers
{
	public static string BundledDir => Path.Combine(AppContext.BaseDirectory, "Assets", "Wallpapers");

	public static string PresetsDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Win81Layer", "wallpapers");

	private static bool IsImage(string f)
	{
		switch (Path.GetExtension(f).ToLowerInvariant())
		{
		case ".png":
		case ".jpg":
		case ".jpeg":
		case ".bmp":
			return true;
		default:
			return false;
		}
	}

	// Preset selections used to persist the absolute path of whichever source, staging or release copy happened
	// to be running. Rebind only paths visibly owned by an Assets\Wallpapers bundle; user pictures stay untouched.
	public static string RebindBundledPath(string path)
	{
		if (string.IsNullOrWhiteSpace(path))
		{
			return path ?? "";
		}
		try
		{
			string full = Path.GetFullPath(path);
			DirectoryInfo parent = Directory.GetParent(full);
			if (parent == null || !parent.Name.Equals("Wallpapers", StringComparison.OrdinalIgnoreCase)
				|| parent.Parent == null || !parent.Parent.Name.Equals("Assets", StringComparison.OrdinalIgnoreCase))
			{
				return path;
			}
			string candidate = Path.GetFullPath(Path.Combine(BundledDir, Path.GetFileName(full)));
			return File.Exists(candidate) ? candidate : path;
		}
		catch
		{
			return path;
		}
	}

	public static void SeedFromPack()
	{
		try
		{
			Directory.CreateDirectory(PresetsDir);
			// The release already ships the complete preset pack. Avoid scanning Downloads and duplicating those files
			// into LocalAppData on every clean installation; Presets() reads the bundled directory directly.
			if (Directory.Exists(BundledDir) && Directory.EnumerateFiles(BundledDir).Any(IsImage))
			{
				return;
			}
			if (Directory.GetFiles(PresetsDir).Any(IsImage))
			{
				return;
			}
			string pack = FindPackFolder();
			if (pack == null)
			{
				Logger.Log("Wallpaper pack folder not found");
				return;
			}
			int n = 0;
			foreach (string f in Directory.EnumerateFiles(pack, "*.*", SearchOption.AllDirectories))
			{
				if (IsImage(f))
				{
					string dest = Path.Combine(PresetsDir, Path.GetFileName(f));
					if (!File.Exists(dest))
					{
						File.Copy(f, dest);
						n++;
					}
				}
			}
			Logger.Log($"Seeded {n} Start wallpaper presets from pack");
		}
		catch (Exception ex)
		{
			Logger.Log("Wallpaper seed failed: " + ex.Message);
		}
	}

	private static string? FindPackFolder()
	{
		string downloads = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
		string exact = Path.Combine(downloads, "Windows 8.X Start Screen Wallpaper Pack");
		if (Directory.Exists(exact))
		{
			return exact;
		}
		try
		{
			string[] directories = Directory.GetDirectories(downloads);
			foreach (string d in directories)
			{
				string name = Path.GetFileName(d);
				if (name.Contains("Wallpaper", StringComparison.OrdinalIgnoreCase) && (name.Contains("Start", StringComparison.OrdinalIgnoreCase) || name.Contains("8.", StringComparison.OrdinalIgnoreCase)))
				{
					return d;
				}
			}
		}
		catch
		{
		}
		return null;
	}

	public static List<string> Presets()
	{
		Dictionary<string, string> byName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
		Add(BundledDir);
		Add(PresetsDir);
		return byName.Values.OrderBy<string, string>((string f) => Path.GetFileName(f), StringComparer.OrdinalIgnoreCase).ToList();
		void Add(string dir)
		{
			try
			{
				if (Directory.Exists(dir))
				{
					string[] files = Directory.GetFiles(dir);
					foreach (string f in files)
					{
						if (IsImage(f))
						{
							byName.TryAdd(Path.GetFileName(f), f);
						}
					}
				}
			}
			catch
			{
			}
		}
	}
}
