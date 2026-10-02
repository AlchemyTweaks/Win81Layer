using System;
using System.Drawing;

namespace Win81Layer;

// Placement maths for the Windows 7 Start menu, in physical pixels. Pure (no WPF or Win32 calls), so the QA harness
// can check every edge, taskbar size and scale without a screen. The glass sits flush on the taskbar: on a bottom bar
// the window's bottom edge is the work area's bottom, X follows the Start button (left or centred taskbars) and the
// result is clamped to the monitor. OverhangDip is the transparent band above the glass and ShadowDip the painted
// shadow band on the right; both may hang outside the work area but never outside the monitor.
internal static class Win7Placement
{
    internal readonly record struct Input(Rectangle MonPx, Rectangle WorkPx, string Edge, Rectangle? StartBtnPx, double Scale, double Wdip, double Hdip, double OverhangDip, double ShadowDip);

    private static int R(double v)
    {
        return (int)Math.Round(v, MidpointRounding.AwayFromZero);
    }

    internal static (int Xpx, int Ypx, int Wpx, int Hpx) Compute(Input i)
    {
        double s = i.Scale > 0.0 ? i.Scale : 1.0;
        int wPx = R(i.Wdip * s);
        int hPx = R(i.Hdip * s);
        int ov = R(i.OverhangDip * s);
        int sh = R(i.ShadowDip * s);
        Rectangle mon = i.MonPx;
        Rectangle work = i.WorkPx;
        Rectangle? btn = i.StartBtnPx;
        int x;
        int y;
        switch (NormalizeEdge(i.Edge))
        {
        case "Top":
            x = btn?.Left ?? work.Left;
            y = work.Top - ov;
            break;
        case "Left":
            x = work.Left;
            y = (btn?.Top ?? work.Top) - ov;
            break;
        case "Right":
            x = work.Right - (wPx - sh);
            y = (btn?.Top ?? work.Top) - ov;
            break;
        default:
            x = btn?.Left ?? work.Left;
            y = work.Bottom - hPx;
            break;
        }
        x = Math.Clamp(x, mon.Left, Math.Max(mon.Left, mon.Right - (wPx - sh)));
        y = Math.Clamp(y, mon.Top, Math.Max(mon.Top, work.Bottom - hPx));
        return (x, y, wPx, hPx);
    }

    // The tallest window (overhang included) that fits the work area, with a 4 DIP margin.
    internal static double MaxWindowDip(Rectangle workPx, double scale)
    {
        return workPx.Height / (scale > 0.0 ? scale : 1.0) - 4.0;
    }

    // The taskbar edge implied by the gap between the monitor and its work area (Bottom when there is none).
    internal static string InferEdge(Rectangle mon, Rectangle work)
    {
        int bottom = mon.Bottom - work.Bottom;
        int top = work.Top - mon.Top;
        int left = work.Left - mon.Left;
        int right = mon.Right - work.Right;
        int max = Math.Max(Math.Max(bottom, top), Math.Max(left, right));
        if (max <= 0 || bottom == max)
        {
            return "Bottom";
        }
        if (top == max)
        {
            return "Top";
        }
        return left == max ? "Left" : "Right";
    }

    private static string NormalizeEdge(string edge)
    {
        if (string.Equals(edge, "Top", StringComparison.OrdinalIgnoreCase))
        {
            return "Top";
        }
        if (string.Equals(edge, "Left", StringComparison.OrdinalIgnoreCase))
        {
            return "Left";
        }
        if (string.Equals(edge, "Right", StringComparison.OrdinalIgnoreCase))
        {
            return "Right";
        }
        return "Bottom";
    }
}
