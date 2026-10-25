using System;
using System.Collections.Generic;
using System.Linq;

#nullable enable annotations

namespace Win81Layer;

// Greek <-> English concept rings for the Windows 7 Start menu search. The system index already carries Windows' own
// Greek names and keywords when the Greek language pack is installed; this table only covers what that data lacks:
// everyday Greek words Windows does not use (ύπνος, οδηγοί, σκοτεινό, ταπετσαρία), transliterations (μπλουτουθ,
// ιντερνετ), greeklish (pontiki) and English program names a Greek query should reach (αριθμομηχανή -> Calculator).
//
// A ring lists English terms first, then Greek terms; terms marked with '~' are greeklish spellings, matched but never
// used as a replacement. Expand rewrites a folded query: every token or token run that matches a ring term is replaced
// by the ring's terms of the other language, which gives a few whole-query alternatives with a penalty (-3 when every
// match was exact, -6 when one was an inflection or a word still being typed). A Greek query is always rewritten to
// English (and to the ring's other Greek words); an English query only when the interface language is Greek, since the
// index then holds Greek names only.
internal static class SearchSynonyms
{
    internal const int ExactPenalty = -3;

    internal const int LoosePenalty = -6;

    private static readonly string[][] RawRings =
    {
        new[] { "mouse", "ποντίκι", "~pontiki" },
        new[] { "pointer", "cursor", "δείκτης", "δρομέας", "κέρσορας", "~deiktis" },
        new[] { "sound", "audio", "ήχος", "ήχοι", "~ixos", "~hxos" },
        new[] { "volume", "ένταση", "ένταση ήχου", "~entasi" },
        new[] { "volume mixer", "μείκτης έντασης", "μείκτης έντασης ήχου" },
        new[] { "microphone", "mic", "μικρόφωνο", "~mikrofono" },
        new[] { "speakers", "speaker", "ηχεία", "ηχείο" },
        new[] { "headphones", "headset", "ακουστικά" },
        new[] { "display", "screen", "οθόνη", "~othoni" },
        new[] { "resolution", "ανάλυση", "~analysi" },
        new[] { "brightness", "φωτεινότητα", "~fwteinotita" },
        new[] { "night light", "νυχτερινός φωτισμός", "νυχτερινό φως" },
        new[] { "wifi", "wireless", "wlan", "ασύρματο", "ασύρματο δίκτυο", "~asyrmato" },
        new[] { "network", "δίκτυο", "~diktyo" },
        new[] { "internet", "ιντερνέτ", "ίντερνετ", "διαδίκτυο" },
        new[] { "bluetooth", "μπλουτούθ", "μπλουτουθ", "~mploutouth", "~blutut" },
        new[] { "printers", "printer", "εκτυπωτής", "εκτυπωτές", "~ektypwths", "~ektipotis" },
        new[] { "print", "εκτύπωση" },
        new[] { "scanner", "σαρωτής" },
        new[] { "keyboard", "πληκτρολόγιο", "~pliktrologio" },
        new[] { "keyboard layout", "διάταξη πληκτρολογίου" },
        new[] { "on-screen keyboard", "πληκτρολόγιο οθόνης" },
        new[] { "language", "γλώσσα", "~glossa" },
        new[] { "battery", "μπαταρία", "~mpataria" },
        new[] { "power", "τροφοδοσία", "παροχή ενέργειας", "ενέργεια" },
        new[] { "power options", "επιλογές παροχής ενέργειας" },
        new[] { "sleep", "ύπνος", "αναστολή λειτουργίας", "~ypnos" },
        new[] { "shut down", "τερματισμός", "τερματισμός λειτουργίας" },
        new[] { "update", "updates", "ενημέρωση", "ενημερώσεις", "~enimerosi" },
        new[] { "drivers", "driver", "οδηγοί", "οδηγός", "προγράμματα οδήγησης", "ντράιβερ" },
        new[] { "device manager", "διαχείριση συσκευών" },
        new[] { "devices", "συσκευές" },
        new[] { "disk management", "διαχείριση δίσκων" },
        new[] { "disk", "drive", "δίσκος", "μονάδα δίσκου" },
        new[] { "storage", "αποθήκευση", "χώρος αποθήκευσης" },
        new[] { "disk cleanup", "εκκαθάριση δίσκου" },
        new[] { "defragment", "ανασυγκρότηση" },
        new[] { "services", "υπηρεσίες" },
        new[] { "event viewer", "προβολή συμβάντων" },
        new[] { "task scheduler", "χρονοδιάγραμμα εργασιών", "χρονοπρογραμματιστής εργασιών" },
        new[] { "task manager", "διαχείριση εργασιών" },
        new[] { "registry editor", "registry", "regedit", "επεξεργαστής μητρώου", "μητρώο" },
        new[] { "system information", "πληροφορίες συστήματος" },
        new[] { "system configuration", "msconfig", "ρύθμιση παραμέτρων συστήματος" },
        new[] { "system", "σύστημα" },
        new[] { "system restore", "επαναφορά συστήματος" },
        new[] { "control panel", "πίνακας ελέγχου" },
        new[] { "settings", "ρυθμίσεις", "~rythmiseis" },
        new[] { "uninstall", "απεγκατάσταση", "κατάργηση εγκατάστασης" },
        new[] { "programs and features", "προγράμματα και δυνατότητες" },
        new[] { "apps", "programs", "εφαρμογές", "προγράμματα" },
        new[] { "default apps", "προεπιλεγμένες εφαρμογές" },
        new[] { "users", "user accounts", "χρήστες", "λογαριασμοί χρηστών" },
        new[] { "accounts", "account", "λογαριασμοί", "λογαριασμός" },
        new[] { "password", "κωδικός", "κωδικός πρόσβασης" },
        new[] { "sign in", "σύνδεση" },
        new[] { "date", "ημερομηνία" },
        new[] { "time", "clock", "ώρα", "ρολόι" },
        new[] { "firewall", "τείχος προστασίας" },
        new[] { "security", "ασφάλεια" },
        new[] { "virus", "antivirus", "ιός", "ιοί", "προστασία από ιούς" },
        new[] { "privacy", "απόρρητο", "προστασία προσωπικών δεδομένων" },
        new[] { "calculator", "αριθμομηχανή" },
        new[] { "notepad", "σημειωματάριο" },
        new[] { "paint", "ζωγραφική" },
        new[] { "file explorer", "εξερεύνηση αρχείων" },
        new[] { "file explorer options", "folder options", "επιλογές εξερεύνησης αρχείων", "επιλογές φακέλων" },
        new[] { "hidden files", "κρυφά αρχεία" },
        new[] { "file extensions", "επεκτάσεις αρχείων" },
        new[] { "background", "wallpaper", "φόντο", "ταπετσαρία" },
        new[] { "colors", "color", "colours", "χρώματα", "χρώμα" },
        new[] { "dark mode", "dark", "σκούρο", "σκοτεινό", "σκοτεινή λειτουργία" },
        new[] { "themes", "theme", "θέματα", "θέμα" },
        new[] { "taskbar", "γραμμή εργασιών" },
        new[] { "start menu", "μενού έναρξη" },
        new[] { "lock screen", "οθόνη κλειδώματος" },
        new[] { "notifications", "ειδοποιήσεις" },
        new[] { "camera", "webcam", "κάμερα" },
        new[] { "fonts", "γραμματοσειρές" },
        new[] { "backup", "αντίγραφα ασφαλείας", "αντίγραφο ασφαλείας" },
        new[] { "recovery", "reset", "αποκατάσταση", "επαναφορά" },
        new[] { "startup", "εκκίνηση" },
        new[] { "location", "τοποθεσία" },
        new[] { "accessibility", "ease of access", "προσβασιμότητα", "διευκόλυνση πρόσβασης" },
        new[] { "narrator", "αφηγητής" },
        new[] { "magnifier", "μεγεθυντικός φακός" },
        new[] { "touchpad", "επιφάνεια αφής" },
        new[] { "clipboard", "πρόχειρο" },
        new[] { "environment variables", "μεταβλητές περιβάλλοντος" },
        new[] { "performance", "επιδόσεις", "απόδοση" },
        new[] { "command prompt", "cmd", "γραμμή εντολών" },
        new[] { "windows tools", "administrative tools", "εργαλεία των windows", "εργαλεία διαχείρισης" },
        new[] { "remote desktop", "απομακρυσμένη επιφάνεια εργασίας" },
        new[] { "personalization", "εξατομίκευση", "προσωποποίηση" },
        new[] { "mail", "email", "αλληλογραφία" },
        new[] { "sync", "συγχρονισμός" },
        new[] { "region", "περιοχή" },
        new[] { "game controllers", "gamepad", "joystick", "χειριστήρια παιχνιδιών", "χειριστήριο" },
        new[] { "gaming", "game mode", "παιχνίδια", "λειτουργία παιχνιδιού" },
        new[] { "proxy", "διακομιστής μεσολάβησης" },
        new[] { "hotspot", "φορητό σημείο πρόσβασης" },
        new[] { "airplane mode", "λειτουργία πτήσης" },
        new[] { "snipping tool", "screenshot", "εργαλείο αποκομμάτων", "στιγμιότυπο οθόνης" },
        new[] { "sound recorder", "ηχογράφηση" },
        new[] { "character map", "χάρτης χαρακτήρων" },
        new[] { "speed", "ταχύτητα" },
        new[] { "double click", "διπλό κλικ" },
        new[] { "scroll", "wheel", "κύλιση", "ροδέλα" },
        new[] { "left handed", "αριστερόχειρας" },
        new[] { "internet options", "επιλογές internet" },
        new[] { "sharing", "κοινή χρήση" },
        new[] { "troubleshoot", "αντιμετώπιση προβλημάτων" },
        new[] { "phone", "τηλέφωνο" },
        new[] { "developers", "προγραμματιστές" },
        new[] { "activation", "ενεργοποίηση" },
    };

    private sealed class Term
    {
        internal Term(int ring, string raw)
        {
            Ring = ring;
            Greeklish = raw.StartsWith("~", StringComparison.Ordinal);
            Text = SearchText.Fold(Greeklish ? raw.Substring(1) : raw);
            Words = SearchText.Words(Text);
            Stems = Words.Select(SearchText.Stem).ToArray();
            Greek = SearchText.HasGreek(Text);
        }

        internal int Ring { get; }

        internal string Text { get; }

        internal string[] Words { get; }

        internal string[] Stems { get; }

        internal bool Greek { get; }

        internal bool Greeklish { get; }
    }

    private static readonly List<Term[]> Rings = BuildRings();

    private static List<Term[]> BuildRings()
    {
        List<Term[]> rings = new List<Term[]>(RawRings.Length);
        for (int i = 0; i < RawRings.Length; i++)
        {
            rings.Add(RawRings[i].Select((string t) => new Term(i, t)).Where((Term t) => t.Words.Length > 0).ToArray());
        }
        return rings;
    }

    internal static int RingCount => Rings.Count;

    private readonly record struct Match(int Ring, Term Term, int Start, int Length, bool Exact);

    // Alternatives for a folded query (the query itself is not among them), at most max. uiGreek: the index's primary
    // names are Greek, so English queries are rewritten to Greek too.
    internal static List<(string Text, int Penalty)> Expand(string folded, bool uiGreek, int max = 6)
    {
        List<(string Text, int Penalty)> result = new List<(string Text, int Penalty)>();
        if (string.IsNullOrEmpty(folded))
        {
            return result;
        }
        string[] tokens = SearchText.Words(folded);
        bool greekQuery = SearchText.HasGreek(folded);
        List<Match> matches = FindMatches(tokens, fromLayout: false);
        if (!greekQuery && folded.Length >= 3)
        {
            // A Greek word typed on the English layout ("pontiki", "ixos" is greeklish and listed as such).
            string gk = SearchText.Fold(GreekLayout.ToGreekKeys(folded));
            if (gk.Length > 0 && SearchText.HasGreek(gk))
            {
                List<Match> layout = FindMatches(SearchText.Words(gk), fromLayout: true);
                if (layout.Count > 0 && matches.Count == 0)
                {
                    tokens = SearchText.Words(gk);
                    matches = layout;
                    // The Greek text itself is searched too (it matches Windows' Greek names directly).
                    result.Add((gk, LoosePenalty));
                }
            }
        }
        if (matches.Count == 0)
        {
            return result;
        }
        // Longest terms first, then the earliest; matches must not overlap.
        List<Match> chosen = new List<Match>();
        bool[] used = new bool[tokens.Length];
        foreach (Match m in matches.OrderByDescending((Match x) => x.Length).ThenBy((Match x) => x.Start).ThenByDescending((Match x) => x.Exact))
        {
            bool free = true;
            for (int k = m.Start; k < m.Start + m.Length; k++)
            {
                free &= !used[k];
            }
            if (!free)
            {
                continue;
            }
            for (int k = m.Start; k < m.Start + m.Length; k++)
            {
                used[k] = true;
            }
            chosen.Add(m);
        }
        // Replacement candidates per match, in ring order.
        List<List<string>> candidates = new List<List<string>>(chosen.Count);
        bool any = false;
        foreach (Match m in chosen)
        {
            bool fromGreek = m.Term.Greek || m.Term.Greeklish;
            List<string> c = new List<string>();
            if (fromGreek || uiGreek)
            {
                foreach (Term t in Rings[m.Ring])
                {
                    if (t.Greeklish || ReferenceEquals(t, m.Term) || string.Equals(t.Text, m.Term.Text, StringComparison.Ordinal))
                    {
                        continue;
                    }
                    // A Greek match goes to English and to the ring's other Greek words; an English one only to Greek.
                    if (fromGreek || t.Greek)
                    {
                        c.Add(t.Text);
                    }
                }
            }
            any |= c.Count > 0;
            candidates.Add(c);
        }
        if (!any)
        {
            return result;
        }
        bool exact = chosen.All((Match m) => m.Exact);
        int penalty = exact ? ExactPenalty : LoosePenalty;
        int variants = Math.Min(max, candidates.Max((List<string> c) => c.Count));
        HashSet<string> seen = new HashSet<string>(StringComparer.Ordinal) { folded };
        foreach ((string text, int _) in result)
        {
            seen.Add(text);
        }
        for (int v = 0; v < variants; v++)
        {
            List<string> outWords = new List<string>(tokens.Length + 2);
            int i = 0;
            while (i < tokens.Length)
            {
                int mi = chosen.FindIndex((Match m) => m.Start == i);
                if (mi < 0)
                {
                    outWords.Add(tokens[i]);
                    i++;
                    continue;
                }
                List<string> c = candidates[mi];
                outWords.Add(c.Count == 0 ? string.Join(" ", tokens, chosen[mi].Start, chosen[mi].Length) : c[Math.Min(v, c.Count - 1)]);
                i += chosen[mi].Length;
            }
            string text = string.Join(" ", outWords);
            if (seen.Add(text))
            {
                result.Add((text, penalty));
            }
        }
        return result;
    }

    // ---- Intents -----------------------------------------------------------------------------------------------------

    // A whole query that asks for one well-known Windows page ('uninstall', 'volume', 'ενημέρωση') lifts the entries that
    // answer it to the prefix tier, whatever the interface language: a Settings page or Control Panel item is added even
    // when its own words do not match, a setting, task or tool only when it matched (by any word when AnyMatch, by name
    // otherwise: 'brightness' lifts 'Change brightness level', not a contrast setting that only has it as a keyword).
    // Targets are compared as they are (ms-settings: names) or by program file name (control.exe, sndvol.exe), with the
    // arguments when given.
    internal sealed class Intent
    {
        internal Intent(string name, string[] terms, params (SystemHitKind Kind, string Target, string? Args, bool AnyMatch)[] lifts)
        {
            Name = name;
            Terms = terms.Select(SearchText.Fold).Where((string t) => t.Length > 0).Distinct(StringComparer.Ordinal).ToArray();
            Lifts = lifts;
        }

        internal string Name { get; }

        internal string[] Terms { get; }

        internal (SystemHitKind Kind, string Target, string? Args, bool AnyMatch)[] Lifts { get; }

        // The query is a term, or (five letters or more) the start of a one-word term still being typed.
        internal bool Matches(string folded)
        {
            foreach (string t in Terms)
            {
                if (string.Equals(t, folded, StringComparison.Ordinal) || (folded.Length >= 5 && t.IndexOf(' ') < 0 && t.StartsWith(folded, StringComparison.Ordinal)))
                {
                    return true;
                }
            }
            return false;
        }
    }

    internal static readonly Intent[] Intents =
    {
        new Intent("uninstall",
            new[]
            {
                "uninstall", "uninstall a program", "uninstall programs", "uninstall apps", "remove program", "remove programs", "remove apps",
                "add or remove programs", "add remove programs", "απεγκατάσταση", "απεγκατάσταση προγράμματος", "απεγκατάσταση προγραμμάτων",
                "απεγκατάσταση εφαρμογών", "κατάργηση εγκατάστασης", "κατάργηση προγράμματος", "προσθαφαίρεση προγραμμάτων"
            },
            (SystemHitKind.SettingsPage, "ms-settings:installed-apps", null, true),
            (SystemHitKind.Setting, "ms-settings:installed-apps", null, true),
            (SystemHitKind.ControlPanelTask, "control.exe", "/name Microsoft.ProgramsAndFeatures", true)),
        new Intent("update",
            new[]
            {
                "update", "updates", "windows update", "windows updates", "check for updates", "ενημέρωση", "ενημερώσεις", "ενημέρωση windows",
                "ενημερώσεις windows", "windows ενημέρωση"
            },
            (SystemHitKind.SettingsPage, "ms-settings:windowsupdate", null, true)),
        new Intent("volume",
            new[] { "volume", "sound volume", "ένταση", "ένταση ήχου" },
            (SystemHitKind.SettingsPage, "ms-settings:sound", null, true),
            (SystemHitKind.Tool, "sndvol.exe", null, false),
            (SystemHitKind.ControlPanelTask, "sndvol.exe", null, false)),
        new Intent("brightness",
            new[] { "brightness", "screen brightness", "φωτεινότητα", "φωτεινότητα οθόνης" },
            (SystemHitKind.Setting, "ms-settings:display", null, false)),
    };

    private static List<Match> FindMatches(string[] tokens, bool fromLayout)
    {
        List<Match> found = new List<Match>();
        if (tokens.Length == 0)
        {
            return found;
        }
        string[] stems = tokens.Select(SearchText.Stem).ToArray();
        for (int r = 0; r < Rings.Count; r++)
        {
            foreach (Term t in Rings[r])
            {
                int m = t.Words.Length;
                for (int start = 0; start + m <= tokens.Length; start++)
                {
                    bool ok = true;
                    bool exact = !fromLayout;
                    for (int k = 0; k < m && ok; k++)
                    {
                        string tok = tokens[start + k];
                        string w = t.Words[k];
                        if (string.Equals(tok, w, StringComparison.Ordinal))
                        {
                            continue;
                        }
                        bool lastTyped = k == m - 1 && start + k == tokens.Length - 1 && tok.Length >= 3 && w.StartsWith(tok, StringComparison.Ordinal);
                        bool inflected = t.Greek && stems[start + k].Length >= 3 && string.Equals(stems[start + k], t.Stems[k], StringComparison.Ordinal);
                        if (lastTyped || inflected)
                        {
                            exact = false;
                            continue;
                        }
                        ok = false;
                    }
                    if (ok)
                    {
                        found.Add(new Match(r, t, start, m, exact));
                    }
                }
            }
        }
        return found;
    }
}
