using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using Microsoft.Win32;

namespace Win81Layer;

internal enum ShellNewKind
{
	NullFile,
	FileName,
	Data
}

internal sealed class ShellNewItem
{
	public string Label = "";

	public string Ext = "";

	public ShellNewKind Kind;

	public string Template;   // resolved, existing template file (FileName kind)

	public byte[] Data;       // literal bytes (Data kind)
}

// Reproduces Explorer's desktop "New >" submenu by reading the authentic Windows ShellNew registry entries
// (HKCR\.<ext>\ShellNew, or under the progid subkey for Office types). Curated allow-list for speed/safety
// (no HKCR enumeration — that can hang; no Command-based entries). Missing templates auto-drop.
internal static class ShellNewItems
{
	internal static event Action? CacheReady;

	private static readonly string[] Exts = new string[7] { ".txt", ".bmp", ".rtf", ".docx", ".xlsx", ".pptx", ".zip" };

	private static readonly object CacheGate = new object();

	private static List<ShellNewItem>? _cache;

	private static long _cacheAtMs;

	private static int _warming;

	internal static List<ShellNewItem> Enumerate()
	{
		lock (CacheGate)
		{
			if (_cache != null && Environment.TickCount64 - _cacheAtMs < 300000L)
			{
				return new List<ShellNewItem>(_cache);
			}
		}

		List<ShellNewItem> list = EnumerateCore();
		lock (CacheGate)
		{
			_cache = list;
			_cacheAtMs = Environment.TickCount64;
			return new List<ShellNewItem>(_cache);
		}
	}

	internal static List<ShellNewItem> EnumerateFast()
	{
		lock (CacheGate)
		{
			if (_cache != null)
			{
				return new List<ShellNewItem>(_cache);
			}
		}
		Warm();
		return new List<ShellNewItem>();
	}

	internal static void Warm()
	{
		if (Interlocked.Exchange(ref _warming, 1) != 0)
		{
			return;
		}
		Thread thread = new Thread(delegate()
		{
			try
			{
				Enumerate();
				try { CacheReady?.Invoke(); } catch { }
			}
			finally
			{
				Interlocked.Exchange(ref _warming, 0);
			}
		})
		{
			IsBackground = true,
			Name = "ShellNewWarm",
			Priority = ThreadPriority.BelowNormal
		};
		thread.SetApartmentState(ApartmentState.STA);
		thread.Start();
	}

	private static List<ShellNewItem> EnumerateCore()
	{
		List<ShellNewItem> list = new List<ShellNewItem>();
		foreach (string ext in Exts)
		{
			try
			{
				ShellNewItem item = Read(ext);
				if (item != null)
				{
					list.Add(item);
				}
			}
			catch (Exception ex)
			{
				Logger.Log("ShellNew enumerate " + ext + " failed: " + ex.Message);
			}
		}
		return list;
	}

	private static ShellNewItem Read(string ext)
	{
		string progid;
		string friendly = null;
		using (RegistryKey extKey = Registry.ClassesRoot.OpenSubKey(ext))
		{
			if (extKey == null)
			{
				return null;
			}
			progid = extKey.GetValue(null) as string;
		}
		if (!string.IsNullOrEmpty(progid))
		{
			using RegistryKey pk = Registry.ClassesRoot.OpenSubKey(progid);
			friendly = pk?.GetValue(null) as string;
		}
		// ShellNew sits directly under the ext, or under the progid subkey (Office style).
		using RegistryKey sn = Registry.ClassesRoot.OpenSubKey(ext + "\\ShellNew")
			?? ((progid != null) ? Registry.ClassesRoot.OpenSubKey(ext + "\\" + progid + "\\ShellNew") : null);
		if (sn == null)
		{
			return null;
		}
		ShellNewItem item = new ShellNewItem
		{
			Ext = ext,
			Label = (!string.IsNullOrWhiteSpace(friendly) ? friendly : (ext.TrimStart('.').ToUpperInvariant() + " File"))
		};
		// Priority mirrors Explorer: FileName (template) > NullFile > Data. Command is ignored.
		if (sn.GetValue("FileName") is string fn && fn.Length > 0)
		{
			string tpl = ResolveTemplate(fn);
			if (tpl == null)
			{
				return null;   // template missing → skip (safe)
			}
			item.Kind = ShellNewKind.FileName;
			item.Template = tpl;
			return item;
		}
		if (sn.GetValue("NullFile") != null)
		{
			item.Kind = ShellNewKind.NullFile;
			return item;
		}
		object data = sn.GetValue("Data");
		if (data != null)
		{
			item.Kind = ShellNewKind.Data;
			item.Data = ((data is byte[] b) ? b : Encoding.UTF8.GetBytes(data.ToString() ?? ""));
			return item;
		}
		return null;   // only Command / empty → skip
	}

	private static string ResolveTemplate(string fileName)
	{
		try
		{
			if (Path.IsPathRooted(fileName))
			{
				return File.Exists(fileName) ? fileName : null;
			}
			string p = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "ShellNew", fileName);
			return File.Exists(p) ? p : null;
		}
		catch
		{
			return null;
		}
	}
}
