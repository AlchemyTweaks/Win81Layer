using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;

#nullable enable annotations

namespace Win81Layer;

// The checks of the final review pass of --pchubtest (run with stage 3): search follows this PC's hardware and names
// pages first, read-only values are whole and announced, the tiles and sliders follow the palette, the search cue sits
// where the text goes, Left moves inside tile grids, Undo is limited to this window and redoable, the radios, power plans
// and the wheel rows stay true after a change, the Keep guard covers groups and page changes, a failed undo stays in the
// list, the Control Panel policy names match, and the winget command is shown as it runs. All against the fake world.
internal static partial class PcHubDiagnostics
{
	private static void ReviewChecks()
	{
		SearchFollowsCaps();
		SearchNamesPages();
		ReadOnlyValues();
		TilesAndGrids();
		SearchCue();
		FieldHeights();
		DisplaysAndLinks();
		UndoScope();
		RadiosUndo();
		PowerFollowsPlan();
		WheelRows();
		GuardCases();
		UndoFailureStays();
		PolicyAndCommands();
		AudioDeviceEvents();
	}

	// Every hub row a search shows exists on its page on this PC (no dead ends): bluetooth, wifi, power and the rest.
	private static void SearchFollowsCaps()
	{
		PcSettingsWindow w = Stage2Window("home");
		PcSettingsWindow probe = NewWindow();
		probe.QaUseCaps(_s2.Caps);
		try
		{
			Show(probe);
			List<string> missing = new List<string>();
			int rows = 0;
			foreach (string q in new[] { "bluetooth", "wifi", "wi-fi", "power", "ενέργεια", "sleep", "μπαταρία", "οθόνη", "brightness", "wallpaper", "ταπετσαρία", "γραμμή εργασιών", "έναρξη", "sound devices", "night light" })
			{
				foreach (PcSettingsWindow.HubResult r in w.Rank(q).Where((PcSettingsWindow.HubResult x) => x.Group == PcSettingsWindow.HubResultGroup.Hub && x.RowId != null && x.PageId != null))
				{
					rows++;
					probe.QaBuildPage(r.PageId!);
					if (probe.QaRowElement(r.RowId!) == null)
					{
						missing.Add(q + ":" + r.RowId);
					}
				}
			}
			Check("search-caps-rows", missing.Count == 0 && rows > 0, missing.Count == 0 ? rows + " hub rows listed, each one on its page" : "not on their page: " + string.Join(", ", missing.Distinct().Take(10)));
		}
		finally
		{
			probe.Close();
			w.Close();
		}
	}

	// A page the query names comes first; launcher rows that decide how the shell starts never show their live control;
	// Home's Quick card copies give way to the row they copy.
	private static void SearchNamesPages()
	{
		PcSettingsWindow w = Stage2Window("home");
		try
		{
			string Top(string q)
			{
				List<PcSettingsWindow.HubResult> r = w.Rank(q);
				return r.Count == 0 ? "-" : r[0].Hit.Id;
			}
			(string Q, string Want)[] cases = new (string, string)[]
			{
				("power", "hub:power"), ("ενέργεια", "hub:power"), ("οθόνη", "hub:displays"), ("γραμμή εργασιών", "hub:taskbar"),
				("έναρξη", "hub:start"), ("taskbar", "hub:taskbar"), ("wallpaper", "hub:appearance.background"), ("ταπετσαρία", "hub:appearance.background")
			};
			List<string> got = cases.Select(((string Q, string Want) c) => c.Q + "->" + Top(c.Q)).ToList();
			bool tops = cases.All(((string Q, string Want) c) => Top(c.Q) == c.Want);
			List<PcSettingsWindow.HubResult> bright = w.Rank("brightness");
			bool noCopies = !bright.Any((PcSettingsWindow.HubResult r) => r.RowId == "home.brightness") && bright.Any((PcSettingsWindow.HubResult r) => r.RowId == "displays.brightness");
			foreach ((string q, string file) in new[] { ("οθόνη", "final-search-othoni.png"), ("power", "final-search-power.png"), ("wallpaper", "final-search-wallpaper.png"), ("bluetooth", "final-search-bluetooth.png") })
			{
				w.QaSearchBox.Text = q;
				w.RunSearch(q);
				w.QaSnapshot(System.IO.Path.Combine(_out, file), 980.0, 760.0);
			}
			// "Start at sign-in": the result opens the Launcher page; its toggle is not on the results page
			w.QaSearchBox.Text = "start at sign-in";
			w.RunSearch("start at sign-in");
			w.QaSnapshot(System.IO.Path.Combine(_out, "final-search-signin.png"), 980.0, 760.0);
			bool topIsLogon = w.QaResults.Count > 0 && w.QaResults[0].RowId == "launcher.logon";
			bool noLiveToggle = w.QaPageElement is DependencyObject page && !PcSettingsWindow.Descendants<ToggleButton>(page).Any((ToggleButton t) => AutomationName(t) == "Start at sign-in");
			w.EscapeStep();
			Check("search-topic-pages", tops && noCopies && topIsLogon && noLiveToggle, $"{string.Join(", ", got)}; Brightness once={noCopies}; 'start at sign-in' top={topIsLogon} no live toggle={noLiveToggle}");
		}
		finally
		{
			w.Close();
		}
	}

	// About: the edition says Windows 11 from build 22000 on; long values use the row's free width, with their tooltip and
	// the name a screen reader reads once they arrive.
	private static void ReadOnlyValues()
	{
		bool rule = SystemInfo.EditionName("Windows 10 Pro", 26200) == "Windows 11 Pro" && SystemInfo.EditionName("Windows 10 Pro", 19045) == "Windows 10 Pro" && SystemInfo.EditionName("", 26200) == "Windows";
		bool live = Environment.OSVersion.Version.Build < 22000 || !SystemInfo.Edition.StartsWith("Windows 10", StringComparison.OrdinalIgnoreCase);
		Check("about-edition", rule && live, $"rule={rule}; this PC: {SystemInfo.Edition} (build {Environment.OSVersion.Version.Build})");
		PcSettingsWindow w = Stage2Window("about");
		try
		{
			w.QaWaitReads();
			string detail = "";
			bool ok = true;
			foreach (string id in new[] { "about.cpu", "about.gpu", "about.type" })
			{
				if (w.QaRowElement(id) is not Grid { Tag: TextBlock v })
				{
					ok = false;
					detail += id + " missing; ";
					continue;
				}
				TextBlock m = new TextBlock { Text = v.Text, FontFamily = v.FontFamily, FontSize = v.FontSize };
				m.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
				bool whole = m.DesiredSize.Width <= v.ActualWidth + 1.0;
				string name = AutomationName(v);
				bool named = v.Text != "…" && name.EndsWith(": " + v.Text, StringComparison.Ordinal);
				bool tip = Equals(v.ToolTip, v.Text);
				ok &= whole && named && tip;
				detail += $"{id} '{v.Text}' {m.DesiredSize.Width:0}/{v.ActualWidth:0} DIP whole={whole} name={named} tooltip={tip}; ";
			}
			Check("info-values", ok, detail.TrimEnd(' ', ';'));
		}
		finally
		{
			w.Close();
		}
	}

	// Look and mode tiles: short captions that fit their 132 DIP strip; Left steps back inside a grid of swatches and leaves
	// it from the first one.
	private static void TilesAndGrids()
	{
		List<string> labels = CompositionProfiles.MenuModes.Select(((string Label, string Id) m) => m.Label).Concat(ShellProfileManager.Profiles.Select((ShellProfileDescriptor p) => p.Name)).ToList();
		Typeface face = new Typeface(PcSettingsWindow.SegoeUi, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
		double widest = labels.Max((string l) => new FormattedText(PcSettingsWindow.ShortTileName(l), CultureInfo.CurrentCulture, FlowDirection.LeftToRight, face, 11.0, Brushes.White, 1.0).Width);
		Check("tile-captions", widest <= 124.0, $"widest caption {widest:0} DIP of 132 ({string.Join(" | ", labels.Select(PcSettingsWindow.ShortTileName))})");
		PcSettingsWindow w = Stage2Window("appearance");
		try
		{
			List<Button> swatches = w.QaRowElement("appearance.accent") is FrameworkElement row ? PcSettingsWindow.Descendants<Button>(row).Where((Button b) => b.IsVisible).ToList() : new List<Button>();
			bool second = swatches.Count > 2 && PcSettingsWindow.LeftStaysInGrid(swatches[1]);
			bool first = swatches.Count > 2 && !PcSettingsWindow.LeftStaysInGrid(swatches[0]);
			Check("left-in-grid", second && first, $"{swatches.Count} swatches; Left from the second stays={second}; from the first goes to the sidebar={first}");
		}
		finally
		{
			w.Close();
		}
	}

	// The placeholder and the typed text start at the same pixel (rendered both ways, ink bounds compared).
	private static void SearchCue()
	{
		PcSettingsWindow w = Stage2Window("home");
		try
		{
			w.QaSearchBox.Text = "";
			Pump();
			w.AlignSearchCue();
			Int32Rect cue = InkBounds(w.QaSearchHost);
			w.QaSearchCue.Visibility = Visibility.Collapsed;
			w.QaSearchBox.Text = "Search settings";
			// the cue would be hidden by the text anyway; the text is rendered alone
			Pump();
			Int32Rect typed = InkBounds(w.QaSearchHost);
			// a word without descenders: its ink runs from the ascenders to the baseline, so its middle is the line's
			// optical centre, which has to sit on the field's centre line (the magnifier glyph is centred on it)
			w.QaSearchBox.Text = "Search";
			Pump();
			Int32Rect word = InkBounds(w.QaSearchHost);
			FrameworkElement? content = w.QaSearchBox.Template?.FindName("PART_ContentHost", w.QaSearchBox) as FrameworkElement;
			double contentY = content?.TranslatePoint(new Point(0.0, 0.0), w.QaSearchHost).Y ?? double.NaN;
			Rect first = w.QaSearchBox.GetRectFromCharacterIndex(0);
			w.QaSearchBox.Text = "";
			Pump();
			bool ok = cue.Width > 0 && typed.Width > 0 && Math.Abs(cue.X - typed.X) <= 1 && Math.Abs(cue.Y - typed.Y) <= 1;
			Check("search-cue-aligned", ok, $"cue ink at {cue.X},{cue.Y} {cue.Width}x{cue.Height}; typed text at {typed.X},{typed.Y} {typed.Width}x{typed.Height}");
			double mid = word.Y + word.Height / 2.0;
			double boxMid = w.QaSearchHost.ActualHeight / 2.0;
			Check("search-text-centered", word.Height > 0 && Math.Abs(mid - boxMid) <= 1.0, $"'Search' ink rows {word.Y}..{word.Y + word.Height - 1}, middle {mid:0.0} vs field middle {boxMid:0.0}; text host at y {contentY:0.#} h {content?.ActualHeight:0.#} in a {w.QaSearchBox.ActualHeight:0.#} DIP box; first character line y {first.Y:0.#} h {first.Height:0.#}");
		}
		finally
		{
			w.Close();
		}
	}

	// Every one-line text field on the pages is laid out at the hub's 28 DIP height (not the Metro style's 36) and its
	// text line is centred in it.
	private static void FieldHeights()
	{
		List<string> got = new List<string>();
		bool ok = true;
		foreach ((string page, string row) in new[] { ("start", "start.weathercity"), ("start", "start.newsfeed"), ("power/plans", "power.plans.filter"), ("keyboard", "keyboard.test") })
		{
			PcSettingsWindow w = Stage2Window(page);
			try
			{
				w.QaWaitReads();
				TextBox? box = w.QaRowElement(row) is DependencyObject r ? PcSettingsWindow.Descendants<TextBox>(r).FirstOrDefault() : null;
				if (box == null)
				{
					ok = false;
					got.Add(row + ": no field");
					continue;
				}
				box.UpdateLayout();
				FrameworkElement? content = box.Template?.FindName("PART_ContentHost", box) as FrameworkElement;
				double mid = content == null ? double.NaN : content.TranslatePoint(new Point(0.0, content.ActualHeight / 2.0), box).Y;
				bool fine = Math.Abs(box.ActualHeight - 28.0) < 0.5 && Math.Abs(mid - 14.0) <= 1.0;
				ok &= fine;
				got.Add($"{row} h {box.ActualHeight:0.#} text middle {mid:0.#}");
			}
			finally
			{
				w.Close();
			}
		}
		Check("fields-28", ok, string.Join("; ", got));
	}

	// The bounds of the text ink in the search field (right of the magnifier glyph): the window's root is rendered and the
	// field's rectangle read from it (a visual rendered on its own keeps its offset in the parent).
	private static Int32Rect InkBounds(FrameworkElement host)
	{
		FrameworkElement root = (FrameworkElement)(Window.GetWindow(host)?.Content ?? host);
		root.UpdateLayout();
		Point at = host.TranslatePoint(new Point(0.0, 0.0), root);
		int rw = (int)Math.Ceiling(root.ActualWidth);
		int rh = (int)Math.Ceiling(root.ActualHeight);
		RenderTargetBitmap rtb = new RenderTargetBitmap(rw, rh, 96.0, 96.0, PixelFormats.Pbgra32);
		rtb.Render(root);
		int width = Math.Min((int)Math.Ceiling(host.ActualWidth), rw - (int)at.X);
		int height = Math.Min((int)Math.Ceiling(host.ActualHeight), rh - (int)at.Y);
		byte[] px = new byte[width * height * 4];
		rtb.CopyPixels(new Int32Rect((int)Math.Round(at.X), (int)Math.Round(at.Y), width, height), px, width * 4, 0);
		double Lum(int x, int y)
		{
			int i = (y * width + x) * 4;
			return (0.2126 * px[i + 2] + 0.7152 * px[i + 1] + 0.0722 * px[i]) / 255.0;
		}
		double bg = Lum(width - 3, height / 2);
		int minX = int.MaxValue;
		int minY = int.MaxValue;
		int maxX = -1;
		int maxY = -1;
		for (int y = 0; y < height; y++)
		{
			for (int x = 26; x < width - 2; x++)
			{
				if (Math.Abs(Lum(x, y) - bg) > 0.18)
				{
					minX = Math.Min(minX, x);
					minY = Math.Min(minY, y);
					maxX = Math.Max(maxX, x);
					maxY = Math.Max(maxY, y);
				}
			}
		}
		return maxX < 0 ? new Int32Rect(0, 0, 0, 0) : new Int32Rect(minX, minY, maxX - minX + 1, maxY - minY + 1);
	}

	// Displays names each monitor once (header) and its mode once (row); a deep link that falls back to a page another
	// row of the same page opens is left out; the page column stays put between short and long pages.
	private static void DisplaysAndLinks()
	{
		PcSettingsWindow w = Stage2Window("displays");
		try
		{
			w.QaWaitReads();
			bool modeRow = w.QaRowElement("displays.monitor") is Grid { Tag: TextBlock v } && v.Text.Contains(" × ", StringComparison.Ordinal) && v.Text.Contains(" Hz", StringComparison.Ordinal);
			List<string> headers = w.QaPageElement is DependencyObject page ? PcSettingsWindow.Descendants<TextBlock>(page).Where((TextBlock t) => t.FontFamily == PcSettingsWindow.SegoeUiSemibold).Select((TextBlock t) => t.Text).ToList() : new List<string>();
			bool headersPlain = headers.Count > 0 && !headers.Any((string h) => h.Contains('×'));
			List<string> folds = new List<string>();
			bool foldsOk = true;
			foreach ((string page2, string id) in new[] { ("displays", "displays.nightlight"), ("displays", "displays.advanced"), ("displays", "displays.graphics"), ("sound", "sound.devices"), ("sound", "sound.apps") })
			{
				w.OpenCategory(page2);
				w.QaWaitReads();
				HubRow row = HubCatalog.Row(id)!;
				bool own = SettingsLink.IndexHasPage(row.Link!.Uri!);
				bool shown = w.QaRowElement(id) != null;
				foldsOk &= own == shown;
				folds.Add(id + (shown ? " shown" : " folded") + (own ? " (own page)" : " (parent page)"));
			}
			// laid out as shown, without the page's entrance slide
			w.QaRender(System.IO.Path.Combine(_out, "final-column-home.png"), "home", 980.0, 760.0);
			double homeX = ColumnX(w);
			w.QaRender(System.IO.Path.Combine(_out, "final-column-devices.png"), "devices", 980.0, 760.0);
			double devicesX = ColumnX(w);
			bool overlay = w.QaPageElement is ScrollViewer sv && ReferenceEquals(sv.Template, PcSettingsWindow.HubScrollViewerTemplate);
			bool steady = Math.Abs(homeX - devicesX) < 0.5 && homeX > 0;
			Check("displays-and-links", modeRow && headersPlain && foldsOk && overlay && steady, $"mode row={modeRow} headers [{string.Join(" | ", headers.Take(4))}]; {string.Join(", ", folds)}; column x home {homeX:0.#} devices {devicesX:0.#} overlay scrollbar={overlay}");
		}
		finally
		{
			w.Close();
		}
	}

	private static double ColumnX(PcSettingsWindow w)
	{
		if (w.QaPageElement is ScrollViewer { Content: Border { Child: StackPanel column } } sv)
		{
			sv.UpdateLayout();
			return column.TranslatePoint(new Point(0.0, 0.0), w.QaRoot).X;
		}
		return -1.0;
	}

	// Ctrl+Z undoes this window's own changes only; an entry from an earlier session stays; a successful undo says so with
	// Redo; Undo of a launcher row that decides how the shell starts asks first.
	private static void UndoScope()
	{
		SettingsJournal.QaReset();
		_s2.Spi[0x6C] = 5;
		SettingsJournal.Add(new JournalEntry { Id = "mouse.hchars", Title = "Horizontal scrolling", Before = "3", After = "5", BeforeText = "3 characters", AfterText = "5 characters", Kind = "knob" }, _s2.Now.AddDays(-2.0));
		PcSettingsWindow w = Stage2Window("mouse");
		try
		{
			w.UndoLast();
			w.QaSettle();
			bool oldKept = SettingsJournal.Count == 1 && _s2.Spi[0x6C] == 5 && w.QaUndoState == "none";
			w.QaChange("mouse.speed", "14");
			w.QaSettle();
			w.UndoLast();
			w.QaSettle();
			string bar = w.QaUndoBarText;
			w.QaSnapshot(System.IO.Path.Combine(_out, "final-undone-redo.png"), 980.0, 760.0);
			bool own = _s2.Spi[0x70] == 20 && SettingsJournal.Count == 1 && bar.StartsWith("Undone: Pointer speed", StringComparison.Ordinal);
			Button? redo = PcSettingsWindow.Descendants<Button>(w.QaRoot).FirstOrDefault((Button b) => AutomationName(b) == "Redo" && b.IsVisible);
			redo?.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
			w.QaSettle();
			bool redone = _s2.Spi[0x70] == 14 && SettingsJournal.Count == 2 && SettingsJournal.Last()?.Id == "mouse.speed";
			w.UndoLast();
			w.QaSettle();
			JournalEntry? logon = SettingsJournal.Add(new JournalEntry { Id = "launcher.logon", Title = "Start at sign-in", Before = "Off", After = "On", BeforeText = "Off", AfterText = "On", Kind = "ui" }, _s2.Now);
			if (logon != null)
			{
				w.UndoEntry(logon);
			}
			bool asks = w.QaUndoState == "confirm" && w.QaUndoBarText.Contains("Start at sign-in", StringComparison.Ordinal) && SettingsJournal.Snapshot().Contains(logon!);
			Check("undo-scope", oldKept && own && redo != null && redone && asks, $"older entry kept by Ctrl+Z={oldKept}; own change undone={own} bar '{bar}'; redo={redone}; launcher row asks first={asks}");
		}
		finally
		{
			w.Close();
			SettingsJournal.QaReset();
			_s2.Spi[0x6C] = 3;
		}
	}

	// Turn off all wireless, then Undo (or turning it off again): the radios that were on come back, the others stay off.
	private static void RadiosUndo()
	{
		FakeSettingsWorld keep = _s2;
		UseFixture("laptop");
		_s2.Wifi = true;
		_s2.Bluetooth = false;
		PcSettingsWindow w = Stage2Window("network");
		try
		{
			w.QaWaitReads();
			w.QaChange("network.allwireless", "1");
			w.QaSettle();
			bool off = _s2.Wifi == false && _s2.Bluetooth == false;
			w.UndoLast();
			w.QaSettle();
			bool undo = _s2.Wifi == true && _s2.Bluetooth == false;
			w.QaWaitReads();
			w.QaChange("network.allwireless", "1");
			w.QaSettle();
			w.QaWaitReads();
			w.QaChange("network.allwireless", "0");
			w.QaSettle();
			bool toggle = _s2.Wifi == true && _s2.Bluetooth == false;
			Check("radios-undo", off && undo && toggle, $"all off={off}; undo: wifi {_s2.Wifi} bluetooth {_s2.Bluetooth} (was on/off)={undo}; switch back={toggle}");
		}
		finally
		{
			w.Close();
			SettingsJournal.QaReset();
			_s2 = keep;
			_world = keep;
			SettingsWorld.Current = keep;
		}
	}

	// After a plan switch the screen row shows the new plan's value; an undo of a screen value goes to the plan it was
	// changed in, not the plan that is active now.
	private static void PowerFollowsPlan()
	{
		SettingsJournal.QaReset();
		Guid balanced = PowerPlanKnob.Balanced;
		Guid seven = FakeSettingsWorld.PlanSeven;
		_s2.PowerValues[(seven, PowerKnob.VideoIdle, true)] = 600;
		PcSettingsWindow w = Stage2Window("power");
		try
		{
			w.QaWaitReads();
			w.QaChange("power.plan", seven.ToString("D"));
			w.QaSettle();
			w.QaWaitReads();
			bool reread = w.QaBinding("power.screen")?.Committed == "600";
			w.QaChange("power.plan", balanced.ToString("D"));
			w.QaSettle();
			w.QaWaitReads();
			w.QaChange("power.screen", "300");
			w.QaSettle();
			w.QaChange("power.plan", seven.ToString("D"));
			w.QaSettle();
			w.QaWaitReads();
			JournalEntry? screen = SettingsJournal.Snapshot().FirstOrDefault((JournalEntry e) => e.Id == "power.screen");
			bool context = screen?.Context != null && Guid.TryParse(screen.Context, out Guid g) && g == balanced;
			if (screen != null)
			{
				w.UndoEntry(screen);
			}
			w.QaSettle();
			w.QaWaitReads();
			bool undone = _s2.PowerValues[(balanced, PowerKnob.VideoIdle, true)] == 900 && _s2.PowerValues[(seven, PowerKnob.VideoIdle, true)] == 600 && _s2.ActivePlan == seven && w.QaBinding("power.screen")?.Committed == "600";
			Check("power-follows-plan", reread && context && undone, $"screen row after switch shows the new plan={reread}; journal scheme={screen?.Context}; undo wrote Balanced 900, left plan 7 at 600 and active={undone}");
		}
		finally
		{
			w.Close();
			SettingsJournal.QaReset();
			_s2.ActivePlan = balanced;
			_s2.PowerValues[(balanced, PowerKnob.VideoIdle, true)] = 900;
			_s2.PowerValues[(seven, PowerKnob.VideoIdle, true)] = 900;
		}
	}

	// The wheel's mode and its line count: each row reads the shared value again after the other changed.
	private static void WheelRows()
	{
		SettingsJournal.QaReset();
		int lines0 = _s2.Spi[0x68];
		PcSettingsWindow w = Stage2Window("mouse");
		try
		{
			w.QaChange("mouse.wheel", "page");
			w.QaSettle();
			w.QaWaitReads();
			bool page = w.QaBinding("mouse.lines")?.Committed == "-1";
			w.QaChange("mouse.wheel", "lines");
			w.QaSettle();
			w.QaWaitReads();
			string? back = w.QaBinding("mouse.lines")?.Committed;
			bool lines = back == lines0.ToString(CultureInfo.InvariantCulture);
			Check("wheel-rows", page && lines, $"one screen: lines row reads -1={page}; multiple lines: lines row {back} (was {lines0})={lines}");
		}
		finally
		{
			w.Close();
			SettingsJournal.QaReset();
			_s2.Spi[0x68] = lines0;
		}
	}

	// Windows defaults that swap the buttons back ask to keep the change too; the banner stays while pages change.
	private static void GuardCases()
	{
		SettingsJournal.QaReset();
		_s2.Swap = 1;
		PcSettingsWindow w = Stage2Window("mouse");
		try
		{
			w.QaWaitReads();
			w.ApplyDefaults("mouse");
			w.QaSettle();
			bool guarded = w.QaGuardShown && _s2.Swap == 0;
			w.OpenCategory("keyboard");
			Pump();
			bool stays = w.QaGuardVisible;
			w.QaSnapshot(System.IO.Path.Combine(_out, "final-guard-on-keyboard.png"), 980.0, 760.0);
			w.GuardRevert();
			w.QaSettle();
			bool reverted = _s2.Swap == 1 && _s2.Spi[0x70] == 10 && SettingsJournal.Snapshot().All((JournalEntry e) => e.Id != "mouse.primary");
			w.UndoLast();
			w.QaSettle();
			bool restored = _s2.Spi[0x70] == 20;
			Check("guard-cases", guarded && stays && reverted && restored, $"defaults group asks to keep the button swap={guarded}; banner stays on another page={stays}; revert puts back the buttons only={reverted}; group undo={restored}");
		}
		finally
		{
			w.Close();
			SettingsJournal.QaReset();
			_s2.Swap = 0;
		}
	}

	// An undo Windows refuses stays in the list (to try again).
	private static void UndoFailureStays()
	{
		SettingsJournal.QaReset();
		PcSettingsWindow w = Stage2Window("mouse");
		try
		{
			w.QaChange("mouse.ctrl", "0");
			w.QaSettle();
			int before = SettingsJournal.Count;
			_s2.RefuseSpi.Add(0x101D);
			w.UndoLast();
			w.QaSettle();
			bool failed = w.QaUndoState == "failed" && SettingsJournal.Count == before && before == 1;
			_s2.RefuseSpi.Remove(0x101D);
			w.UndoLast();
			w.QaSettle();
			bool retried = SettingsJournal.Count == 0 && _s2.Spi[0x101C] == 1;
			Check("undo-failure-stays", failed && retried, $"refused undo kept in the list={failed}; tried again={retried}");
		}
		finally
		{
			_s2.RefuseSpi.Remove(0x101D);
			w.Close();
			SettingsJournal.QaReset();
			_s2.Spi[0x101C] = 1;
		}
	}

	// DisallowCpl / RestrictCpl name Control Panel items by canonical name or .cpl file; both match. The winget install
	// shows the command it runs, without agreeing to licence terms for the user.
	private static void PolicyAndCommands()
	{
		HubCaps deny = new HubCaps();
		deny.DisallowCpl.Add("Microsoft.PowerOptions");
		HubCaps allow = new HubCaps { RestrictCpl = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Microsoft.PowerOptions" } };
		HubCaps denyFile = new HubCaps();
		denyFile.DisallowCpl.Add("mmsys.cpl");
		bool cpl = !deny.CplAllowed("powercfg.cpl") && allow.CplAllowed("powercfg.cpl") && !allow.CplAllowed("mmsys.cpl") && !denyFile.CplAllowed("Microsoft.Sound") && deny.CplAllowed("mmsys.cpl");
		string cmd = PcSettingsWindow.WingetCommand("Microsoft.Sysinternals.ProcessExplorer");
		bool winget = !cmd.Contains("accept", StringComparison.OrdinalIgnoreCase) && cmd == "winget install --id Microsoft.Sysinternals.ProcessExplorer -e";
		Check("policy-and-commands", cpl && winget, $"cpl canonical/file names={cpl}; winget '{cmd}'");
	}

	// The Sound page hears device events only: a volume step (the Core Audio callback) does not reach a devices-only
	// subscriber (reflection on the shared service; no write).
	private static void AudioDeviceEvents()
	{
		AudioEndpointService a = AudioEndpointService.Shared;
		int devices = 0;
		int all = 0;
		IDisposable d = a.Subscribe(delegate { devices++; }, devicesOnly: true);
		IDisposable s = a.Subscribe(delegate { all++; });
		try
		{
			const BindingFlags F = BindingFlags.Instance | BindingFlags.NonPublic;
			object? render = typeof(AudioEndpointService).GetField("_render", F)?.GetValue(a);
			MethodInfo? volume = typeof(AudioEndpointService).GetMethod("OnVolumeEvent", F);
			MethodInfo? device = typeof(AudioEndpointService).GetMethod("OnDeviceEvent", F);
			volume?.Invoke(a, new object?[] { render, 0.5f, false });
			int afterVolume = devices;
			int allAfterVolume = all;
			device?.Invoke(a, new object[] { false });
			bool ok = render != null && volume != null && device != null && afterVolume == 0 && allAfterVolume == 1 && devices == 1 && all == 2;
			Check("audio-device-events", ok, $"volume event: devices-only {afterVolume}, tray {allAfterVolume}; device event: devices-only {devices}, tray {all}");
		}
		finally
		{
			d.Dispose();
			s.Dispose();
			a.Invalidate();
		}
	}
}
