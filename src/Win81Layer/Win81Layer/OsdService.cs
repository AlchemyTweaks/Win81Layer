using System;

namespace Win81Layer;

public static class OsdService
{
	private static OsdWindow? _osd;

	private static OsdWindow Instance => _osd ?? (_osd = new OsdWindow());

	public static void ShowVolume(int pct, bool muted)
	{
		try
		{
			Instance.ShowVolume(pct, muted);
		}
		catch (Exception ex)
		{
			Logger.Log("OSD volume: " + ex.Message);
		}
	}

	public static void ShowBrightness(int pct)
	{
		try
		{
			Instance.ShowBrightness(pct);
		}
		catch (Exception ex)
		{
			Logger.Log("OSD brightness: " + ex.Message);
		}
	}
}
