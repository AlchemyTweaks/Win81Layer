#nullable enable

using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace Win81Layer;

// One guarded path for the live DWM thumbnails (Alt+Tab switcher, taskbar hover preview, Snap Assist). Same look as
// before; what changes is what is sent to DWM: no registration against a source window that is already gone or hidden
// (a switcher rebuild right after a window closed used to race its teardown), an empty destination is not registered,
// the HRESULTs are checked (the old try/catch could not see them: these APIs return errors, they do not throw), and a
// thumbnail whose properties could not be set is unregistered at once instead of lingering until the next hide.
internal static class DwmThumbs
{
	private const uint DWM_TNP_RECTDESTINATION = 0x1u;

	private const uint DWM_TNP_OPACITY = 0x4u;

	private const uint DWM_TNP_VISIBLE = 0x8u;

	private const uint DWM_TNP_SOURCECLIENTAREAONLY = 0x10u;

	internal static int Registered;

	internal static int Rejected;

	internal static int Failed;

	private static long _lastFailureLog = long.MinValue;

	internal static int LastHResult;

	[StructLayout(LayoutKind.Sequential)]
	private struct RECT
	{
		public int Left;

		public int Top;

		public int Right;

		public int Bottom;
	}

	[StructLayout(LayoutKind.Sequential)]
	private struct DWM_THUMBNAIL_PROPERTIES
	{
		public uint dwFlags;

		public RECT rcDestination;

		public RECT rcSource;

		public byte opacity;

		[MarshalAs(UnmanagedType.Bool)]
		public bool fVisible;

		[MarshalAs(UnmanagedType.Bool)]
		public bool fSourceClientAreaOnly;
	}

	// Registers a thumbnail of src drawn into dest at the given physical-pixel rectangle (dest client coordinates).
	// Returns the thumbnail id, or 0 when nothing was registered.
	internal static nint TryRegister(nint dest, nint src, int left, int top, int right, int bottom)
	{
		if (dest == IntPtr.Zero || src == IntPtr.Zero || right <= left || bottom <= top)
		{
			Interlocked.Increment(ref Rejected);
			return IntPtr.Zero;
		}
		if (!IsWindow(dest) || !IsWindow(src) || !IsWindowVisible(src))
		{
			Interlocked.Increment(ref Rejected);
			return IntPtr.Zero;
		}
		int hr = DwmRegisterThumbnail(dest, src, out nint thumb);
		if (hr != 0 || thumb == IntPtr.Zero)
		{
			NoteFailure("DwmRegisterThumbnail", hr, src);
			return IntPtr.Zero;
		}
		DWM_THUMBNAIL_PROPERTIES props = new DWM_THUMBNAIL_PROPERTIES
		{
			dwFlags = DWM_TNP_RECTDESTINATION | DWM_TNP_OPACITY | DWM_TNP_VISIBLE | DWM_TNP_SOURCECLIENTAREAONLY,
			rcDestination = new RECT { Left = left, Top = top, Right = right, Bottom = bottom },
			opacity = byte.MaxValue,
			fVisible = true,
			fSourceClientAreaOnly = false
		};
		hr = DwmUpdateThumbnailProperties(thumb, ref props);
		if (hr != 0)
		{
			DwmUnregisterThumbnail(thumb);
			NoteFailure("DwmUpdateThumbnailProperties", hr, src);
			return IntPtr.Zero;
		}
		Interlocked.Increment(ref Registered);
		return thumb;
	}

	internal static void Unregister(nint thumb)
	{
		if (thumb == IntPtr.Zero)
		{
			return;
		}
		try
		{
			DwmUnregisterThumbnail(thumb);
		}
		catch
		{
		}
	}

	private static void NoteFailure(string api, int hr, nint src)
	{
		Interlocked.Increment(ref Failed);
		LastHResult = hr;
		long now = Environment.TickCount64;
		if (_lastFailureLog == long.MinValue || now - _lastFailureLog > 60000)
		{
			_lastFailureLog = now;
			Logger.Log($"DWM thumbnail: {api} failed 0x{hr:X8} for window 0x{(long)src:X} (failures so far={Failed})");
		}
	}

	[DllImport("user32.dll")]
	private static extern bool IsWindow(nint hwnd);

	[DllImport("user32.dll")]
	private static extern bool IsWindowVisible(nint hwnd);

	[DllImport("dwmapi.dll")]
	private static extern int DwmRegisterThumbnail(nint dest, nint src, out nint thumb);

	[DllImport("dwmapi.dll")]
	private static extern int DwmUnregisterThumbnail(nint thumb);

	[DllImport("dwmapi.dll")]
	private static extern int DwmUpdateThumbnailProperties(nint thumb, ref DWM_THUMBNAIL_PROPERTIES props);
}
