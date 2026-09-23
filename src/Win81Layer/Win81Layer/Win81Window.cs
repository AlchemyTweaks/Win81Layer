using System;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Shell;
using System.Windows.Threading;

namespace Win81Layer;

public partial class Win81Window : Window, IComponentConnector
{
	private SolidColorBrush _active = new SolidColorBrush(Color.FromRgb(77, 144, 254));

	private SolidColorBrush _inactive = new SolidColorBrush(Color.FromRgb(166, 201, 242));

	private Brush _activeText = ColorMath.Frozen(Color.FromRgb(242, 242, 242));

	private SolidColorBrush _inactiveText = ColorMath.Frozen(Color.FromRgb(58, 58, 58));

	private Brush _aeroActiveCaption = Brushes.Transparent;

	private Brush _aeroInactiveCaption = Brushes.Transparent;

	private Brush _aeroActiveFrame = Brushes.Transparent;

	private Brush _aeroInactiveFrame = Brushes.Transparent;

	private Brush _aeroNativeActiveCaption = Brushes.Transparent;

	private Brush _aeroNativeInactiveCaption = Brushes.Transparent;

	private Brush _aeroNativeActiveFrame = Brushes.Transparent;

	private Brush _aeroNativeInactiveFrame = Brushes.Transparent;

	private Brush _aeroActiveOutline = Brushes.Transparent;

	private Brush _aeroInactiveOutline = Brushes.Transparent;

	private bool _nativeAeroFrame;

	private bool _regionApplied;

	private int _shapeRefreshPending;

	private int _regionWidth;

	private int _regionHeight;

	private int _regionRadius;

	private static Win81Window? _sample;

	private int _compositionRefreshPending;

	private bool _explorerChrome81;

	internal event Action? ExplorerPropertiesRequested;

	internal event Action? ExplorerNewFolderRequested;

	// Authentic Win7 Aero caption-text glow (documented — see docs/dwm/DWM_REFERENCE_VALUES.md §2): a soft white
	// bloom that lifts the title off the glossy glass. Shared + frozen (one small title element → negligible cost).
	private static readonly System.Windows.Media.Effects.DropShadowEffect _aeroTextGlow = CreateAeroTextGlow();

	private static System.Windows.Media.Effects.DropShadowEffect CreateAeroTextGlow()
	{
		System.Windows.Media.Effects.DropShadowEffect g = new System.Windows.Media.Effects.DropShadowEffect
		{
			Color = Colors.White,
			ShadowDepth = 0.0,
			BlurRadius = 6.0,
			Opacity = 0.55,
			RenderingBias = System.Windows.Media.Effects.RenderingBias.Quality
		};
		((Freezable)g).Freeze();
		return g;
	}

	// Authentic Win8.1 LIGHT caption palette (VM-verified, Build 9600; documented flat colours — exact hex TBD from
	// aero.msstyles). White bar, dark title ink, thin light frame. Used when the 8.1 flat profile is in LIGHT mode.
	private static readonly Brush _c81LightBg = ColorMath.Frozen(Color.FromRgb(255, 255, 255));

	private static readonly Brush _c81LightBgInactive = ColorMath.Frozen(Color.FromRgb(247, 247, 247));

	private static readonly Brush _c81LightFrame = ColorMath.Frozen(Color.FromRgb(198, 198, 198));

	private static readonly Brush _c81TitleActive = ColorMath.Frozen(Color.FromRgb(38, 38, 38));

	private static readonly Brush _c81TitleInactive = ColorMath.Frozen(Color.FromRgb(109, 109, 109));

	private static readonly Brush _transparent = ColorMath.Frozen(Colors.Transparent);

	private static readonly Brush _ownedBodyBackground = ColorMath.Frozen(Color.FromRgb(27, 27, 27));

	private static readonly Brush _aeroCaptionInk = ColorMath.Frozen(Color.FromRgb(30, 35, 39));

	private static readonly Brush _aeroCaptionInkInactive = ColorMath.Frozen(Color.FromRgb(86, 91, 96));

	private static readonly Brush _nativeClientEdge = ColorMath.Frozen(Color.FromArgb(150, 53, 64, 73));

	// HWNDs of live Win81Window instances (FileBrowser, sample, dialogs). The launcher's task list excludes our OWN
	// process windows so the Start overlay / panels don't appear as taskbar buttons — but these ARE real, minimizable
	// app windows and MUST stay taskable (else a minimize would be unrecoverable). WindowList.Enumerate consults this.
	private static readonly System.Collections.Generic.HashSet<nint> _taskable = new System.Collections.Generic.HashSet<nint>();

	public static bool IsTaskable(nint hwnd)
	{
		lock (_taskable)
		{
			return _taskable.Contains(hwnd);
		}
	}

	public Win81Window()
	{
		InitializeComponent();
		base.SourceInitialized += delegate
		{
			ApplyAccent();
			ApplyChromeMode();
			ApplyOwnedCorner();
		};
		base.Loaded += delegate
		{
			ApplyAccent();
			ApplyChromeMode();
			UpdateMaxGlyph();
			ApplyOwnedCorner();
			try
			{
				nint h = new System.Windows.Interop.WindowInteropHelper(this).Handle;
				if (h != 0)
				{
					lock (_taskable)
					{
						_taskable.Add(h);
					}
				}
			}
			catch
			{
			}
		};
		base.Activated += delegate
		{
			ApplyState(active: true);
		};
		base.Deactivated += delegate
		{
			ApplyState(active: false);
		};
		base.StateChanged += delegate
		{
			UpdateMaxGlyph();
			QueueOwnedShape();
		};
		base.SizeChanged += delegate
		{
			QueueOwnedShape();
		};
		base.DpiChanged += delegate
		{
			QueueOwnedShape();
		};
		DesktopComposition.ModeChanged += OnCompositionModeChanged;
		DwmBlurGlassBridge.StateChanged += OnCompositionModeChanged;
		base.Closed += delegate
		{
			DesktopComposition.ModeChanged -= OnCompositionModeChanged;
			DwmBlurGlassBridge.StateChanged -= OnCompositionModeChanged;
			try
			{
				nint h = new System.Windows.Interop.WindowInteropHelper(this).Handle;
				lock (_taskable)
				{
					_taskable.Remove(h);
				}
			}
			catch
			{
			}
		};
	}

	private void OnCompositionModeChanged()
	{
		try
		{
			if (Interlocked.Exchange(ref _compositionRefreshPending, 1) != 0)
			{
				return;
			}
			base.Dispatcher.BeginInvoke((Action)delegate
			{
				Interlocked.Exchange(ref _compositionRefreshPending, 0);
				ApplyChromeMode();
				ApplyAccent();
				ApplyOwnedCorner();
			}, DispatcherPriority.Render);
		}
		catch
		{
			Interlocked.Exchange(ref _compositionRefreshPending, 0);
		}
	}

	private void ApplyChromeMode()
	{
		try
		{
			bool aero = ShellSkin.OwnedAeroOn && !_explorerChrome81;
			_nativeAeroFrame = aero && ShellSkin.NativeOwnedAeroOn;
			WindowChrome chrome = WindowChrome.GetWindowChrome(this);
			if (chrome != null)
			{
				chrome.CaptionHeight = 30.0;
				chrome.ResizeBorderThickness = new Thickness(aero ? 8.0 : 4.0);
				chrome.CornerRadius = new CornerRadius(aero && !_nativeAeroFrame ? 6.0 : 0.0);
				// Windows 10/11 native glyphs keep their modern shape even when the glass extension restores their
				// height. The launcher draws its DPI-independent Win7 vector buttons over the real DWM glass instead.
				chrome.UseAeroCaptionButtons = false;
			}

			CaptionRow.Height = new GridLength(_nativeAeroFrame ? 30.0 : 29.0);
			CaptionHost.Visibility = Visibility.Visible;
			CaptionHost.Margin = aero ? new Thickness(0.0, -1.0, 4.0, 0.0) : new Thickness(0.0);
			AeroTopHighlight.Visibility = aero ? Visibility.Visible : Visibility.Collapsed;
			if (_explorerChrome81 && !aero)
			{
				ExplorerQuickAccess.Visibility = Visibility.Visible;
				CaptionIcon.Visibility = Visibility.Collapsed;
				TitleText.HorizontalAlignment = HorizontalAlignment.Stretch;
				TitleText.TextAlignment = TextAlignment.Center;
				TitleText.Margin = new Thickness(150.0, 0.0, 150.0, 0.0);
			}
			else
			{
				ExplorerQuickAccess.Visibility = Visibility.Collapsed;
				CaptionIcon.Visibility = Visibility.Visible;
				TitleText.HorizontalAlignment = HorizontalAlignment.Left;
				TitleText.TextAlignment = TextAlignment.Left;
				TitleText.Margin = new Thickness(31.0, 0.0, aero ? 112.0 : 144.0, 0.0);
				CaptionIcon.Margin = new Thickness(aero ? 8.0 : 10.0, 0.0, 0.0, 0.0);
			}

			Style captionStyle = (Style)base.Resources[aero ? "AeroCaptionButton" : "CaptionButton"];
			Style closeStyle = (Style)base.Resources[aero ? "AeroCloseButton" : "CloseButton"];
			MinBtn.Style = captionStyle;
			MaxBtn.Style = captionStyle;
			CloseBtn.Style = closeStyle;

			base.Background = _nativeAeroFrame ? _transparent : _ownedBodyBackground;
			WinBorder.BorderThickness = base.WindowState == WindowState.Maximized || _nativeAeroFrame
				? new Thickness(0.0)
				: new Thickness(1.0);
			ApplyClientFrameMetrics();
			ApplyShadow();
			ApplyState(base.IsActive);
			QueueOwnedShape();
		}
		catch (Exception ex)
		{
			Logger.Log("Owned chrome apply failed: " + ex.Message);
		}
	}

	private void ApplyClientFrameMetrics()
	{
		bool aero = ShellSkin.OwnedAeroOn && !_explorerChrome81;
		if (base.WindowState == WindowState.Maximized)
		{
			ClientFrame.Margin = new Thickness(0.0);
			ClientFrame.BorderThickness = new Thickness(0.0);
			return;
		}
		if (_nativeAeroFrame)
		{
			ClientFrame.Margin = new Thickness(8.0, 0.0, 8.0, 8.0);
			ClientFrame.BorderThickness = new Thickness(1.0);
			ClientFrame.BorderBrush = _nativeClientEdge;
			return;
		}
		if (aero)
		{
			ClientFrame.Margin = new Thickness(7.0, 0.0, 7.0, 7.0);
			ClientFrame.BorderThickness = new Thickness(1.0);
			ClientFrame.BorderBrush = (Brush)base.Resources["ClientEdge"];
			return;
		}
		ClientFrame.Margin = new Thickness(0.0);
		ClientFrame.BorderThickness = new Thickness(0.0);
	}

	private void QueueOwnedShape()
	{
		try
		{
			if (Interlocked.Exchange(ref _shapeRefreshPending, 1) != 0)
			{
				return;
			}
			base.Dispatcher.BeginInvoke((Action)delegate
			{
				Interlocked.Exchange(ref _shapeRefreshPending, 0);
				ApplyOwnedShape();
			}, DispatcherPriority.Render);
		}
		catch
		{
			Interlocked.Exchange(ref _shapeRefreshPending, 0);
		}
	}

	// Windows 10 has no public corner-preference attribute. A small event-driven window region gives the custom
	// fallback the native Win7 rounded outline without AllowsTransparency or a continuously rendered mask. The real
	// DWM frame and Windows 11 paths stay compositor-owned.
	private void ApplyOwnedShape()
	{
		try
		{
			nint hwnd = new WindowInteropHelper(this).Handle;
			if (hwnd == 0)
			{
				return;
			}
			bool roundedFallback = CompositionCapabilities.IsWin10
				&& ShellSkin.OwnedAeroOn
				&& !_explorerChrome81
				&& !_nativeAeroFrame
				&& base.WindowState != WindowState.Maximized;
			if (!roundedFallback)
			{
				if (_regionApplied)
				{
					SetWindowRgn(hwnd, IntPtr.Zero, true);
					_regionApplied = false;
					_regionWidth = _regionHeight = _regionRadius = 0;
				}
				return;
			}
			if (!GetWindowRect(hwnd, out RECT rect))
			{
				return;
			}
			int width = Math.Max(1, rect.Right - rect.Left);
			int height = Math.Max(1, rect.Bottom - rect.Top);
			double scale = VisualTreeHelper.GetDpi(this).DpiScaleX;
			int radius = Math.Max(6, (int)Math.Round(7.0 * scale));
			if (_regionApplied && width == _regionWidth && height == _regionHeight && radius == _regionRadius)
			{
				return;
			}
			nint region = CreateRoundRectRgn(0, 0, width + 1, height + 1, radius * 2, radius * 2);
			if (region == 0)
			{
				return;
			}
			_regionWidth = width;
			_regionHeight = height;
			_regionRadius = radius;
			if (SetWindowRgn(hwnd, region, true) != 0)
			{
				_regionApplied = true;   // the system owns region after success
			}
			else
			{
				DeleteObject(region);
			}
		}
		catch
		{
		}
	}

	// On a Win11 HOST the launcher's OWN windows are excluded from the foreign-window sweep. Apply the profile's
	// corner policy directly to owned HWNDs; SetWindowRgn above supplies the Windows 10 fallback.
	private void ApplyOwnedCorner()
	{
		try
		{
			if (!CompositionCapabilities.SupportsCornerPreference)
			{
				return;
			}
			nint h = new System.Windows.Interop.WindowInteropHelper(this).Handle;
			if (h == 0)
			{
				return;
			}
			string corners = _explorerChrome81
				? "square"
				: CompositionProfiles.Resolve(DesktopComposition.EffectiveMode).Corners;
			int pref = string.Equals(corners, "square", StringComparison.OrdinalIgnoreCase)
				? 1
				: string.Equals(corners, "round", StringComparison.OrdinalIgnoreCase) ? 2 : 0;
			DwmSetWindowAttribute(h, 33, ref pref, 4);   // DWMWA_WINDOW_CORNER_PREFERENCE
		}
		catch
		{
		}
	}

	[StructLayout(LayoutKind.Sequential)]
	private struct RECT
	{
		public int Left;
		public int Top;
		public int Right;
		public int Bottom;
	}

	[DllImport("user32.dll")]
	private static extern bool GetWindowRect(nint hwnd, out RECT rect);

	[DllImport("gdi32.dll")]
	private static extern nint CreateRoundRectRgn(int left, int top, int right, int bottom, int ellipseWidth, int ellipseHeight);

	[DllImport("user32.dll")]
	private static extern int SetWindowRgn(nint hwnd, nint region, bool redraw);

	[DllImport("gdi32.dll")]
	private static extern bool DeleteObject(nint obj);

	[System.Runtime.InteropServices.DllImport("dwmapi.dll")]
	private static extern int DwmSetWindowAttribute(nint h, int attr, ref int val, int size);

	public void SetBody(object content)
	{
		Body.Content = content;
	}

	internal object? BodyContent => Body.Content;

	internal void ConfigureExplorer81()
	{
		_explorerChrome81 = true;
		ClientFrame.Background = Brushes.White;
		ApplyChromeMode();
		ApplyAccent();
	}

	private void OnExplorerProperties(object sender, RoutedEventArgs e)
	{
		ExplorerPropertiesRequested?.Invoke();
	}

	private void OnExplorerNewFolder(object sender, RoutedEventArgs e)
	{
		ExplorerNewFolderRequested?.Invoke();
	}

	private void OnExplorerQuickAccessMenu(object sender, RoutedEventArgs e)
	{
		ContextMenu menu = new ContextMenu
		{
			Background = Brushes.White,
			Foreground = Brushes.Black,
			BorderBrush = ColorMath.Frozen(Color.FromRgb(155, 155, 155)),
			BorderThickness = new Thickness(1),
			PlacementTarget = sender as UIElement,
			Placement = PlacementMode.Bottom
		};
		Style itemStyle = new Style(typeof(MenuItem));
		itemStyle.Setters.Add(new Setter(Control.ForegroundProperty, Brushes.Black));
		itemStyle.Setters.Add(new Setter(Control.BackgroundProperty, Brushes.Transparent));
		itemStyle.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(10, 4, 18, 4)));
		menu.ItemContainerStyle = itemStyle;
		MenuItem properties = new MenuItem { Header = "Properties" };
		properties.Click += (_, _) => TaskbarContextMenu.QueueCommand(delegate { ExplorerPropertiesRequested?.Invoke(); });
		MenuItem folder = new MenuItem { Header = "New folder" };
		folder.Click += (_, _) => TaskbarContextMenu.QueueCommand(delegate { ExplorerNewFolderRequested?.Invoke(); });
		menu.Items.Add(properties);
		menu.Items.Add(folder);
		menu.IsOpen = true;
	}

	public void RefreshAccent()
	{
		ApplyAccent();
	}

	// Native Win7 mode needs the documented glass margins for its real DWM frame. Other profiles retain the previous
	// 1-DIP DWM shadow switch. No layered movable window or application blur is introduced.
	private void ApplyShadow()
	{
		try
		{
			WindowChrome chrome = WindowChrome.GetWindowChrome(this);
			if (chrome != null)
			{
				bool sh = SettingsStore.Load().DeskCompShadows;
				chrome.GlassFrameThickness = _nativeAeroFrame
					? new Thickness(8.0, 30.0, 8.0, 8.0)
					: new Thickness(sh ? 1.0 : 0.0);
			}
		}
		catch
		{
		}
	}

	public static void RefreshAllShadows()
	{
		try
		{
			if (Application.Current == null)
			{
				return;
			}
			foreach (Win81Window w in Application.Current.Windows.OfType<Win81Window>())
			{
				w.ApplyShadow();
			}
		}
		catch
		{
		}
	}

	public static void RefreshAllAccents()
	{
		try
		{
			if (Application.Current == null)
			{
				return;
			}
			foreach (Win81Window w in Application.Current.Windows.OfType<Win81Window>())
			{
				w.RefreshAccent();
			}
		}
		catch
		{
		}
	}

	private void ApplyAccent()
	{
		try
		{
			Color accent = StartAccent.Color();
			_active = new SolidColorBrush(accent);
			((Freezable)_active).Freeze();
			_inactive = new SolidColorBrush(ColorMath.Lighten(accent, 0.42));
			((Freezable)_inactive).Freeze();
			_activeText = ColorMath.Frozen(ColorMath.ReadableInk(_active.Color));
			_inactiveText = ColorMath.Frozen(ColorMath.ReadableInk(_inactive.Color));
			BuildAeroPalette(accent);
			base.Resources["WindowAccent"] = _active;
			bool glass = ShellSkin.OwnedAeroOn && !_explorerChrome81;
			bool light81 = !glass && SettingsStore.Current.Win81LightCaption && !_explorerChrome81;
			if (glass)
			{
				// Native Win7 captions use dark title/button ink over light colorized glass; Close remains white.
				base.Resources["CaptionInk"] = _aeroCaptionInk;
				base.Resources["CaptionHover"] = ColorMath.Frozen(Color.FromArgb(51, 255, 255, 255));
				base.Resources["CaptionPress"] = ColorMath.Frozen(Color.FromArgb(85, 255, 255, 255));
			}
			else if (light81)
			{
				// Authentic 8.1 light caption: dark min/max glyphs on white, subtle light-grey hover (close = red via CloseButton style).
				base.Resources["CaptionInk"] = ColorMath.Frozen(Color.FromRgb(68, 68, 68));
				base.Resources["CaptionHover"] = ColorMath.Frozen(Color.FromArgb(24, 0, 0, 0));
				base.Resources["CaptionPress"] = ColorMath.Frozen(Color.FromArgb(46, 0, 0, 0));
			}
			else
			{
				base.Resources["CaptionInk"] = ColorMath.Frozen(ColorMath.ReadableInk(accent));
				base.Resources["CaptionHover"] = ColorMath.Frozen(Color.FromArgb(34, 0, 0, 0));
				base.Resources["CaptionPress"] = ColorMath.Frozen(Color.FromArgb(58, 0, 0, 0));
			}
			ApplyState(base.IsActive);
		}
		catch
		{
		}
	}

	private void ApplyState(bool active)
	{
		try
		{
			bool glass = ShellSkin.OwnedAeroOn && !_explorerChrome81;
			bool light81 = !glass && SettingsStore.Current.Win81LightCaption && !_explorerChrome81;
			if (_nativeAeroFrame)
			{
				// Keep the real DWM blur and native caption buttons, then apply a frozen translucent Win7 Sky layer.
				// This makes the preset deterministic instead of inheriting a dark Windows 10/11 wallpaper/accent.
				TitleBar.Background = active ? _aeroNativeActiveCaption : _aeroNativeInactiveCaption;
				ChromeRoot.Background = active ? _aeroNativeActiveFrame : _aeroNativeInactiveFrame;
				WinBorder.BorderBrush = _transparent;
				TitleText.Foreground = active ? _aeroCaptionInk : _aeroCaptionInkInactive;
				TitleText.Effect = _aeroTextGlow;
				CaptionHost.Opacity = 1.0;
			}
			else if (light81)
			{
				ChromeRoot.Background = _transparent;
				TitleBar.Background = (active ? _c81LightBg : _c81LightBgInactive);
				if (base.WindowState != WindowState.Maximized)
				{
					WinBorder.BorderBrush = _c81LightFrame;
				}
				TitleText.Foreground = (active ? _c81TitleActive : _c81TitleInactive);
				TitleText.Effect = null;
				CaptionHost.Opacity = (active ? 1.0 : 0.75);   // 8.1 dims an inactive caption only slightly
			}
			else
			{
				TitleBar.Background = glass
					? (active ? _aeroActiveCaption : _aeroInactiveCaption)
					: (Brush)(active ? _active : _inactive);
				ChromeRoot.Background = glass
					? (active ? _aeroActiveFrame : _aeroInactiveFrame)
					: _transparent;
				if (base.WindowState != WindowState.Maximized)
				{
					WinBorder.BorderBrush = glass
						? (active ? _aeroActiveOutline : _aeroInactiveOutline)
						: (Brush)(active ? _active : _inactive);
				}
				TitleText.Foreground = glass
					? (active ? _aeroCaptionInk : _aeroCaptionInkInactive)
					: (active ? _activeText : _inactiveText);
				TitleText.Effect = (glass ? _aeroTextGlow : null);
				CaptionHost.Opacity = (active ? 1.0 : (glass ? 0.78 : 0.6));
			}
		}
		catch
		{
		}
	}

	// Build once per accent change, freeze, and reuse on every activation. The fallback is opaque by design: movable
	// layered/WCA blur windows introduce drag latency on Windows 10. The real Win7 preset uses DWM glass whenever the
	// bridge is live; these brushes cover unsupported builds and Alchemy without a render loop.
	private void BuildAeroPalette(Color accent)
	{
		CompositionProfile profile = CompositionProfiles.Resolve(DesktopComposition.EffectiveMode);
		bool authenticWin7 = string.Equals(profile.Id, "windows7-aero", StringComparison.OrdinalIgnoreCase);
		Color sky = authenticWin7
			? Color.FromRgb(116, 184, 252) // Microsoft Win7 Sky colorization: 0x6B74B8FC
			: Blend(accent, Color.FromRgb(77, 151, 202), 0.34);
		if (ColorMath.RelLuminance(sky) < 0.13)
		{
			sky = ColorMath.Lighten(sky, 0.28);
		}
		Color inactive = Blend(sky, Color.FromRgb(198, 211, 222), 0.58);
		_aeroActiveCaption = FrozenGradient(
			ColorMath.Lighten(sky, 0.68),
			ColorMath.Lighten(sky, 0.46),
			ColorMath.Lighten(sky, 0.27),
			ColorMath.Lighten(sky, 0.10));
		_aeroInactiveCaption = FrozenGradient(
			ColorMath.Lighten(inactive, 0.55),
			ColorMath.Lighten(inactive, 0.32),
			inactive,
			ColorMath.Multiply(inactive, 0.08));
		_aeroActiveFrame = FrozenGradient(
			ColorMath.Lighten(sky, 0.34),
			ColorMath.Lighten(sky, 0.14),
			ColorMath.Multiply(sky, 0.12),
			ColorMath.Multiply(sky, 0.24));
		_aeroInactiveFrame = FrozenGradient(
			ColorMath.Lighten(inactive, 0.25),
			inactive,
			ColorMath.Multiply(inactive, 0.08),
			ColorMath.Multiply(inactive, 0.16));
		_aeroNativeActiveCaption = FrozenGradient(
			WithAlpha(ColorMath.Lighten(sky, 0.68), 158),
			WithAlpha(ColorMath.Lighten(sky, 0.46), 142),
			WithAlpha(ColorMath.Lighten(sky, 0.27), 132),
			WithAlpha(ColorMath.Lighten(sky, 0.10), 148));
		_aeroNativeInactiveCaption = FrozenGradient(
			WithAlpha(ColorMath.Lighten(inactive, 0.55), 136),
			WithAlpha(ColorMath.Lighten(inactive, 0.32), 120),
			WithAlpha(inactive, 112),
			WithAlpha(ColorMath.Multiply(inactive, 0.08), 124));
		_aeroNativeActiveFrame = FrozenGradient(
			WithAlpha(ColorMath.Lighten(sky, 0.34), 126),
			WithAlpha(ColorMath.Lighten(sky, 0.14), 116),
			WithAlpha(ColorMath.Multiply(sky, 0.12), 108),
			WithAlpha(ColorMath.Multiply(sky, 0.24), 124));
		_aeroNativeInactiveFrame = FrozenGradient(
			WithAlpha(ColorMath.Lighten(inactive, 0.25), 110),
			WithAlpha(inactive, 102),
			WithAlpha(ColorMath.Multiply(inactive, 0.08), 96),
			WithAlpha(ColorMath.Multiply(inactive, 0.16), 108));
		_aeroActiveOutline = ColorMath.Frozen(ColorMath.Multiply(sky, 0.42));
		_aeroInactiveOutline = ColorMath.Frozen(ColorMath.Multiply(inactive, 0.34));
	}

	private static Brush FrozenGradient(Color top, Color upper, Color lower, Color bottom)
	{
		LinearGradientBrush gradient = new LinearGradientBrush
		{
			StartPoint = new Point(0.0, 0.0),
			EndPoint = new Point(0.0, 1.0)
		};
		gradient.GradientStops.Add(new GradientStop(top, 0.0));
		gradient.GradientStops.Add(new GradientStop(upper, 0.47));
		gradient.GradientStops.Add(new GradientStop(lower, 0.51));
		gradient.GradientStops.Add(new GradientStop(bottom, 1.0));
		gradient.Freeze();
		return gradient;
	}

	private static Color Blend(Color from, Color to, double amount)
	{
		amount = Math.Clamp(amount, 0.0, 1.0);
		return Color.FromRgb(
			(byte)Math.Round(from.R + (to.R - from.R) * amount),
			(byte)Math.Round(from.G + (to.G - from.G) * amount),
			(byte)Math.Round(from.B + (to.B - from.B) * amount));
	}

	private static Color WithAlpha(Color color, byte alpha)
	{
		return Color.FromArgb(alpha, color.R, color.G, color.B);
	}

	private void UpdateMaxGlyph()
	{
		bool max = base.WindowState == WindowState.Maximized;
		MaxBtn.Content = null;
		MaxBtn.ContentTemplate = (DataTemplate)base.Resources[max ? "RestoreGlyph" : "MaxGlyph"];
		MaxBtn.ToolTip = (max ? "Restore" : "Maximize");
		WinBorder.BorderThickness = max || _nativeAeroFrame ? new Thickness(0.0) : new Thickness(1.0);
		ApplyClientFrameMetrics();
		ApplyShadow();
		ApplyState(base.IsActive);
	}

	private void OnCaptionIconMouseDown(object sender, MouseButtonEventArgs e)
	{
		if (e.ChangedButton != MouseButton.Left)
		{
			return;
		}
		if (e.ClickCount >= 2)
		{
			Close();
		}
		else
		{
			Point menuPoint = PointToScreen(new Point(0.0, 30.0));
			SystemCommands.ShowSystemMenu(this, menuPoint);
		}
		e.Handled = true;
	}

	private void OnMinimize(object sender, RoutedEventArgs e)
	{
		base.WindowState = WindowState.Minimized;
	}

	private void OnMaximizeRestore(object sender, RoutedEventArgs e)
	{
		base.WindowState = ((base.WindowState != WindowState.Maximized) ? WindowState.Maximized : WindowState.Normal);
	}

	private void OnClose(object sender, RoutedEventArgs e)
	{
		Close();
	}

	public static void ShowSample()
	{
		Win81Window sample = _sample;
		if (sample != null && sample.IsVisible)
		{
			_sample.Activate();
			return;
		}
		Win81Window w = new Win81Window
		{
			Title = "Windows 8.1 Layer"
		};
		StackPanel panel = new StackPanel
		{
			Margin = new Thickness(44.0, 34.0, 44.0, 40.0)
		};
		panel.Children.Add(new TextBlock
		{
			Text = "Windows 8.1 Layer",
			FontFamily = new FontFamily("Segoe UI Light"),
			FontSize = 34.0,
			Foreground = Brushes.White
		});
		panel.Children.Add(new TextBlock
		{
			Text = "Authentic Windows 8.1 experience, rebuilt on Windows 10.",
			FontFamily = new FontFamily("Segoe UI"),
			FontSize = 14.0,
			Foreground = new SolidColorBrush(Color.FromArgb(204, byte.MaxValue, byte.MaxValue, byte.MaxValue)),
			Margin = new Thickness(0.0, 8.0, 0.0, 0.0)
		});
		panel.Children.Add(new TextBlock
		{
			Text = "This window uses the tool's own Win8.1 chrome - a flat title bar coloured from your Start background, with authentic minimize, maximize/restore and close buttons. They are fully functional and the close button hovers red, like Windows 10/11.",
			TextWrapping = TextWrapping.Wrap,
			FontFamily = new FontFamily("Segoe UI"),
			FontSize = 13.0,
			Foreground = new SolidColorBrush(Color.FromArgb(170, byte.MaxValue, byte.MaxValue, byte.MaxValue)),
			Margin = new Thickness(0.0, 22.0, 0.0, 0.0)
		});
		w.SetBody(panel);
		_sample = w;
		w.Show();
	}
}
