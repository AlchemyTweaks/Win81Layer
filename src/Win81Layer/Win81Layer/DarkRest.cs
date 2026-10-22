#nullable enable

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;

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

// The Dark rest colours: a true black family. Black canvas, near-black raised surfaces, soft off-white ink (never pure
// white), neutral greys with no hue, and one muted teal accent family for selection, focus and links. Text keeps at
// least 4.5:1 against what it sits on, secondary text 7:1, and the outlines of interactive controls 3:1. Every brush is
// frozen, so it can be shared across windows and threads.
internal static class DarkRestPalette
{
	// ----- the neutral family (no hue)
	public static readonly Color CanvasColor = C("#FF000000");

	public static readonly Color PanelColor = C("#FF0A0A0A");

	public static readonly Color PopupColor = C("#FF0F0F0F");

	public static readonly Color CardColor = C("#FF121212");

	public static readonly Color FieldColor = C("#FF141414");

	public static readonly Color HoverColor = C("#FF1F1F1F");

	public static readonly Color PressedColor = C("#FF2A2A2A");

	public static readonly Color MenuHoverColor = C("#FF262626");

	// Decorative only (never the only cue).
	public static readonly Color SeparatorColor = C("#FF262626");

	// The container outline of popups (decorative).
	public static readonly Color PopupEdgeColor = C("#FF4D4D4D");

	// The outline of an interactive control: 3:1 or more against every surface above.
	public static readonly Color BoundaryColor = C("#FF6E6E6E");

	public static readonly Color InkColor = C("#FFDCDCDC");

	public static readonly Color Ink2Color = C("#FFAAAAAA");

	public static readonly Color HintColor = C("#FF888888");

	// Text on an accent fill (selected rows, the keyboard menu row, primary buttons): the lightest ink that is still not
	// pure white, 4.5:1 on AccentFill.
	public static readonly Color InkOnAccentColor = C("#FFEEEEEE");

	// ----- the accent family: hue 191 at a low saturation, the colour of the Dark rest picture's faint grid
	public static readonly Color AccentColor = C("#FF44636A");

	// Selected rows, primary buttons, toggles that are on, the keyboard-highlighted menu row.
	public static readonly Color AccentFillColor = C("#FF4E7179");

	// Focus borders, indicator lines, slider, progress and OSD fills.
	public static readonly Color AccentLineColor = C("#FF6798A2");

	// Links and headings.
	public static readonly Color AccentInkColor = C("#FF9ABAC1");

	public static readonly Brush Canvas = Frozen(CanvasColor);

	public static readonly Brush Panel = Frozen(PanelColor);

	public static readonly Brush Popup = Frozen(PopupColor);

	public static readonly Brush Card = Frozen(CardColor);

	public static readonly Brush Field = Frozen(FieldColor);

	public static readonly Brush Hover = Frozen(HoverColor);

	public static readonly Brush Pressed = Frozen(PressedColor);

	public static readonly Brush MenuHover = Frozen(MenuHoverColor);

	public static readonly Brush Separator = Frozen(SeparatorColor);

	public static readonly Brush PopupEdge = Frozen(PopupEdgeColor);

	public static readonly Brush Boundary = Frozen(BoundaryColor);

	public static readonly Brush Ink = Frozen(InkColor);

	public static readonly Brush Ink2 = Frozen(Ink2Color);

	public static readonly Brush Hint = Frozen(HintColor);

	public static readonly Brush InkOnAccent = Frozen(InkOnAccentColor);

	public static readonly Brush Accent = Frozen(AccentColor);

	public static readonly Brush AccentFill = Frozen(AccentFillColor);

	public static readonly Brush AccentLine = Frozen(AccentLineColor);

	public static readonly Brush AccentInk = Frozen(AccentInkColor);

	// ----- Metro Start (Start.* resources; the Normal values equal the XAML defaults in StartScreen.xaml)
	public static readonly Brush SearchField = Field;

	public static readonly Brush SearchInk = Ink;

	public static readonly Brush SearchHint = Hint;

	public static readonly Brush SearchGlyph = Ink2;

	// Dims the tile fill and icon (35 %); the tile names are drawn above it.
	public static readonly Brush TileScrim = Hex("#59000000");

	// The header's icon glyph ink (the soft ink at the same 90% the white glyph uses).
	public static readonly Brush InkSoft = Hex("#E6DCDCDC");

	// Hover wash of the Start lists and the semantic zoom letters: a neutral lift, visible on black.
	public static readonly Brush StartHover = Hex("#2EFFFFFF");

	public static readonly Brush StartZoomHover = Hex("#47FFFFFF");

	// The all-apps keyboard focus ring (1.5 DIU) and the "new" label.
	public static readonly Brush StartFocusRing = AccentLine;

	public static readonly Brush StartNewInk = AccentInk;

	// The Start selection bar and the Personalize pane: opaque (on a black board translucency only lets the header and
	// the tiles show through as smudges).
	public static readonly Brush StartPane = Hex("#FF0A0A0A");

	// The tile app bar and its top line (the popups' outline, so the bar's edge shows on the black board).
	public static readonly Brush AppBar = Popup;

	public static readonly Brush AppBarLine = PopupEdge;

	public static readonly Brush NormalInk = Hex("#FFFFFFFF");

	public static readonly Brush NormalInkSoft = Hex("#E6FFFFFF");

	public static readonly Brush NormalSearchField = Hex("#FFFFFFFF");

	public static readonly Brush NormalSearchBorder = Hex("#FF767676");

	public static readonly Brush NormalSearchInk = Hex("#FF1A1A1A");

	public static readonly Brush NormalSearchHint = Hex("#FF767676");

	public static readonly Brush NormalSearchGlyph = Hex("#FF000000");

	// ----- taskbar: opaque black, seamless with the black desktop
	public static readonly Color TaskbarChrome = PopupColor;

	public static readonly Color TaskbarBackground = CanvasColor;

	// The taskbar's accent line and the volume slider fill.
	public static readonly Color TaskbarAccent = AccentLineColor;

	// The tray overflow and the open-apps overflow.
	public static readonly Color FlyoutBg = C("#F20F0F0F");

	// ----- charms bar
	public static readonly Color Charms = PanelColor;

	public static readonly Color CharmsGlass = C("#E60A0A0A");

	// ----- Action Center
	public static readonly Color PanelTop = PanelColor;

	public static readonly Color PanelBottom = PanelColor;

	public static readonly Color PanelLink = AccentInkColor;

	public static readonly Color SliderTrack = C("#FF333333");

	public static readonly Brush SliderTrackBrush = Frozen(SliderTrack);

	// ----- dark search fields (Search pane, switcher, the Start all-apps box, PC Settings fields)
	public static readonly Brush FieldBox = Field;

	public static readonly Brush FieldBorder = Boundary;

	public static readonly Thickness FieldBorderThickness = new Thickness(1.0);

	public static readonly Brush FieldInk = Ink;

	public static readonly Brush FieldHint = Hint;

	// ----- toasts
	public static readonly Brush ToastCard = Popup;

	public static readonly Brush ToastCardGlass = Hex("#F00F0F0F");

	public static readonly Brush ToastBorder = PopupEdge;

	public static readonly Brush ToastApp = Ink2;

	public static readonly Brush ToastTitle = Ink;

	public static readonly Brush ToastMessage = Ink2;

	public static readonly Brush ToastTime = Hint;

	public static readonly Brush ToastClose = Ink2;

	// the close button under the mouse: the menus' hover row (1.5:1 on the card)
	public static readonly Brush ToastCloseHover = Hex("#FF323232");

	// ----- volume and brightness OSD
	public static readonly Brush OsdCard = Hex("#F20F0F0F");

	public static readonly Brush OsdEmpty = Separator;

	public static readonly Brush OsdFill = AccentLine;

	public static readonly Brush OsdMuted = Hex("#FF808080");

	// ----- PC Settings
	public static readonly Brush PcPage = Canvas;

	public static readonly Brush PcText = Ink;

	public static readonly Brush PcText2 = Ink2;

	public static readonly Brush PcField = Field;

	public static readonly Brush PcFieldBorder = Boundary;

	// a row under the mouse, on the page and in the drop-down lists (1.45:1 on the field fill)
	public static readonly Brush PcListHover = Hex("#FF333333");

	public static readonly Brush PcKnob = Ink;

	public static readonly Brush PcTrack = SliderTrackBrush;

	// Headings, link titles and the slider fill on the black page (10:1).
	public static readonly Brush PcAccentInk = AccentInk;

	// The On fill of a toggle: 3.7:1 against the Off fill, so On and Off read apart at a glance.
	public static readonly Brush ToggleOn = AccentFill;

	// The selected navigation row of PC Settings: the accent on the black navigation pane (3:1), soft ink on it (4.7:1).
	public static readonly Brush NavSelected = Accent;

	// A hover wash that can be seen on the black panes (about 1.5:1 against #0A0A0A).
	public static readonly Brush HoverWash = Hex("#28FFFFFF");

	// ----- 8.1 captions: the accent caption while active, a quiet dark one while inactive
	public static readonly Brush CaptionInactive = Field;

	// ----- shell code-behind accessors (ShellTheme.Surface and its siblings)
	public static readonly Brush ShellSurface = Panel;

	public static readonly Brush ShellBorder = PopupEdge;

	// ----- scrollbars: a neutral grey thumb on a black track (3:1 or more). The same values go under the theme-following
	// keys and under the always-dark (*Dark) keys.
	private static readonly (string Key, string Hex)[] ScrollTokens = new (string, string)[8]
	{
		("Metro81.ScrollTrackBrush", "#FF0A0A0A"),
		("Metro81.ScrollThumbBrush", "#FF5E5E5E"),
		("Metro81.ScrollThumbHoverBrush", "#FF767676"),
		("Metro81.ScrollThumbPressedBrush", "#FF8A8A8A"),
		("Metro81.ScrollButtonBrush", "#FF0A0A0A"),
		("Metro81.ScrollButtonHoverBrush", "#FF1F1F1F"),
		("Metro81.ScrollButtonPressedBrush", "#FF2A2A2A"),
		("Metro81.ScrollGlyphBrush", "#FFAAAAAA")
	};

	// ----- the Metro controls: one table for the application scope (every window) and for the PC Settings window. The
	// accent-role keys (focus border, links, slider fill, MenuAccent) carry the accent family instead of the Start accent.
	private static readonly (string Key, string Hex)[] ControlTokens = new (string, string)[53]
	{
		("Metro81.Surface", "#FF141414"),
		("Metro81.Ink", "#FFDCDCDC"),
		("Metro81.Ink2", "#FFAAAAAA"),
		("Metro81.Border", "#FF6E6E6E"),
		("Metro81.DisabledInk", "#FF888888"),
		("Metro81.DisabledSurface", "#FF0F0F0F"),
		("Metro81.FieldBorder", "#FF6E6E6E"),
		("Metro81.HoverSurface", "#FF323232"),
		("Metro81.PressedSurface", "#FF4A4A4A"),
		("Metro81.Separator", "#FF262626"),
		("Metro81.SubtleBorder", "#FF262626"),
		("Metro81.GlyphBorder", "#FFAAAAAA"),
		("Metro81.ControlFill", "#FF1F1F1F"),
		("Metro81.ControlFillHover", "#FF3D3D3D"),
		("Metro81.ControlFillBorder", "#FF6E6E6E"),
		("Metro81.ToggleTrack", "#FF0A0A0A"),
		("Metro81.ToggleThumb", "#FFDCDCDC"),
		("Metro81.SliderThumbFill", "#FFDCDCDC"),
		("Metro81.MenuSurface", "#FF0F0F0F"),
		("Metro81.MenuBorder", "#FF4D4D4D"),
		("Metro81.MenuHover", "#FF262626"),
		("Metro81.HighlightSurface", "#FF4E7179"),
		("Metro81.HighlightInk", "#FFEEEEEE"),
		("Metro81.HighlightGesture", "#FFEEEEEE"),
		("Metro81.OnAccentInk", "#FFEEEEEE"),
		("Metro81.NavInk", "#FFDCDCDC"),
		("Metro81.SliderTrack", "#FF333333"),
		("Metro81.SliderDarkTrack", "#FF333333"),
		("Metro81.SliderDarkThumb", "#FFDCDCDC"),
		("Metro81.SliderFill", "#FF6798A2"),
		("Metro81.FocusBorder", "#FF9ABAC1"),
		("Metro81.LinkInk", "#FF9ABAC1"),
		("Metro81.LinkInkHover", "#FFDCDCDC"),
		("MenuAccent", "#FF4E7179"),
		("MenuAccentPressed", "#FF3A555B"),
		// a slider thumb under the mouse and while dragged, and a pressed toggle thumb: light, so they never fade into
		// the track; the drag value box is a dark popup
		("Metro81.SliderThumbHover", "#FFEEEEEE"),
		("Metro81.SliderThumbDrag", "#FFAAAAAA"),
		("Metro81.ToggleThumbPressed", "#FFEEEEEE"),
		("Metro81.ValuePopupSurface", "#FF0F0F0F"),
		("Metro81.ValuePopupInk", "#FFDCDCDC"),
		// the outline of slider and progress tracks, the progress track, and the resting outline of a plain button
		("Metro81.SliderTrackBorder", "#FF6E6E6E"),
		("Metro81.ProgressTrack", "#FF333333"),
		("Metro81.ButtonBorder", "#FF6E6E6E"),
		// selected text in a field: the accent line (with the opacity below)
		("Metro81.TextSelection", "#FF6798A2"),
		// the hover wash and the selected row of command and navigation buttons
		("Metro81.CommandHover", "#28FFFFFF"),
		("Metro81.CommandSelected", "#FF44636A"),
		// tooltips belong to the popup family (not to the fields)
		("Metro81.TooltipSurface", "#FF0F0F0F"),
		("Metro81.TooltipBorder", "#FF4D4D4D"),
		// the hover washes of the charms panes' rows: about 1.5:1 on the black panes
		("Metro81.PaneHover12", "#28FFFFFF"),
		("Metro81.PaneHover18", "#28FFFFFF"),
		("Metro81.PaneHover20", "#28FFFFFF"),
		// the keyboard position in an open drop-down list: an accent outline around the highlighted row (5.8:1), which
		// the hover lift alone (1.4:1) does not show clearly
		("Metro81.ItemHighlightBorder", "#FF6798A2"),
		// the outline a disabled button keeps (3:1 on black), so its extent stays visible
		("Metro81.DisabledBorder", "#FF5E5E5E")
	};

	// Values of the control family that are not brushes: disabled controls and rows keep their text at 4.5:1 on the
	// black panes (the secondary ink at 0.72 is #7D7D7D on #0A0A0A, the soft ink #A1A1A1), and selected text is a little
	// stronger.
	private static readonly (string Key, object Value)[] ControlValues = new (string, object)[3]
	{
		("Metro81.DisabledOpacity", 0.72),
		("Metro81.DisabledRowOpacity", 0.72),
		("Metro81.TextSelectionOpacity", 0.5)
	};

	// The rest value of a non-brush control key (null when it is not one of them).
	public static object? ControlValue(string key)
	{
		foreach ((string k, object v) in ControlValues)
		{
			if (k == key)
			{
				return v;
			}
		}
		return null;
	}

	private static readonly Dictionary<string, Brush> TokenBrushes = BuildTokenBrushes();

	private static Dictionary<string, Brush> BuildTokenBrushes()
	{
		Dictionary<string, Brush> d = new Dictionary<string, Brush>(StringComparer.Ordinal);
		foreach ((string key, string hex) in ControlTokens)
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

	// The brush the control table holds for key (null when the key is not one of them). For checks and for code that
	// reads a token directly.
	public static Brush? Token(string key)
	{
		return TokenBrushes.TryGetValue(key, out Brush? b) ? b : null;
	}

	// Every key of the control table, with its brush.
	public static IEnumerable<KeyValuePair<string, Brush>> Tokens => TokenBrushes;

	// Quiet scrollbars for a Dark rest surface: the theme-following and the always-dark keys.
	public static void WriteScrollBrushes(ResourceDictionary target)
	{
		foreach ((string key, _) in ScrollTokens)
		{
			target[key] = TokenBrushes[key];
			target[key + "Dark"] = TokenBrushes[key + "Dark"];
		}
	}

	// The PC Settings page set: the control family, the quiet scrollbars and the toggle style with its On fill.
	public static void WritePageResources(ResourceDictionary target, Style? toggleBase)
	{
		foreach ((string key, _) in ControlTokens)
		{
			target[key] = TokenBrushes[key];
		}
		foreach ((string key, object value) in ControlValues)
		{
			target[key] = value;
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
		foreach ((string key, object value) in ControlValues)
		{
			if (target.Contains(key) && ReferenceEquals(target[key], value))
			{
				target.Remove(key);
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

	// What the application scope held under each key before the overlay replaced it (Absent: the key was not set at
	// that level), so leaving Dark rest puts back exactly what was there.
	private static readonly object Absent = new object();

	private static readonly Dictionary<string, object> AppSaved = new Dictionary<string, object>(StringComparer.Ordinal);

	// White glyphs drawn from pictures or fixed vectors (the 8.1 arrows, icon pictures) are shown at this opacity in Dark
	// rest, which puts their white at the soft ink's brightness.
	public const double GlyphOpacity = 0.86;

	private static readonly object RestGlyphOpacity = GlyphOpacity;

	// The application-scope overlay: every window's Metro controls, menus, tooltips and scrollbars take the Dark rest
	// family. Written after ShellTheme.WriteTokens, each time the tokens are written while Dark rest is on. UI thread.
	public static void WriteAppResources(ResourceDictionary app)
	{
		foreach (KeyValuePair<string, Brush> pair in TokenBrushes)
		{
			Save(app, pair.Key, pair.Value);
			app[pair.Key] = pair.Value;
		}
		if (ScrollBarStyle() is Style scroll)
		{
			Save(app, "Metro81.ScrollBarDark", scroll);
			app["Metro81.ScrollBarDark"] = scroll;
		}
		Save(app, "Metro81.GlyphOpacity", RestGlyphOpacity);
		app["Metro81.GlyphOpacity"] = RestGlyphOpacity;
		foreach ((string key, object value) in ControlValues)
		{
			Save(app, key, value);
			app[key] = value;
		}
	}

	private static void Save(ResourceDictionary app, string key, object rest)
	{
		if (!app.Contains(key))
		{
			if (!AppSaved.ContainsKey(key))
			{
				AppSaved[key] = Absent;
			}
			return;
		}
		object current = app[key];
		if (!ReferenceEquals(current, rest))
		{
			AppSaved[key] = current;
		}
	}

	// Puts back what WriteAppResources replaced, for every key that still holds the Dark rest value (a key rewritten
	// meanwhile, for example by ShellTheme.WriteTokens or by Start's accent refresh, is left as it is). UI thread.
	public static void RemoveAppResources(ResourceDictionary app)
	{
		foreach (KeyValuePair<string, Brush> pair in TokenBrushes)
		{
			Restore(app, pair.Key, pair.Value);
		}
		if (_scrollBarStyle != null)
		{
			Restore(app, "Metro81.ScrollBarDark", _scrollBarStyle);
		}
		Restore(app, "Metro81.GlyphOpacity", RestGlyphOpacity);
		foreach ((string key, object value) in ControlValues)
		{
			Restore(app, key, value);
		}
		AppSaved.Clear();
	}

	private static void Restore(ResourceDictionary app, string key, object rest)
	{
		if (!app.Contains(key) || !ReferenceEquals(app[key], rest))
		{
			return;
		}
		if (AppSaved.TryGetValue(key, out object? saved) && !ReferenceEquals(saved, Absent))
		{
			app[key] = saved;
		}
		else
		{
			app.Remove(key);
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

	// The quiet scrollbar style if it was built already (null otherwise), without building it.
	public static Style? BuiltScrollBarStyle => _scrollBarStyle;

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

	// Hand-built surfaces draw their text and glyphs in plain white. While Dark rest is on that white is swapped for the
	// soft ink (rest = true) and back again with Normal (rest = false). Only local values are touched: solid white (or a
	// Dark rest ink set earlier) becomes the soft ink, or the ink for accent fills where the soft ink would not keep
	// 4.5:1 for text (3:1 for glyphs) on the nearest opaque background; with Normal both inks become white again. Every
	// other colour, and anything set by a style, stays as it is. background: what root sits on.
	public static void RetintWhite(DependencyObject? root, bool rest, Color? background = null)
	{
		if (root == null)
		{
			return;
		}
		Retint(root, rest, background ?? PanelColor);
	}

	private static void Retint(DependencyObject d, bool rest, Color under)
	{
		Color bg = BackgroundOf(d, under);
		Swap(d, TextBlock.ForegroundProperty, rest, bg, 4.5);
		if (d is Shape)
		{
			Swap(d, Shape.FillProperty, rest, bg, 3.0);
			Swap(d, Shape.StrokeProperty, rest, bg, 3.0);
		}
		else if (d is Image img)
		{
			// icon pictures (mostly white glyphs) at the soft ink's brightness, and back
			if (rest && img.Opacity == 1.0)
			{
				img.Opacity = GlyphOpacity;
			}
			else if (!rest && img.Opacity == GlyphOpacity)
			{
				img.Opacity = 1.0;
			}
		}
		foreach (object child in LogicalTreeHelper.GetChildren(d))
		{
			if (child is DependencyObject c)
			{
				Retint(c, rest, bg);
			}
		}
	}

	private static Color BackgroundOf(DependencyObject d, Color under)
	{
		Brush? b = d switch
		{
			Border x => x.Background,
			Panel p => p.Background,
			Control c => c.Background,
			_ => null
		};
		if (b is SolidColorBrush s && s.Color.A >= 0x80)
		{
			double a = s.Color.A / 255.0;
			return Color.FromRgb((byte)Math.Round(s.Color.R * a + under.R * (1 - a)), (byte)Math.Round(s.Color.G * a + under.G * (1 - a)), (byte)Math.Round(s.Color.B * a + under.B * (1 - a)));
		}
		return under;
	}

	private static void Swap(DependencyObject d, DependencyProperty dp, bool rest, Color bg, double min)
	{
		object v = d.ReadLocalValue(dp);
		bool ours = ReferenceEquals(v, Ink) || ReferenceEquals(v, InkOnAccent);
		if (rest)
		{
			if (ours || (v is SolidColorBrush b && b.Color == Colors.White))
			{
				Brush want = Contrast(InkColor, bg) >= min ? Ink : InkOnAccent;
				if (!ReferenceEquals(v, want))
				{
					d.SetValue(dp, want);
				}
			}
		}
		else if (ours)
		{
			d.SetValue(dp, Brushes.White);
		}
	}

	// A tile colour toned down for the dark panel: the hue stays recognisable, the saturation drops to about a third
	// and the lightness sits in a narrow dark band. For example Wi-Fi #00A0E7 becomes #325767.
	public static Color Mute(Color c)
	{
		ColorMath.RgbToHsl(c.R / 255.0, c.G / 255.0, c.B / 255.0, out double h, out double s, out double l);
		return FromHsl(h, s * 0.35, Math.Clamp(l, 0.24, 0.30), c.A);
	}

	// A Start tile colour toned down for the black board while the tiles are dimmed: the hue stays, the saturation drops
	// to 40% (0.38 at most) and the lightness is held at 0.27 or below, so no tile is a vivid block (chroma 52 or less,
	// close to the muted Action Center tiles) and the soft ink of the tile names keeps 4.5:1. For example #00A300
	// becomes #2B5F2B.
	public static Color MuteTile(Color c)
	{
		ColorMath.RgbToHsl(c.R / 255.0, c.G / 255.0, c.B / 255.0, out double h, out double s, out double l);
		return FromHsl(h, Math.Min(s * 0.40, 0.38), Math.Min(l, 0.27), c.A);
	}

	private static readonly Dictionary<Color, Brush> MutedTiles = new Dictionary<Color, Brush>();

	// The muted tile colour as a shared frozen brush.
	public static Brush MuteTileBrush(Color c)
	{
		lock (MutedTiles)
		{
			if (!MutedTiles.TryGetValue(c, out Brush? b))
			{
				b = Frozen(MuteTile(c));
				if (MutedTiles.Count > 512)
				{
					MutedTiles.Clear();
				}
				MutedTiles[c] = b;
			}
			return b;
		}
	}

	// The rest rule of SettingsPane.AccentToneColor. The darker tones are neutral (black family: scrims, cards, panes);
	// the lighter ones take the accent's hue at a low saturation (selected items, buttons, badges). For the preset
	// accent #44636A: 0.16 -> #050505, 0.28 -> #121212, 0.34 -> #0A0A0A, 0.46 -> #44636A, 0.50 -> #4E7179.
	public static Color AccentTone(Color accent, double lightness)
	{
		if (lightness < 0.20)
		{
			return C("#FF050505");
		}
		if (lightness < 0.31)
		{
			return CardColor;
		}
		if (lightness < 0.38)
		{
			return PanelColor;
		}
		ColorMath.RgbToHsl(accent.R / 255.0, accent.G / 255.0, accent.B / 255.0, out double h, out double s, out _);
		return FromHsl(h, Math.Clamp(s, 0.08, 0.22), lightness < 0.48 ? 0.34 : 0.39, 255);
	}

	// WCAG 2 contrast ratio of two opaque colours.
	public static double Contrast(Color a, Color b)
	{
		double la = ColorMath.RelLuminance(a);
		double lb = ColorMath.RelLuminance(b);
		return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
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
		return Frozen(C(hex));
	}

	private static Brush Frozen(Color c)
	{
		SolidColorBrush b = new SolidColorBrush(c);
		b.Freeze();
		return b;
	}
}

// An attached flag that a template sets from a dynamic resource (true while a Dark rest treatment applies), so a
// multi-binding converter that reads it runs again when Dark rest turns on or off. False (nothing changes) otherwise.
public static class RestFlag
{
	public static readonly DependencyProperty OnProperty = DependencyProperty.RegisterAttached("On", typeof(bool), typeof(RestFlag), new PropertyMetadata(false));

	public static bool GetOn(DependencyObject d)
	{
		return (bool)d.GetValue(OnProperty);
	}

	public static void SetOn(DependencyObject d, bool value)
	{
		d.SetValue(OnProperty, value);
	}
}

// Start tiles while Dark rest dims them: a solid tile colour is toned down (DarkRestPalette.MuteTile); any other fill,
// and every fill with the flag off, is passed through unchanged. values: the tile brush, the flag.
public sealed class RestTileFillConverter : IMultiValueConverter
{
	public object? Convert(object[] values, Type targetType, object? parameter, CultureInfo culture)
	{
		object? fill = values.Length > 0 ? values[0] : null;
		if (values.Length > 1 && values[1] is true && fill is SolidColorBrush solid)
		{
			return DarkRestPalette.MuteTileBrush(solid.Color);
		}
		return fill is Brush ? fill : null;
	}

	public object[] ConvertBack(object? value, Type[] targetTypes, object? parameter, CultureInfo culture)
	{
		throw new NotSupportedException();
	}
}

// The tile art of an 8.1 or Office 2013 override while Dark rest dims the tiles (the flag): DarkRestIcons.MutedArt with
// the brand colour; the art unchanged otherwise. values: the art, the brand brush, the flag.
public sealed class RestOverrideArtConverter : IMultiValueConverter
{
	public object? Convert(object[] values, Type targetType, object? parameter, CultureInfo culture)
	{
		object? art = values.Length > 0 ? values[0] : null;
		if (values.Length > 2 && values[2] is true && values[1] is SolidColorBrush brand && art is ImageSource img)
		{
			return DarkRestIcons.MutedArt(img, brand.Color);
		}
		return art is ImageSource ? art : null;
	}

	public object[] ConvertBack(object? value, Type[] targetTypes, object? parameter, CultureInfo culture)
	{
		throw new NotSupportedException();
	}
}

// The dimming scrim under a tile's icon: only for a fill that is not one solid colour (a picture), which cannot be
// toned down like a colour. Nothing otherwise. values: the tile brush, the flag.
public sealed class RestTilePlateScrimConverter : IMultiValueConverter
{
	public object? Convert(object[] values, Type targetType, object? parameter, CultureInfo culture)
	{
		bool on = values.Length > 1 && values[1] is true;
		return on && values[0] is Brush fill && fill is not SolidColorBrush ? DarkRestPalette.TileScrim : null;
	}

	public object[] ConvertBack(object? value, Type[] targetTypes, object? parameter, CultureInfo culture)
	{
		throw new NotSupportedException();
	}
}

// App icons on the black Dark rest surfaces. A dark silhouette is lifted the way the taskbar lifts it
// (AppInventory.EnsureVisibleOnDark); an opaque, mostly dark icon that fills its square (it carries no light content of
// its own, so it would melt into black) gets a thin outline in the control boundary grey traced around its shape. Any
// other icon is returned as it is. Results are cached per icon.
internal static class DarkRestIcons
{
	private static readonly ConditionalWeakTable<ImageSource, ImageSource> Cache = new ConditionalWeakTable<ImageSource, ImageSource>();

	public static ImageSource Visible(ImageSource img)
	{
		return Cache.GetValue(img, Make);
	}

	private static ImageSource Make(ImageSource source)
	{
		try
		{
			ImageSource lifted = AppInventory.EnsureVisibleOnDark(source) ?? source;
			if (!ReferenceEquals(lifted, source))
			{
				// the lifted silhouette in the soft ink instead of pure white
				return lifted is BitmapSource up ? Soften(up) : lifted;
			}
			if (source is not BitmapSource bmp)
			{
				return lifted;
			}
			return EdgeIfDarkFill(bmp) ?? LiftIfDarkGlyph(bmp) ?? source;
		}
		catch
		{
			return source;
		}
	}

	// Every colour scaled down so pure white becomes the soft ink (#DCDCDC); alpha and the shading are kept.
	private static BitmapSource Soften(BitmapSource bitmap)
	{
		BitmapSource bmp = bitmap.Format == PixelFormats.Bgra32 ? bitmap : new FormatConvertedBitmap(bitmap, PixelFormats.Bgra32, null, 0.0);
		int w = bmp.PixelWidth;
		int h = bmp.PixelHeight;
		int stride = w * 4;
		byte[] px = new byte[h * stride];
		bmp.CopyPixels(px, stride, 0);
		double k = DarkRestPalette.InkColor.R / 255.0;
		for (int i = 0; i < px.Length; i += 4)
		{
			px[i] = (byte)Math.Round(px[i] * k);
			px[i + 1] = (byte)Math.Round(px[i + 1] * k);
			px[i + 2] = (byte)Math.Round(px[i + 2] * k);
		}
		BitmapSource result = BitmapSource.Create(w, h, bitmap.DpiX > 0 ? bitmap.DpiX : 96.0, bitmap.DpiY > 0 ? bitmap.DpiY : 96.0, PixelFormats.Bgra32, null, px, stride);
		result.Freeze();
		return result;
	}

	// Keyed by the source picture; the muted copy itself is held weakly, so it goes away once no tile or row shows it
	// (before, it lived as long as the app's icon even with Metro Start never shown). A copy needed again is made by the
	// same per-pixel routine at the same size, so it is identical.
	private static readonly ConditionalWeakTable<ImageSource, WeakReference<ImageSource>> MutedArtCache = new ConditionalWeakTable<ImageSource, WeakReference<ImageSource>>();

	// The 8.1 and Office 2013 tile art (an opaque square in the brand colour with a white glyph) toned down with the
	// dimmed tile it sits on: the brand colour becomes DarkRestPalette.MuteTile(brand), white becomes the soft ink, and
	// the shades in between follow (hue kept, saturation reduced as MuteTile does), so the art blends into its muted
	// plate instead of standing out as a vivid square. Cached per picture; the picture itself when it cannot be read.
	public static ImageSource MutedArt(ImageSource art, Color brand)
	{
		WeakReference<ImageSource> slot = MutedArtCache.GetValue(art, static _ => new WeakReference<ImageSource>(null!));
		lock (slot)
		{
			if (slot.TryGetTarget(out ImageSource? muted) && muted != null)
			{
				return muted;
			}
			muted = MuteArt(art, brand);
			slot.SetTarget(muted);
			return muted;
		}
	}

	private static ImageSource MuteArt(ImageSource source, Color brand)
	{
		try
		{
			if (source is not BitmapSource bitmap)
			{
				return source;
			}
			BitmapSource bmp = bitmap.Format == PixelFormats.Bgra32 ? bitmap : new FormatConvertedBitmap(bitmap, PixelFormats.Bgra32, null, 0.0);
			int w = bmp.PixelWidth;
			int h = bmp.PixelHeight;
			if (w < 8 || h < 8 || w > 1024 || h > 1024)
			{
				return source;
			}
			int stride = w * 4;
			byte[] px = new byte[h * stride];
			bmp.CopyPixels(px, stride, 0);
			// Each pixel is read as a mix of the brand colour and white (t along that line) plus what is left over (shading
			// and other detail): the mix moves to the same mix of the plate and the soft ink, and the rest is kept at 40%.
			Color plate = DarkRestPalette.MuteTile(brand);
			Color ink = DarkRestPalette.InkColor;
			double wr = 255.0 - brand.R;
			double wg = 255.0 - brand.G;
			double wb = 255.0 - brand.B;
			double span = wr * wr + wg * wg + wb * wb;
			if (span < 1.0)
			{
				return source;
			}
			for (int i = 0; i < px.Length; i += 4)
			{
				if (px[i + 3] == 0)
				{
					continue;
				}
				double dr = px[i + 2] - brand.R;
				double dg = px[i + 1] - brand.G;
				double db = px[i] - brand.B;
				double t = Math.Clamp((dr * wr + dg * wg + db * wb) / span, 0.0, 1.0);
				double r = plate.R + (ink.R - plate.R) * t + 0.4 * (dr - wr * t);
				double g = plate.G + (ink.G - plate.G) * t + 0.4 * (dg - wg * t);
				double b = plate.B + (ink.B - plate.B) * t + 0.4 * (db - wb * t);
				px[i] = (byte)Math.Clamp((int)Math.Round(b), 0, 255);
				px[i + 1] = (byte)Math.Clamp((int)Math.Round(g), 0, 255);
				px[i + 2] = (byte)Math.Clamp((int)Math.Round(r), 0, 255);
			}
			BitmapSource result = BitmapSource.Create(w, h, bitmap.DpiX > 0 ? bitmap.DpiX : 96.0, bitmap.DpiY > 0 ? bitmap.DpiY : 96.0, PixelFormats.Bgra32, null, px, stride);
			result.Freeze();
			return result;
		}
		catch
		{
			return source;
		}
	}

	// A dark coloured glyph on a transparent ground (a navy or purple logo, for example), which neither rule above
	// catches: at least 4% of the square opaque, the opaque part's mean luminance under 0.03 and under 5% of it bright.
	// Its lightness is raised to about the middle (hue and alpha kept, saturation capped at 0.6), which puts it at 3:1
	// or more on black. Null when the icon is not one.
	internal static BitmapSource? LiftIfDarkGlyph(BitmapSource bitmap)
	{
		BitmapSource bmp = bitmap.Format == PixelFormats.Bgra32 ? bitmap : new FormatConvertedBitmap(bitmap, PixelFormats.Bgra32, null, 0.0);
		int w = bmp.PixelWidth;
		int h = bmp.PixelHeight;
		if (w < 8 || h < 8 || w > 512 || h > 512)
		{
			return null;
		}
		int stride = w * 4;
		byte[] px = new byte[h * stride];
		bmp.CopyPixels(px, stride, 0);
		int opaque = 0;
		int bright = 0;
		double sum = 0.0;
		for (int i = 0; i < px.Length; i += 4)
		{
			if (px[i + 3] < 128)
			{
				continue;
			}
			opaque++;
			double lum = ColorMath.RelLuminance(Color.FromRgb(px[i + 2], px[i + 1], px[i]));
			sum += lum;
			if (lum >= 0.18)
			{
				bright++;
			}
		}
		if (opaque * 25 < w * h || sum >= 0.03 * opaque || bright * 20 >= opaque)
		{
			return null;
		}
		byte[] outPx = (byte[])px.Clone();
		for (int i = 0; i < outPx.Length; i += 4)
		{
			if (outPx[i + 3] == 0)
			{
				continue;
			}
			ColorMath.RgbToHsl(outPx[i + 2] / 255.0, outPx[i + 1] / 255.0, outPx[i] / 255.0, out double hue, out double sat, out double light);
			ColorMath.HslToRgb(hue, Math.Min(sat, 0.6), 0.5 + Math.Min(light, 0.2) * 0.3, out double r, out double g, out double b);
			outPx[i] = B(b);
			outPx[i + 1] = B(g);
			outPx[i + 2] = B(r);
		}
		BitmapSource result = BitmapSource.Create(w, h, bitmap.DpiX > 0 ? bitmap.DpiX : 96.0, bitmap.DpiY > 0 ? bitmap.DpiY : 96.0, PixelFormats.Bgra32, null, outPx, stride);
		result.Freeze();
		return result;
	}

	private static byte B(double v)
	{
		return (byte)Math.Clamp((int)Math.Round(v * 255.0), 0, 255);
	}

	// The outline for an opaque dark icon (null when the icon is not one): at least half of its square opaque and at
	// least 60% of that under 3:1 against black.
	internal static BitmapSource? EdgeIfDarkFill(BitmapSource bitmap)
	{
		BitmapSource bmp = bitmap.Format == PixelFormats.Bgra32 ? bitmap : new FormatConvertedBitmap(bitmap, PixelFormats.Bgra32, null, 0.0);
		int w = bmp.PixelWidth;
		int h = bmp.PixelHeight;
		if (w < 8 || h < 8 || w > 512 || h > 512)
		{
			return null;
		}
		int stride = w * 4;
		byte[] px = new byte[h * stride];
		bmp.CopyPixels(px, stride, 0);
		int opaque = 0;
		int dark = 0;
		for (int i = 0; i < px.Length; i += 4)
		{
			if (px[i + 3] < 200)
			{
				continue;
			}
			opaque++;
			if (ColorMath.RelLuminance(Color.FromRgb(px[i + 2], px[i + 1], px[i])) < 0.10)
			{
				dark++;
			}
		}
		if (opaque * 2 < w * h || dark * 10 < opaque * 6)
		{
			return null;
		}
		int t = Math.Max(1, (int)Math.Round(Math.Min(w, h) / 32.0));
		bool In(int x, int y)
		{
			return x >= 0 && y >= 0 && x < w && y < h && px[(y * w + x) * 4 + 3] >= 128;
		}
		byte[] outPx = (byte[])px.Clone();
		Color edge = DarkRestPalette.BoundaryColor;
		for (int y = 0; y < h; y++)
		{
			for (int x = 0; x < w; x++)
			{
				if (!In(x, y))
				{
					continue;
				}
				bool border = false;
				for (int dy = -t; dy <= t && !border; dy++)
				{
					for (int dx = -t; dx <= t; dx++)
					{
						if (!In(x + dx, y + dy))
						{
							border = true;
							break;
						}
					}
				}
				if (border)
				{
					int i = (y * w + x) * 4;
					outPx[i] = edge.B;
					outPx[i + 1] = edge.G;
					outPx[i + 2] = edge.R;
					outPx[i + 3] = 255;
				}
			}
		}
		BitmapSource result = BitmapSource.Create(w, h, bitmap.DpiX > 0 ? bitmap.DpiX : 96.0, bitmap.DpiY > 0 ? bitmap.DpiY : 96.0, PixelFormats.Bgra32, null, outPx, stride);
		result.Freeze();
		return result;
	}
}

// Taskbar and tray icons while Dark rest is on (the flag): DarkRestIcons.Visible; the icon unchanged otherwise.
// values: the icon, the flag.
public sealed class RestIconEdgeConverter : IMultiValueConverter
{
	public object? Convert(object[] values, Type targetType, object? parameter, CultureInfo culture)
	{
		object? icon = values.Length > 0 ? values[0] : null;
		if (values.Length > 1 && values[1] is true && icon is ImageSource img)
		{
			return DarkRestIcons.Visible(img);
		}
		return icon is ImageSource ? icon : null;
	}

	public object[] ConvertBack(object? value, Type[] targetTypes, object? parameter, CultureInfo culture)
	{
		throw new NotSupportedException();
	}
}
