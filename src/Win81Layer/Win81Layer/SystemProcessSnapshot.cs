#nullable enable

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace Win81Layer;

// One NtQuerySystemInformation(SystemProcessInformation) snapshot in a reusable native buffer, parsed in place: image
// names, process ids and every thread's state, wait reason and context-switch count. No Process objects, no handles and
// no managed allocation per refresh once the buffer has grown to fit. Not thread-safe: each user owns its instance.
internal sealed unsafe class SystemProcessSnapshot : IDisposable
{
	private const int SystemProcessInformation = 5;

	private const uint StatusInfoLengthMismatch = 0xC0000004u;

	// x64 layout of SYSTEM_PROCESS_INFORMATION / SYSTEM_THREAD_INFORMATION.
	private const int ProcessEntrySize = 0x100;

	private const int ThreadEntrySize = 0x50;

	private const int OffNextEntry = 0;

	private const int OffThreadCount = 4;

	private const int OffImageNameLength = 56;

	private const int OffImageNameBuffer = 64;

	private const int OffProcessId = 80;

	private const int OffThreadKernelTime = 0;

	private const int OffThreadUserTime = 8;

	private const int OffThreadId = 48;

	private const int OffThreadContextSwitches = 64;

	private const int OffThreadState = 68;

	private const int OffThreadWaitReason = 72;

	private const int ThreadStateWaiting = 5;

	private const int WaitReasonSuspended = 5;

	private byte* _buf;

	private int _size;

	private bool _valid;

	internal struct HostInfo
	{
		public int NameIndex;

		public uint Pid;

		public int Threads;

		public int SuspendedThreads;
	}

	internal struct ThreadInfo
	{
		public uint Tid;

		public uint ContextSwitches;

		public long KernelTime100ns;

		public long UserTime100ns;

		public int State;

		public int WaitReason;
	}

	internal bool Valid => _valid;

	// Takes a fresh snapshot. Grows the buffer when the system reports it too small (rare after the first call).
	internal bool Refresh()
	{
		_valid = false;
		if (IntPtr.Size != 8)
		{
			return false;   // the offsets above are the x64 layout; the launcher ships x64 only
		}
		if (_buf == null)
		{
			_size = 1024 * 1024;
			_buf = (byte*)NativeMemory.Alloc((nuint)_size);
		}
		for (int attempt = 0; attempt < 6; attempt++)
		{
			uint status = NtQuerySystemInformation(SystemProcessInformation, _buf, _size, out int needed);
			if (status == 0u)
			{
				_valid = true;
				return true;
			}
			if (status != StatusInfoLengthMismatch)
			{
				return false;
			}
			NativeMemory.Free(_buf);
			_size = Math.Max(needed + 128 * 1024, _size * 2);
			_buf = (byte*)NativeMemory.Alloc((nuint)_size);
		}
		return false;
	}

	// Every process whose image name equals one of names (compared without the ".exe" extension, ignoring case, like
	// Process.GetProcessesByName). Fills results and returns the count written; allocates nothing.
	internal int FindProcesses(string[] names, Span<HostInfo> results)
	{
		if (!_valid)
		{
			return 0;
		}
		int written = 0;
		byte* p = _buf;
		byte* end = _buf + _size;
		while (p + ProcessEntrySize <= end)
		{
			ushort nameBytes = *(ushort*)(p + OffImageNameLength);
			char* name = *(char**)(p + OffImageNameBuffer);
			if (nameBytes > 0 && name != null)
			{
				int nameLen = nameBytes / 2;
				for (int n = 0; n < names.Length; n++)
				{
					if (NameMatches(name, nameLen, names[n]))
					{
						if (written < results.Length)
						{
							int threads = *(int*)(p + OffThreadCount);
							int suspended = 0;
							byte* t = p + ProcessEntrySize;
							for (int i = 0; i < threads && t + ThreadEntrySize <= end; i++, t += ThreadEntrySize)
							{
								if (*(int*)(t + OffThreadState) == ThreadStateWaiting && *(int*)(t + OffThreadWaitReason) == WaitReasonSuspended)
								{
									suspended++;
								}
							}
							results[written++] = new HostInfo
							{
								NameIndex = n,
								Pid = (uint)(ulong)*(nint*)(p + OffProcessId),
								Threads = threads,
								SuspendedThreads = suspended
							};
						}
						break;
					}
				}
			}
			uint next = *(uint*)(p + OffNextEntry);
			if (next == 0u)
			{
				break;
			}
			p += next;
		}
		return written;
	}

	// The threads of one process (probe statistics). Returns false when the process is not in the snapshot.
	internal bool ThreadsOf(uint pid, List<ThreadInfo> into)
	{
		into.Clear();
		if (!_valid)
		{
			return false;
		}
		byte* p = _buf;
		byte* end = _buf + _size;
		while (p + ProcessEntrySize <= end)
		{
			if ((uint)(ulong)*(nint*)(p + OffProcessId) == pid)
			{
				int threads = *(int*)(p + OffThreadCount);
				byte* t = p + ProcessEntrySize;
				for (int i = 0; i < threads && t + ThreadEntrySize <= end; i++, t += ThreadEntrySize)
				{
					into.Add(new ThreadInfo
					{
						Tid = (uint)(ulong)*(nint*)(t + OffThreadId),
						ContextSwitches = *(uint*)(t + OffThreadContextSwitches),
						KernelTime100ns = *(long*)(t + OffThreadKernelTime),
						UserTime100ns = *(long*)(t + OffThreadUserTime),
						State = *(int*)(t + OffThreadState),
						WaitReason = *(int*)(t + OffThreadWaitReason)
					});
				}
				return true;
			}
			uint next = *(uint*)(p + OffNextEntry);
			if (next == 0u)
			{
				break;
			}
			p += next;
		}
		return false;
	}

	private static bool NameMatches(char* image, int len, string want)
	{
		// image is "Name.exe"; want is "Name"
		if (len != want.Length + 4)
		{
			return false;
		}
		for (int i = 0; i < want.Length; i++)
		{
			if (char.ToUpperInvariant(image[i]) != char.ToUpperInvariant(want[i]))
			{
				return false;
			}
		}
		return (image[len - 4] == '.') && char.ToUpperInvariant(image[len - 3]) == 'E' && char.ToUpperInvariant(image[len - 2]) == 'X' && char.ToUpperInvariant(image[len - 1]) == 'E';
	}

	public void Dispose()
	{
		if (_buf != null)
		{
			NativeMemory.Free(_buf);
			_buf = null;
		}
		_valid = false;
	}

	~SystemProcessSnapshot()
	{
		Dispose();
	}

	[DllImport("ntdll.dll")]
	private static extern uint NtQuerySystemInformation(int infoClass, void* buffer, int length, out int returnLength);
}
