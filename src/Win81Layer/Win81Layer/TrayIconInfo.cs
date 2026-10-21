using System.Windows.Media;

namespace Win81Layer;

public sealed class TrayIconInfo
{
	public nint OwnerHwnd;

	public uint Id;

	public uint CallbackMessage;

	// NOTIFYICON version the icon registered (from NIM_SETVERSION); 0 = legacy. Selects the click-forward convention.
	public uint Version;

	// Version is real: the tray host saw every NIM_SETVERSION. False for the legacy toolbar source, which reads no
	// version (clicks then use TrayProtocol.EventsUnknownVersion).
	public bool VersionKnown;

	public nint HIcon;

	public string Tooltip = "";

	public bool Hidden;

	public bool FromOverflow;

	// The icon's guidItem when it was registered with NIF_GUID (then it, not the id, identifies the icon), else empty.
	public System.Guid Guid;

	// The host saw this icon's NIM_MODIFY / NIM_SETVERSION but never its NIM_ADD (it may have no callback yet).
	public bool Orphan;

	// Win11 registry path (Win11TrayReader): the icon is pre-decoded (no HICON), there is no owner window/callback, and
	// clicks activate/launch ExecutablePath instead of forwarding. Null on the legacy Win8.1/Win10 path.
	public ImageSource? Image;

	public string? ExecutablePath;

	public string? StableId;
}
