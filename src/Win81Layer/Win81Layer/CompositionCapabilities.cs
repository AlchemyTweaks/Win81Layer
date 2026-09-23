using System;
using System.Runtime.InteropServices;

namespace Win81Layer;

// Composition capability detector (spec §11): determines which DOCUMENTED modern mechanisms are available
// on THIS Win10/11 build so a composition profile applies only supported effects and falls back gracefully
// (§12). Uses RtlGetVersion for the true build number (Environment.OSVersion can be manifest-capped). Cached.
internal static class CompositionCapabilities
{
    private static bool _init;
    private static int _build;

    public static int Build
    {
        get { Ensure(); return _build; }
    }

    public static bool IsWin11
    {
        get { Ensure(); return _build >= 22000; }
    }

    public static bool IsWin10
    {
        get { Ensure(); return _build >= 10240 && _build < 22000; }
    }

    // DWMWA_USE_IMMERSIVE_DARK_MODE (20) is reliable from Win10 1809 (17763).
    public static bool SupportsDarkTitleBar
    {
        get { Ensure(); return _build >= 17763; }
    }

    // DWMWA_BORDER_COLOR(34)/CAPTION_COLOR(35)/TEXT_COLOR(36) + WINDOW_CORNER_PREFERENCE(33): Win11 22000+.
    // On Win10 these attrs are silent no-ops, so gate on this before relying on them.
    public static bool SupportsFrameColors
    {
        get { Ensure(); return _build >= 22000; }
    }

    public static bool SupportsCornerPreference => SupportsFrameColors;

    // The WORKING DWMWA_USE_IMMERSIVE_DARK_MODE attribute index is 19 on 1809-1903 (17763-18362) and 20 from
    // 19041+ (validated 2026-08-27). Using 20 on 1809-1903 silently no-ops.
    public static int DarkModeAttr
    {
        get { Ensure(); return (_build >= 19041) ? 20 : 19; }
    }

    // DWMWA_SYSTEMBACKDROP_TYPE (38) — Mica/Acrylic/Tabbed: Win11 22H2 (22621+).
    public static bool SupportsSystemBackdrop
    {
        get { Ensure(); return _build >= 22621; }
    }

    // DWMWA_TRANSITIONS_FORCEDISABLED (3) — available since Vista; used to flatten window open/min/max feel.
    public static bool SupportsTransitionToggle
    {
        get { Ensure(); return _build >= 10240; }
    }

    public static bool CompositionEnabled
    {
        get
        {
            try
            {
                return DwmIsCompositionEnabled(out bool e) == 0 && e;
            }
            catch
            {
                return true;   // modern Windows always composites; assume yes on failure
            }
        }
    }

    public static string Describe()
    {
        Ensure();
        return $"build={_build} win11={IsWin11} darkTB={SupportsDarkTitleBar} frameColors={SupportsFrameColors} backdrop={SupportsSystemBackdrop} transitions={SupportsTransitionToggle} dwmComposition={CompositionEnabled}";
    }

    private static void Ensure()
    {
        if (_init)
        {
            return;
        }
        _init = true;
        _build = 0;
        try
        {
            RTL_OSVERSIONINFOW v = default(RTL_OSVERSIONINFOW);
            v.dwOSVersionInfoSize = (uint)Marshal.SizeOf<RTL_OSVERSIONINFOW>();
            if (RtlGetVersion(ref v) == 0)
            {
                _build = (int)v.dwBuildNumber;
            }
        }
        catch
        {
        }
        if (_build == 0)
        {
            try
            {
                _build = Environment.OSVersion.Version.Build;
            }
            catch
            {
            }
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct RTL_OSVERSIONINFOW
    {
        public uint dwOSVersionInfoSize;

        public uint dwMajorVersion;

        public uint dwMinorVersion;

        public uint dwBuildNumber;

        public uint dwPlatformId;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string szCSDVersion;
    }

    [DllImport("ntdll.dll")]
    private static extern int RtlGetVersion(ref RTL_OSVERSIONINFOW lpVersionInformation);

    [DllImport("dwmapi.dll")]
    private static extern int DwmIsCompositionEnabled(out bool enabled);
}
