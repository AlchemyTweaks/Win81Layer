using System;
using System.IO;
using Microsoft.Win32;

namespace Win81Layer;

// Legacy migration only. Notifications are entirely Windows' own: the launcher no longer touches them. An older Action
// Center quiet-hours tile switched EVERY native toast off (HKCU PushNotifications\ToastEnabled=0) and kept what was there
// before in %LOCALAPPDATA%\Win81Layer\toast-enabled.snap; it was never restored on exit, so the switch-off could outlive
// the launcher. RestoreLegacySuppression puts that value back once, and only when the snapshot proves the 0 was the
// launcher's (a value the user or Windows set since is left alone), then deletes the snapshot.
internal static class NativeBanner
{
	private const string Key = "Software\\Microsoft\\Windows\\CurrentVersion\\PushNotifications";

	internal enum LegacyWrite
	{
		None,
		Delete,
		Set
	}

	internal readonly record struct LegacyDecision(LegacyWrite Write, int Value, bool DeleteSnap);

	private static string SnapPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Win81Layer", "toast-enabled.snap");

	// Pure decision: snap is the snapshot's text (null = no snapshot), currentToastEnabled the value now (null = unset).
	//   no snapshot                        -> nothing
	//   ToastEnabled == 0, snapshot "unset" -> delete the value (Windows' default: on)
	//   ToastEnabled == 0, snapshot int v   -> set v
	//   ToastEnabled == 0, snapshot garbage -> set 1
	//   ToastEnabled != 0 or unset          -> nothing (changed since by the user or Windows)
	// and the snapshot is deleted whenever it exists.
	internal static LegacyDecision DecideLegacyRestore(string snap, int? currentToastEnabled)
	{
		if (snap == null)
		{
			return new LegacyDecision(LegacyWrite.None, 0, DeleteSnap: false);
		}
		if (currentToastEnabled != 0)
		{
			return new LegacyDecision(LegacyWrite.None, 0, DeleteSnap: true);
		}
		string s = snap.Trim();
		if (string.Equals(s, "unset", StringComparison.OrdinalIgnoreCase))
		{
			return new LegacyDecision(LegacyWrite.Delete, 0, DeleteSnap: true);
		}
		if (int.TryParse(s, out int v))
		{
			return new LegacyDecision(LegacyWrite.Set, v, DeleteSnap: true);
		}
		return new LegacyDecision(LegacyWrite.Set, 1, DeleteSnap: true);
	}

	// Once per real-shell boot (background; never in a read-only diagnostic or the perf probe).
	internal static void RestoreLegacySuppression()
	{
		try
		{
			if (SettingsStore.ReadOnlyDiagnostics || PerfProbe.Active)
			{
				return;
			}
			string path = SnapPath;
			if (!File.Exists(path))
			{
				return;
			}
			string snap = File.ReadAllText(path);
			int? current;
			using (RegistryKey read = Registry.CurrentUser.OpenSubKey(Key))
			{
				current = read?.GetValue("ToastEnabled") is int value ? value : null;
			}
			LegacyDecision d = DecideLegacyRestore(snap, current);
			string action = "left as is";
			if (d.Write != LegacyWrite.None)
			{
				using RegistryKey k = Registry.CurrentUser.OpenSubKey(Key, writable: true);
				if (k != null)
				{
					if (d.Write == LegacyWrite.Delete)
					{
						k.DeleteValue("ToastEnabled", throwOnMissingValue: false);
						action = "value removed (Windows default)";
					}
					else
					{
						k.SetValue("ToastEnabled", d.Value, RegistryValueKind.DWord);
						action = "set to " + d.Value;
					}
				}
			}
			if (d.DeleteSnap)
			{
				try
				{
					File.Delete(path);
				}
				catch
				{
				}
			}
			Logger.Log($"[notif] legacy quiet-hours suppression removed: ToastEnabled {action} (was {(current?.ToString() ?? "unset")}, snapshot '{snap.Trim()}')");
		}
		catch (Exception ex)
		{
			Logger.Log("[notif] legacy quiet-hours restore failed: " + ex.Message);
		}
	}
}
