using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows.Threading;

namespace Win81Layer;

public sealed partial class LiveTileService : IDisposable
{
	private readonly DispatcherTimer _timer;

	private readonly Func<IEnumerable<TileVm>> _tiles;

	private long _lastWeatherTicks;

	private bool _weatherFetching;

	private long _lastGoogleTicks;

	private bool _googleFetching;

	private long _lastUpdateTicks;

	private bool _updateQueued;

	private string _mailPrimary;

	private string _mailSecondary;

	private string? _agendaPrimary;

	private string _agendaSecondary;

	private long _tick;

	private WeatherService.Result? _lastWeather;

	private readonly Dictionary<TileVm, string> _weatherFaceKey = new Dictionary<TileVm, string>();

	private string _weatherCity = "Kalamata";

	private string _weatherUnits = "C";

	private long _weatherRequestRevision = 1;

	private bool _weatherAssetsReady;

	public string WeatherCity
	{
		get => _weatherCity;
		set
		{
			string city = string.IsNullOrWhiteSpace(value) ? "Kalamata" : value.Trim();
			if (string.Equals(_weatherCity, city, StringComparison.OrdinalIgnoreCase)) return;
			_weatherCity = city;
			long revision = ++_weatherRequestRevision;
			_lastWeather = WeatherService.LoadCached(city);
			_lastWeatherTicks = 0;
			_weatherFaceKey.Clear();
			SetWeatherLoadingFaces();
			if (_lastWeather.HasValue)
			{
				_ = WarmWeatherAssetsAsync(_lastWeather.Value, revision);
			}
			else
			{
				_weatherAssetsReady = true;
				ApplyWeatherFaces(null);
			}
		}
	}

	public string WeatherUnits
	{
		get => _weatherUnits;
		set
		{
			string units = string.Equals(value, "F", StringComparison.OrdinalIgnoreCase) ? "F" : "C";
			if (_weatherUnits == units) return;
			_weatherUnits = units;
			RefreshWeatherFaces();
		}
	}

	public LiveTileService(Func<IEnumerable<TileVm>> tiles, string weatherCity, string weatherUnits)
	{
		//IL_003d: Unknown result type (might be due to invalid IL or missing references)
		//IL_0042: Unknown result type (might be due to invalid IL or missing references)
		//IL_0055: Expected O, but got Unknown
		_mailPrimary = "";
		_mailSecondary = "";
		_agendaSecondary = "";
		_tiles = tiles;
		_weatherCity = string.IsNullOrWhiteSpace(weatherCity) ? "Kalamata" : weatherCity.Trim();
		_weatherUnits = string.Equals(weatherUnits, "F", StringComparison.OrdinalIgnoreCase) ? "F" : "C";
		_lastWeather = WeatherService.LoadCached(_weatherCity);
		_timer = new DispatcherTimer((DispatcherPriority)4)
		{
			Interval = TimeSpan.FromSeconds(1L)
		};
		_timer.Tick += delegate
		{
			Update();
		};
		_weatherAssetsReady = !_lastWeather.HasValue;
		if (_lastWeather.HasValue)
		{
			SetWeatherLoadingFaces();
			_ = WarmWeatherAssetsAsync(_lastWeather.Value, _weatherRequestRevision);
		}
		_ = WarmMetroTilesAsync();
	}

	public void Start()
	{
		if (App.SafeMode)
		{
			return;   // safe mode: tiles stay static (no flip timers / Weather-Mail-Agenda network fetches). Covers all call sites.
		}
		if (_timer.IsEnabled)
		{
			return;
		}
		_timer.Start();
		SetWeatherMotion(enabled: true);
		long now = Environment.TickCount64;
		if (_lastUpdateTicks == 0L || now - _lastUpdateTicks >= 500L)
		{
			QueueUpdate();
		}
	}

	private void QueueUpdate()
	{
		if (_updateQueued)
		{
			return;
		}
		_updateQueued = true;
		_timer.Dispatcher.BeginInvoke((Action)delegate
		{
			_updateQueued = false;
			if (_timer.IsEnabled)
			{
				Update();
			}
		}, DispatcherPriority.Background);
	}

	public void Stop()
	{
		_timer.Stop();
		SetWeatherMotion(enabled: false);
	}

	public void Update()
	{
		if (_disposed) return;
		_lastUpdateTicks = Environment.TickCount64;
		_tick++;
		DateTime now = DateTime.Now;
		bool weatherMotion = WeatherMotionAllowed();
		bool hasWeather = false;
		bool hasMail = false;
		bool hasAgenda = false;
		foreach (TileVm t in _tiles())
		{
			if (t.LiveOff)
			{
				if (t.IsMetroLiveTile) t.MetroMotionEnabled = false;
				continue;   // tile's live face is turned off -> do no per-tile work AND don't arm the weather/Google fetches below
			}
			switch (t.Live)
			{
			case LiveKind.Clock:
				EnsureMetroFace(t, weatherMotion);
				break;
			case LiveKind.Calendar:
				t.LivePrimary = now.Day.ToString();
				t.LiveSecondary = now.ToString("dddd, MMMM");
				break;
			case LiveKind.Agenda:
				hasAgenda = true;
				EnsureMetroFace(t, weatherMotion);
				break;
			case LiveKind.News:
				EnsureMetroFace(t, weatherMotion);
				break;
			case LiveKind.Mail:
				hasMail = true;
				if (GoogleAuth.HasMailAccess)
				{
					t.LivePrimary = _mailPrimary;
					t.LiveSecondary = (string.IsNullOrEmpty(_mailSecondary) ? "…" : _mailSecondary);
				}
				else
				{
					t.LivePrimary = "";
					t.LiveSecondary = (GoogleAuth.IsConfigured ? "Sign in for mail" : "");
				}
				break;
			case LiveKind.Weather:
				hasWeather = true;
				t.MetroMotionEnabled = weatherMotion;
				if (_lastWeather.HasValue)
				{
					EnsureWeatherFace(t);   // (re)compose the authentic face when the data or the tile size changed
				}
				else if (string.IsNullOrEmpty(t.LivePrimary))
				{
					t.LivePrimary = "…";
					t.LiveSecondary = WeatherCity;   // brief loading state before the first fetch lands
				}
				break;
			case LiveKind.Photo:
				if (_tick % 8 == 0L && t.Entry != null)
				{
					t.Entry.TileBrush = LiveTiles.NextPhotoFace();
				}
				break;
			case LiveKind.Folder:
				if ((_tick + t.CyclePhase) % 6 == 0)
				{
					t.AdvanceFolderCycle();
				}
				break;
			}
		}
		UpdateNewsRefresh();
		if (hasWeather && !_weatherFetching && (_lastWeatherTicks == 0L || DateTime.UtcNow.Ticks - _lastWeatherTicks > TimeSpan.FromMinutes(15L).Ticks))
		{
			RefreshWeatherAsync();
		}
		if ((hasMail | hasAgenda) && GoogleAuth.IsSignedIn && !_googleFetching && (_lastGoogleTicks == 0L || DateTime.UtcNow.Ticks - _lastGoogleTicks > TimeSpan.FromMinutes(5L).Ticks))
		{
			RefreshGoogleAsync(hasMail, hasAgenda);
		}
	}

	public void RefreshWeatherNow()
	{
		_lastWeatherTicks = 0L;
		if (!_weatherFetching)
		{
			RefreshWeatherAsync();
		}
	}

	public void RefreshGoogleNow()
	{
		_googleRevision++;
		_lastGoogleTicks = 0L;
		if (!_googleFetching)
		{
			RefreshGoogleAsync(mail: true, agenda: true);
		}
	}

	public void ClearGoogle()
	{
		_googleRevision++;
		_agendaSnapshot = null;
		_agendaFailed = false;
		LiveTileDataCache.Delete("agenda");
		_metroFaceKeys.Clear();
		_mailPrimary = "";
		_mailSecondary = "";
		_agendaPrimary = null;
		_agendaSecondary = "";
		_lastGoogleTicks = 0L;
		Update();
	}

	private async Task RefreshGoogleAsync(bool mail, bool agenda)
	{
		mail &= GoogleAuth.HasMailAccess;
		agenda &= GoogleAuth.HasCalendarAccess;
		if (!mail && !agenda) return;
		long revision = _googleRevision;
		string? cacheKey = GoogleAuth.AgendaCacheKey;
		_googleFetching = true;
		try
		{
			bool mailFailed = false;
			if (mail)
			{
				GoogleServices.MailInfo m = await GoogleServices.GetMailAsync();
				if (_disposed || revision != _googleRevision || !GoogleAuth.HasMailAccess) return;
				if ((object)m != null)
				{
					_mailPrimary = ((m.Unread > 0) ? m.Unread.ToString() : "");
					_mailSecondary = m.Summary;
				}
				else
				{
					mailFailed = true;
				}
			}
			if (agenda)
			{
				GoogleServices.AgendaSnapshot? a = await GoogleServices.GetAgendaSnapshotAsync();
				if (_disposed || revision != _googleRevision || !GoogleAuth.HasCalendarAccess) return;
				_agendaFailed = a == null;
				if (a != null)
				{
					_agendaSnapshot = a;
					if (cacheKey != null) await Task.Run(() => LiveTileDataCache.Save(cacheKey, a, encrypted: true));
					if (_disposed || revision != _googleRevision || cacheKey != GoogleAuth.AgendaCacheKey)
					{
						if (cacheKey != null) LiveTileDataCache.Delete(cacheKey);
						return;
					}
				}
				else if (_agendaSnapshot != null) _agendaSnapshot = _agendaSnapshot with { Stale = true };
				_metroFaceKeys.Clear();
				RefreshMetroFaces();
				mailFailed |= a == null;
			}
			_lastGoogleTicks = (mailFailed ? (DateTime.UtcNow.Ticks - TimeSpan.FromMinutes(4L).Ticks) : DateTime.UtcNow.Ticks);
		}
		catch (Exception ex)
		{
			Exception ex2 = ex;
			Logger.Log("Google tile refresh failed: " + ex2.Message);
			_lastGoogleTicks = DateTime.UtcNow.Ticks - TimeSpan.FromMinutes(4L).Ticks;
		}
		finally
		{
			_googleFetching = false;
			if (revision != _googleRevision)
			{
				_lastGoogleTicks = 0;
				if (!_disposed) _ = RefreshGoogleAsync(mail: true, agenda: true);
			}
		}
	}

	private async Task RefreshWeatherAsync()
	{
		_weatherFetching = true;
		long revision = _weatherRequestRevision;
		string requestedCity = WeatherCity;
		bool superseded = false;
		try
		{
			WeatherService.Result? r = await WeatherService.GetAsync(requestedCity);
			if (revision != _weatherRequestRevision)
			{
				superseded = true;
				return;
			}
			if (r.HasValue)
			{
				await Task.Run(() => WeatherTileArt.Preload(r.Value));
				if (revision != _weatherRequestRevision)
				{
					superseded = true;
					return;
				}
				_weatherAssetsReady = true;
				_lastWeather = r;
			}
			else if (_lastWeather.HasValue)
			{
				_lastWeather = WeatherService.MarkStale(_lastWeather.Value);
			}
			ApplyWeatherFaces(_lastWeather);
			_lastWeatherTicks = (r.HasValue ? DateTime.UtcNow.Ticks : (DateTime.UtcNow.Ticks - TimeSpan.FromMinutes(13L).Ticks));
		}
		catch (Exception ex)
		{
			Logger.Log("Weather refresh failed: " + ex.Message);
			if (_lastWeather.HasValue)
			{
				_lastWeather = WeatherService.MarkStale(_lastWeather.Value);
				ApplyWeatherFaces(_lastWeather);
			}
			_lastWeatherTicks = DateTime.UtcNow.Ticks - TimeSpan.FromMinutes(13L).Ticks;
		}
		finally
		{
			_weatherFetching = false;
			if (superseded)
			{
				_lastWeatherTicks = 0;
				_timer.Dispatcher.BeginInvoke((Action)delegate
				{
					if (!_weatherFetching) _ = RefreshWeatherAsync();
				}, DispatcherPriority.Background);
			}
		}
	}

	private async Task WarmWeatherAssetsAsync(WeatherService.Result result, long revision)
	{
		try
		{
			await Task.Run(() => WeatherTileArt.Preload(result));
			if (revision != _weatherRequestRevision) return;
			_weatherAssetsReady = true;
			RefreshWeatherFaces();
		}
		catch (Exception ex)
		{
			Logger.Log("Weather background warmup failed: " + ex.Message);
			if (revision != _weatherRequestRevision) return;
			_weatherAssetsReady = true;
			RefreshWeatherFaces();
		}
	}

	private void SetWeatherLoadingFaces()
	{
		_weatherAssetsReady = false;
		foreach (TileVm tile in _tiles())
		{
			if (tile.Live != LiveKind.Weather) continue;
			tile.Entry.TileBrush = LiveTiles.BrandBrush(LiveKind.Weather);
			tile.MetroVisual = null;
			tile.MetroMotionEnabled = false;
			tile.LivePrimary = "\u2026";
			tile.LiveSecondary = WeatherCity;
			tile.LiveSymbol = "";
		}
	}

	// Build a split cached background/foreground visual for each Weather tile. The presenter crossfades backgrounds and
	// owns motion independently, while the generic live-tile flip layer stays disabled for Weather.
	private void ApplyWeatherFaces(WeatherService.Result? r)
	{
		foreach (TileVm t in _tiles())
		{
			if (t.Live != LiveKind.Weather)
			{
				continue;
			}
			if (r.HasValue)
			{
				EnsureWeatherFace(t);
			}
			else if (!_lastWeather.HasValue)
			{
				t.Entry.TileBrush = LiveTiles.BrandBrush(LiveKind.Weather);
				t.MetroVisual = null;
				t.MetroMotionEnabled = false;
				t.LivePrimary = "--";
				t.LiveSecondary = WeatherCity;
				t.LiveSymbol = "";
			}
		}
	}

	// Rebuild one Weather face only when data, units, local time band, or tile size changes. Resizing gets a native layout
	// for that size instead of stretching a stale bitmap; the key check makes the one-second service tick allocation-free.
	private void EnsureWeatherFace(TileVm t)
	{
		if (!_weatherAssetsReady || !_lastWeather.HasValue || t.Entry == null)
		{
			return;
		}
		WeatherService.Result r = _lastWeather.Value;
		string cityName = string.IsNullOrEmpty(r.City) ? WeatherCity : r.City;
		string key = t.Size + "|" + WeatherUnits + "|" + WeatherTileArt.CurrentBand(r) + "|" + Math.Round(r.TempC) + "|" + cityName + "|" + r.HiC + "/" + r.LoC + "|" + r.TodayDesc + "|" + r.TomHiC + "/" + r.TomLoC + "|" + r.TomDesc + "|" + r.Description + "|" + r.ConditionKey + "|" + r.IsStale;
		if (_weatherFaceKey.TryGetValue(t, out string prev) && prev == key)
		{
			return;
		}
		try
		{
			t.Entry.TileBrush = LiveTiles.BrandBrush(LiveKind.Weather);
			t.MetroVisual = WeatherTileArt.Build(r, cityName, t.Size, TileMetrics.Scale, WeatherUnits == "F");
			t.MetroMotionEnabled = WeatherMotionAllowed();
			t.LivePrimary = "";
			t.LiveSecondary = "";
			t.LiveSymbol = "";
			_weatherFaceKey[t] = key;
		}
		catch (Exception ex)
		{
			Logger.Log("Weather tile compose: " + ex.Message);
		}
	}

	public void RefreshWeatherFaces()
	{
		RefreshMetroFaces();
		_weatherFaceKey.Clear();
		foreach (TileVm tile in _tiles())
		{
			if (tile.Live == LiveKind.Weather && _lastWeather.HasValue) EnsureWeatherFace(tile);
		}
	}

	private bool WeatherMotionAllowed()
	{
		if (!_timer.IsEnabled || Motion.Mode is MotionMode.Off or MotionMode.Reduced) return false;
		try { return !PowerStatus.Read().saver; } catch { return true; }
	}

	private void SetWeatherMotion(bool enabled)
	{
		bool allow = enabled && Motion.Mode is not (MotionMode.Off or MotionMode.Reduced);
		if (allow)
		{
			try { allow = !PowerStatus.Read().saver; } catch { }
		}
		foreach (TileVm tile in _tiles())
		{
			if (tile.IsMetroLiveTile) tile.MetroMotionEnabled = allow && !tile.LiveOff;
		}
	}

	private static string WeatherSymbol(string desc)
	{
		string d = (desc ?? "").ToLowerInvariant();
		string s = (d.Contains("thunder") ? "☈" : ((d.Contains("snow") || d.Contains("sleet") || d.Contains("ice") || d.Contains("blizzard")) ? "❄" : ((d.Contains("rain") || d.Contains("drizzle") || d.Contains("shower")) ? "☔" : ((d.Contains("fog") || d.Contains("mist") || d.Contains("haze") || d.Contains("overcast") || d.Contains("cloud")) ? "☁" : ((d.Contains("clear") || d.Contains("sun") || d.Contains("fair") || d.Contains("partly") || d.Contains("mainly")) ? "☀" : "")))));
		return (s.Length > 0) ? (s + "\ufe0e") : "";
	}

	public void Dispose()
	{
		_disposed = true;
		_newsCancellation?.Cancel();
		_timer.Stop();
		SetWeatherMotion(enabled: false);
	}
}
