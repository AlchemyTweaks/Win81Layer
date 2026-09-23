using System;
using System.Runtime.InteropServices;

namespace Win81Layer;

[ComImport]
[Guid("D666063F-1587-4E43-81F1-B948E807363F")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMMDevice
{
	[PreserveSig]
	int Activate(ref Guid iid, int clsCtx, nint activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object ppInterface);

	[PreserveSig]
	int OpenPropertyStore(int stgmAccess, out IPropertyStore properties);

	[PreserveSig]
	int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);

	[PreserveSig]
	int GetState(out int state);
}
