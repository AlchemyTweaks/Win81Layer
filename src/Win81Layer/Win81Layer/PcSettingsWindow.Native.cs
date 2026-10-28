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
using System.Windows.Media.Animation;
using System.Windows.Threading;

#nullable enable annotations

namespace Win81Layer;

// The native controls of PC Settings: a row bound to a knob reads Windows' value first ("…" until it arrives), applies a
// change live and persists it once on the SettingsWorker thread, reads it back (a value Windows did not take is put back
// with an inline error), journals it and offers Undo (the undo bar, Ctrl+Z, Recent changes, "Undo my changes" per page).
// "Windows defaults…" shows the differences first and applies them as one group; the primary mouse button asks to keep
// the change and reverts after 10 s.
public sealed partial class PcSettingsWindow
{
	// One native row: its knob, the value Windows has, the control and its inline note.
	internal sealed class KnobBinding
	{
		internal HubKnob Knob = null!;

		internal string RowId = "";

		internal string Page = "";

		internal string Title = "";

		// The value Windows has (the last read or committed one); null until it was read.
		internal string? Committed;

		internal FrameworkElement Row = null!;

		internal Action<string?> Show = delegate { };

		internal Action<bool> Enable = delegate { };

		internal TextBlock Note = null!;

		internal bool ReadOnly;

		internal Func<string?, string>? Text;

		// A slider: the value applied live and whether the thumb is being dragged.
		internal Slider? Slider;

		internal string? LiveValue;

		internal bool Dragging;

		// The 5 DIP accent dot after the title: the value differs from the documented Windows default.
		internal FrameworkElement? Dot;

		internal bool Modified => Knob.WindowsDefault != null && Committed != null && !Knob.Same(Committed, Knob.WindowsDefault);

		internal string Display(string? v) => Text != null ? Text(v) : Knob.Format(v);
	}

	private readonly Dictionary<string, KnobBinding> _bindings = new Dictionary<string, KnobBinding>(StringComparer.OrdinalIgnoreCase);

	// The value of each row before its first change in this window session ("Undo my changes").
	private readonly Dictionary<string, (HubKnob Knob, string Before, string Page)> _sessionBefore = new Dictionary<string, (HubKnob, string, string)>(StringComparer.OrdinalIgnoreCase);

	private int _inFlight;

	// Undo puts a launcher row or a look back through its own control: no new journal entry for that.
	private int _journalSuppress;

	// Launcher rows journaled through their own control: row id -> sets the control to a value label.
	private readonly Dictionary<string, Action<string>> _uiSetters = new Dictionary<string, Action<string>>(StringComparer.OrdinalIgnoreCase);

	internal KnobBinding? QaBinding(string rowId) => _bindings.TryGetValue(rowId, out KnobBinding? b) ? b : null;

	// ---- Building bound rows ------------------------------------------------------------------------------------------

	private KnobBinding Bind(string rowId, HubKnob knob, string? title, string page)
	{
		KnobBinding b = new KnobBinding
		{
			Knob = knob,
			RowId = rowId,
			Page = page,
			Title = title ?? HubCatalog.Title(rowId)
		};
		_bindings[rowId] = b;
		return b;
	}

	// The row and its inline note (an error or "Managed by policy" under it), registered as one element.
	private FrameworkElement Wrap(KnobBinding b, FrameworkElement row)
	{
		StackPanel sp = new StackPanel();
		sp.Children.Add(row);
		b.Note = InlineNote(string.Empty, error: true);
		b.Note.Visibility = Visibility.Collapsed;
		sp.Children.Add(b.Note);
		b.Row = sp;
		if (b.Knob.WindowsDefault != null && LabelLine(row) is StackPanel line)
		{
			// the Modified dot follows every value the row shows (read, changed, put back, undone)
			b.Dot = ModifiedDot(b);
			line.Children.Insert(Math.Min(1, line.Children.Count), b.Dot);
			Action<string?> show = b.Show;
			b.Show = delegate(string? v)
			{
				show(v);
				UpdateModified(b);
			};
		}
		Register(b.RowId, sp);
		return sp;
	}

	// The horizontal line of a row's title (title, Modified dot, info button, favourite star), or null.
	private static StackPanel? LabelLine(FrameworkElement? row)
	{
		Grid? shell = row as Grid ?? (row is StackPanel { Children.Count: > 0 } sp ? sp.Children[0] as Grid : null);
		if (shell != null && shell.Children.Count > 0 && shell.Children[0] is StackPanel { Children.Count: > 0 } text && text.Children[0] is StackPanel line)
		{
			return line;
		}
		return null;
	}

	// ---- Modified dots and "Show only changed" -----------------------------------------------------------------------------

	private FrameworkElement ModifiedDot(KnobBinding b)
	{
		System.Windows.Shapes.Ellipse dot = new System.Windows.Shapes.Ellipse
		{
			Width = 5.0,
			Height = 5.0,
			Fill = _pal.ToggleOn,
			Margin = new Thickness(6.0, 1.0, 0.0, 0.0),
			VerticalAlignment = VerticalAlignment.Center,
			Visibility = Visibility.Collapsed,
			SnapsToDevicePixels = true
		};
		ToolTipService.SetInitialShowDelay(dot, 250);
		return dot;
	}

	// The dot shows while Windows' value differs from its documented default; the row says so to UI Automation too.
	private void UpdateModified(KnobBinding b)
	{
		if (b.Dot == null)
		{
			return;
		}
		bool on = b.Modified;
		b.Dot.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
		b.Dot.ToolTip = on ? "Changed from the Windows default (" + b.Display(b.Knob.WindowsDefault) + ")" : null;
		AutomationProperties.SetItemStatus(b.Row, on ? "Changed from the Windows default" : string.Empty);
		if (_changedOnly && _changedPage == _current)
		{
			ApplyChangedFilter();
		}
	}

	// The rows of the page shown with a Modified dot (QA, the toolbar's count).
	internal List<string> ModifiedRows(string page) => _bindings.Values.Where((KnobBinding b) => b.Page == page && b.Dot != null && b.Modified).OrderBy((KnobBinding b) => RowOrder(b.RowId)).Select((KnobBinding b) => b.RowId).ToList();

	// Pages whose rows have documented Windows defaults: the toolbar offers "Show only changed" there.
	private static readonly HashSet<string> ChangedFilterPages = new HashSet<string>(StringComparer.Ordinal) { "mouse", "keyboard", "accessibility", "apps" };

	private bool _changedOnly;

	private string _changedPage = "";

	private readonly List<UIElement> _filterHidden = new List<UIElement>();

	private UIElement? _filterNote;

	internal bool QaChangedOnly => _changedOnly;

	// "Show only changed" (the toolbar): only the rows that differ from the Windows default stay on the page; headers and cards
	// with none of them fold away. Off (or another page) shows everything again.
	internal void SetChangedOnly(bool on)
	{
		_changedOnly = on;
		if (_changedToggle != null && _changedToggle.IsChecked != on)
		{
			_quietChanged = true;
			_changedToggle.IsChecked = on;
			_quietChanged = false;
		}
		ApplyChangedFilter();
	}

	private void ApplyChangedFilter()
	{
		foreach (UIElement e in _filterHidden)
		{
			e.Visibility = Visibility.Visible;
		}
		_filterHidden.Clear();
		if (_filterNote is FrameworkElement { Parent: Panel host })
		{
			host.Children.Remove(_filterNote);
		}
		_filterNote = null;
		_changedPage = _current;
		if (!_changedOnly || !ChangedFilterPages.Contains(_current) || _content.Child is not ScrollViewer { Content: Border { Child: StackPanel column } })
		{
			return;
		}
		HashSet<FrameworkElement> keep = new HashSet<FrameworkElement>(_bindings.Values.Where((KnobBinding b) => b.Page == _current && b.Dot != null && b.Modified).Select((KnobBinding b) => b.Row));
		void Hide(UIElement e)
		{
			if (e.Visibility == Visibility.Visible)
			{
				e.Visibility = Visibility.Collapsed;
				_filterHidden.Add(e);
			}
		}
		UIElement? pendingHeader = null;
		foreach (UIElement child in column.Children.Cast<UIElement>().ToList())
		{
			if (child is TextBlock tb && tb.FontFamily == SegoeUiSemibold)
			{
				if (pendingHeader != null)
				{
					Hide(pendingHeader);
				}
				pendingHeader = child;
				continue;
			}
			if (child is Border { Child: StackPanel card } border && border.BorderThickness.Left > 0.0)
			{
				bool any = false;
				foreach (UIElement row in card.Children.Cast<UIElement>().ToList())
				{
					bool separator = row is Border { Height: 1.0, Child: null };
					if (separator)
					{
						continue;
					}
					if (row is FrameworkElement fe && keep.Contains(fe))
					{
						any = true;
					}
					else
					{
						Hide(row);
					}
				}
				// a separator stays only between two shown rows
				bool seen = false;
				foreach (UIElement row in card.Children.Cast<UIElement>().ToList())
				{
					if (row is Border { Height: 1.0, Child: null } sep)
					{
						int at = card.Children.IndexOf(sep);
						UIElement? next = at + 1 < card.Children.Count ? card.Children[at + 1] : null;
						if (!seen || next == null || next.Visibility != Visibility.Visible)
						{
							Hide(sep);
						}
						continue;
					}
					seen |= row.Visibility == Visibility.Visible;
				}
				if (!any)
				{
					Hide(border);
					if (pendingHeader != null)
					{
						Hide(pendingHeader);
					}
				}
				pendingHeader = null;
				continue;
			}
			// everything outside the cards (hero, footnotes, previews) folds away too
			if (pendingHeader != null)
			{
				Hide(pendingHeader);
				pendingHeader = null;
			}
			Hide(child);
		}
		if (pendingHeader != null)
		{
			Hide(pendingHeader);
		}
		if (keep.Count == 0)
		{
			_filterNote = new TextBlock
			{
				Text = "Everything on this page is at the Windows default.",
				FontFamily = SegoeUi,
				FontSize = 13.5,
				Foreground = _pal.Ink2,
				TextWrapping = TextWrapping.Wrap,
				Margin = new Thickness(12.0, 8.0, 12.0, 0.0)
			};
			column.Children.Insert(0, _filterNote);
		}
	}

	private void SetNote(KnobBinding b, string? text, bool error = true)
	{
		if (b.Note == null)
		{
			return;
		}
		b.Note.Text = text ?? string.Empty;
		b.Note.Foreground = error ? _pal.Error : _pal.Ink2;
		b.Note.Visibility = string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;
	}

	// Reads the knob's value (off the UI thread for slow knobs) and puts it in the control; nothing is ever assumed.
	private void ReadInto(KnobBinding b, Func<string?>? readOverride = null)
	{
		ISettingsWorld w = SettingsWorld.Current;
		HubKnob k = b.Knob;
		b.Enable(false);
		if (k.Slow || readOverride != null)
		{
			Func<string?> read = readOverride ?? (() => k.Read(w));
			ReadAsync(read, delegate(string? v) { Arrive(b, v); });
			return;
		}
		string? value;
		try
		{
			value = k.Read(w);
		}
		catch
		{
			value = null;
		}
		Arrive(b, value);
	}

	private void Arrive(KnobBinding b, string? v)
	{
		b.Committed = v;
		b.Show(v);
		b.Enable(v != null && !b.ReadOnly);
	}

	private static bool DefaultOn(string? v) => v != null && v != "0" && v.Length > 0;

	// A toggle bound to a knob (Windows' value: on/off from isOn; a flip asks valueFor for the value to apply).
	private FrameworkElement NToggle(string rowId, string? help = null, string? secondary = null, string? title = null, HubKnob? knob = null, Func<string?, bool>? isOn = null, Func<bool, string?, string>? valueFor = null, string? page = null, bool read = true)
	{
		HubKnob k = knob ?? HubKnobs.Get(rowId) ?? throw new ArgumentException("no knob " + rowId);
		KnobBinding b = Bind(rowId, k, title, page ?? PageOfRow(rowId));
		Func<string?, bool> on = isOn ?? DefaultOn;
		Func<bool, string?, string> value = valueFor ?? ((bool x, string? cur) => x ? "1" : "0");
		ToggleButton t = CompactToggle(false, delegate(bool x)
		{
			if (b.Committed != null)
			{
				Commit(b, value(x, b.Committed));
			}
		});
		Grid row = RowShell(b.Title, secondary, help, t, out _);
		b.Show = delegate(string? v) { SetQuiet(t, v != null && on(v)); };
		b.Enable = delegate(bool e) { t.IsEnabled = e; };
		FrameworkElement wrap = Wrap(b, row);
		if (read)
		{
			ReadInto(b);
		}
		return wrap;
	}

	// A drop-down bound to a knob. options(current) gives (value, label) pairs; the current value is added when it is a
	// custom one, so no row ever shows an assumed value.
	private FrameworkElement NChoice(string rowId, Func<string?, (string Value, string Label)[]> options, string? help = null, string? secondary = null, string? title = null, HubKnob? knob = null, double width = 180.0, string? page = null, Action<string>? special = null, bool read = true)
	{
		HubKnob k = knob ?? HubKnobs.Get(rowId) ?? throw new ArgumentException("no knob " + rowId);
		KnobBinding b = Bind(rowId, k, title, page ?? PageOfRow(rowId));
		(string Value, string Label)[] current = Array.Empty<(string, string)>();
		bool quiet = true;
		ComboBox? combo = null;
		ComboBox c = Combo(b.Title, new[] { "…" }, 0, delegate(int i)
		{
			if (quiet || i < 0 || i >= current.Length || b.Committed == null)
			{
				return;
			}
			string v = current[i].Value;
			if (special != null && v.StartsWith("special:", StringComparison.Ordinal))
			{
				quiet = true;
				int back = Array.FindIndex(current, ((string Value, string Label) o) => k.Same(o.Value, b.Committed));
				if (combo != null)
				{
					combo.SelectedIndex = back;
				}
				quiet = false;
				special(v);
				return;
			}
			Commit(b, v);
		}, width);
		combo = c;
		Grid row = RowShell(b.Title, secondary, help, c, out _);
		b.Show = delegate(string? v)
		{
			quiet = true;
			try
			{
				if (v == null)
				{
					c.ItemsSource = new[] { "…" };
					c.SelectedIndex = 0;
					return;
				}
				List<(string Value, string Label)> list = options(v).ToList();
				if (!list.Any(((string Value, string Label) o) => k.Same(o.Value, v)))
				{
					list.Insert(0, (v, b.Display(v)));
				}
				current = list.ToArray();
				c.ItemsSource = current.Select(((string Value, string Label) o) => o.Label).ToArray();
				c.SelectedIndex = Array.FindIndex(current, ((string Value, string Label) o) => k.Same(o.Value, v));
			}
			finally
			{
				quiet = false;
			}
		};
		b.Enable = delegate(bool e) { c.IsEnabled = e; };
		FrameworkElement wrap = Wrap(b, row);
		if (read)
		{
			ReadInto(b);
		}
		return wrap;
	}

	// A slider bound to a knob: live while dragging (throttled, not persisted), persisted once on release or 250 ms after
	// the last keyboard step; the value is on the second line.
	private FrameworkElement NSlider(string rowId, int min, int max, string? low = null, string? high = null, string? help = null, string? title = null, HubKnob? knob = null, Func<string?, int>? toPos = null, Func<int, string>? fromPos = null, int large = 0, string? page = null, bool read = true, int step = 1)
	{
		HubKnob k = knob ?? HubKnobs.Get(rowId) ?? throw new ArgumentException("no knob " + rowId);
		KnobBinding b = Bind(rowId, k, title, page ?? PageOfRow(rowId));
		Func<string?, int> pos = toPos ?? ((string? v) => int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out int x) ? x : min);
		Func<int, string> val = fromPos ?? ((int x) => x.ToString(CultureInfo.InvariantCulture));
		Slider s = new Slider
		{
			Minimum = min,
			Maximum = max,
			Value = min,
			Width = low != null && high != null ? 160.0 : 200.0,
			IsSnapToTickEnabled = true,
			TickFrequency = step,
			SmallChange = step,
			LargeChange = large > 0 ? large : Math.Max(step, Math.Round((max - min) / 10.0 / step) * step),
			VerticalAlignment = VerticalAlignment.Center,
			MinHeight = 28.0
		};
		s.SetResourceReference(FrameworkElement.StyleProperty, "Metro81.Slider");
		StackPanel control = new StackPanel { Orientation = Orientation.Horizontal };
		if (low != null)
		{
			control.Children.Add(EndLabel(low, new Thickness(0.0, 0.0, 6.0, 0.0)));
		}
		control.Children.Add(s);
		if (high != null)
		{
			control.Children.Add(EndLabel(high, new Thickness(6.0, 0.0, 0.0, 0.0)));
		}
		Grid row = RowShell(b.Title, "…", help, control, out _);
		AutomationProperties.SetName(s, b.Title);
		TextBlock? valueText = FindSecondary(row);
		bool quiet = true;
		b.Slider = s;
		b.Show = delegate(string? v)
		{
			quiet = true;
			try
			{
				if (v != null)
				{
					int p = pos(v);
					if (p > s.Maximum)
					{
						s.Maximum = p;
					}
					if (p < s.Minimum)
					{
						s.Minimum = p;
					}
					s.Value = p;
				}
				if (valueText != null)
				{
					valueText.Text = b.Display(v);
				}
			}
			finally
			{
				quiet = false;
			}
		};
		b.Enable = delegate(bool e) { s.IsEnabled = e; };
		s.ValueChanged += delegate
		{
			if (quiet || b.Committed == null)
			{
				return;
			}
			string v = val((int)Math.Round(s.Value));
			if (valueText != null)
			{
				valueText.Text = b.Display(v);
			}
			SliderMoved(b, v);
		};
		s.AddHandler(System.Windows.Controls.Primitives.Thumb.DragStartedEvent, (DragStartedEventHandler)delegate { b.Dragging = true; });
		s.AddHandler(System.Windows.Controls.Primitives.Thumb.DragCompletedEvent, (DragCompletedEventHandler)delegate
		{
			b.Dragging = false;
			SliderReleased(b);
		});
		FrameworkElement wrap = Wrap(b, row);
		if (read)
		{
			ReadInto(b);
		}
		return wrap;
	}

	// The page a row belongs to (from the catalog; "home" rows of the Quick card name their own page).
	private string PageOfRow(string rowId) => HubCatalog.Row(rowId)?.Page ?? (_current.Length > 0 ? _current : "home");

	// ---- Live, persist, read back -------------------------------------------------------------------------------------

	private DispatcherTimer? _persistTimer;

	private KnobBinding? _persistPending;

	private void SliderMoved(KnobBinding b, string v)
	{
		LiveApply(b, v);
		if (!b.Dragging)
		{
			// a keyboard step or a click on the track: persisted 250 ms after the last one
			if (_persistPending != null && !ReferenceEquals(_persistPending, b))
			{
				KnobBinding other = _persistPending;
				_persistPending = null;
				if (other.LiveValue != null)
				{
					Commit(other, other.LiveValue);
				}
			}
			_persistPending = b;
			if (_persistTimer == null)
			{
				_persistTimer = new DispatcherTimer(DispatcherPriority.Background, base.Dispatcher)
				{
					Interval = TimeSpan.FromMilliseconds(250)
				};
				_persistTimer.Tick += delegate
				{
					_persistTimer!.Stop();
					KnobBinding? p = _persistPending;
					_persistPending = null;
					if (p?.LiveValue != null)
					{
						Commit(p, p.LiveValue);
					}
				};
			}
			_persistTimer.Stop();
			_persistTimer.Start();
		}
	}

	private void SliderReleased(KnobBinding b)
	{
		if (ReferenceEquals(_persistPending, b))
		{
			_persistTimer?.Stop();
			_persistPending = null;
		}
		if (b.LiveValue != null)
		{
			Commit(b, b.LiveValue);
		}
	}

	// A live write (ini 0, no broadcast) on the worker, throttled to one per 60 ms; the latest value wins.
	private void LiveApply(KnobBinding b, string v)
	{
		b.LiveValue = v;
		ISettingsWorld w = SettingsWorld.Current;
		HubKnob k = b.Knob;
		SettingsWorker.Throttle("live:" + b.RowId, delegate { k.Apply(w, v, persist: false); });
	}

	private sealed class Change
	{
		internal HubKnob K = null!;

		internal KnobBinding? B;

		internal string Before = "";

		internal string After = "";

		internal int Code = -1;

		internal string? Back;

		internal string? Context;

		// An undo, a redo or a revert of a journaled value: the context the journal recorded (the power scheme the value
		// was changed in), so the value goes back where it was changed even when another scheme is active now.
		internal string? TargetContext;

		internal bool Ok => Code == 0 && Back != null && K.Same(Back, After);
	}

	// One change of one row (the user's click, the end of a drag).
	private void Commit(KnobBinding b, string after)
	{
		string? before = b.Committed;
		if (before == null || b.ReadOnly)
		{
			b.Show(before);
			return;
		}
		if (b.Knob.Same(before, after))
		{
			// a drag that ended where it began: nothing to persist or journal
			b.LiveValue = null;
			return;
		}
		_sessionBefore.TryAdd(b.RowId, (b.Knob, before, b.Page));
		b.Committed = after;
		ApplyChanges(new List<Change> { new Change { K = b.Knob, B = b, Before = before, After = after } }, null, null, journal: true);
	}

	// Applies changes in order on the worker (persisted), reads each one back and puts back what Windows did not take;
	// then journals the ones that held (as one group when group is set) and offers Undo.
	private void ApplyChanges(List<Change> changes, string? group, string? groupTitle, bool journal, Action<List<Change>>? done = null)
	{
		if (changes.Count == 0)
		{
			done?.Invoke(changes);
			return;
		}
		ISettingsWorld w = SettingsWorld.Current;
		_inFlight++;
		SettingsWorker.Run(delegate
		{
			foreach (Change c in changes)
			{
				try
				{
					c.Context = c.TargetContext ?? c.K.Context(w);
					c.Code = c.K.ApplyIn(w, c.After, persist: true, c.TargetContext);
					c.Back = c.Code == 0 ? c.K.ReadIn(w, c.TargetContext) : null;
				}
				catch (Exception ex)
				{
					Logger.Log("PC settings: " + c.K.RowId + ": " + ex.Message);
					c.Code = 1;
				}
				if (!c.Ok && c.Code != 5)
				{
					try
					{
						c.K.ApplyIn(w, c.Before, persist: true, c.TargetContext);
					}
					catch
					{
					}
				}
			}
			try
			{
				base.Dispatcher.BeginInvoke(DispatcherPriority.Normal, (Action)delegate
				{
					_inFlight = Math.Max(0, _inFlight - 1);
					Finish(changes, group, groupTitle, journal);
					done?.Invoke(changes);
				});
			}
			catch
			{
			}
		});
	}

	private void Finish(List<Change> changes, string? group, string? groupTitle, bool journal)
	{
		List<JournalEntry> added = new List<JournalEntry>();
		DateTime now = SettingsWorld.Current.UtcNow;
		foreach (Change c in changes)
		{
			KnobBinding? b = c.B != null && _bindings.TryGetValue(c.B.RowId, out KnobBinding? live) && ReferenceEquals(live, c.B) ? c.B : (_bindings.TryGetValue(c.K.RowId, out KnobBinding? other) && ReferenceEquals(other.Knob, c.K) ? other : null);
			if (c.Ok)
			{
				if (c.TargetContext != null)
				{
					// written to the scheme the journal named: the rows show the active scheme, so they read it again
					foreach (KnobBinding x in _bindings.Values.Where((KnobBinding x) => ReferenceEquals(x.Knob, c.K) && !x.Dragging).ToList())
					{
						x.LiveValue = null;
						SetNote(x, null);
						ReadInto(x);
					}
				}
				else
				{
					if (b != null)
					{
						b.Committed = c.Back;
						b.LiveValue = null;
						b.Show(c.Back);
						SetNote(b, null);
					}
					// another row of the page for the same setting (Home's Quick card and a favourite) follows
					foreach (KnobBinding twin in _bindings.Values.Where((KnobBinding x) => !ReferenceEquals(x, b) && ReferenceEquals(x.Knob, c.K) && !x.Dragging).ToList())
					{
						twin.Committed = c.Back;
						twin.LiveValue = null;
						twin.Show(c.Back);
					}
				}
				if (c.K.RowId == "sound.output" && _bindings.TryGetValue("sound.calls", out KnobBinding? calls))
				{
					// "Also use for calls" compares with the output device
					calls.Show(calls.Committed);
				}
				if (c.K is PowerPlanKnob)
				{
					// another plan: every screen, sleep and button row shows that plan's values (and changes them there)
					foreach (KnobBinding x in _bindings.Values.Where((KnobBinding x) => x.Knob is PowerKnob && !x.Dragging).ToList())
					{
						x.LiveValue = null;
						ReadInto(x);
					}
				}
				// the wheel's mode and its line count are one Windows value: each row reads it again after the other changed
				string? partner = c.K.RowId switch
				{
					"mouse.wheel" => "mouse.lines",
					"mouse.lines" => "mouse.wheel",
					_ => null
				};
				if (partner != null && _bindings.TryGetValue(partner, out KnobBinding? pb) && !pb.Dragging)
				{
					pb.LiveValue = null;
					ReadInto(pb);
				}
				if (journal && c.K.Journal)
				{
					JournalEntry? e = SettingsJournal.Add(new JournalEntry
					{
						Id = c.K.RowId,
						Title = b?.Title ?? c.K.Title,
						Before = c.Before,
						After = c.After,
						BeforeText = (b != null ? b.Display(c.Before) : c.K.Format(c.Before)),
						AfterText = (b != null ? b.Display(c.After) : c.K.Format(c.After)),
						Group = group,
						GroupTitle = groupTitle,
						Context = c.Context,
						Kind = "knob"
					}, now);
					if (e != null)
					{
						added.Add(e);
						_sessionEntries.Add(e);
					}
				}
				continue;
			}
			if (b != null)
			{
				if (c.TargetContext != null)
				{
					// a value of another scheme was put back: the row reads what its own (active) scheme has
					b.LiveValue = null;
					ReadInto(b);
				}
				else
				{
					b.Committed = c.Before;
					b.LiveValue = null;
					b.Show(c.Before);
				}
				if (c.Code == 5)
				{
					b.ReadOnly = true;
					b.Enable(false);
					SetNote(b, "Managed by your organisation", error: false);
				}
				else
				{
					SetNote(b, "Windows did not accept this value");
				}
			}
		}
		RefreshPageUndo();
		if (added.Count == 0)
		{
			return;
		}
		string barText = group != null ? (groupTitle ?? "Changes") + " · " + added.Count + (added.Count == 1 ? " setting" : " settings") : added[^1].Line;
		// the primary mouse button asks to be kept, alone or in a group (Windows defaults, Undo my changes, a Setup)
		Change? guarded = changes.FirstOrDefault((Change c) => c.Ok && c.K.Risk == KnobRisk.KeepGuard);
		if (guarded != null)
		{
			JournalEntry? guardedEntry = added.FirstOrDefault((JournalEntry e) => e.Id == guarded.K.RowId);
			if (_closed)
			{
				// the window closed before Windows answered: nobody can keep the change, so it goes back now
				_guardChange = guarded;
				_guardEntry = guardedEntry;
				GuardRevert();
				return;
			}
			ShowGuard(guarded, guardedEntry, barText, added[^1]);
			return;
		}
		ShowUndoBar(barText, added[^1]);
	}

	// ---- Launcher rows journaled through their own control -------------------------------------------------------------

	private static readonly HashSet<string> UiJournalPages = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
	{
		"appearance", "appearance/pointers", "appearance/frames", "appearance/seasonal", "appearance/rest", "taskbar", "start", "launcher", "launcher/experiments", "launcher/diagnostics"
	};

	// A launcher row (toggle, choice, segments) records its change with the labels it shows; Undo sets the control back
	// (the same apply path as a click).
	private bool UiJournaled(string? id)
	{
		if (string.IsNullOrEmpty(id) || HubKnobs.Get(id!) != null || id == "home.look" || id == "appearance.look")
		{
			return false;
		}
		HubRow? r = HubCatalog.Row(id!);
		return r != null && (UiJournalPages.Contains(r.Page) || id == "power.autolock" || id == "sound.win81");
	}

	private void JournalUi(string id, string title, string before, string after)
	{
		if (_journalSuppress > 0 || before == after)
		{
			return;
		}
		JournalEntry? e = SettingsJournal.Add(new JournalEntry
		{
			Id = id,
			Title = title,
			Before = before,
			After = after,
			BeforeText = before,
			AfterText = after,
			Kind = "ui"
		}, SettingsWorld.Current.UtcNow);
		if (e != null)
		{
			_sessionEntries.Add(e);
			ShowUndoBar(e.Line, e);
		}
	}

	// A look preset changed (Normal, a season, Dark rest): undone through the preset's own apply path.
	private void JournalLook(string before, string after)
	{
		if (_journalSuppress > 0 || before == after)
		{
			return;
		}
		string Name(string id) => id.Length == 0 ? "Normal" : SeasonalTheme.Find(id)?.Name ?? id;
		JournalEntry? e = SettingsJournal.Add(new JournalEntry
		{
			Id = "appearance.look",
			Title = "Look",
			Before = before,
			After = after,
			BeforeText = Name(before),
			AfterText = Name(after),
			Kind = "look"
		}, SettingsWorld.Current.UtcNow);
		if (e != null)
		{
			_sessionEntries.Add(e);
			ShowUndoBar(e.Line, e);
		}
	}

	// ---- Undo ---------------------------------------------------------------------------------------------------------

	private string _undoState = "none";

	internal string QaUndoState => _undoState;

	// The journal entries this window made (changes, groups, launcher rows, looks): Ctrl+Z undoes only these. Older
	// entries (an earlier session, up to 14 days back) are undone from Recent changes or an undo bar, never by a key press.
	private readonly HashSet<JournalEntry> _sessionEntries = new HashSet<JournalEntry>();

	// Launcher rows whose Undo changes how the shell starts or runs: Undo always asks first.
	private static readonly HashSet<string> UndoAsks = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
	{
		"launcher.logon", "launcher.dominant", "launcher.taskbar", "launcher.suspend"
	};

	// Undoes the newest change made in this window (Ctrl+Z).
	internal void UndoLast()
	{
		JournalEntry? e = SettingsJournal.Snapshot().FirstOrDefault((JournalEntry x) => _sessionEntries.Contains(x));
		if (e == null)
		{
			_undoState = "none";
			if (SettingsJournal.Count > 0)
			{
				ShowUndoBar("Nothing changed in this window yet. Earlier changes are under Recent changes.", null);
			}
			return;
		}
		UndoEntry(e);
	}

	// Undoes an entry (with its group). The current values are read first: when something else changed a row since,
	// the undo bar asks "Set back to X anyway?" (force skips that). A launcher row that decides how the shell starts asks
	// first in any case. A successful undo says so in the undo bar, with Redo.
	internal void UndoEntry(JournalEntry e, bool force = false)
	{
		List<JournalEntry> group = SettingsJournal.GroupOf(e);
		if (group.Count == 0)
		{
			_undoState = "none";
			return;
		}
		JournalEntry? asks = group.FirstOrDefault((JournalEntry x) => x.Kind == "ui" && UndoAsks.Contains(x.Id));
		if (asks != null && !force)
		{
			_undoState = "confirm";
			ShowUndoConfirm(e, "Set “" + asks.Title + "” back to " + asks.BeforeText + "? It changes how the launcher starts or runs.");
			return;
		}
		HideUndoBar();
		List<JournalEntry> other = group.Where((JournalEntry x) => x.Kind != "knob").ToList();
		foreach (JournalEntry x in other)
		{
			OwnPath(x, x.Before, journal: false);
		}
		List<JournalEntry> knobs = group.Where((JournalEntry x) => x.Kind == "knob").ToList();
		if (knobs.Count == 0)
		{
			SettingsJournal.Remove(other);
			_undoState = "done";
			ShowUndone(group);
			return;
		}
		ISettingsWorld w = SettingsWorld.Current;
		_undoState = "pending";
		_inFlight++;
		SettingsWorker.Run(delegate
		{
			List<(JournalEntry E, HubKnob? K, string? Cur)> now = new List<(JournalEntry, HubKnob?, string?)>();
			foreach (JournalEntry x in knobs)
			{
				HubKnob? k = HubKnobs.Get(x.Id);
				string? cur = null;
				try
				{
					// a power value is read in the scheme it was changed in
					cur = k?.ReadIn(w, x.Context);
				}
				catch
				{
				}
				now.Add((x, k, cur));
			}
			try
			{
				base.Dispatcher.BeginInvoke(DispatcherPriority.Normal, (Action)delegate
				{
					_inFlight = Math.Max(0, _inFlight - 1);
					List<string> changed = now.Where(((JournalEntry E, HubKnob? K, string? Cur) t) => t.K == null || !t.K.Same(t.Cur, t.E.After)).Select(((JournalEntry E, HubKnob? K, string? Cur) t) => t.E.Title).ToList();
					if (changed.Count > 0 && !force)
					{
						_undoState = "confirm";
						JournalEntry first = now.First(((JournalEntry E, HubKnob? K, string? Cur) t) => t.K == null || !t.K.Same(t.Cur, t.E.After)).E;
						ShowUndoConfirm(e, first.Title + " was changed since. Set back to " + first.BeforeText + " anyway?");
						return;
					}
					List<(Change C, JournalEntry E)> pairs = now.Where(((JournalEntry E, HubKnob? K, string? Cur) t) => t.K != null).Reverse().Select(((JournalEntry E, HubKnob? K, string? Cur) t) => (new Change
					{
						K = t.K!,
						B = _bindings.TryGetValue(t.E.Id, out KnobBinding? b) ? b : null,
						Before = t.Cur ?? t.E.After,
						After = t.E.Before,
						TargetContext = t.E.Context
					}, t.E)).ToList();
					// entries without a knob (a row this version no longer has) cannot be set back: they leave the list
					List<JournalEntry> orphans = now.Where(((JournalEntry E, HubKnob? K, string? Cur) t) => t.K == null).Select(((JournalEntry E, HubKnob? K, string? Cur) t) => t.E).ToList();
					ApplyChanges(pairs.Select(((Change C, JournalEntry E) p) => p.C).ToList(), null, null, journal: false, delegate
					{
						// only what was set back leaves the list; a failed one stays there to try again
						List<JournalEntry> undone = pairs.Where(((Change C, JournalEntry E) p) => p.C.Ok).Select(((Change C, JournalEntry E) p) => p.E).ToList();
						SettingsJournal.Remove(undone.Concat(other).Concat(orphans));
						bool all = pairs.All(((Change C, JournalEntry E) p) => p.C.Ok);
						_undoState = all ? "done" : "failed";
						if (all)
						{
							ShowUndone(undone.Concat(other).ToList());
						}
						else
						{
							ShowUndoBar("Some settings could not be set back; they stay in Recent changes", null);
						}
					});
				});
			}
			catch
			{
			}
		});
	}

	// "Undone: Pointer speed 14 → 20" with Redo (the undone values applied again, journaled as a new change).
	private void ShowUndone(List<JournalEntry> undone)
	{
		if (undone.Count == 0 || _closed)
		{
			return;
		}
		JournalEntry first = undone[0];
		string text = undone.Count == 1 && first.Group == null ? "Undone: " + first.Title + "  " + first.AfterText + " → " + first.BeforeText : "Undone: " + (first.GroupTitle ?? first.Title) + " · " + undone.Count + (undone.Count == 1 ? " setting" : " settings");
		EnsureUndoBar();
		_undoBar!.Background = _pal.Card;
		_undoBar.BorderBrush = _pal.CardBorder;
		_undoText!.Foreground = _pal.Ink;
		_undoText.Text = text;
		_undoButtons!.Children.Clear();
		List<JournalEntry> again = undone.ToList();
		_undoButtons.Children.Add(UndoLinkButton("Redo", delegate { Redo(again); }));
		RevealUndoBar(6.0);
	}

	// Applies the undone values again (as one group when they were one), through the same paths as a change.
	internal void Redo(List<JournalEntry> undone)
	{
		HideUndoBar();
		foreach (JournalEntry x in undone.Where((JournalEntry x) => x.Kind != "knob"))
		{
			OwnPath(x, x.After, journal: true);
		}
		List<Change> changes = new List<Change>();
		foreach (JournalEntry x in undone.Where((JournalEntry x) => x.Kind == "knob"))
		{
			HubKnob? k = HubKnobs.Get(x.Id);
			if (k != null)
			{
				changes.Add(new Change { K = k, B = _bindings.TryGetValue(x.Id, out KnobBinding? b) ? b : null, Before = x.Before, After = x.After, TargetContext = x.Context });
			}
		}
		bool grouped = changes.Count > 1 || undone.Any((JournalEntry x) => x.Group != null);
		ApplyChanges(changes, grouped ? "redo:" + Environment.TickCount64 : null, grouped ? undone[0].GroupTitle ?? "Redo" : null, journal: true);
	}

	// A launcher row through its control (navigating to its page when it is not shown), a look through ApplySeason; an
	// undo sets the value before (not journaled), a redo the value after (journaled like the user's own click).
	private void OwnPath(JournalEntry x, string value, bool journal)
	{
		if (!journal)
		{
			_journalSuppress++;
		}
		try
		{
			if (x.Kind == "look")
			{
				ApplySeason(value);
				return;
			}
			if (!_uiSetters.ContainsKey(x.Id) && HubCatalog.Row(x.Id) is HubRow row)
			{
				Navigate(row.Page, x.Id, push: true);
			}
			if (_uiSetters.TryGetValue(x.Id, out Action<string>? set))
			{
				set(value);
			}
		}
		catch (Exception ex)
		{
			Logger.Log("PC settings " + (journal ? "redo " : "undo ") + x.Id + ": " + ex.Message);
		}
		finally
		{
			if (!journal)
			{
				_journalSuppress--;
			}
		}
	}

	// ---- Undo bar (6 s, polite live region) ---------------------------------------------------------------------------

	private Border? _undoBar;

	private TextBlock? _undoText;

	private StackPanel? _undoButtons;

	private DispatcherTimer? _undoTimer;

	internal string QaUndoBarText => _undoBar != null && _undoBar.Visibility == Visibility.Visible ? _undoText?.Text ?? "" : "";

	private void EnsureUndoBar()
	{
		if (_undoBar != null)
		{
			return;
		}
		_undoText = new TextBlock
		{
			FontFamily = SegoeUi,
			FontSize = 13.5,
			VerticalAlignment = VerticalAlignment.Center,
			TextTrimming = TextTrimming.CharacterEllipsis,
			Margin = new Thickness(16.0, 0.0, 12.0, 0.0)
		};
		_undoButtons = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0.0, 0.0, 12.0, 0.0) };
		Grid g = new Grid();
		g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.0, GridUnitType.Star) });
		g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
		g.Children.Add(_undoText);
		Grid.SetColumn(_undoButtons, 1);
		g.Children.Add(_undoButtons);
		_undoBar = new Border
		{
			Height = 36.0,
			VerticalAlignment = VerticalAlignment.Bottom,
			BorderThickness = new Thickness(0.0, 1.0, 0.0, 0.0),
			Child = g,
			Visibility = Visibility.Collapsed,
			SnapsToDevicePixels = true
		};
		AutomationProperties.SetLiveSetting(_undoBar, AutomationLiveSetting.Polite);
		AutomationProperties.SetName(_undoBar, "Undo");
		Grid.SetRow(_undoBar, 1);
		_contentHost.Children.Add(_undoBar);
	}

	private Button UndoLinkButton(string text, Action click)
	{
		Button b = new Button
		{
			Template = _pal.RowTemplate,
			Style = BareButton,
			Padding = new Thickness(10.0, 0.0, 10.0, 0.0),
			Height = 28.0,
			MinWidth = 28.0,
			Cursor = Cursors.Hand,
			Content = new TextBlock { Text = text, FontFamily = SegoeUiSemibold, FontSize = 13.5, Foreground = _pal.Link, Margin = new Thickness(10.0, 0.0, 10.0, 0.0), VerticalAlignment = VerticalAlignment.Center }
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
				Logger.Log("PC settings undo bar: " + ex.Message);
			}
		};
		return b;
	}

	private void ShowUndoBar(string text, JournalEntry? entry)
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
		if (entry != null)
		{
			_undoButtons.Children.Add(UndoLinkButton("Undo", delegate { UndoEntry(entry); }));
		}
		RevealUndoBar(6.0);
	}

	private void ShowUndoConfirm(JournalEntry e, string text)
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
		_undoButtons.Children.Add(UndoLinkButton("Set back", delegate { UndoEntry(e, force: true); }));
		_undoButtons.Children.Add(UndoLinkButton("Cancel", HideUndoBar));
		RevealUndoBar(12.0);
	}

	private void RevealUndoBar(double seconds)
	{
		bool wasHidden = _undoBar!.Visibility != Visibility.Visible;
		_undoBar.Visibility = Visibility.Visible;
		if (wasHidden && Motion.Enabled)
		{
			TranslateTransform rise = new TranslateTransform(0.0, Motion.Distance(32.0));
			_undoBar.RenderTransform = rise;
			rise.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(Motion.Distance(32.0), 0.0, Motion.Dur(Motion.Cat.EdgeEnter)) { EasingFunction = Motion.Ease(Motion.Cat.EdgeEnter) });
		}
		if (_undoTimer == null)
		{
			_undoTimer = new DispatcherTimer(DispatcherPriority.Background, base.Dispatcher);
			_undoTimer.Tick += delegate { HideUndoBar(); };
		}
		_undoTimer.Stop();
		_undoTimer.Interval = TimeSpan.FromSeconds(seconds);
		_undoTimer.Start();
	}

	private void HideUndoBar()
	{
		_undoTimer?.Stop();
		if (_undoBar == null || _undoBar.Visibility != Visibility.Visible)
		{
			return;
		}
		if (!Motion.Enabled || _closed)
		{
			_undoBar.Visibility = Visibility.Collapsed;
			return;
		}
		Border bar = _undoBar;
		TranslateTransform t = bar.RenderTransform as TranslateTransform ?? new TranslateTransform();
		bar.RenderTransform = t;
		DoubleAnimation exit = new DoubleAnimation(0.0, Motion.Distance(32.0), Motion.Dur(Motion.Cat.EdgeExit)) { EasingFunction = Motion.Ease(Motion.Cat.EdgeExit) };
		exit.Completed += delegate
		{
			bar.Visibility = Visibility.Collapsed;
			t.BeginAnimation(TranslateTransform.YProperty, null);
		};
		t.BeginAnimation(TranslateTransform.YProperty, exit);
	}

	// ---- Keep this change? (primary mouse button) ---------------------------------------------------------------------

	private Border? _guard;

	private TextBlock? _guardText;

	private DispatcherTimer? _guardTimer;

	private DateTime _guardDeadline;

	private Change? _guardChange;

	private JournalEntry? _guardEntry;

	internal bool QaGuardShown => _guard != null;

	// What the undo bar says once the change is kept (the change alone, or its group).
	private string? _guardBarText;

	private JournalEntry? _guardBarEntry;

	private void ShowGuard(Change c, JournalEntry? entry, string? barText = null, JournalEntry? barEntry = null)
	{
		CloseGuard();
		_guardChange = c;
		_guardEntry = entry;
		_guardBarText = barText;
		_guardBarEntry = barEntry ?? entry;
		_guardDeadline = SettingsWorld.Current.UtcNow.AddSeconds(10.0);
		_guardText = new TextBlock
		{
			Text = "Keep this change? Reverting in 10 s",
			FontFamily = SegoeUi,
			FontSize = 13.5,
			Foreground = _pal.Ink,
			VerticalAlignment = VerticalAlignment.Center,
			TextWrapping = TextWrapping.Wrap,
			Margin = new Thickness(12.0, 0.0, 12.0, 0.0)
		};
		AutomationProperties.SetLiveSetting(_guardText, AutomationLiveSetting.Assertive);
		Button keep = HubButton("Keep", GuardKeep, accent: true);
		Button revert = HubButton("Revert", GuardRevert);
		revert.Margin = new Thickness(8.0, 0.0, 0.0, 0.0);
		// with the buttons swapped, the user's "primary" is the other button: either one keeps the change
		keep.PreviewMouseRightButtonUp += delegate(object s, MouseButtonEventArgs e)
		{
			e.Handled = true;
			GuardKeep();
		};
		StackPanel buttons = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0.0, 0.0, 12.0, 0.0) };
		buttons.Children.Add(keep);
		buttons.Children.Add(revert);
		Grid g = new Grid { MinHeight = 52.0 };
		g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(3.0) });
		g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.0, GridUnitType.Star) });
		g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
		g.Children.Add(new Border { Background = _pal.ToggleOn });
		Grid.SetColumn(_guardText, 1);
		g.Children.Add(_guardText);
		Grid.SetColumn(buttons, 2);
		g.Children.Add(buttons);
		// above the page (not in it): the banner stays while the user moves to another page, so Keep is always at hand
		// while the countdown runs; it lines up with the page's centred column
		_guard = new Border
		{
			Background = _pal.Card,
			BorderBrush = _pal.CardBorder,
			BorderThickness = new Thickness(1.0),
			Child = g,
			MaxWidth = ColumnMax,
			HorizontalAlignment = HorizontalAlignment.Stretch,
			Margin = new Thickness(24.0, 12.0, 24.0, 0.0)
		};
		Grid.SetRow(_guard, 0);
		_contentHost.Children.Add(_guard);
		if (Motion.Enabled)
		{
			_guard.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0.0, 1.0, Motion.Dur(Motion.Cat.Micro)));
		}
		if (_guardTimer == null)
		{
			_guardTimer = new DispatcherTimer(DispatcherPriority.Normal, base.Dispatcher)
			{
				Interval = TimeSpan.FromMilliseconds(250)
			};
			_guardTimer.Tick += delegate { GuardTick(); };
		}
		_guardTimer.Start();
		base.Dispatcher.BeginInvoke(DispatcherPriority.Input, (Action)delegate { keep.Focus(); });
	}

	// The countdown follows the world's clock (QA moves a fake one).
	internal void GuardTick()
	{
		if (_guardChange == null)
		{
			_guardTimer?.Stop();
			return;
		}
		TimeSpan left = _guardDeadline - SettingsWorld.Current.UtcNow;
		if (left <= TimeSpan.Zero)
		{
			GuardRevert();
			return;
		}
		if (_guardText != null)
		{
			_guardText.Text = "Keep this change? Reverting in " + Math.Ceiling(left.TotalSeconds).ToString(CultureInfo.InvariantCulture) + " s";
		}
	}

	internal void GuardKeep()
	{
		CloseGuard();
		if (_guardBarEntry != null)
		{
			ShowUndoBar(_guardBarText ?? _guardBarEntry.Line, _guardBarEntry);
		}
		_guardChange = null;
		_guardEntry = null;
		_guardBarEntry = null;
		_guardBarText = null;
	}

	internal void GuardRevert()
	{
		Change? c = _guardChange;
		JournalEntry? e = _guardEntry;
		CloseGuard();
		_guardChange = null;
		_guardEntry = null;
		_guardBarEntry = null;
		_guardBarText = null;
		if (c == null)
		{
			return;
		}
		KnobBinding? b = _bindings.TryGetValue(c.K.RowId, out KnobBinding? x) ? x : null;
		ApplyChanges(new List<Change> { new Change { K = c.K, B = b, Before = c.After, After = c.Before } }, null, null, journal: false, delegate
		{
			if (e != null)
			{
				SettingsJournal.Remove(new[] { e });
			}
		});
	}

	private void CloseGuard()
	{
		_guardTimer?.Stop();
		if (_guard?.Parent is Panel p)
		{
			p.Children.Remove(_guard);
		}
		_guard = null;
		_guardText = null;
	}

	// ---- Per page: Undo my changes, Windows defaults ------------------------------------------------------------------

	private FrameworkElement? _pageUndoRow;

	private string _pageUndoPage = "";

	// The last card of a native page: "Undo my changes" (when this window changed something here) and, on Mouse,
	// Keyboard, Accessibility and Files & Explorer, "Windows defaults…" with its preview.
	private void ResetCard(Pg pg, string page, bool defaults)
	{
		int at = pg.Column.Children.Count;
		pg.Header("Reset");
		Button undo = ClickRow("Undo my changes", "Set this page back to how it was when you opened PC settings", MenuGlyphs.ChevronPath(_pal.Ink2), delegate { UndoMyChanges(page); });
		_pageUndoRow = undo;
		_pageUndoPage = page;
		pg.Add(undo);
		_resetSection = defaults ? null : pg.Column.Children.Cast<UIElement>().Skip(at).ToArray();
		if (defaults)
		{
			StackPanel preview = new StackPanel { Visibility = Visibility.Collapsed };
			Button open = ClickRow("Windows defaults…", "See what differs, then set it back in one step", MenuGlyphs.ChevronPath(_pal.Ink2), delegate
			{
				ShowDefaultsPreview(page, preview);
			});
			Register(page + ".reset.defaults", open);
			pg.Add(open);
			pg.Add(CustomRow(page + ".reset.preview", preview, new Thickness(0.0)));
			_defaultsPreview = preview;
		}
		RefreshPageUndo();
	}

	private StackPanel? _defaultsPreview;

	// The "Reset" header and card of a page without Windows defaults: shown once this window changed something there.
	private UIElement[]? _resetSection;

	private List<(KnobBinding B, string To)> SessionChanges(string page)
	{
		List<(KnobBinding, string)> list = new List<(KnobBinding, string)>();
		foreach (KeyValuePair<string, (HubKnob Knob, string Before, string Page)> kv in _sessionBefore)
		{
			if (kv.Value.Page == page && _bindings.TryGetValue(kv.Key, out KnobBinding? b) && b.Committed != null && !b.Knob.Same(b.Committed, kv.Value.Before))
			{
				list.Add((b, kv.Value.Before));
			}
		}
		return list;
	}

	private void RefreshPageUndo()
	{
		if (_pageUndoRow == null)
		{
			return;
		}
		bool any = _pageUndoPage == _current && SessionChanges(_pageUndoPage).Count > 0;
		_pageUndoRow.Visibility = any ? Visibility.Visible : Visibility.Collapsed;
		foreach (UIElement e in _resetSection ?? Array.Empty<UIElement>())
		{
			e.Visibility = any ? Visibility.Visible : Visibility.Collapsed;
		}
	}

	private void UndoMyChanges(string page)
	{
		List<Change> changes = SessionChanges(page).Select(((KnobBinding B, string To) x) => new Change { K = x.B.Knob, B = x.B, Before = x.B.Committed!, After = x.To }).ToList();
		ApplyChanges(changes, "session:" + page + ":" + Environment.TickCount64, "Undo my changes", journal: true);
	}

	// The rows of the page shown that differ from their documented Windows default.
	internal List<(KnobBinding B, string To)> DefaultsDiff(string page)
	{
		return _bindings.Values.Where((KnobBinding b) => b.Page == page && b.Knob.WindowsDefault != null && b.Committed != null && !b.ReadOnly && !b.Knob.Same(b.Committed, b.Knob.WindowsDefault)).Select((KnobBinding b) => (b, b.Knob.WindowsDefault!)).OrderBy(((KnobBinding B, string To) x) => RowOrder(x.B.RowId)).ToList();
	}

	private static int RowOrder(string rowId)
	{
		IReadOnlyList<HubRow> rows = HubCatalog.Rows;
		for (int i = 0; i < rows.Count; i++)
		{
			if (string.Equals(rows[i].Id, rowId, StringComparison.OrdinalIgnoreCase))
			{
				return i;
			}
		}
		return int.MaxValue;
	}

	private void ShowDefaultsPreview(string page, StackPanel host)
	{
		host.Children.Clear();
		List<(KnobBinding B, string To)> diff = DefaultsDiff(page);
		host.Visibility = Visibility.Visible;
		if (diff.Count == 0)
		{
			host.Children.Add(InlineNote("Everything here is at the Windows default."));
			return;
		}
		foreach ((KnobBinding b, string to) in diff)
		{
			host.Children.Add(InfoRowDetached(b.Title, b.Display(b.Committed) + " → " + b.Display(to)));
		}
		StackPanel buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(12.0, 4.0, 12.0, 12.0) };
		Button apply = HubButton("Set " + diff.Count + (diff.Count == 1 ? " setting" : " settings") + " to Windows defaults", delegate
		{
			host.Visibility = Visibility.Collapsed;
			ApplyDefaults(page);
		}, accent: false);
		Button cancel = HubButton("Cancel", delegate { host.Visibility = Visibility.Collapsed; });
		cancel.Margin = new Thickness(8.0, 0.0, 0.0, 0.0);
		buttons.Children.Add(apply);
		buttons.Children.Add(cancel);
		host.Children.Add(buttons);
	}

	internal void ApplyDefaults(string page)
	{
		List<(KnobBinding B, string To)> diff = DefaultsDiff(page);
		List<Change> changes = new List<Change>();
		foreach ((KnobBinding b, string to) in diff)
		{
			_sessionBefore.TryAdd(b.RowId, (b.Knob, b.Committed!, b.Page));
			changes.Add(new Change { K = b.Knob, B = b, Before = b.Committed!, After = to });
		}
		ApplyChanges(changes, "defaults:" + page + ":" + Environment.TickCount64, "Windows defaults", journal: true);
	}

	// ---- Recent changes -----------------------------------------------------------------------------------------------

	private ScrollViewer RecentPage()
	{
		Pg pg = new Pg(this, "recent");
		List<JournalEntry> all = SettingsJournal.Snapshot();
		if (all.Count == 0)
		{
			pg.Block(new TextBlock { Text = "No changes yet. Changes you make here are listed for 14 days, with Undo.", FontFamily = SegoeUi, FontSize = 13.5, Foreground = _pal.Ink2, Margin = new Thickness(12.0, 8.0, 12.0, 0.0), TextWrapping = TextWrapping.Wrap });
			return pg.Done();
		}
		HashSet<string> shownGroups = new HashSet<string>(StringComparer.Ordinal);
		foreach (IGrouping<DateTime, JournalEntry> day in all.GroupBy((JournalEntry e) => e.Utc.ToLocalTime().Date))
		{
			pg.Header(DayTitle(day.Key));
			foreach (JournalEntry e in day)
			{
				if (e.Group != null && !shownGroups.Add(e.Group))
				{
					continue;
				}
				List<JournalEntry> members = e.Group != null ? SettingsJournal.GroupOf(e) : new List<JournalEntry> { e };
				string title = e.Group != null ? (e.GroupTitle ?? "Changes") + " · " + members.Count + (members.Count == 1 ? " setting" : " settings") : e.Title;
				string line = e.Group != null ? string.Join(", ", members.Select((JournalEntry m) => m.Title + " " + m.BeforeText + " → " + m.AfterText)) : e.BeforeText + " → " + e.AfterText;
				string time = e.Utc.ToLocalTime().ToString("t", CultureInfo.CurrentCulture);
				JournalEntry target = e;
				Button undo = HubButton("Undo", delegate { UndoEntry(target); });
				Grid row = RowShell(title, line + "  ·  " + time, null, undo, out _);
				AutomationProperties.SetName(undo, "Undo " + title);
				pg.Add(row);
			}
		}
		pg.Card();
		pg.Add(ButtonRow("recent.clear", "Clear list", delegate { SettingsJournal.Clear(); }, "Only the list is cleared; no setting changes", title: "Clear the list"));
		return pg.Done();
	}

	private static string DayTitle(DateTime day)
	{
		DateTime today = DateTime.Now.Date;
		if (day == today)
		{
			return "Today";
		}
		if (day == today.AddDays(-1))
		{
			return "Yesterday";
		}
		return day.ToString("D", CultureInfo.CurrentCulture);
	}

	private bool _journalSubscribed;

	private void OnJournalChanged()
	{
		base.Dispatcher.BeginInvoke(DispatcherPriority.Background, (Action)delegate
		{
			if (_closed)
			{
				return;
			}
			RefreshSidebarVisibility();
			if (_current == "recent" && !_searchActive)
			{
				RebuildCurrentPage();
			}
		});
	}

	// ---- QA -----------------------------------------------------------------------------------------------------------

	// Waits for the worker and the dispatcher until every change asked for has come back.
	internal void QaSettle(int maxMs = 4000)
	{
		DateTime until = DateTime.UtcNow.AddMilliseconds(maxMs);
		do
		{
			SettingsWorker.Flush(maxMs);
			DispatcherFrame frame = new DispatcherFrame();
			base.Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, (Action)delegate { frame.Continue = false; });
			Dispatcher.PushFrame(frame);
		}
		while ((_inFlight > 0 || _pending > 0) && DateTime.UtcNow < until);
	}

	// As if the user picked a value in the row's control.
	internal void QaChange(string rowId, string value)
	{
		if (_bindings.TryGetValue(rowId, out KnobBinding? b))
		{
			b.Show(value);
			Commit(b, value);
		}
	}

	// As if the user dragged the row's slider thumb to pos, then let go.
	internal void QaDrag(string rowId, int pos)
	{
		if (_bindings.TryGetValue(rowId, out KnobBinding? b) && b.Slider != null)
		{
			b.Dragging = true;
			b.Slider.Value = pos;
		}
	}

	internal void QaRelease(string rowId)
	{
		if (_bindings.TryGetValue(rowId, out KnobBinding? b))
		{
			b.Dragging = false;
			SliderReleased(b);
		}
	}

	// QA: the undo bar and the guard banner as they stand once their entrance is over.
	private void QaEndAnimations()
	{
		if (_undoBar != null)
		{
			_undoBar.RenderTransform = null;
		}
		if (_guard != null)
		{
			_guard.BeginAnimation(UIElement.OpacityProperty, null);
			_guard.Opacity = 1.0;
		}
	}

	internal string? QaNote(string rowId) => _bindings.TryGetValue(rowId, out KnobBinding? b) && b.Note != null && b.Note.Visibility == Visibility.Visible ? b.Note.Text : null;
}
