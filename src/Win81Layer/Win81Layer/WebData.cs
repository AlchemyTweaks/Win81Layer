using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Win81Layer;

internal static class WebData
{
	private static readonly HttpClient Http = CreateClient();

	private static readonly ConcurrentDictionary<string, (DateTime Stamp, EntityCard? Card)> _cache = new ConcurrentDictionary<string, (DateTime, EntityCard)>();

	private static readonly TimeSpan Ttl = TimeSpan.FromMinutes(10L);

	private static HttpClient CreateClient()
	{
		HttpClient h = new HttpClient
		{
			Timeout = TimeSpan.FromSeconds(3L)
		};
		h.DefaultRequestHeaders.UserAgent.ParseAdd("Win81Layer/1.0 (personal use)");
		return h;
	}

	public static bool TryGetCard(string key, out EntityCard? card)
	{
		if (_cache.TryGetValue(key, out (DateTime, EntityCard) e) && DateTime.UtcNow - e.Item1 < Ttl)
		{
			card = e.Item2;
			return true;
		}
		card = null;
		return false;
	}

	public static void PutCard(string key, EntityCard? card)
	{
		_cache[key] = (DateTime.UtcNow, card);
	}

	public static async Task<JsonDocument?> GetJsonAsync(string url, CancellationToken ct)
	{
		try
		{
			using HttpResponseMessage resp = await Http.GetAsync(url, ct);
			if (!resp.IsSuccessStatusCode)
			{
				return null;
			}
			return JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
		}
		catch (OperationCanceledException)
		{
			return null;
		}
		catch (Exception ex2)
		{
			Exception ex3 = ex2;
			Logger.Log("WebData GET " + url + ": " + ex3.Message);
			return null;
		}
	}

	public static async Task<ImageSource?> LoadHeroAsync(string? url, CancellationToken ct)
	{
		if (string.IsNullOrEmpty(url))
		{
			return null;
		}
		try
		{
			byte[] bytes = await Http.GetByteArrayAsync(url, ct);
			BitmapImage bmp = new BitmapImage();
			bmp.BeginInit();
			bmp.CacheOption = BitmapCacheOption.OnLoad;
			bmp.DecodePixelWidth = 340;
			bmp.StreamSource = new MemoryStream(bytes);
			bmp.EndInit();
			((Freezable)bmp).Freeze();
			return bmp;
		}
		catch (OperationCanceledException)
		{
			return null;
		}
		catch (Exception ex2)
		{
			Logger.Log("WebData hero " + url + ": " + ex2.Message);
			return null;
		}
	}
}
