#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.ServiceModel.Syndication;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;

namespace Win81Layer;

internal static class NewsFeedService
{
	internal const string DefaultUrl = "https://www.naftemporiki.gr/feed/";
	internal sealed record Source(string Name, string Url);
	internal static readonly Source[] Sources = {
		new("Naftemporiki", DefaultUrl),
		new("BBC News", "https://feeds.bbci.co.uk/news/rss.xml"),
		new("BBC Technology", "https://feeds.bbci.co.uk/news/technology/rss.xml")
	};
	internal sealed record Article(string Title, string Url, string Summary, DateTimeOffset? Published);
	internal sealed record Feed(string Url, string Title, string HomeUrl, Article[] Articles, DateTimeOffset FetchedAt, bool Stale = false, string? Error = null);
	private static readonly HttpClient Http = new(new HttpClientHandler { AllowAutoRedirect = false, AutomaticDecompression = DecompressionMethods.All }) { Timeout = TimeSpan.FromSeconds(15) };
	private static readonly SemaphoreSlim FetchGate = new(1, 1);
	private static readonly Regex Tags = new("<[^>]*>", RegexOptions.Compiled, TimeSpan.FromMilliseconds(100));
	private static readonly Regex Spaces = new("\\s+", RegexOptions.Compiled, TimeSpan.FromMilliseconds(100));

	internal static string? NormalizeUrl(string? value)
	{
		if (!Uri.TryCreate(value?.Trim(), UriKind.Absolute, out Uri? uri) || !IsWebUri(uri)) return null;
		if ((uri.Host == "naftemporiki.gr" || uri.Host == "www.naftemporiki.gr") && uri.AbsolutePath == "/") return DefaultUrl;
		return uri.AbsoluteUri;
	}
	internal static bool IsWebUri(Uri uri) => uri.IsAbsoluteUri && uri.Scheme is "https" or "http" && !uri.IsLoopback && string.IsNullOrEmpty(uri.UserInfo) && uri.IsDefaultPort;
	internal static bool PublicAddress(IPAddress address)
	{
		if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
		if (IPAddress.IsLoopback(address) || address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any)) return false;
		byte[] b = address.GetAddressBytes();
		if (b.Length == 16) return !address.IsIPv6LinkLocal && !address.IsIPv6SiteLocal && !address.IsIPv6Multicast && (b[0] & 0xfe) != 0xfc;
		return b[0] is not (0 or 10 or 127) && b[0] < 224 && !(b[0] == 169 && b[1] == 254) && !(b[0] == 172 && b[1] >= 16 && b[1] <= 31) && !(b[0] == 192 && b[1] == 168) && !(b[0] == 100 && b[1] >= 64 && b[1] <= 127);
	}
	internal static Feed? Cached(string url)
	{
		Feed? feed = LiveTileDataCache.Load<Feed>("news:" + url);
		return feed?.Url == url && feed.Articles != null ? feed with { Stale = DateTimeOffset.UtcNow - feed.FetchedAt > TimeSpan.FromMinutes(15) } : null;
	}
	internal static async Task<Feed> GetAsync(string url, bool force = false, CancellationToken cancellation = default)
	{
		url = NormalizeUrl(url) ?? throw new ArgumentException("Enter a public HTTP(S) RSS or Atom address.");
		using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
		timeout.CancelAfter(TimeSpan.FromSeconds(20));
		await FetchGate.WaitAsync(timeout.Token).ConfigureAwait(false);
		try
		{
			Feed? cached = Cached(url);
			if (!force && cached != null && DateTimeOffset.UtcNow - cached.FetchedAt < TimeSpan.FromMinutes(10)) return cached;
			try
			{
				byte[] body = await FetchBoundedAsync(new Uri(url), timeout.Token).ConfigureAwait(false);
				Feed feed = await Task.Run(() => Parse(body, url), timeout.Token).ConfigureAwait(false);
				LiveTileDataCache.Save("news:" + url, feed);
				return feed;
			}
			catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { throw; }
			catch (Exception ex)
			{
				Logger.Log("News refresh: " + ex.GetType().Name);
				if (cached != null) return cached with { Stale = true, Error = "Offline data" };
				throw new InvalidOperationException("News unavailable. Check the RSS address or connection.", ex);
			}
		}
		finally { FetchGate.Release(); }
	}
	private static async Task<byte[]> FetchBoundedAsync(Uri uri, CancellationToken cancellation)
	{
		for (int redirect = 0; redirect < 5; redirect++)
		{
			if (!IsWebUri(uri)) throw new InvalidOperationException("Unsupported feed address.");
			IPAddress[] addresses = await Dns.GetHostAddressesAsync(uri.DnsSafeHost, cancellation).ConfigureAwait(false);
			if (addresses.Length == 0 || addresses.Any(a => !PublicAddress(a))) throw new InvalidOperationException("Private network feeds are not supported.");
			using HttpRequestMessage request = new(HttpMethod.Get, uri);
			request.Headers.UserAgent.ParseAdd("Win81Layer/1.0 RSSReader");
			request.Headers.Accept.ParseAdd("application/rss+xml, application/atom+xml, application/xml, text/xml");
			using HttpResponseMessage response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellation).ConfigureAwait(false);
			if ((int)response.StatusCode is >= 300 and < 400 && response.Headers.Location != null)
			{
				uri = new Uri(uri, response.Headers.Location);
				continue;
			}
			response.EnsureSuccessStatusCode();
			if (response.Content.Headers.ContentLength > 2_000_000) throw new InvalidOperationException("Feed exceeds 2 MB.");
			using Stream stream = await response.Content.ReadAsStreamAsync(cancellation).ConfigureAwait(false);
			using MemoryStream data = new();
			byte[] buffer = new byte[16384];
			int read;
			while ((read = await stream.ReadAsync(buffer, cancellation).ConfigureAwait(false)) > 0)
			{
				if (data.Length + read > 2_000_000) throw new InvalidOperationException("Feed exceeds 2 MB.");
				data.Write(buffer, 0, read);
			}
			return data.ToArray();
		}
		throw new InvalidOperationException("Too many feed redirects.");
	}
	internal static Feed Parse(byte[] body, string url)
	{
		using MemoryStream stream = new(body);
		using XmlReader reader = XmlReader.Create(stream, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 2_000_000 });
		SyndicationFeed feed = SyndicationFeed.Load(reader) ?? throw new InvalidDataException("Not an RSS or Atom feed.");
		Uri root = new(url);
		string? WebLink(IEnumerable<SyndicationLink> links) => links.Where(l => l.RelationshipType is null or "alternate")
			.Select(l => Uri.TryCreate(root, l.Uri?.ToString(), out Uri? u) && IsWebUri(u) ? u.AbsoluteUri : null).FirstOrDefault(u => u != null);
		Article[] articles = feed.Items.Take(100).Select(item => new Article(
			Clean(item.Title?.Text, 300), WebLink(item.Links) ?? "", Clean(item.Summary?.Text, 500),
			item.PublishDate == DateTimeOffset.MinValue ? null : item.PublishDate))
			.Where(a => a.Title.Length > 0 && a.Url.Length > 0).DistinctBy(a => a.Url).Take(30).ToArray();
		return new Feed(url, Clean(feed.Title?.Text, 100), WebLink(feed.Links) ?? root.GetLeftPart(UriPartial.Authority), articles, DateTimeOffset.UtcNow);
	}
	internal static string Clean(string? text, int max)
	{
		string value = Spaces.Replace(WebUtility.HtmlDecode(Tags.Replace(text ?? "", " ")), " ").Trim();
		return value.Length <= max ? value : value.Substring(0, max);
	}
}
