using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Windows.Media;

namespace Win81Layer;

public static class AudioSessions
{
	private static readonly Guid CLSID_MMDeviceEnumerator = new Guid("BCDE0395-E52F-467C-8E3D-C4579291692E");

	private static readonly Guid IID_IAudioSessionManager2 = new Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F");

	public static List<AudioSessionVm> Enumerate()
	{
		List<AudioSessionVm> list = new List<AudioSessionVm>();
		try
		{
			Type t = Type.GetTypeFromCLSID(CLSID_MMDeviceEnumerator);
			if ((object)t == null)
			{
				return list;
			}
			IMMDeviceEnumerator en = (IMMDeviceEnumerator)Activator.CreateInstance(t);
			if (en.GetDefaultAudioEndpoint(0, 1, out IMMDevice dev) != 0 || dev == null)
			{
				return list;
			}
			Guid iid = IID_IAudioSessionManager2;
			if (dev.Activate(ref iid, 1, IntPtr.Zero, out object o) != 0)
			{
				return list;
			}
			IAudioSessionManager2 mgr = (IAudioSessionManager2)o;
			if (mgr.GetSessionEnumerator(out IAudioSessionEnumerator sessions) != 0)
			{
				return list;
			}
			if (sessions.GetCount(out var count) != 0)
			{
				return list;
			}
			HashSet<int> seen = new HashSet<int>();
			for (int i = 0; i < count; i++)
			{
				if (sessions.GetSession(i, out IAudioSessionControl2 ctl) != 0 || ctl == null || (ctl.GetState(out var state) == 0 && state == 2))
				{
					continue;
				}
				bool isSystem = ctl.IsSystemSoundsSession() == 0;
				ctl.GetProcessId(out var pid);
				if ((isSystem || pid == 0 || seen.Add(pid)) && ctl is ISimpleAudioVolume vol)
				{
					vol.GetMasterVolume(out var level);
					vol.GetMute(out var mute);
					string name;
					ImageSource icon;
					if (!isSystem)
					{
						(name, icon) = Describe(pid);
					}
					else
					{
						name = "System sounds";
						icon = null;
					}
					list.Add(new AudioSessionVm(vol, name, icon, (int)Math.Round(level * 100f), mute));
				}
			}
		}
		catch (Exception ex)
		{
			Logger.Log("Audio sessions enumerate failed: " + ex.Message);
		}
		return list.OrderBy<AudioSessionVm, string>((AudioSessionVm s) => s.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
	}

	private static (string, ImageSource?) Describe(int pid)
	{
		try
		{
			Process p = Process.GetProcessById(pid);
			string file = null;
			try
			{
				file = p.MainModule?.FileName;
			}
			catch
			{
			}
			string name = p.ProcessName;
			if (file != null)
			{
				try
				{
					string fd = FileVersionInfo.GetVersionInfo(file).FileDescription;
					if (!string.IsNullOrWhiteSpace(fd))
					{
						name = fd;
					}
				}
				catch
				{
				}
				ImageSource icon = AppInventory.LoadIcon(file);
				return (name, icon);
			}
			return (name, null);
		}
		catch
		{
			return ("App", null);
		}
	}
}
