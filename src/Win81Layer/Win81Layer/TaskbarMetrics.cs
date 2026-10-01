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
			// Bumped tray glyph sizes (battery/wifi/volume/notifications + search/task-view/overflow/action-center) for
			// legibility: Small 13->15, Medium 15->18, Large 18->22. Still below the 24px app pins and crisp (VolBox/NetBox
			// request bigger native frames), so the bar stays aligned.
			int num = ((size == "Small") ? 15 : ((!(size == "Large")) ? 18 : 22));
			if (1 == 0)
			{
			}
			return num;
		}
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
