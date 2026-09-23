using System;
using System.Collections.Specialized;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;

namespace Win81Layer;

internal static class FileShell
{
	[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
	private struct SHFILEOPSTRUCT
	{
		public nint hwnd;

		public uint wFunc;

		[MarshalAs(UnmanagedType.LPWStr)]
		public string pFrom;

		[MarshalAs(UnmanagedType.LPWStr)]
		public string? pTo;

		public ushort fFlags;

		[MarshalAs(UnmanagedType.Bool)]
		public bool fAnyOperationsAborted;

		public nint hNameMappings;

		[MarshalAs(UnmanagedType.LPWStr)]
		public string? lpszProgressTitle;
	}

	[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
	private struct SHELLEXECUTEINFO
	{
		public int cbSize;

		public uint fMask;

		public nint hwnd;

		[MarshalAs(UnmanagedType.LPWStr)]
		public string? lpVerb;

		[MarshalAs(UnmanagedType.LPWStr)]
		public string? lpFile;

		[MarshalAs(UnmanagedType.LPWStr)]
		public string? lpParameters;

		[MarshalAs(UnmanagedType.LPWStr)]
		public string? lpDirectory;

		public int nShow;

		public nint hInstApp;

		public nint lpIDList;

		[MarshalAs(UnmanagedType.LPWStr)]
		public string? lpClass;

		public nint hkeyClass;

		public uint dwHotKey;

		public nint hIcon;

		public nint hProcess;
	}

	private const uint FO_MOVE = 1u;

	private const uint FO_COPY = 2u;

	private const uint FO_DELETE = 3u;

	private const ushort FOF_ALLOWUNDO = 64;

	private const ushort FOF_WANTNUKEWARNING = 16384;

	private const uint SEE_MASK_INVOKEIDLIST = 12u;

	private const int SW_SHOW = 5;

	internal static void Open(string path)
	{
		Start(path);
	}

	private static void Start(string path)
	{
		ShellLaunch.Run(delegate
		{
			try
			{
				Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
			}
			catch (Exception ex)
			{
				Logger.Log("open " + path + ": " + ex.Message);
			}
		});
	}

	internal static void OpenLocation(string path)
	{
		try
		{
			if (FileBrowser.TryShowLocation(path))
			{
				return;
			}
			if (File.Exists(path) || Directory.Exists(path))
			{
				ShellLaunch.Run(delegate
				{
					Process.Start(new ProcessStartInfo("explorer.exe", "/select,\"" + path + "\"") { UseShellExecute = true });
				});
			}
		}
		catch (Exception ex)
		{
			Logger.Log("reveal " + path + ": " + ex.Message);
		}
	}

	internal static void OpenWith(string path)
	{
		ShellLaunch.Run(delegate
		{
			try
			{
				Process.Start(new ProcessStartInfo("rundll32.exe", "shell32.dll,OpenAs_RunDLL " + path) { UseShellExecute = true });
			}
			catch (Exception ex)
			{
				Logger.Log("openwith " + path + ": " + ex.Message);
			}
		});
	}

	internal static void RunAsAdmin(string path)
	{
		ShellLaunch.Run(delegate
		{
			try
			{
				Process.Start(new ProcessStartInfo(path)
				{
					UseShellExecute = true,
					Verb = "runas",
					WorkingDirectory = (Path.GetDirectoryName(path) ?? "")
				});
			}
			catch (Exception ex)
			{
				Logger.Log("runas " + path + " (or cancelled): " + ex.Message);
			}
		});
	}

	internal static void TerminalHere(string folder, bool powershell)
	{
		ShellLaunch.Run(delegate
		{
			try
			{
				Process.Start(new ProcessStartInfo(powershell ? "powershell.exe" : "cmd.exe")
				{
					UseShellExecute = true,
					WorkingDirectory = folder
				});
			}
			catch (Exception ex)
			{
				Logger.Log("terminal here " + folder + ": " + ex.Message);
			}
		});
	}

	internal static void CopyPath(string path, bool quoted)
	{
		try
		{
			Clipboard.SetText(quoted ? ("\"" + path + "\"") : path);
		}
		catch (Exception ex)
		{
			Logger.Log("copypath " + path + ": " + ex.Message);
		}
	}

	internal static void Copy(string path)
	{
		SetClipboardFiles(path, move: false);
	}

	internal static void Cut(string path)
	{
		SetClipboardFiles(path, move: true);
	}

	private static void SetClipboardFiles(string path, bool move)
	{
		try
		{
			StringCollection files = new StringCollection { path };
			DataObject data = new DataObject();
			data.SetFileDropList(files);
			MemoryStream effect = new MemoryStream(BitConverter.GetBytes((!move) ? 1 : 2));
			data.SetData("Preferred DropEffect", effect);
			Clipboard.SetDataObject(data, copy: true);
		}
		catch (Exception ex)
		{
			Logger.Log("clipboard " + path + ": " + ex.Message);
		}
	}

	internal static void Recycle(string path)
	{
		ShellLaunch.Run(delegate
		{
			try
			{
				SHFILEOPSTRUCT op = new SHFILEOPSTRUCT
				{
					wFunc = 3u,
					pFrom = path + "\0\0",
					fFlags = 16448
				};
				int rc = SHFileOperation(ref op);
				if (rc != 0)
				{
					Logger.Log($"recycle {path}: SHFileOperation rc={rc}");
				}
			}
			catch (Exception ex)
			{
				Logger.Log("recycle " + path + ": " + ex.Message);
			}
		});
	}

	internal static bool Rename(string path, string newName)
	{
		try
		{
			if (string.IsNullOrWhiteSpace(newName))
			{
				return false;
			}
			if (newName != Path.GetFileName(newName) || Path.IsPathRooted(newName) || newName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
			{
				Logger.Log("rename rejected (not a bare name): " + newName);
				return false;
			}
			string dir = Path.GetDirectoryName(path);
			if (dir == null)
			{
				return false;
			}
			string dest = Path.Combine(dir, newName);
			if (File.Exists(dest) || Directory.Exists(dest))
			{
				Logger.Log("rename target exists: " + dest);
				return false;
			}
			if (Directory.Exists(path))
			{
				Directory.Move(path, dest);
			}
			else
			{
				File.Move(path, dest);
			}
			return true;
		}
		catch (Exception ex)
		{
			Logger.Log("rename " + path + ": " + ex.Message);
			return false;
		}
	}

	internal static void PasteInto(string destDir)
	{
		ShellLaunch.Run(delegate
		{
			try
			{
				StringCollection files = Clipboard.GetFileDropList();
				if (files.Count == 0)
				{
					return;
				}
				bool move = false;
				if (Clipboard.GetData("Preferred DropEffect") is MemoryStream ms && ms.Length >= 4)
				{
					byte[] buf = new byte[4];
					ms.Position = 0L;
					ms.Read(buf, 0, 4);
					move = (BitConverter.ToInt32(buf, 0) & 2) != 0;
				}
				StringBuilder sb = new StringBuilder();
				StringEnumerator enumerator = files.GetEnumerator();
				try
				{
					while (enumerator.MoveNext())
					{
						string f = enumerator.Current;
						sb.Append(f);
						sb.Append('\0');
					}
				}
				finally
				{
					if (enumerator is IDisposable disposable)
					{
						disposable.Dispose();
					}
				}
				sb.Append('\0');
				SHFILEOPSTRUCT op = new SHFILEOPSTRUCT
				{
					wFunc = (move ? 1u : 2u),
					pFrom = sb.ToString(),
					pTo = destDir + "\0\0",
					fFlags = 64
				};
				int rc = SHFileOperation(ref op);
				if (!move || rc != 0 || op.fAnyOperationsAborted)
				{
					return;
				}
				try
				{
					Clipboard.Clear();
				}
				catch
				{
				}
			}
			catch (Exception ex)
			{
				Logger.Log("paste into " + destDir + ": " + ex.Message);
			}
		});
	}

	internal static void CreateShortcut(string targetPath)
	{
		ShellLaunch.Run(delegate
		{
			try
			{
				string dir = Path.GetDirectoryName(targetPath);
				if (dir == null)
				{
					Logger.Log("create shortcut skipped (no parent, e.g. drive root): " + targetPath);
					return;
				}
				string stem = (Directory.Exists(targetPath) ? Path.GetFileName(targetPath) : Path.GetFileNameWithoutExtension(targetPath));
				string lnk = Path.Combine(dir, stem + " - Shortcut.lnk");
				int n = 2;
				while (File.Exists(lnk))
				{
					lnk = Path.Combine(dir, $"{stem} - Shortcut ({n}).lnk");
					n++;
				}
				Type shellType = Type.GetTypeFromProgID("WScript.Shell");
				if ((object)shellType == null)
				{
					return;
				}
				dynamic shell = Activator.CreateInstance(shellType);
				if ((object)shell == null)
				{
					return;
				}
				dynamic sc = null;
				try
				{
					sc = shell.CreateShortcut(lnk);
					sc.TargetPath = targetPath;
					sc.WorkingDirectory = (Directory.Exists(targetPath) ? targetPath : dir);
					sc.Save();
				}
				finally
				{
					try
					{
						if (sc != null)
						{
							Marshal.FinalReleaseComObject(sc);
						}
					}
					catch
					{
					}
					try
					{
						Marshal.FinalReleaseComObject(shell);
					}
					catch
					{
					}
				}
			}
			catch (Exception ex)
			{
				Logger.Log("create shortcut " + targetPath + ": " + ex.Message);
			}
		});
	}

	// The user's shell:sendto folder, real .lnk entries only (skips the .DeskLink/.MAPIMail/.ZFSendToTarget COM handlers,
	// which a plain ShellExecute-with-arg only half-drives — the two common ones are provided natively below instead).
	internal static System.Collections.Generic.List<(string Name, string LnkPath)> SendToLnks()
	{
		System.Collections.Generic.List<(string, string)> list = new System.Collections.Generic.List<(string, string)>();
		try
		{
			string dir = Environment.GetFolderPath(Environment.SpecialFolder.SendTo);
			if (Directory.Exists(dir))
			{
				foreach (string f in Directory.GetFiles(dir, "*.lnk"))
				{
					list.Add((Path.GetFileNameWithoutExtension(f), f));
				}
			}
		}
		catch (Exception ex)
		{
			Logger.Log("SendTo enumerate: " + ex.Message);
		}
		return list;
	}

	// Invoke a SendTo .lnk with the target file as its argument (the shell resolves the .lnk and passes the path).
	internal static void SendToLnk(string lnkPath, string filePath)
	{
		ShellLaunch.Run(delegate
		{
			try
			{
				Process.Start(new ProcessStartInfo(lnkPath)
				{
					UseShellExecute = true,
					Arguments = "\"" + filePath + "\""
				});
			}
			catch (Exception ex)
			{
				Logger.Log("SendTo lnk " + lnkPath + ": " + ex.Message);
			}
		});
	}

	// "Send to -> Desktop (create shortcut)" — native equivalent of the .DeskLink handler.
	internal static void SendToDesktopShortcut(string targetPath)
	{
		ShellLaunch.Run(delegate
		{
			try
			{
				string desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
				string stem = Directory.Exists(targetPath) ? Path.GetFileName(targetPath) : Path.GetFileNameWithoutExtension(targetPath);
				string lnk = Path.Combine(desktop, stem + ".lnk");
				int n = 2;
				while (File.Exists(lnk))
				{
					lnk = Path.Combine(desktop, $"{stem} ({n}).lnk");
					n++;
				}
				Type shellType = Type.GetTypeFromProgID("WScript.Shell");
				if ((object)shellType == null)
				{
					return;
				}
				dynamic shell = Activator.CreateInstance(shellType);
				if ((object)shell == null)
				{
					return;
				}
				dynamic sc = null;
				try
				{
					sc = shell.CreateShortcut(lnk);
					sc.TargetPath = targetPath;
					sc.WorkingDirectory = Directory.Exists(targetPath) ? targetPath : (Path.GetDirectoryName(targetPath) ?? desktop);
					sc.Save();
				}
				finally
				{
					try { if (sc != null) { Marshal.FinalReleaseComObject(sc); } } catch { }
					try { Marshal.FinalReleaseComObject(shell); } catch { }
				}
			}
			catch (Exception ex)
			{
				Logger.Log("SendTo desktop shortcut: " + ex.Message);
			}
		});
	}

	// "Send to -> Compressed (zipped) folder" — native equivalent of the .ZFSendToTarget handler.
	internal static void SendToZip(string path)
	{
		ShellLaunch.Run(delegate
		{
			try
			{
				string dir = Path.GetDirectoryName(path);
				if (dir == null)
				{
					return;
				}
				string stem = Directory.Exists(path) ? Path.GetFileName(path) : Path.GetFileNameWithoutExtension(path);
				string zip = Path.Combine(dir, stem + ".zip");
				int n = 2;
				while (File.Exists(zip))
				{
					zip = Path.Combine(dir, $"{stem} ({n}).zip");
					n++;
				}
				if (Directory.Exists(path))
				{
					System.IO.Compression.ZipFile.CreateFromDirectory(path, zip);
				}
				else
				{
					using System.IO.Compression.ZipArchive archive = System.IO.Compression.ZipFile.Open(zip, System.IO.Compression.ZipArchiveMode.Create);
					System.IO.Compression.ZipFileExtensions.CreateEntryFromFile(archive, path, Path.GetFileName(path));
				}
			}
			catch (Exception ex)
			{
				Logger.Log("SendTo zip: " + ex.Message);
			}
		});
	}

	internal static string? NewFolder(string parentDir, string name)
	{
		try
		{
			if (name != Path.GetFileName(name) || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
			{
				Logger.Log("new folder rejected (bad name): " + name);
				return null;
			}
			string dest = Path.Combine(parentDir, name);
			int n = 2;
			while (Directory.Exists(dest) || File.Exists(dest))
			{
				dest = Path.Combine(parentDir, $"{name} ({n})");
				n++;
			}
			Directory.CreateDirectory(dest);
			return dest;
		}
		catch (Exception ex)
		{
			Logger.Log($"new folder {parentDir}\\{name}: {ex.Message}");
			return null;
		}
	}

	internal static void Properties(string path)
	{
		ShellLaunch.Run(delegate
		{
			try
			{
				SHELLEXECUTEINFO info = new SHELLEXECUTEINFO
				{
					cbSize = Marshal.SizeOf<SHELLEXECUTEINFO>(),
					fMask = 12u,
					lpVerb = "properties",
					lpFile = path,
					nShow = 5
				};
				if (!ShellExecuteEx(ref info))
				{
					Logger.Log($"properties {path}: win32 {Marshal.GetLastWin32Error()}");
				}
			}
			catch (Exception ex)
			{
				Logger.Log("properties " + path + ": " + ex.Message);
			}
		});
	}

	[DllImport("shell32.dll", CharSet = CharSet.Unicode)]
	private static extern int SHFileOperation(ref SHFILEOPSTRUCT lpFileOp);

	[DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static extern bool ShellExecuteEx(ref SHELLEXECUTEINFO lpExecInfo);
}
