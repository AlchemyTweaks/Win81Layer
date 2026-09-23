#nullable enable
using System;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Win81Layer;

internal static class MetroLiveTileArt
{
	private static readonly Typeface Light = new(new FontFamily("Segoe UI Light"), FontStyles.Normal, FontWeights.Light, FontStretches.Normal);
	private static readonly Typeface Regular = new(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
	internal static readonly ImageSource NewsLogo = CreateNewsLogo();
	private static readonly Lazy<ImageSource> ClockBackground = new(() => LoadStationaryBackground("clock-stationary.png", Color.FromRgb(14, 64, 69)));
	private static readonly Lazy<ImageSource> AgendaBackground = new(() => LoadStationaryBackground("agenda-stationary.png", Color.FromRgb(91, 34, 52)));
	private static ImageSource LoadStationaryBackground(string name, Color fallback)
	{
		try
		{
			BitmapImage image = new();
			image.BeginInit();
			image.CacheOption = BitmapCacheOption.OnLoad;
			image.DecodePixelWidth = 640;
			image.UriSource = new Uri("pack://application:,,,/assets/MetroTiles/" + name, UriKind.Absolute);
			image.EndInit();
			image.Freeze();
			return image;
		}
		catch (Exception ex)
		{
			Logger.Log("Stationary tile background: " + ex.GetType().Name);
			DrawingImage image = new(new GeometryDrawing(new SolidColorBrush(fallback), null, new RectangleGeometry(new Rect(0, 0, 1, 1))));
			image.Freeze();
			return image;
		}
	}
	private static ImageSource CreateNewsLogo()
	{
		DrawingGroup drawing = new();
		using (DrawingContext dc = drawing.Open())
		{
			dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, 100, 100));
			DrawNews(dc, new Rect(14, 22, 72, 56));
		}
		drawing.Freeze(); DrawingImage image = new(drawing); image.Freeze(); return image;
	}
	internal static void Preload()
	{
		_ = ClockBackground.Value;
		_ = AgendaBackground.Value;
	}
	internal static MetroTileVisual Build(LiveKind kind, TileSize size, DateTimeOffset now,
		GoogleServices.AgendaSnapshot? agenda = null, bool signedIn = false,
		NewsFeedService.Feed? news = null, int articleIndex = 0, bool unavailable = false)
	{
		double scale = TileMetrics.Scale;
		double w = (size is TileSize.Large or TileSize.Wide ? 310 : size == TileSize.Small ? 70 : 150) * scale;
		double h = (size == TileSize.Large ? 310 : size == TileSize.Small ? 70 : 150) * scale;
		double p = (size == TileSize.Small ? 8 : 14) * scale;
		DrawingGroup drawing = new();
		string accessible = kind.ToString();
		using (DrawingContext dc = drawing.Open())
		{
			dc.DrawRectangle(new SolidColorBrush(Color.FromArgb(kind == LiveKind.News ? (byte)0 : (byte)65, 0, 0, 0)), null, new Rect(0, 0, w, h));
			void Text(string text, double x, double y, double fontSize, double maxWidth, double maxHeight, bool light = false)
			{
				if (string.IsNullOrWhiteSpace(text) || maxWidth <= 0 || maxHeight <= 0) return;
				FormattedText ft = new(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, light ? Light : Regular, fontSize * scale, Brushes.White, 1)
				{ MaxTextWidth = maxWidth, MaxTextHeight = maxHeight, Trimming = TextTrimming.CharacterEllipsis };
				dc.DrawText(ft, new Point(x, y));
			}
			if (kind == LiveKind.Clock)
			{
				accessible = now.ToString("HH:mm, dddd, d MMMM yyyy") + ". " + TimeZoneInfo.Local.DisplayName;
				if (size is TileSize.Small or TileSize.Medium) DrawClock(dc, w / 2, h / 2, Math.Min(w, h) * .30, now);
				else if (size == TileSize.Wide)
				{
					Text(now.ToString("HH:mm"), p, 22 * scale, 47, w * .55, 65 * scale, true);
					Text(now.ToString("dddd"), w * .61, 36 * scale, 17, w * .39 - p, 25 * scale, true);
					Text(now.ToString("d MMM"), w * .61, 64 * scale, 19, w * .39 - p, 30 * scale, true);
				}
				else
				{
					Text(now.ToString("HH:mm"), p, 22 * scale, 78, w - p * 2, 104 * scale, true);
					Text(now.ToString("dddd"), p, 134 * scale, 29, w - p * 2, 43 * scale, true);
					Text(now.ToString("d MMMM yyyy"), p, 182 * scale, 21, w - p * 2, 56 * scale, true);
					Text(now.ToString("zzz") + "  " + TimeZoneInfo.Local.StandardName, p, 249 * scale, 12, w - p * 2, 18 * scale);
				}
			}
			else if (kind == LiveKind.Agenda)
			{
				var events = agenda?.Events.Where(e => e.End > now).Take(3).ToArray() ?? Array.Empty<GoogleServices.AgendaEvent>();
				bool stale = agenda?.Stale == true || (agenda != null && DateTimeOffset.UtcNow - agenda.FetchedAt > TimeSpan.FromMinutes(15));
				string status = !signedIn ? "Sign in to Google Calendar" : agenda == null ? (unavailable ? "Calendar unavailable" : "Loading calendar") : stale ? "Offline data" : events.Length == 0 ? "No upcoming events" : "Upcoming";
				accessible = "Agenda, " + status + ". " + string.Join(". ", events.Select(e => EventTime(e, now) + ", " + e.Title));
				if (size is TileSize.Small or TileSize.Medium)
				{
					DrawCalendar(dc, new Rect(w * .25, h * .23, w * .50, h * .54), now.Day);
				}
				else
				{
					double x = size == TileSize.Wide ? w * .31 : p;
					Text(now.Day.ToString(), p, 6 * scale, size == TileSize.Wide ? 49 : 58, size == TileSize.Wide ? w * .27 : w - p * 2, 90 * scale, true);
					Text(now.ToString("ddd, MMM"), p, size == TileSize.Wide ? 65 * scale : 83 * scale, 15, size == TileSize.Wide ? w * .27 : w - p * 2, 24 * scale, true);
					double y = size == TileSize.Wide ? 16 * scale : 123 * scale;
					if (events.Length == 0) Text(status, x, y, size == TileSize.Wide ? 17 : 21, w - x - p, h - y - 35 * scale, true);
					else
					{
						foreach (var ev in events.Take(size == TileSize.Wide ? 1 : 2))
						{
							Text(EventTime(ev, now), x, y, 13, w - x - p, 20 * scale);
							Text(ev.Title, x, y + 21 * scale, 19, w - x - p, 54 * scale, true);
							y += 80 * scale;
						}
						if (stale) Text("Offline data", w - 86 * scale, h - 23 * scale, 10, 76 * scale, 16 * scale);
					}
				}
			}
			else
			{
				NewsFeedService.Article? article = news?.Articles.Length > 0 ? news.Articles[Math.Abs(articleIndex) % news.Articles.Length] : null;
				string status = article?.Title ?? (news != null ? "No headlines" : unavailable ? "News unavailable" : "Loading headlines");
				accessible = "News, " + (news?.Title ?? "Naftemporiki") + ". " + status + (news?.Stale == true ? ". Offline data" : "");
				if (size is TileSize.Small or TileSize.Medium) DrawNews(dc, new Rect(w * .22, h * .26, w * .56, h * .48));
				else
				{
					Text(news?.Title ?? "News", p, p, 13, w - p * 2, 23 * scale);
					Text(status, p, 42 * scale, size == TileSize.Large ? 27 : 22, w - p * 2, (size == TileSize.Large ? 140 : 74) * scale, true);
					if (size == TileSize.Large && article != null)
						Text(article.Summary, p, 194 * scale, 14, w - p * 2, 63 * scale);
					string stamp = news?.Stale == true ? "Offline data" : article?.Published?.ToLocalTime().ToString("HH:mm") ?? "";
					Text(stamp, w - 96 * scale, h - 23 * scale, 10, 82 * scale, 17 * scale);
				}
			}
			if (size is TileSize.Wide or TileSize.Large) Text(kind.ToString(), p, h - 26 * scale, 12, w * .50, 20 * scale);
		}
		drawing.Freeze();
		DrawingImage foreground = new(drawing); foreground.Freeze();
		ImageSource background;
		if (kind == LiveKind.News)
		{
			DrawingImage brand = new(new GeometryDrawing(LiveTiles.BrandBrush(kind), null, new RectangleGeometry(new Rect(0, 0, 1, 1))));
			brand.Freeze(); background = brand;
		}
		else background = kind == LiveKind.Clock ? ClockBackground.Value : AgendaBackground.Value;
		return new MetroTileVisual { Background = background, Foreground = foreground, Key = accessible + size,
			AccessibleName = accessible, Size = size, Width = w, Height = h, Tint = Colors.Transparent, AnimateBackground = false };
	}
	private static string EventTime(GoogleServices.AgendaEvent ev, DateTimeOffset now) => (ev.Start.Date == now.Date ? "Today" : ev.Start.ToString("ddd d MMM")) + "  " + (ev.AllDay ? "All day" : ev.Start.ToString("HH:mm"));
	private static void DrawClock(DrawingContext dc, double x, double y, double r, DateTimeOffset now)
	{
		Pen pen = new(Brushes.White, Math.Max(1.5, r * .065)) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
		dc.DrawEllipse(null, pen, new Point(x, y), r, r);
		for (int i = 0; i < 12; i++)
		{
			double a = i * Math.PI / 6;
			dc.DrawLine(pen, new Point(x + Math.Sin(a) * r * .84, y - Math.Cos(a) * r * .84), new Point(x + Math.Sin(a) * r * .91, y - Math.Cos(a) * r * .91));
		}
		double hour = (now.Hour % 12 + now.Minute / 60d) * Math.PI / 6, minute = now.Minute * Math.PI / 30;
		dc.DrawLine(pen, new Point(x, y), new Point(x + Math.Sin(hour) * r * .48, y - Math.Cos(hour) * r * .48));
		dc.DrawLine(pen, new Point(x, y), new Point(x + Math.Sin(minute) * r * .71, y - Math.Cos(minute) * r * .71));
	}
	private static void DrawCalendar(DrawingContext dc, Rect r, int day)
	{
		Pen p = new(Brushes.White, Math.Max(1.5, r.Width * .05));
		dc.DrawRectangle(null, p, r);
		dc.DrawLine(p, new Point(r.Left, r.Top + r.Height * .22), new Point(r.Right, r.Top + r.Height * .22));
		foreach (double x in new[] { .25, .75 }) dc.DrawLine(p, new Point(r.Left + x * r.Width, r.Top - r.Height * .1), new Point(r.Left + x * r.Width, r.Top + r.Height * .1));
		FormattedText text = new(day.ToString(), CultureInfo.CurrentCulture, FlowDirection.LeftToRight, Light, r.Height * .47, Brushes.White, 1);
		dc.DrawText(text, new Point(r.Left + (r.Width - text.Width) / 2, r.Top + r.Height * .26));
	}
	internal static void DrawNews(DrawingContext dc, Rect r)
	{
		Pen p = new(Brushes.White, Math.Max(1.4, r.Width * .045));
		dc.DrawRectangle(null, p, r);
		dc.DrawRectangle(Brushes.White, null, new Rect(r.Left + r.Width * .12, r.Top + r.Height * .18, r.Width * .28, r.Height * .32));
		for (int i = 0; i < 3; i++)
			dc.DrawLine(p, new Point(r.Left + r.Width * .52, r.Top + r.Height * (.20 + i * .15)), new Point(r.Right - r.Width * .1, r.Top + r.Height * (.20 + i * .15)));
		for (int i = 0; i < 2; i++)
			dc.DrawLine(p, new Point(r.Left + r.Width * .12, r.Top + r.Height * (.67 + i * .15)), new Point(r.Right - r.Width * .1, r.Top + r.Height * (.67 + i * .15)));
	}
}
