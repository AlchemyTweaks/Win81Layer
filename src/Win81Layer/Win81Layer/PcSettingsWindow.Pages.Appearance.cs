using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

#nullable enable annotations

namespace Win81Layer;

// Appearance: the four looks (their only home), one accent palette, pictures, pointers, icons and sounds, motion and
// the window frames (the old Desktop composition page, compacted). Behaviour and the App.ApplyXxx hooks are those of the
// earlier Launcher and Desktop composition pages.
public sealed partial class PcSettingsWindow
{
	// The Metro tile colours (the Start screen's 8.1 palette): the accent swatches and the Start background colours.
	private static readonly string[] Palette = new string[20]
	{
		"#512BD4", "#6A2C91", "#8E2DC0", "#A61E5A", "#C81E5B", "#E3008C", "#E81123", "#EF6950", "#FF8C00", "#C19C00",
		"#10893E", "#00B294", "#00B7C3", "#0091F7", "#2672EC", "#4617B4", "#1F1F1F", "#5A3E85", "#B146C2", "#767676"
	};

	// The authentic Windows 8.1 window-border colours (Control Panel > Personalization > Color): the window-frame
	// colours when frames use their own colour.
	private static readonly string[] AuthenticAccents = new string[15]
	{
		"#8C8C8C", "#86C5E8", "#EAA3CB", "#E8CB4C", "#A6C64F", "#7FD0C9", "#EFA24C",
		"#F17E75", "#EA55A0", "#45C06A", "#C39BE8", "#5DB0EF", "#9296EC", "#BBAE9E", "#F5F5F5"
	};

	// Look first (the four tiles, their status, and the seasonal and Dark rest options one level down), then Colour,
	// Pictures, Pointers, Icons & sounds, Motion and More: the first screen shows what people change most.
	private ScrollViewer AppearancePage()
	{
		AppSettings s = SettingsStore.Load();
		Pg pg = new Pg(this, "appearance");
		BuildLookSection(pg, s);
		pg.Header("Colour");
		pg.Add(CustomRow("appearance.accent", AccentSwatches(frames: false)));
		pg.Add(ToggleRow("appearance.frameaccent", s.FrameAccentSeparate, delegate(bool on)
		{
			SettingsStore.Update(delegate(AppSettings x) { x.FrameAccentSeparate = on; });
			AfterAccentChange();
			RebuildCurrentPage();
		}, "Off: window frames and captions use the accent above. On: they keep a colour of their own from the Windows 8.1 window-border palette."));
		if (s.FrameAccentSeparate)
		{
			pg.Add(CustomRow(null, AccentSwatches(frames: true)));
		}
		pg.Header("Pictures");
		pg.Add(LinkRow("appearance.background", "Picture, solid colour or slideshow (Windows)"));
		pg.Add(Win81WallpaperRow(s));
		pg.Add(LockScreenRow());
		pg.Add(LinkRow("appearance.accountpicture"));
		pg.Header("Pointers");
		pg.Add(DrillRow("appearance.pointers", PointerChoiceLabel(s), delegate { Navigate("appearance/pointers", null, push: true); }));
		pg.Header("Icons & sounds");
		pg.Add(ToggleRow("appearance.shellicons", s.ReplaceSystemIcons, delegate(bool on)
		{
			SettingsStore.Update(delegate(AppSettings x) { x.ReplaceSystemIcons = on; });
			SettingsWorld.Current.SetWin81ShellIcons(on);
		}, "Authentic Win8.1 icons for This PC, Recycle Bin, Network, Control Panel and your user folder. Turn off to restore the original Windows icons."));
		pg.Add(ToggleRow("appearance.appicons", s.Replace81AppIcons, delegate(bool on)
		{
			SettingsStore.Update(delegate(AppSettings x) { x.Replace81AppIcons = on; });
			AppIconOverrides.RefreshAllSurfaces();
		}, "Authentic Win8.1 / Office 2013 icons on Start tiles, the all-apps list and taskbar pins. Swaps live."));
		pg.Add(ToggleRow("appearance.sounds81", s.UseWin81Sounds, SetWin81Sounds, "The authentic Windows 8.1 system sounds. Turn off to get your own sound scheme back."));
		pg.Header("Motion");
		pg.Add(KnobRow("appearance.motion"));
		pg.Add(DrillRow("appearance.frames", CompositionProfiles.Resolve(DesktopComposition.Mode).Name, delegate { Navigate("appearance/frames", null, push: true); }));
		pg.Header("More");
		pg.Add(LinkRow("appearance.wincolors"));
		pg.Add(LinkRow("appearance.themes"));
		return pg.Done();
	}

	private void SetWin81Sounds(bool on)
	{
		SettingsStore.Update(delegate(AppSettings x) { x.UseWin81Sounds = on; });
		SettingsWorld.Current.SetWin81Sounds(on);
	}

	private static string PointerChoiceLabel(AppSettings s)
	{
		try
		{
			int i = PointerScheme.ChoiceIndex(s);
			return i >= 0 && i < PointerScheme.ChoiceLabels.Length ? PointerScheme.ChoiceLabels[i] : string.Empty;
		}
		catch
		{
			return string.Empty;
		}
	}

	// ===== Look: Normal / Halloween / Christmas / Dark rest =====

	private WrapPanel? _seasonTiles;

	private TextBlock? _seasonStatus;

	private FrameworkElement? _seasonWallpaperRow;

	private FrameworkElement? _restWallpaperRow;

	private bool _seasonBusyUi;

	private int _seasonTilesGen = -1;

	// The four tiles (applied at once), a status line that always says what Normal restores, and the way to the seasonal
	// and the Dark rest options.
	private void BuildLookSection(Pg pg, AppSettings s)
	{
		pg.Header("Look");
		StackPanel look = new StackPanel();
		_seasonTiles = new WrapPanel { Orientation = Orientation.Horizontal };
		_seasonTilesGen = _buildGen;
		look.Children.Add(_seasonTiles);
		_seasonStatus = new TextBlock
		{
			Foreground = _pal.Ink2,
			FontFamily = SegoeUi,
			FontSize = 12.0,
			TextWrapping = TextWrapping.Wrap,
			Margin = new Thickness(0.0, 8.0, 0.0, 0.0),
			Text = SeasonalTheme.StatusText(SeasonalTheme.Describe())
		};
		look.Children.Add(_seasonStatus);
		pg.Add(CustomRow("appearance.look", look));
		_seasonWallpaperRow = null;
		_restWallpaperRow = null;
		RebuildSeasonTiles();
		pg.Add(DrillRow("appearance.seasonal", null, delegate { Navigate("appearance/seasonal", null, push: true); }, "Wallpaper, pictures, effects, pointers and icons"));
		pg.Add(DrillRow("appearance.rest", null, delegate { Navigate("appearance/rest", null, push: true); }, "Wallpaper, picture, calmer motion and dimmer tiles of Dark rest"));
	}

	// Appearance › Seasonal options.
	private ScrollViewer SeasonalOptionsPage()
	{
		AppSettings s = SettingsStore.Load();
		Pg pg = new Pg(this, "appearance/seasonal");
		BuildSeasonalOptions(pg, s);
		LookStatusLine(pg);
		return pg.Done();
	}

	// Appearance › Dark rest options.
	private ScrollViewer RestOptionsPage()
	{
		AppSettings s = SettingsStore.Load();
		Pg pg = new Pg(this, "appearance/rest");
		BuildRestSection(pg, s);
		LookStatusLine(pg);
		return pg.Done();
	}

	// The look's status under the options of a drill-in (what Normal restores; a picture that could not be used).
	private void LookStatusLine(Pg pg)
	{
		_seasonStatus = new TextBlock
		{
			Foreground = _pal.Ink2,
			FontFamily = SegoeUi,
			FontSize = 12.0,
			TextWrapping = TextWrapping.Wrap,
			Margin = new Thickness(12.0, 8.0, 12.0, 0.0),
			Text = SeasonalTheme.StatusText(SeasonalTheme.Describe())
		};
		pg.Block(_seasonStatus);
	}

	// The options of the seasonal looks (Halloween and Christmas). None of them is part of the restore point.
	private void BuildSeasonalOptions(Pg pg, AppSettings s)
	{
		pg.Header("Seasonal");
		_seasonWallpaperRow = ToggleRow("appearance.seasonalwallpaper", s.SeasonalWallpaper, delegate(bool on)
		{
			SettingsStore.Update(delegate(AppSettings x) { x.SeasonalWallpaper = on; });
			string active = SeasonalTheme.NormalizeId(SettingsStore.FastSnapshot.SeasonalActive);
			if (SeasonalTheme.IsSeason(active))
			{
				// ON captures and sets the wallpaper; OFF restores the user's own and drops it from the restore point.
				ApplySeason(active, optionRefresh: true);
			}
		}, "Also set a matching desktop wallpaper while a seasonal theme is on. Your own wallpaper comes back with Normal.");
		_seasonWallpaperRow.IsEnabled = !_seasonBusyUi;
		pg.Add(_seasonWallpaperRow);
		bool first = true;
		foreach (SeasonPreset preset in SeasonalTheme.Seasons)
		{
			SeasonPreset p = preset;
			pg.Add(ActionRow(first ? "appearance.seasonalpictures" : "appearance.seasonalpictures." + p.Id, delegate { ChooseSeasonPicture(p); }, "Your own picture instead of the drawn scene", p.Name + " desktop picture", ""));
			first = false;
		}
		pg.Add(ActionRow("appearance.drawnscenes", delegate
		{
			foreach (SeasonPreset p in SeasonalTheme.Presets)
			{
				try
				{
					SeasonalArt.ClearCustomPicture(p.Art);
				}
				catch (Exception ex)
				{
					Logger.Log("Seasonal: could not remove the " + p.Art + " picture: " + ex.Message);
				}
			}
			RefreshSeasonPicture();
		}, "Forget the chosen pictures", glyph: ""));
		pg.Add(ToggleRow("appearance.seasonaleffects", s.SeasonalAmbient, delegate(bool on)
		{
			SettingsStore.Update(delegate(AppSettings x) { x.SeasonalAmbient = on; });
			HostApp?.ApplySeasonalLive();
		}, "Falling snow or drifting bats behind the Start tiles while Halloween or Christmas is on. Runs only while Start is open and follows the Motion setting."));
		pg.Add(ToggleRow("appearance.seasonalpointers", s.AlchemyCursorsSeasonal, delegate(bool on)
		{
			SettingsStore.Update(delegate(AppSettings x) { x.AlchemyCursorsSeasonal = on; });
			RefreshPointerPreview();
			FollowPointerChange(PointerScheme.ReconcileAsync("seasonal-toggle"));
		}, "While Halloween or Christmas is on, the Alchemy pointers take the season's colours; while Dark rest is on they use the dark style in a muted blue-grey. They go back with Normal. Choose an Alchemy pointer style under Pointers."));
		BuildSeasonalIconsRows(pg, s);
	}

	// The Dark rest options. None of them is part of the restore point; they are kept across Normal.
	private void BuildRestSection(Pg pg, AppSettings s)
	{
		pg.Header("Dark rest");
		_restWallpaperRow = ToggleRow("appearance.restwallpaper", s.RestWallpaper, delegate(bool on)
		{
			SettingsStore.Update(delegate(AppSettings x) { x.RestWallpaper = on; });
			if (SeasonalTheme.IsRest(SeasonalTheme.NormalizeId(SettingsStore.FastSnapshot.SeasonalActive)))
			{
				ApplySeason(SeasonalTheme.Rest, optionRefresh: true);
			}
		}, "Also set a calm, dark desktop wallpaper while Dark rest is on. Your own wallpaper comes back with Normal.");
		_restWallpaperRow.IsEnabled = !_seasonBusyUi;
		pg.Add(_restWallpaperRow);
		SeasonPreset? rest = SeasonalTheme.Find(SeasonalTheme.Rest);
		if (rest != null)
		{
			pg.Add(ActionRow("appearance.restpicture", delegate { ChooseSeasonPicture(rest); }, "Your own picture instead of the drawn scene", glyph: ""));
		}
		pg.Add(ToggleRow("appearance.restcalm", s.RestCalmMotion, delegate(bool on)
		{
			SettingsStore.Update(delegate(AppSettings x) { x.RestCalmMotion = on; });
			DarkRest.Sync();
		}, "While Dark rest is on, animations are shorter and move less, like Motion: Reduced. Your Motion setting itself is not changed."));
		pg.Add(ToggleRow("appearance.restdim", s.RestDimTiles, delegate(bool on)
		{
			SettingsStore.Update(delegate(AppSettings x) { x.RestDimTiles = on; });
			HostApp?.ApplySeasonalLive();
		}, "While Dark rest is on, Start tiles and the icons of the Start app list are dimmed a little so bright tile colours do not glare."));
		// Night light keeps its state in an undocumented value: Dark rest leaves it alone and links to its settings.
		pg.Add(LinkRow("appearance.nightlight", "Warmer colours in the evening (Windows)"));
		if (ShellLookLink.KnownFolderFound == true)
		{
			pg.Footnote("The Metro Tab browser extension follows Dark rest.");
		}
	}

	private TextBlock? _seasonIconsStatus;

	private FrameworkElement? _seasonTrayRow;

	// Seasonal app icons: the toggle, which apps have icons for each season, the tray choice and where to put more.
	private void BuildSeasonalIconsRows(Pg pg, AppSettings s)
	{
		pg.Add(ToggleRow("appearance.seasonalicons", s.SeasonalAppIcons, delegate(bool on)
		{
			SettingsStore.Update(delegate(AppSettings x) { x.SeasonalAppIcons = on; });
			SeasonalAppIcons.Reconcile("toggle");
			SetSeasonalTrayEnabled(on);
		}, "While Halloween or Christmas is on, apps that have a seasonal icon show it everywhere in the shell: Start, all apps, search, the taskbar and its previews, the notification area, the Windows 7 Start menu and the switcher. Normal brings their usual icons back at once."));
		_seasonIconsStatus = new TextBlock
		{
			Foreground = _pal.Ink2,
			FontFamily = SegoeUi,
			FontSize = 12.0,
			TextWrapping = TextWrapping.Wrap,
			Text = SeasonalIconsStatusText()
		};
		pg.Add(CustomRow(null, _seasonIconsStatus, new Thickness(12.0, 8.0, 12.0, 8.0)));
		// Icons added to the folder since the last look are picked up right after the page is built (Reconcile runs on
		// the UI thread; it reads the folder listing only unless something changed).
		if (SettingsWorld.IsReal)
		{
			TextBlock status = _seasonIconsStatus;
			_pending++;
			base.Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, (Action)delegate
			{
				_pending = Math.Max(0, _pending - 1);
				try
				{
					SeasonalAppIcons.Reconcile("settings");
					status.Text = SeasonalIconsStatusText();
				}
				catch (Exception ex)
				{
					Logger.Log("Seasonal icons: " + ex.Message);
				}
			});
		}
		string[] trayOpts = new string[2] { "Seasonal, with live status", "Always the app's own icon" };
		bool trayOriginal = string.Equals(s.SeasonalTrayIcons, "original", StringComparison.OrdinalIgnoreCase);
		_seasonTrayRow = ChoiceRow("appearance.trayicons", trayOpts, trayOriginal ? 1 : 0, delegate(int i)
		{
			string v = i == 1 ? "original" : "live";
			SettingsStore.Update(delegate(AppSettings x) { x.SeasonalTrayIcons = v; });
			TaskbarWindow.RaiseSeasonalTrayChanged();
		}, SeasonalTrayHelpText, width: 220.0);
		pg.Add(_seasonTrayRow);
		SetSeasonalTrayEnabled(s.SeasonalAppIcons);
		pg.Add(ActionRow("appearance.addicons", OpenSeasonalIconsFolder, null, glyph: ""));
	}

	private void SetSeasonalTrayEnabled(bool on)
	{
		if (_seasonTrayRow != null)
		{
			_seasonTrayRow.IsEnabled = on;
			_seasonTrayRow.Opacity = on ? 1.0 : 0.45;
		}
	}

	internal const string SeasonalTrayHelpText = "With live status, a seasonal icon in the notification area still shows the app's status: an unread dot or count is drawn on the seasonal picture, and while the app shows something else, such as a call, a muted microphone or an error, its own icon comes back until it is idle again.";

	internal const string SeasonalIconsHelpText = "Put a PNG named after the app's program file in the seasonal\\halloween-icons or seasonal\\christmas-icons folder next to the launcher, for example discord.png for Discord.exe. For pixel-exact small icons, add copies with the size in the name, such as discord-16.png. At 100% scaling the shell draws 16, 18, 22, 24, 27, 32, 34 and 36 px; tiles are scaled from a picture of 256 px or more. New icons show the next time you open this page or change the season.";

	// "Halloween icons: Chrome, Discord. Christmas icons: none yet."
	internal static string SeasonalIconsStatusText()
	{
		StringBuilder sb = new StringBuilder();
		foreach (SeasonPreset p in SeasonalTheme.Seasons)
		{
			IReadOnlyList<string> keys = SeasonalAppIcons.KeysFor(p.Id);
			if (sb.Length > 0)
			{
				sb.Append(' ');
			}
			sb.Append(p.Name).Append(" icons: ");
			if (keys.Count == 0)
			{
				sb.Append("none yet.");
				continue;
			}
			sb.Append(string.Join(", ", keys.Take(8).Select((string k) => k.Length > 0 ? char.ToUpperInvariant(k[0]) + k.Substring(1) : k)));
			if (keys.Count > 8)
			{
				sb.Append(" and ").Append(keys.Count - 8).Append(" more");
			}
			sb.Append('.');
		}
		return sb.ToString();
	}

	// Creates the seasonal icon folders when they are missing (folders only) and opens them in File Explorer.
	private static void OpenSeasonalIconsFolder()
	{
		if (SettingsStore.ReadOnlyDiagnostics)
		{
			return;
		}
		string root = SeasonalAppIcons.RootDir;
		try
		{
			foreach (SeasonPreset p in SeasonalTheme.Seasons)
			{
				Directory.CreateDirectory(SeasonalAppIcons.FolderFor(p.Id));
			}
		}
		catch (Exception ex)
		{
			Logger.Log("Seasonal icons: could not create the folders: " + ex.Message);
			return;
		}
		SettingsLink.Shell(root);
	}

	private void ChooseSeasonPicture(SeasonPreset p)
	{
		Microsoft.Win32.OpenFileDialog dlg = new Microsoft.Win32.OpenFileDialog
		{
			Title = "Choose a " + p.Name + " desktop picture",
			Filter = "Pictures (*.jpg;*.jpeg;*.png;*.bmp)|*.jpg;*.jpeg;*.png;*.bmp"
		};
		if (dlg.ShowDialog(this) != true)
		{
			return;
		}
		try
		{
			SeasonalArt.SetCustomPicture(p.Art, dlg.FileName);
		}
		catch (Exception ex)
		{
			if (_seasonStatus != null)
			{
				_seasonStatus.Text = "That picture could not be used: " + ex.Message;
			}
			return;
		}
		RefreshSeasonPicture();
	}

	// After a picture change: refresh the previews, and when that season is on, put the new picture on the desktop.
	private void RefreshSeasonPicture()
	{
		RebuildSeasonTiles();
		string active = SeasonalTheme.NormalizeId(SettingsStore.FastSnapshot.SeasonalActive);
		if (active.Length > 0 && !_seasonBusyUi)
		{
			ApplySeason(active, optionRefresh: true);
		}
	}

	private void RebuildSeasonTiles()
	{
		if (_seasonTiles == null)
		{
			return;
		}
		_seasonTiles.Children.Clear();
		string active = SeasonalTheme.NormalizeId(SettingsStore.FastSnapshot.SeasonalActive);
		foreach ((string id, string label) in SeasonalTheme.Choices)
		{
			bool selected = (id == SeasonalTheme.Normal) ? active.Length == 0 : active == id;
			_seasonTiles.Children.Add(SeasonTile(id, label, selected));
		}
		_seasonTiles.IsHitTestVisible = !_seasonBusyUi;
		_seasonTiles.Opacity = _seasonBusyUi ? 0.6 : 1.0;
		// The running change has already read these preferences; they can be changed again once it finishes.
		if (_seasonWallpaperRow != null)
		{
			_seasonWallpaperRow.IsEnabled = !_seasonBusyUi;
		}
		if (_restWallpaperRow != null)
		{
			_restWallpaperRow.IsEnabled = !_seasonBusyUi;
		}
	}

	// A look tile: a 112x64 button with the season's scene (Normal previews what it brings back, under the undo glyph),
	// its name on a caption strip and a frame when it is the look that is on.
	private Button SeasonTile(string id, string label, bool selected)
	{
		SeasonPreset? preset = SeasonalTheme.Find(id);
		Grid g = new Grid
		{
			Width = 108.0,
			Height = 60.0,
			ClipToBounds = true
		};
		if (preset == null)
		{
			g.Background = SeasonalTheme.NormalPreviewBrush(0.5);
			g.Children.Add(new Border { Background = new SolidColorBrush(Color.FromArgb(90, 0, 0, 0)) });
			g.Children.Add(new TextBlock
			{
				Text = "",
				FontFamily = Glyphs,
				FontSize = 20.0,
				Foreground = Brushes.White,
				HorizontalAlignment = HorizontalAlignment.Center,
				VerticalAlignment = VerticalAlignment.Center,
				Margin = new Thickness(0.0, 0.0, 0.0, 16.0),
				Effect = new DropShadowEffect { BlurRadius = 4.0, ShadowDepth = 0.0, Opacity = 0.8 }
			});
		}
		else
		{
			g.Background = SeasonalArt.PreviewBrush(preset.Art);
		}
		if (selected)
		{
			g.Children.Add(new TextBlock
			{
				Text = "",
				FontFamily = Glyphs,
				FontSize = 12.0,
				Foreground = Brushes.White,
				HorizontalAlignment = HorizontalAlignment.Right,
				VerticalAlignment = VerticalAlignment.Top,
				Margin = new Thickness(0.0, 4.0, 5.0, 0.0),
				Effect = new DropShadowEffect { BlurRadius = 3.0, ShadowDepth = 0.0, Opacity = 0.9 }
			});
		}
		Border cap = new Border
		{
			Height = 20.0,
			VerticalAlignment = VerticalAlignment.Bottom,
			Background = selected ? new SolidColorBrush(TileStripColor()) : new SolidColorBrush(Color.FromArgb(204, 0, 0, 0)),
			Child = new TextBlock
			{
				Text = label,
				Foreground = Brushes.White,
				FontFamily = SegoeUi,
				FontSize = 11.0,
				TextTrimming = TextTrimming.CharacterEllipsis,
				VerticalAlignment = VerticalAlignment.Center,
				HorizontalAlignment = HorizontalAlignment.Center
			}
		};
		g.Children.Add(cap);
		Border frame = new Border
		{
			Child = g,
			// constant thickness, so selecting a tile never shifts its content
			BorderThickness = new Thickness(2.0),
			// on the Dark rest page the accent tone would be too dark to see: the selected tile gets the page ink
			BorderBrush = !selected ? Brushes.Transparent : (DarkRest.Visuals ? DarkRestPalette.PcText : new SolidColorBrush(HubPalette.UntilContrast(SettingsPane.AccentToneColor(0.5), _pal.CardColor, 3.0, darker: !_pal.Dark)))
		};
		string name = SeasonalTheme.IsRest(id) ? SeasonalTheme.RestTooltip : "Seasonal theme: " + label;
		Button tile = new Button
		{
			Template = _pal.PlainTemplate,
			Style = BareButton,
			Content = frame,
			Width = 112.0,
			Height = 64.0,
			Margin = new Thickness(0.0, 0.0, 8.0, 0.0),
			Cursor = Cursors.Hand,
			ToolTip = (preset == null) ? "Back to your own look" : (SeasonalTheme.IsRest(id) ? SeasonalTheme.RestTooltip : label)
		};
		tile.SetResourceReference(FrameworkElement.FocusVisualStyleProperty, "Metro81.Focus");
		AutomationProperties.SetName(tile, name);
		tile.Click += delegate { ApplySeason(id); };
		if (DarkRest.Visuals && !selected)
		{
			// Dark rest: the previews of the other looks at 0.6 opacity (full under the mouse), so they do not glare
			tile.Opacity = 0.6;
			tile.MouseEnter += delegate { tile.Opacity = 1.0; };
			tile.MouseLeave += delegate { tile.Opacity = 0.6; };
		}
		return tile;
	}

	// QA: the Win8.1 wallpaper row as last built while it was unavailable (its opacity and the inks of its title and of
	// the sentence that explains it); null when it was not shown that way.
	internal (double Opacity, Color Title, Color Desc)? QaWallpaperRow { get; private set; }

	// QA: the look tiles (count, the accessible name of the selected one); the Appearance page is built for them when
	// the page shown last does not have them.
	internal (int Count, string Selected) QaSeasonTiles()
	{
		(int count, string selected, _) = QaSeasonTilesWithFrame();
		return (count, selected);
	}

	internal (int Count, string Selected, Color Frame) QaSeasonTilesWithFrame()
	{
		if (_seasonTiles == null || _seasonTilesGen != _buildGen)
		{
			BuildPageCore("appearance");
		}
		if (_seasonTiles == null)
		{
			return (-1, "", Colors.Transparent);
		}
		string selected = "";
		Color frame = Colors.Transparent;
		foreach (UIElement child in _seasonTiles.Children)
		{
			if (child is Button { Content: Border tile } b && tile.BorderBrush is SolidColorBrush sb && sb.Color.A > 0)
			{
				selected = AutomationProperties.GetName(b);
				frame = sb.Color;
			}
		}
		return (_seasonTiles.Children.Count, selected, frame);
	}

	// Applies at once: every step is reversible, so there is no separate Apply button. optionRefresh: an option of the
	// preset that is on changed (its wallpaper option or picture); Windows light/dark is then left as it is.
	private async void ApplySeason(string id, bool optionRefresh = false)
	{
		if (_seasonBusyUi)
		{
			return;
		}
		_seasonBusyUi = true;
		// the journal keeps "Normal → Dark rest"; an undo (which runs this same path) adds no entry of its own
		string lookBefore = SeasonalTheme.NormalizeId(SettingsStore.FastSnapshot.SeasonalActive);
		bool journalThis = _journalSuppress == 0 && !optionRefresh;
		RebuildSeasonTiles();
		SeasonPreset? preset = SeasonalTheme.Find(id);
		if (_seasonStatus != null)
		{
			_seasonStatus.Text = preset == null ? "Restoring your normal look…" : "Applying " + preset.Name + "…";
		}
		bool wallpaperPrefBefore = SeasonalTheme.WantsWallpaper(SettingsStore.FastSnapshot, id);
		SeasonalResult result;
		try
		{
			result = await SeasonalTheme.ApplyAsync(id, keepTheme: optionRefresh);
		}
		catch (Exception ex)
		{
			result = new SeasonalResult { Success = false, Message = "Could not change the seasonal theme: " + ex.Message };
		}
		finally
		{
			_seasonBusyUi = false;
		}
		// The live refresh also re-skins this window when the look changed (the page is built again).
		HostApp?.ApplySeasonalLive();
		SyncRestSkin();
		ReskinShell();
		RebuildSeasonTiles();
		RefreshPointerPreview();
		FollowPointerChange(PointerScheme.Pending);
		string activeAfter = SeasonalTheme.NormalizeId(SettingsStore.FastSnapshot.SeasonalActive);
		if (result.Success && journalThis && !_closed)
		{
			JournalLook(lookBefore, activeAfter);
		}
		if (result.Success && activeAfter.Length > 0 && SeasonalTheme.WantsWallpaper(SettingsStore.FastSnapshot, activeAfter) != wallpaperPrefBefore)
		{
			// The wallpaper preference changed while this ran (the change had already read it): apply it now.
			ApplySeason(activeAfter, optionRefresh: true);
			return;
		}
		SeasonalStatus status = SeasonalTheme.Describe();
		string name = preset?.Name ?? "";
		bool plain = result.Message == name + " is on." || result.Message == name + " is already on." || result.Message == "Normal look restored.";
		string message;
		if (result.Success && plain)
		{
			message = SeasonalTheme.StatusText(status);
		}
		else if (result.Success && status.ActiveId.Length > 0)
		{
			message = result.Message + " " + SeasonalTheme.NormalRestoresText(status);
		}
		else
		{
			message = result.Message;
		}
		if (_seasonStatus != null)
		{
			_seasonStatus.Text = message;
		}
	}

	private void ReskinShell()
	{
		try
		{
			TaskbarWindow.RaiseTaskbarColorChanged(invalidateWallpaper: false);
			Win81Window.RefreshAllAccents();
		}
		catch
		{
		}
	}

	// ===== Colour =====

	// One accent palette (Automatic first, then the 20 Metro colours) for Start and, unless frames keep their own, the
	// window frames; with frames: the 15 Windows 8.1 window-border colours for the frames only.
	private FrameworkElement AccentSwatches(bool frames)
	{
		StackPanel host = new StackPanel();
		host.Children.Add(new TextBlock
		{
			Text = frames ? "Window frames" : "Accent colour",
			FontFamily = SegoeUi,
			FontSize = 13.5,
			Foreground = _pal.Ink,
			Margin = new Thickness(0.0, 0.0, 0.0, 8.0)
		});
		WrapPanel wp = new WrapPanel();
		host.Children.Add(wp);
		List<(Button Button, Border Ring, string Key, TextBlock Check)> rings = new List<(Button, Border, string, TextBlock)>();
		List<(string Key, Brush Fill, string Tip, Color Ink)> entries = new List<(string, Brush, string, Color)>();
		if (!frames)
		{
			LinearGradientBrush auto = new LinearGradientBrush { StartPoint = new Point(0.0, 0.0), EndPoint = new Point(1.0, 1.0) };
			auto.GradientStops.Add(new GradientStop(Color.FromRgb(232, 17, 35), 0.0));
			auto.GradientStops.Add(new GradientStop(Color.FromRgb(232, 203, 76), 0.25));
			auto.GradientStops.Add(new GradientStop(Color.FromRgb(69, 192, 106), 0.5));
			auto.GradientStops.Add(new GradientStop(Color.FromRgb(93, 176, 239), 0.75));
			auto.GradientStops.Add(new GradientStop(Color.FromRgb(146, 150, 236), 1.0));
			auto.Freeze();
			entries.Add(("", auto, "Automatic (from the Start background)", Colors.White));
		}
		foreach (string hex in frames ? AuthenticAccents : Palette)
		{
			Color c = HubPalette.ParseHex(hex, Colors.Gray);
			entries.Add((hex, HubPalette.Frozen(c), hex, ColorMath.ReadableInk(c)));
		}
		void Refresh()
		{
			AppSettings now = SettingsStore.Load();
			string cur = ((frames ? now.FrameAccentColor : now.StartAccentColor) ?? "").Trim();
			foreach ((Button _, Border ring, string key, TextBlock check) in rings)
			{
				bool sel = string.Equals(key, cur, StringComparison.OrdinalIgnoreCase);
				ring.BorderBrush = sel ? _pal.Ink : Brushes.Transparent;
				check.Visibility = sel ? Visibility.Visible : Visibility.Collapsed;
			}
		}
		foreach ((string key, Brush fill, string tip, Color ink) in entries)
		{
			string k = key;
			TextBlock check = new TextBlock
			{
				Text = "",
				FontFamily = Glyphs,
				FontSize = 11.0,
				Foreground = HubPalette.Frozen(ink),
				HorizontalAlignment = HorizontalAlignment.Center,
				VerticalAlignment = VerticalAlignment.Center,
				Visibility = Visibility.Collapsed
			};
			Border sw = new Border { Width = 20.0, Height = 20.0, Background = fill, Child = check };
			Border ring = new Border
			{
				BorderThickness = new Thickness(2.0),
				BorderBrush = Brushes.Transparent,
				Child = sw
			};
			Button b = new Button
			{
				Template = _pal.PlainTemplate,
				Style = BareButton,
				Content = ring,
				Width = 24.0,
				Height = 24.0,
				Margin = new Thickness(0.0, 0.0, 4.0, 4.0),
				Cursor = Cursors.Hand,
				ToolTip = tip
			};
			b.SetResourceReference(FrameworkElement.FocusVisualStyleProperty, "Metro81.Focus");
			AutomationProperties.SetName(b, (frames ? "Frame colour " : "Accent colour ") + (k.Length == 0 ? "Automatic" : k));
			b.Click += delegate
			{
				SettingsStore.Update(delegate(AppSettings x)
				{
					if (frames)
					{
						x.FrameAccentColor = k;
					}
					else
					{
						x.StartAccentColor = k;
					}
				});
				AfterAccentChange();
				Refresh();
			};
			rings.Add((b, ring, k, check));
			wp.Children.Add(b);
		}
		Refresh();
		return host;
	}

	private void AfterAccentChange()
	{
		try
		{
			DesktopComposition.RefreshAccent();
		}
		catch
		{
		}
		ReskinShell();
		RebuildCompTiles();
		RefreshCompStatus();
		base.Dispatcher.BeginInvoke(DispatcherPriority.Background, (Action)SyncRestSkin);
	}

	// ===== Pictures =====

	private FrameworkElement? Win81WallpaperRow(AppSettings s)
	{
		if (!WallpaperModule.AssetAvailable)
		{
			return null;
		}
		// While the seasonal wallpaper is on, the Win8.1 wallpaper snapshot would capture the seasonal picture.
		bool seasonalOwnsWallpaper = SeasonalTheme.OwnsDesktopWallpaper;
		string help = seasonalOwnsWallpaper ? "Unavailable while a seasonal or Dark rest wallpaper is on — choose Normal under Look first." : "Set the authentic Win8.1 desktop wallpaper (reversible).";
		FrameworkElement row = ToggleRow("appearance.wallpaper81", s.Win81Wallpaper, delegate(bool on)
		{
			if (SeasonalTheme.OwnsDesktopWallpaper)
			{
				Logger.Log("Win8.1 wallpaper unavailable while the seasonal wallpaper is on");
				return;
			}
			if (on ? WallpaperModule.Apply() : WallpaperModule.Revert())
			{
				SettingsStore.Update(delegate(AppSettings x) { x.Win81Wallpaper = on; });
			}
		}, secondary: seasonalOwnsWallpaper ? "Unavailable while a seasonal look is on" : null, help: help);
		if (seasonalOwnsWallpaper)
		{
			row.IsEnabled = false;
			TextBlock? title = Descendants<TextBlock>(row).FirstOrDefault();
			TextBlock? desc = row is Grid g ? FindSecondary(g) : null;
			if (DarkRest.Visuals)
			{
				// On the black page a half-transparent row would put its explanation below 3:1: the row keeps full opacity
				// and its title takes the hint ink.
				if (title != null)
				{
					title.Foreground = DarkRestPalette.Hint;
				}
			}
			else
			{
				row.Opacity = 0.5;
			}
			QaWallpaperRow = (row.Opacity, (title?.Foreground as SolidColorBrush)?.Color ?? Colors.Transparent, (desc?.Foreground as SolidColorBrush)?.Color ?? Colors.Transparent);
		}
		return row;
	}

	// Lock screen: a preview of the chosen picture (decoded at its display size off the UI thread), the preset pictures
	// and Browse; Windows' own page for everything else.
	private FrameworkElement LockScreenRow()
	{
		StackPanel host = new StackPanel();
		Grid head = new Grid();
		head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.0, GridUnitType.Star) });
		head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
		head.Children.Add(new TextBlock
		{
			Text = HubCatalog.Title("appearance.lockscreen"),
			FontFamily = SegoeUi,
			FontSize = 13.5,
			Foreground = _pal.Ink,
			VerticalAlignment = VerticalAlignment.Center
		});
		StackPanel buttons = new StackPanel { Orientation = Orientation.Horizontal };
		Border preview = new Border
		{
			Width = 160.0,
			Height = 90.0,
			HorizontalAlignment = HorizontalAlignment.Left,
			BorderBrush = _pal.CardBorder,
			BorderThickness = new Thickness(1.0),
			Background = _pal.Field,
			Margin = new Thickness(0.0, 8.0, 0.0, 8.0)
		};
		void SetPreview(string path)
		{
			int px = DevicePixels(158.0);
			ReadAsync(() => Thumb(path, px), delegate(BitmapSource? bmp)
			{
				if (bmp != null)
				{
					preview.Background = new ImageBrush(bmp) { Stretch = Stretch.UniformToFill };
				}
			});
		}
		buttons.Children.Add(HubButton("Browse…", delegate
		{
			Microsoft.Win32.OpenFileDialog dlg = new Microsoft.Win32.OpenFileDialog
			{
				Filter = "Pictures|*.jpg;*.jpeg;*.png;*.bmp",
				Title = "Choose a lock screen picture"
			};
			if (dlg.ShowDialog(this) == true)
			{
				SetPreview(dlg.FileName);
				ApplyLockScreen(dlg.FileName);
			}
		}));
		Grid.SetColumn(buttons, 1);
		head.Children.Add(buttons);
		host.Children.Add(head);
		host.Children.Add(preview);
		WrapPanel thumbs = new WrapPanel();
		List<string> presets;
		try
		{
			presets = Wallpapers.Presets();
		}
		catch
		{
			presets = new List<string>();
		}
		if (presets.Count > 0)
		{
			SetPreview(presets[0]);
		}
		int tpx = DevicePixels(70.0);
		foreach (string wp in presets.Take(12))
		{
			string path = wp;
			Border face = new Border { Width = 72.0, Height = 45.0, BorderBrush = _pal.CardBorder, BorderThickness = new Thickness(1.0), Background = _pal.Field };
			Button b = new Button
			{
				Template = _pal.PlainTemplate,
				Style = BareButton,
				Content = face,
				Margin = new Thickness(0.0, 0.0, 6.0, 6.0),
				Cursor = Cursors.Hand,
				ToolTip = Path.GetFileNameWithoutExtension(wp)
			};
			b.SetResourceReference(FrameworkElement.FocusVisualStyleProperty, "Metro81.Focus");
			AutomationProperties.SetName(b, "Lock screen picture " + Path.GetFileNameWithoutExtension(wp));
			b.Click += delegate
			{
				SetPreview(path);
				ApplyLockScreen(path);
			};
			ReadAsync(() => Thumb(path, tpx), delegate(BitmapSource? bmp)
			{
				if (bmp != null)
				{
					face.Background = new ImageBrush(bmp) { Stretch = Stretch.UniformToFill };
				}
			});
			thumbs.Children.Add(b);
		}
		host.Children.Add(thumbs);
		FrameworkElement? more = LinkRow("appearance.lockscreen", null, null, "Lock screen settings");
		if (more != null)
		{
			more.Margin = new Thickness(-12.0, 4.0, -12.0, -12.0);
			host.Children.Add(more);
		}
		return CustomRow("appearance.lockscreen", host);
	}

	private async void ApplyLockScreen(string path)
	{
		try
		{
			await SettingsWorld.Current.SetLockScreenAsync(path);
			Logger.Log("Lock screen set: " + path);
		}
		catch (Exception ex)
		{
			Logger.Log("Lock screen set failed (" + ex.Message + "); routing to native.");
			SettingsLink.Open(LinkSpec.Page("ms-settings:lockscreen"), _caps);
		}
	}

	// Device pixels for a DIP width on this window's monitor.
	private int DevicePixels(double dip)
	{
		double k = 1.0;
		try
		{
			k = VisualTreeHelper.GetDpi(this).DpiScaleX;
		}
		catch
		{
		}
		return Math.Max(16, (int)Math.Ceiling(dip * k));
	}

	// A picture decoded at the given width (thread-safe: frozen), or null.
	internal static BitmapSource? Thumb(string path, int width)
	{
		try
		{
			BitmapImage bmp = new BitmapImage();
			bmp.BeginInit();
			bmp.UriSource = new Uri(path);
			bmp.CacheOption = BitmapCacheOption.OnLoad;
			bmp.DecodePixelWidth = width;
			bmp.EndInit();
			bmp.Freeze();
			return bmp;
		}
		catch
		{
			return null;
		}
	}

	// ===== Pointers (drill-in) =====

	private ScrollViewer PointersPage()
	{
		Pg pg = new Pg(this, "appearance/pointers");
		BuildPointerSection(pg, SettingsStore.FastSnapshot);
		pg.Header("More");
		pg.Add(LinkRow("mouse.size"));
		pg.Add(LinkRow("mouse.properties"));
		return pg.Done();
	}

	private TextBlock? _pointerStatus;

	private WrapPanel? _pointerPreview;

	// Preview frames of this window only; dropped when the window closes, so a closed PC Settings keeps no images.
	private readonly Dictionary<string, BitmapSource> _pointerPreviewCache = new Dictionary<string, BitmapSource>(StringComparer.Ordinal);

	private static readonly string[] PointerRoleNames = new string[17]
	{
		"Normal select", "Help select", "Working in background", "Busy", "Precision select", "Text select", "Handwriting", "Unavailable",
		"Vertical resize", "Horizontal resize", "Diagonal resize 1", "Diagonal resize 2", "Move", "Alternate select", "Link select",
		"Location select", "Person select"
	};

	// One choice for every pointer role: Windows' own, the Windows 8.1 set or the Alchemy set in a tone. Every choice
	// applies at once and Windows default always brings back the user's own pointers.
	private void BuildPointerSection(Pg pg, AppSettings s)
	{
		pg.Header("Pointers");
		ComboBox? combo = null;
		FrameworkElement row = ChoiceRow("pointers.scheme", PointerScheme.ChoiceLabels, PointerScheme.ChoiceIndex(s), async delegate(int i)
		{
			try
			{
				SettingsStore.Update(delegate(AppSettings x) { PointerScheme.SetChoice(x, i); });
				if (_pointerStatus != null)
				{
					_pointerStatus.Text = i >= 2 ? "Preparing the Alchemy pointers…" : "Changing pointers…";
				}
				if (combo != null)
				{
					combo.IsEnabled = false;
				}
				RefreshPointerPreview();
				PointerResult r = await PointerScheme.ApplyAsync("settings");
				RefreshPointerStatus(r);
			}
			catch (Exception ex)
			{
				Logger.Log("Pointers: settings change failed: " + ex.Message);
				RefreshPointerStatus(null);
			}
			finally
			{
				if (combo != null)
				{
					combo.IsEnabled = true;
				}
			}
		}, "Alchemy pointers replace every Windows pointer, including animated busy and working pointers. While Halloween or Christmas is on they can take the season's colours, and while Dark rest is on they use the dark style. Windows default puts back exactly the pointers you had before.", width: 240.0);
		combo = Descendants<ComboBox>(row).FirstOrDefault();
		pg.Add(row);
		StackPanel body = new StackPanel();
		// Two even rows of nine and eight tiles.
		_pointerPreview = new WrapPanel { MaxWidth = 9 * 50.0, HorizontalAlignment = HorizontalAlignment.Left };
		body.Children.Add(_pointerPreview);
		_pointerStatus = new TextBlock
		{
			Foreground = _pal.Ink2,
			FontFamily = SegoeUi,
			FontSize = 12.0,
			TextWrapping = TextWrapping.Wrap,
			Margin = new Thickness(0.0, 4.0, 0.0, 0.0)
		};
		body.Children.Add(_pointerStatus);
		pg.Add(CustomRow(null, body));
		FollowPointerEvents(_pointerStatus);
		RefreshPointerPreview();
		RefreshPointerStatus(null);
	}

	// While the section is on screen: a light/dark flip redraws the preview in the other tone (Alchemy auto follows
	// it), and every finished pointer change, wherever it started, updates the status line.
	private void FollowPointerEvents(FrameworkElement section)
	{
		bool subscribed = false;
		void OnTheme(object? sender, EventArgs e)
		{
			base.Dispatcher.BeginInvoke((Action)delegate
			{
				if (_pointerStatus == section)
				{
					RefreshPointerPreview();
					RefreshPointerStatus(null);
				}
			});
		}
		void OnPointerState()
		{
			base.Dispatcher.BeginInvoke((Action)delegate
			{
				if (_pointerStatus == section)
				{
					RefreshPointerStatus(null);
				}
			});
		}
		section.Loaded += delegate
		{
			if (!subscribed)
			{
				subscribed = true;
				ShellTheme.Changed += OnTheme;
				PointerScheme.StateChanged += OnPointerState;
			}
		};
		section.Unloaded += delegate
		{
			if (subscribed)
			{
				subscribed = false;
				ShellTheme.Changed -= OnTheme;
				PointerScheme.StateChanged -= OnPointerState;
			}
		};
	}

	private void RefreshPointerStatus(PointerResult? r)
	{
		if (_pointerStatus == null)
		{
			return;
		}
		PointerStatus status = PointerScheme.Describe();
		_pointerStatus.Text = (r != null && (!r.Success || r.ShowsAfterSignIn) && !status.Busy) ? r.Message : PointerScheme.StatusText(status);
	}

	// Shows the status once a change started elsewhere (a season, the seasonal toggle) has finished.
	private async void FollowPointerChange(Task? change)
	{
		try
		{
			if (change != null && !change.IsCompleted)
			{
				if (_pointerStatus != null)
				{
					_pointerStatus.Text = PointerScheme.StatusText(PointerScheme.Describe());
				}
				await change;
			}
		}
		catch (Exception ex)
		{
			Logger.Log("Pointers: follow-up failed: " + ex.Message);
		}
		RefreshPointerStatus(null);
	}

	// The 17 pointers of the palette the setting shows now, drawn in memory (no files). Hidden unless Alchemy is chosen.
	private void RefreshPointerPreview()
	{
		WrapPanel? host = _pointerPreview;
		if (host == null)
		{
			return;
		}
		host.Children.Clear();
		AppSettings s = SettingsStore.FastSnapshot;
		string paletteId = AlchemyPalettes.Resolve(s.AlchemyCursors, ShellTheme.IsDark, s.SeasonalActive, s.AlchemyCursorsSeasonal);
		AlchemyPalette palette = AlchemyPalettes.Find(paletteId);
		host.Visibility = palette == null ? Visibility.Collapsed : Visibility.Visible;
		if (palette == null)
		{
			return;
		}
		int px = PointerPreviewSize();
		for (int i = 0; i < AlchemyCursorArt.Roles.Length; i++)
		{
			host.Children.Add(PointerTile(AlchemyCursorArt.Roles[i], PointerRoleNames[i], palette, px));
		}
	}

	private int PointerPreviewSize()
	{
		double scale = 1.0;
		try
		{
			scale = VisualTreeHelper.GetDpi(this).DpiScaleX;
		}
		catch
		{
		}
		double want = 32.0 * scale;
		return AlchemyCursorArt.Sizes.OrderBy((int v) => Math.Abs(v - want)).First();
	}

	private BitmapSource PointerFrame(CursorRole role, AlchemyPalette palette, int px, int frame)
	{
		string key = palette.Id + "|" + role + "|" + px + "|" + frame;
		if (_pointerPreviewCache.TryGetValue(key, out BitmapSource? hit))
		{
			return hit;
		}
		byte[] bgra = AlchemyCursorArt.RenderBgra(role, palette, px, frame);
		BitmapSource bmp = BitmapSource.Create(px, px, 96.0, 96.0, PixelFormats.Bgra32, null, bgra, px * 4);
		bmp.Freeze();
		if (_pointerPreviewCache.Count > 200)
		{
			_pointerPreviewCache.Clear();
		}
		_pointerPreviewCache[key] = bmp;
		return bmp;
	}

	private FrameworkElement PointerTile(CursorRole role, string name, AlchemyPalette palette, int px)
	{
		const int still = 6;
		Image img = new Image
		{
			Width = 32.0,
			Height = 32.0,
			Stretch = Stretch.Uniform,
			HorizontalAlignment = HorizontalAlignment.Center,
			VerticalAlignment = VerticalAlignment.Center,
			Source = PointerFrame(role, palette, px, AlchemyCursorArt.IsAnimated(role) ? still : 0)
		};
		RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);
		Border tile = new Border
		{
			Width = 44.0,
			Height = 44.0,
			BorderThickness = new Thickness(1.0),
			Background = palette.Tone == AlchemyPalettes.Dark ? HubPalette.Frozen(Color.FromRgb(0x1E, 0x1E, 0x1E)) : HubPalette.Frozen(Color.FromRgb(0xF3, 0xF3, 0xF3)),
			Margin = new Thickness(0.0, 0.0, 6.0, 6.0),
			ToolTip = name,
			Child = img
		};
		tile.SetResourceReference(Border.BorderBrushProperty, "Metro81.Border");
		AutomationProperties.SetName(tile, name);
		if (AlchemyCursorArt.IsAnimated(role))
		{
			// Animates only while hovered; the timer stops on leave and unload, so an open page costs nothing.
			DispatcherTimer? timer = null;
			int frame = still;
			void Stop()
			{
				timer?.Stop();
				timer = null;
				frame = still;
				img.Source = PointerFrame(role, palette, px, still);
			}
			tile.MouseEnter += delegate
			{
				if (timer != null)
				{
					return;
				}
				timer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(33.0) };
				timer.Tick += delegate
				{
					frame = (frame + 1) % AlchemyCursorArt.AnimFrames;
					img.Source = PointerFrame(role, palette, px, frame);
				};
				timer.Start();
			};
			tile.MouseLeave += delegate { Stop(); };
			tile.Unloaded += delegate { Stop(); };
		}
		return tile;
	}

	// QA: the Pointers page, and the Look section with the pointers, rendered offscreen.
	internal void QaRenderPointers(string outPath, double w = 900.0)
	{
		SyncRestSkin();
		Pg pg = new Pg(this, "appearance/pointers");
		BuildPointerSection(pg, SettingsStore.FastSnapshot);
		QaRenderDetached(pg.Done(), outPath, w);
	}

	internal void QaRenderSeasonalPointers(string outPath, double w = 900.0)
	{
		SyncRestSkin();
		Pg pg = new Pg(this, "appearance");
		AppSettings s = SettingsStore.FastSnapshot;
		BuildLookSection(pg, s);
		BuildSeasonalOptions(pg, s);
		BuildPointerSection(pg, s);
		QaRenderDetached(pg.Done(), outPath, w);
	}

	private void QaRenderDetached(ScrollViewer page, string outPath, double w)
	{
		QaPrepareDetached(page);
		QaWaitReads();
		page.VerticalScrollBarVisibility = ScrollBarVisibility.Disabled;
		page.Width = w;
		page.Measure(new Size(w, double.PositiveInfinity));
		double h = Math.Min(8000.0, Math.Max(200.0, page.DesiredSize.Height));
		page.Arrange(new Rect(0.0, 0.0, w, h));
		page.UpdateLayout();
		SavePng(page, (int)w, (int)h, outPath);
	}

	// ===== Window frames (the Desktop composition page, compacted) =====

	private string _compSel = "";

	private WrapPanel? _compTiles;

	private StackPanel? _compStatus;

	private string _compStatusSignature = "";

	private DispatcherTimer? _compStatusTimer;

	private DispatcherTimer? _blurDebounce;

	private ScrollViewer FramesPage()
	{
		_compSel = CompositionProfiles.Resolve(DesktopComposition.Mode).Id;
		AppSettings s = SettingsStore.Load();
		Pg pg = new Pg(this, "appearance/frames");
		pg.Header("Mode");
		StackPanel mode = new StackPanel();
		_compTiles = new WrapPanel();
		mode.Children.Add(_compTiles);
		RebuildCompTiles();
		WrapPanel actions = new WrapPanel { Margin = new Thickness(0.0, 10.0, 0.0, 0.0) };
		void AddAction(string text, bool primary, Action act)
		{
			Button b = HubButton(text, delegate
			{
				act();
				AfterCompChange();
			}, accent: primary);
			b.Margin = new Thickness(0.0, 0.0, 8.0, 6.0);
			actions.Children.Add(b);
		}
		AddAction("Apply", primary: true, delegate { DesktopComposition.SetMode(_compSel); });
		AddAction("Preview (10 s)", primary: false, delegate { DesktopComposition.PreviewMode(_compSel, 10); });
		AddAction("Restore previous", primary: false, delegate
		{
			DesktopComposition.RestorePrevious();
			_compSel = CompositionProfiles.Resolve(DesktopComposition.Mode).Id;
		});
		AddAction("Windows default", primary: false, delegate
		{
			DesktopComposition.RestoreWindowsDefault();
			_compSel = "native";
		});
		AddAction("Reset", primary: false, delegate
		{
			DesktopComposition.Reset();
			_compSel = "native";
		});
		mode.Children.Add(actions);
		pg.Add(CustomRow("frames.mode", mode));
		pg.Footnote("Uses the modern Windows DWM as the backend: no system files are replaced, and every mode can be undone.");
		pg.Header("Options");
		pg.Add(ToggleRow("frames.transparency", s.DeskCompTransparency, delegate(bool v)
		{
			SettingsStore.Update(delegate(AppSettings x) { x.DeskCompTransparency = v; });
			ReskinShell();
			RefreshCompStatus();
		}, "Translucent glass tints on the launcher's own surfaces."));
		pg.Add(ToggleRow("frames.shadows", s.DeskCompShadows, delegate(bool v)
		{
			SettingsStore.Update(delegate(AppSettings x) { x.DeskCompShadows = v; });
			Win81Window.RefreshAllShadows();
			RefreshCompStatus();
		}, "Drop shadow on the launcher's own windows (through the DWM frame)."));
		pg.Add(ToggleRow("frames.optimize", s.DeskCompOptimizePerf, delegate(bool v)
		{
			SettingsStore.Update(delegate(AppSettings x) { x.DeskCompOptimizePerf = v; });
			if (v)
			{
				Motion.Mode = MotionMode.Reduced;
			}
			RefreshCompStatus();
		}, "Reduce animations and heavy effects."));
		pg.Add(SliderRow("frames.blur", 0.0, 100.0, s.DeskCompBlur, delegate(int v)
		{
			// one durable write and one reskin 250 ms after the last change, not one per tick of a drag
			_blurDebounce ??= NewBlurDebounce();
			_blurDebounce.Tag = v;
			_blurDebounce.Stop();
			_blurDebounce.Start();
		}, (int v) => v + "%", "Clear", "Tinted", "The opacity of the shell's tint (the blur intensity of glass surfaces)."));
		pg.Add(ToggleRow("frames.caption", s.Win81LightCaption, delegate(bool v)
		{
			SettingsStore.Update(delegate(AppSettings x) { x.Win81LightCaption = v; });
			Win81Window.RefreshAllAccents();
		}, "Owned 8.1 windows use the authentic white caption (dark glyphs, red-at-rest close), the out-of-box 8.1 default. Off: an accent-coloured caption. Only for the flat 8.1 frame style."));
		pg.Header("More");
		pg.Add(DrillRow("launcher.diagnostics", null, delegate { Navigate("launcher/diagnostics", null, push: true); }, "Composition status and frame timing"));
		return pg.Done();
	}

	private DispatcherTimer NewBlurDebounce()
	{
		DispatcherTimer t = new DispatcherTimer(DispatcherPriority.Background, base.Dispatcher) { Interval = TimeSpan.FromMilliseconds(250.0) };
		t.Tick += delegate
		{
			t.Stop();
			if (t.Tag is int v)
			{
				SettingsStore.Update(delegate(AppSettings x) { x.DeskCompBlur = v; });
				ReskinShell();
			}
		};
		return t;
	}

	private void RebuildCompTiles()
	{
		if (_compTiles == null)
		{
			return;
		}
		_compTiles.Children.Clear();
		char[] glyphs = new char[4] { '', '', '', '' };
		int gi = 0;
		foreach ((string label, string mid) in CompositionProfiles.MenuModes)
		{
			bool sel = string.Equals(mid, _compSel, StringComparison.OrdinalIgnoreCase);
			string id = mid;
			_compTiles.Children.Add(ModeTile(label, glyphs[gi % 4], sel, delegate
			{
				_compSel = id;
				RebuildCompTiles();
			}));
			gi++;
		}
	}

	// The caption strip of the look and mode tiles: the accent tone, darkened until the white caption reads at 4.5:1.
	internal static Color TileStripColor() => HubPalette.UntilContrast(SettingsPane.AccentToneColor(0.5), Colors.White, 4.5, darker: true);

	// The short caption of a mode or profile tile ("Windows 7 Aero (Authentic)" -> "7 Aero"); the full name is the tile's
	// tooltip and accessible name.
	internal static string ShortTileName(string label)
	{
		switch (label)
		{
		case "Windows Native":
			return "Native";
		case "Windows 7 Aero (Authentic)":
			return "7 Aero";
		case "Windows 8.1 (Authentic)":
			return "8.1";
		case "Alchemy Enhanced":
			return "Alchemy";
		case "Windows 8.1 Enhanced":
			return "8.1 Enhanced";
		case "Windows 7 Enhanced":
			return "7 Enhanced";
		case "Windows Native / Recovery":
			return "Native / recovery";
		}
		string s = label.Replace(" (Authentic)", "", StringComparison.Ordinal);
		return s.StartsWith("Windows ", StringComparison.Ordinal) ? s.Substring(8) : s;
	}

	// A 136x64 mode tile (composition modes, experience profiles): accent tone fill, glyph and a short caption.
	private Button ModeTile(string label, char glyph, bool selected, Action onClick)
	{
		Grid g = new Grid
		{
			Width = 132.0,
			Height = 60.0,
			// the selected tile is the lighter one (with the frame); its name is on the 4.5:1 strip below the glyph
			Background = new SolidColorBrush(SettingsPane.AccentToneColor(selected ? 0.46 : 0.32))
		};
		g.Children.Add(new TextBlock
		{
			Text = glyph.ToString(),
			FontFamily = Glyphs,
			FontSize = 20.0,
			Foreground = Brushes.White,
			HorizontalAlignment = HorizontalAlignment.Center,
			VerticalAlignment = VerticalAlignment.Center,
			Margin = new Thickness(0.0, 0.0, 0.0, 16.0)
		});
		g.Children.Add(new Border
		{
			Height = 20.0,
			VerticalAlignment = VerticalAlignment.Bottom,
			Background = new SolidColorBrush(TileStripColor()),
			Child = new TextBlock
			{
				Text = ShortTileName(label),
				Foreground = Brushes.White,
				FontFamily = SegoeUi,
				FontSize = 11.0,
				TextTrimming = TextTrimming.CharacterEllipsis,
				VerticalAlignment = VerticalAlignment.Center,
				HorizontalAlignment = HorizontalAlignment.Center
			}
		});
		Border frame = new Border
		{
			Child = g,
			BorderThickness = new Thickness(2.0),
			BorderBrush = selected ? (DarkRest.Visuals ? DarkRestPalette.Ink : _pal.Ink) : Brushes.Transparent
		};
		Button b = new Button
		{
			Template = _pal.PlainTemplate,
			Style = BareButton,
			Content = frame,
			Width = 136.0,
			Height = 64.0,
			Margin = new Thickness(0.0, 0.0, 8.0, 8.0),
			Cursor = Cursors.Hand,
			ToolTip = label
		};
		b.SetResourceReference(FrameworkElement.FocusVisualStyleProperty, "Metro81.Focus");
		AutomationProperties.SetName(b, label + (selected ? " (selected)" : ""));
		b.Click += delegate { onClick(); };
		return b;
	}

	private void AfterCompChange()
	{
		RebuildCompTiles();
		RefreshCompStatus();
	}

	private void StartCompStatusTimer()
	{
		try
		{
			_compStatusTimer?.Stop();
			DispatcherTimer timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.0) };
			_compStatusTimer = timer;
			timer.Tick += delegate
			{
				if (_compStatus == null || !_compStatus.IsVisible)
				{
					timer.Stop();   // ticks only while the status is on screen; IsVisibleChanged starts it again
					return;
				}
				RefreshCompStatus();
			};
			StackPanel? status = _compStatus;
			if (status != null)
			{
				status.IsVisibleChanged -= OnCompStatusVisibleChanged;
				status.IsVisibleChanged += OnCompStatusVisibleChanged;
			}
			if (status != null && status.IsVisible)
			{
				timer.Start();
			}
		}
		catch
		{
		}
	}

	// The status ticks only while it is shown: a page switched away from or a closed window stops its timer.
	private void OnCompStatusVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
	{
		if (!ReferenceEquals(sender, _compStatus))
		{
			((UIElement)sender).IsVisibleChanged -= OnCompStatusVisibleChanged;
			return;
		}
		DispatcherTimer? timer = _compStatusTimer;
		if (timer == null)
		{
			return;
		}
		if ((bool)e.NewValue)
		{
			timer.Start();
		}
		else
		{
			timer.Stop();
		}
	}

	private void RefreshCompStatus()
	{
		if (_compStatus == null)
		{
			return;
		}
		try
		{
			CompositionStatus st = DesktopComposition.DescribeStatus();
			string unsupported = st.Unsupported == null ? "" : string.Join("\u001f", st.Unsupported);
			string signature = string.Join("\u001e", new[]
			{
				st.ModeName ?? "", st.Transparency.ToString(), st.Shadows.ToString(), st.Animations.ToString(), st.Backend ?? "",
				st.DwmBlurGlassPolicy ?? "", st.DwmBlurGlassState ?? "", st.Status ?? "", st.RestoreAvailable.ToString(), unsupported
			});
			if (string.Equals(_compStatusSignature, signature, StringComparison.Ordinal))
			{
				return;
			}
			_compStatusSignature = signature;
			_compStatus.Children.Clear();
			void Line(string label, string? value)
			{
				Grid g = new Grid { Margin = new Thickness(0.0, 2.0, 0.0, 2.0) };
				g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150.0) });
				g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.0, GridUnitType.Star) });
				g.Children.Add(new TextBlock { Text = label, Foreground = _pal.Ink2, FontFamily = SegoeUi, FontSize = 12.0 });
				TextBlock v = new TextBlock { Text = value ?? "", Foreground = _pal.Ink, FontFamily = SegoeUiSemibold, FontSize = 12.0, TextWrapping = TextWrapping.Wrap };
				Grid.SetColumn(v, 1);
				g.Children.Add(v);
				_compStatus.Children.Add(g);
			}
			Line("Composition mode", st.ModeName);
			Line("Transparency", st.Transparency ? "Enabled" : "Off");
			Line("Shadows", st.Shadows ? "Enabled" : "Off");
			Line("Animations", st.Animations ? "Enabled" : "Off");
			Line("Backend", st.Backend);
			Line("Status", st.Status);
			Line("Restore available", st.RestoreAvailable ? "Yes" : "No");
			if (st.Unsupported != null && st.Unsupported.Count > 0)
			{
				_compStatus.Children.Add(new TextBlock
				{
					Text = "Not reproducible on other apps' windows: " + string.Join("; ", st.Unsupported),
					Foreground = _pal.Ink2,
					FontFamily = SegoeUi,
					FontSize = 12.0,
					TextWrapping = TextWrapping.Wrap,
					Margin = new Thickness(0.0, 6.0, 0.0, 0.0)
				});
			}
		}
		catch
		{
		}
	}
}
