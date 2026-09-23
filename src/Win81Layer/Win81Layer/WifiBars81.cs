using System;
using System.Windows.Media;

namespace Win81Layer;

internal static class WifiBars81
{
	public static DrawingImage Draw(int bars, Color color)
	{
		return (DrawingImage)NetIcons81.Draw(NetworkIconKind.Wifi, NetworkIconState.Connected, Math.Clamp(bars, 0, 4), 32, color);
	}
}
