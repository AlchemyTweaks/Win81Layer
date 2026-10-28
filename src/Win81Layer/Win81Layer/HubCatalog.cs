using System;
using System.Collections.Generic;
using System.Linq;

#nullable enable annotations

namespace Win81Layer;

internal enum HubRowKind
{
	Toggle,
	Choice,
	Slider,
	Segmented,
	DeviceList,
	DrillIn,
	Link,
	Info,
	Button,
	Custom
}

// A page of PC Settings: its sidebar entry (group, glyph, mini-tile colour) and its names. Group 0 = not in the sidebar
// (the account page has its own row, drill-in pages belong to their parent).
internal sealed record HubPage(string Id, string Title, string TitleEl, int Group, char Glyph, string Tile, string Keywords, string? Parent = null)
{
	// Short names the page is known by ("Power", "Οθόνη", "Taskbar"): ranked as names, never shown.
	internal string[] Aliases { get; init; } = Array.Empty<string>();
}

// One row of a page: what search, Favourites and the journal know about it. Link: the Windows page or applet the row
// opens (a link row), or the Windows page that holds the same setting (search folds that page's index hit into the row).
internal sealed record HubRow(string Id, string Page, string Card, string Title, string TitleEl, string Keywords, HubRowKind Kind, LinkSpec? Link = null)
{
	// Other names the setting is known by ("Mouse speed", "Ταχύτητα ποντικιού"): ranked as names, never shown.
	internal string[] Aliases { get; init; } = Array.Empty<string>();
}

// The one catalog of PC Settings, built the first time a window opens and kept (about 60 KB, nothing in it runs). It feeds
// the sidebar, the page builders (titles), search and the old-name aliases. Windows' own pages are not copied in: they
// come from SystemSearchIndex.
internal static class HubCatalog
{
	private static IReadOnlyList<HubPage>? _pages;

	private static IReadOnlyList<HubRow>? _rows;

	private static Dictionary<string, HubPage>? _pageById;

	private static Dictionary<string, HubRow>? _rowById;

	internal static IReadOnlyList<HubPage> Pages
	{
		get
		{
			Ensure();
			return _pages!;
		}
	}

	internal static IReadOnlyList<HubRow> Rows
	{
		get
		{
			Ensure();
			return _rows!;
		}
	}

	internal static HubPage? Page(string id)
	{
		Ensure();
		return id != null && _pageById!.TryGetValue(id, out HubPage? p) ? p : null;
	}

	internal static HubRow? Row(string id)
	{
		Ensure();
		return id != null && _rowById!.TryGetValue(id, out HubRow? r) ? r : null;
	}

	// The row's title (the id itself when it is not catalogued, which QA reports).
	internal static string Title(string rowId) => Row(rowId)?.Title ?? rowId;

	private static readonly object Gate = new object();

	private static void Ensure()
	{
		if (_rows != null)
		{
			return;
		}
		lock (Gate)
		{
			if (_rows != null)
			{
				return;
			}
			List<HubPage> pages = BuildPages().Select(WithPageAliases).ToList();
			List<HubRow> rows = BuildRows().Select(WithAliases).ToList();
			_pageById = pages.ToDictionary((HubPage p) => p.Id, StringComparer.OrdinalIgnoreCase);
			_rowById = rows.ToDictionary((HubRow r) => r.Id, StringComparer.OrdinalIgnoreCase);
			_pages = pages;
			_rows = rows;
		}
	}

	// ---- Old names --------------------------------------------------------------------------------------------------

	// Every category and sub-page name of the earlier PC Settings, so OpenCategory/Nav callers and old QA keep working.
	private static readonly Dictionary<string, string> Aliases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
	{
		["PC & devices"] = "home",
		["Launcher"] = "launcher",
		["Desktop composition"] = "appearance/frames",
		["Accounts"] = "account",
		["OneDrive"] = "home",
		["Search and apps"] = "apps",
		["Privacy"] = "privacy",
		["Network"] = "network",
		["Time and language"] = "time",
		["Ease of Access"] = "accessibility",
		["Update and recovery"] = "about",
		["Control Panel"] = "tools",
		["Performance tools"] = "tools",
		["Experiments"] = "launcher/experiments",
		["Display"] = "displays",
		["Power & sleep"] = "power",
		["PC info"] = "about",
		["Lock screen"] = "appearance",
		["Start screen"] = "start",
		["Account picture"] = "account",
		["Notifications"] = "notifications",
		["Appearance"] = "appearance",
		["Pointers"] = "appearance/pointers"
	};

	// An old name, a page id ("mouse", "appearance/pointers") or a row id ("mouse.speed": its page) -> a page id; null
	// when it names nothing.
	internal static string? Resolve(string? name)
	{
		if (string.IsNullOrWhiteSpace(name))
		{
			return null;
		}
		Ensure();
		string n = name.Trim();
		if (Aliases.TryGetValue(n, out string? alias))
		{
			return alias;
		}
		if (_pageById!.ContainsKey(n))
		{
			return _pageById[n].Id;
		}
		if (_rowById!.TryGetValue(n, out HubRow? row))
		{
			return row.Page;
		}
		return null;
	}

	internal static IEnumerable<string> AliasNames => Aliases.Keys;

	internal static string AliasTarget(string name) => Aliases[name];

	// "win81:pcsettings?page=mouse&row=mouse.speed" -> ("mouse", "mouse.speed"); unknown values are dropped.
	internal static (string? Page, string? Row) ParseUri(string? uri)
	{
		if (string.IsNullOrEmpty(uri))
		{
			return (null, null);
		}
		int q = uri.IndexOf('?');
		if (q < 0)
		{
			return (null, null);
		}
		string? page = null;
		string? row = null;
		foreach (string part in uri.Substring(q + 1).Split('&', StringSplitOptions.RemoveEmptyEntries))
		{
			int eq = part.IndexOf('=');
			if (eq <= 0)
			{
				continue;
			}
			string key = part.Substring(0, eq).Trim();
			string value = Uri.UnescapeDataString(part.Substring(eq + 1).Trim());
			if (key.Equals("page", StringComparison.OrdinalIgnoreCase))
			{
				page = Resolve(value);
			}
			else if (key.Equals("row", StringComparison.OrdinalIgnoreCase) && Row(value) != null)
			{
				row = Row(value)!.Id;
			}
		}
		if (row != null && page == null)
		{
			page = Row(row)!.Page;
		}
		return (page, row);
	}

	// ---- Pages ------------------------------------------------------------------------------------------------------

	internal const string LauncherTile = "accent";

	private static List<HubPage> BuildPages()
	{
		return new List<HubPage>
		{
			new HubPage("account", "Account", "Λογαριασμός", 0, '', "#0072C6", "user account picture sign in profile|χρήστης λογαριασμός εικόνα σύνδεση προφίλ"),
			new HubPage("home", "Home", "Αρχική", 1, '', "#0072C6", "start overview quick|αρχική επισκόπηση γρήγορα"),
			new HubPage("recent", "Recent changes", "Πρόσφατες αλλαγές", 1, '', "#5A5A5A", "history undo changes|ιστορικό αναίρεση αλλαγές"),
			new HubPage("network", "Wi-Fi & network", "Wi-Fi και δίκτυο", 2, '', "#0086B3", "internet ethernet wireless lan connection ip|ίντερνετ διαδίκτυο ασύρματο σύνδεση δίκτυο"),
			new HubPage("devices", "Bluetooth & devices", "Bluetooth και συσκευές", 2, '', "#7436D6", "printers usb hardware peripherals|εκτυπωτές συσκευές υλικό περιφερειακά"),
			new HubPage("sound", "Sound", "Ήχος", 3, '', "#D24726", "audio volume speakers headphones microphone|ήχος ένταση ηχεία ακουστικά μικρόφωνο"),
			new HubPage("sound/apps", "App volume", "Ένταση εφαρμογών", 0, '', "#D24726", "mixer per app volume|μίκτης ένταση εφαρμογής", "sound"),
			new HubPage("displays", "Displays", "Οθόνες", 3, '', "#007EE5", "monitor screen brightness resolution|οθόνη φωτεινότητα ανάλυση"),
			new HubPage("power", "Power & sleep", "Παροχή ενέργειας και αναστολή", 3, '', "#00A300", "battery energy timeout shutdown|μπαταρία ενέργεια τερματισμός αναστολή"),
			new HubPage("power/plans", "Power plans", "Σχέδια παροχής ενέργειας", 0, '', "#00A300", "all plans schemes|όλα τα σχέδια", "power"),
			new HubPage("mouse", "Mouse", "Ποντίκι", 3, '', "#3A5FC4", "pointer cursor click wheel scroll|δείκτης κέρσορας κλικ ροδέλα κύλιση"),
			new HubPage("keyboard", "Keyboard", "Πληκτρολόγιο", 3, '', "#6430D2", "typing keys repeat layout language|πληκτρολόγηση πλήκτρα επανάληψη διάταξη γλώσσα"),
			new HubPage("touchpad", "Touchpad & pen", "Επιφάνεια αφής και πένα", 3, '', "#3A5FC4", "trackpad touch pen stylus|αφή πένα γραφίδα"),
			new HubPage("appearance", "Appearance", "Εμφάνιση", 4, '', "#C72382", "theme colour color look accent wallpaper dark light|θέμα χρώμα εμφάνιση ταπετσαρία σκούρο φωτεινό"),
			new HubPage("appearance/pointers", "Pointers", "Δείκτες", 0, '', "#C72382", "cursor pointer scheme alchemy|δείκτης κέρσορας", "appearance"),
			new HubPage("appearance/frames", "Window frames", "Πλαίσια παραθύρων", 0, '', "#C72382", "composition aero glass transparency shadows|σύνθεση διαφάνεια σκιές", "appearance"),
			new HubPage("appearance/seasonal", "Seasonal options", "Επιλογές εποχών", 0, '', "#C72382", "halloween christmas season seasonal wallpaper pictures effects icons|εποχή εποχικά αποκριές χριστούγεννα ταπετσαρία εικόνες εφέ εικονίδια", "appearance"),
			new HubPage("appearance/rest", "Dark rest options", "Επιλογές σκοτεινής ξεκούρασης", 0, '', "#C72382", "dark rest night calm dim wallpaper|σκοτεινή ξεκούραση νύχτα ήρεμο θαμπό ταπετσαρία", "appearance"),
			new HubPage("taskbar", "Desktop & taskbar", "Επιφάνεια εργασίας και γραμμή εργασιών", 4, '', "#008299", "taskbar corners edges hot corners charms|γραμμή εργασιών γωνίες άκρες", null),
			new HubPage("start", "Start & search", "Έναρξη και αναζήτηση", 4, '', "#00897B", "start menu tiles live tiles search weather|μενού έναρξη πλακίδια αναζήτηση καιρός"),
			new HubPage("notifications", "Notifications", "Ειδοποιήσεις", 5, '', "#CF2056", "notification toast banners alerts|ειδοποιήσεις μηνύματα"),
			new HubPage("accessibility", "Accessibility", "Προσβασιμότητα", 5, '', "#0063B1", "ease of access narrator magnifier contrast|ευκολία πρόσβασης αφηγητής μεγεθυντικός αντίθεση"),
			new HubPage("time", "Time & language", "Ώρα και γλώσσα", 5, '', "#1E7145", "clock date region format language|ρολόι ημερομηνία περιοχή μορφή γλώσσα"),
			new HubPage("apps", "Apps", "Εφαρμογές", 5, '', "#A20025", "programs default apps startup uninstall|προγράμματα προεπιλεγμένες εκκίνηση απεγκατάσταση"),
			new HubPage("privacy", "Privacy & security", "Απόρρητο και ασφάλεια", 5, '', "#2E3B4E", "permissions camera microphone location|απόρρητο ασφάλεια δικαιώματα κάμερα"),
			new HubPage("about", "About this PC", "Πληροφορίες υπολογιστή", 5, '', "#4A5459", "system specs storage update recovery|σύστημα προδιαγραφές αποθήκευση ενημέρωση ανάκτηση"),
			new HubPage("launcher", "Launcher", "Εκκινητής", 6, '', LauncherTile, "win8.1 layer shell startup profile|κέλυφος εκκίνηση προφίλ"),
			new HubPage("launcher/experiments", "Experiments", "Πειράματα", 0, '', LauncherTile, "experimental allowlist|πειραματικά", "launcher"),
			new HubPage("launcher/diagnostics", "Diagnostics", "Διαγνωστικά", 0, '', LauncherTile, "dwm telemetry status|διαγνωστικά κατάσταση", "launcher"),
			new HubPage("tools", "Control Panel & tools", "Πίνακας Ελέγχου και εργαλεία", 6, '', "#5A5A5A", "control panel administrative tools performance|πίνακας ελέγχου εργαλεία διαχείρισης απόδοση"),
			// Fix a problem: drill-ins of Home, named the way people describe the problem (search lists them in a group of
			// their own, after the top hit).
			new HubPage("fix/sound", "No sound", "Δεν ακούγεται ήχος", 0, '', "#D24726", "no sound silent muted speakers not working audio problem|χωρίς ήχο σίγαση ηχεία δεν δουλεύει πρόβλημα ήχου", "home"),
			new HubPage("fix/mouse", "Mouse too fast or slow", "Το ποντίκι κινείται πολύ γρήγορα ή αργά", 0, '', "#3A5FC4", "pointer sensitivity acceleration gaming problem|ευαισθησία επιτάχυνση παιχνίδια πρόβλημα", "home"),
			new HubPage("fix/doubleclick", "Double-click not working", "Δεν πιάνει το διπλό πάτημα", 0, '', "#3A5FC4", "double click opens slow fast problem|διπλό κλικ δεν ανοίγει πρόβλημα", "home"),
			new HubPage("fix/typing", "Wrong language or doubled letters", "Λάθος γλώσσα ή διπλά γράμματα", 0, '', "#6430D2", "typing wrong layout greek english repeated keys letters problem|πληκτρολόγηση διάταξη ελληνικά αγγλικά επαναλαμβανόμενα πλήκτρα πρόβλημα", "home"),
			new HubPage("fix/screen", "Screen too bright or eyes tired", "Πολύ φωτεινή οθόνη ή κουρασμένα μάτια", 0, '', "#007EE5", "brightness glare eye strain night dark problem|φωτεινότητα θάμπωμα μάτια νύχτα σκοτεινό πρόβλημα", "home"),
			new HubPage("fix/timeout", "Screen turns off too soon", "Η οθόνη σβήνει πολύ νωρίς", 0, '', "#00A300", "screen timeout display off sleep problem|λήξη χρόνου οθόνη σβήνει αναστολή πρόβλημα", "home")
		};
	}

	// Rows the Windows 7 Start menu lists in its PC settings group: a row with a control of its own (toggle, choice,
	// slider, segments, device list), not the Quick card's copies on Home, and not one that needs hardware or a page this
	// PC may not have (left out while the capabilities are not known).
	internal static bool StartSearchable(HubRow r, HubCaps? caps)
	{
		if (r.Kind is not (HubRowKind.Toggle or HubRowKind.Choice or HubRowKind.Slider or HubRowKind.Segmented or HubRowKind.DeviceList))
		{
			return false;
		}
		if (r.Page is "home" or "recent" or "touchpad" || r.Page.StartsWith("fix/", StringComparison.Ordinal))
		{
			return false;
		}
		return RowAvailable(r, caps, needControl: true);
	}

	// Whether a row is on its page on this PC: rows that need hardware (Wi-Fi, Bluetooth, radios, a battery, sleep, the
	// power buttons, a digitizer) only with that hardware, the power rows only while their Windows page or Control Panel
	// item can be reached. One predicate for the page builders' gating, hub search, the Windows 7 Start menu's PC settings
	// group and Setups. While the capabilities are not known, rows that need hardware are left out (as the pages do).
	// needControl: the row must have its own control (Sleep after is an info row on a PC without sleep).
	internal static bool RowAvailable(HubRow r, HubCaps? caps, bool needControl = false)
	{
		bool power = caps == null || caps.PageAvailable("powersleep") || caps.CplAllowed("powercfg.cpl");
		return r.Id switch
		{
			"home.wifi" or "network.wifitoggle" => caps?.HasWifi == true,
			"network.wifi" => caps == null || caps.HasWifi,
			"network.allwireless" => caps?.HasRadios == true,
			"devices.bttoggle" or "devices.paired" or "devices.bluetooth" => caps?.HasBluetooth == true,
			"devices.pen" => caps?.Digitizer == true,
			"power.screen" => power,
			"power.screen.dc" => power && caps?.HasBattery == true,
			"power.sleep" => power && (!needControl || caps?.SleepAllowed == true),
			"power.sleep.dc" => power && caps?.HasBattery == true && caps.SleepAllowed,
			"power.button" => power && caps?.PowerButtonPresent == true,
			"power.sleepbutton" => power && caps?.SleepButtonPresent == true,
			"power.lid" => power && caps?.LidPresent == true,
			_ => true
		};
	}

	// The Quick card's copies on Home and the row they copy: search lists the row itself when both match.
	internal static readonly IReadOnlyDictionary<string, string> QuickCopies = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
	{
		["home.brightness"] = "displays.brightness",
		["home.screen"] = "power.screen",
		["home.wifi"] = "network.wifitoggle",
		["home.look"] = "appearance.look"
	};

	// Launcher rows whose control changes how (or whether) the shell starts, recovers or draws: search never puts their
	// live control on the results page (one click there could take the shell away); they open their page instead.
	internal static readonly HashSet<string> NoLiveTopHit = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
	{
		"launcher.logon", "launcher.runkey", "launcher.dominant", "launcher.taskbar", "launcher.suspend", "launcher.replacestart",
		"launcher.lightrender", "launcher.softwarerender", "launcher.autorecover", "launcher.forcereboot"
	};

	// Short names of the pages, ranked as names ("power", "οθόνη", "γραμμή εργασιών" find the page first).
	private static readonly Dictionary<string, string[]> PageAliases = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
	{
		["network"] = new[] { "Network", "Wi-Fi", "Internet", "Δίκτυο" },
		["devices"] = new[] { "Bluetooth", "Devices", "Συσκευές" },
		["sound"] = new[] { "Audio" },
		["displays"] = new[] { "Display", "Screen", "Monitor", "Οθόνη" },
		["power"] = new[] { "Power", "Ενέργεια", "Τροφοδοσία" },
		["appearance"] = new[] { "Personalization", "Personalisation", "Εξατομίκευση" },
		["taskbar"] = new[] { "Taskbar", "Γραμμή εργασιών" },
		["start"] = new[] { "Start", "Start menu", "Έναρξη", "Μενού Έναρξη" },
		["accessibility"] = new[] { "Ease of Access", "Ευκολία πρόσβασης" },
		["time"] = new[] { "Time", "Language", "Region", "Ώρα", "Γλώσσα", "Περιοχή" },
		["apps"] = new[] { "Programs", "Προγράμματα" },
		["about"] = new[] { "System", "About", "Σύστημα" }
	};

	private static HubPage WithPageAliases(HubPage p)
	{
		return PageAliases.TryGetValue(p.Id, out string[]? a) ? p with { Aliases = a } : p;
	}

	// The fix pages, in the order Home shows them.
	internal static IEnumerable<HubPage> FixPages => Pages.Where((HubPage p) => p.Id.StartsWith("fix/", StringComparison.Ordinal));

	// ---- Rows -------------------------------------------------------------------------------------------------------

	private static HubRow R(string id, string card, string title, string titleEl, string keywords, HubRowKind kind, LinkSpec? link = null)
	{
		int dot = id.IndexOf('.');
		string page = dot > 0 ? id.Substring(0, dot) : id;
		page = page switch
		{
			"pointers" => "appearance/pointers",
			"frames" => "appearance/frames",
			"experiments" => "launcher/experiments",
			"diag" => "launcher/diagnostics",
			"access" => "accessibility",
			_ => page
		};
		if (SubPageRows.TryGetValue(id, out string? sub))
		{
			page = sub;
		}
		return new HubRow(id, page, card, title, titleEl, keywords, kind, link);
	}

	// Rows of Appearance that live on its drill-ins (the seasonal and the Dark rest options).
	private static readonly Dictionary<string, string> SubPageRows = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
	{
		["appearance.seasonalwallpaper"] = "appearance/seasonal",
		["appearance.seasonalpictures"] = "appearance/seasonal",
		["appearance.drawnscenes"] = "appearance/seasonal",
		["appearance.seasonaleffects"] = "appearance/seasonal",
		["appearance.seasonalpointers"] = "appearance/seasonal",
		["appearance.seasonalicons"] = "appearance/seasonal",
		["appearance.trayicons"] = "appearance/seasonal",
		["appearance.addicons"] = "appearance/seasonal",
		["appearance.restwallpaper"] = "appearance/rest",
		["appearance.restpicture"] = "appearance/rest",
		["appearance.restcalm"] = "appearance/rest",
		["appearance.restdim"] = "appearance/rest",
		["appearance.nightlight"] = "appearance/rest"
	};

	// Alternative names of the settings people search for in other words.
	private static readonly Dictionary<string, string[]> RowAliases = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
	{
		["mouse.speed"] = new[] { "Mouse speed", "Mouse pointer speed", "Ταχύτητα ποντικιού", "Ταχύτητα δείκτη ποντικιού" },
		["mouse.doubleclick"] = new[] { "Double click speed", "Ταχύτητα διπλού κλικ ποντικιού" },
		["mouse.precision"] = new[] { "Mouse acceleration", "Επιτάχυνση ποντικιού" },
		["mouse.primary"] = new[] { "Swap mouse buttons", "Left-handed mouse", "Εναλλαγή κουμπιών ποντικιού" },
		["mouse.lines"] = new[] { "Scroll speed", "Ταχύτητα κύλισης" },
		["keyboard.delay"] = new[] { "Keyboard repeat delay", "Key repeat delay", "Καθυστέρηση επανάληψης πλήκτρων" },
		["keyboard.rate"] = new[] { "Keyboard repeat rate", "Key repeat rate", "Ρυθμός επανάληψης πλήκτρων" },
		["keyboard.blink"] = new[] { "Caret blink rate" },
		["power.plan"] = new[] { "Power scheme", "Σχέδιο ενέργειας" },
		["power.screen"] = new[] { "Screen timeout", "Display timeout", "Λήξη χρόνου οθόνης" },
		["displays.brightness"] = new[] { "Screen brightness", "Φωτεινότητα οθόνης" },
		["sound.settings"] = new[] { "Volume", "Ένταση ήχου" },
		["appearance.background"] = new[] { "Wallpaper", "Ταπετσαρία", "Background" }
	};

	private static HubRow WithAliases(HubRow r)
	{
		return RowAliases.TryGetValue(r.Id, out string[]? a) ? r with { Aliases = a } : r;
	}

	private static LinkSpec P(string uri, string? file = null, string? args = null, string? parent = null) => LinkSpec.Page(uri, file, args, parent);

	private static LinkSpec C(string file, string? args = null) => LinkSpec.Cmd(file, args);

	private static List<HubRow> BuildRows()
	{
		const HubRowKind Tg = HubRowKind.Toggle;
		const HubRowKind Ch = HubRowKind.Choice;
		const HubRowKind Sl = HubRowKind.Slider;
		const HubRowKind Sg = HubRowKind.Segmented;
		const HubRowKind Dr = HubRowKind.DrillIn;
		const HubRowKind Ln = HubRowKind.Link;
		const HubRowKind In = HubRowKind.Info;
		const HubRowKind Bt = HubRowKind.Button;
		const HubRowKind Cu = HubRowKind.Custom;
		return new List<HubRow>
		{
			// ---- Home
			R("home.brightness", "Quick", "Brightness", "Φωτεινότητα", "screen display dim bright|οθόνη σκοτεινό φωτεινό", Sl),
			R("home.look", "Quick", "Look", "Όψη", "theme season halloween christmas dark rest normal|θέμα εποχή χριστούγεννα σκοτεινή ξεκούραση", Sg),
			R("home.screen", "Quick", "Turn off screen after", "Απενεργοποίηση οθόνης μετά από", "display timeout monitor sleep|οθόνη χρόνος λήξης", Ch),
			R("home.wifi", "Quick", "Wi-Fi", "Wi-Fi", "wireless network|ασύρματο δίκτυο", Tg, P("ms-settings:network-wifi")),
			R("home.favorites", "Favourites", "Favourites", "Αγαπημένα", "star starred pinned favourite favorite ctrl+d|αστέρι καρφιτσωμένα αγαπημένο", HubRowKind.Custom),
			R("home.setups", "Setups", "Setups", "Σύνολα ρυθμίσεων", "setup preset gaming desk presentation profile|σύνολο ρυθμίσεων παιχνίδια γραφείο παρουσίαση", HubRowKind.Custom),
			R("home.fix", "Fix a problem", "Fix a problem", "Επίλυση προβλήματος", "troubleshoot problem help not working|αντιμετώπιση προβλημάτων βοήθεια δεν λειτουργεί", HubRowKind.Custom),
			R("home.windows", "More", "Windows Settings", "Ρυθμίσεις των Windows", "settings app|εφαρμογή ρυθμίσεις", Ln, P("ms-settings:")),
			R("home.controlpanel", "More", "Control Panel", "Πίνακας Ελέγχου", "classic control panel|κλασικός πίνακας ελέγχου", Ln, C("control.exe")),

			// ---- Account
			R("account.picture", "Account", "Change account picture", "Αλλαγή εικόνας λογαριασμού", "avatar photo user|άβαταρ φωτογραφία χρήστης", Ln, P("ms-settings:yourinfo", "control.exe", "userpasswords")),
			R("account.yourinfo", "Account", "Your info", "Τα στοιχεία σας", "name profile|όνομα προφίλ", Ln, P("ms-settings:yourinfo")),
			R("account.signin", "Account", "Sign-in options", "Επιλογές σύνδεσης", "password pin windows hello|κωδικός πρόσβασης pin", Ln, P("ms-settings:signinoptions")),
			R("account.otherusers", "Account", "Other users", "Άλλοι χρήστες", "family accounts add user|οικογένεια λογαριασμοί προσθήκη χρήστη", Ln, P("ms-settings:otherusers")),
			R("account.netplwiz", "Account", "Advanced user accounts", "Σύνθετοι λογαριασμοί χρηστών", "netplwiz users groups autologon|χρήστες ομάδες", Ln, C("netplwiz.exe")),
			R("account.msa", "Account", "Manage my Microsoft account", "Διαχείριση λογαριασμού Microsoft", "online account|λογαριασμός online", Ln, P("https://account.microsoft.com")),

			// ---- Network
			R("network.status", "Status", "Connection", "Σύνδεση", "network status connected internet|κατάσταση δικτύου συνδεδεμένο", In, P("ms-settings:network-status", "control.exe", "/name Microsoft.NetworkAndSharingCenter")),
			R("network.ip", "Status", "IP address", "Διεύθυνση IP", "ipv4 gateway dns copy|πύλη αντιγραφή", In),
			R("network.metered", "Status", "Metered connection", "Μετρούμενη σύνδεση", "data limit metered cost|όριο δεδομένων", In),
			R("network.wifitoggle", "Wireless", "Wi-Fi", "Wi-Fi", "wireless on off wlan|ασύρματο ενεργό ανενεργό", Tg),
			R("network.allwireless", "Wireless", "Turn off all wireless", "Απενεργοποίηση όλων των ασύρματων", "radios off flight plane|ασύρματα πτήση", Tg),
			R("network.wifi", "Wireless", "Wi-Fi settings", "Ρυθμίσεις Wi-Fi", "wireless wlan known networks|ασύρματο", Ln, P("ms-settings:network-wifi")),
			R("network.ethernet", "More", "Ethernet", "Ethernet", "wired cable lan|ενσύρματο καλώδιο", Ln, P("ms-settings:network-ethernet")),
			R("network.proxy", "More", "Proxy", "Διακομιστής μεσολάβησης", "proxy server internet options|διακομιστής μεσολάβησης", Ln, P("ms-settings:network-proxy", "control.exe", "inetcpl.cpl,,4")),
			R("network.vpn", "More", "VPN", "VPN", "virtual private network|εικονικό ιδιωτικό δίκτυο", Ln, P("ms-settings:network-vpn")),
			R("network.advanced", "More", "Advanced network settings", "Σύνθετες ρυθμίσεις δικτύου", "adapters sharing reset|προσαρμογείς κοινή χρήση επαναφορά", Ln, P("ms-settings:network-advancedsettings", "ncpa.cpl")),
			R("network.adapters", "More", "Network adapters", "Προσαρμογείς δικτύου", "ncpa connections adapter|συνδέσεις προσαρμογέας", Ln, C("control.exe", "ncpa.cpl")),
			R("network.datausage", "More", "Data usage", "Χρήση δεδομένων", "traffic data limit|δεδομένα όριο", Ln, P("ms-settings:datausage", parent: "ms-settings:network-status")),

			// ---- Bluetooth & devices
			R("devices.bttoggle", "Bluetooth", "Bluetooth", "Bluetooth", "bluetooth on off radio|bluetooth ενεργό ανενεργό", Tg),
			R("devices.paired", "Bluetooth", "Paired devices", "Συζευγμένες συσκευές", "headset mouse keyboard paired|ακουστικά συζευγμένες", In),
			R("devices.bluetooth", "Bluetooth", "Bluetooth & other devices", "Bluetooth και άλλες συσκευές", "pair wireless headset add device|σύζευξη ασύρματο προσθήκη συσκευής", Ln, P("ms-settings:bluetooth")),
			R("devices.printers", "Devices", "Printers & scanners", "Εκτυπωτές και σαρωτές", "print scan|εκτύπωση σάρωση", Ln, P("ms-settings:printers", "control.exe", "printers")),
			R("devices.autoplay", "Devices", "AutoPlay", "Αυτόματη αναπαραγωγή", "usb drive media|μέσα δίσκος", Ln, P("ms-settings:autoplay", "control.exe", "/name Microsoft.AutoPlay")),
			R("devices.usb", "Devices", "USB", "USB", "usb notifications power|ειδοποιήσεις usb", Ln, P("ms-settings:usb")),
			R("devices.manager", "Devices", "Device Manager", "Διαχείριση συσκευών", "drivers hardware devmgmt|προγράμματα οδήγησης υλικό", Ln, C("devmgmt.msc")),
			R("devices.devicesprinters", "Devices", "Devices and Printers", "Συσκευές και εκτυπωτές", "control panel devices|πίνακας ελέγχου συσκευές", Ln, C("control.exe", "printers")),
			R("devices.pen", "Pen & touch", "Pen & Windows Ink", "Πένα και Windows Ink", "stylus digitizer touch|γραφίδα αφή", Ln, P("ms-settings:pen")),

			// ---- Sound
			R("sound.output", "Output", "Output device", "Συσκευή εξόδου", "speakers headphones default playback device|ηχεία ακουστικά προεπιλεγμένη συσκευή αναπαραγωγής", HubRowKind.DeviceList),
			R("sound.calls", "Output", "Also use for calls", "Χρήση και για κλήσεις", "communications device calls voip|συσκευή επικοινωνίας κλήσεις", Tg),
			R("sound.volume", "Output", "Output volume", "Ένταση εξόδου", "volume level mute loudness|ένταση σίγαση", Sl),
			R("sound.mute", "Output", "Mute", "Σίγαση", "mute silence speakers off|σίγαση σιωπή", Tg),
			R("sound.test", "Output", "Play test sound", "Δοκιμαστικός ήχος", "test speakers|δοκιμή ηχείων", Bt),
			R("sound.input", "Input", "Input device", "Συσκευή εισόδου", "microphone default recording device|μικρόφωνο προεπιλεγμένη συσκευή εγγραφής", HubRowKind.DeviceList),
			R("sound.inputvolume", "Input", "Input volume", "Ένταση εισόδου", "microphone level gain mute|μικρόφωνο στάθμη σίγαση", Sl),
			R("sound.meter", "Input", "Input level", "Στάθμη εισόδου", "microphone meter test|μετρητής μικροφώνου", Cu),
			R("sound.appvolume", "Apps", "App volume", "Ένταση εφαρμογών", "mixer per app volume|μίκτης ένταση εφαρμογής", Cu),
			R("sound.allapps", "Apps", "All apps", "Όλες οι εφαρμογές", "mixer all apps|μίκτης όλες", Dr),
			R("sound.settings", "Output", "Sound settings", "Ρυθμίσεις ήχου", "output device volume speakers|συσκευή εξόδου ένταση ηχεία", Ln, P("ms-settings:sound", "mmsys.cpl")),
			R("sound.devices", "Output", "Sound devices", "Συσκευές ήχου", "playback recording enable disable|αναπαραγωγή εγγραφή", Ln, P("ms-settings:sound-devices", parent: "ms-settings:sound")),
			R("sound.playback", "Output", "Playback devices", "Συσκευές αναπαραγωγής", "speakers headphones default device|ηχεία ακουστικά προεπιλεγμένη", Ln, C("control.exe", "mmsys.cpl,,0")),
			R("sound.recording", "Input", "Recording devices", "Συσκευές εγγραφής", "microphone input level|μικρόφωνο είσοδος", Ln, C("control.exe", "mmsys.cpl,,1")),
			R("sound.apps", "Apps", "App volume and device preferences", "Ένταση εφαρμογών και προτιμήσεις συσκευών", "mixer per app volume|μίκτης ένταση εφαρμογής", Ln, P("ms-settings:apps-volume", parent: "ms-settings:sound")),
			R("sound.win81", "Sounds", "Windows 8.1 sounds", "Ήχοι Windows 8.1", "sound scheme system sounds|ηχητικό σχέδιο ήχοι συστήματος", Tg),
			R("sound.system", "Sounds", "System sounds", "Ήχοι συστήματος", "sound scheme events beep|ηχητικό σχέδιο συμβάντα", Ln, C("control.exe", "mmsys.cpl,,2")),
			R("sound.panel", "More", "Sound Control Panel", "Πίνακας Ελέγχου ήχου", "mmsys classic sound|κλασικός ήχος", Ln, C("control.exe", "mmsys.cpl")),
			R("sound.comms", "More", "Communications", "Επικοινωνίες", "ducking calls reduce volume|κλήσεις μείωση έντασης", Ln, C("control.exe", "mmsys.cpl,,3")),
			R("sound.mono", "More", "Mono audio", "Μονοφωνικός ήχος", "mono stereo accessibility|μονοφωνικό στερεοφωνικό", Ln, P("ms-settings:easeofaccess-audio")),

			// ---- Displays
			R("displays.monitor", "Monitors", "Display", "Οθόνη", "resolution refresh rate hz monitor|ανάλυση ρυθμός ανανέωσης", In),
			R("displays.brightness", "Monitors", "Brightness", "Φωτεινότητα", "screen dim bright ddc|οθόνη σκοτεινό φωτεινό", Sl),
			R("displays.settings", "Arrangement", "Arrangement, resolution and scale", "Διάταξη, ανάλυση και κλίμακα", "multiple displays scale resolution orientation|πολλαπλές οθόνες κλίμακα ανάλυση προσανατολισμός", Ln, P("ms-settings:display")),
			R("displays.nightlight", "Arrangement", "Night light", "Νυχτερινός φωτισμός", "blue light warm evening|μπλε φως ζεστό βράδυ", Ln, P("ms-settings:nightlight", parent: "ms-settings:display")),
			R("displays.advanced", "More", "Advanced display", "Σύνθετες ρυθμίσεις οθόνης", "refresh rate hz|ρυθμός ανανέωσης", Ln, P("ms-settings:display-advanced", parent: "ms-settings:display")),
			R("displays.graphics", "More", "Graphics", "Γραφικά", "gpu preference performance|κάρτα γραφικών προτίμηση", Ln, P("ms-settings:display-advancedgraphics", parent: "ms-settings:display")),
			R("displays.textsize", "More", "Text size", "Μέγεθος κειμένου", "font size larger text|μέγεθος γραμματοσειράς", Ln, P("ms-settings:easeofaccess-display")),
			R("displays.cleartype", "More", "ClearType text", "Κείμενο ClearType", "font smoothing sharp text|εξομάλυνση γραμματοσειράς", Ln, C("cttune.exe")),
			R("displays.color", "More", "Colour management", "Διαχείριση χρωμάτων", "color profile icc calibration|προφίλ χρώματος βαθμονόμηση", Ln, C("colorcpl.exe")),

			// ---- Power
			R("power.plan", "Plan", "Power plan", "Σχέδιο παροχής ενέργειας", "balanced high performance power saver ultimate|ισορροπημένο υψηλή απόδοση εξοικονόμηση", Ch, C("control.exe", "powercfg.cpl")),
			R("power.allplans", "Plan", "All plans", "Όλα τα σχέδια", "custom plans list|προσαρμοσμένα σχέδια", Dr),
			R("power.screen", "Screen & sleep", "Turn off screen after", "Απενεργοποίηση οθόνης μετά από", "display timeout monitor off|οθόνη χρόνος λήξης", Ch, P("ms-settings:powersleep", "control.exe", "powercfg.cpl")),
			R("power.screen.dc", "Screen & sleep", "On battery, turn off screen after", "Με μπαταρία, απενεργοποίηση οθόνης μετά από", "battery display timeout|μπαταρία οθόνη", Ch, P("ms-settings:powersleep", "control.exe", "powercfg.cpl")),
			R("power.sleep", "Screen & sleep", "Sleep after", "Αναστολή λειτουργίας μετά από", "standby sleep timeout|αναμονή αναστολή", Ch, P("ms-settings:powersleep", "control.exe", "powercfg.cpl")),
			R("power.sleep.dc", "Screen & sleep", "On battery, sleep after", "Με μπαταρία, αναστολή μετά από", "battery standby timeout|μπαταρία αναμονή", Ch, P("ms-settings:powersleep", "control.exe", "powercfg.cpl")),
			R("power.button", "Buttons", "Power button", "Κουμπί λειτουργίας", "power button action shut down|ενέργεια κουμπιού τερματισμός", Ch, C("control.exe", "/name Microsoft.PowerOptions /page pageGlobalSettings")),
			R("power.sleepbutton", "Buttons", "Sleep button", "Κουμπί αναστολής", "sleep button action|ενέργεια κουμπιού αναστολής", Ch, C("control.exe", "/name Microsoft.PowerOptions /page pageGlobalSettings")),
			R("power.lid", "Buttons", "Closing the lid", "Κλείσιμο καπακιού", "lid close laptop|καπάκι φορητός", Ch, C("control.exe", "/name Microsoft.PowerOptions /page pageGlobalSettings")),
			R("power.autolock", "Launcher", "Lock after inactivity", "Κλείδωμα μετά από αδράνεια", "auto lock idle|αυτόματο κλείδωμα αδράνεια", Ch),
			R("power.faststartup", "Info", "Fast startup", "Γρήγορη εκκίνηση", "hiberboot boot|εκκίνηση", In),
			R("power.hibernate", "Info", "Hibernate", "Αδρανοποίηση", "hibernation|αδρανοποίηση", In),
			R("power.options", "More", "Power Options", "Επιλογές παροχής ενέργειας", "power plans control panel|σχέδια ενέργειας", Ln, C("control.exe", "powercfg.cpl")),
			R("power.buttons", "More", "Choose what the power buttons do", "Επιλέξτε τι κάνουν τα κουμπιά λειτουργίας", "power button lid sleep button|κουμπί λειτουργίας καπάκι", Ln, C("control.exe", "/name Microsoft.PowerOptions /page pageGlobalSettings")),
			R("power.planedit", "More", "Edit plan settings", "Επεξεργασία ρυθμίσεων σχεδίου", "advanced power settings|σύνθετες ρυθμίσεις ενέργειας", Ln, C("control.exe", "/name Microsoft.PowerOptions /page pagePlanSettings")),

			// ---- Mouse
			R("mouse.speed", "Pointer", "Pointer speed", "Ταχύτητα δείκτη", "mouse cursor sensitivity fast slow|ποντίκι κέρσορας ευαισθησία γρήγορα αργά", Sl, C("control.exe", "main.cpl,,2")),
			R("mouse.precision", "Pointer", "Enhance pointer precision", "Βελτίωση ακρίβειας δείκτη", "acceleration mouse gaming|επιτάχυνση ποντίκι", Tg, C("control.exe", "main.cpl,,2")),
			R("mouse.primary", "Pointer", "Primary button", "Κύριο κουμπί", "left handed right swap buttons|αριστερόχειρας δεξί εναλλαγή κουμπιών", Sg, C("control.exe", "main.cpl,,0")),
			R("mouse.doubleclick", "Clicking", "Double-click speed", "Ταχύτητα διπλού κλικ", "double click delay|διπλό κλικ καθυστέρηση", Sl, C("control.exe", "main.cpl,,0")),
			R("mouse.clicklock", "Clicking", "ClickLock", "ClickLock", "drag without holding|σύρσιμο χωρίς κράτημα", Tg, C("control.exe", "main.cpl,,0")),
			R("mouse.clicklocktime", "Clicking", "ClickLock time", "Χρόνος ClickLock", "hold time lock|χρόνος κράτησης", Sl, C("control.exe", "main.cpl,,0")),
			R("mouse.wheel", "Scrolling", "Roll the wheel to scroll", "Κύλιση με τη ροδέλα", "scroll wheel lines screen|κύλιση ροδέλα γραμμές", Ch, C("control.exe", "main.cpl,,3")),
			R("mouse.lines", "Scrolling", "Lines per notch", "Γραμμές ανά βήμα", "scroll lines wheel|κύλιση γραμμές ροδέλα", Sl, C("control.exe", "main.cpl,,3")),
			R("mouse.hchars", "Scrolling", "Horizontal scrolling", "Οριζόντια κύλιση", "characters tilt wheel|χαρακτήρες", Sl, C("control.exe", "main.cpl,,3")),
			R("mouse.inactive", "Scrolling", "Scroll inactive windows", "Κύλιση ανενεργών παραθύρων", "hover scroll background windows|κύλιση ανενεργά παράθυρα", Ln, P("ms-settings:mousetouchpad", "control.exe", "main.cpl")),
			R("mouse.ctrl", "Visibility", "Show location with Ctrl", "Εμφάνιση θέσης με Ctrl", "find pointer ctrl key|εύρεση δείκτη", Tg, C("control.exe", "main.cpl,,2")),
			R("mouse.hidetyping", "Visibility", "Hide pointer while typing", "Απόκρυψη δείκτη κατά την πληκτρολόγηση", "hide cursor typing|απόκρυψη κέρσορα", Tg, C("control.exe", "main.cpl,,2")),
			R("mouse.trails", "Visibility", "Pointer trails", "Ίχνη δείκτη", "mouse trails|ίχνη ποντικιού", Ch, C("control.exe", "main.cpl,,2")),
			R("mouse.snap", "Visibility", "Snap to default button", "Μετάβαση στο προεπιλεγμένο κουμπί", "snap to dialog button|κουμπί παραθύρου", Tg, C("control.exe", "main.cpl,,2")),
			R("mouse.style", "Style", "Pointer style", "Στυλ δείκτη", "cursor scheme alchemy windows 8.1 pointers|σχήμα δείκτη κέρσορες", Dr),
			R("mouse.properties", "More", "Mouse Properties", "Ιδιότητες ποντικιού", "main.cpl classic mouse|κλασικό ποντίκι", Ln, C("control.exe", "main.cpl")),
			R("mouse.size", "More", "Pointer size and colour", "Μέγεθος και χρώμα δείκτη", "large cursor color accessibility|μεγάλος κέρσορας χρώμα", Ln, P("ms-settings:easeofaccess-mousepointer", "control.exe", "main.cpl,,1")),
			R("mouse.keys", "More", "Mouse Keys", "Πλήκτρα ποντικιού", "numeric keypad move pointer|αριθμητικό πληκτρολόγιο", Ln, P("ms-settings:easeofaccess-mouse", "control.exe", "/name Microsoft.EaseOfAccessCenter /page pageEasierToUseMouse")),

			// ---- Keyboard
			R("keyboard.delay", "Repeat", "Repeat delay", "Καθυστέρηση επανάληψης", "keyboard key repeat delay hold|πληκτρολόγιο επανάληψη πλήκτρου κράτημα", Sl, C("control.exe", "keyboard")),
			R("keyboard.rate", "Repeat", "Repeat rate", "Ρυθμός επανάληψης", "keyboard key repeat speed|πληκτρολόγιο ταχύτητα επανάληψης πλήκτρου", Sl, C("control.exe", "keyboard")),
			R("keyboard.caretwidth", "Text cursor", "Text cursor width", "Πλάτος δρομέα κειμένου", "caret thickness|πάχος δρομέα", Sl, P("ms-settings:easeofaccess-cursor", "control.exe", "keyboard")),
			R("keyboard.blink", "Text cursor", "Cursor blink rate", "Ρυθμός αναβοσβησίματος δρομέα", "caret blink|αναβόσβημα δρομέα", In, C("control.exe", "keyboard")),
			R("keyboard.languages", "Input languages", "Input languages", "Γλώσσες εισαγωγής", "layout greek english keyboard layout|διάταξη ελληνικά αγγλικά", In, P("ms-settings:regionlanguage", "control.exe", "intl.cpl")),
			R("keyboard.switch", "Input languages", "Switch input language", "Εναλλαγή γλώσσας εισαγωγής", "alt shift ctrl shift hotkey|συντόμευση εναλλαγής", In, P("ms-settings:typing")),
			R("keyboard.access", "Assistance", "Underline access keys", "Υπογράμμιση πλήκτρων πρόσβασης", "menu underline alt|υπογράμμιση μενού", Tg, P("ms-settings:easeofaccess-keyboard")),
			R("keyboard.sticky", "Assistance", "Sticky, Filter and Toggle Keys", "Ασύγχρονα, φιλτραρίσματος και εναλλαγής πλήκτρα", "accessibility keys|πλήκτρα προσβασιμότητας", Dr),
			R("keyboard.osk", "Assistance", "On-screen keyboard", "Πληκτρολόγιο οθόνης", "osk virtual keyboard|εικονικό πληκτρολόγιο", Bt),
			R("keyboard.typing", "More", "Typing", "Πληκτρολόγηση", "autocorrect suggestions|αυτόματη διόρθωση προτάσεις", Ln, P("ms-settings:typing")),
			R("keyboard.properties", "More", "Keyboard Properties", "Ιδιότητες πληκτρολογίου", "classic keyboard repeat|κλασικό πληκτρολόγιο", Ln, C("control.exe", "keyboard")),
			R("keyboard.region", "More", "Language & keyboards", "Γλώσσα και πληκτρολόγια", "add keyboard layout language|προσθήκη διάταξης γλώσσα", Ln, P("ms-settings:regionlanguage", "control.exe", "intl.cpl")),

			// ---- Touchpad
			R("touchpad.settings", "Touchpad", "Touchpad", "Επιφάνεια αφής", "trackpad gestures|χειρονομίες", Ln, P("ms-settings:devices-touchpad")),
			R("touchpad.pen", "Touchpad", "Pen & Windows Ink", "Πένα και Windows Ink", "stylus|γραφίδα", Ln, P("ms-settings:pen")),

			// ---- Appearance
			R("appearance.look", "Look", "Look", "Όψη", "season halloween christmas dark rest normal theme|εποχή χριστούγεννα σκοτεινή ξεκούραση θέμα", Cu),
			R("appearance.seasonal", "Look", "Seasonal options", "Επιλογές εποχών", "halloween christmas wallpaper pictures effects icons|αποκριές χριστούγεννα ταπετσαρία εικόνες εφέ εικονίδια", Dr),
			R("appearance.rest", "Look", "Dark rest options", "Επιλογές σκοτεινής ξεκούρασης", "dark rest wallpaper calm dim night light|σκοτεινή ξεκούραση ταπετσαρία ήρεμο θαμπό", Dr),
			R("appearance.seasonalwallpaper", "Seasonal", "Seasonal desktop wallpaper", "Εποχική ταπετσαρία", "season wallpaper|εποχή ταπετσαρία", Tg),
			R("appearance.seasonalpictures", "Seasonal", "Seasonal desktop pictures", "Εποχικές εικόνες επιφάνειας εργασίας", "custom picture season|εικόνα εποχή", Ln),
			R("appearance.drawnscenes", "Seasonal", "Use the drawn scenes", "Χρήση των σχεδιασμένων σκηνών", "reset pictures|επαναφορά εικόνων", Bt),
			R("appearance.seasonaleffects", "Seasonal", "Seasonal effects on Start", "Εποχικά εφέ στην Έναρξη", "snow bats ambient|χιόνι νυχτερίδες", Tg),
			R("appearance.seasonalpointers", "Seasonal", "Seasonal pointer colours", "Εποχικά χρώματα δείκτη", "cursor colours season|χρώματα κέρσορα εποχή", Tg),
			R("appearance.seasonalicons", "Seasonal", "Seasonal app icons", "Εποχικά εικονίδια εφαρμογών", "icons season|εικονίδια εποχή", Tg),
			R("appearance.trayicons", "Seasonal", "Seasonal tray icons", "Εποχικά εικονίδια περιοχής ειδοποιήσεων", "tray notification area icons|περιοχή ειδοποιήσεων", Ch),
			R("appearance.addicons", "Seasonal", "Add your own seasonal icons", "Προσθήκη δικών σας εποχικών εικονιδίων", "custom icons folder|φάκελος εικονιδίων", Ln),
			R("appearance.restwallpaper", "Dark rest", "Dark rest desktop wallpaper", "Ταπετσαρία σκοτεινής ξεκούρασης", "dark wallpaper|σκοτεινή ταπετσαρία", Tg),
			R("appearance.restpicture", "Dark rest", "Dark rest desktop picture", "Εικόνα σκοτεινής ξεκούρασης", "custom picture|εικόνα", Ln),
			R("appearance.restcalm", "Dark rest", "Calmer animations", "Πιο ήρεμες κινήσεις", "reduced motion calm|μειωμένη κίνηση", Tg),
			R("appearance.restdim", "Dark rest", "Dim Start tiles", "Θαμπά πλακίδια Έναρξης", "dim tiles glare|θάμπωμα πλακιδίων", Tg),
			R("appearance.nightlight", "Dark rest", "Night light", "Νυχτερινός φωτισμός", "blue light warm|μπλε φως", Ln, P("ms-settings:nightlight", parent: "ms-settings:display")),
			R("appearance.accent", "Colour", "Accent colour", "Χρώμα έμφασης", "accent colour color tile|χρώμα έμφασης πλακίδια", Cu),
			R("appearance.frameaccent", "Colour", "Use a different colour for window frames", "Διαφορετικό χρώμα για τα πλαίσια παραθύρων", "window border caption colour|περίγραμμα παραθύρου", Tg),
			R("appearance.background", "Pictures", "Desktop background", "Φόντο επιφάνειας εργασίας", "wallpaper background picture desktop slideshow|ταπετσαρία φόντο εικόνα επιφάνεια εργασίας", Ln, P("ms-settings:personalization-background", "control.exe", "/name Microsoft.Personalization /page pageWallpaper")),
			R("appearance.wallpaper81", "Pictures", "Windows 8.1 desktop picture", "Εικόνα επιφάνειας εργασίας Windows 8.1", "wallpaper background|ταπετσαρία φόντο", Tg),
			R("appearance.lockscreen", "Pictures", "Lock screen", "Οθόνη κλειδώματος", "lock screen picture background|εικόνα οθόνης κλειδώματος", Cu, P("ms-settings:lockscreen")),
			R("appearance.accountpicture", "Pictures", "Account picture", "Εικόνα λογαριασμού", "avatar user picture|άβαταρ", Ln, P("ms-settings:yourinfo", "control.exe", "userpasswords")),
			R("appearance.pointers", "Pointers", "Pointers", "Δείκτες", "cursor scheme alchemy|σχήμα δείκτη κέρσορας", Dr),
			R("appearance.shellicons", "Icons & sounds", "Windows 8.1 shell icons", "Εικονίδια κελύφους Windows 8.1", "this pc recycle bin icons|εικονίδια κάδος", Tg),
			R("appearance.appicons", "Icons & sounds", "Windows 8.1 app icons", "Εικονίδια εφαρμογών Windows 8.1", "office 2013 tile icons|εικονίδια πλακιδίων", Tg),
			R("appearance.sounds81", "Icons & sounds", "Windows 8.1 sounds", "Ήχοι Windows 8.1", "sound scheme|ηχητικό σχέδιο", Tg),
			R("appearance.motion", "Motion", "Motion", "Κίνηση", "animations speed reduced off|κινήσεις ταχύτητα μειωμένη", Ch),
			R("appearance.frames", "Window frames", "Window frames", "Πλαίσια παραθύρων", "composition aero glass transparency|σύνθεση διαφάνεια", Dr),
			R("appearance.wincolors", "More", "Windows colours", "Χρώματα των Windows", "dark mode light mode accent|σκοτεινή φωτεινή λειτουργία", Ln, P("ms-settings:colors")),
			R("appearance.themes", "More", "Themes", "Θέματα", "desktop themes|θέματα επιφάνειας εργασίας", Ln, P("ms-settings:themes", "control.exe", "/name Microsoft.Personalization")),
			R("pointers.scheme", "Pointers", "Pointer style", "Στυλ δείκτη", "cursor scheme alchemy windows 8.1|σχήμα κέρσορα", Ch),
			R("frames.mode", "Mode", "Composition mode", "Λειτουργία σύνθεσης", "aero glass flat native|γυαλί επίπεδο", Cu),
			R("frames.transparency", "Options", "Transparency effects", "Εφέ διαφάνειας", "glass translucent|διαφάνεια", Tg),
			R("frames.shadows", "Options", "Window shadows", "Σκιές παραθύρων", "drop shadow|σκιά", Tg),
			R("frames.optimize", "Options", "Optimize for performance", "Βελτιστοποίηση για απόδοση", "reduce effects|λιγότερα εφέ", Tg),
			R("frames.blur", "Options", "Shell tint opacity", "Αδιαφάνεια απόχρωσης", "blur intensity tint|θόλωμα", Sl),
			R("frames.caption", "Options", "8.1 window caption: authentic light", "Λευκή γραμμή τίτλου 8.1", "title bar white caption|γραμμή τίτλου", Tg),

			// ---- Desktop & taskbar
			R("taskbar.position", "Taskbar", "Taskbar position", "Θέση γραμμής εργασιών", "bottom top left right|κάτω πάνω αριστερά δεξιά", Ch),
			R("taskbar.size", "Taskbar", "Taskbar size", "Μέγεθος γραμμής εργασιών", "small medium large|μικρό μεσαίο μεγάλο", Ch),
			R("taskbar.alignment", "Taskbar", "Taskbar alignment", "Στοίχιση γραμμής εργασιών", "center left icons|κέντρο αριστερά", Ch),
			R("taskbar.combine", "Taskbar", "Combine taskbar buttons", "Συνδυασμός κουμπιών", "group buttons labels|ομαδοποίηση κουμπιών", Ch),
			R("taskbar.color", "Taskbar", "Taskbar colour", "Χρώμα γραμμής εργασιών", "transparent wallpaper colour|διάφανη χρώμα", Ch),
			R("taskbar.autohide", "Taskbar", "Auto-hide the taskbar", "Αυτόματη απόκρυψη", "hide taskbar|απόκρυψη γραμμής εργασιών", Tg),
			R("taskbar.lock", "Taskbar", "Lock the taskbar", "Κλείδωμα γραμμής εργασιών", "lock move resize|κλείδωμα μετακίνηση", Tg),
			R("taskbar.perfmon", "Taskbar", "Performance monitor", "Παρακολούθηση απόδοσης", "cpu ram network meter|επεξεργαστής μνήμη δίκτυο", Tg),
			R("taskbar.alldisplays", "Taskbar", "Same taskbar on all displays", "Ίδια γραμμή σε όλες τις οθόνες", "multiple monitors taskbar|πολλαπλές οθόνες", Tg),
			R("taskbar.searchbtn", "Buttons", "Show Search button", "Κουμπί αναζήτησης", "search button|κουμπί αναζήτησης", Tg),
			R("taskbar.taskviewbtn", "Buttons", "Show Task view button", "Κουμπί προβολής εργασιών", "task view|προβολή εργασιών", Tg),
			R("taskbar.acbtn", "Buttons", "Show Action center button", "Κουμπί κέντρου ενεργειών", "action center quick settings|κέντρο ενεργειών", Tg),
			R("taskbar.acpos", "Buttons", "Action center position", "Θέση κέντρου ενεργειών", "clock action center|ρολόι", Ch),
			R("taskbar.hotcorners", "Corners & edges", "Hot corners", "Ενεργές γωνίες", "charms start corner|γωνίες", Tg),
			R("taskbar.switcheredge", "Corners & edges", "App switcher edge reveal", "Εναλλαγή εφαρμογών από την άκρη", "left edge switcher|αριστερή άκρη", Tg),
			R("taskbar.screenshot", "Corners & edges", "Screenshot app", "Εφαρμογή στιγμιότυπων", "share charm screenshot|στιγμιότυπο οθόνης", Ln),
			R("taskbar.windows", "More", "Windows taskbar settings", "Ρυθμίσεις γραμμής εργασιών των Windows", "native taskbar|εγγενής γραμμή εργασιών", Ln, P("ms-settings:taskbar")),

			// ---- Start & search
			R("start.style", "Start", "Start style", "Στυλ Έναρξης", "windows 7 menu start screen metro|μενού windows 7 οθόνη έναρξης", Ch),
			R("start.boot", "Start", "Open Start at launch", "Άνοιγμα Έναρξης κατά την εκκίνηση", "boot to start|εκκίνηση", Tg),
			R("start.tiles", "Start", "Tile size", "Μέγεθος πλακιδίων", "tile density compact comfortable|πυκνότητα πλακιδίων", Ch),
			R("start.packed", "Start", "Packed grid", "Πυκνό πλέγμα", "auto arrange tiles gaps|αυτόματη τακτοποίηση", Tg),
			R("start.background", "Background", "Start background", "Φόντο Έναρξης", "start wallpaper picture pattern|φόντο εικόνα μοτίβο", Cu),
			R("start.bgcolor", "Background", "Background colour", "Χρώμα φόντου", "start colour|χρώμα έναρξης", Cu),
			R("start.weathercity", "Live tiles", "Weather tile city", "Πόλη πλακιδίου καιρού", "weather location city|καιρός πόλη τοποθεσία", Cu),
			R("start.weatherunits", "Live tiles", "Weather temperature unit", "Μονάδα θερμοκρασίας", "celsius fahrenheit|κελσίου φαρενάιτ", Ch),
			R("start.news", "Live tiles", "News source", "Πηγή ειδήσεων", "rss atom feed news|ειδήσεις", Ch),
			R("start.newsfeed", "Live tiles", "Feed address", "Διεύθυνση ροής", "rss atom feed url address news|διεύθυνση ροής ειδήσεις", Cu),
			R("start.newsapp", "Live tiles", "News app", "Εφαρμογή ειδήσεων", "open news pin tile|άνοιγμα ειδήσεων καρφίτσωμα", Bt),
			R("start.google", "Live tiles", "Google (Mail & Agenda)", "Google (Αλληλογραφία και Ημερολόγιο)", "gmail calendar sign in|ημερολόγιο σύνδεση", Bt),
			R("start.searchengine", "Search", "Web search engine", "Μηχανή αναζήτησης", "bing google|αναζήτηση", Ch),
			R("start.browser", "Search", "Open web results in", "Άνοιγμα αποτελεσμάτων web σε", "browser metrobrowser default|πρόγραμμα περιήγησης", Ch),
			R("start.explore", "Search", "Explore a place", "Εξερεύνηση τοποθεσίας", "maps places|χάρτες τοποθεσίες", Bt),
			R("start.windows", "More", "Windows Start settings", "Ρυθμίσεις Έναρξης των Windows", "start menu native|εγγενές μενού", Ln, P("ms-settings:personalization-start")),

			// ---- Notifications
			R("notifications.state", "Windows notifications", "Windows notifications", "Ειδοποιήσεις των Windows", "toast banners on off|μηνύματα", In, P("ms-settings:notifications")),
			R("notifications.center", "Windows notifications", "Open notification centre", "Άνοιγμα κέντρου ειδοποιήσεων", "win+n notification center|κέντρο ειδοποιήσεων", Bt),
			R("notifications.settings", "Windows notifications", "Notification settings", "Ρυθμίσεις ειδοποιήσεων", "do not disturb apps senders|μην ενοχλείτε", Ln, P("ms-settings:notifications")),
			R("notifications.duration", "Windows notifications", "Show notifications for", "Εμφάνιση ειδοποιήσεων για", "notification duration seconds|διάρκεια ειδοποίησης", Dr),

			// ---- Accessibility
			R("access.textsize", "Vision", "Text size", "Μέγεθος κειμένου", "bigger text font|μεγαλύτερο κείμενο", Ln, P("ms-settings:easeofaccess-display")),
			R("access.colorfilters", "Vision", "Colour filters", "Φίλτρα χρώματος", "color blind grayscale|αχρωματοψία κλίμακα του γκρι", Ln, P("ms-settings:easeofaccess-colorfilter")),
			R("access.highcontrast", "Vision", "High contrast", "Υψηλή αντίθεση", "contrast themes|θέματα αντίθεσης", Ln, P("ms-settings:easeofaccess-highcontrast", "control.exe", "/name Microsoft.EaseOfAccessCenter")),
			R("access.magnifier", "Vision", "Magnifier", "Μεγεθυντικός φακός", "zoom magnify|μεγέθυνση", Bt),
			R("access.animations", "Vision", "Animation effects", "Εφέ κίνησης", "windows animations motion|κινήσεις", Tg, P("ms-settings:easeofaccess-visualeffects", parent: "ms-settings:easeofaccess-display")),
			R("access.reducemotion", "Vision", "Reduce motion everywhere", "Μείωση κίνησης παντού", "reduce animations calm launcher windows|λιγότερες κινήσεις", Tg),
			R("access.mono", "Hearing", "Mono audio", "Μονοφωνικός ήχος", "mono|μονοφωνικό", Ln, P("ms-settings:easeofaccess-audio")),
			R("access.duration", "Hearing", "Show notifications for", "Εμφάνιση ειδοποιήσεων για", "notification duration message|διάρκεια ειδοποίησης", Ch, P("ms-settings:easeofaccess-visualeffects", parent: "ms-settings:easeofaccess-display")),
			R("access.sticky", "Interaction", "Sticky Keys", "Ασύγχρονα πλήκτρα", "shortcuts one key at a time|συντομεύσεις ένα πλήκτρο", Tg, P("ms-settings:easeofaccess-keyboard")),
			R("access.filter", "Interaction", "Filter Keys", "Πλήκτρα φιλτραρίσματος", "ignore brief repeated keystrokes|αγνόηση επαναλήψεων", Tg, P("ms-settings:easeofaccess-keyboard")),
			R("access.toggle", "Interaction", "Toggle Keys", "Πλήκτρα εναλλαγής", "caps lock tone|τόνος caps lock", Tg, P("ms-settings:easeofaccess-keyboard")),
			R("access.caret", "Interaction", "Text cursor width", "Πλάτος δρομέα κειμένου", "caret|δρομέας", Dr),
			R("access.narrator", "Interaction", "Narrator", "Αφηγητής", "screen reader read aloud|ανάγνωση οθόνης", Bt),
			R("access.center", "More", "Ease of Access Center", "Κέντρο Ευκολίας Πρόσβασης", "accessibility control panel|πίνακας ελέγχου", Ln, C("control.exe", "/name Microsoft.EaseOfAccessCenter")),

			// ---- Time & language
			R("time.now", "Clock", "Date and time", "Ημερομηνία και ώρα", "clock today|ρολόι σήμερα", In, P("ms-settings:dateandtime", "control.exe", "timedate.cpl")),
			R("time.24h", "Clock", "24-hour clock", "Ρολόι 24 ωρών", "time format am pm|μορφή ώρας", Tg, P("ms-settings:regionformatting", "control.exe", "intl.cpl")),
			R("time.shortdate", "Clock", "Short date", "Σύντομη ημερομηνία", "date format|μορφή ημερομηνίας", Ch, P("ms-settings:regionformatting", "control.exe", "intl.cpl")),
			R("time.firstday", "Clock", "First day of week", "Πρώτη ημέρα της εβδομάδας", "monday sunday week|δευτέρα κυριακή εβδομάδα", Ch, P("ms-settings:regionformatting", "control.exe", "intl.cpl")),
			R("time.measure", "Clock", "Measurement system", "Σύστημα μέτρησης", "metric us units|μετρικό μονάδες", Ch, P("ms-settings:regionformatting", "control.exe", "intl.cpl")),
			R("time.zone", "Time zone", "Time zone", "Ζώνη ώρας", "utc gmt timezone|ζώνη ώρας", In, P("ms-settings:dateandtime", "control.exe", "timedate.cpl")),
			R("time.clocks", "Time zone", "Additional clocks", "Πρόσθετα ρολόγια", "second clock world clock|δεύτερο ρολόι", Ln, C("control.exe", "timedate.cpl,,1")),
			R("time.display", "Language", "Windows display language", "Γλώσσα εμφάνισης των Windows", "ui language english greek|γλώσσα εμφάνισης", In),
			R("time.region", "Language", "Language & region", "Γλώσσα και περιοχή", "region country formats|περιοχή χώρα μορφές", Ln, P("ms-settings:regionlanguage", "control.exe", "intl.cpl")),
			R("time.admin", "Language", "Administrative language settings", "Ρυθμίσεις γλώσσας διαχείρισης", "non-unicode programs system locale|μη unicode προγράμματα", Ln, C("control.exe", "intl.cpl,,2")),
			R("time.speech", "Language", "Speech", "Ομιλία", "voice recognition|αναγνώριση φωνής", Ln, P("ms-settings:speech")),

			// ---- Apps
			R("apps.defaults", "Default apps", "Default apps", "Προεπιλεγμένες εφαρμογές", "browser email pdf photos music video default|πρόγραμμα περιήγησης email προεπιλογή", Ln, P("ms-settings:defaultapps")),
			R("apps.default.browser", "Default apps", "Web browser", "Πρόγραμμα περιήγησης", "default browser http|προεπιλεγμένο πρόγραμμα περιήγησης", In, P("ms-settings:defaultapps")),
			R("apps.default.email", "Default apps", "Email", "Email", "default mail mailto|προεπιλεγμένο ηλεκτρονικό ταχυδρομείο", In, P("ms-settings:defaultapps")),
			R("apps.default.pdf", "Default apps", "PDF", "PDF", "pdf reader|πρόγραμμα ανάγνωσης pdf", In, P("ms-settings:defaultapps")),
			R("apps.default.photos", "Default apps", "Photos", "Φωτογραφίες", "image viewer jpg|προβολή εικόνων", In, P("ms-settings:defaultapps")),
			R("apps.default.music", "Default apps", "Music", "Μουσική", "music player mp3|αναπαραγωγή μουσικής", In, P("ms-settings:defaultapps")),
			R("apps.default.video", "Default apps", "Video", "Βίντεο", "video player mp4|αναπαραγωγή βίντεο", In, P("ms-settings:defaultapps")),
			R("apps.defaultprograms", "Default apps", "Default Programs", "Προεπιλεγμένα προγράμματα", "file associations|συσχετίσεις αρχείων", Ln, C("control.exe", "/name Microsoft.DefaultPrograms")),
			R("apps.startuplist", "Startup", "Apps that start with Windows", "Εφαρμογές που ξεκινούν με τα Windows", "startup run registry folder|εκκίνηση μητρώο φάκελος", In),
			R("apps.startup", "Startup", "Startup apps", "Εφαρμογές εκκίνησης", "run at sign in autostart|αυτόματη εκκίνηση", Ln, P("ms-settings:startupapps", "taskmgr.exe", "/0 /startup")),
			R("apps.installed", "Apps", "Installed apps", "Εγκατεστημένες εφαρμογές", "uninstall programs features|απεγκατάσταση προγράμματα", Ln, P("ms-settings:installed-apps", "control.exe", "appwiz.cpl")),
			R("apps.optional", "Apps", "Optional features", "Προαιρετικές δυνατότητες", "windows features add|δυνατότητες windows", Ln, P("ms-settings:optionalfeatures")),
			R("apps.extensions", "Files & Explorer", "Show file extensions", "Εμφάνιση επεκτάσεων αρχείων", "file name extensions explorer|επεκτάσεις ονομάτων", Tg, C("control.exe", "folders")),
			R("apps.hidden", "Files & Explorer", "Show hidden files", "Εμφάνιση κρυφών αρχείων", "hidden files folders explorer|κρυφά αρχεία φάκελοι", Tg, C("control.exe", "folders")),

			// ---- Privacy
			R("privacy.security", "Security", "Windows Security", "Ασφάλεια των Windows", "defender antivirus firewall|antivirus τείχος προστασίας", Ln, P("windowsdefender:")),

			// ---- About
			R("about.device", "Device", "Device specifications", "Προδιαγραφές συσκευής", "processor ram memory edition version build|επεξεργαστής μνήμη έκδοση", In, P("ms-settings:about", "msinfo32.exe")),
			R("about.gpu", "Device", "Graphics", "Γραφικά", "gpu graphics card video adapter|κάρτα γραφικών", In),
			R("about.displays", "Device", "Displays", "Οθόνες", "monitors resolution|οθόνες ανάλυση", In),
			R("about.copy", "Device", "Copy specs", "Αντιγραφή προδιαγραφών", "copy system info|αντιγραφή πληροφοριών", Bt),
			R("about.rename", "Device", "Rename this PC", "Μετονομασία υπολογιστή", "computer name hostname|όνομα υπολογιστή", Ln, C("SystemPropertiesComputerName.exe")),
			R("about.storage", "Storage", "Storage", "Αποθήκευση", "disk space drives free|χώρος δίσκου", In, P("ms-settings:storagesense", "cleanmgr.exe")),
			R("about.cleanup", "Storage", "Disk Cleanup", "Εκκαθάριση δίσκου", "free space temporary files|ελεύθερος χώρος", Ln, C("cleanmgr.exe")),
			R("about.recycle", "Storage", "Recycle Bin size", "Μέγεθος Κάδου Ανακύκλωσης", "deleted files trash size|μέγεθος κάδου", In),
			R("about.recyclebin", "Storage", "Open Recycle Bin", "Άνοιγμα Κάδου Ανακύκλωσης", "deleted files trash|διαγραμμένα αρχεία κάδος", Ln, P("shell:RecycleBinFolder")),
			R("about.diskmgmt", "Storage", "Disk Management", "Διαχείριση δίσκων", "partitions volumes|διαμερίσματα", Ln, C("diskmgmt.msc")),
			R("about.update", "Windows Update", "Windows Update", "Windows Update", "updates patches|ενημερώσεις", Ln, P("ms-settings:windowsupdate")),
			R("about.updatehistory", "Windows Update", "Update history", "Ιστορικό ενημερώσεων", "installed updates|εγκατεστημένες ενημερώσεις", Ln, P("ms-settings:windowsupdate-history", parent: "ms-settings:windowsupdate")),
			R("about.recovery", "Recovery", "Recovery options", "Επιλογές αποκατάστασης", "reset this pc advanced startup refresh|επαναφορά σύνθετη εκκίνηση", Ln, P("ms-settings:recovery")),
			R("about.protection", "Recovery", "System protection", "Προστασία συστήματος", "restore point system restore|σημείο επαναφοράς", Ln, C("SystemPropertiesProtection.exe")),
			R("about.activation", "Recovery", "Activation", "Ενεργοποίηση", "product key license|κλειδί προϊόντος άδεια", Ln, P("ms-settings:activation")),

			// ---- Launcher
			R("launcher.logon", "Startup", "Start at sign-in", "Έναρξη κατά τη σύνδεση", "autostart logon task|αυτόματη εκκίνηση", Tg),
			R("launcher.runkey", "Startup", "Also add a Run-key entry", "Επιπλέον καταχώριση Run", "registry run autostart|μητρώο", Tg),
			R("launcher.safemode", "Startup", "Safe mode", "Ασφαλής λειτουργία", "safe mode recovery|ασφαλής λειτουργία", In),
			R("launcher.dominant", "Shell", "Dominant mode", "Κυρίαρχη λειτουργία", "takeover shell replace|αντικατάσταση κελύφους", Tg),
			R("launcher.taskbar", "Shell", "Win8.1 taskbar", "Γραμμή εργασιών Win8.1", "replace taskbar|αντικατάσταση γραμμής", Tg),
			R("launcher.suspend", "Shell", "Suspend native Start host", "Αναστολή εγγενούς Έναρξης", "startmenuexperiencehost|αναστολή", Tg),
			R("launcher.replacestart", "Shell", "Replace the Start button", "Αντικατάσταση κουμπιού Έναρξης", "windows key start button|πλήκτρο windows", Tg),
			R("launcher.lightrender", "Performance", "Light rendering on other monitors", "Ελαφριά απόδοση σε άλλες οθόνες", "software rendering memory second monitor|μνήμη δεύτερη οθόνη", Tg),
			R("launcher.softwarerender", "Performance", "Lighter rendering", "Ελαφρύτερη απεικόνιση", "software rendering memory gpu graphics card restart lighter|λογισμικό μνήμη κάρτα γραφικών επανεκκίνηση ελαφρύ", Tg),
			R("launcher.fullscreen", "PC settings window", "Open full screen", "Άνοιγμα σε πλήρη οθόνη", "window size full screen|πλήρης οθόνη", Tg),
			R("launcher.allsettingshub", "PC settings window", "Action Center opens PC settings", "Το Κέντρο ενεργειών ανοίγει τις ρυθμίσεις υπολογιστή", "all settings action center quick settings shift|όλες οι ρυθμίσεις κέντρο ενεργειών", Tg),
			R("launcher.autorecover", "Recovery", "Auto-recover the shell", "Αυτόματη ανάκτηση κελύφους", "explorer restart|επανεκκίνηση explorer", Tg),
			R("launcher.forcereboot", "Recovery", "Force-reboot if Explorer can't recover", "Αναγκαστική επανεκκίνηση", "reboot last resort|επανεκκίνηση", Tg),
			R("launcher.profile", "Profiles", "Experience profile", "Προφίλ εμπειρίας", "profile windows 8.1 native|προφίλ", Cu),
			R("launcher.export", "Profiles", "Portable profile", "Φορητό προφίλ", "export import backup layout|εξαγωγή εισαγωγή αντίγραφο", Bt),
			R("launcher.experiments", "Advanced", "Experiments", "Πειράματα", "experimental|πειραματικά", Dr),
			R("launcher.diagnostics", "Advanced", "Diagnostics", "Διαγνωστικά", "dwm telemetry composition status|τηλεμετρία", Dr),
			R("launcher.restart", "Advanced", "Restart the launcher", "Επανεκκίνηση εκκινητή", "restart reload|επανεκκίνηση", Bt),
			R("experiments.enable", "Experiments", "Enable allowlisted experiments", "Ενεργοποίηση πειραμάτων", "experiments|πειράματα", Tg),
			R("experiments.audit", "Diagnostics", "Export compatibility audit", "Εξαγωγή ελέγχου συμβατότητας", "audit report|αναφορά", Ln),
			R("experiments.journal", "Diagnostics", "Open experiment journal", "Ημερολόγιο πειραμάτων", "journal rollback|ημερολόγιο", Ln),
			R("diag.dwm", "Diagnostics", "DWM frame-timing telemetry", "Τηλεμετρία χρονισμού DWM", "frame timing capture|χρονισμός καρέ", Tg),
			R("diag.capture", "Diagnostics", "Capture frame timing", "Καταγραφή χρονισμού καρέ", "capture 5 seconds|καταγραφή", Bt),
			R("diag.status", "Status", "Composition status", "Κατάσταση σύνθεσης", "backend status|κατάσταση", In),
			R("diag.profile", "Status", "Experience profile status", "Κατάσταση προφίλ", "active profile slots|ενεργό προφίλ", In),

			// ---- Control Panel & tools
			R("tools.controlpanel", "Control Panel", "Control Panel items", "Στοιχεία Πίνακα Ελέγχου", "applets cpl|μικροεφαρμογές", Cu),
			R("tools.admin", "Administrative tools", "Administrative tools", "Εργαλεία διαχείρισης", "services event viewer task scheduler|υπηρεσίες προβολή συμβάντων", Cu),
			R("tools.performance", "Performance tools", "Performance tools", "Εργαλεία απόδοσης", "task manager resource monitor process explorer latencymon|διαχείριση εργασιών", Cu)
		};
	}

	// ---- Search index of the hub rows and pages -------------------------------------------------------------------

	private static SystemSearchIndex? _index;

	// The hub's own small index (about 230 entries), built once and kept: pages are SettingsPage hits ("hub:<page>"),
	// rows Setting hits ("hub:<row>"), both with the English name, the Greek name as alt name and the keywords of both
	// languages, ranked by the same ranker as the Windows 7 Start menu (stems, Greek folding, typos).
	internal static SystemSearchIndex SearchIndex
	{
		get
		{
			if (_index != null)
			{
				return _index;
			}
			lock (Gate)
			{
				if (_index == null)
				{
					SystemSearchData data = new SystemSearchData
					{
						Schema = SystemSearchData.CurrentSchema,
						UiLanguage = "en-US",
						Languages = new[] { "en-US", "el-GR" }
					};
					foreach (HubPage p in Pages)
					{
						if (p.Id == "recent")
						{
							continue;
						}
						data.Hits.Add(new SystemHit
						{
							Id = "hub:" + p.Id,
							Kind = SystemHitKind.SettingsPage,
							Name = p.Title,
							AltNames = new[] { p.TitleEl }.Concat(p.Aliases).ToArray(),
							Keywords = SplitKeywords(p.Keywords),
							Launch = SystemLaunchKind.Uri,
							Target = "win81:pcsettings?page=" + p.Id
						});
					}
					foreach (HubRow r in Rows)
					{
						data.Hits.Add(new SystemHit
						{
							Id = "hub:" + r.Id,
							Kind = SystemHitKind.Setting,
							Name = r.Title,
							AltNames = new[] { r.TitleEl }.Concat(r.Aliases).ToArray(),
							Keywords = SplitKeywords(r.Keywords),
							Launch = SystemLaunchKind.Uri,
							Target = "win81:pcsettings?row=" + r.Id
						});
					}
					_index = SystemSearchIndex.FromData(data, "hub", 0.0, compact: true);
				}
			}
			return _index;
		}
	}

	private static string[] SplitKeywords(string keywords)
	{
		return keywords.Split(new[] { '|', ' ' }, StringSplitOptions.RemoveEmptyEntries);
	}
}
