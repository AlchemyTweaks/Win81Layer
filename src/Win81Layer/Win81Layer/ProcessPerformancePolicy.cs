using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;

namespace Win81Layer;

internal static class ProcessPerformancePolicy
{
	private const int ProcessPowerThrottling = 4;

	private const uint ExecutionSpeed = 0x1;

	[StructLayout(LayoutKind.Sequential)]
	private struct PROCESS_POWER_THROTTLING_STATE
	{
		internal uint Version;
		internal uint ControlMask;
		internal uint StateMask;
	}

	[DllImport("kernel32.dll", SetLastError = true)]
	private static extern bool SetProcessInformation(
		nint hProcess,
		int processInformationClass,
		ref PROCESS_POWER_THROTTLING_STATE processInformation,
		uint processInformationSize);

	[DllImport("kernel32.dll", SetLastError = true)]
	private static extern bool GetProcessInformation(
		nint hProcess,
		int processInformationClass,
		ref PROCESS_POWER_THROTTLING_STATE processInformation,
		uint processInformationSize);

	internal static void ApplyInteractiveShell()
	{
		try
		{
			Thread.CurrentThread.Priority = ThreadPriority.AboveNormal;
		}
		catch (Exception ex)
		{
			Logger.Log("Interactive UI thread priority policy failed: " + ex.Message);
		}

		try
		{
			using Process process = Process.GetCurrentProcess();
			PROCESS_POWER_THROTTLING_STATE state = new PROCESS_POWER_THROTTLING_STATE
			{
				Version = 1,
				ControlMask = ExecutionSpeed,
				StateMask = 0
			};
			if (!SetProcessInformation(process.Handle, ProcessPowerThrottling, ref state, (uint)Marshal.SizeOf<PROCESS_POWER_THROTTLING_STATE>()))
			{
				Logger.Log("Interactive HighQoS policy unavailable: Win32 " + Marshal.GetLastWin32Error());
				return;
			}
			Logger.Log("Interactive performance policy applied: UI thread AboveNormal, process HighQoS (EcoQoS disabled)");
		}
		catch (Exception ex)
		{
			Logger.Log("Interactive performance policy failed: " + ex.Message);
		}
	}

	internal static string Describe()
	{
		try
		{
			using Process process = Process.GetCurrentProcess();
			PROCESS_POWER_THROTTLING_STATE state = new PROCESS_POWER_THROTTLING_STATE { Version = 1 };
			if (!GetProcessInformation(process.Handle, ProcessPowerThrottling, ref state, (uint)Marshal.SizeOf<PROCESS_POWER_THROTTLING_STATE>()))
			{
				return "unavailable:" + Marshal.GetLastWin32Error();
			}
			bool controlled = (state.ControlMask & ExecutionSpeed) != 0;
			bool eco = (state.StateMask & ExecutionSpeed) != 0;
			return controlled && !eco ? "high-qos" : eco ? "eco-qos" : "system-managed";
		}
		catch (Exception ex)
		{
			return "error:" + ex.GetType().Name;
		}
	}
}
