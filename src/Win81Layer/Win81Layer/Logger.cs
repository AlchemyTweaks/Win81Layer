using System;
using System.IO;
using System.Text;
using System.Threading;

namespace Win81Layer;

// NON-BLOCKING logger. Log() only appends to an in-memory buffer and signals a dedicated background thread; it NEVER
// touches the disk on the caller. This matters because Log() is called from the UI thread and the supervisor loop
// during startup, and the same UI thread also stamps the health heartbeat. The previous logger did synchronous file
// I/O with retries inside Log(), so a boot-time lock on log.txt (AV scanning a multi-MB file) could stall startup and
// starve the heartbeat — the shell then wedged with no taskbar and the supervisor could not tell it was alive.
//
// The flusher writes buffered lines to disk on a background thread (BelowNormal), retrying transient locks WITHOUT
// blocking anyone. If the disk stays unavailable, the buffer is bounded (old lines dropped) so memory never grows
// unbounded. At startup an oversized log is rotated to log.1.txt so the live file stays small and AV scans of it are
// cheap — keeping any boot-time lock window tiny.
public static class Logger
{
	internal static bool ReadOnlyDiagnosticsSupported => true;
	private static readonly object Gate = new object();

	private static readonly StringBuilder Pending = new StringBuilder();

	public static readonly string LogPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Win81Layer", "log.txt");

	private const int MaxPendingChars = 400000;   // ~0.8MB of UTF-16; bound so a stuck disk can't grow memory forever

	private const long RotateBytes = 2000000L;     // rotate log.txt -> log.1.txt at process start once it passes ~2MB

	private static readonly AutoResetEvent Signal = new AutoResetEvent(initialState: false);

	private static volatile bool _stopping;

	static Logger()
	{
		if (SettingsStore.ReadOnlyDiagnostics) return;
		try
		{
			TryRotate();
		}
		catch
		{
		}
		try
		{
			Thread t = new Thread(FlushLoop)
			{
				IsBackground = true,
				Name = "LogFlush",
				Priority = ThreadPriority.BelowNormal
			};
			t.Start();
		}
		catch
		{
		}
	}

	public static void Log(string message)
	{
		if (SettingsStore.ReadOnlyDiagnostics) return;
		try
		{
			lock (Gate)
			{
				// Drop new lines only when the buffer is already full (disk unavailable) — never block, never grow.
				if (Pending.Length < MaxPendingChars)
				{
					Pending.Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff")).Append(" [").Append(Environment.CurrentManagedThreadId)
						.Append("] ")
						.Append(message)
						.Append(Environment.NewLine);
				}
			}
			Signal.Set();   // wake the flusher; caller returns immediately without any disk I/O
		}
		catch
		{
		}
	}

	// Synchronous best-effort flush for shutdown / crash handlers, so the last lines are not lost when the background
	// thread dies with the process. Bounded so it can never hang an exit.
	public static void Flush()
	{
		if (SettingsStore.ReadOnlyDiagnostics) return;
		_stopping = true;
		try
		{
			Signal.Set();
			for (int i = 0; i < 5; i++)
			{
				if (!FlushOnce())
				{
					return;   // nothing left (or wrote it all)
				}
				Thread.Sleep(15);
			}
		}
		catch
		{
		}
	}

	private static void FlushLoop()
	{
		while (true)
		{
			try
			{
				Signal.WaitOne(1000);   // flush on signal, or at least once a second as a safety net
				FlushOnce();
			}
			catch
			{
			}
			if (_stopping)
			{
				try { FlushOnce(); } catch { }
			}
		}
	}

	// Writes the current buffer to disk. Returns true if there was pending data (whether or not the write succeeded),
	// false if the buffer was empty. On a locked/failed write the chunk is re-queued at the front (bounded) and retried
	// on the next tick — the caller thread is never involved.
	private static bool FlushOnce()
	{
		if (SettingsStore.ReadOnlyDiagnostics) return false;
		string chunk;
		lock (Gate)
		{
			if (Pending.Length == 0)
			{
				return false;
			}
			chunk = Pending.ToString();
			Pending.Clear();
		}
		try
		{
			Directory.CreateDirectory(Path.GetDirectoryName(LogPath));
			using FileStream fs = new FileStream(LogPath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
			using StreamWriter sw = new StreamWriter(fs);
			sw.Write(chunk);
			sw.Flush();
		}
		catch
		{
			// Disk busy/locked — put the chunk back at the FRONT so ordering is preserved, but stay bounded so a long
			// outage can't balloon memory. Next tick retries.
			lock (Gate)
			{
				int room = MaxPendingChars - Pending.Length;
				if (room > 0)
				{
					string keep = (chunk.Length <= room) ? chunk : chunk.Substring(chunk.Length - room);
					Pending.Insert(0, keep);
				}
			}
		}
		return true;
	}

	private static void TryRotate()
	{
		try
		{
			FileInfo fi = new FileInfo(LogPath);
			if (!fi.Exists || fi.Length <= RotateBytes)
			{
				return;
			}
			string bak = Path.Combine(Path.GetDirectoryName(LogPath), "log.1.txt");
			try { if (File.Exists(bak)) { File.Delete(bak); } } catch { }
			// Move fails (caught) if another instance holds the file without FileShare.Delete — harmless, we just skip;
			// the file stays small because whichever instance started first already rotated it.
			File.Move(LogPath, bak);
		}
		catch
		{
		}
	}
}
