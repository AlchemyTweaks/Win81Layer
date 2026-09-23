using System;
using System.Runtime.InteropServices;
using System.Text;

namespace Win81Layer;

internal static class ForegroundContext
{
	private static nint _hwnd;

	public static nint Hwnd
	{
		get
		{
			nint h = _hwnd;
			return (h != IntPtr.Zero && IsWindow(h)) ? h : IntPtr.Zero;
		}
	}

	[DllImport("user32.dll")]
	private static extern nint GetForegroundWindow();

	[DllImport("user32.dll")]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static extern bool IsWindow(nint h);

	[DllImport("user32.dll")]
	private static extern int GetWindowThreadProcessId(nint h, out int pid);

	[DllImport("user32.dll", CharSet = CharSet.Unicode)]
	private static extern int GetWindowTextW(nint h, StringBuilder s, int n);

	[DllImport("user32.dll", CharSet = CharSet.Unicode)]
	private static extern int GetClassNameW(nint h, StringBuilder s, int n);

	public static void Capture()
	{
		nint h = GetForegroundWindow();
		if (h == IntPtr.Zero)
		{
			_hwnd = IntPtr.Zero;
			return;
		}
		GetWindowThreadProcessId(h, out var pid);
		if (pid != Environment.ProcessId)
		{
			_hwnd = h;
		}
	}

	private static string Text(nint h)
	{
		StringBuilder sb = new StringBuilder(512);
		GetWindowTextW(h, sb, sb.Capacity);
		return sb.ToString();
	}

	private static string Cls(nint h)
	{
		StringBuilder sb = new StringBuilder(256);
		GetClassNameW(h, sb, sb.Capacity);
		return sb.ToString();
	}

	public static string? Classify()
	{
		nint h = _hwnd;
		if (h == IntPtr.Zero)
		{
			return "Desktop";
		}
		string cls = Cls(h);
		string title = Text(h);
		bool flag;
		switch (cls)
		{
		case "Progman":
		case "WorkerW":
		case "SHELLDLL_DefView":
			flag = true;
			break;
		default:
			flag = false;
			break;
		}
		if (flag)
		{
			return "Desktop";
		}
		if (cls == "CabinetWClass")
		{
			if (Has("Personalization"))
			{
				return "Personalization";
			}
			if (Has("System"))
			{
				return "PC info";
			}
			if (Has("Control Panel"))
			{
				return "Control Panel";
			}
		}
		if (Has("Personalization"))
		{
			return "Personalization";
		}
		if (Has("Control Panel"))
		{
			return "Control Panel";
		}
		if (Has("Help"))
		{
			return "Help";
		}
		return null;
		bool Has(string s)
		{
			return title.Contains(s, StringComparison.OrdinalIgnoreCase);
		}
	}
}
