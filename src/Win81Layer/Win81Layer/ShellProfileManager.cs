using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;

namespace Win81Layer;

internal static class ShellProfileIds
{
	public const string Custom = "custom";
	public const string Windows81 = "windows81";
	public const string Windows7 = "windows7";
	public const string Native = "native";
}

internal sealed class ShellProfileDescriptor
{
	public string Id { get; init; } = ShellProfileIds.Native;

	public string Name { get; init; } = "Windows Native / Recovery";

	public string Summary { get; init; } = string.Empty;

	public int Glyph { get; init; }
}

// Only experience-owned settings live in a profile slot. Everything else remains common across profiles, so
// switching the shell never rolls back weather, app data, consent flags, autostart, pins, or the Start layout.
internal sealed class ShellProfileOwnedSettings
{
	public bool HotCornersEnabled { get; set; }
	public bool BootToStart { get; set; }
	public bool ReplaceStartMenu { get; set; }
	public bool Win7StartMenuEnabled { get; set; }
	public bool Win81LightCaption { get; set; }
	public bool ReplaceDesktopMenu { get; set; }
	public bool ReplaceSystemIcons { get; set; }
	public bool Replace81AppIcons { get; set; }
	public bool UseWin81Sounds { get; set; }
	public bool UseWin81Cursors { get; set; }
	public bool SuspendNativeStart { get; set; }
	public bool TaskbarEnabled { get; set; }
	public bool DominantMode { get; set; }
	// NOTE: TileScale + StartBg*/StartAccent/StartPattern are DELIBERATELY NOT profile-owned. They are personal
	// content (the user's Start background, accent, tile size) that must persist across every profile / desktop-
	// composition switch — same principle as pins and the Start layout (see the class comment). Switching the shell
	// ADAPTS the chrome around them; it never rewrites them. They live only in common AppSettings.
	public string MotionMode { get; set; } = "Authentic";
	public string DesktopCompositionMode { get; set; } = "native";
	public bool DeskCompTransparency { get; set; }
	public bool DeskCompShadows { get; set; }
	public bool DeskCompAnimations { get; set; }
	public int DeskCompBlur { get; set; }
	public int DeskCompAnimSpeed { get; set; }
	public bool TaskbarLocked { get; set; }
	public bool TaskbarAutoHide { get; set; }
	public string TaskbarSize { get; set; } = "Medium";
	public string TaskbarPosition { get; set; } = "Bottom";
	public string TaskbarAlignment { get; set; } = "Left";
	public string TaskbarCombine { get; set; } = "Always";
	public string TaskbarColorMode { get; set; } = "Wallpaper";
	public bool TaskbarTransparent { get; set; }
	public bool ShowSearch { get; set; }
	public bool ShowTaskView { get; set; }
	public bool ShowActionCenter { get; set; }
	public bool TaskbarSameOnAllDisplays { get; set; }
	public bool CharmCompact { get; set; }
	public bool CharmTransparent { get; set; }
	public bool CharmBlur { get; set; }

	public static ShellProfileOwnedSettings Capture(AppSettings source)
	{
		return new ShellProfileOwnedSettings
		{
			HotCornersEnabled = source.HotCornersEnabled,
			BootToStart = source.BootToStart,
			ReplaceStartMenu = source.ReplaceStartMenu,
			Win7StartMenuEnabled = source.Win7StartMenuEnabled,
			Win81LightCaption = source.Win81LightCaption,
			ReplaceDesktopMenu = source.ReplaceDesktopMenu,
			ReplaceSystemIcons = source.ReplaceSystemIcons,
			Replace81AppIcons = source.Replace81AppIcons,
			UseWin81Sounds = source.UseWin81Sounds,
			UseWin81Cursors = source.UseWin81Cursors,
			SuspendNativeStart = source.SuspendNativeStart,
			TaskbarEnabled = source.TaskbarEnabled,
			DominantMode = source.DominantMode,
			MotionMode = source.MotionMode ?? "Authentic",
			DesktopCompositionMode = source.DesktopCompositionMode ?? "native",
			DeskCompTransparency = source.DeskCompTransparency,
			DeskCompShadows = source.DeskCompShadows,
			DeskCompAnimations = source.DeskCompAnimations,
			DeskCompBlur = source.DeskCompBlur,
			DeskCompAnimSpeed = source.DeskCompAnimSpeed,
			TaskbarLocked = source.TaskbarLocked,
			TaskbarAutoHide = source.TaskbarAutoHide,
			TaskbarSize = source.TaskbarSize ?? "Medium",
			TaskbarPosition = source.TaskbarPosition ?? "Bottom",
			TaskbarAlignment = source.TaskbarAlignment ?? "Left",
			TaskbarCombine = source.TaskbarCombine ?? "Always",
			TaskbarColorMode = source.TaskbarColorMode ?? "Wallpaper",
			TaskbarTransparent = source.TaskbarTransparent,
			ShowSearch = source.ShowSearch,
			ShowTaskView = source.ShowTaskView,
			ShowActionCenter = source.ShowActionCenter,
			TaskbarSameOnAllDisplays = source.TaskbarSameOnAllDisplays,
			CharmCompact = source.CharmCompact,
			CharmTransparent = source.CharmTransparent,
			CharmBlur = source.CharmBlur
		};
	}

	public AppSettings ApplyTo(AppSettings common)
	{
		AppSettings target = common.Clone();
		target.HotCornersEnabled = HotCornersEnabled;
		target.BootToStart = BootToStart;
		target.ReplaceStartMenu = ReplaceStartMenu;
		target.Win7StartMenuEnabled = Win7StartMenuEnabled;
		target.Win81LightCaption = Win81LightCaption;
		target.ReplaceDesktopMenu = ReplaceDesktopMenu;
		target.ReplaceSystemIcons = ReplaceSystemIcons;
		target.Replace81AppIcons = Replace81AppIcons;
		target.UseWin81Sounds = UseWin81Sounds;
		target.UseWin81Cursors = UseWin81Cursors;
		target.SuspendNativeStart = SuspendNativeStart;
		target.TaskbarEnabled = TaskbarEnabled;
		target.DominantMode = DominantMode;
		// TileScale + StartBg*/StartAccent/StartPattern intentionally NOT copied here — they stay whatever the
		// common settings already hold, so a profile switch preserves the user's Start background / accent / tile size.
		target.MotionMode = MotionMode;
		target.DesktopCompositionMode = DesktopCompositionMode;
		target.DeskCompTransparency = DeskCompTransparency;
		target.DeskCompShadows = DeskCompShadows;
		target.DeskCompAnimations = DeskCompAnimations;
		target.DeskCompBlur = DeskCompBlur;
		target.DeskCompAnimSpeed = DeskCompAnimSpeed;
		target.TaskbarLocked = TaskbarLocked;
		target.TaskbarAutoHide = TaskbarAutoHide;
		target.TaskbarSize = TaskbarSize;
		target.TaskbarPosition = TaskbarPosition;
		target.TaskbarAlignment = TaskbarAlignment;
		target.TaskbarCombine = TaskbarCombine;
		target.TaskbarColorMode = TaskbarColorMode;
		target.TaskbarTransparent = TaskbarTransparent;
		target.ShowSearch = ShowSearch;
		target.ShowTaskView = ShowTaskView;
		target.ShowActionCenter = ShowActionCenter;
		target.TaskbarSameOnAllDisplays = TaskbarSameOnAllDisplays;
		target.CharmCompact = CharmCompact;
		target.CharmTransparent = CharmTransparent;
		target.CharmBlur = CharmBlur;
		return target;
	}
}

internal sealed class ShellProfileSlot
{
	public string ProfileId { get; set; } = ShellProfileIds.Custom;
	public int DefinitionVersion { get; set; } = 1;
	public DateTime CapturedUtc { get; set; }
	public ShellProfileOwnedSettings Settings { get; set; } = new ShellProfileOwnedSettings();
}

internal sealed class ShellProfileTransaction
{
	public string TransactionId { get; set; } = string.Empty;
	public string SourceProfileId { get; set; } = ShellProfileIds.Custom;
	public string TargetProfileId { get; set; } = ShellProfileIds.Native;
	public DateTime PreparedUtc { get; set; }
	public string BeforeCompositionPreviousMode { get; set; } = string.Empty;
	public ShellProfileOwnedSettings Before { get; set; } = new ShellProfileOwnedSettings();
	public ShellProfileOwnedSettings After { get; set; } = new ShellProfileOwnedSettings();
}

internal sealed class ShellProfileState
{
	public int SchemaVersion { get; set; } = 1;
	public string ActiveProfileId { get; set; } = ShellProfileIds.Custom;
	public string PreviousProfileId { get; set; } = string.Empty;
	public Dictionary<string, ShellProfileSlot> Slots { get; set; } = new Dictionary<string, ShellProfileSlot>(StringComparer.OrdinalIgnoreCase);
	public ShellProfileTransaction? Pending { get; set; }
	public string LastTransactionId { get; set; } = string.Empty;
	public string LastResult { get; set; } = "not-applied";
	public string LastError { get; set; } = string.Empty;
	public DateTime UpdatedUtc { get; set; }
}

internal sealed class ShellProfileApplyResult
{
	public bool Success { get; init; }
	public string ProfileId { get; init; } = string.Empty;
	public string Message { get; init; } = string.Empty;
	public IReadOnlyList<string> Errors { get; init; } = Array.Empty<string>();
}

internal sealed class ShellProfileStatus
{
	public string ActiveProfileId { get; init; } = ShellProfileIds.Custom;
	public string ActiveProfileName { get; init; } = "Existing custom setup";
	public string PreviousProfileId { get; init; } = string.Empty;
	public bool RecoveryPending { get; init; }
	public bool IsCustomized { get; init; }
	public int SavedSlots { get; init; }
	public string LastResult { get; init; } = string.Empty;
	public string LastError { get; init; } = string.Empty;
}

internal static class ShellProfileManager
{
	private const string StateName = "shell-profile";
	private const string TransactionMutexName = "Local\\Win81Layer.ShellProfileTransaction.v1";
	private const int DefinitionVersion = 1;
	private static readonly object Gate = new object();
	private static readonly JsonSerializerOptions Json = new JsonSerializerOptions
	{
		PropertyNameCaseInsensitive = true,
		WriteIndented = true
	};

	public static readonly string StatePath = Path.Combine(
		Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
		"Win81Layer",
		"shell-profile-state.json");

	public static readonly ShellProfileDescriptor[] Profiles = new[]
	{
		new ShellProfileDescriptor
		{
			Id = ShellProfileIds.Windows81,
			Name = "Windows 8.1 Enhanced",
			Summary = "Metro Start, Charms, flat composition and the replacement taskbar.",
			Glyph = 0xE80A
		},
		new ShellProfileDescriptor
		{
			Id = ShellProfileIds.Windows7,
			Name = "Windows 7 Enhanced",
			Summary = "Classic Start, Aero shell skin and the replacement taskbar.",
			Glyph = 0xE737
		},
		new ShellProfileDescriptor
		{
			Id = ShellProfileIds.Native,
			Name = "Windows Native / Recovery",
			Summary = "Restores native Start, taskbar, composition, sounds, cursors and icons.",
			Glyph = 0xE7F4
		}
	};

	public static void InitializeAndRecover()
	{
		Mutex? transactionMutex = AcquireTransactionMutex();
		if (transactionMutex == null)
		{
			Logger.Log("Shell profile startup recovery deferred: transaction manager is busy.");
			return;
		}
		try
		{
			lock (Gate)
			{
				ShellProfileState state = LoadOrInitializeLocked();
				if (state.Pending != null)
				{
					RecoverInterruptedLocked(state);
				}
			}
		}
		finally
		{
			ReleaseTransactionMutex(transactionMutex);
		}
	}

	public static ShellProfileStatus Describe()
	{
		Mutex? transactionMutex = AcquireTransactionMutex();
		if (transactionMutex == null)
		{
			return new ShellProfileStatus
			{
				LastResult = "busy",
				LastError = "Another shell profile transaction is still running."
			};
		}
		try
		{
			lock (Gate)
			{
				ShellProfileState state = LoadOrInitializeLocked();
				bool customized = state.Slots.TryGetValue(state.ActiveProfileId, out ShellProfileSlot? activeSlot)
					&& !OwnedEqual(activeSlot.Settings, ShellProfileOwnedSettings.Capture(SettingsStore.Load()));
				return new ShellProfileStatus
				{
					ActiveProfileId = state.ActiveProfileId,
					ActiveProfileName = DisplayName(state.ActiveProfileId),
					PreviousProfileId = state.PreviousProfileId,
					RecoveryPending = state.Pending != null,
					IsCustomized = customized,
					SavedSlots = state.Slots.Count,
					LastResult = state.LastResult,
					LastError = state.LastError
				};
			}
		}
		finally
		{
			ReleaseTransactionMutex(transactionMutex);
		}
	}

	public static ShellProfileApplyResult Apply(string targetProfileId)
	{
		return ApplyCore(targetProfileId, resetTargetSlot: false);
	}

	public static ShellProfileApplyResult ResetAndApply(string targetProfileId)
	{
		return ApplyCore(targetProfileId, resetTargetSlot: true);
	}

	public static ShellProfileApplyResult RestorePrevious()
	{
		Mutex? transactionMutex = AcquireTransactionMutex();
		if (transactionMutex == null)
		{
			return Failure(string.Empty, "Another shell profile transaction is still running.");
		}
		try
		{
			lock (Gate)
			{
				string previous = LoadOrInitializeLocked().PreviousProfileId;
				if (string.IsNullOrWhiteSpace(previous))
				{
					return Failure(string.Empty, "No previous experience profile has been committed.");
				}
				return ApplyCoreLocked(NormalizeTarget(previous), resetTargetSlot: false);
			}
		}
		finally
		{
			ReleaseTransactionMutex(transactionMutex);
		}
	}

	internal static ShellProfileOwnedSettings BuildDefaultsForTest(string profileId, AppSettings current)
	{
		return BuildDefaults(profileId, current);
	}

	internal static bool OwnedEqualForTest(ShellProfileOwnedSettings left, ShellProfileOwnedSettings right)
	{
		return OwnedEqual(left, right);
	}

	private static ShellProfileApplyResult ApplyCore(string targetProfileId, bool resetTargetSlot)
	{
		string targetId = NormalizeTarget(targetProfileId);
		if (!IsApplyTarget(targetId))
		{
			return Failure(targetId, "Unknown experience profile: " + targetProfileId);
		}

		Mutex? transactionMutex = AcquireTransactionMutex();
		if (transactionMutex == null)
		{
			return Failure(targetId, "Another shell profile transaction is still running.");
		}
		try
		{
			lock (Gate)
			{
				return ApplyCoreLocked(targetId, resetTargetSlot);
			}
		}
		finally
		{
			ReleaseTransactionMutex(transactionMutex);
		}
	}

	private static ShellProfileApplyResult ApplyCoreLocked(string targetId, bool resetTargetSlot)
	{
		ShellProfileState state = LoadOrInitializeLocked();
		if (state.Pending != null && !RecoverInterruptedLocked(state))
		{
			return Failure(targetId, "An interrupted profile transaction could not be recovered yet.");
		}

		AppSettings current = SettingsStore.Load();
		string sourceId = string.IsNullOrWhiteSpace(state.ActiveProfileId) ? ShellProfileIds.Custom : state.ActiveProfileId;
		ShellProfileOwnedSettings before = ShellProfileOwnedSettings.Capture(current);
		state.Slots[sourceId] = NewSlot(sourceId, before);
		if (resetTargetSlot)
		{
			state.Slots.Remove(targetId);
		}

		if (string.Equals(targetId, ShellProfileIds.Custom, StringComparison.OrdinalIgnoreCase)
			&& !state.Slots.ContainsKey(targetId))
		{
			return Failure(targetId, "The original custom baseline is no longer available.");
		}
		ShellProfileOwnedSettings after = state.Slots.TryGetValue(targetId, out ShellProfileSlot? saved)
			? CloneOwned(saved.Settings)
			: BuildDefaults(targetId, current);
		AppSettings target = after.ApplyTo(current);
		if (!string.Equals(current.DesktopCompositionMode, target.DesktopCompositionMode, StringComparison.OrdinalIgnoreCase))
		{
			target.DeskCompPreviousMode = current.DesktopCompositionMode;
		}
		target.DeskCompPreviewMode = string.Empty;
		target.DeskCompPreviewUntilUtcTicks = 0L;
		string transactionId = Guid.NewGuid().ToString("N");
		state.Pending = new ShellProfileTransaction
		{
			TransactionId = transactionId,
			SourceProfileId = sourceId,
			TargetProfileId = targetId,
			PreparedUtc = DateTime.UtcNow,
			BeforeCompositionPreviousMode = current.DeskCompPreviousMode ?? string.Empty,
			Before = CloneOwned(before),
			After = CloneOwned(after)
		};
		state.LastTransactionId = transactionId;
		state.LastResult = "prepared";
		state.LastError = string.Empty;
		state.UpdatedUtc = DateTime.UtcNow;
		if (!SaveStateLocked(state))
		{
			return Failure(targetId, "The profile transaction journal could not be stored.");
		}

		if (!SettingsStore.TrySave(target))
		{
			return RollbackLocked(state, current, target, "Target settings were not durable.");
		}

		List<string> runtimeErrors = ApplyRuntime(current, target);
		ShellProfileOwnedSettings persisted = ShellProfileOwnedSettings.Capture(SettingsStore.Load());
		if (!OwnedEqual(after, persisted))
		{
			runtimeErrors.Add("The read-back settings do not match the selected profile.");
		}
		if (runtimeErrors.Count > 0)
		{
			return RollbackLocked(state, current, target, string.Join(" ", runtimeErrors));
		}

		ShellProfileTransaction transaction = state.Pending;
		string previousProfileBeforeCommit = state.PreviousProfileId;
		state.ActiveProfileId = targetId;
		if (!string.Equals(sourceId, targetId, StringComparison.OrdinalIgnoreCase))
		{
			state.PreviousProfileId = sourceId;
		}
		state.Slots[targetId] = NewSlot(targetId, after);
		state.Pending = null;
		state.LastResult = "committed";
		state.LastError = string.Empty;
		state.UpdatedUtc = DateTime.UtcNow;
		if (!SaveStateLocked(state))
		{
			state.ActiveProfileId = sourceId;
			state.PreviousProfileId = previousProfileBeforeCommit;
			state.Pending = transaction;
			return RollbackLocked(state, current, target, "The final profile commit could not be stored.");
		}

		ShellProfileChangeSignal.Set();
		Logger.Log($"Shell profile committed: {sourceId} -> {targetId}; transaction={transactionId}");
		return new ShellProfileApplyResult
		{
			Success = true,
			ProfileId = targetId,
			Message = DisplayName(targetId) + " applied."
		};
	}

	private static ShellProfileApplyResult RollbackLocked(ShellProfileState state, AppSettings beforeCommon, AppSettings failedTarget, string reason)
	{
		ShellProfileTransaction? transaction = state.Pending;
		if (transaction == null)
		{
			return Failure(state.ActiveProfileId, reason);
		}

		AppSettings rollbackBase = SettingsStore.Load();
		AppSettings rollback = transaction.Before.ApplyTo(rollbackBase);
		rollback.DeskCompPreviousMode = beforeCommon.DeskCompPreviousMode ?? string.Empty;
		rollback.DeskCompPreviewMode = string.Empty;
		rollback.DeskCompPreviewUntilUtcTicks = 0L;
		if (!SettingsStore.TrySave(rollback))
		{
			state.LastResult = "rollback-pending";
			state.LastError = reason + " Rollback settings could not be stored; startup recovery remains armed.";
			state.UpdatedUtc = DateTime.UtcNow;
			SaveStateLocked(state);
			Logger.Log("Shell profile rollback remains pending: " + state.LastError);
			return Failure(transaction.TargetProfileId, state.LastError);
		}

		List<string> rollbackErrors = ApplyRuntime(failedTarget, rollback);
		state.ActiveProfileId = transaction.SourceProfileId;
		state.Slots[transaction.SourceProfileId] = NewSlot(transaction.SourceProfileId, transaction.Before);
		state.Pending = rollbackErrors.Count == 0 ? null : transaction;
		state.LastResult = rollbackErrors.Count == 0 ? "rolled-back" : "rollback-runtime-pending";
		state.LastError = rollbackErrors.Count == 0 ? reason : reason + " Rollback: " + string.Join(" ", rollbackErrors);
		state.UpdatedUtc = DateTime.UtcNow;
		bool rollbackStateStored = SaveStateLocked(state);
		if (!rollbackStateStored)
		{
			state.Pending = transaction;
			state.LastResult = "rollback-journal-pending";
			state.LastError += " The rollback completed, but its journal cleanup is still pending.";
			SaveStateLocked(state);
		}
		ShellProfileChangeSignal.Set();
		Logger.Log($"Shell profile transaction rolled back to {state.ActiveProfileId}: {state.LastError}");
		return new ShellProfileApplyResult
		{
			Success = false,
			ProfileId = transaction.TargetProfileId,
			Message = state.Pending == null
				? "Profile apply failed and the previous setup was restored."
				: "Profile apply failed; the previous setup was restored and startup recovery remains armed.",
			Errors = new[] { state.LastError }
		};
	}

	private static bool RecoverInterruptedLocked(ShellProfileState state)
	{
		ShellProfileTransaction? transaction = state.Pending;
		if (transaction == null)
		{
			return true;
		}
		AppSettings current = SettingsStore.Load();
		AppSettings rollback = transaction.Before.ApplyTo(current);
		rollback.DeskCompPreviousMode = transaction.BeforeCompositionPreviousMode ?? string.Empty;
		rollback.DeskCompPreviewMode = string.Empty;
		rollback.DeskCompPreviewUntilUtcTicks = 0L;
		if (!SettingsStore.TrySave(rollback))
		{
			state.LastResult = "recovery-pending";
			state.LastError = "Interrupted profile transaction could not restore its source settings.";
			state.UpdatedUtc = DateTime.UtcNow;
			SaveStateLocked(state);
			Logger.Log("Shell profile startup recovery remains pending.");
			return false;
		}

		List<string> errors = ApplyRuntime(current, rollback);
		state.ActiveProfileId = transaction.SourceProfileId;
		state.Slots[transaction.SourceProfileId] = NewSlot(transaction.SourceProfileId, transaction.Before);
		state.Pending = errors.Count == 0 ? null : transaction;
		state.LastResult = errors.Count == 0 ? "recovered-interrupted-transaction" : "recovery-runtime-pending";
		state.LastError = errors.Count == 0 ? string.Empty : string.Join(" ", errors);
		state.UpdatedUtc = DateTime.UtcNow;
		bool stored = SaveStateLocked(state);
		if (!stored)
		{
			state.Pending = transaction;
			state.LastResult = "recovery-journal-pending";
			state.LastError = string.IsNullOrWhiteSpace(state.LastError)
				? "Recovered settings are active, but the transaction journal could not be cleared."
				: state.LastError + " The transaction journal could not be cleared.";
			SaveStateLocked(state);
		}
		ShellProfileChangeSignal.Set();
		Logger.Log($"Shell profile startup recovery -> {state.ActiveProfileId}; stored={stored}; errors={state.LastError}");
		return stored && state.Pending == null;
	}

	private static List<string> ApplyRuntime(AppSettings before, AppSettings after)
	{
		List<string> errors = new List<string>();
		try
		{
			DesktopComposition.ApplyPersistedModeTransition(before.DesktopCompositionMode);
			if (DesktopComposition.LastStatus is DesktopComposition.ApplyStatus.Degraded or DesktopComposition.ApplyStatus.Partial)
			{
				errors.Add("Composition: " + (string.IsNullOrWhiteSpace(DesktopComposition.LastError) ? DesktopComposition.LastStatus.ToString() : DesktopComposition.LastError));
			}
		}
		catch (Exception ex)
		{
			errors.Add("Composition transition failed: " + ex.Message);
		}

		try
		{
			if (after.ReplaceSystemIcons) { SystemIcons81.Apply(); RecycleBinWatcher.Start(); } else { SystemIcons81.Restore(); }   // Restore() stops the watcher
			if (SystemIcons81.IsApplied != after.ReplaceSystemIcons)
			{
				errors.Add("System icon state did not verify.");
			}
		}
		catch (Exception ex)
		{
			errors.Add("System icons failed: " + ex.Message);
		}

		try
		{
			if (after.UseWin81Sounds) SoundScheme.Apply(); else SoundScheme.Revert();
			if (SoundScheme.IsApplied != after.UseWin81Sounds)
			{
				errors.Add("Sound scheme state did not verify.");
			}
		}
		catch (Exception ex)
		{
			errors.Add("Sound scheme failed: " + ex.Message);
		}

		try
		{
			if (after.UseWin81Cursors) CursorScheme.Apply(); else CursorScheme.Revert();
			if (CursorScheme.IsApplied != after.UseWin81Cursors)
			{
				errors.Add("Cursor scheme state did not verify.");
			}
		}
		catch (Exception ex)
		{
			errors.Add("Cursor scheme failed: " + ex.Message);
		}

		try
		{
			AppIconOverrides.RefreshAllSurfaces();
			if (System.Windows.Application.Current is App app)
			{
				app.ApplyShellProfileLive(after);
			}
		}
		catch (Exception ex)
		{
			errors.Add("Live shell refresh failed: " + ex.Message);
		}
		return errors;
	}

	internal static bool ReadOnlyDiagnosticsSupported => true;

	private static ShellProfileState LoadOrInitializeLocked()
	{
		DurableStateReadResult read = DurableStateStore.Read(StateName, StatePath, null, ValidateStateJson, 8);
		if (read.Json != null)
		{
			try
			{
				ShellProfileState? parsed = JsonSerializer.Deserialize<ShellProfileState>(read.Json, Json);
				if (parsed != null)
				{
					NormalizeState(parsed);
					return parsed;
				}
			}
			catch (Exception ex)
			{
				Logger.Log("Shell profile state parse failed after validation: " + ex.Message);
			}
		}

		AppSettings current = SettingsStore.Load();
		bool migrated = false;
		if (SoundScheme.IsApplied && !current.UseWin81Sounds)
		{
			current.UseWin81Sounds = true;
			migrated = true;
		}
		if (CursorScheme.IsApplied && !current.UseWin81Cursors)
		{
			current.UseWin81Cursors = true;
			migrated = true;
		}
		if (migrated && !SettingsStore.ReadOnlyDiagnostics)
		{
			SettingsStore.TrySave(current);
		}

		ShellProfileState created = new ShellProfileState
		{
			ActiveProfileId = ShellProfileIds.Custom,
			LastResult = "initialized-custom-baseline",
			UpdatedUtc = DateTime.UtcNow
		};
		created.Slots[ShellProfileIds.Custom] = NewSlot(ShellProfileIds.Custom, ShellProfileOwnedSettings.Capture(current));
		if (!SettingsStore.ReadOnlyDiagnostics) SaveStateLocked(created);
		Logger.Log("Shell profile manager initialized; existing setup preserved as custom baseline.");
		return created;
	}

	private static ShellProfileOwnedSettings BuildDefaults(string profileId, AppSettings current)
	{
		ShellProfileOwnedSettings value = ShellProfileOwnedSettings.Capture(current);
		switch (profileId)
		{
			case ShellProfileIds.Windows81:
				value.HotCornersEnabled = true;
				value.BootToStart = true;
				value.ReplaceStartMenu = true;
				value.Win7StartMenuEnabled = false;
				value.Win81LightCaption = true;
				value.ReplaceDesktopMenu = true;
				value.ReplaceSystemIcons = true;
				value.Replace81AppIcons = true;
				value.UseWin81Sounds = true;
				value.UseWin81Cursors = true;
				value.SuspendNativeStart = true;
				value.TaskbarEnabled = true;
				value.DominantMode = true;
				value.MotionMode = "Authentic";
				value.DesktopCompositionMode = "windows81";
				value.DeskCompTransparency = false;
				value.DeskCompShadows = true;
				value.DeskCompAnimations = true;
				value.DeskCompBlur = 0;
				value.DeskCompAnimSpeed = 50;
				value.TaskbarAutoHide = false;
				value.TaskbarSize = "Medium";
				value.TaskbarPosition = "Bottom";
				value.TaskbarAlignment = "Left";
				value.TaskbarCombine = "Always";
				value.TaskbarColorMode = "Wallpaper";
				value.TaskbarTransparent = true;
				value.ShowSearch = false;
				value.ShowTaskView = false;
				value.ShowActionCenter = true;
				value.TaskbarSameOnAllDisplays = true;
				value.CharmCompact = false;
				value.CharmTransparent = false;
				value.CharmBlur = false;
				break;

			case ShellProfileIds.Windows7:
				value.HotCornersEnabled = false;
				value.BootToStart = false;
				value.ReplaceStartMenu = true;
				value.Win7StartMenuEnabled = true;
				value.ReplaceDesktopMenu = true;
				value.ReplaceSystemIcons = false;
				value.Replace81AppIcons = false;
				value.UseWin81Sounds = false;
				value.UseWin81Cursors = false;
				value.SuspendNativeStart = true;
				value.TaskbarEnabled = true;
				value.DominantMode = true;
				value.MotionMode = "Authentic";
				value.DesktopCompositionMode = "windows7-aero";
				value.DeskCompTransparency = true;
				value.DeskCompShadows = true;
				value.DeskCompAnimations = true;
				value.DeskCompBlur = 65;
				value.DeskCompAnimSpeed = 50;
				value.TaskbarAutoHide = false;
				value.TaskbarSize = "Medium";
				value.TaskbarPosition = "Bottom";
				value.TaskbarAlignment = "Left";
				value.TaskbarCombine = "Always";
				value.TaskbarColorMode = "Wallpaper";
				value.TaskbarTransparent = true;
				value.ShowSearch = false;
				value.ShowTaskView = false;
				value.ShowActionCenter = true;
				value.TaskbarSameOnAllDisplays = true;
				value.CharmCompact = true;
				value.CharmTransparent = true;
				value.CharmBlur = true;
				break;

			case ShellProfileIds.Native:
				value.HotCornersEnabled = false;
				value.BootToStart = false;
				value.ReplaceStartMenu = false;
				value.Win7StartMenuEnabled = false;
				value.ReplaceDesktopMenu = false;
				value.ReplaceSystemIcons = false;
				value.Replace81AppIcons = false;
				value.UseWin81Sounds = false;
				value.UseWin81Cursors = false;
				value.SuspendNativeStart = false;
				value.TaskbarEnabled = false;
				value.DominantMode = false;
				value.DesktopCompositionMode = "native";
				value.DeskCompTransparency = false;
				value.CharmCompact = false;
				value.CharmTransparent = false;
				value.CharmBlur = false;
				break;
		}
		return value;
	}

	private static ShellProfileSlot NewSlot(string profileId, ShellProfileOwnedSettings settings)
	{
		return new ShellProfileSlot
		{
			ProfileId = profileId,
			DefinitionVersion = DefinitionVersion,
			CapturedUtc = DateTime.UtcNow,
			Settings = CloneOwned(settings)
		};
	}

	private static ShellProfileOwnedSettings CloneOwned(ShellProfileOwnedSettings source)
	{
		string json = JsonSerializer.Serialize(source, Json);
		return JsonSerializer.Deserialize<ShellProfileOwnedSettings>(json, Json) ?? new ShellProfileOwnedSettings();
	}

	private static bool OwnedEqual(ShellProfileOwnedSettings left, ShellProfileOwnedSettings right)
	{
		return string.Equals(JsonSerializer.Serialize(left, Json), JsonSerializer.Serialize(right, Json), StringComparison.Ordinal);
	}

	private static bool SaveStateLocked(ShellProfileState state)
	{
		NormalizeState(state);
		state.UpdatedUtc = DateTime.UtcNow;
		string json = JsonSerializer.Serialize(state, Json);
		return DurableStateStore.Commit(StateName, StatePath, json, ValidateStateJson).IsDurable;
	}

	private static Mutex? AcquireTransactionMutex()
	{
		Mutex? mutex = null;
		try
		{
			mutex = new Mutex(false, TransactionMutexName);
			try
			{
				if (!mutex.WaitOne(TimeSpan.FromSeconds(15)))
				{
					mutex.Dispose();
					return null;
				}
			}
			catch (AbandonedMutexException)
			{
				Logger.Log("Shell profile transaction mutex was abandoned; ownership recovered.");
			}
			return mutex;
		}
		catch (Exception ex)
		{
			try { mutex?.Dispose(); } catch { }
			Logger.Log("Shell profile transaction mutex failed: " + ex.Message);
			return null;
		}
	}

	private static void ReleaseTransactionMutex(Mutex mutex)
	{
		try { mutex.ReleaseMutex(); } catch { }
		try { mutex.Dispose(); } catch { }
	}

	private static bool ValidateStateJson(string json)
	{
		try
		{
			ShellProfileState? state = JsonSerializer.Deserialize<ShellProfileState>(json, Json);
			if (state == null || state.SchemaVersion != 1 || string.IsNullOrWhiteSpace(state.ActiveProfileId) || state.Slots == null)
			{
				return false;
			}
			foreach (KeyValuePair<string, ShellProfileSlot> item in state.Slots)
			{
				if (string.IsNullOrWhiteSpace(item.Key) || item.Value?.Settings == null)
				{
					return false;
				}
			}
			return state.Pending == null
				|| (!string.IsNullOrWhiteSpace(state.Pending.TransactionId)
					&& !string.IsNullOrWhiteSpace(state.Pending.SourceProfileId)
					&& !string.IsNullOrWhiteSpace(state.Pending.TargetProfileId)
					&& state.Pending.Before != null
					&& state.Pending.After != null);
		}
		catch
		{
			return false;
		}
	}

	private static void NormalizeState(ShellProfileState state)
	{
		state.SchemaVersion = 1;
		state.ActiveProfileId = string.IsNullOrWhiteSpace(state.ActiveProfileId) ? ShellProfileIds.Custom : state.ActiveProfileId.Trim().ToLowerInvariant();
		state.PreviousProfileId = state.PreviousProfileId?.Trim().ToLowerInvariant() ?? string.Empty;
		Dictionary<string, ShellProfileSlot> slots = new Dictionary<string, ShellProfileSlot>(StringComparer.OrdinalIgnoreCase);
		if (state.Slots != null)
		{
			foreach (KeyValuePair<string, ShellProfileSlot> item in state.Slots)
			{
				if (!string.IsNullOrWhiteSpace(item.Key) && item.Value?.Settings != null)
				{
					item.Value.ProfileId = item.Key.Trim().ToLowerInvariant();
					slots[item.Value.ProfileId] = item.Value;
				}
			}
		}
		state.Slots = slots;
		state.LastTransactionId ??= string.Empty;
		state.LastResult ??= string.Empty;
		state.LastError ??= string.Empty;
	}

	private static bool IsApplyTarget(string profileId)
	{
		return profileId is ShellProfileIds.Windows81 or ShellProfileIds.Windows7 or ShellProfileIds.Native or ShellProfileIds.Custom;
	}

	private static string NormalizeTarget(string profileId)
	{
		return profileId?.Trim().ToLowerInvariant() switch
		{
			"windows8.1" or "win81" or "8.1" => ShellProfileIds.Windows81,
			"windows7-aero" or "win7" or "7" => ShellProfileIds.Windows7,
			"windows-native" or "recovery" => ShellProfileIds.Native,
			string value => value,
			_ => string.Empty
		};
	}

	private static string DisplayName(string profileId)
	{
		if (string.Equals(profileId, ShellProfileIds.Custom, StringComparison.OrdinalIgnoreCase))
		{
			return "Existing custom setup";
		}
		foreach (ShellProfileDescriptor profile in Profiles)
		{
			if (string.Equals(profile.Id, profileId, StringComparison.OrdinalIgnoreCase))
			{
				return profile.Name;
			}
		}
		return profileId;
	}

	private static ShellProfileApplyResult Failure(string profileId, string message)
	{
		Logger.Log("Shell profile: " + message);
		return new ShellProfileApplyResult
		{
			Success = false,
			ProfileId = profileId,
			Message = message,
			Errors = new[] { message }
		};
	}
}
