using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Xml;
using System.Xml.Linq;
using Microsoft.Win32;

#nullable enable annotations

namespace Win81Layer;

// Builds the Windows 7 Start menu's system search index from what Windows itself ships, read-only:
// - Settings pages and settings: %windir%\ImmersiveControlPanel\Settings\AllSystemSettings_*.xml (the Settings app's
//   own search index), names and keywords resolved with SHLoadIndirectString in every index language, deep links from
//   the <PolicyIds> ms-settings names (with a built-in page table as the fallback);
// - Windows Security pages from the same files (windowsdefender:// routes);
// - Control Panel items: the All Control Panel Items folder, enumerated once per index language;
// - Control Panel tasks: the cpltasks XML inside shell32 (resource XML #21 of shell32.dll.mun), kept when the All Tasks
//   folder shows the task (Windows evaluates the conditions) and the target exists; plus the Settings index's own
//   Classic_ tasks that shell32 does not list (Adjust ClearType text);
// - Tools that have no Start menu shortcut (Disk Management, Device Manager's devmgmt.msc, gpedit, dxdiag, ...);
// - Greek (or other) names of the Start menu's own system shortcuts (desktop.ini LocalizedFileNames), so a Greek query
//   finds the English 'Services' program.
// Settings pages this PC does not have (PcCaps: hardware, SettingsPageVisibility policy) are left out. A build whose
// sources failed or timed out says so (IncompleteReason) and is not cached as a good one. Nothing is launched or written
// here; the caller caches the result and, on loading the cache, checks its launches with VerifyCached.
internal static class SystemSearchBuilder
{
    internal const string HostClassicControlPanel = "{7E0522FC-1AC4-41CA-AFD0-3610417A9C41}";

    internal const string HostClassicCommand = "{12B1697E-D3A0-4DBC-B568-CCF64A3F934D}";

    internal const string HostWindowsSecurity = "{6E6DDBCB-9C89-434B-A994-D5F22239523B}";

    internal const string AllControlPanelItems = "shell:::{21EC2020-3AEA-1069-A2DD-08002B30309D}";

    internal const string AllTasks = "shell:::{ED7BA470-8E54-465E-825C-99712043E01C}";

    internal const string ControlPanelIcon = "shell:::{26EE0668-A00A-44D7-9371-BEB064C98683}";

    internal static string SettingsDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "ImmersiveControlPanel", "Settings");

    internal static string Shell32Mun => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "SystemResources", "shell32.dll.mun");

    internal static string ControlExe => Path.Combine(Environment.SystemDirectory, "control.exe");

    // ---- Languages ----------------------------------------------------------------------------------------------------

    // The interface language first, then every other language of the user's profile whose Windows resources are
    // installed (a System32\<culture> MUI folder), at most three in all and one per base language.
    internal static (string Ui, string[] All) Languages()
    {
        string ui = CultureInfo.CurrentUICulture.Name;
        if (string.IsNullOrEmpty(ui))
        {
            ui = "en-US";
        }
        List<string> all = new List<string> { ui };
        try
        {
            using RegistryKey? k = Registry.CurrentUser.OpenSubKey(@"Control Panel\International\User Profile");
            if (k?.GetValue("Languages") is string[] langs)
            {
                foreach (string lang in langs)
                {
                    string? mui = MuiFolderFor(lang);
                    if (mui == null || all.Count >= 3)
                    {
                        continue;
                    }
                    string baseLang = mui.Split('-')[0];
                    if (all.Any((string a) => a.Split('-')[0].Equals(baseLang, StringComparison.OrdinalIgnoreCase)))
                    {
                        continue;
                    }
                    all.Add(mui);
                }
            }
        }
        catch
        {
        }
        return (ui, all.ToArray());
    }

    private static string? MuiFolderFor(string lang)
    {
        if (string.IsNullOrWhiteSpace(lang))
        {
            return null;
        }
        string sys = Environment.SystemDirectory;
        try
        {
            if (File.Exists(Path.Combine(sys, lang, "shell32.dll.mui")))
            {
                return lang;
            }
            string specific = CultureInfo.CreateSpecificCulture(lang).Name;
            if (!string.IsNullOrEmpty(specific) && File.Exists(Path.Combine(sys, specific, "shell32.dll.mui")))
            {
                return specific;
            }
            foreach (string dir in Directory.EnumerateDirectories(sys, lang + "-*"))
            {
                if (File.Exists(Path.Combine(dir, "shell32.dll.mui")))
                {
                    return Path.GetFileName(dir);
                }
            }
        }
        catch
        {
        }
        return null;
    }

    // What the cache key covers: the OS build, the languages, the size and time of every source file, and what changes
    // without an OS update: the registered Control Panel items, the Start menu's localized names (desktop.ini files) and
    // what decides which Settings pages this PC has (hardware, policy).
    internal static string SourceStamp(string ui, string[] langs)
    {
        return BaseStamp(ui, langs) + "|ext:" + ExtraFingerprint().ToString("x16", CultureInfo.InvariantCulture) + "|caps:" + PcCaps.Read().Stamp;
    }

    // Registry names of the installed Control Panel items (both views) and .cpl files, and every Start menu desktop.ini
    // with its time, hashed.
    private static ulong ExtraFingerprint()
    {
        ulong hash = 14695981039346656037UL;
        void Add(string s)
        {
            foreach (char c in s)
            {
                hash = (hash ^ c) * 1099511628211UL;
            }
            hash = (hash ^ 0xFFFF) * 1099511628211UL;
        }
        foreach (RegistryKey hive in new[] { Registry.LocalMachine, Registry.CurrentUser })
        {
            foreach (string path in new[] { @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\ControlPanel\NameSpace", @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Explorer\ControlPanel\NameSpace", @"SOFTWARE\Microsoft\Windows\CurrentVersion\Control Panel\Cpls" })
            {
                Add(path);
                try
                {
                    using RegistryKey? k = hive.OpenSubKey(path);
                    if (k == null)
                    {
                        continue;
                    }
                    IEnumerable<string> names = path.EndsWith("Cpls", StringComparison.Ordinal) ? k.GetValueNames() : k.GetSubKeyNames();
                    foreach (string n in names.Select((string x) => x.ToUpperInvariant()).OrderBy((string x) => x, StringComparer.Ordinal))
                    {
                        Add(n);
                    }
                }
                catch
                {
                }
            }
        }
        foreach (Environment.SpecialFolder sf in new[] { Environment.SpecialFolder.CommonPrograms, Environment.SpecialFolder.Programs })
        {
            try
            {
                string root = Environment.GetFolderPath(sf);
                if (string.IsNullOrEmpty(root) || !Directory.Exists(root))
                {
                    continue;
                }
                EnumerationOptions eo = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = 0 };
                foreach (string ini in Directory.EnumerateFiles(root, "desktop.ini", eo).OrderBy((string x) => x, StringComparer.OrdinalIgnoreCase))
                {
                    Add(ini.ToUpperInvariant());
                    Add(File.GetLastWriteTimeUtc(ini).Ticks.ToString(CultureInfo.InvariantCulture));
                }
            }
            catch
            {
            }
        }
        return hash;
    }

    private static string BaseStamp(string ui, string[] langs)
    {
        StringBuilder sb = new StringBuilder();
        try
        {
            using RegistryKey? k = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
            sb.Append(k?.GetValue("CurrentBuild")).Append('.').Append(k?.GetValue("UBR"));
        }
        catch
        {
        }
        sb.Append('|').Append(ui).Append('|').Append(string.Join(",", langs));
        try
        {
            foreach (string f in Directory.GetFiles(SettingsDir, "AllSystemSettings_*.xml").OrderBy((string x) => x, StringComparer.OrdinalIgnoreCase))
            {
                FileInfo fi = new FileInfo(f);
                sb.Append('|').Append(fi.Name).Append(':').Append(fi.Length).Append(':').Append(fi.LastWriteTimeUtc.Ticks);
            }
        }
        catch
        {
        }
        foreach (string f in new[] { Shell32Mun, Path.Combine(Environment.SystemDirectory, "shell32.dll") })
        {
            try
            {
                FileInfo fi = new FileInfo(f);
                if (fi.Exists)
                {
                    sb.Append('|').Append(fi.Name).Append(':').Append(fi.Length).Append(':').Append(fi.LastWriteTimeUtc.Ticks);
                }
            }
            catch
            {
            }
        }
        return sb.ToString();
    }

    // ---- Build --------------------------------------------------------------------------------------------------------

    // How long string resolution may take in all (SHLoadIndirectString can block, e.g. while AppX packages are serviced):
    // what is resolved by then is used, and the build counts as incomplete.
    internal const int StringsTimeoutMs = 30000;

    internal static SystemSearchData Build(string ui, string[] langs, int workers)
    {
        Stopwatch total = Stopwatch.StartNew();
        List<string> notes = new List<string>();
        List<string> incomplete = new List<string>();
        Stopwatch step = Stopwatch.StartNew();
        SystemSearchIndex.ResetCanonicalNames();

        // 1. Raw sources (no string resolution yet).
        List<RawSetting> settings = ReadSettings(out Dictionary<string, string> pageUris, out string settingsNote);
        notes.Add($"settings xml: {settingsNote} ({step.Elapsed.TotalMilliseconds:0} ms)");
        step.Restart();
        List<RawTask> tasks = ReadTasks(out string tasksNote);
        notes.Add($"cpltasks xml: {tasksNote} ({step.Elapsed.TotalMilliseconds:0} ms)");
        step.Restart();
        NamespaceResult ns = EnumerateNamespaces(langs, ui, 20000);
        notes.Add($"namespaces: {ns.Note} ({step.Elapsed.TotalMilliseconds:0} ms)");
        step.Restart();
        List<(string Lnk, string Ref)> localized = ReadLocalizedShortcuts();
        Dictionary<string, string> canonByClsid = CanonicalNames();

        // 2. Every indirect string, resolved once per language on worker threads.
        HashSet<string> refs = new HashSet<string>(StringComparer.Ordinal);
        foreach (RawSetting s in settings)
        {
            AddRef(refs, s.Desc);
            AddRefList(refs, s.HighKw);
            AddRefList(refs, s.Kw);
        }
        foreach (RawTask t in tasks)
        {
            AddRef(refs, t.Name);
            foreach (string k in t.Keywords)
            {
                AddRefList(refs, k);
            }
        }
        foreach (string clsid in ns.Items.Select((NsItem i) => i.Clsid))
        {
            AddRef(refs, ClsidString(clsid, "InfoTip"));
        }
        foreach ((string _, string r) in localized)
        {
            AddRef(refs, r);
        }
        List<string> refList = refs.ToList();
        Dictionary<string, string?>[] resolved = ResolveAll(refList, langs, workers, StringsTimeoutMs, out int failed, out bool stringsTimedOut);
        notes.Add($"strings: {refList.Count} refs x {langs.Length} languages [{string.Join(", ", langs)}], {failed} failed, {workers} workers{(stringsTimedOut ? ", TIMED OUT" : string.Empty)} ({step.Elapsed.TotalMilliseconds:0} ms)");
        step.Restart();
        // What makes a build incomplete: it is then used but not cached as a good one (and built again later).
        if (!ns.Completed)
        {
            incomplete.Add("Control Panel enumeration " + (ns.Note.Length > 0 ? ns.Note : "failed"));
        }
        else if (ns.Items.Count == 0)
        {
            incomplete.Add("no Control Panel items");
        }
        if (ns.Completed && !ns.TasksOk)
        {
            incomplete.Add("All Tasks folder empty");
        }
        if (settings.Count == 0)
        {
            incomplete.Add("no Settings entries (" + settingsNote + ")");
        }
        if (tasks.Count == 0)
        {
            incomplete.Add("no Control Panel tasks (" + tasksNote + ")");
        }
        if (stringsTimedOut)
        {
            incomplete.Add($"string resolution timed out after {StringsTimeoutMs} ms");
        }
        else if (refList.Count > 0 && failed > refList.Count * langs.Length / 5)
        {
            incomplete.Add($"{failed} of {refList.Count * langs.Length} strings failed");
        }
        string? R(int lang, string? src)
        {
            if (string.IsNullOrEmpty(src))
            {
                return null;
            }
            if (src[0] != '@')
            {
                return src;
            }
            return resolved[lang].TryGetValue(src, out string? v) ? v : null;
        }
        List<string> RList(int lang, string? src)
        {
            List<string> outp = new List<string>();
            if (string.IsNullOrEmpty(src))
            {
                return outp;
            }
            foreach (string piece in SplitRefList(src))
            {
                outp.AddRange(SplitKeywords(R(lang, piece)));
            }
            return outp;
        }

        List<SystemHit> hits = new List<SystemHit>();
        // 3. Control Panel items.
        Dictionary<string, SystemHit> itemsByClsid = new Dictionary<string, SystemHit>(StringComparer.OrdinalIgnoreCase);
        foreach (NsItem item in ns.Items)
        {
            if (string.IsNullOrWhiteSpace(item.Name) || item.Name.StartsWith("::", StringComparison.Ordinal))
            {
                continue;
            }
            canonByClsid.TryGetValue(item.Clsid, out string? canon);
            List<string> alt = new List<string>();
            List<string> kw = new List<string>();
            for (int l = 0; l < langs.Length; l++)
            {
                if (l > 0 && item.AltNames.TryGetValue(langs[l], out string? an) && !string.IsNullOrWhiteSpace(an) && !an.StartsWith("::", StringComparison.Ordinal))
                {
                    alt.Add(an);
                }
                string? tip = R(l, ClsidString(item.Clsid, "InfoTip"));
                if (!string.IsNullOrWhiteSpace(tip))
                {
                    kw.Add(tip);
                }
            }
            if (canon != null)
            {
                // 'Microsoft.DeviceManager' -> 'device manager': an English handle for every interface language.
                kw.Add(SplitCamel(canon.Substring(canon.LastIndexOf('.') + 1)));
            }
            SystemHit h = new SystemHit
            {
                Id = "cpl:" + item.Clsid,
                Kind = SystemHitKind.ControlPanelItem,
                Name = item.Name.Trim(),
                AltNames = Distinct(alt, item.Name),
                Keywords = kw.ToArray(),
                Launch = canon != null ? SystemLaunchKind.Command : SystemLaunchKind.Uri,
                Target = canon != null ? ControlExe : "shell:::" + item.Clsid,
                Args = canon != null ? "/name " + canon : null,
                Icon = "shell:::" + item.Clsid
            };
            hits.Add(h);
            itemsByClsid[item.Clsid] = h;
        }
        HashSet<string> itemNames = new HashSet<string>(itemsByClsid.Values.Select((SystemHit h) => SearchText.Fold(h.Name)), StringComparer.Ordinal);

        // 4. Control Panel tasks.
        int taskKept = 0, taskHidden = 0, taskDead = 0;
        HashSet<string> taskIds = new HashSet<string>(tasks.Select((RawTask t) => t.Id), StringComparer.OrdinalIgnoreCase);
        HashSet<string> taskNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (RawTask t in tasks)
        {
            bool visible = ns.TasksOk ? ns.TaskIds.Contains(t.Id) : ShCondition.All(t.Conditions);
            if (!visible)
            {
                taskHidden++;
                continue;
            }
            string? name = R(0, t.Name);
            if (string.IsNullOrWhiteSpace(name))
            {
                taskDead++;
                continue;
            }
            if (!TaskLaunch(t.ControlName, t.ControlPage, t.Command, out SystemLaunchKind launch, out string target, out string? args) || !SystemSearchIndex.TargetExists(launch, target, args, out _)
                || Retired(target))
            {
                taskDead++;
                continue;
            }
            if (itemNames.Contains(SearchText.Fold(name)))
            {
                continue;   // the same thing as a Control Panel item ('Device Manager')
            }
            List<string> alt = new List<string>();
            List<string> kw = new List<string>();
            for (int l = 0; l < langs.Length; l++)
            {
                if (l > 0)
                {
                    string? an = R(l, t.Name);
                    if (!string.IsNullOrWhiteSpace(an))
                    {
                        alt.Add(an);
                    }
                }
                foreach (string k in t.Keywords)
                {
                    kw.AddRange(RList(l, k));
                }
            }
            kw.AddRange(CommandKeywords(launch, target, args));
            itemsByClsid.TryGetValue(t.App, out SystemHit? app);
            hits.Add(new SystemHit
            {
                Id = "task:" + t.Id,
                Kind = SystemHitKind.ControlPanelTask,
                Name = name.Trim(),
                AltNames = Distinct(alt, name),
                Detail = app?.Name,
                Keywords = kw.Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
                Launch = launch,
                Target = target,
                Args = args,
                Icon = app != null ? app.Icon : (launch == SystemLaunchKind.Command && !IsHost(target) ? target : ControlPanelIcon),
                Elevate = t.Elevate && launch == SystemLaunchKind.Command && !IsHost(target),
                CanElevate = launch == SystemLaunchKind.Command && !IsHost(target)
            });
            taskNames.Add(SearchText.Fold(name));
            taskKept++;
        }
        notes.Add($"tasks: kept {taskKept}, hidden by Windows {taskHidden}, dead or nameless {taskDead}; all-tasks folder {(ns.TasksOk ? ns.TaskIds.Count + " items" : "unavailable (conditions evaluated instead)")}");

        // 5. Settings pages, settings and Windows Security.
        int setKept = 0, setNoUri = 0, setCond = 0, setFeature = 0, classicKept = 0, setRetired = 0, setAbsent = 0;
        List<string> classicNames = new List<string>();
        HashSet<string> absentPages = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        bool noMixedReality = OsBuild() >= 26100;
        PcCaps caps = PcCaps.Read();
        // A setting's page title: the page's own entry (AAA_<page>, then AAA_<page>-n), not a related-link page listed
        // under it (AAA_SettingsPageGpuPreferenceView sits under the display page and sorts first).
        Dictionary<string, (int Rank, string Title)> pageTitleRanks = new Dictionary<string, (int Rank, string Title)>(StringComparer.OrdinalIgnoreCase);
        foreach (RawSetting s in settings)
        {
            if (!s.Filename.StartsWith("AAA_SettingsPage", StringComparison.OrdinalIgnoreCase) || s.Page == null || R(0, s.Desc) is not string pt)
            {
                continue;
            }
            string own = "AAA_" + s.Page;
            int rank = s.Filename.Equals(own, StringComparison.OrdinalIgnoreCase) ? 0 : (s.Filename.StartsWith(own + "-", StringComparison.OrdinalIgnoreCase) ? 1 : 2);
            if (!pageTitleRanks.TryGetValue(s.Page, out (int Rank, string Title) cur) || rank < cur.Rank)
            {
                pageTitleRanks[s.Page] = (rank, pt);
            }
        }
        Dictionary<string, string> pageTitles = pageTitleRanks.ToDictionary((KeyValuePair<string, (int Rank, string Title)> kv) => kv.Key, (KeyValuePair<string, (int Rank, string Title)> kv) => kv.Value.Title, StringComparer.OrdinalIgnoreCase);
        HashSet<string> pairedFeatures = new HashSet<string>(settings.Where((RawSetting s) => !string.IsNullOrEmpty(s.Excl)).Select((RawSetting s) => s.Excl!), StringComparer.OrdinalIgnoreCase);
        foreach (RawSetting s in settings)
        {
            // Feature variants: the variant for a feature that is off is the one kept (an entry that only exists when a
            // feature is on may name a page this build does not have).
            if (!string.IsNullOrEmpty(s.Incl))
            {
                setFeature++;
                continue;
            }
            string hostId = (s.HostId ?? string.Empty).ToUpperInvariant();
            bool classicEntry = s.DeepLink != null && (hostId == HostClassicControlPanel || hostId == HostClassicCommand);
            if (!ShCondition.Evaluate(s.Condition, unknownMet: !classicEntry))
            {
                setCond++;
                continue;
            }
            string? name = R(0, s.Desc);
            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }
            List<string> alt = new List<string>();
            List<string> kw = new List<string>();
            for (int l = 0; l < langs.Length; l++)
            {
                if (l > 0)
                {
                    string? an = R(l, s.Desc);
                    if (!string.IsNullOrWhiteSpace(an))
                    {
                        alt.Add(an);
                    }
                }
                kw.AddRange(RList(l, s.HighKw));
                kw.AddRange(RList(l, s.Kw));
            }
            string host = (s.HostId ?? string.Empty).ToUpperInvariant();
            if (s.DeepLink != null && host == HostWindowsSecurity)
            {
                string route = s.DeepLink.Trim();
                hits.Add(new SystemHit
                {
                    Id = "sec:" + s.Filename,
                    Kind = SystemHitKind.Security,
                    Name = name.Trim(),
                    AltNames = Distinct(alt, name),
                    Detail = "Windows Security",
                    Keywords = kw.Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
                    Launch = SystemLaunchKind.Uri,
                    Target = route.Equals("None", StringComparison.OrdinalIgnoreCase) || route.Length == 0 ? "windowsdefender:" : "windowsdefender://" + route.ToLowerInvariant()
                });
                setKept++;
                continue;
            }
            if (s.DeepLink != null && (host == HostClassicControlPanel || host == HostClassicCommand))
            {
                // A classic task the Settings index lists and shell32 does not (Adjust ClearType text); the shell32 ones
                // and the items are already in.
                string id = s.SettingId ?? s.Filename.Replace("Classic_", string.Empty);
                if (taskIds.Contains(id) || itemNames.Contains(SearchText.Fold(name)) || Retired(s.DeepLink))
                {
                    continue;
                }
                if (!ClassicLaunch(host, s.DeepLink, out SystemLaunchKind launch, out string target, out string? args) || !SystemSearchIndex.TargetExists(launch, target, args, out _))
                {
                    continue;
                }
                if (!taskNames.Add(SearchText.Fold(name)))
                {
                    continue;
                }
                kw.AddRange(CommandKeywords(launch, target, args));
                hits.Add(new SystemHit
                {
                    Id = "task:" + id,
                    Kind = SystemHitKind.ControlPanelTask,
                    Name = name.Trim(),
                    AltNames = Distinct(alt, name),
                    Keywords = kw.Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
                    Launch = launch,
                    Target = target,
                    Args = args,
                    Icon = !string.IsNullOrEmpty(s.Icon) ? s.Icon : (launch == SystemLaunchKind.Command && !IsHost(target) ? target : ControlPanelIcon),
                    CanElevate = launch == SystemLaunchKind.Command && !IsHost(target)
                });
                classicKept++;
                classicNames.Add(name.Trim());
                continue;
            }
            if (s.DeepLink != null)
            {
                continue;
            }
            string? uri = PageUri(s.Page, pageUris) ?? PageGroupUri(s.PageGroup);
            if (uri == null)
            {
                setNoUri++;
                continue;
            }
            if (noMixedReality && uri.Contains("holographic", StringComparison.OrdinalIgnoreCase))
            {
                // Windows Mixed Reality is gone from 24H2 on: its pages no longer open.
                setRetired++;
                continue;
            }
            if (!caps.PageAvailable(uri))
            {
                // A page this PC does not have (Recall without an NPU, Touchpad without a touchpad) or that policy hides.
                setAbsent++;
                absentPages.Add(uri);
                continue;
            }
            bool page = s.Filename.StartsWith("AAA_SettingsPage", StringComparison.OrdinalIgnoreCase);
            string? detail = null;
            if (!page && s.Page != null && pageTitles.TryGetValue(s.Page, out string? title) && !string.Equals(title, name, StringComparison.OrdinalIgnoreCase))
            {
                detail = title;
            }
            hits.Add(new SystemHit
            {
                Id = "ms:" + s.Filename,
                Kind = page ? SystemHitKind.SettingsPage : SystemHitKind.Setting,
                Name = name.Trim(),
                AltNames = Distinct(alt, name),
                Detail = detail,
                Keywords = kw.Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
                Launch = SystemLaunchKind.Uri,
                Target = "ms-settings:" + uri
            });
            setKept++;
        }
        notes.Add($"settings: kept {setKept}, feature-on variants skipped {setFeature}, conditions false {setCond}, no deep link {setNoUri}, retired pages {setRetired}, pages this PC lacks {setAbsent} [{string.Join(", ", absentPages.OrderBy((string p) => p, StringComparer.Ordinal))}] ({caps}); classic-only tasks {classicKept} [{string.Join(", ", classicNames)}]");

        // 6. Tools without a Start menu shortcut, or with one (filtered against the inventory when searching).
        int tools = 0, merged = 0;
        foreach (ToolSeed t in ToolSeeds)
        {
            string path = Path.Combine(t.Windir ? Environment.GetFolderPath(Environment.SpecialFolder.Windows) : Environment.SystemDirectory, t.File);
            if (!File.Exists(path))
            {
                continue;
            }
            string stem = Path.GetFileNameWithoutExtension(t.File);
            string[] cmdKw = { stem, t.File };
            if (t.DupClsid != null && itemsByClsid.TryGetValue(t.DupClsid, out SystemHit? dup))
            {
                dup.Keywords = dup.Keywords.Concat(cmdKw).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                merged++;
                continue;
            }
            if (t.DupClsid != null && t.En == null)
            {
                continue;
            }
            string uiName = ToolName(t, ui, path);
            List<string> alt = new List<string>();
            foreach (string l in langs.Skip(1))
            {
                string? an = l.StartsWith("el", StringComparison.OrdinalIgnoreCase) ? t.El : (l.StartsWith("en", StringComparison.OrdinalIgnoreCase) ? t.En : MscTitle(path, l));
                if (!string.IsNullOrWhiteSpace(an))
                {
                    alt.Add(an!);
                }
            }
            hits.Add(new SystemHit
            {
                Id = "tool:" + t.File.ToLowerInvariant(),
                Kind = SystemHitKind.Tool,
                Name = uiName,
                AltNames = Distinct(alt, uiName),
                Detail = t.File,
                Keywords = cmdKw.Concat(t.Keywords).ToArray(),
                Launch = SystemLaunchKind.Command,
                Target = path,
                Icon = path,
                CanElevate = true
            });
            tools++;
        }
        notes.Add($"tools: {tools} (+{merged} command names merged into Control Panel items)");

        // 7. Localized names of the Start menu's system shortcuts: UI-language name -> names in the other languages.
        Dictionary<string, string[]> appAlt = new Dictionary<string, string[]>(StringComparer.Ordinal);
        foreach ((string lnk, string r) in localized)
        {
            string? uiName = R(0, r);
            if (string.IsNullOrWhiteSpace(uiName))
            {
                continue;
            }
            List<string> alt = new List<string>();
            for (int l = 1; l < langs.Length; l++)
            {
                string? an = R(l, r);
                if (!string.IsNullOrWhiteSpace(an) && !string.Equals(an, uiName, StringComparison.OrdinalIgnoreCase))
                {
                    alt.Add(an!);
                }
            }
            if (alt.Count > 0)
            {
                appAlt[SearchText.Fold(uiName)] = alt.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            }
        }
        notes.Add($"program alt names: {appAlt.Count}");

        // 8. One entry per name and target.
        int before = hits.Count;
        List<SystemHit> unique = new List<SystemHit>(hits.Count);
        HashSet<string> seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (SystemHit h in hits)
        {
            if (seen.Add(SearchText.Fold(h.Name) + "\u0001" + h.Target + "\u0001" + h.Args))
            {
                unique.Add(h);
            }
        }
        notes.Add($"total: {unique.Count} entries ({before - unique.Count} duplicates dropped) in {total.Elapsed.TotalMilliseconds:0} ms; {(incomplete.Count == 0 ? "complete" : "INCOMPLETE: " + string.Join("; ", incomplete))}");
        return new SystemSearchData
        {
            Schema = SystemSearchData.CurrentSchema,
            UiLanguage = ui,
            Languages = langs,
            Hits = unique,
            AppAltNames = appAlt,
            Notes = notes,
            BuiltUtc = DateTime.UtcNow,
            BuildMs = total.Elapsed.TotalMilliseconds,
            IncompleteReason = incomplete.Count == 0 ? null : string.Join("; ", incomplete)
        };
    }

    private static int OsBuild()
    {
        try
        {
            using RegistryKey? k = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
            return int.TryParse(k?.GetValue("CurrentBuildNumber") as string, NumberStyles.Integer, CultureInfo.InvariantCulture, out int b) ? b : Environment.OSVersion.Version.Build;
        }
        catch
        {
            return Environment.OSVersion.Version.Build;
        }
    }

    // The legacy troubleshooters (msdt.exe) are retired on current Windows 11 builds: their tasks are left out.
    private static bool Retired(string target)
    {
        return target.Contains("msdt.exe", StringComparison.OrdinalIgnoreCase);
    }

    private static string[] Distinct(List<string> alt, string name)
    {
        return alt.Select((string a) => a.Trim()).Where((string a) => a.Length > 0 && !string.Equals(a, name.Trim(), StringComparison.OrdinalIgnoreCase)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static string SplitCamel(string s)
    {
        StringBuilder sb = new StringBuilder(s.Length + 4);
        for (int i = 0; i < s.Length; i++)
        {
            if (i > 0 && char.IsUpper(s[i]) && !char.IsUpper(s[i - 1]))
            {
                sb.Append(' ');
            }
            sb.Append(s[i]);
        }
        return sb.ToString();
    }

    // ---- Settings XML -------------------------------------------------------------------------------------------------

    internal sealed class RawSetting
    {
        internal string Filename = string.Empty;
        internal string? Incl, Excl, DeepLink, HostId, Icon, Condition, Desc, HighKw, Kw, SettingId, Page, PageGroup;
    }

    // Settings XML files are chosen by content: the one with the most entries gives the entries, every file that has
    // <PolicyIds> adds to the page -> ms-settings map.
    private static List<RawSetting> ReadSettings(out Dictionary<string, string> pageUris, out string note)
    {
        pageUris = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        List<RawSetting> best = new List<RawSetting>();
        List<string> parts = new List<string>();
        string[] files;
        try
        {
            files = Directory.GetFiles(SettingsDir, "AllSystemSettings_*.xml");
        }
        catch (Exception ex)
        {
            note = "unreadable: " + ex.Message;
            return best;
        }
        foreach (string f in files)
        {
            List<RawSetting> list = new List<RawSetting>();
            int pol = 0;
            try
            {
                XmlReaderSettings xs = new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore, IgnoreComments = true, IgnoreWhitespace = true };
                using XmlReader r = XmlReader.Create(f, xs);
                r.MoveToContent();
                while (!r.EOF)
                {
                    if (r.NodeType == XmlNodeType.Element && r.LocalName == "SearchableContent")
                    {
                        XElement el = (XElement)XNode.ReadFrom(r);
                        list.Add(ParseSetting(el, pageUris, ref pol));
                    }
                    else
                    {
                        r.Read();
                    }
                }
            }
            catch (Exception ex)
            {
                parts.Add(Path.GetFileName(f) + " failed: " + ex.Message);
                continue;
            }
            parts.Add($"{Path.GetFileName(f).Replace("AllSystemSettings_", string.Empty)}={list.Count}{(pol > 0 ? " (policy ids " + pol + ")" : string.Empty)}");
            if (list.Count > best.Count)
            {
                best = list;
            }
        }
        note = string.Join(", ", parts) + $"; {pageUris.Count} pages with ms-settings names";
        return best;
    }

    private static RawSetting ParseSetting(XElement sc, Dictionary<string, string> pageUris, ref int pol)
    {
        RawSetting s = new RawSetting
        {
            Filename = (string?)sc.Element("Filename") ?? string.Empty,
            Incl = (string?)sc.Attribute("IncludeWithFeature"),
            Excl = (string?)sc.Attribute("ExcludeWithFeature")
        };
        XElement? ai = sc.Element("ApplicationInformation");
        s.DeepLink = (string?)ai?.Element("DeepLink");
        s.Icon = (string?)ai?.Element("Icon");
        XElement? si = sc.Element("SettingIdentity");
        if (si != null)
        {
            s.HostId = (string?)si.Element("HostID");
            s.Condition = (string?)si.Element("Condition");
            s.SettingId = (string?)si.Element("SettingID");
            string? classicPage = (string?)si.Element("PageID");
            foreach (XElement p in si.Elements("SettingPaths").Elements("Path"))
            {
                string? pid = (string?)p.Element("PageID");
                if (pid != null)
                {
                    s.Page ??= pid;
                    string? ids = (string?)p.Element("PolicyIds");
                    if (!string.IsNullOrWhiteSpace(ids))
                    {
                        pol++;
                        string? uri = PrimaryUri(pid, ids);
                        if (uri != null && !pageUris.ContainsKey(pid))
                        {
                            pageUris[pid] = uri;
                        }
                    }
                }
                foreach (XElement n in p.Elements("Node"))
                {
                    string type = (string?)n.Attribute("Type") ?? string.Empty;
                    if (type == "Page")
                    {
                        s.Page ??= n.Value;
                    }
                    else if (type == "PageGroup")
                    {
                        s.PageGroup ??= n.Value;
                    }
                }
            }
            if (s.Page == null && classicPage != null && classicPage.StartsWith("SettingsPage", StringComparison.OrdinalIgnoreCase))
            {
                s.Page = classicPage;
            }
        }
        XElement? inf = sc.Element("SettingInformation");
        s.Desc = (string?)inf?.Element("Description");
        s.HighKw = (string?)inf?.Element("HighKeywords");
        s.Kw = (string?)inf?.Element("Keywords");
        return s;
    }

    // The ms-settings name a page opens with: some pages list several names; the one most others extend wins
    // ("display" over "display-advanced"), with a few known exceptions.
    private static readonly Dictionary<string, string> UriOverride = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["SettingsPagePowerAndBattery"] = "powersleep",
        ["SettingsPageTimeRegionKeyboard"] = "typing",
        ["SettingsPageTimeRegionSpelling"] = "typing",
        ["SettingsPageTimeRegionLanguage"] = "regionlanguage",
        ["SettingsPageAudio"] = "sound",
        ["SettingsPageOtherUsers"] = "otherusers",
        ["SettingsPageStorageSenseStorageOverview"] = "storagesense",
        ["SettingsPageA9"] = "recall",
        ["SettingsPageSearchIndex"] = "cortana-windowssearch",
        ["SettingsPageRationalizedSearchSettings"] = "search",
        ["SettingsPageInstalledApps"] = "installed-apps",
        ["SettingsPageAppsDefaults"] = "defaultapps",
        ["SettingsPageNetworkManageAdapterOptions"] = "network-advancedsettings",
        ["SettingsPageAccountsSync"] = "backup",
        ["SettingsPagePasskeysManagement"] = "savedpasskeys",
    };

    internal static string? PrimaryUri(string page, string policyIds)
    {
        if (UriOverride.TryGetValue(page, out string? o))
        {
            return o;
        }
        string[] toks = policyIds.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (toks.Length == 0)
        {
            return null;
        }
        string best = toks[0];
        int bestScore = -1;
        foreach (string t in toks)
        {
            int sc = toks.Count((string x) => x.StartsWith(t + "-", StringComparison.OrdinalIgnoreCase));
            if (sc > bestScore)
            {
                best = t;
                bestScore = sc;
            }
        }
        return best;
    }

    // The page table as this build's <PolicyIds> give it (26200), for a later build whose files no longer carry them, plus
    // the pages that never had them.
    private const string BuiltInPages =
        "A9=recall;About=about;AccountsEmailApp=emailandaccounts;AccountsPicture=yourinfo;AccountsSync=backup;Activate=activation;" +
        "AdvancedApps=advanced-apps;AIComponents=aicomponents;AppActions=appactions;AppsDefaults=defaultapps;AppsForWebsites=appsforwebsites;" +
        "AppsNotifications=notifications;Audio=sound;Background=personalization-background;C2D=clicktodo;Clipboard=clipboard;" +
        "CloudPC=privacy-cloudpc;Colors=colors;Continuum=project;DeviceEncryption=deviceencryption;Devices=bluetooth;DevicesCameras=camera;" +
        "DevicesDL=personalization-lighting;DevicesKeyboard=devices-keyboard;DevicesMobileDevices=mobile-devices;DevicesPen=pen;" +
        "DevicesPrinters=printers;DevicesTouch=devices-touch;DevicesTouchpad=devices-touchpad;EaseOfAccessAudio=easeofaccess-audio;" +
        "EaseOfAccessClosedCaptioning=easeofaccess-closedcaptioning;EaseOfAccessColorFilter=easeofaccess-colorfilter;" +
        "EaseOfAccessEyeGaze=easeofaccess-eyecontrol;EaseOfAccessHearingAids=easeofaccess-hearingaids;" +
        "EaseOfAccessHighContrast=easeofaccess-highcontrast;EaseOfAccessKeyboard=easeofaccess-keyboard;" +
        "EaseOfAccessMagnifier=easeofaccess-magnifier;EaseOfAccessMouse=easeofaccess-mouse;EaseOfAccessMousePointer=easeofaccess-mousepointer;" +
        "EaseOfAccessNarrator=easeofaccess-narrator;EaseOfAccessScreenTint=easeofaccess-screentint;" +
        "EaseOfAccessSpeechRecognition=easeofaccess-speechrecognition;EaseOfAccessTextCursor=easeofaccess-cursor;" +
        "EaseOfAccessTextSize=easeofaccess-display;EaseOfAccessVisualEffects=easeofaccess-visualeffects;" +
        "EnvironmentPrivacy=privacy-holographic-environment;FindMyDevice=findmydevice;Focus=quiethours;Fonts=fonts;GameBar=gaming-gamebar;" +
        "GameDVR=gaming-gamedvr;GameMode=gaming-gamemode;GamingPosture=gaming-fullscreen;InstalledApps=installed-apps;LockScreen=lockscreen;" +
        "Maps=maps;MultiTasking=multitasking;NetworkAirplaneMode=network-airplanemode;NetworkDialup=network-dialup;" +
        "NetworkDirectAccess=network-directaccess;NetworkEthernet=network-ethernet;NetworkManageAdapterOptions=network-advancedsettings;" +
        "NetworkMobileBroadband=network-cellular;NetworkMobileHotspot=network-mobilehotspot;NetworkProxy=network-proxy;NetworkVPN=network-vpn;" +
        "NetworkWiFi=network-wifi;OptionalFeatures=optionalfeatures;OtherUsers=otherusers;PasskeysManagement=savedpasskeys;" +
        "PCSystemAutoPlay=autoplay;PCSystemDeviceSettings=mousetouchpad;PCSystemDisplay=display;" +
        "PersonalizationTextInput=personalization-textinput;PersonalizeCopilot=personalization-copilot;PowerAndBattery=powersleep;" +
        "PrivacyAccountInfo=privacy-accountinfo;PrivacyActivityHistory=privacy-activityhistory;PrivacyAppDiagnostics=privacy-appdiagnostics;" +
        "PrivacyAppRequestedDownloads=privacy-automaticfiledownloads;PrivacyBackgroundApps=privacy-backgroundapps;" +
        "PrivacyBroadFileSystemAccess=privacy-broadfilesystemaccess;PrivacyCalendar=privacy-calendar;PrivacyCallHistory=privacy-callhistory;" +
        "PrivacyContacts=privacy-contacts;PrivacyCustomPeripherals=privacy-customdevices;PrivacyDocuments=privacy-documents;" +
        "PrivacyDownloadsFolder=privacy-downloadsfolder;PrivacyEmail=privacy-email;PrivacyForegroundTextAccess=privacy-foregroundtextaccess;" +
        "PrivacyGaze=privacy-eyetracker;PrivacyGeneral=privacy-general;PrivacyGraphicsCaptureProgrammatic=privacy-graphicscaptureprogrammatic;" +
        "PrivacyGraphicsCaptureWithoutBorder=privacy-graphicscapturewithoutborder;PrivacyHumanPresence=privacy-presencesensing;" +
        "PrivacyLocation=privacy-location;PrivacyMessaging=privacy-messaging;PrivacyMicrophone=privacy-microphone;PrivacyMotionData=privacy-motion;" +
        "PrivacyMusicLibrary=privacy-musiclibrary;PrivacyNotifications=privacy-notifications;PrivacyPasskeys=privacy-passkeys;" +
        "PrivacyPersonalization=privacy-speechtyping;PrivacyPhoneCall=privacy-phonecalls;PrivacyPictures=privacy-pictures;" +
        "PrivacyRadios=privacy-radios;PrivacySIUFSettings=privacy-feedback;PrivacySpeech=privacy-speech;PrivacySystemAIModels=privacy-systemaimodels;" +
        "PrivacyTasks=privacy-tasks;PrivacyVideos=privacy-videos;PrivacyVoiceActivation=privacy-voiceactivation;PrivacyWebcam=privacy-webcam;" +
        "RadialController=wheel;RationalizedSearchSettings=search;RemoteDesktop=remotedesktop;RestoreMusUpdate=windowsupdate;" +
        "RestoreRestore=recovery;Resume=system-resume;SearchIndex=cortana-windowssearch;SearchPermissionsAndHistory=search-permissions;" +
        "SharedExperiences=crossdevice;SignInOptions=signinoptions;Speech=speech;Start=personalization-start;Startup=startupapps;" +
        "StorageSenseStorageOverview=storagesense;SystemComponents=systemcomponents;Taskbar=taskbar;Themes=themes;" +
        "TimeRegionDateTime=dateandtime;TimeRegionKeyboard=typing;TimeRegionLanguage=regionlanguage;TimeRegionSpelling=typing;" +
        "Troubleshoot=troubleshoot;Usb=usb;UserIntent=deviceusage;Video=videoplayback;WindowsDefender=windowsdefender;WorkAccess=workplace;" +
        "YourFamilyGroup=family-group;" +
        // Pages without <PolicyIds> in this build.
        "AdvancedSystemSettings=developers;PCSystemBluetooth=bluetooth;PCSystemDevices=connecteddevices;Flights=windowsinsider;" +
        "AccountsUsers=otherusers;NetworkWorkplace=workplace;RestoreOneBackup=backup;Start2=personalization-start";

    private static Dictionary<string, string>? s_builtInPages;

    internal static string? PageUri(string? page, Dictionary<string, string> fromXml)
    {
        if (string.IsNullOrEmpty(page))
        {
            return null;
        }
        if (fromXml.TryGetValue(page, out string? u))
        {
            return u;
        }
        s_builtInPages ??= BuiltInPages.Split(';', StringSplitOptions.RemoveEmptyEntries)
            .Select((string p) => p.Split('=', 2))
            .Where((string[] p) => p.Length == 2)
            .GroupBy((string[] p) => "SettingsPage" + p[0], StringComparer.OrdinalIgnoreCase)
            .ToDictionary((IGrouping<string, string[]> g) => g.Key, (IGrouping<string, string[]> g) => g.First()[1], StringComparer.OrdinalIgnoreCase);
        return s_builtInPages.TryGetValue(page, out string? b) ? b : null;
    }

    private static string? PageGroupUri(string? group)
    {
        if (string.IsNullOrEmpty(group))
        {
            return null;
        }
        if (group.Contains("Network", StringComparison.OrdinalIgnoreCase))
        {
            return "network-status";
        }
        if (group.Contains("Privacy", StringComparison.OrdinalIgnoreCase) || group.Contains("Security", StringComparison.OrdinalIgnoreCase))
        {
            return "privacy";
        }
        return null;
    }

    // ---- Control Panel tasks ------------------------------------------------------------------------------------------

    internal sealed class RawTask
    {
        internal string Id = string.Empty;
        internal string App = string.Empty;
        internal string? Name, ControlName, ControlPage, Command;
        internal bool Elevate;
        internal readonly List<string> Keywords = new List<string>();
        internal readonly List<string> Conditions = new List<string>();
    }

    private static List<RawTask> ReadTasks(out string note)
    {
        List<RawTask> tasks = new List<RawTask>();
        string? xml = FindTasksXml(out string where);
        if (xml == null)
        {
            note = "not found";
            return tasks;
        }
        try
        {
            XDocument doc = XDocument.Parse(xml);
            XNamespace sh = "http://schemas.microsoft.com/windows/tasks/v1";
            XNamespace sh2 = "http://schemas.microsoft.com/windows/tasks/v2";
            int apps = 0;
            foreach (XElement a in doc.Root!.Elements().Where((XElement e) => e.Name.LocalName == "application"))
            {
                apps++;
                string app = (string?)a.Attribute("id") ?? string.Empty;
                foreach (XElement t in a.Elements(sh + "task"))
                {
                    RawTask rt = new RawTask
                    {
                        Id = ((string?)t.Attribute("id") ?? string.Empty).ToUpperInvariant(),
                        App = app.ToUpperInvariant(),
                        Name = (string?)t.Element(sh + "name"),
                        Elevate = string.Equals((string?)t.Attribute("needsElevation"), "true", StringComparison.OrdinalIgnoreCase),
                        Command = (string?)t.Element(sh + "command")
                    };
                    XElement? cp = t.Element(sh2 + "controlpanel");
                    if (cp != null)
                    {
                        rt.ControlName = (string?)cp.Attribute("name");
                        rt.ControlPage = (string?)cp.Attribute("page");
                    }
                    foreach (XElement k in t.Elements(sh + "keywords"))
                    {
                        rt.Keywords.Add(k.Value);
                    }
                    foreach (XElement c in t.Elements(sh + "conditions").Elements(sh + "condition"))
                    {
                        // Turned into the Settings index's own condition form: name;invert;param;value...
                        StringBuilder sb = new StringBuilder((string?)c.Attribute("name") ?? string.Empty);
                        sb.Append(';').Append(string.Equals((string?)c.Attribute("invert"), "true", StringComparison.OrdinalIgnoreCase) ? "1" : "0");
                        foreach (XElement p in c.Elements(sh + "parameter"))
                        {
                            sb.Append(';').Append((string?)p.Attribute("name")).Append(';').Append((string?)p.Attribute("value"));
                        }
                        rt.Conditions.Add(sb.ToString());
                    }
                    if (rt.Id.Length > 0)
                    {
                        tasks.Add(rt);
                    }
                }
            }
            note = $"{where}: {apps} applications, {tasks.Count} tasks";
        }
        catch (Exception ex)
        {
            note = where + " parse failed: " + ex.Message;
        }
        return tasks;
    }

    // controlpanel name/page -> control.exe /name X /page Y; a command -> its program and arguments; shell: paths and
    // addresses -> opened through the shell. False when there is nothing to launch.
    internal static bool TaskLaunch(string? controlName, string? controlPage, string? command, out SystemLaunchKind kind, out string target, out string? args)
    {
        kind = SystemLaunchKind.Command;
        target = string.Empty;
        args = null;
        if (!string.IsNullOrWhiteSpace(controlName))
        {
            target = ControlExe;
            args = "/name " + controlName.Trim() + (string.IsNullOrWhiteSpace(controlPage) ? string.Empty : " /page " + controlPage.Trim());
            return true;
        }
        if (string.IsNullOrWhiteSpace(command))
        {
            return false;
        }
        string c = command.Trim();
        if (c.StartsWith("shell:", StringComparison.OrdinalIgnoreCase) || Regex.IsMatch(c, @"^[a-zA-Z][a-zA-Z0-9+.\-]{2,}:(?!\\)"))
        {
            kind = SystemLaunchKind.Uri;
            target = c;
            return true;
        }
        if (!SplitCommand(c, out string exe, out string rest))
        {
            return false;
        }
        target = exe;
        args = rest.Length > 0 ? rest : null;
        return true;
    }

    // A classic entry of the Settings index: "Microsoft.Name\page" for the Control Panel host, a command line otherwise.
    internal static bool ClassicLaunch(string host, string deepLink, out SystemLaunchKind kind, out string target, out string? args)
    {
        if (string.Equals(host, HostClassicControlPanel, StringComparison.OrdinalIgnoreCase))
        {
            string[] parts = deepLink.Split('\\', 2);
            return TaskLaunch(parts[0], parts.Length > 1 ? parts[1] : null, null, out kind, out target, out args);
        }
        return TaskLaunch(null, null, deepLink, out kind, out target, out args);
    }

    // CreateProcess-style: a quoted program, or the shortest prefix ending at a space that names an existing file.
    internal static bool SplitCommand(string command, out string exe, out string rest)
    {
        string c = Environment.ExpandEnvironmentVariables(command).Trim();
        exe = string.Empty;
        rest = string.Empty;
        if (c.Length == 0)
        {
            return false;
        }
        if (c[0] == '"')
        {
            int q = c.IndexOf('"', 1);
            if (q < 0)
            {
                return false;
            }
            exe = c.Substring(1, q - 1);
            rest = c.Substring(q + 1).Trim();
        }
        else
        {
            int sp = c.IndexOf(' ');
            while (true)
            {
                string cand = sp < 0 ? c : c.Substring(0, sp);
                string? found = ExistingProgram(cand);
                if (found != null)
                {
                    exe = found;
                    rest = sp < 0 ? string.Empty : c.Substring(sp + 1).Trim();
                    break;
                }
                if (sp < 0)
                {
                    int first = c.IndexOf(' ');
                    exe = first < 0 ? c : c.Substring(0, first);
                    rest = first < 0 ? string.Empty : c.Substring(first + 1).Trim();
                    break;
                }
                sp = c.IndexOf(' ', sp + 1);
            }
        }
        if (!Path.IsPathRooted(exe))
        {
            exe = ExistingProgram(exe) ?? exe;
        }
        return exe.Length > 0;
    }

    private static string? ExistingProgram(string cand)
    {
        try
        {
            if (Path.IsPathRooted(cand))
            {
                if (File.Exists(cand))
                {
                    return cand;
                }
                return File.Exists(cand + ".exe") ? cand + ".exe" : null;
            }
            foreach (string dir in new[] { Environment.SystemDirectory, Environment.GetFolderPath(Environment.SpecialFolder.Windows) })
            {
                string p = Path.Combine(dir, cand);
                if (File.Exists(p))
                {
                    return p;
                }
                if (File.Exists(p + ".exe"))
                {
                    return p + ".exe";
                }
            }
        }
        catch
        {
        }
        return null;
    }

    private static readonly HashSet<string> Hosts = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "control", "rundll32", "mmc", "explorer", "cmd", "shell32", "dllhost", "msdt", "powershell", "wscript", "cscript", "conhost"
    };

    // control.exe and rundll32 hosts: their own icon and elevation mean nothing for the task they run.
    internal static bool IsHost(string target)
    {
        return Hosts.Contains(Path.GetFileNameWithoutExtension(target)) || target.EndsWith("control.exe", StringComparison.OrdinalIgnoreCase);
    }

    // The program and file names a typed command can find a task by: 'diskmgmt', 'dfrgui', 'ncpa.cpl' (hosts excluded).
    private static IEnumerable<string> CommandKeywords(SystemLaunchKind kind, string target, string? args)
    {
        if (kind != SystemLaunchKind.Command)
        {
            yield break;
        }
        if (!IsHost(target))
        {
            yield return Path.GetFileNameWithoutExtension(target);
        }
        if (string.IsNullOrEmpty(args))
        {
            yield break;
        }
        foreach (Match m in Regex.Matches(args, @"([A-Za-z0-9_\-]+)\.(msc|cpl)\b", RegexOptions.IgnoreCase))
        {
            yield return m.Groups[1].Value;
            yield return m.Value;
        }
    }

    private static string? FindTasksXml(out string where)
    {
        where = "none";
        foreach (string module in new[] { Shell32Mun, Path.Combine(Environment.SystemDirectory, "shell32.dll") })
        {
            if (!File.Exists(module))
            {
                continue;
            }
            nint h = Native.LoadLibraryExW(module, IntPtr.Zero, Native.LOAD_LIBRARY_AS_DATAFILE | Native.LOAD_LIBRARY_AS_IMAGE_RESOURCE);
            if (h == IntPtr.Zero)
            {
                continue;
            }
            try
            {
                string? direct = ReadResource(h, (nint)21);
                if (direct != null && direct.Contains("cpltasks", StringComparison.Ordinal) && direct.Contains("<applications", StringComparison.Ordinal))
                {
                    where = Path.GetFileName(module) + " XML #21";
                    return direct;
                }
                // A later build may number it differently: the largest XML resource that is the task list.
                string? best = null;
                Native.EnumResourceNamesW(h, "XML", delegate (nint hm, nint type, nint name, nint lp)
                {
                    string? s = ReadResource(hm, name);
                    if (s != null && s.Contains("<applications", StringComparison.Ordinal) && s.Contains("cpltasks", StringComparison.Ordinal) && (best == null || s.Length > best.Length))
                    {
                        best = s;
                    }
                    return true;
                }, IntPtr.Zero);
                if (best != null)
                {
                    where = Path.GetFileName(module) + " XML (scanned)";
                    return best;
                }
            }
            catch
            {
            }
            finally
            {
                Native.FreeLibrary(h);
            }
        }
        return null;
    }

    private static string? ReadResource(nint module, nint name)
    {
        nint res = Native.FindResourceW(module, name, "XML");
        if (res == IntPtr.Zero)
        {
            return null;
        }
        uint size = Native.SizeofResource(module, res);
        nint data = Native.LockResource(Native.LoadResource(module, res));
        if (size < 16 || data == IntPtr.Zero)
        {
            return null;
        }
        byte[] bytes = new byte[size];
        Marshal.Copy(data, bytes, 0, (int)size);
        return Encoding.UTF8.GetString(bytes).TrimStart('\uFEFF');
    }

    // ---- Shell namespaces ---------------------------------------------------------------------------------------------

    internal sealed class NsItem
    {
        internal string Clsid = string.Empty;
        internal string Name = string.Empty;
        internal readonly Dictionary<string, string> AltNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    }

    internal sealed class NamespaceResult
    {
        internal readonly List<NsItem> Items = new List<NsItem>();
        internal readonly HashSet<string> TaskIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        internal bool TasksOk;
        // Every folder was enumerated in time and without an error.
        internal bool Completed;
        internal string Note = string.Empty;
    }

    // All Control Panel Items (in each language: the thread's preferred UI language picks the names) and the All Tasks
    // folder (the GUIDs of the tasks Windows shows), on a dedicated STA thread. A shell extension that hangs only costs
    // the timeout: the index is then built without what was not enumerated.
    private static NamespaceResult EnumerateNamespaces(string[] langs, string ui, int timeoutMs)
    {
        NamespaceResult result = new NamespaceResult();
        NamespaceResult work = new NamespaceResult();
        List<string> notes = new List<string>();
        Thread t = new Thread(delegate ()
        {
            try
            {
                Stopwatch sw = Stopwatch.StartNew();
                foreach ((string name, string parse) in EnumerateFolder(AllControlPanelItems))
                {
                    string clsid = parse.StartsWith("::", StringComparison.Ordinal) ? parse.Substring(2) : parse;
                    if (!clsid.StartsWith("{", StringComparison.Ordinal))
                    {
                        // Fonts reports a name as its parsing name.
                        continue;
                    }
                    work.Items.Add(new NsItem { Clsid = clsid.ToUpperInvariant(), Name = name });
                }
                notes.Add($"control panel items {work.Items.Count} ({sw.Elapsed.TotalMilliseconds:0} ms)");
                foreach (string lang in langs.Skip(1))
                {
                    sw.Restart();
                    Native.SetLanguage(lang);
                    try
                    {
                        Dictionary<string, string> byClsid = EnumerateFolder(AllControlPanelItems)
                            .Where(((string Name, string Parse) x) => x.Parse.StartsWith("::{", StringComparison.Ordinal))
                            .GroupBy(((string Name, string Parse) x) => x.Parse.Substring(2).ToUpperInvariant())
                            .ToDictionary((IGrouping<string, (string Name, string Parse)> g) => g.Key, (IGrouping<string, (string Name, string Parse)> g) => g.First().Name);
                        foreach (NsItem it in work.Items)
                        {
                            if (byClsid.TryGetValue(it.Clsid, out string? n))
                            {
                                it.AltNames[lang] = n;
                            }
                        }
                    }
                    finally
                    {
                        Native.SetLanguage(null);
                    }
                    notes.Add($"{lang} names ({sw.Elapsed.TotalMilliseconds:0} ms)");
                }
                sw.Restart();
                foreach ((string _, string parse) in EnumerateFolder(AllTasks))
                {
                    string id = parse.Trim();
                    int slash = id.LastIndexOf('\\');
                    if (slash >= 0)
                    {
                        id = id.Substring(slash + 1);
                    }
                    if (id.StartsWith("{", StringComparison.Ordinal))
                    {
                        work.TaskIds.Add(id.ToUpperInvariant());
                    }
                }
                work.TasksOk = work.TaskIds.Count > 0;
                notes.Add($"all tasks {work.TaskIds.Count} ({sw.Elapsed.TotalMilliseconds:0} ms)");
                work.Completed = true;
            }
            catch (Exception ex)
            {
                notes.Add("failed: " + ex.GetType().Name + " " + ex.Message);
            }
        })
        {
            IsBackground = true,
            Name = "Win7 Start system index (shell)",
            Priority = ThreadPriority.BelowNormal
        };
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        if (!t.Join(timeoutMs))
        {
            result.Note = "timed out after " + timeoutMs + " ms";
            return result;
        }
        result.Items.AddRange(work.Items);
        result.TaskIds.UnionWith(work.TaskIds);
        result.TasksOk = work.TasksOk;
        result.Completed = work.Completed;
        result.Note = string.Join(", ", notes);
        return result;
    }

    // (display name, parent-relative parsing name) of every child of a shell folder. Each item is released here.
    private static List<(string Name, string Parse)> EnumerateFolder(string path)
    {
        List<(string Name, string Parse)> list = new List<(string Name, string Parse)>();
        Guid iidItem = typeof(Native.IShellItem).GUID;
        Native.SHCreateItemFromParsingName(path, IntPtr.Zero, ref iidItem, out Native.IShellItem folder);
        object? enumObj = null;
        try
        {
            Guid bhid = Native.BHID_EnumItems;
            Guid iidEnum = typeof(Native.IEnumShellItems).GUID;
            folder.BindToHandler(IntPtr.Zero, ref bhid, ref iidEnum, out enumObj);
            Native.IEnumShellItems en = (Native.IEnumShellItems)enumObj;
            while (en.Next(1, out Native.IShellItem item, out uint got) == 0 && got == 1)
            {
                try
                {
                    item.GetDisplayName(Native.SIGDN_NORMALDISPLAY, out nint pn);
                    string name = Native.TakeString(pn) ?? string.Empty;
                    string parse = string.Empty;
                    try
                    {
                        item.GetDisplayName(Native.SIGDN_PARENTRELATIVEPARSING, out nint pp);
                        parse = Native.TakeString(pp) ?? string.Empty;
                    }
                    catch
                    {
                    }
                    list.Add((name, parse));
                }
                catch
                {
                }
                finally
                {
                    Marshal.ReleaseComObject(item);
                }
            }
        }
        finally
        {
            if (enumObj != null && Marshal.IsComObject(enumObj))
            {
                Marshal.ReleaseComObject(enumObj);
            }
            Marshal.ReleaseComObject(folder);
        }
        return list;
    }

    // Canonical names of the registered Control Panel items, by CLSID (control.exe /name).
    internal static Dictionary<string, string> CanonicalNames()
    {
        Dictionary<string, string> map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (RegistryKey hive in new[] { Registry.LocalMachine, Registry.CurrentUser })
        {
            try
            {
                using RegistryKey? ns = hive.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\ControlPanel\NameSpace");
                if (ns == null)
                {
                    continue;
                }
                foreach (string clsid in ns.GetSubKeyNames())
                {
                    string? canon = ClsidString(clsid, "System.ApplicationName");
                    if (!string.IsNullOrWhiteSpace(canon))
                    {
                        map[clsid.ToUpperInvariant()] = canon;
                    }
                }
            }
            catch
            {
            }
        }
        return map;
    }

    private static string? ClsidString(string clsid, string value)
    {
        try
        {
            using RegistryKey? k = Registry.ClassesRoot.OpenSubKey(@"CLSID\" + clsid);
            if (k?.GetValue(value, null, RegistryValueOptions.DoNotExpandEnvironmentNames) is string s && s.Length > 0)
            {
                // "@shell32.dll,-123#immutable1": the suffix is not part of the reference.
                int hash = s.IndexOf("#immutable", StringComparison.OrdinalIgnoreCase);
                return hash > 0 ? s.Substring(0, hash) : s;
            }
        }
        catch
        {
        }
        return null;
    }

    // ---- Start menu shortcuts with localized names ---------------------------------------------------------------------

    private static List<(string Lnk, string Ref)> ReadLocalizedShortcuts()
    {
        List<(string Lnk, string Ref)> list = new List<(string Lnk, string Ref)>();
        foreach (Environment.SpecialFolder sf in new[] { Environment.SpecialFolder.CommonPrograms, Environment.SpecialFolder.Programs })
        {
            string root;
            try
            {
                root = Environment.GetFolderPath(sf);
            }
            catch
            {
                continue;
            }
            if (string.IsNullOrEmpty(root) || !Directory.Exists(root))
            {
                continue;
            }
            IEnumerable<string> dirs;
            try
            {
                dirs = new[] { root }.Concat(Directory.EnumerateDirectories(root, "*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = 0 }));
            }
            catch
            {
                continue;
            }
            foreach (string dir in dirs)
            {
                string ini = Path.Combine(dir, "desktop.ini");
                if (!File.Exists(ini))
                {
                    continue;
                }
                try
                {
                    bool inSection = false;
                    foreach (string raw in File.ReadLines(ini))
                    {
                        string l = raw.Trim();
                        if (l.StartsWith("[", StringComparison.Ordinal))
                        {
                            inSection = l.Equals("[LocalizedFileNames]", StringComparison.OrdinalIgnoreCase);
                            continue;
                        }
                        int eq = l.IndexOf('=');
                        if (!inSection || eq <= 0)
                        {
                            continue;
                        }
                        string file = l.Substring(0, eq).Trim();
                        string r = l.Substring(eq + 1).Trim();
                        if (r.StartsWith("@", StringComparison.Ordinal) && file.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase) && File.Exists(Path.Combine(dir, file)))
                        {
                            list.Add((Path.Combine(dir, file), r));
                        }
                    }
                }
                catch
                {
                }
            }
        }
        return list;
    }

    // ---- Tools -------------------------------------------------------------------------------------------------------

    internal sealed record ToolSeed(string File, string? En, string? El, string? DupClsid = null, bool Windir = false, params string[] Keywords);

    // System tools a typed name or command should find. Those with a Start menu shortcut are hidden while the inventory
    // has them (the program row wins); DupClsid: the Control Panel item that is the same thing, which then only takes the
    // command names as keywords.
    internal static readonly ToolSeed[] ToolSeeds =
    {
        new ToolSeed("devmgmt.msc", "Device Manager", "Διαχείριση Συσκευών", "{74246BFC-4C96-11D0-ABEF-0020AF6B0B7A}", false, "drivers"),
        new ToolSeed("diskmgmt.msc", "Disk Management", "Διαχείριση δίσκων", null, false, "partition", "volume", "format"),
        new ToolSeed("gpedit.msc", "Local Group Policy Editor", "Πρόγραμμα επεξεργασίας πολιτικής τοπικής ομάδας", null, false, "group policy"),
        new ToolSeed("lusrmgr.msc", "Local Users and Groups", "Τοπικοί χρήστες και ομάδες"),
        new ToolSeed("certmgr.msc", "Certificates - Current User", "Πιστοποιητικά - Τρέχων χρήστης", null, false, "certificates"),
        new ToolSeed("fsmgmt.msc", "Shared Folders", "Κοινόχρηστοι φάκελοι"),
        new ToolSeed("tpm.msc", "Trusted Platform Module (TPM) Management", "Διαχείριση μονάδας αξιόπιστης πλατφόρμας (TPM)", null, false, "tpm"),
        new ToolSeed("wmimgmt.msc", "WMI Control", "Έλεγχος WMI"),
        new ToolSeed("secpol.msc", "Local Security Policy", "Τοπική πολιτική ασφαλείας"),
        new ToolSeed("compmgmt.msc", "Computer Management", "Διαχείριση υπολογιστή"),
        new ToolSeed("services.msc", "Services", "Υπηρεσίες"),
        new ToolSeed("eventvwr.msc", "Event Viewer", "Προβολή συμβάντων", null, false, "logs"),
        new ToolSeed("taskschd.msc", "Task Scheduler", "Χρονοδιάγραμμα εργασιών"),
        new ToolSeed("perfmon.msc", "Performance Monitor", "Εποπτεία επιδόσεων"),
        new ToolSeed("comexp.msc", "Component Services", "Υπηρεσίες στοιχείων"),
        new ToolSeed("printmanagement.msc", "Print Management", "Διαχείριση εκτυπώσεων"),
        new ToolSeed("wf.msc", "Windows Defender Firewall with Advanced Security", "Τείχος προστασίας του Windows Defender με εξελιγμένη ασφάλεια"),
        new ToolSeed("dxdiag.exe", "DirectX Diagnostic Tool", "Εργαλείο διαγνωστικών DirectX", null, false, "directx"),
        new ToolSeed("winver.exe", "About Windows", "Πληροφορίες για τα Windows", null, false, "version"),
        new ToolSeed("msinfo32.exe", "System Information", "Πληροφορίες συστήματος"),
        new ToolSeed("regedit.exe", "Registry Editor", "Επεξεργαστής Μητρώου", null, true, "registry"),
        new ToolSeed("msconfig.exe", "System Configuration", "Παράμετροι συστήματος"),
        new ToolSeed("cleanmgr.exe", "Disk Cleanup", "Εκκαθάριση Δίσκου"),
        new ToolSeed("taskmgr.exe", "Task Manager", "Διαχείριση Εργασιών"),
        new ToolSeed("mstsc.exe", "Remote Desktop Connection", "Σύνδεση απομακρυσμένης επιφάνειας εργασίας"),
        new ToolSeed("resmon.exe", "Resource Monitor", "Εποπτεία πόρων"),
        new ToolSeed("sndvol.exe", "Volume Mixer", "Μείκτης έντασης ήχου", null, false, "volume"),
        new ToolSeed("rstrui.exe", "System Restore", "Επαναφορά Συστήματος", null, false, "restore point"),
        new ToolSeed("dfrgui.exe", "Defragment and Optimize Drives", "Ανασυγκρότηση και βελτιστοποίηση μονάδων δίσκων", null, false, "defrag"),
        new ToolSeed("mdsched.exe", "Windows Memory Diagnostic", "Διαγνωστικά μνήμης των Windows", null, false, "memory"),
        new ToolSeed("optionalfeatures.exe", "Windows Features", "Δυνατότητες των Windows"),
        new ToolSeed("sysdm.cpl", "System Properties", "Ιδιότητες συστήματος"),
        new ToolSeed("ncpa.cpl", "Network Connections", "Συνδέσεις δικτύου", null, false, "adapter"),
        new ToolSeed("joy.cpl", "Game Controllers", "Ελεγκτές παιχνιδιών", null, false, "gamepad", "joystick"),
        new ToolSeed("appwiz.cpl", null, null, "{7B81BE6A-CE2B-4676-A29E-EB907A5126C5}"),
        new ToolSeed("main.cpl", null, null, "{6C8EEC18-8D75-41B2-A177-8831D59D2D50}"),
        new ToolSeed("mmsys.cpl", null, null, "{F2DDFC82-8F12-4CDD-B7DC-D4FE1425AA4D}"),
        new ToolSeed("inetcpl.cpl", null, null, "{A3DD4F92-658A-410F-84FD-6FBBBEF2FFFE}"),
        new ToolSeed("powercfg.cpl", null, null, "{025A5937-A6BE-4686-A844-36FE4BEC8B6D}"),
        new ToolSeed("timedate.cpl", null, null, "{E2E7934B-DCE5-43C4-9576-7FE4F75E7480}"),
        new ToolSeed("firewall.cpl", null, null, "{4026492F-2F69-46B8-B9BF-5654FC07E423}"),
        new ToolSeed("intl.cpl", null, null, "{62D8ED13-C9D0-4CE8-A914-47DD628FB1B0}"),
    };

    private static string ToolName(ToolSeed t, string ui, string path)
    {
        if (ui.StartsWith("el", StringComparison.OrdinalIgnoreCase) && t.El != null)
        {
            return t.El;
        }
        if (ui.StartsWith("en", StringComparison.OrdinalIgnoreCase) && t.En != null)
        {
            return t.En;
        }
        return MscTitle(path, ui) ?? t.En ?? Path.GetFileNameWithoutExtension(t.File);
    }

    // The console title a localized copy of an .msc file carries (System32\<culture>\x.msc), without "(Local)".
    private static string? MscTitle(string path, string lang)
    {
        if (!path.EndsWith(".msc", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }
        try
        {
            string loc = Path.Combine(Path.GetDirectoryName(path)!, lang, Path.GetFileName(path));
            if (!File.Exists(loc))
            {
                return null;
            }
            string text = File.ReadAllText(loc);
            foreach (Match m in Regex.Matches(text, "<String ID=\"(\\d+)\" Refs=\"\\d+\">([^<]*)</String>").OrderBy((Match x) => int.Parse(x.Groups[1].Value, CultureInfo.InvariantCulture)))
            {
                string v = System.Net.WebUtility.HtmlDecode(m.Groups[2].Value).Trim();
                if (v.Length > 0 && !v.Equals("Console Root", StringComparison.OrdinalIgnoreCase) && !v.Equals("Favorites", StringComparison.OrdinalIgnoreCase))
                {
                    return Regex.Replace(v, @"\s*\([^)]*\)\s*$", string.Empty);
                }
            }
        }
        catch
        {
        }
        return null;
    }

    // ---- Cache check -------------------------------------------------------------------------------------------------

    private static readonly Regex GuidRx = new Regex(@"^\{[0-9A-Fa-f]{8}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{12}\}$", RegexOptions.CultureInvariant);

    private static readonly Regex SettingsUriRx = new Regex(@"^ms-settings:[a-z0-9][a-z0-9_.\-]*$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    private static readonly Regex DefenderUriRx = new Regex(@"^windowsdefender:(//[a-z0-9_.\-/]*)?$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    // control.exe can only open a registered Control Panel item (and one of its pages).
    private static readonly Regex ControlArgsRx = new Regex(@"^/name [A-Za-z0-9.]+( /page [A-Za-z0-9_.:{}?=\-]+)?$", RegexOptions.CultureInvariant);

    // The disk cache is plain JSON that any program of the user can rewrite, and its launches run (some elevated) on a
    // click. Every entry must be one this builder produces from Windows' own sources: tasks as shell32's task list (or the
    // Settings index) gives them, with elevation only where Windows marks it; tools as the tool table places them;
    // Control Panel items by their registered canonical names; pages as ms-settings: / windowsdefender: names. Nothing is
    // started. False (with the first entry that fails) discards the cache.
    internal static bool VerifyCached(SystemSearchData data, out string why)
    {
        Dictionary<string, RawTask>? tasks = null;
        Dictionary<string, (string Host, string Link)>? classic = null;
        Dictionary<string, string>? canon = null;
        foreach (SystemHit h in data.Hits)
        {
            string? bad;
            try
            {
                bad = CheckCached(h, ref tasks, ref classic, ref canon);
            }
            catch (Exception ex)
            {
                bad = "check failed: " + ex.Message;
            }
            if (bad != null)
            {
                why = h.Id + ": " + bad;
                return false;
            }
        }
        why = data.Hits.Count + " entries check out";
        return true;
    }

    private static string? CheckCached(SystemHit h, ref Dictionary<string, RawTask>? tasks, ref Dictionary<string, (string Host, string Link)>? classic, ref Dictionary<string, string>? canon)
    {
        string id = h.Id ?? string.Empty;
        bool noArgs = string.IsNullOrEmpty(h.Args);
        if (id.StartsWith("ms:", StringComparison.Ordinal))
        {
            bool ok = (h.Kind == SystemHitKind.SettingsPage || h.Kind == SystemHitKind.Setting) && h.Launch == SystemLaunchKind.Uri && SettingsUriRx.IsMatch(h.Target ?? string.Empty) && noArgs && !h.Elevate && !h.CanElevate;
            return ok ? null : "not a Settings page";
        }
        if (id.StartsWith("sec:", StringComparison.Ordinal))
        {
            bool ok = h.Kind == SystemHitKind.Security && h.Launch == SystemLaunchKind.Uri && DefenderUriRx.IsMatch(h.Target ?? string.Empty) && noArgs && !h.Elevate && !h.CanElevate;
            return ok ? null : "not a Windows Security page";
        }
        if (id.StartsWith("cpl:", StringComparison.Ordinal))
        {
            string clsid = id.Substring(4);
            if (h.Kind != SystemHitKind.ControlPanelItem || !GuidRx.IsMatch(clsid) || h.Elevate || h.CanElevate)
            {
                return "not a Control Panel item";
            }
            if (h.Launch == SystemLaunchKind.Uri)
            {
                return string.Equals(h.Target, "shell:::" + clsid, StringComparison.OrdinalIgnoreCase) && noArgs ? null : "Control Panel item address differs";
            }
            canon ??= CanonicalNames();
            return canon.TryGetValue(clsid, out string? c) && string.Equals(h.Target, ControlExe, StringComparison.OrdinalIgnoreCase) && string.Equals(h.Args, "/name " + c, StringComparison.Ordinal)
                ? null
                : "Control Panel item launch differs from its registration";
        }
        if (id.StartsWith("tool:", StringComparison.Ordinal))
        {
            ToolSeed? seed = ToolSeeds.FirstOrDefault((ToolSeed t) => string.Equals("tool:" + t.File.ToLowerInvariant(), id, StringComparison.Ordinal));
            if (seed == null || h.Kind != SystemHitKind.Tool || h.Launch != SystemLaunchKind.Command || !noArgs || h.Elevate)
            {
                return "not a known tool";
            }
            string path = Path.Combine(seed.Windir ? Environment.GetFolderPath(Environment.SpecialFolder.Windows) : Environment.SystemDirectory, seed.File);
            return string.Equals(h.Target, path, StringComparison.OrdinalIgnoreCase) ? null : "tool path differs";
        }
        if (id.StartsWith("task:", StringComparison.Ordinal) && h.Kind == SystemHitKind.ControlPanelTask)
        {
            string tid = id.Substring(5);
            tasks ??= ReadTasks(out _).GroupBy((RawTask t) => t.Id, StringComparer.OrdinalIgnoreCase).ToDictionary((IGrouping<string, RawTask> g) => g.Key, (IGrouping<string, RawTask> g) => g.First(), StringComparer.OrdinalIgnoreCase);
            SystemLaunchKind kind;
            string target;
            string? args;
            bool elevate = false;
            if (tasks.TryGetValue(tid, out RawTask? rt))
            {
                if (!TaskLaunch(rt.ControlName, rt.ControlPage, rt.Command, out kind, out target, out args))
                {
                    return "shell32 task without a launch";
                }
                elevate = rt.Elevate;
            }
            else if (h.Launch == SystemLaunchKind.Command && string.Equals(h.Target, ControlExe, StringComparison.OrdinalIgnoreCase) && (noArgs || ControlArgsRx.IsMatch(h.Args!)) && !h.Elevate && !h.CanElevate)
            {
                return null;   // a classic task that opens a Control Panel item
            }
            else
            {
                if (classic == null)
                {
                    classic = new Dictionary<string, (string Host, string Link)>(StringComparer.OrdinalIgnoreCase);
                    foreach (RawSetting s in ReadSettings(out _, out _))
                    {
                        string host = (s.HostId ?? string.Empty).ToUpperInvariant();
                        if (s.DeepLink != null && (host == HostClassicControlPanel || host == HostClassicCommand))
                        {
                            classic.TryAdd(s.SettingId ?? s.Filename.Replace("Classic_", string.Empty), (host, s.DeepLink));
                        }
                    }
                }
                if (!classic.TryGetValue(tid, out (string Host, string Link) link) || !ClassicLaunch(link.Host, link.Link, out kind, out target, out args))
                {
                    return "unknown task";
                }
            }
            bool program = kind == SystemLaunchKind.Command && !IsHost(target);
            bool same = kind == h.Launch && string.Equals(target, h.Target, StringComparison.OrdinalIgnoreCase) && string.Equals(args ?? string.Empty, h.Args ?? string.Empty, StringComparison.Ordinal)
                && h.Elevate == (elevate && program) && h.CanElevate == program;
            return same ? null : "task launch differs from Windows' own";
        }
        return "unknown entry";
    }

    // ---- Indirect strings -------------------------------------------------------------------------------------------

    private static void AddRef(HashSet<string> refs, string? s)
    {
        if (!string.IsNullOrEmpty(s) && s[0] == '@')
        {
            refs.Add(s);
        }
    }

    private static void AddRefList(HashSet<string> refs, string? s)
    {
        if (string.IsNullOrEmpty(s))
        {
            return;
        }
        foreach (string piece in SplitRefList(s))
        {
            AddRef(refs, piece);
        }
    }

    // "@@shell32.dll,-1@shell32.dll,-2" -> "@shell32.dll,-1", "@shell32.dll,-2"; anything else is one reference.
    private static IEnumerable<string> SplitRefList(string s)
    {
        if (s.StartsWith("@@", StringComparison.Ordinal))
        {
            foreach (string piece in s.Substring(1).Split('@', StringSplitOptions.RemoveEmptyEntries))
            {
                yield return "@" + piece;
            }
        }
        else
        {
            yield return s;
        }
    }

    // Keyword lists are ';'-separated; some translations use '.' or ',' instead.
    private static IEnumerable<string> SplitKeywords(string? s)
    {
        if (string.IsNullOrWhiteSpace(s))
        {
            yield break;
        }
        char[] seps = s.IndexOf(';') >= 0 ? new[] { ';', '\n' } : new[] { ';', '.', ',', '\u060C', '\n' };
        foreach (string p in s.Split(seps, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (p.Length > 0)
            {
                yield return p;
            }
        }
    }

    // Resolves every reference in every language on worker threads, each with its thread's preferred UI language set
    // (SHLoadIndirectString then loads that language's MUI or PRI resources). Returns one map per language. The workers
    // share one deadline: a call that blocks costs at most timeoutMs, what was resolved by then is returned (timedOut),
    // and the blocked background thread is left to finish on its own.
    private static Dictionary<string, string?>[] ResolveAll(List<string> refs, string[] langs, int workers, int timeoutMs, out int failed, out bool timedOut)
    {
        timedOut = false;
        if (refs.Count == 0)
        {
            failed = 0;
            return langs.Select((string _) => new Dictionary<string, string?>(StringComparer.Ordinal)).ToArray();
        }
        int perLang = Math.Max(1, workers / Math.Max(1, langs.Length));
        ConcurrentDictionary<string, string?>[] live = langs.Select((string _) => new ConcurrentDictionary<string, string?>(perLang, refs.Count, StringComparer.Ordinal)).ToArray();
        List<Thread> threads = new List<Thread>();
        for (int l = 0; l < langs.Length; l++)
        {
            for (int w = 0; w < perLang; w++)
            {
                int lang = l;
                int slice = w;
                Thread t = new Thread(delegate ()
                {
                    Native.SetLanguage(langs[lang]);
                    try
                    {
                        StringBuilder sb = new StringBuilder(8192);
                        for (int i = slice; i < refs.Count; i += perLang)
                        {
                            live[lang][refs[i]] = Native.LoadIndirect(refs[i], sb);
                        }
                    }
                    catch
                    {
                    }
                    finally
                    {
                        Native.SetLanguage(null);
                    }
                })
                {
                    IsBackground = true,
                    Priority = ThreadPriority.BelowNormal,
                    Name = "Win7 Start system index (strings " + langs[l] + ")"
                };
                t.SetApartmentState(ApartmentState.MTA);
                threads.Add(t);
            }
        }
        foreach (Thread t in threads)
        {
            t.Start();
        }
        Stopwatch sw = Stopwatch.StartNew();
        foreach (Thread t in threads)
        {
            int left = timeoutMs - (int)sw.ElapsedMilliseconds;
            if (left <= 0 || !t.Join(left))
            {
                timedOut = true;
                break;
            }
        }
        Dictionary<string, string?>[] maps = new Dictionary<string, string?>[langs.Length];
        int fails = 0;
        for (int l = 0; l < langs.Length; l++)
        {
            maps[l] = new Dictionary<string, string?>(live[l], StringComparer.Ordinal);
            // Unresolved and not reached both count as failed.
            fails += refs.Count - maps[l].Count((KeyValuePair<string, string?> kv) => kv.Value != null);
        }
        failed = fails;
        return maps;
    }

    // ---- Conditions (shcond://v1#Name;invert;param;value...) ---------------------------------------------------------

    internal static class ShCondition
    {
        internal static bool All(IEnumerable<string> conditions)
        {
            foreach (string c in conditions)
            {
                if (!Evaluate(c))
                {
                    return false;
                }
            }
            return true;
        }

        // Unknown conditions count as met, unless unknownMet is false (a classic task Windows hides behind a condition
        // this code cannot read stays hidden).
        internal static bool Evaluate(string? cond, bool unknownMet = true)
        {
            if (string.IsNullOrWhiteSpace(cond))
            {
                return true;
            }
            try
            {
                string body = cond.Trim();
                int hash = body.IndexOf('#');
                if (hash >= 0)
                {
                    body = body.Substring(hash + 1);
                }
                string[] parts = body.Split(';');
                string name = parts[0];
                bool invert = parts.Length > 1 && parts[1] == "1";
                Dictionary<string, string> p = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                for (int i = 2; i + 1 < parts.Length; i += 2)
                {
                    p[parts[i]] = parts[i + 1];
                }
                bool? v = name.ToLowerInvariant() switch
                {
                    "isserver" => IsServer(),
                    "regkeyexists" => p.TryGetValue("Regkey", out string? k1) ? RegKeyExists(k1) : null,
                    "regvalexists" => p.TryGetValue("Regkey", out string? k2) && p.TryGetValue("Regval", out string? v2) ? RegValue(k2, v2) != null : null,
                    "regvalequals" => p.TryGetValue("Regkey", out string? k3) && p.TryGetValue("Regval", out string? v3) && p.TryGetValue("RegvalExpected", out string? e3)
                        ? string.Equals(RegValue(k3, v3)?.ToString(), e3, StringComparison.OrdinalIgnoreCase)
                        : null,
                    "ismachineondomain" => IsOnDomain(),
                    "ismobilepc" or "ismobilitycenterenabled" => IsMobilePc(),
                    "istabletpc" => Native.GetSystemMetrics(86) != 0,                // SM_TABLETPC
                    "ispenavailable" => (Native.GetSystemMetrics(94) & 0x0C) != 0,   // SM_DIGITIZER: integrated or external pen
                    "istouchavailable" => (Native.GetSystemMetrics(94) & 0x03) != 0, // SM_DIGITIZER: integrated or external touch
                    _ => null
                };
                if (v == null)
                {
                    return unknownMet;
                }
                return invert ? !v.Value : v.Value;
            }
            catch
            {
                return unknownMet;
            }
        }

        private static bool? IsMobilePc()
        {
            try
            {
                uint role = Native.PowerDeterminePlatformRoleEx(2);   // POWER_PLATFORM_ROLE_V2
                return role == 2 || role == 8;                         // PlatformRoleMobile, PlatformRoleSlate
            }
            catch
            {
                return null;
            }
        }

        private static bool IsServer()
        {
            try
            {
                using RegistryKey? k = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\ProductOptions");
                string? t = k?.GetValue("ProductType") as string;
                return t != null && !t.Equals("WinNT", StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }

        private static (RegistryKey? Root, string Sub) Split(string key)
        {
            int slash = key.IndexOf('\\');
            string hive = slash < 0 ? key : key.Substring(0, slash);
            string sub = slash < 0 ? string.Empty : key.Substring(slash + 1);
            RegistryKey? root = hive.ToUpperInvariant() switch
            {
                "HKEY_LOCAL_MACHINE" or "HKLM" => Registry.LocalMachine,
                "HKEY_CURRENT_USER" or "HKCU" => Registry.CurrentUser,
                "HKEY_CLASSES_ROOT" or "HKCR" => Registry.ClassesRoot,
                "HKEY_USERS" => Registry.Users,
                _ => null
            };
            return (root, sub);
        }

        private static bool RegKeyExists(string key)
        {
            (RegistryKey? root, string sub) = Split(key);
            if (root == null)
            {
                return true;
            }
            using RegistryKey? k = root.OpenSubKey(sub);
            return k != null;
        }

        private static object? RegValue(string key, string value)
        {
            (RegistryKey? root, string sub) = Split(key);
            if (root == null)
            {
                return null;
            }
            using RegistryKey? k = root.OpenSubKey(sub);
            return k?.GetValue(value);
        }

        private static bool? IsOnDomain()
        {
            try
            {
                if (Native.NetGetJoinInformation(null, out nint name, out int status) == 0)
                {
                    Native.NetApiBufferFree(name);
                    return status == 3;   // NetSetupDomainName
                }
            }
            catch
            {
            }
            return null;
        }
    }

    // ---- Pages this PC has ---------------------------------------------------------------------------------------------

    // The Settings index lists pages for hardware a PC may not have, mostly without a <Condition>; the Settings app hides
    // them, and so does this index: Touchpad without a precision touchpad, Touch without a touch screen, Wheel without a
    // radial controller, Recall and Click to Do without an NPU. The SettingsPageVisibility policy (showonly: / hide:) is
    // honoured too. Stamp goes into the cache key.
    internal sealed class PcCaps
    {
        internal bool Touchpad;
        internal bool Touch;
        internal bool Wheel;
        internal bool Npu;
        internal string Policy = string.Empty;
        private HashSet<string>? _showOnly;
        private readonly HashSet<string> _hide = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        internal static PcCaps Read()
        {
            PcCaps c = new PcCaps();
            try
            {
                (c.Touchpad, c.Wheel) = HidCollections();
            }
            catch
            {
            }
            try
            {
                c.Touch = (Native.GetSystemMetrics(94) & 0x03) != 0;   // SM_DIGITIZER: integrated or external touch
            }
            catch
            {
            }
            c.Npu = NpuPresent();
            c.Policy = PagePolicy();
            string p = c.Policy.ToLowerInvariant();
            if (p.StartsWith("showonly:", StringComparison.Ordinal))
            {
                c._showOnly = new HashSet<string>(PolicyPages(p.Substring(9)), StringComparer.OrdinalIgnoreCase);
            }
            else if (p.StartsWith("hide:", StringComparison.Ordinal))
            {
                c._hide.UnionWith(PolicyPages(p.Substring(5)));
            }
            return c;
        }

        // uri: the ms-settings name without the scheme.
        internal bool PageAvailable(string uri)
        {
            if ((_showOnly != null && !_showOnly.Contains(uri)) || _hide.Contains(uri))
            {
                return false;
            }
            return uri.ToLowerInvariant() switch
            {
                "devices-touchpad" => Touchpad,
                "devices-touch" => Touch,
                "wheel" => Wheel,
                "recall" or "clicktodo" => Npu,
                _ => true
            };
        }

        internal string Stamp => (Touchpad ? "p" : "-") + (Touch ? "t" : "-") + (Wheel ? "w" : "-") + (Npu ? "n" : "-") + (Policy.Length > 0 ? ":" + Policy : string.Empty);

        public override string ToString()
        {
            return $"touchpad={Touchpad} touch={Touch} wheel={Wheel} npu={Npu}" + (Policy.Length > 0 ? " policy=" + Policy : string.Empty);
        }

        private static IEnumerable<string> PolicyPages(string list)
        {
            foreach (string part in list.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                yield return part.StartsWith("ms-settings:", StringComparison.OrdinalIgnoreCase) ? part.Substring(12) : part;
            }
        }

        // HID top-level collections: a precision touchpad (Digitizer page 0x0D, Touch Pad 0x05) and a radial controller
        // (Generic Desktop 0x01, System Multi-Axis Controller 0x0E).
        private static (bool Touchpad, bool Wheel) HidCollections()
        {
            uint size = (uint)Marshal.SizeOf<Native.RAWINPUTDEVICELIST>();
            uint count = 0;
            if (Native.GetRawInputDeviceList(null, ref count, size) == uint.MaxValue || count == 0)
            {
                return (false, false);
            }
            count += 8;
            Native.RAWINPUTDEVICELIST[] list = new Native.RAWINPUTDEVICELIST[count];
            uint got = Native.GetRawInputDeviceList(list, ref count, size);
            if (got == uint.MaxValue)
            {
                return (false, false);
            }
            bool touchpad = false, wheel = false;
            for (int i = 0; i < got && i < list.Length; i++)
            {
                if (list[i].dwType != Native.RIM_TYPEHID)
                {
                    continue;
                }
                Native.RID_DEVICE_INFO info = new Native.RID_DEVICE_INFO { cbSize = (uint)Marshal.SizeOf<Native.RID_DEVICE_INFO>() };
                uint cb = info.cbSize;
                if (Native.GetRawInputDeviceInfo(list[i].hDevice, Native.RIDI_DEVICEINFO, ref info, ref cb) == uint.MaxValue)
                {
                    continue;
                }
                touchpad |= info.usUsagePage == 0x0D && info.usUsage == 0x05;
                wheel |= info.usUsagePage == 0x01 && info.usUsage == 0x0E;
            }
            return (touchpad, wheel);
        }

        // A device of the Neural processors class (ComputeAccelerator).
        private static bool NpuPresent()
        {
            try
            {
                using RegistryKey? k = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Class\{f01a9d53-3ff6-48d2-9f97-c8a7004be10c}");
                return k != null && k.GetSubKeyNames().Any((string n) => n.Length == 4 && n.All(char.IsAsciiDigit));
            }
            catch
            {
                return false;
            }
        }

        private static string PagePolicy()
        {
            foreach (RegistryKey hive in new[] { Registry.LocalMachine, Registry.CurrentUser })
            {
                try
                {
                    using RegistryKey? k = hive.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\Explorer");
                    if (k?.GetValue("SettingsPageVisibility") is string s && s.Trim().Length > 0)
                    {
                        return s.Trim();
                    }
                }
                catch
                {
                }
            }
            return string.Empty;
        }
    }

    // ---- Interop -------------------------------------------------------------------------------------------------------

    internal static class Native
    {
        internal const uint LOAD_LIBRARY_AS_DATAFILE = 0x2;

        internal const uint LOAD_LIBRARY_AS_IMAGE_RESOURCE = 0x20;

        internal const uint SIGDN_NORMALDISPLAY = 0;

        internal const uint SIGDN_PARENTRELATIVEPARSING = 0x80018001;

        internal static readonly Guid BHID_EnumItems = new Guid("94f60519-2850-4924-aa5a-d15e84868039");

        [ComImport]
        [Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        internal interface IShellItem
        {
            void BindToHandler(nint pbc, ref Guid bhid, ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out object ppv);

            void GetParent(out IShellItem ppsi);

            void GetDisplayName(uint sigdnName, out nint ppszName);

            void GetAttributes(uint sfgaoMask, out uint psfgaoAttribs);

            void Compare(IShellItem psi, uint hint, out int piOrder);
        }

        [ComImport]
        [Guid("70629033-e363-4a28-a567-0db78006e6d7")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        internal interface IEnumShellItems
        {
            [PreserveSig]
            int Next(uint celt, out IShellItem rgelt, out uint pceltFetched);

            void Skip(uint celt);

            void Reset();

            void Clone(out IEnumShellItems ppenum);
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
        internal static extern void SHCreateItemFromParsingName(string pszPath, nint pbc, ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out IShellItem ppv);

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        internal static extern int SHParseDisplayName(string pszName, nint pbc, out nint ppidl, uint sfgaoIn, out uint psfgaoOut);

        [DllImport("ole32.dll")]
        internal static extern void CoTaskMemFree(nint pv);

        [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)]
        private static extern int SHLoadIndirectString(string pszSource, StringBuilder pszOutBuf, int cchOutBuf, nint ppvReserved);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool SetThreadPreferredUILanguages(uint dwFlags, string? pwszLanguagesBuffer, out uint pulNumLanguages);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetPackagesByPackageFamily(string packageFamilyName, ref uint count, [Out] nint[]? packageFullNames, ref uint bufferLength, [Out] char[]? buffer);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern nint LoadLibraryExW(string lpLibFileName, nint hFile, uint dwFlags);

        [DllImport("kernel32.dll")]
        internal static extern bool FreeLibrary(nint hModule);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        internal static extern nint FindResourceW(nint hModule, nint lpName, string lpType);

        [DllImport("kernel32.dll")]
        internal static extern nint LoadResource(nint hModule, nint hResInfo);

        [DllImport("kernel32.dll")]
        internal static extern nint LockResource(nint hResData);

        [DllImport("kernel32.dll")]
        internal static extern uint SizeofResource(nint hModule, nint hResInfo);

        internal delegate bool EnumResNameProc(nint hModule, nint lpType, nint lpName, nint lParam);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        internal static extern bool EnumResourceNamesW(nint hModule, string lpType, EnumResNameProc lpEnumFunc, nint lParam);

        [DllImport("netapi32.dll", CharSet = CharSet.Unicode)]
        internal static extern int NetGetJoinInformation(string? server, out nint name, out int status);

        [DllImport("netapi32.dll")]
        internal static extern int NetApiBufferFree(nint buffer);

        [DllImport("user32.dll")]
        internal static extern int GetSystemMetrics(int index);

        [DllImport("powrprof.dll")]
        internal static extern uint PowerDeterminePlatformRoleEx(uint version);

        internal const uint RIM_TYPEHID = 2;

        internal const uint RIDI_DEVICEINFO = 0x2000000B;

        [StructLayout(LayoutKind.Sequential)]
        internal struct RAWINPUTDEVICELIST
        {
            public nint hDevice;
            public uint dwType;
        }

        // RID_DEVICE_INFO with its HID member (the union's largest member, the keyboard's, makes it 32 bytes).
        [StructLayout(LayoutKind.Explicit, Size = 32)]
        internal struct RID_DEVICE_INFO
        {
            [FieldOffset(0)]
            public uint cbSize;
            [FieldOffset(4)]
            public uint dwType;
            [FieldOffset(20)]
            public ushort usUsagePage;
            [FieldOffset(22)]
            public ushort usUsage;
        }

        [DllImport("user32.dll", SetLastError = true)]
        internal static extern uint GetRawInputDeviceList([Out] RAWINPUTDEVICELIST[]? pRawInputDeviceList, ref uint puiNumDevices, uint cbSize);

        [DllImport("user32.dll", SetLastError = true, EntryPoint = "GetRawInputDeviceInfoW")]
        internal static extern uint GetRawInputDeviceInfo(nint hDevice, uint uiCommand, ref RID_DEVICE_INFO pData, ref uint pcbSize);

        internal static string? TakeString(nint p)
        {
            if (p == IntPtr.Zero)
            {
                return null;
            }
            string? s = Marshal.PtrToStringUni(p);
            CoTaskMemFree(p);
            return s;
        }

        private const uint MUI_LANGUAGE_NAME = 0x8;

        // null resets the thread to the process languages.
        internal static void SetLanguage(string? lang)
        {
            try
            {
                SetThreadPreferredUILanguages(lang == null ? 0u : MUI_LANGUAGE_NAME, lang == null ? null : lang + "\0\0", out _);
            }
            catch
            {
            }
        }

        private static readonly ConcurrentDictionary<string, string?> FamilyToFull = new ConcurrentDictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

        private static readonly Regex FamilyRx = new Regex(@"^@\{([A-Za-z0-9.\-]+_[a-z0-9]{13})\?", RegexOptions.Compiled | RegexOptions.CultureInvariant);

        // SHLoadIndirectString, with a package family name ("@{Microsoft.SecHealthUI_8wekyb3d8bbwe?ms-resource:...}")
        // swapped for the installed package's full name, which is what the API needs.
        internal static string? LoadIndirect(string src, StringBuilder sb)
        {
            Match m = FamilyRx.Match(src);
            if (m.Success)
            {
                string? full = FamilyToFull.GetOrAdd(m.Groups[1].Value, FullNameForFamily);
                if (full != null)
                {
                    src = "@{" + full + "?" + src.Substring(m.Length);
                }
            }
            sb.Clear();
            sb.EnsureCapacity(8192);
            int hr = SHLoadIndirectString(src, sb, sb.Capacity, IntPtr.Zero);
            if (hr != 0)
            {
                return null;
            }
            string s = sb.ToString().Trim();
            return s.Length == 0 || s.StartsWith("@", StringComparison.Ordinal) ? null : s;
        }

        private static string? FullNameForFamily(string family)
        {
            try
            {
                uint count = 0;
                uint len = 0;
                GetPackagesByPackageFamily(family, ref count, null, ref len, null);
                if (count == 0 || len == 0)
                {
                    return null;
                }
                nint[] ptrs = new nint[count];
                char[] buf = new char[len];
                if (GetPackagesByPackageFamily(family, ref count, ptrs, ref len, buf) != 0)
                {
                    return null;
                }
                return new string(buf).Split('\0')[0];
            }
            catch
            {
                return null;
            }
        }
    }
}
