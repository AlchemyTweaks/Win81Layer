using System;
using System.Runtime.InteropServices;

namespace Win81Layer;

[ComImport]
[Guid("bfb7ff88-7239-4fc9-8fa2-07c950be9c6d")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioSessionControl2
{
	[PreserveSig]
	int GetState(out int state);

	[PreserveSig]
	int GetDisplayName([MarshalAs(UnmanagedType.LPWStr)] out string name);

	[PreserveSig]
	int SetDisplayName([MarshalAs(UnmanagedType.LPWStr)] string name, ref Guid ctx);

	[PreserveSig]
	int GetIconPath([MarshalAs(UnmanagedType.LPWStr)] out string path);

	[PreserveSig]
	int SetIconPath([MarshalAs(UnmanagedType.LPWStr)] string path, ref Guid ctx);

	[PreserveSig]
	int GetGroupingParam(out Guid group);

	[PreserveSig]
	int SetGroupingParam(ref Guid group, ref Guid ctx);

	[PreserveSig]
	int RegisterAudioSessionNotification(nint n);

	[PreserveSig]
	int UnregisterAudioSessionNotification(nint n);

	[PreserveSig]
	int GetSessionIdentifier([MarshalAs(UnmanagedType.LPWStr)] out string id);

	[PreserveSig]
	int GetSessionInstanceIdentifier([MarshalAs(UnmanagedType.LPWStr)] out string id);

	[PreserveSig]
	int GetProcessId(out int pid);

	[PreserveSig]
	int IsSystemSoundsSession();

	[PreserveSig]
	int SetDuckingPreference([MarshalAs(UnmanagedType.Bool)] bool optOut);
}
