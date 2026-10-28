using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

#nullable enable annotations

namespace Win81Layer;

// Home's own cards and what they share with the pages: the knob rows (one definition, used by the pages, Favourites,
// Setups and the fixes), Favourites (a star on a row's hover or Ctrl+D; the rows live on Home), Setups (named groups of
// values applied by hand as one undoable group) and Fix a problem (drill-ins that only read until a fix is clicked; each
// fix is journaled).
public sealed partial class PcSettingsWindow
{
	// ===== Knob rows =====

	private static readonly (string Value, string Label)[] DurationChoices = new (string, string)[]
	{
		("5", "5 seconds"), ("7", "7 seconds"), ("15", "15 seconds"), ("30", "30 seconds"), ("60", "1 minute"), ("300", "5 minutes")
	};

	private static readonly string[] MotionOptions = new string[4] { "Authentic", "Fast", "Reduced", "Off" };

	// The row of a native setting exactly as its page builds it, for the page given (null: the row's own page). Null when
	// the setting has no such row (it needs hardware this PC lacks, or it is not a single-control row).
	private FrameworkElement? KnobRow(string id, string? page = null)
	{
		switch (id)
		{
		case "mouse.speed":
			return NSlider(id, 1, 20, "Slow", "Fast", "How far the pointer moves for a movement of the mouse. Windows' default is 10.", large: 5, page: page);
		case "mouse.precision":
			return NToggle(id, "Mouse acceleration: faster movements take the pointer further. Gamers often turn it off.", isOn: MouseAccelKnob.IsOn, valueFor: (bool on, string? cur) => MouseAccelKnob.Toggle(cur, on), page: page);
		case "mouse.primary":
			return NSegmented(id, new[] { ("0", "Left"), ("1", "Right") }, "You are asked to keep the change; it is put back after 10 s otherwise.", page: page);
		case "mouse.doubleclick":
			// Slow -> Fast like Pointer speed and Windows' Mouse Properties: the position runs against the time (1100 - ms)
			return NSlider(id, 200, 900, "Slow", "Fast", "The longest time between the two clicks of a double-click. Windows' default is 500 ms.", toPos: (string? v) => int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out int ms) ? 1100 - ms : 600, fromPos: (int p) => (1100 - p).ToString(CultureInfo.InvariantCulture), step: 10, page: page);
		case "mouse.clicklock":
			return NToggle(id, "Hold the button briefly to lock it, click again to release: drag without holding.", page: page);
		case "mouse.clicklocktime":
			return NSlider(id, 200, 2200, "Short", "Long", "How long the button is held before it locks.", step: 100, page: page);
		case "mouse.wheel":
			return NChoice(id, (string? v) => new[] { ("lines", "Multiple lines at a time"), ("page", "One screen at a time") }, width: 220.0, page: page);
		case "mouse.lines":
			return NSlider(id, 1, 20, "1", "20", "Lines per notch of the wheel.", toPos: (string? v) => int.TryParse(v, out int x) && x > 0 ? x : 3, page: page);
		case "mouse.hchars":
			return NSlider(id, 1, 20, "1", "20", "Characters per tilt of the wheel.", page: page);
		case "mouse.ctrl":
			return NToggle(id, "Press Ctrl to show where the pointer is.", page: page);
		case "mouse.hidetyping":
			return NToggle(id, page: page);
		case "mouse.trails":
			return NChoice(id, (string? v) => new[] { ("0", "Off"), ("3", "Short"), ("7", "Medium"), ("12", "Long") }, page: page);
		case "mouse.snap":
			return NToggle(id, "The pointer jumps to the default button of a dialog box.", page: page);
		case "keyboard.delay":
			return NSlider(id, 0, 3, "Short", "Long", "How long a key is held before it starts to repeat (about 250 ms to 1 s).", page: page);
		case "keyboard.rate":
			return NSlider(id, 0, 31, "Slow", "Fast", "How fast a held key repeats.", page: page);
		case "keyboard.caretwidth":
			return NSlider(id, 1, 20, "Thin", "Thick", "The width of the blinking text cursor.", page: page);
		case "keyboard.access":
			return NToggle(id, "Underline the letter of menu items and buttons that Alt plus that letter opens.", page: page);
		case "access.animations":
			return NToggle(id, "Windows' own animations of windows, menus and controls.", page: page);
		case "access.duration":
			return NChoice(id, (string? v) => DurationChoices, "How long Windows shows a notification before it goes to the notification centre.", page: page);
		case "access.sticky":
			return NToggle(id, "Press one key at a time for keyboard shortcuts. The Shift-five-times shortcut keeps its own setting.", page: page);
		case "access.filter":
			return NToggle(id, "Ignore brief or repeated keystrokes.", page: page);
		case "access.toggle":
			return NToggle(id, "Hear a tone when you press Caps Lock, Num Lock and Scroll Lock.", page: page);
		case "time.24h":
			return NToggle(id, "13:00 instead of 1:00 PM, in the taskbar clock and every app.", isOn: TimeFormatKnob.Is24, valueFor: (bool on, string? cur) => TimeFormatKnob.For(on), page: page);
		case "time.shortdate":
		{
			string[] formats = SettingsWorld.Current.ReadShortDateFormats();
			FrameworkElement row = NChoice(id, (string? v) => formats.Select((string x) => (x, DatePreview(x))).ToArray(), "How short dates are written.", width: 220.0, page: page);
			if (_bindings.TryGetValue(id, out KnobBinding? sd))
			{
				// the custom format the user has is shown with today's date too
				sd.Text = (string? v) => v == null ? "…" : DatePreview(v);
				sd.Show(sd.Committed);
			}
			return row;
		}
		case "time.firstday":
			return NChoice(id, (string? v) => Enumerable.Range(0, 7).Select((int i) => (i.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture.DateTimeFormat.DayNames[(i + 1) % 7])).ToArray(), page: page);
		case "time.measure":
			return NChoice(id, (string? v) => new[] { ("0", "Metric"), ("1", "US") }, page: page);
		case "apps.extensions":
			return NToggle(id, "Show .txt, .exe and the other extensions after file names in File Explorer.", page: page);
		case "apps.hidden":
			return NToggle(id, "Show hidden files, folders and drives in File Explorer.", page: page);
		case "appearance.motion":
		{
			AppSettings s = SettingsStore.FastSnapshot;
			MotionMode userMotion = Motion.Parse(s.MotionMode);
			bool calm = DarkRest.IsOn && DarkRest.CalmMotion(s) && (userMotion == MotionMode.Authentic || userMotion == MotionMode.Fast);
			return NChoice(id, (string? v) => MotionOptions.Select((string o) => (o, o)).ToArray(), "How the launcher animates: Authentic (Windows 8.1 timing), Fast, Reduced (half the travel) or Off.", secondary: calm ? "Dark rest plays it as Reduced for now" : null, page: page);
		}
		case "power.screen":
			return PowerChoice(id, _caps != null && _caps.HasBattery ? "Plugged in, turn off screen after" : "Turn off screen after", TimeoutChoices, "When plugged in, the screen turns off after this long without input.");
		case "power.sleep":
			return _caps != null && _caps.SleepAllowed ? PowerChoice(id, _caps.HasBattery ? "Plugged in, sleep after" : "Sleep after", TimeoutChoices, "When plugged in, the PC goes to sleep after this long without input.") : null;
		case "network.wifitoggle":
			return _caps != null && _caps.HasWifi ? NToggle(id, page: page) : null;
		case "devices.bttoggle":
			return _caps != null && _caps.HasBluetooth ? NToggle(id, page: page) : null;
		default:
			return null;
		}
	}

	// ===== Favourites =====

	// The starred rows, in the order they were starred (only rows the catalog still has).
	internal static List<string> FavoriteList()
	{
		string raw = SettingsStore.FastSnapshot.PcFavorites ?? string.Empty;
		List<string> list = new List<string>();
		foreach (string part in raw.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
		{
			HubRow? r = HubCatalog.Row(part);
			if (r != null && !list.Contains(r.Id, StringComparer.OrdinalIgnoreCase))
			{
				list.Add(r.Id);
			}
		}
		return list;
	}

	// A row that can be starred: a control of its own or a link to Windows, not one of Home's own rows.
	internal static bool CanFavorite(string? id)
	{
		if (id == null || HubCatalog.Row(id) is not HubRow r)
		{
			return false;
		}
		if (r.Page is "home" or "recent" || r.Page.StartsWith("fix/", StringComparison.Ordinal))
		{
			return false;
		}
		return r.Kind is HubRowKind.Toggle or HubRowKind.Choice or HubRowKind.Slider or HubRowKind.Segmented or HubRowKind.DeviceList or HubRowKind.Link;
	}

	internal static bool IsFavorite(string id) => FavoriteList().Contains(id, StringComparer.OrdinalIgnoreCase);

	// Stars or unstars a row (the star, Ctrl+D): saved at once (AppSettings.PcFavorites), Home follows.
	internal void ToggleFavorite(string id)
	{
		if (!CanFavorite(id))
		{
			return;
		}
		string rowId = HubCatalog.Row(id)!.Id;
		List<string> list = FavoriteList();
		bool on = list.RemoveAll((string x) => string.Equals(x, rowId, StringComparison.OrdinalIgnoreCase)) == 0;
		if (on)
		{
			list.Add(rowId);
		}
		string value = string.Join(";", list);
		_favoriteSet = null;
		try
		{
			HubSettings.Update(delegate(AppSettings s) { s.PcFavorites = value; });
		}
		catch (Exception ex)
		{
			Logger.Log("PC settings favourites: " + ex.Message);
			return;
		}
		if (_stars.TryGetValue(rowId, out Button? star))
		{
			PaintStar(star, on, hovered: true);
		}
		else if (on && _starHosts.TryGetValue(rowId, out (StackPanel Line, FrameworkElement Host) place))
		{
			// starred from the keyboard before the pointer ever passed over the row
			MakeStar(rowId, place.Line, place.Host, on: true);
		}
		string title = HubCatalog.Title(rowId);
		ShowNote(on ? "“" + title + "” is on Home now" : "“" + title + "” left Favourites", "Undo", delegate { ToggleFavorite(rowId); });
		if (_current == "home" && !_searchActive)
		{
			RebuildCurrentPage();
		}
	}

	// The row (or the control inside it) that has the keyboard focus: its registered row id.
	private string? FocusedRowId() => RowIdOf(Keyboard.FocusedElement as DependencyObject);

	// The registered row an element belongs to (the element itself or an ancestor).
	internal string? RowIdOf(DependencyObject? d)
	{
		while (d != null)
		{
			if (d is FrameworkElement fe)
			{
				string aid = AutomationProperties.GetAutomationId(fe);
				if (!string.IsNullOrEmpty(aid) && _rowElements.TryGetValue(aid, out FrameworkElement? row) && ReferenceEquals(row, fe))
				{
					return aid;
				}
			}
			d = d is Visual || d is System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);
		}
		return null;
	}

	// Ctrl+D: stars or unstars the focused row.
	internal bool ToggleFavoriteFocused()
	{
		string? id = FocusedRowId();
		if (id == null || !CanFavorite(id))
		{
			return false;
		}
		ToggleFavorite(id);
		return true;
	}

	private readonly Dictionary<string, Button> _stars = new Dictionary<string, Button>(StringComparer.OrdinalIgnoreCase);

	// Where a row's star goes (its title line and the row the pointer passes over), until the star is made.
	private readonly Dictionary<string, (StackPanel Line, FrameworkElement Host)> _starHosts = new Dictionary<string, (StackPanel, FrameworkElement)>(StringComparer.OrdinalIgnoreCase);

	private HashSet<string>? _favoriteSet;

	// The star of a row: shown while the pointer is over the row (made then, so a page build stays light) or the row has
	// the keyboard focus, and always on a starred row (filled, accent).
	private void AttachStar(string id, FrameworkElement row)
	{
		if (!CanFavorite(id) || LabelLine(row) is not StackPanel line || row is ButtonBase)
		{
			return;
		}
		_favoriteSet ??= new HashSet<string>(FavoriteList(), StringComparer.OrdinalIgnoreCase);
		FrameworkElement hoverHost = row is StackPanel { Children.Count: > 0 } sp && sp.Children[0] is FrameworkElement first ? first : row;
		_starHosts[id] = (line, hoverHost);
		if (_favoriteSet.Contains(id))
		{
			MakeStar(id, line, hoverHost, on: true);
			return;
		}
		void Ensure(object? s, EventArgs e)
		{
			hoverHost.MouseEnter -= Ensure;
			hoverHost.IsKeyboardFocusWithinChanged -= EnsureFocus;
			if (_stars.TryGetValue(id, out Button? made) && made.Parent == line)
			{
				return;
			}
			Button star = MakeStar(id, line, hoverHost, on: false);
			PaintStar(star, false, hovered: true);
		}
		void EnsureFocus(object s, DependencyPropertyChangedEventArgs e)
		{
			if (e.NewValue is true)
			{
				Ensure(s, EventArgs.Empty);
			}
		}
		hoverHost.MouseEnter += Ensure;
		hoverHost.IsKeyboardFocusWithinChanged += EnsureFocus;
	}

	private Button MakeStar(string id, StackPanel line, FrameworkElement hoverHost, bool on)
	{
		TextBlock glyph = new TextBlock { FontFamily = Glyphs, FontSize = 13.0, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
		Button star = new Button
		{
			Template = _pal.PlainTemplate,
			Style = BareButton,
			Width = 24.0,
			Height = 24.0,
			Margin = new Thickness(2.0, 0.0, 0.0, 0.0),
			VerticalAlignment = VerticalAlignment.Center,
			Cursor = Cursors.Hand,
			Content = glyph,
			Tag = id
		};
		star.SetResourceReference(FrameworkElement.FocusVisualStyleProperty, "Metro81.Focus");
		ToolTipService.SetInitialShowDelay(star, 400);
		star.Click += delegate { ToggleFavorite(id); };
		line.Children.Add(star);
		_stars[id] = star;
		PaintStar(star, on, hovered: false);
		hoverHost.MouseEnter += delegate { PaintStar(star, IsStarOn(star), hovered: true); };
		hoverHost.MouseLeave += delegate { PaintStar(star, IsStarOn(star), hovered: hoverHost.IsKeyboardFocusWithin); };
		hoverHost.IsKeyboardFocusWithinChanged += delegate(object s, DependencyPropertyChangedEventArgs e) { PaintStar(star, IsStarOn(star), hovered: e.NewValue is true || hoverHost.IsMouseOver); };
		return star;
	}

	private static bool IsStarOn(Button star) => star.Tag is string id && IsFavorite(id);

	// Filled accent star when starred; an outline in the secondary ink on hover; hidden otherwise.
	private void PaintStar(Button star, bool on, bool hovered)
	{
		if (star.Content is TextBlock glyph)
		{
			glyph.Text = on ? "" : "";
			glyph.Foreground = on ? _pal.Link : _pal.Ink2;
		}
		star.Visibility = on || hovered ? Visibility.Visible : Visibility.Hidden;
		string title = star.Tag is string id ? HubCatalog.Title(id) : "this setting";
		string name = on ? "Remove " + title + " from Favourites" : "Add " + title + " to Favourites";
		AutomationProperties.SetName(star, name);
		star.ToolTip = (on ? "Remove from Favourites" : "Add to Favourites") + " (Ctrl+D)";
	}

	// Home's Favourites card: each starred row live with its own control (a link opens Windows; any other row jumps to
	// its place). Hidden while nothing is starred.
	private void FavoritesCard(Pg pg)
	{
		List<string> favs = FavoriteList();
		if (favs.Count == 0)
		{
			return;
		}
		pg.Header("Favourites");
		foreach (string id in favs)
		{
			HubRow row = HubCatalog.Row(id)!;
			if (!PageShown(row.Page) || !RowShown(row))
			{
				continue;
			}
			FrameworkElement? live = KnobRow(id, "home");
			if (live == null && row.Kind == HubRowKind.Link)
			{
				live = LinkRow(id, PathOf(id));
				if (live != null)
				{
					live = WithStar(id, live);
				}
			}
			if (live == null)
			{
				string target = id;
				Button jump = ClickRow(row.Title, PathOf(id), MenuGlyphs.ChevronPath(_pal.Ink2), delegate { Navigate(row.Page, target, push: true); });
				Register(id, jump);
				live = WithStar(id, jump);
			}
			pg.Add(live);
		}
	}

	// A whole-row button (a link, a jump) with its filled star beside it (a star inside the button would be a button in a
	// button).
	private FrameworkElement WithStar(string id, FrameworkElement rowButton)
	{
		Grid g = new Grid();
		g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.0, GridUnitType.Star) });
		g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
		g.Children.Add(rowButton);
		StackPanel host = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0.0, 0.0, 8.0, 0.0) };
		Grid.SetColumn(host, 1);
		g.Children.Add(host);
		MakeStar(id, host, g, on: true);
		return g;
	}

	// ===== Undo bar notes (favourites, setups) =====

	// A note in the undo bar with one action of its own (not a journal entry).
	private void ShowNote(string text, string? actionText = null, Action? action = null)
	{
		if (_closed)
		{
			return;
		}
		EnsureUndoBar();
		_undoBar!.Background = _pal.Card;
		_undoBar.BorderBrush = _pal.CardBorder;
		_undoText!.Foreground = _pal.Ink;
		_undoText.Text = text;
		_undoButtons!.Children.Clear();
		if (actionText != null && action != null)
		{
			_undoButtons.Children.Add(UndoLinkButton(actionText, delegate
			{
				HideUndoBar();
				action();
			}));
		}
		RevealUndoBar(6.0);
	}

	// ===== Setups =====

	// A named group of values (row id -> value), applied by hand as one journal group. Kind: the template it came from
	// ("gaming", "desk", "presentation"), which only adds a hint.
	internal sealed record HubSetup(string Kind, string Name, (string Row, string Value)[] Values);

	internal const int MaxSetups = 6;

	// The rows a Desk setup captures (the values the user has now).
	private static readonly string[] DeskRows = new string[7] { "mouse.speed", "mouse.precision", "mouse.doubleclick", "mouse.lines", "keyboard.delay", "keyboard.rate", "power.screen" };

	// One setup per line: kind|name|row=value;row=value (each part escaped).
	internal static List<HubSetup> LoadSetups()
	{
		List<HubSetup> list = new List<HubSetup>();
		string raw = SettingsStore.FastSnapshot.PcSetups ?? string.Empty;
		foreach (string line in raw.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
		{
			string[] parts = line.Split('|');
			if (parts.Length != 3)
			{
				continue;
			}
			try
			{
				List<(string, string)> values = new List<(string, string)>();
				foreach (string pair in parts[2].Split(';', StringSplitOptions.RemoveEmptyEntries))
				{
					int eq = pair.IndexOf('=');
					if (eq > 0)
					{
						string row = Uri.UnescapeDataString(pair.Substring(0, eq));
						if (HubKnobs.Get(row) != null)
						{
							values.Add((row, Uri.UnescapeDataString(pair.Substring(eq + 1))));
						}
					}
				}
				if (values.Count > 0)
				{
					list.Add(new HubSetup(Uri.UnescapeDataString(parts[0]), Uri.UnescapeDataString(parts[1]), values.ToArray()));
				}
			}
			catch
			{
			}
			if (list.Count >= MaxSetups)
			{
				break;
			}
		}
		return list;
	}

	private static string Serialize(IEnumerable<HubSetup> setups)
	{
		return string.Join("\n", setups.Select((HubSetup s) => Uri.EscapeDataString(s.Kind) + "|" + Uri.EscapeDataString(s.Name) + "|" + string.Join(";", s.Values.Select(((string Row, string Value) v) => Uri.EscapeDataString(v.Row) + "=" + Uri.EscapeDataString(v.Value)))));
	}

	private static void SaveSetups(List<HubSetup> list)
	{
		string value = Serialize(list.Take(MaxSetups));
		HubSettings.Update(delegate(AppSettings s) { s.PcSetups = value; });
	}

	private static string TemplateName(string kind) => kind switch
	{
		"gaming" => "Gaming",
		"desk" => "Desk",
		_ => "Presentation"
	};

	// A new setup from a template: Gaming (pointer speed 10, precision off, screen never off), Desk (the values this PC has
	// now) or Presentation (screen never off). The name gets a number when it is taken. done: called with the new setup on
	// the UI thread (null when there were already six or nothing could be read).
	internal void AddSetup(string kind, Action<HubSetup?>? done = null)
	{
		List<HubSetup> list = LoadSetups();
		if (list.Count >= MaxSetups)
		{
			done?.Invoke(null);
			return;
		}
		string baseName = TemplateName(kind);
		string name = baseName;
		for (int n = 2; list.Any((HubSetup s) => string.Equals(s.Name, name, StringComparison.CurrentCultureIgnoreCase)); n++)
		{
			name = baseName + " " + n.ToString(CultureInfo.InvariantCulture);
		}
		void Save((string Row, string Value)[] values)
		{
			if (values.Length == 0)
			{
				done?.Invoke(null);
				return;
			}
			HubSetup setup = new HubSetup(kind, name, values);
			List<HubSetup> now = LoadSetups();
			if (now.Count >= MaxSetups)
			{
				done?.Invoke(null);
				return;
			}
			now.Add(setup);
			try
			{
				SaveSetups(now);
			}
			catch (Exception ex)
			{
				Logger.Log("PC settings setups: " + ex.Message);
				done?.Invoke(null);
				return;
			}
			done?.Invoke(setup);
			if (_current == "home" && !_searchActive)
			{
				RebuildCurrentPage();
			}
		}
		switch (kind)
		{
		case "gaming":
			Save(new[] { ("mouse.speed", "10"), ("mouse.precision", "0,0,0"), ("power.screen", "0") });
			break;
		case "desk":
		{
			ISettingsWorld w = SettingsWorld.Current;
			RunThenUi(delegate
			{
				List<(string, string)> values = new List<(string, string)>();
				foreach (string row in DeskRows)
				{
					string? v = SafeRead(HubKnobs.Get(row), w);
					if (v != null)
					{
						values.Add((row, v));
					}
				}
				return values.ToArray();
			}, Save);
			break;
		}
		default:
			Save(new[] { ("power.screen", "0") });
			break;
		}
	}

	private static string? SafeRead(HubKnob? k, ISettingsWorld w)
	{
		if (k == null)
		{
			return null;
		}
		try
		{
			return k.Read(w);
		}
		catch
		{
			return null;
		}
	}

	// Reads off the UI thread and continues on it, whatever page is shown by then (a Setup applies even if Home was built
	// again meanwhile). Counted as pending for QA.
	private void RunThenUi<T>(Func<T> read, Action<T> then)
	{
		_pending++;
		Task.Run(read).ContinueWith(delegate(Task<T> t)
		{
			try
			{
				base.Dispatcher.BeginInvoke(DispatcherPriority.Normal, (Action)delegate
				{
					_pending = Math.Max(0, _pending - 1);
					if (t.Status != TaskStatus.RanToCompletion || _closed)
					{
						return;
					}
					try
					{
						then(t.Result);
					}
					catch (Exception ex)
					{
						Logger.Log("PC settings: " + ex.Message);
					}
				});
			}
			catch
			{
			}
		}, TaskScheduler.Default);
	}

	internal void RemoveSetup(string name)
	{
		List<HubSetup> list = LoadSetups();
		HubSetup? gone = list.FirstOrDefault((HubSetup s) => s.Name == name);
		if (gone == null)
		{
			return;
		}
		list.Remove(gone);
		try
		{
			SaveSetups(list);
		}
		catch (Exception ex)
		{
			Logger.Log("PC settings setups: " + ex.Message);
			return;
		}
		ShowNote("Setup “" + name + "” removed", "Undo", delegate
		{
			List<HubSetup> back = LoadSetups();
			if (back.Count < MaxSetups)
			{
				back.Add(gone);
				SaveSetups(back);
				if (_current == "home" && !_searchActive)
				{
					RebuildCurrentPage();
				}
			}
		});
		if (_current == "home" && !_searchActive)
		{
			RebuildCurrentPage();
		}
	}

	// Applies a setup: the current values are read first (off the UI thread), the rows that differ change as one journal
	// group (one Undo puts them all back). A row this PC does not have is skipped.
	internal void ApplySetup(HubSetup setup)
	{
		ISettingsWorld w = SettingsWorld.Current;
		List<(HubKnob K, string Value)> items = setup.Values.Select(((string Row, string Value) v) => (HubKnobs.Get(v.Row), v.Value)).Where(((HubKnob? K, string Value) x) => x.K != null && SetupRowAvailable(x.K.RowId)).Select(((HubKnob? K, string Value) x) => (x.K!, x.Value)).ToList();
		RunThenUi(() => items.Select(((HubKnob K, string Value) x) => (x.K, x.Value, Cur: SafeRead(x.K, w))).ToList(), delegate(List<(HubKnob K, string Value, string? Cur)> now)
		{
			List<Change> changes = now.Where(((HubKnob K, string Value, string? Cur) x) => x.Cur != null && !x.K.Same(x.Cur, x.Value)).Select(((HubKnob K, string Value, string? Cur) x) => new Change
			{
				K = x.K,
				B = _bindings.TryGetValue(x.K.RowId, out KnobBinding? b) ? b : null,
				Before = x.Cur!,
				After = x.Value
			}).ToList();
			if (changes.Count == 0)
			{
				ShowNote("“" + setup.Name + "” is already in place");
				return;
			}
			ApplyChanges(changes, "setup:" + setup.Name + ":" + Environment.TickCount64, "Setup “" + setup.Name + "”", journal: true);
		});
	}

	// A Setup changes only the rows this PC has (the same gating as the pages and search).
	private bool SetupRowAvailable(string rowId)
	{
		HubRow? r = HubCatalog.Row(rowId);
		return r == null || HubCatalog.RowAvailable(r, _caps, needControl: true);
	}

	// "Speed 10 · Precision off · Screen never off" (the first three; the full list is the row's help).
	private static string SetupSummary(HubSetup s, int max = 3)
	{
		IEnumerable<string> parts = s.Values.Select(((string Row, string Value) v) => SetupPart(v.Row, v.Value));
		string line = string.Join(" · ", parts.Take(max));
		return s.Values.Length > max ? line + " · +" + (s.Values.Length - max) + " more" : line;
	}

	private static string SetupPart(string row, string value)
	{
		return row switch
		{
			"mouse.speed" => "Speed " + value,
			"mouse.precision" => "Precision " + (MouseAccelKnob.IsOn(value) ? "on" : "off"),
			"mouse.doubleclick" => "Double-click " + value + " ms",
			"mouse.lines" => value + (value == "1" ? " line" : " lines") + " per notch",
			"keyboard.delay" => "Delay " + FormatValue(row, value).ToLowerInvariant(),
			"keyboard.rate" => "Repeat " + value + "/31",
			"power.screen" => value == "0" ? "Screen never off" : "Screen off after " + FormatValue(row, value),
			_ => HubCatalog.Title(row) + " " + FormatValue(row, value)
		};
	}

	private static string FormatValue(string row, string value)
	{
		if (row.StartsWith("power.screen", StringComparison.Ordinal) || row.StartsWith("power.sleep", StringComparison.Ordinal))
		{
			string? label = TimeoutChoices.FirstOrDefault(((string Value, string Label) c) => c.Value == value).Label;
			return label ?? HubKnobs.Get(row)?.Format(value) ?? value;
		}
		return HubKnobs.Get(row)?.Format(value) ?? value;
	}

	private void SetupsCard(Pg pg)
	{
		List<HubSetup> setups = LoadSetups();
		pg.Header("Setups");
		foreach (HubSetup s in setups)
		{
			HubSetup target = s;
			StackPanel buttons = new StackPanel { Orientation = Orientation.Horizontal };
			Button apply = HubButton("Apply", delegate { ApplySetup(target); });
			AutomationProperties.SetName(apply, "Apply " + s.Name);
			Button remove = HubButton("Remove", delegate { RemoveSetup(target.Name); });
			AutomationProperties.SetName(remove, "Remove " + s.Name);
			remove.Margin = new Thickness(6.0, 0.0, 0.0, 0.0);
			buttons.Children.Add(apply);
			buttons.Children.Add(remove);
			string help = string.Join(", ", s.Values.Select(((string Row, string Value) v) => HubCatalog.Title(v.Row) + ": " + FormatValue(v.Row, v.Value))) + "." + (s.Kind == "presentation" ? " Tip: turn on Do not disturb in the notification centre (Win+N) too." : string.Empty);
			Grid row = RowShell(s.Name, SetupSummary(s), help, buttons, out _);
			AutomationProperties.SetName(buttons, s.Name);
			Register("home.setup." + s.Name, row);
			pg.Add(row);
		}
		if (setups.Count >= MaxSetups)
		{
			pg.Add(InfoRow("home.setups.full", "6 of 6", title: "Setups", help: "Remove a setup to make room for another."));
			return;
		}
		// one row; the templates appear when asked for (Home opens lighter)
		StackPanel host = new StackPanel();
		WrapPanel? chips = null;
		Button add = HubButton("New…", delegate
		{
			if (chips != null)
			{
				chips.Visibility = chips.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
				return;
			}
			chips = new WrapPanel { Margin = new Thickness(12.0, 0.0, 12.0, 6.0) };
			foreach ((string kind, string line) in new[] { ("gaming", "Gaming"), ("desk", "Desk (your current values)"), ("presentation", "Presentation") })
			{
				string k = kind;
				Button chip = Chip(line, delegate { AddSetup(k); });
				AutomationProperties.SetName(chip, "New setup: " + line);
				chips.Children.Add(chip);
			}
			host.Children.Add(chips);
			if (chips.Children.Count > 0 && chips.Children[0] is Button first)
			{
				first.Focus();
			}
		});
		AutomationProperties.SetName(add, "New setup");
		Grid row2 = RowShell(setups.Count == 0 ? "New setup" : "Another setup", setups.Count == 0 ? "Switch several settings at once, by hand" : "From a template: Gaming, Desk or Presentation", "Start from Gaming (pointer speed 10, precision off, screen never off), Desk (the values you have now) or Presentation (screen never off). Apply switches them all at once; one Undo puts them back.", add, out _);
		host.Children.Add(row2);
		Register("home.setups", host);
		pg.Add(host);
	}

	// A light chip (Fix a problem, the setup templates): one shared template per look, no control style to resolve.
	private ControlTemplate? _chipTemplate;

	private Button Chip(string text, Action click)
	{
		if (_chipTemplate == null)
		{
			ControlTemplate tpl = new ControlTemplate(typeof(Button));
			FrameworkElementFactory bd = new FrameworkElementFactory(typeof(Border), "Bd");
			bd.SetValue(Border.BackgroundProperty, _pal.RowHover);
			bd.SetValue(Border.BorderBrushProperty, _pal.CardBorder);
			bd.SetValue(Border.BorderThicknessProperty, new Thickness(1.0));
			bd.SetValue(Border.PaddingProperty, new Thickness(12.0, 0.0, 12.0, 0.0));
			bd.SetValue(UIElement.SnapsToDevicePixelsProperty, true);
			FrameworkElementFactory cp = new FrameworkElementFactory(typeof(ContentPresenter), "Cp");
			cp.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
			cp.SetValue(System.Windows.Documents.TextElement.ForegroundProperty, _pal.Ink);
			bd.AppendChild(cp);
			tpl.VisualTree = bd;
			Trigger hover = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
			hover.Setters.Add(new Setter(Border.BackgroundProperty, _pal.Pressed, "Bd"));
			tpl.Triggers.Add(hover);
			Trigger press = new Trigger { Property = ButtonBase.IsPressedProperty, Value = true };
			press.Setters.Add(new Setter(Border.BackgroundProperty, _pal.ToggleOn, "Bd"));
			press.Setters.Add(new Setter(Border.BorderBrushProperty, _pal.ToggleOn, "Bd"));
			press.Setters.Add(new Setter(System.Windows.Documents.TextElement.ForegroundProperty, _pal.Rest ? DarkRestPalette.InkOnAccent : Brushes.White, "Cp"));
			tpl.Triggers.Add(press);
			tpl.Seal();
			_chipTemplate = tpl;
		}
		Button b = new Button
		{
			Style = BareButton,
			Template = _chipTemplate,
			Height = 28.0,
			Margin = new Thickness(0.0, 0.0, 6.0, 6.0),
			Cursor = Cursors.Hand,
			Content = text,
			FontSize = 13.0
		};
		b.SetResourceReference(FrameworkElement.FocusVisualStyleProperty, "Metro81.Focus");
		AutomationProperties.SetName(b, text);
		b.Click += delegate
		{
			try
			{
				click();
			}
			catch (Exception ex)
			{
				Logger.Log("PC settings chip '" + text + "': " + ex.Message);
			}
		};
		return b;
	}

	// ===== Fix a problem =====

	// Home's chips: one per problem, each a drill-in page.
	private void FixChips(Pg pg)
	{
		pg.Header("Fix a problem");
		WrapPanel chips = new WrapPanel();
		foreach (HubPage p in HubCatalog.FixPages)
		{
			string id = p.Id;
			chips.Children.Add(Chip(p.Title, delegate { Navigate(id, null, push: true); }));
		}
		pg.Add(CustomRow("home.fix", chips, new Thickness(12.0, 10.0, 12.0, 6.0)));
	}

	private ScrollViewer FixPage(string id)
	{
		return id switch
		{
			"fix/sound" => FixSoundPage(),
			"fix/mouse" => FixMousePage(),
			"fix/doubleclick" => FixDoubleClickPage(),
			"fix/typing" => FixTypingPage(),
			"fix/screen" => FixScreenPage(),
			_ => FixTimeoutPage()
		};
	}

	private void FixIntro(Pg pg, string id, string line)
	{
		HubPage p = HubCatalog.Page(id)!;
		pg.Block(Hero(p.Tile, p.Glyph, p.Title, line));
	}

	// A verdict line of a diagnosis card (what was found, read only).
	private FrameworkElement Finding(string id, string text, string? secondary = null, bool problem = true)
	{
		TextBlock mark = new TextBlock
		{
			Text = problem ? "" : "",
			FontFamily = Glyphs,
			FontSize = 14.0,
			Foreground = problem ? _pal.Error : _pal.Link,
			VerticalAlignment = VerticalAlignment.Center
		};
		Grid row = RowShell(text, secondary, null, mark, out _);
		AutomationProperties.SetName(mark, problem ? "Problem" : "OK");
		Register(id, row);
		return row;
	}

	// A fix: a button that changes one setting (or a group) through the journal, so it can be undone.
	private FrameworkElement FixButton(string id, string title, string button, string? secondary, List<Change> changes, string? group = null)
	{
		return ButtonRow(id, button, delegate
		{
			ApplyChanges(changes, group == null ? null : "fix:" + group + ":" + Environment.TickCount64, group, journal: true, delegate
			{
				if (_current.StartsWith("fix/", StringComparison.Ordinal))
				{
					RebuildCurrentPage();
				}
			});
		}, secondary, title: title);
	}

	// No sound: what plays where (the output device, its volume and mute, muted apps, other devices), then the fixes.
	private ScrollViewer FixSoundPage()
	{
		Pg pg = new Pg(this, "fix/sound");
		ISettingsWorld w = SettingsWorld.Current;
		FixIntro(pg, "fix/sound", "Checks what Windows plays sound on; nothing changes until you pick a fix");
		pg.Header("What was found");
		StackPanel found = new StackPanel();
		found.Children.Add(InfoRowDetached("Checking the sound…", "…"));
		pg.Add(CustomRow("fix.sound.found", found, new Thickness(0.0)));
		pg.Header("Fix it");
		StackPanel fixes = new StackPanel();
		pg.Add(CustomRow("fix.sound.fixes", fixes, new Thickness(0.0)));
		pg.Add(ButtonRow("fix.sound.test", "Play", delegate { RunWrite(w.PlayTestSound); }, "Plays the Windows sound on the output device", title: "Play a test sound"));
		pg.Add(LinkRow("fix.sound.troubleshoot", "Windows' own sound troubleshooter", LinkSpec.Page("ms-settings:troubleshoot", parent: "ms-settings:sound"), "Troubleshoot"));
		pg.Add(LinkRow("sound.playback", "Sound Control Panel › Playback"));
		ReadAsync(() => (Devices: w.ReadEndpoints(false), Volume: w.ReadEndpointVolume(false), Sessions: w.ReadSessions()), delegate((AudioEndpointInfo[] Devices, (int Volume, bool Muted)? Volume, AppVolumeInfo[] Sessions) r)
		{
			found.Children.Clear();
			fixes.Children.Clear();
			void AddTo(StackPanel host, FrameworkElement row)
			{
				if (host.Children.Count > 0)
				{
					host.Children.Add(Separator());
				}
				host.Children.Add(row);
			}
			AudioEndpointInfo? output = r.Devices.FirstOrDefault((AudioEndpointInfo d) => d.IsDefault);
			HubKnob vol = HubKnobs.Get("sound.volume")!;
			HubKnob mute = HubKnobs.Get("sound.mute")!;
			HubKnob device = HubKnobs.Get("sound.output")!;
			if (r.Devices.Length == 0 || output == null)
			{
				AddTo(found, Finding("fix.sound.nodevice", "No output device is connected or turned on", "Plug in speakers or headphones, or turn a device on in the Sound Control Panel"));
			}
			else
			{
				AddTo(found, Finding("fix.sound.device", "Plays on " + output.Name, r.Devices.Length > 1 ? r.Devices.Length + " output devices on this PC" : "The only output device", problem: false));
			}
			if (r.Volume is { } vm)
			{
				int level = vm.Volume;
				bool muted = vm.Muted;
				if (muted)
				{
					AddTo(found, Finding("fix.sound.muted", "The sound is muted"));
					AddTo(fixes, FixButton("fix.sound.unmute", "Turn the sound back on", "Unmute", "Mute On → Off", new List<Change> { new Change { K = mute, Before = "1", After = "0" } }));
				}
				if (level < 5)
				{
					AddTo(found, Finding("fix.sound.low", "The volume is at " + level + "%"));
					AddTo(fixes, FixButton("fix.sound.volume", "Set the volume to 40%", "Set 40%", "Output volume " + level + "% → 40%", new List<Change> { new Change { K = vol, Before = level.ToString(CultureInfo.InvariantCulture), After = "40" } }));
				}
				if (!muted && level >= 5)
				{
					AddTo(found, Finding("fix.sound.level", "Volume " + level + "%, not muted", problem: false));
				}
			}
			List<AppVolumeInfo> mutedApps = r.Sessions.Where((AppVolumeInfo s) => s.Muted || s.Volume == 0).ToList();
			if (mutedApps.Count > 0)
			{
				AddTo(found, Finding("fix.sound.apps", string.Join(", ", mutedApps.Take(3).Select((AppVolumeInfo s) => s.Name)) + (mutedApps.Count == 1 ? " is" : " are") + " muted in the app mixer", "An app's own volume can be off while the rest plays"));
				AddTo(fixes, DrillRow("fix.sound.appvolume", null, delegate { Navigate("sound/apps", null, push: true); }, "Turn an app's own sound back on", "App volume"));
			}
			foreach (AudioEndpointInfo other in r.Devices.Where((AudioEndpointInfo d) => !d.IsDefault).Take(3))
			{
				if (output == null)
				{
					continue;
				}
				AddTo(fixes, FixButton("fix.sound.use:" + other.Id, "Use " + other.Name, "Use", "Plays everything there (calls keep their device)", new List<Change> { new Change { K = device, Before = output.Id, After = other.Id } }));
			}
			if (fixes.Children.Count == 0)
			{
				fixes.Children.Add(InfoRowDetached("Nothing to fix here", "Try the test sound"));
			}
		});
		return pg.Done();
	}

	// Mouse too fast or slow: speed and precision, plus the gaming preset (speed 10, precision off) as one group.
	private ScrollViewer FixMousePage()
	{
		Pg pg = new Pg(this, "fix/mouse");
		FixIntro(pg, "fix/mouse", "Pointer speed and acceleration decide how far the pointer moves");
		pg.Header("Pointer");
		pg.Add(KnobRow("mouse.speed", "fix/mouse"));
		pg.Add(KnobRow("mouse.precision", "fix/mouse"));
		pg.Header("Presets");
		HubKnob speed = HubKnobs.Get("mouse.speed")!;
		HubKnob precision = HubKnobs.Get("mouse.precision")!;
		pg.Add(ButtonRow("fix.mouse.gaming", "Apply", delegate
		{
			KnobBinding? sb = QaBinding("mouse.speed");
			KnobBinding? pb = QaBinding("mouse.precision");
			if (sb?.Committed == null || pb?.Committed == null)
			{
				return;
			}
			List<Change> changes = new List<Change>();
			if (!speed.Same(sb.Committed, "10"))
			{
				changes.Add(new Change { K = speed, B = sb, Before = sb.Committed, After = "10" });
			}
			if (MouseAccelKnob.IsOn(pb.Committed))
			{
				changes.Add(new Change { K = precision, B = pb, Before = pb.Committed, After = "0,0,0" });
			}
			if (changes.Count == 0)
			{
				ShowNote("The gaming preset is already in place");
				return;
			}
			ApplyChanges(changes, "fix:gaming:" + Environment.TickCount64, "Gaming preset", journal: true);
		}, "Speed 10, precision off: the pointer follows the mouse 1:1", "Pointer speed 10 and Enhance pointer precision off: the pointer moves exactly as far as the mouse, however fast it moves.", title: "Gaming preset"));
		pg.Add(LinkRow("mouse.properties"));
		return pg.Done();
	}

	// Double-click not working: the speed with its test folder.
	private ScrollViewer FixDoubleClickPage()
	{
		Pg pg = new Pg(this, "fix/doubleclick");
		FixIntro(pg, "fix/doubleclick", "A double-click counts only when both clicks come within this time");
		pg.Header("Double-click speed");
		pg.Add(DoubleClickRow("fix/doubleclick"));
		pg.Footnote("Opens on the second click only when you are slower than the setting: move it towards Slow.");
		pg.Add(KnobRow("mouse.clicklock", "fix/doubleclick"));
		return pg.Done();
	}

	// Typing in the wrong language or doubled letters: layouts and the switch shortcut, repeat delay, Filter Keys.
	private ScrollViewer FixTypingPage()
	{
		Pg pg = new Pg(this, "fix/typing");
		ISettingsWorld w = SettingsWorld.Current;
		FixIntro(pg, "fix/typing", "The input language switches with a shortcut; doubled letters come from the repeat");
		pg.Header("Wrong language");
		FrameworkElement languages = InfoRow("fix.typing.layouts", null, "…", "Input languages");
		pg.Add(languages);
		FrameworkElement hotkey = InfoRow("fix.typing.switch", null, null, "Switch with");
		pg.Add(hotkey);
		ReadAsync(() => (Layouts: w.ReadKeyboardLayouts(), Hotkey: w.ReadLayoutHotkey()), delegate((string[] Layouts, string Hotkey) r)
		{
			SetInfo(languages, r.Layouts.Length == 1 ? "1 layout" : r.Layouts.Length + " layouts");
			if (languages is Grid g && FindSecondary(g) is TextBlock line)
			{
				line.Text = r.Layouts.Length == 0 ? "None found" : string.Join(" · ", r.Layouts);
			}
			SetInfo(hotkey, r.Hotkey);
		});
		pg.Add(LinkRow("keyboard.region"));
		pg.Header("Doubled or missing letters");
		pg.Add(KnobRow("keyboard.delay", "fix/typing"));
		pg.Add(KnobRow("access.filter", "fix/typing"));
		pg.Footnote("A longer repeat delay stops a key held a little too long from typing twice; Filter Keys ignores very short presses.");
		return pg.Done();
	}

	// Screen too bright or eyes tired: brightness per monitor, Dark rest, Night light.
	private ScrollViewer FixScreenPage()
	{
		Pg pg = new Pg(this, "fix/screen");
		FixIntro(pg, "fix/screen", "Lower the brightness, darken the look or warm the colours in the evening");
		pg.Header("Brightness");
		StackPanel brightness = new StackPanel();
		pg.Add(CustomRow("fix.screen.brightness", brightness, new Thickness(0.0)));
		BrightnessRows(brightness, "fix.screen.brightness", quick: false);
		pg.Header("Easier on the eyes");
		bool rest = DarkRest.IsOn;
		FrameworkElement restRow = ButtonRow("fix.screen.rest", rest ? "On" : "Use", delegate
		{
			if (!DarkRest.IsOn)
			{
				ApplySeason(SeasonalTheme.Rest);
			}
		}, rest ? "Dark rest is on" : "True black for the whole shell", rest ? null : "A true-black look for the whole shell; Normal (under Appearance) brings back what you have now.", title: "Dark rest");
		foreach (Button b in Descendants<Button>(restRow))
		{
			b.IsEnabled = !rest;
		}
		pg.Add(restRow);
		pg.Add(LinkRow("displays.nightlight", "Warmer colours in the evening"));
		pg.Add(LinkRow("displays.textsize"));
		return pg.Done();
	}

	// Screen turns off too soon: the screen (and sleep) timeouts of the active plan.
	private ScrollViewer FixTimeoutPage()
	{
		Pg pg = new Pg(this, "fix/timeout");
		FixIntro(pg, "fix/timeout", "The screen turns off after this long without input");
		pg.Header("Screen & sleep");
		bool editable = _caps == null || _caps.PageAvailable("powersleep") || _caps.CplAllowed("powercfg.cpl");
		if (editable)
		{
			pg.Add(KnobRow("power.screen", "fix/timeout"));
			pg.Add(KnobRow("power.sleep", "fix/timeout"));
		}
		else
		{
			pg.Add(InfoRow("fix.timeout.managed", "Managed by your organisation", title: "Turn off screen after"));
		}
		pg.Add(LinkRow("power.options"));
		PolicyFootnote(pg);
		return pg.Done();
	}
}
