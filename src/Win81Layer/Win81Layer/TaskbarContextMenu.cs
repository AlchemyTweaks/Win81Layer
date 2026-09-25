using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;

namespace Win81Layer;

public static class TaskbarContextMenu
{
	private static readonly Dictionary<string, ImageSource?> _exeIconCache = new Dictionary<string, ImageSource>(StringComparer.OrdinalIgnoreCase);

	private static readonly Dictionary<string, ImageSource?> _asset81Cache = new Dictionary<string, ImageSource>(StringComparer.OrdinalIgnoreCase);

	private static readonly Dictionary<string, ImageSource?> _extIconCache = new Dictionary<string, ImageSource>(StringComparer.OrdinalIgnoreCase);

	private static readonly Dictionary<int, ImageSource?> _stockIconCache = new Dictionary<int, ImageSource?>();

	// Official Microsoft stock shell icons (version-safe across Windows releases). SIID_* values:
	internal const int SIID_FOLDER = 3;
	internal const int SIID_FOLDEROPEN = 4;
	internal const int SIID_SHIELD = 77;   // UAC shield — for "Run as administrator"
	internal const int SIID_RENAME = 56;
	internal const int SIID_DELETE = 57;
	internal const int SIID_FIND = 22;

	private static readonly object ThemeGate = new object();

	private static Dictionary<string, Brush>? _themePalette;

	private static bool _themePaletteGlass;

	private static bool _themePaletteLight;

	private static bool _preferenceHooked;

	internal static Style ItemStyle => (Style)Application.Current.Resources["Win81MenuItem"];

	internal static Style SepStyle => (Style)Application.Current.Resources["Win81Separator"];

	public static ContextMenu Build(TaskbarWindow owner)
	{
		ResourceDictionary res = Application.Current.Resources;
		ContextMenu ctx = new ContextMenu
		{
			Style = (Style)res["Win81ContextMenu"]
		};
		ApplyTheme(ctx);
		AppSettings s = SettingsStore.FastSnapshot;
		string desktopPath = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
		string linksPath = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Links");
		HashSet<string> tbSet = new HashSet<string>(s.TaskbarToolbars ?? new List<string>(), StringComparer.OrdinalIgnoreCase);
		MenuItem toolbars = Sub("Toolbars",
			Check("Address", s.TaskbarAddressBar, delegate(bool on) { SetAddressBar(on); }),
			Check("Links", tbSet.Contains(linksPath), delegate(bool on) { SetToolbar(linksPath, on); }),
			Check("Desktop", tbSet.Contains(desktopPath), delegate(bool on) { SetToolbar(desktopPath, on); }),
			Leaf("New toolbar…", delegate { PickNewToolbar(); }));
		MenuItem search = Sub("Search", 57626, Leaf("Open search", delegate
		{
			TaskbarWindow.RaiseSearch();
		}));
		MenuItem combine = Sub("Taskbar buttons", CombineRadio("Always combine", "Always", s.TaskbarCombine == "Always"), CombineRadio("Combine when full", "WhenFull", s.TaskbarCombine == "WhenFull"), CombineRadio("Never combine", "Never", s.TaskbarCombine == "Never"));
		MenuItem cascade = Leaf("Cascade windows", delegate
		{
			ShellCmd("CascadeWindows");
		});
		MenuItem stacked = Leaf("Show windows stacked", delegate
		{
			ShellCmd("TileHorizontally");
		});
		MenuItem side = Leaf("Show windows side by side", delegate
		{
			ShellCmd("TileVertically");
		});
		MenuItem desktop = Leaf("Show the desktop", delegate
		{
			ShellCmd("ToggleDesktop");
		});
		MenuItem taskmgr = Leaf81("Task Manager", "taskmgr.png", 57832, delegate
		{
			Launch("taskmgr.exe");
		});
		MenuItem lockItem = Check("Lock the taskbar", s.TaskbarLocked, delegate(bool on)
		{
			AppSettings appSettings = SettingsStore.Load();
			appSettings.TaskbarLocked = on;
			SettingsStore.Save(appSettings);
		});
		lockItem.Icon = MenuGlyph(57846);
		MenuItem autoHide = Check("Automatically hide the taskbar", s.TaskbarAutoHide, delegate(bool on)
		{
			AppSettings appSettings = SettingsStore.Load();
			appSettings.TaskbarAutoHide = on;
			SettingsStore.Save(appSettings);
			TaskbarWindow.RaiseAutoHideChanged();
		});
		MenuItem position = Sub("Taskbar position", PositionRadio("Bottom", s.TaskbarPosition), PositionRadio("Top", s.TaskbarPosition), PositionRadio("Left", s.TaskbarPosition), PositionRadio("Right", s.TaskbarPosition));
		MenuItem size = Sub("Taskbar size", SizeRadio("Small", s.TaskbarSize == "Small"), SizeRadio("Medium", s.TaskbarSize != "Small" && s.TaskbarSize != "Large"), SizeRadio("Large", s.TaskbarSize == "Large"));
		MenuItem align = Sub("Alignment", 59620, AlignRadio("Left", s.TaskbarAlignment != "Center"), AlignRadio("Center", s.TaskbarAlignment == "Center"));
		MenuItem showBtns = Sub("Show buttons", 59165, Check("Search", s.ShowSearch, delegate(bool on)
		{
			SetShowButton(delegate(AppSettings b)
			{
				b.ShowSearch = on;
			});
		}), Check("Task view", s.ShowTaskView, delegate(bool on)
		{
			SetShowButton(delegate(AppSettings b)
			{
				b.ShowTaskView = on;
			});
		}), Check("Action center", s.ShowActionCenter, delegate(bool on)
		{
			SetShowButton(delegate(AppSettings b)
			{
				b.ShowActionCenter = on;
			});
		}));
		MenuItem displays = Sub("Multiple displays", Radio("Same buttons on all displays", s.TaskbarSameOnAllDisplays, delegate
		{
			SetSameAll(on: true);
		}), Radio("Per display (Win8.1)", !s.TaskbarSameOnAllDisplays, delegate
		{
			SetSameAll(on: false);
		}));
		MenuItem colour = Sub("Taskbar colour", 59280, ColorRadio("Automatic (desktop)", s.TaskbarColorMode != "Start", "Wallpaper"), ColorRadio("Match Start screen", s.TaskbarColorMode == "Start", "Start"), Sep(), Check("Transparent", s.TaskbarTransparent || s.TaskbarColorMode == "Transparent", delegate(bool on)
		{
			AppSettings appSettings = SettingsStore.Load();
			appSettings.TaskbarTransparent = on;
			if (appSettings.TaskbarColorMode == "Transparent")
			{
				appSettings.TaskbarColorMode = "Wallpaper";
			}
			SettingsStore.Save(appSettings);
			TaskbarWindow.RaiseTaskbarColorChanged();
		}));
		MenuItem systools = SystemToolsSub();
		MenuItem properties = Leaf("Properties", 59718, delegate
		{
			OpenUri("ms-settings:taskbar");
		});
		ctx.Items.Add(toolbars);
		ctx.Items.Add(search);
		ctx.Items.Add(combine);
		ctx.Items.Add(Sep());
		ctx.Items.Add(cascade);
		ctx.Items.Add(stacked);
		ctx.Items.Add(side);
		ctx.Items.Add(desktop);
		ctx.Items.Add(Sep());
		ctx.Items.Add(taskmgr);
		ctx.Items.Add(Sep());
		ctx.Items.Add(lockItem);
		ctx.Items.Add(autoHide);
		ctx.Items.Add(position);
		ctx.Items.Add(size);
		ctx.Items.Add(align);
		ctx.Items.Add(showBtns);
		ctx.Items.Add(displays);
		ctx.Items.Add(colour);
		ctx.Items.Add(Sep());
		ctx.Items.Add(systools);
		ctx.Items.Add(Sep());
		ctx.Items.Add(properties);
		MakeSticky(ctx);
		MenuItem[] array = new MenuItem[6] { combine, position, size, align, displays, colour };
		foreach (MenuItem group in array)
		{
			MakeRadioGroup(group);
		}
		ctx.Items.Add(Sep());
		ctx.Items.Add(Leaf("Close menu", 59579, delegate
		{
			ctx.IsOpen = false;
		}));
		return ctx;
	}

	private static void MakeSticky(ItemsControl parent)
	{
		foreach (object obj in (IEnumerable)parent.Items)
		{
			if (obj is MenuItem mi)
			{
				if (mi.HasItems)
				{
					MakeSticky(mi);
				}
				else if (mi.IsCheckable)
				{
					// only toggles/radios stay open (so several can be changed at once); plain action items close the menu
					mi.StaysOpenOnClick = true;
				}
			}
		}
	}

	private static void MakeRadioGroup(MenuItem submenu)
	{
		List<MenuItem> radios = (from m in submenu.Items.OfType<MenuItem>()
			where m.IsCheckable && m.Tag as string != "toggle"
			select m).ToList();
		if (radios.Count < 2)
		{
			return;
		}
		foreach (MenuItem r in radios)
		{
			MenuItem self = r;
			self.Click += delegate
			{
				foreach (MenuItem current in radios)
				{
					current.IsChecked = current == self;
				}
			};
		}
	}

	internal static MenuItem Leaf(string header, Action onClick)
	{
		MenuItem mi = new MenuItem
		{
			Header = header,
			Style = ItemStyle
		};
		mi.Click += delegate
		{
			QueueCommand(onClick);
		};
		return mi;
	}

	internal static MenuItem Leaf(string header, Action onClick, bool enabled)
	{
		MenuItem mi = Leaf(header, onClick);
		mi.IsEnabled = enabled;
		return mi;
	}

	internal static MenuItem Leaf(string header, int glyph, Action onClick)
	{
		MenuItem mi = Leaf(header, onClick);
		mi.Icon = MenuGlyph(glyph);
		return mi;
	}

	internal static MenuItem LeafCaptured<T>(string header, int glyph, Func<T> capture, Action<T> onClick)
	{
		MenuItem mi = new MenuItem
		{
			Header = header,
			Style = ItemStyle,
			Icon = MenuGlyph(glyph)
		};
		mi.Click += delegate
		{
			T state;
			try
			{
				state = capture();
			}
			catch (Exception ex)
			{
				Logger.Log("Menu command capture failed: " + ex.Message);
				return;
			}
			QueueCommand(delegate { onClick(state); });
		};
		return mi;
	}

	internal static MenuItem Leaf81Captured<T>(string header, string asset, int glyphFallback, Func<T> capture, Action<T> onClick)
	{
		MenuItem mi = LeafCaptured(header, glyphFallback, capture, onClick);
		ImageSource icon = Bundled81Icon(asset);
		mi.Icon = ((icon != null) ? ((FrameworkElement)MenuImage(icon)) : ((FrameworkElement)MenuGlyph(glyphFallback)));
		return mi;
	}

	internal static ImageSource? ExeIcon(string exeOrName)
	{
		if (_exeIconCache.TryGetValue(exeOrName, out ImageSource cached))
		{
			return cached;
		}
		ImageSource img = null;
		try
		{
			string path = (Path.IsPathRooted(exeOrName) ? exeOrName : (ResolveOnPath(exeOrName) ?? exeOrName));
			if (File.Exists(path))
			{
				img = AppInventory.LoadIcon(path);
				if (img != null && ((Freezable)img).CanFreeze)
				{
					((Freezable)img).Freeze();
				}
			}
		}
		catch
		{
		}
		if (_exeIconCache.Count >= 256) { _exeIconCache.Clear(); }
		_exeIconCache[exeOrName] = img;
		return img;
	}

	private static string? ResolveOnPath(string name)
	{
		string sys = Path.Combine(Environment.SystemDirectory, name);
		if (File.Exists(sys))
		{
			return sys;
		}
		string[] array = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';');
		foreach (string dir in array)
		{
			if (string.IsNullOrWhiteSpace(dir))
			{
				continue;
			}
			try
			{
				string cand = Path.Combine(dir, name);
				if (File.Exists(cand))
				{
					return cand;
				}
			}
			catch
			{
			}
		}
		return null;
	}

	internal static MenuItem LeafExe(string header, string exe, int glyphFallback, Action onClick)
	{
		MenuItem mi = Leaf(header, onClick);
		ImageSource icon = ExeIcon(exe);
		mi.Icon = ((icon != null) ? ((FrameworkElement)MenuImage(icon)) : ((FrameworkElement)MenuGlyph(glyphFallback)));
		return mi;
	}

	internal static MenuItem Leaf81(string header, string asset, int glyphFallback, Action onClick)
	{
		MenuItem mi = Leaf(header, onClick);
		ImageSource icon = Bundled81Icon(asset);
		mi.Icon = ((icon != null) ? ((FrameworkElement)MenuImage(icon)) : ((FrameworkElement)MenuGlyph(glyphFallback)));
		return mi;
	}

	// Leaf whose icon is the OS's authentic file-type icon for an extension (as Explorer shows it),
	// e.g. ".docx" -> Word icon, ".txt" -> text icon. Falls back to a glyph if the shell has none.
	internal static MenuItem LeafFileIcon(string header, string ext, int glyphFallback, Action onClick)
	{
		MenuItem mi = Leaf(header, onClick);
		ImageSource icon = ShellExtIcon(ext);
		mi.Icon = ((icon != null) ? ((FrameworkElement)MenuImage(icon)) : ((FrameworkElement)MenuGlyph(glyphFallback)));
		return mi;
	}

	// Leaf whose icon is the OS's authentic folder icon.
	internal static MenuItem LeafFolderIcon(string header, int glyphFallback, Action onClick)
	{
		MenuItem mi = Leaf(header, onClick);
		ImageSource icon = ShellFolderIcon();
		mi.Icon = ((icon != null) ? ((FrameworkElement)MenuImage(icon)) : ((FrameworkElement)MenuGlyph(glyphFallback)));
		return mi;
	}

	// Like LeafFolderIcon but uses the authentic shell file-type icon for an extension (e.g. ".lnk" -> the real
	// shortcut icon) instead of a colored glyph tile — used where the item represents a real file kind.
	internal static MenuItem LeafExtIcon(string header, string ext, int glyphFallback, Action onClick)
	{
		MenuItem mi = Leaf(header, onClick);
		ImageSource? icon = ShellExtIcon(ext);
		mi.Icon = ((icon != null) ? ((FrameworkElement)MenuImage(icon)) : ((FrameworkElement)MenuGlyph(glyphFallback)));
		return mi;
	}

	[System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential, CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
	private struct SHFILEINFO
	{
		public nint hIcon;

		public int iIcon;

		public uint dwAttributes;

		[System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.ByValTStr, SizeConst = 260)]
		public string szDisplayName;

		[System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.ByValTStr, SizeConst = 80)]
		public string szTypeName;
	}

	[System.Runtime.InteropServices.DllImport("shell32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
	private static extern nint SHGetFileInfo(string pszPath, uint dwFileAttributes, ref SHFILEINFO psfi, uint cbSizeFileInfo, uint uFlags);

	[System.Runtime.InteropServices.DllImport("user32.dll")]
	private static extern bool DestroyIcon(nint hIcon);

	[System.Runtime.InteropServices.DllImport("shell32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
	private static extern int SHEmptyRecycleBin(nint hwnd, string? pszRootPath, uint dwFlags);

	[System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential, CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
	private struct SHSTOCKICONINFO
	{
		public int cbSize;
		public nint hIcon;
		public int iSysImageIndex;
		public int iIcon;
		[System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.ByValTStr, SizeConst = 260)]
		public string szPath;
	}

	[System.Runtime.InteropServices.DllImport("shell32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
	private static extern int SHGetStockIconInfo(int siid, uint uFlags, ref SHSTOCKICONINFO psii);

	// Official Microsoft stock shell icon by SIID (cached, frozen). Returns null on failure so callers glyph-fall-back.
	internal static ImageSource? StockIcon(int siid)
	{
		if (_stockIconCache.TryGetValue(siid, out ImageSource cached))
		{
			return cached;
		}
		ImageSource img = null;
		nint hIcon = IntPtr.Zero;
		try
		{
			SHSTOCKICONINFO sii = default(SHSTOCKICONINFO);
			sii.cbSize = System.Runtime.InteropServices.Marshal.SizeOf<SHSTOCKICONINFO>();
			// SHGSI_ICON(0x100) | SHGSI_SMALLICON(0x1)
			if (SHGetStockIconInfo(siid, 0x101u, ref sii) == 0 && sii.hIcon != IntPtr.Zero)
			{
				hIcon = sii.hIcon;
				BitmapSource bs = System.Windows.Interop.Imaging.CreateBitmapSourceFromHIcon(hIcon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
				((Freezable)bs).Freeze();
				img = bs;
			}
		}
		catch
		{
		}
		finally
		{
			if (hIcon != IntPtr.Zero)
			{
				DestroyIcon(hIcon);
			}
		}
		_stockIconCache[siid] = img;
		return img;
	}

	// Menu icon element that prefers the authentic Microsoft stock icon and falls back to the Segoe glyph.
	internal static FrameworkElement MenuStockIcon(int siid, int glyphFallback)
	{
		ImageSource? stock = StockIcon(siid);
		return (stock != null) ? (FrameworkElement)MenuImage(stock) : (FrameworkElement)MenuGlyph(glyphFallback);
	}

	// Authentic file-type icon for a bare extension via the shell (SHGFI_USEFILEATTRIBUTES => no real file needed).
	internal static ImageSource? ShellExtIcon(string ext)
	{
		if (string.IsNullOrEmpty(ext))
		{
			return null;
		}
		if (!ext.StartsWith('.'))
		{
			ext = "." + ext;
		}
		if (_extIconCache.TryGetValue(ext, out ImageSource cached))
		{
			return cached;
		}
		ImageSource img = null;
		nint hIcon = IntPtr.Zero;
		try
		{
			SHFILEINFO info = default(SHFILEINFO);
			// SHGFI_ICON(0x100) | SHGFI_SMALLICON(0x1) | SHGFI_USEFILEATTRIBUTES(0x10); FILE_ATTRIBUTE_NORMAL(0x80)
			SHGetFileInfo("file" + ext, 128u, ref info, (uint)System.Runtime.InteropServices.Marshal.SizeOf<SHFILEINFO>(), 273u);
			hIcon = info.hIcon;
			if (hIcon != IntPtr.Zero)
			{
				BitmapSource bs = System.Windows.Interop.Imaging.CreateBitmapSourceFromHIcon(hIcon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
				((Freezable)bs).Freeze();
				img = bs;
			}
		}
		catch
		{
		}
		finally
		{
			if (hIcon != IntPtr.Zero)
			{
				DestroyIcon(hIcon);
			}
		}
		if (_extIconCache.Count >= 256) { _extIconCache.Clear(); }
		_extIconCache[ext] = img;
		return img;
	}

	// Authentic folder icon via the shell (SHGFI_USEFILEATTRIBUTES + FILE_ATTRIBUTE_DIRECTORY => no real dir needed).
	internal static ImageSource? ShellFolderIcon()
	{
		if (_extIconCache.TryGetValue("<dir>", out ImageSource cached))
		{
			return cached;
		}
		ImageSource img = null;
		nint hIcon = IntPtr.Zero;
		try
		{
			SHFILEINFO info = default(SHFILEINFO);
			// FILE_ATTRIBUTE_DIRECTORY(0x10); SHGFI_ICON|SHGFI_SMALLICON|SHGFI_USEFILEATTRIBUTES(0x111)
			SHGetFileInfo("folder", 16u, ref info, (uint)System.Runtime.InteropServices.Marshal.SizeOf<SHFILEINFO>(), 273u);
			hIcon = info.hIcon;
			if (hIcon != IntPtr.Zero)
			{
				BitmapSource bs = System.Windows.Interop.Imaging.CreateBitmapSourceFromHIcon(hIcon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
				((Freezable)bs).Freeze();
				img = bs;
			}
		}
		catch
		{
		}
		finally
		{
			if (hIcon != IntPtr.Zero)
			{
				DestroyIcon(hIcon);
			}
		}
		_extIconCache["<dir>"] = img;
		return img;
	}

	private static ImageSource? Bundled81Icon(string asset)
	{
		if (_asset81Cache.TryGetValue(asset, out ImageSource cached))
		{
			return cached;
		}
		ImageSource img = null;
		try
		{
			BitmapImage bmp = new BitmapImage();
			bmp.BeginInit();
			bmp.UriSource = new Uri("pack://application:,,,/Assets/Win81Icons/" + asset);
			bmp.CacheOption = BitmapCacheOption.OnLoad;
			bmp.EndInit();
			((Freezable)bmp).Freeze();
			img = bmp;
		}
		catch
		{
		}
		if (_asset81Cache.Count >= 256) { _asset81Cache.Clear(); }
		_asset81Cache[asset] = img;
		return img;
	}

	internal static void WarmCaches()
	{
		try
		{
			EnsurePreferenceHook();
			GetThemePalette();
			string[] array = new string[7] { "cmd.png", "powershell.png", "taskmgr.png", "control.png", "mmc.png", "resmon.png", "perfmon.png" };
			foreach (string a in array)
			{
				Bundled81Icon(a);
			}
			ExeIcon("explorer.exe");
			ShellFolderIcon();
			string[] ext = new string[7] { ".txt", ".bmp", ".rtf", ".docx", ".xlsx", ".pptx", ".zip" };
			foreach (string item in ext)
			{
				ShellExtIcon(item);
			}
			ShellNewItems.Warm();
			DesktopView.Warm();
		}
		catch
		{
		}
	}

	internal static void Warm()
	{
		WarmCaches();
		DesktopContextMenu.Warm();
		FileContextMenu.Warm();
	}

	private static Image MenuImage(ImageSource src)
	{
		Image img = new Image
		{
			Source = src,
			Width = 16.0,
			Height = 16.0,
			SnapsToDevicePixels = true
		};
		RenderOptions.SetBitmapScalingMode((DependencyObject)(object)img, BitmapScalingMode.HighQuality);
		return img;
	}

	// Colored Metro SQUARE tile with a white glyph — the dark-menu icon style (matches the reference mockup). The tile
	// colour is a deterministic pick from a small Metro palette so the menu shows varied blue/purple/teal/... tiles.
	internal static FrameworkElement MenuGlyph(int codepoint)
	{
		FrameworkElement child;
		if (codepoint == 57621 || codepoint == 59155)   // E115 / E713 Settings gear -> the ONE canonical launcher gear (white on the tile)
		{
			child = new System.Windows.Controls.Image
			{
				Source = SettingsGlyph.Gear(Colors.White),
				Width = 14.0,
				Height = 14.0,
				HorizontalAlignment = HorizontalAlignment.Center,
				VerticalAlignment = VerticalAlignment.Center
			};
		}
		else
		{
			bool sym = codepoint >= 57600 && codepoint <= 57855;
			child = new TextBlock
			{
				Text = ((char)codepoint).ToString(),
				FontFamily = new FontFamily(sym ? "Segoe UI Symbol" : "Segoe MDL2 Assets"),
				FontSize = (sym ? 11.0 : 10.0),
				Foreground = System.Windows.Media.Brushes.White,
				HorizontalAlignment = HorizontalAlignment.Center,
				VerticalAlignment = VerticalAlignment.Center
			};
		}
		return new Border
		{
			Width = 18.0,
			Height = 18.0,
			Background = TileBrushFor(codepoint),
			SnapsToDevicePixels = true,
			HorizontalAlignment = HorizontalAlignment.Center,
			VerticalAlignment = VerticalAlignment.Center,
			Child = child
		};
	}

	private static readonly Color[] TilePalette = new Color[7]
	{
		Color.FromRgb(0x2D, 0x7F, 0xF9),   // blue
		Color.FromRgb(0x82, 0x50, 0xDF),   // purple
		Color.FromRgb(0x00, 0xA3, 0xA3),   // teal
		Color.FromRgb(0x16, 0xA0, 0x1E),   // green
		Color.FromRgb(0xCA, 0x50, 0x10),   // orange
		Color.FromRgb(0xB4, 0x00, 0x9E),   // magenta
		Color.FromRgb(0x5A, 0x6B, 0x85)    // slate
	};

	private static readonly Dictionary<int, Brush> _tileBrushCache = new Dictionary<int, Brush>();

	private static Brush TileBrushFor(int codepoint)
	{
		lock (_tileBrushCache)
		{
			if (_tileBrushCache.TryGetValue(codepoint, out Brush cached))
			{
				return cached;
			}
			SolidColorBrush b = new SolidColorBrush(TilePalette[(uint)codepoint % (uint)TilePalette.Length]);
			b.Freeze();
			_tileBrushCache[codepoint] = b;
			return b;
		}
	}

	internal static MenuItem Check(string header, bool isChecked, Action<bool> onToggle)
	{
		MenuItem mi = new MenuItem
		{
			Header = header,
			Style = ItemStyle,
			IsCheckable = true,
			IsChecked = isChecked,
			Tag = "toggle"
		};
		mi.Click += delegate
		{
			QueueCommand(delegate
			{
				onToggle(mi.IsChecked);
			});
		};
		return mi;
	}

	private static MenuItem Radio(string header, bool isChecked, Action? onClick)
	{
		MenuItem mi = new MenuItem
		{
			Header = header,
			Style = ItemStyle,
			IsCheckable = true,
			IsChecked = isChecked
		};
		if (onClick == null)
		{
			mi.IsEnabled = isChecked;
		}
		else
		{
			mi.Click += delegate
			{
				QueueCommand(onClick);
			};
		}
		return mi;
	}

	internal static MenuItem Choice(string header, bool isChecked, Action onClick)
	{
		MenuItem mi = new MenuItem
		{
			Header = header,
			Style = ItemStyle,
			IsCheckable = true,
			IsChecked = isChecked,
			Tag = "radio"   // mutually-exclusive choice → 8.1 shows a filled bullet, not a checkmark
		};
		mi.Click += delegate
		{
			QueueCommand(onClick);
		};
		return mi;
	}

	internal static MenuItem Disabled(string header)
	{
		return new MenuItem
		{
			Header = header,
			Style = ItemStyle,
			IsEnabled = false
		};
	}

	// ---- classic taskbar "Toolbars" persistence (folders shown as shortcuts buttons next to the tray) ----

	internal static void SetToolbar(string path, bool on)
	{
		if (string.IsNullOrWhiteSpace(path))
		{
			return;
		}
		AppSettings s = SettingsStore.Load();
		if (s.TaskbarToolbars == null)
		{
			s.TaskbarToolbars = new List<string>();
		}
		bool has = s.TaskbarToolbars.Any((string p) => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
		if (on && !has)
		{
			s.TaskbarToolbars.Add(path);
		}
		else if (!on && has)
		{
			s.TaskbarToolbars.RemoveAll((string p) => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
		}
		else
		{
			return;
		}
		SettingsStore.Save(s);
		TaskbarWindow.RaiseToolbarsChanged();
	}

	internal static void RemoveToolbar(string path)
	{
		SetToolbar(path, on: false);
	}

	internal static void SetAddressBar(bool on)
	{
		AppSettings s = SettingsStore.Load();
		if (s.TaskbarAddressBar == on)
		{
			return;
		}
		s.TaskbarAddressBar = on;
		SettingsStore.Save(s);
		TaskbarWindow.RaiseToolbarsChanged();
	}

	private static void PickNewToolbar()
	{
		try
		{
			using System.Windows.Forms.FolderBrowserDialog dlg = new System.Windows.Forms.FolderBrowserDialog
			{
				Description = "Choose a folder to show as a taskbar toolbar (its shortcuts appear next to the tray)",
				UseDescriptionForTitle = true,
				ShowNewFolderButton = true
			};
			if (dlg.ShowDialog() == System.Windows.Forms.DialogResult.OK && !string.IsNullOrEmpty(dlg.SelectedPath))
			{
				SetToolbar(dlg.SelectedPath, on: true);
			}
		}
		catch (Exception ex)
		{
			Logger.Log("New toolbar picker failed: " + ex.Message);
		}
	}

	private static MenuItem DeskCompRadio(string header, string mode)
	{
		MenuItem mi = new MenuItem
		{
			Header = header,
			Style = ItemStyle,
			IsCheckable = true,
			IsChecked = (DesktopComposition.Mode == mode)
		};
		mi.Click += delegate
		{
			QueueCommand(delegate
			{
				DesktopComposition.SetMode(mode);
			});
		};
		return mi;
	}

	private static MenuItem PositionRadio(string edge, string current)
	{
		MenuItem mi = new MenuItem
		{
			Header = edge,
			Style = ItemStyle,
			IsCheckable = true,
			IsChecked = (current == edge)
		};
		mi.Click += delegate
		{
			QueueCommand(delegate
			{
				AppSettings appSettings = SettingsStore.Load();
				appSettings.TaskbarPosition = edge;
				SettingsStore.Save(appSettings);
				TaskbarWindow.RaiseTaskbarPositionChanged();
			});
		};
		return mi;
	}

	private static void SetSameAll(bool on)
	{
		AppSettings st = SettingsStore.Load();
		st.TaskbarSameOnAllDisplays = on;
		SettingsStore.Save(st);
		TaskbarWindow.RaiseTaskbarLayoutChanged();
	}

	private static void SetShowButton(Action<AppSettings> mutate)
	{
		AppSettings st = SettingsStore.Load();
		mutate(st);
		SettingsStore.Save(st);
		TaskbarWindow.RaiseTaskbarButtonsChanged();
	}

	private static MenuItem AlignRadio(string header, bool isChecked)
	{
		MenuItem mi = new MenuItem
		{
			Header = header,
			Style = ItemStyle,
			IsCheckable = true,
			IsChecked = isChecked
		};
		mi.Click += delegate
		{
			QueueCommand(delegate
			{
				AppSettings appSettings = SettingsStore.Load();
				appSettings.TaskbarAlignment = header;
				SettingsStore.Save(appSettings);
				TaskbarWindow.RaiseTaskbarAlignmentChanged();
			});
		};
		return mi;
	}

	private static MenuItem CombineRadio(string header, string mode, bool isChecked)
	{
		MenuItem mi = new MenuItem
		{
			Header = header,
			Style = ItemStyle,
			IsCheckable = true,
			IsChecked = isChecked
		};
		mi.Click += delegate
		{
			QueueCommand(delegate
			{
				AppSettings appSettings = SettingsStore.Load();
				appSettings.TaskbarCombine = mode;
				SettingsStore.Save(appSettings);
				TaskbarWindow.RaiseTaskbarLayoutChanged();
			});
		};
		return mi;
	}

	private static MenuItem SizeRadio(string header, bool isChecked)
	{
		MenuItem mi = new MenuItem
		{
			Header = header,
			Style = ItemStyle,
			IsCheckable = true,
			IsChecked = isChecked
		};
		mi.Click += delegate
		{
			QueueCommand(delegate
			{
				AppSettings appSettings = SettingsStore.Load();
				appSettings.TaskbarSize = header;
				SettingsStore.Save(appSettings);
				TaskbarWindow.RaiseTaskbarSizeChanged();
			});
		};
		return mi;
	}

	private static MenuItem ColorRadio(string header, bool isChecked, string mode)
	{
		MenuItem mi = new MenuItem
		{
			Header = header,
			Style = ItemStyle,
			IsCheckable = true,
			IsChecked = isChecked
		};
		mi.Click += delegate
		{
			QueueCommand(delegate
			{
				AppSettings appSettings = SettingsStore.Load();
				appSettings.TaskbarColorMode = mode;
				SettingsStore.Save(appSettings);
				TaskbarWindow.RaiseTaskbarColorChanged();
			});
		};
		return mi;
	}

	internal static MenuItem Sub(string header, params object[] children)
	{
		MenuItem mi = new MenuItem
		{
			Header = header,
			Style = ItemStyle
		};
		foreach (object c in children)
		{
			mi.Items.Add(c);
		}
		return mi;
	}

	internal static MenuItem Sub(string header, int glyph, params object[] children)
	{
		MenuItem mi = Sub(header, children);
		mi.Icon = MenuGlyph(glyph);
		return mi;
	}

	// Submenu with a bundled Win8.1 PNG icon (assets/win81icons/<asset>), falling back to a font glyph — the Sub
	// analogue of Leaf81.
	internal static MenuItem Sub81(string header, string asset, int glyphFallback, params object[] children)
	{
		MenuItem mi = Sub(header, children);
		ImageSource icon = Bundled81Icon(asset);
		mi.Icon = ((icon != null) ? ((FrameworkElement)MenuImage(icon)) : ((FrameworkElement)MenuGlyph(glyphFallback)));
		return mi;
	}

	internal static Separator Sep()
	{
		return new Separator
		{
			Style = SepStyle
		};
	}

	internal static void ApplyTheme(ContextMenu ctx)
	{
		foreach (KeyValuePair<string, Brush> pair in GetThemePalette())
		{
			ctx.Resources[pair.Key] = pair.Value;
		}
	}

	internal static void PrepareForInstantOpen(ContextMenu menu)
	{
		menu.ApplyTemplate();
		menu.Measure(new Size(560.0, 1400.0));
		menu.Arrange(new Rect(menu.DesiredSize));
		menu.UpdateLayout();
	}

	internal static void InvalidateTheme()
	{
		lock (ThemeGate)
		{
			_themePalette = null;
		}
	}

	private static Dictionary<string, Brush> GetThemePalette()
	{
		bool glass = ShellSkin.GlassOn;
		bool light = IsLightTheme();
		lock (ThemeGate)
		{
			if (_themePalette != null && _themePaletteGlass == glass && _themePaletteLight == light)
			{
				return _themePalette;
			}

			Color accent;
			try
			{
				accent = StartAccent.Color();
			}
			catch
			{
				accent = Color.FromRgb(61, 90, 115);
			}

			Dictionary<string, Brush> palette = new Dictionary<string, Brush>(StringComparer.Ordinal);
			if (glass)
			{
				Color tone = ShellSkin.AccentTone(0.30);
				Add("M.Bg", Color.FromArgb(0xB0, tone.R, tone.G, tone.B));
				AddHex("M.Fg", "#F0F0F0");
				AddHex("M.Dis", "#8E8E8E");
				Add("M.Hover", Color.FromArgb(0x66, accent.R, accent.G, accent.B));
				Add("M.Border", Color.FromArgb(0x66, byte.MaxValue, byte.MaxValue, byte.MaxValue));
				Add("M.Sep", Color.FromArgb(0x40, byte.MaxValue, byte.MaxValue, byte.MaxValue));
				AddHex("M.Arrow", "#C8C8C8");
				Add("M.HoverBorder", Color.FromArgb(0x88, accent.R, accent.G, accent.B));
				Add("M.KbBg", Color.FromArgb(0xFF, accent.R, accent.G, accent.B));
				AddHex("M.KbFg", "#FFFFFF");
				AddHex("M.KbGesture", "#D0D0D0");
			}
			else if (light)
			{
				// Windows 8 "Content Menu" pattern (the user's Patterns reference): WHITE surface, black text,
				// 2px black border, #DEDEDE mouse hover, and the keyboard-highlighted row inverts to solid black.
				// These LIGHT values are kept byte-identical to the authentic look.
				AddHex("M.Bg", "#FFFFFF");
				AddHex("M.Fg", "#000000");
				AddHex("M.Dis", "#767676");
				AddHex("M.Hover", "#DEDEDE");
				AddHex("M.HoverBorder", "#00FFFFFF");
				AddHex("M.Border", "#000000");
				AddHex("M.Sep", "#C8C8C8");
				AddHex("M.Arrow", "#000000");
				AddHex("M.KbBg", "#000000");
				AddHex("M.KbFg", "#FFFFFF");
				AddHex("M.KbGesture", "#C8C8C8");
			}
			else
			{
				// Dark menu (AppsUseLightTheme=0). Windows 8.1 shipped no dark context menu, so these values track
				// Windows 11 dark chrome and stay legible. The keyboard-highlighted row inverts to a WHITE bar with
				// black text (the mirror of the light-mode black bar / white text). Matches ShellTheme's Metro81.* dark set.
				AddHex("M.Bg", "#2B2B2B");
				AddHex("M.Fg", "#F2F2F2");
				AddHex("M.Dis", "#8A8A8A");
				AddHex("M.Hover", "#3F3F3F");
				AddHex("M.HoverBorder", "#00000000");
				AddHex("M.Border", "#5A5A5A");
				AddHex("M.Sep", "#3F3F3F");
				AddHex("M.Arrow", "#DDDDDD");
				AddHex("M.KbBg", "#FFFFFF");
				AddHex("M.KbFg", "#000000");
				AddHex("M.KbGesture", "#3A3A3A");
			}

			_themePaletteGlass = glass;
			_themePaletteLight = light;
			_themePalette = palette;
			return palette;

			void AddHex(string key, string hex) => Add(key, (Color)ColorConverter.ConvertFromString(hex));
			void Add(string key, Color color)
			{
				SolidColorBrush brush = new SolidColorBrush(color);
				brush.Freeze();
				palette[key] = brush;
			}
		}
	}

	private static void EnsurePreferenceHook()
	{
		lock (ThemeGate)
		{
			if (_preferenceHooked)
			{
				return;
			}
			SystemEvents.UserPreferenceChanged += delegate { InvalidateTheme(); };
			_preferenceHooked = true;
		}
	}

	private static bool IsLightTheme()
	{
		return ShellTheme.IsLight;   // single source of truth (cached + change-signalled)
	}

	private static void ShellCmd(string method)
	{
		ShellLaunch.Run(delegate
		{
			try
			{
				Type t = Type.GetTypeFromProgID("Shell.Application");
				if (t == null)
				{
					return;
				}
				dynamic shell = Activator.CreateInstance(t);
				if (!((shell == null) ? true : false))
				{
					switch (method)
					{
					case "CascadeWindows": shell.CascadeWindows(); break;
					case "TileHorizontally": shell.TileHorizontally(); break;
					case "TileVertically": shell.TileVertically(); break;
					case "ToggleDesktop": shell.ToggleDesktop(); break;
					}
				}
			}
			catch (Exception ex)
			{
				Logger.Log("ShellCmd " + method + " failed: " + ex.Message);
			}
		});
	}

	internal static void Launch(string exe)
	{
		ShellLaunch.Run(delegate
		{
			try
			{
				Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true });
			}
			catch (Exception ex)
			{
				Logger.Log("Launch " + exe + " failed: " + ex.Message);
			}
		});
	}

	internal static void OpenUri(string uri)
	{
		ShellLaunch.Run(delegate
		{
			try
			{
				Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true });
			}
			catch (Exception ex)
			{
				Logger.Log("OpenUri " + uri + " failed: " + ex.Message);
			}
		});
	}

	internal static void OpenTerminal()
	{
		ShellLaunch.Run(delegate
		{
			try
			{
				Process.Start(new ProcessStartInfo("wt.exe") { UseShellExecute = true });
			}
			catch
			{
				try
				{
					Process.Start(new ProcessStartInfo("powershell.exe") { UseShellExecute = true });
				}
				catch (Exception ex)
				{
					Logger.Log("Terminal launch failed: " + ex.Message);
				}
			}
		});
	}

	internal static void RunDialog()
	{
		ShellLaunch.Run(delegate
		{
			try
			{
				Process.Start(new ProcessStartInfo("rundll32.exe", "shell32.dll,#61") { UseShellExecute = true });
			}
			catch (Exception ex)
			{
				Logger.Log("Run dialog failed: " + ex.Message);
			}
		});
	}

	// Empties the Recycle Bin via the native shell (dwFlags=0 => Windows' own confirm + progress + sound), on all drives.
	// Safe fallback: any failure is logged and swallowed so the shell never breaks.
	internal static void EmptyRecycleBin()
	{
		ShellLaunch.Run(delegate
		{
			try
			{
				SHEmptyRecycleBin(nint.Zero, null, 0u);
			}
			catch (Exception ex)
			{
				Logger.Log("Empty Recycle Bin failed: " + ex.Message);
			}
		});
	}

	internal static void QueueCommand(Action action)
	{
		Dispatcher? dispatcher = Application.Current?.Dispatcher;
		if (dispatcher == null || dispatcher.HasShutdownStarted)
		{
			Safe(action);
			return;
		}
		dispatcher.BeginInvoke((Action)delegate { Safe(action); }, DispatcherPriority.Background);
	}

	internal static void Safe(Action a)
	{
		try
		{
			a();
		}
		catch (Exception ex)
		{
			Logger.Log("Menu command failed: " + ex.Message);
		}
	}

	internal static MenuItem SystemToolsSub()
	{
		return Sub("System tools", 59663, Leaf81("Task Manager", "taskmgr.png", 57832, delegate
		{
			Launch("taskmgr.exe");
		}), Leaf("Settings", 57621, delegate
		{
			OpenUri("ms-settings:");
		}), Leaf81("Control Panel", "control.png", 57587, delegate
		{
			Launch("control.exe");
		}), Leaf81("Device Manager", "mmc.png", 59767, delegate
		{
			Launch("devmgmt.msc");
		}), Leaf81("Terminal", "cmd.png", 59222, OpenTerminal), Leaf("Run", 59222, RunDialog), Sep(), LeafExe("Restart Explorer", "explorer.exe", 57673, ShellRestart.RestartExplorer), Leaf("Empty Recycle Bin", 59213, EmptyRecycleBin));
	}

	internal static MenuItem PerformanceToolsSub()
	{
		return Sub("Performance tools", 57832, Leaf81("Resource Monitor", "resmon.png", 57832, delegate
		{
			Launch("resmon.exe");
		}), Leaf81("Performance Monitor", "perfmon.png", 57832, delegate
		{
			Launch("perfmon.exe");
		}));
	}
}
