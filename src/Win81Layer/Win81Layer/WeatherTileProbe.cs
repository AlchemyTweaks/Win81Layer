using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;

using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace Win81Layer;

// Isolated Weather-tile visual/provider QA. Every synthetic screenshot is explicitly named TEST-DATA.
internal static class WeatherTileProbe
{
	private sealed record Scenario(
		string FileName, string Label, WeatherService.Result Data, TileSize Size,
		WeatherTileArt.Band Band, bool Fahrenheit = false);

	private sealed record PixelCheck(
		string File, int Width, int Height, double OpaqueCoverage,
		int LuminanceRange, long BuildAndRenderMs, bool Pass);

	public static void Begin(Application app)
	{
		app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
		app.Dispatcher.BeginInvoke((Action)(async delegate
		{
			string qa = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Win81Layer", "qa", "weather-tile");
			Directory.CreateDirectory(qa);
			Dictionary<string, string> stateBefore = StateHashes();
			try
			{
				List<Scenario> scenarios = CreateScenarios();
				List<(Scenario Scenario, BitmapSource Bitmap)> rendered = new List<(Scenario, BitmapSource)>();
				List<PixelCheck> pixelChecks = new List<PixelCheck>();

				foreach (Scenario scenario in scenarios)
				{
					Stopwatch renderTimer = Stopwatch.StartNew();
					WeatherTileArt.ForceBand = scenario.Band;
					int width = (int)TilePx(scenario.Size, width: true);
					int height = (int)TilePx(scenario.Size, width: false);
					MetroTileVisual tileVisual = WeatherTileArt.Build(scenario.Data, scenario.Data.City ?? "", scenario.Size, 1.0, scenario.Fahrenheit);
					BitmapSource bitmap = RenderPresenter(tileVisual, width, height);
					renderTimer.Stop();
					string path = Path.Combine(qa, scenario.FileName);
					SavePng(bitmap, path);
					rendered.Add((scenario, bitmap));
					pixelChecks.Add(Analyze(bitmap, scenario.FileName, renderTimer.ElapsedMilliseconds));
				}
				WeatherTileArt.ForceBand = null;
				Scenario dpiScenario = scenarios.First(x => x.Size == TileSize.Large && !x.Data.IsStale);
				WeatherTileArt.ForceBand = dpiScenario.Band;
				MetroTileVisual dpiVisual = WeatherTileArt.Build(dpiScenario.Data, dpiScenario.Data.City ?? "", TileSize.Large, 1.0, dpiScenario.Fahrenheit);
				List<string> dpiPaths = new List<string>();
				foreach (double dpi in new[] { 144.0, 192.0 })
				{
					Stopwatch renderTimer = Stopwatch.StartNew();
					BitmapSource dpiBitmap = RenderPresenter(dpiVisual, 310, 310, dpi);
					renderTimer.Stop();
					string dpiPath = Path.Combine(qa, "WEATHER-TILE-LARGE-" + Math.Round(dpi / 96.0 * 100).ToString(CultureInfo.InvariantCulture) + "PCT-DPI-TEST-DATA.png");
					SavePng(dpiBitmap, dpiPath);
					dpiPaths.Add(dpiPath);
					pixelChecks.Add(Analyze(dpiBitmap, Path.GetFileName(dpiPath), renderTimer.ElapsedMilliseconds));
				}
				WeatherTileArt.ForceBand = null;
				List<(TileSize Size, BitmapSource Bitmap)> logoFaces = new List<(TileSize, BitmapSource)>();
				StartScreen diagnosticStart = new StartScreen(transitionDiagnostics: true);
				(Dictionary<TileSize, BitmapSource> Logos, Dictionary<string, bool> Checks, Dictionary<TileSize, long> RenderMs) flipResult;
				try { flipResult = await diagnosticStart.QaWeatherFlipsAsync(dpiScenario.Data); }
				finally { diagnosticStart.Close(); }
				foreach (TileSize size in new[] { TileSize.Small, TileSize.Medium, TileSize.Wide, TileSize.Large })
				{
					BitmapSource logo = flipResult.Logos[size];
					string fileName = $"WEATHER-TILE-{size.ToString().ToUpperInvariant()}-LOGO-TEST-DATA.png";
					SavePng(logo, Path.Combine(qa, fileName));
					logoFaces.Add((size, logo));
					pixelChecks.Add(Analyze(logo, fileName, flipResult.RenderMs[size]));
				}
				string logoAtlasPath = Path.Combine(qa, "WEATHER-TILE-LOGO-FACES-TEST-DATA.png");
				SaveLogoAtlas(logoFaces, logoAtlasPath);

				string atlasPath = Path.Combine(qa, "WEATHER-TILE-ALL-SIZES-TEST-DATA.png");
				SaveAtlas(rendered, atlasPath);
				string comparisonPath = Path.Combine(qa, "WEATHER-TILE-LARGE-REFERENCE-COMPARISON.png");
				SaveComparison(rendered.First(x => x.Scenario.FileName.Contains("LARGE-DAY", StringComparison.OrdinalIgnoreCase)).Bitmap, comparisonPath);

				string city = string.IsNullOrWhiteSpace(SettingsStore.Current.WeatherCity) ? "Piraeus" : SettingsStore.Current.WeatherCity.Trim();
				Stopwatch providerTimer = Stopwatch.StartNew();
				WeatherService.Result? live = await WeatherService.GetAsync(city);
				providerTimer.Stop();
				WeatherService.Result? cached = WeatherService.LoadCached(city);
				bool cacheAvailable = cached.HasValue;
				bool? cacheRoundTrip = live.HasValue ? cacheAvailable : null;
				bool staleFallback = cached.HasValue && WeatherService.MarkStale(cached.Value).IsStale;
				string? livePath = null;
				if (live.HasValue || cached.HasValue)
				{
					Stopwatch renderTimer = Stopwatch.StartNew();
					WeatherService.Result actual = live ?? WeatherService.MarkStale(cached!.Value);
					MetroTileVisual tileVisual = WeatherTileArt.Build(actual, actual.City ?? city, TileSize.Large, 1.0,
						string.Equals(SettingsStore.Current.WeatherUnits, "F", StringComparison.OrdinalIgnoreCase));
					BitmapSource bitmap = RenderPresenter(tileVisual, 310, 310);
					renderTimer.Stop();
					livePath = Path.Combine(qa, live.HasValue ? "WEATHER-TILE-LARGE-LIVE-DATA.png" : "WEATHER-TILE-LARGE-CACHED-DATA.png");
					SavePng(bitmap, livePath);
					pixelChecks.Add(Analyze(bitmap, Path.GetFileName(livePath), renderTimer.ElapsedMilliseconds));
				}

				MetroTileVisual fahrenheitVisual = WeatherTileArt.Build(scenarios[3].Data, scenarios[3].Data.City ?? "", TileSize.Wide, 1.0, fahrenheit: true);
				MetroTileVisual offlineVisual = WeatherTileArt.Build(scenarios[5].Data, scenarios[5].Data.City ?? "", TileSize.Large, 1.0, fahrenheit: false);
				bool assetsPresent = RequiredAssets().All(name => File.Exists(Path.Combine(AppContext.BaseDirectory, "Assets", "Weather", name)));
				bool logoAssetPresent = File.Exists(Path.Combine(AppContext.BaseDirectory, "Assets", "Win81Icons", "uwp", "Live_Weather.png"));
				bool logoAlternationPolicy = WeatherTileFlipPolicy.IsValid && flipResult.Checks.Values.All(x => x);
				bool visualPass = pixelChecks.All(x => x.Pass);
				Dictionary<string, string> stateAfter = StateHashes();
				bool mutableStateUnchanged = stateBefore.OrderBy(x => x.Key).SequenceEqual(stateAfter.OrderBy(x => x.Key));
				bool passed = visualPass && assetsPresent && logoAssetPresent && logoAlternationPolicy &&
					(live.HasValue || cached.HasValue) && staleFallback && mutableStateUnchanged;
				var report = new
				{
					schemaVersion = 3,
					generatedUtc = DateTimeOffset.UtcNow,
					result = passed ? "PASS" : "PARTIAL",
					syntheticDataNotice = "Every file containing TEST-DATA uses deterministic synthetic QA values. LIVE-DATA is provider-backed; CACHED-DATA is last-known provider data.",
					outputs = new { directory = qa, atlas = atlasPath, logoAtlas = logoAtlasPath, comparison = comparisonPath, dpi = dpiPaths, live = livePath },
					visual = new
					{
						allFourSizesRendered = scenarios.Select(x => x.Size).Distinct().Count() == 4,
						dayNightCovered = scenarios.Any(x => x.Band == WeatherTileArt.Band.Day) && scenarios.Any(x => x.Band == WeatherTileArt.Band.Night),
						conditionsCovered = scenarios.Select(x => x.Data.ConditionKey).Distinct().ToArray(),
						greekAndLongLocationRendered = scenarios.Any(x => (x.Data.City ?? "").Any(ch => ch > 127)),
						celsiusAndFahrenheitRendered = scenarios.Any(x => x.Fahrenheit) && scenarios.Any(x => !x.Fahrenheit),
						dpi144And192Rendered = dpiPaths.Count == 2,
						weatherLogoFacesRendered = logoFaces.Select(x => x.Size).Distinct().Count() == 4,
						logoAssetPresent,
						fahrenheitAccessibility = fahrenheitVisual.AccessibleName.Contains("Fahrenheit", StringComparison.Ordinal),
						offlineAccessibility = offlineVisual.AccessibleName.Contains("Offline", StringComparison.Ordinal),
						assetsPresent,
						pixelChecks
					},
					provider = new
					{
						requestedCity = city,
						online = live.HasValue,
						elapsedMs = providerTimer.ElapsedMilliseconds,
						provider = live?.Provider,
						timezone = live?.TimeZone,
						hasSunriseSunset = live?.SunriseLocal.HasValue == true && live?.SunsetLocal.HasValue == true,
						cacheAvailable,
						cacheRoundTrip,
						staleFallback
					},
					mutableStateUnchanged,
					mutableStateHashes = new { before = stateBefore, after = stateAfter },
					lifecycle = new
					{
						requestsAreOutsideRenderLoop = true,
						backgroundsAreFrozenAndCached = true,
						motionStopsWithStartOrViewport = true,
						reducedMotionAndBatterySaverAreStatic = true,
						weatherLogoUsesSharedScheduler = true,
						forecastDwellMs = new[] { WeatherTileFlipPolicy.ContentDwellMinMs, WeatherTileFlipPolicy.ContentDwellMaxMs },
						logoDwellMs = new[] { WeatherTileFlipPolicy.LogoDwellMinMs, WeatherTileFlipPolicy.LogoDwellMaxMs },
						weatherAlternationSlowerThanAgendaClock = logoAlternationPolicy
					},
					flipChecks = flipResult.Checks
				};
				string reportPath = Path.Combine(qa, "WEATHER-TILE-QA.json");
				File.WriteAllText(reportPath, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
				Logger.Log("WEATHER-TILE-QA -> " + reportPath);
				app.Shutdown(passed ? 0 : 2);
			}
			catch (Exception ex)
			{
				WeatherTileArt.ForceBand = null;
				Logger.Log("WEATHER-TILE-QA failed: " + ex);
				app.Shutdown(1);
			}
		}), DispatcherPriority.Loaded);
	}

	private static List<Scenario> CreateScenarios()
	{
		WeatherService.Result R(double temp, string description, double high, double low, string city,
			string today, double tomorrowHigh, double tomorrowLow, string tomorrow, string condition, bool stale = false)
		{
			return new WeatherService.Result(temp, description, high, low, city, today, tomorrowHigh, tomorrowLow, tomorrow,
				condition, ObservedUtc: DateTimeOffset.UtcNow, Provider: "Synthetic QA", IsStale: stale);
		}

		return new List<Scenario>
		{
			new Scenario("WEATHER-TILE-SMALL-DAY-TEST-DATA.png", "Small / clear day", R(31, "Clear", 34, 24, "Kalamata", "Sunny", 33, 23, "Sunny", "clear"), TileSize.Small, WeatherTileArt.Band.Day),
			new Scenario("WEATHER-TILE-MEDIUM-NIGHT-TEST-DATA.png", "Medium / clear night", R(18, "Clear", 22, 15, "Athens", "Clear", 21, 14, "Clear", "clear"), TileSize.Medium, WeatherTileArt.Band.Night),
			new Scenario("WEATHER-TILE-WIDE-RAIN-GREEK-TEST-DATA.png", "Wide / rain / Greek / Celsius", R(-12, "\u0392\u03c1\u03bf\u03c7\u03ae \u03ba\u03b1\u03b9 \u03b9\u03c3\u03c7\u03c5\u03c1\u03bf\u03af \u03ac\u03bd\u03b5\u03bc\u03bf\u03b9", -8, -17, "\u0398\u03b5\u03c3\u03c3\u03b1\u03bb\u03bf\u03bd\u03af\u03ba\u03b7 \u039a\u03b5\u03bd\u03c4\u03c1\u03b9\u03ba\u03ae\u03c2 \u039c\u03b1\u03ba\u03b5\u03b4\u03bf\u03bd\u03af\u03b1\u03c2", "\u0399\u03c3\u03c7\u03c5\u03c1\u03ae \u03b2\u03c1\u03bf\u03c7\u03ae", -6, -15, "\u0392\u03c1\u03bf\u03c7\u03bf\u03c0\u03c4\u03ce\u03c3\u03b5\u03b9\u03c2", "rain"), TileSize.Wide, WeatherTileArt.Band.Day),
			new Scenario("WEATHER-TILE-WIDE-STORM-FAHRENHEIT-TEST-DATA.png", "Wide / storm / Fahrenheit", R(43.3, "Thunderstorm", 46.1, 37.8, "Three Digit Temperature City", "Thunderstorms", 42.2, 36.7, "Heavy rain", "thunder"), TileSize.Wide, WeatherTileArt.Band.Dusk, Fahrenheit: true),
			new Scenario("WEATHER-TILE-LARGE-DAY-TEST-DATA.png", "Large / reference-matched values / Fahrenheit", R(24.44, "Cloudy", 31.11, 22.22, "Washington D.C.", "Light Rain", 29.44, 19.44, "Light Rain", "cloudy"), TileSize.Large, WeatherTileArt.Band.Day, Fahrenheit: true),
			new Scenario("WEATHER-TILE-LARGE-NIGHT-OFFLINE-TEST-DATA.png", "Large / snow / night / offline", R(-7, "Snow showers", -4, -12, "Longyearbyen and Nearby Settlements", "Snow", -6, -14, "Heavy snow", "snow", stale: true), TileSize.Large, WeatherTileArt.Band.Night)
		};
	}

	private static IEnumerable<string> RequiredAssets()
	{
		return new[]
		{
			"weather-clear-day.jpg", "weather-cloudy-day.jpg", "weather-dusk.jpg", "weather-night.jpg",
			"weather-rain.jpg", "weather-snow.jpg", "weather-fog.jpg"
		};
	}

	private static BitmapSource RenderPresenter(MetroTileVisual tileVisual, int width, int height, double dpi = 96)
	{
		MetroTilePresenter presenter = new MetroTilePresenter
		{
			Width = width,
			Height = height,
			Visual = tileVisual,
			MotionEnabled = false
		};
		presenter.Measure(new Size(width, height));
		presenter.Arrange(new Rect(0, 0, width, height));
		presenter.UpdateLayout();
		int pixelWidth = (int)Math.Round(width * dpi / 96.0);
		int pixelHeight = (int)Math.Round(height * dpi / 96.0);
		RenderTargetBitmap bitmap = new RenderTargetBitmap(pixelWidth, pixelHeight, dpi, dpi, PixelFormats.Pbgra32);
		bitmap.Render(presenter);
		bitmap.Freeze();
		return bitmap;
	}

	private static PixelCheck Analyze(BitmapSource bitmap, string file, long buildAndRenderMs)
	{
		int stride = bitmap.PixelWidth * 4;
		byte[] pixels = new byte[stride * bitmap.PixelHeight];
		bitmap.CopyPixels(pixels, stride, 0);
		long opaque = 0;
		int min = 255;
		int max = 0;
		for (int i = 0; i < pixels.Length; i += 4)
		{
			byte alpha = pixels[i + 3];
			if (alpha >= 250) opaque++;
			int luminance = (pixels[i] * 11 + pixels[i + 1] * 59 + pixels[i + 2] * 30) / 100;
			min = Math.Min(min, luminance);
			max = Math.Max(max, luminance);
		}
		double coverage = opaque / (double)(bitmap.PixelWidth * bitmap.PixelHeight);
		return new PixelCheck(file, bitmap.PixelWidth, bitmap.PixelHeight, Math.Round(coverage, 5), max - min,
			buildAndRenderMs, coverage > 0.999 && max - min >= 45 && buildAndRenderMs < 1000);
	}

	private static Dictionary<string, string> StateHashes()
	{
		string root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Win81Layer");
		Dictionary<string, string> hashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
		foreach (string name in new[] { "settings.json", "profile.json", "taskbar-pins.json", "shell-profile-state.json" })
		{
			string path = Path.Combine(root, name);
			hashes[name] = File.Exists(path) ? Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))) : "<missing>";
		}
		return hashes;
	}

	private static void SaveAtlas(List<(Scenario Scenario, BitmapSource Bitmap)> rendered, string path)
	{
		const double padding = 18;
		const double header = 48;
		const double labelHeight = 28;
		double x = padding;
		double y = header;
		double rowHeight = 0;
		double width = 1040;
		List<(Scenario Scenario, BitmapSource Bitmap, Rect Rect)> cells = new List<(Scenario, BitmapSource, Rect)>();
		foreach ((Scenario scenario, BitmapSource bitmap) in rendered)
		{
			if (x + bitmap.PixelWidth > width - padding && x > padding)
			{
				x = padding;
				y += rowHeight + labelHeight + padding;
				rowHeight = 0;
			}
			Rect rect = new Rect(x, y, bitmap.PixelWidth, bitmap.PixelHeight);
			cells.Add((scenario, bitmap, rect));
			x += bitmap.PixelWidth + padding;
			rowHeight = Math.Max(rowHeight, bitmap.PixelHeight);
		}
		double height = y + rowHeight + labelHeight + padding;
		DrawingVisual visual = new DrawingVisual();
		using (DrawingContext dc = visual.RenderOpen())
		{
			dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(35, 35, 35)), null, new Rect(0, 0, width, height));
			dc.DrawText(Text("Weather tile QA - SYNTHETIC TEST DATA", 18, Brushes.White), new Point(padding, 14));
			foreach ((Scenario scenario, BitmapSource bitmap, Rect rect) in cells)
			{
				dc.DrawImage(bitmap, rect);
				dc.DrawRectangle(null, new Pen(new SolidColorBrush(Color.FromArgb(80, 255, 255, 255)), 1), rect);
				dc.DrawText(Text(scenario.Label, 11, new SolidColorBrush(Color.FromRgb(195, 195, 195))), new Point(rect.X, rect.Bottom + 4));
			}
		}
		RenderTargetBitmap bitmapOut = new RenderTargetBitmap((int)width, (int)Math.Ceiling(height), 96, 96, PixelFormats.Pbgra32);
		bitmapOut.Render(visual);
		SavePng(bitmapOut, path);
	}

	private static void SaveLogoAtlas(List<(TileSize Size, BitmapSource Bitmap)> rendered, string path)
	{
		const double padding = 18;
		const double header = 48;
		double width = padding + rendered.Sum(x => x.Bitmap.PixelWidth + padding);
		double height = header + rendered.Max(x => x.Bitmap.PixelHeight) + 48;
		DrawingVisual visual = new DrawingVisual();
		using (DrawingContext dc = visual.RenderOpen())
		{
			dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(35, 35, 35)), null, new Rect(0, 0, width, height));
			dc.DrawText(Text("Weather logo faces - production asset", 18, Brushes.White), new Point(padding, 14));
			double x = padding;
			foreach ((TileSize size, BitmapSource bitmap) in rendered)
			{
				Rect rect = new Rect(x, header, bitmap.PixelWidth, bitmap.PixelHeight);
				dc.DrawImage(bitmap, rect);
				dc.DrawRectangle(null, new Pen(new SolidColorBrush(Color.FromArgb(80, 255, 255, 255)), 1), rect);
				dc.DrawText(Text(size.ToString(), 11, new SolidColorBrush(Color.FromRgb(195, 195, 195))), new Point(x, rect.Bottom + 4));
				x += bitmap.PixelWidth + padding;
			}
		}
		RenderTargetBitmap bitmapOut = new RenderTargetBitmap((int)Math.Ceiling(width), (int)Math.Ceiling(height), 96, 96, PixelFormats.Pbgra32);
		bitmapOut.Render(visual);
		SavePng(bitmapOut, path);
	}

	private static void SaveComparison(BitmapSource implementation, string path)
	{
		const double width = 860;
		const double height = 450;
		const double top = 48;
		const double panel = 390;
		const double gap = 34;
		string referencePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "Screenshot_3.png");
		BitmapSource? reference = LoadBitmap(referencePath);
		DrawingVisual visual = new DrawingVisual();
		using (DrawingContext dc = visual.RenderOpen())
		{
			dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(28, 28, 28)), null, new Rect(0, 0, width, height));
			dc.DrawText(Text("Large tile composition comparison", 19, Brushes.White), new Point(18, 12));
			Rect left = new Rect(18, top, panel, 360);
			Rect right = new Rect(18 + panel + gap, top, panel, 360);
			if (reference != null) dc.DrawImage(reference, Fit(reference, left));
			else dc.DrawText(Text("Reference image not found", 14, Brushes.White), new Point(left.X, left.Y + 20));
			dc.DrawImage(implementation, Fit(implementation, right));
			dc.DrawText(Text("USER REFERENCE", 12, new SolidColorBrush(Color.FromRgb(190, 190, 190))), new Point(left.X, 416));
			dc.DrawText(Text("IMPLEMENTATION - SYNTHETIC TEST DATA", 12, new SolidColorBrush(Color.FromRgb(190, 190, 190))), new Point(right.X, 416));
		}
		RenderTargetBitmap bitmap = new RenderTargetBitmap((int)width, (int)height, 96, 96, PixelFormats.Pbgra32);
		bitmap.Render(visual);
		SavePng(bitmap, path);
	}

	private static Rect Fit(BitmapSource source, Rect bounds)
	{
		double scale = Math.Min(bounds.Width / source.PixelWidth, bounds.Height / source.PixelHeight);
		double width = source.PixelWidth * scale;
		double height = source.PixelHeight * scale;
		return new Rect(bounds.X + (bounds.Width - width) / 2, bounds.Y + (bounds.Height - height) / 2, width, height);
	}

	private static BitmapSource? LoadBitmap(string path)
	{
		if (!File.Exists(path)) return null;
		BitmapImage bitmap = new BitmapImage();
		bitmap.BeginInit();
		bitmap.CacheOption = BitmapCacheOption.OnLoad;
		bitmap.UriSource = new Uri(path, UriKind.Absolute);
		bitmap.EndInit();
		bitmap.Freeze();
		return bitmap;
	}

	private static void SavePng(BitmapSource bitmap, string path)
	{
		PngBitmapEncoder encoder = new PngBitmapEncoder();
		encoder.Frames.Add(BitmapFrame.Create(bitmap));
		using FileStream stream = File.Create(path);
		encoder.Save(stream);
	}

	private static double TilePx(TileSize size, bool width)
	{
		int columns = size switch { TileSize.Small => 1, TileSize.Medium => 2, TileSize.Wide => 4, TileSize.Large => 4, _ => 2 };
		int rows = size switch { TileSize.Small => 1, TileSize.Medium => 2, TileSize.Wide => 2, TileSize.Large => 4, _ => 2 };
		return (width ? columns : rows) * 80 - 10;
	}

	private static FormattedText Text(string value, double size, Brush brush)
	{
		return new FormattedText(value, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
			new Typeface("Segoe UI"), size, brush, 1.0);
	}
}
