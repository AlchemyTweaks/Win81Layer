using System.Collections.Generic;

namespace Win81Layer;

public sealed class AppSettings
{
	public bool HotCornersEnabled { get; set; } = true;

	// App switcher (left-edge / top-left) mouse reveal. Off = open it only via Win+Tab or the taskbar Task View button.
	public bool SwitcherEdgeReveal { get; set; } = true;

	// How long the mouse must rest at the left edge before the switcher appears (ms). Higher = fewer accidental reveals.
	public int SwitcherEdgeDwellMs { get; set; } = 900;

	public bool BootToStart { get; set; } = true;

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

	public bool RouteNotifications { get; set; } = false;

	public bool ReplaceSystemIcons { get; set; } = true;

	public bool Replace81AppIcons { get; set; } = true;

	// Declarative profile-owned state. Older builds inferred these from the presence of a registry snapshot,
	// which made the effective experience impossible to reproduce or switch transactionally.
	public bool UseWin81Sounds { get; set; }

	public bool UseWin81Cursors { get; set; }

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

	public bool NativeChrome { get; set; }

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

	public string StartPattern { get; set; } = "waves";

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
		return c;
	}
}
