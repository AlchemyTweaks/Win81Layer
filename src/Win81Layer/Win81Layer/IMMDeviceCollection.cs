using System.Runtime.InteropServices;

namespace Win81Layer;

[ComImport]
[Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMMDeviceCollection
{
	[PreserveSig]
	int GetCount(out int count);

	[PreserveSig]
	int Item(int index, out IMMDevice device);
}
