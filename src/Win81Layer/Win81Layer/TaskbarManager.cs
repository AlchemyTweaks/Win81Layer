using System;
using System.Collections.Generic;
using System.Threading;
using System.Windows;
using System.Windows.Forms;
using System.Windows.Threading;
using Microsoft.Win32;

namespace Win81Layer;

public sealed class TaskbarManager
{
	private readonly List<TaskbarWindow> _bars = new List<TaskbarWindow>();

	private TaskbarWindow[] _wheelRouteBars = Array.Empty<TaskbarWindow>();

	private bool _active;

	private bool _displayHooked;

	private DispatcherTimer? _displayDebounce;

	private DispatcherTimer? _pinSettle;

	private int _pinSettleChecks;

	private long _pinRevision;

	private bool _rebuilding;

	public bool IsActive => _active;
	internal int BarCountForDiagnostics => _bars.Count;

	public event Action? StartRequested;

	public event Action? StartPeekRequested;

	public event Action<bool>? StartPeekEnded;

	public void Activate()
	{
		if (_active)
		{
			return;
		}
		Screen[] allScreens = Screen.AllScreens;
		TaskbarWorkArea.BeginSession(allScreens);
		AppBar.HideNativeTaskbar();
		CreateBars(allScreens);
		_active = true;
		StartPinSettle();
		if (!_displayHooked)
		{
			SystemEvents.DisplaySettingsChanged += OnDisplayChanged;
			_displayHooked = true;
		}
		Logger.Log($"Taskbar activated on {_bars.Count} monitor(s)");
	}

	private void CreateBars(IEnumerable<Screen> screens)
	{
		foreach (Screen screen in screens)
		{
			TaskbarWindow bar = new TaskbarWindow();
			bar.StartRequested += delegate { StartRequested?.Invoke(); };
			bar.StartPeekRequested += delegate { StartPeekRequested?.Invoke(); };
			bar.StartPeekEnded += delegate(bool commit) { StartPeekEnded?.Invoke(commit); };
			bar.StartOn(screen);
			_bars.Add(bar);
		}
		Volatile.Write(ref _wheelRouteBars, _bars.ToArray());
	}

	private void StartPinSettle()
	{
		_pinRevision = TaskbarPins.Revision;
		_pinSettleChecks = 0;
		_pinSettle ??= new DispatcherTimer(DispatcherPriority.Background)
		{
			Interval = TimeSpan.FromSeconds(3.0)
		};
		_pinSettle.Stop();
		_pinSettle.Tick -= OnPinSettleTick;
		_pinSettle.Tick += OnPinSettleTick;
		_pinSettle.Start();
	}

	private void OnPinSettleTick(object? sender, EventArgs e)
	{
		if (!_active || ++_pinSettleChecks >= 4)
		{
			_pinSettle?.Stop();
		}
		if (!_active)
		{
			return;
		}
		TaskbarPins.Load();
		long current = TaskbarPins.Revision;
		if (current != _pinRevision)
		{
			_pinRevision = current;
			Logger.Log($"Pin state settled at revision {current}; refreshing all monitor taskbars once");
			TaskbarWindow.ReloadPins();
		}
	}

	private void OnDisplayChanged(object? sender, EventArgs e)
	{
		//IL_006c: Unknown result type (might be due to invalid IL or missing references)
		//IL_0071: Unknown result type (might be due to invalid IL or missing references)
		//IL_0088: Expected O, but got Unknown
		System.Windows.Application current = System.Windows.Application.Current;
		Dispatcher disp = ((current != null) ? ((DispatcherObject)current).Dispatcher : null);
		if (disp == null)
		{
			return;
		}
		if (!disp.CheckAccess())
		{
			disp.BeginInvoke((Delegate)(Action)delegate
			{
				OnDisplayChanged(sender, e);
			}, Array.Empty<object>());
			return;
		}
		if (_displayDebounce == null)
		{
			_displayDebounce = new DispatcherTimer
			{
				Interval = TimeSpan.FromMilliseconds(900L)
			};
		}
		_displayDebounce.Stop();
		_displayDebounce.Tick -= OnDisplayDebounceTick;
		_displayDebounce.Tick += OnDisplayDebounceTick;
		_displayDebounce.Start();
	}

	private void OnDisplayDebounceTick(object? sender, EventArgs e)
	{
		DispatcherTimer? displayDebounce = _displayDebounce;
		if (displayDebounce != null)
		{
			displayDebounce.Stop();
		}
		if (_active && !_rebuilding)
		{
			RebuildForDisplays();
		}
	}

	private void RebuildForDisplays()
	{
		_rebuilding = true;
		try
		{
			Screen[] screens = Screen.AllScreens;
			Logger.Log($"Display topology/DPI changed: rebuilding {_bars.Count} bar(s) for {screens.Length} monitor(s)");
			TaskbarWorkArea.BeginSession(screens);
			foreach (TaskbarWindow bar in _bars)
			{
				bar.StopBar();
			}
			_bars.Clear();
			AppBar.HideNativeTaskbar();
			CreateBars(screens);
			TaskbarWorkArea.NudgeWindows();
			StartPinSettle();
			SchedulePostRebuildRepaint();
		}
		catch (Exception ex)
		{
			Logger.Log("Taskbar display rebuild failed: " + ex.Message);
		}
		finally
		{
			_rebuilding = false;
		}
	}

	// After a topology/DPI rebuild, force each freshly-created bar to re-present on its target monitor once
	// the new windows have laid out. Without this the SECONDARY monitor's bar can stay blank (visible +
	// topmost but painting nothing) until the shell is restarted. Scheduled at Background priority so it runs
	// after the initial render; harmless on the primary bar (a no-op re-present).
	private void SchedulePostRebuildRepaint()
	{
		System.Windows.Application app = System.Windows.Application.Current;
		Dispatcher disp = ((app != null) ? ((DispatcherObject)app).Dispatcher : null);
		if (disp == null)
		{
			return;
		}
		disp.BeginInvoke((Delegate)(Action)delegate
		{
			foreach (TaskbarWindow bar in _bars)
			{
				try
				{
					bar.RepresentOnMonitor();
				}
				catch
				{
				}
			}
		}, (DispatcherPriority)4, Array.Empty<object>());
	}

	internal void RebuildForDiagnostics()
	{
		if (!_active || _rebuilding)
		{
			throw new InvalidOperationException("Taskbar manager is not ready for a topology rebuild.");
		}
		RebuildForDisplays();
	}

	public void Deactivate()
	{
		_active = false;
		_pinSettle?.Stop();
		foreach (TaskbarWindow b in _bars)
		{
			b.StopBar();
		}
		_bars.Clear();
		Volatile.Write(ref _wheelRouteBars, Array.Empty<TaskbarWindow>());
		AppBar.ShowNativeTaskbar();
	}

	public void CloseFlyoutsOutside(int x, int y)
	{
		foreach (TaskbarWindow b in _bars)
		{
			b.CloseFlyoutsOutside(x, y);
		}
	}

	public bool TryRouteGlobalFlyoutMouseWheel(int x, int y, int delta)
	{
		foreach (TaskbarWindow bar in Volatile.Read(ref _wheelRouteBars))
		{
			if (bar.TryRouteGlobalFlyoutMouseWheel(x, y, delta))
			{
				return true;
			}
		}
		return false;
	}

	public void ShowDesktopMenu(int x, int y)
	{
		TaskbarWindow bar = null;
		foreach (TaskbarWindow b in _bars)
		{
			if (b.ContainsScreenPoint(x, y))
			{
				bar = b;
				break;
			}
		}
		if (bar == null && _bars.Count > 0)
		{
			bar = _bars[0];
		}
		bar?.ShowDesktopMenu(x, y);
	}
}
