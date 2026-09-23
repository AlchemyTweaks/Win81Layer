using System;
using System.Diagnostics;
using System.IO;

namespace Win81Layer;

// DETECTION surface for the EXTERNAL DWMBlurGlass tool (github.com/Maplespe/DWMBlurGlass) that the user installs and
// runs STANDALONE (always-on via its own scheduled task). The launcher does NOT drive it: there is no clean live
// on/off hook (config.ini has no master enable flag; the injected ext reloads only via DWMBlurGlass's own GUI IPC,
// not a file-watch), and — critically — the launcher must NEVER tear down the user's glass. So Apply()/Disable()/
// EnsureOnDemandAutostartPolicy() are intentional NO-OPS. These read-only properties only REPORT whether DWMBlurGlass
// is installed + mounted, so composition diagnostics/status are honest. Safe: all built-in profiles keep
// ExternalAeroGlass=false and UseDwmBlurGlassForWin7=false, so nothing here changes owned-window rendering.
public static class DwmBlurGlassBridge
{
	public const string InstallDir = "C:\\DWMBlurGlass";

	internal static bool LastApplyChanged => false;

	internal static int DiagnosticRefreshPosts => 0;

	public static event Action StateChanged
	{
		add { }
		remove { }
	}

	// Installed = the DWMBlurGlass GUI is present in its (mandatory non-user) install dir.
	public static bool Installed
	{
		get
		{
			try { return File.Exists(Path.Combine(InstallDir, "DWMBlurGlassGUI.exe")); }
			catch { return false; }
		}
	}

	public static bool Supported => Installed;

	// HostRunning = the DWMBlurGlass host/extension is currently mounted (its host process is alive). Best-effort,
	// cross-privilege (process-name probe, per-process access guarded); reports false if it can't be read.
	public static bool HostRunning
	{
		get
		{
			try
			{
				Process[] all = Process.GetProcesses();
				foreach (Process p in all)
				{
					try
					{
						if (p.ProcessName.IndexOf("DWMBlurGlass", StringComparison.OrdinalIgnoreCase) >= 0)
						{
							return true;
						}
					}
					catch
					{
					}
				}
			}
			catch
			{
			}
			return false;
		}
	}

	// A mounted host is our signal that real foreign Aero glass is active.
	public static bool Injected => HostRunning;

	public static bool AeroEnabled => Installed && HostRunning;

	public static bool RollbackSnapshotPresent => false;

	public static bool EffectDisabled => !AeroEnabled;

	// The launcher NEVER drives DWMBlurGlass (it self-manages via its own GUI + the DWMBlurGlass_Extend scheduled task).
	// Intentional no-ops so a composition-mode change or launcher exit can never turn the user's glass off.
	public static void Apply(CompositionProfile profile, bool enableForWin7)
	{
	}

	public static void Disable()
	{
	}

	public static string DescribeState(CompositionProfile profile, bool enableForWin7)
	{
		try
		{
			if (!Installed)
			{
				return "Not installed";
			}
			return HostRunning
				? "External DWMBlurGlass active — real Aero glass (self-managed by DWMBlurGlass)"
				: "Installed but not currently mounted";
		}
		catch
		{
			return "Unknown";
		}
	}

	public static void EnsureOnDemandAutostartPolicy()
	{
	}
}
