#nullable enable

using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace Win81Layer;

// Dark rest: the fourth choice of the seasonal picker (Normal / Halloween / Christmas / Dark rest). Windows itself is
// switched to dark mode by the seasonal engine, so everything that already follows light/dark goes dark on its own.
// The surfaces that never followed light/dark (Metro Start, the taskbar and its flyouts, Action Center, the charms and
// their panes, toasts, the OSD, PC Settings and the 8.1 captions) draw a dark variant only while the marker says
// "rest"; with Normal, or under High Contrast, every one of those branches is skipped.
internal static class DarkRest
{
	private static bool _calmApplied;

	public static bool IsOnFor(AppSettings? s)
	{
		return s != null && SeasonalTheme.IsRest(SeasonalTheme.NormalizeId(s.SeasonalActive));
	}

	// Read-only diagnostics that did not pin a look (the render tests of single surfaces) always show the Normal
	// variants, whatever the user's real marker says; --seasonaltest pins its own settings to render Dark rest.
	private static bool Pinned => !SettingsStore.ReadOnlyDiagnostics || SettingsStore.DiagnosticsOverride != null;

	// The marker as the settings cache has it: no clone, no lock once the cache is loaded.
	public static bool IsOn => Pinned && SeasonalTheme.IsRest(SeasonalTheme.NormalizeId(SettingsStore.PeekSeasonalActive()));

	// High Contrast wins: its own colours are an accessibility setting.
	public static bool Visuals => IsOn && !SystemParameters.HighContrast;

	// Visuals for settings a paint path already loaded.
	public static bool VisualsFor(AppSettings? s)
	{
		return Pinned && IsOnFor(s) && !SystemParameters.HighContrast;
	}

	public static bool CalmMotion(AppSettings s)
	{
		return IsOnFor(s) && s.RestCalmMotion;
	}

	public static bool DimTiles(AppSettings s)
	{
		return IsOnFor(s) && s.RestDimTiles;
	}

	private static bool _highContrastWatched;

	private static bool _lastHighContrast;

	// Accessibility wins at once: when High Contrast is switched on or off while Dark rest is on, every Dark rest
	// surface is refreshed right away (refresh = the app's live seasonal refresh). One system event, no polling. UI
	// thread, once.
	public static void WatchHighContrast(Action refresh)
	{
		if (_highContrastWatched)
		{
			return;
		}
		_highContrastWatched = true;
		_lastHighContrast = SystemParameters.HighContrast;
		SystemParameters.StaticPropertyChanged += delegate(object? _, System.ComponentModel.PropertyChangedEventArgs e)
		{
			if (e.PropertyName != nameof(SystemParameters.HighContrast))
			{
				return;
			}
			bool now = SystemParameters.HighContrast;
			if (now == _lastHighContrast)
			{
				return;
			}
			_lastHighContrast = now;
			if (!IsOn)
			{
				return;
			}
			Logger.Log("Dark rest: High Contrast turned " + (now ? "on" : "off") + "; the Dark rest surfaces follow");
			Application.Current?.Dispatcher.BeginInvoke(refresh);
		};
	}

	// Calmer animations follow the marker and the option. The user's own Motion setting is never written. UI thread.
	public static void Sync()
	{
		bool calm = false;
		try
		{
			calm = Pinned && CalmMotion(SettingsStore.FastSnapshot);
		}
		catch
		{
		}
		Motion.CalmOverride = calm;
		if (calm != _calmApplied)
		{
			_calmApplied = calm;
			Logger.Log("Dark rest: calmer animations " + (calm ? "on" : "off"));
		}
	}
}

// The Dark rest colours. Low glare: near-black slate surfaces, soft off-white ink, one muted blue-grey accent, and no
// saturated colour anywhere. Every brush is frozen, so it can be shared across windows and threads.
internal static class DarkRestPalette
{
	// ----- Metro Start (Start.* resources; the Normal values equal the XAML defaults in StartScreen.xaml)
	public static readonly Brush Ink = Hex("#FFE4E7EB");

	public static readonly Brush SearchField = Hex("#FF262B31");

	public static readonly Brush SearchInk = Hex("#FFE4E7EB");

	public static readonly Brush SearchHint = Hex("#FF8D96A0");

	public static readonly Brush SearchGlyph = Hex("#FFC3CAD2");

	public static readonly Brush TileScrim = Hex("#40000000");

	// The header's icon glyph ink (the soft ink at the same 90% the white glyph uses).
	public static readonly Brush InkSoft = Hex("#E6E4E7EB");

	public static readonly Brush NormalInk = Hex("#FFFFFFFF");

	public static readonly Brush NormalInkSoft = Hex("#E6FFFFFF");

	public static readonly Brush NormalSearchField = Hex("#FFFFFFFF");

	public static readonly Brush NormalSearchBorder = Hex("#FF767676");

	public static readonly Brush NormalSearchInk = Hex("#FF1A1A1A");

	public static readonly Brush NormalSearchHint = Hex("#FF767676");

	public static readonly Brush NormalSearchGlyph = Hex("#FF000000");

	// ----- taskbar
	public static readonly Color TaskbarChrome = C("#FF161A1F");

	public static readonly Color TaskbarBackground = C("#F2161A1F");

	// The taskbar's accent line and the volume slider fill: the flyouts' selection tone instead of a near-white
	// brightened accent (4.3:1 on the taskbar).
	public static readonly Color TaskbarAccent = C("#FF688197");

	// ----- charms bar
	public static readonly Color Charms = C("#FF16191D");

	public static readonly Color CharmsGlass = C("#E616191D");

	// ----- Action Center
	public static readonly Color PanelTop = C("#FF1E2329");

	public static readonly Color PanelBottom = C("#FF14171B");

	public static readonly Color PanelLink = C("#FF9FB0C2");

	public static readonly Color SliderTrack = C("#3AFFFFFF");

	// ----- dark search fields (Search pane, switcher). Darker than the pane they sit on, with the border every dark
	// field shares (the Start all-apps box and the PC Settings fields use it too).
	public static readonly Brush FieldBox = Hex("#FF1A1E23");

	public static readonly Brush FieldBorder = Hex("#FF4A525B");

	public static readonly Thickness FieldBorderThickness = new Thickness(1.0);

	public static readonly Brush FieldInk = Hex("#FFE4E7EB");

	public static readonly Brush FieldHint = Hex("#FF8D96A0");

	// ----- toasts
	public static readonly Brush ToastCard = Hex("#FF1E2226");

	public static readonly Brush ToastCardGlass = Hex("#F01E2226");

	public static readonly Brush ToastBorder = Hex("#FF3A4048");

	public static readonly Brush ToastApp = Hex("#FF8A939D");

	public static readonly Brush ToastTitle = Hex("#FFE4E7EB");

	public static readonly Brush ToastMessage = Hex("#FFB9C0C8");

	public static readonly Brush ToastTime = Hex("#FF7D8690");

	public static readonly Brush ToastClose = Hex("#FFB9C0C8");

	public static readonly Brush ToastCloseHover = Hex("#FF2E343B");

	// ----- volume and brightness OSD
	public static readonly Brush OsdCard = Hex("#F0181B1F");

	public static readonly Brush OsdEmpty = Hex("#FF343A41");

	// ----- PC Settings
	public static readonly Brush PcPage = Hex("#FF1A1D21");

	public static readonly Brush PcText = Hex("#FFE4E7EB");

	public static readonly Brush PcText2 = Hex("#FF9AA3AD");

	public static readonly Brush PcField = Hex("#FF23272C");

	public static readonly Brush PcFieldBorder = Hex("#FF4A525B");

	public static readonly Brush PcListHover = Hex("#FF2E343B");

	public static readonly Brush PcKnob = Hex("#FFE4E7EB");

	public static readonly Brush PcTrack = Hex("#FF3A4048");

	// Headings and link titles on the dark page (the accent's hue, light enough to read: 7.6:1 on the page). Also the
	// frame of a selected picker tile.
	public static readonly Brush PcAccentInk = Hex("#FF9FB0C2");

	// The On fill of a toggle on the dark page: 3.3:1 against the Off fill, so On and Off read apart at a glance.
	public static readonly Brush ToggleOn = Hex("#FF688197");

	// ----- scrollbars: a quiet blue-grey thumb on a dark track instead of the bright neutral grey of the Windows-like
	// dark set. The same values go under the theme-following keys and under the always-dark (*Dark) keys.
	private static readonly (string Key, string Hex)[] ScrollTokens = new (string, string)[8]
	{
		("Metro81.ScrollTrackBrush", "#FF16191D"),
		("Metro81.ScrollThumbBrush", "#FF4A525B"),
		("Metro81.ScrollThumbHoverBrush", "#FF5A6570"),
		("Metro81.ScrollThumbPressedBrush", "#FF6B7783"),
		("Metro81.ScrollButtonBrush", "#FF16191D"),
		("Metro81.ScrollButtonHoverBrush", "#FF2E343B"),
		("Metro81.ScrollButtonPressedBrush", "#FF3A4048"),
		("Metro81.ScrollGlyphBrush", "#FF9AA3AD")
	};

	// ----- the launcher's own dark pages (PC Settings): one blue-grey family for every Metro control instead of the
	// neutral greys of the Windows-like dark token set. Written after ShellTheme.WriteTokens(dark) at window scope.
	private static readonly (string Key, string Hex)[] PageTokens = new (string, string)[21]
	{
		("Metro81.Surface", "#FF23272C"),
		("Metro81.Ink", "#FFE4E7EB"),
		("Metro81.Ink2", "#FFB9C0C8"),
		("Metro81.Border", "#FF3A4048"),
		("Metro81.DisabledInk", "#FF8A939D"),
		("Metro81.DisabledSurface", "#FF20242A"),
		("Metro81.FieldBorder", "#FF4A525B"),
		("Metro81.HoverSurface", "#FF2E343B"),
		("Metro81.PressedSurface", "#FF3A4048"),
		("Metro81.Separator", "#FF343A41"),
		("Metro81.SubtleBorder", "#FF343A41"),
		("Metro81.GlyphBorder", "#FF9AA3AD"),
		("Metro81.ControlFill", "#FF2E343B"),
		("Metro81.ControlFillHover", "#FF3A4048"),
		("Metro81.ControlFillBorder", "#FF4A525B"),
		("Metro81.ToggleTrack", "#FF2A2F35"),
		("Metro81.ToggleThumb", "#FFE4E7EB"),
		("Metro81.SliderThumbFill", "#FFE4E7EB"),
		("Metro81.MenuSurface", "#FF23272C"),
		("Metro81.MenuBorder", "#FF4A525B"),
		("Metro81.MenuHover", "#FF2E343B")
	};

	private static readonly Dictionary<string, Brush> TokenBrushes = BuildTokenBrushes();

	private static Dictionary<string, Brush> BuildTokenBrushes()
	{
		Dictionary<string, Brush> d = new Dictionary<string, Brush>(StringComparer.Ordinal);
		foreach ((string key, string hex) in PageTokens)
		{
			d[key] = Hex(hex);
		}
		foreach ((string key, string hex) in ScrollTokens)
		{
			Brush b = Hex(hex);
			d[key] = b;
			d[key + "Dark"] = b;
		}
		return d;
	}

	// Quiet scrollbars for a Dark rest surface: the theme-following and the always-dark keys.
	public static void WriteScrollBrushes(ResourceDictionary target)
	{
		foreach ((string key, _) in ScrollTokens)
		{
			target[key] = TokenBrushes[key];
			target[key + "Dark"] = TokenBrushes[key + "Dark"];
		}
	}

	// The PC Settings page set: the control family, the quiet scrollbars and the toggle style with its lighter On fill.
	public static void WritePageResources(ResourceDictionary target, Style? toggleBase)
	{
		foreach ((string key, _) in PageTokens)
		{
			target[key] = TokenBrushes[key];
		}
		WriteScrollBrushes(target);
		if (ToggleStyle(toggleBase) is Style toggle)
		{
			target["Metro81.ToggleSwitch"] = toggle;
		}
	}

	// Removes only what the Write methods put there (an entry that holds anything else is left alone), so the
	// surface falls back to its normal resources exactly.
	public static void RemoveRestResources(ResourceDictionary target)
	{
		foreach (KeyValuePair<string, Brush> pair in TokenBrushes)
		{
			if (target.Contains(pair.Key) && ReferenceEquals(target[pair.Key], pair.Value))
			{
				target.Remove(pair.Key);
			}
		}
		if (_toggleStyle != null && target.Contains("Metro81.ToggleSwitch") && ReferenceEquals(target["Metro81.ToggleSwitch"], _toggleStyle))
		{
			target.Remove("Metro81.ToggleSwitch");
		}
		if (_scrollBarStyle != null && target.Contains("Metro81.ScrollBarDark") && ReferenceEquals(target["Metro81.ScrollBarDark"], _scrollBarStyle))
		{
			target.Remove("Metro81.ScrollBarDark");
		}
	}

	private static Style? _toggleStyle;

	private static Style? _toggleBase;

	private static Style? ToggleStyle(Style? baseStyle)
	{
		if (baseStyle == null)
		{
			return null;
		}
		if (ReferenceEquals(baseStyle, _toggleStyle))
		{
			// Looked up where the Dark rest style is already in place.
			return _toggleStyle;
		}
		if (_toggleStyle == null || !ReferenceEquals(_toggleBase, baseStyle))
		{
			Style s = new Style(typeof(ToggleButton), baseStyle);
			s.Resources["MenuAccent"] = ToggleOn;
			s.Seal();
			_toggleStyle = s;
			_toggleBase = baseStyle;
		}
		return _toggleStyle;
	}

	private static Style? _scrollBarStyle;

	// A dark scrollbar style for the surfaces that use the always-dark Metro81.ScrollBarDark style (its own resources
	// fix the neutral greys): the same template with the quiet colours. Null when the base style is not available.
	public static Style? ScrollBarStyle()
	{
		if (_scrollBarStyle == null && Application.Current?.TryFindResource("Metro81.ScrollBar") is Style baseStyle)
		{
			Style s = new Style(typeof(ScrollBar), baseStyle);
			foreach ((string key, _) in ScrollTokens)
			{
				s.Resources[key] = TokenBrushes[key];
			}
			s.Seal();
			_scrollBarStyle = s;
		}
		return _scrollBarStyle;
	}

	// The always-dark scrollbar style key, pointed at the quiet style for one surface's resources.
	public static void WriteScrollBarDarkStyle(ResourceDictionary target)
	{
		if (ScrollBarStyle() is Style s)
		{
			target["Metro81.ScrollBarDark"] = s;
		}
	}

	// A tile colour toned down for the dark panel: the hue stays recognisable, the saturation drops to about a third
	// and the lightness sits in a narrow dark band. For example Wi-Fi #00A0E7 becomes #325767.
	public static Color Mute(Color c)
	{
		ColorMath.RgbToHsl(c.R / 255.0, c.G / 255.0, c.B / 255.0, out double h, out double s, out double l);
		return FromHsl(h, s * 0.35, Math.Clamp(l, 0.24, 0.30), c.A);
	}

	// The rest rule of SettingsPane.AccentToneColor: the accent's hue at a low saturation, about half as light as the
	// normal tone. For the muted accent #4F6272, 0.34 gives the pane colour #273038.
	public static Color AccentTone(Color accent, double lightness)
	{
		ColorMath.RgbToHsl(accent.R / 255.0, accent.G / 255.0, accent.B / 255.0, out double h, out double s, out _);
		return FromHsl(h, Math.Clamp(s, 0.08, 0.20), lightness * 0.55, 255);
	}

	private static Color FromHsl(double h, double s, double l, byte a)
	{
		ColorMath.HslToRgb(h, Math.Clamp(s, 0.0, 1.0), Math.Clamp(l, 0.0, 1.0), out double r, out double g, out double b);
		return Color.FromArgb(a, B(r), B(g), B(b));
	}

	private static byte B(double v)
	{
		return (byte)Math.Clamp((int)Math.Round(v * 255.0), 0, 255);
	}

	private static Color C(string hex)
	{
		return (Color)ColorConverter.ConvertFromString(hex);
	}

	private static Brush Hex(string hex)
	{
		SolidColorBrush b = new SolidColorBrush(C(hex));
		b.Freeze();
		return b;
	}
}
