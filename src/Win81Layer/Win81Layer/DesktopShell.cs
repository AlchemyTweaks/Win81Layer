using System;
using System.Collections.Specialized;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;

namespace Win81Layer;

internal static class DesktopShell
{
	[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
	private struct SHFILEOPSTRUCT
	{
		public nint hwnd;

		public uint wFunc;

		[MarshalAs(UnmanagedType.LPWStr)]
		public string pFrom;

		[MarshalAs(UnmanagedType.LPWStr)]
		public string pTo;

		public ushort fFlags;

		[MarshalAs(UnmanagedType.Bool)]
		public bool fAnyOperationsAborted;

		public nint hNameMappings;

		[MarshalAs(UnmanagedType.LPWStr)]
		public string lpszProgressTitle;
	}

	private const uint SHCNE_CREATE = 2u;

	private const uint SHCNE_MKDIR = 8u;

	private const uint SHCNE_UPDATEDIR = 4096u;

	private const uint SHCNF_PATHW = 5u;

	// Blocks until the shell has processed the event (~15 ms); only ever sent through NotifyFlushedAsync, so no caller waits.
	private const uint SHCNF_FLUSH = 4096u;

	private const uint FO_MOVE = 1u;

	private const uint FO_COPY = 2u;

	private const ushort FOF_ALLOWUNDO = 64;

	private const ushort FOF_NOCONFIRMMKDIR = 512;

	private static string DesktopDir => Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);

	[DllImport("shell32.dll", CharSet = CharSet.Unicode)]
	private static extern void SHChangeNotify(uint eventId, uint flags, string item1, nint item2);

	[DllImport("user32.dll")]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static extern bool IsClipboardFormatAvailable(uint format);

	// Flushed notify (the shell applies it at once) handed to the thread pool, so a hung Explorer can never stall the
	// caller (a ShellLaunch worker, or RecycleBinWatcher while it holds its evaluation lock).
	private static void NotifyFlushedAsync(uint eventId, string path)
	{
		System.Threading.ThreadPool.UnsafeQueueUserWorkItem(delegate
		{
			try
			{
				SHChangeNotify(eventId, SHCNF_PATHW | SHCNF_FLUSH, path, IntPtr.Zero);
			}
			catch (Exception ex)
			{
				Logger.Log("Shell notify failed: " + ex.Message);
			}
		}, null);
	}

	internal static void Refresh()
	{
		try
		{
			NotifyFlushedAsync(SHCNE_UPDATEDIR, DesktopDir);
		}
		catch (Exception ex)
		{
			Logger.Log("Desktop refresh failed: " + ex.Message);
		}
	}

	internal static bool CanPaste()
	{
		try
		{
			// CF_HDROP can be tested without opening/OLE-marshalling the clipboard, so a busy clipboard cannot stall a menu.
			return IsClipboardFormatAvailable(15u);
		}
		catch
		{
			return false;
		}
	}

	internal static void Paste()
	{
		ShellLaunch.Run(delegate
		{
			try
			{
				if (!Clipboard.ContainsFileDropList())
				{
					return;
				}
				StringCollection files = Clipboard.GetFileDropList();
				if (files.Count == 0)
				{
					return;
				}
				bool move = false;
				try
				{
					if (Clipboard.GetData("Preferred DropEffect") is MemoryStream ms && ms.Length >= 4)
					{
						byte[] buf = new byte[4];
						ms.ReadExactly(buf, 0, 4);
						int effect = BitConverter.ToInt32(buf, 0);
						move = (effect & 2) != 0;
					}
				}
				catch
				{
				}
				string from = string.Join('\0', files.Cast<string>()) + "\0\0";
				string to = DesktopDir + "\0\0";
				SHFILEOPSTRUCT op = new SHFILEOPSTRUCT
				{
					wFunc = (move ? 1u : 2u),
					pFrom = from,
					pTo = to,
					fFlags = 576
				};
				SHFileOperation(ref op);
			}
			catch (Exception ex)
			{
				Logger.Log("Desktop paste failed: " + ex.Message);
			}
		});
	}

	internal static void NewFolder()
	{
		ShellLaunch.Run(delegate
		{
			try
			{
				string baseName = "New folder";
				string path = Path.Combine(DesktopDir, baseName);
				int i = 2;
				while (Directory.Exists(path) || File.Exists(path))
				{
					path = Path.Combine(DesktopDir, $"{baseName} ({i})");
					i++;
				}
				Directory.CreateDirectory(path);
				// Without an explicit flushed notify the desktop waits on its own file-system poll (0.3-1.3 s).
				NotifyFlushedAsync(SHCNE_MKDIR, path);
			}
			catch (Exception ex)
			{
				Logger.Log("New folder failed: " + ex.Message);
			}
		});
	}

	// Create a new file on the desktop from a ShellNew template (authentic Windows "New >" behaviour).
	internal static void NewFile(ShellNewItem item)
	{
		ShellLaunch.Run(delegate
		{
			try
			{
				string ext = (item.Ext.StartsWith('.') ? item.Ext : ("." + item.Ext));
				string baseName = "New " + item.Label;
				string path = Path.Combine(DesktopDir, baseName + ext);
				int i = 2;
				while (File.Exists(path) || Directory.Exists(path))
				{
					path = Path.Combine(DesktopDir, $"{baseName} ({i}){ext}");
					i++;
				}
				switch (item.Kind)
				{
				case ShellNewKind.FileName:
					if (item.Template == null || !File.Exists(item.Template))
					{
						return;
					}
					File.Copy(item.Template, path, overwrite: false);
					break;
				case ShellNewKind.Data:
					File.WriteAllBytes(path, item.Data ?? Array.Empty<byte>());
					break;
				default:
					using (File.Create(path))
					{
					}
					break;
				}
				// Announce just the new item; a whole-desktop UPDATEDIR re-enumerates every icon and flickers.
				NotifyFlushedAsync(SHCNE_CREATE, path);
			}
			catch (Exception ex)
			{
				Logger.Log("New file (" + item.Ext + ") failed: " + ex.Message);
			}
		});
	}

	// Authentic Windows "New > Shortcut": launch the real Create Shortcut wizard (rundll32 appwiz.cpl,NewLinkHere)
	// so the user browses for a target exactly like Explorer, no custom UI.
	internal static void NewShortcut()
	{
		string dir = DesktopDir;
		ShellLaunch.Run(delegate
		{
			try
			{
				Process.Start(new ProcessStartInfo("rundll32.exe")
				{
					Arguments = "appwiz.cpl,NewLinkHere " + dir,
					UseShellExecute = true
				});
			}
			catch (Exception ex)
			{
				Logger.Log("New shortcut failed: " + ex.Message);
			}
		});
	}

	internal static void OpenTerminalHere()
	{
		string dir = DesktopDir;
		ShellLaunch.Run(delegate
		{
			try
			{
				Process.Start(new ProcessStartInfo("cmd.exe")
				{
					UseShellExecute = true,
					WorkingDirectory = dir
				});
			}
			catch (Exception ex)
			{
				Logger.Log("Open command prompt here failed: " + ex.Message);
			}
		});
	}

	internal static void OpenPowerShellHere()
	{
		string dir = DesktopDir;
		ShellLaunch.Run(delegate
		{
			try
			{
				Process.Start(new ProcessStartInfo("powershell.exe")
				{
					UseShellExecute = true,
					WorkingDirectory = dir
				});
			}
			catch (Exception ex)
			{
				Logger.Log("Open PowerShell here failed: " + ex.Message);
			}
		});
	}

	// Elevated PowerShell in the Desktop folder (UAC prompt via the "runas" verb). A cancelled UAC prompt throws
	// Win32Exception 1223 which is swallowed here — never breaks the shell.
	internal static void OpenPowerShellHereAdmin()
	{
		string dir = DesktopDir;
		ShellLaunch.Run(delegate
		{
			try
			{
				Process.Start(new ProcessStartInfo("powershell.exe")
				{
					UseShellExecute = true,
					Verb = "runas",
					WorkingDirectory = dir
				});
			}
			catch (Exception ex)
			{
				Logger.Log("Open PowerShell here (admin) failed/cancelled: " + ex.Message);
			}
		});
	}

	[DllImport("shell32.dll", CharSet = CharSet.Unicode)]
	private static extern int SHFileOperation(ref SHFILEOPSTRUCT lpFileOp);
}
