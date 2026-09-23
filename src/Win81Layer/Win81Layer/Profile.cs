using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Win81Layer;

public static class Profile
{
	public sealed class TileRecord
	{
		public required string Path { get; set; }

		public string Name { get; set; } = string.Empty;

		public TileSize Size { get; set; } = TileSize.Medium;

		public LiveKind Live { get; set; } = LiveKind.None;

		public bool LiveOff { get; set; }

		public int Col { get; set; } = -1;

		public int Row { get; set; } = -1;

		public List<string> Members { get; set; } = new List<string>();
	}

	// One saved window position for the group's Workspace layout (physical pixels). Additive + optional -> old profiles
	// load fine (System.Text.Json default-inits the missing list; validation/health gates only count tiles).
	public sealed class WindowLayoutRecord
	{
		public string LaunchPath { get; set; } = string.Empty;   // stable relaunch key (exe | shell:AppsFolder\AUMID | .lnk)

		public string? AppId { get; set; }                        // AUMID, for packaged-app match/relaunch

		public string Name { get; set; } = string.Empty;

		public int X { get; set; }

		public int Y { get; set; }

		public int W { get; set; }

		public int H { get; set; }

		public string Monitor { get; set; } = string.Empty;       // Screen.DeviceName at capture (for absent-monitor clamp)

		public string State { get; set; } = "Normal";             // Normal | Maximized | Minimized
	}

	public sealed class GroupRecord
	{
		public string Name { get; set; } = string.Empty;

		public List<TileRecord> Tiles { get; set; } = new List<TileRecord>();

		public List<WindowLayoutRecord> SavedLayout { get; set; } = new List<WindowLayoutRecord>();
	}

	public sealed class ProfileRecord
	{
		public int Version { get; set; } = 1;

		public List<GroupRecord> Groups { get; set; } = new List<GroupRecord>();
	}

	public static readonly string ProfilePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Win81Layer", "profile.json");

	// Legacy one-time import only. Mutable recovery state now lives in DurableStateStore so every source/staged/live
	// executable sees the same profile. Never write user changes beside an executable again.
	public static readonly string GoldenProfilePath = Path.Combine(AppContext.BaseDirectory, "golden", "profile.json");

	private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
	{
		WriteIndented = true,
		Converters = { (JsonConverter)new JsonStringEnumConverter() }
	};

	private const int CurrentVersion = 5;

	private static bool _preReadWasDegraded;

	private static string _preReadSource = "missing";

	public static bool LoadWasDegraded { get; private set; }

	// >0 when the last load could not resolve some saved tiles against a still-incomplete inventory. Those entries
	// remain in the in-memory layout as placeholders, so a later user save preserves them instead of deleting pins.
	public static int LastDropped { get; private set; }

	// Background-thread-safe read with a GENEROUS retry: the cold-boot storm (AV/indexer) can lock profile.json well
	// beyond the UI thread's ~4s budget. Call this OFF the UI thread (the inventory task) and hand the result to
	// Load(inventory, json) so the on-UI materialize NEVER falls back to the DEFAULT layout just because the read
	// timed out — the #1 cause of "restart shows the default layout instead of my saved one". Returns null only if the
	// file is genuinely absent and no pending/last-good/legacy snapshot exists. DurableStateStore can immediately serve
	// a user-scoped pending or last-good snapshot; the EXE-local golden file is considered only as a one-time legacy seed.
	public static string ReadJsonWithRetry(int maxAttempts = 120)
	{
		DurableStateReadResult result = DurableStateStore.Read(
			"profile",
			ProfilePath,
			GoldenProfilePath,
			ValidateProfileJson,
			Math.Max(1, maxAttempts));
		_preReadWasDegraded = result.Json != null && !result.PrimaryHealthy;
		_preReadSource = result.Source;
		return result.Json;
	}

	// Materialize from a PRE-READ json string (no file I/O — safe + instant on the UI thread). preReadJson==null means
	// the off-UI read could not get the file: keep the real file, flag degraded, show defaults this session (self-heal
	// re-loads once free). This is the boot-critical path used by StartScreen.
	public static List<GroupVm> Load(IReadOnlyList<AppEntry> inventory, string preReadJson)
	{
		LoadWasDegraded = _preReadWasDegraded;
		LastDropped = 0;
		string json = preReadJson;
		string source = _preReadSource;
		if (json == null)
		{
			DurableStateReadResult fallback = DurableStateStore.Read("profile", ProfilePath, GoldenProfilePath, ValidateProfileJson, 4);
			json = fallback.Json;
			source = fallback.Source;
			LoadWasDegraded = json != null && !fallback.PrimaryHealthy;
		}
		if (json != null)
		{
			try
			{
				ProfileRecord record = JsonSerializer.Deserialize<ProfileRecord>(json, JsonOptions);
				if (record != null && record.Groups != null && record.Groups.Count > 0)
				{
					Logger.Log($"Profile state source: {source}");
					return MaterializeChecked(record, inventory);
				}
			}
			catch (Exception ex)
			{
				Logger.Log("Profile parse failed after durable validation; preserving stored files: " + ex.Message);
			}
			LoadWasDegraded = true;
			return CreateDefault(inventory);
		}
		// No valid primary, pending, stable recovery, or legacy seed: genuine first run.
		List<GroupVm> fresh = CreateDefault(inventory);
		Save(fresh);
		return fresh;
	}

	public static List<GroupVm> Load(IReadOnlyList<AppEntry> inventory)
	{
		DurableStateReadResult result = DurableStateStore.Read("profile", ProfilePath, GoldenProfilePath, ValidateProfileJson, 80);
		LoadWasDegraded = result.Json != null && !result.PrimaryHealthy;
		LastDropped = 0;
		if (result.Json != null)
		{
			try
			{
				ProfileRecord record = JsonSerializer.Deserialize<ProfileRecord>(result.Json, JsonOptions);
				if (record != null && record.Groups != null && record.Groups.Count > 0)
				{
					Logger.Log($"Profile state source: {result.Source}");
					return MaterializeChecked(record, inventory);
				}
			}
			catch (Exception ex)
			{
				Logger.Log("Profile parse failed after durable validation; preserving stored files: " + ex.Message);
				LoadWasDegraded = true;
				return CreateDefault(inventory);
			}
		}
		List<GroupVm> groups2 = CreateDefault(inventory);
		Save(groups2);
		return groups2;
	}

	private static List<GroupVm> Materialize(ProfileRecord record, IReadOnlyList<AppEntry> inventory)
	{
		Dictionary<string, AppEntry> byPath = new Dictionary<string, AppEntry>(StringComparer.OrdinalIgnoreCase);
		Dictionary<string, AppEntry> byAppId = new Dictionary<string, AppEntry>(StringComparer.OrdinalIgnoreCase);
		Dictionary<string, AppEntry> byName = new Dictionary<string, AppEntry>(StringComparer.CurrentCultureIgnoreCase);
		foreach (AppEntry a in inventory)
		{
			if (!string.IsNullOrWhiteSpace(a.LaunchPath))
			{
				byPath.TryAdd(a.LaunchPath, a);
				string aumid = ExtractAppsFolderId(a.LaunchPath);
				if (!string.IsNullOrWhiteSpace(aumid))
				{
					byAppId.TryAdd(aumid, a);
				}
			}
			if (!string.IsNullOrWhiteSpace(a.AppId))
			{
				byAppId.TryAdd(a.AppId, a);
			}
			byName.TryAdd(a.Name, a);
		}
		bool migrated = false;
		int dropped = 0;
		bool hasCoords = record.Version >= 2;
		int coordScale = ((record.Version != 2) ? 1 : 2);
		List<GroupVm> groups = new List<GroupVm>();
		foreach (GroupRecord g in record.Groups)
		{
			GroupVm vm = new GroupVm
			{
				Name = g.Name
			};
			vm.SavedLayout = g.SavedLayout ?? new List<WindowLayoutRecord>();
			foreach (TileRecord t in g.Tiles)
			{
				int col = ((hasCoords && t.Col >= 0) ? (t.Col * coordScale) : (-1));
				int row = ((hasCoords && t.Row >= 0) ? (t.Row * coordScale) : (-1));
				if (t.Live == LiveKind.Folder)
				{
					object obj;
					if (!t.Path.StartsWith("folder:", StringComparison.Ordinal))
					{
						obj = "Apps";
					}
					else
					{
						string path = t.Path;
						int length = "folder:".Length;
						obj = path.Substring(length, path.Length - length);
					}
					string fname = (string)obj;
					List<AppEntry> members = new List<AppEntry>();
					foreach (string mp in t.Members)
					{
						AppEntry me = ResolveSavedEntry(mp, string.Empty, byPath, byAppId, byName, out bool memberMigrated);
						if (me == null)
						{
							me = CreatePlaceholder(mp, string.Empty);
							dropped++;
						}
						migrated |= memberMigrated;
						members.Add(me);
					}
					if (members.Count != 0)
					{
						TileVm folder = FolderTiles.MakeFolder(fname, members, t.Size);
						folder.Col = col;
						folder.Row = row;
						vm.Tiles.Add(folder);
					}
					continue;
				}
				if (t.Live != LiveKind.None)
				{
					TileVm live = LiveTiles.Create(t.Live);
					live.Size = t.Size;
					live.LiveOff = t.LiveOff;
					live.Col = col;
					live.Row = row;
					vm.Tiles.Add(live);
					continue;
				}
				AppEntry entry = ResolveSavedEntry(t.Path, t.Name, byPath, byAppId, byName, out bool entryMigrated);
				if (entry == null)
				{
					entry = CreatePlaceholder(t.Path, t.Name);
					dropped++;
				}
				migrated |= entryMigrated;
				vm.Tiles.Add(new TileVm
				{
					Entry = entry,
					Size = t.Size,
					Col = col,
					Row = row
				});
			}
			if (vm.Tiles.Count > 0)
			{
				groups.Add(vm);
			}
		}
		if (record.Version < 4 && groups.Count > 0 && !groups.Any((GroupVm groupVm) => groupVm.Tiles.Any((TileVm tileVm) => tileVm.IsFolder)))
		{
			SeedDemoFolder(groups[0], inventory);
		}
		LastDropped = dropped;
		if (dropped > 0)
		{
			Logger.Log($"Profile: {dropped} saved item(s) unresolved; preserved as launchable placeholders instead of dropping them");
		}
		else
		{
			if (record.Version < CurrentVersion)
			{
				Logger.Log($"Profile upgraded v{record.Version}→v{CurrentVersion}");
				Save(groups);
			}
			if (migrated)
			{
				Logger.Log("Profile migrated to new launch paths");
				Save(groups);
			}
		}
		return groups;
	}

	private static AppEntry? ResolveSavedEntry(
		string path,
		string savedName,
		IReadOnlyDictionary<string, AppEntry> byPath,
		IReadOnlyDictionary<string, AppEntry> byAppId,
		IReadOnlyDictionary<string, AppEntry> byName,
		out bool migrated)
	{
		migrated = false;
		if (!string.IsNullOrWhiteSpace(path) && byPath.TryGetValue(path, out AppEntry exact))
		{
			return exact;
		}
		string appId = ExtractAppsFolderId(path);
		if (!string.IsNullOrWhiteSpace(appId) && byAppId.TryGetValue(appId, out AppEntry byId))
		{
			migrated = !string.Equals(path, byId.LaunchPath, StringComparison.OrdinalIgnoreCase);
			return byId;
		}
		if (!string.IsNullOrWhiteSpace(savedName) && byName.TryGetValue(savedName, out AppEntry named))
		{
			migrated = !string.Equals(path, named.LaunchPath, StringComparison.OrdinalIgnoreCase);
			return named;
		}
		string fallbackName = FriendlyName(path);
		if (!string.IsNullOrWhiteSpace(fallbackName) && byName.TryGetValue(fallbackName, out named))
		{
			migrated = !string.Equals(path, named.LaunchPath, StringComparison.OrdinalIgnoreCase);
			return named;
		}
		return null;
	}

	private static AppEntry CreatePlaceholder(string path, string savedName)
	{
		return new AppEntry
		{
			Name = string.IsNullOrWhiteSpace(savedName) ? FriendlyName(path) : savedName,
			LaunchPath = path ?? string.Empty,
			TileBrush = System.Windows.Media.Brushes.Transparent
		};
	}

	private static string ExtractAppsFolderId(string? path)
	{
		const string prefix = "shell:AppsFolder\\";
		return !string.IsNullOrWhiteSpace(path) && path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
			? path.Substring(prefix.Length)
			: string.Empty;
	}

	private static string FriendlyName(string? path)
	{
		if (string.IsNullOrWhiteSpace(path))
		{
			return "Unavailable app";
		}
		string candidate = ExtractAppsFolderId(path);
		if (string.IsNullOrWhiteSpace(candidate))
		{
			candidate = path;
		}
		try
		{
			string fileName = Path.GetFileNameWithoutExtension(candidate);
			if (!string.IsNullOrWhiteSpace(fileName))
			{
				candidate = fileName;
			}
		}
		catch
		{
		}
		int bang = candidate.IndexOf('!');
		if (bang > 0)
		{
			candidate = candidate.Substring(0, bang);
		}
		return string.IsNullOrWhiteSpace(candidate) ? "Unavailable app" : candidate;
	}

	// Saved-but-currently-unresolved apps are materialized as placeholders, so inventory timing can never remove them.
	private static List<GroupVm> MaterializeChecked(ProfileRecord record, IReadOnlyList<AppEntry> inventory)
	{
		int savedTiles = record.Groups.Sum((GroupRecord g) => g.Tiles?.Count ?? 0);
		List<GroupVm> mats = Materialize(record, inventory);
		int gotTiles = mats.Sum((GroupVm g) => g.Tiles.Count);
		bool healthy = gotTiles > 0 && gotTiles >= savedTiles * 6 / 10;
		if (healthy)
		{
			return mats;
		}
		Logger.Log($"Profile materialize degraded (got {gotTiles}/{savedTiles}); using stable recovery/default without overwriting stored state");
		LoadWasDegraded = true;
		return FallbackLayout(inventory);
	}

	private static List<GroupVm> FallbackLayout(IReadOnlyList<AppEntry> inventory)
	{
		try
		{
			DurableStateReadResult result = DurableStateStore.Read("profile", ProfilePath, GoldenProfilePath, ValidateProfileJson, 2);
			if (result.Json != null)
			{
				ProfileRecord rec = JsonSerializer.Deserialize<ProfileRecord>(result.Json, JsonOptions);
				if (rec != null && rec.Groups != null && rec.Groups.Count > 0)
				{
					List<GroupVm> mats = Materialize(rec, inventory);
					if (mats.Sum((GroupVm g) => g.Tiles.Count) > 0)
					{
						return mats;
					}
				}
			}
		}
		catch (Exception ex)
		{
			Logger.Log("Stable profile fallback failed: " + ex.Message);
		}
		return CreateDefault(inventory);
	}

	public static void Save(IReadOnlyList<GroupVm> groups)
	{
		try
		{
			ProfileRecord record = new ProfileRecord
			{
				Version = CurrentVersion,
				Groups = groups.Select((GroupVm g) => new GroupRecord
				{
					Name = g.Name,
					SavedLayout = g.SavedLayout ?? new List<WindowLayoutRecord>(),
					Tiles = g.Tiles.Select((TileVm t) => new TileRecord
					{
						Path = (t.IsFolder ? ("folder:" + t.Entry.Name) : (t.IsLive ? $"live:{t.Live}" : t.Entry.LaunchPath)),
						Name = t.Entry?.Name ?? string.Empty,
						Size = t.Size,
						Live = t.Live,
						LiveOff = t.LiveOff,
						Col = t.Col,
						Row = t.Row,
						Members = (t.IsFolder ? t.Members.Select((AppEntry m) => m.LaunchPath).ToList() : new List<string>())
					}).ToList()
				}).ToList()
			};
			string json = JsonSerializer.Serialize(record, JsonOptions);
			DurableStateCommitResult committed = DurableStateStore.Commit("profile", ProfilePath, json, ValidateProfileJson);
			if (!committed.IsDurable)
			{
				Logger.Log("Profile save FAILED: neither primary nor pending journal accepted the state");
				return;
			}
			LoadWasDegraded = false;
			LastDropped = 0;
			Logger.Log($"Profile saved durably: {record.Groups.Count} groups, {record.Groups.Sum((GroupRecord g) => g.Tiles.Count)} tiles; primary={(committed.PrimaryCommitted ? "committed" : "queued")}; path={ProfilePath}");
		}
		catch (Exception ex)
		{
			Logger.Log("Profile save failed: " + ex.Message);
		}
	}

	internal static bool ImportJson(string json, string reason)
	{
		try
		{
			if (!ValidateProfileJson(json))
			{
				Logger.Log("Profile import rejected: invalid JSON structure");
				return false;
			}
			ProfileRecord record = JsonSerializer.Deserialize<ProfileRecord>(json, JsonOptions);
			record.Version = CurrentVersion;
			string normalized = JsonSerializer.Serialize(record, JsonOptions);
			DurableStateCommitResult committed = DurableStateStore.Commit("profile", ProfilePath, normalized, ValidateProfileJson);
			if (!committed.IsDurable)
			{
				Logger.Log("Profile import failed: no durable destination");
				return false;
			}
			Logger.Log($"Profile imported durably ({reason}); groups={record.Groups.Count}, tiles={record.Groups.Sum((GroupRecord g) => g.Tiles.Count)}, primary={(committed.PrimaryCommitted ? "committed" : "queued")}");
			return true;
		}
		catch (Exception ex)
		{
			Logger.Log("Profile import failed: " + ex.Message);
			return false;
		}
	}

	private static bool ValidateProfileJson(string json)
	{
		try
		{
			ProfileRecord rec = JsonSerializer.Deserialize<ProfileRecord>(json, JsonOptions);
			return rec?.Groups != null
				&& rec.Groups.Count > 0
				&& rec.Groups.All((GroupRecord g) => g != null && g.Tiles != null)
				&& rec.Groups.SelectMany((GroupRecord g) => g.Tiles).All((TileRecord t) => t != null && !string.IsNullOrWhiteSpace(t.Path));
		}
		catch
		{
			return false;
		}
	}

	private static void SeedDemoFolder(GroupVm group, IReadOnlyList<AppEntry> inventory)
	{
		string[] wanted = new string[7] { "notepad", "calculator", "paint", "snipping", "control panel", "task manager", "character map" };
		List<AppEntry> members = new List<AppEntry>();
		string[] array = wanted;
		foreach (string w in array)
		{
			AppEntry e = inventory.FirstOrDefault((AppEntry a) => a.Name.Contains(w, StringComparison.CurrentCultureIgnoreCase));
			if (e != null && !members.Contains(e))
			{
				members.Add(e);
			}
			if (members.Count >= 4)
			{
				break;
			}
		}
		if (members.Count >= 2)
		{
			group.Tiles.Add(FolderTiles.MakeFolder("Tools", members));
		}
	}

	private static List<GroupVm> CreateDefault(IReadOnlyList<AppEntry> inventory)
	{
		string[] wideFragments = new string[5] { "chrome", "firefox", "edge", "spotify", "steam" };
		string[] productivity = new string[14]
		{
			"chrome", "firefox", "edge", "word", "excel", "powerpoint", "outlook", "notepad", "calculator", "paint",
			"visual studio code", "onenote", "file explorer", "snipping"
		};
		string[] entertainment = new string[11]
		{
			"spotify", "steam", "discord", "vlc", "obs", "whatsapp", "viber", "winamp", "windows media player", "photos",
			"qbittorrent"
		};
		HashSet<AppEntry> used = new HashSet<AppEntry>();
		List<GroupVm> groups = new List<GroupVm>();
		GroupVm g1 = new GroupVm
		{
			Name = "Productivity"
		};
		foreach (TileVm t in Match(productivity, 12))
		{
			g1.Tiles.Add(t);
		}
		GroupVm g2 = new GroupVm
		{
			Name = "Entertainment"
		};
		foreach (TileVm t2 in Match(entertainment, 9))
		{
			g2.Tiles.Add(t2);
		}
		if (g1.Tiles.Count > 0)
		{
			groups.Add(g1);
		}
		if (g2.Tiles.Count > 0)
		{
			groups.Add(g2);
		}
		if (groups.Count == 0)
		{
			GroupVm fallback = new GroupVm();
			foreach (AppEntry entry in inventory.Take(12))
			{
				fallback.Tiles.Add(new TileVm
				{
					Entry = entry
				});
			}
			groups.Add(fallback);
		}
		return groups;
		List<TileVm> Match(string[] fragments, int cap)
		{
			List<TileVm> tiles = new List<TileVm>();
			foreach (string fragment in fragments)
			{
				AppEntry entry2 = inventory.FirstOrDefault((AppEntry a) => !used.Contains(a) && a.Name.Contains(fragment, StringComparison.CurrentCultureIgnoreCase));
				if (entry2 != null)
				{
					used.Add(entry2);
					TileSize size = ((!wideFragments.Any((string w) => entry2.Name.Contains(w, StringComparison.CurrentCultureIgnoreCase))) ? TileSize.Medium : TileSize.Wide);
					tiles.Add(new TileVm
					{
						Entry = entry2,
						Size = size
					});
					if (tiles.Count >= cap)
					{
						break;
					}
				}
			}
			return tiles;
		}
	}
}
