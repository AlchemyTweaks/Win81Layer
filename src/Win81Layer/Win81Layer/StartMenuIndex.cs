using System;
using System.Collections.Generic;
using System.IO;

namespace Win81Layer;

public static class StartMenuIndex
{
	// LnkPath is the shortcut the entry was read from. When the same name exists at the root and in a folder, it always
	// sits in the folder that Category names.
	public sealed record Info(string Category, DateTime Installed)
	{
		public string? LnkPath { get; init; }
	}

	private static Dictionary<string, Info>? _cache;

	public static IReadOnlyDictionary<string, Info> Get()
	{
		return _cache ?? (_cache = Build());
	}

	public static void Invalidate()
	{
		_cache = null;
	}

	private static Dictionary<string, Info> Build()
	{
		Dictionary<string, Info> map = new Dictionary<string, Info>(StringComparer.OrdinalIgnoreCase);
		string[] roots = new string[2]
		{
			Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu),
			Environment.GetFolderPath(Environment.SpecialFolder.StartMenu)
		};
		string[] array = roots;
		foreach (string root in array)
		{
			string programs = Path.Combine(root, "Programs");
			if (Directory.Exists(programs))
			{
				Scan(programs, programs, map);
			}
		}
		return map;
	}

	internal static void Scan(string dir, string programsRoot, Dictionary<string, Info> map)
	{
		try
		{
			foreach (string lnk in Directory.EnumerateFiles(dir, "*.lnk"))
			{
				string name = Path.GetFileNameWithoutExtension(lnk);
				string rel = Path.GetRelativePath(programsRoot, dir);
				bool flag = ((rel == "." || (rel != null && rel.Length == 0)) ? true : false);
				string category = (flag ? "" : rel.Split(Path.DirectorySeparatorChar)[0]);
				DateTime installed;
				try
				{
					installed = File.GetCreationTime(lnk);
				}
				catch
				{
					installed = DateTime.MinValue;
				}
				if (!map.TryGetValue(name, out Info cur))
				{
					map[name] = new Info(category, installed)
					{
						LnkPath = lnk
					};
					continue;
				}
				string cat = (string.IsNullOrEmpty(cur.Category) ? category : cur.Category);
				DateTime dt = ((installed != DateTime.MinValue && installed < cur.Installed) ? installed : cur.Installed);
				// The shortcut follows the category: a root entry merged with a folder entry points into that folder.
				bool switchCat = string.IsNullOrEmpty(cur.Category) && !string.IsNullOrEmpty(category);
				map[name] = new Info(cat, dt)
				{
					LnkPath = (switchCat ? lnk : (cur.LnkPath ?? lnk))
				};
			}
			foreach (string sub in Directory.EnumerateDirectories(dir))
			{
				Scan(sub, programsRoot, map);
			}
		}
		catch
		{
		}
	}
}
