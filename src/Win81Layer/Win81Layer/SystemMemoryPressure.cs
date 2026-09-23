using System.Runtime.InteropServices;

namespace Win81Layer;

internal static class SystemMemoryPressure
{
	[StructLayout(LayoutKind.Sequential)]
	private struct MEMORYSTATUSEX
	{
		internal uint Length;
		internal uint MemoryLoad;
		internal ulong TotalPhysical;
		internal ulong AvailablePhysical;
		internal ulong TotalPageFile;
		internal ulong AvailablePageFile;
		internal ulong TotalVirtual;
		internal ulong AvailableVirtual;
		internal ulong AvailableExtendedVirtual;
	}

	[DllImport("kernel32.dll")]
	private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX buffer);

	internal static bool ShouldReleaseVisualCaches(out uint memoryLoad, out ulong availableBytes)
	{
		MEMORYSTATUSEX status = new MEMORYSTATUSEX
		{
			Length = (uint)Marshal.SizeOf<MEMORYSTATUSEX>()
		};
		if (!GlobalMemoryStatusEx(ref status))
		{
			memoryLoad = 100;
			availableBytes = 0;
			return true;
		}
		memoryLoad = status.MemoryLoad;
		availableBytes = status.AvailablePhysical;
		return memoryLoad >= 80 || availableBytes < 1024UL * 1024UL * 1024UL;
	}
}
