using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Win81Layer;

// Offscreen QA of the Action Center (--action-center-test, --uxpaneltest and the seasonal suites): the real panel, built and
// laid out as it is shown, rendered to a PNG, never placed on screen, never activated, never refreshed from live state.
public sealed partial class ActionCenter
{
	// device: Laptop shows every tile (radios forced on) and both sliders; Desktop the six tiles a radio-less desktop has
	// and the volume slider. maxHeightDip bounds the panel as a short work area would. currentTheme renders with the live
	// Start theme (seasonal and Dark rest QA); otherwise a fixed blue accent.
	internal static ActionCenterQaMetrics QaRenderMetro(string outPath, ActionCenterQaDevice device, bool currentTheme = false, double maxHeightDip = double.PositiveInfinity, double widthDip = PanelWidthDip)
	{
		Stopwatch total = Stopwatch.StartNew();
		Stopwatch build = Stopwatch.StartNew();
		ActionCenter panel = new ActionCenter
		{
			Left = -5000.0,
			Top = -5000.0,
			Topmost = false,
			ShowActivated = false
		};
		build.Stop();
		panel._qaRendering = true;
		bool laptop = device == ActionCenterQaDevice.Laptop;
		panel._isLaptop = laptop;
		panel._brightnessAvailable = laptop;
		panel._qaRadios = laptop;
		panel._wifiOn = true;
		panel._btOn = true;
		panel._airplaneOn = false;
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
		System.Windows.Media.Color blue = System.Windows.Media.Color.FromRgb(0, 112, 192);
		System.Windows.Media.Color cyan = System.Windows.Media.Color.FromRgb(0, 174, 239);
		if (currentTheme)
		{
			panel.ApplyMetroTheme();
		}
		else
		{
			panel.ApplyMetroPalette(blue, cyan);
		}
		double width = widthDip;
		double height = panel.LayoutPanel(width, maxHeightDip);
		panel.Width = width;
		panel.Height = height;
		panel.Show();
		panel._slide.X = 0.0;
		panel._slide.Y = 0.0;
		panel._metroRoot.Opacity = 1.0;
		panel._metroRoot.CacheMode = null;
		FrameworkElement root = panel._frame;
		root.ApplyTemplate();
		root.Measure(new Size(width, height));
		root.Arrange(new Rect(0.0, 0.0, width, height));
		root.UpdateLayout();
		RenderTargetBitmap bitmap = new RenderTargetBitmap((int)Math.Ceiling(width), (int)Math.Ceiling(height), 96.0, 96.0, PixelFormats.Pbgra32);
		bitmap.Render(root);
		PngBitmapEncoder encoder = new PngBitmapEncoder();
		encoder.Frames.Add(BitmapFrame.Create(bitmap));
		using (FileStream stream = File.Create(outPath)) encoder.Save(stream);

		Rect RectOf(FrameworkElement e)
		{
			try
			{
				if (!e.IsVisible || e.ActualWidth <= 0.0)
				{
					return Rect.Empty;
				}
				return e.TransformToAncestor(root).TransformBounds(new Rect(0.0, 0.0, e.ActualWidth, e.ActualHeight));
			}
			catch
			{
				return Rect.Empty;
			}
		}
		List<ActionCenterQaTile> tileStates = new List<ActionCenterQaTile>();
		foreach (MetroTile tile in panel._metroTiles.Where(t => t.Root.Visibility == Visibility.Visible))
		{
			bool tileOn = false;
			try { tileOn = tile.IsOn?.Invoke() ?? false; } catch { }
			tileStates.Add(new ActionCenterQaTile(tile.Id, tile.IsOn != null, tileOn, tile.Fill.Color,
				(tile.Root.BorderBrush as SolidColorBrush)?.Color ?? System.Windows.Media.Colors.Transparent, tile.Root.BorderThickness.Left,
				(tile.Label?.Foreground as SolidColorBrush)?.Color ?? System.Windows.Media.Colors.Transparent, tile.Check.Visibility == Visibility.Visible,
				(tile.Status.Foreground as SolidColorBrush)?.Color ?? System.Windows.Media.Colors.Transparent, tile.Icon.Opacity,
				RectOf(tile.Root), tile.Label != null ? RectOf(tile.Label) : Rect.Empty));
		}
		Rect viewportRect = RectOf(panel._tileViewport);
		Rect slidersRect = RectOf(panel._sliderHost);
		Rect footerRect = RectOf(panel._footer);
		Rect linkRect = RectOf(panel._settingsLink);
		Rect labelRect = RectOf(panel._settingsLabel);
		Rect gearRect = RectOf(panel._settingsGear);
		Rect powerRect = RectOf(panel._powerButton);
		Rect volumeIconRect = panel._volumeIconHost.Child is FrameworkElement vi ? RectOf(vi) : Rect.Empty;
		// every visible tile the same width (also when the tile viewport scrolls: its scrollbar sits in the side padding)
		List<double> tileWidths = tileStates.Where(t => !t.TileRect.IsEmpty).Select(t => t.TileRect.Width).ToList();
		bool widthsEqual = tileWidths.Count > 0 && tileWidths.Max() - tileWidths.Min() < 0.5;
		// the body between the tile block and the first slider row (nothing is drawn there)
		Rect probe = viewportRect.IsEmpty
			? Rect.Empty
			: new Rect(FrameDip + SidePaddingDip, viewportRect.Bottom, Math.Max(1.0, width - 2.0 * (FrameDip + SidePaddingDip)), TilesToSlidersDip);
		bool insideFrame = !slidersRect.IsEmpty && !footerRect.IsEmpty
			&& slidersRect.Top >= viewportRect.Bottom - 0.5 && footerRect.Bottom <= height - FrameDip + 0.5 && footerRect.Top >= slidersRect.Bottom - 0.5
			&& Math.Abs(footerRect.Height - (FooterDip + 1.0)) < 0.5;
		double viewportHeight = panel._tileViewport.ActualHeight;
		double scrollable = panel._tileScroll.ScrollableHeight;
		List<string> texts = new List<string>();
		int scrollViewers = 0;
		bool onlyTileScroll = true;
		void Walk(DependencyObject d)
		{
			if (d is TextBlock tb && !string.IsNullOrEmpty(tb.Text))
			{
				texts.Add(tb.Text);
			}
			if (d is ScrollViewer sv)
			{
				scrollViewers++;
				onlyTileScroll &= ReferenceEquals(sv, panel._tileScroll);
			}
			int n = VisualTreeHelper.GetChildrenCount(d);
			for (int i = 0; i < n; i++)
			{
				Walk(VisualTreeHelper.GetChild(d, i));
			}
		}
		Walk(root);
		System.Windows.Media.Color bodyTop = panel._themeBackground.GradientStops[0].Color;
		System.Windows.Media.Color bodyBottom = panel._themeBackground.GradientStops[^1].Color;
		System.Windows.Media.Color frameColor = panel._frameBrush.Color;
		System.Windows.Media.Color footerColor = panel._footerBrush.Color;
		System.Windows.Media.Color linkColor = panel._linkBrush.Color;
		string sliderFill = ((panel._volume as MetroSlider)?.FillBrush as SolidColorBrush)?.Color.ToString() ?? string.Empty;
		bool restPalette = panel._restPalette;
		int checksShown = panel._metroTiles.Count(t => t.Root.Visibility == Visibility.Visible && t.Check.Visibility == Visibility.Visible);

		// the panel follows the Start theme: a red theme changes the body, the blue one brings it back exactly
		System.Windows.Media.Color before = panel._themeBackground.GradientStops[0].Color;
		panel.ApplyMetroPalette(blue, cyan);
		System.Windows.Media.Color blueTop = panel._themeBackground.GradientStops[0].Color;
		panel.ApplyMetroPalette(System.Windows.Media.Color.FromRgb(170, 24, 36), System.Windows.Media.Color.FromRgb(235, 50, 60));
		System.Windows.Media.Color red = panel._themeBackground.GradientStops[0].Color;
		panel.ApplyMetroPalette(blue, cyan);
		System.Windows.Media.Color after = panel._themeBackground.GradientStops[0].Color;
		System.Windows.Media.Color themeBase = panel._themeBase;

		// routes: the 11 tiles and the 2 footer actions, each with its exact contract
		Dictionary<string, string> routeMap = new Dictionary<string, string>(StringComparer.Ordinal);
		foreach (MetroTile tile in panel._metroTiles)
		{
			routeMap[tile.Id] = tile.RouteContract;
		}
		foreach ((string Id, string RouteContract, Func<System.Threading.Tasks.Task> Action) footer in panel._footerActions)
		{
			routeMap[footer.Id] = footer.RouteContract;
		}
		bool exactRoutes = routeMap.Count == MetroRouteContracts.Count
			&& MetroRouteContracts.All(pair => routeMap.TryGetValue(pair.Key, out string contract) && string.Equals(contract, pair.Value, StringComparison.Ordinal));
		int functional = panel._metroTiles.Count(tile => tile.Action != null) + panel._footerActions.Count(f => f.Action != null);
		ProcessStartInfo mapsStart = CreateExternalStartInfo(GoogleMapsTarget);

		// device rules: laptop-only tiles follow the battery, radios follow the adapters
		bool savedLaptop = panel._isLaptop;
		bool savedBrightness = panel._brightnessAvailable;
		bool? savedRadios = panel._qaRadios;
		panel._isLaptop = true;
		panel._brightnessAvailable = true;
		panel._qaRadios = true;
		panel.ArrangeMetroTiles();
		bool laptopRules = panel._metroTiles.All(tile => tile.Root.Visibility == Visibility.Visible) && panel._visibleTileRows == 3;
		panel._isLaptop = false;
		panel._brightnessAvailable = false;
		panel._qaRadios = false;
		panel.ArrangeMetroTiles();
		bool desktopRules = panel._metroTiles.Where(tile => tile.LaptopOnly || tile.Id == "wifi" || tile.Id == "airplane").All(tile => tile.Root.Visibility == Visibility.Collapsed)
			&& panel._metroTiles.Count(tile => tile.Root.Visibility == Visibility.Visible) == 6 && panel._visibleTileRows == 2;
		panel._isLaptop = savedLaptop;
		panel._brightnessAvailable = savedBrightness;
		panel._qaRadios = savedRadios;
		panel.ArrangeMetroTiles();
		int visibleTiles = panel._metroTiles.Count(tile => tile.Root.Visibility == Visibility.Visible);
		string[] visibleOrder = panel._metroTiles.Where(tile => tile.Root.Visibility == Visibility.Visible).Select(tile => tile.Id).ToArray();
		double tileWidth = panel._metroTiles.FirstOrDefault(tile => tile.Root.Visibility == Visibility.Visible)?.Root.ActualWidth ?? 0.0;
		total.Stop();

		// vector glyphs only: the slider icons are white shapes on the 24-unit grid, and no tile glyph is a bitmap (the Wi-Fi
		// glyph is the vector network renderer's drawing)
		bool ShapesOnly(UIElement? e) => e is Viewbox { Child: Canvas c } && c.Children.Count > 0 && c.Children.OfType<UIElement>().All(x => x is System.Windows.Shapes.Shape);
		bool sliderIconsVector = ShapesOnly(panel._volumeIconHost.Child) && ShapesOnly(panel._brightnessIconHost.Child);
		bool tileIconsBitmapFree = panel._metroTiles.All(t => t.Icon is Viewbox { Child: Canvas c }
			&& c.Children.OfType<Image>().All(img => img.Source is DrawingImage));
		int[] volumeWaves = { VolumeWaves(0, false), VolumeWaves(20, false), VolumeWaves(50, false), VolumeWaves(100, false), VolumeWaves(100, true) };

		// the Notifications tile hands over to Windows' own notification center: Win+N, captured by the QA seam (no real input);
		// when the chord cannot be sent it opens Settings > Notifications (recorded, never opened). Connect does the same with
		// Win+K.
		panel.QaChords = new List<byte[]>();
		panel.QaLaunches = new List<string>();
		byte[]? chord = null;
		string notificationsFallback = string.Empty;
		string connectChord = string.Empty;
		string connectFallback = string.Empty;
		string allSettingsClick = string.Empty;
		string allSettingsShift = string.Empty;
		try
		{
			panel.OpenNotificationCenter().GetAwaiter().GetResult();
			chord = panel.QaChords.FirstOrDefault();
			bool launchedOnSuccess = panel.QaLaunches.Count > 0;
			panel.QaChords.Clear();
			panel.QaChordFails = true;
			panel.OpenNotificationCenter().GetAwaiter().GetResult();
			notificationsFallback = launchedOnSuccess ? "launched-on-success" : string.Join("|", panel.QaLaunches);
			panel.QaLaunches.Clear();
			panel.QaChords.Clear();
			panel.QaChordFails = false;
			panel.OpenConnect().GetAwaiter().GetResult();
			connectChord = string.Join(",", panel.QaChords.FirstOrDefault() ?? Array.Empty<byte>());
			panel.QaChordFails = true;
			panel.OpenConnect().GetAwaiter().GetResult();
			connectFallback = string.Join("|", panel.QaLaunches);
			// All settings: PC settings on a click, Windows Settings on a Shift+click (recorded, never opened)
			panel.QaLaunches.Clear();
			panel.OpenAllSettings(shift: false).GetAwaiter().GetResult();
			allSettingsClick = string.Join("|", panel.QaLaunches);
			panel.QaLaunches.Clear();
			panel.OpenAllSettings(shift: true).GetAwaiter().GetResult();
			allSettingsShift = string.Join("|", panel.QaLaunches);
		}
		catch
		{
		}
		finally
		{
			panel.QaChordFails = false;
		}
		ActionCenterQaMetrics metrics = new ActionCenterQaMetrics
		{
			Tiles = tileStates,
			Device = device.ToString(),
			WidthDiu = width,
			HeightDiu = height,
			ExpectedHeightDiu = ContentHeightDip(panel._visibleTileRows, panel.SliderRowCount()),
			MaxHeightDiu = double.IsInfinity(maxHeightDip) ? 0.0 : maxHeightDip,
			TileRows = panel._visibleTileRows,
			SliderRows = panel.SliderRowCount(),
			DefinedTiles = panel._metroTiles.Count,
			VisibleTiles = visibleTiles,
			TileColumns = panel._tiles.ColumnDefinitions.Count(c => c.Width.IsStar),
			TileOrder = panel._metroTiles.Select(tile => tile.Id).ToArray(),
			VisibleTileOrder = visibleOrder,
			TileWidthDiu = tileWidth,
			TileHeightDiu = TileHeightDip,
			TileViewportHeightDiu = viewportHeight,
			TileScrollableHeightDiu = scrollable,
			SlidersAndFooterInside = insideFrame,
			FunctionalRoutes = functional,
			RouteContracts = routeMap.Count(pair => !string.IsNullOrWhiteSpace(pair.Value)),
			RouteContractMap = routeMap,
			StatefulTiles = panel._metroTiles.Count(tile => tile.IsOn != null),
			IconsAreVectors = panel._metroTiles.All(tile => tile.Icon is Viewbox viewbox && viewbox.Child is Canvas),
			IconsUseUniformPremiumGeometry = panel._metroTiles.All(tile => tile.Icon is Viewbox viewbox
				&& Math.Abs(viewbox.Width - TileIconSize) < 0.1
				&& Math.Abs(viewbox.Height - TileIconSize) < 0.1
				&& viewbox.Child is Canvas { Width: 24.0, Height: 24.0 }),
			TileIconSizeDiu = TileIconSize,
			ExactRouteContracts = exactRoutes,
			MapsUsesGoogleMaps = panel._metroTiles.Any(tile => tile.Id == "maps" && tile.RouteContract == "browser:" + GoogleMapsTarget),
			ExternalLinksUseDefaultHandler = mapsStart.UseShellExecute && string.Equals(mapsStart.FileName, GoogleMapsTarget, StringComparison.Ordinal),
			MapsTarget = GoogleMapsTarget,
			ActiveChecksPresent = panel._metroTiles.Where(tile => tile.IsOn != null).All(tile => tile.Check != null && Math.Abs(tile.Check.Width - 12.0) < 0.1 && Math.Abs(tile.Check.StrokeThickness - 2.2) < 0.01),
			ChecksShown = checksShown,
			FourColumnReferenceOrder = TileReferenceOrder.SequenceEqual(panel._metroTiles.Select(tile => tile.Id)),
			LaptopRulesPassed = laptopRules,
			DesktopRulesPassed = desktopRules,
			SlidersAreLiveControls = panel._volume is MetroSlider && panel._brightness is MetroSlider,
			NotificationsChord = chord == null ? string.Empty : string.Join(",", chord),
			NotificationsChordCaptured = chord != null && chord.SequenceEqual(new byte[] { 91, 78 }),
			AllSettingsClick = allSettingsClick,
			AllSettingsShiftClick = allSettingsShift,
			Texts = texts.Distinct().ToArray(),
			ScrollViewers = scrollViewers,
			OnlyScrollViewerIsTileViewport = scrollViewers == 1 && onlyTileScroll,
			FooterRect = footerRect,
			FooterLinkRect = linkRect,
			PowerButtonRect = powerRect,
			BackgroundProbe = probe,
			SlidersRect = slidersRect,
			ThemeBackgroundSynchronized = blueTop == after && themeBase == blue,
			ThemeSwitchPassed = red != blueTop && after == blueTop,
			ThemeBase = themeBase.ToString(),
			RestPalette = restPalette,
			BodyTop = bodyTop.ToString(),
			BodyBottom = bodyBottom.ToString(),
			BorderColor = frameColor.ToString(),
			FooterColor = footerColor.ToString(),
			LinkColor = linkColor.ToString(),
			SliderFill = sliderFill,
			OpenTravelDiu = Motion.DistanceFor(OpenTravel, MotionMode.Authentic),
			CloseTravelDiu = Motion.DistanceFor(CloseTravel, MotionMode.Authentic),
			ReducedOpenTravelDiu = Motion.DistanceFor(OpenTravel, MotionMode.Reduced),
			ReducedCloseTravelDiu = Motion.DistanceFor(CloseTravel, MotionMode.Reduced),
			OpenMoveMs = (int)Motion.TokMs(Motion.Move(OpenTravel), MotionMode.Authentic),
			OpenFadeMs = (int)Motion.TokMs(Motion.Tok.FadeIn, MotionMode.Authentic),
			CloseMoveMs = (int)Motion.TokMs(Motion.Tok.ExitMove, MotionMode.Authentic),
			CloseFadeMs = (int)Motion.TokMs(Motion.Tok.FadeOut, MotionMode.Authentic),
			TileHoverMs = TileHoverMilliseconds,
			TileClickMs = TileClickMilliseconds,
			VisualBuildMs = build.ElapsedMilliseconds,
			RenderAndLayoutMs = total.ElapsedMilliseconds,
			RefreshWorkerActiveDuringDetachedRender = panel._runtimeCts != null,
			IdleRenderingHookInactive = !SmoothScroll.IsVerticalActive(panel._tileScroll),
			Screenshot = outPath
		};
		panel.Close();
		return metrics;
	}

	// QA: the Normal tones for a theme, as the panel computes them.
	internal static ActionCenterTones QaNormalTones(System.Windows.Media.Color themeBase, System.Windows.Media.Color accent, bool seasonal)
	{
		return NormalTones(themeBase, accent, seasonal);
	}
}
