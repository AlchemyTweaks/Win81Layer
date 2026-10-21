using System;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace Win81Layer;

// Reliable, non-fragile facts about the app that was foreground when the Charms bar opened (ForegroundContext captures the
// HWND at that moment). Deliberately avoids fragile scraping (no keystroke URL grabs) — only exe/title/Explorer-folder,
// which are all obtainable robustly. Feeds the context-aware Share pane.
internal static class ShareActions
{
	public static string ForegroundTitle()
	{
		nint h = ForegroundContext.Hwnd;
		if (h == IntPtr.Zero)
		{
			return "";
		}
		try { return WindowList.GetTitle(h) ?? ""; }
		catch { return ""; }
	}

	public static string ForegroundAppName()
	{
		nint h = ForegroundContext.Hwnd;
		if (h == IntPtr.Zero)
		{
			return "";
		}
		try
		{
			string? exe = WindowList.GetExePath(h);
			return string.IsNullOrEmpty(exe) ? "" : System.IO.Path.GetFileNameWithoutExtension(exe);
		}
		catch { return ""; }
	}

	// How long opening the Share pane waits for Explorer's answer.
	internal const int FolderTimeoutMs = 200;

	private static int _folderBusy;

	// The current folder of the foreground File Explorer window (Shell.Application match on HWND). null if not an Explorer
	// window / unavailable. Reliable — the same Shell.Application COM the rest of the shell uses. ShellWindows is served
	// by explorer.exe, so the lookup runs on its own STA thread and the caller (the UI thread, opening Share) waits at
	// most FolderTimeoutMs: an Explorer that is busy or hung costs the pane its folder rows, never the shell its UI.
	// Only a File Explorer window has a folder, so nothing else asks Explorer at all; one lookup at a time.
	public static string ExplorerFolderPath()
	{
		nint h = ForegroundContext.Hwnd;
		if (h == IntPtr.Zero || !IsFileExplorerWindow(h))
		{
			return null;
		}
		if (Interlocked.CompareExchange(ref _folderBusy, 1, 0) != 0)
		{
			return null;
		}
		string found = null;
		ManualResetEvent done = new ManualResetEvent(initialState: false);
		try
		{
			Thread t = new Thread((ThreadStart)delegate
			{
				try
				{
					found = FolderOf(h);
				}
				finally
				{
					Interlocked.Exchange(ref _folderBusy, 0);
					try { done.Set(); } catch { }
				}
			})
			{
				IsBackground = true,
				Name = "ShareFolderLookup"
			};
			t.SetApartmentState(ApartmentState.STA);
			t.Start();
		}
		catch (Exception ex)
		{
			Interlocked.Exchange(ref _folderBusy, 0);
			Logger.Log("share folder: " + ex.Message);
			return null;
		}
		if (!done.WaitOne(FolderTimeoutMs))
		{
			Logger.Log($"share folder: Explorer did not answer within {FolderTimeoutMs} ms");
			return null;
		}
		done.Dispose();
		return found;
	}

	private static bool IsFileExplorerWindow(nint h)
	{
		StringBuilder sb = new StringBuilder(64);
		if (GetClassNameW(h, sb, sb.Capacity) == 0)
		{
			return false;
		}
		string cls = sb.ToString();
		return cls == "CabinetWClass" || cls == "ExploreWClass";
	}

	[DllImport("user32.dll", CharSet = CharSet.Unicode)]
	private static extern int GetClassNameW(nint hWnd, StringBuilder name, int max);

	private static string FolderOf(nint h)
	{
		try
		{
			Type t = Type.GetTypeFromProgID("Shell.Application");
			if (t == null)
			{
				return null;
			}
			dynamic shell = Activator.CreateInstance(t);
			if (shell == null)
			{
				return null;
			}
			try
			{
				dynamic wins = shell.Windows();
				int n = (int)wins.Count;
				for (int i = 0; i < n; i++)
				{
					dynamic w = wins.Item(i);
					if (w == null)
					{
						continue;
					}
					try
					{
						long wh = (long)w.HWND;
						if (wh == (long)h)
						{
							string? p = w.Document?.Folder?.Self?.Path as string;
							return string.IsNullOrEmpty(p) ? null : p;
						}
					}
					catch { }
				}
			}
			finally
			{
				try { Marshal.FinalReleaseComObject(shell); } catch { }
			}
		}
		catch (Exception ex)
		{
			Logger.Log("share folder: " + ex.Message);
		}
		return null;
	}

	public static void CopyText(string s)
	{
		if (string.IsNullOrEmpty(s))
		{
			return;
		}
		try { System.Windows.Clipboard.SetText(s); }
		catch
		{
			try { System.Windows.Clipboard.SetDataObject(s, copy: true); }
			catch (Exception ex) { Logger.Log("share copy: " + ex.Message); }
		}
	}
}
