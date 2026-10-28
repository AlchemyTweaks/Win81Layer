using System;

namespace Win81Layer;

// The default output's volume and mute for the tray, the OSD, Action Center, the Settings charm and the search pane: a
// facade over the one shared AudioEndpointService (no enumerator or endpoint of its own, so every surface agrees and a
// default-device change reaches all of them).
public sealed class AudioController
{
	public float GetVolume()
	{
		return AudioEndpointService.Shared.GetVolume();
	}

	public bool GetMute()
	{
		return AudioEndpointService.Shared.GetMute();
	}

	public void SetVolume(float scalar)
	{
		SettingsGuard.ThrowIfReadOnly("volume");
		AudioEndpointService.Shared.SetVolume(Math.Clamp(scalar, 0f, 1f));
	}

	public void SetMute(bool mute)
	{
		SettingsGuard.ThrowIfReadOnly("mute");
		AudioEndpointService.Shared.SetMute(mute);
	}

	public void ToggleMute()
	{
		SetMute(!GetMute());
	}

	// The default device changed: the shared endpoints are resolved again on the next read.
	public void Reset()
	{
		AudioEndpointService.Shared.Invalidate();
	}
}
