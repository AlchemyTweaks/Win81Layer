using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace Win81Layer;

// Data-driven desktop-composition profile (spec §8). Built-ins style launcher-owned windows only. The legacy
// GlobalAccentColorization field is still deserialized for profile compatibility, but it is forcibly disabled:
// ColorPrevalence changes every application's non-client frame and leaves 1-2 px edge artifacts in maximized
// Chromium/Electron windows. StyleForeignWindows is ALSO force-clamped to false in Resolve (2026-09-07, user
// request): the launcher must never write DWM caption/border/corner attributes into OTHER apps' windows - that
// cross-process styling (and the maximize hooks it arms) is what produced the left-edge line on maximized
// Chromium windows. Every foreign app now stays 100% native; only the launcher's OWN windows are themed. A user
// may override any other built-in field by dropping %LocalAppData%\Win81Layer\composition\<id>.json.
public sealed class CompositionProfile
{
    public string Id { get; set; } = "native";

    public string Name { get; set; } = "Windows Native";

    public string Description { get; set; } = "";

    public string Corners { get; set; } = "system";        // system | square | round

    public string TitleBar { get; set; } = "system";       // system | dark | accent  ("color" is accepted as an alias of accent)

    public bool GlobalAccentColorization { get; set; }     // legacy input; Resolve always clamps this to false

    public string Backdrop { get; set; } = "none";         // none | mica | acrylic (Win11 22H2 caption tint)

    public bool DisableWindowTransitions { get; set; }     // DWMWA_TRANSITIONS_FORCEDISABLED (flat feel)

    public bool DisableNcRendering { get; set; }           // DWMWA_NCRENDERING_POLICY=DISABLED on foreign windows (flatter 8.1 frame — the dwm-basic technique)

    public string OwnedFrameStyle { get; set; } = "win81"; // win81 | aero | native — AUTHORITATIVE owned-window frame look (read via ShellSkin.OwnedAeroOn → Win81Window)

    public bool ShellGlass { get; set; }                   // translucent glass on the launcher's own SHELL surfaces (taskbar/flyouts/panels) — the Win7 Aero look

    public bool ExternalAeroGlass { get; set; }            // legacy override field; built-ins keep this false and the bridge is compile-time disabled

    public bool StyleForeignWindows { get; set; }          // when true, the composition sweep + foreground hook apply the FLAT 8.1 caption/text/border/square-corners to OTHER apps' top-level windows too (cross-process DwmSetWindowAttribute, no injection). Only the authentic 8.1 profile sets this; reverts to native when the mode is left.

    public string FidelityNote { get; set; } = "";         // honest note surfaced in the settings UI

    public bool IsNative => string.Equals(Id, "native", StringComparison.OrdinalIgnoreCase);
}

public static class CompositionProfiles
{
    private static readonly JsonSerializerOptions Json = new JsonSerializerOptions
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    // Built-in profiles (validated values). Legacy modes "Accent"/"Dark" are mapped by Resolve() for back-compat.
    private static readonly Dictionary<string, CompositionProfile> BuiltIn = new Dictionary<string, CompositionProfile>(StringComparer.OrdinalIgnoreCase)
    {
        ["native"] = new CompositionProfile
        {
            Id = "native",
            Name = "Windows Native",
            Description = "The system's own composition — no overrides.",
            Corners = "system",
            TitleBar = "system",
            GlobalAccentColorization = false,
            Backdrop = "none",
            OwnedFrameStyle = "win81",
            FidelityNote = ""
        },
        ["windows81"] = new CompositionProfile
        {
            Id = "windows81",
            Name = "Windows 8.1 (Authentic)",
            Description = "Flat, clean, opaque — the authentic 8.1 composition.",
            Corners = "square",
            TitleBar = "accent",
            GlobalAccentColorization = false,
            Backdrop = "none",
			// Windows 8.1 still used DWM non-client rendering and transitions. Disabling either globally makes modern
			// applications repaint poorly and forces a foreground-window hook on Windows 10, with no fidelity benefit.
			DisableWindowTransitions = false,
			DisableNcRendering = false,
			OwnedFrameStyle = "win81",
			// Re-enabled 2026-09-05 (user): apply the authentic flat 8.1 chrome (light silver caption, dark title,
			// thin grey border, SQUARE corners) to EVERY app window via cross-process DWM attributes — verified on
			// this Win11 build. No injection; fully reverts to native when the mode is left.
			StyleForeignWindows = true,
			FidelityNote = "Authentic flat 8.1 window chrome (light caption, square corners, thin border) applied to all windows via documented DWM attributes. Chromium/Electron apps that draw their own title bar keep it. Fully reversible."
        },
        ["windows7-aero"] = new CompositionProfile
        {
            Id = "windows7-aero",
            Name = "Windows 7 Aero (Authentic)",
            Description = "Windows 7 geometry, native caption controls and real Aero blur-behind glass when the DWM bridge is available.",
            Corners = "round",
            TitleBar = "accent",
            GlobalAccentColorization = false,
            // SYSTEMBACKDROP acrylic is a Windows 11 material, not Windows 7 Aero. The external bridge supplies
            // authentic Aero; the owned-window renderer supplies a deterministic lightweight fallback.
            Backdrop = "none",
            DisableWindowTransitions = false,
            OwnedFrameStyle = "aero",
            ShellGlass = true,
            ExternalAeroGlass = false,   // DWMBlurGlass removed (rendered buggy) — Win7 Aero uses the launcher's OWN owned-frame renderer, no external DWM hook
			FidelityNote = "Owned windows use a 30 px DPI-aware Aero frame with 8 px sizing borders and Windows 7-size caption buttons. Other applications keep their native DWM geometry. No external DWM hook is used."
        },
        ["alchemy-enhanced"] = new CompositionProfile
        {
            Id = "alchemy-enhanced",
            Name = "Alchemy Enhanced",
            Description = "Alchemy's own composition — inspired by 7 and 8.1, refined for modern Windows.",
            Corners = "round",
            TitleBar = "accent",
            GlobalAccentColorization = false,
            Backdrop = "mica",
            DisableWindowTransitions = false,
            OwnedFrameStyle = "aero",
            ShellGlass = true,
            FidelityNote = "A distinct Alchemy look (NOT an Authentic mode): cleaner Mica/Acrylic backdrop, translucent glass tints, high-DPI, accent-aware, high-refresh-friendly."
        }
    };

    public static IEnumerable<string> Ids => BuiltIn.Keys;

    // The EXACTLY FOUR user-facing composition modes, in display order. Single source of truth — both the tray
    // menu and the Desktop Composition settings page enumerate this so they can never drift.
    public static readonly (string Label, string Id)[] MenuModes = new (string, string)[4]
    {
        ("Windows Native", "native"),
        ("Windows 7 Aero (Authentic)", "windows7-aero"),
        ("Windows 8.1 (Authentic)", "windows81"),
        ("Alchemy Enhanced", "alchemy-enhanced")
    };

    // Resolve a mode string (new profile id OR legacy "Accent"/"Dark"/off) to a profile, merging any
    // user JSON override on top of the built-in.
    public static CompositionProfile Resolve(string mode)
    {
        string id = MapLegacy(mode);
        CompositionProfile p = (BuiltIn.TryGetValue(id, out CompositionProfile bi) ? Clone(bi) : Clone(BuiltIn["native"]));
        try
        {
            string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Win81Layer", "composition", id + ".json");
            if (File.Exists(path))
            {
                CompositionProfile ov = JsonSerializer.Deserialize<CompositionProfile>(File.ReadAllText(path), Json);
                if (ov != null)
                {
                    ov.Id = id;
                    // Foreign application frames are a hard ownership boundary. Older overrides may contain
                    // GlobalAccentColorization=true; never let that legacy value re-enable system-wide borders.
                    ov.GlobalAccentColorization = false;
                    ov.StyleForeignWindows = false;   // 2026-09-07 (user): never restyle another app's DWM chrome - the cross-process caption/border/corner writes (and the maximize hooks they arm) are what drew the edge line on maximized Chromium windows. Owned launcher windows are styled separately and are unaffected. Reversible: delete this clamp to restore the 8.1 foreign-frame look.
                    return ov;   // a full override replaces the built-in (simplest, predictable)
                }
            }
        }
        catch
        {
        }
        p.StyleForeignWindows = false;   // 2026-09-07 (user): keep every foreign window native - see the clamp note above
        return p;
    }

    private static string MapLegacy(string mode)
    {
        if (string.IsNullOrEmpty(mode))
        {
            return "native";
        }
        return mode.ToLowerInvariant() switch
        {
            "off" or "none" or "native" or "windows native" => "native",
            "accent" => "windows81",                        // the old "Accent" mode was essentially the flat 8.1 look
            "windows7-aero-enhanced" => "alchemy-enhanced", // CRITICAL: migrate existing Enhanced users to the renamed id
            "dark" => "native",                             // retired "dark" profile → clean native fallback
            _ => (BuiltIn.ContainsKey(mode) ? mode : "native"),
        };
    }

    private static CompositionProfile Clone(CompositionProfile s)
    {
        return new CompositionProfile
        {
            Id = s.Id,
            Name = s.Name,
            Description = s.Description,
            Corners = s.Corners,
            TitleBar = s.TitleBar,
            GlobalAccentColorization = s.GlobalAccentColorization,
            Backdrop = s.Backdrop,
            DisableWindowTransitions = s.DisableWindowTransitions,
            DisableNcRendering = s.DisableNcRendering,
            OwnedFrameStyle = s.OwnedFrameStyle,
            ShellGlass = s.ShellGlass,
            ExternalAeroGlass = s.ExternalAeroGlass,
            StyleForeignWindows = s.StyleForeignWindows,
            FidelityNote = s.FidelityNote
        };
    }
}
