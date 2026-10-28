using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Shell;
using System.Windows.Threading;

#nullable enable annotations

namespace Win81Layer;

// PC Settings: one hub for the launcher's own settings, the Windows settings people actually change and exact links to
// every other Windows Settings page, Control Panel item and tool. Laid out like macOS System Settings (a sidebar with
// search, compact grouped rows, controls inside the rows, a toolbar with back/forward and the page title) in the Windows
// 8.1 colours. A compact resizable window by default (bounds remembered per monitor), full screen as an option. Pages
// are built on navigation only, nothing runs while the window is closed.
//
// Files: PcSettingsWindow.cs (window, sidebar, navigation, keyboard, QA), .Rows.cs (palette, row factory), .Search.cs,
// and one .Pages.<Area>.cs per area.
public sealed partial class PcSettingsWindow : Window
{
	// The content area: a page enters with a short fade and an 8 DIP horizontal slide (forward from the right, back
	// from the left); the old page is removed at once.
	private sealed class PageHost : Border
	{
		internal int Direction = 1;

		internal double Distance = 8.0;

		internal bool Animate = true;

		protected override void OnVisualChildrenChanged(DependencyObject visualAdded, DependencyObject visualRemoved)
		{
			base.OnVisualChildrenChanged(visualAdded, visualRemoved);
			if (visualAdded is not UIElement el || !Animate || !Motion.Enabled)
			{
				return;
			}
			double d = Motion.Distance(Distance) * Direction;
			TranslateTransform slide = new TranslateTransform(d, 0.0);
			el.RenderTransform = slide;
			Duration dur = Motion.Dur(Distance > 8.0 ? Motion.Cat.Navigation : Motion.Cat.ViewEnter);
			IEasingFunction ease = Motion.Ease(Motion.Cat.ViewEnter);
			if (Math.Abs(d) > 0.1)
			{
				slide.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(d, 0.0, dur) { EasingFunction = ease });
			}
			el.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0.0, 1.0, dur) { EasingFunction = ease });
		}
	}

	private const double SidebarW = 232.0;

	private const double CaptionH = 32.0;

	private readonly StartScreen? _start;

	private HubPalette _pal;

	private string _palSignature = "";

	private readonly Grid _root;

	private readonly Border _rail;

	private readonly Grid _railGrid;

	private Border _searchBox = null!;

	private TextBox _search = null!;

	private TextBlock _searchCue = null!;

	private TextBlock _searchGlyph = null!;

	private ListBox _nav = null!;

	private readonly Grid _toolbar;

	private Button _backBtn = null!;

	private Button _fwdBtn = null!;

	private TextBlock _pageTitle = null!;

	private Button _maxBtn = null!;

	private readonly Grid _contentHost;

	private readonly PageHost _content;

	// The page shown ("home", "appearance/pointers", "search").
	private string _current = "";

	private readonly List<(string Page, double Offset)> _back = new List<(string, double)>();

	private readonly List<(string Page, double Offset)> _forward = new List<(string, double)>();

	private const int HistoryMax = 20;

	private bool _fullScreen;

	private nint _hwnd;

	private bool _closed;

	// How the shown page is built again (after the look changed).
	private Func<UIElement>? _pageBuilder;

	internal static nint LiveHwnd { get; private set; }

	// A deep link (win81:pcsettings?page=...&row=...) handed over by Start or the Windows 7 Start menu, taken by
	// App.OpenPcSettings.
	private static string? _pendingLink;

	internal static void SetPendingLink(string? link)
	{
		_pendingLink = link;
	}

	internal static string? TakePendingLink()
	{
		string? link = _pendingLink;
		_pendingLink = null;
		return link;
	}

	// QA counters.
	internal int QaPageBuilds { get; private set; }

	internal string CurrentPage => _current;

	internal bool IsFullScreenMode => _fullScreen;

	public PcSettingsWindow(StartScreen start)
	{
		if (!ShellSkin.GlassOn)
		{
			MetroPatternTheme.Apply(this);
		}
		_start = start;
		_pal = HubPalette.Compute();
		base.Title = "PC settings";
		base.WindowStyle = WindowStyle.None;
		base.ResizeMode = ResizeMode.CanResize;
		base.ShowInTaskbar = true;
		base.MinWidth = 780.0;
		base.MinHeight = 520.0;
		base.Width = 980.0;
		base.Height = 720.0;
		base.UseLayoutRounding = true;
		WindowChrome.SetWindowChrome(this, new WindowChrome
		{
			CaptionHeight = CaptionH,
			ResizeBorderThickness = new Thickness(4.0),
			GlassFrameThickness = new Thickness(0.0),
			CornerRadius = new CornerRadius(0.0),
			UseAeroCaptionButtons = false
		});
		_root = new Grid();
		_root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(SidebarW) });
		_root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.0, GridUnitType.Star) });
		_root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(CaptionH) });
		_root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1.0, GridUnitType.Star) });
		_railGrid = new Grid();
		_rail = new Border { Child = _railGrid };
		Grid.SetRowSpan(_rail, 2);
		_root.Children.Add(_rail);
		BuildSidebar();
		_toolbar = BuildToolbar();
		Grid.SetColumn(_toolbar, 1);
		_root.Children.Add(_toolbar);
		_content = new PageHost();
		_contentHost = new Grid();
		// row 0: the "Keep this change?" banner (above the page, so it stays while pages change); row 1: the page and the
		// undo bar docked at its bottom
		_contentHost.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
		_contentHost.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1.0, GridUnitType.Star) });
		Grid.SetRow(_content, 1);
		_contentHost.Children.Add(_content);
		Grid.SetColumn(_contentHost, 1);
		Grid.SetRow(_contentHost, 1);
		_root.Children.Add(_contentHost);
		KeyboardNavigation.SetTabNavigation(_contentHost, KeyboardNavigationMode.Local);
		base.Content = _root;
		try
		{
			base.Icon = BuildGearIcon();
		}
		catch
		{
		}
		base.PreviewKeyDown += OnPreviewKey;
		base.PreviewTextInput += OnPreviewText;
		AddHandler(UIElement.KeyDownEvent, new KeyEventHandler(OnKeyBubble), handledEventsToo: false);
		base.StateChanged += delegate { UpdateMaxGlyph(); };
		ApplySkin();
		HubCaps? cached = CachedCaps();
		if (cached != null)
		{
			_caps = cached;
		}
		RefreshSidebarVisibility();
	}

	// ---- Look -------------------------------------------------------------------------------------------------------

	private bool _restSkinApplied;

	private static string PaletteSignature()
	{
		AppSettings s = SettingsStore.FastSnapshot;
		Color accent;
		try
		{
			accent = StartAccent.Color();
		}
		catch
		{
			accent = Colors.Transparent;
		}
		return string.Join("|", DarkRest.Visuals, ShellTheme.IsDark, ShellSkin.GlassOn, SystemParameters.HighContrast, s.SeasonalActive ?? "", s.StartBgMode ?? "", s.StartBgColor ?? "", accent.ToString(CultureInfo.InvariantCulture));
	}

	// The palette, the Dark rest control family (or the normal one again) and the accent of the toggles, written into
	// this window's resources; then every surface of the shell takes its colours.
	private void ApplySkin()
	{
		_pal = HubPalette.Compute();
		_palSignature = PaletteSignature();
		_segmentTemplate = null;
		_chipTemplate = null;
		_restSkinApplied = DarkRest.Visuals;
		ShellTheme.RemoveTokens(base.Resources);
		DarkRestPalette.RemoveRestResources(base.Resources);
		base.Resources.Remove("Metro81.LinkButton");
		base.Resources.Remove("Metro81.SliderFill");
		base.Resources.Remove("Metro81.FocusBorder");
		base.Resources.Remove("Metro81.TextSelection");
		if (DarkRest.Visuals)
		{
			WriteRestResources(base.Resources, TryFindResource("Metro81.ToggleSwitch") as Style);
		}
		else
		{
			// sliders, focused fields and text selection take the hub's accent, like the toggles (one palette source)
			base.Resources["Metro81.SliderFill"] = _pal.SliderFill;
			base.Resources["Metro81.FocusBorder"] = _pal.ToggleOn;
			base.Resources["Metro81.TextSelection"] = _pal.ToggleOn;
		}
		base.Resources["MenuAccent"] = _pal.ToggleOn;
		base.Resources["Metro81.LinkInk"] = _pal.Link;
		ApplyScrollBrushes();
		base.Background = _pal.Page;
		base.Foreground = _pal.Ink;
		_rail.Background = _pal.Rail;
		_rail.BorderBrush = _pal.RailEdge;
		_rail.BorderThickness = new Thickness(0.0, 0.0, _pal.RailEdge != null ? 1.0 : 0.0, 0.0);
		_toolbar.Background = _pal.Page;
		_contentHost.Background = _pal.Page;
		_pageTitle.Foreground = _pal.Ink;
		ApplySearchLook();
		RebuildSidebarItems();
		RestyleToolbar();
	}

	// The Dark rest resources of this window (or of a page rendered on its own): the dark token set, the black control
	// family with toggles whose On and Off fills read apart and quiet scrollbars, and the light link style.
	private static void WriteRestResources(ResourceDictionary target, Style? toggleBase)
	{
		ShellTheme.WriteTokens(target, dark: true);
		DarkRestPalette.WritePageResources(target, toggleBase);
		if (RestLinkStyle() is Style link)
		{
			target["Metro81.LinkButton"] = link;
		}
	}

	private static Style? _restLinkStyle;

	private static Style? RestLinkStyle()
	{
		if (_restLinkStyle == null && Application.Current?.TryFindResource("Metro81.LinkButton") is Style baseStyle)
		{
			Style style = new Style(typeof(Button), baseStyle);
			style.Setters.Add(new Setter(Control.ForegroundProperty, DarkRestPalette.PcAccentInk));
			Trigger hover = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
			hover.Setters.Add(new Setter(Control.ForegroundProperty, DarkRestPalette.PcText));
			style.Triggers.Add(hover);
			style.Seal();
			_restLinkStyle = style;
		}
		return _restLinkStyle;
	}

	// After Dark rest, a season, the accent or Windows light/dark changed (from here, Start, the tray or a recovery):
	// an open window takes the new colours and builds the page on screen again. Nothing happens when nothing changed.
	internal void SyncRestSkin()
	{
		if (_restSkinApplied == DarkRest.Visuals && string.Equals(_palSignature, PaletteSignature(), StringComparison.Ordinal))
		{
			return;
		}
		ApplySkin();
		RebuildCurrentPage();
	}

	private void OnThemeChanged(object? sender, EventArgs e)
	{
		base.Dispatcher.BeginInvoke(DispatcherPriority.Background, (Action)SyncRestSkin);
	}

	// ---- Toolbar (the 32 DIP caption) -------------------------------------------------------------------------------

	private Grid BuildToolbar()
	{
		Grid bar = new Grid { Height = CaptionH };
		StackPanel left = new StackPanel
		{
			Orientation = Orientation.Horizontal,
			Margin = new Thickness(8.0, 0.0, 0.0, 0.0),
			VerticalAlignment = VerticalAlignment.Center
		};
		_backBtn = ArrowButton(MetroArrowDirection81.Left, "Back", Back);
		_fwdBtn = ArrowButton(MetroArrowDirection81.Right, "Forward", Forward);
		left.Children.Add(_backBtn);
		left.Children.Add(_fwdBtn);
		bar.Children.Add(left);
		_pageTitle = new TextBlock
		{
			FontFamily = SegoeUiSemibold,
			FontSize = 15.0,
			VerticalAlignment = VerticalAlignment.Center,
			Margin = new Thickness(72.0, 0.0, 160.0, 0.0),
			TextTrimming = TextTrimming.CharacterEllipsis,
			IsHitTestVisible = false
		};
		AutomationProperties.SetHeadingLevel(_pageTitle, AutomationHeadingLevel.Level1);
		bar.Children.Add(_pageTitle);
		StackPanel caption = new StackPanel
		{
			Orientation = Orientation.Horizontal,
			HorizontalAlignment = HorizontalAlignment.Right,
			VerticalAlignment = VerticalAlignment.Top
		};
		_changedToggle = ChangedOnlyToggle();
		caption.Children.Add(_changedToggle);
		caption.Children.Add(CaptionButton("\uE921", "Minimize", false, delegate { base.WindowState = WindowState.Minimized; }));
		_maxBtn = CaptionButton("\uE922", "Maximize", false, delegate
		{
			base.WindowState = base.WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
		});
		caption.Children.Add(_maxBtn);
		caption.Children.Add(CaptionButton("\uE8BB", "Close", true, base.Close));
		bar.Children.Add(caption);
		KeyboardNavigation.SetTabNavigation(bar, KeyboardNavigationMode.Local);
		return bar;
	}

	// "Show only changed" (pages whose rows have documented Windows defaults): shows only the rows that differ from them.
	private CheckBox? _changedToggle;

	private bool _quietChanged;

	private CheckBox ChangedOnlyToggle()
	{
		// a check box (a filter that is on or off), drawn as a pill
		CheckBox t = new CheckBox
		{
			Style = BareButton,
			Height = 24.0,
			Margin = new Thickness(0.0, 4.0, 10.0, 0.0),
			VerticalAlignment = VerticalAlignment.Top,
			Cursor = Cursors.Hand,
			Visibility = Visibility.Collapsed,
			ToolTip = "Show only the settings that differ from the Windows defaults"
		};
		WindowChrome.SetIsHitTestVisibleInChrome(t, true);
		AutomationProperties.SetName(t, "Show only changed");
		t.SetResourceReference(FrameworkElement.FocusVisualStyleProperty, "Metro81.Focus");
		t.Checked += delegate
		{
			if (!_quietChanged)
			{
				SetChangedOnly(true);
			}
		};
		t.Unchecked += delegate
		{
			if (!_quietChanged)
			{
				SetChangedOnly(false);
			}
		};
		return t;
	}

	// The pill: a 5 DIP accent dot and "Show only changed"; filled with the accent while it is on.
	private void StyleChangedToggle()
	{
		if (_changedToggle == null)
		{
			return;
		}
		ControlTemplate tpl = new ControlTemplate(typeof(CheckBox));
		FrameworkElementFactory bd = new FrameworkElementFactory(typeof(Border), "Bd");
		bd.SetValue(Border.BackgroundProperty, Brushes.Transparent);
		bd.SetValue(Border.BorderBrushProperty, _pal.CardBorder);
		bd.SetValue(Border.BorderThicknessProperty, new Thickness(1.0));
		bd.SetValue(Border.PaddingProperty, new Thickness(9.0, 0.0, 10.0, 0.0));
		bd.SetValue(UIElement.SnapsToDevicePixelsProperty, true);
		FrameworkElementFactory sp = new FrameworkElementFactory(typeof(StackPanel));
		sp.SetValue(StackPanel.OrientationProperty, Orientation.Horizontal);
		sp.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
		FrameworkElementFactory dot = new FrameworkElementFactory(typeof(System.Windows.Shapes.Ellipse), "Dot");
		dot.SetValue(FrameworkElement.WidthProperty, 5.0);
		dot.SetValue(FrameworkElement.HeightProperty, 5.0);
		dot.SetValue(System.Windows.Shapes.Shape.FillProperty, _pal.ToggleOn);
		dot.SetValue(FrameworkElement.MarginProperty, new Thickness(0.0, 1.0, 7.0, 0.0));
		dot.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
		sp.AppendChild(dot);
		FrameworkElementFactory text = new FrameworkElementFactory(typeof(TextBlock), "Text");
		text.SetValue(TextBlock.TextProperty, "Show only changed");
		text.SetValue(TextBlock.FontFamilyProperty, SegoeUi);
		text.SetValue(TextBlock.FontSizeProperty, 12.0);
		text.SetValue(TextBlock.ForegroundProperty, _pal.Ink);
		text.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
		sp.AppendChild(text);
		bd.AppendChild(sp);
		tpl.VisualTree = bd;
		Trigger hover = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
		hover.Setters.Add(new Setter(Border.BackgroundProperty, _pal.RowHover, "Bd"));
		tpl.Triggers.Add(hover);
		Trigger on = new Trigger { Property = ToggleButton.IsCheckedProperty, Value = true };
		on.Setters.Add(new Setter(Border.BackgroundProperty, _pal.ToggleOn, "Bd"));
		on.Setters.Add(new Setter(Border.BorderBrushProperty, _pal.ToggleOn, "Bd"));
		on.Setters.Add(new Setter(TextBlock.ForegroundProperty, _pal.Rest ? DarkRestPalette.InkOnAccent : Brushes.White, "Text"));
		on.Setters.Add(new Setter(System.Windows.Shapes.Shape.FillProperty, _pal.Rest ? DarkRestPalette.InkOnAccent : Brushes.White, "Dot"));
		tpl.Triggers.Add(on);
		tpl.Seal();
		_changedToggle.Template = tpl;
	}

	// Shown on the pages that have it; the page title keeps clear of it.
	private void UpdateChangedToggle()
	{
		if (_changedToggle == null)
		{
			return;
		}
		bool show = ChangedFilterPages.Contains(_current) && !_searchActive;
		_changedToggle.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
		_pageTitle.Margin = new Thickness(72.0, 0.0, show ? 300.0 : 160.0, 0.0);
	}

	private Button ArrowButton(MetroArrowDirection81 dir, string name, Action click)
	{
		Button b = new Button
		{
			Style = BareButton,
			Width = 28.0,
			Height = 28.0,
			Cursor = Cursors.Hand,
			Tag = dir
		};
		WindowChrome.SetIsHitTestVisibleInChrome(b, true);
		AutomationProperties.SetName(b, name);
		b.ToolTip = name + (dir == MetroArrowDirection81.Left ? " (Alt+Left)" : " (Alt+Right)");
		b.SetResourceReference(FrameworkElement.FocusVisualStyleProperty, "Metro81.Focus");
		b.Click += delegate { click(); };
		return b;
	}

	private Button CaptionButton(string glyph, string name, bool close, Action click)
	{
		Button b = new Button
		{
			Style = BareButton,
			Width = 46.0,
			Height = CaptionH,
			Focusable = false,
			Content = new TextBlock { Text = glyph, FontFamily = Glyphs, FontSize = 10.0 }
		};
		WindowChrome.SetIsHitTestVisibleInChrome(b, true);
		AutomationProperties.SetName(b, name);
		b.Tag = close ? "close" : "caption";
		b.Click += delegate { click(); };
		return b;
	}

	private void RestyleToolbar()
	{
		foreach (Button b in new[] { _backBtn, _fwdBtn })
		{
			MetroArrowDirection81 dir = (MetroArrowDirection81)b.Tag;
			System.Windows.Shapes.Rectangle glyph = new System.Windows.Shapes.Rectangle
			{
				Width = 16.0,
				Height = 16.0,
				Fill = _pal.Ink,
				OpacityMask = new ImageBrush(MetroDirectionalArrow81Vectors.Draw(dir, MetroArrowState81.Normal, Colors.White))
			};
			b.Template = _pal.CaptionTemplate(close: false);
			b.Content = glyph;
		}
		if (_toolbar.Children.Count > 2 && _toolbar.Children[2] is StackPanel caption)
		{
			foreach (Button b in caption.Children.OfType<Button>())
			{
				bool close = Equals(b.Tag, "close");
				b.Template = _pal.CaptionTemplate(close);
				b.Foreground = _pal.Ink;
				if (b.Content is TextBlock t)
				{
					t.Foreground = null;
					t.ClearValue(TextBlock.ForegroundProperty);
				}
			}
		}
		StyleChangedToggle();
		UpdateMaxGlyph();
		UpdateHistoryButtons();
	}

	private void UpdateMaxGlyph()
	{
		if (_maxBtn?.Content is TextBlock t)
		{
			bool max = base.WindowState == WindowState.Maximized;
			t.Text = max ? "\uE923" : "\uE922";
			AutomationProperties.SetName(_maxBtn, max ? "Restore" : "Maximize");
		}
	}

	private void UpdateHistoryButtons()
	{
		_backBtn.IsEnabled = _back.Count > 0;
		_fwdBtn.IsEnabled = _forward.Count > 0;
	}

	// ---- Sidebar ----------------------------------------------------------------------------------------------------

	// One sidebar entry: a page, or the account row at the top.
	private sealed class NavEntry
	{
		internal string Id = "";

		internal HubPage? Page;

		internal ListBoxItem Item = null!;

		internal TextBlock? Label;

		internal bool Visible = true;
	}

	private readonly List<NavEntry> _entries = new List<NavEntry>();

	private Border _accountPic = null!;

	private TextBlock _accountName = null!;

	private TextBlock _accountType = null!;

	private bool _syncingNav;

	private void BuildSidebar()
	{
		_railGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(CaptionH + 8.0) });
		_railGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
		_railGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1.0, GridUnitType.Star) });
		// search (fixed, does not scroll); MinHeight 0 because the window's Metro81.TextBox style asks for 36 DIP, which
		// would lay the field out taller than its 28 DIP box and centre the text 4 DIP low
		_search = new TextBox
		{
			BorderThickness = new Thickness(0.0),
			Background = Brushes.Transparent,
			FontFamily = SegoeUi,
			FontSize = 13.5,
			VerticalContentAlignment = VerticalAlignment.Center,
			MinHeight = 0.0,
			Padding = new Thickness(0.0),
			Margin = new Thickness(26.0, 0.0, 6.0, 0.0),
			Template = PlainTextBoxTemplate()
		};
		AutomationProperties.SetName(_search, "Search settings");
		KeyboardNavigation.SetTabIndex(_search, 0);
		_search.SetResourceReference(FrameworkElement.FocusVisualStyleProperty, "Metro81.Focus");
		// The placeholder sits where the typed text will be: its origin is taken from the text box's own first character
		// position once laid out (AlignSearchCue), so nothing jumps on the first keystroke.
		_searchCue = new TextBlock
		{
			Text = "Search settings",
			FontFamily = SegoeUi,
			FontSize = 13.5,
			VerticalAlignment = VerticalAlignment.Center,
			HorizontalAlignment = HorizontalAlignment.Left,
			Margin = new Thickness(26.0, 0.0, 0.0, 0.0),
			IsHitTestVisible = false
		};
		_searchGlyph = new TextBlock
		{
			Text = "\uE721",
			FontFamily = Glyphs,
			FontSize = 12.0,
			VerticalAlignment = VerticalAlignment.Center,
			Margin = new Thickness(8.0, 0.0, 0.0, 0.0),
			IsHitTestVisible = false
		};
		Grid sg = new Grid();
		sg.Children.Add(_searchGlyph);
		sg.Children.Add(_searchCue);
		sg.Children.Add(_search);
		_searchBox = new Border
		{
			Height = 28.0,
			Margin = new Thickness(8.0, 0.0, 8.0, 8.0),
			Child = sg,
			SnapsToDevicePixels = true
		};
		_search.GotKeyboardFocus += delegate { ApplySearchLook(); };
		_search.LostKeyboardFocus += delegate { ApplySearchLook(); };
		_search.SizeChanged += delegate { base.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, (Action)AlignSearchCue); };
		_search.Loaded += delegate { base.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, (Action)AlignSearchCue); };
		_search.TextChanged += delegate { OnSearchTextChanged(); };
		_search.PreviewKeyDown += OnSearchKey;
		Grid.SetRow(_searchBox, 1);
		_railGrid.Children.Add(_searchBox);
		_nav = new ListBox
		{
			Background = Brushes.Transparent,
			BorderThickness = new Thickness(0.0),
			Padding = new Thickness(0.0),
			Margin = new Thickness(0.0, 0.0, 0.0, 8.0),
			HorizontalContentAlignment = HorizontalAlignment.Stretch
		};
		ScrollViewer.SetHorizontalScrollBarVisibility(_nav, ScrollBarVisibility.Disabled);
		ScrollViewer.SetVerticalScrollBarVisibility(_nav, ScrollBarVisibility.Auto);
		ScrollViewer.SetCanContentScroll(_nav, false);
		_nav.Template = HubListTemplate;
		AutomationProperties.SetName(_nav, "Settings categories");
		KeyboardNavigation.SetTabIndex(_nav, 1);
		_nav.SelectionChanged += OnNavSelection;
		_nav.PreviewKeyDown += OnNavKey;
		Grid.SetRow(_nav, 2);
		_railGrid.Children.Add(_nav);
	}

	private static ControlTemplate? _plainTextBoxTemplate;

	private static ControlTemplate PlainTextBoxTemplate()
	{
		if (_plainTextBoxTemplate != null)
		{
			return _plainTextBoxTemplate;
		}
		ControlTemplate t = new ControlTemplate(typeof(TextBox));
		FrameworkElementFactory sv = new FrameworkElementFactory(typeof(ScrollViewer), "PART_ContentHost");
		sv.SetValue(UIElement.FocusableProperty, false);
		sv.SetValue(ScrollViewer.HorizontalScrollBarVisibilityProperty, ScrollBarVisibility.Hidden);
		sv.SetValue(ScrollViewer.VerticalScrollBarVisibilityProperty, ScrollBarVisibility.Hidden);
		sv.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
		t.VisualTree = sv;
		t.Seal();
		_plainTextBoxTemplate = t;
		return t;
	}

	// Puts the placeholder's top-left on the text box's first character position (the text view's own inset and line
	// placement included), so the cue and the typed text share one origin.
	internal void AlignSearchCue()
	{
		if (_searchCue == null || !_search.IsLoaded || _searchCue.Parent is not Grid host)
		{
			return;
		}
		try
		{
			Rect r = _search.GetRectFromCharacterIndex(0);
			if (r.IsEmpty || double.IsNaN(r.X) || double.IsInfinity(r.Y))
			{
				return;
			}
			Point p = _search.TranslatePoint(new Point(r.X, r.Y), host);
			Thickness m = new Thickness(Math.Round(p.X), Math.Round(p.Y), 0.0, 0.0);
			if (_searchCue.VerticalAlignment != VerticalAlignment.Top || _searchCue.Margin != m)
			{
				_searchCue.VerticalAlignment = VerticalAlignment.Top;
				_searchCue.Margin = m;
			}
		}
		catch (Exception ex)
		{
			Logger.Log("PC settings: search cue: " + ex.Message);
		}
	}

	private void ApplySearchLook()
	{
		if (_searchBox == null)
		{
			return;
		}
		bool focused = _search.IsKeyboardFocusWithin;
		_searchBox.Background = focused ? _pal.Field : _pal.SearchFill;
		Brush ink = focused ? _pal.Ink : _pal.RailInk;
		_search.Foreground = ink;
		_search.CaretBrush = ink;
		_searchGlyph.Foreground = focused ? _pal.Ink2 : _pal.SearchCue;
		_searchCue.Foreground = focused ? _pal.Ink2 : _pal.SearchCue;
		_searchCue.Visibility = _search.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
	}

	private ControlTemplate? _navItemTemplate;

	private ControlTemplate NavItemTemplate()
	{
		if (_navItemTemplate != null)
		{
			return _navItemTemplate;
		}
		ControlTemplate t = new ControlTemplate(typeof(ListBoxItem));
		FrameworkElementFactory root = new FrameworkElementFactory(typeof(Grid));
		root.SetValue(Panel.BackgroundProperty, Brushes.Transparent);
		FrameworkElementFactory hover = new FrameworkElementFactory(typeof(Border), "Hover");
		hover.SetValue(Border.BackgroundProperty, _pal.Hover);
		hover.SetValue(UIElement.OpacityProperty, 0.0);
		root.AppendChild(hover);
		FrameworkElementFactory sel = new FrameworkElementFactory(typeof(Border), "Sel");
		sel.SetValue(Border.BackgroundProperty, _pal.Selected);
		sel.SetValue(UIElement.VisibilityProperty, Visibility.Hidden);
		root.AppendChild(sel);
		FrameworkElementFactory bar = new FrameworkElementFactory(typeof(Border), "Bar");
		bar.SetValue(FrameworkElement.WidthProperty, 2.0);
		bar.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Left);
		bar.SetValue(Border.BackgroundProperty, _pal.SelectedBar ?? Brushes.Transparent);
		bar.SetValue(UIElement.VisibilityProperty, Visibility.Hidden);
		root.AppendChild(bar);
		FrameworkElementFactory cp = new FrameworkElementFactory(typeof(ContentPresenter));
		cp.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
		root.AppendChild(cp);
		t.VisualTree = root;
		Trigger over = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
		DoubleAnimation fadeIn = new DoubleAnimation(1.0, Motion.Dur(Motion.Cat.Hover));
		DoubleAnimation fadeOut = new DoubleAnimation(0.0, Motion.Dur(Motion.Cat.Hover));
		Storyboard.SetTargetName(fadeIn, "Hover");
		Storyboard.SetTargetProperty(fadeIn, new PropertyPath(UIElement.OpacityProperty));
		Storyboard.SetTargetName(fadeOut, "Hover");
		Storyboard.SetTargetProperty(fadeOut, new PropertyPath(UIElement.OpacityProperty));
		Storyboard sbIn = new Storyboard();
		sbIn.Children.Add(fadeIn);
		Storyboard sbOut = new Storyboard();
		sbOut.Children.Add(fadeOut);
		over.EnterActions.Add(new BeginStoryboard { Storyboard = sbIn });
		over.ExitActions.Add(new BeginStoryboard { Storyboard = sbOut });
		t.Triggers.Add(over);
		Trigger selected = new Trigger { Property = Selector.IsSelectedProperty, Value = true };
		selected.Setters.Add(new Setter(UIElement.VisibilityProperty, Visibility.Visible, "Sel"));
		if (_pal.SelectedBar != null)
		{
			selected.Setters.Add(new Setter(UIElement.VisibilityProperty, Visibility.Visible, "Bar"));
		}
		if (_pal.HighContrast)
		{
			selected.Setters.Add(new Setter(Control.ForegroundProperty, SystemColors.HighlightTextBrush));
		}
		t.Triggers.Add(selected);
		t.Seal();
		_navItemTemplate = t;
		return t;
	}

	// The sidebar entries: the account row, then the six groups (9 DIP between groups, no headers).
	private void RebuildSidebarItems()
	{
		if (_nav == null)
		{
			return;
		}
		_navItemTemplate = null;
		string? selected = (_nav.SelectedItem as ListBoxItem)?.Tag as string;
		_syncingNav = true;
		try
		{
			_nav.Items.Clear();
			_entries.Clear();
			_entries.Add(new NavEntry { Id = "account", Item = AccountItem() });
			int group = 0;
			foreach (HubPage p in HubCatalog.Pages)
			{
				if (p.Group <= 0)
				{
					continue;
				}
				NavEntry e = new NavEntry { Id = p.Id, Page = p };
				e.Item = PageItem(p, out e.Label);
				e.Item.Margin = new Thickness(0.0, p.Group != group ? 9.0 : 0.0, 0.0, 0.0);
				group = p.Group;
				_entries.Add(e);
			}
			foreach (NavEntry e in _entries)
			{
				e.Item.Template = NavItemTemplate();
				e.Item.MinHeight = 0.0;
				e.Item.Padding = new Thickness(0.0);
				e.Item.SetResourceReference(FrameworkElement.FocusVisualStyleProperty, "Metro81.Focus");
				e.Item.Foreground = _pal.RailInk;
				_nav.Items.Add(e.Item);
			}
			RefreshSidebarVisibility();
			SelectNav(selected ?? TopLevel(_current));
		}
		finally
		{
			_syncingNav = false;
		}
	}

	private ListBoxItem AccountItem()
	{
		Grid g = new Grid { Height = 40.0 };
		g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(36.0) });
		g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.0, GridUnitType.Star) });
		_accountPic = new Border
		{
			Width = 24.0,
			Height = 24.0,
			HorizontalAlignment = HorizontalAlignment.Left,
			Margin = new Thickness(8.0, 0.0, 0.0, 0.0),
			Background = _pal.MiniTile("#0072C6"),
			Child = new TextBlock
			{
				Text = "\uE77B",
				FontFamily = Glyphs,
				FontSize = 14.0,
				Foreground = _pal.MiniGlyph,
				HorizontalAlignment = HorizontalAlignment.Center,
				VerticalAlignment = VerticalAlignment.Center
			}
		};
		g.Children.Add(_accountPic);
		StackPanel text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
		_accountName = new TextBlock
		{
			Text = SafeUserName(),
			FontFamily = SegoeUi,
			FontSize = 13.5,
			Foreground = _pal.RailInk,
			TextTrimming = TextTrimming.CharacterEllipsis
		};
		_accountType = new TextBlock
		{
			Text = AccountKind(),
			FontFamily = SegoeUi,
			FontSize = 11.0,
			Foreground = _pal.RailInk2,
			TextTrimming = TextTrimming.CharacterEllipsis
		};
		text.Children.Add(_accountName);
		text.Children.Add(_accountType);
		Grid.SetColumn(text, 1);
		g.Children.Add(text);
		ListBoxItem item = new ListBoxItem { Content = g, Tag = "account", Padding = new Thickness(0.0) };
		AutomationProperties.SetName(item, "Account: " + _accountName.Text);
		if (_accountLoaded != null)
		{
			ApplyAccount(_accountLoaded.Value.Picture, _accountLoaded.Value.Name);
		}
		else if (SettingsWorld.IsReal && Application.Current != null)
		{
			LoadAccountAsync();
		}
		return item;
	}

	private static (ImageSource? Picture, string Name)? _accountLoaded;

	private async void LoadAccountAsync()
	{
		_pending++;
		try
		{
			(ImageSource? img, string name) = await UserAccount.LoadAsync();
			_accountLoaded = (img, name);
			ApplyAccount(img, name);
		}
		catch
		{
		}
		finally
		{
			_pending = Math.Max(0, _pending - 1);
		}
	}

	private void ApplyAccount(ImageSource? img, string name)
	{
		if (!string.IsNullOrWhiteSpace(name))
		{
			_accountName.Text = name;
		}
		if (img != null)
		{
			_accountPic.Child = new Image { Source = img, Stretch = Stretch.UniformToFill };
		}
	}

	private static string SafeUserName()
	{
		try
		{
			return Environment.UserName;
		}
		catch
		{
			return "User";
		}
	}

	// "Microsoft account" when the user signs in with one (a connected MSA identity), otherwise "Local account".
	private static string AccountKind()
	{
		try
		{
			using Microsoft.Win32.RegistryKey? k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\IdentityCRL\UserExtendedProperties");
			if (k != null && k.GetSubKeyNames().Any((string n) => n.Contains('@')))
			{
				return "Microsoft account";
			}
		}
		catch
		{
		}
		return "Local account";
	}

	private ListBoxItem PageItem(HubPage p, out TextBlock label)
	{
		Grid g = new Grid { Height = 28.0 };
		Border tile = MiniTile(p.Tile, p.Glyph, 20.0, 13.0);
		tile.HorizontalAlignment = HorizontalAlignment.Left;
		tile.Margin = new Thickness(8.0, 0.0, 0.0, 0.0);
		g.Children.Add(tile);
		label = new TextBlock
		{
			Text = SidebarTitle(p),
			FontFamily = SegoeUi,
			FontSize = 13.5,
			Foreground = _pal.RailInk,
			VerticalAlignment = VerticalAlignment.Center,
			Margin = new Thickness(36.0, 0.0, 8.0, 0.0),
			TextTrimming = TextTrimming.CharacterEllipsis
		};
		g.Children.Add(label);
		ListBoxItem item = new ListBoxItem { Content = g, Tag = p.Id, Padding = new Thickness(0.0) };
		AutomationProperties.SetName(item, SidebarTitle(p));
		return item;
	}

	// "Power" when the PC has no battery and no sleep.
	private string SidebarTitle(HubPage p)
	{
		if (p.Id == "power" && _caps != null && !_caps.HasBattery && !_caps.SleepAllowed)
		{
			return "Power";
		}
		return p.Title;
	}

	internal string PageTitle(string id)
	{
		if (id == "search")
		{
			return "Search";
		}
		HubPage? p = HubCatalog.Page(id);
		return p == null ? "PC settings" : SidebarTitle(p);
	}

	// Which entries this PC shows (hidden entries collapse).
	private bool EntryVisible(string id)
	{
		switch (id)
		{
		case "recent":
			return SettingsJournal.Count > 0;
		case "touchpad":
			return _caps != null && (_caps.Pc.Touchpad || _caps.Pc.Touch) && (_caps.PageAvailable("devices-touchpad") || _caps.PageAvailable("pen"));
		case "privacy":
			return _caps == null || PrivacyPages().Count > 0 || SecurityLinkAvailable();
		case "tools":
			return _caps == null || !_caps.NoControlPanel;
		default:
			return true;
		}
	}

	private void RefreshSidebarVisibility()
	{
		foreach (NavEntry e in _entries)
		{
			e.Visible = EntryVisible(e.Id);
			e.Item.Visibility = e.Visible ? Visibility.Visible : Visibility.Collapsed;
			if (e.Page != null && e.Label != null)
			{
				e.Label.Text = SidebarTitle(e.Page);
			}
		}
	}

	// The page entries the sidebar shows now, in order (Ctrl+1..9 and QA).
	internal List<string> VisibleEntries() => _entries.Where((NavEntry e) => e.Visible && e.Page != null).Select((NavEntry e) => e.Id).ToList();

	private static string TopLevel(string page)
	{
		if (string.IsNullOrEmpty(page))
		{
			return "home";
		}
		HubPage? p = HubCatalog.Page(page);
		return p?.Parent ?? page;
	}

	private void SelectNav(string id)
	{
		if (_nav == null)
		{
			return;
		}
		bool was = _syncingNav;
		_syncingNav = true;
		try
		{
			ListBoxItem? item = _entries.FirstOrDefault((NavEntry e) => e.Id == id)?.Item;
			_nav.SelectedItem = item;
		}
		finally
		{
			_syncingNav = was;
		}
	}

	private void OnNavSelection(object sender, SelectionChangedEventArgs e)
	{
		if (_syncingNav || _nav.SelectedItem is not ListBoxItem { Tag: string id })
		{
			return;
		}
		if (_searchActive)
		{
			ClearSearch(keepPage: true);
		}
		Navigate(id, null, push: true);
	}

	private void OnNavKey(object sender, KeyEventArgs e)
	{
		if (e.Key == Key.Enter || e.Key == Key.Right)
		{
			if (_nav.SelectedItem is ListBoxItem { Tag: string id } && id != TopLevel(_current))
			{
				Navigate(id, null, push: true);
			}
			FocusContent();
			e.Handled = true;
		}
	}

	// ---- Navigation -------------------------------------------------------------------------------------------------

	// Opens a page by an old category name, a page id or a row id (the probe calls it by reflection: one overload).
	private void Nav(string name)
	{
		string id = HubCatalog.Resolve(name) ?? "home";
		string? row = HubCatalog.Row(name) != null ? HubCatalog.Row(name)!.Id : null;
		Navigate(id, row, push: _current.Length > 0);
	}

	// Opens one category (for example "Launcher") from outside: the tray menu's "Launcher settings..." and the taskbar
	// menu's Properties. Unknown names are ignored.
	internal void OpenCategory(string name)
	{
		string? id = HubCatalog.Resolve(name);
		if (id == null)
		{
			return;
		}
		try
		{
			ClearSearch(keepPage: true);
			Navigate(id, HubCatalog.Row(name)?.Id, push: _current.Length > 0);
		}
		catch (Exception ex)
		{
			Logger.Log("PC settings: open category failed: " + ex.Message);
		}
	}

	// win81:pcsettings?page=<id>&row=<rowId>
	internal void OpenUri(string? uri)
	{
		(string? page, string? row) = HubCatalog.ParseUri(uri);
		if (page != null)
		{
			ClearSearch(keepPage: true);
			Navigate(page, row, push: _current.Length > 0);
		}
	}

	private void Navigate(string id, string? row, bool push, int direction = 1)
	{
		if (HubCatalog.Page(id) == null)
		{
			id = "home";
		}
		if (push && _current.Length > 0 && !string.Equals(_current, id, StringComparison.Ordinal) && _current != "search")
		{
			_back.Add((_current, CurrentOffset()));
			if (_back.Count > HistoryMax)
			{
				_back.RemoveAt(0);
			}
			_forward.Clear();
		}
		bool drill = HubCatalog.Page(id)?.Parent != null && HubCatalog.Page(id)!.Parent == TopLevel(_current);
		bool back = direction < 0;
		_current = id;
		_content.Direction = back ? -1 : 1;
		_content.Distance = drill || (back && HubCatalog.Page(_back.Count > 0 ? _back[^1].Page : "")?.Parent != null) ? 16.0 : 8.0;
		string pageId = id;
		ShowPage(() => BuildPageCore(pageId));
		_pageTitle.Text = PageTitle(id);
		SelectNav(TopLevel(id));
		UpdateHistoryButtons();
		UpdateChangedToggle();
		if (row != null)
		{
			RevealRow(row);
		}
	}

	private double CurrentOffset() => (_content.Child as ScrollViewer)?.VerticalOffset ?? 0.0;

	internal void Back()
	{
		if (_searchActive)
		{
			ClearSearch(keepPage: true);
			return;
		}
		if (_back.Count == 0)
		{
			return;
		}
		(string page, double offset) = _back[^1];
		_back.RemoveAt(_back.Count - 1);
		_forward.Add((_current, CurrentOffset()));
		Navigate(page, null, push: false, direction: -1);
		RestoreOffset(offset);
	}

	internal void Forward()
	{
		if (_forward.Count == 0)
		{
			return;
		}
		(string page, double offset) = _forward[^1];
		_forward.RemoveAt(_forward.Count - 1);
		_back.Add((_current, CurrentOffset()));
		Navigate(page, null, push: false, direction: 1);
		RestoreOffset(offset);
	}

	private void RestoreOffset(double offset)
	{
		if (offset <= 0.0 || _content.Child is not ScrollViewer sv)
		{
			return;
		}
		base.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, (Action)delegate { sv.ScrollToVerticalOffset(offset); });
	}

	private void ShowPage(Func<UIElement> build)
	{
		_pageBuilder = build;
		UIElement page = build();
		_content.Child = page;
		if (_changedOnly)
		{
			ApplyChangedFilter();
		}
		if (DarkRest.Visuals)
		{
			// white text and glyphs of picture tiles: the soft ink
			DarkRestPalette.RetintWhite(page, rest: true, DarkRestPalette.CanvasColor);
		}
	}

	// Builds the page on screen again (after the look changed) at the same scroll position.
	private void RebuildCurrentPage()
	{
		if (_pageBuilder == null)
		{
			return;
		}
		double offset = CurrentOffset();
		_content.Animate = false;
		try
		{
			ShowPage(_pageBuilder);
		}
		finally
		{
			_content.Animate = true;
		}
		RestoreOffset(offset);
	}

	// Compatibility: the page of a category (old name or id) as a fresh, unparented ScrollViewer.
	private ScrollViewer BuildPage(string name)
	{
		string id = HubCatalog.Resolve(name) ?? "home";
		return BuildPageCore(id);
	}

	private ScrollViewer BuildPageCore(string id)
	{
		_buildGen++;
		_rowElements.Clear();
		_bindings.Clear();
		_uiSetters.Clear();
		_pageUndoRow = null;
		_defaultsPreview = null;
		_meterHost = null;
		_favoriteSet = null;
		_stars.Clear();
		_starHosts.Clear();
		QaPageBuilds++;
		ScrollViewer sv = id switch
		{
			"home" => HomePage(),
			"recent" => RecentPage(),
			"sound/apps" => SoundAppsPage(),
			"power/plans" => PowerPlansPage(),
			"account" => AccountPage(),
			"network" => NetworkPage(),
			"devices" => DevicesPage(),
			"sound" => SoundPage(),
			"displays" => DisplaysPage(),
			"power" => PowerPage(),
			"mouse" => MousePage(),
			"keyboard" => KeyboardPage(),
			"touchpad" => TouchpadPage(),
			"appearance" => AppearancePage(),
			"appearance/pointers" => PointersPage(),
			"appearance/frames" => FramesPage(),
			"appearance/seasonal" => SeasonalOptionsPage(),
			"appearance/rest" => RestOptionsPage(),
			"taskbar" => TaskbarPage(),
			"start" => StartPage(),
			"notifications" => NotificationsPage(),
			"accessibility" => AccessibilityPage(),
			"time" => TimePage(),
			"apps" => AppsPage(),
			"privacy" => PrivacyPage(),
			"about" => AboutPage(),
			"launcher" => LauncherPage(),
			"launcher/experiments" => ExperimentsPage(),
			"launcher/diagnostics" => DiagnosticsPage(),
			"tools" => ToolsPage(),
			_ when id.StartsWith("fix/", StringComparison.Ordinal) => FixPage(id),
			_ => HomePage()
		};
		KeyboardNavigation.SetTabIndex(sv, 3);
		return sv;
	}

	// Scrolls a row into view and draws a 2 DIP accent ring that fades out (search hits, deep links).
	private void RevealRow(string rowId)
	{
		base.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, (Action)delegate
		{
			if (!_rowElements.TryGetValue(rowId, out FrameworkElement? row) || !row.IsLoaded)
			{
				return;
			}
			row.BringIntoView(new Rect(0.0, -24.0, row.ActualWidth, row.ActualHeight + 48.0));
			AdornerLayer? layer = AdornerLayer.GetAdornerLayer(row);
			if (layer == null)
			{
				return;
			}
			RingAdorner ring = new RingAdorner(row, _pal.ToggleOn);
			layer.Add(ring);
			DoubleAnimation fade = new DoubleAnimation(1.0, 0.0, TimeSpan.FromMilliseconds(Motion.Enabled ? 1200 : 1))
			{
				BeginTime = TimeSpan.FromMilliseconds(300),
				EasingFunction = Motion.Linear
			};
			fade.Completed += delegate { layer.Remove(ring); };
			ring.BeginAnimation(UIElement.OpacityProperty, fade);
			if (row.Focusable)
			{
				row.Focus();
			}
		});
	}

	private sealed class RingAdorner : Adorner
	{
		private readonly Pen _pen;

		internal RingAdorner(UIElement e, Brush brush) : base(e)
		{
			_pen = new Pen(brush, 2.0);
			_pen.Freeze();
			IsHitTestVisible = false;
		}

		protected override void OnRender(DrawingContext dc)
		{
			Size s = AdornedElement.RenderSize;
			dc.DrawRectangle(null, _pen, new Rect(1.0, 1.0, Math.Max(0.0, s.Width - 2.0), Math.Max(0.0, s.Height - 2.0)));
		}
	}

	private void FocusContent()
	{
		if (_content.Child is not DependencyObject page)
		{
			return;
		}
		foreach (Control c in Descendants<Control>(page))
		{
			if (c.Focusable && c.IsEnabled && c.IsVisible && c is not ScrollViewer)
			{
				c.Focus();
				return;
			}
		}
	}

	internal static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
	{
		Stack<DependencyObject> stack = new Stack<DependencyObject>();
		stack.Push(root);
		HashSet<DependencyObject> seen = new HashSet<DependencyObject>();
		List<DependencyObject> ordered = new List<DependencyObject>();
		while (stack.Count > 0)
		{
			DependencyObject d = stack.Pop();
			if (!seen.Add(d))
			{
				continue;
			}
			if (d is T t)
			{
				yield return t;
			}
			int n = d is Visual || d is System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetChildrenCount(d) : 0;
			if (n == 0)
			{
				foreach (object child in LogicalTreeHelper.GetChildren(d))
				{
					if (child is DependencyObject dc)
					{
						ordered.Add(dc);
					}
				}
			}
			else
			{
				for (int i = 0; i < n; i++)
				{
					ordered.Add(VisualTreeHelper.GetChild(d, i));
				}
			}
			for (int i = ordered.Count - 1; i >= 0; i--)
			{
				stack.Push(ordered[i]);
			}
			ordered.Clear();
		}
	}

	// ---- Keyboard ---------------------------------------------------------------------------------------------------

	private static bool InTextField() => Keyboard.FocusedElement is TextBoxBase or PasswordBox;

	private void OnPreviewKey(object sender, KeyEventArgs e)
	{
		Key key = e.Key == Key.System ? e.SystemKey : e.Key;
		// Left on a row of the page goes back to the sidebar (not in a slider, a text field or a list); in a grid of tiles or
		// swatches it moves to the previous item first, and to the sidebar from the first one.
		if (key == Key.Left && Keyboard.Modifiers == ModifierKeys.None && _content.IsKeyboardFocusWithin && Keyboard.FocusedElement is ButtonBase and not RadioButton && !LeftStaysInGrid((ButtonBase)Keyboard.FocusedElement))
		{
			(_nav.SelectedItem as ListBoxItem)?.Focus();
			e.Handled = true;
			return;
		}
		if (HandleShortcut(key, Keyboard.Modifiers, InTextField(), Keyboard.FocusedElement is ComboBox))
		{
			e.Handled = true;
		}
	}

	// A tile or swatch of a wrapping grid (accent swatches, looks, frame modes, profiles, Fix a problem chips) with another
	// item of the same grid to its left: WPF's directional navigation takes Left there.
	internal static bool LeftStaysInGrid(ButtonBase focused)
	{
		WrapPanel? grid = null;
		DependencyObject? d = focused;
		for (int i = 0; i < 3 && d != null && grid == null; i++)
		{
			d = VisualTreeHelper.GetParent(d) ?? LogicalTreeHelper.GetParent(d);
			grid = d as WrapPanel;
		}
		if (grid == null)
		{
			return false;
		}
		return focused.PredictFocus(FocusNavigationDirection.Left) is DependencyObject next && next != focused && grid.IsAncestorOf(next);
	}

	// The window's shortcuts (QA calls it with the modifiers it wants): Ctrl+F and "/" search, Ctrl+1..9 the sidebar
	// entries, Alt+Left/Right and Backspace history, F6 the regions. true when the key was used.
	internal bool HandleShortcut(Key key, ModifierKeys mods, bool inText, bool inCombo = false)
	{
		bool ctrl = mods == ModifierKeys.Control;
		bool alt = mods == ModifierKeys.Alt;
		if (ctrl && key == Key.F)
		{
			FocusSearch();
			return true;
		}
		if (ctrl && key == Key.Z && !inText)
		{
			UndoLast();
			return true;
		}
		if (ctrl && key == Key.D && !inText)
		{
			// Favourites: stars or unstars the focused row
			ToggleFavoriteFocused();
			return true;
		}
		if (ctrl && key >= Key.D1 && key <= Key.D9)
		{
			List<string> visible = VisibleEntries();
			int i = key - Key.D1;
			if (i < visible.Count)
			{
				ClearSearch(keepPage: true);
				Navigate(visible[i], null, push: true);
			}
			return true;
		}
		if (alt && key == Key.Left)
		{
			Back();
			return true;
		}
		if (alt && key == Key.Right)
		{
			Forward();
			return true;
		}
		if (key == Key.F6)
		{
			CycleRegion((mods & ModifierKeys.Shift) != 0 ? -1 : 1);
			return true;
		}
		if (inText)
		{
			return false;
		}
		if (mods == ModifierKeys.None && key == Key.Back)
		{
			Back();
			return true;
		}
		if (mods == ModifierKeys.None && (key == Key.Divide || key == Key.OemQuestion) && !inCombo)
		{
			FocusSearch();
			return true;
		}
		return false;
	}

	// Typing a printable character anywhere outside a text field starts a search with it.
	private void OnPreviewText(object sender, TextCompositionEventArgs e)
	{
		if (InTextField() || Keyboard.FocusedElement is ComboBox || Keyboard.FocusedElement is ComboBoxItem)
		{
			return;
		}
		if ((Keyboard.Modifiers & (ModifierKeys.Control | ModifierKeys.Alt)) != 0)
		{
			return;
		}
		if (TypeToSearch(e.Text))
		{
			e.Handled = true;
		}
	}

	internal bool TypeToSearch(string? text)
	{
		if (string.IsNullOrEmpty(text))
		{
			return false;
		}
		char c = text[0];
		if (char.IsControl(c) || char.IsWhiteSpace(c) || c == '/')
		{
			return false;
		}
		FocusSearch();
		_search.Text += text;
		_search.CaretIndex = _search.Text.Length;
		return true;
	}

	// Escape: close a popup (the combo box does that itself), then clear the search, then leave a drill-in, then close.
	private void OnKeyBubble(object sender, KeyEventArgs e)
	{
		if (e.Key != Key.Escape || e.Handled)
		{
			return;
		}
		e.Handled = true;
		EscapeStep();
	}

	internal void EscapeStep()
	{
		if (_search.Text.Length > 0 || _searchActive)
		{
			ClearSearch(keepPage: true);
			return;
		}
		if (HubCatalog.Page(_current)?.Parent != null)
		{
			Back();
			return;
		}
		Close();
	}

	internal void FocusSearch()
	{
		_search.Focus();
		Keyboard.Focus(_search);
		_search.CaretIndex = _search.Text.Length;
	}

	private void CycleRegion(int dir)
	{
		UIElement[] regions = new UIElement[] { _search, _nav, _backBtn, _content };
		int at = 0;
		for (int i = 0; i < regions.Length; i++)
		{
			if (regions[i].IsKeyboardFocusWithin)
			{
				at = i;
			}
		}
		int next = (at + dir + regions.Length) % regions.Length;
		switch (next)
		{
		case 0:
			FocusSearch();
			break;
		case 1:
			(_nav.SelectedItem as ListBoxItem ?? _nav.Items.OfType<ListBoxItem>().FirstOrDefault())?.Focus();
			break;
		case 2:
			if (_backBtn.IsEnabled)
			{
				_backBtn.Focus();
			}
			else
			{
				FocusContent();
			}
			break;
		default:
			FocusContent();
			break;
		}
	}

	// ---- Showing ----------------------------------------------------------------------------------------------------

	// The legacy entry point (Start, charms, tray): the hub at its last or home page.
	public void ShowFullScreen()
	{
		ShowHub(null);
	}

	// Opens the window on the requested page (or row), with its bounds set before the first frame: centred on the
	// monitor under the pointer the first time, then where the user left it on that monitor; full screen when chosen.
	public void ShowHub(string? page, string? row = null)
	{
		AppSettings s = SettingsStore.FastSnapshot;
		_fullScreen = s.PcSettingsFullScreen;
		System.Windows.Forms.Screen screen = System.Windows.Forms.Screen.FromPoint(System.Windows.Forms.Cursor.Position);
		System.Drawing.Rectangle wa = TaskbarWorkArea.Current(screen);
		_assumedScale = MonitorDpi.ScaleFor(screen.Bounds);
		_screenName = screen.DeviceName;
		PlaceOn(wa, s);
		ApplySkin();
		LoadCaps();
		if (page != null || _current.Length == 0)
		{
			Navigate(HubCatalog.Resolve(page) ?? (_current.Length == 0 ? "home" : _current), row, push: false);
		}
		_root.Opacity = 0.0;
		Show();
		Activate();
		FixDpiAfterShow(wa, s);
		PlayOpen();
		if (_fullScreen)
		{
			TaskbarWindow.RaiseTaskbarLayoutChanged();
		}
		if (SettingsWorld.IsReal)
		{
			SystemSearchIndex.Ensure(synchronous: false, base.Dispatcher, OnIndexReady);
		}
		(_nav.SelectedItem as ListBoxItem)?.Focus();
	}

	private double _assumedScale = 1.0;

	private string _screenName = "";

	private void PlaceOn(System.Drawing.Rectangle wa, AppSettings s)
	{
		double k = _assumedScale <= 0.0 ? 1.0 : _assumedScale;
		if (_fullScreen)
		{
			base.ResizeMode = ResizeMode.NoResize;
			base.Left = wa.Left / k;
			base.Top = wa.Top / k;
			base.Width = wa.Width / k;
			base.Height = wa.Height / k;
			return;
		}
		base.ResizeMode = ResizeMode.CanResize;
		(System.Drawing.Rectangle Rect, bool Max)? saved = SavedBounds(s.PcSettingsBounds, _screenName);
		if (saved != null && saved.Value.Rect.IntersectsWith(wa))
		{
			System.Drawing.Rectangle r = saved.Value.Rect;
			base.Left = r.Left / k;
			base.Top = r.Top / k;
			base.Width = Math.Max(base.MinWidth, r.Width / k);
			base.Height = Math.Max(base.MinHeight, r.Height / k);
			if (saved.Value.Max)
			{
				base.WindowState = WindowState.Maximized;
			}
			return;
		}
		double waW = wa.Width / k;
		double waH = wa.Height / k;
		double w = Math.Min(980.0, waW);
		double h = Math.Min(760.0, Math.Floor(waH * 0.9));
		base.Width = Math.Max(base.MinWidth, w);
		base.Height = Math.Max(Math.Min(base.MinHeight, waH), h);
		base.Left = wa.Left / k + (waW - base.Width) / 2.0;
		base.Top = wa.Top / k + (waH - base.Height) / 2.0;
	}

	// On a monitor whose scale differs from what the window was created with, WPF reports the real scale only once the
	// window exists: the bounds are set again with it.
	private void FixDpiAfterShow(System.Drawing.Rectangle wa, AppSettings s)
	{
		try
		{
			double real = PresentationSource.FromVisual(this)?.CompositionTarget?.TransformToDevice.M11 ?? _assumedScale;
			if (real > 0.0 && Math.Abs(real - _assumedScale) > 0.01)
			{
				_assumedScale = real;
				PlaceOn(wa, s);
			}
		}
		catch
		{
		}
	}

	// "\\.\DISPLAY1|x,y,w,h,max;..." (physical pixels).
	internal static (System.Drawing.Rectangle Rect, bool Max)? SavedBounds(string? all, string device)
	{
		if (string.IsNullOrEmpty(all) || string.IsNullOrEmpty(device))
		{
			return null;
		}
		foreach (string entry in all.Split(';', StringSplitOptions.RemoveEmptyEntries))
		{
			int bar = entry.IndexOf('|');
			if (bar <= 0 || !string.Equals(entry.Substring(0, bar), device, StringComparison.OrdinalIgnoreCase))
			{
				continue;
			}
			string[] n = entry.Substring(bar + 1).Split(',');
			if (n.Length >= 4 && int.TryParse(n[0], out int x) && int.TryParse(n[1], out int y) && int.TryParse(n[2], out int w) && int.TryParse(n[3], out int h) && w > 200 && h > 200)
			{
				bool max = n.Length >= 5 && n[4] == "1";
				return (new System.Drawing.Rectangle(x, y, w, h), max);
			}
		}
		return null;
	}

	internal static string WithBounds(string? all, string device, System.Drawing.Rectangle r, bool max)
	{
		List<string> keep = new List<string>();
		foreach (string entry in (all ?? string.Empty).Split(';', StringSplitOptions.RemoveEmptyEntries))
		{
			int bar = entry.IndexOf('|');
			if (bar > 0 && !string.Equals(entry.Substring(0, bar), device, StringComparison.OrdinalIgnoreCase))
			{
				keep.Add(entry);
			}
		}
		keep.Add($"{device}|{r.Left},{r.Top},{r.Width},{r.Height},{(max ? 1 : 0)}");
		while (keep.Count > 4)
		{
			keep.RemoveAt(0);
		}
		return string.Join(";", keep);
	}

	private void SaveBounds()
	{
		if (_fullScreen || SettingsStore.ReadOnlyDiagnostics || _screenName.Length == 0)
		{
			return;
		}
		try
		{
			Rect rb = base.WindowState == WindowState.Normal ? new Rect(base.Left, base.Top, base.ActualWidth, base.ActualHeight) : base.RestoreBounds;
			double k = _assumedScale <= 0.0 ? 1.0 : _assumedScale;
			System.Drawing.Rectangle px = new System.Drawing.Rectangle((int)Math.Round(rb.Left * k), (int)Math.Round(rb.Top * k), (int)Math.Round(rb.Width * k), (int)Math.Round(rb.Height * k));
			bool max = base.WindowState == WindowState.Maximized;
			string device = _screenName;
			SettingsStore.Update(delegate(AppSettings x)
			{
				x.PcSettingsBounds = WithBounds(x.PcSettingsBounds, device, px, max);
			});
		}
		catch (Exception ex)
		{
			Logger.Log("PC settings: saving the window bounds failed: " + ex.Message);
		}
	}

	// The first frame: the content fades in (120 ms) with an 8 DIP rise. Nothing else.
	private void PlayOpen()
	{
		_root.Opacity = 1.0;
		if (!Motion.Enabled)
		{
			_root.RenderTransform = null;
			return;
		}
		TranslateTransform rise = new TranslateTransform(0.0, Motion.Distance(8.0));
		_root.RenderTransform = rise;
		rise.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(Motion.Distance(8.0), 0.0, Motion.Dur(Motion.Tok.FadeIn)) { EasingFunction = Motion.Decelerate });
		_root.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0.0, 1.0, Motion.Dur(Motion.Tok.FadeIn)) { EasingFunction = Motion.Decelerate });
	}

	// Show desktop (Win+D, the switcher's desktop button): only a full-screen hub gets out of the way.
	internal void CloseForShowDesktop()
	{
		if (_fullScreen)
		{
			Close();
		}
	}

	// ---- Capabilities -----------------------------------------------------------------------------------------------

	private HubCaps? _caps;

	private static HubCaps? _capsCache;

	// The capabilities read the last time PC settings opened (the Windows 7 Start menu's PC settings rows follow them);
	// null until then.
	internal static HubCaps? LastCaps => _capsCache;

	private static DateTime _capsAt;

	private static string _capsWorld = "";

	private static HubCaps? CachedCaps()
	{
		return _capsCache != null && DateTime.UtcNow - _capsAt < TimeSpan.FromMinutes(10) && _capsWorld == SettingsWorld.Current.Name ? _capsCache : null;
	}

	// Reads what this PC has (off the UI thread); the sidebar and the page shown follow once it arrives.
	private void LoadCaps()
	{
		HubCaps? cached = CachedCaps();
		if (cached != null)
		{
			ApplyCaps(cached);
			return;
		}
		ISettingsWorld world = SettingsWorld.Current;
		_pending++;
		Task.Run(world.ReadCaps).ContinueWith(delegate(Task<HubCaps> t)
		{
			base.Dispatcher.BeginInvoke((Action)delegate
			{
				_pending = Math.Max(0, _pending - 1);
				if (t.Status == TaskStatus.RanToCompletion && t.Result != null)
				{
					_capsCache = t.Result;
					_capsAt = DateTime.UtcNow;
					_capsWorld = world.Name;
					ApplyCaps(t.Result);
				}
			});
		}, TaskScheduler.Default);
	}

	private void ApplyCaps(HubCaps caps)
	{
		bool first = _caps == null || !ReferenceEquals(_caps, caps);
		_caps = caps;
		RefreshSidebarVisibility();
		if (!_searchActive && _current.Length > 0)
		{
			_pageTitle.Text = PageTitle(_current);
		}
		if (first && !_closed && !_searchActive && _pageBuilder != null)
		{
			RebuildCurrentPage();
		}
		else if (first && !_closed && _searchActive)
		{
			// the results follow what this PC has (rows that need hardware come and go)
			RunSearch(_search.Text);
		}
	}

	// QA: the capabilities a fixture world reports, applied at once.
	internal void QaUseCaps(HubCaps caps)
	{
		_capsCache = caps;
		_capsAt = DateTime.UtcNow;
		_capsWorld = SettingsWorld.Current.Name;
		_caps = caps;
		RefreshSidebarVisibility();
	}

	internal HubCaps? Caps => _caps;

	// ---- Lifetime ---------------------------------------------------------------------------------------------------

	private bool _themeSubscribed;

	protected override void OnSourceInitialized(EventArgs e)
	{
		base.OnSourceInitialized(e);
		_hwnd = new WindowInteropHelper(this).Handle;
		LiveHwnd = _hwnd;
		HwndSource.FromHwnd(_hwnd)?.AddHook(WndProc);
		if (!_themeSubscribed)
		{
			_themeSubscribed = true;
			ShellTheme.Changed += OnThemeChanged;
		}
		if (!_journalSubscribed)
		{
			_journalSubscribed = true;
			SettingsJournal.Changed += OnJournalChanged;
		}
	}

	protected override void OnActivated(EventArgs e)
	{
		base.OnActivated(e);
		UpdateMeter();
	}

	protected override void OnDeactivated(EventArgs e)
	{
		base.OnDeactivated(e);
		UpdateMeter();
	}

	protected override void OnClosed(EventArgs e)
	{
		_closed = true;
		SaveBounds();
		if (LiveHwnd == _hwnd)
		{
			LiveHwnd = IntPtr.Zero;
		}
		if (_themeSubscribed)
		{
			_themeSubscribed = false;
			ShellTheme.Changed -= OnThemeChanged;
		}
		if (_fullScreen)
		{
			TaskbarWindow.RaiseTaskbarLayoutChanged();
		}
		StopSearchTimers();
		_pointerPreviewCache.Clear();
		if (_journalSubscribed)
		{
			_journalSubscribed = false;
			SettingsJournal.Changed -= OnJournalChanged;
		}
		// a keyboard step persisted 250 ms later: persisted now
		_persistTimer?.Stop();
		if (_persistPending?.LiveValue is string pendingValue)
		{
			KnobBinding pending = _persistPending;
			_persistPending = null;
			Commit(pending, pendingValue);
		}
		_persistTimer = null;
		_undoTimer?.Stop();
		_undoTimer = null;
		// closing during "Keep this change?" puts the change back
		if (_guardChange != null)
		{
			GuardRevert();
		}
		CloseGuard();
		_guardTimer = null;
		_meterHost = null;
		_meterTimer?.Stop();
		_meterTimer = null;
		foreach (IDisposable sub in _liveSubs.ToList())
		{
			try
			{
				sub.Dispose();
			}
			catch
			{
			}
		}
		_liveSubs.Clear();
		// An enabled DispatcherTimer is rooted by the dispatcher: left running, it kept this closed window (and its whole
		// tree) alive.
		_compStatusTimer?.Stop();
		_compStatusTimer = null;
		if (_compStatus != null)
		{
			_compStatus.IsVisibleChanged -= OnCompStatusVisibleChanged;
			_compStatus = null;
		}
		ReleaseToolsIcons();
		base.OnClosed(e);
	}

	// A borderless window maximizes onto the work area (the launcher's taskbar stays visible).
	private nint WndProc(nint hwnd, int msg, nint wParam, nint lParam, ref bool handled)
	{
		if (msg == 0x0024)
		{
			try
			{
				System.Windows.Forms.Screen screen = System.Windows.Forms.Screen.FromHandle(hwnd);
				System.Drawing.Rectangle wa = TaskbarWorkArea.Current(screen);
				System.Drawing.Rectangle mon = screen.Bounds;
				MINMAXINFO mmi = Marshal.PtrToStructure<MINMAXINFO>(lParam);
				mmi.ptMaxPosition.X = wa.Left - mon.Left;
				mmi.ptMaxPosition.Y = wa.Top - mon.Top;
				mmi.ptMaxSize.X = wa.Width;
				mmi.ptMaxSize.Y = wa.Height;
				Marshal.StructureToPtr(mmi, lParam, fDeleteOld: false);
			}
			catch
			{
			}
		}
		return IntPtr.Zero;
	}

	[StructLayout(LayoutKind.Sequential)]
	private struct POINT
	{
		public int X;

		public int Y;
	}

	[StructLayout(LayoutKind.Sequential)]
	private struct MINMAXINFO
	{
		public POINT ptReserved;

		public POINT ptMaxSize;

		public POINT ptMaxPosition;

		public POINT ptMinTrackSize;

		public POINT ptMaxTrackSize;
	}

	// ---- Shared ---------------------------------------------------------------------------------------------------

	// The running shell, so launcher toggles can be applied live through App-private objects (see App.ApplyXxx).
	private static App? HostApp => Application.Current as App;

	internal static ImageSource WhiteGear()
	{
		// The launcher's one canonical Settings cog (SettingsGlyph), crisp at every size.
		return SettingsGlyph.Gear();
	}

	private static ImageSource? _gearIcon;

	private static string _gearAccent = "";

	// The 64 px accent tile with the white cog (window and taskbar icon), rendered once per accent colour.
	internal static ImageSource BuildGearIcon()
	{
		Color accent;
		try
		{
			accent = StartAccent.Color();
		}
		catch
		{
			accent = Color.FromRgb(81, 43, 212);
		}
		string key = accent.ToString(CultureInfo.InvariantCulture);
		if (_gearIcon != null && _gearAccent == key)
		{
			return _gearIcon;
		}
		Image img = new Image
		{
			Source = WhiteGear(),
			Stretch = Stretch.Uniform,
			Width = 46.0,
			Height = 46.0,
			HorizontalAlignment = HorizontalAlignment.Center,
			VerticalAlignment = VerticalAlignment.Center
		};
		RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);
		Border host = new Border
		{
			Width = 64.0,
			Height = 64.0,
			Background = new SolidColorBrush(accent),
			Child = img
		};
		host.Measure(new Size(64.0, 64.0));
		host.Arrange(new Rect(0.0, 0.0, 64.0, 64.0));
		host.UpdateLayout();
		RenderTargetBitmap rtb = new RenderTargetBitmap(64, 64, 96.0, 96.0, PixelFormats.Pbgra32);
		rtb.Render(host);
		rtb.Freeze();
		_gearIcon = rtb;
		_gearAccent = key;
		return rtb;
	}

	internal static ImageSource BuildGearGlyph(int size = 48)
	{
		return WhiteGear();
	}

	// ---- QA ---------------------------------------------------------------------------------------------------------

	// Renders the window at w x h on the given category (old names work) into a PNG. A realized HwndSource is needed
	// for nested ScrollViewer content to render, so the (offscreen) window is shown without activation.
	public void QaRender(string outPath, string category = "PC & devices", double w = 1366.0, double h = 768.0)
	{
		base.Width = w;
		base.Height = h;
		SyncRestSkin();
		ApplySkin();
		if (!base.IsVisible)
		{
			base.ShowActivated = false;
			Show();
		}
		_root.Opacity = 1.0;
		_root.RenderTransform = null;
		_content.Animate = false;
		try
		{
			ClearSearch(keepPage: true);
			string id = HubCatalog.Resolve(category) ?? "home";
			Navigate(id, null, push: false);
		}
		finally
		{
			_content.Animate = true;
		}
		QaWaitReads();
		if (_content.Child is UIElement page)
		{
			page.BeginAnimation(UIElement.OpacityProperty, null);
			page.Opacity = 1.0;
			page.RenderTransform = null;
		}
		_root.ApplyTemplate();
		_root.Measure(new Size(w, h));
		_root.Arrange(new Rect(0.0, 0.0, w, h));
		_root.UpdateLayout();
		SavePng(_root, (int)w, (int)h, outPath);
	}

	// QA / perf probe: one build of a page, in milliseconds (the page is not shown).
	internal double QaTimeBuild(string id)
	{
		System.Diagnostics.Stopwatch sw = System.Diagnostics.Stopwatch.StartNew();
		BuildPageCore(id);
		sw.Stop();
		return sw.Elapsed.TotalMilliseconds;
	}

	// QA: the window as it is now (no navigation), laid out at w x h.
	internal void QaSnapshot(string outPath, double w, double h)
	{
		QaWaitReads();
		QaEndAnimations();
		if (_content.Child is UIElement page)
		{
			page.BeginAnimation(UIElement.OpacityProperty, null);
			page.Opacity = 1.0;
			page.RenderTransform = null;
		}
		_root.Measure(new Size(w, h));
		_root.Arrange(new Rect(0.0, 0.0, w, h));
		_root.UpdateLayout();
		SavePng(_root, (int)w, (int)h, outPath);
	}

	// The full height of a page's body at width w (an unparented build with this window's resources).
	public void QaRenderTall(string outPath, string category, double w = 900.0)
	{
		SyncRestSkin();
		ScrollViewer scr = BuildPage(category);
		QaPrepareDetached(scr);
		QaWaitReads();
		scr.VerticalScrollBarVisibility = ScrollBarVisibility.Disabled;
		scr.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
		scr.Width = w;
		scr.Measure(new Size(w, double.PositiveInfinity));
		double hh = Math.Min(8000.0, Math.Max(400.0, scr.DesiredSize.Height));
		scr.Arrange(new Rect(0.0, 0.0, w, hh));
		scr.UpdateLayout();
		SavePng(scr, (int)w, (int)hh, outPath);
	}

	// An unparented page takes this window's control styles and look resources (and the Dark rest retint).
	private void QaPrepareDetached(FrameworkElement page)
	{
		foreach (object key in base.Resources.Keys)
		{
			page.Resources[key] = base.Resources[key];
		}
		foreach (ResourceDictionary merged in base.Resources.MergedDictionaries)
		{
			page.Resources.MergedDictionaries.Add(merged);
		}
		if (DarkRest.Visuals)
		{
			DarkRestPalette.RetintWhite(page, rest: true, DarkRestPalette.CanvasColor);
		}
	}

	internal static void SavePng(Visual v, int w, int h, string path)
	{
		RenderTargetBitmap rtb = new RenderTargetBitmap(Math.Max(1, w), Math.Max(1, h), 96.0, 96.0, PixelFormats.Pbgra32);
		rtb.Render(v);
		PngBitmapEncoder enc = new PngBitmapEncoder();
		enc.Frames.Add(BitmapFrame.Create(rtb));
		using FileStream fs = File.Create(path);
		enc.Save(fs);
	}

	internal HubPalette QaPalette => _pal;

	internal FrameworkElement QaRoot => _root;

	internal ListBox QaNav => _nav;

	internal TextBox QaSearchBox => _search;

	internal Border QaSearchHost => _searchBox;

	internal TextBlock QaSearchCue => _searchCue;

	// The "Keep this change?" banner is on screen (in the window, above the page).
	internal bool QaGuardVisible => _guard != null && _guard.Parent != null && _guard.IsVisible;

	// QA: builds a page the way a search top hit or a deep link does (not shown), with its async reads arrived.
	internal ScrollViewer QaBuildPage(string id)
	{
		ScrollViewer sv = BuildPageCore(id);
		QaWaitReads();
		return sv;
	}

	internal UIElement? QaPageElement => _content.Child;

	internal int QaHistory => _back.Count;

	internal int QaForward => _forward.Count;

	internal int QaPending => _pending;
}
