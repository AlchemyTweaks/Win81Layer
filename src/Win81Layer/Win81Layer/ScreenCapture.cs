using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

namespace Win81Layer;

internal static class ScreenCapture
{
	private struct RECT
	{
		public int Left;

		public int Top;

		public int Right;

		public int Bottom;
	}

	private const int DWMWA_EXTENDED_FRAME_BOUNDS = 9;

	[DllImport("user32.dll")]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static extern bool GetWindowRect(nint h, out RECT r);

	[DllImport("user32.dll")]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static extern bool IsIconic(nint h);

	[DllImport("user32.dll", CharSet = CharSet.Unicode)]
	private static extern int GetClassNameW(nint h, StringBuilder s, int n);

	[DllImport("dwmapi.dll")]
	private static extern int DwmGetWindowAttribute(nint h, int attr, out RECT r, int size);

	internal static (string? Path, Bitmap? Bitmap) CaptureToFile(nint hwnd)
	{
		try
		{
			Rectangle b = Bounds(hwnd);
			Bitmap bmp = new Bitmap(b.Width, b.Height, PixelFormat.Format32bppArgb);
			using (Graphics g = Graphics.FromImage(bmp))
			{
				g.CopyFromScreen(b.X, b.Y, 0, 0, new Size(b.Width, b.Height), CopyPixelOperation.SourceCopy);
			}
			string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "Screenshots");
			Directory.CreateDirectory(dir);
			string path = Path.Combine(dir, $"Screenshot {DateTime.Now:yyyy-MM-dd HHmmss.fff}.png");
			bmp.Save(path, ImageFormat.Png);
			return (Path: path, Bitmap: bmp);
		}
		catch (Exception ex)
		{
			Logger.Log("ScreenCapture: " + ex.Message);
			return (Path: null, Bitmap: null);
		}
	}

	private static Rectangle Bounds(nint hwnd)
	{
		if (hwnd != IntPtr.Zero && !IsIconic(hwnd) && !IsDesktopWindow(hwnd) && TryWindowBounds(hwnd, out var wr))
		{
			wr.Intersect(SystemInformation.VirtualScreen);
			if (wr.Width > 0 && wr.Height > 0)
			{
				return wr;
			}
		}
		return Screen.FromPoint(Cursor.Position)?.Bounds ?? Screen.PrimaryScreen?.Bounds ?? new Rectangle(0, 0, 1920, 1080);
	}

	private static bool TryWindowBounds(nint hwnd, out Rectangle rect)
	{
		rect = Rectangle.Empty;
		if (DwmGetWindowAttribute(hwnd, 9, out var d, Marshal.SizeOf<RECT>()) == 0 && d.Right > d.Left && d.Bottom > d.Top)
		{
			rect = new Rectangle(d.Left, d.Top, d.Right - d.Left, d.Bottom - d.Top);
			return true;
		}
		if (GetWindowRect(hwnd, out var r) && r.Right > r.Left && r.Bottom > r.Top)
		{
			rect = new Rectangle(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top);
			return true;
		}
		return false;
	}

	private static bool IsDesktopWindow(nint hwnd)
	{
		StringBuilder sb = new StringBuilder(64);
		GetClassNameW(hwnd, sb, sb.Capacity);
		switch (sb.ToString())
		{
		case "Progman":
		case "WorkerW":
		case "SHELLDLL_DefView":
			return true;
		default:
			return false;
		}
	}
}
