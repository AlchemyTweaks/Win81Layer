#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Win81Layer;

public sealed partial class LiveTileService
{
	private readonly Dictionary<TileVm, string> _metroFaceKeys = new();
	private GoogleServices.AgendaSnapshot? _agendaSnapshot;
	private NewsFeedService.Feed? _newsSnapshot;
	private string _newsUrl = NewsFeedService.DefaultUrl;
	private bool _metroReady, _agendaFailed, _newsFailed, _newsFetching, _disposed;
	private long _googleRevision, _newsRevision, _lastNewsTicks;
	private CancellationTokenSource? _newsCancellation;
	internal Func<string, CancellationToken, Task<NewsFeedService.Feed>> NewsFetcher = (url, token) => NewsFeedService.GetAsync(url, cancellation: token);
	internal bool MetroReady => _metroReady;
	internal NewsFeedService.Feed? CurrentNews => _newsSnapshot;

	private async Task WarmMetroTilesAsync()
	{
		try
		{
			_newsUrl = SettingsStore.FastSnapshot.NewsFeedUrl;
			long newsRevision = _newsRevision, googleRevision = _googleRevision;
			var cached = await Task.Run(() =>
			{
				MetroLiveTileArt.Preload();
				string? agendaKey = GoogleAuth.AgendaCacheKey;
				return (NewsFeedService.Cached(_newsUrl), agendaKey != null ? LiveTileDataCache.Load<GoogleServices.AgendaSnapshot>(agendaKey, encrypted: true) : null);
			});
			if (_disposed) return;
			if (_newsRevision == newsRevision) _newsSnapshot = cached.Item1;
			if (_googleRevision == googleRevision && GoogleAuth.HasCalendarAccess) _agendaSnapshot = cached.Item2;
		}
		catch (Exception ex) { Logger.Log("Metro tile warmup: " + ex.GetType().Name); }
		finally { if (!_disposed) { _metroReady = true; QueueUpdate(); } }
	}

	private void EnsureMetroFace(TileVm tile, bool motion)
	{
		if (!_metroReady) return;
		DateTimeOffset now = DateTimeOffset.Now;
		bool signedIn = GoogleAuth.HasCalendarAccess;
		int newsIndex = (int)(_tick / 24);
		string key = string.Join("|", tile.Size, TileMetrics.Scale, now.ToString("yyyyMMddHHmmzzz"), signedIn,
			tile.Live == LiveKind.News ? _newsRevision + ":" + _newsSnapshot?.FetchedAt.Ticks + ":" + newsIndex + ":" + _newsFailed + ":" + _newsSnapshot?.Stale : "",
			tile.Live == LiveKind.Agenda ? _agendaSnapshot?.FetchedAt.Ticks + ":" + _agendaSnapshot?.Stale + ":" + _agendaFailed : "");
		tile.MetroMotionEnabled = motion && !tile.LiveOff;
		if (_metroFaceKeys.TryGetValue(tile, out string? previous) && previous == key) return;
		tile.MetroVisual = MetroLiveTileArt.Build(tile.Live, tile.Size, now, signedIn ? _agendaSnapshot : null, signedIn, _newsSnapshot, newsIndex,
			tile.Live == LiveKind.Agenda ? _agendaFailed : _newsFailed);
		tile.LivePrimary = tile.Live.ToString();
		tile.LiveSecondary = "";
		tile.LiveSymbol = "";
		_metroFaceKeys[tile] = key;
	}

	private void RefreshMetroFaces()
	{
		if (_disposed || !_timer.IsEnabled) return;
		bool motion = WeatherMotionAllowed();
		foreach (TileVm tile in _tiles())
			if (!tile.LiveOff && tile.Live is LiveKind.Clock or LiveKind.Agenda or LiveKind.News) EnsureMetroFace(tile, motion);
	}
	private void UpdateNewsRefresh()
	{
		HashSet<TileVm> tiles = new(_tiles());
		foreach (TileVm old in _metroFaceKeys.Keys.Where(t => !tiles.Contains(t)).ToArray()) _metroFaceKeys.Remove(old);
		foreach (TileVm old in _weatherFaceKey.Keys.Where(t => !tiles.Contains(t)).ToArray()) _weatherFaceKey.Remove(old);
		if (!_disposed && _timer.IsEnabled && !_newsFetching && tiles.Any(t => t.Live == LiveKind.News && !t.LiveOff) &&
			(_lastNewsTicks == 0 || DateTime.UtcNow.Ticks - _lastNewsTicks > TimeSpan.FromMinutes(_newsFailed ? 2 : 10).Ticks))
			_ = RefreshNewsAsync();
	}
	public void SetNewsSource(string url)
	{
		url = NewsFeedService.NormalizeUrl(url) ?? NewsFeedService.DefaultUrl;
		_newsRevision++;
		_newsCancellation?.Cancel();
		_newsUrl = url;
		_newsSnapshot = null;
		_newsFailed = false;
		_lastNewsTicks = 0;
		_metroFaceKeys.Clear();
		RefreshMetroFaces();
		if (!_newsFetching && _timer.IsEnabled) _ = RefreshNewsAsync();
	}
	private async Task RefreshNewsAsync()
	{
		_newsFetching = true;
		long revision = _newsRevision;
		string url = _newsUrl;
		using CancellationTokenSource cancellation = new();
		_newsCancellation = cancellation;
		try
		{
			NewsFeedService.Feed result = await NewsFetcher(url, cancellation.Token);
			if (_disposed || revision != _newsRevision) return;
			_newsSnapshot = result;
			_newsFailed = result.Stale;
		}
		catch (OperationCanceledException) { }
		catch { if (revision == _newsRevision) _newsFailed = true; }
		finally
		{
			_newsFetching = false;
			_newsCancellation = null;
			if (revision == _newsRevision) _lastNewsTicks = DateTime.UtcNow.Ticks;
			_metroFaceKeys.Clear();
			RefreshMetroFaces();
		}
	}
}
