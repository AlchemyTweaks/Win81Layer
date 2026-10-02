namespace Win81Layer;

public static class TaskbarMetrics
{
	public static double BarHeight
	{
		get
		{
			string size = Size;
			if (1 == 0)
			{
			}
			int num = ((size == "Small") ? 30 : ((!(size == "Large")) ? 40 : 48));
			if (1 == 0)
			{
			}
			return num;
		}
	}

	public static double IconSize
	{
		get
		{
			string size = Size;
			if (1 == 0)
			{
			}
			int num = ((size == "Small") ? 18 : ((!(size == "Large")) ? 24 : 32));
			if (1 == 0)
			{
			}
			return num;
		}
	}

	public static double GlyphSize
	{
		get
		{
			string size = Size;
			if (1 == 0)
			{
			}
			// Bumped MDL2 glyph sizes (battery / overflow chevron / search / task-view / task-overflow, plus the volume and
			// Action Center glyph fallbacks) for legibility: Small 13->15, Medium 15->18, Large 18->22. The authentic tray
			// IMAGES no longer follow this (see TrayIconPx); only the volume-flyout speaker (TrayVm.VolBox) still keys off it.
			int num = ((size == "Small") ? 15 : ((!(size == "Large")) ? 18 : 22));
			if (1 == 0)
			{
			}
			return num;
		}
	}

	// System tray icons (network / volume / Action Center flag): the box is an AUTHENTIC native frame size so the 8.1 art is
	// drawn 1:1. DIP at 100% DPI: Small 16 / Medium 20 / Large 24 (pnidui, SndVolSSO and ActionCenter all ship 16/20/24/32).
	public static double TrayIconSize => Size switch
	{
		"Small" => 16.0,
		"Large" => 24.0,
		_ => 20.0
	};

	// PHYSICAL tray frame for a monitor at dpiScale: the candidate nearest TrayIconSize * dpiScale among the native frames
	// (16/20/24/32) and their exact pixel doubles (40/48/64); ties keep the smaller one (so 32 = native, never 16x2). The
	// frame is always drawn 1:1 (a double is baked as a 2x pixel copy), so the tray art is never resampled.
	// 100%: 16/20/24, 125%: 20/24/32, 150%: 24/32/32, 175%: 24/32/40, 200%: 32/40/48 (Small/Medium/Large).
	public static int TrayIconPx(double dpiScale)
	{
		int ideal = (int)System.Math.Round(TrayIconSize * System.Math.Max(0.5, dpiScale));
		int best = 16;
		int bestD = int.MaxValue;
		foreach (int c in new int[7] { 16, 20, 24, 32, 40, 48, 64 })
		{
			int d = System.Math.Abs(c - ideal);
			if (d < bestD)
			{
				best = c;
				bestD = d;
			}
		}
		return best;
	}

	// App tray-icon box (physical px) for a tray frame. The authentic frames carry a 1px dark keyline (2px when doubled)
	// that is invisible on the dark bar, so their VISIBLE size is frame - 2 keylines; full-bleed app icons use exactly
	// that at Medium/Large (18/22 at 100%) so both read the same size. Small keeps the frame size (16 at 100% = the app's
	// own 16px HICON, drawn 1:1 like real Windows 8.1).
	public static int TrayAppIconPx(int trayPx)
	{
		if (Size == "Small")
		{
			return trayPx;
		}
		return trayPx - ((trayPx > 32) ? 4 : 2);
	}

	// Slot (pitch + hover box) shared by every tray button and app icon, physical px: 1.5 x the tray frame (24/30/36 at
	// 100%). Always even, so the 1:1 tray frames and the app boxes centre on whole pixels.
	public static int TraySlotPx(int trayPx)
	{
		return (int)System.Math.Round(trayPx * 1.5);
	}

	public static double StartGlyphSize
	{
		get
		{
			return Size switch
			{
				"Small" => 18.0,
				"Large" => 28.0,
				_ => 24.0
			};
		}
	}

	private static string Size
	{
		get
		{
			try
			{
				return SettingsStore.Current.TaskbarSize;
			}
			catch
			{
				return "Medium";
			}
		}
	}
}
