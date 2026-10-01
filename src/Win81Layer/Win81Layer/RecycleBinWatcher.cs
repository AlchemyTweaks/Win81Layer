using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

namespace Win81Layer;

// Windows re-evaluates the Recycle Bin's empty/full icon lazily, so after the bin is emptied the desktop icon can lag
// several seconds before switching to the "empty" glyph (the user reported this). We watch every fixed drive's
// $Recycle.Bin and, on any change (debounced), call the documented SHUpdateRecycleBinIcon() so the shell re-reads the
// bin state and swaps the icon IMMEDIATELY. Fully passive/reversible — no registry or file changes.
internal static class RecycleBinWatcher
{
	private static readonly object _gate = new object();
	private static readonly object _evalGate = new object();   // serializes Evaluate (timer, debounce, settle and FSW threads)
	private static readonly List<FileSystemWatcher> _watchers = new List<FileSystemWatcher>();
	private static Timer _debounce;
	private static Timer _settle;
	private static Timer _poll;
	// Backstop poll: fast only when some bin is not reliably watched (container fallback / watcher failure).
	private const int PollFastMs = 5000;
	private const int PollSlowMs = 30000;
	private static bool _fastPoll;        // true once some bin is known not to be watched per-user
	private static uint _driveMask;       // GetLogicalDrives at Start(): a volume attached later has no watcher
	private static int _lastEmpty = -1;   // -1 unknown, 0 has items, 1 empty
	private static int _pending;          // 0 = idle, 1 = a refresh cycle is in flight (drives the leading-edge response)
	private static bool _started;

	internal static void Start()
	{
		lock (_gate)
		{
			if (_started)
			{
				return;
			}
			_started = true;
			try
			{
				// Trailing settle: fires shortly after the LAST change to lock in the final state and coalesce bursts (e.g.
				// emptying many files). The FSW path MUST re-evaluate state + update the rendered (Default) icon (not just
				// redraw), so route it through Evaluate. The instant response itself comes from the leading edge in OnChange.
				// The 400 ms settle re-checks once more because SHQueryRecycleBin's count can lag the file operations.
				_settle = new Timer(delegate { Evaluate(forceRefresh: false); }, null, Timeout.Infinite, Timeout.Infinite);
				_debounce = new Timer(delegate
				{
					Interlocked.Exchange(ref _pending, 0);
					Evaluate(forceRefresh: true);
					try { _settle?.Change(400, Timeout.Infinite); } catch { }
				}, null, Timeout.Infinite, Timeout.Infinite);
				// A standard user cannot LIST the $Recycle.Bin CONTAINER, so a watcher on it can silently miss UI-driven
				// deletes/empties (then the icon only updated on the slow poll). Watch our OWN per-user SID subfolder instead
				// (<drive>\$Recycle.Bin\<SID>) — the user has full rights there, so FileSystemWatcher fires reliably & fast.
				string sid = null;
				try { sid = System.Security.Principal.WindowsIdentity.GetCurrent()?.User?.Value; } catch { }
				bool anyFallback = false;
				foreach (DriveInfo d in DriveInfo.GetDrives())
				{
					try
					{
						if (d.DriveType != DriveType.Fixed)
						{
							continue;
						}
						if (!d.IsReady)
						{
							anyFallback = true;   // e.g. a BitLocker volume still locked at logon: only the poll sees its bin
							continue;
						}
						string container = Path.Combine(d.RootDirectory.FullName, "$Recycle.Bin");
						if (!Directory.Exists(container))
						{
							continue;
						}
						string mine = (sid != null) ? Path.Combine(container, sid) : container;
						string bin = Directory.Exists(mine) ? mine : container;   // per-user subfolder if present, else the container
						anyFallback |= (bin == container);
						FileSystemWatcher w = new FileSystemWatcher(bin)
						{
							IncludeSubdirectories = true,
							NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName,
							InternalBufferSize = 16384
						};
						w.Created += OnChange;
						w.Deleted += OnChange;
						w.Renamed += OnChange;
						w.Error += OnWatcherError;
						w.EnableRaisingEvents = true;
						_watchers.Add(w);
					}
					catch
					{
						anyFallback = true;   // this drive's bin is unwatched, so only the poll covers it
					}
				}
				// Reliable backstop: a standard user has no list/traverse rights on the $Recycle.Bin CONTAINER, so the
				// FileSystemWatcher above can miss an empty performed through the Windows UI. Poll SHQueryRecycleBin and
				// refresh the icon whenever the bin crosses the empty<->non-empty boundary (the only time the glyph changes).
				// Each poll scans every volume's bin, so poll slowly when the per-user watchers cover every drive.
				bool fast = anyFallback || _watchers.Count == 0;
				int period = fast ? PollFastMs : PollSlowMs;
				_fastPoll = fast;
				_driveMask = GetLogicalDrives();
				try { _poll = new Timer(delegate { PollTick(); }, null, 1500, period); } catch { }
				Logger.Log($"RecycleBinWatcher: watching {_watchers.Count} recycle folder(s) + SHQueryRecycleBin backstop poll every {period / 1000}s ({(fast ? "fallback: some bin not watched per-user" : "all bins watched per-user")}).");
			}
			catch (Exception ex)
			{
				Logger.Log("RecycleBinWatcher start failed: " + ex.Message);
			}
		}
	}

	internal static void Stop()
	{
		lock (_gate)
		{
			foreach (FileSystemWatcher w in _watchers)
			{
				try { w.EnableRaisingEvents = false; w.Dispose(); } catch { }
			}
			_watchers.Clear();
			try { _debounce?.Dispose(); } catch { }
			_debounce = null;
			try { _settle?.Dispose(); } catch { }
			_settle = null;
			try { _poll?.Dispose(); } catch { }
			_poll = null;
			_lastEmpty = -1;
			_pending = 0;
			_started = false;
		}
	}

	private static void OnChange(object sender, FileSystemEventArgs e)
	{
		try
		{
			// A file landing in a bin already known to be non-empty cannot flip the glyph, so a delete into a full bin
			// costs no scan at all. Deletes/renames (restore, empty) can empty it and still evaluate.
			if (e.ChangeType == WatcherChangeTypes.Created && Volatile.Read(ref _lastEmpty) == 0)
			{
				return;
			}
			// Leading edge: respond to the FIRST change after idle INSTANTLY (native-feel — no debounce wait). A trailing
			// timer (below) settles the final state and coalesces rapid bursts so we don't thrash on multi-file operations.
			if (Interlocked.Exchange(ref _pending, 1) == 0)
			{
				Evaluate(forceRefresh: true);
			}
			_debounce?.Change(60, Timeout.Infinite);
		}
		catch { }
	}

	// Buffer overflow means events were dropped, so resync. Any other error ends that watcher for good, so the
	// slow poll would become the only path for its drive: drop to the fast poll.
	private static void OnWatcherError(object sender, ErrorEventArgs e)
	{
		try
		{
			if (!(e.GetException() is InternalBufferOverflowException))
			{
				Logger.Log("RecycleBinWatcher: watcher stopped (" + e.GetException()?.Message + ") -> backstop poll every " + (PollFastMs / 1000) + "s");
				_fastPoll = true;
				_poll?.Change(PollFastMs, PollFastMs);
			}
		}
		catch { }
		Evaluate(forceRefresh: true);
	}

	// Skip a tick while another Evaluate is running (e.g. a slow SHQueryRecycleBin), so poll callbacks cannot pile up on
	// the lock; the next tick re-checks anyway.
	private static void PollTick()
	{
		if (!_fastPoll)
		{
			// A volume attached after Start() (USB disk, dock, BitLocker unlock) has no watcher: poll fast from then on.
			uint mask = GetLogicalDrives();
			if (mask != _driveMask)
			{
				_driveMask = mask;
				_fastPoll = true;
				Logger.Log("RecycleBinWatcher: volume set changed -> backstop poll every " + (PollFastMs / 1000) + "s");
				try { _poll?.Change(PollFastMs, PollFastMs); } catch { }
			}
		}
		if (!Monitor.TryEnter(_evalGate))
		{
			return;
		}
		try
		{
			Evaluate(forceRefresh: false);
		}
		finally
		{
			Monitor.Exit(_evalGate);
		}
	}

	// Single source of truth for the empty<->full swap. Queries the bin, keeps the rendered Recycle Bin (Default) icon in
	// sync with actual state, then tells the shell to redraw it. Redraws only on an empty<->non-empty transition (or the
	// first sync), so ordinary deletes never make Explorer re-query the bin. forceRefresh=true (FSW change / watcher error)
	// additionally re-syncs a stale (Default) and redraws when the query fails. (Default) is updated BEFORE redrawing:
	// otherwise the pinned icon would never change. Serialized: _lastEmpty is shared by the timer, settle and FSW threads,
	// and an unserialized race could detect one transition twice (two whole-desktop refreshes).
	private static void Evaluate(bool forceRefresh)
	{
		lock (_evalGate)
		{
			try
			{
				SHQUERYRBINFO info = default;
				info.cbSize = Marshal.SizeOf<SHQUERYRBINFO>();
				// null root => aggregate across all bins the user can see.
				int hr = SHQueryRecycleBin(null, ref info);
				if (hr != 0)
				{
					if (forceRefresh) { RefreshNow(); }
					return;
				}
				int empty = (info.i64NumItems <= 0) ? 1 : 0;
				bool transition = (_lastEmpty != -1 && empty != _lastEmpty);
				if (transition || _lastEmpty == -1)
				{
					SystemIcons81.SetRecycleDefault(empty == 1);
					// Desktop re-enumeration only when the empty/full glyph actually flips (or first sync), not on every delete.
					RefreshNow(desktopRefresh: true);
				}
				else if (forceRefresh && SystemIcons81.SetRecycleDefault(empty == 1))
				{
					RefreshNow();   // no flip, but (Default) had drifted from the real state: item-level redraw, as before
				}
				if (transition)
				{
					Logger.Log($"RecycleBinWatcher: bin is now {(empty == 1 ? "EMPTY" : "FULL")} -> (Default) icon swapped + desktop item notified");
				}
				_lastEmpty = empty;
			}
			catch
			{
			}
		}
	}

	// Public so the shell can also force an immediate refresh right after an in-app "empty recycle bin" action.
	internal static void RefreshNow(bool desktopRefresh = false)
	{
		try
		{
			SHUpdateRecycleBinIcon();
		}
		catch (Exception ex)
		{
			Logger.Log("RecycleBinWatcher refresh failed: " + ex.Message);
		}
		// VERIFIED 2026-09-06 on Win11 26200: SHUpdateRecycleBinIcon() alone does NOT make the native desktop repaint the
		// Recycle Bin when DefaultIcon (Default) switches between two different .ico FILES (our empty81a/full81a) — the
		// desktop view keeps the cached glyph until the user presses F5 (the reported bug). Telling the shell that the
		// Recycle Bin ITEM changed (SHCNE_UPDATEITEM on its pidl) makes every view re-query the item's icon and repaint at
		// once, without an icon-cache wipe and without touching the other desktop icons. Probe-verified before shipping.
		NotifyRecycleBinItemChanged();
		// On Win11 26200 the item-level notify above re-reads the bin's association but does NOT force the live desktop
		// to re-EXTRACT its cached empty/full glyph — it stayed stale until a manual F5, in BOTH directions. On an actual
		// empty<->full flip, do the F5-equivalent desktop re-enumeration so the glyph swaps immediately. Gated by
		// desktopRefresh so ordinary deletes (bin already non-empty) do NOT trigger a whole-desktop refresh every time.
		if (desktopRefresh)
		{
			DesktopShell.Refresh();
		}
	}

	private static void NotifyRecycleBinItemChanged()
	{
		nint pidl = 0;
		try
		{
			Guid rfid = FOLDERID_RecycleBinFolder;
			if (SHGetKnownFolderIDList(ref rfid, 0u, 0, out pidl) == 0 && pidl != 0)
			{
				// FLUSHNOWAIT: the shell copies the pidl and processes the event asynchronously — never blocks our timer thread.
				SHChangeNotify(SHCNE_UPDATEITEM, SHCNF_IDLIST | SHCNF_FLUSHNOWAIT, pidl, 0);
			}
		}
		catch (Exception ex)
		{
			Logger.Log("RecycleBinWatcher item-notify failed: " + ex.Message);
		}
		finally
		{
			if (pidl != 0)
			{
				try { ILFree(pidl); } catch { }
			}
		}
	}

	private const int SHCNE_UPDATEITEM = 0x00002000;
	private const uint SHCNF_IDLIST = 0x0000;
	private const uint SHCNF_FLUSHNOWAIT = 0x2000;
	private static readonly Guid FOLDERID_RecycleBinFolder = new Guid("B7534046-3ECB-4C18-BE4E-64CD4CB7D6AC");

	[DllImport("shell32.dll")]
	private static extern void SHUpdateRecycleBinIcon();

	[DllImport("kernel32.dll")]
	private static extern uint GetLogicalDrives();

	[DllImport("shell32.dll")]
	private static extern void SHChangeNotify(int wEventId, uint uFlags, nint dwItem1, nint dwItem2);

	[DllImport("shell32.dll")]
	private static extern int SHGetKnownFolderIDList(ref Guid rfid, uint dwFlags, nint hToken, out nint ppidl);

	[DllImport("shell32.dll")]
	private static extern void ILFree(nint pidl);

	[StructLayout(LayoutKind.Sequential)]
	private struct SHQUERYRBINFO
	{
		public int cbSize;
		public long i64Size;
		public long i64NumItems;
	}

	[DllImport("shell32.dll", CharSet = CharSet.Unicode)]
	private static extern int SHQueryRecycleBin(string? pszRootPath, ref SHQUERYRBINFO pSHQueryRBInfo);
}
