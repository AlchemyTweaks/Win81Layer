#nullable enable

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Win81Layer;

// --seasonaltest, Dark rest follow-ups: opaque dark icons on black, the Dark rest picture decoded with its full range
// of dark levels, the name of its fitted copy, the link file kept robust (a manifest with a byte order mark, an
// extension folder that appears later, a reader holding the file for a moment), and on Start the desktop's very pixels,
// no copy of the picture for the zoom overlay, the picture released with Normal and a broken picture decoded once.
// Read-only like the rest of the harness; every file goes to the output folder.
internal static partial class SeasonalDiagnostics
{
    private static void RestVisibilityPureChecks(string outDir)
    {
        DarkFillIconChecks();
        PictureProfileChecks(outDir);
        PictureNameChecks();
        LinkRobustnessChecks(outDir);
    }

    private static BitmapSource SolidSquare(int size, Color c)
    {
        byte[] px = new byte[size * size * 4];
        for (int i = 0; i < px.Length; i += 4)
        {
            px[i] = c.B;
            px[i + 1] = c.G;
            px[i + 2] = c.R;
            px[i + 3] = 255;
        }
        BitmapSource b = BitmapSource.Create(size, size, 96, 96, PixelFormats.Bgra32, null, px, size * 4);
        b.Freeze();
        return b;
    }

    private static Color PixelAt(BitmapSource b, int x, int y)
    {
        byte[] p = Pixels(new CroppedBitmap(b, new Int32Rect(x, y, 1, 1)));
        return Color.FromRgb(p[2], p[1], p[0]);
    }

    // An opaque near-black square icon (it would melt into the black taskbar) gets the control-grey outline on every
    // icon path of Dark rest, and only there; a colourful icon is left alone.
    private static void DarkFillIconChecks()
    {
        BitmapSource dark = SolidSquare(32, Color.FromRgb(0x10, 0x14, 0x19));
        ImageSource visible = DarkRestIcons.Visible(dark);
        bool edged = false;
        string detail = "not a bitmap";
        if (visible is BitmapSource vb && !ReferenceEquals(vb, dark))
        {
            Color edge = PixelAt(vb, 0, 16);
            Color corner = PixelAt(vb, 31, 31);
            Color inside = PixelAt(vb, 16, 16);
            edged = edge == DarkRestPalette.BoundaryColor && corner == DarkRestPalette.BoundaryColor && inside == Color.FromRgb(0x10, 0x14, 0x19) && Contrast(edge, Colors.Black) >= 3.0;
            detail = $"edge={Hex(edge)} corner={Hex(corner)} inside={Hex(inside)} {Contrast(edge, Colors.Black):F2}:1 on black";
        }
        bool cached = ReferenceEquals(DarkRestIcons.Visible(dark), visible);
        BitmapSource red = SolidSquare(32, Color.FromRgb(0xD1, 0x34, 0x38));
        bool colourKept = ReferenceEquals(DarkRestIcons.Visible(red), red);
        RestIconEdgeConverter edgeConverter = new RestIconEdgeConverter();
        object? barOff = edgeConverter.Convert(new object[2] { dark, false }, typeof(ImageSource), null, CultureInfo.InvariantCulture);
        object? barOn = edgeConverter.Convert(new object[2] { dark, true }, typeof(ImageSource), null, CultureInfo.InvariantCulture);
        object? tileOff = SeasonalIconConverter.Instance.Convert(new object[4] { dark, null!, 24.0, false }, typeof(ImageSource), null, CultureInfo.InvariantCulture);
        object? tileOn = SeasonalIconConverter.Instance.Convert(new object[4] { dark, null!, 24.0, true }, typeof(ImageSource), null, CultureInfo.InvariantCulture);
        object? tileThree = SeasonalIconConverter.Instance.Convert(new object[3] { dark, null!, 24.0 }, typeof(ImageSource), null, CultureInfo.InvariantCulture);
        bool paths = ReferenceEquals(barOff, dark) && ReferenceEquals(barOn, visible) && ReferenceEquals(tileOff, dark) && ReferenceEquals(tileOn, visible) && ReferenceEquals(tileThree, dark)
            && ReferenceEquals(RestIconConverter.Lift(dark), visible);
        Check("rest-icons-dark-fill", edged && cached && colourKept && paths, $"{detail} cached={cached} colourKept={colourKept} paths(taskbar off/on, tiles off/on/plain, search)={paths}");
    }

    // A near-black JPEG with an embedded sRGB colour profile: the shell's decode keeps the darkest levels (the faint grid
    // of the Dark rest picture lives at levels 1 to 6), for Start and for the desktop copy alike. The colour-managed decode
    // is reported next to it for comparison.
    private static void PictureProfileChecks(string outDir)
    {
        string dir = Path.Combine(outDir, "fixture-profile");
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, "rest.jpg");
        int[] levels = new int[8] { 0, 1, 2, 3, 4, 5, 6, 8 };
        const int w = 2000;
        const int h = 1125;
        byte[] px = new byte[w * h * 3];
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                byte v = (byte)levels[Math.Min(levels.Length - 1, x / 250)];
                int i = (y * w + x) * 3;
                px[i] = v;
                px[i + 1] = v;
                px[i + 2] = v;
            }
        }
        BitmapSource src = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgr24, null, px, w * 3);
        byte[] jpeg;
        using (MemoryStream ms = new MemoryStream())
        {
            JpegBitmapEncoder enc = new JpegBitmapEncoder { QualityLevel = 100 };
            enc.Frames.Add(BitmapFrame.Create(src));
            enc.Save(ms);
            jpeg = ms.ToArray();
        }
        byte[] profile = Array.Empty<byte>();
        try
        {
            using Stream ps = new ColorContext(PixelFormats.Bgra32).OpenProfileStream();
            using MemoryStream pm = new MemoryStream();
            ps.CopyTo(pm);
            profile = pm.ToArray();
        }
        catch
        {
        }
        File.WriteAllBytes(path, profile.Length > 0 && profile.Length < 65000 ? WithIccProfile(jpeg, profile) : jpeg);
        int embedded = 0;
        try
        {
            using FileStream fs = File.OpenRead(path);
            BitmapDecoder probe = BitmapDecoder.Create(fs, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
            embedded = probe.Frames[0].ColorContexts?.Count ?? 0;
        }
        catch
        {
        }
        double[] Bands(BitmapSource bmp, double scale)
        {
            byte[] p = Pixels(bmp);
            int bw = bmp.PixelWidth;
            double[] means = new double[levels.Length];
            for (int k = 0; k < levels.Length; k++)
            {
                int x0 = (int)Math.Round((k * 250 + 75) * scale);
                int y0 = (int)Math.Round(500 * scale);
                int n = (int)Math.Round(100 * scale);
                double sum = 0;
                for (int y = y0; y < y0 + n; y++)
                {
                    for (int x = x0; x < x0 + n; x++)
                    {
                        sum += p[(y * bw + x) * 4 + 1];
                    }
                }
                means[k] = sum / (n * n);
            }
            return means;
        }
        List<string> bad = new List<string>();
        double[] shell = Bands(SeasonalArt.LoadPictureFilled(path, w, h), 1.0);
        string desk = Path.Combine(dir, "desktop-copy.png");
        SeasonalArt.RenderPicturePng(SeasonalArt.RestArt, path, 1920, 1080, desk);
        double[] desktop = Bands(LoadPng(desk), 0.96);
        BitmapImage managedImg = new BitmapImage();
        managedImg.BeginInit();
        managedImg.UriSource = new Uri(path);
        managedImg.CacheOption = BitmapCacheOption.OnLoad;
        managedImg.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
        managedImg.EndInit();
        managedImg.Freeze();
        double[] managed = Bands(managedImg, 1.0);
        for (int k = 0; k < levels.Length; k++)
        {
            if (Math.Abs(shell[k] - levels[k]) > 0.6)
            {
                bad.Add($"start level {levels[k]} decoded as {shell[k]:F2}");
            }
            if (Math.Abs(desktop[k] - levels[k]) > 0.6)
            {
                bad.Add($"desktop level {levels[k]} decoded as {desktop[k]:F2}");
            }
        }
        double sourceMean = levels.Average();
        if (Math.Abs(shell.Average() - sourceMean) > 0.3)
        {
            bad.Add($"mean {shell.Average():F2} vs source {sourceMean:F2}");
        }
        string detail = $"profile embedded={embedded} start={string.Join("/", shell.Select(v => v.ToString("F1", CultureInfo.InvariantCulture)))} desktop={string.Join("/", desktop.Select(v => v.ToString("F1", CultureInfo.InvariantCulture)))} colour-managed={string.Join("/", managed.Select(v => v.ToString("F1", CultureInfo.InvariantCulture)))}";
        Metrics["RestPictureProfile"] = detail;
        Check("rest-picture-colour-profile", embedded > 0 && bad.Count == 0, bad.Count == 0 ? detail : string.Join(" ; ", bad) + " | " + detail);
        try
        {
            File.Delete(desk);
        }
        catch
        {
        }
    }

    // The JPEG with an APP2 ICC_PROFILE segment holding profile (after the JFIF header when there is one).
    private static byte[] WithIccProfile(byte[] jpeg, byte[] profile)
    {
        int at = 2;
        if (jpeg.Length > 6 && jpeg[2] == 0xFF && jpeg[3] == 0xE0)
        {
            at = 4 + (jpeg[4] << 8 | jpeg[5]);
        }
        byte[] tag = System.Text.Encoding.ASCII.GetBytes("ICC_PROFILE\0");
        int len = 2 + tag.Length + 2 + profile.Length;
        using MemoryStream ms = new MemoryStream();
        ms.Write(jpeg, 0, at);
        ms.WriteByte(0xFF);
        ms.WriteByte(0xE2);
        ms.WriteByte((byte)(len >> 8));
        ms.WriteByte((byte)len);
        ms.Write(tag, 0, tag.Length);
        ms.WriteByte(1);
        ms.WriteByte(1);
        ms.Write(profile, 0, profile.Length);
        ms.Write(jpeg, at, jpeg.Length - at);
        return ms.ToArray();
    }

    // The fitted copy of the Dark rest picture carries its pipeline version (a copy fitted the earlier way is fitted
    // again); the seasons' names are unchanged.
    private static void PictureNameChecks()
    {
        string source = Path.Combine(_fixturePicture, "rest.png");
        string rest = SeasonalArt.CustomWallpaperFileName(SeasonalArt.RestArt, source, 1920, 1080);
        string halloween = SeasonalArt.CustomWallpaperFileName("halloween", source, 1920, 1080);
        bool ok = Regex.IsMatch(rest, "^rest-picture-v2-[0-9a-f]{8}-1920x1080\\.png$") && Regex.IsMatch(halloween, "^halloween-picture-[0-9a-f]{8}-1920x1080\\.png$");
        Check("rest-picture-file-name", ok, $"{rest} {halloween}");
    }

    private static void LinkRobustnessChecks(string outDir)
    {
        string root = Path.Combine(outDir, "link-robust");
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
        // a manifest saved with a byte order mark is still found
        {
            string launcher = Path.Combine(root, "bom", "Launcher-x64");
            string ext = Path.Combine(root, "bom", "ChromeExtension", "MetroNewTab");
            Directory.CreateDirectory(launcher);
            Directory.CreateDirectory(ext);
            byte[] body = System.Text.Encoding.UTF8.GetBytes("{\"manifest_version\":3,\"name\":\"Metro Tab\",\"version\":\"1.0\"}");
            File.WriteAllBytes(Path.Combine(ext, "manifest.json"), new byte[3] { 0xEF, 0xBB, 0xBF }.Concat(body).ToArray());
            ShellLookLink.ResetCache();
            string? found = ShellLookLink.FindExtensionFolder("", launcher);
            ShellLookLink.ResetCache();
            Check("link-manifest-bom", string.Equals(found, ext, StringComparison.OrdinalIgnoreCase), $"found={found}");
        }
        // an extension folder that appears after a first miss is found by the next sync, without a restart
        {
            string launcher = Path.Combine(root, "later", "Launcher-x64");
            Directory.CreateDirectory(launcher);
            ShellLookLink.ResetCache();
            string? first = ShellLookLink.FindExtensionFolder("", launcher);
            string ext = Path.Combine(root, "later", "ChromeExtension", "MetroNewTab");
            Directory.CreateDirectory(ext);
            File.WriteAllText(Path.Combine(ext, "manifest.json"), "{\"manifest_version\":3,\"name\":\"Metro Tab\"}");
            string? second = ShellLookLink.FindExtensionFolder("", launcher);
            ShellLookLink.ResetCache();
            Check("link-folder-appears-later", first == null && string.Equals(second, ext, StringComparison.OrdinalIgnoreCase), $"first={first ?? "none"} second={second}");
        }
        // a reader holding the file for a moment (without delete sharing, as a browser read does): the replace is tried
        // again and goes through
        {
            string folder = Path.Combine(root, "retry");
            Directory.CreateDirectory(folder);
            DateTime t0 = new DateTime(2026, 10, 17, 9, 30, 0, DateTimeKind.Utc);
            ShellLookLink.LookState a = new ShellLookLink.LookState("rest", true, "#44636A");
            ShellLookLink.LookState b = new ShellLookLink.LookState("normal", false, "#2672EC");
            ShellLookLink.WriteIfChanged(folder, a, t0);
            string target = Path.Combine(folder, ShellLookLink.FileName);
            ShellLookLink.Outcome o;
            FileStream hold = new FileStream(target, FileMode.Open, FileAccess.Read, FileShare.Read);
            Task release = Task.Run(async delegate
            {
                await Task.Delay(300);
                hold.Dispose();
            });
            try
            {
                o = ShellLookLink.WriteIfChanged(folder, b, t0.AddMinutes(1));
            }
            finally
            {
                release.Wait(2000);
                hold.Dispose();
            }
            bool read = ShellLookLink.TryRead(target, out ShellLookLink.LookState back);
            bool clean = !Directory.GetFiles(folder, "*.tmp-*").Any();
            Check("link-retry-after-reader", o == ShellLookLink.Outcome.Written && read && back == b && clean, $"outcome={o} back={back} tempLeft={!clean}");
        }
    }

    // ----- Start: the desktop's pixels, no copy for the overlay, released with Normal, a broken picture decoded once

    private static void RestStartRobustChecks(string outDir, StartScreen start, AppSettings real)
    {
        // Start and the desktop copy come from one decode: the empty board shows exactly the desktop file's pixels
        if (_syntheticPicture || File.Exists(Path.Combine(outDir, "start-rest-picture.png")))
        {
            SeasonalArt.DiagnosticsPictureDir = _fixturePicture;
            string? picture = SeasonalArt.CustomPictureFor(SeasonalArt.RestArt);
            SeasonalArt.DiagnosticsPictureDir = _fixtureEmpty;
            string desk = Path.Combine(outDir, "fixture-picture-desktop.png");
            int worst = -1;
            if (picture != null)
            {
                SeasonalArt.RenderPicturePng(SeasonalArt.RestArt, picture, 1920, 1080, desk);
                byte[] a = Pixels(new CroppedBitmap(LoadPng(Path.Combine(outDir, "start-rest-picture.png")), new Int32Rect(1500, 650, 400, 350)));
                byte[] b = Pixels(new CroppedBitmap(LoadPng(desk), new Int32Rect(1500, 650, 400, 350)));
                worst = 0;
                for (int i = 0; i < a.Length; i += 4)
                {
                    for (int c = 0; c < 3; c++)
                    {
                        worst = Math.Max(worst, Math.Abs(a[i + c] - b[i + c]));
                    }
                }
                try
                {
                    File.Delete(desk);
                }
                catch
                {
                }
            }
            Check("rest-start-picture-matches-desktop", worst >= 0 && worst <= 1, $"largest channel difference={worst}");
        }
        // the zoom overlay draws the board's own bitmap (a new brush around it, not a copy)
        Check("rest-start-overlay-no-copy", start.QaRestOverlaySharesBitmap, $"overlay={start.QaZoomOverlayBrush?.GetType().Name}");
        // a picture that cannot be read: decoded once, the drawn scene shown, and not decoded again on the next refresh
        {
            string broken = Path.Combine(outDir, "fixture-broken");
            Directory.CreateDirectory(broken);
            byte[] junk = new byte[4096];
            for (int i = 0; i < junk.Length; i++)
            {
                junk[i] = (byte)(i * 31 + 7);
            }
            File.WriteAllBytes(Path.Combine(broken, "rest.jpg"), junk);
            SeasonalArt.DiagnosticsPictureDir = broken;
            StartScreen.QaClearBackgroundCache();
            int d0 = StartScreen.QaRestPictureDecodes;
            start.RefreshBackground();
            start.QaWaitBackground();
            int d1 = StartScreen.QaRestPictureDecodes;
            bool drawn = start.QaBgLayerBrush is ImageBrush && start.QaBackgroundSettled;
            start.RefreshBackground();
            start.QaWaitBackground();
            int d2 = StartScreen.QaRestPictureDecodes;
            Check("rest-start-broken-picture-once", d1 == d0 + 1 && d2 == d1 && drawn, $"decodes={d1 - d0} then {d2 - d1} drawn={drawn}");
            SeasonalArt.DiagnosticsPictureDir = _fixtureEmpty;
            start.RefreshBackground();
            start.QaWaitBackground();
        }
        // Normal releases the screen-sized picture (the brush and its cache entries); Dark rest brings it back
        {
            SettingsStore.DiagnosticsOverride = NormalSettings(real);
            ShellTheme.SyncRest();
            start.RefreshBackground();
            start.QaWaitBackground();
            bool released = !start.QaRestBackgroundHeld;
            SettingsStore.DiagnosticsOverride = RestSettings(real);
            ShellTheme.SyncRest();
            start.RefreshBackground();
            start.QaWaitBackground();
            Check("rest-start-released-in-normal", released && start.QaRestBackgroundHeld, $"releasedInNormal={released} heldInRest={start.QaRestBackgroundHeld}");
        }
    }
}
