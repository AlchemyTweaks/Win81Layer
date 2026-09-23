using System.Runtime.InteropServices;

namespace Win81Layer;

[ComImport]
[Guid("886d8eeb-8cf2-4446-8d02-cdba1dbdcf99")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IPropertyStore
{
	[PreserveSig]
	int GetCount(out int count);

	[PreserveSig]
	int GetAt(int index, out AUDIO_PROPERTYKEY key);

	[PreserveSig]
	int GetValue(ref AUDIO_PROPERTYKEY key, out AUDIO_PROPVARIANT value);

	[PreserveSig]
	int SetValue(ref AUDIO_PROPERTYKEY key, ref AUDIO_PROPVARIANT value);

	[PreserveSig]
	int Commit();
}
