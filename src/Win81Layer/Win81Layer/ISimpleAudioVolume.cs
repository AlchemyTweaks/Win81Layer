using System;
using System.Runtime.InteropServices;

namespace Win81Layer;

[ComImport]
[Guid("87CE5498-68D6-44E5-9215-6DA47EF883D8")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface ISimpleAudioVolume
{
	[PreserveSig]
	int SetMasterVolume(float level, ref Guid ctx);

	[PreserveSig]
	int GetMasterVolume(out float level);

	[PreserveSig]
	int SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, ref Guid ctx);

	[PreserveSig]
	int GetMute([MarshalAs(UnmanagedType.Bool)] out bool mute);
}
