#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Win81Layer;

public sealed record PlaceCandidate(long Id, string Name, string Region, string Country,
    string CountryCode, double Latitude, double Longitude, string TimeZone)
{
    public string DisplayName => string.Join(", ", new[] { Name, Region, Country }
        .Where(s => !string.IsNullOrWhiteSpace(s)).Distinct(StringComparer.OrdinalIgnoreCase));
    public override string ToString() => DisplayName;
}

public sealed record PlaceWeather(double TemperatureC, double? WindKmh, int? WeatherCode,
    DateTimeOffset ObservedUtc);
public sealed record PlacePhoto(string ImageUrl, string SourceUrl, string Title, string Artist,
    string License, string LicenseUrl);
public sealed record PlaceArticle(long PageId, string Title, string Url, string Description,
    double Latitude, double Longitude, string ImageName, PlacePhoto? Photo = null);
public sealed record PlaceSource<T>(T? Data, DateTimeOffset? FetchedUtc, bool Cached, bool Stale,
    string? Error = null) where T : class;
public sealed record PlaceDetails(PlaceCandidate Place, PlaceSource<PlaceWeather> Weather,
    PlaceSource<PlaceArticle> Article, PlaceSource<PlaceArticle[]> Nearby);

// Independent of shell settings and WeatherService's first-match, non-cancellable city lookup.
// All facts originate in public structured APIs. No general web results are synthesized.
public sealed class PlaceSearchService
{
    public const string GeocodingSource = "https://open-meteo.com/en/docs/geocoding-api";
    public const string WeatherSource = "https://open-meteo.com/";
    public const string TextLicense = "https://creativecommons.org/licenses/by-sa/4.0/";
    private const string Wiki = "https://en.wikipedia.org/w/api.php?action=query&format=json&formatversion=2&maxlag=5";
    private const string ArticleProperties = "&prop=coordinates%7Cpageimages%7Cpageprops%7Cextracts%7Cinfo&inprop=url&piprop=name&pilicense=free&exintro=1&explaintext=1&exchars=900&exlimit=max&colimit=max";
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    private static readonly HttpClient DefaultHttp = MakeClient();
    public static PlaceSearchService Shared { get; } = new();
    private static readonly SemaphoreSlim NetworkSlots = new(3);
    private static readonly SemaphoreSlim WikiRequestGate = new(1);
    private static DateTimeOffset _nextWikiRequest;
    private static DateTimeOffset _wikiRetryAfter;
    private readonly HttpClient _http;
    private readonly bool _spacePublicRequests;
    private readonly string _cacheDirectory;
    private readonly Func<DateTimeOffset> _clock;

    public PlaceSearchService(HttpClient? http = null, string? cacheDirectory = null,
        Func<DateTimeOffset>? clock = null)
    {
        _http = http ?? DefaultHttp;
        _spacePublicRequests = http == null;
        _cacheDirectory = cacheDirectory ?? Path.Combine(Environment.GetFolderPath(
            Environment.SpecialFolder.LocalApplicationData), "Win81Layer", "cache", "places-v1");
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    private static HttpClient MakeClient()
    {
        HttpClient client = new(new HttpClientHandler { AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.All });
        client.Timeout = TimeSpan.FromSeconds(14);
        client.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Win81LayerPlaceSearch/1.0 (Windows desktop; Wikipedia place lookup)");
        return client;
    }

    public Task<PlaceSource<PlaceCandidate[]>> SearchAsync(string query,
        CancellationToken cancellation = default, bool force = false)
    {
        query = NormalizeQuery(query);
        if (query.Length < 2) return Task.FromResult(new PlaceSource<PlaceCandidate[]>(
            Array.Empty<PlaceCandidate>(), null, false, false));
        string url = "https://geocoding-api.open-meteo.com/v1/search?name="
            + Uri.EscapeDataString(query) + "&count=12&language=en&format=json";
        return FetchAsync(url, ParseCandidates, TimeSpan.FromDays(7), TimeSpan.FromDays(90), force, cancellation);
    }

    public async Task<PlaceDetails> GetDetailsAsync(PlaceCandidate place,
        CancellationToken cancellation = default, bool force = false)
    {
        ValidateCoordinates(place.Latitude, place.Longitude);
        Task<PlaceSource<PlaceWeather>> weather = GetWeatherAsync(place, cancellation, force);
        Task<PlaceSource<PlaceArticle>> article = GetArticleAsync(place, cancellation, force);
        Task<PlaceSource<PlaceArticle[]>> nearby = GetNearbyAsync(place, cancellation, force);
        await Task.WhenAll(weather, article, nearby).ConfigureAwait(false);
        cancellation.ThrowIfCancellationRequested();
        PlaceSource<PlaceArticle[]> adjacent = await nearby.ConfigureAwait(false);
        PlaceArticle? main = (await article.ConfigureAwait(false)).Data;
        if (adjacent.Data != null)
            adjacent = adjacent with { Data = adjacent.Data.Where(p => p.PageId != main?.PageId).Take(5).ToArray() };
        return new PlaceDetails(place, await weather.ConfigureAwait(false), await article.ConfigureAwait(false), adjacent);
    }

    public Task<PlaceSource<PlaceWeather>> GetWeatherAsync(PlaceCandidate place,
        CancellationToken cancellation = default, bool force = false)
    {
        ValidateCoordinates(place.Latitude, place.Longitude);
        string url = "https://api.open-meteo.com/v1/forecast?latitude=" + Number(place.Latitude)
            + "&longitude=" + Number(place.Longitude)
            + "&current=temperature_2m,weather_code,wind_speed_10m&temperature_unit=celsius&wind_speed_unit=kmh&timeformat=unixtime&timezone=GMT&forecast_days=1";
        return FetchAsync(url, ParseWeather, TimeSpan.FromMinutes(15), TimeSpan.FromHours(24), force, cancellation);
    }

    public async Task<PlaceSource<PlaceArticle>> GetArticleAsync(PlaceCandidate place,
        CancellationToken cancellation = default, bool force = false)
    {
        string[] titles = new[] { place.Name, place.Name + ", " + place.Region, place.Name + ", " + place.Country,
            place.Name + " (" + place.Country + ")" }.Distinct().ToArray();
        PlaceSource<PlaceArticle[]> result = await FetchAsync(Wiki + "&redirects=1&titles="
            + Uri.EscapeDataString(string.Join("|", titles)) + ArticleProperties, ParseArticles,
            TimeSpan.FromDays(7), TimeSpan.FromDays(30), force, cancellation).ConfigureAwait(false);
        PlaceArticle? article = MatchArticle(place, result.Data ?? Array.Empty<PlaceArticle>());
        // A title may redirect (e.g. an exonym). The API-returned title plus coordinates still
        // need a name match; ambiguous or distant pages are never used as this city's identity.
        if (article == null && result.Error == null)
        {
            PlaceSource<PlaceArticle[]> search = await FetchAsync(Wiki
                + "&generator=search&gsrnamespace=0&gsrlimit=8&gsrsearch="
                + Uri.EscapeDataString(place.Name + " " + place.Country) + ArticleProperties, ParseArticles,
                TimeSpan.FromDays(7), TimeSpan.FromDays(30), force, cancellation).ConfigureAwait(false);
            result = search;
            article = MatchArticle(place, search.Data ?? Array.Empty<PlaceArticle>());
        }
        if (article != null && article.ImageName.Length > 0)
        {
            PlaceSource<PlacePhoto> photo = await GetPhotoAsync(article.ImageName, cancellation, force).ConfigureAwait(false);
            article = article with { Photo = photo.Data };
            result = result with { Stale = result.Stale || photo.Stale, Error = result.Error ?? photo.Error };
        }
        return new PlaceSource<PlaceArticle>(article, result.FetchedUtc, result.Cached, result.Stale, result.Error);
    }

    public async Task<PlaceSource<PlaceArticle[]>> GetNearbyAsync(PlaceCandidate place,
        CancellationToken cancellation = default, bool force = false)
    {
        string url = Wiki + "&generator=geosearch&ggscoord=" + Number(place.Latitude) + "%7C"
            + Number(place.Longitude) + "&ggsradius=10000&ggslimit=16&ggsnamespace=0" + ArticleProperties;
        PlaceSource<PlaceArticle[]> result = await FetchAsync(url, ParseArticles,
            TimeSpan.FromDays(7), TimeSpan.FromDays(30), force, cancellation).ConfigureAwait(false);
        if (result.Data == null) return result;
        PlaceArticle[] pages = result.Data.Where(p => DistanceKm(place.Latitude, place.Longitude,
            p.Latitude, p.Longitude) <= 10 && !NameMatches(place.Name, p.Title))
            .OrderByDescending(p => p.ImageName.Length > 0)
            .ThenBy(p => DistanceKm(place.Latitude, place.Longitude, p.Latitude, p.Longitude)).Take(5).ToArray();
        // Geotagged pages establish proximity, not tourist status. The UI calls these Nearby places.
        string[] names = pages.Select(p => p.ImageName).Where(n => n.Length > 0).Distinct().ToArray();
        if (names.Length == 0) return result with { Data = pages };
        PlaceSource<PlacePhoto[]> photos = await GetPhotosAsync(names, cancellation, force).ConfigureAwait(false);
        return result with { Data = pages.Select(p => p with { Photo = photos.Data?.FirstOrDefault(photo =>
            string.Equals(photo.Title.Replace('_', ' '), "File:" + p.ImageName.Replace('_', ' '), StringComparison.OrdinalIgnoreCase)) }).ToArray(),
            Stale = result.Stale || photos.Stale, Error = result.Error ?? photos.Error };
    }

    private async Task<PlaceSource<PlacePhoto>> GetPhotoAsync(string name, CancellationToken cancellation, bool force)
    {
        PlaceSource<PlacePhoto[]> photos = await GetPhotosAsync(new[] { name }, cancellation, force).ConfigureAwait(false);
        return new PlaceSource<PlacePhoto>(photos.Data?.FirstOrDefault(), photos.FetchedUtc, photos.Cached, photos.Stale, photos.Error);
    }
    private Task<PlaceSource<PlacePhoto[]>> GetPhotosAsync(string[] names, CancellationToken cancellation, bool force)
    {
        string url = "https://commons.wikimedia.org/w/api.php?action=query&format=json&formatversion=2&maxlag=5&titles="
            + Uri.EscapeDataString(string.Join("|", names.Select(n => "File:" + n)))
            + "&prop=imageinfo&iiprop=url%7Cextmetadata%7Cmime&iiurlwidth=1280&iiextmetadatalanguage=en"
            + "&iiextmetadatafilter=Artist%7CLicenseShortName%7CLicenseUrl%7CAttributionRequired%7CObjectName";
        return FetchAsync(url, ParsePhotos, TimeSpan.FromDays(30), TimeSpan.FromDays(90), force, cancellation);
    }

    public Task<PlaceSource<byte[]>> GetImageAsync(PlacePhoto photo, CancellationToken cancellation = default)
    {
        if (!IsImageUrl(photo.ImageUrl)) return Task.FromResult(new PlaceSource<byte[]>(null, null, false, false, "Photo URL unavailable"));
        return FetchAsync(photo.ImageUrl, bytes => bytes.Length > 16 &&
            ((bytes[0] == 0xff && bytes[1] == 0xd8 && bytes[2] == 0xff)
             || bytes.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }))
            ? bytes : throw new JsonException("Invalid photo content"),
            TimeSpan.FromDays(30), TimeSpan.FromDays(90), false, cancellation, 8 * 1024 * 1024);
    }

    internal static PlaceCandidate[] ParseCandidates(byte[] bytes)
    {
        using JsonDocument doc = JsonDocument.Parse(bytes);
        JsonElement root = doc.RootElement;
        CheckApiError(root);
        if (!root.TryGetProperty("results", out JsonElement results)) return Array.Empty<PlaceCandidate>();
        return results.EnumerateArray().Where(p => Text(p, "feature_code").StartsWith("P", StringComparison.Ordinal))
            .Select(p => new PlaceCandidate(p.GetProperty("id").GetInt64(), Text(p, "name"), Text(p, "admin1"),
                Text(p, "country"), Text(p, "country_code"), p.GetProperty("latitude").GetDouble(),
                p.GetProperty("longitude").GetDouble(), Text(p, "timezone")))
            .Where(p => p.Id > 0 && p.Name.Length > 0 && ValidCoordinates(p.Latitude, p.Longitude))
            .DistinctBy(p => p.Id).Take(12).ToArray();
    }

    internal static PlaceWeather ParseWeather(byte[] bytes)
    {
        using JsonDocument doc = JsonDocument.Parse(bytes);
        JsonElement root = doc.RootElement;
        CheckApiError(root);
        JsonElement units = root.GetProperty("current_units"), current = root.GetProperty("current");
        if (Text(units, "temperature_2m") != "\u00b0C" || Text(units, "time") != "unixtime")
            throw new JsonException("Unexpected weather units");
        double temperature = current.GetProperty("temperature_2m").GetDouble();
        if (!double.IsFinite(temperature)) throw new JsonException("Invalid temperature");
        double? wind = Text(units, "wind_speed_10m") == "km/h" ? OptionalNumber(current, "wind_speed_10m") : null;
        int? code = current.TryGetProperty("weather_code", out JsonElement c) && c.ValueKind == JsonValueKind.Number && c.TryGetInt32(out int n) ? n : null;
        return new PlaceWeather(temperature, wind, code,
            DateTimeOffset.FromUnixTimeSeconds(current.GetProperty("time").GetInt64()));
    }

    internal static PlaceArticle[] ParseArticles(byte[] bytes)
    {
        using JsonDocument doc = JsonDocument.Parse(bytes);
        CheckApiError(doc.RootElement);
        List<PlaceArticle> articles = new();
        foreach (JsonElement p in Pages(doc.RootElement))
        {
            if (p.TryGetProperty("missing", out _) || !p.TryGetProperty("pageid", out JsonElement id)
                || !p.TryGetProperty("coordinates", out JsonElement coordinates) || coordinates.GetArrayLength() == 0)
                continue;
            if (p.TryGetProperty("pageprops", out JsonElement props) && props.TryGetProperty("disambiguation", out _)) continue;
            JsonElement coord = coordinates[0];
            if (Text(coord, "globe") is not ("" or "earth")) continue;
            double lat = coord.GetProperty("lat").GetDouble(), lon = coord.GetProperty("lon").GetDouble();
            string url = Text(p, "fullurl"), extract = Text(p, "extract");
            if (!ValidCoordinates(lat, lon) || !IsHttpsHost(url, "en.wikipedia.org") || extract.Length == 0) continue;
            string image = Text(p, "pageimage");
            if (!(image.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase)
                || image.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase)
                || image.EndsWith(".png", StringComparison.OrdinalIgnoreCase))) image = "";
            articles.Add(new PlaceArticle(id.GetInt64(), Text(p, "title"), url, extract, lat, lon, image));
        }
        return articles.ToArray();
    }

    internal static PlacePhoto ParsePhoto(byte[] bytes) => ParsePhotos(bytes).FirstOrDefault()
        ?? throw new JsonException("No attributed free photograph available");

    private static PlacePhoto[] ParsePhotos(byte[] bytes)
    {
        using JsonDocument doc = JsonDocument.Parse(bytes);
        CheckApiError(doc.RootElement);
        List<PlacePhoto> photos = new();
        foreach (JsonElement p in Pages(doc.RootElement))
        {
            if (!p.TryGetProperty("imageinfo", out JsonElement info) || info.GetArrayLength() == 0) continue;
            JsonElement i = info[0];
            if (Text(i, "mime") is not ("image/jpeg" or "image/png")) continue;
            string image = Text(i, "thumburl"), source = Text(i, "descriptionurl");
            if (!IsImageUrl(image) || !IsHttpsHost(source, "commons.wikimedia.org")) continue;
            if (!i.TryGetProperty("extmetadata", out JsonElement meta)) continue;
            string artist = Metadata(meta, "Artist"), license = Metadata(meta, "LicenseShortName"), licenseUrl = Metadata(meta, "LicenseUrl");
            if (licenseUrl.StartsWith("//", StringComparison.Ordinal)) licenseUrl = "https:" + licenseUrl;
            if (licenseUrl.StartsWith("http://creativecommons.org/", StringComparison.Ordinal)) licenseUrl = "https" + licenseUrl.Substring(4);
            if (artist.Length == 0 || license.Length == 0 || !IsHttpsHost(licenseUrl, "creativecommons.org")) continue;
            photos.Add(new PlacePhoto(image, source, Text(p, "title"), artist, license, licenseUrl));
        }
        return photos.ToArray();
    }

    internal static PlaceArticle? MatchArticle(PlaceCandidate place, IEnumerable<PlaceArticle> pages) => pages
        .Where(p => NameMatches(place.Name, p.Title) && DistanceKm(place.Latitude, place.Longitude, p.Latitude, p.Longitude) <= 15)
        .OrderBy(p => DistanceKm(place.Latitude, place.Longitude, p.Latitude, p.Longitude)).FirstOrDefault();

    internal static bool NameMatches(string place, string title)
    {
        static string Normalize(string value) => string.Concat(value.Normalize(NormalizationForm.FormD)
            .Where(c => CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark))
            .Normalize(NormalizationForm.FormC).Trim().ToLowerInvariant();
        string a = Normalize(place), b = Normalize(title);
        return b == a || b.StartsWith(a + ", ", StringComparison.Ordinal) || b.StartsWith(a + " (", StringComparison.Ordinal);
    }

    public static string NormalizeQuery(string? query)
    {
        string value = (query ?? "").Trim();
        return value.Length > 200 ? value.Substring(0, 200) : value;
    }
    public static string BuildWebSearchUrl(string query, string engine) =>
        (string.Equals(engine, "Google", StringComparison.OrdinalIgnoreCase) ? "https://www.google.com/search?q=" : "https://www.bing.com/search?q=")
        + Uri.EscapeDataString(NormalizeQuery(query));
    public static string MapsUrl(PlaceCandidate place, bool defaultGoogleMaps = true)
    {
        ValidateCoordinates(place.Latitude, place.Longitude);
        return defaultGoogleMaps
            ? "https://www.google.com/maps/search/?api=1&query=" + Uri.EscapeDataString(Number(place.Latitude) + "," + Number(place.Longitude))
            : "https://www.bing.com/maps?q=" + Uri.EscapeDataString(place.DisplayName)
                + "&cp=" + Number(place.Latitude) + "~" + Number(place.Longitude) + "&lvl=12";
    }
    public static DateTimeOffset? LocalTime(PlaceCandidate place, DateTimeOffset utc)
    {
        try { return TimeZoneInfo.ConvertTime(utc, TimeZoneInfo.FindSystemTimeZoneById(place.TimeZone)); }
        catch (TimeZoneNotFoundException) { return null; }
        catch (InvalidTimeZoneException) { return null; }
        catch (ArgumentException) { return null; }
    }
    public static string Temperature(double celsius, bool fahrenheit) =>
        (fahrenheit ? celsius * 9 / 5 + 32 : celsius).ToString("0", CultureInfo.CurrentCulture) + (fahrenheit ? "\u00b0F" : "\u00b0C");
    public static string WeatherDescription(int? code) => code switch
    {
        0 => "Clear sky", 1 => "Mainly clear", 2 => "Partly cloudy", 3 => "Overcast",
        45 or 48 => "Fog", 51 or 53 or 55 => "Drizzle", 56 or 57 => "Freezing drizzle",
        61 or 63 or 65 => "Rain", 66 or 67 => "Freezing rain", 71 or 73 or 75 => "Snow",
        77 => "Snow grains", 80 or 81 or 82 => "Rain showers", 85 or 86 => "Snow showers",
        95 => "Thunderstorm", 96 or 99 => "Thunderstorm with hail", _ => "Conditions unavailable"
    };
    public static double DistanceKm(double lat1, double lon1, double lat2, double lon2)
    {
        double dLat = (lat2 - lat1) * Math.PI / 180, dLon = (lon2 - lon1) * Math.PI / 180;
        double a = Math.Pow(Math.Sin(dLat / 2), 2) + Math.Cos(lat1 * Math.PI / 180)
            * Math.Cos(lat2 * Math.PI / 180) * Math.Pow(Math.Sin(dLon / 2), 2);
        return 6371 * 2 * Math.Asin(Math.Sqrt(Math.Clamp(a, 0, 1)));
    }

    private sealed record CacheEntry(int Version, DateTimeOffset SavedUtc, byte[] Bytes);
    private async Task<PlaceSource<T>> FetchAsync<T>(string url, Func<byte[], T> parse,
        TimeSpan freshFor, TimeSpan usableFor, bool force, CancellationToken cancellation,
        int maxBytes = 2 * 1024 * 1024) where T : class
    {
        cancellation.ThrowIfCancellationRequested();
        string file = Path.Combine(_cacheDirectory, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(url))) + ".json");
        // The first asynchronous hop moves both disk I/O and JSON decoding off the dispatcher.
        var cacheRead = await Task.Run(() =>
        {
            CacheEntry? entry = ReadCache(file, usableFor, maxBytes);
            T? data = null;
            if (entry != null)
            {
                try { data = parse(entry.Bytes); }
                catch (Exception ex) when (IsDataError(ex)) { entry = null; }
            }
            return (Entry: entry, Data: data);
        }, cancellation).ConfigureAwait(false);
        CacheEntry? cached = cacheRead.Entry;
        T? previous = cacheRead.Data;
        if (!force && cached != null && _clock() - cached.SavedUtc <= freshFor)
            return new PlaceSource<T>(previous, cached.SavedUtc, true, false);
        try
        {
            bool wikiApi = IsHttpsHost(url, "en.wikipedia.org", "commons.wikimedia.org");
            if (wikiApi && _spacePublicRequests)
                await SpaceWikiRequestAsync(cancellation).ConfigureAwait(false);
            using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            timeout.CancelAfter(TimeSpan.FromSeconds(14));
            await NetworkSlots.WaitAsync(timeout.Token).ConfigureAwait(false);
            byte[] bytes;
            try
            {
                using HttpResponseMessage response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
                if (wikiApi && _spacePublicRequests && (response.StatusCode == HttpStatusCode.TooManyRequests || response.StatusCode == HttpStatusCode.ServiceUnavailable))
                {
                    DateTimeOffset retry = response.Headers.RetryAfter?.Date
                        ?? DateTimeOffset.UtcNow.Add(response.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(60));
                    await WikiRequestGate.WaitAsync(cancellation).ConfigureAwait(false);
                    try { if (retry > _wikiRetryAfter) _wikiRetryAfter = retry; }
                    finally { WikiRequestGate.Release(); }
                }
                response.EnsureSuccessStatusCode();
                if (response.Content.Headers.ContentLength > maxBytes) throw new IOException("Provider response too large");
                using Stream input = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
                using MemoryStream output = new();
                byte[] buffer = new byte[16384];
                int count;
                while ((count = await input.ReadAsync(buffer.AsMemory(), timeout.Token).ConfigureAwait(false)) > 0)
                {
                    if (output.Length + count > maxBytes) throw new IOException("Provider response too large");
                    output.Write(buffer, 0, count);
                }
                bytes = output.ToArray();
            }
            finally { NetworkSlots.Release(); }
            T data = await Task.Run(() => parse(bytes), cancellation).ConfigureAwait(false);
            cancellation.ThrowIfCancellationRequested();
            DateTimeOffset now = _clock();
            await Task.Run(() => WriteCache(file, new CacheEntry(1, now, bytes)), cancellation).ConfigureAwait(false);
            return new PlaceSource<T>(data, now, false, false);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { throw; }
        catch (Exception ex) when (ex is HttpRequestException or IOException or OperationCanceledException || IsDataError(ex))
        {
            return new PlaceSource<T>(previous, cached?.SavedUtc, cached != null, cached != null,
                ex is JsonException ? "Source data unavailable" : ex is HttpRequestException { StatusCode: HttpStatusCode.TooManyRequests }
                    ? "Source temporarily rate limited; retry later" : "Could not reach source");
        }
    }

    private static async Task SpaceWikiRequestAsync(CancellationToken cancellation)
    {
        await WikiRequestGate.WaitAsync(cancellation).ConfigureAwait(false);
        try
        {
            if (DateTimeOffset.UtcNow < _wikiRetryAfter)
                throw new HttpRequestException("Source cooling down", null, HttpStatusCode.TooManyRequests);
            TimeSpan wait = _nextWikiRequest - DateTimeOffset.UtcNow;
            if (wait > TimeSpan.Zero) await Task.Delay(wait, cancellation).ConfigureAwait(false);
            // No account/contact identity is assumed: stay below the unidentified 10 API calls/min.
            // An injected HttpClient is caller-managed; the built-in public client uses this gate.
            _nextWikiRequest = DateTimeOffset.UtcNow.AddSeconds(7);
        }
        finally { WikiRequestGate.Release(); }
    }

    private CacheEntry? ReadCache(string file, TimeSpan usableFor, int maxBytes)
    {
        try
        {
            FileInfo info = new(file);
            if (!info.Exists || info.Length > maxBytes * 1.5 + 1024) return null;
            CacheEntry? entry = JsonSerializer.Deserialize<CacheEntry>(File.ReadAllBytes(file));
            return entry is { Version: 1, Bytes: not null } && entry.Bytes.Length <= maxBytes
                && entry.SavedUtc <= _clock().AddMinutes(5) && _clock() - entry.SavedUtc <= usableFor ? entry : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return null; }
    }
    private void WriteCache(string file, CacheEntry entry)
    {
        string temp = file + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            Directory.CreateDirectory(_cacheDirectory);
            File.WriteAllBytes(temp, JsonSerializer.SerializeToUtf8Bytes(entry));
            File.Move(temp, file, true);
            long total = 0; int count = 0;
            foreach (FileInfo info in new DirectoryInfo(_cacheDirectory).EnumerateFiles("*.json").OrderByDescending(f => f.LastWriteTimeUtc))
            {
                total += info.Length;
                if (++count > 128 || total > 64L * 1024 * 1024) { try { info.Delete(); } catch (IOException) { } }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        finally { try { File.Delete(temp); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { } }
    }

    private static bool IsDataError(Exception ex) => ex is JsonException or InvalidOperationException or FormatException or RegexMatchTimeoutException
        or KeyNotFoundException or ArgumentException or OverflowException;
    private static void CheckApiError(JsonElement root)
    {
        if (root.TryGetProperty("error", out JsonElement e) && e.ValueKind != JsonValueKind.False) throw new JsonException("Provider API error");
    }
    private static IEnumerable<JsonElement> Pages(JsonElement root) => root.TryGetProperty("query", out JsonElement query)
        && query.TryGetProperty("pages", out JsonElement pages) && pages.ValueKind == JsonValueKind.Array
        ? pages.EnumerateArray().ToArray() : Array.Empty<JsonElement>();
    private static string Text(JsonElement p, string key) => p.ValueKind == JsonValueKind.Object
        && p.TryGetProperty(key, out JsonElement v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
    private static double? OptionalNumber(JsonElement p, string key) => p.TryGetProperty(key, out JsonElement v)
        && v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out double n) && double.IsFinite(n) ? n : null;
    private static string Metadata(JsonElement meta, string key)
    {
        if (!meta.TryGetProperty(key, out JsonElement value)) return "";
        // Only API metadata is reduced to plain text. No provider HTML pages are scraped/rendered.
        return WebUtility.HtmlDecode(Regex.Replace(Text(value, "value"), "<[^>]*>", " ", RegexOptions.None,
            TimeSpan.FromMilliseconds(100))).Trim();
    }
    internal static bool IsImageUrl(string url) => IsHttpsHost(url, "upload.wikimedia.org", "thumb.wikimedia.org");
    internal static bool IsHttpsHost(string url, params string[] hosts) => Uri.TryCreate(url, UriKind.Absolute, out Uri? uri)
        && uri.Scheme == "https" && uri.IsDefaultPort && uri.UserInfo.Length == 0 && hosts.Contains(uri.Host, StringComparer.OrdinalIgnoreCase);
    private static string Number(double n) => n.ToString("0.#####", Inv);
    private static bool ValidCoordinates(double lat, double lon) => double.IsFinite(lat) && double.IsFinite(lon) && lat is >= -90 and <= 90 && lon is >= -180 and <= 180;
    private static void ValidateCoordinates(double lat, double lon)
    {
        if (!ValidCoordinates(lat, lon)) throw new ArgumentOutOfRangeException(nameof(lat), "Invalid place coordinates");
    }
}
