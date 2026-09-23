using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Win81Layer;

public static class JumpList
{
	public sealed record Item(string Path, string Name);

	private static readonly string AutoDir;

	public static List<Item> ReadByAppId(string appIdHash, int max = 10)
	{
		string file = Path.Combine(AutoDir, appIdHash + ".automaticDestinations-ms");
		return File.Exists(file) ? ReadFile(file, max) : new List<Item>();
	}

	public static List<Item> ReadFile(string path, int max = 10)
	{
		List<Item> items = new List<Item>();
		try
		{
			CompoundFile cf = CompoundFile.Load(File.ReadAllBytes(path));
			List<string> numeric = (from n in cf.StreamNames
				where n.All(Uri.IsHexDigit) && n.Length <= 8
				orderby Convert.ToInt64(n, 16) descending
				select n).ToList();
			HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
			foreach (string name in numeric)
			{
				if (items.Count >= max)
				{
					break;
				}
				string target = ShellLink.GetTargetPath(cf.ReadStream(name));
				if (!string.IsNullOrWhiteSpace(target) && seen.Add(target))
				{
					items.Add(new Item(target, Path.GetFileName(target)));
				}
			}
		}
		catch (Exception ex)
		{
			Logger.Log("JumpList parse failed for " + Path.GetFileName(path) + ": " + ex.Message);
		}
		return items;
	}

	static JumpList()
	{
		global::_003C_003Ey__InlineArray5<string> buffer = default(global::_003C_003Ey__InlineArray5<string>);
		buffer[0] = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
		buffer[1] = "Microsoft";
		buffer[2] = "Windows";
		buffer[3] = "Recent";
		buffer[4] = "AutomaticDestinations";
		AutoDir = Path.Combine(buffer);
	}
}
