using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

#nullable enable annotations

namespace Win81Layer;

// The colours of PC Settings for the look that is on (Normal light or dark, Dark rest, a season, Win7 glass, high
// contrast): computed when the window opens and again when the look changes. Every brush is frozen.
internal sealed class HubPalette
{
	internal bool Dark;
	internal bool Rest;
	internal bool Seasonal;
	internal bool Glass;
	internal bool HighContrast;

	internal Brush Rail = Brushes.Black;
	internal Brush? RailEdge;
	internal Brush RailInk = Brushes.White;
	internal Brush RailInk2 = Brushes.White;
	internal Brush Selected = Brushes.Transparent;
	internal Brush? SelectedBar;
	internal Brush Hover = Brushes.Transparent;
	internal Brush Page = Brushes.White;
	internal Brush Card = Brushes.White;
	internal Brush CardBorder = Brushes.Gray;
	internal Brush Separator = Brushes.Gray;
	internal Brush Ink = Brushes.Black;
	internal Brush Ink2 = Brushes.Gray;
	internal Brush Link = Brushes.Blue;
	internal Brush RowHover = Brushes.Transparent;
	internal Brush Pressed = Brushes.Transparent;
	internal Brush Field = Brushes.White;
	internal Brush ToggleOn = Brushes.Blue;
	internal Brush Error = Brushes.Red;
	internal Brush SearchFill = Brushes.Transparent;
	internal Brush SearchCue = Brushes.White;
	internal Brush MiniGlyph = Brushes.White;
	internal Brush CaptionHover = Brushes.Transparent;
	internal Brush CaptionPress = Brushes.Transparent;
	internal Brush SliderFill = Brushes.Blue;
	internal Brush ScrollThumb = Brushes.Gray;
	internal Brush ScrollThumbHover = Brushes.Gray;
	internal Brush RailScrollThumb = Brushes.Gray;
	internal Brush RailScrollThumbHover = Brushes.Gray;
	internal Color SliderFillColor;
	internal Color SliderTrackColor;

	internal Color RailColor;
	internal Color PageColor;
	internal Color CardColor;
	internal Color InkColor;
	internal Color Ink2Color;
	internal Color LinkColor;
	internal Color ToggleOnColor;
	internal Color FieldColor;
	internal Color RailInkColor;
	internal Color AccentColor;

	private readonly Dictionary<string, Brush> _tiles = new Dictionary<string, Brush>(StringComparer.OrdinalIgnoreCase);

	internal static readonly Color White = Colors.White;

	// The mini-tile of a sidebar entry or hero: the table colour (darkened until the white glyph reads at 3:1), the
	// Launcher's accent tone, Dark rest's one muted accent fill, or an outline in high contrast.
	internal Brush MiniTile(string tile)
	{
		if (_tiles.TryGetValue(tile, out Brush? b))
		{
			return b;
		}
		Color c;
		if (Rest)
		{
			c = DarkRestPalette.AccentFillColor;
		}
		else if (HighContrast)
		{
			c = Colors.Transparent;
		}
		else if (tile == HubCatalog.LauncherTile)
		{
			c = UntilContrast(SettingsPane.AccentToneColor(0.46), White, 3.0, darker: true);
		}
		else
		{
			c = UntilContrast(ParseHex(tile, Colors.SteelBlue), White, 3.0, darker: true);
		}
		b = Frozen(c);
		_tiles[tile] = b;
		return b;
	}

	internal Color MiniTileColor(string tile) => (MiniTile(tile) as SolidColorBrush)?.Color ?? Colors.Transparent;

	internal Color MiniGlyphColor => (MiniGlyph as SolidColorBrush)?.Color ?? Colors.White;

	private ControlTemplate? _rowTemplate;

	// The template of a row that acts on a click (link, drill-in, button rows): a square wash on hover and press.
	internal ControlTemplate RowTemplate => _rowTemplate ??= BuildRowTemplate();

	private ControlTemplate BuildRowTemplate()
	{
		ControlTemplate t = new ControlTemplate(typeof(Button));
		FrameworkElementFactory bd = new FrameworkElementFactory(typeof(Border), "Bd");
		bd.SetValue(Border.BackgroundProperty, Brushes.Transparent);
		bd.SetValue(UIElement.SnapsToDevicePixelsProperty, true);
		FrameworkElementFactory cp = new FrameworkElementFactory(typeof(ContentPresenter));
		cp.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Stretch);
		cp.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Stretch);
		bd.AppendChild(cp);
		t.VisualTree = bd;
		Trigger hover = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
		hover.Setters.Add(new Setter(Border.BackgroundProperty, RowHover, "Bd"));
		t.Triggers.Add(hover);
		Trigger press = new Trigger { Property = ButtonBase.IsPressedProperty, Value = true };
		press.Setters.Add(new Setter(Border.BackgroundProperty, Pressed, "Bd"));
		t.Triggers.Add(press);
		Trigger off = new Trigger { Property = UIElement.IsEnabledProperty, Value = false };
		off.Setters.Add(new Setter(UIElement.OpacityProperty, 0.55));
		t.Triggers.Add(off);
		t.Seal();
		return t;
	}

	private ControlTemplate? _plainTemplate;

	// A focusable button that draws only its content (info button, caption toolbar buttons get their own).
	internal ControlTemplate PlainTemplate => _plainTemplate ??= BuildPlainTemplate();

	private static ControlTemplate BuildPlainTemplate()
	{
		ControlTemplate t = new ControlTemplate(typeof(Button));
		FrameworkElementFactory bd = new FrameworkElementFactory(typeof(Border), "Bd");
		bd.SetValue(Border.BackgroundProperty, Brushes.Transparent);
		FrameworkElementFactory cp = new FrameworkElementFactory(typeof(ContentPresenter));
		cp.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
		cp.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
		bd.AppendChild(cp);
		t.VisualTree = bd;
		t.Seal();
		return t;
	}

	private ControlTemplate? _captionTemplate;

	internal ControlTemplate CaptionTemplate(bool close)
	{
		if (!close)
		{
			return _captionTemplate ??= BuildCaptionTemplate(CaptionHover, CaptionPress, null);
		}
		return _closeTemplate ??= BuildCaptionTemplate(Frozen(Color.FromRgb(0xE8, 0x11, 0x23)), Frozen(Color.FromRgb(0xF1, 0x70, 0x7A)), Brushes.White);
	}

	private ControlTemplate? _closeTemplate;

	private static ControlTemplate BuildCaptionTemplate(Brush hover, Brush press, Brush? hoverInk)
	{
		ControlTemplate t = new ControlTemplate(typeof(Button));
		FrameworkElementFactory bd = new FrameworkElementFactory(typeof(Border), "Bd");
		bd.SetValue(Border.BackgroundProperty, Brushes.Transparent);
		FrameworkElementFactory cp = new FrameworkElementFactory(typeof(ContentPresenter));
		cp.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
		cp.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
		bd.AppendChild(cp);
		t.VisualTree = bd;
		Trigger h = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
		h.Setters.Add(new Setter(Border.BackgroundProperty, hover, "Bd"));
		if (hoverInk != null)
		{
			h.Setters.Add(new Setter(Control.ForegroundProperty, hoverInk));
		}
		t.Triggers.Add(h);
		Trigger p = new Trigger { Property = ButtonBase.IsPressedProperty, Value = true };
		p.Setters.Add(new Setter(Border.BackgroundProperty, press, "Bd"));
		t.Triggers.Add(p);
		Trigger off = new Trigger { Property = UIElement.IsEnabledProperty, Value = false };
		off.Setters.Add(new Setter(UIElement.OpacityProperty, 0.35));
		t.Triggers.Add(off);
		t.Seal();
		return t;
	}

	internal static HubPalette Compute()
	{
		HubPalette p = new HubPalette();
		p.HighContrast = SystemParameters.HighContrast;
		p.Rest = DarkRest.Visuals;
		p.Glass = ShellSkin.GlassOn;
		bool windowsDark = ShellTheme.IsDark;
		Color themeBase;
		Color accent;
		bool seasonal;
		try
		{
			FlyoutPalette.ReadTheme(out themeBase, out accent, out seasonal);
		}
		catch
		{
			accent = Color.FromRgb(0x2D, 0x7D, 0x9A);
			themeBase = accent;
			seasonal = false;
		}
		p.Seasonal = seasonal && !p.Rest && SeasonalTheme.IsSeason(SeasonalTheme.NormalizeId(SettingsStore.FastSnapshot.SeasonalActive));
		p.Dark = p.Rest || windowsDark;
		p.AccentColor = accent;
		if (p.HighContrast)
		{
			p.FillHighContrast();
			return p;
		}
		if (p.Rest)
		{
			p.FillRest();
			return p;
		}
		// The page family follows Windows light or dark in Normal, a season and Win7 glass.
		if (p.Dark)
		{
			p.PageColor = Hex("#1B1B1B");
			p.CardColor = Hex("#262626");
			p.Card = Frozen(p.CardColor);
			p.CardBorder = Frozen(Hex("#333333"));
			p.Separator = Frozen(Hex("#2F2F2F"));
			p.InkColor = Hex("#F2F2F2");
			p.Ink2Color = Hex("#ABABAB");
			p.RowHover = Frozen(Hex("#2E2E2E"));
			p.Pressed = Frozen(Hex("#363636"));
			p.Error = Frozen(Hex("#FF99A4"));
			p.CaptionHover = Frozen(Color.FromArgb(0x1A, 0xFF, 0xFF, 0xFF));
			p.CaptionPress = Frozen(Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF));
		}
		else
		{
			p.PageColor = Hex("#F3F3F3");
			p.CardColor = Hex("#FFFFFF");
			p.Card = Frozen(p.CardColor);
			p.CardBorder = Frozen(Hex("#E3E3E3"));
			p.Separator = Frozen(Hex("#EDEDED"));
			p.InkColor = Hex("#1A1A1A");
			p.Ink2Color = Hex("#5F5F5F");
			p.RowHover = Frozen(Hex("#F5F5F5"));
			p.Pressed = Frozen(Hex("#EBEBEB"));
			p.Error = Frozen(Hex("#C42B1C"));
			p.CaptionHover = Frozen(Color.FromArgb(0x1A, 0x00, 0x00, 0x00));
			p.CaptionPress = Frozen(Color.FromArgb(0x33, 0x00, 0x00, 0x00));
		}
		p.Page = Frozen(p.PageColor);
		p.Ink = Frozen(p.InkColor);
		p.Ink2 = Frozen(p.Ink2Color);
		p.FieldColor = TokenColor("Metro81.Surface", p.Dark ? Hex("#1F1F1F") : Colors.White);
		p.Field = Frozen(p.FieldColor);
		// The rail: the accent tone (or the season's base), with white ink at 4.5:1 or more.
		Color rail;
		if (p.Seasonal)
		{
			rail = FlyoutPalette.Tone(themeBase, 0.0, 0.0, 0.26);
		}
		else if (p.Dark)
		{
			rail = FlyoutPalette.Tone(accent, 0.0, 0.0, 0.26);
		}
		else
		{
			rail = SettingsPane.AccentToneColor(0.32);
		}
		rail = UntilContrast(rail, White, 4.5, darker: true);
		p.RailColor = rail;
		if (p.Glass)
		{
			Color top = UntilContrast(SettingsPane.AccentToneColor(0.35), White, 4.5, darker: true);
			Color bottom = UntilContrast(SettingsPane.AccentToneColor(0.26), White, 4.5, darker: true);
			LinearGradientBrush g = new LinearGradientBrush(top, bottom, 90.0);
			g.Freeze();
			p.Rail = g;
			p.RailColor = top;
		}
		else
		{
			p.Rail = Frozen(rail);
		}
		p.RailInkColor = Colors.White;
		p.RailInk = Brushes.White;
		p.RailInk2 = Frozen(Color.FromArgb(0xBF, 0xFF, 0xFF, 0xFF));
		p.Selected = Frozen(Color.FromArgb(0x2E, 0xFF, 0xFF, 0xFF));
		p.Hover = Frozen(Color.FromArgb(0x1A, 0xFF, 0xFF, 0xFF));
		p.SearchFill = Frozen(Color.FromArgb(0x24, 0xFF, 0xFF, 0xFF));
		p.SearchCue = Frozen(Color.FromArgb(0xB3, 0xFF, 0xFF, 0xFF));
		p.MiniGlyph = Brushes.White;
		// Links: a deep (light page) or lifted (dark page) tone of the accent, 4.5:1 on the card; a season keeps its own
		// accent hue.
		Color linkSeed = p.Seasonal ? accent : accent;
		Color link = p.Dark ? FlyoutPalette.Tone(linkSeed, 0.0, 0.5, 0.72) : FlyoutPalette.Tone(linkSeed, 0.0, 0.5, 0.38);
		link = UntilContrast(link, p.CardColor, 4.5, darker: !p.Dark);
		p.LinkColor = link;
		p.Link = Frozen(link);
		// Toggles that are on: the accent, 3:1 against the Off fill (the field colour).
		Color on = UntilContrast(accent, p.FieldColor, 3.0, darker: !p.Dark);
		p.ToggleOnColor = on;
		p.ToggleOn = Frozen(on);
		// Slider fills: the same accent, 3:1 against the slider's own track (one palette source for every control).
		p.SliderTrackColor = TokenColor("Metro81.SliderTrack", p.Dark ? Hex("#5A5A5A") : Hex("#C6C6C6"));
		p.SliderFillColor = UntilContrast(on, p.SliderTrackColor, 3.0, darker: !p.Dark);
		p.SliderFill = Frozen(p.SliderFillColor);
		p.ScrollThumb = Frozen(Color.FromArgb(0x73, p.Ink2Color.R, p.Ink2Color.G, p.Ink2Color.B));
		p.ScrollThumbHover = Frozen(Color.FromArgb(0xB3, p.Ink2Color.R, p.Ink2Color.G, p.Ink2Color.B));
		p.RailScrollThumb = Frozen(Color.FromArgb(0x59, 0xFF, 0xFF, 0xFF));
		p.RailScrollThumbHover = Frozen(Color.FromArgb(0x8C, 0xFF, 0xFF, 0xFF));
		return p;
	}

	private void FillRest()
	{
		RailColor = DarkRestPalette.PanelColor;
		Rail = DarkRestPalette.Panel;
		RailEdge = Frozen(Hex("#262626"));
		RailInkColor = DarkRestPalette.InkColor;
		RailInk = DarkRestPalette.Ink;
		RailInk2 = Frozen(Hex("#AAAAAA"));
		// The selected entry: the muted accent (3:1 on the black pane, the soft ink 4.7:1 on it) with the accent line.
		Selected = DarkRestPalette.NavSelected;
		SelectedBar = DarkRestPalette.AccentLine;
		Hover = DarkRestPalette.HoverWash;
		PageColor = DarkRestPalette.CanvasColor;
		Page = DarkRestPalette.Canvas;
		CardColor = DarkRestPalette.CardColor;
		Card = Frozen(CardColor);
		CardBorder = Frozen(Hex("#262626"));
		Separator = Frozen(Hex("#262626"));
		InkColor = DarkRestPalette.InkColor;
		Ink = DarkRestPalette.Ink;
		Ink2Color = DarkRestPalette.Ink2Color;
		Ink2 = Frozen(Ink2Color);
		LinkColor = DarkRestPalette.AccentInkColor;
		Link = DarkRestPalette.AccentInk;
		RowHover = Frozen(DarkRestPalette.HoverColor);
		Pressed = Frozen(DarkRestPalette.PressedColor);
		FieldColor = DarkRestPalette.FieldColor;
		Field = Frozen(FieldColor);
		ToggleOnColor = DarkRestPalette.AccentFillColor;
		ToggleOn = DarkRestPalette.Token("MenuAccent") ?? Frozen(ToggleOnColor);
		Error = Frozen(Hex("#E0A0A0"));
		SearchFill = Frozen(Hex("#141414"));
		SearchCue = Frozen(Hex("#888888"));
		MiniGlyph = Frozen(Hex("#EEEEEE"));
		CaptionHover = Frozen(Color.FromArgb(0x28, 0xFF, 0xFF, 0xFF));
		CaptionPress = Frozen(Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF));
		// the Dark rest control family's own slider (its overlay writes the same tokens)
		SliderTrackColor = (DarkRestPalette.Token("Metro81.SliderTrack") as SolidColorBrush)?.Color ?? Hex("#3A3A3A");
		SliderFillColor = (DarkRestPalette.Token("Metro81.SliderFill") as SolidColorBrush)?.Color ?? Hex("#6798A2");
		SliderFill = Frozen(SliderFillColor);
		ScrollThumb = Frozen(Color.FromArgb(0x73, Ink2Color.R, Ink2Color.G, Ink2Color.B));
		ScrollThumbHover = Frozen(Color.FromArgb(0xB3, Ink2Color.R, Ink2Color.G, Ink2Color.B));
		RailScrollThumb = ScrollThumb;
		RailScrollThumbHover = ScrollThumbHover;
	}

	private void FillHighContrast()
	{
		Color window = SystemColors.WindowColor;
		Color text = SystemColors.WindowTextColor;
		RailColor = window;
		Rail = SystemColors.WindowBrush;
		RailEdge = SystemColors.WindowTextBrush;
		RailInkColor = text;
		RailInk = SystemColors.WindowTextBrush;
		RailInk2 = SystemColors.WindowTextBrush;
		Selected = SystemColors.HighlightBrush;
		Hover = SystemColors.HighlightBrush;
		PageColor = window;
		Page = SystemColors.WindowBrush;
		CardColor = window;
		Card = SystemColors.WindowBrush;
		CardBorder = SystemColors.WindowTextBrush;
		Separator = SystemColors.WindowTextBrush;
		InkColor = text;
		Ink = SystemColors.WindowTextBrush;
		Ink2Color = text;
		Ink2 = SystemColors.WindowTextBrush;
		LinkColor = SystemColors.HotTrackColor;
		Link = SystemColors.HotTrackBrush;
		RowHover = SystemColors.HighlightBrush;
		Pressed = SystemColors.HighlightBrush;
		FieldColor = window;
		Field = SystemColors.WindowBrush;
		ToggleOnColor = SystemColors.HighlightColor;
		ToggleOn = SystemColors.HighlightBrush;
		Error = SystemColors.WindowTextBrush;
		SearchFill = SystemColors.WindowBrush;
		SearchCue = SystemColors.GrayTextBrush;
		MiniGlyph = SystemColors.WindowTextBrush;
		CaptionHover = SystemColors.HighlightBrush;
		CaptionPress = SystemColors.HighlightBrush;
		SliderTrackColor = window;
		SliderFillColor = SystemColors.HighlightColor;
		SliderFill = SystemColors.HighlightBrush;
		ScrollThumb = SystemColors.WindowTextBrush;
		ScrollThumbHover = SystemColors.HighlightBrush;
		RailScrollThumb = SystemColors.WindowTextBrush;
		RailScrollThumbHover = SystemColors.HighlightBrush;
	}

	// ---- colour helpers -------------------------------------------------------------------------------------------

	internal static Color Hex(string hex) => ParseHex(hex, Colors.Magenta);

	internal static Color ParseHex(string hex, Color fallback)
	{
		try
		{
			return (Color)ColorConverter.ConvertFromString(hex);
		}
		catch
		{
			return fallback;
		}
	}

	internal static SolidColorBrush Frozen(Color c)
	{
		SolidColorBrush b = new SolidColorBrush(c);
		b.Freeze();
		return b;
	}

	// Moves c toward black (darker) or white until it reads at the given contrast against ground (at most 40 steps).
	internal static Color UntilContrast(Color c, Color ground, double ratio, bool darker)
	{
		Color cur = c;
		for (int i = 0; i < 40 && FlyoutPalette.Contrast(cur, ground) < ratio; i++)
		{
			cur = darker ? Mix(cur, Colors.Black, 0.08) : Mix(cur, Colors.White, 0.08);
		}
		if (FlyoutPalette.Contrast(cur, ground) < ratio)
		{
			// the other direction (a very dark accent on a dark page, a pale one on a light page)
			cur = c;
			for (int i = 0; i < 40 && FlyoutPalette.Contrast(cur, ground) < ratio; i++)
			{
				cur = darker ? Mix(cur, Colors.White, 0.08) : Mix(cur, Colors.Black, 0.08);
			}
		}
		return cur;
	}

	private static Color Mix(Color a, Color b, double t)
	{
		return Color.FromRgb((byte)Math.Round(a.R + (b.R - a.R) * t), (byte)Math.Round(a.G + (b.G - a.G) * t), (byte)Math.Round(a.B + (b.B - a.B) * t));
	}

	private static Color TokenColor(string key, Color fallback)
	{
		try
		{
			if (Application.Current?.TryFindResource(key) is SolidColorBrush b)
			{
				return b.Color;
			}
		}
		catch
		{
		}
		return fallback;
	}
}

public sealed partial class PcSettingsWindow
{
	// Shared type faces: one instance each for every page build.
	internal static readonly FontFamily SegoeUi = new FontFamily("Segoe UI");

	internal static readonly FontFamily SegoeUiLight = new FontFamily("Segoe UI Light");

	internal static readonly FontFamily SegoeUiSemibold = new FontFamily("Segoe UI Semibold");

	internal static readonly FontFamily Glyphs = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets");

	private static Style? _bareButton;

	// Buttons that draw their own content (rows, tiles, swatches, info and toolbar buttons) take this explicit style, so
	// the window's implicit Metro button style (MinWidth 80, MinHeight 32, padding) does not size them.
	internal static Style BareButton
	{
		get
		{
			if (_bareButton == null)
			{
				Style s = new Style(typeof(ButtonBase));
				s.Setters.Add(new Setter(FrameworkElement.MinWidthProperty, 0.0));
				s.Setters.Add(new Setter(FrameworkElement.MinHeightProperty, 0.0));
				s.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(0.0)));
				s.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(0.0)));
				s.Setters.Add(new Setter(Control.BackgroundProperty, Brushes.Transparent));
				s.Setters.Add(new Setter(Control.FontFamilyProperty, SegoeUi));
				s.Seal();
				_bareButton = s;
			}
			return _bareButton;
		}
	}

	internal const double RowH = 40.0;

	internal const double RowH2 = 52.0;

	internal const double ColumnMax = 640.0;

	// ---- The page builder -------------------------------------------------------------------------------------------

	// Builds one page: a column (at most 640 DIP, centred) of grouped cards in a ScrollViewer. Rows go into the current
	// card with a 1 DIP separator between them; a group header or Card() starts the next card.
	private sealed class Pg
	{
		private readonly PcSettingsWindow _w;

		internal readonly string Id;

		internal readonly StackPanel Column;

		private StackPanel? _card;

		private bool _headerPending;

		internal Pg(PcSettingsWindow w, string id)
		{
			_w = w;
			Id = id;
			Column = new StackPanel
			{
				MaxWidth = ColumnMax,
				HorizontalAlignment = HorizontalAlignment.Stretch
			};
		}

		internal HubPalette P => _w._pal;

		internal void Header(string text)
		{
			_card = null;
			Column.Children.Add(new TextBlock
			{
				Text = text,
				FontFamily = SegoeUiSemibold,
				FontSize = 12.0,
				Foreground = P.Ink2,
				Margin = new Thickness(12.0, Column.Children.Count == 0 ? 4.0 : 20.0, 0.0, 6.0),
				TextTrimming = TextTrimming.CharacterEllipsis
			});
			_headerPending = true;
		}

		// The next row starts a new card (without a header).
		internal void Card()
		{
			_card = null;
		}

		private StackPanel CurrentCard()
		{
			if (_card == null)
			{
				_card = new StackPanel();
				Border b = new Border
				{
					Background = P.Card,
					BorderBrush = P.CardBorder,
					BorderThickness = new Thickness(1.0),
					SnapsToDevicePixels = true,
					Child = _card,
					Margin = new Thickness(0.0, _headerPending || Column.Children.Count == 0 ? 0.0 : 12.0, 0.0, 0.0)
				};
				_headerPending = false;
				Column.Children.Add(b);
			}
			return _card;
		}

		// Adds a row to the current card; a null row (nothing to show on this PC) is skipped.
		internal FrameworkElement? Add(FrameworkElement? row)
		{
			if (row == null)
			{
				return null;
			}
			StackPanel card = CurrentCard();
			if (card.Children.Count > 0)
			{
				card.Children.Add(new Border
				{
					Height = 1.0,
					Background = P.Separator,
					Margin = new Thickness(12.0, 0.0, 0.0, 0.0),
					SnapsToDevicePixels = true,
					IsHitTestVisible = false
				});
			}
			card.Children.Add(row);
			return row;
		}

		internal void Footnote(string text)
		{
			_card = null;
			Column.Children.Add(new TextBlock
			{
				Text = text,
				FontFamily = SegoeUi,
				FontSize = 12.0,
				Foreground = P.Ink2,
				TextWrapping = TextWrapping.Wrap,
				MaxHeight = 34.0,
				TextTrimming = TextTrimming.CharacterEllipsis,
				Margin = new Thickness(12.0, 6.0, 12.0, 0.0)
			});
		}

		// Anything outside the cards (a hero, a guard banner).
		internal void Block(UIElement e)
		{
			_card = null;
			Column.Children.Add(e);
		}

		internal bool CardEmpty => _card == null || _card.Children.Count == 0;

		internal ScrollViewer Done()
		{
			// a header with no row after it (every row of its card hidden on this PC) is dropped
			for (int i = Column.Children.Count - 1; i >= 0; i--)
			{
				if (Column.Children[i] is TextBlock tb && tb.FontFamily == SegoeUiSemibold && (i == Column.Children.Count - 1 || Column.Children[i + 1] is TextBlock { FontFamily: var f } && f == SegoeUiSemibold))
				{
					Column.Children.RemoveAt(i);
				}
			}
			Border pad = new Border
			{
				Padding = new Thickness(24.0, 16.0, 24.0, 40.0),
				Child = Column
			};
			ScrollViewer sv = new ScrollViewer
			{
				VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
				HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
				Content = pad,
				Focusable = false,
				Background = P.Page,
				Template = HubScrollViewerTemplate,
				Tag = Id
			};
			return sv;
		}
	}

	// ---- Scrolling --------------------------------------------------------------------------------------------------

	private const string XamlNs = "xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\" xmlns:x=\"http://schemas.microsoft.com/winfx/2006/xaml\"";

	// A quiet overlay scrollbar: a 6 DIP thumb (9 under the mouse or while dragged) in the ink at about 45% on a clear
	// track, floating over the right edge of the content, so the centred column is at the same place on short and long
	// pages. Its brushes are the resources Hub.ScrollThumb and Hub.ScrollThumbHover (the page's ink, or white on the rail).
	private static readonly string ScrollBarXaml =
		"<ScrollBar x:Name=\"PART_VerticalScrollBar\" HorizontalAlignment=\"Right\" Orientation=\"Vertical\" Width=\"12\" Focusable=\"False\" IsTabStop=\"False\" OverridesDefaultStyle=\"True\""
		+ " Maximum=\"{TemplateBinding ScrollableHeight}\" ViewportSize=\"{TemplateBinding ViewportHeight}\" Visibility=\"{TemplateBinding ComputedVerticalScrollBarVisibility}\""
		+ " Value=\"{Binding VerticalOffset, RelativeSource={RelativeSource TemplatedParent}, Mode=OneWay}\">"
		+ "<ScrollBar.Template><ControlTemplate TargetType=\"ScrollBar\"><Grid Background=\"Transparent\">"
		+ "<Track x:Name=\"PART_Track\" Orientation=\"Vertical\" IsDirectionReversed=\"True\">"
		+ "<Track.DecreaseRepeatButton><RepeatButton Command=\"ScrollBar.PageUpCommand\" Focusable=\"False\" IsTabStop=\"False\" OverridesDefaultStyle=\"True\"><RepeatButton.Template><ControlTemplate TargetType=\"RepeatButton\"><Border Background=\"Transparent\" /></ControlTemplate></RepeatButton.Template></RepeatButton></Track.DecreaseRepeatButton>"
		+ "<Track.IncreaseRepeatButton><RepeatButton Command=\"ScrollBar.PageDownCommand\" Focusable=\"False\" IsTabStop=\"False\" OverridesDefaultStyle=\"True\"><RepeatButton.Template><ControlTemplate TargetType=\"RepeatButton\"><Border Background=\"Transparent\" /></ControlTemplate></RepeatButton.Template></RepeatButton></Track.IncreaseRepeatButton>"
		+ "<Track.Thumb><Thumb MinHeight=\"28\" Focusable=\"False\" OverridesDefaultStyle=\"True\"><Thumb.Template><ControlTemplate TargetType=\"Thumb\">"
		+ "<Border x:Name=\"Bd\" Width=\"6\" Margin=\"0,2,3,2\" HorizontalAlignment=\"Right\" SnapsToDevicePixels=\"True\" Background=\"{DynamicResource Hub.ScrollThumb}\" />"
		+ "<ControlTemplate.Triggers>"
		+ "<Trigger Property=\"IsMouseOver\" Value=\"True\"><Setter TargetName=\"Bd\" Property=\"Width\" Value=\"9\" /><Setter TargetName=\"Bd\" Property=\"Background\" Value=\"{DynamicResource Hub.ScrollThumbHover}\" /></Trigger>"
		+ "<Trigger Property=\"IsDragging\" Value=\"True\"><Setter TargetName=\"Bd\" Property=\"Width\" Value=\"9\" /><Setter TargetName=\"Bd\" Property=\"Background\" Value=\"{DynamicResource Hub.ScrollThumbHover}\" /></Trigger>"
		+ "</ControlTemplate.Triggers></ControlTemplate></Thumb.Template></Thumb></Track.Thumb>"
		+ "</Track></Grid></ControlTemplate></ScrollBar.Template></ScrollBar>";

	private static ControlTemplate? _hubScrollViewerTemplate;

	// The pages' (and the sidebar's) scroll viewer: the content takes the whole width, the bar floats over it.
	internal static ControlTemplate HubScrollViewerTemplate => _hubScrollViewerTemplate ??= (ControlTemplate)XamlReader.Parse(
		"<ControlTemplate " + XamlNs + " TargetType=\"ScrollViewer\"><Grid Background=\"{TemplateBinding Background}\">"
		+ "<ScrollContentPresenter x:Name=\"PART_ScrollContentPresenter\" CanContentScroll=\"{TemplateBinding CanContentScroll}\" Margin=\"{TemplateBinding Padding}\" />"
		+ ScrollBarXaml + "</Grid></ControlTemplate>");

	private static ControlTemplate? _hubListTemplate;

	// The sidebar list: its items in that scroll viewer (the rail's thumb is white at about 35%).
	internal static ControlTemplate HubListTemplate => _hubListTemplate ??= (ControlTemplate)XamlReader.Parse(
		"<ControlTemplate " + XamlNs + " TargetType=\"ListBox\"><Border Background=\"{TemplateBinding Background}\" Padding=\"{TemplateBinding Padding}\" SnapsToDevicePixels=\"True\">"
		+ "<ScrollViewer x:Name=\"ScrollHost\" Focusable=\"False\" CanContentScroll=\"False\" HorizontalScrollBarVisibility=\"Disabled\" VerticalScrollBarVisibility=\"Auto\" Background=\"Transparent\">"
		+ "<ScrollViewer.Template><ControlTemplate TargetType=\"ScrollViewer\"><Grid Background=\"{TemplateBinding Background}\">"
		+ "<ScrollContentPresenter x:Name=\"PART_ScrollContentPresenter\" CanContentScroll=\"{TemplateBinding CanContentScroll}\" />"
		+ ScrollBarXaml + "</Grid></ControlTemplate></ScrollViewer.Template>"
		+ "<ItemsPresenter SnapsToDevicePixels=\"True\" /></ScrollViewer></Border></ControlTemplate>");

	// The thumbs' brushes of the page and of the rail.
	private void ApplyScrollBrushes()
	{
		base.Resources["Hub.ScrollThumb"] = _pal.ScrollThumb;
		base.Resources["Hub.ScrollThumbHover"] = _pal.ScrollThumbHover;
		if (_nav != null)
		{
			_nav.Resources["Hub.ScrollThumb"] = _pal.RailScrollThumb;
			_nav.Resources["Hub.ScrollThumbHover"] = _pal.RailScrollThumbHover;
		}
	}

	// ---- Row registry (search highlight, QA) -----------------------------------------------------------------------

	private readonly Dictionary<string, FrameworkElement> _rowElements = new Dictionary<string, FrameworkElement>(StringComparer.OrdinalIgnoreCase);

	private void Register(string? id, FrameworkElement row)
	{
		if (string.IsNullOrEmpty(id))
		{
			return;
		}
		_rowElements[id] = row;
		AutomationProperties.SetAutomationId(row, id);
		AttachStar(id!, row);
	}

	internal FrameworkElement? QaRowElement(string id) => _rowElements.TryGetValue(id, out FrameworkElement? e) ? e : null;

	// ---- Async reads ------------------------------------------------------------------------------------------------

	private int _pending;

	private int _buildGen;

	// Reads a value off the UI thread and applies it when it arrives (only if the page that asked is still shown).
	private void ReadAsync<T>(Func<T> read, Action<T> apply)
	{
		int gen = _buildGen;
		_pending++;
		Task.Run(read).ContinueWith(delegate(Task<T> t)
		{
			try
			{
				base.Dispatcher.BeginInvoke(DispatcherPriority.Normal, (Action)delegate
				{
					_pending = Math.Max(0, _pending - 1);
					if (gen != _buildGen || t.Status != TaskStatus.RanToCompletion)
					{
						return;
					}
					try
					{
						apply(t.Result);
					}
					catch (Exception ex)
					{
						Logger.Log("PC settings: applying a read failed: " + ex.Message);
					}
				});
			}
			catch
			{
			}
		}, TaskScheduler.Default);
	}

	// QA: pumps the dispatcher until every async read of the page shown has arrived (or the time is up).
	internal void QaWaitReads(int maxMs = 4000)
	{
		DateTime until = DateTime.UtcNow.AddMilliseconds(maxMs);
		while (_pending > 0 && DateTime.UtcNow < until)
		{
			DispatcherFrame frame = new DispatcherFrame();
			base.Dispatcher.BeginInvoke(DispatcherPriority.Background, (Action)delegate { frame.Continue = false; });
			Dispatcher.PushFrame(frame);
			if (_pending > 0)
			{
				System.Threading.Thread.Sleep(5);
			}
		}
	}

	// ---- Row parts --------------------------------------------------------------------------------------------------

	private TextBlock Label(string text, double size = 13.5)
	{
		return new TextBlock
		{
			Text = text,
			FontFamily = SegoeUi,
			FontSize = size,
			Foreground = _pal.Ink,
			TextTrimming = TextTrimming.CharacterEllipsis,
			VerticalAlignment = VerticalAlignment.Center
		};
	}

	private TextBlock Secondary(string text)
	{
		return new TextBlock
		{
			Text = text,
			FontFamily = SegoeUi,
			FontSize = 12.0,
			Foreground = _pal.Ink2,
			TextTrimming = TextTrimming.CharacterEllipsis,
			Margin = new Thickness(0.0, 1.0, 0.0, 0.0)
		};
	}

	// A 14 px info glyph that carries long help as its tooltip (and as the control's HelpText).
	private Button InfoButton(string help)
	{
		Button b = new Button
		{
			Template = _pal.PlainTemplate,
			Style = BareButton,
			Width = 22.0,
			Height = 22.0,
			Margin = new Thickness(4.0, 0.0, 0.0, 0.0),
			VerticalAlignment = VerticalAlignment.Center,
			Cursor = Cursors.Help,
			ToolTip = new ToolTip { Content = new TextBlock { Text = help, MaxWidth = 360.0, TextWrapping = TextWrapping.Wrap } },
			Content = new TextBlock
			{
				Text = "",
				FontFamily = Glyphs,
				FontSize = 14.0,
				Foreground = _pal.Ink2
			}
		};
		ToolTipService.SetInitialShowDelay(b, 250);
		ToolTipService.SetShowDuration(b, 30000);
		AutomationProperties.SetName(b, "More information");
		AutomationProperties.SetHelpText(b, help);
		b.SetResourceReference(FrameworkElement.FocusVisualStyleProperty, "Metro81.Focus");
		return b;
	}

	// A short description stays as the second line; a long one moves into the info button.
	private const int SecondaryMax = 64;

	// The standard row: label (+ second line, + info button) on the left, the control right-aligned in a column of at
	// most 240 DIP. 40 DIP high, 52 with a second line.
	private Grid RowShell(string title, string? secondary, string? help, FrameworkElement? control, out TextBlock label)
	{
		if (secondary != null && secondary.Length > SecondaryMax && help == null)
		{
			help = secondary;
			secondary = null;
		}
		Grid g = new Grid
		{
			Height = secondary == null ? RowH : RowH2,
			Background = Brushes.Transparent,
			SnapsToDevicePixels = true
		};
		g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.0, GridUnitType.Star) });
		g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto, MaxWidth = 252.0 });
		StackPanel text = new StackPanel
		{
			VerticalAlignment = VerticalAlignment.Center,
			Margin = new Thickness(12.0, 0.0, 12.0, 0.0)
		};
		StackPanel line = new StackPanel { Orientation = Orientation.Horizontal };
		label = Label(title);
		line.Children.Add(label);
		if (!string.IsNullOrEmpty(help))
		{
			line.Children.Add(InfoButton(help!));
		}
		text.Children.Add(line);
		if (!string.IsNullOrEmpty(secondary))
		{
			text.Children.Add(Secondary(secondary!));
		}
		g.Children.Add(text);
		if (control != null)
		{
			control.Margin = new Thickness(0.0, 0.0, 12.0, 0.0);
			control.VerticalAlignment = VerticalAlignment.Center;
			control.HorizontalAlignment = HorizontalAlignment.Right;
			Grid.SetColumn(control, 1);
			g.Children.Add(control);
			AutomationProperties.SetName(control, title);
			AutomationProperties.SetLabeledBy(control, label);
			string? helpText = help ?? secondary;
			if (!string.IsNullOrEmpty(helpText))
			{
				AutomationProperties.SetHelpText(control, helpText);
			}
		}
		return g;
	}

	// ---- Rows -------------------------------------------------------------------------------------------------------

	// A toggle row. The control is the compact Metro toggle; flipping it calls set (errors are logged, never thrown).
	private FrameworkElement ToggleRow(string id, bool on, Action<bool> set, string? help = null, string? title = null, string? secondary = null)
	{
		string t0 = title ?? HubCatalog.Title(id);
		bool journaled = UiJournaled(id);
		bool cur = on;
		ToggleButton t = CompactToggle(on, delegate(bool v)
		{
			bool before = cur;
			set(v);
			cur = v;
			if (journaled)
			{
				JournalUi(id, t0, before ? "On" : "Off", v ? "On" : "Off");
			}
		});
		if (journaled)
		{
			_uiSetters[id] = delegate(string label) { t.IsChecked = label == "On"; };
		}
		Grid row = RowShell(t0, secondary, help, t, out _);
		Register(id, row);
		return row;
	}

	private ToggleButton CompactToggle(bool on, Action<bool> set)
	{
		ToggleButton t = new ToggleButton
		{
			IsChecked = on,
			Width = 40.0,
			Height = 28.0
		};
		t.SetResourceReference(FrameworkElement.StyleProperty, "Metro81.ToggleSwitch.Compact");
		bool quiet = false;
		void Changed(bool value)
		{
			if (quiet)
			{
				return;
			}
			SlideThumb(t, value);
			try
			{
				set(value);
			}
			catch (Exception ex)
			{
				Logger.Log("PC settings toggle: " + ex.Message);
			}
		}
		t.Checked += delegate { Changed(true); };
		t.Unchecked += delegate { Changed(false); };
		t.Tag = (Action<bool>)delegate(bool v)
		{
			quiet = true;
			t.IsChecked = v;
			quiet = false;
		};
		return t;
	}

	// Sets a compact toggle without running its handler (a value read later or put back).
	private static void SetQuiet(ToggleButton t, bool value)
	{
		if (t.Tag is Action<bool> set)
		{
			set(value);
		}
		else
		{
			t.IsChecked = value;
		}
	}

	// Slides the thumb to its new end (Motion Micro, 130 ms; instantly with Motion Off).
	private static void SlideThumb(ToggleButton t, bool on)
	{
		if (!Motion.Enabled || t.Template?.FindName("Thumb", t) is not Border thumb)
		{
			return;
		}
		TranslateTransform tr = thumb.RenderTransform as TranslateTransform ?? new TranslateTransform();
		thumb.RenderTransform = tr;
		double from = on ? -30.0 : 30.0;
		tr.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(from, 0.0, Motion.Dur(Motion.Cat.Micro))
		{
			EasingFunction = Motion.Ease(Motion.Cat.Micro)
		});
	}

	// A drop-down row (Metro combo box, 28 high, 180 wide).
	private FrameworkElement ChoiceRow(string id, string[] options, int selected, Action<int> set, string? help = null, string? title = null, string? secondary = null, double width = 180.0)
	{
		string t0 = title ?? HubCatalog.Title(id);
		bool journaled = UiJournaled(id);
		int cur = selected;
		ComboBox c = Combo(t0, options, selected, delegate(int i)
		{
			int before = cur;
			set(i);
			cur = i;
			if (journaled && before >= 0 && before < options.Length && i >= 0 && i < options.Length)
			{
				JournalUi(id, t0, options[before], options[i]);
			}
		}, width);
		if (journaled)
		{
			_uiSetters[id] = delegate(string label)
			{
				int i = Array.IndexOf(options, label);
				if (i >= 0)
				{
					c.SelectedIndex = i;
				}
			};
		}
		Grid row = RowShell(title ?? HubCatalog.Title(id), secondary, help, c, out _);
		Register(id, row);
		return row;
	}

	private ComboBox Combo(string name, string[] options, int selected, Action<int> set, double width = 180.0)
	{
		ComboBox c = MetroPatternTheme.CreateComboBox(options, selected, delegate(int i)
		{
			try
			{
				set(i);
			}
			catch (Exception ex)
			{
				Logger.Log("PC settings choice: " + ex.Message);
			}
		}, name);
		c.Width = Math.Clamp(width, 140.0, 240.0);
		c.Height = 28.0;
		c.MinHeight = 28.0;
		c.FontSize = 13.5;
		c.Padding = new Thickness(8.0, 0.0, 8.0, 0.0);
		c.MaxDropDownHeight = 340.0;
		return c;
	}

	// A slider row: end labels inline, the value on the second line.
	private FrameworkElement SliderRow(string id, double min, double max, double value, Action<int> changed, Func<int, string> format, string? low = null, string? high = null, string? help = null, string? title = null, Action<int>? released = null)
	{
		Slider s = new Slider
		{
			Minimum = min,
			Maximum = max,
			Value = Math.Clamp(value, min, max),
			Width = 200.0,
			IsSnapToTickEnabled = true,
			TickFrequency = 1.0,
			SmallChange = 1.0,
			LargeChange = Math.Max(1.0, Math.Round((max - min) / 10.0)),
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
		string t = title ?? HubCatalog.Title(id);
		Grid row = RowShell(t, format((int)Math.Round(s.Value)), help, control, out _);
		AutomationProperties.SetName(s, t);
		TextBlock? valueText = FindSecondary(row);
		s.ValueChanged += delegate
		{
			int v = (int)Math.Round(s.Value);
			if (valueText != null)
			{
				valueText.Text = format(v);
			}
			try
			{
				changed(v);
			}
			catch (Exception ex)
			{
				Logger.Log("PC settings slider: " + ex.Message);
			}
		};
		if (released != null)
		{
			s.AddHandler(System.Windows.Controls.Primitives.Thumb.DragCompletedEvent, (DragCompletedEventHandler)delegate { released((int)Math.Round(s.Value)); });
		}
		row.Tag = s;
		Register(id, row);
		return row;
	}

	private TextBlock EndLabel(string text, Thickness margin)
	{
		return new TextBlock
		{
			Text = text,
			FontFamily = SegoeUi,
			FontSize = 11.0,
			Foreground = _pal.Ink2,
			VerticalAlignment = VerticalAlignment.Center,
			Margin = margin
		};
	}

	private static TextBlock? FindSecondary(Grid row)
	{
		if (row.Children.Count > 0 && row.Children[0] is StackPanel text && text.Children.Count > 1 && text.Children[1] is TextBlock tb)
		{
			return tb;
		}
		return null;
	}

	// A strip of square radio segments (at least 72 wide, 28 high).
	private FrameworkElement SegmentedRow(string id, string[] options, int selected, Action<int> set, string? help = null, string? title = null)
	{
		StackPanel strip = new StackPanel { Orientation = Orientation.Horizontal };
		string group = "seg-" + id + "-" + _buildGen;
		string t = title ?? HubCatalog.Title(id);
		bool journaled = UiJournaled(id);
		int cur = selected;
		List<RadioButton> segments = new List<RadioButton>();
		for (int i = 0; i < options.Length; i++)
		{
			int idx = i;
			RadioButton rb = new RadioButton
			{
				Style = BareButton,
				GroupName = group,
				IsChecked = i == selected,
				Content = options[i],
				MinWidth = 64.0,
				Height = 28.0,
				Template = SegmentTemplate(),
				FontFamily = SegoeUi,
				FontSize = 12.0,
				Margin = new Thickness(i == 0 ? 0.0 : -1.0, 0.0, 0.0, 0.0)
			};
			rb.SetResourceReference(FrameworkElement.FocusVisualStyleProperty, "Metro81.Focus");
			AutomationProperties.SetName(rb, t + ": " + options[i]);
			rb.Checked += delegate
			{
				try
				{
					int before = cur;
					set(idx);
					cur = idx;
					if (journaled && before >= 0 && before < options.Length)
					{
						JournalUi(id, t, options[before], options[idx]);
					}
				}
				catch (Exception ex)
				{
					Logger.Log("PC settings segment: " + ex.Message);
				}
			};
			segments.Add(rb);
			strip.Children.Add(rb);
		}
		if (journaled)
		{
			_uiSetters[id] = delegate(string label)
			{
				int i = Array.IndexOf(options, label);
				if (i >= 0)
				{
					segments[i].IsChecked = true;
				}
			};
		}
		Grid row = RowShell(t, null, help, strip, out _);
		// four segments of at least 64 DIP do not fit the 240 DIP control column; the label is short anyway
		row.ColumnDefinitions[1].MaxWidth = 360.0;
		Register(id, row);
		return row;
	}

	private ControlTemplate? _segmentTemplate;

	private ControlTemplate SegmentTemplate()
	{
		if (_segmentTemplate != null)
		{
			return _segmentTemplate;
		}
		ControlTemplate t = new ControlTemplate(typeof(RadioButton));
		FrameworkElementFactory bd = new FrameworkElementFactory(typeof(Border), "Bd");
		bd.SetValue(Border.BackgroundProperty, _pal.Field);
		bd.SetResourceReference(Border.BorderBrushProperty, "Metro81.Border");
		bd.SetValue(Border.BorderThicknessProperty, new Thickness(1.0));
		bd.SetValue(Border.PaddingProperty, new Thickness(10.0, 0.0, 10.0, 0.0));
		bd.SetValue(UIElement.SnapsToDevicePixelsProperty, true);
		FrameworkElementFactory cp = new FrameworkElementFactory(typeof(ContentPresenter), "Cp");
		cp.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
		cp.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
		cp.SetValue(System.Windows.Documents.TextElement.ForegroundProperty, _pal.Ink);
		bd.AppendChild(cp);
		t.VisualTree = bd;
		Trigger hover = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
		hover.Setters.Add(new Setter(Border.BackgroundProperty, _pal.RowHover, "Bd"));
		t.Triggers.Add(hover);
		Trigger on = new Trigger { Property = ToggleButton.IsCheckedProperty, Value = true };
		on.Setters.Add(new Setter(Border.BackgroundProperty, _pal.ToggleOn, "Bd"));
		on.Setters.Add(new Setter(Border.BorderBrushProperty, _pal.ToggleOn, "Bd"));
		on.Setters.Add(new Setter(System.Windows.Documents.TextElement.ForegroundProperty, _pal.Rest ? DarkRestPalette.InkOnAccent : Brushes.White, "Cp"));
		t.Triggers.Add(on);
		t.Seal();
		_segmentTemplate = t;
		return t;
	}

	// A row that acts on a click: the whole row is the button (link, drill-in, button-like rows).
	private Button ClickRow(string title, string? secondary, FrameworkElement? trailing, Action click, string? help = null)
	{
		Grid shell = RowShell(title, secondary, help, null, out TextBlock label);
		if (trailing != null)
		{
			trailing.Margin = new Thickness(0.0, 0.0, 14.0, 0.0);
			trailing.VerticalAlignment = VerticalAlignment.Center;
			trailing.HorizontalAlignment = HorizontalAlignment.Right;
			Grid.SetColumn(trailing, 1);
			shell.Children.Add(trailing);
		}
		Button b = new Button
		{
			Template = _pal.RowTemplate,
			Style = BareButton,
			Content = shell,
			HorizontalContentAlignment = HorizontalAlignment.Stretch,
			VerticalContentAlignment = VerticalAlignment.Stretch,
			Cursor = Cursors.Hand,
			Foreground = _pal.Ink
		};
		b.SetResourceReference(FrameworkElement.FocusVisualStyleProperty, "Metro81.Focus");
		AutomationProperties.SetName(b, title);
		if (!string.IsNullOrEmpty(help ?? secondary))
		{
			AutomationProperties.SetHelpText(b, help ?? secondary);
		}
		b.Click += delegate
		{
			try
			{
				click();
			}
			catch (Exception ex)
			{
				Logger.Log("PC settings row '" + title + "': " + ex.Message);
			}
		};
		return b;
	}

	private TextBlock TrailingGlyph(string glyph, double size = 12.0)
	{
		return new TextBlock
		{
			Text = glyph,
			FontFamily = Glyphs,
			FontSize = size,
			Foreground = _pal.Ink2,
			IsHitTestVisible = false
		};
	}

	// A link to a Windows page or applet (E8A7 = opens outside). Null when nothing can be opened on this PC.
	private FrameworkElement? LinkRow(string id, string? secondary = null, LinkSpec? spec = null, string? title = null)
	{
		HubRow? r = HubCatalog.Row(id);
		spec ??= r?.Link;
		if (spec == null)
		{
			return null;
		}
		if (_caps != null && !SettingsLink.Available(spec, _caps))
		{
			return null;
		}
		if (LinkFolded(id, spec))
		{
			// an unverified deep link that falls back to the Windows page another row of this page already opens
			return null;
		}
		LinkSpec s = spec;
		Button b = ClickRow(title ?? r?.Title ?? id, secondary, TrailingGlyph(""), delegate
		{
			SettingsLink.Open(s, _caps);
		});
		Register(id, b);
		return b;
	}

	// A link whose deep page is not on this PC opens its parent page; when another link row of the same page opens that
	// parent already ("Sound devices" and "App volume" -> Sound settings), the row is left out rather than shown as a
	// second way to the same page.
	internal bool LinkFolded(string id, LinkSpec spec)
	{
		if (spec.Parent == null || spec.Uri == null || string.Equals(spec.Uri, spec.Parent, StringComparison.OrdinalIgnoreCase))
		{
			return false;
		}
		ResolvedLink? r = SettingsLink.Resolve(spec, _caps);
		if (r == null || !r.Value.IsUri || !string.Equals(r.Value.Target, spec.Parent, StringComparison.OrdinalIgnoreCase))
		{
			return false;
		}
		string? page = HubCatalog.Row(id)?.Page;
		if (page == null)
		{
			return false;
		}
		foreach (HubRow o in HubCatalog.Rows)
		{
			if (o.Page != page || o.Kind != HubRowKind.Link || o.Link?.Uri == null || string.Equals(o.Id, id, StringComparison.OrdinalIgnoreCase) || !string.Equals(o.Link.Uri, spec.Parent, StringComparison.OrdinalIgnoreCase))
			{
				continue;
			}
			ResolvedLink? opener = SettingsLink.Resolve(o.Link, _caps);
			if (opener != null && opener.Value.IsUri && string.Equals(opener.Value.Target, spec.Parent, StringComparison.OrdinalIgnoreCase))
			{
				return true;
			}
		}
		return false;
	}

	// A row that runs an action of the launcher (a dialog, a folder) with the outside glyph.
	private FrameworkElement ActionRow(string id, Action act, string? secondary = null, string? title = null, string glyph = "")
	{
		Button b = ClickRow(title ?? HubCatalog.Title(id), secondary, TrailingGlyph(glyph), act);
		Register(id, b);
		return b;
	}

	// A drill-in row: the current value in the secondary ink, then the menus' vector chevron.
	private FrameworkElement DrillRow(string id, string? value, Action go, string? secondary = null, string? title = null)
	{
		StackPanel trail = new StackPanel { Orientation = Orientation.Horizontal, IsHitTestVisible = false };
		if (!string.IsNullOrEmpty(value))
		{
			trail.Children.Add(new TextBlock
			{
				Text = value,
				FontFamily = SegoeUi,
				FontSize = 13.5,
				Foreground = _pal.Ink2,
				VerticalAlignment = VerticalAlignment.Center,
				Margin = new Thickness(0.0, 0.0, 10.0, 0.0),
				MaxWidth = 200.0,
				TextTrimming = TextTrimming.CharacterEllipsis
			});
		}
		System.Windows.Shapes.Path chevron = MenuGlyphs.ChevronPath(_pal.Ink2);
		trail.Children.Add(chevron);
		Button b = ClickRow(title ?? HubCatalog.Title(id), secondary, trail, go);
		Register(id, b);
		return b;
	}

	// A read-only value; null value shows the "…" placeholder until SetInfo fills it.
	private FrameworkElement InfoRow(string id, string? value, string? secondary = null, string? title = null, string? help = null)
	{
		Grid row = ValueShell(title ?? HubCatalog.Title(id), value, secondary, help);
		Register(id, row);
		return row;
	}

	// The label and value row of read-only values: the label takes what it needs, the value the rest of the row (right
	// aligned, trimmed with the whole value in its tooltip), so a long value ("Intel(R) Core(TM) i5-10400F CPU @ 2.90GHz")
	// is not cut at 240 DIP while the row is half empty.
	private Grid ValueShell(string title, string? value, string? secondary = null, string? help = null)
	{
		TextBlock v = new TextBlock
		{
			FontFamily = SegoeUi,
			FontSize = 13.5,
			Foreground = _pal.Ink2,
			TextTrimming = TextTrimming.CharacterEllipsis,
			TextAlignment = TextAlignment.Right,
			Focusable = false
		};
		Grid row = RowShell(title, secondary, help, v, out _);
		row.ColumnDefinitions[0].Width = GridLength.Auto;
		row.ColumnDefinitions[1].Width = new GridLength(1.0, GridUnitType.Star);
		row.ColumnDefinitions[1].MaxWidth = double.PositiveInfinity;
		row.ColumnDefinitions[1].MinWidth = 96.0;
		v.HorizontalAlignment = HorizontalAlignment.Stretch;
		row.Tag = v;
		SetValueText(v, title, value ?? "…");
		return row;
	}

	// The value of a read-only row: its text, the tooltip with the whole value, and the name UI Automation reads.
	private static void SetValueText(TextBlock v, string title, string value)
	{
		v.Text = value;
		v.ToolTip = value.Length > 0 && value != "…" ? value : null;
		AutomationProperties.SetName(v, title + ": " + (value == "…" ? "" : value));
	}

	private static void SetInfo(FrameworkElement? row, string value)
	{
		if (row is Grid { Tag: TextBlock v })
		{
			string name = AutomationProperties.GetName(v);
			int colon = name.IndexOf(": ", StringComparison.Ordinal);
			SetValueText(v, colon >= 0 ? name.Substring(0, colon) : name, value);
		}
	}

	// A value read here with a link that changes it in Windows (the values the native controls will own).
	private FrameworkElement? ValueLinkRow(string id, string? value, string? secondary = null, LinkSpec? spec = null)
	{
		HubRow? r = HubCatalog.Row(id);
		spec ??= r?.Link;
		if (spec == null || (_caps != null && !SettingsLink.Available(spec, _caps)))
		{
			return InfoRow(id, value, secondary);
		}
		LinkSpec s = spec;
		StackPanel trail = new StackPanel { Orientation = Orientation.Horizontal, IsHitTestVisible = false };
		TextBlock v = new TextBlock
		{
			Text = value ?? "…",
			FontFamily = SegoeUi,
			FontSize = 13.5,
			Foreground = _pal.Ink2,
			VerticalAlignment = VerticalAlignment.Center,
			Margin = new Thickness(0.0, 0.0, 10.0, 0.0),
			MaxWidth = 200.0,
			TextTrimming = TextTrimming.CharacterEllipsis
		};
		trail.Children.Add(v);
		trail.Children.Add(TrailingGlyph(""));
		Button b = ClickRow(r?.Title ?? id, secondary, trail, delegate
		{
			SettingsLink.Open(s, _caps);
		});
		b.Tag = v;
		AutomationProperties.SetHelpText(b, "Change in " + s.Describe());
		Register(id, b);
		return b;
	}

	// A row with a button on the right (Open, Copy, Restart...).
	private FrameworkElement ButtonRow(string id, string buttonText, Action click, string? secondary = null, string? help = null, string? title = null, bool accent = false)
	{
		Button btn = HubButton(buttonText, click, accent);
		Grid row = RowShell(title ?? HubCatalog.Title(id), secondary, help, btn, out _);
		AutomationProperties.SetName(btn, buttonText + " – " + (title ?? HubCatalog.Title(id)));
		Register(id, row);
		return row;
	}

	private Button HubButton(string text, Action click, bool accent = false)
	{
		Button b = new Button
		{
			Content = text,
			Height = 28.0,
			MinHeight = 28.0,
			MinWidth = 72.0,
			Padding = new Thickness(12.0, 0.0, 12.0, 0.0),
			FontSize = 13.5
		};
		b.SetResourceReference(FrameworkElement.StyleProperty, accent ? "Metro81.AccentButton" : "Metro81.Button");
		AutomationProperties.SetName(b, text.TrimEnd('…'));
		b.Click += delegate
		{
			try
			{
				click();
			}
			catch (Exception ex)
			{
				Logger.Log("PC settings button '" + text + "': " + ex.Message);
			}
		};
		return b;
	}

	// Free content inside a card row (tiles, previews, status lines) with the row's 12 DIP inset.
	private FrameworkElement CustomRow(string? id, UIElement content, Thickness? padding = null)
	{
		Border b = new Border
		{
			Padding = padding ?? new Thickness(12.0, 10.0, 12.0, 12.0),
			Child = content
		};
		Register(id, b);
		return b;
	}

	// The hero at the top of Home, About, Account and Sound: a 32 DIP mini-tile, a title and one line.
	private FrameworkElement Hero(string tile, char glyph, string title, string line, UIElement? picture = null)
	{
		Grid g = new Grid { Height = 64.0, Margin = new Thickness(0.0, 0.0, 0.0, 4.0) };
		g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
		g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.0, GridUnitType.Star) });
		UIElement icon = picture ?? MiniTile(tile, glyph, 32.0, 18.0);
		if (icon is FrameworkElement fe)
		{
			fe.Margin = new Thickness(12.0, 0.0, 12.0, 0.0);
			fe.VerticalAlignment = VerticalAlignment.Center;
		}
		g.Children.Add(icon);
		StackPanel text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
		text.Children.Add(new TextBlock
		{
			Text = title,
			FontFamily = SegoeUiLight,
			FontSize = 20.0,
			Foreground = _pal.Ink,
			TextTrimming = TextTrimming.CharacterEllipsis
		});
		text.Children.Add(new TextBlock
		{
			Text = line,
			FontFamily = SegoeUi,
			FontSize = 12.0,
			Foreground = _pal.Ink2,
			TextTrimming = TextTrimming.CharacterEllipsis
		});
		Grid.SetColumn(text, 1);
		g.Children.Add(text);
		return g;
	}

	// A square mini-tile with a white glyph (outlined in high contrast).
	private Border MiniTile(string tile, char glyph, double size, double glyphSize)
	{
		Border b = new Border
		{
			Width = size,
			Height = size,
			Background = _pal.MiniTile(tile),
			SnapsToDevicePixels = true,
			Child = new TextBlock
			{
				Text = glyph.ToString(),
				FontFamily = Glyphs,
				FontSize = glyphSize,
				Foreground = _pal.MiniGlyph,
				HorizontalAlignment = HorizontalAlignment.Center,
				VerticalAlignment = VerticalAlignment.Center
			}
		};
		if (_pal.HighContrast)
		{
			b.BorderBrush = SystemColors.WindowTextBrush;
			b.BorderThickness = new Thickness(1.0);
		}
		return b;
	}

	// A one-line text field of the hub's 28 DIP control height. The window's Metro81.TextBox style asks for 36 DIP and
	// 4 DIP of vertical padding; left as is, a 28 DIP field is laid out 36 tall and its text sits low, so both are reset.
	internal static TextBox FieldBox(double width)
	{
		TextBox box = new TextBox
		{
			Width = width,
			Height = 28.0,
			MinHeight = 28.0,
			FontSize = 13.5,
			Padding = new Thickness(6.0, 0.0, 6.0, 0.0),
			VerticalContentAlignment = VerticalAlignment.Center
		};
		box.SetResourceReference(FrameworkElement.StyleProperty, "Metro81.TextBox");
		return box;
	}

	// An inline line under a row (an error, a note) in the secondary or error ink.
	private TextBlock InlineNote(string text, bool error = false)
	{
		return new TextBlock
		{
			Text = text,
			FontFamily = SegoeUi,
			FontSize = 12.0,
			Foreground = error ? _pal.Error : _pal.Ink2,
			TextWrapping = TextWrapping.Wrap,
			Margin = new Thickness(12.0, 0.0, 12.0, 10.0)
		};
	}
}
