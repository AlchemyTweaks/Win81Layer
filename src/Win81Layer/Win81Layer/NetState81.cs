using System;
using Microsoft.Win32;
using Windows.Networking.Connectivity;

namespace Win81Layer;

public sealed record NetState81(NetKind Kind, string Label, int Bars, bool Internet, NetworkIconState IconState = NetworkIconState.Auto)
{
	public NetworkIconState EffectiveIconState => IconState != NetworkIconState.Auto
		? IconState
		: Kind switch
		{
			NetKind.Offline => NetworkIconState.Offline,
			NetKind.Airplane => NetworkIconState.Airplane,
			_ => Internet ? NetworkIconState.Connected : NetworkIconState.Limited
		};

	public bool Connected
	{
		get
		{
			if (EffectiveIconState is NetworkIconState.NotConnected or NetworkIconState.CableUnplugged
				or NetworkIconState.Offline or NetworkIconState.Airplane or NetworkIconState.Disabled
				or NetworkIconState.HardwareOff or NetworkIconState.NetworkError)
			{
				return false;
			}
			NetKind kind = Kind;
			if ((uint)kind <= 2u)
			{
				return true;
			}
			return false;
		}
	}

	public string Status
	{
		get
		{
			NetKind kind = Kind;
			if (1 == 0)
			{
			}
			string result = EffectiveIconState switch
			{
				NetworkIconState.Airplane => "Airplane mode",
				NetworkIconState.CaptivePortal => "Sign-in required",
				NetworkIconState.NoInternet => "No Internet access",
				NetworkIconState.Limited => "Limited",
				NetworkIconState.Metered => "Connected (metered)",
				NetworkIconState.Connecting => "Connecting",
				NetworkIconState.Scanning => "Scanning",
				NetworkIconState.Identifying => "Identifying network",
				NetworkIconState.NotConnected or NetworkIconState.CableUnplugged or NetworkIconState.Offline
					or NetworkIconState.Disabled or NetworkIconState.HardwareOff or NetworkIconState.NetworkError => "Not connected",
				_ => Internet ? "Connected" : "Limited"
			};
			if (1 == 0)
			{
			}
			return result;
		}
	}

	public static NetState81 Read()
	{
		try
		{
			ConnectionProfile p = NetworkInformation.GetInternetConnectionProfile();
			if ((object)p == null)
			{
				return IsAirplaneMode() ? new NetState81(NetKind.Airplane, "Airplane mode", 0, Internet: false) : new NetState81(NetKind.Offline, "Not connected", 0, Internet: false);
			}
			NetworkConnectivityLevel connectivity = p.GetNetworkConnectivityLevel();
			bool internet = connectivity == NetworkConnectivityLevel.InternetAccess;
			NetworkIconState iconState = ResolveIconState(p, connectivity);
			if (p.IsWlanConnectionProfile)
			{
				string ssid = "";
				try
				{
					ssid = p.WlanConnectionProfileDetails?.GetConnectedSsid() ?? "";
				}
				catch
				{
				}
				int bars = 0;
				try
				{
					bars = p.GetSignalBars().GetValueOrDefault();
				}
				catch
				{
				}
				return new NetState81(NetKind.Wifi, string.IsNullOrWhiteSpace(ssid) ? "Wi-Fi" : ssid, Math.Clamp(bars, 0, 5), internet, iconState);
			}
			if (p.IsWwanConnectionProfile)
			{
				int bars2 = 0;
				try
				{
					bars2 = p.GetSignalBars().GetValueOrDefault();
				}
				catch
				{
				}
				string name = "";
				try
				{
					name = p.ProfileName ?? "";
				}
				catch
				{
				}
				return new NetState81(NetKind.Cellular, string.IsNullOrWhiteSpace(name) ? "Cellular" : name, Math.Clamp(bars2, 0, 5), internet, iconState);
			}
			return new NetState81(NetKind.Ethernet, "Ethernet", 0, internet, iconState);
		}
		catch
		{
			return new NetState81(NetKind.Offline, "Not connected", 0, Internet: false);
		}
	}

	private static NetworkIconState ResolveIconState(ConnectionProfile profile, NetworkConnectivityLevel connectivity)
	{
		if (connectivity == NetworkConnectivityLevel.ConstrainedInternetAccess)
		{
			return NetworkIconState.CaptivePortal;
		}
		if (connectivity == NetworkConnectivityLevel.LocalAccess)
		{
			return NetworkIconState.NoInternet;
		}
		if (connectivity != NetworkConnectivityLevel.InternetAccess)
		{
			return NetworkIconState.Limited;
		}
		try
		{
			ConnectionCost cost = profile.GetConnectionCost();
			if (cost.NetworkCostType is NetworkCostType.Fixed or NetworkCostType.Variable)
			{
				return cost.Roaming ? NetworkIconState.Roaming : NetworkIconState.Metered;
			}
		}
		catch
		{
		}
		return NetworkIconState.Connected;
	}

	private static bool IsAirplaneMode()
	{
		try
		{
			using RegistryKey k = Registry.LocalMachine.OpenSubKey("SYSTEM\\CurrentControlSet\\Control\\RadioManagement");
			object value = k?.GetValue("SystemRadioState");
			return value is int i && i != 0;
		}
		catch
		{
			return false;
		}
	}
}
