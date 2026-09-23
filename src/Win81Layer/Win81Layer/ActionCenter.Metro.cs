using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using Windows.Devices.Radios;
using Windows.UI.Notifications;
using Windows.UI.Notifications.Management;
using Path = System.Windows.Shapes.Path;

namespace Win81Layer;

public sealed class ActionCenterQaMetrics
{
	public double WidthDiu { get; set; }
	public double HeightDiu { get; set; }
	public int DefinedTiles { get; set; }
	public int VisibleTiles { get; set; }
	public int TileColumns { get; set; }
	public int VisibleTileRows { get; set; }
	public int FunctionalRoutes { get; set; }
	public int RouteContracts { get; set; }
	public int StatefulTiles { get; set; }
	public bool IconsAreVectors { get; set; }
	public bool IconsUseUniformPremiumGeometry { get; set; }
	public double TileIconSizeDiu { get; set; }
	public bool ExactRouteContracts { get; set; }
	public bool MapsUsesGoogleMaps { get; set; }
	public bool ExternalLinksUseDefaultHandler { get; set; }
	public string MapsTarget { get; set; } = string.Empty;
	public bool ActiveChecksPresent { get; set; }
	public bool FourColumnReferenceOrder { get; set; }
	public bool LaptopRulesPassed { get; set; }
	public bool DesktopRulesPassed { get; set; }
	public bool SlidersAreLiveControls { get; set; }
	public bool NotificationViewportBounded { get; set; }
	public bool PixelScrollingEnabled { get; set; }
	public double ScrollBarHitWidthDiu { get; set; }
	public double ScrollRangeDiu { get; set; }
	public bool EmptyStateVisible { get; set; }
	public int NotificationCards { get; set; }
	public bool ClearAllFunctional { get; set; }
	public bool FewerSettingsFunctional { get; set; }
	public bool CollapseUsesCanonicalDirectionalArrow { get; set; }
	public bool CollapseDirectionMatchesAction { get; set; }
	public bool CollapseHitTargetExpanded { get; set; }
	public bool ThemeBackgroundSynchronized { get; set; }
	public bool ThemeSwitchPassed { get; set; }
	public string ThemeBase { get; set; } = string.Empty;
	public int AuthenticOpenMs { get; set; }
	public int AuthenticCloseMs { get; set; }
	public int TileHoverMs { get; set; }
	public int TileClickMs { get; set; }
	public int NotificationTransitionMs { get; set; }
	public int ClearAllTransitionMs { get; set; }
	public long VisualBuildMs { get; set; }
	public long RenderAndLayoutMs { get; set; }
	public bool RefreshWorkerActiveDuringDetachedRender { get; set; }
	public bool IdleRenderingHookInactive { get; set; }
	public string Screenshot { get; set; } = string.Empty;
}

public sealed partial class ActionCenter
{
	private sealed class MetroTile
	{
		public required string Id { get; init; }
		public required Border Root { get; init; }
		public required SolidColorBrush Fill { get; init; }
		public required TextBlock Status { get; init; }
		public required FrameworkElement Icon { get; init; }
		public required Path Check { get; init; }
		public required ScaleTransform Scale { get; init; }
		public required System.Windows.Media.Color BaseColor { get; init; }
		public required Func<Task> Action { get; init; }
		public required string RouteContract { get; init; }
		public required Func<string> StatusText { get; init; }
		public Func<bool>? IsOn { get; init; }
		public Func<bool>? IsVisible { get; init; }
		public bool LaptopOnly { get; init; }
		public bool Busy { get; set; }
	}

	private sealed record MetroRuntimeSnapshot(
		float Volume,
		bool Muted,
		int Brightness,
		bool BatteryPresent,
		bool BatterySaver,
		NetState81 Network,
		bool RadioReadable,
		bool WifiOn,
		bool BluetoothOn,
		bool AirplaneOn);

	private sealed class MetroSlider : Slider
	{
		private bool _dragging;

		// Unfilled track: was alpha 80 (~31% white) which read faint on the dark panel; raised to ~60% white to match
		// the sound flyout so the volume/brightness bars are clearly visible.
		public System.Windows.Media.Brush TrackBrush { get; set; } = new SolidColorBrush(System.Windows.Media.Color.FromArgb(153, 255, 255, 255));

		public System.Windows.Media.Brush FillBrush { get; set; } = new SolidColorBrush(System.Windows.Media.Color.FromRgb(108, 216, 244));

		public MetroSlider()
		{
			Height = 30.0;
			Minimum = 0.0;
			Maximum = 100.0;
			SmallChange = 1.0;
			LargeChange = 10.0;
			Focusable = true;
			Template = new ControlTemplate(typeof(Slider));
			SnapsToDevicePixels = true;
		}

		protected override void OnValueChanged(double oldValue, double newValue)
		{
			base.OnValueChanged(oldValue, newValue);
			InvalidateVisual();
		}

		protected override void OnRender(DrawingContext drawingContext)
		{
			base.OnRender(drawingContext);
			// Full-bounds transparent fill so the ENTIRE control area is hit-testable for clicks, drag
			// and mouse wheel. With an empty template and no Background, WPF only hit-tests the painted
			// track/thumb geometry below — leaving most of the 30px row dead to the mouse, which is why
			// adjusting "didn't catch easily" unless the cursor landed exactly on the thin track/thumb.
			drawingContext.DrawRectangle(System.Windows.Media.Brushes.Transparent, null, new Rect(0.0, 0.0, ActualWidth, ActualHeight));
			double center = ActualHeight * 0.5;
			double usable = Math.Max(1.0, ActualWidth - 12.0);
			double ratio = Maximum <= Minimum ? 0.0 : Math.Clamp((Value - Minimum) / (Maximum - Minimum), 0.0, 1.0);
			drawingContext.DrawRectangle(TrackBrush, null, new Rect(0.0, center - 3.0, ActualWidth, 6.0));
			drawingContext.DrawRectangle(FillBrush, null, new Rect(0.0, center - 3.0, Math.Max(0.0, ratio * usable + 6.0), 6.0));
			double thumbX = ratio * usable;
			drawingContext.DrawRectangle(System.Windows.Media.Brushes.White, null, new Rect(thumbX, center - 12.0, 12.0, 24.0));
		}

		protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
		{
			base.OnMouseLeftButtonDown(e);
			Focus();
			// Begin dragging unconditionally. CaptureMouse() can be REFUSED while the flyout is still
			// mid-activation (returns false), which previously left _dragging=false so the thumb drag
			// silently did nothing. Capture is only needed to track a drag OUTSIDE the control, so treat
			// it as best-effort and drive the drag from OnMouseMove regardless.
			_dragging = true;
			CaptureMouse();
			SetFromPoint(e.GetPosition(this).X);
			e.Handled = true;
		}

		protected override void OnMouseMove(System.Windows.Input.MouseEventArgs e)
		{
			base.OnMouseMove(e);
			if (!_dragging)
			{
				return;
			}
			if (e.LeftButton == MouseButtonState.Pressed)
			{
				SetFromPoint(e.GetPosition(this).X);
				e.Handled = true;
			}
			else
			{
				// The button-up happened outside our bounds (no capture) — stop dragging cleanly.
				_dragging = false;
				if (IsMouseCaptured)
				{
					ReleaseMouseCapture();
				}
			}
		}

		protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
		{
			base.OnMouseLeftButtonUp(e);
			if (_dragging)
			{
				SetFromPoint(e.GetPosition(this).X);
				_dragging = false;
				if (IsMouseCaptured)
				{
					ReleaseMouseCapture();
				}
				e.Handled = true;
			}
		}

		protected override void OnMouseWheel(MouseWheelEventArgs e)
		{
			Value = Math.Clamp(Value + Math.Sign(e.Delta) * LargeChange, Minimum, Maximum);
			e.Handled = true;
		}

		protected override void OnKeyDown(System.Windows.Input.KeyEventArgs e)
		{
			if (e.Key == Key.Left || e.Key == Key.Down)
			{
				Value = Math.Max(Minimum, Value - SmallChange);
				e.Handled = true;
			}
			else if (e.Key == Key.Right || e.Key == Key.Up)
			{
				Value = Math.Min(Maximum, Value + SmallChange);
				e.Handled = true;
			}
			else
			{
				base.OnKeyDown(e);
			}
		}

		private void SetFromPoint(double x)
		{
			double usable = Math.Max(1.0, ActualWidth - 12.0);
			double ratio = Math.Clamp((x - 6.0) / usable, 0.0, 1.0);
			Value = Minimum + ratio * (Maximum - Minimum);
		}
	}

	private readonly List<MetroTile> _metroTiles = new();
	private Grid _metroRoot = null!;
	private Grid _emptyState = null!;
	private ScrollViewer _notificationScroll = null!;
	private ScrollViewer _tileScroll = null!;
	private Border _tileViewport = null!;
	private StackPanel _sliderHost = null!;
	private FrameworkElement _brightnessRow = null!;
	private Button _clearAllButton = null!;
	private Border _collapseButton = null!;
	private TextBlock _collapseLabel = null!;
	private MetroDirectionalArrow81 _collapseArrow = null!;
	private TextBlock _headerTitle = null!;
	private TextBlock _notificationsHeading = null!;
	private TextBlock _emptySubtitle = null!;
	private TextBlock _volumeValueText = null!;
	private TextBlock _brightnessValueText = null!;
	private Border _volumeIconHost = null!;
	private Border _brightnessIconHost = null!;
	private CancellationTokenSource? _runtimeCts;
	private int _runtimeGeneration;
	private int _animationGeneration;
	private int _notificationCount;
	private int _visibleTileRows;
	private bool _expanded = true;
	private bool _qaRendering;
	private bool _isLaptop;
	private bool _brightnessAvailable;
	private bool _batterySaver;
	private bool _projectConnected;
	private NetState81? _lastNet;
	private string _wifiStatus = "Checking";
	private string _bluetoothStatus = "Checking";
	private string _airplaneStatus = "Checking";
	private string _batteryStatus = "Checking";
	private double _tileHeight = 96.0;
	private System.Windows.Media.Color _themeBase;
	private System.Windows.Media.Color _themeAccent;
	private LinearGradientBrush _themeBackground = null!;
	private SolidColorBrush _secondaryTextBrush = null!;
	private SolidColorBrush _linkBrush = null!;
	private const int AuthenticOpenMilliseconds = 210;
	private const int AuthenticCloseMilliseconds = 150;
	private const int TileHoverMilliseconds = 100;
	private const int TileClickMilliseconds = 100;
	private const int NotificationMilliseconds = 200;
	private const int ClearAllMilliseconds = 150;
	private const double PremiumTileIconSize = 34.0;
	private const string WifiSettingsTarget = "ms-settings:network-wifi";
	private const string NetworkStatusTarget = "ms-settings:network-status";
	private const string BluetoothSettingsTarget = "ms-settings:bluetooth";
	private const string AirplaneSettingsTarget = "ms-settings:network-airplanemode";
	private const string DisplaySettingsTarget = "ms-settings:display";
	private const string ProjectSettingsTarget = "ms-settings:project";
	private const string ConnectedDevicesTarget = "ms-settings:connecteddevices";
	private const string PersonalizationSettingsTarget = "ms-settings:personalization";
	private const string ColorsSettingsTarget = "ms-settings:colors";
	private const string BatterySaverSettingsTarget = "ms-settings:batterysaver";
	private const string PowerSleepSettingsTarget = "ms-settings:powersleep";
	private const string SettingsHomeTarget = "ms-settings:";
	private const string NightLightSettingsTarget = "ms-settings:nightlight";
	private const string GoogleMapsTarget = "https://www.google.com/maps";
	private static readonly IReadOnlyDictionary<string, string> MetroRouteContracts = new Dictionary<string, string>(StringComparer.Ordinal)
	{
		["wifi"] = "toggle:Windows.Devices.Radios/WiFi|fallback:" + WifiSettingsTarget,
		["bluetooth"] = "toggle:Windows.Devices.Radios/Bluetooth|fallback:" + BluetoothSettingsTarget,
		["brightness"] = "control:MonitorBrightness|fallback:" + DisplaySettingsTarget,
		["airplane"] = "toggle:Windows.Devices.Radios/all|fallback:" + AirplaneSettingsTarget,
		["quiet"] = "toggle:launcher-native-notification-banners",
		["theme"] = "launcher:start-personalize|fallback:" + PersonalizationSettingsTarget,
		["maps"] = "browser:" + GoogleMapsTarget,
		["project"] = "shell:DisplaySwitch.exe|fallback:" + ProjectSettingsTarget,
		["connect"] = "shell:Win+K|fallback:" + ConnectedDevicesTarget,
		["battery"] = "settings:" + BatterySaverSettingsTarget,
		["settings"] = "settings:" + SettingsHomeTarget,
		["power"] = "launcher:power-menu",
		["nightlight"] = "settings:" + NightLightSettingsTarget
	};

	private void BuildMetroSurface()
	{
		_isLaptop = PowerStatus.Read().present;
		// Cheap initial guess only — a synchronous MonitorBrightness.Get() here is a 50-300ms DDC/CI I2C read (can even time
		// out on a desktop with no DDC monitor), which would stall the first open. The async refresh path (RefreshMetro ->
		// SyncBrightness, off-thread) sets the real _brightnessAvailable from the live probe on ALL device types shortly after.
		_brightnessAvailable = _isLaptop;
		base.UseLayoutRounding = true;
		base.SnapsToDevicePixels = true;
		_metroRoot = new Grid
		{
			ClipToBounds = true,
			RenderTransform = _slide,
			UseLayoutRounding = true,
			SnapsToDevicePixels = true
		};
		StyleMetroScrollBars();
		_metroRoot.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
		_metroRoot.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
		_metroRoot.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1.0, GridUnitType.Star) });
		_metroRoot.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
		_metroRoot.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
		_metroRoot.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

		Grid header = new Grid { Margin = new Thickness(22.0, 18.0, 20.0, 6.0) };
		header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.0, GridUnitType.Star) });
		header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
		_headerTitle = new TextBlock
		{
			Text = "Action center",
			FontFamily = new System.Windows.Media.FontFamily("Segoe UI Light"),
			FontSize = 38.0,
			Foreground = System.Windows.Media.Brushes.White,
			VerticalAlignment = VerticalAlignment.Center,
			TextWrapping = TextWrapping.NoWrap
		};
		header.Children.Add(_headerTitle);
		_clearAllButton = LinkButton("Clear all");
		_clearAllButton.FontSize = 14.0;
		_clearAllButton.Padding = new Thickness(9.0, 7.0, 0.0, 7.0);
		_clearAllButton.HorizontalAlignment = HorizontalAlignment.Right;
		_clearAllButton.VerticalAlignment = VerticalAlignment.Center;
		_clearAllButton.Click += delegate { ClearAllNotifications(); };
		Grid.SetColumn(_clearAllButton, 1);
		header.Children.Add(_clearAllButton);
		Grid.SetRow(header, 0);
		_metroRoot.Children.Add(header);

		_notificationsHeading = new TextBlock
		{
			Text = "Notifications",
			FontFamily = new System.Windows.Media.FontFamily("Segoe UI Light"),
			FontSize = 22.0,
			Foreground = System.Windows.Media.Brushes.White,
			Margin = new Thickness(22.0, 8.0, 22.0, 7.0)
		};
		Grid.SetRow(_notificationsHeading, 1);
		_metroRoot.Children.Add(_notificationsHeading);

		Grid notificationHost = new Grid { Margin = new Thickness(18.0, 0.0, 11.0, 5.0), MinHeight = 76.0 };
		_notifs = new StackPanel { Margin = new Thickness(0.0, 0.0, 4.0, 0.0) };
		_notificationScroll = new ScrollViewer
		{
			Content = _notifs,
			VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
			HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
			CanContentScroll = false,
			PanningMode = PanningMode.VerticalOnly,
			PanningDeceleration = 0.001
		};
		_notificationScroll.AddHandler(Mouse.PreviewMouseWheelEvent, new MouseWheelEventHandler(OnMetroNotificationWheel), handledEventsToo: true);
		_notificationScroll.AddHandler(Mouse.PreviewMouseDownEvent, new MouseButtonEventHandler(OnMetroNotificationPointerDown), handledEventsToo: true);
		notificationHost.Children.Add(_notificationScroll);

		_notifEmpty = new TextBlock
		{
			Text = "No new notifications",
			FontFamily = new System.Windows.Media.FontFamily("Segoe UI Light"),
			FontSize = 23.0,
			Foreground = System.Windows.Media.Brushes.White,
			TextAlignment = TextAlignment.Left
		};
		_emptySubtitle = new TextBlock
		{
			Text = "You're all caught up!",
			FontFamily = new System.Windows.Media.FontFamily("Segoe UI Light"),
			FontSize = 16.0,
			Margin = new Thickness(0.0, 2.0, 0.0, 0.0)
		};
		StackPanel emptyText = new StackPanel
		{
			HorizontalAlignment = HorizontalAlignment.Left,
			VerticalAlignment = VerticalAlignment.Center
		};
		emptyText.Children.Add(_notifEmpty);
		emptyText.Children.Add(_emptySubtitle);
		_emptyState = new Grid { Margin = new Thickness(20.0, 0.0, 20.0, 0.0), IsHitTestVisible = false };
		_emptyState.Children.Add(emptyText);
		notificationHost.Children.Add(_emptyState);
		Grid.SetRow(notificationHost, 2);
		_metroRoot.Children.Add(notificationHost);

		_sliderHost = new StackPanel { Margin = new Thickness(20.0, 0.0, 20.0, 5.0) };
		BuildMetroSliders();
		Grid.SetRow(_sliderHost, 3);
		_metroRoot.Children.Add(_sliderHost);

		_tiles = new Grid { Margin = new Thickness(0.0), UseLayoutRounding = true };
		_tileScroll = new ScrollViewer
		{
			Content = _tiles,
			VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
			HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
			CanContentScroll = false,
			PanningMode = PanningMode.VerticalOnly
		};
		_tileScroll.AddHandler(Mouse.PreviewMouseWheelEvent, new MouseWheelEventHandler(OnMetroTileWheel), handledEventsToo: true);
		_tileViewport = new Border
		{
			Margin = new Thickness(18.0, 0.0, 14.0, 0.0),
			ClipToBounds = true,
			Child = _tileScroll
		};
		Grid.SetRow(_tileViewport, 4);
		_metroRoot.Children.Add(_tileViewport);

		_collapseLabel = new TextBlock
		{
			Text = "Fewer settings",
			FontFamily = new System.Windows.Media.FontFamily("Segoe UI"),
			FontSize = 16.0,
			Foreground = System.Windows.Media.Brushes.White,
			VerticalAlignment = VerticalAlignment.Center,
			Margin = new Thickness(10.0, 0.0, 0.0, 0.0)
		};
		_collapseArrow = new MetroDirectionalArrow81
		{
			Width = 42.0,
			Height = 42.0,
			GlyphSize = 32.0,
			Direction = MetroArrowDirection81.Up,
			Focusable = false,
			IsTabStop = false
		};
		AutomationProperties.SetName(_collapseArrow, "Fewer settings");
		StackPanel collapseContent = new StackPanel { Orientation = Orientation.Horizontal };
		collapseContent.Children.Add(_collapseArrow);
		collapseContent.Children.Add(_collapseLabel);
		_collapseButton = new Border
		{
			Background = System.Windows.Media.Brushes.Transparent,
			Padding = new Thickness(5.0, 7.0, 9.0, 10.0),
			Margin = new Thickness(14.0, 2.0, 14.0, 0.0),
			Cursor = System.Windows.Input.Cursors.Hand,
			Focusable = true,
			Child = collapseContent
		};
		AutomationProperties.SetName(_collapseButton, "Fewer settings");
		_collapseButton.AddHandler(UIElement.MouseLeftButtonUpEvent,
			new MouseButtonEventHandler(delegate(object _, MouseButtonEventArgs e)
			{
				e.Handled = true;
				ToggleMetroExpanded();
			}),
			handledEventsToo: true);
		_collapseButton.KeyDown += delegate(object _, System.Windows.Input.KeyEventArgs e)
		{
			if (e.Key == Key.Enter || e.Key == Key.Space)
			{
				ToggleMetroExpanded();
				e.Handled = true;
			}
		};
		Grid.SetRow(_collapseButton, 5);
		_metroRoot.Children.Add(_collapseButton);

		base.Content = _metroRoot;
		BuildTiles();
		ApplyMetroTheme();
		ConfigureMetroLayout(PanelW, 860.0);
		ShowMetroEmpty("You're all caught up!");
	}

	private void BuildMetroSliders()
	{
		_volume = new MetroSlider { Value = 50.0 };
		_volumeValueText = CreateSliderValue("50");
		FrameworkElement volumeRow = CreateMetroSliderRow("volume", "Volume", _volume, _volumeValueText, out Border volumeIcon);
		_volumeIconHost = volumeIcon;
		volumeIcon.ToolTip = "Mute or unmute";
		volumeIcon.MouseLeftButtonUp += delegate
		{
			try
			{
				_audio.ToggleMute();
				bool m = _audio.GetMute();
				_volumeValueText.Text = m ? "Muted" : Math.Round(_volume.Value).ToString();
				UpdateVolumeSliderIcon((int)Math.Round(_volume.Value), m);
			}
			catch { }
		};
		_volume.ValueChanged += delegate(object _, RoutedPropertyChangedEventArgs<double> e)
		{
			_volumeValueText.Text = Math.Round(e.NewValue).ToString();
			// Reflect the new level immediately (dragging volume up unmutes in Windows, so muted=false while > 0).
			UpdateVolumeSliderIcon((int)Math.Round(e.NewValue), false);
			if (_syncingSliders) return;
			try { _audio.SetVolume((float)(e.NewValue / 100.0)); } catch { }
		};
		_sliderHost.Children.Add(volumeRow);

		_brightness = new MetroSlider { Value = 50.0 };
		_brightnessValueText = CreateSliderValue("50");
		_brightnessRow = CreateMetroSliderRow("brightness", "Brightness", _brightness, _brightnessValueText, out Border brightnessIcon);
		_brightnessIconHost = brightnessIcon;
		brightnessIcon.ToolTip = "Brightness";
		brightnessIcon.MouseLeftButtonUp += delegate { _brightness.Focus(); };
		_brightness.ValueChanged += delegate(object _, RoutedPropertyChangedEventArgs<double> e)
		{
			_brightnessValueText.Text = Math.Round(e.NewValue).ToString();
			UpdateBrightnessSliderIcon((int)Math.Round(e.NewValue));
			if (_syncingSliders) return;
			MonitorBrightness.SetThrottled((int)Math.Round(e.NewValue));
		};
		UpdateVolumeSliderIcon((int)Math.Round(_volume.Value), false);
		UpdateBrightnessSliderIcon((int)Math.Round(_brightness.Value));
		_sliderHost.Children.Add(_brightnessRow);
	}

	// #4: the volume slider icon reflects the live level + mute (authentic SndVolSSO variants, white-tinted for the dark AC).
	private void UpdateVolumeSliderIcon(int pct, bool muted)
	{
		if (_volumeIconHost == null)
		{
			return;
		}
		System.Windows.Media.ImageSource img = null;
		try { img = Win81AssetResolver.VolumeImage(pct, muted, 32); } catch { }
		if (img != null)
		{
			System.Windows.Controls.Image im = new System.Windows.Controls.Image
			{
				Width = 30.0,
				Height = 30.0,
				Source = img,
				Stretch = Stretch.Uniform,
				SnapsToDevicePixels = true,
				HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
				VerticalAlignment = System.Windows.VerticalAlignment.Center
			};
			RenderOptions.SetBitmapScalingMode(im, BitmapScalingMode.HighQuality);
			_volumeIconHost.Child = im;
		}
		else
		{
			_volumeIconHost.Child = CreateMetroIcon("volume", 30.0);
		}
	}

	// #4: the brightness slider icon dims at low brightness and brightens toward full (laptop-only surface).
	private void UpdateBrightnessSliderIcon(int level)
	{
		if (_brightnessIconHost == null)
		{
			return;
		}
		FrameworkElement icon = CreateMetroIcon("brightness", 30.0);
		icon.Opacity = 0.45 + 0.55 * Math.Clamp((double)level / 100.0, 0.0, 1.0);
		_brightnessIconHost.Child = icon;
	}

	private FrameworkElement CreateMetroSliderRow(string iconId, string label, Slider slider, TextBlock value, out Border iconHost)
	{
		Grid row = new Grid { Margin = new Thickness(0.0, 2.0, 0.0, 2.0), Height = 52.0 };
		row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(44.0) });
		row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.0, GridUnitType.Star) });
		// Value column: wide enough for the word "Muted" (the volume row shows it when muted), not just a 2-3 digit
		// number — 38px clipped "Muted" to "Mute". The value is right-aligned to the slider host's content edge
		// (20px inside the panel), so this only borrows width from the star slider column and never runs off-panel.
		row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(64.0) });
		iconHost = new Border
		{
			Width = 36.0,
			Height = 36.0,
			Background = System.Windows.Media.Brushes.Transparent,
			Cursor = System.Windows.Input.Cursors.Hand,
			VerticalAlignment = VerticalAlignment.Bottom,
			Child = CreateMetroIcon(iconId, 30.0)
		};
		Grid.SetColumn(iconHost, 0);
		row.Children.Add(iconHost);
		Grid sliderArea = new Grid();
		sliderArea.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
		sliderArea.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1.0, GridUnitType.Star) });
		TextBlock title = new TextBlock
		{
			Text = label,
			Foreground = System.Windows.Media.Brushes.White,
			FontFamily = new System.Windows.Media.FontFamily("Segoe UI"),
			FontSize = 14.0,
			Margin = new Thickness(0.0, 0.0, 0.0, 0.0)
		};
		sliderArea.Children.Add(title);
		Grid.SetRow(slider, 1);
		sliderArea.Children.Add(slider);
		Grid.SetColumn(sliderArea, 1);
		row.Children.Add(sliderArea);
		Grid.SetColumn(value, 2);
		row.Children.Add(value);
		return row;
	}

	private static TextBlock CreateSliderValue(string text)
	{
		return new TextBlock
		{
			Text = text,
			Foreground = System.Windows.Media.Brushes.White,
			FontFamily = new System.Windows.Media.FontFamily("Segoe UI Light"),
			FontSize = 15.0,
			VerticalAlignment = VerticalAlignment.Bottom,
			TextAlignment = TextAlignment.Right,
			Margin = new Thickness(4.0, 0.0, 0.0, 8.0)
		};
	}

	private void BuildMetroTiles()
	{
		_metroTiles.Clear();
		_tiles.Children.Clear();
		_tiles.ColumnDefinitions.Clear();
		_tiles.RowDefinitions.Clear();
		for (int i = 0; i < 4; i++) _tiles.ColumnDefinitions.Add(new ColumnDefinition());

		AddMetroTile("wifi", "Wi-Fi", System.Windows.Media.Color.FromRgb(0, 160, 231), ToggleWifi, MetroRouteContracts["wifi"], () => _wifiStatus, () => _wifiOn, () => NetCaps.HasWifi);
		AddMetroTile("bluetooth", "Bluetooth", System.Windows.Media.Color.FromRgb(116, 54, 214), ToggleBluetooth, MetroRouteContracts["bluetooth"], () => _bluetoothStatus, () => _btOn);
		AddMetroTile("brightness", "Brightness", System.Windows.Media.Color.FromRgb(0, 126, 229), CycleMetroBrightness, MetroRouteContracts["brightness"], () => _brightnessAvailable ? $"{Math.Round(_brightness?.Value ?? 0.0)}%" : "Unavailable", null, () => _brightnessAvailable, laptopOnly: true);
		AddMetroTile("airplane", "Airplane mode", System.Windows.Media.Color.FromRgb(207, 32, 86), ToggleAirplane, MetroRouteContracts["airplane"], () => _airplaneStatus, () => _airplaneOn, () => NetCaps.RadiosPresent, laptopOnly: false);
		AddMetroTile("quiet", "Quiet hours", System.Windows.Media.Color.FromRgb(0, 174, 184), () => RunSync(ToggleQuiet), MetroRouteContracts["quiet"], () => _quiet ? "On" : "Off", () => _quiet);
		AddMetroTile("theme", "Theme", System.Windows.Media.Color.FromRgb(199, 35, 130), OpenMetroPersonalize, MetroRouteContracts["theme"], () => "Personalize");
		AddMetroTile("maps", "Maps", System.Windows.Media.Color.FromRgb(45, 183, 44), OpenMetroMaps, MetroRouteContracts["maps"], () => "Google Maps");
		AddMetroTile("project", "Project", System.Windows.Media.Color.FromRgb(0, 126, 229), OpenProject, MetroRouteContracts["project"], () => _projectConnected ? "Connected" : "Disconnected", () => _projectConnected);
		AddMetroTile("connect", "Connect", System.Windows.Media.Color.FromRgb(100, 48, 210), OpenConnect, MetroRouteContracts["connect"], () => "Open panel");
		AddMetroTile("battery", "Battery saver", System.Windows.Media.Color.FromRgb(0, 155, 170), () => OpenExternal("Battery saver", BatterySaverSettingsTarget, PowerSleepSettingsTarget), MetroRouteContracts["battery"], () => _batteryStatus, () => _batterySaver, () => _isLaptop, laptopOnly: true);
		AddMetroTile("settings", "All settings", System.Windows.Media.Color.FromRgb(0, 126, 229), () => OpenExternal("All settings", SettingsHomeTarget), MetroRouteContracts["settings"], () => string.Empty);
		AddMetroTile("power", "Power", System.Windows.Media.Color.FromRgb(0, 126, 229), () => RunSync(ShowPowerMenu), MetroRouteContracts["power"], () => string.Empty);
		AddMetroTile("nightlight", "Night light", System.Windows.Media.Color.FromRgb(58, 95, 196), () => OpenExternal("Night light", NightLightSettingsTarget, DisplaySettingsTarget), MetroRouteContracts["nightlight"], () => "Open settings", null, () => _isLaptop && IsNightLightSupported(), laptopOnly: true);
		ArrangeMetroTiles(animateHeight: false);
		RefreshMetroTileStates(animate: false);
	}

	private void AddMetroTile(
		string id,
		string label,
		System.Windows.Media.Color color,
		Func<Task> action,
		string routeContract,
		Func<string> status,
		Func<bool>? isOn = null,
		Func<bool>? isVisible = null,
		bool laptopOnly = false)
	{
		SolidColorBrush fill = new SolidColorBrush(color);
		ScaleTransform scale = new ScaleTransform(1.0, 1.0);
		FrameworkElement icon = CreateMetroIcon(id, PremiumTileIconSize);
		TextBlock labelText = new TextBlock
		{
			Text = label,
			Foreground = System.Windows.Media.Brushes.White,
			FontFamily = new System.Windows.Media.FontFamily("Segoe UI"),
			FontSize = 13.2,
			TextWrapping = TextWrapping.Wrap,
			LineHeight = 15.0,
			Margin = new Thickness(0.0, 4.0, 0.0, 0.0)
		};
		TextBlock statusText = new TextBlock
		{
			Text = string.Empty,
			Foreground = new SolidColorBrush(System.Windows.Media.Color.FromArgb(230, 255, 255, 255)),
			FontFamily = new System.Windows.Media.FontFamily("Segoe UI"),
			FontSize = 10.2,
			TextTrimming = TextTrimming.CharacterEllipsis,
			Margin = new Thickness(0.0, 0.0, 0.0, 0.0)
		};
		StackPanel text = new StackPanel { VerticalAlignment = VerticalAlignment.Bottom };
		text.Children.Add(labelText);
		text.Children.Add(statusText);
		Grid content = new Grid { Margin = new Thickness(7.0, 7.0, 6.0, 6.0) };
		content.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1.0, GridUnitType.Star) });
		content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
		icon.HorizontalAlignment = HorizontalAlignment.Left;
		icon.VerticalAlignment = VerticalAlignment.Top;
		content.Children.Add(icon);
		Grid.SetRow(text, 1);
		content.Children.Add(text);
		Path check = CreateStrokePath("M 4,12 L 9,17 L 20,5", 2.6);
		check.Width = 17.0;
		check.Height = 17.0;
		check.Stretch = Stretch.Uniform;
		check.HorizontalAlignment = HorizontalAlignment.Right;
		check.VerticalAlignment = VerticalAlignment.Top;
		check.Visibility = Visibility.Collapsed;
		content.Children.Add(check);
		Border root = new Border
		{
			Background = fill,
			Margin = new Thickness(3.0),
			CornerRadius = new CornerRadius(0.0),
			Cursor = System.Windows.Input.Cursors.Hand,
			Focusable = true,
			RenderTransformOrigin = new Point(0.5, 0.5),
			RenderTransform = scale,
			Child = content
		};
		AutomationProperties.SetName(root, label);
		MetroTile tile = new MetroTile
		{
			Id = id,
			Root = root,
			Fill = fill,
			Status = statusText,
			Icon = icon,
			Check = check,
			Scale = scale,
			BaseColor = color,
			Action = action,
			RouteContract = routeContract,
			StatusText = status,
			IsOn = isOn,
			IsVisible = isVisible,
			LaptopOnly = laptopOnly
		};
		root.MouseEnter += delegate { ApplyMetroTileVisual(tile, animate: true, hover: true); };
		root.MouseLeave += delegate
		{
			AnimateScale(tile.Scale, 1.0, TileClickMilliseconds);
			ApplyMetroTileVisual(tile, animate: true, hover: false);
		};
		root.MouseLeftButtonDown += delegate(object _, MouseButtonEventArgs e)
		{
			if (tile.Busy) return;
			root.Focus();
			AnimateScale(tile.Scale, 0.96, TileClickMilliseconds);
			e.Handled = true;
		};
		root.MouseLeftButtonUp += async delegate(object _, MouseButtonEventArgs e)
		{
			if (tile.Busy) return;
			e.Handled = true;
			await RunMetroTileAsync(tile);
		};
		root.KeyDown += async delegate(object _, System.Windows.Input.KeyEventArgs e)
		{
			if ((e.Key == Key.Enter || e.Key == Key.Space) && !tile.Busy)
			{
				e.Handled = true;
				await RunMetroTileAsync(tile);
			}
		};
		_metroTiles.Add(tile);
		_tiles.Children.Add(root);
	}

	private async Task RunMetroTileAsync(MetroTile tile)
	{
		tile.Busy = true;
		AnimateScale(tile.Scale, 1.0, TileClickMilliseconds);
		tile.Root.Opacity = 0.86;
		try
		{
			await tile.Action();
		}
		catch (Exception ex)
		{
			Logger.Log($"Action Center '{tile.Id}' failed: {ex.Message}");
			ToastService.Show(null, "Action center", tile.Id, "Windows could not complete this action.");
		}
		finally
		{
			tile.Root.Opacity = 1.0;
			tile.Busy = false;
			RefreshMetroTileStates(animate: true);
		}
	}

	private void ArrangeMetroTiles(bool animateHeight)
	{
		int visible = 0;
		foreach (MetroTile tile in _metroTiles)
		{
			bool show = true;
			try { show = tile.IsVisible?.Invoke() ?? true; } catch { show = false; }
			tile.Root.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
			if (!show) continue;
			Grid.SetColumn(tile.Root, visible % 4);
			Grid.SetRow(tile.Root, visible / 4);
			visible++;
		}
		_visibleTileRows = Math.Max(1, (int)Math.Ceiling(visible / 4.0));
		while (_tiles.RowDefinitions.Count < _visibleTileRows)
		{
			_tiles.RowDefinitions.Add(new RowDefinition { Height = new GridLength(_tileHeight) });
		}
		for (int i = 0; i < _tiles.RowDefinitions.Count; i++)
		{
			_tiles.RowDefinitions[i].Height = new GridLength(_tileHeight);
		}
		UpdateTileViewportHeight(animateHeight);
	}

	private void ToggleMetroExpanded()
	{
		_expanded = !_expanded;
		_collapseLabel.Text = _expanded ? "Fewer settings" : "More settings";
		AutomationProperties.SetName(_collapseButton, _collapseLabel.Text);
		AutomationProperties.SetName(_collapseArrow, _collapseLabel.Text);
		_collapseArrow.Direction = _expanded ? MetroArrowDirection81.Up : MetroArrowDirection81.Down;
		UpdateTileViewportHeight(animate: true);
	}

	private void UpdateTileViewportHeight(bool animate)
	{
		if (_tileViewport == null) return;
		double expandedHeight = _visibleTileRows * _tileHeight;
		double maxForScreen = Math.Max(_tileHeight, base.Height > 0.0 ? base.Height * 0.41 : expandedHeight);
		double target = _expanded ? Math.Min(expandedHeight, maxForScreen) : _tileHeight;
		_tileViewport.BeginAnimation(FrameworkElement.HeightProperty, null);
		if (!animate || _qaRendering || Motion.Mode == MotionMode.Off)
		{
			_tileViewport.Height = target;
			return;
		}
		DoubleAnimation height = new DoubleAnimation(_tileViewport.ActualHeight > 0.0 ? _tileViewport.ActualHeight : _tileViewport.Height, target, MetroDuration(165))
		{
			EasingFunction = Motion.Ease(Motion.Cat.Navigation)
		};
		height.Completed += delegate { _tileViewport.Height = target; _tileViewport.BeginAnimation(FrameworkElement.HeightProperty, null); };
		_tileViewport.BeginAnimation(FrameworkElement.HeightProperty, height, HandoffBehavior.SnapshotAndReplace);
	}

	private void RefreshMetroTileStates(bool animate)
	{
		foreach (MetroTile tile in _metroTiles)
		{
			try { tile.Status.Text = tile.StatusText(); } catch { tile.Status.Text = string.Empty; }
			if (tile.Id == "wifi") UpdateWifiTileIcon(tile);
			ApplyMetroTileVisual(tile, animate && !_qaRendering, tile.Root.IsMouseOver);
		}
	}

	// The Wi-Fi quick tile is a RADIO toggle, so its glyph reflects the real Wi-Fi radio + (when Wi-Fi is the active
	// link) the live NetState81 signal/limited state — never the old hardcoded "connected 4-bar". It uses the one
	// canonical renderer (NetIcons81), the same as the tray/flyout/charms, so a single network state maps to a
	// single icon everywhere.
	private void UpdateWifiTileIcon(MetroTile tile)
	{
		if (_lastNet == null) return;   // keep the initial glyph until the first real snapshot — avoids a red-x flash on open
		if (tile.Icon is System.Windows.Controls.Canvas canvas)
		{
			foreach (UIElement child in canvas.Children)
			{
				if (child is System.Windows.Controls.Image image)
				{
					image.Source = WifiTileIcon();
					break;
				}
			}
		}
	}

	private System.Windows.Media.ImageSource WifiTileIcon()
	{
		if (!_wifiOn)
		{
			// Radio off -> dimmed Wi-Fi arcs + red badge (the reference set's "Wi-Fi Off").
			return NetIcons81.Draw(NetworkIconKind.Wifi, NetworkIconState.NotConnected, 0, 32);
		}
		NetState81? net = _lastNet;
		if (net != null && net.Kind == NetKind.Wifi)
		{
			// Wi-Fi is the active link -> real signal bars + limited/no-internet overlay.
			return NetIcons81.For(net, 32);
		}
		// Radio on but connected via something else (or nothing) -> idle Wi-Fi arcs, no bars.
		return NetIcons81.Draw(NetworkIconKind.Wifi, NetworkIconState.Connected, 0, 32);
	}

	private void ApplyMetroTileVisual(MetroTile tile, bool animate, bool hover)
	{
		bool on = false;
		try { on = tile.IsOn?.Invoke() ?? false; } catch { }
		tile.Check.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
		System.Windows.Media.Color target = Mix(tile.BaseColor, System.Windows.Media.Colors.Black, on ? 0.0 : 0.10);
		if (hover) target = Mix(target, System.Windows.Media.Colors.White, 0.11);
		if (!animate)
		{
			tile.Fill.BeginAnimation(SolidColorBrush.ColorProperty, null);
			tile.Fill.Color = target;
			return;
		}
		tile.Fill.BeginAnimation(SolidColorBrush.ColorProperty, new ColorAnimation(target, MetroDuration(TileHoverMilliseconds))
		{
			EasingFunction = Motion.Ease(Motion.Cat.Hover)
		}, HandoffBehavior.SnapshotAndReplace);
	}

	private static void AnimateScale(ScaleTransform transform, double target, int milliseconds)
	{
		Duration duration = MetroDuration(milliseconds);
		DoubleAnimation animation = new DoubleAnimation(target, duration) { EasingFunction = Motion.Ease(Motion.Cat.Press) };
		transform.BeginAnimation(ScaleTransform.ScaleXProperty, animation, HandoffBehavior.SnapshotAndReplace);
		transform.BeginAnimation(ScaleTransform.ScaleYProperty, animation, HandoffBehavior.SnapshotAndReplace);
	}

	private Task CycleMetroBrightness()
	{
		if (_brightness == null || !_brightnessAvailable)
		{
			return OpenExternal("Brightness", DisplaySettingsTarget);
		}
		double current = _brightness.Value;
		double next = current >= 99.0 ? 25.0 : Math.Min(100.0, (Math.Floor(current / 25.0) + 1.0) * 25.0);
		_brightness.Value = next;
		_brightness.Focus();
		try { OsdService.ShowBrightness((int)next); } catch { }
		_brightnessRow.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0.65, 1.0, MetroDuration(130)));
		return Task.CompletedTask;
	}

	private Task OpenMetroPersonalize()
	{
		DismissMetroPanel();
		if (System.Windows.Application.Current is App app && app.TryShowStartPersonalize())
		{
			return Task.CompletedTask;
		}
		return OpenExternal("Theme", PersonalizationSettingsTarget, ColorsSettingsTarget);
	}

	private Task OpenMetroMaps()
	{
		return OpenExternal("Maps", GoogleMapsTarget);
	}

	private void ShowMetroPanel()
	{
		_dismissing = false;
		int generation = ++_animationGeneration;
		PlaceOnEdge();
		ApplyMetroTheme();
		_quiet = NativeBanner.IsSuppressed();
		_projectConnected = System.Windows.Forms.Screen.AllScreens.Length > 1;
		RefreshMetroTileStates(animate: false);
		_metroRoot.BeginAnimation(UIElement.OpacityProperty, null);
		_slide.BeginAnimation(TranslateTransform.XProperty, null);
		_slide.X = _offScreen;
		_metroRoot.Opacity = Motion.Mode == MotionMode.Off ? 1.0 : 0.94;
		_metroRoot.CacheMode = Motion.Mode == MotionMode.Off ? null : new BitmapCache();
		if (!base.IsVisible) Show();
		base.Left = _finalLeft;
		// Robustly take the real foreground (AttachThreadInput + SetForegroundWindow), like the Start screen. Plain
		// Activate() is subject to Windows' foreground-lock and INTERMITTENTLY leaves the flyout visible-but-inactive,
		// so the FIRST click on a slider/tile only activates the window (gets eaten) and needs a second click.
		WindowUtil.ForceForeground(this);
		if (Motion.Mode == MotionMode.Off)
		{
			_slide.X = 0.0;
			_metroRoot.CacheMode = null;
		}
		else
		{
			DoubleAnimation slide = new DoubleAnimation(_offScreen, 0.0, MetroDuration(AuthenticOpenMilliseconds)) { EasingFunction = Motion.Ease(Motion.Cat.EdgeEnter), FillBehavior = FillBehavior.Stop };
			DoubleAnimation fade = new DoubleAnimation(0.94, 1.0, MetroDuration(AuthenticOpenMilliseconds)) { EasingFunction = Motion.Ease(Motion.Cat.EdgeEnter), FillBehavior = FillBehavior.Stop };
			slide.Completed += delegate
			{
				if (generation != _animationGeneration || _dismissing) return;
				_slide.X = 0.0;
				_metroRoot.Opacity = 1.0;
				_metroRoot.CacheMode = null;
			};
			_slide.BeginAnimation(TranslateTransform.XProperty, slide, HandoffBehavior.SnapshotAndReplace);
			_metroRoot.BeginAnimation(UIElement.OpacityProperty, fade, HandoffBehavior.SnapshotAndReplace);
		}
		StartMetroRefresh();
	}

	// One idempotent "hide and fully reset" primitive. The window's only real Hide() used to live SOLELY
	// inside the close animation's Completed callback; if that clock was ever abandoned (a dual-monitor focus
	// race, or re-opening mid-close), Completed never fired, _dismissing stayed latched true forever, the
	// translucent full-height window lingered on screen, and EVERY dismiss path became a no-op - so the only
	// way out was exiting the launcher. Routing every hide through this, plus the fallback timer and the
	// force-close-on-repeat below, makes a dropped Completed harmless. Safe to call more than once.
	private void HardHide()
	{
		StopMetroRefresh();
		SmoothScroll.Stop(_notificationScroll);
		SmoothScroll.Stop(_tileScroll);
		Hide();
		_slide.BeginAnimation(TranslateTransform.XProperty, null);
		_metroRoot.BeginAnimation(UIElement.OpacityProperty, null);
		_slide.X = 0.0;
		_metroRoot.Opacity = 1.0;
		_metroRoot.CacheMode = null;
		_dismissing = false;
	}

	private void DismissMetroPanel()
	{
		if (!base.IsVisible) return;
		if (_dismissing) { HardHide(); return; }   // a repeat dismiss while the close is mid-flight (or stuck) forces the hide
		_dismissing = true;
		int generation = ++_animationGeneration;
		StopMetroRefresh();
		SmoothScroll.Stop(_notificationScroll);
		SmoothScroll.Stop(_tileScroll);
		if (Motion.Mode == MotionMode.Off)
		{
			HardHide();
			return;
		}
		_metroRoot.CacheMode = new BitmapCache();
		DoubleAnimation slide = new DoubleAnimation(_offScreen, MetroDuration(AuthenticCloseMilliseconds)) { EasingFunction = Motion.Ease(Motion.Cat.EdgeExit) };
		DoubleAnimation fade = new DoubleAnimation(0.94, MetroDuration(AuthenticCloseMilliseconds)) { EasingFunction = Motion.Ease(Motion.Cat.EdgeExit) };
		slide.Completed += delegate
		{
			if (generation != _animationGeneration || !_dismissing) return;
			HardHide();
		};
		_slide.BeginAnimation(TranslateTransform.XProperty, slide, HandoffBehavior.SnapshotAndReplace);
		_metroRoot.BeginAnimation(UIElement.OpacityProperty, fade, HandoffBehavior.SnapshotAndReplace);
		// Safety net: if that Completed is ever dropped (interrupted animation clock on a dual-monitor focus
		// race), guarantee the hide anyway. The generation + _dismissing checks make it a no-op when the panel
		// was already hidden by Completed or re-opened in the meantime. 800ms is safely past any close anim.
		System.Windows.Threading.DispatcherTimer fallback = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(800.0) };
		fallback.Tick += delegate
		{
			fallback.Stop();
			if (generation == _animationGeneration && _dismissing) HardHide();
		};
		fallback.Start();
	}

	private void StartMetroRefresh()
	{
		StopMetroRefresh();
		_runtimeCts = new CancellationTokenSource();
		int generation = ++_runtimeGeneration;
		CancellationToken token = _runtimeCts.Token;
		_ = RefreshMetroRuntimeAsync(generation, token);
		_ = RefreshMetroNotificationsAsync(generation, token);
	}

	private void StopMetroRefresh()
	{
		CancellationTokenSource? cts = _runtimeCts;
		_runtimeCts = null;
		if (cts == null) return;
		try { cts.Cancel(); } catch { }
		cts.Dispose();
	}

	private async Task RefreshMetroRuntimeAsync(int generation, CancellationToken token)
	{
		try
		{
			Task<(float volume, bool muted, int brightness, (bool present, int percent, bool charging, bool saver) power, NetState81 network)> basicTask = Task.Run(() =>
			{
				AudioController audio = new AudioController();
				return (audio.GetVolume(), audio.GetMute(), SafeGetBrightness(), PowerStatus.Read(), NetState81.Read());
			}, token);
			Task<(bool readable, bool wifi, bool bluetooth, bool airplane)> radioTask = ReadMetroRadiosAsync();
			await Task.WhenAll(basicTask, radioTask);
			token.ThrowIfCancellationRequested();
			var basic = await basicTask;
			var radios = await radioTask;
			MetroRuntimeSnapshot snapshot = new MetroRuntimeSnapshot(
				basic.volume,
				basic.muted,
				basic.brightness,
				basic.power.present,
				basic.power.saver,
				basic.network,
				radios.readable,
				radios.wifi,
				radios.bluetooth,
				radios.airplane);
			if (generation != _runtimeGeneration || token.IsCancellationRequested || !base.IsVisible) return;
			ApplyMetroRuntimeSnapshot(snapshot);
		}
		catch (OperationCanceledException) { }
		catch (Exception ex) { Logger.Log("Action Center state refresh failed: " + ex.Message); }
	}

	private static async Task<(bool readable, bool wifi, bool bluetooth, bool airplane)> ReadMetroRadiosAsync()
	{
		try
		{
			if (await Radio.RequestAccessAsync() != RadioAccessStatus.Allowed) return (false, false, false, false);
			IReadOnlyList<Radio> radios = await Radio.GetRadiosAsync();
			Radio? wifi = radios.FirstOrDefault(r => r.Kind == RadioKind.WiFi);
			Radio? bluetooth = radios.FirstOrDefault(r => r.Kind == RadioKind.Bluetooth);
			bool airplane = radios.Count > 0 && radios.All(r => r.State != RadioState.On);
			return (true, wifi?.State == RadioState.On, bluetooth?.State == RadioState.On, airplane);
		}
		catch { return (false, false, false, false); }
	}

	private void ApplyMetroRuntimeSnapshot(MetroRuntimeSnapshot snapshot)
	{
		_syncingSliders = true;
		if (_volume != null) _volume.Value = Math.Round(snapshot.Volume * 100f);
		_volumeValueText.Text = snapshot.Muted ? "Muted" : Math.Round(snapshot.Volume * 100f).ToString();
		if (snapshot.Brightness >= 0 && _brightness != null) _brightness.Value = snapshot.Brightness;
		_brightnessValueText.Text = snapshot.Brightness >= 0 ? snapshot.Brightness.ToString() : "—";
		_syncingSliders = false;
		// #4: reflect the real volume level/mute (and brightness level) on the slider icons every time the panel syncs.
		UpdateVolumeSliderIcon((int)Math.Round(snapshot.Volume * 100f), snapshot.Muted);
		if (snapshot.Brightness >= 0) UpdateBrightnessSliderIcon(snapshot.Brightness);
		_isLaptop = snapshot.BatteryPresent;
		_brightnessAvailable = snapshot.Brightness >= 0 && (_isLaptop || snapshot.Brightness >= 0);
		_batterySaver = snapshot.BatterySaver;
		_batteryStatus = _batterySaver ? "On" : "Off";
		_projectConnected = System.Windows.Forms.Screen.AllScreens.Length > 1;
		_lastNet = snapshot.Network;
		if (snapshot.RadioReadable)
		{
			_wifiOn = snapshot.WifiOn;
			_btOn = snapshot.BluetoothOn;
			_airplaneOn = snapshot.AirplaneOn;
		}
		else
		{
			_wifiOn = snapshot.Network.Kind == NetKind.Wifi || snapshot.Network.Connected;
			_airplaneOn = snapshot.Network.Kind == NetKind.Airplane;
		}
		_wifiStatus = snapshot.Network.Kind == NetKind.Wifi
			? snapshot.Network.Label
			: (_wifiOn ? "On" : "Off");
		_bluetoothStatus = _btOn ? "On" : "Off";
		_airplaneStatus = _airplaneOn ? "On" : "Off";
		_brightnessRow.Visibility = _brightnessAvailable ? Visibility.Visible : Visibility.Collapsed;
		ArrangeMetroTiles(animateHeight: true);
		RefreshMetroTileStates(animate: true);
	}

	private async Task RefreshMetroNotificationsAsync(int generation, CancellationToken token)
	{
		try
		{
			UserNotificationListener listener = UserNotificationListener.Current;
			if (await listener.RequestAccessAsync() != UserNotificationListenerAccessStatus.Allowed)
			{
				if (generation == _runtimeGeneration && !token.IsCancellationRequested) ShowMetroEmpty("Notification access is disabled");
				return;
			}
			IReadOnlyList<UserNotification> notifications = await listener.GetNotificationsAsync(NotificationKinds.Toast);
			token.ThrowIfCancellationRequested();
			List<(uint id, string app, string title, string body, string aumid)> metadata = new();
			foreach (UserNotification notification in notifications)
			{
				string app = string.Empty;
				string aumid = string.Empty;
				string title = string.Empty;
				string body = string.Empty;
				try
				{
					app = notification.AppInfo?.DisplayInfo?.DisplayName ?? string.Empty;
					aumid = notification.AppInfo?.AppUserModelId ?? string.Empty;
					IReadOnlyList<AdaptiveNotificationText>? texts = notification.Notification.Visual.GetBinding(KnownNotificationBindings.ToastGeneric)?.GetTextElements();
					if (texts != null && texts.Count > 0)
					{
						title = texts[0].Text;
						if (texts.Count > 1) body = texts[1].Text;
					}
				}
				catch { }
				metadata.Add((notification.Id, app, title, body, aumid));
			}
			List<(uint id, string app, string title, string body, string aumid, ImageSource? icon)> rows = await Task.Run(() => metadata.Select(item =>
				(item.id, item.app, item.title, item.body, item.aumid, ResolveNotifIcon(item.aumid))).ToList(), token);
			if (generation != _runtimeGeneration || token.IsCancellationRequested || !base.IsVisible) return;
			ShowMetroNotifications(rows);
		}
		catch (OperationCanceledException) { }
		catch (Exception ex)
		{
			Logger.Log("Action Center notifications refresh failed: " + ex.Message);
			if (generation == _runtimeGeneration && !token.IsCancellationRequested) ShowMetroEmpty("Notifications are unavailable");
		}
	}

	private void ShowMetroNotifications(List<(uint id, string app, string title, string body, string aumid, ImageSource? icon)>? rows)
	{
		_notifs.Children.Clear();
		_notificationCount = rows?.Count ?? 0;
		_clearAllButton.IsEnabled = _notificationCount > 0;
		_clearAllButton.Opacity = _notificationCount > 0 ? 1.0 : 0.55;
		if (rows == null || rows.Count == 0)
		{
			ShowMetroEmpty("You're all caught up!");
			return;
		}
		_emptyState.Visibility = Visibility.Collapsed;
		_notificationScroll.Visibility = Visibility.Visible;
		int index = 0;
		foreach (var row in rows)
		{
			Border card = CreateMetroNotificationCard(row.id, row.app, row.title, row.body, row.aumid, row.icon);
			_notifs.Children.Add(card);
			if (!_qaRendering && base.IsVisible)
			{
				TranslateTransform translate = new TranslateTransform(22.0, 0.0);
				card.RenderTransform = translate;
				card.Opacity = 0.0;
				TimeSpan delay = TimeSpan.FromMilliseconds(Math.Min(index, 5) * 18.0 * Motion.Factor);
				DoubleAnimation x = new DoubleAnimation(22.0, 0.0, MetroDuration(NotificationMilliseconds)) { EasingFunction = Motion.Ease(Motion.Cat.ViewEnter), BeginTime = delay };
				DoubleAnimation opacity = new DoubleAnimation(0.0, 1.0, MetroDuration(NotificationMilliseconds)) { EasingFunction = Motion.Ease(Motion.Cat.ViewEnter), BeginTime = delay };
				x.Completed += delegate { card.Opacity = 1.0; translate.X = 0.0; };
				translate.BeginAnimation(TranslateTransform.XProperty, x);
				card.BeginAnimation(UIElement.OpacityProperty, opacity);
			}
			index++;
		}
	}

	private Border CreateMetroNotificationCard(uint id, string app, string title, string body, string aumid, ImageSource? icon)
	{
		SolidColorBrush fill = new SolidColorBrush(System.Windows.Media.Color.FromArgb(42, 255, 255, 255));
		Border card = new Border
		{
			Background = fill,
			Margin = new Thickness(0.0, 0.0, 6.0, 7.0),
			MinHeight = 68.0,
			Cursor = string.IsNullOrWhiteSpace(aumid) ? System.Windows.Input.Cursors.Arrow : System.Windows.Input.Cursors.Hand,
			CornerRadius = new CornerRadius(0.0)
		};
		Grid grid = new Grid();
		grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(4.0) });
		grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
		grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.0, GridUnitType.Star) });
		Border stripe = new Border { Background = new SolidColorBrush(_themeAccent) };
		grid.Children.Add(stripe);
		if (icon != null)
		{
			System.Windows.Controls.Image image = new System.Windows.Controls.Image
			{
				Source = icon,
				Width = 34.0,
				Height = 34.0,
				Margin = new Thickness(10.0, 0.0, 0.0, 0.0),
				VerticalAlignment = VerticalAlignment.Center
			};
			RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
			Grid.SetColumn(image, 1);
			grid.Children.Add(image);
		}
		StackPanel text = new StackPanel { Margin = new Thickness(11.0, 9.0, 32.0, 9.0), VerticalAlignment = VerticalAlignment.Center };
		if (!string.IsNullOrWhiteSpace(app))
		{
			text.Children.Add(new TextBlock
			{
				Text = app.ToUpperInvariant(),
				Foreground = _linkBrush,
				FontFamily = new System.Windows.Media.FontFamily("Segoe UI Semibold"),
				FontSize = 10.0
			});
		}
		if (!string.IsNullOrWhiteSpace(title))
		{
			text.Children.Add(new TextBlock
			{
				Text = title,
				Foreground = System.Windows.Media.Brushes.White,
				FontFamily = new System.Windows.Media.FontFamily("Segoe UI Semibold"),
				FontSize = 13.0,
				TextTrimming = TextTrimming.CharacterEllipsis
			});
		}
		if (!string.IsNullOrWhiteSpace(body))
		{
			text.Children.Add(new TextBlock
			{
				Text = body,
				Foreground = _secondaryTextBrush,
				FontFamily = new System.Windows.Media.FontFamily("Segoe UI"),
				FontSize = 12.0,
				TextWrapping = TextWrapping.Wrap,
				MaxHeight = 50.0
			});
		}
		Grid.SetColumn(text, 2);
		grid.Children.Add(text);
		Border close = new Border
		{
			Width = 28.0,
			Height = 28.0,
			HorizontalAlignment = HorizontalAlignment.Right,
			VerticalAlignment = VerticalAlignment.Top,
			Background = System.Windows.Media.Brushes.Transparent,
			Cursor = System.Windows.Input.Cursors.Hand,
			Visibility = Visibility.Hidden,
			ToolTip = "Dismiss",
			Child = CreateMetroIcon("close", 13.0)
		};
		close.MouseEnter += delegate { close.Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(210, 25, 50)); };
		close.MouseLeave += delegate { close.Background = System.Windows.Media.Brushes.Transparent; };
		close.MouseLeftButtonUp += delegate(object _, MouseButtonEventArgs e) { e.Handled = true; DismissOne(id, card); };
		Grid.SetColumn(close, 2);
		grid.Children.Add(close);
		card.MouseEnter += delegate
		{
			close.Visibility = Visibility.Visible;
			fill.BeginAnimation(SolidColorBrush.ColorProperty, new ColorAnimation(System.Windows.Media.Color.FromArgb(62, 255, 255, 255), MetroDuration(TileHoverMilliseconds)));
		};
		card.MouseLeave += delegate
		{
			close.Visibility = Visibility.Hidden;
			fill.BeginAnimation(SolidColorBrush.ColorProperty, new ColorAnimation(System.Windows.Media.Color.FromArgb(42, 255, 255, 255), MetroDuration(TileHoverMilliseconds)));
		};
		if (!string.IsNullOrWhiteSpace(aumid))
		{
			card.MouseLeftButtonUp += delegate { Launch("shell:AppsFolder\\" + aumid); DismissMetroPanel(); };
		}
		card.Child = grid;
		return card;
	}

	private void ShowMetroEmpty(string subtitle)
	{
		_notificationCount = 0;
		_notifs.Children.Clear();
		_emptySubtitle.Text = subtitle;
		_emptyState.Visibility = Visibility.Visible;
		_notificationScroll.Visibility = Visibility.Collapsed;
		_clearAllButton.IsEnabled = false;
		_clearAllButton.Opacity = 0.55;
	}

	private async Task DismissMetroNotificationAsync(uint id, Border card)
	{
		if (_qaRendering)
		{
			_notifs.Children.Remove(card);
			_notificationCount = _notifs.Children.Count;
			if (_notificationCount == 0) ShowMetroEmpty("You're all caught up!");
			return;
		}
		TranslateTransform translate = card.RenderTransform as TranslateTransform ?? new TranslateTransform();
		card.RenderTransform = translate;
		TaskCompletionSource completion = new TaskCompletionSource();
		DoubleAnimation opacity = new DoubleAnimation(0.0, MetroDuration(130));
		DoubleAnimation x = new DoubleAnimation(18.0, MetroDuration(130)) { EasingFunction = Motion.Ease(Motion.Cat.ViewExit) };
		opacity.Completed += delegate { completion.TrySetResult(); };
		card.BeginAnimation(UIElement.OpacityProperty, opacity);
		translate.BeginAnimation(TranslateTransform.XProperty, x);
		await completion.Task;
		_notifs.Children.Remove(card);
		_notificationCount = _notifs.Children.Count;
		if (_notificationCount == 0) ShowMetroEmpty("You're all caught up!");
		try
		{
			UserNotificationListener listener = UserNotificationListener.Current;
			if (await listener.RequestAccessAsync() == UserNotificationListenerAccessStatus.Allowed) listener.RemoveNotification(id);
		}
		catch { }
	}

	private async Task ClearMetroNotificationsAsync()
	{
		if (_notificationCount == 0) return;
		if (!_qaRendering)
		{
			TranslateTransform translate = _notifs.RenderTransform as TranslateTransform ?? new TranslateTransform();
			_notifs.RenderTransform = translate;
			TaskCompletionSource completion = new TaskCompletionSource();
			DoubleAnimation opacity = new DoubleAnimation(0.0, MetroDuration(ClearAllMilliseconds));
			DoubleAnimation y = new DoubleAnimation(-8.0, MetroDuration(ClearAllMilliseconds)) { EasingFunction = Motion.Ease(Motion.Cat.ViewExit) };
			opacity.Completed += delegate { completion.TrySetResult(); };
			_notifs.BeginAnimation(UIElement.OpacityProperty, opacity);
			translate.BeginAnimation(TranslateTransform.YProperty, y);
			await completion.Task;
		}
		ShowMetroEmpty("You're all caught up!");
		_notifs.BeginAnimation(UIElement.OpacityProperty, null);
		_notifs.Opacity = 1.0;
		_notifs.RenderTransform = null;
		if (_qaRendering) return;
		try
		{
			UserNotificationListener listener = UserNotificationListener.Current;
			if (await listener.RequestAccessAsync() == UserNotificationListenerAccessStatus.Allowed)
			{
				try { listener.ClearNotifications(); }
				catch
				{
					foreach (UserNotification notification in await listener.GetNotificationsAsync(NotificationKinds.Toast))
					{
						try { listener.RemoveNotification(notification.Id); } catch { }
					}
				}
			}
		}
		catch { }
	}

	private void ConfigureMetroLayout(double width, double height)
	{
		bool compact = width < 420.0 || height < 740.0;
		_tileHeight = compact ? 84.0 : 96.0;
		_headerTitle.FontSize = compact ? 31.0 : 38.0;
		_notificationsHeading.FontSize = compact ? 19.0 : 22.0;
		_sliderHost.Margin = compact ? new Thickness(16.0, 0.0, 16.0, 3.0) : new Thickness(20.0, 0.0, 20.0, 5.0);
		_brightnessRow.Visibility = _brightnessAvailable ? Visibility.Visible : Visibility.Collapsed;
		ArrangeMetroTiles(animateHeight: false);
	}

	private void ApplyMetroTheme()
	{
		System.Windows.Media.Color accent = StartAccent.Color();
		System.Windows.Media.Color themeBase = accent;
		try
		{
			AppSettings settings = SettingsStore.Load();
			if (settings.StartBgMode == "pattern" && !string.IsNullOrWhiteSpace(settings.StartBgColor))
			{
				themeBase = (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(settings.StartBgColor);
			}
		}
		catch { }
		ApplyMetroPalette(themeBase, accent);
	}

	private void ApplyMetroPalette(System.Windows.Media.Color themeBase, System.Windows.Media.Color accent)
	{
		_themeBase = themeBase;
		_themeAccent = accent;
		System.Windows.Media.Color top = EnsureMetroSaturation(Mix(themeBase, accent, 0.28));
		System.Windows.Media.Color bottom = Mix(top, System.Windows.Media.Colors.Black, 0.32);
		_themeBackground = new LinearGradientBrush(top, bottom, new Point(0.0, 0.0), new Point(1.0, 1.0));
		_themeBackground.Freeze();
		base.Background = _themeBackground;
		_metroRoot.Background = _themeBackground;
		_secondaryTextBrush = FreezeMetroBrush(System.Windows.Media.Color.FromArgb(205, 255, 255, 255));
		_linkBrush = FreezeMetroBrush(Mix(accent, System.Windows.Media.Colors.White, 0.56));
		_clearAllButton.Foreground = _linkBrush;
		_emptySubtitle.Foreground = _secondaryTextBrush;
		_collapseLabel.Foreground = System.Windows.Media.Brushes.White;
		if (_volume is MetroSlider volume)
		{
			volume.FillBrush = FreezeMetroBrush(accent);   // pattern 19: the filled side is the ACCENT, not a whitened mix
			volume.TrackBrush = FreezeMetroBrush(System.Windows.Media.Color.FromArgb(58, 255, 255, 255));
			volume.InvalidateVisual();
		}
		if (_brightness is MetroSlider brightness)
		{
			brightness.FillBrush = FreezeMetroBrush(accent);
			brightness.TrackBrush = FreezeMetroBrush(System.Windows.Media.Color.FromArgb(58, 255, 255, 255));
			brightness.InvalidateVisual();
		}
		RefreshMetroTileStates(animate: false);
	}

	private void OnMetroNotificationWheel(object sender, MouseWheelEventArgs e)
	{
		if (_notificationScroll.ScrollableHeight <= 0.5 || e.Delta == 0) return;
		e.Handled = true;
		double step = Math.Clamp(_notificationScroll.ViewportHeight * 0.22, 52.0, 82.0);
		SmoothScroll.ByVertical(_notificationScroll, -(e.Delta / 120.0) * step);
	}

	private void OnMetroNotificationPointerDown(object sender, MouseButtonEventArgs e)
	{
		if (e.ChangedButton == MouseButton.Left) SmoothScroll.StopVertical(_notificationScroll);
	}

	private void OnMetroTileWheel(object sender, MouseWheelEventArgs e)
	{
		if (_tileScroll.ScrollableHeight <= 0.5 || e.Delta == 0) return;
		e.Handled = true;
		double step = Math.Clamp(_tileScroll.ViewportHeight * 0.35, 60.0, 96.0);
		SmoothScroll.ByVertical(_tileScroll, -(e.Delta / 120.0) * step);
	}

	private static bool IsNightLightSupported()
	{
		return Environment.OSVersion.Version.Major >= 10;
	}

	private static Duration MetroDuration(int authenticMilliseconds)
	{
		return new Duration(TimeSpan.FromMilliseconds(Math.Max(1.0, authenticMilliseconds * Motion.Factor)));
	}

	private static System.Windows.Media.Color Mix(System.Windows.Media.Color from, System.Windows.Media.Color to, double amount)
	{
		amount = Math.Clamp(amount, 0.0, 1.0);
		byte blend(byte a, byte b) => (byte)Math.Round(a + (b - a) * amount);
		return System.Windows.Media.Color.FromArgb(blend(from.A, to.A), blend(from.R, to.R), blend(from.G, to.G), blend(from.B, to.B));
	}

	private static System.Windows.Media.Color EnsureMetroSaturation(System.Windows.Media.Color color)
	{
		byte max = Math.Max(color.R, Math.Max(color.G, color.B));
		byte min = Math.Min(color.R, Math.Min(color.G, color.B));
		if (max - min >= 50) return Mix(color, System.Windows.Media.Colors.White, 0.03);
		return System.Windows.Media.Color.FromRgb((byte)Math.Clamp(color.R + 8, 0, 255), (byte)Math.Clamp(color.G + 18, 0, 255), (byte)Math.Clamp(color.B + 28, 0, 255));
	}

	private static SolidColorBrush FreezeMetroBrush(System.Windows.Media.Color color)
	{
		SolidColorBrush brush = new SolidColorBrush(color);
		brush.Freeze();
		return brush;
	}

	private void StyleMetroScrollBars()
	{
		if (SystemParameters.HighContrast) return;
		try
		{
			Style style = new Style(typeof(System.Windows.Controls.Primitives.ScrollBar));
			style.Setters.Add(new Setter(FrameworkElement.WidthProperty, 14.0));
			style.Setters.Add(new Setter(Control.BackgroundProperty, new DynamicResourceExtension("Metro81.ScrollTrackBrushDark")));
			style.Setters.Add(new Setter(Control.TemplateProperty,
				(ControlTemplate)System.Windows.Markup.XamlReader.Parse(MetroScrollBarTemplateXaml)));
			_metroRoot.Resources[typeof(System.Windows.Controls.Primitives.ScrollBar)] = style;
		}
		catch (Exception ex)
		{
			Logger.Log("Action Center scrollbar style failed: " + ex.Message);
		}
	}

	private const string MetroScrollBarTemplateXaml =
		"<ControlTemplate TargetType=\"ScrollBar\" xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\" xmlns:x=\"http://schemas.microsoft.com/winfx/2006/xaml\">" +
		"<Grid Width=\"14\" Background=\"{DynamicResource Metro81.ScrollTrackBrushDark}\" SnapsToDevicePixels=\"True\">" +
		"<Track x:Name=\"PART_Track\" Orientation=\"Vertical\" IsDirectionReversed=\"True\">" +
		"<Track.DecreaseRepeatButton><RepeatButton Command=\"ScrollBar.PageUpCommand\" Opacity=\"0\" Focusable=\"False\"/></Track.DecreaseRepeatButton>" +
		"<Track.Thumb><Thumb MinHeight=\"28\" Focusable=\"False\" Cursor=\"Hand\"><Thumb.Template><ControlTemplate TargetType=\"Thumb\"><Grid Background=\"Transparent\"><Border x:Name=\"ThumbVisual\" Width=\"5\" Margin=\"0,2\" Background=\"{DynamicResource Metro81.ScrollThumbBrushDark}\" HorizontalAlignment=\"Center\"/></Grid><ControlTemplate.Triggers><Trigger Property=\"IsMouseOver\" Value=\"True\"><Setter TargetName=\"ThumbVisual\" Property=\"Background\" Value=\"{DynamicResource Metro81.ScrollThumbHoverBrushDark}\"/></Trigger><Trigger Property=\"IsDragging\" Value=\"True\"><Setter TargetName=\"ThumbVisual\" Property=\"Background\" Value=\"{DynamicResource Metro81.ScrollThumbPressedBrushDark}\"/></Trigger></ControlTemplate.Triggers></ControlTemplate></Thumb.Template></Thumb></Track.Thumb>" +
		"<Track.IncreaseRepeatButton><RepeatButton Command=\"ScrollBar.PageDownCommand\" Opacity=\"0\" Focusable=\"False\"/></Track.IncreaseRepeatButton>" +
		"</Track></Grid></ControlTemplate>";

	private static FrameworkElement CreateMetroIcon(string id, double size)
	{
		Canvas canvas = new Canvas { Width = 24.0, Height = 24.0, SnapsToDevicePixels = true };
		void stroke(string data, double thickness = 1.6) => canvas.Children.Add(CreateStrokePath(data, thickness));
		void fill(string data) => canvas.Children.Add(CreateFillPath(data));
		switch (id)
		{
			case "wifi":
				canvas.Children.Add(new Image
				{
					Width = 24.0,
					Height = 24.0,
					Stretch = Stretch.Uniform,
					SnapsToDevicePixels = true,
					Source = Win81AssetResolver.GetAsset("Wifi.Bars5", 32) ?? NetIcons81.Draw(NetworkIconKind.Wifi, NetworkIconState.Connected, 4, 32)
				});
				break;
			case "bluetooth":
				stroke("M 9.5,2.2 L 16,8 L 11.2,12 L 16,16 L 9.5,21.8 L 9.5,2.2 M 9.5,12 L 4.5,7.2 M 9.5,12 L 4.5,16.8", 1.8);
				break;
			case "brightness":
				canvas.Children.Add(new Ellipse { Width = 7.6, Height = 7.6, Stroke = System.Windows.Media.Brushes.White, StrokeThickness = 1.6 });
				Canvas.SetLeft(canvas.Children[^1], 8.2); Canvas.SetTop(canvas.Children[^1], 8.2);
				stroke("M 12,1.5 L 12,5 M 12,19 L 12,22.5 M 1.5,12 L 5,12 M 19,12 L 22.5,12 M 4.6,4.6 L 7.1,7.1 M 16.9,16.9 L 19.4,19.4 M 19.4,4.6 L 16.9,7.1 M 7.1,16.9 L 4.6,19.4", 1.6);
				break;
			case "airplane":
				canvas.Children.Add(new Image
				{
					Width = 24.0,
					Height = 24.0,
					Stretch = Stretch.Uniform,
					SnapsToDevicePixels = true,
					Source = Win81AssetResolver.GetAsset("Network.Airplane", 32) ?? NetIcons81.Draw(NetworkIconKind.Wifi, NetworkIconState.Airplane, 0, 32)
				});
				break;
			case "quiet":
				fill("M 16.5,17 C 11,18 7,14 8,8.5 C 4.5,10 3.5,14.5 5.8,18 C 8.5,22 14.5,22 18,18 Z");
				break;
			case "theme":
				stroke("M 2.2,4 L 18.2,4 L 18.2,15 L 2.2,15 Z M 7.2,19.5 L 13.2,19.5 M 10.2,15 L 10.2,19.5", 1.55);
				stroke("M 15.4,19.7 L 21.5,13.6 M 18.1,20.7 L 22,16.8", 1.75);
				break;
			case "maps":
				stroke("M 2.2,5.2 L 8.2,2.5 L 15.8,5.2 L 21.8,2.5 L 21.8,18.8 L 15.8,21.5 L 8.2,18.8 L 2.2,21.5 Z M 8.2,2.5 L 8.2,18.8 M 15.8,5.2 L 15.8,21.5", 1.45);
				canvas.Children.Add(new Ellipse { Width = 3.2, Height = 3.2, Fill = System.Windows.Media.Brushes.White });
				Canvas.SetLeft(canvas.Children[^1], 10.4); Canvas.SetTop(canvas.Children[^1], 9.0);
				break;
			case "project":
				stroke("M 2.2,3.2 L 21.8,3.2 L 21.8,16.8 L 2.2,16.8 Z M 7.8,21 L 16.2,21 M 12,16.8 L 12,21", 1.6);
				break;
			case "connect":
				stroke("M 1.5,4 L 14.5,4 L 14.5,13.5 L 1.5,13.5 Z M 5,17.5 L 11,17.5 M 8,13.5 L 8,17.5 M 12,9 L 22.5,9 L 22.5,18 L 12,18 Z", 1.45);
				break;
			case "battery":
				stroke("M 2.5,7 L 19.5,7 L 19.5,18 L 2.5,18 Z M 19.5,10 L 22,10 L 22,15 L 19.5,15", 1.6);
				fill("M 5,10 L 12.5,10 L 12.5,15 L 5,15 Z");
				break;
			case "settings":
				// Settings = the one canonical flat Metro cog (SettingsGlyph). NOT the sun/rays glyph, which is
				// reserved exclusively for display Brightness — the two functions must never look alike.
				canvas.Children.Add(new Image
				{
					Width = 24.0,
					Height = 24.0,
					Stretch = Stretch.Uniform,
					SnapsToDevicePixels = true,
					Source = SettingsGlyph.Gear()
				});
				break;
			case "power":
				stroke("M 12,1.5 L 12,11.5 M 6.3,5.3 C 2.4,8.5 2.5,14.8 6.4,18 C 10,21 15.5,20.4 18.5,16.8 C 21.5,13.1 20.6,8 17.7,5.3", 1.9);
				break;
			case "nightlight":
				fill("M 16.5,17 C 11,18 7,14 8,8.5 C 4.5,10 3.5,14.5 5.8,18 C 8.5,22 14.5,22 18,18 Z");
				stroke("M 18,2 L 18,5 M 14.7,3.3 L 16.3,4.9 M 21.3,3.3 L 19.7,4.9", 1.35);
				break;
			case "volume":
			{
				System.Windows.Media.ImageSource vimg = Win81AssetResolver.GetAsset("Volume.High", 32);
				if (vimg != null)
				{
					canvas.Children.Add(new Image { Width = 24.0, Height = 24.0, Stretch = Stretch.Uniform, SnapsToDevicePixels = true, Source = vimg });
				}
				else
				{
					fill("M 2,9 L 7,9 L 12,4 L 12,20 L 7,15 L 2,15 Z");
					stroke("M 15,8 C 18,10 18,14 15,16 M 18,5 C 22.5,8.8 22.5,15.2 18,19", 1.55);
				}
				break;
			}
			case "close":
				stroke("M 4,4 L 20,20 M 20,4 L 4,20", 2.2);
				break;
			default:
				stroke("M 4,4 L 20,4 L 20,20 L 4,20 Z", 1.8);
				break;
		}
		return new Viewbox
		{
			Width = size,
			Height = size,
			Stretch = Stretch.Uniform,
			SnapsToDevicePixels = true,
			UseLayoutRounding = true,
			Child = canvas
		};
	}

	private static Path CreateStrokePath(string data, double thickness)
	{
		Geometry geometry = Geometry.Parse(data);
		if (geometry.CanFreeze) geometry.Freeze();
		return new Path
		{
			Data = geometry,
			Stroke = System.Windows.Media.Brushes.White,
			StrokeThickness = thickness,
			StrokeStartLineCap = PenLineCap.Round,
			StrokeEndLineCap = PenLineCap.Round,
			StrokeLineJoin = PenLineJoin.Round
		};
	}

	private static Path CreateFillPath(string data)
	{
		Geometry geometry = Geometry.Parse(data);
		if (geometry.CanFreeze) geometry.Freeze();
		return new Path { Data = geometry, Fill = System.Windows.Media.Brushes.White };
	}

	internal static ActionCenterQaMetrics QaRenderMetro(string outPath, double width, double height, bool compact, bool withNotifications)
	{
		Stopwatch total = Stopwatch.StartNew();
		Stopwatch build = Stopwatch.StartNew();
		ActionCenter panel = new ActionCenter
		{
			Width = width,
			Height = height,
			Left = -5000.0,
			Top = -5000.0,
			Topmost = false
		};
		build.Stop();
		panel._qaRendering = true;
		panel._isLaptop = true;
		panel._brightnessAvailable = true;
		panel._wifiOn = true;
		panel._btOn = true;
		panel._airplaneOn = false;
		panel._quiet = false;
		panel._batterySaver = false;
		panel._projectConnected = false;
		panel._wifiStatus = "HomeNetwork";
		panel._bluetoothStatus = "Not connected";
		panel._airplaneStatus = "Off";
		panel._batteryStatus = "Off";
		panel._syncingSliders = true;
		if (panel._volume != null) panel._volume.Value = 100.0;
		if (panel._brightness != null) panel._brightness.Value = 100.0;
		panel._volumeValueText.Text = "100";
		panel._brightnessValueText.Text = "100";
		panel._syncingSliders = false;
		panel._expanded = !compact;
		panel._collapseLabel.Text = panel._expanded ? "Fewer settings" : "More settings";
		panel._collapseArrow.Direction = panel._expanded ? MetroArrowDirection81.Up : MetroArrowDirection81.Down;
		panel.ConfigureMetroLayout(width, height);
		System.Windows.Media.Color blue = System.Windows.Media.Color.FromRgb(0, 112, 192);
		panel.ApplyMetroPalette(blue, System.Windows.Media.Color.FromRgb(0, 174, 239));
		if (withNotifications)
		{
			panel.ShowMetroNotifications(new List<(uint, string, string, string, string, ImageSource?)>
			{
				(1u, "Mail", "Project update", "The latest build is ready for review.", string.Empty, null),
				(2u, "Calendar", "Design review", "Today at 17:30", string.Empty, null),
				(3u, "Windows Security", "No actions needed", "Your device is protected.", string.Empty, null),
				(4u, "Teams", "New message", "Alex sent you a message.", string.Empty, null),
				(5u, "Photos", "Import complete", "12 photos were imported.", string.Empty, null),
				(6u, "Store", "Updates installed", "Your apps are up to date.", string.Empty, null),
				(7u, "OneDrive", "Files synced", "All changes are available on this device.", string.Empty, null)
			});
		}
		else
		{
			panel.ShowMetroEmpty("You're all caught up!");
		}
		panel.Show();
		panel._slide.X = 0.0;
		panel._metroRoot.Opacity = 1.0;
		panel._metroRoot.CacheMode = null;
		FrameworkElement root = panel._metroRoot;
		root.ApplyTemplate();
		root.Measure(new System.Windows.Size(width, height));
		root.Arrange(new Rect(0.0, 0.0, width, height));
		root.UpdateLayout();
		RenderTargetBitmap bitmap = new RenderTargetBitmap((int)Math.Ceiling(width), (int)Math.Ceiling(height), 96.0, 96.0, PixelFormats.Pbgra32);
		bitmap.Render(root);
		PngBitmapEncoder encoder = new PngBitmapEncoder();
		encoder.Frames.Add(BitmapFrame.Create(bitmap));
		using (FileStream stream = File.Create(outPath)) encoder.Save(stream);
		System.Windows.Media.Color before = panel._themeBackground.GradientStops[0].Color;
		panel.ApplyMetroPalette(System.Windows.Media.Color.FromRgb(170, 24, 36), System.Windows.Media.Color.FromRgb(235, 50, 60));
		System.Windows.Media.Color red = panel._themeBackground.GradientStops[0].Color;
		panel.ApplyMetroPalette(blue, System.Windows.Media.Color.FromRgb(0, 174, 239));
		System.Windows.Media.Color after = panel._themeBackground.GradientStops[0].Color;
		panel._notificationScroll.ApplyTemplate();
		System.Windows.Controls.Primitives.ScrollBar? scrollBar = panel._notificationScroll.Template.FindName("PART_VerticalScrollBar", panel._notificationScroll) as System.Windows.Controls.Primitives.ScrollBar;
		double scrollWidth = scrollBar?.ActualWidth > 0.0 ? scrollBar.ActualWidth : 17.0;
		double scrollRange = panel._notificationScroll.ScrollableHeight;
		int notificationCards = panel._notifs.Children.Count;
		bool emptyStateVisible = panel._emptyState.Visibility == Visibility.Visible;
		bool originalExpanded = panel._expanded;
		double originalTileHeight = panel._tileViewport.Height;
		panel._collapseButton.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
		{
			RoutedEvent = UIElement.MouseLeftButtonUpEvent,
			Source = panel._collapseArrow
		});
		bool collapseChanged = panel._expanded != originalExpanded
			&& panel._collapseLabel.Text == (panel._expanded ? "Fewer settings" : "More settings")
			&& panel._collapseArrow.Direction == (panel._expanded ? MetroArrowDirection81.Up : MetroArrowDirection81.Down)
			&& Math.Abs(panel._tileViewport.Height - originalTileHeight) > 0.5;
		panel._collapseButton.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
		{
			RoutedEvent = UIElement.MouseLeftButtonUpEvent,
			Source = panel._collapseLabel
		});
		bool collapseRestored = panel._expanded == originalExpanded;
		panel.ClearMetroNotificationsAsync().GetAwaiter().GetResult();
		bool clearAllWorked = !withNotifications || (panel._notifs.Children.Count == 0 && panel._emptyState.Visibility == Visibility.Visible);
		total.Stop();
		string[] reference = { "wifi", "bluetooth", "brightness", "airplane", "quiet", "theme", "maps", "project", "connect", "battery", "settings", "power" };
		string[] actual = panel._metroTiles.Take(reference.Length).Select(tile => tile.Id).ToArray();
		bool exactRoutes = panel._metroTiles.Count == MetroRouteContracts.Count
			&& panel._metroTiles.All(tile => MetroRouteContracts.TryGetValue(tile.Id, out string? contract)
				&& string.Equals(contract, tile.RouteContract, StringComparison.Ordinal));
		ProcessStartInfo mapsStart = CreateExternalStartInfo(GoogleMapsTarget);
		bool laptopRules = panel._metroTiles.Where(tile => tile.LaptopOnly).All(tile => tile.Root.Visibility == Visibility.Visible);
		panel._isLaptop = false;
		panel._brightnessAvailable = false;
		panel.ArrangeMetroTiles(animateHeight: false);
		bool desktopRules = panel._metroTiles.Where(tile => tile.LaptopOnly).All(tile => tile.Root.Visibility == Visibility.Collapsed);
		panel._isLaptop = true;
		panel._brightnessAvailable = true;
		panel.ArrangeMetroTiles(animateHeight: false);
		ActionCenterQaMetrics metrics = new ActionCenterQaMetrics
		{
			WidthDiu = width,
			HeightDiu = height,
			DefinedTiles = panel._metroTiles.Count,
			VisibleTiles = panel._metroTiles.Count(tile => tile.Root.Visibility == Visibility.Visible),
			TileColumns = panel._tiles.ColumnDefinitions.Count,
			VisibleTileRows = panel._visibleTileRows,
			FunctionalRoutes = panel._metroTiles.Count(tile => tile.Action != null),
			RouteContracts = panel._metroTiles.Count(tile => !string.IsNullOrWhiteSpace(tile.RouteContract)),
			StatefulTiles = panel._metroTiles.Count(tile => tile.IsOn != null),
			IconsAreVectors = panel._metroTiles.All(tile => tile.Icon is Viewbox viewbox && viewbox.Child is Canvas),
			IconsUseUniformPremiumGeometry = panel._metroTiles.All(tile => tile.Icon is Viewbox viewbox
				&& Math.Abs(viewbox.Width - PremiumTileIconSize) < 0.1
				&& Math.Abs(viewbox.Height - PremiumTileIconSize) < 0.1
				&& viewbox.Child is Canvas { Width: 24.0, Height: 24.0 }),
			TileIconSizeDiu = PremiumTileIconSize,
			ExactRouteContracts = exactRoutes,
			MapsUsesGoogleMaps = panel._metroTiles.Any(tile => tile.Id == "maps"
				&& tile.RouteContract == "browser:" + GoogleMapsTarget),
			ExternalLinksUseDefaultHandler = mapsStart.UseShellExecute
				&& string.Equals(mapsStart.FileName, GoogleMapsTarget, StringComparison.Ordinal),
			MapsTarget = GoogleMapsTarget,
			ActiveChecksPresent = panel._metroTiles.Where(tile => tile.IsOn != null).All(tile => tile.Check != null),
			FourColumnReferenceOrder = reference.SequenceEqual(actual),
			LaptopRulesPassed = laptopRules,
			DesktopRulesPassed = desktopRules,
			SlidersAreLiveControls = panel._volume is MetroSlider && panel._brightness is MetroSlider,
			NotificationViewportBounded = panel._notificationScroll.VerticalScrollBarVisibility == ScrollBarVisibility.Auto,
			PixelScrollingEnabled = !panel._notificationScroll.CanContentScroll && SmoothScroll.VerticalSupported,
			ScrollBarHitWidthDiu = scrollWidth,
			ScrollRangeDiu = scrollRange,
			EmptyStateVisible = emptyStateVisible,
			NotificationCards = notificationCards,
			ClearAllFunctional = panel._clearAllButton != null && clearAllWorked,
			FewerSettingsFunctional = panel._collapseButton != null && panel._tileViewport != null && collapseChanged && collapseRestored,
			CollapseUsesCanonicalDirectionalArrow = panel._collapseArrow is MetroDirectionalArrow81
				&& Math.Abs(panel._collapseArrow.GlyphSize - 32.0) < 0.1,
			CollapseDirectionMatchesAction = panel._collapseArrow.Direction == (panel._expanded ? MetroArrowDirection81.Up : MetroArrowDirection81.Down),
			CollapseHitTargetExpanded = panel._collapseArrow.Width >= 42.0 && panel._collapseArrow.Height >= 42.0,
			ThemeBackgroundSynchronized = before == after && panel._themeBase == blue,
			ThemeSwitchPassed = red != before && after == before,
			ThemeBase = panel._themeBase.ToString(),
			AuthenticOpenMs = AuthenticOpenMilliseconds,
			AuthenticCloseMs = AuthenticCloseMilliseconds,
			TileHoverMs = TileHoverMilliseconds,
			TileClickMs = TileClickMilliseconds,
			NotificationTransitionMs = NotificationMilliseconds,
			ClearAllTransitionMs = ClearAllMilliseconds,
			VisualBuildMs = build.ElapsedMilliseconds,
			RenderAndLayoutMs = total.ElapsedMilliseconds,
			RefreshWorkerActiveDuringDetachedRender = panel._runtimeCts != null,
			IdleRenderingHookInactive = !SmoothScroll.IsVerticalActive(panel._notificationScroll) && !SmoothScroll.IsVerticalActive(panel._tileScroll),
			Screenshot = outPath
		};
		panel.Close();
		return metrics;
	}
}
