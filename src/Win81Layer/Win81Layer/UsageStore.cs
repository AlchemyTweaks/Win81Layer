using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace Win81Layer;

public static class UsageStore
{
	public sealed class Record
	{
		public int Count { get; set; }

		public long FirstSeenTicks { get; set; }

		public long LastUsedTicks { get; set; }
	}

	public sealed class Data
	{
		public long BaselineTicks { get; set; }

		public Dictionary<string, Record> Apps { get; set; } = new Dictionary<string, Record>(StringComparer.OrdinalIgnoreCase);
	}

	private static readonly string Path_ = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Win81Layer", "usage.json");

	private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
	{
		WriteIndented = true
	};

	private static Data _data = new Data();

	private static readonly object _gate = new object();

	private static Dictionary<string, Record> _map => _data.Apps;

	public static void Load()
	{
		lock (_gate)
		{
			try
			{
				if (File.Exists(Path_))
				{
					_data = JsonSerializer.Deserialize<Data>(File.ReadAllText(Path_), JsonOptions) ?? new Data();
				}
			}
			catch (Exception ex)
			{
				Logger.Log("Usage load failed: " + ex.Message);
			}
			if (_data.BaselineTicks == 0)
			{
				_data.BaselineTicks = DateTime.UtcNow.Ticks;
				Save();
			}
		}
	}

	private static void Save()
	{
		try
		{
			Directory.CreateDirectory(Path.GetDirectoryName(Path_));
			// atomic: write a tmp then move-overwrite (with a short retry for transient AV/indexer locks) so a
			// crash mid-write can't corrupt usage.json (mirrors SettingsStore)
			string tmp = Path_ + ".tmp";
			File.WriteAllText(tmp, JsonSerializer.Serialize(_data, JsonOptions));
			for (int i = 0; ; i++)
			{
				try
				{
					File.Move(tmp, Path_, overwrite: true);
					break;
				}
				catch when (i < 8)
				{
					System.Threading.Thread.Sleep(15);
				}
			}
		}
		catch (Exception ex)
		{
			Logger.Log("Usage save failed: " + ex.Message);
		}
	}

	public static bool IsNew(string path)
	{
		lock (_gate)
		{
			if (!_map.TryGetValue(path, out Record r) || r.Count > 0)
			{
				return false;
			}
			long grace = _data.BaselineTicks + TimeSpan.FromMinutes(2L).Ticks;
			long age = DateTime.UtcNow.Ticks - r.FirstSeenTicks;
			return r.FirstSeenTicks > grace && age < TimeSpan.FromDays(14).Ticks;
		}
	}

	public static void RegisterInventory(IEnumerable<AppEntry> apps, long nowTicks)
	{
		lock (_gate)
		{
			bool changed = false;
			foreach (AppEntry a in apps)
			{
				if (!_map.ContainsKey(a.LaunchPath))
				{
					_map[a.LaunchPath] = new Record
					{
						FirstSeenTicks = nowTicks
					};
					changed = true;
				}
			}
			if (changed)
			{
				Save();
			}
		}
	}

	public static void RecordLaunch(string path, long nowTicks)
	{
		lock (_gate)
		{
			if (!_map.TryGetValue(path, out Record r))
			{
				r = new Record
				{
					FirstSeenTicks = nowTicks
				};
				_map[path] = r;
			}
			r.Count++;
			r.LastUsedTicks = nowTicks;
			Save();
		}
	}

	public static int Count(string path)
	{
		lock (_gate)
		{
			Record r;
			return _map.TryGetValue(path, out r) ? r.Count : 0;
		}
	}

	public static long FirstSeen(string path)
	{
		lock (_gate)
		{
			Record r;
			return _map.TryGetValue(path, out r) ? r.FirstSeenTicks : 0;
		}
	}

	public static long LastUsed(string path)
	{
		lock (_gate)
		{
			Record r;
			return _map.TryGetValue(path, out r) ? r.LastUsedTicks : 0;
		}
	}
}
