using System;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;

namespace Win81Layer;

public sealed class PerfMonitor
{
	private struct MEMORYSTATUSEX
	{
		public uint dwLength;

		public uint dwMemoryLoad;

		public ulong ullTotalPhys;

		public ulong ullAvailPhys;

		public ulong ullTotalPageFile;

		public ulong ullAvailPageFile;

		public ulong ullTotalVirtual;

		public ulong ullAvailVirtual;

		public ulong ullAvailExtendedVirtual;
	}

	private long _prevIdle;

	private long _prevKernel;

	private long _prevUser;

	private long _prevRecv;

	private long _prevSent;

	private long _prevTicks;

	public int CpuPercent { get; private set; }

	public int RamPercent { get; private set; }

	public double DownBytesPerSec { get; private set; }

	public double UpBytesPerSec { get; private set; }

	[DllImport("kernel32.dll")]
	private static extern bool GetSystemTimes(out long idle, out long kernel, out long user);

	[DllImport("kernel32.dll")]
	private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX m);

	public void Update()
	{
		if (GetSystemTimes(out var idle, out var kernel, out var user))
		{
			long dIdle = idle - _prevIdle;
			long dKernel = kernel - _prevKernel;
			long dUser = user - _prevUser;
			long total = dKernel + dUser;
			if (_prevKernel != 0L && total > 0)
			{
				CpuPercent = (int)Math.Clamp(100.0 * (double)(total - dIdle) / (double)total, 0.0, 100.0);
			}
			_prevIdle = idle;
			_prevKernel = kernel;
			_prevUser = user;
		}
		MEMORYSTATUSEX mem = new MEMORYSTATUSEX
		{
			dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>()
		};
		if (GlobalMemoryStatusEx(ref mem))
		{
			RamPercent = (int)mem.dwMemoryLoad;
		}
		long recv = 0L;
		long sent = 0L;
		try
		{
			NetworkInterface[] allNetworkInterfaces = NetworkInterface.GetAllNetworkInterfaces();
			foreach (NetworkInterface ni in allNetworkInterfaces)
			{
				if (ni.OperationalStatus == OperationalStatus.Up)
				{
					NetworkInterfaceType networkInterfaceType = ni.NetworkInterfaceType;
					if ((networkInterfaceType != NetworkInterfaceType.Loopback && networkInterfaceType != NetworkInterfaceType.Tunnel) || 1 == 0)
					{
						IPInterfaceStatistics s = ni.GetIPStatistics();
						recv += s.BytesReceived;
						sent += s.BytesSent;
					}
				}
			}
		}
		catch
		{
		}
		long now = Environment.TickCount64;
		double secs = ((_prevTicks == 0L) ? 1.0 : Math.Max(0.001, (double)(now - _prevTicks) / 1000.0));
		if (_prevTicks != 0)
		{
			DownBytesPerSec = Math.Max(0.0, (double)(recv - _prevRecv) / secs);
			UpBytesPerSec = Math.Max(0.0, (double)(sent - _prevSent) / secs);
		}
		_prevRecv = recv;
		_prevSent = sent;
		_prevTicks = now;
	}

	public static string Rate(double bytesPerSec)
	{
		if (!(bytesPerSec >= 1048576.0))
		{
			if (!(bytesPerSec >= 1024.0))
			{
				return $"{bytesPerSec:0} B/s";
			}
			return $"{bytesPerSec / 1024.0:0} KB/s";
		}
		return $"{bytesPerSec / 1048576.0:0.0} MB/s";
	}
}
