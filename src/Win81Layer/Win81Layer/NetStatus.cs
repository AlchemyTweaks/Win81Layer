using System;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;

namespace Win81Layer;

internal static class NetStatus
{
	public static (bool up, bool wifi, bool internet) Read()
	{
		try
		{
			if (!NetworkInterface.GetIsNetworkAvailable())
			{
				return (false, false, false);
			}
			// Pick the adapter that actually ROUTES to the internet (has a real default gateway), NOT just the first Up
			// one — a VMware/Hyper-V/VPN/WSL virtual Ethernet is often enumerated first and would make a Wi-Fi laptop
			// wrongly show the WIRED glyph. Skip known-virtual adapters, and among real Wi-Fi/Ethernet adapters prefer
			// the one with a default gateway; fall back to the first real adapter if none advertises a gateway yet.
			NetworkInterface withGateway = null;
			NetworkInterface firstReal = null;
			foreach (NetworkInterface ni in NetworkInterface.GetAllNetworkInterfaces())
			{
				if (ni.OperationalStatus != OperationalStatus.Up)
				{
					continue;
				}
				NetworkInterfaceType t = ni.NetworkInterfaceType;
				if (t != NetworkInterfaceType.Wireless80211 && t != NetworkInterfaceType.Ethernet && t != NetworkInterfaceType.GigabitEthernet)
				{
					continue;   // ignore loopback, tunnel, PPP, etc.
				}
				if (IsVirtualAdapter(ni))
				{
					continue;
				}
				firstReal ??= ni;
				if (withGateway == null && HasDefaultGateway(ni))
				{
					withGateway = ni;
				}
			}
			NetworkInterface chosen = withGateway ?? firstReal;
			if (chosen == null)
			{
				return (false, false, false);
			}
			bool wifi = chosen.NetworkInterfaceType == NetworkInterfaceType.Wireless80211;
			return (true, wifi, HasInternet());
		}
		catch
		{
			return (false, false, false);
		}
	}

	private static bool IsVirtualAdapter(NetworkInterface ni)
	{
		string d = ((ni.Description ?? "") + " " + (ni.Name ?? "")).ToLowerInvariant();
		return d.Contains("virtual") || d.Contains("vmware") || d.Contains("hyper-v") || d.Contains("vethernet") || d.Contains("virtualbox") || d.Contains("vpn") || d.Contains("tap-windows") || d.Contains("tap adapter") || d.Contains("wsl") || d.Contains("docker") || d.Contains("loopback") || d.Contains("pseudo") || d.Contains("npcap") || d.Contains("wan miniport") || d.Contains("bluetooth");
	}

	private static bool HasDefaultGateway(NetworkInterface ni)
	{
		try
		{
			foreach (GatewayIPAddressInformation g in ni.GetIPProperties().GatewayAddresses)
			{
				System.Net.IPAddress a = g?.Address;
				if (a != null && a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork && !a.Equals(System.Net.IPAddress.Any))
				{
					return true;
				}
			}
		}
		catch
		{
		}
		return false;
	}

	private static readonly Guid CLSID_NetworkListManager = new Guid("DCB00C01-570F-4A9B-8D69-199FDBA5723B");

	// NLM INetworkListManager.IsConnectedToInternet — exactly what real Windows uses for the "no internet" tray
	// state (cached, no network round-trip). Late-bound via IDispatch. Fails OPEN (true) so a COM/policy hiccup
	// never falsely shows "no internet".
	private static bool HasInternet()
	{
		object nlm = null;
		try
		{
			Type t = Type.GetTypeFromCLSID(CLSID_NetworkListManager);
			if (t == null)
			{
				return true;
			}
			nlm = Activator.CreateInstance(t);
			object v = t.InvokeMember("IsConnectedToInternet", System.Reflection.BindingFlags.GetProperty, null, nlm, null);
			return v is bool b ? b : true;
		}
		catch
		{
			return true;
		}
		finally
		{
			if (nlm != null && Marshal.IsComObject(nlm))
			{
				Marshal.ReleaseComObject(nlm);
			}
		}
	}
}
