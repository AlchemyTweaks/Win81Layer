using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

#nullable enable annotations

namespace Win81Layer;

// The launcher's own pages: Launcher (startup, shell takeover, performance, recovery, profiles), Desktop & taskbar,
// Start & search, and the Experiments and Diagnostics drill-ins. Every row persists through SettingsStore.Update and
// applies live through the same static side effects and App.ApplyXxx hooks as the tray menu and the taskbar menu.
public sealed partial class PcSettingsWindow
{
	private string _shellProfileSel = "";

	private WrapPanel? _shellProfileTiles;

	private TextBlock? _shellProfileStatus;

	private ScrollViewer LauncherPage()
	{
		AppSettings s = SettingsStore.Load();
		Pg pg = new Pg(this, "launcher");
		ISettingsWorld world = SettingsWorld.Current;
		pg.Header("Startup");
		ToggleButton? logonToggle = null;
		FrameworkElement logon = ToggleRow("launcher.logon", false, delegate(bool on)
		{
			_logonTaskCached = on;
			Task.Run(delegate
			{
				try
				{
					if (world.SetLogonTask(on))
					{
						SettingsStore.Update(delegate(AppSettings x) { x.RunFirstTask = on; });
					}
					else
					{
						_logonTaskCached = null;
					}
				}
				catch (Exception ex)
				{
					Logger.Log("PC settings: logon task: " + ex.Message);
				}
			});
		}, "Launch the Win8.1 layer automatically when you sign in: a per-user logon task, which also runs the boot supervisor that keeps the shell healthy.");
		logonToggle = Descendants<ToggleButton>(logon).FirstOrDefault();
		if (logonToggle != null)
		{
			logonToggle.IsEnabled = false;
			ToggleButton t = logonToggle;
			// schtasks takes up to seconds: read once per window, off the UI thread
			if (_logonTaskCached.HasValue)
			{
				SetQuiet(t, _logonTaskCached.Value);
				t.IsEnabled = true;
			}
			else
			{
				ReadAsync(world.ReadLogonTask, delegate(bool on)
				{
					_logonTaskCached = on;
					SetQuiet(t, on);
					t.IsEnabled = true;
				});
			}
		}
		pg.Add(logon);
		pg.Add(ToggleRow("launcher.runkey", world.ReadRunKey(), delegate(bool on)
		{
			Task.Run(delegate
			{
				try
				{
					world.SetRunKey(on);
				}
				catch (Exception ex)
				{
					Logger.Log("PC settings: Run key: " + ex.Message);
				}
			});
		}, "Legacy fallback autostart through the HKCU Run key. Normally not needed when Start at sign-in is on."));
		pg.Add(InfoRow("launcher.safemode", App.SafeMode ? "On (core surfaces only)" : "Off", help: "After repeated crashes the launcher starts with the taskbar and Start only, and clears this by itself once it runs healthy."));
		pg.Header("Shell");
		pg.Add(ToggleRow("launcher.dominant", s.DominantMode, delegate(bool on)
		{
			SettingsStore.Update(delegate(AppSettings x)
			{
				x.DominantMode = on;
				x.SuspendNativeStart = on;
				x.TaskbarEnabled = on;
			});
			HostApp?.ApplyDominantMode(on);
		}, "Suspends the native Start host, replaces the taskbar and routes Win+S to the Win8.1 Search. Native is restored on exit."));
		pg.Add(ToggleRow("launcher.taskbar", s.TaskbarEnabled, delegate(bool on)
		{
			SettingsStore.Update(delegate(AppSettings x) { x.TaskbarEnabled = on; });
			HostApp?.ApplyTaskbarEnabled(on);
		}, "Replace the native taskbar with the Win8.1 taskbar."));
		pg.Add(ToggleRow("launcher.suspend", s.SuspendNativeStart, delegate(bool on)
		{
			SettingsStore.Update(delegate(AppSettings x) { x.SuspendNativeStart = on; });
			HostApp?.ApplySuspendNativeStart(on);
		}, "Freeze StartMenuExperienceHost while the layer runs. Always resumed on exit."));
		pg.Add(ToggleRow("launcher.replacestart", s.ReplaceStartMenu, delegate(bool on)
		{
			SettingsStore.Update(delegate(AppSettings x) { x.ReplaceStartMenu = on; });
			HostApp?.ApplyReplaceStartButton(on);
		}, "Route the taskbar Start button and the Windows key to the launcher's Start."));
		pg.Header("Performance");
		pg.Add(ToggleRow("launcher.lightrender", s.SecondaryMonitorSoftwareRender, delegate(bool on)
		{
			SettingsStore.Update(delegate(AppSettings x) { x.SecondaryMonitorSoftwareRender = on; });
		}, "Draw the taskbar and its menus on secondary monitors without the graphics card, saving about 50-100 MB of memory. Turn off if anything on a second monitor looks wrong.", secondary: "Applies after the launcher restarts"));
		pg.Add(LighterRenderingRow(s));
		pg.Header("PC settings window");
		pg.Add(ToggleRow("launcher.fullscreen", s.PcSettingsFullScreen, delegate(bool on)
		{
			SettingsStore.Update(delegate(AppSettings x) { x.PcSettingsFullScreen = on; });
		}, "Off: a compact window you can place next to the app you are tuning (it remembers where you left it on each monitor). On: the immersive Windows 8.1 full-screen page.", secondary: "Takes effect the next time PC settings opens"));
		pg.Add(ToggleRow("launcher.allsettingshub", s.AllSettingsOpensHub, delegate(bool on)
		{
			HubSettings.Update(delegate(AppSettings x) { x.AllSettingsOpensHub = on; });
		}, "All settings at the bottom of the Action Center opens this window; Shift+click opens Windows Settings. Off: it always opens Windows Settings.", secondary: "Shift+click opens Windows Settings"));
		pg.Header("Recovery");
		pg.Add(ToggleRow("launcher.autorecover", s.AutoRecoverShell, delegate(bool on)
		{
			SettingsStore.Update(delegate(AppSettings x) { x.AutoRecoverShell = on; });
		}, "Keep re-hiding any native taskbar that reappears, and restart Explorer if it dies."));
		pg.Add(ToggleRow("launcher.forcereboot", s.ForceRebootOnShellFailure, delegate(bool on)
		{
			SettingsStore.Update(delegate(AppSettings x) { x.ForceRebootOnShellFailure = on; });
		}, "Last resort: reboot (cancellable) if Explorer will not come back. Requires Auto-recover."));
		pg.Header("Profiles");
		pg.Add(ProfileTilesRow());
		pg.Add(ButtonRow("launcher.export", "Export…", ExportProfileDialog, "Start layout, tiles, workspaces and settings in one file", title: "Portable profile"));
		pg.Add(ButtonRow("launcher.import", "Import…", ImportProfileDialog, "Backs up your setup first; applies after a restart", title: "Import a portable profile"));
		pg.Header("Advanced");
		pg.Add(DrillRow("launcher.experiments", null, delegate { Navigate("launcher/experiments", null, push: true); }));
		pg.Add(DrillRow("launcher.diagnostics", null, delegate { Navigate("launcher/diagnostics", null, push: true); }));
		pg.Add(ButtonRow("launcher.restart", "Restart", delegate { SettingsLink.Command(LogonTask.CanonicalExe); }, "Starts the deployed build again"));
		return pg.Done();
	}

	// The logon task's state, read once per window (schtasks takes up to seconds).
	private bool? _logonTaskCached;

	// Lighter rendering (every window in software, no Direct3D device): read once at start, so a change shows an inline
	// "Restart now" until the running launcher matches it again.
	private FrameworkElement LighterRenderingRow(AppSettings s)
	{
		StackPanel host = new StackPanel();
		FrameworkElement? restart = null;
		TextBlock? line = null;
		void Follow(bool wanted)
		{
			bool pending = wanted != App.ProcessSoftwareRendering;
			if (restart != null)
			{
				restart.Visibility = pending ? Visibility.Visible : Visibility.Collapsed;
			}
			if (line != null)
			{
				line.Text = pending ? "Restart the launcher to apply" : wanted ? "On: uses less memory" : "Uses less memory, needs a restart";
			}
		}
		FrameworkElement toggle = ToggleRow("launcher.softwarerender", s.ProcessSoftwareRender, delegate(bool on)
		{
			HubSettings.Update(delegate(AppSettings x) { x.ProcessSoftwareRender = on; });
			Follow(on);
		}, "Every window of the launcher is drawn without the graphics card: about 55-60 MB less memory, plus the graphics memory the launcher holds. Recommended with the Windows 7 Start menu; check that animations still look smooth to you.", secondary: "Uses less memory, needs a restart");
		line = toggle is Grid g ? FindSecondary(g) : null;
		host.Children.Add(toggle);
		Button now = HubButton("Restart now", delegate { SettingsLink.Command(LogonTask.CanonicalExe); });
		Grid prompt = RowShell("Restart the launcher", "The new rendering starts with it", null, now, out _);
		AutomationProperties.SetName(now, "Restart the launcher now");
		StackPanel ask = new StackPanel();
		ask.Children.Add(Separator());
		ask.Children.Add(prompt);
		restart = ask;
		host.Children.Add(ask);
		Follow(s.ProcessSoftwareRender);
		return host;
	}

	// Experience profiles: one tile per profile and the actions on the selected one.
	private FrameworkElement ProfileTilesRow()
	{
		ShellProfileStatus profileStatus = ShellProfileManager.Describe();
		if (string.IsNullOrWhiteSpace(_shellProfileSel))
		{
			_shellProfileSel = ShellProfileManager.Profiles.Any((ShellProfileDescriptor p) => string.Equals(p.Id, profileStatus.ActiveProfileId, StringComparison.OrdinalIgnoreCase))
				? profileStatus.ActiveProfileId
				: ShellProfileIds.Windows81;
		}
		StackPanel host = new StackPanel();
		host.Children.Add(new TextBlock
		{
			Text = HubCatalog.Title("launcher.profile"),
			FontFamily = SegoeUi,
			FontSize = 13.5,
			Foreground = _pal.Ink,
			Margin = new Thickness(0.0, 0.0, 0.0, 8.0)
		});
		_shellProfileTiles = new WrapPanel();
		host.Children.Add(_shellProfileTiles);
		RebuildShellProfileTiles();
		WrapPanel actions = new WrapPanel { Margin = new Thickness(0.0, 4.0, 0.0, 0.0) };
		void Add(string text, bool primary, Action act)
		{
			Button b = HubButton(text, act, accent: primary);
			b.Margin = new Thickness(0.0, 0.0, 8.0, 6.0);
			actions.Children.Add(b);
		}
		Add("Apply profile", primary: true, delegate
		{
			ShellProfileApplyResult result = ShellProfileManager.Apply(_shellProfileSel);
			if (!result.Success)
			{
				ShowShellProfileFailure(result);
			}
			RebuildShellProfileTiles();
			RefreshShellProfileStatus();
		});
		Add("Restore previous", primary: false, delegate
		{
			ShellProfileApplyResult result = ShellProfileManager.RestorePrevious();
			if (result.Success && !string.IsNullOrWhiteSpace(result.ProfileId))
			{
				_shellProfileSel = result.ProfileId;
			}
			else if (!result.Success)
			{
				ShowShellProfileFailure(result);
			}
			RebuildShellProfileTiles();
			RefreshShellProfileStatus();
		});
		Add("Native recovery", primary: false, delegate
		{
			ShellProfileApplyResult result = ShellProfileManager.Apply(ShellProfileIds.Native);
			if (result.Success)
			{
				_shellProfileSel = ShellProfileIds.Native;
			}
			else
			{
				ShowShellProfileFailure(result);
			}
			RebuildShellProfileTiles();
			RefreshShellProfileStatus();
		});
		Add("Reset selected", primary: false, delegate
		{
			if (MessageBox.Show("Reset the selected experience to its built-in defaults? Common settings, Start layout and pins are preserved.", "Reset experience profile", MessageBoxButton.OKCancel, MessageBoxImage.Warning) == MessageBoxResult.OK)
			{
				ShellProfileApplyResult result = ShellProfileManager.ResetAndApply(_shellProfileSel);
				if (!result.Success)
				{
					ShowShellProfileFailure(result);
				}
				RebuildShellProfileTiles();
				RefreshShellProfileStatus();
			}
		});
		host.Children.Add(actions);
		return CustomRow("launcher.profile", host);
	}

	private void RebuildShellProfileTiles()
	{
		if (_shellProfileTiles == null)
		{
			return;
		}
		_shellProfileTiles.Children.Clear();
		foreach (ShellProfileDescriptor profile in ShellProfileManager.Profiles)
		{
			ShellProfileDescriptor p = profile;
			bool selected = string.Equals(profile.Id, _shellProfileSel, StringComparison.OrdinalIgnoreCase);
			_shellProfileTiles.Children.Add(ModeTile(profile.Name, (char)profile.Glyph, selected, delegate
			{
				_shellProfileSel = p.Id;
				RebuildShellProfileTiles();
			}));
		}
	}

	private void RefreshShellProfileStatus()
	{
		if (_shellProfileStatus != null)
		{
			_shellProfileStatus.Text = ShellProfileStatusText();
		}
	}

	private static string ShellProfileStatusText()
	{
		ShellProfileStatus status = ShellProfileManager.Describe();
		string text = $"Active: {status.ActiveProfileName}{(status.IsCustomized ? " (customized)" : string.Empty)} | Saved slots: {status.SavedSlots} | Last result: {status.LastResult}";
		if (status.RecoveryPending)
		{
			text += " | Recovery pending";
		}
		if (!string.IsNullOrWhiteSpace(status.LastError))
		{
			text += " | " + status.LastError;
		}
		return text;
	}

	private static void ShowShellProfileFailure(ShellProfileApplyResult result)
	{
		MessageBox.Show(result.Message, "Experience profile", MessageBoxButton.OK, MessageBoxImage.Error);
	}

	private void ExportProfileDialog()
	{
		try
		{
			Microsoft.Win32.SaveFileDialog dlg = new Microsoft.Win32.SaveFileDialog
			{
				Title = "Export launcher profile",
				FileName = "launcher-profile.json",
				Filter = "Launcher profile (*.json)|*.json|All files (*.*)|*.*",
				AddExtension = true,
				DefaultExt = ".json"
			};
			if (dlg.ShowDialog(this) == true)
			{
				bool ok = LauncherProfile.Export(dlg.FileName, out string? err);
				MessageBox.Show(this, ok ? "Profile exported." : ("Export failed:\n" + err), "Export profile", MessageBoxButton.OK, ok ? MessageBoxImage.Information : MessageBoxImage.Warning);
			}
		}
		catch (Exception ex)
		{
			Logger.Log("Export profile dialog: " + ex.Message);
		}
	}

	private void ImportProfileDialog()
	{
		try
		{
			Microsoft.Win32.OpenFileDialog dlg = new Microsoft.Win32.OpenFileDialog
			{
				Title = "Import launcher profile",
				Filter = "Launcher profile (*.json)|*.json|All files (*.*)|*.*",
				CheckFileExists = true
			};
			if (dlg.ShowDialog(this) != true)
			{
				return;
			}
			MessageBoxResult confirm = MessageBox.Show(this, "Import this profile?\n\nYour current layout and settings will be backed up, then replaced. Changes apply after the launcher restarts.", "Import profile", MessageBoxButton.OKCancel, MessageBoxImage.Warning);
			if (confirm != MessageBoxResult.OK)
			{
				return;
			}
			bool ok = LauncherProfile.Import(dlg.FileName, out string? err);
			MessageBox.Show(this, ok ? "Profile imported. Restart the launcher to apply." : ("Import rejected — your current setup was kept:\n" + err), "Import profile", MessageBoxButton.OK, ok ? MessageBoxImage.Information : MessageBoxImage.Warning);
		}
		catch (Exception ex)
		{
			Logger.Log("Import profile dialog: " + ex.Message);
		}
	}

	// ===== Experiments (drill-in) =====

	private ScrollViewer ExperimentsPage()
	{
		ExperimentAuditReport audit = ExperimentRegistry.Audit(persist: false);
		AppSettings settings = SettingsStore.Load();
		Pg pg = new Pg(this, "launcher/experiments");
		pg.Header("Experiments");
		pg.Add(InfoRow("experiments.build", audit.BuildFingerprint + (string.IsNullOrWhiteSpace(audit.DisplayVersion) ? "" : " (" + audit.DisplayVersion + ")"), title: "Windows build"));
		pg.Add(InfoRow("experiments.policy", audit.MutationPolicy, title: "Mutation policy"));
		pg.Add(ToggleRow("experiments.enable", settings.EnableExperimentalFeatures, delegate(bool enabled)
		{
			SettingsStore.Update(delegate(AppSettings x) { x.EnableExperimentalFeatures = enabled; });
		}, "Permits only experiments with build gates and a tested capture, apply, verify and undo path."));
		pg.Header("Capabilities");
		foreach (ExperimentCapability capability in audit.Capabilities)
		{
			pg.Add(InfoRow(null!, capability.Status, capability.Detail, capability.Name));
		}
		pg.Header("Diagnostics");
		pg.Add(ActionRow("experiments.audit", delegate
		{
			string path = ExperimentRegistry.WriteAuditReport();
			SettingsLink.Command("explorer.exe", "/select,\"" + path + "\"");
		}, "Write the current OS, API and power-policy snapshot"));
		pg.Add(ActionRow("experiments.journal", delegate
		{
			Directory.CreateDirectory(ExperimentRegistry.JournalPath);
			SettingsLink.Shell(ExperimentRegistry.JournalPath);
		}, "Versioned experiment transactions and rollback records"));
		return pg.Done();
	}

	// ===== Diagnostics (drill-in) =====

	private ScrollViewer DiagnosticsPage()
	{
		AppSettings s = SettingsStore.Load();
		Pg pg = new Pg(this, "launcher/diagnostics");
		pg.Header("Diagnostics");
		pg.Add(ToggleRow("diag.dwm", s.EnableDwmTimingTelemetry, delegate(bool v)
		{
			SettingsStore.Update(delegate(AppSettings x) { x.EnableDwmTimingTelemetry = v; });
		}, "On-demand frame-timing capture: the tray item or the button below writes a bounded report of about 5 s to %LocalAppData%\\Win81Layer\\dwm-diag. It samples from the live shell and never runs a background loop."));
		pg.Add(ButtonRow("diag.capture", "Capture", delegate
		{
			if (!SettingsStore.Load().EnableDwmTimingTelemetry || SettingsStore.ReadOnlyDiagnostics)
			{
				return;
			}
			nint h = HostApp?.ShellHwndForDiagnostics() ?? 0;
			Task.Run(delegate
			{
				try
				{
					DwmDiagnostics.FrameTimingBurst(h, 300, 16);
				}
				catch
				{
				}
			});
		}, "About 5 seconds, needs the telemetry option"));
		pg.Header("Status");
		_compStatus = new StackPanel();
		_compStatusSignature = "";
		pg.Add(CustomRow("diag.status", _compStatus));
		RefreshCompStatus();
		StartCompStatusTimer();
		_shellProfileStatus = new TextBlock
		{
			Foreground = _pal.Ink2,
			FontFamily = SegoeUi,
			FontSize = 12.0,
			TextWrapping = TextWrapping.Wrap,
			Text = ShellProfileStatusText()
		};
		pg.Add(CustomRow("diag.profile", _shellProfileStatus));
		return pg.Done();
	}

	// ===== Desktop & taskbar =====

	private ScrollViewer TaskbarPage()
	{
		AppSettings s = SettingsStore.Load();
		Pg pg = new Pg(this, "taskbar");
		pg.Header("Taskbar");
		string[] posOpts = new string[4] { "Bottom", "Top", "Left", "Right" };
		pg.Add(ChoiceRow("taskbar.position", posOpts, Math.Max(0, Array.IndexOf(posOpts, s.TaskbarPosition)), delegate(int i)
		{
			string v = posOpts[i];
			SettingsStore.Update(delegate(AppSettings x) { x.TaskbarPosition = v; });
			TaskbarWindow.RaiseTaskbarPositionChanged();
		}));
		string[] sizeOpts = new string[3] { "Small", "Medium", "Large" };
		int sizeIdx = s.TaskbarSize == "Small" ? 0 : (s.TaskbarSize == "Large" ? 2 : 1);
		pg.Add(ChoiceRow("taskbar.size", sizeOpts, sizeIdx, delegate(int i)
		{
			string v = sizeOpts[i];
			SettingsStore.Update(delegate(AppSettings x) { x.TaskbarSize = v; });
			TaskbarWindow.RaiseTaskbarSizeChanged();
		}));
		string[] alignOpts = new string[2] { "Left", "Center" };
		pg.Add(ChoiceRow("taskbar.alignment", alignOpts, s.TaskbarAlignment == "Center" ? 1 : 0, delegate(int i)
		{
			string v = alignOpts[i];
			SettingsStore.Update(delegate(AppSettings x) { x.TaskbarAlignment = v; });
			TaskbarWindow.RaiseTaskbarAlignmentChanged();
		}));
		string[] combineOpts = new string[3] { "Always", "When taskbar is full", "Never" };
		string[] combineVals = new string[3] { "Always", "WhenFull", "Never" };
		pg.Add(ChoiceRow("taskbar.combine", combineOpts, Math.Max(0, Array.IndexOf(combineVals, s.TaskbarCombine)), delegate(int i)
		{
			string v = combineVals[i];
			SettingsStore.Update(delegate(AppSettings x) { x.TaskbarCombine = v; });
			TaskbarWindow.RaiseTaskbarLayoutChanged();
		}));
		string[] colorOpts = new string[3] { "From desktop wallpaper", "Match Start screen", "Transparent" };
		string[] colorVals = new string[3] { "Wallpaper", "Start", "Transparent" };
		pg.Add(ChoiceRow("taskbar.color", colorOpts, Math.Max(0, Array.IndexOf(colorVals, s.TaskbarColorMode)), delegate(int i)
		{
			string v = colorVals[i];
			SettingsStore.Update(delegate(AppSettings x) { x.TaskbarColorMode = v; });
			TaskbarWindow.RaiseTaskbarColorChanged();
		}, secondary: DarkRest.IsOn ? "Dark rest keeps the taskbar dark for now" : null, width: 200.0));
		pg.Add(ToggleRow("taskbar.autohide", s.TaskbarAutoHide, delegate(bool on)
		{
			SettingsStore.Update(delegate(AppSettings x) { x.TaskbarAutoHide = on; });
			TaskbarWindow.RaiseAutoHideChanged();
		}, "Hide the taskbar until you point at its screen edge."));
		pg.Add(ToggleRow("taskbar.lock", s.TaskbarLocked, delegate(bool on)
		{
			SettingsStore.Update(delegate(AppSettings x) { x.TaskbarLocked = on; });
			TaskbarWindow.RaiseTaskbarLayoutChanged();
		}, "Prevent resizing and moving the taskbar."));
		pg.Add(ToggleRow("taskbar.perfmon", s.PerfMonEnabled, delegate(bool on)
		{
			SettingsStore.Update(delegate(AppSettings x) { x.PerfMonEnabled = on; });
			TaskbarWindow.ReloadPerfMode();
		}, "Show CPU, RAM and network telemetry on the taskbar."));
		pg.Add(ToggleRow("taskbar.alldisplays", s.TaskbarSameOnAllDisplays, delegate(bool on)
		{
			SettingsStore.Update(delegate(AppSettings x) { x.TaskbarSameOnAllDisplays = on; });
			TaskbarWindow.RaiseTaskbarLayoutChanged();
		}, "Show the same taskbar buttons on every monitor (off: per-display Win8.1 taskbars)."));
		pg.Header("Buttons");
		pg.Add(ToggleRow("taskbar.searchbtn", s.ShowSearch, delegate(bool on)
		{
			SettingsStore.Update(delegate(AppSettings x) { x.ShowSearch = on; });
			TaskbarWindow.RaiseTaskbarButtonsChanged();
		}));
		pg.Add(ToggleRow("taskbar.taskviewbtn", s.ShowTaskView, delegate(bool on)
		{
			SettingsStore.Update(delegate(AppSettings x) { x.ShowTaskView = on; });
			TaskbarWindow.RaiseTaskbarButtonsChanged();
		}));
		pg.Add(ToggleRow("taskbar.acbtn", s.ShowActionCenter, delegate(bool on)
		{
			SettingsStore.Update(delegate(AppSettings x) { x.ShowActionCenter = on; });
			TaskbarWindow.RaiseTaskbarButtonsChanged();
		}));
		string[] acPosOpts = new string[2] { "Far right (after date)", "Left of the clock" };
		string[] acPosVals = new string[2] { "Right", "Left" };
		pg.Add(ChoiceRow("taskbar.acpos", acPosOpts, Math.Max(0, Array.IndexOf(acPosVals, s.ActionCenterPosition)), delegate(int i)
		{
			string v = acPosVals[i];
			SettingsStore.Update(delegate(AppSettings x) { x.ActionCenterPosition = v; });
			TaskbarWindow.RaiseTaskbarButtonsChanged();
		}, width: 200.0));
		pg.Header("Corners & edges");
		pg.Add(ToggleRow("taskbar.hotcorners", s.HotCornersEnabled, delegate(bool on)
		{
			SettingsStore.Update(delegate(AppSettings x) { x.HotCornersEnabled = on; });
			HostApp?.ApplyHotCorners(on);
		}, "Win8.1 screen-corner gestures (charms, Start, recent-app switch)."));
		pg.Add(ToggleRow("taskbar.switcheredge", s.SwitcherEdgeReveal, delegate(bool on)
		{
			SettingsStore.Update(delegate(AppSettings x) { x.SwitcherEdgeReveal = on; });
			HostApp?.ApplySwitcherEdge(on);
		}, "Open the left-edge app switcher by pushing the mouse to the left edge. Win+Tab and the Task View button still open it when this is off."));
		string shotName = string.IsNullOrEmpty(s.ScreenshotApp) ? "Default" : Path.GetFileName(s.ScreenshotApp);
		pg.Add(ActionRow("taskbar.screenshot", delegate
		{
			Microsoft.Win32.OpenFileDialog dlg = new Microsoft.Win32.OpenFileDialog
			{
				Filter = "Programs (*.exe)|*.exe|All files (*.*)|*.*",
				Title = "Choose the screenshot app"
			};
			if (dlg.ShowDialog(this) == true)
			{
				string p = dlg.FileName;
				SettingsStore.Update(delegate(AppSettings x) { x.ScreenshotApp = p; });
				RebuildCurrentPage();
			}
		}, "Opened by the Share charm: " + shotName, glyph: ""));
		pg.Header("More");
		pg.Add(LinkRow("taskbar.windows"));
		return pg.Done();
	}

	// ===== Start & search =====

	private ScrollViewer StartPage()
	{
		AppSettings s = SettingsStore.Load();
		Pg pg = new Pg(this, "start");
		pg.Header("Start");
		string[] styles = new string[2] { "Windows 7 menu", "8.1 Start screen" };
		pg.Add(ChoiceRow("start.style", styles, s.Win7StartMenuEnabled ? 0 : 1, delegate(int i)
		{
			bool on = i == 0;
			SettingsStore.Update(delegate(AppSettings x) { x.Win7StartMenuEnabled = on; });
			HostApp?.ApplyWin7StartMenu(on);
		}, "The classic Windows 7 orb menu or the full-screen 8.1 Start screen."));
		pg.Add(ToggleRow("start.boot", s.BootToStart, delegate(bool on)
		{
			SettingsStore.Update(delegate(AppSettings x) { x.BootToStart = on; });
		}, "Show Start as soon as the layer starts at sign-in."));
		string[] tileOpts = new string[2] { "Comfortable", "Compact" };
		double[] tileVals = new double[2] { 1.0, 0.8 };
		pg.Add(ChoiceRow("start.tiles", tileOpts, Math.Abs(s.TileScale - 0.8) < 0.01 ? 1 : 0, delegate(int i)
		{
			double v = tileVals[i];
			SettingsStore.Update(delegate(AppSettings x) { x.TileScale = v; });
			HostApp?.ApplyTileDensity(v);
		}));
		pg.Add(ToggleRow("start.packed", s.PackedGrid, delegate(bool on)
		{
			SettingsStore.Update(delegate(AppSettings x) { x.PackedGrid = on; });
			HostApp?.ApplyPackedGrid(on);
		}, "Automatically pack tiles to remove gaps."));
		if (_start != null)
		{
			pg.Header("Background");
			pg.Add(CustomRow("start.background", StartBackgroundPicker()));
			pg.Add(CustomRow("start.bgcolor", StartColourPicker()));
			StartScreen start = _start;
			pg.Add(ActionRow("start.matchtheme", delegate { start.SyncStartAccentToTheme(); }, "The accent follows the Start background again", "Match the Start screen theme", ""));
		}
		pg.Header("Live tiles");
		TextBox city = FieldBox(180.0);
		city.Text = s.WeatherCity ?? "";
		city.LostKeyboardFocus += delegate
		{
			string c = city.Text.Trim();
			if (c == (SettingsStore.FastSnapshot.WeatherCity ?? ""))
			{
				return;
			}
			SettingsStore.Update(delegate(AppSettings x) { x.WeatherCity = c; });
			HostApp?.ApplyWeatherCity(c);
		};
		Grid cityRow = RowShell(HubCatalog.Title("start.weathercity"), null, null, city, out _);
		Register("start.weathercity", cityRow);
		pg.Add(cityRow);
		pg.Add(ChoiceRow("start.weatherunits", new[] { "Celsius", "Fahrenheit" }, string.Equals(s.WeatherUnits, "F", StringComparison.OrdinalIgnoreCase) ? 1 : 0, delegate(int i)
		{
			string unit = i == 1 ? "F" : "C";
			SettingsStore.Update(delegate(AppSettings x) { x.WeatherUnits = unit; });
			HostApp?.ApplyWeatherUnits(unit);
		}));
		NewsRows(pg, s);
		pg.Add(GoogleRow());
		pg.Header("Search");
		pg.Add(ChoiceRow("start.searchengine", new[] { "Bing", "Google" }, s.PlaceSearchEngine == "Google" ? 1 : 0, delegate(int i)
		{
			SettingsStore.Update(delegate(AppSettings x) { x.PlaceSearchEngine = i == 1 ? "Google" : "Bing"; });
		}));
		pg.Add(ChoiceRow("start.browser", new[] { "MetroBrowser", "System default" }, string.Equals(s.PreferredBrowser, "System", StringComparison.OrdinalIgnoreCase) ? 1 : 0, delegate(int i)
		{
			SettingsStore.Update(delegate(AppSettings x) { x.PreferredBrowser = i == 1 ? "System" : "MetroBrowser"; });
		}));
		pg.Add(ButtonRow("start.explore", "Explore", delegate { MetroPlaceSearchWindow.ShowFor(""); }, "Maps and facts about a place"));
		pg.Header("More");
		pg.Add(LinkRow("start.windows"));
		return pg.Done();
	}

	// The News tile's source in the hub's own rows: the source as a choice (the built-in feeds or a custom one), the feed
	// address as a 28 DIP field (Enter or leaving it saves; an address that is not a public RSS or Atom feed says so under
	// the row), and Open News / Pin to Start as buttons.
	private void NewsRows(Pg pg, AppSettings s)
	{
		string feed = s.NewsFeedUrl ?? "";
		NewsFeedService.Source[] sources = NewsFeedService.Sources;
		string[] names = sources.Select((NewsFeedService.Source x) => x.Name).Append("Custom feed").ToArray();
		int index = Array.FindIndex(sources, (NewsFeedService.Source x) => x.Url == feed);
		TextBox box = FieldBox(240.0);
		box.Text = feed;
		TextBlock note = InlineNote(string.Empty, error: true);
		note.Visibility = Visibility.Collapsed;
		ComboBox? choice = null;
		bool quietChoice = false;
		void Save(string text)
		{
			string? url = NewsFeedService.NormalizeUrl(text);
			if (url == null)
			{
				note.Text = "Enter a public HTTP(S) RSS or Atom address.";
				note.Visibility = Visibility.Visible;
				return;
			}
			note.Visibility = Visibility.Collapsed;
			box.Text = url;
			if (url == (SettingsStore.FastSnapshot.NewsFeedUrl ?? ""))
			{
				return;
			}
			SettingsStore.Update(delegate(AppSettings x) { x.NewsFeedUrl = url; });
			HostApp?.ApplyNewsSource(url);
			int i = Array.FindIndex(sources, (NewsFeedService.Source x) => x.Url == url);
			if (choice != null && choice.SelectedIndex != (i < 0 ? names.Length - 1 : i))
			{
				// the choice follows the address typed (journaled like a pick, so Undo brings the earlier source back)
				quietChoice = true;
				choice.SelectedIndex = i < 0 ? names.Length - 1 : i;
				quietChoice = false;
			}
		}
		FrameworkElement sourceRow = ChoiceRow("start.news", names, index < 0 ? names.Length - 1 : index, delegate(int i)
		{
			if (quietChoice)
			{
				return;
			}
			if (i >= 0 && i < sources.Length)
			{
				box.Text = sources[i].Url;
				Save(sources[i].Url);
			}
			else
			{
				box.Focus();
				box.SelectAll();
			}
		}, "The headlines of the News tile and the News app.", width: 220.0);
		choice = Descendants<ComboBox>(sourceRow).FirstOrDefault();
		pg.Add(sourceRow);
		Grid boxRow = RowShell(HubCatalog.Title("start.newsfeed"), null, null, box, out _);
		StackPanel feedRow = new StackPanel();
		feedRow.Children.Add(boxRow);
		feedRow.Children.Add(note);
		Register("start.newsfeed", feedRow);
		pg.Add(feedRow);
		box.KeyDown += delegate(object sender, KeyEventArgs e)
		{
			if (e.Key == Key.Enter)
			{
				Save(box.Text);
				e.Handled = true;
			}
		};
		box.LostKeyboardFocus += delegate
		{
			if (box.Text.Trim() != (SettingsStore.FastSnapshot.NewsFeedUrl ?? ""))
			{
				Save(box.Text);
			}
		};
		StackPanel buttons = new StackPanel { Orientation = Orientation.Horizontal };
		Button open = HubButton("Open News", delegate { HostApp?.OpenNews(); });
		open.Margin = new Thickness(0.0, 0.0, 8.0, 0.0);
		buttons.Children.Add(open);
		buttons.Children.Add(HubButton("Pin to Start", delegate { HostApp?.PinNewsTile(); }));
		Grid newsApp = RowShell(HubCatalog.Title("start.newsapp"), null, null, buttons, out _);
		AutomationProperties.SetName(buttons, HubCatalog.Title("start.newsapp"));
		Register("start.newsapp", newsApp);
		pg.Add(newsApp);
	}

	// Google (Mail & Agenda): the status line doubles as the inline result of Sign out (no launcher toasts).
	private FrameworkElement GoogleRow()
	{
		bool configured = false;
		bool signedIn = false;
		bool calendar = false;
		try
		{
			configured = GoogleAuth.IsConfigured;
			signedIn = GoogleAuth.IsSignedIn;
			calendar = GoogleAuth.HasCalendarAccess;
		}
		catch
		{
		}
		string status = configured ? (signedIn ? "Signed in" : "Not signed in") : "Not set up — add OAuth credentials first";
		StackPanel buttons = new StackPanel { Orientation = Orientation.Horizontal };
		Grid row = RowShell(HubCatalog.Title("start.google"), status, null, buttons, out _);
		TextBlock? statusText = FindSecondary(row);
		if (signedIn)
		{
			Button? signOut = null;
			signOut = HubButton("Sign out", delegate
			{
				try
				{
					GoogleAuth.SignOut();
					Logger.Log("Google: signed out");
					if (statusText != null)
					{
						statusText.Text = "Signed out — the local connection was removed";
					}
					signOut!.Visibility = Visibility.Collapsed;
				}
				catch (Exception ex)
				{
					Logger.Log("Google disconnect failed: " + ex.GetType().Name);
					if (statusText != null)
					{
						statusText.Text = "Could not disconnect — manage the saved connection to try again";
					}
					GoogleCalendarConnectWindow.ShowFor(this);
				}
			});
			signOut.Margin = new Thickness(0.0, 0.0, 8.0, 0.0);
			buttons.Children.Add(signOut);
		}
		buttons.Children.Add(HubButton(calendar ? "Manage" : "Connect", delegate { GoogleCalendarConnectWindow.ShowFor(this); }));
		Register("start.google", row);
		return row;
	}

	// The Start background pictures (thumbnails decoded off the UI thread at their display size).
	private FrameworkElement StartBackgroundPicker()
	{
		StackPanel host = new StackPanel();
		host.Children.Add(new TextBlock { Text = HubCatalog.Title("start.background"), FontFamily = SegoeUi, FontSize = 13.5, Foreground = _pal.Ink, Margin = new Thickness(0.0, 0.0, 0.0, 8.0) });
		WrapPanel wrap = new WrapPanel();
		List<string> presets;
		try
		{
			presets = Wallpapers.Presets();
		}
		catch
		{
			presets = new List<string>();
		}
		int px = DevicePixels(70.0);
		StartScreen start = _start!;
		foreach (string wp in presets)
		{
			string path = wp;
			Border face = new Border { Width = 72.0, Height = 45.0, BorderBrush = _pal.CardBorder, BorderThickness = new Thickness(1.0), Background = _pal.Field };
			Button b = new Button
			{
				Template = _pal.PlainTemplate,
				Style = BareButton,
				Content = face,
				Margin = new Thickness(0.0, 0.0, 6.0, 6.0),
				Cursor = Cursors.Hand,
				ToolTip = Path.GetFileNameWithoutExtension(wp)
			};
			b.SetResourceReference(FrameworkElement.FocusVisualStyleProperty, "Metro81.Focus");
			AutomationProperties.SetName(b, "Start background " + Path.GetFileNameWithoutExtension(wp));
			b.Click += delegate { start.ApplyStartWallpaper(path); };
			ReadAsync(() => Thumb(path, px), delegate(BitmapSource? bmp)
			{
				if (bmp != null)
				{
					face.Background = new ImageBrush(bmp) { Stretch = Stretch.UniformToFill };
				}
			});
			wrap.Children.Add(b);
		}
		host.Children.Add(wrap);
		return host;
	}

	private FrameworkElement StartColourPicker()
	{
		StackPanel host = new StackPanel();
		host.Children.Add(new TextBlock { Text = HubCatalog.Title("start.bgcolor"), FontFamily = SegoeUi, FontSize = 13.5, Foreground = _pal.Ink, Margin = new Thickness(0.0, 0.0, 0.0, 8.0) });
		WrapPanel strip = new WrapPanel();
		StartScreen start = _start!;
		foreach (string hex in Palette)
		{
			string h = hex;
			Button b = new Button
			{
				Template = _pal.PlainTemplate,
				Style = BareButton,
				Content = new Border { Width = 24.0, Height = 24.0, Background = HubPalette.Frozen(HubPalette.ParseHex(hex, Colors.Gray)) },
				Margin = new Thickness(0.0, 0.0, 4.0, 4.0),
				Cursor = Cursors.Hand,
				ToolTip = hex
			};
			b.SetResourceReference(FrameworkElement.FocusVisualStyleProperty, "Metro81.Focus");
			AutomationProperties.SetName(b, "Start background colour " + hex);
			b.Click += delegate { start.ApplyStartColor(h); };
			strip.Children.Add(b);
		}
		host.Children.Add(strip);
		return host;
	}
}
