using System;
using System.Diagnostics;

namespace Win81Layer;

// UNIFIED ACTION ROUTER (directive §6/§7/§33): one backend every invocation surface routes through — Deep-Pin tiles,
// Search results, the command palette, and external launcher:// URIs all call LauncherAction.Invoke, which dispatches to
// the SAME existing capabilities (workspaces, display, radios, settings, folders). SECURITY (§9): only the registered
// routes + whitelisted targets are honoured — never an arbitrary path or command. UI-touching routes go through hooks that
// App wires with Dispatcher marshalling, so Invoke is safe to call from any thread (tile click or the --uri CLI).
internal static class ActionRouter
{
	// Wired by App at startup (marshal to the UI thread inside the hook). Null when unavailable (e.g. a transient --uri run).
	public static Action<string>? WorkspaceLaunch;

	public static Action<string>? ShowSearch;

	public const string Scheme = "launcher://";

	// Returns true only if a REGISTERED route handled it. Unknown/invalid routes are rejected (logged), never executed.
	public static bool Invoke(string uriOrId)
	{
		if (string.IsNullOrWhiteSpace(uriOrId))
		{
			return false;
		}
		try
		{
			string s = uriOrId.Trim();
			if (s.StartsWith(Scheme, StringComparison.OrdinalIgnoreCase))
			{
				s = s.Substring(Scheme.Length);
			}
			string query = "";
			int qi = s.IndexOf('?');
			if (qi >= 0)
			{
				query = s.Substring(qi + 1);
				s = s.Substring(0, qi);
			}
			string[] parts = s.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
			if (parts.Length == 0)
			{
				return false;
			}
			string route = parts[0].ToLowerInvariant();
			string target = (parts.Length > 1) ? Uri.UnescapeDataString(parts[1]) : "";
			switch (route)
			{
				case "workspace":
					if (string.IsNullOrEmpty(target) || WorkspaceLaunch == null)
					{
						return false;
					}
					WorkspaceLaunch(target);
					return true;
				case "display":
				{
					string m = target.ToLowerInvariant();
					string? mode = m switch
					{
						"clone" or "duplicate" => "/clone",
						"extend" => "/extend",
						"external" or "second" => "/external",
						"internal" or "pc" => "/internal",
						_ => null
					};
					if (mode == null) { return false; }
					DeviceActions.Display(mode);
					return true;
				}
				case "bluetooth":
					if (target == "toggle") { _ = RadioQuick.ToggleBluetooth(); return true; }
					OpenUri("ms-settings:bluetooth");
					return true;
				case "settings":
				{
					string? uri = SettingsUriFor(target.ToLowerInvariant());
					if (uri == null) { return false; }
					OpenUri(uri);
					return true;
				}
				case "action":
					return RunNamedAction(target.ToLowerInvariant());
				case "search":
					if (ShowSearch == null) { return false; }
					ShowSearch(ParseQuery(query, "q"));
					return true;
				default:
					Logger.Log("[launcher-uri] unknown route: " + route);
					return false;
			}
		}
		catch (Exception ex)
		{
			Logger.Log("[launcher-uri] invoke failed: " + ex.Message);
			return false;
		}
	}

	// Whitelisted ms-settings targets only (no arbitrary URI passthrough).
	private static string? SettingsUriFor(string key)
	{
		return key switch
		{
			"bluetooth" => "ms-settings:bluetooth",
			"display" => "ms-settings:display",
			"sound" or "audio" or "volume" => "ms-settings:sound",
			"network" => "ms-settings:network-status",
			"wifi" or "wi-fi" => "ms-settings:network-wifi",
			"battery" or "power" => "ms-settings:batterysaver",
			"update" => "ms-settings:windowsupdate",
			"apps" => "ms-settings:appsfeatures",
			"personalization" or "theme" => "ms-settings:personalization",
			"about" or "system" => "ms-settings:about",
			_ => null
		};
	}

	// Named folder actions only — a fixed set of known-folder opens. No arbitrary path.
	private static bool RunNamedAction(string name)
	{
		Environment.SpecialFolder? sf = name switch
		{
			"open-downloads" => (Environment.SpecialFolder?)null,   // Downloads has no SpecialFolder enum; handled below
			"open-documents" => Environment.SpecialFolder.MyDocuments,
			"open-pictures" => Environment.SpecialFolder.MyPictures,
			"open-music" => Environment.SpecialFolder.MyMusic,
			"open-videos" => Environment.SpecialFolder.MyVideos,
			"open-desktop" => Environment.SpecialFolder.DesktopDirectory,
			_ => (Environment.SpecialFolder?)null
		};
		string? path = null;
		if (name == "open-downloads")
		{
			try { path = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads"); } catch { }
		}
		else if (sf.HasValue)
		{
			try { path = Environment.GetFolderPath(sf.Value); } catch { }
		}
		if (string.IsNullOrEmpty(path))
		{
			return false;
		}
		string p = path;
		ShellLaunch.Run(delegate
		{
			try { Process.Start(new ProcessStartInfo("explorer.exe", "\"" + p + "\"") { UseShellExecute = true }); }
			catch (Exception ex) { Logger.Log("[launcher-uri] open folder: " + ex.Message); }
		});
		return true;
	}

	private static void OpenUri(string uri)
	{
		ShellLaunch.Run(delegate
		{
			try { Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true }); }
			catch (Exception ex) { Logger.Log("[launcher-uri] open uri: " + ex.Message); }
		});
	}

	private static string ParseQuery(string query, string key)
	{
		if (string.IsNullOrEmpty(query))
		{
			return "";
		}
		foreach (string kv in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
		{
			int eq = kv.IndexOf('=');
			if (eq > 0 && string.Equals(kv.Substring(0, eq), key, StringComparison.OrdinalIgnoreCase))
			{
				try { return Uri.UnescapeDataString(kv.Substring(eq + 1)); } catch { return kv.Substring(eq + 1); }
			}
		}
		return "";
	}
}
