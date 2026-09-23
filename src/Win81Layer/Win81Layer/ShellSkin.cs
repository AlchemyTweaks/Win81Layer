using System.Windows;

namespace Win81Layer;

// The launcher's OWN shell skin, driven by the desktop-composition profile (Win8.1 flat ↔ Win7 Aero glass).
// Applies an acrylic-blur backdrop to the launcher's fixed-position panels when the active profile has ShellGlass.
// Fixed-position surfaces only (taskbar/flyouts/charms) — never movable windows (WCA acrylic drag-lags on Win10).
internal static class ShellSkin
{
    public static bool GlassOn
    {
        get
        {
            try
            {
                // The "Transparency effects" toggle on the Desktop Composition page can force all shell glass off.
                if (!SettingsStore.Load().DeskCompTransparency)
                {
                    return false;
                }
                // Preview must skin the launcher's surfaces with the mode that is actually active, not the
                // persisted mode that will boot later.
                return CompositionProfiles.Resolve(DesktopComposition.EffectiveMode).ShellGlass;
            }
            catch
            {
                return false;
            }
        }
    }

    // Owned-WINDOW frame look (Win81Window glossy-Aero vs flat-8.1), driven by the profile's purpose-built
    // OwnedFrameStyle field — the single authoritative source for owned frames (NOT ShellGlass, which is for the
    // taskbar/flyout surfaces). Same DeskCompTransparency gate. Uses EffectiveMode so a live Preview re-skins owned
    // windows in lockstep with Win81Window.ApplyOwnedCorner (which also reads EffectiveMode). On the 4 built-ins
    // OwnedFrameStyle=="aero" ⟺ ShellGlass, so defaults are unchanged; a JSON override can now decouple them.
    public static bool OwnedAeroOn
    {
        get
        {
            try
            {
                if (!SettingsStore.Load().DeskCompTransparency)
                {
                    return false;
                }
                return CompositionProfiles.Resolve(DesktopComposition.EffectiveMode).OwnedFrameStyle == "aero";
            }
            catch
            {
                return false;
            }
        }
    }

    // Retained for call-site compatibility. The external bridge is compile-time disabled, so owned windows
    // always use the deterministic cached frame selected by OwnedAeroOn.
    public static bool NativeOwnedAeroOn
    {
        get
        {
            try
            {
                if (!OwnedAeroOn)
                {
                    return false;
                }
                CompositionProfile profile = CompositionProfiles.Resolve(DesktopComposition.EffectiveMode);
                return profile.ExternalAeroGlass && DwmBlurGlassBridge.AeroEnabled;
            }
            catch
            {
                return false;
            }
        }
    }

    // Shared panel background — the same choke-point ActionCenter/Search/Settings/Charms use. Glass => translucent
    // smoked accent (alpha 0xB0), flat => opaque accent. Route any other launcher panel/flyout through this.
    public static System.Windows.Media.Brush PanelBg()
    {
        return SettingsPane.PaneBg();
    }

    // Shared card background — a touch darker than a panel (search entity/answer/calc cards, scope popups).
    // Glass => translucent (alpha 0xB0), flat => opaque.
    public static System.Windows.Media.Brush CardBg()
    {
        System.Windows.Media.Color c = SettingsPane.AccentToneColor(GlassOn ? 0.26 : 0.28);
        byte a = (byte)(GlassOn ? 0xD8 : 0xFF);   // Aero cards kept readable (~85% opaque), not near-transparent
        return new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(a, c.R, c.G, c.B));
    }

    // Convenience accessors for template/code sites that want the raw values.
    public static System.Windows.Media.Color AccentTone(double lightness)
    {
        return SettingsPane.AccentToneColor(lightness);
    }

    // The Win7-Aero glass look is delivered by a translucent WPF background (see SettingsPane.PaneBg / per-panel
    // backgrounds), NOT by a DWM blur region. We deliberately do NOT use WCA acrylic here: the undocumented
    // ACCENT_ENABLE_ACRYLICBLURBEHIND backdrop leaks a stuck compositor rectangle that survives the owning window
    // (and even the whole process) dying on some GPUs — a ghost that only a DWM restart clears. A plain alpha
    // window is torn down cleanly by DWM and can never leave a ghost. This call now only defensively clears any
    // stale accent region a previous build may have left on the HWND.
    public static void ApplyGlass(Window w)
    {
        if (w == null)
        {
            return;
        }
        try
        {
            nint hwnd = new System.Windows.Interop.WindowInteropHelper(w).Handle;
            AcrylicGlass.Clear(hwnd);
        }
        catch
        {
        }
    }
}
