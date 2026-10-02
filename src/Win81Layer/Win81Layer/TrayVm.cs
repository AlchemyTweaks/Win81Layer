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
				OnCh("VolumePct"); OnCh("VolumeImage"); OnCh("VolumeImagePadded"); OnCh("VolumeImageVis"); OnCh("VolumeGlyphVis"); OnCh("TrayVolumeImage");
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
				OnCh("Muted"); OnCh("VolumeImage"); OnCh("VolumeImagePadded"); OnCh("VolumeImageVis"); OnCh("VolumeGlyphVis"); OnCh("TrayVolumeImage");
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

	// Volume-FLYOUT speaker box (VolumeImage = the mute button in the flyout header): the native frame nearest GlyphSize.
	private static int VolBox => (int)Math.Round(TaskbarMetrics.GlyphSize);

	// TRAY frame in PHYSICAL pixels for this bar (TaskbarMetrics.TrayIconPx: 16/20/24 at 100% DPI for Small/Medium/Large),
	// pushed by TaskbarWindow.ApplyTrayIconMetrics. The network and volume tray icons are requested at exactly this frame and
	// drawn 1:1 on an ink-centred canvas (Win81AssetResolver.GetTrayAsset), so they share the Action Center flag's size and
	// centre line. Replaces NetBox = round(GlyphSize*1.5), which fetched a 24/32 frame and shrank it into the GlyphSize box
	// (the network icon read smaller and ~1.5px higher than the speaker).
	private int _trayPx = 20;

	public void SetTrayPx(int px)
	{
		if (px > 0)
		{
			_trayPx = px;
			NotifySizeChanged();
		}
	}

	// Authentic SndVolSSO speaker for the volume FLYOUT (square native frame, full size, no padding). null when the library
	// is absent -> the MDL2 glyph (VolumeGlyph) shows instead (VolumeImageVis / VolumeGlyphVis). Resolver caches.
	public ImageSource? VolumeImage => Win81AssetResolver.GetAsset(VolumeSemantic, VolBox);

	// TRAY speaker: the same state as the 1:1 ink-centred tray canvas at _trayPx. Separate from VolumeImage so the flyout's
	// square speaker is unaffected. Null exactly when VolumeImage is null (same manifest entry).
	public ImageSource? TrayVolumeImage => Win81AssetResolver.GetTrayAsset(VolumeSemantic, _trayPx);

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

	public string BatteryGlyph => G(BatteryCodepoint(_batteryPct, _charging, _saver));

	// Segoe MDL2 Assets battery codepoint for level = round(pct/10) (0-10). The font's tables are NOT contiguous at the top:
	// Battery0-9 = E850-E859 but Battery10 = E83F; BatteryCharging0-8 = E85A-E862, Charging9 = E83E, Charging10 = EA93;
	// BatterySaver0-8 = E863-E86B, Saver9 = EA94, Saver10 = EA95. (The old base+level math drew the charging plug for a full
	// battery, was off by one for charging and by three for saver, where levels 6-10 drew cell-signal bars.) Shared with
	// the Charms clock so both surfaces use one table.
	internal static int BatteryCodepoint(int pct, bool charging, bool saver)
	{
		int level = Math.Clamp((int)Math.Round((double)pct / 10.0), 0, 10);
		if (charging)
		{
			return (level < 9) ? (0xE85A + level) : ((level == 9) ? 0xE83E : 0xEA93);
		}
		if (saver)
		{
			return (level < 9) ? (0xE863 + level) : ((level == 9) ? 0xEA94 : 0xEA95);
		}
		return (level < 10) ? (0xE850 + level) : 0xE83F;
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

	// Re-request the size-dependent tray icons for the current tray frame (_trayPx: taskbar size or monitor DPI changed).
	// Called from SetTrayPx. Before the first network read the vector placeholder is redrawn at the new frame too.
	public void NotifySizeChanged()
	{
		if (_lastNet != null)
		{
			SetNetwork(_lastNet);
		}
		else
		{
			NetImage = NetIcons81.Draw(NetworkIconKind.Wifi, NetworkIconState.Connected, 4, _trayPx);
		}
		OnCh("VolumeImage"); OnCh("VolumeImagePadded"); OnCh("TrayVolumeImage");
	}

	public void SetNetwork(NetState81 state)
	{
		_lastNet = state;
		// Prefer the AUTHENTIC extracted Windows 8.1 network icon (pnidui.dll); fall back to the vector renderer if the
		// asset library isn't present or lacks this state, so the tray icon can never break. Requested at the bar's tray
		// frame (_trayPx) as the 1:1 ink-centred tray canvas, so it matches the speaker and flag in size and centre line.
		NetImage = Win81AssetResolver.NetworkTrayImage(state, _trayPx) ?? NetIcons81.For(state, _trayPx);
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
