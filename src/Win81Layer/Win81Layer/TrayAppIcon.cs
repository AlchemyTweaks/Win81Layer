using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Media;

namespace Win81Layer;

public sealed class TrayAppIcon : INotifyPropertyChanged
{
	public nint OwnerHwnd;

	public uint Id;

	// The icon's guidItem when it was registered with NIF_GUID (two guid icons may share a window and id), else empty.
	public Guid Guid;

	public uint CallbackMessage;

	// NOTIFYICON version the icon registered (from NIM_SETVERSION); 0 = legacy. Selects the click-forward convention.
	public uint Version;

	// False when the source could not tell the version (TrayIconInfo.VersionKnown).
	public bool VersionKnown = true;

	public nint HIcon;

	public bool Hidden;

	// True for the repurposed system audio/volume indicator (explorer-owned, no callback). Left-click cycles the default
	// output device; right-click opens a device picker. Distinguishes it from real forwardable tray icons.
	public bool IsAudioSwitcher;

	public string StableKey = "";

	// Win11 registry-sourced icons have no owner HWND/callback; a click activates or launches this executable instead
	// of forwarding a tray message. Null for legacy Win8.1/Win10 icons (which use OwnerHwnd + CallbackMessage).
	public string? ExecutablePath;

	// Native-size icon as received (HICON / registry snapshot / fallback), before fitting to the tray box. Image is
	// TrayIconFit.Fit(RawImage, app box px); kept so a taskbar size or DPI change can re-fit without a new HICON.
	public ImageSource? RawImage;

	// RawImage is a stand-in, not the app's own tray image: the owner window's or program's icon (the tray HICON could
	// not be read), a letter tile, or a registry snapshot. It says nothing about the app's state (SeasonalTrayIcons).
	internal bool RawSubstitute;

	// The seasonal app icon shown instead of the live image (SeasonalAppIcons), or null. RawImage stays the app's own.
	public string SeasonalKey;

	// What the state-aware seasonal tray logic last saw and decided for this icon (SeasonalTrayIcons), or null.
	internal SeasonalTrayLive SeasonalTray;

	// The launcher's own icon, which never takes a seasonal icon.
	public bool IsOwn;

	private ImageSource? _image;

	private UIElement? _hostElement;

	private string _tooltip = "";

	// When set, this tray entry renders the given live control (a system flyout button: sound / network / clock /
	// notifications) INSTEAD of an icon image, so the fixed system buttons can live in the same reorderable strip as the
	// app icons. HostElement items reuse all the existing drag/order/persistence logic; their own click handlers run
	// (OnAppIconClick/RightClick early-return for them).
	public UIElement? HostElement
	{
		get => _hostElement;
		set
		{
			_hostElement = value;
			OnCh("HostElement");
			OnCh("HasHost");
		}
	}

	public bool HasHost => _hostElement != null;

	private int _dropHint;

	public string Key => KeyOf(OwnerHwnd, Id, Guid);

	public static string KeyOf(nint owner, uint id, Guid guid)
	{
		return (guid == Guid.Empty) ? $"{((IntPtr)owner).ToInt64():X}:{id}" : $"{((IntPtr)owner).ToInt64():X}:{id}:{guid:N}";
	}

	public ImageSource? Image
	{
		get
		{
			return _image;
		}
		set
		{
			_image = value;
			OnCh("Image");
		}
	}

	public string Tooltip
	{
		get
		{
			return _tooltip;
		}
		set
		{
			if (_tooltip != value)
			{
				_tooltip = value;
				OnCh("Tooltip");
			}
		}
	}

	public int DropHint
	{
		get
		{
			return _dropHint;
		}
		set
		{
			if (_dropHint != value)
			{
				_dropHint = value;
				OnCh("DropHint");
			}
		}
	}

	public event PropertyChangedEventHandler? PropertyChanged;

	private void OnCh(string n)
	{
		PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
	}
}
