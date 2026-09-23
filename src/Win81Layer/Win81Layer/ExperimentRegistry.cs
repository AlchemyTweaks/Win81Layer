using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text.Json;
using Microsoft.Win32;

namespace Win81Layer;

public sealed class ExperimentCapability
{
	public string Id { get; set; } = "";

	public string Name { get; set; } = "";

	public string Status { get; set; } = "unavailable";

	public string Detail { get; set; } = "";

	public int MinimumBuild { get; set; }

	public bool UsesUndocumentedApi { get; set; }

	public bool CanMutateSystem { get; set; }
}

public sealed class PowerPolicySnapshot
{
	public string ActiveSchemeGuid { get; set; } = "";

	public uint? PerfBoostModeAc { get; set; }

	public uint? PerfBoostModeDc { get; set; }

	public uint? LatencyHintPerfAc { get; set; }

	public uint? LatencyHintPerfDc { get; set; }

	public uint? EnergyPerformancePreferenceAc { get; set; }

	public uint? EnergyPerformancePreferenceDc { get; set; }

	public string ProbeStatus { get; set; } = "unavailable";
}

public sealed class ExperimentAuditReport
{
	public int SchemaVersion { get; set; } = 1;

	public DateTime GeneratedUtc { get; set; }

	public string WindowsFamily { get; set; } = "Windows";

	public string DisplayVersion { get; set; } = "";

	public int Major { get; set; }

	public int Minor { get; set; }

	public int Build { get; set; }

	public int Revision { get; set; }

	public string Architecture { get; set; } = "";

	public string BuildFingerprint { get; set; } = "";

	public bool ExperimentalOptIn { get; set; }

	public string MutationPolicy { get; set; } = "disabled";

	public int AllowlistedMutationCount { get; set; }

	public PowerPolicySnapshot PowerPolicy { get; set; } = new PowerPolicySnapshot();

	public List<ExperimentCapability> Capabilities { get; set; } = new List<ExperimentCapability>();
}

internal sealed class ExperimentJournalRecord
{
	public int SchemaVersion { get; set; } = 1;

	public string TransactionId { get; set; } = "";

	public string ExperimentId { get; set; } = "";

	public string BuildFingerprint { get; set; } = "";

	public DateTime StartedUtc { get; set; }

	public DateTime? CompletedUtc { get; set; }

	public string Status { get; set; } = "pending";

	public string BeforeStateJson { get; set; } = "{}";

	public string RequestedStateJson { get; set; } = "{}";

	public string AfterStateJson { get; set; } = "{}";

	public string Error { get; set; } = "";
}

internal sealed class ExperimentChangeTransaction
{
	private readonly string _path;

	private readonly ExperimentJournalRecord _record;

	internal ExperimentChangeTransaction(string path, ExperimentJournalRecord record)
	{
		_path = path;
		_record = record;
	}

	public void MarkApplied(string afterStateJson)
	{
		_record.Status = "applied";
		_record.AfterStateJson = NormalizeJson(afterStateJson);
		_record.CompletedUtc = DateTime.UtcNow;
		ExperimentRegistry.PersistJournal(_path, _record);
	}

	public void MarkRolledBack(string afterStateJson)
	{
		_record.Status = "rolled-back";
		_record.AfterStateJson = NormalizeJson(afterStateJson);
		_record.CompletedUtc = DateTime.UtcNow;
		ExperimentRegistry.PersistJournal(_path, _record);
	}

	public void MarkFailed(Exception error)
	{
		_record.Status = "failed";
		_record.Error = error.ToString();
		_record.CompletedUtc = DateTime.UtcNow;
		ExperimentRegistry.PersistJournal(_path, _record);
	}

	private static string NormalizeJson(string value)
	{
		return string.IsNullOrWhiteSpace(value) ? "{}" : value;
	}
}

[SupportedOSPlatform("windows")]
public static class ExperimentRegistry
{
	// ViVe's feature-control API is available from Windows build 18963. Older Windows 10 builds remain supported
	// by the launcher core, but this capability must stay unavailable there.
	public const int FeatureStoreMinimumBuild = 18963;

	public static readonly string RootPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Win81Layer", "experiments");

	public static readonly string LatestAuditPath = Path.Combine(RootPath, "audit-latest.json");

	public static readonly string JournalPath = Path.Combine(RootPath, "journal");

	private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
	{
		WriteIndented = true
	};

	// Deliberately empty. Adding an ID here requires an implementation with capture/apply/verify/undo and a
	// build-specific QA record. User opt-in alone never makes an unknown Feature Store ID writable.
	private static readonly HashSet<string> AllowlistedMutations = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

	public static ExperimentAuditReport Audit(bool persist)
	{
		WindowsBuildInfo os = WindowsBuildInfo.Read();
		bool optedIn = false;
		try
		{
			optedIn = SettingsStore.Current.EnableExperimentalFeatures;
		}
		catch
		{
		}
		ExperimentAuditReport report = new ExperimentAuditReport
		{
			GeneratedUtc = DateTime.UtcNow,
			WindowsFamily = os.Build >= 22000 ? "Windows 11" : "Windows 10",
			DisplayVersion = os.DisplayVersion,
			Major = os.Major,
			Minor = os.Minor,
			Build = os.Build,
			Revision = os.Revision,
			Architecture = RuntimeInformation.OSArchitecture.ToString(),
			BuildFingerprint = os.Fingerprint,
			ExperimentalOptIn = optedIn,
			AllowlistedMutationCount = AllowlistedMutations.Count,
			MutationPolicy = !optedIn ? "disabled" : (AllowlistedMutations.Count == 0 ? "locked-no-allowlisted-experiments" : "allowlist-only"),
			PowerPolicy = PowerPolicyProbe.Read()
		};

		bool featureQuery = os.Build >= FeatureStoreMinimumBuild
			&& HasExport("ntdll.dll", "RtlQueryFeatureConfiguration")
			&& HasExport("ntdll.dll", "RtlQueryAllFeatureConfigurations");
		bool featureWrite = featureQuery && HasExport("ntdll.dll", "RtlSetFeatureConfigurations");
		report.Capabilities.Add(BuildCapability(
			"windows.feature-store.query",
			"Windows Feature Store query",
			os.Build,
			FeatureStoreMinimumBuild,
			featureQuery,
			usesUndocumentedApi: true,
			canMutate: false,
			featureQuery ? "Read-only API surface detected." : "Feature configuration exports were not detected."));
		report.Capabilities.Add(BuildCapability(
			"windows.feature-store.mutation",
			"Windows Feature Store override",
			os.Build,
			FeatureStoreMinimumBuild,
			featureWrite,
			usesUndocumentedApi: true,
			canMutate: true,
			featureWrite ? "API detected; writes remain locked behind the empty allowlist." : "Mutation export was not detected."));
		report.Capabilities.Add(BuildCapability(
			"windows.dwm.timing",
			"DWM composition timing",
			os.Build,
			10240,
			HasExport("dwmapi.dll", "DwmGetCompositionTimingInfo"),
			usesUndocumentedApi: false,
			canMutate: false,
			"Used by the bounded frame-timing diagnostic."));
		report.Capabilities.Add(BuildCapability(
			"windows.composition.attribute",
			"Window composition attribute",
			os.Build,
			10240,
			HasExport("user32.dll", "SetWindowCompositionAttribute"),
			usesUndocumentedApi: true,
			canMutate: true,
			"Build-sensitive visual effects; never assumed from the OS version alone."));
		report.Capabilities.Add(BuildCapability(
			"windows.process.power-throttling",
			"Process power throttling",
			os.Build,
			16299,
			HasExport("kernel32.dll", "SetProcessInformation"),
			usesUndocumentedApi: false,
			canMutate: true,
			"Documented process-level efficiency control."));
		report.Capabilities.Add(BuildCapability(
			"windows.dwm.system-backdrop",
			"DWM system backdrop",
			os.Build,
			22621,
			HasExport("dwmapi.dll", "DwmSetWindowAttribute"),
			usesUndocumentedApi: false,
			canMutate: true,
			"Windows 11 backdrop attributes; unavailable on Windows 10."));
		report.Capabilities.Add(new ExperimentCapability
		{
			Id = "browser.native-messaging",
			Name = "Browser native messaging",
			Status = "available",
			Detail = "Supported bridge for launcher and Chromium/Edge extension state.",
			MinimumBuild = 10240,
			UsesUndocumentedApi = false,
			CanMutateSystem = false
		});

		if (persist)
		{
			AtomicWrite(LatestAuditPath, JsonSerializer.Serialize(report, JsonOptions));
		}
		return report;
	}

	public static string WriteAuditReport()
	{
		Audit(persist: true);
		Logger.Log("Experiment audit written: " + LatestAuditPath);
		return LatestAuditPath;
	}

	public static void LogCompatibilitySummary()
	{
		try
		{
			WindowsBuildInfo os = WindowsBuildInfo.Read();
			bool featureStoreAvailable = os.Build >= FeatureStoreMinimumBuild
				&& HasExport("ntdll.dll", "RtlQueryFeatureConfiguration")
				&& HasExport("ntdll.dll", "RtlQueryAllFeatureConfigurations");
			Logger.Log($"Experiments: {os.Fingerprint}; FeatureStore={(featureStoreAvailable ? "available" : "unavailable")}; mutation allowlist={AllowlistedMutations.Count}");
		}
		catch (Exception ex)
		{
			Logger.Log("Experiment compatibility probe failed: " + ex.Message);
		}
	}

	internal static ExperimentChangeTransaction BeginChange(string experimentId, string beforeStateJson, string requestedStateJson)
	{
		if (!AllowlistedMutations.Contains(experimentId))
		{
			throw new InvalidOperationException("Experiment is not mutation-allowlisted: " + experimentId);
		}
		ExperimentAuditReport audit = Audit(persist: false);
		if (!audit.ExperimentalOptIn)
		{
			throw new InvalidOperationException("Experimental feature opt-in is disabled.");
		}
		ExperimentJournalRecord record = new ExperimentJournalRecord
		{
			TransactionId = Guid.NewGuid().ToString("N"),
			ExperimentId = experimentId,
			BuildFingerprint = audit.BuildFingerprint,
			StartedUtc = DateTime.UtcNow,
			BeforeStateJson = string.IsNullOrWhiteSpace(beforeStateJson) ? "{}" : beforeStateJson,
			RequestedStateJson = string.IsNullOrWhiteSpace(requestedStateJson) ? "{}" : requestedStateJson
		};
		string safeId = SanitizeFileName(experimentId);
		string path = Path.Combine(JournalPath, DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff") + "-" + safeId + "-" + record.TransactionId + ".json");
		PersistJournal(path, record);
		return new ExperimentChangeTransaction(path, record);
	}

	internal static void PersistJournal(string path, ExperimentJournalRecord record)
	{
		AtomicWrite(path, JsonSerializer.Serialize(record, JsonOptions));
	}

	private static ExperimentCapability BuildCapability(string id, string name, int currentBuild, int minimumBuild, bool available, bool usesUndocumentedApi, bool canMutate, string detail)
	{
		string status = currentBuild < minimumBuild ? "unsupported-build" : (available ? "available" : "unavailable");
		if (id == "windows.feature-store.mutation" && available && AllowlistedMutations.Count == 0)
		{
			status = "locked";
		}
		return new ExperimentCapability
		{
			Id = id,
			Name = name,
			Status = status,
			Detail = currentBuild < minimumBuild ? $"Requires Windows build {minimumBuild} or newer." : detail,
			MinimumBuild = minimumBuild,
			UsesUndocumentedApi = usesUndocumentedApi,
			CanMutateSystem = canMutate
		};
	}

	private static bool HasExport(string moduleName, string exportName)
	{
		nint module = LoadLibrary(moduleName);
		if (module == nint.Zero)
		{
			return false;
		}
		try
		{
			return GetProcAddress(module, exportName) != nint.Zero;
		}
		finally
		{
			FreeLibrary(module);
		}
	}

	private static void AtomicWrite(string path, string content)
	{
		string directory = Path.GetDirectoryName(path) ?? RootPath;
		Directory.CreateDirectory(directory);
		string temp = path + ".tmp-" + Environment.ProcessId + "-" + Guid.NewGuid().ToString("N");
		try
		{
			using (FileStream stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
			using (StreamWriter writer = new StreamWriter(stream))
			{
				writer.Write(content);
				writer.Flush();
				stream.Flush(flushToDisk: true);
			}
			File.Move(temp, path, overwrite: true);
		}
		finally
		{
			try
			{
				if (File.Exists(temp))
				{
					File.Delete(temp);
				}
			}
			catch
			{
			}
		}
	}

	private static string SanitizeFileName(string value)
	{
		foreach (char invalid in Path.GetInvalidFileNameChars())
		{
			value = value.Replace(invalid, '_');
		}
		return value;
	}

	[DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
	private static extern nint LoadLibrary(string fileName);

	[DllImport("kernel32.dll", CharSet = CharSet.Ansi, SetLastError = true)]
	private static extern nint GetProcAddress(nint module, string procName);

	[DllImport("kernel32.dll", SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static extern bool FreeLibrary(nint module);
}

internal sealed class WindowsBuildInfo
{
	public int Major { get; private set; }

	public int Minor { get; private set; }

	public int Build { get; private set; }

	public int Revision { get; private set; }

	public string DisplayVersion { get; private set; } = "";

	public string Fingerprint => $"{Major}.{Minor}.{Build}.{Revision}-{RuntimeInformation.OSArchitecture.ToString().ToLowerInvariant()}";

	public static WindowsBuildInfo Read()
	{
		RTL_OSVERSIONINFOEX version = new RTL_OSVERSIONINFOEX
		{
			dwOSVersionInfoSize = (uint)Marshal.SizeOf<RTL_OSVERSIONINFOEX>()
		};
		int status = RtlGetVersion(ref version);
		Version fallback = Environment.OSVersion.Version;
		WindowsBuildInfo result = new WindowsBuildInfo
		{
			Major = status == 0 ? (int)version.dwMajorVersion : fallback.Major,
			Minor = status == 0 ? (int)version.dwMinorVersion : fallback.Minor,
			Build = status == 0 ? (int)version.dwBuildNumber : fallback.Build,
			Revision = fallback.Revision >= 0 ? fallback.Revision : 0
		};
		try
		{
			using RegistryKey key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
			if (key != null)
			{
				object ubr = key.GetValue("UBR");
				if (ubr is int revision)
				{
					result.Revision = revision;
				}
				result.DisplayVersion = Convert.ToString(key.GetValue("DisplayVersion")) ?? "";
				if (string.IsNullOrWhiteSpace(result.DisplayVersion))
				{
					result.DisplayVersion = Convert.ToString(key.GetValue("ReleaseId")) ?? "";
				}
			}
		}
		catch
		{
		}
		return result;
	}

	[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
	private struct RTL_OSVERSIONINFOEX
	{
		public uint dwOSVersionInfoSize;
		public uint dwMajorVersion;
		public uint dwMinorVersion;
		public uint dwBuildNumber;
		public uint dwPlatformId;

		[MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
		public string szCSDVersion;

		public ushort wServicePackMajor;
		public ushort wServicePackMinor;
		public ushort wSuiteMask;
		public byte wProductType;
		public byte wReserved;
	}

	[DllImport("ntdll.dll", CharSet = CharSet.Unicode)]
	private static extern int RtlGetVersion(ref RTL_OSVERSIONINFOEX versionInfo);
}

internal static class PowerPolicyProbe
{
	private static readonly Guid SubProcessor = new Guid("54533251-82be-4824-96c1-47b60b740d00");

	private static readonly Guid PerfBoostMode = new Guid("be337238-0d82-4146-a960-4f3749d470c7");

	private static readonly Guid LatencyHintPerf = new Guid("619b7505-003b-4e82-b7a6-4dd29c300971");

	private static readonly Guid EnergyPerformancePreference = new Guid("36687f9e-e3a5-4dbf-b1dc-15eb381c6863");

	public static PowerPolicySnapshot Read()
	{
		PowerPolicySnapshot result = new PowerPolicySnapshot();
		nint schemePointer = nint.Zero;
		try
		{
			uint status = PowerGetActiveScheme(nint.Zero, out schemePointer);
			if (status != 0 || schemePointer == nint.Zero)
			{
				result.ProbeStatus = "PowerGetActiveScheme failed: " + status;
				return result;
			}
			Guid scheme = Marshal.PtrToStructure<Guid>(schemePointer);
			result.ActiveSchemeGuid = scheme.ToString("D");
			result.PerfBoostModeAc = ReadAc(scheme, PerfBoostMode);
			result.PerfBoostModeDc = ReadDc(scheme, PerfBoostMode);
			result.LatencyHintPerfAc = ReadAc(scheme, LatencyHintPerf);
			result.LatencyHintPerfDc = ReadDc(scheme, LatencyHintPerf);
			result.EnergyPerformancePreferenceAc = ReadAc(scheme, EnergyPerformancePreference);
			result.EnergyPerformancePreferenceDc = ReadDc(scheme, EnergyPerformancePreference);
			result.ProbeStatus = "ok";
			return result;
		}
		catch (Exception ex)
		{
			result.ProbeStatus = ex.GetType().Name + ": " + ex.Message;
			return result;
		}
		finally
		{
			if (schemePointer != nint.Zero)
			{
				LocalFree(schemePointer);
			}
		}
	}

	private static uint? ReadAc(Guid scheme, Guid setting)
	{
		Guid subgroup = SubProcessor;
		return PowerReadACValueIndex(nint.Zero, ref scheme, ref subgroup, ref setting, out uint value) == 0 ? value : null;
	}

	private static uint? ReadDc(Guid scheme, Guid setting)
	{
		Guid subgroup = SubProcessor;
		return PowerReadDCValueIndex(nint.Zero, ref scheme, ref subgroup, ref setting, out uint value) == 0 ? value : null;
	}

	[DllImport("powrprof.dll")]
	private static extern uint PowerGetActiveScheme(nint userRootPowerKey, out nint activePolicyGuid);

	[DllImport("powrprof.dll")]
	private static extern uint PowerReadACValueIndex(nint rootPowerKey, ref Guid schemeGuid, ref Guid subgroupGuid, ref Guid settingGuid, out uint valueIndex);

	[DllImport("powrprof.dll")]
	private static extern uint PowerReadDCValueIndex(nint rootPowerKey, ref Guid schemeGuid, ref Guid subgroupGuid, ref Guid settingGuid, out uint valueIndex);

	[DllImport("kernel32.dll")]
	private static extern nint LocalFree(nint memory);
}
