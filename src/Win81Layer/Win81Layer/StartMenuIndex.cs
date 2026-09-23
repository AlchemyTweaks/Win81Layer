using System;
using System.Collections.Generic;
using System.IO;

namespace Win81Layer;

public static class StartMenuIndex
{
	public sealed record Info(string Category, DateTime Installed);

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

	private static void Scan(string dir, string programsRoot, Dictionary<string, Info> map)
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
					map[name] = new Info(category, installed);
					continue;
				}
				string cat = (string.IsNullOrEmpty(cur.Category) ? category : cur.Category);
				DateTime dt = ((installed != DateTime.MinValue && installed < cur.Installed) ? installed : cur.Installed);
				map[name] = new Info(cat, dt);
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
