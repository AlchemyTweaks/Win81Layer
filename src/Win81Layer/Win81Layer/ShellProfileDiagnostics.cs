using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;

namespace Win81Layer;

internal static class ShellProfileDiagnostics
{
	private static readonly JsonSerializerOptions Json = new JsonSerializerOptions
	{
		PropertyNameCaseInsensitive = true,
		WriteIndented = true
	};

	public static readonly string SelfTestPath = Path.Combine(PersistenceDiagnostics.QaRoot, "shell-profile-selftest-latest.json");

	public static readonly string StatusPath = Path.Combine(PersistenceDiagnostics.QaRoot, "shell-profile-status-latest.json");

	public static string RunSelfTest()
	{
		Directory.CreateDirectory(PersistenceDiagnostics.QaRoot);
		string stateName = "qa-shell-profile-" + Guid.NewGuid().ToString("N");
		string tempRoot = Path.Combine(Path.GetTempPath(), "Win81Layer-shell-profile-qa-" + Guid.NewGuid().ToString("N"));
		string primary = Path.Combine(tempRoot, "shell-profile-state.json");
		List<string> checks = new List<string>();
		bool passed = false;
		string? error = null;
		try
		{
			Directory.CreateDirectory(tempRoot);
			AppSettings common = new AppSettings
			{
				WeatherCity = "Common setting must survive",
				EnableExperimentalFeatures = true,
				AutoRecoverShell = true,
				ScreenshotApp = "unchanged.exe",
				RunFirstTask = true,
				DeskCompOptimizePerf = true,
				UseDwmBlurGlassForWin7 = false,
				TaskbarSize = "Large",
				TaskbarAlignment = "Center",
				DesktopCompositionMode = "windows7-aero",
				DeskCompPreviousMode = "windows81",
				DeskCompPreviewMode = "alchemy",
				DeskCompPreviewUntilUtcTicks = DateTime.UtcNow.AddMinutes(1).Ticks
			};

			ShellProfileOwnedSettings windows81 = ShellProfileManager.BuildDefaultsForTest(ShellProfileIds.Windows81, common);
			AppSettings applied81 = windows81.ApplyTo(common);
			Require(applied81.DominantMode && applied81.TaskbarEnabled && applied81.SuspendNativeStart, "8.1 takeover invariants");
			Require(!applied81.Win7StartMenuEnabled && applied81.HotCornersEnabled, "8.1 Start/Charms invariants");
			Require(applied81.UseWin81Sounds && applied81.UseWin81Cursors && applied81.ReplaceSystemIcons, "8.1 asset invariants");
			Require(applied81.DesktopCompositionMode == "windows81", "8.1 composition invariant");
			Require(CommonSettingsPreserved(common, applied81), "8.1 common settings preservation");
			checks.Add("windows81-invariants");

			ShellProfileOwnedSettings windows7 = ShellProfileManager.BuildDefaultsForTest(ShellProfileIds.Windows7, applied81);
			AppSettings applied7 = windows7.ApplyTo(applied81);
			Require(applied7.Win7StartMenuEnabled && applied7.TaskbarEnabled && applied7.DominantMode, "7 shell invariants");
			Require(!applied7.HotCornersEnabled && !applied7.UseWin81Sounds && !applied7.Replace81AppIcons, "7 separation from 8.1 assets");
			Require(applied7.DesktopCompositionMode == "windows7-aero", "7 composition invariant");
			Require(CommonSettingsPreserved(common, applied7), "7 common settings preservation");
			checks.Add("windows7-invariants");

			CompositionProfile aero7 = CompositionProfiles.Resolve("windows7-aero");
			CompositionProfile alchemy = CompositionProfiles.Resolve("alchemy-enhanced");
			Require(!aero7.ExternalAeroGlass && aero7.OwnedFrameStyle == "aero", "7 owned Aero frame contract");
			Require(aero7.Backdrop == "none" && aero7.Corners == "round", "7 excludes modern backdrop and keeps round geometry");
			Require(!alchemy.ExternalAeroGlass, "Alchemy cannot activate the global Win7 DWM bridge");
			checks.Add("windows7-owned-aero-frame-contract");

			ShellProfileOwnedSettings native = ShellProfileManager.BuildDefaultsForTest(ShellProfileIds.Native, applied7);
			AppSettings appliedNative = native.ApplyTo(applied7);
			Require(!appliedNative.ReplaceStartMenu && !appliedNative.TaskbarEnabled && !appliedNative.SuspendNativeStart && !appliedNative.DominantMode, "native takeover disabled");
			Require(!appliedNative.ReplaceSystemIcons && !appliedNative.UseWin81Sounds && !appliedNative.UseWin81Cursors, "native assets restored");
			Require(appliedNative.DesktopCompositionMode == "native", "native composition invariant");
			Require(CommonSettingsPreserved(common, appliedNative), "native common settings preservation");
			checks.Add("native-recovery-invariants");

			windows81.TaskbarSize = "Small";
			windows81.TaskbarAlignment = "Center";
			AppSettings commonAfterOtherProfile = applied7.Clone();
			commonAfterOtherProfile.WeatherCity = "Changed while in Windows 7";
			AppSettings restored81Slot = windows81.ApplyTo(commonAfterOtherProfile);
			Require(restored81Slot.TaskbarSize == "Small" && restored81Slot.TaskbarAlignment == "Center", "profile slot customization retained");
			Require(restored81Slot.WeatherCity == "Changed while in Windows 7", "new common setting retained while restoring slot");
			checks.Add("profile-slot-roundtrip");

			ShellProfileOwnedSettings before = ShellProfileOwnedSettings.Capture(common);
			ShellProfileState prepared = new ShellProfileState
			{
				ActiveProfileId = ShellProfileIds.Custom,
				LastResult = "prepared",
				UpdatedUtc = DateTime.UtcNow,
				Pending = new ShellProfileTransaction
				{
					TransactionId = Guid.NewGuid().ToString("N"),
					SourceProfileId = ShellProfileIds.Custom,
					TargetProfileId = ShellProfileIds.Windows81,
					PreparedUtc = DateTime.UtcNow,
					BeforeCompositionPreviousMode = common.DeskCompPreviousMode,
					Before = before,
					After = windows81
				}
			};
			prepared.Slots[ShellProfileIds.Custom] = new ShellProfileSlot
			{
				ProfileId = ShellProfileIds.Custom,
				DefinitionVersion = 1,
				CapturedUtc = DateTime.UtcNow,
				Settings = before
			};
			string preparedJson = JsonSerializer.Serialize(prepared, Json);
			DurableStateCommitResult commit = DurableStateStore.Commit(stateName, primary, preparedJson, ValidState);
			Require(commit.PrimaryCommitted && commit.RecoveryCommitted, "prepared journal durable commit");
			DurableStateReadResult read = DurableStateStore.Read(stateName, primary, null, ValidState, 2);
			ShellProfileState loaded = JsonSerializer.Deserialize<ShellProfileState>(read.Json!, Json)!;
			Require(loaded.Pending != null && loaded.Pending.TargetProfileId == ShellProfileIds.Windows81, "prepared journal round-trip");
			checks.Add("prepared-journal-roundtrip");

			AppSettings simulatedTarget = loaded.Pending!.After.ApplyTo(common);
			simulatedTarget.WeatherCity = "Changed during transaction";
			simulatedTarget.DeskCompPreviousMode = common.DesktopCompositionMode;
			simulatedTarget.DeskCompPreviewMode = string.Empty;
			simulatedTarget.DeskCompPreviewUntilUtcTicks = 0L;
			AppSettings recovered = loaded.Pending.Before.ApplyTo(simulatedTarget);
			recovered.DeskCompPreviousMode = loaded.Pending.BeforeCompositionPreviousMode;
			recovered.DeskCompPreviewMode = string.Empty;
			recovered.DeskCompPreviewUntilUtcTicks = 0L;
			Require(ShellProfileManager.OwnedEqualForTest(ShellProfileOwnedSettings.Capture(recovered), loaded.Pending.Before), "recovery restores source-owned settings");
			Require(recovered.WeatherCity == "Changed during transaction", "recovery preserves common settings");
			Require(recovered.DeskCompPreviousMode == "windows81" && recovered.DeskCompPreviewMode.Length == 0 && recovered.DeskCompPreviewUntilUtcTicks == 0L, "recovery cancels interrupted composition preview safely");
			loaded.ActiveProfileId = loaded.Pending.SourceProfileId;
			loaded.Pending = null;
			loaded.LastResult = "recovered-interrupted-transaction";
			string recoveredJson = JsonSerializer.Serialize(loaded, Json);
			DurableStateCommitResult recoveryCommit = DurableStateStore.Commit(stateName, primary, recoveredJson, ValidState);
			Require(recoveryCommit.IsDurable, "recovered state durable commit");
			DurableStateReadResult recoveredRead = DurableStateStore.Read(stateName, primary, null, ValidState, 2);
			ShellProfileState recoveredState = JsonSerializer.Deserialize<ShellProfileState>(recoveredRead.Json!, Json)!;
			Require(recoveredState.Pending == null && recoveredState.ActiveProfileId == ShellProfileIds.Custom, "interrupted transaction cleared after rollback");
			checks.Add("crash-recovery-rollback");
			checks.Add("composition-preview-recovery");
			passed = true;
		}
		catch (Exception ex)
		{
			error = ex.ToString();
		}
		finally
		{
			TryDeleteTree(tempRoot);
			TryDelete(DurableStateStore.PendingPath(stateName));
			TryDelete(DurableStateStore.LastGoodPath(stateName));
			TryDelete(DurableStateStore.PreviousPath(stateName));
		}

		AtomicWrite(SelfTestPath, JsonSerializer.Serialize(new
		{
			SchemaVersion = 1,
			GeneratedUtc = DateTime.UtcNow,
			Passed = passed,
			Checks = checks,
			Error = error
		}, Json));
		Logger.Log($"Shell profile self-test {(passed ? "PASSED" : "FAILED")}: {SelfTestPath}");
		return SelfTestPath;
	}

	public static string WriteStatus()
	{
		ShellProfileStatus status = ShellProfileManager.Describe();
		AppSettings settings = SettingsStore.Load();
		AtomicWrite(StatusPath, JsonSerializer.Serialize(new
		{
			SchemaVersion = 1,
			GeneratedUtc = DateTime.UtcNow,
			StatePath = ShellProfileManager.StatePath,
			status.ActiveProfileId,
			status.ActiveProfileName,
			status.PreviousProfileId,
			status.RecoveryPending,
			status.IsCustomized,
			status.SavedSlots,
			status.LastResult,
			status.LastError,
			Effective = new
			{
				settings.DesktopCompositionMode,
				settings.Win7StartMenuEnabled,
				settings.DominantMode,
				settings.TaskbarEnabled,
				settings.SuspendNativeStart,
				settings.ReplaceSystemIcons,
				settings.Replace81AppIcons,
				settings.UseWin81Sounds,
				settings.UseWin81Cursors
			}
		}, Json));
		Logger.Log("Shell profile status report: " + StatusPath);
		return StatusPath;
	}

	private static bool CommonSettingsPreserved(AppSettings expected, AppSettings actual)
	{
		return expected.WeatherCity == actual.WeatherCity
			&& expected.EnableExperimentalFeatures == actual.EnableExperimentalFeatures
			&& expected.AutoRecoverShell == actual.AutoRecoverShell
			&& expected.ScreenshotApp == actual.ScreenshotApp
			&& expected.RunFirstTask == actual.RunFirstTask
			&& expected.DeskCompOptimizePerf == actual.DeskCompOptimizePerf
			&& expected.UseDwmBlurGlassForWin7 == actual.UseDwmBlurGlassForWin7;
	}

	private static bool ValidState(string json)
	{
		try
		{
			ShellProfileState? state = JsonSerializer.Deserialize<ShellProfileState>(json, Json);
			return state != null
				&& state.SchemaVersion == 1
				&& !string.IsNullOrWhiteSpace(state.ActiveProfileId)
				&& state.Slots != null
				&& (state.Pending == null
					|| (!string.IsNullOrWhiteSpace(state.Pending.TransactionId)
						&& state.Pending.Before != null
						&& state.Pending.After != null));
		}
		catch
		{
			return false;
		}
	}

	private static void Require(bool condition, string message)
	{
		if (!condition)
		{
			throw new InvalidOperationException("Shell profile self-test assertion failed: " + message);
		}
	}

	private static void AtomicWrite(string path, string content)
	{
		Directory.CreateDirectory(Path.GetDirectoryName(path) ?? PersistenceDiagnostics.QaRoot);
		string temp = path + ".tmp-" + Environment.ProcessId + "-" + Guid.NewGuid().ToString("N");
		try
		{
			using (FileStream stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
			using (StreamWriter writer = new StreamWriter(stream, new UTF8Encoding(false)))
			{
				writer.Write(content);
				writer.Flush();
				stream.Flush(flushToDisk: true);
			}
			File.Move(temp, path, overwrite: true);
		}
		finally
		{
			TryDelete(temp);
		}
	}

	private static void TryDelete(string path)
	{
		try
		{
			if (File.Exists(path)) File.Delete(path);
		}
		catch
		{
		}
	}

	private static void TryDeleteTree(string path)
	{
		try
		{
			if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
		}
		catch
		{
		}
	}
}
