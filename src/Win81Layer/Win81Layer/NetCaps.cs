using System;
using System.Net.NetworkInformation;

namespace Win81Layer;

// Hardware capability probe for the network UI. The launcher runs on both a DESKTOP (often Ethernet-only) and a
// LAPTOP, and the rule is: only ever surface network states the machine can actually have — a radio-less desktop
// must never show Wi-Fi / cellular / airplane affordances. Presence is decided from the real adapter list
// (NetworkInterface), so a Wi-Fi card counts even while it is turned off or disconnected. FAIL-OPEN: if the probe
// throws we report everything present, so a transient error hides nothing.
internal static class NetCaps
{
	private static readonly object Gate = new object();

	private static bool _cached;

	private static Caps _caps;

	private readonly record struct Caps(bool Wifi, bool Ethernet, bool Cellular, bool Radios);

	public static bool HasWifi => Read().Wifi;

	public static bool HasEthernet => Read().Ethernet;

	public static bool HasCellular => Read().Cellular;

	// A machine has "radios" (and therefore a meaningful airplane mode) when it has Wi-Fi or cellular hardware.
	public static bool RadiosPresent => Read().Radios;

	// Adapters can be added/removed (USB Wi-Fi, docks) — call on NetworkChange to re-probe on next read.
	public static void Invalidate()
	{
		lock (Gate)
		{
			_cached = false;
		}
	}

	private static Caps Read()
	{
		lock (Gate)
		{
			if (_cached)
			{
				return _caps;
			}
			_caps = Probe();
			_cached = true;
			return _caps;
		}
	}

	private static Caps Probe()
	{
		bool wifi = false;
		bool ethernet = false;
		bool cellular = false;
		try
		{
			foreach (NetworkInterface ni in NetworkInterface.GetAllNetworkInterfaces())
			{
				if (IsVirtualAdapter(ni))
				{
					continue;
				}
				switch (ni.NetworkInterfaceType)
				{
					case NetworkInterfaceType.Wireless80211:
						wifi = true;
						break;
					case NetworkInterfaceType.Ethernet:
					case NetworkInterfaceType.GigabitEthernet:
					case NetworkInterfaceType.FastEthernetT:
					case NetworkInterfaceType.FastEthernetFx:
						ethernet = true;
						break;
					case NetworkInterfaceType.Wwanpp:
					case NetworkInterfaceType.Wwanpp2:
						cellular = true;
						break;
				}
			}
		}
		catch
		{
			// Fail-open: never hide an affordance because the probe failed.
			return new Caps(true, true, false, true);
		}
		return new Caps(wifi, ethernet, cellular, wifi || cellular);
	}

	// Same virtual-adapter filter used by NetInfo/NetStatus so a VMware/Hyper-V/VPN NIC is never mistaken for real
	// Wi-Fi/Ethernet hardware.
	private static bool IsVirtualAdapter(NetworkInterface ni)
	{
		string d = ((ni.Description ?? string.Empty) + " " + (ni.Name ?? string.Empty)).ToLowerInvariant();
		return d.Contains("virtual") || d.Contains("vmware") || d.Contains("hyper-v") || d.Contains("vethernet")
			|| d.Contains("virtualbox") || d.Contains("vpn") || d.Contains("tap-windows") || d.Contains("tap adapter")
			|| d.Contains("wsl") || d.Contains("docker") || d.Contains("loopback") || d.Contains("pseudo")
			|| d.Contains("npcap") || d.Contains("wan miniport") || d.Contains("bluetooth");
	}
}
