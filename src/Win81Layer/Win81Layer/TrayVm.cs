using System;
using System.ComponentModel;
using System.Windows.Media;

namespace Win81Layer;

public sealed class TrayVm : INotifyPropertyChanged
{
	private const int CpMute = 59215;

	private const int CpVol0 = 59794;

	private const int CpVol1 = 59795;

	private const int CpVol2 = 59796;

	private const int CpVol3 = 59797;

	private const int CpWifi = 59137;

	private const int CpEthernet = 59449;

	private const int CpNetOff = 60245;

	private const int CpBattery0 = 59472;

	private const int CpBatteryCharging0 = 59483;

	private const int CpBatterySaver0 = 59494;

	private int _volumePct;

	private bool _muted;

	private bool _batteryVisible;

	private int _batteryPct;

	private bool _charging;

	private bool _saver;

	private ImageSource? _netImage = NetIcons81.Draw(NetworkIconKind.Wifi, NetworkIconState.Connected, 4, 20);

	private string _netTip = "Network";

	private bool _perfVisible;

	private string _cpuText = "";

	private string _ramText = "";

	private string _netText = "";

	public int VolumePct
	{
		get
		{
			return _volumePct;
		}
		set
		{
			value = Math.Clamp(value, 0, 100);
			if (_volumePct != value)
			{
				_volumePct = value;
				OnCh("VolumePct"); OnCh("VolumeImage"); OnCh("VolumeImagePadded"); OnCh("VolumeImageVis"); OnCh("VolumeGlyphVis");
				OnCh("VolumeGlyph");
				OnCh("VolumeTip");
			}
		}
	}

	public bool Muted
	{
		get
		{
			return _muted;
		}
		set
		{
			if (_muted != value)
			{
				_muted = value;
				OnCh("Muted"); OnCh("VolumeImage"); OnCh("VolumeImagePadded"); OnCh("VolumeImageVis"); OnCh("VolumeGlyphVis");
				OnCh("VolumeGlyph");
				OnCh("VolumeTip");
			}
		}
	}

	// Segoe MDL2 Assets codepoint for the current level/mute (mute / 0 / low / med / high). Shared by the tray glyph
	// fallback AND the flyout-header vector icon so both track the same state.
	private int VolumeCp => Muted ? 59215 : ((_volumePct == 0) ? 59794 : ((_volumePct < 34) ? 59795 : ((_volumePct < 67) ? 59796 : 59797)));

	public string VolumeGlyph => G(VolumeCp);

	private string VolumeSemantic => Muted ? "Volume.Muted" : ((_volumePct == 0) ? "Volume.Zero" : ((_volumePct < 34) ? "Volume.Low" : ((_volumePct < 67) ? "Volume.Medium" : "Volume.High")));

	// Tray box sizes at the CURRENT taskbar size. Requesting the authentic asset at these makes Win81AssetResolver pick the
	// native frame nearest the box (16/20/24) instead of the 32px master, so the icon renders near 1:1 = crisp (fixes the
	// blurry net/volume tray icons at 100% DPI). Net icon box = round(GlyphSize*1.5); volume box = GlyphSize.
	private static int VolBox => (int)Math.Round(TaskbarMetrics.GlyphSize);
	private static int NetBox => (int)Math.Round(TaskbarMetrics.GlyphSize * 1.5);

	// TRAY icon: authentic SndVolSSO speaker at FULL size (no padding) so it isn't shrunk and the wave arcs stay legible in the
	// small tray. null when the library is absent -> the MDL2 glyph (VolumeGlyph) shows instead. Resolver caches.
	public ImageSource? VolumeImage => Win81AssetResolver.GetAsset(VolumeSemantic, VolBox);

	// FLYOUT header icon (large, ~44px). The authentic SndVolSSO raster tops out at a 32px native frame, so it upscaled
	// SOFT in the big header ("θολό"). Use a crisp VECTOR of the Segoe MDL2 speaker instead — razor-sharp at any header
	// size, white to match the flyout text, and it still tracks level/mute via VolumeCp. Matches the flyout's other MDL2
	// glyph buttons (Playback / Recording / Sounds / Settings). fillFraction 0.72 restores the padding the old asset needed.
	public ImageSource? VolumeImagePadded => GlyphImage81.Get("Segoe MDL2 Assets", VolumeCp, System.Windows.Media.Colors.White, 0.72);

	public System.Windows.Visibility VolumeImageVis => VolumeImage != null ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;

	public System.Windows.Visibility VolumeGlyphVis => VolumeImage != null ? System.Windows.Visibility.Collapsed : System.Windows.Visibility.Visible;

	public string VolumeTip => Muted ? "Volume: muted" : $"Volume: {_volumePct}%";

	public bool BatteryVisible
	{
		get
		{
			return _batteryVisible;
		}
		set
		{
			if (_batteryVisible != value)
			{
				_batteryVisible = value;
				OnCh("BatteryVisible");
			}
		}
	}

	public int BatteryPct
	{
		get
		{
			return _batteryPct;
		}
		set
		{
			if (_batteryPct != value)
			{
				_batteryPct = value;
				OnCh("BatteryPct");
				OnCh("BatteryGlyph");
				OnCh("BatteryTip");
			}
		}
	}

	public bool Charging
	{
		get
		{
			return _charging;
		}
		set
		{
			if (_charging != value)
			{
				_charging = value;
				OnCh("Charging");
				OnCh("BatteryGlyph");
				OnCh("BatteryTip");
			}
		}
	}

	public bool Saver
	{
		get
		{
			return _saver;
		}
		set
		{
			if (_saver != value)
			{
				_saver = value;
				OnCh("Saver");
				OnCh("BatteryGlyph");
				OnCh("BatteryTip");
			}
		}
	}

	private int _batterySecondsLeft = -1;   // seconds of battery life remaining; -1 / sentinel = unknown or charging

	public int BatterySecondsLeft
	{
		get
		{
			return _batterySecondsLeft;
		}
		set
		{
			if (_batterySecondsLeft != value)
			{
				_batterySecondsLeft = value;
				OnCh("BatteryTip");
			}
		}
	}

	public string BatteryGlyph
	{
		get
		{
			int level = Math.Clamp((int)Math.Round((double)_batteryPct / 10.0), 0, 10);
			int baseCp = (_charging ? 59483 : (_saver ? 59494 : 59472));
			return G(baseCp + level);
		}
	}

	public string BatteryTip
	{
		get
		{
			if (_charging)
			{
				return $"Battery: {_batteryPct}% (charging)";
			}
			string saver = (_saver ? " (battery saver)" : "");
			return $"Battery: {_batteryPct}%{saver}{BatteryTimeText()}";
		}
	}

	// " - 2h 15m remaining" when on battery with a valid estimate; empty when unknown (Windows reports -1 / a huge
	// sentinel while it is still learning the rate or when charging).
	private string BatteryTimeText()
	{
		int s = _batterySecondsLeft;
		if (s <= 0 || s >= 16777215)
		{
			return "";
		}
		int h = s / 3600;
		int m = s % 3600 / 60;
		if (h > 0)
		{
			return $" - {h}h {m}m remaining";
		}
		if (m > 0)
		{
			return $" - {m}m remaining";
		}
		return "";
	}

	public ImageSource? NetImage
	{
		get
		{
			return _netImage;
		}
		set
		{
			if (_netImage != value)
			{
				_netImage = value;
				OnCh("NetImage");
			}
		}
	}

	public string NetTip
	{
		get
		{
			return _netTip;
		}
		set
		{
			if (_netTip != value)
			{
				_netTip = value;
				OnCh("NetTip");
			}
		}
	}

	public bool PerfVisible
	{
		get
		{
			return _perfVisible;
		}
		set
		{
			if (_perfVisible != value)
			{
				_perfVisible = value;
				OnCh("PerfVisible");
			}
		}
	}

	public string CpuText
	{
		get
		{
			return _cpuText;
		}
		set
		{
			if (_cpuText != value)
			{
				_cpuText = value;
				OnCh("CpuText");
			}
		}
	}

	public string RamText
	{
		get
		{
			return _ramText;
		}
		set
		{
			if (_ramText != value)
			{
				_ramText = value;
				OnCh("RamText");
			}
		}
	}

	public string NetText
	{
		get
		{
			return _netText;
		}
		set
		{
			if (_netText != value)
			{
				_netText = value;
				OnCh("NetText");
			}
		}
	}

	public event PropertyChangedEventHandler? PropertyChanged;

	private static string G(int cp)
	{
		return ((char)cp).ToString();
	}

	private void OnCh(string n)
	{
		PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
	}

	public void SetNetwork(bool up, bool wifi, bool internet = true)
	{
		SetNetwork(new NetState81(
			up ? (wifi ? NetKind.Wifi : NetKind.Ethernet) : NetKind.Offline,
			wifi ? "Wi-Fi" : "Ethernet",
			wifi && up ? 4 : 0,
			internet && up));
	}

	private NetState81 _lastNet;

	// Re-request the size-dependent tray icons when the taskbar size changes (Small/Normal/Large), so the resolver picks
	// the native frame nearest the NEW box instead of leaving a frame chosen for the old size. Called from ApplyTaskbarSize.
	public void NotifySizeChanged()
	{
		if (_lastNet != null)
		{
			SetNetwork(_lastNet);
		}
		OnCh("VolumeImage"); OnCh("VolumeImagePadded");
	}

	public void SetNetwork(NetState81 state)
	{
		_lastNet = state;
		// Prefer the AUTHENTIC extracted Windows 8.1 network icon (pnidui.dll); fall back to the vector renderer if the
		// asset library isn't present or lacks this state, so the tray icon can never break. Request at the tray box size
		// (NetBox) so the resolver returns the near-1:1 native frame (crisp) rather than a downscaled 32px master.
		NetImage = Win81AssetResolver.NetworkImage(state, NetBox) ?? NetIcons81.For(state, NetBox);
		string name = state.Kind switch
		{
			NetKind.Wifi => string.IsNullOrWhiteSpace(state.Label) ? "Wi-Fi" : state.Label,
			NetKind.Ethernet => "Ethernet",
			NetKind.Cellular => string.IsNullOrWhiteSpace(state.Label) ? "Cellular" : state.Label,
			NetKind.Airplane => "Airplane mode",
			_ => "Network"
		};
		NetTip = state.Kind == NetKind.Offline ? "No network" : $"{name}: {state.Status}";
	}
}
