using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Win81Layer;

public static class AppInventory
{
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

	private struct SIZE
	{
		public int cx;

		public int cy;
	}

	[ComImport]
	[Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe")]
	[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
	internal interface IShellItem
	{
		void BindToHandler(nint pbc, ref Guid bhid, ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out object ppv);

		void GetParent(out IShellItem ppsi);

		void GetDisplayName(uint sigdnName, out nint ppszName);

		void GetAttributes(uint sfgaoMask, out uint psfgaoAttribs);

		void Compare(IShellItem psi, uint hint, out int piOrder);
	}

	[ComImport]
	[Guid("70629033-e363-4a28-a567-0db78006e6d7")]
	[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
	internal interface IEnumShellItems
	{
		[PreserveSig]
		int Next(uint celt, out IShellItem rgelt, out uint pceltFetched);

		void Skip(uint celt);

		void Reset();

		void Clone(out IEnumShellItems ppenum);
	}

	[ComImport]
	[Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b")]
	[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
	private interface IShellItemImageFactory
	{
		void GetImage(SIZE size, int flags, out nint phbm);
	}

	private static readonly string InvCountPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Win81Layer", "inventory-count.txt");

	private static readonly string[] Palette = new string[16]
	{
		"#2672EC", "#2E8DEF", "#1BA1E2", "#00ABA9", "#008287", "#199900", "#33991A", "#8CBF26", "#A05000", "#D24726",
		"#B01E00", "#C1004F", "#7200AC", "#4617B4", "#006AC1", "#FF981D"
	};

	private const uint SIGDN_NORMALDISPLAY = 0u;

	private const uint SIGDN_PARENTRELATIVEPARSING = 2147581953u;

	private static Guid _bhidPropertyStore = new Guid("0384e1a4-1523-439c-a4c8-ab911052f586");

	private static Guid _iidPropertyStore = typeof(IPropertyStore).GUID;

	private static PROPERTYKEY _pkeyAppId = new PROPERTYKEY
	{
		fmtid = new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"),
		pid = 5u
	};

	private const int SIIGBF_BIGGERSIZEOK = 1;

	private const int SIIGBF_ICONONLY = 4;

	private const int SIIGBF_SCALEUP = 0x100;

	private const int PreferredIconPixels = 256;

	[StructLayout(LayoutKind.Sequential)]
	private struct BITMAP
	{
		public int bmType;
		public int bmWidth;
		public int bmHeight;
		public int bmWidthBytes;
		public ushort bmPlanes;
		public ushort bmBitsPixel;
		public nint bmBits;
	}

	[DllImport("gdi32.dll")]
	private static extern int GetObject(nint hgdiobj, int cbBuffer, ref BITMAP lpvObject);

	// GetImage hands back a 32bpp premultiplied-BGRA DIB, but CreateBitmapSourceFromHBitmap DROPS the
	// alpha channel (returns opaque Bgr32), which makes TrimToOpaque think every icon fills its canvas so
	// it never crops the transparent padding → tiny glyphs. Read the DIB bits directly to keep alpha. The
	// DIB is BOTTOM-UP, so flip the rows to top-down (otherwise the icons render upside-down). Falls back
	// to the old path for any non-32bpp handle.
	private static BitmapSource FromHBitmap(nint hbm)
	{
		try
		{
			BITMAP bm = default(BITMAP);
			if (GetObject(hbm, Marshal.SizeOf<BITMAP>(), ref bm) != 0 && bm.bmBitsPixel == 32 && bm.bmBits != IntPtr.Zero && bm.bmWidth > 0 && bm.bmHeight > 0)
			{
				int stride = bm.bmWidth * 4;
				int size = stride * bm.bmHeight;
				byte[] src = new byte[size];
				Marshal.Copy(bm.bmBits, src, 0, size);
				byte[] top = new byte[size];
				for (int y = 0; y < bm.bmHeight; y++)
				{
					Array.Copy(src, (bm.bmHeight - 1 - y) * stride, top, y * stride, stride);
				}
				BitmapSource bs = BitmapSource.Create(bm.bmWidth, bm.bmHeight, 96.0, 96.0, PixelFormats.Pbgra32, null, top, stride);
				((Freezable)bs).Freeze();
				return bs;
			}
		}
		catch
		{
		}
		return Imaging.CreateBitmapSourceFromHBitmap(hbm, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
	}

	public static int LastGoodCount { get; private set; }

	public static bool LastLoadDegraded { get; private set; }

	public static void NoteInventoryCount(int count)
	{
		try
		{
			int baseline = LastGoodCount;
			if (baseline == 0)
			{
				try
				{
					if (File.Exists(InvCountPath) && int.TryParse(File.ReadAllText(InvCountPath).Trim(), out var b))
					{
						baseline = b;
					}
				}
				catch
				{
				}
			}
			if (count > baseline)
			{
				baseline = count;
				try
				{
					Directory.CreateDirectory(Path.GetDirectoryName(InvCountPath));
					File.WriteAllText(InvCountPath, count.ToString());
				}
				catch
				{
				}
			}
			LastGoodCount = baseline;
			LastLoadDegraded = baseline > 20 && count < baseline * 6 / 10;
			if (LastLoadDegraded)
			{
				Logger.Log($"Inventory DEGRADED: {count} apps loaded vs baseline {baseline} (partial cold-boot shell) — will self-heal when the shell warms up.");
			}
		}
		catch (Exception ex)
		{
			Logger.Log("NoteInventoryCount: " + ex.Message);
		}
	}

	public static int RescanCount()
	{
		try
		{
			return EnumerateAppsFolder().Count;
		}
		catch
		{
			return 0;
		}
	}

	public static bool ColdBoot()
	{
		try
		{
			if (Environment.GetEnvironmentVariable("WIN81_FORCE_COLDBOOT") == "1")
			{
				return true;
			}
			return Environment.TickCount64 < 180000;
		}
		catch
		{
			return false;
		}
	}

	internal static List<(AppEntry Entry, IShellItem Item)> EnumerateAppsFolderStable()
	{
		List<(AppEntry, IShellItem)> items = new List<(AppEntry, IShellItem)>();
		try
		{
			items = EnumerateAppsFolder();
		}
		catch
		{
		}
		if (!ColdBoot())
		{
			return items;
		}
		int target = LastGoodCount;
		if (target == 0)
		{
			try
			{
				if (File.Exists(InvCountPath) && int.TryParse(File.ReadAllText(InvCountPath).Trim(), out var b))
				{
					target = b;
				}
			}
			catch
			{
			}
		}
		int last = items.Count;
		int stable = 0;
		long deadline = Environment.TickCount64 + 240000;
		while (Environment.TickCount64 < deadline && ColdBoot() && (target <= 0 || items.Count < target * 85 / 100 || stable < 2))
		{
			Thread.Sleep(4000);
			try
			{
				List<(AppEntry, IShellItem)> again = EnumerateAppsFolder();
				if (again.Count > last)
				{
					ReleaseAppsFolderItems(items);
					items = again;
					last = again.Count;
					stable = 0;
				}
				else
				{
					ReleaseAppsFolderItems(again);
					stable++;
				}
			}
			catch
			{
			}
		}
		try
		{
			List<(AppEntry, IShellItem)> final = EnumerateAppsFolder();
			if (final.Count > items.Count)
			{
				ReleaseAppsFolderItems(items);
				items = final;
			}
			else
			{
				ReleaseAppsFolderItems(final);
			}
		}
		catch
		{
		}
		Logger.Log($"Cold-boot inventory gate: settled at {items.Count} apps (target {target})");
		return items;
	}

	internal static List<(AppEntry Entry, IShellItem Item)> EnumerateAppsFolder()
	{
		SortedDictionary<string, (AppEntry, IShellItem)> byName = new SortedDictionary<string, (AppEntry, IShellItem)>(StringComparer.CurrentCultureIgnoreCase);
		IShellItem? folder = null;
		object? enumObj = null;
		try
		{
			Guid appsFolderId = new Guid("1E87508D-89C2-42F0-8A7E-645A0F50CA58");
			Guid shellItemIid = typeof(IShellItem).GUID;
			SHGetKnownFolderItem(ref appsFolderId, 0, IntPtr.Zero, ref shellItemIid, out folder);
			Guid enumBhid = new Guid("94f60519-2850-4924-aa5a-d15e84868039");
			Guid enumIid = typeof(IEnumShellItems).GUID;
			folder.BindToHandler(IntPtr.Zero, ref enumBhid, ref enumIid, out enumObj);
			IEnumShellItems enumerator = (IEnumShellItems)enumObj;
			while (enumerator.Next(1u, out IShellItem item, out uint fetched) == 0 && fetched == 1)
			{
				bool retained = false;
				try
				{
					string name = GetDisplayName(item, 0u);
					string parse = GetDisplayName(item, 2147581953u);
					if (!string.IsNullOrWhiteSpace(name) && !byName.ContainsKey(name))
					{
						AppEntry entry = new AppEntry
						{
							Name = name,
							LaunchPath = "shell:AppsFolder\\" + parse,
							TileBrush = BrushFor(name),
							AppId = GetAppId(item)
						};
						byName[name] = (entry, item);
						retained = true;
					}
				}
				finally
				{
					if (!retained)
					{
						ReleaseCom(item);
					}
				}
			}
			return byName.Values.ToList();
		}
		catch
		{
			ReleaseAppsFolderItems(byName.Values);
			throw;
		}
		finally
		{
			ReleaseCom(enumObj);
			ReleaseCom(folder);
		}
	}

	internal static void ReleaseAppsFolderItems(IEnumerable<(AppEntry Entry, IShellItem Item)>? items)
	{
		if (items == null)
		{
			return;
		}
		foreach ((AppEntry _, IShellItem item) in items)
		{
			ReleaseCom(item);
		}
	}

	private static void ReleaseCom(object? value)
	{
		if (value == null || !Marshal.IsComObject(value))
		{
			return;
		}
		try
		{
			Marshal.FinalReleaseComObject(value);
		}
		catch
		{
		}
	}

	private static string GetDisplayName(IShellItem item, uint sigdn)
	{
		item.GetDisplayName(sigdn, out var ptr);
		try
		{
			return Marshal.PtrToStringUni(ptr) ?? string.Empty;
		}
		finally
		{
			Marshal.FreeCoTaskMem(ptr);
		}
	}

	private static string? GetAppId(IShellItem item)
	{
		object? psObj = null;
		try
		{
			item.BindToHandler(IntPtr.Zero, ref _bhidPropertyStore, ref _iidPropertyStore, out psObj);
			if (!(psObj is IPropertyStore ps))
			{
				return null;
			}
			PROPERTYKEY key = _pkeyAppId;
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
		finally
		{
			ReleaseCom(psObj);
		}
	}

	[DllImport("shell32.dll")]
	private static extern int SHGetPropertyStoreForWindow(nint hwnd, ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out object ppv);

	internal static string? GetWindowAppId(nint hwnd)
	{
		try
		{
			if (hwnd == IntPtr.Zero)
			{
				return null;
			}
			if (SHGetPropertyStoreForWindow(hwnd, ref _iidPropertyStore, out object psObj) != 0)
			{
				return null;
			}
			if (!(psObj is IPropertyStore ps))
			{
				return null;
			}
			try
			{
				PROPERTYKEY key = _pkeyAppId;
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
			finally
			{
				Marshal.ReleaseComObject(ps);
			}
		}
		catch
		{
			return null;
		}
	}

	[DllImport("ole32.dll")]
	private static extern int PropVariantClear(ref PROPVARIANT pv);

	internal static ImageSource? IconFromItem(IShellItem item, int preferredPixels = PreferredIconPixels)
	{
		//IL_0040: Unknown result type (might be due to invalid IL or missing references)
		nint hBitmap = IntPtr.Zero;
		try
		{
			IShellItemImageFactory factory = (IShellItemImageFactory)item;
			// BIGGERSIZEOK returns the largest REAL icon without force-upscaling a small (16/32/48px) source into a
			// blurry 256 (which SCALEUP did). Fall back to SCALEUP only if BIGGERSIZEOK yields nothing.
			try
			{
				factory.GetImage(new SIZE
				{
					cx = preferredPixels,
					cy = preferredPixels
				}, SIIGBF_ICONONLY | SIIGBF_BIGGERSIZEOK, out hBitmap);
			}
			catch
			{
				hBitmap = IntPtr.Zero;
			}
			if (hBitmap == IntPtr.Zero)
			{
				factory.GetImage(new SIZE
				{
					cx = preferredPixels,
					cy = preferredPixels
				}, SIIGBF_ICONONLY | SIIGBF_SCALEUP, out hBitmap);
			}
			BitmapSource source = FromHBitmap(hBitmap);
			return TrimToOpaque(source);
		}
		catch (Exception ex)
		{
			Logger.Log("[icon] IconFromItem failed: " + ex.Message);
			return null;
		}
		finally
		{
			if (hBitmap != IntPtr.Zero)
			{
				DeleteObject(hBitmap);
			}
		}
	}

	private static ImageSource TrimToOpaque(BitmapSource src)
	{
		//IL_01e6: Unknown result type (might be due to invalid IL or missing references)
		try
		{
			BitmapSource bmp = ((src.Format == PixelFormats.Bgra32) ? src : new FormatConvertedBitmap(src, PixelFormats.Bgra32, null, 0.0));
			int w = bmp.PixelWidth;
			int h = bmp.PixelHeight;
			int stride = w * 4;
			byte[] px = new byte[h * stride];
			bmp.CopyPixels(px, stride, 0);
			byte peak = 0;
			for (int i = 3; i < px.Length; i += 4)
			{
				if (px[i] > peak)
				{
					peak = px[i];
				}
			}
			byte alphaThreshold = (byte)Math.Max(24.0, (double)(int)peak * 0.2);
			int minX = w;
			int minY = h;
			int maxX = -1;
			int maxY = -1;
			long opaqueCount = 0L;
			for (int y = 0; y < h; y++)
			{
				for (int x = 0; x < w; x++)
				{
					if (px[y * stride + x * 4 + 3] > alphaThreshold)
					{
						opaqueCount++;
						if (x < minX)
						{
							minX = x;
						}
						if (x > maxX)
						{
							maxX = x;
						}
						if (y < minY)
						{
							minY = y;
						}
						if (y > maxY)
						{
							maxY = y;
						}
					}
				}
			}
			if (maxX < minX || maxY < minY)
			{
				((Freezable)src).Freeze();
				return src;
			}
			int cw = maxX - minX + 1;
			int ch = maxY - minY + 1;
			if ((cw <= 6 && ch <= 6) || opaqueCount < 16)
			{
				((Freezable)src).Freeze();
				return src;
			}
			if ((double)cw >= (double)w * 0.88 && (double)ch >= (double)h * 0.88)
			{
				// Opaque-background icon (e.g. LatencyMon: a solid square with a tiny centred glyph). The
				// alpha crop can't see the uniform opaque border, so the glyph reads microscopic. Detect the
				// uniform corner colour and crop to the non-background (glyph) content instead.
				if (TryTrimUniformBackground(px, w, h, stride, alphaThreshold, out var inner))
				{
					return MaterializeCrop(new CroppedBitmap(bmp, inner), bmp);
				}
				((Freezable)src).Freeze();
				return src;
			}
			return MaterializeCrop(new CroppedBitmap(bmp, new Int32Rect(minX, minY, cw, ch)), bmp);
		}
		catch
		{
			((Freezable)src).Freeze();
			return src;
		}
	}

	// Copy a crop's pixels into a standalone BitmapSource so the (up to 256x256) source bitmap it was cropped from
	// can be collected — a CroppedBitmap keeps its whole Source (+ any FormatConvertedBitmap) alive for the life of
	// the tile. Format/DPI/palette preserved so the render is bit-identical (crispness unchanged). Runs on the
	// background icon-load thread; non-fatal — falls back to the crop itself on any failure.
	private static BitmapSource MaterializeCrop(CroppedBitmap crop, BitmapSource dpiSource)
	{
		try
		{
			int cw = crop.PixelWidth;
			int ch = crop.PixelHeight;
			int stride = cw * ((crop.Format.BitsPerPixel + 7) / 8);
			byte[] buffer = new byte[ch * stride];
			crop.CopyPixels(buffer, stride, 0);
			BitmapSource materialized = BitmapSource.Create(cw, ch, dpiSource.DpiX, dpiSource.DpiY, crop.Format, crop.Palette, buffer, stride);
			((Freezable)materialized).Freeze();
			return materialized;
		}
		catch
		{
			((Freezable)crop).Freeze();
			return crop;
		}
	}

	// For an icon whose art bakes a uniform OPAQUE background (a solid square with a small centred glyph),
	// the alpha crop is useless. Sample the four corners; if they're opaque + the same colour, crop to the
	// pixels that differ from that background (the glyph) with a little padding. Fires only on this shape.
	private static bool TryTrimUniformBackground(byte[] px, int w, int h, int stride, byte alphaThreshold, out Int32Rect rect)
	{
		rect = default(Int32Rect);
		(int cx, int cy)[] corners = new (int, int)[4] { (2, 2), (w - 3, 2), (2, h - 3), (w - 3, h - 3) };
		int bs = 0, gs = 0, rs = 0, n = 0;
		int b0 = -1, g0 = -1, r0 = -1;
		foreach (var (cx, cy) in corners)
		{
			int o = cy * stride + cx * 4;
			if (px[o + 3] <= alphaThreshold)
			{
				return false;   // a transparent corner → not an opaque-background icon
			}
			int b = px[o], g = px[o + 1], r = px[o + 2];
			if (b0 < 0) { b0 = b; g0 = g; r0 = r; }
			else if (Math.Abs(b - b0) + Math.Abs(g - g0) + Math.Abs(r - r0) > 40)
			{
				return false;   // non-uniform background → leave it alone
			}
			bs += b; gs += g; rs += r; n++;
		}
		bs /= n; gs /= n; rs /= n;
		const int colourThreshold = 60;
		int minX = w, minY = h, maxX = -1, maxY = -1;
		long content = 0L;
		for (int y = 0; y < h; y++)
		{
			for (int x = 0; x < w; x++)
			{
				int o = y * stride + x * 4;
				if (px[o + 3] <= alphaThreshold)
				{
					continue;
				}
				if (Math.Abs(px[o] - bs) + Math.Abs(px[o + 1] - gs) + Math.Abs(px[o + 2] - rs) > colourThreshold)
				{
					content++;
					if (x < minX) minX = x;
					if (x > maxX) maxX = x;
					if (y < minY) minY = y;
					if (y > maxY) maxY = y;
				}
			}
		}
		if (maxX < minX || maxY < minY)
		{
			return false;
		}
		int cw = maxX - minX + 1, ch = maxY - minY + 1;
		if (content < 32L)
		{
			return false;   // too little signal → unsafe
		}
		if ((double)(cw * ch) > (double)(w * h) * 0.55)
		{
			return false;   // glyph already fills → nothing to gain
		}
		int pad = (int)(Math.Max(cw, ch) * 0.18);
		minX = Math.Max(0, minX - pad); minY = Math.Max(0, minY - pad);
		maxX = Math.Min(w - 1, maxX + pad); maxY = Math.Min(h - 1, maxY + pad);
		rect = new Int32Rect(minX, minY, maxX - minX + 1, maxY - minY + 1);
		return true;
	}

	// Saturation-aware dominant colour of an icon, used as the tile background so tile + icon read as one
	// (authentic Metro: brand-coloured tile + white glyph). Returns null for a grayscale/monochrome icon
	// (no confident brand colour) so the caller keeps its palette colour. Brightness-capped for white text.
	public static Color? DominantColor(BitmapSource src)
	{
		try
		{
			BitmapSource bmp = ((src.Format == PixelFormats.Bgra32) ? src : new FormatConvertedBitmap(src, PixelFormats.Bgra32, null, 0.0));
			int w = bmp.PixelWidth;
			int h = bmp.PixelHeight;
			if (w < 2 || h < 2)
			{
				return null;
			}
			int stride = w * 4;
			byte[] px = new byte[h * stride];
			bmp.CopyPixels(px, stride, 0);
			Dictionary<int, long[]> buckets = new Dictionary<int, long[]>();
			int step = Math.Max(1, Math.Min(w, h) / 72);
			for (int y = 0; y < h; y += step)
			{
				for (int x = 0; x < w; x += step)
				{
					int o = y * stride + x * 4;
					if (px[o + 3] < 128)
					{
						continue;
					}
					int b = px[o];
					int g = px[o + 1];
					int r = px[o + 2];
					int max = Math.Max(r, Math.Max(g, b));
					int min = Math.Min(r, Math.Min(g, b));
					if (max == 0)
					{
						continue;
					}
					double sat = (double)(max - min) / (double)max;
					double val = (double)max / 255.0;
					if (sat < 0.22 || val < 0.14 || val > 0.98)
					{
						continue;   // skip near-gray / near-white / near-black
					}
					int key = ((r >> 4) << 8) | ((g >> 4) << 4) | (b >> 4);
					if (!buckets.TryGetValue(key, out long[] acc))
					{
						acc = new long[4];
						buckets[key] = acc;
					}
					acc[0] += r;
					acc[1] += g;
					acc[2] += b;
					acc[3]++;
				}
			}
			if (buckets.Count == 0)
			{
				return null;   // monochrome icon → no confident brand colour
			}
			long bestN = 0L;
			long[] best = null;
			foreach (KeyValuePair<int, long[]> kv in buckets)
			{
				if (kv.Value[3] > bestN)
				{
					bestN = kv.Value[3];
					best = kv.Value;
				}
			}
			if (best == null)
			{
				return null;
			}
			double rr = (double)best[0] / (double)best[3];
			double gg = (double)best[1] / (double)best[3];
			double bb = (double)best[2] / (double)best[3];
			double lum = 0.299 * rr + 0.587 * gg + 0.114 * bb;
			if (lum > 176.0)
			{
				double k = 176.0 / lum;
				rr *= k;
				gg *= k;
				bb *= k;
			}
			return Color.FromRgb((byte)Math.Round(rr), (byte)Math.Round(gg), (byte)Math.Round(bb));
		}
		catch
		{
			return null;
		}
	}

	public static List<AppEntry> LoadFromStartMenuLinks()
	{
		string[] roots = new string[2]
		{
			Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu),
			Environment.GetFolderPath(Environment.SpecialFolder.StartMenu)
		};
		SortedDictionary<string, AppEntry> byName = new SortedDictionary<string, AppEntry>(StringComparer.CurrentCultureIgnoreCase);
		string[] array = roots;
		foreach (string root in array)
		{
			if (!Directory.Exists(root))
			{
				continue;
			}
			foreach (string lnk in Directory.EnumerateFiles(root, "*.lnk", SearchOption.AllDirectories))
			{
				string name = Path.GetFileNameWithoutExtension(lnk);
				if (!byName.ContainsKey(name))
				{
					byName[name] = new AppEntry
					{
						Name = name,
						LaunchPath = lnk,
						TileBrush = BrushFor(name)
					};
				}
			}
		}
		return byName.Values.ToList();
	}

	private static Brush BrushFor(string name)
	{
		int hash = 17;
		foreach (char c in name)
		{
			hash = hash * 31 + char.ToUpperInvariant(c);
		}
		SolidColorBrush brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(Palette[Math.Abs(hash) % Palette.Length]));
		((Freezable)brush).Freeze();
		return brush;
	}

	// Process-wide result cache for LoadIcon. Icon extraction (SHCreateItemFromParsingName/GetImage + TrimToOpaque) is
	// expensive and was re-run for the ENTIRE inventory on every Start idle->reopen (ReleaseIdleResources nulls
	// AppEntry.Icon), causing visible pop-in. Frozen BitmapSources are thread-safe to share; the WeakReference still
	// lets GC reclaim them under memory pressure, so idle hibernation is preserved. Keys are bounded by the app count.
	private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, WeakReference<ImageSource>> _iconCache
		= new System.Collections.Concurrent.ConcurrentDictionary<string, WeakReference<ImageSource>>();

	public static ImageSource? LoadIcon(string path)
	{
		return LoadIcon(path, PreferredIconPixels);
	}

	public static ImageSource? LoadIcon(string path, int preferredPixels)
	{
		if (string.IsNullOrWhiteSpace(path))
		{
			return null;
		}
		preferredPixels = Math.Clamp(preferredPixels, 16, PreferredIconPixels);
		string cacheKey = path + "|" + preferredPixels;
		if (_iconCache.TryGetValue(cacheKey, out WeakReference<ImageSource>? wref) && wref.TryGetTarget(out ImageSource? cached) && cached != null)
		{
			return cached;
		}
		try
		{
			string iconLocation = Environment.ExpandEnvironmentVariables(path.Trim());
			int iconIndex = 0;
			string sourcePath = SplitIconLocation(iconLocation, out iconIndex);
			string? filePath = ResolveFileBackedPath(sourcePath);
			ImageSource? result = null;
			if (!string.IsNullOrEmpty(filePath))
			{
				result = LoadBestFileIcon(filePath, iconIndex, preferredPixels);
			}
			if (result == null)
			{
				result = LoadShellIcon(iconLocation, preferredPixels);
			}
			if (result != null && IconResolver.IsBlank(result))
			{
				result = null;   // a decoded-but-blank/white icon is a miss; let the caller's fallback chain continue
			}
			if (result != null)
			{
				// Only cache a frozen (immutable, thread-safe) source; freeze if we can, otherwise skip caching.
				if (result is Freezable freezable && freezable.CanFreeze && !freezable.IsFrozen)
				{
					freezable.Freeze();
				}
				if (!(result is Freezable notFrozen) || notFrozen.IsFrozen)
				{
					_iconCache[cacheKey] = new WeakReference<ImageSource>(result);
				}
			}
			return result;
		}
		catch (Exception ex)
		{
			Logger.Log("[icon] LoadIcon('" + path + "') failed: " + ex.Message);
			return null;
		}
	}

	internal static string? ResolveFileBackedPath(string path)
	{
		if (string.IsNullOrWhiteSpace(path))
		{
			return null;
		}
		string candidate = Environment.ExpandEnvironmentVariables(path.Trim().Trim('"'));
		if (File.Exists(candidate))
		{
			return Path.GetFullPath(candidate);
		}
		const string appsPrefix = "shell:AppsFolder\\";
		if (!candidate.StartsWith(appsPrefix, StringComparison.OrdinalIgnoreCase))
		{
			return null;
		}
		string parsing = candidate.Substring(appsPrefix.Length);
		int slash = parsing.IndexOf('\\');
		if (slash <= 0 || slash >= parsing.Length - 1 || !Guid.TryParse(parsing.Substring(0, slash), out Guid folderId))
		{
			return null;
		}
		nint knownPath = IntPtr.Zero;
		try
		{
			if (SHGetKnownFolderPath(ref folderId, 0u, IntPtr.Zero, out knownPath) != 0 || knownPath == IntPtr.Zero)
			{
				return null;
			}
			string root = Marshal.PtrToStringUni(knownPath) ?? string.Empty;
			string resolved = Path.Combine(root, parsing.Substring(slash + 1));
			return File.Exists(resolved) ? Path.GetFullPath(resolved) : null;
		}
		catch
		{
			return null;
		}
		finally
		{
			if (knownPath != IntPtr.Zero)
			{
				Marshal.FreeCoTaskMem(knownPath);
			}
		}
	}

	private static string SplitIconLocation(string value, out int iconIndex)
	{
		iconIndex = 0;
		int comma = value.LastIndexOf(',');
		if (comma <= 1 || !int.TryParse(value.Substring(comma + 1).Trim(), out int parsed))
		{
			return value.Trim().Trim('"');
		}
		string candidate = value.Substring(0, comma).Trim().Trim('"');
		candidate = Environment.ExpandEnvironmentVariables(candidate);
		if (!File.Exists(candidate))
		{
			return value.Trim().Trim('"');
		}
		iconIndex = parsed;
		return candidate;
	}

	private static ImageSource? LoadBestFileIcon(string filePath, int iconIndex, int preferredPixels)
	{
		string extension = Path.GetExtension(filePath);
		if (extension.Equals(".ico", StringComparison.OrdinalIgnoreCase))
		{
			return LoadIconFile(filePath, preferredPixels);
		}
		if (iconIndex == 0)
		{
			string sidecar = Path.ChangeExtension(filePath, ".ico");
			if (!sidecar.Equals(filePath, StringComparison.OrdinalIgnoreCase) && File.Exists(sidecar))
			{
				ImageSource? sidecarIcon = LoadIconFile(sidecar, preferredPixels);
				if (sidecarIcon != null)
				{
					return sidecarIcon;
				}
			}
		}
		if (extension.Equals(".exe", StringComparison.OrdinalIgnoreCase) ||
			extension.Equals(".dll", StringComparison.OrdinalIgnoreCase) ||
			extension.Equals(".cpl", StringComparison.OrdinalIgnoreCase) ||
			extension.Equals(".scr", StringComparison.OrdinalIgnoreCase))
		{
			ImageSource? extracted = ExtractLargeIcon(filePath, iconIndex, preferredPixels);
			if (extracted != null)
			{
				return extracted;
			}
		}
		return LoadShellIcon(filePath, preferredPixels);
	}

	private static ImageSource? LoadIconFile(string path, int preferredPixels)
	{
		try
		{
			using FileStream stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
			IconBitmapDecoder decoder = new IconBitmapDecoder(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
			BitmapFrame? best = decoder.Frames
				.Where((BitmapFrame frame) => Math.Max(frame.PixelWidth, frame.PixelHeight) >= preferredPixels)
				.OrderBy((BitmapFrame frame) => frame.PixelWidth * frame.PixelHeight)
				.ThenByDescending((BitmapFrame frame) => frame.Format.BitsPerPixel)
				.FirstOrDefault()
				?? decoder.Frames
					.OrderByDescending((BitmapFrame frame) => frame.PixelWidth * frame.PixelHeight)
					.ThenByDescending((BitmapFrame frame) => frame.Format.BitsPerPixel)
					.FirstOrDefault();
			return best == null ? null : TrimToOpaque(best);
		}
		catch
		{
			return null;
		}
	}

	private static ImageSource? ExtractLargeIcon(string path, int iconIndex, int preferredPixels)
	{
		nint[] icons = new nint[1];
		try
		{
			if (PrivateExtractIcons(path, iconIndex, preferredPixels, preferredPixels, icons, IntPtr.Zero, 1u, 0u) == 0 || icons[0] == IntPtr.Zero)
			{
				return null;
			}
			BitmapSource source = Imaging.CreateBitmapSourceFromHIcon(icons[0], Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
			return TrimToOpaque(source);
		}
		catch
		{
			return null;
		}
		finally
		{
			if (icons[0] != IntPtr.Zero)
			{
				DestroyIcon(icons[0]);
			}
		}
	}

	private static ImageSource? LoadShellIcon(string path, int preferredPixels)
	{
		nint hBitmap = IntPtr.Zero;
		IShellItemImageFactory? factory = null;
		try
		{
			Guid iid = typeof(IShellItemImageFactory).GUID;
			SHCreateItemFromParsingName(path, IntPtr.Zero, ref iid, out factory);
			try
			{
				factory.GetImage(new SIZE { cx = preferredPixels, cy = preferredPixels }, SIIGBF_ICONONLY | SIIGBF_BIGGERSIZEOK, out hBitmap);
			}
			catch
			{
				hBitmap = IntPtr.Zero;
			}
			if (hBitmap == IntPtr.Zero)
			{
				factory.GetImage(new SIZE { cx = preferredPixels, cy = preferredPixels }, SIIGBF_ICONONLY | SIIGBF_SCALEUP, out hBitmap);
			}
			return hBitmap == IntPtr.Zero ? null : TrimToOpaque(FromHBitmap(hBitmap));
		}
		catch
		{
			return null;
		}
		finally
		{
			if (hBitmap != IntPtr.Zero)
			{
				DeleteObject(hBitmap);
			}
			ReleaseCom(factory);
		}
	}

	public static ImageSource? EnsureVisibleOnDark(ImageSource? source)
	{
		if (!(source is BitmapSource bitmap))
		{
			return source;
		}
		try
		{
			BitmapSource bmp = bitmap.Format == PixelFormats.Bgra32 ? bitmap : new FormatConvertedBitmap(bitmap, PixelFormats.Bgra32, null, 0.0);
			int w = bmp.PixelWidth;
			int h = bmp.PixelHeight;
			if (w <= 0 || h <= 0)
			{
				return source;
			}
			int stride = w * 4;
			byte[] pixels = new byte[h * stride];
			bmp.CopyPixels(pixels, stride, 0);
			long visible = 0L;
			long transparent = 0L;
			long darkNeutral = 0L;
			long bright = 0L;
			long luminanceTotal = 0L;
			for (int o = 0; o < pixels.Length; o += 4)
			{
				int alpha = pixels[o + 3];
				if (alpha < 24)
				{
					transparent++;
					continue;
				}
				int b = pixels[o];
				int g = pixels[o + 1];
				int r = pixels[o + 2];
				int max = Math.Max(r, Math.Max(g, b));
				int min = Math.Min(r, Math.Min(g, b));
				int luminance = (299 * r + 587 * g + 114 * b) / 1000;
				visible++;
				luminanceTotal += luminance;
				if (luminance <= 76 && max - min <= 36)
				{
					darkNeutral++;
				}
				if (luminance >= 170)
				{
					bright++;
				}
			}
			long total = visible + transparent;
			// Leave the icon UNTOUCHED (keep its original colours and shapes) when it already carries its own light
			// content — a dark badge with a white symbol (e.g. Gather's black circle) is self-visible on the dark
			// taskbar and must NOT be recoloured to a washed-out grey. Only a genuinely all-dark monochrome silhouette
			// (essentially no bright pixels, e.g. a solid black glyph like GitHub) is lifted for contrast. See memory
			// icon-whitecircle-and-start-hotspot.
			if (visible < 8 || total == 0 || transparent * 5 < total || darkNeutral * 100 < visible * 72 || luminanceTotal / visible > 82 || bright * 100 >= visible * 5)
			{
				return source;
			}
			byte[] contrasted = (byte[])pixels.Clone();
			for (int o = 0; o < contrasted.Length; o += 4)
			{
				if (contrasted[o + 3] < 8)
				{
					continue;
				}
				int bb = contrasted[o];
				int gg = contrasted[o + 1];
				int rr = contrasted[o + 2];
				int max = Math.Max(rr, Math.Max(gg, bb));
				int min = Math.Min(rr, Math.Min(gg, bb));
				if (max - min <= 48)
				{
					// Lift dark near-grey pixels to a LIGHT shade while PRESERVING relative luminance, so a filled
					// logo keeps its internal detail instead of collapsing to a featureless white blob. A uniform
					// near-black mono glyph (e.g. GitHub) still maps to ~white so those keep working; a dark circular
					// logo with an inner mark now shows that mark as a faint darker area on a light disc rather than a
					// blank white circle. See memory icon-whitecircle-and-start-hotspot.
					int lum = (299 * rr + 587 * gg + 114 * bb) / 1000;
					if (lum <= 150)
					{
						int outv = 255 - lum;
						if (outv < 160)
						{
							outv = 160;
						}
						contrasted[o] = (byte)outv;
						contrasted[o + 1] = (byte)outv;
						contrasted[o + 2] = (byte)outv;
					}
				}
			}
			double dpiX = bitmap.DpiX > 0.0 ? bitmap.DpiX : 96.0;
			double dpiY = bitmap.DpiY > 0.0 ? bitmap.DpiY : 96.0;
			BitmapSource result = BitmapSource.Create(w, h, dpiX, dpiY, PixelFormats.Bgra32, null, contrasted, stride);
			((Freezable)result).Freeze();
			return result;
		}
		catch
		{
			return source;
		}
	}

	[DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
	private static extern void SHCreateItemFromParsingName(string pszPath, nint pbc, ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out IShellItemImageFactory ppv);

	[DllImport("shell32.dll", PreserveSig = false)]
	private static extern void SHGetKnownFolderItem(ref Guid rfid, int flags, nint hToken, ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out IShellItem ppv);

	[DllImport("shell32.dll")]
	private static extern int SHGetKnownFolderPath(ref Guid rfid, uint flags, nint token, out nint path);

	[DllImport("user32.dll", CharSet = CharSet.Unicode)]
	private static extern uint PrivateExtractIcons(string file, int iconIndex, int cxIcon, int cyIcon, nint[] icons, nint iconIds, uint count, uint flags);

	[DllImport("user32.dll")]
	private static extern bool DestroyIcon(nint icon);

	[DllImport("gdi32.dll")]
	private static extern bool DeleteObject(nint hObject);
}
