using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace Win81Layer;

public static class NetInfo
{
	public sealed record LocalNet(string Type, string LocalIp, string Gateway, string Dns, string Adapter, string Speed);

	private static readonly HttpClient Http = new HttpClient
	{
		Timeout = TimeSpan.FromSeconds(4L)
	};

	private static readonly SemaphoreSlim PublicIpGate = new SemaphoreSlim(1, 1);

	private static string? _cachedPublicIp;

	private static DateTime _publicIpExpiresUtc;

	private static string FormatSpeed(long bitsPerSec)
	{
		if (bitsPerSec <= 0)
		{
			return "—";
		}
		double mbps = (double)bitsPerSec / 1000000.0;
		return (mbps >= 1000.0) ? $"{mbps / 1000.0:0.#} Gbps" : $"{mbps:0} Mbps";
	}

	public static LocalNet Local()
	{
		try
		{
			NetworkInterface? fallback = null;
			NetworkInterface? routed = null;
			foreach (NetworkInterface ni in NetworkInterface.GetAllNetworkInterfaces())
			{
				if (ni.OperationalStatus != OperationalStatus.Up)
				{
					continue;
				}
				NetworkInterfaceType networkInterfaceType = ni.NetworkInterfaceType;
				if (networkInterfaceType != NetworkInterfaceType.Wireless80211
					&& networkInterfaceType != NetworkInterfaceType.Ethernet
					&& networkInterfaceType != NetworkInterfaceType.GigabitEthernet)
				{
					continue;
				}
				if (IsVirtualAdapter(ni))
				{
					continue;
				}
				IPInterfaceProperties props = ni.GetIPProperties();
				bool hasIpv4 = props.UnicastAddresses.Any(address => address.Address.AddressFamily == AddressFamily.InterNetwork);
				if (!hasIpv4)
				{
					continue;
				}
				fallback ??= ni;
				if (props.GatewayAddresses.Any(gateway => gateway.Address.AddressFamily == AddressFamily.InterNetwork && !gateway.Address.Equals(IPAddress.Any)))
				{
					routed = ni;
					break;
				}
			}

			NetworkInterface? selected = routed ?? fallback;
			if (selected != null)
			{
				IPInterfaceProperties props = selected.GetIPProperties();
				string ip = props.UnicastAddresses.FirstOrDefault(address => address.Address.AddressFamily == AddressFamily.InterNetwork)?.Address.ToString() ?? "N/A";
				string gw = props.GatewayAddresses.FirstOrDefault(gateway => gateway.Address.AddressFamily == AddressFamily.InterNetwork)?.Address.ToString() ?? "N/A";
				string[] dnsAddresses = props.DnsAddresses.Where(address => address.AddressFamily == AddressFamily.InterNetwork).Take(2).Select(address => address.ToString()).ToArray();
				string dns = dnsAddresses.Length == 0 ? "N/A" : string.Join(", ", dnsAddresses);
				string type = selected.NetworkInterfaceType == NetworkInterfaceType.Wireless80211 ? "Wi-Fi" : "Ethernet";
				return new LocalNet(type, ip, gw, dns, selected.Name, FormatSpeed(selected.Speed));
			}
		}
		catch
		{
		}
		return new LocalNet("Offline", "N/A", "N/A", "N/A", "—", "—");
	}

	public static Task<string?> PublicIpAsync()
	{
		return PublicIpAsync(CancellationToken.None);
	}

	public static async Task<string?> PublicIpAsync(CancellationToken cancellationToken)
	{
		if (!string.IsNullOrWhiteSpace(_cachedPublicIp) && DateTime.UtcNow < _publicIpExpiresUtc)
		{
			return _cachedPublicIp;
		}
		await PublicIpGate.WaitAsync(cancellationToken);
		try
		{
			if (!string.IsNullOrWhiteSpace(_cachedPublicIp) && DateTime.UtcNow < _publicIpExpiresUtc)
			{
				return _cachedPublicIp;
			}
			string value = (await Http.GetStringAsync("https://api.ipify.org", cancellationToken)).Trim();
			if (!IPAddress.TryParse(value, out _))
			{
				return null;
			}
			_cachedPublicIp = value;
			_publicIpExpiresUtc = DateTime.UtcNow.AddMinutes(10.0);
			return value;
		}
		catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
		{
			throw;
		}
		catch
		{
			return null;
		}
		finally
		{
			PublicIpGate.Release();
		}
	}

	private static bool IsVirtualAdapter(NetworkInterface ni)
	{
		string description = ((ni.Description ?? string.Empty) + " " + (ni.Name ?? string.Empty)).ToLowerInvariant();
		return description.Contains("virtual")
			|| description.Contains("vmware")
			|| description.Contains("hyper-v")
			|| description.Contains("vethernet")
			|| description.Contains("virtualbox")
			|| description.Contains("vpn")
			|| description.Contains("tap-windows")
			|| description.Contains("tap adapter")
			|| description.Contains("wsl")
			|| description.Contains("docker")
			|| description.Contains("loopback")
			|| description.Contains("pseudo")
			|| description.Contains("npcap")
			|| description.Contains("wan miniport")
			|| description.Contains("bluetooth");
	}

	public static async Task<long> PingAsync(string host = "8.8.8.8")
	{
		try
		{
			using Ping ping = new Ping();
			PingReply r = await ping.SendPingAsync(host, 2000);
			return (r.Status == IPStatus.Success) ? r.RoundtripTime : (-1);
		}
		catch
		{
			return -1L;
		}
	}
}
