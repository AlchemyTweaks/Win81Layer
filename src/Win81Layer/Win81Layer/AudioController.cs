using System;

namespace Win81Layer;

public sealed class AudioController
{
	private static readonly Guid CLSID_MMDeviceEnumerator = new Guid("BCDE0395-E52F-467C-8E3D-C4579291692E");

	private static readonly Guid IID_IAudioEndpointVolume = new Guid("5CDF2C82-841E-4546-9722-0CF74078229A");

	private IAudioEndpointVolume? _vol;

	private Guid _noCtx = Guid.Empty;

	private bool Ensure()
	{
		if (_vol != null)
		{
			return true;
		}
		try
		{
			Type t = Type.GetTypeFromCLSID(CLSID_MMDeviceEnumerator);
			if (t == null)
			{
				return false;
			}
			IMMDeviceEnumerator enumr = (IMMDeviceEnumerator)Activator.CreateInstance(t);
			if (enumr.GetDefaultAudioEndpoint(0, 0, out IMMDevice dev) != 0 || dev == null)
			{
				return false;
			}
			Guid iid = IID_IAudioEndpointVolume;
			if (dev.Activate(ref iid, 1, IntPtr.Zero, out object o) != 0)
			{
				return false;
			}
			_vol = (IAudioEndpointVolume)o;
			return true;
		}
		catch
		{
			_vol = null;
			return false;
		}
	}

	public float GetVolume()
	{
		if (!Ensure())
		{
			return 0f;
		}
		try
		{
			_vol.GetMasterVolumeLevelScalar(out var v);
			return v;
		}
		catch
		{
			_vol = null;
			return 0f;
		}
	}

	public bool GetMute()
	{
		if (!Ensure())
		{
			return false;
		}
		try
		{
			_vol.GetMute(out var m);
			return m;
		}
		catch
		{
			_vol = null;
			return false;
		}
	}

	public void SetVolume(float scalar)
	{
		if (!Ensure())
		{
			return;
		}
		scalar = Math.Clamp(scalar, 0f, 1f);
		try
		{
			_vol.SetMasterVolumeLevelScalar(scalar, ref _noCtx);
		}
		catch
		{
			_vol = null;
		}
	}

	public void SetMute(bool mute)
	{
		if (!Ensure())
		{
			return;
		}
		try
		{
			_vol.SetMute(mute, ref _noCtx);
		}
		catch
		{
			_vol = null;
		}
	}

	public void ToggleMute()
	{
		SetMute(!GetMute());
	}

	public void Reset()
	{
		_vol = null;
	}
}
