#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace Win81Layer;

public static class SettingsStore
{
	public static readonly string SettingsPath = Path.Combine(
		Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
		"Win81Layer",
		"settings.json");

	// Legacy one-time import only. Mutable settings recovery is user-scoped in DurableStateStore.
	public static readonly string GoldenSettingsPath = Path.Combine(AppContext.BaseDirectory, "golden", "settings.json");

	private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
	{
		WriteIndented = true
	};

	private static readonly object Gate = new object();

	private static AppSettings? _cache;

	private static DateTime _cacheStampUtc;

	private static long _lastDegradedProbeMs;

	private static long _lastHealthyProbeMs;

	public static bool LoadWasDegraded { get; private set; }

	// Mutex-free render/diagnostic processes must never migrate or commit user-owned settings from a staging path.
	// Deliberately fail an accidental write so QA catches it instead of silently masking the mutation.
	internal static bool ReadOnlyDiagnostics { get; set; }

	// Never expose the mutable cache. Every caller receives a snapshot and persists changes through Save/Update.
	public static AppSettings Current => Load();

	// Input-path consumers such as context menus must not probe the filesystem just to paint current check marks.
	// The cache is refreshed by every durable Load/Save and shell-profile signal; fall back to Load only at startup.
	public static AppSettings FastSnapshot
	{
		get
		{
			lock (Gate)
			{
				return _cache?.Clone() ?? LoadCore(force: false, 8, out _);
			}
		}
	}

	public static void PrimeCacheOffThread(int maxAttempts = 120)
	{
		lock (Gate)
		{
			LoadCore(force: true, Math.Max(1, maxAttempts), out string source);
			Logger.Log($"Settings primed off-UI from {source}; taskbar/start consumers share the durable snapshot.");
		}
	}

	public static AppSettings Load()
	{
		lock (Gate)
		{
			return LoadCore(force: false, 8, out _);
		}
	}

	// Bypass the in-memory cache entirely and re-read from the durable store (many attempts). Used by the dominance
	// backstop so a poisoned early cache can never keep native suppression / composition from being re-asserted.
	public static AppSettings LoadForced()
	{
		lock (Gate)
		{
			AppSettings s = LoadCore(force: true, 24, out string source);
			Logger.Log($"LoadForced: source={source}; mode={s.DesktopCompositionMode}, suspend={s.SuspendNativeStart}, taskbar={s.TaskbarEnabled}; path={SettingsPath}");
			return s;
		}
	}

	public static void Update(Action<AppSettings> mutate)
	{
		if (mutate == null)
		{
			throw new ArgumentNullException(nameof(mutate));
		}
		if (ReadOnlyDiagnostics)
		{
			throw new InvalidOperationException("Settings writes are disabled in read-only diagnostics mode.");
		}
		lock (Gate)
		{
			AppSettings settings = LoadCore(force: false, 8, out _);
			mutate(settings);
			SaveCore(settings);
		}
	}

	public static void Save(AppSettings settings)
	{
		if (settings == null)
		{
			throw new ArgumentNullException(nameof(settings));
		}
		if (ReadOnlyDiagnostics)
		{
			throw new InvalidOperationException("Settings writes are disabled in read-only diagnostics mode.");
		}
		lock (Gate)
		{
			SaveCore(settings);
		}
	}

	// Transactional callers need to know whether the new value reached either the verified primary file or the
	// durable pending journal. Save remains source-compatible for existing UI call sites.
	public static bool TrySave(AppSettings settings)
	{
		if (settings == null)
		{
			throw new ArgumentNullException(nameof(settings));
		}
		if (ReadOnlyDiagnostics)
		{
			throw new InvalidOperationException("Settings writes are disabled in read-only diagnostics mode.");
		}
		lock (Gate)
		{
			return SaveCore(settings);
		}
	}

	private static AppSettings LoadCore(bool force, int attempts, out string source)
	{
		// TTL fast path: re-serve a healthy cache for up to 500ms WITHOUT the multi-syscall on-disk stamp probe
		// below. Steady-state hot readers (taskbar 2s timer, per-keystroke search, per-brush accent) hit this
		// instead of ~5 File.Exists/GetLastWriteTime calls each. In-process Save refreshes _cache immediately, and
		// the ShellProfileChangeSignal path uses LoadForced(), so only rare out-of-band file edits wait <=500ms.
		if (!force && _cache != null && !LoadWasDegraded && Environment.TickCount64 - _lastHealthyProbeMs < 500L)
		{
			source = "cache-ttl";
			return _cache.Clone();
		}
		DateTime stateStamp = DurableStateStore.GetStateStampUtc("settings", SettingsPath);
		// Fast path serves the cache ONLY when it is healthy. A degraded/defaults cache (e.g. an early cold-boot read
		// that lost a file-lock race and fell back to native defaults) must NOT be pinned here: the on-disk stamp
		// never changes on its own, so without this guard the poisoned cache would be returned forever and dominance
		// (SuspendNativeStart / windows81 composition) would silently never apply. Skipping the fast path lets the
		// throttled re-read below (and the full read after it) pick up the real settings once the file is readable.
		if (!force && _cache != null && !LoadWasDegraded && stateStamp == _cacheStampUtc)
		{
			_lastHealthyProbeMs = Environment.TickCount64;
			source = "cache";
			return _cache.Clone();
		}
		if (!force && _cache != null && LoadWasDegraded && Environment.TickCount64 - _lastDegradedProbeMs < 2000L)
		{
			source = "degraded-cache";
			return _cache.Clone();
		}

		DurableStateReadResult result = DurableStateStore.Read(
			"settings",
			SettingsPath,
			GoldenSettingsPath,
			ValidateSettingsJson,
			attempts,
			repair: !ReadOnlyDiagnostics);
		source = result.Source;
		if (result.Json != null)
		{
			try
			{
				AppSettings? parsed = JsonSerializer.Deserialize<AppSettings>(result.Json, JsonOptions);
				if (parsed != null)
				{
					string originalWallpaperPath = parsed.StartBgCustomPath;
					Normalize(parsed, rebindBundledPath: !ReadOnlyDiagnostics);
					if (!ReadOnlyDiagnostics && !string.Equals(originalWallpaperPath, parsed.StartBgCustomPath, StringComparison.OrdinalIgnoreCase))
					{
						string migratedJson = JsonSerializer.Serialize(parsed, JsonOptions);
						DurableStateCommitResult migrated = DurableStateStore.Commit("settings", SettingsPath, migratedJson, ValidateSettingsJson);
						if (migrated.IsDurable)
						{
							source += "+bundled-wallpaper-rebound";
							Logger.Log($"Settings migration: bundled wallpaper rebound to current payload: {parsed.StartBgCustomPath}");
						}
						else
						{
							Logger.Log("Settings migration: bundled wallpaper rebind could not be persisted; using rebound path for this process.");
						}
					}
					_cache = parsed.Clone();
					_cacheStampUtc = DurableStateStore.GetStateStampUtc("settings", SettingsPath);
					LoadWasDegraded = !result.PrimaryHealthy;
					if (LoadWasDegraded)
					{
						_lastDegradedProbeMs = Environment.TickCount64;
					}
					return parsed.Clone();
				}
			}
			catch (Exception ex)
			{
				Logger.Log("Settings parse failed after durable validation: " + ex.Message);
			}
		}

		if (_cache != null)
		{
			LoadWasDegraded = true;
			_lastDegradedProbeMs = Environment.TickCount64;
			source = "cache-after-read-failure";
			return _cache.Clone();
		}

		AppSettings defaults = new AppSettings();
		Normalize(defaults, rebindBundledPath: !ReadOnlyDiagnostics);
		_cache = defaults.Clone();
		_cacheStampUtc = stateStamp;
		LoadWasDegraded = File.Exists(SettingsPath);
		source = LoadWasDegraded ? "defaults-degraded" : "defaults-first-run";
		return defaults;
	}

	private static bool SaveCore(AppSettings settings)
	{
		Normalize(settings, rebindBundledPath: true);
		string json = JsonSerializer.Serialize(settings, JsonOptions);
		DurableStateCommitResult committed = DurableStateStore.Commit("settings", SettingsPath, json, ValidateSettingsJson);
		if (!committed.IsDurable)
		{
			Logger.Log("Settings save FAILED: neither primary nor pending journal accepted the state");
			return false;
		}
		_cache = settings.Clone();
		_cacheStampUtc = DurableStateStore.GetStateStampUtc("settings", SettingsPath);
		LoadWasDegraded = false;
		Logger.Log($"Settings saved durably: DesktopCompositionMode={settings.DesktopCompositionMode}, TaskbarEnabled={settings.TaskbarEnabled}, StartBgMode={settings.StartBgMode}; primary={(committed.PrimaryCommitted ? "committed" : "queued")}; path={SettingsPath}");
		return true;
	}

	private static bool ValidateSettingsJson(string json)
	{
		try
		{
			return JsonSerializer.Deserialize<AppSettings>(json, JsonOptions) != null;
		}
		catch
		{
			return false;
		}
	}

	private static void Normalize(AppSettings settings, bool rebindBundledPath)
	{
		settings.TrayOrder ??= new List<string>();
		settings.TrayForceShown ??= new List<string>();
		settings.TrayForceHidden ??= new List<string>();
		settings.CharmQuickOrder ??= new List<string>();
		settings.CharmQuickHidden ??= new List<string>();
		if (rebindBundledPath)
		{
			settings.StartBgCustomPath = Wallpapers.RebindBundledPath(settings.StartBgCustomPath);
		}
		settings.DesktopCompositionMode = string.IsNullOrWhiteSpace(settings.DesktopCompositionMode) ? "native" : settings.DesktopCompositionMode;
		settings.TaskbarSize = string.IsNullOrWhiteSpace(settings.TaskbarSize) ? "Medium" : settings.TaskbarSize;
		settings.TaskbarPosition = string.IsNullOrWhiteSpace(settings.TaskbarPosition) ? "Bottom" : settings.TaskbarPosition;
		settings.TaskbarAlignment = string.IsNullOrWhiteSpace(settings.TaskbarAlignment) ? "Left" : settings.TaskbarAlignment;
		settings.TaskbarCombine = string.IsNullOrWhiteSpace(settings.TaskbarCombine) ? "Always" : settings.TaskbarCombine;
		settings.TaskbarColorMode = string.IsNullOrWhiteSpace(settings.TaskbarColorMode) ? "Wallpaper" : settings.TaskbarColorMode;
		settings.ActionCenterPosition = (settings.ActionCenterPosition == "Left") ? "Left" : "Right";
		settings.WeatherCity = string.IsNullOrWhiteSpace(settings.WeatherCity) ? "Kalamata" : settings.WeatherCity.Trim();
		settings.WeatherUnits = string.Equals(settings.WeatherUnits, "F", StringComparison.OrdinalIgnoreCase) ? "F" : "C";
		settings.NewsFeedUrl = NewsFeedService.NormalizeUrl(settings.NewsFeedUrl) ?? NewsFeedService.DefaultUrl;
		settings.PlaceSearchEngine = string.Equals(settings.PlaceSearchEngine, "Google", StringComparison.OrdinalIgnoreCase) ? "Google" : "Bing";
	}
}
