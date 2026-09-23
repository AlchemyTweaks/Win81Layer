using System;
using System.Runtime.InteropServices;

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

	// The current folder of the foreground File Explorer window (Shell.Application match on HWND). null if not an Explorer
	// window / unavailable. Reliable — the same Shell.Application COM the rest of the shell uses.
	public static string? ExplorerFolderPath()
	{
		nint h = ForegroundContext.Hwnd;
		if (h == IntPtr.Zero)
		{
			return null;
		}
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
