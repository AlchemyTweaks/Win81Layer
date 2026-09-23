using System;
using System.Runtime.InteropServices;

namespace Win81Layer;

[ComImport]
[Guid("5CDF2C82-841E-4546-9722-0CF74078229A")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioEndpointVolume
{
	[PreserveSig]
	int RegisterControlChangeNotify(nint notify);

	[PreserveSig]
	int UnregisterControlChangeNotify(nint notify);

	[PreserveSig]
	int GetChannelCount(out int count);

	[PreserveSig]
	int SetMasterVolumeLevel(float level, ref Guid ctx);

	[PreserveSig]
	int SetMasterVolumeLevelScalar(float level, ref Guid ctx);

	[PreserveSig]
	int GetMasterVolumeLevel(out float level);

	[PreserveSig]
	int GetMasterVolumeLevelScalar(out float level);

	[PreserveSig]
	int SetChannelVolumeLevel(uint ch, float level, ref Guid ctx);

	[PreserveSig]
	int SetChannelVolumeLevelScalar(uint ch, float level, ref Guid ctx);

	[PreserveSig]
	int GetChannelVolumeLevel(uint ch, out float level);

	[PreserveSig]
	int GetChannelVolumeLevelScalar(uint ch, out float level);

	[PreserveSig]
	int SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, ref Guid ctx);

	[PreserveSig]
	int GetMute([MarshalAs(UnmanagedType.Bool)] out bool mute);

	[PreserveSig]
	int GetVolumeStepInfo(out uint step, out uint stepCount);

	[PreserveSig]
	int VolumeStepUp(ref Guid ctx);

	[PreserveSig]
	int VolumeStepDown(ref Guid ctx);

	[PreserveSig]
	int QueryHardwareSupport(out uint mask);

	[PreserveSig]
	int GetVolumeRange(out float min, out float max, out float inc);
}
