using System.Collections.Generic;

namespace Win81Layer;

public sealed class AppSettings
{
	public bool HotCornersEnabled { get; set; } = true;

	// App switcher (left-edge / top-left) mouse reveal. Off = open it only via Win+Tab or the taskbar Task View button.
	public bool SwitcherEdgeReveal { get; set; } = true;

	// How long the mouse must rest at the left edge before the switcher appears (ms). Higher = fewer accidental reveals.
	public int SwitcherEdgeDwellMs { get; set; } = 900;

	// Default to booting to the DESKTOP (Start closed). The user can re-enable "Open Start at launch" in PC Settings.
	public bool BootToStart { get; set; } = false;

	// One-shot migration guard: flips an older persisted BootToStart=true to false exactly once (see SettingsStore).
	public bool BootToStartMigratedV2 { get; set; } = false;

	public bool ReplaceStartMenu { get; set; } = true;

	// Opt-in: show the classic Win7 orb Start menu instead of the full-screen Metro Start. OFF = Metro 8.1 Start
	// stays the permanent default. Deliberately INDEPENDENT of desktop-composition mode (never auto-driven by it).
	public bool Win7StartMenuEnabled { get; set; } = false;

	// 8.1 owned-window caption style: TRUE = authentic LIGHT caption (white bar, dark glyphs, red-at-rest close — the
	// out-of-box 8.1 default, VM-verified); FALSE = accent-coloured caption (8.1 with "show color on title bar"). Only
	// affects the flat 8.1 owned-frame style (not the Win7-Aero glossy caption).
	public bool Win81LightCaption { get; set; } = true;

	// The launcher-owned Windows 8.1 Explorer keeps its two user-facing layout choices durable without affecting
	// the native Explorer profile. Details uses a recycling GridView; Icons uses a larger recycling list row.
	public string Explorer81ViewMode { get; set; } = "Details";

	public bool Explorer81NavigationPane { get; set; } = true;

	// FALSE (default) = the 8.1 composition uses the REAL native File Explorer: full functionality (view modes,
	// copy/paste, drag-drop, multi-select, the complete Windows context menus) reskinned only with the 8.1 system
	// icons (+ border where the OS allows it). TRUE re-enables the launcher's own lightweight 8.1 browser window
	// (fewer features). The user chose native Explorer over the limited custom browser.
	public bool Win81CustomExplorer { get; set; } = false;

	public bool ReplaceDesktopMenu { get; set; } = true;

	public bool ReplaceSystemIcons { get; set; } = true;

	public bool Replace81AppIcons { get; set; } = true;

	// Declarative profile-owned state. Older builds inferred these from the presence of a registry snapshot,
	// which made the effective experience impossible to reproduce or switch transactionally.
	public bool UseWin81Sounds { get; set; }

	public bool UseWin81Cursors { get; set; }

	// Alchemy pointers: "" (off), "dark", "light" or "auto" (follows light/dark mode). Profile-owned. When set it takes
	// the place of UseWin81Cursors: one pointer scheme at a time.
	public string AlchemyCursors { get; set; } = "";

	// While Halloween or Christmas is on, the Alchemy pointers take the season's colours. Not profile-owned.
	public bool AlchemyCursorsSeasonal { get; set; } = true;

	public bool AutoRecoverShell { get; set; } = true;

	public bool ForceRebootOnShellFailure { get; set; } = false;

	public bool Win81Wallpaper { get; set; }

	public double TileScale { get; set; } = 1.0;

	public string WeatherCity { get; set; } = "Kalamata";

	public string WeatherUnits { get; set; } = "C";

	public string NewsFeedUrl { get; set; } = "https://www.naftemporiki.gr/feed/";

	public string PlaceSearchEngine { get; set; } = "Bing";

	public bool SuspendNativeStart { get; set; }

	public bool TaskbarEnabled { get; set; }

	public bool DominantMode { get; set; }

	public bool RunFirstTask { get; set; }

	public int AutoLockMinutes { get; set; }

	public string ScreenshotApp { get; set; } = "";

	// Which browser web links / web searches open in: "MetroBrowser" (default) or "System". MetroBrowserDir
	// overrides where MetroBrowser lives (empty = <Desktop>\MetroBrowser). See WebOpen.
	public string PreferredBrowser { get; set; } = "MetroBrowser";

	public string MetroBrowserDir { get; set; } = "";

	public string StartBgMode { get; set; } = "default";

	public string StartBgCustomPath { get; set; } = "";

	public string StartBgColor { get; set; } = "#FF3C1E70";

	public string StartAccentColor { get; set; } = "";

	// Window frames and 8.1 captions keep a colour of their own (FrameAccentColor, one of the Windows 8.1 window-border
	// colours) instead of following StartAccentColor. Off by default.
	public bool FrameAccentSeparate { get; set; }

	public string FrameAccentColor { get; set; } = "";

	// PC settings: the window's bounds per monitor ("device|x,y,w,h,max;..." in physical pixels) and the immersive
	// full-screen mode instead of the compact window.
	public string PcSettingsBounds { get; set; } = "";

	public bool PcSettingsFullScreen { get; set; }

	// PC settings: the rows starred as Favourites (row ids, ";"-separated, shown live on Home) and the user's Setups
	// (one "name|row=value,row=value" per line; at most 6, applied by hand as one undoable group).
	public string PcFavorites { get; set; } = "";

	public string PcSetups { get; set; } = "";

	// The Action Center's "All settings" opens PC settings (Shift+click opens Windows Settings); false: Windows Settings.
	public bool AllSettingsOpensHub { get; set; } = true;

	// Lighter rendering: every window of the launcher renders in software (no Direct3D device at all). Less memory; read
	// once at start, so a change needs a restart.
	public bool ProcessSoftwareRender { get; set; }

	public string StartPattern { get; set; } = "waves";

	// Seasonal theme marker ("" = normal, "halloween", "christmas"). Written in the same save as the Start values it
	// describes; the user's own values live in seasonal-state.json until Normal puts them back. Not profile-owned.
	public string SeasonalActive { get; set; } = "";

	// Seasonal preferences (kept across Normal): also set a matching desktop wallpaper, and show the Start effect.
	public bool SeasonalWallpaper { get; set; } = true;

	public bool SeasonalAmbient { get; set; } = true;

	// While a season is on, apps with a picture in <launcher folder>\seasonal\<season>-icons show it on every launcher
	// surface (SeasonalAppIcons). Not profile-owned.
	public bool SeasonalAppIcons { get; set; } = true;

	// Dark rest options (SeasonalActive = "rest"): a calm drawn desktop wallpaper, calmer animations (Motion plays as
	// Reduced, the Motion setting itself is not changed) and slightly dimmed Start tiles. Not profile-owned; kept
	// across Normal.
	public bool RestWallpaper { get; set; } = true;

	public bool RestCalmMotion { get; set; } = true;

	public bool RestDimTiles { get; set; } = true;

	// The Metro Tab browser extension's folder, where the launcher writes shell-look.json (ShellLookLink). Empty = find
	// it next to the launcher (<parent of the launcher folder>\ChromeExtension). Not profile-owned.
	public string ShellLookExtensionDir { get; set; } = "";

	// Notification-area icons with a seasonal picture: "live" keeps the app's status visible (badges on the picture, the
	// app's own icon during calls and other states, SeasonalTrayIcons); "original" always shows the app's own icon.
	public string SeasonalTrayIcons { get; set; } = "live";

	public bool StartGridFilledV1 { get; set; }

	public string MotionMode { get; set; } = "Authentic";

	public bool PackedGrid { get; set; } = false;

	public bool FreeGridDefaultV1 { get; set; }

	public bool PackedGridMigratedV1 { get; set; }

	public bool PackedGridAlignedV2 { get; set; }

	public bool PerfMonEnabled { get; set; }

	// Phase-3 DWM frame-timing telemetry master switch (default OFF). The on-demand capture burst is additionally
	// manual (a tray / PC-Settings action) — this flag just gates whether that action does anything.
	public bool EnableDwmTimingTelemetry { get; set; }

	// Master consent for build-gated, allowlisted experiments. Unknown feature IDs are never writable even when
	// this is enabled; ExperimentRegistry additionally requires a capture/apply/verify/undo implementation.
	public bool EnableExperimentalFeatures { get; set; }

	public string DesktopCompositionMode { get; set; } = "native";   // guaranteed clean fallback — no Alchemy composition effects by default

	// Reversibility state (the accent-registry snapshot itself lives in deskcomp-accent.json, not here).
	public string DeskCompPreviousMode { get; set; } = "";          // target for "Restore Previous"

	public string DeskCompPreviewMode { get; set; } = "";           // non-empty while a Preview is live (for crash/reboot reconciliation)

	public long DeskCompPreviewUntilUtcTicks { get; set; }          // wall-clock deadline of the live preview

	// Desktop-composition option toggles + sliders (shown on the Desktop Composition settings page).
	public bool DeskCompTransparency { get; set; } = true;

	public bool DeskCompShadows { get; set; } = true;

	public bool DeskCompAnimations { get; set; } = true;

	public bool DeskCompOptimizePerf { get; set; }

	// Legacy compatibility field. The external hook is permanently disabled; authentic presets use
	// launcher-owned frames and never mutate foreign windows or inject into DWM.
	public bool UseDwmBlurGlassForWin7 { get; set; }

	public int DeskCompBlur { get; set; } = 60;                     // 0..100 shell tint opacity / blur intensity

	public int DeskCompAnimSpeed { get; set; } = 50;                // 0..100 (50 = Normal) → Motion scale

	public bool TaskbarLocked { get; set; }

	public bool SearchEntityCards { get; set; } = true;

	public bool TaskbarAutoHide { get; set; }

	public string TaskbarSize { get; set; } = "Medium";

	public string TaskbarPosition { get; set; } = "Bottom";

	public string TaskbarAlignment { get; set; } = "Left";

	public string TaskbarCombine { get; set; } = "Always";

	public string TaskbarColorMode { get; set; } = "Wallpaper";

	public bool TaskbarTransparent { get; set; }

	public bool ShowSearch { get; set; } = true;

	public bool ShowTaskView { get; set; } = true;

	public bool ShowActionCenter { get; set; } = true;

	// Where the Action center button sits in the tray. "Right" = far right after the date (Win10 style, default);
	// "Left" = to the left of the clock (Win8.1 style). Applied identically on every monitor so the position is
	// consistent across displays.
	public string ActionCenterPosition { get; set; } = "Right";

	public bool TaskbarSameOnAllDisplays { get; set; } = true;

	// Taskbar surfaces (bar, preview, tooltips, menus) on non-primary monitors render in software, so the shell never
	// creates a second Direct3D device (~48 MB private, ~20 MB pinned RAM, ~24 MB VRAM). False = hardware everywhere.
	public bool SecondaryMonitorSoftwareRender { get; set; } = true;

	public List<string> TrayOrder { get; set; } = new List<string>();

	public List<string> TrayForceShown { get; set; } = new List<string>();

	public List<string> TrayForceHidden { get; set; } = new List<string>();

	public List<string> CharmQuickOrder { get; set; } = new List<string>();

	public List<string> CharmQuickHidden { get; set; } = new List<string>();

	// Classic taskbar "Toolbars": folder paths shown as a shortcuts toolbar next to the tray (Desktop/Links/custom).
	public List<string> TaskbarToolbars { get; set; } = new List<string>();

	public List<string> Win7StartMenuPins { get; set; } = new List<string>();

	public List<string> Win7StartMenuMruHidden { get; set; } = new List<string>();

	// The "Address" taskbar toolbar (a path/command box next to the tray).
	public bool TaskbarAddressBar { get; set; }

	public bool CharmQuickCustomised { get; set; }

	public bool CharmCompact { get; set; }

	public bool CharmTransparent { get; set; }

	public bool CharmBlur { get; set; }

	public AppSettings Clone()
	{
		AppSettings c = (AppSettings)MemberwiseClone();
		c.TrayOrder = new List<string>(TrayOrder);
		c.TrayForceShown = new List<string>(TrayForceShown);
		c.TrayForceHidden = new List<string>(TrayForceHidden);
		c.CharmQuickOrder = new List<string>(CharmQuickOrder);
		c.CharmQuickHidden = new List<string>(CharmQuickHidden);
		c.TaskbarToolbars = new List<string>(TaskbarToolbars);
		c.Win7StartMenuPins = new List<string>(Win7StartMenuPins ?? new List<string>());
		c.Win7StartMenuMruHidden = new List<string>(Win7StartMenuMruHidden ?? new List<string>());
		return c;
	}
}
