#nullable enable

using System;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;
using System.Threading;

namespace Win81Layer;

// The shell's liveness stamp. The UI thread writes the wall-clock time into a small named shared-memory section on every
// shell clock tick (an aligned 8-byte store: no file system, no AV scan), and into heartbeat.dat only at startup, on exit
// and every 15 s, which keeps a supervisor still running from an older build (file only, 30 s tolerance) satisfied.
// Readers (supervisor, watchdog) take the fresher of the section and the file. The SHELL creates the section, so its
// default security is the shell's own medium token; the elevated supervisor (same user) can still open it for reading.
// Layout: uint magic, int version, int pid, int reserved, long utcTicks.
internal static class Heartbeat
{
	private const string SectionName = "Local\\Win81Layer.Heartbeat.v1";

	private const uint Magic = 0x57384248u;   // "HB8W"

	private const int Version = 1;

	private const int Size = 4096;

	private const int OffMagic = 0;

	private const int OffVersion = 4;

	private const int OffPid = 8;

	private const int OffTicks = 16;

	private const long FileEveryMs = 15000L;

	private static MemoryMappedFile? _section;

	private static MemoryMappedViewAccessor? _view;

	private static bool _sectionFailed;

	private static long _lastFileMs = long.MinValue / 2;

	internal static long Beats;

	internal static long FileWrites;

	// The probe uses a name and a file of its own, so a live supervisor can never see a probe's beat.
	internal static string Name => PerfProbe.Active ? $"Local\\Win81Layer.Heartbeat.probe.{Environment.ProcessId}" : SectionName;

	internal static string DefaultFilePath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Win81Layer", "heartbeat.dat");

	internal static string FilePath => PerfProbe.Active ? Path.Combine(PerfProbeDiagnostics.OutDir, "probe-heartbeat.dat") : DefaultFilePath;

	// Shell clock tick (UI thread): the section every time, the file every 15 s.
	internal static void Beat()
	{
		long ticks = DateTime.UtcNow.Ticks;
		Beats++;
		if (!WriteSection(ticks) || Environment.TickCount64 - _lastFileMs >= FileEveryMs)
		{
			WriteFile(ticks);
		}
	}

	// Startup and exit: both, right now.
	internal static void BeatNow()
	{
		long ticks = DateTime.UtcNow.Ticks;
		WriteSection(ticks);
		WriteFile(ticks);
	}

	private static bool WriteSection(long ticks)
	{
		if (_sectionFailed)
		{
			return false;
		}
		try
		{
			if (_view == null)
			{
				_section = MemoryMappedFile.CreateOrOpen(Name, Size, MemoryMappedFileAccess.ReadWrite);
				_view = _section.CreateViewAccessor(0, 32, MemoryMappedFileAccess.ReadWrite);
				_view.Write(OffMagic, Magic);
				_view.Write(OffVersion, Version);
				_view.Write(OffPid, Environment.ProcessId);
			}
			_view.Write(OffPid, Environment.ProcessId);
			_view.Write(OffTicks, ticks);   // the view starts on a page boundary: an aligned 8-byte store
			return true;
		}
		catch (Exception ex)
		{
			_sectionFailed = true;   // fall back to the file for the rest of this session
			Logger.Log("Heartbeat section unavailable, using heartbeat.dat only: " + ex.Message);
			return false;
		}
	}

	private static void WriteFile(long ticks)
	{
		_lastFileMs = Environment.TickCount64;
		try
		{
			using FileStream fs = new FileStream(FilePath, FileMode.Create, FileAccess.Write, FileShare.ReadWrite);
			byte[] bytes = BitConverter.GetBytes(ticks);
			fs.Write(bytes, 0, bytes.Length);
			FileWrites++;
		}
		catch
		{
		}
	}

	// Supervisor / watchdog: the fresher of the section (when it exists, is valid and belongs to a live process) and the
	// file. 0 when neither can be read.
	internal static long ReadFreshest(string? filePath = null, string? sectionName = null)
	{
		long section = ReadSection(sectionName ?? SectionName);
		long file = ReadFile(filePath ?? DefaultFilePath);
		return Math.Max(section, file);
	}

	internal static long ReadSection(string name)
	{
		try
		{
			using MemoryMappedFile mmf = MemoryMappedFile.OpenExisting(name, MemoryMappedFileRights.Read);
			using MemoryMappedViewAccessor v = mmf.CreateViewAccessor(0, 32, MemoryMappedFileAccess.Read);
			if (v.ReadUInt32(OffMagic) != Magic || v.ReadInt32(OffVersion) != Version)
			{
				return 0L;
			}
			int pid = v.ReadInt32(OffPid);
			if (pid <= 0 || !ProcessAlive(pid))
			{
				return 0L;
			}
			return v.ReadInt64(OffTicks);
		}
		catch
		{
			return 0L;   // missing (no shell holds it) or unreadable: the file decides
		}
	}

	internal static long ReadFile(string path)
	{
		try
		{
			using FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
			byte[] bytes = new byte[8];
			if (fs.Read(bytes, 0, 8) == 8)
			{
				return BitConverter.ToInt64(bytes, 0);
			}
		}
		catch
		{
		}
		return 0L;
	}

	internal static void Close()
	{
		try { _view?.Dispose(); } catch { }
		try { _section?.Dispose(); } catch { }
		_view = null;
		_section = null;
	}

	private static bool ProcessAlive(int pid)
	{
		nint h = OpenProcess(0x1000u, false, (uint)pid);   // PROCESS_QUERY_LIMITED_INFORMATION
		if (h == IntPtr.Zero)
		{
			// Access can be refused for a process of another session or integrity; existence is what matters here and
			// the section only exists while a shell holds it.
			return Marshal.GetLastWin32Error() == 5;
		}
		try
		{
			return GetExitCodeProcess(h, out uint code) && code == 259u;   // STILL_ACTIVE
		}
		finally
		{
			CloseHandle(h);
		}
	}

	[DllImport("kernel32.dll", SetLastError = true)]
	private static extern nint OpenProcess(uint access, bool inherit, uint pid);

	[DllImport("kernel32.dll")]
	private static extern bool GetExitCodeProcess(nint h, out uint code);

	[DllImport("kernel32.dll")]
	private static extern bool CloseHandle(nint h);
}
