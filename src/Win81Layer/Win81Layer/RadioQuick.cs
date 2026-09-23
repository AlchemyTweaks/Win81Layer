using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Windows.Devices.Radios;

namespace Win81Layer;

internal static class RadioQuick
{
	public static async Task<(bool airplaneOn, bool bluetoothOn)> ReadStates()
	{
		try
		{
			if (await Radio.RequestAccessAsync() != RadioAccessStatus.Allowed)
			{
				return (airplaneOn: false, bluetoothOn: false);
			}
			IReadOnlyList<Radio> radios = await Radio.GetRadiosAsync();
			bool air = radios.Count > 0 && radios.All((Radio x) => x.State != RadioState.On);
			Radio bt = radios.FirstOrDefault((Radio x) => x.Kind == RadioKind.Bluetooth);
			bool blue = (object)bt != null && bt.State == RadioState.On;
			return (airplaneOn: air, bluetoothOn: blue);
		}
		catch
		{
			return (airplaneOn: false, bluetoothOn: false);
		}
	}

	public static async Task<bool> ToggleAirplane()
	{
		try
		{
			if (await Radio.RequestAccessAsync() != RadioAccessStatus.Allowed)
			{
				return false;
			}
			IReadOnlyList<Radio> radios = await Radio.GetRadiosAsync();
			if (radios.Count == 0)
			{
				return false;
			}
			bool anyOn = radios.Any((Radio x) => x.State == RadioState.On);
			bool accepted = true;
			foreach (Radio r in radios)
			{
				try
				{
					accepted &= await r.SetStateAsync((!anyOn) ? RadioState.On : RadioState.Off) == RadioAccessStatus.Allowed;
				}
				catch
				{
					accepted = false;
				}
			}
			return accepted;
		}
		catch
		{
			return false;
		}
	}

	// Direction-aware setters (so "turn off bluetooth" ends OFF regardless of current state, unlike Toggle*).
	public static async Task<bool> SetKind(RadioKind kind, bool on)
	{
		try
		{
			if (await Radio.RequestAccessAsync() != RadioAccessStatus.Allowed)
			{
				return false;
			}
			Radio r = (await Radio.GetRadiosAsync()).FirstOrDefault((Radio x) => x.Kind == kind);
			if ((object)r == null)
			{
				return false;
			}
			return await r.SetStateAsync(on ? RadioState.On : RadioState.Off) == RadioAccessStatus.Allowed;
		}
		catch
		{
			return false;
		}
	}

	public static async Task<bool> SetAirplane(bool on)
	{
		try
		{
			if (await Radio.RequestAccessAsync() != RadioAccessStatus.Allowed)
			{
				return false;
			}
			IReadOnlyList<Radio> radios = await Radio.GetRadiosAsync();
			if (radios.Count == 0)
			{
				return false;
			}
			bool accepted = true;
			foreach (Radio r in radios)
			{
				try
				{
					accepted &= await r.SetStateAsync(on ? RadioState.Off : RadioState.On) == RadioAccessStatus.Allowed;   // airplane ON = all radios OFF
				}
				catch
				{
					accepted = false;
				}
			}
			return accepted;
		}
		catch
		{
			return false;
		}
	}

	public static async Task<bool> ToggleBluetooth()
	{
		try
		{
			if (await Radio.RequestAccessAsync() != RadioAccessStatus.Allowed)
			{
				return false;
			}
			Radio bt = (await Radio.GetRadiosAsync()).FirstOrDefault((Radio x) => x.Kind == RadioKind.Bluetooth);
			if ((object)bt == null)
			{
				return false;
			}
			return await bt.SetStateAsync((bt.State != RadioState.On) ? RadioState.On : RadioState.Off) == RadioAccessStatus.Allowed;
		}
		catch
		{
			return false;
		}
	}
}
