using System.Runtime.InteropServices;

namespace Win81Layer;

[ComImport]
[Guid("A95664D2-9614-4F35-A746-DE8DB63617E6")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMMDeviceEnumerator
{
	[PreserveSig]
	int EnumAudioEndpoints(int dataFlow, int stateMask, out IMMDeviceCollection devices);

	[PreserveSig]
	int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice ppDevice);
}
