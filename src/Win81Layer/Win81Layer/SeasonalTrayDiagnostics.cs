#nullable enable

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace Win81Layer;

// --traystatetest <outDir> [--seasonal-root <dir>] [--tray-samples <dir>]: offscreen QA of the state-aware seasonal
// tray icons. Read-only by construction: settings writes throw (ReadOnlyDiagnostics), the season comes from a
// diagnostics override, the learned states are kept in <outDir>\store, and every file it writes is inside outDir.
// Synthetic chat-app and team-app icon sequences (idle, unread dot, mention count, call, speaking ring, muted slash,
// away, error, blinking, starting with a badge, DPI variants, a first start during a call, start-up votes, rebuilt
// bars, stand-in images, an animated icon, a damaged store) are fed through TaskbarWindow.TrayImageFor with a
// simulated clock, each decision is checked, and before/after strips are drawn at 16, 18, 20, 22 and 24 px (Small,
// Medium and Large at 100 %, and 125 % / 150 %) on a dark and a light bar. --seasonal-root draws with real seasonal
// pictures; --tray-samples runs a folder of an app's own tray images (tray.png idle, tray-unread.png, tray-*.png
// states, optional @<scale>x sizes) read only.
internal static class SeasonalTrayDiagnostics
{
	private static readonly List<(string Name, bool Pass, string Detail)> Checks = new List<(string, bool, string)>();

	private static readonly Dictionary<string, object> Metrics = new Dictionary<string, object>();

	private static readonly string DataRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Win81Layer");

	private static readonly int[] Sizes = new int[5] { 16, 18, 20, 22, 24 };

	private static readonly Color DarkBar = Color.FromRgb(0x1F, 0x1F, 0x1F);

	private static readonly Color LightBar = Color.FromRgb(0xEE, 0xEE, 0xEE);

	private static long _now = new DateTime(2026, 10, 31, 17, 0, 0, DateTimeKind.Utc).Ticks;

	private static bool _fresh;

	// The simulated process of each icon (a new one per app start; a rebuilt bar's icon keeps its app's).
	private static readonly Dictionary<TrayAppIcon, string> Procs = new Dictionary<TrayAppIcon, string>();

	private static int _procSeq;

	private static AppSettings _real = new AppSettings();

	private static string _outDir = "";

	public static void Begin(Application app, string[] args)
	{
		app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
		int idx = Array.IndexOf(args, "--traystatetest");
		string outDir = (idx >= 0 && idx + 1 < args.Length && !args[idx + 1].StartsWith("--", StringComparison.Ordinal))
			? args[idx + 1]
			: Path.Combine(DataRoot, "qa", "tray-state");
		string? realRoot = Arg(args, "--seasonal-root");
		string? samples = Arg(args, "--tray-samples");
		app.Dispatcher.BeginInvoke((Action)delegate
		{
			int code = 1;
			try
			{
				code = Run(outDir, realRoot, samples) ? 0 : 1;
			}
			catch (Exception ex)
			{
				try
				{
					Directory.CreateDirectory(outDir);
					File.WriteAllText(Path.Combine(outDir, "traystatetest-error.txt"), ex.ToString());
				}
				catch
				{
				}
			}
			app.Shutdown(code);
		}, DispatcherPriority.Loaded);
	}

	private static string? Arg(string[] args, string name)
	{
		int i = Array.IndexOf(args, name);
		return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
	}

	private static bool Run(string outDir, string? realRoot, string? samples)
	{
		_outDir = outDir;
		Directory.CreateDirectory(outDir);
		foreach (string old in Directory.GetFiles(outDir, "traystate-*.png"))
		{
			File.Delete(old);
		}
		string before = OutsideStamp();
		_real = SettingsStore.Load();
		List<string> log = new List<string>();
		SeasonalAppIcons.QaLog = log;
		string store = Path.Combine(outDir, "store", "seasonal-tray.json");
		Directory.CreateDirectory(Path.GetDirectoryName(store)!);
		if (File.Exists(store))
		{
			File.Delete(store);
		}
		SeasonalTrayIcons.QaClock = () => _now;
		SeasonalTrayIcons.QaStorePath = store;
		SeasonalTrayIcons.QaFreshProcess = vm => _fresh && Procs.TryGetValue(vm, out string? proc) ? proc : null;
		SeasonalTrayIcons.QaSyncWrites = true;
		try
		{
			string fixture = BuildFixture(outDir);
			SeasonalAppIcons.DiagnosticsRoot = realRoot ?? fixture;
			Metrics["Art"] = realRoot != null ? "real (--seasonal-root)" : "generated fixture";
			Use(SeasonalTheme.Halloween);
			Check("art-present", SeasonalAppIcons.Frame("discord", 16) != null && SeasonalAppIcons.Frame("slack", 16) != null, "discord and slack pictures at 16 px");

			ChatSequence();
			TeamSequence();
			LongCall();
			StartsWithBadge(store);
			MidCallStart();
			DpiVariants(store);
			SeasonsAndModes();
			ModeSwitchEndsBlinking();
			Votes();
			EpisodeContinuity(store);
			Substitutes();
			Animation();
			StoreRobustness(store);
			CostChecks();
			if (samples != null && Directory.Exists(samples))
			{
				RealSamples(samples);
			}
			RenderPcSettings(Path.Combine(outDir, "traystate-pcsettings.png"));
			if (realRoot != null)
			{
				// The same chat sequence over the generated pumpkin art, for comparison.
				SeasonalAppIcons.DiagnosticsRoot = fixture;
				Use(SeasonalTheme.Halloween);
				Forget();
				ChatSequence(suffix: "-fixture-art", check: false);
			}
		}
		catch (Exception ex)
		{
			Check("harness-completed", false, ex.ToString());
		}
		finally
		{
			SettingsStore.DiagnosticsOverride = null;
			SeasonalAppIcons.DiagnosticsRoot = null;
			SeasonalTrayIcons.QaReset();
			SeasonalTrayIcons.QaClock = null;
			SeasonalTrayIcons.QaStorePath = null;
			SeasonalTrayIcons.QaFreshProcess = null;
			SeasonalTrayIcons.QaSyncWrites = false;
			try
			{
				SeasonalAppIcons.Reconcile("qa-end");
				Pump();
			}
			catch
			{
			}
			SeasonalAppIcons.QaLog = null;
		}
		string after = OutsideStamp();
		Check("readonly-data-folder", before == after && !File.Exists(Path.Combine(DataRoot, "seasonal-tray.json")), before == after ? "settings, state and data folder unchanged; no seasonal-tray.json" : $"before={before} after={after}");
		Metrics["Renders"] = Directory.GetFiles(outDir, "traystate-*.png").Select(Path.GetFileName).OrderBy(n => n).ToArray();

		bool passed = Checks.All(c => c.Pass);
		StringBuilder txt = new StringBuilder();
		foreach ((string name, bool pass, string detail) in Checks)
		{
			txt.AppendLine((pass ? "PASS " : "FAIL ") + name + (detail.Length > 0 ? ": " + detail : ""));
		}
		File.WriteAllText(Path.Combine(outDir, "traystatetest-results.txt"), txt.ToString());
		var report = new
		{
			SchemaVersion = 1,
			GeneratedUtc = DateTime.UtcNow,
			Passed = passed,
			Checks = Checks.Select(c => new { c.Name, c.Pass, c.Detail }).ToArray(),
			Metrics
		};
		File.WriteAllText(Path.Combine(outDir, "traystatetest-report.json"), JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
		Logger.Log($"TRAY-STATE-QA: passed={passed} checks={Checks.Count} failed={Checks.Count(c => !c.Pass)} -> {outDir}");
		return passed;
	}

	private static void Check(string name, bool pass, string detail = "")
	{
		Checks.Add((name, pass, detail));
	}

	// Nothing learned: memory and the QA store are cleared.
	private static void Forget()
	{
		SeasonalTrayIcons.QaReset();
		string? store = SeasonalTrayIcons.QaStorePath;
		if (store != null && File.Exists(store))
		{
			File.Delete(store);
		}
	}

	// A launcher restart: memory is cleared, what was saved is read again on first use.
	private static void Restart()
	{
		SeasonalTrayIcons.QaReset();
	}

	// ===================================================================== settings

	private static void Use(string season, bool icons = true, string tray = "live")
	{
		AppSettings s = _real.Clone();
		s.SeasonalActive = season == SeasonalTheme.Normal ? "" : season;
		s.SeasonalAppIcons = icons;
		s.SeasonalTrayIcons = tray;
		s.TaskbarSize = "Medium";
		SettingsStore.DiagnosticsOverride = s;
		SeasonalAppIcons.Reconcile("qa");
		Pump();
	}

	private static void Pump()
	{
		for (int i = 0; i < 3; i++)
		{
			Dispatcher.CurrentDispatcher.Invoke(DispatcherPriority.ContextIdle, new Action(delegate
			{
			}));
		}
	}

	// ===================================================================== sequences

	private const SeasonalTrayLook Pic = SeasonalTrayLook.Seasonal;

	private const SeasonalTrayLook Badge = SeasonalTrayLook.SeasonalBadge;

	private const SeasonalTrayLook Own = SeasonalTrayLook.Original;

	// One step: wait, then the app sets this image (or, with no image, the regular tray scan runs: it draws the icon
	// again only when NeedsSettle says so; a blinking state went quiet or a vote or idle change came due). Sub: the
	// image is a stand-in, not the app's own tray image. Quiet: a scan that is not expected to draw the icon again.
	private sealed record Step(string Name, ImageSource? Image, SeasonalTrayLook Want, double After, bool Fresh = false, bool Sub = false, bool Quiet = false);

	private sealed class Cell
	{
		public required string Name;

		public required ImageSource? Raw;

		public required SeasonalTrayLook Want;

		public required SeasonalTrayLook Got;

		public required ImageSource?[] Shown;

		// A step with no new image that expects the regular tray scan to draw the icon again (NeedsSettle).
		public bool Settle;

		public bool SettleNeeded;
	}

	private sealed class Player
	{
		public readonly TrayAppIcon Vm;

		public readonly string Proc;

		public readonly List<Cell> Cells = new List<Cell>();

		private ImageSource?[]? _last;

		// A new icon; proc: its app's process when it is the same app instance (a rebuilt bar), else a new process.
		public Player(string stableKey, string? proc = null)
		{
			Vm = Icon(stableKey, proc);
			Proc = Procs[Vm];
		}

		public Cell Play(Step s)
		{
			_now += (long)(s.After * TimeSpan.TicksPerSecond);
			_fresh = s.Fresh;
			ImageSource?[] shown = new ImageSource?[Sizes.Length];
			bool needed = false;
			bool draw = true;
			if (s.Image != null)
			{
				Vm.RawImage = s.Image;
				Vm.RawSubstitute = s.Sub;
			}
			else
			{
				needed = SeasonalTrayIcons.NeedsSettle(Vm);
				draw = needed || _last == null;
			}
			for (int i = 0; i < Sizes.Length; i++)
			{
				shown[i] = draw ? TaskbarWindow.TrayImageFor(Vm, Sizes[i]) : _last![i];
			}
			_last = shown;
			Vm.Image = shown[0];
			Cell c = new Cell
			{
				Name = s.Name,
				Raw = Vm.RawImage,
				Want = s.Want,
				Got = Vm.SeasonalTray?.Look ?? (Vm.SeasonalKey != null ? Pic : Own),
				Shown = shown,
				Settle = s.Image == null && !s.Quiet,
				SettleNeeded = needed
			};
			Cells.Add(c);
			return c;
		}

		public void PlayAll(IEnumerable<Step> steps)
		{
			foreach (Step s in steps)
			{
				Play(s);
			}
		}
	}

	// A tray icon of the given app with its own simulated process.
	private static TrayAppIcon Icon(string stableKey, string? proc = null)
	{
		TrayAppIcon vm = new TrayAppIcon { StableKey = stableKey };
		Procs[vm] = proc ?? "qa-" + (++_procSeq).ToString(CultureInfo.InvariantCulture);
		return vm;
	}

	private static string Name(SeasonalTrayLook l)
	{
		return l switch
		{
			SeasonalTrayLook.Seasonal => "seasonal",
			SeasonalTrayLook.SeasonalBadge => "seasonal+badge",
			_ => "original"
		};
	}

	private static void CheckCells(string name, List<Cell> cells)
	{
		List<string> bad = cells.Where(c => c.Got != c.Want).Select(c => $"{c.Name}: got {Name(c.Got)}, want {Name(c.Want)}").ToList();
		bad.AddRange(cells.Where(c => c.Settle && !c.SettleNeeded).Select(c => $"{c.Name}: the tray scan would not have redrawn it"));
		Check(name, bad.Count == 0, bad.Count == 0 ? string.Join(", ", cells.Select(c => c.Name + "=" + Name(c.Got))) : string.Join("; ", bad));
	}

	// A chat app: idle, unread dot, mention count, call, speaking ring, muted, blinking unread, back to idle.
	private static void ChatSequence(string suffix = "", bool check = true)
	{
		Forget();
		Player p = new Player("Discord");
		int n = 16;
		ImageSource idle = Chat(n, ChatState.Idle);
		ImageSource unread = Chat(n, ChatState.Unread);
		ImageSource mention = Chat(n, ChatState.Mention);
		ImageSource call = Chat(n, ChatState.InCall);
		ImageSource speak = Chat(n, ChatState.Speaking);
		ImageSource muted = Chat(n, ChatState.Muted);
		ImageSource error = Chat(n, ChatState.Error);
		List<Step> steps = new List<Step>
		{
			new Step("idle", idle, Pic, 0, Fresh: true),
			new Step("unread", unread, Badge, 60),
			new Step("read", idle, Pic, 120),
			new Step("mention", mention, Badge, 300),
			new Step("idle", idle, Pic, 60),
			new Step("call", call, Own, 600),
			new Step("speak", speak, Own, 20),
			new Step("quiet", call, Own, 2),
			new Step("speak", speak, Own, 0.4),
			new Step("quiet", call, Own, 0.4),
			new Step("speak", speak, Own, 0.5),
			new Step("quiet", call, Own, 0.6),
			new Step("settled", null, Own, 3),
			new Step("muted", muted, Own, 120),
			new Step("left", idle, Pic, 300),
			new Step("blink1", unread, Badge, 30),
			new Step("blink0", idle, Pic, 0.5),
			new Step("blink1", unread, Badge, 0.5),
			new Step("blink0", idle, Pic, 0.5),
			new Step("blink1", unread, Badge, 0.5),
			new Step("settled", null, Badge, 3),
			new Step("error", error, Own, 120),
			new Step("online", idle, Pic, 30)
		};
		p.PlayAll(steps);
		if (check)
		{
			CheckCells("chat-decisions", p.Cells);
			CheckBadgeReadable("chat-badge-readable", p.Cells.First(c => c.Name == "unread"), "discord", 0.8, 0.2, 0.2, Color.FromRgb(0xF0, 0x47, 0x47));
			CheckBadgeReadable("chat-mention-readable", p.Cells.First(c => c.Name == "mention"), "discord", 0.74, 0.74, 0.26, Color.FromRgb(0xF0, 0x47, 0x47));
			// The original look is exactly the app's own icon fitted to the box (what Normal shows).
			Cell callCell = p.Cells.First(c => c.Name == "call");
			Check("chat-original-exact", Sizes.Select((px, i) => SamePixels(callCell.Shown[i], TrayIconFit.Fit(call, px))).All(b => b), "call frames equal TrayIconFit.Fit of the app's image at " + string.Join("/", Sizes));
			Cell idleCell = p.Cells[0];
			Check("chat-seasonal-exact", Sizes.Select((px, i) => ReferenceEquals(idleCell.Shown[i], SeasonalAppIcons.Frame("discord", px))).All(b => b), "idle frames are the seasonal frames themselves");
			// Blinking a badge on and off is a clean blink on the picture: the idle frames are the picture.
			Check("chat-blink-no-mess", p.Cells.Where(c => c.Name == "blink0").All(c => ReferenceEquals(c.Shown[0], SeasonalAppIcons.Frame("discord", 16))), "badge blink alternates picture / picture+badge");
		}
		RenderStrip("chat" + suffix, "Chat app (synthetic): idle, unread, mention, call, speaking, muted, blinking, error", p.Cells, "discord");
	}

	// A team app: idle, unread dot, mention, away (recoloured), error, attention flash (blinking whole icon).
	private static void TeamSequence()
	{
		Forget();
		Player p = new Player("slack");
		int n = 16;
		ImageSource idle = Team(n, TeamState.Idle);
		ImageSource unread = Team(n, TeamState.Unread);
		ImageSource mention = Team(n, TeamState.Mention);
		ImageSource away = Team(n, TeamState.Away);
		ImageSource error = Team(n, TeamState.Error);
		ImageSource flash = Team(n, TeamState.Flash);
		List<Step> steps = new List<Step>
		{
			new Step("idle", idle, Pic, 0, Fresh: true),
			new Step("unread", unread, Badge, 45),
			new Step("mention", mention, Badge, 30),
			new Step("read", idle, Pic, 90),
			new Step("away", away, Own, 600),
			new Step("back", idle, Pic, 300),
			new Step("flash", flash, Own, 30),
			new Step("idle", idle, Own, 0.6),
			new Step("flash", flash, Own, 0.6),
			new Step("idle", idle, Own, 0.6),
			new Step("flash", flash, Own, 0.6),
			new Step("idle", idle, Own, 0.6),
			new Step("settled", null, Pic, 3),
			new Step("error", error, Own, 200),
			new Step("online", idle, Pic, 40),
			new Step("unread", unread, Badge, 40)
		};
		p.PlayAll(steps);
		CheckCells("team-decisions", p.Cells);
		CheckBadgeReadable("team-unread-readable", p.Cells.First(c => c.Name == "unread"), "slack", 0.82, 0.82, 0.18, Color.FromRgb(0x1D, 0x9B, 0xD1));
		CheckBadgeReadable("team-mention-readable", p.Cells.First(c => c.Name == "mention"), "slack", 0.74, 0.74, 0.26, Color.FromRgb(0xE0, 0x1E, 0x5A));
		// While the whole icon flashes, every frame is the app's own: never the picture in between.
		List<Cell> flashCells = p.Cells.Skip(6).Take(6).ToList();
		Check("team-flash-no-mess", flashCells.All(c => !ReferenceEquals(c.Shown[0], SeasonalAppIcons.Frame("slack", 16))), "6 alternating frames, all the app's own");
		RenderStrip("team", "Team app (synthetic): idle, unread, mention, away, flashing, error", p.Cells, "slack");
	}

	// Three hours in a call while talking (thousands of state changes) ten minutes after the icon was first seen do not
	// make the call the idle state: with the app just started (a start-up vote) and with the launcher started while
	// the app was already running (only the first image seen).
	private static void LongCall()
	{
		foreach (bool fresh in new bool[2] { true, false })
		{
			Forget();
			Player p = new Player("Discord");
			ImageSource idle = Chat(16, ChatState.Idle);
			ImageSource call = Chat(16, ChatState.InCall);
			ImageSource speak = Chat(16, ChatState.Speaking);
			p.Play(new Step("idle", idle, Pic, 0, Fresh: fresh));
			int idleId = p.Vm.SeasonalTray!.ClusterId;
			p.Play(new Step("call", call, Own, 600));
			Stopwatch sw = Stopwatch.StartNew();
			int frames = 0;
			int wrong = 0;
			long end = _now + 3 * TimeSpan.TicksPerHour;
			while (_now < end)
			{
				// Talking: the speaking ring for 1.5 s, quiet for 3 s, now and then a long silence.
				p.Vm.RawImage = speak;
				_now += 1500 * TimeSpan.TicksPerMillisecond;
				TaskbarWindow.TrayImageFor(p.Vm, 20);
				wrong += p.Vm.SeasonalTray.Look == Own ? 0 : 1;
				p.Vm.RawImage = call;
				_now += (frames % 40 == 0 ? 120 : 3) * TimeSpan.TicksPerSecond;
				TaskbarWindow.TrayImageFor(p.Vm, 20);
				wrong += p.Vm.SeasonalTray.Look == Own ? 0 : 1;
				frames += 2;
			}
			sw.Stop();
			var model = SeasonalTrayIcons.QaModel("discord");
			Cell left = p.Play(new Step("left", idle, Pic, 10));
			string label = fresh ? "long-call-keeps-idle" : "long-call-keeps-idle-no-vote";
			Check(label, model.Idle == idleId && left.Got == Pic && wrong == 0, $"{frames} call frames over 3 h; idle state {model.Idle} (want {idleId}); call frames not the app's own: {wrong}; after the call: {Name(left.Got)}");
			Metrics[label + "-MsPerFrame"] = Math.Round(sw.Elapsed.TotalMilliseconds / frames, 4);
		}
	}

	// The app starts with a badge. With what an earlier session learned: the badge is shown on the picture at once.
	// With nothing learned: the first image is taken as idle until the plain icon has been seen once.
	private static void StartsWithBadge(string store)
	{
		// An earlier session learns the plain icon and saves it.
		Forget();
		Player a = new Player("Discord");
		a.Play(new Step("idle", Chat(16, ChatState.Idle), Pic, 0, Fresh: true));
		a.Play(new Step("unread", Chat(16, ChatState.Unread), Badge, 600));
		a.Play(new Step("idle", Chat(16, ChatState.Idle), Pic, 600));
		SeasonalTrayIcons.SaveNow(_now, background: false);
		long size = File.Exists(store) ? new FileInfo(store).Length : -1;
		Metrics["StoreBytes"] = size;
		Check("store-written", size > 0 && size < 64 * 1024, $"{size} bytes in outDir\\store");

		// Restart: a new session reads the store; the app starts with an unread dot and a mention.
		Restart();
		_now += TimeSpan.TicksPerHour * 10;
		Player b = new Player("Discord");
		b.Play(new Step("start+dot", Chat(16, ChatState.Unread), Badge, 0, Fresh: true));
		b.Play(new Step("mention", Chat(16, ChatState.Mention), Badge, 30));
		b.Play(new Step("read", Chat(16, ChatState.Idle), Pic, 60));
		CheckCells("start-with-badge-learned", b.Cells);

		// No history at all (a team app seen for the first time, starting with a mention).
		Restart();
		Player c = new Player("slack");
		c.Play(new Step("start+mention", Team(16, TeamState.Mention), Pic, 0, Fresh: true));
		c.Play(new Step("read", Team(16, TeamState.Idle), Pic, 120));
		c.Play(new Step("mention", Team(16, TeamState.Mention), Badge, 120));
		c.Play(new Step("read", Team(16, TeamState.Idle), Pic, 60));
		CheckCells("start-with-badge-unlearned", c.Cells);

		// Restart with a call in progress after a session learned the plain icon: the call shows as the app's own.
		Restart();
		Player d = new Player("Discord");
		d.Play(new Step("start in call", Chat(16, ChatState.InCall), Own, 0, Fresh: false));
		d.Play(new Step("idle", Chat(16, ChatState.Idle), Pic, 900));
		CheckCells("restart-in-call-learned", d.Cells);
		List<Cell> all = b.Cells.Concat(c.Cells).Concat(d.Cells).ToList();
		RenderStrip("restart", "Restarts: start with a badge (learned / first time), start in a call (learned)", all, null);
	}

	// The launcher starts (for the first time ever, nothing learned, no start-up vote) while a call is in progress: the
	// first image seen is a call image and is taken as idle (shown as the picture: nothing else is known yet). Once the
	// speaking indicator shows it is a frame of a whole-icon blinking state, it is no longer trusted and is shown as the
	// app's own icon for 15 minutes after each blink (a call without any speaking for longer shows the picture until
	// the next blink). After the call the plain icon takes over as soon as it has been shown longer than the call (the
	// tray scan picks that up, no new image needed); later calls never take over from it.
	private static void MidCallStart()
	{
		Forget();
		Player p = new Player("Discord");
		ImageSource idle = Chat(16, ChatState.Idle);
		ImageSource call = Chat(16, ChatState.InCall);
		ImageSource speak = Chat(16, ChatState.Speaking);
		ImageSource unread = Chat(16, ChatState.Unread);
		p.Play(new Step("call (first)", call, Pic, 0));
		p.Play(new Step("speak", speak, Own, 30));
		p.Play(new Step("quiet", call, Own, 0.5));
		p.Play(new Step("speak", speak, Own, 0.5));
		p.Play(new Step("quiet", call, Own, 0.5));
		p.Play(new Step("settled", null, Own, 3));
		var m = SeasonalTrayIcons.QaModel("discord");
		bool revoked = !m.SeedTrusted;
		p.Play(new Step("silent 15 min", null, Pic, 900));
		p.Play(new Step("speak", speak, Own, 300));
		p.Play(new Step("quiet", call, Own, 1));
		p.Play(new Step("settled", null, Own, 3));
		p.Play(new Step("left", idle, Own, 600));
		p.Play(new Step("scan", null, Own, 600, Quiet: true));
		p.Play(new Step("plain > call", null, Pic, 1260));
		p.Play(new Step("unread", unread, Badge, 600));
		p.Play(new Step("read", idle, Pic, 60));
		p.Play(new Step("call 2", call, Own, 600));
		p.Play(new Step("speak", speak, Own, 10));
		p.Play(new Step("quiet", call, Own, 0.5));
		p.Play(new Step("speak", speak, Own, 0.5));
		p.Play(new Step("quiet", call, Own, 0.5));
		p.Play(new Step("settled", null, Own, 3));
		p.Play(new Step("scan", null, Own, 3600, Quiet: true));
		p.Play(new Step("left", idle, Pic, 1800));
		CheckCells("mid-call-first-start", p.Cells);
		Check("mid-call-seed-revoked", revoked, "the call image stops being trusted as idle once it blinks with the speaking image");
		var after = SeasonalTrayIcons.QaModel("discord");
		Check("mid-call-learns-plain", after.Idle == SeasonalTrayIcons.QaState("discord", idle).Id && after.Idle != 0, $"idle state {after.Idle}, plain icon's state {SeasonalTrayIcons.QaState("discord", idle).Id}; a second call of 90 minutes did not take over");
		RenderStrip("midcall", "First start ever during a call: the call shows as the app's own after its first blink; the plain icon takes over after the call", p.Cells, "discord");
	}

	// The same states drawn at 20 and 24 px (125 % and 150 %), learned at that size, and a DPI change after a session
	// that learned the 16 px icon.
	private static void DpiVariants(string store)
	{
		List<Cell> all = new List<Cell>();
		foreach (int n in new int[2] { 20, 24 })
		{
			Forget();
			File.Delete(store);
			Player p = new Player("Discord");
			p.PlayAll(new Step[6]
			{
				new Step($"idle@{n}", Chat(n, ChatState.Idle), Pic, 0, Fresh: true),
				new Step($"unread@{n}", Chat(n, ChatState.Unread), Badge, 60),
				new Step($"mention@{n}", Chat(n, ChatState.Mention), Badge, 60),
				new Step($"call@{n}", Chat(n, ChatState.InCall), Own, 60),
				new Step($"muted@{n}", Chat(n, ChatState.Muted), Own, 60),
				new Step($"idle@{n}", Chat(n, ChatState.Idle), Pic, 60)
			});
			CheckCells($"dpi-{n}-decisions", p.Cells);
			Player t = new Player("slack");
			t.PlayAll(new Step[4]
			{
				new Step($"team@{n}", Team(n, TeamState.Idle), Pic, 0, Fresh: true),
				new Step($"unread@{n}", Team(n, TeamState.Unread), Badge, 60),
				new Step($"away@{n}", Team(n, TeamState.Away), Own, 60),
				new Step($"team@{n}", Team(n, TeamState.Idle), Pic, 60)
			});
			CheckCells($"dpi-{n}-team-decisions", t.Cells);
			all.AddRange(p.Cells);
			all.AddRange(t.Cells);
		}
		// Learned at 16 px and saved; after a restart at 150 % the 24 px images are the same states.
		Forget();
		File.Delete(store);
		Player a = new Player("Discord");
		a.Play(new Step("idle@16", Chat(16, ChatState.Idle), Pic, 0, Fresh: true));
		a.Play(new Step("call@16", Chat(16, ChatState.InCall), Own, 600));
		a.Play(new Step("idle@16", Chat(16, ChatState.Idle), Pic, 600));
		SeasonalTrayIcons.SaveNow(_now, background: false);
		Restart();
		Player b = new Player("Discord");
		b.PlayAll(new Step[5]
		{
			new Step("unread@24 (start)", Chat(24, ChatState.Unread), Badge, 3600, Fresh: true),
			new Step("idle@24", Chat(24, ChatState.Idle), Pic, 60),
			new Step("mention@24", Chat(24, ChatState.Mention), Badge, 60),
			new Step("call@24", Chat(24, ChatState.InCall), Own, 60),
			new Step("idle@24", Chat(24, ChatState.Idle), Pic, 60)
		});
		CheckCells("dpi-change-after-restart", b.Cells);
		var m = SeasonalTrayIcons.QaModel("discord");
		Check("dpi-variants-one-state", m.States <= 4, $"{m.States} states learned for idle, unread, mention and call across 16 and 24 px");
		all.AddRange(b.Cells);
		RenderStrip("dpi", "20 px and 24 px icons; DPI change after restart (learned at 16 px)", all, null);
		Forget();
		File.Delete(store);
	}

	// Normal and a season without a picture are untouched, "always the app's own icon" shows the app's icons, and
	// none of them learns anything.
	private static void SeasonsAndModes()
	{
		Forget();
		ImageSource mention = Chat(16, ChatState.Mention);
		List<string> bad = new List<string>();
		foreach ((string season, string mode) in new (string, string)[3] { (SeasonalTheme.Normal, "live"), (SeasonalTheme.Christmas, "live"), (SeasonalTheme.Halloween, "original") })
		{
			Use(season, tray: mode);
			TrayAppIcon vm = Icon("Discord");
			vm.RawImage = mention;
			foreach (int px in Sizes)
			{
				ImageSource? img = TaskbarWindow.TrayImageFor(vm, px);
				if (!SamePixels(img, TrayIconFit.Fit(mention, px)) || vm.SeasonalKey != null || vm.SeasonalTray != null)
				{
					bad.Add($"{season}/{mode}@{px}");
				}
			}
		}
		Check("normal-christmas-original-untouched", bad.Count == 0 && SeasonalTrayIcons.QaModel("discord").States == 0, bad.Count == 0 ? "Normal, Christmas (no picture) and 'original': exactly the app's icon, nothing learned" : string.Join(", ", bad));
		// Switching to "original" while a badge composite shows: the app's own icon, and back.
		Use(SeasonalTheme.Halloween);
		TrayAppIcon v2 = Icon("Discord");
		v2.RawImage = Chat(16, ChatState.Idle);
		_fresh = true;
		TaskbarWindow.TrayImageFor(v2, 16);
		_fresh = false;
		_now += TimeSpan.TicksPerMinute;
		v2.RawImage = mention;
		ImageSource? live = TaskbarWindow.TrayImageFor(v2, 16);
		Use(SeasonalTheme.Halloween, tray: "original");
		ImageSource? orig = TaskbarWindow.TrayImageFor(v2, 16);
		Use(SeasonalTheme.Halloween);
		ImageSource? back = TaskbarWindow.TrayImageFor(v2, 16);
		Check("mode-switch", v2.SeasonalTray!.Look == Badge && SamePixels(orig, TrayIconFit.Fit(mention, 16)) && SamePixels(back, live) && !SamePixels(live, orig), "live badge -> original -> live badge again");
	}

	// Work only on a new image: drawing the same image again (a size change, a refresh) observes nothing, an image sent
	// again under a new handle costs one fingerprint and no change, and the per-change cost stays small.
	private static void CostChecks()
	{
		Forget();
		Use(SeasonalTheme.Halloween);
		TrayAppIcon vm = Icon("Discord");
		ImageSource idle = Chat(16, ChatState.Idle);
		ImageSource unread = Chat(16, ChatState.Unread);
		ImageSource call = Chat(16, ChatState.InCall);
		_fresh = true;
		vm.RawImage = idle;
		TaskbarWindow.TrayImageFor(vm, 18);
		_fresh = false;
		int states = SeasonalTrayIcons.QaModel("discord").States;
		long since = vm.SeasonalTray!.Since;
		Stopwatch redraw = Stopwatch.StartNew();
		for (int i = 0; i < 1000; i++)
		{
			TaskbarWindow.TrayImageFor(vm, 18);
		}
		redraw.Stop();
		// The same pixels under a new image object (a new HICON): no new episode.
		_now += TimeSpan.TicksPerSecond * 30;
		vm.RawImage = Chat(16, ChatState.Idle);
		TaskbarWindow.TrayImageFor(vm, 18);
		bool sameEpisode = vm.SeasonalTray.Since == since && SeasonalTrayIcons.QaModel("discord").States == states;
		// The start-up vote came due during those 30 s: cast by that same draw, so nothing is left for the tray scan.
		bool voted = SeasonalTrayIcons.QaModel("discord").Votes == 1;
		Check("cost-redraw-no-observe", sameEpisode && voted && !SeasonalTrayIcons.NeedsSettle(vm), $"1000 redraws {redraw.Elapsed.TotalMilliseconds:F2} ms; resent idle = same episode; start-up vote cast: {voted}");
		// Per change: fingerprint, classify and compose for alternating images at 18 px. The store is written off the
		// calling thread here, as in the shell.
		ImageSource[] cycle = new ImageSource[3] { unread, idle, call };
		List<double> times = new List<double>();
		SeasonalTrayIcons.QaSyncWrites = false;
		for (int warm = 0; warm < 30; warm++)
		{
			_now += TimeSpan.TicksPerSecond * 10;
			vm.RawImage = cycle[warm % 3];
			TaskbarWindow.TrayImageFor(vm, 18);
		}
		for (int i = 0; i < 300; i++)
		{
			_now += TimeSpan.TicksPerSecond * 10;
			// New objects each time, like new HICONs, so every change is fingerprinted.
			BitmapSource b = (BitmapSource)cycle[i % 3];
			vm.RawImage = BitmapSource.Create(b.PixelWidth, b.PixelHeight, 96, 96, PixelFormats.Pbgra32, null, Bytes(b), b.PixelWidth * 4);
			Stopwatch one = Stopwatch.StartNew();
			TaskbarWindow.TrayImageFor(vm, 18);
			one.Stop();
			times.Add(one.Elapsed.TotalMilliseconds);
		}
		SeasonalTrayIcons.QaWaitWrites();
		SeasonalTrayIcons.QaSyncWrites = true;
		times.Sort();
		double median = times[times.Count / 2];
		double p99 = times[(int)(times.Count * 0.99)];
		Metrics["ChangeMsMedian"] = Math.Round(median, 3);
		Metrics["ChangeMsP99"] = Math.Round(p99, 3);
		Check("cost-per-change", median < 2.0 && p99 < 8.0, $"median {median:F3} ms, p99 {p99:F3} ms per icon change (fingerprint + decision + composite)");
		// An image sent again under a new handle: one hash lookup, no comparison.
		BitmapSource again = (BitmapSource)idle;
		List<double> resend = new List<double>();
		for (int i = 0; i < 300; i++)
		{
			_now += TimeSpan.TicksPerSecond;
			vm.RawImage = BitmapSource.Create(again.PixelWidth, again.PixelHeight, 96, 96, PixelFormats.Pbgra32, null, Bytes(again), again.PixelWidth * 4);
			Stopwatch one = Stopwatch.StartNew();
			TaskbarWindow.TrayImageFor(vm, 18);
			one.Stop();
			resend.Add(one.Elapsed.TotalMilliseconds);
		}
		resend.Sort();
		Metrics["ResendMsMedian"] = Math.Round(resend[resend.Count / 2], 4);
		Check("cost-resend", resend[resend.Count / 2] < 0.5, $"median {resend[resend.Count / 2]:F4} ms for the same pixels under a new handle");
		// No watcher, timer or thread is owned by the feature.
		Type[] watched = new Type[5] { typeof(FileSystemWatcher), typeof(DispatcherTimer), typeof(System.Threading.Timer), typeof(System.Timers.Timer), typeof(Thread) };
		List<string> owned = new List<string>();
		foreach (Type t in new Type[2] { typeof(SeasonalTrayIcons), typeof(SeasonalTrayLive) }.Concat(typeof(SeasonalTrayIcons).GetNestedTypes(BindingFlags.NonPublic | BindingFlags.Public)))
		{
			foreach (FieldInfo f in t.GetFields(BindingFlags.Static | BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public))
			{
				if (watched.Any(w => w.IsAssignableFrom(f.FieldType)))
				{
					owned.Add(t.Name + "." + f.Name);
				}
			}
		}
		Check("no-watchers-timers-threads", owned.Count == 0, owned.Count == 0 ? "none" : string.Join(",", owned));
	}

	// The season goes to Normal (or to Christmas, which has no picture here) or the tray choice to "always the app's
	// own icon" while an icon is blinking: the state-aware logic lets go of it, so the tray scan has nothing to settle
	// and never redraws it again; back to live, the time spent with the app's own icon is not counted for any state.
	private static void ModeSwitchEndsBlinking()
	{
		List<string> bad = new List<string>();
		foreach ((string season, string mode) in new (string, string)[3] { (SeasonalTheme.Normal, "live"), (SeasonalTheme.Christmas, "live"), (SeasonalTheme.Halloween, "original") })
		{
			Forget();
			Use(SeasonalTheme.Halloween);
			Player p = new Player("Discord");
			ImageSource idle = Chat(16, ChatState.Idle);
			p.Play(new Step("idle", idle, Pic, 0, Fresh: true));
			p.Play(new Step("call", Chat(16, ChatState.InCall), Own, 600));
			p.Play(new Step("speak", Chat(16, ChatState.Speaking), Own, 0.5));
			p.Play(new Step("quiet", Chat(16, ChatState.InCall), Own, 0.5));
			p.Play(new Step("speak", Chat(16, ChatState.Speaking), Own, 0.5));
			bool blinking = p.Vm.SeasonalTray?.BurstUntil > 0;
			Use(season, tray: mode);
			ImageSource? drawn = TaskbarWindow.TrayImageFor(p.Vm, 18);
			int redraws = 0;
			for (int scan = 0; scan < 10; scan++)
			{
				_now += 4 * TimeSpan.TicksPerSecond;
				redraws += SeasonalTrayIcons.NeedsSettle(p.Vm) ? 1 : 0;
			}
			bool exact = SamePixels(drawn, TrayIconFit.Fit(Chat(16, ChatState.Speaking), 18));
			if (!blinking || redraws > 0 || p.Vm.SeasonalTray != null || !exact)
			{
				bad.Add($"{season}/{mode}: blinking before {blinking}, redraws {redraws}, state kept {p.Vm.SeasonalTray != null}, app's own image {exact}");
			}
			if (mode == "original")
			{
				// Five hours with the app's own icon, then back to live with the plain icon shown again.
				double before = SeasonalTrayIcons.QaState("discord", idle).Score;
				_now += 5 * TimeSpan.TicksPerHour;
				p.Vm.RawImage = idle;
				TaskbarWindow.TrayImageFor(p.Vm, 18);
				Use(SeasonalTheme.Halloween);
				TaskbarWindow.TrayImageFor(p.Vm, 18);
				_now += 60 * TimeSpan.TicksPerSecond;
				p.Vm.RawImage = Chat(16, ChatState.Unread);
				TaskbarWindow.TrayImageFor(p.Vm, 18);
				double after = SeasonalTrayIcons.QaState("discord", idle).Score;
				if (after - before > 120)
				{
					bad.Add($"original for 5 h counted {after - before:F0} s for the plain icon (want about 60 s)");
				}
				Metrics["OriginalGapCredit"] = Math.Round(after - before, 1);
			}
		}
		Use(SeasonalTheme.Halloween);
		Check("mode-switch-ends-blinking", bad.Count == 0, bad.Count == 0 ? "Normal, Christmas without a picture and 'original' during a blinking call: no redraws, state released, the app's own image; no time counted while 'original'" : string.Join("; ", bad));
	}

	// Start-up votes: one per process (a bar rebuilt in the first minutes after a start does not vote again), cast for
	// the image the app keeps (a short "connecting" image right after the start does not get it), and votes for an icon
	// the app no longer shows are dropped once two later starts voted for its new icon.
	private static void Votes()
	{
		List<Cell> all = new List<Cell>();
		// Rebuilt bars: the same process's icon is recreated twice within its first minutes.
		Forget();
		ImageSource idle = Chat(16, ChatState.Idle);
		Player a = new Player("Discord");
		a.Play(new Step("idle (start)", idle, Pic, 0, Fresh: true));
		a.Play(new Step("vote", null, Pic, 21));
		for (int rebuild = 1; rebuild <= 2; rebuild++)
		{
			SeasonalTrayIcons.Drop(a.Vm);
			Player b = new Player("Discord", a.Proc);
			b.Play(new Step($"rebuilt {rebuild}", idle, Pic, 5, Fresh: true));
			b.Play(new Step("scan", null, Pic, 25, Quiet: true));
			a = b;
		}
		int votes = SeasonalTrayIcons.QaModel("discord").Votes;
		Check("vote-once-per-process", votes == 1, $"{votes} vote(s) after the icon was recreated twice for the same process");

		// A short image right after the start (connecting, updating) is not the start-up image.
		Forget();
		Player c = new Player("Discord");
		ImageSource connecting = Chat(16, ChatState.Error);
		c.Play(new Step("connecting", connecting, Pic, 0, Fresh: true));
		c.Play(new Step("idle", idle, Own, 4));
		c.Play(new Step("kept 20 s", null, Pic, 21));
		c.Play(new Step("unread", Chat(16, ChatState.Unread), Badge, 60));
		c.Play(new Step("offline", connecting, Own, 60));
		c.Play(new Step("online", idle, Pic, 60));
		CheckCells("vote-skips-start-image", c.Cells);
		all.AddRange(c.Cells);

		// The app changes its icon: five starts voted for the old one; two starts with the new one are enough.
		Forget();
		for (int k = 0; k < 5; k++)
		{
			Player o = new Player("Discord");
			o.Play(new Step("old", idle, Pic, 3600, Fresh: true));
			o.Play(new Step("unread", Chat(16, ChatState.Unread), Badge, 60));
			o.Play(new Step("old", idle, Pic, 60));
			SeasonalTrayIcons.Drop(o.Vm);
		}
		ImageSource logo = Chat(16, ChatState.NewLogo);
		Player n1 = new Player("Discord");
		n1.Play(new Step("new icon, start 1", logo, Own, 86400, Fresh: true));
		n1.Play(new Step("scan", null, Own, 25));
		SeasonalTrayIcons.Drop(n1.Vm);
		Player n2 = new Player("Discord");
		n2.Play(new Step("start 2", logo, Own, 86400, Fresh: true));
		n2.Play(new Step("vote", null, Pic, 25));
		n2.Play(new Step("unread", Chat(16, ChatState.NewLogoUnread), Badge, 60));
		CheckCells("vote-new-icon", n1.Cells.Concat(n2.Cells).ToList());
		all.AddRange(n1.Cells);
		all.AddRange(n2.Cells);
		RenderStrip("votes", "Start-up votes: a short start image; the app changes its icon (two starts)", all, "discord");
	}

	// Display time per app: a bar rebuilt after a display change continues the episode, an app that exits closes it (an
	// hour without the app is not counted), and the shell closing normally saves the open episode.
	private static void EpisodeContinuity(string store)
	{
		Forget();
		ImageSource idle = Chat(16, ChatState.Idle);
		Player a = new Player("Discord");
		a.Play(new Step("idle", idle, Pic, 0, Fresh: true));
		a.Play(new Step("unread", Chat(16, ChatState.Unread), Badge, 60));
		a.Play(new Step("idle", idle, Pic, 60));
		double s0 = SeasonalTrayIcons.QaState("discord", idle).Score;
		_now += 2 * TimeSpan.TicksPerHour;
		SeasonalTrayIcons.Drop(a.Vm);
		Player b = new Player("Discord", a.Proc);
		b.Play(new Step("rebuilt", idle, Pic, 1));
		double s1 = SeasonalTrayIcons.QaState("discord", idle).Score;
		// Shown 30 more minutes, then the app exits; it starts again an hour later.
		_now += 30 * TimeSpan.TicksPerMinute;
		SeasonalTrayIcons.Drop(b.Vm);
		_now += TimeSpan.TicksPerHour;
		Player c = new Player("Discord");
		c.Play(new Step("restarted", idle, Pic, 0, Fresh: true));
		double s2 = SeasonalTrayIcons.QaState("discord", idle).Score;
		// Shown 40 minutes, then the shell closes normally (no image change in between).
		_now += 40 * TimeSpan.TicksPerMinute;
		SeasonalTrayIcons.Flush();
		Restart();
		Player d = new Player("Discord");
		d.Play(new Step("next session", idle, Pic, 60));
		double s3 = SeasonalTrayIcons.QaState("discord", idle).Score;
		bool ok = s1 - s0 > 7000 && s2 - s1 > 1700 && s2 - s1 < 2000 && s3 - s2 > 2300;
		Check("episode-continuity", ok, $"plain icon: {s0:F0} s, after 2 h and a bar rebuild {s1:F0} s, after 30 min, an exit and a restart an hour later {s2:F0} s, after 40 min and a normal close {s3:F0} s");
	}

	// Stand-ins (the program's or window's icon while the tray image could not be read, a letter tile, a saved
	// snapshot) are never learned: the first one shows the picture, one in the middle of a call keeps the call's look.
	private static void Substitutes()
	{
		Forget();
		Player p = new Player("Discord");
		ImageSource idle = Chat(16, ChatState.Idle);
		ImageSource call = Chat(16, ChatState.InCall);
		ImageSource program = ProgramIcon(32);
		p.Play(new Step("stand-in (first)", program, Pic, 0, Fresh: true, Sub: true));
		int learned = SeasonalTrayIcons.QaModel("discord").States;
		p.Play(new Step("idle", idle, Pic, 1, Fresh: true));
		p.Play(new Step("unread", Chat(16, ChatState.Unread), Badge, 60));
		p.Play(new Step("stand-in", program, Badge, 1, Sub: true));
		p.Play(new Step("call", call, Own, 60));
		p.Play(new Step("stand-in", program, Own, 0.3, Sub: true));
		p.Play(new Step("call", call, Own, 0.3));
		p.Play(new Step("left", idle, Pic, 60));
		CheckCells("stand-ins-not-learned", p.Cells);
		var m = SeasonalTrayIcons.QaModel("discord");
		Check("stand-ins-not-states", learned == 0 && m.States == 3, $"states before the first real image: {learned}; after: {m.States} (idle, unread, call)");
		RenderStrip("stand-ins", "Stand-in images (program icon) are never learned", p.Cells, "discord");
	}

	// An animated tray icon (a spinner, 24 frames, 4 per second for 30 s): every frame is the app's own, frames seen
	// during the animation are not learned (no new states, nothing evicted, no structural save), and it settles back.
	private static void Animation()
	{
		Forget();
		Player p = new Player("Discord");
		ImageSource idle = Chat(16, ChatState.Idle);
		p.Play(new Step("idle", idle, Pic, 0, Fresh: true));
		p.Play(new Step("unread", Chat(16, ChatState.Unread), Badge, 60));
		p.Play(new Step("idle", idle, Pic, 60));
		SeasonalTrayIcons.SaveNow(_now, background: false);
		int states = SeasonalTrayIcons.QaModel("discord").States;
		int wrong = 0;
		for (int f = 0; f < 120; f++)
		{
			Cell c = p.Play(new Step("spin", Spinner(16, f % 24), Own, 0.25));
			wrong += c.Got == Own ? 0 : 1;
		}
		int during = SeasonalTrayIcons.QaModel("discord").States;
		bool structural = SeasonalTrayIcons.QaStructuralPending;
		p.Play(new Step("done", idle, Own, 0.25));
		p.Play(new Step("settled", null, Pic, 3));
		p.Play(new Step("unread", Chat(16, ChatState.Unread), Badge, 60));
		var m = SeasonalTrayIcons.QaModel("discord");
		Check("animation-no-churn", wrong == 0 && during - states <= 3 && !structural && m.States == during && p.Cells[^1].Got == Badge && p.Cells[^2].Got == Pic, $"120 frames: not the app's own {wrong}; states {states} -> {during} (at most 3 new, before the animation was recognised); structural save pending: {structural}; after: {Name(p.Cells[^2].Got)}, {Name(p.Cells[^1].Got)}");
	}

	// The store: an unreadable image or app is skipped (the rest loads), and a file that cannot be parsed is set aside
	// as .bad and learning starts afresh.
	private static void StoreRobustness(string store)
	{
		Forget();
		Player d = new Player("Discord");
		d.Play(new Step("idle", Chat(16, ChatState.Idle), Pic, 0, Fresh: true));
		d.Play(new Step("unread", Chat(16, ChatState.Unread), Badge, 60));
		d.Play(new Step("idle", Chat(16, ChatState.Idle), Pic, 60));
		Player t = new Player("slack");
		t.Play(new Step("idle", Team(16, TeamState.Idle), Pic, 0, Fresh: true));
		t.Play(new Step("unread", Team(16, TeamState.Unread), Badge, 60));
		t.Play(new Step("idle", Team(16, TeamState.Idle), Pic, 60));
		SeasonalTrayIcons.SaveNow(_now, background: false);
		JsonNode root = JsonNode.Parse(File.ReadAllText(store))!;
		JsonObject apps = root["Apps"]!.AsObject();
		int idleId = (int)apps["discord"]!["Idle"]!;
		foreach (JsonNode? st in apps["discord"]!["States"]!.AsArray())
		{
			if ((int)st!["Id"]! != idleId)
			{
				st["Images"]![0]!["Pixels"] = "not base64 !";
			}
		}
		apps["broken"] = JsonNode.Parse("{\"Idle\":1,\"States\":[{\"Id\":1,\"Images\":[{\"W\":16,\"H\":16,\"Pixels\":\"AAAA\"}]}]}");
		File.WriteAllText(store, root.ToJsonString());
		Restart();
		Player d2 = new Player("Discord");
		d2.Play(new Step("start+dot", Chat(16, ChatState.Unread), Badge, 600, Fresh: true));
		Player t2 = new Player("slack");
		t2.Play(new Step("start+dot", Team(16, TeamState.Unread), Badge, 0, Fresh: true));
		bool partial = d2.Cells[0].Got == Badge && t2.Cells[0].Got == Badge;
		// A file that is not JSON at all.
		File.WriteAllText(store, "{ not json");
		Restart();
		Player d3 = new Player("Discord");
		d3.Play(new Step("first", Chat(16, ChatState.Unread), Pic, 600, Fresh: true));
		bool setAside = File.Exists(store + ".bad") && File.ReadAllText(store + ".bad") == "{ not json";
		SeasonalTrayIcons.SaveNow(_now, background: false);
		bool fresh = false;
		try
		{
			fresh = JsonNode.Parse(File.ReadAllText(store))?["Apps"]?["discord"] != null;
		}
		catch
		{
		}
		Check("store-bad-entries", partial && setAside && fresh, $"bad image and app skipped, rest loaded: {partial}; unparsable file kept as .bad: {setAside}; new store written: {fresh}");
		File.Delete(store + ".bad");
	}

	// An app's own tray images from a folder (read only): tray.png is idle, names with unread/badge/mention/dot are
	// badges, every other tray-*.png is a state of its own. Each size set (@1.25x, @1.5x, @2x) is learned fresh.
	private static void RealSamples(string dir)
	{
		List<Cell> all = new List<Cell>();
		string[] scales = new string[4] { "", "@1.25x", "@1.5x", "@2x" };
		List<string> files = Directory.GetFiles(dir, "tray*.png").Select(Path.GetFileNameWithoutExtension).Where(n => n != null && n.IndexOf('@') < 0).Cast<string>().OrderBy(n => n == "tray" ? "" : n, StringComparer.Ordinal).ToList();
		if (!files.Contains("tray"))
		{
			Check("real-samples", false, "no tray.png in " + dir);
			return;
		}
		Metrics["RealSamples"] = files;
		foreach (string scale in scales)
		{
			if (!File.Exists(Path.Combine(dir, "tray" + scale + ".png")))
			{
				continue;
			}
			Forget();
			Player p = new Player("Discord");
			ImageSource idle = LoadPng(Path.Combine(dir, "tray" + scale + ".png"));
			p.Play(new Step("tray" + scale, idle, Pic, 0, Fresh: true));
			foreach (string f in files.Where(f => f != "tray"))
			{
				string path = Path.Combine(dir, f + scale + ".png");
				if (!File.Exists(path))
				{
					continue;
				}
				string lower = f.ToLowerInvariant();
				bool badge = lower.Contains("unread") || lower.Contains("badge") || lower.Contains("mention") || lower.Contains("dot");
				p.Play(new Step(f.Replace("tray-", "") + scale, LoadPng(path), badge ? Badge : Own, 60));
				p.Play(new Step("tray" + scale, idle, Pic, 60));
			}
			CheckCells("real-samples" + (scale.Length > 0 ? scale : "@1x"), p.Cells);
			all.AddRange(p.Cells);
		}
		CheckRealRim("real-samples-unread-rim", all);
		RenderStrip("real-samples", "App's own tray images (" + Path.GetFileName(dir.TrimEnd('\\', '/')) + ")", all, "discord");
	}

	private static ImageSource LoadPng(string path)
	{
		byte[] bytes = File.ReadAllBytes(path);
		using MemoryStream ms = new MemoryStream(bytes, writable: false);
		BitmapDecoder d = BitmapDecoder.Create(ms, BitmapCreateOptions.PreservePixelFormat | BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.OnLoad);
		BitmapSource f = new FormatConvertedBitmap(d.Frames[0], PixelFormats.Bgra32, null, 0.0);
		byte[] px = new byte[f.PixelWidth * f.PixelHeight * 4];
		f.CopyPixels(px, f.PixelWidth * 4, 0);
		BitmapSource b = BitmapSource.Create(f.PixelWidth, f.PixelHeight, 96, 96, PixelFormats.Bgra32, null, px, f.PixelWidth * 4);
		b.Freeze();
		// The path a real tray icon takes (IconToSource): lifted only when it is an all-dark silhouette.
		return AppInventory.EnsureVisibleOnDark(b) ?? b;
	}

	// ===================================================================== readability

	// The badge reads: at every size its colour is inside the badge of the composite (sampled left of the centre, off a
	// digit), and just outside the badge, where the picture lies, the ring contrasts with it (the halo): at least 60 %
	// of the ring samples over the picture reach 2.5:1 against the badge colour.
	private static void CheckBadgeReadable(string name, Cell cell, string key, double fx, double fy, double fr, Color badge)
	{
		List<string> detail = new List<string>();
		bool ok = cell.Got == Badge;
		for (int i = 0; i < Sizes.Length; i++)
		{
			int px = Sizes[i];
			if (cell.Shown[i] is not BitmapSource img || SeasonalAppIcons.Frame(key, px) is not BitmapSource art)
			{
				ok = false;
				continue;
			}
			double cx = fx * px;
			double cy = fy * px;
			double r = fr * px;
			Color c = Unpremul(img, (int)Math.Floor(cx - 0.55 * r), (int)Math.Floor(cy));
			double dist = Math.Sqrt(Sq(c.R - badge.R) + Sq(c.G - badge.G) + Sq(c.B - badge.B));
			double ring = r + 0.7;
			int over = 0;
			int good = 0;
			for (int a = 0; a < 24; a++)
			{
				double ang = a * Math.PI / 12;
				int x = (int)Math.Floor(cx + Math.Cos(ang) * ring);
				int y = (int)Math.Floor(cy + Math.Sin(ang) * ring);
				if (x < 0 || y < 0 || x >= px || y >= px)
				{
					continue;
				}
				Color o = Unpremul(img, x, y, out byte alpha);
				Unpremul(art, x, y, out byte artA);
				if (alpha < 100 || artA < 128)
				{
					continue;
				}
				over++;
				if (SeasonalAppIcons.Contrast(o, badge) >= 2.5)
				{
					good++;
				}
			}
			// No pale rim: around the badge's edge nothing is lighter than both the badge and the picture there.
			int rim = RimLighter(img, art, px, (x, y) =>
			{
				double d = Math.Sqrt(Sq(x + 0.5 - cx) + Sq(y + 0.5 - cy));
				return d >= r - 0.5 && d <= r + 1.5;
			}, badge);
			detail.Add($"{px}px colour dist {dist:F0}, halo {good}/{over}, pale rim {rim}");
			ok &= dist < 70 && (over == 0 || good * 10 >= over * 6) && rim == 0;
		}
		Check(name, ok, string.Join(", ", detail));
	}

	// The number of pixels in band whose colour over the dark or the light bar is lighter than both the badge colour
	// and the picture there (the app's logo showing as a pale rim). Checked only where the halo is dark (a light badge).
	private static int RimLighter(BitmapSource img, BitmapSource art, int px, Func<int, int, bool> band, Color badge)
	{
		if (Lum(badge) <= 0.179)
		{
			return 0;
		}
		byte[] o = Bytes(img);
		byte[] a = Bytes(art);
		int bad = 0;
		for (int y = 0; y < px; y++)
		{
			for (int x = 0; x < px; x++)
			{
				int i = (y * px + x) * 4;
				if (!band(x, y) || a[i + 3] < 128)
				{
					continue;
				}
				foreach (Color bar in new Color[2] { DarkBar, LightBar })
				{
					if (Lum(Over(o, i, bar)) > Math.Max(Lum(Over(a, i, bar)), Lum(badge)) + 0.05)
					{
						bad++;
						break;
					}
				}
			}
		}
		return bad;
	}

	// The pixels next to the badge (the badge found by its colour in the composite) on the app's real unread images:
	// none lighter than both the badge and the picture.
	private static void CheckRealRim(string name, List<Cell> cells)
	{
		List<string> detail = new List<string>();
		bool ok = true;
		foreach (Cell cell in cells.Where(c => c.Name.StartsWith("unread", StringComparison.Ordinal) && c.Got == Badge && c.Raw is BitmapSource))
		{
			Color badge = SaturatedColour((BitmapSource)cell.Raw!);
			int total = 0;
			for (int i = 0; i < Sizes.Length; i++)
			{
				int px = Sizes[i];
				if (cell.Shown[i] is not BitmapSource img || SeasonalAppIcons.Frame("discord", px) is not BitmapSource art)
				{
					continue;
				}
				byte[] o = Bytes(img);
				bool[] isBadge = new bool[px * px];
				for (int k = 0; k < px * px; k++)
				{
					int a = o[k * 4 + 3];
					if (a > 200)
					{
						double d = Math.Sqrt(Sq(o[k * 4 + 2] * 255.0 / a - badge.R) + Sq(o[k * 4 + 1] * 255.0 / a - badge.G) + Sq(o[k * 4] * 255.0 / a - badge.B));
						isBadge[k] = d < 60;
					}
				}
				total += RimLighter(img, art, px, (x, y) =>
				{
					if (isBadge[y * px + x])
					{
						return false;
					}
					for (int dy = -1; dy <= 1; dy++)
					{
						for (int dx = -1; dx <= 1; dx++)
						{
							int xx = x + dx;
							int yy = y + dy;
							if (xx >= 0 && yy >= 0 && xx < px && yy < px && isBadge[yy * px + xx])
							{
								return true;
							}
						}
					}
					return false;
				}, badge);
			}
			detail.Add($"{cell.Name}: pale rim {total}");
			ok &= total == 0;
		}
		Check(name, ok && detail.Count > 0, detail.Count > 0 ? string.Join(", ", detail) : "no unread cells");
	}

	// The average colour of the clearly coloured, opaque pixels of an image (a badge on a white or grey logo).
	private static Color SaturatedColour(BitmapSource src)
	{
		byte[] b = Bytes(src);
		double r = 0, g = 0, bl = 0;
		int n = 0;
		for (int i = 0; i < b.Length; i += 4)
		{
			int a = b[i + 3];
			if (a < 230)
			{
				continue;
			}
			double cr = b[i + 2] * 255.0 / a;
			double cg = b[i + 1] * 255.0 / a;
			double cb = b[i] * 255.0 / a;
			double max = Math.Max(cr, Math.Max(cg, cb));
			double min = Math.Min(cr, Math.Min(cg, cb));
			if (max > 0 && (max - min) / max > 0.5)
			{
				r += cr;
				g += cg;
				bl += cb;
				n++;
			}
		}
		return n == 0 ? Colors.Red : Color.FromRgb((byte)(r / n), (byte)(g / n), (byte)(bl / n));
	}

	private static Color Over(byte[] premul, int i, Color bar)
	{
		double a = premul[i + 3] / 255.0;
		return Color.FromRgb((byte)Math.Clamp(Math.Round(premul[i + 2] + bar.R * (1 - a)), 0, 255), (byte)Math.Clamp(Math.Round(premul[i + 1] + bar.G * (1 - a)), 0, 255), (byte)Math.Clamp(Math.Round(premul[i] + bar.B * (1 - a)), 0, 255));
	}

	private static double Lum(Color c)
	{
		static double L(byte v)
		{
			double s = v / 255.0;
			return s <= 0.04045 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
		}
		return 0.2126 * L(c.R) + 0.7152 * L(c.G) + 0.0722 * L(c.B);
	}

	private static double Sq(double v)
	{
		return v * v;
	}

	private static Color Unpremul(BitmapSource src, int x, int y)
	{
		return Unpremul(src, x, y, out _);
	}

	private static Color Unpremul(BitmapSource src, int x, int y, out byte alpha)
	{
		BitmapSource conv = src.Format == PixelFormats.Bgra32 ? src : new FormatConvertedBitmap(src, PixelFormats.Bgra32, null, 0.0);
		byte[] px = new byte[4];
		conv.CopyPixels(new Int32Rect(Math.Clamp(x, 0, conv.PixelWidth - 1), Math.Clamp(y, 0, conv.PixelHeight - 1), 1, 1), px, 4, 0);
		alpha = px[3];
		return Color.FromArgb(px[3], px[2], px[1], px[0]);
	}

	// ===================================================================== synthetic icons

	private enum ChatState { Idle, Unread, Mention, InCall, Speaking, Muted, Error, NewLogo, NewLogoUnread }

	private enum TeamState { Idle, Unread, Mention, Away, Error, Flash }

	private static readonly Brush BadgeRed = Frozen(Color.FromRgb(0xF0, 0x47, 0x47));

	private static readonly Brush TeamRed = Frozen(Color.FromRgb(0xE0, 0x1E, 0x5A));

	private static readonly Brush TeamBlue = Frozen(Color.FromRgb(0x1D, 0x9B, 0xD1));

	private static Brush Frozen(Color c)
	{
		SolidColorBrush b = new SolidColorBrush(c);
		b.Freeze();
		return b;
	}

	// A chat app's tray icon: a white rounded mascot with two eyes; badges are cut out of it like real tray badges.
	private static ImageSource Chat(int n, ChatState state)
	{
		return DrawIcon(n, delegate(DrawingContext dc, double s)
		{
			Geometry body = Mascot(s);
			switch (state)
			{
				case ChatState.Idle:
					dc.DrawGeometry(Brushes.White, null, body);
					break;
				case ChatState.Unread:
					dc.DrawGeometry(Brushes.White, null, Cut(body, new Point(0.8 * s, 0.2 * s), 0.27 * s));
					dc.DrawEllipse(BadgeRed, null, new Point(0.8 * s, 0.2 * s), 0.2 * s, 0.2 * s);
					break;
				case ChatState.Mention:
					dc.DrawGeometry(Brushes.White, null, Cut(body, new Point(0.74 * s, 0.74 * s), 0.32 * s));
					dc.DrawEllipse(BadgeRed, null, new Point(0.74 * s, 0.74 * s), 0.26 * s, 0.26 * s);
					Digit(dc, "3", new Point(0.74 * s, 0.74 * s), 0.42 * s, Brushes.White);
					break;
				case ChatState.InCall:
					dc.DrawEllipse(Frozen(Color.FromRgb(0x2D, 0x5E, 0x46)), new Pen(Frozen(Color.FromRgb(0x2E, 0x8B, 0x57)), 0.09 * s), new Point(0.5 * s, 0.5 * s), 0.42 * s, 0.42 * s);
					break;
				case ChatState.Speaking:
					dc.DrawEllipse(Frozen(Color.FromRgb(0x43, 0xB5, 0x81)), new Pen(Frozen(Color.FromRgb(0xB8, 0xF5, 0xD0)), 0.1 * s), new Point(0.5 * s, 0.5 * s), 0.41 * s, 0.41 * s);
					break;
				case ChatState.Muted:
				{
					Pen white = new Pen(Brushes.White, 0.1 * s) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
					dc.DrawRoundedRectangle(Brushes.White, null, new Rect(0.38 * s, 0.08 * s, 0.24 * s, 0.48 * s), 0.12 * s, 0.12 * s);
					dc.DrawGeometry(null, white, Geometry.Parse(string.Format(CultureInfo.InvariantCulture, "M {0},{1} A {2},{2} 0 0 0 {3},{1}", 0.26 * s, 0.46 * s, 0.24 * s, 0.74 * s)));
					dc.DrawLine(white, new Point(0.5 * s, 0.72 * s), new Point(0.5 * s, 0.9 * s));
					dc.DrawLine(new Pen(BadgeRed, 0.11 * s) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round }, new Point(0.12 * s, 0.12 * s), new Point(0.88 * s, 0.88 * s));
					break;
				}
				case ChatState.Error:
					dc.DrawGeometry(Frozen(Color.FromRgb(0x8E, 0x92, 0x97)), null, body);
					dc.DrawEllipse(Frozen(Color.FromRgb(0xFA, 0xA6, 0x1A)), null, new Point(0.5 * s, 0.52 * s), 0.17 * s, 0.17 * s);
					break;
				case ChatState.NewLogo:
				case ChatState.NewLogoUnread:
				{
					// The app's redesigned icon: the mascot in the brand colour instead of white.
					Brush brand = Frozen(Color.FromRgb(0x58, 0x65, 0xF2));
					if (state == ChatState.NewLogoUnread)
					{
						dc.DrawGeometry(brand, null, Cut(body, new Point(0.8 * s, 0.2 * s), 0.27 * s));
						dc.DrawEllipse(BadgeRed, null, new Point(0.8 * s, 0.2 * s), 0.2 * s, 0.2 * s);
					}
					else
					{
						dc.DrawGeometry(brand, null, body);
					}
					break;
				}
			}
		});
	}

	// A program icon (what the shell falls back to when the tray image cannot be read): a coloured square with the mascot.
	private static ImageSource ProgramIcon(int n)
	{
		return DrawIcon(n, delegate(DrawingContext dc, double s)
		{
			dc.DrawRoundedRectangle(Frozen(Color.FromRgb(0x58, 0x65, 0xF2)), null, new Rect(0, 0, s, s), 0.22 * s, 0.22 * s);
			dc.PushTransform(new TranslateTransform(0.15 * s, 0.15 * s));
			dc.PushTransform(new ScaleTransform(0.7, 0.7));
			dc.DrawGeometry(Brushes.White, null, Mascot(s));
			dc.Pop();
			dc.Pop();
		});
	}

	// A sync spinner frame k of 24: a ring with a gap, turned by 15 degrees per frame.
	private static ImageSource Spinner(int n, int k)
	{
		return DrawIcon(n, delegate(DrawingContext dc, double s)
		{
			double a0 = k * Math.PI / 12.0;
			double a1 = a0 + 1.5 * Math.PI;
			Point c = new Point(0.5 * s, 0.5 * s);
			double r = 0.36 * s;
			Point p0 = new Point(c.X + r * Math.Cos(a0), c.Y + r * Math.Sin(a0));
			Point p1 = new Point(c.X + r * Math.Cos(a1), c.Y + r * Math.Sin(a1));
			StreamGeometry g = new StreamGeometry();
			using (StreamGeometryContext ctx = g.Open())
			{
				ctx.BeginFigure(p0, isFilled: false, isClosed: false);
				ctx.ArcTo(p1, new Size(r, r), 0, isLargeArc: true, SweepDirection.Clockwise, isStroked: true, isSmoothJoin: true);
			}
			dc.DrawGeometry(null, new Pen(Frozen(Color.FromRgb(0x3B, 0xA5, 0x5D)), 0.16 * s) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round }, g);
		});
	}

	private static Geometry Mascot(double s)
	{
		Geometry body = new RectangleGeometry(new Rect(0.06 * s, 0.2 * s, 0.88 * s, 0.6 * s), 0.24 * s, 0.24 * s);
		Geometry eyes = new GeometryGroup
		{
			Children =
			{
				new EllipseGeometry(new Point(0.36 * s, 0.5 * s), 0.09 * s, 0.1 * s),
				new EllipseGeometry(new Point(0.64 * s, 0.5 * s), 0.09 * s, 0.1 * s)
			}
		};
		return new CombinedGeometry(GeometryCombineMode.Exclude, body, eyes);
	}

	private static Geometry Cut(Geometry g, Point c, double r)
	{
		return new CombinedGeometry(GeometryCombineMode.Exclude, g, new EllipseGeometry(c, r, r));
	}

	private static void Digit(DrawingContext dc, string text, Point c, double size, Brush brush)
	{
		FormattedText ft = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal), size, brush, 1.0);
		dc.DrawText(ft, new Point(c.X - ft.Width / 2.0, c.Y - ft.Height / 2.0));
	}

	// A team app's tray icon: four rounded bars in four colours forming a hash.
	private static ImageSource Team(int n, TeamState state)
	{
		return DrawIcon(n, delegate(DrawingContext dc, double s)
		{
			bool grey = state == TeamState.Away || state == TeamState.Error;
			Brush[] bars = grey
				? new Brush[4] { Frozen(Color.FromRgb(0x9A, 0x9A, 0x9A)), Frozen(Color.FromRgb(0x9A, 0x9A, 0x9A)), Frozen(Color.FromRgb(0x9A, 0x9A, 0x9A)), Frozen(Color.FromRgb(0x9A, 0x9A, 0x9A)) }
				: new Brush[4] { Frozen(Color.FromRgb(0x36, 0xC5, 0xF0)), Frozen(Color.FromRgb(0x2E, 0xB6, 0x7D)), Frozen(Color.FromRgb(0xEC, 0xB2, 0x2E)), Frozen(Color.FromRgb(0xE0, 0x1E, 0x5A)) };
			Geometry hash = new GeometryGroup();
			Rect[] rects = new Rect[4]
			{
				new Rect(0.06 * s, 0.26 * s, 0.56 * s, 0.17 * s),
				new Rect(0.57 * s, 0.06 * s, 0.17 * s, 0.56 * s),
				new Rect(0.38 * s, 0.57 * s, 0.56 * s, 0.17 * s),
				new Rect(0.26 * s, 0.38 * s, 0.17 * s, 0.56 * s)
			};
			if (state == TeamState.Flash)
			{
				dc.DrawRoundedRectangle(Brushes.White, null, new Rect(0, 0, s, s), 0.2 * s, 0.2 * s);
			}
			Point? cut = state switch
			{
				TeamState.Unread => new Point(0.82 * s, 0.82 * s),
				TeamState.Mention => new Point(0.74 * s, 0.74 * s),
				_ => null
			};
			double cutR = state == TeamState.Unread ? 0.25 * s : 0.32 * s;
			for (int i = 0; i < 4; i++)
			{
				Geometry bar = new RectangleGeometry(rects[i], 0.085 * s, 0.085 * s);
				dc.DrawGeometry(bars[i], null, cut.HasValue ? Cut(bar, cut.Value, cutR) : bar);
			}
			if (state == TeamState.Unread)
			{
				dc.DrawEllipse(TeamBlue, null, cut!.Value, 0.18 * s, 0.18 * s);
			}
			else if (state == TeamState.Mention)
			{
				dc.DrawEllipse(TeamRed, null, cut!.Value, 0.26 * s, 0.26 * s);
				Digit(dc, "2", cut.Value, 0.42 * s, Brushes.White);
			}
			else if (state == TeamState.Error)
			{
				dc.DrawEllipse(Frozen(Color.FromRgb(0xD9, 0x30, 0x25)), null, new Point(0.5 * s, 0.5 * s), 0.22 * s, 0.22 * s);
				Digit(dc, "!", new Point(0.5 * s, 0.5 * s), 0.38 * s, Brushes.White);
			}
		});
	}

	private static ImageSource DrawIcon(int n, Action<DrawingContext, double> paint)
	{
		DrawingVisual dv = new DrawingVisual();
		using (DrawingContext dc = dv.RenderOpen())
		{
			paint(dc, n);
		}
		RenderTargetBitmap rtb = new RenderTargetBitmap(n, n, 96.0, 96.0, PixelFormats.Pbgra32);
		rtb.Render(dv);
		byte[] px = new byte[n * n * 4];
		rtb.CopyPixels(px, n * 4, 0);
		BitmapSource b = BitmapSource.Create(n, n, 96.0, 96.0, PixelFormats.Pbgra32, null, px, n * 4);
		b.Freeze();
		return AppInventory.EnsureVisibleOnDark(b) ?? b;
	}

	// ===================================================================== fixture

	// <outDir>\fixture\seasonal\halloween-icons: a drawn pumpkin for discord and slack (16, 20, 24, 32, 256 px).
	private static string BuildFixture(string outDir)
	{
		string root = Path.Combine(outDir, "fixture", "seasonal");
		if (Directory.Exists(root))
		{
			Directory.Delete(root, recursive: true);
		}
		string hall = Path.Combine(root, "halloween-icons");
		Directory.CreateDirectory(hall);
		Directory.CreateDirectory(Path.Combine(root, "christmas-icons"));
		foreach ((string key, Color body) in new (string, Color)[2] { ("discord", Color.FromRgb(0xF2, 0x8C, 0x28)), ("slack", Color.FromRgb(0x8E, 0x44, 0xAD)) })
		{
			foreach (int size in new int[5] { 16, 20, 24, 32, 256 })
			{
				BitmapSource b = (BitmapSource)DrawIcon(size, delegate(DrawingContext dc, double s)
				{
					dc.DrawEllipse(Frozen(body), new Pen(Frozen(Color.FromRgb(0x2A, 0x14, 0x06)), Math.Max(1.0, 0.06 * s)), new Point(0.5 * s, 0.56 * s), 0.44 * s, 0.38 * s);
					dc.DrawRectangle(Frozen(Color.FromRgb(0x3C, 0x7A, 0x2C)), null, new Rect(0.45 * s, 0.06 * s, 0.1 * s, 0.16 * s));
					Brush face = Frozen(Color.FromRgb(0x1A, 0x0C, 0x02));
					dc.DrawGeometry(face, null, Geometry.Parse(string.Format(CultureInfo.InvariantCulture, "M {0},{1} L {2},{3} L {4},{1} Z M {5},{1} L {6},{3} L {7},{1} Z", 0.28 * s, 0.52 * s, 0.35 * s, 0.38 * s, 0.42 * s, 0.58 * s, 0.65 * s, 0.72 * s)));
					dc.DrawRectangle(face, null, new Rect(0.32 * s, 0.66 * s, 0.36 * s, 0.08 * s));
				});
				SavePng(b, Path.Combine(hall, size == 256 ? key + ".png" : $"{key}-{size}.png"));
			}
		}
		return root;
	}

	// ===================================================================== strips

	// One column per step. For each size (16, 20, 24 px): the app's own icon and what the tray shows, on the dark bar
	// and on the light bar, all drawn 1:1; a 4x zoomed copy is saved next to it for review.
	private static void RenderStrip(string name, string title, List<Cell> cells, string? key)
	{
		foreach (int zoom in new int[2] { 1, 4 })
		{
			int col = 30 * zoom + (zoom == 1 ? 46 : 20);
			int left = zoom == 1 ? 96 : 150;
			int top = zoom == 1 ? 44 : 64;
			int rowH = 28 * zoom;
			int rows = Sizes.Length * 4;
			int w = left + col * cells.Count + 8;
			int h = top + rows * rowH + 8;
			DrawingVisual dv = new DrawingVisual();
			RenderOptions.SetBitmapScalingMode(dv, BitmapScalingMode.NearestNeighbor);
			using (DrawingContext dc = dv.RenderOpen())
			{
				dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, w, h));
				double fs = zoom == 1 ? 9.5 : 13.0;
				Text(dc, title, 4, 2, fs + 1.5, Brushes.Black, bold: true);
				for (int ci = 0; ci < cells.Count; ci++)
				{
					Cell c = cells[ci];
					double x = left + ci * col;
					Text(dc, c.Name, x, 16 * (zoom == 1 ? 1 : 1.5), fs, Brushes.Black);
					bool ok = c.Got == c.Want;
					Text(dc, Name(c.Got) switch { "seasonal" => "pic", "seasonal+badge" => "pic+badge", _ => "own" } + (ok ? "" : " (want " + Name(c.Want) + ")"), x, 28 * (zoom == 1 ? 1 : 1.5), fs, ok ? Brushes.DarkGreen : Brushes.Red);
				}
				int r = 0;
				for (int si = 0; si < Sizes.Length; si++)
				{
					int px = Sizes[si];
					foreach ((string label, Color bar) in new (string, Color)[2] { ("dark", DarkBar), ("light", LightBar) })
					{
						foreach (bool after in new bool[2] { false, true })
						{
							double y = top + r * rowH;
							dc.DrawRectangle(Frozen(bar), null, new Rect(left - 4, y, col * cells.Count + 4, rowH));
							Text(dc, $"{px}px {label} {(after ? "after" : "before")}", 4, y + rowH / 2.0 - fs * 0.7, fs, Brushes.Black);
							for (int ci = 0; ci < cells.Count; ci++)
							{
								ImageSource? img = after ? cells[ci].Shown[si] : (cells[ci].Raw != null ? TrayIconFit.Fit(cells[ci].Raw!, px) : null);
								if (img == null)
								{
									continue;
								}
								double x = left + ci * col;
								double iy = y + (rowH - px * zoom) / 2;
								dc.DrawImage(zoom == 1 ? img : Replicate(img, zoom), new Rect(Math.Round(x), Math.Round(iy), px * zoom, px * zoom));
							}
							r++;
						}
					}
				}
			}
			RenderTargetBitmap rtb = new RenderTargetBitmap(w, h, 96.0, 96.0, PixelFormats.Pbgra32);
			rtb.Render(dv);
			SavePng(rtb, Path.Combine(_outDir, $"traystate-{name}{(zoom == 1 ? "" : "-x4")}.png"));
		}
		Metrics["Decisions_" + name] = cells.Select(c => $"{c.Name}={Name(c.Got)}").ToArray();
	}

	// Each pixel as a z x z block (drawn 1:1, so the zoomed strips show the real pixels).
	private static BitmapSource Replicate(ImageSource img, int z)
	{
		BitmapSource b = (BitmapSource)img;
		byte[] src = Bytes(b);
		int w = b.PixelWidth;
		int h = b.PixelHeight;
		byte[] dst = new byte[w * z * h * z * 4];
		for (int y = 0; y < h * z; y++)
		{
			for (int x = 0; x < w * z; x++)
			{
				Buffer.BlockCopy(src, ((y / z) * w + x / z) * 4, dst, (y * w * z + x) * 4, 4);
			}
		}
		BitmapSource r = BitmapSource.Create(w * z, h * z, 96.0, 96.0, PixelFormats.Pbgra32, null, dst, w * z * 4);
		r.Freeze();
		return r;
	}

	private static void Text(DrawingContext dc, string text, double x, double y, double size, Brush brush, bool bold = false)
	{
		FormattedText ft = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, bold ? FontWeights.SemiBold : FontWeights.Normal, FontStretches.Normal), size, brush, 1.0);
		dc.DrawText(ft, new Point(x, y));
	}

	// ===================================================================== PC Settings

	private static void RenderPcSettings(string path)
	{
		StartScreen start = new StartScreen(transitionDiagnostics: true)
		{
			Left = -6000.0,
			Top = -6000.0,
			ShowActivated = false,
			Topmost = false
		};
		try
		{
			PcSettingsWindow pc = new PcSettingsWindow(start)
			{
				Left = -6000.0,
				Top = -6000.0
			};
			try
			{
				pc.QaRenderTall(path, "Launcher");
			}
			finally
			{
				pc.Close();
			}
		}
		finally
		{
			start.Close();
		}
		Check("pcsettings-rendered", File.Exists(path), Path.GetFileName(path));
	}

	// ===================================================================== helpers

	// The launcher data folder's entries and the settings file's hash (this test must change neither).
	private static string OutsideStamp()
	{
		StringBuilder sb = new StringBuilder();
		if (Directory.Exists(DataRoot))
		{
			foreach (string f in Directory.GetFileSystemEntries(DataRoot).Select(p => Path.GetFileName(p) ?? "").Where(n => !n.StartsWith("log", StringComparison.OrdinalIgnoreCase)).OrderBy(n => n, StringComparer.OrdinalIgnoreCase))
			{
				sb.Append(f).Append(';');
			}
		}
		string settings = Path.Combine(DataRoot, "settings.json");
		try
		{
			sb.Append('|').Append(File.Exists(settings) ? Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(settings)))[..16] : "-");
		}
		catch (Exception ex)
		{
			sb.Append("|unreadable:").Append(ex.GetType().Name);
		}
		return sb.ToString();
	}

	private static bool SamePixels(ImageSource? a, ImageSource? b)
	{
		return a is BitmapSource x && b is BitmapSource y && x.PixelWidth == y.PixelWidth && x.PixelHeight == y.PixelHeight && Bytes(x).SequenceEqual(Bytes(y));
	}

	private static byte[] Bytes(BitmapSource src)
	{
		BitmapSource conv = src.Format == PixelFormats.Pbgra32 ? src : new FormatConvertedBitmap(src, PixelFormats.Pbgra32, null, 0.0);
		int stride = conv.PixelWidth * 4;
		byte[] px = new byte[stride * conv.PixelHeight];
		conv.CopyPixels(px, stride, 0);
		return px;
	}

	private static void SavePng(BitmapSource bmp, string path)
	{
		PngBitmapEncoder enc = new PngBitmapEncoder();
		enc.Frames.Add(BitmapFrame.Create(bmp));
		using FileStream fs = File.Create(path);
		enc.Save(fs);
	}
}
