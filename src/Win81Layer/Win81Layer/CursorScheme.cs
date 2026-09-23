using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Win32;

namespace Win81Layer;

public static class CursorScheme
{
	public const string SchemeName = "Windows 8.1 cursors";

	private const string CursorsKey = "Control Panel\\Cursors";

	private const string SchemesKey = "Control Panel\\Cursors\\Schemes";

	private static readonly string[] SourceDirs;

	private static readonly (string Role, string? File)[] Roles;

	private const uint SPI_SETCURSORS = 87u;

	private const uint SPIF_UPDATEINIFILE = 1u;

	private const uint SPIF_SENDCHANGE = 2u;

	private static string SnapshotPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Win81Layer", "cursor-snapshot.json");

	public static bool IsApplied => File.Exists(SnapshotPath);

	public static void Reassert()
	{
		if (IsApplied)
		{
			Apply();
		}
	}

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

	public static void EnsureSchemeInstalled()
	{
		try
		{
			string[] paths = Roles.Select(((string Role, string File) r) => Resolve(r.File) ?? "").ToArray();
			using RegistryKey schemes = Registry.CurrentUser.CreateSubKey("Control Panel\\Cursors\\Schemes");
			schemes.SetValue("Windows 8.1 cursors", string.Join(",", paths), RegistryValueKind.ExpandString);
		}
		catch (Exception ex)
		{
			Logger.Log("Cursor scheme install failed: " + ex.Message);
		}
	}

	public static void Apply()
	{
		try
		{
			EnsureSchemeInstalled();
			using RegistryKey cur = Registry.CurrentUser.OpenSubKey("Control Panel\\Cursors", writable: true);
			if (cur == null)
			{
				return;
			}
			if (!File.Exists(SnapshotPath))
			{
				Dictionary<string, string> dictionary = new Dictionary<string, string>();
				dictionary["(Default)"] = (cur.GetValue("") as string) ?? "";
				Dictionary<string, string> snap = dictionary;
				(string, string)[] roles = Roles;
				for (int i = 0; i < roles.Length; i++)
				{
					string role = roles[i].Item1;
					snap[role] = (cur.GetValue(role) as string) ?? "";
				}
				Directory.CreateDirectory(Path.GetDirectoryName(SnapshotPath));
				File.WriteAllText(SnapshotPath, JsonSerializer.Serialize(snap));
			}
			(string, string)[] roles2 = Roles;
			for (int j = 0; j < roles2.Length; j++)
			{
				(string, string) tuple = roles2[j];
				string role2 = tuple.Item1;
				string file = tuple.Item2;
				string path = Resolve(file);
				if (path != null)
				{
					cur.SetValue(role2, path, RegistryValueKind.ExpandString);
				}
				else
				{
					cur.SetValue(role2, "", RegistryValueKind.ExpandString);
				}
			}
			cur.SetValue("", "Windows 8.1 cursors");
			RefreshCursors();
			Logger.Log("Win8.1 cursors applied");
		}
		catch (Exception ex)
		{
			Logger.Log("Cursor apply failed: " + ex.Message);
		}
	}

	public static void Revert()
	{
		try
		{
			if (!File.Exists(SnapshotPath))
			{
				return;
			}
			Dictionary<string, string> snap = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(SnapshotPath)) ?? new Dictionary<string, string>();
			using (RegistryKey cur = Registry.CurrentUser.OpenSubKey("Control Panel\\Cursors", writable: true))
			{
				if (cur != null)
				{
					(string, string)[] roles = Roles;
					for (int i = 0; i < roles.Length; i++)
					{
						string role = roles[i].Item1;
						cur.SetValue(role, snap.TryGetValue(role, out var v) ? (v ?? "") : "", RegistryValueKind.ExpandString);
					}
					cur.SetValue("", snap.TryGetValue("(Default)", out var d) ? (d ?? "") : "");
				}
			}
			File.Delete(SnapshotPath);
			RefreshCursors();
			Logger.Log("Win8.1 cursors reverted");
		}
		catch (Exception ex)
		{
			Logger.Log("Cursor revert failed: " + ex.Message);
		}
	}

	[DllImport("user32.dll", SetLastError = true)]
	private static extern bool SystemParametersInfo(uint action, uint param, nint ptr, uint winIni);

	private static void RefreshCursors()
	{
		SystemParametersInfo(87u, 0u, IntPtr.Zero, 3u);
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
