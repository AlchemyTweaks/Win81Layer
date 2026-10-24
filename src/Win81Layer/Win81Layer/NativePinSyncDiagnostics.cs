#nullable enable

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;

namespace Win81Layer;

// --pinsynctest <outDir> [--start2-golden <copy of start2.bin>] [--live-pins <launcher pins json>] [--live-since <time>]
// [--expect-first-run-add <name>]
//
// Offscreen QA of the native pin sync. Read-only by construction: App sets ReadOnlyDiagnostics before this runs (every
// settings/state write throws), the native stores are only read, and every change is replayed against fixtures: a
// VOLATILE registry key of its own (deleted at the end), fixture shortcuts and fixture start2.bin files in outDir, an
// in-memory launcher (taskbar, Windows 7 pinned list, Metro tiles) and an in-memory snapshot. The real service, watchers,
// readers and engine run unchanged against those fixtures. The live dry run reads the real native taskbar store and the
// real launcher pin file (read only) with the cutoff the shell itself would use, unless --live-pins / --live-since name
// others. Results: pinsynctest-results.txt / -report.json / -transcript.txt in outDir. Exits by itself (hard deadline
// 240 s).
internal static class NativePinSyncDiagnostics
{
	private const int HardDeadlineMs = 240000;

	// Written into the fixture folder by this harness; a fixture folder without it is never deleted.
	private const string FixtureMarker = ".pinsynctest-fixture";

	private static readonly List<(string Name, bool Pass, string Detail)> Checks = new List<(string, bool, string)>();

	private static readonly List<string> Transcript = new List<string>();

	private static readonly Dictionary<string, object> Metrics = new Dictionary<string, object>();

	public static void Begin(Application app, string[] args)
	{
		app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
		string outDir = ArgAfter(args, "--pinsynctest") ?? Path.Combine(Path.GetTempPath(), "win81layer-pinsynctest");
		Thread deadline = new Thread(delegate ()
		{
			Thread.Sleep(HardDeadlineMs);
			try { File.AppendAllText(Path.Combine(outDir, "pinsynctest-hardexit.txt"), DateTime.Now.ToString("O") + " hard deadline\r\n"); } catch { }
			try
			{
				if (_fixtureKeyPath != null)
				{
					Registry.CurrentUser.DeleteSubKeyTree(_fixtureKeyPath, throwOnMissingSubKey: false);
				}
			}
			catch
			{
			}
			Environment.Exit(3);
		})
		{
			IsBackground = true,
			Name = "PinSyncTest.Deadline"
		};
		deadline.Start();
		// The scenario runs on its own STA thread and only waits there, so the UI dispatcher keeps pumping, as it does in
		// the running shell (shell COM objects may call back into the apartment that first created them).
		app.Dispatcher.BeginInvoke((Action)delegate
		{
			Thread runner = new Thread(delegate ()
			{
				int code = 1;
				try
				{
					code = Run(outDir, args, app.Dispatcher) ? 0 : 1;
				}
				catch (Exception ex)
				{
					try
					{
						Directory.CreateDirectory(outDir);
						File.WriteAllText(Path.Combine(outDir, "pinsynctest-error.txt"), ex.ToString());
					}
					catch
					{
					}
				}
				app.Dispatcher.BeginInvoke((Action)(() => app.Shutdown(code)));
			})
			{
				IsBackground = true,
				Name = "PinSyncTest"
			};
			runner.SetApartmentState(ApartmentState.STA);
			runner.Start();
		}, DispatcherPriority.Loaded);
	}

	private static string? ArgAfter(string[] args, string name)
	{
		int i = Array.IndexOf(args, name);
		return i >= 0 && i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal) ? args[i + 1] : null;
	}

	private static void Check(string name, bool pass, string detail = "")
	{
		Checks.Add((name, pass, detail));
		Trace((pass ? "PASS " : "FAIL ") + name + (detail.Length > 0 ? "  (" + detail + ")" : ""));
	}

	private static volatile string? _fixtureKeyPath;

	private static readonly ManualResetEvent NapEvent = new ManualResetEvent(false);

	// A wait that keeps pumping COM on this STA thread (Thread.Sleep would not).
	private static void Nap(int ms)
	{
		NapEvent.WaitOne(ms);
	}

	private static string? _livePath;

	private static readonly long StartTick = Environment.TickCount64;

	// The logger is silent in read-only diagnostics, so the transcript is also appended live (a stall shows where).
	private static void Trace(string line)
	{
		string stamped = $"{(Environment.TickCount64 - StartTick) / 1000.0,7:0.000}s {line}";
		lock (Transcript)
		{
			Transcript.Add(stamped);
			if (_livePath != null)
			{
				try { File.AppendAllText(_livePath, stamped + Environment.NewLine); } catch { }
			}
		}
	}

	private static bool Run(string outDir, string[] args, Dispatcher ui)
	{
		Directory.CreateDirectory(outDir);
		_livePath = Path.Combine(outDir, "pinsynctest-live.txt");
		try { File.Delete(_livePath); File.Delete(Path.Combine(outDir, "pinsynctest-hardexit.txt")); } catch { }
		Baseline before = Baseline.Capture();
		string fixture = PrepareFixture(outDir);
		string keyPath = "Software\\Win81LayerPinSyncTest-" + Guid.NewGuid().ToString("N");
		_fixtureKeyPath = keyPath;
		try
		{
			SafetyChecks(fixture);
			UnitChecks(fixture);
			EngineChecks(fixture);
			ShellChecks(fixture);
			StartCodecChecks(fixture, ArgAfter(args, "--start2-golden"));
			ServiceScenario(fixture, keyPath);
			LiveTargetChecks(ui);
			LiveReadOnlyDryRun(args);
		}
		catch (Exception ex)
		{
			Check("harness-completed", false, ex.ToString());
		}
		finally
		{
			try { Registry.CurrentUser.DeleteSubKeyTree(keyPath, throwOnMissingSubKey: false); } catch { }
		}
		bool keyGone;
		using (RegistryKey? k = Registry.CurrentUser.OpenSubKey(keyPath))
		{
			keyGone = k == null;
		}
		Check("safety-fixture-key-deleted", keyGone, keyPath);
		Baseline after = Baseline.Capture();
		Check("safety-native-taskband-unchanged", before.Taskband == after.Taskband, before.Taskband == after.Taskband ? before.Taskband : $"before={before.Taskband} after={after.Taskband}");
		Check("safety-native-taskbar-folder-unchanged", before.LinkFolder == after.LinkFolder, before.LinkFolder == after.LinkFolder ? "" : $"before={before.LinkFolder} after={after.LinkFolder}");
		Check("safety-native-start2-unchanged", before.Start2 == after.Start2, before.Start2);
		Check("safety-launcher-state-unchanged", before.LauncherState == after.LauncherState, before.LauncherState == after.LauncherState ? "" : $"before={before.LauncherState} after={after.LauncherState}");

		bool passed = Checks.All(c => c.Pass);
		StringBuilder txt = new StringBuilder();
		foreach ((string name, bool pass, string detail) in Checks)
		{
			txt.AppendLine(pass ? "PASS " + name + (detail.Length > 0 && detail.Length < 200 ? "  (" + detail + ")" : "") : "FAIL " + name + ": " + detail);
		}
		txt.AppendLine($"{Checks.Count(c => c.Pass)}/{Checks.Count} passed");
		File.WriteAllText(Path.Combine(outDir, "pinsynctest-results.txt"), txt.ToString());
		File.WriteAllLines(Path.Combine(outDir, "pinsynctest-transcript.txt"), Transcript);
		var report = new
		{
			SchemaVersion = 1,
			GeneratedUtc = DateTime.UtcNow,
			Passed = passed,
			Checks = Checks.Select(c => new { c.Name, c.Pass, c.Detail }).ToArray(),
			Metrics
		};
		File.WriteAllText(Path.Combine(outDir, "pinsynctest-report.json"), JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
		Logger.Log($"PINSYNC-QA: passed={passed} checks={Checks.Count} failed={Checks.Count(c => !c.Pass)} -> {outDir}");
		return passed;
	}

	// The fixture folder under outDir. One this harness made before (it carries the marker) is cleared; a folder of the
	// same name without the marker is left alone and a new, uniquely named one is used.
	private static string PrepareFixture(string outDir)
	{
		string fixture = Path.Combine(outDir, "fixture");
		if (Directory.Exists(fixture))
		{
			if (File.Exists(Path.Combine(fixture, FixtureMarker)))
			{
				try
				{
					Directory.Delete(fixture, recursive: true);
				}
				catch
				{
				}
			}
			if (Directory.Exists(fixture))
			{
				fixture = Path.Combine(outDir, "fixture-" + Guid.NewGuid().ToString("N"));
			}
		}
		Directory.CreateDirectory(fixture);
		File.WriteAllText(Path.Combine(fixture, FixtureMarker), "created by --pinsynctest " + DateTime.UtcNow.ToString("O"));
		Trace("fixture: " + fixture);
		return fixture;
	}

	// ===================================================================== safety

	// The write guard is checked without pointing a save at any live store: the pin store and the snapshot store both
	// commit through DurableStateStore, so a commit of a throwaway state whose primary file is in the fixture must throw.
	private static void SafetyChecks(string fixture)
	{
		Check("safety-readonly-diagnostics", SettingsStore.ReadOnlyDiagnostics);
		string stateName = "pinsynctest-guard-" + Guid.NewGuid().ToString("N");
		string primary = Path.Combine(fixture, "guard-state.json");
		bool refused = false;
		try
		{
			DurableStateStore.Commit(stateName, primary, "{}", _ => true);
		}
		catch (InvalidOperationException)
		{
			refused = true;
		}
		List<string> leftovers = new List<string> { primary, DurableStateStore.PendingPath(stateName), DurableStateStore.LastGoodPath(stateName), DurableStateStore.PreviousPath(stateName) }
			.Where(File.Exists).ToList();
		foreach (string p in leftovers)
		{
			try { File.Delete(p); } catch { }
		}
		Check("safety-state-writes-refused", refused && leftovers.Count == 0, refused ? (leftovers.Count == 0 ? "DurableStateStore.Commit throws in read-only diagnostics" : "files written: " + string.Join(", ", leftovers)) : "the commit was not refused");
		Check("safety-live-snapshot-save-refused", !new DurablePinSnapshotStore().Save(new NativePinSnapshot()), "DurablePinSnapshotStore.Save returns false");
	}

	private sealed class Baseline
	{
		public string Taskband = "";

		public string LinkFolder = "";

		public string Start2 = "";

		public string LauncherState = "";

		public static Baseline Capture()
		{
			Baseline b = new Baseline();
			try
			{
				using RegistryKey? k = Registry.CurrentUser.OpenSubKey(NativeTaskbarFavorites.KeyPath, writable: false);
				byte[] fav = k?.GetValue("Favorites") as byte[] ?? Array.Empty<byte>();
				b.Taskband = $"{fav.Length}b:{Convert.ToHexString(SHA256.HashData(fav))[..16]}:changes={k?.GetValue("FavoritesChanges")}";
			}
			catch (Exception ex)
			{
				b.Taskband = "err " + ex.Message;
			}
			try
			{
				string dir = NativePinSync.DefaultLinkFolder;
				b.LinkFolder = Directory.Exists(dir)
					? string.Join(";", Directory.EnumerateFileSystemEntries(dir).OrderBy(p => p, StringComparer.OrdinalIgnoreCase).Select(p => Path.GetFileName(p) + "@" + File.GetLastWriteTimeUtc(p).Ticks))
					: "missing";
			}
			catch (Exception ex)
			{
				b.LinkFolder = "err " + ex.Message;
			}
			try
			{
				string s2 = StartLayoutReader.DefaultPath;
				b.Start2 = File.Exists(s2) ? $"{new FileInfo(s2).Length}b@{File.GetLastWriteTimeUtc(s2).Ticks}" : "missing";
			}
			catch (Exception ex)
			{
				b.Start2 = "err " + ex.Message;
			}
			string root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Win81Layer");
			List<string> files = new List<string>
			{
				TaskbarPins.PinsPath,
				Path.Combine(root, "profile.json"),
				SettingsStore.SettingsPath,
				DurablePinSnapshotStore.PrimaryPath
			};
			try
			{
				if (Directory.Exists(DurableStateStore.RecoveryRoot))
				{
					files.AddRange(Directory.EnumerateFiles(DurableStateStore.RecoveryRoot).Where(p => Path.GetFileName(p).StartsWith("taskbar-pins", StringComparison.OrdinalIgnoreCase)
						|| Path.GetFileName(p).StartsWith("native-pin-snapshot", StringComparison.OrdinalIgnoreCase)
						|| Path.GetFileName(p).StartsWith("profile", StringComparison.OrdinalIgnoreCase)
						|| Path.GetFileName(p).StartsWith("settings", StringComparison.OrdinalIgnoreCase)));
				}
			}
			catch
			{
			}
			b.LauncherState = string.Join(";", files.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(p => p, StringComparer.OrdinalIgnoreCase).Select(p =>
			{
				try
				{
					return File.Exists(p) ? Path.GetFileName(p) + "=" + Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(p)))[..12] : Path.GetFileName(p) + "=none";
				}
				catch
				{
					return Path.GetFileName(p) + "=locked";
				}
			}));
			return b;
		}
	}

	// ===================================================================== fixtures

	private static readonly Guid UserPinnedFolder = new Guid("1f3427c8-5c10-4210-aa03-2ee45287d668");

	private static readonly Guid AppsFolder = new Guid("4234d49b-0245-4df3-b780-3893943456e1");

	private static readonly Guid FmtidAppUserModel = new Guid("9f4c2855-9f79-4b39-a8d0-e1d42de1d5f3");

	private sealed class FavEntry
	{
		public string? LnkName;

		public string? PackagedAumid;

		public string? Aumid;

		public string Origin = "UserPinned";

		public DateTime PinnedUtc;
	}

	private static byte[] BuildFavorites(IEnumerable<FavEntry> entries)
	{
		MemoryStream ms = new MemoryStream();
		foreach (FavEntry e in entries)
		{
			byte[] idl = e.PackagedAumid != null ? PackagedIdList(e) : LinkIdList(e);
			ms.WriteByte(0x00);
			ms.Write(BitConverter.GetBytes(idl.Length));
			ms.Write(idl);
		}
		ms.WriteByte(0xFF);
		return ms.ToArray();
	}

	private static byte[] Item(byte[] body)
	{
		byte[] item = new byte[body.Length + 2];
		BitConverter.GetBytes((ushort)item.Length).CopyTo(item, 0);
		body.CopyTo(item, 2);
		return item;
	}

	private static byte[] RootItem(Guid folder)
	{
		byte[] body = new byte[18];
		body[0] = 0x1F;
		body[1] = 0x50;
		folder.ToByteArray().CopyTo(body, 2);
		return Item(body);
	}

	private static byte[] ExtBlock(uint sig, ushort version, byte[] payload)
	{
		byte[] b = new byte[8 + payload.Length];
		BitConverter.GetBytes((ushort)b.Length).CopyTo(b, 0);
		BitConverter.GetBytes(version).CopyTo(b, 2);
		BitConverter.GetBytes(sig).CopyTo(b, 4);
		payload.CopyTo(b, 8);
		return b;
	}

	private static byte[] Utf16Z(string s) => Encoding.Unicode.GetBytes(s + "\0");

	private static byte[] TaggedString(string s)
	{
		byte[] text = Utf16Z(s);
		byte[] payload = new byte[2 + text.Length];
		text.CopyTo(payload, 2);
		return payload;
	}

	private static List<byte[]> TailBlocks(FavEntry e)
	{
		List<byte[]> blocks = new List<byte[]>();
		if (e.Aumid != null)
		{
			blocks.Add(ExtBlock(0xBEEF001Du, 1, TaggedString(e.Aumid)));
		}
		blocks.Add(ExtBlock(0xBEEF001Eu, 1, TaggedString(e.Origin)));
		blocks.Add(ExtBlock(0xBEEF002Bu, 1, BitConverter.GetBytes(e.PinnedUtc.ToFileTimeUtc())));
		return blocks;
	}

	private static byte[] WithBlocks(byte[] head, List<byte[]> blocks)
	{
		MemoryStream ms = new MemoryStream();
		ms.Write(head);
		int first = (int)ms.Length + 2;   // offsets are within the item, which starts with its own 2-byte size
		foreach (byte[] b in blocks)
		{
			ms.Write(b);
		}
		ms.Write(BitConverter.GetBytes((ushort)first));
		return Item(ms.ToArray());
	}

	private static byte[] LinkIdList(FavEntry e)
	{
		MemoryStream ms = new MemoryStream();
		ms.Write(RootItem(UserPinnedFolder));
		byte[] folderBody = new byte[12 + 8];
		folderBody[0] = 0x31;
		folderBody[10] = 0x10;
		Encoding.ASCII.GetBytes("TASKBAR").CopyTo(folderBody, 12);
		ms.Write(Item(folderBody));
		// File entry: type 0x32, size, DOS date, attributes, 8.3 name, then BEEF0004 (long name) + the pin blocks.
		byte[] head = new byte[12 + 14];
		head[0] = 0x32;
		Encoding.ASCII.GetBytes("PINNED~1.LNK").CopyTo(head, 12);
		byte[] longName = new byte[46 - 8];
		byte[] name = Utf16Z(e.LnkName!);
		byte[] b4payload = new byte[longName.Length + name.Length + 2];
		name.CopyTo(b4payload, longName.Length);
		List<byte[]> blocks = new List<byte[]> { ExtBlock(0xBEEF0004u, 9, b4payload) };
		blocks.AddRange(TailBlocks(e));
		ms.Write(WithBlocks(head, blocks));
		ms.Write(new byte[2]);
		return ms.ToArray();
	}

	private static byte[] PackagedIdList(FavEntry e)
	{
		MemoryStream ms = new MemoryStream();
		ms.Write(RootItem(AppsFolder));
		MemoryStream props = new MemoryStream();
		byte[] text = Utf16Z(e.PackagedAumid!);
		int vsize = 17 + text.Length;
		vsize += (4 - vsize % 4) % 4;
		byte[] value = new byte[vsize];
		BitConverter.GetBytes(vsize).CopyTo(value, 0);
		BitConverter.GetBytes(5).CopyTo(value, 4);
		BitConverter.GetBytes((ushort)0x1F).CopyTo(value, 9);
		BitConverter.GetBytes(text.Length / 2).CopyTo(value, 13);
		text.CopyTo(value, 17);
		int ssize = 24 + value.Length + 4;
		props.Write(BitConverter.GetBytes(ssize));
		props.Write(Encoding.ASCII.GetBytes("1SPS"));
		props.Write(FmtidAppUserModel.ToByteArray());
		props.Write(value);
		props.Write(new byte[4]);
		props.Write(new byte[4]);
		byte[] head = new byte[10];
		Encoding.ASCII.GetBytes("APPS").CopyTo(head, 4);
		byte[] full = head.Concat(props.ToArray()).ToArray();
		ms.Write(WithBlocks(full, TailBlocks(e)));
		ms.Write(new byte[2]);
		return ms.ToArray();
	}

	private static void WriteLink(string path, string target, string? args = null, string? workingDir = null)
	{
		Type shellType = Type.GetTypeFromProgID("WScript.Shell") ?? throw new InvalidOperationException("WScript.Shell unavailable");
		dynamic shell = Activator.CreateInstance(shellType)!;
		dynamic? sc = null;
		try
		{
			sc = shell.CreateShortcut(path);
			sc.TargetPath = target;
			if (args != null)
			{
				sc.Arguments = args;
			}
			if (workingDir != null)
			{
				sc.WorkingDirectory = workingDir;
			}
			sc.Save();
		}
		finally
		{
			if (sc != null) { try { Marshal.FinalReleaseComObject(sc); } catch { } }
			try { Marshal.FinalReleaseComObject(shell); } catch { }
		}
	}

	private static byte[] EncodeStart2(string json, ulong ft)
	{
		(byte[] key, byte[] iv) = StartLayoutReader.KeyIv(ft);
		byte[] ct;
		using (Aes aes = Aes.Create())
		{
			aes.Key = key;
			ct = aes.EncryptCbc(Encoding.UTF8.GetBytes(json), iv, PaddingMode.PKCS7);
		}
		int pre = StartLayoutReader.PadPrefix(ft);
		StartLayoutReader.Mt19937 junk = new StartLayoutReader.Mt19937(5489);
		MemoryStream ms = new MemoryStream();
		ms.Write(StartLayoutReader.Magic);
		ms.Write(StartLayoutReader.FormatVersion);
		ms.Write(BitConverter.GetBytes(ft));
		ms.Write(BitConverter.GetBytes((uint)(ct.Length + StartLayoutReader.PadTotal)));
		for (int i = 0; i < pre; i++) ms.WriteByte((byte)junk.Next());
		ms.Write(ct);
		for (int i = 0; i < StartLayoutReader.PadTotal - pre; i++) ms.WriteByte((byte)junk.Next());
		return ms.ToArray();
	}

	private static string StartJson(params string[] ids)
	{
		return JsonSerializer.Serialize(new { pinnedList = ids.Select(id => new { id, pinnedBy = 8 }).ToArray(), layoutPolicyApplied = false });
	}

	private static List<string> SystemExes()
	{
		string sys = Environment.SystemDirectory;
		string[] names = { "cmd.exe", "charmap.exe", "control.exe", "osk.exe", "mmc.exe", "msinfo32.exe", "dxdiag.exe", "write.exe", "notepad.exe", "magnify.exe", "cleanmgr.exe", "taskmgr.exe", "mspaint.exe", "wordpad.exe", "calc.exe", "eventvwr.exe", "perfmon.exe", "resmon.exe", "SndVol.exe", "where.exe" };
		return names.Select(n => Path.Combine(sys, n)).Where(File.Exists).ToList();
	}

	// ===================================================================== unit checks

	private static void UnitChecks(string fixture)
	{
		DateTime t = new DateTime(2026, 10, 22, 8, 15, 0, DateTimeKind.Utc);
		byte[] blob = BuildFavorites(new[]
		{
			new FavEntry { LnkName = "Chess Lab.lnk", Aumid = "Microsoft.AutoGenerated.{C4E55000-0000-4000-8000-000000000000}", PinnedUtc = t },
			new FavEntry { PackagedAumid = "Contoso.Calc_8wekyb3d8bbwe!App", Origin = "UserPinned", PinnedUtc = t.AddDays(-3) },
			new FavEntry { LnkName = "File Explorer.lnk", Aumid = "Microsoft.Windows.Explorer", Origin = "SystemPinned", PinnedUtc = t.AddDays(-30) }
		});
		List<NativeTaskbarPin>? parsed = NativeTaskbarFavorites.Parse(blob);
		bool ok = parsed != null && parsed.Count == 3
			&& parsed[0].Kind == "lnk" && parsed[0].LnkName == "Chess Lab.lnk" && parsed[0].Origin == "UserPinned" && parsed[0].PinnedUtc == t && parsed[0].Aumid!.StartsWith("Microsoft.AutoGenerated.")
			&& parsed[1].Kind == "packaged" && parsed[1].Aumid == "Contoso.Calc_8wekyb3d8bbwe!App" && parsed[1].PinnedUtc == t.AddDays(-3)
			&& parsed[2].Kind == "lnk" && parsed[2].Origin == "SystemPinned" && parsed[2].Aumid == "Microsoft.Windows.Explorer";
		Check("unit-favorites-parse", ok, parsed == null ? "null" : string.Join(" | ", parsed));
		Check("unit-favorites-truncated-is-unknown", NativeTaskbarFavorites.Parse(blob.Take(blob.Length - 9).ToArray()) == null && NativeTaskbarFavorites.Parse(blob.Take(blob.Length - 1).ToArray()) == null);
		Check("unit-favorites-empty-list", NativeTaskbarFavorites.Parse(new byte[] { 0xFF })?.Count == 0);

		string sys = Environment.SystemDirectory;
		string charmap = Path.Combine(sys, "charmap.exe");
		SortedSet<string> kf = NativePinSync.KeysFor("{1AC14E77-02E7-4E5D-B744-2EB1AE5198B7}\\charmap.exe", null, null);
		Check("unit-keys-knownfolder-aumid", kf.Contains("path:" + NativePinSync.NormPath(charmap) + "|"), string.Join(",", kf));
		string viber = Path.Combine(sys, "osk.exe");
		bool viberMatch = NativePinSync.LauncherKeys(new PinnedApp { Name = "V", LaunchPath = "shell:AppsFolder\\" + viber })
			.Overlaps(NativePinSync.KeysFor(viber, viber, null));
		Check("unit-keys-appsfolder-path-form", viberMatch, "launcher shell:AppsFolder\\<exe path> = native exe pin");
		bool explorerMatch = NativePinSync.LauncherKeys(new PinnedApp { Name = "File Explorer", LaunchPath = "shell:AppsFolder\\Microsoft.Windows.Explorer", Aumid = "Microsoft.Windows.Explorer" })
			.Overlaps(NativePinSync.KeysFor(null, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe"), null));
		Check("unit-keys-file-explorer", explorerMatch);
		bool argsDiffer = !NativePinSync.KeysFor(null, charmap, "-a").Overlaps(NativePinSync.KeysFor(null, charmap, "-b"));
		Check("unit-keys-args-distinguish", argsDiffer, "same exe, different args = different pins");
		Check("unit-keys-args-whitespace", NativePinSync.KeysFor(null, charmap, "  -A   x ").SetEquals(NativePinSync.KeysFor(null, charmap.ToUpperInvariant(), "-a x")));

		string chessDir = Path.Combine(fixture, "unit-wd");
		Directory.CreateDirectory(chessDir);
		NativePinRecord rec = new NativePinRecord { Name = "Chess Lab", Source = "lnk", Target = Path.Combine(sys, "cmd.exe"), Args = "/c run.cmd", WorkingDir = chessDir, Aumid = "Microsoft.AutoGenerated.{X}", Keys = new List<string> { "k" } };
		PinnedApp? built = PinSyncEngine.BuildPin(rec, out string? why);
		Check("unit-build-lnk-pin", built != null && built.WorkingDir == chessDir && built.Aumid == null && built.LaunchPath == rec.Target && built.Args == "/c run.cmd", why ?? JsonSerializer.Serialize(built));
		PinnedApp? pk = PinSyncEngine.BuildPin(new NativePinRecord { Name = "Calc", Source = "packaged", Aumid = "Contoso.Calc_8wekyb3d8bbwe!App", Keys = new List<string> { "k" } }, out _);
		Check("unit-build-packaged-pin", pk != null && pk.LaunchPath == "shell:AppsFolder\\Contoso.Calc_8wekyb3d8bbwe!App" && pk.Aumid == "Contoso.Calc_8wekyb3d8bbwe!App");
		PinnedApp? missing = PinSyncEngine.BuildPin(new NativePinRecord { Name = "Gone", Target = "Z:\\nope\\gone.exe", Keys = new List<string> { "k" } }, out string? whyMissing);
		Check("unit-build-missing-target-skipped", missing == null && whyMissing != null && whyMissing.Contains("not found"), whyMissing ?? "");

		string json = JsonSerializer.Serialize(new List<PinnedApp> { new PinnedApp { Name = "A", LaunchPath = "C:\\a.exe" } });
		string jsonWd = JsonSerializer.Serialize(new List<PinnedApp> { new PinnedApp { Name = "A", LaunchPath = "C:\\a.exe", WorkingDir = "C:\\w" } });
		PinnedApp? back = JsonSerializer.Deserialize<List<PinnedApp>>(jsonWd)?.FirstOrDefault();
		Check("unit-pin-json-shape", !json.Contains("WorkingDir") && jsonWd.Contains("WorkingDir") && back?.WorkingDir == "C:\\w", "existing pin files keep their exact shape");
		Check("unit-launch-working-dir", AppLauncher.ChooseWorkingDirectory(Path.Combine(sys, "cmd.exe"), chessDir) == chessDir
			&& AppLauncher.ChooseWorkingDirectory(Path.Combine(sys, "cmd.exe"), Path.Combine(fixture, "nope")) == sys
			&& AppLauncher.ChooseWorkingDirectory(Path.Combine(sys, "cmd.exe"), null) == sys
			&& AppLauncher.ChooseWorkingDirectory("shell:AppsFolder\\X!App", null) == null);
		Check("unit-link-working-dir", TaskbarPins.LinkWorkingDir(chessDir, Path.Combine(sys, "cmd.exe")) == chessDir
			&& TaskbarPins.LinkWorkingDir(sys, Path.Combine(sys, "cmd.exe")) == null
			&& TaskbarPins.LinkWorkingDir(Path.Combine(fixture, "nope"), Path.Combine(sys, "cmd.exe")) == null
			&& TaskbarPins.LinkWorkingDir("", Path.Combine(sys, "cmd.exe")) == null);

		Check("unit-snapshot-validate", NativePinSnapshot.Validate(new NativePinSnapshot().Serialize())
			&& !NativePinSnapshot.Validate("{\"Version\":1,\"Taskbar\":[{\"Keys\":[],\"Name\":\"x\"}],\"Start\":[]}")
			&& !NativePinSnapshot.Validate("not json"));
		Check("unit-start-ids", NativePinSync.TryAppsFolderId("P~A_b!App", out string a1) && a1 == "A_b!App"
			&& NativePinSync.TryAppsFolderId("W~Microsoft.Windows.Explorer", out string a2) && a2 == "Microsoft.Windows.Explorer"
			&& !NativePinSync.TryAppsFolderId("T~placeholder", out _) && !NativePinSync.TryAppsFolderId("", out _));
		Check("unit-same-launch-target", NativePinSync.SameLaunchTarget("shell:AppsFolder\\{1AC14E77-02E7-4E5D-B744-2EB1AE5198B7}\\charmap.exe", charmap)
			&& NativePinSync.SameLaunchTarget("shell:AppsFolder\\" + charmap, charmap)
			&& !NativePinSync.SameLaunchTarget("shell:AppsFolder\\A!App", "shell:AppsFolder\\B!App"));
	}

	// ===================================================================== engine checks

	private static AppEntry Entry(string name, string launchPath, string? appId = null)
	{
		return new AppEntry { Name = name, LaunchPath = launchPath, TileBrush = Brushes.Transparent, AppId = appId };
	}

	private static void EngineChecks(string fixture)
	{
		string sys = Environment.SystemDirectory;
		string cmd = Path.Combine(sys, "cmd.exe");
		string charmap = Path.Combine(sys, "charmap.exe");
		string osk = Path.Combine(sys, "osk.exe");
		string explorer = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");
		List<string> log = new List<string>();

		// Which skips are retried: an unreadable shortcut and a missing program may come back; a document target or a
		// readable shortcut with no file target will not.
		PinSyncEngine.BuildPin(new NativePinRecord { Name = "L", LinkName = "L.lnk", Incomplete = true, Keys = { "aumid:x" } }, out string? w1, out bool t1);
		PinSyncEngine.BuildPin(new NativePinRecord { Name = "M", Target = "Z:\\offline\\m.exe", Keys = { "k" } }, out string? w2, out bool t2);
		PinSyncEngine.BuildPin(new NativePinRecord { Name = "D", Target = Path.Combine(sys, "drivers", "etc", "hosts"), Keys = { "k" } }, out string? w3, out bool t3);
		PinSyncEngine.BuildPin(new NativePinRecord { Name = "I", LinkName = "I.lnk", Keys = { "lnk:i.lnk" } }, out string? w4, out bool t4);
		Check("engine-build-transient-reasons", t1 && t2 && !t3 && !t4, $"unreadable={t1} ({w1}); offline={t2} ({w2}); document={t3} ({w3}); id-list={t4} ({w4})");

		// An unreadable shortcut is matched by its shortcut name: in a first-run baseline it does not come back as a new
		// pin once it reads fully, and a full record read as unreadable later keeps the full record in the baseline.
		NativePinRecord inc = new NativePinRecord { Name = "Chess", LinkName = "Chess.lnk", Incomplete = true, Keys = { "lnk:chess.lnk" } };
		NativePinRecord full = new NativePinRecord { Name = "Chess", LinkName = "Chess.lnk", Keys = { "aumid:microsoft.autogenerated.{c}", "path:" + NativePinSync.NormPath(cmd) + "|/c chess" } };
		NativePinRecord other = new NativePinRecord { Name = "Other", LinkName = "Other.lnk", Keys = { "path:" + NativePinSync.NormPath(osk) + "|" } };
		List<NativePinRecord> b1 = PinSyncEngine.NextTaskbarBaseline(null, new[] { inc }, null);
		TaskbarPinDiff d1 = PinSyncEngine.DiffTaskbar(b1, new[] { full }, null);
		List<NativePinRecord> b2 = PinSyncEngine.NextTaskbarBaseline(new[] { full }, new[] { inc }, null);
		TaskbarPinDiff d2 = PinSyncEngine.DiffTaskbar(new[] { full }, new[] { inc }, null);
		List<NativePinRecord> b3 = PinSyncEngine.NextTaskbarBaseline(new[] { full }, new[] { full, other }, new[] { other });
		TaskbarPinDiff d3 = PinSyncEngine.DiffTaskbar(b3, new[] { full, other }, null);
		Check("engine-unreadable-shortcut-matching", b1.Count == 1 && b1[0].Incomplete && !d1.HasWork && b2.Count == 1 && ReferenceEquals(b2[0], full) && !d2.HasWork,
			$"b1={b1.Count} d1.work={d1.HasWork} b2.full={(b2.Count == 1 && ReferenceEquals(b2[0], full))} d2.work={d2.HasWork}");
		Check("engine-retried-pin-stays-out-of-baseline", b3.Count == 1 && ReferenceEquals(b3[0], full) && d3.Added.Count == 1 && ReferenceEquals(d3.Added[0], other));
		NativePinSnapshot snap = new NativePinSnapshot { Taskbar = { inc, full } };
		string snapJson = snap.Serialize();
		NativePinSnapshot? snapBack = NativePinSnapshot.Deserialize(snapJson);
		Check("engine-unreadable-flag-roundtrip", snapBack != null && snapBack.Taskbar[0].Incomplete && !snapBack.Taskbar[1].Incomplete && !snapJson.Contains("\"Incomplete\": false"));

		// A native pin updated in place (same shortcut, new program and app id): the launcher pin keeps its position,
		// group and name.
		List<PinnedApp> lu = new List<PinnedApp>
		{
			new PinnedApp { Name = "First", LaunchPath = cmd, ExePath = cmd },
			new PinnedApp { Name = "Tool", LaunchPath = charmap, ExePath = charmap, GroupName = "Work", IconPath = "C:\\icons\\tool.ico" },
			new PinnedApp { Name = "Last", LaunchPath = explorer, Aumid = "Microsoft.Windows.Explorer" }
		};
		NativePinRecord oldTool = new NativePinRecord { Name = "Tool", LinkName = "Tool.lnk", Keys = NativePinSync.KeysFor("Microsoft.AutoGenerated.{0LD}", charmap, null).ToList() };
		NativePinRecord newTool = new NativePinRecord { Name = "Tool", LinkName = "Tool.lnk", Target = osk, Keys = NativePinSync.KeysFor("Microsoft.AutoGenerated.{NEW}", osk, null).ToList() };
		TaskbarPinDiff du = new TaskbarPinDiff();
		du.Removed.Add(oldTool);
		du.Added.Add(newTool);
		du.NowKeys.UnionWith(newTool.Keys);
		log.Clear();
		int cu = PinSyncEngine.PlanTaskbar(lu, du, log);
		Check("engine-update-in-place", cu == 1 && lu.Count == 3 && lu[1].Name == "Tool" && string.Equals(lu[1].LaunchPath, osk, StringComparison.OrdinalIgnoreCase) && lu[1].GroupName == "Work" && lu[1].IconPath == "C:\\icons\\tool.ico" && log.Any(l => l.Contains("updated in place")),
			string.Join(" | ", log) + " / " + JsonSerializer.Serialize(lu[1]));

		// An unpin of one native entry while another native entry of the same app remains: the launcher pin stays.
		List<PinnedApp> lk = new List<PinnedApp> { new PinnedApp { Name = "Fx", LaunchPath = charmap, ExePath = charmap } };
		TaskbarPinDiff dk = new TaskbarPinDiff();
		dk.Removed.Add(new NativePinRecord { Name = "Fx one", LinkName = "Fx one.lnk", Keys = NativePinSync.KeysFor(null, charmap, null).ToList() });
		dk.NowKeys.UnionWith(NativePinSync.KeysFor("Microsoft.AutoGenerated.{FX2}", charmap, null));
		log.Clear();
		int ck = PinSyncEngine.PlanTaskbar(lk, dk, log);
		Check("engine-unpin-kept-while-still-pinned-natively", ck == 0 && lk.Count == 1 && log.Any(l => l.Contains("kept")), string.Join(" | ", log));

		// A native unpin of an app that is not on the launcher taskbar is reported (for the toast), nothing changes.
		TaskbarPinDiff dn = new TaskbarPinDiff { Live = true };
		dn.Removed.Add(new NativePinRecord { Name = "Calc", Keys = { "aumid:contoso.calc_x!app" } });
		log.Clear();
		Check("engine-unpin-not-on-taskbar-reported", PinSyncEngine.PlanTaskbar(lk, dn, log) == 0 && dn.NotOnTaskbar.SequenceEqual(new[] { "Calc" }) && lk.Count == 1);

		// A native pin that went away because its app was uninstalled is not the user's unpin: no toast for it.
		TaskbarPinDiff dg = new TaskbarPinDiff { Live = true };
		dg.Removed.Add(new NativePinRecord { Name = "Uninstalled Tool", LinkName = "Uninstalled Tool.lnk", Target = Path.Combine(fixture, "uninstalled", "tool.exe"), Keys = { "path:uninstalled|" } });
		dg.Removed.Add(new NativePinRecord { Name = "Gone Store App", Source = "packaged", Aumid = "Contoso.Gone_8wekyb3d8bbwe!App", Keys = { "aumid:contoso.gone_8wekyb3d8bbwe!app" } });
		dg.Removed.Add(new NativePinRecord { Name = "Store App", Source = "packaged", Aumid = "Contoso.Here_8wekyb3d8bbwe!App", Keys = { "aumid:contoso.here_8wekyb3d8bbwe!app" } });
		dg.Removed.Add(new NativePinRecord { Name = "Desk Tool", LinkName = "Desk Tool.lnk", Target = cmd, Keys = { "path:desk|" } });
		PinSyncEngine.PrepareTaskbar(dg, aumid => aumid == "Contoso.Here_8wekyb3d8bbwe!App");
		log.Clear();
		PinSyncEngine.PlanTaskbar(new List<PinnedApp>(), dg, log);
		Check("engine-uninstall-is-not-an-unpin", dg.ProgramGone.Count == 2 && dg.NotOnTaskbar.SequenceEqual(new[] { "Store App", "Desk Tool" }) && log.Count(l => l.Contains("its program is gone")) == 2,
			$"[{string.Join(", ", dg.NotOnTaskbar)}] {string.Join(" | ", log)}");

		// Windows takes a provisioned pin away and puts it back (two passes): it returns where it was, not lost.
		List<PinnedApp> lr = new List<PinnedApp>
		{
			new PinnedApp { Name = "File Explorer", LaunchPath = "shell:AppsFolder\\Microsoft.Windows.Explorer", Aumid = "Microsoft.Windows.Explorer", GroupName = "Shell" },
			new PinnedApp { Name = "A", LaunchPath = cmd, ExePath = cmd },
			new PinnedApp { Name = "B", LaunchPath = charmap, ExePath = charmap }
		};
		List<string> explorerKeys = NativePinSync.KeysFor("Microsoft.Windows.Explorer", explorer, null).ToList();
		TaskbarPinDiff dr1 = new TaskbarPinDiff();
		dr1.Removed.Add(new NativePinRecord { Name = "File Explorer", LinkName = "File Explorer.lnk", Origin = "SystemPinned", Keys = explorerKeys.ToList() });
		log.Clear();
		PinSyncEngine.PlanTaskbar(lr, dr1, log);
		TaskbarPinDiff dr2 = new TaskbarPinDiff { RecentlyRemoved = dr1.RemovedPins.ToList() };
		dr2.Added.Add(new NativePinRecord { Name = "File Explorer", LinkName = "File Explorer.lnk", Origin = "SystemPinned", Keys = explorerKeys.ToList(), Target = explorer });
		dr2.NowKeys.UnionWith(explorerKeys);
		int cr = PinSyncEngine.PlanTaskbar(lr, dr2, log);
		Check("engine-reprovisioned-pin-restored", dr1.RemovedPins.Count == 1 && dr1.RemovedPins[0].Index == 0 && cr == 1 && lr.Count == 3 && lr[0].Name == "File Explorer" && lr[0].GroupName == "Shell" && dr2.Restored.Count == 1,
			string.Join(" | ", log));
		TaskbarPinDiff dr3 = new TaskbarPinDiff();
		dr3.Added.Add(new NativePinRecord { Name = "Edge", LinkName = "Edge.lnk", Origin = "SystemPinned", Keys = { "aumid:msedge" }, Target = cmd });
		log.Clear();
		Check("engine-new-provisioned-pin-skipped", PinSyncEngine.PlanTaskbar(lr, dr3, log) == 0 && log.Any(l => l.Contains("not a user pin")));

		// First run: a recent native pin made before the catch-up threshold that is not on the launcher is logged.
		DateTime since = new DateTime(2026, 10, 21, 15, 49, 52, DateTimeKind.Utc);
		NativePinRecord after = new NativePinRecord { Name = "After", LinkName = "After.lnk", Origin = "UserPinned", PinnedUtc = since.AddHours(16), Target = cmd, Keys = { "aumid:after" } };
		NativePinRecord nearMiss = new NativePinRecord { Name = "Near", LinkName = "Near.lnk", Origin = "UserPinned", PinnedUtc = since.AddHours(-2), Target = cmd, Keys = { "aumid:near" } };
		NativePinRecord old = new NativePinRecord { Name = "Old", LinkName = "Old.lnk", Origin = "UserPinned", PinnedUtc = since.AddDays(-40), Target = cmd, Keys = { "aumid:old" } };
		TaskbarPinDiff dm = PinSyncEngine.DiffTaskbar(null, new[] { old, nearMiss, after }, since);
		log.Clear();
		PinSyncEngine.PlanTaskbar(new List<PinnedApp>(), dm, log);
		Check("engine-first-run-near-miss-logged", dm.Added.SequenceEqual(new[] { after }) && dm.NearMisses.SequenceEqual(new[] { nearMiss }) && log.Any(l => l.Contains("first run: 'Near' was pinned natively")) && !log.Any(l => l.Contains("'Old' was pinned")),
			string.Join(" | ", log));

		// "Pin to Start" for the Windows 7 menu: the app-list entry for an exe, an app id, a path id or a shortcut.
		string chromeExe = Path.Combine(fixture, "Chrome", "Application", "chrome.exe");
		string discUpdate = Path.Combine(fixture, "Disc", "Update.exe");
		string discExe = Path.Combine(fixture, "Disc", "app-1.0.9", "Disc.exe");
		List<AppEntry> apps = new List<AppEntry>
		{
			Entry("Chrome Like", "shell:AppsFolder\\ChromeLike", "ChromeLike"),
			Entry("Disc", "shell:AppsFolder\\com.squirrel.Disc.Disc", "com.squirrel.Disc.Disc"),
			Entry("Character Map", "shell:AppsFolder\\{1AC14E77-02E7-4E5D-B744-2EB1AE5198B7}\\charmap.exe"),
			Entry("Mail", "shell:AppsFolder\\Contoso.Mail_8wekyb3d8bbwe!App", "Contoso.Mail_8wekyb3d8bbwe!App")
		};
		Func<string, string?> startMenu = name => name == "Chrome Like" ? chromeExe : name == "Disc" ? discUpdate : null;
		string?[] noIds = { null };
		AppEntry? e1 = NativePinSync.FindStartEntry(apps, chromeExe, noIds, chromeExe, startMenu);
		AppEntry? e2 = NativePinSync.FindStartEntry(apps, discExe, noIds, discExe, startMenu);
		AppEntry? e3 = NativePinSync.FindStartEntry(apps, Path.Combine(fixture, "x.exe"), new string?[] { "ChromeLike" }, Path.Combine(fixture, "x.exe"), startMenu);
		AppEntry? e4 = NativePinSync.FindStartEntry(apps, charmap, noIds, charmap, startMenu);
		AppEntry? e5 = NativePinSync.FindStartEntry(apps, "shell:AppsFolder\\Contoso.Mail_8wekyb3d8bbwe!App", Array.Empty<string?>(), null, startMenu);
		AppEntry? e6 = NativePinSync.FindStartEntry(apps, Path.Combine(fixture, "portable.exe"), noIds, Path.Combine(fixture, "portable.exe"), startMenu);
		AppEntry? e7 = NativePinSync.FindStartEntry(apps, Path.Combine(fixture, "Profile 2.lnk"), noIds, null, startMenu);
		Check("engine-start-entry-for-exe-and-ids", e1?.Name == "Chrome Like" && e2?.Name == "Disc" && e3?.Name == "Chrome Like" && e4?.Name == "Character Map" && e5?.Name == "Mail" && e6 == null && e7 == null,
			$"chrome={e1?.Name} squirrel={e2?.Name} id={e3?.Name} pathId={e4?.Name} exact={e5?.Name} portable={e6?.Name} lnkWithArgs={e7?.Name}");
	}

	// ===================================================================== shell checks (read only)

	[DllImport("shell32.dll", CharSet = CharSet.Unicode)]
	private static extern int SHParseDisplayName(string pszName, IntPtr pbc, out IntPtr ppidl, uint sfgaoIn, out uint psfgaoOut);

	[ComImport]
	[Guid("00021401-0000-0000-C000-000000000046")]
	private class CShellLink
	{
	}

	// Only the first methods of IShellLinkW, in vtable order (nothing after SetIDList is called).
	[ComImport]
	[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
	[Guid("000214F9-0000-0000-C000-000000000046")]
	private interface IShellLinkW
	{
		void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszFile, int cch, IntPtr pfd, uint fFlags);

		void GetIDList(out IntPtr ppidl);

		void SetIDList(IntPtr pidl);
	}

	// An ID-list shortcut to an AppsFolder item (what dragging a Store app to the desktop makes), in the fixture.
	private static bool WriteAppsFolderLink(string path, string aumid)
	{
		IntPtr pidl = IntPtr.Zero;
		object? link = null;
		try
		{
			if (SHParseDisplayName("shell:AppsFolder\\" + aumid, IntPtr.Zero, out pidl, 0, out _) != 0 || pidl == IntPtr.Zero)
			{
				return false;
			}
			link = new CShellLink();
			((IShellLinkW)link).SetIDList(pidl);
			((IPersistFile)link).Save(path, true);
			return File.Exists(path);
		}
		catch (Exception ex)
		{
			Trace("WriteAppsFolderLink failed: " + ex.Message);
			return false;
		}
		finally
		{
			if (pidl != IntPtr.Zero)
			{
				Marshal.FreeCoTaskMem(pidl);
			}
			if (link != null)
			{
				try { Marshal.FinalReleaseComObject(link); } catch { }
			}
		}
	}

	// The real AppsFolder is only read: a packaged app's display name by AUMID, and an ID-list shortcut to it (written in
	// the fixture) resolved to that app for "Pin to taskbar".
	private static void ShellChecks(string fixture)
	{
		string? aumid = null, name = null;
		List<(AppEntry Entry, AppInventory.IShellItem Item)>? items = null;
		try
		{
			items = AppInventory.EnumerateAppsFolder();
			(AppEntry Entry, AppInventory.IShellItem Item) hit = items.FirstOrDefault(x => x.Entry.LaunchPath.IndexOf('!') > 0);
			if (hit.Entry != null)
			{
				aumid = hit.Entry.LaunchPath.Substring(NativePinSync.AppsFolderPrefix.Length);
				name = hit.Entry.Name;
			}
		}
		catch (Exception ex)
		{
			Trace("AppsFolder enumeration failed: " + ex.Message);
		}
		finally
		{
			AppInventory.ReleaseAppsFolderItems(items);
		}
		Metrics["ShellCheckApp"] = aumid ?? "none";
		if (aumid == null)
		{
			Check("shell-packaged-app-available", false, "no packaged app in AppsFolder");
			return;
		}
		string? shown = ShellDropResolver.AppsFolderDisplayName(aumid);
		string? bogus = ShellDropResolver.AppsFolderDisplayName("Contoso.DoesNotExist_0000000000000!App");
		Check("shell-packaged-display-name", shown == name && bogus == null, $"{aumid}: '{shown}' (app list '{name}'); unknown id -> {bogus ?? "null"}");
		string appLink = Path.Combine(fixture, "Store App.lnk");
		bool made = WriteAppsFolderLink(appLink, aumid);
		PinnedApp? viaApp = made ? ShellDropResolver.ResolveShortcutApp(appLink) : null;
		string folderLink = Path.Combine(fixture, "Folder.lnk");
		WriteLink(folderLink, fixture);
		PinnedApp? viaFolder = ShellDropResolver.ResolveShortcutApp(folderLink);
		Check("shell-store-app-shortcut-pins-as-app", made && viaApp != null && string.Equals(viaApp.Aumid, aumid, StringComparison.OrdinalIgnoreCase) && viaApp.LaunchPath == NativePinSync.AppsFolderPrefix + viaApp.Aumid && TaskbarPins.FromDroppedPath(appLink) == null,
			$"made={made}; resolved={viaApp?.LaunchPath ?? "null"}");
		Check("shell-folder-shortcut-not-pinnable", viaFolder == null && TaskbarPins.FromDroppedPath(folderLink) == null);
	}

	// ===================================================================== Start codec

	private static void StartCodecChecks(string fixture, string? goldenPath)
	{
		ulong ft = (ulong)new DateTime(2026, 10, 23, 8, 11, 46, DateTimeKind.Utc).ToFileTimeUtc();
		byte[] fx = EncodeStart2(StartJson("W~Microsoft.Windows.Explorer", "P~Contoso.Mail_8wekyb3d8bbwe!App", "T~placeholder"), ft);
		StartLayoutRead r = StartLayoutReader.Decode(fx);
		Check("start-codec-roundtrip", r.Ok && r.FileTime == (long)ft && r.Ids.SequenceEqual(new[] { "W~Microsoft.Windows.Explorer", "P~Contoso.Mail_8wekyb3d8bbwe!App", "T~placeholder" }), r.Error ?? string.Join(",", r.Ids));
		string nested = "{\"pinnedList\":[{\"id\":\"P~A!App\"},{\"name\":\"Folder\",\"items\":[{\"id\":\"W~B\"},{\"id\":\"P~C!App\"}]},{\"id\":\"W~D\"}]}";
		StartLayoutRead rn = StartLayoutReader.Decode(EncodeStart2(nested, ft + 12345));
		Check("start-codec-folders-flattened", rn.Ok && rn.Ids.SequenceEqual(new[] { "P~A!App", "W~B", "P~C!App", "W~D" }), rn.Error ?? string.Join(",", rn.Ids));
		byte[] badVersion = (byte[])fx.Clone();
		badVersion[20] ^= 0x5A;
		// Last byte of the second-to-last ciphertext block: flips the final plaintext byte, the PKCS7 padding length.
		byte[] badCipher = (byte[])fx.Clone();
		int cipherLen = fx.Length - StartLayoutReader.HeaderSize - StartLayoutReader.PadTotal;
		badCipher[StartLayoutReader.HeaderSize + StartLayoutReader.PadPrefix(ft) + cipherLen - 17] ^= 0x77;
		byte[] truncated = fx.Take(fx.Length - 600).ToArray();
		byte[] notJson = EncodeStart2("[1,2,3]", ft);
		StartLayoutRead v = StartLayoutReader.Decode(badVersion), c = StartLayoutReader.Decode(badCipher), tr = StartLayoutReader.Decode(truncated), nj = StartLayoutReader.Decode(notJson);
		Check("start-codec-fails-closed", !v.Ok && !c.Ok && !tr.Ok && !nj.Ok && !StartLayoutReader.Decode(new byte[10]).Ok,
			$"version={v.Error}; cipher={c.Error}; truncated={tr.Error}; notjson={nj.Error}");
		string missingPath = Path.Combine(fixture, "no-such-start2.bin");
		Check("start-codec-missing-is-unknown", !StartLayoutReader.TryRead(missingPath).Ok);
		if (goldenPath != null && !StartLayoutReader.Available)
		{
			Check("start-golden-decode", true, "skipped: the Start layout reader is not included in this build");
		}
		else if (goldenPath != null)
		{
			StartLayoutRead g = StartLayoutReader.TryRead(goldenPath, 1);
			Metrics["GoldenStartIds"] = g.Ids;
			Check("start-golden-decode", g.Ok && g.Ids.Count == 17 && g.Ids.Contains("W~Microsoft.Windows.Explorer") && g.Ids.Contains("P~Microsoft.WindowsCalculator_8wekyb3d8bbwe!App"),
				g.Ok ? $"{g.Ids.Count} ids, written {DateTime.FromFileTimeUtc(g.FileTime):u}" : g.Error ?? "");
		}
	}

	// ===================================================================== in-memory launcher + snapshot

	private sealed class MemoryTarget : IPinSyncTarget
	{
		public readonly object Gate = new object();

		public List<PinnedApp> Taskbar = new List<PinnedApp>();

		public bool Win7Style = true;

		public List<string> Win7Pins = new List<string>();

		public List<string> MetroTiles = new List<string>();

		public List<(string LaunchPath, string Name)> Inventory = new List<(string, string)>();

		public DateTime? FirstRunCatchUpSinceUtc { get; set; }

		public int DeferTaskbarTimes;

		public bool StartReady = true;

		public int TaskbarApplies;

		// What the last taskbar apply would have shown a toast for (the live target shows it when Live is set).
		public List<string> LastNotOnTaskbar = new List<string>();

		public bool LastLive;

		// Every apply came with its pins already built on the worker (no file checks on the "UI" side).
		public bool AllPrepared = true;

		public PinSyncOutcome ApplyTaskbar(TaskbarPinDiff diff, List<string> log, out string? deferReason)
		{
			lock (Gate)
			{
				deferReason = null;
				if (DeferTaskbarTimes > 0)
				{
					DeferTaskbarTimes--;
					deferReason = "a taskbar pin is being dragged (simulated)";
					return PinSyncOutcome.Deferred;
				}
				AllPrepared &= diff.Added.All(diff.Built.ContainsKey);
				List<PinnedApp> copy = Taskbar.Select(Clone).ToList();
				PinSyncEngine.PlanTaskbar(copy, diff, log);
				Taskbar = copy;
				TaskbarApplies++;
				LastNotOnTaskbar = diff.NotOnTaskbar.ToList();
				LastLive = diff.Live;
				return PinSyncOutcome.Applied;
			}
		}

		public void SetGroup(string name, string group)
		{
			lock (Gate)
			{
				PinnedApp? p = Taskbar.FirstOrDefault(x => x.Name == name);
				if (p != null)
				{
					p.GroupName = group;
				}
			}
		}

		public PinSyncOutcome ApplyStart(StartPinDiff diff, List<string> log, out string? deferReason)
		{
			lock (Gate)
			{
				deferReason = null;
				if (!StartReady)
				{
					deferReason = "Start not loaded yet (simulated)";
					return PinSyncOutcome.Deferred;
				}
				PinSyncEngine.PlanStart(new MemoryStartModel(this), diff, log);
				return PinSyncOutcome.Applied;
			}
		}

		public List<PinnedApp> TaskbarCopy()
		{
			lock (Gate)
			{
				return Taskbar.Select(Clone).ToList();
			}
		}

		private static PinnedApp Clone(PinnedApp p) => new PinnedApp { Name = p.Name, LaunchPath = p.LaunchPath, Args = p.Args, ExePath = p.ExePath, IconPath = p.IconPath, Aumid = p.Aumid, GroupName = p.GroupName, WorkingDir = p.WorkingDir };
	}

	private sealed class MemoryStartModel : IStartPinModel
	{
		private readonly MemoryTarget _t;

		public MemoryStartModel(MemoryTarget t)
		{
			_t = t;
		}

		public bool Win7Style => _t.Win7Style;

		public (string LaunchPath, string Name)? Resolve(string appsFolderId)
		{
			string lp = NativePinSync.AppsFolderPrefix + appsFolderId;
			(string LaunchPath, string Name) hit = _t.Inventory.FirstOrDefault(x => string.Equals(x.LaunchPath, lp, StringComparison.OrdinalIgnoreCase));
			return hit.LaunchPath == null ? null : hit;
		}

		public bool Win7Has(string launchPath) => _t.Win7Pins.Any(p => NativePinSync.SameLaunchTarget(p, launchPath));

		public void Win7Add(string launchPath) => _t.Win7Pins.Add(launchPath);

		public int Win7Remove(string launchPath) => _t.Win7Pins.RemoveAll(p => NativePinSync.SameLaunchTarget(p, launchPath));

		public bool MetroHas(string launchPath) => _t.MetroTiles.Any(p => NativePinSync.SameLaunchTarget(p, launchPath));

		public bool MetroAdd(string launchPath)
		{
			_t.MetroTiles.Add(launchPath);
			return true;
		}

		public int MetroRemove(string launchPath) => _t.MetroTiles.RemoveAll(p => NativePinSync.SameLaunchTarget(p, launchPath));
	}

	private sealed class MemorySnapshotStore : IPinSnapshotStore
	{
		public string? Json;

		public bool Unreadable;

		public int Saves;

		public NativePinSnapshot? Load(out bool ok)
		{
			if (Unreadable)
			{
				ok = false;
				return null;
			}
			ok = true;
			return Json == null ? null : NativePinSnapshot.Deserialize(Json);
		}

		public bool Save(NativePinSnapshot snapshot)
		{
			string json = snapshot.Serialize();
			if (!NativePinSnapshot.Validate(json))
			{
				return false;
			}
			Json = json;
			Saves++;
			return true;
		}

		public NativePinSnapshot? Peek() => Json == null ? null : NativePinSnapshot.Deserialize(Json);
	}

	// ===================================================================== service scenario

	private sealed class Harness
	{
		public NativePinSyncService? Service;

		public readonly ConcurrentQueue<PinSyncPassReport> Reports = new ConcurrentQueue<PinSyncPassReport>();

		public int Count;

		public NativePinSyncService StartService(NativePinSource source, MemoryTarget target, MemorySnapshotStore store, int retryMs = 300, int maxDeferrals = 20)
		{
			NativePinSyncService s = new NativePinSyncService(source, target, store, taskbandDebounceMs: 400, startDebounceMs: 600, retryMs: retryMs, maxDeferrals: maxDeferrals,
				pendingDelaysMs: new[] { 500, 500, 500, 500, 500, 500 }, watchRecoveryDelaysMs: new[] { 200, 300, 500, 800, 1000 });
			s.PassCompleted += r =>
			{
				Reports.Enqueue(r);
				Trace($"    [pass {Count + 1}: {r.Reasons}; taskbarRead={r.TaskbarRead} startRead={r.StartRead} deferred={r.Deferred} saved={r.SnapshotSaved} paused={r.Paused}{(r.Error != null ? " ERROR " + r.Error : "")}]");
				foreach (string line in r.Lines)
				{
					Trace("        " + line);
				}
				Interlocked.Increment(ref Count);
			};
			Service = s;
			s.Start();
			return s;
		}

		// Waits until at least `target` passes completed, then a quiet period with no further pass.
		public bool WaitFor(int target, int timeoutMs = 8000, int quietMs = 700)
		{
			long end = Environment.TickCount64 + timeoutMs;
			while (Volatile.Read(ref Count) < target && Environment.TickCount64 < end)
			{
				Nap(20);
			}
			if (Volatile.Read(ref Count) < target)
			{
				return false;
			}
			int seen = Volatile.Read(ref Count);
			long quietEnd = Environment.TickCount64 + quietMs;
			while (Environment.TickCount64 < quietEnd)
			{
				Nap(20);
				if (Volatile.Read(ref Count) != seen)
				{
					seen = Volatile.Read(ref Count);
					quietEnd = Environment.TickCount64 + quietMs;
				}
			}
			return true;
		}

		public List<string> LinesSince(int pass)
		{
			return Reports.Skip(pass).SelectMany(r => r.Lines).ToList();
		}

		public List<PinSyncPassReport> ReportsSince(int pass)
		{
			return Reports.Skip(pass).ToList();
		}

		// Waits until a pass satisfies the condition (or the timeout).
		public bool WaitUntil(Func<bool> condition, int timeoutMs)
		{
			long end = Environment.TickCount64 + timeoutMs;
			while (!condition() && Environment.TickCount64 < end)
			{
				Nap(20);
			}
			return condition();
		}
	}

	private static void ServiceScenario(string fixture, string keyPath)
	{
		List<string> exes = SystemExes();
		Metrics["FixtureExes"] = exes;
		if (exes.Count < 14)
		{
			Check("scenario-fixture-exes", false, "need 14 System32 programs, found " + exes.Count);
			return;
		}
		string linkDir = Path.Combine(fixture, "TaskBar");
		string startDir = Path.Combine(fixture, "LocalState");
		string start2 = Path.Combine(startDir, "start2.bin");
		Directory.CreateDirectory(linkDir);
		Directory.CreateDirectory(startDir);
		string chessDir = Path.Combine(fixture, "ChessLab");
		Directory.CreateDirectory(chessDir);
		// Squirrel-style app: Update.exe --processStart Discordish.exe with the real exe under app-1.2.3.
		string squirrel = Path.Combine(fixture, "Squirrel");
		Directory.CreateDirectory(Path.Combine(squirrel, "app-1.2.3"));
		string update = Path.Combine(squirrel, "Update.exe");
		string discordish = Path.Combine(squirrel, "app-1.2.3", "Discordish.exe");
		File.Copy(exes[0], update, overwrite: true);
		File.Copy(exes[0], discordish, overwrite: true);
		string squirrel2 = Path.Combine(fixture, "Squirrel2");
		Directory.CreateDirectory(Path.Combine(squirrel2, "app-2.0.0"));
		File.Copy(exes[0], Path.Combine(squirrel2, "Update.exe"), overwrite: true);
		File.Copy(exes[0], Path.Combine(squirrel2, "app-2.0.0", "Squirrelish.exe"), overwrite: true);
		string gone = Path.Combine(fixture, "Gone", "gone.exe");
		Directory.CreateDirectory(Path.GetDirectoryName(gone)!);
		File.Copy(exes[0], gone, overwrite: true);

		string exeCmd = exes[0], exeOld = exes[1], exeViber = exes[2], exeFirefox = exes[3], exeNew = exes[4], exeLauncherOnly = exes[5], exeBurst = exes[6], exeDown = exes[7];
		string exeKf = exes[8];   // pinned natively under a "{FOLDERID_System}\name.exe" AUMID, in the launcher as a plain path
		string exeLocked = exes[9], exeUpdated = exes[10], exeBudget = exes[11], exeRecovered = exes[12], exeLater = exes[13];
		bool haveKf = true;
		string explorer = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");

		WriteLink(Path.Combine(linkDir, "File Explorer.lnk"), explorer);
		WriteLink(Path.Combine(linkDir, "Old Tool.lnk"), exeOld);
		WriteLink(Path.Combine(linkDir, "Viber Like.lnk"), exeViber);
		if (haveKf)
		{
			WriteLink(Path.Combine(linkDir, "KF Tool.lnk"), exeKf);
		}
		WriteLink(Path.Combine(linkDir, "Discordish.lnk"), update, "--processStart Discordish.exe");
		WriteLink(Path.Combine(linkDir, "Chess Lab.lnk"), exeCmd, "/c chess_server.cmd --auto-exit 900", chessDir);
		WriteLink(Path.Combine(linkDir, "Late System.lnk"), exeLauncherOnly, "--provisioned");
		WriteLink(Path.Combine(linkDir, "Late Dup.lnk"), exeFirefox);
		WriteLink(Path.Combine(linkDir, "Orphan.lnk"), exeNew, "--never-pinned");   // in the folder, not in Favorites
		WriteLink(Path.Combine(linkDir, "Gone Tool.lnk"), gone);
		File.Delete(gone);   // its target vanished (offline drive / uninstalled)

		DateTime storeSaved = DateTime.UtcNow.AddDays(-2);
		List<FavEntry> native = new List<FavEntry>
		{
			new FavEntry { LnkName = "File Explorer.lnk", Aumid = "Microsoft.Windows.Explorer", Origin = "SystemPinned", PinnedUtc = storeSaved.AddDays(-30) },
			new FavEntry { LnkName = "Old Tool.lnk", Aumid = "Microsoft.AutoGenerated.{11111111-2222-3333-4444-555555555555}", PinnedUtc = storeSaved.AddDays(-10) },
			new FavEntry { LnkName = "Viber Like.lnk", Aumid = exeViber, PinnedUtc = storeSaved.AddDays(-9) },
			new FavEntry { LnkName = "Discordish.lnk", Aumid = "com.squirrel.Discordish.Discordish", PinnedUtc = storeSaved.AddDays(-7) },
			new FavEntry { PackagedAumid = "Contoso.Calculator_8wekyb3d8bbwe!App", PinnedUtc = storeSaved.AddDays(-6) },
			new FavEntry { LnkName = "Chess Lab.lnk", Aumid = "Microsoft.AutoGenerated.{C4E55000-0000-4000-8000-000000000001}", PinnedUtc = storeSaved.AddHours(15) },
			new FavEntry { LnkName = "Late System.lnk", Aumid = "Contoso.Provisioned", Origin = "SystemPinned", PinnedUtc = storeSaved.AddHours(16) },
			new FavEntry { LnkName = "Late Dup.lnk", Aumid = exeFirefox, PinnedUtc = storeSaved.AddHours(17) },
			new FavEntry { LnkName = "Gone Tool.lnk", Aumid = gone, PinnedUtc = storeSaved.AddDays(-20) }
		};
		if (haveKf)
		{
			native.Insert(3, new FavEntry { LnkName = "KF Tool.lnk", Aumid = "{1AC14E77-02E7-4E5D-B744-2EB1AE5198B7}\\" + Path.GetFileName(exeKf), PinnedUtc = storeSaved.AddDays(-8) });
		}

		MemoryTarget target = new MemoryTarget { FirstRunCatchUpSinceUtc = storeSaved };
		target.Taskbar.AddRange(new[]
		{
			new PinnedApp { Name = "File Explorer", LaunchPath = "shell:AppsFolder\\Microsoft.Windows.Explorer", Aumid = "Microsoft.Windows.Explorer" },
			new PinnedApp { Name = "Viber Like", LaunchPath = "shell:AppsFolder\\" + exeViber },
			new PinnedApp { Name = "Discordish", LaunchPath = update, Args = "--processStart Discordish.exe", ExePath = discordish },
			new PinnedApp { Name = "Firefox Like", LaunchPath = exeFirefox, ExePath = exeFirefox },
			new PinnedApp { Name = "Launcher Only", LaunchPath = exeLauncherOnly, ExePath = exeLauncherOnly },
			new PinnedApp { Name = "Mail", LaunchPath = "shell:AppsFolder\\Contoso.Mail_8wekyb3d8bbwe!App", Aumid = "Contoso.Mail_8wekyb3d8bbwe!App" },
			new PinnedApp { Name = "Squirrelish", LaunchPath = Path.Combine(squirrel2, "Update.exe"), Args = "--processStart Squirrelish.exe", ExePath = Path.Combine(squirrel2, "app-2.0.0", "Squirrelish.exe") }
		});
		if (haveKf)
		{
			target.Taskbar.Insert(2, new PinnedApp { Name = "KF Tool", LaunchPath = exeKf, ExePath = exeKf });
		}
		int launcherStart = target.Taskbar.Count;
		target.Inventory.AddRange(new[]
		{
			("shell:AppsFolder\\Microsoft.Windows.Explorer", "File Explorer"),
			("shell:AppsFolder\\Contoso.Mail_8wekyb3d8bbwe!App", "Mail"),
			("shell:AppsFolder\\Contoso.Paint_8wekyb3d8bbwe!App", "Paint"),
			("shell:AppsFolder\\{6D809377-6AF0-444B-8957-A3773F02200E}\\Contoso\\Tool.exe", "Contoso Tool"),
			("shell:AppsFolder\\Contoso.Notes_8wekyb3d8bbwe!App", "Contoso Notes"),
			("shell:AppsFolder\\Contoso.Photos_8wekyb3d8bbwe!App", "Contoso Photos")
		});
		target.Win7Pins.Add("shell:AppsFolder\\Microsoft.Windows.Explorer");
		target.MetroTiles.Add("shell:AppsFolder\\Microsoft.Windows.Explorer");
		target.MetroTiles.Add("shell:AppsFolder\\Contoso.Photos_8wekyb3d8bbwe!App");

		Dictionary<string, string> packagedNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
		{
			["Contoso.Calculator_8wekyb3d8bbwe!App"] = "Calculator",
			["Contoso.Notes_8wekyb3d8bbwe!App"] = "Contoso Notes",
			["Contoso.Mail_8wekyb3d8bbwe!App"] = "Mail"
		};
		NativePinSource source = new NativePinSource
		{
			TaskbandKeyPath = keyPath,
			LinkFolder = linkDir,
			Start2Path = start2,
			PackagedName = aumid => packagedNames.TryGetValue(aumid, out string? n) ? n : null,
			AppInstalled = aumid => packagedNames.ContainsKey(aumid)
		};
		ulong ft = (ulong)DateTime.UtcNow.AddMinutes(-30).ToFileTimeUtc();
		File.WriteAllBytes(start2, EncodeStart2(StartJson("W~Microsoft.Windows.Explorer", "P~Contoso.Mail_8wekyb3d8bbwe!App", "P~Uninstalled.App_1234567890abc!App", "T~placeholder"), ft));

		RegistryKey key = Registry.CurrentUser.CreateSubKey(keyPath, writable: true, RegistryOptions.Volatile)
			?? throw new InvalidOperationException("could not create the fixture key");
		int changes = 300;
		void WriteNative(IEnumerable<FavEntry> entries)
		{
			key.SetValue("Favorites", BuildFavorites(entries), RegistryValueKind.Binary);
			key.SetValue("FavoritesChanges", ++changes, RegistryValueKind.DWord);
		}
		MemorySnapshotStore store = new MemorySnapshotStore();
		Harness h = new Harness();
		try
		{
			WriteNative(native);

			// ---- 1. first run: baseline + catch-up of the one native pin made after the launcher's pins were saved
			Trace("--- step 1. first run: baseline + catch-up of the one native pin made after the launcher's pins were saved");
			NativePinSyncService svc = h.StartService(source, target, store);
			Check("scenario-watchers-armed", svc.TaskbarWatched && svc.StartWatched, $"taskbar={svc.TaskbarWatched} start={svc.StartWatched}");
			bool firstOk = h.WaitFor(1);
			List<PinnedApp> tb = target.TaskbarCopy();
			PinnedApp? chess = tb.FirstOrDefault(p => p.Name == "Chess Lab");
			List<string> l1 = h.LinesSince(0);
			Check("first-run-catch-up-newer-pin", firstOk && chess != null && tb.Count == launcherStart + 1 && tb[^1] == chess,
				$"{tb.Count} pins: {string.Join(", ", tb.Select(p => p.Name))}");
			Check("first-run-working-dir-kept", chess != null && chess.WorkingDir == chessDir && chess.Args == "/c chess_server.cmd --auto-exit 900" && chess.Aumid == null && string.Equals(chess.LaunchPath, exeCmd, StringComparison.OrdinalIgnoreCase),
				chess == null ? "missing" : JsonSerializer.Serialize(chess));
			Check("first-run-older-native-only-not-added", !tb.Any(p => p.Name is "Old Tool" or "Calculator" or "Gone Tool"));
			Check("first-run-provisioned-pin-skipped", !tb.Any(p => p.Name == "Late System") && l1.Any(l => l.Contains("skip 'Late System'") && l.Contains("SystemPinned")));
			Check("first-run-duplicate-not-added", tb.Count(p => string.Equals(p.LaunchPath, exeFirefox, StringComparison.OrdinalIgnoreCase)) == 1 && l1.Any(l => l.Contains("= 'Late Dup' already pinned as 'Firefox Like'")));
			Check("first-run-orphan-lnk-ignored", !tb.Any(p => p.Name == "Orphan"));
			NativePinSnapshot? snap1 = store.Peek();
			Check("first-run-snapshot-baseline", snap1 != null && snap1.TaskbarKnown && snap1.Taskbar.Count == native.Count && snap1.StartKnown && snap1.Start.Count == 4,
				snap1 == null ? "none" : $"taskbar={snap1.Taskbar.Count} start={snap1.Start.Count}");
			Check("first-run-start-baseline-only", target.Win7Pins.Count == 1 && target.MetroTiles.Count == 2 && l1.Any(l => l.Contains("Start first run: baseline 4")));
			Check("first-run-log-one-line-per-change", l1.Count(l => l.StartsWith("Pin sync: taskbar + ", StringComparison.Ordinal)) == 1);
			Check("first-run-near-misses-logged", l1.Any(l => l.Contains("first run: 'Old Tool' was pinned natively")) && l1.Any(l => l.Contains("first run: 'Calculator' was pinned natively"))
				&& !l1.Any(l => l.Contains("first run: 'Viber Like' was pinned")) && !l1.Any(l => l.Contains("first run: 'Gone Tool' was pinned")), string.Join(" | ", l1));

			// ---- 2. a native pin while running (registry watcher -> debounced pass)
			Trace("--- step 2. a native pin while running (registry watcher -> debounced pass)");
			int pc = h.Count;
			WriteLink(Path.Combine(linkDir, "New App.lnk"), exeNew);
			native.Add(new FavEntry { LnkName = "New App.lnk", Aumid = exeNew, PinnedUtc = DateTime.UtcNow });
			WriteNative(native);
			bool p2 = h.WaitFor(pc + 1);
			tb = target.TaskbarCopy();
			Check("live-native-pin-added-at-end", p2 && h.Count == pc + 1 && tb[^1].Name == "New App" && string.Equals(tb[^1].LaunchPath, exeNew, StringComparison.OrdinalIgnoreCase),
				$"passes={h.Count - pc}; last={tb[^1].Name}");

			// ---- 3. packaged app pinned natively (Taskband blob only, no .lnk)
			Trace("--- step 3. packaged app pinned natively (Taskband blob only, no .lnk)");
			pc = h.Count;
			native.Add(new FavEntry { PackagedAumid = "Contoso.Notes_8wekyb3d8bbwe!App", PinnedUtc = DateTime.UtcNow });
			WriteNative(native);
			h.WaitFor(pc + 1);
			tb = target.TaskbarCopy();
			PinnedApp? notes = tb.LastOrDefault();
			Check("live-packaged-pin-added", notes != null && notes.Name == "Contoso Notes" && notes.LaunchPath == "shell:AppsFolder\\Contoso.Notes_8wekyb3d8bbwe!App" && notes.Aumid == "Contoso.Notes_8wekyb3d8bbwe!App",
				notes == null ? "none" : JsonSerializer.Serialize(notes));

			// ---- 4. native unpin: one the launcher has (AppsFolder full-path form), one it does not
			Trace("--- step 4. native unpin: one the launcher has (AppsFolder full-path form), one it does not");
			pc = h.Count;
			native.RemoveAll(e => e.LnkName is "Viber Like.lnk" or "Old Tool.lnk" or "Gone Tool.lnk");   // Gone Tool: its program was uninstalled
			WriteNative(native);
			h.WaitFor(pc + 1);
			tb = target.TaskbarCopy();
			List<string> l4 = h.LinesSince(pc);
			Check("live-native-unpin-removed", !tb.Any(p => p.Name == "Viber Like") && l4.Any(l => l.Contains("taskbar - 'Viber Like'")), string.Join(" | ", l4));
			Check("live-native-unpin-not-in-launcher", l4.Any(l => l.Contains("'Old Tool' unpinned natively, not on the launcher taskbar")) && tb.Any(p => p.Name == "Launcher Only"));
			Check("live-native-unpin-not-in-launcher-toast", target.LastLive && target.LastNotOnTaskbar.SequenceEqual(new[] { "Old Tool" }) && l4.Any(l => l.Contains("'Gone Tool' left the native taskbar (its program is gone)")),
				$"live={target.LastLive}; names=[{string.Join(", ", target.LastNotOnTaskbar)}]");

			// ---- 5a. burst: pinned then unpinned within the debounce window -> one pass, no change
			Trace("--- step 5a. burst: pinned then unpinned within the debounce window -> one pass, no change");
			pc = h.Count;
			int before5 = tb.Count;
			WriteLink(Path.Combine(linkDir, "Burst App.lnk"), exeBurst);
			WriteNative(native.Append(new FavEntry { LnkName = "Burst App.lnk", Aumid = exeBurst, PinnedUtc = DateTime.UtcNow }));
			Nap(60);
			WriteNative(native);
			h.WaitFor(pc + 1, quietMs: 1200);
			tb = target.TaskbarCopy();
			Check("burst-pin-unpin-one-pass-no-change", h.Count == pc + 1 && tb.Count == before5 && !tb.Any(p => p.Name == "Burst App"), $"passes={h.Count - pc}; pins {before5}->{tb.Count}");

			// ---- 5b. burst: two pins 60 ms apart -> one pass, both added in order
			Trace("--- step 5b. burst: two pins 60 ms apart -> one pass, both added in order");
			pc = h.Count;
			WriteLink(Path.Combine(linkDir, "Burst Two.lnk"), exeBurst, "--two");
			native.Add(new FavEntry { LnkName = "Burst App.lnk", Aumid = exeBurst, PinnedUtc = DateTime.UtcNow });
			WriteNative(native);
			Nap(60);
			native.Add(new FavEntry { LnkName = "Burst Two.lnk", Aumid = "Microsoft.AutoGenerated.{B0B0B0B0-0000-4000-8000-000000000002}", PinnedUtc = DateTime.UtcNow });
			WriteNative(native);
			h.WaitFor(pc + 1, quietMs: 1200);
			tb = target.TaskbarCopy();
			Check("burst-two-pins-one-pass", h.Count == pc + 1 && tb.Count >= 2 && tb[^2].Name == "Burst App" && tb[^1].Name == "Burst Two" && tb[^1].Args == "--two", $"passes={h.Count - pc}; tail={string.Join(",", tb.TakeLast(2).Select(p => p.Name))}");

			// ---- 6. rename of a native pin's .lnk: same app, no unpin + pin
			Trace("--- step 6. rename of a native pin's .lnk: same app, no unpin + pin");
			pc = h.Count;
			int before6 = tb.Count;
			File.Move(Path.Combine(linkDir, "Chess Lab.lnk"), Path.Combine(linkDir, "Chess Lab (2).lnk"));
			native.First(e => e.LnkName == "Chess Lab.lnk").LnkName = "Chess Lab (2).lnk";
			WriteNative(native);
			h.WaitFor(pc + 1);
			tb = target.TaskbarCopy();
			List<string> l6 = h.LinesSince(pc);
			Check("rename-no-change", tb.Count == before6 && tb.Count(p => p.Name == "Chess Lab") == 1 && l6.Any(l => l.Contains("renamed")) && !l6.Any(l => l.Contains("taskbar + ") || l.Contains("taskbar - ")),
				string.Join(" | ", l6));
			Check("rename-snapshot-updated", store.Peek()?.Taskbar.Any(r => r.LinkName == "Chess Lab (2).lnk") == true);

			// ---- 7. native pins that duplicate existing launcher pins in other identity forms
			Trace("--- step 7. native pins that duplicate existing launcher pins in other identity forms");
			pc = h.Count;
			int before7 = tb.Count;
			// New to the native taskbar, already on ours under another identity form: a "{FOLDERID_System}\x.exe" AUMID
			// vs a plain exe path, a packaged AUMID vs an AppsFolder pin, a Squirrel stub with different spacing.
			WriteLink(Path.Combine(linkDir, "LO Native.lnk"), exeLauncherOnly);
			WriteLink(Path.Combine(linkDir, "Squirrelish.lnk"), Path.Combine(squirrel2, "Update.exe"), "--processStart   Squirrelish.exe");
			native.Add(new FavEntry { LnkName = "LO Native.lnk", Aumid = "{1AC14E77-02E7-4E5D-B744-2EB1AE5198B7}\\" + Path.GetFileName(exeLauncherOnly), PinnedUtc = DateTime.UtcNow });
			native.Add(new FavEntry { PackagedAumid = "Contoso.Mail_8wekyb3d8bbwe!App", PinnedUtc = DateTime.UtcNow });
			native.Add(new FavEntry { LnkName = "Squirrelish.lnk", Aumid = "com.squirrel.Squirrelish.Squirrelish", PinnedUtc = DateTime.UtcNow });
			// And a second native pin of an app the native taskbar already had (same exe as 'Late Dup').
			WriteLink(Path.Combine(linkDir, "Firefox Again.lnk"), exeFirefox);
			native.Add(new FavEntry { LnkName = "Firefox Again.lnk", Aumid = "Microsoft.AutoGenerated.{F1F1F1F1-0000-4000-8000-000000000003}", PinnedUtc = DateTime.UtcNow });
			WriteNative(native);
			h.WaitFor(pc + 1);
			tb = target.TaskbarCopy();
			List<string> l7 = h.LinesSince(pc);
			Check("duplicates-of-launcher-pins-not-added", tb.Count == before7
				&& l7.Any(l => l.Contains("= 'LO Native' already pinned as 'Launcher Only'"))
				&& l7.Any(l => l.Contains("already pinned as 'Mail'"))
				&& l7.Any(l => l.Contains("= 'Squirrelish' already pinned as 'Squirrelish'")), string.Join(" | ", l7));
			Check("second-native-pin-of-same-app-no-change", l7.Any(l => l.Contains("'Firefox Again' is another native pin")) && !l7.Any(l => l.Contains("renamed")));

			// ---- 8. deferral (pin drag in progress): retried, applied once
			Trace("--- step 8. deferral (pin drag in progress): retried, applied once");
			pc = h.Count;
			target.DeferTaskbarTimes = 2;
			WriteLink(Path.Combine(linkDir, "Deferred App.lnk"), exeDown, "--deferred");
			native.Add(new FavEntry { LnkName = "Deferred App.lnk", Aumid = "Microsoft.AutoGenerated.{DEFE0000-0000-4000-8000-000000000004}", PinnedUtc = DateTime.UtcNow });
			WriteNative(native);
			h.WaitFor(pc + 3, timeoutMs: 10000);
			tb = target.TaskbarCopy();
			List<string> l8 = h.LinesSince(pc);
			Check("deferred-then-applied-once", tb.Count(p => p.Name == "Deferred App") == 1 && h.Count == pc + 3 && l8.Count(l => l.StartsWith("Pin sync: taskbar waiting", StringComparison.Ordinal)) == 1,
				$"passes={h.Count - pc}; {string.Join(" | ", l8)}");
			Check("deferred-retries-read-only-the-taskbar", h.ReportsSince(pc).All(r => r.Halves == PinSyncHalves.Taskbar && !r.StartRead));

			// ---- 9. native store unreadable: nothing changes, snapshot kept
			Trace("--- step 9. native store unreadable: nothing changes, snapshot kept");
			pc = h.Count;
			string? snapBefore9 = store.Json;
			int pins9 = tb.Count;
			key.SetValue("Favorites", new byte[] { 0x00, 0x40, 0x00, 0x00, 0x00, 0x01 }, RegistryValueKind.Binary);
			h.WaitFor(pc + 1);
			tb = target.TaskbarCopy();
			Check("unreadable-native-no-change", tb.Count == pins9 && store.Json == snapBefore9 && h.LinesSince(pc).Any(l => l.Contains("native taskbar unreadable")));
			pc = h.Count;
			WriteNative(native);
			h.WaitFor(pc + 1);
			tb = target.TaskbarCopy();
			Check("unreadable-native-recovers-without-changes", tb.Count == pins9 && !h.LinesSince(pc).Any(l => l.Contains("taskbar + ") || l.Contains("taskbar - ")));

			// ---- 10. mass change (reset/restore of the native taskbar): re-baseline, launcher unchanged
			Trace("--- step 10. mass change (reset/restore of the native taskbar): re-baseline, launcher unchanged");
			pc = h.Count;
			List<FavEntry> saved = native.ToList();
			WriteNative(native.Take(1));
			h.WaitFor(pc + 1);
			tb = target.TaskbarCopy();
			Check("mass-removal-guarded", tb.Count == pins9 && h.LinesSince(pc).Any(l => l.Contains("re-baselined")), string.Join(" | ", h.LinesSince(pc)));
			pc = h.Count;
			WriteNative(saved);
			h.WaitFor(pc + 1);
			tb = target.TaskbarCopy();
			Check("mass-restore-guarded", tb.Count == pins9 && h.LinesSince(pc).Any(l => l.Contains("re-baselined")), string.Join(" | ", h.LinesSince(pc)));

			// ---- 10b. a native pin whose shortcut is locked (a scanner holds it) when it is read: retried, added once
			Trace("--- step 10b. a native pin whose shortcut is locked when it is read: retried, added once");
			pc = h.Count;
			string lockedLnk = Path.Combine(linkDir, "Locked App.lnk");
			WriteLink(lockedLnk, exeLocked, "--locked");
			List<PinSyncPassReport> r10b;
			using (FileStream hold = new FileStream(lockedLnk, FileMode.Open, FileAccess.Read, FileShare.None))
			{
				native.Add(new FavEntry { LnkName = "Locked App.lnk", Aumid = "Microsoft.AutoGenerated.{10C10000-0000-4000-8000-000000000007}", PinnedUtc = DateTime.UtcNow });
				WriteNative(native);
				h.WaitFor(pc + 1, quietMs: 0);
				r10b = h.ReportsSince(pc);
			}
			Check("locked-shortcut-waits", r10b.Count >= 1 && r10b[0].TaskbarPending == 1 && r10b[0].Lines.Any(l => l.Contains("skip 'Locked App' (shortcut could not be read yet; retried later)")) && !target.TaskbarCopy().Any(p => p.Name == "Locked App"),
				string.Join(" | ", r10b.SelectMany(r => r.Lines)));
			h.WaitUntil(() => target.TaskbarCopy().Any(p => p.Name == "Locked App"), 6000);
			h.WaitFor(h.Count, quietMs: 1200);
			tb = target.TaskbarCopy();
			Check("locked-shortcut-added-after-retry", tb.Count(p => p.Name == "Locked App") == 1 && tb.First(p => p.Name == "Locked App").Args == "--locked" && h.ReportsSince(pc).Any(r => r.Reasons.Contains("retry")),
				string.Join(" | ", h.LinesSince(pc)));

			// ---- 10c. a native pin whose program is not there yet (drive not mounted): retried, added once it appears
			Trace("--- step 10c. a native pin whose program is not there yet: retried, added once it appears");
			pc = h.Count;
			string laterExe = Path.Combine(fixture, "Later", "Later Tool.exe");
			Directory.CreateDirectory(Path.GetDirectoryName(laterExe)!);
			File.Copy(exeLater, laterExe, overwrite: true);
			WriteLink(Path.Combine(linkDir, "Later Tool.lnk"), laterExe);
			File.Delete(laterExe);
			native.Add(new FavEntry { LnkName = "Later Tool.lnk", Aumid = laterExe, PinnedUtc = DateTime.UtcNow });
			WriteNative(native);
			h.WaitFor(pc + 1, quietMs: 0);
			List<PinSyncPassReport> r10c = h.ReportsSince(pc);
			Check("missing-program-waits", r10c.Count >= 1 && r10c[0].TaskbarPending == 1 && r10c[0].Lines.Any(l => l.Contains("skip 'Later Tool' (target not found") && l.Contains("retried later")),
				string.Join(" | ", r10c.SelectMany(r => r.Lines)));
			File.Copy(exeLater, laterExe, overwrite: true);
			h.WaitUntil(() => target.TaskbarCopy().Any(p => p.Name == "Later Tool"), 6000);
			h.WaitFor(h.Count, quietMs: 1200);
			tb = target.TaskbarCopy();
			Check("missing-program-added-when-back", tb.Count(p => p.Name == "Later Tool") == 1 && h.ReportsSince(pc).Last().TaskbarPending == 0, string.Join(" | ", h.LinesSince(pc)));
			Check("pins-built-on-worker", target.AllPrepared);

			// ---- 10d. a native pin updated in place (same shortcut, new program and app id): position and group kept
			Trace("--- step 10d. a native pin updated in place: position and group kept");
			pc = h.Count;
			target.SetGroup("Burst Two", "Tools");
			int burstIndex = target.TaskbarCopy().FindIndex(p => p.Name == "Burst Two");
			WriteLink(Path.Combine(linkDir, "Burst Two.lnk"), exeUpdated, "--two-v2");
			native.First(e => e.LnkName == "Burst Two.lnk").Aumid = "Microsoft.AutoGenerated.{B0B0B0B0-0000-4000-8000-0000000000F2}";
			WriteNative(native);
			h.WaitFor(pc + 1);
			tb = target.TaskbarCopy();
			int burstAfter = tb.FindIndex(p => p.Name == "Burst Two");
			Check("update-in-place", burstIndex >= 0 && burstAfter == burstIndex && string.Equals(tb[burstAfter].LaunchPath, exeUpdated, StringComparison.OrdinalIgnoreCase) && tb[burstAfter].Args == "--two-v2" && tb[burstAfter].GroupName == "Tools"
				&& tb.Count(p => p.Name == "Burst Two") == 1 && h.LinesSince(pc).Any(l => l.Contains("~ 'Burst Two' updated in place")),
				$"index {burstIndex}->{burstAfter}; {string.Join(" | ", h.LinesSince(pc))}");

			// ---- 10e. Windows takes a provisioned pin away and puts it back: the launcher pin returns where it was
			Trace("--- step 10e. Windows takes a provisioned pin away and puts it back: the launcher pin returns where it was");
			pc = h.Count;
			int explorerIndex = target.TaskbarCopy().FindIndex(p => p.Name == "File Explorer");
			FavEntry explorerFav = native.First(e => e.LnkName == "File Explorer.lnk");
			native.Remove(explorerFav);
			WriteNative(native);
			h.WaitFor(pc + 1);
			bool goneFirst = !target.TaskbarCopy().Any(p => p.Name == "File Explorer");
			int pc10e = h.Count;
			native.Insert(0, explorerFav);
			WriteNative(native);
			h.WaitFor(pc10e + 1);
			tb = target.TaskbarCopy();
			Check("reprovisioned-pin-restored", explorerIndex >= 0 && goneFirst && tb.FindIndex(p => p.Name == "File Explorer") == explorerIndex && tb.Count(p => p.Name == "File Explorer") == 1
				&& h.LinesSince(pc10e).Any(l => l.Contains("+ 'File Explorer' restored at position")),
				$"index {explorerIndex}; removed={goneFirst}; {string.Join(" | ", h.LinesSince(pc))}");
			pins9 = tb.Count;

			// ---- 11. Start, Windows 7 style: native pin (installed, not installed) through the start2.bin watcher
			Trace("--- step 11. Start, Windows 7 style: native pin (installed, not installed) through the start2.bin watcher");
			pc = h.Count;
			target.Win7Style = true;
			File.WriteAllBytes(start2, EncodeStart2(StartJson("W~Microsoft.Windows.Explorer", "P~Contoso.Mail_8wekyb3d8bbwe!App", "P~Uninstalled.App_1234567890abc!App", "T~placeholder",
				"P~Contoso.Paint_8wekyb3d8bbwe!App", "W~{6D809377-6AF0-444B-8957-A3773F02200E}\\Contoso\\Tool.exe", "P~NotInstalled.App_zzz!App"), ft + 1000));
			h.WaitFor(pc + 1, quietMs: 1500);
			List<string> l11 = h.LinesSince(pc);
			Check("start-win7-pins-added", target.Win7Pins.Contains("shell:AppsFolder\\Contoso.Paint_8wekyb3d8bbwe!App") && target.Win7Pins.Contains("shell:AppsFolder\\{6D809377-6AF0-444B-8957-A3773F02200E}\\Contoso\\Tool.exe") && target.Win7Pins.Count == 3 && target.MetroTiles.Count == 2,
				$"win7=[{string.Join(", ", target.Win7Pins)}] metro=[{string.Join(", ", target.MetroTiles)}]");
			Check("start-not-installed-skipped", l11.Any(l => l.Contains("skip 'NotInstalled.App_zzz!App' (not installed; retried later)")), string.Join(" | ", l11));

			// ---- 12. Start: native unpin removes it from both Start styles
			Trace("--- step 12. Start: native unpin removes it from both Start styles");
			pc = h.Count;
			File.WriteAllBytes(start2, EncodeStart2(StartJson("P~Contoso.Mail_8wekyb3d8bbwe!App", "P~Uninstalled.App_1234567890abc!App", "T~placeholder",
				"P~Contoso.Paint_8wekyb3d8bbwe!App", "W~{6D809377-6AF0-444B-8957-A3773F02200E}\\Contoso\\Tool.exe", "P~NotInstalled.App_zzz!App"), ft + 2000));
			h.WaitFor(pc + 1, quietMs: 1500);
			Check("start-unpin-removed-from-both", !target.Win7Pins.Any(p => p.Contains("Microsoft.Windows.Explorer")) && !target.MetroTiles.Any(p => p.Contains("Microsoft.Windows.Explorer")),
				$"win7=[{string.Join(", ", target.Win7Pins)}] metro=[{string.Join(", ", target.MetroTiles)}]");

			// ---- 13. Start, Metro style: a new pin becomes a tile; one that already has a tile is not duplicated
			Trace("--- step 13. Start, Metro style: a new pin becomes a tile; one that already has a tile is not duplicated");
			pc = h.Count;
			target.Win7Style = false;
			int win7Count = target.Win7Pins.Count;
			File.WriteAllBytes(start2, EncodeStart2(StartJson("P~Contoso.Mail_8wekyb3d8bbwe!App", "P~Uninstalled.App_1234567890abc!App", "T~placeholder",
				"P~Contoso.Paint_8wekyb3d8bbwe!App", "W~{6D809377-6AF0-444B-8957-A3773F02200E}\\Contoso\\Tool.exe", "P~NotInstalled.App_zzz!App",
				"P~Contoso.Notes_8wekyb3d8bbwe!App", "P~Contoso.Photos_8wekyb3d8bbwe!App"), ft + 3000));
			h.WaitFor(pc + 1, quietMs: 1500);
			List<string> l13 = h.LinesSince(pc);
			Check("start-metro-tile-added", target.MetroTiles.Contains("shell:AppsFolder\\Contoso.Notes_8wekyb3d8bbwe!App") && target.MetroTiles.Count(p => p.Contains("Contoso.Photos")) == 1 && target.Win7Pins.Count == win7Count,
				$"metro=[{string.Join(", ", target.MetroTiles)}] {string.Join(" | ", l13)}");
			Check("start-metro-duplicate-not-added", l13.Any(l => l.Contains("= 'Contoso Photos' already pinned (Start screen)")));

			// ---- 14. Start: corrupt / truncated start2.bin -> nothing changes, snapshot kept
			Trace("--- step 14. Start: corrupt / truncated start2.bin -> nothing changes, snapshot kept");
			pc = h.Count;
			NativePinSnapshot? s14 = store.Peek();
			byte[] good = File.ReadAllBytes(start2);
			File.WriteAllBytes(start2, good.Take(good.Length / 2).ToArray());
			h.WaitFor(pc + 1, quietMs: 1500);
			NativePinSnapshot? s14b = store.Peek();
			Check("start-corrupt-no-change", s14 != null && s14b != null && s14b.Start.SequenceEqual(s14.Start) && target.MetroTiles.Count == 2 && h.LinesSince(pc).Any(l => l.Contains("Start layout unreadable")),
				string.Join(" | ", h.LinesSince(pc)));
			pc = h.Count;
			File.WriteAllBytes(start2, good);
			h.WaitFor(pc + 1, quietMs: 1500);
			Check("start-recovers-without-changes", target.MetroTiles.Count == 2 && !h.LinesSince(pc).Any(l => l.Contains("Start + ") || l.Contains("Start - ")));

			// ---- 14b. Start: a native pin of an app that is not in the app list yet: retried, pinned once it is listed
			Trace("--- step 14b. Start: a native pin of an app not in the app list yet: retried, pinned once it is listed");
			h.WaitFor(h.Count, quietMs: 3500);   // let the earlier not-installed retries run out
			pc = h.Count;
			target.Win7Style = true;
			File.WriteAllBytes(start2, EncodeStart2(StartJson("P~Contoso.Mail_8wekyb3d8bbwe!App", "P~Uninstalled.App_1234567890abc!App", "T~placeholder",
				"P~Contoso.Paint_8wekyb3d8bbwe!App", "W~{6D809377-6AF0-444B-8957-A3773F02200E}\\Contoso\\Tool.exe", "P~NotInstalled.App_zzz!App",
				"P~Contoso.Notes_8wekyb3d8bbwe!App", "P~Contoso.Photos_8wekyb3d8bbwe!App", "P~Contoso.Later_8wekyb3d8bbwe!App"), ft + 3500));
			h.WaitFor(pc + 1, quietMs: 0);
			List<PinSyncPassReport> r14b = h.ReportsSince(pc);
			bool laterWaits = r14b.Count >= 1 && r14b[0].StartPending == 2 && r14b[0].Lines.Any(l => l.Contains("skip 'Contoso.Later_8wekyb3d8bbwe!App' (not installed; retried later)"))
				&& !target.Win7Pins.Any(p => p.Contains("Contoso.Later"));
			lock (target.Gate)
			{
				target.Inventory.Add(("shell:AppsFolder\\Contoso.Later_8wekyb3d8bbwe!App", "Contoso Later"));
			}
			h.WaitUntil(() => { lock (target.Gate) { return target.Win7Pins.Any(p => p.Contains("Contoso.Later")); } }, 6000);
			Check("start-not-listed-yet-waits", laterWaits, string.Join(" | ", r14b.SelectMany(r => r.Lines)));
			Check("start-pinned-once-listed", target.Win7Pins.Count(p => p.Contains("Contoso.Later")) == 1 && h.ReportsSince(pc).Any(r => r.Reasons.Contains("retry") && r.Halves == PinSyncHalves.Start && !r.TaskbarRead),
				$"win7=[{string.Join(", ", target.Win7Pins)}]; {string.Join(" | ", h.LinesSince(pc))}");
			h.WaitFor(h.Count, quietMs: 3500);

			// ---- 15. launcher restart, with native changes made while it was not running
			Trace("--- step 15. launcher restart, with native changes made while it was not running");
			svc.Dispose();
			int afterStop = h.Count;
			WriteLink(Path.Combine(linkDir, "While Down.lnk"), exeDown, "--while-down");
			native.Add(new FavEntry { LnkName = "While Down.lnk", Aumid = "Microsoft.AutoGenerated.{D0D0D0D0-0000-4000-8000-000000000005}", PinnedUtc = DateTime.UtcNow });
			native.RemoveAll(e => e.LnkName == "New App.lnk");
			WriteNative(native);
			File.WriteAllBytes(start2, EncodeStart2(StartJson("P~Contoso.Mail_8wekyb3d8bbwe!App", "P~Uninstalled.App_1234567890abc!App", "T~placeholder",
				"P~Contoso.Paint_8wekyb3d8bbwe!App", "W~{6D809377-6AF0-444B-8957-A3773F02200E}\\Contoso\\Tool.exe",
				"P~Contoso.Photos_8wekyb3d8bbwe!App", "P~Contoso.Later_8wekyb3d8bbwe!App"), ft + 4000));
			Nap(900);
			Check("stopped-service-does-nothing", h.Count == afterStop, $"passes after stop={h.Count - afterStop}");
			Harness h2 = new Harness();
			NativePinSyncService svc2 = h2.StartService(source, target, store);
			h2.WaitFor(1, quietMs: 1500);
			tb = target.TaskbarCopy();
			List<string> l15 = h2.LinesSince(0);
			Check("restart-catches-up-pin-made-while-down", tb.Count(p => p.Name == "While Down") == 1 && tb[^1].Name == "While Down", string.Join(" | ", l15));
			Check("restart-catches-up-unpin-made-while-down", !tb.Any(p => p.Name == "New App"));
			Check("restart-catches-up-start-unpin", !target.MetroTiles.Any(p => p.Contains("Contoso.Notes")));
			Check("restart-no-first-run", !l15.Any(l => l.Contains("first run")));

			// ---- 16. snapshot unreadable: paused, nothing changes
			Trace("--- step 16. snapshot unreadable: paused, nothing changes");
			int pc16 = h2.Count;
			store.Unreadable = true;
			int pins16 = tb.Count;
			WriteLink(Path.Combine(linkDir, "Paused App.lnk"), exeDown, "--paused");
			native.Add(new FavEntry { LnkName = "Paused App.lnk", Aumid = "Microsoft.AutoGenerated.{FAFA0000-0000-4000-8000-000000000006}", PinnedUtc = DateTime.UtcNow });
			WriteNative(native);
			h2.WaitFor(pc16 + 1);
			Check("snapshot-unreadable-paused", target.TaskbarCopy().Count == pins16 && h2.Reports.Last().Paused);
			store.Unreadable = false;

			// ---- 17. zero idle cost: no pass without a native change
			Trace("--- step 17. zero idle cost: no pass without a native change");
			int idle = h2.Count;
			Nap(2500);
			Check("idle-no-passes", h2.Count == idle, $"passes while idle={h2.Count - idle}");
			svc2.Dispose();

			// ---- 18. separate budgets: a Start that stays deferred does not use up the taskbar's retries
			Trace("--- step 18. separate budgets: a Start that stays deferred does not use up the taskbar's retries");
			lock (target.Gate)
			{
				target.StartReady = false;
			}
			File.WriteAllBytes(start2, EncodeStart2(StartJson("P~Contoso.Mail_8wekyb3d8bbwe!App", "P~Uninstalled.App_1234567890abc!App", "T~placeholder",
				"P~Contoso.Paint_8wekyb3d8bbwe!App", "W~{6D809377-6AF0-444B-8957-A3773F02200E}\\Contoso\\Tool.exe",
				"P~Contoso.Photos_8wekyb3d8bbwe!App", "P~Contoso.Later_8wekyb3d8bbwe!App", "P~Contoso.Notes_8wekyb3d8bbwe!App"), ft + 5000));
			Harness h3 = new Harness();
			NativePinSyncService svc3 = h3.StartService(source, target, store, retryMs: 200, maxDeferrals: 3);
			h3.WaitUntil(() => h3.Reports.Any(r => r.Lines.Any(l => l.StartsWith("Pin sync: Start still waiting", StringComparison.Ordinal))), 6000);
			h3.WaitFor(h3.Count, quietMs: 800);
			List<PinSyncPassReport> r18a = h3.ReportsSince(0);
			bool startExhausted = r18a.Any(r => r.Lines.Any(l => l.StartsWith("Pin sync: Start still waiting", StringComparison.Ordinal)))
				&& r18a.Skip(1).All(r => r.Halves == PinSyncHalves.Start && !r.TaskbarRead)
				&& target.TaskbarCopy().Count(p => p.Name == "Paused App") == 1;
			int pc18 = h3.Count;
			lock (target.Gate)
			{
				target.DeferTaskbarTimes = 2;
			}
			WriteLink(Path.Combine(linkDir, "Budget App.lnk"), exeBudget, "--budget");
			native.Add(new FavEntry { LnkName = "Budget App.lnk", Aumid = "Microsoft.AutoGenerated.{B0D6E700-0000-4000-8000-000000000008}", PinnedUtc = DateTime.UtcNow });
			WriteNative(native);
			h3.WaitUntil(() => target.TaskbarCopy().Any(p => p.Name == "Budget App"), 6000);
			h3.WaitFor(h3.Count, quietMs: 800);
			List<PinSyncPassReport> r18b = h3.ReportsSince(pc18);
			Check("start-deferral-budget-separate", startExhausted, string.Join(" | ", r18a.Select(r => $"[{r.Halves} tb={r.TaskbarRead} st={r.StartRead} def={r.Deferred}] " + string.Join(" / ", r.Lines))));
			Check("taskbar-retries-after-start-budget-spent", target.TaskbarCopy().Count(p => p.Name == "Budget App") == 1 && r18b.Count == 3 && r18b.All(r => r.Halves == PinSyncHalves.Taskbar && !r.StartRead)
				&& r18b.Count(r => r.Lines.Any(l => l.StartsWith("Pin sync: taskbar waiting", StringComparison.Ordinal))) == 1,
				string.Join(" | ", r18b.Select(r => $"[{r.Halves} {r.Reasons} def={r.Deferred}] " + string.Join(" / ", r.Lines))));
			lock (target.Gate)
			{
				target.StartReady = true;
			}

			// ---- 19. the watches survive their key or folder being deleted and re-created
			Trace("--- step 19a. the native taskbar key is deleted and re-created: the watch is re-established");
			int pc19 = h3.Count;
			key.Dispose();
			Registry.CurrentUser.DeleteSubKeyTree(keyPath, throwOnMissingSubKey: false);
			bool lostSeen = h3.WaitUntil(() => !svc3.TaskbarWatched, 3000);
			key = Registry.CurrentUser.CreateSubKey(keyPath, writable: true, RegistryOptions.Volatile) ?? throw new InvalidOperationException("could not re-create the fixture key");
			WriteLink(Path.Combine(linkDir, "Recovered App.lnk"), exeRecovered, "--recovered");
			native.Add(new FavEntry { LnkName = "Recovered App.lnk", Aumid = "Microsoft.AutoGenerated.{2EC0BE2E-0000-4000-8000-000000000009}", PinnedUtc = DateTime.UtcNow });
			WriteNative(native);
			h3.WaitUntil(() => svc3.TaskbarWatched && target.TaskbarCopy().Any(p => p.Name == "Recovered App"), 8000);
			h3.WaitFor(h3.Count, quietMs: 800);
			Check("taskbar-watch-recovers-after-key-recreated", lostSeen && svc3.TaskbarWatched && target.TaskbarCopy().Count(p => p.Name == "Recovered App") == 1,
				$"lost={lostSeen}; watched={svc3.TaskbarWatched}; {string.Join(" | ", h3.ReportsSince(pc19).Select(r => $"[{r.Reasons}] " + string.Join(" / ", r.Lines)))}");
			int pc19b = h3.Count;
			native.RemoveAll(e => e.LnkName == "Recovered App.lnk");
			WriteNative(native);
			h3.WaitUntil(() => !target.TaskbarCopy().Any(p => p.Name == "Recovered App"), 5000);
			Check("taskbar-watch-live-after-recovery", !target.TaskbarCopy().Any(p => p.Name == "Recovered App") && h3.ReportsSince(pc19b).Any(r => r.Reasons.Contains("taskbar change")),
				string.Join(" | ", h3.LinesSince(pc19b)));

			Trace("--- step 19b. the native Start folder is deleted and re-created: the watch is re-established");
			int pc19c = h3.Count;
			byte[] keep = File.ReadAllBytes(start2);
			Directory.Delete(startDir, recursive: true);
			bool startLost = h3.WaitUntil(() => !svc3.StartWatched, 3000);
			bool recreated = h3.WaitUntil(() =>
			{
				try
				{
					Directory.CreateDirectory(startDir);
					return Directory.Exists(startDir);
				}
				catch
				{
					return false;
				}
			}, 3000);
			h3.WaitUntil(() => svc3.StartWatched, 5000);
			File.WriteAllBytes(start2, EncodeStart2(StartJson("P~Contoso.Mail_8wekyb3d8bbwe!App", "P~Uninstalled.App_1234567890abc!App", "T~placeholder",
				"P~Contoso.Paint_8wekyb3d8bbwe!App", "W~{6D809377-6AF0-444B-8957-A3773F02200E}\\Contoso\\Tool.exe",
				"P~Contoso.Photos_8wekyb3d8bbwe!App", "P~Contoso.Later_8wekyb3d8bbwe!App"), ft + 6000));
			bool startSeen = h3.WaitUntil(() => h3.ReportsSince(pc19c).Any(r => r.StartRead && r.Reasons.Contains("Start change")), 6000);
			h3.WaitFor(h3.Count, quietMs: 800);
			Check("start-watch-recovers-after-folder-recreated", startLost && recreated && svc3.StartWatched && startSeen && keep.Length > 0,
				$"lost={startLost}; recreated={recreated}; watched={svc3.StartWatched}; seen={startSeen}; {string.Join(" | ", h3.ReportsSince(pc19c).Select(r => $"[{r.Reasons}] " + string.Join(" / ", r.Lines)))}");

			Metrics["Passes"] = h.Count + h2.Count + h3.Count;
			Metrics["SnapshotSaves"] = store.Saves;
			Metrics["FinalLauncherTaskbar"] = target.TaskbarCopy().Select(p => p.Name).ToArray();
			Metrics["FinalWin7Pins"] = target.Win7Pins.ToArray();
			Metrics["FinalMetroTiles"] = target.MetroTiles.ToArray();
			svc3.Dispose();
		}
		finally
		{
			h.Service?.Dispose();
			key.Dispose();
		}
	}

	// ===================================================================== live target (no store is touched)

	// The running shell's target marshals to the UI dispatcher and waits instead of acting when Start is not loaded or
	// the shell is stopping. Neither path reads or writes a launcher store.
	private static void LiveTargetChecks(Dispatcher ui)
	{
		Trace("--- live target: UI marshaling and waits");
		LauncherPinSyncTarget t = new LauncherPinSyncTarget(ui, () => null, () => null);
		StartPinDiff d = new StartPinDiff();
		d.Added.Add("P~Contoso.Mail_8wekyb3d8bbwe!App");
		List<string> log = new List<string>();
		PinSyncOutcome o = t.ApplyStart(d, log, out string? why);
		Check("live-target-start-waits-until-loaded", o == PinSyncOutcome.Deferred && why == "Start not loaded yet", $"{o}: {why}");
		t.Dispose();
		TaskbarPinDiff td = new TaskbarPinDiff();
		td.Added.Add(new NativePinRecord { Name = "X", Keys = new List<string> { "path:x|" }, Target = "C:\\x.exe" });
		PinSyncOutcome o2 = t.ApplyTaskbar(td, log, out string? why2);
		Check("live-target-stopped-does-nothing", o2 == PinSyncOutcome.Deferred && why2 == "shutting down" && log.Count == 0, $"{o2}: {why2}");
		Metrics["LauncherPinsStampUtc"] = TaskbarPins.StoreStampAtFirstLoadUtc?.ToString("u") ?? "none";
	}

	// ===================================================================== live, read-only

	// Reads the REAL native taskbar store (registry value + shortcuts, read only) and computes what a first run would
	// do against the launcher's pin file (read only) and the cutoff the shell would use on its first run: the same rule
	// as TaskbarPins.StoreStampAtFirstLoadUtc (the later last write of the pin file and its pending journal in the
	// "state" folder next to it), unless --live-since names one. --live-pins names the pin file when this process sees a
	// different copy than the shell does (a sandboxed AppData). Nothing is applied anywhere.
	private static void LiveReadOnlyDryRun(string[] args)
	{
		string pinsPath = ArgAfter(args, "--live-pins") ?? TaskbarPins.PinsPath;
		string? sinceText = ArgAfter(args, "--live-since");
		Trace("--- live read-only dry run of the real native taskbar store");
		NativePinSource real = new NativePinSource { Start2Path = null };
		List<NativePinRecord>? records = real.ReadTaskbar(out string? error);
		Check("live-readonly-native-read", records != null, error ?? $"{records?.Count} native pins");
		if (records == null)
		{
			return;
		}
		Metrics["LiveNativePins"] = records.Select(r => $"{r.Name} [{r.Source}, {r.Origin}, {r.PinnedUtc:u}{(r.Incomplete ? ", unreadable" : "")}]").ToArray();
		string pendingPath = Path.Combine(Path.GetDirectoryName(pinsPath) ?? string.Empty, "state", "taskbar-pins.pending.json");
		DateTime? stamp = sinceText != null
			? DateTime.Parse(sinceText, null, System.Globalization.DateTimeStyles.AssumeLocal | System.Globalization.DateTimeStyles.AdjustToUniversal)
			: TaskbarPins.StoreStampOf(pinsPath, pendingPath);
		Metrics["LiveCutoffSource"] = sinceText != null ? "--live-since" : "last write of " + pinsPath + " / " + pendingPath;
		Metrics["LiveCutoffUtc"] = stamp?.ToString("O") ?? "none";
		if (!File.Exists(pinsPath) || stamp is not DateTime since)
		{
			Check("live-readonly-first-run-plan", false, "no launcher pin file or cutoff");
			return;
		}
		List<PinnedApp> launcher = JsonSerializer.Deserialize<List<PinnedApp>>(File.ReadAllText(pinsPath)) ?? new List<PinnedApp>();
		TaskbarPinDiff diff = PinSyncEngine.DiffTaskbar(null, records, since);
		PinSyncEngine.PrepareTaskbar(diff);
		List<string> lines = new List<string>();
		List<PinnedApp> copy = launcher.ToList();
		PinSyncEngine.PlanTaskbar(copy, diff, lines);
		List<string> added = copy.Skip(launcher.Count).Select(p => p.Name).ToList();
		Metrics["LiveFirstRunPlan"] = lines;
		foreach (string l in lines)
		{
			Trace("    [live dry-run] " + l);
		}
		int matched = records.Count(r => launcher.Any(p => NativePinSync.LauncherKeys(p).Overlaps(r.Keys)));
		Metrics["LiveMatchedNativePins"] = matched;
		string? expect = ArgAfter(args, "--expect-first-run-add");
		Check("live-readonly-first-run-plan", expect == null ? added.Count <= PinSyncEngine.MaxAddsPerPass : added.SequenceEqual(new[] { expect }),
			$"launcher {launcher.Count} pins, native {records.Count}, matched {matched}, since {since:u}: adds [{string.Join(", ", added)}]");
	}
}
