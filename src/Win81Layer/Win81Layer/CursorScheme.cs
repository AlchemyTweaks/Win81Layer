using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Win32;

namespace Win81Layer;

// The Windows 8.1 pointer files and their "Windows 8.1 cursors" entry in Mouse Properties. Applying and reverting a
// pointer scheme is done by PointerScheme, which keeps the one restore point for every scheme the launcher offers.
public static class CursorScheme
{
	public const string SchemeName = "Windows 8.1 cursors";

	private const string SchemesKey = "Control Panel\\Cursors\\Schemes";

	private static readonly string[] SourceDirs;

	// Where the launcher copies its Windows 8.1 pointer files when Windows has no "Windows 8.1" pointer folder.
	internal static string AssetsDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Win81Layer", "assets", "win81", "cursors");

	private static readonly (string Role, string? File)[] Roles;

	private static string? Resolve(string? file)
	{
		if (file == null)
		{
			return null;
		}
		string[] sourceDirs = SourceDirs;
		foreach (string dir in sourceDirs)
		{
			string p = Path.Combine(dir, file);
			if (File.Exists(p))
			{
				return p;
			}
		}
		return null;
	}

	// Role to file for the Windows 8.1 pointers; "" where Windows' own pointer is used (precision and text select, or a
	// file that is not available).
	internal static IReadOnlyDictionary<CursorRole, string> ResolveWin81()
	{
		Dictionary<CursorRole, string> d = new Dictionary<CursorRole, string>();
		foreach ((string role, string file) in Roles)
		{
			if (Enum.TryParse(role, out CursorRole r))
			{
				d[r] = Resolve(file) ?? "";
			}
		}
		return d;
	}

	public static void EnsureSchemeInstalled()
	{
		try
		{
			string[] paths = Roles.Select(((string Role, string File) r) => Resolve(r.File) ?? "").ToArray();
			using RegistryKey schemes = Registry.CurrentUser.CreateSubKey(SchemesKey);
			schemes.SetValue(SchemeName, string.Join(",", paths), RegistryValueKind.ExpandString);
		}
		catch (Exception ex)
		{
			Logger.Log("Cursor scheme install failed: " + ex.Message);
		}
	}

	static CursorScheme()
	{
		string[] obj = new string[3]
		{
			"C:\\Windows\\Cursors\\Windows 8.1",
			Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Cursors"),
			null
		};
		global::_003C_003Ey__InlineArray5<string> buffer = default(global::_003C_003Ey__InlineArray5<string>);
		buffer[0] = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
		buffer[1] = "Win81Layer";
		buffer[2] = "assets";
		buffer[3] = "win81";
		buffer[4] = "cursors";
		obj[2] = Path.Combine(buffer);
		SourceDirs = obj;
		Roles = new(string, string)[17]
		{
			("Arrow", "aero_arrow.cur"),
			("Help", "aero_helpsel.cur"),
			("AppStarting", "aero_working.ani"),
			("Wait", "aero_busy.ani"),
			("Crosshair", null),
			("IBeam", null),
			("NWPen", "aero_pen.cur"),
			("No", "aero_unavail.cur"),
			("SizeNS", "aero_ns.cur"),
			("SizeWE", "aero_ew.cur"),
			("SizeNWSE", "aero_nwse.cur"),
			("SizeNESW", "aero_nesw.cur"),
			("SizeAll", "aero_move.cur"),
			("UpArrow", "aero_up.cur"),
			("Hand", "aero_link.cur"),
			("Pin", "aero_pin.cur"),
			("Person", "aero_person.cur")
		};
	}
}
