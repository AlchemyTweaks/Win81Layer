using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Win81Layer;

// PORTABLE LAUNCHER PROFILE (directive §15-20): export the launcher's settings + Start layout as one versioned JSON bundle,
// and import it back (validate -> backup -> commit through the SAME DurableStateStore the launcher already uses). Machine-
// specific bits (wallpaper paths, monitor layout, exact exe paths) ride inside and degrade GRACEFULLY on import — Profile
// resolves apps by identity (placeholders for missing) and Workspace restore clamps absent monitors. Applies on next launch.
internal static class LauncherProfile
{
	private const int SchemaVersion = 1;

	private static readonly JsonSerializerOptions Pretty = new JsonSerializerOptions { WriteIndented = true };

	private static readonly JsonSerializerOptions ProfileOpts = new JsonSerializerOptions
	{
		WriteIndented = true,
		Converters = { new JsonStringEnumConverter() }
	};

	public sealed class Bundle
	{
		public int SchemaVersion { get; set; }

		public string LauncherVersion { get; set; } = "";

		public string CreatedAt { get; set; } = "";

		public string Settings { get; set; } = "{}";   // raw settings.json text

		public string Profile { get; set; } = "{}";     // raw profile.json text
	}

	private static string SettingsFile => SettingsStore.SettingsPath;

	private static string ProfileFile => Profile.ProfilePath;

	public static bool Export(string destPath, out string? error)
	{
		error = null;
		try
		{
			string settings = File.Exists(SettingsFile) ? File.ReadAllText(SettingsFile) : "{}";
			string profile = File.Exists(ProfileFile) ? File.ReadAllText(ProfileFile) : "{}";
			Bundle b = new Bundle
			{
				SchemaVersion = SchemaVersion,
				LauncherVersion = typeof(LauncherProfile).Assembly.GetName().Version?.ToString() ?? "",
				CreatedAt = DateTime.UtcNow.ToString("o"),
				Settings = settings,
				Profile = profile
			};
			File.WriteAllText(destPath, JsonSerializer.Serialize(b, Pretty));
			Logger.Log("[profile] exported to " + destPath);
			return true;
		}
		catch (Exception ex)
		{
			error = ex.Message;
			Logger.Log("[profile] export failed: " + ex.Message);
			return false;
		}
	}

	// Validate -> backup -> commit. Never touches live config until the bundle is confirmed well-formed (§19).
	public static bool Import(string srcPath, out string? error)
	{
		error = null;
		try
		{
			if (!File.Exists(srcPath))
			{
				error = "File not found.";
				return false;
			}
			Bundle? b;
			try { b = JsonSerializer.Deserialize<Bundle>(File.ReadAllText(srcPath), Pretty); }
			catch { error = "Not a valid launcher profile file."; return false; }
			if (b == null || b.SchemaVersion <= 0)
			{
				error = "Not a valid launcher profile.";
				return false;
			}
			if (b.SchemaVersion > SchemaVersion)
			{
				error = "This profile was made by a newer launcher version.";
				return false;
			}
			if (!IsValidSettings(b.Settings) || !IsValidProfile(b.Profile))
			{
				error = "Profile contents are malformed — import cancelled, current setup kept.";
				return false;
			}
			BackupCurrent();
			bool s = DurableStateStore.Commit("settings", SettingsFile, b.Settings, IsValidSettings).IsDurable;
			bool p = DurableStateStore.Commit("profile", ProfileFile, b.Profile, IsValidProfile).IsDurable;
			if (!s || !p)
			{
				error = "Could not write the imported configuration.";
				return false;
			}
			Logger.Log("[profile] imported from " + srcPath + " (applies on next launch)");
			return true;
		}
		catch (Exception ex)
		{
			error = ex.Message;
			Logger.Log("[profile] import failed: " + ex.Message);
			return false;
		}
	}

	// Snapshot current settings/profile next to them so a bad import (or the user) can roll back.
	private static void BackupCurrent()
	{
		try
		{
			string stamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss");
			if (File.Exists(SettingsFile)) { File.Copy(SettingsFile, SettingsFile + "." + stamp + ".bak", overwrite: true); }
			if (File.Exists(ProfileFile)) { File.Copy(ProfileFile, ProfileFile + "." + stamp + ".bak", overwrite: true); }
		}
		catch (Exception ex)
		{
			Logger.Log("[profile] backup warning: " + ex.Message);
		}
	}

	private static bool IsValidSettings(string json)
	{
		try { return JsonSerializer.Deserialize<AppSettings>(json, Pretty) != null; }
		catch { return false; }
	}

	private static bool IsValidProfile(string json)
	{
		try { return JsonSerializer.Deserialize<Profile.ProfileRecord>(json, ProfileOpts) != null; }
		catch { return false; }
	}
}
