using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace Win81Layer;

public static class AudioDevices
{
	private static readonly Guid CLSID_MMDeviceEnumerator = new Guid("BCDE0395-E52F-467C-8E3D-C4579291692E");

	private static readonly Guid CLSID_PolicyConfigClient = new Guid("870af99c-171d-4f9e-af0d-e63df40c2bc9");

	private static readonly AUDIO_PROPERTYKEY PKEY_Device_FriendlyName = new AUDIO_PROPERTYKEY
	{
		fmtid = new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"),
		pid = 14
	};

	private const int DEVICE_STATE_ACTIVE = 1;

	private const int STGM_READ = 0;

	public static List<AudioDevice> Enumerate(bool capture)
	{
		List<AudioDevice> result = new List<AudioDevice>();
		try
		{
			Type t = Type.GetTypeFromCLSID(CLSID_MMDeviceEnumerator);
			if (t == null)
			{
				return result;
			}
			IMMDeviceEnumerator en = (IMMDeviceEnumerator)Activator.CreateInstance(t);
			int flow = (capture ? 1 : 0);
			string defId = "";
			if (en.GetDefaultAudioEndpoint(flow, 0, out IMMDevice def) == 0)
			{
				def?.GetId(out defId);
			}
			if (en.EnumAudioEndpoints(flow, 1, out IMMDeviceCollection col) != 0 || col == null)
			{
				return result;
			}
			col.GetCount(out var n);
			for (int i = 0; i < n; i++)
			{
				if (col.Item(i, out IMMDevice dev) == 0 && dev != null)
				{
					dev.GetId(out string id);
					result.Add(new AudioDevice
					{
						Id = id,
						Name = (FriendlyName(dev) ?? id),
						IsDefault = (id == defId)
					});
				}
			}
		}
		catch (Exception ex)
		{
			Logger.Log("AudioDevices.Enumerate failed: " + ex.Message);
		}
		return result;
	}

	private static string? FriendlyName(IMMDevice dev)
	{
		try
		{
			if (dev.OpenPropertyStore(0, out IPropertyStore store) != 0 || store == null)
			{
				return null;
			}
			AUDIO_PROPERTYKEY key = PKEY_Device_FriendlyName;
			if (store.GetValue(ref key, out var pv) != 0)
			{
				return null;
			}
			try
			{
				return (pv.vt == 31 && pv.p != IntPtr.Zero) ? Marshal.PtrToStringUni(pv.p) : null;
			}
			finally
			{
				PropVariantClear(ref pv);
			}
		}
		catch
		{
			return null;
		}
	}

	public static void SetDefault(string deviceId)
	{
		try
		{
			Type t = Type.GetTypeFromCLSID(CLSID_PolicyConfigClient);
			if (!(t == null))
			{
				IPolicyConfig pc = (IPolicyConfig)Activator.CreateInstance(t);
				pc.SetDefaultEndpoint(deviceId, 0);
				pc.SetDefaultEndpoint(deviceId, 1);
				pc.SetDefaultEndpoint(deviceId, 2);
			}
		}
		catch (Exception ex)
		{
			Logger.Log("AudioDevices.SetDefault failed: " + ex.Message);
		}
	}

	[DllImport("ole32.dll")]
	private static extern int PropVariantClear(ref AUDIO_PROPVARIANT pv);
}
