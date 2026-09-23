using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Forms;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace Win81Layer;

public partial class TaskbarWindow : Window, IComponentConnector, IStyleConnector
{
	private readonly record struct LiveWin(nint Hwnd, string Title, string? Exe, string? Aumid);

	private sealed record TaskSpec(string Key, nint Rep, string Title, List<nint> Members, bool ShowLabel);

	private sealed record AudioFlyoutSnapshot(List<AudioDevice> Outputs, List<AudioDevice> Inputs, List<AudioSessionVm> Sessions, long EnumerateMs);

	private sealed record NetworkFlyoutSnapshot(NetState81 State, NetInfo.LocalNet Local, string? PublicIp, long EnumerateMs);

	private struct RECT
	{
		public int left;

		public int top;

		public int right;

		public int bottom;
	}

	private struct POINT
	{
		public int X;

		public int Y;
	}

	private struct MONITORINFO
	{
		public int cbSize;

		public RECT rcMonitor;

		public RECT rcWork;

		public uint dwFlags;
	}

	private const int PeekHoldMs = 600;

	private DispatcherTimer? _holdTimer;

	private bool _peeking;

	private readonly ObservableCollection<TaskWindow> _tasks;

	private readonly ObservableCollection<TaskWindow> _visibleTasks;

	private readonly ObservableCollection<TaskWindow> _overflowTasks;

	private bool _reflowing;

	private readonly Dictionary<string, TaskWindow> _byKey;

	private Screen _screen;

	private DispatcherTimer? _timer;

	private DispatcherTimer? _fastTimer;

	private readonly TrayVm _tray;

	private readonly AudioController _audio;

	private readonly PerfMonitor _perf;

	private bool _isPrimary;

	private int _tick;

	private int _refreshCount;

	private readonly ObservableCollection<TrayAppIcon> _appIcons;

	private readonly Dictionary<string, TrayAppIcon> _appByKey;

	private readonly ObservableCollection<TrayAppIcon> _overflowIcons;

	private readonly Dictionary<string, TrayAppIcon> _overflowByKey;

	private readonly Dictionary<string, string> _stableByKey;

	private readonly uint _ownPid;

	private uint _shellPid;

	private List<PinnedApp> _pins;

	private static int _pinReresolveScheduled;

	private readonly ObservableCollection<PinItem> _pinItems;

	private readonly ObservableCollection<PinItem> _visiblePinItems;

	private readonly ObservableCollection<PinItem> _overflowPinItems;

	private readonly List<PinnedTile> _allTiles;

	private readonly List<GroupTile> _groups;

	private readonly Dictionary<string, PinnedTile> _pinsByExe;

	// Filename-only fallback index (e.g. "Discord.exe" -> tile) so a running window still folds into its
	// pin when the pinned app's FULL path drifts: Squirrel app-*\ version folders (Discord auto-update),
	// or per-user vs machine / Program Files vs Program Files (x86) installs (Chrome). Excludes explorer.exe
	// and the shared-host exes; a value of null marks a filename claimed by two different pins (ambiguous).
	private readonly Dictionary<string, PinnedTile> _pinsByExeName;

	private readonly Dictionary<string, PinnedTile> _pinsByAumid;

	private readonly ConcurrentDictionary<nint, string?> _exeCache;

	private readonly ConcurrentDictionary<nint, string> _screenCache;

	private volatile bool _gatherBusy;

	private volatile bool _refreshAfterGather;

	private readonly HashSet<nint> _iconResolving;

	private static readonly int[] GlyphSizes = new int[7] { 16, 20, 24, 32, 40, 48, 64 };

	private const double GlyphDiu = 24.0;

	private SolidColorBrush? _startGlyphBrush;

	private bool _startBtnHover;

	private bool _startBtnPressed;

	private static bool _startOpen;

	private static bool _charmsOpen;

	private static readonly Dictionary<string, ImageSource> _pinIconCache = new Dictionary<string, ImageSource>(StringComparer.OrdinalIgnoreCase);

	private nint _lastFgTick;

	private nint _lastExternalForeground;

	private int _shellHookMsg;

	private int _taskbarCreatedMsg;

	private bool _refreshQueued;

	private bool _themeRefreshQueued;

	private bool _wallpaperRefreshPending;

	private bool _stopped;

	private const int WM_SETTINGCHANGE = 26;

	private const int WM_DWMCOLORIZATIONCOLORCHANGED = 800;

	private const int SPI_SETDESKWALLPAPER = 20;

	private const int WM_MOUSEACTIVATE = 33;

	private const int MA_NOACTIVATE = 3;

	private const int HSHELL_WINDOWCREATED = 1;

	private const int HSHELL_WINDOWDESTROYED = 2;

	private const int HSHELL_WINDOWACTIVATED = 4;

	private const int HSHELL_WINDOWREPLACED = 13;

	private const int HSHELL_WINDOWREPLACING = 14;

	private const int HSHELL_RUDEAPPACTIVATED = 32772;

	private const int GWL_EXSTYLE = -20;

	private const int WS_EX_NOACTIVATE = 134217728;

	private string _position;

	private bool _autoHide;

	private bool _hidden;

	private string? _lastThemeSig;

	private bool? _orientedVertical;

	private static System.Windows.Controls.ContextMenu? _openContextMenu;

	private System.Windows.Controls.ContextMenu? _taskbarContextMenu;

	private Window? _menuHost;

	private static readonly string[] SharedHostExes = new string[8] { "applicationframehost.exe", "mmc.exe", "rundll32.exe", "dllhost.exe", "pythonw.exe", "python.exe", "javaw.exe", "java.exe" };

	private bool _trayTelemetryBusy;

	private bool _trayScanBusy;

	private int _lastTrayDiagSig = -1;

	// One-time log per distinct explorer-owned tray icon we skip, so the benign "live=bar+1" delta is attributable
	// (names the system icon being filtered) without spamming the log every ~150ms snapshot.
	private readonly HashSet<string> _loggedTraySkips = new HashSet<string>();

	// Keys (owner:id) of explorer-owned icons identified as the system VOLUME indicator (tooltip carried a "%"). Sticky so
	// the repurposed audio-switcher keeps working while muted (the muted tooltip drops the "%"). Distinguishes the volume
	// icon from other explorer-owned no-callback indicators like "Discord is using your microphone".
	private readonly HashSet<string> _audioSwitcherKeys = new HashSet<string>();

	private int _diagPosLogs;

	// Classic taskbar "Toolbars" — each enabled folder shows as a shortcuts button next to the tray.
	private readonly System.Collections.ObjectModel.ObservableCollection<ToolbarVm> _toolbars = new System.Collections.ObjectModel.ObservableCollection<ToolbarVm>();

	public static event Action? ToolbarsChanged;

	public static void RaiseToolbarsChanged()
	{
		ToolbarsChanged?.Invoke();
	}

	// Physical-pixel target rect the taskbar must occupy (its monitor edge), cached by PlaceOnScreen. Used to VETO any
	// foreign SetWindowPos that tries to shrink the bar to content size — which happens when a fullscreen app (or a
	// screen-capture tool) closes and the shell re-shows the bar without re-asserting its width.
	private int _tbTargetX, _tbTargetY, _tbTargetW, _tbTargetH;

	private bool _tbTargetValid;
	private bool _placingPhysicalRect;

	private DispatcherTimer? _trayScanDebounce;

	private const string TrayDragFmt = "Win81TrayIcon";

	private System.Windows.Point _trayDragStart;

	private TrayAppIcon? _trayDragItem;

	private bool _trayDragging;

	private TrayDragGhost? _trayGhost;

	private static ImageSource? _fallbackTrayIcon;

	private readonly ObservableCollection<AudioSessionVm> _appSessions;

	private DispatcherTimer? _volumeTick;

	private bool _volumeInventoryBusy;

	private bool _volumeRefreshPending;

	private bool _volumeSyncing;

	private bool _volumeClosing;

	private int _volumeRefreshToken;

	private int _volumeAnimationToken;

	private int _volumeWheelRouteCount;

	private int _volumeWheelRoutingActive;

	private int _volumeWheelLeft;

	private int _volumeWheelTop;

	private int _volumeWheelRight;

	private int _volumeWheelBottom;

	private int _queuedGlobalVolumeWheelDelta;

	private int _globalVolumeWheelDispatchQueued;

	private int _pendingVolumeWheelDelta;

	private bool _pendingVolumeWheelFlushQueued;

	private bool _volumeDiagnosticsMode;

	private CancellationTokenSource? _networkRefreshCts;

	private NetworkFlyoutSnapshot? _networkSnapshot;

	private NetKind _networkHeroKind = NetKind.Offline;

	private NetworkIconState _networkHeroState = NetworkIconState.Offline;

	private int _networkRefreshToken;

	private int _networkAnimationToken;

	private int _networkWheelRouteCount;

	private bool _networkClosing;

	private long _networkInventoryMs;

	private long _volumeLastInventoryMs;

	private long _volumeInventoryMs;

	private int _volumeOutputCount;

	private int _volumeInputCount;

	private string _volumeDefaultOutputId = string.Empty;

	private DateTime _calMonth;

	private DispatcherTimer? _clockTick;

	private readonly System.Windows.Controls.Button[] _calendarDayButtons = new System.Windows.Controls.Button[42];

	private readonly TextBlock[] _calendarHeaderLabels = new TextBlock[7];

	private bool _calendarVisualsReady;

	private DateTime _calendarSelection = DateTime.Today;

	private DateTime _calendarToday = DateTime.Today;

	private long _clockCalendarBuildMs;

	private int _clockAnimationToken;

	private int _monthAnimationToken;

	private bool _clockClosing;

	private ThumbnailPreview? _preview;

	private DispatcherTimer? _hoverShow;

	private DispatcherTimer? _hoverHide;

	private FrameworkElement? _hoverTarget;

	private System.Windows.Point _pinDragStart;

	private PinItem? _pinDragItem;

	private System.Windows.Controls.Button? _pinDragButton;

	private FrameworkElement? _pinDragEl;

	private bool _pinDragging;

	private double _pinDragGrabOffset;   // where inside the icon (0..48) the drag started, so it doesn't snap to center

	private bool _pinDidDrag;

	// Compositor-only drag state. During a pin drag we mutate NO collection — the dragged tile follows the cursor and
	// neighbours slide aside purely via RenderTransform. The reorder commits ONCE, on drop. All indices are in
	// _visiblePinItems space (== logical _pinItems index for visible pins, since _visiblePinItems is an in-order prefix
	// of _pinItems). This replaces the old per-move _pinItems.Move, which desynced from the bound _visiblePinItems and
	// made the dragged icon drift off the cursor while neighbours never moved. See memory taskbar-pin-drag-redesign.
	private int _pinDragFromVisible = -1;   // fixed visible slot the drag started from (baseline for the dragged tile)

	private int _pinDragGapIndex = -1;      // current open-gap target slot; also the final drop index

	private int _pinDragSettleToken;        // guards the deferred lift-reset so a rapid re-drag can't be clobbered

	private bool _reflowPending;            // a reflow was skipped mid-drag; drained once on drop

	private const double PinSlot = 48.0;

	private int _groupTargetIdx;

	private FrameworkElement? _groupHiliteEl;

	private readonly StringBuilder _clsSb;

	private const uint SWP_NOSIZE = 1u;

	private const uint SWP_NOZORDER = 4u;

	private const uint SWP_NOACTIVATE = 16u;

	public static double HeightPx => TaskbarMetrics.BarHeight;

	public bool IsVertical
	{
		get
		{
			string position = _position;
			if (position == "Left" || position == "Right")
			{
				return true;
			}
			return false;
		}
	}

	private static ImageSource FallbackTrayIcon
	{
		get
		{
			//IL_0082: Unknown result type (might be due to invalid IL or missing references)
			//IL_00bb: Unknown result type (might be due to invalid IL or missing references)
			if (_fallbackTrayIcon != null)
			{
				return _fallbackTrayIcon;
			}
			DrawingVisual dv = new DrawingVisual();
			using (DrawingContext dc = dv.RenderOpen())
			{
				System.Windows.Media.Color col = System.Windows.Media.Color.FromArgb(204, byte.MaxValue, byte.MaxValue, byte.MaxValue);
				System.Windows.Media.Pen pen = new System.Windows.Media.Pen(new SolidColorBrush(col), 1.3);
				dc.DrawRoundedRectangle(null, pen, new Rect(2.5, 2.5, 11.0, 11.0), 2.0, 2.0);
				dc.DrawEllipse(new SolidColorBrush(col), null, new System.Windows.Point(8.0, 8.0), 1.7, 1.7);
			}
			RenderTargetBitmap rtb = new RenderTargetBitmap(16, 16, 96.0, 96.0, PixelFormats.Pbgra32);
			rtb.Render(dv);
			((Freezable)rtb).Freeze();
			return _fallbackTrayIcon = rtb;
		}
	}

	public event Action? StartRequested;

	public event Action? StartPeekRequested;

	public event Action<bool>? StartPeekEnded;

	public static event Action? StartOpenChanged;

	private static event Action? PinsChanged;

	private static event Action? PerfModeChanged;

	public static event Action? SearchRequested;

	public static event Action? TaskViewRequested;

	private static event Action? AutoHideChanged;

	private static event Action? TaskbarColorChanged;

	private static event Action? TaskbarLayoutChanged;

	private static event Action? TaskbarAlignmentChanged;

	private static event Action? TaskbarButtonsChanged;

	private static event Action? TaskbarPositionChanged;
	private static bool _positionDiagnosticsActive;
	private static string? _positionDiagnosticsOverride;
	private static bool _fullscreenDiagnosticsEnabled;
	private static nint _fullscreenDiagnosticsWindow;

	private static event Action? TaskbarSizeChanged;

	public static void RaiseStartOpen(bool open)
	{
		_startOpen = open;
		StartOpenChanged?.Invoke();
	}

	public static void RaiseCharmsOpen(bool open)
	{
		_charmsOpen = open;
	}

	public TaskbarWindow()
	{
		//IL_02f5: Unknown result type (might be due to invalid IL or missing references)
		//IL_02fa: Unknown result type (might be due to invalid IL or missing references)
		//IL_030d: Expected O, but got Unknown
		_tasks = new ObservableCollection<TaskWindow>();
		_visibleTasks = new ObservableCollection<TaskWindow>();
		_overflowTasks = new ObservableCollection<TaskWindow>();
		_byKey = new Dictionary<string, TaskWindow>();
		_screen = Screen.PrimaryScreen;
		_tray = new TrayVm();
		_audio = new AudioController();
		_perf = new PerfMonitor();
		_appIcons = new ObservableCollection<TrayAppIcon>();
		_appByKey = new Dictionary<string, TrayAppIcon>();
		_overflowIcons = new ObservableCollection<TrayAppIcon>();
		_overflowByKey = new Dictionary<string, TrayAppIcon>();
		_stableByKey = new Dictionary<string, string>();
		_ownPid = (uint)Environment.ProcessId;
		_pins = new List<PinnedApp>();
		_pinItems = new ObservableCollection<PinItem>();
		_visiblePinItems = new ObservableCollection<PinItem>();
		_overflowPinItems = new ObservableCollection<PinItem>();
		_allTiles = new List<PinnedTile>();
		_groups = new List<GroupTile>();
		_pinsByExe = new Dictionary<string, PinnedTile>(StringComparer.OrdinalIgnoreCase);
		_pinsByExeName = new Dictionary<string, PinnedTile>(StringComparer.OrdinalIgnoreCase);
		_pinsByAumid = new Dictionary<string, PinnedTile>(StringComparer.OrdinalIgnoreCase);
		_exeCache = new ConcurrentDictionary<nint, string>();
		_screenCache = new ConcurrentDictionary<nint, string>();
		_iconResolving = new HashSet<nint>();
		_position = "Bottom";
		_appSessions = new ObservableCollection<AudioSessionVm>();
		_calMonth = DateTime.Today;
		_groupTargetIdx = -1;
		_clsSb = new StringBuilder(64);
		InitializeComponent();
		ClockPopup.CustomPopupPlacementCallback = PlaceClockPopup;
		VolumePopup.CustomPopupPlacementCallback = PlaceVolumePopup;
		NetPopup.CustomPopupPlacementCallback = PlaceNetworkPopup;
		SoundCommandPopup.CustomPopupPlacementCallback = PlaceVolumePopup;
		NetworkCommandPopup.CustomPopupPlacementCallback = PlaceNetworkPopup;
		VolumeFlyoutRoot.AddHandler(Mouse.PreviewMouseWheelEvent, new MouseWheelEventHandler(OnVolumeBodyPreviewMouseWheel), handledEventsToo: true);
		VolumeBodyScroll.AddHandler(Mouse.PreviewMouseDownEvent, new MouseButtonEventHandler(OnVolumeBodyPreviewMouseLeftButtonDown), handledEventsToo: true);
		NetworkBodyScroll.AddHandler(Mouse.PreviewMouseWheelEvent, new MouseWheelEventHandler(OnNetworkBodyPreviewMouseWheel), handledEventsToo: true);
		NetworkBodyScroll.AddHandler(Mouse.PreviewMouseDownEvent, new MouseButtonEventHandler(OnNetworkBodyPreviewMouseLeftButtonDown), handledEventsToo: true);
		AppVolumeHost.ItemsSource = _appSessions;
		VolDeviceName.Text = "Audio device";
		VolumeEmptyApps.Visibility = Visibility.Visible;
		ApplyTaskbarSize();
		TaskHost.ItemsSource = _visibleTasks;
		TaskOverflowHost.ItemsSource = _overflowTasks;
		PinOverflowHost.ItemsSource = _overflowPinItems;
		TrayPanel.DataContext = _tray;
		TrayAppHost.ItemsSource = _appIcons;
		// Keep tray icons crisp (see PolishTrayIconRendering): snap once the tray has rendered, and re-assert whenever the
		// app-icon set changes (new templated Image containers are realized) so late-arriving tray icons are sharp too.
		TrayPanel.Loaded += delegate { PolishTrayIconRendering(); };
		_appIcons.CollectionChanged += delegate { Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(PolishTrayIconRendering)); };
		_overflowIcons.CollectionChanged += delegate { Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(PolishTrayIconRendering)); };
		ToolbarHost.ItemsSource = _toolbars;
		OverflowHost.ItemsSource = _overflowIcons;
		PinnedHost.ItemsSource = _visiblePinItems;
		RebuildPins();
		PinsChanged += RebuildPins;
		PerfModeChanged += ApplyPerfMode;
		AutoHideChanged += ApplyAutoHide;
		TaskbarColorChanged += ApplyTaskbarColor;
		DesktopComposition.ModeChanged += ApplyTaskbarColor;   // re-skin (glass↔flat) when the composition profile changes
		TrayHostService.Changed += OnTrayHostChanged;          // Win11 real tray host pushed a new icon table → re-scan
		ToolbarsChanged += RebuildToolbars;
		RebuildToolbars();
		// Cold boot: the ctor read can predate the golden-primed settings, so re-read once they've settled.
		DispatcherTimer toolbarPrime = new DispatcherTimer((DispatcherPriority)4)
		{
			Interval = TimeSpan.FromMilliseconds(3500.0)
		};
		toolbarPrime.Tick += delegate
		{
			toolbarPrime.Stop();
			RebuildToolbars();
		};
		toolbarPrime.Start();
		System.Net.NetworkInformation.NetworkChange.NetworkAddressChanged += OnNetChanged;        // instant net-icon refresh
		System.Net.NetworkInformation.NetworkChange.NetworkAvailabilityChanged += OnNetAvail;
		Microsoft.Win32.SystemEvents.PowerModeChanged += OnPowerModeChanged;                     // instant battery refresh on plug/unplug/suspend
		TaskbarSizeChanged += ApplyTaskbarSize;
		TaskbarLayoutChanged += Refresh;
		TaskbarAlignmentChanged += ApplyAlignment;
		TaskbarButtonsChanged += ApplyBarButtons;
		TaskbarPositionChanged += OnPositionChanged;
		TaskbarColorChanged += InvalidateTaskbarContextMenu;
		TaskbarSizeChanged += InvalidateTaskbarContextMenu;
		TaskbarLayoutChanged += InvalidateTaskbarContextMenu;
		TaskbarAlignmentChanged += InvalidateTaskbarContextMenu;
		TaskbarButtonsChanged += InvalidateTaskbarContextMenu;
		TaskbarPositionChanged += InvalidateTaskbarContextMenu;
		DesktopComposition.ModeChanged += InvalidateTaskbarContextMenu;
		StartOpenChanged += OnStartOpenChanged;
		StartButton.Loaded += delegate
		{
			ApplyStartGlyph();
		};
		ApplyBarButtons();
		TrayRoot.SizeChanged += delegate
		{
			QueueReflow();
		};
		RootLayout.SizeChanged += delegate
		{
			QueueReflow();
		};
		TaskArea.SizeChanged += delegate
		{
			QueueReflow();
		};
		PinnedHost.SizeChanged += delegate
		{
			QueueReflow();
		};
		LeftCluster.SizeChanged += delegate
		{
			QueueReflow();
		};
		_autoHide = SettingsStore.Load().TaskbarAutoHide;
		Dispatcher.BeginInvoke((Action)PrimeTaskbarContextMenu, DispatcherPriority.ApplicationIdle);
		Dispatcher.BeginInvoke((Action)TaskbarContextMenu.WarmCaches, DispatcherPriority.ApplicationIdle);
		Dispatcher.BeginInvoke((Action)DesktopContextMenu.Warm, DispatcherPriority.ApplicationIdle);
		for (int menuKind = 0; menuKind < 3; menuKind++)
		{
			int warmKind = menuKind;
			Dispatcher.BeginInvoke((Action)delegate { FileContextMenu.WarmKind(warmKind); }, DispatcherPriority.ApplicationIdle);
		}
	}

	private void PrimeTaskbarContextMenu()
	{
		if (_stopped || _taskbarContextMenu != null)
		{
			return;
		}
		_taskbarContextMenu = TaskbarContextMenu.Build(this);
		TaskbarContextMenu.PrepareForInstantOpen(_taskbarContextMenu);
	}

	private void InvalidateTaskbarContextMenu()
	{
		_taskbarContextMenu = null;
		Dispatcher.BeginInvoke((Action)PrimeTaskbarContextMenu, DispatcherPriority.ApplicationIdle);
	}

	public static void ReloadPins()
	{
		PinsChanged?.Invoke();
	}

	public static void ReloadPerfMode()
	{
		PerfModeChanged?.Invoke();
	}

	public static void RaiseSearch()
	{
		SearchRequested?.Invoke();
	}

	public static void RaiseTaskView()
	{
		TaskViewRequested?.Invoke();
	}

	public static void RaiseAutoHideChanged()
	{
		AutoHideChanged?.Invoke();
	}

	public static void RaiseTaskbarColorChanged()
	{
		RaiseTaskbarColorChanged(invalidateWallpaper: true);
	}

	// Public accent/theme-change signal (raised whenever the Start/taskbar accent changes) so non-taskbar surfaces —
	// e.g. the tray NotifyIcon flag in App.cs — can re-tint themselves to match the Start theme.
	public static event Action? AccentChanged;

	public static void RaiseTaskbarColorChanged(bool invalidateWallpaper)
	{
		TaskbarContextMenu.InvalidateTheme();
		if (invalidateWallpaper)
		{
			TaskbarTheme.Invalidate();
		}
		TaskbarColorChanged?.Invoke();
		try { AccentChanged?.Invoke(); } catch { }
	}

	public static void RaiseTaskbarLayoutChanged()
	{
		TaskbarLayoutChanged?.Invoke();
	}

	public static void RaiseTaskbarAlignmentChanged()
	{
		TaskbarAlignmentChanged?.Invoke();
	}

	public static void RaiseTaskbarButtonsChanged()
	{
		TaskbarButtonsChanged?.Invoke();
	}

	private void ApplyBarButtons()
	{
		AppSettings s = SettingsStore.Load();
		SearchBtn.Visibility = ((!s.ShowSearch) ? Visibility.Collapsed : Visibility.Visible);
		TaskViewBtn.Visibility = ((!s.ShowTaskView) ? Visibility.Collapsed : Visibility.Visible);
		ActionCenterBtn.Visibility = ((!s.ShowActionCenter) ? Visibility.Collapsed : Visibility.Visible);
		ApplyActionCenterPosition(s.ActionCenterPosition);
		// The 8.1 notification icon is gated by the "Win8.1 app icons" toggle (reverts to the native MDL2 glyph when off),
		// so the whole custom-icon set toggles together.
		bool win81Icons = s.Replace81AppIcons;
		NotifIcon81.Visibility = (win81Icons ? Visibility.Visible : Visibility.Collapsed);
		NotifGlyphNative.Visibility = (win81Icons ? Visibility.Collapsed : Visibility.Visible);
		if (win81Icons)
		{
			// Prefer the AUTHENTIC Win8.1 Action Center flag (ActionCenter.dll); keep the recreated PNG as fallback.
			System.Windows.Media.ImageSource notif = Win81AssetResolver.GetAsset("Notifications.Flag", 20);
			if (notif != null) { NotifIcon81.Source = notif; }
		}
	}

	// Places the Action center button in the tray. "Left" puts it just before the clock (Win8.1 style); anything else
	// (default "Right") keeps it far right after the date, immediately before the show-desktop sliver. Reorders the live
	// TrayRoot children so both monitors stay identical, and is idempotent (re-running with the same value is a no-op).
	private void ApplyActionCenterPosition(string pos)
	{
		if (TrayRoot == null || ActionCenterBtn == null || ClockPanel == null)
		{
			return;
		}
		try
		{
			bool left = pos == "Left";
			int desired;
			if (left)
			{
				// Immediately before the clock panel.
				int clockIdx = TrayRoot.Children.IndexOf(ClockPanel);
				desired = (clockIdx < 0) ? 0 : clockIdx;
			}
			else
			{
				// Far right: last, but keep the show-desktop sliver at the very edge if it is present.
				int sdIdx = (ShowDesktop != null) ? TrayRoot.Children.IndexOf(ShowDesktop) : -1;
				desired = (sdIdx < 0) ? TrayRoot.Children.Count : sdIdx;
			}
			int current = TrayRoot.Children.IndexOf(ActionCenterBtn);
			if (current == desired)
			{
				return;   // already where it belongs
			}
			TrayRoot.Children.Remove(ActionCenterBtn);
			// Removing shifts everything after it down by one, so recompute the anchor against the mutated collection.
			if (left)
			{
				int clockIdx = TrayRoot.Children.IndexOf(ClockPanel);
				desired = (clockIdx < 0) ? 0 : clockIdx;
			}
			else
			{
				int sdIdx = (ShowDesktop != null) ? TrayRoot.Children.IndexOf(ShowDesktop) : -1;
				desired = (sdIdx < 0) ? TrayRoot.Children.Count : sdIdx;
			}
			desired = Math.Clamp(desired, 0, TrayRoot.Children.Count);
			TrayRoot.Children.Insert(desired, ActionCenterBtn);
		}
		catch (Exception ex)
		{
			Logger.Log("[taskbar] ApplyActionCenterPosition: " + ex.Message);
		}
	}

	public static void RaiseTaskbarPositionChanged()
	{
		TaskbarPositionChanged?.Invoke();
	}

	internal static void ApplyPositionForDiagnostics(string edge)
	{
		if (edge is not ("Bottom" or "Top" or "Left" or "Right"))
		{
			throw new ArgumentOutOfRangeException(nameof(edge));
		}
		_positionDiagnosticsActive = true;
		TracePositionDiagnostics($"edge={edge} apply begin");
		_positionDiagnosticsOverride = edge;
		TracePositionDiagnostics($"edge={edge} in-memory override updated");
		RaiseTaskbarPositionChanged();
		TracePositionDiagnostics($"edge={edge} event completed");
	}

	internal static void EndPositionDiagnostics()
	{
		TracePositionDiagnostics("trace end");
		_fullscreenDiagnosticsEnabled = false;
		_fullscreenDiagnosticsWindow = nint.Zero;
		_positionDiagnosticsOverride = null;
		_positionDiagnosticsActive = false;
	}

	internal static void SetFullscreenDiagnostics(bool enabled, nint window = default)
	{
		_fullscreenDiagnosticsEnabled = enabled;
		_fullscreenDiagnosticsWindow = enabled ? window : nint.Zero;
	}

	private static void TracePositionDiagnostics(string message)
	{
		if (_positionDiagnosticsActive)
		{
			Logger.Log("[taskbar-position-qa] " + message);
		}
	}

	public static void RaiseTaskbarSizeChanged()
	{
		TaskbarSizeChanged?.Invoke();
	}

	private void ApplyTaskbarSize()
	{
		base.Resources["TB.BarHeight"] = TaskbarMetrics.BarHeight;
		base.Resources["TB.IconSize"] = TaskbarMetrics.IconSize;
		base.Resources["TB.GlyphSize"] = TaskbarMetrics.GlyphSize;
		// The tray NETWORK icon is an Image (a NetIcons81 vector glyph), not a font glyph like the volume/battery
		// buttons. Size it so its visible mark matches the MDL2 tray glyphs at every taskbar size: the network
		// glyph fills ~19/32 of its image, so image = GlyphSize * 1.5 makes the ethernet/wifi mark the same height
		// as the volume speaker glyph. ROUND to whole pixels — a fractional box (13*1.5=19.5, 15*1.5=22.5) forces the
		// bitmap to render at a sub-pixel size, which is a primary source of the "blurry tray icon" the user reported.
		base.Resources["TB.NetIconSize"] = Math.Round(TaskbarMetrics.GlyphSize * 1.5);
		// Authentic Win8.1 tray-status icons (network/volume/notifications) are drawn near-full-bleed, so at the 1.5x
		// NetIconSize box they read oversized vs the recreated predecessors. Render them at glyph scale so they match the
		// other tray glyphs / their predecessors. RULE: all authentic tray icons use TB.Win81TrayIconSize, not NetIconSize.
		base.Resources["TB.Win81TrayIconSize"] = TaskbarMetrics.GlyphSize;
		base.Resources["TB.StartGlyphSize"] = TaskbarMetrics.StartGlyphSize;
		if (PresentationSource.FromVisual(this) != null)
		{
			PlaceOnScreen();
		}
		// Re-request the size-dependent tray assets so the resolver picks the native frame nearest the NEW box (crisp,
		// near 1:1) instead of keeping a frame chosen for the old size.
		_tray?.NotifySizeChanged();
		// A size change re-lays-out the tray; re-assert crisp rendering (pixel snap + HighQuality scaling) once the new
		// containers exist so icons stay sharp at Small / Normal / Large.
		Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(PolishTrayIconRendering));
	}

	// Tray icons were rendering soft ("θολά"): the centered taskbar places the tray strip at fractional device-pixel
	// offsets, and the authentic 32/48px tray assets were being downscaled into their boxes with WPF's default (linear)
	// filter. Fix universally: UseLayoutRounding on the tray root snaps EVERY descendant (fixed net/volume buttons AND the
	// templated app-icon images, present and future) to whole device pixels at any DPI; HighQuality (Fant) scaling on each
	// Image keeps the downscale crisp. Idempotent + cheap (the tray holds a handful of icons); safe to re-run on any change.
	private void PolishTrayIconRendering()
	{
		try
		{
			if (TrayPanel == null)
			{
				return;
			}
			TrayPanel.UseLayoutRounding = true;
			TrayPanel.SnapsToDevicePixels = true;
			WalkTrayImages(TrayPanel);
			// Defensive: the app-icon strip / overflow may not sit under TrayPanel in the visual tree; cover them directly
			// so templated app-icon Images get the same pixel-snap + HighQuality treatment as the fixed net/volume buttons.
			if (TrayAppHost != null) { TrayAppHost.UseLayoutRounding = true; WalkTrayImages(TrayAppHost); }
			if (OverflowHost != null) { OverflowHost.UseLayoutRounding = true; WalkTrayImages(OverflowHost); }
		}
		catch (Exception ex)
		{
			Logger.Log("PolishTrayIconRendering: " + ex.Message);
		}
	}

	private static void WalkTrayImages(DependencyObject root)
	{
		int count = VisualTreeHelper.GetChildrenCount(root);
		for (int i = 0; i < count; i++)
		{
			DependencyObject child = VisualTreeHelper.GetChild(root, i);
			if (child is System.Windows.Controls.Image img)
			{
				if (RenderOptions.GetBitmapScalingMode(img) != BitmapScalingMode.HighQuality)
				{
					RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);
				}
				img.SnapsToDevicePixels = true;
			}
			WalkTrayImages(child);
		}
	}

	private void ApplyTaskbarColor()
	{
		nint tbHwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
		if (ShellSkin.GlassOn)   // single-source glass (honors the DeskCompTransparency gate like every other shell surface)
		{
			// Win7-Aero shell skin: translucent smoked-glass taskbar (wallpaper shows through). Uses a plain alpha
			// WPF brush, NOT WCA acrylic — the acrylic backdrop leaks a stuck DWM region that outlives the process.
			AcrylicGlass.Clear(tbHwnd);
			base.Background = new SolidColorBrush(System.Windows.Media.Color.FromArgb(184, 22, 22, 28));
		}
		else
		{
			AcrylicGlass.Clear(tbHwnd);
			base.Background = TaskbarTheme.CurrentBackground();
		}
		try
		{
			ResourceDictionary res = System.Windows.Application.Current.Resources;
			System.Windows.Media.Color sc = TaskbarTheme.SelectionColor();
			SolidColorBrush sel = new SolidColorBrush(sc);
			((Freezable)sel).Freeze();
			SolidColorBrush link = new SolidColorBrush(TaskbarTheme.LinkColor());
			((Freezable)link).Freeze();
			SolidColorBrush hover = new SolidColorBrush(System.Windows.Media.Color.FromArgb(51, sc.R, sc.G, sc.B));
			((Freezable)hover).Freeze();
			res["TaskbarSelection"] = sel;
			res["TaskbarLink"] = link;
			res["TaskbarHover"] = hover;
		}
		catch
		{
		}
		try
		{
			System.Windows.Media.Color bright = Brighten(StartAccent.Color());
			SolidColorBrush acc = new SolidColorBrush(bright);
			((Freezable)acc).Freeze();
			SolidColorBrush dim = new SolidColorBrush(System.Windows.Media.Color.FromArgb(180, bright.R, bright.G, bright.B));
			((Freezable)dim).Freeze();
			base.Resources["TB.Accent"] = acc;
			base.Resources["TB.AccentDim"] = dim;
		}
		catch
		{
		}
		try
		{
			// Skin-aware button/glyph/flyout brushes: Win8.1 flat = crisp white-alpha; Win7-Aero glass = translucent
			// accent tint. Templates bind these as DynamicResource so a mode switch re-skins the whole bar live.
			bool glass = ShellSkin.GlassOn;
			System.Windows.Media.Color sc2 = TaskbarTheme.SelectionColor();
			if (glass)
			{
				System.Windows.Media.Color t = ShellSkin.AccentTone(0.30);
				base.Resources["TB.FlyoutBg"] = Fz(System.Windows.Media.Color.FromArgb(0xC0, t.R, t.G, t.B));
				// Win7 "pearl" gel: a glossy vertical accent gradient (bright top highlight → glossy mid split → medium bottom).
				base.Resources["TB.BtnHover"] = FzGel(sc2, 0x66, 0x33, 0x55, 0x40);
				base.Resources["TB.BtnActive"] = FzGel(sc2, 0x80, 0x50, 0x74, 0x5A);
				base.Resources["TB.BtnPressed"] = FzGel(sc2, 0xA0, 0x78, 0x96, 0x82);
				base.Resources["TB.BtnRunning"] = FzGel(sc2, 0x22, 0x10, 0x1C, 0x16);   // faint persistent gel on RUNNING apps
				base.Resources["TB.GlyphHover"] = Fz(System.Windows.Media.Color.FromArgb(40, sc2.R, sc2.G, sc2.B));
				base.Resources["TB.GlyphPressed"] = Fz(System.Windows.Media.Color.FromArgb(96, sc2.R, sc2.G, sc2.B));
				base.Resources["TB.BarEdge"] = Fz(System.Windows.Media.Color.FromArgb(0x50, byte.MaxValue, byte.MaxValue, byte.MaxValue));
			}
			else
			{
				base.Resources["TB.FlyoutBg"] = Fz(System.Windows.Media.Color.FromArgb(0xF2, 0x1A, 0x1A, 0x1F));
				base.Resources["TB.BtnHover"] = Fz(System.Windows.Media.Color.FromArgb(0x1A, byte.MaxValue, byte.MaxValue, byte.MaxValue));
				base.Resources["TB.BtnActive"] = Fz(System.Windows.Media.Color.FromArgb(0x30, byte.MaxValue, byte.MaxValue, byte.MaxValue));
				base.Resources["TB.BtnPressed"] = Fz(System.Windows.Media.Color.FromArgb(0x55, byte.MaxValue, byte.MaxValue, byte.MaxValue));
				base.Resources["TB.BtnRunning"] = Fz(System.Windows.Media.Color.FromArgb(0x00, byte.MaxValue, byte.MaxValue, byte.MaxValue));   // flat 8.1: no idle gel (indicator only)
				base.Resources["TB.GlyphHover"] = Fz(System.Windows.Media.Color.FromArgb(0x22, byte.MaxValue, byte.MaxValue, byte.MaxValue));
				base.Resources["TB.GlyphPressed"] = Fz(System.Windows.Media.Color.FromArgb(0x33, byte.MaxValue, byte.MaxValue, byte.MaxValue));
				base.Resources["TB.BarEdge"] = Fz(System.Windows.Media.Color.FromArgb(0x22, byte.MaxValue, byte.MaxValue, byte.MaxValue));
			}
		}
		catch
		{
		}
		ApplyClockCalendarTheme();
		ApplySoundFlyoutTheme();
		ApplyNetworkFlyoutTheme();
		ApplyStartButtonTheme();
	}

	private void ApplyStartButtonTheme()
	{
		UpdateStartButtonSurfaceState();
		UpdateStartOrb();
		UpdateStartGlyphColor(animate: false);
	}

	private void UpdateStartButtonSurfaceState()
	{
		string mode = DesktopComposition.EffectiveMode;
		bool active = _startOpen || _startBtnPressed;
		string restingState = active ? "open" : "normal";
		string hoverState = active ? "open" : "hover";
		base.Resources["TB.StartSurface"] = Fz(StartSurfaceTone(mode, restingState));
		base.Resources["TB.StartSurfaceHover"] = Fz(StartSurfaceTone(mode, hoverState));
		base.Resources["TB.StartSurfacePressed"] = Fz(StartSurfaceTone(mode, "pressed"));
		base.Resources["TB.StartFrame"] = Fz(StartSurfaceTone(mode, restingState));
		base.Resources["TB.StartFramePressed"] = Fz(StartSurfaceTone(mode, "pressed"));
	}

	internal static bool UsesMetroStartFrame(string mode)
	{
		try
		{
			return !string.Equals(CompositionProfiles.Resolve(mode).Id, "windows7-aero", StringComparison.OrdinalIgnoreCase);
		}
		catch
		{
			return true;
		}
	}

	internal static System.Windows.Media.Color StartSurfaceTone(string mode, string state)
	{
		if (!UsesMetroStartFrame(mode))
		{
			return Colors.Transparent;
		}
		return state is "pressed" or "open" ? Colors.Black : Colors.Transparent;
	}

	internal static System.Windows.Media.Color StartGlyphTone(string mode, System.Windows.Media.Color accent, bool open, bool hover, bool pressed)
	{
		// User request: the Start flag is WHITE by default and turns the theme accent colour when pressed. It also
		// stays accent while Start is open (so the press-colour persists as an "active" state) and returns to white
		// on close/leave. UpdateStartGlyphColor animates this swap over ~130ms (Micro) in both directions. Hover is
		// intentionally left white — the button's surface already gives hover feedback.
		return (pressed || open) ? accent : Colors.White;
	}

	internal void PrepareStartButtonForDiagnostics(string state = "idle")
	{
		_startBtnHover = string.Equals(state, "hover", StringComparison.OrdinalIgnoreCase);
		_startBtnPressed = string.Equals(state, "pressed", StringComparison.OrdinalIgnoreCase);
		_startOpen = string.Equals(state, "open", StringComparison.OrdinalIgnoreCase);
		if (!_startBtnHover && !_startBtnPressed && !_startOpen && !string.Equals(state, "idle", StringComparison.OrdinalIgnoreCase))
		{
			throw new ArgumentOutOfRangeException(nameof(state), state, "Expected idle, hover, pressed or open.");
		}
		ApplyTaskbarSize();
		ApplyStartButtonTheme();
		StartButton.ApplyTemplate();
		ApplyStartGlyph();
		StartButton.UpdateLayout();
	}

	private void ApplyClockCalendarTheme()
	{
		try
		{
			AppSettings settings = SettingsStore.Load();
			System.Windows.Media.Color accent = StartAccent.Color();
			System.Windows.Media.Color themeBase = settings.StartBgMode == "pattern"
				? StartBackgroundFactory.Parse(settings.StartBgColor, accent)
				: accent;
			ApplyClockCalendarPalette(accent, themeBase);
		}
		catch (Exception ex)
		{
			Logger.Log("Clock/calendar theme apply failed: " + ex.Message);
		}
	}

	private void ApplyClockCalendarPalette(System.Windows.Media.Color accent, System.Windows.Media.Color themeBase)
	{
		System.Windows.Media.Color bodyTop = SoundTone(themeBase, 0.0, 0.0, 0.23);
		System.Windows.Media.Color bodyBottom = SoundTone(accent, 0.0, 0.0, 0.12);
		LinearGradientBrush body = new LinearGradientBrush
		{
			StartPoint = new System.Windows.Point(0.0, 0.0),
			EndPoint = new System.Windows.Point(1.0, 1.0)
		};
		body.GradientStops.Add(new GradientStop(bodyTop, 0.0));
		body.GradientStops.Add(new GradientStop(bodyBottom, 1.0));
		((Freezable)body).Freeze();

		base.Resources["TB.ClockBackground"] = body;
		base.Resources["TB.ClockHeader"] = Fz(SoundTone(accent, 0.0, 0.0, 0.32));
		base.Resources["TB.ClockBody"] = body;
		base.Resources["TB.ClockFooter"] = Fz(SoundTone(accent, 0.0, 0.0, 0.14));
		base.Resources["TB.ClockBorder"] = Fz(SoundTone(accent, 0.0, 0.42, 0.48));
		base.Resources["TB.ClockLink"] = Fz(SoundTone(accent, -0.01, 0.55, 0.68));
		base.Resources["TB.ClockSelection"] = Fz(SoundTone(accent, 0.0, 0.62, 0.50));
	}

	private void ApplySoundFlyoutTheme()
	{
		try
		{
			AppSettings settings = SettingsStore.Load();
			System.Windows.Media.Color accent = StartAccent.Color();
			System.Windows.Media.Color themeBase = settings.StartBgMode == "pattern"
				? StartBackgroundFactory.Parse(settings.StartBgColor, accent)
				: accent;
			System.Windows.Media.Color bodyTop = SoundTone(themeBase, 0.0, 0.0, 0.24);
			System.Windows.Media.Color bodyBottom = SoundTone(accent, 0.0, 0.0, 0.14);
			LinearGradientBrush background = new LinearGradientBrush
			{
				StartPoint = new System.Windows.Point(0.0, 0.0),
				EndPoint = new System.Windows.Point(1.0, 1.0)
			};
			background.GradientStops.Add(new GradientStop(bodyTop, 0.0));
			background.GradientStops.Add(new GradientStop(bodyBottom, 1.0));
			((Freezable)background).Freeze();

			base.Resources["TB.SoundBackground"] = background;
			base.Resources["TB.SoundHeader"] = Fz(SoundTone(accent, 0.0, 0.0, 0.29));
			base.Resources["TB.SoundFooter"] = Fz(SoundTone(accent, 0.0, 0.0, 0.16));
			base.Resources["TB.SoundBorder"] = Fz(SoundTone(accent, 0.0, 0.42, 0.48));
			base.Resources["TB.SoundLink"] = Fz(SoundTone(accent, -0.01, 0.55, 0.68));
		}
		catch (Exception ex)
		{
			Logger.Log("Sound flyout theme apply failed: " + ex.Message);
		}
	}

	private void ApplyNetworkFlyoutTheme()
	{
		try
		{
			AppSettings settings = SettingsStore.Load();
			System.Windows.Media.Color accent = StartAccent.Color();
			System.Windows.Media.Color themeBase = settings.StartBgMode == "pattern"
				? StartBackgroundFactory.Parse(settings.StartBgColor, accent)
				: accent;
			ApplyNetworkFlyoutPalette(accent, themeBase);
		}
		catch (Exception ex)
		{
			Logger.Log("Network flyout theme apply failed: " + ex.Message);
		}
	}

	private void ApplyNetworkFlyoutPalette(System.Windows.Media.Color accent, System.Windows.Media.Color themeBase)
	{
		System.Windows.Media.Color bodyTop = SoundTone(themeBase, 0.0, 0.0, 0.25);
		System.Windows.Media.Color bodyBottom = SoundTone(accent, 0.0, 0.0, 0.13);
		LinearGradientBrush background = new LinearGradientBrush
		{
			StartPoint = new System.Windows.Point(0.0, 0.0),
			EndPoint = new System.Windows.Point(1.0, 1.0)
		};
		background.GradientStops.Add(new GradientStop(bodyTop, 0.0));
		background.GradientStops.Add(new GradientStop(bodyBottom, 1.0));
		((Freezable)background).Freeze();

		System.Windows.Media.Color stateTone = SoundTone(accent, 0.0, 0.42, 0.31);
		base.Resources["TB.NetworkBackground"] = background;
		base.Resources["TB.NetworkHeader"] = Fz(SoundTone(accent, 0.0, 0.0, 0.30));
		base.Resources["TB.NetworkFooter"] = Fz(SoundTone(accent, 0.0, 0.0, 0.15));
		base.Resources["TB.NetworkBorder"] = Fz(SoundTone(accent, 0.0, 0.42, 0.48));
		base.Resources["TB.NetworkLink"] = Fz(SoundTone(accent, -0.01, 0.55, 0.68));
		base.Resources["TB.NetworkState"] = Fz(System.Windows.Media.Color.FromArgb(0xC6, stateTone.R, stateTone.G, stateTone.B));
		base.Resources["TB.NetworkWifiTile"] = Fz(SoundTone(accent, 0.0, 0.60, 0.49));
		base.Resources["TB.NetworkConnectionsTile"] = Fz(SoundTone(accent, 0.02, 0.56, 0.39));
		base.Resources["TB.NetworkTroubleshootTile"] = Fz(SoundTone(accent, 0.09, 0.58, 0.48));
	}

	private static System.Windows.Media.Color SoundTone(System.Windows.Media.Color seed, double hueShift, double saturationFloor, double lightness)
	{
		ColorMath.RgbToHsl((double)seed.R / 255.0, (double)seed.G / 255.0, (double)seed.B / 255.0, out double h, out double s, out _);
		if (s < 0.06)
		{
			if (saturationFloor >= 0.4)
			{
				h = 0.58;
				s = saturationFloor;
			}
			else
			{
				s = 0.0;
			}
		}
		else
		{
			s = Math.Clamp(Math.Max(s, saturationFloor), 0.0, 0.84);
		}
		h = (h + hueShift) % 1.0;
		if (h < 0.0)
		{
			h += 1.0;
		}
		ColorMath.HslToRgb(h, s, Math.Clamp(lightness, 0.0, 1.0), out double r, out double g, out double b);
		return System.Windows.Media.Color.FromRgb(
			(byte)Math.Clamp((int)Math.Round(r * 255.0), 0, 255),
			(byte)Math.Clamp((int)Math.Round(g * 255.0), 0, 255),
			(byte)Math.Clamp((int)Math.Round(b * 255.0), 0, 255));
	}

	private static SolidColorBrush Fz(System.Windows.Media.Color c)
	{
		SolidColorBrush b = new SolidColorBrush(c);
		((Freezable)b).Freeze();
		return b;
	}

	// Frozen glossy "pearl" vertical gel gradient (Win7 taskbar button look) from a single accent colour at 4 alphas.
	private static System.Windows.Media.Brush FzGel(System.Windows.Media.Color c, byte aTop, byte aUpMid, byte aLoMid, byte aBot)
	{
		LinearGradientBrush g = new LinearGradientBrush
		{
			StartPoint = new System.Windows.Point(0.0, 0.0),
			EndPoint = new System.Windows.Point(0.0, 1.0)
		};
		g.GradientStops.Add(new GradientStop(System.Windows.Media.Color.FromArgb(aTop, c.R, c.G, c.B), 0.0));
		g.GradientStops.Add(new GradientStop(System.Windows.Media.Color.FromArgb(aUpMid, c.R, c.G, c.B), 0.48));
		g.GradientStops.Add(new GradientStop(System.Windows.Media.Color.FromArgb(aLoMid, c.R, c.G, c.B), 0.5));
		g.GradientStops.Add(new GradientStop(System.Windows.Media.Color.FromArgb(aBot, c.R, c.G, c.B), 1.0));
		((Freezable)g).Freeze();
		return g;
	}

	private static System.Windows.Media.Color Brighten(System.Windows.Media.Color c)
	{
		return System.Windows.Media.Color.FromRgb(Up(c.R), Up(c.G), Up(c.B));
		static byte Up(byte v)
		{
			return (byte)Math.Min(255, v + 82 + (255 - v) * 22 / 100);
		}
	}

	private void ApplyPerfMode()
	{
		if (_isPrimary)
		{
			_tray.PerfVisible = SettingsStore.Load().PerfMonEnabled;
		}
	}

	private void RebuildPins()
	{
		try
		{
			_pins = TaskbarPins.Load();
			_pinItems.Clear();
			_allTiles.Clear();
			_groups.Clear();
			_pinsByExe.Clear();
			_pinsByExeName.Clear();
			_pinsByAumid.Clear();
			Dictionary<string, GroupTile> groupByName = new Dictionary<string, GroupTile>(StringComparer.OrdinalIgnoreCase);
			foreach (PinnedApp p in _pins)
			{
				ImageSource icon = null;
				try
				{
					icon = CachedPinIcon(p);
				}
				catch (Exception ex)
				{
					Logger.Log("Pin icon failed '" + p.Name + "': " + ex.Message);
				}
				PinnedTile tile = new PinnedTile
				{
					Name = p.Name,
					LaunchPath = p.LaunchPath,
					Args = p.Args,
					ExePath = p.ExePath,
					IconPath = p.IconPath,
					AumidExplicit = p.Aumid,
					Icon = icon
				};
				_allTiles.Add(tile);
				if (!string.IsNullOrEmpty(p.ExePath))
				{
					_pinsByExe[p.ExePath] = tile;
					// Also index by exe filename so a running window folds into this pin when the pinned FULL
					// path drifts (Squirrel app-*\ versions like Discord; Chrome per-user/machine installs).
					// Skip explorer.exe (File Explorer folds by its stable exact path; Control Panel / This PC
					// must keep splitting by AUMID) and the shared-host exes (they host several distinct apps
					// under one filename). On a collision between two DIFFERENT pins, null the entry so an
					// ambiguous filename never folds the wrong window — those pins keep exact-path matching.
					string pinExeName = System.IO.Path.GetFileName(p.ExePath);
					if (!string.IsNullOrEmpty(pinExeName) && !IsExplorerHost(p.ExePath) && !IsSharedHostExe(p.ExePath))
					{
						if (_pinsByExeName.TryGetValue(pinExeName, out PinnedTile existingByName))
						{
							if (existingByName != tile)
							{
								_pinsByExeName[pinExeName] = null;
							}
						}
						else
						{
							_pinsByExeName[pinExeName] = tile;
						}
					}
				}
				string aumid = tile.Aumid;
				if (aumid != null)
				{
					_pinsByAumid[aumid] = tile;
				}
				// shell:AppsFolder pins (ExePath == null) whose LIVE windows expose NO AppUserModel.ID on Win11
				// (File Explorer, Task Manager) cannot fold by exact path OR by AUMID (the window AUMID is empty, so
				// _pinsByAumid never matches). Map the pin's known AUMID to its backing process exe filename so a
				// running window folds by filename instead. (Control Panel / This PC are explorer.exe with an empty
				// AUMID too and are indistinguishable from File Explorer here, so they fold into the File Explorer
				// pin — better than a permanent duplicate.)
				if (string.IsNullOrEmpty(p.ExePath))
				{
					string shellExe = ShellPinExeName(tile.Aumid);
					if (!string.IsNullOrEmpty(shellExe))
					{
						if (_pinsByExeName.TryGetValue(shellExe, out PinnedTile exByName))
						{
							if (exByName != tile) { _pinsByExeName[shellExe] = null; }
						}
						else { _pinsByExeName[shellExe] = tile; }
					}
				}
				if (string.IsNullOrEmpty(p.GroupName))
				{
					_pinItems.Add(tile);
					continue;
				}
				if (groupByName.TryGetValue(p.GroupName, out var g))
				{
					g.Members.Add(tile);
					continue;
				}
				GroupTile group = new GroupTile
				{
					Name = p.GroupName
				};
				group.Members.Add(tile);
				groupByName[p.GroupName] = group;
				_groups.Add(group);
				_pinItems.Add(group);
			}
			foreach (GroupTile g2 in _groups)
			{
				try
				{
					g2.Icon = GroupIcon(g2);
				}
				catch
				{
				}
			}
			ReflowAppStrip();
			Logger.Log($"Pins rebuilt: {_pins.Count} saved → {_allTiles.Count} tiles");
			foreach (string appId in _allTiles.Select((PinnedTile tile) => tile.Aumid).Where((string? id) => !string.IsNullOrEmpty(id)).Distinct(StringComparer.OrdinalIgnoreCase).Take(12))
			{
				JumpListApi.GetRecentAsync(appId, 8, delegate { });
			}
			if (AppInventory.ColdBoot() && System.Threading.Interlocked.CompareExchange(ref _pinReresolveScheduled, 1, 0) == 0)
			{
				Task.Run(async delegate
				{
					try
					{
						while (AppInventory.ColdBoot())
						{
							await Task.Delay(5000);
						}
						await Task.Delay(3000);
						Dispatcher? dispatcher = System.Windows.Application.Current?.Dispatcher;
						if (dispatcher != null && !dispatcher.HasShutdownStarted)
						{
							await dispatcher.BeginInvoke((Delegate)(Action)delegate
							{
								_pinIconCache.Clear();
								PinsChanged?.Invoke();
							}, Array.Empty<object>());
						}
					}
					finally
					{
						System.Threading.Interlocked.Exchange(ref _pinReresolveScheduled, 0);
					}
				});
			}
			if (!_stopped)
			{
				QueueRefresh();
			}
		}
		catch (Exception ex2)
		{
			Logger.Log("RebuildPins failed: " + ex2.Message);
		}
	}

	private static ImageSource? GroupIcon(GroupTile g)
	{
		//IL_00be: Unknown result type (might be due to invalid IL or missing references)
		try
		{
			List<ImageSource> icons = (from m in g.Members
				select m.Icon into imageSource
				where imageSource != null
				select imageSource).Take(4).ToList();
			if (icons.Count == 0)
			{
				return null;
			}
			DrawingVisual dv = new DrawingVisual();
			using (DrawingContext dc = dv.RenderOpen())
			{
				for (int i = 0; i < icons.Count; i++)
				{
					double x = 2 + i % 2 * 24;
					double y = 2 + i / 2 * 24;
					dc.DrawImage(icons[i], new Rect(x, y, 22.0, 22.0));
				}
			}
			RenderTargetBitmap rtb = new RenderTargetBitmap(48, 48, 96.0, 96.0, PixelFormats.Pbgra32);
			rtb.Render(dv);
			((Freezable)rtb).Freeze();
			return rtb;
		}
		catch
		{
			return g.Members.FirstOrDefault()?.Icon;
		}
	}

	private static ImageSource? CachedPinIcon(PinnedApp p)
	{
		string key = p.IconPath + "|" + p.ExePath + "|" + p.LaunchPath;
		if (_pinIconCache.TryGetValue(key, out ImageSource cached))
		{
			return cached;
		}
		ImageSource icon = (SettingsStore.Current.Replace81AppIcons ? AppIconOverrides.Resolve(p) : null) ?? PinIcon(p);
		// Guarantee a real, non-blank icon: EnsureVisibleOnDark for dark-glyph contrast, then EnsureNonBlank so a
		// pin whose IconPath/ExePath/LaunchPath all fail (or resolve to a blank/white handle) shows a letter tile
		// instead of a white circle. Never null now, so cache unconditionally.
		icon = IconResolver.EnsureNonBlank(AppInventory.EnsureVisibleOnDark(icon), p.Name);
		_pinIconCache[key] = icon;
		return icon;
	}

	public static void RefreshPinIcons()
	{
		_pinIconCache.Clear();
		PinsChanged?.Invoke();
	}

	private static ImageSource? PinIcon(PinnedApp p)
	{
		if (!string.IsNullOrEmpty(p.IconPath))
		{
			ImageSource i = AppInventory.LoadIcon(p.IconPath);
			if (i != null)
			{
				return i;
			}
		}
		if (!string.IsNullOrEmpty(p.ExePath))
		{
			ImageSource i2 = AppInventory.LoadIcon(p.ExePath);
			if (i2 != null)
			{
				return i2;
			}
		}
		return AppInventory.LoadIcon(p.LaunchPath);
	}

	private void ApplyStartGlyph()
	{
		//IL_009e: Unknown result type (might be due to invalid IL or missing references)
		//IL_00a3: Unknown result type (might be due to invalid IL or missing references)
		if (!(StartButton.Template?.FindName("StartGlyph", StartButton) is System.Windows.Shapes.Rectangle rect) || !(StartButton.Template?.FindName("StartGlyphMask", StartButton) is ImageBrush mask))
		{
			return;
		}
		try
		{
			PresentationSource src = PresentationSource.FromVisual(this);
			double? num;
			if (src == null)
			{
				num = null;
			}
			else
			{
				CompositionTarget compositionTarget = src.CompositionTarget;
				if (compositionTarget == null)
				{
					num = null;
				}
				else
				{
					Matrix transformToDevice = compositionTarget.TransformToDevice;
					num = transformToDevice.M11;
				}
			}
			double scale = num ?? 1.0;
			if (scale <= 0.0)
			{
				scale = 1.0;
			}
			int targetPx = (int)Math.Round(TaskbarMetrics.StartGlyphSize * scale);
			int chosen = GlyphSizes.FirstOrDefault((int s) => s >= targetPx, GlyphSizes[^1]);
			Uri uri = new Uri($"pack://application:,,,/Assets/start81_{chosen}.png", UriKind.Absolute);
			BitmapImage bmp = new BitmapImage();
			bmp.BeginInit();
			bmp.UriSource = uri;
			bmp.CacheOption = BitmapCacheOption.OnLoad;
			bmp.EndInit();
			((Freezable)bmp).Freeze();
			mask.ImageSource = bmp;
			if (_startGlyphBrush == null)
			{
				_startGlyphBrush = new SolidColorBrush(Colors.White);
			}
			rect.Fill = _startGlyphBrush;
			UpdateStartOrb();
			UpdateStartGlyphColor(animate: false);
		}
		catch (Exception ex)
		{
			Logger.Log("ApplyStartGlyph failed: " + ex.Message);
		}
	}

	private void UpdateStartOrb()
	{
		try
		{
			if (StartButton.Template?.FindName("StartOrb", StartButton) is System.Windows.Shapes.Ellipse orb)
			{
				orb.Visibility = UsesMetroStartFrame(DesktopComposition.EffectiveMode) ? Visibility.Collapsed : Visibility.Visible;
			}
		}
		catch
		{
		}
	}

	private void UpdateStartGlyphColor(bool animate = true)
	{
		if (_startGlyphBrush != null)
		{
			System.Windows.Media.Color target = StartGlyphTone(
				DesktopComposition.EffectiveMode,
				StartAccent.Color(),
				_startOpen,
				_startBtnHover,
				_startBtnPressed);
			if (animate)
			{
				_startGlyphBrush.BeginAnimation(SolidColorBrush.ColorProperty, new ColorAnimation(target, Motion.Dur(Motion.Cat.Micro))
				{
					EasingFunction = Motion.Ease(Motion.Cat.Micro)
				});
			}
			else
			{
				_startGlyphBrush.BeginAnimation(SolidColorBrush.ColorProperty, null);
				_startGlyphBrush.Color = target;
			}
		}
	}

	private void OnStartBtnEnter(object sender, System.Windows.Input.MouseEventArgs e)
	{
		_startBtnHover = true;
		UpdateStartGlyphColor();
	}

	private void OnStartBtnLeave(object sender, System.Windows.Input.MouseEventArgs e)
	{
		_startBtnHover = false;
		UpdateStartGlyphColor();
	}

	private void OnStartBtnDown(object sender, MouseButtonEventArgs e)
	{
		//IL_0020: Unknown result type (might be due to invalid IL or missing references)
		//IL_002a: Expected O, but got Unknown
		_startBtnPressed = true;
		_peeking = false;
		UpdateStartButtonSurfaceState();
		UpdateStartGlyphColor();
		if (_holdTimer == null)
		{
			_holdTimer = new DispatcherTimer();
		}
		_holdTimer.Interval = TimeSpan.FromMilliseconds(600L);
		_holdTimer.Tick -= OnHoldTick;
		_holdTimer.Tick += OnHoldTick;
		_holdTimer.Start();
	}

	private void OnHoldTick(object? sender, EventArgs e)
	{
		DispatcherTimer? holdTimer = _holdTimer;
		if (holdTimer != null)
		{
			holdTimer.Stop();
		}
		if (_startBtnPressed)
		{
			_peeking = true;
			StartPeekRequested?.Invoke();
		}
	}

	private void OnStartBtnUp(object sender, MouseButtonEventArgs e)
	{
		_startBtnPressed = false;
		DispatcherTimer? holdTimer = _holdTimer;
		if (holdTimer != null)
		{
			holdTimer.Stop();
		}
		UpdateStartButtonSurfaceState();
		UpdateStartGlyphColor();
		if (_peeking)
		{
			_peeking = false;
			bool commit = StartButton.IsMouseOver;
			StartPeekEnded?.Invoke(commit);
			e.Handled = true;
		}
	}

	private void OnStartOpenChanged()
	{
		UpdateStartButtonSurfaceState();
		UpdateStartGlyphColor();
	}

	public void StartOn(Screen screen)
	{
		//IL_00d3: Unknown result type (might be due to invalid IL or missing references)
		//IL_00d8: Unknown result type (might be due to invalid IL or missing references)
		//IL_00ec: Expected O, but got Unknown
		//IL_019a: Unknown result type (might be due to invalid IL or missing references)
		//IL_019f: Unknown result type (might be due to invalid IL or missing references)
		//IL_01b6: Expected O, but got Unknown
		//IL_01dc: Unknown result type (might be due to invalid IL or missing references)
		//IL_01e1: Unknown result type (might be due to invalid IL or missing references)
		//IL_01f8: Expected O, but got Unknown
		_screen = screen;
		_isPrimary = screen.Primary;
		TrayPanel.Visibility = ((!_isPrimary) ? Visibility.Collapsed : Visibility.Visible);
		if (_isPrimary)
		{
			_tray.PerfVisible = SettingsStore.Load().PerfMonEnabled;
		}
		ApplyTaskbarColor();
		Show();
		PlaceOnScreen();
		((DispatcherObject)this).Dispatcher.BeginInvoke((Delegate)new Action(ReapplyAll), (DispatcherPriority)6, Array.Empty<object>());
		// One short settle pass covers the appbar/work-area race. Settings and pins are already backed by durable,
		// process-wide caches, so the former 6/20/60/130/190-second cold-boot reapply cascade only invalidated layout.
		DispatcherTimer settleApply = new DispatcherTimer
		{
			Interval = TimeSpan.FromMilliseconds(2500L)
		};
		settleApply.Tick += delegate
		{
			settleApply.Stop();
			ReapplyAll();
		};
		settleApply.Start();
		nint handle = new WindowInteropHelper(this).Handle;
		int ex = GetWindowLong(handle, -20);
		SetWindowLong(handle, -20, ex | 0x8000000);
		_shellHookMsg = RegisterWindowMessage("SHELLHOOK");
		_taskbarCreatedMsg = RegisterWindowMessage("TaskbarCreated");
		RegisterShellHookWindow(handle);
		HwndSource.FromHwnd(handle)?.AddHook(ShellHookProc);
		_timer = new DispatcherTimer((DispatcherPriority)4)
		{
			// Shell-hook events refresh create/destroy/activation immediately. This is only the recovery poll for
			// applications that do not emit a usable shell event, so scanning every monitor twice per second is enough.
			Interval = TimeSpan.FromMilliseconds(2000L)
		};
		_timer.Tick += delegate
		{
			Refresh();
		};
		_timer.Start();
		_fastTimer = new DispatcherTimer((DispatcherPriority)4)
		{
			// Only auto-hide needs sub-200ms responsiveness; foreground/fullscreen changes arrive via the shell hook.
			// With auto-hide off this remains a low-cost recovery poll rather than the primary update mechanism.
			Interval = TimeSpan.FromMilliseconds(SettingsStore.Current.TaskbarAutoHide ? 150L : 1000L)
		};
		_fastTimer.Tick += delegate
		{
			FastTick();
		};
		_fastTimer.Start();
		Refresh();
		void ReapplyAll()
		{
			if (!_stopped)
			{
				AppSettings s = SettingsStore.Load();
				ApplyTaskbarSize();
				ApplyAlignment();
				ApplyBarButtons();
				ApplyTaskbarColor();
				ApplyAutoHide();
				if (_isPrimary)
				{
					Logger.Log($"Taskbar applied: align={s.TaskbarAlignment} size={s.TaskbarSize} color={s.TaskbarColorMode} transp={s.TaskbarTransparent} autohide={s.TaskbarAutoHide} combine={s.TaskbarCombine}");
				}
			}
		}
	}

	private void OnNetChanged(object sender, EventArgs e)
	{
		NetCaps.Invalidate();   // hardware may have changed (USB Wi-Fi / dock) -> re-probe capabilities on next read
		((DispatcherObject)this).Dispatcher.BeginInvoke((Delegate)(Action)RefreshNetNow, Array.Empty<object>());
	}

	private void OnNetAvail(object sender, System.Net.NetworkInformation.NetworkAvailabilityEventArgs e)
	{
		NetCaps.Invalidate();
		((DispatcherObject)this).Dispatcher.BeginInvoke((Delegate)(Action)RefreshNetNow, Array.Empty<object>());
	}

	private void RefreshNetNow()
	{
		if (_stopped)
		{
			return;
		}
		System.Threading.Tasks.Task.Run(delegate
		{
			NetState81 net = NetState81.Read();
			try
			{
				((DispatcherObject)this).Dispatcher.BeginInvoke((Delegate)(Action)delegate
				{
					_tray?.SetNetwork(net);
					if (NetPopup?.IsOpen == true && !_networkClosing)
					{
						_ = RefreshNetworkFlyoutAsync(includeExternal: true);
					}
				}, Array.Empty<object>());
			}
			catch
			{
			}
		});
	}

	// Battery is otherwise only refreshed on the ~10s slow tick (and not at all under a fullscreen app). Refresh it
	// immediately on a power event (plug/unplug, suspend/resume, battery-saver toggle) so the tray never shows a stale
	// charge/percent. Only the primary bar carries the battery indicator.
	private void OnPowerModeChanged(object sender, Microsoft.Win32.PowerModeChangedEventArgs e)
	{
		((DispatcherObject)this).Dispatcher.BeginInvoke((Delegate)(Action)RefreshPowerNow, Array.Empty<object>());
	}

	private void RefreshPowerNow()
	{
		if (_stopped || !_isPrimary)
		{
			return;
		}
		System.Threading.Tasks.Task.Run(delegate
		{
			var power = PowerStatus.Read();
			int secondsLeft = (power.present && !power.charging) ? PowerStatus.SecondsLeft() : -1;
			try
			{
				((DispatcherObject)this).Dispatcher.BeginInvoke((Delegate)(Action)delegate
				{
					if (_tray != null)
					{
						_tray.BatteryVisible = power.present;
						if (power.present)
						{
							_tray.BatteryPct = power.percent;
							_tray.Charging = power.charging;
							_tray.Saver = power.saver;
							_tray.BatterySecondsLeft = secondsLeft;
						}
						else
						{
							_tray.BatteryPct = 0;
							_tray.Charging = false;
							_tray.Saver = false;
							_tray.BatterySecondsLeft = -1;
						}
					}
				}, Array.Empty<object>());
			}
			catch
			{
			}
		});
	}

	private long _lastFullscreenCheckMs;

	private bool _hiddenForFullscreen;

	private long _lastWorkingSetTrimMs;

	// While the bar is hidden under a TRUE-fullscreen app, poll fast so exiting fullscreen WITHIN the same window
	// (F11/Esc, a video player leaving fullscreen, exclusive-fullscreen exit — none of which emit an HSHELL activation)
	// reveals the bar in ~150ms instead of waiting for the 1s recovery tick. Steady-state (visible) cadence is untouched.
	private void SetFullscreenHidden(bool hidden)
	{
		if (_hiddenForFullscreen == hidden)
		{
			return;
		}
		_hiddenForFullscreen = hidden;
		if (_fastTimer != null)
		{
			_fastTimer.Interval = TimeSpan.FromMilliseconds((hidden || _autoHide) ? 150L : 1000L);
		}
	}

	private void FastTick()
	{
		nint fg = GetForegroundWindow();
		if (fg != _lastFgTick)
		{
			_lastFgTick = fg;
			UpdateForegroundOnly();
		}
		// Throttle the fullscreen check (6 P/Invokes per monitor) to ~400ms wall-clock so the fast auto-hide cadence
		// (150ms) doesn't run it at 6.7Hz. Fullscreen enter/exit is also caught by ShellHookProc + the 2s Refresh.
		long now = Environment.TickCount64;
		// Poll much more often while hidden-for-fullscreen so the reveal is near-instant on exit; stay lazy otherwise.
		if (now - _lastFullscreenCheckMs >= (_hiddenForFullscreen ? 120L : 400L))
		{
			_lastFullscreenCheckMs = now;
			UpdateFullscreenVisibility();
		}
		AutoHideTick();
	}

	private void UpdateForegroundOnly()
	{
		nint fg = GetEffectiveForegroundWindow();
		foreach (TaskWindow t in _tasks)
		{
			t.IsForeground = ((t.Members.Count > 0) ? t.Members.Contains(fg) : (t.Hwnd == fg));
		}
		foreach (PinnedTile tile in _allTiles)
		{
			tile.IsForeground = tile.RunningHwnds.Contains(fg);
		}
		foreach (GroupTile g in _groups)
		{
			g.IsForeground = g.Members.Any((PinnedTile m) => m.RunningHwnds.Contains(fg));
		}
		ReflowAppStrip();
	}

	private nint GetEffectiveForegroundWindow()
	{
		nint foreground = GetForegroundWindow();
		if (foreground != IntPtr.Zero && TrayReader.ProcessIdOf(foreground) != _ownPid)
		{
			_lastExternalForeground = foreground;
			return foreground;
		}
		if (_lastExternalForeground != IntPtr.Zero && WindowList.IsWindowAlive(_lastExternalForeground))
		{
			return _lastExternalForeground;
		}
		return foreground;
	}

	public void StopBar()
	{
		_stopped = true;
		DispatcherTimer? timer = _timer;
		if (timer != null)
		{
			timer.Stop();
		}
		DispatcherTimer? fastTimer = _fastTimer;
		if (fastTimer != null)
		{
			fastTimer.Stop();
		}
		DispatcherTimer? trayScanDebounce = _trayScanDebounce;
		if (trayScanDebounce != null)
		{
			trayScanDebounce.Stop();
		}
		DispatcherTimer? hoverShow = _hoverShow;
		if (hoverShow != null)
		{
			hoverShow.Stop();
		}
		DispatcherTimer? hoverHide = _hoverHide;
		if (hoverHide != null)
		{
			hoverHide.Stop();
		}
		StopVolumeTick();
		DeactivateVolumeWheelRouting();
		SmoothScroll.StopVertical(VolumeBodyScroll);
		_volumeRefreshToken++;
		if (VolumePopup != null)
		{
			VolumePopup.IsOpen = false;
		}
		if (SoundCommandPopup != null)
		{
			SoundCommandPopup.IsOpen = false;
		}
		CancelNetworkRefresh();
		SmoothScroll.StopVertical(NetworkBodyScroll);
		if (NetPopup != null)
		{
			NetPopup.IsOpen = false;
		}
		if (NetworkCommandPopup != null)
		{
			NetworkCommandPopup.IsOpen = false;
		}
		StopClockTick();
		if (ClockPopup != null)
		{
			ClockPopup.IsOpen = false;
		}
		try
		{
			_preview?.Close();
		}
		catch
		{
		}
		_preview = null;
		try
		{
			_menuHost?.Close();
		}
		catch
		{
		}
		_menuHost = null;
		PinsChanged -= RebuildPins;
		PerfModeChanged -= ApplyPerfMode;
		AutoHideChanged -= ApplyAutoHide;
		TaskbarColorChanged -= ApplyTaskbarColor;
		DesktopComposition.ModeChanged -= ApplyTaskbarColor;
		System.Net.NetworkInformation.NetworkChange.NetworkAddressChanged -= OnNetChanged;
		System.Net.NetworkInformation.NetworkChange.NetworkAvailabilityChanged -= OnNetAvail;
		Microsoft.Win32.SystemEvents.PowerModeChanged -= OnPowerModeChanged;
		TaskbarSizeChanged -= ApplyTaskbarSize;
		TaskbarLayoutChanged -= Refresh;
		TaskbarAlignmentChanged -= ApplyAlignment;
		TaskbarButtonsChanged -= ApplyBarButtons;
		TaskbarPositionChanged -= OnPositionChanged;
		TaskbarColorChanged -= InvalidateTaskbarContextMenu;
		TaskbarSizeChanged -= InvalidateTaskbarContextMenu;
		TaskbarLayoutChanged -= InvalidateTaskbarContextMenu;
		TaskbarAlignmentChanged -= InvalidateTaskbarContextMenu;
		TaskbarButtonsChanged -= InvalidateTaskbarContextMenu;
		TaskbarPositionChanged -= InvalidateTaskbarContextMenu;
		DesktopComposition.ModeChanged -= InvalidateTaskbarContextMenu;
		StartOpenChanged -= OnStartOpenChanged;
		try
		{
			nint handle = new WindowInteropHelper(this).Handle;
			if (handle != IntPtr.Zero)
			{
				HwndSource.FromHwnd(handle)?.RemoveHook(ShellHookProc);
				DeregisterShellHookWindow(handle);
			}
		}
		catch
		{
		}
		try
		{
			Close();
		}
		catch
		{
			Hide();
		}
	}

	private nint ShellHookProc(nint hwnd, int msg, nint wParam, nint lParam, ref bool handled)
	{
		if (_stopped)
		{
			return IntPtr.Zero;
		}
		// TASKBAR SIZE GUARD: with auto-hide off, the bar must always fill its monitor edge. A fullscreen app closing (or
		// a screen-capture tool) can fire a foreign SetWindowPos that shrinks us to content size (~160x28) and re-centers
		// us. Catch that WM_WINDOWPOSCHANGING and rewrite the proposed rect back to our cached edge rect before it applies.
		if (msg == 0x0046 && lParam != IntPtr.Zero && !_autoHide && !_placingPhysicalRect && _tbTargetValid && _tbTargetW > 0)   // WM_WINDOWPOSCHANGING
		{
			try
			{
				uint flg = (uint)System.Runtime.InteropServices.Marshal.ReadInt32(lParam, 32);
				int x = System.Runtime.InteropServices.Marshal.ReadInt32(lParam, 16);
				int y = System.Runtime.InteropServices.Marshal.ReadInt32(lParam, 20);
				int cx = System.Runtime.InteropServices.Marshal.ReadInt32(lParam, 24);
				int cy = System.Runtime.InteropServices.Marshal.ReadInt32(lParam, 28);
				bool wrongMove = (flg & 2u) == 0u && (x != _tbTargetX || y != _tbTargetY);
				bool wrongSize = (flg & 1u) == 0u && (cx != _tbTargetW || cy != _tbTargetH);
				if (wrongMove || wrongSize)
				{
					System.Runtime.InteropServices.Marshal.WriteInt32(lParam, 16, _tbTargetX);
					System.Runtime.InteropServices.Marshal.WriteInt32(lParam, 20, _tbTargetY);
					System.Runtime.InteropServices.Marshal.WriteInt32(lParam, 24, _tbTargetW);
					System.Runtime.InteropServices.Marshal.WriteInt32(lParam, 28, _tbTargetH);
					System.Runtime.InteropServices.Marshal.WriteInt32(lParam, 32, (int)(flg & 0xFFFFFFFCu));
					if (_diagPosLogs < 4) { _diagPosLogs++; Logger.Log($"[taskbar-guard] vetoed {x},{y} {cx}x{cy}; forced {_tbTargetX},{_tbTargetY} {_tbTargetW}x{_tbTargetH}"); }
				}
			}
			catch { }
		}
		if (msg == 33)
		{
			handled = true;
			return 3;
		}
		// After the position change is APPLIED: WPF keeps its logical size (base.Width stays 1920) but the real HWND can
		// be left shrunk by a foreign SetWindowPos while hidden, then re-shown at that shrunk size. WPF never pushes its
		// size back to the HWND, so force it directly here. Self-terminating: the forced rect == target, so the resulting
		// WM_WINDOWPOSCHANGED reports the target and we stop.
		if (msg == 0x0047 && !_autoHide && !_placingPhysicalRect && _tbTargetValid && !_stopped && base.Visibility == Visibility.Visible && _tbTargetW > 0)   // WM_WINDOWPOSCHANGED
		{
			try
			{
				if (GetWindowRect(hwnd, out RECT rr))
				{
					int w = rr.right - rr.left;
					int h2 = rr.bottom - rr.top;
					if (rr.left != _tbTargetX || rr.top != _tbTargetY || w != _tbTargetW || h2 != _tbTargetH)
					{
						SetWindowPos(hwnd, IntPtr.Zero, _tbTargetX, _tbTargetY, _tbTargetW, _tbTargetH, 0x0014u);   // SWP_NOZORDER|SWP_NOACTIVATE
					}
				}
			}
			catch { }
		}
		if (msg == WM_SETTINGCHANGE && ((IntPtr)wParam).ToInt32() == SPI_SETDESKWALLPAPER)
		{
			QueueThemeRefresh(wallpaperChanged: true);
		}
		else if (msg == WM_DWMCOLORIZATIONCOLORCHANGED)
		{
			// Accent broadcasts do not mean the wallpaper changed. Keeping the decoded wallpaper colour cache
			// avoids synchronous image analysis and duplicate taskbar redraws during composition transitions.
			QueueThemeRefresh(wallpaperChanged: false);
		}
		if (msg == _taskbarCreatedMsg && _taskbarCreatedMsg != 0)
		{
			((DispatcherObject)this).Dispatcher.BeginInvoke((Delegate)(Action)delegate
			{
				try
				{
					if (_isPrimary)
					{
						AppBar.HideNativeTaskbar();
					}
					RegisterShellHookWindow(hwnd);
					_shellPid = 0u;
					// Explorer recreated its taskbar and may have reasserted the native work area. Reapply our
					// single monitor-aware reservation and exact physical edge rectangle.
					PlaceOnScreen();
					Refresh();
					if (_isPrimary)
					{
						ScanTrayAsync();
						// Re-fit windows that were maximized before the restart to the restored work area.
						((DispatcherObject)this).Dispatcher.BeginInvoke((Delegate)(Action)TaskbarWorkArea.NudgeWindows, (DispatcherPriority)4, Array.Empty<object>());
					}
					Logger.Log("TaskbarCreated: native bar re-hidden, shell hook + work area reapplied");
				}
				catch (Exception ex)
				{
					Logger.Log("TaskbarCreated handling failed: " + ex.Message);
				}
			}, (DispatcherPriority)4, Array.Empty<object>());
			return IntPtr.Zero;
		}
		if (msg == _shellHookMsg && _shellHookMsg != 0)
		{
			switch (((IntPtr)wParam).ToInt32())
			{
			case 1:   // HSHELL_WINDOWCREATED — app launched: show its button promptly (in-frame), not at idle
			case 2:   // HSHELL_WINDOWDESTROYED — app closed: drop its button promptly
				QueueRefresh(prompt: true);
				if (_isPrimary)
				{
					QueueTrayScan();
				}
				break;
			case 13:
			case 14:
				QueueRefresh();
				if (_isPrimary)
				{
					QueueTrayScan();
				}
				break;
			case 4:
			case 32772:
				UpdateFullscreenVisibility();
				UpdateForegroundOnly();
				QueueRefresh();
				break;
			}
		}
		return IntPtr.Zero;
	}

	private void QueueRefresh(bool prompt = false)
	{
		if (!_refreshQueued)
		{
			_refreshQueued = true;
			// Structural window changes (created/destroyed) refresh at Render priority so the button appears/vanishes
			// in the same frame as the window; other churn stays at Background so it never competes with input/animation.
			((DispatcherObject)this).Dispatcher.BeginInvoke((Delegate)(Action)delegate
			{
				_refreshQueued = false;
				Refresh();
			}, prompt ? (DispatcherPriority)7 : (DispatcherPriority)4, Array.Empty<object>());
		}
	}

	private void QueueThemeRefresh(bool wallpaperChanged)
	{
		_wallpaperRefreshPending |= wallpaperChanged;
		if (_themeRefreshQueued)
		{
			return;
		}
		_themeRefreshQueued = true;
		((DispatcherObject)this).Dispatcher.BeginInvoke((Delegate)(Action)delegate
		{
			bool invalidateWallpaper = _wallpaperRefreshPending;
			_wallpaperRefreshPending = false;
			_themeRefreshQueued = false;
			if (_stopped)
			{
				return;
			}
			if (invalidateWallpaper)
			{
				TaskbarTheme.Invalidate();
			}
			ApplyTaskbarColor();
		}, (DispatcherPriority)4, Array.Empty<object>());
	}

	[DllImport("user32.dll")]
	private static extern int GetWindowLong(nint h, int i);

	[DllImport("user32.dll")]
	private static extern int SetWindowLong(nint h, int i, int v);

	[DllImport("user32.dll", CharSet = CharSet.Unicode)]
	private static extern int RegisterWindowMessage(string msg);

	[DllImport("user32.dll")]
	private static extern bool RegisterShellHookWindow(nint hWnd);

	[DllImport("user32.dll")]
	private static extern bool DeregisterShellHookWindow(nint hWnd);

	private void OnPositionChanged()
	{
		TracePositionDiagnostics($"{_screen.DeviceName} handler begin");
		PlaceOnScreen();
		TracePositionDiagnostics($"{_screen.DeviceName} placement completed");
		if (_isPrimary)
		{
			((DispatcherObject)this).Dispatcher.BeginInvoke((Delegate)new Action(TaskbarWorkArea.NudgeWindows), (DispatcherPriority)4, Array.Empty<object>());
		}
	}

	// Force this bar to re-present its composition surface on its CURRENT monitor. After a display
	// topology/DPI rebuild, a freshly-created WPF window on a SECONDARY monitor can end up visible +
	// topmost yet paint nothing (its DirectComposition surface was created against the primary and never
	// re-hosted on the target monitor). A cross-tick Hidden -> Visible toggle makes WPF drop and rebuild
	// that surface on the right monitor, then we re-assert the physical rect. Boot never calls this (only
	// the topology-rebuild path does), so the normal startup path is completely unaffected.
	public void RepresentOnMonitor()
	{
		if (_stopped)
		{
			return;
		}
		try
		{
			base.Visibility = Visibility.Hidden;
		}
		catch
		{
		}
		((DispatcherObject)this).Dispatcher.BeginInvoke((Delegate)(Action)delegate
		{
			if (!_stopped)
			{
				try
				{
					base.Visibility = Visibility.Visible;
					base.Topmost = true;
					PlaceOnScreen();
				}
				catch
				{
				}
			}
		}, (DispatcherPriority)6, Array.Empty<object>());
	}

	public void PlaceOnScreen()
	{
		//IL_0039: Unknown result type (might be due to invalid IL or missing references)
		//IL_003e: Unknown result type (might be due to invalid IL or missing references)
		//IL_0084: Unknown result type (might be due to invalid IL or missing references)
		//IL_0089: Unknown result type (might be due to invalid IL or missing references)
		TracePositionDiagnostics($"{_screen.DeviceName} PlaceOnScreen begin");
		System.Drawing.Rectangle b = _screen.Bounds;
		PresentationSource src = PresentationSource.FromVisual(this);
		double? num;
		Matrix transformToDevice;
		if (src == null)
		{
			num = null;
		}
		else
		{
			CompositionTarget compositionTarget = src.CompositionTarget;
			if (compositionTarget == null)
			{
				num = null;
			}
			else
			{
				transformToDevice = compositionTarget.TransformToDevice;
				num = transformToDevice.M11;
			}
		}
		double sx = num ?? 1.0;
		double? num2;
		if (src == null)
		{
			num2 = null;
		}
		else
		{
			CompositionTarget compositionTarget2 = src.CompositionTarget;
			if (compositionTarget2 == null)
			{
				num2 = null;
			}
			else
			{
				transformToDevice = compositionTarget2.TransformToDevice;
				num2 = transformToDevice.M22;
			}
		}
		double sy = num2 ?? 1.0;
		if (sx <= 0.0)
		{
			sx = 1.0;
		}
		if (sy <= 0.0)
		{
			sy = 1.0;
		}
		_position = _positionDiagnosticsOverride ?? SettingsStore.Load().TaskbarPosition;
		bool vertical = IsVertical;
		double thickDiu = HeightPx;
		int thickPx = (int)Math.Round(HeightPx * (vertical ? sx : sy));
		double monW = (double)(b.Right - b.Left) / sx;
		double monH = (double)(b.Bottom - b.Top) / sy;
		// Publish the next target before WPF changes Width/Height. The framework emits intermediate
		// WINDOWPOS messages for each dependency-property change; those are ignored inside this
		// transaction and one exact physical rectangle is committed at the end.
		switch (_position)
		{
		case "Top":
			_tbTargetX = b.Left; _tbTargetY = b.Top; _tbTargetW = b.Right - b.Left; _tbTargetH = thickPx; break;
		case "Left":
			_tbTargetX = b.Left; _tbTargetY = b.Top; _tbTargetW = thickPx; _tbTargetH = b.Bottom - b.Top; break;
		case "Right":
			_tbTargetX = b.Right - thickPx; _tbTargetY = b.Top; _tbTargetW = thickPx; _tbTargetH = b.Bottom - b.Top; break;
		default:
			_tbTargetX = b.Left; _tbTargetY = b.Bottom - thickPx; _tbTargetW = b.Right - b.Left; _tbTargetH = thickPx; break;
		}
		_tbTargetValid = true;
		TracePositionDiagnostics($"{_screen.DeviceName} physical target {_tbTargetX},{_tbTargetY} {_tbTargetW}x{_tbTargetH}");
		_placingPhysicalRect = true;
		try
		{
		// WPF coerces a top-level window back inside Screen.WorkingArea. A taskbar intentionally
		// lives outside that area, so assigning Left/Top here creates a correction loop. WPF owns
		// only the logical layout size; CommitPhysicalRect is the single position owner.
			if (vertical)
			{
				base.Width = thickDiu;
				base.Height = monH;
			}
			else
			{
				base.Width = monW;
				base.Height = thickDiu;
			}
			TracePositionDiagnostics($"{_screen.DeviceName} WPF layout size assigned for {_position}");
			base.Topmost = true;
			ApplyOrientation(vertical);
			TracePositionDiagnostics($"{_screen.DeviceName} orientation applied");
			ApplyFlyoutPlacement();
			TracePositionDiagnostics($"{_screen.DeviceName} flyouts placed");
			TaskbarWorkArea.Reserve(_screen, _position, thickPx);
			TracePositionDiagnostics($"{_screen.DeviceName} work area reserved");
			CommitPhysicalRect(hidden: false);
			TracePositionDiagnostics($"{_screen.DeviceName} physical rect committed");
			ApplyStartGlyph();
			if (_autoHide)
			{
				SetHiddenState(_hidden);
			}
		}
		finally
		{
			_placingPhysicalRect = false;
		}
		TracePositionDiagnostics($"{_screen.DeviceName} PlaceOnScreen completed");
	}

	private void CommitPhysicalRect(bool hidden)
	{
		if (!_tbTargetValid || _tbTargetW <= 0 || _tbTargetH <= 0)
		{
			return;
		}
		int x = _tbTargetX;
		int y = _tbTargetY;
		int width = _tbTargetW;
		int height = _tbTargetH;
		if (hidden)
		{
			int sliver = Math.Max(2, (int)Math.Round(2.0 * DpiScale()));
			switch (_position)
			{
			case "Top":
				y -= height - sliver;
				break;
			case "Left":
				x -= width - sliver;
				break;
			case "Right":
				x += width - sliver;
				break;
			default:
				y += height - sliver;
				break;
			}
		}
		try
		{
			nint handle = new WindowInteropHelper(this).Handle;
			if (handle != IntPtr.Zero)
			{
				SetWindowPos(handle, IntPtr.Zero, x, y, width, height, 0x0014u);
			}
		}
		catch
		{
		}
	}

	private PlacementMode FlyoutMode()
	{
		string position = _position;
		if (1 == 0)
		{
		}
		PlacementMode result = position switch
		{
			"Top" => PlacementMode.Bottom, 
			"Left" => PlacementMode.Right, 
			"Right" => PlacementMode.Left, 
			_ => PlacementMode.Top, 
		};
		if (1 == 0)
		{
		}
		return result;
	}

	private void ApplyFlyoutPlacement()
	{
		PlacementMode mode = FlyoutMode();
		string position = _position;
		if (1 == 0)
		{
		}
		double num = ((position == "Left") ? 4.0 : ((!(position == "Right")) ? 0.0 : (-4.0)));
		if (1 == 0)
		{
		}
		double hOff = num;
		string position2 = _position;
		if (1 == 0)
		{
		}
		num = ((position2 == "Top") ? 4.0 : ((!(position2 == "Bottom")) ? 0.0 : (-4.0)));
		if (1 == 0)
		{
		}
		double vOff = num;
		Popup[] array = new Popup[2] { TaskOverflowPopup, OverflowPopup };
		foreach (Popup pop in array)
		{
			if (pop != null)
			{
				pop.Placement = mode;
				pop.HorizontalOffset = hOff;
				pop.VerticalOffset = vOff;
			}
		}
		if (ClockPopup != null)
		{
			ClockPopup.Placement = PlacementMode.Custom;
			ClockPopup.HorizontalOffset = 0.0;
			ClockPopup.VerticalOffset = 0.0;
		}
		if (VolumePopup != null)
		{
			VolumePopup.Placement = PlacementMode.Custom;
			VolumePopup.HorizontalOffset = 0.0;
			VolumePopup.VerticalOffset = 0.0;
		}
		if (NetPopup != null)
		{
			NetPopup.Placement = PlacementMode.Custom;
			NetPopup.HorizontalOffset = 0.0;
			NetPopup.VerticalOffset = 0.0;
		}
	}

	private void ApplyAutoHide()
	{
		_autoHide = SettingsStore.Load().TaskbarAutoHide;
		if (_fastTimer != null)
		{
			// Auto-hide needs 150ms responsiveness; with it off the fast timer is only a low-cost fullscreen/recovery
			// poll (foreground is event-driven via the shell hook), so 1s halves the idle wakeups.
			_fastTimer.Interval = TimeSpan.FromMilliseconds(_autoHide ? 150L : 1000L);
		}
		if (!_autoHide)
		{
			SetHiddenState(hide: false);
		}
	}

	private bool AnyTaskbarFlyoutOpen()
	{
		try
		{
			if (base.IsMouseOver || _preview != null)
			{
				return true;
			}
			if ((TaskOverflowPopup != null && TaskOverflowPopup.IsOpen)
				|| (OverflowPopup != null && OverflowPopup.IsOpen)
				|| (NetPopup != null && NetPopup.IsOpen)
				|| (VolumePopup != null && VolumePopup.IsOpen)
				|| (ClockPopup != null && ClockPopup.IsOpen))
			{
				return true;
			}
		}
		catch
		{
		}
		return false;
	}

	private void AutoHideTick()
	{
		if (!_autoHide || base.Visibility != Visibility.Visible)
		{
			return;
		}
		// Never auto-hide while the user is interacting with a taskbar flyout/preview/menu (or hovering the bar) —
		// otherwise the bar slides away out from under an open overflow list, tray flyout, clock, or thumbnail.
		if (AnyTaskbarFlyoutOpen())
		{
			SetHiddenState(hide: false);
			return;
		}
		System.Drawing.Rectangle b = _screen.Bounds;
		System.Drawing.Point p = System.Windows.Forms.Cursor.Position;
		if (!b.Contains(p))
		{
			SetHiddenState(hide: true);
			return;
		}
		int thick = (int)Math.Round(HeightPx * DpiScale());
		string position = _position;
		if (1 == 0)
		{
		}
		bool flag = position switch
		{
			"Top" => p.Y <= b.Top + 2, 
			"Left" => p.X <= b.Left + 2, 
			"Right" => p.X >= b.Right - 2, 
			_ => p.Y >= b.Bottom - 2, 
		};
		if (1 == 0)
		{
		}
		bool nearEdge = flag;
		bool flag2 = !_hidden;
		bool flag3 = flag2;
		if (flag3)
		{
			string position2 = _position;
			if (1 == 0)
			{
			}
			flag = position2 switch
			{
				"Top" => p.Y <= b.Top + thick, 
				"Left" => p.X <= b.Left + thick, 
				"Right" => p.X >= b.Right - thick, 
				_ => p.Y >= b.Bottom - thick, 
			};
			if (1 == 0)
			{
			}
			flag3 = flag;
		}
		bool overBar = flag3;
		SetHiddenState(!(nearEdge | overBar));
	}

	private void CheckWallpaperChanged()
	{
		try
		{
			AppSettings s = SettingsStore.Load();
			string sig = $"{s.TaskbarColorMode}|{s.TaskbarTransparent}|{s.StartAccentColor}|{s.StartBgColor}|{s.StartPattern}|{s.StartBgMode}|{s.StartBgCustomPath}|{TaskbarTheme.WallpaperPath()}";
			if (!string.Equals(sig, _lastThemeSig, StringComparison.Ordinal))
			{
				bool first = _lastThemeSig == null;
				_lastThemeSig = sig;
				if (!first)
				{
					RaiseTaskbarColorChanged();
				}
			}
		}
		catch
		{
		}
	}

	private double DpiScale()
	{
		//IL_006a: Unknown result type (might be due to invalid IL or missing references)
		//IL_006f: Unknown result type (might be due to invalid IL or missing references)
		//IL_0033: Unknown result type (might be due to invalid IL or missing references)
		//IL_0038: Unknown result type (might be due to invalid IL or missing references)
		PresentationSource src = PresentationSource.FromVisual(this);
		double? num;
		Matrix transformToDevice;
		if (!IsVertical)
		{
			if (src == null)
			{
				num = null;
			}
			else
			{
				CompositionTarget compositionTarget = src.CompositionTarget;
				if (compositionTarget == null)
				{
					num = null;
				}
				else
				{
					transformToDevice = compositionTarget.TransformToDevice;
					num = transformToDevice.M22;
				}
			}
		}
		else if (src == null)
		{
			num = null;
		}
		else
		{
			CompositionTarget compositionTarget2 = src.CompositionTarget;
			if (compositionTarget2 == null)
			{
				num = null;
			}
			else
			{
				transformToDevice = compositionTarget2.TransformToDevice;
				num = transformToDevice.M11;
			}
		}
		double s = num ?? 1.0;
		return (s <= 0.0) ? 1.0 : s;
	}

	private void SetHiddenState(bool hide)
	{
		if (hide == _hidden && PresentationSource.FromVisual(this) != null)
		{
			return;
		}
		_hidden = hide;
		CommitPhysicalRect(hide);
	}

	private void ApplyOrientation(bool vertical)
	{
		if (_orientedVertical == vertical)
		{
			return;
		}
		_orientedVertical = vertical;
		GridLength Auto = GridLength.Auto;
		GridLength Star = new GridLength(1.0, GridUnitType.Star);
		System.Windows.Controls.Orientation Vert = System.Windows.Controls.Orientation.Vertical;
		System.Windows.Controls.Orientation Horz = System.Windows.Controls.Orientation.Horizontal;
		RootLayout.ColumnDefinitions.Clear();
		RootLayout.RowDefinitions.Clear();
		UIElement[] sections = new UIElement[4] { LeftCluster, PinnedHost, TaskArea, TrayRoot };
		if (vertical)
		{
			GridLength[] array = new GridLength[4] { Auto, Auto, Star, Auto };
			foreach (GridLength h in array)
			{
				RootLayout.RowDefinitions.Add(new RowDefinition
				{
					Height = h
				});
			}
			for (int j = 0; j < sections.Length; j++)
			{
				Grid.SetColumn(sections[j], 0);
				Grid.SetRow(sections[j], j);
			}
			LeftCluster.Orientation = Vert;
			PinnedHost.ItemsPanel = StackTemplate(typeof(StackPanel), Vert);
			TaskHost.ItemsPanel = StackTemplate(typeof(StackPanel), Vert);
			ConfigureTaskArea(vertical: true);
			TrayRoot.Orientation = Vert;
			TrayPanel.Orientation = Vert;
			ClockPanel.MinWidth = 0.0;
			ClockPanel.HorizontalAlignment = System.Windows.HorizontalAlignment.Center;
			ClockPanel.Margin = new Thickness(0.0, 8.0, 0.0, 6.0);
			ClockTime.HorizontalAlignment = System.Windows.HorizontalAlignment.Center;
			ClockDate.HorizontalAlignment = System.Windows.HorizontalAlignment.Center;
		}
		else
		{
			GridLength[] array2 = new GridLength[7]
			{
				new GridLength(0.0),
				new GridLength(0.0),
				Auto,
				Auto,
				Star,
				new GridLength(0.0),
				Auto
			};
			foreach (GridLength w in array2)
			{
				RootLayout.ColumnDefinitions.Add(new ColumnDefinition
				{
					Width = w
				});
			}
			int[] cols = new int[4] { 2, 3, 4, 6 };
			for (int l = 0; l < sections.Length; l++)
			{
				Grid.SetRow(sections[l], 0);
				Grid.SetColumn(sections[l], cols[l]);
			}
			LeftCluster.Orientation = Horz;
			PinnedHost.ItemsPanel = StackTemplate(typeof(AnimatedBar), Horz);
			TaskHost.ItemsPanel = StackTemplate(typeof(ShrinkStrip), Horz);
			ConfigureTaskArea(vertical: false);
			TrayRoot.Orientation = Horz;
			TrayPanel.Orientation = Horz;
			ClockPanel.MinWidth = 70.0;
			ClockPanel.HorizontalAlignment = System.Windows.HorizontalAlignment.Left;
			ClockPanel.Margin = new Thickness(12.0, 0.0, 12.0, 0.0);
			ClockTime.HorizontalAlignment = System.Windows.HorizontalAlignment.Right;
			ClockDate.HorizontalAlignment = System.Windows.HorizontalAlignment.Right;
			ApplyAlignment();
		}
		DateTime now = DateTime.Now;
		ClockTime.Text = now.ToString("HH:mm");
		ClockDate.Text = now.ToString((_orientedVertical == true) ? "d/M" : "ddd, d MMM");
		ReflowAppStrip();
	}

	private void ConfigureTaskArea(bool vertical)
	{
		TaskArea.ColumnDefinitions.Clear();
		TaskArea.RowDefinitions.Clear();
		if (vertical)
		{
			TaskArea.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.0, GridUnitType.Star) });
			TaskArea.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1.0, GridUnitType.Star) });
			TaskArea.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
			Grid.SetColumn(TaskHost, 0);
			Grid.SetRow(TaskHost, 0);
			Grid.SetColumn(TaskOverflowButton, 0);
			Grid.SetRow(TaskOverflowButton, 1);
			TaskOverflowButton.Width = double.NaN;
			TaskOverflowButton.Height = 40.0;
		}
		else
		{
			TaskArea.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.0, GridUnitType.Star) });
			TaskArea.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
			TaskArea.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1.0, GridUnitType.Star) });
			Grid.SetColumn(TaskHost, 0);
			Grid.SetRow(TaskHost, 0);
			Grid.SetColumn(TaskOverflowButton, 1);
			Grid.SetRow(TaskOverflowButton, 0);
			TaskOverflowButton.Width = 40.0;
			TaskOverflowButton.Height = double.NaN;
		}
	}

	private void ApplyAlignment()
	{
		try
		{
			if (_orientedVertical != true)
			{
				ColumnDefinitionCollection cols = RootLayout.ColumnDefinitions;
				if (cols.Count >= 7)
				{
					bool center = SettingsStore.Current.TaskbarAlignment == "Center";
					GridLength star = new GridLength(1.0, GridUnitType.Star);
					GridLength zero = new GridLength(0.0);
					// Centering uses a dynamic left offset (cols[0], computed in ReflowAppStrip with the FRESH tray width)
					// plus a right star (cols[5]) that keeps the tray flush-right. The offset is clamped so the cluster
					// never crosses into the tray (Win11 shifts it left when large) — so it can't overflow off the right.
					cols[1].Width = zero;
					cols[4].Width = (center ? GridLength.Auto : star);
					cols[5].Width = (center ? star : zero);
					UpdateCenterBalance();
				}
			}
		}
		catch
		{
		}
	}

	private void UpdateCenterBalance()
	{
		try
		{
			ColumnDefinitionCollection cols = RootLayout.ColumnDefinitions;
			if (_orientedVertical != true && cols.Count >= 7)
			{
				// No fixed left reservation — the symmetric col1/col5 stars center the cluster and absorb all slack, so
				// the tray can never be pushed off the right edge. The full budget (root-leading-tray) prevents the
				// premature pin overflow the old 2x-tray reservation caused.
				cols[0].Width = new GridLength(0.0);
				UpdateTaskCap();
			}
		}
		catch
		{
		}
	}

	private void UpdateTaskCap()
	{
		try
		{
			TaskHost.MaxWidth = double.PositiveInfinity;
			PinnedHost.MaxWidth = double.PositiveInfinity;
			ReflowAppStrip();
		}
		catch
		{
		}
	}

	private bool _reflowQueued;
	// Coalesce the reflow. A single resize/rebuild fires several child SizeChanged handlers that each funnel to a
	// full UpdateTaskCap/ReflowAppStrip in the SAME layout pass (and they re-trigger each other by mutating
	// ColumnDefinitions/MaxWidth) - up to ~5 full reflows per pass, each a CPU recomposite under software rendering.
	// Collapse them into ONE reflow per frame at Render priority (resolves within the current frame, no flicker).
	// Explicit build-path reflows stay synchronous; drag is still deferred inside ReflowAppStrip (_pinDragging).
	private void QueueReflow()
	{
		if (_reflowQueued)
		{
			return;
		}
		_reflowQueued = true;
		Dispatcher.BeginInvoke((Action)delegate
		{
			_reflowQueued = false;
			UpdateTaskCap();
		}, DispatcherPriority.Render);
	}

	private void ReflowAppStrip()
	{
		if (_pinDragging)
		{
			// A tray/task/size-driven reflow mid-drag would SyncCollection _visiblePinItems (moving/removing/inserting
			// containers), clobbering the gap transforms or yanking the dragged pin into overflow. Defer it to drop.
			_reflowPending = true;
			return;
		}
		if (_reflowing || TaskOverflowButton == null)
		{
			return;
		}
		_reflowing = true;
		try
		{
			bool vertical = _orientedVertical == true;
			double root = vertical ? RootLayout.ActualHeight : RootLayout.ActualWidth;
			if (root < 1.0)
			{
				SyncCollection(_visiblePinItems, _pinItems);
				SyncCollection(_visibleTasks, _tasks);
				SyncCollection(_overflowPinItems, Array.Empty<PinItem>());
				SyncCollection(_overflowTasks, Array.Empty<TaskWindow>());
				TaskOverflowButton.Visibility = Visibility.Collapsed;
				return;
			}

			double leading = vertical ? LeftCluster.ActualHeight : LeftCluster.ActualWidth;
			double tray = vertical
				? Math.Max(TrayRoot.DesiredSize.Height, TrayRoot.ActualHeight)
				: Math.Max(TrayRoot.DesiredSize.Width, TrayRoot.ActualWidth);
			// Budget = the full space between the left (Start) cluster and the tray, for BOTH alignments. Centered mode
			// used to subtract the tray width a SECOND time (to keep the cluster screen-centered), which — with a wide
			// tray like the Toolbars — starved the bar and pushed pins into the overflow flyout. Instead we keep the full
			// budget and center the cluster with a dynamic left offset (UpdateCenterBalance), shifting it left when it
			// grows — the Windows 10/11 behaviour — so icons only overflow when they genuinely can't fit.
			double budget = Math.Max(0.0, root - leading - tray - 8.0);

			TaskbarLayoutPlan plan = TaskbarLayoutPlanner.Compute(_pinItems.Count, _tasks.Count, budget);
			int visiblePins = plan.VisiblePins;
			int visibleTasks = plan.VisibleTasks;
			// Centering offset (cols[0]): computed here where `tray` is freshly measured. Screen-center the cluster, but
			// clamp so its right edge never crosses into the tray — shifting it left instead (Windows 10/11 behaviour),
			// which guarantees the tray can never be pushed off the right edge.
			if (!vertical && RootLayout.ColumnDefinitions.Count >= 7)
			{
				if (SettingsStore.Current.TaskbarAlignment == "Center")
				{
					// Measure the ACTUAL rendered cluster: AnimatedBar (pins) and ShrinkStrip (tasks) arrange children
					// edge-to-edge by their real DesiredSize, so a per-count slot estimate (48/40) drifts the cluster
					// off-centre whenever an icon's rendered width isn't exactly the slot. Fall back to the slot estimate
					// only before first layout, when both hosts still measure 0.
					double pinsW = Math.Max(PinnedHost.DesiredSize.Width, PinnedHost.ActualWidth);
					double tasksW = Math.Max(TaskHost.DesiredSize.Width, TaskHost.ActualWidth);
					double contentW = (pinsW >= 1.0 || tasksW >= 1.0)
						? leading + pinsW + tasksW
						: leading + (double)visiblePins * TaskbarLayoutPlanner.PinSlot + (double)visibleTasks * TaskbarLayoutPlanner.TaskSlot;
					double centerLeft = (root - contentW) / 2.0;
					double maxLeft = root - tray - contentW - 8.0;
					if (centerLeft > maxLeft)
					{
						centerLeft = maxLeft;
					}
					if (centerLeft < 0.0)
					{
						centerLeft = 0.0;
					}
					RootLayout.ColumnDefinitions[0].Width = new GridLength(centerLeft);
				}
				else
				{
					RootLayout.ColumnDefinitions[0].Width = new GridLength(0.0);
				}
			}

			List<PinItem> shownPins = _pinItems.Take(visiblePins).ToList();
			List<TaskWindow> shownTasks = _tasks.Take(visibleTasks).ToList();
			TaskWindow foreground = _tasks.FirstOrDefault(task => task.IsForeground);
			if (foreground != null && visibleTasks > 0 && !shownTasks.Contains(foreground))
			{
				shownTasks[^1] = foreground;
			}
			List<PinItem> hiddenPins = _pinItems.Where(item => !shownPins.Contains(item)).ToList();
			List<TaskWindow> hiddenTasks = _tasks.Where(task => !shownTasks.Contains(task)).ToList();

			SyncCollection(_visiblePinItems, shownPins);
			SyncCollection(_visibleTasks, shownTasks);
			SyncCollection(_overflowPinItems, hiddenPins);
			SyncCollection(_overflowTasks, hiddenTasks);
			bool hasOverflow = hiddenPins.Count > 0 || hiddenTasks.Count > 0;
			TaskOverflowButton.Visibility = hasOverflow ? Visibility.Visible : Visibility.Collapsed;
			PinOverflowSection.Visibility = hiddenPins.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
			TaskOverflowSection.Visibility = hiddenTasks.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
			if (!hasOverflow)
			{
				TaskOverflowPopup.IsOpen = false;
			}
		}
		finally
		{
			_reflowing = false;
		}
	}

	private static void SyncCollection<T>(ObservableCollection<T> target, IEnumerable<T> desiredItems)
	{
		List<T> desired = desiredItems.ToList();
		HashSet<T> want = new HashSet<T>(desired);   // O(1) membership → removal pass is O(n), not O(n^2)
		for (int index = target.Count - 1; index >= 0; index--)
		{
			if (!want.Contains(target[index]))
			{
				target.RemoveAt(index);
			}
		}
		for (int index = 0; index < desired.Count; index++)
		{
			T item = desired[index];
			int current = target.IndexOf(item);
			if (current < 0)
			{
				target.Insert(Math.Min(index, target.Count), item);
			}
			else if (current != index)
			{
				target.Move(current, index);
			}
		}
	}

	private static ItemsPanelTemplate StackTemplate(Type panelType, System.Windows.Controls.Orientation orientation)
	{
		FrameworkElementFactory f = new FrameworkElementFactory(panelType);
		if (panelType == typeof(StackPanel))
		{
			f.SetValue(StackPanel.OrientationProperty, orientation);
		}
		return new ItemsPanelTemplate(f);
	}

	private void OnTaskbarRightClick(object sender, MouseButtonEventArgs e)
	{
		System.Windows.Controls.ContextMenu menu = _taskbarContextMenu ?? TaskbarContextMenu.Build(this);
		_taskbarContextMenu = menu;
		TaskbarContextMenu.ApplyTheme(menu);
		menu.Placement = PlacementMode.MousePoint;
		menu.PlacementTarget = this;
		OpenTrackedMenu(menu);
		e.Handled = true;
	}

	private static void OpenTrackedMenu(System.Windows.Controls.ContextMenu menu)
	{
		menu.Closed += OnContextMenuClosed;
		_openContextMenu = menu;
		menu.IsOpen = true;
	}

	private void OnNetworkRightClick(object sender, MouseButtonEventArgs e)
	{
		bool open = !NetworkCommandPopup.IsOpen;
		CloseNetworkFlyout(animate: false);
		CloseTrayCommandPopups();
		CloseOpenContextMenu();
		if (open)
		{
			ApplyNetworkFlyoutTheme();
			NetworkCommandPopup.IsOpen = true;
		}
		e.Handled = true;
	}

	private void OnVolumeRightClick(object sender, MouseButtonEventArgs e)
	{
		bool open = !SoundCommandPopup.IsOpen;
		CloseVolumeFlyout(animate: false);
		CloseTrayCommandPopups();
		CloseOpenContextMenu();
		if (open)
		{
			ApplySoundFlyoutTheme();
			SoundCommandPopup.IsOpen = true;
		}
		e.Handled = true;
	}

	private static void CloseOpenContextMenu()
	{
		if (_openContextMenu != null)
		{
			_openContextMenu.IsOpen = false;
		}
	}

	private void CloseTrayCommandPopups()
	{
		SoundCommandPopup.IsOpen = false;
		NetworkCommandPopup.IsOpen = false;
	}

	private void OnSoundCommandPopupOpened(object sender, EventArgs e)
	{
		AnimateTrayCommandPopup(SoundCommandRoot, SoundCommandSlide);
	}

	private void OnNetworkCommandPopupOpened(object sender, EventArgs e)
	{
		AnimateTrayCommandPopup(NetworkCommandRoot, NetworkCommandSlide);
	}

	private void OnTrayCommandPopupClosed(object sender, EventArgs e)
	{
		if (sender == SoundCommandPopup)
		{
			ResetTrayCommandPopup(SoundCommandRoot, SoundCommandSlide);
		}
		else
		{
			ResetTrayCommandPopup(NetworkCommandRoot, NetworkCommandSlide);
		}
	}

	private void AnimateTrayCommandPopup(FrameworkElement root, TranslateTransform slide)
	{
		ResetTrayCommandPopup(root, slide);
		if (Motion.Mode == MotionMode.Off)
		{
			return;
		}
		System.Windows.Vector offset = _position switch
		{
			"Top" => new System.Windows.Vector(0.0, -8.0),
			"Left" => new System.Windows.Vector(-8.0, 0.0),
			"Right" => new System.Windows.Vector(8.0, 0.0),
			_ => new System.Windows.Vector(0.0, 8.0)
		};
		// Route through the Motion contract (base 90ms == old value at Authentic) so it scales with Fast/Reduced like every
		// other flyout instead of a hardcoded 90ms + local ease.
		Duration duration = Motion.Dur(Motion.Cat.Hover);
		IEasingFunction ease = Motion.Ease(Motion.Cat.Hover);
		root.CacheMode = new BitmapCache();
		root.Opacity = 0.0;
		slide.X = offset.X;
		slide.Y = offset.Y;
		DoubleAnimation fade = new DoubleAnimation(1.0, duration) { EasingFunction = ease };
		fade.Completed += delegate
		{
			root.CacheMode = null;
		};
		root.BeginAnimation(UIElement.OpacityProperty, fade, HandoffBehavior.SnapshotAndReplace);
		slide.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(0.0, duration) { EasingFunction = ease }, HandoffBehavior.SnapshotAndReplace);
		slide.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(0.0, duration) { EasingFunction = ease }, HandoffBehavior.SnapshotAndReplace);
	}

	private static void ResetTrayCommandPopup(FrameworkElement root, TranslateTransform slide)
	{
		root.BeginAnimation(UIElement.OpacityProperty, null);
		slide.BeginAnimation(TranslateTransform.XProperty, null);
		slide.BeginAnimation(TranslateTransform.YProperty, null);
		root.Opacity = 1.0;
		slide.X = 0.0;
		slide.Y = 0.0;
		root.CacheMode = null;
	}

	private void OnBatteryRightClick(object sender, MouseButtonEventArgs e)
	{
		ShowTrayMenu((FrameworkElement)sender, TaskbarContextMenu.Leaf("Power & sleep settings", 59368, delegate
		{
			TaskbarContextMenu.OpenUri("ms-settings:powersleep");
		}), TaskbarContextMenu.Leaf("Power Options", delegate
		{
			TrayRun("control.exe", "/name Microsoft.PowerOptions");
		}), TaskbarContextMenu.Leaf("Battery saver settings", delegate
		{
			TaskbarContextMenu.OpenUri("ms-settings:batterysaver");
		}));
		e.Handled = true;
	}

	private void OnClockRightClick(object sender, MouseButtonEventArgs e)
	{
		ShowTrayMenu((FrameworkElement)sender, TaskbarContextMenu.Leaf("Adjust date/time", 59271, delegate
		{
			TaskbarContextMenu.OpenUri("ms-settings:dateandtime");
		}), TaskbarContextMenu.Leaf("Date and Time settings", delegate
		{
			TrayRun("control.exe", "timedate.cpl");
		}));
		e.Handled = true;
	}

	private void ShowTrayMenu(FrameworkElement target, params object[] items)
	{
		System.Windows.Controls.ContextMenu ctx = new System.Windows.Controls.ContextMenu
		{
			Style = (Style)System.Windows.Application.Current.Resources["Win81ContextMenu"]
		};
		TaskbarContextMenu.ApplyTheme(ctx);
		foreach (object it in items)
		{
			ctx.Items.Add(it);
		}
		ctx.PlacementTarget = target;
		ctx.Placement = FlyoutMode();
		OpenTrackedMenu(ctx);
	}

	private static void TrayRun(string exe, string args)
	{
		ShellLaunch.Run(delegate
		{
			try
			{
				Process.Start(new ProcessStartInfo(exe, args) { UseShellExecute = true });
			}
			catch (Exception ex)
			{
				Logger.Log($"Tray menu launch {exe} {args} failed: {ex.Message}");
			}
		});
	}

	private static void OnContextMenuClosed(object? sender, RoutedEventArgs e)
	{
		if (sender is System.Windows.Controls.ContextMenu m)
		{
			m.Closed -= OnContextMenuClosed;
			if (_openContextMenu == m)
			{
				_openContextMenu = null;
			}
		}
	}

	public static void CloseContextMenuOnOutsideClick(int sx, int sy)
	{
		System.Windows.Controls.ContextMenu menu = _openContextMenu;
		if (menu != null && menu.IsOpen)
		{
			nint hwnd = WindowFromPoint(new POINT
			{
				X = sx,
				Y = sy
			});
			if (hwnd == IntPtr.Zero || TrayReader.ProcessIdOf(hwnd) != (uint)Environment.ProcessId)
			{
				menu.IsOpen = false;
			}
		}
	}

	public bool ContainsScreenPoint(int sx, int sy)
	{
		return _screen.Bounds.Contains(sx, sy);
	}

	public void ShowDesktopMenu(int sx, int sy)
	{
		//IL_0034: Unknown result type (might be due to invalid IL or missing references)
		//IL_0039: Unknown result type (might be due to invalid IL or missing references)
		//IL_007f: Unknown result type (might be due to invalid IL or missing references)
		//IL_0084: Unknown result type (might be due to invalid IL or missing references)
		try
		{
			PresentationSource src = PresentationSource.FromVisual(this);
			double? num;
			Matrix transformToDevice;
			if (src == null)
			{
				num = null;
			}
			else
			{
				CompositionTarget compositionTarget = src.CompositionTarget;
				if (compositionTarget == null)
				{
					num = null;
				}
				else
				{
					transformToDevice = compositionTarget.TransformToDevice;
					num = transformToDevice.M11;
				}
			}
			double dx = num ?? 1.0;
			double? num2;
			if (src == null)
			{
				num2 = null;
			}
			else
			{
				CompositionTarget compositionTarget2 = src.CompositionTarget;
				if (compositionTarget2 == null)
				{
					num2 = null;
				}
				else
				{
					transformToDevice = compositionTarget2.TransformToDevice;
					num2 = transformToDevice.M22;
				}
			}
			double dy = num2 ?? 1.0;
			if (dx <= 0.0)
			{
				dx = 1.0;
			}
			if (dy <= 0.0)
			{
				dy = 1.0;
			}
			if (_menuHost == null)
			{
				_menuHost = new Window
				{
					WindowStyle = WindowStyle.None,
					ResizeMode = ResizeMode.NoResize,
					ShowInTaskbar = false,
					AllowsTransparency = true,
					Background = System.Windows.Media.Brushes.Transparent,
					Width = 1.0,
					Height = 1.0,
					Topmost = true
				};
			}
			_menuHost.Left = (double)sx / dx;
			_menuHost.Top = (double)sy / dy;
			_menuHost.Show();
			_menuHost.Activate();
			System.Windows.Controls.ContextMenu menu = DesktopContextMenu.Build();
			menu.PlacementTarget = _menuHost;
			menu.Placement = PlacementMode.Bottom;
			menu.Closed += delegate
			{
				try
				{
					_menuHost?.Hide();
				}
				catch
				{
				}
			};
			OpenTrackedMenu(menu);
			int px = sx;
			int py = sy;
			((DispatcherObject)menu).Dispatcher.BeginInvoke((Delegate)(Action)delegate
			{
				if (PresentationSource.FromVisual(menu) is HwndSource hwndSource && hwndSource.Handle != IntPtr.Zero)
				{
					SetWindowPos(hwndSource.Handle, IntPtr.Zero, px, py, 0, 0, 21u);
				}
			}, (DispatcherPriority)6, Array.Empty<object>());
		}
		catch (Exception ex)
		{
			Logger.Log("ShowDesktopMenu failed: " + ex.Message);
		}
	}

	private void UpdateFullscreenVisibility()
	{
		if (IsFullscreenForeground())
		{
			// Only TRUE fullscreen (non-maximized, covers the whole monitor) reaches here now — such a window already
			// covers the taskbar's strip, so simply hiding the bar leaves no gap. No work-area release (that release was
			// what turned maximized windows into "fullscreen" and stuck the bar hidden).
			if (base.Visibility == Visibility.Visible)
			{
				base.Visibility = Visibility.Hidden;
			}
			SetFullscreenHidden(true);
		}
		else if (base.Visibility != Visibility.Visible && !_charmsOpen)
		{
			base.Visibility = Visibility.Visible;
			base.Topmost = true;
			PlaceOnScreen();   // re-assert full edge size after a fullscreen-hidden episode
			SetFullscreenHidden(false);
		}
		else
		{
			SetFullscreenHidden(false);
		}
	}

	private void Refresh()
	{
		if (IsFullscreenForeground())
		{
			if (base.Visibility == Visibility.Visible)
			{
				base.Visibility = Visibility.Hidden;
			}
			SetFullscreenHidden(true);
			return;
		}
		if (base.Visibility != Visibility.Visible)
		{
			if (_charmsOpen)
			{
				return;
			}
			base.Visibility = Visibility.Visible;
			base.Topmost = true;
			PlaceOnScreen();   // re-assert full edge size after any hide/show cycle
		}
		SetFullscreenHidden(false);
		if (++_refreshCount % 12 == 0)
		{
			_screenCache.Clear();
			_exeCache.Clear();
		}
		// Keep the idle working set low over long uptime: trim at most every 45s (primary bar only, one process-wide
		// call). EmptyWorkingSet is cheap and idle pages fault back lazily only if touched, so there is no perceptible
		// cost while the shell sits idle — which is most of the time.
		if (_isPrimary)
		{
			long trimNow = Environment.TickCount64;
			if (trimNow - _lastWorkingSetTrimMs >= 45000L)
			{
				_lastWorkingSetTrimMs = trimNow;
				NativeShell.TrimSelf();
				// Keep native hosts dormant: if Windows respawned StartMenuExperienceHost/SearchHost in the background,
				// re-suspend + trim the fresh instance back to WS ~0 (churn-free; no kill/respawn cycle). No-op when
				// native suppression is off (nothing to re-suspend).
				if (SettingsStore.FastSnapshot.SuspendNativeStart)
				{
					NativeShell.ReassertSuspension();
				}
			}
		}
		DateTime now = DateTime.Now;
		ClockTime.Text = now.ToString("HH:mm");
		ClockDate.Text = now.ToString((_orientedVertical == true) ? "d/M" : "ddd, d MMM");
		if (_isPrimary)
		{
			UpdateTray();
		}
		if (_isPrimary && _refreshCount % 6 == 0)
		{
			CheckWallpaperChanged();
		}
		if (_refreshCount % 6 == 0)
		{
			int thicknessPx = IsVertical ? _tbTargetW : _tbTargetH;
			TaskbarWorkArea.EnsureReserved(_screen, _position, thicknessPx);
		}
		if (_gatherBusy)
		{
			_refreshAfterGather = true;
			return;
		}
		_gatherBusy = true;
		try
		{
			nint ownHwnd = new WindowInteropHelper(this).Handle;
			uint ownPid = _ownPid;
			string device = _screen.DeviceName;
			bool needAumid = _pinsByAumid.Count > 0;
			bool sameAll = SettingsStore.Current.TaskbarSameOnAllDisplays;
			nint pcSettings = PcSettingsWindow.LiveHwnd;
			Task.Run(delegate
			{
				try
				{
					List<LiveWin> list;
					try
					{
						list = GatherLive(ownHwnd, ownPid, device, needAumid, pcSettings, sameAll);
					}
					catch
					{
						list = new List<LiveWin>();
					}
					((DispatcherObject)this).Dispatcher.BeginInvoke((Delegate)(Action)delegate
					{
						_gatherBusy = false;
						bool refreshAgain = _refreshAfterGather;
						_refreshAfterGather = false;
						if (_stopped || base.Visibility != Visibility.Visible)
						{
							return;
						}
						try
						{
							ApplyLive(list, GetEffectiveForegroundWindow(), needAumid);
						}
						catch (Exception ex)
						{
							Logger.Log("ApplyLive: " + ex.Message);
						}
						if (refreshAgain)
						{
							QueueRefresh();
						}
					}, Array.Empty<object>());
				}
				catch
				{
					_gatherBusy = false;
				}
			});
		}
		catch
		{
			_gatherBusy = false;
		}
	}

	private List<LiveWin> GatherLive(nint ownHwnd, uint ownPid, string device, bool needAumid, nint pcSettings, bool sameAll)
	{
		List<LiveWin> result = new List<LiveWin>();
		foreach (var (hwnd, title) in WindowList.Enumerate(ownHwnd, pcSettings))
		{
			if ((sameAll || OnThisScreen(hwnd, device)) && (TrayReader.ProcessIdOf(hwnd) != ownPid || hwnd == pcSettings))
			{
				string exe = ExeFor(hwnd);
				string aumid = (needAumid ? AumidForWindow(hwnd) : null);
				if (IsExplorerHost(exe) || IsSharedHostExe(exe))
				{
					// A shared host process (explorer.exe for Control Panel/This PC; ApplicationFrameHost for UWP;
					// mmc/rundll32/dllhost/python/java) hosts several DISTINCT apps. Use the per-window AUMID as the
					// identity so each hosted app gets its own button/grouping instead of being merged under the host exe.
					string winAppId = AppInventory.GetWindowAppId(hwnd);
					if (!string.IsNullOrEmpty(winAppId) && !winAppId.Equals(ExplorerAumid, StringComparison.OrdinalIgnoreCase))
					{
						if (aumid == null)
						{
							aumid = winAppId;
						}
						exe = "shell:" + winAppId;
					}
					else if (!IsExplorerHost(exe))
					{
						// Non-explorer shared host with no resolvable per-window AUMID: fall to per-window identity
						// (w:hwnd in BuildTaskSpecs) rather than lumping every window into one host-exe button.
						// (explorer.exe with no AUMID = a real File Explorer window → keep exe so those still group.)
						exe = null;
					}
				}
				result.Add(new LiveWin(hwnd, title, exe, aumid));
			}
		}
		return result;
	}

	private void ApplyLive(List<LiveWin> live, nint fg, bool aumidsGathered)
	{
		foreach (PinnedTile t in _allTiles)
		{
			t.RunningHwnds.Clear();
			t.IsRunning = false;
			t.IsForeground = false;
		}
		HashSet<nint> absorbed = new HashSet<nint>();
		// Always fold a running window into its pinned button (so the pin lights up / shows its window count) — even in
		// Combine=Never. Whether NON-pinned windows combine is governed independently inside BuildTaskSpecs, so this
		// does not change Never-Combine behavior for unpinned apps; it only stops a pinned app from also showing a
		// separate running button (the classic duplicate).
		bool absorbPins = true;
		if (absorbPins && _pinsByExe.Count > 0)
		{
			foreach (LiveWin w in live)
			{
				if (w.Exe != null && _pinsByExe.TryGetValue(w.Exe, out PinnedTile tile))
				{
					tile.RunningHwnds.Add(w.Hwnd);
					tile.IsRunning = true;
					if (w.Hwnd == fg)
					{
						tile.IsForeground = true;
					}
					absorbed.Add(w.Hwnd);
				}
			}
		}
		// Filename fallback: a running window whose FULL path did not match any pin (Discord app-*\ version
		// drift, Chrome install-location differences) still folds into its pin by exe filename. Runs after the
		// exact-path pass so an exact match always wins. Only real filesystem exes are eligible: skip the
		// shell:<aumid> rewrites (UWP / Control Panel / This PC given a per-window AUMID), explorer.exe (File
		// Explorer folds by its stable exact path; Control Panel / This PC must not fold here), and the
		// shared-host exes. This touches PINNED apps only, so Combine=Never for unpinned apps is unaffected.
		if (absorbPins && _pinsByExeName.Count > 0)
		{
			foreach (LiveWin wn in live)
			{
				if (absorbed.Contains(wn.Hwnd))
				{
					continue;
				}
				string wnExe = wn.Exe;
				// explorer.exe is allowed here (unlike the general shared-host skip): only the File Explorer pin ever
				// claims the "explorer.exe" filename key, so File Explorer / This PC / Control Panel windows fold into
				// it. shell:<aumid> rewrites (UWP / distinct shell apps) and the OTHER shared hosts stay excluded.
				if (wnExe == null || wnExe.StartsWith("shell:", StringComparison.OrdinalIgnoreCase) || IsSharedHostExe(wnExe))
				{
					continue;
				}
				if (_pinsByExeName.TryGetValue(System.IO.Path.GetFileName(wnExe), out PinnedTile tileN) && tileN != null)
				{
					tileN.RunningHwnds.Add(wn.Hwnd);
					tileN.IsRunning = true;
					if (wn.Hwnd == fg)
					{
						tileN.IsForeground = true;
					}
					absorbed.Add(wn.Hwnd);
				}
			}
		}
		if ((absorbPins & aumidsGathered) && _pinsByAumid.Count > 0)
		{
			foreach (LiveWin w2 in live)
			{
				if (!absorbed.Contains(w2.Hwnd) && w2.Aumid != null && _pinsByAumid.TryGetValue(w2.Aumid, out PinnedTile tile2))
				{
					tile2.RunningHwnds.Add(w2.Hwnd);
					tile2.IsRunning = true;
					if (w2.Hwnd == fg)
					{
						tile2.IsForeground = true;
					}
					absorbed.Add(w2.Hwnd);
				}
			}
		}
		foreach (PinnedTile t2 in _allTiles)
		{
			t2.MultiInstance = t2.RunningHwnds.Count >= 2;
		}
		foreach (GroupTile g in _groups)
		{
			g.RunningHwnds.Clear();
			foreach (PinnedTile m in g.Members)
			{
				g.RunningHwnds.AddRange(m.RunningHwnds);
			}
			g.IsRunning = g.RunningHwnds.Count > 0;
			g.IsForeground = g.Members.Any((PinnedTile pinnedTile) => pinnedTile.IsForeground);
			g.MultiInstance = g.RunningHwnds.Count >= 2;
		}
		List<(nint, string, string)> visible = (from liveWin in live
			where !absorbed.Contains(liveWin.Hwnd)
			select (Hwnd: liveWin.Hwnd, Title: liveWin.Title, Exe: liveWin.Exe)).ToList();
		List<TaskSpec> specs = BuildTaskSpecs(visible, fg);
		HashSet<string> wantKeys = new HashSet<string>(specs.Select((TaskSpec taskSpec) => taskSpec.Key));
		foreach (string goneKey in _byKey.Keys.Where((string k) => !wantKeys.Contains(k)).ToList())
		{
			_tasks.Remove(_byKey[goneKey]);
			_byKey.Remove(goneKey);
		}
		foreach (TaskSpec s in specs)
		{
			if (_byKey.TryGetValue(s.Key, out TaskWindow tw))
			{
				tw.Hwnd = s.Rep;
				tw.Title = s.Title;
				tw.ShowLabel = s.ShowLabel;
				tw.Members.Clear();
				tw.Members.AddRange(s.Members);
				tw.Count = s.Members.Count;
				tw.IsForeground = s.Members.Contains(fg);
				if (tw.Icon == null)
				{
					ResolveIconAsync(tw, s.Rep);
				}
			}
			else
			{
				TaskWindow nw = new TaskWindow
				{
					Key = s.Key,
					Hwnd = s.Rep,
					Title = s.Title,
					ShowLabel = s.ShowLabel,
					IsForeground = s.Members.Contains(fg)
				};
				nw.Members.AddRange(s.Members);
				nw.Count = s.Members.Count;
				_byKey[s.Key] = nw;
				_tasks.Add(nw);
				ResolveIconAsync(nw, s.Rep);
			}
		}
		ReflowAppStrip();
	}

	private void ResolveIconAsync(TaskWindow tw, nint hwnd)
	{
		if (hwnd == IntPtr.Zero || !_iconResolving.Add(hwnd))
		{
			return;
		}
		if (hwnd == PcSettingsWindow.LiveHwnd)
		{
			_iconResolving.Remove(hwnd);
			try
			{
				tw.Icon = PcSettingsWindow.BuildGearIcon();
				return;
			}
			catch
			{
				return;
			}
		}
		Task.Run(delegate
		{
			ImageSource icon = null;
			try
			{
				if (SettingsStore.Current.Replace81AppIcons)
				{
					string windowAppId = AppInventory.GetWindowAppId(hwnd);
					if (!string.IsNullOrEmpty(windowAppId))
					{
						icon = AppIconOverrides.ResolveByAumid(windowAppId);
					}
				}
			}
			catch
			{
			}
			try
			{
				string exePath = WindowList.GetExePath(hwnd);
				// Desktop apps (Firefox/devenv/Code) tag no window AUMID, so resolve the 8.1 override by exe name BEFORE the
				// native LoadIcon fallback — otherwise a running window keeps its native taskbar icon.
				if (icon == null && SettingsStore.Current.Replace81AppIcons)
				{
					icon = AppIconOverrides.ResolveByExe(exePath);
				}
				if (icon == null && IsExplorerHost(exePath))
				{
					// explorer-hosted shell app (e.g. Control Panel): use the shell item's own icon by its AUMID.
					string winAppId2 = AppInventory.GetWindowAppId(hwnd);
					if (!string.IsNullOrEmpty(winAppId2) && !winAppId2.Equals(ExplorerAumid, StringComparison.OrdinalIgnoreCase))
					{
						icon = AppInventory.LoadIcon("shell:AppsFolder\\" + winAppId2);
					}
					else if (SettingsStore.Current.Replace81AppIcons)
					{
						// A genuine File Explorer window (Explorer AUMID, or none) — apply the 8.1 File Explorer override
						// so explorer.exe windows get the icon too (Win+E / folders opened outside the pin).
						icon = AppIconOverrides.ResolveByAumid(ExplorerAumid);
					}
				}
				else if (icon == null && !string.IsNullOrEmpty(exePath) && !IsSharedHostExe(exePath))
				{
					icon = AppInventory.LoadIcon(exePath);
				}
			}
			catch
			{
			}
			if (icon == null)
			{
				icon = WindowList.GetIcon(hwnd);
			}
			// Guarantee a real, non-blank icon so a running window whose icon resolves to a blank/white handle (common
			// for Electron/Chromium apps like Gather) shows its title initial instead of a white circle.
			icon = IconResolver.EnsureNonBlank(AppInventory.EnsureVisibleOnDark(icon), tw.Title);
			((DispatcherObject)this).Dispatcher.BeginInvoke((Delegate)(Action)delegate
			{
				_iconResolving.Remove(hwnd);
				if (tw.Icon == null)
				{
					tw.Icon = icon;
				}
			}, Array.Empty<object>());
		});
	}

	private static bool IsSharedHostExe(string exePath)
	{
		string name = System.IO.Path.GetFileName(exePath);
		string[] sharedHostExes = SharedHostExes;
		foreach (string h in sharedHostExes)
		{
			if (name.Equals(h, StringComparison.OrdinalIgnoreCase))
			{
				return true;
			}
		}
		return false;
	}

	private const string ExplorerAumid = "Microsoft.Windows.Explorer";

	// explorer.exe is the shared host for several distinct shell "apps" (File Explorer, Control Panel,
	// This PC, ...). Detect it so we can split those into separate taskbar identities by per-window AUMID.
	private static bool IsExplorerHost(string exe)
	{
		return !string.IsNullOrEmpty(exe) && System.IO.Path.GetFileName(exe).Equals("explorer.exe", StringComparison.OrdinalIgnoreCase);
	}

	// A shell:AppsFolder pin has no ExePath and, on Win11, its live window exposes no AppUserModel.ID — so it folds
	// neither by exact path nor by AUMID. Map the known shell/legacy AUMIDs to the backing process exe filename so a
	// running window folds into the pin via the filename index (_pinsByExeName). Returns null for AUMIDs we don't
	// map (those keep AUMID-only matching). Control Panel is deliberately NOT mapped: its windows are explorer.exe
	// with an empty AUMID too, indistinguishable from File Explorer, so they fold into the File Explorer pin.
	private static string? ShellPinExeName(string? aumid)
	{
		if (string.IsNullOrEmpty(aumid))
		{
			return null;
		}
		if (aumid.Equals("Microsoft.Windows.Explorer", StringComparison.OrdinalIgnoreCase))
		{
			return "explorer.exe";
		}
		if (aumid.Equals("Microsoft.AutoGenerated.{923DD477-5846-686B-A659-0FCCD73851A8}", StringComparison.OrdinalIgnoreCase))
		{
			return "taskmgr.exe";   // Task Manager (this box's shell-generated AppID; matches AppIconOverrides)
		}
		return null;
	}

	private List<TaskSpec> BuildTaskSpecs(List<(nint Hwnd, string Title, string? Exe)> visible, nint fg)
	{
		string taskbarCombine = SettingsStore.Current.TaskbarCombine;
		if (1 == 0)
		{
		}
		bool flag = !(taskbarCombine == "Never") && (!(taskbarCombine == "WhenFull") || (double)(visible.Count * 168) > ((TaskHost.ActualWidth > 1.0) ? TaskHost.ActualWidth : 800.0));
		if (1 == 0)
		{
		}
		bool combine = flag;
		List<TaskSpec> specs = new List<TaskSpec>();
		if (!combine)
		{
			foreach (var item in visible)
			{
				nint hwnd = item.Hwnd;
				string title = item.Title;
				string key = "w:" + (IntPtr)hwnd;
				nint rep = hwnd;
				int num = 1;
				List<nint> list = new List<nint>(num);
				CollectionsMarshal.SetCount(list, num);
				Span<nint> span = CollectionsMarshal.AsSpan(list);
				int index = 0;
				span[index] = hwnd;
				specs.Add(new TaskSpec(key, rep, title, list, ShowLabel: true));
			}
			return specs;
		}
		Dictionary<string, List<(nint, string)>> groups = new Dictionary<string, List<(nint, string)>>(StringComparer.OrdinalIgnoreCase);
		List<string> order = new List<string>();
		foreach (var w in visible)
		{
			string key2 = w.Exe ?? ("w:" + (IntPtr)w.Hwnd);
			if (!groups.TryGetValue(key2, out var lst))
			{
				lst = (groups[key2] = new List<(nint, string)>());
				order.Add(key2);
			}
			lst.Add((w.Hwnd, w.Title));
		}
		foreach (string key3 in order)
		{
			List<(nint, string)> members = groups[key3];
			nint rep2 = (members.Any<(nint, string)>(((nint Hwnd, string Title) m) => m.Hwnd == fg) ? fg : members[0].Item1);
			string title2 = ((members.Count == 1 || key3.StartsWith("w:") || key3.StartsWith("shell:")) ? members[0].Item2 : System.IO.Path.GetFileNameWithoutExtension(key3));
			specs.Add(new TaskSpec("g:" + key3, rep2, title2, members.Select<(nint, string), nint>(((nint Hwnd, string Title) m) => m.Hwnd).ToList(), ShowLabel: false));
		}
		return specs;
	}

	private string? ExeFor(nint hwnd)
	{
		if (_exeCache.TryGetValue(hwnd, out string cached))
		{
			return cached;
		}
		if (_exeCache.Count > 256)
		{
			_exeCache.Clear();
		}
		string exe = WindowList.GetExePath(hwnd);
		_exeCache[hwnd] = exe;
		return exe;
	}

	private bool OnThisScreen(nint hwnd)
	{
		return OnThisScreen(hwnd, _screen.DeviceName);
	}

	private bool OnThisScreen(nint hwnd, string device)
	{
		if (!_screenCache.TryGetValue(hwnd, out string dev))
		{
			try
			{
				dev = Screen.FromHandle(hwnd).DeviceName;
			}
			catch
			{
				dev = device;
			}
			_screenCache[hwnd] = dev;
		}
		return dev == device;
	}

	private void OnStartClick(object sender, RoutedEventArgs e)
	{
		Logger.Log("[taskbar-diag] Start button Click fired (StartButton hit)");
		StartRequested?.Invoke();
	}

	private void UpdateTray()
	{
		if (!VolumePopup.IsOpen)
		{
			_tray.VolumePct = (int)Math.Round(_audio.GetVolume() * 100f);
			_tray.Muted = _audio.GetMute();
		}
		if (_tick % 2 == 0)
		{
			ScanTrayAsync();
		}
		bool doPerf = _tray.PerfVisible;
		bool doSlow = _tick % 5 == 0;
		_tick++;
		if (!(doPerf | doSlow) || _trayTelemetryBusy)
		{
			return;
		}
		_trayTelemetryBusy = true;
		Task.Run(delegate
		{
			string cpu = null;
			string ram = null;
			string netText = null;
			if (doPerf)
			{
				_perf.Update();
				cpu = $"CPU {_perf.CpuPercent}%";
				ram = $"RAM {_perf.RamPercent}%";
				netText = "↓" + PerfMonitor.Rate(_perf.DownBytesPerSec) + "  ↑" + PerfMonitor.Rate(_perf.UpBytesPerSec);
			}
			(bool present, int percent, bool charging, bool saver) power = (doSlow ? PowerStatus.Read() : default((bool, int, bool, bool)));
			NetState81 net = doSlow ? NetState81.Read() : null;
			((DispatcherObject)this).Dispatcher.BeginInvoke((Delegate)(Action)delegate
			{
				try
				{
					if (doPerf)
					{
						_tray.CpuText = cpu;
						_tray.RamText = ram;
						_tray.NetText = netText;
					}
					if (doSlow)
					{
						_tray.BatteryVisible = power.present;
						if (power.present)
						{
							_tray.BatteryPct = power.percent;
							_tray.Charging = power.charging;
							_tray.Saver = power.saver;
						}
						if (!power.present) { _tray.BatteryPct = 0; _tray.Charging = false; _tray.Saver = false; }
							if (net != null) _tray.SetNetwork(net);
					}
				}
				finally
				{
					_trayTelemetryBusy = false;
				}
			}, Array.Empty<object>());
		});
	}

	// Win11 real tray host raised its coalesced Changed event on a threadpool thread — marshal to the UI thread and
	// re-pull the icon table into this taskbar.
	private void OnTrayHostChanged()
	{
		try
		{
			((DispatcherObject)this).Dispatcher.BeginInvoke((Delegate)(Action)ScanTrayAsync, Array.Empty<object>());
		}
		catch
		{
		}
	}

	protected override void OnClosed(EventArgs e)
	{
		try { TrayHostService.Changed -= OnTrayHostChanged; } catch { }
		try { ToolbarsChanged -= RebuildToolbars; } catch { }
		base.OnClosed(e);
	}

	// ---- Classic taskbar "Toolbars" (Desktop / Links / custom folders) shown next to the tray ----

	private void RebuildToolbars()
	{
		try
		{
			AppSettings s = SettingsStore.Current;
			_toolbars.Clear();
			foreach (string path in (s.TaskbarToolbars ?? new System.Collections.Generic.List<string>()))
			{
				if (string.IsNullOrWhiteSpace(path))
				{
					continue;
				}
				string name;
				try { name = new System.IO.DirectoryInfo(path).Name; if (string.IsNullOrEmpty(name)) { name = path; } } catch { name = path; }
				_toolbars.Add(new ToolbarVm { Name = name, Path = path });
			}
			AddressBox.Visibility = (s.TaskbarAddressBar ? Visibility.Visible : Visibility.Collapsed);
			if (_isPrimary)
			{
				Logger.Log($"Toolbars rebuilt: {_toolbars.Count} folder toolbar(s), address={s.TaskbarAddressBar}");
			}
		}
		catch (Exception ex)
		{
			Logger.Log("RebuildToolbars failed: " + ex.Message);
		}
	}

	private void OnToolbarClick(object sender, RoutedEventArgs e)
	{
		if ((sender as FrameworkElement)?.Tag is string path)
		{
			System.Windows.Controls.ContextMenu menu = new System.Windows.Controls.ContextMenu
			{
				Style = (Style)System.Windows.Application.Current.Resources["Win81ContextMenu"]
			};
			TaskbarContextMenu.ApplyTheme(menu);
			PopulateFolderItems(menu.Items, path, 0);
			menu.PlacementTarget = (UIElement)sender;
			menu.Placement = FlyoutMode();
			OpenTrackedMenu(menu);
		}
	}

	private void OnToolbarRightClick(object sender, MouseButtonEventArgs e)
	{
		if ((sender as FrameworkElement)?.Tag is string path)
		{
			System.Windows.Controls.ContextMenu menu = new System.Windows.Controls.ContextMenu
			{
				Style = (Style)System.Windows.Application.Current.Resources["Win81ContextMenu"]
			};
			TaskbarContextMenu.ApplyTheme(menu);
			menu.Items.Add(TaskbarContextMenu.Leaf("Open folder", 57736, delegate { LaunchToolbarPath(path); }));
			menu.Items.Add(TaskbarContextMenu.Sep());
			menu.Items.Add(TaskbarContextMenu.Leaf("Remove this toolbar", 59579, delegate { TaskbarContextMenu.RemoveToolbar(path); }));
			menu.PlacementTarget = (UIElement)sender;
			menu.Placement = FlyoutMode();
			OpenTrackedMenu(menu);
			e.Handled = true;
		}
	}

	private void OnAddressBoxKeyUp(object sender, System.Windows.Input.KeyEventArgs e)
	{
		if (e.Key == System.Windows.Input.Key.Enter && sender is System.Windows.Controls.TextBox tb)
		{
			string text = (tb.Text ?? "").Trim();
			if (text.Length > 0)
			{
				LaunchToolbarPath(text);
				tb.SelectAll();
			}
		}
	}

	// Populate a flyout with a folder's items: subfolders as (lazy) submenus, files/shortcuts as launchable leaves.
	private void PopulateFolderItems(ItemCollection items, string path, int depth)
	{
		try
		{
			System.IO.DirectoryInfo di = new System.IO.DirectoryInfo(path);
			if (!di.Exists)
			{
				items.Add(TaskbarContextMenu.Leaf("(folder not found)", delegate { }, enabled: false));
				return;
			}
			System.IO.DirectoryInfo[] dirs;
			System.IO.FileInfo[] files;
			try { dirs = di.GetDirectories(); } catch { dirs = Array.Empty<System.IO.DirectoryInfo>(); }
			try { files = di.GetFiles(); } catch { files = Array.Empty<System.IO.FileInfo>(); }
			Array.Sort(dirs, (System.IO.DirectoryInfo a, System.IO.DirectoryInfo b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
			Array.Sort(files, (System.IO.FileInfo a, System.IO.FileInfo b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
			int count = 0;
			foreach (System.IO.DirectoryInfo d in dirs)
			{
				if ((d.Attributes & System.IO.FileAttributes.Hidden) != 0 || (d.Attributes & System.IO.FileAttributes.System) != 0) { continue; }
				if (count++ >= 150) { break; }
				string dpath = d.FullName;
				System.Windows.Controls.MenuItem sub = TaskbarContextMenu.LeafFolderIcon(d.Name, 57744, delegate { LaunchToolbarPath(dpath); });
				if (depth < 4)
				{
					sub.Items.Add(new System.Windows.Controls.MenuItem { Header = "…", IsEnabled = false });   // placeholder so the submenu arrow shows
					bool populated = false;
					sub.SubmenuOpened += delegate
					{
						if (!populated)
						{
							populated = true;
							sub.Items.Clear();
							PopulateFolderItems(sub.Items, dpath, depth + 1);
						}
					};
				}
				items.Add(sub);
			}
			foreach (System.IO.FileInfo f in files)
			{
				if ((f.Attributes & System.IO.FileAttributes.Hidden) != 0 || (f.Attributes & System.IO.FileAttributes.System) != 0) { continue; }
				if (count++ >= 150) { break; }
				string fpath = f.FullName;
				string display = f.Extension.Equals(".lnk", StringComparison.OrdinalIgnoreCase) || f.Extension.Equals(".url", StringComparison.OrdinalIgnoreCase)
					? System.IO.Path.GetFileNameWithoutExtension(f.Name)
					: f.Name;
				items.Add(TaskbarContextMenu.LeafFileIcon(display, f.Extension, 57667, delegate { LaunchToolbarPath(fpath); }));
			}
			if (items.Count == 0)
			{
				items.Add(TaskbarContextMenu.Leaf("(empty)", delegate { }, enabled: false));
			}
			items.Add(TaskbarContextMenu.Sep());
			items.Add(TaskbarContextMenu.Leaf("Open folder", 57736, delegate { LaunchToolbarPath(path); }));
		}
		catch (Exception ex)
		{
			Logger.Log("toolbar flyout failed: " + ex.Message);
		}
	}

	private static void LaunchToolbarPath(string path)
	{
		try
		{
			Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
		}
		catch (Exception ex)
		{
			Logger.Log("toolbar launch failed (" + path + "): " + ex.Message);
		}
	}

	private void QueueTrayScan()
	{
		//IL_0011: Unknown result type (might be due to invalid IL or missing references)
		//IL_0016: Unknown result type (might be due to invalid IL or missing references)
		//IL_002d: Expected O, but got Unknown
		if (_trayScanDebounce == null)
		{
			_trayScanDebounce = new DispatcherTimer((DispatcherPriority)4)
			{
				Interval = TimeSpan.FromMilliseconds(300L)
			};
			_trayScanDebounce.Tick += delegate
			{
				_trayScanDebounce.Stop();
				ScanTrayAsync();
			};
		}
		_trayScanDebounce.Stop();
		_trayScanDebounce.Start();
	}

	private volatile bool _trayScanDirty;

	private void ScanTrayAsync()
	{
		if (_trayScanBusy)
		{
			_trayScanDirty = true;   // a tray Changed arrived while a scan was in flight — coalesce one more pass
			return;
		}
		_trayScanBusy = true;
		Task.Run(delegate
		{
			List<TrayIconInfo> infos;
			try
			{
				// Win11 (>=22000) has no classic ToolbarWindow32 tray chain — read the notification-area registry
				// instead. Legacy Win8.1/Win10 (and the reference VM) keep the ToolbarWindow32/TRAYDATA path.
				// Win11: prefer the real tray host (owner window + callback message => real app menus). It falls back to
				// the render-only registry reader if the host isn't active. Legacy keeps the ToolbarWindow32 path.
				infos = TrayHostService.Active ? TrayHostService.Snapshot() : (Win11TrayReader.IsWin11 ? Win11TrayReader.Enumerate() : TrayReader.Enumerate());
			}
			catch (Exception ex)
			{
				Logger.Log("TrayReader failed: " + ex.Message);
				infos = new List<TrayIconInfo>();
			}
			((DispatcherObject)this).Dispatcher.BeginInvoke((Delegate)(Action)delegate
			{
				try
				{
					ApplyTrayInfos(infos);
				}
				finally
				{
					_trayScanBusy = false;
					if (_trayScanDirty)
					{
						_trayScanDirty = false;
						ScanTrayAsync();   // pick up the icon change(s) that arrived during this scan
					}
				}
			}, Array.Empty<object>());
		});
	}

	private void RefreshAppIcons()
	{
		ApplyTrayInfos(TrayHostService.Active ? TrayHostService.Snapshot() : (Win11TrayReader.IsWin11 ? Win11TrayReader.Enumerate() : TrayReader.Enumerate()));
	}

	private static System.Windows.Media.ImageSource _audioSwitchImage;

	private static bool _audioSwitchTried;

	// Neutral "switch audio output" icon (white speaker + swap arrows) for the repurposed system audio indicator.
	private static System.Windows.Media.ImageSource AudioSwitchImage()
	{
		if (!_audioSwitchTried)
		{
			_audioSwitchTried = true;
			try
			{
				string file = System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "Win81Icons", "uwp", "AudioSwitch.png");
				if (System.IO.File.Exists(file))
				{
					System.Windows.Media.Imaging.BitmapImage bi = new System.Windows.Media.Imaging.BitmapImage();
					bi.BeginInit();
					bi.UriSource = new Uri(file);
					bi.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
					bi.EndInit();
					bi.Freeze();
					_audioSwitchImage = bi;
				}
			}
			catch
			{
			}
		}
		return _audioSwitchImage;
	}

	// Left-click the audio switcher: make the NEXT active output device the default (cycles), with a toast. Works for any
	// device type (wired/wireless headset, speakers) — it just cycles whatever endpoints Windows reports as active.
	private void CycleAudioOutput()
	{
		System.Threading.Tasks.Task.Run(delegate
		{
			try
			{
				System.Collections.Generic.List<AudioDevice> outs = AudioDevices.Enumerate(capture: false);
				if (outs.Count == 0)
				{
					return;
				}
				int cur = outs.FindIndex((AudioDevice d) => d.IsDefault);
				if (cur < 0)
				{
					cur = 0;
				}
				AudioDevice next = outs[(cur + 1) % outs.Count];
				bool switched = outs.Count > 1;
				if (switched)
				{
					AudioDevices.SetDefault(next.Id);
				}
				string name = next.Name;
				((System.Windows.Threading.DispatcherObject)this).Dispatcher.BeginInvoke((Delegate)(Action)delegate
				{
					ToastService.Show(AudioSwitchImage(), "Sound", switched ? "Output switched" : "Output device", name);
				}, Array.Empty<object>());
			}
			catch (Exception ex)
			{
				Logger.Log("CycleAudioOutput failed: " + ex.Message);
			}
		});
	}

	// Right-click the audio switcher: a Win8.1-styled output-device picker (checkmark = current default) + Sound settings.
	private void ShowAudioDeviceMenu(object sender)
	{
		try
		{
			System.Collections.Generic.List<AudioDevice> outs = AudioDevices.Enumerate(capture: false);
			ResourceDictionary res = System.Windows.Application.Current.Resources;
			System.Windows.Controls.ContextMenu ctx = new System.Windows.Controls.ContextMenu { Style = (Style)res["Win81ContextMenu"] };
			TaskbarContextMenu.ApplyTheme(ctx);
			foreach (AudioDevice d in outs)
			{
				string id = d.Id;
				ctx.Items.Add(TaskbarContextMenu.Choice(d.Name, d.IsDefault, delegate
				{
					System.Threading.Tasks.Task.Run(delegate { AudioDevices.SetDefault(id); });
				}));
			}
			if (outs.Count > 0)
			{
				ctx.Items.Add(TaskbarContextMenu.Sep());
			}
			ctx.Items.Add(TaskbarContextMenu.Leaf("Sound settings", delegate { OpenUri("ms-settings:sound"); }));
			if (sender is UIElement ui)
			{
				ctx.PlacementTarget = ui;
				ctx.Placement = System.Windows.Controls.Primitives.PlacementMode.Top;
			}
			ctx.IsOpen = true;
		}
		catch (Exception ex)
		{
			Logger.Log("ShowAudioDeviceMenu failed: " + ex.Message);
		}
	}

	private bool _sysTrayButtonsReady;

	// Move the fixed system flyout buttons (sound / network / clock / notifications) out of their XAML parents and into the
	// reorderable tray strip as HostElement entries, so any tray item can be dragged/intermixed (user request: fully
	// intermixed). One-time; reuses the existing _appIcons drag/order/persistence machinery.
	private void EnsureSystemTrayButtons()
	{
		// REVERTED 2026-09-04 (user request): do NOT intermix the system flyout buttons (Net/Volume/ActionCenter) into the
		// app-icon strip any more. Intermixing (a) mixed third-party app icons with the system icons instead of keeping the
		// app icons grouped on the LEFT, and (b) moved the Action Center to mid-tray on the PRIMARY monitor only (it stayed
		// far-right on secondary) — an inconsistency. Leaving them in their fixed XAML positions gives the same layout on
		// every monitor: [app icons] [network] [volume] [clock] [Action Center, far right, after the date].
		_sysTrayButtonsReady = true;
	}

	private void AddSystemTrayButton(string key, uint id, FrameworkElement el, object vm)
	{
		try
		{
			if (el == null)
			{
				return;
			}
			if (el.Parent is System.Windows.Controls.Panel parent)
			{
				parent.Children.Remove(el);
			}
			// Keep the control's data bindings (VolumeGlyph / NetImage / VolumeTip / ...) resolving against the taskbar VM,
			// not the TrayAppIcon that now hosts it inside the item container.
			el.DataContext = vm;
			_appIcons.Add(new TrayAppIcon { StableKey = key, Id = id, HostElement = el, Tooltip = "" });
		}
		catch (Exception ex)
		{
			Logger.Log("AddSystemTrayButton failed: " + ex.Message);
		}
	}

	private void ApplyTrayInfos(List<TrayIconInfo> live)
	{
		if (_trayDragging)
		{
			return;
		}
		EnsureSystemTrayButtons();
		if (_shellPid == 0)
		{
			_shellPid = TrayReader.ShellProcessId();
		}
		AppSettings st = SettingsStore.Current;
		HashSet<string> forceShown = new HashSet<string>(st.TrayForceShown ?? new List<string>());
		HashSet<string> forceHidden = new HashSet<string>(st.TrayForceHidden ?? new List<string>());
		HashSet<string> seen = new HashSet<string>();
		int index = 0;
		foreach (TrayIconInfo info in live)
		{
			uint ownerPid = TrayReader.ProcessIdOf(info.OwnerHwnd);
			bool audioSwitcher = false;
			if (ownerPid == _shellPid)
			{
				// Explorer-owned entries are system chrome the launcher already redraws. Repurpose ONLY the system VOLUME
				// indicator (no callback + a "%"-bearing tooltip like "Speakers: 60%") into a neutral one-click audio-device
				// switcher; remember its key so it stays the switcher even while muted (muted tooltip drops the "%"). Every
				// other explorer-owned icon (e.g. "Discord is using your microphone", camera/location indicators) is skipped.
				string skey = $"{((IntPtr)info.OwnerHwnd).ToInt64():X}:{info.Id}";
				if (info.CallbackMessage == 0 && !string.IsNullOrEmpty(info.Tooltip) && info.Tooltip.Contains('%'))
				{
					_audioSwitcherKeys.Add(skey);
				}
				if (info.CallbackMessage == 0 && _audioSwitcherKeys.Contains(skey))
				{
					audioSwitcher = true;
				}
				else
				{
					string sproc = TrayReader.ProcessNameOf(info.OwnerHwnd);
					if (_loggedTraySkips.Add(sproc + ":" + info.Id))
					{
						Logger.Log($"[traydiag] skipped shell-owned tray icon proc={sproc} pid={ownerPid} id={info.Id} tip='{info.Tooltip}'");
					}
					continue;
				}
			}
			string key = $"{((IntPtr)info.OwnerHwnd).ToInt64():X}:{info.Id}";
			seen.Add(key);
			if (!_stableByKey.TryGetValue(key, out string stable))
			{
				string pn = info.OwnerHwnd != IntPtr.Zero
					? TrayReader.ProcessNameOf(info.OwnerHwnd)
					: (string.IsNullOrEmpty(info.ExecutablePath) ? null : System.IO.Path.GetFileNameWithoutExtension(info.ExecutablePath));
				if (!string.IsNullOrEmpty(pn))
				{
					stable = pn;
					_stableByKey[key] = stable;
				}
				else
				{
					stable = key;
				}
			}
			bool over = info.FromOverflow;
			if (forceShown.Contains(stable))
			{
				over = false;
			}
			else if (forceHidden.Contains(stable))
			{
				over = true;
			}
			ObservableCollection<TrayAppIcon> coll = (over ? _overflowIcons : _appIcons);
			Dictionary<string, TrayAppIcon> dict = (over ? _overflowByKey : _appByKey);
			ObservableCollection<TrayAppIcon> otherColl = (over ? _appIcons : _overflowIcons);
			Dictionary<string, TrayAppIcon> otherDict = (over ? _appByKey : _overflowByKey);
			if (otherDict.TryGetValue(key, out var moved))
			{
				otherColl.Remove(moved);
				otherDict.Remove(key);
			}
			if (!dict.TryGetValue(key, out var vm))
			{
				vm = (dict[key] = new TrayAppIcon
				{
					OwnerHwnd = info.OwnerHwnd,
					Id = info.Id,
					StableKey = stable
				});
				if (over)
				{
					_overflowIcons.Add(vm);
				}
				else if (index <= _appIcons.Count)
				{
					_appIcons.Insert(Math.Min(index, _appIcons.Count), vm);
				}
				else
				{
					_appIcons.Add(vm);
				}
			}
			else if (stable != key || string.IsNullOrEmpty(vm.StableKey))
			{
				vm.StableKey = stable;
			}
			vm.CallbackMessage = info.CallbackMessage;
			vm.IsAudioSwitcher = audioSwitcher;
			vm.Version = info.Version;
			vm.Hidden = info.Hidden; vm.ExecutablePath = info.ExecutablePath;
			vm.Tooltip = (string.IsNullOrEmpty(info.Tooltip) ? "" : info.Tooltip);
			if (info.HIcon != vm.HIcon || vm.Image == null)
			{
				vm.HIcon = info.HIcon;
				string trayName = (!string.IsNullOrEmpty(info.Tooltip)) ? info.Tooltip : ((!string.IsNullOrEmpty(info.ExecutablePath)) ? System.IO.Path.GetFileNameWithoutExtension(info.ExecutablePath) : "App");
				vm.Image = IconResolver.EnsureNonBlank(info.Image ?? IconToSource(info.HIcon, info.OwnerHwnd), trayName);
				if (audioSwitcher) { System.Windows.Media.ImageSource sw = AudioSwitchImage(); if (sw != null) { vm.Image = sw; } }
			}
			if (!over)
			{
				index++;
			}
		}
		foreach (string key2 in _appByKey.Keys.Where((string k) => !seen.Contains(k)).ToList())
		{
			_appIcons.Remove(_appByKey[key2]);
			_appByKey.Remove(key2);
		}
		foreach (string key3 in _overflowByKey.Keys.Where((string k) => !seen.Contains(k)).ToList())
		{
			_overflowIcons.Remove(_overflowByKey[key3]);
			_overflowByKey.Remove(key3);
		}
		foreach (string key4 in _stableByKey.Keys.Where((string k) => !seen.Contains(k)).ToList())
		{
			_stableByKey.Remove(key4);
		}
		ApplySavedOrder(_appIcons, st.TrayOrder ?? new List<string>());
		int diagSig = (live.Count * 1000) + (_appIcons.Count * 10) + _overflowIcons.Count;
		if (diagSig != _lastTrayDiagSig)
		{
			_lastTrayDiagSig = diagSig;
			Logger.Log($"[traydiag] source={(TrayHostService.Active ? "host" : (Win11TrayReader.IsWin11 ? "registry" : "toolbar"))} live={live.Count} bar={_appIcons.Count} overflow={_overflowIcons.Count}");
		}
		OverflowChevron.Visibility = ((_overflowIcons.Count <= 0) ? Visibility.Collapsed : Visibility.Visible);
		if (_overflowIcons.Count == 0 && OverflowPopup.IsOpen)
		{
			OverflowPopup.IsOpen = false;
		}
	}

	private static void ApplySavedOrder(ObservableCollection<TrayAppIcon> coll, List<string> order)
	{
		if (coll.Count < 2 || order.Count == 0)
		{
			return;
		}
		Dictionary<string, int> rank = new Dictionary<string, int>();
		for (int i = 0; i < order.Count; i++)
		{
			if (!string.IsNullOrEmpty(order[i]))
			{
				rank.TryAdd(order[i], i);
			}
		}
		Dictionary<TrayAppIcon, int> pos = new Dictionary<TrayAppIcon, int>();
		for (int j = 0; j < coll.Count; j++)
		{
			pos[coll[j]] = j;
		}
		List<TrayAppIcon> desired = (from a in coll
			orderby rank.TryGetValue(a.StableKey, out var value) ? value : int.MaxValue, pos[a]
			select a).ToList();
		for (int i2 = 0; i2 < desired.Count; i2++)
		{
			int at = coll.IndexOf(desired[i2]);
			if (at != i2)
			{
				coll.Move(at, i2);
			}
		}
	}

	private void OnTrayIconPreviewMouseDown(object sender, MouseButtonEventArgs e)
	{
		//IL_0004: Unknown result type (might be due to invalid IL or missing references)
		//IL_0009: Unknown result type (might be due to invalid IL or missing references)
		if (e.ClickCount == 2 && (sender as FrameworkElement)?.DataContext is TrayAppIcon dcIcon)
		{
			// Double-click a tray icon → forward WM_LBUTTONDBLCLK so legacy apps restore/open their main window.
			if (dcIcon.ExecutablePath != null) { TrayActivate(dcIcon); } else { TrayReader.ForwardClick(dcIcon.OwnerHwnd, dcIcon.CallbackMessage, dcIcon.Id, right: false, version: dcIcon.Version, dbl: true); }
			return;
		}
		_trayDragStart = e.GetPosition(null);
		_trayDragItem = (sender as FrameworkElement)?.DataContext as TrayAppIcon;
	}

	private void OnTrayIconPreviewMouseMove(object sender, System.Windows.Input.MouseEventArgs e)
	{
		//IL_0021: Unknown result type (might be due to invalid IL or missing references)
		//IL_0026: Unknown result type (might be due to invalid IL or missing references)
		//IL_00d3: Unknown result type (might be due to invalid IL or missing references)
		//IL_00e9: Expected O, but got Unknown
		if (e.LeftButton != MouseButtonState.Pressed || _trayDragItem == null)
		{
			return;
		}
		System.Windows.Point p = e.GetPosition(null);
		if (Math.Abs(p.X - _trayDragStart.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(p.Y - _trayDragStart.Y) < SystemParameters.MinimumVerticalDragDistance)
		{
			return;
		}
		if (SettingsStore.Load().TaskbarLocked)
		{
			_trayDragItem = null;
			return;
		}
		TrayAppIcon item = _trayDragItem;
		_trayDragItem = null;
		_trayDragging = true;
		_trayGhost = new TrayDragGhost(item.Image);
		UIElement src = (UIElement)sender;
		src.GiveFeedback += OnFeedback;
		try
		{
			DragDrop.DoDragDrop((DependencyObject)sender, new System.Windows.DataObject("Win81TrayIcon", item), System.Windows.DragDropEffects.Move);
		}
		catch (Exception ex)
		{
			Logger.Log("tray drag: " + ex.Message);
		}
		finally
		{
			src.GiveFeedback -= OnFeedback;
			_trayGhost?.Dispose();
			_trayGhost = null;
			ClearTrayDropHints();
			_trayDragging = false;
		}
		void OnFeedback(object? s, System.Windows.GiveFeedbackEventArgs fe)
		{
			fe.UseDefaultCursors = false;
			Mouse.SetCursor(System.Windows.Input.Cursors.Arrow);
			fe.Handled = true;
			_trayGhost?.MoveToCursor();
		}
	}

	private void ClearTrayDropHints()
	{
		foreach (TrayAppIcon a in _appIcons)
		{
			a.DropHint = 0;
		}
		foreach (TrayAppIcon a2 in _overflowIcons)
		{
			a2.DropHint = 0;
		}
	}

	private void OnTrayDragOver(object sender, System.Windows.DragEventArgs e)
	{
		//IL_008a: Unknown result type (might be due to invalid IL or missing references)
		//IL_008f: Unknown result type (might be due to invalid IL or missing references)
		bool ok = e.Data.GetDataPresent("Win81TrayIcon");
		if (!ok)
		{
			// Not a tray-icon reorder. Leave the event unhandled so an app being dropped over the tray region
			// bubbles up to the pin drop target (RootLayout.OnPinDragOver) instead of being refused here — this is
			// what lets the WHOLE taskbar, tray included, accept a dragged app.
			return;
		}
		e.Effects = System.Windows.DragDropEffects.Move;
		e.Handled = true;
		if (sender is FrameworkElement { DataContext: TrayAppIcon target, ActualWidth: >0.0 } fe)
		{
			TrayAppIcon dragged = e.Data.GetData("Win81TrayIcon") as TrayAppIcon;
			int num;
			if (dragged != target)
			{
				System.Windows.Point position = e.GetPosition(fe);
				num = ((!(position.X > fe.ActualWidth / 2.0)) ? 1 : 2);
			}
			else
			{
				num = 0;
			}
			int hint = num;
			foreach (TrayAppIcon a in _appIcons)
			{
				if (a != target)
				{
					a.DropHint = 0;
				}
			}
			foreach (TrayAppIcon a2 in _overflowIcons)
			{
				if (a2 != target)
				{
					a2.DropHint = 0;
				}
			}
			target.DropHint = hint;
		}
		else
		{
			ClearTrayDropHints();
		}
	}

	private void OnTrayIconDrop(object sender, System.Windows.DragEventArgs e)
	{
		//IL_0111: Unknown result type (might be due to invalid IL or missing references)
		//IL_0116: Unknown result type (might be due to invalid IL or missing references)
		if (!e.Data.GetDataPresent("Win81TrayIcon"))
		{
			return;
		}
		TrayAppIcon dragged = e.Data.GetData("Win81TrayIcon") as TrayAppIcon;
		TrayAppIcon target = (sender as FrameworkElement)?.DataContext as TrayAppIcon;
		e.Handled = true;
		if (dragged == null || target == null || dragged == target)
		{
			return;
		}
		bool targetOverflow = _overflowIcons.Contains(target);
		bool draggedOverflow = _overflowIcons.Contains(dragged);
		if (draggedOverflow != targetOverflow)
		{
			MoveTrayZone(dragged, targetOverflow);
		}
		ObservableCollection<TrayAppIcon> coll = (targetOverflow ? _overflowIcons : _appIcons);
		if (coll.Contains(dragged) && coll.Contains(target))
		{
			int from = coll.IndexOf(dragged);
			int to = coll.IndexOf(target);
			bool after = false;
			if (sender is FrameworkElement { ActualWidth: >0.0 } fe)
			{
				System.Windows.Point position = e.GetPosition(fe);
				after = position.X > fe.ActualWidth / 2.0;
			}
			int insert = to + (after ? 1 : 0);
			if (from < insert)
			{
				insert--;
			}
			insert = Math.Max(0, Math.Min(insert, coll.Count - 1));
			if (insert != from)
			{
				coll.Move(from, insert);
			}
		}
		if (!targetOverflow)
		{
			SaveTrayOrder();
		}
	}

	private void OnTrayStripDrop(object sender, System.Windows.DragEventArgs e)
	{
		if (!e.Data.GetDataPresent("Win81TrayIcon"))
		{
			return;
		}
		e.Handled = true;
		if (!(e.Data.GetData("Win81TrayIcon") is TrayAppIcon dragged))
		{
			return;
		}
		if (_overflowIcons.Contains(dragged))
		{
			MoveTrayZone(dragged, toOverflow: false);
		}
		if (_appIcons.Contains(dragged))
		{
			int f = _appIcons.IndexOf(dragged);
			if (f != _appIcons.Count - 1)
			{
				_appIcons.Move(f, _appIcons.Count - 1);
			}
		}
		SaveTrayOrder();
	}

	private void OnTrayHideDrop(object sender, System.Windows.DragEventArgs e)
	{
		if (e.Data.GetDataPresent("Win81TrayIcon"))
		{
			e.Handled = true;
			if (e.Data.GetData("Win81TrayIcon") is TrayAppIcon dragged && dragged.HostElement == null && !_overflowIcons.Contains(dragged))
			{
				MoveTrayZone(dragged, toOverflow: true);
			}
		}
	}

	private void MoveTrayZone(TrayAppIcon a, bool toOverflow)
	{
		ObservableCollection<TrayAppIcon> fromColl = (toOverflow ? _appIcons : _overflowIcons);
		ObservableCollection<TrayAppIcon> toColl = (toOverflow ? _overflowIcons : _appIcons);
		Dictionary<string, TrayAppIcon> fromDict = (toOverflow ? _appByKey : _overflowByKey);
		Dictionary<string, TrayAppIcon> toDict = (toOverflow ? _overflowByKey : _appByKey);
		string key = a.Key;
		fromColl.Remove(a);
		fromDict.Remove(key);
		if (!toColl.Contains(a))
		{
			toColl.Add(a);
		}
		toDict[key] = a;
		if (!string.IsNullOrEmpty(a.StableKey))
		{
			SettingsStore.Update(delegate(AppSettings s)
			{
				s.TrayForceShown.Remove(a.StableKey);
				s.TrayForceHidden.Remove(a.StableKey);
				(toOverflow ? s.TrayForceHidden : s.TrayForceShown).Add(a.StableKey);
			});
		}
		OverflowChevron.Visibility = ((_overflowIcons.Count <= 0) ? Visibility.Collapsed : Visibility.Visible);
		if (_overflowIcons.Count == 0 && OverflowPopup.IsOpen)
		{
			OverflowPopup.IsOpen = false;
		}
	}

	private void SaveTrayOrder()
	{
		List<string> keys = new List<string>();
		foreach (TrayAppIcon a in _appIcons)
		{
			if (!string.IsNullOrEmpty(a.StableKey) && !keys.Contains(a.StableKey))
			{
				keys.Add(a.StableKey);
			}
		}
		SettingsStore.Update(delegate(AppSettings s)
		{
			s.TrayOrder = keys;
		});
	}

	private void OnOverflowChevron(object sender, RoutedEventArgs e)
	{
		OverflowPopup.IsOpen = !OverflowPopup.IsOpen;
	}

	private void OnTaskOverflow(object sender, RoutedEventArgs e)
	{
		TaskOverflowPopup.IsOpen = !TaskOverflowPopup.IsOpen;
	}

	private static ImageSource? IconToSource(nint hIcon, nint ownerHwnd)
	{
		//IL_0017: Unknown result type (might be due to invalid IL or missing references)
		//IL_00a6: Unknown result type (might be due to invalid IL or missing references)
		// Primary: convert the tray HICON. CopyIcon promotes explorer's/foreign handle to a locally-owned copy and
		// defends against the owner (e.g. Electron/Chromium apps) having destroyed its Shell_NotifyIcon icon after
		// NIM_ADD — the common reason the stored handle won't render and we used to draw a black square.
		if (hIcon != IntPtr.Zero)
		{
			nint dup = CopyIcon(hIcon);
			nint use = ((dup != IntPtr.Zero) ? dup : hIcon);
			try
			{
				BitmapSource src = Imaging.CreateBitmapSourceFromHIcon(use, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
				((Freezable)src).Freeze();
				if (!IconResolver.IsBlank(src))
				{
					return AppInventory.EnsureVisibleOnDark(src);
				}
			}
			catch
			{
			}
			finally
			{
				if (dup != IntPtr.Zero)
				{
					DestroyIcon(dup);
				}
			}
		}
		// Fallback 1: the owner window's own icon (WM_GETICON / class icon) — the same reliable, process-agnostic
		// source the taskbar task buttons already render successfully, so it works where the stored tray hIcon won't.
		try
		{
			ImageSource? viaWnd = WindowList.GetIcon(ownerHwnd, preferLarge: false);
			if (viaWnd != null && !IconResolver.IsBlank(viaWnd))
			{
				return AppInventory.EnsureVisibleOnDark(viaWnd);
			}
		}
		catch
		{
		}
		// Fallback 2: extract the icon from the owner's executable.
		try
		{
			string? exe = WindowList.GetExePath(ownerHwnd);
			if (!string.IsNullOrEmpty(exe))
			{
				nint[] h = new nint[1];
				if (PrivateExtractIcons(exe, 0, 32, 32, h, IntPtr.Zero, 1u, 0u) > 0 && h[0] != IntPtr.Zero)
				{
					try
					{
						BitmapSource s = Imaging.CreateBitmapSourceFromHIcon(h[0], Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
						((Freezable)s).Freeze();
						return AppInventory.EnsureVisibleOnDark(s);
					}
					finally
					{
						DestroyIcon(h[0]);
					}
				}
			}
		}
		catch
		{
		}
		return null;
	}

	[DllImport("user32.dll")]
	private static extern bool DrawIconEx(nint hdc, int x, int y, nint hIcon, int w, int h, int step, nint brush, int flags);

	[DllImport("gdi32.dll", EntryPoint = "DeleteObject")]
	private static extern bool DeleteGdiObject(nint o);

	[DllImport("user32.dll")]
	private static extern nint CopyIcon(nint hIcon);

	[DllImport("user32.dll")]
	private static extern bool DestroyIcon(nint hIcon);

	[DllImport("user32.dll", CharSet = CharSet.Unicode)]
	private static extern uint PrivateExtractIcons(string szFile, int nIconIndex, int cxIcon, int cyIcon, nint[] phicon, nint piconid, uint nIcons, uint flags);

	private void OnAppIconClick(object sender, RoutedEventArgs e)
	{
		if ((sender as FrameworkElement)?.DataContext is TrayAppIcon a)
		{
			if (a.HostElement != null) { return; }   // hosted system button handles its own click
			if (a.IsAudioSwitcher) { CycleAudioOutput(); }
			else if (a.ExecutablePath != null) { TrayActivate(a); } else { TrayReader.ForwardClick(a.OwnerHwnd, a.CallbackMessage, a.Id, right: false, version: a.Version); }
		}
	}

	private void OnAppIconRightClick(object sender, MouseButtonEventArgs e)
	{
		if ((sender as FrameworkElement)?.DataContext is TrayAppIcon a)
		{
			if (a.HostElement != null) { return; }   // hosted system button handles its own right-click
			if (a.IsAudioSwitcher) { ShowAudioDeviceMenu(sender); }
			else if (a.ExecutablePath != null) { ShowTrayIconMenu(a, sender); } else { TrayReader.ForwardClick(a.OwnerHwnd, a.CallbackMessage, a.Id, right: true, version: a.Version); }
			e.Handled = true;
		}
	}

	// Win11 registry-sourced tray icons carry no owner HWND / callback message, so a click can't be forwarded to the app
	// the way legacy Shell_NotifyIcon icons are. Instead we activate the app's existing main window (bring it to the
	// foreground / restore it), or launch the executable when it has no visible window. Best-effort by design — a
	// tray-only app with no top-level window simply gets re-launched, which for most apps re-shows the running instance.
	private void TrayActivate(TrayAppIcon a)
	{
		string exe = a?.ExecutablePath;
		if (string.IsNullOrEmpty(exe))
		{
			return;
		}
		try
		{
			nint hwnd = FindMainWindowForExe(exe);
			if (hwnd != IntPtr.Zero)
			{
				WindowList.Activate(hwnd);
				return;
			}
			if (File.Exists(exe))
			{
				Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true });
			}
		}
		catch (Exception ex)
		{
			Logger.Log("TrayActivate failed: " + ex.Message);
		}
	}

	// Find a top-level main window owned by the process behind 'exe' (matched by base name — a registry ExecutablePath
	// can differ slightly from the live module path). Returns Zero when the app has no visible main window, which is the
	// normal case for a tray-only app.
	private static nint FindMainWindowForExe(string exe)
	{
		string want = "";
		try { want = System.IO.Path.GetFileNameWithoutExtension(exe); } catch { }
		if (string.IsNullOrEmpty(want))
		{
			return IntPtr.Zero;
		}
		nint found = IntPtr.Zero;
		try
		{
			foreach (Process p in Process.GetProcessesByName(want))
			{
				try
				{
					if (found == IntPtr.Zero && p.MainWindowHandle != IntPtr.Zero)
					{
						found = p.MainWindowHandle;
					}
				}
				catch { }
				finally { p.Dispose(); }
			}
		}
		catch { }
		return found;
	}

	// Right-click menu for a Win11 registry tray icon. The app's own context menu is unreachable (no callback), so we
	// offer the useful launcher-side actions: activate/open the app, and reveal/copy its executable.
	private void ShowTrayIconMenu(TrayAppIcon a, object sender)
	{
		string exe = a.ExecutablePath;
		System.Windows.Controls.ContextMenu menu = new System.Windows.Controls.ContextMenu
		{
			Style = (Style)System.Windows.Application.Current.Resources["Win81ContextMenu"]
		};
		TaskbarContextMenu.ApplyTheme(menu);
		menu.Items.Add(TaskbarContextMenu.Leaf("Open", 59559, delegate
		{
			TrayActivate(a);
		}));
		if (!string.IsNullOrEmpty(exe))
		{
			menu.Items.Add(TaskbarContextMenu.Sep());
			menu.Items.Add(TaskbarContextMenu.Leaf("Open file location", 57736, delegate
			{
				OpenFileLocation(exe);
			}));
			menu.Items.Add(TaskbarContextMenu.Leaf("Copy path", 59592, delegate
			{
				CopyToClipboard(exe);
			}));
		}
		menu.PlacementTarget = sender as UIElement;
		menu.Placement = FlyoutMode();
		OpenTrackedMenu(menu);
	}

	private void OnVolumeWheel(object sender, MouseWheelEventArgs e)
	{
		int nv = Math.Clamp(_tray.VolumePct + ((e.Delta > 0) ? 2 : (-2)), 0, 100);
		_audio.SetVolume((float)nv / 100f);
		if (nv > 0 && _tray.Muted)
		{
			_audio.SetMute(mute: false);
			_tray.Muted = false;
		}
		_tray.VolumePct = nv;
		e.Handled = true;
	}

	private void OnVolumeClick(object sender, RoutedEventArgs e)
	{
		CloseTrayCommandPopups();
		if (VolumePopup.IsOpen)
		{
			if (_volumeClosing)
			{
				_volumeClosing = false;
				_volumeAnimationToken++;
				AnimateVolumeFlyoutIn();
				StartVolumeTick();
			}
			else
			{
				CloseVolumeFlyout(animate: true);
			}
			return;
		}

		TaskOverflowPopup.IsOpen = false;
		OverflowPopup.IsOpen = false;
		CloseNetworkFlyout(animate: false);
		CloseClockFlyout(animate: false);
		ApplySoundFlyoutTheme();
		SyncMasterVolume();
		ConfigureVolumeFlyoutSize();
		PrepareVolumeFlyoutEnter();
		VolumePopup.IsOpen = true;
	}

	private void OnVolumeBodyPreviewMouseWheel(object sender, MouseWheelEventArgs e)
	{
		if (RouteVolumeWheelDelta(e.Delta))
		{
			e.Handled = true;
		}
	}

	private bool RouteVolumeWheelDelta(int delta)
	{
		if (delta == 0 || _stopped)
		{
			return false;
		}
		_volumeWheelRouteCount++;
		VolumeBodyScroll.UpdateLayout();
		if (VolumeBodyScroll.ScrollableHeight <= 0.5)
		{
			_pendingVolumeWheelDelta = Math.Clamp(_pendingVolumeWheelDelta + delta, -960, 960);
			QueuePendingVolumeWheelFlush();
			return true;
		}
		ApplyVolumeWheelDelta(delta);
		return true;
	}

	private void ApplyVolumeWheelDelta(int delta)
	{
		double notches = (double)delta / 120.0;
		double step = Math.Clamp(VolumeBodyScroll.ViewportHeight * 0.2, 48.0, 72.0);
		SmoothScroll.ByVertical(VolumeBodyScroll, -notches * step);
	}

	private void QueuePendingVolumeWheelFlush()
	{
		if (_pendingVolumeWheelFlushQueued || _pendingVolumeWheelDelta == 0)
		{
			return;
		}
		_pendingVolumeWheelFlushQueued = true;
		Dispatcher.BeginInvoke((Action)delegate
		{
			_pendingVolumeWheelFlushQueued = false;
			VolumeBodyScroll.UpdateLayout();
			if (VolumeBodyScroll.ScrollableHeight <= 0.5 || _pendingVolumeWheelDelta == 0)
			{
				return;
			}
			int pending = _pendingVolumeWheelDelta;
			_pendingVolumeWheelDelta = 0;
			ApplyVolumeWheelDelta(pending);
		}, DispatcherPriority.Render);
	}

	public bool TryRouteGlobalFlyoutMouseWheel(int screenX, int screenY, int delta)
	{
		if (delta == 0 || Volatile.Read(ref _volumeWheelRoutingActive) == 0)
		{
			return false;
		}
		int left = Volatile.Read(ref _volumeWheelLeft);
		int top = Volatile.Read(ref _volumeWheelTop);
		int right = Volatile.Read(ref _volumeWheelRight);
		int bottom = Volatile.Read(ref _volumeWheelBottom);
		if (screenX < left || screenX >= right || screenY < top || screenY >= bottom)
		{
			return false;
		}

		Interlocked.Add(ref _queuedGlobalVolumeWheelDelta, delta);
		QueueGlobalVolumeWheelDrain();
		return true;
	}

	private void QueueGlobalVolumeWheelDrain()
	{
		if (Interlocked.CompareExchange(ref _globalVolumeWheelDispatchQueued, 1, 0) != 0)
		{
			return;
		}
		try
		{
			Dispatcher.BeginInvoke((Action)DrainGlobalVolumeWheel, DispatcherPriority.Input);
		}
		catch
		{
			Interlocked.Exchange(ref _globalVolumeWheelDispatchQueued, 0);
			Interlocked.Exchange(ref _queuedGlobalVolumeWheelDelta, 0);
		}
	}

	private void DrainGlobalVolumeWheel()
	{
		int delta = Interlocked.Exchange(ref _queuedGlobalVolumeWheelDelta, 0);
		if (delta != 0 && VolumePopup.IsOpen && !_stopped)
		{
			RouteVolumeWheelDelta(delta);
		}
		Interlocked.Exchange(ref _globalVolumeWheelDispatchQueued, 0);
		if (Volatile.Read(ref _queuedGlobalVolumeWheelDelta) != 0)
		{
			QueueGlobalVolumeWheelDrain();
		}
	}

	private void ActivateVolumeWheelRouting()
	{
		UpdateVolumeWheelScreenBounds();
		Dispatcher.BeginInvoke((Action)UpdateVolumeWheelScreenBounds, DispatcherPriority.Render);
	}

	private void UpdateVolumeWheelScreenBounds()
	{
		FrameworkElement? child = VolumePopup.Child as FrameworkElement;
		if (!VolumePopup.IsOpen || child == null || child.ActualWidth <= 0.0 || child.ActualHeight <= 0.0)
		{
			return;
		}
		try
		{
			System.Windows.Point tl = child.PointToScreen(new System.Windows.Point(0.0, 0.0));
			System.Windows.Point br = child.PointToScreen(new System.Windows.Point(child.ActualWidth, child.ActualHeight));
			Volatile.Write(ref _volumeWheelLeft, (int)Math.Floor(Math.Min(tl.X, br.X)));
			Volatile.Write(ref _volumeWheelTop, (int)Math.Floor(Math.Min(tl.Y, br.Y)));
			Volatile.Write(ref _volumeWheelRight, (int)Math.Ceiling(Math.Max(tl.X, br.X)));
			Volatile.Write(ref _volumeWheelBottom, (int)Math.Ceiling(Math.Max(tl.Y, br.Y)));
			Volatile.Write(ref _volumeWheelRoutingActive, 1);
		}
		catch
		{
			Volatile.Write(ref _volumeWheelRoutingActive, 0);
		}
	}

	private void DeactivateVolumeWheelRouting()
	{
		Volatile.Write(ref _volumeWheelRoutingActive, 0);
		Interlocked.Exchange(ref _queuedGlobalVolumeWheelDelta, 0);
		Interlocked.Exchange(ref _globalVolumeWheelDispatchQueued, 0);
		_pendingVolumeWheelDelta = 0;
		_pendingVolumeWheelFlushQueued = false;
	}

	private void OnVolumeBodyPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
	{
		if (e.ChangedButton != MouseButton.Left)
		{
			return;
		}
		// A direct click or thumb drag must take ownership immediately instead of competing with wheel inertia.
		SmoothScroll.StopVertical(VolumeBodyScroll);
	}

	public void CloseFlyoutsOutside(int sx, int sy)
	{
		FlyoutOutside(SoundCommandPopup, VolumeButton, sx, sy);
		FlyoutOutside(NetworkCommandPopup, NetButton, sx, sy);
		if (VolumePopup.IsOpen && !InScreenRect(VolumePopup.Child as FrameworkElement, sx, sy) && !InScreenRect(VolumeButton, sx, sy))
		{
			CloseVolumeFlyout(animate: true);
		}
		if (NetPopup.IsOpen && !InScreenRect(NetPopup.Child as FrameworkElement, sx, sy) && !InScreenRect(NetButton, sx, sy))
		{
			CloseNetworkFlyout(animate: true);
		}
		if (ClockPopup.IsOpen && !InScreenRect(ClockPopup.Child as FrameworkElement, sx, sy) && !InScreenRect(ClockPanel, sx, sy))
		{
			CloseClockFlyout(animate: true);
		}
		FlyoutOutside(OverflowPopup, OverflowChevron, sx, sy);
		FlyoutOutside(TaskOverflowPopup, TaskOverflowButton, sx, sy);
	}

	private static void FlyoutOutside(Popup popup, FrameworkElement icon, int sx, int sy)
	{
		if (popup.IsOpen && !InScreenRect(popup.Child as FrameworkElement, sx, sy) && !InScreenRect(icon, sx, sy))
		{
			popup.IsOpen = false;
		}
	}

	private static bool InScreenRect(FrameworkElement? fe, int sx, int sy)
	{
		//IL_0043: Unknown result type (might be due to invalid IL or missing references)
		//IL_0048: Unknown result type (might be due to invalid IL or missing references)
		//IL_004d: Unknown result type (might be due to invalid IL or missing references)
		//IL_005b: Unknown result type (might be due to invalid IL or missing references)
		//IL_0060: Unknown result type (might be due to invalid IL or missing references)
		//IL_0065: Unknown result type (might be due to invalid IL or missing references)
		if (fe == null || !fe.IsVisible || fe.ActualWidth <= 0.0)
		{
			return false;
		}
		try
		{
			System.Windows.Point tl = fe.PointToScreen(new System.Windows.Point(0.0, 0.0));
			System.Windows.Point br = fe.PointToScreen(new System.Windows.Point(fe.ActualWidth, fe.ActualHeight));
			return (double)sx >= Math.Min(tl.X, br.X) && (double)sx < Math.Max(tl.X, br.X) && (double)sy >= Math.Min(tl.Y, br.Y) && (double)sy < Math.Max(tl.Y, br.Y);
		}
		catch
		{
			return false;
		}
	}

	private async Task RefreshVolumeInventoryAsync(bool force)
	{
		if (_volumeInventoryBusy)
		{
			_volumeRefreshPending |= force;
			return;
		}
		if (!force && Environment.TickCount64 - _volumeLastInventoryMs < 4500L)
		{
			return;
		}

		_volumeInventoryBusy = true;
		int token = _volumeRefreshToken;
		VolumeRefreshStatus.Text = "Updating...";
		VolumeRefreshStatus.Visibility = Visibility.Visible;
		try
		{
			AudioFlyoutSnapshot snapshot = await Task.Run(delegate
			{
				Stopwatch sw = Stopwatch.StartNew();
				List<AudioDevice> outputs = AudioDevices.Enumerate(capture: false);
				List<AudioDevice> inputs = AudioDevices.Enumerate(capture: true);
				List<AudioSessionVm> sessions = AudioSessions.Enumerate();
				sw.Stop();
				return new AudioFlyoutSnapshot(outputs, inputs, sessions, sw.ElapsedMilliseconds);
			});
			if (token != _volumeRefreshToken || !VolumePopup.IsOpen || _stopped)
			{
				return;
			}
			ApplyAudioSnapshot(snapshot);
			_volumeLastInventoryMs = Environment.TickCount64;
		}
		catch (Exception ex)
		{
			Logger.Log("Sound flyout refresh failed: " + ex.Message);
			if (token == _volumeRefreshToken && VolumePopup.IsOpen)
			{
				VolumeRefreshStatus.Text = "Audio service unavailable";
				VolumeRefreshStatus.Visibility = Visibility.Visible;
			}
		}
		finally
		{
			_volumeInventoryBusy = false;
			if (token == _volumeRefreshToken && VolumePopup.IsOpen && VolumeRefreshStatus.Text == "Updating...")
			{
				VolumeRefreshStatus.Visibility = Visibility.Collapsed;
			}
			if (_volumeRefreshPending && VolumePopup.IsOpen && !_stopped)
			{
				_volumeRefreshPending = false;
				Dispatcher.BeginInvoke((Action)(async () => await RefreshVolumeInventoryAsync(force: true)), DispatcherPriority.Background);
			}
		}
	}

	private void ApplyAudioSnapshot(AudioFlyoutSnapshot snapshot)
	{
		string nextDefaultOutputId = snapshot.Outputs.FirstOrDefault(device => device.IsDefault)?.Id ?? string.Empty;
		bool defaultOutputChanged = !string.IsNullOrEmpty(_volumeDefaultOutputId)
			&& !string.Equals(_volumeDefaultOutputId, nextDefaultOutputId, StringComparison.Ordinal);
		_volumeDefaultOutputId = nextDefaultOutputId;
		if (defaultOutputChanged)
		{
			_audio.Reset();
			SyncMasterVolume();
		}
		BuildDeviceRows(OutputList, snapshot.Outputs, capture: false);
		BuildDeviceRows(InputList, snapshot.Inputs, capture: true);
		_appSessions.Clear();
		foreach (AudioSessionVm session in snapshot.Sessions)
		{
			_appSessions.Add(session);
		}
		_volumeOutputCount = snapshot.Outputs.Count;
		_volumeInputCount = snapshot.Inputs.Count;
		_volumeInventoryMs = snapshot.EnumerateMs;
		VolumeEmptyApps.Visibility = _appSessions.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
		VolDeviceName.Text = DefaultDeviceName(snapshot.Outputs, "No output device");
		QueuePendingVolumeWheelFlush();
	}

	private static string DefaultDeviceName(List<AudioDevice> devices, string fallback)
	{
		AudioDevice? selected = devices.FirstOrDefault(device => device.IsDefault) ?? devices.FirstOrDefault();
		return selected?.Name ?? fallback;
	}

	private void OnAppMuteClick(object sender, RoutedEventArgs e)
	{
		if ((sender as FrameworkElement)?.DataContext is AudioSessionVm s)
		{
			s.Toggle();
		}
	}

	private void PopulateDevices()
	{
		List<AudioDevice> outputs = AudioDevices.Enumerate(capture: false);
		List<AudioDevice> inputs = AudioDevices.Enumerate(capture: true);
		ApplyAudioSnapshot(new AudioFlyoutSnapshot(outputs, inputs, new List<AudioSessionVm>(), 0L));
	}

	// Map an output device name to the best authentic mmres endpoint semantic.
	private static string OutputAudioSemantic(string name)
	{
		string n = (name ?? "").ToLowerInvariant();
		if (n.Contains("headphone")) return "AudioDevice.Headphones";
		if (n.Contains("headset")) return "AudioDevice.Headset";
		if (n.Contains("hdmi") || n.Contains("display")) return "AudioDevice.Hdmi";
		if (n.Contains("digital") || n.Contains("spdif") || n.Contains("optical")) return "AudioDevice.Digital";
		return "AudioDevice.Speakers";
	}

	private void BuildDeviceRows(StackPanel panel, List<AudioDevice> devices, bool capture)
	{
		panel.Children.Clear();
		if (devices.Count == 0)
		{
			panel.Children.Add(new TextBlock
			{
				Text = capture ? "No input device available" : "No output device available",
				Foreground = new SolidColorBrush(System.Windows.Media.Color.FromArgb(180, 255, 255, 255)),
				FontFamily = new System.Windows.Media.FontFamily("Segoe UI"),
				FontSize = 12.0,
				Margin = new Thickness(2.0, 4.0, 0.0, 7.0)
			});
			return;
		}

		foreach (AudioDevice d in devices)
		{
			Grid content = new Grid();
			content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(50.0) });
			content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.0) });
			content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.0, GridUnitType.Star) });
			content.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
			// Authentic Win8.1 mmres device icon (speakers/headphones/headset/hdmi/digital, or microphone for inputs);
			// falls back to the MDL2 glyph when the asset library is absent.
			string audioSem = capture ? "AudioDevice.Microphone" : OutputAudioSemantic(d.Name);
			System.Windows.Media.ImageSource audioImg = Win81AssetResolver.GetAsset(audioSem, 32);
			FrameworkElement iconEl;
			if (audioImg != null)
			{
				System.Windows.Controls.Image img = new System.Windows.Controls.Image
				{
					Width = 24.0,
					Height = 24.0,
					Stretch = System.Windows.Media.Stretch.Uniform,
					Source = audioImg,
					SnapsToDevicePixels = true,
					HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
					VerticalAlignment = VerticalAlignment.Center
				};
				RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);
				iconEl = img;
			}
			else
			{
				iconEl = new TextBlock
				{
					Text = char.ConvertFromUtf32(capture ? 59168 : 59239),
					FontFamily = new System.Windows.Media.FontFamily("Segoe MDL2 Assets"),
					FontSize = 24.0,
					Foreground = System.Windows.Media.Brushes.White,
					HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
					VerticalAlignment = VerticalAlignment.Center
				};
			}
			Grid.SetColumn(iconEl, 0);
			content.Children.Add(iconEl);
			Border separator = new Border
			{
				Background = new SolidColorBrush(System.Windows.Media.Color.FromArgb(110, 255, 255, 255)),
				Margin = new Thickness(0.0, 8.0, 0.0, 8.0)
			};
			Grid.SetColumn(separator, 1);
			content.Children.Add(separator);
			StackPanel labels = new StackPanel
			{
				Margin = new Thickness(10.0, 6.0, 7.0, 5.0),
				VerticalAlignment = VerticalAlignment.Center
			};
			labels.Children.Add(new TextBlock
			{
				Text = d.Name,
				Foreground = System.Windows.Media.Brushes.White,
				FontFamily = new System.Windows.Media.FontFamily("Segoe UI"),
				FontSize = 12.0,
				TextTrimming = TextTrimming.CharacterEllipsis
			});
			labels.Children.Add(new TextBlock
			{
				Text = d.IsDefault ? "Default device" : "Select device",
				Foreground = new SolidColorBrush(System.Windows.Media.Color.FromArgb(190, 255, 255, 255)),
				FontFamily = new System.Windows.Media.FontFamily("Segoe UI"),
				FontSize = 10.5,
				Margin = new Thickness(0.0, 1.0, 0.0, 0.0)
			});
			Grid.SetColumn(labels, 2);
			content.Children.Add(labels);
			TextBlock arrow = new TextBlock
			{
				Text = "›",
				Foreground = System.Windows.Media.Brushes.White,
				FontFamily = new System.Windows.Media.FontFamily("Segoe UI"),
				FontSize = 22.0,
				VerticalAlignment = VerticalAlignment.Center,
				Margin = new Thickness(4.0, 0.0, 9.0, 2.0),
				Visibility = d.IsDefault ? Visibility.Collapsed : Visibility.Visible
			};
			Grid.SetColumn(arrow, 3);
			content.Children.Add(arrow);
			System.Windows.Controls.Button btn = new System.Windows.Controls.Button
			{
				Content = content,
				Style = (Style)base.Resources["DeviceRow"],
				Margin = new Thickness(0.0, 0.0, 0.0, 4.0),
				ToolTip = d.IsDefault ? d.Name + " (default)" : "Use " + d.Name
			};
			string resourceKey = capture
				? (d.IsDefault ? "TB.SoundInputDefault" : "TB.SoundInputOther")
				: (d.IsDefault ? "TB.SoundOutputDefault" : "TB.SoundOutputOther");
			btn.SetResourceReference(System.Windows.Controls.Control.BackgroundProperty, resourceKey);
			string id = d.Id;
			btn.Click += async delegate
			{
				if (!d.IsDefault)
				{
					await SwitchDefaultDeviceAsync(id);
				}
			};
			panel.Children.Add(btn);
		}
	}

	private async Task SwitchDefaultDeviceAsync(string id)
	{
		VolumeRefreshStatus.Text = "Switching device...";
		VolumeRefreshStatus.Visibility = Visibility.Visible;
		try
		{
			await Task.Run(delegate { AudioDevices.SetDefault(id); });
			await Task.Delay(120);
			_audio.Reset();
			SyncMasterVolume();
			_volumeLastInventoryMs = 0L;
			await RefreshVolumeInventoryAsync(force: true);
		}
		catch (Exception ex)
		{
			Logger.Log("Switch audio device failed: " + ex.Message);
			if (VolumePopup.IsOpen)
			{
				VolumeRefreshStatus.Text = "Could not switch device";
			}
		}
	}

	private void OnMuteToggle(object sender, RoutedEventArgs e)
	{
		_audio.ToggleMute();
		SyncMasterVolume();
	}

	private void OnVolumeSliderChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
	{
		if (VolumePopup.IsOpen && !_volumeSyncing)
		{
			int v = (int)Math.Round(e.NewValue);
			_audio.SetVolume((float)v / 100f);
			if (v > 0 && _tray.Muted)
			{
				_audio.SetMute(mute: false);
				_tray.Muted = false;
			}
		}
	}

	private void SyncMasterVolume()
	{
		_volumeSyncing = true;
		try
		{
			_tray.VolumePct = Math.Clamp((int)Math.Round(_audio.GetVolume() * 100f), 0, 100);
			_tray.Muted = _audio.GetMute();
		}
		finally
		{
			_volumeSyncing = false;
		}
	}

	private void OnVolumePopupOpened(object sender, EventArgs e)
	{
		_volumeClosing = false;
		_volumeRefreshToken++;
		ActivateVolumeWheelRouting();
		if (_volumeDiagnosticsMode)
		{
			return;
		}
		StartVolumeTick();
		Dispatcher.BeginInvoke((Action)AnimateVolumeFlyoutIn, DispatcherPriority.Render);
		_ = RefreshVolumeInventoryAsync(force: true);
	}

	private void OnVolumePopupClosed(object sender, EventArgs e)
	{
		DeactivateVolumeWheelRouting();
		StopVolumeTick();
		SmoothScroll.StopVertical(VolumeBodyScroll);
		_volumeRefreshToken++;
		_volumeClosing = false;
		_volumeRefreshPending = false;
		VolumeFlyoutRoot.BeginAnimation(UIElement.OpacityProperty, null);
		VolumeFlyoutSlide.BeginAnimation(TranslateTransform.XProperty, null);
		VolumeFlyoutSlide.BeginAnimation(TranslateTransform.YProperty, null);
		VolumeFlyoutRoot.Opacity = 1.0;
		VolumeFlyoutSlide.X = 0.0;
		VolumeFlyoutSlide.Y = 0.0;
		VolumeFlyoutRoot.CacheMode = null;
		if (VolumeRefreshStatus.Text == "Updating...")
		{
			VolumeRefreshStatus.Visibility = Visibility.Collapsed;
		}
	}

	private void StartVolumeTick()
	{
		if (_volumeTick == null)
		{
			_volumeTick = new DispatcherTimer(DispatcherPriority.Background)
			{
				Interval = TimeSpan.FromMilliseconds(750.0)
			};
			_volumeTick.Tick += delegate
			{
				if (!VolumePopup.IsOpen || _volumeClosing || _stopped)
				{
					StopVolumeTick();
					return;
				}
				SyncMasterVolume();
				_ = RefreshVolumeInventoryAsync(force: false);
			};
		}
		_volumeTick.Start();
	}

	private void StopVolumeTick()
	{
		_volumeTick?.Stop();
	}

	private void ConfigureVolumeFlyoutSize(double? forcedWidthDiu = null, double? forcedHeightDiu = null)
	{
		double dpiX = 1.0;
		double dpiY = 1.0;
		try
		{
			DpiScale dpi = VisualTreeHelper.GetDpi(this);
			dpiX = Math.Max(0.5, dpi.DpiScaleX);
			dpiY = Math.Max(0.5, dpi.DpiScaleY);
		}
		catch
		{
		}
		double availableWidth = _screen.WorkingArea.Width / dpiX;
		double availableHeight = _screen.WorkingArea.Height / dpiY;
		double width = forcedWidthDiu ?? Math.Max(340.0, Math.Min(420.0, availableWidth - 10.0));
		double height = forcedHeightDiu ?? Math.Max(450.0, Math.Min(540.0, availableHeight - 10.0));
		bool veryCompact = width < 380.0 || height < 490.0;
		bool compact = veryCompact || width < 420.0 || height < 540.0;

		VolumeFlyoutRoot.Width = width;
		VolumeFlyoutRoot.Height = height;
		// Taller header so the speaker+slider row (the '*' row after the title + device name) has real room — the tall "100%"
		// title was squeezing it and clipping the speaker. The speaker Viewbox scales to whatever this row gives it, so it is
		// always whole; this just makes that room generous. (+22px vs the old 102/110/118.)
		VolumeHeaderRow.Height = new GridLength(veryCompact ? 124.0 : (compact ? 132.0 : 140.0));
		VolumeFooterRow.Height = new GridLength(veryCompact ? 104.0 : (compact ? 110.0 : 116.0));
		VolumeBodyScroll.Padding = veryCompact ? new Thickness(10.0, 5.0, 7.0, 4.0) : (compact ? new Thickness(12.0, 6.0, 8.0, 5.0) : new Thickness(14.0, 7.0, 10.0, 6.0));
		VolumeTitle.FontSize = veryCompact ? 24.0 : (compact ? 27.0 : 29.0);
		VolumePercent.FontSize = veryCompact ? 35.0 : (compact ? 39.0 : 43.0);
		MuteToggle.FontSize = veryCompact ? 21.0 : (compact ? 23.0 : 25.0);
	}

	private CustomPopupPlacement[] PlaceVolumePopup(System.Windows.Size popupSize, System.Windows.Size targetSize, System.Windows.Point offset)
	{
		const double gap = 6.0;
		return _position switch
		{
			"Top" => new[] { new CustomPopupPlacement(new System.Windows.Point(targetSize.Width - popupSize.Width, targetSize.Height + gap), PopupPrimaryAxis.Horizontal) },
			"Left" => new[] { new CustomPopupPlacement(new System.Windows.Point(targetSize.Width + gap, targetSize.Height - popupSize.Height), PopupPrimaryAxis.Vertical) },
			"Right" => new[] { new CustomPopupPlacement(new System.Windows.Point(-popupSize.Width - gap, targetSize.Height - popupSize.Height), PopupPrimaryAxis.Vertical) },
			_ => new[] { new CustomPopupPlacement(new System.Windows.Point(targetSize.Width - popupSize.Width, -popupSize.Height - gap), PopupPrimaryAxis.Horizontal) }
		};
	}

	private System.Windows.Vector VolumeFlyoutOffset()
	{
		return _position switch
		{
			"Top" => new System.Windows.Vector(0.0, -12.0),
			"Left" => new System.Windows.Vector(-12.0, 0.0),
			"Right" => new System.Windows.Vector(12.0, 0.0),
			_ => new System.Windows.Vector(0.0, 12.0)
		};
	}

	private void PrepareVolumeFlyoutEnter()
	{
		VolumeFlyoutRoot.BeginAnimation(UIElement.OpacityProperty, null);
		VolumeFlyoutSlide.BeginAnimation(TranslateTransform.XProperty, null);
		VolumeFlyoutSlide.BeginAnimation(TranslateTransform.YProperty, null);
		System.Windows.Vector offset = VolumeFlyoutOffset();
		VolumeFlyoutRoot.Opacity = Motion.Mode == MotionMode.Off ? 1.0 : 0.0;
		VolumeFlyoutSlide.X = Motion.Mode == MotionMode.Off ? 0.0 : offset.X;
		VolumeFlyoutSlide.Y = Motion.Mode == MotionMode.Off ? 0.0 : offset.Y;
	}

	private void AnimateVolumeFlyoutIn()
	{
		if (!VolumePopup.IsOpen)
		{
			return;
		}
		_volumeClosing = false;
		int token = ++_volumeAnimationToken;
		System.Windows.Vector offset = VolumeFlyoutOffset();
		VolumeFlyoutRoot.BeginAnimation(UIElement.OpacityProperty, null);
		VolumeFlyoutSlide.BeginAnimation(TranslateTransform.XProperty, null);
		VolumeFlyoutSlide.BeginAnimation(TranslateTransform.YProperty, null);
		if (Motion.Mode == MotionMode.Off)
		{
			VolumeFlyoutRoot.Opacity = 1.0;
			VolumeFlyoutSlide.X = 0.0;
			VolumeFlyoutSlide.Y = 0.0;
			VolumeFlyoutRoot.CacheMode = null;
			return;
		}
		VolumeFlyoutRoot.CacheMode = new BitmapCache();
		Duration duration = Motion.Dur(Motion.Cat.EdgeEnter);
		IEasingFunction ease = Motion.Ease(Motion.Cat.EdgeEnter);
		DoubleAnimation fade = new DoubleAnimation(0.0, 1.0, duration)
		{
			EasingFunction = ease,
			FillBehavior = FillBehavior.Stop
		};
		fade.Completed += delegate
		{
			if (token == _volumeAnimationToken)
			{
				VolumeFlyoutRoot.Opacity = 1.0;
				VolumeFlyoutSlide.X = 0.0;
				VolumeFlyoutSlide.Y = 0.0;
				VolumeFlyoutRoot.CacheMode = null;
			}
		};
		VolumeFlyoutRoot.BeginAnimation(UIElement.OpacityProperty, fade, HandoffBehavior.SnapshotAndReplace);
		VolumeFlyoutSlide.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(offset.X, 0.0, duration) { EasingFunction = ease, FillBehavior = FillBehavior.Stop }, HandoffBehavior.SnapshotAndReplace);
		VolumeFlyoutSlide.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(offset.Y, 0.0, duration) { EasingFunction = ease, FillBehavior = FillBehavior.Stop }, HandoffBehavior.SnapshotAndReplace);
		// Subtle modern grow-in: centre-anchored scale 0.985 -> 1.0 alongside the fade+slide. FillBehavior.Stop reverts
		// the scale to its base 1.0 afterwards, so no explicit reset is needed.
		VolumeFlyoutRoot.RenderTransformOrigin = new System.Windows.Point(0.5, 0.5);
		VolumeFlyoutScale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.985, 1.0, duration) { EasingFunction = ease, FillBehavior = FillBehavior.Stop }, HandoffBehavior.SnapshotAndReplace);
		VolumeFlyoutScale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.985, 1.0, duration) { EasingFunction = ease, FillBehavior = FillBehavior.Stop }, HandoffBehavior.SnapshotAndReplace);
	}

	private void CloseVolumeFlyout(bool animate)
	{
		if (!VolumePopup.IsOpen || (_volumeClosing && animate))
		{
			return;
		}
		StopVolumeTick();
		int token = ++_volumeAnimationToken;
		if (!animate || Motion.Mode == MotionMode.Off)
		{
			VolumePopup.IsOpen = false;
			return;
		}
		_volumeClosing = true;
		VolumeFlyoutRoot.CacheMode = new BitmapCache();
		System.Windows.Vector offset = VolumeFlyoutOffset();
		Duration duration = Motion.Dur(Motion.Cat.EdgeExit);
		IEasingFunction ease = Motion.Ease(Motion.Cat.EdgeExit);
		DoubleAnimation fade = new DoubleAnimation(0.0, duration) { EasingFunction = ease };
		fade.Completed += delegate
		{
			if (token == _volumeAnimationToken && _volumeClosing)
			{
				VolumePopup.IsOpen = false;
			}
		};
		VolumeFlyoutRoot.BeginAnimation(UIElement.OpacityProperty, fade, HandoffBehavior.SnapshotAndReplace);
		VolumeFlyoutSlide.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(offset.X, duration) { EasingFunction = ease }, HandoffBehavior.SnapshotAndReplace);
		VolumeFlyoutSlide.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(offset.Y, duration) { EasingFunction = ease }, HandoffBehavior.SnapshotAndReplace);
	}

	private void OnNetworkClick(object sender, RoutedEventArgs e)
	{
		CloseTrayCommandPopups();
		CloseVolumeFlyout(animate: false);
		if (NetPopup.IsOpen)
		{
			if (_networkClosing)
			{
				_networkClosing = false;
				_networkAnimationToken++;
				AnimateNetworkFlyoutIn();
				_ = RefreshNetworkFlyoutAsync(includeExternal: true);
			}
			else
			{
				CloseNetworkFlyout(animate: true);
			}
			return;
		}

		TaskOverflowPopup.IsOpen = false;
		OverflowPopup.IsOpen = false;
		CloseClockFlyout(animate: false);
		ApplyNetworkFlyoutTheme();
		ConfigureNetworkFlyoutSize();
		if (_networkSnapshot != null)
		{
			ApplyNetworkSnapshot(_networkSnapshot);
		}
		else
		{
			ApplyNetworkLoadingState();
		}
		PrepareNetworkFlyoutEnter();
		NetPopup.IsOpen = true;
	}

	private void OnNetworkPopupOpened(object sender, EventArgs e)
	{
		_networkClosing = false;
		Dispatcher.BeginInvoke((Action)AnimateNetworkFlyoutIn, DispatcherPriority.Render);
		_ = RefreshNetworkFlyoutAsync(includeExternal: true);
	}

	private void OnNetworkPopupClosed(object sender, EventArgs e)
	{
		CancelNetworkRefresh();
		SmoothScroll.StopVertical(NetworkBodyScroll);
		_networkClosing = false;
		NetworkFlyoutRoot.BeginAnimation(UIElement.OpacityProperty, null);
		NetworkFlyoutSlide.BeginAnimation(TranslateTransform.XProperty, null);
		NetworkFlyoutSlide.BeginAnimation(TranslateTransform.YProperty, null);
		NetworkFlyoutRoot.Opacity = 1.0;
		NetworkFlyoutSlide.X = 0.0;
		NetworkFlyoutSlide.Y = 0.0;
		NetworkFlyoutRoot.CacheMode = null;
		NetRefreshStatus.Visibility = Visibility.Collapsed;
	}

	private async Task RefreshNetworkFlyoutAsync(bool includeExternal)
	{
		CancelNetworkRefresh();
		int token = ++_networkRefreshToken;
		CancellationTokenSource cts = new CancellationTokenSource();
		_networkRefreshCts = cts;
		if (NetPopup.IsOpen)
		{
			NetRefreshStatus.Visibility = Visibility.Visible;
		}
		try
		{
			Stopwatch read = Stopwatch.StartNew();
			var localSnapshot = await Task.Run(() =>
			{
				cts.Token.ThrowIfCancellationRequested();
				NetState81 state = NetState81.Read();
				NetInfo.LocalNet local = NetInfo.Local();
				return (state, local);
			}, cts.Token);
			read.Stop();
			_networkInventoryMs = read.ElapsedMilliseconds;
			NetworkFlyoutSnapshot snapshot = new NetworkFlyoutSnapshot(localSnapshot.state, localSnapshot.local, null, read.ElapsedMilliseconds);
			_networkSnapshot = snapshot;
			if (token != _networkRefreshToken || cts.IsCancellationRequested || !NetPopup.IsOpen)
			{
				return;
			}
			ApplyNetworkSnapshot(snapshot);

			if (includeExternal && snapshot.State.Connected && snapshot.State.Internet)
			{
				string? publicIp = await NetInfo.PublicIpAsync(cts.Token);
				if (token != _networkRefreshToken || cts.IsCancellationRequested || !NetPopup.IsOpen)
				{
					return;
				}
				snapshot = snapshot with { PublicIp = publicIp };
				_networkSnapshot = snapshot;
				ApplyNetworkSnapshot(snapshot);
			}
		}
		catch (OperationCanceledException)
		{
		}
		catch (Exception ex)
		{
			Logger.Log("Network flyout refresh failed: " + ex.Message);
			if (token == _networkRefreshToken && NetPopup.IsOpen)
			{
				NetRefreshStatus.Text = "Refresh failed";
			}
		}
		finally
		{
			bool isCurrent = ReferenceEquals(_networkRefreshCts, cts);
			if (isCurrent)
			{
				_networkRefreshCts = null;
				if (NetPopup.IsOpen)
				{
					NetRefreshStatus.Visibility = Visibility.Collapsed;
					NetRefreshStatus.Text = "Updating...";
				}
			}
			cts.Dispose();
		}
	}

	private void CancelNetworkRefresh()
	{
		_networkRefreshToken++;
		CancellationTokenSource? cts = _networkRefreshCts;
		_networkRefreshCts = null;
		if (cts != null)
		{
			try
			{
				cts.Cancel();
			}
			catch
			{
			}
		}
	}

	private void ApplyNetworkLoadingState()
	{
		SetNetworkHero(new NetState81(NetKind.Wifi, "Wi-Fi", 0, Internet: false, NetworkIconState.Scanning));
		NetTitle.Text = "Network";
		NetConnectionStatus.Text = "Checking connection";
		NetAdapter.Text = "Detecting network adapter...";
		NetPrimaryLabel.Text = "Network";
		NetPrimaryValue.Text = "—";
		NetSpeed.Text = "—";
		NetSignalStrength.Text = "—";
		NetLocalIp.Text = "—";
		NetGateway.Text = "—";
		NetDns.Text = "—";
		NetPublicIp.Text = "—";
		NetStateBanner.Visibility = Visibility.Collapsed;
	}

	private void ApplyNetworkSnapshot(NetworkFlyoutSnapshot snapshot)
	{
		NetState81 state = snapshot.State;
		NetInfo.LocalNet local = snapshot.Local;
		SetNetworkHero(state);
		NetTitle.Text = state.Kind switch
		{
			NetKind.Wifi => "Wi-Fi",
			NetKind.Ethernet => "Ethernet",
			NetKind.Cellular => "Cellular",
			NetKind.Airplane => "Airplane mode",
			_ => "No network"
		};
		NetConnectionStatus.Text = state.EffectiveIconState switch
		{
			NetworkIconState.Airplane => "Wireless disabled",
			NetworkIconState.CaptivePortal => "Sign-in required",
			NetworkIconState.NoInternet => "No Internet access",
			NetworkIconState.Limited => "Limited connectivity",
			NetworkIconState.Metered => "Connected · metered",
			NetworkIconState.Roaming => "Connected · roaming",
			NetworkIconState.Connecting => "Connecting",
			NetworkIconState.Identifying => "Identifying network",
			_ => state.Connected ? "Connected" : "Not connected"
		};
		NetAdapter.Text = state.Connected && local.Type != "Offline" ? local.Adapter : "No active network adapter";
		NetPrimaryLabel.Text = state.Kind == NetKind.Wifi ? "SSID" : (state.Kind == NetKind.Ethernet ? "Network" : "Connection");
		NetPrimaryValue.Text = state.Kind == NetKind.Wifi || state.Kind == NetKind.Cellular
			? state.Label
			: (state.Kind == NetKind.Ethernet ? (string.IsNullOrWhiteSpace(local.Adapter) ? "Ethernet" : local.Adapter) : "—");
		NetSpeed.Text = state.Connected ? local.Speed : "—";
		NetSignalStrength.Text = state.Kind == NetKind.Wifi || state.Kind == NetKind.Cellular
			? SignalStrengthLabel(state.Bars)
			: (state.Kind == NetKind.Ethernet ? "Wired" : "—");
		NetLocalIp.Text = state.Connected ? local.LocalIp : "—";
		NetGateway.Text = state.Connected ? local.Gateway : "—";
		NetDns.Text = state.Connected ? local.Dns : "—";
		NetPublicIp.Text = !state.Connected || !state.Internet
			? "Unavailable"
			: (string.IsNullOrWhiteSpace(snapshot.PublicIp) ? "Checking..." : snapshot.PublicIp);

		if (state.Internet)
		{
			NetStateBanner.Visibility = Visibility.Collapsed;
		}
		else
		{
			NetStateMessage.Text = state.EffectiveIconState switch
			{
				NetworkIconState.Airplane => "Airplane mode is on. Turn on Wi-Fi or disable airplane mode to connect.",
				NetworkIconState.CaptivePortal => "This network requires sign-in before Internet access is available.",
				NetworkIconState.NoInternet => "Connected to the local network, but there is no Internet access.",
				NetworkIconState.Offline or NetworkIconState.NotConnected or NetworkIconState.CableUnplugged => "There are no active network connections available.",
				_ => "Connected to the local network, but there is no Internet access."
			};
			NetStateBanner.Visibility = Visibility.Visible;
		}
	}

	private void SetNetworkHero(NetState81 state)
	{
		_networkHeroKind = state.Kind;
		_networkHeroState = state.EffectiveIconState;
		NetHeroImage.Source = NetIcons81.For(state, 32);
	}

	private static string SignalStrengthLabel(int bars)
	{
		return Math.Clamp(bars, 0, 5) switch
		{
			5 => "Excellent",
			4 => "Excellent",
			3 => "Good",
			2 => "Fair",
			1 => "Weak",
			_ => "Unavailable"
		};
	}

	private void ConfigureNetworkFlyoutSize(double? forcedWidthDiu = null, double? forcedHeightDiu = null)
	{
		double dpiX = 1.0;
		double dpiY = 1.0;
		try
		{
			DpiScale dpi = VisualTreeHelper.GetDpi(this);
			dpiX = Math.Max(0.5, dpi.DpiScaleX);
			dpiY = Math.Max(0.5, dpi.DpiScaleY);
		}
		catch
		{
		}
		double availableWidth = _screen.WorkingArea.Width / dpiX;
		double availableHeight = _screen.WorkingArea.Height / dpiY;
		double width = forcedWidthDiu ?? Math.Max(360.0, Math.Min(420.0, availableWidth - 10.0));
		double height = forcedHeightDiu ?? Math.Max(430.0, Math.Min(500.0, availableHeight - 10.0));
		bool veryCompact = width < 380.0 || height < 460.0;
		bool compact = veryCompact || width < 420.0 || height < 500.0;

		NetworkFlyoutRoot.Width = width;
		NetworkFlyoutRoot.Height = height;
		NetworkHeaderRow.Height = new GridLength(veryCompact ? 98.0 : (compact ? 105.0 : 112.0));
		NetworkFooterRow.Height = new GridLength(veryCompact ? 108.0 : (compact ? 114.0 : 120.0));
		NetworkBodyScroll.Padding = veryCompact ? new Thickness(10.0, 5.0, 7.0, 4.0) : (compact ? new Thickness(12.0, 6.0, 8.0, 5.0) : new Thickness(15.0, 8.0, 10.0, 7.0));
		NetTitle.FontSize = veryCompact ? 24.0 : (compact ? 27.0 : 29.0);
		NetConnectionStatus.FontSize = veryCompact ? 14.5 : (compact ? 16.0 : 17.0);
	}

	private CustomPopupPlacement[] PlaceNetworkPopup(System.Windows.Size popupSize, System.Windows.Size targetSize, System.Windows.Point offset)
	{
		const double gap = 6.0;
		return _position switch
		{
			"Top" => new[] { new CustomPopupPlacement(new System.Windows.Point(targetSize.Width - popupSize.Width, targetSize.Height + gap), PopupPrimaryAxis.Horizontal) },
			"Left" => new[] { new CustomPopupPlacement(new System.Windows.Point(targetSize.Width + gap, targetSize.Height - popupSize.Height), PopupPrimaryAxis.Vertical) },
			"Right" => new[] { new CustomPopupPlacement(new System.Windows.Point(-popupSize.Width - gap, targetSize.Height - popupSize.Height), PopupPrimaryAxis.Vertical) },
			_ => new[] { new CustomPopupPlacement(new System.Windows.Point(targetSize.Width - popupSize.Width, -popupSize.Height - gap), PopupPrimaryAxis.Horizontal) }
		};
	}

	private System.Windows.Vector NetworkFlyoutOffset()
	{
		return _position switch
		{
			"Top" => new System.Windows.Vector(0.0, -12.0),
			"Left" => new System.Windows.Vector(-12.0, 0.0),
			"Right" => new System.Windows.Vector(12.0, 0.0),
			_ => new System.Windows.Vector(0.0, 12.0)
		};
	}

	private void PrepareNetworkFlyoutEnter()
	{
		NetworkFlyoutRoot.BeginAnimation(UIElement.OpacityProperty, null);
		NetworkFlyoutSlide.BeginAnimation(TranslateTransform.XProperty, null);
		NetworkFlyoutSlide.BeginAnimation(TranslateTransform.YProperty, null);
		System.Windows.Vector offset = NetworkFlyoutOffset();
		NetworkFlyoutRoot.Opacity = Motion.Mode == MotionMode.Off ? 1.0 : 0.0;
		NetworkFlyoutSlide.X = Motion.Mode == MotionMode.Off ? 0.0 : offset.X;
		NetworkFlyoutSlide.Y = Motion.Mode == MotionMode.Off ? 0.0 : offset.Y;
	}

	private void AnimateNetworkFlyoutIn()
	{
		if (!NetPopup.IsOpen)
		{
			return;
		}
		_networkClosing = false;
		int token = ++_networkAnimationToken;
		System.Windows.Vector offset = NetworkFlyoutOffset();
		NetworkFlyoutRoot.BeginAnimation(UIElement.OpacityProperty, null);
		NetworkFlyoutSlide.BeginAnimation(TranslateTransform.XProperty, null);
		NetworkFlyoutSlide.BeginAnimation(TranslateTransform.YProperty, null);
		if (Motion.Mode == MotionMode.Off)
		{
			NetworkFlyoutRoot.Opacity = 1.0;
			NetworkFlyoutSlide.X = 0.0;
			NetworkFlyoutSlide.Y = 0.0;
			NetworkFlyoutRoot.CacheMode = null;
			return;
		}
		NetworkFlyoutRoot.CacheMode = new BitmapCache();
		Duration duration = Motion.Dur(Motion.Cat.EdgeEnter);
		IEasingFunction ease = Motion.Ease(Motion.Cat.EdgeEnter);
		DoubleAnimation fade = new DoubleAnimation(0.0, 1.0, duration)
		{
			EasingFunction = ease,
			FillBehavior = FillBehavior.Stop
		};
		fade.Completed += delegate
		{
			if (token == _networkAnimationToken && NetPopup.IsOpen && !_networkClosing)
			{
				NetworkFlyoutRoot.Opacity = 1.0;
				NetworkFlyoutSlide.X = 0.0;
				NetworkFlyoutSlide.Y = 0.0;
				NetworkFlyoutRoot.CacheMode = null;
			}
		};
		NetworkFlyoutRoot.BeginAnimation(UIElement.OpacityProperty, fade, HandoffBehavior.SnapshotAndReplace);
		NetworkFlyoutSlide.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(offset.X, 0.0, duration) { EasingFunction = ease, FillBehavior = FillBehavior.Stop }, HandoffBehavior.SnapshotAndReplace);
		NetworkFlyoutSlide.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(offset.Y, 0.0, duration) { EasingFunction = ease, FillBehavior = FillBehavior.Stop }, HandoffBehavior.SnapshotAndReplace);
		NetworkFlyoutRoot.RenderTransformOrigin = new System.Windows.Point(0.5, 0.5);
		NetworkFlyoutScale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.985, 1.0, duration) { EasingFunction = ease, FillBehavior = FillBehavior.Stop }, HandoffBehavior.SnapshotAndReplace);
		NetworkFlyoutScale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.985, 1.0, duration) { EasingFunction = ease, FillBehavior = FillBehavior.Stop }, HandoffBehavior.SnapshotAndReplace);
	}

	private void CloseNetworkFlyout(bool animate)
	{
		if (!NetPopup.IsOpen || (_networkClosing && animate))
		{
			return;
		}
		CancelNetworkRefresh();
		int token = ++_networkAnimationToken;
		if (!animate || Motion.Mode == MotionMode.Off)
		{
			NetPopup.IsOpen = false;
			return;
		}
		_networkClosing = true;
		NetworkFlyoutRoot.CacheMode = new BitmapCache();
		System.Windows.Vector offset = NetworkFlyoutOffset();
		Duration duration = Motion.Dur(Motion.Cat.EdgeExit);
		IEasingFunction ease = Motion.Ease(Motion.Cat.EdgeExit);
		DoubleAnimation fade = new DoubleAnimation(0.0, duration) { EasingFunction = ease };
		fade.Completed += delegate
		{
			if (token == _networkAnimationToken && _networkClosing)
			{
				NetPopup.IsOpen = false;
			}
		};
		NetworkFlyoutRoot.BeginAnimation(UIElement.OpacityProperty, fade, HandoffBehavior.SnapshotAndReplace);
		NetworkFlyoutSlide.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(offset.X, duration) { EasingFunction = ease }, HandoffBehavior.SnapshotAndReplace);
		NetworkFlyoutSlide.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(offset.Y, duration) { EasingFunction = ease }, HandoffBehavior.SnapshotAndReplace);
	}

	private void OnNetworkBodyPreviewMouseWheel(object sender, MouseWheelEventArgs e)
	{
		if (NetworkBodyScroll.ScrollableHeight <= 0.5 || e.Delta == 0)
		{
			return;
		}
		_networkWheelRouteCount++;
		e.Handled = true;
		double notches = (double)e.Delta / 120.0;
		double step = Math.Clamp(NetworkBodyScroll.ViewportHeight * 0.2, 48.0, 72.0);
		SmoothScroll.ByVertical(NetworkBodyScroll, -notches * step);
	}

	private void OnNetworkBodyPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
	{
		if (e.ChangedButton == MouseButton.Left)
		{
			SmoothScroll.StopVertical(NetworkBodyScroll);
		}
	}

	private void OnOpenWifiSettings(object sender, RoutedEventArgs e)
	{
		CloseTrayCommandPopups();
		CloseNetworkFlyout(animate: false);
		OpenUri("ms-settings:network-wifi");
	}

	private void OnOpenNetworkSettings(object sender, RoutedEventArgs e)
	{
		CloseTrayCommandPopups();
		CloseNetworkFlyout(animate: false);
		OpenUri("ms-settings:network-status");
	}

	private void OnOpenNetworkConnections(object sender, RoutedEventArgs e)
	{
		CloseTrayCommandPopups();
		CloseNetworkFlyout(animate: false);
		TrayRun("control.exe", "ncpa.cpl");
	}

	private void OnTroubleshootNetwork(object sender, RoutedEventArgs e)
	{
		CloseTrayCommandPopups();
		CloseNetworkFlyout(animate: false);
		string msdt = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "msdt.exe");
		if (Environment.OSVersion.Version.Build < 22000 && File.Exists(msdt))
		{
			TrayRun(msdt, "/id NetworkDiagnosticsNetworkAdapter");
		}
		else
		{
			OpenUri("ms-settings:troubleshoot");
		}
	}

	private void OnOpenNetworkSharingCenter(object sender, RoutedEventArgs e)
	{
		CloseTrayCommandPopups();
		CloseNetworkFlyout(animate: false);
		TrayRun("control.exe", "/name Microsoft.NetworkAndSharingCenter");
	}

	private void OnOpenSoundSettings(object sender, RoutedEventArgs e)
	{
		CloseTrayCommandPopups();
		CloseVolumeFlyout(animate: false);
		OpenUri("ms-settings:sound");
	}

	private void OnOpenPlaybackDevices(object sender, RoutedEventArgs e)
	{
		CloseTrayCommandPopups();
		CloseVolumeFlyout(animate: false);
		TrayRun("control.exe", "mmsys.cpl");
	}

	private void OnOpenRecordingDevices(object sender, RoutedEventArgs e)
	{
		CloseTrayCommandPopups();
		CloseVolumeFlyout(animate: false);
		TrayRun("control.exe", "mmsys.cpl,,1");
	}

	private void OnOpenSounds(object sender, RoutedEventArgs e)
	{
		CloseTrayCommandPopups();
		CloseVolumeFlyout(animate: false);
		TrayRun("control.exe", "mmsys.cpl,,2");
	}

	private void OnOpenClassicVolumeMixer(object sender, RoutedEventArgs e)
	{
		CloseTrayCommandPopups();
		CloseVolumeFlyout(animate: false);
		TrayRun("sndvol.exe", "");
	}

	private void OnBatteryClick(object sender, RoutedEventArgs e)
	{
		OpenUri("ms-settings:batterysaver");
	}

	private void OnClockClick(object sender, MouseButtonEventArgs e)
	{
		if (ClockPopup.IsOpen)
		{
			if (_clockClosing)
			{
				AnimateClockFlyoutIn();
				StartClockTick();
			}
			else
			{
				CloseClockFlyout(animate: true);
			}
			e.Handled = true;
			return;
		}

		TaskOverflowPopup.IsOpen = false;
		OverflowPopup.IsOpen = false;
		CloseNetworkFlyout(animate: false);
		VolumePopup.IsOpen = false;
		DateTime now = DateTime.Now;
		ApplyClockCalendarTheme();
		_calendarToday = now.Date;
		_calendarSelection = now.Date;
		_calMonth = new DateTime(now.Year, now.Month, 1);
		UpdateClockDisplay(now);
		BuildMonth(_calMonth);
		ConfigureClockFlyoutSize();
		PrepareClockFlyoutEnter();
		ClockPopup.IsOpen = true;
		e.Handled = true;
	}

	private void OnClockPopupOpened(object sender, EventArgs e)
	{
		_clockClosing = false;
		StartClockTick();
		Dispatcher.BeginInvoke((Action)AnimateClockFlyoutIn, DispatcherPriority.Render);
	}

	private void OnClockPopupClosed(object sender, EventArgs e)
	{
		StopClockTick();
		_clockClosing = false;
		_clockAnimationToken++;
		ClockFlyoutRoot.BeginAnimation(UIElement.OpacityProperty, null);
		ClockFlyoutSlide.BeginAnimation(TranslateTransform.XProperty, null);
		ClockFlyoutSlide.BeginAnimation(TranslateTransform.YProperty, null);
		ClockFlyoutRoot.Opacity = 1.0;
		ClockFlyoutSlide.X = 0.0;
		ClockFlyoutSlide.Y = 0.0;
		ClockFlyoutRoot.CacheMode = null;
	}

	private void UpdateClockDisplay(DateTime now)
	{
		CultureInfo ci = CultureInfo.CurrentCulture;
		ClockBig.Text = now.ToString("HH:mm", ci);
		ClockSeconds.Text = now.ToString("ss", ci);
		ClockFull.Text = now.ToString("dddd, d MMMM yyyy", ci);
	}

	// The timer only exists while the flyout is open. Re-aligning each interval to the next wall-clock second avoids
	// drift without polling more often or adding an idle wake-up to the shell.
	private void StartClockTick()
	{
		if (_clockTick == null)
		{
			_clockTick = new DispatcherTimer(DispatcherPriority.Background);
			_clockTick.Tick += delegate
			{
				if (ClockPopup == null || !ClockPopup.IsOpen)
				{
					_clockTick?.Stop();
					return;
				}
				DateTime now = DateTime.Now;
				UpdateClockDisplay(now);
				if (now.Date != _calendarToday)
				{
					bool followedToday = _calMonth.Year == _calendarToday.Year && _calMonth.Month == _calendarToday.Month;
					_calendarToday = now.Date;
					_calendarSelection = now.Date;
					if (followedToday)
					{
						_calMonth = new DateTime(now.Year, now.Month, 1);
					}
					BuildMonth(_calMonth);
				}
				_clockTick.Interval = TimeSpan.FromMilliseconds(Math.Max(100.0, 1000.0 - DateTime.Now.Millisecond));
			};
		}
		DateTime current = DateTime.Now;
		UpdateClockDisplay(current);
		_clockTick.Interval = TimeSpan.FromMilliseconds(Math.Max(100.0, 1000.0 - current.Millisecond));
		_clockTick.Stop();
		_clockTick.Start();
	}

	private void StopClockTick()
	{
		_clockTick?.Stop();
	}

	private void OnPrevMonth(object sender, RoutedEventArgs e)
	{
		_calMonth = _calMonth.AddMonths(-1);
		BuildMonth(_calMonth);
		AnimateMonthChange(-1);
	}

	private void OnNextMonth(object sender, RoutedEventArgs e)
	{
		_calMonth = _calMonth.AddMonths(1);
		BuildMonth(_calMonth);
		AnimateMonthChange(1);
	}

	private void BuildMonth(DateTime month)
	{
		EnsureCalendarVisuals();
		CultureInfo ci = CultureInfo.CurrentCulture;
		MonthLabel.Text = month.ToString("MMMM yyyy", ci);
		DayOfWeek firstDow = ci.DateTimeFormat.FirstDayOfWeek;
		string[] abbreviated = ci.DateTimeFormat.AbbreviatedDayNames;
		for (int i = 0; i < 7; i++)
		{
			int dow = (int)(firstDow + i) % 7;
			string label = abbreviated[dow].TrimEnd('.');
			if (label.Length > 3)
			{
				label = label.Substring(0, 3);
			}
			_calendarHeaderLabels[i].Text = label.ToUpper(ci);
		}
		int offset = (new DateTime(month.Year, month.Month, 1).DayOfWeek - firstDow + 7) % 7;
		int days = DateTime.DaysInMonth(month.Year, month.Month);
		for (int slot = 0; slot < _calendarDayButtons.Length; slot++)
		{
			System.Windows.Controls.Button dayButton = _calendarDayButtons[slot];
			int day = slot - offset + 1;
			if (day < 1 || day > days)
			{
				dayButton.Content = string.Empty;
				dayButton.Tag = null;
				dayButton.IsEnabled = false;
				dayButton.Visibility = Visibility.Hidden;
				dayButton.Background = System.Windows.Media.Brushes.Transparent;
				continue;
			}

			DateTime date = new DateTime(month.Year, month.Month, day);
			bool selected = date == _calendarSelection;
			dayButton.Content = day.ToString(ci);
			dayButton.Tag = date;
			dayButton.IsEnabled = true;
			dayButton.Visibility = Visibility.Visible;
			dayButton.FontWeight = (selected || date == _calendarToday) ? FontWeights.SemiBold : FontWeights.Normal;
			System.Windows.Automation.AutomationProperties.SetName(dayButton, date.ToString("D", ci));
			if (selected)
			{
				dayButton.SetResourceReference(System.Windows.Controls.Control.BackgroundProperty, "TB.ClockSelection");
			}
			else
			{
				dayButton.Background = System.Windows.Media.Brushes.Transparent;
			}
		}
	}

	private void EnsureCalendarVisuals()
	{
		if (_calendarVisualsReady)
		{
			return;
		}

		Stopwatch sw = Stopwatch.StartNew();
		MonthGrid.Children.Clear();
		MonthGrid.ColumnDefinitions.Clear();
		MonthGrid.RowDefinitions.Clear();
		for (int c = 0; c < 7; c++)
		{
			MonthGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.0, GridUnitType.Star) });
		}
		MonthGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(0.72, GridUnitType.Star) });
		for (int r = 1; r < 7; r++)
		{
			MonthGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1.0, GridUnitType.Star) });
		}

		for (int i = 0; i < 7; i++)
		{
			TextBlock header = new TextBlock
			{
				Foreground = new SolidColorBrush(System.Windows.Media.Color.FromArgb(190, byte.MaxValue, byte.MaxValue, byte.MaxValue)),
				FontFamily = new System.Windows.Media.FontFamily("Segoe UI"),
				FontSize = 14.0,
				FontWeight = FontWeights.SemiBold,
				HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
				VerticalAlignment = VerticalAlignment.Center
			};
			_calendarHeaderLabels[i] = header;
			Grid.SetColumn(header, i);
			Grid.SetRow(header, 0);
			MonthGrid.Children.Add(header);
		}

		Style dayStyle = (Style)FindResource("MetroCalendarDay");
		for (int slot = 0; slot < _calendarDayButtons.Length; slot++)
		{
			System.Windows.Controls.Button dayButton = new System.Windows.Controls.Button
			{
				Style = dayStyle,
				Width = 44.0,
				Height = 42.0,
				HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
				VerticalAlignment = VerticalAlignment.Center,
				Visibility = Visibility.Hidden,
				IsEnabled = false
			};
			dayButton.Click += OnCalendarDayClick;
			_calendarDayButtons[slot] = dayButton;
			Grid.SetColumn(dayButton, slot % 7);
			Grid.SetRow(dayButton, 1 + slot / 7);
			MonthGrid.Children.Add(dayButton);
		}

		_calendarVisualsReady = true;
		sw.Stop();
		_clockCalendarBuildMs = sw.ElapsedMilliseconds;
	}

	private void OnCalendarDayClick(object sender, RoutedEventArgs e)
	{
		if (sender is System.Windows.Controls.Button { Tag: DateTime date })
		{
			_calendarSelection = date;
			BuildMonth(_calMonth);
		}
	}

	private void AnimateMonthChange(int direction)
	{
		int token = ++_monthAnimationToken;
		MonthGrid.BeginAnimation(UIElement.OpacityProperty, null);
		MonthLabel.BeginAnimation(UIElement.OpacityProperty, null);
		MonthGridSlide.BeginAnimation(TranslateTransform.XProperty, null);
		MonthLabelSlide.BeginAnimation(TranslateTransform.XProperty, null);
		MonthGrid.Opacity = 1.0;
		MonthLabel.Opacity = 1.0;
		MonthGridSlide.X = 0.0;
		MonthLabelSlide.X = 0.0;
		if (Motion.Mode == MotionMode.Off)
		{
			MonthGrid.CacheMode = null;
			return;
		}

		double from = direction > 0 ? 20.0 : -20.0;
		Duration duration = Motion.Dur(Motion.Cat.Micro);
		IEasingFunction ease = Motion.Ease(Motion.Cat.Micro);
		MonthGrid.CacheMode = new BitmapCache();
		DoubleAnimation fade = new DoubleAnimation(0.28, 1.0, duration)
		{
			EasingFunction = ease,
			FillBehavior = FillBehavior.Stop
		};
		fade.Completed += delegate
		{
			if (token == _monthAnimationToken)
			{
				MonthGrid.CacheMode = null;
			}
		};
		MonthGrid.BeginAnimation(UIElement.OpacityProperty, fade, HandoffBehavior.SnapshotAndReplace);
		MonthLabel.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0.45, 1.0, duration)
		{
			EasingFunction = ease,
			FillBehavior = FillBehavior.Stop
		}, HandoffBehavior.SnapshotAndReplace);
		MonthGridSlide.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(from, 0.0, duration)
		{
			EasingFunction = ease,
			FillBehavior = FillBehavior.Stop
		}, HandoffBehavior.SnapshotAndReplace);
		MonthLabelSlide.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(from * 0.6, 0.0, duration)
		{
			EasingFunction = ease,
			FillBehavior = FillBehavior.Stop
		}, HandoffBehavior.SnapshotAndReplace);
	}

	private void ConfigureClockFlyoutSize(double? forcedWidthDiu = null, double? forcedHeightDiu = null)
	{
		double dpiX = 1.0;
		double dpiY = 1.0;
		try
		{
			DpiScale dpi = VisualTreeHelper.GetDpi(this);
			dpiX = Math.Max(0.5, dpi.DpiScaleX);
			dpiY = Math.Max(0.5, dpi.DpiScaleY);
		}
		catch
		{
		}
		double availableWidth = _screen.WorkingArea.Width / dpiX;
		double availableHeight = _screen.WorkingArea.Height / dpiY;
		double width = forcedWidthDiu ?? Math.Max(300.0, Math.Min(340.0, availableWidth - 10.0));
		double height = forcedHeightDiu ?? Math.Max(430.0, Math.Min(500.0, availableHeight - 10.0));
		bool veryCompact = height < 450.0;
		bool compact = height < 490.0;

		ClockFlyoutRoot.Width = width;
		ClockFlyoutRoot.Height = height;
		ClockHeaderRow.Height = new GridLength(veryCompact ? 108.0 : (compact ? 118.0 : 126.0));
		ClockFooterRow.Height = new GridLength(veryCompact ? 50.0 : (compact ? 54.0 : 58.0));
		CalendarNavRow.Height = new GridLength(veryCompact ? 36.0 : (compact ? 38.0 : 42.0));
		ClockBodyLayout.Margin = veryCompact ? new Thickness(10.0, 6.0, 10.0, 6.0) : (compact ? new Thickness(12.0, 8.0, 12.0, 8.0) : new Thickness(14.0, 10.0, 14.0, 10.0));
		MonthGrid.Margin = veryCompact ? new Thickness(0.0, 2.0, 0.0, 0.0) : (compact ? new Thickness(0.0, 3.0, 0.0, 0.0) : new Thickness(0.0, 4.0, 0.0, 0.0));
		ClockBig.FontSize = veryCompact ? 48.0 : (compact ? 52.0 : 56.0);
		ClockSeconds.FontSize = veryCompact ? 22.0 : (compact ? 24.0 : 26.0);
		ClockFull.FontSize = veryCompact ? 14.0 : (compact ? 15.0 : 16.0);
		MonthLabel.FontSize = veryCompact ? 20.0 : (compact ? 21.0 : 23.0);
		for (int i = 0; i < _calendarHeaderLabels.Length; i++)
		{
			TextBlock header = _calendarHeaderLabels[i];
			if (header != null)
			{
				header.FontSize = veryCompact ? 10.0 : (compact ? 10.5 : 11.0);
			}
		}
		for (int i = 0; i < _calendarDayButtons.Length; i++)
		{
			System.Windows.Controls.Button dayButton = _calendarDayButtons[i];
			if (dayButton == null)
			{
				continue;
			}
			dayButton.Width = veryCompact ? 28.0 : (compact ? 30.0 : 34.0);
			dayButton.Height = veryCompact ? 26.0 : (compact ? 28.0 : 32.0);
			dayButton.FontSize = veryCompact ? 13.0 : (compact ? 14.0 : 15.0);
		}
	}

	private CustomPopupPlacement[] PlaceClockPopup(System.Windows.Size popupSize, System.Windows.Size targetSize, System.Windows.Point offset)
	{
		const double gap = 6.0;
		return _position switch
		{
			"Top" => new[] { new CustomPopupPlacement(new System.Windows.Point(targetSize.Width - popupSize.Width, targetSize.Height + gap), PopupPrimaryAxis.Horizontal) },
			"Left" => new[] { new CustomPopupPlacement(new System.Windows.Point(targetSize.Width + gap, targetSize.Height - popupSize.Height), PopupPrimaryAxis.Vertical) },
			"Right" => new[] { new CustomPopupPlacement(new System.Windows.Point(-popupSize.Width - gap, targetSize.Height - popupSize.Height), PopupPrimaryAxis.Vertical) },
			_ => new[] { new CustomPopupPlacement(new System.Windows.Point(targetSize.Width - popupSize.Width, -popupSize.Height - gap), PopupPrimaryAxis.Horizontal) }
		};
	}

	private System.Windows.Vector ClockFlyoutOffset()
	{
		return _position switch
		{
			"Top" => new System.Windows.Vector(0.0, -12.0),
			"Left" => new System.Windows.Vector(-12.0, 0.0),
			"Right" => new System.Windows.Vector(12.0, 0.0),
			_ => new System.Windows.Vector(0.0, 12.0)
		};
	}

	private void PrepareClockFlyoutEnter()
	{
		ClockFlyoutRoot.BeginAnimation(UIElement.OpacityProperty, null);
		ClockFlyoutSlide.BeginAnimation(TranslateTransform.XProperty, null);
		ClockFlyoutSlide.BeginAnimation(TranslateTransform.YProperty, null);
		System.Windows.Vector offset = ClockFlyoutOffset();
		ClockFlyoutRoot.Opacity = Motion.Mode == MotionMode.Off ? 1.0 : 0.0;
		ClockFlyoutSlide.X = Motion.Mode == MotionMode.Off ? 0.0 : offset.X;
		ClockFlyoutSlide.Y = Motion.Mode == MotionMode.Off ? 0.0 : offset.Y;
	}

	private void AnimateClockFlyoutIn()
	{
		if (!ClockPopup.IsOpen)
		{
			return;
		}
		int token = ++_clockAnimationToken;
		_clockClosing = false;
		System.Windows.Vector offset = ClockFlyoutOffset();
		ClockFlyoutRoot.BeginAnimation(UIElement.OpacityProperty, null);
		ClockFlyoutSlide.BeginAnimation(TranslateTransform.XProperty, null);
		ClockFlyoutSlide.BeginAnimation(TranslateTransform.YProperty, null);
		ClockFlyoutRoot.Opacity = 1.0;
		ClockFlyoutSlide.X = 0.0;
		ClockFlyoutSlide.Y = 0.0;
		if (Motion.Mode == MotionMode.Off)
		{
			ClockFlyoutRoot.CacheMode = null;
			return;
		}

		ClockFlyoutRoot.CacheMode = new BitmapCache();
		Duration duration = Motion.Dur(Motion.Cat.EdgeEnter);
		IEasingFunction ease = Motion.Ease(Motion.Cat.EdgeEnter);
		DoubleAnimation fade = new DoubleAnimation(0.0, 1.0, duration)
		{
			EasingFunction = ease,
			FillBehavior = FillBehavior.Stop
		};
		fade.Completed += delegate
		{
			if (token == _clockAnimationToken && ClockPopup.IsOpen && !_clockClosing)
			{
				ClockFlyoutRoot.CacheMode = null;
			}
		};
		ClockFlyoutRoot.BeginAnimation(UIElement.OpacityProperty, fade, HandoffBehavior.SnapshotAndReplace);
		ClockFlyoutSlide.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(offset.X, 0.0, duration)
		{
			EasingFunction = ease,
			FillBehavior = FillBehavior.Stop
		}, HandoffBehavior.SnapshotAndReplace);
		ClockFlyoutSlide.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(offset.Y, 0.0, duration)
		{
			EasingFunction = ease,
			FillBehavior = FillBehavior.Stop
		}, HandoffBehavior.SnapshotAndReplace);
		ClockFlyoutRoot.RenderTransformOrigin = new System.Windows.Point(0.5, 0.5);
		ClockFlyoutScale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.985, 1.0, duration) { EasingFunction = ease, FillBehavior = FillBehavior.Stop }, HandoffBehavior.SnapshotAndReplace);
		ClockFlyoutScale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.985, 1.0, duration) { EasingFunction = ease, FillBehavior = FillBehavior.Stop }, HandoffBehavior.SnapshotAndReplace);
	}

	private void CloseClockFlyout(bool animate)
	{
		if (!ClockPopup.IsOpen)
		{
			return;
		}
		if (_clockClosing && animate)
		{
			return;
		}
		StopClockTick();
		int token = ++_clockAnimationToken;
		if (!animate || Motion.Mode == MotionMode.Off)
		{
			ClockPopup.IsOpen = false;
			return;
		}

		_clockClosing = true;
		ClockFlyoutRoot.CacheMode = new BitmapCache();
		System.Windows.Vector offset = ClockFlyoutOffset();
		Duration duration = Motion.Dur(Motion.Cat.EdgeExit);
		IEasingFunction ease = Motion.Ease(Motion.Cat.EdgeExit);
		DoubleAnimation fade = new DoubleAnimation(0.0, duration)
		{
			EasingFunction = ease
		};
		fade.Completed += delegate
		{
			if (token == _clockAnimationToken && _clockClosing)
			{
				ClockPopup.IsOpen = false;
			}
		};
		ClockFlyoutRoot.BeginAnimation(UIElement.OpacityProperty, fade, HandoffBehavior.SnapshotAndReplace);
		ClockFlyoutSlide.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(offset.X, duration)
		{
			EasingFunction = ease
		}, HandoffBehavior.SnapshotAndReplace);
		ClockFlyoutSlide.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(offset.Y, duration)
		{
			EasingFunction = ease
		}, HandoffBehavior.SnapshotAndReplace);
	}

	private void OnOpenDateTimeSettings(object sender, RoutedEventArgs e)
	{
		CloseClockFlyout(animate: false);
		OpenUri("ms-settings:dateandtime");
	}

	private static void OpenUri(string uri)
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
			Logger.Log("OpenUri " + uri + " failed: " + ex.Message);
		}
	}

	public void QaRenderTray(string outPath)
	{
		//IL_0119: Unknown result type (might be due to invalid IL or missing references)
		//IL_0145: Unknown result type (might be due to invalid IL or missing references)
		//IL_0183: Unknown result type (might be due to invalid IL or missing references)
		//IL_0188: Unknown result type (might be due to invalid IL or missing references)
		//IL_021d: Unknown result type (might be due to invalid IL or missing references)
		//IL_0251: Unknown result type (might be due to invalid IL or missing references)
		//IL_033a: Unknown result type (might be due to invalid IL or missing references)
		//IL_036c: Unknown result type (might be due to invalid IL or missing references)
		_isPrimary = true;
		TrayPanel.Visibility = Visibility.Visible;
		_tray.VolumePct = 60;
		_tray.Muted = false;
		_tray.BatteryVisible = true;
		_tray.BatteryPct = 80;
		_tray.Charging = true;
		_tray.SetNetwork(up: true, wifi: true);
		_tray.PerfVisible = true;
		_tray.CpuText = "CPU 6%";
		_tray.RamText = "RAM 32%";
		_tray.NetText = "↓ 1.2 MB/s  ↑ 0.8 MB/s";
		RefreshAppIcons();
		ApplyStartGlyph();
		DateTime now = DateTime.Now;
		ClockTime.Text = now.ToString("HH:mm");
		ClockDate.Text = now.ToString((_orientedVertical == true) ? "d/M" : "ddd, d MMM");
		FrameworkElement root = (FrameworkElement)base.Content;
		root.Measure(new System.Windows.Size(2400.0, HeightPx));
		root.Arrange(new Rect(0.0, 0.0, 2400.0, HeightPx));
		root.UpdateLayout();
		PresentationSource src = PresentationSource.FromVisual(this);
		double? num;
		if (src == null)
		{
			num = null;
		}
		else
		{
			CompositionTarget compositionTarget = src.CompositionTarget;
			if (compositionTarget == null)
			{
				num = null;
			}
			else
			{
				Matrix transformToDevice = compositionTarget.TransformToDevice;
				num = transformToDevice.M11;
			}
		}
		double scale = num ?? 1.0;
		RenderTargetBitmap rtb = new RenderTargetBitmap((int)(2400.0 * scale), (int)(HeightPx * scale), 96.0 * scale, 96.0 * scale, PixelFormats.Pbgra32);
		DrawingVisual dv = new DrawingVisual();
		using (DrawingContext dc = dv.RenderOpen())
		{
			dc.DrawRectangle(new SolidColorBrush(TaskbarTheme.Current), null, new Rect(0.0, 0.0, 2400.0, HeightPx));
			dc.DrawRectangle(new VisualBrush(root), null, new Rect(0.0, 0.0, 2400.0, HeightPx));
		}
		rtb.Render(dv);
		PngBitmapEncoder enc = new PngBitmapEncoder();
		enc.Frames.Add(BitmapFrame.Create(rtb));
		using (FileStream fs = File.Create(outPath))
		{
			enc.Save(fs);
		}
		try
		{
			double tw = Math.Max(1.0, TrayPanel.ActualWidth);
			RenderTargetBitmap trb = new RenderTargetBitmap((int)Math.Ceiling(tw * scale), (int)(HeightPx * scale), 96.0 * scale, 96.0 * scale, PixelFormats.Pbgra32);
			DrawingVisual tdv = new DrawingVisual();
			using (DrawingContext dc2 = tdv.RenderOpen())
			{
				dc2.DrawRectangle(new SolidColorBrush(TaskbarTheme.Current), null, new Rect(0.0, 0.0, tw, HeightPx));
				dc2.DrawRectangle(new VisualBrush(TrayPanel), null, new Rect(0.0, 0.0, tw, HeightPx));
			}
			trb.Render(tdv);
			PngBitmapEncoder tenc = new PngBitmapEncoder();
			tenc.Frames.Add(BitmapFrame.Create(trb));
			string tpath = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(outPath), "traypanel.png");
			using FileStream tfs = File.Create(tpath);
			tenc.Save(tfs);
		}
		catch (Exception ex)
		{
			Logger.Log("tray-panel render failed: " + ex.Message);
		}
		// QA: render both Action-center positions so the reorder can be verified visually (the live shell picks the one
		// stored in settings via ApplyBarButtons). Restores the default ("Right") order afterwards.
		try
		{
			string dir = System.IO.Path.GetDirectoryName(outPath);
			foreach (string acPos in new[] { "Left", "Right" })
			{
				ApplyActionCenterPosition(acPos);
				root.Measure(new System.Windows.Size(2400.0, HeightPx));
				root.Arrange(new Rect(0.0, 0.0, 2400.0, HeightPx));
				root.UpdateLayout();
				RenderTargetBitmap acb = new RenderTargetBitmap((int)(2400.0 * scale), (int)(HeightPx * scale), 96.0 * scale, 96.0 * scale, PixelFormats.Pbgra32);
				DrawingVisual acdv = new DrawingVisual();
				using (DrawingContext dc3 = acdv.RenderOpen())
				{
					dc3.DrawRectangle(new SolidColorBrush(TaskbarTheme.Current), null, new Rect(0.0, 0.0, 2400.0, HeightPx));
					dc3.DrawRectangle(new VisualBrush(root), null, new Rect(0.0, 0.0, 2400.0, HeightPx));
				}
				acb.Render(acdv);
				PngBitmapEncoder acenc = new PngBitmapEncoder();
				acenc.Frames.Add(BitmapFrame.Create(acb));
				using FileStream acfs = File.Create(System.IO.Path.Combine(dir, "traytest_ac" + acPos.ToLowerInvariant() + ".png"));
				acenc.Save(acfs);
			}
		}
		catch (Exception ex)
		{
			Logger.Log("tray AC-position render failed: " + ex.Message);
		}
		Logger.Log("QaRenderTray -> " + outPath);
	}

	public void QaRenderFlyouts(string volumePath, string clockPath, string? networkPath = null)
	{
		_tray.VolumePct = 100;
		_tray.Muted = false;
		PopulateDevices();
		RenderDetached((FrameworkElement)VolumePopup.Child, _tray, volumePath);
		QaRenderClockCalendar(clockPath, DateTime.Now, 340.0, 500.0);
		if (networkPath != null)
		{
			ApplyNetworkFlyoutTheme();
			ApplyNetworkSnapshot(new NetworkFlyoutSnapshot(
				new NetState81(NetKind.Wifi, "HomeNetwork", 5, Internet: true),
				new NetInfo.LocalNet("Wi-Fi", "192.168.1.9", "192.168.1.1", "1.1.1.1", "Intel Wi-Fi 6E AX210", "866 Mbps"),
				"203.0.113.42",
				0L));
			RenderDetached((FrameworkElement)NetPopup.Child, null, networkPath);
		}
		Logger.Log("QaRenderFlyouts done");
	}

	private long QaWaitForVolumeScrollSettle(long timeoutMs = 400L)
	{
		Stopwatch stopwatch = Stopwatch.StartNew();
		DispatcherFrame frame = new DispatcherFrame();
		DispatcherTimer settleProbe = new DispatcherTimer(DispatcherPriority.Background)
		{
			Interval = TimeSpan.FromMilliseconds(6.0)
		};
		settleProbe.Tick += delegate
		{
			if (!SmoothScroll.IsVerticalActive(VolumeBodyScroll) || stopwatch.ElapsedMilliseconds >= timeoutMs)
			{
				frame.Continue = false;
			}
		};
		settleProbe.Start();
		Dispatcher.PushFrame(frame);
		settleProbe.Stop();
		stopwatch.Stop();
		return stopwatch.ElapsedMilliseconds;
	}

	private void QaSetVolumeScrollOffset(double offset)
	{
		SmoothScroll.StopVertical(VolumeBodyScroll);
		VolumeBodyScroll.ScrollToVerticalOffset(offset);
		VolumeBodyScroll.UpdateLayout();
		Dispatcher.Invoke(delegate { }, DispatcherPriority.Render);
	}

	private (bool Ready, bool Routed, bool Reached, long SettleMs) QaProbeGlobalVolumeWheel()
	{
		if (VolumeBodyScroll.ScrollableHeight <= 0.5)
		{
			return (false, false, false, 0L);
		}
		bool wasOpen = VolumePopup.IsOpen;
		_volumeDiagnosticsMode = true;
		try
		{
			VolumePopup.IsOpen = true;
			Dispatcher.Invoke(delegate { }, DispatcherPriority.Render);
			UpdateVolumeWheelScreenBounds();
			bool ready = Volatile.Read(ref _volumeWheelRoutingActive) == 1
				&& Volatile.Read(ref _volumeWheelRight) > Volatile.Read(ref _volumeWheelLeft)
				&& Volatile.Read(ref _volumeWheelBottom) > Volatile.Read(ref _volumeWheelTop);
			QaSetVolumeScrollOffset(0.0);
			int before = _volumeWheelRouteCount;
			int x = (Volatile.Read(ref _volumeWheelLeft) + Volatile.Read(ref _volumeWheelRight)) / 2;
			int y = (Volatile.Read(ref _volumeWheelTop) + Volatile.Read(ref _volumeWheelBottom)) / 2;
			bool captured = ready && TryRouteGlobalFlyoutMouseWheel(x, y, -120);
			Dispatcher.Invoke(delegate { }, DispatcherPriority.Input);
			bool routed = captured
				&& _volumeWheelRouteCount == before + 1
				&& SmoothScroll.IsVerticalActive(VolumeBodyScroll);
			long settleMs = QaWaitForVolumeScrollSettle();
			double target = Math.Min(Math.Clamp(VolumeBodyScroll.ViewportHeight * 0.2, 48.0, 72.0), VolumeBodyScroll.ScrollableHeight);
			bool reached = routed
				&& !SmoothScroll.IsVerticalActive(VolumeBodyScroll)
				&& Math.Abs(VolumeBodyScroll.VerticalOffset - target) <= 1.0;
			return (ready, routed, reached, settleMs);
		}
		finally
		{
			if (!wasOpen)
			{
				VolumePopup.IsOpen = false;
			}
			_volumeDiagnosticsMode = false;
			SmoothScroll.StopVertical(VolumeBodyScroll);
			QaSetVolumeScrollOffset(0.0);
		}
	}

	public SoundFlyoutQaMetrics QaRenderSoundFlyout(string screenshotPath, double widthDiu = 420.0, double heightDiu = 540.0, string? commandScreenshotPath = null)
	{
		StopVolumeTick();
		Stopwatch total = Stopwatch.StartNew();
		ApplySoundFlyoutTheme();
		_tray.VolumePct = 100;
		_tray.Muted = false;
		List<AudioDevice> outputs = new List<AudioDevice>
		{
			new AudioDevice { Id = "qa-output", Name = "RAZER AKOUSTIKA (Realtek(R) Audio)", IsDefault = true }
		};
		List<AudioDevice> inputs = new List<AudioDevice>
		{
			new AudioDevice { Id = "qa-input-default", Name = "Mic ακουστικων (Realtek(R) Audio)", IsDefault = true },
			new AudioDevice { Id = "qa-input-alternate", Name = "Microphone (Razer Seiren Mini)", IsDefault = false }
		};
		List<AudioSessionVm> sessions = new List<AudioSessionVm>
		{
			AudioSessionVm.Preview("System sounds", 73)
		};
		Stopwatch build = Stopwatch.StartNew();
		ApplyAudioSnapshot(new AudioFlyoutSnapshot(outputs, inputs, sessions, 0L));
		build.Stop();
		ConfigureVolumeFlyoutSize(widthDiu, heightDiu);
		VolumeRefreshStatus.Visibility = Visibility.Collapsed;
		VolumeBodyScroll.ScrollToTop();
		VolumeFlyoutRoot.BeginAnimation(UIElement.OpacityProperty, null);
		VolumeFlyoutSlide.BeginAnimation(TranslateTransform.XProperty, null);
		VolumeFlyoutSlide.BeginAnimation(TranslateTransform.YProperty, null);
		VolumeFlyoutRoot.Opacity = 1.0;
		VolumeFlyoutSlide.X = 0.0;
		VolumeFlyoutSlide.Y = 0.0;
		VolumeFlyoutRoot.CacheMode = null;
		RenderDetached((FrameworkElement)VolumePopup.Child, _tray, screenshotPath);
		if (!string.IsNullOrWhiteSpace(commandScreenshotPath))
		{
			RenderDetached((FrameworkElement)SoundCommandPopup.Child, null, commandScreenshotPath);
		}
		total.Stop();
		bool smoothWheelCyclePassed = false;
		bool smoothWheelReachedTarget = false;
		bool wheelOverContentRouted = false;
		bool wheelOverHeaderRouted = false;
		bool wheelOverFooterRouted = false;
		bool wheelOverScrollBarRouted = false;
		bool handledWheelOverScrollBarRouted = false;
		bool rapidWheelReversalPassed = false;
		long smoothWheelSettleMs = 0L;
		if (VolumeBodyScroll.ScrollableHeight > 0.5)
		{
			double wheelStep = Math.Clamp(VolumeBodyScroll.ViewportHeight * 0.2, 48.0, 72.0);
			double targetOffset = Math.Min(wheelStep, VolumeBodyScroll.ScrollableHeight);
			(bool Routed, bool Reached, long SettleMs) ProbeWheel(UIElement source, bool alreadyHandled = false)
			{
				QaSetVolumeScrollOffset(0.0);
				int before = _volumeWheelRouteCount;
				MouseWheelEventArgs wheel = new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, -120)
				{
					RoutedEvent = Mouse.PreviewMouseWheelEvent,
					Source = source,
					Handled = alreadyHandled
				};
				source.RaiseEvent(wheel);
				bool routed = wheel.Handled
					&& _volumeWheelRouteCount == before + 1
					&& SmoothScroll.IsVerticalActive(VolumeBodyScroll);
				long settle = QaWaitForVolumeScrollSettle();
				bool reached = !SmoothScroll.IsVerticalActive(VolumeBodyScroll)
					&& Math.Abs(VolumeBodyScroll.VerticalOffset - targetOffset) <= 1.0;
				return (routed, reached, settle);
			}

			var contentProbe = ProbeWheel(VolumeBodyPanel);
			wheelOverContentRouted = contentProbe.Routed;
			var headerProbe = ProbeWheel(MuteToggleGlyph);
			wheelOverHeaderRouted = headerProbe.Routed;
			var footerProbe = ProbeWheel(SoundActionGrid);
			wheelOverFooterRouted = footerProbe.Routed;

			VolumeBodyScroll.ApplyTemplate();
			System.Windows.Controls.Primitives.ScrollBar? verticalScrollBar = VolumeBodyScroll.Template.FindName("PART_VerticalScrollBar", VolumeBodyScroll) as System.Windows.Controls.Primitives.ScrollBar;
			long scrollBarSettleMs = 0L;
			bool scrollBarReached = false;
			if (verticalScrollBar != null)
			{
				var scrollBarProbe = ProbeWheel(verticalScrollBar, alreadyHandled: true);
				handledWheelOverScrollBarRouted = scrollBarProbe.Routed;
				wheelOverScrollBarRouted = scrollBarProbe.Routed;
				scrollBarSettleMs = scrollBarProbe.SettleMs;
				scrollBarReached = scrollBarProbe.Reached;
			}

			QaSetVolumeScrollOffset(targetOffset);
			int routeBefore = _volumeWheelRouteCount;
			MouseWheelEventArgs reverseDown = new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, -120)
			{
				RoutedEvent = Mouse.PreviewMouseWheelEvent,
				Source = VolumeBodyPanel
			};
			MouseWheelEventArgs reverseUp = new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, 120)
			{
				RoutedEvent = Mouse.PreviewMouseWheelEvent,
				Source = VolumeBodyPanel
			};
			VolumeBodyPanel.RaiseEvent(reverseDown);
			VolumeBodyPanel.RaiseEvent(reverseUp);
			bool reversalRouted = _volumeWheelRouteCount == routeBefore + 2
				&& SmoothScroll.IsVerticalActive(VolumeBodyScroll);
			long reversalSettleMs = QaWaitForVolumeScrollSettle();
			rapidWheelReversalPassed = reversalRouted
				&& !SmoothScroll.IsVerticalActive(VolumeBodyScroll)
				&& VolumeBodyScroll.VerticalOffset <= 1.0;

			smoothWheelSettleMs = new[] { contentProbe.SettleMs, headerProbe.SettleMs, footerProbe.SettleMs, scrollBarSettleMs, reversalSettleMs }.Max();
			smoothWheelReachedTarget = contentProbe.Reached && headerProbe.Reached && footerProbe.Reached && scrollBarReached;
			smoothWheelCyclePassed = wheelOverContentRouted
				&& wheelOverHeaderRouted
				&& wheelOverFooterRouted
				&& wheelOverScrollBarRouted
				&& rapidWheelReversalPassed
				&& !SmoothScroll.IsVerticalActive(VolumeBodyScroll);
			SmoothScroll.StopVertical(VolumeBodyScroll);
			QaSetVolumeScrollOffset(0.0);
		}
		(bool globalWheelReady, bool globalWheelRouted, bool globalWheelReached, long globalWheelSettleMs) = QaProbeGlobalVolumeWheel();
		double scrollBarHitWidth = 0.0;
		if (VolumeBodyScroll.Resources[typeof(System.Windows.Controls.Primitives.ScrollBar)] is Style scrollBarStyle)
		{
			Setter? widthSetter = scrollBarStyle.Setters.OfType<Setter>().FirstOrDefault(setter => setter.Property == FrameworkElement.WidthProperty);
			if (widthSetter?.Value != null)
			{
				scrollBarHitWidth = Convert.ToDouble(widthSetter.Value, CultureInfo.InvariantCulture);
			}
		}
		SolidColorBrush? defaultInputBrush = base.Resources["TB.SoundInputDefault"] as SolidColorBrush;
		SolidColorBrush? alternateInputBrush = base.Resources["TB.SoundInputOther"] as SolidColorBrush;
		System.Windows.Media.Color defaultInput = defaultInputBrush?.Color ?? Colors.Transparent;
		System.Windows.Media.Color alternateInput = alternateInputBrush?.Color ?? Colors.Transparent;
		LinearGradientBrush? themedBackground = base.Resources["TB.SoundBackground"] as LinearGradientBrush;
		System.Windows.Media.Color expectedThemeBottom = SoundTone(StartAccent.Color(), 0.0, 0.0, 0.14);
		double inputColorDistance = Math.Sqrt(
			Math.Pow((double)defaultInput.R - alternateInput.R, 2.0)
			+ Math.Pow((double)defaultInput.G - alternateInput.G, 2.0)
			+ Math.Pow((double)defaultInput.B - alternateInput.B, 2.0));
		System.Windows.Controls.Button[] actionButtons = SoundActionGrid.Children.OfType<System.Windows.Controls.Button>().ToArray();
		System.Windows.Controls.Button[] commandButtons = SoundCommandList.Children.OfType<System.Windows.Controls.Button>().ToArray();
		return new SoundFlyoutQaMetrics
		{
			WidthDiu = VolumeFlyoutRoot.Width,
			HeightDiu = VolumeFlyoutRoot.Height,
			OutputRows = OutputList.Children.Count,
			InputRows = InputList.Children.Count,
			AppSessionRows = AppVolumeHost.Items.Count,
			ActionTiles = SoundActionGrid.Children.Count,
			ActionIconsCentered = actionButtons.All(button =>
				button.Content is Grid content
					&& content.Children.OfType<Viewbox>().Any(iconBox => iconBox.Width == 30.0
						&& iconBox.Height == 30.0
						&& iconBox.HorizontalAlignment == System.Windows.HorizontalAlignment.Center
						&& iconBox.VerticalAlignment == VerticalAlignment.Center)),
			ActionIconsVector = actionButtons.All(button =>
				button.Content is Grid content
					&& content.Children.OfType<Viewbox>().Any(iconBox =>
						iconBox.Child is Canvas canvas
							&& canvas.Children.OfType<Shape>().Any()
							&& !canvas.Children.OfType<TextBlock>().Any())),
			DefaultOutputName = VolDeviceName.Text,
			MasterIconContainerWidthDiu = MuteToggle.ActualWidth,
			MasterIconGlyphWidthDiu = MuteToggleGlyph.ActualWidth,
			MasterIconSafeInsetDiu = Math.Max(0.0, (MuteToggle.ActualWidth - MuteToggleGlyph.ActualWidth) / 2.0),
			MasterIconHasSafeInsets = MuteToggle.ActualWidth >= 48.0
				&& MuteToggleGlyph.ActualWidth <= MuteToggle.ActualWidth - 8.0
				&& !MuteToggle.ClipToBounds
				&& !MuteToggleGlyph.ClipToBounds,
			MasterSliderMinimum = VolumeSlider.Minimum,
			MasterSliderMaximum = VolumeSlider.Maximum,
			BoundedScrollEnabled = VolumeBodyScroll.VerticalScrollBarVisibility == ScrollBarVisibility.Auto,
			PixelScrollingEnabled = !VolumeBodyScroll.CanContentScroll && SmoothScroll.VerticalSupported,
			SmoothWheelCyclePassed = smoothWheelCyclePassed,
			SmoothWheelReachedTarget = smoothWheelReachedTarget,
			WheelOverContentRouted = wheelOverContentRouted,
			WheelOverHeaderRouted = wheelOverHeaderRouted,
			WheelOverFooterRouted = wheelOverFooterRouted,
			WheelOverScrollBarRouted = wheelOverScrollBarRouted,
			HandledWheelOverScrollBarRouted = handledWheelOverScrollBarRouted,
			RapidWheelReversalPassed = rapidWheelReversalPassed,
			GlobalWheelCaptureReady = globalWheelReady,
			GlobalWheelRoutePassed = globalWheelRouted,
			GlobalWheelReachedTarget = globalWheelReached,
			GlobalWheelSettleMs = globalWheelSettleMs,
			SmoothWheelSettleMs = smoothWheelSettleMs,
			ScrollBarHitWidthDiu = scrollBarHitWidth,
			CustomPlacementEnabled = VolumePopup.Placement == PlacementMode.Custom && VolumePopup.CustomPopupPlacementCallback != null,
			SquareCorners = VolumeFlyoutRoot.CornerRadius == new CornerRadius(0.0),
			ThemeBackgroundSynchronized = themedBackground?.GradientStops.Count == 2
				&& themedBackground.GradientStops[1].Color == expectedThemeBottom
				&& defaultInputBrush != null
				&& alternateInputBrush != null,
			ThemeAccent = "#" + StartAccent.Color().ToString(CultureInfo.InvariantCulture).TrimStart('#'),
			DefaultInputColor = defaultInput.ToString(CultureInfo.InvariantCulture),
			AlternateInputColor = alternateInput.ToString(CultureInfo.InvariantCulture),
			InputStateColorDistance = inputColorDistance,
			VisualBuildMs = build.ElapsedMilliseconds,
			RenderAndLayoutMs = total.ElapsedMilliseconds,
			TimerActiveDuringDetachedRender = _volumeTick?.IsEnabled == true,
			RightClickCommands = commandButtons.Length,
			RightClickIconsVector = commandButtons.All(button =>
				button.Content is Grid content
					&& content.Children.OfType<Viewbox>().Any(iconBox =>
						iconBox.Child is Canvas canvas
							&& canvas.Children.OfType<Shape>().Any()
							&& !canvas.Children.OfType<TextBlock>().Any())),
			RightClickMetroSurface = SoundCommandRoot.Width == 306.0
				&& SoundCommandRoot.CornerRadius == new CornerRadius(0.0)
				&& SoundCommandRoot.Background != null,
			RightClickCustomPlacement = SoundCommandPopup.Placement == PlacementMode.Custom
				&& SoundCommandPopup.CustomPopupPlacementCallback != null,
			RightClickScreenshot = commandScreenshotPath ?? string.Empty,
			Screenshot = screenshotPath
		};
	}

	public NetworkFlyoutQaMetrics QaRenderNetworkFlyout(
		string screenshotPath,
		NetState81 state,
		NetInfo.LocalNet local,
		string? publicIp,
		double widthDiu = 420.0,
		double heightDiu = 500.0,
		string? commandScreenshotPath = null)
	{
		CancelNetworkRefresh();
		Stopwatch total = Stopwatch.StartNew();
		ApplyNetworkFlyoutTheme();
		Stopwatch build = Stopwatch.StartNew();
		ApplyNetworkSnapshot(new NetworkFlyoutSnapshot(state, local, publicIp, 0L));
		build.Stop();
		ConfigureNetworkFlyoutSize(widthDiu, heightDiu);
		NetRefreshStatus.Visibility = Visibility.Collapsed;
		NetworkBodyScroll.ScrollToTop();
		NetworkFlyoutRoot.BeginAnimation(UIElement.OpacityProperty, null);
		NetworkFlyoutSlide.BeginAnimation(TranslateTransform.XProperty, null);
		NetworkFlyoutSlide.BeginAnimation(TranslateTransform.YProperty, null);
		NetworkFlyoutRoot.Opacity = 1.0;
		NetworkFlyoutSlide.X = 0.0;
		NetworkFlyoutSlide.Y = 0.0;
		NetworkFlyoutRoot.CacheMode = null;
		RenderDetached((FrameworkElement)NetPopup.Child, null, screenshotPath);
		if (!string.IsNullOrWhiteSpace(commandScreenshotPath))
		{
			RenderDetached((FrameworkElement)NetworkCommandPopup.Child, null, commandScreenshotPath);
		}
		total.Stop();

		double scrollBarHitWidth = 0.0;
		if (NetworkBodyScroll.Resources[typeof(System.Windows.Controls.Primitives.ScrollBar)] is Style scrollBarStyle)
		{
			Setter? widthSetter = scrollBarStyle.Setters.OfType<Setter>().FirstOrDefault(setter => setter.Property == FrameworkElement.WidthProperty);
			if (widthSetter?.Value != null)
			{
				scrollBarHitWidth = Convert.ToDouble(widthSetter.Value, CultureInfo.InvariantCulture);
			}
		}
		LinearGradientBrush? themedBackground = base.Resources["TB.NetworkBackground"] as LinearGradientBrush;
		System.Windows.Media.Color expectedThemeBottom = SoundTone(StartAccent.Color(), 0.0, 0.0, 0.13);
		System.Windows.Controls.Button[] actionButtons = NetworkActionGrid.Children.OfType<System.Windows.Controls.Button>().ToArray();
		System.Windows.Controls.Button[] commandButtons = NetworkCommandList.Children.OfType<System.Windows.Controls.Button>().ToArray();
		ImageSource wifiTray = NetIcons81.For(up: true, wifi: true, 20, internet: true);
		ImageSource ethernetTray = NetIcons81.For(up: true, wifi: false, 20, internet: true);
		ImageSource limitedTray = NetIcons81.For(up: true, wifi: true, 20, internet: false);
		ImageSource offlineTray = NetIcons81.For(up: false, wifi: false, 20, internet: false);
		bool themeSwitchPassed = QaVerifyNetworkThemeSwitch();
		return new NetworkFlyoutQaMetrics
		{
			WidthDiu = NetworkFlyoutRoot.Width,
			HeightDiu = NetworkFlyoutRoot.Height,
			ActionTiles = NetworkActionGrid.Children.Count,
			ActionIconsCentered = actionButtons.All(button =>
				button.Content is Grid content
					&& content.Children.OfType<Viewbox>().Any(iconBox => iconBox.Width == 30.0
						&& iconBox.Height == 30.0
						&& iconBox.HorizontalAlignment == System.Windows.HorizontalAlignment.Center
						&& iconBox.VerticalAlignment == VerticalAlignment.Center)),
			ActionIconsVector = actionButtons.All(button =>
				button.Content is Grid content
					&& content.Children.OfType<Viewbox>().Any(iconBox =>
						iconBox.Child is Canvas canvas
							&& canvas.Children.OfType<Shape>().Any()
							&& !canvas.Children.OfType<TextBlock>().Any())),
			HeroIconsVector = NetHeroImage.Source is DrawingImage
				&& _networkHeroKind == state.Kind
				&& _networkHeroState == state.EffectiveIconState,
			TrayIconsVector = wifiTray is DrawingImage
				&& ethernetTray is DrawingImage
				&& limitedTray is DrawingImage
				&& offlineTray is DrawingImage,
			TrayStateIconsDistinct = !ReferenceEquals(wifiTray, ethernetTray)
				&& !ReferenceEquals(wifiTray, limitedTray)
				&& !ReferenceEquals(limitedTray, offlineTray),
			DetailRows = 7,
			Title = NetTitle.Text,
			ConnectionStatus = NetConnectionStatus.Text,
			PrimaryLabel = NetPrimaryLabel.Text,
			PrimaryValue = NetPrimaryValue.Text,
			StateBannerVisible = NetStateBanner.Visibility == Visibility.Visible,
			WifiHeroVisible = _networkHeroKind is NetKind.Wifi or NetKind.Cellular,
			EthernetHeroVisible = _networkHeroKind == NetKind.Ethernet,
			OfflineHeroVisible = _networkHeroKind == NetKind.Offline,
			AirplaneHeroVisible = _networkHeroKind == NetKind.Airplane,
			BoundedScrollEnabled = NetworkBodyScroll.VerticalScrollBarVisibility == ScrollBarVisibility.Auto,
			PixelScrollingEnabled = !NetworkBodyScroll.CanContentScroll && SmoothScroll.VerticalSupported,
			ScrollBarHitWidthDiu = scrollBarHitWidth,
			CustomPlacementEnabled = NetPopup.Placement == PlacementMode.Custom && NetPopup.CustomPopupPlacementCallback != null,
			SquareCorners = NetworkFlyoutRoot.CornerRadius == new CornerRadius(0.0),
			ThemeBackgroundSynchronized = themedBackground?.GradientStops.Count == 2
				&& themedBackground.GradientStops[1].Color == expectedThemeBottom,
			ThemeSwitchPassed = themeSwitchPassed,
			ThemeAccent = "#" + StartAccent.Color().ToString(CultureInfo.InvariantCulture).TrimStart('#'),
			VisualBuildMs = build.ElapsedMilliseconds,
			RenderAndLayoutMs = total.ElapsedMilliseconds,
			RefreshWorkerActiveDuringDetachedRender = _networkRefreshCts != null,
			RightClickCommands = commandButtons.Length,
			RightClickIconsVector = commandButtons.All(button =>
				button.Content is Grid content
					&& content.Children.OfType<Viewbox>().Any(iconBox =>
						iconBox.Child is Canvas canvas
							&& canvas.Children.OfType<Shape>().Any()
							&& !canvas.Children.OfType<TextBlock>().Any())),
			RightClickMetroSurface = NetworkCommandRoot.Width == 326.0
				&& NetworkCommandRoot.CornerRadius == new CornerRadius(0.0)
				&& NetworkCommandRoot.Background != null,
			RightClickCustomPlacement = NetworkCommandPopup.Placement == PlacementMode.Custom
				&& NetworkCommandPopup.CustomPopupPlacementCallback != null,
			RightClickScreenshot = commandScreenshotPath ?? string.Empty,
			Screenshot = screenshotPath
		};
	}

	private bool QaVerifyNetworkThemeSwitch()
	{
		try
		{
			System.Windows.Media.Color red = System.Windows.Media.Color.FromRgb(196, 32, 40);
			System.Windows.Media.Color blue = System.Windows.Media.Color.FromRgb(0, 114, 198);
			ApplyNetworkFlyoutPalette(red, red);
			System.Windows.Media.Color redBottom = ((LinearGradientBrush)base.Resources["TB.NetworkBackground"]).GradientStops[1].Color;
			ApplyNetworkFlyoutPalette(blue, blue);
			System.Windows.Media.Color blueBottom = ((LinearGradientBrush)base.Resources["TB.NetworkBackground"]).GradientStops[1].Color;
			return redBottom != blueBottom && redBottom.R > redBottom.B && blueBottom.B > blueBottom.R;
		}
		finally
		{
			ApplyNetworkFlyoutTheme();
		}
	}

	public ClockCalendarQaMetrics QaRenderClockCalendar(string clockPath, DateTime sample, double widthDiu = 340.0, double heightDiu = 500.0)
	{
		Stopwatch total = Stopwatch.StartNew();
		ApplyClockCalendarTheme();
		_calendarToday = sample.Date;
		_calendarSelection = sample.Date;
		_calMonth = new DateTime(sample.Year, sample.Month, 1);
		UpdateClockDisplay(sample);
		BuildMonth(_calMonth);
		ConfigureClockFlyoutSize(widthDiu, heightDiu);
		ClockFlyoutRoot.BeginAnimation(UIElement.OpacityProperty, null);
		ClockFlyoutSlide.BeginAnimation(TranslateTransform.XProperty, null);
		ClockFlyoutSlide.BeginAnimation(TranslateTransform.YProperty, null);
		ClockFlyoutRoot.Opacity = 1.0;
		ClockFlyoutSlide.X = 0.0;
		ClockFlyoutSlide.Y = 0.0;
		ClockFlyoutRoot.CacheMode = null;
		RenderDetached((FrameworkElement)ClockPopup.Child, null, clockPath);
		total.Stop();
		LinearGradientBrush? themedBackground = base.Resources["TB.ClockBody"] as LinearGradientBrush;
		System.Windows.Media.Color expectedThemeBottom = SoundTone(StartAccent.Color(), 0.0, 0.0, 0.12);
		bool themeSwitchPassed = QaVerifyClockCalendarThemeSwitch();
		return new ClockCalendarQaMetrics
		{
			WidthDiu = ClockFlyoutRoot.Width,
			HeightDiu = ClockFlyoutRoot.Height,
			CalendarColumns = MonthGrid.ColumnDefinitions.Count,
			CalendarRows = MonthGrid.RowDefinitions.Count,
			DayVisuals = _calendarDayButtons.Length,
			VisibleDays = _calendarDayButtons.Count(button => button.Visibility == Visibility.Visible),
			SelectedDate = _calendarSelection.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
			MonthTitle = MonthLabel.Text,
			ClockText = ClockBig.Text + ":" + ClockSeconds.Text,
			ThemeBackgroundSynchronized = themedBackground?.GradientStops.Count == 2
				&& themedBackground.GradientStops[1].Color == expectedThemeBottom,
			ThemeSwitchPassed = themeSwitchPassed,
			ThemeAccent = "#" + StartAccent.Color().ToString(CultureInfo.InvariantCulture).TrimStart('#'),
			InitialVisualBuildMs = _clockCalendarBuildMs,
			RenderAndLayoutMs = total.ElapsedMilliseconds,
			TimerActiveDuringDetachedRender = _clockTick?.IsEnabled == true,
			Screenshot = clockPath
		};
	}

	private bool QaVerifyClockCalendarThemeSwitch()
	{
		try
		{
			System.Windows.Media.Color red = System.Windows.Media.Color.FromRgb(196, 32, 40);
			System.Windows.Media.Color blue = System.Windows.Media.Color.FromRgb(0, 114, 198);
			ApplyClockCalendarPalette(red, red);
			System.Windows.Media.Color redBottom = ((LinearGradientBrush)base.Resources["TB.ClockBody"]).GradientStops[1].Color;
			System.Windows.Media.Color redHeader = ((SolidColorBrush)base.Resources["TB.ClockHeader"]).Color;
			ApplyClockCalendarPalette(blue, blue);
			System.Windows.Media.Color blueBottom = ((LinearGradientBrush)base.Resources["TB.ClockBody"]).GradientStops[1].Color;
			System.Windows.Media.Color blueHeader = ((SolidColorBrush)base.Resources["TB.ClockHeader"]).Color;
			return redBottom != blueBottom
				&& redHeader != blueHeader
				&& redBottom.R > redBottom.B
				&& blueBottom.B > blueBottom.R;
		}
		finally
		{
			ApplyClockCalendarTheme();
		}
	}

	private void RenderDetached(FrameworkElement el, object? dataContext, string outPath)
	{
		//IL_0026: Unknown result type (might be due to invalid IL or missing references)
		//IL_0033: Unknown result type (might be due to invalid IL or missing references)
		//IL_0038: Unknown result type (might be due to invalid IL or missing references)
		//IL_0076: Unknown result type (might be due to invalid IL or missing references)
		//IL_007b: Unknown result type (might be due to invalid IL or missing references)
		//IL_009e: Unknown result type (might be due to invalid IL or missing references)
		//IL_00a3: Unknown result type (might be due to invalid IL or missing references)
		//IL_00bc: Unknown result type (might be due to invalid IL or missing references)
		//IL_00c1: Unknown result type (might be due to invalid IL or missing references)
		if (dataContext != null)
		{
			el.DataContext = dataContext;
		}
		el.Measure(new System.Windows.Size(double.PositiveInfinity, double.PositiveInfinity));
		el.Arrange(new Rect(el.DesiredSize));
		el.UpdateLayout();
		PresentationSource src = PresentationSource.FromVisual(this);
		double? num;
		if (src == null)
		{
			num = null;
		}
		else
		{
			CompositionTarget compositionTarget = src.CompositionTarget;
			if (compositionTarget == null)
			{
				num = null;
			}
			else
			{
				Matrix transformToDevice = compositionTarget.TransformToDevice;
				num = transformToDevice.M11;
			}
		}
		double scale = num ?? 1.0;
		System.Windows.Size desiredSize = el.DesiredSize;
		int w = Math.Max(1, (int)Math.Ceiling(desiredSize.Width * scale));
		desiredSize = el.DesiredSize;
		int h = Math.Max(1, (int)Math.Ceiling(desiredSize.Height * scale));
		RenderTargetBitmap rtb = new RenderTargetBitmap(w, h, 96.0 * scale, 96.0 * scale, PixelFormats.Pbgra32);
		rtb.Render(el);
		PngBitmapEncoder enc = new PngBitmapEncoder();
		enc.Frames.Add(BitmapFrame.Create(rtb));
		using FileStream fs = File.Create(outPath);
		enc.Save(fs);
	}

	private void OnTaskbarBtnEnter(object sender, System.Windows.Input.MouseEventArgs e)
	{
		//IL_0028: Unknown result type (might be due to invalid IL or missing references)
		//IL_002d: Unknown result type (might be due to invalid IL or missing references)
		//IL_0044: Expected O, but got Unknown
		_hoverTarget = sender as FrameworkElement;
		DispatcherTimer? hoverHide = _hoverHide;
		if (hoverHide != null)
		{
			hoverHide.Stop();
		}
		if (_hoverShow == null)
		{
			_hoverShow = new DispatcherTimer
			{
				// Hover-INTENT debounce (stops previews flashing while sweeping across buttons). Was 280ms which read as
				// laggy; 110ms swallows quick sweeps yet lands the reveal near the §9 70-100ms budget.
				Interval = TimeSpan.FromMilliseconds(110L)
			};
		}
		_hoverShow.Tick -= HoverShowTick;
		_hoverShow.Tick += HoverShowTick;
		_hoverShow.Stop();
		_hoverShow.Start();
	}

	private void OnTaskbarBtnLeave(object sender, System.Windows.Input.MouseEventArgs e)
	{
		//IL_001c: Unknown result type (might be due to invalid IL or missing references)
		//IL_0021: Unknown result type (might be due to invalid IL or missing references)
		//IL_0038: Expected O, but got Unknown
		DispatcherTimer? hoverShow = _hoverShow;
		if (hoverShow != null)
		{
			hoverShow.Stop();
		}
		if (_hoverHide == null)
		{
			_hoverHide = new DispatcherTimer
			{
				Interval = TimeSpan.FromMilliseconds(250L)
			};
		}
		_hoverHide.Tick -= HoverHideTick;
		_hoverHide.Tick += HoverHideTick;
		_hoverHide.Stop();
		_hoverHide.Start();
	}

	private void HoverShowTick(object? s, EventArgs e)
	{
		//IL_00dd: Unknown result type (might be due to invalid IL or missing references)
		//IL_00e2: Unknown result type (might be due to invalid IL or missing references)
		//IL_00e7: Unknown result type (might be due to invalid IL or missing references)
		//IL_0104: Unknown result type (might be due to invalid IL or missing references)
		//IL_0109: Unknown result type (might be due to invalid IL or missing references)
		//IL_010e: Unknown result type (might be due to invalid IL or missing references)
		//IL_0112: Unknown result type (might be due to invalid IL or missing references)
		//IL_0113: Unknown result type (might be due to invalid IL or missing references)
		//IL_0149: Unknown result type (might be due to invalid IL or missing references)
		//IL_014e: Unknown result type (might be due to invalid IL or missing references)
		//IL_0197: Unknown result type (might be due to invalid IL or missing references)
		//IL_019c: Unknown result type (might be due to invalid IL or missing references)
		//IL_01f9: Unknown result type (might be due to invalid IL or missing references)
		DispatcherTimer? hoverShow = _hoverShow;
		if (hoverShow != null)
		{
			hoverShow.Stop();
		}
		if (_hoverTarget == null)
		{
			return;
		}
		var (hwnds, icon) = HwndsFor(_hoverTarget.DataContext);
		if (hwnds.Count == 0)
		{
			return;
		}
		List<(nint, ImageSource, string)> items = (from h in hwnds
			select (Hwnd: h, Icon: icon, Title: WindowList.GetTitle(h)) into x
			where !string.IsNullOrWhiteSpace(x.Title)
			select x).Take(6).ToList();
		if (items.Count == 0)
		{
			return;
		}
		System.Windows.Point tl = _hoverTarget.PointToScreen(new System.Windows.Point(0.0, 0.0));
		System.Windows.Point br = _hoverTarget.PointToScreen(new System.Windows.Point(_hoverTarget.ActualWidth, _hoverTarget.ActualHeight));
		Rect anchor = default(Rect);
		anchor = new Rect(tl, br);
		PresentationSource src = PresentationSource.FromVisual(this);
		double? num;
		Matrix transformToDevice;
		if (src == null)
		{
			num = null;
		}
		else
		{
			CompositionTarget compositionTarget = src.CompositionTarget;
			if (compositionTarget == null)
			{
				num = null;
			}
			else
			{
				transformToDevice = compositionTarget.TransformToDevice;
				num = transformToDevice.M11;
			}
		}
		double dx = num ?? 1.0;
		double? num2;
		if (src == null)
		{
			num2 = null;
		}
		else
		{
			CompositionTarget compositionTarget2 = src.CompositionTarget;
			if (compositionTarget2 == null)
			{
				num2 = null;
			}
			else
			{
				transformToDevice = compositionTarget2.TransformToDevice;
				num2 = transformToDevice.M22;
			}
		}
		double dy = num2 ?? 1.0;
		if (_preview == null)
		{
			_preview = new ThumbnailPreview();
			_preview.RequestHide += delegate
			{
				DispatcherTimer? hoverHide = _hoverHide;
				if (hoverHide != null)
				{
					hoverHide.Stop();
				}
				_preview?.Hide();
			};
		}
		_preview.ShowFor(items, anchor, dx, dy, _position);
	}

	private void HoverHideTick(object? s, EventArgs e)
	{
		DispatcherTimer? hoverHide = _hoverHide;
		if (hoverHide != null)
		{
			hoverHide.Stop();
		}
		ThumbnailPreview preview = _preview;
		if (preview == null || !preview.IsHovering)
		{
			_preview?.Hide();
		}
	}

	private static (List<nint>, ImageSource?) HwndsFor(object? dc)
	{
		if (1 == 0)
		{
		}
		(List<nint>, ImageSource) result;
		if (!(dc is TaskWindow tw))
		{
			result = ((!(dc is PinItem pi)) ? (new List<nint>(), null) : (pi.RunningHwnds.ToList(), pi.Icon));
		}
		else
		{
			List<nint> list;
			if (tw.Members.Count <= 0)
			{
				int num = 1;
				list = new List<nint>(num);
				CollectionsMarshal.SetCount(list, num);
				Span<nint> span = CollectionsMarshal.AsSpan(list);
				int index = 0;
				span[index] = tw.Hwnd;
			}
			else
			{
				list = tw.Members.ToList();
			}
			result = (list, tw.Icon);
		}
		if (1 == 0)
		{
		}
		return result;
	}

	private void OnTaskClick(object sender, RoutedEventArgs e)
	{
		if ((sender as FrameworkElement)?.DataContext is TaskWindow tw)
		{
			TaskOverflowPopup.IsOpen = false;
			nint rawForeground = GetForegroundWindow();
			nint fg = GetEffectiveForegroundWindow();
			bool targetIsForeground = tw.Hwnd == fg || tw.Members.Contains(fg);
			Logger.Log($"[taskbar-diag] task Click -> '{tw.Title}' rawFg=0x{rawForeground:X} effectiveFg=0x{fg:X} rep=0x{tw.Hwnd:X} members={tw.Members.Count} targetForeground={targetIsForeground}");
			if (targetIsForeground)
			{
				WindowList.Minimize(fg);
				_lastExternalForeground = IntPtr.Zero;
				tw.IsForeground = false;
				return;
			}
			tw.Members.RemoveAll(hwnd => !WindowList.IsWindowAlive(hwnd));
			foreach (nint hwnd in tw.Members)
			{
				if (WindowList.TryActivate(hwnd))
				{
					_lastExternalForeground = hwnd;
					return;
				}
			}
			if (WindowList.IsWindowAlive(tw.Hwnd))
			{
				if (WindowList.TryActivate(tw.Hwnd))
				{
					_lastExternalForeground = tw.Hwnd;
				}
			}
		}
	}

	private void OnTaskStripWheel(object sender, MouseWheelEventArgs e)
	{
		int n = _tasks.Count;
		if (n == 0)
		{
			return;
		}
		nint fg = GetEffectiveForegroundWindow();
		int cur = -1;
		for (int i = 0; i < n; i++)
		{
			if (_tasks[i].Members.Contains(fg) || _tasks[i].Hwnd == fg)
			{
				cur = i;
				break;
			}
		}
		int dir = ((e.Delta <= 0) ? 1 : (-1));
		int next = ((cur >= 0) ? (((cur + dir) % n + n) % n) : 0);
		TaskWindow t = _tasks[next];
		WindowList.Activate((t.Members.Count > 0) ? t.Members[0] : t.Hwnd);
		e.Handled = true;
	}

	private void OnTaskRightClick(object sender, MouseButtonEventArgs e)
	{
		object obj = (sender as FrameworkElement)?.DataContext;
		TaskWindow tw = obj as TaskWindow;
		if (tw == null)
		{
			return;
		}
		nint hwnd = tw.Hwnd;
		string exe = ExeFor(hwnd) ?? WindowList.GetExePath(hwnd);
		int pid = PidForWindow(hwnd);
		System.Windows.Controls.ContextMenu menu = new System.Windows.Controls.ContextMenu
		{
			Style = (Style)System.Windows.Application.Current.Resources["Win81ContextMenu"]
		};
		TaskbarContextMenu.ApplyTheme(menu);
		AppendJumpListForApp(menu, JumpListApi.GetAppId(exe) ?? AumidForWindow(hwnd));
		menu.Items.Add(TaskbarContextMenu.Leaf("Restore / Activate", 59559, delegate
		{
			WindowList.Activate(hwnd);
		}));
		menu.Items.Add(TaskbarContextMenu.Leaf("Minimize", 59681, delegate
		{
			WindowList.Minimize(hwnd);
		}));
		if (!string.IsNullOrEmpty(exe))
		{
			menu.Items.Add(TaskbarContextMenu.Sep());
			menu.Items.Add(TaskbarContextMenu.Leaf("Open file location", 57736, delegate
			{
				OpenFileLocation(exe);
			}));
			menu.Items.Add(TaskbarContextMenu.Leaf("Copy path", 59592, delegate
			{
				CopyToClipboard(exe);
			}));
			menu.Items.Add(TaskbarContextMenu.Leaf("Run as administrator", 57767, delegate
			{
				RunAsAdmin(exe);
			}));
			FileContextMenu.AppendExtras(menu, exe);
		}
		if (pid > 0)
		{
			menu.Items.Add(PrioritySub(hwnd));
		}
		if (!string.IsNullOrEmpty(exe) && !TaskbarPins.IsPinned(_pins, exe))
		{
			menu.Items.Add(TaskbarContextMenu.Sep());
			menu.Items.Add(TaskbarContextMenu.Leaf("Pin to taskbar", 57665, delegate
			{
				PinTaskWindow(hwnd);
			}));
		}
		menu.Items.Add(TaskbarContextMenu.Sep());
		if (tw.Members.Count > 1)
		{
			menu.Items.Add(TaskbarContextMenu.Leaf("Close all windows", 59579, delegate
			{
				foreach (nint current in tw.Members.ToList())
				{
					WindowList.Close(current);
				}
			}));
		}
		else
		{
			menu.Items.Add(TaskbarContextMenu.Leaf("Close", 59579, delegate
			{
				WindowList.Close(hwnd);
			}));
		}
		if (pid > 0)
		{
			menu.Items.Add(TaskbarContextMenu.Leaf("End task", 59162, delegate
			{
				EndTask(hwnd);
			}));
		}
		menu.PlacementTarget = (UIElement)sender;
		menu.Placement = FlyoutMode();
		OpenTrackedMenu(menu);
		e.Handled = true;
	}

	private static int PidForWindow(nint hwnd)
	{
		try
		{
			GetWindowThreadProcessId(hwnd, out var pid);
			return (int)pid;
		}
		catch
		{
			return 0;
		}
	}

	private static System.Windows.Controls.MenuItem PrioritySub(nint hwnd)
	{
		ProcessPriorityClass? cur = null;
		try
		{
			int p = PidForWindow(hwnd);
			if (p > 0)
			{
				cur = Process.GetProcessById(p).PriorityClass;
			}
		}
		catch
		{
		}
		return TaskbarContextMenu.Sub("Priority", 57832, P("High", ProcessPriorityClass.High), P("Above normal", ProcessPriorityClass.AboveNormal), P("Normal", ProcessPriorityClass.Normal), P("Below normal", ProcessPriorityClass.BelowNormal), P("Low", ProcessPriorityClass.Idle));
		System.Windows.Controls.MenuItem P(string label, ProcessPriorityClass cls)
		{
			return TaskbarContextMenu.Choice(label, cur == cls, delegate
			{
				SetPriority(hwnd, cls);
			});
		}
	}

	private static void OpenFileLocation(string exe)
	{
		ShellLaunch.Run(delegate
		{
			try
			{
				if (File.Exists(exe))
				{
					Process.Start(new ProcessStartInfo("explorer.exe", "/select,\"" + exe + "\"")
					{
						UseShellExecute = true
					});
				}
			}
			catch (Exception ex)
			{
				Logger.Log("Open file location: " + ex.Message);
			}
		});
	}

	private static void CopyToClipboard(string text)
	{
		try
		{
			System.Windows.Clipboard.SetText(text);
		}
		catch (Exception ex)
		{
			Logger.Log("Copy path: " + ex.Message);
		}
	}

	private static void RunAsAdmin(string exe)
	{
		ShellLaunch.Run(delegate
		{
			try
			{
				Process.Start(new ProcessStartInfo(exe)
				{
					UseShellExecute = true,
					Verb = "runas",
					WorkingDirectory = (System.IO.Path.GetDirectoryName(exe) ?? "")
				});
			}
			catch (Exception ex)
			{
				Logger.Log("Run as admin (or cancelled): " + ex.Message);
			}
		});
	}

	private static void SetPriority(nint hwnd, ProcessPriorityClass cls)
	{
		ShellLaunch.Run(delegate
		{
			try
			{
				int pid = PidForWindow(hwnd);
				if (pid > 0)
				{
					Process.GetProcessById(pid).PriorityClass = cls;
				}
			}
			catch (Exception ex)
			{
				Logger.Log("Set priority: " + ex.Message);
			}
		});
	}

	private static void EndTask(nint hwnd)
	{
		ShellLaunch.Run(delegate
		{
			try
			{
				int pid = PidForWindow(hwnd);
				if (pid > 0)
				{
					Process.GetProcessById(pid).Kill();
				}
			}
			catch (Exception ex)
			{
				Logger.Log("End task: " + ex.Message);
			}
		});
	}

	private void OnPinnedClick(object sender, RoutedEventArgs e)
	{
		if (_pinDidDrag)
		{
			_pinDidDrag = false;
			return;
		}
		FrameworkElement fe = sender as FrameworkElement;
		TaskOverflowPopup.IsOpen = false;
		if (fe?.DataContext is GroupTile group)
		{
			Logger.Log("[taskbar-diag] pinned Click -> group flyout");
			OpenGroupFlyout(group, fe);
		}
		else if (fe?.DataContext is PinnedTile tile)
		{
			Logger.Log($"[taskbar-diag] pinned Click -> '{tile.Name}' (running={tile.RunningHwnds.Count})");
			if (tile.RunningHwnds.Count == 0 && fe != null)
			{
				PulseLaunch(fe);
			}
			ActivateOrLaunch(tile);
		}
	}

	private static void PulseLaunch(FrameworkElement el)
	{
		//IL_004c: Unknown result type (might be due to invalid IL or missing references)
		ScaleTransform st = el.RenderTransform as ScaleTransform;
		if (st == null)
		{
			st = (ScaleTransform)(el.RenderTransform = new ScaleTransform(1.0, 1.0));
			el.RenderTransformOrigin = new System.Windows.Point(0.5, 0.5);
		}
		DoubleAnimationUsingKeyFrames anim = new DoubleAnimationUsingKeyFrames();
		anim.KeyFrames.Add(new EasingDoubleKeyFrame(0.8, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(70L))));
		anim.KeyFrames.Add(new EasingDoubleKeyFrame(1.0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(260L)))
		{
			EasingFunction = new BackEase
			{
				EasingMode = EasingMode.EaseOut,
				Amplitude = 0.5
			}
		});
		st.BeginAnimation(ScaleTransform.ScaleXProperty, anim);
		st.BeginAnimation(ScaleTransform.ScaleYProperty, anim);
	}

	private void ActivateOrLaunch(PinnedTile tile)
	{
		tile.RunningHwnds.RemoveAll(hwnd => !WindowList.IsWindowAlive(hwnd));
		if (tile.RunningHwnds.Count == 0)
		{
			TaskbarPins.Launch(tile);
			return;
		}
		nint fg = GetEffectiveForegroundWindow();
		if (tile.RunningHwnds.Contains(fg))
		{
			WindowList.Minimize(fg);
			_lastExternalForeground = IntPtr.Zero;
			tile.IsForeground = false;
			return;
		}
		foreach (nint target in tile.RunningHwnds.ToList())
		{
			if (WindowList.TryActivate(target))
			{
				_lastExternalForeground = target;
				return;
			}
			tile.RunningHwnds.Remove(target);
		}
		TaskbarPins.Launch(tile);
	}

	private void OnPinnedRightClick(object sender, MouseButtonEventArgs e)
	{
		FrameworkElement fe = sender as FrameworkElement;
		System.Windows.Controls.ContextMenu menu = new System.Windows.Controls.ContextMenu
		{
			Style = (Style)System.Windows.Application.Current.Resources["Win81ContextMenu"]
		};
		TaskbarContextMenu.ApplyTheme(menu);
		object obj = fe?.DataContext;
		GroupTile group = obj as GroupTile;
		if (group != null)
		{
			menu.Items.Add(TaskbarContextMenu.Leaf("Open group", 57736, delegate
			{
				OpenGroupFlyout(group, fe);
			}));
			menu.Items.Add(TaskbarContextMenu.Leaf("Rename group…", 59564, delegate
			{
				RenameGroup(group);
			}));
			menu.Items.Add(TaskbarContextMenu.Sep());
			menu.Items.Add(TaskbarContextMenu.Leaf("Ungroup", 57750, delegate
			{
				UngroupGroup(group);
			}));
		}
		else
		{
			obj = fe?.DataContext;
			PinnedTile tile = obj as PinnedTile;
			if (tile == null)
			{
				return;
			}
			AppendJumpList(menu, tile);
			menu.Items.Add(TaskbarContextMenu.Leaf("Open new window", 57724, delegate
			{
				TaskbarPins.Launch(tile);
			}));
			if (tile.RunningHwnds.Count > 0)
			{
				menu.Items.Add(TaskbarContextMenu.Leaf("Activate", 59559, delegate
				{
					WindowList.Activate(tile.RunningHwnds[0]);
				}));
				menu.Items.Add(TaskbarContextMenu.Leaf("Close", 59579, delegate
				{
					foreach (nint current in tile.RunningHwnds.ToList())
					{
						WindowList.Close(current);
					}
				}));
			}
			string exe = tile.ExePath;
			if (!string.IsNullOrEmpty(exe) && File.Exists(exe))
			{
				menu.Items.Add(TaskbarContextMenu.Sep());
				menu.Items.Add(TaskbarContextMenu.Leaf("Open file location", 57736, delegate
				{
					OpenFileLocation(exe);
				}));
				menu.Items.Add(TaskbarContextMenu.Leaf("Copy path", 59592, delegate
				{
					CopyToClipboard(exe);
				}));
				menu.Items.Add(TaskbarContextMenu.Leaf("Run as administrator", 57767, delegate
				{
					RunAsAdmin(exe);
				}));
				FileContextMenu.AppendExtras(menu, exe);
			}
			menu.Items.Add(TaskbarContextMenu.Sep());
			System.Windows.Controls.MenuItem groupMenu = TaskbarContextMenu.Sub("Add to group", TaskbarContextMenu.Leaf("New group…", delegate
			{
				AddToNewGroup(tile);
			}));
			foreach (string gname in _groups.Select((GroupTile g) => g.Name).Distinct<string>(StringComparer.OrdinalIgnoreCase).ToList())
			{
				string gn = gname;
				groupMenu.Items.Add(TaskbarContextMenu.Leaf(gn, delegate
				{
					SetPinGroup(tile, gn);
				}));
			}
			menu.Items.Add(groupMenu);
			menu.Items.Add(TaskbarContextMenu.Leaf("Unpin from taskbar", 57750, delegate
			{
				Unpin(tile);
			}));
		}
		menu.PlacementTarget = (UIElement)sender;
		menu.Placement = FlyoutMode();
		OpenTrackedMenu(menu);
		e.Handled = true;
	}

	private void AppendJumpList(System.Windows.Controls.ContextMenu menu, PinnedTile tile)
	{
		AppendJumpListForApp(menu, tile.Aumid ?? JumpListApi.GetAppId(tile.LaunchPath) ?? JumpListApi.GetAppId(tile.ExePath));
	}

	private static void AppendJumpListForApp(System.Windows.Controls.ContextMenu menu, string? appId)
	{
		if (string.IsNullOrEmpty(appId))
		{
			return;
		}
		int insertAt = menu.Items.Count;
		if (JumpListApi.TryGetRecentCached(appId, 8, out List<JumpList.Item> cached))
		{
			InsertJumpListItems(menu, cached, insertAt);
			return;
		}

		JumpListApi.GetRecentAsync(appId, 8, delegate(List<JumpList.Item> recent)
		{
			Dispatcher dispatcher = menu.Dispatcher;
			if (dispatcher.HasShutdownStarted)
			{
				return;
			}
			dispatcher.BeginInvoke((Action)delegate
			{
				if (menu.IsOpen)
				{
					InsertJumpListItems(menu, recent, Math.Min(insertAt, menu.Items.Count));
				}
			}, DispatcherPriority.Background);
		});
	}

	private static void InsertJumpListItems(System.Windows.Controls.ContextMenu menu, List<JumpList.Item> recent, int insertAt)
	{
		if (recent.Count == 0)
		{
			return;
		}
		menu.Items.Insert(insertAt++, TaskbarContextMenu.Leaf("Recent", delegate { }, enabled: false));
		foreach (JumpList.Item item in recent)
		{
			string path = item.Path;
			menu.Items.Insert(insertAt++, TaskbarContextMenu.Leaf(item.Name, delegate { OpenRecentFile(path); }));
		}
		menu.Items.Insert(insertAt, TaskbarContextMenu.Sep());
	}

	private static void OpenRecentFile(string path)
	{
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

	private PinnedApp? FindPin(PinnedTile t)
	{
		return _pins.FirstOrDefault((PinnedApp x) => SamePin(x, t));
	}

	private void SetPinGroup(PinnedTile tile, string groupName)
	{
		PinnedApp p = FindPin(tile);
		if (p != null)
		{
			p.GroupName = groupName;
			TaskbarPins.Save(_pins);
			PinsChanged?.Invoke();
		}
	}

	private void AddToNewGroup(PinnedTile tile)
	{
		SetPinGroup(tile, NextGroupName());
	}

	private void RenameGroup(GroupTile group)
	{
		string name = PromptForText("Rename group", "Group name:", group.Name);
		if (string.IsNullOrWhiteSpace(name) || name.Trim() == group.Name)
		{
			return;
		}
		foreach (PinnedTile m in group.Members)
		{
			PinnedApp p = FindPin(m);
			if (p != null)
			{
				p.GroupName = name.Trim();
			}
		}
		TaskbarPins.Save(_pins);
		PinsChanged?.Invoke();
	}

	private void UngroupGroup(GroupTile group)
	{
		foreach (PinnedTile m in group.Members)
		{
			PinnedApp p = FindPin(m);
			if (p != null)
			{
				p.GroupName = "";
			}
		}
		TaskbarPins.Save(_pins);
		PinsChanged?.Invoke();
	}

	private void RemoveFromGroup(PinnedTile tile)
	{
		PinnedApp p = FindPin(tile);
		if (p != null)
		{
			p.GroupName = "";
			TaskbarPins.Save(_pins);
			PinsChanged?.Invoke();
		}
	}

	private void OpenGroupFlyout(GroupTile group, FrameworkElement anchor)
	{
		Popup popup = new Popup
		{
			PlacementTarget = anchor,
			Placement = FlyoutMode(),
			StaysOpen = false,
			AllowsTransparency = true,
			PopupAnimation = PopupAnimation.Fade
		};
		popup.Child = BuildGroupFlyout(group, delegate
		{
			popup.IsOpen = false;
		});
		popup.IsOpen = true;
	}

	private Border BuildGroupFlyout(GroupTile group, Action? onClose)
	{
		StackPanel panel = new StackPanel
		{
			MinWidth = 190.0
		};
		panel.Children.Add(new TextBlock
		{
			Text = group.Name,
			Foreground = System.Windows.Media.Brushes.White,
			FontFamily = new System.Windows.Media.FontFamily("Segoe UI"),
			FontSize = 14.0,
			FontWeight = FontWeights.SemiBold,
			Margin = new Thickness(2.0, 0.0, 0.0, 8.0)
		});
		foreach (PinnedTile m in group.Members)
		{
			StackPanel row = new StackPanel
			{
				Orientation = System.Windows.Controls.Orientation.Horizontal
			};
			System.Windows.Controls.Image jlImg = new System.Windows.Controls.Image
			{
				Source = m.Icon,
				Width = 22.0,
				Height = 22.0,
				Margin = new Thickness(0.0, 0.0, 10.0, 0.0)
			};
			RenderOptions.SetBitmapScalingMode(jlImg, BitmapScalingMode.HighQuality);
			row.Children.Add(jlImg);
			row.Children.Add(new TextBlock
			{
				Text = m.Name,
				Foreground = System.Windows.Media.Brushes.White,
				VerticalAlignment = VerticalAlignment.Center,
				FontFamily = new System.Windows.Media.FontFamily("Segoe UI"),
				FontSize = 12.0
			});
			System.Windows.Controls.Button btn = new System.Windows.Controls.Button
			{
				Content = row,
				Style = (Style)base.Resources["DeviceRow"]
			};
			PinnedTile member = m;
			btn.Click += delegate
			{
				onClose?.Invoke();
				ActivateOrLaunch(member);
			};
			btn.MouseRightButtonUp += delegate(object _, MouseButtonEventArgs ev)
			{
				onClose?.Invoke();
				RemoveFromGroup(member);
				ev.Handled = true;
			};
			panel.Children.Add(btn);
		}
		return new Border
		{
			Background = ShellSkin.PanelBg(),   // flat opaque accent / Win7-Aero translucent glass
			BorderBrush = new SolidColorBrush(System.Windows.Media.Color.FromArgb(64, byte.MaxValue, byte.MaxValue, byte.MaxValue)),
			BorderThickness = new Thickness(1.0),
			CornerRadius = new CornerRadius(2.0),
			Padding = new Thickness(12.0),
			Child = panel
		};
	}

	public void QaRenderGroupFlyout(string path)
	{
		GroupTile g = _groups.FirstOrDefault();
		if (g == null)
		{
			Logger.Log("QaRenderGroupFlyout: no group");
			return;
		}
		RenderDetached(BuildGroupFlyout(g, null), null, path);
		Logger.Log("QaRenderGroupFlyout (" + g.Name + ") -> " + path);
	}

	private static string? PromptForText(string title, string label, string initial)
	{
		using Form form = new Form
		{
			Text = title,
			FormBorderStyle = FormBorderStyle.FixedDialog,
			StartPosition = FormStartPosition.CenterScreen,
			MinimizeBox = false,
			MaximizeBox = false,
			TopMost = true,
			ClientSize = new System.Drawing.Size(320, 110)
		};
		System.Windows.Forms.Label lbl = new System.Windows.Forms.Label
		{
			Text = label,
			Left = 12,
			Top = 12,
			Width = 296
		};
		System.Windows.Forms.TextBox box = new System.Windows.Forms.TextBox
		{
			Text = initial,
			Left = 12,
			Top = 36,
			Width = 296
		};
		System.Windows.Forms.Button ok = new System.Windows.Forms.Button
		{
			Text = "OK",
			DialogResult = System.Windows.Forms.DialogResult.OK,
			Left = 152,
			Top = 70,
			Width = 74
		};
		System.Windows.Forms.Button cancel = new System.Windows.Forms.Button
		{
			Text = "Cancel",
			DialogResult = System.Windows.Forms.DialogResult.Cancel,
			Left = 234,
			Top = 70,
			Width = 74
		};
		form.Controls.AddRange(lbl, box, ok, cancel);
		form.AcceptButton = ok;
		form.CancelButton = cancel;
		box.SelectAll();
		return (form.ShowDialog() == System.Windows.Forms.DialogResult.OK) ? box.Text : null;
	}

	private void OnPinnedDragStart(object sender, MouseButtonEventArgs e)
	{
		//IL_0021: Unknown result type (might be due to invalid IL or missing references)
		//IL_0026: Unknown result type (might be due to invalid IL or missing references)
		_pinDidDrag = false;
		if (SettingsStore.Load().TaskbarLocked)
		{
			_pinDragItem = null;
			return;
		}
		_pinDragStart = e.GetPosition(PinnedHost);
		_pinDragItem = (sender as FrameworkElement)?.DataContext as PinItem;
		_pinDragButton = sender as System.Windows.Controls.Button;
		_pinDragEl = ((_pinDragItem != null) ? (PinnedHost.ItemContainerGenerator.ContainerFromItem(_pinDragItem) as FrameworkElement) : null);
		// Freeze the start slot in VISIBLE space (the collection the host actually lays out). Everything during the drag
		// is computed against this fixed index, so the dragged tile tracks the cursor 1:1 with no baseline lurch.
		_pinDragFromVisible = ((_pinDragItem != null) ? _visiblePinItems.IndexOf(_pinDragItem) : -1);
		_pinDragGapIndex = _pinDragFromVisible;
		_pinDragging = false;
	}

	private void OnPinnedDragMove(object sender, System.Windows.Input.MouseEventArgs e)
	{
		//IL_002a: Unknown result type (might be due to invalid IL or missing references)
		//IL_002f: Unknown result type (might be due to invalid IL or missing references)
		//IL_00fa: Unknown result type (might be due to invalid IL or missing references)
		if (_pinDragItem == null || e.LeftButton != MouseButtonState.Pressed)
		{
			return;
		}
		System.Windows.Point pos = e.GetPosition(PinnedHost);
		if (!_pinDragging)
		{
			if (Math.Abs(pos.X - _pinDragStart.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(pos.Y - _pinDragStart.Y) < SystemParameters.MinimumVerticalDragDistance)
			{
				return;
			}
			_pinDragging = true;
			_pinDidDrag = true;
			_pinDragButton?.CaptureMouse();
			// Grab offset measured against the FIXED start slot (visible space), so the tile never snaps to centre.
			_pinDragGrabOffset = Math.Clamp(_pinDragStart.X - (double)_pinDragFromVisible * PinSlot, 0.0, PinSlot);
			if (_pinDragEl != null)
			{
				System.Windows.Controls.Panel.SetZIndex(_pinDragEl, 100);
				_pinDragEl.Opacity = 0.85;
				_pinDragEl.RenderTransformOrigin = new System.Windows.Point(0.5, 0.5);
				AnimatedBar.DragExempt = _pinDragEl;
			}
		}
		// Compositor-only from here: mutate NO collection until drop. `from` is the fixed start slot, `over` the live
		// target slot, both in _visiblePinItems space (the collection the host actually lays out).
		int from = _pinDragFromVisible;
		int count = _visiblePinItems.Count;
		if (from < 0 || count <= 0)
		{
			e.Handled = true;
			return;
		}
		// Target slot from the dragged icon's CENTRE (which tracks the grab point). Hysteresis: a dead-zone around the
		// CURRENT gap so sub-pixel jitter at a boundary can't flip the target and twitch the neighbours. Clamp to the
		// VISIBLE range (overflow pins aren't draggable, so we never target a slot with no container).
		double draggedCenter = pos.X - _pinDragGrabOffset + PinSlot / 2.0;
		int cur = (_pinDragGapIndex >= 0) ? _pinDragGapIndex : from;
		int over = cur;
		double lo = (double)cur * PinSlot - 16.0;
		double hi = (double)(cur + 1) * PinSlot + 16.0;
		if (draggedCenter < lo || draggedCenter > hi)
		{
			over = Math.Clamp((int)(draggedCenter / PinSlot), 0, count - 1);
		}
		if (_pinDragItem is PinnedTile && pos.Y < -12.0 && over != from)
		{
			// Grouping gesture (tile lifted above the bar onto a neighbour): close the reorder gap, then highlight.
			if (_pinDragGapIndex != from)
			{
				ApplyGap(from, from);
				_pinDragGapIndex = from;
			}
			SetGroupHilite(over);
		}
		else
		{
			ClearGroupHilite();
			if (over != _pinDragGapIndex)
			{
				// Slide neighbours to open the gap at `over` — pure RenderTransform, no layout, no collection change.
				ApplyGap(from, over);
				_pinDragGapIndex = over;
			}
		}
		if (_pinDragEl != null)
		{
			// Dragged tile follows the cursor 1:1 against its FIXED start slot — no reorder, so no baseline drift.
			double naturalLeft = (double)from * PinSlot;
			TranslateTransform tt = _pinDragEl.RenderTransform as TranslateTransform;
			if (tt == null)
			{
				tt = new TranslateTransform();
				_pinDragEl.RenderTransform = tt;
			}
			tt.BeginAnimation(TranslateTransform.XProperty, null);
			tt.X = pos.X - naturalLeft - _pinDragGrabOffset;
		}
		e.Handled = true;
	}

	// Slide every visible neighbour to open a gap at `target` for a tile dragged from `from`. Pure RenderTransform —
	// no add/remove/move on the bound collection, so AnimatedBar.ArrangeOverride never runs and cannot fight these
	// transforms. Each neighbour's post-drop slot is computed as if the dragged tile were pulled out of `from` and
	// reinserted at `target`; we animate it there. Runs only when the gap target actually changes.
	private void ApplyGap(int from, int target)
	{
		int n = _visiblePinItems.Count;
		for (int k = 0; k < n; k++)
		{
			if (k == from)
			{
				continue;
			}
			if (!(PinnedHost.ItemContainerGenerator.ContainerFromIndex(k) is FrameworkElement c) || c == _pinDragEl)
			{
				continue;
			}
			int eff = (k < from) ? k : (k - 1);          // index once the dragged tile is pulled out of `from`
			int slot = eff + ((eff >= target) ? 1 : 0);  // index once it is reinserted at `target`
			AnimateTx(c, (double)(slot - k) * PinSlot);
		}
	}

	private static void AnimateTx(FrameworkElement fe, double targetX)
	{
		TranslateTransform tt = fe.RenderTransform as TranslateTransform;
		if (tt == null)
		{
			tt = new TranslateTransform();
			fe.RenderTransform = tt;
		}
		if (Math.Abs(tt.X - targetX) < 0.5)
		{
			return;
		}
		// Animate from the CURRENT offset (even mid-flight) to the new one, so rapid gap changes never snap.
		tt.BeginAnimation(TranslateTransform.XProperty, Motion.GlideTo(tt.X, targetX));
	}

	private void SetGroupHilite(int idx)
	{
		//IL_0058: Unknown result type (might be due to invalid IL or missing references)
		if (_groupTargetIdx != idx)
		{
			ClearGroupHilite();
			_groupTargetIdx = idx;
			if (PinnedHost.ItemContainerGenerator.ContainerFromIndex(idx) is FrameworkElement el)
			{
				_groupHiliteEl = el;
				el.RenderTransformOrigin = new System.Windows.Point(0.5, 0.5);
				// Grow-in on a compositor ScaleTransform (was an instant 1.25x pop that jarred beside the gliding neighbours).
				ScaleTransform st = new ScaleTransform(1.0, 1.0);
				el.RenderTransform = st;
				st.BeginAnimation(ScaleTransform.ScaleXProperty, Motion.To(1.25, Motion.Cat.Hover));
				st.BeginAnimation(ScaleTransform.ScaleYProperty, Motion.To(1.25, Motion.Cat.Hover));
			}
		}
	}

	private void ClearGroupHilite()
	{
		if (_groupHiliteEl != null)
		{
			FrameworkElement el = _groupHiliteEl;
			if (el.RenderTransform is ScaleTransform st)
			{
				// Ease back to 1.0, then drop the transform once settled (mirror of the grow-in).
				DoubleAnimation back = Motion.To(1.0, Motion.Cat.Hover);
				back.Completed += delegate
				{
					if (el.RenderTransform == st)
					{
						el.RenderTransform = null;
					}
				};
				st.BeginAnimation(ScaleTransform.ScaleXProperty, back);
				st.BeginAnimation(ScaleTransform.ScaleYProperty, Motion.To(1.0, Motion.Cat.Hover));
			}
			else
			{
				el.RenderTransform = null;
			}
			_groupHiliteEl = null;
		}
		_groupTargetIdx = -1;
	}

	private void OnPinnedDragEnd(object sender, MouseButtonEventArgs e)
	{
		if (_pinDragging)
		{
			_pinDragging = false;
			AnimatedBar.DragExempt = null;
			int groupIdx = _groupTargetIdx;
			int from = _pinDragFromVisible;
			int to = _pinDragGapIndex;
			PinItem dragged = _pinDragItem;
			FrameworkElement el = _pinDragEl;
			_pinDragButton?.ReleaseMouseCapture();
			if (el != null)
			{
				// Keep the tile LIFTED (ZIndex) and clear opacity now, but do NOT null its transform — nulling it here
				// is what used to snap it back to its slot. It eases home via the animations kicked off below.
				el.Opacity = 1.0;
			}
			ClearGroupHilite();
			bool rebuilt = false;
			if (groupIdx >= 0 && dragged is PinnedTile dt)
			{
				// Grouping gesture. On a real merge GroupOnDrop fires PinsChanged -> RebuildPins regenerates every
				// container (which clears our transforms). On a no-op fallback it returns false and we settle the tile.
				rebuilt = GroupOnDrop(dt, groupIdx);
				if (!rebuilt && el?.RenderTransform is TranslateTransform ttg)
				{
					ttg.BeginAnimation(TranslateTransform.XProperty, Motion.GlideTo(ttg.X, 0.0));
				}
			}
			else if (from >= 0 && to >= 0 && to != from)
			{
				// Commit the reorder ONCE, in both collections (they remain a consistent in-order prefix). The Move
				// relocates containers and invalidates arrange; AnimatedBar's continuity Glide (from = dx + tt.X) then
				// eases the dragged tile from the cursor to slot `to` and holds each neighbour exactly where the gap
				// already placed it — zero snap. Persist WITHOUT firing PinsChanged (that would rebuild + flash).
				_pinItems.Move(from, to);
				_visiblePinItems.Move(from, to);
				PersistPinOrder();
			}
			else
			{
				// Dropped back where it started: no reorder. Ease the lifted tile home from wherever the cursor left it.
				if (el?.RenderTransform is TranslateTransform tt)
				{
					tt.BeginAnimation(TranslateTransform.XProperty, Motion.GlideTo(tt.X, 0.0));
				}
				PersistPinOrder();
			}
			if (!rebuilt)
			{
				ScheduleDragLiftReset(el);
			}
			if (_reflowPending)
			{
				// A tray/task/size reflow was deferred during the drag — run it now that the strip has settled.
				_reflowPending = false;
				ReflowAppStrip();
			}
			e.Handled = true;
		}
		_pinDragItem = null;
		_pinDragButton = null;
		_pinDragEl = null;
		_pinDragFromVisible = -1;
		_pinDragGapIndex = -1;
	}

	// Drop the just-dragged tile back to ZIndex 0 AFTER its settle animation lands (so it stays visually on top until
	// it reaches its slot). Token-guarded so a rapid re-grab of the same tile can't be yanked down mid-drag.
	private void ScheduleDragLiftReset(FrameworkElement el)
	{
		if (el == null)
		{
			return;
		}
		int token = ++_pinDragSettleToken;
		System.Windows.Threading.DispatcherTimer timer = new System.Windows.Threading.DispatcherTimer
		{
			Interval = Motion.Time(Motion.Cat.Reposition) + TimeSpan.FromMilliseconds(40.0)
		};
		timer.Tick += delegate
		{
			timer.Stop();
			if (token != _pinDragSettleToken || _pinDragging)
			{
				return;
			}
			System.Windows.Controls.Panel.SetZIndex(el, 0);
		};
		timer.Start();
	}

	// Returns true if it changed grouping structure (fired PinsChanged -> a full RebuildPins). Returns false on a no-op
	// fallback (dropped on itself / invalid target), in which case the caller settles the tile and no rebuild happens.
	private bool GroupOnDrop(PinnedTile dragged, int targetIdx)
	{
		try
		{
			// targetIdx (from SetGroupHilite) is a VISIBLE-space index, so resolve it against _visiblePinItems.
			if (targetIdx < 0 || targetIdx >= _visiblePinItems.Count)
			{
				PersistPinOrder();
				return false;
			}
			PinItem target = _visiblePinItems[targetIdx];
			if (target == dragged)
			{
				PersistPinOrder();
				return false;
			}
			if (target is GroupTile g)
			{
				SetPinGroup(dragged, g.Name);   // fires PinsChanged -> rebuild
				return true;
			}
			if (target is PinnedTile t)
			{
				string name = NextGroupName();
				PinnedApp pd = FindPin(dragged);
				PinnedApp pt = FindPin(t);
				if (pd != null && pt != null)
				{
					pt.GroupName = name;
					pd.GroupName = name;
					TaskbarPins.Save(_pins);
					PinsChanged?.Invoke();   // rebuild
					return true;
				}
				PersistPinOrder();
				return false;
			}
			PersistPinOrder();
			return false;
		}
		catch (Exception ex)
		{
			Logger.Log("Group-on-drop failed: " + ex.Message);
			return false;
		}
	}

	private string NextGroupName()
	{
		HashSet<string> existing = new HashSet<string>(_groups.Select((GroupTile g) => g.Name), StringComparer.OrdinalIgnoreCase);
		if (!existing.Contains("Group"))
		{
			return "Group";
		}
		int i = 2;
		while (existing.Contains($"Group {i}"))
		{
			i++;
		}
		return $"Group {i}";
	}

	// Persist the current _pinItems order to disk. Deliberately does NOT fire PinsChanged: the drop path has already
	// reordered _pinItems + _visiblePinItems in place (containers intact, settle animation running), so firing
	// PinsChanged -> RebuildPins would Clear+re-add fresh tiles and hard-snap the strip we just smoothly settled.
	private void PersistPinOrder()
	{
		List<PinnedApp> reordered = new List<PinnedApp>();
		foreach (PinItem item in _pinItems)
		{
			if (item is GroupTile g)
			{
				foreach (PinnedTile m in g.Members)
				{
					PinnedApp pg = _pins.FirstOrDefault((PinnedApp x) => SamePin(x, m));
					if (pg != null)
					{
						reordered.Add(pg);
					}
				}
				continue;
			}
			PinnedTile t = item as PinnedTile;
			if (t != null)
			{
				PinnedApp p = _pins.FirstOrDefault((PinnedApp x) => SamePin(x, t));
				if (p != null)
				{
					reordered.Add(p);
				}
			}
		}
		if (reordered.Count == _pins.Count)
		{
			_pins = reordered;
			TaskbarPins.Save(_pins);
		}
	}

	// Two pins are the same app only when the launch target, its arguments AND any AUMID all match.
	private static bool SamePinIdentity(PinnedApp a, PinnedApp b)
	{
		if (a == null || b == null)
		{
			return false;
		}
		string ka = a.ExePath ?? a.LaunchPath;
		string kb = b.ExePath ?? b.LaunchPath;
		return string.Equals(ka, kb, StringComparison.OrdinalIgnoreCase)
			&& string.Equals(a.Args ?? "", b.Args ?? "", StringComparison.OrdinalIgnoreCase)
			&& string.Equals(a.Aumid ?? "", b.Aumid ?? "", StringComparison.OrdinalIgnoreCase);
	}

	private static bool SamePin(PinnedApp p, PinnedTile t)
	{
		return string.Equals(p.ExePath, t.ExePath, StringComparison.OrdinalIgnoreCase) && string.Equals(p.LaunchPath, t.LaunchPath, StringComparison.OrdinalIgnoreCase) && (p.Args ?? "") == (t.Args ?? "");
	}

	private void PinTaskWindow(nint hwnd)
	{
		// Shared-host windows must be pinned as their own shell app keyed by AUMID, not as the host exe:
		// explorer.exe hosts Control Panel/This PC, and ApplicationFrameHost.exe hosts UWP apps (Store, ...).
		// Pinning the host exe gives a blank/broken pin (no icon, wrong launch, never lights up).
		string hostExe = WindowList.GetExePath(hwnd);
		bool sharedHost = IsSharedHostExe(hostExe);
		bool packagedExe = AppLauncher.IsWindowsAppsPath(hostExe);
		if (IsExplorerHost(hostExe) || sharedHost || packagedExe)
		{
			string appId = AppInventory.GetWindowAppId(hwnd);
			if (!string.IsNullOrEmpty(appId) && !appId.Equals(ExplorerAumid, StringComparison.OrdinalIgnoreCase))
			{
				string launch = "shell:AppsFolder\\" + appId;
				if (!_pins.Any((PinnedApp p) => string.Equals(p.LaunchPath, launch, StringComparison.OrdinalIgnoreCase)))
				{
					string title = WindowList.GetTitle(hwnd);
					_pins.Add(new PinnedApp
					{
						Name = (string.IsNullOrEmpty(title) ? appId : title),
						LaunchPath = launch,
						Aumid = appId
					});
					TaskbarPins.Save(_pins);
					PinsChanged?.Invoke();
				}
				return;
			}
			if (sharedHost || packagedExe)
			{
				return;   // shared/packaged host with no AUMID: never persist an unstable host/versioned WindowsApps path
			}
		}
		string exe = ExeFor(hwnd) ?? WindowList.GetExePath(hwnd);
		if (!string.IsNullOrEmpty(exe) && !TaskbarPins.IsPinned(_pins, exe))
		{
			_pins.Add(new PinnedApp
			{
				Name = System.IO.Path.GetFileNameWithoutExtension(exe),
				LaunchPath = exe,
				ExePath = exe
			});
			TaskbarPins.Save(_pins);
			PinsChanged?.Invoke();
		}
	}

	private void OnPinDragOver(object sender, System.Windows.DragEventArgs e)
	{
		// Accept a classic app (.exe/.lnk FileDrop) OR a virtual app dragged from the shell (UWP / Start-menu item,
		// which arrives as a "Shell IDList Array" with no file path). ShellDropResolver.CanPin covers both.
		e.Effects = (ShellDropResolver.CanPin(e.Data) ? System.Windows.DragDropEffects.Link : System.Windows.DragDropEffects.None);
		e.Handled = true;
	}

	private void OnPinFileDrop(object sender, System.Windows.DragEventArgs e)
	{
		try
		{
			List<PinnedApp> resolved = ShellDropResolver.Resolve(e.Data);
			int added = 0;
			foreach (PinnedApp pin in resolved)
			{
				// Identity is exe + ARGUMENTS (+ AUMID), never the exe alone: a shortcut like "Metro Browser" runs the same
				// chrome.exe as the plain Chrome pin but with different --app arguments, so it is a DIFFERENT app. Keying on
				// ExePath alone made every such shortcut collide with an existing pin and silently refuse to pin it.
				bool already = _pins.Any((PinnedApp p) => SamePinIdentity(p, pin));
				if (!already)
				{
					_pins.Add(pin);
					added++;
					Logger.Log($"taskbar pin-drop: added '{pin.Name}' (launch={pin.LaunchPath}, aumid={pin.Aumid ?? "-"})");
				}
			}
			if (added > 0)
			{
				TaskbarPins.Save(_pins);
				PinsChanged?.Invoke();
			}
			else if (resolved.Count > 0)
			{
				Logger.Log("taskbar pin-drop: dropped item(s) already pinned; nothing added");
			}
			else
			{
				Logger.Log("taskbar pin-drop: payload had nothing pinnable");
			}
			e.Handled = true;
		}
		catch (Exception ex)
		{
			Logger.Log("pin drop: " + ex.Message);
		}
	}

	private void Unpin(PinnedTile tile)
	{
		_pins.RemoveAll((PinnedApp p) => string.Equals(p.ExePath, tile.ExePath, StringComparison.OrdinalIgnoreCase) && string.Equals(p.LaunchPath, tile.LaunchPath, StringComparison.OrdinalIgnoreCase));
		TaskbarPins.Save(_pins);
		PinsChanged?.Invoke();
	}

	private void OnShowDesktop(object sender, RoutedEventArgs e)
	{
		DwmPeek.Unpeek();   // end any active Aero Peek before the real show-desktop toggle
		keybd_event(91, 0, 0u, UIntPtr.Zero);
		keybd_event(68, 0, 0u, UIntPtr.Zero);
		keybd_event(68, 0, 2u, UIntPtr.Zero);
		keybd_event(91, 0, 2u, UIntPtr.Zero);
	}

	// Aero Peek: hovering the show-desktop sliver in Win7 mode fades all windows to glass outlines to reveal the
	// desktop; leaving restores them. Only in Win7 (aero) mode; DwmPeek self-disables if the entry point is missing.
	private void OnShowDesktopEnter(object sender, System.Windows.Input.MouseEventArgs e)
	{
		if (!ShellSkin.GlassOn)
		{
			return;
		}
		try
		{
			nint hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
			DwmPeek.Peek(hwnd);
		}
		catch
		{
		}
	}

	private void OnShowDesktopLeave(object sender, System.Windows.Input.MouseEventArgs e)
	{
		DwmPeek.Unpeek();
	}

	private void OnSearchClick(object sender, RoutedEventArgs e)
	{
		PulsePress(sender as UIElement);
		RaiseSearch();
	}

	private void OnTaskViewClick(object sender, RoutedEventArgs e)
	{
		PulsePress(sender as UIElement);
		RaiseTaskView();
	}

	private static void PulsePress(UIElement? el)
	{
		//IL_0056: Unknown result type (might be due to invalid IL or missing references)
		if (el != null)
		{
			ScaleTransform st = el.RenderTransform as ScaleTransform;
			if (st == null)
			{
				st = (ScaleTransform)(el.RenderTransform = new ScaleTransform(1.0, 1.0));
				el.RenderTransformOrigin = new System.Windows.Point(0.5, 0.5);
			}
			DoubleAnimationUsingKeyFrames anim = new DoubleAnimationUsingKeyFrames();
			anim.KeyFrames.Add(new EasingDoubleKeyFrame(0.82, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(80L)), new CubicEase
			{
				EasingMode = EasingMode.EaseOut
			}));
			anim.KeyFrames.Add(new EasingDoubleKeyFrame(1.0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(300L)), new BackEase
			{
				EasingMode = EasingMode.EaseOut,
				Amplitude = 0.7
			}));
			st.BeginAnimation(ScaleTransform.ScaleXProperty, anim);
			st.BeginAnimation(ScaleTransform.ScaleYProperty, anim);
		}
	}

	private void OnActionCenter(object sender, RoutedEventArgs e)
	{
		PulsePress(sender as UIElement);
		ActionCenter.Toggle(_screen, _position);
	}

	protected override void OnClosing(CancelEventArgs e)
	{
		if (!_stopped)
		{
			e.Cancel = true;
		}
	}

	private bool IsFullscreenForeground()
	{
		if (_positionDiagnosticsActive && !_fullscreenDiagnosticsEnabled)
		{
			return false;
		}
		System.Drawing.Rectangle m = _screen.Bounds;
		// Use the actual foreground owner. Looking up the window at the monitor centre can select
		// a covered/background fullscreen window and makes multi-monitor activation ambiguous.
		nint candidate = _positionDiagnosticsActive && _fullscreenDiagnosticsEnabled && _fullscreenDiagnosticsWindow != nint.Zero
			? _fullscreenDiagnosticsWindow
			: GetForegroundWindow();
		nint fg = GetAncestor(candidate, 2u);
		// Fullscreen = a window covers the ENTIRE monitor (browser/video fullscreen, games). A normal MAXIMIZED
		// window is naturally excluded because it respects the reserved taskbar strip, so its rect bottom stays above
		// the monitor bottom (we no longer release the work area, which is what previously broke this).
		if (CoversMonitor(fg, m))
		{
			return true;
		}
		// Also test the TOP window sitting at THIS monitor's centre — regardless of where focus is. This catches a
		// fullscreen window on this monitor even when focus is on another screen (a video playing fullscreen here while
		// you work on a second monitor): GetForegroundWindow alone reports the other screen's window and wrongly shows
		// the bar, and a rect-based "is the foreground on this monitor" test is fooled by a maximized window's ~8px
		// frame overhang poking across the monitor edge. A maximized (non-fullscreen) window is returned here too but
		// fails CoversMonitor (it respects the taskbar strip), and the desktop/tray are excluded — so only a genuine
		// fullscreen window on this monitor hides the bar. WindowFromPoint returns the TOP window at the point, so a
		// covered/background window can never cause a false hide.
		nint onThis = IntPtr.Zero;
		bool covers = false;
		if (!_positionDiagnosticsActive)
		{
			POINT c = default(POINT);
			c.X = m.Left + m.Width / 2;
			c.Y = m.Top + m.Height / 2;
			onThis = GetAncestor(WindowFromPoint(c), 2u);
			covers = onThis != fg && CoversMonitor(onThis, m);
		}
		return covers;
	}

	// True when root window w fully covers monitor m and is not our own bar, the desktop, or a tray host.
	private bool CoversMonitor(nint w, System.Drawing.Rectangle m)
	{
		if (w == IntPtr.Zero)
		{
			return false;
		}
		if (TrayReader.ProcessIdOf(w) == _ownPid)
		{
			return false;
		}
		_clsSb.Clear();
		GetClassName(w, _clsSb, _clsSb.Capacity);
		switch (_clsSb.ToString())
		{
		case "Progman":
		case "WorkerW":
		case "Shell_TrayWnd":
		case "Shell_SecondaryTrayWnd":
			return false;
		}
		if (!GetWindowRect(w, out var r))
		{
			return false;
		}
		return r.left <= m.Left && r.top <= m.Top && r.right >= m.Right && r.bottom >= m.Bottom;
	}

	// Static gate for the Charms edge-pull gesture: true when a TRUE-fullscreen window (a game or fullscreen video —
	// NOT a normal maximized window) owns the foreground, so the gesture never steals the edge from a fullscreen app.
	// A maximized window respects the reserved taskbar strip, so its bottom stays above the monitor bottom and is
	// correctly excluded; the desktop and shell/tray windows are excluded by class.
	public static bool AnyFullscreenForeground()
	{
		try
		{
			nint fg = GetAncestor(GetForegroundWindow(), 2u);
			if (fg == IntPtr.Zero || !GetWindowRect(fg, out RECT r))
			{
				return false;
			}
			System.Windows.Forms.Screen scr = System.Windows.Forms.Screen.FromRectangle(System.Drawing.Rectangle.FromLTRB(r.left, r.top, r.right, r.bottom));
			System.Drawing.Rectangle m = scr.Bounds;
			System.Text.StringBuilder sb = new System.Text.StringBuilder(64);
			GetClassName(fg, sb, sb.Capacity);
			switch (sb.ToString())
			{
			case "Progman":
			case "WorkerW":
			case "Shell_TrayWnd":
			case "Shell_SecondaryTrayWnd":
				return false;
			}
			return r.left <= m.Left && r.top <= m.Top && r.right >= m.Right && r.bottom >= m.Bottom;
		}
		catch
		{
			return false;
		}
	}

	[DllImport("user32.dll")]
	private static extern nint MonitorFromPoint(POINT pt, uint flags);

	[DllImport("user32.dll")]
	private static extern bool GetMonitorInfo(nint h, ref MONITORINFO mi);

	[DllImport("user32.dll")]
	private static extern nint GetForegroundWindow();

	[DllImport("user32.dll")]
	private static extern bool GetWindowRect(nint hWnd, out RECT rect);

	[DllImport("user32.dll")]
	private static extern nint WindowFromPoint(POINT p);

	[DllImport("user32.dll")]
	private static extern nint GetAncestor(nint hWnd, uint flags);

	[DllImport("user32.dll")]
	private static extern bool IsZoomed(nint hWnd);

	[DllImport("user32.dll", CharSet = CharSet.Unicode)]
	private static extern int GetClassName(nint hWnd, StringBuilder buf, int max);

	[DllImport("user32.dll")]
	private static extern void keybd_event(byte vk, byte scan, uint flags, nuint extra);

	private static string? AumidForWindow(nint hwnd)
	{
		try
		{
			GetWindowThreadProcessId(hwnd, out var pid);
			if (pid == 0)
			{
				return null;
			}
			nint h = OpenProcess(4096u, inherit: false, pid);
			if (h == IntPtr.Zero)
			{
				return null;
			}
			try
			{
				uint len = 260u;
				StringBuilder sb = new StringBuilder((int)len);
				return (GetApplicationUserModelId(h, ref len, sb) == 0) ? sb.ToString() : null;
			}
			finally
			{
				CloseHandle(h);
			}
		}
		catch
		{
			return null;
		}
	}

	[DllImport("user32.dll")]
	private static extern uint GetWindowThreadProcessId(nint hWnd, out uint pid);

	[DllImport("kernel32.dll")]
	private static extern nint OpenProcess(uint access, bool inherit, uint pid);

	[DllImport("kernel32.dll")]
	private static extern bool CloseHandle(nint h);

	[DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
	private static extern int GetApplicationUserModelId(nint hProcess, ref uint length, StringBuilder aumid);

	[DllImport("user32.dll")]
	private static extern bool SetWindowPos(nint h, nint after, int x, int y, int cx, int cy, uint flags);
}
