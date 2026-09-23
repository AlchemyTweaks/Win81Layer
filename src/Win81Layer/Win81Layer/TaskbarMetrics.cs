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
			int num = ((size == "Small") ? 13 : ((!(size == "Large")) ? 15 : 18));
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
