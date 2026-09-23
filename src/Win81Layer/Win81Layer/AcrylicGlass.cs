using System;
using System.Runtime.InteropServices;

namespace Win81Layer;

// CLEAR-ONLY accent utility. We deliberately do NOT expose an "apply acrylic" path any more:
// SetWindowCompositionAttribute with ACCENT_ENABLE_ACRYLICBLURBEHIND leaks a stuck DWM blur-behind rectangle
// that survives the owning window — and even the whole process — dying, leaving a ghost on the desktop that only
// a DWM restart clears (confirmed on this hardware). The launcher's "glass" is now a plain translucent WPF alpha
// brush, which DWM tears down cleanly. This class only ever DISABLES any accent region left on an HWND, so no
// future edit can reintroduce the ghost by calling an Apply helper.
internal static class AcrylicGlass
{
    [StructLayout(LayoutKind.Sequential)]
    private struct ACCENT_POLICY
    {
        public int AccentState;

        public int AccentFlags;

        public uint GradientColor;

        public int AnimationId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WINCOMPATTRDATA
    {
        public int Attribute;

        public IntPtr Data;

        public int SizeOfData;
    }

    private const int WCA_ACCENT_POLICY = 19;

    private const int ACCENT_DISABLED = 0;

    // Disable any accent/blur region on the window (idempotent, safe on a window that never had one).
    public static void Clear(nint hwnd)
    {
        if (hwnd == IntPtr.Zero)
        {
            return;
        }
        try
        {
            ACCENT_POLICY accent = default(ACCENT_POLICY);
            accent.AccentState = ACCENT_DISABLED;
            accent.AccentFlags = 0;
            accent.GradientColor = 0u;
            accent.AnimationId = 0;
            int size = Marshal.SizeOf(accent);
            IntPtr ptr = Marshal.AllocHGlobal(size);
            try
            {
                Marshal.StructureToPtr(accent, ptr, fDeleteOld: false);
                WINCOMPATTRDATA data = default(WINCOMPATTRDATA);
                data.Attribute = WCA_ACCENT_POLICY;
                data.Data = ptr;
                data.SizeOfData = size;
                SetWindowCompositionAttribute(hwnd, ref data);
            }
            finally
            {
                Marshal.FreeHGlobal(ptr);
            }
        }
        catch (Exception ex)
        {
            Logger.Log("AcrylicGlass.Clear: " + ex.Message);
        }
    }

    [DllImport("user32.dll")]
    private static extern int SetWindowCompositionAttribute(nint hwnd, ref WINCOMPATTRDATA data);
}
