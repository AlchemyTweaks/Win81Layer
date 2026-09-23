using System.Runtime.InteropServices;

namespace Win81Layer;

[ComImport]
[Guid("E2F5BB11-0570-40CA-ACDD-3AA01277DEE8")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioSessionEnumerator
{
	[PreserveSig]
	int GetCount(out int count);

	[PreserveSig]
	int GetSession(int index, out IAudioSessionControl2 session);
}
