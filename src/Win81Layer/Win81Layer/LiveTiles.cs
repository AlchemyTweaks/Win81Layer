using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Win81Layer;

public static class LiveTiles
{
	private static string? _deskFaceKey;

	private static Brush? _deskFaceBrush;

	private static List<string>? _photoPaths;

	private static int _photoIdx = -1;

	private static readonly Random _photoRng = new Random();

	public static TileVm Create(LiveKind kind)
	{
		switch (kind)
		{
		case LiveKind.Desktop:
		{
			AppEntry deskEntry = new AppEntry
			{
				Name = "Desktop",
				LaunchPath = "win81:desktop",
				TileBrush = DesktopFace()
			};
			return new TileVm
			{
				Entry = deskEntry,
				Live = LiveKind.Desktop,
				Size = TileSize.Wide
			};
		}
		case LiveKind.Photo:
		{
			string pics = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
			AppEntry photoEntry = new AppEntry
			{
				Name = "Photos",
				LaunchPath = pics,
				TileBrush = PhotoFace(0)
			};
			return new TileVm
			{
				Entry = photoEntry,
				Live = LiveKind.Photo,
				Size = TileSize.Wide
			};
		}
		default:
		{
			if (1 == 0)
			{
			}
			(string, string, string, TileSize) tuple = kind switch
			{
				LiveKind.Clock => ("Clock", "ms-settings:dateandtime", "#FF61A400", TileSize.Wide),
				LiveKind.Calendar => ("Calendar", "outlookcal:", "#FF2672EC", TileSize.Medium),
				LiveKind.Weather => ("Weather", "https://www.google.com/search?q=weather", "#FF2672EC", TileSize.Wide),
				LiveKind.Mail => ("Mail", "https://mail.google.com/", "#FF0B70CB", TileSize.Medium),
				LiveKind.Agenda => ("Agenda", "https://calendar.google.com/", "#FF5133AB", TileSize.Medium),
				LiveKind.News => ("News", "win81:news", "#FFB91D47", TileSize.Wide),
				_ => ("Live", "", "#FF333333", TileSize.Medium), 
			};
			if (1 == 0)
			{
			}
			(string, string, string, TileSize) tuple2 = tuple;
			string name = tuple2.Item1;
			string launch = tuple2.Item2;
			string color = BrandColorHex(kind, SettingsStore.Current.Replace81AppIcons);
			TileSize size = tuple2.Item4;
			Brush brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));
			((Freezable)brush).Freeze();
			AppEntry entry = new AppEntry
			{
				Name = name,
				LaunchPath = launch,
				TileBrush = brush
			};
			return new TileVm
			{
				Entry = entry,
				Live = kind,
				Size = size
			};
		}
		}
	}

	// Live-tile brand colour. When the "Win8.1 app icons" toggle is ON, use the authentic 8.1 colours (matching the
	// supplied tile art); when OFF, revert to the launcher's original colours — so the whole custom set toggles together.
	public static string BrandColorHex(LiveKind kind, bool replace81)
	{
		if (replace81)
		{
			return kind switch
			{
				// Exact background colours from the authentic Win8.1 SVGs the user supplied (kept verbatim).
				LiveKind.Clock => "#FFAC193D",
				LiveKind.Calendar => "#FF5132AA",
				LiveKind.Weather => "#FF2671EC",
				LiveKind.Mail => "#FF0B70CB",
				LiveKind.Agenda => "#FF5133AB",
				LiveKind.News => "#FFB91D47",
				_ => "#FF333333",
			};
		}
		return kind switch
		{
			LiveKind.Clock => "#FF00ABA9",
			LiveKind.Calendar => "#FF2672EC",
			LiveKind.Weather => "#FF1BA1E2",
			LiveKind.Mail => "#FF2D89EF",
			LiveKind.Agenda => "#FFDA532C",
			LiveKind.News => "#FFB91D47",
			_ => "#FF333333",
		};
	}

	public static Brush BrandBrush(LiveKind kind)
	{
		SolidColorBrush b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(BrandColorHex(kind, SettingsStore.Current.Replace81AppIcons)));
		((Freezable)b).Freeze();
		return b;
	}

	private static ImageSource? _deskSmallIcon;

	// The authentic Win8.1 purple "Desktop" tile glyph, shown ONLY on the SMALL Desktop tile (via the
	// IsDesktop+IsSmall MultiDataTrigger in StartScreen.xaml). Medium/Wide/Large keep the live wallpaper
	// preview (DesktopFace). Full-bleed purple PNG + white monitor glyph, loaded once + frozen.
	public static ImageSource? DesktopSmallIcon()
	{
		if (_deskSmallIcon != null)
		{
			return _deskSmallIcon;
		}
		try
		{
			string file = Path.Combine(AppContext.BaseDirectory, "Assets", "Win81Icons", "uwp", "Desktop81.png");
			if (File.Exists(file))
			{
				BitmapImage bi = new BitmapImage();
				bi.BeginInit();
				bi.UriSource = new Uri(file);
				bi.CacheOption = BitmapCacheOption.OnLoad;
				bi.EndInit();
				((Freezable)bi).Freeze();
				_deskSmallIcon = bi;
			}
		}
		catch
		{
		}
		return _deskSmallIcon;
	}

	public static Brush DesktopFace()
	{
		try
		{
			string wp = TaskbarTheme.WallpaperPath();
			if (!string.IsNullOrEmpty(wp) && File.Exists(wp))
			{
				string key = wp + "|" + File.GetLastWriteTimeUtc(wp).Ticks;
				if (_deskFaceBrush != null && _deskFaceKey == key)
				{
					return _deskFaceBrush;
				}
				BitmapImage bmp = new BitmapImage();
				bmp.BeginInit();
				bmp.CacheOption = BitmapCacheOption.OnLoad;
				bmp.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
				bmp.DecodePixelWidth = 320;
				bmp.UriSource = new Uri(wp);
				bmp.EndInit();
				((Freezable)bmp).Freeze();
				ImageBrush ib = new ImageBrush(bmp)
				{
					Stretch = Stretch.UniformToFill
				};
				((Freezable)ib).Freeze();
				_deskFaceKey = key;
				_deskFaceBrush = ib;
				return ib;
			}
		}
		catch
		{
		}
		SolidColorBrush fallback = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#FF1F1F1F"));
		((Freezable)fallback).Freeze();
		return fallback;
	}

	private static List<string> PhotoPaths()
	{
		if (_photoPaths != null)
		{
			return _photoPaths;
		}
		List<string> list = new List<string>();
		try
		{
			string pics = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
			if (Directory.Exists(pics))
			{
				string[] exts = new string[4] { ".jpg", ".jpeg", ".png", ".bmp" };
				list = (from f in Directory.EnumerateFiles(pics, "*.*", SearchOption.AllDirectories)
					where exts.Contains(Path.GetExtension(f).ToLowerInvariant())
					select f).Take(300).ToList();
			}
		}
		catch
		{
		}
		_photoPaths = list;
		return list;
	}

	private static Brush ImageFace(string path)
	{
		BitmapImage bmp = new BitmapImage();
		bmp.BeginInit();
		bmp.CacheOption = BitmapCacheOption.OnLoad;
		bmp.DecodePixelWidth = 320;
		bmp.UriSource = new Uri(path);
		bmp.EndInit();
		((Freezable)bmp).Freeze();
		ImageBrush ib = new ImageBrush(bmp)
		{
			Stretch = Stretch.UniformToFill
		};
		((Freezable)ib).Freeze();
		return ib;
	}

	public static Brush PhotoFace(int index)
	{
		List<string> paths = PhotoPaths();
		if (paths.Count > 0)
		{
			try
			{
				return ImageFace(paths[(index % paths.Count + paths.Count) % paths.Count]);
			}
			catch
			{
			}
		}
		SolidColorBrush fallback = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#FF1F1F1F"));
		((Freezable)fallback).Freeze();
		return fallback;
	}

	public static Brush NextPhotoFace()
	{
		List<string> paths = PhotoPaths();
		if (paths.Count == 0)
		{
			return PhotoFace(0);
		}
		if (paths.Count == 1)
		{
			_photoIdx = 0;
			return PhotoFace(0);
		}
		int next;
		do
		{
			next = _photoRng.Next(paths.Count);
		}
		while (next == _photoIdx);
		_photoIdx = next;
		return PhotoFace(_photoIdx);
	}
}
