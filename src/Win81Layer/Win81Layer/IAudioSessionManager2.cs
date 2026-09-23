using System.Runtime.InteropServices;

namespace Win81Layer;

[ComImport]
[Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioSessionManager2
{
	[PreserveSig]
	int GetAudioSessionControl(nint sessionGuid, int flags, out nint control);

	[PreserveSig]
	int GetSimpleAudioVolume(nint sessionGuid, int flags, out nint volume);

	[PreserveSig]
	int GetSessionEnumerator(out IAudioSessionEnumerator sessionEnum);

	[PreserveSig]
	int RegisterSessionNotification(nint notification);

	[PreserveSig]
	int UnregisterSessionNotification(nint notification);

	[PreserveSig]
	int RegisterDuckNotification([MarshalAs(UnmanagedType.LPWStr)] string sessionId, nint notification);

	[PreserveSig]
	int UnregisterDuckNotification(nint notification);
}
