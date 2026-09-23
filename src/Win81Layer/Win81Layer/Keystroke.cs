using System;
using System.Runtime.InteropServices;

namespace Win81Layer;

internal static class Keystroke
{
	internal const byte VK_LWIN = 91;

	internal const byte VK_CONTROL = 17;

	internal const byte VK_K = 75;

	internal const byte VK_P = 80;

	private const uint KEYEVENTF_KEYUP = 2u;

	[DllImport("user32.dll")]
	private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, nuint dwExtraInfo);

	internal static void Chord(params byte[] vks)
	{
		try
		{
			foreach (byte vk in vks)
			{
				keybd_event(vk, 0, 0u, UIntPtr.Zero);
			}
			for (int i2 = vks.Length - 1; i2 >= 0; i2--)
			{
				keybd_event(vks[i2], 0, 2u, UIntPtr.Zero);
			}
		}
		catch (Exception ex)
		{
			Logger.Log("Keystroke: " + ex.Message);
		}
	}
}
