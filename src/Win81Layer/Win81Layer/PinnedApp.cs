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

	// The shortcut's own "Start in" folder, for pins taken from a .lnk whose program needs it (e.g. a script host run
	// with a relative script path). Null means the exe's folder, as before; omitted from the JSON when null so existing
	// pin files keep their exact shape.
	[System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
	public string? WorkingDir { get; set; }
}
