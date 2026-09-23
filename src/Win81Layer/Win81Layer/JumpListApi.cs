#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;

namespace Win81Layer;

public static class JumpListApi
{
	private sealed class RecentCacheEntry
	{
		public List<JumpList.Item> Items = new List<JumpList.Item>();

		public long AtMs;
	}

	private static readonly object CacheGate = new object();

	private static readonly Dictionary<string, RecentCacheEntry> RecentCache = new Dictionary<string, RecentCacheEntry>(StringComparer.OrdinalIgnoreCase);

	private static readonly Dictionary<string, List<Action<List<JumpList.Item>>>> RecentWaiters = new Dictionary<string, List<Action<List<JumpList.Item>>>>(StringComparer.OrdinalIgnoreCase);

	private static readonly Dictionary<string, string> AppIdCache = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

	private static readonly HashSet<string> AppIdMisses = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

	private const long RecentCacheMs = 10000L;

	private enum APPDOCLISTTYPE
	{
		RECENT,
		FREQUENT
	}

	[ComImport]
	[Guid("86bec222-30f2-47e0-9f25-60d11cd75c28")]
	private class ApplicationDocumentLists
	{
	}

	[ComImport]
	[Guid("3c594f9f-9f30-47a1-979a-c9e83d3d0a06")]
	[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
	private interface IApplicationDocumentLists
	{
		void SetAppID([MarshalAs(UnmanagedType.LPWStr)] string appId);

		void GetList(APPDOCLISTTYPE listType, uint cItemsDesired, ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out object ppv);
	}

	[ComImport]
	[Guid("92CA9DCD-5622-4bba-A805-5E9F541BD8C9")]
	[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
	private interface IObjectArray
	{
		void GetCount(out uint count);

		void GetAt(uint index, ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out object ppv);
	}

	[ComImport]
	[Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe")]
	[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
	private interface IShellItem
	{
		void BindToHandler(nint pbc, ref Guid bhid, ref Guid riid, out nint ppv);

		void GetParent(out IShellItem ppsi);

		void GetDisplayName(uint sigdn, out nint ppszName);

		void GetAttributes(uint mask, out uint attribs);

		void Compare(IShellItem psi, uint hint, out int order);
	}

	[ComImport]
	[Guid("886d8eeb-8cf2-4446-8d02-cdba1dbdcf99")]
	[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
	private interface IPropertyStore
	{
		void GetCount(out uint count);

		void GetAt(uint index, out PROPERTYKEY key);

		void GetValue(ref PROPERTYKEY key, out PROPVARIANT pv);

		void SetValue(ref PROPERTYKEY key, ref PROPVARIANT pv);

		void Commit();
	}

	private struct PROPERTYKEY
	{
		public Guid fmtid;

		public uint pid;
	}

	private struct PROPVARIANT
	{
		public ushort vt;

		public ushort r1;

		public ushort r2;

		public ushort r3;

		public nint p;

		public nint p2;
	}

	private const uint SIGDN_NORMALDISPLAY = 0u;

	private const uint SIGDN_FILESYSPATH = 2147844096u;

	private const int GPS_DEFAULT = 0;

	private static readonly PROPERTYKEY PKEY_AppUserModel_ID = new PROPERTYKEY
	{
		fmtid = new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"),
		pid = 5u
	};

	public static List<JumpList.Item> GetRecent(string? appId, int max = 10)
	{
		if (string.IsNullOrEmpty(appId))
		{
			return new List<JumpList.Item>();
		}
		lock (CacheGate)
		{
			if (RecentCache.TryGetValue(appId, out RecentCacheEntry? cached) && cached != null && Environment.TickCount64 - cached.AtMs < RecentCacheMs)
			{
				return cached.Items.Take(max).ToList();
			}
		}
		List<JumpList.Item> items = GetRecentCore(appId, max);
		StoreRecent(appId, items);
		return items;
	}

	public static bool TryGetRecentCached(string? appId, int max, out List<JumpList.Item> items)
	{
		items = new List<JumpList.Item>();
		if (string.IsNullOrEmpty(appId))
		{
			return true;
		}
		bool stale;
		lock (CacheGate)
		{
			if (!RecentCache.TryGetValue(appId, out RecentCacheEntry? cached) || cached == null)
			{
				return false;
			}
			items = cached.Items.Take(max).ToList();
			stale = Environment.TickCount64 - cached.AtMs >= RecentCacheMs;
		}
		if (stale)
		{
			GetRecentAsync(appId, max, delegate { });
		}
		return true;
	}

	public static void GetRecentAsync(string? appId, int max, Action<List<JumpList.Item>> ready)
	{
		if (ready == null)
		{
			return;
		}
		if (string.IsNullOrEmpty(appId))
		{
			ready(new List<JumpList.Item>());
			return;
		}
		List<JumpList.Item>? immediate = null;
		lock (CacheGate)
		{
			if (RecentCache.TryGetValue(appId, out RecentCacheEntry? cached) && cached != null && Environment.TickCount64 - cached.AtMs < RecentCacheMs)
			{
				immediate = cached.Items.Take(max).ToList();
			}
			else if (RecentWaiters.TryGetValue(appId, out List<Action<List<JumpList.Item>>>? waiters) && waiters != null)
			{
				waiters.Add(ready);
				return;
			}
			else
			{
				RecentWaiters[appId] = new List<Action<List<JumpList.Item>>> { ready };
			}
		}
		if (immediate != null)
		{
			ready(immediate);
			return;
		}

		string id = appId;
		Thread thread = new Thread(delegate()
		{
			List<JumpList.Item> result = GetRecentCore(id, Math.Max(16, max));
			StoreRecent(id, result);
			List<Action<List<JumpList.Item>>> callbacks;
			lock (CacheGate)
			{
				callbacks = RecentWaiters.TryGetValue(id, out List<Action<List<JumpList.Item>>>? found) && found != null
					? found
					: new List<Action<List<JumpList.Item>>>();
				RecentWaiters.Remove(id);
			}
			foreach (Action<List<JumpList.Item>> callback in callbacks)
			{
				try { callback(result.Take(max).ToList()); } catch { }
			}
		})
		{
			IsBackground = true,
			Name = "JumpListRefresh",
			Priority = ThreadPriority.BelowNormal
		};
		thread.SetApartmentState(ApartmentState.STA);
		thread.Start();
	}

	private static void StoreRecent(string appId, List<JumpList.Item> items)
	{
		lock (CacheGate)
		{
			RecentCache[appId] = new RecentCacheEntry
			{
				Items = new List<JumpList.Item>(items),
				AtMs = Environment.TickCount64
			};
		}
	}

	private static List<JumpList.Item> GetRecentCore(string appId, int max)
	{
		try
		{
			IApplicationDocumentLists lists = (IApplicationDocumentLists)new ApplicationDocumentLists();
			lists.SetAppID(appId);
			Guid iidArray = typeof(IObjectArray).GUID;
			lists.GetList(APPDOCLISTTYPE.RECENT, (uint)max, ref iidArray, out object arrObj);
			if (!(arrObj is IObjectArray arr))
			{
				return new List<JumpList.Item>();
			}
			List<JumpList.Item> items = new List<JumpList.Item>();
			arr.GetCount(out var count);
			Guid iidItem = typeof(IShellItem).GUID;
			for (uint i = 0u; i < count; i++)
			{
				if (items.Count >= max)
				{
					break;
				}
				arr.GetAt(i, ref iidItem, out object o);
				if (o is IShellItem si)
				{
					string path = Name(si, 2147844096u) ?? Name(si, 0u) ?? "";
					string name = Name(si, 0u) ?? Path.GetFileName(path);
					if (!string.IsNullOrWhiteSpace(path))
					{
						items.Add(new JumpList.Item(path, name));
					}
				}
			}
			return items;
		}
		catch (Exception ex)
		{
			Logger.Log("JumpListApi failed for " + appId + ": " + ex.Message);
			return new List<JumpList.Item>();
		}
	}

	private static string? Name(IShellItem si, uint sigdn)
	{
		try
		{
			si.GetDisplayName(sigdn, out var p);
			try
			{
				return Marshal.PtrToStringUni(p);
			}
			finally
			{
				Marshal.FreeCoTaskMem(p);
			}
		}
		catch
		{
			return null;
		}
	}

	public static string? GetAppId(string? launchPath)
	{
		if (string.IsNullOrEmpty(launchPath))
		{
			return null;
		}
		lock (CacheGate)
		{
			if (AppIdCache.TryGetValue(launchPath, out string? cached) && cached != null)
			{
				return cached;
			}
			if (AppIdMisses.Contains(launchPath))
			{
				return null;
			}
		}
		string? appId = TryGetAppId(launchPath);
		lock (CacheGate)
		{
			if (string.IsNullOrEmpty(appId))
			{
				AppIdMisses.Add(launchPath);
			}
			else
			{
				AppIdCache[launchPath] = appId;
			}
		}
		return appId;
	}

	private static string? TryGetAppId(string launchPath)
	{
		if (launchPath.StartsWith("shell:AppsFolder\\", StringComparison.OrdinalIgnoreCase))
		{
			int length = "shell:AppsFolder\\".Length;
			string id = launchPath.Substring(length, launchPath.Length - length);
			if (id.Contains('!'))
			{
				return id;
			}
		}
		try
		{
			Guid iid = typeof(IPropertyStore).GUID;
			if (SHGetPropertyStoreFromParsingName(launchPath, IntPtr.Zero, 0, ref iid, out IPropertyStore ps) != 0 || ps == null)
			{
				return null;
			}
			PROPERTYKEY key = PKEY_AppUserModel_ID;
			ps.GetValue(ref key, out var pv);
			try
			{
				return (pv.vt == 31 && pv.p != IntPtr.Zero) ? Marshal.PtrToStringUni(pv.p) : null;
			}
			finally
			{
				PropVariantClear(ref pv);
			}
		}
		catch
		{
			return null;
		}
	}

	[DllImport("shell32.dll")]
	private static extern int SHGetPropertyStoreFromParsingName([MarshalAs(UnmanagedType.LPWStr)] string path, nint bindCtx, int flags, ref Guid riid, out IPropertyStore propertyStore);

	[DllImport("ole32.dll")]
	private static extern int PropVariantClear(ref PROPVARIANT pv);
}
