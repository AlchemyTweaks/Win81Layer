using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Win81Layer;

internal sealed class PlaceResolver : IEntityResolver
{
	public IntentKind Kind => IntentKind.Place;

	public async Task<EntityCard?> ResolveEntityAsync(Query q, Intent intent, CancellationToken ct)
	{
		string name = (intent.EntityHint ?? q.Raw).Trim();
		if (name.Length < 2)
		{
			return null;
		}
		string cacheKey = "place:" + name.ToLowerInvariant();
		if (WebData.TryGetCard(cacheKey, out EntityCard cached))
		{
			return cached;
		}
		EntityCard card = await BuildAsync(name, ct);
		WebData.PutCard(cacheKey, card);
		return card;
	}

	private static async Task<EntityCard?> BuildAsync(string name, CancellationToken ct)
	{
		string display = null;
		string type = null;
		string country = null;
		double lat = 0.0;
		double lon = 0.0;
		bool geocoded = false;
		string geoUrl = "https://nominatim.openstreetmap.org/search?q=" + Uri.EscapeDataString(name) + "&format=jsonv2&limit=1&addressdetails=1";
		using (JsonDocument doc = await WebData.GetJsonAsync(geoUrl, ct))
		{
			if (doc != null && doc.RootElement.ValueKind == JsonValueKind.Array && doc.RootElement.GetArrayLength() > 0)
			{
				JsonElement e = doc.RootElement[0];
				display = Str(e, "display_name") ?? name;
				type = Str(e, "type");
				if (double.TryParse(Str(e, "lat"), NumberStyles.Float, CultureInfo.InvariantCulture, out var la))
				{
					lat = la;
				}
				if (double.TryParse(Str(e, "lon"), NumberStyles.Float, CultureInfo.InvariantCulture, out var lo))
				{
					lon = lo;
				}
				if (e.TryGetProperty("address", out var ad) && ad.ValueKind == JsonValueKind.Object)
				{
					country = Str(ad, "country");
				}
				geocoded = true;
			}
		}
		if (!geocoded)
		{
			return null;
		}
		ct.ThrowIfCancellationRequested();
		string text = display;
		string primary = (((text != null) ? text.Split(',')[0] : null) ?? name).Trim();
		string extract = null;
		string hero = null;
		string wUrl = "https://en.wikipedia.org/api/rest_v1/page/summary/" + Uri.EscapeDataString(primary);
		using (JsonDocument wdoc = await WebData.GetJsonAsync(wUrl, ct))
		{
			if (wdoc != null && wdoc.RootElement.ValueKind == JsonValueKind.Object)
			{
				JsonElement r = wdoc.RootElement;
				extract = Str(r, "extract");
				if (r.TryGetProperty("originalimage", out var oi) && oi.ValueKind == JsonValueKind.Object)
				{
					hero = Str(oi, "source");
				}
				if (hero == null && r.TryGetProperty("thumbnail", out var th) && th.ValueKind == JsonValueKind.Object)
				{
					hero = Str(th, "source");
				}
			}
		}
		WeatherService.Result? weather = null;
		try
		{
			weather = await WeatherService.GetAsync(primary);
		}
		catch (Exception ex)
		{
			Logger.Log("PlaceResolver weather: " + ex.Message);
		}
		List<Fact> facts = new List<Fact>();
		if (!string.IsNullOrWhiteSpace(extract))
		{
			string ex2 = extract.Trim();
			if (ex2.Length > 260)
			{
				ex2 = ex2.Substring(0, 257) + "…";
			}
			facts.Add(new Fact("", ex2));
		}
		WeatherService.Result w = default(WeatherService.Result);
		int num;
		if (weather.HasValue)
		{
			w = weather.GetValueOrDefault();
			num = 1;
		}
		else
		{
			num = 0;
		}
		if (num != 0)
		{
			facts.Add(new Fact("Weather", $"{Math.Round(w.TempC)}°C · {w.Description}", "\ue706"));
		}
		if (!string.IsNullOrEmpty(country) && !string.Equals(country, primary, StringComparison.OrdinalIgnoreCase))
		{
			facts.Add(new Fact("Country", country, "\ue909"));
		}
		if (!string.IsNullOrEmpty(type))
		{
			facts.Add(new Fact("Type", Cap(type.Replace('_', ' ')), "\ue1d2"));
		}
		string latS = lat.ToString(CultureInfo.InvariantCulture);
		string lonS = lon.ToString(CultureInfo.InvariantCulture);
		return new EntityCard(Actions: new List<LauncherAction>
		{
			new LauncherAction("maps", "Open in Maps", "\ue707", () => Launch($"bingmaps:?cp={latS}~{lonS}&lvl=12&q={Uri.EscapeDataString(primary)}")),
			new LauncherAction("directions", "Directions", "\ue8ad", () => Launch("bingmaps:?rtp=~pos." + latS + "_" + lonS)),
			new LauncherAction("web", "Search the web", "\ue11a", () => Launch("https://www.bing.com/search?q=" + Uri.EscapeDataString(primary))),
			new LauncherAction("wiki", "Wikipedia", "\ue7be", () => Launch("https://en.wikipedia.org/wiki/" + Uri.EscapeDataString(primary)))
		}, Kind: IntentKind.Place, Name: primary, TypeLabel: "Place", HeroImageUrl: hero, Facts: facts);
	}

	private static string? Str(JsonElement e, string prop)
	{
		JsonElement v;
		return (e.TryGetProperty(prop, out v) && v.ValueKind == JsonValueKind.String) ? v.GetString() : null;
	}

	private static Task Launch(string uri)
	{
		try
		{
			if (WebOpen.IsWeb(uri))
			{
				WebOpen.Url(uri);   // "Search the web" / Wikipedia -> preferred browser; bingmaps: stays on the system handler
			}
			else
			{
				Process.Start(new ProcessStartInfo(uri)
				{
					UseShellExecute = true
				});
			}
		}
		catch (Exception ex)
		{
			Logger.Log("PlaceResolver launch " + uri + ": " + ex.Message);
		}
		return Task.CompletedTask;
	}

	private static string Cap(string s)
	{
		return string.IsNullOrEmpty(s) ? s : (char.ToUpperInvariant(s[0]) + s.Substring(1));
	}
}
