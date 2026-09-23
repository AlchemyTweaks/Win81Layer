using System;
using System.ComponentModel;
using System.Windows.Media;

namespace Win81Layer;

public sealed class AudioSessionVm : INotifyPropertyChanged
{
	private readonly ISimpleAudioVolume? _vol;

	private Guid _ctx = Guid.Empty;

	private bool _applying;

	private int _volumePct;

	private bool _muted;

	public string Name { get; }

	public ImageSource? Icon { get; }

	public int VolumePct
	{
		get
		{
			return _volumePct;
		}
		set
		{
			value = Math.Clamp(value, 0, 100);
			if (_volumePct == value)
			{
				return;
			}
			_volumePct = value;
			Changed("VolumePct");
			if (!_applying)
			{
				try
				{
					_vol?.SetMasterVolume((float)value / 100f, ref _ctx);
				}
				catch
				{
				}
				if (value > 0 && _muted)
				{
					Muted = false;
				}
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
			if (_muted == value)
			{
				return;
			}
			_muted = value;
			Changed("Muted");
			Changed("MuteGlyph");
			try
			{
				_vol?.SetMute(value, ref _ctx);
			}
			catch
			{
			}
		}
	}

	public string MuteGlyph => _muted ? "\ue74f" : "\ue767";

	public event PropertyChangedEventHandler? PropertyChanged;

	internal AudioSessionVm(ISimpleAudioVolume vol, string name, ImageSource? icon, int pct, bool muted)
	{
		_vol = vol;
		Name = name;
		Icon = icon;
		_volumePct = pct;
		_muted = muted;
	}

	internal static AudioSessionVm Preview(string name, int pct, bool muted = false)
	{
		return new AudioSessionVm(null, name, null, Math.Clamp(pct, 0, 100), muted);
	}

	public void Toggle()
	{
		Muted = !_muted;
	}

	private void Changed(string p)
	{
		PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(p));
	}
}
