#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Win81Layer;

// Two-way link with MetroBrowser: reads the browser's own data (user bookmarks + history) and the Chrome bookmarks
// MetroBrowser mirrors/shows, so the launcher's Smart Search can surface them. Read-only local files, no network.
// Results open through WebOpen (i.e. in MetroBrowser). Briefly cached so typing stays snappy.
public static class BrowserData
{
	public readonly record struct Entry(string Title, string Url, bool Bookmark);

	private static readonly object Gate = new object();
	private static List<Entry>? _cache;
	private static DateTime _cacheAtUtc;

	private static string MetroDataDir()
		=> Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "metro-browser");

	public static IReadOnlyList<Entry> All()
	{
		lock (Gate)
		{
			if (_cache != null && (DateTime.UtcNow - _cacheAtUtc).TotalSeconds < 20)
			{
				return _cache;
			}
			List<Entry> list = new List<Entry>();
			HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
			void Add(string? title, string? url, bool bm)
			{
				if (string.IsNullOrWhiteSpace(url)) return;
				if (!(url!.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))) return;
				if (!seen.Add(url)) return;
				list.Add(new Entry(string.IsNullOrWhiteSpace(title) ? url : title!.Trim(), url, bm));
			}
			// 1) MetroBrowser's own user bookmarks
			ReadArray(Path.Combine(MetroDataDir(), "bookmarks.json"), el => Add(Str(el, "title"), Str(el, "url"), bm: true));
			// 2) Chrome bookmarks (the set MetroBrowser mirrors & shows)
			foreach ((string t, string u) in ChromeBookmarks())
			{
				Add(t, u, bm: true);
			}
			// 3) MetroBrowser history (newest first)
			ReadArray(Path.Combine(MetroDataDir(), "history.json"), el => Add(Str(el, "title"), Str(el, "url"), bm: false));
			_cache = list;
			_cacheAtUtc = DateTime.UtcNow;
			return list;
		}
	}

	public static List<Entry> Search(string query, int max = 6)
	{
		string q = (query ?? "").Trim();
		if (q.Length < 2) return new List<Entry>();
		string ql = q.ToLowerInvariant();
		string[] toks = ql.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
		List<(int score, Entry e)> scored = new List<(int, Entry)>();
		foreach (Entry e in All())
		{
			string hay = (e.Title + " " + e.Url).ToLowerInvariant();
			if (!toks.All(t => hay.Contains(t))) continue;
			int score = e.Bookmark ? 100 : 0;
			if (e.Title.ToLowerInvariant().StartsWith(toks[0])) score += 50;
			if (hay.Contains(ql)) score += 25;
			scored.Add((score, e));
		}
		return scored.OrderByDescending(x => x.score).Take(max).Select(x => x.e).ToList();
	}

	public static string Host(string url)
	{
		try { return new Uri(url).Host.Replace("www.", ""); } catch { return url; }
	}

	private static void ReadArray(string path, Action<JsonElement> onItem)
	{
		try
		{
			if (!File.Exists(path)) return;
			using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(path));
			if (doc.RootElement.ValueKind != JsonValueKind.Array) return;
			foreach (JsonElement el in doc.RootElement.EnumerateArray())
			{
				onItem(el);
			}
		}
		catch
		{
		}
	}

	private static IEnumerable<(string, string)> ChromeBookmarks()
	{
		List<(string, string)> outp = new List<(string, string)>();
		try
		{
			string? file = ChromeBookmarksFile();
			if (file == null || !File.Exists(file)) return outp;
			using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(file));
			if (!doc.RootElement.TryGetProperty("roots", out JsonElement roots)) return outp;
			foreach (string rootName in new[] { "bookmark_bar", "other", "synced" })
			{
				if (roots.TryGetProperty(rootName, out JsonElement r)) Walk(r, outp);
			}
		}
		catch
		{
		}
		return outp;
	}

	private static void Walk(JsonElement node, List<(string, string)> outp)
	{
		try
		{
			string? type = Str(node, "type");
			if (type == "url")
			{
				string? url = Str(node, "url");
				if (url != null && (url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)))
				{
					outp.Add((Str(node, "name") ?? url, url));
				}
			}
			else if (node.TryGetProperty("children", out JsonElement kids) && kids.ValueKind == JsonValueKind.Array)
			{
				foreach (JsonElement k in kids.EnumerateArray())
				{
					Walk(k, outp);
				}
			}
		}
		catch
		{
		}
	}

	private static string? ChromeBookmarksFile()
	{
		try
		{
			string? baseDir = Environment.GetEnvironmentVariable("LOCALAPPDATA");
			if (string.IsNullOrEmpty(baseDir)) return null;
			string udir = Path.Combine(baseDir, "Google", "Chrome", "User Data");
			List<string> cands = new List<string> { "Default" };
			try
			{
				cands.AddRange(Directory.GetDirectories(udir)
					.Select(Path.GetFileName)
					.Where(n => n != null && Regex.IsMatch(n!, "^Profile \\d+$"))!);
			}
			catch
			{
			}
			foreach (string c in cands)
			{
				string p = Path.Combine(udir, c, "Bookmarks");
				if (File.Exists(p)) return p;
			}
		}
		catch
		{
		}
		return null;
	}

	private static string? Str(JsonElement e, string prop)
		=> (e.ValueKind == JsonValueKind.Object && e.TryGetProperty(prop, out JsonElement v) && v.ValueKind == JsonValueKind.String) ? v.GetString() : null;
}
