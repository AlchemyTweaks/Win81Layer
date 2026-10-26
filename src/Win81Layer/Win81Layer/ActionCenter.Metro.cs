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
using Path = System.Windows.Shapes.Path;

namespace Win81Layer;

// QA: the device the Action Center is rendered for (the laptop shows every tile and both sliders).
public enum ActionCenterQaDevice
{
	Laptop,
	Desktop
}

// QA: one quick tile as rendered (whether it is a switch, its state, its colours and where it and its label are drawn).
public sealed record ActionCenterQaTile(string Id, bool Toggle, bool On, System.Windows.Media.Color Fill, System.Windows.Media.Color Border, double BorderThickness, System.Windows.Media.Color Label, bool Check, System.Windows.Media.Color Status, double IconOpacity, Rect TileRect, Rect LabelRect);

public sealed class ActionCenterQaMetrics
{
	public List<ActionCenterQaTile> Tiles { get; set; } = new List<ActionCenterQaTile>();

	public string Device { get; set; } = string.Empty;
	public double WidthDiu { get; set; }
	public double HeightDiu { get; set; }
	public double ExpectedHeightDiu { get; set; }
	public double MaxHeightDiu { get; set; }
	public int TileRows { get; set; }
	public int SliderRows { get; set; }
	public int DefinedTiles { get; set; }
	public int VisibleTiles { get; set; }
	public int TileColumns { get; set; }
	public string[] TileOrder { get; set; } = Array.Empty<string>();
	public string[] VisibleTileOrder { get; set; } = Array.Empty<string>();
	public double TileWidthDiu { get; set; }
	public double TileHeightDiu { get; set; }
	public double TileViewportHeightDiu { get; set; }
	public double TileScrollableHeightDiu { get; set; }
	public bool SlidersAndFooterInside { get; set; }
	public int FunctionalRoutes { get; set; }
	public int RouteContracts { get; set; }
	public Dictionary<string, string> RouteContractMap { get; set; } = new Dictionary<string, string>();
	public int StatefulTiles { get; set; }
	public bool IconsAreVectors { get; set; }
	public bool IconsUseUniformPremiumGeometry { get; set; }
	public double TileIconSizeDiu { get; set; }
	public bool ExactRouteContracts { get; set; }
	public bool MapsUsesGoogleMaps { get; set; }
	public bool ExternalLinksUseDefaultHandler { get; set; }
	public string MapsTarget { get; set; } = string.Empty;
	public bool ActiveChecksPresent { get; set; }
	public int ChecksShown { get; set; }
	public bool FourColumnReferenceOrder { get; set; }
	public bool LaptopRulesPassed { get; set; }
	public bool DesktopRulesPassed { get; set; }
	public bool SlidersAreLiveControls { get; set; }
	public string NotificationsChord { get; set; } = string.Empty;
	public bool NotificationsChordCaptured { get; set; }
	public string[] Texts { get; set; } = Array.Empty<string>();
	public int ScrollViewers { get; set; }
	public bool OnlyScrollViewerIsTileViewport { get; set; }
	public Rect FooterRect { get; set; }
	public Rect FooterLinkRect { get; set; }
	public Rect PowerButtonRect { get; set; }
	public Rect BackgroundProbe { get; set; }
	public Rect SlidersRect { get; set; }
	public bool ThemeBackgroundSynchronized { get; set; }
	public bool ThemeSwitchPassed { get; set; }
	public string ThemeBase { get; set; } = string.Empty;
	public bool RestPalette { get; set; }
	public string BodyTop { get; set; } = string.Empty;
	public string BodyBottom { get; set; } = string.Empty;
	public string BorderColor { get; set; } = string.Empty;
	public string FooterColor { get; set; } = string.Empty;
	public string LinkColor { get; set; } = string.Empty;
	public string SliderFill { get; set; } = string.Empty;
	public double OpenTravelDiu { get; set; }
	public double CloseTravelDiu { get; set; }
	public double ReducedOpenTravelDiu { get; set; }
	public double ReducedCloseTravelDiu { get; set; }
	public int OpenMoveMs { get; set; }
	public int OpenFadeMs { get; set; }
	public int CloseMoveMs { get; set; }
	public int CloseFadeMs { get; set; }
	public int TileHoverMs { get; set; }
	public int TileClickMs { get; set; }
	public long VisualBuildMs { get; set; }
	public long RenderAndLayoutMs { get; set; }
	public bool RefreshWorkerActiveDuringDetachedRender { get; set; }
	public bool IdleRenderingHookInactive { get; set; }
	public string Screenshot { get; set; } = string.Empty;
}

// The Normal (non Dark rest) tones of the panel: body gradient, frame, footer band, link and slider fill.
internal readonly record struct ActionCenterTones(System.Windows.Media.Color BodyTop, System.Windows.Media.Color BodyBottom, System.Windows.Media.Color Border, System.Windows.Media.Color Footer, System.Windows.Media.Color Link, System.Windows.Media.Color SliderFill);

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

		// The status line's own brush, kept while Dark rest recolours it so Normal puts the same one back.
		public System.Windows.Media.Brush? StatusNormal { get; set; }

		public TextBlock? Label { get; init; }
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

	// The flyouts' slider (TaskbarWindow's MetroSlider style): an 8 DIP track, the filled part up to a 10x24 thumb. The whole
	// row is hit-testable and a drag works without mouse capture.
	private sealed class MetroSlider : Slider
	{
		private const double TrackHeight = 8.0;

		private const double ThumbWidth = 10.0;

		private const double ThumbHeight = 24.0;

		private bool _dragging;

		public System.Windows.Media.Brush TrackBrush { get; set; } = new SolidColorBrush(System.Windows.Media.Color.FromArgb(0x66, 255, 255, 255));

		public System.Windows.Media.Brush FillBrush { get; set; } = new SolidColorBrush(System.Windows.Media.Color.FromRgb(108, 216, 244));

		public System.Windows.Media.Brush ThumbBrush { get; set; } = System.Windows.Media.Brushes.White;

		// An outline around the whole track (Dark rest: the control grey, so the range stays visible at a low value);
		// none by default.
		public System.Windows.Media.Brush? TrackBorderBrush { get; set; }

		public MetroSlider()
		{
			Minimum = 0.0;
			Maximum = 100.0;
			SmallChange = 1.0;
			LargeChange = 10.0;
			Focusable = true;
			Template = new ControlTemplate(typeof(Slider));
			SnapsToDevicePixels = true;
			VerticalAlignment = VerticalAlignment.Stretch;
		}

		protected override void OnValueChanged(double oldValue, double newValue)
		{
			base.OnValueChanged(oldValue, newValue);
			InvalidateVisual();
		}

		protected override void OnRender(DrawingContext drawingContext)
		{
			base.OnRender(drawingContext);
			// Full-bounds transparent fill so the ENTIRE row is hit-testable for clicks, drag and mouse wheel (with an empty
			// template WPF only hit-tests the painted track and thumb).
			drawingContext.DrawRectangle(System.Windows.Media.Brushes.Transparent, null, new Rect(0.0, 0.0, ActualWidth, ActualHeight));
			double center = Math.Round(ActualHeight * 0.5);
			double usable = Math.Max(1.0, ActualWidth - ThumbWidth);
			double ratio = Maximum <= Minimum ? 0.0 : Math.Clamp((Value - Minimum) / (Maximum - Minimum), 0.0, 1.0);
			double thumbX = Math.Round(ratio * usable);
			Rect track = new Rect(0.0, center - TrackHeight * 0.5, ActualWidth, TrackHeight);
			drawingContext.DrawRectangle(TrackBrush, null, track);
			if (TrackBorderBrush != null)
			{
				drawingContext.DrawRectangle(null, new Pen(TrackBorderBrush, 1.0), new Rect(track.X + 0.5, track.Y + 0.5, Math.Max(0.0, track.Width - 1.0), track.Height - 1.0));
			}
			drawingContext.DrawRectangle(FillBrush, null, new Rect(0.0, track.Y, thumbX, TrackHeight));
			drawingContext.DrawRectangle(ThumbBrush, null, new Rect(thumbX, center - ThumbHeight * 0.5, ThumbWidth, ThumbHeight));
		}

		protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
		{
			base.OnMouseLeftButtonDown(e);
			Focus();
			// Begin dragging unconditionally. CaptureMouse() can be REFUSED while the panel is still mid-activation (returns
			// false); capture is only needed to track a drag OUTSIDE the control, so it is best-effort.
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
			double usable = Math.Max(1.0, ActualWidth - ThumbWidth);
			double ratio = Math.Clamp((x - ThumbWidth * 0.5) / usable, 0.0, 1.0);
			Value = Minimum + ratio * (Maximum - Minimum);
		}
	}

	private readonly List<MetroTile> _metroTiles = new();
	private Border _frame = null!;
	private Grid _metroRoot = null!;
	private Grid _tiles = null!;
	private ScrollViewer _tileScroll = null!;
	private Border _tileViewport = null!;
	private StackPanel _sliderHost = null!;
	private FrameworkElement _brightnessRow = null!;
	private Border _footer = null!;
	private Button _settingsLink = null!;
	private System.Windows.Controls.Image _settingsGear = null!;
	private TextBlock _settingsLabel = null!;
	private Button _powerButton = null!;
	private TextBlock _volumeValueText = null!;
	private TextBlock _brightnessValueText = null!;
	private Border _volumeIconHost = null!;
	private Border _brightnessIconHost = null!;
	private CancellationTokenSource? _runtimeCts;
	private int _runtimeGeneration;
	private int _animationGeneration;
	private int _visibleTileRows = 1;
	// The panel's height as laid out (clamped to the space there is) and the height its content wants (unclamped).
	private double _contentHeight;
	private double _naturalHeight;
	// The tile viewport's wheel: whole rows only (the target row offset and the wheel delta not yet worth a row).
	private double _tileWheelTarget;
	private int _tileWheelAccum;
	private bool _qaRendering;
	// QA: forces the radio tiles (Wi-Fi, Airplane) on or off regardless of the machine's adapters.
	private bool? _qaRadios;
	private bool _isLaptop;
	private bool _brightnessAvailable;
	private bool _batterySaver;
	private bool _projectConnected;
	private NetState81? _lastNet;
	private string _wifiStatus = "Checking";
	private string _bluetoothStatus = "Checking";
	private string _airplaneStatus = "Checking";
	private string _batteryStatus = "Checking";
	private System.Windows.Media.Color _themeBase;
	private LinearGradientBrush _themeBackground = null!;
	private SolidColorBrush _linkBrush = null!;
	private SolidColorBrush _footerBrush = null!;
	private SolidColorBrush _frameBrush = null!;
	// True while the Dark rest palette is applied (the quick tiles are muted).
	private bool _restPalette;

	// The motion contract (Motion tokens, identical to the tray flyouts): the content rises 12 DIP from the taskbar over 250 ms
	// (MoveShort, DECELERATE) with a 120 ms linear fade; the close fades over 100 ms while moving 6 DIP toward the taskbar
	// with ACCELERATE (ExitMove). Calm motion moves half as far; Off shows and hides at once.
	private const double OpenTravel = 12.0;
	private const double CloseTravel = 6.0;
	private const int TileHoverMilliseconds = 100;
	private const int TileClickMilliseconds = 100;
	private const double TileIconSize = 20.0;
	private const double SliderIconSize = 20.0;
	private const double FooterGlyphSize = 16.0;
	// The slider rows' icon box (the footer's gear is centred on the same column) and the gap after it.
	private const double SliderIconBoxDip = 32.0;
	private const double SliderIconGapDip = 8.0;
	// The tile viewport's slim scrollbar (it sits in the right side padding, so the tiles keep their width).
	private const double ScrollBarDip = 10.0;

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
	private const string NotificationsPageTarget = "ms-settings:notifications";
	private const string GoogleMapsTarget = "https://www.google.com/maps";

	// The reference order of the quick tiles (four columns, row by row); All settings and Power live in the footer.
	internal static readonly string[] TileReferenceOrder = { "wifi", "bluetooth", "brightness", "airplane", "notifications", "theme", "maps", "project", "connect", "battery", "nightlight" };

	internal static readonly IReadOnlyDictionary<string, string> MetroRouteContracts = new Dictionary<string, string>(StringComparer.Ordinal)
	{
		["wifi"] = "toggle:Windows.Devices.Radios/WiFi|fallback:" + WifiSettingsTarget,
		["bluetooth"] = "toggle:Windows.Devices.Radios/Bluetooth|fallback:" + BluetoothSettingsTarget,
		["brightness"] = "control:MonitorBrightness|fallback:" + DisplaySettingsTarget,
		["airplane"] = "toggle:Windows.Devices.Radios/all|fallback:" + AirplaneSettingsTarget,
		["notifications"] = "shell:Win+N|fallback:" + NotificationsPageTarget,
		["theme"] = "launcher:start-personalize|fallback:" + PersonalizationSettingsTarget,
		["maps"] = "browser:" + GoogleMapsTarget,
		["project"] = "shell:DisplaySwitch.exe|fallback:" + ProjectSettingsTarget,
		["connect"] = "shell:Win+K|fallback:" + ConnectedDevicesTarget,
		["battery"] = "settings:" + BatterySaverSettingsTarget,
		["settings"] = "settings:" + SettingsHomeTarget,
		["power"] = "launcher:power-menu",
		["nightlight"] = "settings:" + NightLightSettingsTarget
	};

	// The footer's two actions (with the same route contracts they had as tiles).
	private readonly List<(string Id, string RouteContract, Func<Task> Action)> _footerActions = new();

	private void BuildMetroSurface()
	{
		_isLaptop = SafeIsLaptop();
		// Cheap initial guess only — a synchronous MonitorBrightness.Get() here is a 50-300ms DDC/CI I2C read (can even time
		// out on a desktop with no DDC monitor), which would stall the first open. The async refresh sets the real value from
		// the live probe shortly after, and the panel keeps it for the next open.
		_brightnessAvailable = _isLaptop;
		base.UseLayoutRounding = true;
		base.SnapsToDevicePixels = true;
		_metroRoot = new Grid
		{
			Background = System.Windows.Media.Brushes.Transparent,
			RenderTransform = _slide,
			UseLayoutRounding = true,
			SnapsToDevicePixels = true
		};
		// The panel used to be a layered window, which always renders text grayscale; keep that look on the plain HWND.
		TextOptions.SetTextRenderingMode(_metroRoot, TextRenderingMode.Grayscale);
		KeyboardNavigation.SetTabNavigation(_metroRoot, KeyboardNavigationMode.Cycle);
		StyleMetroScrollBars();
		_metroRoot.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
		_metroRoot.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
		_metroRoot.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1.0, GridUnitType.Star) });

		// the quick tiles: four columns with 4 DIP gutters, a bounded viewport only when the panel cannot be tall enough
		_tiles = new Grid { UseLayoutRounding = true };
		_tileScroll = new ScrollViewer
		{
			Content = _tiles,
			VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
			HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
			CanContentScroll = false,
			PanningMode = PanningMode.VerticalOnly,
			Focusable = false
		};
		_tileScroll.AddHandler(Mouse.PreviewMouseWheelEvent, new MouseWheelEventHandler(OnMetroTileWheel), handledEventsToo: true);
		_tileViewport = new Border
		{
			Margin = new Thickness(SidePaddingDip, TopPaddingDip, SidePaddingDip, 0.0),
			ClipToBounds = true,
			Child = _tileScroll
		};
		Grid.SetRow(_tileViewport, 0);
		_metroRoot.Children.Add(_tileViewport);

		// volume (always) and brightness (when the display allows it), 40 DIP rows
		_sliderHost = new StackPanel { Margin = new Thickness(SidePaddingDip, TilesToSlidersDip, SidePaddingDip, SlidersToFooterDip) };
		BuildMetroSliders();
		Grid.SetRow(_sliderHost, 1);
		_metroRoot.Children.Add(_sliderHost);

		// the footer: All settings on the left, Power on the right, under a 1 DIP divider
		_footer = new Border
		{
			BorderThickness = new Thickness(0.0, 1.0, 0.0, 0.0),
			Height = FooterDip + 1.0,
			VerticalAlignment = VerticalAlignment.Bottom,
			SnapsToDevicePixels = true
		};
		Grid footerGrid = new Grid();
		footerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.0, GridUnitType.Star) });
		footerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
		_settingsGear = new System.Windows.Controls.Image
		{
			Width = FooterGlyphSize,
			Height = FooterGlyphSize,
			Stretch = Stretch.Uniform,
			SnapsToDevicePixels = true,
			VerticalAlignment = VerticalAlignment.Center
		};
		RenderOptions.SetBitmapScalingMode(_settingsGear, BitmapScalingMode.HighQuality);
		// The footer keeps the slider rows' two columns: the gear is centred on their 32 DIP icon box and the label starts
		// where the slider tracks start.
		double gearInset = (SliderIconBoxDip - FooterGlyphSize) / 2.0;
		_settingsLabel = new TextBlock
		{
			Text = "All settings",
			FontFamily = new System.Windows.Media.FontFamily("Segoe UI"),
			FontSize = 12.0,
			VerticalAlignment = VerticalAlignment.Center,
			Margin = new Thickness(gearInset + SliderIconGapDip, 0.0, 0.0, 1.0)
		};
		StackPanel linkContent = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
		linkContent.Children.Add(_settingsGear);
		linkContent.Children.Add(_settingsLabel);
		_settingsLink = FooterButton(linkContent, "All settings");
		_settingsLink.Padding = new Thickness(SidePaddingDip + gearInset, 0.0, 8.0, 0.0);
		_settingsLink.HorizontalAlignment = HorizontalAlignment.Left;
		_settingsLink.Click += async delegate { await RunFooterActionAsync("settings"); };
		Grid.SetColumn(_settingsLink, 0);
		footerGrid.Children.Add(_settingsLink);
		_powerButton = FooterButton(CreateMetroIcon("power", FooterGlyphSize), "Power");
		_powerButton.Width = FooterDip;
		_powerButton.ToolTip = "Power";
		_powerButton.HorizontalContentAlignment = HorizontalAlignment.Center;
		_powerButton.Click += async delegate { await RunFooterActionAsync("power"); };
		Grid.SetColumn(_powerButton, 1);
		footerGrid.Children.Add(_powerButton);
		_footer.Child = footerGrid;
		Grid.SetRow(_footer, 2);
		_metroRoot.Children.Add(_footer);
		_footerActions.Clear();
		_footerActions.Add(("settings", MetroRouteContracts["settings"], () => OpenExternal("All settings", SettingsHomeTarget)));
		_footerActions.Add(("power", MetroRouteContracts["power"], () => RunSync(ShowPowerMenu)));

		_frame = new Border
		{
			BorderThickness = new Thickness(FrameDip),
			ClipToBounds = true,
			SnapsToDevicePixels = true,
			UseLayoutRounding = true,
			Child = _metroRoot
		};
		base.Content = _frame;
		BuildMetroTiles();
		ApplyMetroTheme();
		base.Width = PanelWidthDip;
		base.Height = LayoutPanel(PanelWidthDip, double.PositiveInfinity);
	}

	private static bool SafeIsLaptop()
	{
		try
		{
			return PowerStatus.Read().present;
		}
		catch
		{
			return false;
		}
	}

	private static readonly ControlTemplate FooterButtonTemplate = (ControlTemplate)System.Windows.Markup.XamlReader.Parse(
		"<ControlTemplate TargetType=\"Button\" xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\" xmlns:x=\"http://schemas.microsoft.com/winfx/2006/xaml\">" +
		"<Border x:Name=\"Bd\" Background=\"Transparent\" Padding=\"{TemplateBinding Padding}\" SnapsToDevicePixels=\"True\">" +
		"<ContentPresenter HorizontalAlignment=\"{TemplateBinding HorizontalContentAlignment}\" VerticalAlignment=\"Center\"/></Border>" +
		"<ControlTemplate.Triggers>" +
		"<Trigger Property=\"IsKeyboardFocused\" Value=\"True\"><Setter TargetName=\"Bd\" Property=\"Background\" Value=\"{DynamicResource AC.FooterHover}\"/></Trigger>" +
		"<Trigger Property=\"IsMouseOver\" Value=\"True\"><Setter TargetName=\"Bd\" Property=\"Background\" Value=\"{DynamicResource AC.FooterHover}\"/></Trigger>" +
		"<Trigger Property=\"IsPressed\" Value=\"True\"><Setter TargetName=\"Bd\" Property=\"Background\" Value=\"{DynamicResource AC.FooterPressed}\"/></Trigger>" +
		"</ControlTemplate.Triggers></ControlTemplate>");

	// A footer button: the hover and pressed washes cover the whole 40 DIP row.
	private static Button FooterButton(object content, string automationName)
	{
		Button b = new Button
		{
			Content = content,
			Template = FooterButtonTemplate,
			Height = FooterDip,
			Background = System.Windows.Media.Brushes.Transparent,
			BorderThickness = new Thickness(0.0),
			Cursor = System.Windows.Input.Cursors.Hand,
			FocusVisualStyle = null,
			HorizontalContentAlignment = HorizontalAlignment.Left,
			VerticalAlignment = VerticalAlignment.Top
		};
		AutomationProperties.SetName(b, automationName);
		return b;
	}

	private async Task RunFooterActionAsync(string id)
	{
		foreach ((string Id, string RouteContract, Func<Task> Action) footer in _footerActions)
		{
			if (footer.Id != id)
			{
				continue;
			}
			try
			{
				await footer.Action();
			}
			catch (Exception ex)
			{
				Logger.Log($"Action Center '{id}' failed: {ex.Message}");
			}
			return;
		}
	}

	// True when the focused element is something Enter activates (a tile or a footer button).
	private bool IsActionableFocus()
	{
		if (Keyboard.FocusedElement is not DependencyObject focused)
		{
			return false;
		}
		if (focused is Button)
		{
			return true;
		}
		return _metroTiles.Any(t => ReferenceEquals(t.Root, focused));
	}

	private void BuildMetroSliders()
	{
		_volume = new MetroSlider { Value = 50.0 };
		_volumeValueText = CreateSliderValue("50");
		FrameworkElement volumeRow = CreateMetroSliderRow("volume", "Volume", "Mute or unmute", _volume, _volumeValueText, out Border volumeIcon);
		_volumeIconHost = volumeIcon;
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
		_brightnessRow = CreateMetroSliderRow("brightness", "Brightness", "Brightness", _brightness, _brightnessValueText, out Border brightnessIcon);
		_brightnessIconHost = brightnessIcon;
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

	// The volume slider icon reflects the live level + mute: the speaker as a white vector on the same 24-unit grid and 20 DIP
	// box as the brightness sun below it (no waves at 0, one to three waves by level, a cross when muted), re-inked in Dark rest.
	private void UpdateVolumeSliderIcon(int pct, bool muted)
	{
		if (_volumeIconHost == null)
		{
			return;
		}
		_volumeIconHost.Child = RestTinted(CreateVolumeGlyph(VolumeWaves(pct, muted), muted, SliderIconSize));
	}

	// The level variants of the speaker, on the same thresholds as the tray and the OSD (Win81AssetResolver.VolumeSemantic).
	internal static int VolumeWaves(int pct, bool muted)
	{
		return Win81AssetResolver.VolumeSemantic(pct, muted) switch
		{
			"Volume.High" => 3,
			"Volume.Medium" => 2,
			"Volume.Low" => 1,
			_ => 0
		};
	}

	// Shared with the tray's volume flyout (its master and app rows), so the speaker is one vector everywhere.
	internal static FrameworkElement CreateVolumeGlyph(int waves, bool muted, double size)
	{
		Canvas canvas = new Canvas { Width = 24.0, Height = 24.0, SnapsToDevicePixels = true };
		canvas.Children.Add(CreateFillPath("M 2,9.2 L 6.2,9.2 L 11,4.8 L 11,19.2 L 6.2,14.8 L 2,14.8 Z"));
		if (muted)
		{
			canvas.Children.Add(CreateStrokePath("M 15.2,9 L 21.2,15 M 21.2,9 L 15.2,15", 1.7));
		}
		else
		{
			string[] arcs =
			{
				"M 14,9.4 C 15.3,10.8 15.3,13.2 14,14.6",
				"M 16.8,6.9 C 19.4,9.8 19.4,14.2 16.8,17.1",
				"M 19.6,4.4 C 23.3,8.5 23.3,15.5 19.6,19.6"
			};
			for (int i = 0; i < Math.Clamp(waves, 0, 3); i++)
			{
				canvas.Children.Add(CreateStrokePath(arcs[i], 1.6));
			}
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

	// The brightness slider icon dims at low brightness and brightens toward full.
	private void UpdateBrightnessSliderIcon(int level)
	{
		if (_brightnessIconHost == null)
		{
			return;
		}
		FrameworkElement icon = RestTinted(CreateMetroIcon("brightness", SliderIconSize));
		icon.Opacity = 0.45 + 0.55 * Math.Clamp((double)level / 100.0, 0.0, 1.0);
		_brightnessIconHost.Child = icon;
	}

	// A freshly made white vector glyph in the soft ink while Dark rest is on.
	private FrameworkElement RestTinted(FrameworkElement icon)
	{
		if (_restPalette)
		{
			DarkRestPalette.RetintWhite(icon, rest: true);
		}
		return icon;
	}

	private FrameworkElement CreateMetroSliderRow(string iconId, string name, string toolTip, Slider slider, TextBlock value, out Border iconHost)
	{
		// [32 icon box | 8 | slider | 44 value]; no caption: the icon and the automation name say what the slider is
		Grid row = new Grid { Height = SliderRowDip };
		row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(SliderIconBoxDip) });
		row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(SliderIconGapDip) });
		row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.0, GridUnitType.Star) });
		row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(44.0) });
		iconHost = new Border
		{
			Width = SliderIconBoxDip,
			Height = SliderRowDip,
			Background = System.Windows.Media.Brushes.Transparent,
			Cursor = System.Windows.Input.Cursors.Hand,
			ToolTip = toolTip,
			Child = CreateMetroIcon(iconId, SliderIconSize)
		};
		Grid.SetColumn(iconHost, 0);
		row.Children.Add(iconHost);
		AutomationProperties.SetName(slider, name);
		Grid.SetColumn(slider, 2);
		row.Children.Add(slider);
		Grid.SetColumn(value, 3);
		row.Children.Add(value);
		return row;
	}

	private static TextBlock CreateSliderValue(string text)
	{
		return new TextBlock
		{
			Text = text,
			Foreground = System.Windows.Media.Brushes.White,
			FontFamily = new System.Windows.Media.FontFamily("Segoe UI"),
			FontSize = 12.0,
			VerticalAlignment = VerticalAlignment.Center,
			TextAlignment = TextAlignment.Right,
			Margin = new Thickness(4.0, 0.0, 0.0, 1.0)
		};
	}

	private void BuildMetroTiles()
	{
		_metroTiles.Clear();
		_tiles.Children.Clear();
		_tiles.ColumnDefinitions.Clear();
		_tiles.RowDefinitions.Clear();
		// four equal columns with 4 DIP gutter columns between them: 4 x 85 + 3 x 4 = 352 DIP at the 376 DIP width
		for (int i = 0; i < 7; i++)
		{
			_tiles.ColumnDefinitions.Add(new ColumnDefinition { Width = i % 2 == 0 ? new GridLength(1.0, GridUnitType.Star) : new GridLength(TileGapDip) });
		}

		AddMetroTile("wifi", "Wi-Fi", System.Windows.Media.Color.FromRgb(0, 160, 231), ToggleWifi, () => _wifiStatus, () => _wifiOn, () => _qaRadios ?? NetCaps.HasWifi);
		AddMetroTile("bluetooth", "Bluetooth", System.Windows.Media.Color.FromRgb(116, 54, 214), ToggleBluetooth, () => _bluetoothStatus, () => _btOn);
		AddMetroTile("brightness", "Brightness", System.Windows.Media.Color.FromRgb(0, 126, 229), CycleMetroBrightness, () => _brightnessAvailable ? $"{Math.Round(_brightness?.Value ?? 0.0)}%" : "Unavailable", null, () => _brightnessAvailable, laptopOnly: true);
		// "Airplane mode" does not fit the 73 DIP label width at 12 px SemiBold; the full name is the tooltip and automation name
		AddMetroTile("airplane", "Airplane", System.Windows.Media.Color.FromRgb(207, 32, 86), ToggleAirplane, () => _airplaneStatus, () => _airplaneOn, () => _qaRadios ?? NetCaps.RadiosPresent, fullName: "Airplane mode");
		// Windows' own notification center (Win+N); the launcher shows no notifications of its own
		AddMetroTile("notifications", "Notifications", System.Windows.Media.Color.FromRgb(0, 174, 184), OpenNotificationCenter, () => string.Empty);
		// tiles without a state show no status line
		AddMetroTile("theme", "Theme", System.Windows.Media.Color.FromRgb(199, 35, 130), OpenMetroPersonalize, () => string.Empty);
		AddMetroTile("maps", "Maps", System.Windows.Media.Color.FromRgb(45, 183, 44), OpenMetroMaps, () => string.Empty);
		AddMetroTile("project", "Project", System.Windows.Media.Color.FromRgb(0, 126, 229), OpenProject, () => _projectConnected ? "Connected" : "Disconnected", () => _projectConnected);
		AddMetroTile("connect", "Connect", System.Windows.Media.Color.FromRgb(100, 48, 210), OpenConnect, () => string.Empty);
		AddMetroTile("battery", "Battery saver", System.Windows.Media.Color.FromRgb(0, 155, 170), () => OpenExternal("Battery saver", BatterySaverSettingsTarget, PowerSleepSettingsTarget), () => _batteryStatus, () => _batterySaver, () => _isLaptop, laptopOnly: true);
		AddMetroTile("nightlight", "Night light", System.Windows.Media.Color.FromRgb(58, 95, 196), () => OpenExternal("Night light", NightLightSettingsTarget, DisplaySettingsTarget), () => string.Empty, null, () => _isLaptop && IsNightLightSupported(), laptopOnly: true);
		ArrangeMetroTiles();
		RefreshMetroTileStates(animate: false);
	}

	private void AddMetroTile(
		string id,
		string label,
		System.Windows.Media.Color color,
		Func<Task> action,
		Func<string> status,
		Func<bool>? isOn = null,
		Func<bool>? isVisible = null,
		bool laptopOnly = false,
		string? fullName = null)
	{
		SolidColorBrush fill = new SolidColorBrush(color);
		ScaleTransform scale = new ScaleTransform(1.0, 1.0);
		FrameworkElement icon = CreateMetroIcon(id, TileIconSize);
		// Label and state are both 12 px (the floor), so the weight carries the hierarchy: the name SemiBold, the state
		// Regular. One line each, trimmed, so every tile keeps the same 66 DIP.
		TextBlock labelText = new TextBlock
		{
			Text = label,
			Foreground = System.Windows.Media.Brushes.White,
			FontFamily = new System.Windows.Media.FontFamily("Segoe UI"),
			FontSize = 12.0,
			FontWeight = FontWeights.SemiBold,
			LineHeight = 16.0,
			LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
			TextWrapping = TextWrapping.NoWrap,
			TextTrimming = TextTrimming.CharacterEllipsis
		};
		TextBlock statusText = new TextBlock
		{
			Text = string.Empty,
			Foreground = System.Windows.Media.Brushes.White,
			FontFamily = new System.Windows.Media.FontFamily("Segoe UI"),
			FontSize = 12.0,
			LineHeight = 16.0,
			LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
			TextWrapping = TextWrapping.NoWrap,
			TextTrimming = TextTrimming.CharacterEllipsis,
			Visibility = Visibility.Collapsed
		};
		StackPanel text = new StackPanel { VerticalAlignment = VerticalAlignment.Bottom };
		text.Children.Add(labelText);
		text.Children.Add(statusText);
		Grid content = new Grid { Margin = TileContentMargin };
		content.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1.0, GridUnitType.Star) });
		content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
		icon.HorizontalAlignment = HorizontalAlignment.Left;
		icon.VerticalAlignment = VerticalAlignment.Top;
		content.Children.Add(icon);
		Grid.SetRow(text, 1);
		content.Children.Add(text);
		Path check = CreateStrokePath("M 4,12 L 9,17 L 20,5", 2.2);
		check.Width = 12.0;
		check.Height = 12.0;
		check.Stretch = Stretch.Uniform;
		check.HorizontalAlignment = HorizontalAlignment.Right;
		check.VerticalAlignment = VerticalAlignment.Top;
		check.Margin = new Thickness(0.0, 1.0, 1.0, 0.0);
		check.Visibility = Visibility.Collapsed;
		content.Children.Add(check);
		Border root = new Border
		{
			Background = fill,
			Height = TileHeightDip,
			CornerRadius = new CornerRadius(0.0),
			Cursor = System.Windows.Input.Cursors.Hand,
			Focusable = true,
			RenderTransformOrigin = new Point(0.5, 0.5),
			RenderTransform = scale,
			Child = content
		};
		AutomationProperties.SetName(root, fullName ?? label);
		if (fullName != null)
		{
			root.ToolTip = fullName;
		}
		MetroTile tile = new MetroTile
		{
			Id = id,
			Root = root,
			Fill = fill,
			Label = labelText,
			Status = statusText,
			Icon = icon,
			Check = check,
			Scale = scale,
			BaseColor = color,
			Action = action,
			RouteContract = MetroRouteContracts[id],
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
			// log only: the launcher shows no toasts of its own
			Logger.Log($"Action Center '{tile.Id}' failed: {ex.Message}");
		}
		finally
		{
			tile.Root.Opacity = 1.0;
			tile.Busy = false;
			RefreshMetroTileStates(animate: true);
		}
	}

	// Places the visible tiles row by row in the four columns (the last row stays left-aligned).
	private void ArrangeMetroTiles()
	{
		int visible = 0;
		foreach (MetroTile tile in _metroTiles)
		{
			bool show = true;
			try { show = tile.IsVisible?.Invoke() ?? true; } catch { show = false; }
			tile.Root.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
			if (!show) continue;
			Grid.SetColumn(tile.Root, (visible % 4) * 2);
			Grid.SetRow(tile.Root, (visible / 4) * 2);
			visible++;
		}
		_visibleTileRows = Math.Max(1, (int)Math.Ceiling(visible / 4.0));
		int rowDefs = _visibleTileRows * 2 - 1;
		while (_tiles.RowDefinitions.Count < rowDefs)
		{
			_tiles.RowDefinitions.Add(new RowDefinition());
		}
		while (_tiles.RowDefinitions.Count > rowDefs)
		{
			_tiles.RowDefinitions.RemoveAt(_tiles.RowDefinitions.Count - 1);
		}
		for (int i = 0; i < _tiles.RowDefinitions.Count; i++)
		{
			_tiles.RowDefinitions[i].Height = new GridLength(i % 2 == 0 ? TileHeightDip : TileGapDip);
		}
	}

	private int SliderRowCount()
	{
		return _brightnessAvailable ? 2 : 1;
	}

	// Lays the content out for a panel of the given width and returns its height: 71 + T + 40n DIP, or less when maxHeightDip
	// is less (the tile block then scrolls inside a viewport of whole rows, at least one; the sliders and the footer stay
	// whole). The viewport's slim scrollbar sits in the right side padding, so the four columns keep their width.
	private double LayoutPanel(double width, double maxHeightDip)
	{
		ArrangeMetroTiles();
		int sliders = SliderRowCount();
		_brightnessRow.Visibility = _brightnessAvailable ? Visibility.Visible : Visibility.Collapsed;
		double tileBlock = TileBlockDip(_visibleTileRows);
		double natural = ContentHeightDip(_visibleTileRows, sliders);
		double viewport = tileBlock;
		if (maxHeightDip > 0.0 && !double.IsInfinity(maxHeightDip) && !double.IsNaN(maxHeightDip) && natural > maxHeightDip + 0.01)
		{
			viewport = TileViewportDip(maxHeightDip - (natural - tileBlock), _visibleTileRows);
		}
		double height = natural - tileBlock + viewport;
		bool scrolls = viewport < tileBlock - 0.5;
		_tileViewport.Height = viewport;
		_tileViewport.Margin = new Thickness(SidePaddingDip, TopPaddingDip, scrolls ? SidePaddingDip - ScrollBarDip : SidePaddingDip, 0.0);
		_tileScroll.VerticalScrollBarVisibility = scrolls ? ScrollBarVisibility.Visible : ScrollBarVisibility.Disabled;
		if (!scrolls)
		{
			_tileScroll.ScrollToVerticalOffset(0.0);
		}
		_tileWheelTarget = 0.0;
		_tileWheelAccum = 0;
		_contentHeight = height;
		_naturalHeight = natural;
		return height;
	}

	private void RefreshMetroTileStates(bool animate)
	{
		foreach (MetroTile tile in _metroTiles)
		{
			string status;
			try { status = tile.StatusText() ?? string.Empty; } catch { status = string.Empty; }
			tile.Status.Text = status;
			tile.Status.Visibility = string.IsNullOrEmpty(status) ? Visibility.Collapsed : Visibility.Visible;
			if (tile.Id == "wifi") UpdateWifiTileIcon(tile);
			ApplyMetroTileVisual(tile, animate && !_qaRendering, tile.Root.IsMouseOver);
		}
	}

	// The Wi-Fi quick tile is a RADIO toggle, so its glyph reflects the real Wi-Fi radio + (when Wi-Fi is the active
	// link) the live NetState81 signal/limited state — never a hardcoded "connected 4-bar". It uses the one canonical
	// renderer (NetIcons81), the same as the tray/flyout/charms, so a single network state maps to a single icon everywhere.
	private void UpdateWifiTileIcon(MetroTile tile)
	{
		if (_lastNet == null) return;   // keep the initial glyph until the first real snapshot — avoids a red-x flash on open
		if (tile.Icon is Viewbox { Child: Canvas canvas })
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

	// The Wi-Fi glyph's unlit signal arcs on a coloured tile, and the whole glyph while the radio is off or not linked: soft
	// whites that stay legible on every tile colour (NetIcons81's own unlit grey is for a dark tray).
	private static readonly System.Windows.Media.Color TileUnlitInk = System.Windows.Media.Color.FromArgb(0x66, 255, 255, 255);
	private static readonly System.Windows.Media.Color TileIdleInk = System.Windows.Media.Color.FromArgb(0x99, 255, 255, 255);

	private System.Windows.Media.ImageSource WifiTileIcon()
	{
		System.Windows.Media.Color white = System.Windows.Media.Colors.White;
		if (!_wifiOn)
		{
			// Radio off -> soft Wi-Fi arcs + red badge (the reference set's "Wi-Fi Off").
			return NetIcons81.Draw(NetworkIconKind.Wifi, NetworkIconState.NotConnected, 0, 32, white, TileIdleInk);
		}
		NetState81? net = _lastNet;
		if (net != null && net.Kind == NetKind.Wifi)
		{
			// Wi-Fi is the active link -> real signal bars + limited/no-internet overlay.
			return NetIcons81.Draw(NetworkIconKind.Wifi, net.EffectiveIconState, net.Bars, 32, white, TileUnlitInk);
		}
		// Radio on but connected via something else (or nothing) -> idle Wi-Fi arcs, no bars.
		return NetIcons81.Draw(NetworkIconKind.Wifi, NetworkIconState.Connected, 0, 32, white, TileIdleInk);
	}

	private void ApplyMetroTileVisual(MetroTile tile, bool animate, bool hover)
	{
		bool on = false;
		try { on = tile.IsOn?.Invoke() ?? false; } catch { }
		tile.Check.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
		// Dark rest mutes the tile colours. A switch that is off is a dark field with an outline and a softer label,
		// so On and Off read apart by more than the check mark; the other mixes stay the same.
		System.Windows.Media.Color fill = _restPalette ? DarkRestPalette.Mute(tile.BaseColor) : tile.BaseColor;
		System.Windows.Media.Color target = Mix(fill, System.Windows.Media.Colors.Black, on ? 0.0 : 0.10);
		if (_restPalette)
		{
			bool off = tile.IsOn != null && !on;
			if (off)
			{
				target = DarkRestPalette.FieldColor;
			}
			// constant thickness, so switching never shifts the content; the content gives the 1 DIP back so the label keeps
			// its full 73 DIP ("Battery saver", "Disconnected" fit as they do in Normal)
			tile.Root.BorderThickness = new Thickness(1.0);
			if (tile.Root.Child is FrameworkElement restContent)
			{
				restContent.Margin = TileContentMarginInBorder;
			}
			tile.Root.BorderBrush = off ? DarkRestPalette.Boundary : System.Windows.Media.Brushes.Transparent;
			if (tile.Label != null)
			{
				tile.Label.Foreground = off ? DarkRestPalette.Ink2 : DarkRestPalette.Ink;
			}
			// the status line never outshines the title: the same soft ink on an On tile, the secondary ink on an Off
			// one, whose glyph is quieter too, so Off reads as one calm state
			tile.StatusNormal ??= tile.Status.Foreground;
			tile.Status.Foreground = off ? DarkRestPalette.Ink2 : DarkRestPalette.Ink;
			tile.Icon.Opacity = off ? RestOffGlyphOpacity : 1.0;
		}
		else
		{
			tile.Root.ClearValue(Border.BorderThicknessProperty);
			tile.Root.ClearValue(Border.BorderBrushProperty);
			if (tile.Root.Child is FrameworkElement normalContent)
			{
				normalContent.Margin = TileContentMargin;
			}
			if (tile.Label != null)
			{
				tile.Label.Foreground = System.Windows.Media.Brushes.White;
			}
			if (tile.StatusNormal != null)
			{
				tile.Status.Foreground = tile.StatusNormal;
			}
			if (tile.Icon.Opacity == RestOffGlyphOpacity)
			{
				tile.Icon.Opacity = 1.0;
			}
		}
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

	// The glyph of a switch that is off in Dark rest (with the soft glyph opacity, about the secondary ink).
	private const double RestOffGlyphOpacity = 0.8;

	// The tile content inset (7,7,5,6 DIP: a 20 DIP icon over two 16 DIP lines in 66 DIP), and the same box inside Dark
	// rest's 1 DIP tile outline.
	private static readonly Thickness TileContentMargin = new Thickness(7.0, 7.0, 5.0, 6.0);

	private static readonly Thickness TileContentMarginInBorder = new Thickness(6.0, 6.0, 4.0, 5.0);

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
		_shownTick = System.Environment.TickCount64;   // grace window so the opening click can't self-dismiss (see CloseOnOutsideClick)
		_shownStamp = System.Diagnostics.Stopwatch.GetTimestamp();
		int generation = ++_animationGeneration;
		// The visible tiles and sliders come from cached state (the battery read is cheap, the radios and the brightness
		// availability are the last known), so the async snapshot rarely changes the height after the panel is up.
		_isLaptop = SafeIsLaptop();
		_projectConnected = System.Windows.Forms.Screen.AllScreens.Length > 1;
		ApplyMetroTheme();
		PlaceOnEdge();
		RefreshMetroTileStates(animate: false);
		_metroRoot.BeginAnimation(UIElement.OpacityProperty, null);
		_slide.BeginAnimation(TranslateTransform.XProperty, null);
		_slide.BeginAnimation(TranslateTransform.YProperty, null);
		// The window appears at its final position (its frame and body are static): only the content rises 12 DIP from the
		// taskbar while it fades in. No BitmapCache, no scale.
		_metroRoot.CacheMode = null;
		System.Windows.Vector start = Motion.Enabled ? _toward * Motion.Distance(OpenTravel) : new System.Windows.Vector(0.0, 0.0);
		_slide.X = start.X;
		_slide.Y = start.Y;
		_metroRoot.Opacity = Motion.Enabled ? 0.0 : 1.0;
		// Show()+ForceForeground can bounce activation (WA_INACTIVE, then WA_ACTIVE again) while the foreground is handed over
		// from another process; Start, which makes the same two calls from the same taskbar route, logs that bounce on ~1 in 8
		// opens. Deactivated is raised synchronously inside these calls, so _showing tells its handler the bounce is ours.
		_showing = true;
		try
		{
			if (!base.IsVisible) Show();
			// in physical pixels once shown, so a window last hidden on a monitor of another DPI lands and sizes exactly
			ApplyPlacementPx();
			// Robustly take the real foreground (WindowUtil.ForceForeground, never attaching this thread), like Start. Plain
			// Activate() is subject to Windows' foreground-lock and INTERMITTENTLY leaves the panel visible-but-inactive,
			// so the FIRST click on a slider/tile only activates the window (gets eaten) and needs a second click.
			WindowUtil.ForceForeground(this);
		}
		finally
		{
			_showing = false;
		}
		if (!base.IsVisible)
		{
			return;
		}
		if (_dismissing)
		{
			// Backstop: a dismiss still ran inside the show. If we ended up active it was a bounce: a new generation voids the
			// stale close Completed and the 800ms fallback (both bound to the old one). If not, let that close finish.
			if (!base.IsActive)
			{
				return;
			}
			_dismissing = false;
			generation = ++_animationGeneration;
			Logger.Log("Action Center: mid-show dismiss cancelled (activation bounce)");
		}
		double tookMs = System.Diagnostics.Stopwatch.GetElapsedTime(_shownStamp).TotalMilliseconds;
		// took= stays a whole number (existing log miners parse it); precise= carries the sub-millisecond value.
		Logger.Log($"Action Center: show via {_via} edge={_edge} {_placedPx.Width}x{_placedPx.Height}@{_placedPx.Left},{_placedPx.Top} active={base.IsActive} fg={ForegroundDescription()} took={(long)tookMs}ms precise={tookMs.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture)}ms");
		if (!Motion.Enabled)
		{
			_slide.X = 0.0;
			_slide.Y = 0.0;
			_metroRoot.Opacity = 1.0;
		}
		else
		{
			// one-shot clocks over the exact final base values (0 and 1): 250 ms DECELERATE rise, 120 ms linear fade. The token
			// comes from the designed 12 DIP travel, so calm motion rises 6 DIP over the same 250 ms.
			double fromX = _slide.X;
			double fromY = _slide.Y;
			_slide.X = 0.0;
			_slide.Y = 0.0;
			_metroRoot.Opacity = 1.0;
			Motion.Tok move = Motion.Move(OpenTravel);
			if (Math.Abs(fromX) > 0.01)
			{
				_slide.BeginAnimation(TranslateTransform.XProperty, Motion.Anim(fromX, 0.0, move), HandoffBehavior.SnapshotAndReplace);
			}
			if (Math.Abs(fromY) > 0.01)
			{
				_slide.BeginAnimation(TranslateTransform.YProperty, Motion.Anim(fromY, 0.0, move), HandoffBehavior.SnapshotAndReplace);
			}
			_metroRoot.BeginAnimation(UIElement.OpacityProperty, Motion.Anim(0.0, 1.0, Motion.Tok.FadeIn), HandoffBehavior.SnapshotAndReplace);
		}
		StartMetroRefresh();
	}

	// One idempotent "hide and fully reset" primitive. The window's only real Hide() used to live SOLELY
	// inside the close animation's Completed callback; if that clock was ever abandoned (a dual-monitor focus
	// race, or re-opening mid-close), Completed never fired, _dismissing stayed latched true forever and EVERY
	// dismiss path became a no-op. Routing every hide through this, plus the fallback timer and the
	// force-close-on-repeat below, makes a dropped Completed harmless. Safe to call more than once.
	private void HardHide()
	{
		StopMetroRefresh();
		SmoothScroll.Stop(_tileScroll);
		Hide();
		_slide.BeginAnimation(TranslateTransform.XProperty, null);
		_slide.BeginAnimation(TranslateTransform.YProperty, null);
		_metroRoot.BeginAnimation(UIElement.OpacityProperty, null);
		_slide.X = 0.0;
		_slide.Y = 0.0;
		_metroRoot.Opacity = 1.0;
		_metroRoot.CacheMode = null;
		_dismissing = false;
	}

	private void DismissMetroPanel(string cause = "action")
	{
		if (!base.IsVisible) return;
		Logger.Log($"Action Center: hide cause = {cause}{(_dismissing ? " (forced, close already in flight)" : "")} at +{System.Environment.TickCount64 - _shownTick}ms after show");
		if (_dismissing) { HardHide(); return; }   // a repeat dismiss while the close is mid-flight (or stuck) forces the hide
		_dismissing = true;
		int generation = ++_animationGeneration;
		StopMetroRefresh();
		SmoothScroll.Stop(_tileScroll);
		if (Motion.Mode == MotionMode.Off)
		{
			HardHide();
			return;
		}
		// a 100 ms linear fade while the content moves 6 DIP toward the taskbar with ACCELERATE; then HardHide
		_metroRoot.CacheMode = null;
		System.Windows.Vector to = _toward * Motion.Distance(CloseTravel);
		DoubleAnimation fade = new DoubleAnimation(0.0, Motion.Dur(Motion.Tok.FadeOut)) { EasingFunction = Motion.Ease(Motion.Tok.FadeOut) };
		Duration moveDuration = Motion.Dur(Motion.Tok.ExitMove);
		IEasingFunction accelerate = Motion.Ease(Motion.Tok.ExitMove);
		fade.Completed += delegate
		{
			if (generation != _animationGeneration || !_dismissing) return;
			HardHide();
		};
		_slide.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(to.X, moveDuration) { EasingFunction = accelerate }, HandoffBehavior.SnapshotAndReplace);
		_slide.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(to.Y, moveDuration) { EasingFunction = accelerate }, HandoffBehavior.SnapshotAndReplace);
		_metroRoot.BeginAnimation(UIElement.OpacityProperty, fade, HandoffBehavior.SnapshotAndReplace);
		// Safety net: if that Completed is ever dropped (interrupted animation clock on a dual-monitor focus
		// race), guarantee the hide anyway. The generation + _dismissing checks make it a no-op when the panel
		// was already hidden by Completed or re-opened in the meantime. 800ms is safely past any close anim.
		System.Windows.Threading.DispatcherTimer fallback = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(800.0) };
		fallback.Tick += delegate
		{
			fallback.Stop();
			if (generation == _animationGeneration && _dismissing)
			{
				Logger.Log("Action Center: hide completed by fallback timer (close animation Completed was dropped)");
				HardHide();
			}
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
		// reflect the real volume level/mute (and brightness level) on the slider icons every time the panel syncs
		UpdateVolumeSliderIcon((int)Math.Round(snapshot.Volume * 100f), snapshot.Muted);
		if (snapshot.Brightness >= 0) UpdateBrightnessSliderIcon(snapshot.Brightness);
		_isLaptop = snapshot.BatteryPresent;
		_brightnessAvailable = snapshot.Brightness >= 0;
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
		double before = _contentHeight;
		ArrangeMetroTiles();
		double natural = ContentHeightDip(_visibleTileRows, SliderRowCount());
		// compared with the natural height the panel was laid out for (a panel clamped to a short work area is not re-placed
		// on every open just because its natural height is taller than the space it has)
		if (Math.Abs(natural - _naturalHeight) > 0.5 && base.IsVisible && !_dismissing)
		{
			// The live state changed what the panel shows (brightness became available, a radio appeared): re-place it once,
			// top and height together, without animation.
			PlaceOnEdge();
			Logger.Log($"Action Center: re-placed (content height {before:0}→{_contentHeight:0})");
		}
		else
		{
			_brightnessRow.Visibility = _brightnessAvailable ? Visibility.Visible : Visibility.Collapsed;
		}
		RefreshMetroTileStates(animate: true);
	}

	private void ApplyMetroTheme()
	{
		if (DarkRest.Visuals)
		{
			ApplyRestPalette();
			return;
		}
		// the one theme rule of the tray-end panels (the clock and volume flyouts read the same)
		FlyoutPalette.ReadTheme(out System.Windows.Media.Color themeBase, out System.Windows.Media.Color accent, out bool seasonal);
		ApplyMetroPalette(themeBase, accent, seasonal);
	}

	// The Normal tones, from the flyouts' tone function (FlyoutPalette.Tone) so the panel sits in the clock, volume and network
	// flyout family: the body runs from the Start background's tone to the accent's (a season keeps its base hue for the
	// whole body; its accent stays on the sliders, the On tiles and the link), the frame and the link are the flyouts' own.
	internal static ActionCenterTones NormalTones(System.Windows.Media.Color themeBase, System.Windows.Media.Color accent, bool seasonal)
	{
		System.Windows.Media.Color top = seasonal ? FlyoutPalette.Tone(themeBase, 0.0, 0.0, 0.26) : FlyoutPalette.Tone(themeBase, 0.0, 0.0, 0.24);
		System.Windows.Media.Color bottom = seasonal ? FlyoutPalette.Tone(themeBase, 0.0, 0.0, 0.17) : FlyoutPalette.Tone(accent, 0.0, 0.0, 0.14);
		System.Windows.Media.Color border = FlyoutPalette.Tone(accent, 0.0, 0.42, 0.48);
		// the footer band continues the body it sits under: the accent's tone in Normal (the body ends in it), the base
		// hue's in a season (whose body keeps the base hue), so no second hue appears at the divider
		System.Windows.Media.Color footer = seasonal ? FlyoutPalette.Tone(themeBase, 0.0, 0.0, 0.16) : FlyoutPalette.Tone(accent, 0.0, 0.0, 0.16);
		System.Windows.Media.Color linkTone = FlyoutPalette.Tone(accent, -0.01, 0.55, 0.68);
		System.Windows.Media.Color link = linkTone;
		// the link stays readable on everything it can sit over (at least 4.5:1): raised toward white when it is not
		for (double w = 0.04; w <= 1.0 && MinContrast(link, top, bottom, footer) < 4.5; w += 0.04)
		{
			link = Mix(linkTone, System.Windows.Media.Colors.White, w);
		}
		return new ActionCenterTones(top, bottom, border, footer, link, FlyoutPalette.Brighten(accent));
	}

	private static double MinContrast(System.Windows.Media.Color ink, params System.Windows.Media.Color[] grounds)
	{
		return grounds.Min(g => FlyoutPalette.Contrast(ink, g));
	}

	private static readonly SolidColorBrush NormalDivider = FreezeMetroBrush(System.Windows.Media.Color.FromArgb(0x32, 255, 255, 255));
	private static readonly SolidColorBrush NormalFooterHover = FreezeMetroBrush(System.Windows.Media.Color.FromArgb(0x1A, 255, 255, 255));
	private static readonly SolidColorBrush NormalFooterPressed = FreezeMetroBrush(System.Windows.Media.Color.FromArgb(0x33, 255, 255, 255));
	private static readonly SolidColorBrush NormalValueInk = FreezeMetroBrush(System.Windows.Media.Color.FromArgb(0xE8, 255, 255, 255));
	private static readonly SolidColorBrush NormalSliderTrack = FreezeMetroBrush(System.Windows.Media.Color.FromArgb(0x66, 255, 255, 255));

	// Dark rest: the flyouts' near-black body, the popup edge, the soft inks, the accent ink for the link and the accent line
	// on the sliders; the tiles are muted.
	private void ApplyRestPalette()
	{
		_restPalette = true;
		DarkRestPalette.RetintWhite(_metroRoot, rest: true);
		// the tile viewport's scrollbar (only when the panel cannot be tall enough): a quiet grey thumb on a black track
		DarkRestPalette.WriteScrollBrushes(_metroRoot.Resources);
		_themeBase = DarkRestPalette.PopupColor;
		_themeBackground = new LinearGradientBrush(DarkRestPalette.PopupColor, DarkRestPalette.PanelColor, new Point(0.0, 0.0), new Point(1.0, 1.0));
		_themeBackground.Freeze();
		ApplySurface(
			frame: (SolidColorBrush)DarkRestPalette.PopupEdge,
			footer: (SolidColorBrush)DarkRestPalette.Panel,
			divider: (SolidColorBrush)DarkRestPalette.Separator,
			value: (SolidColorBrush)DarkRestPalette.Ink2,
			link: (SolidColorBrush)DarkRestPalette.AccentInk,
			hover: (SolidColorBrush)DarkRestPalette.MenuHover,
			pressed: (SolidColorBrush)DarkRestPalette.Pressed,
			track: DarkRestPalette.SliderTrackBrush,
			trackBorder: DarkRestPalette.Boundary,
			sliderFill: DarkRestPalette.AccentLine,
			thumb: DarkRestPalette.Ink);
		// the footer gear carries the link ink itself (RetintWhite dims pictures it cannot recolour)
		_settingsGear.Opacity = 1.0;
	}

	private void ApplyMetroPalette(System.Windows.Media.Color themeBase, System.Windows.Media.Color accent, bool seasonal = false)
	{
		if (_restPalette)
		{
			DarkRestPalette.RetintWhite(_metroRoot, rest: false);
		}
		_restPalette = false;
		DarkRestPalette.RemoveRestResources(_metroRoot.Resources);
		_themeBase = themeBase;
		ActionCenterTones tones = NormalTones(themeBase, accent, seasonal);
		_themeBackground = new LinearGradientBrush(tones.BodyTop, tones.BodyBottom, new Point(0.0, 0.0), new Point(1.0, 1.0));
		_themeBackground.Freeze();
		ApplySurface(
			frame: FreezeMetroBrush(tones.Border),
			footer: FreezeMetroBrush(tones.Footer),
			divider: NormalDivider,
			value: NormalValueInk,
			link: FreezeMetroBrush(tones.Link),
			hover: NormalFooterHover,
			pressed: NormalFooterPressed,
			track: NormalSliderTrack,
			trackBorder: null,
			sliderFill: FreezeMetroBrush(tones.SliderFill),
			thumb: System.Windows.Media.Brushes.White);
	}

	private void ApplySurface(SolidColorBrush frame, SolidColorBrush footer, SolidColorBrush divider, SolidColorBrush value, SolidColorBrush link,
		SolidColorBrush hover, SolidColorBrush pressed, System.Windows.Media.Brush track, System.Windows.Media.Brush? trackBorder,
		System.Windows.Media.Brush sliderFill, System.Windows.Media.Brush thumb)
	{
		base.Background = _themeBackground;
		_frame.Background = _themeBackground;
		_frame.BorderBrush = frame;
		_frameBrush = frame;
		_footer.Background = footer;
		_footer.BorderBrush = divider;
		_footerBrush = footer;
		_metroRoot.Resources["AC.FooterHover"] = hover;
		_metroRoot.Resources["AC.FooterPressed"] = pressed;
		_volumeValueText.Foreground = value;
		_brightnessValueText.Foreground = value;
		_linkBrush = link;
		_settingsLabel.Foreground = link;
		_settingsGear.Source = SettingsGlyph.Gear(link.Color);
		foreach (MetroSlider slider in new[] { _volume as MetroSlider, _brightness as MetroSlider })
		{
			if (slider == null)
			{
				continue;
			}
			slider.FillBrush = sliderFill;
			slider.TrackBrush = track;
			slider.TrackBorderBrush = trackBorder;
			slider.ThumbBrush = thumb;
			slider.InvalidateVisual();
		}
		SyncNativeClearColor();
		RefreshMetroTileStates(animate: false);
	}

	// The tile viewport scrolls by whole rows (70 DIP, one per wheel notch; a precision touchpad's small deltas add up to a
	// notch first), so a row is never left cut through its label.
	private void OnMetroTileWheel(object sender, MouseWheelEventArgs e)
	{
		if (_tileScroll.ScrollableHeight <= 0.5 || e.Delta == 0) return;
		e.Handled = true;
		_tileWheelAccum += e.Delta;
		int notches = _tileWheelAccum / 120;
		if (notches == 0) return;
		_tileWheelAccum -= notches * 120;
		double from = SmoothScroll.IsVerticalActive(_tileScroll) ? _tileWheelTarget : _tileScroll.VerticalOffset;
		double row = Math.Round(from / TileStrideDip) - notches;
		double target = Math.Clamp(row * TileStrideDip, 0.0, _tileScroll.ScrollableHeight);
		_tileWheelTarget = target;
		SmoothScroll.ToVertical(_tileScroll, target);
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
			style.Setters.Add(new Setter(FrameworkElement.WidthProperty, 10.0));
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

	// A slim scrollbar for the tile viewport (shown only when the panel cannot be tall enough for every row).
	private const string MetroScrollBarTemplateXaml =
		"<ControlTemplate TargetType=\"ScrollBar\" xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\" xmlns:x=\"http://schemas.microsoft.com/winfx/2006/xaml\">" +
		"<Grid Width=\"10\" Background=\"Transparent\" SnapsToDevicePixels=\"True\">" +
		"<Track x:Name=\"PART_Track\" Orientation=\"Vertical\" IsDirectionReversed=\"True\">" +
		"<Track.DecreaseRepeatButton><RepeatButton Command=\"ScrollBar.PageUpCommand\" Opacity=\"0\" Focusable=\"False\"/></Track.DecreaseRepeatButton>" +
		"<Track.Thumb><Thumb MinHeight=\"24\" Focusable=\"False\" Cursor=\"Hand\"><Thumb.Template><ControlTemplate TargetType=\"Thumb\"><Grid Background=\"Transparent\"><Border x:Name=\"ThumbVisual\" Width=\"4\" Margin=\"0,1\" Background=\"{DynamicResource Metro81.ScrollThumbBrushDark}\" HorizontalAlignment=\"Center\"/></Grid><ControlTemplate.Triggers><Trigger Property=\"IsMouseOver\" Value=\"True\"><Setter TargetName=\"ThumbVisual\" Property=\"Background\" Value=\"{DynamicResource Metro81.ScrollThumbHoverBrushDark}\"/></Trigger><Trigger Property=\"IsDragging\" Value=\"True\"><Setter TargetName=\"ThumbVisual\" Property=\"Background\" Value=\"{DynamicResource Metro81.ScrollThumbPressedBrushDark}\"/></Trigger></ControlTemplate.Triggers></ControlTemplate></Thumb.Template></Thumb></Track.Thumb>" +
		"<Track.IncreaseRepeatButton><RepeatButton Command=\"ScrollBar.PageDownCommand\" Opacity=\"0\" Focusable=\"False\"/></Track.IncreaseRepeatButton>" +
		"</Track></Grid></ControlTemplate>";

	// Also the volume flyout's device glyphs (the "project" monitor for a display output).
	internal static FrameworkElement CreateMetroIcon(string id, double size)
	{
		Canvas canvas = new Canvas { Width = 24.0, Height = 24.0, SnapsToDevicePixels = true };
		void stroke(string data, double thickness = 1.6) => canvas.Children.Add(CreateStrokePath(data, thickness));
		void fill(string data) => canvas.Children.Add(CreateFillPath(data));
		switch (id)
		{
			case "wifi":
				// the canonical vector renderer in white (never the outlined tray bitmap), so the glyph keeps one style in
				// every state; UpdateWifiTileIcon swaps in the live state
				canvas.Children.Add(new Image
				{
					Width = 24.0,
					Height = 24.0,
					Stretch = Stretch.Uniform,
					SnapsToDevicePixels = true,
					Source = NetIcons81.Draw(NetworkIconKind.Wifi, NetworkIconState.Connected, 4, 32, System.Windows.Media.Colors.White, TileUnlitInk)
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
				// a white vector plane (nose to the right), like the other tiles' glyphs
				fill("M 21.6,12 C 21.6,11.1 20.8,10.5 19.8,10.5 L 14.8,10.5 L 9.8,3 L 7.6,3 L 10.4,10.5 L 5.8,10.5 L 3.9,7.8 L 2.3,7.8 L 3.4,12 L 2.3,16.2 L 3.9,16.2 L 5.8,13.5 L 10.4,13.5 L 7.6,21 L 9.8,21 L 14.8,13.5 L 19.8,13.5 C 20.8,13.5 21.6,12.9 21.6,12 Z");
				break;
			case "notifications":
				// the notification center's speech bubble with two lines of text
				stroke("M 2.8,4 L 21.2,4 L 21.2,16.6 L 10.6,16.6 L 6.4,20.6 L 6.4,16.6 L 2.8,16.6 Z", 1.6);
				stroke("M 7,8.6 L 17,8.6 M 7,12.2 L 14,12.2", 1.5);
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
			case "power":
				stroke("M 12,1.5 L 12,11.5 M 6.3,5.3 C 2.4,8.5 2.5,14.8 6.4,18 C 10,21 15.5,20.4 18.5,16.8 C 21.5,13.1 20.6,8 17.7,5.3", 1.9);
				break;
			case "nightlight":
				fill("M 16.5,17 C 11,18 7,14 8,8.5 C 4.5,10 3.5,14.5 5.8,18 C 8.5,22 14.5,22 18,18 Z");
				stroke("M 18,2 L 18,5 M 14.7,3.3 L 16.3,4.9 M 21.3,3.3 L 19.7,4.9", 1.35);
				break;
			case "volume":
				return CreateVolumeGlyph(3, muted: false, size);
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
}
