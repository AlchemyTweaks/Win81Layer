#nullable enable

using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media;

namespace Win81Layer;

// Tells the Metro Tab browser extension which shell look is on. The extension is loaded unpacked from a folder on disk,
// so a file placed there is served to its own pages; the launcher writes exactly one: shell-look.json (UTF-8, no BOM),
// for example {"schema":1,"look":"rest","ultraDark":true,"accent":"#44636A","changedUtc":"2026-10-17T09:30:00Z"}.
// look is the seasonal marker ("normal", "halloween", "christmas" or "rest"), ultraDark is true exactly while Dark rest
// is on, accent is the Start accent in effect (informational). The file is replaced atomically (a temporary file in the
// same folder, then a move) and only when look, ultraDark or accent changed, on a seasonal apply, a restore, startup and
// the live seasonal refresh. No timers and nothing at idle; nothing else is ever written into that folder, and no
// error reaches the caller.
internal static class ShellLookLink
{
    public const string FileName = "shell-look.json";

    // The extension's manifest name, used to find its folder.
    public const string ExtensionName = "Metro Tab";

    internal readonly record struct LookState(string Look, bool UltraDark, string Accent);

    internal enum Outcome
    {
        NoFolder,
        Unchanged,
        Written,
        Failed
    }

    private static readonly object Gate = new object();

    // The last folder lookup: its key (override and launcher folder) and its result.
    private static string? _cacheKey;

    private static string? _cachedFolder;

    // The pending sync (the latest state wins) and whether a runner is working on it.
    private static (string Override, LookState State, string Reason)? _pending;

    private static bool _running;

    private static bool _degradedLogged;

    // -1 not known yet, 0 no extension folder, 1 found.
    private static int _known = -1;

    private static int _loggedMissing;

    private static int _loggedFailure;

    // QA hooks, honoured only in read-only diagnostics.
    internal static bool QaFailBeforeMove { get; set; }

    internal static bool? QaKnownFolderFound { get; set; }

    // Whether the last sync found the extension folder (null before the first sync).
    internal static bool? KnownFolderFound
    {
        get
        {
            if (SettingsStore.ReadOnlyDiagnostics)
            {
                return QaKnownFolderFound;
            }
            int k = Volatile.Read(ref _known);
            return k < 0 ? null : k == 1;
        }
    }

    // The look the given settings describe.
    public static LookState Current(AppSettings s)
    {
        string id = SeasonalTheme.NormalizeId(s.SeasonalActive);
        Color accent;
        try
        {
            accent = string.IsNullOrWhiteSpace(s.StartAccentColor) ? StartAccent.Color() : (Color)ColorConverter.ConvertFromString(s.StartAccentColor);
        }
        catch
        {
            accent = StartAccent.Color();
        }
        return new LookState(id.Length == 0 ? SeasonalTheme.Normal : id, SeasonalTheme.IsRest(id), $"#{accent.R:X2}{accent.G:X2}{accent.B:X2}");
    }

    public static string? FindExtensionFolder(AppSettings s, string launcherDir)
    {
        return FindExtensionFolder(s.ShellLookExtensionDir ?? "", launcherDir);
    }

    // The extension folder: the settings override when one is set (and only when it exists; an explicit choice has no
    // fallback), otherwise <parent of the launcher folder>\ChromeExtension\<subfolder> whose manifest.json is named
    // "Metro Tab" (subfolders in ordinal order, the first match wins). Never creates a folder. A folder that was found
    // is remembered per override and launcher folder while it still exists; when nothing was found, or the folder went
    // away, the next sync looks again (one directory listing and a few small manifests, only on seasonal events), so an
    // extension that is unpacked or restored later is picked up without a restart.
    public static string? FindExtensionFolder(string overrideDir, string launcherDir)
    {
        string over = (overrideDir ?? "").Trim();
        string key = over + "|" + launcherDir;
        lock (Gate)
        {
            if (_cacheKey == key && _cachedFolder != null && Directory.Exists(_cachedFolder))
            {
                return _cachedFolder;
            }
        }
        string? found = Scan(over, launcherDir);
        lock (Gate)
        {
            _cacheKey = key;
            _cachedFolder = found;
        }
        return found;
    }

    // Forgets the cached folder lookup (QA, between test trees).
    internal static void ResetCache()
    {
        lock (Gate)
        {
            _cacheKey = null;
            _cachedFolder = null;
        }
    }

    private static string? Scan(string over, string launcherDir)
    {
        try
        {
            if (over.Length > 0)
            {
                return Directory.Exists(over) ? Path.GetFullPath(over) : null;
            }
            string trimmed = (launcherDir ?? "").TrimEnd('\\', '/');
            DirectoryInfo? parent = trimmed.Length == 0 ? null : Directory.GetParent(trimmed);
            if (parent == null)
            {
                return null;
            }
            string root = Path.Combine(parent.FullName, "ChromeExtension");
            if (!Directory.Exists(root))
            {
                return null;
            }
            foreach (string dir in Directory.GetDirectories(root).OrderBy(d => Path.GetFileName(d), StringComparer.Ordinal))
            {
                if (IsMetroTab(Path.Combine(dir, "manifest.json")))
                {
                    return dir;
                }
            }
        }
        catch
        {
        }
        return null;
    }

    private static bool IsMetroTab(string manifest)
    {
        try
        {
            FileInfo fi = new FileInfo(manifest);
            if (!fi.Exists || fi.Length > 256 * 1024)
            {
                return false;
            }
            using JsonDocument doc = JsonDocument.Parse(WithoutBom(File.ReadAllBytes(manifest)), new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
            return doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("name", out JsonElement name)
                && name.ValueKind == JsonValueKind.String
                && string.Equals(name.GetString(), ExtensionName, StringComparison.Ordinal);
        }
        catch
        {
            // a malformed manifest is not the Metro Tab extension
            return false;
        }
    }

    // The bytes after a leading UTF-8 byte order mark (Chrome accepts a manifest saved with one; the JSON reader does not).
    private static ReadOnlyMemory<byte> WithoutBom(byte[] bytes)
    {
        return bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF ? bytes.AsMemory(3) : bytes.AsMemory();
    }

    // How long the replace waits between attempts when the file is held open by a reader (the extension polls it while a
    // page is visible, and a virus scanner may open a new file for a moment). Background task only.
    private static readonly int[] MoveRetryDelays = new int[3] { 100, 250, 500 };

    // Writes shell-look.json into folder when what it holds differs from st (otherwise the file, its time stamp
    // included, is left alone). A replace that collides with a reader is tried again a few times before it counts as a
    // failure. Never throws.
    internal static Outcome WriteIfChanged(string folder, LookState st, DateTime utcNow)
    {
        string target = Path.Combine(folder, FileName);
        string tmp = Path.Combine(folder, FileName + ".tmp-" + Environment.ProcessId);
        try
        {
            if (!Directory.Exists(folder))
            {
                return Outcome.NoFolder;
            }
            if (TryRead(target, out LookState current) && current == st)
            {
                return Outcome.Unchanged;
            }
            File.WriteAllBytes(tmp, Serialize(st, utcNow));
            if (QaFailBeforeMove && SettingsStore.ReadOnlyDiagnostics)
            {
                throw new IOException("simulated failure before the move (QA)");
            }
            for (int attempt = 0; ; attempt++)
            {
                try
                {
                    File.Move(tmp, target, overwrite: true);
                    return Outcome.Written;
                }
                catch (Exception ex) when ((ex is IOException || ex is UnauthorizedAccessException) && attempt < MoveRetryDelays.Length)
                {
                    Thread.Sleep(MoveRetryDelays[attempt]);
                }
            }
        }
        catch (Exception ex)
        {
            try
            {
                if (File.Exists(tmp))
                {
                    File.Delete(tmp);
                }
            }
            catch
            {
            }
            if (Interlocked.Exchange(ref _loggedFailure, 1) == 0)
            {
                Logger.Log("Shell look: could not write " + FileName + ": " + ex.Message);
            }
            return Outcome.Failed;
        }
    }

    // UTF-8 without a byte order mark.
    internal static byte[] Serialize(LookState st, DateTime utcNow)
    {
        return JsonSerializer.SerializeToUtf8Bytes(new
        {
            schema = 1,
            look = st.Look,
            ultraDark = st.UltraDark,
            accent = st.Accent,
            changedUtc = utcNow.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture)
        });
    }

    // The state the file holds (schema 1 only); false when it is missing, too large or not readable.
    internal static bool TryRead(string path, out LookState state)
    {
        state = default;
        try
        {
            FileInfo fi = new FileInfo(path);
            if (!fi.Exists || fi.Length > 64 * 1024)
            {
                return false;
            }
            using JsonDocument doc = JsonDocument.Parse(WithoutBom(File.ReadAllBytes(path)));
            JsonElement r = doc.RootElement;
            if (r.ValueKind != JsonValueKind.Object || !r.TryGetProperty("schema", out JsonElement schema) || schema.ValueKind != JsonValueKind.Number || schema.GetInt32() != 1)
            {
                return false;
            }
            if (!r.TryGetProperty("look", out JsonElement look) || look.ValueKind != JsonValueKind.String)
            {
                return false;
            }
            if (!r.TryGetProperty("ultraDark", out JsonElement dark) || (dark.ValueKind != JsonValueKind.True && dark.ValueKind != JsonValueKind.False))
            {
                return false;
            }
            string accent = r.TryGetProperty("accent", out JsonElement a) && a.ValueKind == JsonValueKind.String ? a.GetString() ?? "" : "";
            state = new LookState(look.GetString() ?? "", dark.GetBoolean(), accent);
            return true;
        }
        catch
        {
            return false;
        }
    }

    // Brings the extension's file up to date with the current look: the state is read here (the caller's thread), the
    // folder lookup and the write run on one background task, and a newer state replaces one still waiting. Does
    // nothing in read-only diagnostics.
    public static void Sync(string reason)
    {
        if (SettingsStore.ReadOnlyDiagnostics)
        {
            return;
        }
        string over;
        LookState st;
        try
        {
            AppSettings s = SettingsStore.Load();
            if (SettingsStore.LoadWasDegraded)
            {
                // A settings read that cannot be trusted may not show the Dark rest marker: writing "normal" then would
                // switch the extension away from Ultra dark for nothing. The next sync after a healthy read writes it.
                if (!_degradedLogged)
                {
                    _degradedLogged = true;
                    Logger.Log("Shell look: sync skipped while the settings read is degraded");
                }
                return;
            }
            over = s.ShellLookExtensionDir ?? "";
            st = Current(s);
        }
        catch (Exception ex)
        {
            Logger.Log("Shell look: sync skipped: " + ex.Message);
            return;
        }
        lock (Gate)
        {
            _pending = (over, st, reason);
            if (_running)
            {
                return;
            }
            _running = true;
        }
        Task.Run(Runner);
    }

    private static void Runner()
    {
        string launcherDir = AppContext.BaseDirectory;
        while (true)
        {
            (string Override, LookState State, string Reason) job;
            lock (Gate)
            {
                if (_pending == null)
                {
                    _running = false;
                    return;
                }
                job = _pending.Value;
                _pending = null;
            }
            try
            {
                string? folder = FindExtensionFolder(job.Override, launcherDir);
                Volatile.Write(ref _known, folder != null ? 1 : 0);
                if (folder == null)
                {
                    if (Interlocked.Exchange(ref _loggedMissing, 1) == 0)
                    {
                        Logger.Log("Shell look: no Metro Tab extension folder found");
                    }
                    continue;
                }
                if (WriteIfChanged(folder, job.State, DateTime.UtcNow) == Outcome.Written)
                {
                    Logger.Log($"Shell look: wrote {FileName} (look={job.State.Look}, ultraDark={(job.State.UltraDark ? "true" : "false")}) [{job.Reason}]");
                }
            }
            catch (Exception ex)
            {
                Logger.Log("Shell look: sync failed: " + ex.Message);
            }
        }
    }
}
