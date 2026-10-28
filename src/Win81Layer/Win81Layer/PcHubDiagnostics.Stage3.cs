using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;

#nullable enable annotations

namespace Win81Layer;

// Stage 3 of --pchubtest: Fix a problem, Favourites, Modified dots and "Changed only", Setups, the fix intents in search,
// the PC settings rows the Windows 7 Start menu ranks, Lighter rendering, the search index's release and lazy hash, and
// the dead code that left. All against the fake world; launcher writes go to the diagnostics override only.
internal static partial class PcHubDiagnostics
{
	private static bool _stage3 = true;

	private static void Stage3Checks()
	{
		FakeSettingsWorld stage1 = _world;
		_s2 = new FakeSettingsWorld("this-pc");
		_world = _s2;
		SettingsWorld.Current = _s2;
		SettingsJournal.QaReset();
		AppSettings? over = SettingsStore.DiagnosticsOverride;
		string favorites0 = over?.PcFavorites ?? "";
		string setups0 = over?.PcSetups ?? "";
		bool render0 = over?.ProcessSoftwareRender ?? false;
		if (over != null)
		{
			over.PcFavorites = "";
			over.PcSetups = "";
		}
		try
		{
			FixCards();
			Favourites();
			ModifiedDots();
			Setups();
			FixSearch();
			StartMenuRows();
			LighterRendering();
			IndexLifetime();
			DeadCode();
			ReviewChecks();
		}
		catch (Exception ex)
		{
			Check("stage3", false, ex.ToString());
		}
		finally
		{
			SettingsWorker.Flush(3000);
			SettingsJournal.QaReset();
			if (over != null)
			{
				over.PcFavorites = favorites0;
				over.PcSetups = setups0;
				over.ProcessSoftwareRender = render0;
			}
			_world = stage1;
			SettingsWorld.Current = stage1;
		}
	}

	private static void Click(PcSettingsWindow w, string rowId)
	{
		// the row's own button (a row with long help also has the info button in its title line)
		FrameworkElement? row = w.QaRowElement(rowId);
		ButtonBase? b = row as ButtonBase ?? (row == null ? null : PcSettingsWindow.Descendants<ButtonBase>(row).LastOrDefault((ButtonBase x) => AutomationName(x) != "More information"));
		b?.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
	}

	private static bool Writes(string[] lines) => lines.Any((string l) => l.StartsWith("set ", StringComparison.Ordinal) || l.StartsWith("persist ", StringComparison.Ordinal) || l.StartsWith("broadcast", StringComparison.Ordinal) || l.StartsWith("launch ", StringComparison.Ordinal));

	// Every fix page only reads until a fix is clicked; a click is journaled and one Undo puts it back.
	private static void FixCards()
	{
		_s2.Muted = true;
		_s2.Volume = 3;
		List<string> notes = new List<string>();
		bool quiet = true;
		foreach (HubPage p in HubCatalog.FixPages)
		{
			int mark = Mark();
			PcSettingsWindow pw = Stage2Window(p.Id);
			try
			{
				pw.QaWaitReads();
				string[] lines = Lines(mark);
				bool ok = !Writes(lines);
				quiet &= ok;
				notes.Add(p.Id + (ok ? " reads only" : " WROTE " + string.Join(",", lines.Where((string l) => !l.StartsWith("get", StringComparison.Ordinal)).Take(3))));
			}
			finally
			{
				pw.Close();
			}
		}
		PcSettingsWindow w = Stage2Window("fix/sound");
		try
		{
			w.QaWaitReads();
			bool found = w.QaRowElement("fix.sound.muted") != null && w.QaRowElement("fix.sound.low") != null && w.QaRowElement("fix.sound.apps") != null;
			bool offer = w.QaRowElement("fix.sound.unmute") != null && w.QaRowElement("fix.sound.volume") != null && w.QaRowElement("fix.sound.use:{render-2}") != null;
			w.QaRender(Path.Combine(_out, "s3-fix-sound-found.png"), "fix/sound", 980.0, 760.0);
			w.QaWaitReads();
			int before = SettingsJournal.Count;
			Click(w, "fix.sound.unmute");
			w.QaSettle();
			JournalEntry? e = SettingsJournal.Last();
			bool journaled = SettingsJournal.Count == before + 1 && e?.Id == "sound.mute" && e.Before == "1" && e.After == "0" && !_s2.Muted;
			w.UndoLast();
			w.QaSettle();
			bool undone = _s2.Muted && SettingsJournal.Count == before;
			// the gaming preset: one group (speed 20 -> 10; precision is already off on this PC)
			w.OpenCategory("fix/mouse");
			w.QaWaitReads();
			Click(w, "fix.mouse.gaming");
			w.QaSettle();
			List<JournalEntry> j = SettingsJournal.Snapshot();
			bool gaming = j.Count == 1 && j[0].Id == "mouse.speed" && j[0].Group != null && _s2.Spi[0x70] == 10;
			w.UndoLast();
			w.QaSettle();
			bool gamingBack = _s2.Spi[0x70] == 20 && SettingsJournal.Count == 0;
			Check("fix-cards", quiet && found && offer && journaled && undone && gaming && gamingBack,
				$"{string.Join("; ", notes)}; No sound found muted/volume/app={found} fixes offered={offer}; Unmute journaled={journaled} undone={undone}; gaming preset one group={gaming} undone={gamingBack}");
		}
		finally
		{
			w.Close();
			_s2.Muted = false;
			_s2.Volume = 40;
		}
	}

	// Favourites: the star (Ctrl+D on the focused row) saves the row ids in the settings (the diagnostics override here),
	// Home shows them live with their own controls, and they leave again.
	private static void Favourites()
	{
		AppSettings over = SettingsStore.DiagnosticsOverride!;
		PcSettingsWindow w = Stage2Window("mouse");
		try
		{
			bool none = PcSettingsWindow.FavoriteList().Count == 0;
			w.ToggleFavorite("mouse.speed");
			w.ToggleFavorite("keyboard.delay");
			w.ToggleFavorite("sound.settings");
			// Ctrl+D: the row a focused control belongs to
			Slider? lines = SliderOf(w, "mouse.lines");
			string? viaFocus = lines == null ? null : w.RowIdOf(lines);
			if (viaFocus != null)
			{
				w.ToggleFavorite(viaFocus);
			}
			string savedText = over.PcFavorites;
			bool saved = savedText == "mouse.speed;keyboard.delay;sound.settings;mouse.lines";
			bool starOn = w.QaRowElement("mouse.speed") is FrameworkElement r && PcSettingsWindow.Descendants<Button>(r).Any((Button b) => b.Tag is string id && id == "mouse.speed" && b.Visibility == Visibility.Visible);
			w.QaRender(Path.Combine(_out, "s3-mouse-starred.png"), "mouse", 980.0, 760.0);
			w.QaRender(Path.Combine(_out, "s3-home-favourites.png"), "home", 980.0, 760.0);
			w.QaWaitReads();
			bool live = w.QaBinding("mouse.speed") is PcSettingsWindow.KnobBinding b1 && b1.Page == "home" && b1.Committed == "20"
				&& w.QaBinding("keyboard.delay")?.Committed == "0" && w.QaRowElement("sound.settings") != null;
			// a change from Home is a change of the setting itself
			w.QaChange("mouse.speed", "15");
			w.QaSettle();
			bool fromHome = _s2.Spi[0x70] == 15 && SettingsJournal.Last()?.Id == "mouse.speed";
			w.UndoLast();
			w.QaSettle();
			foreach (string id in PcSettingsWindow.FavoriteList())
			{
				w.ToggleFavorite(id);
			}
			w.QaRender(Path.Combine(_out, "s3-home-nofavourites.png"), "home", 980.0, 760.0);
			bool cleared = over.PcFavorites.Length == 0 && w.QaRowElement("mouse.speed") == null;
			Check("favourites", none && saved && starOn && live && fromHome && cleared && viaFocus == "mouse.lines",
				$"saved='{savedText}'={saved}; Ctrl+D row={viaFocus}; star shown={starOn}; Home live rows={live}; change from Home={fromHome}; cleared={cleared}");
		}
		finally
		{
			w.Close();
			over.PcFavorites = "";
		}
	}

	// The Modified dots match the documented differences of the fixture (the same nine as Windows defaults); "Changed only"
	// leaves just those rows and gives the page back.
	private static void ModifiedDots()
	{
		List<string> dots = new List<string>();
		int shown = 0;
		bool filter = false;
		string filterDetail = "";
		foreach (string page in new[] { "mouse", "keyboard", "accessibility", "apps" })
		{
			PcSettingsWindow w = Stage2Window(page);
			try
			{
				List<string> rows = w.ModifiedRows(page);
				dots.AddRange(rows);
				foreach (string id in rows)
				{
					if (w.QaBinding(id)?.Dot is FrameworkElement d && d.Visibility == Visibility.Visible)
					{
						shown++;
					}
				}
				if (page == "mouse")
				{
					w.SetChangedOnly(true);
					w.QaSnapshot(Path.Combine(_out, "s3-mouse-changed-only.png"), 980.0, 760.0);
					w.UpdateLayout();
					bool keepSpeed = w.QaBinding("mouse.speed")?.Row.IsVisible == true;
					bool hideSnap = w.QaBinding("mouse.snap")?.Row.IsVisible == false;
					bool hideLinks = w.QaRowElement("mouse.properties")?.IsVisible == false;
					w.SetChangedOnly(false);
					w.UpdateLayout();
					bool back = w.QaBinding("mouse.snap")?.Row.IsVisible == true && w.QaRowElement("mouse.properties")?.IsVisible == true;
					filter = keepSpeed && hideSnap && hideLinks && back;
					filterDetail = $"changed only: speed kept={keepSpeed} snap hidden={hideSnap} links hidden={hideLinks} all back={back}";
				}
			}
			finally
			{
				w.Close();
			}
		}
		string[] expected = new[] { "mouse.speed", "mouse.precision", "mouse.doubleclick", "mouse.lines", "mouse.ctrl", "mouse.hidetyping", "keyboard.delay", "access.duration", "apps.extensions" };
		bool exact = dots.Count == expected.Length && expected.All(dots.Contains) && shown == expected.Length;
		Check("modified-dots", exact && filter, $"{dots.Count} dots ({shown} shown): {string.Join(", ", dots)}; {filterDetail}");
	}

	// A Setup applies the values that differ as one journal group, and one Undo puts them back.
	private static void Setups()
	{
		AppSettings over = SettingsStore.DiagnosticsOverride!;
		PcSettingsWindow w = Stage2Window("home");
		try
		{
			PcSettingsWindow.HubSetup? gaming = null;
			w.AddSetup("gaming", delegate(PcSettingsWindow.HubSetup? s) { gaming = s; });
			w.AddSetup("desk");
			w.QaSettle();
			List<PcSettingsWindow.HubSetup> list = PcSettingsWindow.LoadSetups();
			bool stored = list.Count == 2 && list[0].Name == "Gaming" && list[1].Name == "Desk" && list[1].Values.Length == 7 && gaming != null;
			w.QaRender(Path.Combine(_out, "s3-home-setups.png"), "home", 980.0, 760.0);
			int mark = Mark();
			if (gaming != null)
			{
				w.ApplySetup(gaming);
			}
			w.QaSettle();
			List<JournalEntry> j = SettingsJournal.Snapshot();
			int screen = _s2.PowerValues[(PowerPlanKnob.Balanced, PowerKnob.VideoIdle, true)];
			bool group = j.Count == 2 && j.Select((JournalEntry e) => e.Group).Distinct().Count() == 1 && j[0].Group != null && j.Any((JournalEntry e) => e.Id == "mouse.speed") && j.Any((JournalEntry e) => e.Id == "power.screen");
			bool applied = _s2.Spi[0x70] == 10 && screen == 0;
			w.UndoLast();
			w.QaSettle();
			bool undone = _s2.Spi[0x70] == 20 && _s2.PowerValues[(PowerPlanKnob.Balanced, PowerKnob.VideoIdle, true)] == 900 && SettingsJournal.Count == 0;
			w.RemoveSetup("Gaming");
			w.RemoveSetup("Desk");
			bool removed = PcSettingsWindow.LoadSetups().Count == 0;
			Check("setups", stored && group && applied && undone && removed,
				$"stored Gaming + Desk ({(list.Count > 1 ? list[1].Values.Length : 0)} values)={stored}; apply one group of {j.Count}={group} applied={applied}; one undo={undone}; removed={removed}");
		}
		finally
		{
			w.Close();
			over.PcSetups = "";
		}
	}

	// The problems people describe are found in their own group, after the settings, and never take the top hit from one.
	private static void FixSearch()
	{
		PcSettingsWindow w = Stage2Window("home");
		try
		{
			List<PcSettingsWindow.HubResult> r = w.Rank("no sound");
			// the query names the problem: its fix page is the top hit (a full tier above any setting)
			bool found = r.Any((PcSettingsWindow.HubResult x) => x.Group == PcSettingsWindow.HubResultGroup.Fix && x.Hit.Id == "hub:fix/sound");
			bool notFirst = r.Count > 0 && r[0].Hit.Id == "hub:fix/sound";
			List<PcSettingsWindow.HubResult> el = w.Rank("δεν ακούγεται");
			bool greek = el.Any((PcSettingsWindow.HubResult x) => x.Hit.Id == "hub:fix/sound");
			List<PcSettingsWindow.HubResult> dc = w.Rank("double click");
			bool rowFirst = dc.Count > 0 && dc[0].Hit.Id == "hub:mouse.doubleclick" && dc.Any((PcSettingsWindow.HubResult x) => x.Hit.Id == "hub:fix/doubleclick");
			w.QaSearchBox.Text = "no sound";
			w.RunSearch("no sound");
			w.QaSnapshot(Path.Combine(_out, "s3-search-no-sound.png"), 980.0, 760.0);
			w.EscapeStep();
			Check("fix-search", found && notFirst && greek && rowFirst, $"'no sound' -> {string.Join(", ", r.Take(4).Select((PcSettingsWindow.HubResult x) => x.Hit.Id + "/" + x.Group))}; Greek finds it={greek}; 'double click' row first, fix listed={rowFirst}");
		}
		finally
		{
			w.Close();
		}
	}

	// The rows the Windows 7 Start menu gets: native controls only, hardware rows only when the PC has the hardware; the
	// pointer speed row is a name match for 'pointer speed' and its Greek name, and opens the hub at that row.
	private static void StartMenuRows()
	{
		SearchQuery q = SearchQuery.Build("pointer speed", uiGreek: false);
		List<(SystemHit Hit, int Match)> hits = HubCatalog.SearchIndex.Rank(q);
		bool speed = hits.Any(((SystemHit Hit, int Match) x) => x.Hit.Id == "hub:mouse.speed" && x.Match >= SystemSearchIndex.NameFloor && x.Hit.Target == "win81:pcsettings?row=mouse.speed");
		List<(SystemHit Hit, int Match)> el = HubCatalog.SearchIndex.Rank(SearchQuery.Build("ταχύτητα δείκτη", uiGreek: false));
		bool greek = el.Any(((SystemHit Hit, int Match) x) => x.Hit.Id == "hub:mouse.speed" && x.Match >= SystemSearchIndex.NameFloor);
		HubCaps none = new HubCaps();
		bool rules = HubCatalog.StartSearchable(HubCatalog.Row("mouse.speed")!, null) && !HubCatalog.StartSearchable(HubCatalog.Row("mouse.properties")!, null)
			&& !HubCatalog.StartSearchable(HubCatalog.Row("home.screen")!, null) && !HubCatalog.StartSearchable(HubCatalog.Row("network.wifitoggle")!, null)
			&& !HubCatalog.StartSearchable(HubCatalog.Row("network.wifitoggle")!, none) && HubCatalog.StartSearchable(HubCatalog.Row("network.wifitoggle")!, new HubCaps { HasWifi = true });
		int count = HubCatalog.Rows.Count((HubRow r) => HubCatalog.StartSearchable(r, _s2.Caps));
		bool notInIndex = SystemSearchIndex.Current == null || !SystemSearchIndex.Current.Hits.Any((SystemHit h) => h.Id.StartsWith("hub:", StringComparison.Ordinal));
		Check("start-menu-rows", speed && greek && rules && notInIndex, $"'pointer speed' -> hub:mouse.speed by name={speed}; Greek={greek}; rules={rules}; {count} rows on this PC; none in the system index={notInIndex}");
	}

	// Lighter rendering: the Launcher row writes the setting (here the override) and asks for a restart while the running
	// process does not match it.
	private static void LighterRendering()
	{
		AppSettings over = SettingsStore.DiagnosticsOverride!;
		bool before = over.ProcessSoftwareRender;
		PcSettingsWindow w = Stage2Window("launcher");
		try
		{
			ToggleButton? t = w.QaRowElement("launcher.softwarerender") is FrameworkElement row ? PcSettingsWindow.Descendants<ToggleButton>(row).FirstOrDefault() : null;
			FrameworkElement? prompt = t == null ? null : PcSettingsWindow.Descendants<Button>(w.QaRowElement("launcher.softwarerender")!.Parent as DependencyObject ?? w.QaRowElement("launcher.softwarerender")!).FirstOrDefault((Button b) => AutomationName(b) == "Restart the launcher now");
			bool hiddenAtFirst = prompt == null || !prompt.IsVisible;
			if (t != null)
			{
				t.IsChecked = !before;
			}
			w.UpdateLayout();
			bool written = over.ProcessSoftwareRender == !before;
			bool asks = prompt != null && prompt.IsVisible == (over.ProcessSoftwareRender != App.ProcessSoftwareRendering);
			w.QaSnapshot(Path.Combine(_out, "s3-launcher-lighter.png"), 980.0, 760.0);
			if (t != null)
			{
				t.IsChecked = before;
			}
			Check("lighter-rendering", t != null && written && asks && hiddenAtFirst && over.ProcessSoftwareRender == before, $"row={t != null} written={written} restart prompt follows={asks} hidden while it matches={hiddenAtFirst}; process software rendering={App.ProcessSoftwareRendering}");
		}
		finally
		{
			w.Close();
			over.ProcessSoftwareRender = before;
			SettingsJournal.QaReset();
		}
	}

	private static string AutomationName(DependencyObject d) => System.Windows.Automation.AutomationProperties.GetName(d);

	// The Windows 7 Start menu's search index: given back after 5 minutes unused (never while in use), hashed only when a
	// refresh compares it.
	private static void IndexLifetime()
	{
		SystemSearchIndex? keep = SystemSearchIndex.Current;
		try
		{
			// asking for the complete index only notes the use (nothing is built or read)
			SystemSearchIndex.Ensure(synchronous: false);
			bool recent = !SystemSearchIndex.MaybeRelease(Environment.TickCount64 + 1000, inUse: false) && SystemSearchIndex.Current != null;
			long now = Environment.TickCount64 + SystemSearchIndex.ReleaseAfterMs + 1000;
			bool inUse = !SystemSearchIndex.MaybeRelease(now, inUse: true) && SystemSearchIndex.Current != null;
			bool released = SystemSearchIndex.MaybeRelease(now, inUse: false) && SystemSearchIndex.Current == null;
			SystemSearchIndex.QaSet(keep);
			SystemSearchData data = new SystemSearchData { Schema = SystemSearchData.CurrentSchema, UiLanguage = "en-US", Languages = new[] { "en-US" } };
			data.Hits.Add(new SystemHit { Id = "qa:1", Kind = SystemHitKind.Setting, Name = "Pointer", Keywords = new[] { "mouse" }, Target = "ms-settings:mousetouchpad" });
			ulong compactHash = SystemSearchIndex.FromData(data, "qa", 0.0, compact: true).ContentHash;
			SystemSearchData data2 = new SystemSearchData { Schema = SystemSearchData.CurrentSchema, UiLanguage = "en-US", Languages = new[] { "en-US" } };
			data2.Hits.Add(new SystemHit { Id = "qa:1", Kind = SystemHitKind.Setting, Name = "Pointer", Keywords = new[] { "mouse" }, Target = "ms-settings:mousetouchpad" });
			ulong fullHash = SystemSearchIndex.FromData(data2, "qa", 0.0, compact: false).ContentHash;
			bool lazy = compactHash == 0UL && fullHash != 0UL;
			Check("index-lifetime", inUse && released && recent && lazy && ReferenceEquals(SystemSearchIndex.Current, keep), $"kept while in use={inUse} released after 5 min={released} kept while recent={recent}; hash only when asked (compact={compactHash}, full={fullHash != 0UL})");
		}
		finally
		{
			SystemSearchIndex.QaSet(keep);
		}
	}

	// The dead code and the unused embedded pictures are gone.
	private static void DeadCode()
	{
		Assembly a = typeof(PcSettingsWindow).Assembly;
		string[] types = { "Win81Layer.PlaceResolver", "Win81Layer.PressFx", "Win81Layer.WifiBars81" };
		List<string> left = types.Where((string n) => a.GetType(n) != null).ToList();
		bool nativeChrome = typeof(AppSettings).GetProperty("NativeChrome") != null;
		List<string> assets = new List<string>();
		foreach (string name in new[] { "assets/pcsettings_gear.png", "assets/start-bg.orig.png" })
		{
			try
			{
				using Stream? s = Application.GetResourceStream(new Uri("pack://application:,,,/" + name))?.Stream;
				if (s != null)
				{
					assets.Add(name);
				}
			}
			catch (IOException)
			{
			}
		}
		Check("dead-code", left.Count == 0 && !nativeChrome && assets.Count == 0, $"types left: {(left.Count == 0 ? "none" : string.Join(", ", left))}; AppSettings.NativeChrome={nativeChrome}; embedded pictures left: {(assets.Count == 0 ? "none" : string.Join(", ", assets))}");
	}

	// Renders of the stage 3 surfaces in light, dark and Dark rest into <out>\stage3.
	private static void Stage3Renders()
	{
		string dir = Path.Combine(_out, "stage3");
		Directory.CreateDirectory(dir);
		FakeSettingsWorld stage1 = _world;
		_s2 = new FakeSettingsWorld("this-pc");
		_world = _s2;
		SettingsWorld.Current = _s2;
		AppSettings? keep = SettingsStore.DiagnosticsOverride;
		try
		{
			foreach (string look in new[] { "light", "dark", "rest" })
			{
				UseLook(look);
				AppSettings over = SettingsStore.DiagnosticsOverride!;
				over.PcFavorites = "mouse.speed;keyboard.delay;apps.extensions;sound.settings";
				over.PcSetups = "gaming|Gaming|mouse.speed=10;mouse.precision=0%2C0%2C0;power.screen=0\npresentation|Presentation|power.screen=0";
				_s2.Muted = true;
				PcSettingsWindow w = NewWindow();
				w.QaUseCaps(_s2.Caps);
				try
				{
					Show(w);
					foreach (string page in new[] { "home", "fix/sound", "fix/mouse", "fix/doubleclick", "fix/typing", "fix/screen", "fix/timeout", "mouse", "keyboard", "launcher" })
					{
						w.QaRender(Path.Combine(dir, look + "-" + page.Replace('/', '-') + ".png"), page, 980.0, 760.0);
						w.QaWaitReads();
						w.QaRenderTall(Path.Combine(dir, look + "-" + page.Replace('/', '-') + "-tall.png"), page, 748.0);
					}
					w.QaRender(Path.Combine(dir, look + "-mouse-changed.png"), "mouse", 980.0, 760.0);
					w.SetChangedOnly(true);
					w.QaSnapshot(Path.Combine(dir, look + "-mouse-changed-only.png"), 980.0, 760.0);
					w.SetChangedOnly(false);
					w.QaSearchBox.Text = "no sound";
					w.RunSearch("no sound");
					w.QaSnapshot(Path.Combine(dir, look + "-search-no-sound.png"), 980.0, 760.0);
					w.EscapeStep();
				}
				finally
				{
					w.Close();
					_s2.Muted = false;
				}
			}
			Check("stage3-renders", true, Directory.GetFiles(dir, "*.png").Length + " PNGs in " + dir);
		}
		catch (Exception ex)
		{
			Check("stage3-renders", false, ex.Message);
		}
		finally
		{
			SettingsWorker.Flush(3000);
			SettingsJournal.QaReset();
			UseLook("light");
			if (keep != null && SettingsStore.DiagnosticsOverride != null)
			{
				SettingsStore.DiagnosticsOverride.PcFavorites = "";
				SettingsStore.DiagnosticsOverride.PcSetups = "";
			}
			_world = stage1;
			SettingsWorld.Current = stage1;
		}
	}
}
