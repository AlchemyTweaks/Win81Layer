#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Windows.Threading;

namespace Win81Layer;

// The launcher side of the native pin sync, for the running shell. Every change runs on the UI thread as one step:
// read the store, change it, save it, refresh the live surfaces, so no bar or menu copy can overwrite it. It waits
// (Deferred) rather than act while a store loaded degraded, a pin is being dragged, or the Start board is open or not
// loaded yet.
internal sealed class LauncherPinSyncTarget : IPinSyncTarget, IDisposable
{
	private readonly Dispatcher _ui;

	private readonly Func<StartScreen?> _startScreen;

	private readonly Func<Win7StartMenu?> _win7Start;

	private volatile bool _stopped;

	public LauncherPinSyncTarget(Dispatcher ui, Func<StartScreen?> startScreen, Func<Win7StartMenu?> win7Start)
	{
		_ui = ui;
		_startScreen = startScreen;
		_win7Start = win7Start;
	}

	public DateTime? FirstRunCatchUpSinceUtc => TaskbarPins.StoreStampAtFirstLoadUtc;

	public PinSyncOutcome ApplyTaskbar(TaskbarPinDiff diff, List<string> log, out string? deferReason)
	{
		return OnUi(lines => ApplyTaskbarOnUi(diff, lines), log, out deferReason);
	}

	public PinSyncOutcome ApplyStart(StartPinDiff diff, List<string> log, out string? deferReason)
	{
		return OnUi(lines => ApplyStartOnUi(diff, lines), log, out deferReason);
	}

	private PinSyncOutcome OnUi(Func<List<string>, (PinSyncOutcome Outcome, string? Why)> apply, List<string> log, out string? deferReason)
	{
		deferReason = null;
		if (_stopped)
		{
			deferReason = "shutting down";
			return PinSyncOutcome.Deferred;
		}
		List<string> lines = new List<string>();
		(PinSyncOutcome Outcome, string? Why) result = (PinSyncOutcome.Deferred, "UI busy");
		try
		{
			// Not started within 30 s (UI hung or shutting down): aborted, which leaves result Deferred.
			_ui.Invoke(delegate
			{
				if (_stopped)
				{
					result = (PinSyncOutcome.Deferred, "shutting down");
					return;
				}
				try
				{
					result = apply(lines);
				}
				catch (Exception ex)
				{
					lines.Add("Pin sync: apply failed: " + ex.Message);
					result = (PinSyncOutcome.Failed, null);
				}
			}, DispatcherPriority.Background, CancellationToken.None, TimeSpan.FromSeconds(30));
		}
		catch (Exception ex)
		{
			lines.Add("Pin sync: UI dispatch failed: " + ex.Message);
			result = (PinSyncOutcome.Deferred, "UI dispatch failed");
		}
		log.AddRange(lines);
		deferReason = result.Outcome == PinSyncOutcome.Deferred ? result.Why : null;
		return result.Outcome;
	}

	// No file checks here: the worker built the pins (PinSyncEngine.PrepareTaskbar) before this runs.
	private static (PinSyncOutcome, string?) ApplyTaskbarOnUi(TaskbarPinDiff diff, List<string> log)
	{
		if (TaskbarWindow.AnyPinDragActive)
		{
			return (PinSyncOutcome.Deferred, "a taskbar pin is being dragged");
		}
		List<PinnedApp> pins = TaskbarPins.Load();
		if (TaskbarPins.LoadWasDegraded)
		{
			return (PinSyncOutcome.Deferred, "taskbar pins not readable yet");
		}
		List<string> planned = new List<string>();
		int changes = PinSyncEngine.PlanTaskbar(pins, diff, planned);
		if (changes > 0)
		{
			if (!TaskbarPins.Save(pins, "native pin sync"))
			{
				return (PinSyncOutcome.Failed, null);
			}
			TaskbarWindow.ReloadPins();
		}
		log.AddRange(planned);
		if (diff.Live && diff.NotOnTaskbar.Count > 0)
		{
			LogNotOnTaskbar(diff.NotOnTaskbar);
		}
		return (PinSyncOutcome.Applied, null);
	}

	// Explorer's menus follow the native pin list, so they can offer "Unpin from taskbar" for an app that is not on
	// this taskbar. Clicking it changes nothing here; the log says so, and how to add the app instead (the launcher shows
	// no toasts of its own).
	private static void LogNotOnTaskbar(List<string> names)
	{
		try
		{
			List<string> distinct = names.Where(n => !string.IsNullOrWhiteSpace(n)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
			if (distinct.Count == 0)
			{
				return;
			}
			string message = distinct.Count == 1
				? distinct[0] + " wasn't on the taskbar, so there was nothing to unpin. To add it, choose Pin to taskbar."
				: string.Join(", ", distinct.Take(3)) + (distinct.Count > 3 ? " and others" : "") + " weren't on the taskbar, so there was nothing to unpin. To add them, choose Pin to taskbar.";
			Logger.Log("Native pin sync: " + message);
		}
		catch
		{
		}
	}

	private (PinSyncOutcome, string?) ApplyStartOnUi(StartPinDiff diff, List<string> log)
	{
		StartScreen? start = _startScreen();
		if (start == null || !start.PinSyncReady)
		{
			return (PinSyncOutcome.Deferred, "Start not loaded yet");
		}
		if (SettingsStore.LoadWasDegraded || !SettingsStore.TryLoadDurable(out AppSettings settings))
		{
			return (PinSyncOutcome.Deferred, "settings not readable yet");
		}
		bool win7 = settings.Win7StartMenuEnabled;
		// In either style: a removal also takes tiles off the Metro board, which must not change under an open board
		// (a tile drag or a group edit in progress there saves its own copy later).
		if (start.IsVisible)
		{
			return (PinSyncOutcome.Deferred, "the Start screen is open");
		}
		LiveStartPinModel model = new LiveStartPinModel(start, win7, settings.Win7StartMenuPins);
		List<string> planned = new List<string>();
		int changes = PinSyncEngine.PlanStart(model, diff, planned);
		if (changes > 0 && model.Win7Changed)
		{
			if (!model.CommitWin7())
			{
				return (PinSyncOutcome.Failed, null);
			}
			try
			{
				_win7Start()?.RefreshAfterExternalPinChange();
			}
			catch (Exception ex)
			{
				log.Add("Pin sync: Windows 7 menu refresh failed (shown on next open): " + ex.Message);
			}
		}
		log.AddRange(planned);
		return (PinSyncOutcome.Applied, null);
	}

	public void Dispose()
	{
		_stopped = true;
	}
}

// Live Start model (UI thread). Metro changes go straight through StartScreen (which saves the profile); Windows 7
// changes are collected and committed with one SettingsStore.TryUpdate, which refuses to save over a fallback read.
internal sealed class LiveStartPinModel : IStartPinModel
{
	private readonly StartScreen _start;

	private readonly List<string> _win7;

	private readonly List<(bool Add, string LaunchPath)> _win7Ops = new List<(bool, string)>();

	public LiveStartPinModel(StartScreen start, bool win7Style, List<string>? win7Pins)
	{
		_start = start;
		Win7Style = win7Style;
		_win7 = new List<string>(win7Pins ?? new List<string>());
	}

	public bool Win7Style { get; }

	public bool Win7Changed => _win7Ops.Count > 0;

	public (string LaunchPath, string Name)? Resolve(string appsFolderId)
	{
		AppEntry? e = _start.FindAppForPinSync(appsFolderId);
		return e == null ? null : (e.LaunchPath, e.Name);
	}

	public bool Win7Has(string launchPath) => _win7.Any(p => NativePinSync.SameLaunchTarget(p, launchPath));

	public void Win7Add(string launchPath)
	{
		_win7.Add(launchPath);
		_win7Ops.Add((true, launchPath));
	}

	public int Win7Remove(string launchPath)
	{
		int n = _win7.RemoveAll(p => NativePinSync.SameLaunchTarget(p, launchPath));
		if (n > 0)
		{
			_win7Ops.Add((false, launchPath));
		}
		return n;
	}

	public bool MetroHas(string launchPath) => _start.HasStartTileForPinSync(launchPath);

	public bool MetroAdd(string launchPath)
	{
		AppEntry? e = _start.AllApps.FirstOrDefault(a => string.Equals(a.LaunchPath, launchPath, StringComparison.OrdinalIgnoreCase));
		return e != null && _start.PinToStartForPinSync(e);
	}

	public int MetroRemove(string launchPath) => _start.UnpinFromStartForPinSync(launchPath);

	public bool CommitWin7()
	{
		if (_win7Ops.Count == 0)
		{
			return true;
		}
		return SettingsStore.TryUpdate(delegate (AppSettings x)
		{
			x.Win7StartMenuPins ??= new List<string>();
			foreach ((bool add, string lp) in _win7Ops)
			{
				x.Win7StartMenuPins.RemoveAll(p => NativePinSync.SameLaunchTarget(p, lp));
				if (add)
				{
					x.Win7StartMenuPins.Add(lp);
				}
			}
		});
	}
}
