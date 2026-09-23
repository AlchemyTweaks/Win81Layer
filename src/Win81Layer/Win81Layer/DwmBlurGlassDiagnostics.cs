using System;
using System.IO;
using System.Text.Json;

namespace Win81Layer;

internal static class DwmBlurGlassDiagnostics
{
	private static string DataRoot => Path.Combine(
		Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
		"Win81Layer");

	public static string RunPolicyRoundTrip()
	{
		string qaRoot = Path.Combine(DataRoot, "qa");
		Directory.CreateDirectory(qaRoot);
		string reportPath = Path.Combine(qaRoot, "dwmblurglass-policy-qa-latest.json");
		bool markerPresent = File.Exists(Path.Combine(AppContext.BaseDirectory, "DWM_DISABLED.marker"));
		bool passed = markerPresent
			&& !DwmBlurGlassBridge.Installed
			&& !DwmBlurGlassBridge.Supported
			&& !DwmBlurGlassBridge.AeroEnabled
			&& !DwmBlurGlassBridge.Injected
			&& !DwmBlurGlassBridge.HostRunning
			&& !DwmBlurGlassBridge.RollbackSnapshotPresent;
		WriteReport(reportPath, new
		{
			SchemaVersion = 2,
			GeneratedUtc = DateTime.UtcNow,
			Passed = passed,
			Mode = "permanently-disabled",
			DisabledMarkerPresent = markerPresent,
			InstalledForLauncher = DwmBlurGlassBridge.Installed,
			SupportedByLauncher = DwmBlurGlassBridge.Supported,
			AeroEnabled = DwmBlurGlassBridge.AeroEnabled,
			Injected = DwmBlurGlassBridge.Injected,
			HostRunning = DwmBlurGlassBridge.HostRunning,
			RollbackSnapshotPresent = DwmBlurGlassBridge.RollbackSnapshotPresent
		});
		if (!passed)
		{
			throw new InvalidOperationException("DWMBlurGlass disabled-policy audit failed; see " + reportPath);
		}
		return reportPath;
	}

	private static void WriteReport(string path, object report)
	{
		File.WriteAllText(path, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
	}
}
