using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;

#nullable enable annotations

namespace Win81Layer;

// Windows 7 Start menu palette. Build() returns one ResourceDictionary of frozen W7.* brushes for a theme; the menu keeps
// exactly one of them as its merged dictionary and every element reads it through a resource reference, so a light/dark
// or colour change is a single dictionary swap with no rebuild. The glass is tinted from the colour family the launcher
// taskbar is painted from (TaskbarTheme.SourceAccent), and a contrast guard keeps white glass text readable over a pure
// white wallpaper, the streak highlight included.
internal static class Win7Palette
{
    // Glass gradient stops: offset, light alpha, light L, dark alpha, dark L.
    private static readonly (double Offset, byte LightA, double LightL, byte DarkA, double DarkL)[] GlassStops =
    {
        (0.0, 0xD9, 0.30, 0xE6, 0.22),
        (0.5, 0xE0, 0.25, 0xEB, 0.15),
        (1.0, 0xE8, 0.19, 0xF0, 0.10),
    };

    // List pane colours (W7.PaneBg); the icon classifier tests icons against both.
    internal static readonly Color PaneLight = Color.FromRgb(0xFF, 0xFF, 0xFF);

    internal static readonly Color PaneDark = Color.FromRgb(0x1F, 0x1F, 0x1F);

    internal const byte StreakPeakLight = 0x1A;

    internal const byte StreakPeakDark = 0x0D;

    // Painted shadow rings, innermost first (1 to 6 DIP outside the glass on the top and right).
    internal const int ShadowRings = 6;

    private static readonly byte[] ShadowAlphaLight = { 0x30, 0x20, 0x14, 0x0C, 0x06, 0x03 };

    private static readonly byte[] ShadowAlphaDark = { 0x40, 0x2C, 0x1C, 0x10, 0x08, 0x04 };

    // Hue and saturation of the glass in 0..1 units. A near-grey source gives a cool neutral glass.
    internal static (double h, double s) GlassHueSat(Color src, bool dark)
    {
        ColorMath.RgbToHsl(src.R / 255.0, src.G / 255.0, src.B / 255.0, out double h, out double s, out _);
        double sL;
        if (s < 0.06)
        {
            h = 210.0 / 360.0;
            sL = 0.10;
        }
        else
        {
            sL = Math.Clamp(s, 0.12, 0.60);
        }
        return (h, dark ? 0.8 * sL : sL);
    }

    internal static Color Tone(double h01, double s, double l)
    {
        ColorMath.HslToRgb(h01, Math.Clamp(s, 0.0, 1.0), Math.Clamp(l, 0.0, 1.0), out double r, out double g, out double b);
        return Color.FromRgb(B(r * 255.0), B(g * 255.0), B(b * 255.0));
    }

    private static byte B(double v)
    {
        return (byte)Math.Clamp(Math.Round(v, MidpointRounding.AwayFromZero), 0.0, 255.0);
    }

    // WCAG contrast ratio of two opaque colours.
    internal static double Contrast(Color a, Color b)
    {
        double la = ColorMath.RelLuminance(a);
        double lb = ColorMath.RelLuminance(b);
        return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
    }

    // Source-over of a translucent colour on an opaque one.
    internal static Color Over(Color top, Color under)
    {
        double a = top.A / 255.0;
        return Color.FromRgb(B(top.R * a + under.R * (1.0 - a)), B(top.G * a + under.G * (1.0 - a)), B(top.B * a + under.B * (1.0 - a)));
    }

    // The glass stop colours (offset order) after the contrast guard. With transparency off every stop is opaque.
    internal static Color[] GlassStopColors(Color source, bool dark, bool transparency, out int darkenSteps, out double guardContrast)
    {
        (double h, double s) = GlassHueSat(source, dark);
        double[] l = new double[GlassStops.Length];
        for (int i = 0; i < l.Length; i++)
        {
            l[i] = dark ? GlassStops[i].DarkL : GlassStops[i].LightL;
        }
        double target = dark ? 7.0 : 4.5;
        byte peak = dark ? StreakPeakDark : StreakPeakLight;
        darkenSteps = 0;
        Color[] stops;
        while (true)
        {
            stops = new Color[l.Length];
            for (int i = 0; i < l.Length; i++)
            {
                Color t = Tone(h, s, l[i]);
                byte a = transparency ? (dark ? GlassStops[i].DarkA : GlassStops[i].LightA) : (byte)0xFF;
                stops[i] = Color.FromArgb(a, t.R, t.G, t.B);
            }
            // Worst case behind right-column text: stop 0 over a white wallpaper, plus the streak peak on top. The sheen
            // ends above the first link, so it is not part of it.
            Color composite = Over(Color.FromArgb(peak, 0xFF, 0xFF, 0xFF), Over(stops[0], Colors.White));
            guardContrast = Contrast(Colors.White, composite);
            if (guardContrast >= target || darkenSteps >= 20 || l[0] <= 0.04)
            {
                break;
            }
            for (int i = 0; i < l.Length; i++)
            {
                l[i] = Math.Max(0.04, l[i] - 0.02);
            }
            darkenSteps++;
        }
        return stops;
    }

    internal static ResourceDictionary Build(bool dark, Color glassSource, bool transparency)
    {
        ResourceDictionary d = new ResourceDictionary();
        (double h, double s) = GlassHueSat(glassSource, dark);
        (_, double sL) = GlassHueSat(glassSource, dark: false);

        // Glass
        Color[] stops = GlassStopColors(glassSource, dark, transparency, out _, out _);
        d["W7.GlassTint"] = Grad(V0, V1, (stops[0], 0.0), (stops[1], 0.5), (stops[2], 1.0));
        Color sheen = dark ? H("#1AFFFFFF") : H("#38FFFFFF");
        LinearGradientBrush sheenBrush = new LinearGradientBrush
        {
            MappingMode = BrushMappingMode.Absolute,
            StartPoint = new Point(0.0, 0.0),
            EndPoint = new Point(0.0, 48.0)
        };
        sheenBrush.GradientStops.Add(new GradientStop(sheen, 0.0));
        sheenBrush.GradientStops.Add(new GradientStop(H("#00FFFFFF"), 1.0));
        sheenBrush.Freeze();
        d["W7.GlassSheen"] = sheenBrush;
        byte p1 = dark ? (byte)0x0D : (byte)0x1A;
        byte p2 = dark ? (byte)0x09 : (byte)0x12;
        d["W7.GlassStreak"] = Grad(new Point(0.0, 0.0), new Point(1.0, 0.6),
            (W(0x00), 0.28), (W(p1), 0.34), (W(0x00), 0.40), (W(0x00), 0.62), (W(p2), 0.70), (W(0x00), 0.78));
        d["W7.FrameOuter"] = S(dark ? "#CC000000" : "#8C000000");
        d["W7.FrameInner"] = Grad(V0, V1, (H(dark ? "#26FFFFFF" : "#59FFFFFF"), 0.0), (H(dark ? "#0AFFFFFF" : "#1FFFFFFF"), 1.0));
        for (int i = 0; i < ShadowRings; i++)
        {
            d["W7.Shadow" + (i + 1)] = F(Color.FromArgb(dark ? ShadowAlphaDark[i] : ShadowAlphaLight[i], 0, 0, 0));
        }

        // List pane
        d["W7.PaneBg"] = F(dark ? PaneDark : PaneLight);
        d["W7.PaneBorder"] = S(dark ? "#3B4049" : "#8FA2BA");
        d["W7.PaneGlow"] = S(dark ? "#00FFFFFF" : "#40FFFFFF");
        d["W7.Ink"] = S(dark ? "#F2F2F2" : "#000000");
        d["W7.Ink2"] = S(dark ? "#A8A8A8" : "#6D6D6D");
        Color sep = H(dark ? "#3A3F47" : "#C9D3DF");
        d["W7.Sep"] = Grad(H0, H1, (Color.FromArgb(0, sep.R, sep.G, sep.B), 0.0), (sep, 0.15), (sep, 0.85), (Color.FromArgb(0, sep.R, sep.G, sep.B), 1.0));
        d["W7.Arrow"] = S(dark ? "#C8C8C8" : "#333333");
        d["W7.HotFill"] = Grad(V0, V1, (H(dark ? "#2A3F58" : "#DCEBFC"), 0.0), (H(dark ? "#22344A" : "#C1DBFC"), 1.0));
        d["W7.HotStroke"] = S(dark ? "#3D6A9E" : "#7DA2CE");
        d["W7.HotInner"] = S(dark ? "#14FFFFFF" : "#80FFFFFF");
        d["W7.PressFill"] = Grad(V0, V1, (H(dark ? "#1E3047" : "#C1DBFC"), 0.0), (H(dark ? "#192838" : "#A6C9F3"), 1.0));
        d["W7.PressStroke"] = S(dark ? "#4A7BB3" : "#6E92C2");
        d["W7.GroupHeaderInk"] = S(dark ? "#9CC3EA" : "#1E395B");
        d["W7.GroupRule"] = S(dark ? "#3A3F47" : "#D6DEE8");
        d["W7.IconPlate"] = F(Tone(h, s, dark ? 0.36 : 0.42));
        d["W7.IconPlateLight"] = S("#D6D6D6");
        // Pale multi-colour icons on the light pane: a quiet slate, not the accent.
        d["W7.IconPlateNeutral"] = S(dark ? "#D6D6D6" : "#8C96A3");

        // Search box
        d["W7.SearchBg"] = S(dark ? "#2A2A2A" : "#FFFFFF");
        d["W7.SearchBorder"] = S(dark ? "#5A5A5A" : "#ABADB3");
        d["W7.SearchBorderFocus"] = S(dark ? "#4C8BD6" : "#3D7BAD");
        d["W7.SearchInk"] = S(dark ? "#F2F2F2" : "#000000");
        d["W7.SearchHint"] = S(dark ? "#9A9A9A" : "#6D6D6D");
        d["W7.SearchCaret"] = S(dark ? "#F2F2F2" : "#000000");
        d["W7.SearchSelection"] = S(dark ? "#3D7BAD" : "#3399FF");
        d["W7.SelectionOpacity"] = dark ? 0.5 : 0.4;
        d["W7.SearchGlyph"] = S(dark ? "#9CC3EA" : "#3B6FA5");

        // Right column
        d["W7.GlassInk"] = S("#FFFFFF");
        d["W7.GlassInkShadow"] = S(dark ? "#80000000" : "#66000000");
        d["W7.GlassHotFill"] = Grad(V0, V1, (H(dark ? "#24FFFFFF" : "#38FFFFFF"), 0.0), (H(dark ? "#0DFFFFFF" : "#14FFFFFF"), 1.0));
        d["W7.GlassHotStroke"] = S(dark ? "#40FFFFFF" : "#66FFFFFF");
        d["W7.GlassHotInner"] = S(dark ? "#14FFFFFF" : "#26FFFFFF");
        d["W7.GlassPressFill"] = S(dark ? "#33000000" : "#26000000");
        d["W7.GlassPressStroke"] = S(dark ? "#40FFFFFF" : "#59FFFFFF");
        d["W7.GlassSep"] = Grad(H0, H1, (H("#00FFFFFF"), 0.0), (H(dark ? "#30FFFFFF" : "#40FFFFFF"), 0.5), (H("#00FFFFFF"), 1.0));
        // The etch fades with the highlight above it, so the groove has soft ends.
        d["W7.GlassSepEtch"] = Grad(H0, H1, (H("#00000000"), 0.0), (H(dark ? "#33000000" : "#26000000"), 0.5), (H("#00000000"), 1.0));
        d["W7.PicFrameFill"] = Grad(V0, V1, (H(dark ? "#5D6B7C" : "#F9FBFE"), 0.0), (H(dark ? "#323C49" : "#C9D9EB"), 1.0));
        d["W7.PicFrameStroke"] = S(dark ? "#B3000000" : "#80000000");
        d["W7.PicFrameInner"] = S(dark ? "#40FFFFFF" : "#B3FFFFFF");
        d["W7.PicInnerBorder"] = S("#66FFFFFF");
        d["W7.PicFallbackBg"] = F(dark ? Tone(h, s, 0.30) : Tone(h, 0.8 * sL, 0.50));

        // Shut down
        d["W7.PowerFill"] = dark
            ? Grad(V0, V1, (H("#32FFFFFF"), 0.0), (H("#16FFFFFF"), 0.49), (H("#07000000"), 0.50), (H("#1AFFFFFF"), 1.0))
            : Grad(V0, V1, (H("#47FFFFFF"), 0.0), (H("#1FFFFFFF"), 0.49), (H("#0A000000"), 0.50), (H("#26FFFFFF"), 1.0));
        d["W7.PowerStroke"] = S(dark ? "#CC000000" : "#99000000");
        d["W7.PowerInner"] = S(dark ? "#33FFFFFF" : "#4DFFFFFF");
        d["W7.PowerHotFill"] = Grad(V0, V1, (H(dark ? "#592F6EA8" : "#597FC4FF"), 0.0), (H(dark ? "#80224F7A" : "#802F7DD1"), 1.0));
        d["W7.PowerHotStroke"] = S(dark ? "#CC000000" : "#B3000000");
        d["W7.PowerPressFill"] = S(dark ? "#99163F66" : "#991D5FA8");
        d["W7.PowerDivDark"] = S(dark ? "#80000000" : "#66000000");
        d["W7.PowerDivLight"] = S(dark ? "#1FFFFFFF" : "#33FFFFFF");
        d["W7.PowerInk"] = S("#FFFFFF");
        d["W7.PowerInkShadow"] = S("#80000000");

        // Scrollbar
        d["W7.ScrollTrackHot"] = S(dark ? "#14FFFFFF" : "#0D000000");
        d["W7.ScrollThumb"] = S(dark ? "#5C6670" : "#BAC4D0");
        d["W7.ScrollThumbHot"] = S(dark ? "#77828D" : "#98A6B6");
        d["W7.ScrollThumbDrag"] = S(dark ? "#8E99A4" : "#7D8EA2");

        // Menus
        d["W7.MenuBg"] = S(dark ? "#2B2B2B" : "#F5F5F5");
        d["W7.MenuBorder"] = S(dark ? "#5A5A5A" : "#959595");
        d["W7.MenuShadow1"] = S(dark ? "#4D000000" : "#26000000");
        d["W7.MenuShadow2"] = S(dark ? "#26000000" : "#10000000");
        d["W7.MenuGutter"] = S(dark ? "#262626" : "#F1F1F1");
        // Gutter line and separator are an etched groove in both themes: the darker line first, the lighter one after it.
        d["W7.MenuGutterLine"] = S(dark ? "#1E1E1E" : "#E2E3E3");
        d["W7.MenuGutterLine2"] = S(dark ? "#3A3A3A" : "#FFFFFF");
        d["W7.MenuInk"] = S(dark ? "#F2F2F2" : "#000000");
        d["W7.MenuInkDisabled"] = S(dark ? "#8A8A8A" : "#9A9A9A");
        d["W7.MenuHotFill"] = Grad(V0, V1, (H(dark ? "#66335C8C" : "#34C5EBFF"), 0.0), (H(dark ? "#662B4F7A" : "#3481D8FF"), 1.0));
        d["W7.MenuHotStroke"] = S(dark ? "#806FA8DC" : "#8071CBF1");
        d["W7.MenuHotInner"] = S(dark ? "#1FFFFFFF" : "#40FFFFFF");
        d["W7.MenuSep"] = S(dark ? "#1E1E1E" : "#E0E0E0");
        d["W7.MenuSep2"] = S(dark ? "#3F3F3F" : "#FFFFFF");

        // Tooltips
        d["W7.TipBg"] = dark ? S("#2B2B2B") : Grad(V0, V1, (H("#FFFFFF"), 0.0), (H("#E4E5F0"), 1.0));
        d["W7.TipBorder"] = S(dark ? "#5A5A5A" : "#767676");
        d["W7.TipInk"] = S(dark ? "#F2F2F2" : "#575757");
        d["W7.TipShadow"] = S("#20000000");
        return d;
    }

    // The keys a popup (menu, tooltip, the search box's edit menu) needs: popups are outside the window's resource scope.
    internal static void CopyPopupKeys(ResourceDictionary? from, ResourceDictionary to)
    {
        if (from == null)
        {
            return;
        }
        foreach (object key in from.Keys)
        {
            if (key is string k && (k.StartsWith("W7.Menu", StringComparison.Ordinal) || k.StartsWith("W7.Tip", StringComparison.Ordinal) || k == "W7.Ink" || k == "W7.Sep"))
            {
                to[k] = from[k];
            }
        }
    }

    // First colour of a solid brush, or the end colour of a gradient (the colour a check compares against).
    internal static Color EndColor(object? brush)
    {
        if (brush is SolidColorBrush sc)
        {
            return sc.Color;
        }
        if (brush is GradientBrush g && g.GradientStops.Count > 0)
        {
            return g.GradientStops[g.GradientStops.Count - 1].Color;
        }
        return Colors.Transparent;
    }

    internal static Color StartColor(object? brush)
    {
        if (brush is SolidColorBrush sc)
        {
            return sc.Color;
        }
        if (brush is GradientBrush g && g.GradientStops.Count > 0)
        {
            return g.GradientStops[0].Color;
        }
        return Colors.Transparent;
    }

    // Frozen brushes for an icon drawn in code (the open folder), in colours taken from the shell's own icon.
    internal static SolidColorBrush Solid(Color c)
    {
        return F(c);
    }

    internal static LinearGradientBrush Vertical(Color top, Color bottom)
    {
        return Grad(V0, V1, (top, 0.0), (bottom, 1.0));
    }

    private static readonly Point V0 = new Point(0.0, 0.0);

    private static readonly Point V1 = new Point(0.0, 1.0);

    private static readonly Point H0 = new Point(0.0, 0.0);

    private static readonly Point H1 = new Point(1.0, 0.0);

    private static Color H(string hex)
    {
        return (Color)ColorConverter.ConvertFromString(hex);
    }

    private static Color W(byte alpha)
    {
        return Color.FromArgb(alpha, 0xFF, 0xFF, 0xFF);
    }

    private static SolidColorBrush S(string hex)
    {
        return F(H(hex));
    }

    private static SolidColorBrush F(Color c)
    {
        SolidColorBrush b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }

    private static LinearGradientBrush Grad(Point start, Point end, params (Color C, double Offset)[] stops)
    {
        LinearGradientBrush b = new LinearGradientBrush
        {
            StartPoint = start,
            EndPoint = end
        };
        foreach ((Color c, double o) in stops)
        {
            b.GradientStops.Add(new GradientStop(c, o));
        }
        b.Freeze();
        return b;
    }
}

// Menu side of the palette: one merged dictionary, swapped whole on a theme or colour change. A hidden menu only marks
// itself dirty; the signature is re-checked on every show.
public sealed partial class Win7StartMenu
{
    private static readonly Color QaGlassSourceFixed = Color.FromRgb(0x3A, 0x6E, 0xA5);

    private ResourceDictionary? _paletteDict;

    private string? _paletteSig;

    private bool _paletteIsDark;

    // The last two dictionaries by signature, so flipping back and forth builds nothing.
    private readonly List<(string Sig, ResourceDictionary Dict)> _paletteCache = new List<(string Sig, ResourceDictionary Dict)>(2);

    // The glass source colour last read from the taskbar theme, and whether an idle read of it is queued.
    private Color? _lastGlassSource;

    private bool _accentWarmQueued;

    private string PaletteSignature(out bool dark, out Color source, out bool transparency)
    {
        dark = ShellTheme.IsDark;
        if (_qa && _qaGlassSource.HasValue)
        {
            source = _qaGlassSource.Value;
            transparency = true;
        }
        else
        {
            source = CurrentGlassSource();
            transparency = ReadEnableTransparency();
        }
        return (dark ? "dark" : "light") + $"|#{source.R:X2}{source.G:X2}{source.B:X2}|" + (transparency ? "t1" : "t0");
    }

    // The taskbar source colour without decoding the wallpaper on the open path. When the taskbar theme has no cached
    // wallpaper average (a wallpaper change no bar has repainted for yet), the previous colour is used and the decode
    // runs once at idle; the palette follows when it is done. Only the very first read may decode inline.
    private Color CurrentGlassSource()
    {
        try
        {
            if (TaskbarTheme.TryCachedSourceAccent(out Color cached))
            {
                _lastGlassSource = cached;
                return cached;
            }
            if (_lastGlassSource.HasValue)
            {
                QueueAccentWarm();
                return _lastGlassSource.Value;
            }
            Color c = TaskbarTheme.SourceAccent();
            _lastGlassSource = c;
            return c;
        }
        catch
        {
            return _lastGlassSource ?? QaGlassSourceFixed;
        }
    }

    // One idle-priority read of the source colour (it decodes the wallpaper when needed). A changed colour repaints an
    // open menu in place or marks a hidden one dirty. Nothing runs again until the next signal or show.
    private void QueueAccentWarm()
    {
        if (_accentWarmQueued || _closed)
        {
            return;
        }
        _accentWarmQueued = true;
        Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, (Action)delegate
        {
            _accentWarmQueued = false;
            if (_closed)
            {
                return;
            }
            Color c;
            try
            {
                c = TaskbarTheme.SourceAccent();
            }
            catch
            {
                return;
            }
            if (_lastGlassSource != c)
            {
                _lastGlassSource = c;
                OnThemeSignal();
            }
        });
    }

    // Rebuilds or reuses the dictionary when the theme, the glass colour or the transparency setting changed. The
    // signature is read once per show.
    private void EnsurePalette()
    {
        string sig = PaletteSignature(out bool dark, out Color source, out bool transparency);
        if (_paletteDict != null && !_paletteDirty && string.Equals(sig, _paletteSig, StringComparison.Ordinal))
        {
            return;
        }
        ApplyPalette(sig, dark, source, transparency);
    }

    private void ApplyPalette()
    {
        string sig = PaletteSignature(out bool dark, out Color source, out bool transparency);
        ApplyPalette(sig, dark, source, transparency);
    }

    // Swaps the current palette in: one merged dictionary, the search selection opacity, open popups and icon plates.
    private void ApplyPalette(string sig, bool dark, Color source, bool transparency)
    {
        ResourceDictionary? dict = null;
        for (int i = 0; i < _paletteCache.Count; i++)
        {
            if (string.Equals(_paletteCache[i].Sig, sig, StringComparison.Ordinal))
            {
                dict = _paletteCache[i].Dict;
                // Most recently used first, so a light/dark flip keeps both of its dictionaries.
                _paletteCache.RemoveAt(i);
                _paletteCache.Insert(0, (sig, dict));
                break;
            }
        }
        bool built = false;
        if (dict == null)
        {
            dict = Win7Palette.Build(dark, source, transparency);
            _paletteCache.Insert(0, (sig, dict));
            if (_paletteCache.Count > 2)
            {
                _paletteCache.RemoveAt(_paletteCache.Count - 1);
            }
            built = true;
        }
        bool changed = !ReferenceEquals(dict, _paletteDict);
        _paletteDict = dict;
        _paletteSig = sig;
        _paletteIsDark = dark;
        _paletteDirty = false;
        if (Resources.MergedDictionaries.Count == 0)
        {
            Resources.MergedDictionaries.Add(dict);
        }
        else if (!ReferenceEquals(Resources.MergedDictionaries[0], dict))
        {
            Resources.MergedDictionaries[0] = dict;
        }
        if (dict["W7.SelectionOpacity"] is double so)
        {
            _search.SelectionOpacity = so;
        }
        if (dict["W7.SearchSelection"] is Brush selection)
        {
            // A selection without keyboard focus (the edit menu is open) keeps the palette colour, not the system grey.
            _search.Resources[SystemColors.InactiveSelectionHighlightBrushKey] = selection;
        }
        ContextMenu? child = _openChild;
        if (child != null)
        {
            Win7Palette.CopyPopupKeys(dict, child.Resources);
        }
        if (_openTip != null)
        {
            Win7Palette.CopyPopupKeys(dict, _openTip.Resources);
        }
        if (changed)
        {
            RefreshPlates();
            Trace("Win7 Start: palette " + sig + (built ? " (built)" : " (cached)"));
        }
    }

    // Theme and accent signals. A hidden menu does no work beyond the flag; an open one repaints in place.
    private void OnThemeSignal(object? sender, EventArgs e)
    {
        OnThemeSignal();
    }

    // The taskbar colour changed (a wallpaper change clears the taskbar theme's cached average first). A hidden menu
    // also reads the new colour once at idle, so the next open does not decode the wallpaper.
    private void OnAccentSignal()
    {
        OnThemeSignal();
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke((Action)QueueAccentWarmIfHidden);
            return;
        }
        QueueAccentWarmIfHidden();
    }

    private void QueueAccentWarmIfHidden()
    {
        if (!_closed && !_qa && !IsVisible && _lastGlassSource.HasValue && !TaskbarTheme.TryCachedSourceAccent(out _))
        {
            QueueAccentWarm();
        }
    }

    private void OnThemeSignal()
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke((Action)OnThemeSignal);
            return;
        }
        if (_closed)
        {
            return;
        }
        if (IsVisible)
        {
            ApplyPalette();
        }
        else
        {
            _paletteDirty = true;
        }
    }

    // HKCU ...\Themes\Personalize EnableTransparency; a missing value means on.
    internal static bool ReadEnableTransparency()
    {
        try
        {
            using RegistryKey? k = Registry.CurrentUser.OpenSubKey("Software\\Microsoft\\Windows\\CurrentVersion\\Themes\\Personalize");
            object? v = k?.GetValue("EnableTransparency");
            return v is not int i || i != 0;
        }
        catch
        {
            return true;
        }
    }
}
