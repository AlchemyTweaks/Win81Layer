#nullable enable

using System;
using System.IO;
using System.Linq;
using System.Security;
using System.Text.Json.Serialization;
using Microsoft.Win32;

namespace Win81Layer;

// Windows light/dark mode as it was before Dark rest changed it: the two Personalize values exactly as Windows stored
// them (kind and data), or null for a value that did not exist.
internal sealed class WindowsThemeSnapshot
{
	public RegValue? AppsUseLightTheme { get; set; }

	public RegValue? SystemUsesLightTheme { get; set; }

	public WindowsThemeSnapshot Clone()
	{
		return new WindowsThemeSnapshot
		{
			AppsUseLightTheme = AppsUseLightTheme?.Clone(),
			SystemUsesLightTheme = SystemUsesLightTheme?.Clone()
		};
	}

	// The value Dark rest writes: a DWORD 0.
	public static bool IsOurs(RegValue? v)
	{
		return v != null
			&& string.Equals(v.Kind, nameof(RegistryValueKind.DWord), StringComparison.Ordinal)
			&& string.Equals(v.Data, "0", StringComparison.Ordinal);
	}

	// Same kind and data (the name's casing does not matter); two missing values are the same.
	public static bool Same(RegValue? a, RegValue? b)
	{
		if (a == null || b == null)
		{
			return a == null && b == null;
		}
		return string.Equals(a.Kind, b.Kind, StringComparison.Ordinal) && string.Equals(a.Data, b.Data, StringComparison.Ordinal);
	}

	[JsonIgnore]
	public bool IsDark => IsOurs(AppsUseLightTheme) && IsOurs(SystemUsesLightTheme);

	// For the log, for example "apps=DWord:1 sys=absent".
	public string Describe()
	{
		return "apps=" + Short(AppsUseLightTheme) + " sys=" + Short(SystemUsesLightTheme);
	}

	private static string Short(RegValue? v)
	{
		return v == null ? "absent" : v.Kind + ":" + v.Data;
	}
}

// Windows light/dark mode through the same two HKCU values and the same ImmersiveColorSet broadcast that Windows
// Settings and ShellTheme.Toggle use. Only Dark rest changes them, and only through the seasonal engine, which
// snapshots them first and writes back exactly that snapshot.
internal static class WindowsThemeMode
{
	internal const string KeyPath = "Software\\Microsoft\\Windows\\CurrentVersion\\Themes\\Personalize";

	internal const string AppsName = "AppsUseLightTheme";

	internal const string SystemName = "SystemUsesLightTheme";

	// A missing key reads as two missing values. Throws SeasonalReadException when the key cannot be read.
	public static WindowsThemeSnapshot Read()
	{
		try
		{
			using RegistryKey? key = Registry.CurrentUser.OpenSubKey(KeyPath, writable: false);
			if (key == null)
			{
				return new WindowsThemeSnapshot();
			}
			return new WindowsThemeSnapshot
			{
				AppsUseLightTheme = ReadOne(key, AppsName),
				SystemUsesLightTheme = ReadOne(key, SystemName)
			};
		}
		catch (Exception ex) when (ex is SecurityException || ex is IOException || ex is UnauthorizedAccessException)
		{
			throw new SeasonalReadException("Could not read the Windows light/dark mode, so nothing was changed.");
		}
	}

	private static RegValue? ReadOne(RegistryKey key, string name)
	{
		string? actual = key.GetValueNames().FirstOrDefault(n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase));
		return actual == null ? null : RealPointerWorld.ReadValue(key, actual);
	}

	// Writes DWORD 0 to each value that is not 0 yet, tells Windows once, and reads back. True when both are dark.
	public static bool SetDark()
	{
		Guard();
		WindowsThemeSnapshot before = Read();
		if (!before.IsDark)
		{
			using (RegistryKey key = Registry.CurrentUser.CreateSubKey(KeyPath, writable: true))
			{
				if (!WindowsThemeSnapshot.IsOurs(before.AppsUseLightTheme))
				{
					RealPointerWorld.WriteOne(key, AppsName, Zero(before.AppsUseLightTheme, AppsName));
				}
				if (!WindowsThemeSnapshot.IsOurs(before.SystemUsesLightTheme))
				{
					RealPointerWorld.WriteOne(key, SystemName, Zero(before.SystemUsesLightTheme, SystemName));
				}
			}
			ShellTheme.BroadcastImmersiveColorSet();
		}
		bool dark = Read().IsDark;
		Logger.Log("Dark rest: Windows light/dark set to dark (" + before.Describe() + ")" + (dark ? "" : "; the read-back is not dark"));
		return dark;
	}

	// Puts back each value that still holds what Dark rest wrote; a value the user changed since is kept, and a value
	// that already equals the original is left alone. Returns
	// "restored", "kept-user", "partial", "unchanged" or "failed:<message>".
	public static string Restore(WindowsThemeSnapshot original)
	{
		Guard();
		string outcome;
		try
		{
			WindowsThemeSnapshot now = Read();
			int written = 0;
			int kept = 0;
			bool apps = Plan(original.AppsUseLightTheme, now.AppsUseLightTheme, ref written, ref kept);
			bool sys = Plan(original.SystemUsesLightTheme, now.SystemUsesLightTheme, ref written, ref kept);
			if (apps || sys)
			{
				using RegistryKey key = Registry.CurrentUser.CreateSubKey(KeyPath, writable: true);
				if (apps)
				{
					RealPointerWorld.WriteOne(key, AppsName, Original(original.AppsUseLightTheme, AppsName));
				}
				if (sys)
				{
					RealPointerWorld.WriteOne(key, SystemName, Original(original.SystemUsesLightTheme, SystemName));
				}
			}
			if (written > 0)
			{
				ShellTheme.BroadcastImmersiveColorSet();
			}
			outcome = Outcome(written, kept);
		}
		catch (Exception ex)
		{
			outcome = "failed:" + ex.Message;
		}
		Logger.Log("Dark rest: Windows light/dark " + outcome);
		return outcome;
	}

	// One value of a restore. A value that already equals the original needs nothing and is not counted (a restore that
	// was interrupted after this value went back, or a user who put back their own value, ends up the same). True when
	// the original must be written back: the value still holds what Dark rest wrote (a missing original deletes it
	// again). Any other value was changed outside the launcher and is kept as it is.
	internal static bool Plan(RegValue? original, RegValue? now, ref int written, ref int kept)
	{
		if (WindowsThemeSnapshot.Same(now, original))
		{
			return false;
		}
		if (!WindowsThemeSnapshot.IsOurs(now))
		{
			kept++;
			return false;
		}
		written++;
		return true;
	}

	private static RegValue? Original(RegValue? original, string name)
	{
		return original == null ? null : new RegValue { Name = original.Name.Length > 0 ? original.Name : name, Kind = original.Kind, Data = original.Data };
	}

	internal static string Outcome(int written, int kept)
	{
		if (kept > 0)
		{
			return written > 0 ? "partial" : "kept-user";
		}
		return written > 0 ? "restored" : "unchanged";
	}

	private static RegValue Zero(RegValue? existing, string name)
	{
		return new RegValue(existing?.Name is { Length: > 0 } n ? n : name, RegistryValueKind.DWord, "0");
	}

	private static void Guard()
	{
		if (SettingsStore.ReadOnlyDiagnostics)
		{
			throw new InvalidOperationException("Light/dark changes are disabled in read-only diagnostics mode.");
		}
	}
}
