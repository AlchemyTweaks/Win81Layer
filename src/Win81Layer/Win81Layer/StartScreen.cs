using System;
using System.CodeDom.Compiler;
using System.Collections;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Forms;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using Microsoft.Win32;

namespace Win81Layer;

public partial class StartScreen : Window, IComponentConnector, IStyleConnector
{
	private enum AppsSort
	{
		Name,
		DateInstalled,
		MostUsed,
		Category
	}

	private sealed class LiveFlipState
	{
		public required FrameworkElement Host;

		public required ScaleTransform Flip;

		public required UIElement ContentFace;

		public required UIElement IconFace;

		public bool ContentUp = true;

		public long NextFlip;

		public int ContentDwellMinMs = 5000;

		public int ContentDwellMaxMs = 9500;

		public int IconDwellMinMs = 5000;

		public int IconDwellMaxMs = 9500;

		public bool RestOnContent;

		public long AnimationVersion;

		public bool IsFlipping;

		public Action? Detach;
	}

	private sealed class AppsComparer(AppsSort mode) : IComparer
	{
		public int Compare(object? x, object? y)
		{
			if (!(x is AppEntry a) || !(y is AppEntry b))
			{
				return 0;
			}
			AppsSort appsSort = mode;
			if (1 == 0)
			{
			}
			int result;
			switch (appsSort)
			{
			case AppsSort.MostUsed:
			{
				int c = UsageStore.Count(b.LaunchPath).CompareTo(UsageStore.Count(a.LaunchPath));
				result = ((c != 0) ? c : ByName.Compare(a, b));
				break;
			}
			case AppsSort.DateInstalled:
			{
				int d = InstalledKey(b).CompareTo(InstalledKey(a));
				result = ((d != 0) ? d : ByName.Compare(a, b));
				break;
			}
			case AppsSort.Category:
			{
				int g = CategoryKey(a).CompareTo(CategoryKey(b));
				result = ((g != 0) ? g : ByName.Compare(a, b));
				break;
			}
			default:
				result = ByName.Compare(a, b);
				break;
			}
			if (1 == 0)
			{
			}
			return result;
		}

		private static long InstalledKey(AppEntry e)
		{
			return e.InstallDate?.Ticks ?? UsageStore.FirstSeen(e.LaunchPath);
		}

		private static string CategoryKey(AppEntry e)
		{
			return string.IsNullOrEmpty(e.Category) ? "\uffff" : e.Category;
		}
	}

	private List<AppEntry> _apps = new List<AppEntry>();

	private ObservableCollection<GroupVm> _groups = new ObservableCollection<GroupVm>();

	private ICollectionView? _appsView;

	private AppsSort _appsSort = AppsSort.Name;

	private readonly object _deferredIconGate = new object();

	private List<AppEntry>? _deferredIconApps;

	private bool _deferredIconLoadRequested;

	private bool _deferredIconLoaderRunning;

	private int _deferredIconPixels = 64;

	private readonly object _priorityIconGate = new object();

	private HashSet<string> _priorityIconPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

	private bool _appsViewBuilt;

	private string _searchQuery = string.Empty;

	private bool _showingAllApps;

	private int _viewTransitionVersion;

	private bool _viewTransitionActive;

	private long _viewTransitionStartTimestamp;

	private DispatcherOperation _appsWarmupOperation;

	private readonly bool _transitionDiagnostics;

	private event Action<int, bool, double> ViewTransitionCompletedForQa;

	internal long QaLastRequestAllocatedBytes { get; private set; }

	internal int QaLastRequestGen0Collections { get; private set; }

	internal int QaLastRequestGen1Collections { get; private set; }

	internal int QaLastRequestGen2Collections { get; private set; }

	private BitmapCache _startViewTransitionCache;

	private BitmapCache _appsViewTransitionCache;

	private DispatcherTimer? _deferredIconDelayTimer;

	private GCLatencyMode? _viewPreviousGcLatencyMode;

	private EventHandler? _viewFrameHandler;

	private bool _viewFrameHooked;

	private int _viewFrameVersion;

	private bool _viewFrameTargetApps;

	private long _viewFrameStartTimestamp;

	private FrameworkElement? _viewFrameIncoming;

	private FrameworkElement? _viewFrameOutgoing;

	private TranslateTransform? _viewFrameIncomingSlide;

	private TranslateTransform? _viewFrameOutgoingSlide;

	private double _viewFrameIncomingOpacityFrom;

	private double _viewFrameOutgoingOpacityFrom;

	private double _viewFrameIncomingYFrom;

	private double _viewFrameOutgoingYFrom;

	private double _viewFrameOutgoingYTo;

	private double _viewFrameEnterMs;

	private double _viewFrameExitMs;

	private IEasingFunction? _viewFrameEnterEase;

	private IEasingFunction? _viewFrameExitEase;

	private bool _contextMenuOpen;

	private System.Windows.Point _dragStart;

	private TileVm? _dragCandidate;

	private FrameworkElement? _dragSourceEl;

	private bool _dragging;

	private TileVm? _dragPrimary;

	private FrameworkElement? _dragSource;

	private System.Windows.Point _grabOffset;

	private TileVm? _hoverOccupant;

	private int _hoverSince;

	private const int FoldDwellMs = 600;

	private Border? _ghost;

	private LiveTileService? _liveTiles;

	private readonly List<LiveFlipState> _liveFlips = new List<LiveFlipState>();

	private DispatcherTimer? _flipTimer;

	private string _weatherCity = "Kalamata";

	private string _weatherUnits = "C";

	private int _showGuardUntil;

	private int _scrollGuardUntil;

	private ScrollViewer? _activeScroller;

	private DispatcherTimer? _scrollHideTimer;

	private static readonly string[] PersBgColors = new string[20]
	{
		"#FF1F1F1F", "#FF2D2D30", "#FF5A3E85", "#FF6A2C91", "#FF8E2DC0", "#FFA61E5A", "#FFC81E5B", "#FFB0006E", "#FF9E3B2E", "#FFC24A1E",
		"#FFB5651D", "#FF7A6A00", "#FF2E7D32", "#FF00695C", "#FF006B8F", "#FF0B5394", "#FF283593", "#FF37474F", "#FF4E342E", "#FF880E4F"
	};

	private static readonly string[] PersAccentColors = new string[20]
	{
		"#FFE81123", "#FFFF4343", "#FFFF8C00", "#FFF7B500", "#FFFFD500", "#FF7CDA00", "#FF10893E", "#FF00B294", "#FF00B7C3", "#FF0091F7",
		"#FF2672EC", "#FF6B69D6", "#FF8764B8", "#FFB146C2", "#FFE3008C", "#FFFF43A0", "#FFEF6950", "#FFC19C00", "#FF767676", "#FFFFFFFF"
	};

	private bool _personalizeStandalone;

	private static readonly Dictionary<string, ImageSource> _wpThumbCache = new Dictionary<string, ImageSource>(StringComparer.OrdinalIgnoreCase);

	private static readonly object _wpThumbGate = new object();

	private bool _personalizeBuilt;

	private string _personalizeWallpaperSignature = "";

	private int _personalizeThumbGeneration;

	private sealed class PersonalizeChoice
	{
		public string Kind { get; }

		public string Value { get; }

		public string Secondary { get; }

		public System.Windows.Media.Color Ink { get; }

		public PersonalizeChoice(string kind, string value, string secondary, System.Windows.Media.Color ink)
		{
			Kind = kind;
			Value = value;
			Secondary = secondary;
			Ink = ink;
		}
	}

	private int _bgToken;

	private static readonly Dictionary<string, ImageBrush> _bgBrushCache = new Dictionary<string, ImageBrush>(StringComparer.OrdinalIgnoreCase);

	private const double GroupDragThreshold = 6.0;

	private bool _closing;

	private bool _peekActive;

	private bool _zoomDragging;

	private System.Windows.Controls.Button? _zoomDragBtn;

	private GroupVm? _zoomDragGroup;

	private System.Windows.Point _zoomDragStart;

	private System.Windows.Point _zoomGrabOffset;

	private Border? _zoomGhost;

	private readonly Random _flipRand = new Random();

	private bool _semanticZoomed;

	private bool _viewToggleShown;

	private DispatcherTimer? _vtRevealTimer;

	private DispatcherTimer? _vtHideTimer;

	private TileVm? _appBarTile;

	private AppEntry? _appBarEntry;

	private bool _customiseActive;

	private TranslateTransform? _pressTiltXf;

	private ScaleTransform? _pressTiltScale;

	private TranslateTransform? _ghostMove;

	// Win8.1-style board pull-back: while a tile is being dragged, the whole tile board zooms out slightly (so the
	// lifted ghost stands out / feels focused). Applied as a scale on StartScroller, composed with the existing view
	// slide so nothing about the view transition changes. See memory start-tile-drag-zoom.
	private ScaleTransform? _boardZoom;

	private const double BoardDragZoom = 0.96;

	private bool _dragDirty;

	private bool _dragFrameHooked;

	private bool _saveQueued;

	private bool _dragLogged;

	private System.Windows.Point _lastDragPoint;

	private bool _packedFoldArmed;

	private System.Windows.Point _packedLastPos;

	private int _packedLastMoveTs;

	private DispatcherTimer? _dragScrollTimer;

	private int _dragScrollDir;

	private string? _renameOriginal;

	// The group-name rename box currently visible/editing. Because the borderless Start overlay owns the Win32
	// foreground, this TextBox holds only LOGICAL focus, so native Backspace/Delete/Space are swallowed Ã¢â‚¬â€ exactly
	// like the Apps SearchBox. We track it so OnPreviewKeyDown can own its editing keys.
	private System.Windows.Controls.TextBox? _activeGroupEdit;

	// The whole rename EDIT PANEL (input + Save/Cancel buttons + helper text) currently showing. The root
	// PreviewMouseLeftButtonDown treats a click anywhere inside this scope as "inside the editor" (so the ✕ Cancel
	// button cancels via its own handler instead of the click-away logic committing the name). Falls back to the
	// TextBox itself if the named panel can't be found.
	private FrameworkElement? _activeGroupEditScope;

	private static readonly Comparer<AppEntry> ByName = Comparer<AppEntry>.Create((AppEntry a, AppEntry b) => string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase));

	private readonly Dictionary<AppEntry, System.Windows.Controls.Button> _appButtons = new Dictionary<AppEntry, System.Windows.Controls.Button>();

	private static readonly string[] Alphabet;

	private System.Windows.Controls.ContextMenu? _sortMenu;

	private readonly Dictionary<AppsSort, System.Windows.Controls.MenuItem> _sortItems = new Dictionary<AppsSort, System.Windows.Controls.MenuItem>();

	private DispatcherTimer? _searchDebounce;

	internal static StartScreen? Current { get; private set; }

	public IReadOnlyList<AppEntry> Apps => _apps;

	public bool QaKeepOpen { get; set; }

	public event Action? SearchRequested;

	public event Action? PcSettingsRequested;

	public StartScreen()
		: this(transitionDiagnostics: false)
	{
	}

	internal StartScreen(bool transitionDiagnostics)
	{
		_transitionDiagnostics = transitionDiagnostics;
		Current = this;
		InitializeComponent();
		InitBottomScroll();
		ViewToggleArrowUp(up: false);
		SetupCornerStartButton();
		TilePanel.BandRowCapacity = StartRowCapacity();
		base.SizeChanged += delegate
		{
			UpdateBandCapacity();
		};
		StartScroller.SizeChanged += delegate
		{
			UpdateBandCapacity();
		};
		base.Loaded += delegate
		{
			((DispatcherObject)this).Dispatcher.BeginInvoke((Delegate)new Action(UpdateBandCapacity), (DispatcherPriority)6, Array.Empty<object>());
		};
		// window-level so a fast flick that leaves the pressed tile's bounds before the 6px threshold still starts the drag
		base.PreviewMouseMove += OnTileMove;
		// window-level release: clears a pending candidate on a release that missed the tile's OnTileUp (gap/header),
		// AND acts as a fallback drag terminator so a drag can't get stuck if CaptureMouse() ever failed
		base.PreviewMouseLeftButtonUp += delegate(object _, MouseButtonEventArgs e)
		{
			if (_dragging)
			{
				System.Windows.Point pos = e.GetPosition(this);
				EndTileDrag();
				CommitDrop(pos);
			}
			else
			{
				_dragCandidate = null;
				_dragSourceEl = null;
				ResetPressTilt();   // release over a gap/header never fires the per-tile OnTileUp Ã¢â‚¬â€ clear the press tilt so it doesn't stick
			}
		};
		base.PreviewMouseLeftButtonDown += delegate(object _, MouseButtonEventArgs e)
		{
			// While renaming a group, a click ANYWHERE except inside the rename box commits the name — no need to aim
			// precisely at a particular spot. CommitActiveGroupEditForSave reads the box's live text, so nothing is lost.
			// The committing click is consumed so clicking a tile to save the name doesn't also launch that app.
			if (_activeGroupEdit != null)
			{
				FrameworkElement scope = _activeGroupEditScope ?? _activeGroupEdit;
				bool insideBox = e.OriginalSource is DependencyObject rd && (rd == scope || (rd is Visual rv && scope.IsAncestorOf(rv)));
				if (!insideBox)
				{
					CommitActiveGroupEditAndFlush();
					e.Handled = true;
					return;
				}
			}
			if (TileAppBar.Visibility == Visibility.Visible)
			{
				object originalSource = e.OriginalSource;
				DependencyObject val = (DependencyObject)((originalSource is DependencyObject) ? originalSource : null);
				// Ctrl+click is the multi-select gesture: leave the bar AND the current selection alone so
				// OnTileClick can toggle this tile in/out of the selection.
				bool ctrlHeld = ((Enum)Keyboard.Modifiers).HasFlag((Enum)(object)(ModifierKeys)2);
				if (val != null && !IsInsideAppBar(val) && !ctrlHeld)
				{
					HideTileAppBar();
					// Consume the press. HideTileAppBar() clears every tile's IsSelected synchronously, so without
					// this the SAME press keeps tunnelling down to the tile Button; OnTileClick then reads
					// AnySelected() == false and LAUNCHES the app. One physical click would then do two things,
					// which is exactly what 'a single click acts like a double click' felt like. Authentic 8.1:
					// the first click only dismisses the app bar. Mirrors the group-rename branch above, which
					// consumes its click for the same reason. Deliberately no 'return' - the _customiseActive
					// block below must still run for this press.
					e.Handled = true;
				}
			}
			if (_customiseActive && (!(e.OriginalSource is FrameworkElement frameworkElement) || !(frameworkElement.DataContext is GroupVm)))
			{
				SetCustomise(on: false);
			}
		};
		base.ContextMenuOpening += delegate
		{
			_contextMenuOpen = true;
		};
		UserNameText.Text = Environment.UserName;
		try
		{
			UserTile.Tag = new BitmapImage(new Uri("pack://application:,,,/Assets/user-icon.png"));
		}
		catch
		{
		}
		if (transitionDiagnostics)
		{
			QaKeepOpen = true;
			InitializeTransitionDiagnosticData();
		}
		else
		{
			LoadUserAccount();
			RefreshBackground();
			SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
			LoadAppsOnStaThread();
		}
	}

	private void InitializeTransitionDiagnosticData()
	{
		SolidColorBrush tileBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(38, 114, 236));
		tileBrush.Freeze();
		_apps = Enumerable.Range(0, 220).Select(delegate(int i)
		{
			char group = (char)('A' + i % 26);
			return new AppEntry
			{
				Name = $"{group} QA Application {i:000}",
				LaunchPath = $"qa:application:{i:000}",
				Category = "QA",
				TileBrush = tileBrush
			};
		}).ToList();
		_appsView = CollectionViewSource.GetDefaultView(_apps);
		ApplyAppsSort(_appsSort, rebuild: false);

		_groups = new ObservableCollection<GroupVm>();
		for (int groupIndex = 0; groupIndex < 6; groupIndex++)
		{
			GroupVm group = new GroupVm { Name = "QA Group " + (groupIndex + 1) };
			for (int tileIndex = 0; tileIndex < 8; tileIndex++)
			{
				group.Tiles.Add(new TileVm
				{
					Entry = _apps[groupIndex * 8 + tileIndex],
					Size = TileSize.Medium,
					Col = (tileIndex % 4) * 2,
					Row = (tileIndex / 4) * 2
				});
			}
			_groups.Add(group);
		}
		GroupsHost.ItemsSource = _groups;
		BuildAppsView();
		AppsScroller.Visibility = Visibility.Hidden;
		PremeasureAppsView();
		StartScroller.Visibility = Visibility.Visible;
	}

	private async void LoadUserAccount()
	{
		try
		{
			var (pic, name) = await UserAccount.LoadAsync();
			if (!string.IsNullOrWhiteSpace(name))
			{
				UserNameText.Text = name;
			}
			if (pic != null)
			{
				UserTile.Tag = pic;
			}
			Logger.Log($"User account: name='{name}', picture={pic != null}");
		}
		catch (Exception ex)
		{
			Exception ex2 = ex;
			Logger.Log("User account load: " + ex2.Message);
		}
	}

	public void RefreshBackground()
	{
		try
		{
			AppSettings s = SettingsStore.Load();
			switch (s.StartBgMode)
			{
			case "custom":
				if (!File.Exists(s.StartBgCustomPath))
				{
					goto default;
				}
				ApplyImageBgAsync(s.StartBgCustomPath);
				break;
			case "desktop":
			{
				string wp = TaskbarTheme.WallpaperPath();
				if (wp == null)
				{
					goto default;
				}
				ApplyImageBgAsync(wp);
				break;
			}
			case "pattern":
				SetStartBg(StartBackgroundFactory.Build(StartBackgroundFactory.Parse(s.StartBgColor, System.Windows.Media.Color.FromRgb(60, 30, 112)), StartAccent.Color(), s.StartPattern));
				break;
			default:
				if (FindResource("StartBackground") is System.Windows.Media.Brush def)
				{
					SetStartBg(def);
				}
				break;
			}
			try
			{
				SolidColorBrush accent = new SolidColorBrush(StartAccent.Color());
				base.Resources["SearchAccent"] = accent;
				System.Windows.Application.Current.Resources["MenuAccent"] = accent;
				System.Windows.Application.Current.Resources["MenuAccentLight"] = new SolidColorBrush(StartAccent.Tint(0.55));
				System.Windows.Media.Color acPress = StartAccent.Color();
				SolidColorBrush pressed = new SolidColorBrush(System.Windows.Media.Color.FromRgb((byte)(acPress.R * 0.78), (byte)(acPress.G * 0.78), (byte)(acPress.B * 0.78)));
				pressed.Freeze();
				System.Windows.Application.Current.Resources["MenuAccentPressed"] = pressed;   // primary-button press/hover (pattern 1) - live with the accent
				SolidColorBrush headerAccent = new SolidColorBrush(StartAccent.Tint(0.6));
				base.Resources["HeaderAccent"] = headerAccent;
				System.Windows.Application.Current.Resources["HeaderAccent"] = headerAccent;
				System.Windows.Media.Color ac = StartAccent.Color();
				SolidColorBrush hover = new SolidColorBrush(System.Windows.Media.Color.FromArgb(64, ac.R, ac.G, ac.B));
				base.Resources["HoverTint"] = hover;
				System.Windows.Application.Current.Resources["HoverTint"] = hover;
				base.Resources["ZoomHover"] = new SolidColorBrush(System.Windows.Media.Color.FromArgb(102, ac.R, ac.G, ac.B));
				Win81Window.RefreshAllAccents();
				// Start changes do not invalidate the desktop-wallpaper average. Repaint shell colours immediately and
				// let DesktopComposition serialize the slower registry/broadcast work off the UI thread.
				TaskbarWindow.RaiseTaskbarColorChanged(invalidateWallpaper: false);
				DesktopComposition.RefreshAccent();
			}
			catch
			{
			}
		}
		catch (Exception ex)
		{
			Logger.Log("Start background apply failed: " + ex.Message);
		}
	}

	private void OnPersonalizeClick(object sender, RoutedEventArgs e)
	{
		OpenPersonalize();
	}

	public void ShowPersonalize()
	{
		_personalizeStandalone = false;
		ShowStartCore();
		((DispatcherObject)this).Dispatcher.BeginInvoke((Delegate)new Action(OpenPersonalize), (DispatcherPriority)6, Array.Empty<object>());
	}

	public void ShowPersonalizeStandalone()
	{
		_personalizeStandalone = true;
		ShowStartCore();
		((DispatcherObject)this).Dispatcher.BeginInvoke((Delegate)new Action(OpenPersonalize), (DispatcherPriority)6, Array.Empty<object>());
	}

	internal static (int Count, long Milliseconds) QaWarmWallpaperThumbnails()
	{
		Stopwatch sw = Stopwatch.StartNew();
		List<string> wallpapers = Wallpapers.Presets();
		foreach (string path in wallpapers)
		{
			WallpaperThumb(path);
		}
		sw.Stop();
		return (wallpapers.Count, sw.ElapsedMilliseconds);
	}

	internal long QaRenderPersonalize(string outPath, double width = 1366.0, double height = 768.0)
	{
		Width = width;
		Height = height;
		Left = -4000.0;
		Top = -4000.0;
		if (!IsVisible)
		{
			Show();
		}
		PersonalizeHost.Visibility = Visibility.Visible;
		PersonalizeSlide.BeginAnimation(TranslateTransform.XProperty, null);
		PersonalizeSlide.X = 0.0;
		Stopwatch build = Stopwatch.StartNew();
		BuildPersonalize();
		build.Stop();
		foreach (UIElement element in PatternGrid.Children)
		{
			if (element is Border border && border.Tag is PersonalizeChoice choice && choice.Kind == "wallpaper")
			{
				border.Background = new ImageBrush(WallpaperThumb(choice.Value)) { Stretch = Stretch.UniformToFill };
			}
		}
		FrameworkElement root = (FrameworkElement)Content;
		root.ApplyTemplate();
		root.Measure(new System.Windows.Size(width, height));
		root.Arrange(new Rect(0.0, 0.0, width, height));
		root.UpdateLayout();
		RenderTargetBitmap bitmap = new RenderTargetBitmap((int)width, (int)height, 96.0, 96.0, PixelFormats.Pbgra32);
		bitmap.Render(root);
		PngBitmapEncoder encoder = new PngBitmapEncoder();
		encoder.Frames.Add(BitmapFrame.Create(bitmap));
		using FileStream stream = File.Create(outPath);
		encoder.Save(stream);
		return build.ElapsedMilliseconds;
	}

	private void SetStartChromeVisible(bool v)
	{
		Visibility vis = ((!v) ? Visibility.Collapsed : Visibility.Visible);
		BgLayer.Visibility = vis;
		HeaderBar.Visibility = vis;
		ViewToggle.Visibility = vis;
		if (!v)
		{
			StartScroller.Visibility = Visibility.Collapsed;
			AppsScroller.Visibility = Visibility.Collapsed;
			BottomScroll.Visibility = Visibility.Collapsed;
			SelectionBar.Visibility = Visibility.Collapsed;
		}
	}

	private void OpenPersonalize()
	{
		PersonalizeBackdrop.Background = new SolidColorBrush(System.Windows.Media.Color.FromArgb(1, 0, 0, 0));
		PersonalizeHost.Visibility = Visibility.Visible;
		PersonalizeSlide.BeginAnimation(TranslateTransform.XProperty, Motion.To(0.0, Motion.Cat.EdgeEnter));
		PersonalizeHost.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0.0, 1.0, Motion.Dur(Motion.Cat.Micro)));
		// Show/animate the pane first. Cold thumbnail decode and control population happen after the input/render pass,
		// so opening Personalize never blocks the Start dispatcher.
		((DispatcherObject)this).Dispatcher.BeginInvoke((Delegate)new Action(BuildPersonalize), DispatcherPriority.Background, Array.Empty<object>());
	}

	private void OnPersonalizeClose(object sender, RoutedEventArgs e)
	{
		ClosePersonalize();
	}

	private void OnPersonalizeBackdropClick(object sender, MouseButtonEventArgs e)
	{
		ClosePersonalize();
	}

	private void ClosePersonalize()
	{
		bool standalone = _personalizeStandalone;
		DoubleAnimation slide = Motion.To(392.0, Motion.Cat.EdgeExit);
		slide.Completed += delegate
		{
			PersonalizeHost.Visibility = Visibility.Collapsed;
			_personalizeStandalone = false;
			if (standalone)
			{
				HideStart();
			}
		};
		PersonalizeSlide.BeginAnimation(TranslateTransform.XProperty, slide);
	}

	private void BuildPersonalize()
	{
		Stopwatch sw = Stopwatch.StartNew();
		AppSettings s = SettingsStore.Load();
		List<string> wallpapers = Wallpapers.Presets();
		string wallpaperSignature = string.Join("|", wallpapers.Select(delegate(string path)
		{
			try
			{
				FileInfo info = new FileInfo(path);
				return path + ":" + info.Length + ":" + info.LastWriteTimeUtc.Ticks;
			}
			catch
			{
				return path;
			}
		}));

		if (!_personalizeBuilt)
		{
			DesignGrid.Children.Clear();
			foreach (StartBackgroundFactory.Preset preset in StartBackgroundFactory.Presets)
			{
				System.Windows.Media.Color pbase = StartBackgroundFactory.Parse(preset.Base, Colors.Gray);
				System.Windows.Media.Color pacc = StartBackgroundFactory.Parse(preset.Accent, pbase);
				Border design = new Border
				{
					Width = 80.0,
					Height = 50.0,
					Margin = new Thickness(0.0, 0.0, 7.0, 7.0),
					Background = StartBackgroundFactory.Build(pbase, pacc, preset.Pattern),
					BorderBrush = new SolidColorBrush(System.Windows.Media.Color.FromArgb(68, byte.MaxValue, byte.MaxValue, byte.MaxValue)),
					BorderThickness = new Thickness(1.0),
					Cursor = System.Windows.Input.Cursors.Hand,
					Tag = new PersonalizeChoice("design", preset.Pattern, preset.Base, Colors.White)
				};
				StartBackgroundFactory.Preset captured = preset;
				design.MouseLeftButtonDown += delegate { ApplyDesign(captured); };
				DesignGrid.Children.Add(design);
			}
			BuildColorBand(BgColorGrid, "background", ApplyBgColor);
			BuildColorBand(AccentColorGrid, "accent", delegate(string hex) { ApplyPersonalize(null, null, hex); });
			_personalizeBuilt = true;
		}

		if (!string.Equals(_personalizeWallpaperSignature, wallpaperSignature, StringComparison.Ordinal))
		{
			_personalizeWallpaperSignature = wallpaperSignature;
			PatternGrid.Children.Clear();
			List<(Border Thumb, string Path)> pending = new List<(Border, string)>();
			foreach (string wp in wallpapers)
			{
				Border thumb = new Border
				{
					Width = 80.0,
					Height = 50.0,
					Margin = new Thickness(0.0, 0.0, 7.0, 7.0),
					BorderBrush = new SolidColorBrush(System.Windows.Media.Color.FromArgb(68, byte.MaxValue, byte.MaxValue, byte.MaxValue)),
					BorderThickness = new Thickness(1.0),
					Cursor = System.Windows.Input.Cursors.Hand,
					ToolTip = System.IO.Path.GetFileNameWithoutExtension(wp),
					Background = System.Windows.Media.Brushes.Gray,
					Tag = new PersonalizeChoice("wallpaper", wp, "", Colors.White)
				};
				string path = wp;
				thumb.MouseLeftButtonDown += delegate { ApplyWallpaper(path); };
				PatternGrid.Children.Add(thumb);
				pending.Add((thumb, wp));
			}
			QueueWallpaperThumbs(pending);
		}

		RefreshPersonalizeSelection(s);
		try
		{
			PersonalizePane.Background = new SolidColorBrush(SettingsPane.AccentToneColor(0.3));
		}
		catch
		{
		}
		sw.Stop();
		int cachedCount;
		lock (_wpThumbGate)
		{
			cachedCount = _wpThumbCache.Count;
		}
		Logger.Log($"Personalize controls ready in {sw.ElapsedMilliseconds} ms (wallpapers={wallpapers.Count}, cached={cachedCount})");
	}

	private static ImageSource WallpaperThumb(string path)
	{
		lock (_wpThumbGate)
		{
			if (_wpThumbCache.TryGetValue(path, out ImageSource cached))
			{
				return cached;
			}
		}
		BitmapImage bmp = new BitmapImage();
		bmp.BeginInit();
		bmp.UriSource = new Uri(path);
		bmp.DecodePixelWidth = 120;
		bmp.CacheOption = BitmapCacheOption.OnLoad;
		bmp.EndInit();
		((Freezable)bmp).Freeze();
		lock (_wpThumbGate)
		{
			_wpThumbCache[path] = bmp;
		}
		return bmp;
	}

	private void QueueWallpaperThumbs(List<(Border Thumb, string Path)> pending)
	{
		int generation = ++_personalizeThumbGeneration;
		Task.Run(delegate
		{
			foreach ((Border thumb, string path) in pending)
			{
				ImageSource image;
				try
				{
					image = WallpaperThumb(path);
				}
				catch (Exception ex)
				{
					Logger.Log("Wallpaper thumbnail failed '" + path + "': " + ex.Message);
					continue;
				}
				((DispatcherObject)this).Dispatcher.BeginInvoke((Delegate)(Action)delegate
				{
					if (generation == _personalizeThumbGeneration && thumb.Tag is PersonalizeChoice choice && string.Equals(choice.Value, path, StringComparison.OrdinalIgnoreCase))
					{
						thumb.Background = new ImageBrush(image) { Stretch = Stretch.UniformToFill };
					}
				}, DispatcherPriority.Background, Array.Empty<object>());
			}
		});
	}

	private void BuildColorBand(System.Windows.Controls.Panel host, string kind, Action<string> onPick)
	{
		host.Children.Clear();
		double[] lights = new double[4] { 0.24, 0.38, 0.52, 0.66 };
		for (int r = 0; r < lights.Length; r++)
		{
			for (int c = 0; c <= 12; c++)
			{
				System.Windows.Media.Color col = ((c == 12) ? FromHsl(0.0, 0.0, 0.16 + (double)r * 0.2) : FromHsl((double)c / 12.0, 0.72, lights[r]));
				string hex = $"#FF{col.R:X2}{col.G:X2}{col.B:X2}";
				Border cell = new Border
				{
					Width = 26.0,
					Height = 26.0,
					Background = new SolidColorBrush(col),
					Cursor = System.Windows.Input.Cursors.Hand,
					Tag = new PersonalizeChoice(kind, hex, "", ((double)(int)col.R * 0.299 + (double)(int)col.G * 0.587 + (double)(int)col.B * 0.114 > 150.0) ? Colors.Black : Colors.White)
				};
				string captured = hex;
				cell.MouseLeftButtonDown += delegate
				{
					onPick(captured);
				};
				host.Children.Add(cell);
			}
		}
	}

	private void RefreshPersonalizeSelection(AppSettings s)
	{
		foreach (UIElement element in DesignGrid.Children)
		{
			if (element is Border border && border.Tag is PersonalizeChoice choice)
			{
				bool selected = s.StartBgMode == "pattern" && s.StartPattern == choice.Value && string.Equals(s.StartBgColor, choice.Secondary, StringComparison.OrdinalIgnoreCase);
				SetPersonalizeSelected(border, selected, choice.Ink, shadow: true, useBorder: true);
			}
		}
		foreach (UIElement element in PatternGrid.Children)
		{
			if (element is Border border && border.Tag is PersonalizeChoice choice)
			{
				bool selected = s.StartBgMode == "custom" && string.Equals(s.StartBgCustomPath, choice.Value, StringComparison.OrdinalIgnoreCase);
				SetPersonalizeSelected(border, selected, choice.Ink, shadow: true, useBorder: true);
			}
		}
		foreach (System.Windows.Controls.Panel panel in new[] { BgColorGrid, AccentColorGrid })
		{
			foreach (UIElement element in panel.Children)
			{
				if (element is Border border && border.Tag is PersonalizeChoice choice)
				{
					bool selected = choice.Kind == "background"
						? s.StartBgMode == "pattern" && s.StartPattern == "none" && string.Equals(s.StartBgColor, choice.Value, StringComparison.OrdinalIgnoreCase)
						: !string.IsNullOrEmpty(s.StartAccentColor) && string.Equals(s.StartAccentColor, choice.Value, StringComparison.OrdinalIgnoreCase);
					SetPersonalizeSelected(border, selected, choice.Ink, shadow: false, useBorder: false);
				}
			}
		}
		AccentAutoBtn.Tag = (string.IsNullOrEmpty(s.StartAccentColor) ? "active" : null);
	}

	private static void SetPersonalizeSelected(Border border, bool selected, System.Windows.Media.Color ink, bool shadow, bool useBorder)
	{
		if (useBorder)
		{
			border.BorderBrush = selected ? System.Windows.Media.Brushes.White : new SolidColorBrush(System.Windows.Media.Color.FromArgb(68, byte.MaxValue, byte.MaxValue, byte.MaxValue));
			border.BorderThickness = new Thickness(selected ? 2.5 : 1.0);
		}
		border.Child = selected ? SelectionGlyph(ink, shadow) : null;
	}

	private static TextBlock SelectionGlyph(System.Windows.Media.Color ink, bool shadow)
	{
		TextBlock glyph = new TextBlock
		{
			Text = "\uE73E",
			FontFamily = new System.Windows.Media.FontFamily("Segoe MDL2 Assets"),
			FontSize = 18.0,
			Foreground = new SolidColorBrush(ink),
			HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
			VerticalAlignment = VerticalAlignment.Center
		};
		if (shadow)
		{
			glyph.Effect = new DropShadowEffect { BlurRadius = 4.0, ShadowDepth = 0.0, Color = Colors.Black, Opacity = 0.8 };
		}
		return glyph;
	}

	private static System.Windows.Media.Color FromHsl(double h, double s, double l)
	{
		double r;
		double g;
		double b;
		double q;
		double p;
		if (s <= 0.0)
		{
			r = (g = (b = l));
		}
		else
		{
			q = ((l < 0.5) ? (l * (1.0 + s)) : (l + s - l * s));
			p = 2.0 * l - q;
			r = Hue(h + 1.0 / 3.0);
			g = Hue(h);
			b = Hue(h - 1.0 / 3.0);
		}
		return System.Windows.Media.Color.FromRgb((byte)(r * 255.0), (byte)(g * 255.0), (byte)(b * 255.0));
		double Hue(double t)
		{
			if (t < 0.0)
			{
				t++;
			}
			if (t > 1.0)
			{
				t--;
			}
			if (t < 1.0 / 6.0)
			{
				return p + (q - p) * 6.0 * t;
			}
			if (t < 0.5)
			{
				return q;
			}
			if (t < 2.0 / 3.0)
			{
				return p + (q - p) * (2.0 / 3.0 - t) * 6.0;
			}
			return p;
		}
	}

	private void OnBgHueClick(object sender, MouseButtonEventArgs e)
	{
		//IL_0008: Unknown result type (might be due to invalid IL or missing references)
		//IL_000d: Unknown result type (might be due to invalid IL or missing references)
		System.Windows.Point position = e.GetPosition(BgHueStrip);
		double x = Math.Clamp(position.X / Math.Max(1.0, BgHueStrip.ActualWidth), 0.0, 1.0);
		System.Windows.Media.Color col = FromHsl(x, 0.55, 0.3);
		ApplyBgColor($"#FF{col.R:X2}{col.G:X2}{col.B:X2}");
	}

	private void OnAccentHueClick(object sender, MouseButtonEventArgs e)
	{
		//IL_0008: Unknown result type (might be due to invalid IL or missing references)
		//IL_000d: Unknown result type (might be due to invalid IL or missing references)
		System.Windows.Point position = e.GetPosition(AccentHueStrip);
		double x = Math.Clamp(position.X / Math.Max(1.0, AccentHueStrip.ActualWidth), 0.0, 1.0);
		System.Windows.Media.Color col = FromHsl(x, 0.75, 0.5);
		ApplyPersonalize(null, null, $"#FF{col.R:X2}{col.G:X2}{col.B:X2}");
	}

	private void ApplyBgColor(string hex)
	{
		AppSettings s = SettingsStore.Load();
		s.StartBgMode = "pattern";
		s.StartPattern = "none";
		s.StartBgColor = hex;
		s.StartAccentColor = "";
		CommitPersonalize(s, "background colour");
	}

	private void ApplyDesign(StartBackgroundFactory.Preset p)
	{
		AppSettings s = SettingsStore.Load();
		s.StartBgMode = "pattern";
		s.StartPattern = p.Pattern;
		s.StartBgColor = p.Base;
		s.StartAccentColor = p.Accent;
		CommitPersonalize(s, "design preset");
	}

	public void ApplyStartWallpaper(string path)
	{
		ApplyWallpaper(path);
	}

	public void ApplyStartColor(string hex)
	{
		ApplyBgColor(hex);
	}

	public void SyncStartAccentToTheme()
	{
		SyncAccentToTheme();
	}

	private void OnAccentAuto(object sender, RoutedEventArgs e)
	{
		SyncAccentToTheme();
	}

	private void SyncAccentToTheme()
	{
		AppSettings s = SettingsStore.Load();
		s.StartAccentColor = "";
		CommitPersonalize(s, "automatic accent");
	}

	private static void BuildSwatches(System.Windows.Controls.Panel host, string[] colors, string? selected, Action<string> onPick)
	{
		host.Children.Clear();
		foreach (string hex in colors)
		{
			bool sel = string.Equals(selected, hex, StringComparison.OrdinalIgnoreCase);
			System.Windows.Media.Color c = StartBackgroundFactory.Parse(hex, Colors.Gray);
			Border sw = new Border
			{
				Width = 34.0,
				Height = 34.0,
				Margin = new Thickness(0.0, 0.0, 4.0, 4.0),
				Background = new SolidColorBrush(c),
				BorderBrush = (sel ? System.Windows.Media.Brushes.White : new SolidColorBrush(System.Windows.Media.Color.FromArgb(34, byte.MaxValue, byte.MaxValue, byte.MaxValue))),
				BorderThickness = new Thickness((!sel) ? 1 : 2),
				Cursor = System.Windows.Input.Cursors.Hand
			};
			if (sel)
			{
				sw.Child = new TextBlock
				{
					Text = "\uE73E",
					FontFamily = new System.Windows.Media.FontFamily("Segoe MDL2 Assets"),
					Foreground = new SolidColorBrush(((double)(int)c.R * 0.299 + (double)(int)c.G * 0.587 + (double)(int)c.B * 0.114 > 150.0) ? Colors.Black : Colors.White),
					FontSize = 17.0,
					FontWeight = FontWeights.Bold,
					HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
					VerticalAlignment = VerticalAlignment.Center
				};
			}
			string captured = hex;
			sw.MouseLeftButtonDown += delegate
			{
				onPick(captured);
			};
			host.Children.Add(sw);
		}
	}

	private void ApplyPersonalize(string? pattern = null, string? bgColor = null, string? accentColor = null)
	{
		AppSettings s = SettingsStore.Load();
		if (pattern != null || bgColor != null)
		{
			s.StartBgMode = "pattern";
		}
		if (pattern != null)
		{
			s.StartPattern = pattern;
		}
		if (bgColor != null)
		{
			s.StartBgColor = bgColor;
		}
		if (accentColor != null)
		{
			s.StartAccentColor = accentColor;
		}
		CommitPersonalize(s, "accent");
	}

	private void ApplyWallpaper(string path)
	{
		AppSettings s = SettingsStore.Load();
		s.StartBgMode = "custom";
		s.StartBgCustomPath = path;
		s.StartAccentColor = "";
		CommitPersonalize(s, "wallpaper");
	}

	private void OnChooseStartPicture(object sender, RoutedEventArgs e)
	{
		Microsoft.Win32.OpenFileDialog dlg = new Microsoft.Win32.OpenFileDialog
		{
			Title = "Choose a Start background picture",
			Filter = "Images|*.jpg;*.jpeg;*.png;*.bmp"
		};
		if (dlg.ShowDialog() == true)
		{
			AppSettings s = SettingsStore.Load();
			s.StartBgMode = "custom";
			s.StartBgCustomPath = dlg.FileName;
			s.StartAccentColor = "";
			CommitPersonalize(s, "chosen picture");
		}
	}

	private void OnUseDesktopBg(object sender, RoutedEventArgs e)
	{
		AppSettings s = SettingsStore.Load();
		s.StartBgMode = "desktop";
		s.StartAccentColor = "";
		CommitPersonalize(s, "desktop wallpaper");
	}

	private void CommitPersonalize(AppSettings settings, string source)
	{
		Stopwatch sw = Stopwatch.StartNew();
		SettingsStore.Save(settings);
		RefreshBackground();
		RefreshPersonalizeSelection(settings);
		sw.Stop();
		Logger.Log($"Personalize {source} applied on UI in {sw.ElapsedMilliseconds} ms; full image decode and DWM accent refresh continue asynchronously");
	}

	private void SetStartBg(System.Windows.Media.Brush brush)
	{
		BgLayer.Background = brush;
		if (AppsZoomOverlay != null)
		{
			AppsZoomOverlay.Background = brush;
		}
		if (ZoomOverlay != null)
		{
			ZoomOverlay.Background = brush;
		}
	}

	private void ApplyImageBgAsync(string path)
	{
		int token = ++_bgToken;
		int decodeW = MaxScreenWidth();
		string key;
		try
		{
			key = $"{path}|{File.GetLastWriteTimeUtc(path).Ticks}|{decodeW}";
		}
		catch
		{
			key = $"{path}|{decodeW}";
		}
		if (_bgBrushCache.TryGetValue(key, out ImageBrush cached))
		{
			SetStartBg(cached);
			return;
		}
		Task.Run(delegate
		{
			try
			{
				BitmapImage bitmapImage = new BitmapImage();
				bitmapImage.BeginInit();
				bitmapImage.UriSource = new Uri(path);
				bitmapImage.DecodePixelWidth = decodeW;
				bitmapImage.CacheOption = BitmapCacheOption.OnLoad;
				bitmapImage.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
				bitmapImage.EndInit();
				((Freezable)bitmapImage).Freeze();
				ImageBrush ib = new ImageBrush(bitmapImage)
				{
					Stretch = Stretch.UniformToFill
				};
				((Freezable)ib).Freeze();
				((DispatcherObject)this).Dispatcher.BeginInvoke((Delegate)(Action)delegate
				{
					// Cap the full-res wallpaper brush cache; evict an older entry, never the active key. Each entry is a
					// full-screen decoded bitmap (~8-33MB), so keep the cap tight (2) — the user rarely cycles wallpaper.
					if (_bgBrushCache.Count >= 2 && !_bgBrushCache.ContainsKey(key))
					{
						foreach (string oldKey in new System.Collections.Generic.List<string>(_bgBrushCache.Keys))
						{
							if (oldKey != key) { _bgBrushCache.Remove(oldKey); break; }
						}
					}
					_bgBrushCache[key] = ib;
					if (token == _bgToken)
					{
						SetStartBg(ib);
					}
				}, Array.Empty<object>());
			}
			catch (Exception ex)
			{
				Logger.Log("Start bg async decode failed: " + ex.Message);
			}
		});
	}

	private static int MaxScreenWidth()
	{
		try
		{
			// Decode the Start wallpaper at the PRIMARY monitor's physical width (where Start actually shows), not the
			// widest of all monitors — on a laptop docked to a big external panel that avoided decoding a needlessly
			// large bitmap. Keep Screen.Bounds (physical px); SystemParameters.*Width is DIU and would under-decode/blur on HiDPI.
			return Math.Max(1280, Screen.PrimaryScreen?.Bounds.Width ?? 1920);
		}
		catch
		{
			return 1920;
		}
	}

	public void OnAnyContextMenuClosed(object sender, RoutedEventArgs e)
	{
		_contextMenuOpen = false;
		if (base.IsVisible)
		{
			Activate();
		}
	}

	private void LoadAppsOnStaThread()
	{
		Thread thread = new Thread((ThreadStart)delegate
		{
			try
			{
				UsageStore.Load();   // moved off the boot UI thread; runs before its first consumer RegisterInventory below
				List<(AppEntry, AppInventory.IShellItem)> list = null;
				List<AppEntry> apps;
				try
				{
					list = AppInventory.EnumerateAppsFolderStable();
					apps = list.Select<(AppEntry, AppInventory.IShellItem), AppEntry>(((AppEntry Entry, AppInventory.IShellItem Item) t) => t.Entry).ToList();
					Logger.Log($"AppsFolder inventory loaded: {apps.Count} entries");
				}
				catch (Exception ex)
				{
					Logger.Log("AppsFolder enumeration failed, falling back to Start Menu links: " + ex.Message);
					apps = AppInventory.LoadFromStartMenuLinks();
					Logger.Log($"Start Menu inventory loaded: {apps.Count} entries");
				}
				AppInventory.NoteInventoryCount(apps.Count);
				UsageStore.RegisterInventory(apps, DateTime.UtcNow.Ticks);
				try
				{
					IReadOnlyDictionary<string, StartMenuIndex.Info> readOnlyDictionary = StartMenuIndex.Get();
					foreach (AppEntry current in apps)
					{
						if (readOnlyDictionary.TryGetValue(current.Name, out var value))
						{
							current.Category = value.Category;
							if (value.Installed != DateTime.MinValue)
							{
								current.InstallDate = value.Installed;
							}
						}
					}
				}
				catch (Exception ex2)
				{
					Logger.Log("Start Menu enrich failed: " + ex2.Message);
				}
				// COLD-BOOT FIX: prime settings.json off-thread first (Start background/accent + taskbar align come from it),
				// so the UI below builds with the REAL saved settings instead of degraded defaults during the boot storm.
				SettingsStore.PrimeCacheOffThread();
				// COLD-BOOT FIX: read profile.json HERE, on the background STA thread, with a long retry window.
				// The boot storm (AV/indexer) can hold the file locked for many seconds — far past the UI thread's
				// ~4s budget. Reading it off-thread means the on-UI Profile.Load never has to fall back to the DEFAULT
				// layout just because the read timed out (the root cause of "restart shows the wrong/default Start").
				string preProfileJson = Profile.ReadJsonWithRetry();
				Logger.Log(preProfileJson != null
					? $"Profile pre-read OK off-UI ({preProfileJson.Length} chars)"
					: "Profile pre-read returned null (file missing or unreadable for full window)");
				HashSet<string> priorityIconPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
				((DispatcherObject)this).Dispatcher.Invoke((Action)delegate
				{
					if (!apps.Any((AppEntry a) => a.LaunchPath == "win81:desktop"))
					{
						apps.Add(MakeDesktopAppEntry());
					}
					if (!apps.Any((AppEntry a) => a.LaunchPath == "win81:pcsettings"))
					{
						apps.Add(MakePcSettingsAppEntry());
					}
					_apps = apps;
					_appsView = CollectionViewSource.GetDefaultView(_apps);
					ApplyAppsSort(_appsSort, rebuild: false);
					_groups = new ObservableCollection<GroupVm>(Profile.Load(_apps, preProfileJson));
					EnsureDesktopTile();
					if (!SettingsStore.LoadWasDegraded)
					{
						MigratePackedGridOnce();
						ReAlignPackedOrderV2();
						FillGridToHeightOnce();
					}
					else
					{
						Logger.Log("Migrations SKIPPED: settings load was degraded (cold-boot lock) - layout left exactly as saved");
					}
					AssignInitialLayout();
					foreach (TileVm tile in _groups.SelectMany((GroupVm group) => group.Tiles))
					{
						if (!string.IsNullOrWhiteSpace(tile.Entry.LaunchPath))
						{
							priorityIconPaths.Add(tile.Entry.LaunchPath);
						}
					}
					TilePanel.BandRowCapacity = StartRowCapacity();
					Logger.Log($"Start band capacity = {TilePanel.BandRowCapacity} (layout needs {RequiredBandRows()} rows)");
					GroupsHost.ItemsSource = _groups;
					if (_showingAllApps && !_appsViewBuilt)
					{
						BuildAppsView();
					}
					else
					{
						ScheduleAppsViewWarmup();
					}
					_liveTiles = new LiveTileService(
						() => _groups.SelectMany((GroupVm g) => g.Tiles),
						_weatherCity,
						_weatherUnits);
					// Safe mode: keep tiles static (skip flip timers + Weather/Mail/Agenda network fetches).
					if (base.IsVisible && !App.SafeMode)
					{
						_liveTiles.Start();
					}
					Logger.Log($"Profile loaded: {_groups.Count} groups, {_groups.Sum((GroupVm g) => g.Tiles.Count)} tiles");
					PersistenceDiagnostics.TryWritePostBootVerification();
					// Profile.Load has now run — re-check for a degraded/partial cold-boot layout (dropped tiles, which
					// the early OnStartup self-heal check could not see) and arm the self-heal restart if needed.
					try { (System.Windows.Application.Current as App)?.MaybeSelfHealAfterLayout(); } catch { }
				});
				foreach (PinnedApp pin in TaskbarPins.Load())
				{
					if (!string.IsNullOrWhiteSpace(pin.LaunchPath))
					{
						priorityIconPaths.Add(pin.LaunchPath);
					}
				}
				bool replace81AppIcons = SettingsStore.Current.Replace81AppIcons;
				List<(AppEntry Entry, ImageSource Icon, bool Over)> batch = new List<(AppEntry, ImageSource, bool)>(16);
				List<AppEntry> deferred = new List<AppEntry>();
				int priorityLoaded = 0;
				if (list != null)
				{
					try
					{
						foreach (var item5 in list)
						{
							AppEntry item = item5.Item1;
							AppInventory.IShellItem item2 = item5.Item2;
							if (!priorityIconPaths.Contains(item.LaunchPath))
							{
								deferred.Add(item);
								continue;
							}
							ImageSource imageSource = (replace81AppIcons ? AppIconOverrides.TryGet(item) : null);
							// Prefer the parsing path: Win32 AppsFolder entries can be resolved to their real EXE and expose
							// larger native frames than the AppsFolder thumbnail returned by the live shell item.
							ImageSource imageSource2 = imageSource ?? AppInventory.LoadIcon(item.LaunchPath) ?? AppInventory.IconFromItem(item2);
							if (imageSource2 != null)
							{
								batch.Add((item, imageSource2, imageSource != null));
							}
							else
							{
								Logger.Log($"[icon] no icon for {item.Name} ({item.LaunchPath})");
							}
							if (batch.Count >= 16)
							{
								QueueIconBatch(batch);
							}
							priorityLoaded++;
						}
					}
					finally
					{
						AppInventory.ReleaseAppsFolderItems(list);
						list = null;
					}
				}
				else
				{
					foreach (AppEntry current3 in apps)
					{
						if (!priorityIconPaths.Contains(current3.LaunchPath))
						{
							deferred.Add(current3);
							continue;
						}
						ImageSource imageSource3 = (replace81AppIcons ? AppIconOverrides.TryGet(current3) : null);
						ImageSource imageSource4 = imageSource3 ?? AppInventory.LoadIcon(current3.LaunchPath);
						if (imageSource4 != null)
						{
							ImageSource item3 = imageSource4;
							if (true)
							{
								batch.Add((current3, item3, imageSource3 != null));
							}
						}
						if (batch.Count >= 16)
						{
							QueueIconBatch(batch);
						}
						priorityLoaded++;
					}
				}
				QueueIconBatch(batch);
				lock (_priorityIconGate)
				{
					_priorityIconPaths = new HashSet<string>(priorityIconPaths, StringComparer.OrdinalIgnoreCase);
				}
				SetDeferredIconApps(deferred);
				Logger.Log($"Priority icon loading finished: {priorityLoaded} Start/taskbar apps; deferred={deferred.Count}");
			}
			catch (Exception value2)
			{
				Logger.Log($"App loading failed: {value2}");
			}
		})
		{
			IsBackground = true,
			Name = "AppLoader"
		};
		thread.SetApartmentState(ApartmentState.STA);
		thread.Start();
	}

	private void QueueIconBatch(List<(AppEntry Entry, ImageSource Icon, bool Over)> batch)
	{
		if (batch.Count == 0)
		{
			return;
		}
		List<(AppEntry Entry, ImageSource Icon, bool Over, System.Windows.Media.Color? Dominant)> ready =
			new List<(AppEntry, ImageSource, bool, System.Windows.Media.Color?)>(batch.Count);
		foreach ((AppEntry Entry, ImageSource Icon, bool Over) item in batch)
		{
			try
			{
				if (item.Icon.CanFreeze && !item.Icon.IsFrozen)
				{
					item.Icon.Freeze();
				}
				System.Windows.Media.Color? dominant = (!item.Over && item.Icon is BitmapSource bitmap)
					? AppInventory.DominantColor(bitmap)
					: null;
				ready.Add((item.Entry, item.Icon, item.Over, dominant));
			}
			catch (Exception ex)
			{
				Logger.Log($"[icon] prepare failed for {item.Entry.Name}: {ex.Message}");
			}
		}
		batch.Clear();
		if (ready.Count == 0)
		{
			return;
		}
		try
		{
			((DispatcherObject)this).Dispatcher.BeginInvoke((Delegate)(Action)delegate
			{
				foreach ((AppEntry Entry, ImageSource Icon, bool Over, System.Windows.Media.Color? Dominant) item in ready)
				{
					item.Entry.IsOverrideIcon = item.Over;
					item.Entry.OverrideBrush = item.Over ? AppIconOverrides.BrandBrush(item.Entry) : null;
					// Never store a blank/white icon on an AppEntry (feeds Start tiles, all-apps and search) — a
					// decoded-but-blank shell icon becomes the app's letter tile instead.
					item.Entry.Icon = IconResolver.EnsureNonBlank(item.Icon, item.Entry.Name);
					if (!item.Over && item.Dominant.HasValue)
					{
						SolidColorBrush brush = new SolidColorBrush(item.Dominant.Value);
						brush.Freeze();
						item.Entry.TileBrush = brush;
					}
				}
			}, DispatcherPriority.Background, Array.Empty<object>());
		}
		catch (TaskCanceledException)
		{
			// The dispatcher is shutting down; no UI remains to update.
		}
	}

	private void SetDeferredIconApps(List<AppEntry> apps)
	{
		List<AppEntry>? start = null;
		int preferredPixels = 64;
		lock (_deferredIconGate)
		{
			_deferredIconApps = apps;
			if (_deferredIconLoadRequested && !_deferredIconLoaderRunning && apps.Count > 0)
			{
				_deferredIconLoaderRunning = true;
				start = _deferredIconApps;
				_deferredIconApps = null;
				preferredPixels = _deferredIconPixels;
			}
		}
		if (start != null)
		{
			StartDeferredIconLoader(start, preferredPixels);
		}
	}

	private void EnsureDeferredAppIconsLoaded()
	{
		List<AppEntry>? start = null;
		int preferredPixels = PreferredAppsIconPixels();
		lock (_deferredIconGate)
		{
			_deferredIconLoadRequested = true;
			_deferredIconPixels = preferredPixels;
			if (!_deferredIconLoaderRunning && _deferredIconApps is { Count: > 0 })
			{
				_deferredIconLoaderRunning = true;
				start = _deferredIconApps;
				_deferredIconApps = null;
			}
		}
		if (start != null)
		{
			StartDeferredIconLoader(start, preferredPixels);
		}
	}

	private int PreferredAppsIconPixels()
	{
		double scale = 1.0;
		try
		{
			DpiScale dpi = VisualTreeHelper.GetDpi(this);
			scale = Math.Max(dpi.DpiScaleX, dpi.DpiScaleY);
		}
		catch
		{
		}
		int physicalPixels = (int)Math.Ceiling(32.0 * Math.Max(1.0, scale));
		return Math.Clamp(((physicalPixels + 15) / 16) * 16, 32, 128);
	}

	private void StartDeferredIconLoader(List<AppEntry> apps, int preferredPixels)
	{
		Thread thread = new Thread((ThreadStart)delegate
		{
			Stopwatch sw = Stopwatch.StartNew();
			int loaded = 0;
			List<(AppEntry Entry, ImageSource Icon, bool Over)> batch = new List<(AppEntry, ImageSource, bool)>(16);
			try
			{
				bool replace81AppIcons = SettingsStore.Current.Replace81AppIcons;
				foreach (AppEntry entry in apps)
				{
					if (entry.Icon != null)
					{
						continue;
					}
					ImageSource over = replace81AppIcons ? AppIconOverrides.TryGet(entry) : null;
					ImageSource icon = over ?? AppInventory.LoadIcon(entry.LaunchPath, preferredPixels);
					if (icon != null)
					{
						batch.Add((entry, icon, over != null));
						loaded++;
						if (batch.Count >= 16)
						{
							QueueIconBatch(batch);
						}
					}
					else
					{
						Logger.Log($"[icon] no deferred icon for {entry.Name} ({entry.LaunchPath})");
					}
				}
				QueueIconBatch(batch);
				Logger.Log($"Deferred icon loading finished: {loaded}/{apps.Count} apps at {preferredPixels}px in {sw.ElapsedMilliseconds}ms");
			}
			catch (Exception ex)
			{
				Logger.Log("Deferred icon loading failed: " + ex);
			}
			finally
			{
				lock (_deferredIconGate)
				{
					_deferredIconLoaderRunning = false;
				}
			}
		})
		{
			IsBackground = true,
			Name = "DeferredIconLoader",
			Priority = ThreadPriority.BelowNormal
		};
		thread.SetApartmentState(ApartmentState.STA);
		thread.Start();
	}

	public void ShowSearch()
	{
		ShowStart();
		SwitchView(showAllApps: true, animate: false);
		SearchBox.Focus();
	}

	public static void RefreshAppIcons()
	{
		StartScreen s = Current;
		if (s != null)
		{
			((DispatcherObject)s).Dispatcher.Invoke((Action)delegate
			{
				s.RefreshAppIconsInstance();
			});
		}
	}

	// Re-applies the live tiles' brand colour + custom icon when the "Win8.1 app icons" toggle flips, so the built-in
	// live tiles (Weather/Agenda/Mail/Clock) revert together with the app-icon overrides.
	public static void RefreshLiveTiles()
	{
		StartScreen s = Current;
		if (s != null)
		{
			((DispatcherObject)s).Dispatcher.Invoke((Action)delegate
			{
				foreach (TileVm t in s._groups.SelectMany((GroupVm g) => g.Tiles))
				{
					t.RefreshLiveAppearance();
				}
			});
		}
	}

	private void RefreshAppIconsInstance()
	{
		bool useOverrides = SettingsStore.Current.Replace81AppIcons;
		foreach (AppEntry e in _apps)
		{
			bool hasOver = AppIconOverrides.Has(e);
			if (!hasOver && !e.IsOverrideIcon)
			{
				continue;
			}
			if (useOverrides & hasOver)
			{
				ImageSource over = AppIconOverrides.TryGet(e);
				if (over != null)
				{
					e.IsOverrideIcon = true;
					e.OverrideBrush = AppIconOverrides.BrandBrush(e);
					e.Icon = over;
				}
			}
			else if (e.IsOverrideIcon)
			{
				e.IsOverrideIcon = false;
				e.OverrideBrush = null;
				ImageSource native = AppInventory.LoadIcon(e.LaunchPath);
				if (native != null)
				{
					e.Icon = native;
				}
			}
		}
	}

	public void LaunchApp(AppEntry entry)
	{
		Launch(entry);
	}

	// The loaded app inventory (used by the Windows 7 Start menu to populate its program list).
	public System.Collections.Generic.IReadOnlyList<AppEntry> AllApps => _apps;

	internal bool ReleaseIdleResources()
	{
		if (base.IsVisible)
		{
			return false;
		}
		_deferredIconDelayTimer?.Stop();
		// Release rarely-used, on-demand-rebuildable surfaces whenever Start is hidden (not only under memory pressure),
		// since holding them while Start is closed buys nothing: (a) semantic-zoom snapshot RenderTargetBitmaps left
		// resident if Start was closed while zoomed-out (rebuilt by BuildZoomOverview on next zoom), and (b) the
		// Personalize wallpaper-thumbnail set (rebuilt from the signature reset on next Personalize open). A few MB each,
		// only after the user has actually used those surfaces. Guarded — a failure here must never block idle trim.
		try
		{
			if (ZoomOverlay != null && ZoomOverlay.Visibility == Visibility.Visible)
			{
				ZoomOverlay.Visibility = Visibility.Collapsed;
				ZoomHost.Children.Clear();
				_semanticZoomed = false;
				RefreshZoomButton();
			}
		}
		catch
		{
		}
		try
		{
			if (PatternGrid != null && PatternGrid.Children.Count > 0)
			{
				PatternGrid.Children.Clear();
				_personalizeWallpaperSignature = "";
				lock (_wpThumbGate)
				{
					_wpThumbCache.Clear();
				}
			}
		}
		catch
		{
		}
		if (!SystemMemoryPressure.ShouldReleaseVisualCaches(out uint memoryLoad, out ulong availableBytes))
		{
			Logger.Log($"Apps idle cache retained for instant reopen: memoryLoad={memoryLoad}% available={availableBytes / (1024 * 1024)}MB");
			return false;
		}
		// Under real memory pressure, also drop the full-res wallpaper brush cache (the currently-shown brush stays
		// referenced by BgLayer, so only non-active entries are freed; re-decoded on next wallpaper change).
		_bgBrushCache.Clear();
		int releasedIcons = 0;
		lock (_deferredIconGate)
		{
			if (!_deferredIconLoaderRunning)
			{
				HashSet<string> priority;
				lock (_priorityIconGate)
				{
					priority = new HashSet<string>(_priorityIconPaths, StringComparer.OrdinalIgnoreCase);
				}
				List<AppEntry> deferred = new List<AppEntry>();
				foreach (AppEntry entry in _apps)
				{
					if (priority.Contains(entry.LaunchPath))
					{
						continue;
					}
					deferred.Add(entry);
					if (entry.Icon != null)
					{
						entry.Icon = null;
						entry.IsOverrideIcon = false;
						entry.OverrideBrush = null;
						releasedIcons++;
					}
				}
				_deferredIconApps = deferred;
				_deferredIconLoadRequested = false;
			}
		}
		if (releasedIcons > 0)
		{
			Logger.Log($"Apps idle hibernation: retained {_appButtons.Count} premeasured rows; released {releasedIcons} non-priority icons");
			return true;
		}
		return false;
	}

	public void ShowAppsList()
	{
		ShowStart();
		SwitchView(showAllApps: true, animate: false);
		FocusFirstApp();
	}

	internal async Task<(double RequestReturnMs, double CompletionMs, int Version)> QaSwitchViewAsync(bool showAllApps, bool animate = true)
	{
		int expectedVersion = _viewTransitionVersion + 1;
		TaskCompletionSource<double> completed = new TaskCompletionSource<double>(TaskCreationOptions.RunContinuationsAsynchronously);
		void OnCompleted(int version, bool target, double elapsedMs)
		{
			if (version == expectedVersion && target == showAllApps)
			{
				completed.TrySetResult(elapsedMs);
			}
		}
		ViewTransitionCompletedForQa += OnCompleted;
		try
		{
			long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
			int gen0Before = GC.CollectionCount(0);
			int gen1Before = GC.CollectionCount(1);
			int gen2Before = GC.CollectionCount(2);
			Stopwatch request = Stopwatch.StartNew();
			SwitchView(showAllApps, animate);
			request.Stop();
			QaLastRequestAllocatedBytes = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
			QaLastRequestGen0Collections = GC.CollectionCount(0) - gen0Before;
			QaLastRequestGen1Collections = GC.CollectionCount(1) - gen1Before;
			QaLastRequestGen2Collections = GC.CollectionCount(2) - gen2Before;
			double completionMs = await completed.Task.WaitAsync(TimeSpan.FromSeconds(3.0));
			return (request.Elapsed.TotalMilliseconds, completionMs, expectedVersion);
		}
		finally
		{
			ViewTransitionCompletedForQa -= OnCompleted;
		}
	}

	internal async Task<(double MaxRequestReturnMs, double CompletionMs, int Version)> QaRapidViewToggleAsync()
	{
		SwitchView(showAllApps: false, animate: false);
		double maxRequestMs = 0.0;
		MeasureRequest(showAllApps: true);
		await Task.Delay(24);
		MeasureRequest(showAllApps: false);
		await Task.Delay(24);

		int expectedVersion = _viewTransitionVersion + 1;
		TaskCompletionSource<double> completed = new TaskCompletionSource<double>(TaskCreationOptions.RunContinuationsAsynchronously);
		void OnCompleted(int version, bool target, double elapsedMs)
		{
			if (version == expectedVersion && target)
			{
				completed.TrySetResult(elapsedMs);
			}
		}
		ViewTransitionCompletedForQa += OnCompleted;
		try
		{
			MeasureRequest(showAllApps: true);
			double completionMs = await completed.Task.WaitAsync(TimeSpan.FromSeconds(3.0));
			return (maxRequestMs, completionMs, expectedVersion);
		}
		finally
		{
			ViewTransitionCompletedForQa -= OnCompleted;
		}

		void MeasureRequest(bool showAllApps)
		{
			Stopwatch request = Stopwatch.StartNew();
			SwitchView(showAllApps, animate: true);
			request.Stop();
			maxRequestMs = Math.Max(maxRequestMs, request.Elapsed.TotalMilliseconds);
		}
	}

	internal int QaAppVisualCount => _appButtons.Count;

	internal bool QaAppsViewBuilt => _appsViewBuilt;

	internal bool QaRetainAppsAcrossIdle()
	{
		int before = _appButtons.Count;
		Hide();
		ReleaseIdleResources();
		Show();
		return before > 0 && _appsViewBuilt && _appButtons.Count == before && AppsHost.Children.Count > 0;
	}

	internal bool QaViewStateIsConsistent(bool showAllApps)
	{
		FrameworkElement target = showAllApps ? AppsScroller : StartScroller;
		FrameworkElement other = showAllApps ? StartScroller : AppsScroller;
		return _showingAllApps == showAllApps
			&& !_viewTransitionActive
			&& target.Visibility == Visibility.Visible
			&& target.IsHitTestVisible
			&& Math.Abs(target.Opacity - 1.0) < 0.001
			&& Math.Abs(ViewSlide(target).Y) < 0.001
			&& target.CacheMode == null
			&& other.Visibility == Visibility.Hidden
			&& !other.IsHitTestVisible
			&& Math.Abs(other.Opacity - 1.0) < 0.001
			&& Math.Abs(ViewSlide(other).Y) < 0.001
			&& other.CacheMode == null;
	}

	public void ShowStart()
	{
		_personalizeStandalone = false;
		ShowStartCore();
	}

	// Warm the first-open cost off the critical path. The Start window is constructed at boot and its app list
	// loads async (LoadAppsOnStaThread in the ctor), but the FIRST real open still pays for WPF's first
	// measure/arrange of the whole tile board + the initial tile-icon decode + texture realization — which is
	// why the first Start open lags the (already-warm) taskbar. Forcing one off-screen layout pass at idle after
	// boot moves that cost off the user's first click. Pure layout: never Show()/Activate()/foreground — it
	// cannot flash or steal focus, and Measure/Arrange are idempotent so the real open re-lays out harmlessly.
	private bool _prewarmed;
	public void Prewarm()
	{
		if (_prewarmed)
		{
			return;
		}
		_prewarmed = true;
		try
		{
			double w = SystemParameters.PrimaryScreenWidth;
			double h = SystemParameters.PrimaryScreenHeight;
			if (!(w >= 1.0)) w = 1920.0;
			if (!(h >= 1.0)) h = 1080.0;
			System.Windows.Size s = new System.Windows.Size(w, h);
			RootGrid.Measure(s);
			RootGrid.Arrange(new System.Windows.Rect(new System.Windows.Point(0.0, 0.0), s));
			RootGrid.UpdateLayout();
		}
		catch
		{
		}
	}

	private void ShowStartCore()
	{
		if (_closing)
		{
			_closing = false;
			RootGrid.BeginAnimation(UIElement.OpacityProperty, null);
			RootGrid.Opacity = 1.0;
			RootGrid.RenderTransform = null;
			SetTransientCache(on: false);
		}
		_peekActive = false;
		NewGroupVisual.Opacity = 0.0;   // clear any orphaned "new group" drop indicator from a prior interrupted drag
		ApplyStartSkin();               // flatÃ¢â€ â€glass skin for SelectionBar / PersonalizePane / semantic-zoom scrims
		BeginAnimation(UIElement.OpacityProperty, null);
		base.Opacity = 1.0;
		RootGrid.IsHitTestVisible = true;
		base.ShowActivated = true;
		Stopwatch sw = Stopwatch.StartNew();
		base.WindowState = WindowState.Normal;
		base.Left = 0.0;
		base.Top = 0.0;
		base.Width = SystemParameters.PrimaryScreenWidth;
		base.Height = SystemParameters.PrimaryScreenHeight;
		SearchBox.Text = string.Empty;
		// Start each open with the corner Windows button hidden, so it only reappears when the cursor nears the corner.
		if (_cornerStartBtn != null)
		{
			_cornerBtnShown = false;
			_cornerStartBtn.BeginAnimation(UIElement.OpacityProperty, null);
			_cornerStartBtn.Opacity = 0.0;
			_cornerStartBtn.IsHitTestVisible = false;
			if (_cornerGlyphBrush != null)
			{
				_cornerGlyphBrush.Color = Colors.White;
			}
		}
		HideZoom();
		HideAppsZoom();
		ResetGroupRenames();
		if (AnySelected())
		{
			ClearSelection();
		}
		SwitchView(showAllApps: false, animate: false);
		HideViewToggle(instant: true);
		SetStartChromeVisible(!_personalizeStandalone);
		_showGuardUntil = Environment.TickCount + 500;
		Show();
		PositionOnActiveScreen();
		WindowUtil.ForceForeground(this);
		base.Topmost = true;
		// Live-tile data fetch + the flip-animation loop are non-critical decoration — defer them off the show/render
		// critical path (Background) so Start paints and becomes interactive sooner, especially the first boot open.
		((DispatcherObject)this).Dispatcher.BeginInvoke((Delegate)(Action)delegate
		{
			_liveTiles?.Start();
			StartLiveFlips();
		}, (DispatcherPriority)4, Array.Empty<object>());
		((DispatcherObject)this).Dispatcher.BeginInvoke((Delegate)new Action(RefreshDesktopTile), (DispatcherPriority)4, Array.Empty<object>());
		long tShow = sw.ElapsedMilliseconds;
		((DispatcherObject)this).Dispatcher.BeginInvoke((Delegate)(Action)delegate
		{
			double value = ((WinKeyHook.LastTriggerTs != 0L) ? ((double)(Stopwatch.GetTimestamp() - WinKeyHook.LastTriggerTs) * 1000.0 / (double)Stopwatch.Frequency) : (-1.0));
			Logger.Log($"Start latency: show={tShow}ms rendered={sw.ElapsedMilliseconds}ms fromTrigger={value:F0}ms");
		}, (DispatcherPriority)6, Array.Empty<object>());
		AnimateEntrance();
		((DispatcherObject)this).Dispatcher.BeginInvoke((Delegate)(Action)delegate
		{
			if (!_showingAllApps)
			{
				DependencyObject val = GroupsHost.ItemContainerGenerator.ContainerFromIndex(0);
				if (val != null)
				{
					FindFirstButton(val)?.Focus();
				}
			}
		}, (DispatcherPriority)6, Array.Empty<object>());
	}

	private static System.Windows.Controls.Button? FindFirstButton(DependencyObject root)
	{
		for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
		{
			DependencyObject child = VisualTreeHelper.GetChild(root, i);
			if (child is System.Windows.Controls.Button b)
			{
				return b;
			}
			System.Windows.Controls.Button nested = FindFirstButton(child);
			if (nested != null)
			{
				return nested;
			}
		}
		return null;
	}

	private void ResetGroupRenames()
	{
		foreach (GroupVm g in _groups)
		{
			g.IsEditing = false;
		}
	}

	private void ShowZoom()
	{
		//IL_0076: Unknown result type (might be due to invalid IL or missing references)
		if (ZoomOverlay.Visibility != Visibility.Visible && BuildZoomOverview())
		{
			ZoomOverlay.Visibility = Visibility.Visible;
			ScaleTransform scale = new ScaleTransform(1.22, 1.22);
			ZoomOverlay.RenderTransform = scale;
			ZoomOverlay.RenderTransformOrigin = new System.Windows.Point(0.5, 0.5);
			Duration dur = Motion.Dur(Motion.Cat.Semantic);
			IEasingFunction ease = Motion.Ease(Motion.Cat.Semantic);
			scale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(1.22, 1.0, dur)
			{
				EasingFunction = ease
			});
			scale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(1.22, 1.0, dur)
			{
				EasingFunction = ease
			});
			ZoomOverlay.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0.0, 1.0, Motion.Dur(Motion.Cat.Micro)));
			_semanticZoomed = true;
			RefreshZoomButton();
		}
	}

	private bool BuildZoomOverview()
	{
		ZoomHost.Children.Clear();
		List<(GroupVm, FrameworkElement)> els = new List<(GroupVm, FrameworkElement)>();
		double totalW = 0.0;
		double maxH = 0.0;
		foreach (GroupVm g in _groups)
		{
			if (GroupsHost.ItemContainerGenerator.ContainerFromItem(g) is FrameworkElement { ActualWidth: >1.0, ActualHeight: >1.0 } container)
			{
				FrameworkElement el = ((VisualTreeHelper.GetChildrenCount((DependencyObject)(object)container) > 0 && VisualTreeHelper.GetChild((DependencyObject)(object)container, 0) is FrameworkElement inner) ? inner : container);
				els.Add((g, el));
				totalW += el.ActualWidth;
				maxH = Math.Max(maxH, el.ActualHeight);
			}
		}
		if (els.Count == 0)
		{
			return false;
		}
		double availW = ((ZoomOverlay.ActualWidth > 100.0) ? ZoomOverlay.ActualWidth : base.ActualWidth) - 180.0;
		double availH = ((ZoomOverlay.ActualHeight > 100.0) ? ZoomOverlay.ActualHeight : (base.ActualHeight - 200.0)) - 80.0;
		double fitW = availW / Math.Max(1.0, totalW + 16.0 * (double)(els.Count - 1));
		double fitH = availH / Math.Max(1.0, maxH);
		double zoom = Math.Clamp(Math.Min(fitW, fitH), 0.18, 0.5);
		foreach (var item in els)
		{
			GroupVm g2 = item.Item1;
			FrameworkElement el2 = item.Item2;
			ImageSource snap = null;
			try
			{
				RenderTargetBitmap rtb = new RenderTargetBitmap((int)Math.Ceiling(el2.ActualWidth), (int)Math.Ceiling(el2.ActualHeight), 96.0, 96.0, PixelFormats.Pbgra32);
				rtb.Render(el2);
				((Freezable)rtb).Freeze();
				snap = rtb;
			}
			catch
			{
			}
			System.Windows.Controls.Image img = new System.Windows.Controls.Image
			{
				Source = snap,
				Width = el2.ActualWidth * zoom,
				Height = el2.ActualHeight * zoom,
				Stretch = Stretch.Fill,
				VerticalAlignment = VerticalAlignment.Top
			};
			RenderOptions.SetBitmapScalingMode((DependencyObject)(object)img, BitmapScalingMode.HighQuality);
			Border hover = new Border
			{
				Background = System.Windows.Media.Brushes.Transparent
			};
			Grid host = new Grid();
			host.Children.Add(img);
			host.Children.Add(hover);
			System.Windows.Controls.Button btn = new System.Windows.Controls.Button
			{
				Content = host,
				Margin = new Thickness(0.0, 0.0, 16.0, 0.0),
				Cursor = System.Windows.Input.Cursors.Hand,
				Background = System.Windows.Media.Brushes.Transparent,
				BorderThickness = new Thickness(0.0),
				FocusVisualStyle = null,
				Template = FlatButtonTemplate(),
				VerticalAlignment = VerticalAlignment.Center
			};
			GroupVm captured = g2;
			btn.Tag = g2;
			btn.Click += delegate
			{
				ZoomToGroup(captured);
			};
			btn.PreviewMouseLeftButtonDown += OnZoomGroupPress;
			btn.PreviewMouseMove += OnZoomGroupMove;
			btn.PreviewMouseLeftButtonUp += OnZoomGroupUp;
			ZoomHost.Children.Add(btn);
		}
		return true;
	}

	private void HideZoom()
	{
		if (ZoomOverlay.Visibility == Visibility.Visible)
		{
			if (_zoomDragging)
			{
				EndZoomGroupDrag();
			}
			Duration zdur = Motion.Dur(Motion.Cat.Micro);
			DoubleAnimation fade = new DoubleAnimation(ZoomOverlay.Opacity, 0.0, zdur);
			fade.Completed += delegate
			{
				ZoomOverlay.Visibility = Visibility.Collapsed;
				ZoomHost.Children.Clear();
			};
			// Mirror the zoom-in's scale so the exit isn't a flat opacity-only fade (recede 1.0 -> 1.22 in step with the fade).
			if (ZoomOverlay.RenderTransform is ScaleTransform zst)
			{
				IEasingFunction zease = Motion.Ease(Motion.Cat.Micro);
				zst.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(zst.ScaleX, 1.22, zdur) { EasingFunction = zease });
				zst.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(zst.ScaleY, 1.22, zdur) { EasingFunction = zease });
			}
			ZoomOverlay.BeginAnimation(UIElement.OpacityProperty, fade);
			_semanticZoomed = false;
			RefreshZoomButton();
		}
	}

	private void ZoomToGroup(GroupVm group)
	{
		//IL_004a: Unknown result type (might be due to invalid IL or missing references)
		//IL_004f: Unknown result type (might be due to invalid IL or missing references)
		//IL_0054: Unknown result type (might be due to invalid IL or missing references)
		HideZoom();
		if (GroupsHost.ItemContainerGenerator.ContainerFromItem(group) is FrameworkElement el)
		{
			System.Windows.Point val = el.TransformToAncestor(StartScroller).Transform(new System.Windows.Point(0.0, 0.0));
			double x = val.X;
			SmoothScroll.To(StartScroller, StartScroller.HorizontalOffset + x - 80.0);
		}
	}

	private void OnZoomBackgroundClick(object sender, MouseButtonEventArgs e)
	{
		HideZoom();
	}

	private void OnZoomGroupPress(object sender, MouseButtonEventArgs e)
	{
		//IL_0032: Unknown result type (might be due to invalid IL or missing references)
		//IL_0037: Unknown result type (might be due to invalid IL or missing references)
		_zoomDragBtn = sender as System.Windows.Controls.Button;
		_zoomDragGroup = _zoomDragBtn?.Tag as GroupVm;
		_zoomDragStart = e.GetPosition(ZoomDragLayer);
	}

	private void OnZoomGroupMove(object sender, System.Windows.Input.MouseEventArgs e)
	{
		//IL_002a: Unknown result type (might be due to invalid IL or missing references)
		//IL_002f: Unknown result type (might be due to invalid IL or missing references)
		//IL_0040: Unknown result type (might be due to invalid IL or missing references)
		//IL_0042: Unknown result type (might be due to invalid IL or missing references)
		//IL_0047: Unknown result type (might be due to invalid IL or missing references)
		//IL_004c: Unknown result type (might be due to invalid IL or missing references)
		//IL_00cd: Unknown result type (might be due to invalid IL or missing references)
		//IL_00d2: Unknown result type (might be due to invalid IL or missing references)
		if (_zoomDragBtn == null || e.LeftButton != MouseButtonState.Pressed)
		{
			return;
		}
		System.Windows.Point p = e.GetPosition(ZoomDragLayer);
		if (!_zoomDragging)
		{
			Vector val = p - _zoomDragStart;
			if (val.Length < 6.0)
			{
				return;
			}
			BeginZoomGroupDrag(e);
		}
		if (_zoomGhost != null)
		{
			Canvas.SetLeft(_zoomGhost, p.X - _zoomGrabOffset.X);
			Canvas.SetTop(_zoomGhost, p.Y - _zoomGrabOffset.Y);
		}
		System.Windows.Point position = e.GetPosition(ZoomHost);
		double cx = position.X;
		int target = TargetZoomIndex(cx);
		int cur = ZoomHost.Children.IndexOf(_zoomDragBtn);
		if (cur >= 0 && target >= 0 && target != cur)
		{
			ZoomHost.Children.Remove(_zoomDragBtn);
			ZoomHost.Children.Insert(target, _zoomDragBtn);
		}
		e.Handled = true;
	}

	private int TargetZoomIndex(double cursorX)
	{
		//IL_0032: Unknown result type (might be due to invalid IL or missing references)
		//IL_0037: Unknown result type (might be due to invalid IL or missing references)
		double x = 0.0;
		int n = ZoomHost.Children.Count;
		for (int i = 0; i < n; i++)
		{
			System.Windows.Size desiredSize = ZoomHost.Children[i].DesiredSize;
			double w = desiredSize.Width;
			if (cursorX < x + w / 2.0)
			{
				return i;
			}
			x += w;
		}
		return n - 1;
	}

	private void BeginZoomGroupDrag(System.Windows.Input.MouseEventArgs e)
	{
		//IL_001f: Unknown result type (might be due to invalid IL or missing references)
		//IL_0024: Unknown result type (might be due to invalid IL or missing references)
		if (_zoomDragBtn != null)
		{
			_zoomDragging = true;
			_zoomGrabOffset = e.GetPosition(_zoomDragBtn);
			_zoomDragBtn.CaptureMouse();
			_zoomDragBtn.LostMouseCapture += OnZoomGroupLostCapture;
			ZoomDragLayer.Visibility = Visibility.Visible;
			CreateZoomGhost(_zoomDragBtn);
			_zoomDragBtn.Opacity = 0.3;
			AnimatedBar.DragExempt = _zoomDragBtn;
		}
	}

	private void CreateZoomGhost(System.Windows.Controls.Button src)
	{
		//IL_00d7: Unknown result type (might be due to invalid IL or missing references)
		try
		{
			double w = src.ActualWidth;
			double h = src.ActualHeight;
			if (!(w < 1.0) && !(h < 1.0))
			{
				RenderTargetBitmap rtb = new RenderTargetBitmap((int)Math.Ceiling(w), (int)Math.Ceiling(h), 96.0, 96.0, PixelFormats.Pbgra32);
				rtb.Render(src);
				((Freezable)rtb).Freeze();
				ImageBrush brush = new ImageBrush(rtb)
				{
					Stretch = Stretch.Fill
				};
				((Freezable)brush).Freeze();
				ScaleTransform st = new ScaleTransform(1.0, 1.0);
				Border ghost = new Border
				{
					Width = w,
					Height = h,
					Background = brush,
					IsHitTestVisible = false,
					RenderTransformOrigin = new System.Windows.Point(0.5, 0.5),
					RenderTransform = st,
					Effect = new DropShadowEffect
					{
						BlurRadius = 20.0,
						ShadowDepth = 0.0,
						Opacity = 0.55,
						Color = Colors.Black
					}
				};
				ZoomDragLayer.Children.Add(ghost);
				_zoomGhost = ghost;
				Duration dur = new Duration(TimeSpan.FromMilliseconds(140L));
				QuadraticEase ease = new QuadraticEase
				{
					EasingMode = EasingMode.EaseOut
				};
				st.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(1.0, 1.05, dur)
				{
					EasingFunction = ease
				});
				st.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(1.0, 1.05, dur)
				{
					EasingFunction = ease
				});
			}
		}
		catch (Exception ex)
		{
			Logger.Log("CreateZoomGhost failed: " + ex.Message);
		}
	}

	private void OnZoomGroupUp(object sender, MouseButtonEventArgs e)
	{
		if (_zoomDragging)
		{
			EndZoomGroupDrag();
			CommitZoomGroupOrder();
			e.Handled = true;
		}
		_zoomDragBtn = null;
		_zoomDragGroup = null;
	}

	private void OnZoomGroupLostCapture(object sender, System.Windows.Input.MouseEventArgs e)
	{
		if (_zoomDragging)
		{
			EndZoomGroupDrag();
			CommitZoomGroupOrder();
			_zoomDragBtn = null;
			_zoomDragGroup = null;
		}
	}

	private void EndZoomGroupDrag()
	{
		_zoomDragging = false;
		AnimatedBar.DragExempt = null;
		if (_zoomDragBtn != null)
		{
			_zoomDragBtn.LostMouseCapture -= OnZoomGroupLostCapture;
			_zoomDragBtn.ReleaseMouseCapture();
			_zoomDragBtn.Opacity = 1.0;
		}
		if (_zoomGhost != null)
		{
			ZoomDragLayer.Children.Remove(_zoomGhost);
			_zoomGhost = null;
		}
		ZoomDragLayer.Visibility = Visibility.Collapsed;
	}

	private void CommitZoomGroupOrder()
	{
		try
		{
			List<GroupVm> ordered = (from b in ZoomHost.Children.OfType<System.Windows.Controls.Button>()
				select b.Tag as GroupVm into g
				where g != null
				select g).Cast<GroupVm>().ToList();
			if (ordered.Count != _groups.Count)
			{
				return;
			}
			bool changed = false;
			for (int i = 0; i < ordered.Count; i++)
			{
				int cur = _groups.IndexOf(ordered[i]);
				if (cur >= 0 && cur != i)
				{
					_groups.Move(cur, i);
					changed = true;
				}
			}
			if (changed)
			{
				Profile.Save(_groups);
			}
		}
		catch (Exception ex)
		{
			Logger.Log("Group reorder commit failed: " + ex.Message);
		}
	}

	private void OnLiveContentLoaded(object sender, RoutedEventArgs e)
	{
		Grid host = sender as Grid;
		if (host?.DataContext is TileVm { IsMetroLiveTile: true } && host.Children.Count >= 2)
		{
			host.Children[0].Visibility = Visibility.Collapsed;
			host.Children[1].Visibility = Visibility.Visible;
			return;
		}
		if (host != null && host.DataContext is TileVm tile && !tile.IsMetroLiveTile && host.RenderTransform is ScaleTransform flip && host.Children.Count >= 2 && !_liveFlips.Any((LiveFlipState x) => x.Host == host))
		{
			// Authentic Win8.1 live tiles ALTERNATE between their icon face (the brand-colour tile with its 8.1 art/glyph)
			// and a CONTENT face carrying live data — clock time, weather temp, mail/agenda. Start on the ICON face so a tile
			// whose content hasn't loaded yet (Weather/Mail fetch async) never flashes blank; FlipDueTiles only turns to the
			// content face once LivePrimary is non-empty. Motion=Off keeps everything static (StartLiveFlips returns early).
			host.Children[0].Visibility = Visibility.Collapsed;   // content face hidden until there's content to show
			host.Children[1].Visibility = Visibility.Visible;     // icon face
			_liveFlips.Add(new LiveFlipState
			{
				Host = host,
				Flip = flip,
				ContentFace = host.Children[0],
				IconFace = host.Children[1],
				ContentUp = false,   // currently showing the icon face
				NextFlip = Environment.TickCount64 + _flipRand.Next(1800, 6500)
			});
		}
	}

	private void OnMetroContentLoaded(object sender, RoutedEventArgs e)
	{
		if (sender is not Grid host || host.DataContext is not TileVm { IsMetroLiveTile: true } ||
			host.RenderTransform is not ScaleTransform flip || host.Children.Count < 2 ||
			_liveFlips.Any((LiveFlipState x) => x.Host == host))
		{
			return;
		}

		// Rest on useful content; headlines and forecasts get longer reading time than the clock/agenda.
		var dwell = WeatherTileFlipPolicy.For(((TileVm)host.DataContext).Live);
		host.Children[0].Visibility = Visibility.Visible;
		host.Children[1].Visibility = Visibility.Collapsed;
		LiveFlipState state = new LiveFlipState
		{
			Host = host,
			Flip = flip,
			ContentFace = host.Children[0],
			IconFace = host.Children[1],
			ContentUp = true,
			RestOnContent = true,
			ContentDwellMinMs = dwell.ContentMin,
			ContentDwellMaxMs = dwell.ContentMax,
			IconDwellMinMs = dwell.LogoMin,
			IconDwellMaxMs = dwell.LogoMax,
			NextFlip = Environment.TickCount64 + _flipRand.Next(dwell.ContentMin, dwell.ContentMax)
		};
		_liveFlips.Add(state);
		TileVm tile = (TileVm)host.DataContext;
		DependencyPropertyChangedEventHandler visibilityChanged = delegate { if (!CanFlipMetro(state)) ResetLiveFlip(state); };
		SizeChangedEventHandler sizeChanged = delegate { ResetLiveFlip(state); };
		System.ComponentModel.PropertyChangedEventHandler settingsChanged = delegate(object? _, System.ComponentModel.PropertyChangedEventArgs args)
		{
			if (args.PropertyName is nameof(TileVm.MetroMotionEnabled) or nameof(TileVm.LiveOff))
				if (!CanFlipMetro(state)) ResetLiveFlip(state);
		};
		ScrollViewer? scroll = MetroFlipViewport(host);
		ScrollChangedEventHandler scrolled = delegate { if (!CanFlipMetro(state)) ResetLiveFlip(state); };
		host.IsVisibleChanged += visibilityChanged;
		host.SizeChanged += sizeChanged;
		tile.PropertyChanged += settingsChanged;
		if (scroll != null) scroll.ScrollChanged += scrolled;
		state.Detach = delegate
		{
			host.IsVisibleChanged -= visibilityChanged;
			host.SizeChanged -= sizeChanged;
			tile.PropertyChanged -= settingsChanged;
			if (scroll != null) scroll.ScrollChanged -= scrolled;
		};
	}

	private static ScrollViewer? MetroFlipViewport(DependencyObject host)
	{
		for (DependencyObject? parent = host; parent != null; parent = VisualTreeHelper.GetParent(parent))
			if (parent is ScrollViewer scroll) return scroll;
		return null;
	}

	private static bool CanFlipMetro(LiveFlipState state)
	{
		if (Motion.Mode is MotionMode.Off or MotionMode.Reduced || !state.Host.IsVisible ||
			state.Host.DataContext is not TileVm { MetroMotionEnabled: true, LiveOff: false, MetroVisual: not null })
			return false;
		ScrollViewer? scroll = MetroFlipViewport(state.Host);
		if (scroll == null) return true;
		try
		{
			FrameworkElement target = state.Host.TemplatedParent as FrameworkElement ?? state.Host;
			Rect bounds = target.TransformToAncestor(scroll).TransformBounds(new Rect(0, 0, target.ActualWidth, target.ActualHeight));
			return bounds.IntersectsWith(new Rect(0, 0, scroll.ViewportWidth, scroll.ViewportHeight));
		}
		catch { return false; }
	}

	private void ResetLiveFlip(LiveFlipState state)
	{
		state.AnimationVersion++;
		state.IsFlipping = false;
		state.Flip.BeginAnimation(ScaleTransform.ScaleYProperty, null);
		state.Flip.ScaleY = 1;
		state.ContentUp = state.RestOnContent;
		state.ContentFace.Visibility = state.ContentUp ? Visibility.Visible : Visibility.Collapsed;
		state.IconFace.Visibility = state.ContentUp ? Visibility.Collapsed : Visibility.Visible;
		state.NextFlip = Environment.TickCount64 + (state.ContentUp
			? _flipRand.Next(state.ContentDwellMinMs, state.ContentDwellMaxMs)
			: _flipRand.Next(state.IconDwellMinMs, state.IconDwellMaxMs));
	}

	private void OnLiveContentUnloaded(object sender, RoutedEventArgs e)
	{
		FrameworkElement fe = sender as FrameworkElement;
		if (fe != null)
		{
			foreach (LiveFlipState state in _liveFlips.Where(x => x.Host == fe))
			{
				ResetLiveFlip(state);
				state.Detach?.Invoke();
			}
			_liveFlips.RemoveAll((LiveFlipState x) => x.Host == fe);
		}
	}

	private void StartLiveFlips()
	{
		//IL_0011: Unknown result type (might be due to invalid IL or missing references)
		//IL_0016: Unknown result type (might be due to invalid IL or missing references)
		//IL_002d: Expected O, but got Unknown
		if (_flipTimer == null)
		{
			_flipTimer = new DispatcherTimer((DispatcherPriority)4)
			{
				Interval = TimeSpan.FromMilliseconds(900L)
			};
			_flipTimer.Tick += delegate
			{
				FlipDueTiles();
			};
		}
		// Reduced-motion contract: when motion is Off, do not run the continuous live-tile flip loop at all.
		if (Motion.Mode == MotionMode.Off)
		{
			StopLiveFlips();
			return;
		}
		_flipTimer.Start();
	}

	private void StopLiveFlips()
	{
		DispatcherTimer? flipTimer = _flipTimer;
		if (flipTimer != null)
		{
			flipTimer.Stop();
		}
		foreach (LiveFlipState s in _liveFlips)
		{
			ResetLiveFlip(s);
		}
	}

	private void PauseLiveFlips()
	{
		_flipTimer?.Stop();
		foreach (LiveFlipState state in _liveFlips)
			if (state.RestOnContent) ResetLiveFlip(state);
	}

	private void FlipDueTiles()
	{
		if (Motion.Mode == MotionMode.Off)
		{
			StopLiveFlips();
			return;
		}
		long now = Environment.TickCount64;
		foreach (LiveFlipState s in _liveFlips)
		{
			if (s.RestOnContent && !CanFlipMetro(s))
			{
				if (s.IsFlipping || !s.ContentUp) ResetLiveFlip(s);
				continue;
			}
			if (!s.Host.IsVisible || now < s.NextFlip || s.IsFlipping)
			{
				continue;
			}
			// Don't turn to the content face while there's no live content yet (Weather/Mail still fetching, or a signed-out
			// account) — keep the icon face up and re-check shortly, so a tile never shows a blank content face.
			if (!s.ContentUp && !HasLiveContent(s))
			{
				s.NextFlip = now + 2000L;
				continue;
			}
			// Schedule the dwell of the face that will be visible after this flip.
			s.NextFlip = now + (s.ContentUp
				? _flipRand.Next(s.IconDwellMinMs, s.IconDwellMaxMs)
				: _flipRand.Next(s.ContentDwellMinMs, s.ContentDwellMaxMs));
			FlipOne(s);
		}
	}

	private static bool HasLiveContent(LiveFlipState s)
	{
		return s.Host?.DataContext is TileVm t && (t.IsMetroLiveTile
			? t.MetroVisual != null
			: !string.IsNullOrWhiteSpace(t.LivePrimary));
	}

	private void FlipOne(LiveFlipState state)
	{
		long animationVersion = ++state.AnimationVersion;
		state.IsFlipping = true;
		// Honor the Motion timing contract: Fast (0.8) / Reduced (0.55) shorten the flip; Authentic (1.0) is unchanged.
		double f = Motion.Factor;
		int downMs = Math.Max(1, (int)(_flipRand.Next(170, 230) * f));
		int upMs = Math.Max(1, (int)(_flipRand.Next(220, 300) * f));
		DoubleAnimation down = new DoubleAnimation(1.0, 0.0, new Duration(TimeSpan.FromMilliseconds(downMs)))
		{
			EasingFunction = new QuadraticEase
			{
				EasingMode = EasingMode.EaseIn
			}
		};
		down.Completed += delegate
		{
			if (state.AnimationVersion != animationVersion) return;
			if (state.RestOnContent && !CanFlipMetro(state))
			{
				ResetLiveFlip(state);
				return;
			}
			state.ContentUp = !state.ContentUp;
			state.ContentFace.Visibility = ((!state.ContentUp) ? Visibility.Collapsed : Visibility.Visible);
			state.IconFace.Visibility = (state.ContentUp ? Visibility.Collapsed : Visibility.Visible);
			DoubleAnimation up = new DoubleAnimation(0.0, 1.0, new Duration(TimeSpan.FromMilliseconds(upMs)))
			{
				EasingFunction = new QuadraticEase
				{
					EasingMode = EasingMode.EaseOut
				}
			};
			up.Completed += delegate
			{
				if (state.AnimationVersion != animationVersion) return;
				state.IsFlipping = false;
				state.Flip.BeginAnimation(ScaleTransform.ScaleYProperty, null);
				state.Flip.ScaleY = 1;
			};
			state.Flip.BeginAnimation(ScaleTransform.ScaleYProperty, up);
		};
		state.Flip.BeginAnimation(ScaleTransform.ScaleYProperty, down);
	}

	// ---- Win8.1-style corner Start button (inside the fullscreen Start screen) --------------------------------------
	// The Metro Start covers the taskbar, so the affordance to return to the Desktop is a Windows button that appears
	// in the BOTTOM-LEFT CORNER when the cursor nears it. It is a black square box holding the Win8.1 Windows glyph
	// (white by default, like the taskbar's), and while pressed the glyph turns the theme's accent colour. Clicking it
	// hides Start (back to Desktop). See memory icon-whitecircle-and-start-hotspot.
	private Border? _cornerStartBtn;

	private SolidColorBrush? _cornerGlyphBrush;

	private bool _cornerBtnShown;

	private void SetupCornerStartButton()
	{
		try
		{
			// Glyph = a Rectangle filled with a recolourable brush, masked to the Win8.1 logo shape (same technique as
			// the taskbar Start button, so it matches exactly). White default; theme accent while pressed.
			_cornerGlyphBrush = new SolidColorBrush(Colors.White);
			System.Windows.Shapes.Rectangle glyph = new System.Windows.Shapes.Rectangle
			{
				Width = 26.0,
				Height = 26.0,
				HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
				VerticalAlignment = System.Windows.VerticalAlignment.Center,
				Fill = _cornerGlyphBrush
			};
			try
			{
				BitmapImage bmp = new BitmapImage();
				bmp.BeginInit();
				bmp.UriSource = new Uri("pack://application:,,,/Assets/start81_32.png", UriKind.Absolute);
				bmp.CacheOption = BitmapCacheOption.OnLoad;
				bmp.EndInit();
				((Freezable)bmp).Freeze();
				glyph.OpacityMask = new ImageBrush(bmp) { Stretch = Stretch.Uniform };
			}
			catch
			{
			}
			_cornerStartBtn = new Border
			{
				Width = 48.0,
				Height = 48.0,
				Background = new SolidColorBrush(System.Windows.Media.Colors.Black),   // black box
				HorizontalAlignment = System.Windows.HorizontalAlignment.Left,
				VerticalAlignment = System.Windows.VerticalAlignment.Bottom,
				Margin = new Thickness(0.0),
				Opacity = 0.0,
				IsHitTestVisible = false,
				Cursor = System.Windows.Input.Cursors.Hand,
				Child = glyph
			};
			System.Windows.Controls.Grid.SetRow(_cornerStartBtn, 0);
			System.Windows.Controls.Grid.SetRowSpan(_cornerStartBtn, 3);   // span all rows so Bottom+Left = screen corner
			System.Windows.Controls.Panel.SetZIndex(_cornerStartBtn, 10000);
			_cornerStartBtn.MouseLeftButtonDown += delegate(object _, MouseButtonEventArgs e)
			{
				if (_cornerGlyphBrush != null)
				{
					_cornerGlyphBrush.Color = StartAccent.Color();   // pressed -> theme accent
				}
				e.Handled = true;
			};
			_cornerStartBtn.MouseLeftButtonUp += delegate(object _, MouseButtonEventArgs e)
			{
				if (_cornerGlyphBrush != null)
				{
					_cornerGlyphBrush.Color = Colors.White;
				}
				e.Handled = true;
				HideStart(animate: true);   // back to Desktop
			};
			RootGrid.Children.Add(_cornerStartBtn);
			// Reveal on approach to the bottom-left corner; hide when the cursor leaves it.
			RootGrid.MouseMove += delegate(object _, System.Windows.Input.MouseEventArgs e)
			{
				System.Windows.Point p = e.GetPosition(RootGrid);
				bool near = p.X <= 72.0 && p.Y >= RootGrid.ActualHeight - 72.0;
				ShowCornerStartButton(near);
			};
			base.MouseLeave += delegate
			{
				ShowCornerStartButton(show: false);
			};
		}
		catch (Exception ex)
		{
			Logger.Log("Corner Start button setup failed: " + ex.Message);
		}
	}

	private void ShowCornerStartButton(bool show)
	{
		if (_cornerStartBtn == null || _cornerBtnShown == show)
		{
			return;
		}
		_cornerBtnShown = show;
		_cornerStartBtn.IsHitTestVisible = show;
		if (!show && _cornerGlyphBrush != null)
		{
			_cornerGlyphBrush.Color = Colors.White;
		}
		_cornerStartBtn.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(show ? 1.0 : 0.0, Motion.Dur(Motion.Cat.Micro))
		{
			EasingFunction = Motion.Ease(Motion.Cat.Micro)
		});
	}

	public void HideStart(bool animate = false)
	{
		//IL_01d1: Unknown result type (might be due to invalid IL or missing references)
		if (_dragging)
		{
			// Packed mode reorders live while dragging. Commit the current drop before flushing so a Start
			// deactivation cannot leave the visible layout newer than the durable profile.
			System.Windows.Point pos = _lastDragPoint;
			EndTileDrag();
			CommitDrop(pos);
		}
		CommitActiveGroupEditAndFlush();
		Logger.Log($"Start: hide (was visible={base.IsVisible}, animate={animate})");
		NewGroupVisual.Opacity = 0.0;
		if (PersonalizeHost.Visibility == Visibility.Visible)
		{
			PersonalizeSlide.BeginAnimation(TranslateTransform.XProperty, null);
			PersonalizeSlide.X = 392.0;
			PersonalizeHost.Visibility = Visibility.Collapsed;
		}
		if (TileAppBar.Visibility == Visibility.Visible)
		{
			TileAppBarSlide.BeginAnimation(TranslateTransform.YProperty, null);
			TileAppBarSlide.Y = 92.0;
			TileAppBar.Visibility = Visibility.Collapsed;
			if (_appBarTile != null)
			{
				_appBarTile.IsSelected = false;
				_appBarTile = null;
			}
			if (_appBarEntry != null)
			{
				_appBarEntry.IsSelected = false;
				_appBarEntry = null;
			}
		}
		SetCustomise(on: false);
		if (animate && base.IsVisible && !_closing && Motion.Mode != MotionMode.Off)
		{
			_closing = true;
			SetTransientCache(on: true);
			ScaleTransform scale = new ScaleTransform(1.0, 1.0);
			RootGrid.RenderTransform = scale;
			RootGrid.RenderTransformOrigin = new System.Windows.Point(0.5, 0.5);
			// Authentic 8.1 close = the Start surface RECEDES (1.0 -> 0.92) and fades to reveal the desktop — the mirror
			// of the open, replacing the old inauthentic grow-to-1.06. 160ms is deliberately snappier than the 200ms
			// open (dismissals feel best faster than entrances), and CubicEase-In holds it near-opaque as it visibly
			// falls back, then removes it decisively with no lingering ghost veil.
			Duration dur = new Duration(TimeSpan.FromMilliseconds(Math.Max(1.0, 160.0 * Motion.Factor)));
			IEasingFunction ease = Motion.StartCloseEase;
			double to = ((Motion.Mode == MotionMode.Reduced) ? 0.98 : 0.92);
			scale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(1.0, to, dur)
			{
				EasingFunction = ease
			});
			scale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(1.0, to, dur)
			{
				EasingFunction = ease
			});
			DoubleAnimation fade = new DoubleAnimation(1.0, 0.0, dur)
			{
				EasingFunction = ease
			};
			fade.Completed += delegate
			{
				_liveTiles?.Stop();
				StopLiveFlips();
				Hide();
				EndViewLowLatency();
				RootGrid.BeginAnimation(UIElement.OpacityProperty, null);
				RootGrid.Opacity = 1.0;
				scale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
				scale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
				RootGrid.RenderTransform = null;
				SetTransientCache(on: false);
				_closing = false;
				ScheduleIdleTrim();
			};
			RootGrid.BeginAnimation(UIElement.OpacityProperty, fade);
		}
		else
		{
			_liveTiles?.Stop();
			StopLiveFlips();
			Hide();
			EndViewLowLatency();
			ScheduleIdleTrim();
		}
	}

	private bool _trimScheduled;

	// After Start closes (the launcher's heaviest surface — it decodes tile art + every all-apps icon), return that
	// idle RAM to the OS: a non-blocking gen2 collect frees transients, then EmptyWorkingSet trims the working set
	// (cached bitmap pages fault back only if Start reopens). Runs at ApplicationIdle so it never competes with the
	// close animation, and is debounced so rapid open/close can't thrash it.
	private void ScheduleIdleTrim()
	{
		if (_trimScheduled)
		{
			return;
		}
		_trimScheduled = true;
		base.Dispatcher.BeginInvoke((System.Windows.Threading.DispatcherPriority)2, (Delegate)(Action)delegate
		{
			_trimScheduled = false;
			try
			{
				GC.Collect(2, GCCollectionMode.Optimized, blocking: false);
				NativeShell.TrimSelf();
			}
			catch
			{
			}
		});
	}

	private void EnsureDesktopTile()
	{
		if (!_groups.SelectMany((GroupVm g) => g.Tiles).Any((TileVm t) => t.IsDesktop))
		{
			if (_groups.Count == 0)
			{
				_groups.Add(new GroupVm());
			}
			_groups[0].Tiles.Insert(0, LiveTiles.Create(LiveKind.Desktop));
			Profile.Save(_groups);
		}
	}

	private void RefreshDesktopTile()
	{
		try
		{
			TileVm desk = _groups.SelectMany((GroupVm g) => g.Tiles).FirstOrDefault((TileVm t) => t.IsDesktop);
			if (desk?.Entry != null)
			{
				desk.Entry.TileBrush = LiveTiles.DesktopFace();
			}
		}
		catch (Exception ex)
		{
			Logger.Log("RefreshDesktopTile failed: " + ex.Message);
		}
	}

	public void AddLiveTile(LiveKind kind)
	{
		if (_groups.Count == 0)
		{
			_groups.Add(new GroupVm
			{
				Name = "Live"
			});
		}
		if (!_groups.Any((GroupVm g) => g.Tiles.Any((TileVm t) => t.Live == kind)))
		{
			ObservableCollection<GroupVm> groups = _groups;
			groups[groups.Count - 1].Tiles.Add(LiveTiles.Create(kind));
			_liveTiles?.Update();
			if (kind == LiveKind.Weather)
			{
				_liveTiles?.RefreshWeatherNow();
			}
			LiveKind liveKind = kind;
			if ((uint)(liveKind - 7) <= 1u)
			{
				_liveTiles?.RefreshGoogleNow();
			}
			Profile.Save(_groups);
		}
	}

	public void SetWeatherCity(string city)
	{
		_weatherCity = city;
		if (_liveTiles != null)
		{
			_liveTiles.WeatherCity = city;
			_liveTiles.RefreshWeatherNow();
		}
	}

	public void SetWeatherUnits(string units)
	{
		_weatherUnits = string.Equals(units, "F", StringComparison.OrdinalIgnoreCase) ? "F" : "C";
		if (_liveTiles != null) _liveTiles.WeatherUnits = _weatherUnits;
	}

	public void RefreshGoogleTiles()
	{
		_liveTiles?.RefreshGoogleNow();
	}

	public void SetNewsSource(string url) => _liveTiles?.SetNewsSource(url);

	public void ClearGoogleTiles()
	{
		_liveTiles?.ClearGoogle();
	}

	public void SetTileDensity(double scale)
	{
		TileMetrics.Scale = scale;
		if (_groups.Count != 0)
		{
			GroupsHost.ItemsSource = null;
			GroupsHost.ItemsSource = _groups;
		}
	}

	private void PositionOnActiveScreen()
	{
		//IL_003f: Unknown result type (might be due to invalid IL or missing references)
		//IL_0044: Unknown result type (might be due to invalid IL or missing references)
		//IL_008a: Unknown result type (might be due to invalid IL or missing references)
		//IL_008f: Unknown result type (might be due to invalid IL or missing references)
		Screen screen = Screen.FromPoint(System.Windows.Forms.Cursor.Position);
		// Full monitor bounds: the Metro Start screen is fullscreen and covers the taskbar (authentic Win8.1). The
		// affordance to return to Desktop while Start is open is the in-Start corner Windows button (see the corner
		// Start button wired in the constructor), NOT a visible taskbar. See memory icon-whitecircle-and-start-hotspot.
		System.Drawing.Rectangle b = screen.Bounds;
		// Size from the TARGET monitor's DPI (the screen the cursor is on), NOT the window's current TransformToDevice —
		// the latter is the monitor the window is CURRENTLY on, which mis-sizes the fullscreen Start when it opens on a
		// different-scale monitor. On single-monitor / same-DPI this is identical, so it's a no-op there.
		double sx = MonitorDpi.ScaleFor(b);
		double sy = sx;
		if (sx <= 0.0)
		{
			sx = 1.0;
		}
		if (sy <= 0.0)
		{
			sy = 1.0;
		}
		base.Left = (double)b.Left / sx;
		base.Top = (double)b.Top / sy;
		base.Width = (double)b.Width / sx;
		base.Height = (double)b.Height / sy;
	}

	private void OnDisplaySettingsChanged(object? sender, EventArgs e)
	{
		if (base.IsVisible)
		{
			((DispatcherObject)this).Dispatcher.BeginInvoke((Delegate)new Action(PositionOnActiveScreen), Array.Empty<object>());
		}
	}

	private void SwitchView(bool showAllApps, bool animate = true)
	{
		int transitionVersion = ++_viewTransitionVersion;
		_viewTransitionStartTimestamp = Stopwatch.GetTimestamp();
		bool targetAlreadyActive = showAllApps == _showingAllApps && !_viewTransitionActive;
		animate = animate && Motion.Mode != MotionMode.Off;
		if (animate)
		{
			BeginViewLowLatency();
		}
		HideTileAppBar();
		if (showAllApps)
		{
			if (!_appsViewBuilt)
			{
				BuildAppsView();
			}
			ScheduleDeferredAppIconLoad(animate);
		}
		else
		{
			_deferredIconDelayTimer?.Stop();
		}
		_showingAllApps = showAllApps;
		if (showAllApps)
		{
			_liveTiles?.Stop();
			PauseLiveFlips();
		}
		FrameworkElement incoming = (showAllApps ? AppsScroller : StartScroller);
		FrameworkElement outgoing = (showAllApps ? StartScroller : AppsScroller);
		TranslateTransform incomingSlide = ViewSlide(incoming);
		TranslateTransform outgoingSlide = ViewSlide(outgoing);
		bool incomingWasVisible = incoming.Visibility == Visibility.Visible;
		if (targetAlreadyActive && incomingWasVisible)
		{
			animate = false;
		}
		StopViewFrameTransition();
		StopViewTransition(StartScroller, StartViewSlide);
		StopViewTransition(AppsScroller, AppsViewSlide);
		incoming.Visibility = Visibility.Visible;
		outgoing.Visibility = Visibility.Visible;
		incoming.IsHitTestVisible = true;
		outgoing.IsHitTestVisible = false;
		System.Windows.Controls.Panel.SetZIndex(outgoing, 0);
		System.Windows.Controls.Panel.SetZIndex(incoming, 1);
		HeaderText.Text = (showAllApps ? "Apps" : "Start");
		SortDropdown.Visibility = ((!showAllApps) ? Visibility.Collapsed : Visibility.Visible);
		SearchGroup.Visibility = ((!showAllApps) ? Visibility.Collapsed : Visibility.Visible);
		StartHeaderButtons.Visibility = (showAllApps ? Visibility.Collapsed : Visibility.Visible);
		ViewToggleArrowUp(showAllApps);
		_activeScroller = showAllApps ? AppsScroller : StartScroller;
		((DispatcherObject)this).Dispatcher.BeginInvoke((Delegate)(Action)delegate
		{
			if (transitionVersion == _viewTransitionVersion)
			{
				UpdateBottomScroll();
			}
		}, DispatcherPriority.Background, Array.Empty<object>());
		if (showAllApps && SearchBox.IsKeyboardFocused)
		{
			AppsScroller.Focusable = true;
			AppsScroller.Focus();
		}
		UpdateSearchWatermark();
		if (!animate)
		{
			CompleteViewTransition(transitionVersion, showAllApps, incoming, incomingSlide, outgoing, outgoingSlide);
			return;
		}

		double from = (showAllApps ? 36.0 : -36.0);
		double outTo = (showAllApps ? -20.0 : 20.0);
		if (!incomingWasVisible)
		{
			incoming.Opacity = 0.0;
			incomingSlide.Y = from;
		}
		incoming.CacheMode ??= ViewTransitionCache(incoming);
		outgoing.CacheMode ??= ViewTransitionCache(outgoing);
		_viewTransitionActive = true;
		StartViewFrameTransition(transitionVersion, showAllApps, incoming, incomingSlide, outgoing, outgoingSlide, outTo);
	}

	private void StartViewFrameTransition(int version, bool showAllApps, FrameworkElement incoming, TranslateTransform incomingSlide, FrameworkElement outgoing, TranslateTransform outgoingSlide, double outgoingYTo)
	{
		_viewFrameVersion = version;
		_viewFrameTargetApps = showAllApps;
		_viewFrameStartTimestamp = Stopwatch.GetTimestamp();
		_viewFrameIncoming = incoming;
		_viewFrameOutgoing = outgoing;
		_viewFrameIncomingSlide = incomingSlide;
		_viewFrameOutgoingSlide = outgoingSlide;
		_viewFrameIncomingOpacityFrom = incoming.Opacity;
		_viewFrameOutgoingOpacityFrom = outgoing.Opacity;
		_viewFrameIncomingYFrom = incomingSlide.Y;
		_viewFrameOutgoingYFrom = outgoingSlide.Y;
		_viewFrameOutgoingYTo = outgoingYTo;
		_viewFrameEnterMs = Motion.Time(Motion.Cat.ViewEnter).TotalMilliseconds;
		_viewFrameExitMs = Motion.Time(Motion.Cat.ViewExit).TotalMilliseconds;
		_viewFrameEnterEase = Motion.Ease(Motion.Cat.ViewEnter);
		_viewFrameExitEase = Motion.Ease(Motion.Cat.ViewExit);
		_viewFrameHandler ??= OnViewTransitionFrame;
		if (!_viewFrameHooked)
		{
			CompositionTarget.Rendering += _viewFrameHandler;
			_viewFrameHooked = true;
		}
	}

	private void OnViewTransitionFrame(object? sender, EventArgs e)
	{
		FrameworkElement? incoming = _viewFrameIncoming;
		FrameworkElement? outgoing = _viewFrameOutgoing;
		TranslateTransform? incomingSlide = _viewFrameIncomingSlide;
		TranslateTransform? outgoingSlide = _viewFrameOutgoingSlide;
		if (!_viewFrameHooked || incoming == null || outgoing == null || incomingSlide == null || outgoingSlide == null
			|| _viewFrameEnterEase == null || _viewFrameExitEase == null || _viewFrameVersion != _viewTransitionVersion)
		{
			StopViewFrameTransition();
			return;
		}

		double elapsedMs = (double)(Stopwatch.GetTimestamp() - _viewFrameStartTimestamp) * 1000.0 / Stopwatch.Frequency;
		double enterProgress = Math.Clamp(elapsedMs / Math.Max(1.0, _viewFrameEnterMs), 0.0, 1.0);
		double exitProgress = Math.Clamp(elapsedMs / Math.Max(1.0, _viewFrameExitMs), 0.0, 1.0);
		double enterEase = _viewFrameEnterEase.Ease(enterProgress);
		double exitEase = _viewFrameExitEase.Ease(exitProgress);
		incoming.Opacity = _viewFrameIncomingOpacityFrom + (1.0 - _viewFrameIncomingOpacityFrom) * enterEase;
		incomingSlide.Y = _viewFrameIncomingYFrom * (1.0 - enterEase);
		outgoing.Opacity = _viewFrameOutgoingOpacityFrom * (1.0 - exitEase);
		outgoingSlide.Y = _viewFrameOutgoingYFrom + (_viewFrameOutgoingYTo - _viewFrameOutgoingYFrom) * exitEase;

		if (enterProgress >= 1.0 && exitProgress >= 1.0)
		{
			int version = _viewFrameVersion;
			bool targetApps = _viewFrameTargetApps;
			StopViewFrameTransition();
			CompleteViewTransition(version, targetApps, incoming, incomingSlide, outgoing, outgoingSlide);
		}
	}

	private void StopViewFrameTransition()
	{
		if (_viewFrameHooked && _viewFrameHandler != null)
		{
			CompositionTarget.Rendering -= _viewFrameHandler;
		}
		_viewFrameHooked = false;
	}

	private void BeginViewLowLatency()
	{
		try
		{
			GCLatencyMode current = GCSettings.LatencyMode;
			if (current == GCLatencyMode.NoGCRegion)
			{
				return;
			}
			_viewPreviousGcLatencyMode ??= current;
			GCSettings.LatencyMode = GCLatencyMode.LowLatency;
		}
		catch
		{
		}
	}

	private void RelaxViewLowLatency()
	{
		if (!_viewPreviousGcLatencyMode.HasValue)
		{
			return;
		}
		try
		{
			if (GCSettings.LatencyMode != GCLatencyMode.NoGCRegion)
			{
				GCSettings.LatencyMode = GCLatencyMode.SustainedLowLatency;
			}
		}
		catch
		{
		}
	}

	private void EndViewLowLatency()
	{
		StopViewFrameTransition();
		if (!_viewPreviousGcLatencyMode.HasValue)
		{
			return;
		}
		GCLatencyMode previous = _viewPreviousGcLatencyMode.Value;
		_viewPreviousGcLatencyMode = null;
		try
		{
			if (GCSettings.LatencyMode != GCLatencyMode.NoGCRegion)
			{
				GCSettings.LatencyMode = previous;
			}
		}
		catch
		{
		}
	}

	private void ScheduleDeferredAppIconLoad(bool afterTransition)
	{
		_deferredIconDelayTimer?.Stop();
		if (!afterTransition)
		{
			EnsureDeferredAppIconsLoaded();
			return;
		}
		_deferredIconDelayTimer ??= new DispatcherTimer(DispatcherPriority.Background)
		{
			Interval = TimeSpan.FromMilliseconds(200.0)
		};
		_deferredIconDelayTimer.Interval = Motion.Time(Motion.Cat.ViewEnter) + TimeSpan.FromMilliseconds(24.0);
		_deferredIconDelayTimer.Tick -= OnDeferredIconDelay;
		_deferredIconDelayTimer.Tick += OnDeferredIconDelay;
		_deferredIconDelayTimer.Start();
	}

	private void OnDeferredIconDelay(object? sender, EventArgs e)
	{
		_deferredIconDelayTimer?.Stop();
		if (_showingAllApps && base.IsVisible)
		{
			EnsureDeferredAppIconsLoaded();
		}
	}

	private TranslateTransform ViewSlide(FrameworkElement view)
	{
		return ReferenceEquals(view, StartScroller) ? StartViewSlide : AppsViewSlide;
	}

	private BitmapCache ViewTransitionCache(FrameworkElement view)
	{
		if (ReferenceEquals(view, StartScroller))
		{
			return _startViewTransitionCache ??= DpiCache();
		}
		return _appsViewTransitionCache ??= DpiCache();
	}

	private static void StopViewTransition(FrameworkElement view, TranslateTransform slide)
	{
		double opacity = view.Opacity;
		double y = slide.Y;
		if (view.HasAnimatedProperties)
		{
			view.BeginAnimation(UIElement.OpacityProperty, null);
		}
		if (slide.HasAnimatedProperties)
		{
			slide.BeginAnimation(TranslateTransform.YProperty, null);
		}
		view.Opacity = double.IsFinite(opacity) ? Math.Clamp(opacity, 0.0, 1.0) : 1.0;
		slide.Y = double.IsFinite(y) ? y : 0.0;
	}

	private void CompleteViewTransition(int transitionVersion, bool showAllApps, FrameworkElement incoming, TranslateTransform incomingSlide, FrameworkElement outgoing, TranslateTransform outgoingSlide)
	{
		if (transitionVersion != _viewTransitionVersion)
		{
			return;
		}
		StopViewFrameTransition();
		StopViewTransition(incoming, incomingSlide);
		StopViewTransition(outgoing, outgoingSlide);
		incoming.Opacity = 1.0;
		incomingSlide.Y = 0.0;
		incoming.Visibility = Visibility.Visible;
		incoming.IsHitTestVisible = true;
		outgoing.Opacity = 1.0;
		outgoingSlide.Y = 0.0;
		outgoing.Visibility = Visibility.Hidden;
		outgoing.IsHitTestVisible = false;
		incoming.CacheMode = null;
		outgoing.CacheMode = null;
		System.Windows.Controls.Panel.SetZIndex(incoming, 0);
		System.Windows.Controls.Panel.SetZIndex(outgoing, 0);
		_viewTransitionActive = false;
		RelaxViewLowLatency();
		// Resume decorative tile work only after the compositor has delivered the final view frame.
		if (!showAllApps && base.IsVisible)
		{
			_liveTiles?.Start();
			StartLiveFlips();
		}
		double elapsedMs = (double)(Stopwatch.GetTimestamp() - _viewTransitionStartTimestamp) * 1000.0 / Stopwatch.Frequency;
		ViewTransitionCompletedForQa?.Invoke(transitionVersion, showAllApps, elapsedMs);
	}

	private void InitBottomScroll()
	{
		//IL_0002: Unknown result type (might be due to invalid IL or missing references)
		//IL_0007: Unknown result type (might be due to invalid IL or missing references)
		//IL_001e: Expected O, but got Unknown
		_scrollHideTimer = new DispatcherTimer
		{
			Interval = TimeSpan.FromMilliseconds(1200L)
		};
		_scrollHideTimer.Tick += delegate
		{
			_scrollHideTimer.Stop();
			FadeScrollBar(0.0, 300.0);
		};
		StartScroller.ScrollChanged += OnAnyScrollChanged;
		AppsScroller.ScrollChanged += OnAnyScrollChanged;
		RootGrid.MouseMove += delegate(object _, System.Windows.Input.MouseEventArgs e)
		{
			//IL_0010: Unknown result type (might be due to invalid IL or missing references)
			PokeScrollBar();
			UpdateViewTogglePeek(e.GetPosition(RootGrid));
		};
		BottomScroll.MouseEnter += delegate
		{
			FadeScrollBar(1.0, 120.0);
			DispatcherTimer? scrollHideTimer = _scrollHideTimer;
			if (scrollHideTimer != null)
			{
				scrollHideTimer.Stop();
			}
		};
		BottomScroll.MouseLeave += delegate
		{
			PokeScrollBar();
		};
		ZoomOutBtn.MouseEnter += delegate { FadeScrollBar(1.0, 120.0); _scrollHideTimer?.Stop(); };
		ZoomOutBtn.MouseLeave += delegate { PokeScrollBar(); };
		ZoomOutBtn.GotKeyboardFocus += delegate { PokeScrollBar(); };
		ZoomOutBtn.LostKeyboardFocus += delegate { PokeScrollBar(); };
		BottomScroll.PreviewMouseDown += delegate
		{
			_scrollGuardUntil = Environment.TickCount + 700;
		};
		ZoomOutBtn.PreviewMouseDown += delegate
		{
			_scrollGuardUntil = Environment.TickCount + 700;
		};
		StyleBottomScroll();
	}

	private void StyleBottomScroll()
	{
		try
		{
			BottomScroll.Orientation = System.Windows.Controls.Orientation.Horizontal;
			// Bottom-anchored: the bar grows UPWARD. The Apps down-arrow (ViewToggle) sits ~14px above, so 15px (was 12)
			// still clears it with headroom while giving an easier grab target; do NOT push much higher or the arrow's
			// fixed 56px row clips its circular background.
			BottomScroll.Height = 15.0;
			BottomScroll.ClearValue(System.Windows.Controls.Control.TemplateProperty);
			BottomScroll.SetResourceReference(FrameworkElement.StyleProperty, "Metro81.ScrollBarDark");   // the Start board is a dark/accent surface - keep the dark rail (the light pattern rail is for white panes)
		}
		catch (Exception ex)
		{
			Logger.Log("StyleBottomScroll failed: " + ex.Message);
		}
	}

	private void OnAnyScrollChanged(object sender, ScrollChangedEventArgs e)
	{
		if (sender == _activeScroller)
		{
			UpdateBottomScroll();
			if (e.HorizontalChange != 0.0 || e.ExtentWidthChange != 0.0)
			{
				PokeScrollBar();
			}
			if (sender == StartScroller)
			{
				BgParallax.X = ((StartScroller.ScrollableWidth > 1.0) ? (0.0 - Math.Min(StartScroller.HorizontalOffset * 0.15, 160.0)) : 0.0);
			}
		}
	}

	private void BindBottomScroll(ScrollViewer sv)
	{
		_activeScroller = sv;
		UpdateBottomScroll();
	}

	private void UpdateBottomScroll()
	{
		ScrollViewer sv = _activeScroller;
		if (sv != null && BottomScroll != null)
		{
			if (_personalizeStandalone)
			{
				BottomScroll.Visibility = Visibility.Collapsed;
				return;
			}
			BottomScroll.Maximum = sv.ScrollableWidth;
			BottomScroll.ViewportSize = sv.ViewportWidth;
			BottomScroll.Value = sv.HorizontalOffset;
			BottomScroll.SmallChange = 160.0;
			BottomScroll.LargeChange = Math.Max(160.0, sv.ViewportWidth * 0.9);
			BottomScroll.Visibility = ((!(sv.ScrollableWidth > 1.0)) ? Visibility.Collapsed : Visibility.Visible);
			RefreshZoomButton();
		}
	}

	private void OnBottomScroll(object sender, System.Windows.Controls.Primitives.ScrollEventArgs e)
	{
		if (_activeScroller != null)
		{
			SmoothScroll.Stop(_activeScroller);
			_activeScroller.ScrollToHorizontalOffset(e.NewValue);
			PokeScrollBar();
		}
	}

	private void PokeScrollBar()
	{
		if (BottomScroll == null || _activeScroller == null)
		{
			return;
		}
		if (_personalizeStandalone)
		{
			BottomScroll.Visibility = Visibility.Collapsed;
		}
		else if (!(_activeScroller.ScrollableWidth <= 1.0))
		{
			if (BottomScroll.Visibility != Visibility.Visible)
			{
				BottomScroll.Visibility = Visibility.Visible;
			}
			FadeScrollBar(1.0, 120.0);
			DispatcherTimer? scrollHideTimer = _scrollHideTimer;
			if (scrollHideTimer != null)
			{
				scrollHideTimer.Stop();
			}
			DispatcherTimer? scrollHideTimer2 = _scrollHideTimer;
			if (scrollHideTimer2 != null && !BottomScroll.IsMouseOver && !ZoomOutBtn.IsMouseOver && !ZoomOutBtn.IsKeyboardFocusWithin)
			{
				scrollHideTimer2.Start();
			}
		}
	}

	private void FadeScrollBar(double to, double ms)
	{
		BottomScroll?.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(to, new Duration(TimeSpan.FromMilliseconds(ms)))
		{
			EasingFunction = new CubicEase
			{
				EasingMode = EasingMode.EaseOut
			}
		});
	}

	private void OnZoomOutButton(object sender, RoutedEventArgs e)
	{
		if (_showingAllApps)
		{
			if (AppsZoomOverlay.Visibility == Visibility.Visible)
			{
				HideAppsZoom();
			}
			else
			{
				ShowAppsZoom();
			}
		}
		else if (ZoomOverlay.Visibility == Visibility.Visible)
		{
			HideZoom();
		}
		else
		{
			ShowZoom();
		}
	}

	private void RefreshZoomButton()
	{
		if (ZoomOutBtn != null && ZoomVBar != null)
		{
			ZoomVBar.Visibility = ((!_semanticZoomed) ? Visibility.Collapsed : Visibility.Visible);
			ZoomOutBtn.ToolTip = (_semanticZoomed ? "Zoom in" : "Zoom out");
			ScrollViewer sv = _activeScroller;
			bool scrollable = sv != null && sv.ScrollableWidth > 1.0;
			ZoomOutBtn.Visibility = ((_personalizeStandalone || !(_semanticZoomed | scrollable)) ? Visibility.Collapsed : Visibility.Visible);
		}
	}

	private void ViewToggleArrowUp(bool up)
	{
		ViewToggle.Direction = up ? MetroArrowDirection81.Up : MetroArrowDirection81.Down;
		ViewToggle.ToolTip = up ? "Return to Start" : "Show all apps";
		System.Windows.Automation.AutomationProperties.SetName(ViewToggle, up ? "Return to Start" : "Show all apps");
	}

	private void UpdateViewTogglePeek(System.Windows.Point p)
	{
		//IL_00fd: Unknown result type (might be due to invalid IL or missing references)
		//IL_0102: Unknown result type (might be due to invalid IL or missing references)
		//IL_0119: Expected O, but got Unknown
		//IL_0083: Unknown result type (might be due to invalid IL or missing references)
		//IL_0088: Unknown result type (might be due to invalid IL or missing references)
		//IL_009f: Expected O, but got Unknown
		if (_personalizeStandalone)
		{
			return;
		}
		if ((p.X <= 360.0 && p.Y >= base.ActualHeight - 260.0) || ViewToggle.IsMouseOver)
		{
			DispatcherTimer? vtHideTimer = _vtHideTimer;
			if (vtHideTimer != null)
			{
				vtHideTimer.Stop();
			}
			_vtHideTimer = null;
			if (_viewToggleShown || _vtRevealTimer != null)
			{
				return;
			}
			_vtRevealTimer = new DispatcherTimer
			{
				// Intent gate before the down-arrow fades in. Was 150ms (felt sticky); 85ms keeps a light anti-flicker
				// dwell while the whole reveal lands inside the §9 70-100ms budget. (Hide gate below stays 150ms.)
				Interval = TimeSpan.FromMilliseconds(85L)
			};
			_vtRevealTimer.Tick += delegate
			{
				DispatcherTimer? vtRevealTimer2 = _vtRevealTimer;
				if (vtRevealTimer2 != null)
				{
					vtRevealTimer2.Stop();
				}
				_vtRevealTimer = null;
				RevealViewToggle();
			};
			_vtRevealTimer.Start();
			return;
		}
		DispatcherTimer? vtRevealTimer = _vtRevealTimer;
		if (vtRevealTimer != null)
		{
			vtRevealTimer.Stop();
		}
		_vtRevealTimer = null;
		if (!_viewToggleShown || _vtHideTimer != null)
		{
			return;
		}
		_vtHideTimer = new DispatcherTimer
		{
			Interval = TimeSpan.FromMilliseconds(150L)
		};
		_vtHideTimer.Tick += delegate
		{
			DispatcherTimer? vtHideTimer2 = _vtHideTimer;
			if (vtHideTimer2 != null)
			{
				vtHideTimer2.Stop();
			}
			_vtHideTimer = null;
			HideViewToggle();
		};
		_vtHideTimer.Start();
	}

	private void RevealViewToggle()
	{
		if (!_viewToggleShown)
		{
			_viewToggleShown = true;
			ViewToggle.IsHitTestVisible = true;
			Duration dur = Motion.Dur(Motion.Cat.Hover);   // 90ms reveal (was Micro 130) -> inside the §9 70-100ms band
			IEasingFunction ease = Motion.Ease(Motion.Cat.Hover);
			ViewToggle.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(1.0, dur)
			{
				EasingFunction = ease
			});
			ViewToggleSlide.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(0.0, dur)
			{
				EasingFunction = ease
			});
		}
	}

	private void HideViewToggle(bool instant = false)
	{
		DispatcherTimer? vtRevealTimer = _vtRevealTimer;
		if (vtRevealTimer != null)
		{
			vtRevealTimer.Stop();
		}
		_vtRevealTimer = null;
		DispatcherTimer? vtHideTimer = _vtHideTimer;
		if (vtHideTimer != null)
		{
			vtHideTimer.Stop();
		}
		_vtHideTimer = null;
		_viewToggleShown = false;
		ViewToggle.IsHitTestVisible = false;
		if (instant)
		{
			ViewToggle.BeginAnimation(UIElement.OpacityProperty, null);
			ViewToggle.Opacity = 0.0;
			ViewToggleSlide.BeginAnimation(TranslateTransform.YProperty, null);
			ViewToggleSlide.Y = 10.0;
		}
		else
		{
			Duration dur = Motion.Dur(Motion.Cat.Micro);
			IEasingFunction ease = Motion.Ease(Motion.Cat.Micro);
			ViewToggle.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0.0, dur)
			{
				EasingFunction = ease
			});
			ViewToggleSlide.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(10.0, dur)
			{
				EasingFunction = ease
			});
		}
	}

	private void OnViewToggle(object sender, RoutedEventArgs e)
	{
		SwitchView(!_showingAllApps);
	}

	private void FocusFirstApp()
	{
		((DispatcherObject)this).Dispatcher.BeginInvoke((Delegate)(Action)delegate
		{
			foreach (object current in AppsHost.Children)
			{
				if (current is System.Windows.Controls.Button button)
				{
					button.Focus();
					break;
				}
			}
		}, (DispatcherPriority)5, Array.Empty<object>());
	}

	private void OnSearchButtonClick(object sender, RoutedEventArgs e)
	{
		SwitchView(showAllApps: true);
		SearchBox.Focus();
	}

	private void OnQuickExplorer(object sender, RoutedEventArgs e)
	{
		try
		{
			if (!FileBrowser.TryShow())
			{
				// Off the UI thread so the Start dismiss animation never hitches on ShellExecuteEx.
				ShellLaunch.Run(delegate
				{
					Process.Start(new ProcessStartInfo("explorer.exe")
					{
						UseShellExecute = true
					});
				});
			}
			HideStart();
		}
		catch (Exception ex)
		{
			Logger.Log("Explorer launch failed: " + ex.Message);
		}
	}

	private void OnQuickSettings(object sender, RoutedEventArgs e)
	{
		try
		{
			Process.Start(new ProcessStartInfo("ms-settings:")
			{
				UseShellExecute = true
			});
			HideStart();
		}
		catch (Exception ex)
		{
			Logger.Log("Settings launch failed: " + ex.Message);
		}
	}

	private void OnUserTileClick(object sender, RoutedEventArgs e)
	{
		OpenHeaderMenu(UserTile, ("Lock", delegate
		{
			HideStart();
			PowerActions.Lock();
		}), ("Sign out", PowerActions.SignOut), ("Change account settings", delegate
		{
			HideStart();
			PowerActions.AccountSettings();
		}));
	}

	private void OnPowerClick(object sender, RoutedEventArgs e)
	{
		OpenHeaderMenu(PowerButton, ("Sleep", delegate
		{
			HideStart();
			PowerActions.Sleep();
		}), ("Shut down", delegate
		{
			HideStart();
			PowerActions.ShutDown();
		}), ("Restart", delegate
		{
			HideStart();
			PowerActions.Restart();
		}));
	}

	private void OnHeaderSearchClick(object sender, RoutedEventArgs e)
	{
		SearchRequested?.Invoke();
	}

	private void OpenHeaderMenu(UIElement target, params (string Header, Action Action)[] items)
	{
		System.Windows.Controls.ContextMenu menu = new System.Windows.Controls.ContextMenu
		{
			Style = (Style)FindResource("Metro81.ContextMenu")
		};
		for (int i = 0; i < items.Length; i++)
		{
			var (header, action) = items[i];
			var item = TaskbarContextMenu.Leaf(header, action);
			item.SetResourceReference(FrameworkElement.StyleProperty, "Metro81.MenuItem");
			menu.Items.Add(item);
		}
		_contextMenuOpen = true;
		menu.Closed += delegate
		{
			OnAnyContextMenuClosed(menu, new RoutedEventArgs());
		};
		menu.PlacementTarget = target;
		menu.Placement = PlacementMode.Bottom;
		menu.IsOpen = true;
	}

	private void SetTransientCache(bool on)
	{
		try
		{
			if (on)
			{
				if (!(RootGrid.CacheMode is BitmapCache))
				{
					RootGrid.CacheMode = DpiCache();
				}
			}
			else
			{
				RootGrid.CacheMode = null;
			}
		}
		catch
		{
		}
	}

	private BitmapCache DpiCache()
	{
		double scale = 1.0;
		try
		{
			scale = VisualTreeHelper.GetDpi(this).DpiScaleX;
		}
		catch
		{
		}
		return new BitmapCache
		{
			RenderAtScale = ((scale > 0.0) ? scale : 1.0)
		};
	}

	private void AnimateEntrance()
	{
		//IL_00aa: Unknown result type (might be due to invalid IL or missing references)
		if (Motion.Mode == MotionMode.Off)
		{
			RootGrid.BeginAnimation(UIElement.OpacityProperty, null);
			RootGrid.Opacity = 1.0;
			RootGrid.RenderTransform = null;
			return;
		}
		SetTransientCache(on: true);
		double from = ((Motion.Mode == MotionMode.Reduced) ? 0.98 : 0.88);
		double opacityFrom = ((Motion.Mode == MotionMode.Reduced) ? 0.80 : 0.45);
		// Authentic 8.1 "rush in from 0.88, then settle": a confident zoom on a strong ExponentialEase-Out over 200ms
		// (scaled by Motion.Factor => Fast 160ms, Reduced 110ms). The fade is coupled to the same curve + full length,
		// so — because Exponent=6 is heavily front-loaded — the surface reads ~opaque by ~100ms yet lands fully opaque
		// exactly as the zoom settles (keeps the "brightness and zoom settle together" behaviour, now more pronounced).
		int ms = Math.Max(1, (int)(200.0 * Motion.Factor));
		ScaleTransform scale = new ScaleTransform(from, from);
		RootGrid.RenderTransform = scale;
		RootGrid.RenderTransformOrigin = new System.Windows.Point(0.5, 0.5);
		IEasingFunction ease = Motion.StartOpenEase;   // shared frozen ExponentialEase-Out — no per-open Freezable alloc
		Duration dur = new Duration(TimeSpan.FromMilliseconds(ms));
		DoubleAnimation ax = new DoubleAnimation(from, 1.0, dur)
		{
			EasingFunction = ease
		};
		ax.Completed += delegate
		{
			if (!_closing)
			{
				SetTransientCache(on: false);
			}
		};
		scale.BeginAnimation(ScaleTransform.ScaleXProperty, ax);
		scale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(from, 1.0, dur)
		{
			EasingFunction = ease
		});
		// Fade shares the scale's ease-out curve AND full length, so brightness and zoom settle together (was a linear
		// fade over 0.7*ms — the light landed before the zoom, reading slightly "off" on the most-seen surface).
		RootGrid.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(opacityFrom, 1.0, dur)
		{
			EasingFunction = ease
		});
	}

	public void ShowPeek()
	{
		//IL_010d: Unknown result type (might be due to invalid IL or missing references)
		if (!base.IsVisible && !_peekActive)
		{
			_peekActive = true;
			_personalizeStandalone = false;
			SearchBox.Text = string.Empty;
			HideZoom();
			HideAppsZoom();
			ResetGroupRenames();
			if (AnySelected())
			{
				ClearSelection();
			}
			SwitchView(showAllApps: false, animate: false);
			SetStartChromeVisible(v: true);
			base.WindowState = WindowState.Normal;
			base.Left = 0.0;
			base.Top = 0.0;
			base.Width = SystemParameters.PrimaryScreenWidth;
			base.Height = SystemParameters.PrimaryScreenHeight;
			RootGrid.IsHitTestVisible = false;
			base.Opacity = 0.0;
			ScaleTransform scale = new ScaleTransform(0.9, 0.9);
			RootGrid.RenderTransform = scale;
			RootGrid.RenderTransformOrigin = new System.Windows.Point(0.5, 0.5);
			_showGuardUntil = Environment.TickCount + 500;
			base.ShowActivated = false;
			Show();
			PositionOnActiveScreen();
			base.Topmost = true;
			_liveTiles?.Start();
			StartLiveFlips();
			((DispatcherObject)this).Dispatcher.BeginInvoke((Delegate)new Action(RefreshDesktopTile), (DispatcherPriority)4, Array.Empty<object>());
			SetTransientCache(on: true);
			Duration dur = new Duration(TimeSpan.FromMilliseconds(Math.Max(1.0, 180.0 * Motion.Factor)));   // scale with Fast/Reduced
			QuinticEase ease = new QuinticEase
			{
				EasingMode = EasingMode.EaseOut
			};
			scale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.9, 0.94, dur)
			{
				EasingFunction = ease
			});
			scale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.9, 0.94, dur)
			{
				EasingFunction = ease
			});
			BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0.0, 0.72, dur));
		}
	}

	public void CommitPeek()
	{
		//IL_006d: Unknown result type (might be due to invalid IL or missing references)
		if (!_peekActive)
		{
			return;
		}
		_peekActive = false;
		ScaleTransform scale = (RootGrid.RenderTransform as ScaleTransform) ?? new ScaleTransform(0.94, 0.94);
		RootGrid.RenderTransform = scale;
		RootGrid.RenderTransformOrigin = new System.Windows.Point(0.5, 0.5);
		RootGrid.IsHitTestVisible = true;
		base.ShowActivated = true;
		WindowUtil.ForceForeground(this);
		base.Topmost = true;
		SetTransientCache(on: true);
		Duration dur = new Duration(TimeSpan.FromMilliseconds(Math.Max(1.0, 150.0 * Motion.Factor)));   // scale with Fast/Reduced
		QuinticEase ease = new QuinticEase
		{
			EasingMode = EasingMode.EaseOut
		};
		DoubleAnimation ax = new DoubleAnimation(1.0, dur)
		{
			EasingFunction = ease
		};
		ax.Completed += delegate
		{
			if (!_closing)
			{
				SetTransientCache(on: false);
			}
		};
		scale.BeginAnimation(ScaleTransform.ScaleXProperty, ax);
		scale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(1.0, dur)
		{
			EasingFunction = ease
		});
		BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(1.0, dur));
		((DispatcherObject)this).Dispatcher.BeginInvoke((Delegate)(Action)delegate
		{
			if (!_showingAllApps)
			{
				DependencyObject val = GroupsHost.ItemContainerGenerator.ContainerFromIndex(0);
				if (val != null)
				{
					FindFirstButton(val)?.Focus();
				}
			}
		}, (DispatcherPriority)6, Array.Empty<object>());
	}

	public void CancelPeek()
	{
		if (_peekActive)
		{
			_peekActive = false;
			SetTransientCache(on: true);
			ScaleTransform scale = (RootGrid.RenderTransform as ScaleTransform) ?? new ScaleTransform(0.94, 0.94);
			Duration dur = new Duration(TimeSpan.FromMilliseconds(Math.Max(1.0, 140.0 * Motion.Factor)));   // scale with Fast/Reduced
			CubicEase ease = new CubicEase
			{
				EasingMode = EasingMode.EaseIn
			};
			scale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.9, dur)
			{
				EasingFunction = ease
			});
			scale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.9, dur)
			{
				EasingFunction = ease
			});
			DoubleAnimation fade = new DoubleAnimation(0.0, dur);
			fade.Completed += delegate
			{
				_liveTiles?.Stop();
				StopLiveFlips();
				Hide();
				BeginAnimation(UIElement.OpacityProperty, null);
				base.Opacity = 1.0;
				scale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
				scale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
				RootGrid.RenderTransform = null;
				RootGrid.IsHitTestVisible = true;
				base.ShowActivated = true;
				SetTransientCache(on: false);
			};
			BeginAnimation(UIElement.OpacityProperty, fade);
		}
	}

	private void Launch(AppEntry entry, bool asAdmin = false, bool keepOpen = false)
	{
		if (_groups.SelectMany(g => g.Tiles).Any(t => t.Live == LiveKind.Agenda && ReferenceEquals(t.Entry, entry)) && !GoogleAuth.HasCalendarAccess)
		{
			HideStart(animate: true);
			GoogleCalendarConnectWindow.ShowFor();
			return;
		}
		if (entry.LaunchPath == "win81:news")
		{
			HideStart(animate: true);
			(System.Windows.Application.Current as App)?.OpenNews();
			return;
		}
		if (entry.LaunchPath == "win81:desktop")
		{
			HideStart(animate: true);
			return;
		}
		if (entry.LaunchPath == "win81:pcsettings")
		{
			HideStart();
			PcSettingsRequested?.Invoke();
			return;
		}
		// DEEP PIN: a tile whose target is a launcher:// action routes through the unified Action Router (workspaces,
		// display, settings, ...) — same backend as Search/URI, no duplicate execution path.
		if (entry.LaunchPath != null && entry.LaunchPath.StartsWith(ActionRouter.Scheme, StringComparison.OrdinalIgnoreCase))
		{
			if (!keepOpen)
			{
				HideStart(animate: true);
			}
			ActionRouter.Invoke(entry.LaunchPath);
			return;
		}
		if (!keepOpen)
		{
			// Grant the launched app foreground rights WHILE the Start window is still the foreground process. After
			// HideStart() the launcher is no longer foreground, so a later AllowSetForegroundWindow (inside
			// ShellLaunch.Run) fails and the app opens BEHIND — leaving the user stuck looking at Start instead of the
			// app they just clicked. Granting here, before hiding, lets the app raise itself to the foreground.
			ShellLaunch.AllowForeground();
			HideStart();
		}
		string launchPath = entry.LaunchPath;
		string name = entry.Name;
		string appId = entry.AppId;
		ShellLaunch.Run(delegate
		{
			if (AppLauncher.TryLaunch(launchPath, null, appId, asAdmin, out string error))
			{
				UsageStore.RecordLaunch(launchPath, DateTime.UtcNow.Ticks);
				return;
			}
			if (asAdmin && error != null && error.Contains("canceled", StringComparison.OrdinalIgnoreCase))
			{
				return;
			}
			((DispatcherObject)this).Dispatcher.BeginInvoke((Delegate)(Func<MessageBoxResult>)(() => System.Windows.MessageBox.Show(this, "Couldn't launch:\n" + error, name)), Array.Empty<object>());
		});
	}

	private static AppEntry MakeDesktopAppEntry()
	{
		// The authentic Win8.1 Desktop icon is a full-bleed purple (#4214B5) tile with a white monitor glyph,
		// so it carries its OWN background. Mark it as an override so the all-apps list/search draw the WHOLE
		// icon filling the 32px slot (not a 24px icon centred on a separate accent square). OverrideBrush =
		// the same purple so any edge blends seamlessly. This entry is list/search-only — the Desktop TILE is a
		// separate LiveTiles.Create(Desktop) entry (wallpaper preview), so this does not affect the tile.
		SolidColorBrush purple = new SolidColorBrush(System.Windows.Media.Color.FromRgb(66, 20, 181));
		((Freezable)purple).Freeze();
		System.Windows.Media.ImageSource deskIcon = LiveTiles.DesktopSmallIcon();
		return new AppEntry
		{
			Name = "Desktop",
			LaunchPath = "win81:desktop",
			TileBrush = purple,
			Icon = deskIcon ?? GlyphImage(59380, 22.0),
			IsOverrideIcon = (deskIcon != null),
			OverrideBrush = purple
		};
	}

	private static AppEntry MakePcSettingsAppEntry()
	{
		System.Windows.Media.Color accent;
		try
		{
			accent = StartAccent.Color();
		}
		catch
		{
			accent = System.Windows.Media.Color.FromRgb(81, 43, 212);
		}
		SolidColorBrush brush = new SolidColorBrush(accent);
		((Freezable)brush).Freeze();
		return new AppEntry
		{
			Name = "PC settings",
			LaunchPath = "win81:pcsettings",
			TileBrush = brush,
			Icon = PcSettingsWindow.BuildGearGlyph(24)
		};
	}

	private static ImageSource GlyphImage(int codepoint, double size)
	{
		//IL_0051: Unknown result type (might be due to invalid IL or missing references)
		//IL_005e: Unknown result type (might be due to invalid IL or missing references)
		//IL_0063: Unknown result type (might be due to invalid IL or missing references)
		//IL_007a: Unknown result type (might be due to invalid IL or missing references)
		//IL_007f: Unknown result type (might be due to invalid IL or missing references)
		//IL_00ab: Unknown result type (might be due to invalid IL or missing references)
		TextBlock tb = new TextBlock
		{
			Text = ((char)codepoint).ToString(),
			FontFamily = new System.Windows.Media.FontFamily("Segoe MDL2 Assets"),
			FontSize = size,
			Foreground = System.Windows.Media.Brushes.White
		};
		tb.Measure(new System.Windows.Size(double.PositiveInfinity, double.PositiveInfinity));
		System.Windows.Size desiredSize = tb.DesiredSize;
		int w = Math.Max(1, (int)Math.Ceiling(desiredSize.Width));
		desiredSize = tb.DesiredSize;
		int h = Math.Max(1, (int)Math.Ceiling(desiredSize.Height));
		tb.Arrange(new Rect(0.0, 0.0, (double)w, (double)h));
		RenderTargetBitmap rtb = new RenderTargetBitmap(w, h, 96.0, 96.0, PixelFormats.Pbgra32);
		rtb.Render(tb);
		((Freezable)rtb).Freeze();
		return rtb;
	}

	private void OnAppMiddleDown(object sender, MouseButtonEventArgs e)
	{
		if (e.ChangedButton == MouseButton.Middle && (sender as FrameworkElement)?.DataContext is AppEntry entry)
		{
			Launch(entry, asAdmin: false, keepOpen: true);
			e.Handled = true;
		}
	}

	private void OnRunAsAdminTile(object sender, RoutedEventArgs e)
	{
		if ((sender as FrameworkElement)?.DataContext is TileVm tile)
		{
			Launch(tile.Entry, asAdmin: true);
		}
	}

	private void OnTileRecentOpened(object sender, RoutedEventArgs e)
	{
		if (sender is System.Windows.Controls.MenuItem { DataContext: TileVm tile } menu)
		{
			PopulateRecent(menu, tile.Entry.AppId);
		}
	}

	private void PopulateRecent(System.Windows.Controls.MenuItem menu, string? appId)
	{
		menu.Tag = appId ?? "";
		if (JumpListApi.TryGetRecentCached(appId, 12, out List<JumpList.Item> cached))
		{
			Render(cached);
			return;
		}
		menu.Items.Clear();
		menu.Items.Add(new System.Windows.Controls.MenuItem { Header = "Loading recent files…", IsEnabled = false });
		JumpListApi.GetRecentAsync(appId, 12, delegate(List<JumpList.Item> items)
		{
			Dispatcher.BeginInvoke((Action)delegate
			{
				if (string.Equals(menu.Tag as string, appId ?? "", StringComparison.Ordinal))
				{
					Render(items);
				}
			}, DispatcherPriority.Background);
		});

		void Render(List<JumpList.Item> items)
		{
			menu.Items.Clear();
			if (items.Count == 0)
			{
				menu.Items.Add(new System.Windows.Controls.MenuItem { Header = "No recent files", IsEnabled = false });
				return;
			}
			foreach (JumpList.Item item in items)
			{
				string path = item.Path;
				System.Windows.Controls.MenuItem recent = new System.Windows.Controls.MenuItem { Header = item.Name };
				recent.Click += delegate { TaskbarContextMenu.QueueCommand(delegate { OpenRecent(path); }); };
				menu.Items.Add(recent);
			}
		}
	}

	private void OpenRecent(string path)
	{
		HideStart();
		ShellLaunch.Run(delegate
		{
			try
			{
				Process.Start(new ProcessStartInfo(path)
				{
					UseShellExecute = true
				});
			}
			catch (Exception ex)
			{
				Logger.Log("Open recent failed: " + ex.Message);
			}
		});
	}

	private void OnTileClick(object sender, RoutedEventArgs e)
	{
		//IL_0029: Unknown result type (might be due to invalid IL or missing references)
		if ((sender as FrameworkElement)?.DataContext is TileVm tile)
		{
			if (((Enum)Keyboard.Modifiers).HasFlag((Enum)(object)(ModifierKeys)2))
			{
				tile.IsSelected = !tile.IsSelected;
				RefreshTileAppBar();
			}
			else if (AnySelected())
			{
				ClearSelection();
				HideTileAppBar();
			}
			else if (tile.IsDesktop)
			{
				HideStart(animate: true);
			}
			else if (tile.IsFolder)
			{
				OpenFolderFlyout(tile, sender as FrameworkElement);
			}
			else
			{
				Launch(tile.Entry);
			}
		}
	}

	private void OpenFolderFlyout(TileVm folder, FrameworkElement? anchor)
	{
		if (anchor != null && folder.Members.Count != 0)
		{
			Popup popup = new Popup
			{
				PlacementTarget = anchor,
				Placement = PlacementMode.Bottom,
				StaysOpen = false,
				AllowsTransparency = true,
				PopupAnimation = PopupAnimation.Fade
			};
			popup.Child = BuildFolderFlyout(folder, delegate
			{
				popup.IsOpen = false;
			});
			popup.IsOpen = true;
		}
	}

	private FrameworkElement BuildFolderFlyout(TileVm folder, Action onClose)
	{
		StackPanel panel = new StackPanel
		{
			MinWidth = 220.0
		};
		panel.Children.Add(new TextBlock
		{
			Text = (string.IsNullOrWhiteSpace(folder.Entry.Name) ? "Apps" : folder.Entry.Name),
			Foreground = System.Windows.Media.Brushes.White,
			FontFamily = new System.Windows.Media.FontFamily("Segoe UI"),
			FontSize = 15.0,
			FontWeight = FontWeights.SemiBold,
			Margin = new Thickness(4.0, 0.0, 0.0, 10.0)
		});
		foreach (AppEntry m in folder.Members)
		{
			StackPanel row = new StackPanel
			{
				Orientation = System.Windows.Controls.Orientation.Horizontal
			};
			System.Windows.Controls.Image memberImg = new System.Windows.Controls.Image
			{
				Source = m.Icon,
				Width = 24.0,
				Height = 24.0,
				Margin = new Thickness(0.0, 0.0, 12.0, 0.0),
				VerticalAlignment = VerticalAlignment.Center
			};
			RenderOptions.SetBitmapScalingMode(memberImg, BitmapScalingMode.HighQuality);
			row.Children.Add(memberImg);
			row.Children.Add(new TextBlock
			{
				Text = m.Name,
				Foreground = System.Windows.Media.Brushes.White,
				VerticalAlignment = VerticalAlignment.Center,
				FontFamily = new System.Windows.Media.FontFamily("Segoe UI"),
				FontSize = 13.0
			});
			System.Windows.Controls.Button btn = new System.Windows.Controls.Button
			{
				Content = row,
				Background = System.Windows.Media.Brushes.Transparent,
				BorderThickness = new Thickness(0.0),
				Cursor = System.Windows.Input.Cursors.Hand,
				FocusVisualStyle = null,
				Padding = new Thickness(8.0, 5.0, 12.0, 5.0),
				HorizontalContentAlignment = System.Windows.HorizontalAlignment.Left,
				Template = FolderRowTemplate()
			};
			AppEntry member = m;
			btn.Click += delegate
			{
				onClose();
				Launch(member);
			};
			btn.MouseRightButtonUp += delegate(object _, MouseButtonEventArgs ev)
			{
				onClose();
				RemoveFromFolder(folder, member);
				ev.Handled = true;
			};
			panel.Children.Add(btn);
		}
		panel.Children.Add(new Border
		{
			Height = 1.0,
			Background = new SolidColorBrush(System.Windows.Media.Color.FromArgb(51, byte.MaxValue, byte.MaxValue, byte.MaxValue)),
			Margin = new Thickness(0.0, 8.0, 0.0, 6.0)
		});
		StackPanel ugRow = new StackPanel
		{
			Orientation = System.Windows.Controls.Orientation.Horizontal
		};
		ugRow.Children.Add(new TextBlock
		{
			Text = '\ue8b7'.ToString(),
			FontFamily = new System.Windows.Media.FontFamily("Segoe MDL2 Assets"),
			FontSize = 15.0,
			Foreground = System.Windows.Media.Brushes.White,
			Margin = new Thickness(2.0, 0.0, 14.0, 0.0),
			VerticalAlignment = VerticalAlignment.Center
		});
		ugRow.Children.Add(new TextBlock
		{
			Text = "Ungroup",
			Foreground = System.Windows.Media.Brushes.White,
			VerticalAlignment = VerticalAlignment.Center,
			FontFamily = new System.Windows.Media.FontFamily("Segoe UI"),
			FontSize = 13.0
		});
		System.Windows.Controls.Button ungroupBtn = new System.Windows.Controls.Button
		{
			Content = ugRow,
			Background = System.Windows.Media.Brushes.Transparent,
			BorderThickness = new Thickness(0.0),
			Cursor = System.Windows.Input.Cursors.Hand,
			FocusVisualStyle = null,
			HorizontalContentAlignment = System.Windows.HorizontalAlignment.Left,
			Template = FolderRowTemplate()
		};
		ungroupBtn.Click += delegate
		{
			onClose();
			Ungroup(folder);
		};
		panel.Children.Add(ungroupBtn);
		return new Border
		{
			Background = (ShellSkin.GlassOn ? ShellSkin.PanelBg() : new SolidColorBrush(System.Windows.Media.Color.FromArgb(242, 31, 31, 31))),
			BorderBrush = new SolidColorBrush(System.Windows.Media.Color.FromArgb(64, byte.MaxValue, byte.MaxValue, byte.MaxValue)),
			BorderThickness = new Thickness(1.0),
			Padding = new Thickness(14.0, 12.0, 14.0, 12.0),
			Child = panel
		};
	}

	private static ControlTemplate FolderRowTemplate()
	{
		ControlTemplate t = new ControlTemplate(typeof(System.Windows.Controls.Button));
		FrameworkElementFactory bd = new FrameworkElementFactory(typeof(Border))
		{
			Name = "bd"
		};
		bd.SetValue(Border.BackgroundProperty, System.Windows.Media.Brushes.Transparent);
		bd.SetValue(Border.PaddingProperty, new Thickness(8.0, 5.0, 12.0, 5.0));
		FrameworkElementFactory cp = new FrameworkElementFactory(typeof(ContentPresenter));
		cp.SetValue(FrameworkElement.HorizontalAlignmentProperty, System.Windows.HorizontalAlignment.Left);
		bd.AppendChild(cp);
		t.VisualTree = bd;
		Trigger hover = new Trigger
		{
			Property = UIElement.IsMouseOverProperty,
			Value = true
		};
		hover.Setters.Add(new Setter(Border.BackgroundProperty, new SolidColorBrush(System.Windows.Media.Color.FromArgb(34, byte.MaxValue, byte.MaxValue, byte.MaxValue)), "bd"));
		t.Triggers.Add(hover);
		return t;
	}

	private IEnumerable<TileVm> SelectedTiles()
	{
		return from t in _groups.SelectMany((GroupVm g) => g.Tiles)
			where t.IsSelected
			select t;
	}

	private bool AnySelected()
	{
		return SelectedTiles().Any();
	}

	private void UpdateSelectionBar()
	{
		SelectionBar.Visibility = Visibility.Collapsed;
	}

	private void ClearSelection()
	{
		foreach (TileVm t in SelectedTiles().ToList())
		{
			t.IsSelected = false;
		}
		UpdateSelectionBar();
	}

	private void OnClearSelection(object sender, RoutedEventArgs e)
	{
		ClearSelection();
	}

	private void OnUnpinSelected(object sender, RoutedEventArgs e)
	{
		foreach (TileVm t in SelectedTiles().ToList())
		{
			_groups.FirstOrDefault((GroupVm g) => g.Tiles.Contains(t))?.Tiles.Remove(t);
		}
		foreach (GroupVm empty in _groups.Where((GroupVm g) => g.Tiles.Count == 0).ToList())
		{
			_groups.Remove(empty);
		}
		ClearSelection();
		Profile.Save(_groups);
	}

	private void OnGroupSelected(object sender, RoutedEventArgs e)
	{
		List<TileVm> sel = SelectedTiles().ToList();
		if (sel.Count == 0)
		{
			return;
		}
		GroupVm group = new GroupVm
		{
			Name = SuggestGroupName(sel)
		};
		_groups.Add(group);
		foreach (TileVm t in sel)
		{
			MoveTileLive(t, group, group.Tiles.Count);
			t.IsSelected = false;
		}
		HideTileAppBar();
		Profile.Save(_groups);
		SmoothScroll.To(StartScroller, StartScroller.ScrollableWidth);
		((DispatcherObject)this).Dispatcher.BeginInvoke((Delegate)(Action)delegate
		{
			group.IsEditing = true;
			if (GroupsHost.ItemContainerGenerator.ContainerFromItem(group) is FrameworkElement root)
			{
				System.Windows.Controls.TextBox textBox = FindDescendant<System.Windows.Controls.TextBox>((DependencyObject)(object)root, "GroupNameEdit");
				if (textBox != null)
				{
					textBox.Focus();
					textBox.SelectAll();
				}
			}
		}, (DispatcherPriority)6, Array.Empty<object>());
	}

	private static string SuggestGroupName(IEnumerable<TileVm> tiles)
	{
		List<string> names = (from t in tiles
			where !t.IsLive
			select t.Entry.Name.ToLowerInvariant()).ToList();
		if (names.Count == 0)
		{
			return "New group";
		}
		(string, string[])[] categories = new(string, string[])[7]
		{
			("Office", new string[8] { "word", "excel", "powerpoint", "outlook", "onenote", "publisher", "access", "office" }),
			("Web", new string[7] { "chrome", "firefox", "edge", "opera", "brave", "internet explorer", "browser" }),
			("Games", new string[8] { "steam", "epic", "game", "xbox", "riot", "battle.net", "origin", "gog" }),
			("Media", new string[9] { "spotify", "vlc", "winamp", "media player", "music", "video", "obs", "photos", "itunes" }),
			("Communication", new string[9] { "discord", "whatsapp", "viber", "zoom", "skype", "teams", "slack", "telegram", "messenger" }),
			("Development", new string[9] { "visual studio", "code", "git", "github", "python", "node", "terminal", "powershell", "command prompt" }),
			("System", new string[11]
			{
				"settings", "control panel", "task manager", "registry", "event viewer", "defragment", "disk", "services", "computer management", "this pc",
				"explorer"
			})
		};
		string best = "New group";
		int bestScore = 0;
		(string, string[])[] array = categories;
		for (int num = 0; num < array.Length; num++)
		{
			(string, string[]) tuple = array[num];
			string cat = tuple.Item1;
			string[] keys = tuple.Item2;
			int score = names.Count((string n) => keys.Any(n.Contains));
			if (score > bestScore)
			{
				bestScore = score;
				best = cat;
			}
		}
		return (bestScore >= 2 || (bestScore >= 1 && names.Count <= 2)) ? best : "New group";
	}

	private void OnTileMiddleDown(object sender, MouseButtonEventArgs e)
	{
		if (e.ChangedButton == MouseButton.Middle && (sender as FrameworkElement)?.DataContext is TileVm tile)
		{
			if (tile.IsDesktop)
			{
				HideStart(animate: true);
			}
			else if (tile.IsFolder)
			{
				OpenFolderFlyout(tile, sender as FrameworkElement);
			}
			else
			{
				Launch(tile.Entry, asAdmin: false, keepOpen: true);
			}
			e.Handled = true;
		}
	}

	private void OnAppListClick(object sender, RoutedEventArgs e)
	{
		if ((sender as FrameworkElement)?.DataContext is AppEntry entry)
		{
			Launch(entry);
		}
	}

	private void OnResizeTile(object sender, RoutedEventArgs e)
	{
		if (!(sender is System.Windows.Controls.MenuItem { Tag: string tag, DataContext: var dataContext }))
		{
			return;
		}
		TileVm tile = dataContext as TileVm;
		if (tile == null)
		{
			return;
		}
		tile.Size = Enum.Parse<TileSize>(tag);
		_liveTiles?.RefreshWeatherFaces();
		if (tile.HasCell)
		{
			GroupVm grp = _groups.FirstOrDefault((GroupVm g) => g.Tiles.Contains(tile));
			if (grp != null)
			{
				var (c, r) = ResolveCell(grp, tile, tile.Col, tile.Row);
				tile.Col = c;
				tile.Row = r;
			}
		}
		Profile.Save(_groups);
	}

	private void OnUnpinTile(object sender, RoutedEventArgs e)
	{
		if (!(sender is System.Windows.Controls.MenuItem { DataContext: var dataContext }))
		{
			return;
		}
		TileVm tile = dataContext as TileVm;
		if (tile == null)
		{
			return;
		}
		GroupVm owner = _groups.FirstOrDefault((GroupVm g) => g.Tiles.Contains(tile));
		if (owner != null)
		{
			owner.Tiles.Remove(tile);
			if (owner.Tiles.Count == 0)
			{
				_groups.Remove(owner);
			}
			Profile.Save(_groups);
		}
	}

	private void OnTileRightClick(object sender, MouseButtonEventArgs e)
	{
		if (sender is System.Windows.Controls.Button { DataContext: TileVm tile })
		{
			e.Handled = true;
			if (_appBarEntry != null)
			{
				_appBarEntry.IsSelected = false;
				_appBarEntry = null;
			}
			tile.IsSelected = !tile.IsSelected;
			RefreshTileAppBar();
		}
	}

	private void RefreshTileAppBar()
	{
		List<TileVm> sel = SelectedTiles().ToList();
		if (sel.Count == 0)
		{
			HideTileAppBar();
			return;
		}
		BuildTileAppBarCommands(sel);
		ShowTileAppBar();
	}

	private void ShowTileAppBar()
	{
		TileAppBar.Background = AppBarThemeBrush();
		TileAppBar.BorderBrush = AppBarSeparatorBrush();
		TileAppBar.Visibility = Visibility.Visible;
		TileAppBarSlide.BeginAnimation(TranslateTransform.YProperty, Motion.To(0.0, Motion.Cat.EdgeEnter));
	}

	// FlatÃ¢â€ â€glass skin for the Start surfaces that are static XAML (SelectionBar, PersonalizePane, semantic-zoom
	// scrims). Runs on every Start show. These live inside the OPAQUE Start window, so alpha blends over the Start
	// background Ã¢â‚¬â€ plain WPF alpha, never WCA, never new AllowsTransparency (Start root stays opaque).
	private void ApplyStartSkin()
	{
		try
		{
			bool glass = ShellSkin.GlassOn;
			SelectionBar.Background = glass
				? ShellSkin.PanelBg()
				: new SolidColorBrush(System.Windows.Media.Color.FromArgb(0xF2, 0x11, 0x11, 0x11));
			PersonalizePane.Background = glass
				? ShellSkin.PanelBg()
				: new SolidColorBrush(System.Windows.Media.Color.FromArgb(0xF2, 0x1A, 0x1A, 0x1A));
			SolidColorBrush scrim;
			if (glass)
			{
				System.Windows.Media.Color a = SettingsPane.AccentToneColor(0.16);
				scrim = new SolidColorBrush(System.Windows.Media.Color.FromArgb(0xC8, a.R, a.G, a.B));
			}
			else
			{
				scrim = new SolidColorBrush(System.Windows.Media.Color.FromArgb(0xD8, 0x07, 0x07, 0x10));
			}
			AppsZoomOverlay.Background = scrim;
			ZoomOverlay.Background = scrim;
		}
		catch
		{
		}
	}

	private static SolidColorBrush AppBarThemeBrush()
	{
		System.Windows.Media.Color c;
		try
		{
			c = StartAccent.Color();
		}
		catch
		{
			c = System.Windows.Media.Color.FromRgb(90, 45, 145);
		}
		if (ShellSkin.GlassOn)
		{
			// Win7-Aero: translucent smoked-accent app bar (blends over the Start background) Ã¢â‚¬â€ plain alpha, no WCA.
			return new SolidColorBrush(System.Windows.Media.Color.FromArgb(200, S(c.R), S(c.G), S(c.B)));
		}
		return new SolidColorBrush(System.Windows.Media.Color.FromRgb(D(c.R), D(c.G), D(c.B)));
		static byte D(byte v)
		{
			return (byte)((double)(int)v * 0.88);
		}
		static byte S(byte v)
		{
			return (byte)((double)(int)v * 0.60);
		}
	}

	private static SolidColorBrush AppBarSeparatorBrush()
	{
		System.Windows.Media.Color c;
		try
		{
			c = StartAccent.Color();
		}
		catch
		{
			c = System.Windows.Media.Color.FromRgb(90, 45, 145);
		}
		if (ShellSkin.GlassOn)
		{
			return new SolidColorBrush(System.Windows.Media.Color.FromArgb(0x66, byte.MaxValue, byte.MaxValue, byte.MaxValue));
		}
		return new SolidColorBrush(System.Windows.Media.Color.FromRgb(D(c.R), D(c.G), D(c.B)));
		static byte D(byte v)
		{
			return (byte)((double)(int)v * 0.62);
		}
	}

	private bool IsInsideAppBar(DependencyObject d)
	{
		for (DependencyObject cur = d; cur != null; cur = VisualTreeHelper.GetParent(cur) ?? LogicalTreeHelper.GetParent(cur))
		{
			if ((object)cur == TileAppBar)
			{
				return true;
			}
		}
		return false;
	}

	public void HideTileAppBar()
	{
		if (TileAppBar.Visibility != Visibility.Visible)
		{
			return;
		}
		_appBarTile = null;
		if (_appBarEntry != null)
		{
			_appBarEntry.IsSelected = false;
			_appBarEntry = null;
		}
		foreach (TileVm t in SelectedTiles().ToList())
		{
			t.IsSelected = false;
		}
		SelectionBar.Visibility = Visibility.Collapsed;
		DoubleAnimation slide = Motion.To(92.0, Motion.Cat.EdgeExit);
		slide.Completed += delegate
		{
			TileAppBar.Visibility = Visibility.Collapsed;
		};
		TileAppBarSlide.BeginAnimation(TranslateTransform.YProperty, slide);
	}

	private void OnAppEntryRightClick(object sender, MouseButtonEventArgs e)
	{
		if (!(sender is System.Windows.Controls.Button { DataContext: AppEntry entry }))
		{
			return;
		}
		e.Handled = true;
		if (_appBarEntry == entry && TileAppBar.Visibility == Visibility.Visible)
		{
			HideTileAppBar();
			return;
		}
		if (_appBarTile != null)
		{
			_appBarTile.IsSelected = false;
			_appBarTile = null;
		}
		if (_appBarEntry != null)
		{
			_appBarEntry.IsSelected = false;
		}
		_appBarEntry = entry;
		entry.IsSelected = true;
		BuildAppEntryAppBarCommands(entry);
		ShowTileAppBar();
	}

	private void BuildAppEntryAppBarCommands(AppEntry entry)
	{
		TileAppBarCommands.Children.Clear();
		TileAppBarCustomise.Children.Clear();
		if (_groups.Any((GroupVm g) => g.Tiles.Any((TileVm t) => t.Entry == entry)))
		{
			TileAppBarCommands.Children.Add(AppBarCommand("\ue196", "Unpin from Start", delegate
			{
				UnpinEntryFromStart(entry);
				HideTileAppBar();
			}));
		}
		else
		{
			TileAppBarCommands.Children.Add(AppBarCommand("\ue141", "Pin to Start", delegate
			{
				PinEntryToStart(entry);
				HideTileAppBar();
			}));
		}
		if (IsTaskbarPinned(entry.LaunchPath))
		{
			TileAppBarCommands.Children.Add(AppBarCommand(AppBarVectorIcon(("M8,27 L32,27 L32,32 L8,32 Z", false), ("M11,28.3 L14,28.3 L14,30.7 L11,30.7 Z M18.5,28.3 L21.5,28.3 L21.5,30.7 L18.5,30.7 Z M26,28.3 L29,28.3 L29,30.7 L26,30.7 Z M14,7 L26,7 L26,12 L22,12 L20.8,24 L19.2,24 L18,12 L14,12 Z", true)), "Unpin from taskbar", delegate
			{
				UnpinLaunchPathFromTaskbar(entry.LaunchPath);
				HideTileAppBar();
			}));
		}
		else
		{
			TileAppBarCommands.Children.Add(AppBarCommand(AppBarVectorIcon(("M8,27 L32,27 L32,32 L8,32 Z", false), ("M11,28.3 L14,28.3 L14,30.7 L11,30.7 Z M18.5,28.3 L21.5,28.3 L21.5,30.7 L18.5,30.7 Z M26,28.3 L29,28.3 L29,30.7 L26,30.7 Z M14,7 L26,7 L26,12 L22,12 L20.8,24 L19.2,24 L18,12 L14,12 Z", true)), "Pin to taskbar", delegate
			{
				PinEntryToTaskbar(entry);
				HideTileAppBar();
			}));
		}
		TileAppBarCommands.Children.Add(AppBarCommand("\ue107", "Uninstall", delegate
		{
			HideTileAppBar();
			LaunchUri("ms-settings:appsfeatures");
		}));
		TileAppBarCommands.Children.Add(AppBarCommand(AppBarVectorIcon(("M9,13 L31,13 L31,27 L9,27 Z M20,24 L20,15 M16,19 L20,15 L24,19", false)), "Open new window", delegate
		{
			Launch(entry, asAdmin: false, keepOpen: true);
			HideTileAppBar();
		}));
		TileAppBarCommands.Children.Add(AppBarCommand("\ue1a7", "Run as administrator", delegate
		{
			Launch(entry, asAdmin: true);
			HideTileAppBar();
		}));
		TileAppBarCommands.Children.Add(AppBarCommand(AppBarVectorIcon(("M9,27 L9,14 L17,14 L20,11 L31,11 L31,27 Z M20,25 L20,16.5 M16.5,20 L20,16.5 L23.5,20", false)), "Open file location", delegate
		{
			OpenEntryFileLocation(entry);
			HideTileAppBar();
		}));
		TileAppBarCommands.Children.Add(AppBarCommand(AppBarVectorIcon(("M8,9 L14,9 L14,14 L8,14 Z M17,9 L23,9 L23,14 L17,14 Z M26,9 L32,9 L32,14 L26,14 Z", true), ("M20,30 L20,18 M16,22 L20,18 L24,22", false)), "Find in Start", delegate
		{
			FindInStart(entry);
			HideTileAppBar();
		}));
	}

	private void PinEntryToStart(AppEntry entry)
	{
		if (!_groups.Any((GroupVm g) => g.Tiles.Any((TileVm t) => t.Entry == entry)))
		{
			if (_groups.Count == 0)
			{
				_groups.Add(new GroupVm());
			}
			ObservableCollection<GroupVm> groups = _groups;
			groups[groups.Count - 1].Tiles.Add(new TileVm
			{
				Entry = entry
			});
			Profile.Save(_groups);
			UpgradeEntryIconForTile(entry);
		}
	}

	private void UpgradeEntryIconForTile(AppEntry entry)
	{
		lock (_priorityIconGate)
		{
			_priorityIconPaths.Add(entry.LaunchPath);
		}
		if (entry.IsOverrideIcon || entry.Icon is BitmapSource bitmap && Math.Max(bitmap.PixelWidth, bitmap.PixelHeight) >= 128)
		{
			return;
		}
		Thread thread = new Thread((ThreadStart)delegate
		{
			try
			{
				ImageSource icon = AppInventory.LoadIcon(entry.LaunchPath);
				if (icon != null)
				{
					List<(AppEntry Entry, ImageSource Icon, bool Over)> batch = new List<(AppEntry, ImageSource, bool)>(1)
					{
						(entry, icon, false)
					};
					QueueIconBatch(batch);
				}
			}
			catch (Exception ex)
			{
				Logger.Log($"[icon] tile quality upgrade failed for {entry.Name}: {ex.Message}");
			}
		})
		{
			IsBackground = true,
			Name = "TileIconUpgrade",
			Priority = ThreadPriority.BelowNormal
		};
		thread.SetApartmentState(ApartmentState.STA);
		thread.Start();
	}

	public void PinAppByPath(string? exePath, string fallbackName)
	{
		if (!string.IsNullOrEmpty(exePath))
		{
			AppEntry entry = _apps.FirstOrDefault((AppEntry a) => string.Equals(a.LaunchPath, exePath, StringComparison.OrdinalIgnoreCase));
			if (entry == null)
			{
				entry = new AppEntry
				{
					Name = (string.IsNullOrWhiteSpace(fallbackName) ? System.IO.Path.GetFileNameWithoutExtension(exePath) : fallbackName),
					LaunchPath = exePath,
					TileBrush = new SolidColorBrush(StartAccent.Color()),
					Icon = AppInventory.LoadIcon(exePath)
				};
				_apps.Add(entry);
			}
			PinEntryToStart(entry);
			ToastService.Show(entry.Icon, "Start", "Pinned", entry.Name + " was pinned to Start.");
		}
	}

	private void UnpinEntryFromStart(AppEntry entry)
	{
		foreach (GroupVm g in _groups.ToList())
		{
			TileVm t = g.Tiles.FirstOrDefault((TileVm x) => x.Entry == entry);
			if (t != null)
			{
				g.Tiles.Remove(t);
				if (g.Tiles.Count == 0)
				{
					_groups.Remove(g);
				}
			}
		}
		Profile.Save(_groups);
	}

	private void PinEntryToTaskbar(AppEntry entry)
	{
		lock (_priorityIconGate)
		{
			_priorityIconPaths.Add(entry.LaunchPath);
		}
		try
		{
			List<PinnedApp> pins = TaskbarPins.Load();
			if (!pins.Any((PinnedApp p) => string.Equals(p.LaunchPath, entry.LaunchPath, StringComparison.OrdinalIgnoreCase)))
			{
				pins.Add(new PinnedApp
				{
					Name = entry.Name,
					LaunchPath = entry.LaunchPath
				});
				TaskbarPins.Save(pins);
				TaskbarWindow.ReloadPins();
			}
		}
		catch (Exception ex)
		{
			Logger.Log("Pin entry to taskbar failed: " + ex.Message);
		}
	}

	// True when an app (by its launch path) is currently pinned to the taskbar — drives the Pin/Unpin toggle label.
	private static bool IsTaskbarPinned(string? launchPath)
	{
		if (string.IsNullOrEmpty(launchPath))
		{
			return false;
		}
		try
		{
			return TaskbarPins.Load().Any((PinnedApp p) => string.Equals(p.LaunchPath, launchPath, StringComparison.OrdinalIgnoreCase));
		}
		catch
		{
			return false;
		}
	}

	private void UnpinLaunchPathFromTaskbar(string? launchPath)
	{
		if (string.IsNullOrEmpty(launchPath))
		{
			return;
		}
		try
		{
			List<PinnedApp> pins = TaskbarPins.Load();
			if (pins.RemoveAll((PinnedApp p) => string.Equals(p.LaunchPath, launchPath, StringComparison.OrdinalIgnoreCase)) > 0)
			{
				TaskbarPins.Save(pins);
				TaskbarWindow.ReloadPins();
			}
		}
		catch (Exception ex)
		{
			Logger.Log("Unpin from taskbar failed: " + ex.Message);
		}
	}

	private static void OpenEntryFileLocation(AppEntry entry)
	{
		try
		{
			if (File.Exists(entry.LaunchPath))
			{
				string sel = entry.LaunchPath;
				// Off the UI thread: revealing in Explorer via ShellExecuteEx must not block the shell.
				ShellLaunch.Run(delegate
				{
					Process.Start(new ProcessStartInfo("explorer.exe", "/select,\"" + sel + "\"")
					{
						UseShellExecute = true
					});
				});
			}
		}
		catch (Exception ex)
		{
			Logger.Log("Open file location: " + ex.Message);
		}
	}

	private void FindInStart(AppEntry entry)
	{
		if (_showingAllApps)
		{
			SwitchView(showAllApps: false);
		}
		GroupVm group = _groups.FirstOrDefault((GroupVm g) => g.Tiles.Any((TileVm t) => t.Entry == entry));
		if (group == null)
		{
			return;
		}
		((DispatcherObject)this).Dispatcher.BeginInvoke((Delegate)(Action)delegate
		{
			//IL_004e: Unknown result type (might be due to invalid IL or missing references)
			//IL_0053: Unknown result type (might be due to invalid IL or missing references)
			//IL_0058: Unknown result type (might be due to invalid IL or missing references)
			if (GroupsHost.ItemContainerGenerator.ContainerFromItem(group) is FrameworkElement frameworkElement)
			{
				System.Windows.Point val = frameworkElement.TransformToAncestor(StartScroller).Transform(new System.Windows.Point(0.0, 0.0));
				double x = val.X;
				SmoothScroll.To(StartScroller, StartScroller.HorizontalOffset + x - 80.0);
			}
		}, (DispatcherPriority)6, Array.Empty<object>());
	}

	private void BuildTileAppBarCommands(List<TileVm> sel)
	{
		TileVm tile = sel[0];
		bool allReal = sel.All((TileVm t) => !t.IsDesktop && !t.IsFolder);
		TileAppBarCommands.Children.Clear();
		TileAppBarCommands.Children.Add(AppBarCommand("\ue196", "Unpin from Start", delegate
		{
			foreach (TileVm current in sel.ToList())
			{
				UnpinTileVm(current);
			}
			HideTileAppBar();
		}));
		if (allReal)
		{
			if (sel.Count == 1 && tile.Entry != null)
			{
				AppEntry tentry = tile.Entry;
				if (IsTaskbarPinned(tentry.LaunchPath))
				{
					TileAppBarCommands.Children.Add(AppBarCommand("\ue141", "Unpin from taskbar", delegate
					{
						UnpinLaunchPathFromTaskbar(tentry.LaunchPath);
						HideTileAppBar();
					}));
				}
				else
				{
					TileAppBarCommands.Children.Add(AppBarCommand("\ue141", "Pin to taskbar", delegate
					{
						PinTileToTaskbar(tile);
						HideTileAppBar();
					}));
				}
				TileAppBarCommands.Children.Add(AppBarCommand("\ue107", "Uninstall", delegate
				{
					HideTileAppBar();
					LaunchUri("ms-settings:appsfeatures");
				}));
				TileAppBarCommands.Children.Add(AppBarCommand(AppBarVectorIcon(("M9,13 L31,13 L31,27 L9,27 Z M20,24 L20,15 M16,19 L20,15 L24,19", false)), "Open new window", delegate
				{
					Launch(tentry, asAdmin: false, keepOpen: true);
					HideTileAppBar();
				}));
				TileAppBarCommands.Children.Add(AppBarCommand("\ue1a7", "Run as administrator", delegate
				{
					Launch(tentry, asAdmin: true);
					HideTileAppBar();
				}));
				TileAppBarCommands.Children.Add(AppBarCommand(AppBarVectorIcon(("M9,27 L9,14 L17,14 L20,11 L31,11 L31,27 Z M20,25 L20,16.5 M16.5,20 L20,16.5 L23.5,20", false)), "Open file location", delegate
				{
					OpenEntryFileLocation(tentry);
					HideTileAppBar();
				}));
			}
			else
			{
				TileAppBarCommands.Children.Add(AppBarCommand("\ue141", "Pin to taskbar", delegate
				{
					foreach (TileVm current in sel.ToList())
					{
						PinTileToTaskbar(current);
					}
					HideTileAppBar();
				}));
				TileAppBarCommands.Children.Add(AppBarCommand("\ue107", "Uninstall", delegate
				{
					HideTileAppBar();
					LaunchUri("ms-settings:appsfeatures");
				}));
			}
		}
		if (sel.Count >= 2)
		{
			TileAppBarCommands.Children.Add(AppBarCommand("\ue109", "New group", delegate
			{
				OnGroupSelected(this, new RoutedEventArgs());
			}));
		}
		if (sel.Count == 1 && tile.IsFolder)
		{
			TileAppBarCommands.Children.Add(AppBarCommand("\ue8b7", "Ungroup", delegate
			{
				Ungroup(tile);
				HideTileAppBar();
			}));
		}
		System.Windows.Controls.Button resize = AppBarCommand("\ue1d9", "Resize", delegate
		{
		});
		resize.Click += delegate
		{
			ShowResizeFlyout(resize, tile);
		};
		TileAppBarCommands.Children.Add(resize);
		if (sel.Count == 1 && tile.Live == LiveKind.Agenda)
		{
			TileAppBarCommands.Children.Add(AppBarCommand("\ue787", "Calendar account", delegate
			{
				HideTileAppBar();
				HideStart(animate: true);
				GoogleCalendarConnectWindow.ShowFor();
			}));
		}
		if (sel.Count == 1 && tile.Live != LiveKind.None && tile.Live != LiveKind.Desktop && tile.Live != LiveKind.Folder && tile.Live != LiveKind.Photo)
		{
			TileAppBarCommands.Children.Add(AppBarCommand("\ue894", tile.LiveOff ? "Turn live tile on" : "Turn live tile off", delegate
			{
				tile.LiveOff = !tile.LiveOff;
				if (tile.LiveOff)
				{
					tile.MetroMotionEnabled = false;
					tile.Entry.TileBrush = LiveTiles.BrandBrush(tile.Live);
				}
				else
				{
					_liveTiles?.RefreshWeatherFaces();
				}
				Profile.Save(_groups);
				HideTileAppBar();
			}));
		}
		TileAppBarCustomise.Children.Clear();
		TileAppBarCustomise.Children.Add(AppBarCommand("\ue104", "Customise", ToggleCustomise));
	}

	private void ToggleCustomise()
	{
		bool turnOn = !_customiseActive;
		HideTileAppBar();
		SetCustomise(turnOn);
	}

	private void SetCustomise(bool on)
	{
		if (_customiseActive == on)
		{
			return;
		}
		_customiseActive = on;
		foreach (GroupVm g in _groups)
		{
			g.ShowNamePrompt = on;
		}
		if (on)
		{
			return;
		}
		foreach (GroupVm g2 in _groups)
		{
			g2.IsEditing = false;
		}
	}

	private System.Windows.Controls.Button AppBarCommand(string glyph, string label, Action onClick)
	{
		string font = ((glyph.Length > 0 && glyph[0] >= '\ue100' && glyph[0] <= '\ue1ff') ? "Segoe UI Symbol" : "Segoe MDL2 Assets");
		TextBlock tb = new TextBlock
		{
			Text = glyph,
			FontFamily = new System.Windows.Media.FontFamily(font),
			FontSize = 18.0,
			Foreground = System.Windows.Media.Brushes.White,
			HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
			VerticalAlignment = VerticalAlignment.Center
		};
		return AppBarCommand(tb, label, onClick);
	}

	private System.Windows.Controls.Button AppBarCommand(UIElement icon, string label, Action onClick)
	{
		var button = MetroPatternTheme.CreateAppBarButton(icon, label,
			() => TaskbarContextMenu.QueueCommand(onClick));
		button.Foreground = System.Windows.Media.Brushes.White;
		return button;
	}

	private static Grid AppBarVectorIcon(params (string data, bool filled)[] parts)
	{
		Grid grid = new Grid
		{
			Width = 40.0,
			Height = 40.0,
			HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
			VerticalAlignment = VerticalAlignment.Center
		};
		for (int i = 0; i < parts.Length; i++)
		{
			(string data, bool filled) tuple = parts[i];
			string data = tuple.data;
			bool filled = tuple.filled;
			System.Windows.Shapes.Path p = new System.Windows.Shapes.Path
			{
				Data = Geometry.Parse(data),
				StrokeStartLineCap = PenLineCap.Round,
				StrokeEndLineCap = PenLineCap.Round,
				StrokeLineJoin = PenLineJoin.Round
			};
			if (filled)
			{
				p.Fill = System.Windows.Media.Brushes.White;
			}
			else
			{
				p.Stroke = System.Windows.Media.Brushes.White;
				p.StrokeThickness = 1.6;
			}
			grid.Children.Add(p);
		}
		return grid;
	}

	private void ShowResizeFlyout(System.Windows.Controls.Button anchor, TileVm tile)
	{
		StackPanel panel = new StackPanel();
		(string, TileSize)[] array = new(string, TileSize)[4]
		{
			("Small", TileSize.Small),
			("Medium", TileSize.Medium),
			("Wide", TileSize.Wide),
			("Large", TileSize.Large)
		};
		for (int i = 0; i < array.Length; i++)
		{
			(string, TileSize) tuple = array[i];
			string label = tuple.Item1;
			TileSize size = tuple.Item2;
			System.Windows.Controls.Button item = new System.Windows.Controls.Button
			{
				Content = label,
				Foreground = System.Windows.Media.Brushes.White,
				FontFamily = new System.Windows.Media.FontFamily("Segoe UI"),
				FontSize = 14.0,
				HorizontalContentAlignment = System.Windows.HorizontalAlignment.Left,
				Background = System.Windows.Media.Brushes.Transparent,
				BorderThickness = new Thickness(0.0),
				Padding = new Thickness(16.0, 8.0, 24.0, 8.0),
				Cursor = System.Windows.Input.Cursors.Hand,
				FocusVisualStyle = null,
				Template = HoverButtonTemplate()
			};
			TileSize sz = size;
			item.Click += delegate
			{
				SetTileSize(tile, sz);
				HideTileAppBar();
			};
			panel.Children.Add(item);
		}
		Border wrap = new Border
		{
			Background = (ShellSkin.GlassOn ? ShellSkin.PanelBg() : new SolidColorBrush(System.Windows.Media.Color.FromArgb(245, 38, 38, 38))),
			BorderBrush = new SolidColorBrush(System.Windows.Media.Color.FromArgb(85, byte.MaxValue, byte.MaxValue, byte.MaxValue)),
			BorderThickness = new Thickness(1.0),
			Child = panel,
			MinWidth = 150.0
		};
		Popup popup = new Popup
		{
			Child = wrap,
			PlacementTarget = anchor,
			Placement = PlacementMode.Top,
			StaysOpen = false,
			AllowsTransparency = true
		};
		popup.IsOpen = true;
	}

	private static ControlTemplate FlatButtonTemplate()
	{
		ControlTemplate t = new ControlTemplate(typeof(System.Windows.Controls.Button));
		FrameworkElementFactory cp = new FrameworkElementFactory(typeof(ContentPresenter));
		t.VisualTree = cp;
		return t;
	}

	private static ControlTemplate HoverButtonTemplate()
	{
		ControlTemplate t = new ControlTemplate(typeof(System.Windows.Controls.Button));
		FrameworkElementFactory bd = new FrameworkElementFactory(typeof(Border))
		{
			Name = "bd"
		};
		bd.SetValue(Border.BackgroundProperty, System.Windows.Media.Brushes.Transparent);
		FrameworkElementFactory cp = new FrameworkElementFactory(typeof(ContentPresenter));
		cp.SetValue(FrameworkElement.MarginProperty, new TemplateBindingExtension(System.Windows.Controls.Control.PaddingProperty));
		bd.AppendChild(cp);
		t.VisualTree = bd;
		Trigger hover = new Trigger
		{
			Property = UIElement.IsMouseOverProperty,
			Value = true
		};
		hover.Setters.Add(new Setter(Border.BackgroundProperty, new SolidColorBrush(System.Windows.Media.Color.FromArgb(34, byte.MaxValue, byte.MaxValue, byte.MaxValue)), "bd"));
		t.Triggers.Add(hover);
		return t;
	}

	private void SetTileSize(TileVm tile, TileSize size)
	{
		tile.Size = size;
		_liveTiles?.RefreshWeatherFaces();
		if (tile.HasCell)
		{
			GroupVm grp = _groups.FirstOrDefault((GroupVm g) => g.Tiles.Contains(tile));
			if (grp != null)
			{
				var (c, r) = ResolveCell(grp, tile, tile.Col, tile.Row);
				tile.Col = c;
				tile.Row = r;
			}
		}
		Profile.Save(_groups);
	}

	private void UnpinTileVm(TileVm tile)
	{
		GroupVm owner = _groups.FirstOrDefault((GroupVm g) => g.Tiles.Contains(tile));
		if (owner != null)
		{
			owner.Tiles.Remove(tile);
			if (owner.Tiles.Count == 0)
			{
				_groups.Remove(owner);
			}
			Profile.Save(_groups);
		}
	}

	private void PinTileToTaskbar(TileVm tile)
	{
		try
		{
			List<PinnedApp> pins = TaskbarPins.Load();
			if (!pins.Any((PinnedApp p) => string.Equals(p.LaunchPath, tile.Entry.LaunchPath, StringComparison.OrdinalIgnoreCase)))
			{
				pins.Add(new PinnedApp
				{
					Name = tile.Entry.Name,
					LaunchPath = tile.Entry.LaunchPath,
					Aumid = tile.Entry.AppId
				});
				TaskbarPins.Save(pins);
				TaskbarWindow.ReloadPins();
			}
		}
		catch (Exception ex)
		{
			Logger.Log("Pin to taskbar failed: " + ex.Message);
		}
	}

	private static void LaunchUri(string uri)
	{
		try
		{
			Process.Start(new ProcessStartInfo(uri)
			{
				UseShellExecute = true
			});
		}
		catch (Exception ex)
		{
			Logger.Log("Launch '" + uri + "' failed: " + ex.Message);
		}
	}

	private void OnTilePress(object sender, MouseButtonEventArgs e)
	{
		//IL_0004: Unknown result type (might be due to invalid IL or missing references)
		//IL_0009: Unknown result type (might be due to invalid IL or missing references)
		//IL_0085: Unknown result type (might be due to invalid IL or missing references)
		//IL_008a: Unknown result type (might be due to invalid IL or missing references)
		_dragStart = e.GetPosition(this);
		_dragCandidate = (sender as FrameworkElement)?.DataContext as TileVm;
		_dragSourceEl = sender as FrameworkElement;
		if (sender is System.Windows.Controls.Button b && b.Template?.FindName("PressScale", b) is ScaleTransform ps && b.Template?.FindName("PressNudge", b) is TranslateTransform tn)
		{
			System.Windows.Point p = e.GetPosition(b);
			double w = ((b.ActualWidth <= 0.0) ? 1.0 : b.ActualWidth);
			double h = ((b.ActualHeight <= 0.0) ? 1.0 : b.ActualHeight);
			double nx = Math.Clamp(p.X / w * 2.0 - 1.0, -1.0, 1.0);
			double ny = Math.Clamp(p.Y / h * 2.0 - 1.0, -1.0, 1.0);
			_pressTiltScale = ps;
			_pressTiltXf = tn;
			Duration dur = Motion.Dur(Motion.Cat.Press);
			IEasingFunction ease = Motion.Ease(Motion.Cat.Press);
			ps.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.965, dur)
			{
				EasingFunction = ease
			});
			ps.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.965, dur)
			{
				EasingFunction = ease
			});
			tn.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(nx * 3.0, dur)
			{
				EasingFunction = ease
			});
			tn.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(ny * 3.0, dur)
			{
				EasingFunction = ease
			});
		}
	}

	private void ResetPressTilt()
	{
		Duration dur = Motion.Dur(Motion.Cat.Micro);
		IEasingFunction ease = Motion.Ease(Motion.Cat.Micro);
		_pressTiltScale?.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(1.0, dur)
		{
			EasingFunction = ease
		});
		_pressTiltScale?.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(1.0, dur)
		{
			EasingFunction = ease
		});
		_pressTiltXf?.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(0.0, dur)
		{
			EasingFunction = ease
		});
		_pressTiltXf?.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(0.0, dur)
		{
			EasingFunction = ease
		});
		_pressTiltScale = null;
		_pressTiltXf = null;
	}

	private void OnTileMove(object sender, System.Windows.Input.MouseEventArgs e)
	{
		//IL_0031: Unknown result type (might be due to invalid IL or missing references)
		//IL_0036: Unknown result type (might be due to invalid IL or missing references)
		if (!_dragging)
		{
			if (_dragCandidate != null && e.LeftButton != MouseButtonState.Pressed)
			{
				// button was released (possibly over a gap / off-window, where the per-button OnTileUp never fired) Ã¢â‚¬â€
				// drop the stale candidate so a later unrelated left-drag can't resurrect it into a spurious tile drag
				_dragCandidate = null;
				_dragSourceEl = null;
				return;
			}
			if (_dragCandidate != null && !_zoomDragging && e.LeftButton == MouseButtonState.Pressed)
			{
				System.Windows.Point p = e.GetPosition(this);
				if (!(Math.Abs(p.X - _dragStart.X) < 6.0) || !(Math.Abs(p.Y - _dragStart.Y) < 6.0))
				{
					BeginTileDrag(_dragSourceEl ?? (sender as FrameworkElement), e);
				}
			}
		}
		else
		{
			_dragDirty = true;
			HookDragFrame();
		}
	}

	private void BeginTileDrag(FrameworkElement? source, System.Windows.Input.MouseEventArgs e)
	{
		//IL_00c3: Unknown result type (might be due to invalid IL or missing references)
		//IL_00ba: Unknown result type (might be due to invalid IL or missing references)
		//IL_00c8: Unknown result type (might be due to invalid IL or missing references)
		//IL_01a4: Unknown result type (might be due to invalid IL or missing references)
		//IL_01a9: Unknown result type (might be due to invalid IL or missing references)
		//IL_0205: Unknown result type (might be due to invalid IL or missing references)
		//IL_0225: Unknown result type (might be due to invalid IL or missing references)
		ResetPressTilt();
		TileVm tile = _dragCandidate;
		_dragCandidate = null;
		_dragging = true;
		_dragPrimary = tile;
		_dragSource = source;
		_hoverOccupant = null;
		_hoverSince = 0;
		if (tile.IsSelected && SelectedTiles().Count() > 1)
		{
			foreach (TileVm t in SelectedTiles().ToList())
			{
				FreezeCell(t);
			}
		}
		_grabOffset = (System.Windows.Point)((source != null) ? e.GetPosition(source) : new System.Windows.Point(tile.PixelWidth / 2.0, tile.PixelHeight / 2.0));
		CreateGhost(tile, source);
		// Pull the whole board back slightly so the lifted ghost stands out (Win8.1 drag feel).
		EnsureBoardZoom();
		AnimateBoardZoom(BoardDragZoom);
		List<TileVm> list;
		if (!tile.IsSelected || SelectedTiles().Count() <= 1)
		{
			int num = 1;
			list = new List<TileVm>(num);
			CollectionsMarshal.SetCount(list, num);
			Span<TileVm> span = CollectionsMarshal.AsSpan(list);
			int index = 0;
			span[index] = tile;
		}
		else
		{
			list = SelectedTiles().ToList();
		}
		List<TileVm> dimSet = list;
		foreach (TileVm t2 in dimSet)
		{
			t2.IsDragging = true;
		}
		source?.CaptureMouse();
		if (source != null)
		{
			source.LostMouseCapture += OnTileLostCapture;
		}
		_dragLogged = false;
		_packedFoldArmed = false;
		_packedLastPos = new System.Windows.Point(-9999.0, -9999.0);
		_packedLastMoveTs = 0;
		Logger.Log($"BeginTileDrag: tile='{tile.Entry?.Name}', sourceSize={source?.RenderSize}, grab={_grabOffset}, ghost={_ghost != null}, overlayChildren={DragOverlay.Children.Count}");
		UpdateTileDrag(e);
	}

	private void CreateGhost(TileVm tile, FrameworkElement? source)
	{
		//IL_014f: Unknown result type (might be due to invalid IL or missing references)
		RemoveGhost();
		System.Windows.Media.Brush fill;
		if (source != null && source.ActualWidth > 0.0 && source.ActualHeight > 0.0)
		{
			RenderTargetBitmap bmp = new RenderTargetBitmap((int)Math.Ceiling(source.ActualWidth), (int)Math.Ceiling(source.ActualHeight), 96.0, 96.0, PixelFormats.Pbgra32);
			bmp.Render(source);
			((Freezable)bmp).Freeze();
			fill = new ImageBrush(bmp)
			{
				Stretch = Stretch.Fill
			};
		}
		else
		{
			fill = System.Windows.Media.Brushes.Gray;
		}
		DropShadowEffect shadow = new DropShadowEffect
		{
			BlurRadius = 30.0,
			ShadowDepth = 0.0,
			Opacity = 0.6,
			Color = Colors.Black
		};
		ScaleTransform lift = new ScaleTransform(1.0, 1.0);
		_ghostMove = new TranslateTransform();
		_ghost = new Border
		{
			Width = tile.PixelWidth,
			Height = tile.PixelHeight,
			Background = fill,
			Opacity = 0.6,
			IsHitTestVisible = false,
			RenderTransformOrigin = new System.Windows.Point(0.5, 0.5),
			RenderTransform = new TransformGroup
			{
				Children = 
				{
					(Transform)lift,
					(Transform)_ghostMove
				}
			},
			Effect = shadow,
			CacheMode = new BitmapCache()
		};
		DragOverlay.Children.Add(_ghost);
		QuadraticEase ease = new QuadraticEase
		{
			EasingMode = EasingMode.EaseOut
		};
		Duration dur = Motion.Dur(Motion.Cat.Press);   // snappy pick-up (was a flat 150ms), scales with mode
		lift.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(1.0, 1.14, dur)
		{
			EasingFunction = ease
		});
		lift.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(1.0, 1.14, dur)
		{
			EasingFunction = ease
		});
		_ghost.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0.6, 0.9, dur)
		{
			EasingFunction = ease
		});
	}

	private void RemoveGhost()
	{
		if (_ghost != null)
		{
			DragOverlay.Children.Remove(_ghost);
			_ghost = null;
		}
		_ghostMove = null;
	}

	// Lazily insert a scale in front of StartScroller's view-slide transform, centred on the viewport. Idempotent.
	// StartViewSlide keeps being animated directly by the view transition (ViewSlide() returns the field, not the
	// RenderTransform), so composing it into a group here is transparent to that code.
	private void EnsureBoardZoom()
	{
		if (_boardZoom != null)
		{
			return;
		}
		_boardZoom = new ScaleTransform(1.0, 1.0);
		TransformGroup grp = new TransformGroup();
		grp.Children.Add(_boardZoom);
		Transform existing = StartScroller.RenderTransform;
		if (existing != null && existing != Transform.Identity)
		{
			grp.Children.Add(existing);   // the existing StartViewSlide (TranslateTransform)
		}
		StartScroller.RenderTransformOrigin = new System.Windows.Point(0.5, 0.5);
		StartScroller.RenderTransform = grp;
	}

	private void AnimateBoardZoom(double to)
	{
		if (_boardZoom == null)
		{
			return;
		}
		_boardZoom.BeginAnimation(ScaleTransform.ScaleXProperty, null);
		_boardZoom.BeginAnimation(ScaleTransform.ScaleYProperty, null);
		if (Motion.Mode == MotionMode.Off)
		{
			_boardZoom.ScaleX = to;
			_boardZoom.ScaleY = to;
			SyncDropPreviewZoom();
			return;
		}
		Duration dur = Motion.Dur(Motion.Cat.Reposition);
		IEasingFunction ease = new CubicEase { EasingMode = EasingMode.EaseOut };
		_boardZoom.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(to, dur) { EasingFunction = ease });
		_boardZoom.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(to, dur) { EasingFunction = ease });
	}

	// Keep the drop-preview placeholder the same visual size as the (zoomed) tiles it sits among. Its top-left is
	// already positioned through the board transform (TransformToVisual), so a top-left-anchored scale by the same
	// factor makes it line up exactly.
	private void SyncDropPreviewZoom()
	{
		double z = ((_boardZoom != null) && _boardZoom.ScaleX > 0.0) ? _boardZoom.ScaleX : 1.0;
		if (!(DropPreview.RenderTransform is ScaleTransform ds))
		{
			ds = new ScaleTransform();
			DropPreview.RenderTransformOrigin = new System.Windows.Point(0.0, 0.0);
			DropPreview.RenderTransform = ds;
		}
		ds.ScaleX = z;
		ds.ScaleY = z;
	}

	private void HookDragFrame()
	{
		if (!_dragFrameHooked)
		{
			_dragFrameHooked = true;
			CompositionTarget.Rendering += OnDragFrame;
		}
	}

	private void UnhookDragFrame()
	{
		if (_dragFrameHooked)
		{
			_dragFrameHooked = false;
			CompositionTarget.Rendering -= OnDragFrame;
		}
	}

	private void OnDragFrame(object? sender, EventArgs e)
	{
		if (!_dragging)
		{
			UnhookDragFrame();
		}
		else if (_dragDirty)
		{
			_dragDirty = false;
			UpdateTileDrag();
		}
	}

	private void ScheduleSave()
	{
		if (!_saveQueued)
		{
			_saveQueued = true;
			((DispatcherObject)this).Dispatcher.BeginInvoke((Delegate)(Action)delegate
			{
				FlushQueuedProfileSave();
			}, (DispatcherPriority)4, Array.Empty<object>());
		}
	}

	public void CommitActiveGroupEditAndFlush()
	{
		CommitActiveGroupEditForSave();
		FlushQueuedProfileSave();
	}

	private void CommitActiveGroupEditForSave()
	{
		System.Windows.Controls.TextBox? activeGroupEdit = _activeGroupEdit;
		if (activeGroupEdit == null || activeGroupEdit.DataContext is not GroupVm group)
		{
			return;
		}
		string? original = _renameOriginal;
		bool hadRenameState = group.IsEditing || original != null;
		if (group.IsEditing && activeGroupEdit.IsVisible)
		{
			group.Name = activeGroupEdit.Text ?? group.Name;
			group.IsEditing = false;
			Keyboard.ClearFocus();
		}
		if (hadRenameState && !string.Equals(group.Name, original, StringComparison.Ordinal))
		{
			_saveQueued = true;
		}
		_activeGroupEdit = null;
		_renameOriginal = null;
	}

	private void FlushQueuedProfileSave()
	{
		if (!_saveQueued)
		{
			return;
		}
		_saveQueued = false;
		Profile.Save(_groups);
	}

	private void UpdateTileDrag(System.Windows.Input.MouseEventArgs e)
	{
		UpdateTileDrag();
	}

	private void UpdateTileDrag()
	{
		//IL_001b: Unknown result type (might be due to invalid IL or missing references)
		//IL_0020: Unknown result type (might be due to invalid IL or missing references)
		//IL_007f: Unknown result type (might be due to invalid IL or missing references)
		//IL_0084: Unknown result type (might be due to invalid IL or missing references)
		//IL_0086: Unknown result type (might be due to invalid IL or missing references)
		//IL_0087: Unknown result type (might be due to invalid IL or missing references)
		//IL_008d: Unknown result type (might be due to invalid IL or missing references)
		//IL_009b: Unknown result type (might be due to invalid IL or missing references)
		//IL_00e0: Unknown result type (might be due to invalid IL or missing references)
		//IL_00d2: Unknown result type (might be due to invalid IL or missing references)
		//IL_011c: Unknown result type (might be due to invalid IL or missing references)
		if (!_dragging)
		{
			return;
		}
		System.Windows.Point over = Mouse.GetPosition(DragOverlay);
		if (_ghost != null && _ghostMove != null)
		{
			_ghostMove.X = over.X - _grabOffset.X;
			_ghostMove.Y = over.Y - _grabOffset.Y;
		}
		System.Windows.Point posInThis = (_lastDragPoint = Mouse.GetPosition(this));
		UpdateDragEdgeScroll(posInThis);
		bool overNew = IsOverElement(NewGroupZone, posInThis);
		NewGroupVisual.Opacity = (overNew ? 0.8 : 0.0);
		if (TilePanel.Packed)
		{
			UpdatePackedDrag(posInThis, overNew);
			return;
		}
		var (group, panel) = GroupPanelAt(posInThis);
		if (!overNew && group != null && panel != null && _dragPrimary != null)
		{
			(int, int) tuple2 = DropCell(panel, Mouse.GetPosition(panel));
			int rawCol = tuple2.Item1;
			int rawRow = tuple2.Item2;
			TileVm occ = FoldCandidate(group, _dragPrimary, rawCol, rawRow);
			if (occ != _hoverOccupant)
			{
				_hoverOccupant = occ;
				_hoverSince = Environment.TickCount;
			}
			bool foldArmed = occ != null && Environment.TickCount - _hoverSince >= 600;
			if (foldArmed && occ != null)
			{
				ShowFoldPreviewAt(panel, occ);
			}
			else
			{
				var (col, row) = ResolveCell(group, _dragPrimary, rawCol, rawRow);
				ShowDropPreviewAt(panel, col, row, _dragPrimary);
			}
			if (!_dragLogged)
			{
				_dragLogged = true;
				Logger.Log($"DragPreview: group={group.Name}, cell=({rawCol},{rawRow}), fold={foldArmed}");
			}
		}
		else
		{
			_hoverOccupant = null;
			HideDropPreview();
			if (!_dragLogged)
			{
				_dragLogged = true;
				Logger.Log($"DragPreview: NO group/panel under cursor (overNew={overNew})");
			}
		}
	}

	private (int, int) DropCell(TilePanel panel, System.Windows.Point cursorInPanel)
	{
		double pitch = TilePanel.PitchValue;
		int col = Math.Max(0, (int)Math.Round((cursorInPanel.X - _grabOffset.X) / pitch));
		int row = Math.Max(0, (int)Math.Round((cursorInPanel.Y - _grabOffset.Y) / pitch));
		return (col, row);
	}

	private void UpdateDragEdgeScroll(System.Windows.Point posInThis)
	{
		//IL_0056: Unknown result type (might be due to invalid IL or missing references)
		//IL_005b: Unknown result type (might be due to invalid IL or missing references)
		//IL_0060: Unknown result type (might be due to invalid IL or missing references)
		if (_showingAllApps || StartScroller.ScrollableWidth <= 1.0)
		{
			_dragScrollDir = 0;
			return;
		}
		double left;
		try
		{
			System.Windows.Point val = StartScroller.TransformToAncestor(this).Transform(new System.Windows.Point(0.0, 0.0));
			left = val.X;
		}
		catch
		{
			_dragScrollDir = 0;
			return;
		}
		double right = left + StartScroller.ActualWidth;
		if (posInThis.X < left + 72.0 && StartScroller.HorizontalOffset > 0.0)
		{
			_dragScrollDir = -1;
		}
		else if (posInThis.X > right - 72.0 && StartScroller.HorizontalOffset < StartScroller.ScrollableWidth)
		{
			_dragScrollDir = 1;
		}
		else
		{
			_dragScrollDir = 0;
		}
		if (_dragScrollDir != 0)
		{
			if (_dragScrollTimer == null)
			{
				_dragScrollTimer = CreateDragScrollTimer();
			}
			if (!_dragScrollTimer.IsEnabled)
			{
				_dragScrollTimer.Start();
			}
		}
	}

	private DispatcherTimer CreateDragScrollTimer()
	{
		//IL_000f: Unknown result type (might be due to invalid IL or missing references)
		//IL_0014: Unknown result type (might be due to invalid IL or missing references)
		//IL_0028: Expected O, but got Unknown
		DispatcherTimer t = new DispatcherTimer
		{
			Interval = TimeSpan.FromMilliseconds(16L)
		};
		t.Tick += delegate
		{
			if (!_dragging || _dragScrollDir == 0)
			{
				t.Stop();
			}
			else
			{
				double offset = Math.Clamp(StartScroller.HorizontalOffset + (double)(_dragScrollDir * 22), 0.0, StartScroller.ScrollableWidth);
				StartScroller.ScrollToHorizontalOffset(offset);
				UpdateTileDrag();
			}
		};
		return t;
	}

	private void StopDragScroll()
	{
		_dragScrollDir = 0;
		DispatcherTimer? dragScrollTimer = _dragScrollTimer;
		if (dragScrollTimer != null)
		{
			dragScrollTimer.Stop();
		}
	}

	private void OnTileUp(object sender, MouseButtonEventArgs e)
	{
		//IL_0021: Unknown result type (might be due to invalid IL or missing references)
		//IL_0026: Unknown result type (might be due to invalid IL or missing references)
		//IL_002f: Unknown result type (might be due to invalid IL or missing references)
		ResetPressTilt();
		if (!_dragging)
		{
			_dragCandidate = null;
			_dragSourceEl = null;
			return;
		}
		System.Windows.Point pos = e.GetPosition(this);
		EndTileDrag();
		CommitDrop(pos);
		e.Handled = true;
	}

	private void OnTileLostCapture(object sender, System.Windows.Input.MouseEventArgs e)
	{
		//IL_0011: Unknown result type (might be due to invalid IL or missing references)
		//IL_0016: Unknown result type (might be due to invalid IL or missing references)
		//IL_001f: Unknown result type (might be due to invalid IL or missing references)
		if (_dragging)
		{
			System.Windows.Point pos = _lastDragPoint;
			EndTileDrag();
			CommitDrop(pos);
		}
	}

	private void EndTileDrag()
	{
		_dragging = false;
		AnimateBoardZoom(1.0);   // ease the board pull-back back to normal
		_dragSourceEl = null;
		_dragDirty = false;
		UnhookDragFrame();
		StopDragScroll();
		if (_dragSource != null)
		{
			_dragSource.LostMouseCapture -= OnTileLostCapture;
		}
		_dragSource?.ReleaseMouseCapture();
		RemoveGhost();
		HideDropPreview();
		NewGroupVisual.Opacity = 0.0;
		foreach (TileVm t in _groups.SelectMany((GroupVm g) => g.Tiles))
		{
			t.IsDragging = false;
		}
		_dragSource = null;
		_dragLogged = false;
	}

	private void CommitDrop(System.Windows.Point posInThis)
	{
		//IL_000c: Unknown result type (might be due to invalid IL or missing references)
		//IL_003c: Unknown result type (might be due to invalid IL or missing references)
		//IL_0070: Unknown result type (might be due to invalid IL or missing references)
		//IL_0094: Unknown result type (might be due to invalid IL or missing references)
		//IL_00bf: Unknown result type (might be due to invalid IL or missing references)
		//IL_00c0: Unknown result type (might be due to invalid IL or missing references)
		//IL_00c5: Unknown result type (might be due to invalid IL or missing references)
		//IL_00c9: Unknown result type (might be due to invalid IL or missing references)
		if (TilePanel.Packed)
		{
			CommitPackedDrop(posInThis);
			return;
		}
		TileVm primary = _dragPrimary;
		_dragPrimary = null;
		if (primary == null)
		{
			return;
		}
		if (IsOverElement(NewGroupZone, posInThis))
		{
			GroupVm ng = new GroupVm();
			_groups.Add(ng);
			PlaceTileAt(primary, ng, 0, 0);
			return;
		}
		var (group, panel) = GroupPanelAt(posInThis);
		if (group == null || panel == null)
		{
			(group, panel) = NearestGroupPanel(posInThis);
		}
		if (group != null && panel != null)
		{
			System.Windows.Point inPanel = TransformToVisual(panel).Transform(posInThis);
			var (col, row) = DropCell(panel, inPanel);
			PlaceTileAt(primary, group, col, row);
		}
		else
		{
			Profile.Save(_groups);
		}
	}

	private void UpdatePackedDrag(System.Windows.Point posInThis, bool overNew)
	{
		//IL_0030: Unknown result type (might be due to invalid IL or missing references)
		//IL_0054: Unknown result type (might be due to invalid IL or missing references)
		//IL_00b4: Unknown result type (might be due to invalid IL or missing references)
		//IL_00b5: Unknown result type (might be due to invalid IL or missing references)
		//IL_00ba: Unknown result type (might be due to invalid IL or missing references)
		//IL_0100: Unknown result type (might be due to invalid IL or missing references)
		//IL_0102: Unknown result type (might be due to invalid IL or missing references)
		//IL_01df: Unknown result type (might be due to invalid IL or missing references)
		//IL_014d: Unknown result type (might be due to invalid IL or missing references)
		//IL_015d: Unknown result type (might be due to invalid IL or missing references)
		//IL_0162: Unknown result type (might be due to invalid IL or missing references)
		//IL_01b6: Unknown result type (might be due to invalid IL or missing references)
		TileVm primary = _dragPrimary;
		if ((primary == null) | overNew)
		{
			_hoverOccupant = null;
			_packedFoldArmed = false;
			HideDropPreview();
			return;
		}
		var (group, panel) = GroupPanelAt(posInThis);
		if (group == null || panel == null)
		{
			(group, panel) = NearestGroupPanel(posInThis);
		}
		if (group == null || panel == null)
		{
			return;
		}
		List<TileVm> dragged = ((primary.IsSelected && SelectedTiles().Count() > 1) ? SelectedTiles().ToList() : new List<TileVm> { primary });
		System.Windows.Point inPanel = TransformToVisual(panel).Transform(posInThis);
		if (Math.Abs(inPanel.X - _packedLastPos.X) + Math.Abs(inPanel.Y - _packedLastPos.Y) > 6.0)
		{
			_packedLastPos = inPanel;
			_packedLastMoveTs = Environment.TickCount;
		}
		bool stationary = Environment.TickCount - _packedLastMoveTs >= 150;
		if ((dragged.Count == 1 && !primary.IsDesktop) & stationary)
		{
			var (foldT, cell) = PackedCentralTarget(group, panel, inPanel, dragged);
			if (foldT != null)
			{
				if (foldT != _hoverOccupant)
				{
					_hoverOccupant = foldT;
					_hoverSince = Environment.TickCount;
				}
				_packedFoldArmed = Environment.TickCount - _hoverSince >= 600;
				ShowFoldPreviewCell(panel, cell, _packedFoldArmed);
				return;
			}
		}
		_hoverOccupant = null;
		_packedFoldArmed = false;
		HideDropPreview();
		int idx = PackedInsertIndex(group, panel, inPanel, dragged);
		if (dragged.Count > 1)
		{
			PackedMoveBlockLive(dragged, primary, group, idx);
		}
		else
		{
			PackedMoveLive(primary, group, idx);
		}
	}

	private void PackedMoveBlockLive(List<TileVm> block, TileVm primary, GroupVm group, int insertIdx)
	{
		if (!block.All(group.Tiles.Contains))
		{
			PackedMoveLive(primary, group, insertIdx);
			return;
		}
		List<TileVm> nonBlock = group.Tiles.Where((TileVm t) => !block.Contains(t)).ToList();
		List<TileVm> blockOrdered = group.Tiles.Where(block.Contains).ToList();
		List<TileVm> desired = new List<TileVm>(nonBlock);
		desired.InsertRange(Math.Clamp(insertIdx, 0, nonBlock.Count), blockOrdered);
		for (int i = 0; i < desired.Count; i++)
		{
			int cur = group.Tiles.IndexOf(desired[i]);
			if (cur != i && cur >= 0)
			{
				group.Tiles.Move(cur, i);
			}
		}
	}

	private (TileVm? tile, Rect cell) PackedCentralTarget(GroupVm group, TilePanel panel, System.Windows.Point cursorInPanel, List<TileVm> dragged)
	{
		//IL_0102: Unknown result type (might be due to invalid IL or missing references)
		//IL_0108: Unknown result type (might be due to invalid IL or missing references)
		//IL_00c3: Unknown result type (might be due to invalid IL or missing references)
		//IL_00d8: Unknown result type (might be due to invalid IL or missing references)
		double pitch = TilePanel.PitchValue;
		Rect inner = default(Rect);
		foreach (TileVm t in group.Tiles)
		{
			if (dragged.Contains(t) || t.IsDesktop)
			{
				continue;
			}
			ContentPresenter cp = FindTileContainer(t);
			if (cp != null && panel.TryGetCell(cp, out var c, out var r))
			{
				double x = (double)c * pitch;
				double y = (double)r * pitch;
				double w = t.PixelWidth;
				double h = t.PixelHeight;
				inner = new Rect(x + w * 0.2, y + h * 0.2, w * 0.6, h * 0.6);
				if (inner.Contains(cursorInPanel))
				{
					return (tile: t, cell: new Rect(x, y, w, h));
				}
			}
		}
		return (tile: null, cell: default(Rect));
	}

	private void ShowFoldPreviewCell(TilePanel panel, Rect cell, bool armed)
	{
		//IL_001b: Unknown result type (might be due to invalid IL or missing references)
		//IL_0020: Unknown result type (might be due to invalid IL or missing references)
		//IL_0025: Unknown result type (might be due to invalid IL or missing references)
		System.Windows.Point tl = panel.TransformToVisual(DragOverlay).Transform(new System.Windows.Point(cell.X, cell.Y));
		Canvas.SetLeft(DropPreview, tl.X);
		Canvas.SetTop(DropPreview, tl.Y);
		DropPreview.Width = cell.Width;
		DropPreview.Height = cell.Height;
		DropPreview.Opacity = (armed ? 1.0 : 0.55);
		DropFoldGlyph.Visibility = ((!armed) ? Visibility.Collapsed : Visibility.Visible);
		DropPreview.Visibility = Visibility.Visible;
		SyncDropPreviewZoom();
	}

	private int PackedInsertIndex(GroupVm group, TilePanel panel, System.Windows.Point cursorInPanel, List<TileVm> dragged)
	{
		//IL_0013: Unknown result type (might be due to invalid IL or missing references)
		int capacity = Math.Max(1, TilePanel.RowsForHeight(panel.ActualHeight));
		(int, int) cc = TilePanel.CellAt(cursorInPanel);
		long cursorScalar = (long)cc.Item1 * (long)capacity + cc.Item2;
		int idx = 0;
		foreach (TileVm t in group.Tiles)
		{
			if (!dragged.Contains(t))
			{
				ContentPresenter cp = FindTileContainer(t);
				if (cp != null && panel.TryGetCell(cp, out var c, out var r) && (long)c * (long)capacity + r < cursorScalar)
				{
					idx++;
				}
			}
		}
		return idx;
	}

	private void PackedMoveLive(TileVm tile, GroupVm targetGroup, int insertIdx)
	{
		GroupVm src = _groups.FirstOrDefault((GroupVm g) => g.Tiles.Contains(tile));
		if (src == null)
		{
			return;
		}
		if (src == targetGroup)
		{
			int cur = targetGroup.Tiles.IndexOf(tile);
			int dest = Math.Clamp(insertIdx, 0, targetGroup.Tiles.Count - 1);
			if (cur >= 0 && cur != dest)
			{
				targetGroup.Tiles.Move(cur, dest);
			}
		}
		else
		{
			src.Tiles.Remove(tile);
			targetGroup.Tiles.Insert(Math.Clamp(insertIdx, 0, targetGroup.Tiles.Count), tile);
		}
	}

	private void CommitPackedDrop(System.Windows.Point posInThis)
	{
		//IL_00ff: Unknown result type (might be due to invalid IL or missing references)
		TileVm primary = _dragPrimary;
		_dragPrimary = null;
		bool foldArmed = _packedFoldArmed;
		TileVm foldTarget = _hoverOccupant;
		_packedFoldArmed = false;
		_hoverOccupant = null;
		if (primary == null)
		{
			Profile.Save(_groups);
			return;
		}
		if (foldArmed && foldTarget != null && foldTarget != primary && !foldTarget.IsDesktop && !primary.IsDesktop && (!primary.IsSelected || SelectedTiles().Count() <= 1))
		{
			GroupVm fg = _groups.FirstOrDefault((GroupVm groupVm) => groupVm.Tiles.Contains(foldTarget));
			if (fg != null)
			{
				Folderize(foldTarget, primary, fg);
				return;
			}
		}
		GroupVm target;
		if (IsOverElement(NewGroupZone, posInThis))
		{
			_groups.FirstOrDefault((GroupVm groupVm) => groupVm.Tiles.Contains(primary))?.Tiles.Remove(primary);
			target = new GroupVm();
			_groups.Add(target);
			target.Tiles.Add(primary);
		}
		else
		{
			target = _groups.FirstOrDefault((GroupVm groupVm) => groupVm.Tiles.Contains(primary)) ?? _groups.FirstOrDefault() ?? new GroupVm();
			if (!_groups.Contains(target))
			{
				_groups.Add(target);
			}
		}
		if (primary.IsSelected && SelectedTiles().Count() > 1)
		{
			int at = target.Tiles.IndexOf(primary) + 1;
			foreach (TileVm t in (from tileVm in SelectedTiles()
				where tileVm != primary
				select tileVm).ToList())
			{
				GroupVm s2 = _groups.FirstOrDefault((GroupVm groupVm) => groupVm.Tiles.Contains(t));
				if (s2 == target)
				{
					int cur = target.Tiles.IndexOf(t);
					if (cur < 0)
					{
						continue;
					}
					if (cur < at)
					{
						at--;
					}
					target.Tiles.Move(cur, Math.Clamp(at, 0, target.Tiles.Count - 1));
				}
				else
				{
					s2?.Tiles.Remove(t);
					target.Tiles.Insert(Math.Clamp(at, 0, target.Tiles.Count), t);
				}
				at++;
			}
			ClearSelection();
		}
		foreach (GroupVm g in _groups.Where((GroupVm groupVm) => groupVm.Tiles.Count == 0).ToList())
		{
			_groups.Remove(g);
		}
		foreach (GroupVm g2 in _groups)
		{
			SyncColRowToPackedOrder(g2);
		}
		Profile.Save(_groups);
	}

	private void MigratePackedGridOnce()
	{
		AppSettings s = SettingsStore.Load();
		if (!s.PackedGrid || s.PackedGridMigratedV1)
		{
			return;
		}
		try
		{
			if (File.Exists(Profile.ProfilePath))
			{
				File.Copy(Profile.ProfilePath, Profile.ProfilePath + ".prepacked.bak", overwrite: true);
			}
		}
		catch (Exception ex)
		{
			Logger.Log("Packed-grid backup failed: " + ex.Message);
		}
		foreach (GroupVm g in _groups)
		{
			List<TileVm> order = g.Tiles.OrderBy((TileVm t) => t.HasCell ? (t.Col * 1000 + t.Row) : int.MaxValue).ToList();
			for (int i = 0; i < order.Count; i++)
			{
				int cur = g.Tiles.IndexOf(order[i]);
				if (cur != i && cur >= 0)
				{
					g.Tiles.Move(cur, i);
				}
			}
		}
		Profile.Save(_groups);
		s.PackedGridMigratedV1 = true;
		SettingsStore.Save(s);
		Logger.Log("Packed grid: migrated (profile backed up, tile order aligned to reading order)");
	}

	private static void AlignOrderToReadingOrder(GroupVm g)
	{
		List<TileVm> order = g.Tiles.OrderBy((TileVm t) => t.HasCell ? (t.Col * 1000 + t.Row) : int.MaxValue).ToList();
		for (int i = 0; i < order.Count; i++)
		{
			int cur = g.Tiles.IndexOf(order[i]);
			if (cur != i && cur >= 0)
			{
				g.Tiles.Move(cur, i);
			}
		}
	}

	private static void SyncColRowToPackedOrder(GroupVm g)
	{
		int capacity = Math.Max(1, TilePanel.BandRowCapacity);
		List<bool[]> columns = new List<bool[]>();
		foreach (TileVm t in g.Tiles)
		{
			int cols = Math.Max(1, t.Cols);
			int rows = Math.Min(Math.Max(1, t.Rows), capacity);
			int col = 0;
			while (true)
			{
				bool placed = false;
				for (int row = 0; row + rows <= capacity; row++)
				{
					if (Fits(col, row, cols, rows))
					{
						Occupy(col, row, cols, rows);
						t.Col = col;
						t.Row = row;
						placed = true;
						break;
					}
				}
				if (placed)
				{
					break;
				}
				col++;
			}
		}
		bool Fits(int num, int num2, int num4, int num3)
		{
			if (num < 0 || num2 < 0 || num2 + num3 > capacity)
			{
				return false;
			}
			for (int c = num; c < num + num4; c++)
			{
				while (c >= columns.Count)
				{
					columns.Add(new bool[capacity]);
				}
				for (int r = num2; r < num2 + num3; r++)
				{
					if (columns[c][r])
					{
						return false;
					}
				}
			}
			return true;
		}
		void Occupy(int num, int num3, int num2, int num4)
		{
			for (int c = num; c < num + num2; c++)
			{
				while (c >= columns.Count)
				{
					columns.Add(new bool[capacity]);
				}
				for (int r = num3; r < num3 + num4; r++)
				{
					columns[c][r] = true;
				}
			}
		}
	}

	private void ReAlignPackedOrderV2()
	{
		AppSettings s = SettingsStore.Load();
		if (!s.PackedGrid || s.PackedGridAlignedV2)
		{
			return;
		}
		try
		{
			if (File.Exists(Profile.ProfilePath))
			{
				File.Copy(Profile.ProfilePath, Profile.ProfilePath + ".prealign2.bak", overwrite: true);
			}
		}
		catch (Exception ex)
		{
			Logger.Log("Pre-align(v2) backup failed: " + ex.Message);
		}
		foreach (GroupVm g in _groups)
		{
			AlignOrderToReadingOrder(g);
		}
		Profile.Save(_groups);
		SettingsStore.Update(delegate(AppSettings x)
		{
			x.PackedGridAlignedV2 = true;
		});
		Logger.Log("Packed grid: re-aligned tile order to stored Col/Row (v2 heal, profile backed up)");
	}

	private (GroupVm?, TilePanel?) NearestGroupPanel(System.Windows.Point posInThis)
	{
		//IL_0090: Unknown result type (might be due to invalid IL or missing references)
		//IL_0095: Unknown result type (might be due to invalid IL or missing references)
		//IL_009a: Unknown result type (might be due to invalid IL or missing references)
		//IL_009e: Unknown result type (might be due to invalid IL or missing references)
		//IL_00a2: Unknown result type (might be due to invalid IL or missing references)
		GroupVm bestG = null;
		TilePanel bestP = null;
		double best = double.MaxValue;
		Rect rect = default(Rect);
		foreach (GroupVm g in _groups)
		{
			DependencyObject cp = GroupsHost.ItemContainerGenerator.ContainerFromItem(g);
			if (cp == null)
			{
				continue;
			}
			TilePanel panel = FindVisualChild<TilePanel>(cp);
			if (panel != null && panel.IsVisible)
			{
				System.Windows.Point tl = panel.TransformToVisual(this).Transform(new System.Windows.Point(0.0, 0.0));
				rect = new Rect(tl, panel.RenderSize);
				double dx = ((posInThis.X < rect.Left) ? (rect.Left - posInThis.X) : ((posInThis.X > rect.Right) ? (posInThis.X - rect.Right) : 0.0));
				double dy = ((posInThis.Y < rect.Top) ? (rect.Top - posInThis.Y) : ((posInThis.Y > rect.Bottom) ? (posInThis.Y - rect.Bottom) : 0.0));
				double d = dx * dx + dy * dy;
				if (d < best)
				{
					best = d;
					bestG = g;
					bestP = panel;
				}
			}
		}
		return (bestG, bestP);
	}

	private (GroupVm?, TilePanel?) GroupPanelAt(System.Windows.Point posInThis)
	{
		//IL_0002: Unknown result type (might be due to invalid IL or missing references)
		DependencyObject hit = VisualTreeHelper.HitTest(this, posInThis)?.VisualHit;
		TilePanel panel = FindAncestor<TilePanel>(hit);
		ItemsControl ic = ((panel != null) ? FindAncestor<ItemsControl>((DependencyObject?)(object)panel) : FindAncestor<ItemsControl>(hit));
		if (panel == null)
		{
			panel = ((ic != null) ? FindVisualChild<TilePanel>((DependencyObject?)(object)ic) : null);
		}
		return (ic?.DataContext is GroupVm g && panel != null) ? (g, panel) : (null, null);
	}

	private bool IsOverElement(FrameworkElement el, System.Windows.Point posInThis)
	{
		//IL_002b: Unknown result type (might be due to invalid IL or missing references)
		//IL_0030: Unknown result type (might be due to invalid IL or missing references)
		//IL_0035: Unknown result type (might be due to invalid IL or missing references)
		//IL_0036: Unknown result type (might be due to invalid IL or missing references)
		//IL_0038: Unknown result type (might be due to invalid IL or missing references)
		//IL_003d: Unknown result type (might be due to invalid IL or missing references)
		//IL_0042: Unknown result type (might be due to invalid IL or missing references)
		//IL_0045: Unknown result type (might be due to invalid IL or missing references)
		if (!el.IsVisible)
		{
			return false;
		}
		System.Windows.Point tl = el.TransformToVisual(this).Transform(new System.Windows.Point(0.0, 0.0));
		Rect val = new Rect(tl, el.RenderSize);
		return val.Contains(posInThis);
	}

	private void ShowDropPreviewAt(TilePanel panel, int col, int row, TileVm tile)
	{
		//IL_0028: Unknown result type (might be due to invalid IL or missing references)
		//IL_002d: Unknown result type (might be due to invalid IL or missing references)
		//IL_0032: Unknown result type (might be due to invalid IL or missing references)
		DropFoldGlyph.Visibility = Visibility.Collapsed;
		double pitch = TilePanel.PitchValue;
		System.Windows.Point tl = panel.TransformToVisual(DragOverlay).Transform(new System.Windows.Point((double)col * pitch, (double)row * pitch));
		Canvas.SetLeft(DropPreview, tl.X);
		Canvas.SetTop(DropPreview, tl.Y);
		DropPreview.Width = tile.PixelWidth;
		DropPreview.Height = tile.PixelHeight;
		DropPreview.Visibility = Visibility.Visible;
		SyncDropPreviewZoom();
	}

	private void ShowFoldPreviewAt(TilePanel panel, TileVm occupant)
	{
		//IL_0025: Unknown result type (might be due to invalid IL or missing references)
		//IL_002a: Unknown result type (might be due to invalid IL or missing references)
		//IL_002f: Unknown result type (might be due to invalid IL or missing references)
		double pitch = TilePanel.PitchValue;
		System.Windows.Point tl = panel.TransformToVisual(DragOverlay).Transform(new System.Windows.Point((double)occupant.Col * pitch, (double)occupant.Row * pitch));
		Canvas.SetLeft(DropPreview, tl.X);
		Canvas.SetTop(DropPreview, tl.Y);
		DropPreview.Width = occupant.PixelWidth;
		DropPreview.Height = occupant.PixelHeight;
		DropFoldGlyph.Visibility = Visibility.Visible;
		DropPreview.Visibility = Visibility.Visible;
		SyncDropPreviewZoom();
	}

	private static TileVm? FoldCandidate(GroupVm target, TileVm tile, int col, int row)
	{
		col = Math.Max(0, col);
		row = Math.Max(0, row);
		TileVm occupant = null;
		foreach (TileVm o in target.Tiles)
		{
			if (o == tile || !o.HasCell || col < o.Col || col >= o.Col + o.Cols || row < o.Row || row >= o.Row + o.Rows)
			{
				continue;
			}
			occupant = o;
			break;
		}
		if (occupant == null || occupant.IsDesktop || tile.IsDesktop)
		{
			return null;
		}
		int centerCol = col + tile.Cols / 2;
		int centerRow = row + tile.Rows / 2;
		if (centerCol >= occupant.Col && centerCol < occupant.Col + occupant.Cols && centerRow >= occupant.Row && centerRow < occupant.Row + occupant.Rows)
		{
			return occupant;
		}
		return null;
	}

	private void HideDropPreview()
	{
		DropPreview.Visibility = Visibility.Collapsed;
		DropPreview.Opacity = 1.0;
		DropFoldGlyph.Visibility = Visibility.Collapsed;
	}

	private void OnAutoArrange(object sender, RoutedEventArgs e)
	{
		// Deterministic reading-order pack, capped to the visible screen height (authentic Win8.1):
		// keep each group's current reading order, then assign EXPLICIT Col/Row column-major within the
		// screen-height band so tiles flow into new columns instead of being scattered by first-fit.
		TilePanel.BandRowCapacity = StartRowCapacity();
		foreach (GroupVm g in _groups)
		{
			AlignOrderToReadingOrder(g);
			SyncColRowToPackedOrder(g);
		}
		Profile.Save(_groups);
		RepackTiles();
	}

	private static T? FindVisualChild<T>(DependencyObject? root) where T : DependencyObject
	{
		if (root == null)
		{
			return default(T);
		}
		int n = VisualTreeHelper.GetChildrenCount(root);
		for (int i = 0; i < n; i++)
		{
			DependencyObject c = VisualTreeHelper.GetChild(root, i);
			T t = (T)(object)((c is T) ? c : null);
			if (t != null)
			{
				return t;
			}
			T r = FindVisualChild<T>(c);
			if (r != null)
			{
				return r;
			}
		}
		return default(T);
	}

	private void PlaceTileAt(TileVm tile, GroupVm target, int col, int row)
	{
		col = Math.Max(0, col);
		row = Math.Max(0, row);
		if (tile.IsSelected && SelectedTiles().Count() > 1)
		{
			MoveSelection(SelectedTiles().ToList(), tile, target, col, row);
			return;
		}
		GroupVm source = _groups.FirstOrDefault((GroupVm g) => g.Tiles.Contains(tile));
		int origCol = tile.Col;
		int origRow = tile.Row;
		bool hadCell = tile.HasCell;
		TileVm occupant = null;
		foreach (TileVm o in target.Tiles)
		{
			if (o == tile || !o.HasCell || col < o.Col || col >= o.Col + o.Cols || row < o.Row || row >= o.Row + o.Rows)
			{
				continue;
			}
			occupant = o;
			break;
		}
		if (occupant == _hoverOccupant && Environment.TickCount - _hoverSince >= 600 && occupant != null && occupant != tile && !occupant.IsDesktop && !tile.IsDesktop)
		{
			int centerCol = col + tile.Cols / 2;
			int centerRow = row + tile.Rows / 2;
			if (centerCol >= occupant.Col && centerCol < occupant.Col + occupant.Cols && centerRow >= occupant.Row && centerRow < occupant.Row + occupant.Rows)
			{
				Folderize(occupant, tile, target);
				return;
			}
		}
		if (source != target)
		{
			source?.Tiles.Remove(tile);
			target.Tiles.Add(tile);
			if (source != null && source != target && source.Tiles.Count == 0)
			{
				_groups.Remove(source);
			}
		}
		if (((occupant != null) & hadCell) && source == target)
		{
			tile.Col = occupant.Col;
			tile.Row = occupant.Row;
			occupant.Col = origCol;
			occupant.Row = origRow;
			(int, int) tuple = ResolveCell(target, occupant, origCol, origRow);
			int rc = tuple.Item1;
			int rr = tuple.Item2;
			occupant.Col = rc;
			occupant.Row = rr;
		}
		else
		{
			(col, row) = ResolveCell(target, tile, col, row);
			tile.Col = col;
			tile.Row = row;
		}
		AlignOrderToReadingOrder(target);
		ScheduleSave();
	}

	private void MoveSelection(List<TileVm> selected, TileVm primary, GroupVm target, int dropCol, int dropRow)
	{
		int baseCol = (primary.HasCell ? primary.Col : dropCol);
		int baseRow = (primary.HasCell ? primary.Row : dropRow);
		int dCol = dropCol - baseCol;
		int dRow = dropRow - baseRow;
		foreach (TileVm t in selected)
		{
			GroupVm src = _groups.FirstOrDefault((GroupVm g) => g.Tiles.Contains(t));
			if (src != target)
			{
				src?.Tiles.Remove(t);
				target.Tiles.Add(t);
				if (src != null && src.Tiles.Count == 0)
				{
					_groups.Remove(src);
				}
			}
		}
		foreach (TileVm t2 in selected)
		{
			if (t2.HasCell)
			{
				t2.Col = Math.Max(0, t2.Col + dCol);
				t2.Row = Math.Max(0, t2.Row + dRow);
				continue;
			}
			(int, int) tuple = ResolveCell(target, t2, dropCol, dropRow);
			int c = tuple.Item1;
			int r = tuple.Item2;
			t2.Col = c;
			t2.Row = r;
		}
		ClearSelection();
		AlignOrderToReadingOrder(target);
		ScheduleSave();
	}

	private void Folderize(TileVm targetTile, TileVm dragged, GroupVm targetGroup)
	{
		List<AppEntry> incoming = (dragged.IsFolder ? dragged.Members.ToList() : new List<AppEntry> { dragged.Entry });
		GroupVm src = _groups.FirstOrDefault((GroupVm g) => g.Tiles.Contains(dragged));
		src?.Tiles.Remove(dragged);
		if (src != null && src != targetGroup && src.Tiles.Count == 0)
		{
			_groups.Remove(src);
		}
		if (dragged.IsFolder)
		{
			dragged.Members.Clear();
		}
		if (targetTile.IsFolder)
		{
			foreach (AppEntry a in incoming)
			{
				if (!targetTile.Members.Contains(a))
				{
					targetTile.Members.Add(a);
				}
			}
			targetTile.RebuildFolderIcon();
		}
		else
		{
			List<AppEntry> members = new List<AppEntry> { targetTile.Entry };
			foreach (AppEntry a2 in incoming)
			{
				if (!members.Contains(a2))
				{
					members.Add(a2);
				}
			}
			string name = SuggestGroupName(members.Select((AppEntry entry) => new TileVm
			{
				Entry = entry
			}));
			if (name == "New group")
			{
				name = "Apps";
			}
			TileVm folder = FolderTiles.MakeFolder(name, members, targetTile.Size);
			folder.Col = targetTile.Col;
			folder.Row = targetTile.Row;
			int idx = targetGroup.Tiles.IndexOf(targetTile);
			targetGroup.Tiles.Remove(targetTile);
			if (idx >= 0 && idx <= targetGroup.Tiles.Count)
			{
				targetGroup.Tiles.Insert(idx, folder);
			}
			else
			{
				targetGroup.Tiles.Add(folder);
			}
		}
		ClearSelection();
		Profile.Save(_groups);
	}

	private void RemoveFromFolder(TileVm folder, AppEntry member)
	{
		if (!folder.Members.Remove(member))
		{
			return;
		}
		GroupVm group = _groups.FirstOrDefault((GroupVm g) => g.Tiles.Contains(folder)) ?? _groups.FirstOrDefault();
		if (group == null)
		{
			Profile.Save(_groups);
			return;
		}
		group.Tiles.Add(new TileVm
		{
			Entry = member
		});
		if (folder.Members.Count <= 1)
		{
			int idx = group.Tiles.IndexOf(folder);
			int col = folder.Col;
			int row = folder.Row;
			TileSize size = folder.Size;
			AppEntry last = ((folder.Members.Count == 1) ? folder.Members[0] : null);
			group.Tiles.Remove(folder);
			folder.Members.Clear();
			if (last != null)
			{
				TileVm tile = new TileVm
				{
					Entry = last,
					Size = size,
					Col = col,
					Row = row
				};
				if (idx >= 0 && idx <= group.Tiles.Count)
				{
					group.Tiles.Insert(idx, tile);
				}
				else
				{
					group.Tiles.Add(tile);
				}
			}
		}
		else
		{
			folder.RebuildFolderIcon();
		}
		Profile.Save(_groups);
	}

	private void Ungroup(TileVm folder)
	{
		if (!folder.IsFolder)
		{
			return;
		}
		GroupVm group = _groups.FirstOrDefault((GroupVm g) => g.Tiles.Contains(folder));
		if (group == null)
		{
			return;
		}
		int idx = group.Tiles.IndexOf(folder);
		int col = folder.Col;
		int row = folder.Row;
		TileSize size = folder.Size;
		List<AppEntry> members = folder.Members.ToList();
		group.Tiles.Remove(folder);
		folder.Members.Clear();
		bool first = true;
		foreach (AppEntry m in members)
		{
			TileVm tile = (first ? new TileVm
			{
				Entry = m,
				Size = size,
				Col = col,
				Row = row
			} : new TileVm
			{
				Entry = m
			});
			if (idx >= 0 && idx <= group.Tiles.Count)
			{
				group.Tiles.Insert(idx, tile);
				idx++;
			}
			else
			{
				group.Tiles.Add(tile);
			}
			first = false;
		}
		Profile.Save(_groups);
	}

	private ContentPresenter? FindTileContainer(TileVm tile)
	{
		return Search((DependencyObject)(object)this);
		ContentPresenter? Search(DependencyObject root)
		{
			int n = VisualTreeHelper.GetChildrenCount(root);
			for (int i = 0; i < n; i++)
			{
				DependencyObject c = VisualTreeHelper.GetChild(root, i);
				if (c is ContentPresenter cp && cp.DataContext == tile && VisualTreeHelper.GetParent((DependencyObject)(object)cp) is TilePanel)
				{
					return cp;
				}
				ContentPresenter found = Search(c);
				if (found != null)
				{
					return found;
				}
			}
			return null;
		}
	}

	public void RepackTiles()
	{
		foreach (GroupVm g in _groups)
		{
			DependencyObject c = GroupsHost.ItemContainerGenerator.ContainerFromItem(g);
			if (c != null)
			{
				FindVisualChild<TilePanel>(c)?.InvalidateMeasure();
			}
		}
	}

	private void FreezeCell(TileVm tile)
	{
		if (!tile.HasCell)
		{
			ContentPresenter cp = FindTileContainer(tile);
			if (cp != null && VisualTreeHelper.GetParent((DependencyObject)(object)cp) is TilePanel panel && panel.TryGetCell(cp, out var col, out var row))
			{
				tile.Col = col;
				tile.Row = row;
			}
		}
	}

	private void UpdateBandCapacity()
	{
		int cap = StartRowCapacity();
		if (cap != TilePanel.BandRowCapacity)
		{
			TilePanel.BandRowCapacity = cap;
			RepackTiles();
		}
	}

	private int StartRowCapacity()
	{
		ScrollViewer startScroller = StartScroller;
		double h = ((startScroller != null && startScroller.ActualHeight > 120.0) ? StartScroller.ActualHeight : 0.0);
		if (h < 120.0)
		{
			double win = ((base.ActualHeight > 200.0) ? base.ActualHeight : SystemParameters.PrimaryScreenHeight);
			h = win - 168.0;
		}
		// Cap the band to what the VIEWPORT can show (authentic Win8.1): tiles that don't fit flow into
		// new COLUMNS (horizontal scroll), never stacking below the screen. Do NOT inflate to the stored
		// layout's required rows Ã¢â‚¬â€ that was the old anti-reflow hack and it caused permanent vertical
		// overflow once any tile sat past the screen-fit row.
		int byHeight = Math.Clamp(TilePanel.RowsForHeight(h), 6, 20);
		return byHeight;
	}

	private int RequiredBandRows()
	{
		int max = 0;
		foreach (GroupVm g in _groups)
		{
			foreach (TileVm t in g.Tiles)
			{
				if (t.Row >= 0)
				{
					max = Math.Max(max, t.Row + Math.Max(1, t.Rows));
				}
			}
		}
		return max;
	}

	private void FillGridToHeightOnce()
	{
		AppSettings s = SettingsStore.Load();
		if (s.StartGridFilledV1)
		{
			return;
		}
		if (_groups.SelectMany((GroupVm g) => g.Tiles).Any((TileVm tileVm) => tileVm.Col >= 0 || tileVm.Row >= 0))
		{
			Logger.Log("FillGridToHeightOnce SKIPPED: profile already positioned - protecting the saved layout");
			return;
		}
		foreach (TileVm t in _groups.SelectMany((GroupVm g) => g.Tiles))
		{
			t.Col = -1;
			t.Row = -1;
		}
		SettingsStore.Update(delegate(AppSettings x)
		{
			x.StartGridFilledV1 = true;
		});
		Logger.Log("Start grid: one-time reflow to fill screen height");
	}

	private void AssignInitialLayout()
	{
		int startRows = StartRowCapacity();
		bool changed = false;
		foreach (GroupVm g in _groups)
		{
			HashSet<(int, int)> used = new HashSet<(int, int)>();
			foreach (TileVm t in g.Tiles)
			{
				if (t.HasCell)
				{
					Occupy(t);
				}
			}
			foreach (TileVm t2 in g.Tiles)
			{
				if (t2.HasCell)
				{
					continue;
				}
				int col = 0;
				while (true)
				{
					bool placed = false;
					for (int row = 0; row + t2.Rows <= startRows; row++)
					{
						if (Free(col, row, t2.Cols, t2.Rows))
						{
							t2.Col = col;
							t2.Row = row;
							Occupy(t2);
							placed = true;
							changed = true;
							break;
						}
					}
					if (placed)
					{
						break;
					}
					col++;
				}
			}
			bool Free(int num, int num2, int cols, int rows)
			{
				if (num < 0 || num2 < 0 || num2 + rows > startRows)
				{
					return false;
				}
				for (int c = num; c < num + cols; c++)
				{
					for (int r = num2; r < num2 + rows; r++)
					{
						if (used.Contains((c, r)))
						{
							return false;
						}
					}
				}
				return true;
			}
			void Occupy(TileVm tileVm)
			{
				for (int c = tileVm.Col; c < tileVm.Col + tileVm.Cols; c++)
				{
					for (int r = tileVm.Row; r < tileVm.Row + tileVm.Rows; r++)
					{
						used.Add((c, r));
					}
				}
			}
		}
		if (changed)
		{
			Profile.Save(_groups);
			Logger.Log("Start layout assigned (all tiles explicit)");
		}
	}

	private static (int, int) ResolveCell(GroupVm group, TileVm tile, int col, int row)
	{
		int cols = tile.Cols;
		int rows = tile.Rows;
		if (!Overlaps(col, row))
		{
			return (col, row);
		}
		for (int radius = 1; radius < 60; radius++)
		{
			for (int dc = -radius; dc <= radius; dc++)
			{
				for (int dr = -radius; dr <= radius; dr++)
				{
					if (Math.Abs(dc) == radius || Math.Abs(dr) == radius)
					{
						int c = col + dc;
						int r = row + dr;
						if (c >= 0 && r >= 0 && !Overlaps(c, r))
						{
							return (c, r);
						}
					}
				}
			}
		}
		return (col, row);
		bool Overlaps(int num, int num2)
		{
			foreach (TileVm o in group.Tiles)
			{
				if (o != tile && o.HasCell && num < o.Col + o.Cols && o.Col < num + cols && num2 < o.Row + o.Rows && o.Row < num2 + rows)
				{
					return true;
				}
			}
			return false;
		}
	}

	private static T? FindAncestor<T>(DependencyObject? d) where T : DependencyObject
	{
		while (d != null && !(d is T))
		{
			d = VisualTreeHelper.GetParent(d);
		}
		return (T)(object)((d is T) ? d : null);
	}

	private void HideNewGroupVisual()
	{
		NewGroupVisual.Opacity = 0.0;
	}

	private void MoveTileLive(TileVm tile, GroupVm target, int index)
	{
		GroupVm source = _groups.FirstOrDefault((GroupVm g) => g.Tiles.Contains(tile));
		if (source != null)
		{
			int oldIndex = source.Tiles.IndexOf(tile);
			if (source == target && oldIndex == index)
			{
				return;
			}
			source.Tiles.RemoveAt(oldIndex);
			if (source == target && oldIndex < index)
			{
				index--;
			}
		}
		index = Math.Clamp(index, 0, target.Tiles.Count);
		target.Tiles.Insert(index, tile);
		tile.Col = -1;
		tile.Row = -1;
		if (source != null && source != target && source.Tiles.Count == 0)
		{
			_groups.Remove(source);
		}
	}

	private static T? FindDescendant<T>(DependencyObject root, string name) where T : FrameworkElement
	{
		for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
		{
			DependencyObject child = VisualTreeHelper.GetChild(root, i);
			if (child is T fe && fe.Name == name)
			{
				return fe;
			}
			T nested = FindDescendant<T>(child, name);
			if (nested != null)
			{
				return nested;
			}
		}
		return null;
	}

	private void BeginGroupRename(FrameworkElement headerScope)
	{
		if (headerScope.DataContext is GroupVm group)
		{
			_renameOriginal = group.Name;
			group.IsEditing = true;
		}
	}

	private void OnGroupEditVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
	{
		System.Windows.Controls.TextBox tb = sender as System.Windows.Controls.TextBox;
		if (tb == null)
		{
			return;
		}
		if (!tb.IsVisible)
		{
			if (_activeGroupEdit == tb)
			{
				_activeGroupEdit = null;
				_activeGroupEditScope = null;
			}
			return;
		}
		_activeGroupEdit = tb;
		_activeGroupEditScope = FindNamedAncestor(tb, "GroupEditPanel") ?? (FrameworkElement)tb;
		tb.Focus();
		tb.SelectAll();
		((DispatcherObject)this).Dispatcher.BeginInvoke((Delegate)(Action)delegate
		{
			if (tb.IsVisible && !tb.IsKeyboardFocusWithin)
			{
				tb.Focus();
				tb.SelectAll();
			}
		}, (DispatcherPriority)5, Array.Empty<object>());
	}

	private void OnGroupHeaderMouseDown(object sender, MouseButtonEventArgs e)
	{
		if (sender is FrameworkElement scope && (e.ClickCount == 2 || _customiseActive))
		{
			BeginGroupRename(scope);
			e.Handled = true;
		}
	}

	private void OnRenameGroup(object sender, RoutedEventArgs e)
	{
		if (sender is System.Windows.Controls.MenuItem { Parent: System.Windows.Controls.ContextMenu { PlacementTarget: FrameworkElement scope } })
		{
			TaskbarContextMenu.QueueCommand(delegate { BeginGroupRename(scope); });
		}
	}

	private void OnGroupNameEditKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
	{
		//IL_002b: Unknown result type (might be due to invalid IL or missing references)
		//IL_0032: Invalid comparison between Unknown and I4
		//IL_006a: Unknown result type (might be due to invalid IL or missing references)
		//IL_0070: Invalid comparison between Unknown and I4
		if (sender is System.Windows.Controls.TextBox { DataContext: GroupVm group })
		{
			if ((int)e.Key == 13)
			{
				group.Name = _renameOriginal ?? group.Name;
				group.IsEditing = false;
				Keyboard.ClearFocus();
				e.Handled = true;
			}
			else if ((int)e.Key == 6)
			{
				group.Name = (group.Name ?? "").Trim();
				group.IsEditing = false;
				Keyboard.ClearFocus();
				e.Handled = true;
			}
		}
	}

	private void OnGroupNameEditLostFocus(object sender, RoutedEventArgs e)
	{
		if (sender is System.Windows.Controls.TextBox lostBox && _activeGroupEdit == lostBox)
		{
			_activeGroupEdit = null;
			_activeGroupEditScope = null;
		}
		if (sender is System.Windows.Controls.TextBox { DataContext: GroupVm group })
		{
			group.IsEditing = false;
			if (group.Name != _renameOriginal)
			{
				ScheduleSave();
			}
			_renameOriginal = null;
		}
		else
		{
			_renameOriginal = null;
		}
	}

	// Save (✓) button in the rename panel — same as pressing Enter: trim + commit the typed name, stop editing.
	// Focus-neutral (the Border is not focusable) so the TextBox keeps focus until we clear it here; e.Handled stops
	// the root click-away handler from also acting.
	private void OnGroupRenameCommit(object sender, MouseButtonEventArgs e)
	{
		if (sender is FrameworkElement { DataContext: GroupVm group })
		{
			group.Name = (group.Name ?? "").Trim();
			group.IsEditing = false;
			Keyboard.ClearFocus();
			e.Handled = true;
		}
	}

	// Cancel (✕) button — same as pressing Esc: revert to the original name, stop editing.
	private void OnGroupRenameCancel(object sender, MouseButtonEventArgs e)
	{
		if (sender is FrameworkElement { DataContext: GroupVm group })
		{
			group.Name = _renameOriginal ?? group.Name;
			group.IsEditing = false;
			Keyboard.ClearFocus();
			e.Handled = true;
		}
	}

	// Walk the visual tree UP from `start` to the first FrameworkElement with the given x:Name (null if none).
	private static FrameworkElement? FindNamedAncestor(DependencyObject start, string name)
	{
		DependencyObject cur = start;
		while (cur != null)
		{
			if (cur is FrameworkElement fe && fe.Name == name)
			{
				return fe;
			}
			cur = System.Windows.Media.VisualTreeHelper.GetParent(cur);
		}
		return null;
	}

	// DEEP PIN: pin this group as a launcher://workspace/<name> tile on Start. Clicking it routes through ActionRouter to
	// restore/launch the workspace — same backend as Search and the group menu. (Survives restart with its name; brush/icon
	// go generic on reload — cosmetic.)
	private void OnPinWorkspaceTile(object sender, RoutedEventArgs e)
	{
		if (sender is not System.Windows.Controls.MenuItem { DataContext: GroupVm group } || string.IsNullOrWhiteSpace(group.Name))
		{
			return;
		}
		try
		{
			SolidColorBrush brush = new SolidColorBrush(StartAccent.Color());
			((System.Windows.Freezable)brush).Freeze();
			AppEntry entry = new AppEntry
			{
				Name = group.Name + " workspace",
				LaunchPath = ActionRouter.Scheme + "workspace/" + Uri.EscapeDataString(group.Name),
				TileBrush = brush,
				Icon = GlyphImage(57609, 22.0)
			};
			PinEntryToStart(entry);
			Profile.Save(_groups);
			Logger.Log("[deep-pin] pinned workspace tile for '" + group.Name + "'");
		}
		catch (Exception ex)
		{
			Logger.Log("Pin workspace tile: " + ex.Message);
		}
	}

	// Group names that are usable as a "workspace" (have at least one real app tile) — surfaced by the Search command palette.
	public System.Collections.Generic.IReadOnlyList<string> WorkspaceNames()
	{
		try
		{
			return _groups
				.Where((GroupVm g) => !string.IsNullOrWhiteSpace(g.Name) && g.Tiles.Any((TileVm t) => t.Entry != null && !t.IsFolder && !t.IsDesktop))
				.Select((GroupVm g) => g.Name)
				.Distinct(StringComparer.OrdinalIgnoreCase)
				.ToList();
		}
		catch (Exception ex)
		{
			Logger.Log("WorkspaceNames: " + ex.Message);
			return System.Array.Empty<string>();
		}
	}

	// Launch a workspace by group name from Search: restore its saved window layout if present, else launch all its apps.
	public void LaunchWorkspaceByName(string name)
	{
		try
		{
			GroupVm group = _groups.FirstOrDefault((GroupVm g) => string.Equals(g.Name, name, StringComparison.OrdinalIgnoreCase));
			if (group == null)
			{
				return;
			}
			if (group.SavedLayout != null && group.SavedLayout.Count > 0)
			{
				Workspace.Restore(group.SavedLayout);
			}
			else
			{
				foreach (TileVm t in group.Tiles.ToList())
				{
					if (t.Entry != null && !t.IsFolder && !t.IsDesktop)
					{
						Launch(t.Entry, asAdmin: false, keepOpen: true);
					}
				}
			}
			if (base.IsVisible)
			{
				HideStart(animate: true);
			}
		}
		catch (Exception ex)
		{
			Logger.Log("LaunchWorkspaceByName: " + ex.Message);
		}
	}

	// WORKSPACE: snapshot the current on-screen window layout for this group's running apps (position/size/state/monitor).
	private void OnSaveGroupLayout(object sender, RoutedEventArgs e)
	{
		if (sender is not System.Windows.Controls.MenuItem { DataContext: GroupVm group })
		{
			return;
		}
		try
		{
			IEnumerable<(string, string?, string)> apps = group.Tiles.ToList()
				.Where((TileVm t) => t.Entry != null && !t.IsFolder && !t.IsDesktop)
				.Select((TileVm t) => (t.Entry.LaunchPath, t.Entry.AppId, t.Entry.Name));
			group.SavedLayout = Workspace.Capture(apps);
			Profile.Save(_groups);
			Logger.Log($"[workspace] saved {group.SavedLayout.Count} window(s) for group '{group.Name}'");
		}
		catch (Exception ex)
		{
			Logger.Log("Save group layout failed: " + ex.Message);
		}
	}

	// WORKSPACE: restore the saved layout — reposition running windows, relaunch+position missing apps. If nothing was
	// saved, fall back to "Launch all apps" so the command is never a dead no-op.
	private void OnRestoreGroupLayout(object sender, RoutedEventArgs e)
	{
		if (sender is not System.Windows.Controls.MenuItem { DataContext: GroupVm group })
		{
			return;
		}
		if (group.SavedLayout == null || group.SavedLayout.Count == 0)
		{
			OnLaunchAllInGroup(sender, e);
			return;
		}
		try
		{
			Workspace.Restore(group.SavedLayout);
		}
		catch (Exception ex)
		{
			Logger.Log("Restore group layout failed: " + ex.Message);
		}
		HideStart(animate: true);
	}

	// Group-level "Launch all apps" (Win8.1-workspace-style): fires every real app tile in the group, then closes Start once.
	private void OnLaunchAllInGroup(object sender, RoutedEventArgs e)
	{
		if (sender is not System.Windows.Controls.MenuItem { DataContext: GroupVm group })
		{
			return;
		}
		int launched = 0;
		foreach (TileVm t in group.Tiles.ToList())
		{
			if (t.Entry != null && !t.IsFolder && !t.IsDesktop)
			{
				try
				{
					Launch(t.Entry, asAdmin: false, keepOpen: true);
					launched++;
				}
				catch (Exception ex)
				{
					Logger.Log("Launch all in group: " + ex.Message);
				}
			}
		}
		if (launched > 0)
		{
			HideStart(animate: true);
		}
	}

	// Group reordering via the header menu — a deterministic _groups.Move (the ObservableCollection reorders the bound
	// GroupsHost, gliding into place). Reliable alternative to a drag-to-reorder gesture.
	private void OnMoveGroupLeft(object sender, RoutedEventArgs e)
	{
		if (sender is System.Windows.Controls.MenuItem { DataContext: GroupVm group })
		{
			int i = _groups.IndexOf(group);
			if (i > 0)
			{
				_groups.Move(i, i - 1);
				Profile.Save(_groups);
			}
		}
	}

	private void OnMoveGroupRight(object sender, RoutedEventArgs e)
	{
		if (sender is System.Windows.Controls.MenuItem { DataContext: GroupVm group })
		{
			int i = _groups.IndexOf(group);
			if (i >= 0 && i < _groups.Count - 1)
			{
				_groups.Move(i, i + 1);
				Profile.Save(_groups);
			}
		}
	}

	private void OnNewGroupAfter(object sender, RoutedEventArgs e)
	{
		if (sender is System.Windows.Controls.MenuItem { DataContext: GroupVm group })
		{
			TaskbarContextMenu.QueueCommand(delegate
			{
				int index = _groups.IndexOf(group);
				_groups.Insert(index + 1, new GroupVm { Name = "New group" });
				Profile.Save(_groups);
			});
		}
	}

	private void OnDeleteGroup(object sender, RoutedEventArgs e)
	{
		if (sender is System.Windows.Controls.MenuItem { DataContext: GroupVm group } && group.Tiles.Count <= 0)
		{
			TaskbarContextMenu.QueueCommand(delegate
			{
				_groups.Remove(group);
				Profile.Save(_groups);
			});
		}
	}

	private void ScheduleAppsViewWarmup()
	{
		if (_appsView == null || _appsViewBuilt || _appsWarmupOperation != null)
		{
			return;
		}
		_appsWarmupOperation = ((DispatcherObject)this).Dispatcher.BeginInvoke((Delegate)(Action)delegate
		{
			_appsWarmupOperation = null;
			if (_appsView == null || _appsViewBuilt)
			{
				return;
			}
			Stopwatch warmup = Stopwatch.StartNew();
			BuildAppsView();
			PremeasureAppsView();
			warmup.Stop();
			Logger.Log($"Apps view prewarmed at dispatcher idle in {warmup.ElapsedMilliseconds} ms");
		}, DispatcherPriority.ContextIdle, Array.Empty<object>());
	}

	private void PremeasureAppsView()
	{
		Visibility previous = AppsScroller.Visibility;
		if (previous == Visibility.Collapsed)
		{
			AppsScroller.Visibility = Visibility.Hidden;
		}
		double width = Math.Max(320.0, SystemParameters.PrimaryScreenWidth - 80.0);
		double height = Math.Max(176.0, SystemParameters.PrimaryScreenHeight - 160.0);
		AppsScroller.Measure(new System.Windows.Size(width, height));
		AppsScroller.Arrange(new Rect(0.0, 0.0, width, height));
		if (previous == Visibility.Collapsed)
		{
			AppsScroller.Visibility = Visibility.Collapsed;
		}
	}

	private void ApplyAppsSort(AppsSort mode, bool rebuild = true)
	{
		_appsSort = mode;
		if (_appsView is ListCollectionView lcv)
		{
			lcv.CustomSort = new AppsComparer(mode);
			TextBlock sortLabel = SortLabel;
			if (1 == 0)
			{
			}
			string text = mode switch
			{
				AppsSort.DateInstalled => "by date installed", 
				AppsSort.MostUsed => "by most used", 
				AppsSort.Category => "by category", 
				_ => "by name", 
			};
			if (1 == 0)
			{
			}
			sortLabel.Text = text;
			if (rebuild)
			{
				BuildAppsView();
				DoubleAnimation fade = new DoubleAnimation(0.35, 1.0, Motion.Dur(Motion.Cat.Micro))
				{
					EasingFunction = Motion.Ease(Motion.Cat.Micro)
				};
				AppsHost.BeginAnimation(UIElement.OpacityProperty, fade);
			}
		}
	}

	private void BuildAppsView()
	{
		if (_appsView == null)
		{
			return;
		}
		Stopwatch stopwatch = Stopwatch.StartNew();
		AppsHost.Children.Clear();
		try
		{
			base.Resources["HeaderAccent"] = new SolidColorBrush(StartAccent.Tint(0.6));
		}
		catch
		{
		}
		bool searching = !string.IsNullOrEmpty(_searchQuery);
		if (searching)
		{
			foreach (SearchExtras.Setting setting in SearchExtras.MatchingSettings(_searchQuery).Take(6))
			{
				AppsHost.Children.Add(BuildSettingButton(setting));
			}
		}
		int count = 0;
		string lastKey = null;
		foreach (AppEntry entry in (IEnumerable)_appsView)
		{
			if (!searching)
			{
				string key = GroupHeaderFor(entry);
				if (key != null && key != lastKey)
				{
					AppsHost.Children.Add(BuildHeader(key));
					lastKey = key;
				}
			}
			AppsHost.Children.Add(GetAppButton(entry));
			count++;
		}
		if (searching)
		{
			AppsHost.Children.Add(BuildWebSearchButton(_searchQuery, count));
		}
		_appsViewBuilt = true;
		stopwatch.Stop();
		Logger.Log($"Apps view built: {count} apps, {AppsHost.Children.Count} rows in {stopwatch.ElapsedMilliseconds} ms (search={searching})");
	}

	private string? GroupHeaderFor(AppEntry e)
	{
		AppsSort appsSort = _appsSort;
		if (1 == 0)
		{
		}
		string result = appsSort switch
		{
			AppsSort.Name => e.GroupLetter, 
			AppsSort.Category => string.IsNullOrEmpty(e.Category) ? "Other" : e.Category, 
			AppsSort.DateInstalled => DateBucket(e), 
			AppsSort.MostUsed => UsageBucket(e), 
			_ => null, 
		};
		if (1 == 0)
		{
		}
		return result;
	}

	private static string DateBucket(AppEntry e)
	{
		long ticks = e.InstallDate?.Ticks ?? UsageStore.FirstSeen(e.LaunchPath);
		if (ticks <= 0)
		{
			return "Earlier";
		}
		double days = (DateTime.Now - new DateTime(ticks)).TotalDays;
		if (1 == 0)
		{
		}
		string result = ((days < 1.0) ? "Today" : ((days < 2.0) ? "Yesterday" : ((days < 7.0) ? "This week" : ((days < 31.0) ? "This month" : ((!(days < 366.0)) ? "Earlier" : "This year")))));
		if (1 == 0)
		{
		}
		return result;
	}

	private static string UsageBucket(AppEntry e)
	{
		int num = UsageStore.Count(e.LaunchPath);
		if (1 == 0)
		{
		}
		string result = ((num >= 3) ? ((num < 10) ? "Frequently used" : "Most used") : ((num < 1) ? "Rarely used" : "Occasionally used"));
		if (1 == 0)
		{
		}
		return result;
	}

	private System.Windows.Controls.Button BuildWebSearchButton(string query, int appHits)
	{
		TextBlock glyph = new TextBlock
		{
			Text = "\ue721",
			FontFamily = new System.Windows.Media.FontFamily("Segoe MDL2 Assets"),
			FontSize = 18.0,
			Foreground = System.Windows.Media.Brushes.White,
			VerticalAlignment = VerticalAlignment.Center,
			Width = 32.0,
			TextAlignment = TextAlignment.Center
		};
		TextBlock label = new TextBlock
		{
			Text = ((appHits == 0) ? ("No apps found. Search the web for \"" + query + "\"") : ("Search the web for \"" + query + "\"")),
			Foreground = System.Windows.Media.Brushes.White,
			FontFamily = new System.Windows.Media.FontFamily("Segoe UI"),
			FontSize = 14.0,
			VerticalAlignment = VerticalAlignment.Center,
			Margin = new Thickness(10.0, 0.0, 0.0, 0.0),
			TextTrimming = TextTrimming.CharacterEllipsis,
			Width = 190.0
		};
		StackPanel panel = new StackPanel
		{
			Orientation = System.Windows.Controls.Orientation.Horizontal
		};
		Border accent = new Border
		{
			Width = 32.0,
			Height = 32.0,
			Child = glyph
		};
		accent.SetResourceReference(Border.BackgroundProperty, "SearchAccent");
		panel.Children.Add(accent);
		panel.Children.Add(label);
		System.Windows.Controls.Button btn = new System.Windows.Controls.Button
		{
			Style = (Style)FindResource("ResultRowButton"),
			Content = panel,
			Width = 248.0,
			Height = 44.0,
			HorizontalContentAlignment = System.Windows.HorizontalAlignment.Left,
			Padding = new Thickness(6.0),
			Cursor = System.Windows.Input.Cursors.Hand
		};
		btn.Click += delegate
		{
			WebSearch(query);
		};
		return btn;
	}

	private System.Windows.Controls.Button BuildSettingButton(SearchExtras.Setting setting)
	{
		TextBlock glyph = new TextBlock
		{
			Text = "\ue713",
			FontFamily = new System.Windows.Media.FontFamily("Segoe MDL2 Assets"),
			FontSize = 16.0,
			Foreground = System.Windows.Media.Brushes.White,
			HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
			VerticalAlignment = VerticalAlignment.Center
		};
		Border accent = new Border
		{
			Width = 32.0,
			Height = 32.0,
			Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0, 130, 135)),
			Child = glyph
		};
		TextBlock label = new TextBlock
		{
			Text = setting.Name,
			Foreground = System.Windows.Media.Brushes.White,
			FontFamily = new System.Windows.Media.FontFamily("Segoe UI"),
			FontSize = 14.0,
			VerticalAlignment = VerticalAlignment.Center,
			Margin = new Thickness(10.0, 0.0, 0.0, 0.0),
			TextTrimming = TextTrimming.CharacterEllipsis,
			Width = 190.0
		};
		StackPanel panel = new StackPanel
		{
			Orientation = System.Windows.Controls.Orientation.Horizontal
		};
		panel.Children.Add(accent);
		panel.Children.Add(label);
		System.Windows.Controls.Button btn = new System.Windows.Controls.Button
		{
			Style = (Style)FindResource("ResultRowButton"),
			Content = panel,
			Width = 248.0,
			Height = 44.0,
			HorizontalContentAlignment = System.Windows.HorizontalAlignment.Left,
			Padding = new Thickness(6.0),
			Cursor = System.Windows.Input.Cursors.Hand,
			ToolTip = setting.Name
		};
		btn.Click += delegate
		{
			OpenRecent(setting.Uri);
		};
		return btn;
	}

	private void WebSearch(string query)
	{
		try
		{
			WebOpen.Url(PlaceSearchService.BuildWebSearchUrl(query, SettingsStore.FastSnapshot.PlaceSearchEngine));
			HideStart();
		}
		catch (Exception ex)
		{
			Logger.Log("Web search failed: " + ex.Message);
		}
	}

	private FrameworkElement BuildHeader(string letter)
	{
		bool isLetter = letter.Length <= 2;
		TextBlock t = new TextBlock
		{
			Text = letter,
			FontFamily = new System.Windows.Media.FontFamily(isLetter ? "Segoe UI Light" : "Segoe UI Semibold"),
			FontSize = (isLetter ? 24 : 17),
			Margin = new Thickness(6.0, 0.0, 0.0, isLetter ? 2.0 : 1.0),
			TextTrimming = TextTrimming.CharacterEllipsis
		};
		t.SetResourceReference(TextBlock.ForegroundProperty, "HeaderAccent");
		// Thin rule under the header text (authentic Win8.1 all-apps look). Same accent as the header, dimmed.
		System.Windows.Controls.Border rule = new System.Windows.Controls.Border
		{
			Height = 1.0,
			Opacity = 0.5,
			Margin = new Thickness(6.0, 0.0, 14.0, isLetter ? 5.0 : 4.0),
			HorizontalAlignment = System.Windows.HorizontalAlignment.Stretch,
			SnapsToDevicePixels = true
		};
		rule.SetResourceReference(System.Windows.Controls.Border.BackgroundProperty, "HeaderAccent");
		System.Windows.Controls.StackPanel sp = new System.Windows.Controls.StackPanel
		{
			Orientation = System.Windows.Controls.Orientation.Vertical,
			Width = 244.0,
			Tag = letter,
			HorizontalAlignment = System.Windows.HorizontalAlignment.Left,
			VerticalAlignment = VerticalAlignment.Bottom,
			Cursor = System.Windows.Input.Cursors.Hand
		};
		sp.Children.Add(t);
		sp.Children.Add(rule);
		AppsColumnsPanel.SetIsHeader((DependencyObject)(object)sp, v: true);
		if (isLetter)
		{
			sp.MouseLeftButtonDown += delegate
			{
				ShowAppsZoom();
			};
		}
		return sp;
	}

	private void ShowAppsZoom()
	{
		//IL_01d3: Unknown result type (might be due to invalid IL or missing references)
		HashSet<string> present = new HashSet<string>(AppsHost.Letters());
		AlphabetGrid.Children.Clear();
		string[] alphabet = Alphabet;
		foreach (string letter in alphabet)
		{
			bool has = present.Contains(letter);
			System.Windows.Controls.Button btn = new System.Windows.Controls.Button
			{
				Content = letter,
				Style = (Style)FindResource("ZoomLetterButton"),
				Width = 88.0,
				Height = 88.0,
				Margin = new Thickness(6.0),
				FontFamily = new System.Windows.Media.FontFamily("Segoe UI Light"),
				FontSize = 30.0,
				Foreground = System.Windows.Media.Brushes.White,
				Background = new SolidColorBrush(System.Windows.Media.Color.FromArgb((byte)(has ? 51 : 17), byte.MaxValue, byte.MaxValue, byte.MaxValue)),
				IsEnabled = has,
				Cursor = (has ? System.Windows.Input.Cursors.Hand : System.Windows.Input.Cursors.Arrow),
				Opacity = (has ? 1.0 : 0.4)
			};
			string captured = letter;
			btn.Click += delegate
			{
				JumpToLetter(captured);
			};
			AlphabetGrid.Children.Add(btn);
		}
		AppsZoomOverlay.Visibility = Visibility.Visible;
		ScaleTransform scale = new ScaleTransform(1.15, 1.15);
		AlphabetGrid.RenderTransform = scale;
		AlphabetGrid.RenderTransformOrigin = new System.Windows.Point(0.5, 0.5);
		Duration dur = Motion.Dur(Motion.Cat.Semantic);
		IEasingFunction ease = Motion.Ease(Motion.Cat.Semantic);
		scale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(1.15, 1.0, dur)
		{
			EasingFunction = ease
		});
		scale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(1.15, 1.0, dur)
		{
			EasingFunction = ease
		});
		AppsZoomOverlay.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0.0, 1.0, Motion.Dur(Motion.Cat.Micro)));
		_semanticZoomed = true;
		RefreshZoomButton();
	}

	private void HideAppsZoom()
	{
		_semanticZoomed = false;
		RefreshZoomButton();
		Duration adur = Motion.Dur(Motion.Cat.Micro);
		DoubleAnimation fade = new DoubleAnimation(0.0, adur)
		{
			EasingFunction = Motion.Ease(Motion.Cat.Micro)
		};
		fade.Completed += delegate
		{
			if (!_semanticZoomed)
			{
				AppsZoomOverlay.Visibility = Visibility.Collapsed;
				AppsZoomOverlay.BeginAnimation(UIElement.OpacityProperty, null);
				AppsZoomOverlay.Opacity = 1.0;
			}
		};
		// Mirror the zoom-in's scale so the exit isn't opacity-only (recede 1.0 -> 1.15 in step with the fade).
		if (AppsZoomOverlay.RenderTransform is ScaleTransform ast)
		{
			IEasingFunction aease = Motion.Ease(Motion.Cat.Micro);
			ast.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(ast.ScaleX, 1.15, adur) { EasingFunction = aease });
			ast.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(ast.ScaleY, 1.15, adur) { EasingFunction = aease });
		}
		AppsZoomOverlay.BeginAnimation(UIElement.OpacityProperty, fade);
	}

	private void JumpToLetter(string letter)
	{
		HideAppsZoom();
		double x = AppsHost.HeaderOffset(letter);
		if (x >= 0.0)
		{
			SmoothScroll.To(AppsScroller, x);
		}
	}

	private System.Windows.Controls.Button GetAppButton(AppEntry entry)
	{
		if (_appButtons.TryGetValue(entry, out System.Windows.Controls.Button cached))
		{
			if (cached is AppListItem cachedItem)
			{
				cachedItem.SetTheme((System.Windows.Media.Brush)FindResource("HoverTint"), (System.Windows.Media.Brush)FindResource("SearchAccent"));
			}
			return cached;
		}
		System.Windows.Controls.Button b = new AppListItem(entry, (System.Windows.Media.Brush)FindResource("HoverTint"), (System.Windows.Media.Brush)FindResource("SearchAccent"));
		b.Click += OnAppListClick;
		b.MouseDown += OnAppMiddleDown;
		b.PreviewMouseRightButtonUp += OnAppEntryRightClick;
		_appButtons[entry] = b;
		return b;
	}

	private void OnSortLabelClick(object sender, MouseButtonEventArgs e)
	{
		System.Windows.Controls.ContextMenu menu = _sortMenu ?? (_sortMenu = BuildSortMenu());
		try
		{
			menu.Resources["SortSelected"] = new SolidColorBrush(StartAccent.Color());
			menu.Resources["SortHighlight"] = new SolidColorBrush(StartAccent.Tint(0.78));
		}
		catch
		{
		}
		foreach (var (mode, item) in _sortItems)
		{
			item.IsChecked = _appsSort == mode;
		}
		_contextMenuOpen = true;
		menu.PlacementTarget = (UIElement)sender;
		menu.Placement = PlacementMode.Bottom;
		menu.IsOpen = true;
	}

	private System.Windows.Controls.ContextMenu BuildSortMenu()
	{
		System.Windows.Controls.ContextMenu menu = new System.Windows.Controls.ContextMenu
		{
			Style = (Style)base.Resources["SortMenu"]
		};
		menu.Closed += delegate
		{
			_contextMenuOpen = false;
		};
		Style itemStyle = (Style)base.Resources["SortMenuItem"];
		Add("by name", AppsSort.Name);
		Add("by date installed", AppsSort.DateInstalled);
		Add("by most used", AppsSort.MostUsed);
		Add("by category", AppsSort.Category);
		return menu;
		void Add(string header, AppsSort mode)
		{
			System.Windows.Controls.MenuItem item = new System.Windows.Controls.MenuItem
			{
				Header = header,
				Style = itemStyle,
				IsCheckable = true
			};
			item.Click += delegate
			{
				TaskbarContextMenu.QueueCommand(delegate { ApplyAppsSort(mode); });
			};
			menu.Items.Add(item);
			_sortItems[mode] = item;
		}
	}

	private void UpdateSearchWatermark()
	{
		bool empty = string.IsNullOrEmpty(SearchBox.Text);
		SearchWatermark.Visibility = ((!empty || SearchBox.IsKeyboardFocused) ? Visibility.Collapsed : Visibility.Visible);
		SearchClear.Visibility = (empty ? Visibility.Collapsed : Visibility.Visible);
	}

	private void OnSearchFocusChanged(object sender, KeyboardFocusChangedEventArgs e)
	{
		UpdateSearchWatermark();
	}

	private void OnSearchClear(object sender, RoutedEventArgs e)
	{
		SearchBox.Clear();
		SearchBox.Focus();
	}

	private void OnSearchChanged(object sender, TextChangedEventArgs e)
	{
		//IL_007e: Unknown result type (might be due to invalid IL or missing references)
		//IL_0083: Unknown result type (might be due to invalid IL or missing references)
		//IL_0097: Expected O, but got Unknown
		UpdateSearchWatermark();
		if (_appsView == null)
		{
			return;
		}
		_searchQuery = SearchBox.Text.Trim();
		if (string.IsNullOrEmpty(_searchQuery))
		{
			if (_showingAllApps)
			{
				SwitchView(showAllApps: false, animate: false);
			}
		}
		else if (!_showingAllApps)
		{
			SwitchView(showAllApps: true, animate: false);
		}
		if (_searchDebounce == null)
		{
			_searchDebounce = new DispatcherTimer((DispatcherPriority)4)
			{
				Interval = TimeSpan.FromMilliseconds(90L)
			};
			_searchDebounce.Tick += delegate
			{
				_searchDebounce.Stop();
				ApplySearchFilter();
			};
		}
		_searchDebounce.Stop();
		_searchDebounce.Start();
	}

	private void ApplySearchFilter()
	{
		if (_appsView != null)
		{
			string q = _searchQuery;
			_appsView.Filter = (string.IsNullOrEmpty(q) ? null : ((Predicate<object>)((object o) => o is AppEntry appEntry && SearchExtras.AppMatches(appEntry.Name, q))));
			BuildAppsView();
		}
	}

	private void OnPreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
	{
		//IL_0002: Unknown result type (might be due to invalid IL or missing references)
		//IL_0009: Invalid comparison between Unknown and I4
		//IL_00f2: Unknown result type (might be due to invalid IL or missing references)
		//IL_00f8: Invalid comparison between Unknown and I4
		//IL_01ae: Unknown result type (might be due to invalid IL or missing references)
		//IL_01b3: Unknown result type (might be due to invalid IL or missing references)
		//IL_01b5: Unknown result type (might be due to invalid IL or missing references)
		//IL_01b9: Unknown result type (might be due to invalid IL or missing references)
		//IL_01bb: Invalid comparison between Unknown and I4
		//IL_0241: Unknown result type (might be due to invalid IL or missing references)
		//IL_0246: Unknown result type (might be due to invalid IL or missing references)
		//IL_0248: Unknown result type (might be due to invalid IL or missing references)
		//IL_024c: Unknown result type (might be due to invalid IL or missing references)
		//IL_024e: Invalid comparison between Unknown and I4
		//IL_027e: Unknown result type (might be due to invalid IL or missing references)
		//IL_0285: Invalid comparison between Unknown and I4
		//IL_0214: Unknown result type (might be due to invalid IL or missing references)
		//IL_021b: Invalid comparison between Unknown and I4
		// Tab must not move focus inside the Start overlay. WPF's default focus traversal jumps to the far-right
		// tab stop, which yanked the user out of a group-name edit (they only wanted to type). The overlay has no
		// keyboard tab-navigation model, so swallow Tab (and Shift+Tab) entirely — pressing it now does nothing.
		if ((int)e.Key == 3) // Tab
		{
			e.Handled = true;
			return;
		}
		// Group-name rename box: like the SearchBox it holds only LOGICAL focus while this overlay owns the Win32
		// foreground, so native Backspace/Delete/Space never reach it AND the search/scroll blocks below would swallow
		// them. While a group name is being edited we OWN its editing keys and mutate Text/CaretIndex directly.
		if (_activeGroupEdit != null && _activeGroupEdit.IsVisible)
		{
			System.Windows.Controls.TextBox gb = _activeGroupEdit;
			if ((int)e.Key == 6) // Enter -> commit (trim)
			{
				if (gb.DataContext is GroupVm ge)
				{
					ge.Name = (ge.Name ?? "").Trim();
					ge.IsEditing = false;
				}
				Keyboard.ClearFocus();
				e.Handled = true;
				return;
			}
			if ((int)e.Key == 13) // Escape -> cancel, restore original
			{
				if (gb.DataContext is GroupVm ge2)
				{
					ge2.Name = _renameOriginal ?? ge2.Name;
					ge2.IsEditing = false;
				}
				Keyboard.ClearFocus();
				e.Handled = true;
				return;
			}
			if (e.Key == System.Windows.Input.Key.Back || e.Key == System.Windows.Input.Key.Delete)
			{
				string t = gb.Text ?? "";
				int ci = System.Math.Clamp(gb.CaretIndex, 0, t.Length);
				if (gb.SelectionLength > 0)
				{
					int st = gb.SelectionStart;
					gb.Text = t.Remove(st, gb.SelectionLength);
					gb.CaretIndex = st;
				}
				else if (e.Key == System.Windows.Input.Key.Back && ci > 0)
				{
					gb.Text = t.Remove(ci - 1, 1);
					gb.CaretIndex = ci - 1;
				}
				else if (e.Key == System.Windows.Input.Key.Delete && ci < t.Length)
				{
					gb.Text = t.Remove(ci, 1);
					gb.CaretIndex = ci;
				}
				e.Handled = true;
				return;
			}
			if (e.Key == System.Windows.Input.Key.Space)
			{
				string s = gb.Text ?? "";
				int ci = System.Math.Clamp(gb.CaretIndex, 0, s.Length);
				if (gb.SelectionLength > 0)
				{
					int st = gb.SelectionStart;
					gb.Text = s.Remove(st, gb.SelectionLength).Insert(st, " ");
					gb.CaretIndex = st + 1;
				}
				else
				{
					gb.Text = s.Insert(ci, " ");
					gb.CaretIndex = ci + 1;
				}
				e.Handled = true;
				return;
			}
			if ((int)e.Key == 21 || (int)e.Key == 22) // End / Home -> caret, don't scroll the band
			{
				gb.CaretIndex = (((int)e.Key == 22) ? 0 : (gb.Text ?? "").Length);
				e.Handled = true;
				return;
			}
		}
		if ((int)e.Key == 13)
		{
			if (TileAppBar.Visibility == Visibility.Visible)
			{
				HideTileAppBar();
			}
			else if (_customiseActive)
			{
				SetCustomise(on: false);
			}
			else if (AnySelected())
			{
				ClearSelection();
			}
			else if (AppsZoomOverlay.Visibility == Visibility.Visible)
			{
				HideAppsZoom();
			}
			else if (ZoomOverlay.Visibility == Visibility.Visible)
			{
				HideZoom();
			}
			else if (!string.IsNullOrEmpty(SearchBox.Text))
			{
				SearchBox.Text = string.Empty;
			}
			else if (_showingAllApps)
			{
				SwitchView(showAllApps: false);
			}
			else
			{
				HideStart(animate: true);
			}
			e.Handled = true;
			return;
		}
		// Backspace/Delete on the search query. The box often holds only LOGICAL focus (chars are fed via
		// OnPreviewTextInput), so native editing keys vanish; even when it has real keyboard focus the split-focus
		// state is unreliable. So we ALWAYS own Back/Delete here: edit the text directly (respecting caret +
		// selection) and set e.Handled=true Ã¢â‚¬â€ which stops the tunnel, so a truly-focused box never double-deletes.
		if ((e.Key == System.Windows.Input.Key.Back || e.Key == System.Windows.Input.Key.Delete) && SearchBox.Text.Length > 0)
		{
			string t = SearchBox.Text;
			int ci = System.Math.Clamp(SearchBox.CaretIndex, 0, t.Length);
			if (SearchBox.SelectionLength > 0)
			{
				int st = SearchBox.SelectionStart;
				SearchBox.Text = t.Remove(st, SearchBox.SelectionLength);
				SearchBox.CaretIndex = st;
			}
			else if (e.Key == System.Windows.Input.Key.Back && ci > 0)
			{
				SearchBox.Text = t.Remove(ci - 1, 1);
				SearchBox.CaretIndex = ci - 1;
			}
			else if (e.Key == System.Windows.Input.Key.Delete && ci < t.Length)
			{
				SearchBox.Text = t.Remove(ci, 1);
				SearchBox.CaretIndex = ci;
			}
			e.Handled = true;
			return;
		}
		// Space on the Apps search box. Like Back/Delete above, the box usually holds only LOGICAL focus, so the
		// space keystroke is swallowed by whatever owns real keyboard focus (a tile/app Button eats Space as a click)
		// and never reaches OnPreviewTextInput. In the Apps view we therefore own Space and insert it at the caret.
		// Gated on _showingAllApps so Space still toggles tile selection on the Start screen grid.
		if (e.Key == System.Windows.Input.Key.Space && _showingAllApps)
		{
			string s = SearchBox.Text;
			int ci = System.Math.Clamp(SearchBox.CaretIndex, 0, s.Length);
			if (SearchBox.SelectionLength > 0)
			{
				int st = SearchBox.SelectionStart;
				SearchBox.Text = s.Remove(st, SearchBox.SelectionLength).Insert(st, " ");
				SearchBox.CaretIndex = st + 1;
			}
			else
			{
				SearchBox.Text = s.Insert(ci, " ");
				SearchBox.CaretIndex = ci + 1;
			}
			e.Handled = true;
			return;
		}
		if ((int)e.Key == 6 && _showingAllApps && _appsView != null)
		{
			if (Keyboard.FocusedElement is System.Windows.Controls.Button { DataContext: AppEntry focused })
			{
				Launch(focused);
			}
			else
			{
				AppEntry first = ((IEnumerable)_appsView).Cast<AppEntry>().FirstOrDefault();
				if (first != null)
				{
					Launch(first);
				}
				else if (!string.IsNullOrEmpty(_searchQuery))
				{
					WebSearch(_searchQuery);
				}
			}
			e.Handled = true;
			return;
		}
		Key key = e.Key;
		if ((int)key == 21 || (int)key == 22)
		{
			ScrollViewer sv = (_showingAllApps ? AppsScroller : StartScroller);
			if (_showingAllApps || SearchBox.Text.Length <= 0)
			{
				SmoothScroll.To(sv, ((int)e.Key == 22) ? 0.0 : sv.ScrollableWidth);
				e.Handled = true;
			}
			return;
		}
		key = e.Key;
		if ((int)key == 19 || (int)key == 20)
		{
			ScrollViewer sv2 = (_showingAllApps ? AppsScroller : StartScroller);
			SmoothScroll.By(sv2, ((int)e.Key == 20) ? sv2.ViewportWidth : (0.0 - sv2.ViewportWidth));
			e.Handled = true;
		}
	}

	private void OnPreviewTextInput(object sender, TextCompositionEventArgs e)
	{
		if (!(Keyboard.FocusedElement is System.Windows.Controls.TextBox) && !char.IsControl(e.Text.FirstOrDefault()))
		{
			if (!_showingAllApps)
			{
				SwitchView(showAllApps: true, animate: false);
			}
			SearchBox.Focus();
			Keyboard.Focus(SearchBox);   // try to bind real keyboard focus so subsequent editing (Backspace/arrows) goes native
			SearchBox.Text += e.Text;
			SearchBox.CaretIndex = SearchBox.Text.Length;
			e.Handled = true;
		}
	}

	private void OnBandWheel(object sender, MouseWheelEventArgs e)
	{
		//IL_0001: Unknown result type (might be due to invalid IL or missing references)
		if (((Enum)Keyboard.Modifiers).HasFlag((Enum)(object)(ModifierKeys)2))
		{
			if (_showingAllApps)
			{
				if (e.Delta < 0)
				{
					ShowAppsZoom();
				}
				else
				{
					HideAppsZoom();
				}
			}
			else if (e.Delta < 0)
			{
				ShowZoom();
			}
			else
			{
				HideZoom();
			}
			e.Handled = true;
		}
		else if (sender is ScrollViewer sv)
		{
			SmoothScroll.By(sv, (double)(-e.Delta) * 2.5);
			e.Handled = true;
		}
	}

	private void OnDeactivated(object? sender, EventArgs e)
	{
		if (!QaKeepOpen && !_contextMenuOpen && !_dragging && !_zoomDragging)
		{
			if (Environment.TickCount < _scrollGuardUntil)
			{
				Logger.Log("Start: deactivate ignored (scrollbar/zoom press)");
				return;
			}
			if (Environment.TickCount < _showGuardUntil)
			{
				Logger.Log("Start: deactivate ignored (show guard)");
				return;
			}
			Logger.Log("Start: hide cause = Deactivated");
			HideStart(animate: true);
		}
	}

	protected override void OnClosing(CancelEventArgs e)
	{
		if (_transitionDiagnostics)
		{
			EndViewLowLatency();
			e.Cancel = false;
			return;
		}
		e.Cancel = true;
		CommitActiveGroupEditAndFlush();
		HideStart();
	}

	static StartScreen()
	{
		List<string> list = new List<string>();
		list.Add("#");
		list.AddRange(from c in Enumerable.Range(65, 26)
			select ((char)c).ToString());
		Alphabet = list.ToArray();
	}
}
