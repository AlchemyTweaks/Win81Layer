using System;
using System.ComponentModel;
using System.Windows.Media;

namespace Win81Layer;

public sealed class AudioSessionVm : INotifyPropertyChanged
{
	// The session's volume control (replaced by a refresh of the same session, SyncFrom).
	private ISimpleAudioVolume? _vol;

	private Guid _ctx = Guid.Empty;

	private bool _applying;

	private int _volumePct;

	private bool _muted;

	// When the user last set this app's level or mute (Environment.TickCount64; 0 never): a refresh read before it is older
	// than what the row shows.
	private long _userWriteMs;

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
				_userWriteMs = Environment.TickCount64;
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
			_userWriteMs = Environment.TickCount64;
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

	// The volume flyout's app row: the tooltip and the name of its mute button.
	public string MuteTip => "Mute or unmute " + Name;

	// A refresh of the same session: takes the newer control, level and mute without writing them back, so the row is kept
	// (no flicker, no slider rebuilt under a drag) and still drives the live session. readStartedMs is when the refresh
	// began reading (Environment.TickCount64): a level or mute the user set after that is newer than the refresh's, so the
	// row keeps it (a drag never snaps back to a level read before it).
	internal void SyncFrom(AudioSessionVm fresh, long readStartedMs)
	{
		_applying = true;
		try
		{
			if (fresh._vol != null)
			{
				_vol = fresh._vol;
				_ctx = fresh._ctx;
			}
			if (_userWriteMs > readStartedMs)
			{
				return;
			}
			if (_volumePct != fresh._volumePct)
			{
				_volumePct = fresh._volumePct;
				Changed("VolumePct");
			}
			if (_muted != fresh._muted)
			{
				_muted = fresh._muted;
				Changed("Muted");
				Changed("MuteGlyph");
			}
		}
		finally
		{
			_applying = false;
		}
	}

	public event PropertyChangedEventHandler? PropertyChanged;

	internal AudioSessionVm(ISimpleAudioVolume vol, string name, ImageSource? icon, int pct, bool muted)
	{
		_vol = vol;
		Name = name;
		Icon = icon;
		_volumePct = pct;
		_muted = muted;
	}

	internal static AudioSessionVm Preview(string name, int pct, bool muted = false, ImageSource? icon = null)
	{
		return new AudioSessionVm(null, name, icon, Math.Clamp(pct, 0, 100), muted);
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
