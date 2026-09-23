#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace Win81Layer;

internal static class MetroTileProbe
{
	private static readonly string Root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Win81Layer", "qa", "metro-tiles");
	private static readonly TileSize[] Sizes = { TileSize.Small, TileSize.Medium, TileSize.Wide, TileSize.Large };
	internal static void Begin(Application app)
	{
		app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
		app.Dispatcher.BeginInvoke(new Action(async () =>
		{
			Directory.CreateDirectory(Root);
			try { bool passed = await RunAsync(); app.Shutdown(passed ? 0 : 2); }
			catch (Exception ex) { File.WriteAllText(Path.Combine(Root, "ERROR.txt"), ex.ToString()); app.Shutdown(3); }
		}), DispatcherPriority.Background);
	}
	private static Dictionary<string, string> StateHashes() => new[] { "settings.json", "profile.json", "taskbar-pins.json", "shell-profile-state.json" }
		.ToDictionary(f => f, f => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Win81Layer", f)))));
	private static async Task<bool> RunAsync()
	{
		var before = StateHashes();
		Dictionary<string, bool> checks = new();
		List<object> samples = new();
		DateTimeOffset day = new(2026, 9, 6, 12, 45, 0, TimeSpan.FromHours(3));
		var agenda = new GoogleServices.AgendaSnapshot(new[] {
			new GoogleServices.AgendaEvent("Συνάντηση ομάδας: σχεδιασμός της επόμενης εβδομάδας", day.AddHours(1), day.AddHours(2), false, "Athens"),
			new GoogleServices.AgendaEvent("Project review and planning", day.AddDays(1), day.AddDays(1).AddHours(1), false, "") }, DateTimeOffset.UtcNow);
		var news = new NewsFeedService.Feed("https://qa.example/feed", "TEST SOURCE / Δοκιμαστική πηγή", "https://qa.example/", new[] {
			new NewsFeedService.Article("Δοκιμαστικός τίτλος: η νέα ημέρα στην οικονομία και την τεχνολογία", "https://qa.example/article", "Synthetic QA content only. Ελληνικά, μεγάλοι τίτλοι και ασφαλής αναδίπλωση κειμένου στα Metro tiles.", day) }, DateTimeOffset.UtcNow);
		await Task.Run(MetroLiveTileArt.Preload);
		var clockDay = MetroLiveTileArt.Build(LiveKind.Clock, TileSize.Large, day);
		var clockNight = MetroLiveTileArt.Build(LiveKind.Clock, TileSize.Large, day.AddHours(10));
		var agendaDay = MetroLiveTileArt.Build(LiveKind.Agenda, TileSize.Large, day);
		var agendaNight = MetroLiveTileArt.Build(LiveKind.Agenda, TileSize.Large, day.AddHours(10));
		checks["ClockBackgroundDoesNotChangeWithTime"] = ReferenceEquals(clockDay.Background, clockNight.Background);
		checks["AgendaBackgroundDoesNotChangeWithTime"] = ReferenceEquals(agendaDay.Background, agendaNight.Background);
		checks["DistinctClockAndAgendaBackgrounds"] = !ReferenceEquals(clockDay.Background, agendaDay.Background);
		checks["StationaryBackgroundsNeverAnimate"] = !clockDay.AnimateBackground && !agendaDay.AnimateBackground;
		checks["StationaryBackgroundsAreRealBitmapAssets"] = clockDay.Background is BitmapSource c && c.PixelWidth == 640 && agendaDay.Background is BitmapSource a && a.PixelWidth == 640;
		foreach (LiveKind kind in new[] { LiveKind.Clock, LiveKind.Agenda, LiveKind.News })
		{
			List<(string Label, BitmapSource Image)> atlas = new();
			foreach (bool night in new[] { false, true })
			foreach (TileSize size in Sizes)
			{
				DateTimeOffset time = night ? day.AddHours(10) : day;
				MetroTileVisual visual = MetroLiveTileArt.Build(kind, size, time, night ? agenda with { Stale = true } : agenda, true, night ? news with { Stale = true } : news);
				Stopwatch watch = Stopwatch.StartNew();
				BitmapSource bitmap = Render(visual);
				string name = $"{kind}-{size}-{(night ? "NIGHT-OFFLINE" : "DAY")}-TEST-DATA.png";
				Save(bitmap, name);
				checks[name] = NonBlank(bitmap) && visual.AccessibleName.Length > 10;
				samples.Add(new { File = name, bitmap.PixelWidth, bitmap.PixelHeight, RenderMs = watch.ElapsedMilliseconds });
				atlas.Add(($"{size} / {(night ? "night or offline" : "day")}", bitmap));
			}
			SaveAtlas(kind + " / SYNTHETIC TEST DATA", atlas, kind + "-ALL-SIZES-TEST-DATA.png");
			StartScreen start = new(transitionDiagnostics: true);
			try
			{
				var result = await start.QaMetroFlipsAsync(kind, size => MetroLiveTileArt.Build(kind, size, day, agenda, true, news));
				foreach (var check in result.Checks) checks[kind + ":" + check.Key] = check.Value;
				foreach (var logo in result.Logos) Save(logo.Value, $"{kind}-{logo.Key}-LOGO-TEST-DATA.png");
			}
			finally { start.Close(); }
			foreach (double dpi in new[] { 144d, 192d })
			{
				BitmapSource bitmap = Render(MetroLiveTileArt.Build(kind, TileSize.Large, day, agenda, true, news), dpi);
				string file = $"{kind}-Large-{dpi}-DPI-TEST-DATA.png";
				Save(bitmap, file); checks[file] = NonBlank(bitmap) && bitmap.PixelWidth == (int)(310 * TileMetrics.Scale * dpi / 96);
			}
		}
		foreach ((string name, bool signedIn, GoogleServices.AgendaSnapshot? data) in new[] {
			("SIGNED-OUT", false, (GoogleServices.AgendaSnapshot?)null), ("EMPTY", true, new GoogleServices.AgendaSnapshot(Array.Empty<GoogleServices.AgendaEvent>(), DateTimeOffset.UtcNow)), ("ERROR", true, (GoogleServices.AgendaSnapshot?)null) })
		{
			var visual = MetroLiveTileArt.Build(LiveKind.Agenda, TileSize.Large, day, data, signedIn, unavailable: true);
			Save(Render(visual), "Agenda-" + name + "-TEST-DATA.png");
			checks["Agenda:" + name] = visual.AccessibleName.Contains(name == "SIGNED-OUT" ? "Sign in" : name == "EMPTY" ? "No upcoming" : "unavailable");
		}
		checks["NaftemporikiHomepageNormalized"] = NewsFeedService.NormalizeUrl("https://www.naftemporiki.gr/") == NewsFeedService.DefaultUrl;
		checks["InvalidSchemeRejected"] = NewsFeedService.NormalizeUrl("file:///C:/Windows/win.ini") == null && NewsFeedService.NormalizeUrl("javascript:alert(1)") == null;
		checks["PrivateAddressesRejected"] = new[] { "127.0.0.1", "10.0.0.1", "192.168.1.1", "169.254.169.254", "::1", "fc00::1" }.All(a => !NewsFeedService.PublicAddress(IPAddress.Parse(a)));
		string rss = "<rss version='2.0'><channel><title>QA</title><link>https://example.org/</link><description>QA</description><item><title>Greek Ελληνικά &amp; test</title><link>https://example.org/1</link><description>&lt;b&gt;Text&lt;/b&gt;</description></item><item><title>Duplicate</title><link>https://example.org/1</link></item><item><title>Unsafe</title><link>javascript:alert(1)</link></item></channel></rss>";
		var parsed = NewsFeedService.Parse(Encoding.UTF8.GetBytes(rss), "https://example.org/feed");
		checks["RssParsingDedupAndSafeLinks"] = parsed.Articles.Length == 1 && parsed.Articles[0].Title == "Greek Ελληνικά & test" && parsed.Articles[0].Summary == "Text";
		string atom = "<feed xmlns='http://www.w3.org/2005/Atom'><title>Atom</title><id>urn:feed</id><updated>2026-09-06T10:00:00Z</updated><entry><title>Atom title</title><id>urn:one</id><updated>2026-09-06T10:00:00Z</updated><link href='https://example.org/atom'/></entry></feed>";
		checks["AtomParsing"] = NewsFeedService.Parse(Encoding.UTF8.GetBytes(atom), "https://example.org/feed").Articles.Length == 1;
		try { NewsFeedService.Parse(Encoding.UTF8.GetBytes("<!DOCTYPE rss [<!ENTITY x SYSTEM 'file:///C:/Windows/win.ini'>]><rss version='2.0'><channel><title>&x;</title></channel></rss>"), "https://example.org/feed"); checks["DtdRejected"] = false; }
		catch (System.Xml.XmlException) { checks["DtdRejected"] = true; }
		string cacheKey = "qa:" + Guid.NewGuid().ToString("N");
		try
		{
			LiveTileDataCache.Save(cacheKey, agenda, encrypted: true);
			checks["EncryptedAgendaCacheRoundTrip"] = LiveTileDataCache.Load<GoogleServices.AgendaSnapshot>(cacheKey, encrypted: true)?.Events[0].Title == agenda.Events[0].Title;
			checks["AgendaCacheNotPlainJson"] = LiveTileDataCache.Load<GoogleServices.AgendaSnapshot>(cacheKey) == null;
		}
		finally { LiveTileDataCache.Delete(cacheKey); }
		using (JsonDocument events = JsonDocument.Parse("[{\"summary\":\"All day\",\"start\":{\"date\":\"2026-09-06\"},\"end\":{\"date\":\"2026-09-07\"}},{\"summary\":\"Cancelled\",\"status\":\"cancelled\",\"start\":{\"date\":\"2026-09-06\"},\"end\":{\"date\":\"2026-09-07\"}}]"))
		{
			var result = GoogleServices.ParseAgenda(events.RootElement, day);
			checks["AgendaAllDayAndCancellation"] = result.Events.Length == 1 && result.Events[0].AllDay && result.Events[0].End.Date == day.Date.AddDays(1);
		}
		await CheckSourceRaceAsync(checks, news);
		Stopwatch network = Stopwatch.StartNew();
		NewsFeedService.Feed? live = null;
		string? providerError = null;
		try { live = await NewsFeedService.GetAsync(NewsFeedService.DefaultUrl, force: true); }
		catch (Exception ex) { providerError = ex.Message; }
		network.Stop();
		if (live != null)
		{
			Save(Render(MetroLiveTileArt.Build(LiveKind.News, TileSize.Large, DateTimeOffset.Now, news: live)), "News-Large-LIVE-DATA.png");
			checks["LiveNaftemporikiFeed"] = live.Articles.Length > 0 && live.Title.Contains("ΝΑΥΤΕΜΠΟΡΙΚΗ", StringComparison.OrdinalIgnoreCase);
			checks["NewsDiskCache"] = NewsFeedService.Cached(NewsFeedService.DefaultUrl)?.Articles.FirstOrDefault()?.Url == live.Articles[0].Url;
		}
		else checks["LiveNaftemporikiFeed"] = false;
		checks["MutableStateUnchanged"] = before.OrderBy(x => x.Key).SequenceEqual(StateHashes().OrderBy(x => x.Key));
		bool passed = checks.Values.All(v => v);
		File.WriteAllText(Path.Combine(Root, "METRO-TILES-QA-LATEST.json"), JsonSerializer.Serialize(new {
			SchemaVersion = 1, GeneratedUtc = DateTimeOffset.UtcNow, Passed = passed,
			LauncherDllSha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Win81Layer.dll")))),
			Checks = checks, Samples = samples, Provider = new { Url = NewsFeedService.DefaultUrl, Articles = live?.Articles.Length, ElapsedMs = network.ElapsedMilliseconds, Error = providerError },
			GoogleAccountSignedIn = GoogleAuth.IsSignedIn, AgendaRuntimeScope = "synthetic events, parser and protected cache; no real calendar request in this isolated probe",
			BeforeState = before, AfterState = StateHashes()
		}, new JsonSerializerOptions { WriteIndented = true }));
		return passed;
	}
	private static async Task CheckSourceRaceAsync(Dictionary<string, bool> checks, NewsFeedService.Feed fixture)
	{
		TileVm tile = LiveTiles.Create(LiveKind.News);
		using LiveTileService service = new(() => new[] { tile }, "Piraeus", "C");
		while (!service.MetroReady) await Task.Delay(20);
		TaskCompletionSource<NewsFeedService.Feed> old = new();
		int calls = 0;
		service.NewsFetcher = (url, _) => { calls++; return url == "https://old.example/feed" ? old.Task : Task.FromResult(fixture with { Url = url }); };
		service.SetNewsSource("https://old.example/feed"); service.Start();
		for (int i = 0; i < 50 && calls == 0; i++) await Task.Delay(20);
		service.SetNewsSource("https://new.example/feed");
		old.SetResult(fixture with { Url = "https://old.example/feed" });
		await Task.Delay(80);
		checks["OldSourceResponseDiscarded"] = service.CurrentNews == null;
		service.Update(); await Task.Delay(80);
		checks["NewSourceApplied"] = service.CurrentNews?.Url == "https://new.example/feed";
		service.Stop();
		checks["StoppedTileMotionDisabled"] = !tile.MetroMotionEnabled;
		int stoppedCalls = calls; service.Update(); await Task.Delay(50);
		checks["ClosedStartDoesNotFetchNews"] = calls == stoppedCalls;
	}
	private static BitmapSource Render(MetroTileVisual visual, double dpi = 96)
	{
		MetroTilePresenter presenter = new() { Visual = visual, Width = visual.Width, Height = visual.Height, MotionEnabled = false };
		presenter.Measure(new Size(visual.Width, visual.Height)); presenter.Arrange(new Rect(0, 0, visual.Width, visual.Height)); presenter.UpdateLayout();
		RenderTargetBitmap bitmap = new((int)Math.Round(visual.Width * dpi / 96), (int)Math.Round(visual.Height * dpi / 96), dpi, dpi, PixelFormats.Pbgra32);
		bitmap.Render(presenter); bitmap.Freeze(); return bitmap;
	}
	private static bool NonBlank(BitmapSource bitmap)
	{
		byte[] pixels = new byte[bitmap.PixelWidth * bitmap.PixelHeight * 4]; bitmap.CopyPixels(pixels, bitmap.PixelWidth * 4, 0);
		int white = 0, opaque = 0;
		for (int i = 0; i < pixels.Length; i += 4) { if (pixels[i + 3] > 240) opaque++; if (pixels[i] > 225 && pixels[i + 1] > 225 && pixels[i + 2] > 225) white++; }
		return opaque > bitmap.PixelWidth * bitmap.PixelHeight * .99 && white > 15;
	}
	private static void Save(BitmapSource bitmap, string file)
	{
		PngBitmapEncoder encoder = new(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
		using FileStream stream = File.Create(Path.Combine(Root, file)); encoder.Save(stream);
	}
	private static void SaveAtlas(string title, List<(string Label, BitmapSource Image)> tiles, string file)
	{
		DrawingVisual visual = new();
		using (DrawingContext dc = visual.RenderOpen())
		{
			dc.DrawRectangle(Brushes.Black, null, new Rect(0, 0, 930, 755));
			void Label(string value, double x, double y) => dc.DrawText(new FormattedText(value, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), 13, Brushes.White, 1), new Point(x, y));
			Label(title, 16, 12);
			for (int row = 0; row < 2; row++)
			{
				double x = 16;
				for (int col = 0; col < 4; col++)
				{
					var tile = tiles[row * 4 + col]; double y = 44 + row * 350;
					dc.DrawImage(tile.Image, new Rect(x, y, tile.Image.Width, tile.Image.Height)); Label(tile.Label, x, y + tile.Image.Height + 6); x += tile.Image.Width + 16;
				}
			}
		}
		RenderTargetBitmap bitmap = new(930, 755, 96, 96, PixelFormats.Pbgra32); bitmap.Render(visual); Save(bitmap, file);
	}
}
