#nullable enable

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace Win81Layer;

// --seasonaltest, Dark rest: the surfaces and states a review found hidden or too bright on black. The volume flyout's
// device rows and an app card with a dark icon, the
// switcher's selection ring, the Share and Devices panes, a search result card, the Settings pane's power flyout, the
// Start tile app bar with a selected tile, menu toggles in every row state, the keyboard row of an open drop-down list
// and the search scope drop-down. Then the startup upgrade with a wallpaper the user chose, dark coloured glyph icons,
// the toned-down tile family, the tile names with and without dimming, the picker preview (smooth, built off the UI
// thread), the Start picture (a locked file tried again, a late decode dropped, the screen scale) and the Search pane's
// scrollbar after leaving Dark rest. Read-only like the rest of the harness; every file goes to the output folder.
internal static partial class SeasonalDiagnostics
{
    // A glyph icon on a transparent 32 x 32 square: a filled square with a hole (the shape of a monochrome logo).
    private static BitmapSource GlyphIcon(Color c)
    {
        int size = 32;
        byte[] px = new byte[size * size * 4];
        for (int y = 6; y < 26; y++)
        {
            for (int x = 6; x < 26; x++)
            {
                if (x >= 13 && x < 19 && y >= 13 && y < 19)
                {
                    continue;
                }
                int i = (y * size + x) * 4;
                px[i] = c.B;
                px[i + 1] = c.G;
                px[i + 2] = c.R;
                px[i + 3] = 255;
            }
        }
        BitmapSource b = BitmapSource.Create(size, size, 96, 96, PixelFormats.Bgra32, null, px, size * 4);
        b.Freeze();
        return b;
    }

    // The picture of the fixture folder (the synthetic one, or the local picture given with --restpicture).
    private static string? FixturePicture()
    {
        return Directory.Exists(_fixturePicture) ? Directory.GetFiles(_fixturePicture, "rest.*").FirstOrDefault() : null;
    }

    private static Color Px(string path, int x, int y)
    {
        BitmapSource b = LoadPng(path);
        return PixelAt(b, Math.Clamp(x, 0, b.PixelWidth - 1), Math.Clamp(y, 0, b.PixelHeight - 1));
    }

    private static bool Near(Color a, Color b, int tol)
    {
        return Math.Abs(a.R - b.R) <= tol && Math.Abs(a.G - b.G) <= tol && Math.Abs(a.B - b.B) <= tol;
    }

    private static Int32Rect R(Rect r, double dx, double dy, double w, double h)
    {
        return new Int32Rect((int)Math.Round(r.X + dx), (int)Math.Round(r.Y + dy), (int)Math.Round(w), (int)Math.Round(h));
    }

    // ===================================================================== pure checks

    private static void RestStatesPureChecks(string outDir)
    {
        SeasonPreset rest = SeasonalTheme.Find(SeasonalTheme.Rest)!;
        // The startup upgrade of an earlier Dark rest look, after the user chose a wallpaper by hand while it was on: the
        // Start values move to the new look, the chosen wallpaper and the restore point's original wallpaper stay as they
        // are (it only forgets the applied file, which no display shows), nothing is left for the next start to redraw,
        // the earlier picture is cleaned up, and Normal afterwards leaves the chosen wallpaper too.
        bool RedrawDue(FakeSeasonalWorld w)
        {
            return SeasonalTheme.NeedsStartupWork(w.Peek(), w.PeekState(), unreadable: false, degraded: false, w.FileExists,
                (_, applied) => !string.Equals(Path.GetFileName(applied), w.ExpectedWallpaperName(SeasonalArt.RestArt), StringComparison.OrdinalIgnoreCase));
        }
        {
            const string chosen = "C:\\Pictures\\chosen.jpg";
            FakeSeasonalWorld w = LegacyRest(windowsDark: true, out _);
            foreach (MonitorWallpaper m in w.Monitors)
            {
                m.Path = chosen;
            }
            string origBefore = w.OriginalJson(blankApplied: true);
            SeasonalResult r = SeasonalTheme.CreateEngine(w).RecoverAndMigrate();
            AppSettings s = w.Peek();
            bool migrated = r.Success && s.StartPattern == rest.Pattern && s.StartBgColor == rest.Base && s.StartAccentColor == rest.Accent;
            bool kept = w.Monitors.All(m => m.Path == chosen) && w.OriginalJson(blankApplied: true) == origBefore && w.PeekState().Original?.Wallpaper?.AppliedPath == "";
            bool settled = !RedrawDue(w) && !w.Files.Contains(LegacyRestFile);
            SeasonalResult off = SeasonalTheme.CreateEngine(w).Apply(SeasonalTheme.Normal);
            bool keptAfterNormal = off.Success && w.Monitors.All(m => m.Path == chosen);
            Check("rest-upgrade-hand-wallpaper-kept", migrated && kept && settled && keptAfterNormal,
                $"migrated={migrated} wallpaperKept={kept} settled={settled} keptAfterNormal={keptAfterNormal} msg={r.Message} world={w.World()}");
        }
        // The same upgrade with the user's own picture on one display only: the new picture goes on the display that still
        // shows the earlier one, the other keeps the user's picture (also when the upgrade is interrupted after any write
        // and finished on the next start), and Normal puts back the original on the first display only.
        {
            const string chosen = "C:\\Pictures\\chosen.jpg";
            string expect = FakeSeasonalWorld.Folder + SeasonalArt.WallpaperFileName(SeasonalArt.RestArt, 1920, 1080);
            FakeSeasonalWorld probe = LegacyRest(windowsDark: true, out _);
            probe.Monitors[1].Path = chosen;
            int positionBefore = probe.Position;
            SeasonalResult r = SeasonalTheme.CreateEngine(probe).RecoverAndMigrate();
            int total = probe.Writes;
            SeasonalStateFile st = probe.PeekState();
            bool upgraded = r.Success && probe.Monitors[0].Path == expect && probe.Monitors[1].Path == chosen && probe.Position == positionBefore
                && st.Original?.Wallpaper?.AppliedPath == expect && st.Original.Wallpaper.Monitors.Any(m => m.MonitorId == "MON2" && m.Path == chosen) && !RedrawDue(probe);
            SeasonalResult off = SeasonalTheme.CreateEngine(probe).Apply(SeasonalTheme.Normal);
            bool normal = off.Success && probe.Monitors[0].Path == "C:\\Pictures\\desk1.jpg" && probe.Monitors[1].Path == chosen;
            List<string> failures = new List<string>();
            for (int n = 1; n <= total; n++)
            {
                FakeSeasonalWorld w = LegacyRest(windowsDark: true, out _);
                w.Monitors[1].Path = chosen;
                w.CrashAfter = n;
                try
                {
                    SeasonalTheme.CreateEngine(w).RecoverAndMigrate();
                }
                catch (SimulatedCrash)
                {
                }
                w.CrashAfter = -1;
                SeasonalTheme.CreateEngine(w).Recover();
                bool keptMine = w.Monitors[1].Path == chosen;
                SeasonalTheme.CreateEngine(w).Apply(SeasonalTheme.Normal);
                if (!keptMine || w.Monitors[0].Path != "C:\\Pictures\\desk1.jpg" || w.Monitors[1].Path != chosen || w.LoadState().Phase != SeasonalTheme.PhaseIdle)
                {
                    failures.Add($"n={n} keptMine={keptMine} world={w.World()}");
                }
            }
            Check("rest-upgrade-mixed-displays", upgraded && normal && failures.Count == 0 && total >= 3,
                $"upgraded={upgraded} normal={normal} crashPoints={total} msg={r.Message} world={probe.World()}" + (failures.Count > 0 ? " ; " + string.Join(" ; ", failures) : ""));
        }
        // A dark coloured glyph on transparency (a navy logo, 1.5:1 on black) is lifted on the black surfaces, its hue
        // kept; a bright colourful icon is left exactly as it is.
        {
            Color navy = Color.FromRgb(0x1A, 0x2A, 0x5A);
            BitmapSource glyph = GlyphIcon(navy);
            ImageSource shown = DarkRestIcons.Visible(glyph);
            string detail = "not lifted";
            bool lifted = false;
            if (shown is BitmapSource sb && !ReferenceEquals(sb, glyph))
            {
                Color c = PixelAt(sb, 8, 8);
                double hueIn = Hsl(navy.R, navy.G, navy.B).H;
                double hueOut = Hsl(c.R, c.G, c.B).H;
                double hueDiff = Math.Abs(hueIn - hueOut);
                hueDiff = Math.Min(hueDiff, 360.0 - hueDiff);
                byte[] p = Pixels(sb);
                bool holeClear = p[(16 * 32 + 16) * 4 + 3] == 0;
                lifted = Contrast(c, Colors.Black) >= 3.0 && hueDiff <= 12.0 && holeClear;
                detail = $"{Hex(navy)} -> {Hex(c)} {Contrast(c, Colors.Black):F2}:1 on black, hue {hueIn:F0} -> {hueOut:F0}, hole clear={holeClear}";
            }
            BitmapSource bright = GlyphIcon(Color.FromRgb(0x2D, 0x7F, 0xF9));
            bool brightKept = ReferenceEquals(DarkRestIcons.Visible(bright), bright);
            Check("rest-icons-dark-glyph", lifted && brightKept, detail + $" brightKept={brightKept}");
        }
        // The toned-down Start tiles: one family with the muted Action Center tiles (saturation 0.40 and chroma 55 at
        // most) and the soft ink of the names at 4.5:1 or more on each.
        {
            List<string> bad = new List<string>();
            List<string> got = new List<string>();
            foreach (string hex in new string[12] { "#FF00A300", "#FF2672EC", "#FF2E8DEF", "#FFDC572E", "#FF8C0095", "#FFAC193D", "#FF00A0B1", "#FF008299", "#FF4E4E4E", "#FFFFC40D", "#FFE3A21A", "#FF1E7145" })
            {
                Color m = DarkRestPalette.MuteTile(SeasonalArt.Hex(hex));
                double sat = Hsl(m.R, m.G, m.B).S;
                int chroma = Math.Max(m.R, Math.Max(m.G, m.B)) - Math.Min(m.R, Math.Min(m.G, m.B));
                double ink = Contrast(DarkRestPalette.InkColor, m);
                got.Add($"{hex}->{Hex(m)} s{sat:F2} c{chroma} ink{ink:F2}");
                if (sat > 0.40 + 1e-9 || chroma > 55 || ink < 4.5)
                {
                    bad.Add(got[^1]);
                }
            }
            Check("rest-tiles-muted-family", bad.Count == 0, bad.Count == 0 ? string.Join(" ", got) : string.Join(" ; ", bad));
        }
        // The picker preview of Dark rest: averaged over a 40 pixel window, so the fine grid leaves no plaid (neighbouring
        // preview pixels differ by at most one level away from the marker), for the drawn scene and for the picture.
        {
            List<string> got = new List<string>();
            bool smooth = true;
            foreach ((string name, string? picture) in new (string, string?)[2] { ("drawn", null), ("picture", FixturePicture()) })
            {
                if (name == "picture" && picture == null)
                {
                    continue;
                }
                BitmapSource prev = SeasonalArt.RestPreview(picture);
                BitmapSource blocks = SeasonalArt.BoxDownscale(picture != null ? SeasonalArt.LoadPictureFilled(picture, 1920, 1080) : SeasonalArt.RenderRestScene(1920, 1080, 960.0), 8);
                int Step(BitmapSource b)
                {
                    byte[] p = Pixels(b);
                    int w = b.PixelWidth;
                    int worst = 0;
                    for (int y = 4; y < 80; y++)
                    {
                        for (int x = 4; x < 180; x++)
                        {
                            int i = (y * w + x) * 4;
                            for (int c = 0; c < 3; c++)
                            {
                                worst = Math.Max(worst, Math.Abs(p[i + c] - p[i + 4 + c]));
                                worst = Math.Max(worst, Math.Abs(p[i + c] - p[i + w * 4 + c]));
                            }
                        }
                    }
                    return worst;
                }
                int now = Step(prev);
                int before = Step(blocks);
                got.Add($"{name}: largest step {now} (8 x 8 blocks alone {before}), {prev.PixelWidth}x{prev.PixelHeight}");
                smooth &= now <= 1 && prev.PixelWidth == 240 && prev.PixelHeight == 135;
            }
            Check("rest-preview-smooth", smooth && got.Count >= 1, string.Join(" ; ", got));
        }
        // A picture that cannot be decoded: the preview shows the drawn scene (the desktop and Start fall back to it too),
        // not a black placeholder.
        {
            string broken = Path.Combine(outDir, "fixture-broken");
            Directory.CreateDirectory(broken);
            string file = Path.Combine(broken, "rest.jpg");
            File.WriteAllText(file, "not a picture");
            string detail;
            bool ok;
            try
            {
                BitmapSource prev = SeasonalArt.RestPreview(file);
                BitmapSource drawn = SeasonalArt.RestPreview(null);
                ok = prev.PixelWidth == drawn.PixelWidth && prev.PixelHeight == drawn.PixelHeight && Pixels(prev).SequenceEqual(Pixels(drawn));
                detail = $"{prev.PixelWidth}x{prev.PixelHeight} same as the drawn scene={ok}";
            }
            catch (Exception ex)
            {
                ok = false;
                detail = "threw " + ex.GetType().Name + ": " + ex.Message;
            }
            Check("rest-preview-undecodable", ok, detail);
        }
        // The 8.1 and Office 2013 tile art on the dimmed tiles: drawn at the glyph opacity over its toned-down plate, the
        // art is no vivid square (chroma 55 at most, like the toned-down tiles), its glyph keeps 4.5:1 or more on the
        // plate, nothing in it is brighter than the soft ink, and with the flag off the art is passed through unchanged.
        // The tiles as the dimmed board draws them go to rest-override-tiles.png (the full-colour art in the top row).
        {
            List<string> got = new List<string>();
            List<string> bad = new List<string>();
            string[] ids = new string[6] { "Microsoft.WindowsCalculator_8wekyb3d8bbwe!App", "Microsoft.WindowsAlarms_8wekyb3d8bbwe!App", "microsoft.windowscommunicationsapps_8wekyb3d8bbwe!microsoft.windowslive.mail", "Microsoft.WindowsStore_8wekyb3d8bbwe!App", "windows.immersivecontrolpanel_cw5n1h2txyewy!microsoft.windows.immersivecontrolpanel", "Claude_pzs8sxrjxfjjc!Claude" };
            const int cell = 150;
            DrawingVisual board = new DrawingVisual();
            RestOverrideArtConverter conv = new RestOverrideArtConverter();
            bool passThrough = true;
            int n = 0;
            using (DrawingContext dc = board.RenderOpen())
            {
                dc.DrawRectangle(Brushes.Black, null, new Rect(0, 0, ids.Length * (cell + 10), 2 * (cell + 10)));
                for (int i = 0; i < ids.Length; i++)
                {
                    AppEntry entry = new AppEntry { Name = "QA", LaunchPath = "shell:AppsFolder\\" + ids[i], TileBrush = Brushes.Gray, AppId = ids[i] };
                    ImageSource? art = AppIconOverrides.TryGet(entry);
                    if (art == null || AppIconOverrides.BrandBrush(entry) is not SolidColorBrush brand)
                    {
                        continue;
                    }
                    n++;
                    passThrough &= ReferenceEquals(conv.Convert(new object[3] { art, brand, false }, typeof(ImageSource), null, System.Globalization.CultureInfo.InvariantCulture), art);
                    ImageSource muted = (ImageSource)conv.Convert(new object[3] { art, brand, true }, typeof(ImageSource), null, System.Globalization.CultureInfo.InvariantCulture)!;
                    Color plate = DarkRestPalette.MuteTile(brand.Color);
                    double x = i * (cell + 10);
                    Rect face = new Rect(x + 21, cell + 10 + 21, 108, 108);
                    dc.DrawRectangle(brand, null, new Rect(x, 0, cell, cell));
                    dc.DrawImage(art, new Rect(x + 21, 21, 108, 108));
                    dc.DrawRectangle(new SolidColorBrush(plate), null, new Rect(x, cell + 10, cell, cell));
                    dc.PushOpacity(DarkRestPalette.GlyphOpacity);
                    dc.DrawImage(muted, face);
                    dc.Pop();
                }
            }
            BitmapSource shot = Render(board, ids.Length * (cell + 10), 2 * (cell + 10));
            SavePng(shot, Path.Combine(outDir, "rest-override-tiles.png"));
            byte[] p = Pixels(shot);
            int stride = shot.PixelWidth * 4;
            for (int i = 0; i < n; i++)
            {
                int x0 = i * (cell + 10);
                int y0 = cell + 10;
                int maxChroma = 0;
                int maxLevel = 0;
                Color plateC = Color.FromRgb(p[(y0 + 4) * stride + (x0 + 4) * 4 + 2], p[(y0 + 4) * stride + (x0 + 4) * 4 + 1], p[(y0 + 4) * stride + (x0 + 4) * 4]);
                Color brightest = plateC;
                for (int y = y0; y < y0 + cell; y++)
                {
                    for (int x = x0; x < x0 + cell; x++)
                    {
                        int k = y * stride + x * 4;
                        int hi = Math.Max(p[k], Math.Max(p[k + 1], p[k + 2]));
                        int lo = Math.Min(p[k], Math.Min(p[k + 1], p[k + 2]));
                        maxChroma = Math.Max(maxChroma, hi - lo);
                        if (hi > maxLevel)
                        {
                            maxLevel = hi;
                            brightest = Color.FromRgb(p[k + 2], p[k + 1], p[k]);
                        }
                    }
                }
                double glyph = Contrast(brightest, plateC);
                got.Add($"{i}: plate {Hex(plateC)} glyph {Hex(brightest)} {glyph:F2}:1 chroma {maxChroma}");
                if (maxChroma > 55 || glyph < 4.5 || maxLevel > 0xDC)
                {
                    bad.Add(got[^1]);
                }
            }
            Check("rest-override-art-muted", n >= 4 && passThrough && bad.Count == 0, $"art={n} passThrough={passThrough} " + (bad.Count == 0 ? string.Join(" ; ", got) : string.Join(" ; ", bad)));
        }
    }

    // ===================================================================== renders and checks while Dark rest is on

    // A keyboard-highlighted drop-down row (IsHighlighted is only settable by a derived item).
    private sealed class QaHighlightedComboItem : ComboBoxItem
    {
        public QaHighlightedComboItem()
        {
            IsHighlighted = true;
        }
    }

    private static Rect _restVolumeDefault;

    private static Rect _restVolumeOther;

    private static Rect _restVolumeCard;

    // The compact flyouts' rendered geometry the Dark rest checks need: the selected calendar day and the app logos' column.
    private static Rect _restClockSelectedDay;

    private static Rect _restClockSelectedDayText;

    private static Int32Rect _restVolumeLogos = new Int32Rect(12, 0, 32, 1);

    private static Rect _restSwitcherSelected;

    // The renders (made before the glare census, which includes them).
    private static void RestStatesRenders(string outDir, StartScreen start, RestFixtures f)
    {
        // Share and Devices panes
        CharmListPane share = new CharmListPane("Share", "Desktop", () => new CharmListPane.Row[3]
        {
            new CharmListPane.Row(59592, "Copy folder path", "C:\\Users\\Public\\Documents", delegate { }),
            new CharmListPane.Row(59592, "Copy window title", "Dark rest", delegate { }),
            new CharmListPane.Row(59592, "Copy app name", "File Explorer", delegate { }, Enabled: false)
        }) { Left = -4000.0, Top = -4000.0 };
        CharmListPane devices = new CharmListPane("Devices", "Desktop", () => new CharmListPane.Row[4]
        {
            new CharmListPane.Row(59240, "Play", "Play on TV", delegate { }),
            new CharmListPane.Row(59209, "Print", "Print", delegate { }),
            new CharmListPane.Row(59380, "Project", "Project to a second screen", delegate { }),
            new CharmListPane.Row(59152, "Add a device", "Add a device", delegate { })
        }) { Left = -4000.0, Top = -4000.0 };
        try
        {
            share.QaRender(Path.Combine(outDir, "share-rest.png"), 520.0);
            devices.QaRender(Path.Combine(outDir, "devices-rest.png"), 520.0);
        }
        finally
        {
            share.Close();
            devices.Close();
        }
        // a search result card (the calculator), the scope drop-down and the Settings pane's power flyout
        f.Search.QaRender(Path.Combine(outDir, "search-rest-calc.png"), "12*7", 920.0);
        f.Search.QaRenderScopeMenu(Path.Combine(outDir, "searchscope-rest.png"));
        f.Settings.QaRenderPowerFlyout(Path.Combine(outDir, "settings-power-rest.png"));
        // the Start tile app bar with the first tile selected
        start.QaRenderTileAppBar(Path.Combine(outDir, "start-rest-appbar.png"), 1920.0, 1080.0);

        // menu toggles: on and off, in normal rows and on the keyboard row
        {
            ContextMenu ctx = new ContextMenu { Style = (Style)Application.Current.Resources["Win81ContextMenu"] };
            TaskbarContextMenu.ApplyTheme(ctx);
            ctx.Items.Add(TaskbarContextMenu.Check("Lock the taskbar", true, delegate { }));
            ctx.Items.Add(TaskbarContextMenu.Check("Automatically hide", false, delegate { }));
            ctx.Items.Add(new QaHighlightedItem { Header = "Show Task view", Style = TaskbarContextMenu.ItemStyle, IsCheckable = true, IsChecked = true, Tag = "toggle" });
            ctx.Items.Add(new QaHighlightedItem { Header = "Show Search", Style = TaskbarContextMenu.ItemStyle, IsCheckable = true, IsChecked = false, Tag = "toggle" });
            ctx.Items.Add(TaskbarContextMenu.Sep());
            ctx.Items.Add(new MenuItem { Header = "Task Manager", InputGestureText = "Ctrl+Shift+Esc", Style = TaskbarContextMenu.ItemStyle });
            TaskbarContextMenu.PrepareForInstantOpen(ctx);
            int w = (int)Math.Ceiling(ctx.ActualWidth);
            int h = (int)Math.Ceiling(ctx.ActualHeight);
            RenderTargetBitmap menu = new RenderTargetBitmap(Math.Max(1, w), Math.Max(1, h), 96, 96, PixelFormats.Pbgra32);
            menu.Render(ctx);
            DrawingVisual dv = new DrawingVisual();
            using (DrawingContext dc = dv.RenderOpen())
            {
                dc.DrawRectangle(DarkRestPalette.Canvas, null, new Rect(0, 0, w + 40, h + 40));
                dc.DrawImage(menu, new Rect(20, 20, w, h));
            }
            SavePng(Render(dv, w + 40, h + 40), Path.Combine(outDir, "contextmenu-toggles-rest.png"));
        }
        // an open drop-down list: a plain row, the keyboard row and the selected row
        {
            Style item = (Style)Application.Current.FindResource("Metro81.ComboBoxItem");
            StackPanel list = new StackPanel { Width = 240 };
            list.SetResourceReference(Panel.BackgroundProperty, "Metro81.Surface");
            list.Children.Add(new ComboBoxItem { Content = "Bottom", Style = item });
            list.Children.Add(new QaHighlightedComboItem { Content = "Top (keyboard)", Style = item });
            list.Children.Add(new ComboBoxItem { Content = "Left (selected)", Style = item, IsSelected = true });
            Border host = new Border { Background = DarkRestPalette.Canvas, Padding = new Thickness(10), Child = list };
            host.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            host.Arrange(new Rect(host.DesiredSize));
            host.UpdateLayout();
            RenderTargetBitmap rtb = new RenderTargetBitmap((int)Math.Ceiling(host.ActualWidth), (int)Math.Ceiling(host.ActualHeight), 96, 96, PixelFormats.Pbgra32);
            rtb.Render(host);
            SavePng(rtb, Path.Combine(outDir, "combo-rest.png"));
        }
    }

    // Called from RestFlyouts: the volume flyout with both sections open (the input rows and the app rows), a second app with
    // a black logo.
    private static void RestVolumeRender(string path, TaskbarWindow taskbar)
    {
        taskbar.QaRenderSoundFlyout(path, expandInputs: true, expandApps: true, qaSessionIcon: GlyphIcon(Colors.Black), probes: false);
        _restVolumeDefault = taskbar.QaVolumeRowBounds(capture: false, 0);
        _restVolumeOther = taskbar.QaVolumeRowBounds(capture: true, 1);
        _restVolumeCard = taskbar.QaVolumeSessionBounds(1);
        Rect first = taskbar.QaVolumeSessionBounds(0);
        if (!first.IsEmpty && !_restVolumeCard.IsEmpty)
        {
            // the app rows' icon column (a lifted dark logo is light by design)
            _restVolumeLogos = new Int32Rect((int)first.Left + 11, (int)first.Top, 32, (int)Math.Ceiling(_restVolumeCard.Bottom - first.Top));
        }
    }

    // Called from RestPanes: the switcher with an entry selected by the keyboard.
    private static void RestSwitcherSelectedRender(string path, AppSwitcher sw)
    {
        sw.QaRender(path, 1);
        if (sw.QaSelectedBounds.IsEmpty)
        {
            sw.QaRender(path, 0);
        }
        _restSwitcherSelected = sw.QaSelectedBounds;
    }

    private static void RestStatesChecks(string outDir, StartScreen start, AppSettings real, RestFixtures f)
    {
        string P(string name) => Path.Combine(outDir, name);
        // the volume flyout (both sections open): the device rows are drawn on the near-black body, the tick only on the
        // default one (3:1 or more; nothing in the other row's tick column), each name 4.5:1 on the body, and the dark logo
        // of an app row lifted (3:1 or more against the body)
        {
            string vol = P("flyout-volume-rest.png");
            List<string> bad = new List<string>();
            Rect d = _restVolumeDefault;
            Rect o = _restVolumeOther;
            Rect c = _restVolumeCard;
            if (d.IsEmpty || o.IsEmpty || c.IsEmpty)
            {
                bad.Add($"rows not laid out: default={d} other={o} card={c}");
            }
            else
            {
                // a row: 11 DIP padding, [32 icon | 8 | name | 24 tick]; the body shows in the left padding
                Int32Rect Pad(Rect r) => R(r, 1, 4, 8, r.Height - 8);
                Int32Rect TickColumn(Rect r) => R(r, r.Width - 11 - 24 + 4, 8, 16, r.Height - 16);
                Color body = Px(vol, (int)(o.Left + 4), (int)(o.Top + o.Height / 2));
                double dTick = ContrastIn(vol, TickColumn(d), Pad(d));
                double oTick = ContrastIn(vol, TickColumn(o), Pad(o));
                double dName = ContrastIn(vol, R(d, 51, 8, 140, 16), Pad(d));
                double oName = ContrastIn(vol, R(o, 51, 8, 140, 16), Pad(o));
                (double ir, double ig, double ib) = RegionAverage(vol, (int)(c.Left + 21), (int)(c.Top + 18), 3, 3);
                Color icon = Color.FromRgb((byte)ir, (byte)ig, (byte)ib);
                Color plate = Px(vol, (int)(c.Left + 4), (int)(c.Top + 4));
                if (body.R > 0x14 || body.G > 0x14 || body.B > 0x14) bad.Add("row body " + Hex(body));
                if (dTick < 3.0) bad.Add($"default tick {dTick:F2}");
                if (oTick > 1.3) bad.Add($"tick on the other row {oTick:F2}");
                if (dName < 4.5) bad.Add($"default name {dName:F2}");
                if (oName < 4.5) bad.Add($"other name {oName:F2}");
                if (Contrast(icon, plate) < 3.0) bad.Add($"app logo {Hex(icon)} on {Hex(plate)} {Contrast(icon, plate):F2}");
                Metrics["RestVolumeRows"] = $"body {Hex(body)}; default tick {dTick:F2} name {dName:F2}; other tick {oTick:F2} name {oName:F2}; logo {Hex(icon)} on {Hex(plate)} {Contrast(icon, plate):F2}";
            }
            Check("rest-volume-rows", bad.Count == 0, bad.Count == 0 ? Metrics["RestVolumeRows"]?.ToString() ?? "" : string.Join(" ; ", bad));
        }
        // the switcher's keyboard selection: the ring 3:1 or more against the panel and against what it frames
        {
            Rect b = _restSwitcherSelected;
            string sw = P("switcher-rest-selected.png");
            if (b.IsEmpty || !File.Exists(sw))
            {
                Check("rest-switcher-selection", false, "no selection rendered");
            }
            else
            {
                Color ring = Px(sw, (int)(b.X + b.Width / 2), (int)b.Y);
                Color panel = Px(sw, (int)b.X - 4, (int)(b.Y + b.Height / 2));
                Color inner = Px(sw, (int)(b.X + b.Width / 2), (int)b.Y + 4);
                bool ok = Contrast(ring, panel) >= 3.0 && Contrast(ring, inner) >= 3.0;
                Check("rest-switcher-selection", ok, $"ring {Hex(ring)} vs panel {Hex(panel)} {Contrast(ring, panel):F2}, vs inside {Hex(inner)} {Contrast(ring, inner):F2}");
            }
        }
        // menu toggles: knob against its track, track against its row (the outline), On against Off, in the normal, hover
        // and keyboard rows; the shortcut text and the submenu arrow
        {
            List<string> bad = new List<string>();
            List<string> got = new List<string>();
            Color M(string key) => TaskbarContextMenu.QaMenuColor(key);
            Color on = (Application.Current.Resources["MenuAccent"] as SolidColorBrush)?.Color ?? Colors.Magenta;
            void Pair(string name, Color a, Color b, double min)
            {
                double ratio = Contrast(a, b);
                got.Add($"{name}={ratio:F2}");
                if (ratio < min)
                {
                    bad.Add($"{name} {Hex(a)} on {Hex(b)} = {ratio:F2} < {min}");
                }
            }
            Pair("knob/off track", M("M.Fg"), M("M.ToggleOff"), 3.0);
            Pair("knob/on track", M("M.Fg"), on, 3.0);
            Pair("on/off track", on, M("M.ToggleOff"), 3.0);
            Pair("track outline/menu", M("M.ToggleBorder"), M("M.Bg"), 3.0);
            Pair("track outline/hover row", M("M.ToggleBorder"), M("M.Hover"), 3.0);
            Pair("keyboard knob/off track", M("M.KbFg"), M("M.ToggleOff"), 3.0);
            Pair("keyboard knob/on track", M("M.KbFg"), on, 3.0);
            Pair("keyboard track outline/keyboard row", M("M.KbToggleBorder"), M("M.KbBg"), 3.0);
            Pair("shortcut/hover row", M("M.Gesture"), M("M.Hover"), 4.5);
            Pair("shortcut/menu", M("M.Gesture"), M("M.Bg"), 4.5);
            Pair("submenu arrow/menu", M("M.Arrow"), M("M.Bg"), 4.5);
            Pair("separator/menu", M("M.Sep"), M("M.Bg"), 1.6);
            // the rendered keyboard row with the On toggle: its outline visible on the row
            string menu = P("contextmenu-toggles-rest.png");
            if (File.Exists(menu))
            {
                BitmapSource mb = LoadPng(menu);
                // the third row (keyboard, On): the toggle sits in the right column, the track's top edge row
                (int tx, int ty) = FindTrackEdge(mb, M("M.KbToggleBorder"));
                got.Add($"keyboard track outline found at {tx},{ty}");
                if (tx < 0) bad.Add("keyboard toggle outline not found in the render");
            }
            Check("rest-menu-toggles", bad.Count == 0, bad.Count == 0 ? string.Join(" ", got) : string.Join(" ; ", bad));
        }
        // the open drop-down list: the keyboard row has an accent outline (3:1 on the list)
        {
            string combo = P("combo-rest.png");
            Color ring = Px(combo, 10, 10 + 32 + 16);
            Color list = Px(combo, 10 + 120, 10 + 4);
            Color plain = Px(combo, 10, 10 + 16);
            bool ok = Contrast(ring, list) >= 3.0 && Near(ring, DarkRestPalette.AccentLineColor, 3) && Near(plain, list, 2);
            Check("rest-combo-keyboard-row", ok, $"ring {Hex(ring)} on list {Hex(list)} {Contrast(ring, list):F2}, plain row edge {Hex(plain)}");
        }
        // the search scope drop-down: its edge 3:1 against the pane
        {
            string scope = P("searchscope-rest.png");
            Color edge = Px(scope, 16, 40);
            Color pane = Px(scope, 6, 40);
            Check("rest-search-scope-edge", Contrast(edge, pane) >= 3.0, $"edge {Hex(edge)} on pane {Hex(pane)} {Contrast(edge, pane):F2}");
        }
        // Start: the selected tile's check corner in the selection fill, the app bar's labels in the soft ink
        {
            string bar = P("start-rest-appbar.png");
            Color check = Px(bar, 80 + 150 - 22, 154 + 4);
            Color barFill = Px(bar, 1900, 1075);
            double label = ContrastIn(bar, new Int32Rect(0, 1040, 900, 30), new Int32Rect(1200, 1040, 200, 30));
            Color brightest = Brightest(bar, new Int32Rect(0, 990, 1920, 90));
            bool ok = Near(check, DarkRestPalette.AccentFillColor, 3) && Near(barFill, DarkRestPalette.PopupColor, 2) && label >= 4.5 && brightest.R < 0xF0;
            Check("rest-start-appbar", ok, $"check corner {Hex(check)} bar {Hex(barFill)} labels {label:F2} brightest {Hex(brightest)}");
        }
        // the zoomed-out group name over the picture: 4.5:1 like over the drawn scene
        if (File.Exists(P("start-rest-zoom-picture.png")))
        {
            double zoomName = ContrastIn(P("start-rest-zoom-picture.png"), new Int32Rect(370, 384, 80, 20), new Int32Rect(470, 384, 80, 20));
            Check("rest-start-zoom-picture", zoomName >= 4.5, $"group name over the picture {zoomName:F2}");
        }
        // Share, Devices, the result card and the power flyout: readable in the soft ink (no pure white, see the census)
        {
            List<string> got = new List<string>();
            List<string> bad = new List<string>();
            void Spot(string name, string file, Int32Rect ink, Int32Rect bg, double min)
            {
                double c = ContrastIn(P(file), ink, bg);
                got.Add($"{name}={c:F2}");
                if (c < min)
                {
                    bad.Add($"{name} {c:F2} < {min}");
                }
            }
            Spot("share title", "share-rest.png", new Int32Rect(24, 24, 110, 40), new Int32Rect(200, 24, 100, 40), 4.5);
            Spot("share row", "share-rest.png", new Int32Rect(68, 126, 110, 16), new Int32Rect(250, 126, 60, 16), 4.5);
            Spot("share row detail", "share-rest.png", new Int32Rect(68, 146, 140, 12), new Int32Rect(250, 146, 60, 12), 4.5);
            Spot("share unavailable row", "share-rest.png", new Int32Rect(68, 240, 100, 16), new Int32Rect(250, 240, 60, 16), 4.5);
            Spot("devices title", "devices-rest.png", new Int32Rect(24, 24, 140, 40), new Int32Rect(220, 24, 100, 40), 4.5);
            // the second row ("Shut down"): 32 px rows, text from 12 px inside the flyout
            Spot("power item", "settings-power-rest.png", new Int32Rect(30, 58, 60, 14), new Int32Rect(150, 58, 16, 14), 4.5);
            Check("rest-panes-soft-ink", bad.Count == 0, bad.Count == 0 ? string.Join(" ", got) : string.Join(" ; ", bad) + " | " + string.Join(" ", got));
        }
        // PC Settings: the outline of a secondary action button stays one crisp pixel (3:1 on the black page)
        {
            string pcs = P("pcsettings-rest-launcher.png");
            (int bx, int by) = _restSecondaryButton;
            double edge = by > 0 ? ContrastIn(pcs, new Int32Rect(bx, by, 60, 1), new Int32Rect(bx, by + 4, 60, 3)) : 0.0;
            Check("rest-pcsettings-button-outline", edge >= 3.0, $"bottom edge at {bx},{by}: {edge:F2}");
        }
        // the taskbar on the black bar: the top edge just above black, the foreground app's fill a step lighter, and the
        // pressed or open Start button a visible neutral fill
        {
            Color edge = (f.Taskbar.QaResource("TB.BarEdge") as SolidColorBrush)?.Color ?? Colors.Magenta;
            Color active = (f.Taskbar.QaResource("TB.BtnActive") as SolidColorBrush)?.Color ?? Colors.Magenta;
            Color startOpen = TaskbarWindow.StartSurfaceTone("windows81", "open");
            bool flat = !ShellSkin.GlassOn;
            bool ok = edge == Color.FromRgb(0x14, 0x14, 0x14) && (!flat || Contrast(active, Colors.Black) >= 1.8) && Contrast(startOpen, Colors.Black) >= 1.4 && startOpen.A == 255;
            Check("rest-taskbar-states", ok, $"edge {Hex(edge)} active {Hex(active)} (flat={flat}) start open {Hex(startOpen)}");
        }
        // the menu glyph squares are toned down with a soft-ink glyph
        {
            FrameworkElement g = TaskbarContextMenu.MenuGlyph(57846);
            Color fill = ((g as Border)?.Background as SolidColorBrush)?.Color ?? Colors.Magenta;
            Brush? glyph = ((g as Border)?.Child as TextBlock)?.Foreground;
            bool ok = Hsl(fill.R, fill.G, fill.B).S <= 0.40 && ReferenceEquals(glyph, DarkRestPalette.Ink) && Contrast(DarkRestPalette.InkColor, fill) >= 4.5;
            Check("rest-menu-glyph-tiles", ok, $"fill {Hex(fill)} s={Hsl(fill.R, fill.G, fill.B).S:F2} glyph ink={ReferenceEquals(glyph, DarkRestPalette.Ink)}");
        }
        RestPreviewAsyncCheck();
        RestStartPictureStateChecks(outDir, start, real);
        RestSearchScrollBarCheck(outDir, start, real);
    }

    // The bottom edge of a secondary action button ("Restore previous", Launcher > Profiles of the PC settings hub) in
    // pcsettings-rest-launcher.png (900 DIP wide, the content column centred). Stage 3 added two rows above it (Lighter
    // rendering, Action Center opens PC settings): 106 DIP lower.
    private static readonly (int X, int Y) _restSecondaryButton = (256, 978);

    // The brightest pixel of a region.
    private static Color Brightest(string path, Int32Rect r)
    {
        BitmapSource b = LoadPng(path);
        int x = Math.Clamp(r.X, 0, b.PixelWidth - 1);
        int y = Math.Clamp(r.Y, 0, b.PixelHeight - 1);
        byte[] p = Pixels(new CroppedBitmap(b, new Int32Rect(x, y, Math.Clamp(r.Width, 1, b.PixelWidth - x), Math.Clamp(r.Height, 1, b.PixelHeight - y))));
        Color best = Colors.Black;
        double bl = -1.0;
        for (int i = 0; i < p.Length; i += 4)
        {
            Color c = Color.FromRgb(p[i + 2], p[i + 1], p[i]);
            double l = ColorMath.RelLuminance(c);
            if (l > bl)
            {
                bl = l;
                best = c;
            }
        }
        return best;
    }

    // A horizontal run of at least 20 pixels in the given colour (the top edge of a toggle track); (-1, -1) when none.
    private static (int X, int Y) FindTrackEdge(BitmapSource b, Color c)
    {
        byte[] p = Pixels(b);
        int w = b.PixelWidth;
        for (int y = 0; y < b.PixelHeight; y++)
        {
            int run = 0;
            for (int x = 0; x < w; x++)
            {
                int i = (y * w + x) * 4;
                bool hit = Math.Abs(p[i + 2] - c.R) <= 3 && Math.Abs(p[i + 1] - c.G) <= 3 && Math.Abs(p[i] - c.B) <= 3;
                run = hit ? run + 1 : 0;
                if (run >= 20)
                {
                    return (x - run + 1, y);
                }
            }
        }
        return (-1, -1);
    }

    // The Dark rest preview is built on a worker: the page gets a black placeholder at once, and the same pixels as the
    // direct build land on it.
    private static void RestPreviewAsyncCheck()
    {
        bool placeholder = false;
        bool landed = false;
        bool same = false;
        string detail = "";
        try
        {
            SeasonalArt.QaClearPreviews();
            SeasonalArt.QaAsyncPreviews = true;
            Brush b = SeasonalArt.PreviewBrush(SeasonalArt.RestArt);
            ImageBrush? ib = b as ImageBrush;
            placeholder = ib != null && !ib.IsFrozen && ib.ImageSource is BitmapSource s0 && s0.PixelWidth == 1;
            SeasonalArt.QaLastPreviewTask?.Wait(15000);
            Application.Current.Dispatcher.Invoke(DispatcherPriority.Background, (Action)delegate { });
            if (ib?.ImageSource is BitmapSource s1 && s1.PixelWidth == 240)
            {
                landed = true;
                byte[] a = Pixels(s1);
                byte[] direct = Pixels(SeasonalArt.RestPreview(SeasonalArt.CustomPictureFor(SeasonalArt.RestArt)));
                same = a.SequenceEqual(direct);
            }
            detail = $"placeholder at once={placeholder} landed={landed} same as the direct build={same}";
        }
        catch (Exception ex)
        {
            detail = ex.Message;
        }
        finally
        {
            SeasonalArt.QaAsyncPreviews = false;
            SeasonalArt.QaClearPreviews();
        }
        Check("rest-preview-off-ui-thread", placeholder && landed && same, detail);
    }

    // The Start picture: a picture that is only locked is tried again (not remembered as broken), a decode that
    // finishes after Dark rest was left is not kept, and a screen of another scale gets the picture sized for it (only
    // the picture is prepared again).
    private static void RestStartPictureStateChecks(string outDir, StartScreen start, AppSettings real)
    {
        string? fixture = FixturePicture();
        if (fixture == null)
        {
            Check("rest-start-picture-states", false, "no fixture picture");
            return;
        }
        List<string> got = new List<string>();
        bool ok = true;
        string locked = Path.Combine(outDir, "fixture-locked");
        Directory.CreateDirectory(locked);
        string lockedFile = Path.Combine(locked, Path.GetFileName(fixture));
        File.Copy(fixture, lockedFile, overwrite: true);
        try
        {
            // locked while Start decodes it: the drawn scene, no entry for the picture; unlocked: decoded and shown
            SeasonalArt.DiagnosticsPictureDir = locked;
            StartScreen.QaClearBackgroundCache();
            using (FileStream hold = new FileStream(lockedFile, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                start.RefreshBackground();
                start.QaWaitBackground();
            }
            bool noPicKey = !start.QaRestBgKeys.Contains("rest-pic|", StringComparison.Ordinal);
            start.RefreshBackground();
            start.QaWaitBackground();
            bool picKey = start.QaRestBgKeys.Contains("rest-pic|", StringComparison.Ordinal);
            got.Add($"locked: picture remembered={!noPicKey}, after unlock shown={picKey}");
            ok &= noPicKey && picKey;
            // the cache keys carry the scale; another scale prepares the picture for it, and back
            bool scaleKey = start.QaRestBgKeys.Contains("@1|", StringComparison.Ordinal) || start.QaRestBgKeys.EndsWith("@1", StringComparison.Ordinal) || start.QaRestBgKeys.Contains("@1,", StringComparison.Ordinal);
            start.QaSetScreenScale(1.25);
            start.QaWaitBackground();
            double vw = (start.QaBgLayerBrush as ImageBrush)?.Viewport.Width ?? 0.0;
            start.QaSetScreenScale(0.0);
            start.QaWaitBackground();
            double vw0 = (start.QaBgLayerBrush as ImageBrush)?.Viewport.Width ?? 0.0;
            got.Add($"keys with the scale={scaleKey} viewport at 1.25={vw:F1} back={vw0:F1}");
            ok &= scaleKey && Math.Abs(vw - 1920.0 / 1.25) < 1.0 && Math.Abs(vw0 - 1920.0) < 1.0;
            // a decode still running when Normal comes back is dropped
            StartScreen.QaClearBackgroundCache();
            start.QaRefreshBackgroundNoWait();
            SettingsStore.DiagnosticsOverride = NormalSettings(real);
            ShellTheme.SyncRest();
            start.RefreshBackground();
            System.Diagnostics.Stopwatch sw = System.Diagnostics.Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < 2500)
            {
                Application.Current.Dispatcher.Invoke(DispatcherPriority.Background, (Action)delegate { });
                System.Threading.Thread.Sleep(20);
            }
            bool dropped = !start.QaRestBackgroundHeld;
            got.Add($"late decode kept in Normal={!dropped}");
            ok &= dropped;
        }
        finally
        {
            SettingsStore.DiagnosticsOverride = RestSettings(real);
            ShellTheme.SyncRest();
            SeasonalArt.DiagnosticsPictureDir = _fixtureEmpty;
            start.RefreshBackground();
            start.QaWaitBackground();
        }
        Check("rest-start-picture-states", ok, string.Join(" ; ", got));
    }

    // A Search pane built while Dark rest is on: after Normal comes back, its results scrollbar is the plain dark one
    // again (based on the application's own style, not on the Dark rest style it saw when it was built).
    private static void RestSearchScrollBarCheck(string outDir, StartScreen start, AppSettings real)
    {
        SearchPane pane = new SearchPane(() => start.AllApps, delegate { }) { Left = -4000.0, Top = -4000.0, ShowActivated = false };
        bool builtInRest = DarkRest.Visuals;
        bool ok = false;
        string detail = "";
        try
        {
            SettingsStore.DiagnosticsOverride = NormalSettings(real);
            ShellTheme.SyncRest();
            DarkRest.Sync();
            pane.QaRender(Path.Combine(outDir, "searchpane-built-in-rest-normal.png"), "", 400.0);
            Style? style = pane.QaResultsScrollBarStyle;
            object? normal = Application.Current.TryFindResource("Metro81.ScrollBarDark");
            ok = builtInRest && style != null && ReferenceEquals(style.BasedOn, normal) && !ReferenceEquals(normal, DarkRestPalette.BuiltScrollBarStyle);
            detail = $"built in Dark rest={builtInRest} based on the plain style={ReferenceEquals(style?.BasedOn, normal)}";
        }
        finally
        {
            SettingsStore.DiagnosticsOverride = RestSettings(real);
            ShellTheme.SyncRest();
            DarkRest.Sync();
            pane.Close();
        }
        Check("rest-search-scrollbar-after-rest", ok, detail);
    }
}
