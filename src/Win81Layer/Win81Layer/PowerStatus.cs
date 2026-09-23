using System.Runtime.InteropServices;

namespace Win81Layer;

internal static class PowerStatus
{
	private struct SYSTEM_POWER_STATUS
	{
		public byte ACLineStatus;

		public byte BatteryFlag;

		public byte BatteryLifePercent;

		public byte SystemStatusFlag;

		public int BatteryLifeTime;

		public int BatteryFullLifeTime;
	}

	[DllImport("kernel32.dll")]
	private static extern bool GetSystemPowerStatus(out SYSTEM_POWER_STATUS status);

	public static (bool present, int percent, bool charging, bool saver) Read()
	{
		if (!GetSystemPowerStatus(out var s))
		{
			return (present: false, percent: 0, charging: false, saver: false);
		}
		// Presence: the 0x80 "no system battery" bit is authoritative ONLY when the flag is not the 0xFF "unknown"
		// sentinel — some laptop ECs transiently report BatteryFlag=0xFF at boot / resume-from-sleep (which has 0x80
		// set) and that must NOT make a real battery's icon vanish. A valid percent means a battery is present.
		bool hasPct = s.BatteryLifePercent != byte.MaxValue;
		bool noBattery = s.BatteryFlag != byte.MaxValue && (s.BatteryFlag & 0x80) != 0;
		bool present = hasPct && !noBattery;
		int pct = (hasPct ? s.BatteryLifePercent : 0);
		// Charging = the REAL charging bit (0x08), not merely "AC line online" — a full or charge-limited battery on AC
		// is not charging and must not show a permanent charging bolt.
		bool charging = (s.BatteryFlag & 0x08) != 0;
		bool saver = (s.SystemStatusFlag & 1) != 0;
		return (present: present, percent: pct, charging: charging, saver: saver);
	}

	// Estimated seconds of battery life remaining, or -1 if unknown (Windows reports -1, or the 0xFFFFFFFF sentinel,
	// while still estimating or when on AC). Kept separate from Read() so its tuple shape (used in many places) is
	// untouched. Best-effort: any failure returns -1.
	public static int SecondsLeft()
	{
		try
		{
			if (GetSystemPowerStatus(out var s) && s.BatteryLifeTime > 0)
			{
				return s.BatteryLifeTime;
			}
		}
		catch
		{
		}
		return -1;
	}
}
