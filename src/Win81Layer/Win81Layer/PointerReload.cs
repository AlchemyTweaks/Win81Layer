#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;

namespace Win81Layer;

// One system pointer image as Windows holds it: size, hotspot and the raw bits of its colour bitmap (empty for a
// monochrome pointer) and of its mask. For an animated pointer this is its first frame.
internal sealed class CursorImage
{
	public int Width { get; init; }

	public int Height { get; init; }

	public int HotX { get; init; }

	public int HotY { get; init; }

	public byte[] Color { get; init; } = Array.Empty<byte>();

	public byte[] Mask { get; init; } = Array.Empty<byte>();

	// Same size, hotspot and pixels (a mean difference of at most 1/255 is allowed for rounding; different pointers
	// differ by far more).
	public bool SameAs(CursorImage? other)
	{
		return other != null && Width == other.Width && Height == other.Height && HotX == other.HotX && HotY == other.HotY
			&& Near(Color, other.Color) && Near(Mask, other.Mask);
	}

	private static bool Near(byte[] a, byte[] b)
	{
		if (a.Length != b.Length)
		{
			return false;
		}
		if (a.AsSpan().SequenceEqual(b))
		{
			return true;
		}
		long sum = 0;
		for (int i = 0; i < a.Length; i++)
		{
			sum += Math.Abs(a[i] - b[i]);
		}
		return sum <= a.Length;
	}
}

// What the pointer engine needs from Windows to put pointers on screen. The real one is Win32CursorSystem; the
// self-test drives the same logic with a fake.
internal interface ICursorSystem
{
	// SystemParametersInfo(SPI_SETCURSORS, 0, NULL, fWinIni): Windows reloads every system pointer from
	// HKCU\Control Panel\Cursors. error is GetLastError when it returns false (0 = Windows set none).
	bool ReloadFromRegistry(uint fWinIni, out int error);

	// The role's system pointer as it is now; null when it cannot be read.
	CursorImage? Current(CursorRole role);

	// The file loaded the way Windows loads a pointer file at this size; null when it does not load (why says how).
	CursorImage? LoadFile(string path, int size, out string why);

	// SetSystemCursor(LoadImage(path, size), role): this session only, nothing is saved.
	bool SetFromFile(CursorRole role, string path, int size, out int error);
}

internal sealed class PointerReloadResult
{
	// Every role whose file loads shows that file now (and, when Windows refused the reload with an error or returned
	// FALSE having changed nothing, no role is left that could not be checked). False means the registry is right but the
	// screen is not until the next sign-in.
	public bool Loaded { get; set; }

	public uint Flags { get; set; }

	public bool SpiReturned { get; set; }

	public int SpiError { get; set; }

	// Roles compared with their file after the reload.
	public int Checked { get; set; }

	// Roles the reload left as they were, put on directly with SetSystemCursor.
	public List<string> Fallback { get; } = new List<string>();

	// Roles that still do not show their file.
	public List<string> NotShown { get; } = new List<string>();

	// Roles whose file does not load (Windows shows its own pointer for them).
	public List<string> BadFiles { get; } = new List<string>();

	// Roles on Windows' own pointer ("" in the registry), with a relative path or unreadable: not compared.
	public List<string> Unchecked { get; } = new List<string>();

	// Some compared pointer had to change, yet no compared pointer changed across the reload: Windows evidently did not
	// reload anything.
	public bool ReloadChangedNothing { get; set; }

	public string Describe()
	{
		StringBuilder sb = new StringBuilder();
		sb.Append("SPI_SETCURSORS(flags ").Append(Flags).Append(") returned ").Append(SpiReturned ? "TRUE" : "FALSE (error " + SpiError + ")");
		if (ReloadChangedNothing)
		{
			sb.Append(" and changed nothing");
		}
		sb.Append("; ").Append(Checked).Append(" pointer(s) compared with their files");
		if (Fallback.Count > 0)
		{
			sb.Append("; set directly: ").Append(string.Join(", ", Fallback));
		}
		if (NotShown.Count > 0)
		{
			sb.Append("; NOT shown: ").Append(string.Join(", ", NotShown));
		}
		if (BadFiles.Count > 0)
		{
			sb.Append("; file does not load: ").Append(string.Join(", ", BadFiles));
		}
		if (Unchecked.Count > 0)
		{
			sb.Append("; not compared: ").Append(string.Join(", ", Unchecked));
		}
		sb.Append(Loaded ? " -> on screen." : " -> not on screen until the next sign-in.");
		return sb.ToString();
	}
}

// Puts the pointers the registry names on screen and checks that they are there. The registry stays the only source
// of truth: this never writes it, and the fallback only sets the same files for this session.
internal static class PointerReloader
{
	public const uint SpiSetCursors = 0x57;

	public const uint SpifUpdateIniFile = 0x1;

	public const uint SpifSendChange = 0x2;

	// Neither flag. With SPIF_UPDATEINIFILE, SPI_SETCURSORS reloads every pointer and then returns FALSE without an error
	// code: the action has no value of its own to save (the launcher writes the registry itself), and win32k only
	// reports success for an "update the profile" call when it wrote one. That made every change look failed.
	// SPIF_SENDCHANGE changes nothing for this action (Windows broadcasts nothing for it); the system pointer handles
	// every app draws with are updated in place.
	public const uint ReloadFlags = 0;

	// Thread: any thread of this process works (it is on the interactive desktop); the call never needs the UI thread.
	public static PointerReloadResult Reload(ICursorSystem sys, IReadOnlyDictionary<CursorRole, string> expected)
	{
		PointerReloadResult r = new PointerReloadResult { Flags = ReloadFlags };
		// 1. What each role must show, loaded at the size Windows uses for that role now. A file that does not load is
		// named (Windows shows its own pointer for that role instead).
		List<(CursorRole Role, string Path, CursorImage Before, CursorImage Want)> check = new List<(CursorRole, string, CursorImage, CursorImage)>();
		foreach (CursorRole role in AlchemyCursorArt.Roles)
		{
			string path = expected.TryGetValue(role, out string? p) ? (p ?? "").Trim() : "";
			if (path.Length == 0)
			{
				r.Unchecked.Add(role.ToString());
				continue;
			}
			if (!Path.IsPathFullyQualified(path))
			{
				r.Unchecked.Add(role + " (relative path)");
				continue;
			}
			CursorImage? now = TryCurrent(sys, role);
			if (now == null || now.Width <= 0)
			{
				r.Unchecked.Add(role + " (unreadable)");
				continue;
			}
			CursorImage? want = TryLoad(sys, path, now.Width, out string why);
			if (want == null)
			{
				r.BadFiles.Add($"{role} ({why}: {path})");
				continue;
			}
			check.Add((role, path, now, want));
		}
		// 2. Windows loads every pointer the registry names.
		bool ok = sys.ReloadFromRegistry(ReloadFlags, out int error);
		r.SpiReturned = ok;
		r.SpiError = ok ? 0 : error;
		// 3. The screen decides, not the return value: each compared role must show its file now. A role the reload left
		// as it was gets its file directly (this session only; the registry names the same file for the next sign-in).
		r.Checked = check.Count;
		bool needed = false;
		bool changed = false;
		foreach ((CursorRole role, string path, CursorImage beforeImage, CursorImage loaded) in check)
		{
			// The reload may have changed the size (a new pointer size setting): compare, and set, at the size it uses now.
			CursorImage? after = TryCurrent(sys, role);
			int before = beforeImage.Width;
			needed |= !loaded.SameAs(beforeImage);
			// Read before this role is set by hand; an unreadable pointer is no evidence that nothing changed.
			changed |= after == null || !after.SameAs(beforeImage);
			int size = after != null && after.Width > 0 ? after.Width : before;
			CursorImage want = size == before ? loaded : (TryLoad(sys, path, size, out _) ?? loaded);
			if (want.SameAs(after))
			{
				continue;
			}
			bool set;
			int setError;
			try
			{
				set = sys.SetFromFile(role, path, size, out setError);
			}
			catch (Exception ex) when (ex is not InvalidOperationException)
			{
				set = false;
				setError = -1;
				Logger.Log($"Pointers: setting {role} directly failed: {ex.Message}");
			}
			if (set && want.SameAs(TryCurrent(sys, role)))
			{
				r.Fallback.Add(role.ToString());
			}
			else
			{
				r.NotShown.Add(set ? role.ToString() : $"{role} (error {setError})");
			}
		}
		// A reload Windows refused with an error did not touch the roles that could not be compared either; nor did one
		// that returned FALSE without an error and visibly changed none of the pointers that had to change. Those roles
		// (Windows' own pointers, say the text-select one) still show the previous set, so the screen is not the
		// registry's until the next sign-in. A TRUE return is trusted for them.
		r.ReloadChangedNothing = needed && !changed;
		bool stale = !ok && (error != 0 || r.ReloadChangedNothing);
		r.Loaded = r.NotShown.Count == 0 && !(stale && (r.Unchecked.Count > 0 || r.BadFiles.Count > 0));
		return r;
	}

	private static CursorImage? TryLoad(ICursorSystem sys, string path, int size, out string why)
	{
		try
		{
			return sys.LoadFile(path, size, out why);
		}
		catch (Exception ex) when (ex is not InvalidOperationException)
		{
			why = ex.GetType().Name;
			return null;
		}
	}

	private static CursorImage? TryCurrent(ICursorSystem sys, CursorRole role)
	{
		try
		{
			return sys.Current(role);
		}
		catch (Exception ex) when (ex is not InvalidOperationException)
		{
			return null;
		}
	}
}

// The real pointers of this session. Reading is harmless; the two calls that change what is on screen refuse to run in
// read-only diagnostics.
internal sealed class Win32CursorSystem : ICursorSystem
{
	private const uint ImageCursor = 2;

	private const uint LrLoadFromFile = 0x10;

	// The OCR_/IDC_ id of each registry role (Pin and Person are IDC_PIN and IDC_PERSON).
	public static uint OcrId(CursorRole role)
	{
		return role switch
		{
			CursorRole.Arrow => 32512u,
			CursorRole.IBeam => 32513u,
			CursorRole.Wait => 32514u,
			CursorRole.Crosshair => 32515u,
			CursorRole.UpArrow => 32516u,
			CursorRole.NWPen => 32631u,
			CursorRole.SizeNWSE => 32642u,
			CursorRole.SizeNESW => 32643u,
			CursorRole.SizeWE => 32644u,
			CursorRole.SizeNS => 32645u,
			CursorRole.SizeAll => 32646u,
			CursorRole.No => 32648u,
			CursorRole.Hand => 32649u,
			CursorRole.AppStarting => 32650u,
			CursorRole.Help => 32651u,
			CursorRole.Pin => 32671u,
			CursorRole.Person => 32672u,
			_ => throw new ArgumentOutOfRangeException(nameof(role))
		};
	}

	private static void Guard()
	{
		if (SettingsStore.ReadOnlyDiagnostics)
		{
			throw new InvalidOperationException("Pointer changes are disabled in read-only diagnostics mode.");
		}
	}

	public bool ReloadFromRegistry(uint fWinIni, out int error)
	{
		Guard();
		bool ok = SystemParametersInfoW(PointerReloader.SpiSetCursors, 0u, nint.Zero, fWinIni);
		error = ok ? 0 : Marshal.GetLastPInvokeError();
		return ok;
	}

	public CursorImage? Current(CursorRole role)
	{
		// The shared system pointer: never destroyed.
		nint h = LoadCursorW(nint.Zero, (nint)OcrId(role));
		return h == nint.Zero ? null : Read(h);
	}

	public CursorImage? LoadFile(string path, int size, out string why)
	{
		if (!File.Exists(path))
		{
			why = "missing";
			return null;
		}
		nint h = LoadImageW(nint.Zero, path, ImageCursor, size, size, LrLoadFromFile);
		if (h == nint.Zero)
		{
			why = "LoadImage error " + Marshal.GetLastPInvokeError();
			return null;
		}
		try
		{
			CursorImage? img = Read(h);
			why = img == null ? "unreadable" : "";
			return img;
		}
		finally
		{
			DestroyCursor(h);
		}
	}

	public bool SetFromFile(CursorRole role, string path, int size, out int error)
	{
		Guard();
		nint h = LoadImageW(nint.Zero, path, ImageCursor, size, size, LrLoadFromFile);
		if (h == nint.Zero)
		{
			error = Marshal.GetLastPInvokeError();
			return false;
		}
		// Windows takes the handle when it succeeds (it destroys it); otherwise it is still ours.
		if (SetSystemCursor(h, OcrId(role)))
		{
			error = 0;
			return true;
		}
		error = Marshal.GetLastPInvokeError();
		DestroyCursor(h);
		return false;
	}

	private static CursorImage? Read(nint cursor)
	{
		if (!GetIconInfo(cursor, out ICONINFO ii))
		{
			return null;
		}
		try
		{
			byte[] color = Bits(ii.hbmColor, out int cw, out int ch);
			byte[] mask = Bits(ii.hbmMask, out int mw, out int mh);
			bool mono = ii.hbmColor == nint.Zero;
			int w = mono ? mw : cw;
			int h = mono ? mh / 2 : ch;
			if (w <= 0 || h <= 0)
			{
				return null;
			}
			return new CursorImage { Width = w, Height = h, HotX = ii.xHotspot, HotY = ii.yHotspot, Color = color, Mask = mask };
		}
		finally
		{
			if (ii.hbmColor != nint.Zero)
			{
				DeleteObject(ii.hbmColor);
			}
			if (ii.hbmMask != nint.Zero)
			{
				DeleteObject(ii.hbmMask);
			}
		}
	}

	private static byte[] Bits(nint hbm, out int width, out int height)
	{
		width = 0;
		height = 0;
		if (hbm == nint.Zero || GetObjectW(hbm, Marshal.SizeOf<BITMAP>(), out BITMAP bm) == 0)
		{
			return Array.Empty<byte>();
		}
		width = bm.bmWidth;
		height = bm.bmHeight;
		long n = (long)bm.bmWidthBytes * bm.bmHeight;
		if (n <= 0 || n > 64L * 1024 * 1024)
		{
			return Array.Empty<byte>();
		}
		byte[] bits = new byte[n];
		int got = GetBitmapBits(hbm, (int)n, bits);
		return got == n ? bits : bits.AsSpan(0, Math.Max(0, got)).ToArray();
	}

	[StructLayout(LayoutKind.Sequential)]
	private struct ICONINFO
	{
		public int fIcon;

		public int xHotspot;

		public int yHotspot;

		public nint hbmMask;

		public nint hbmColor;
	}

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

	[DllImport("user32.dll", EntryPoint = "SystemParametersInfoW", CharSet = CharSet.Unicode, SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static extern bool SystemParametersInfoW(uint action, uint param, nint ptr, uint winIni);

	[DllImport("user32.dll", EntryPoint = "LoadCursorW", SetLastError = true)]
	private static extern nint LoadCursorW(nint hInstance, nint name);

	[DllImport("user32.dll", EntryPoint = "LoadImageW", CharSet = CharSet.Unicode, SetLastError = true)]
	private static extern nint LoadImageW(nint hInst, string name, uint type, int cx, int cy, uint fuLoad);

	[DllImport("user32.dll", SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static extern bool DestroyCursor(nint hCursor);

	[DllImport("user32.dll", SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static extern bool SetSystemCursor(nint hcur, uint id);

	[DllImport("user32.dll", SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static extern bool GetIconInfo(nint hIcon, out ICONINFO info);

	[DllImport("gdi32.dll", EntryPoint = "GetObjectW")]
	private static extern int GetObjectW(nint h, int c, out BITMAP bm);

	[DllImport("gdi32.dll")]
	private static extern int GetBitmapBits(nint hbm, int cb, byte[] bits);

	[DllImport("gdi32.dll")]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static extern bool DeleteObject(nint h);
}
