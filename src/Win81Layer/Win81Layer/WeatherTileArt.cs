using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Win81Layer;

internal sealed class MetroTileVisual
{
	public required ImageSource Background { get; init; }
	public required ImageSource Foreground { get; init; }
	public required string Key { get; init; }
	public required string AccessibleName { get; init; }
	public WeatherTileArt.Category Category { get; init; } = WeatherTileArt.Category.Clear;
	public WeatherTileArt.Band Band { get; init; } = WeatherTileArt.Band.Day;
	public required TileSize Size { get; init; }
	public required Color Tint { get; init; }
	public required double Width { get; init; }
	public required double Height { get; init; }
	public bool AnimateBackground { get; init; } = true;
}

internal static class WeatherTileArt
{
	private static readonly Typeface Light = new Typeface(new FontFamily("Segoe UI Light"), FontStyles.Normal, FontWeights.Light, FontStretches.Normal);
	private static readonly Typeface Regular = new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
	private static readonly Typeface Semibold = new Typeface(new FontFamily("Segoe UI Semibold"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
	private static readonly SolidColorBrush White = FrozenBrush(Colors.White);
	private static readonly object AssetGate = new object();
	private static readonly Dictionary<string, BitmapSource> AssetCache = new Dictionary<string, BitmapSource>(StringComparer.OrdinalIgnoreCase);
	private static readonly LinkedList<string> AssetLru = new LinkedList<string>();
	private const int MaxDecodedAssets = 4;

	internal enum Category { Clear, Partly, Cloudy, Rain, Snow, Thunder, Fog }
	internal enum Band { Dawn, Day, Dusk, Night }

	internal static Band? ForceBand;

	internal static void Preload(WeatherService.Result result)
	{
		Category category = Classify(result.ConditionKey, result.Description);
		Band band = CurrentBand(result);
		string current = AssetName(category, band);
		LoadAsset(current);
		Band next = band switch { Band.Dawn => Band.Day, Band.Day => Band.Dusk, Band.Dusk => Band.Night, _ => Band.Dawn };
		string upcoming = AssetName(category, next);
		if (!string.Equals(current, upcoming, StringComparison.OrdinalIgnoreCase)) LoadAsset(upcoming);
	}

	public static MetroTileVisual Build(WeatherService.Result result, string city, TileSize size, double scale, bool fahrenheit)
	{
		double width = TilePx(size, true) * scale;
		double height = TilePx(size, false) * scale;
		if (width < 8 || height < 8)
		{
			width = size is TileSize.Wide or TileSize.Large ? 310 : 150;
			height = size == TileSize.Large ? 310 : 150;
		}

		Category category = Classify(result.ConditionKey, result.Description);
		Band band = CurrentBand(result);
		string assetName = AssetName(category, band);
		BitmapSource background = LoadAsset(assetName);
		DrawingImage foreground = BuildForeground(result, city, size, width, height, fahrenheit, category, band);
		string key = string.Join("|", size, fahrenheit ? "F" : "C", band, category,
			Math.Round(result.TempC, 1), city, result.Description, result.HiC, result.LoC,
			result.TodayDesc, result.TomHiC, result.TomLoC, result.TomDesc, result.IsStale, assetName);

		return new MetroTileVisual
		{
			Background = background,
			Foreground = foreground,
			Key = key,
			AccessibleName = AccessibleText(result, city, fahrenheit),
			Category = category,
			Band = band,
			Size = size,
			Tint = TintFor(category, band),
			Width = width,
			Height = height
		};
	}

	public static Brush Compose(WeatherService.Result result, string city, TileSize size, double scale)
	{
		return Compose(result, city, size, scale, fahrenheit: false);
	}

	public static Brush Compose(WeatherService.Result result, string city, TileSize size, double scale, bool fahrenheit)
	{
		MetroTileVisual visual = Build(result, city, size, scale, fahrenheit);
		DrawingVisual drawing = new DrawingVisual();
		using (DrawingContext dc = drawing.RenderOpen())
		{
			Rect bounds = new Rect(0, 0, visual.Width, visual.Height);
			ImageBrush background = BackgroundBrush(visual.Background, size);
			dc.DrawRectangle(background, null, bounds);
			if (visual.Tint.A > 0)
				dc.DrawRectangle(FrozenBrush(visual.Tint), null, bounds);
			dc.DrawImage(visual.Foreground, bounds);
		}
		RenderTargetBitmap bitmap = new RenderTargetBitmap(
			Math.Max(1, (int)Math.Round(visual.Width)),
			Math.Max(1, (int)Math.Round(visual.Height)),
			96, 96, PixelFormats.Pbgra32);
		bitmap.Render(drawing);
		bitmap.Freeze();
		ImageBrush face = new ImageBrush(bitmap) { Stretch = Stretch.Fill };
		face.Freeze();
		return face;
	}

	internal static Band CurrentBand(WeatherService.Result result)
	{
		if (ForceBand.HasValue) return ForceBand.Value;

		DateTimeOffset local = result.LocalTime.HasValue
			? DateTimeOffset.UtcNow.ToOffset(result.LocalTime.Value.Offset)
			: DateTimeOffset.Now;
		if (result.SunriseLocal.HasValue && result.SunsetLocal.HasValue)
		{
			TimeSpan time = local.TimeOfDay;
			TimeSpan sunrise = result.SunriseLocal.Value.TimeOfDay;
			TimeSpan sunset = result.SunsetLocal.Value.TimeOfDay;
			if (time >= sunrise - TimeSpan.FromMinutes(45) && time < sunrise + TimeSpan.FromMinutes(45)) return Band.Dawn;
			if (time >= sunset - TimeSpan.FromMinutes(45) && time < sunset + TimeSpan.FromMinutes(45)) return Band.Dusk;
			if (time >= sunrise + TimeSpan.FromMinutes(45) && time < sunset - TimeSpan.FromMinutes(45)) return Band.Day;
			return Band.Night;
		}
		int hour = local.Hour;
		if (hour >= 5 && hour < 8) return Band.Dawn;
		if (hour >= 8 && hour < 18) return Band.Day;
		if (hour >= 18 && hour < 20) return Band.Dusk;
		return Band.Night;
	}

	internal static ImageBrush BackgroundBrush(ImageSource source, TileSize size)
	{
		ImageBrush brush = new ImageBrush(source)
		{
			Stretch = Stretch.UniformToFill,
			AlignmentX = AlignmentX.Center,
			AlignmentY = size == TileSize.Wide ? AlignmentY.Bottom : AlignmentY.Center
		};
		return brush;
	}

	private static DrawingImage BuildForeground(WeatherService.Result result, string city, TileSize size,
		double width, double height, bool fahrenheit, Category category, Band band)
	{
		DrawingGroup group = new DrawingGroup();
		using (DrawingContext dc = group.Open())
		{
			Rect bounds = new Rect(0, 0, width, height);
			if (size is TileSize.Small or TileSize.Medium)
			{
				dc.DrawRectangle(FrozenBrush(Color.FromArgb(35, 0, 0, 0)), null, bounds);
				double iconSize = Math.Min(width, height) * (size == TileSize.Small ? 0.58 : 0.52);
				DrawConditionIcon(dc, category, band, width / 2, height / 2, iconSize);
			}
			else
			{
				DrawReadabilityOverlay(dc, bounds, size);
				if (size == TileSize.Wide)
					DrawWide(dc, result, city, width, height, fahrenheit);
				else
					DrawLarge(dc, result, city, width, height, fahrenheit);
			}
		}
		group.Freeze();
		DrawingImage image = new DrawingImage(group);
		image.Freeze();
		return image;
	}

	private static void DrawReadabilityOverlay(DrawingContext dc, Rect bounds, TileSize size)
	{
		LinearGradientBrush left = new LinearGradientBrush
		{
			StartPoint = new Point(0, 0.5),
			EndPoint = new Point(1, 0.5)
		};
		left.GradientStops.Add(new GradientStop(Color.FromArgb(size == TileSize.Wide ? (byte)150 : (byte)125, 0, 9, 23), 0));
		left.GradientStops.Add(new GradientStop(Color.FromArgb(45, 0, 9, 23), size == TileSize.Wide ? 0.72 : 0.82));
		left.GradientStops.Add(new GradientStop(Color.FromArgb(20, 0, 0, 0), 1));
		left.Freeze();
		dc.DrawRectangle(left, null, bounds);

		LinearGradientBrush bottom = new LinearGradientBrush
		{
			StartPoint = new Point(0.5, 0),
			EndPoint = new Point(0.5, 1)
		};
		bottom.GradientStops.Add(new GradientStop(Colors.Transparent, 0.45));
		bottom.GradientStops.Add(new GradientStop(Color.FromArgb(125, 0, 0, 0), 1));
		bottom.Freeze();
		dc.DrawRectangle(bottom, null, bounds);
	}

	private static void DrawLarge(DrawingContext dc, WeatherService.Result result, string city, double width, double height, bool fahrenheit)
	{
		double u = height / 310.0;
		double pad = 16 * u;
		double x = pad;
		double maxWidth = width - pad * 2;
		double y = 5 * u;

		FormattedText temp = Fit(Temperature(result.TempC, fahrenheit), Light, 80 * u, 54 * u, White, maxWidth);
		dc.DrawText(temp, new Point(x, y));
		y += temp.Height * 0.79;

		FormattedText place = Fit(city, Light, 27 * u, 19 * u, White, maxWidth);
		dc.DrawText(place, new Point(x, y));
		y += place.Height * 0.88;

		FormattedText condition = Fit(Cap(result.Description), Light, 21 * u, 17 * u, White, maxWidth);
		dc.DrawText(condition, new Point(x, y));
		y += condition.Height + 10 * u;

		DrawForecast(dc, "Today", result.HiC, result.LoC, result.TodayDesc, x, ref y, maxWidth, 15 * u, 16 * u, u, fahrenheit);
		y += 6 * u;
		DrawForecast(dc, "Tomorrow", result.TomHiC, result.TomLoC, result.TomDesc, x, ref y, maxWidth, 15 * u, 16 * u, u, fahrenheit);

		double labelY = height - 25 * u;
		dc.DrawText(Fit("Weather", Semibold, 13 * u, 11 * u, White, maxWidth * 0.55), new Point(x, labelY));
		if (result.IsStale)
		{
			FormattedText stale = Fit("Offline data", Regular, 10 * u, 9 * u, Dim(215), maxWidth * 0.45);
			dc.DrawText(stale, new Point(width - pad - stale.Width, height - 22 * u));
		}
	}

	private static void DrawWide(DrawingContext dc, WeatherService.Result result, string city, double width, double height, bool fahrenheit)
	{
		double u = height / 150.0;
		double pad = 12 * u;
		double gap = 14 * u;
		double leftWidth = Math.Min(158 * u, (width - pad * 2 - gap) * 0.54);
		double rightX = pad + leftWidth + gap;
		double rightWidth = width - pad - rightX;
		double y = 1 * u;

		FormattedText temp = Fit(Temperature(result.TempC, fahrenheit), Light, 49 * u, 34 * u, White, leftWidth);
		dc.DrawText(temp, new Point(pad, y));
		y += temp.Height * 0.78;
		FormattedText place = Fit(city, Regular, 16 * u, 13 * u, White, leftWidth);
		dc.DrawText(place, new Point(pad, y));
		y += place.Height * 0.88;
		dc.DrawText(Fit(Cap(result.Description), Regular, 13 * u, 11 * u, Dim(235), leftWidth), new Point(pad, y));

		double forecastY = 12 * u;
		DrawForecast(dc, "Today", result.HiC, result.LoC, result.TodayDesc, rightX, ref forecastY, rightWidth, 11 * u, 12 * u, u, fahrenheit);
		forecastY += 11 * u;
		DrawForecast(dc, "Tomorrow", result.TomHiC, result.TomLoC, result.TomDesc, rightX, ref forecastY, rightWidth, 11 * u, 12 * u, u, fahrenheit);

		dc.DrawText(Fit("Weather", Semibold, 12 * u, 10 * u, White, leftWidth), new Point(pad, height - 21 * u));
		if (result.IsStale)
		{
			FormattedText stale = Fit("Offline data", Regular, 9 * u, 8 * u, Dim(215), rightWidth);
			dc.DrawText(stale, new Point(width - pad - stale.Width, height - 18 * u));
		}
	}

	private static void DrawForecast(DrawingContext dc, string label, double? highC, double? lowC, string? description,
		double x, ref double y, double width, double labelSize, double lineSize, double unit, bool fahrenheit)
	{
		FormattedText heading = Fit(label, Semibold, labelSize, Math.Max(9 * unit, labelSize * 0.72), White, width);
		dc.DrawText(heading, new Point(x, y));
		y += heading.Height * 0.88;
		string line = TemperaturePair(highC, lowC, fahrenheit);
		if (!string.IsNullOrWhiteSpace(description)) line += "  " + Cap(description!);
		FormattedText detail = Fit(line, Regular, lineSize, Math.Max(10 * unit, lineSize * 0.82), Dim(242), width);
		dc.DrawText(detail, new Point(x, y));
		y += detail.Height;
	}

	private static FormattedText Fit(string? text, Typeface face, double preferredSize, double minimumSize, Brush brush, double maxWidth)
	{
		string value = string.IsNullOrWhiteSpace(text) ? "--" : text.Trim();
		double size = preferredSize;
		FormattedText formatted = Text(value, face, size, brush);
		while (formatted.Width > maxWidth && size > minimumSize + 0.25)
		{
			size = Math.Max(minimumSize, size - Math.Max(0.5, preferredSize * 0.045));
			formatted = Text(value, face, size, brush);
		}
		formatted.MaxTextWidth = Math.Max(1, maxWidth);
		formatted.MaxLineCount = 1;
		formatted.Trimming = TextTrimming.CharacterEllipsis;
		return formatted;
	}

	private static FormattedText Text(string value, Typeface face, double size, Brush brush)
	{
		return new FormattedText(value, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, face, size, brush, 1.0);
	}

	private static string Temperature(double celsius, bool fahrenheit)
	{
		double value = fahrenheit ? celsius * 9.0 / 5.0 + 32.0 : celsius;
		return Math.Round(value).ToString(CultureInfo.InvariantCulture) + "\u00b0";
	}

	private static string TemperaturePair(double? highC, double? lowC, bool fahrenheit)
	{
		string high = highC.HasValue ? Temperature(highC.Value, fahrenheit) : "--";
		string low = lowC.HasValue ? Temperature(lowC.Value, fahrenheit) : "--";
		return high + "/" + low;
	}

	private static string AccessibleText(WeatherService.Result result, string city, bool fahrenheit)
	{
		string unit = fahrenheit ? "Fahrenheit" : "Celsius";
		string text = "Weather, " + city + ", " + Temperature(result.TempC, fahrenheit) + " " + unit + ", " + Cap(result.Description);
		if (result.HiC.HasValue || result.LoC.HasValue)
			text += ". Today " + TemperaturePair(result.HiC, result.LoC, fahrenheit) + ", " + Cap(result.TodayDesc ?? "");
		if (result.TomHiC.HasValue || result.TomLoC.HasValue)
			text += ". Tomorrow " + TemperaturePair(result.TomHiC, result.TomLoC, fahrenheit) + ", " + Cap(result.TomDesc ?? "");
		if (result.IsStale) text += ". Offline, showing last known data";
		return text;
	}

	internal static BitmapSource LoadAsset(string name)
	{
		lock (AssetGate)
		{
			if (AssetCache.TryGetValue(name, out BitmapSource cached))
			{
				TouchAsset(name);
				return cached;
			}
			string path = Path.Combine(AppContext.BaseDirectory, "Assets", "Weather", name);
			if (!File.Exists(path))
			{
				BitmapSource fallback = GradientFallback(name);
				CacheAsset(name, fallback);
				return fallback;
			}
			BitmapImage bitmap = new BitmapImage();
			bitmap.BeginInit();
			bitmap.CacheOption = BitmapCacheOption.OnLoad;
			bitmap.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
			bitmap.DecodePixelWidth = 720;
			bitmap.UriSource = new Uri(path, UriKind.Absolute);
			bitmap.EndInit();
			bitmap.Freeze();
			CacheAsset(name, bitmap);
			return bitmap;
		}
	}

	private static void CacheAsset(string name, BitmapSource bitmap)
	{
		AssetCache[name] = bitmap;
		TouchAsset(name);
		while (AssetLru.Count > MaxDecodedAssets)
		{
			string oldest = AssetLru.First!.Value;
			AssetLru.RemoveFirst();
			AssetCache.Remove(oldest);
		}
	}

	private static void TouchAsset(string name)
	{
		LinkedListNode<string>? existing = AssetLru.Find(name);
		if (existing != null) AssetLru.Remove(existing);
		AssetLru.AddLast(name);
	}

	private static BitmapSource GradientFallback(string key)
	{
		Color top = key.Contains("night", StringComparison.OrdinalIgnoreCase) ? Color.FromRgb(10, 28, 58) : Color.FromRgb(70, 120, 165);
		Color bottom = key.Contains("dusk", StringComparison.OrdinalIgnoreCase) ? Color.FromRgb(180, 105, 92) : Color.FromRgb(35, 58, 82);
		DrawingVisual visual = new DrawingVisual();
		using (DrawingContext dc = visual.RenderOpen())
			dc.DrawRectangle(new LinearGradientBrush(top, bottom, 90), null, new Rect(0, 0, 720, 720));
		RenderTargetBitmap bitmap = new RenderTargetBitmap(720, 720, 96, 96, PixelFormats.Pbgra32);
		bitmap.Render(visual);
		bitmap.Freeze();
		return bitmap;
	}

	private static string AssetName(Category category, Band band)
	{
		if (category is Category.Clear or Category.Partly)
		{
			if (band == Band.Night) return "weather-night.jpg";
			if (band is Band.Dawn or Band.Dusk) return "weather-dusk.jpg";
			return category == Category.Partly ? "weather-cloudy-day.jpg" : "weather-clear-day.jpg";
		}
		return category switch
		{
			Category.Rain or Category.Thunder => "weather-rain.jpg",
			Category.Snow => "weather-snow.jpg",
			Category.Fog => "weather-fog.jpg",
			_ => "weather-cloudy-day.jpg"
		};
	}

	private static Color TintFor(Category category, Band band)
	{
		if (band == Band.Night && category is not (Category.Clear or Category.Partly)) return Color.FromArgb(125, 0, 13, 37);
		if (band is Band.Dawn or Band.Dusk && category is not (Category.Clear or Category.Partly)) return Color.FromArgb(45, 50, 24, 58);
		if (category == Category.Snow) return Color.FromArgb(18, 0, 30, 65);
		if (category == Category.Cloudy) return Color.FromArgb(24, 8, 38, 82);
		return Color.FromArgb(0, 0, 0, 0);
	}

	private static Category Classify(string? conditionKey, string? description)
	{
		string key = (conditionKey ?? "").Trim().ToLowerInvariant();
		if (key == "partly") return Category.Partly;
		if (key == "cloudy") return Category.Cloudy;
		if (key == "rain") return Category.Rain;
		if (key == "snow") return Category.Snow;
		if (key == "thunder") return Category.Thunder;
		if (key == "fog") return Category.Fog;
		if (key == "clear") return Category.Clear;

		string value = (description ?? "").ToLowerInvariant();
		if (value.Contains("thunder")) return Category.Thunder;
		if (value.Contains("snow") || value.Contains("sleet") || value.Contains("ice") || value.Contains("blizzard")) return Category.Snow;
		if (value.Contains("rain") || value.Contains("drizzle") || value.Contains("shower")) return Category.Rain;
		if (value.Contains("fog") || value.Contains("mist") || value.Contains("haze")) return Category.Fog;
		if (value.Contains("partly") || value.Contains("mainly")) return Category.Partly;
		if (value.Contains("overcast") || value.Contains("cloud")) return Category.Cloudy;
		return Category.Clear;
	}

	private static void DrawConditionIcon(DrawingContext dc, Category category, Band band, double centerX, double centerY, double size)
	{
		bool night = band == Band.Night;
		double radius = size * 0.5;
		if (category is Category.Clear or Category.Partly)
		{
			if (night) DrawMoon(dc, centerX - (category == Category.Partly ? radius * 0.18 : 0), centerY - (category == Category.Partly ? radius * 0.18 : 0), radius * (category == Category.Partly ? 0.64 : 0.82));
			else DrawSun(dc, centerX - (category == Category.Partly ? radius * 0.20 : 0), centerY - (category == Category.Partly ? radius * 0.20 : 0), radius * (category == Category.Partly ? 0.62 : 0.82));
			if (category == Category.Partly) DrawCloudGlyph(dc, centerX + radius * 0.22, centerY + radius * 0.23, radius * 0.98, White);
			return;
		}

		DrawCloudGlyph(dc, centerX, centerY - (category == Category.Cloudy ? 0 : radius * 0.22), size * (category == Category.Cloudy ? 0.92 : 0.78), White);
		Brush dim = Dim(240);
		if (category == Category.Rain)
		{
			Pen rain = FrozenPen(dim, Math.Max(1.2, size * 0.042));
			for (int i = -1; i <= 1; i++)
			{
				double x = centerX + i * size * 0.19;
				dc.DrawLine(rain, new Point(x, centerY + radius * 0.32), new Point(x - size * 0.055, centerY + radius * 0.72));
			}
		}
		else if (category == Category.Snow)
		{
			for (int i = -1; i <= 1; i++)
				dc.DrawEllipse(White, null, new Point(centerX + i * size * 0.20, centerY + radius * 0.52), size * 0.045, size * 0.045);
		}
		else if (category == Category.Thunder)
		{
			StreamGeometry bolt = new StreamGeometry();
			using (StreamGeometryContext g = bolt.Open())
			{
				g.BeginFigure(new Point(centerX + size * 0.02, centerY + radius * 0.18), true, true);
				g.LineTo(new Point(centerX - size * 0.12, centerY + radius * 0.62), true, false);
				g.LineTo(new Point(centerX, centerY + radius * 0.58), true, false);
				g.LineTo(new Point(centerX - size * 0.06, centerY + radius * 0.96), true, false);
				g.LineTo(new Point(centerX + size * 0.16, centerY + radius * 0.46), true, false);
			}
			bolt.Freeze();
			dc.DrawGeometry(FrozenBrush(Color.FromRgb(255, 213, 72)), null, bolt);
		}
		else if (category == Category.Fog)
		{
			Pen fog = FrozenPen(dim, Math.Max(1.4, size * 0.045));
			for (int i = 0; i < 3; i++)
				dc.DrawLine(fog, new Point(centerX - radius * 0.68, centerY + radius * 0.30 + i * size * 0.14), new Point(centerX + radius * 0.68, centerY + radius * 0.30 + i * size * 0.14));
		}
	}

	private static void DrawSun(DrawingContext dc, double x, double y, double radius)
	{
		dc.DrawEllipse(White, null, new Point(x, y), radius * 0.58, radius * 0.58);
		Pen rays = FrozenPen(White, Math.Max(1.4, radius * 0.10));
		for (int i = 0; i < 8; i++)
		{
			double angle = i * Math.PI / 4;
			dc.DrawLine(rays,
				new Point(x + Math.Cos(angle) * radius * 0.78, y + Math.Sin(angle) * radius * 0.78),
				new Point(x + Math.Cos(angle) * radius, y + Math.Sin(angle) * radius));
		}
	}

	private static void DrawMoon(DrawingContext dc, double x, double y, double radius)
	{
		EllipseGeometry outer = new EllipseGeometry(new Point(x, y), radius * 0.72, radius * 0.72);
		EllipseGeometry cut = new EllipseGeometry(new Point(x + radius * 0.34, y - radius * 0.25), radius * 0.68, radius * 0.68);
		CombinedGeometry crescent = new CombinedGeometry(GeometryCombineMode.Exclude, outer, cut);
		crescent.Freeze();
		dc.DrawGeometry(White, null, crescent);
	}

	private static void DrawCloudGlyph(DrawingContext dc, double x, double y, double size, Brush brush)
	{
		double radius = size * 0.5;
		dc.DrawEllipse(brush, null, new Point(x, y), radius * 0.60, radius * 0.42);
		dc.DrawEllipse(brush, null, new Point(x - radius * 0.55, y + radius * 0.14), radius * 0.42, radius * 0.30);
		dc.DrawEllipse(brush, null, new Point(x + radius * 0.55, y + radius * 0.12), radius * 0.46, radius * 0.32);
		dc.DrawEllipse(brush, null, new Point(x, y + radius * 0.20), radius * 0.72, radius * 0.32);
	}

	private static double TilePx(TileSize size, bool width)
	{
		int columns = size switch { TileSize.Small => 1, TileSize.Medium => 2, TileSize.Wide => 4, TileSize.Large => 4, _ => 2 };
		int rows = size switch { TileSize.Small => 1, TileSize.Medium => 2, TileSize.Wide => 2, TileSize.Large => 4, _ => 2 };
		return (width ? columns : rows) * 80 - 10;
	}

	private static string Cap(string? value)
	{
		if (string.IsNullOrWhiteSpace(value)) return "--";
		string text = value.Trim();
		return char.ToUpper(text[0], CultureInfo.CurrentUICulture) + text.Substring(1);
	}

	private static SolidColorBrush FrozenBrush(Color color)
	{
		SolidColorBrush brush = new SolidColorBrush(color);
		brush.Freeze();
		return brush;
	}

	private static Brush Dim(byte alpha) => FrozenBrush(Color.FromArgb(alpha, 255, 255, 255));

	private static Pen FrozenPen(Brush brush, double thickness)
	{
		Pen pen = new Pen(brush, thickness)
		{
			StartLineCap = PenLineCap.Round,
			EndLineCap = PenLineCap.Round,
			LineJoin = PenLineJoin.Round
		};
		pen.Freeze();
		return pen;
	}
}
