using System;
using System.Collections.Generic;
using System.Linq;

namespace Win81Layer;

public static class SearchExtras
{
	public sealed record Setting(string Name, string Uri, string[] Keywords);

	public static readonly (string Alias, string Match)[] Aliases = new(string, string)[21]
	{
		("cmd", "command prompt"),
		("command", "command prompt"),
		("calc", "calculator"),
		("regedit", "registry editor"),
		("taskmgr", "task manager"),
		("explorer", "file explorer"),
		("files", "file explorer"),
		("ps", "powershell"),
		("pwsh", "powershell"),
		("control", "control panel"),
		("msconfig", "system configuration"),
		("notepad", "notepad"),
		("paint", "paint"),
		("snip", "snipping"),
		("screenshot", "snipping"),
		("terminal", "terminal"),
		("vscode", "visual studio code"),
		("vs", "visual studio"),
		("word", "word"),
		("excel", "excel"),
		("ppt", "powerpoint")
	};

	public static readonly Setting[] Settings = new Setting[22]
	{
		new Setting("Sound settings", "ms-settings:sound", new string[6] { "sound", "audio", "volume", "speaker", "microphone", "playback" }),
		new Setting("Mouse settings", "ms-settings:mousetouchpad", new string[4] { "mouse", "touchpad", "pointer", "cursor" }),
		new Setting("Bluetooth & devices", "ms-settings:bluetooth", new string[2] { "bluetooth", "device" }),
		new Setting("Display settings", "ms-settings:display", new string[6] { "display", "screen", "resolution", "monitor", "brightness", "scaling" }),
		new Setting("Network & Internet", "ms-settings:network-status", new string[5] { "network", "internet", "ethernet", "vpn", "proxy" }),
		new Setting("Wi-Fi", "ms-settings:network-wifi", new string[3] { "wifi", "wi-fi", "wireless" }),
		new Setting("Windows Update", "ms-settings:windowsupdate", new string[2] { "update", "windows update" }),
		new Setting("Apps & features", "ms-settings:appsfeatures", new string[4] { "apps", "uninstall", "programs", "features" }),
		new Setting("Default apps", "ms-settings:defaultapps", new string[3] { "default app", "default program", "file association" }),
		new Setting("Printers & scanners", "ms-settings:printers", new string[3] { "printer", "scanner", "print" }),
		new Setting("Power & sleep", "ms-settings:powersleep", new string[3] { "power", "sleep", "battery" }),
		new Setting("Keyboard", "ms-settings:keyboard", new string[1] { "keyboard" }),
		new Setting("Date & time", "ms-settings:dateandtime", new string[4] { "date", "time", "clock", "timezone" }),
		new Setting("Language & region", "ms-settings:regionlanguage", new string[3] { "language", "region", "locale" }),
		new Setting("Personalization", "ms-settings:personalization", new string[6] { "personalization", "wallpaper", "background", "theme", "colors", "lock screen" }),
		new Setting("Taskbar settings", "ms-settings:taskbar", new string[1] { "taskbar" }),
		new Setting("Notifications", "ms-settings:notifications", new string[2] { "notification", "focus assist" }),
		new Setting("Storage", "ms-settings:storagesense", new string[3] { "storage", "disk space", "cleanup" }),
		new Setting("About (System)", "ms-settings:about", new string[5] { "about", "system info", "pc name", "specs", "device name" }),
		new Setting("Windows Security", "windowsdefender:", new string[5] { "security", "antivirus", "defender", "firewall", "virus" }),
		new Setting("Control Panel", "control", new string[1] { "control panel" }),
		new Setting("Mobile hotspot", "ms-settings:network-mobilehotspot", new string[2] { "hotspot", "tethering" })
	};

	public static bool AppMatches(string appName, string query)
	{
		if (string.IsNullOrEmpty(query))
		{
			return true;
		}
		// Use the real relevance engine (same one the charm Search pane uses) so the in-Start type-to-search filter matches
		// its quality: exact/prefix/word-sequence/acronym/word-start/substring, not just plain Contains. This upgrades the
		// in-Start filter for free and keeps the two search surfaces consistent. Kept a bool predicate (call sites unchanged).
		int sc = SearchScore.Name(appName, query);
		if (sc >= SearchScore.Substring)
		{
			return true;
		}
		// Typo tolerance (Damerau fuzzy) only for longer queries, so short 2-3 char queries don't flood the unranked filter.
		if (query.Length >= 4 && sc > 0)
		{
			return true;
		}
		(string, string)[] aliases = Aliases;
		for (int i = 0; i < aliases.Length; i++)
		{
			var (alias, match) = aliases[i];
			if (alias.StartsWith(query, StringComparison.OrdinalIgnoreCase) && appName.Contains(match, StringComparison.CurrentCultureIgnoreCase))
			{
				return true;
			}
		}
		return false;
	}

	public static IEnumerable<Setting> MatchingSettings(string query)
	{
		Setting[] settings = Settings;
		foreach (Setting s in settings)
		{
			if (s.Name.Contains(query, StringComparison.CurrentCultureIgnoreCase) || s.Keywords.Any((string k) => k.Contains(query, StringComparison.OrdinalIgnoreCase) || query.Contains(k, StringComparison.OrdinalIgnoreCase)))
			{
				yield return s;
			}
		}
	}
}
