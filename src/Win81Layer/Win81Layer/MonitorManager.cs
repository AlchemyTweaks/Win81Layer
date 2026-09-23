using System.Drawing;
using System.Windows.Forms;

namespace Win81Layer;

internal static class MonitorManager
{
	public static Rectangle? NeighborWorkArea(Rectangle from, bool left)
	{
		int fromCx = from.Left + from.Width / 2;
		Rectangle? best = null;
		int bestDx = int.MaxValue;
		Screen[] allScreens = Screen.AllScreens;
		foreach (Screen s in allScreens)
		{
			Rectangle wa = TaskbarWorkArea.Current(s);
			int cx = wa.Left + wa.Width / 2;
			int dx = (left ? (fromCx - cx) : (cx - fromCx));
			if (dx > 0 && dx < bestDx)
			{
				bestDx = dx;
				best = wa;
			}
		}
		return best;
	}
}
