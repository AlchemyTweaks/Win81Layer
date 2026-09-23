#nullable enable

using System;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace Win81Layer;

internal static class FileBrowser
{
	private static WeakReference<Win81Window>? _lastWindow;

	internal static bool IsWin81ExplorerActive
	{
		get
		{
			try
			{
				// Native File Explorer is kept (user request; a full 8.1 native reskin will be done later via injection).
				// The launcher's own 8.1 browser stays inert unless explicitly opted in AND running the 8.1 composition.
				if (!SettingsStore.Current.Win81CustomExplorer)
				{
					return false;
				}
				return string.Equals(
					CompositionProfiles.Resolve(DesktopComposition.EffectiveMode).Id,
					"windows81",
					StringComparison.OrdinalIgnoreCase);
			}
			catch
			{
				return false;
			}
		}
	}

	internal static bool TryShow(string? path = null, string? selectPath = null, bool forceNew = false)
	{
		return IsWin81ExplorerActive && Show(path, selectPath, forceNew);
	}

	internal static bool Show(string? path = null, string? selectPath = null, bool forceNew = false)
	{
		try
		{
			Dispatcher dispatcher = Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;
			if (!dispatcher.CheckAccess())
			{
				return dispatcher.Invoke(() => Show(path, selectPath, forceNew));
			}

			if (!forceNew && _lastWindow != null && _lastWindow.TryGetTarget(out Win81Window? existing) && existing.IsVisible)
			{
				if (existing.BodyContent is FileBrowserBody existingBody)
				{
					existingBody.Open(path, selectPath);
				}
				if (existing.WindowState == WindowState.Minimized)
				{
					existing.WindowState = WindowState.Normal;
				}
				existing.Activate();
				return true;
			}

			FileBrowserBody body = new();
			Win81Window window = new()
			{
				Title = "This PC",
				Width = 800,
				Height = 600,
				MinWidth = 640,
				MinHeight = 430,
				WindowStartupLocation = WindowStartupLocation.CenterScreen,
				Icon = Win81AssetResolver.GetAsset("Shell.ThisPC", 32) ?? new BitmapImage(new Uri("pack://application:,,,/Assets/win81icons/thispc.ico", UriKind.Absolute))
			};
			window.ConfigureExplorer81();
			window.SetBody(body);
			body.TitleChanged += title => window.Title = title;
			window.ExplorerPropertiesRequested += body.ShowCurrentProperties;
			window.ExplorerNewFolderRequested += body.CreateNewFolder;
			window.Closed += (_, _) => body.Dispose();
			body.Open(path, selectPath);
			window.Show();
			_lastWindow = new WeakReference<Win81Window>(window);
			return true;
		}
		catch (Exception ex)
		{
			Logger.Log("FileBrowser.Show: " + ex);
			return false;
		}
	}

	internal static bool TryShowLocation(string path)
	{
		if (!IsWin81ExplorerActive || string.IsNullOrWhiteSpace(path))
		{
			return false;
		}
		try
		{
			if (Directory.Exists(path))
			{
				return Show(path, null, forceNew: true);
			}
			if (File.Exists(path))
			{
				string? parent = Path.GetDirectoryName(path);
				return !string.IsNullOrWhiteSpace(parent) && Show(parent, path, forceNew: true);
			}
		}
		catch
		{
		}
		return false;
	}

	internal static bool TryHandleExplorerLaunch(string launchPath, string? arguments, bool asAdmin)
	{
		if (asAdmin || !IsWin81ExplorerActive || string.IsNullOrWhiteSpace(launchPath))
		{
			return false;
		}
		string fileName;
		try
		{
			fileName = Path.GetFileName(Environment.ExpandEnvironmentVariables(launchPath.Trim().Trim('"')));
		}
		catch
		{
			return false;
		}
		if (!fileName.Equals("explorer.exe", StringComparison.OrdinalIgnoreCase))
		{
			return false;
		}

		string raw = (arguments ?? "").Trim();
		if (raw.Length == 0)
		{
			return Show(null, null, forceNew: true);
		}
		string? select = null;
		if (raw.StartsWith("/select,", StringComparison.OrdinalIgnoreCase))
		{
			select = Unquote(raw.Substring(8));
			return TryShowLocation(select);
		}
		if (raw.StartsWith("/e,", StringComparison.OrdinalIgnoreCase))
		{
			raw = raw.Substring(3).Trim();
		}
		string target = Unquote(raw);
		if (target.Equals("shell:MyComputerFolder", StringComparison.OrdinalIgnoreCase)
			|| target.Equals("shell:::{20D04FE0-3AEA-1069-A2D8-08002B30309D}", StringComparison.OrdinalIgnoreCase))
		{
			return Show(null, null, forceNew: true);
		}
		if (target.Equals("shell:NetworkPlacesFolder", StringComparison.OrdinalIgnoreCase))
		{
			return Show("Network", null, forceNew: true);
		}
		if (Directory.Exists(target))
		{
			return Show(target, null, forceNew: true);
		}
		if (File.Exists(target))
		{
			return TryShowLocation(target);
		}
		return false;
	}

	private static string Unquote(string value)
	{
		return value.Trim().Trim('"').Replace("\"\"", "\"");
	}
}
