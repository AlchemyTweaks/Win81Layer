#nullable enable

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace Win81Layer;

// Process-wide flag for the offscreen performance probe (--perfprobe). While it is set the shell-owning entry points
// refuse to run (taskbar, tray host, hooks, supervisor, native hosts) and the persistence/registry side effects that
// the probed surfaces could reach become no-ops. The real shell never sets it.
internal static class PerfProbe
{
	internal static volatile bool Active;

	// --policy legacy: reproduce the previous scheduling inside the same binary (A/B without a second build).
	internal static bool LegacyPolicy;

	private static readonly List<string> RefusalLog = new List<string>();

	internal static string[] Refusals
	{
		get
		{
			lock (RefusalLog)
			{
				return RefusalLog.ToArray();
			}
		}
	}

	// Throws (and records the attempt) when the probe reaches an entry point that would act on the live session.
	internal static void Refuse(string what)
	{
		if (!Active)
		{
			return;
		}
		lock (RefusalLog)
		{
			RefusalLog.Add(what);
		}
		throw new InvalidOperationException("perf probe: " + what + " is not allowed in the offscreen probe");
	}
}

// --perfprobe <outDir> <scenario> [--policy legacy|new] [--assume-idle] [--software-render]
// Scenarios: mem (memory and decoded-pixel census), open (offscreen open latency with and without a preceding trim),
// idle (timer wake-ups and context switches of the idle shell schedule), stall (UI dispatch delay while the periodic
// maintenance runs). Every window stays offscreen (Left/Top <= -3000), is never activated and carries WS_EX_NOACTIVATE.
// The user state folder is hashed before and after; any difference fails the run. Self-exits at 100 s (hard at 115 s).
internal static partial class PerfProbeDiagnostics
{
	private const int SoftDeadlineMs = 100000;

	private const int HardDeadlineMs = 115000;

	private static string _outDir = "";

	private static string _scenario = "mem";

	private static bool _assumeIdle;

	// metrofirst --cold: the first show without the Settings-pane signal.
	private static bool _metroCold;

	private static readonly Stopwatch Clock = Stopwatch.StartNew();

	private static readonly Dictionary<string, object?> Report = new Dictionary<string, object?>();

	private static readonly List<string> Notes = new List<string>();

	private static Dictionary<string, string> _stateBefore = new Dictionary<string, string>();

	private static int _finished;

	private static App? _app;

	internal static string OutDir => _outDir;

	public static void Begin(App app, string[] args)
	{
		_app = app;
		app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
		int idx = Array.IndexOf(args, "--perfprobe");
		_outDir = (idx >= 0 && idx + 1 < args.Length && !args[idx + 1].StartsWith("--", StringComparison.Ordinal))
			? args[idx + 1]
			: Path.Combine(Path.GetTempPath(), "win81layer-perfprobe");
		_scenario = (idx >= 0 && idx + 2 < args.Length && !args[idx + 2].StartsWith("--", StringComparison.Ordinal))
			? args[idx + 2].ToLowerInvariant()
			: "mem";
		int pol = Array.IndexOf(args, "--policy");
		PerfProbe.LegacyPolicy = pol >= 0 && pol + 1 < args.Length && string.Equals(args[pol + 1], "legacy", StringComparison.OrdinalIgnoreCase);
		_assumeIdle = args.Contains("--assume-idle");
		_metroCold = args.Contains("--cold");
		bool softwareRender = args.Contains("--software-render");
		Directory.CreateDirectory(_outDir);
		// Hard deadline: whatever happens on the UI thread, the process is gone within 115 s.
		Thread deadline = new Thread(delegate()
		{
			Thread.Sleep(HardDeadlineMs);
			try { File.AppendAllText(Path.Combine(_outDir, "perfprobe-hardexit.txt"), DateTime.Now.ToString("O") + " hard deadline\r\n"); } catch { }
			Environment.Exit(3);
		})
		{
			IsBackground = true,
			Name = "PerfProbe.Deadline"
		};
		deadline.Start();
		DispatcherTimer soft = new DispatcherTimer(DispatcherPriority.Send)
		{
			Interval = TimeSpan.FromMilliseconds(SoftDeadlineMs)
		};
		soft.Tick += delegate
		{
			soft.Stop();
			Note("soft deadline reached");
			Finish(2);
		};
		soft.Start();
		app.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, (Action)async delegate
		{
			try
			{
				_stateBefore = HashState();
				AppSettings s = UserLikeSettings(SettingsStore.Load());
				// --software-render: Lighter rendering on (through the override only), applied as App.OnStartup applies it
				s.ProcessSoftwareRender = softwareRender;
				SettingsStore.DiagnosticsOverride = s;
				App.ApplyProcessRenderMode(s);
				Report["softwareRender"] = App.ProcessSoftwareRendering;
				Report["scenario"] = _scenario;
				Report["policy"] = PerfProbe.LegacyPolicy ? "legacy" : "new";
				Report["pid"] = Environment.ProcessId;
				Report["build"] = typeof(PerfProbeDiagnostics).Assembly.Location;
				Report["darkRest"] = DarkRest.VisualsFor(s);
				Report["seasonalActive"] = s.SeasonalActive;
				Report["startBgMode"] = s.StartBgMode;
				Report["assumeIdle"] = _assumeIdle;
				Report["policyFeatures"] = PolicyFeatures();
				switch (_scenario)
				{
				case "mem":
					await RunMem();
					break;
				case "open":
					await RunOpen();
					break;
				case "idle":
					await RunIdle();
					break;
				case "stall":
					await RunStall();
					break;
				case "unit":
					RunUnit();
					break;
				case "metrofirst":
					await RunMetroFirst();
					break;
				case "modeswitch":
					await RunModeSwitch();
					break;
				case "pcsettings":
					await RunPcSettings();
					break;
				default:
					Note("unknown scenario " + _scenario);
					Finish(1);
					return;
				}
				Finish(0);
			}
			catch (Exception ex)
			{
				Note("error: " + ex);
				Finish(1);
			}
		});
	}

	// The user's mode: the Windows 7 Start menu on (Metro Start kept as the model owner) and Dark rest on, applied the way
	// the seasonal harness pins it (the settings file this process can read may be an older copy).
	private static AppSettings UserLikeSettings(AppSettings real)
	{
		AppSettings s = real.Clone();
		s.Win7StartMenuEnabled = true;
		SeasonPreset? p = SeasonalTheme.Find(SeasonalTheme.Rest);
		if (p != null)
		{
			s.StartBgMode = "pattern";
			s.StartPattern = p.Pattern;
			s.StartBgColor = p.Base;
			s.StartAccentColor = p.Accent;
			s.SeasonalActive = p.Id;
			s.RestWallpaper = true;
			s.RestCalmMotion = true;
			s.RestDimTiles = true;
		}
		return s;
	}

	// What this binary implements of the new policy.
	private static string[] PolicyFeatures()
	{
		return new[]
		{
			"1 idle-gated trims off the UI thread (IdleMaintenance)",
			"1b native-host warden on a pool thread",
			"2 hot corners armed by the mouse hook",
			"4 one shell clock with idle back-off",
			"5 heartbeat in shared memory",
			"6 tray host signals only real changes",
			"8 Metro Start visuals deferred in Windows 7 Start mode",
			"10b weather art cache 2 entries"
		};
	}

	private static void Note(string text)
	{
		lock (Notes)
		{
			Notes.Add($"+{Clock.ElapsedMilliseconds}ms {text}");
		}
		try { Logger.Log("[perfprobe] " + text); } catch { }
	}

	private static void Finish(int code)
	{
		if (Interlocked.Exchange(ref _finished, 1) != 0)
		{
			return;
		}
		Dictionary<string, string> after;
		try
		{
			after = HashState();
		}
		catch (Exception ex)
		{
			after = new Dictionary<string, string> { ["<error>"] = ex.Message };
		}
		List<string> changed = new List<string>();
		foreach (KeyValuePair<string, string> kv in _stateBefore)
		{
			if (!after.TryGetValue(kv.Key, out string? v) || v != kv.Value)
			{
				changed.Add(kv.Key);
			}
		}
		foreach (string k in after.Keys)
		{
			if (!_stateBefore.ContainsKey(k))
			{
				changed.Add(k + " (new)");
			}
		}
		string[] refusals = PerfProbe.Refusals;
		bool stateOk = changed.Count == 0;
		if (code == 0 && (!stateOk || refusals.Length > 0))
		{
			code = 1;
		}
		Report["stateFilesHashed"] = _stateBefore.Count;
		Report["stateUnchanged"] = stateOk;
		Report["stateChanged"] = changed;
		Report["refusals"] = refusals;
		Report["notes"] = Notes.ToArray();
		Report["exitCode"] = code;
		Report["elapsedMs"] = Clock.ElapsedMilliseconds;
		try
		{
			string name = $"perfprobe-{_scenario}-{(PerfProbe.LegacyPolicy ? "legacy" : "new")}-{Environment.ProcessId}.json";
			File.WriteAllText(Path.Combine(_outDir, name), JsonSerializer.Serialize(Report, new JsonSerializerOptions { WriteIndented = true }));
		}
		catch
		{
		}
		try
		{
			_app?.Shutdown(code);
		}
		catch
		{
			Environment.Exit(code);
		}
	}

	// ---------------------------------------------------------------- state hash (user state must not change)

	private static Dictionary<string, string> HashState()
	{
		string root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Win81Layer");
		Dictionary<string, string> hashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
		if (!Directory.Exists(root))
		{
			return hashes;
		}
		byte[] buffer = new byte[64 * 1024];
		foreach (string file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
		{
			string rel = Path.GetRelativePath(root, file);
			string name = Path.GetFileName(file);
			if (name.StartsWith("log", StringComparison.OrdinalIgnoreCase)
				|| rel.StartsWith("cache" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
				|| string.Equals(rel, "heartbeat.dat", StringComparison.OrdinalIgnoreCase))
			{
				continue;   // logs, caches and the live shell's liveness stamp are not user state
			}
			try
			{
				using FileStream fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1, FileOptions.SequentialScan);
				using IncrementalHash h = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
				int n;
				while ((n = fs.Read(buffer, 0, buffer.Length)) > 0)
				{
					h.AppendData(buffer, 0, n);
				}
				hashes[rel] = Convert.ToHexString(h.GetHashAndReset()) + ":" + fs.Length;
			}
			catch (Exception ex)
			{
				hashes[rel] = "unreadable:" + ex.GetType().Name;
			}
		}
		return hashes;
	}

	// ---------------------------------------------------------------- process metrics

	[StructLayout(LayoutKind.Sequential)]
	private struct PROCESS_MEMORY_COUNTERS_EX
	{
		public uint cb;
		public uint PageFaultCount;
		public nuint PeakWorkingSetSize;
		public nuint WorkingSetSize;
		public nuint QuotaPeakPagedPoolUsage;
		public nuint QuotaPagedPoolUsage;
		public nuint QuotaPeakNonPagedPoolUsage;
		public nuint QuotaNonPagedPoolUsage;
		public nuint PagefileUsage;
		public nuint PeakPagefileUsage;
		public nuint PrivateUsage;
	}

	[DllImport("psapi.dll", SetLastError = true)]
	private static extern bool GetProcessMemoryInfo(nint process, out PROCESS_MEMORY_COUNTERS_EX counters, uint cb);

	[DllImport("kernel32.dll")]
	private static extern nint GetCurrentProcess();

	[DllImport("kernel32.dll")]
	private static extern nint GetCurrentThread();

	[DllImport("kernel32.dll")]
	private static extern uint GetCurrentThreadId();

	[DllImport("kernel32.dll")]
	private static extern bool GetProcessTimes(nint process, out long creation, out long exit, out long kernel, out long user);

	[DllImport("kernel32.dll")]
	private static extern bool QueryThreadCycleTime(nint thread, out ulong cycles);

	[DllImport("kernel32.dll")]
	private static extern bool QueryProcessCycleTime(nint process, out ulong cycles);

	[DllImport("kernel32.dll")]
	private static extern bool GetProcessHandleCount(nint process, out uint count);

	[DllImport("user32.dll")]
	private static extern int GetWindowLong(nint hwnd, int index);

	[DllImport("user32.dll")]
	private static extern int SetWindowLong(nint hwnd, int index, int value);

	internal static (double PrivateMB, double WorkingSetMB, uint PageFaults) Memory()
	{
		PROCESS_MEMORY_COUNTERS_EX c = default;
		c.cb = (uint)Marshal.SizeOf<PROCESS_MEMORY_COUNTERS_EX>();
		if (!GetProcessMemoryInfo(GetCurrentProcess(), out c, c.cb))
		{
			return (0, 0, 0);
		}
		return (c.PrivateUsage / 1048576.0, c.WorkingSetSize / 1048576.0, c.PageFaultCount);
	}

	private static object Sample(string label)
	{
		(double priv, double ws, uint faults) = Memory();
		GCMemoryInfo gi = GC.GetGCMemoryInfo(GCKind.Any);
		GetProcessHandleCount(GetCurrentProcess(), out uint handles);
		int threads = 0;
		try
		{
			using Process me = Process.GetCurrentProcess();
			threads = me.Threads.Count;
		}
		catch
		{
		}
		return new
		{
			Label = label,
			AtMs = Clock.ElapsedMilliseconds,
			PrivateMB = Math.Round(priv, 2),
			WorkingSetMB = Math.Round(ws, 2),
			PageFaults = faults,
			GcCommittedMB = Math.Round(gi.TotalCommittedBytes / 1048576.0, 2),
			GcHeapMB = Math.Round(gi.HeapSizeBytes / 1048576.0, 2),
			GcLiveMB = Math.Round(GC.GetTotalMemory(false) / 1048576.0, 2),
			Gen0 = GC.CollectionCount(0),
			Gen1 = GC.CollectionCount(1),
			Gen2 = GC.CollectionCount(2),
			JitMethods = System.Runtime.JitInfo.GetCompiledMethodCount(false),
			Handles = handles,
			Threads = threads
		};
	}

	private static Task Delay(int ms)
	{
		return Task.Delay(ms);
	}

	private static Task NextRender()
	{
		TaskCompletionSource tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		EventHandler? h = null;
		h = delegate
		{
			CompositionTarget.Rendering -= h;
			tcs.TrySetResult();
		};
		CompositionTarget.Rendering += h;
		return tcs.Task;
	}

	private static void NoActivate(Window w)
	{
		try
		{
			nint hwnd = new WindowInteropHelper(w).EnsureHandle();
			int ex = GetWindowLong(hwnd, -20);
			if ((ex & 0x08000000) == 0)
			{
				SetWindowLong(hwnd, -20, ex | 0x08000000);
			}
		}
		catch
		{
		}
	}

	// ---------------------------------------------------------------- the probed surfaces, built as the shell builds them

	private sealed class Surfaces
	{
		public StartScreen Start = null!;

		public Win7StartMenu? Win7;

		public bool AppsLoaded;
	}

	private static async Task<Surfaces> BuildSurfaces(bool forOpen)
	{
		Surfaces s = new Surfaces();
		Stopwatch sw = Stopwatch.StartNew();
		s.Start = new StartScreen();   // the live constructor (inventory, profile, background), guarded read-only
		Note($"StartScreen constructed in {sw.ElapsedMilliseconds}ms");
		StartScreen.AppsReloaded += delegate
		{
			s.AppsLoaded = true;
		};
		// As App.OnStartup: the 2 s start warm, the Win7 menu prewarm and the Action Center prewarm at idle.
		DispatcherTimer startWarm = new DispatcherTimer(DispatcherPriority.Background)
		{
			Interval = TimeSpan.FromSeconds(2.0)
		};
		startWarm.Tick += delegate
		{
			startWarm.Stop();
			try
			{
				BootStartWarm(s.Start);
			}
			catch (Exception ex)
			{
				Note("start warm: " + ex.Message);
			}
		};
		startWarm.Start();
		await s.Start.Dispatcher.InvokeAsync(delegate
		{
			try
			{
				s.Win7 = new Win7StartMenu(() => s.Start.AllApps, delegate(AppEntry a, bool asAdmin) { }, null);
				if (forOpen)
				{
					s.Win7.QaInit();   // never placed, never activated, -4000,-4000
				}
				s.Win7.Prewarm();
			}
			catch (Exception ex)
			{
				Note("win7 prewarm: " + ex.Message);
			}
			try
			{
				ActionCenter.Prewarm();
			}
			catch (Exception ex)
			{
				Note("action center prewarm: " + ex.Message);
			}
		}, DispatcherPriority.ApplicationIdle);
		return s;
	}

	// App's 2 s Start warm, mirrored for the probe. WIN81_PROBE_BOARD=1 (experiment): prewarm the board even when deferred.
	private static void BootStartWarm(StartScreen start)
	{
		if (Environment.GetEnvironmentVariable("WIN81_PROBE_BOARD") == "1")
		{
			System.Reflection.FieldInfo? f = typeof(StartScreen).GetField("_visualsDeferred", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
			object? was = f?.GetValue(start);
			f?.SetValue(start, false);
			start.Prewarm();
			f?.SetValue(start, was);
			return;
		}
		start.Prewarm();
	}

	private static object Census(Surfaces s)
	{
		long iconPx = 0;
		int icons = 0;
		int icons256 = 0;
		HashSet<ImageSource> seen = new HashSet<ImageSource>();
		try
		{
			foreach (AppEntry a in s.Start.AllApps)
			{
				if (a.Icon is BitmapSource b && seen.Add(b))
				{
					icons++;
					iconPx += (long)b.PixelWidth * b.PixelHeight;
					if (b.PixelWidth >= 256)
					{
						icons256++;
					}
				}
			}
		}
		catch
		{
		}
		int muted = CountWeakTable(typeof(DarkRestIcons), "MutedArtCache", out long mutedPx);
		long weatherBytes = 0;
		int weatherAssets = 0;
		try
		{
			if (typeof(WeatherTileArt).GetField("AssetCache", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)?.GetValue(null) is Dictionary<string, BitmapSource> cache)
			{
				lock (cache)
				{
					foreach (BitmapSource b in cache.Values)
					{
						weatherAssets++;
						weatherBytes += (long)b.PixelWidth * b.PixelHeight * 4;
					}
				}
			}
		}
		catch
		{
		}
		long bgPx = 0;
		int bgEntries = 0;
		try
		{
			if (typeof(StartScreen).GetField("_bgBrushCache", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)?.GetValue(null) is Dictionary<string, ImageBrush> bg)
			{
				foreach (ImageBrush ib in bg.Values)
				{
					bgEntries++;
					if (ib.ImageSource is BitmapSource b)
					{
						bgPx += (long)b.PixelWidth * b.PixelHeight;
					}
				}
			}
		}
		catch
		{
		}
		bool LazyCreated(string field)
		{
			try
			{
				System.Reflection.FieldInfo? f = typeof(MetroLiveTileArt).GetField(field, System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)
					?? typeof(MetroLiveTileArt).GetField("_" + char.ToLowerInvariant(field[0]) + field.Substring(1), System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
				object? v = f?.GetValue(null);
				return v is Lazy<ImageSource> l ? l.IsValueCreated : v is ImageSource;
			}
			catch
			{
				return false;
			}
		}
		bool deferred = false;
		try
		{
			deferred = (bool)(typeof(StartScreen).GetField("_visualsDeferred", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)?.GetValue(s.Start) ?? false);
		}
		catch
		{
		}
		bool prewarmed = false;
		try
		{
			prewarmed = (bool)(typeof(StartScreen).GetField("_prewarmed", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)?.GetValue(s.Start) ?? false);
		}
		catch
		{
		}
		return new
		{
			AppEntries = s.Start.AllApps?.Count ?? 0,
			DistinctIcons = icons,
			Icons256 = icons256,
			IconPixelsMB = Math.Round(iconPx * 4 / 1048576.0, 2),
			MutedCopies = muted,
			MutedPixelsMB = Math.Round(mutedPx * 4 / 1048576.0, 2),
			WeatherAssets = weatherAssets,
			WeatherAssetMB = Math.Round(weatherBytes / 1048576.0, 2),
			StartBackgroundEntries = bgEntries,
			StartBackgroundMB = Math.Round(bgPx * 4 / 1048576.0, 2),
			RestBackgroundHeld = s.Start.QaRestBackgroundHeld,
			AppsViewBuilt = s.Start.QaAppsViewBuilt,
			AppRowVisuals = s.Start.QaAppVisualCount,
			BoardPrewarmed = prewarmed,
			VisualsDeferred = deferred,
			ClockArtDecoded = LazyCreated("ClockBackground"),
			AgendaArtDecoded = LazyCreated("AgendaBackground")
		};
	}

	private static int CountWeakTable(Type owner, string field, out long pixels)
	{
		pixels = 0;
		int n = 0;
		try
		{
			object? table = owner.GetField(field, System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)?.GetValue(null);
			if (table is System.Collections.IEnumerable e)
			{
				foreach (object? kv in e)
				{
					n++;
					object? value = kv?.GetType().GetProperty("Value")?.GetValue(kv);
					if (value is WeakReference<ImageSource> wr)
					{
						value = wr.TryGetTarget(out ImageSource? t) ? t : null;
						if (value == null)
						{
							n--;   // a slot whose copy was collected holds no pixels
						}
					}
					if (value is BitmapSource b)
					{
						pixels += (long)b.PixelWidth * b.PixelHeight;
					}
				}
			}
		}
		catch
		{
		}
		return n;
	}

	private static async Task WaitApps(Surfaces s, int maxMs)
	{
		Stopwatch sw = Stopwatch.StartNew();
		while (!s.AppsLoaded && sw.ElapsedMilliseconds < maxMs)
		{
			await Delay(250);
		}
		Note($"apps loaded={s.AppsLoaded} after {sw.ElapsedMilliseconds}ms ({s.Start.AllApps?.Count ?? 0} entries)");
	}

	// ---------------------------------------------------------------- mem

	private static async Task RunMem()
	{
		List<object> samples = new List<object> { Sample("baseline") };
		Surfaces s = await BuildSurfaces(forOpen: false);
		await WaitApps(s, 20000);
		samples.Add(Sample("apps-loaded"));
		// App's one-shot boot trim at 12 s.
		await DelayUntil(12000);
		BootTrim();
		samples.Add(Sample("after-boot-trim"));
		await DelayUntil(45000);
		GC.Collect();
		GC.WaitForPendingFinalizers();
		GC.Collect();
		await DelayUntil(50000);
		samples.Add(Sample("t50"));
		Report["censusT50"] = Census(s);
		await DelayUntil(70000);
		samples.Add(Sample("t70"));
		await DelayUntil(88000);
		samples.Add(Sample("t88"));
		Report["census"] = Census(s);
		Report["samples"] = samples;
	}

	// App's one-shot boot trim, mirrored for the probe (own process only).
	private static void BootTrim()
	{
		GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true);
		NativeShell.TrimSelf();
	}

	private static async Task DelayUntil(long atMs)
	{
		long wait = atMs - Clock.ElapsedMilliseconds;
		if (wait > 0)
		{
			await Delay((int)wait);
		}
	}

	// ---------------------------------------------------------------- metrofirst

	// The first Metro Start show after boot in Windows 7 Start mode: the background on the first rendered frame must be the
	// decoded picture (deferred builds decode it synchronously before that frame), and the settled render is written to
	// PNG for a pixel comparison between the policies.
	// By default the run follows the only way to the full Metro Start in that mode: the Settings pane opens (the shell's
	// signal: StartScreen.PrepareInBackground), the user reads it for a moment, then Personalization shows Metro Start.
	// The preparation's UI-thread steps are measured with a dispatch-delay meter (what the open pane would feel).
	// --cold: no signal (the show pays everything, as with the --start / --apps switches).
	private static async Task RunMetroFirst()
	{
		Surfaces s = await BuildSurfaces(forOpen: false);
		await WaitApps(s, 20000);
		await DelayUntil(15000);
		StartScreen start = s.Start;
		start.Width = 1366.0;
		start.Height = 768.0;
		start.Left = -6000.0;
		start.Top = -6000.0;
		start.ShowActivated = false;
		start.Topmost = false;
		NoActivate(start);
		object? preparation = null;
		object? censusBeforeSignal = null;
		if (!_metroCold)
		{
			censusBeforeSignal = Census(s);
			double privBefore = Memory().PrivateMB;
			List<double> delays = new List<double>();
			object delaysGate = new object();
			bool metering = true;
			Dispatcher ui = start.Dispatcher;
			Thread meter = new Thread(delegate()
			{
				while (Volatile.Read(ref metering))
				{
					long posted = Stopwatch.GetTimestamp();
					ui.BeginInvoke(DispatcherPriority.Input, (Action)delegate
					{
						double ms = (Stopwatch.GetTimestamp() - posted) * 1000.0 / Stopwatch.Frequency;
						lock (delaysGate)
						{
							delays.Add(ms);
						}
					});
					Thread.Sleep(10);
				}
			})
			{
				IsBackground = true,
				Name = "PerfProbe.PrepareMeter"
			};
			meter.Start();
			Stopwatch pw = Stopwatch.StartNew();
			start.PrepareInBackground("probe: Settings pane opened");
			while (start.PrepareActive && pw.ElapsedMilliseconds < 10000)
			{
				await Delay(20);
			}
			double wallMs = pw.Elapsed.TotalMilliseconds;
			await Delay(300);
			Volatile.Write(ref metering, false);
			meter.Join(500);
			List<double> d;
			lock (delaysGate)
			{
				d = delays.ToList();
			}
			await Delay(700);   // the user reads the pane; the live-tile art lands
			preparation = new
			{
				Prepared = start.QaPrepared,
				Steps = start.QaPrepareSteps,
				GroupsOneByOne = start.QaPrepareGroups,
				UiMsTotal = Math.Round(start.QaPrepareStepsMs, 2),
				LongestStepMs = Math.Round(start.QaPrepareMaxStepMs, 2),
				StepLog = start.QaPrepareStepLog.ToArray(),
				WallMs = Math.Round(wallMs, 1),
				MeterSamples = d.Count,
				InputDispatchMaxDelayMs = d.Count == 0 ? double.NaN : Math.Round(d.Max(), 2),
				InputDispatchP99Ms = Math.Round(Percentile(d, 0.99), 2),
				InputDispatchOver50Ms = d.Count(x => x > 50.0),
				PrivateMBAdded = Math.Round(Memory().PrivateMB - privBefore, 2)
			};
		}
		object census0 = Census(s);
		// Split the cost: realizing the deferred visuals (background decode, live-tile warm) and then the show itself.
		Stopwatch ev = Stopwatch.StartNew();
		bool split = Environment.GetEnvironmentVariable("WIN81_PROBE_SPLIT") == "1";
		if (split)
		{
			typeof(StartScreen).GetMethod("EnsureVisualsReady", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)?.Invoke(start, null);
		}
		double ensureMs = ev.Elapsed.TotalMilliseconds;
		uint f0 = Memory().PageFaults;
		Stopwatch sw = Stopwatch.StartNew();
		Task frame = NextRender();
		start.Show();
		Brush atShow = start.QaBgLayerBrush;
		await frame;
		double firstFrameMs = sw.Elapsed.TotalMilliseconds;
		Brush atFrame = start.QaBgLayerBrush;
		uint f1 = Memory().PageFaults;
		await Delay(2500);
		string png = Path.Combine(_outDir, $"metrofirst-{(PerfProbe.LegacyPolicy ? "legacy" : "new")}-{Environment.ProcessId}.png");
		start.QaRenderSeason(png, 1366.0, 768.0, withAmbient: false);
		Brush settled = start.QaBgLayerBrush;
		start.Hide();
		static object Describe(Brush b)
		{
			if (b is ImageBrush ib && ib.ImageSource is BitmapSource bs)
			{
				return new { Kind = "picture", Width = bs.PixelWidth, Height = bs.PixelHeight };
			}
			return new { Kind = b?.GetType().Name ?? "null", Width = 0, Height = 0 };
		}
		bool firstIsPicture = atFrame is ImageBrush { ImageSource: BitmapSource };
		bool sameAsSettled = ReferenceEquals((atFrame as ImageBrush)?.ImageSource, (settled as ImageBrush)?.ImageSource);
		Report["metroFirst"] = new
		{
			Cold = _metroCold,
			CensusBeforeSignal = censusBeforeSignal,
			Preparation = preparation,
			CensusBeforeShow = census0,
			BackgroundAtShow = Describe(atShow),
			BackgroundAtFirstFrame = Describe(atFrame),
			BackgroundSettled = Describe(settled),
			FirstFrameIsPicture = firstIsPicture,
			FirstFrameIsSettledPicture = sameAsSettled,
			ShowToFirstFrameMs = Math.Round(firstFrameMs, 2),
			SplitEnsureVisualsMs = split ? Math.Round(ensureMs, 2) : -1.0,
			PageFaultsDuringShow = f1 - f0,
			Png = png
		};
		if (!firstIsPicture || !sameAsSettled)
		{
			Note("first frame did not carry the settled background picture");
			throw new InvalidOperationException("metrofirst: background flash");
		}
		if (_metroCold || PerfProbe.LegacyPolicy)
		{
			return;
		}
		// Five minutes later (hidden, Windows 7 Start mode) the visuals are released; the next Settings-pane signal must
		// bring the clock, agenda and news faces back before Metro Start is shown again.
		await Delay(300);
		start.MaybeReleaseVisuals(Environment.TickCount64 + 600000L);
		bool released = start.VisualsDeferred;
		(int Built, int Total) facesReleased = start.QaLiveFaces;
		Stopwatch rw = Stopwatch.StartNew();
		start.PrepareInBackground("probe: Settings pane opened again");
		while (start.PrepareActive && rw.ElapsedMilliseconds < 10000)
		{
			await Delay(20);
		}
		await Delay(700);
		(int Built, int Total) facesBack = start.QaLiveFaces;
		Report["metroReprepare"] = new
		{
			Released = released,
			FacesAfterRelease = facesReleased.Built,
			FacesBeforeShow = facesBack.Built,
			LiveFaceTiles = facesBack.Total,
			Prepared = start.QaPrepared,
			LongestStepMs = Math.Round(start.QaPrepareMaxStepMs, 2),
			BackgroundSettled = start.QaBackgroundSettled
		};
		if (!released || facesReleased.Built != 0 || facesBack.Built != facesBack.Total || !start.QaPrepared || !start.QaBackgroundSettled)
		{
			Note("after a release, the next preparation did not bring the live faces and the background back");
			throw new InvalidOperationException("metrofirst: live faces or background missing after a release");
		}
	}

	// ---------------------------------------------------------------- modeswitch

	// The Windows 7 menu turned off at run time (the PC Settings toggle): Metro Start is the Start surface again and is
	// prepared at idle. Measured: the UI dispatch delay while that runs (what the PC Settings window and the taskbar would
	// feel), the preparation's steps, and whether the board and the all-apps view end up ready, as at boot in Metro mode.
	// Only fields change (SetWin7Mode never writes settings); the mode is set back at the end.
	private static async Task RunModeSwitch()
	{
		Surfaces s = await BuildSurfaces(forOpen: false);
		await WaitApps(s, 20000);
		await DelayUntil(15000);
		StartScreen start = s.Start;
		List<double> delays = new List<double>();
		bool metering = true;
		Dispatcher ui = start.Dispatcher;
		Thread meter = new Thread(delegate()
		{
			while (Volatile.Read(ref metering))
			{
				long posted = Stopwatch.GetTimestamp();
				ui.BeginInvoke(DispatcherPriority.Input, (Action)delegate
				{
					double ms = (Stopwatch.GetTimestamp() - posted) * 1000.0 / Stopwatch.Frequency;
					lock (delays)
					{
						delays.Add(ms);
					}
				});
				Thread.Sleep(10);
			}
		})
		{
			IsBackground = true,
			Name = "PerfProbe.SwitchMeter"
		};
		meter.Start();
		await Delay(300);
		Stopwatch pw = Stopwatch.StartNew();
		start.SetWin7Mode(false);
		bool sliced = start.PrepareActive;
		while ((start.PrepareActive || !start.QaPrepared || !start.QaAppsViewBuilt) && pw.ElapsedMilliseconds < 10000)
		{
			await Delay(20);
		}
		double wallMs = pw.Elapsed.TotalMilliseconds;
		await Delay(300);
		Volatile.Write(ref metering, false);
		meter.Join(500);
		List<double> d;
		lock (delays)
		{
			d = delays.ToList();
		}
		Report["modeSwitch"] = new
		{
			Sliced = sliced,
			Prepared = start.QaPrepared,
			AppsViewBuilt = start.QaAppsViewBuilt,
			Steps = sliced ? start.QaPrepareSteps : 0,
			UiMsTotal = sliced ? Math.Round(start.QaPrepareStepsMs, 2) : 0.0,
			LongestStepMs = sliced ? Math.Round(start.QaPrepareMaxStepMs, 2) : 0.0,
			ReadyAfterMs = Math.Round(wallMs, 1),
			MeterSamples = d.Count,
			InputDispatchMaxDelayMs = d.Count == 0 ? double.NaN : Math.Round(d.Max(), 2),
			InputDispatchP99Ms = Math.Round(Percentile(d, 0.99), 2),
			InputDispatchOver50Ms = d.Count(x => x > 50.0),
			InputDispatchOver100Ms = d.Count(x => x > 100.0)
		};
		start.SetWin7Mode(true);
		if (!start.QaPrepared || !start.QaAppsViewBuilt)
		{
			Note("Metro Start was not ready after the Windows 7 menu was turned off");
			throw new InvalidOperationException("modeswitch: not ready");
		}
	}

	// ---------------------------------------------------------------- open

	private static async Task RunOpen()
	{
		Surfaces s = await BuildSurfaces(forOpen: true);
		await WaitApps(s, 20000);
		await Delay(1500);
		StartScreen start = s.Start;
		start.Width = 1366.0;
		start.Height = 768.0;
		start.Left = -5000.0;
		start.Top = -5000.0;
		start.ShowActivated = false;
		start.Topmost = false;
		NoActivate(start);
		Window? ac = ActionCenterWindow();
		if (ac != null)
		{
			ac.ShowActivated = false;
			ac.Topmost = false;
			ac.Left = -4600.0;
			ac.Top = -4600.0;
			NoActivate(ac);
		}
		string[] surfaces = { "win7", "actioncenter", "metro" };
		// Warm: three opens each.
		for (int i = 0; i < 3; i++)
		{
			foreach (string name in surfaces)
			{
				await OpenOnce(s, ac, name);
				await Delay(150);
			}
		}
		BootTrim();
		await Delay(1000);
		Dictionary<string, List<double>> msTrim = surfaces.ToDictionary(n => n, n => new List<double>());
		Dictionary<string, List<double>> msWarm = surfaces.ToDictionary(n => n, n => new List<double>());
		Dictionary<string, List<double>> faultsTrim = surfaces.ToDictionary(n => n, n => new List<double>());
		Dictionary<string, List<double>> faultsWarm = surfaces.ToDictionary(n => n, n => new List<double>());
		int cycle = 0;
		while (Clock.ElapsedMilliseconds < 88000 && cycle < 30)
		{
			string name = surfaces[cycle % surfaces.Length];
			bool trim = (cycle / surfaces.Length) % 2 == 0;
			if (trim)
			{
				NativeShell.TrimSelf();
			}
			await Delay(3000);
			(double ms, double faults) = await OpenOnce(s, ac, name);
			(trim ? msTrim : msWarm)[name].Add(Math.Round(ms, 2));
			(trim ? faultsTrim : faultsWarm)[name].Add(faults);
			cycle++;
		}
		Report["openAfterTrimMs"] = msTrim;
		Report["openNoTrimMs"] = msWarm;
		Report["openAfterTrimFaults"] = faultsTrim;
		Report["openNoTrimFaults"] = faultsWarm;
		Report["openSummary"] = surfaces.Select(n => new
		{
			Surface = n,
			AfterTrimMedianMs = Median(msTrim[n]),
			NoTrimMedianMs = Median(msWarm[n]),
			AfterTrimMedianFaults = Median(faultsTrim[n]),
			NoTrimMedianFaults = Median(faultsWarm[n])
		}).ToArray();
		try { s.Win7?.Dismiss("probe", instant: true); } catch { }
		try { start.Hide(); } catch { }
		try { ac?.Hide(); } catch { }
	}

	private static Window? ActionCenterWindow()
	{
		try
		{
			return typeof(ActionCenter).GetField("_instance", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)?.GetValue(null) as Window;
		}
		catch
		{
			return null;
		}
	}

	// Show -> first rendered frame after layout; then hide. Returns (ms, page faults during the open).
	private static async Task<(double, double)> OpenOnce(Surfaces s, Window? ac, string name)
	{
		uint f0 = Memory().PageFaults;
		Stopwatch sw = Stopwatch.StartNew();
		Task frame = NextRender();
		switch (name)
		{
		case "win7":
			s.Win7?.ShowMenu("probe");
			break;
		case "actioncenter":
			if (ac != null)
			{
				ac.Left = -4600.0;
				ac.Top = -4600.0;
				ac.Show();
			}
			break;
		default:
			s.Start.Left = -5000.0;
			s.Start.Top = -5000.0;
			s.Start.Show();
			break;
		}
		await frame;
		double ms = sw.Elapsed.TotalMilliseconds;
		uint f1 = Memory().PageFaults;
		await Delay(120);
		switch (name)
		{
		case "win7":
			s.Win7?.Dismiss("probe", instant: true);
			break;
		case "actioncenter":
			ac?.Hide();
			break;
		default:
			s.Start.Hide();
			break;
		}
		await Delay(60);
		return (ms, f1 - f0);
	}

	private static double Median(List<double> v)
	{
		if (v.Count == 0)
		{
			return double.NaN;
		}
		List<double> o = v.OrderBy(x => x).ToList();
		return o.Count % 2 == 1 ? o[o.Count / 2] : Math.Round((o[o.Count / 2 - 1] + o[o.Count / 2]) / 2.0, 3);
	}

	private static double Percentile(List<double> v, double p)
	{
		if (v.Count == 0)
		{
			return double.NaN;
		}
		List<double> o = v.OrderBy(x => x).ToList();
		int rank = (int)Math.Ceiling(p * o.Count) - 1;
		return o[Math.Clamp(rank, 0, o.Count - 1)];
	}

	// ---------------------------------------------------------------- idle

	// Wake-up accounting of the idle shell schedule. The bars cannot exist in the probe, so read-only consumers do the
	// same gather work (window enumeration, identity, foreground and fullscreen tests, tray telemetry) on the same
	// cadence the shell uses; hot corners and the heartbeat are the real classes / the same file write.
	private static async Task RunIdle()
	{
		uint uiTid = GetCurrentThreadId();
		double tscPerMs = CyclesPerMs();
		PerfProbeIdleRig rig = PerfProbeIdleRig.Create(PerfProbe.LegacyPolicy, _outDir, _assumeIdle);
		rig.Start();
		await Delay(30000);   // settle (JIT, first gathers)
		IdleCounters c0 = IdleCounters.Take(uiTid, rig);
		await Delay(60000);
		IdleCounters c1 = IdleCounters.Take(uiTid, rig);
		rig.Stop();
		double secs = (c1.AtMs - c0.AtMs) / 1000.0;
		Report["idle"] = new
		{
			WindowSeconds = Math.Round(secs, 1),
			UiContextSwitchesPerSec = Math.Round((c1.UiCs - c0.UiCs) / secs, 2),
			ProcessContextSwitchesPerSec = Math.Round((c1.ProcCs - c0.ProcCs) / secs, 2),
			UiCpuMsPerSec = Math.Round((c1.UiCpu100ns - c0.UiCpu100ns) / 10000.0 / secs, 3),
			UiCyclesPerSecM = Math.Round((c1.UiCycles - c0.UiCycles) / 1e6 / secs, 3),
			ProcessCyclesPerSecM = Math.Round((c1.ProcCycles - c0.ProcCycles) / 1e6 / secs, 3),
			UiCpuMsPerSecFromCycles = Math.Round((c1.UiCycles - c0.UiCycles) / tscPerMs / secs, 3),
			ProcessCpuMsPerSecFromCycles = Math.Round((c1.ProcCycles - c0.ProcCycles) / tscPerMs / secs, 3),
			CyclesPerMs = Math.Round(tscPerMs, 0),
			DispatcherTicksPerSec = Math.Round((c1.Ticks - c0.Ticks) / secs, 2),
			HotCornerPollsPerSec = Math.Round((c1.HotPolls - c0.HotPolls) / secs, 2),
			GathersPerSec = Math.Round((c1.Gathers - c0.Gathers) / secs, 3),
			HeartbeatFileWritesPerSec = Math.Round((c1.FileBeats - c0.FileBeats) / secs, 3),
			HeartbeatBeatsPerSec = Math.Round((c1.Beats - c0.Beats) / secs, 3),
			ThreadPoolItemsPerSec = Math.Round((c1.TpItems - c0.TpItems) / secs, 2),
			PageFaultsPerSec = Math.Round((c1.Faults - c0.Faults) / secs, 2),
			Threads = c1.Threads
		};
		Report["idleSchedule"] = rig.Describe();
	}

	// Thread cycle counts tick at the invariant TSC rate: calibrate it with a short spin on this thread.
	private static double CyclesPerMs()
	{
		QueryThreadCycleTime(GetCurrentThread(), out ulong c0);
		Stopwatch sw = Stopwatch.StartNew();
		while (sw.Elapsed.TotalMilliseconds < 40.0)
		{
		}
		QueryThreadCycleTime(GetCurrentThread(), out ulong c1);
		double ms = sw.Elapsed.TotalMilliseconds;
		return ms > 0 ? (c1 - c0) / ms : 1.0;
	}

	private sealed class IdleCounters
	{
		public long AtMs;
		public long UiCs;
		public long ProcCs;
		public long UiCpu100ns;
		public long ProcCpu100ns;
		public ulong UiCycles;
		public ulong ProcCycles;
		public long Ticks;
		public long HotPolls;
		public long Gathers;
		public long FileBeats;
		public long Beats;
		public long TpItems;
		public long Faults;
		public int Threads;

		private static readonly SystemProcessSnapshot Snap = new SystemProcessSnapshot();

		private static readonly List<SystemProcessSnapshot.ThreadInfo> Threads_ = new List<SystemProcessSnapshot.ThreadInfo>();

		public static IdleCounters Take(uint uiTid, PerfProbeIdleRig rig)
		{
			IdleCounters c = new IdleCounters { AtMs = Clock.ElapsedMilliseconds };
			if (Snap.Refresh() && Snap.ThreadsOf((uint)Environment.ProcessId, Threads_))
			{
				foreach (SystemProcessSnapshot.ThreadInfo t in Threads_)
				{
					c.ProcCs += t.ContextSwitches;
					c.ProcCpu100ns += t.KernelTime100ns + t.UserTime100ns;
					if (t.Tid == uiTid)
					{
						c.UiCs = t.ContextSwitches;
						c.UiCpu100ns = t.KernelTime100ns + t.UserTime100ns;
					}
				}
				c.Threads = Threads_.Count;
			}
			QueryThreadCycleTime(GetCurrentThread(), out c.UiCycles);
			QueryProcessCycleTime(GetCurrentProcess(), out c.ProcCycles);
			c.Ticks = rig.Ticks;
			c.HotPolls = rig.HotPolls;
			c.Gathers = rig.Gathers;
			c.FileBeats = rig.FileBeats;
			c.Beats = rig.Beats;
			c.TpItems = ThreadPool.CompletedWorkItemCount;
			c.Faults = Memory().PageFaults;
			return c;
		}
	}

	// ---------------------------------------------------------------- stall

	// UI dispatch delay while the periodic maintenance runs, accelerated to every 5 s. Legacy: the working-set trim and
	// the native-host probe (process enumeration, read-only) on the UI thread, as the taskbar's 45 s tick did.
	private static async Task RunStall()
	{
		Surfaces s = await BuildSurfaces(forOpen: false);
		await WaitApps(s, 15000);
		await DelayUntil(15000);
		BootTrim();
		await Delay(1000);
		List<double> delays = new List<double>();
		object delaysGate = new object();
		bool running = true;
		Dispatcher ui = s.Start.Dispatcher;
		Thread meter = new Thread(delegate()
		{
			while (Volatile.Read(ref running))
			{
				long posted = Stopwatch.GetTimestamp();
				ui.BeginInvoke(DispatcherPriority.Send, (Action)delegate
				{
					double ms = (Stopwatch.GetTimestamp() - posted) * 1000.0 / Stopwatch.Frequency;
					lock (delaysGate)
					{
						delays.Add(ms);
					}
				});
				Thread.Sleep(10);
			}
		})
		{
			IsBackground = true,
			Name = "PerfProbe.StallMeter"
		};
		meter.Start();
		List<double> opMs = new List<double>();
		int ops = 0;
		while (Clock.ElapsedMilliseconds < 82000 && ops < 12)
		{
			await Delay(5000);
			opMs.Add(Math.Round(RunMaintenanceOnce(), 3));
			ops++;
		}
		await Delay(1500);
		Volatile.Write(ref running, false);
		meter.Join(500);
		List<double> d;
		lock (delaysGate)
		{
			d = delays.ToList();
		}
		Report["stall"] = new
		{
			Samples = d.Count,
			P50Ms = Math.Round(Percentile(d, 0.50), 3),
			P99Ms = Math.Round(Percentile(d, 0.99), 3),
			MaxMs = d.Count == 0 ? double.NaN : Math.Round(d.Max(), 3),
			Over16Ms = d.Count(x => x > 16.0),
			Over8Ms = d.Count(x => x > 8.0),
			MaintenanceRuns = ops,
			MaintenanceUiMs = opMs
		};
	}

	// One maintenance pass as the policy schedules it. Returns the UI-thread time it took. New: the trim and the host
	// snapshot (dry run: parsed, no handle opened) go to the thread pool.
	private static double RunMaintenanceOnce()
	{
		Stopwatch sw = Stopwatch.StartNew();
		if (PerfProbe.LegacyPolicy)
		{
			NativeShell.TrimSelf();
			LegacyHostProbeDryRun();
		}
		else
		{
			ThreadPool.UnsafeQueueUserWorkItem(delegate
			{
				NativeShell.TrimSelf();
				_lastDryRun = NativeHostWarden.DryRun();
			}, null);
		}
		return sw.Elapsed.TotalMilliseconds;
	}

	private static (int Hosts, int Running, int Suspended, double Ms) _lastDryRun;

	// ---------------------------------------------------------------- unit (pure logic, fake clocks, no windows)

	private static readonly List<string> UnitResults = new List<string>();

	private static void UnitCheck(string name, bool ok, string detail)
	{
		UnitResults.Add((ok ? "PASS " : "FAIL ") + name + ": " + detail);
	}

	private static void RunUnit()
	{
		UnitIdleTrimPolicy();
		UnitShellClockCadence();
		UnitHotCornerZones();
		UnitHotCornerOuterPush();
		UnitHookLiveness();
		UnitShellClockAsleep();
		UnitAumidCache();
		UnitSuspensionGuard();
		UnitHeartbeat();
		UnitWardenDryRun();
		UnitMotionTokens();
		UnitMotionFirstFrame();
		UnitNativeHostsExcludeNotificationHost();
		UnitTrayMenuMetrics();
		Report["unit"] = UnitResults.ToArray();
		Report["unitPassed"] = UnitResults.Count(r => r.StartsWith("PASS", StringComparison.Ordinal));
		Report["unitFailed"] = UnitResults.Count(r => r.StartsWith("FAIL", StringComparison.Ordinal));
		try
		{
			File.WriteAllLines(Path.Combine(_outDir, "perfprobe-unit-checks.txt"), UnitResults);
		}
		catch
		{
		}
		if (UnitResults.Any(r => r.StartsWith("FAIL", StringComparison.Ordinal)))
		{
			Note("unit checks failed");
			throw new InvalidOperationException("unit checks failed");
		}
	}

	private static void UnitIdleTrimPolicy()
	{
		IdleTrimPolicy p = new IdleTrimPolicy();
		const long S = 1000L;
		long stamp = 0L;
		bool ok1 = p.Evaluate(10 * S, 0L, stamp, false, false) == IdleTrimPolicy.Reason.None;
		UnitCheck("trim-not-while-active", ok1, "input 0 s idle, shell used 10 s ago -> no trim");
		bool ok2 = p.Evaluate(121 * S, 121 * S, stamp, false, false) == IdleTrimPolicy.Reason.Idle;
		p.Trimmed(121 * S);
		UnitCheck("trim-after-2min-input-idle", ok2, "input idle 121 s -> trim");
		bool ok3 = p.Evaluate(200 * S, 200 * S, stamp, false, false) == IdleTrimPolicy.Reason.None;
		UnitCheck("trim-once-per-episode", ok3, "same episode, still idle -> no second trim");
		stamp = 300 * S;   // the shell is used again: a new episode
		bool ok4 = p.Evaluate(500 * S, 150 * S, stamp, false, false) == IdleTrimPolicy.Reason.None;
		UnitCheck("trim-spacing-10min", ok4, "new episode but only 379 s since the last trim -> no trim");
		bool ok5 = p.Evaluate(722 * S, 130 * S, stamp, false, false) == IdleTrimPolicy.Reason.Idle;
		UnitCheck("trim-new-episode-after-spacing", ok5, "new episode, 601 s since the last trim, input idle 130 s -> trim");
		p.Trimmed(722 * S);
		stamp = 800 * S;
		bool ok6 = p.Evaluate(2000 * S, 0L, stamp, true, true) == IdleTrimPolicy.Reason.None;
		UnitCheck("trim-never-under-open-surface", ok6, "surface open, even with memory pressure -> no trim");
		bool ok7 = p.Evaluate(2000 * S, 0L, stamp, false, false) == IdleTrimPolicy.Reason.Idle;
		UnitCheck("trim-shell-unused-5min", ok7, "input active but the shell unused for 1200 s -> trim");
		p.Trimmed(2000 * S);
		bool ok8 = p.Evaluate(2060 * S, 0L, stamp, false, true) == IdleTrimPolicy.Reason.None
			&& p.Evaluate(2121 * S, 0L, stamp, false, true) == IdleTrimPolicy.Reason.Pressure;
		UnitCheck("trim-pressure-rate-limited", ok8, "pressure 60 s after a trim -> no; 121 s after -> trim");
		p.Trimmed(2121 * S);
		int lockTrims = 0;
		for (int i = 0; i < 6; i++)
		{
			long t = 2130 * S + i * 15 * S;   // lock / unlock flapping every 15 s
			if (p.EvaluateAsleep(t, false, IdleTrimPolicy.Reason.Lock) != IdleTrimPolicy.Reason.None)
			{
				lockTrims++;
				p.Trimmed(t);
			}
		}
		bool ok9 = lockTrims == 0 && p.EvaluateAsleep(2242 * S, false, IdleTrimPolicy.Reason.Lock) == IdleTrimPolicy.Reason.Lock;
		UnitCheck("trim-lock-flapping-limited", ok9, $"6 locks within 90 s of a trim -> {lockTrims} trims; 121 s later -> trim");
		bool ok10 = p.EvaluateAsleep(5000 * S, true, IdleTrimPolicy.Reason.DisplayOff) == IdleTrimPolicy.Reason.None;
		UnitCheck("trim-display-off-not-under-surface", ok10, "display off with a surface open -> no trim");
	}

	private static void UnitShellClockCadence()
	{
		bool d = ShellClock.Decide(0, false) == ShellClock.Mode.Active && ShellClock.Decide(59999, false) == ShellClock.Mode.Active
			&& ShellClock.Decide(60000, false) == ShellClock.Mode.Idle && ShellClock.Decide(0, true) == ShellClock.Mode.Asleep;
		UnitCheck("clock-modes", d, "active < 60 s idle <= idle; locked/display-off -> asleep");
		bool c = ShellClock.IntervalMs(ShellClock.Mode.Active) == 1000 && ShellClock.IntervalMs(ShellClock.Mode.Idle) == 3000 && ShellClock.IntervalMs(ShellClock.Mode.Asleep) == 5000
			&& ShellClock.RefreshEveryMs(ShellClock.Mode.Active) == 2000 && ShellClock.RefreshEveryMs(ShellClock.Mode.Idle) == 10000;
		UnitCheck("clock-cadence", c, "ticks 1/3/5 s; bars refresh 2 s active, 10 s idle");
		// A real clock instance driven by hand (never started): refresh spacing, heartbeat on every tick, kick.
		long idle = 0L;
		Func<long>? saved = ShellClock.InputIdleOverride;
		ShellClock.InputIdleOverride = () => idle;
		try
		{
			ShellClock clock = new ShellClock(Dispatcher.CurrentDispatcher, watchSession: false);
			int beats = 0, refreshes = 0, fasts = 0, sup = 0;
			clock.Heartbeat = () => beats++;
			clock.RefreshBars = () => refreshes++;
			clock.FastBars = () => fasts++;
			clock.Supervisor = () => sup++;
			clock.Tick();
			clock.Tick();
			bool t1 = beats == 2 && refreshes == 1 && fasts == 2 && sup == 1 && clock.CurrentMode == ShellClock.Mode.Active && !ShellClock.WantKick;
			UnitCheck("clock-active-ticks", t1, $"2 quick ticks: beats={beats} refreshes={refreshes} fast={fasts} supervisor={sup}");
			idle = 70000L;
			clock.Tick();
			bool t2 = clock.CurrentMode == ShellClock.Mode.Idle && ShellClock.WantKick && beats == 3 && sup == 2;
			UnitCheck("clock-idle-backoff", t2, $"mode={clock.CurrentMode} wantKick={ShellClock.WantKick} beats={beats} supervisor={sup}");
			idle = 0L;
			clock.Kick();
			bool t3 = clock.CurrentMode == ShellClock.Mode.Active && beats == 4;
			UnitCheck("clock-kick-resumes-active", t3, $"mode={clock.CurrentMode} beats={beats}");
			ShellClock.WantKick = false;
			clock.Dispose();
			// A consumer that throws on every tick (a bar) stops none of the others.
			ShellClock iso = new ShellClock(Dispatcher.CurrentDispatcher, watchSession: false);
			int ib = 0, ir = 0, isup = 0, im = 0;
			int failures0 = ShellClock.Failures;
			iso.Heartbeat = () => ib++;
			iso.FastBars = () => throw new InvalidOperationException("probe: failing bar");
			iso.RefreshBars = () => { ir++; throw new InvalidOperationException("probe: failing refresh"); };
			iso.Supervisor = () => isup++;
			iso.Maintenance = (_, _) => im++;
			iso.Tick();
			iso.Tick();
			int failed = ShellClock.Failures - failures0;
			bool t4 = ib == 2 && ir == 1 && isup == 1 && im == 2 && failed == 3;
			UnitCheck("clock-consumer-isolation", t4, $"fast bars and refresh throwing: heartbeat={ib} refresh={ir} supervisor={isup} maintenance={im} failures logged={failed}");
			ShellClock.WantKick = false;
			iso.Dispose();
		}
		finally
		{
			ShellClock.InputIdleOverride = saved;
		}
	}

	// The hook-side zones must equal exactly the regions the poll tests (HotCorners.Poll), on several monitor layouts.
	private static void UnitHotCornerZones()
	{
		System.Drawing.Rectangle[][] layouts =
		{
			new[] { new System.Drawing.Rectangle(0, 0, 1920, 1080), new System.Drawing.Rectangle(1920, 0, 1920, 1080) },
			new[] { new System.Drawing.Rectangle(0, 0, 1920, 1080), new System.Drawing.Rectangle(-1920, 200, 1920, 1080) },
			new[] { new System.Drawing.Rectangle(0, 0, 2560, 1440), new System.Drawing.Rectangle(2560, 360, 1920, 1080), new System.Drawing.Rectangle(0, 1440, 1920, 1080) },
			new[] { new System.Drawing.Rectangle(0, 0, 1366, 768) }
		};
		int mismatches = 0;
		int samples = 0;
		string first = "";
		foreach (System.Drawing.Rectangle[] screens in layouts)
		{
			System.Drawing.Rectangle virt = screens[0];
			foreach (System.Drawing.Rectangle s in screens)
			{
				virt = System.Drawing.Rectangle.Union(virt, s);
			}
			foreach (bool right in new[] { false, true })
			{
				int[] zones = HotCorners.BuildZonesFor(screens, virt, bottomLeft: right, topLeft: true, rightCorners: right, leftEdge: true);
				foreach (System.Drawing.Rectangle s in screens)
				{
					foreach (int x in EdgeSamples(s.Left, s.Right))
					{
						foreach (int y in EdgeSamples(s.Top, s.Bottom))
						{
							samples++;
							int want = PollMask(new System.Drawing.Point(x, y), screens, virt, right, true, right, true);
							int got = HotCorners.ZoneMask(x, y, zones);
							if (want != got)
							{
								if (mismatches++ == 0)
								{
									first = $"({x},{y}) want {want} got {got}";
								}
							}
						}
					}
				}
			}
		}
		UnitCheck("hotcorner-zones-equal-poll", mismatches == 0, $"{samples} points on 4 layouts: {mismatches} mismatches {first}");
		// Transition tracking: enter, park, leave, fire-then-leave-then-re-enter produce exactly one report each.
		int[] z = HotCorners.BuildZonesFor(layouts[0], System.Drawing.Rectangle.Union(layouts[0][0], layouts[0][1]), false, true, false, true);
		int last = 0;
		int reports = 0;
		(int, int)[] path = { (500, 500), (3, 3), (2, 2), (1, 4), (500, 500), (0, 500), (0, 600), (1, 700), (300, 700), (0, 0) };
		foreach ((int px, int py) in path)
		{
			int m = HotCorners.ZoneMask(px, py, z);
			if (m != last)
			{
				last = m;
				reports++;
			}
		}
		UnitCheck("hotcorner-transitions", reports == 5, $"{reports} reports for enter corner, park, leave, enter edge, park, leave, enter corner (want 5)");
	}

	// A low-level mouse hook gets the point before it is clipped to the screen: a push past an outer edge (into the
	// top-left corner, against the left edge) must still be inside the zone, and never inside another monitor's.
	private static void UnitHotCornerOuterPush()
	{
		System.Drawing.Rectangle[] two = { new System.Drawing.Rectangle(0, 0, 1920, 1080), new System.Drawing.Rectangle(1920, 0, 1920, 1080) };
		System.Drawing.Rectangle virt = System.Drawing.Rectangle.Union(two[0], two[1]);
		int[] z = HotCorners.BuildZonesFor(two, virt, bottomLeft: true, topLeft: true, rightCorners: true, leftEdge: true);
		(int X, int Y, int Want, string What)[] cases =
		{
			(-12, -7, 2, "push into the top-left corner"),
			(-400, -300, 2, "a fast throw far past the top-left corner"),
			(-30, 500, 16, "push against the left edge"),
			(-30, 20, 0, "left of the screen between the corner and the edge zone"),
			(3850, -20, 4, "push into the top-right corner"),
			(3900, 1200, 8, "push into the bottom-right corner"),
			(-50, 1100, 1, "push into the bottom-left corner"),
			(1919, -20, 0, "past the top edge between the monitors"),
			(1925, 1100, 0, "past the bottom edge between the monitors")
		};
		int bad = 0;
		string first = "";
		foreach ((int x, int y, int want, string what) in cases)
		{
			int got = HotCorners.ZoneMask(x, y, z);
			if (got != want && bad++ == 0)
			{
				first = $"{what} ({x},{y}): want {want} got {got}";
			}
		}
		// A monitor whose left edge is not the desktop's: a point left of it (pre-clip, in the gap above the other monitor)
		// is in no zone; the outer monitor's own edge still is.
		System.Drawing.Rectangle[] offset = { new System.Drawing.Rectangle(0, 0, 1920, 1080), new System.Drawing.Rectangle(-1920, 200, 1920, 1080) };
		int[] z2 = HotCorners.BuildZonesFor(offset, System.Drawing.Rectangle.Union(offset[0], offset[1]), bottomLeft: true, topLeft: true, rightCorners: true, leftEdge: true);
		bool gap = HotCorners.ZoneMask(-5, 100, z2) == 0 && HotCorners.ZoneMask(-1930, 150, z2) == 0 && HotCorners.ZoneMask(-1930, 600, z2) == 16;
		UnitCheck("hotcorner-outer-push", bad == 0 && gap, $"{cases.Length} pushes past outer edges on two side-by-side monitors: {bad} wrong {first}; offset layout gap checks {gap}");
	}

	// The input-hook liveness rules (InputHookLiveness) on scripted ticks.
	private static void UnitHookLiveness()
	{
		InputHookLiveness.Sample S(long now, int x, int mouse, int keys, long idle) => new InputHookLiveness.Sample { Now = now, HaveCursor = true, X = x, Y = 500, MouseEvents = mouse, KeyEvents = keys, InputIdleMs = idle };
		(bool K, bool M) Tick(InputHookLiveness l, InputHookLiveness.Sample s, bool excused = false)
		{
			l.Observe(in s);
			return l.Judge(s.Now, excused, active: true);
		}
		// Moves the hook sees: never.
		InputHookLiveness a = new InputHookLiveness();
		bool okSeen = true;
		for (int i = 0; i < 6; i++)
		{
			(bool k, bool m) = Tick(a, S(100000 + i * 1000, 100 + i * 50, i * 40, 0, 10));
			okSeen &= !k && !m;
		}
		UnitCheck("liveness-moves-seen", okSeen, "the cursor moves and the mouse hook counts events: no re-install");
		// Moves the hook misses: the mouse hook (only) on the second silent tick.
		InputHookLiveness b = new InputHookLiveness();
		Tick(b, S(200000, 100, 7, 3, 10));
		(bool k1, bool m1) = Tick(b, S(201000, 300, 7, 3, 10));
		(bool k2, bool m2) = Tick(b, S(202000, 500, 7, 3, 10));
		UnitCheck("liveness-mouse-dead", !m1 && !k1 && m2 && !k2 && b.SilentMove, $"silent moves: tick 1 mouse={m1}, tick 2 mouse={m2} keyboard={k2} (want false, true, false)");
		// Rate limit: another dead mouse within 30 s waits.
		Tick(b, S(203000, 700, 7, 3, 10));
		(bool _, bool m3) = Tick(b, S(204000, 900, 7, 3, 10));
		(bool _, bool m4) = Tick(b, S(233000, 1100, 7, 3, 10));
		UnitCheck("liveness-rate-limited", !m3 && m4, $"2 s after a re-install: {m3}; 31 s after: {m4}");
		// Keyboard input a remote-desktop or VM client swallows (excused) never re-installs; typing the keyboard hook sees
		// never does; typing nobody sees, with a normal window in front, re-installs the keyboard hook only.
		InputHookLiveness c = new InputHookLiveness();
		bool okKeys = true;
		for (int i = 0; i < 6; i++)
		{
			(bool k, bool m) = Tick(c, S(300000 + i * 1000, 100, 5, 10 + i * 30, 20));
			okKeys &= !k && !m;
		}
		InputHookLiveness d = new InputHookLiveness();
		bool okCapture = true;
		for (int i = 0; i < 6; i++)
		{
			(bool k, bool m) = Tick(d, S(400000 + i * 1000, 100, 5, 10, 20), excused: true);
			okCapture &= !k && !m;
		}
		InputHookLiveness e = new InputHookLiveness();
		Tick(e, S(500000, 100, 5, 10, 20));
		(bool k5, bool m5) = Tick(e, S(501000, 100, 5, 10, 20));
		(bool k6, bool m6) = Tick(e, S(502000, 100, 5, 10, 20));
		UnitCheck("liveness-keyboard", okKeys && okCapture && !k5 && k6 && !m5 && !m6, $"keys seen: none={okKeys}; swallowed by a capture client: none={okCapture}; unseen typing: tick 1 {k5}, tick 2 keyboard={k6} mouse={m6}");
		// A move without input (SetCursorPos) and the first seconds after unlock never count.
		InputHookLiveness f = new InputHookLiveness();
		bool okWarp = true;
		for (int i = 0; i < 6; i++)
		{
			(bool k, bool m) = Tick(f, S(600000 + i * 1000, 100 + i * 100, 5, 10, 90000));
			okWarp &= !k && !m;
		}
		InputHookLiveness g = new InputHookLiveness();
		g.Grace(700000);
		bool okGrace = true;
		for (int i = 1; i <= 4; i++)
		{
			(bool k, bool m) = Tick(g, S(700000 + i * 1000, 100 + i * 100, 5, 10, 10));
			okGrace &= !k && !m;
		}
		(bool _, bool m7) = Tick(g, S(706000, 700, 5, 10, 10));
		(bool _, bool m8) = Tick(g, S(707000, 900, 5, 10, 10));
		UnitCheck("liveness-warp-and-grace", okWarp && okGrace && !m7 && m8, $"cursor warped without input: none={okWarp}; within 5 s of unlock: none={okGrace}; after it: tick 1 {m7}, tick 2 {m8}");
	}

	// Asleep (display off) never asks the input hooks for kicks; input after the display went off means the display-on
	// notification was lost: the next tick wakes the clock.
	private static void UnitShellClockAsleep()
	{
		long idle = 600000L;
		Func<long>? saved = ShellClock.InputIdleOverride;
		ShellClock.InputIdleOverride = () => idle;
		try
		{
			ShellClock clock = new ShellClock(Dispatcher.CurrentDispatcher, watchSession: false);
			int refreshes = 0;
			clock.RefreshBars = () => refreshes++;
			clock.Tick();
			clock.QaDisplayState(off: true);
			bool asleep = clock.CurrentMode == ShellClock.Mode.Asleep && !ShellClock.WantKick;
			int r0 = refreshes;
			clock.Tick();
			clock.Tick();
			bool quiet = clock.CurrentMode == ShellClock.Mode.Asleep && !ShellClock.WantKick && refreshes == r0;
			UnitCheck("clock-asleep-no-kick", asleep && quiet, $"display off: mode={clock.CurrentMode} wantKick={ShellClock.WantKick}; ticks while asleep refreshed {refreshes - r0} times (want 0)");
			Thread.Sleep(650);
			idle = 0L;   // input after the display went off, and no display-on notification
			clock.Tick();
			bool woke = clock.CurrentMode == ShellClock.Mode.Active && !clock.DisplayOff && refreshes > r0;
			UnitCheck("clock-lost-display-on", woke, $"input after display-off: mode={clock.CurrentMode} displayOff={clock.DisplayOff} refreshed={refreshes > r0}");
			ShellClock.WantKick = false;
			clock.Dispose();
		}
		finally
		{
			ShellClock.InputIdleOverride = saved;
		}
	}

	// The process AUMID cache trusts an entry only for the process it was resolved for (same creation time).
	private static void UnitAumidCache()
	{
		HwndSource? src = null;
		try
		{
			src = new HwndSource(new HwndSourceParameters("PerfProbe.Aumid") { ParentWindow = new IntPtr(-3), WindowStyle = 0 });
			nint hwnd = src.Handle;
			uint pid = (uint)Environment.ProcessId;
			TaskbarWindow.ClearAumidCache();
			string? first = TaskbarWindow.QaAumid(hwnd);
			(long Created, string? Aumid)? entry = TaskbarWindow.QaAumidEntry(pid);
			GetProcessTimes(GetCurrentProcess(), out long created, out _, out _, out _);
			bool resolved = entry.HasValue && entry.Value.Created == created && entry.Value.Aumid == first;
			// An entry left by an earlier process with this id (another creation time) is not handed out.
			TaskbarWindow.QaPlantAumid(pid, created - 10_000_000L, "Stale.Package_x!App");
			string? stale = TaskbarWindow.QaAumid(hwnd);
			(long Created, string? Aumid)? healed = TaskbarWindow.QaAumidEntry(pid);
			// The same process's entry is used without asking again.
			TaskbarWindow.QaPlantAumid(pid, created, "Same.Package_x!App");
			string? same = TaskbarWindow.QaAumid(hwnd);
			UnitCheck("aumid-cache-creation-time", resolved && stale == first && healed?.Created == created && same == "Same.Package_x!App",
				$"own process resolved={resolved} ({first ?? "unpackaged"}); entry of another process with this id -> {(stale ?? "null")} (want {first ?? "null"}), re-resolved={healed?.Created == created}; same process -> {same}");
		}
		finally
		{
			TaskbarWindow.ClearAumidCache();
			src?.Dispose();
		}
	}

	private static IEnumerable<int> EdgeSamples(int lo, int hi)
	{
		for (int v = lo; v < lo + 60 && v < hi; v++)
		{
			yield return v;
		}
		for (int v = Math.Max(lo + 60, hi - 60); v < hi; v++)
		{
			yield return v;
		}
		yield return (lo + hi) / 2;
	}

	// HotCorners.Poll's own tests, written out (the specification the zones must match).
	private static int PollMask(System.Drawing.Point p, System.Drawing.Rectangle[] screens, System.Drawing.Rectangle virt, bool bottomLeft, bool topLeft, bool right, bool leftEdge)
	{
		System.Drawing.Rectangle b = screens[0];
		foreach (System.Drawing.Rectangle s in screens)
		{
			if (s.Contains(p))
			{
				b = s;
				break;
			}
		}
		bool lo = b.Left == virt.Left, ro = b.Right == virt.Right, to = b.Top == virt.Top, bo = b.Bottom == virt.Bottom;
		int m = 0;
		if (bottomLeft && lo && bo && p.X <= b.Left + 6 && p.Y >= b.Bottom - 6) m |= 1;
		if (topLeft && lo && to && p.X <= b.Left + 6 && p.Y <= b.Top + 6) m |= 2;
		if (right && ro && to && p.X >= b.Right - 6 && p.Y <= b.Top + 6) m |= 4;
		if (right && ro && bo && p.X >= b.Right - 6 && p.Y >= b.Bottom - 6) m |= 8;
		if (leftEdge && lo && p.X <= b.Left + 4 && p.Y > b.Top + 48 && p.Y < b.Bottom - 48) m |= 16;
		return m;
	}

	// A resume racing a warden pass must never end with the host suspended (fake host, private lock, fake claim).
	private static void UnitSuspensionGuard()
	{
		Random rnd = new Random(1234);
		int bad = 0;
		int passesAfterBlock = 0;
		for (int iter = 0; iter < 300; iter++)
		{
			object gate = new object();
			bool claimed = true;
			SuspensionGuard guard = new SuspensionGuard(gate, () => claimed);
			int host = 0;   // 0 running, 1 suspended
			int passDelay = rnd.Next(0, 3);
			int resumeDelay = rnd.Next(0, 3);
			ManualResetEventSlim go = new ManualResetEventSlim(false);
			Thread pass = new Thread(delegate()
			{
				go.Wait();
				for (int k = 0; k < 3; k++)
				{
					guard.Run(delegate
					{
						if (passDelay > 0) Thread.Sleep(passDelay);
						Volatile.Write(ref host, 1);
						return new List<uint>();
					});
				}
			});
			Thread resume = new Thread(delegate()
			{
				go.Wait();
				if (resumeDelay > 0) Thread.Sleep(resumeDelay);
				guard.Block();
				Volatile.Write(ref host, 0);   // resume after the block: nothing may suspend it again
				claimed = false;
			});
			pass.Start();
			resume.Start();
			go.Set();
			pass.Join();
			resume.Join();
			if (Volatile.Read(ref host) != 0)
			{
				bad++;
			}
			if (guard.Run(() => new List<uint>()) != null)
			{
				passesAfterBlock++;
			}
		}
		UnitCheck("warden-resume-race", bad == 0 && passesAfterBlock == 0, $"300 races: host left suspended {bad} times; passes after block {passesAfterBlock}");
		SuspensionGuard g2 = new SuspensionGuard(new object(), () => true);
		g2.Block();
		g2.Unblock();
		UnitCheck("warden-claim-unblocks", g2.Run(() => new List<uint>()) != null, "a new claim after a resume allows passes again");
	}

	private static void UnitHeartbeat()
	{
		Heartbeat.Beat();
		long now = DateTime.UtcNow.Ticks;
		long section = Heartbeat.ReadSection(Heartbeat.Name);
		bool fresh = section > 0 && Math.Abs(now - section) < TimeSpan.TicksPerSecond * 2;
		UnitCheck("heartbeat-section-roundtrip", fresh, $"section beat {(section > 0 ? ((now - section) / TimeSpan.TicksPerMillisecond) + " ms old" : "missing")} (probe-unique name {Heartbeat.Name})");
		long both = Heartbeat.ReadFreshest(Heartbeat.FilePath, Heartbeat.Name);
		UnitCheck("heartbeat-freshest", both >= section && both > 0, $"freshest {both} >= section {section}");
		long missing = Heartbeat.ReadSection("Local\\Win81Layer.Heartbeat.missing." + Environment.ProcessId);
		UnitCheck("heartbeat-missing-section", missing == 0, "a section nobody holds reads 0 (the file decides)");
		bool fileOk = File.Exists(Heartbeat.FilePath) && Heartbeat.ReadFile(Heartbeat.FilePath) > 0;
		UnitCheck("heartbeat-file-fallback", fileOk, "first beat also wrote the probe's own heartbeat file");
	}

	private static void UnitWardenDryRun()
	{
		(int hosts, int running, int suspended, double ms) = NativeHostWarden.DryRun();
		(int hosts2, int running2, int suspended2, double ms2) = NativeHostWarden.DryRun();
		long alloc0 = GC.GetAllocatedBytesForCurrentThread();
		NativeHostWarden.DryRun();
		long alloc = GC.GetAllocatedBytesForCurrentThread() - alloc0;
		UnitCheck("warden-dry-run", hosts2 >= 0 && ms2 < 50.0, $"hosts={hosts2} running={running2} suspended={suspended2}; {ms2:0.00} ms per snapshot (first {ms:0.00} ms); {alloc} bytes allocated per pass");
		Report["wardenDryRun"] = new { Hosts = hosts2, Running = running2, Suspended = suspended2, Ms = Math.Round(ms2, 3), FirstMs = Math.Round(ms, 3), AllocatedBytes = alloc };
	}

	// The legacy native-host check without acting: three by-name enumerations plus each host's thread list (both are
	// snapshot reads; no handle is opened to any host).
	private static int LegacyHostProbeDryRun()
	{
		int suspended = 0;
		foreach (string name in new[] { "StartMenuExperienceHost", "SearchApp", "SearchHost" })
		{
			foreach (Process p in Process.GetProcessesByName(name))
			{
				try
				{
					using Process q = Process.GetProcessById(p.Id);
					foreach (ProcessThread t in q.Threads)
					{
						try
						{
							if (t.ThreadState == System.Diagnostics.ThreadState.Wait && t.WaitReason == ThreadWaitReason.Suspended)
							{
								suspended++;
							}
						}
						catch
						{
						}
					}
				}
				catch
				{
				}
				finally
				{
					p.Dispose();
				}
			}
		}
		return suspended;
	}
}
