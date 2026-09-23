#nullable enable

using System;
using System.Diagnostics;
using System.IO;

namespace Win81Layer;

// Single choke-point for opening a WEB (http/https) URL. When the preferred browser is MetroBrowser (default) and
// its Electron host is present, launch it with the URL as an argument - MetroBrowser's single-instance lock
// forwards the URL to the already-running window and brings it to the front. Anything else, or any failure, falls
// back to the system default browser (ShellExecute). Non-web URIs (bingmaps:, ms-settings:, search-ms:, files)
// must NOT be routed here - callers guard on http/https first.
public static class WebOpen
{
	private static string DefaultMetroDir =>
		Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "MetroBrowser");

	private static string ResolveMetroDir()
	{
		try
		{
			string d = SettingsStore.FastSnapshot.MetroBrowserDir;
			if (!string.IsNullOrWhiteSpace(d))
			{
				return d;
			}
		}
		catch
		{
		}
		return DefaultMetroDir;
	}

	private static bool PrefersMetro()
	{
		try
		{
			return !string.Equals(SettingsStore.FastSnapshot.PreferredBrowser, "System", StringComparison.OrdinalIgnoreCase);
		}
		catch
		{
			return true;
		}
	}

	public static bool IsWeb(string? url)
	{
		return !string.IsNullOrWhiteSpace(url)
			&& (url!.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
				|| url.StartsWith("https://", StringComparison.OrdinalIgnoreCase));
	}

	// Open a web URL in the preferred browser. Always attempts something; never throws.
	public static void Url(string url)
	{
		if (string.IsNullOrWhiteSpace(url))
		{
			return;
		}
		if (PrefersMetro() && TryMetro(url))
		{
			return;
		}
		OpenSystemDefault(url);
	}

	private static bool TryMetro(string url)
	{
		try
		{
			string dir = ResolveMetroDir();
			string exe = Path.Combine(dir, "node_modules", "electron", "dist", "electron.exe");
			if (!File.Exists(exe) || !Directory.Exists(dir))
			{
				return false;
			}
			ProcessStartInfo psi = new ProcessStartInfo
			{
				FileName = exe,
				UseShellExecute = false,
				WorkingDirectory = dir
			};
			psi.ArgumentList.Add(dir);   // the app directory Electron runs
			psi.ArgumentList.Add(url);   // the URL, forwarded to MetroBrowser's single-instance handler
			Process.Start(psi);
			return true;
		}
		catch (Exception ex)
		{
			Logger.Log("WebOpen MetroBrowser failed, falling back to system default: " + ex.Message);
			return false;
		}
	}

	private static void OpenSystemDefault(string url)
	{
		try
		{
			Process.Start(new ProcessStartInfo(url)
			{
				UseShellExecute = true
			});
		}
		catch (Exception ex)
		{
			Logger.Log("WebOpen system-default failed for '" + url + "': " + ex.Message);
		}
	}
}
