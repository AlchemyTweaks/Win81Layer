using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;

#nullable enable annotations

namespace Win81Layer;

// The one Core Audio service of the launcher: the tray, the volume OSD, the sound flyout, Action Center, the Settings
// charm, the search pane and PC Settings all go through it (AudioController is a thin facade over it). The default
// render and capture endpoints are resolved once and again only after Windows reports a default-device change (the old
// per-instance controllers kept the endpoint they first saw: the stale-endpoint bug). While at least one subscriber
// listens (the live tray always does), the volume and mute come from IAudioEndpointVolumeCallback and device changes from
// IMMNotificationClient, so reading the tray's level is a field read, not a COM call per tick.
internal sealed class AudioEndpointService
{
	internal static readonly AudioEndpointService Shared = new AudioEndpointService();

	private static readonly Guid CLSID_MMDeviceEnumerator = new Guid("BCDE0395-E52F-467C-8E3D-C4579291692E");

	private static readonly Guid CLSID_PolicyConfigClient = new Guid("870af99c-171d-4f9e-af0d-e63df40c2bc9");

	private static readonly Guid IID_IAudioEndpointVolume = new Guid("5CDF2C82-841E-4546-9722-0CF74078229A");

	private static readonly Guid IID_IAudioMeterInformation = new Guid("C02216F6-8C67-4B5B-9D00-D008E73E0064");

	private static readonly Guid IID_IAudioSessionManager2 = new Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F");

	private readonly object _gate = new object();

	private sealed class Endpoint
	{
		internal string Id = "";

		internal IAudioEndpointVolume? Volume;

		internal IAudioMeterInformation? Meter;

		internal VolumeCallback? Callback;

		internal nint CallbackPtr;

		internal volatile bool Stale = true;

		// The cached level (0..1, -1 unknown) and mute, kept by the callback while one is registered.
		internal float Level = -1f;

		internal bool Muted;

		// When it was resolved (Environment.TickCount64).
		internal long ResolvedAt;
	}

	private readonly Endpoint _render = new Endpoint();

	private readonly Endpoint _capture = new Endpoint();

	private IMMDeviceEnumeratorFull? _enumerator;

	private NotificationClient? _notify;

	private int _subscribers;

	private Guid _ctx = Guid.Empty;

	private static long _comReads;

	// COM volume/mute reads made by this process (QA: a live, subscribed tray reads none per tick).
	internal static long ComReads => Interlocked.Read(ref _comReads);

	// Raised on a Core Audio thread when the default device, the device list or the level of a default endpoint changed.
	// Subscribers marshal to their own dispatcher.
	internal event Action? Changed;

	// Raised for device events only (a device came or went, a default changed): not for every volume step.
	internal event Action? DevicesChanged;

	private AudioEndpointService()
	{
	}

	// ---- Subscription -------------------------------------------------------------------------------------------------

	// While at least one subscriber listens, the default endpoints notify the service; the last one leaving unregisters.
	// devicesOnly: the subscriber hears device events only (PC settings' Sound page), not volume and mute changes.
	internal IDisposable Subscribe(Action changed, bool devicesOnly = false)
	{
		lock (_gate)
		{
			if (devicesOnly)
			{
				DevicesChanged += changed;
			}
			else
			{
				Changed += changed;
			}
			_subscribers++;
			if (_subscribers == 1)
			{
				RegisterNotifications();
			}
		}
		return new Subscription(this, changed, devicesOnly);
	}

	private sealed class Subscription : IDisposable
	{
		private AudioEndpointService? _owner;

		private readonly Action _changed;

		private readonly bool _devicesOnly;

		internal Subscription(AudioEndpointService owner, Action changed, bool devicesOnly)
		{
			_owner = owner;
			_changed = changed;
			_devicesOnly = devicesOnly;
		}

		public void Dispose()
		{
			AudioEndpointService? o = Interlocked.Exchange(ref _owner, null);
			o?.Unsubscribe(_changed, _devicesOnly);
		}
	}

	private void Unsubscribe(Action changed, bool devicesOnly)
	{
		lock (_gate)
		{
			if (devicesOnly)
			{
				DevicesChanged -= changed;
				// the microphone endpoint was opened for that page (its volume and level meter): let it go with it
				Release(_capture);
			}
			else
			{
				Changed -= changed;
			}
			_subscribers = Math.Max(0, _subscribers - 1);
			if (_subscribers == 0)
			{
				UnregisterNotifications();
			}
		}
	}

	internal int Subscribers
	{
		get
		{
			lock (_gate)
			{
				return _subscribers;
			}
		}
	}

	// A fresh enumerator for one enumeration off the UI thread (the shared one serves the default endpoints and the
	// notifications).
	private static IMMDeviceEnumeratorFull? NewEnumerator()
	{
		try
		{
			Type? t = Type.GetTypeFromCLSID(CLSID_MMDeviceEnumerator);
			return t == null ? null : (IMMDeviceEnumeratorFull?)Activator.CreateInstance(t);
		}
		catch
		{
			return null;
		}
	}

	private IMMDeviceEnumeratorFull? Enumerator()
	{
		if (_enumerator != null)
		{
			return _enumerator;
		}
		try
		{
			Type? t = Type.GetTypeFromCLSID(CLSID_MMDeviceEnumerator);
			if (t == null)
			{
				return null;
			}
			_enumerator = (IMMDeviceEnumeratorFull?)Activator.CreateInstance(t);
		}
		catch (Exception ex)
		{
			Logger.Log("Audio service: enumerator: " + ex.Message);
			_enumerator = null;
		}
		return _enumerator;
	}

	private void RegisterNotifications()
	{
		try
		{
			IMMDeviceEnumeratorFull? en = Enumerator();
			if (en != null && _notify == null)
			{
				NotificationClient n = new NotificationClient(this);
				if (en.RegisterEndpointNotificationCallback(n) == 0)
				{
					_notify = n;
				}
			}
		}
		catch (Exception ex)
		{
			Logger.Log("Audio service: device notifications: " + ex.Message);
		}
		_render.Stale = true;
		_capture.Stale = true;
	}

	private void UnregisterNotifications()
	{
		try
		{
			if (_notify != null)
			{
				_enumerator?.UnregisterEndpointNotificationCallback(_notify);
			}
		}
		catch
		{
		}
		_notify = null;
		Release(_render);
		Release(_capture);
	}

	private void Release(Endpoint e)
	{
		try
		{
			if (e.Volume != null && e.CallbackPtr != IntPtr.Zero)
			{
				e.Volume.UnregisterControlChangeNotify(e.CallbackPtr);
			}
		}
		catch
		{
		}
		if (e.CallbackPtr != IntPtr.Zero)
		{
			try
			{
				Marshal.Release(e.CallbackPtr);
			}
			catch
			{
			}
		}
		e.CallbackPtr = IntPtr.Zero;
		e.Callback = null;
		e.Volume = null;
		e.Meter = null;
		e.Level = -1f;
		e.Stale = true;
	}

	// The default endpoint changed (or a device came or went): resolved again on the next use.
	internal void Invalidate()
	{
		lock (_gate)
		{
			Release(_render);
			Release(_capture);
		}
	}

	private void OnDeviceEvent(bool defaultChanged)
	{
		if (defaultChanged)
		{
			_render.Stale = true;
			_capture.Stale = true;
		}
		try
		{
			Changed?.Invoke();
		}
		catch
		{
		}
		try
		{
			DevicesChanged?.Invoke();
		}
		catch
		{
		}
	}

	private void OnVolumeEvent(Endpoint e, float level, bool muted)
	{
		e.Level = level;
		e.Muted = muted;
		try
		{
			Changed?.Invoke();
		}
		catch
		{
		}
	}

	// ---- The default endpoints ----------------------------------------------------------------------------------------

	// Without a subscriber nobody hears device events: the endpoint is resolved again when it is older than this, so a
	// removed or changed default device is never used for long.
	private const long UnwatchedMaxAgeMs = 2000;

	private Endpoint? Resolve(bool capture)
	{
		Endpoint e = capture ? _capture : _render;
		lock (_gate)
		{
			bool watched = _subscribers > 0 && e.CallbackPtr != IntPtr.Zero;
			if (!e.Stale && e.Volume != null && (watched || Environment.TickCount64 - e.ResolvedAt < UnwatchedMaxAgeMs || (_subscribers > 0 && capture)))
			{
				return e;
			}
			Release(e);
			try
			{
				IMMDeviceEnumeratorFull? en = Enumerator();
				if (en == null || en.GetDefaultAudioEndpoint(capture ? 1 : 0, 1, out IMMDevice dev) != 0 || dev == null)
				{
					return null;
				}
				dev.GetId(out string id);
				Guid iid = IID_IAudioEndpointVolume;
				if (dev.Activate(ref iid, 1, IntPtr.Zero, out object o) != 0)
				{
					return null;
				}
				e.Id = id ?? "";
				e.Volume = (IAudioEndpointVolume)o;
				Guid miid = IID_IAudioMeterInformation;
				try
				{
					if (dev.Activate(ref miid, 1, IntPtr.Zero, out object m) == 0)
					{
						e.Meter = m as IAudioMeterInformation;
					}
				}
				catch
				{
				}
				e.Stale = false;
				e.ResolvedAt = Environment.TickCount64;
				// Only the output endpoint is watched (the tray's level and mute); the microphone is read when asked, so
				// nothing wakes the shell for it once PC settings' Sound page is gone.
				if (_subscribers > 0 && !capture)
				{
					VolumeCallback cb = new VolumeCallback(this, e);
					nint ptr = Marshal.GetComInterfaceForObject(cb, typeof(IAudioEndpointVolumeCallback));
					if (e.Volume.RegisterControlChangeNotify(ptr) == 0)
					{
						e.Callback = cb;
						e.CallbackPtr = ptr;
						Interlocked.Increment(ref _comReads);
						if (e.Volume.GetMasterVolumeLevelScalar(out float lv) == 0 && e.Volume.GetMute(out bool mu) == 0)
						{
							e.Level = lv;
							e.Muted = mu;
						}
					}
					else
					{
						Marshal.Release(ptr);
					}
				}
				return e;
			}
			catch (Exception ex)
			{
				Logger.Log("Audio service: default endpoint: " + ex.Message);
				Release(e);
				return null;
			}
		}
	}

	internal float GetVolume(bool capture = false)
	{
		Endpoint? e = Resolve(capture);
		if (e?.Volume == null)
		{
			return 0f;
		}
		if (e.CallbackPtr != IntPtr.Zero && e.Level >= 0f)
		{
			return e.Level;
		}
		try
		{
			Interlocked.Increment(ref _comReads);
			// [PreserveSig]: a failure (AUDCLNT_E_DEVICE_INVALIDATED) is a return code, not an exception
			if (e.Volume.GetMasterVolumeLevelScalar(out float v) != 0)
			{
				Invalidate();
				return 0f;
			}
			return v;
		}
		catch
		{
			Invalidate();
			return 0f;
		}
	}

	internal bool GetMute(bool capture = false)
	{
		Endpoint? e = Resolve(capture);
		if (e?.Volume == null)
		{
			return false;
		}
		if (e.CallbackPtr != IntPtr.Zero && e.Level >= 0f)
		{
			return e.Muted;
		}
		try
		{
			Interlocked.Increment(ref _comReads);
			if (e.Volume.GetMute(out bool m) != 0)
			{
				Invalidate();
				return false;
			}
			return m;
		}
		catch
		{
			Invalidate();
			return false;
		}
	}

	// True when the endpoint exists (QA and the pages tell "no device" from "muted at 0").
	internal bool HasEndpoint(bool capture) => Resolve(capture)?.Volume != null;

	internal void SetVolume(float scalar, bool capture = false)
	{
		SettingsGuard.ThrowIfReadOnly(capture ? "input volume" : "volume");
		Endpoint? e = Resolve(capture);
		if (e?.Volume == null)
		{
			return;
		}
		scalar = Math.Clamp(scalar, 0f, 1f);
		try
		{
			// the cache follows only a write Windows took; a failed one drops the endpoint (resolved again next time)
			if (e.Volume.SetMasterVolumeLevelScalar(scalar, ref _ctx) != 0)
			{
				Invalidate();
				return;
			}
			if (e.CallbackPtr != IntPtr.Zero)
			{
				e.Level = scalar;
			}
		}
		catch
		{
			Invalidate();
		}
	}

	internal void SetMute(bool mute, bool capture = false)
	{
		SettingsGuard.ThrowIfReadOnly(capture ? "input mute" : "mute");
		Endpoint? e = Resolve(capture);
		if (e?.Volume == null)
		{
			return;
		}
		try
		{
			if (e.Volume.SetMute(mute, ref _ctx) != 0)
			{
				Invalidate();
				return;
			}
			if (e.CallbackPtr != IntPtr.Zero)
			{
				e.Muted = mute;
			}
		}
		catch
		{
			Invalidate();
		}
	}

	internal float GetPeak(bool capture)
	{
		Endpoint? e = Resolve(capture);
		if (e?.Meter == null)
		{
			return 0f;
		}
		try
		{
			e.Meter.GetPeakValue(out float p);
			return p;
		}
		catch
		{
			return 0f;
		}
	}

	// ---- Devices ------------------------------------------------------------------------------------------------------

	// The active endpoints of one direction with the console and communications defaults.
	internal List<AudioEndpointInfo> Endpoints(bool capture)
	{
		List<AudioEndpointInfo> list = new List<AudioEndpointInfo>();
		try
		{
			IMMDeviceEnumeratorFull? en = NewEnumerator();
			if (en == null)
			{
				return list;
			}
			int flow = capture ? 1 : 0;
			string console = DefaultId(en, flow, 0);
			string comms = DefaultId(en, flow, 2);
			if (en.EnumAudioEndpoints(flow, 1, out IMMDeviceCollection col) != 0 || col == null)
			{
				return list;
			}
			col.GetCount(out int n);
			for (int i = 0; i < n; i++)
			{
				if (col.Item(i, out IMMDevice dev) != 0 || dev == null)
				{
					continue;
				}
				dev.GetId(out string id);
				list.Add(new AudioEndpointInfo(id, AudioDevices.FriendlyNameOf(dev) ?? id, id == console, id == comms));
			}
		}
		catch (Exception ex)
		{
			Logger.Log("Audio service: endpoints: " + ex.Message);
		}
		return list;
	}

	private static string DefaultId(IMMDeviceEnumeratorFull en, int flow, int role)
	{
		try
		{
			if (en.GetDefaultAudioEndpoint(flow, role, out IMMDevice d) == 0 && d != null)
			{
				d.GetId(out string id);
				return id ?? "";
			}
		}
		catch
		{
		}
		return "";
	}

	// The default device for the console and multimedia roles, or for communications alone (so calls are never moved
	// silently). Uses the shipped IPolicyConfig (undocumented); an exception reaches the caller.
	internal void SetDefault(string id, bool communications)
	{
		SettingsGuard.ThrowIfReadOnly("default audio device");
		Type? t = Type.GetTypeFromCLSID(CLSID_PolicyConfigClient);
		if (t == null)
		{
			throw new InvalidOperationException("the audio policy service is not available");
		}
		IPolicyConfig pc = (IPolicyConfig)Activator.CreateInstance(t)!;
		if (communications)
		{
			Marshal.ThrowExceptionForHR(pc.SetDefaultEndpoint(id, 2));
		}
		else
		{
			Marshal.ThrowExceptionForHR(pc.SetDefaultEndpoint(id, 0));
			Marshal.ThrowExceptionForHR(pc.SetDefaultEndpoint(id, 1));
		}
		Invalidate();
	}

	// ---- App sessions -------------------------------------------------------------------------------------------------

	private readonly Dictionary<string, ISimpleAudioVolume> _sessions = new Dictionary<string, ISimpleAudioVolume>(StringComparer.Ordinal);

	internal List<AppVolumeInfo> Sessions()
	{
		List<AppVolumeInfo> list = new List<AppVolumeInfo>();
		lock (_sessions)
		{
			_sessions.Clear();
		}
		try
		{
			IMMDeviceEnumeratorFull? en = NewEnumerator();
			if (en == null || en.GetDefaultAudioEndpoint(0, 1, out IMMDevice dev) != 0 || dev == null)
			{
				return list;
			}
			Guid iid = IID_IAudioSessionManager2;
			if (dev.Activate(ref iid, 1, IntPtr.Zero, out object o) != 0)
			{
				return list;
			}
			IAudioSessionManager2 mgr = (IAudioSessionManager2)o;
			if (mgr.GetSessionEnumerator(out IAudioSessionEnumerator sessions) != 0 || sessions.GetCount(out int count) != 0)
			{
				return list;
			}
			HashSet<int> seen = new HashSet<int>();
			for (int i = 0; i < count; i++)
			{
				if (sessions.GetSession(i, out IAudioSessionControl2 ctl) != 0 || ctl == null || (ctl.GetState(out int state) == 0 && state == 2))
				{
					continue;
				}
				bool system = ctl.IsSystemSoundsSession() == 0;
				ctl.GetProcessId(out int pid);
				if (!system && pid != 0 && !seen.Add(pid))
				{
					continue;
				}
				if (ctl is not ISimpleAudioVolume vol)
				{
					continue;
				}
				vol.GetMasterVolume(out float level);
				vol.GetMute(out bool mute);
				string key = system ? "system" : "pid:" + pid;
				string name = system ? "System sounds" : ProcessName(pid);
				lock (_sessions)
				{
					_sessions[key] = vol;
				}
				list.Add(new AppVolumeInfo(key, name, (int)Math.Round(level * 100f), mute));
			}
		}
		catch (Exception ex)
		{
			Logger.Log("Audio service: sessions: " + ex.Message);
		}
		return list.OrderBy((AppVolumeInfo a) => a.Key == "system" ? 0 : 1).ThenBy((AppVolumeInfo a) => a.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
	}

	private static string ProcessName(int pid)
	{
		try
		{
			using Process p = Process.GetProcessById(pid);
			try
			{
				string? file = p.MainModule?.FileName;
				if (file != null)
				{
					string fd = FileVersionInfo.GetVersionInfo(file).FileDescription ?? "";
					if (!string.IsNullOrWhiteSpace(fd))
					{
						return fd;
					}
				}
			}
			catch
			{
			}
			return p.ProcessName;
		}
		catch
		{
			return "App";
		}
	}

	internal void SetSessionVolume(string key, int percent)
	{
		SettingsGuard.ThrowIfReadOnly("app volume");
		ISimpleAudioVolume? v;
		lock (_sessions)
		{
			_sessions.TryGetValue(key, out v);
		}
		v?.SetMasterVolume(Math.Clamp(percent, 0, 100) / 100f, ref _ctx);
	}

	internal void SetSessionMute(string key, bool mute)
	{
		SettingsGuard.ThrowIfReadOnly("app mute");
		ISimpleAudioVolume? v;
		lock (_sessions)
		{
			_sessions.TryGetValue(key, out v);
		}
		v?.SetMute(mute, ref _ctx);
	}

	// ---- COM callbacks ------------------------------------------------------------------------------------------------

	[ComVisible(true)]
	[ClassInterface(ClassInterfaceType.None)]
	private sealed class VolumeCallback : IAudioEndpointVolumeCallback
	{
		private readonly AudioEndpointService _owner;

		private readonly Endpoint _endpoint;

		internal VolumeCallback(AudioEndpointService owner, Endpoint endpoint)
		{
			_owner = owner;
			_endpoint = endpoint;
		}

		public int OnNotify(nint data)
		{
			try
			{
				// AUDIO_VOLUME_NOTIFICATION_DATA: GUID guidEventContext; BOOL bMuted; float fMasterVolume; ...
				bool muted = Marshal.ReadInt32(data, 16) != 0;
				float level = BitConverter.Int32BitsToSingle(Marshal.ReadInt32(data, 20));
				_owner.OnVolumeEvent(_endpoint, level, muted);
			}
			catch
			{
			}
			return 0;
		}
	}

	[ComVisible(true)]
	[ClassInterface(ClassInterfaceType.None)]
	private sealed class NotificationClient : IMMNotificationClient
	{
		private readonly AudioEndpointService _owner;

		internal NotificationClient(AudioEndpointService owner)
		{
			_owner = owner;
		}

		public int OnDeviceStateChanged(string deviceId, int newState)
		{
			_owner.OnDeviceEvent(defaultChanged: true);
			return 0;
		}

		public int OnDeviceAdded(string deviceId)
		{
			_owner.OnDeviceEvent(defaultChanged: false);
			return 0;
		}

		public int OnDeviceRemoved(string deviceId)
		{
			_owner.OnDeviceEvent(defaultChanged: true);
			return 0;
		}

		public int OnDefaultDeviceChanged(int flow, int role, string? defaultDeviceId)
		{
			_owner.OnDeviceEvent(defaultChanged: true);
			return 0;
		}

		public int OnPropertyValueChanged(string deviceId, AUDIO_PROPERTYKEY key)
		{
			return 0;
		}
	}
}

[ComImport]
[Guid("A95664D2-9614-4F35-A746-DE8DB63617E6")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMMDeviceEnumeratorFull
{
	[PreserveSig]
	int EnumAudioEndpoints(int dataFlow, int stateMask, out IMMDeviceCollection devices);

	[PreserveSig]
	int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice ppDevice);

	[PreserveSig]
	int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice device);

	[PreserveSig]
	int RegisterEndpointNotificationCallback(IMMNotificationClient client);

	[PreserveSig]
	int UnregisterEndpointNotificationCallback(IMMNotificationClient client);
}

[ComImport]
[Guid("7991EEC9-7E89-4D85-8390-6C703CEC60C0")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMMNotificationClient
{
	[PreserveSig]
	int OnDeviceStateChanged([MarshalAs(UnmanagedType.LPWStr)] string deviceId, int newState);

	[PreserveSig]
	int OnDeviceAdded([MarshalAs(UnmanagedType.LPWStr)] string deviceId);

	[PreserveSig]
	int OnDeviceRemoved([MarshalAs(UnmanagedType.LPWStr)] string deviceId);

	[PreserveSig]
	int OnDefaultDeviceChanged(int flow, int role, [MarshalAs(UnmanagedType.LPWStr)] string? defaultDeviceId);

	[PreserveSig]
	int OnPropertyValueChanged([MarshalAs(UnmanagedType.LPWStr)] string deviceId, AUDIO_PROPERTYKEY key);
}

[ComImport]
[Guid("657804FA-D6AD-4496-8A60-352752AF4F89")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioEndpointVolumeCallback
{
	[PreserveSig]
	int OnNotify(nint data);
}

[ComImport]
[Guid("C02216F6-8C67-4B5B-9D00-D008E73E0064")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioMeterInformation
{
	[PreserveSig]
	int GetPeakValue(out float peak);
}
