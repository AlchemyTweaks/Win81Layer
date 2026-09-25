using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;

namespace Win81Layer;

// Single source of truth for the shell's light/dark state. Windows exposes it as HKCU\...\Personalize\AppsUseLightTheme
// (1 = light, 0 = dark). Before this existed the value was read independently in three places with no shared cache and
// no change signal; every reader now delegates here. On a theme flip we (a) recompute the semantic brush tokens that
// every DynamicResource-based template consumes, so open windows repaint with zero re-instantiation, and (b) rebuild
// the code-built context-menu palette. Code-behind surfaces that hold frozen brushes subscribe to Changed.
public static class ShellTheme
{
	private static readonly object Gate = new object();
	private static bool? _isDark;
	private static bool _initialized;

	// Frozen brush cache for code-behind consumers (File Browser, Win7 Start, PC Settings), rebuilt on Invalidate.
	private static Dictionary<string, SolidColorBrush>? _brushes;

	public static event EventHandler? Changed;

	public static bool IsDark
	{
		get
		{
			lock (Gate)
			{
				if (!_isDark.HasValue) _isDark = ReadIsDarkFromRegistry();
				return _isDark.Value;
			}
		}
	}

	public static bool IsLight => !IsDark;

	// Idempotent. Arms the OS preference hook and paints the initial tokens. Call once from App.OnStartup.
	public static void Initialize()
	{
		lock (Gate)
		{
			if (_initialized) return;
			_initialized = true;
		}
		try { SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged; } catch { }
	}

	private static void OnUserPreferenceChanged(object? sender, UserPreferenceChangedEventArgs e)
	{
		// Don't filter by category: Windows raises this under different categories across versions when the app
		// theme flips. Re-reading the registry is cheap and the equality guard below skips all unrelated changes,
		// so we never do work unless dark/light actually toggled.
		bool before;
		lock (Gate) { before = _isDark ?? ReadIsDarkFromRegistry(); }
		bool now = ReadIsDarkFromRegistry();
		if (now == before) return;   // an unrelated preference change, not a light/dark flip
		Invalidate();
		Dispatcher? d = Application.Current?.Dispatcher;
		if (d != null && !d.HasShutdownStarted)
			d.BeginInvoke((Action)(() => { ApplyResourceTokens(); RaiseChanged(); }));
		else { ApplyResourceTokens(); RaiseChanged(); }
	}

	// Drop the cached state so the next read re-hits the registry; also rebuild the code-built menu palette.
	public static void Invalidate()
	{
		lock (Gate) { _isDark = null; _brushes = null; }
		try { TaskbarContextMenu.InvalidateTheme(); } catch { }
	}

	// The wired in-app flip. Belt-and-suspenders: repaint directly instead of trusting the WM_SETTINGCHANGE round-trip.
	public static void Toggle()
	{
		try
		{
			int val = IsDark ? 1 : 0;   // currently dark -> go light (1); currently light -> go dark (0)
			using (RegistryKey k = Registry.CurrentUser.CreateSubKey("Software\\Microsoft\\Windows\\CurrentVersion\\Themes\\Personalize"))
			{
				k.SetValue("AppsUseLightTheme", val, RegistryValueKind.DWord);
				k.SetValue("SystemUsesLightTheme", val, RegistryValueKind.DWord);
			}
			BroadcastSettingChange("ImmersiveColorSet");
		}
		catch (Exception ex)
		{
			Logger.Log("ShellTheme.Toggle failed: " + ex.Message);
		}
		Invalidate();
		ApplyResourceTokens();
		RaiseChanged();
	}

	private static void RaiseChanged()
	{
		try { Changed?.Invoke(null, EventArgs.Empty); } catch (Exception ex) { Logger.Log("ShellTheme.Changed handler failed: " + ex.Message); }
	}

	private static bool ReadIsDarkFromRegistry()
	{
		try
		{
			using RegistryKey? k = Registry.CurrentUser.OpenSubKey("Software\\Microsoft\\Windows\\CurrentVersion\\Themes\\Personalize");
			return (k?.GetValue("AppsUseLightTheme") as int?) == 0;
		}
		catch { return false; }   // default to light, mirroring the readers this replaces
	}

	// ---- semantic token table -------------------------------------------------------------------------------------
	// Keys mirror the light defaults declared in Themes/Metro81.Controls.xaml plus Win81Menu.xaml's menu palette.
	// LIGHT values are kept byte-identical to today so nothing changes in light mode. DARK values are a design choice
	// (Windows 8.1 shipped no dark menu/controls) chosen to match Windows 11's dark chrome and stay legible.
	private static readonly (string Key, string Light, string Dark)[] Tokens =
	{
		// generic surfaces / ink
		("Metro81.Surface",          "#FFFFFF", "#1F1F1F"),
		("Metro81.Ink",              "#000000", "#FFFFFF"),
		("Metro81.Ink2",             "#5A5A5A", "#B4B4B4"),
		("Metro81.Border",           "#A6A6A6", "#3F3F3F"),
		("Metro81.DisabledInk",      "#707070", "#909090"),
		("Metro81.DisabledSurface",  "#E5E5E5", "#2A2A2A"),
		("Metro81.FieldBorder",      "#767676", "#6A6A6A"),
		("Metro81.HoverSurface",     "#DEDEDE", "#3F3F3F"),
		("Metro81.PressedSurface",   "#CCCCCC", "#4A4A4A"),
		("Metro81.Separator",        "#C8C8C8", "#3F3F3F"),
		("Metro81.SubtleBorder",     "#CCCCCC", "#3F3F3F"),
		("Metro81.GlyphBorder",      "#333333", "#B0B0B0"),
		("Metro81.ControlFill",      "#D3D3D3", "#3A3A3A"),
		("Metro81.ControlFillHover", "#BEBEBE", "#4A4A4A"),
		("Metro81.ControlFillBorder","#808080", "#6A6A6A"),
		("Metro81.ToggleTrack",      "#B0B0B0", "#5A5A5A"),
		("Metro81.ToggleThumb",      "#000000", "#F2F2F2"),
		("Metro81.SliderThumbFill",  "#000000", "#F2F2F2"),
		// menu surfaces (default WPF menus; the code-built shell menus use the parallel M.* palette in GetThemePalette)
		("Metro81.MenuSurface",      "#FFFFFF", "#2B2B2B"),
		("Metro81.MenuBorder",       "#000000", "#5A5A5A"),
		("Metro81.MenuHover",        "#DEDEDE", "#3F3F3F"),
		// keyboard-highlight row inverts in BOTH themes (dark bar/white text in light; white bar/dark text in dark)
		("Metro81.HighlightSurface", "#000000", "#FFFFFF"),
		("Metro81.HighlightInk",     "#FFFFFF", "#000000"),
		("Metro81.HighlightGesture", "#C8C8C8", "#3A3A3A"),
		// scrollbar set follows the theme (the always-dark identity surfaces use Metro81.ScrollBarDark explicitly)
		("Metro81.ScrollTrackBrush",         "#DEDEDE", "#1A1A1A"),
		("Metro81.ScrollThumbBrush",         "#000000", "#8A8A8A"),
		("Metro81.ScrollThumbHoverBrush",    "#333333", "#9A9A9A"),
		("Metro81.ScrollThumbPressedBrush",  "#666666", "#B0B0B0"),
		("Metro81.ScrollButtonBrush",        "#DEDEDE", "#1A1A1A"),
		("Metro81.ScrollButtonHoverBrush",   "#C8C8C8", "#333333"),
		("Metro81.ScrollButtonPressedBrush", "#B0B0B0", "#4A4A4A"),
		("Metro81.ScrollGlyphBrush",         "#000000", "#FFFFFF"),
	};

	// Writes the current-theme color into each token at Application scope. Because every template consumes these via
	// {DynamicResource}, all open instances repaint instantly with no per-window subscription (WPF re-resolves).
	public static void ApplyResourceTokens()
	{
		Application? app = Application.Current;
		if (app == null) return;
		void Do()
		{
			bool dark = IsDark;
			ResourceDictionary res = app.Resources;
			foreach (var (key, light, darkHex) in Tokens)
			{
				try
				{
					SolidColorBrush b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(dark ? darkHex : light));
					b.Freeze();
					res[key] = b;
				}
				catch { }
			}
		}
		Dispatcher d = app.Dispatcher;
		if (d.CheckAccess()) Do();
		else d.Invoke(Do);
	}

	// ---- code-behind brush accessors (for surfaces that build brushes in code, not via XAML resources) -------------
	private static SolidColorBrush Brush(string lightHex, string darkHex)
	{
		lock (Gate)
		{
			_brushes ??= new Dictionary<string, SolidColorBrush>(StringComparer.Ordinal);
			string hex = IsDark ? darkHex : lightHex;
			string cacheKey = hex;
			if (_brushes.TryGetValue(cacheKey, out SolidColorBrush? cached)) return cached;
			SolidColorBrush b;
			try { b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)); }
			catch { b = new SolidColorBrush(IsDark ? Colors.Black : Colors.White); }
			b.Freeze();
			_brushes[cacheKey] = b;
			return b;
		}
	}

	public static Brush Surface => Brush("#FFFFFF", "#1F1F1F");
	public static Brush Ink => Brush("#000000", "#FFFFFF");
	public static Brush Ink2 => Brush("#5A5A5A", "#B4B4B4");
	public static Brush FieldSurface => Brush("#FFFFFF", "#2A2A2A");
	public static Brush Hover => Brush("#DEDEDE", "#3F3F3F");
	public static Brush Separator => Brush("#CFD8E3", "#3F3F3F");
	public static Brush Border => Brush("#A6A6A6", "#3F3F3F");

	[DllImport("user32.dll", CharSet = CharSet.Auto)]
	private static extern nint SendMessageTimeout(nint hWnd, uint msg, nint wParam, string lParam, uint flags, uint timeout, out nint result);

	private static void BroadcastSettingChange(string section)
	{
		System.Threading.Tasks.Task.Run(delegate
		{
			try { SendMessageTimeout((nint)65535, 26u, nint.Zero, section, 2u, 200u, out _); } catch { }
		});
	}
}
