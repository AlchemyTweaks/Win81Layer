#nullable enable

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Win81Layer;

// --seasonaltest, ultra dark part: the true black Dark rest. The picture fixtures, the upgrade from the earlier Dark rest
// over the same restore point, the startup predicate, the link file for the Metro Tab extension (in temporary folders
// only), the contrast rules of the palette and of the rendered surfaces, the states that must stay visible, the glare
// census and the Start picture. Read-only like the rest of the harness.
internal static partial class SeasonalDiagnostics
{
	// --restpicture <path>: a local picture used instead of the generated fixture (a manual look; the marker checks are
	// then reported as skipped). It is copied into the output folder only.
	internal static string? RestPictureArg;

	private static string _fixtureEmpty = "";

	private static string _fixturePicture = "";

	private static bool _syntheticPicture = true;

	// The generated picture: the drawn black grid at 2000 x 1125 (the size of a typical 16:9 picture larger than the
	// screen) with one #404040 square centred at (1771, 833), which lands on the screen at (1700, 800) after the fill.
	private static readonly Color MarkerColor = Color.FromRgb(0x40, 0x40, 0x40);

	private static void PrepareRestFixtures(string outDir)
	{
		_fixtureEmpty = Path.Combine(outDir, "fixture-empty");
		_fixturePicture = Path.Combine(outDir, "fixture-picture");
		Directory.CreateDirectory(_fixtureEmpty);
		Directory.CreateDirectory(_fixturePicture);
		foreach (string f in Directory.GetFiles(_fixturePicture))
		{
			File.Delete(f);
		}
		if (RestPictureArg != null && File.Exists(RestPictureArg))
		{
			File.Copy(RestPictureArg, Path.Combine(_fixturePicture, "rest" + Path.GetExtension(RestPictureArg).ToLowerInvariant()), overwrite: true);
			_syntheticPicture = false;
			return;
		}
		_syntheticPicture = true;
		BitmapSource scene = SeasonalArt.RenderRestScene(2000, 1125, 1000.0);
		DrawingVisual dv = new DrawingVisual();
		using (DrawingContext dc = dv.RenderOpen())
		{
			dc.DrawImage(scene, new Rect(0, 0, 2000, 1125));
			dc.DrawRectangle(new SolidColorBrush(MarkerColor), null, new Rect(1771 - 30, 833 - 30, 60, 60));
		}
		SavePng(new FormatConvertedBitmap(Render(dv, 2000, 1125), PixelFormats.Bgr24, null, 0.0), Path.Combine(_fixturePicture, "rest.png"));
	}

	// ===================================================================== pure checks

	private static void UltraDarkPureChecks(string outDir)
	{
		{
			string rest = SeasonalArt.WallpaperFileName(SeasonalArt.RestArt, 1920, 1080);
			string h = SeasonalArt.WallpaperFileName("halloween", 1920, 1080);
			string c = SeasonalArt.WallpaperFileName("christmas", 1920, 1080);
			Check("rest-wallpaper-name", rest == "rest-v2-1920x1080.png" && h == "halloween-v1-1920x1080.png" && c == "christmas-v1-1920x1080.png", $"{rest} {h} {c}");
		}
		UpgradeChecks();
		StartupPredicateChecks();
		LinkChecks(outDir);
		IconChecks();
		RestVisibilityPureChecks(outDir);
		RestStatesPureChecks(outDir);
	}

	// ----- the earlier Dark rest look brought up to date over the same restore point

	private const string LegacyRestFile = FakeSeasonalWorld.Folder + "rest-v1-1920x1080.png";

	// A world that had Dark rest from the earlier version: Normal first (Windows dark or light), Dark rest applied, then
	// the Start values and the drawn file rewritten to what the earlier version left. original is the world before the
	// first preset.
	private static FakeSeasonalWorld LegacyRest(bool windowsDark, out string original, string? pictureName = null, string? accent = null, bool dropRestorePoint = false)
	{
		FakeSeasonalWorld w = new FakeSeasonalWorld();
		if (windowsDark)
		{
			w.Apps = Dw(WindowsThemeMode.AppsName, "0");
			w.Sys = Dw(WindowsThemeMode.SystemName, "0");
		}
		original = w.World();
		SeasonalTheme.CreateEngine(w).Apply(SeasonalTheme.Rest);
		w.SetSettings(delegate(AppSettings x)
		{
			x.StartPattern = SeasonalTheme.LegacyRestPattern;
			x.StartBgColor = SeasonalTheme.LegacyRestBase;
			x.StartAccentColor = accent ?? SeasonalTheme.LegacyRestAccent;
		});
		foreach (string f in w.Files.ToList())
		{
			w.Files.Remove(f);
		}
		w.Files.Add(LegacyRestFile);
		foreach (MonitorWallpaper m in w.Monitors)
		{
			m.Path = LegacyRestFile;
		}
		SeasonalStateFile st = w.PeekState();
		if (st.Original?.Wallpaper != null)
		{
			st.Original.Wallpaper.AppliedPath = LegacyRestFile;
		}
		w.StateJson = dropRestorePoint ? null : JsonSerializer.Serialize(st, SeasonalTheme.Json);
		w.PictureName = pictureName;
		w.Now = w.Now.AddDays(3);
		w.Writes = 0;
		w.ThemeWrites = 0;
		return w;
	}

	private static void UpgradeChecks()
	{
		SeasonPreset rest = SeasonalTheme.Find(SeasonalTheme.Rest)!;
		const string darkTheme = "apps=DWord:0|sys=DWord:0";
		const string lightTheme = "apps=DWord:1|sys=absent";
		bool NewLook(AppSettings s)
		{
			return s.SeasonalActive == SeasonalTheme.Rest && s.StartBgMode == "pattern" && s.StartPattern == rest.Pattern && s.StartBgColor == rest.Base && s.StartAccentColor == rest.Accent;
		}
		// the migration itself
		foreach ((string name, string? picture, string expect) in new (string, string?, string)[2]
		{
			("rest-upgrade-migrates", null, "rest-v2-1920x1080.png"),
			("rest-upgrade-picture", "rest-picture-0badf00d-1920x1080.png", "rest-picture-0badf00d-1920x1080.png")
		})
		{
			FakeSeasonalWorld w = LegacyRest(windowsDark: true, out _, picture);
			bool legacy = SeasonalTheme.IsLegacyRestLook(w.Peek());
			string origBefore = w.OriginalJson(blankApplied: true);
			SeasonalStateFile before = w.PeekState();
			string theme = w.ThemeText();
			SeasonalResult r = SeasonalTheme.CreateEngine(w).RecoverAndMigrate();
			SeasonalStateFile after = w.PeekState();
			bool ok = legacy && r.Success && NewLook(w.Peek()) && w.OriginalJson(blankApplied: true) == origBefore
				&& after.Original!.CapturedUtc == before.Original!.CapturedUtc && after.ActiveSinceUtc == before.ActiveSinceUtc && after.Phase == SeasonalTheme.PhaseIdle
				&& w.Monitors.All(m => m.Path == FakeSeasonalWorld.Folder + expect) && after.Original.Wallpaper?.AppliedPath == FakeSeasonalWorld.Folder + expect
				&& w.ThemeText() == theme && w.ThemeWrites == 0 && !w.Files.Contains(LegacyRestFile);
			Check(name, ok, $"legacy={legacy} msg={r.Message} world={w.World()} files={string.Join(",", w.Files)} themeWrites={w.ThemeWrites} sameOriginal={w.OriginalJson(blankApplied: true) == origBefore}");
		}
		// Normal afterwards: exactly the world before the first preset
		{
			FakeSeasonalWorld w = LegacyRest(windowsDark: true, out string original);
			SeasonalEngine e = SeasonalTheme.CreateEngine(w);
			SeasonalResult m = e.RecoverAndMigrate();
			SeasonalResult off = e.Apply(SeasonalTheme.Normal);
			SeasonalStateFile st = w.PeekState();
			bool ok = m.Success && off.Success && w.World() == original && st.Original == null && st.Phase == SeasonalTheme.PhaseIdle && w.Files.Count == 0 && w.ThemeText() == darkTheme;
			Check("rest-upgrade-normal-exact", ok, $"original={original} after={w.World()} msg={off.Message} files={w.Files.Count}");
		}
		// Windows was light before the first Dark rest: the migration keeps it dark, Normal brings light back
		{
			FakeSeasonalWorld w = LegacyRest(windowsDark: false, out string original);
			SeasonalEngine e = SeasonalTheme.CreateEngine(w);
			SeasonalResult m = e.RecoverAndMigrate();
			string during = w.ThemeText();
			SeasonalResult off = e.Apply(SeasonalTheme.Normal);
			bool ok = m.Success && during == darkTheme && off.Success && w.ThemeText() == lightTheme && w.World() == original;
			Check("rest-upgrade-light-windows", ok, $"during={during} after={w.ThemeText()} world={w.World()}");
		}
		// a crash after every write of the migration, then the plain recovery and Normal
		{
			FakeSeasonalWorld probe = LegacyRest(windowsDark: true, out _);
			SeasonalTheme.CreateEngine(probe).RecoverAndMigrate();
			int total = probe.Writes;
			List<string> failures = new List<string>();
			for (int n = 1; n <= total; n++)
			{
				FakeSeasonalWorld w = LegacyRest(windowsDark: true, out string original);
				w.CrashAfter = n;
				bool crashed = false;
				try
				{
					SeasonalTheme.CreateEngine(w).RecoverAndMigrate();
				}
				catch (SimulatedCrash)
				{
					crashed = true;
				}
				w.CrashAfter = -1;
				SeasonalTheme.CreateEngine(w).Recover();
				bool restOn = w.LoadState().Phase == SeasonalTheme.PhaseIdle && w.Peek().SeasonalActive == SeasonalTheme.Rest;
				SeasonalTheme.CreateEngine(w).Apply(SeasonalTheme.Normal);
				if (!crashed || !restOn || w.World() != original || w.LoadState().Phase != SeasonalTheme.PhaseIdle)
				{
					failures.Add($"n={n} crashed={crashed} restOn={restOn} world={w.World()}");
				}
			}
			Check("crash-recovery-rest-upgrade", failures.Count == 0 && total >= 3, failures.Count == 0 ? $"{total} crash points" : string.Join(" ; ", failures));
		}
		// a tweaked earlier look is kept (its Start values and its restore point; only the outdated desktop file may be
		// redrawn, as for any picture change), and a lost restore point is never migrated
		{
			FakeSeasonalWorld t = LegacyRest(windowsDark: true, out _, accent: "#FF445566");
			AppSettings ts = t.Peek();
			string tStart = $"{ts.StartBgMode}|{ts.StartPattern}|{ts.StartBgColor}|{ts.StartAccentColor}|{ts.SeasonalActive}";
			string tOriginal = t.OriginalJson(blankApplied: true);
			string tTheme = t.ThemeText();
			SeasonalResult tr = SeasonalTheme.CreateEngine(t).RecoverAndMigrate();
			AppSettings ta = t.Peek();
			bool tweaked = tr.Success && $"{ta.StartBgMode}|{ta.StartPattern}|{ta.StartBgColor}|{ta.StartAccentColor}|{ta.SeasonalActive}" == tStart
				&& t.OriginalJson(blankApplied: true) == tOriginal && t.ThemeText() == tTheme && t.ThemeWrites == 0;
			Check("rest-upgrade-tweaked-kept", tweaked, $"msg={tr.Message} start={ta.StartPattern}/{ta.StartBgColor}/{ta.StartAccentColor} sameOriginal={t.OriginalJson(blankApplied: true) == tOriginal}");
			FakeSeasonalWorld l = LegacyRest(windowsDark: true, out _, dropRestorePoint: true);
			string lBefore = l.World();
			SeasonalResult lr = SeasonalTheme.CreateEngine(l).RecoverAndMigrate();
			bool lost = lr.Success && l.World() == lBefore && l.StateJson == null && l.Writes == 0;
			Check("rest-upgrade-lost-restore-point", lost, $"msg={lr.Message} writes={l.Writes} state={(l.StateJson == null ? "none" : "written")}");
		}
	}

	private static void StartupPredicateChecks()
	{
		List<string> bad = new List<string>();
		void Want(string label, bool want, AppSettings s, SeasonalStateFile st, bool exists, bool changed)
		{
			bool got = SeasonalTheme.NeedsStartupWork(s, st, unreadable: false, degraded: false, _ => exists, (_, _) => changed);
			if (got != want)
			{
				bad.Add($"{label}: got {got} want {want}");
			}
		}
		FakeSeasonalWorld legacy = LegacyRest(windowsDark: true, out _);
		Want("legacy look with a restore point", true, legacy.Peek(), legacy.PeekState(), exists: true, changed: false);
		FakeSeasonalWorld fresh = From(SeasonalTheme.Rest, out _);
		Want("new look, expected file", false, fresh.Peek(), fresh.PeekState(), exists: true, changed: false);
		Want("new look, picture changed", true, fresh.Peek(), fresh.PeekState(), exists: true, changed: true);
		Want("new look, file missing", true, fresh.Peek(), fresh.PeekState(), exists: false, changed: false);
		SeasonalStateFile pending = fresh.PeekState();
		pending.Phase = SeasonalTheme.PhaseApplying;
		Want("pending phase", true, fresh.Peek(), pending, exists: true, changed: false);
		FakeSeasonalWorld normal = new FakeSeasonalWorld();
		Want("normal without a restore point", false, normal.Peek(), normal.PeekState(), exists: true, changed: false);
		FakeSeasonalWorld lost = LegacyRest(windowsDark: true, out _, dropRestorePoint: true);
		Want("legacy look without a restore point", false, lost.Peek(), lost.PeekState(), exists: true, changed: false);
		Check("rest-startup-predicate", bad.Count == 0, string.Join(" ; ", bad));
	}

	// ----- the link file for the Metro Tab extension (temporary folders under the output folder only)

	private static void LinkChecks(string outDir)
	{
		string root = Path.Combine(outDir, "link");
		try
		{
			if (Directory.Exists(root))
			{
				Directory.Delete(root, recursive: true);
			}
		}
		catch
		{
		}
		Directory.CreateDirectory(root);
		DateTime t0 = new DateTime(2026, 10, 17, 9, 30, 0, DateTimeKind.Utc);
		Regex accentRx = new Regex("^#[0-9A-F]{6}$");
		// content for every look
		foreach ((string look, string marker, string accent) in new (string, string, string)[4]
		{
			("normal", "", "#FF2672EC"), ("halloween", SeasonalTheme.Halloween, "#FFE0620B"), ("christmas", SeasonalTheme.Christmas, "#FFC8102E"), ("rest", SeasonalTheme.Rest, "#FF44636A")
		})
		{
			string folder = Path.Combine(root, "content-" + look);
			Directory.CreateDirectory(folder);
			AppSettings s = new AppSettings { SeasonalActive = marker, StartAccentColor = accent };
			ShellLookLink.LookState st = ShellLookLink.Current(s);
			ShellLookLink.Outcome o = ShellLookLink.WriteIfChanged(folder, st, t0);
			string file = Path.Combine(folder, ShellLookLink.FileName);
			List<string> bad = new List<string>();
			try
			{
				byte[] bytes = File.ReadAllBytes(file);
				if (bytes.Length == 0 || bytes[0] != (byte)'{')
				{
					bad.Add("first byte is not '{' (BOM?)");
				}
				using JsonDocument doc = JsonDocument.Parse(bytes);
				JsonElement r = doc.RootElement;
				if (r.GetProperty("schema").GetInt32() != 1)
				{
					bad.Add("schema");
				}
				if (r.GetProperty("look").GetString() != look)
				{
					bad.Add("look=" + r.GetProperty("look").GetString());
				}
				if (r.GetProperty("ultraDark").GetBoolean() != (look == "rest"))
				{
					bad.Add("ultraDark");
				}
				string acc = r.GetProperty("accent").GetString() ?? "";
				if (!accentRx.IsMatch(acc) || acc != "#" + accent.Substring(3))
				{
					bad.Add("accent=" + acc);
				}
				string changed = r.GetProperty("changedUtc").GetString() ?? "";
				if (!changed.EndsWith("Z", StringComparison.Ordinal) || !DateTime.TryParse(changed, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out DateTime parsed) || parsed != t0)
				{
					bad.Add("changedUtc=" + changed);
				}
			}
			catch (Exception ex)
			{
				bad.Add(ex.Message);
			}
			Check("link-content-" + look, o == ShellLookLink.Outcome.Written && bad.Count == 0, $"outcome={o} " + string.Join(" ; ", bad));
		}
		string main = Path.Combine(root, "content-rest");
		string target = Path.Combine(main, ShellLookLink.FileName);
		ShellLookLink.LookState restState = ShellLookLink.Current(new AppSettings { SeasonalActive = SeasonalTheme.Rest, StartAccentColor = "#FF44636A" });
		// no rewrite when nothing changed
		{
			byte[] before = File.ReadAllBytes(target);
			DateTime mtime = File.GetLastWriteTimeUtc(target);
			ShellLookLink.Outcome o = ShellLookLink.WriteIfChanged(main, restState, t0.AddHours(1));
			bool same = before.AsSpan().SequenceEqual(File.ReadAllBytes(target)) && File.GetLastWriteTimeUtc(target) == mtime;
			Check("link-no-rewrite", o == ShellLookLink.Outcome.Unchanged && same, $"outcome={o} same={same}");
		}
		// a changed accent is written, with a newer time stamp
		{
			ShellLookLink.LookState changed = restState with { Accent = "#4E7179" };
			ShellLookLink.Outcome o = ShellLookLink.WriteIfChanged(main, changed, t0.AddMinutes(1));
			bool read = ShellLookLink.TryRead(target, out ShellLookLink.LookState back);
			string text = File.ReadAllText(target);
			Check("link-rewrite-on-change", o == ShellLookLink.Outcome.Written && read && back == changed && text.Contains("2026-10-17T09:31:00Z", StringComparison.Ordinal), $"outcome={o} back={back}");
			ShellLookLink.WriteIfChanged(main, restState, t0.AddMinutes(2));
		}
		// atomic: no temporary file left; a failure before the move keeps the old file and removes the temporary one
		{
			byte[] before = File.ReadAllBytes(target);
			bool threw = false;
			ShellLookLink.Outcome o = ShellLookLink.Outcome.Written;
			try
			{
				ShellLookLink.QaFailBeforeMove = true;
				o = ShellLookLink.WriteIfChanged(main, restState with { Look = "normal", UltraDark = false }, t0.AddMinutes(3));
			}
			catch
			{
				threw = true;
			}
			finally
			{
				ShellLookLink.QaFailBeforeMove = false;
			}
			string[] names = Directory.GetFiles(main).Select(Path.GetFileName).Select(n => n ?? "").ToArray();
			bool intact = before.AsSpan().SequenceEqual(File.ReadAllBytes(target));
			bool clean = names.Length == 1 && names[0] == ShellLookLink.FileName && !Directory.GetFiles(root, "*.tmp-*", SearchOption.AllDirectories).Any();
			Check("link-atomic", !threw && o == ShellLookLink.Outcome.Failed && intact && clean, $"outcome={o} threw={threw} intact={intact} files={string.Join(",", names)}");
		}
		// the extension folder: found by its manifest name next to the launcher folder, or the override
		{
			string tree = Path.Combine(root, "detect");
			string launcher = Path.Combine(tree, "Launcher-x64");
			string ext = Path.Combine(tree, "ChromeExtension");
			Directory.CreateDirectory(launcher);
			void Manifest(string name, string json)
			{
				Directory.CreateDirectory(Path.Combine(ext, name));
				File.WriteAllText(Path.Combine(ext, name, "manifest.json"), json);
			}
			Manifest("Broken", "{ this is not json");
			Manifest("MetroNewTab", "{\"manifest_version\":3,\"name\":\"Metro Tab\",\"version\":\"1.0\"}");
			Manifest("Other", "{\"manifest_version\":3,\"name\":\"Other\"}");
			Manifest("Aaa", "{\"manifest_version\":3,\"name\":\"Something else\"}");
			ShellLookLink.ResetCache();
			string? auto = ShellLookLink.FindExtensionFolder("", launcher + "\\");
			ShellLookLink.ResetCache();
			string over = Path.Combine(ext, "Other");
			string? chosen = ShellLookLink.FindExtensionFolder(over, launcher);
			ShellLookLink.ResetCache();
			string? missing = ShellLookLink.FindExtensionFolder(Path.Combine(tree, "nowhere"), launcher);
			string lonely = Path.Combine(root, "lonely", "Launcher-x64");
			Directory.CreateDirectory(lonely);
			ShellLookLink.ResetCache();
			string? none = ShellLookLink.FindExtensionFolder("", lonely);
			bool created = Directory.Exists(Path.Combine(root, "lonely", "ChromeExtension")) || Directory.Exists(Path.Combine(tree, "nowhere"));
			ShellLookLink.ResetCache();
			bool ok = string.Equals(auto, Path.Combine(ext, "MetroNewTab"), StringComparison.OrdinalIgnoreCase) && string.Equals(chosen, over, StringComparison.OrdinalIgnoreCase)
				&& missing == null && none == null && !created;
			Check("link-detect", ok, $"auto={auto} override={chosen} missingOverride={missing} noExtensionFolder={none} created={created}");
		}
		// a locked target: Failed, no exception
		{
			bool threw = false;
			ShellLookLink.Outcome o = ShellLookLink.Outcome.Written;
			try
			{
				using FileStream hold = new FileStream(target, FileMode.Open, FileAccess.Read, FileShare.None);
				o = ShellLookLink.WriteIfChanged(main, restState with { Accent = "#000000" }, t0.AddMinutes(4));
			}
			catch
			{
				threw = true;
			}
			bool clean = !Directory.GetFiles(main, "*.tmp-*").Any();
			Check("link-locked", !threw && o == ShellLookLink.Outcome.Failed && clean, $"outcome={o} threw={threw} tempLeft={!clean}");
		}
		// Sync never writes in read-only diagnostics (the real folder is compared before and after the run)
		ShellLookLink.Sync("qa-readonly");
	}

	// ----- icons on the black panes

	private static void IconChecks()
	{
		// a black monochrome glyph (a filled square on a transparent 32 x 32 icon)
		int size = 32;
		byte[] px = new byte[size * size * 4];
		for (int y = 6; y < 26; y++)
		{
			for (int x = 6; x < 26; x++)
			{
				px[(y * size + x) * 4 + 3] = 255;
			}
		}
		BitmapSource black = BitmapSource.Create(size, size, 96, 96, PixelFormats.Bgra32, null, px, size * 4);
		black.Freeze();
		int MinChannel(ImageSource? img)
		{
			if (img is not BitmapSource b)
			{
				return -1;
			}
			byte[] p = Pixels(b);
			int min = 255;
			for (int i = 0; i < p.Length; i += 4)
			{
				if (p[i + 3] >= 200)
				{
					min = Math.Min(min, Math.Min(p[i], Math.Min(p[i + 1], p[i + 2])));
				}
			}
			return min;
		}
		int lifted = MinChannel(AppInventory.EnsureVisibleOnDark(black));
		object[] values = new object[2] { black, null! };
		object? restPath = RestIconConverter.Instance.Convert(values, typeof(ImageSource), 24.0, CultureInfo.InvariantCulture);
		object? normalPath = SeasonalIconConverter.Instance.Convert(values, typeof(ImageSource), 24.0, CultureInfo.InvariantCulture);
		int restMin = MinChannel(restPath as ImageSource);
		bool cached = ReferenceEquals(RestIconConverter.Instance.Convert(values, typeof(ImageSource), 24.0, CultureInfo.InvariantCulture), restPath);
		Check("rest-icons-visible", lifted >= 160 && restMin >= 160 && cached && ReferenceEquals(normalPath, black),
			$"lifted min channel={lifted} restSearchPath={restMin} cached={cached} normalPathSameInstance={ReferenceEquals(normalPath, black)}");
	}

	// ----- the palette, pair by pair

	private static void PaletteContrastChecks(TaskbarWindow taskbar)
	{
		List<string> bad = new List<string>();
		List<string> got = new List<string>();
		Color Tok(string key)
		{
			return (DarkRestPalette.Token(key) as SolidColorBrush)?.Color ?? Colors.Magenta;
		}
		void Pair(string name, Color fg, Color bg, double min)
		{
			if (fg.A < 255)
			{
				fg = Win7Palette.Over(fg, bg);
			}
			double c = Contrast(fg, bg);
			got.Add($"{name}={c:F2}");
			if (c < min - 1e-9)
			{
				bad.Add($"{name} {Hex(fg)} on {Hex(bg)} = {c:F2} < {min}");
			}
		}
		Color canvas = DarkRestPalette.CanvasColor;
		Color panel = DarkRestPalette.PanelColor;
		Color popup = DarkRestPalette.PopupColor;
		Color card = DarkRestPalette.CardColor;
		Color field = DarkRestPalette.FieldColor;
		Color hover = DarkRestPalette.HoverColor;
		Color pressed = DarkRestPalette.PressedColor;
		(string, Color)[] surfaces = new (string, Color)[7] { ("canvas", canvas), ("panel", panel), ("popup", popup), ("card", card), ("field", field), ("hover", hover), ("pressed", pressed) };
		foreach ((string n, Color bg) in surfaces)
		{
			Pair("ink/" + n, DarkRestPalette.InkColor, bg, 4.5);
			Pair("accentInk/" + n, DarkRestPalette.AccentInkColor, bg, 4.5);
		}
		foreach ((string n, Color bg) in surfaces.Take(6))
		{
			Pair("ink2/" + n, DarkRestPalette.Ink2Color, bg, 7.0);
			Pair("hint/" + n, DarkRestPalette.HintColor, bg, 4.5);
			Pair("boundary/" + n, DarkRestPalette.BoundaryColor, bg, 3.0);
			Pair("accentLine/" + n, DarkRestPalette.AccentLineColor, bg, 3.0);
		}
		Pair("onAccentInk/accentFill", DarkRestPalette.InkOnAccentColor, DarkRestPalette.AccentFillColor, 4.5);
		Pair("onAccentInk/accent", DarkRestPalette.InkOnAccentColor, DarkRestPalette.AccentColor, 4.5);
		Pair("ink/accent (caption)", DarkRestPalette.InkColor, DarkRestPalette.AccentColor, 4.5);
		Pair("ink2/inactive caption", DarkRestPalette.Ink2Color, (DarkRestPalette.CaptionInactive as SolidColorBrush)!.Color, 4.5);
		Pair("accentFill/panel", DarkRestPalette.AccentFillColor, panel, 3.0);
		Pair("accentFill/popup", DarkRestPalette.AccentFillColor, popup, 3.0);
		Pair("accentFill/canvas", DarkRestPalette.AccentFillColor, canvas, 3.0);
		Pair("accentLine/sliderTrack", DarkRestPalette.AccentLineColor, DarkRestPalette.SliderTrack, 3.0);
		// the OSD: empty segments are hollow outlines, so fill, muted fill and outline each need 3:1 against the card
		Color osdCard = Win7Palette.Over(((SolidColorBrush)DarkRestPalette.OsdCard).Color, canvas);
		Pair("osd fill/card", DarkRestPalette.AccentLineColor, osdCard, 3.0);
		Pair("osd muted/card", ((SolidColorBrush)DarkRestPalette.OsdMuted).Color, osdCard, 3.0);
		Pair("osd empty outline/card", DarkRestPalette.BoundaryColor, osdCard, 3.0);
		// the Metro controls (application scope and PC Settings)
		Color surface = Tok("Metro81.Surface");
		Pair("control ink/surface", Tok("Metro81.Ink"), surface, 4.5);
		Pair("control ink2/surface", Tok("Metro81.Ink2"), surface, 4.5);
		Pair("control disabled/surface", Tok("Metro81.DisabledInk"), surface, 4.5);
		Pair("control disabled/disabledSurface", Tok("Metro81.DisabledInk"), Tok("Metro81.DisabledSurface"), 4.5);
		Pair("field border/surface", Tok("Metro81.FieldBorder"), surface, 3.0);
		Pair("field border/page", Tok("Metro81.FieldBorder"), canvas, 3.0);
		Pair("focus border/surface", Tok("Metro81.FocusBorder"), surface, 3.0);
		Pair("check box border/surface", Tok("Metro81.GlyphBorder"), surface, 3.0);
		Pair("toggle rail/page", Tok("Metro81.Border"), canvas, 3.0);
		Pair("toggle on/off", Tok("MenuAccent"), Tok("Metro81.ToggleTrack"), 3.0);
		Pair("toggle thumb/on", Tok("Metro81.ToggleThumb"), Tok("MenuAccent"), 3.0);
		Pair("toggle thumb/off", Tok("Metro81.ToggleThumb"), Tok("Metro81.ToggleTrack"), 3.0);
		Pair("slider fill/track", Tok("Metro81.SliderFill"), Tok("Metro81.SliderTrack"), 3.0);
		Pair("slider thumb/track", Tok("Metro81.SliderThumbFill"), Tok("Metro81.SliderTrack"), 3.0);
		Pair("dark slider thumb/track", Tok("Metro81.SliderDarkThumb"), Tok("Metro81.SliderDarkTrack"), 3.0);
		Pair("dark slider fill/track", Tok("Metro81.SliderFill"), Tok("Metro81.SliderDarkTrack"), 3.0);
		Pair("link/page", Tok("Metro81.LinkInk"), canvas, 4.5);
		Pair("link hover/page", Tok("Metro81.LinkInkHover"), canvas, 4.5);
		Pair("highlight ink/surface", Tok("Metro81.HighlightInk"), Tok("Metro81.HighlightSurface"), 4.5);
		Pair("highlight gesture/surface", Tok("Metro81.HighlightGesture"), Tok("Metro81.HighlightSurface"), 4.5);
		Pair("menu ink/menu", Tok("Metro81.Ink"), Tok("Metro81.MenuSurface"), 4.5);
		Pair("menu ink/menu hover", Tok("Metro81.Ink"), Tok("Metro81.MenuHover"), 4.5);
		Pair("selected row/page", Tok("MenuAccent"), canvas, 3.0);
		Pair("on-accent ink/selected row", Tok("Metro81.OnAccentInk"), Tok("MenuAccent"), 4.5);
		Pair("on-accent ink/primary hover", Tok("Metro81.OnAccentInk"), Tok("MenuAccentPressed"), 4.5);
		// slider and toggle thumbs in every state, the drag value box, track and button outlines, the selected
		// navigation row and its ink, tooltips, selected text, and disabled text at the disabled opacity
		Pair("slider thumb hover/track", Tok("Metro81.SliderThumbHover"), Tok("Metro81.SliderTrack"), 3.0);
		Pair("slider thumb hover/page", Tok("Metro81.SliderThumbHover"), canvas, 3.0);
		Pair("slider thumb drag/track", Tok("Metro81.SliderThumbDrag"), Tok("Metro81.SliderTrack"), 3.0);
		Pair("slider thumb drag/page", Tok("Metro81.SliderThumbDrag"), canvas, 3.0);
		Pair("toggle thumb pressed/on", Tok("Metro81.ToggleThumbPressed"), Tok("MenuAccent"), 3.0);
		Pair("toggle thumb pressed/off", Tok("Metro81.ToggleThumbPressed"), Tok("Metro81.ToggleTrack"), 3.0);
		Pair("value box ink/box", Tok("Metro81.ValuePopupInk"), Tok("Metro81.ValuePopupSurface"), 4.5);
		Pair("track outline/page", Tok("Metro81.SliderTrackBorder"), canvas, 3.0);
		Pair("track outline/pane", Tok("Metro81.SliderTrackBorder"), panel, 3.0);
		Pair("slider fill/progress track", Tok("Metro81.SliderFill"), Tok("Metro81.ProgressTrack"), 3.0);
		Pair("button outline/page", Tok("Metro81.ButtonBorder"), canvas, 3.0);
		Pair("button outline/pane", Tok("Metro81.ButtonBorder"), panel, 3.0);
		Pair("nav selected/nav pane", Tok("Metro81.CommandSelected"), panel, 3.0);
		Pair("nav ink/nav selected", Tok("Metro81.NavInk"), Tok("Metro81.CommandSelected"), 4.5);
		Pair("tooltip ink/tooltip", Tok("Metro81.Ink"), Tok("Metro81.TooltipSurface"), 4.5);
		Pair("text under selection/field", Tok("Metro81.Ink"), Win7Palette.Over(Color.FromArgb((byte)Math.Round(255 * (double)DarkRestPalette.ControlValue("Metro81.TextSelectionOpacity")!), Tok("Metro81.TextSelection").R, Tok("Metro81.TextSelection").G, Tok("Metro81.TextSelection").B), Tok("Metro81.Surface")), 3.0);
		Pair("selection/field", Win7Palette.Over(Color.FromArgb((byte)Math.Round(255 * (double)DarkRestPalette.ControlValue("Metro81.TextSelectionOpacity")!), Tok("Metro81.TextSelection").R, Tok("Metro81.TextSelection").G, Tok("Metro81.TextSelection").B), Tok("Metro81.Surface")), Tok("Metro81.Surface"), 2.0);
		double disabledOpacity = (double)DarkRestPalette.ControlValue("Metro81.DisabledOpacity")!;
		Color Faded(Color c, Color under) => Win7Palette.Over(Color.FromArgb((byte)Math.Round(255 * disabledOpacity), c.R, c.G, c.B), under);
		Pair("disabled ink/page", Faded(Tok("Metro81.Ink"), canvas), canvas, 4.5);
		Pair("disabled link/page", Faded(Tok("Metro81.LinkInk"), canvas), canvas, 4.5);
		Pair("disabled nav ink/nav pane", Faded(Tok("Metro81.NavInk"), panel), panel, 4.5);
		// hover washes that can be seen on the black panes
		Pair("command hover/pane", Win7Palette.Over(Tok("Metro81.CommandHover"), panel), panel, 1.4);
		Pair("pane hover/pane", Win7Palette.Over(Tok("Metro81.PaneHover12"), panel), panel, 1.4);
		Pair("list hover/menu", Tok("Metro81.HoverSurface"), Tok("Metro81.MenuSurface"), 1.4);
		Pair("scroll thumb/track", Tok("Metro81.ScrollThumbBrush"), Tok("Metro81.ScrollTrackBrush"), 3.0);
		Pair("scroll thumb hover/track", Tok("Metro81.ScrollThumbHoverBrush"), Tok("Metro81.ScrollTrackBrush"), 3.0);
		Pair("scroll glyph/button", Tok("Metro81.ScrollGlyphBrush"), Tok("Metro81.ScrollButtonBrush"), 4.5);
		if (ColorMath.RelLuminance(Tok("Metro81.FocusBorder")) <= ColorMath.RelLuminance(Tok("Metro81.FieldBorder")))
		{
			bad.Add("the focus border is not brighter than the resting border");
		}
		// the code-built menus
		Color mBg = TaskbarContextMenu.QaMenuColor("M.Bg");
		Pair("menu fg/bg", TaskbarContextMenu.QaMenuColor("M.Fg"), mBg, 4.5);
		Pair("menu disabled/bg", TaskbarContextMenu.QaMenuColor("M.Dis"), mBg, 4.5);
		Pair("menu fg/hover", TaskbarContextMenu.QaMenuColor("M.Fg"), TaskbarContextMenu.QaMenuColor("M.Hover"), 4.5);
		Pair("menu hover/bg", TaskbarContextMenu.QaMenuColor("M.Hover"), mBg, 1.4);
		Pair("menu arrow/bg", TaskbarContextMenu.QaMenuColor("M.Arrow"), mBg, 3.0);
		Pair("menu kb row/bg", TaskbarContextMenu.QaMenuColor("M.KbBg"), mBg, 3.0);
		Pair("menu kb fg/kb row", TaskbarContextMenu.QaMenuColor("M.KbFg"), TaskbarContextMenu.QaMenuColor("M.KbBg"), 4.5);
		Pair("menu kb gesture/kb row", TaskbarContextMenu.QaMenuColor("M.KbGesture"), TaskbarContextMenu.QaMenuColor("M.KbBg"), 4.5);
		// the Windows 7 Start menu
		ResourceDictionary w7 = Win7Palette.Build(true, DarkRestPalette.AccentColor, true, rest: true);
		Color W7(string key)
		{
			return Win7Palette.EndColor(w7[key]);
		}
		Color pane = W7("W7.PaneBg");
		Pair("w7 ink/pane", W7("W7.Ink"), pane, 4.5);
		Pair("w7 ink2/pane", W7("W7.Ink2"), pane, 7.0);
		Pair("w7 group header/pane", W7("W7.GroupHeaderInk"), pane, 4.5);
		Pair("w7 ink/hot", W7("W7.Ink"), W7("W7.HotFill"), 4.5);
		Pair("w7 hot outline/pane", W7("W7.HotStroke"), pane, 3.0);
		Pair("w7 search ink/box", W7("W7.SearchInk"), W7("W7.SearchBg"), 4.5);
		Pair("w7 search hint/box", W7("W7.SearchHint"), W7("W7.SearchBg"), 4.5);
		Pair("w7 search border/box", W7("W7.SearchBorder"), W7("W7.SearchBg"), 3.0);
		Pair("w7 search border/pane", W7("W7.SearchBorder"), pane, 3.0);
		Pair("w7 search focus/box", W7("W7.SearchBorderFocus"), W7("W7.SearchBg"), 3.0);
		Pair("w7 menu ink/menu", W7("W7.MenuInk"), W7("W7.MenuBg"), 4.5);
		Pair("w7 menu disabled/menu", W7("W7.MenuInkDisabled"), W7("W7.MenuBg"), 4.5);
		Pair("w7 menu ink/menu hot", W7("W7.MenuInk"), W7("W7.MenuHotFill"), 4.5);
		Pair("w7 tip ink/tip", W7("W7.TipInk"), W7("W7.TipBg"), 4.5);
		Pair("w7 scroll thumb/pane", W7("W7.ScrollThumb"), pane, 3.0);
		Pair("w7 power outline/backdrop", W7("W7.PowerStroke"), canvas, 3.0);
		Pair("w7 power ink/pane", W7("W7.PowerInk"), pane, 4.5);
		// no Windows 7 blue left: every solid rest brush of the menu (outside the glass tint) stays nearly neutral
		foreach (object key in w7.Keys)
		{
			if (key is string k && !k.StartsWith("W7.Glass", StringComparison.Ordinal) && w7[k] is SolidColorBrush wb && wb.Color.A >= 0x80)
			{
				double ws = Hsl(wb.Color.R, wb.Color.G, wb.Color.B).S;
				if (ws > 0.25 && ColorMath.RelLuminance(wb.Color) > 0.01)
				{
					bad.Add($"w7 {k} {Hex(wb.Color)} saturation {ws:F2} > 0.25");
				}
			}
		}
		// the taskbar flyouts, as the flyout theme wrote them
		Color Res(string key, int stop = 0)
		{
			Brush? b = taskbar.QaResource(key);
			return b is GradientBrush g && stop < g.GradientStops.Count ? g.GradientStops[stop].Color : (b as SolidColorBrush)?.Color ?? Colors.Magenta;
		}
		Pair("flyout ink/body", Res("TB.Ink"), Res("TB.ClockBody"), 4.5);
		Pair("flyout ink/footer", Res("TB.Ink"), Res("TB.ClockFooter"), 4.5);
		Pair("flyout secondary ink/body", Res("TB.FlyoutSecondaryInk"), Res("TB.ClockBody"), 4.5);
		Pair("flyout link/footer", Res("TB.ClockLink"), Res("TB.ClockFooter"), 4.5);
		Pair("flyout link/body", Res("TB.ClockLink"), Res("TB.ClockBody", 1), 4.5);
		Pair("flyout today/body", Res("TB.ClockSelection"), Res("TB.ClockBody"), 3.0);
		Pair("selected day ink/flyout today", Res("TB.DaySelectedInk"), Res("TB.ClockSelection"), 4.5);
		Pair("volume track outline/body", Res("TB.SliderTrackBorder"), Res("TB.ClockBody"), 3.0);
		Pair("volume fill/track", Res("TB.Accent"), Res("TB.SliderTrack"), 3.0);
		Pair("taskbar accent/taskbar", Res("TB.Accent"), DarkRestPalette.TaskbarBackground, 3.0);
		foreach (string key in new string[3] { "TB.NetworkWifiTile", "TB.NetworkConnectionsTile", "TB.NetworkTroubleshootTile" })
		{
			Pair("flyout ink/" + key, Res("TB.Ink"), Res(key), 4.5);
		}
		// Start
		Pair("start ink/canvas", ((SolidColorBrush)DarkRestPalette.Ink).Color, canvas, 4.5);
		Pair("start ink/picture's brightest pixel", ((SolidColorBrush)DarkRestPalette.Ink).Color, Color.FromRgb(57, 63, 63), 7.0);
		Pair("start search ink/field", ((SolidColorBrush)DarkRestPalette.SearchInk).Color, ((SolidColorBrush)DarkRestPalette.SearchField).Color, 4.5);
		Pair("start search hint/field", ((SolidColorBrush)DarkRestPalette.SearchHint).Color, ((SolidColorBrush)DarkRestPalette.SearchField).Color, 4.5);
		Pair("start search glyph/field", ((SolidColorBrush)DarkRestPalette.SearchGlyph).Color, ((SolidColorBrush)DarkRestPalette.SearchField).Color, 3.0);
		Pair("start search border/field", ((SolidColorBrush)DarkRestPalette.FieldBorder).Color, ((SolidColorBrush)DarkRestPalette.SearchField).Color, 3.0);
		Pair("start search border/canvas", ((SolidColorBrush)DarkRestPalette.FieldBorder).Color, canvas, 3.0);
		Pair("start focus ring/canvas", ((SolidColorBrush)DarkRestPalette.StartFocusRing).Color, canvas, 3.0);
		Pair("start new/canvas", ((SolidColorBrush)DarkRestPalette.StartNewInk).Color, canvas, 4.5);
		Pair("start hover/canvas", Win7Palette.Over(((SolidColorBrush)DarkRestPalette.StartHover).Color, canvas), canvas, 1.3);
		// the muted Action Center tiles keep 4.5:1 for their soft ink
		foreach (string hex in new string[6] { "#FF00A0E7", "#FFCF2056", "#FF2DB72C", "#FFFFC40D", "#FF007EE5", "#FF009BAA" })
		{
			Pair("ink/muted " + hex, DarkRestPalette.InkColor, DarkRestPalette.Mute(SeasonalArt.Hex(hex)), 4.5);
		}
		Metrics["RestContrastPalette"] = got;
		Check("rest-contrast-palette", bad.Count == 0, bad.Count == 0 ? $"{got.Count} pairs" : string.Join(" ; ", bad));
	}

	// ===================================================================== rendered checks

	// A pixel rect from DIP metrics (the QA renders are at 96 DPI, so one DIP is one pixel).
	private static Int32Rect R32(double x, double y, double w, double h)
	{
		return new Int32Rect((int)Math.Round(x), (int)Math.Round(y), Math.Max(1, (int)Math.Round(w)), Math.Max(1, (int)Math.Round(h)));
	}

	// WCAG contrast between the 98th-percentile pixel (by luminance) of inkRect and the median of bgRect.
	private static double ContrastIn(string path, Int32Rect inkRect, Int32Rect bgRect)
	{
		BitmapSource bmp = LoadPng(path);
		double[] Lums(Int32Rect r)
		{
			int x = Math.Clamp(r.X, 0, bmp.PixelWidth - 1);
			int y = Math.Clamp(r.Y, 0, bmp.PixelHeight - 1);
			int w = Math.Clamp(r.Width, 1, bmp.PixelWidth - x);
			int h = Math.Clamp(r.Height, 1, bmp.PixelHeight - y);
			byte[] p = Pixels(new CroppedBitmap(bmp, new Int32Rect(x, y, w, h)));
			double[] l = new double[p.Length / 4];
			for (int i = 0; i < l.Length; i++)
			{
				l[i] = ColorMath.RelLuminance(Color.FromRgb(p[i * 4 + 2], p[i * 4 + 1], p[i * 4]));
			}
			Array.Sort(l);
			return l;
		}
		double[] ink = Lums(inkRect);
		double[] bg = Lums(bgRect);
		double li = ink[Math.Min(ink.Length - 1, (int)(ink.Length * 0.98))];
		double lb = bg[bg.Length / 2];
		return (Math.Max(li, lb) + 0.05) / (Math.Min(li, lb) + 0.05);
	}

	// Glare census of one render: the share of pixels with every channel at 0xF0 or more (outside the given image areas),
	// and whether any 20 x 20 block is brighter than the accent ink throughout.
	private static (double WhiteShare, bool BrightBlock) Glare(string path, params Int32Rect[] exclude)
	{
		BitmapSource bmp = LoadPng(path);
		byte[] p = Pixels(bmp);
		int w = bmp.PixelWidth;
		int h = bmp.PixelHeight;
		double limit = ColorMath.RelLuminance(DarkRestPalette.AccentInkColor);
		int[] sum = new int[(w + 1) * (h + 1)];
		long white = 0;
		long counted = 0;
		for (int y = 0; y < h; y++)
		{
			int row = 0;
			for (int x = 0; x < w; x++)
			{
				bool skip = exclude.Any(r => x >= r.X && x < r.X + r.Width && y >= r.Y && y < r.Y + r.Height);
				int i = (y * w + x) * 4;
				bool bright = false;
				if (!skip)
				{
					counted++;
					if (p[i] >= 0xF0 && p[i + 1] >= 0xF0 && p[i + 2] >= 0xF0)
					{
						white++;
					}
					bright = ColorMath.RelLuminance(Color.FromRgb(p[i + 2], p[i + 1], p[i])) > limit;
				}
				row += bright ? 1 : 0;
				sum[(y + 1) * (w + 1) + x + 1] = sum[y * (w + 1) + x + 1] + row;
			}
		}
		bool block = false;
		for (int y = 20; y <= h && !block; y++)
		{
			for (int x = 20; x <= w; x++)
			{
				int s = sum[y * (w + 1) + x] - sum[(y - 20) * (w + 1) + x] - sum[y * (w + 1) + x - 20] + sum[(y - 20) * (w + 1) + x - 20];
				if (s == 400)
				{
					block = true;
					break;
				}
			}
		}
		return (counted == 0 ? 0 : white / (double)counted, block);
	}

	private static void GlareChecks(string outDir, IEnumerable<(string Name, Int32Rect[] Exclude)> renders)
	{
		List<string> bad = new List<string>();
		List<string> got = new List<string>();
		foreach ((string name, Int32Rect[] exclude) in renders)
		{
			string path = Path.Combine(outDir, name);
			if (!File.Exists(path))
			{
				bad.Add(name + " missing");
				continue;
			}
			(double share, bool block) = Glare(path, exclude);
			got.Add($"{name}={share * 100:F3}%{(block ? " BLOCK" : "")}");
			if (share > 0.0005 || block)
			{
				bad.Add($"{name}: white {share * 100:F3}%{(block ? ", a bright block of 20x20 px or more" : "")}");
			}
		}
		Metrics["RestGlare"] = got;
		Check("rest-no-glare", bad.Count == 0, bad.Count == 0 ? string.Join(" ", got) : string.Join(" ; ", bad));
	}

	// ----- Start with the picture, the drawn scene, no flash, the zoom overlay

	private static void RestStartPictureChecks(string outDir, StartScreen start, AppSettings real)
	{
		// the drawn scene: Start shows the very pixels of the desktop file
		{
			string startPath = Path.Combine(outDir, "start-rest.png");
			BitmapSource wp = SeasonalArt.RenderWallpaper(SeasonalArt.RestArt, 1920, 1080);
			byte[] a = Pixels(new CroppedBitmap(LoadPng(startPath), new Int32Rect(1500, 650, 400, 350)));
			byte[] b = Pixels(new CroppedBitmap(wp, new Int32Rect(1500, 650, 400, 350)));
			int worst = 0;
			for (int i = 0; i < a.Length; i += 4)
			{
				for (int c = 0; c < 3; c++)
				{
					worst = Math.Max(worst, Math.Abs(a[i + c] - b[i + c]));
				}
			}
			Check("rest-start-drawn-matches-desktop", worst <= 1, $"largest channel difference={worst}");
		}
		// the picture
		SeasonalArt.DiagnosticsPictureDir = _fixturePicture;
		try
		{
			string pic = Path.Combine(outDir, "start-rest-picture.png");
			start.QaRenderSeason(pic, 1920.0, 1080.0, withAmbient: true);
			bool settled = start.QaBackgroundSettled;
			ImageBrush? ib = start.QaBgLayerBrush as ImageBrush;
			bool brushOk = ib != null && ib.ViewportUnits == BrushMappingMode.Absolute && Math.Abs(ib.Viewport.Width - 1920.0) < 0.01 && Math.Abs(ib.Viewport.Height - 1080.0) < 0.01
				&& ib.TileMode == TileMode.FlipX && ib.ImageSource is BitmapSource bs && bs.PixelWidth == 1920 && bs.PixelHeight == 1080;
			(double r, double g, double b) = RegionAverage(pic, 1690, 790, 20, 20);
			byte[] corner = Pixels(new CroppedBitmap(LoadPng(pic), new Int32Rect(0, 1040, 40, 40)));
			int cornerMax = 0;
			for (int i = 0; i < corner.Length; i += 4)
			{
				cornerMax = Math.Max(cornerMax, Math.Max(corner[i], Math.Max(corner[i + 1], corner[i + 2])));
			}
			bool marker = Math.Abs(r - 0x40) <= 4 && Math.Abs(g - 0x40) <= 4 && Math.Abs(b - 0x40) <= 4;
			string detail = $"settled={settled} imageBrush={brushOk} marker=rgb({r:F0},{g:F0},{b:F0}) cornerMax={cornerMax}";
			Check("rest-start-picture", settled && brushOk && (!_syntheticPicture || (marker && cornerMax <= 6)), _syntheticPicture ? detail : detail + " (local picture: marker not checked)");
			// the header and the tiles stay legible over the picture
			double header = ContrastIn(pic, new Int32Rect(80, 50, 90, 45), new Int32Rect(220, 30, 200, 60));
			double user = ContrastIn(pic, new Int32Rect(1612, 58, 80, 24), new Int32Rect(1380, 30, 180, 60));
			Metrics["RestPictureHeaderContrast"] = $"{header:F2} {user:F2}";
			_pictureHeaderContrast = (header, user);
			// the Dark rest preview of the picker shows the picture
			Brush preview = SeasonalArt.PreviewBrush(SeasonalArt.RestArt);
			bool previewOk = false;
			string previewDetail = "not an image";
			if (preview is ImageBrush pb && pb.ImageSource is BitmapSource src)
			{
				double sx = src.PixelWidth / 2000.0;
				int mx = (int)Math.Round(1771 * sx);
				int my = (int)Math.Round(833 * sx);
				// the marker's centre (the preview averages over 40 screen pixels, so its rim blends into the black)
				byte[] mp = Pixels(new CroppedBitmap(src, new Int32Rect(Math.Clamp(mx, 0, src.PixelWidth - 1), Math.Clamp(my, 0, src.PixelHeight - 1), 1, 1)));
				(double pr, double pg, double pbv) = Average(mp);
				// the preview is the screen-sized picture averaged down to 240 x 135 (no plaid from the fine grid)
				previewOk = src.PixelWidth == 240 && src.PixelHeight == 135 && (!_syntheticPicture || (Math.Abs(pr - 0x40) <= 8 && Math.Abs(pg - 0x40) <= 8 && Math.Abs(pbv - 0x40) <= 8));
				previewDetail = $"{src.PixelWidth}x{src.PixelHeight} marker=rgb({pr:F0},{pg:F0},{pbv:F0})";
			}
			Check("rest-preview-picture", previewOk, previewDetail);
		}
		finally
		{
			SeasonalArt.DiagnosticsPictureDir = _fixtureEmpty;
		}
		// no flash: from the Normal background straight to black, then to the picture
		{
			SettingsStore.DiagnosticsOverride = NormalSettings(real);
			ShellTheme.SyncRest();
			start.QaRenderSeason(Path.Combine(outDir, "start-noflash-normal.png"), 1920.0, 1080.0, withAmbient: false);
			Brush normalBrush = start.QaBgLayerBrush;
			SettingsStore.DiagnosticsOverride = RestSettings(real);
			ShellTheme.SyncRest();
			SeasonalArt.DiagnosticsPictureDir = _fixturePicture;
			StartScreen.QaClearBackgroundCache();
			start.QaRefreshBackgroundNoWait();
			Brush first = start.QaBgLayerBrush;
			bool black = ReferenceEquals(first, StartScreen.QaRestCanvasBrush);
			start.QaWaitBackground();
			Brush landed = start.QaBgLayerBrush;
			bool picture = landed is ImageBrush && start.QaBackgroundSettled;
			Check("rest-start-no-flash", black && picture && !ReferenceEquals(first, normalBrush), $"first={(black ? "black" : first?.GetType().Name)} then={landed?.GetType().Name} settled={start.QaBackgroundSettled}");
			SeasonalArt.DiagnosticsPictureDir = _fixtureEmpty;
			try
			{
				File.Delete(Path.Combine(outDir, "start-noflash-normal.png"));
			}
			catch
			{
			}
		}
		// the semantic zoom overlay lines up with the board (the drawn grid continues across its top edge)
		{
			string zoom = Path.Combine(outDir, "start-rest-zoom.png");
			double top = start.QaRenderZoom(zoom, 1920.0, 1080.0);
			string detail = "the zoom overview could not be built";
			bool ok = false;
			if (top > 20)
			{
				BitmapSource bmp = LoadPng(zoom);
				byte[] p = Pixels(bmp);
				int t = (int)Math.Round(top);
				int Phase(int y0, int y1)
				{
					double[] byPhase = new double[10];
					for (int y = y0; y < y1; y++)
					{
						double s = 0;
						for (int x = 1500; x < 1900; x++)
						{
							int i = (y * 1920 + x) * 4;
							s += 0.0722 * p[i] + 0.7152 * p[i + 1] + 0.2126 * p[i + 2];
						}
						byPhase[y % 10] += s;
					}
					int best = 0;
					for (int k = 1; k < 10; k++)
					{
						if (byPhase[k] > byPhase[best])
						{
							best = k;
						}
					}
					return best;
				}
				int above = Phase(t - 30, t);
				int below = Phase(t, t + 30);
				int d = Math.Abs(above - below);
				d = Math.Min(d, 10 - d);
				ok = d <= 1 && start.QaZoomOverlayBrush is ImageBrush;
				detail = $"overlayTop={top:F1} line phase above={above} below={below} overlay={start.QaZoomOverlayBrush?.GetType().Name}";
			}
			Check("rest-start-zoom", ok, detail);
			// the zoomed-out overview over the picture too (the readable group names are checked with the others)
			if (FixturePicture() != null)
			{
				SeasonalArt.DiagnosticsPictureDir = _fixturePicture;
				try
				{
					start.RefreshBackground();
					start.QaWaitBackground();
					start.QaRenderZoom(Path.Combine(outDir, "start-rest-zoom-picture.png"), 1920.0, 1080.0);
				}
				finally
				{
					SeasonalArt.DiagnosticsPictureDir = _fixtureEmpty;
					start.RefreshBackground();
					start.QaWaitBackground();
				}
			}
		}
		RestStartRobustChecks(outDir, start, real);
	}

	private static (double Header, double User) _pictureHeaderContrast;

	private static ActionCenterQaMetrics? _restActionCenter;

	private static Brush? _restSwitcherCaret;

	// The states that must stay visible on black: focus, selection, the keyboard menu row, switches, levels, hover,
	// focus rings and carets.
	private static void RestStateChecks(string outDir, StartScreen start, RestFixtures f)
	{
		List<string> bad = new List<string>();
		Color Tok(string key)
		{
			return (DarkRestPalette.Token(key) as SolidColorBrush)?.Color ?? Colors.Magenta;
		}
		Color Of(object? b)
		{
			return (b as SolidColorBrush)?.Color ?? Colors.Magenta;
		}
		void Need(string what, bool ok, string detail)
		{
			if (!ok)
			{
				bad.Add(what + ": " + detail);
			}
		}
		// focus border: 3:1 on the field and brighter than the resting border
		Color fieldC = Tok("Metro81.Surface");
		Need("focus border", Contrast(Tok("Metro81.FocusBorder"), fieldC) >= 3.0 && ColorMath.RelLuminance(Tok("Metro81.FocusBorder")) > ColorMath.RelLuminance(Tok("Metro81.FieldBorder")),
			$"{Hex(Tok("Metro81.FocusBorder"))} vs field {Hex(fieldC)} and border {Hex(Tok("Metro81.FieldBorder"))}");
		Need("app-scope focus border", Of(Application.Current.Resources["Metro81.FocusBorder"]) == Tok("Metro81.FocusBorder"), Hex(Of(Application.Current.Resources["Metro81.FocusBorder"])));
		// the selected row and its white text
		Color sel = Of(Application.Current.Resources["MenuAccent"]);
		Need("selected row", Contrast(sel, DarkRestPalette.CanvasColor) >= 3.0 && Contrast(Colors.White, sel) >= 4.5, Hex(sel));
		// the keyboard-highlighted menu row
		Color kb = TaskbarContextMenu.QaMenuColor("M.KbBg");
		Need("keyboard menu row", Contrast(kb, TaskbarContextMenu.QaMenuColor("M.Bg")) >= 3.0 && Contrast(TaskbarContextMenu.QaMenuColor("M.KbFg"), kb) >= 4.5 && Contrast(TaskbarContextMenu.QaMenuColor("M.KbGesture"), kb) >= 4.5, Hex(kb));
		// Action Center switches: Off is a dark field with an outline and no check, On has the check; both labels 4.5:1
		if (_restActionCenter == null || _restActionCenter.Tiles.Count == 0)
		{
			bad.Add("Action Center tiles: not rendered");
		}
		else
		{
			List<ActionCenterQaTile> toggles = _restActionCenter.Tiles.Where(t => t.Toggle).ToList();
			foreach (ActionCenterQaTile t in toggles)
			{
				// the status line never brighter than the title, and an Off glyph quieter than an On one
				if (t.On)
				{
					Need("tile " + t.Id + " on", t.Check && t.Border.A == 0 && Contrast(t.Label, t.Fill) >= 4.5 && Contrast(t.Status, t.Fill) >= 4.5
						&& ColorMath.RelLuminance(t.Status) <= ColorMath.RelLuminance(t.Label) + 1e-9 && t.IconOpacity >= 0.99,
						$"check={t.Check} border={t.Border} label {Hex(t.Label)} status {Hex(t.Status)} on {Hex(t.Fill)} glyph={t.IconOpacity:F2}");
				}
				else
				{
					Need("tile " + t.Id + " off", !t.Check && t.Fill == DarkRestPalette.FieldColor && t.Border == DarkRestPalette.BoundaryColor && Math.Abs(t.BorderThickness - 1.0) < 0.01
						&& Contrast(t.Label, t.Fill) >= 4.5 && Contrast(t.Status, t.Fill) >= 4.5 && ColorMath.RelLuminance(t.Status) <= ColorMath.RelLuminance(t.Label) + 1e-9
						&& t.IconOpacity < 0.99 && Contrast(t.Border, DarkRestPalette.PanelColor) >= 3.0,
						$"check={t.Check} fill={Hex(t.Fill)} border={Hex(t.Border)} label={Hex(t.Label)} status={Hex(t.Status)} glyph={t.IconOpacity:F2}");
				}
			}
			Need("Action Center has switches on and off", toggles.Any(t => t.On) && toggles.Any(t => !t.On), string.Join(",", toggles.Select(t => t.Id + "=" + t.On)));
			foreach (ActionCenterQaTile t in _restActionCenter.Tiles.Where(t => !t.Toggle))
			{
				Need("tile " + t.Id, Contrast(t.Label, t.Fill) >= 4.5, $"label {Hex(t.Label)} on {Hex(t.Fill)}");
			}
		}
		// OSD level, muted too: fill, muted fill and the hollow empty outline each 3:1 against the card
		Color card = Win7Palette.Over(Of(DarkRestPalette.OsdCard), DarkRestPalette.CanvasColor);
		Need("OSD level", Contrast(Of(DarkRestPalette.OsdFill), card) >= 3.0 && Contrast(Of(DarkRestPalette.OsdMuted), card) >= 3.0 && Contrast(DarkRestPalette.BoundaryColor, card) >= 3.0, "fill/muted/outline vs card");
		// Start: the hover wash is visible, keyboard focus draws the accent ring, "new" is legible
		Color hover = Win7Palette.Over(Of(start.TryFindResource("HoverTint")), DarkRestPalette.CanvasColor);
		Need("all apps hover", hover != DarkRestPalette.CanvasColor && Contrast(hover, DarkRestPalette.CanvasColor) >= 1.3, Hex(hover));
		Need("all apps focus ring", ReferenceEquals(start.TryFindResource("Start.FocusRing"), DarkRestPalette.AccentLine), "Start.FocusRing");
		Need("all apps new label", ReferenceEquals(start.TryFindResource("Start.NewInk"), DarkRestPalette.AccentInk), "Start.NewInk");
		// carets
		Color ink = DarkRestPalette.InkColor;
		ResourceDictionary w7 = Win7Palette.Build(true, DarkRestPalette.AccentColor, true, rest: true);
		(string Name, Color C)[] carets = new (string, Color)[5]
		{
			("Start search", Of(start.TryFindResource("Start.SearchInk"))),
			("Search pane", Of(f.Search.QaCaretBrush)),
			("switcher", Of(_restSwitcherCaret)),
			("Metro text box", Of(Application.Current.Resources["Metro81.Ink"])),
			("Windows 7 search", Of(w7["W7.SearchCaret"]))
		};
		foreach ((string name, Color c) in carets)
		{
			Need("caret " + name, c == ink, Hex(c));
		}
		// the unavailable Win8.1 wallpaper row of PC Settings (Dark rest owns the wallpaper): not faded, its title in the
		// hint ink and the sentence that says what to do in the secondary ink, both 4.5:1 on the black page
		if (f.Pc.QaWallpaperRow is (double rowOpacity, Color rowTitle, Color rowDesc))
		{
			Need("unavailable wallpaper row", rowOpacity >= 0.99 && Contrast(rowTitle, DarkRestPalette.CanvasColor) >= 4.5 && Contrast(rowDesc, DarkRestPalette.CanvasColor) >= 4.5,
				$"opacity={rowOpacity:F2} title={Hex(rowTitle)} sentence={Hex(rowDesc)}");
		}
		else
		{
			bad.Add("unavailable wallpaper row: not shown");
		}
		Check("rest-states", bad.Count == 0, bad.Count == 0 ? "focus, selection, keyboard row, switches, levels, hover, rings, carets" : string.Join(" ; ", bad));
	}

	// The renders checked for glare (with the picture areas they contain left out).
	private static IEnumerable<(string Name, Int32Rect[] Exclude)> RestGlareRenders()
	{
		Int32Rect[] none = Array.Empty<Int32Rect>();
		yield return ("settingspane-rest.png", none);
		// the text column only (the result icons are left out)
		yield return ("searchpane-rest.png", new Int32Rect[1] { new Int32Rect(0, 210, 56, 600) });
		yield return ("charms-rest.png", none);
		yield return ("osd-rest.png", none);
		yield return ("flyout-clock-rest.png", none);
		// the app cards' logos left out (a lifted dark logo is light by design)
		yield return ("flyout-volume-rest.png", new Int32Rect[1] { _restVolumeLogos });
		yield return ("flyout-network-rest.png", none);
		// the hero's mini-tile glyph on Home of the PC settings hub (left out like the preview tiles)
		yield return ("pcsettings-rest-devices.png", new Int32Rect[1] { new Int32Rect(488, 60, 40, 40) });
		yield return ("actioncenter-rest.png", none);
		yield return ("contextmenu-rest.png", none);
		yield return ("caption-rest.png", none);
		yield return ("controls-rest.png", none);
		yield return ("tooltip-rest.png", none);
		// the surfaces that are on screen all the time: the taskbar strip (its pinned and running app icons left out),
		// the switcher's search box and Start bar (its window list and thumbnails left out), the Windows 7 Start menu
		// (its app icons and the account picture left out), Start's header (the account picture left out) and the
		// charms clock
		yield return ("taskbar-rest.png", new Int32Rect[2] { new Int32Rect(0, 0, 1366, 720), new Int32Rect(48, 720, 1050, 48) });
		yield return ("switcher-rest.png", new Int32Rect[1] { new Int32Rect(0, 40, 200, 812) });
		yield return ("win7start-rest.png", new Int32Rect[2] { new Int32Rect(10, 25, 42, 300), new Int32Rect(290, 0, 80, 70) });
		yield return ("start-rest.png", new Int32Rect[2] { new Int32Rect(0, 120, 1920, 960), new Int32Rect(1695, 45, 50, 50) });
		yield return ("charmsclock-rest.png", none);
		// the surfaces added with the review of what stayed hidden or bright: the Share and Devices panes, a result card,
		// the scope drop-down, the power flyout, the menu toggles, the drop-down list, the tile app bar (the board above
		// it left out)
		yield return ("share-rest.png", none);
		yield return ("devices-rest.png", none);
		yield return ("search-rest-calc.png", new Int32Rect[1] { new Int32Rect(0, 210, 56, 700) });
		yield return ("searchscope-rest.png", none);
		yield return ("settings-power-rest.png", none);
		yield return ("contextmenu-toggles-rest.png", none);
		yield return ("combo-rest.png", none);
		yield return ("start-rest-appbar.png", new Int32Rect[2] { new Int32Rect(0, 120, 1920, 868), new Int32Rect(1695, 45, 50, 50) });
		yield return ("switcher-rest-selected.png", new Int32Rect[1] { new Int32Rect(0, 40, 200, 812) });
	}

	// Text and glyphs as rendered, against what they sit on.
	private static void RestRenderedContrastChecks(string outDir)
	{
		List<string> bad = new List<string>();
		List<string> got = new List<string>();
		void Spot(string name, string file, Int32Rect ink, Int32Rect bg, double min)
		{
			string path = Path.Combine(outDir, file);
			if (!File.Exists(path))
			{
				bad.Add(name + ": " + file + " missing");
				return;
			}
			double c = ContrastIn(path, ink, bg);
			got.Add($"{name}={c:F2}");
			if (c < min)
			{
				bad.Add($"{name} {c:F2} < {min}");
			}
		}
		// Start header and user name over the drawn scene and over the picture
		Spot("start title", "start-rest.png", new Int32Rect(80, 50, 90, 45), new Int32Rect(220, 30, 200, 60), 7.0);
		Spot("start user", "start-rest.png", new Int32Rect(1612, 58, 80, 24), new Int32Rect(1380, 30, 180, 60), 7.0);
		Spot("start group header", "start-rest.png", new Int32Rect(80, 124, 100, 22), new Int32Rect(300, 122, 300, 24), 4.5);
		got.Add($"start title over picture={_pictureHeaderContrast.Header:F2} user={_pictureHeaderContrast.User:F2}");
		if (_pictureHeaderContrast.Header < 7.0 || _pictureHeaderContrast.User < 7.0)
		{
			bad.Add($"start header over the picture {_pictureHeaderContrast.Header:F2}/{_pictureHeaderContrast.User:F2} < 7");
		}
		Spot("settings title", "settingspane-rest.png", new Int32Rect(24, 40, 110, 36), new Int32Rect(180, 40, 140, 40), 4.5);
		Spot("settings item", "settingspane-rest.png", new Int32Rect(24, 156, 96, 20), new Int32Rect(180, 150, 140, 30), 4.5);
		Spot("search title", "searchpane-rest.png", new Int32Rect(20, 30, 100, 36), new Int32Rect(160, 30, 140, 40), 4.5);
		Spot("search query in the field", "searchpane-rest.png", new Int32Rect(26, 108, 120, 20), new Int32Rect(160, 104, 60, 20), 4.5);
		Spot("search hint line", "searchpane-rest.png", new Int32Rect(20, 150, 250, 14), new Int32Rect(20, 166, 280, 10), 4.5);
		Spot("search row", "searchpane-rest.png", new Int32Rect(62, 266, 140, 18), new Int32Rect(220, 262, 70, 24), 4.5);
		// the Action Center (its rects come from the render's own metrics): the footer link's text against the footer band
		// right of it, and a switch tile's label (Off and On) against an empty part of that tile (between icon and check)
		if (_restActionCenter == null)
		{
			bad.Add("action center: not rendered");
		}
		else
		{
			Rect link = _restActionCenter.FooterLinkRect;
			Spot("action center footer link", "actioncenter-rest.png", R32(link.X + 35.0, link.Y + 12.0, link.Width - 43.0, 16.0), R32(link.Right + 12.0, link.Y + 10.0, 28.0, 20.0), 4.5);
			ActionCenterQaTile? off = _restActionCenter.Tiles.FirstOrDefault(t => t.Toggle && !t.On);
			ActionCenterQaTile? on = _restActionCenter.Tiles.FirstOrDefault(t => t.Toggle && t.On);
			foreach ((string what, ActionCenterQaTile? t) in new[] { ("off", off), ("on", on) })
			{
				if (t == null || t.LabelRect.IsEmpty)
				{
					bad.Add("action center tile " + what + ": none rendered");
					continue;
				}
				Spot("action center tile " + what, "actioncenter-rest.png", R32(t.LabelRect.X, t.LabelRect.Y, t.LabelRect.Width, t.LabelRect.Height), R32(t.TileRect.X + 32.0, t.TileRect.Y + 8.0, 16.0, 14.0), 4.5);
			}
		}
		// the compact clock: the time at x = 12 on the 40 DIP line under the 12 DIP top padding, the empty header right of it
		Spot("clock digits", "flyout-clock-rest.png", new Int32Rect(12, 13, 120, 40), new Int32Rect(240, 14, 100, 30), 4.5);
		// the QA menu at (20, 20): 1 px border, 3 px padding, 32 px rows (row tops 24, 56, 88; separator 120-127; the keyboard
		// row 127-159), text from x = 50
		Spot("menu item", "contextmenu-rest.png", new Int32Rect(50, 30, 120, 20), new Int32Rect(170, 28, 40, 24), 4.5);
		Spot("menu disabled item", "contextmenu-rest.png", new Int32Rect(50, 94, 130, 20), new Int32Rect(185, 92, 25, 24), 4.5);
		Spot("menu keyboard row", "contextmenu-rest.png", new Int32Rect(50, 131, 100, 24), new Int32Rect(28, 132, 16, 22), 4.5);
		Spot("caption title active", "caption-rest.png", new Int32Rect(40, 8, 100, 16), new Int32Rect(200, 4, 200, 24), 4.5);
		Spot("caption title inactive", "caption-rest.png", new Int32Rect(40, 46, 100, 16), new Int32Rect(200, 42, 200, 24), 4.5);
		Spot("tooltip text", "tooltip-rest.png", new Int32Rect(28, 26, 200, 18), new Int32Rect(30, 20, 4, 4), 4.5);
		// the OSD card is 376 DIP wide now (12x20 segments): its value sits at the right, before the 14 px padding
		Spot("osd value", "osd-rest.png", new Int32Rect(326, 10, 40, 28), new Int32Rect(4, 4, 10, 38), 4.5);
		// the review follow-ups: the selected calendar day, the zoomed-out group name,
		// the hollow OSD segments, the outline of a plain button and of a slider's remainder, and the open category of
		// PC Settings (its fill against the navigation pane and its ink on it)
		// the compact calendar's 12 px number: its own text box against the selection fill around it
		Rect day = _restClockSelectedDay;
		Rect dayText = _restClockSelectedDayText.IsEmpty ? day : _restClockSelectedDayText;
		Spot("clock selected day", "flyout-clock-rest.png", R32(dayText.X, dayText.Y, Math.Max(1.0, dayText.Width), Math.Max(1.0, dayText.Height)), R32(day.X + 3.0, day.Y + 3.0, Math.Max(1.0, day.Width - 6.0), Math.Max(1.0, day.Height - 6.0)), 4.5);
		Spot("zoom group name", "start-rest-zoom.png", new Int32Rect(370, 384, 80, 20), new Int32Rect(470, 384, 80, 20), 4.5);
		Spot("osd empty outline", "osd-rest.png", new Int32Rect(240, 12, 30, 20), new Int32Rect(247, 15, 8, 10), 3.0);
		Spot("button outline", "controls-rest.png", new Int32Rect(240, 559, 70, 6), new Int32Rect(240, 540, 70, 8), 3.0);
		Spot("slider remainder outline", "controls-rest.png", new Int32Rect(300, 304, 140, 12), new Int32Rect(300, 320, 140, 6), 3.0);
		// the hub's sidebar: "Home" selected (28 DIP rows from y 126), its ink against the selected wash right of the text
		Spot("pc nav selected ink", "pcsettings-rest-devices.png", new Int32Rect(36, 132, 40, 16), new Int32Rect(120, 129, 90, 22), 4.5);
		{
			string devices = Path.Combine(outDir, "pcsettings-rest-devices.png");
			if (File.Exists(devices))
			{
				// the selected Home row against the unselected Sound row, right of their labels
				(double sr, double sg, double sb) = RegionAverage(devices, 120, 129, 90, 22);
				(double pr, double pg, double pb) = RegionAverage(devices, 120, 231, 90, 22);
				double navC = Contrast(Color.FromRgb((byte)sr, (byte)sg, (byte)sb), Color.FromRgb((byte)pr, (byte)pg, (byte)pb));
				got.Add($"pc nav selected fill={navC:F2}");
				if (navC < 3.0)
				{
					bad.Add($"pc nav selected fill {navC:F2} < 3");
				}
			}
		}
		Metrics["RestRenderedContrast"] = got;
		Check("rest-contrast-rendered", bad.Count == 0, bad.Count == 0 ? string.Join(" ", got) : string.Join(" ; ", bad) + " | " + string.Join(" ", got));
	}

	// ----- small renders: the code-built menu, the Metro controls, the caption, the tooltip, the pointers

	// A keyboard-highlighted menu row (IsHighlighted is only settable by a derived item).
	private sealed class QaHighlightedItem : MenuItem
	{
		public QaHighlightedItem()
		{
			IsHighlighted = true;
		}
	}

	// The QA menu: a plain row, a submenu header (chevron), a disabled row, a separator, the keyboard row with a shortcut
	// and a plain last row.
	private static ContextMenu BuildQaContextMenu()
	{
		ContextMenu ctx = new ContextMenu { Style = (Style)Application.Current.Resources["Win81ContextMenu"] };
		TaskbarContextMenu.ApplyTheme(ctx);
		ctx.Items.Add(TaskbarContextMenu.Leaf("Toolbars", delegate { }));
		ctx.Items.Add(TaskbarContextMenu.Sub("Cascade windows", TaskbarContextMenu.Leaf("Side by side", delegate { })));
		ctx.Items.Add(TaskbarContextMenu.Disabled("Undo minimize all"));
		ctx.Items.Add(TaskbarContextMenu.Sep());
		ctx.Items.Add(new QaHighlightedItem { Header = "Task Manager", InputGestureText = "Ctrl+Shift+Esc", Style = TaskbarContextMenu.ItemStyle });
		ctx.Items.Add(TaskbarContextMenu.Leaf("Properties", delegate { }));
		return ctx;
	}

	private static void RenderContextMenu(string path)
	{
		ContextMenu ctx = BuildQaContextMenu();
		TaskbarContextMenu.PrepareForInstantOpen(ctx);
		double w = Math.Ceiling(ctx.ActualWidth);
		double h = Math.Ceiling(ctx.ActualHeight);
		// the mouse-hover row, drawn with the menu's own hover colours below the menu: a one-row menu (border of
		// M.BorderThickness, 3 px padding) whose 30 px row (inside the 1 px item border) shows the hover fill and 14 px text
		DrawingVisual dv = new DrawingVisual();
		RenderTargetBitmap menu = new RenderTargetBitmap((int)Math.Max(1, w), (int)Math.Max(1, h), 96, 96, PixelFormats.Pbgra32);
		menu.Render(ctx);
		Color bg = TaskbarContextMenu.QaMenuColor("M.Bg");
		double b = TaskbarContextMenu.BorderThicknessNow();
		using (DrawingContext dc = dv.RenderOpen())
		{
			dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(0x30, 0x30, 0x30)), null, new Rect(0, 0, w + 40, h + 90));
			dc.DrawImage(menu, new Rect(20, 20, w, h));
			double top = h + 30;
			double outerH = b + 3 + 32 + 3 + b;
			dc.DrawRectangle(new SolidColorBrush(TaskbarContextMenu.QaMenuColor("M.Border")), null, new Rect(20, top, w, outerH));
			dc.DrawRectangle(new SolidColorBrush(Win7Palette.Over(bg, Color.FromRgb(0x30, 0x30, 0x30))), null, new Rect(20 + b, top + b, w - 2 * b, outerH - 2 * b));
			dc.DrawRectangle(new SolidColorBrush(TaskbarContextMenu.QaMenuColor("M.Hover")), null, new Rect(20 + b + 1, top + b + 3 + 1, w - 2 * b - 2, 30));
			FormattedText ft = new FormattedText("Lock the taskbar (hover)", CultureInfo.InvariantCulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), 14, new SolidColorBrush(TaskbarContextMenu.QaMenuColor("M.Fg")), 1.0);
			dc.DrawText(ft, new Point(20 + b + 1 + 24 + 4, top + b + 3 + 1 + (30 - ft.Height) / 2));
		}
		SavePng(Render(dv, (int)w + 40, (int)h + 90), path);
	}

	// The QA menu laid out at another DPI (VisualTreeHelper.SetRootDpi, so layout rounding and the 1 DIP borders snap to
	// that device grid) and rendered at that DPI: rows must be exactly 40 px at 120 dpi and 48 px at 144 dpi. Returns the
	// row heights in device pixels.
	private static List<double> RenderContextMenuDpi(string path, double dpi)
	{
		double scale = dpi / 96.0;
		ContextMenu ctx = BuildQaContextMenu();
		VisualTreeHelper.SetRootDpi(ctx, new DpiScale(scale, scale));
		TaskbarContextMenu.PrepareForInstantOpen(ctx);
		int pw = (int)Math.Ceiling(ctx.ActualWidth * scale);
		int ph = (int)Math.Ceiling(ctx.ActualHeight * scale);
		RenderTargetBitmap bmp = new RenderTargetBitmap(Math.Max(1, pw), Math.Max(1, ph), dpi, dpi, PixelFormats.Pbgra32);
		bmp.Render(ctx);
		SavePng(bmp, path);
		List<double> rows = new List<double>();
		foreach (object o in ctx.Items)
		{
			if (o is MenuItem mi)
			{
				rows.Add(Math.Round(mi.ActualHeight * scale, 3));
			}
		}
		return rows;
	}

	// 120 and 144 dpi variants of the QA menu in the current look ("contextmenu-<tag>-120.png" / "-144.png").
	private static void RenderContextMenuDpiSet(string outDir, string tag, List<string> bad, List<string> got)
	{
		foreach (double dpi in new double[2] { 120.0, 144.0 })
		{
			List<double> rows = RenderContextMenuDpi(Path.Combine(outDir, $"contextmenu-{tag}-{(int)dpi}.png"), dpi);
			double want = 32.0 * dpi / 96.0;
			got.Add($"{tag}@{(int)dpi}=[{string.Join(",", rows)}]");
			if (rows.Count == 0 || rows.Any(r => Math.Abs(r - want) > 0.01))
			{
				bad.Add($"{tag}@{(int)dpi} rows {string.Join(",", rows)} != {want}");
			}
		}
	}

	private static void RenderControls(string path, Brush page)
	{
		StackPanel col = new StackPanel { Margin = new Thickness(24), Width = 420 };
		Style S(string key)
		{
			return (Style)Application.Current.FindResource(key);
		}
		TextBlock Label(string text)
		{
			TextBlock t = new TextBlock { Text = text, Margin = new Thickness(0, 10, 0, 2), FontFamily = new FontFamily("Segoe UI"), FontSize = 13 };
			t.SetResourceReference(TextBlock.ForegroundProperty, "Metro81.Ink2");
			return t;
		}
		col.Children.Add(Label("Text box: empty, typed and focused, disabled"));
		col.Children.Add(new TextBox { Style = S("Metro81.TextBox"), Margin = new Thickness(0, 2, 0, 4) });
		TextBox focused = new TextBox { Style = S("Metro81.TextBox"), Text = "Dark rest", Margin = new Thickness(0, 2, 0, 4) };
		focused.SetResourceReference(Control.BorderBrushProperty, "Metro81.FocusBorder");
		col.Children.Add(focused);
		col.Children.Add(new TextBox { Style = S("Metro81.TextBox"), Text = "Not available", IsEnabled = false, Margin = new Thickness(0, 2, 0, 4) });
		col.Children.Add(Label("Toggle: on, off"));
		StackPanel toggles = new StackPanel { Orientation = Orientation.Horizontal };
		ToggleButton on = new ToggleButton { IsChecked = true, Content = "On" };
		on.SetResourceReference(FrameworkElement.StyleProperty, "Metro81.ToggleSwitch");
		ToggleButton off = new ToggleButton { IsChecked = false, Content = "Off" };
		off.SetResourceReference(FrameworkElement.StyleProperty, "Metro81.ToggleSwitch");
		toggles.Children.Add(on);
		toggles.Children.Add(off);
		col.Children.Add(toggles);
		col.Children.Add(Label("Slider, dark slider (on a pane), progress"));
		col.Children.Add(new Slider { Style = S("Metro81.Slider"), Minimum = 0, Maximum = 100, Value = 60 });
		Border paneStrip = new Border { Padding = new Thickness(8, 0, 8, 0) };
		paneStrip.Background = DarkRest.Visuals ? DarkRestPalette.Panel : new SolidColorBrush(Color.FromRgb(0x1C, 0x1C, 0x1C));
		paneStrip.Child = new Slider { Style = S("Metro81.SliderDark"), Minimum = 0, Maximum = 100, Value = 60 };
		col.Children.Add(paneStrip);
		col.Children.Add(new ProgressBar { Style = S("Metro81.ProgressBar"), Value = 40, Margin = new Thickness(0, 8, 0, 0) });
		col.Children.Add(Label("Check boxes, combo box, link, buttons"));
		col.Children.Add(new CheckBox { Style = S("Metro81.CheckBox"), Content = "Checked", IsChecked = true });
		col.Children.Add(new CheckBox { Style = S("Metro81.CheckBox"), Content = "Not checked", IsChecked = false });
		ComboBox combo = new ComboBox { Style = S("Metro81.ComboBox"), Width = 240, HorizontalAlignment = HorizontalAlignment.Left };
		combo.Items.Add(new ComboBoxItem { Content = "Dark rest", Style = S("Metro81.ComboBoxItem") });
		combo.SelectedIndex = 0;
		col.Children.Add(combo);
		col.Children.Add(new Button { Style = S("Metro81.LinkButton"), Content = "A link to more settings" });
		StackPanel buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };
		buttons.Children.Add(new Button { Style = S("Metro81.AccentButton"), Content = "Apply", Margin = new Thickness(0, 0, 8, 0) });
		Button hot = new Button { Style = S("Metro81.AccentButton"), Content = "Apply (hover)", Margin = new Thickness(0, 0, 8, 0) };
		hot.SetResourceReference(Control.BackgroundProperty, "MenuAccentPressed");
		buttons.Children.Add(hot);
		buttons.Children.Add(new Button { Style = S("Metro81.Button"), Content = "Cancel" });
		col.Children.Add(buttons);
		col.Children.Add(Label("List with a selected row, scrollbar"));
		Grid listRow = new Grid { Height = 120 };
		listRow.ColumnDefinitions.Add(new ColumnDefinition());
		listRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
		ListBox list = new ListBox { Style = S("Metro81.ListBox") };
		foreach (string item in new string[3] { "Normal", "Dark rest", "Christmas" })
		{
			list.Items.Add(new ListBoxItem { Content = item, Style = S("Metro81.ListBoxItem") });
		}
		list.SelectedIndex = 1;
		listRow.Children.Add(list);
		ScrollBar bar = new ScrollBar { Style = S("Metro81.ScrollBar"), Orientation = Orientation.Vertical, Minimum = 0, Maximum = 100, ViewportSize = 30, Value = 30, Margin = new Thickness(8, 0, 0, 0) };
		Grid.SetColumn(bar, 1);
		listRow.Children.Add(bar);
		col.Children.Add(listRow);
		Border host = new Border { Background = page, Child = col, Width = 470 };
		host.Measure(new Size(470, double.PositiveInfinity));
		double h = Math.Ceiling(host.DesiredSize.Height);
		host.Arrange(new Rect(0, 0, 470, h));
		host.UpdateLayout();
		RenderTargetBitmap rtb = new RenderTargetBitmap(470, (int)h, 96, 96, PixelFormats.Pbgra32);
		rtb.Render(host);
		SavePng(rtb, path);
	}

	private static void RenderCaption(string path)
	{
		Win81Window w = new Win81Window { Left = -4000.0, Top = -4000.0, ShowActivated = false, Title = "Dark rest caption", Width = 640, Height = 240 };
		try
		{
			w.RefreshAccent();
			w.QaRenderCaption(path, 640.0);
		}
		finally
		{
			w.Close();
		}
	}

	private static void RenderTooltip(string path)
	{
		// A tooltip cannot have a parent: it is rendered on its own and drawn onto the black desktop.
		ToolTip tip = new ToolTip { Content = "Dark rest: a calm, fully dark shell" };
		tip.Style = (Style)Application.Current.FindResource("Metro81.ToolTip");
		tip.Measure(new Size(420, double.PositiveInfinity));
		tip.Arrange(new Rect(tip.DesiredSize));
		tip.UpdateLayout();
		int w = Math.Max(1, (int)Math.Ceiling(tip.ActualWidth));
		int h = Math.Max(1, (int)Math.Ceiling(tip.ActualHeight));
		RenderTargetBitmap rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
		rtb.Render(tip);
		DrawingVisual dv = new DrawingVisual();
		using (DrawingContext dc = dv.RenderOpen())
		{
			dc.DrawRectangle(DarkRestPalette.Canvas, null, new Rect(0, 0, w + 32, h + 32));
			dc.DrawImage(rtb, new Rect(16, 16, w, h));
		}
		SavePng(Render(dv, w + 32, h + 32), path);
	}

	// The Dark rest pointers (arrow and busy) at 32 px over black and over the picture's brightest area; the ring keeps
	// 3:1 against both.
	private static void PointerChecks(string outDir)
	{
		AlchemyPalette p = AlchemyPalettes.Find("rest-dark")!;
		Color[] backs = new Color[2] { Colors.Black, MarkerColor };
		List<string> got = new List<string>();
		bool ok = p.Accent == 0x8FA3B8u;
		DrawingVisual dv = new DrawingVisual();
		using (DrawingContext dc = dv.RenderOpen())
		{
			int col = 0;
			foreach (CursorRole role in new CursorRole[2] { CursorRole.Arrow, CursorRole.Wait })
			{
				byte[] px = AlchemyCursorArt.RenderBgra(role, p, 32);
				foreach (Color back in backs)
				{
					// composite (straight alpha) and measure the ring: the lightest quarter of the opaque pixels
					byte[] comp = new byte[px.Length];
					List<double> ring = new List<double>();
					for (int i = 0; i < px.Length; i += 4)
					{
						double a = px[i + 3] / 255.0;
						comp[i] = (byte)Math.Round(px[i] * a + back.B * (1 - a));
						comp[i + 1] = (byte)Math.Round(px[i + 1] * a + back.G * (1 - a));
						comp[i + 2] = (byte)Math.Round(px[i + 2] * a + back.R * (1 - a));
						comp[i + 3] = 255;
						if (px[i + 3] >= 200)
						{
							ring.Add(ColorMath.RelLuminance(Color.FromRgb(comp[i + 2], comp[i + 1], comp[i])));
						}
					}
					ring.Sort();
					double lum = ring.Count == 0 ? 0 : ring[(int)(ring.Count * 0.75)];
					double lb = ColorMath.RelLuminance(back);
					double c = (Math.Max(lum, lb) + 0.05) / (Math.Min(lum, lb) + 0.05);
					got.Add($"{role}/{Hex(back)}={c:F2}");
					ok &= c >= 3.0;
					BitmapSource bmp = BitmapSource.Create(32, 32, 96, 96, PixelFormats.Bgra32, null, comp, 128);
					dc.DrawImage(bmp, new Rect(col * 80 + 8, 8, 64, 64));
					col++;
				}
			}
		}
		SavePng(Render(dv, 4 * 80, 80), Path.Combine(outDir, "pointer-rest.png"));
		Check("rest-pointer-visible", ok, string.Join(" ", got) + $" accent=#{p.Accent:X6}");
	}
}
