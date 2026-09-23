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
	private static readonly List<FileSystemWatcher> _watchers = new List<FileSystemWatcher>();
	private static Timer _debounce;
	private static Timer _poll;
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
				_debounce = new Timer(delegate { Interlocked.Exchange(ref _pending, 0); Evaluate(forceRefresh: true); }, null, Timeout.Infinite, Timeout.Infinite);
				// A standard user cannot LIST the $Recycle.Bin CONTAINER, so a watcher on it can silently miss UI-driven
				// deletes/empties (then the icon only updated on the slow poll). Watch our OWN per-user SID subfolder instead
				// (<drive>\$Recycle.Bin\<SID>) — the user has full rights there, so FileSystemWatcher fires reliably & fast.
				string sid = null;
				try { sid = System.Security.Principal.WindowsIdentity.GetCurrent()?.User?.Value; } catch { }
				foreach (DriveInfo d in DriveInfo.GetDrives())
				{
					try
					{
						if (!d.IsReady || d.DriveType != DriveType.Fixed)
						{
							continue;
						}
						string container = Path.Combine(d.RootDirectory.FullName, "$Recycle.Bin");
						if (!Directory.Exists(container))
						{
							continue;
						}
						string mine = (sid != null) ? Path.Combine(container, sid) : container;
						string bin = Directory.Exists(mine) ? mine : container;   // per-user subfolder if present, else the container
						FileSystemWatcher w = new FileSystemWatcher(bin)
						{
							IncludeSubdirectories = true,
							NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.Size,
							InternalBufferSize = 16384
						};
						w.Created += OnChange;
						w.Deleted += OnChange;
						w.Renamed += OnChange;
						w.Error += delegate { };   // swallow buffer-overflow etc.; next change re-arms
						w.EnableRaisingEvents = true;
						_watchers.Add(w);
					}
					catch
					{
					}
				}
				// Reliable backstop: a standard user has no list/traverse rights on the $Recycle.Bin CONTAINER, so the
				// FileSystemWatcher above can miss an empty performed through the Windows UI. Poll SHQueryRecycleBin and
				// refresh the icon whenever the bin crosses the empty<->non-empty boundary (the only time the glyph changes).
				try { _poll = new Timer(delegate { PollTick(); }, null, 1500, 2000); } catch { }
				Logger.Log($"RecycleBinWatcher: watching {_watchers.Count} recycle folder(s) + SHQueryRecycleBin poll for instant empty/full icon updates.");
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

	private static void PollTick() => Evaluate(forceRefresh: false);

	// Single source of truth for the empty<->full swap. Queries the bin, keeps the rendered Recycle Bin (Default) icon in
	// sync with actual state, then tells the shell to redraw it. forceRefresh=true (FSW change, or an explicit RefreshNow)
	// always re-syncs + redraws; the backstop poll (forceRefresh=false) only acts on an empty<->non-empty transition so it
	// stays cheap at idle. Both paths update (Default) BEFORE redrawing — otherwise the pinned icon would never change.
	private static void Evaluate(bool forceRefresh)
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
			if (transition || _lastEmpty == -1 || forceRefresh)
			{
				SystemIcons81.SetRecycleDefault(empty == 1);
				RefreshNow();
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

	// Public so the shell can also force an immediate refresh right after an in-app "empty recycle bin" action.
	internal static void RefreshNow()
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
