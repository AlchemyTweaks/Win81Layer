using System;
using System.Runtime.InteropServices;
using System.Windows.Threading;

namespace Win81Layer;

// Aero Peek — "peek at the desktop": hovering the show-desktop sliver makes all windows fade to transparent glass
// outlines revealing the desktop, then restores on leave. This drives the DWM's OWN live-preview (dwmapi.dll
// ordinal 113 — the exact mechanism the Windows shell uses for Aero Peek). It only ACTIVATES a transient preview;
// it does NOT replace the compositor, inject, or create any persistent region (nothing to leak like WCA acrylic did).
// The entry point is undocumented, so: every call is guarded, a signature failure DISABLES it for the session, and a
// hard safety timer force-restores — the screen can never get stuck peeked.
internal static class DwmPeek
{
    // Win8+/Win10 signature (5 args). The user's build is Win10, so this matches the live export.
    [DllImport("dwmapi.dll", EntryPoint = "#113", PreserveSig = false)]
    private static extern void DwmpActivateLivePreview(int fActivate, IntPtr hExcludeHwnd, IntPtr hInsertBeforeHwnd, int lpt, IntPtr prcFinalRect);

    private static bool _disabled;

    private static bool _active;

    private static DispatcherTimer _safety;

    public static void Peek(nint excludeHwnd)
    {
        if (_disabled || _active)
        {
            return;
        }
        try
        {
            DwmpActivateLivePreview(1, excludeHwnd, IntPtr.Zero, 1, IntPtr.Zero);
            _active = true;
            if (_safety == null)
            {
                _safety = new DispatcherTimer
                {
                    Interval = TimeSpan.FromSeconds(6.0)
                };
                _safety.Tick += delegate
                {
                    Unpeek();
                };
            }
            _safety.Stop();
            _safety.Start();
        }
        catch (Exception ex)
        {
            _disabled = true;   // ordinal missing / bad signature on this build → never try again this session
            _active = false;
            Logger.Log("DwmPeek disabled: " + ex.Message);
        }
    }

    public static void Unpeek()
    {
        try
        {
            _safety?.Stop();
        }
        catch
        {
        }
        if (!_active)
        {
            return;
        }
        _active = false;
        try
        {
            DwmpActivateLivePreview(0, IntPtr.Zero, IntPtr.Zero, 1, IntPtr.Zero);
        }
        catch
        {
        }
    }
}
