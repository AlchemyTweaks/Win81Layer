#nullable enable

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;

namespace Win81Layer;

// --seasonaliconstest <outDir> [--seasonal-root <dir>]: offscreen QA of the seasonal app icons. Read-only by
// construction: settings writes throw (ReadOnlyDiagnostics), the season is set through a diagnostics override, the
// icons come from a fixture generated inside outDir, and every file it writes is inside outDir. With --seasonal-root
// the real icon folder is also probed (read only) and rendered.
internal static class SeasonalAppIconsDiagnostics
{
	private static readonly List<(string Name, bool Pass, string Detail)> Checks = new List<(string, bool, string)>();

	private static readonly Dictionary<string, object> Metrics = new Dictionary<string, object>();

	private static readonly string DataRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Win81Layer");

	private static readonly string[] FixtureKeys = new string[5] { "discord", "chrome", "slack", "spotify", "notion" };

	private static readonly int[] FixtureSizes = new int[10] { 16, 20, 24, 32, 40, 48, 64, 96, 128, 256 };

	private static readonly string[] AppNames = new string[5] { "discord", "chrome", "spotify", "slack", "notion" };

	private static readonly Color BarColor = Color.FromRgb(0x1F, 0x1F, 0x1F);

	private static readonly Color StartColor = Color.FromRgb(0x1D, 0x1D, 0x1D);

	private static AppSettings _real = new AppSettings();

	public static void Begin(Application app, string[] args)
	{
		app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
		int idx = Array.IndexOf(args, "--seasonaliconstest");
		string outDir = (idx >= 0 && idx + 1 < args.Length && !args[idx + 1].StartsWith("--", StringComparison.Ordinal))
			? args[idx + 1]
			: Path.Combine(DataRoot, "qa", "seasonal-icons");
		int r = Array.IndexOf(args, "--seasonal-root");
		string? realRoot = (r >= 0 && r + 1 < args.Length) ? args[r + 1] : null;
		app.Dispatcher.BeginInvoke((Action)delegate
		{
			int code = 1;
			try
			{
				code = Run(outDir, realRoot) ? 0 : 1;
			}
			catch (Exception ex)
			{
				try
				{
					Directory.CreateDirectory(outDir);
					File.WriteAllText(Path.Combine(outDir, "seasonaliconstest-error.txt"), ex.ToString());
				}
				catch
				{
				}
			}
			app.Shutdown(code);
		}, DispatcherPriority.Loaded);
	}

	private static bool Run(string outDir, string? realRoot)
	{
		Directory.CreateDirectory(outDir);
		Baseline before = Baseline.Capture(realRoot);
		_real = SettingsStore.Load();
		List<string> log = new List<string>();
		SeasonalAppIcons.QaLog = log;
		StartScreen? start = null;
		try
		{
			string fixture = BuildFixture(outDir);
			SeasonalAppIcons.DiagnosticsRoot = fixture;
			Metrics["Fixture"] = fixture;
			IdleChecks();
			MatchingChecks();
			GatingChecks();
			FrameChecks(fixture, log);
			start = new StartScreen(transitionDiagnostics: true)
			{
				Left = -6000.0,
				Top = -6000.0,
				ShowActivated = false,
				Topmost = false
			};
			StartChecks(outDir, start);
			RobustnessChecks(outDir, start, log, fixture);
			UnreadableArtChecks(outDir, start, fixture);
			ReconcileChecks();
			TaskbarChecks(outDir);
			TrayChecks(outDir);
			Win7Checks(outDir);
			OtherSurfaceChecks();
			JumpListChecks(start);
			Use(SeasonalTheme.Halloween);
			RenderPcSettings(Path.Combine(outDir, "pcsettings-seasonal-icons.png"), start);
			if (realRoot != null)
			{
				RealProbe(outDir, realRoot, start);
			}
		}
		catch (Exception ex)
		{
			Check("harness-completed", false, ex.ToString());
		}
		finally
		{
			try
			{
				SettingsStore.DiagnosticsOverride = null;
				SeasonalAppIcons.DiagnosticsRoot = null;
				SeasonalAppIcons.Reconcile("qa-end");
				Pump();
			}
			catch
			{
			}
			try
			{
				start?.Close();
			}
			catch
			{
			}
			SeasonalAppIcons.QaLog = null;
		}
		Baseline after = Baseline.Capture(realRoot);
		Check("readonly-state-files", before.Hashes == after.Hashes, before.Hashes == after.Hashes ? before.Hashes : $"before={before.Hashes} after={after.Hashes}");
		Check("readonly-seasonal-folders", before.Folders == after.Folders, before.Folders == after.Folders ? "" : $"before={before.Folders} after={after.Folders}");
		Check("readonly-shortcuts", before.Links == after.Links, $"{before.LinkCount} shortcut(s) hashed" + (before.Links == after.Links ? "" : $"; before={before.Links} after={after.Links}"));
		Check("readonly-registry", before.Registry == after.Registry, before.Registry == after.Registry ? "" : $"before={before.Registry} after={after.Registry}");
		Check("no-new-files-outside-outdir", before.Outside == after.Outside, before.Outside == after.Outside ? "" : $"before={before.Outside} after={after.Outside}");
		Metrics["Log"] = log.ToArray();
		Metrics["Renders"] = Directory.GetFiles(outDir, "*.png").Select(Path.GetFileName).OrderBy(n => n).ToArray();

		bool passed = Checks.All(c => c.Pass);
		StringBuilder txt = new StringBuilder();
		foreach ((string name, bool pass, string detail) in Checks)
		{
			txt.AppendLine((pass ? "PASS " : "FAIL ") + name + (detail.Length > 0 ? ": " + detail : ""));
		}
		File.WriteAllText(Path.Combine(outDir, "seasonaliconstest-results.txt"), txt.ToString());
		var report = new
		{
			SchemaVersion = 1,
			GeneratedUtc = DateTime.UtcNow,
			Passed = passed,
			Checks = Checks.Select(c => new { c.Name, c.Pass, c.Detail }).ToArray(),
			Metrics
		};
		File.WriteAllText(Path.Combine(outDir, "seasonaliconstest-report.json"), JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
		Logger.Log($"SEASONAL-ICONS-QA: passed={passed} checks={Checks.Count} failed={Checks.Count(c => !c.Pass)} -> {outDir}");
		return passed;
	}

	private static void Check(string name, bool pass, string detail = "")
	{
		Checks.Add((name, pass, detail));
	}

	// ===================================================================== fixture

	// <outDir>\fixture\seasonal\halloween-icons: five keys with masters and every pre-sized frame, a key with small frames
	// only, a key with a master only, and aliases.txt; christmas-icons is empty. Each frame is filled with a colour that
	// encodes its own size (R = size & 0xFF, B = 0x80 | size >> 8) and its key (G), inside a 1 px white border, so the
	// source frame of any output can be read back from its centre pixel.
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
		for (int k = 0; k < FixtureKeys.Length; k++)
		{
			WriteFrame(Path.Combine(hall, FixtureKeys[k] + ".png"), 512, k + 1);
			foreach (int size in FixtureSizes)
			{
				WriteFrame(Path.Combine(hall, $"{FixtureKeys[k]}-{size}.png"), size, k + 1);
			}
		}
		WriteFrame(Path.Combine(hall, "onlysmall-16.png"), 16, 6);
		WriteFrame(Path.Combine(hall, "onlysmall-24.png"), 24, 6);
		WriteFrame(Path.Combine(hall, "masteronly.png"), 512, 7);
		File.WriteAllText(Path.Combine(hall, "aliases.txt"), "# QA aliases\r\ndiscord = discordptb\r\n");
		return root;
	}

	private static void WriteFrame(string path, int size, int keyIndex)
	{
		byte[] px = new byte[size * size * 4];
		for (int y = 0; y < size; y++)
		{
			for (int x = 0; x < size; x++)
			{
				int i = (y * size + x) * 4;
				bool border = x == 0 || y == 0 || x == size - 1 || y == size - 1;
				px[i] = border ? (byte)255 : (byte)(0x80 | (size >> 8));
				px[i + 1] = border ? (byte)255 : (byte)keyIndex;
				px[i + 2] = border ? (byte)255 : (byte)(size & 0xFF);
				px[i + 3] = 255;
			}
		}
		BitmapSource bmp = BitmapSource.Create(size, size, 96.0, 96.0, PixelFormats.Bgra32, null, px, size * 4);
		SavePng(bmp, path);
	}

	private static int SourceSize(Color c)
	{
		return c.R + ((c.B & 0x7F) << 8);
	}

	// ===================================================================== settings

	private static AppSettings Settings(string season, bool icons, bool replace81, string size)
	{
		AppSettings s = _real.Clone();
		s.SeasonalActive = season;
		s.SeasonalAppIcons = icons;
		s.Replace81AppIcons = replace81;
		s.TaskbarSize = size;
		s.TileScale = 1.0;
		return s;
	}

	// Sets the diagnostics settings and reconciles, as the PC Settings season tiles and toggle do.
	private static void Use(string season, bool icons = true, bool replace81 = true, string size = "Medium")
	{
		SettingsStore.DiagnosticsOverride = Settings(season == SeasonalTheme.Normal ? "" : season, icons, replace81, size);
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

	private static bool WaitUntil(Func<bool> done, int ms)
	{
		Stopwatch sw = Stopwatch.StartNew();
		while (sw.ElapsedMilliseconds < ms)
		{
			Pump();
			if (done())
			{
				return true;
			}
			Thread.Sleep(20);
		}
		Pump();
		return done();
	}

	// ===================================================================== A. matching

	private static void MatchingChecks()
	{
		Use(SeasonalTheme.Halloween);
		(string Name, SeasonalIdentity Id, string? Want)[] rows = new (string, SeasonalIdentity, string?)[]
		{
			("aumid-squirrel-discord", new SeasonalIdentity(null, null, "com.squirrel.Discord.Discord", null, null), "discord"),
			("squirrel-update-processstart", new SeasonalIdentity("C:\\QA\\Discord\\Update.exe", "--processStart Discord.exe", null, null, null), "discord"),
			("discord-app-folder-exe", new SeasonalIdentity("C:\\QA\\Discord\\app-1.0.9200\\Discord.exe", null, null, null, null), "discord"),
			("aumid-chrome", new SeasonalIdentity(null, null, "Chrome", "Google Chrome", null), "chrome"),
			("chrome-exe-no-aumid", new SeasonalIdentity("C:\\QA\\Google\\Chrome\\Application\\chrome.exe", null, null, null, null), "chrome"),
			("chrome-pwa-aumid", new SeasonalIdentity("C:\\QA\\Google\\Chrome\\Application\\chrome.exe", null, "Chrome._crx_abc", null, null), null),
			("chrome-proxy-app-id", new SeasonalIdentity("C:\\QA\\Google\\Chrome\\Application\\chrome_proxy.exe", "--profile-directory=Default --app-id=x", null, null, null), null),
			("slack-packaged", new SeasonalIdentity(null, null, "com.tinyspeck.slackdesktop_8yrtsj140pw4g!Slack", null, null), "slack"),
			("spotify-store", new SeasonalIdentity(null, null, "SpotifyAB.SpotifyMusic_zpdnekdrzrea0!Spotify", null, null), "spotify"),
			("spotify-appsfolder-path", new SeasonalIdentity(null, null, "shell:AppsFolder\\C:\\X\\Spotify\\Spotify.exe", null, null), "spotify"),
			("spotify-knownfolder-path", new SeasonalIdentity(null, null, "{6D809377-6AF0-444B-8957-A3773F02200E}\\QA\\Spotify.exe", "Music player", null), "spotify"),
			("notion-id", new SeasonalIdentity(null, null, "notion.id", null, null), "notion"),
			("notion-calendar", new SeasonalIdentity(null, null, "com.cron.electron", "Notion Calendar", null), null),
			("chrome-remote-desktop", new SeasonalIdentity(null, null, "Chrome._crx_inomeogfingihgjfjlpeplalcfajhgai", "Chrome Remote Desktop", null), null),
			("alias-discordptb", new SeasonalIdentity("C:\\QA\\DiscordPTB\\app-1.0\\DiscordPTB.exe", null, null, null, null), "discord"),
			("explorer-never", new SeasonalIdentity("C:\\Windows\\explorer.exe", null, "com.squirrel.Discord.Discord", "Discord", null), null),
			("applicationframehost-alone", new SeasonalIdentity("C:\\Windows\\System32\\ApplicationFrameHost.exe", null, null, null, null), null),
			// A Store app's window runs in ApplicationFrameHost: the host is ignored and the window's AUMID decides.
			("applicationframehost-store-aumid", new SeasonalIdentity("C:\\Windows\\System32\\ApplicationFrameHost.exe", null, "SpotifyAB.SpotifyMusic_zpdnekdrzrea0!Spotify", null, null), "spotify"),
			("wwahost-store-aumid", new SeasonalIdentity("C:\\Windows\\System32\\WWAHost.exe", null, "com.tinyspeck.slackdesktop_8yrtsj140pw4g!Slack", null, null), "slack"),
			("rundll32-alone", new SeasonalIdentity("C:\\Windows\\System32\\rundll32.exe", "shell32.dll,Control_RunDLL", null, null, null), null),
			("office-word", new SeasonalIdentity(null, null, "Microsoft.Office.WINWORD.EXE.15", "Word", null), null),
			("display-name-whole", new SeasonalIdentity(null, null, null, "Discord", null), "discord"),
			("unknown-app", new SeasonalIdentity("C:\\Windows\\System32\\notepad.exe", null, "Microsoft.WindowsNotepad_8wekyb3d8bbwe!App", "Notepad", null), null),
			("hash-like-aumid", new SeasonalIdentity(null, null, "308046B0AF4A39CB", null, null), null)
		};
		foreach ((string name, SeasonalIdentity id, string? want) in rows)
		{
			string? got = SeasonalAppIcons.KeyForIdentity(id);
			Check("match-" + name, got == want, $"got={got ?? "null"} want={want ?? "null"}");
		}
		Expect("match-tray-process-Discord", SeasonalAppIcons.KeyForProcess("Discord", null), "discord");
		Expect("match-tray-process-slack", SeasonalAppIcons.KeyForProcess("slack", null), "slack");
		Expect("match-tray-process-explorer", SeasonalAppIcons.KeyForProcess("explorer", null), null);
		Expect("match-window-exe", SeasonalAppIcons.KeyForWindow(IntPtr.Zero, "C:\\QA\\Discord\\app-1.0.9200\\Discord.exe", ""), "discord");
		Expect("match-window-pwa", SeasonalAppIcons.KeyForWindow(IntPtr.Zero, "C:\\QA\\Google\\Chrome\\Application\\chrome.exe", "Chrome._crx_abc"), null);
		Expect("match-window-store-app", SeasonalAppIcons.KeyForWindow(IntPtr.Zero, "C:\\Windows\\System32\\ApplicationFrameHost.exe", "SpotifyAB.SpotifyMusic_zpdnekdrzrea0!Spotify"), "spotify");
		Expect("match-window-host-no-aumid", SeasonalAppIcons.KeyForWindow(IntPtr.Zero, "C:\\Windows\\System32\\ApplicationFrameHost.exe", ""), null);
		Expect("match-window-explorer-aumid", SeasonalAppIcons.KeyForWindow(IntPtr.Zero, "C:\\Windows\\explorer.exe", "Microsoft.Windows.Explorer"), null);
		Expect("match-tray-process-host", SeasonalAppIcons.KeyForProcess("ApplicationFrameHost", null), null);
		Expect("match-entry-appsfolder-path", SeasonalAppIcons.KeyForEntry(Entry("Music player", "shell:AppsFolder\\C:\\QA\\Spotify\\Spotify.exe", "C:\\QA\\Spotify\\Spotify.exe")), "spotify");
		Expect("match-entry-exe", SeasonalAppIcons.KeyForEntry(Entry("Chat", "C:\\QA\\Discord\\app-1.0.9200\\Discord.exe", null)), "discord");
		Expect("match-entry-win81", SeasonalAppIcons.KeyForEntry(Entry("Discord", "win81:desktop", null)), null);
		Expect("match-entry-qa", SeasonalAppIcons.KeyForEntry(Entry("Discord", "qa:x", null)), null);
		Expect("match-entry-launcher", SeasonalAppIcons.KeyForEntry(Entry("Discord", ActionRouter.Scheme + "discord", null)), null);
		Expect("match-entry-web-link", SeasonalAppIcons.KeyForEntry(Entry("Discord", "https://discord.com/app", null)), null);
		Expect("match-pin-squirrel", SeasonalAppIcons.KeyForPin(new PinnedApp { Name = "Chat pin", LaunchPath = "C:\\QA\\Discord\\Update.exe", Args = "--processStart Discord.exe" }), "discord");
		Expect("match-pin-imported", SeasonalAppIcons.KeyForPin(new PinnedApp { Name = "Chat pin", LaunchPath = "C:\\QA\\Discord\\Update.exe", Args = "--processStart Discord.exe", ExePath = "C:\\QA\\Discord\\app-1.0.9200\\Discord.exe" }), "discord");
		Expect("match-pin-pwa", SeasonalAppIcons.KeyForPin(new PinnedApp { Name = "Web app", LaunchPath = "C:\\QA\\Google\\Chrome\\Application\\chrome_proxy.exe", Args = "--profile-directory=Default --app-id=abc", ExePath = "C:\\QA\\Google\\Chrome\\Application\\chrome_proxy.exe" }), null);
		Expect("match-pin-appsfolder", SeasonalAppIcons.KeyForPin(new PinnedApp { Name = "Notes", LaunchPath = "shell:AppsFolder\\notion.id" }), "notion");
	}

	private static void Expect(string name, string? got, string? want)
	{
		Check(name, got == want, $"got={got ?? "null"} want={want ?? "null"}");
	}

	private static AppEntry Entry(string name, string launchPath, string? appId)
	{
		SolidColorBrush b = new SolidColorBrush(Color.FromRgb(0x2E, 0x8D, 0xEF));
		b.Freeze();
		return new AppEntry
		{
			Name = name,
			LaunchPath = launchPath,
			AppId = appId,
			TileBrush = b
		};
	}

	// ===================================================================== B. gating

	private static void GatingChecks()
	{
		AppEntry discord = Entry("Discord", "shell:AppsFolder\\com.squirrel.Discord.Discord", "com.squirrel.Discord.Discord");
		PinnedApp pin = new PinnedApp { Name = "Discord", LaunchPath = "C:\\QA\\Discord\\Update.exe", Args = "--processStart Discord.exe" };
		SeasonalIdentity id = new SeasonalIdentity(null, null, "com.squirrel.Discord.Discord", "Discord", null);
		foreach ((string label, string season, bool icons) in new (string, string, bool)[3] { ("normal", "", true), ("toggle-off", SeasonalTheme.Halloween, false), ("christmas-empty", SeasonalTheme.Christmas, true) })
		{
			Use(season, icons);
			int io = SeasonalAppIcons.IoCount;
			int hits = 0;
			for (int i = 0; i < 10000; i++)
			{
				if (SeasonalAppIcons.KeyForIdentity(id) != null || SeasonalAppIcons.KeyForEntry(discord) != null || SeasonalAppIcons.KeyForPin(pin) != null
					|| SeasonalAppIcons.KeyForWindow(IntPtr.Zero, "C:\\QA\\Discord\\Discord.exe", "com.squirrel.Discord.Discord") != null
					|| SeasonalAppIcons.KeyForProcess("Discord", null) != null
					|| SeasonalAppIcons.Frame("discord", 24) != null || SeasonalAppIcons.IsActive)
				{
					hits++;
				}
			}
			int ioAfter = SeasonalAppIcons.IoCount;
			Check("gating-" + label, hits == 0 && ioAfter == io, $"non-null answers={hits} io {io}->{ioAfter} active={SeasonalAppIcons.ActiveSeason}");
		}
		Use(SeasonalTheme.Halloween);
		Check("gating-halloween", SeasonalAppIcons.IsActive && SeasonalAppIcons.KeyForIdentity(id) == "discord" && SeasonalAppIcons.KeyForEntry(discord) == "discord" && SeasonalAppIcons.KeyForPin(pin) == "discord",
			$"active={SeasonalAppIcons.ActiveSeason} keys={string.Join(",", SeasonalAppIcons.KeysFor(SeasonalTheme.Halloween))}");

		// Self-heal: the settings change without a Reconcile (another writer); the next lookup already answers for the new
		// values, and exactly one Reconcile is posted, which then refreshes the surfaces.
		Use(SeasonalTheme.Normal);
		int posts = SeasonalAppIcons.SelfHealPosts;
		int version = SeasonalAppIcons.Version;
		SettingsStore.DiagnosticsOverride = Settings(SeasonalTheme.Halloween, true, true, "Medium");
		bool now = SeasonalAppIcons.IsActive;
		for (int i = 0; i < 200; i++)
		{
			now &= SeasonalAppIcons.KeyForIdentity(id) == "discord";
		}
		int posted = SeasonalAppIcons.SelfHealPosts - posts;
		bool versionBefore = SeasonalAppIcons.Version == version;
		Pump();
		bool reconciled = SeasonalAppIcons.Version == version + 1;
		Check("gating-self-heal", now && posted == 1 && versionBefore && reconciled, $"immediate={now} posts={posted} reconciledAfterPump={reconciled}");
		Use(SeasonalTheme.Normal);
	}

	// ===================================================================== C. frames

	private static void FrameChecks(string fixture, List<string> log)
	{
		Use(SeasonalTheme.Halloween);
		string hall = Path.Combine(fixture, "halloween-icons");
		int[] sizes = new int[18] { 16, 18, 20, 22, 24, 27, 32, 34, 36, 40, 48, 64, 76, 96, 112, 128, 256, 300 };
		List<string> shapeFail = new List<string>();
		List<string> sourceFail = new List<string>();
		List<string> exactFail = new List<string>();
		Dictionary<string, int> sources = new Dictionary<string, int>();
		foreach (int px in sizes)
		{
			if (SeasonalAppIcons.Frame("discord", px) is not BitmapSource f)
			{
				shapeFail.Add($"{px}: null");
				continue;
			}
			if (f.PixelWidth != px || f.PixelHeight != px || Math.Abs(f.DpiX - 96.0) > 0.01 || Math.Abs(f.DpiY - 96.0) > 0.01 || f.Format != PixelFormats.Pbgra32 || !f.IsFrozen)
			{
				shapeFail.Add($"{px}: {f.PixelWidth}x{f.PixelHeight} dpi={f.DpiX} {f.Format} frozen={f.IsFrozen}");
			}
			Color c = Pixel(f, px / 2, px / 2);
			int src = SourceSize(c);
			int want = ExpectedSource(px, FixtureSizes.Concat(new int[1] { 512 }));
			sources[px.ToString()] = src;
			if (src != want || c.G != 1)
			{
				sourceFail.Add($"{px}<-{src} (want {want}, key {c.G})");
			}
			if (FixtureSizes.Contains(px))
			{
				byte[] a = Bytes(f);
				byte[] b = Bytes(Decode(Path.Combine(hall, $"discord-{px}.png")));
				if (!a.SequenceEqual(b))
				{
					exactFail.Add(px.ToString());
				}
			}
		}
		Metrics["DiscordFrameSources"] = sources;
		Check("frames-shape", shapeFail.Count == 0, shapeFail.Count == 0 ? $"{sizes.Length} sizes: exact px, 96 dpi, Pbgra32, frozen" : string.Join("; ", shapeFail));
		Check("frames-source", sourceFail.Count == 0, sourceFail.Count == 0 ? string.Join(" ", sources.Select(kv => kv.Key + "<-" + kv.Value)) : string.Join("; ", sourceFail));
		Check("frames-exact-pixel-identical", exactFail.Count == 0, exactFail.Count == 0 ? "16 20 24 32 40 48 64 96 128 256 equal to their files" : "differ: " + string.Join(",", exactFail));

		// Small frames only: 48 is scaled up from 24, logged once.
		SeasonalAppIcons.QaResetLogOnce();
		int logBefore = CountLog(log, "onlysmall has no frame of 48 px");
		ImageSource? up = SeasonalAppIcons.Frame("onlysmall", 48);
		ImageSource? upAgain = SeasonalAppIcons.Frame("onlysmall", 48);
		int logAfter = CountLog(log, "onlysmall has no frame of 48 px");
		bool upOk = up is BitmapSource ub && ub.PixelWidth == 48 && SourceSize(Pixel(ub, 24, 24)) == 24 && Pixel(ub, 24, 24).G == 6 && ReferenceEquals(up, upAgain);
		Check("frames-upscale-only-smaller", upOk && logAfter - logBefore == 1, $"48<-{(up is BitmapSource u2 ? SourceSize(Pixel(u2, 24, 24)) : -1)} log lines={logAfter - logBefore}");
		ImageSource? small20 = SeasonalAppIcons.Frame("onlysmall", 20);
		Check("frames-onlysmall-down", small20 is BitmapSource s20 && s20.PixelWidth == 20 && SourceSize(Pixel(s20, 10, 10)) == 24, "20<-24");
		ImageSource? master16 = SeasonalAppIcons.Frame("masteronly", 16);
		Check("frames-master-only", master16 is BitmapSource m16 && m16.PixelWidth == 16 && SourceSize(Pixel(m16, 8, 8)) == 512 && Pixel(m16, 8, 8).G == 7, "16<-512");

		// The same frame object until something changes; a new one after an effective change.
		ImageSource? a1 = SeasonalAppIcons.Frame("discord", 24);
		ImageSource? a2 = SeasonalAppIcons.Frame("discord", 24);
		Use(SeasonalTheme.Halloween, icons: false);
		Use(SeasonalTheme.Halloween, icons: true);
		ImageSource? a3 = SeasonalAppIcons.Frame("discord", 24);
		Check("frames-cached-then-renewed", a1 != null && ReferenceEquals(a1, a2) && a3 != null && !ReferenceEquals(a1, a3), $"same={ReferenceEquals(a1, a2)} renewed={!ReferenceEquals(a1, a3)}");

		// Px follows the sharpest monitor; this machine runs at 100 %.
		Metrics["MaxMonitorScale"] = AppIconOverrides.MaxMonitorScale();
		Check("frames-px", SeasonalAppIcons.Px(24.0) == (int)Math.Ceiling(24.0 * AppIconOverrides.MaxMonitorScale() - 0.0001), $"Px(24)={SeasonalAppIcons.Px(24.0)} Px(112)={SeasonalAppIcons.Px(112.0)}");
		Check("frames-keys", string.Join(",", SeasonalAppIcons.KeysFor(SeasonalTheme.Halloween)) == "chrome,discord,masteronly,notion,onlysmall,slack,spotify" && SeasonalAppIcons.KeysFor(SeasonalTheme.Christmas).Count == 0,
			"halloween=" + string.Join(",", SeasonalAppIcons.KeysFor(SeasonalTheme.Halloween)) + " christmas=" + SeasonalAppIcons.KeysFor(SeasonalTheme.Christmas).Count);
	}

	// The frame a size is made from: the exact frame, else the smallest at least twice as large, else the largest
	// larger one, else (scaling up) the largest.
	private static int ExpectedSource(int px, IEnumerable<int> available)
	{
		int[] sizes = available.ToArray();
		if (sizes.Contains(px))
		{
			return px;
		}
		int[] twice = sizes.Where(s => s >= 2 * px).ToArray();
		if (twice.Length > 0)
		{
			return twice.Min();
		}
		int[] larger = sizes.Where(s => s > px).ToArray();
		return larger.Length > 0 ? larger.Max() : sizes.Max();
	}

	private static int CountLog(List<string> log, string text)
	{
		lock (log)
		{
			return log.Count(l => l.Contains(text, StringComparison.Ordinal));
		}
	}

	// ===================================================================== D/E. Start

	private sealed class Probe
	{
		public required AppEntry Entry;

		public required string? Key;

		public required ImageSource Native;

		public bool Over;

		public (ImageSource? Icon, Brush TileBrush, bool IsOver, Brush? OverBrush) Snap;
	}

	private static ImageSource NativeIcon(int index, int px = 128)
	{
		Color[] colors = new Color[10]
		{
			Color.FromRgb(0x58, 0x65, 0xF2), Color.FromRgb(0x34, 0xA8, 0x53), Color.FromRgb(0x61, 0x1F, 0x69), Color.FromRgb(0x1E, 0xD7, 0x60), Color.FromRgb(0x30, 0x30, 0x30),
			Color.FromRgb(0x3C, 0x8C, 0xD9), Color.FromRgb(0xFF, 0x00, 0x33), Color.FromRgb(0x00, 0x75, 0x00), Color.FromRgb(0x77, 0x4B, 0xA8), Color.FromRgb(0xC0, 0x60, 0x20)
		};
		DrawingVisual dv = new DrawingVisual();
		using (DrawingContext dc = dv.RenderOpen())
		{
			dc.DrawEllipse(new SolidColorBrush(colors[index % colors.Length]), null, new Point(px / 2.0, px / 2.0), px * 0.45, px * 0.45);
			dc.DrawRectangle(Brushes.White, null, new Rect(px * 0.35, px * 0.35, px * 0.3, px * 0.3));
		}
		RenderTargetBitmap rtb = new RenderTargetBitmap(px, px, 96.0, 96.0, PixelFormats.Pbgra32);
		rtb.Render(dv);
		rtb.Freeze();
		return rtb;
	}

	private static void StartChecks(string outDir, StartScreen start)
	{
		Use(SeasonalTheme.Normal);
		const string calcId = "Microsoft.WindowsCalculator_8wekyb3d8bbwe!App";
		List<Probe> probes = new List<Probe>
		{
			new Probe { Entry = Entry("Discord", "shell:AppsFolder\\com.squirrel.Discord.Discord", "com.squirrel.Discord.Discord"), Key = "discord", Native = NativeIcon(0) },
			new Probe { Entry = Entry("Google Chrome", "shell:AppsFolder\\Chrome", "Chrome"), Key = "chrome", Native = NativeIcon(1) },
			new Probe { Entry = Entry("Slack", "shell:AppsFolder\\com.tinyspeck.slackdesktop_8yrtsj140pw4g!Slack", "com.tinyspeck.slackdesktop_8yrtsj140pw4g!Slack"), Key = "slack", Native = NativeIcon(2) },
			new Probe { Entry = Entry("Spotify", "shell:AppsFolder\\C:\\QA\\Spotify\\Spotify.exe", "C:\\QA\\Spotify\\Spotify.exe"), Key = "spotify", Native = NativeIcon(3) },
			new Probe { Entry = Entry("Notion", "shell:AppsFolder\\notion.id", "notion.id"), Key = "notion", Native = NativeIcon(4) },
			new Probe { Entry = Entry("Notepad", "C:\\QA\\Windows\\notepad.exe", null), Key = null, Native = NativeIcon(5) },
			new Probe { Entry = Entry("YouTube", "shell:AppsFolder\\Chrome._crx_agimnkijcaahngcdmfeangaknmldooml", "Chrome._crx_agimnkijcaahngcdmfeangaknmldooml"), Key = null, Native = NativeIcon(6) },
			new Probe { Entry = Entry("Calculator", "shell:AppsFolder\\" + calcId, calcId), Key = null, Native = NativeIcon(7), Over = true },
			// A seasonal app that also has a Win8.1 icon (matched by its name): exercises the 8.1 toggle underneath.
			new Probe { Entry = Entry("Discord", "shell:AppsFolder\\" + calcId, calcId), Key = "discord", Native = NativeIcon(8), Over = true }
		};
		start.QaInjectApps(probes.Select(p => p.Entry));
		LoadProbes(start, probes, replace81: true);
		Check("start-load-normal", probes.All(p => p.Entry.Icon != null && p.Entry.SeasonalKey == null && p.Entry.IsOverrideIcon == p.Over),
			string.Join(" ", probes.Select(p => $"{p.Entry.Name}:{(p.Entry.Icon != null ? "icon" : "none")}{(p.Entry.IsOverrideIcon ? "/8.1" : "")}")));
		foreach (Probe p in probes)
		{
			p.Snap = (p.Entry.Icon, p.Entry.TileBrush, p.Entry.IsOverrideIcon, p.Entry.OverrideBrush);
		}
		Probe discord = probes[0];
		RenderTiles(start, Path.Combine(outDir, "start-tiles-before.png"), probes, StartBackground(SeasonalTheme.Normal));
		RenderAllApps(Path.Combine(outDir, "allapps-before.png"), probes, StartBackground(SeasonalTheme.Normal));
		RenderSearchRows(Path.Combine(outDir, "searchrow-before.png"), probes);

		// Halloween: matched entries show their frame and its tile colour; the others are untouched.
		List<(AppEntry Entry, string Prop, object? Value, bool KeyApplies)> events = new List<(AppEntry, string, object?, bool)>();
		foreach (Probe p in probes)
		{
			p.Entry.PropertyChanged += (s, e) =>
			{
				AppEntry ae = (AppEntry)s!;
				object? v = e.PropertyName == "Icon" ? ae.Icon : null;
				events.Add((ae, e.PropertyName ?? "", v, SeasonalAppIcons.KeyForEntry(ae) != null));
			};
		}
		Use(SeasonalTheme.Halloween);
		CheckSeasonal("start-halloween-applied", probes);
		TileColourChecks("tile-colours-fixture", FixtureKeys, SeasonalTheme.Halloween);
		RenderTiles(start, Path.Combine(outDir, "start-tiles-halloween.png"), probes, StartBackground(SeasonalTheme.Halloween));
		RenderAllApps(Path.Combine(outDir, "allapps-halloween.png"), probes, StartBackground(SeasonalTheme.Halloween));
		RenderSearchRows(Path.Combine(outDir, "searchrow-halloween.png"), probes);
		Use(SeasonalTheme.Normal);
		CheckSnapshot("start-normal-restores-exactly", probes);
		RenderTiles(start, Path.Combine(outDir, "start-tiles-normal.png"), probes, StartBackground(SeasonalTheme.Normal));
		RenderAllApps(Path.Combine(outDir, "allapps-normal.png"), probes, StartBackground(SeasonalTheme.Normal));
		RenderSearchRows(Path.Combine(outDir, "searchrow-normal.png"), probes);
		(int diff, int total) = PixelDiff(Path.Combine(outDir, "start-tiles-before.png"), Path.Combine(outDir, "start-tiles-normal.png"));
		(int diff2, int total2) = PixelDiff(Path.Combine(outDir, "allapps-before.png"), Path.Combine(outDir, "allapps-normal.png"));
		(int diff3, int total3) = PixelDiff(Path.Combine(outDir, "searchrow-before.png"), Path.Combine(outDir, "searchrow-normal.png"));
		Check("start-tiles-normal-equals-before", diff == 0 && diff2 == 0, $"tiles {diff}/{total} differing, all apps {diff2}/{total2}");
		Check("searchrow-normal-equals-before", diff3 == 0, $"{diff3}/{total3} differing");

		// Halloween -> Christmas (no icons) -> Normal
		Use(SeasonalTheme.Halloween);
		Use(SeasonalTheme.Christmas);
		CheckSnapshot("start-christmas-empty-restores", probes);
		Use(SeasonalTheme.Normal);
		CheckSnapshot("start-christmas-then-normal", probes);
		// Halloween -> toggle off -> on -> Normal
		Use(SeasonalTheme.Halloween);
		Use(SeasonalTheme.Halloween, icons: false);
		CheckSnapshot("start-toggle-off-restores", probes);
		Use(SeasonalTheme.Halloween, icons: true);
		CheckSeasonal("start-toggle-on-applies", probes);
		Use(SeasonalTheme.Normal);
		CheckSnapshot("start-toggle-then-normal", probes);

		// No flash: while a key applies, no Icon change carries the entry's own icon.
		HashSet<ImageSource> natives = new HashSet<ImageSource>(probes.Where(p => p.Snap.Icon != null).Select(p => p.Snap.Icon!), ReferenceEqualityComparer.Instance);
		List<string> flashes = events.Where(e => e.Prop == "Icon" && e.KeyApplies && e.Value is ImageSource v && natives.Contains(v)).Select(e => e.Entry.Name).ToList();
		Metrics["StartPropertyEvents"] = events.Count;

		// Idle release under Halloween: reverted, then released; the reload puts the seasonal icon back on.
		Use(SeasonalTheme.Halloween);
		int released = start.QaReleaseDeferredIcons();
		bool releasedClean = discord.Entry.Icon == null && discord.Entry.SeasonalKey == null && discord.Entry.SeasonalUnder == null && !discord.Entry.IsOverrideIcon;
		events.Clear();
		start.QaQueueIconBatch(new List<(AppEntry, ImageSource, bool)> { (discord.Entry, discord.Native, false) });
		Pump();
		ImageSource? want = SeasonalAppIcons.Frame("discord", SeasonalAppIcons.Px(112.0));
		bool reloaded = discord.Entry.SeasonalKey == "discord" && ReferenceEquals(discord.Entry.Icon, want) && discord.Entry.SeasonalUnder?.Icon == discord.Native;
		flashes.AddRange(events.Where(e => e.Prop == "Icon" && e.KeyApplies && e.Value is ImageSource v && (natives.Contains(v) || ReferenceEquals(v, discord.Native))).Select(e => e.Entry.Name));
		Check("start-idle-release-under-halloween", released > 0 && releasedClean && reloaded, $"released={released} clean={releasedClean} reloaded={reloaded}");
		Check("start-no-flash", flashes.Count == 0, flashes.Count == 0 ? $"{Metrics["StartPropertyEvents"]} + {events.Count} property changes recorded" : "native icon shown while seasonal: " + string.Join(",", flashes));
		Use(SeasonalTheme.Normal);
		// The released entries reload in Normal (as the deferred loader would) for the remaining checks.
		LoadProbes(start, probes, replace81: true);
		foreach (Probe p in probes)
		{
			p.Snap = (p.Entry.Icon, p.Entry.TileBrush, p.Entry.IsOverrideIcon, p.Entry.OverrideBrush);
		}

		// The 8.1 toggle flips while Halloween is on: still seasonal; Normal shows the 8.1 state of the new setting.
		Probe discord81 = probes[8];
		Use(SeasonalTheme.Halloween, replace81: true);
		SettingsStore.DiagnosticsOverride = Settings(SeasonalTheme.Halloween, true, false, "Medium");
		start.QaRefreshAppIcons();
		Pump();
		bool stillSeasonal = discord81.Entry.SeasonalKey == "discord" && discord81.Entry.SeasonalUnder != null && !discord81.Entry.SeasonalUnder.IsOverrideIcon && discord81.Entry.SeasonalUnder.OverrideBrush == null;
		Use(SeasonalTheme.Normal, replace81: false);
		bool normalNo81 = discord81.Entry.SeasonalKey == null && !discord81.Entry.IsOverrideIcon && discord81.Entry.OverrideBrush == null && discord81.Entry.Icon != null;
		start.QaRefreshAppIcons();
		Use(SeasonalTheme.Halloween, replace81: true);
		start.QaRefreshAppIcons();
		Pump();
		bool stillSeasonal2 = discord81.Entry.SeasonalKey == "discord" && discord81.Entry.SeasonalUnder != null && discord81.Entry.SeasonalUnder.IsOverrideIcon;
		Use(SeasonalTheme.Normal, replace81: true);
		bool normal81 = discord81.Entry.SeasonalKey == null && discord81.Entry.IsOverrideIcon && discord81.Entry.OverrideBrush != null;
		Check("start-81-toggle-underneath", stillSeasonal && normalNo81 && stillSeasonal2 && normal81, $"off:seasonal={stillSeasonal} normal-native={normalNo81}; on:seasonal={stillSeasonal2} normal-8.1={normal81}");

		// Pinning while seasonal upgrades the icon underneath (what Normal shows), not the seasonal one.
		AppEntry pinEntry = Entry("Discord", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "notepad.exe"), null);
		ImageSource smallNative = NativeIcon(9, 32);
		start.QaInjectApps(new AppEntry[1] { pinEntry });
		Use(SeasonalTheme.Halloween);
		start.QaQueueIconBatch(new List<(AppEntry, ImageSource, bool)> { (pinEntry, smallNative, false) });
		Pump();
		bool pinSeasonal = pinEntry.SeasonalKey == "discord" && pinEntry.SeasonalUnder?.Icon == smallNative;
		start.QaUpgradeEntryIconForTile(pinEntry);
		bool upgraded = WaitUntil(() => pinEntry.SeasonalUnder != null && !ReferenceEquals(pinEntry.SeasonalUnder.Icon, smallNative), 8000);
		int upgradedPx = pinEntry.SeasonalUnder?.Icon is BitmapSource ub ? Math.Max(ub.PixelWidth, ub.PixelHeight) : 0;
		Check("start-pin-upgrades-underneath", pinSeasonal && upgraded && pinEntry.SeasonalKey == "discord" && ReferenceEquals(pinEntry.Icon, SeasonalAppIcons.Frame("discord", SeasonalAppIcons.Px(112.0))),
			$"seasonal={pinSeasonal} upgraded={upgraded} underneath={upgradedPx}px");
		Use(SeasonalTheme.Normal);
		Check("start-pin-normal-shows-upgraded", pinEntry.SeasonalKey == null && pinEntry.Icon != null && !ReferenceEquals(pinEntry.Icon, smallNative), $"icon={(pinEntry.Icon is BitmapSource pb ? pb.PixelWidth + "px" : "none")}");

		// Folder tiles follow their members (no edit of their own): a refresh reaches the members of a folder tile.
		AppEntry member = Entry("Notion", "shell:AppsFolder\\notion.id", "notion.id");
		member.Icon = NativeIcon(4);
		TileVm folder = FolderTiles.MakeFolder("QA folder", new AppEntry[1] { member });
		start.QaAddTiles(new TileVm[1] { folder });
		Use(SeasonalTheme.Halloween);
		bool memberSeasonal = member.SeasonalKey == "notion";
		Use(SeasonalTheme.Normal);
		Check("start-folder-members", memberSeasonal && member.SeasonalKey == null, $"member seasonal={memberSeasonal}");
	}

	private static void LoadProbes(StartScreen start, List<Probe> probes, bool replace81)
	{
		List<(AppEntry, ImageSource, bool)> batch = new List<(AppEntry, ImageSource, bool)>();
		foreach (Probe p in probes)
		{
			ImageSource? over = p.Over && replace81 ? AppIconOverrides.TryGet(p.Entry) : null;
			batch.Add((p.Entry, over ?? p.Native, over != null));
		}
		start.QaQueueIconBatch(batch);
		Pump();
	}

	private static void CheckSeasonal(string name, List<Probe> probes)
	{
		List<string> fail = new List<string>();
		foreach (Probe p in probes)
		{
			AppEntry e = p.Entry;
			if (p.Key != null)
			{
				ImageSource? frame = SeasonalAppIcons.Frame(p.Key, SeasonalAppIcons.Px(112.0));
				if (e.SeasonalKey != p.Key || frame == null || !ReferenceEquals(e.Icon, frame) || !ReferenceEquals(e.TileBrush, SeasonalAppIcons.TileBrushFor(p.Key)) || e.IsOverrideIcon || e.OverrideBrush != null)
				{
					fail.Add($"{e.Name}: key={e.SeasonalKey} frame={ReferenceEquals(e.Icon, frame)} brush={ReferenceEquals(e.TileBrush, SeasonalAppIcons.TileBrushFor(p.Key))} over={e.IsOverrideIcon}");
				}
			}
			else if (!SameAsSnapshot(p))
			{
				fail.Add(e.Name + ": changed");
			}
		}
		Check(name, fail.Count == 0, fail.Count == 0 ? $"{probes.Count(p => p.Key != null)} seasonal, {probes.Count(p => p.Key == null)} untouched" : string.Join("; ", fail));
	}

	private static void CheckSnapshot(string name, List<Probe> probes)
	{
		List<string> fail = probes.Where(p => !SameAsSnapshot(p) || p.Entry.SeasonalKey != null || p.Entry.SeasonalUnder != null).Select(p => p.Entry.Name).ToList();
		Check(name, fail.Count == 0, fail.Count == 0 ? $"{probes.Count} entries: same Icon, TileBrush, IsOverrideIcon, OverrideBrush" : "differ: " + string.Join(",", fail));
	}

	private static bool SameAsSnapshot(Probe p)
	{
		AppEntry e = p.Entry;
		return ReferenceEquals(e.Icon, p.Snap.Icon) && ReferenceEquals(e.TileBrush, p.Snap.TileBrush) && e.IsOverrideIcon == p.Snap.IsOver && ReferenceEquals(e.OverrideBrush, p.Snap.OverBrush);
	}

	// Discord as a Small, Medium, Wide and Large tile, then Chrome, Notepad and Calculator (Win8.1 icon) as Medium tiles,
	// rendered with the real Start tile template on the Start background of the season.
	private static void RenderTiles(StartScreen start, string path, List<Probe> probes, Brush background)
	{
		Canvas canvas = new Canvas { Background = background };
		(AppEntry Entry, TileSize Size)[] tiles = new (AppEntry, TileSize)[7]
		{
			(probes[0].Entry, TileSize.Small), (probes[0].Entry, TileSize.Medium), (probes[0].Entry, TileSize.Wide), (probes[0].Entry, TileSize.Large),
			(probes[1].Entry, TileSize.Medium), (probes[5].Entry, TileSize.Medium), (probes[7].Entry, TileSize.Medium)
		};
		double x = 10.0;
		foreach ((AppEntry entry, TileSize size) in tiles)
		{
			TileVm vm = new TileVm { Entry = entry, Size = size };
			Button b = new Button { Style = (Style)start.FindResource("TileButton"), DataContext = vm };
			Canvas.SetLeft(b, x);
			Canvas.SetTop(b, 10.0);
			canvas.Children.Add(b);
			x += vm.PixelWidth + 10.0;
		}
		int w = (int)x;
		BitmapSource bmp = RenderElement(canvas, w, 330);
		SavePng(bmp, path);
		if (path.Contains("halloween", StringComparison.Ordinal))
		{
			// Centre pixels of the four Discord tiles: Small 36 <- 96, Medium and Wide 76 <- 256, Large 112 <- 256 (a frame
			// at least twice the size).
			int[] centres = new int[4]
			{
				SourceSize(Pixel(bmp, 10 + 35, 10 + 35)),
				SourceSize(Pixel(bmp, 90 + 75, 10 + 75)),
				SourceSize(Pixel(bmp, 250 + 155, 10 + 75)),
				SourceSize(Pixel(bmp, 570 + 155, 10 + 155))
			};
			int[] sizes = FixtureSizes.Concat(new int[1] { 512 }).ToArray();
			int[] want = new int[4] { ExpectedSource(36, sizes), ExpectedSource(76, sizes), ExpectedSource(76, sizes), ExpectedSource(112, sizes) };
			Metrics["TileCentreSources"] = centres;
			Check("render-start-tiles-frames", centres.SequenceEqual(want), "Small/Medium/Wide/Large <- " + string.Join("/", centres) + " (want " + string.Join("/", want) + ")");
		}
	}

	private static void RenderAllApps(string path, List<Probe> probes, Brush background)
	{
		StackPanel panel = new StackPanel { Background = background };
		SolidColorBrush hover = new SolidColorBrush(Color.FromArgb(64, 255, 255, 255));
		SolidColorBrush accent = new SolidColorBrush(Color.FromRgb(0x46, 0x17, 0x8F));
		List<AppListItem> items = new List<AppListItem>();
		foreach (int i in new int[5] { 0, 1, 4, 5, 7 })
		{
			AppListItem item = new AppListItem(probes[i].Entry, hover, accent);
			items.Add(item);
			panel.Children.Add(item);
		}
		int h = (int)(5 * AppsColumnsPanel.ItemHeight);
		BitmapSource bmp = RenderElement(panel, 248, h);
		SavePng(bmp, path);
		foreach (AppListItem item in items)
		{
			item.Dispose();
		}
		if (path.Contains("halloween", StringComparison.Ordinal))
		{
			int src = SourceSize(Pixel(bmp, 22, (int)(AppsColumnsPanel.ItemHeight / 2)));
			Check("render-allapps-frame", src == 24, $"Discord row icon <- {src}");
		}
	}

	// The search charm's app row: the icon binding SearchPane uses, at 24 DIP.
	private static void RenderSearchRows(string path, List<Probe> probes)
	{
		StackPanel panel = new StackPanel { Background = new SolidColorBrush(Color.FromRgb(0x2B, 0x2B, 0x2B)) };
		List<Image> images = new List<Image>();
		foreach (int i in new int[3] { 0, 1, 5 })
		{
			StackPanel row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(10.0, 6.0, 10.0, 6.0) };
			Image img = new Image { Width = 24.0, Height = 24.0, VerticalAlignment = VerticalAlignment.Center };
			RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);
			SearchPane.BindAppIcon(img, probes[i].Entry, 24.0);
			images.Add(img);
			row.Children.Add(img);
			row.Children.Add(new TextBlock { Text = probes[i].Entry.Name, Foreground = Brushes.White, FontFamily = new FontFamily("Segoe UI"), FontSize = 14.0, Margin = new Thickness(12.0, 0.0, 0.0, 0.0), VerticalAlignment = VerticalAlignment.Center });
			panel.Children.Add(row);
		}
		BitmapSource bmp = RenderElement(panel, 300, 108);
		SavePng(bmp, path);
		if (!path.Contains("halloween", StringComparison.Ordinal))
		{
			return;
		}
		ImageSource? frame = SeasonalAppIcons.Frame("discord", SeasonalAppIcons.Px(24.0));
		int src = SourceSize(Pixel(bmp, 10 + 12, 6 + 12));
		Check("render-searchrow-frame", ReferenceEquals(images[0].Source, frame) && ReferenceEquals(images[2].Source, probes[5].Entry.Icon) && src == 24, $"converter returns the 24 frame={ReferenceEquals(images[0].Source, frame)} centre<-{src}");
	}

	// The Start background a season shows (its preset colour and pattern), or the neutral board for Normal.
	private static Brush StartBackground(string season)
	{
		SeasonPreset? p = SeasonalTheme.Find(season);
		if (p == null)
		{
			return new SolidColorBrush(StartColor);
		}
		return StartBackgroundFactory.Build(SeasonalArt.Hex(p.Base), SeasonalArt.Hex(p.Accent), p.Pattern);
	}

	// Every key's seasonal tile colour keeps 1.5:1 against the season's Start background and 4.5:1 for the white label.
	private static void TileColourChecks(string name, IEnumerable<string> keys, string season)
	{
		Color bg = SeasonalArt.Hex(SeasonalTheme.Find(season)!.Base);
		List<string> rows = new List<string>();
		bool ok = true;
		foreach (string k in keys)
		{
			// The active season's brush as Start uses it; for another season, the same art under that season's band.
			Color c = SeasonalAppIcons.ActiveSeason == season
				? ((SolidColorBrush)SeasonalAppIcons.TileBrushFor(k)).Color
				: SeasonalAppIcons.TileColorFor(SeasonalAppIcons.Frame(k, 64) as BitmapSource, season, k);
			double onBg = SeasonalAppIcons.Contrast(c, bg);
			double label = SeasonalAppIcons.Contrast(c, Colors.White);
			ok &= onBg >= 1.5 && label >= 4.5;
			rows.Add($"{k}=#{c.R:X2}{c.G:X2}{c.B:X2} bg {onBg:F2}:1 label {label:F2}:1");
		}
		Metrics[name] = rows;
		Check(name, ok, season + ": " + string.Join("; ", rows));
	}

	// ===================================================================== robustness

	// A second fixture with bad files: a PNG whose data cannot be decoded, a header that claims 4096 px, and a frame
	// whose -n suffix disagrees with its real size. Bad files are skipped (logged once), an entry whose picture cannot
	// be read keeps its own icon, and the real size wins over the suffix.
	private static void RobustnessChecks(string outDir, StartScreen start, List<string> log, string fixture)
	{
		string root = Path.Combine(outDir, "fixture-bad", "seasonal");
		if (Directory.Exists(root))
		{
			Directory.Delete(root, recursive: true);
		}
		string hall = Path.Combine(root, "halloween-icons");
		Directory.CreateDirectory(hall);
		File.WriteAllBytes(Path.Combine(hall, "broken.png"), FakePng(64, 64, 200));
		File.WriteAllBytes(Path.Combine(hall, "huge.png"), FakePng(4096, 4096, 64));
		File.WriteAllBytes(Path.Combine(hall, "notapng.png"), Encoding.ASCII.GetBytes("this is not a picture, just text"));
		WriteFrame(Path.Combine(hall, "mismatch-32.png"), 30, 9);
		SeasonalAppIcons.QaResetLogOnce();
		int logStart;
		lock (log)
		{
			logStart = log.Count;
		}
		SeasonalAppIcons.DiagnosticsRoot = root;
		Use(SeasonalTheme.Halloween);
		string keys = string.Join(",", SeasonalAppIcons.KeysFor(SeasonalTheme.Halloween));
		ImageSource? broken = SeasonalAppIcons.Frame("broken", 24);
		ImageSource? brokenAgain = SeasonalAppIcons.Frame("broken", 24);
		ImageSource? mismatch = SeasonalAppIcons.Frame("mismatch", 30);
		IReadOnlyList<(int Px, string Path, bool Sized)> mismatchFrames = SeasonalAppIcons.QaFrames("mismatch");
		AppEntry entry = Entry("Broken", "C:\\QA\\Broken\\broken.exe", null);
		ImageSource native = NativeIcon(1);
		start.QaInjectApps(new AppEntry[1] { entry });
		start.QaQueueIconBatch(new List<(AppEntry, ImageSource, bool)> { (entry, native, false) });
		Pump();
		List<string> lines;
		lock (log)
		{
			lines = log.Skip(logStart).ToList();
		}
		bool keysOk = keys == "broken,mismatch";
		bool brokenOk = broken == null && brokenAgain == null && SeasonalAppIcons.KeyForEntry(entry) == "broken" && ReferenceEquals(entry.Icon, native) && entry.SeasonalKey == null && entry.SeasonalUnder == null;
		bool mismatchOk = mismatch is BitmapSource mb && mb.PixelWidth == 30 && SourceSize(Pixel(mb, 15, 15)) == 30 && mismatchFrames.Count == 1 && mismatchFrames[0].Px == 30;
		int decodeLogs = lines.Count(l => l.Contains("could not decode broken.png", StringComparison.Ordinal));
		bool logsOk = decodeLogs == 1 && lines.Any(l => l.Contains("huge.png", StringComparison.Ordinal)) && lines.Any(l => l.Contains("notapng.png", StringComparison.Ordinal))
			&& lines.Any(l => l.Contains("mismatch-32.png is 30x30", StringComparison.Ordinal));
		Metrics["RobustnessLog"] = lines;
		Check("robust-bad-files", keysOk && brokenOk && mismatchOk && logsOk, $"keys={keys} broken keeps own icon={brokenOk} mismatch 30px={mismatchOk} decode logged {decodeLogs}x");
		SeasonalAppIcons.DiagnosticsRoot = fixture;
		Use(SeasonalTheme.Normal);
	}

	// An app's pictures stop decoding while its seasonal icon is shown (an interrupted copy): after the next Reconcile
	// the Start entry shows its own icon again, like the taskbar pin, instead of keeping the old picture.
	private static void UnreadableArtChecks(string outDir, StartScreen start, string fixture)
	{
		string root = Path.Combine(outDir, "fixture-swap", "seasonal");
		if (Directory.Exists(root))
		{
			Directory.Delete(root, recursive: true);
		}
		string hall = Path.Combine(root, "halloween-icons");
		Directory.CreateDirectory(hall);
		string art = Path.Combine(hall, "discord.png");
		WriteFrame(art, 512, 1);
		SeasonalAppIcons.DiagnosticsRoot = root;
		Use(SeasonalTheme.Normal);
		AppEntry entry = Entry("Discord", "shell:AppsFolder\\com.squirrel.Discord.Discord", "com.squirrel.Discord.Discord");
		ImageSource native = NativeIcon(0);
		start.QaInjectApps(new AppEntry[1] { entry });
		start.QaQueueIconBatch(new List<(AppEntry, ImageSource, bool)> { (entry, native, false) });
		Pump();
		Brush nativeBrush = entry.TileBrush;
		Use(SeasonalTheme.Halloween);
		bool shown = entry.SeasonalKey == "discord" && entry.Icon is BitmapSource;
		File.WriteAllBytes(art, FakePng(512, 512, 300));
		Use(SeasonalTheme.Halloween);   // as opening PC Settings does
		PinnedApp pin = new PinnedApp { Name = "Discord", LaunchPath = "C:\\QA\\Discord\\Update.exe", Args = "--processStart Discord.exe" };
		TaskbarWindow.QaPinIcon(pin, out string? pinKey);
		bool reverted = entry.SeasonalKey == null && entry.SeasonalUnder == null && ReferenceEquals(entry.Icon, native) && ReferenceEquals(entry.TileBrush, nativeBrush);
		Check("start-unreadable-art-reverts", shown && reverted && pinKey == null && SeasonalAppIcons.KeyForEntry(entry) == "discord",
			$"shown={shown} own icon back={reverted} pin key={pinKey ?? "null"}");
		SeasonalAppIcons.DiagnosticsRoot = fixture;
		Use(SeasonalTheme.Normal);
	}

	// A Reconcile between two states that show nothing refreshes no surface, and one with nothing changed only lists the
	// folder (no header reads, no new version).
	private static void ReconcileChecks()
	{
		int changed = 0;
		Action counter = delegate
		{
			changed++;
		};
		SeasonalAppIcons.Changed += counter;
		try
		{
			Use(SeasonalTheme.Normal);
			changed = 0;
			int version = SeasonalAppIcons.Version;
			Use(SeasonalTheme.Normal, icons: false);
			Use(SeasonalTheme.Normal, icons: true);
			Use(SeasonalTheme.Christmas);
			Use(SeasonalTheme.Normal);
			int quiet = changed;
			int versions = SeasonalAppIcons.Version - version;
			Use(SeasonalTheme.Halloween);
			int onHalloween = changed - quiet;
			int io = SeasonalAppIcons.IoCount;
			int v2 = SeasonalAppIcons.Version;
			SeasonalAppIcons.Reconcile("qa-again");
			Pump();
			int ioAgain = SeasonalAppIcons.IoCount - io;
			int changedAgain = changed - quiet - onHalloween;
			Check("reconcile-quiet-when-nothing-shown", quiet == 0 && versions == 4 && onHalloween == 1, $"refreshes while nothing shown={quiet} (versions +{versions}), on Halloween={onHalloween}");
			Check("reconcile-unchanged-lists-only", ioAgain == 1 && SeasonalAppIcons.Version == v2 && changedAgain == 0, $"io +{ioAgain} (one folder listing), version same={SeasonalAppIcons.Version == v2}, refreshes={changedAgain}");
		}
		finally
		{
			SeasonalAppIcons.Changed -= counter;
		}
		Use(SeasonalTheme.Normal);
	}

	// A PNG signature and IHDR claiming w x h, followed by bytes that do not decode.
	private static byte[] FakePng(int w, int h, int junk)
	{
		byte[] b = new byte[33 + junk];
		byte[] head = new byte[16] { 0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 13, (byte)'I', (byte)'H', (byte)'D', (byte)'R' };
		head.CopyTo(b, 0);
		System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(b.AsSpan(16, 4), w);
		System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(b.AsSpan(20, 4), h);
		b[24] = 8;
		b[25] = 6;
		for (int i = 33; i < b.Length; i++)
		{
			b[i] = (byte)(i * 37);
		}
		return b;
	}

	// ===================================================================== E. taskbar

	private static void TaskbarChecks(string outDir)
	{
		PinnedApp[] pins = new PinnedApp[4]
		{
			new PinnedApp { Name = "Discord", LaunchPath = "C:\\QA\\Discord\\Update.exe", Args = "--processStart Discord.exe" },
			new PinnedApp { Name = "Google Chrome", LaunchPath = "C:\\QA\\Google\\Chrome\\Application\\chrome.exe", ExePath = "C:\\QA\\Google\\Chrome\\Application\\chrome.exe" },
			new PinnedApp { Name = "Notepad", LaunchPath = "C:\\QA\\Windows\\notepad.exe", ExePath = "C:\\QA\\Windows\\notepad.exe" },
			new PinnedApp { Name = "YouTube", LaunchPath = "C:\\QA\\Google\\Chrome\\Application\\chrome_proxy.exe", Args = "--profile-directory=Default --app-id=agimnkijcaahngcdmfeangaknmldooml", ExePath = "C:\\QA\\Google\\Chrome\\Application\\chrome_proxy.exe" }
		};
		Use(SeasonalTheme.Normal);
		TaskbarWindow.RefreshPinIcons();
		BitmapSource before = RenderBar(pins, "Medium");
		SavePng(before, Path.Combine(outDir, "taskbar-before.png"));
		ImageSource? notepadBefore = TaskbarWindow.QaCachedPinIcon(pins[2]);
		Use(SeasonalTheme.Halloween);
		ImageSource? icon = TaskbarWindow.QaPinIcon(pins[0], out string? key);
		bool pinOk = key == "discord" && ReferenceEquals(icon, SeasonalAppIcons.Frame("discord", 24)) && icon is BitmapSource ib && ib.PixelWidth == 24;
		TaskbarWindow.QaPinIcon(pins[3], out string? pwaKey);
		TaskbarWindow.QaPinIcon(pins[2], out string? notepadKey);
		Check("taskbar-pin-squirrel", pinOk && pwaKey == null && notepadKey == null, $"key={key} 24px frame={pinOk} pwa={pwaKey ?? "null"} notepad={notepadKey ?? "null"}");
		BitmapSource halloween = RenderBar(pins, "Medium");
		SavePng(halloween, Path.Combine(outDir, "taskbar-halloween.png"));
		Use(SeasonalTheme.Halloween, size: "Large");
		ImageSource? large = TaskbarWindow.QaPinIcon(pins[0], out _);
		SavePng(RenderBar(pins, "Large"), Path.Combine(outDir, "taskbar-halloween-large.png"));
		Use(SeasonalTheme.Halloween, size: "Small");
		ImageSource? small = TaskbarWindow.QaPinIcon(pins[0], out _);
		SavePng(RenderBar(pins, "Small"), Path.Combine(outDir, "taskbar-halloween-small.png"));
		int smallSrc = small is BitmapSource sb ? SourceSize(Pixel(sb, 9, 9)) : -1;
		Check("taskbar-sizes", Px(large) == 32 && Px(small) == 18 && smallSrc == 40, $"Large={Px(large)} Small={Px(small)} (18<-{smallSrc}, want 40)");

		// A size change re-picks the frames of seasonal pins, groups and running buttons in place.
		Use(SeasonalTheme.Halloween, size: "Medium");
		PinnedTile tile = new PinnedTile { Name = "Discord", SeasonalKey = "discord", Icon = SeasonalAppIcons.Frame("discord", 24) };
		PinnedTile plain = new PinnedTile { Name = "Notepad", Icon = NativeIcon(5, 32) };
		GroupTile group = new GroupTile { Name = "QA" };
		group.Members.Add(tile);
		group.Members.Add(plain);
		TaskWindow running = new TaskWindow { Key = "qa", SeasonalKey = "slack", Icon = SeasonalAppIcons.Frame("slack", 24) };
		ImageSource? plainIcon = plain.Icon;
		ImageSource? groupBefore = group.Icon;
		Use(SeasonalTheme.Halloween, size: "Large");
		TaskbarWindow.RefreshSeasonalIconSizes(new PinnedTile[2] { tile, plain }, new GroupTile[1] { group }, new TaskWindow[1] { running });
		Check("taskbar-size-change-repicks", Px(tile.Icon) == 32 && Px(running.Icon) == 32 && ReferenceEquals(plain.Icon, plainIcon) && group.Icon != null && !ReferenceEquals(group.Icon, groupBefore),
			$"pin={Px(tile.Icon)} running={Px(running.Icon)} plain unchanged={ReferenceEquals(plain.Icon, plainIcon)} group regenerated={group.Icon != null}");

		// Thumbnail previews: the 16 px frame while the button shows a seasonal icon.
		ImageSource own = NativeIcon(3, 16);
		Use(SeasonalTheme.Halloween);
		ImageSource? thumb = TaskbarWindow.PreviewIcon(running, own);
		ImageSource? thumbPin = TaskbarWindow.PreviewIcon(tile, own);
		ImageSource? thumbPlain = TaskbarWindow.PreviewIcon(plain, own);
		Use(SeasonalTheme.Normal);
		ImageSource? thumbNormal = TaskbarWindow.PreviewIcon(running, own);
		Check("thumbnail-icon", Px(thumb) == 16 && Px(thumbPin) == 16 && ReferenceEquals(thumbPlain, own) && ReferenceEquals(thumbNormal, own), $"running={Px(thumb)} pin={Px(thumbPin)} plain=own normal=own");

		Use(SeasonalTheme.Normal);
		BitmapSource normal = RenderBar(pins, "Medium");
		SavePng(normal, Path.Combine(outDir, "taskbar-normal.png"));
		Check("taskbar-normal-equals-before", Bytes(before).SequenceEqual(Bytes(normal)), "pixel-equal");
		// Season changes never drop the cached native pin icons: Normal shows the very same objects again.
		ImageSource? notepadAfter = TaskbarWindow.QaPinIcon(pins[2], out _);
		Check("taskbar-native-cache-kept", notepadBefore != null && ReferenceEquals(notepadBefore, notepadAfter) && ReferenceEquals(TaskbarWindow.QaCachedPinIcon(pins[2]), notepadBefore),
			$"same cached icon after Halloween/Normal and size changes={ReferenceEquals(notepadBefore, notepadAfter)}");
	}

	private static int Px(ImageSource? img)
	{
		return img is BitmapSource b && b.PixelWidth == b.PixelHeight ? b.PixelWidth : -1;
	}

	// The pinned strip as the taskbar draws it: each icon at TB.IconSize, centred in a 48 DIP button, on the dark bar.
	private static BitmapSource RenderBar(PinnedApp[] pins, string size)
	{
		double barH = TaskbarMetrics.BarHeight;
		double icon = TaskbarMetrics.IconSize;
		int w = 48 * (pins.Length + 1);
		DrawingVisual dv = new DrawingVisual();
		using (DrawingContext dc = dv.RenderOpen())
		{
			dc.DrawRectangle(new SolidColorBrush(BarColor), null, new Rect(0, 0, w, barH));
			for (int i = 0; i < pins.Length; i++)
			{
				ImageSource? img = TaskbarWindow.QaPinIcon(pins[i], out _);
				if (img != null)
				{
					double x = 48 * (i + 1) + Math.Floor((48 - icon) / 2);
					double y = Math.Floor((barH - icon) / 2);
					dc.DrawImage(img, new Rect(x, y, icon, icon));
				}
			}
		}
		return Render(dv, w, (int)barH);
	}

	// ===================================================================== E. tray

	private static void TrayChecks(string outDir)
	{
		ImageSource raw = NativeIcon(0, 16);
		TrayAppIcon vm = new TrayAppIcon { StableKey = "Discord", RawImage = raw };
		TrayAppIcon own = new TrayAppIcon { StableKey = "Discord", RawImage = raw, IsOwn = true };
		TrayAppIcon audio = new TrayAppIcon { StableKey = "Discord", RawImage = raw, IsAudioSwitcher = true };
		TrayAppIcon win11 = new TrayAppIcon { StableKey = "1A2B:3", ExecutablePath = "C:\\QA\\Slack\\slack.exe", RawImage = raw };
		List<string> fail = new List<string>();
		List<(string Label, ImageSource? Img, int Px)> strip = new List<(string, ImageSource?, int)>();
		BitmapSource trayBefore = RenderTrayStrip(new TrayAppIcon[4] { vm, own, audio, win11 });
		SavePng(trayBefore, Path.Combine(outDir, "tray-before.png"));
		foreach ((string size, int want, int src) in new (string, int, int)[3] { ("Small", 16, 16), ("Medium", 18, 40), ("Large", 22, 48) })
		{
			Use(SeasonalTheme.Halloween, size: size);
			int px = TaskbarMetrics.TrayAppIconPx(TaskbarMetrics.TrayIconPx(1.0));
			ImageSource? img = TaskbarWindow.TrayImageFor(vm, px);
			ImageSource? ownImg = TaskbarWindow.TrayImageFor(own, px);
			ImageSource? audioImg = TaskbarWindow.TrayImageFor(audio, px);
			ImageSource? win11Img = TaskbarWindow.TrayImageFor(win11, px);
			if (px != want || Px(img) != want || SourceSize(Pixel((BitmapSource)img!, want / 2, want / 2)) != src || vm.SeasonalKey != "discord")
			{
				fail.Add($"{size}: px={px} img={Px(img)} key={vm.SeasonalKey}");
			}
			if (own.SeasonalKey != null || audio.SeasonalKey != null || !SamePixels(ownImg, TrayIconFit.Fit(raw, px)) || !SamePixels(audioImg, TrayIconFit.Fit(raw, px)))
			{
				fail.Add($"{size}: own/audio took a seasonal icon");
			}
			if (win11.SeasonalKey != "slack" || Px(win11Img) != want)
			{
				fail.Add($"{size}: registry icon key={win11.SeasonalKey}");
			}
			strip.Add((size, img, px));
			Use(SeasonalTheme.Normal, size: size);
			ImageSource? normal = TaskbarWindow.TrayImageFor(vm, px);
			if (vm.SeasonalKey != null || !SamePixels(normal, TrayIconFit.Fit(raw, px)) || !ReferenceEquals(vm.RawImage, raw))
			{
				fail.Add($"{size}: Normal is not the app's own icon");
			}
			strip.Add((size + " normal", normal, px));
		}
		Check("tray-frames", fail.Count == 0, fail.Count == 0 ? "16 exact, 18<-40, 22<-48; Normal = fitted RawImage; own/audio never seasonal; registry icons by executable" : string.Join("; ", fail));
		Use(SeasonalTheme.Normal);
		BitmapSource trayNormal = RenderTrayStrip(new TrayAppIcon[4] { vm, own, audio, win11 });
		SavePng(trayNormal, Path.Combine(outDir, "tray-normal.png"));
		Check("tray-normal-equals-before", Bytes(trayBefore).SequenceEqual(Bytes(trayNormal)), "Small/Medium/Large strips pixel-equal");
		DrawingVisual dv = new DrawingVisual();
		using (DrawingContext dc = dv.RenderOpen())
		{
			dc.DrawRectangle(new SolidColorBrush(BarColor), null, new Rect(0, 0, 36 * strip.Count, 40));
			for (int i = 0; i < strip.Count; i++)
			{
				if (strip[i].Img != null)
				{
					dc.DrawImage(strip[i].Img, new Rect(36 * i + (36 - strip[i].Px) / 2, (40 - strip[i].Px) / 2, strip[i].Px, strip[i].Px));
				}
			}
		}
		SavePng(Render(dv, 36 * strip.Count, 40), Path.Combine(outDir, "tray-halloween.png"));
	}

	// The notification-area icons in Normal at the Small, Medium and Large taskbar sizes, as TrayImageFor gives them.
	private static BitmapSource RenderTrayStrip(TrayAppIcon[] vms)
	{
		List<(ImageSource? Img, int Px)> cells = new List<(ImageSource?, int)>();
		foreach (string size in new string[3] { "Small", "Medium", "Large" })
		{
			Use(SeasonalTheme.Normal, size: size);
			int px = TaskbarMetrics.TrayAppIconPx(TaskbarMetrics.TrayIconPx(1.0));
			foreach (TrayAppIcon vm in vms)
			{
				cells.Add((TaskbarWindow.TrayImageFor(vm, px), px));
			}
		}
		Use(SeasonalTheme.Normal);
		DrawingVisual dv = new DrawingVisual();
		using (DrawingContext dc = dv.RenderOpen())
		{
			dc.DrawRectangle(new SolidColorBrush(BarColor), null, new Rect(0, 0, 36 * cells.Count, 40));
			for (int i = 0; i < cells.Count; i++)
			{
				if (cells[i].Img != null)
				{
					dc.DrawImage(cells[i].Img, new Rect(36 * i + (36 - cells[i].Px) / 2, (40 - cells[i].Px) / 2, cells[i].Px, cells[i].Px));
				}
			}
		}
		return Render(dv, 36 * cells.Count, 40);
	}

	private static bool SamePixels(ImageSource? a, ImageSource? b)
	{
		return a is BitmapSource x && b is BitmapSource y && x.PixelWidth == y.PixelWidth && x.PixelHeight == y.PixelHeight && Bytes(x).SequenceEqual(Bytes(y));
	}

	// ===================================================================== E. Windows 7 menu

	private static void Win7Checks(string outDir)
	{
		List<(AppEntry Entry, ImageSource Native)> rows = new List<(AppEntry, ImageSource)>
		{
			(Entry("Discord", "shell:AppsFolder\\com.squirrel.Discord.Discord", "com.squirrel.Discord.Discord"), NativeIcon(0)),
			(Entry("Google Chrome", "C:\\QA\\Google\\Chrome\\Application\\chrome.exe", null), NativeIcon(1)),
			(Entry("Notepad", "C:\\QA\\Windows\\notepad.exe", null), NativeIcon(5)),
			(Entry("Slack", "shell:AppsFolder\\com.tinyspeck.slackdesktop_8yrtsj140pw4g!Slack", "com.tinyspeck.slackdesktop_8yrtsj140pw4g!Slack"), NativeIcon(2))
		};
		Use(SeasonalTheme.Normal);
		(BitmapSource rowsBefore, _) = RenderWin7Rows(rows);
		SavePng(rowsBefore, Path.Combine(outDir, "win7-rows-before.png"));
		Use(SeasonalTheme.Halloween);
		(BitmapSource rowsHalloween, List<W7Row> shown) = RenderWin7Rows(rows);
		SavePng(rowsHalloween, Path.Combine(outDir, "win7-rows-halloween.png"));
		W7Icons.Entry? d32 = W7Icons.GetOrAddSeasonal("discord", 32, 32);
		bool rowSeasonal = d32 is W7Icons.Entry de && ReferenceEquals(shown[0].IconSource, de.Img) && shown.Count(r => r.Text == "Notepad" && r.IconSource is BitmapSource) == 4;
		Check("win7-rows-halloween", rowSeasonal, $"Discord row shows the 32 frame={rowSeasonal}");
		Use(SeasonalTheme.Normal);
		(BitmapSource rowsNormal, _) = RenderWin7Rows(rows);
		SavePng(rowsNormal, Path.Combine(outDir, "win7-rows-normal.png"));
		Check("win7-rows-normal-equals-before", Bytes(rowsBefore).SequenceEqual(Bytes(rowsNormal)), "light and dark panes pixel-equal");
		Use(SeasonalTheme.Halloween);
		W7Icons.Entry? e32 = W7Icons.GetOrAddSeasonal("discord", 32, 32);
		W7Icons.Entry? e16 = W7Icons.GetOrAddSeasonal("discord", 16, 16);
		W7Icons.Entry? again = W7Icons.GetOrAddSeasonal("discord", 32, 32);
		bool ok = e32 is W7Icons.Entry a && Px(a.Img) == 32 && SourceSize(Pixel((BitmapSource)a.Img!, 16, 16)) == 32 && !a.IsLetter && a.Plate == W7Icons.Classify((BitmapSource)a.Img!)
			&& e16 is W7Icons.Entry b && Px(b.Img) == 16 && SourceSize(Pixel((BitmapSource)b.Img!, 8, 8)) == 16 && b.Plate == W7Icons.Classify((BitmapSource)b.Img!)
			&& again is W7Icons.Entry c && ReferenceEquals(c.Img, a.Img);
		Check("win7-seasonal-entries", ok, $"32={Px(e32?.Img)} plate={e32?.Plate} 16={Px(e16?.Img)} plate={e16?.Plate} cached={again.HasValue && e32.HasValue && ReferenceEquals(again.Value.Img, e32.Value.Img)}");
		Use(SeasonalTheme.Normal);
		Check("win7-normal-no-seasonal", W7Icons.GetOrAddSeasonal("discord", 32, 32) == null, "");
		Use(SeasonalTheme.Halloween);
		RenderWin7(Path.Combine(outDir, "win7-halloween.png"), FixtureKeys);
	}

	// Real Windows 7 program rows (36 and 22 DIP) on the light and dark panes. Each row takes the icon BindIcon would:
	// W7Icons.SeasonalFor, else the entry's own icon at the row's size.
	private static (BitmapSource Bmp, List<W7Row> Rows) RenderWin7Rows(List<(AppEntry Entry, ImageSource Native)> entries)
	{
		List<W7Row> made = new List<W7Row>();
		StackPanel both = new StackPanel { Orientation = Orientation.Horizontal };
		foreach (bool dark in new bool[2] { false, true })
		{
			StackPanel pane = new StackPanel
			{
				Width = 260.0,
				Background = new SolidColorBrush(dark ? Win7Palette.PaneDark : Win7Palette.PaneLight),
				Resources = Win7Palette.Build(dark, Color.FromRgb(0x33, 0x66, 0x99), transparency: false)
			};
			foreach ((AppEntry entry, ImageSource native) in entries)
			{
				foreach (W7Row.Kind kind in new W7Row.Kind[2] { W7Row.Kind.Program36, W7Row.Kind.Program22 })
				{
					W7Row row = new W7Row(kind, onGlass: false);
					row.SetText(entry.Name);
					int box = row.IconBox;
					if (W7Icons.SeasonalFor(entry, box, box) is W7Icons.Entry se)
					{
						row.SetIcon(se.Img, se.Plated, se.Plate, null, alwaysPlate: false, dark);
					}
					else
					{
						row.SetIcon(TrayIconFit.Fit(native, box), null, IconPlate.None, null, alwaysPlate: false, dark);
					}
					pane.Children.Add(row);
					made.Add(row);
				}
			}
			both.Children.Add(pane);
		}
		int h = (int)(entries.Count * (36 + 22));
		return (RenderElement(both, 520, h), made);
	}

	private static void RenderWin7(string path, string[] keys)
	{
		int rowH = 40;
		int w = 2 * 260;
		int h = rowH * keys.Length + 10;
		DrawingVisual dv = new DrawingVisual();
		using (DrawingContext dc = dv.RenderOpen())
		{
			for (int pane = 0; pane < 2; pane++)
			{
				bool dark = pane == 1;
				double ox = pane * 260;
				dc.DrawRectangle(new SolidColorBrush(dark ? Win7Palette.PaneDark : Win7Palette.PaneLight), null, new Rect(ox, 0, 260, h));
				for (int i = 0; i < keys.Length; i++)
				{
					double y = 5 + i * rowH;
					foreach ((int box, double x) in new (int, double)[2] { (32, ox + 8), (16, ox + 48) })
					{
						if (W7Icons.GetOrAddSeasonal(keys[i], box, box) is not W7Icons.Entry e || e.Img == null)
						{
							continue;
						}
						double top = y + (32 - box) / 2;
						bool plate = (!dark && (e.Plate == IconPlate.LightMono || e.Plate == IconPlate.PaleOnLight)) || (dark && (e.Plate == IconPlate.DarkMono || e.Plate == IconPlate.PaleOnDark || e.Plate == IconPlate.DarkFill));
						if (plate)
						{
							dc.DrawRectangle(new SolidColorBrush(dark ? Color.FromRgb(0xE6, 0xE6, 0xE6) : Color.FromRgb(0x33, 0x66, 0x99)), null, new Rect(x, top, box, box));
							double inset = W7Icons.InsetDip(box);
							dc.DrawImage(e.Plated ?? e.Img, new Rect(x + (box - inset) / 2, top + (box - inset) / 2, inset, inset));
						}
						else
						{
							dc.DrawImage(e.Img, new Rect(x, top, box, box));
						}
					}
					FormattedText ft = new FormattedText(keys[i] + "  (" + (W7Icons.GetOrAddSeasonal(keys[i], 32, 32)?.Plate.ToString() ?? "-") + ")", System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), 12.0, dark ? Brushes.White : Brushes.Black, 1.0);
					dc.DrawText(ft, new Point(ox + 74, y + 8));
				}
			}
		}
		SavePng(Render(dv, w, h), path);
	}

	// ===================================================================== E. other surfaces

	private static void OtherSurfaceChecks()
	{
		Use(SeasonalTheme.Halloween);
		// (notifications are entirely Windows' own: no Action Center cards or launcher toasts carry seasonal icons any more)
		ImageSource? audio = AudioSessions.IconFor("Discord", "C:\\QA\\Discord\\app-1.0.9200\\Discord.exe");
		string? switcher = SeasonalAppIcons.KeyForWindow(IntPtr.Zero, "C:\\QA\\Discord\\app-1.0.9200\\Discord.exe", null);
		int audioSrc = audio is BitmapSource aub ? SourceSize(Pixel(aub, 13, 13)) : -1;
		Check("volume-row-27", Px(audio) == 27 && audioSrc == 64, $"{Px(audio)} (27<-{audioSrc}, want 64)");
		Check("switcher-key", switcher == "discord" && Px(SeasonalAppIcons.Frame(switcher, SeasonalAppIcons.Px(16.0))) == 16, $"{switcher}");
		Use(SeasonalTheme.Normal);
		Check("volume-row-normal", AudioSessions.IconFor("Discord", "C:\\QA\\Discord\\app-1.0.9200\\Discord.exe") == null, "a missing file has no icon, as before");
	}

	// ===================================================================== F. jump lists

	private static void JumpListChecks(StartScreen start)
	{
		Use(SeasonalTheme.Halloween);
		List<JumpList.Item> items = new List<JumpList.Item> { new JumpList.Item("C:\\QA\\a.txt", "a.txt"), new JumpList.Item("C:\\QA\\b.txt", "b.txt") };
		ContextMenu menu = new ContextMenu();
		TaskbarWindow.QaInsertJumpListItems(menu, items);
		MenuItem recent = new MenuItem();
		start.QaRenderRecent(recent, items);
		int images = menu.Items.OfType<MenuItem>().Count(m => m.Icon != null) + recent.Items.OfType<MenuItem>().Count(m => m.Icon != null);
		Check("jumplists-text-only", images == 0 && menu.Items.Count == 4 && recent.Items.Count == 2, $"taskbar items={menu.Items.Count} start recent={recent.Items.Count} icons={images}");
		Use(SeasonalTheme.Normal);
	}

	// ===================================================================== H. idle cost

	private static void IdleChecks()
	{
		Use(SeasonalTheme.Normal);
		AppEntry e = Entry("Discord", "shell:AppsFolder\\com.squirrel.Discord.Discord", "com.squirrel.Discord.Discord");
		nint hwnd = Process.GetCurrentProcess().MainWindowHandle;
		// Steady state, as in the running shell: let the JIT finish optimising the lookup path before timing it.
		for (int round = 0; round < 3; round++)
		{
			for (int i = 0; i < 2000; i++)
			{
				SeasonalAppIcons.KeyForWindow(hwnd, null, null);
				SeasonalAppIcons.KeyForEntry(e);
			}
			Thread.Sleep(150);
		}
		int io = SeasonalAppIcons.IoCount;
		int hits = 0;
		List<double> runs = new List<double>();
		for (int run = 0; run < 3; run++)
		{
			Stopwatch sw = Stopwatch.StartNew();
			for (int i = 0; i < 10000; i++)
			{
				if (SeasonalAppIcons.KeyForWindow(hwnd, null, null) != null || SeasonalAppIcons.KeyForEntry(e) != null)
				{
					hits++;
				}
			}
			sw.Stop();
			runs.Add(sw.Elapsed.TotalMilliseconds);
		}
		double ms = runs.Min();
		Metrics["NormalLookups20000Ms"] = runs.Select(r => Math.Round(r, 3)).ToArray();
		Check("idle-lookups-normal", ms < 5.0 && hits == 0 && SeasonalAppIcons.IoCount == io, $"10,000 x (KeyForWindow + KeyForEntry) = {ms:F3} ms (best of {string.Join(" / ", runs.Select(r => r.ToString("F3")))}), io +{SeasonalAppIcons.IoCount - io}");

		// No watcher, timer or thread is owned by the feature.
		Type[] watched = new Type[5] { typeof(FileSystemWatcher), typeof(DispatcherTimer), typeof(System.Threading.Timer), typeof(System.Timers.Timer), typeof(Thread) };
		List<string> owned = new List<string>();
		foreach (Type t in new Type[1] { typeof(SeasonalAppIcons) }.Concat(typeof(SeasonalAppIcons).GetNestedTypes(BindingFlags.NonPublic | BindingFlags.Public)))
		{
			foreach (FieldInfo f in t.GetFields(BindingFlags.Static | BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public))
			{
				if (watched.Any(w => w.IsAssignableFrom(f.FieldType)))
				{
					owned.Add(t.Name + "." + f.Name);
				}
			}
		}
		Check("idle-no-watchers-timers-threads", owned.Count == 0, owned.Count == 0 ? "none" : string.Join(",", owned));

		// A full season cycle leaves no thread behind.
		int threadsBefore = ThreadCount();
		Use(SeasonalTheme.Halloween);
		SeasonalAppIcons.Frame("discord", 24);
		SeasonalAppIcons.KeyForEntry(e);
		Use(SeasonalTheme.Normal);
		Stopwatch wait = Stopwatch.StartNew();
		while (wait.ElapsedMilliseconds < 2000)
		{
			Pump();
			Thread.Sleep(50);
		}
		int threadsAfter = ThreadCount();
		Metrics["Threads"] = new int[2] { threadsBefore, threadsAfter };
		Check("idle-threads-after-cycle", threadsAfter <= threadsBefore, $"{threadsBefore} -> {threadsAfter}");
	}

	private static int ThreadCount()
	{
		using Process p = Process.GetCurrentProcess();
		p.Refresh();
		return p.Threads.Count;
	}

	// ===================================================================== PC Settings

	private static void RenderPcSettings(string path, StartScreen start)
	{
		PcSettingsWindow pc = new PcSettingsWindow(start)
		{
			Left = -6000.0,
			Top = -6000.0
		};
		try
		{
			pc.QaRenderTall(path, "Appearance");
		}
		finally
		{
			pc.Close();
		}
		string status = PcSettingsWindow.SeasonalIconsStatusText();
		Metrics["PcSettingsStatus_" + Path.GetFileNameWithoutExtension(path)] = status;
	}

	// ===================================================================== I. real folder (read only)

	private static void RealProbe(string outDir, string realRoot, StartScreen start)
	{
		SeasonalAppIcons.DiagnosticsRoot = realRoot;
		Use(SeasonalTheme.Halloween);
		IReadOnlyList<string> keys = SeasonalAppIcons.KeysFor(SeasonalTheme.Halloween);
		Dictionary<string, string> frames = new Dictionary<string, string>();
		List<string> missing = new List<string>();
		foreach (string k in keys)
		{
			IReadOnlyList<(int Px, string Path, bool Sized)> f = SeasonalAppIcons.QaFrames(k);
			frames[k] = string.Join(",", f.Select(x => x.Px + (x.Sized ? "" : "m")));
			foreach (int s in FixtureSizes.Concat(new int[1] { 512 }))
			{
				if (!f.Any(x => x.Px == s))
				{
					missing.Add(k + ":" + s);
				}
			}
		}
		Metrics["RealKeys"] = frames;
		// The folder may hold more pictures than the five the feature started with; those five must be there.
		Check("real-folder-keys", new string[5] { "chrome", "discord", "notion", "slack", "spotify" }.All(k => keys.Contains(k)) && missing.Count == 0, "keys=" + string.Join(",", keys) + (missing.Count > 0 ? " missing=" + string.Join(",", missing) : " each 16..256 + 512 master"));

		// Which installed apps and native taskbar pins match (read only).
		List<string> appHits = new List<string>();
		List<(AppEntry Entry, AppInventory.IShellItem Item)>? apps = null;
		try
		{
			apps = AppInventory.EnumerateAppsFolder();
			foreach ((AppEntry entry, AppInventory.IShellItem _) in apps)
			{
				if (SeasonalAppIcons.KeyForEntry(entry) is string key)
				{
					appHits.Add($"{entry.Name}={key} [{entry.AppId}]");
				}
			}
		}
		catch (Exception ex)
		{
			appHits.Add("error: " + ex.Message);
		}
		finally
		{
			AppInventory.ReleaseAppsFolderItems(apps);
		}
		List<string> pinHits = new List<string>();
		List<PinnedApp> nativePins = new List<PinnedApp>();
		try
		{
			nativePins = TaskbarPins.ImportFromNative();
			foreach (PinnedApp p in nativePins)
			{
				pinHits.Add($"{p.Name}={SeasonalAppIcons.KeyForPin(p) ?? "-"}");
			}
		}
		catch (Exception ex)
		{
			pinHits.Add("error: " + ex.Message);
		}
		Metrics["RealAppsMatched"] = appHits;
		Metrics["RealPins"] = pinHits;
		string[] wantApps = new string[5] { "Discord=discord", "Google Chrome=chrome", "Notion=notion", "Slack=slack", "Spotify=spotify" };
		List<string> names = appHits.Select(h => h.Split(" [")[0]).ToList();
		// Every matched app must use a picture that exists, and the five reference apps must match their own picture.
		bool exact = wantApps.All(w => names.Contains(w)) && names.All(n => n.Contains('=') && keys.Contains(n.Substring(n.LastIndexOf('=') + 1)));
		Check("real-apps-match", exact, string.Join("; ", appHits));
		Check("real-pins-match", pinHits.Count(h => !h.EndsWith("=-", StringComparison.Ordinal)) >= 1, string.Join("; ", pinHits));

		// Renders with the real art: Start tiles, all apps, taskbar (native pins, before and after), tray, Windows 7 rows
		// and every surface size side by side.
		List<Probe> probes = new List<Probe>();
		string[] real = new string[5] { "discord", "chrome", "notion", "slack", "spotify" };
		string[] titles = new string[5] { "Discord", "Google Chrome", "Notion", "Slack", "Spotify" };
		for (int i = 0; i < real.Length; i++)
		{
			probes.Add(new Probe { Entry = Entry(titles[i], "shell:AppsFolder\\qa.real." + real[i], null), Key = real[i], Native = NativeIcon(i) });
		}
		Use(SeasonalTheme.Normal);
		start.QaInjectApps(probes.Select(p => p.Entry));
		LoadProbes(start, probes, replace81: true);
		Use(SeasonalTheme.Halloween);
		TileColourChecks("real-tile-colours-halloween", real, SeasonalTheme.Halloween);
		TileColourChecks("real-tile-colours-christmas", real, SeasonalTheme.Christmas);
		SharpnessMetrics(real);
		RenderRealTiles(start, Path.Combine(outDir, "real-start-tiles-halloween.png"), probes);
		RenderRealAllApps(Path.Combine(outDir, "real-allapps-halloween.png"), probes);
		RenderMoreSurfaces(start, Path.Combine(outDir, "real-more-surfaces-halloween.png"), probes, titles);
		Use(SeasonalTheme.Normal);
		RenderRealBar(Path.Combine(outDir, "real-taskbar-normal.png"), nativePins);
		foreach (string size in new string[3] { "Small", "Medium", "Large" })
		{
			Use(SeasonalTheme.Halloween, size: size);
			RenderRealBar(Path.Combine(outDir, $"real-taskbar-halloween-{size.ToLowerInvariant()}.png"), nativePins);
		}
		Use(SeasonalTheme.Halloween);
		RenderWin7(Path.Combine(outDir, "real-win7-halloween.png"), real);
		RenderSheet(Path.Combine(outDir, "real-surfaces-halloween.png"), real);
		RenderPcSettings(Path.Combine(outDir, "real-pcsettings-launcher.png"), start);
		Use(SeasonalTheme.Normal);
		SeasonalAppIcons.DiagnosticsRoot = null;
	}

	private static void RenderRealTiles(StartScreen start, string path, List<Probe> probes)
	{
		Canvas canvas = new Canvas { Background = StartBackground(SeasonalTheme.Halloween) };
		double y = 10.0;
		foreach (Probe p in probes)
		{
			double x = 10.0;
			foreach (TileSize size in new TileSize[4] { TileSize.Small, TileSize.Medium, TileSize.Wide, TileSize.Large })
			{
				TileVm vm = new TileVm { Entry = p.Entry, Size = size };
				Button b = new Button { Style = (Style)start.FindResource("TileButton"), DataContext = vm };
				Canvas.SetLeft(b, x);
				Canvas.SetTop(b, y);
				canvas.Children.Add(b);
				x += vm.PixelWidth + 10.0;
			}
			y += 320.0;
		}
		SavePng(RenderElement(canvas, 900, (int)y), path);
	}

	private static void RenderRealAllApps(string path, List<Probe> probes)
	{
		StackPanel panel = new StackPanel { Background = StartBackground(SeasonalTheme.Halloween) };
		SolidColorBrush hover = new SolidColorBrush(Color.FromArgb(64, 255, 255, 255));
		SolidColorBrush accent = new SolidColorBrush(Color.FromRgb(0x46, 0x17, 0x8F));
		List<AppListItem> items = new List<AppListItem>();
		foreach (Probe p in probes)
		{
			AppListItem item = new AppListItem(p.Entry, hover, accent);
			items.Add(item);
			panel.Children.Add(item);
		}
		StackPanel search = new StackPanel { Background = new SolidColorBrush(Color.FromRgb(0x2B, 0x2B, 0x2B)) };
		foreach (Probe p in probes)
		{
			StackPanel row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(10.0, 6.0, 10.0, 6.0) };
			Image img = new Image { Width = 24.0, Height = 24.0, VerticalAlignment = VerticalAlignment.Center };
			RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);
			SearchPane.BindAppIcon(img, p.Entry, 24.0);
			row.Children.Add(img);
			row.Children.Add(new TextBlock { Text = p.Entry.Name, Foreground = Brushes.White, FontFamily = new FontFamily("Segoe UI"), FontSize = 14.0, Margin = new Thickness(12.0, 0.0, 0.0, 0.0), VerticalAlignment = VerticalAlignment.Center });
			search.Children.Add(row);
		}
		StackPanel both = new StackPanel { Orientation = Orientation.Horizontal };
		both.Children.Add(panel);
		both.Children.Add(search);
		search.Width = 260.0;
		SavePng(RenderElement(both, 508, (int)(probes.Count * AppsColumnsPanel.ItemHeight)), path);
		foreach (AppListItem item in items)
		{
			item.Dispose();
		}
	}

	private static void RenderRealBar(string path, List<PinnedApp> pins)
	{
		double barH = TaskbarMetrics.BarHeight;
		double icon = TaskbarMetrics.IconSize;
		int w = Math.Max(48, 48 * pins.Count);
		DrawingVisual dv = new DrawingVisual();
		using (DrawingContext dc = dv.RenderOpen())
		{
			dc.DrawRectangle(new SolidColorBrush(BarColor), null, new Rect(0, 0, w, barH));
			for (int i = 0; i < pins.Count; i++)
			{
				ImageSource? img = TaskbarWindow.QaPinIcon(pins[i], out _);
				if (img != null)
				{
					dc.DrawImage(img, new Rect(48 * i + Math.Floor((48 - icon) / 2), Math.Floor((barH - icon) / 2), icon, icon));
				}
			}
		}
		SavePng(Render(dv, w, (int)barH), path);
	}

	// Edge energy (mean luminance step between neighbouring pixels, alpha-weighted) of the 18 and 22 px icons as made now
	// (from a frame at least twice the size) and as a plain downscale of the next larger frame would give them.
	private static void SharpnessMetrics(string[] keys)
	{
		List<string> rows = new List<string>();
		foreach (string k in keys)
		{
			IReadOnlyList<(int Px, string Path, bool Sized)> frames = SeasonalAppIcons.QaFrames(k);
			foreach ((int px, int next) in new (int, int)[2] { (18, 20), (22, 24) })
			{
				(int Px, string Path, bool Sized) f = frames.FirstOrDefault(x => x.Px == next);
				if (f.Path == null || SeasonalAppIcons.Frame(k, px) is not BitmapSource now)
				{
					continue;
				}
				ImageSource old = TrayIconFit.Fit(Decode(f.Path), px);
				rows.Add($"{k} {px}px: {EdgeEnergy(now):F1} (was {EdgeEnergy((BitmapSource)old):F1} from {next})");
			}
		}
		Metrics["RealEdgeEnergy"] = rows;
	}

	private static double EdgeEnergy(BitmapSource src)
	{
		byte[] p = Bytes(src);
		int w = src.PixelWidth;
		int h = src.PixelHeight;
		double sum = 0.0;
		int n = 0;
		for (int y = 0; y < h - 1; y++)
		{
			for (int x = 0; x < w - 1; x++)
			{
				int i = (y * w + x) * 4;
				double l0 = Lum(p, i);
				sum += Math.Abs(l0 - Lum(p, i + 4)) + Math.Abs(l0 - Lum(p, i + w * 4));
				n += 2;
			}
		}
		return n > 0 ? sum / n : 0.0;

		static double Lum(byte[] b, int i)
		{
			return 0.114 * b[i] + 0.587 * b[i + 1] + 0.299 * b[i + 2];   // premultiplied: transparent pixels count as dark
		}
	}

	// The surfaces that only had size checks, drawn with the real art as each surface builds them: running taskbar
	// buttons, thumbnail preview headers, switcher entries, volume mixer rows (with the speaker glyph underneath)
	// and a folder tile (the real tile template).
	private static void RenderMoreSurfaces(StartScreen start, string path, List<Probe> probes, string[] titles)
	{
		StackPanel root = new StackPanel { Background = new SolidColorBrush(Color.FromRgb(0x10, 0x10, 0x10)) };
		FontFamily ui = new FontFamily("Segoe UI");
		void Header(string text)
		{
			root.Children.Add(new TextBlock { Text = text, Foreground = Brushes.White, FontFamily = ui, FontSize = 13.0, Margin = new Thickness(10.0, 12.0, 0.0, 6.0) });
		}
		ImageSource Native(int i, int px)
		{
			return TrayIconFit.Fit(NativeIcon(i), px);
		}
		string Exe(int i)
		{
			return "C:\\QA\\" + titles[i].Replace(" ", "") + "\\" + probes[i].Key + ".exe";
		}

		// Running buttons: the taskbar's TaskButtonTemplate (icon at TB.IconSize, 10 DIP padding, 3 DIP underline).
		Header("Running taskbar buttons (Medium)");
		StackPanel bar = new StackPanel { Orientation = Orientation.Horizontal, Background = new SolidColorBrush(BarColor), Height = TaskbarMetrics.BarHeight, HorizontalAlignment = HorizontalAlignment.Left };
		for (int i = 0; i < probes.Count; i++)
		{
			string? key = SeasonalAppIcons.KeyForWindow(IntPtr.Zero, Exe(i), "");
			ImageSource icon = (key != null ? SeasonalAppIcons.Frame(key, SeasonalAppIcons.Px(TaskbarMetrics.IconSize)) : null) ?? Native(i, (int)TaskbarMetrics.IconSize);
			Grid button = new Grid { Background = new SolidColorBrush(Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF)), Margin = new Thickness(0.0, 0.0, 2.0, 0.0) };
			button.Children.Add(new Image { Source = icon, Width = TaskbarMetrics.IconSize, Height = TaskbarMetrics.IconSize, Margin = new Thickness(10.0, 0.0, 10.0, 0.0), VerticalAlignment = VerticalAlignment.Center, SnapsToDevicePixels = true });
			button.Children.Add(new Border { Height = 3.0, Background = new SolidColorBrush(Color.FromArgb(0xB4, 0xE0, 0x62, 0x0B)), VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(6.0, 0.0, 6.0, 2.0) });
			bar.Children.Add(button);
		}
		root.Children.Add(Wrap(bar));

		// Thumbnail previews: the header ThumbnailPreview builds (16 DIP icon from PreviewIcon, title) over the preview area.
		Header("Thumbnail preview headers");
		Color chrome = TaskbarTheme.ChromeColor();
		StackPanel thumbs = new StackPanel { Orientation = Orientation.Horizontal, Background = new SolidColorBrush(Color.FromRgb(chrome.R, chrome.G, chrome.B)), HorizontalAlignment = HorizontalAlignment.Left };
		for (int i = 0; i < probes.Count; i++)
		{
			TaskWindow tw = new TaskWindow { Key = "qa" + i, Title = titles[i], SeasonalKey = probes[i].Key };
			StackPanel cell = new StackPanel { Margin = new Thickness(3.0, 0.0, 3.0, 0.0) };
			StackPanel hp = new StackPanel { Orientation = Orientation.Horizontal, Height = 24.0, Width = 140.0, Margin = new Thickness(3.0, 0.0, 0.0, 0.0) };
			hp.Children.Add(new Image { Source = TaskbarWindow.PreviewIcon(tw, Native(i, 16)), Width = 16.0, Height = 16.0, Margin = new Thickness(0.0, 0.0, 6.0, 0.0), VerticalAlignment = VerticalAlignment.Center });
			hp.Children.Add(new TextBlock { Text = titles[i], Foreground = new SolidColorBrush(Color.FromRgb(240, 240, 240)), FontFamily = ui, FontSize = 12.0, VerticalAlignment = VerticalAlignment.Center });
			cell.Children.Add(hp);
			cell.Children.Add(new Border { Width = 140.0, Height = 70.0, Background = new SolidColorBrush(Color.FromRgb(0x3A, 0x3A, 0x3A)), Margin = new Thickness(0.0, 0.0, 0.0, 6.0) });
			thumbs.Children.Add(cell);
		}
		root.Children.Add(Wrap(thumbs));

		// App switcher entries: thumbnail box over a 30 DIP label with the 16 DIP icon (KeyForWindow first).
		Header("App switcher entries");
		StackPanel switcher = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Left };
		for (int i = 0; i < probes.Count; i++)
		{
			string? key = SeasonalAppIcons.KeyForWindow(IntPtr.Zero, Exe(i), null);
			ImageSource icon = (key != null ? SeasonalAppIcons.Frame(key, SeasonalAppIcons.Px(16.0)) : null) ?? Native(i, 16);
			StackPanel entry = new StackPanel { Width = 140.0, Margin = new Thickness(0.0, 0.0, 6.0, 0.0) };
			entry.Children.Add(new Border { Height = 60.0, Background = new SolidColorBrush(Color.FromRgb(42, 42, 42)) });
			StackPanel label = new StackPanel { Orientation = Orientation.Horizontal, Height = 30.0, Background = new SolidColorBrush(Color.FromRgb(17, 17, 17)) };
			label.Children.Add(new Image { Source = icon, Width = 16.0, Height = 16.0, Margin = new Thickness(9.0, 0.0, 8.0, 0.0), VerticalAlignment = VerticalAlignment.Center });
			label.Children.Add(new TextBlock { Text = titles[i], Foreground = Brushes.White, FontFamily = ui, FontSize = 14.0, VerticalAlignment = VerticalAlignment.Center });
			entry.Children.Add(label);
			switcher.Children.Add(entry);
		}
		root.Children.Add(Wrap(switcher));

		// Volume mixer: the SessionCard template, the speaker glyph drawn under the 27 DIP icon.
		Header("Volume mixer rows");
		StackPanel volume = new StackPanel { Width = 300.0, HorizontalAlignment = HorizontalAlignment.Left };
		for (int i = 0; i < probes.Count; i++)
		{
			ImageSource icon = AudioSessions.IconFor(probes[i].Key, Exe(i)) ?? Native(i, 27);
			Grid card = new Grid { Height = 62.0, Background = new SolidColorBrush(Color.FromRgb(0x0D, 0x8E, 0xA5)), Margin = new Thickness(0.0, 0.0, 0.0, 4.0) };
			card.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(48.0) });
			card.ColumnDefinitions.Add(new ColumnDefinition());
			Grid iconCol = new Grid { Background = new SolidColorBrush(Color.FromArgb(0x26, 0, 0, 0)) };
			iconCol.Children.Add(new TextBlock { FontFamily = new FontFamily("Segoe MDL2 Assets"), Text = "\ue767", FontSize = 22.0, Foreground = new SolidColorBrush(Color.FromArgb(0xD8, 0xFF, 0xFF, 0xFF)), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center });
			Image img = new Image { Width = 27.0, Height = 27.0, Stretch = Stretch.Uniform, Source = icon, SnapsToDevicePixels = true };
			RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);
			iconCol.Children.Add(img);
			card.Children.Add(iconCol);
			TextBlock name = new TextBlock { Text = titles[i], Foreground = Brushes.White, FontFamily = ui, FontSize = 12.0, Margin = new Thickness(10.0, 5.0, 7.0, 0.0) };
			Grid.SetColumn(name, 1);
			card.Children.Add(name);
			volume.Children.Add(card);
		}
		root.Children.Add(Wrap(volume));

		// A folder tile of the five apps: the real Start tile template on the Halloween background.
		Header("Folder tiles (Medium and Large)");
		Canvas folders = new Canvas { Width = 500.0, Height = 330.0, Background = StartBackground(SeasonalTheme.Halloween), HorizontalAlignment = HorizontalAlignment.Left };
		double fx = 10.0;
		foreach (TileSize size in new TileSize[2] { TileSize.Medium, TileSize.Large })
		{
			TileVm folder = FolderTiles.MakeFolder("Apps", probes.Select(p => p.Entry), size);
			Button b = new Button { Style = (Style)start.FindResource("TileButton"), DataContext = folder };
			Canvas.SetLeft(b, fx);
			Canvas.SetTop(b, 10.0);
			folders.Children.Add(b);
			fx += folder.PixelWidth + 10.0;
		}
		root.Children.Add(Wrap(folders));

		root.Measure(new Size(760.0, double.PositiveInfinity));
		SavePng(RenderElement(root, 760, (int)Math.Ceiling(root.DesiredSize.Height) + 10), path);

		static Border Wrap(FrameworkElement e)
		{
			return new Border { Child = e, Margin = new Thickness(10.0, 0.0, 10.0, 0.0), HorizontalAlignment = HorizontalAlignment.Left };
		}
	}

	// Every key at every surface size, 1:1, on the dark bar and on white.
	private static void RenderSheet(string path, string[] keys)
	{
		(string Label, int Px)[] cols = new (string, int)[13]
		{
			("tray S", 16), ("tray M", 18), ("tray L", 22), ("bar S", 18), ("bar M", 24), ("bar L", 32), ("list", 24), ("volume", 27), ("toast", 32),
			("card", 34), ("tile S", 36), ("tile M", 76), ("tile L", 112)
		};
		int colW = 120;
		int rowH = 130;
		int w = 90 + cols.Length * colW;
		int h = 30 + keys.Length * rowH * 2;
		DrawingVisual dv = new DrawingVisual();
		using (DrawingContext dc = dv.RenderOpen())
		{
			dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, w, h));
			for (int c = 0; c < cols.Length; c++)
			{
				Label(dc, $"{cols[c].Label} {cols[c].Px}", 90 + c * colW + 4, 6, 11, Brushes.Black);
			}
			for (int k = 0; k < keys.Length; k++)
			{
				for (int pass = 0; pass < 2; pass++)
				{
					double y = 30 + (k * 2 + pass) * rowH;
					bool dark = pass == 0;
					dc.DrawRectangle(new SolidColorBrush(dark ? BarColor : Color.FromRgb(0xF4, 0xF4, 0xF4)), null, new Rect(0, y, w, rowH));
					Label(dc, keys[k], 6, y + 6, 12, dark ? Brushes.White : Brushes.Black);
					for (int c = 0; c < cols.Length; c++)
					{
						if (SeasonalAppIcons.Frame(keys[k], cols[c].Px) is ImageSource img)
						{
							double x = 90 + c * colW + (colW - cols[c].Px) / 2;
							dc.DrawImage(img, new Rect(Math.Floor(x), Math.Floor(y + (rowH - cols[c].Px) / 2.0), cols[c].Px, cols[c].Px));
						}
					}
				}
			}
		}
		SavePng(Render(dv, w, h), path);
	}

	// ===================================================================== G. baseline

	private sealed class Baseline
	{
		public string Hashes = "";

		public string Folders = "";

		public string Links = "";

		public int LinkCount;

		public string Registry = "";

		public string Outside = "";

		public static Baseline Capture(string? realRoot)
		{
			Baseline b = new Baseline();
			List<string> files = new List<string>();
			foreach (string name in new string[4] { "settings.json", "seasonal-state.json", "taskbar-pins.json", "profile.json" })
			{
				files.Add(Path.Combine(DataRoot, name));
			}
			string state = Path.Combine(DataRoot, "state");
			if (Directory.Exists(state))
			{
				files.AddRange(Directory.GetFiles(state).Where(f => !f.Contains("log", StringComparison.OrdinalIgnoreCase)).OrderBy(f => f, StringComparer.OrdinalIgnoreCase));
			}
			b.Hashes = string.Join(";", files.Select(f => Path.GetFileName(f) + "=" + Hash(f)));
			StringBuilder folders = new StringBuilder();
			foreach (string? dir in new string?[3] { realRoot, Path.Combine(AppContext.BaseDirectory, "seasonal"), SeasonalArt.SeasonalDir })
			{
				if (dir == null)
				{
					continue;
				}
				folders.Append(dir).Append('=');
				if (Directory.Exists(dir))
				{
					foreach (string f in Directory.GetFileSystemEntries(dir, "*", SearchOption.AllDirectories).OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
					{
						FileInfo fi = new FileInfo(f);
						folders.Append(Path.GetRelativePath(dir, f)).Append(':').Append(fi.Exists ? fi.Length : -1).Append(':').Append(File.GetLastWriteTimeUtc(f).Ticks).Append(';');
					}
				}
				else
				{
					folders.Append("(none)");
				}
				folders.Append('|');
			}
			b.Folders = folders.ToString();
			List<string> links = new List<string>();
			string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
			foreach ((string dir, SearchOption opt) in new (string, SearchOption)[5]
			{
				(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), SearchOption.TopDirectoryOnly),
				(Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory), SearchOption.TopDirectoryOnly),
				(Environment.GetFolderPath(Environment.SpecialFolder.StartMenu), SearchOption.AllDirectories),
				(Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu), SearchOption.AllDirectories),
				(Path.Combine(appData, "Microsoft", "Internet Explorer", "Quick Launch", "User Pinned", "TaskBar"), SearchOption.TopDirectoryOnly)
			})
			{
				try
				{
					if (!Directory.Exists(dir))
					{
						continue;
					}
					foreach (string f in Directory.EnumerateFiles(dir, "*.lnk", opt))
					{
						string n = Path.GetFileName(f).ToLowerInvariant();
						if (AppNames.Any(a => n.Contains(a, StringComparison.Ordinal)))
						{
							links.Add(f + "=" + Hash(f));
						}
					}
				}
				catch
				{
				}
			}
			links.Sort(StringComparer.OrdinalIgnoreCase);
			b.Links = string.Join(";", links.Select(l => Path.GetFileName(l)));
			b.LinkCount = links.Count;
			b.Registry = RegistryValue("Software\\Microsoft\\Windows\\CurrentVersion\\Explorer\\Taskband", "Favorites")
				+ "|" + RegistryValue("Control Panel\\Desktop", "WallPaper")
				+ "|" + RegistryValue("Control Panel\\Cursors", "Arrow");
			StringBuilder outside = new StringBuilder();
			foreach (string dir in new string[2] { DataRoot, AppContext.BaseDirectory })
			{
				if (Directory.Exists(dir))
				{
					foreach (string f in Directory.GetFileSystemEntries(dir).Select(p => Path.GetFileName(p) ?? "").Where(n => !n.StartsWith("log", StringComparison.OrdinalIgnoreCase)).OrderBy(n => n, StringComparer.OrdinalIgnoreCase))
					{
						outside.Append(f).Append(';');
					}
				}
				outside.Append('|');
			}
			b.Outside = outside.ToString();
			return b;
		}

		private static string Hash(string f)
		{
			try
			{
				return File.Exists(f) ? Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(f)))[..16] : "-";
			}
			catch (Exception ex)
			{
				return "unreadable:" + ex.GetType().Name;
			}
		}

		private static string RegistryValue(string key, string name)
		{
			try
			{
				using RegistryKey? k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(key, writable: false);
				object? v = k?.GetValue(name);
				return v switch
				{
					null => "-",
					byte[] bytes => Convert.ToHexString(SHA256.HashData(bytes))[..16],
					_ => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(v.ToString() ?? "")))[..16]
				};
			}
			catch
			{
				return "?";
			}
		}
	}

	// ===================================================================== helpers

	private static BitmapSource RenderElement(FrameworkElement el, int w, int h)
	{
		el.Measure(new Size(w, h));
		el.Arrange(new Rect(0, 0, w, h));
		el.UpdateLayout();
		RenderTargetBitmap rtb = new RenderTargetBitmap(w, h, 96.0, 96.0, PixelFormats.Pbgra32);
		rtb.Render(el);
		rtb.Freeze();
		return rtb;
	}

	private static BitmapSource Render(DrawingVisual dv, int w, int h)
	{
		RenderTargetBitmap rtb = new RenderTargetBitmap(w, h, 96.0, 96.0, PixelFormats.Pbgra32);
		rtb.Render(dv);
		rtb.Freeze();
		return rtb;
	}

	private static void Label(DrawingContext dc, string text, double x, double y, double size, Brush brush)
	{
		FormattedText ft = new FormattedText(text, System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), size, brush, 1.0);
		dc.DrawText(ft, new Point(x, y));
	}

	private static byte[] Bytes(BitmapSource src)
	{
		BitmapSource conv = src.Format == PixelFormats.Pbgra32 ? src : new FormatConvertedBitmap(src, PixelFormats.Pbgra32, null, 0.0);
		int stride = conv.PixelWidth * 4;
		byte[] px = new byte[stride * conv.PixelHeight];
		conv.CopyPixels(px, stride, 0);
		return px;
	}

	private static Color Pixel(BitmapSource src, int x, int y)
	{
		BitmapSource conv = src.Format == PixelFormats.Bgra32 ? src : new FormatConvertedBitmap(src, PixelFormats.Bgra32, null, 0.0);
		byte[] px = new byte[4];
		conv.CopyPixels(new Int32Rect(x, y, 1, 1), px, 4, 0);
		return Color.FromArgb(px[3], px[2], px[1], px[0]);
	}

	private static BitmapSource Decode(string path)
	{
		using FileStream fs = File.OpenRead(path);
		BitmapDecoder d = BitmapDecoder.Create(fs, BitmapCreateOptions.PreservePixelFormat | BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.OnLoad);
		BitmapSource f = d.Frames[0];
		f.Freeze();
		return f;
	}

	private static (int Diff, int Total) PixelDiff(string a, string b)
	{
		byte[] x = Bytes(Decode(a));
		byte[] y = Bytes(Decode(b));
		if (x.Length != y.Length)
		{
			return (-1, x.Length / 4);
		}
		int diff = 0;
		for (int i = 0; i < x.Length; i += 4)
		{
			if (x[i] != y[i] || x[i + 1] != y[i + 1] || x[i + 2] != y[i + 2] || x[i + 3] != y[i + 3])
			{
				diff++;
			}
		}
		return (diff, x.Length / 4);
	}

	private static void SavePng(BitmapSource bmp, string path)
	{
		PngBitmapEncoder enc = new PngBitmapEncoder();
		enc.Frames.Add(BitmapFrame.Create(bmp));
		using FileStream fs = File.Create(path);
		enc.Save(fs);
	}
}
