using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Media;

namespace Win81Layer;

public sealed class TrayAppIcon : INotifyPropertyChanged
{
	public nint OwnerHwnd;

	public uint Id;

	public uint CallbackMessage;

	// NOTIFYICON version the icon registered (from NIM_SETVERSION); 0 = legacy. Selects the click-forward convention.
	public uint Version;

	public nint HIcon;

	public bool Hidden;

	// True for the repurposed system audio/volume indicator (explorer-owned, no callback). Left-click cycles the default
	// output device; right-click opens a device picker. Distinguishes it from real forwardable tray icons.
	public bool IsAudioSwitcher;

	public string StableKey = "";

	// Win11 registry-sourced icons have no owner HWND/callback; a click activates or launches this executable instead
	// of forwarding a tray message. Null for legacy Win8.1/Win10 icons (which use OwnerHwnd + CallbackMessage).
	public string? ExecutablePath;

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

	public string Key => $"{((IntPtr)OwnerHwnd).ToInt64():X}:{Id}";

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
