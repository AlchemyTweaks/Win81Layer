using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;

namespace Win81Layer;

// Resolves an OLE drag payload dropped onto the taskbar into pinnable apps. Two payload kinds are handled:
//   * CF_HDROP / FileDrop  -> filesystem paths (.exe/.lnk) resolved via TaskbarPins.FromDroppedPath.
//   * "Shell IDList Array" -> virtual shell items that have NO file path (UWP apps dragged from
//     shell:AppsFolder, Windows Start-menu app entries) -> resolved to an AppUserModelID (AUMID) pin,
//     the same shape the launcher already stores for Claude/Slack/ChatGPT etc.
// This is what lets the user drag ANY app onto the taskbar and have it pin, not just classic .exe/.lnk.
// Every entry point is exception-safe; the COM here is fully self-contained so it can't destabilise the shell.
internal static class ShellDropResolver
{
	private const string ShellIdListFormat = "Shell IDList Array";
	private const uint SIGDN_NORMALDISPLAY = 0u;
	private const uint SIGDN_FILESYSPATH = 0x80058000u;

	// PKEY_AppUserModel_ID {9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3}, pid 5 -> the AUMID of a shell app item.
	private static readonly PROPERTYKEY PKEY_AppUserModel_ID = new PROPERTYKEY
	{
		fmtid = new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"),
		pid = 5u
	};

	private static Guid IID_IShellItem2 = new Guid("7E9FB0D3-919F-4307-AB2E-9B1860310C93");

	internal static bool IsPinnablePath(string p)
	{
		if (string.IsNullOrWhiteSpace(p))
		{
			return false;
		}
		return p.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
			|| p.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase);
	}

	// Cheap check for DragOver: does the payload look like something we can pin? (Resolved fully only on drop.)
	internal static bool CanPin(System.Windows.IDataObject data)
	{
		try
		{
			if (data.GetDataPresent(System.Windows.DataFormats.FileDrop) && data.GetData(System.Windows.DataFormats.FileDrop) is string[] paths)
			{
				foreach (string p in paths)
				{
					if (IsPinnablePath(p))
					{
						return true;
					}
				}
			}
			// A shell-item drag (UWP/Start-menu app) carries no file path; accept optimistically and resolve on drop.
			if (data.GetDataPresent(ShellIdListFormat))
			{
				return true;
			}
		}
		catch
		{
		}
		return false;
	}

	// Full resolution for Drop. Filesystem paths win; only if none resolve do we parse the Shell IDList Array.
	internal static List<PinnedApp> Resolve(System.Windows.IDataObject data)
	{
		List<PinnedApp> result = new List<PinnedApp>();
		try
		{
			if (data.GetDataPresent(System.Windows.DataFormats.FileDrop) && data.GetData(System.Windows.DataFormats.FileDrop) is string[] paths)
			{
				foreach (string p in paths)
				{
					PinnedApp pin = TaskbarPins.FromDroppedPath(p);
					if (pin != null)
					{
						result.Add(pin);
					}
				}
			}
			if (result.Count == 0 && data.GetDataPresent(ShellIdListFormat))
			{
				ResolveIdList(data, result);
			}
		}
		catch (Exception ex)
		{
			Logger.Log("ShellDropResolver.Resolve: " + ex.Message);
		}
		return result;
	}

	// For the --pindroptest QA hook: resolve a filesystem path OR a shell parsing name ("shell:AppsFolder\<AUMID>").
	internal static PinnedApp ResolveParsingName(string arg)
	{
		try
		{
			if (!string.IsNullOrWhiteSpace(arg) && File.Exists(arg))
			{
				return TaskbarPins.FromDroppedPath(arg);
			}
			Guid iid = IID_IShellItem2;
			if (SHCreateItemFromParsingName(arg, IntPtr.Zero, ref iid, out object o) == 0 && o is IShellItem2 si)
			{
				try
				{
					return FromShellItem(si);
				}
				finally
				{
					Marshal.FinalReleaseComObject(si);
				}
			}
		}
		catch (Exception ex)
		{
			Logger.Log("ShellDropResolver.ResolveParsingName: " + ex.Message);
		}
		return null;
	}

	private static void ResolveIdList(System.Windows.IDataObject data, List<PinnedApp> outp)
	{
		byte[] bytes = null;
		object raw = data.GetData(ShellIdListFormat);
		if (raw is MemoryStream ms)
		{
			bytes = ms.ToArray();
		}
		else if (raw is byte[] b)
		{
			bytes = b;
		}
		if (bytes == null || bytes.Length < 8)
		{
			return;
		}
		// CIDA: UINT cidl; UINT aoffset[cidl+1]; aoffset[0]=parent folder pidl, aoffset[1..]=child pidls (relative).
		uint cidl = BitConverter.ToUInt32(bytes, 0);
		if (cidl == 0 || cidl > 256)
		{
			return;
		}
		uint[] off = new uint[cidl + 1];
		for (int i = 0; i <= cidl; i++)
		{
			int at = 4 + i * 4;
			if (at + 4 > bytes.Length)
			{
				return;
			}
			off[i] = BitConverter.ToUInt32(bytes, at);
		}
		IntPtr parent = MarshalPidl(bytes, (int)off[0]);
		try
		{
			for (int i = 1; i <= cidl; i++)
			{
				IntPtr child = MarshalPidl(bytes, (int)off[i]);
				IntPtr abs = IntPtr.Zero;
				try
				{
					abs = ILCombine(parent, child);
					if (abs == IntPtr.Zero)
					{
						continue;
					}
					Guid iid = IID_IShellItem2;
					if (SHCreateItemFromIDList(abs, ref iid, out object o) == 0 && o is IShellItem2 si)
					{
						try
						{
							PinnedApp pin = FromShellItem(si);
							if (pin != null)
							{
								outp.Add(pin);
							}
						}
						finally
						{
							Marshal.FinalReleaseComObject(si);
						}
					}
				}
				finally
				{
					if (abs != IntPtr.Zero)
					{
						ILFree(abs);
					}
					if (child != IntPtr.Zero)
					{
						Marshal.FreeHGlobal(child);
					}
				}
			}
		}
		finally
		{
			if (parent != IntPtr.Zero)
			{
				Marshal.FreeHGlobal(parent);
			}
		}
	}

	// Copy a self-terminating ITEMIDLIST out of the CIDA buffer at 'offset' into fresh unmanaged memory. The pidl
	// self-terminates on a 2-byte cb==0, so copying through to the end of the buffer is safe (ILCombine stops at it).
	private static IntPtr MarshalPidl(byte[] buffer, int offset)
	{
		if (offset < 0 || offset >= buffer.Length)
		{
			return IntPtr.Zero;
		}
		int len = buffer.Length - offset;
		IntPtr mem = Marshal.AllocHGlobal(len);
		Marshal.Copy(buffer, offset, mem, len);
		return mem;
	}

	private static PinnedApp FromShellItem(IShellItem2 si)
	{
		// 1) Real filesystem item? Pin only .exe/.lnk; ignore folders/documents dragged by mistake.
		string fsPath = Name(si, SIGDN_FILESYSPATH);
		if (!string.IsNullOrEmpty(fsPath))
		{
			return IsPinnablePath(fsPath) ? TaskbarPins.FromDroppedPath(fsPath) : null;
		}
		// 2) Virtual app (UWP / Start-menu entry): pin by AUMID, matching how running windows are keyed.
		string aumid = GetString(si, PKEY_AppUserModel_ID);
		if (!string.IsNullOrWhiteSpace(aumid))
		{
			string name = Name(si, SIGDN_NORMALDISPLAY);
			return new PinnedApp
			{
				Name = (string.IsNullOrWhiteSpace(name) ? aumid : name),
				LaunchPath = "shell:AppsFolder\\" + aumid,
				Aumid = aumid
			};
		}
		return null;
	}

	private static string Name(IShellItem2 si, uint sigdn)
	{
		IntPtr p = IntPtr.Zero;
		try
		{
			si.GetDisplayName(sigdn, out p);
			return (p != IntPtr.Zero) ? Marshal.PtrToStringUni(p) : null;
		}
		catch
		{
			return null;
		}
		finally
		{
			if (p != IntPtr.Zero)
			{
				Marshal.FreeCoTaskMem(p);
			}
		}
	}

	private static string GetString(IShellItem2 si, PROPERTYKEY key)
	{
		IntPtr p = IntPtr.Zero;
		try
		{
			if (si.GetString(ref key, out p) == 0 && p != IntPtr.Zero)
			{
				return Marshal.PtrToStringUni(p);
			}
			return null;
		}
		catch
		{
			return null;
		}
		finally
		{
			if (p != IntPtr.Zero)
			{
				Marshal.FreeCoTaskMem(p);
			}
		}
	}

	[DllImport("shell32.dll")]
	private static extern int SHCreateItemFromIDList(IntPtr pidl, ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out object ppv);

	[DllImport("shell32.dll", CharSet = CharSet.Unicode)]
	private static extern int SHCreateItemFromParsingName(string pszPath, IntPtr pbc, ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out object ppv);

	[DllImport("shell32.dll")]
	private static extern IntPtr ILCombine(IntPtr pidl1, IntPtr pidl2);

	[DllImport("shell32.dll")]
	private static extern void ILFree(IntPtr pidl);

	// IShellItem2 vtable. Only GetDisplayName and GetString are ever called; the earlier slots are declared purely
	// to occupy their vtable positions in the correct order (out-interface params are IntPtr so no RCW is built).
	[ComImport, Guid("7E9FB0D3-919F-4307-AB2E-9B1860310C93"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
	private interface IShellItem2
	{
		// --- IShellItem ---
		void BindToHandler(IntPtr pbc, ref Guid bhid, ref Guid riid, out IntPtr ppv);
		void GetParent(out IntPtr ppsi);
		void GetDisplayName(uint sigdnName, out IntPtr ppszName);
		void GetAttributes(uint sfgaoMask, out uint psfgaoAttribs);
		void Compare(IntPtr psi, uint hint, out int piOrder);
		// --- IShellItem2 (declared through GetString) ---
		void GetPropertyStore(uint flags, ref Guid riid, out IntPtr ppv);
		void GetPropertyStoreWithCreateObject(uint flags, IntPtr punkFactory, ref Guid riid, out IntPtr ppv);
		void GetPropertyStoreForKeys(IntPtr rgKeys, uint cKeys, uint flags, ref Guid riid, out IntPtr ppv);
		void GetPropertyDescriptionList(ref PROPERTYKEY keyType, ref Guid riid, out IntPtr ppv);
		void Update(IntPtr pbc);
		void GetProperty(ref PROPERTYKEY key, IntPtr ppropvar);
		void GetCLSID(ref PROPERTYKEY key, out Guid pclsid);
		void GetFileTime(ref PROPERTYKEY key, out long pft);
		void GetInt32(ref PROPERTYKEY key, out int pi);
		[PreserveSig] int GetString(ref PROPERTYKEY key, out IntPtr ppsz);
	}
}
