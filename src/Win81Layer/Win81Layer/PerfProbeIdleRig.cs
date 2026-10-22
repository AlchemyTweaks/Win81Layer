#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;

namespace Win81Layer;

// The idle shell schedule for --perfprobe idle, with read-only consumers in place of the bars (which cannot exist in the
// probe). Each consumer does the same kind of work the shell does on that tick: foreground and fullscreen tests on the
// UI thread, a window enumeration with identity resolution on the thread pool, tray telemetry, the heartbeat write and
// the supervisor's window check. Nothing here sends anything to another process's window or opens a host handle.
internal sealed class PerfProbeIdleRig
{
	private readonly bool _legacy;

	private readonly string _outDir;

	private readonly bool _assumeIdle;

	private ShellClock? _clock;

	private IdleMaintenance? _maintenance;

	private readonly List<DispatcherTimer> _minuteTimers = new List<DispatcherTimer>();

	private readonly List<DispatcherTimer> _timers = new List<DispatcherTimer>();

	private readonly List<string> _schedule = new List<string>();

	private HotCorners? _corners;

	private readonly AudioController _audio = new AudioController();

	private readonly EventWaitHandle _signal = new EventWaitHandle(false, EventResetMode.AutoReset);

	private readonly Dictionary<uint, string> _exeByPid = new Dictionary<uint, string>();

	private readonly object _exeGate = new object();

	private long _ticks;

	private long _gathers;

	private long _fileBeats;

	private long _beats;

	internal long Ticks => Interlocked.Read(ref _ticks);

	internal long Gathers => Interlocked.Read(ref _gathers);

	internal long FileBeats => _legacy ? Interlocked.Read(ref _fileBeats) : Heartbeat.FileWrites;

	internal long Beats => _legacy ? Interlocked.Read(ref _beats) : Heartbeat.Beats;

	internal long HotPolls => _corners?.PollCount ?? 0;

	private PerfProbeIdleRig(bool legacy, string outDir, bool assumeIdle)
	{
		_legacy = legacy;
		_outDir = outDir;
		_assumeIdle = assumeIdle;
	}

	internal static PerfProbeIdleRig Create(bool legacy, string outDir, bool assumeIdle)
	{
		return new PerfProbeIdleRig(legacy, outDir, assumeIdle);
	}

	internal string[] Describe()
	{
		return _schedule.ToArray();
	}

	internal void Start()
	{
		if (_legacy)
		{
			StartLegacy();
		}
		else
		{
			StartNew();
		}
	}

	internal void Stop()
	{
		foreach (DispatcherTimer t in _timers)
		{
			t.Stop();
		}
		foreach (DispatcherTimer t in _minuteTimers)
		{
			t.Stop();
		}
		_clock?.Dispose();
		_maintenance?.Dispose();
		_corners?.Dispose();
		ShellClock.InputIdleOverride = null;
	}

	// ----- new: hot corners armed by the hook (no hook here: the pointer is not in a zone, so nothing runs), one shell
	// clock at the idle cadence, the shared-memory heartbeat and idle maintenance.

	private void StartNew()
	{
		_corners = new HotCorners
		{
			TopLeft = NoAction,
			LeftEdge = NoAction,
			LeftEdgeDwellTicks = 5,
			Enabled = true
		};
		_corners.AttachHook(null);
		_schedule.Add("hot corners: armed by the mouse hook (dwell timer only while the pointer rests in a zone)");
		if (_assumeIdle)
		{
			ShellClock.InputIdleOverride = () => 600000L;
		}
		_clock = new ShellClock(Dispatcher.CurrentDispatcher, watchSession: false);
		_maintenance = new IdleMaintenance(() => false);
		BarState[] bars = { new BarState(true), new BarState(false) };
		_clock.Heartbeat = delegate
		{
			Interlocked.Increment(ref _ticks);
			Heartbeat.Beat();
		};
		_clock.Supervisor = SupervisorCheck;
		_clock.RefreshBars = delegate
		{
			foreach (BarState b in bars)
			{
				RefreshBar(b);
			}
		};
		_clock.FastBars = delegate
		{
			foreach (BarState b in bars)
			{
				FastBar(b);
			}
		};
		// As App.OnShellMaintenance: idle maintenance, the stray frame-hook tripwire every 45 s, the input-hook liveness
		// observation (cursor read; the window checks only when something looked silent) and the hot corners' clock tick.
		long lastTripwire = Environment.TickCount64;
		InputHookLiveness liveness = new InputHookLiveness();
		_clock.Maintenance = delegate(long now, long idle)
		{
			_maintenance.Tick(now, idle);
			if (now - lastTripwire >= 45000L)
			{
				lastTripwire = now;
				TaskbarWindow.StopStrayFrameHooks();
			}
			InputHookLiveness.Sample s = default;
			s.Now = now;
			s.HaveCursor = WindowUtil.TryGetCursorPos(out s.X, out s.Y);
			s.InputIdleMs = idle;
			if (liveness.Observe(in s))
			{
				bool excused = WindowUtil.WindowAtInputHiddenFromHooks(s.X, s.Y) || WindowUtil.ForegroundIsElevatedOrUnknown() || WindowUtil.ForegroundIsInputCaptureClient();
				liveness.Judge(now, excused, active: false);
			}
			_corners?.ClockTick(liveness.SilentMove);
		};
		// Idle ticks between two bar refreshes: volume and mute (the CPU/RAM meter is off by default).
		_clock.Telemetry = delegate
		{
			try
			{
				_ = _audio.GetVolume();
				_ = _audio.GetMute();
			}
			catch
			{
			}
		};
		_clock.Start();
		_schedule.Add("maintenance tick: idle trims, frame-hook tripwire every 45 s, hook liveness, hot-corner cursor check; tray volume on idle ticks between refreshes");
		_schedule.Add($"shell clock: {ShellClock.IntervalMs(ShellClock.Mode.Active)}/{ShellClock.IntervalMs(ShellClock.Mode.Idle)}/{ShellClock.IntervalMs(ShellClock.Mode.Asleep)} ms (active/idle/asleep), bars refresh {ShellClock.RefreshEveryMs(ShellClock.Mode.Active)}/{ShellClock.RefreshEveryMs(ShellClock.Mode.Idle)} ms, heartbeat every tick (file every 15 s)");
		foreach (BarState b in bars)
		{
			DispatcherTimer minute = new DispatcherTimer(DispatcherPriority.Background);
			minute.Tick += delegate
			{
				minute.Stop();
				Interlocked.Increment(ref _ticks);
				_ = DateTime.Now.ToString("HH:mm");
				ArmMinute(minute);
			};
			ArmMinute(minute);
			_minuteTimers.Add(minute);
		}
		_schedule.Add("bar clocks: one-shot timers on the minute boundary");
	}

	private static void ArmMinute(DispatcherTimer t)
	{
		DateTime now = DateTime.Now;
		t.Interval = TimeSpan.FromMilliseconds(Math.Max(50.0, 60000.0 - (now.Second * 1000 + now.Millisecond) + 40.0));
		t.Start();
	}

	private sealed class BarState
	{
		public readonly bool Primary;
		public bool Busy;
		public long LastFullscreen;
		public int TrayTick;

		public BarState(bool primary)
		{
			Primary = primary;
		}
	}

	private void RefreshBar(BarState b)
	{
		FullscreenForeground();
		_ = DateTime.Now.ToString("HH:mm");
		if (b.Primary)
		{
			TrayTelemetryNew(ref b.TrayTick);
		}
		if (b.Busy)
		{
			return;
		}
		b.Busy = true;
		Dispatcher ui = Dispatcher.CurrentDispatcher;
		Task.Run(delegate
		{
			int n = Gather();
			ui.BeginInvoke((Action)delegate
			{
				b.Busy = false;
				_ = n;
			});
		});
	}

	private static void FastBar(BarState b)
	{
		_ = GetForegroundWindow();
		long now = Environment.TickCount64;
		if (now - b.LastFullscreen >= 400)
		{
			b.LastFullscreen = now;
			FullscreenForeground();
		}
	}

	private long _lastTrayBackstop = long.MinValue / 2;

	// As the new UpdateTray: volume every refresh, the tray-host backstop scan every 30 s, power/network every 5th.
	private void TrayTelemetryNew(ref int trayTick)
	{
		try
		{
			_ = _audio.GetVolume();
			_ = _audio.GetMute();
		}
		catch
		{
		}
		Dispatcher ui = Dispatcher.CurrentDispatcher;
		long nowMs = Environment.TickCount64;
		if (nowMs - _lastTrayBackstop >= 30000L)
		{
			_lastTrayBackstop = nowMs;
			Task.Run(delegate
			{
				ui.BeginInvoke((Action)delegate { });
			});
		}
		bool slow = trayTick % 5 == 0;
		trayTick++;
		if (slow)
		{
			Task.Run(delegate
			{
				_ = PowerStatus.Read();
				_ = NetState81.Read();
				ui.BeginInvoke((Action)delegate { });
			});
		}
	}

	private DispatcherTimer Every(int ms, Action tick, string what)
	{
		DispatcherTimer t = new DispatcherTimer(DispatcherPriority.Background)
		{
			Interval = TimeSpan.FromMilliseconds(ms)
		};
		t.Tick += delegate
		{
			Interlocked.Increment(ref _ticks);
			try
			{
				tick();
			}
			catch
			{
			}
		};
		_timers.Add(t);
		_schedule.Add($"{what}: {ms} ms");
		t.Start();
		return t;
	}

	// ----- legacy: the schedule of the previous build

	private void StartLegacy()
	{
		_corners = new HotCorners
		{
			TopLeft = NoAction,
			LeftEdge = NoAction,
			LeftEdgeDwellTicks = 5,
			Enabled = true
		};
		_schedule.Add("hot corners: 100 ms poll");
		for (int bar = 0; bar < 2; bar++)
		{
			bool primary = bar == 0;
			int refreshCount = 0;
			bool busy = false;
			long lastFullscreen = 0;
			int trayTick = 0;
			Every(2000, delegate
			{
				// The fullscreen test costs the same either way; the probe keeps gathering even when the user's own
				// foreground happens to be fullscreen, so every run does the same work.
				FullscreenForeground();
				refreshCount++;
				_ = DateTime.Now.ToString("HH:mm");
				if (primary)
				{
					TrayTelemetry(ref trayTick);
				}
				if (busy)
				{
					return;
				}
				busy = true;
				Dispatcher ui = Dispatcher.CurrentDispatcher;
				Task.Run(delegate
				{
					int n = Gather();
					ui.BeginInvoke((Action)delegate
					{
						busy = false;
						_ = n;
					});
				});
			}, primary ? "bar 1 refresh" : "bar 2 refresh");
			Every(1000, delegate
			{
				_ = GetForegroundWindow();
				long now = Environment.TickCount64;
				if (now - lastFullscreen >= 400)
				{
					lastFullscreen = now;
					FullscreenForeground();
				}
			}, primary ? "bar 1 fast tick" : "bar 2 fast tick");
		}
		int beatTick = 0;
		Every(1000, delegate
		{
			if ((++beatTick & 3) == 0)
			{
				WriteFileBeat();
			}
			Interlocked.Increment(ref _beats);
			_signal.WaitOne(0);
			SupervisorCheck();
		}, "shell timer (heartbeat file every 4th tick, signal poll, supervisor)");
	}

	private static void NoAction()
	{
	}

	// ----- consumers (read-only)

	private void TrayTelemetry(ref int trayTick)
	{
		try
		{
			_ = _audio.GetVolume();
			_ = _audio.GetMute();
		}
		catch
		{
		}
		Dispatcher ui = Dispatcher.CurrentDispatcher;
		if (trayTick % 2 == 0)
		{
			// The tray scan: a snapshot on the pool, applied back on the UI thread.
			Task.Run(delegate
			{
				ui.BeginInvoke((Action)delegate { });
			});
		}
		bool slow = trayTick % 5 == 0;
		trayTick++;
		if (slow)
		{
			Task.Run(delegate
			{
				_ = PowerStatus.Read();
				_ = NetState81.Read();
				ui.BeginInvoke((Action)delegate { });
			});
		}
	}

	internal int Gather()
	{
		Interlocked.Increment(ref _gathers);
		int n = 0;
		foreach ((nint hwnd, string _) in WindowList.Enumerate(IntPtr.Zero))
		{
			GetWindowThreadProcessId(hwnd, out uint pid);
			string exe = ExeFor(pid);
			_ = MonitorFromWindow(hwnd, 2u);
			if (exe.EndsWith("\\explorer.exe", StringComparison.OrdinalIgnoreCase) || exe.EndsWith("\\ApplicationFrameHost.exe", StringComparison.OrdinalIgnoreCase))
			{
				_ = AppInventory.GetWindowAppId(hwnd);
			}
			n++;
		}
		return n;
	}

	private string ExeFor(uint pid)
	{
		lock (_exeGate)
		{
			if (_exeByPid.TryGetValue(pid, out string? cached))
			{
				return cached;
			}
		}
		string path = "";
		nint h = OpenProcess(0x1000u, false, pid);   // PROCESS_QUERY_LIMITED_INFORMATION: the image name only
		if (h != IntPtr.Zero)
		{
			StringBuilder sb = new StringBuilder(1024);
			int len = sb.Capacity;
			if (QueryFullProcessImageName(h, 0, sb, ref len))
			{
				path = sb.ToString();
			}
			CloseHandle(h);
		}
		lock (_exeGate)
		{
			_exeByPid[pid] = path;
		}
		return path;
	}

	internal static bool FullscreenForeground()
	{
		nint fg = GetForegroundWindow();
		if (fg == IntPtr.Zero || !GetWindowRect(fg, out RECT r))
		{
			return false;
		}
		nint mon = MonitorFromWindow(fg, 2u);
		MONITORINFO mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
		if (!GetMonitorInfo(mon, ref mi))
		{
			return false;
		}
		return r.L <= mi.rcMonitor.L && r.T <= mi.rcMonitor.T && r.R >= mi.rcMonitor.R && r.B >= mi.rcMonitor.B;
	}

	private void WriteFileBeat()
	{
		try
		{
			using FileStream fs = new FileStream(Path.Combine(_outDir, "probe-heartbeat.dat"), FileMode.Create, FileAccess.Write, FileShare.ReadWrite);
			byte[] bytes = BitConverter.GetBytes(DateTime.UtcNow.Ticks);
			fs.Write(bytes, 0, bytes.Length);
			Interlocked.Increment(ref _fileBeats);
		}
		catch
		{
		}
	}

	private static void SupervisorCheck()
	{
		nint tray = FindWindow("Shell_TrayWnd", null);
		if (tray != IntPtr.Zero)
		{
			_ = IsWindowVisible(tray);
		}
	}

	private struct RECT
	{
		public int L;
		public int T;
		public int R;
		public int B;
	}

	private struct MONITORINFO
	{
		public int cbSize;
		public RECT rcMonitor;
		public RECT rcWork;
		public uint dwFlags;
	}

	[DllImport("user32.dll")]
	private static extern nint GetForegroundWindow();

	[DllImport("user32.dll")]
	private static extern bool GetWindowRect(nint hwnd, out RECT rect);

	[DllImport("user32.dll")]
	private static extern nint MonitorFromWindow(nint hwnd, uint flags);

	[DllImport("user32.dll")]
	private static extern bool GetMonitorInfo(nint monitor, ref MONITORINFO info);

	[DllImport("user32.dll")]
	private static extern uint GetWindowThreadProcessId(nint hwnd, out uint pid);

	[DllImport("user32.dll", CharSet = CharSet.Unicode)]
	private static extern nint FindWindow(string? cls, string? title);

	[DllImport("user32.dll")]
	private static extern bool IsWindowVisible(nint hwnd);

	[DllImport("kernel32.dll")]
	private static extern nint OpenProcess(uint access, bool inherit, uint pid);

	[DllImport("kernel32.dll")]
	private static extern bool CloseHandle(nint h);

	[DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
	private static extern bool QueryFullProcessImageName(nint h, int flags, StringBuilder name, ref int size);
}
