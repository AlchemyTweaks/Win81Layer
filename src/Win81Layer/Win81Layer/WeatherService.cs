using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;

namespace Win81Layer;

public static class WeatherService
{
	public readonly record struct Result(
		double TempC, string Description,
		double? HiC = null, double? LoC = null,
		string? City = null, string? TodayDesc = null,
		double? TomHiC = null, double? TomLoC = null, string? TomDesc = null,
		string? ConditionKey = null, int? WeatherCode = null,
		DateTimeOffset? ObservedUtc = null, DateTimeOffset? LocalTime = null,
		DateTimeOffset? SunriseLocal = null, DateTimeOffset? SunsetLocal = null,
		double? Latitude = null, double? Longitude = null,
		string? TimeZone = null, string? Provider = null, bool IsStale = false);

	private sealed class CacheEnvelope
	{
		public int SchemaVersion { get; set; } = 2;
		public List<CacheEntry> Entries { get; set; } = new List<CacheEntry>();
	}

	private sealed class CacheEntry
	{
		public string City { get; set; } = "";
		public DateTimeOffset SavedUtc { get; set; }
		public Result Data { get; set; }
	}

	private static readonly HttpClient Http = CreateClient();
	private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
	private static readonly object CacheGate = new object();
	private static readonly JsonSerializerOptions CacheJson = new JsonSerializerOptions
	{
		PropertyNameCaseInsensitive = true
	};

	private static string CachePath => Path.Combine(
		Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
		"Win81Layer", "cache", "weather-v2.json");

	private static HttpClient CreateClient()
	{
		HttpClient client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
		client.DefaultRequestHeaders.UserAgent.ParseAdd("Win81Layer/2.0");
		return client;
	}

	public static async Task<Result?> GetAsync(string city)
	{
		city = string.IsNullOrWhiteSpace(city) ? "Kalamata" : city.Trim();
		Task<Result?> wttrTask = GetWttrAsync(city);
		Task<Result?> meteoTask = GetOpenMeteoAsync(city);
		await Task.WhenAll(wttrTask, meteoTask);

		Result? wttr = wttrTask.Result;
		Result? meteo = meteoTask.Result;
		Result? result = wttr.HasValue && meteo.HasValue
			? Merge(wttr.Value, meteo.Value)
			: wttr ?? meteo;
		if (result.HasValue)
		{
			Result fresh = result.Value with
			{
				ObservedUtc = DateTimeOffset.UtcNow,
				IsStale = false
			};
			SaveCached(city, fresh);
			return fresh;
		}
		return null;
	}

	public static Result? LoadCached(string city)
	{
		try
		{
			lock (CacheGate)
			{
				if (!File.Exists(CachePath)) return null;
				CacheEnvelope? cache = JsonSerializer.Deserialize<CacheEnvelope>(File.ReadAllText(CachePath), CacheJson);
				CacheEntry? entry = cache?.Entries
					.Where(x => SameCity(x.City, city))
					.OrderByDescending(x => x.SavedUtc)
					.FirstOrDefault();
				if (entry == null) return null;
				bool stale = DateTimeOffset.UtcNow - entry.SavedUtc > TimeSpan.FromMinutes(30);
				return entry.Data with { IsStale = stale };
			}
		}
		catch (Exception ex)
		{
			Logger.Log("Weather cache read failed: " + ex.Message);
			return null;
		}
	}

	public static Result MarkStale(Result result) => result with { IsStale = true };

	private static void SaveCached(string city, Result data)
	{
		try
		{
			lock (CacheGate)
			{
				string path = CachePath;
				Directory.CreateDirectory(Path.GetDirectoryName(path)!);
				CacheEnvelope cache = new CacheEnvelope();
				if (File.Exists(path))
				{
					try
					{
						cache = JsonSerializer.Deserialize<CacheEnvelope>(File.ReadAllText(path), CacheJson) ?? cache;
					}
					catch
					{
					}
				}
				cache.SchemaVersion = 2;
				cache.Entries.RemoveAll(x => SameCity(x.City, city));
				cache.Entries.Add(new CacheEntry { City = city.Trim(), SavedUtc = DateTimeOffset.UtcNow, Data = data });
				cache.Entries = cache.Entries.OrderByDescending(x => x.SavedUtc).Take(8).ToList();
				string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
				try
				{
					File.WriteAllText(temp, JsonSerializer.Serialize(cache, CacheJson));
					File.Move(temp, path, true);
				}
				finally
				{
					try { if (File.Exists(temp)) File.Delete(temp); } catch { }
				}
			}
		}
		catch (Exception ex)
		{
			Logger.Log("Weather cache write failed: " + ex.Message);
		}
	}

	private static bool SameCity(string? a, string? b) => string.Equals(a?.Trim(), b?.Trim(), StringComparison.OrdinalIgnoreCase);

	private static Result Merge(Result wttr, Result meteo)
	{
		return wttr with
		{
			// The geocoder resolves the exact configured query. wttr's nearest_area can be a nearby district,
			// which made a Piraeus tile unexpectedly display another locality.
			City = string.IsNullOrWhiteSpace(meteo.City) ? wttr.City : meteo.City,
			HiC = wttr.HiC ?? meteo.HiC,
			LoC = wttr.LoC ?? meteo.LoC,
			TodayDesc = string.IsNullOrWhiteSpace(wttr.TodayDesc) ? meteo.TodayDesc : wttr.TodayDesc,
			TomHiC = wttr.TomHiC ?? meteo.TomHiC,
			TomLoC = wttr.TomLoC ?? meteo.TomLoC,
			TomDesc = string.IsNullOrWhiteSpace(wttr.TomDesc) ? meteo.TomDesc : wttr.TomDesc,
			ConditionKey = string.IsNullOrWhiteSpace(wttr.ConditionKey) ? meteo.ConditionKey : wttr.ConditionKey,
			LocalTime = meteo.LocalTime,
			SunriseLocal = meteo.SunriseLocal,
			SunsetLocal = meteo.SunsetLocal,
			Latitude = meteo.Latitude ?? wttr.Latitude,
			Longitude = meteo.Longitude ?? wttr.Longitude,
			TimeZone = meteo.TimeZone,
			Provider = "wttr.in + Open-Meteo"
		};
	}

	private static async Task<Result?> GetWttrAsync(string city)
	{
		try
		{
			using JsonDocument doc = JsonDocument.Parse(await Http.GetStringAsync(
				"https://wttr.in/" + Uri.EscapeDataString(city) + "?format=j1"));
			JsonElement root = doc.RootElement;
			JsonElement cur = root.GetProperty("current_condition")[0];
			double temp = ParseStringDouble(cur, "temp_C");
			string description = Description(cur);
			int? code = TryStringInt(cur, "weatherCode");
			double? hi = null, lo = null, tomHi = null, tomLo = null;
			string todayDesc = "", tomorrowDesc = "";
			try
			{
				JsonElement days = root.GetProperty("weather");
				JsonElement today = days[0];
				hi = ParseStringDouble(today, "maxtempC");
				lo = ParseStringDouble(today, "mintempC");
				todayDesc = DayDesc(today);
				if (days.GetArrayLength() > 1)
				{
					JsonElement tomorrow = days[1];
					tomHi = ParseStringDouble(tomorrow, "maxtempC");
					tomLo = ParseStringDouble(tomorrow, "mintempC");
					tomorrowDesc = DayDesc(tomorrow);
				}
			}
			catch
			{
			}
			(double? lat, double? lon) = Coordinates(root);
			return new Result(
				temp, description, hi, lo, CityOf(root, city),
				string.IsNullOrWhiteSpace(todayDesc) ? description : todayDesc,
				tomHi, tomLo, tomorrowDesc,
				ConditionFromWttr(code, description), code, DateTimeOffset.UtcNow,
				Latitude: lat, Longitude: lon, Provider: "wttr.in");
		}
		catch (Exception ex)
		{
			Logger.Log("Weather (wttr.in) failed: " + ex.Message);
			return null;
		}
	}

	private static async Task<Result?> GetOpenMeteoAsync(string city)
	{
		try
		{
			using JsonDocument geo = JsonDocument.Parse(await Http.GetStringAsync(
				"https://geocoding-api.open-meteo.com/v1/search?name=" + Uri.EscapeDataString(city) + "&count=1&language=en&format=json"));
			if (!geo.RootElement.TryGetProperty("results", out JsonElement results) || results.GetArrayLength() == 0)
			{
				Logger.Log("Weather: city '" + city + "' not found");
				return null;
			}
			JsonElement first = results[0];
			double lat = first.GetProperty("latitude").GetDouble();
			double lon = first.GetProperty("longitude").GetDouble();
			string name = first.TryGetProperty("name", out JsonElement nm) ? (nm.GetString() ?? city) : city;
			string url = "https://api.open-meteo.com/v1/forecast?latitude=" + lat.ToString(Inv)
				+ "&longitude=" + lon.ToString(Inv)
				+ "&current=temperature_2m,weather_code,is_day"
				+ "&daily=temperature_2m_max,temperature_2m_min,weather_code,sunrise,sunset"
				+ "&forecast_days=2&timezone=auto";
			using JsonDocument wxDoc = JsonDocument.Parse(await Http.GetStringAsync(url));
			JsonElement root = wxDoc.RootElement;
			JsonElement current = root.GetProperty("current");
			double temp = current.GetProperty("temperature_2m").GetDouble();
			int code = current.GetProperty("weather_code").GetInt32();
			int offset = root.TryGetProperty("utc_offset_seconds", out JsonElement off) ? off.GetInt32() : 0;
			string? zone = root.TryGetProperty("timezone", out JsonElement tz) ? tz.GetString() : null;
			DateTimeOffset? localTime = ParseLocal(current, "time", offset);
			double? hi = null, lo = null, tomHi = null, tomLo = null;
			string todayDesc = DescribeWmo(code), tomorrowDesc = "";
			DateTimeOffset? sunrise = null, sunset = null;
			try
			{
				JsonElement daily = root.GetProperty("daily");
				JsonElement max = daily.GetProperty("temperature_2m_max");
				JsonElement min = daily.GetProperty("temperature_2m_min");
				JsonElement dayCodes = daily.GetProperty("weather_code");
				hi = max[0].GetDouble();
				lo = min[0].GetDouble();
				todayDesc = DescribeWmo(dayCodes[0].GetInt32());
				sunrise = ParseLocal(daily.GetProperty("sunrise"), 0, offset);
				sunset = ParseLocal(daily.GetProperty("sunset"), 0, offset);
				if (max.GetArrayLength() > 1)
				{
					tomHi = max[1].GetDouble();
					tomLo = min[1].GetDouble();
					tomorrowDesc = DescribeWmo(dayCodes[1].GetInt32());
				}
			}
			catch
			{
			}
			return new Result(
				temp, DescribeWmo(code), hi, lo, name, todayDesc, tomHi, tomLo, tomorrowDesc,
				ConditionFromWmo(code), code, DateTimeOffset.UtcNow, localTime, sunrise, sunset,
				lat, lon, zone, "Open-Meteo");
		}
		catch (Exception ex)
		{
			Logger.Log("Weather (Open-Meteo) failed: " + ex.Message);
			return null;
		}
	}

	private static DateTimeOffset? ParseLocal(JsonElement parent, string property, int offsetSeconds)
	{
		if (!parent.TryGetProperty(property, out JsonElement value)) return null;
		return ParseLocal(value.GetString(), offsetSeconds);
	}

	private static DateTimeOffset? ParseLocal(JsonElement array, int index, int offsetSeconds)
	{
		if (array.ValueKind != JsonValueKind.Array || array.GetArrayLength() <= index) return null;
		return ParseLocal(array[index].GetString(), offsetSeconds);
	}

	private static DateTimeOffset? ParseLocal(string? value, int offsetSeconds)
	{
		if (string.IsNullOrWhiteSpace(value) || !DateTime.TryParse(value, Inv, DateTimeStyles.AllowWhiteSpaces, out DateTime local))
			return null;
		TimeSpan offset = TimeSpan.FromSeconds(Math.Clamp(offsetSeconds, -50400, 50400));
		return new DateTimeOffset(DateTime.SpecifyKind(local, DateTimeKind.Unspecified), offset);
	}

	private static string DayDesc(JsonElement day)
	{
		try
		{
			JsonElement hourly = day.GetProperty("hourly");
			int index = Math.Min(4, hourly.GetArrayLength() - 1);
			return Description(hourly[index]);
		}
		catch
		{
			return "";
		}
	}

	private static string Description(JsonElement condition)
	{
		try
		{
			return (condition.GetProperty("weatherDesc")[0].GetProperty("value").GetString() ?? "").Trim();
		}
		catch
		{
			return "";
		}
	}

	private static string CityOf(JsonElement root, string fallback)
	{
		try
		{
			JsonElement area = root.GetProperty("nearest_area")[0];
			string name = (area.GetProperty("areaName")[0].GetProperty("value").GetString() ?? "").Trim();
			return string.IsNullOrWhiteSpace(name) ? fallback : name;
		}
		catch
		{
			return fallback;
		}
	}

	private static (double? Latitude, double? Longitude) Coordinates(JsonElement root)
	{
		try
		{
			JsonElement area = root.GetProperty("nearest_area")[0];
			return (ParseStringDouble(area, "latitude"), ParseStringDouble(area, "longitude"));
		}
		catch
		{
			return (null, null);
		}
	}

	private static double ParseStringDouble(JsonElement element, string property)
	{
		JsonElement value = element.GetProperty(property);
		return value.ValueKind == JsonValueKind.Number ? value.GetDouble() : double.Parse(value.GetString() ?? "0", Inv);
	}

	private static int? TryStringInt(JsonElement element, string property)
	{
		try
		{
			JsonElement value = element.GetProperty(property);
			return value.ValueKind == JsonValueKind.Number ? value.GetInt32() : int.Parse(value.GetString() ?? "", Inv);
		}
		catch
		{
			return null;
		}
	}

	private static string ConditionFromWttr(int? code, string description)
	{
		if (code.HasValue)
		{
			int c = code.Value;
			if (c == 113) return "clear";
			if (c == 116) return "partly";
			if (c is 119 or 122) return "cloudy";
			if (c is 143 or 248 or 260) return "fog";
			if (c is 179 or 182 or 185 or 227 or 230 or 320 or 323 or 326 or 329 or 332 or 335 or 338 or 350 or 368 or 371) return "snow";
			if (c is 200 or 386 or 389 or 392 or 395) return "thunder";
			if (c is 176 or 263 or 266 or 281 or 284 or 293 or 296 or 299 or 302 or 305 or 308 or 311 or 314 or 317 or 353 or 356 or 359 or 362 or 365) return "rain";
		}
		return ConditionFromText(description);
	}

	private static string ConditionFromWmo(int code)
	{
		return code switch
		{
			0 or 1 => "clear",
			2 => "partly",
			3 => "cloudy",
			45 or 48 => "fog",
			51 or 53 or 55 or 56 or 57 or 61 or 63 or 65 or 66 or 67 or 80 or 81 or 82 => "rain",
			71 or 73 or 75 or 77 or 85 or 86 => "snow",
			95 or 96 or 99 => "thunder",
			_ => "cloudy"
		};
	}

	private static string ConditionFromText(string? description)
	{
		string value = (description ?? "").ToLowerInvariant();
		if (value.Contains("thunder")) return "thunder";
		if (value.Contains("snow") || value.Contains("sleet") || value.Contains("ice") || value.Contains("blizzard")) return "snow";
		if (value.Contains("rain") || value.Contains("drizzle") || value.Contains("shower")) return "rain";
		if (value.Contains("fog") || value.Contains("mist") || value.Contains("haze")) return "fog";
		if (value.Contains("partly") || value.Contains("mainly")) return "partly";
		if (value.Contains("overcast") || value.Contains("cloud")) return "cloudy";
		return "clear";
	}

	private static string DescribeWmo(int code)
	{
		return code switch
		{
			0 => "Clear",
			1 => "Mainly clear",
			2 => "Partly cloudy",
			3 => "Overcast",
			45 or 48 => "Fog",
			51 or 53 or 55 => "Drizzle",
			56 or 57 => "Freezing drizzle",
			61 or 63 or 65 => "Rain",
			66 or 67 => "Freezing rain",
			71 or 73 or 75 => "Snow",
			77 => "Snow grains",
			80 or 81 or 82 => "Rain showers",
			85 or 86 => "Snow showers",
			95 => "Thunderstorm",
			96 or 99 => "Thunderstorm, hail",
			_ => "Unknown"
		};
	}
}
