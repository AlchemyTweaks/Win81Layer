using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace Win81Layer;

// Central launch path for Start, Search, Win7 mode and the replacement taskbar. Packaged apps must be activated by
// AUMID; directly starting versioned executables under WindowsApps is both unstable and commonly access-denied.
public static class AppLauncher
{
	internal static string DiagnosticExecutableLaunchMode => "native-createprocessw-with-shell-fallback";

	private const uint NormalPriorityClass = 0x00000020;

	private const uint CreateDefaultErrorMode = 0x04000000;

	[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
	private struct STARTUPINFO
	{
		internal uint cb;
		internal string? lpReserved;
		internal string? lpDesktop;
		internal string? lpTitle;
		internal uint dwX;
		internal uint dwY;
		internal uint dwXSize;
		internal uint dwYSize;
		internal uint dwXCountChars;
		internal uint dwYCountChars;
		internal uint dwFillAttribute;
		internal uint dwFlags;
		internal ushort wShowWindow;
		internal ushort cbReserved2;
		internal nint lpReserved2;
		internal nint hStdInput;
		internal nint hStdOutput;
		internal nint hStdError;
	}

	[StructLayout(LayoutKind.Sequential)]
	private struct PROCESS_INFORMATION
	{
		internal nint hProcess;
		internal nint hThread;
		internal uint dwProcessId;
		internal uint dwThreadId;
	}

	[DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static extern bool CreateProcessW(
		string lpApplicationName,
		StringBuilder lpCommandLine,
		nint lpProcessAttributes,
		nint lpThreadAttributes,
		[MarshalAs(UnmanagedType.Bool)] bool bInheritHandles,
		uint dwCreationFlags,
		nint lpEnvironment,
		string? lpCurrentDirectory,
		ref STARTUPINFO lpStartupInfo,
		out PROCESS_INFORMATION lpProcessInformation);

	[DllImport("kernel32.dll", SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static extern bool CloseHandle(nint handle);

	private const string AppsFolderPrefix = "shell:AppsFolder\\";

	private static readonly object IdentityGate = new object();

	private static List<(string Name, string Aumid)>? _packagedIdentities;

	private static DateTime _identityScanUtc;

	private static readonly Guid ActivationManagerClsid = new Guid("45BA127D-10A8-46EA-8AB7-56EA9078943C");

	// The CLSID->Type resolution is process-invariant (registry lookup); resolve it once instead of on every launch.
	// The COM instance is apartment-bound, so each persistent launch STA owns one cached manager.
	private static readonly Type? s_activationManagerType = Type.GetTypeFromCLSID(ActivationManagerClsid, throwOnError: false);

	[ThreadStatic]
	private static object? t_activationManagerObject;

	[ThreadStatic]
	private static IApplicationActivationManager? t_activationManager;

	public static bool TryLaunch(
		string launchPath,
		string? args,
		string? explicitAumid,
		bool asAdmin,
		out string? error)
	{
		error = null;
		try
		{
			if (string.IsNullOrWhiteSpace(launchPath))
			{
				error = "Launch path is empty.";
				return false;
			}
			// Web links / tiles / place-search cards all funnel through here; route http(s) to the preferred
			// browser (MetroBrowser by default) instead of the system default browser. See WebOpen.
			if (WebOpen.IsWeb(launchPath))
			{
				WebOpen.Url(launchPath);
				return true;
			}
			// In the Windows 8.1 profile, File Explorer pins use the launcher-owned Explorer surface. Unsupported shell
			// arguments and every other profile fall through untouched to the native explorer.exe path below.
			if (FileBrowser.TryHandleExplorerLaunch(launchPath, args, asAdmin))
			{
				return true;
			}

			string? aumid = ResolveAumid(null, launchPath, explicitAumid);
			if (!string.IsNullOrWhiteSpace(aumid) && !asAdmin)
			{
				if (TryActivateApplication(aumid!, args, out error))
				{
					return true;
				}
				if (TryLaunchViaExplorer(AppsFolderPrefix + aumid, out string? explorerError))
				{
					return true;
				}
				error = $"AUMID activation failed ({error}); Explorer fallback failed ({explorerError}).";
				return false;
			}

			if (IsWindowsAppsPath(launchPath))
			{
				error = "Packaged app executable has no resolvable AUMID; refusing unstable direct WindowsApps launch.";
				return false;
			}

			if (launchPath.StartsWith(AppsFolderPrefix, StringComparison.OrdinalIgnoreCase))
			{
				return TryLaunchViaExplorer(launchPath, out error);
			}

			bool directExecutable = !asAdmin && IsDirectExecutable(launchPath);
			string? workingDirectory = null;
			if (LooksLikeFilePath(launchPath))
			{
				string? directory = Path.GetDirectoryName(launchPath);
				if (!string.IsNullOrWhiteSpace(directory) && Directory.Exists(directory))
				{
					workingDirectory = directory;
				}
			}
			string? directError = null;
			if (directExecutable && TryCreateProcess(launchPath, args, workingDirectory, out directError))
			{
				return true;
			}
			if (directExecutable)
			{
				Logger.Log($"Native executable launch fell back to ShellExecute for '{launchPath}': {directError}");
			}
			ProcessStartInfo startInfo = new ProcessStartInfo(launchPath)
			{
				UseShellExecute = true,
				WorkingDirectory = workingDirectory ?? string.Empty
			};
			if (!string.IsNullOrWhiteSpace(args))
			{
				startInfo.Arguments = args;
			}
			if (asAdmin)
			{
				startInfo.Verb = "runas";
			}
			Process.Start(startInfo);
			return true;
		}
		catch (Exception ex)
		{
			error = ex.Message;
			return false;
		}
	}

	public static bool NormalizePinnedApp(PinnedApp pin)
	{
		if (pin == null)
		{
			return false;
		}
		string? aumid = ResolveAumid(pin.Name, pin.LaunchPath, pin.Aumid);
		if (string.IsNullOrWhiteSpace(aumid) && IsWindowsAppsPath(pin.ExePath))
		{
			aumid = ResolveAumid(pin.Name, pin.ExePath, null);
		}
		if (string.IsNullOrWhiteSpace(aumid))
		{
			return false;
		}

		string stableLaunch = AppsFolderPrefix + aumid;
		bool changed = !string.Equals(pin.Aumid, aumid, StringComparison.OrdinalIgnoreCase)
			|| !string.Equals(pin.LaunchPath, stableLaunch, StringComparison.OrdinalIgnoreCase)
			|| IsWindowsAppsPath(pin.ExePath)
			|| IsWindowsAppsPath(pin.IconPath);
		pin.Aumid = aumid;
		pin.LaunchPath = stableLaunch;
		if (IsWindowsAppsPath(pin.ExePath))
		{
			pin.ExePath = null;
		}
		if (IsWindowsAppsPath(pin.IconPath))
		{
			pin.IconPath = null;
		}
		return changed;
	}

	public static string? ResolveAumid(string? displayName, string? launchPath, string? explicitAumid)
	{
		if (!string.IsNullOrWhiteSpace(explicitAumid))
		{
			return explicitAumid.Trim();
		}
		if (!string.IsNullOrWhiteSpace(launchPath)
			&& launchPath.StartsWith(AppsFolderPrefix, StringComparison.OrdinalIgnoreCase))
		{
			string shellId = launchPath.Substring(AppsFolderPrefix.Length).Trim();
			if (!string.IsNullOrWhiteSpace(shellId))
			{
				return shellId;
			}
		}

		if (!IsWindowsAppsPath(launchPath))
		{
			return null;
		}

		List<(string Name, string Aumid)> identities = PackagedIdentities();
		string? family = PackageFamilyHint(launchPath!);
		if (!string.IsNullOrWhiteSpace(family))
		{
			(string Name, string Aumid) familyMatch = identities.FirstOrDefault(x => x.Aumid.StartsWith(family + "!", StringComparison.OrdinalIgnoreCase));
			if (!string.IsNullOrWhiteSpace(familyMatch.Aumid))
			{
				return familyMatch.Aumid;
			}
		}
		if (!string.IsNullOrWhiteSpace(displayName))
		{
			(string Name, string Aumid) exact = identities.FirstOrDefault(x => string.Equals(x.Name, displayName, StringComparison.CurrentCultureIgnoreCase));
			if (!string.IsNullOrWhiteSpace(exact.Aumid))
			{
				return exact.Aumid;
			}
		}
		return null;
	}

	public static bool IsWindowsAppsPath(string? path)
	{
		return !string.IsNullOrWhiteSpace(path)
			&& path.IndexOf("\\WindowsApps\\", StringComparison.OrdinalIgnoreCase) >= 0;
	}

	// Populate the packaged-app AUMID cache on a background thread at startup so the FIRST Store/packaged-app launch
	// after boot does not pay the AppsFolder enumeration cost inline (perceived launch latency). Safe + idempotent:
	// PackagedIdentities() is lock-guarded and caches for the process lifetime once it has entries.
	private static int _prewarmStarted;
	public static void PrewarmIdentities()
	{
		if (System.Threading.Interlocked.Exchange(ref _prewarmStarted, 1) != 0)
		{
			return;
		}
		System.Threading.Thread t = new System.Threading.Thread((System.Threading.ThreadStart)delegate
		{
			try
			{
				int n = PackagedIdentities().Count;
				Logger.Log($"AppLauncher: packaged-identity cache prewarmed ({n} AUMIDs)");
			}
			catch (Exception ex)
			{
				Logger.Log("AppLauncher identity prewarm failed: " + ex.Message);
			}
		})
		{
			IsBackground = true,
			Name = "AumidPrewarm",
			Priority = System.Threading.ThreadPriority.BelowNormal
		};
		t.SetApartmentState(System.Threading.ApartmentState.STA);
		t.Start();
	}

	private static List<(string Name, string Aumid)> PackagedIdentities()
	{
		lock (IdentityGate)
		{
			if (_packagedIdentities != null
				&& (_packagedIdentities.Count > 0 || DateTime.UtcNow - _identityScanUtc < TimeSpan.FromSeconds(10)))
			{
				return _packagedIdentities;
			}
			List<(string Name, string Aumid)> result = new List<(string, string)>();
			try
			{
				foreach ((AppEntry entry, AppInventory.IShellItem _) in AppInventory.EnumerateAppsFolder())
				{
					string? aumid = !string.IsNullOrWhiteSpace(entry.AppId)
						? entry.AppId
						: (entry.LaunchPath.StartsWith(AppsFolderPrefix, StringComparison.OrdinalIgnoreCase)
							? entry.LaunchPath.Substring(AppsFolderPrefix.Length)
							: null);
					if (!string.IsNullOrWhiteSpace(aumid) && aumid.Contains('!'))
					{
						result.Add((entry.Name, aumid));
					}
				}
			}
			catch (Exception ex)
			{
				Logger.Log("Packaged app identity scan failed: " + ex.Message);
			}
			_packagedIdentities = result;
			_identityScanUtc = DateTime.UtcNow;
			return result;
		}
	}

	private static string? PackageFamilyHint(string windowsAppsPath)
	{
		try
		{
			string marker = "\\WindowsApps\\";
			int start = windowsAppsPath.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
			if (start < 0)
			{
				return null;
			}
			start += marker.Length;
			int end = windowsAppsPath.IndexOf('\\', start);
			string packageFullName = end < 0 ? windowsAppsPath.Substring(start) : windowsAppsPath.Substring(start, end - start);
			Match match = Regex.Match(packageFullName, "^(?<name>.+?)_(?:\\d+\\.){3}\\d+_[^_]+(?:_[^_]*)?__(?<publisher>[^_]+)$", RegexOptions.IgnoreCase);
			return match.Success ? match.Groups["name"].Value + "_" + match.Groups["publisher"].Value : null;
		}
		catch
		{
			return null;
		}
	}

	private static bool TryActivateApplication(string aumid, string? arguments, out string? error)
	{
		error = null;
		try
		{
			IApplicationActivationManager manager = GetThreadActivationManager();
			int hr = manager.ActivateApplication(aumid, arguments, ActivateOptions.None, out _);
			if (hr < 0)
			{
				error = Marshal.GetExceptionForHR(hr)?.Message ?? $"HRESULT 0x{hr:X8}";
				return false;
			}
			return true;
		}
		catch (Exception ex)
		{
			ResetThreadActivationManager();
			error = ex.Message;
			return false;
		}
	}

	private static IApplicationActivationManager GetThreadActivationManager()
	{
		if (t_activationManager != null)
		{
			return t_activationManager;
		}
		Type managerType = s_activationManagerType ?? Type.GetTypeFromCLSID(ActivationManagerClsid, throwOnError: true)!;
		t_activationManagerObject = Activator.CreateInstance(managerType);
		t_activationManager = (IApplicationActivationManager)t_activationManagerObject!;
		return t_activationManager;
	}

	private static void ResetThreadActivationManager()
	{
		try
		{
			if (t_activationManagerObject != null && Marshal.IsComObject(t_activationManagerObject))
			{
				Marshal.FinalReleaseComObject(t_activationManagerObject);
			}
		}
		catch
		{
		}
		t_activationManager = null;
		t_activationManagerObject = null;
	}

	private static bool TryLaunchViaExplorer(string shellPath, out string? error)
	{
		error = null;
		try
		{
			Process.Start(new ProcessStartInfo("explorer.exe")
			{
				UseShellExecute = true,
				Arguments = "\"" + shellPath.Replace("\"", "") + "\""
			});
			return true;
		}
		catch (Exception ex)
		{
			error = ex.Message;
			return false;
		}
	}

	private static bool LooksLikeFilePath(string value)
	{
		return Path.IsPathFullyQualified(value) || value.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || value.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase);
	}

	private static bool IsDirectExecutable(string value)
	{
		return Path.IsPathFullyQualified(value)
			&& value.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
			&& File.Exists(value);
	}

	private static bool TryCreateProcess(string executable, string? arguments, string? workingDirectory, out string? error)
	{
		error = null;
		STARTUPINFO startup = new STARTUPINFO
		{
			cb = (uint)Marshal.SizeOf<STARTUPINFO>()
		};
		StringBuilder commandLine = new StringBuilder(executable.Length + (arguments?.Length ?? 0) + 4);
		commandLine.Append('"').Append(executable).Append('"');
		if (!string.IsNullOrWhiteSpace(arguments))
		{
			commandLine.Append(' ').Append(arguments);
		}
		if (!CreateProcessW(
			executable,
			commandLine,
			IntPtr.Zero,
			IntPtr.Zero,
			bInheritHandles: false,
			NormalPriorityClass | CreateDefaultErrorMode,
			IntPtr.Zero,
			workingDirectory,
			ref startup,
			out PROCESS_INFORMATION process))
		{
			int code = Marshal.GetLastWin32Error();
			error = new Win32Exception(code).Message + $" (Win32 {code})";
			return false;
		}
		try
		{
			return true;
		}
		finally
		{
			if (process.hThread != IntPtr.Zero)
			{
				CloseHandle(process.hThread);
			}
			if (process.hProcess != IntPtr.Zero)
			{
				CloseHandle(process.hProcess);
			}
		}
	}

	[Flags]
	private enum ActivateOptions
	{
		None = 0,
		DesignMode = 1,
		NoErrorUi = 2,
		NoSplashScreen = 4
	}

	[ComImport]
	[Guid("2E941141-7F97-4756-BA1D-9DECDE894A3D")]
	[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
	private interface IApplicationActivationManager
	{
		[PreserveSig]
		int ActivateApplication(
			[MarshalAs(UnmanagedType.LPWStr)] string appUserModelId,
			[MarshalAs(UnmanagedType.LPWStr)] string? arguments,
			ActivateOptions options,
			out uint processId);

		[PreserveSig]
		int ActivateForFile(IntPtr appUserModelId, IntPtr itemArray, IntPtr verb, out uint processId);

		[PreserveSig]
		int ActivateForProtocol(IntPtr appUserModelId, IntPtr itemArray, out uint processId);
	}
}
