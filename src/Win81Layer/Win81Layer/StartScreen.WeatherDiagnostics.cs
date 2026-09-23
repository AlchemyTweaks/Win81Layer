#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace Win81Layer;

public partial class StartScreen
{
	internal Task<(Dictionary<TileSize, BitmapSource> Logos, Dictionary<string, bool> Checks, Dictionary<TileSize, long> RenderMs)> QaWeatherFlipsAsync(WeatherService.Result data)
		=> QaMetroFlipsAsync(LiveKind.Weather, size => WeatherTileArt.Build(data, data.City ?? "QA", size, TileMetrics.Scale, true));

	internal async Task<(Dictionary<TileSize, BitmapSource> Logos, Dictionary<string, bool> Checks, Dictionary<TileSize, long> RenderMs)> QaMetroFlipsAsync(LiveKind kind, Func<TileSize, MetroTileVisual> build)
	{
		if (!_transitionDiagnostics) throw new InvalidOperationException("Weather flip QA requires an isolated Start instance.");
		Dictionary<TileSize, BitmapSource> logos = new();
		Dictionary<string, bool> checks = new();
		Dictionary<TileSize, long> renderMs = new();
		MotionMode previousMotion = Motion.Mode;
		Motion.Mode = MotionMode.Fast;
		ScrollViewer viewport = new() { HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled };
		Window window = new() { Width = 350, Height = 350, Left = -8000, Top = -8000, ShowActivated = false, ShowInTaskbar = false, WindowStyle = WindowStyle.None, Content = viewport };
		try
		{
			window.Show();
			foreach (TileSize size in new[] { TileSize.Small, TileSize.Medium, TileSize.Wide, TileSize.Large })
			{
				TileVm tile = LiveTiles.Create(kind);
				tile.Size = size;
				tile.MetroVisual = build(size);
				tile.MetroMotionEnabled = true;
				Button button = new() { DataContext = tile, Style = (Style)Resources["TileButton"], Width = tile.PixelWidth, Height = tile.PixelHeight };
				Canvas canvas = new() { Width = 1400, Height = 330 };
				canvas.Children.Add(button);
				viewport.Content = canvas;
				viewport.ScrollToHorizontalOffset(0);
				window.UpdateLayout();
				await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
				Grid host = (Grid)button.Template.FindName("MetroFlipHost", button);
				LiveFlipState state = _liveFlips.Single(x => x.Host == host);
				checks[size + "RegisteredOnce"] = state.ContentUp && state.ContentDwellMinMs == WeatherTileFlipPolicy.For(kind).ContentMin;
				state.NextFlip = 0;
				FlipDueTiles();
				await Task.Delay(650);
				checks[size + "LogoShown"] = !state.ContentUp && !state.IsFlipping && state.IconFace.IsVisible && !state.ContentFace.IsVisible && Math.Abs(state.Flip.ScaleY - 1) < 0.001;
				Stopwatch renderTimer = Stopwatch.StartNew();
				RenderTargetBitmap bitmap = new((int)tile.PixelWidth, (int)tile.PixelHeight, 96, 96, PixelFormats.Pbgra32);
				bitmap.Render(button);
				bitmap.Freeze();
				logos[size] = bitmap;
				renderMs[size] = renderTimer.ElapsedMilliseconds;
				state.NextFlip = 0;
				FlipDueTiles();
				await Task.Delay(650);
				checks[size + "ForecastRestored"] = state.ContentUp && !state.IsFlipping && state.ContentFace.IsVisible && !state.IconFace.IsVisible;

				state.NextFlip = 0;
				FlipDueTiles();
				tile.MetroMotionEnabled = false;
				await Task.Delay(650);
				checks[size + "PowerPauseCancelsFlip"] = state.ContentUp && !state.IsFlipping && !state.Flip.HasAnimatedProperties && Math.Abs(state.Flip.ScaleY - 1) < 0.001;
				tile.MetroMotionEnabled = true;
				Motion.Mode = MotionMode.Reduced;
				state.NextFlip = 0;
				FlipDueTiles();
				checks[size + "ReducedMotionStatic"] = !state.IsFlipping && state.ContentUp;
				Motion.Mode = MotionMode.Fast;

				viewport.ScrollToHorizontalOffset(800);
				window.UpdateLayout();
				await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
				state.NextFlip = 0;
				FlipDueTiles();
				checks[size + "OutsideViewportStatic"] = !CanFlipMetro(state) && !state.IsFlipping;
				viewport.ScrollToHorizontalOffset(0);
				window.UpdateLayout();
				await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
				state.NextFlip = 0;
				FlipDueTiles();
				TileSize nextSize = size == TileSize.Wide ? TileSize.Large : TileSize.Wide;
				tile.Size = nextSize;
				tile.MetroVisual = build(nextSize);
				button.Width = tile.PixelWidth;
				button.Height = tile.PixelHeight;
				window.UpdateLayout();
				await Task.Delay(650);
				checks[size + "ResizeCancelsFlip"] = state.ContentUp && !state.IsFlipping && !state.Flip.HasAnimatedProperties && Math.Abs(state.Flip.ScaleY - 1) < 0.001;

				tile.LiveOff = true;
				window.UpdateLayout();
				checks[size + "LiveOffShowsStaticLogo"] = !host.IsVisible && ((Grid)button.Template.FindName("LiveIconFace", button)).IsVisible && !state.IsFlipping;
				tile.LiveOff = false;
				tile.MetroVisual = null;
				window.UpdateLayout();
				Grid fallback = (Grid)button.Template.FindName("LiveIconFace", button);
				checks[size + "MissingDataShowsLogo"] = !host.IsVisible && fallback.IsVisible;
				viewport.Content = null;
				await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
				checks[size + "UnloadedUnregistered"] = _liveFlips.All(x => x.Host != host) && !state.Flip.HasAnimatedProperties;
			}
			checks["NoDedicatedTimer"] = _flipTimer == null;
		}
		finally
		{
			StopLiveFlips();
			window.Close();
			Motion.Mode = previousMotion;
		}
		return (logos, checks, renderMs);
	}
}
