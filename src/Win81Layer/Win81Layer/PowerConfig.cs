using System;
using System.Runtime.InteropServices;

namespace Win81Layer;

internal static class PowerConfig
{
	private static Guid _subVideo = new Guid("7516b95f-f776-4464-8c53-06167f40cc99");

	private static Guid _videoIdle = new Guid("3c0bc021-c8a8-4e07-a973-6b14cbcb2b7e");

	private static Guid _subSleep = new Guid("238C9FA8-0AAD-41ED-83F4-97BE242C8F20");

	private static Guid _standbyIdle = new Guid("29f6c1db-86da-48c5-9fdb-f2b67b1f44da");

	[DllImport("powrprof.dll")]
	private static extern uint PowerGetActiveScheme(nint userRootPowerKey, out nint activePolicyGuid);

	[DllImport("powrprof.dll")]
	private static extern uint PowerReadACValueIndex(nint rootPowerKey, ref Guid schemeGuid, ref Guid subGroupGuid, ref Guid settingGuid, out uint acValueIndex);

	[DllImport("powrprof.dll")]
	private static extern uint PowerWriteACValueIndex(nint rootPowerKey, ref Guid schemeGuid, ref Guid subGroupGuid, ref Guid settingGuid, uint acValueIndex);

	[DllImport("powrprof.dll")]
	private static extern uint PowerSetActiveScheme(nint userRootPowerKey, ref Guid schemeGuid);

	[DllImport("kernel32.dll")]
	private static extern nint LocalFree(nint hMem);

	public static int MonitorTimeoutAcMin()
	{
		return ReadMin(ref _subVideo, ref _videoIdle);
	}

	public static int StandbyTimeoutAcMin()
	{
		return ReadMin(ref _subSleep, ref _standbyIdle);
	}

	public static void SetMonitorTimeoutAcMin(int min)
	{
		Write(ref _subVideo, ref _videoIdle, min);
	}

	public static void SetStandbyTimeoutAcMin(int min)
	{
		Write(ref _subSleep, ref _standbyIdle, min);
	}

	private static int ReadMin(ref Guid sub, ref Guid setting)
	{
		nint active = IntPtr.Zero;
		try
		{
			if (PowerGetActiveScheme(IntPtr.Zero, out active) != 0 || active == IntPtr.Zero)
			{
				return -1;
			}
			Guid scheme = Marshal.PtrToStructure<Guid>(active);
			if (PowerReadACValueIndex(IntPtr.Zero, ref scheme, ref sub, ref setting, out var secs) != 0)
			{
				return -1;
			}
			return (int)(secs / 60);
		}
		catch
		{
			return -1;
		}
		finally
		{
			if (active != IntPtr.Zero)
			{
				LocalFree(active);
			}
		}
	}

	private static void Write(ref Guid sub, ref Guid setting, int min)
	{
		nint active = IntPtr.Zero;
		try
		{
			if (PowerGetActiveScheme(IntPtr.Zero, out active) == 0 && active != IntPtr.Zero)
			{
				Guid scheme = Marshal.PtrToStructure<Guid>(active);
				uint secs = (uint)(Math.Max(0, min) * 60);
				if (PowerWriteACValueIndex(IntPtr.Zero, ref scheme, ref sub, ref setting, secs) == 0)
				{
					PowerSetActiveScheme(IntPtr.Zero, ref scheme);
				}
			}
		}
		catch
		{
		}
		finally
		{
			if (active != IntPtr.Zero)
			{
				LocalFree(active);
			}
		}
	}
}
