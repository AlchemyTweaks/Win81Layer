using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Win81Layer;

// Layer C — resolver over the authentic Windows 8.1 asset library (Layer A) built by the extractor.
// Call GetAsset("Network.Wifi.Bars5", px) to get an ORIGINAL Win8.1 icon (native size nearest px, alpha preserved),
// instead of hardcoding file paths. Manifest-driven: reads assets/Windows81/**/<Category>.manifest.json so new
// categories light up automatically. Everything is cached + frozen; nothing is redrawn or restyled.
public static class Win81AssetResolver
{
    private sealed class Entry
    {
        public string Dir = "";     // the category directory that holds the manifest
        public string Dll = "";     // source dll base name (folder under _raw)
        public string Id = "";      // RT_GROUP_ICON resource id
        public int[] Sizes = Array.Empty<int>();
    }

    private static readonly string Root = Path.Combine(AppContext.BaseDirectory, "assets", "Windows81");
    private static readonly object _gate = new object();
    private static Dictionary<string, Entry> _map;
    private static readonly Dictionary<string, ImageSource> _cache = new Dictionary<string, ImageSource>(StringComparer.OrdinalIgnoreCase);

    public static bool Available
    {
        get { EnsureLoaded(); return _map.Count > 0; }
    }

    // Returns the authentic asset for a semantic name at (or above) the requested pixel size, or null if not present
    // (callers keep their existing recreated/vector fallback so a missing asset can never break a surface).
    public static ImageSource GetAsset(string semantic, int px)
    {
        if (string.IsNullOrEmpty(semantic)) return null;
        EnsureLoaded();
        if (!_map.TryGetValue(semantic, out Entry e)) return null;
        int size = BestSize(e.Sizes, px);
        if (size <= 0) return null;
        string key = semantic + "@" + size;
        lock (_gate)
        {
            if (_cache.TryGetValue(key, out ImageSource cached)) return cached;
            ImageSource img = LoadPng(Path.Combine(e.Dir, "_raw", e.Dll, e.Id + "_" + size + "x" + size + ".png"));
            _cache[key] = img;   // cache nulls too, so we don't hit the disk repeatedly for a missing file
            return img;
        }
    }

    // Maps the live network state to the authentic pnidui semantic and returns the icon (null => caller falls back).
    public static ImageSource NetworkImage(NetState81 state, int px)
    {
        if (state == null) return null;
        NetworkIconState es = state.EffectiveIconState;
        string sem;
        switch (es)
        {
            case NetworkIconState.Airplane:
                sem = "Network.Airplane";
                break;
            case NetworkIconState.Offline:
            case NetworkIconState.NotConnected:
            case NetworkIconState.CableUnplugged:
            case NetworkIconState.Disabled:
            case NetworkIconState.HardwareOff:
            case NetworkIconState.NetworkError:
                sem = state.Kind == NetKind.Ethernet ? "Ethernet.Disconnected" : "Wifi.Disconnected";
                break;
            case NetworkIconState.Scanning:
            case NetworkIconState.Identifying:
            case NetworkIconState.Connecting:
                sem = "Network.Searching";
                break;
            default:
                bool ok = es == NetworkIconState.Connected || state.Internet;
                if (state.Kind == NetKind.Ethernet)
                {
                    sem = ok ? "Ethernet.Connected" : "Ethernet.Limited";
                }
                else
                {
                    int b = Math.Clamp(state.Bars, 0, 5);
                    sem = (ok ? "Wifi.Bars" : "Wifi.Limited.Bars") + b;
                }
                break;
        }
        return GetAsset(sem, px);
    }

    // Canonical volume icon (SndVolSSO) for the current level/mute — used by the tray, the volume flyout, the OSD and the
    // Action Center so sound shows ONE authentic icon set + its level variants everywhere. Null => caller falls back.
    public static ImageSource VolumeImage(int pct, bool muted, int px)
    {
        string sem = VolumeSemantic(pct, muted);
        ImageSource img = GetAsset(sem, px);
        if (img == null) return null;
        // The authentic SndVolSSO art fills its canvas edge-to-edge (X:0..31), so in a tight host the speaker's left edge and
        // the waves get visually clipped ("cut"). Add transparent padding so it sits with margin. Keep the ORIGINAL colours
        // (e.g. the red mute prohibition sign on Volume.Muted) — do NOT recolour.
        return Padded(img, sem + "|pad", 0.12);
    }

    // The Volume.* semantic for a given level/mute — shared so callers (Action Center, OSD) map identically.
    public static string VolumeSemantic(int pct, bool muted)
    {
        return muted
            ? "Volume.Muted"
            : (pct <= 0 ? "Volume.Zero" : (pct < 34 ? "Volume.Low" : (pct < 67 ? "Volume.Medium" : "Volume.High")));
    }

    private static readonly Dictionary<string, ImageSource> _whiteCache = new Dictionary<string, ImageSource>(StringComparer.OrdinalIgnoreCase);

    // Recolours an authentic icon to FLAT WHITE (every non-transparent pixel -> white, alpha preserved) for the launcher's
    // dark surfaces, matching the authentic Win8.1 tray (flat white icons). Removes the dark outline that reads as faint/muddy
    // when downscaled. Cached + frozen; returns the source unchanged if it isn't a BitmapSource or on any failure.
    public static ImageSource WhiteTinted(ImageSource src, string cacheKey)
    {
        if (src is not BitmapSource bs) return src;
        lock (_gate)
        {
            if (_whiteCache.TryGetValue(cacheKey, out ImageSource c)) return c;
            ImageSource result = src;
            try
            {
                FormatConvertedBitmap fmt = new FormatConvertedBitmap(bs, PixelFormats.Bgra32, null, 0.0);
                int w = fmt.PixelWidth, h = fmt.PixelHeight, stride = w * 4;
                byte[] px = new byte[h * stride];
                fmt.CopyPixels(px, stride, 0);
                for (int i = 0; i < px.Length; i += 4)
                {
                    if (px[i + 3] != 0) { px[i] = 255; px[i + 1] = 255; px[i + 2] = 255; }
                }
                BitmapSource wb = BitmapSource.Create(w, h, 96.0, 96.0, PixelFormats.Bgra32, null, px, stride);
                wb.Freeze();
                result = wb;
            }
            catch
            {
            }
            _whiteCache[cacheKey] = result;
            return result;
        }
    }

    private static readonly Dictionary<string, ImageSource> _padCache = new Dictionary<string, ImageSource>(StringComparer.OrdinalIgnoreCase);

    // Returns a copy of an icon with transparent PADDING around it (frac of the larger side) so art that fills its canvas
    // edge-to-edge doesn't render visually clipped in a tight host. Colours untouched; cached + frozen.
    public static ImageSource Padded(ImageSource src, string cacheKey, double frac)
    {
        if (src is not BitmapSource bs || frac <= 0.0) return src;
        lock (_gate)
        {
            if (_padCache.TryGetValue(cacheKey, out ImageSource c)) return c;
            ImageSource result = src;
            try
            {
                // Thread-safe pixel copy (NO RenderTargetBitmap — that needs a UI thread and would throw when first called
                // from a background tray refresh, silently caching the un-padded icon so the flyout stayed "cut").
                FormatConvertedBitmap fmt = new FormatConvertedBitmap(bs, PixelFormats.Bgra32, null, 0.0);
                int w = fmt.PixelWidth, h = fmt.PixelHeight;
                int pad = (int)Math.Round(Math.Max(w, h) * frac);
                if (pad < 1) pad = 1;
                int nw = w + 2 * pad, nh = h + 2 * pad;
                int srcStride = w * 4, dstStride = nw * 4;
                byte[] srcPx = new byte[h * srcStride];
                fmt.CopyPixels(srcPx, srcStride, 0);
                byte[] dstPx = new byte[nh * dstStride];   // zero-filled = fully transparent border
                for (int y = 0; y < h; y++)
                {
                    Array.Copy(srcPx, y * srcStride, dstPx, (y + pad) * dstStride + pad * 4, srcStride);
                }
                BitmapSource padded = BitmapSource.Create(nw, nh, 96.0, 96.0, PixelFormats.Bgra32, null, dstPx, dstStride);
                padded.Freeze();
                result = padded;
            }
            catch
            {
            }
            _padCache[cacheKey] = result;
            return result;
        }
    }

    private static int BestSize(int[] sizes, int px)
    {
        if (sizes == null || sizes.Length == 0) return 0;
        if (px <= 0) px = 20;
        // Pick the native frame AUTHORED for this display size: the SMALLEST frame >= px, so the icon renders at or near
        // 1:1 with its own pixel-hinted art. This is how Windows keeps tray icons crisp — a 16/20px frame shown at ~16/20px,
        // NOT a 32/48px master downscaled into a 15px box (which turns fine detail like the Ethernet monitors / sound waves
        // into anti-aliased mush; that downscale was the "θολά tray icons" the user reported at 100% DPI). Cap at 64 so the
        // 256px masters are never decoded for these small surfaces.
        int pick = -1;
        foreach (int s in sizes)
        {
            if (s > 64) continue;
            if (s >= px && (pick < 0 || s < pick)) pick = s;   // smallest frame >= px
        }
        if (pick > 0) return pick;
        // none >= px: fall back to the largest available frame (<=64 if possible), upscaling a smaller hinted frame.
        foreach (int s in sizes)
        {
            if (s <= 64 && s > pick) pick = s;
        }
        if (pick > 0) return pick;
        foreach (int s in sizes)
        {
            if (s > pick) pick = s;
        }
        return pick;
    }

    private static ImageSource LoadPng(string file)
    {
        try
        {
            if (!File.Exists(file)) return null;
            BitmapImage bi = new BitmapImage();
            bi.BeginInit();
            bi.UriSource = new Uri(file);
            bi.CacheOption = BitmapCacheOption.OnLoad;
            bi.EndInit();
            bi.Freeze();
            return bi;
        }
        catch
        {
            return null;
        }
    }

    private static void EnsureLoaded()
    {
        if (_map != null) return;
        lock (_gate)
        {
            if (_map != null) return;
            Dictionary<string, Entry> map = new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);
            try
            {
                if (Directory.Exists(Root))
                {
                    foreach (string mf in Directory.GetFiles(Root, "*.manifest.json", SearchOption.AllDirectories))
                    {
                        try
                        {
                            using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(mf));
                            string dir = Path.GetDirectoryName(mf);
                            if (!doc.RootElement.TryGetProperty("assets", out JsonElement assets)) continue;
                            foreach (JsonElement a in assets.EnumerateArray())
                            {
                                if (!a.TryGetProperty("semantic", out JsonElement semEl)) continue;
                                string sem = semEl.GetString();
                                if (string.IsNullOrEmpty(sem)) continue;
                                string dll = a.TryGetProperty("sourceDll", out JsonElement d) ? d.GetString() : "";
                                string id = a.TryGetProperty("resId", out JsonElement r)
                                    ? (r.ValueKind == JsonValueKind.Number ? r.GetInt32().ToString() : r.GetString())
                                    : "";
                                int[] sizes = ParseSizes(a);
                                map[sem] = new Entry
                                {
                                    Dir = dir,
                                    Dll = Path.GetFileNameWithoutExtension(dll ?? ""),
                                    Id = id ?? "",
                                    Sizes = sizes
                                };
                            }
                        }
                        catch (Exception ex) { Logger.Log("Win81AssetResolver manifest " + Path.GetFileName(mf) + ": " + ex.Message); }
                    }
                }
            }
            catch (Exception ex) { Logger.Log("Win81AssetResolver load: " + ex.Message); }
            _map = map;
            Logger.Log("Win81AssetResolver: " + map.Count + " authentic assets from " + Root);
        }
    }

    private static int[] ParseSizes(JsonElement a)
    {
        try
        {
            if (a.TryGetProperty("sizes", out JsonElement s) && s.ValueKind == JsonValueKind.String)
            {
                List<int> list = new List<int>();
                foreach (string tok in (s.GetString() ?? "").Split(','))
                {
                    string t = tok.Trim();
                    int x = t.IndexOf('x');
                    if (x > 0) t = t.Substring(0, x);
                    if (int.TryParse(t, out int v) && !list.Contains(v)) list.Add(v);
                }
                return list.ToArray();
            }
        }
        catch { }
        return new[] { 16, 20, 24, 32 };
    }
}
