using System;
using System.Collections.Generic;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

#nullable enable annotations

namespace Win81Layer;

// Whether an icon needs a plate behind it to stay visible on the list pane, and on which pane.
internal enum IconPlate
{
    None,
    // A white single-colour logo: accent plate on the light pane.
    LightMono,
    // A black single-colour logo: light neutral plate on the dark pane.
    DarkMono,
    // A pale multi-colour icon with no part that stands out from the light pane: neutral plate there.
    PaleOnLight,
    // A dark multi-colour icon with no part that stands out from the dark pane: light neutral plate there.
    PaleOnDark,
    // A mostly opaque icon whose body is as dark as the dark pane (a black square with a few grey strokes): a few pixels
    // stand out, but the icon reads as a hole in the list. Light neutral plate on the dark pane.
    DarkFill
}

// Windows 7 Start menu icons: exact-size, untrimmed bitmaps (32 px in the main list, 16 px in All Programs and search)
// loaded through AppInventory.LoadIconExact on one background STA worker into a strong cache keyed "path|box|px". The
// worker runs at BelowNormal priority and exits after 1.5 s with an empty queue, so a closed menu costs nothing. Loaded
// is raised on the UI dispatcher at Background priority with the cache key. The QA harness loads synchronously.
// A letter tile (no icon found) is not final: a request older than LetterRetryMs loads the path again, so an icon that
// was only missing for a moment (a cold shell namespace after logon, an app being updated) appears on a later open.
internal static class W7Icons
{
    // Img: the icon at box x scale. Plated: the same icon drawn at the plate inset size from a larger source, used while
    // a plate is shown. Tick: when the entry was made (Environment.TickCount64).
    internal readonly record struct Entry(ImageSource? Img, IconPlate Plate, bool IsLetter, ImageSource? Plated, long Tick);

    // Age after which a letter tile is loaded again (the QA harness lowers it for one check).
    internal static long LetterRetryMs = 30000;

    // Worker threads whose Dispatcher was shut down on exit (read by the QA harness).
    private static int _dispatcherShutdowns;

    internal static int DispatcherShutdowns => Volatile.Read(ref _dispatcherShutdowns);

    private static readonly object Gate = new object();

    private static readonly Dictionary<string, Entry> Cache = new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);

    private static readonly Queue<(string Key, string Path, int Box, int Px, string Name)> Work = new Queue<(string Key, string Path, int Box, int Px, string Name)>();

    private static readonly HashSet<string> Pending = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    private static Thread? _worker;

    private static Dispatcher? _ui;

    internal static event Action<string>? Loaded;

    // QA: load on the calling thread, never start the worker.
    internal static bool Synchronous;

    // Loads done on the calling thread (QA only). The harness leaves shows that loaded icons out of its warm-open
    // metric, because the live menu loads them off the UI thread.
    private static int _syncLoads;

    internal static int SyncLoads => Volatile.Read(ref _syncLoads);

    internal static bool WorkerAlive
    {
        get
        {
            lock (Gate)
            {
                return _worker != null && _worker.IsAlive;
            }
        }
    }

    internal static string Key(string path, int box, int px)
    {
        return path + "|" + box + "|" + px;
    }

    // The plated icon size in DIP: 24 inside the 32 box, 14 inside the 16 box (a 1 px plate rim).
    internal static int InsetDip(int box)
    {
        return box == 32 ? 24 : (box == 16 ? 14 : (int)Math.Round(box * 0.75, MidpointRounding.AwayFromZero));
    }

    internal static int PlatedPx(int box, int px)
    {
        return Math.Max(1, (int)Math.Round(InsetDip(box) * (px / (double)Math.Max(1, box)), MidpointRounding.AwayFromZero));
    }

    // A letter tile that has waited long enough to be loaded again.
    internal static bool IsStale(in Entry e)
    {
        return e.IsLetter && Environment.TickCount64 - e.Tick >= LetterRetryMs;
    }

    internal static bool TryGet(string path, int box, int px, out Entry e)
    {
        return TryGetKey(Key(path, box, px), out e);
    }

    internal static bool TryGetKey(string key, out Entry e)
    {
        lock (Gate)
        {
            return Cache.TryGetValue(key, out e);
        }
    }

    // Glyph entries (win81:* and launcher://) are fitted on the UI thread, because their source belongs to it. They are
    // always plated, so the plated copy is drawn from the vector at the inset size.
    internal static Entry GetOrAddGlyph(string key, ImageSource src, int box, int px)
    {
        lock (Gate)
        {
            if (Cache.TryGetValue(key, out Entry e))
            {
                return e;
            }
        }
        Entry made = new Entry(Fit(src, px), IconPlate.None, IsLetter: false, Plated: Fit(src, PlatedPx(box, px)), Environment.TickCount64);
        lock (Gate)
        {
            Cache[key] = made;
        }
        return made;
    }

    // A letter tile on the UI thread, for entries that have no shell icon at all (a glyph entry whose glyph was released).
    // It depends only on the name and size, so it is cached for good.
    internal static Entry Letter(string name, int px)
    {
        string key = "letter:" + name + "|" + px;
        lock (Gate)
        {
            if (Cache.TryGetValue(key, out Entry e))
            {
                return e;
            }
        }
        Entry made = new Entry(LetterTile(name, px), IconPlate.None, IsLetter: true, Plated: null, Environment.TickCount64);
        lock (Gate)
        {
            Cache[key] = made;
        }
        return made;
    }

    // QA: puts a letter-tile entry for a path, as a load that found no icon (a cold shell after logon) would leave it.
    internal static void QaPutLetter(string path, int box, int px, string name)
    {
        Entry made = new Entry(LetterTile(name, px), IconPlate.None, IsLetter: true, Plated: null, Environment.TickCount64);
        lock (Gate)
        {
            Cache[Key(path, box, px)] = made;
        }
    }

    // Queues a load (or, in QA, loads now). A request for a key that is already queued, or cached and not a stale letter
    // tile, does nothing. A stale letter tile stays in the cache (and on screen) until the new result replaces it.
    internal static void Request(string path, int box, int px, string name)
    {
        string key = Key(path, box, px);
        lock (Gate)
        {
            if (Pending.Contains(key) || (Cache.TryGetValue(key, out Entry cached) && !IsStale(cached)))
            {
                return;
            }
            if (!Synchronous)
            {
                Pending.Add(key);
                Work.Enqueue((key, path, box, px, name));
                _ui ??= Dispatcher.CurrentDispatcher;
                if (_worker == null || !_worker.IsAlive)
                {
                    Thread t = new Thread(WorkerLoop)
                    {
                        IsBackground = true,
                        Priority = ThreadPriority.BelowNormal,
                        Name = "Win7 Start icons"
                    };
                    t.SetApartmentState(ApartmentState.STA);
                    _worker = t;
                    t.Start();
                }
                else
                {
                    Monitor.PulseAll(Gate);
                }
                return;
            }
        }
        Interlocked.Increment(ref _syncLoads);
        Entry loaded = SafeLoadEntry(path, box, px, name);
        lock (Gate)
        {
            Cache[key] = loaded;
        }
    }

    internal static void Clear()
    {
        lock (Gate)
        {
            Cache.Clear();
            Work.Clear();
            Pending.Clear();
        }
    }

    private static void WorkerLoop()
    {
        try
        {
            while (true)
            {
                (string Key, string Path, int Box, int Px, string Name) item;
                lock (Gate)
                {
                    while (Work.Count == 0)
                    {
                        if (!Monitor.Wait(Gate, 1500) && Work.Count == 0)
                        {
                            _worker = null;
                            return;
                        }
                    }
                    item = Work.Dequeue();
                }
                Entry e = SafeLoadEntry(item.Path, item.Box, item.Px, item.Name);
                Dispatcher? ui;
                lock (Gate)
                {
                    if (Pending.Remove(item.Key))
                    {
                        Cache[item.Key] = e;
                    }
                    ui = _ui;
                }
                string key = item.Key;
                try
                {
                    ui?.BeginInvoke(DispatcherPriority.Background, (Action)delegate
                    {
                        Loaded?.Invoke(key);
                    });
                }
                catch
                {
                }
            }
        }
        finally
        {
            // DrawingVisual and RenderTargetBitmap gave this thread a Dispatcher (a message-only window and render
            // state). Shut it down before the thread ends so nothing is left behind; the frozen results do not need it.
            try
            {
                Dispatcher? d = Dispatcher.FromThread(Thread.CurrentThread);
                if (d != null)
                {
                    d.InvokeShutdown();
                    if (d.HasShutdownFinished)
                    {
                        Interlocked.Increment(ref _dispatcherShutdowns);
                    }
                }
            }
            catch
            {
            }
        }
    }

    // A load that never throws: a failure gives an empty letter entry, which is retried like any letter tile.
    private static Entry SafeLoadEntry(string path, int box, int px, string name)
    {
        try
        {
            return LoadEntry(path, box, px, name);
        }
        catch
        {
            return new Entry(null, IconPlate.None, IsLetter: true, Plated: null, Environment.TickCount64);
        }
    }

    // The exact-size icon, or a letter tile at the same size when the path has none. A plated icon also gets a copy drawn
    // at the plate inset size from a larger source (32 px for a 16 px row), which stays sharp where scaling the row
    // bitmap down would blur it.
    private static Entry LoadEntry(string path, int box, int px, string name)
    {
        ImageSource? img = LoadExact(path, px);
        BitmapSource? main = null;
        if (img is BitmapSource bs && bs.PixelWidth == px && bs.PixelHeight == px)
        {
            main = bs;
        }
        else if (img != null)
        {
            main = Fit(img, px) as BitmapSource;
        }
        if (main == null)
        {
            return new Entry(LetterTile(name, px), IconPlate.None, IsLetter: true, Plated: null, Environment.TickCount64);
        }
        IconPlate plate = Classify(main);
        ImageSource? plated = null;
        if (plate != IconPlate.None)
        {
            int inset = PlatedPx(box, px);
            ImageSource? hi = LoadExact(path, box == 16 ? px * 2 : (int)Math.Round(px * 1.5, MidpointRounding.AwayFromZero));
            plated = Fit(hi ?? main, inset);
        }
        return new Entry(main, plate, IsLetter: false, plated, Environment.TickCount64);
    }

    private static ImageSource? LoadExact(string path, int px)
    {
        try
        {
            return AppInventory.LoadIconExact(path, px);
        }
        catch
        {
            return null;
        }
    }

    // IconResolver's letter tile drawn straight at px (its own cache is UI-thread only and not keyed by size).
    private static ImageSource LetterTile(string name, int px)
    {
        DrawingVisual dv = new DrawingVisual();
        using (DrawingContext dc = dv.RenderOpen())
        {
            IconResolver.DrawLetter(dc, new Rect(0.0, 0.0, px, px), name, IconResolver.AccentFor(name));
        }
        RenderTargetBitmap rtb = new RenderTargetBitmap(px, px, 96.0, 96.0, PixelFormats.Pbgra32);
        rtb.Render(dv);
        rtb.Freeze();
        return rtb;
    }

    // Over the pixels with alpha >= 0x80: luma is Rec.601 of the sRGB values and saturation is HSL saturation, both 0..1.
    // - LightMono: a white single-colour logo (mean luma >= 0.80, mean saturation <= 0.15, coverage >= 3%) with no dark
    //   outline (10th-percentile luma >= 0.75). A white page with a grey outline is readable on white and is not plated.
    // - DarkMono: a black single-colour logo (mean luma <= 0.12, mean saturation <= 0.15) with no light part
    //   (90th-percentile luma <= 0.25).
    // - PaleOnLight / PaleOnDark: any other icon (coverage >= 3%) whose most contrasting tenth still has less than 1.5:1
    //   WCAG contrast with that pane. These get a neutral plate, not the accent one.
    // - DarkFill: any other icon with coverage >= 50% whose median and 75th-percentile pixels have less than 1.5:1 with
    //   the dark pane: most of its area vanishes there, even if a few strokes stand out.
    internal static IconPlate Classify(BitmapSource src)
    {
        try
        {
            BitmapSource b = src.Format == PixelFormats.Bgra32 ? src : new FormatConvertedBitmap(src, PixelFormats.Bgra32, null, 0.0);
            int w = b.PixelWidth;
            int h = b.PixelHeight;
            if (w <= 0 || h <= 0)
            {
                return IconPlate.None;
            }
            int stride = w * 4;
            byte[] px = new byte[stride * h];
            b.CopyPixels(px, stride, 0);
            List<double> lumas = new List<double>(w * h);
            List<double> rel = new List<double>(w * h);
            double lum = 0.0;
            double sat = 0.0;
            for (int o = 0; o < px.Length; o += 4)
            {
                if (px[o + 3] < 0x80)
                {
                    continue;
                }
                double bl = px[o] / 255.0;
                double gr = px[o + 1] / 255.0;
                double rd = px[o + 2] / 255.0;
                double l = 0.299 * rd + 0.587 * gr + 0.114 * bl;
                lum += l;
                lumas.Add(l);
                rel.Add(ColorMath.RelLuminance(Color.FromRgb(px[o + 2], px[o + 1], px[o])));
                ColorMath.RgbToHsl(rd, gr, bl, out _, out double s, out _);
                sat += s;
            }
            int n = lumas.Count;
            if (n == 0)
            {
                return IconPlate.None;
            }
            lum /= n;
            sat /= n;
            double coverage = n / (double)(w * h);
            lumas.Sort();
            rel.Sort();
            double p10 = lumas[(int)Math.Floor(0.10 * (n - 1))];
            double p90 = lumas[(int)Math.Ceiling(0.90 * (n - 1))];
            if (lum >= 0.80 && sat <= 0.15 && coverage >= 0.03 && p10 >= 0.75)
            {
                return IconPlate.LightMono;
            }
            if (lum <= 0.12 && sat <= 0.15 && p90 <= 0.25)
            {
                return IconPlate.DarkMono;
            }
            if (coverage < 0.03)
            {
                return IconPlate.None;
            }
            double lightPane = ColorMath.RelLuminance(Win7Palette.PaneLight);
            double darkPane = ColorMath.RelLuminance(Win7Palette.PaneDark);
            double darkest = rel[(int)Math.Floor(0.10 * (n - 1))];
            double lightest = rel[(int)Math.Ceiling(0.90 * (n - 1))];
            if ((lightPane + 0.05) / (darkest + 0.05) < 1.5)
            {
                return IconPlate.PaleOnLight;
            }
            if ((lightest + 0.05) / (darkPane + 0.05) < 1.5)
            {
                return IconPlate.PaleOnDark;
            }
            if (coverage >= 0.5)
            {
                (double p50, double p75, _) = PaneContrast(rel, darkPane);
                if (p50 < 1.5 && p75 < 1.5)
                {
                    return IconPlate.DarkFill;
                }
            }
            return IconPlate.None;
        }
        catch
        {
            return IconPlate.None;
        }
    }

    // The 50th, 75th and 90th percentile of the WCAG contrast between pixels (relative luminances) and a pane.
    internal static (double P50, double P75, double P90) PaneContrast(List<double> rel, double pane)
    {
        if (rel.Count == 0)
        {
            return (0.0, 0.0, 0.0);
        }
        List<double> cr = new List<double>(rel.Count);
        foreach (double l in rel)
        {
            cr.Add((Math.Max(l, pane) + 0.05) / (Math.Min(l, pane) + 0.05));
        }
        cr.Sort();
        int n = cr.Count;
        return (cr[(int)Math.Floor(0.50 * (n - 1))], cr[(int)Math.Floor(0.75 * (n - 1))], cr[(int)Math.Ceiling(0.90 * (n - 1))]);
    }

    // Bgra32 pixels of a bitmap (null when it is not one).
    private static byte[]? PixelsOf(ImageSource? src, out int w, out int h)
    {
        w = 0;
        h = 0;
        if (src is not BitmapSource bs || bs.PixelWidth <= 0 || bs.PixelHeight <= 0)
        {
            return null;
        }
        BitmapSource b = bs.Format == PixelFormats.Bgra32 ? bs : new FormatConvertedBitmap(bs, PixelFormats.Bgra32, null, 0.0);
        w = b.PixelWidth;
        h = b.PixelHeight;
        byte[] px = new byte[w * h * 4];
        b.CopyPixels(px, w * 4, 0);
        return px;
    }

    // Whether two bitmaps have the same size and the same pixels.
    internal static bool SamePixels(ImageSource? a, ImageSource? b)
    {
        byte[]? pa = PixelsOf(a, out int wa, out int ha);
        byte[]? pb = PixelsOf(b, out int wb, out int hb);
        return pa != null && pb != null && wa == wb && ha == hb && pa.AsSpan().SequenceEqual(pb);
    }

    // An open folder drawn at px x px from the closed folder icon's footprint and colours (no image asset): the back
    // panel with its tab in the closed icon's darker tab colour, a sheet of paper above the front, and the front flap as
    // a lighter parallelogram over the lower part whose top edge is shifted right, the way Windows 7 drew an open
    // folder. Without a closed icon, Windows 7 folder tones are used.
    internal static ImageSource OpenFolder(ImageSource? closed, int px)
    {
        double k = px / 16.0;
        double top = Math.Round(3.0 * k);
        double bottom = Math.Round(15.0 * k);
        Color tab = Color.FromRgb(0xE8, 0xAE, 0x2A);
        Color frontTop = Color.FromRgb(0xFF, 0xE8, 0xA0);
        Color frontBottom = Color.FromRgb(0xFF, 0xD0, 0x50);
        Color edge = Color.FromRgb(0xDC, 0x9E, 0x22);
        byte[]? p = PixelsOf(closed, out int w, out int h);
        if (p != null && w == px && h == px)
        {
            // The opaque rows of the closed icon give the footprint; its top rows are the tab, the middle and lower rows
            // the front, the last row the bottom edge.
            int first = -1;
            int last = -1;
            for (int y = 0; y < h; y++)
            {
                if (RowMean(p, w, y, out _) > 0)
                {
                    if (first < 0)
                    {
                        first = y;
                    }
                    last = y;
                }
            }
            if (first >= 0 && last - first >= 4)
            {
                top = first;
                bottom = last + 1;
                double hh = bottom - top;
                tab = MeanRows(p, w, first, first + Math.Max(1, (int)Math.Round(0.15 * hh)), tab);
                frontTop = MeanRows(p, w, first + (int)Math.Round(0.40 * hh), first + (int)Math.Round(0.40 * hh) + 1, frontTop);
                frontBottom = MeanRows(p, w, first + (int)Math.Round(0.80 * hh), first + (int)Math.Round(0.80 * hh) + 1, frontBottom);
                edge = MeanRows(p, w, last, last + 1, edge);
            }
        }
        double height = bottom - top;
        double left = 0.0;
        double right = px;
        double unit = Math.Max(1.0, Math.Round(k));
        double bodyTop = top + Math.Round(0.16 * height);
        double flapTop = top + Math.Round(0.46 * height);
        double skew = Math.Round(3.0 * k);
        Color backBottom = Color.FromRgb((byte)(tab.R * 0.92), (byte)(tab.G * 0.92), (byte)(tab.B * 0.92));
        DrawingVisual dv = new DrawingVisual();
        using (DrawingContext dc = dv.RenderOpen())
        {
            // Back panel: the tab over the left half, then the full-width body, with softly rounded corners like the shell's
            // folder.
            double radius = Math.Max(0.5, 0.8 * k);
            GeometryGroup back = new GeometryGroup { FillRule = FillRule.Nonzero };
            back.Children.Add(new RectangleGeometry(new Rect(left, top, Math.Round(0.45 * px), bodyTop - top + 2.0 * unit), radius, radius));
            back.Children.Add(new RectangleGeometry(new Rect(left, bodyTop, right - left, bottom - bodyTop), radius, radius));
            back.Freeze();
            dc.DrawGeometry(Win7Palette.Vertical(tab, backBottom), null, back);
            // A sheet of paper inside, showing between the back panel and the open flap.
            Rect paper = new Rect(left + Math.Round(2.0 * k), bodyTop + unit, Math.Max(unit, px - Math.Round(2.0 * k) - Math.Round(3.0 * k)), Math.Max(unit, flapTop - bodyTop));
            dc.DrawRectangle(Win7Palette.Solid(Color.FromRgb(0xC8, 0xC8, 0xC8)), null, paper);
            dc.DrawRectangle(Win7Palette.Solid(Colors.White), null, new Rect(paper.X + unit, paper.Y + unit, Math.Max(0.0, paper.Width - 2.0 * unit), Math.Max(0.0, paper.Height)));
            // Front flap: a parallelogram whose top edge is shifted right, with a light top line and a darker bottom edge.
            StreamGeometry flap = new StreamGeometry();
            using (StreamGeometryContext g = flap.Open())
            {
                g.BeginFigure(new Point(left + skew, flapTop), isFilled: true, isClosed: true);
                g.LineTo(new Point(right, flapTop), true, false);
                g.LineTo(new Point(right - skew, bottom), true, false);
                g.LineTo(new Point(left, bottom), true, false);
            }
            flap.Freeze();
            dc.DrawGeometry(Win7Palette.Vertical(frontTop, frontBottom), null, flap);
            dc.DrawRectangle(Win7Palette.Solid(Color.FromArgb(0xB0, 0xFF, 0xFF, 0xFF)), null, new Rect(left + skew + unit, flapTop, Math.Max(0.0, right - left - skew - 2.0 * unit), unit));
            dc.DrawRectangle(Win7Palette.Solid(edge), null, new Rect(left, bottom - unit, Math.Max(0.0, right - skew - left), unit));
        }
        RenderTargetBitmap rtb = new RenderTargetBitmap(px, px, 96.0, 96.0, PixelFormats.Pbgra32);
        rtb.Render(dv);
        rtb.Freeze();
        return rtb;
    }

    // Mean colour of the opaque pixels of one row; returns their count.
    private static int RowMean(byte[] p, int w, int y, out Color mean)
    {
        long r = 0;
        long g = 0;
        long b = 0;
        int n = 0;
        for (int x = 0; x < w; x++)
        {
            int o = (y * w + x) * 4;
            if (p[o + 3] >= 0x80)
            {
                b += p[o];
                g += p[o + 1];
                r += p[o + 2];
                n++;
            }
        }
        mean = n > 0 ? Color.FromRgb((byte)(r / n), (byte)(g / n), (byte)(b / n)) : Colors.Transparent;
        return n;
    }

    private static Color MeanRows(byte[] p, int w, int from, int to, Color fallback)
    {
        long r = 0;
        long g = 0;
        long b = 0;
        int rows = 0;
        int h = p.Length / (w * 4);
        for (int y = Math.Max(0, from); y < Math.Min(h, to); y++)
        {
            if (RowMean(p, w, y, out Color c) > 0)
            {
                r += c.R;
                g += c.G;
                b += c.B;
                rows++;
            }
        }
        return rows > 0 ? Color.FromRgb((byte)(r / rows), (byte)(g / rows), (byte)(b / rows)) : fallback;
    }

    // HighQuality, uniform and centred into an exact px x px bitmap; frozen. An exact-size bitmap is returned as is.
    internal static ImageSource Fit(ImageSource src, int px)
    {
        if (src is BitmapSource bs && bs.PixelWidth == px && bs.PixelHeight == px)
        {
            if (bs.CanFreeze && !bs.IsFrozen)
            {
                bs.Freeze();
            }
            return bs;
        }
        double sw = src.Width;
        double sh = src.Height;
        if (src is BitmapSource b2)
        {
            sw = b2.PixelWidth;
            sh = b2.PixelHeight;
        }
        double k = (sw > 0.0 && sh > 0.0) ? Math.Min(px / sw, px / sh) : 1.0;
        double dw = sw * k;
        double dh = sh * k;
        DrawingVisual dv = new DrawingVisual();
        RenderOptions.SetBitmapScalingMode(dv, BitmapScalingMode.HighQuality);
        using (DrawingContext dc = dv.RenderOpen())
        {
            dc.DrawImage(src, new Rect((px - dw) / 2.0, (px - dh) / 2.0, dw, dh));
        }
        RenderTargetBitmap rtb = new RenderTargetBitmap(px, px, 96.0, 96.0, PixelFormats.Pbgra32);
        rtb.Render(dv);
        rtb.Freeze();
        return rtb;
    }
}
