namespace Win81Layer;

public sealed class PinnedApp
{
	public string Name { get; set; } = "";

	public string LaunchPath { get; set; } = "";

	public string? Args { get; set; }

	public string? ExePath { get; set; }

	public string? IconPath { get; set; }

	// Explicit per-app AUMID (e.g. "Microsoft.Windows.ControlPanel") so an explorer-hosted shell app
	// can be matched to its running window, which the AppsFolder-parse token in LaunchPath does not equal.
	public string? Aumid { get; set; }

	public string GroupName { get; set; } = "";
}
