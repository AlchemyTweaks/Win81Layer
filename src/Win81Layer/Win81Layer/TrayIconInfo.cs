using System.Windows.Media;

namespace Win81Layer;

public sealed class TrayIconInfo
{
	public nint OwnerHwnd;

	public uint Id;

	public uint CallbackMessage;

	// NOTIFYICON version the icon registered (from NIM_SETVERSION); 0 = legacy. Selects the click-forward convention.
	public uint Version;

	public nint HIcon;

	public string Tooltip = "";

	public bool Hidden;

	public bool FromOverflow;

	// Win11 registry path (Win11TrayReader): the icon is pre-decoded (no HICON), there is no owner window/callback, and
	// clicks activate/launch ExecutablePath instead of forwarding. Null on the legacy Win8.1/Win10 path.
	public ImageSource? Image;

	public string? ExecutablePath;

	public string? StableId;
}
