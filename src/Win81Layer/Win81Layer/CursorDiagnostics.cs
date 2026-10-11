#nullable enable

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;

namespace Win81Layer;

// --cursortest <outDir> [--cursorref <dir>]: offscreen QA of the Alchemy pointers. Read-only by construction: settings,
// state, registry and pointer-folder writes throw, the files are drawn into outDir only and loaded in this process
// only, and the pointer engine is driven against an in-memory registry, including a crash at every write.
internal static class CursorDiagnostics
{
	private static readonly List<(string Name, bool Pass, string Detail)> Checks = new List<(string, bool, string)>();

	private static readonly Dictionary<string, object> Metrics = new Dictionary<string, object>();

	private static readonly string Root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Win81Layer");

	public static void Begin(Application app, string[] args)
	{
		app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
		int idx = Array.IndexOf(args, "--cursortest");
		string outDir = (idx >= 0 && idx + 1 < args.Length && !args[idx + 1].StartsWith("--", StringComparison.Ordinal))
			? args[idx + 1]
			: Path.Combine(Root, "qa", "cursors");
		int refIdx = Array.IndexOf(args, "--cursorref");
		string? refDir = (refIdx >= 0 && refIdx + 1 < args.Length) ? args[refIdx + 1] : null;
		app.Dispatcher.BeginInvoke((Action)delegate
		{
			int code = 1;
			try
			{
				code = Run(outDir, refDir) ? 0 : 1;
			}
			catch (Exception ex)
			{
				try
				{
					Directory.CreateDirectory(outDir);
					File.WriteAllText(Path.Combine(outDir, "cursortest-error.txt"), ex.ToString());
				}
				catch
				{
				}
			}
			app.Shutdown(code);
		}, DispatcherPriority.Loaded);
	}

	private static bool Run(string outDir, string? refDir)
	{
		Directory.CreateDirectory(outDir);
		Baseline before = Baseline.Capture();
		AppSettings real = SettingsStore.Load();
		string files = Path.Combine(outDir, "files");
		try
		{
			SafetyChecks();
			GenerationChecks(outDir, files);
			FileChecks(files);
			LoadChecks(files);
			GeometryChecks();
			LegibilityChecks();
			SourceChecks(outDir, refDir);
			Renders(outDir);
			EngineChecks();
			ProfileChecks(real);
			StatusChecks();
			PcSettingsRenders(outDir, real);
		}
		catch (Exception ex)
		{
			Check("harness-completed", false, ex.ToString());
		}
		finally
		{
			SettingsStore.DiagnosticsOverride = null;
			PointerScheme.StatusOverrideForTest = null;
			AlchemyCursorSet.SwapFaultForTest = null;
		}
		Baseline after = Baseline.Capture();
		Check("safety-registry-unchanged", before.Registry == after.Registry, before.Registry == after.Registry ? before.Registry : $"before={before.Registry} after={after.Registry}");
		Check("safety-schemes-unchanged", before.Schemes == after.Schemes, before.Schemes == after.Schemes ? before.Schemes : $"before={before.Schemes} after={after.Schemes}");
		Check("safety-pointer-folder-unchanged", before.Folder == after.Folder, before.Folder == after.Folder ? "" : $"before={before.Folder} after={after.Folder}");
		Check("safety-state-files-unchanged", before.Hashes == after.Hashes, before.Hashes == after.Hashes ? before.Hashes : $"before={before.Hashes} after={after.Hashes}");
		Check("safety-legacy-snapshot-unchanged", before.Legacy == after.Legacy, $"before={before.Legacy} after={after.Legacy}");
		string[] expected = new string[4] { "pcsettings-pointers.png", "pcsettings-seasonal-pointers.png", "zoom-32.png", "zoom-48.png" }
			.Concat(AlchemyPalettes.All.SelectMany(p => new string[3] { $"sheet-{p.Id}.png", $"anim-{p.Id}-working.png", $"anim-{p.Id}-busy.png" }))
			.ToArray();
		List<string> missing = expected.Where(n => !File.Exists(Path.Combine(outDir, n)) || new FileInfo(Path.Combine(outDir, n)).Length <= 10240).ToList();
		Check("renders-present", missing.Count == 0, missing.Count == 0 ? $"{expected.Length} renders" : "missing or small: " + string.Join(", ", missing));
		Metrics["Renders"] = Directory.GetFiles(outDir, "*.png").Select(Path.GetFileName).ToArray();

		bool passed = Checks.All(c => c.Pass);
		StringBuilder txt = new StringBuilder();
		foreach ((string name, bool pass, string detail) in Checks)
		{
			txt.AppendLine(pass ? "PASS " + name + (detail.Length > 0 && detail.Length < 160 ? "  (" + detail + ")" : "") : "FAIL " + name + ": " + detail);
		}
		File.WriteAllText(Path.Combine(outDir, "cursortest-results.txt"), txt.ToString());
		var report = new
		{
			SchemaVersion = 1,
			GeneratedUtc = DateTime.UtcNow,
			Passed = passed,
			Checks = Checks.Select(c => new { c.Name, c.Pass, c.Detail }).ToArray(),
			Metrics
		};
		File.WriteAllText(Path.Combine(outDir, "cursortest-report.json"), JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
		Logger.Log($"CURSOR-QA: passed={passed} checks={Checks.Count} failed={Checks.Count(c => !c.Pass)} -> {outDir}");
		return passed;
	}

	private static void Check(string name, bool pass, string detail = "")
	{
		Checks.Add((name, pass, detail));
	}

	// ===================================================================== safety

	private static void SafetyChecks()
	{
		string detail = "";
		bool refused = false;
		try
		{
			PointerResult a = PointerScheme.ApplyBlocking("qa");
			PointerResult b = PointerScheme.ApplyBlocking(new AppSettings { AlchemyCursors = "dark" }, "qa");
			PointerResult c = PointerScheme.ApplyAsync("qa").Result;
			PointerResult? d = PointerScheme.ReconcileAsync("qa").Result;
			refused = !a.Success && !b.Success && !c.Success && d == null && a.Message.StartsWith("Read-only", StringComparison.Ordinal) && b.Message == a.Message && c.Message == a.Message;
			detail = a.Message;
		}
		catch (Exception ex)
		{
			detail = ex.Message;
		}
		Check("safety-apply-refused-read-only", refused, detail);
		RealPointerWorld w = new RealPointerWorld();
		List<string> unguarded = new List<string>();
		void Expect(string name, Action action)
		{
			try
			{
				action();
				unguarded.Add(name);
			}
			catch (InvalidOperationException)
			{
			}
			catch (Exception ex)
			{
				unguarded.Add(name + ":" + ex.GetType().Name);
			}
		}
		Expect("WriteValues", () => w.WriteValues(new List<(string, RegValue?)>()));
		Expect("WriteSchemeEntry", () => w.WriteSchemeEntry(PointerScheme.AlchemySchemeName, null));
		Expect("RefreshSystemCursors", () => w.RefreshSystemCursors());
		Expect("SaveState", () => w.SaveState(new PointerStateFile()));
		Expect("DeleteLegacySnapshot", w.DeleteLegacySnapshot);
		Expect("EnsurePalette", () => w.EnsurePalette("dark"));
		Expect("CleanupPalettes", () => w.CleanupPalettes(Array.Empty<string>()));
		Expect("AlchemyCursorSet.Ensure", () => AlchemyCursorSet.Ensure("dark", null, null));
		Expect("AlchemyCursorSet.Cleanup", () => AlchemyCursorSet.Cleanup(Array.Empty<string>()));
		Check("safety-real-writes-throw", unguarded.Count == 0, unguarded.Count == 0 ? "9 write paths refused" : "not guarded: " + string.Join(", ", unguarded));
	}

	// ===================================================================== files

	private static void GenerationChecks(string outDir, string files)
	{
		try
		{
			if (Directory.Exists(files))
			{
				Directory.Delete(files, recursive: true);
			}
		}
		catch
		{
		}
		Dictionary<string, long> ms = new Dictionary<string, long>();
		Dictionary<string, long> bytes = new Dictionary<string, long>();
		List<string> failed = new List<string>();
		foreach (AlchemyPalette p in AlchemyPalettes.All)
		{
			Stopwatch sw = Stopwatch.StartNew();
			AlchemyPaletteFiles? set = AlchemyCursorSet.Ensure(p.Id, files, AlchemyCursorSet.ValidateWithLoadImage);
			sw.Stop();
			ms[p.Id] = sw.ElapsedMilliseconds;
			if (set == null || !set.Generated || set.Paths.Count != 17 || sw.ElapsedMilliseconds >= 1500)
			{
				failed.Add($"{p.Id}: set={(set == null ? "null (" + AlchemyCursorSet.LastError + ")" : set.Paths.Count + " generated=" + set.Generated)} ms={sw.ElapsedMilliseconds}");
				continue;
			}
			bytes[p.Id] = set.Paths.Values.Sum(f => new FileInfo(f).Length);
		}
		Metrics["GenerationMs"] = ms;
		Metrics["PaletteBytes"] = bytes;
		Check("generate-all-palettes-under-1500ms", failed.Count == 0, failed.Count == 0 ? string.Join(", ", ms.Select(kv => kv.Key + "=" + kv.Value + "ms")) : string.Join(" ; ", failed));

		// Reuse: a complete set is not drawn again.
		Stopwatch reuse = Stopwatch.StartNew();
		AlchemyPaletteFiles? again = AlchemyCursorSet.Ensure("dark", files, AlchemyCursorSet.ValidateWithLoadImage);
		reuse.Stop();
		Check("generate-reuses-complete-set", again != null && !again.Generated && reuse.ElapsedMilliseconds < 200, $"generated={again?.Generated} ms={reuse.ElapsedMilliseconds}");
		Metrics["ReuseMs"] = reuse.ElapsedMilliseconds;

		// Manifest hashes match the files.
		List<string> bad = new List<string>();
		foreach (AlchemyPalette p in AlchemyPalettes.All)
		{
			string folder = AlchemyCursorSet.FolderFor(p.Id, files);
			try
			{
				AlchemyCursorManifest? m = JsonSerializer.Deserialize<AlchemyCursorManifest>(File.ReadAllBytes(Path.Combine(folder, AlchemyCursorSet.ManifestName)));
				if (m == null || m.GeneratorVersion != AlchemyCursorSet.GeneratorVersion || m.Palette != p.Id || m.Files.Count != 17)
				{
					bad.Add(p.Id + ": manifest");
					continue;
				}
				foreach (AlchemyCursorManifestFile f in m.Files)
				{
					byte[] data = File.ReadAllBytes(Path.Combine(folder, f.Name));
					if (data.Length != f.Bytes || Convert.ToHexString(SHA256.HashData(data)) != f.Sha256)
					{
						bad.Add(p.Id + "/" + f.Name);
					}
				}
			}
			catch (Exception ex)
			{
				bad.Add(p.Id + ": " + ex.Message);
			}
		}
		Check("generate-manifest-hashes", bad.Count == 0, string.Join(", ", bad));

		// A damaged file makes the set draw again.
		string arrow = Path.Combine(AlchemyCursorSet.FolderFor("dark", files), "arrow.cur");
		long good = new FileInfo(arrow).Length;
		File.WriteAllBytes(arrow, new byte[10]);
		AlchemyPaletteFiles? repaired = AlchemyCursorSet.Ensure("dark", files, AlchemyCursorSet.ValidateWithLoadImage);
		Check("generate-repairs-damaged-set", repaired != null && repaired.Generated && new FileInfo(arrow).Length == good, $"generated={repaired?.Generated} length={new FileInfo(arrow).Length}");

		// If the new set cannot be moved into place, the folder in use (the registry may point at it) is put back.
		string darkFolder = AlchemyCursorSet.FolderFor("dark", files);
		File.WriteAllBytes(arrow, new byte[10]);
		AlchemyCursorSet.SwapFaultForTest = folder => string.Equals(folder, darkFolder, StringComparison.OrdinalIgnoreCase);
		AlchemyPaletteFiles? failedSwap;
		try
		{
			failedSwap = AlchemyCursorSet.Ensure("dark", files, AlchemyCursorSet.ValidateWithLoadImage);
		}
		finally
		{
			AlchemyCursorSet.SwapFaultForTest = null;
		}
		string[] leftovers = Directory.GetDirectories(AlchemyCursorSet.VersionFolder(files), "dark.*").Where(x => !string.Equals(Path.GetFileName(x), "dark", StringComparison.OrdinalIgnoreCase)).ToArray();
		bool keptInPlace = failedSwap == null && Directory.Exists(darkFolder) && new FileInfo(arrow).Length == 10 && File.Exists(Path.Combine(darkFolder, AlchemyCursorSet.ManifestName)) && leftovers.Length == 0;
		AlchemyPaletteFiles? healed = AlchemyCursorSet.Ensure("dark", files, AlchemyCursorSet.ValidateWithLoadImage);
		Check("generate-failed-swap-keeps-folder-in-use", keptInPlace && healed != null && healed.Generated && new FileInfo(arrow).Length == good, $"result={(failedSwap == null ? "null" : "set")} folder={Directory.Exists(darkFolder)} leftovers={leftovers.Length} healed={healed?.Generated}");

		// Cleanup keeps what is asked for and removes other palettes, old versions and leftovers.
		string scratch = Path.Combine(outDir, "cleanup-test");
		try
		{
			if (Directory.Exists(scratch))
			{
				Directory.Delete(scratch, recursive: true);
			}
			Directory.CreateDirectory(Path.Combine(scratch, "v0", "dark"));
			Directory.CreateDirectory(Path.Combine(AlchemyCursorSet.VersionFolder(scratch), "christmas-dark"));
			Directory.CreateDirectory(Path.Combine(AlchemyCursorSet.VersionFolder(scratch), "dark.tmp-1-2"));
			Directory.CreateDirectory(Path.Combine(AlchemyCursorSet.VersionFolder(scratch), "light.old-3"));
			AlchemyCursorSet.Ensure("dark", scratch, null);
			AlchemyCursorSet.Cleanup(new string[1] { "dark" }, scratch);
			string[] left = Directory.GetDirectories(scratch, "*", SearchOption.AllDirectories).Select(d => Path.GetRelativePath(scratch, d)).OrderBy(d => d).ToArray();
			bool ok = left.Length == 2 && left[0] == "v1" && left[1] == Path.Combine("v1", "dark") && AlchemyCursorSet.IsReady("dark", scratch);
			Check("generate-cleanup-keeps-only-active", ok, string.Join(";", left));
			Directory.Delete(scratch, recursive: true);
		}
		catch (Exception ex)
		{
			Check("generate-cleanup-keeps-only-active", false, ex.Message);
		}
	}

	private static void FileChecks(string files)
	{
		List<string> curBad = new List<string>();
		List<string> aniBad = new List<string>();
		List<string> loopBad = new List<string>();
		int curCount = 0;
		int aniCount = 0;
		Dictionary<string, long> aniBytes = new Dictionary<string, long>();
		foreach (AlchemyPalette p in AlchemyPalettes.All)
		{
			string folder = AlchemyCursorSet.FolderFor(p.Id, files);
			foreach ((CursorRole role, string name) in AlchemyCursorSet.Files)
			{
				byte[] data = File.ReadAllBytes(Path.Combine(folder, name));
				if (!AlchemyCursorArt.IsAnimated(role))
				{
					curCount++;
					string? why = CheckCur(data, role, p, 0, aniFrame: false);
					if (why != null)
					{
						curBad.Add($"{p.Id}/{name}: {why}");
					}
					continue;
				}
				aniCount++;
				ParsedAni ani = CursorFileFormat.ReadAni(data);
				List<string> why2 = new List<string>();
				if (ani.RiffSize != data.Length - 8)
				{
					why2.Add($"riff={ani.RiffSize} len={data.Length}");
				}
				if (ani.Form != "ACON")
				{
					why2.Add("form=" + ani.Form);
				}
				if (ani.HeaderSize != 36 || ani.Frames != 24 || ani.Steps != 24 || ani.JifRate != 2 || ani.Flags != 1 || ani.BitCount != 32 || ani.Planes != 1)
				{
					why2.Add($"anih cb={ani.HeaderSize} frames={ani.Frames} steps={ani.Steps} jif={ani.JifRate} flags={ani.Flags}");
				}
				if (ani.Icons.Count != 24 || !ani.ChunksPadded)
				{
					why2.Add($"icons={ani.Icons.Count} padded={ani.ChunksPadded}");
				}
				HashSet<string> distinct = new HashSet<string>();
				for (int fi = 0; fi < ani.Icons.Count; fi++)
				{
					byte[] icon = ani.Icons[fi];
					string? w = CheckCur(icon, role, p, fi, aniFrame: true);
					if (w != null)
					{
						why2.Add($"frame {fi}: " + w);
						break;
					}
					distinct.Add(Convert.ToHexString(SHA256.HashData(icon)));
				}
				aniBytes[p.Id + "/" + name] = data.Length;
				if (distinct.Count != 24)
				{
					why2.Add("distinct frames=" + distinct.Count);
				}
				if (why2.Count > 0)
				{
					aniBad.Add($"{p.Id}/{name}: {string.Join(", ", why2)}");
				}
				foreach (int s in AlchemyCursorArt.Sizes)
				{
					if (!AlchemyCursorArt.RenderBgra(role, p, s, 24).AsSpan().SequenceEqual(AlchemyCursorArt.RenderBgra(role, p, s, 0)))
					{
						loopBad.Add($"{p.Id}/{role}/{s}");
					}
					// The last frame flows into the first: the step is the same as between any two frames.
					double step = MeanAbs(AlchemyCursorArt.RenderBgra(role, p, s, 23), AlchemyCursorArt.RenderBgra(role, p, s, 0));
					double mid = MeanAbs(AlchemyCursorArt.RenderBgra(role, p, s, 11), AlchemyCursorArt.RenderBgra(role, p, s, 12));
					if (Math.Abs(step - mid) > Math.Max(0.15, mid * 0.2))
					{
						loopBad.Add($"{p.Id}/{role}/{s}: wrap step {step:F2} vs {mid:F2}");
					}
				}
			}
		}
		Metrics["AniBytes"] = aniBytes;
		Check("file-cur-structure", curBad.Count == 0, curBad.Count == 0 ? $"{curCount} .cur files: type 2, 5 sizes 32..128, 40-byte BI_RGB 32bpp DIBs, mask = alpha==0, 136606 bytes, hotspots, pixels = renderer" : string.Join(" ; ", curBad.Take(12)));
		Check("file-ani-structure", aniBad.Count == 0, aniBad.Count == 0 ? $"{aniCount} .ani files: RIFF ACON, anih 36/24/24/jif 2/flags 1, 24 distinct 5-size PNG frames (every image < 64 KB into its frame), hotspots, pixels = renderer" : string.Join(" ; ", aniBad.Take(12)));
		Check("file-ani-seamless-loop", loopBad.Count == 0, string.Join(" ; ", loopBad.Take(12)));
	}

	private static double MeanAbs(byte[] a, byte[] b)
	{
		long d = 0;
		for (int i = 0; i < a.Length; i++)
		{
			d += Math.Abs(a[i] - b[i]);
		}
		return (double)d / a.Length;
	}

	// A five-size cursor whose images are exactly what the renderer draws: DIBs in a .cur, PNGs in an .ani frame (each
	// starting inside the first 64 KB of the frame).
	private static string? CheckCur(byte[] data, CursorRole role, AlchemyPalette p, int frame, bool aniFrame)
	{
		ParsedCur cur = CursorFileFormat.ReadCur(data);
		if (cur.Reserved != 0 || cur.Type != 2 || cur.Frames.Count != 5)
		{
			return $"header reserved={cur.Reserved} type={cur.Type} count={cur.Frames.Count}";
		}
		if (!aniFrame && data.Length != 136606)
		{
			return "length " + data.Length;
		}
		for (int i = 0; i < 5; i++)
		{
			ParsedCurFrame f = cur.Frames[i];
			int s = AlchemyCursorArt.Sizes[i];
			bool wantPng = aniFrame;
			if (f.Offset + f.BytesInRes > data.Length || (aniFrame && f.Offset >= CursorFileFormat.AniImageOffsetLimit))
			{
				return $"frame {s}: offset {f.Offset} size {f.BytesInRes}";
			}
			byte[] pixels;
			if (wantPng)
			{
				if (!f.IsPng || f.Width != s || f.Height != s || f.BiWidth != s || f.BiHeight != s)
				{
					return $"frame {s}: png={f.IsPng} {f.BiWidth}x{f.BiHeight}";
				}
				pixels = DecodePng(f.Png, s);
			}
			else
			{
				if (f.IsPng || f.Width != s || f.Height != s || f.HeaderSize != 40 || f.BiWidth != s || f.BiHeight != 2 * s || f.Planes != 1 || f.BitCount != 32 || f.Compression != 0 || f.SizeImage != s * s * 4)
				{
					return $"frame {i} {f.Width}x{f.Height} png={f.IsPng} hs={f.HeaderSize} bi={f.BiWidth}x{f.BiHeight} planes={f.Planes} bpp={f.BitCount} comp={f.Compression} isz={f.SizeImage}";
				}
				if (!f.MaskLengthOk || !f.MaskMatchesAlpha)
				{
					return $"frame {s}: mask length={f.MaskLengthOk} matches alpha={f.MaskMatchesAlpha}";
				}
				pixels = f.Bgra;
			}
			(int hx, int hy) = ExpectedHotspot(role, s);
			if (f.HotX != hx || f.HotY != hy)
			{
				return $"frame {s}: hotspot ({f.HotX},{f.HotY}) want ({hx},{hy})";
			}
			if (!pixels.AsSpan().SequenceEqual(AlchemyCursorArt.RenderBgra(role, p, s, frame)))
			{
				return $"frame {s}: pixels differ from the renderer";
			}
		}
		return null;
	}

	private static byte[] DecodePng(byte[] png, int s)
	{
		using MemoryStream ms = new MemoryStream(png);
		PngBitmapDecoder dec = new PngBitmapDecoder(ms, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
		BitmapSource f = dec.Frames[0];
		if (f.Format != PixelFormats.Bgra32)
		{
			f = new FormatConvertedBitmap(f, PixelFormats.Bgra32, null, 0.0);
		}
		byte[] px = new byte[s * s * 4];
		f.CopyPixels(px, s * 4, 0);
		return px;
	}

	// The hotspot table of the design, written out independently of the renderer.
	private static (int X, int Y) ExpectedHotspot(CursorRole role, int s)
	{
		int i = Array.IndexOf(AlchemyCursorArt.Sizes, s);
		return role switch
		{
			CursorRole.NWPen => (new int[5] { 3, 4, 6, 9, 12 }[i], new int[5] { 3, 4, 6, 9, 12 }[i]),
			CursorRole.UpArrow => (s / 2, new int[5] { 3, 4, 6, 9, 12 }[i]),
			CursorRole.Pin => (s / 2, new int[5] { 28, 42, 56, 85, 113 }[i]),
			_ => (s / 2, s / 2)
		};
	}

	// ===================================================================== loads (this process only)

	private static void LoadChecks(string files)
	{
		List<string> bad = new List<string>();
		int handles = 0;
		foreach (AlchemyPalette p in AlchemyPalettes.All)
		{
			string folder = AlchemyCursorSet.FolderFor(p.Id, files);
			foreach ((CursorRole role, string name) in AlchemyCursorSet.Files)
			{
				string path = Path.Combine(folder, name);
				foreach (int s in AlchemyCursorArt.Sizes)
				{
					nint h = LoadImage(nint.Zero, path, 2u, s, s, 0x10u);
					if (h == nint.Zero)
					{
						bad.Add($"{p.Id}/{name}@{s}: LoadImage failed ({Marshal.GetLastWin32Error()})");
						continue;
					}
					handles++;
					try
					{
						if (!AlchemyCursorArt.IsAnimated(role))
						{
							if (!GetIconInfo(h, out ICONINFO info))
							{
								bad.Add($"{p.Id}/{name}@{s}: GetIconInfo failed");
								continue;
							}
							try
							{
								(int hx, int hy) = ExpectedHotspot(role, s);
								int bw = 0;
								int bh = 0;
								if (info.hbmColor != nint.Zero && GetObject(info.hbmColor, Marshal.SizeOf<BITMAP>(), out BITMAP bm) > 0)
								{
									bw = bm.bmWidth;
									bh = bm.bmHeight;
								}
								if (info.xHotspot != hx || info.yHotspot != hy || bw != s || bh != s)
								{
									bad.Add($"{p.Id}/{name}@{s}: hot=({info.xHotspot},{info.yHotspot}) want ({hx},{hy}) bitmap {bw}x{bh}");
								}
							}
							finally
							{
								if (info.hbmColor != nint.Zero)
								{
									DeleteObject(info.hbmColor);
								}
								if (info.hbmMask != nint.Zero)
								{
									DeleteObject(info.hbmMask);
								}
							}
						}
					}
					finally
					{
						DestroyCursor(h);
					}
				}
				if (AlchemyCursorArt.IsAnimated(role))
				{
					nint a = LoadCursorFromFileW(path);
					if (a == nint.Zero)
					{
						bad.Add($"{p.Id}/{name}: LoadCursorFromFile failed ({Marshal.GetLastWin32Error()})");
					}
					else
					{
						handles++;
						DestroyCursor(a);
					}
				}
			}
		}
		Metrics["LoadedHandles"] = handles;
		Check("load-every-file-every-size", bad.Count == 0, bad.Count == 0 ? $"{handles} handles loaded and destroyed; hotspots and bitmap sizes match" : string.Join(" ; ", bad.Take(12)));

		// Windows draws what was written: each pointer drawn by DrawIconEx over black and over white gives back the
		// renderer's alpha and colour, for DIB images, PNG images and animation frames alike.
		List<string> drawBad = new List<string>();
		List<string> drawn = new List<string>();
		(CursorRole Role, int Size, int Step)[] probes = new (CursorRole, int, int)[12]
		{
			(CursorRole.Arrow, 32, 0), (CursorRole.Arrow, 128, 0), (CursorRole.Hand, 64, 0), (CursorRole.IBeam, 48, 0),
			(CursorRole.Wait, 32, 0), (CursorRole.Wait, 128, 7), (CursorRole.AppStarting, 96, 13), (CursorRole.AppStarting, 64, 23),
			(CursorRole.Wait, 48, 18), (CursorRole.AppStarting, 32, 5), (CursorRole.Pin, 96, 0), (CursorRole.NWPen, 128, 0)
		};
		foreach (AlchemyPalette p in AlchemyPalettes.All)
		{
			string folder = AlchemyCursorSet.FolderFor(p.Id, files);
			foreach ((CursorRole role, int s, int step) in probes)
			{
				string path = Path.Combine(folder, AlchemyCursorSet.FileName(role));
				nint h = LoadImage(nint.Zero, path, 2u, s, s, 0x10u);
				if (h == nint.Zero)
				{
					drawBad.Add($"{p.Id}/{role}@{s}: not loaded");
					continue;
				}
				try
				{
					byte[] black = DrawCursor(h, s, step, 0);
					byte[] white = DrawCursor(h, s, step, 255);
					byte[] want = AlchemyCursorArt.RenderBgra(role, p, s, step);
					int maxA = 0;
					int maxC = 0;
					double sumA = 0;
					for (int i = 0; i < s * s; i++)
					{
						int k = i * 4;
						int a = 255 - ((white[k] - black[k]) + (white[k + 1] - black[k + 1]) + (white[k + 2] - black[k + 2])) / 3;
						int da = Math.Abs(a - want[k + 3]);
						sumA += da;
						maxA = Math.Max(maxA, da);
						for (int ch = 0; ch < 3; ch++)
						{
							int pre = (int)Math.Round(want[k + ch] * want[k + 3] / 255.0);
							maxC = Math.Max(maxC, Math.Abs(black[k + ch] - pre));
						}
					}
					string line = $"{p.Id}/{role}@{s}#{step}: max dA {maxA} max dRGB {maxC} mean dA {sumA / (s * s):F2}";
					if (maxA > 3 || maxC > 3)
					{
						// Which frame was drawn instead, if any.
						int bestFrame = -1;
						double bestMean = double.MaxValue;
						for (int f = 0; f < (AlchemyCursorArt.IsAnimated(role) ? AlchemyCursorArt.AnimFrames : 1); f++)
						{
							byte[] cand = AlchemyCursorArt.RenderBgra(role, p, s, f);
							double m = 0;
							for (int i = 0; i < s * s; i++)
							{
								int k = i * 4;
								int a = 255 - ((white[k] - black[k]) + (white[k + 1] - black[k + 1]) + (white[k + 2] - black[k + 2])) / 3;
								m += Math.Abs(a - cand[k + 3]);
							}
							m /= s * s;
							if (m < bestMean)
							{
								bestMean = m;
								bestFrame = f;
							}
						}
						line += $" (closest frame {bestFrame}, mean dA {bestMean:F2})";
						drawBad.Add(line);
					}
					drawn.Add(line);
				}
				finally
				{
					DestroyCursor(h);
				}
			}
		}
		Metrics["DrawnByWindows"] = drawn;
		Check("load-windows-draws-as-rendered", drawBad.Count == 0, drawBad.Count == 0 ? $"{drawn.Count} pointers drawn by Windows match the renderer within 3/255 (DIB, PNG and animation frames)" : string.Join(" ; ", drawBad.Take(12)));
	}

	// Draws a pointer (an animation frame for an .ani) onto an opaque grey level with DrawIconEx; BGRA, top-down.
	private static byte[] DrawCursor(nint cursor, int s, int step, byte level)
	{
		BITMAPINFOHEADER bih = new BITMAPINFOHEADER
		{
			biSize = 40,
			biWidth = s,
			biHeight = -s,
			biPlanes = 1,
			biBitCount = 32
		};
		nint dc = CreateCompatibleDC(nint.Zero);
		nint dib = CreateDIBSection(dc, ref bih, 0u, out nint bits, nint.Zero, 0u);
		nint old = SelectObject(dc, dib);
		try
		{
			byte[] fill = new byte[s * s * 4];
			for (int i = 0; i < fill.Length; i += 4)
			{
				fill[i] = level;
				fill[i + 1] = level;
				fill[i + 2] = level;
				fill[i + 3] = 255;
			}
			Marshal.Copy(fill, 0, bits, fill.Length);
			DrawIconEx(dc, 0, 0, cursor, s, s, (uint)step, nint.Zero, 3u);
			GdiFlush();
			byte[] o = new byte[s * s * 4];
			Marshal.Copy(bits, o, 0, o.Length);
			return o;
		}
		finally
		{
			SelectObject(dc, old);
			DeleteObject(dib);
			DeleteDC(dc);
		}
	}

	// ===================================================================== geometry and colour

	// Design constants, written out from the measurements (32-unit grid).
	private const double SpecRD = 2.565;

	private const double SpecRIN = 7.7225;

	private const double SpecROUT = 9.05;

	private const double SpecLRIN = 11.5775;

	private const double SpecLROUT = 13.05;

	private const double SpecLRD = 2.5525;

	private static readonly double[] TargetNormalDot = new double[5] { 2.57, 3.85, 5.13, 7.70, 10.26 };

	private static readonly (double In, double Out)[] TargetNormalRing = new (double, double)[5] { (7.72, 9.05), (11.58, 13.58), (15.45, 18.10), (23.17, 27.15), (30.89, 36.20) };

	private static readonly double[] TargetLinkDot = new double[5] { 2.55, 3.83, 5.11, 7.66, 10.21 };

	private static readonly (double In, double Out)[] TargetLinkRing = new (double, double)[5] { (11.58, 13.05), (17.37, 19.58), (23.16, 26.10), (34.73, 39.15), (46.31, 52.20) };

	// Website cursor colours (dot, ring) the seasonal sets must reproduce.
	private static readonly Dictionary<string, (uint Dot, uint Ring)> Website = new Dictionary<string, (uint, uint)>
	{
		["dark"] = (0x2F9DFFu, 0xDEE6F2u),
		["light"] = (0x0A6AC2u, 0x0F192Bu),
		["halloween-dark"] = (0xFF8A2Au, 0xDEE6F2u),
		["halloween-light"] = (0xAA4405u, 0x26160Cu),
		["christmas-dark"] = (0xFF5C70u, 0xDEE6F2u),
		["christmas-light"] = (0xC51332u, 0x2C1115u)
	};

	private static void GeometryChecks()
	{
		List<string> geomBad = new List<string>();
		List<string> fits = new List<string>();
		foreach (string id in new string[2] { "dark", "light" })
		{
			AlchemyPalette p = AlchemyPalettes.Find(id)!;
			for (int i = 0; i < 5; i++)
			{
				int s = AlchemyCursorArt.Sizes[i];
				foreach (bool link in new bool[2] { false, true })
				{
					// The brand geometry is measured without the halo outline, which sits outside the ring.
					byte[] px = AlchemyCursorArt.RenderBgra(link ? CursorRole.Hand : CursorRole.Arrow, p, s, 0, keyline: false);
					(double dot, double rin, double rout, double cx, double cy) = Fit(px, s, p, link);
					double td = link ? TargetLinkDot[i] : TargetNormalDot[i];
					(double ti, double to) = link ? TargetLinkRing[i] : TargetNormalRing[i];
					string line = $"{id}-{(link ? "link" : "normal")}@{s}: centre ({cx:F3},{cy:F3}) dot {dot:F3}/{td} ring {rin:F3}/{ti} {rout:F3}/{to}";
					fits.Add(line);
					if (Math.Abs(dot - td) > 0.3 || Math.Abs(rin - ti) > 0.3 || Math.Abs(rout - to) > 0.3 || Math.Abs(cx - s / 2.0) > 0.05 || Math.Abs(cy - s / 2.0) > 0.05)
					{
						geomBad.Add(line);
					}
				}
			}
		}
		Metrics["GeometryFits"] = fits;
		Check("geometry-normal-link-edges", geomBad.Count == 0, geomBad.Count == 0 ? "dot, ring inner and outer edges within 0.3 px of the measured targets at 32/48/64/96/128, centre within 0.05 px" : string.Join(" ; ", geomBad));

		List<string> colourBad = new List<string>();
		List<string> plateaus = new List<string>();
		foreach (AlchemyPalette p in AlchemyPalettes.All)
		{
			(uint webDot, uint webRing) = Website[p.Id];
			foreach (int s in new int[2] { 64, 128 })
			{
				double u = s / 32.0;
				byte[] n = AlchemyCursorArt.RenderBgra(CursorRole.Arrow, p, s);
				byte[] l = AlchemyCursorArt.RenderBgra(CursorRole.Hand, p, s);
				(uint dotC, int dotA) = Plateau(n, s, 0, SpecRD * u - 1.0);
				(uint ringC, int ringA) = Plateau(n, s, SpecRIN * u + 1.0, SpecROUT * u - 1.0);
				(uint lDotC, int lDotA) = Plateau(l, s, 0, SpecLRD * u - 1.0);
				(uint fillC, int fillA) = Plateau(l, s, SpecLRD * u + 2.0, SpecLRIN * u - 2.0);
				(uint lRingC, int lRingA) = Plateau(l, s, SpecLRIN * u + 1.0, SpecLROUT * u - 1.0);
				string line = $"{p.Id}@{s}: dot #{dotC:X6}/{dotA} ring #{ringC:X6}/{ringA} link dot #{lDotC:X6}/{lDotA} fill #{fillC:X6}/{fillA} ring #{lRingC:X6}/{lRingA}";
				plateaus.Add(line);
				bool ok = Near(dotC, webDot) && dotA == 255
					&& Near(ringC, webRing) && Math.Abs(ringA - p.NeutralAlpha) <= 2
					&& Near(lDotC, webDot) && lDotA == 255
					&& Near(fillC, p.Accent) && Math.Abs(fillA - p.LinkFillAlpha) <= 2
					&& Near(lRingC, p.Accent) && Math.Abs(lRingA - 242) <= 2;
				if (!ok)
				{
					colourBad.Add(line);
				}
			}
		}
		Metrics["Plateaus"] = plateaus;
		Check("colour-plateaus-match-palette-and-website", colourBad.Count == 0, colourBad.Count == 0 ? "dot, ring, fill and link ring within 2/255 of the palette; seasonal dots and rings equal the website's" : string.Join(" ; ", colourBad));

		// Every role, palette, size and frame: a clear 1 px margin and an inked hotspot.
		List<string> marginBad = new List<string>();
		List<string> hotBad = new List<string>();
		int images = 0;
		foreach (AlchemyPalette p in AlchemyPalettes.All)
		{
			foreach (CursorRole role in AlchemyCursorArt.Roles)
			{
				int frames = AlchemyCursorArt.IsAnimated(role) ? AlchemyCursorArt.AnimFrames : 1;
				foreach (int s in AlchemyCursorArt.Sizes)
				{
					(int hx, int hy) = ExpectedHotspot(role, s);
					for (int f = 0; f < frames; f++)
					{
						byte[] px = AlchemyCursorArt.RenderBgra(role, p, s, f);
						images++;
						for (int k = 0; k < s; k++)
						{
							if (px[(0 * s + k) * 4 + 3] != 0 || px[((s - 1) * s + k) * 4 + 3] != 0 || px[(k * s) * 4 + 3] != 0 || px[(k * s + s - 1) * 4 + 3] != 0)
							{
								marginBad.Add($"{p.Id}/{role}@{s}#{f}");
								break;
							}
						}
						int max = 0;
						for (int y = hy - 1; y <= hy + 1; y++)
						{
							for (int x = hx - 1; x <= hx + 1; x++)
							{
								if (x >= 0 && y >= 0 && x < s && y < s)
								{
									max = Math.Max(max, px[(y * s + x) * 4 + 3]);
								}
							}
						}
						if (max < 102)
						{
							hotBad.Add($"{p.Id}/{role}@{s}#{f}: {max}");
						}
					}
				}
			}
		}
		Metrics["CheckedImages"] = images;
		Check("margin-1px-clear-everywhere", marginBad.Count == 0, marginBad.Count == 0 ? $"{images} images" : string.Join(", ", marginBad.Take(12)));
		Check("hotspot-on-ink-everywhere", hotBad.Count == 0, hotBad.Count == 0 ? "3x3 around every hotspot reaches alpha >= 0.4" : string.Join(", ", hotBad.Take(12)));

		// Thin glyph strokes land on whole pixels: the text-select stem is one solid column at 32 px.
		List<string> crispBad = new List<string>();
		foreach (AlchemyPalette p in AlchemyPalettes.All)
		{
			byte[] px = AlchemyCursorArt.RenderBgra(CursorRole.IBeam, p, 32);
			for (int y = 11; y <= 21; y++)
			{
				int solid = 0;
				for (int x = 0; x < 32; x++)
				{
					if (px[(y * 32 + x) * 4 + 3] >= 230)
					{
						solid++;
					}
				}
				if (solid != 1)
				{
					crispBad.Add($"{p.Id} row {y}: {solid} columns");
				}
			}
			// The serifs are one solid row each.
			int rows = 0;
			for (int y = 0; y < 32; y++)
			{
				if (px[(y * 32 + 18) * 4 + 3] >= 230)
				{
					rows++;
				}
			}
			if (rows != 2)
			{
				crispBad.Add($"{p.Id}: serif rows {rows}");
			}
		}
		Check("glyph-strokes-crisp-at-32", crispBad.Count == 0, crispBad.Count == 0 ? "IBeam stem = 1 solid column, serifs = 1 solid row each" : string.Join(", ", crispBad.Take(12)));
	}

	private static bool Near(uint a, uint b)
	{
		return Math.Abs((int)((a >> 16) & 0xFF) - (int)((b >> 16) & 0xFF)) <= 2
			&& Math.Abs((int)((a >> 8) & 0xFF) - (int)((b >> 8) & 0xFF)) <= 2
			&& Math.Abs((int)(a & 0xFF) - (int)(b & 0xFF)) <= 2;
	}

	// Median straight colour and alpha of the pixels whose centre lies between r0 and r1.
	private static (uint Rgb, int A) Plateau(byte[] px, int s, double r0, double r1)
	{
		List<int> rs = new List<int>();
		List<int> gs = new List<int>();
		List<int> bs = new List<int>();
		List<int> a = new List<int>();
		double c = s / 2.0;
		for (int y = 0; y < s; y++)
		{
			for (int x = 0; x < s; x++)
			{
				double r = Math.Sqrt((x + 0.5 - c) * (x + 0.5 - c) + (y + 0.5 - c) * (y + 0.5 - c));
				if (r < r0 || r > r1)
				{
					continue;
				}
				int k = (y * s + x) * 4;
				bs.Add(px[k]);
				gs.Add(px[k + 1]);
				rs.Add(px[k + 2]);
				a.Add(px[k + 3]);
			}
		}
		if (a.Count == 0)
		{
			return (0, 0);
		}
		static int Median(List<int> v)
		{
			v.Sort();
			return v[v.Count / 2];
		}
		return ((uint)((Median(rs) << 16) | (Median(gs) << 8) | Median(bs)), Median(a));
	}

	// Least-squares fit of the dot and ring edges against the analytic model of the design (coverage of a 1 px
	// ramp, the glow and shade under the ring), plus the alpha centroid.
	private static (double Dot, double In, double Out, double Cx, double Cy) Fit(byte[] px, int s, AlchemyPalette p, bool link)
	{
		double u = s / 32.0;
		double c = s / 2.0;
		int n = s * s;
		double[] r = new double[n];
		double[] a = new double[n];
		double sw = 0;
		double sx = 0;
		double sy = 0;
		for (int y = 0; y < s; y++)
		{
			for (int x = 0; x < s; x++)
			{
				int i = y * s + x;
				r[i] = Math.Sqrt((x + 0.5 - c) * (x + 0.5 - c) + (y + 0.5 - c) * (y + 0.5 - c));
				a[i] = px[i * 4 + 3] / 255.0;
				sw += a[i];
				sx += a[i] * (x + 0.5);
				sy += a[i] * (y + 0.5);
			}
		}
		double fill = p.LinkFillAlpha / 255.0;
		double na = p.NeutralAlpha / 255.0;
		double Model(double rr, double rd, double ri, double ro)
		{
			double dot = Cov(rr - rd);
			double ring = Cov(Math.Max(ri - rr, rr - ro));
			if (link)
			{
				return 1.0 - (1.0 - fill * Cov(rr - ri)) * (1.0 - dot) * (1.0 - 242.0 / 255.0 * ring);
			}
			double inside = Cov(rr - ri);
			double e = Math.Max(0.0, rr - 3.0 * u);
			double g = 0.54 * Math.Exp(-e * e / (2.0 * (1.95 * u) * (1.95 * u)));
			double t = Math.Clamp((rr - 2.6 * u) / (3.4 * u), 0.0, 1.0);
			double k = 0.145 * t * t * (3.0 - 2.0 * t);
			double layer = (g + k) * inside;
			return 1.0 - (1.0 - layer) * (1.0 - dot) * (1.0 - na * ring);
		}
		double d0 = (link ? SpecLRD : SpecRD) * u;
		double i0 = (link ? SpecLRIN : SpecRIN) * u;
		double o0 = (link ? SpecLROUT : SpecROUT) * u;
		double rd = d0;
		double ri = i0;
		double ro = o0;
		for (int round = 0; round < 3; round++)
		{
			rd = Best(d0, v => Sse(r, a, d0, rr => Model(rr, v, ri, ro)));
			ri = Best(i0, v => Sse(r, a, i0, rr => Model(rr, rd, v, ro)));
			ro = Best(o0, v => Sse(r, a, o0, rr => Model(rr, rd, ri, v)));
		}
		return (rd, ri, ro, sx / sw, sy / sw);
	}

	private static double Sse(double[] r, double[] a, double around, Func<double, double> model)
	{
		double e = 0;
		for (int i = 0; i < r.Length; i++)
		{
			if (Math.Abs(r[i] - around) > 2.0)
			{
				continue;
			}
			double d = model(r[i]) - a[i];
			e += d * d;
		}
		return e;
	}

	private static double Best(double start, Func<double, double> sse)
	{
		double best = start;
		double bestE = double.MaxValue;
		for (double v = start - 0.6; v <= start + 0.6; v += 0.005)
		{
			double e = sse(v);
			if (e < bestE)
			{
				bestE = e;
				best = v;
			}
		}
		return best;
	}

	private static double Cov(double d)
	{
		return Math.Clamp(0.5 - d, 0.0, 1.0);
	}

	// ===================================================================== source reference

	private static void SourceChecks(string outDir, string? refDir)
	{
		string dir = refDir ?? Path.Combine(Root, "assets", "alchemy", "cursors");
		Dictionary<string, string> found = new Dictionary<string, string>();
		if (Directory.Exists(dir))
		{
			foreach (string name in new string[4] { "alchemy-dark-normal.cur", "alchemy-dark-link.cur", "alchemy-light-normal.cur", "alchemy-light-link.cur" })
			{
				string? f = Directory.EnumerateFiles(dir, name, SearchOption.AllDirectories).FirstOrDefault();
				if (f != null)
				{
					found[name] = f;
				}
			}
		}
		if (found.Count < 4)
		{
			Check("source-reference-skipped", true, "no reference cursors in " + dir + " (the feature never needs them)");
			Metrics["SourceReference"] = "skipped";
			return;
		}
		List<string> rows = new List<string>();
		List<string> bad = new List<string>();
		List<(string Label, byte[][] Src, byte[][] Code)> pairs = new List<(string, byte[][], byte[][])>();
		foreach ((string name, string path) in found.OrderBy(kv => kv.Key))
		{
			bool link = name.Contains("-link");
			AlchemyPalette p = AlchemyPalettes.Find(name.Contains("-dark-") ? "dark" : "light")!;
			ParsedCur src = CursorFileFormat.ReadCur(File.ReadAllBytes(path));
			byte[][] srcFrames = new byte[5][];
			byte[][] codeFrames = new byte[5][];
			foreach (ParsedCurFrame f in src.Frames)
			{
				int i = Array.IndexOf(AlchemyCursorArt.Sizes, f.BiWidth);
				if (i < 0)
				{
					continue;
				}
				// The brand body (no halo) against the source's raw alpha; the shipped pointer (with its halo) against
				// the source as both look on the background the set is made for, where the halo does not show.
				byte[] body = AlchemyCursorArt.RenderBgra(link ? CursorRole.Hand : CursorRole.Arrow, p, f.BiWidth, 0, keyline: false);
				byte[] code = AlchemyCursorArt.RenderBgra(link ? CursorRole.Hand : CursorRole.Arrow, p, f.BiWidth);
				srcFrames[i] = f.Bgra;
				codeFrames[i] = code;
				(double da, double dp) = Compare(body, f.Bgra, f.BiWidth);
				bool darkTone = p.Tone == AlchemyPalettes.Dark;
				double dc = CompareOver(code, f.Bgra, f.BiWidth, darkTone ? Black : White);
				double dg = CompareOver(code, f.Bgra, f.BiWidth, darkTone ? BgDark : BgLight);
				string row = $"{name}@{f.BiWidth}: mean |dAlpha| {da:F2}/255, premultiplied RGB {dp:F2}/255, with halo on {(darkTone ? "black" : "white")} {dc:F2}/255 (on {(darkTone ? "#1A1A1A" : "#F0F0F0")} {dg:F2}), hotspot source ({f.HotX},{f.HotY})";
				rows.Add(row);
				if (f.BiWidth <= 64 && (da > (link ? 4.5 : 3.5) || dp > 3.0 || dc > 3.0))
				{
					bad.Add(row);
				}
				if (f.HotX != f.BiWidth / 2 || f.HotY != f.BiWidth / 2)
				{
					bad.Add(name + ": source hotspot is not the centre");
				}
			}
			pairs.Add((name.Replace(".cur", ""), srcFrames, codeFrames));
		}
		Metrics["SourceCompare"] = rows;
		Check("source-reference-match", bad.Count == 0, bad.Count == 0 ? "body: normal <= 3.5/255 and link <= 4.5/255 mean alpha, premultiplied RGB <= 3.0; with halo over black (dark) or white (light) <= 3.0; at 32/48/64" : string.Join(" ; ", bad));
		RenderCompare(Path.Combine(outDir, "compare-source.png"), pairs);
	}

	private static (double Alpha, double Rgb) Compare(byte[] a, byte[] b, int s)
	{
		double da = 0;
		double dp = 0;
		for (int i = 0; i < s * s; i++)
		{
			int k = i * 4;
			int aa = a[k + 3];
			int ab = b[k + 3];
			da += Math.Abs(aa - ab);
			for (int ch = 0; ch < 3; ch++)
			{
				dp += Math.Abs(a[k + ch] * aa / 255.0 - b[k + ch] * ab / 255.0);
			}
		}
		return (da / (s * s), dp / (s * s * 3));
	}

	// Mean |dRGB| (0..255) of two pointers composited over the same solid background.
	private static double CompareOver(byte[] a, byte[] b, int s, Color bg)
	{
		double[] ca = Composite(a, s, bg);
		double[] cb = Composite(b, s, bg);
		double sum = 0;
		for (int i = 0; i < ca.Length; i++)
		{
			sum += Math.Abs(ca[i] - cb[i]);
		}
		return sum * 255.0 / ca.Length;
	}

	// Straight BGRA over a solid colour: RGB triples 0..1, row-major.
	private static double[] Composite(byte[] px, int s, Color bg)
	{
		double[] o = new double[s * s * 3];
		for (int i = 0; i < s * s; i++)
		{
			int k = i * 4;
			double a = px[k + 3] / 255.0;
			o[i * 3] = (px[k + 2] * a + bg.R * (1.0 - a)) / 255.0;
			o[i * 3 + 1] = (px[k + 1] * a + bg.G * (1.0 - a)) / 255.0;
			o[i * 3 + 2] = (px[k] * a + bg.B * (1.0 - a)) / 255.0;
		}
		return o;
	}

	private static double Luminance(double r, double g, double b)
	{
		static double Lin(double c)
		{
			return c <= 0.04045 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
		}
		return 0.2126 * Lin(r) + 0.7152 * Lin(g) + 0.0722 * Lin(b);
	}

	private static double ContrastRatio(double l1, double l2)
	{
		return (Math.Max(l1, l2) + 0.05) / (Math.Min(l1, l2) + 0.05);
	}

	// Pixels of a pointer over bg whose colour reaches min:1 contrast against bg. With differFrom, only pixels that
	// also differ visibly (more than 8/255 in a channel) from that pointer over the same background count.
	private static int Visible(byte[] px, int s, Color bg, double min, byte[]? differFrom = null)
	{
		double[] c = Composite(px, s, bg);
		double[]? o = differFrom == null ? null : Composite(differFrom, s, bg);
		double lb = Luminance(bg.R / 255.0, bg.G / 255.0, bg.B / 255.0);
		int n = 0;
		for (int i = 0; i < s * s; i++)
		{
			int k = i * 3;
			if (o != null && Math.Max(Math.Abs(c[k] - o[k]), Math.Max(Math.Abs(c[k + 1] - o[k + 1]), Math.Abs(c[k + 2] - o[k + 2]))) * 255.0 <= 8.0)
			{
				continue;
			}
			if (ContrastRatio(Luminance(c[k], c[k + 1], c[k + 2]), lb) >= min)
			{
				n++;
			}
		}
		return n;
	}

	// ===================================================================== legibility

	private static readonly Color White = Color.FromRgb(0xFF, 0xFF, 0xFF);

	private static readonly Color Black = Color.FromRgb(0x00, 0x00, 0x00);

	private static void LegibilityChecks()
	{
		// The working pointer (shown right after a click) must not look like the normal one: at 32 px every frame has at
		// least 25 pixels that differ from the normal pointer and stand out 3:1 against the set's own background.
		List<string> spinBad = new List<string>();
		Dictionary<string, int> spinMin = new Dictionary<string, int>();
		foreach (AlchemyPalette p in AlchemyPalettes.All)
		{
			Color bg = p.Tone == AlchemyPalettes.Dark ? BgDark : BgLight;
			byte[] arrow = AlchemyCursorArt.RenderBgra(CursorRole.Arrow, p, 32);
			int min = int.MaxValue;
			for (int f = 0; f < AlchemyCursorArt.AnimFrames; f++)
			{
				int n = Visible(AlchemyCursorArt.RenderBgra(CursorRole.AppStarting, p, 32, f), 32, bg, 3.0, arrow);
				min = Math.Min(min, n);
			}
			spinMin[p.Id] = min;
			if (min < 25)
			{
				spinBad.Add($"{p.Id}: {min}");
			}
		}
		Metrics["WorkingDistinctPixelsMin32"] = spinMin;
		Check("working-stands-out-from-normal-at-32", spinBad.Count == 0, spinBad.Count == 0 ? "every frame, every set: >= 25 pixels differ from Normal at >= 3:1 on the set's background (min " + string.Join(", ", spinMin.Select(kv => kv.Key + "=" + kv.Value)) + ")" : string.Join(" ; ", spinBad));

		// Every pointer stays findable on white, black and mid grey: at 32 px at least 20 pixels reach 3:1 against each,
		// in every set (animated pointers: every frame).
		List<string> visBad = new List<string>();
		Dictionary<string, string> visMin = new Dictionary<string, string>();
		Color[] bgs = new Color[3] { White, Black, BgMid };
		foreach (AlchemyPalette p in AlchemyPalettes.All)
		{
			foreach (CursorRole role in AlchemyCursorArt.Roles)
			{
				int frames = AlchemyCursorArt.IsAnimated(role) ? AlchemyCursorArt.AnimFrames : 1;
				int[] min = new int[3] { int.MaxValue, int.MaxValue, int.MaxValue };
				for (int f = 0; f < frames; f++)
				{
					byte[] px = AlchemyCursorArt.RenderBgra(role, p, 32, f);
					for (int b = 0; b < 3; b++)
					{
						min[b] = Math.Min(min[b], Visible(px, 32, bgs[b], 3.0));
					}
				}
				visMin[p.Id + "/" + role] = $"white {min[0]}, black {min[1]}, grey {min[2]}";
				if (min.Any(m => m < 20))
				{
					visBad.Add($"{p.Id}/{role}: white {min[0]} black {min[1]} grey {min[2]}");
				}
			}
		}
		Metrics["Visible3to1PixelsMin32"] = visMin;
		Check("every-pointer-visible-on-white-black-grey-at-32", visBad.Count == 0, visBad.Count == 0 ? "17 pointers x 6 sets: >= 20 pixels at >= 3:1 on #FFFFFF, #000000 and #7F7F7F" : string.Join(" ; ", visBad.Take(16)));

		// The halo outline of the ring pointers vanishes on the background the set is made for (black for the dark
		// tone, white for the light tone): there the pointers look exactly like the brand cursors.
		List<string> keyBad = new List<string>();
		foreach (AlchemyPalette p in AlchemyPalettes.All)
		{
			Color bg = p.Tone == AlchemyPalettes.Dark ? Black : White;
			foreach (CursorRole role in AlchemyCursorArt.Roles)
			{
				foreach (int s in new int[2] { 32, 64 })
				{
					foreach (int f in AlchemyCursorArt.IsAnimated(role) ? new int[3] { 0, 6, 17 } : new int[1])
					{
						double[] with = Composite(AlchemyCursorArt.RenderBgra(role, p, s, f), s, bg);
						double[] without = Composite(AlchemyCursorArt.RenderBgra(role, p, s, f, keyline: false), s, bg);
						double max = 0;
						for (int i = 0; i < with.Length; i++)
						{
							max = Math.Max(max, Math.Abs(with[i] - without[i]) * 255.0);
						}
						if (max > 2.0)
						{
							keyBad.Add($"{p.Id}/{role}@{s}#{f}: {max:F1}");
						}
					}
				}
			}
		}
		Check("halo-invisible-on-own-background", keyBad.Count == 0, keyBad.Count == 0 ? "ring-pointer halos change nothing over black (dark sets) or white (light sets), within 2/255" : string.Join(" ; ", keyBad.Take(12)));
	}

	// ===================================================================== renders

	private static readonly Color BgDark = Color.FromRgb(0x1A, 0x1A, 0x1A);

	private static readonly Color BgLight = Color.FromRgb(0xF0, 0xF0, 0xF0);

	private static readonly Color BgMid = Color.FromRgb(0x7F, 0x7F, 0x7F);

	private static readonly string[] RoleLabels = new string[17]
	{
		"Normal (Arrow)", "Help", "Working (AppStarting)", "Busy (Wait)", "Precision (Crosshair)", "Text (IBeam)", "Handwriting (NWPen)", "Unavailable (No)",
		"Vertical (SizeNS)", "Horizontal (SizeWE)", "Diagonal 1 (SizeNWSE)", "Diagonal 2 (SizeNESW)", "Move (SizeAll)", "Alternate (UpArrow)", "Link (Hand)",
		"Location (Pin)", "Person"
	};

	private static void Renders(string outDir)
	{
		foreach (AlchemyPalette p in AlchemyPalettes.All)
		{
			RenderSheet(Path.Combine(outDir, $"sheet-{p.Id}.png"), p);
			RenderAnim(Path.Combine(outDir, $"anim-{p.Id}-working.png"), p, CursorRole.AppStarting);
			RenderAnim(Path.Combine(outDir, $"anim-{p.Id}-busy.png"), p, CursorRole.Wait);
		}
		RenderZoom(Path.Combine(outDir, "zoom-32.png"), 32, 5);
		RenderZoom(Path.Combine(outDir, "zoom-48.png"), 48, 4);
	}

	// 17 roles x 5 sizes on dark, light and mid grey, at 1:1.
	private static void RenderSheet(string path, AlchemyPalette p)
	{
		const int labelW = 190;
		const int headH = 34;
		const int rowH = 140;
		int blockW = AlchemyCursorArt.Sizes.Sum(s => s + 12) + 12;
		int w = labelW + 3 * blockW;
		int h = headH + 17 * rowH;
		DrawingVisual dv = new DrawingVisual();
		using (DrawingContext dc = dv.RenderOpen())
		{
			dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(0x30, 0x30, 0x30)), null, new Rect(0, 0, w, h));
			Label(dc, $"{p.Id}  (frame 6 for animated roles)", 8, 8, 14, Colors.White);
			Color[] bgs = new Color[3] { BgDark, BgLight, BgMid };
			for (int b = 0; b < 3; b++)
			{
				double x0 = labelW + b * blockW;
				dc.DrawRectangle(new SolidColorBrush(bgs[b]), null, new Rect(x0, headH, blockW - 4, 17 * rowH));
			}
			for (int ri = 0; ri < 17; ri++)
			{
				CursorRole role = AlchemyCursorArt.Roles[ri];
				double y = headH + ri * rowH;
				Label(dc, RoleLabels[ri], 8, y + 8, 13, Colors.White);
				for (int b = 0; b < 3; b++)
				{
					double x = labelW + b * blockW + 8;
					foreach (int s in AlchemyCursorArt.Sizes)
					{
						BitmapSource bmp = Bitmap(AlchemyCursorArt.RenderBgra(role, p, s, AlchemyCursorArt.IsAnimated(role) ? 6 : 0), s);
						dc.DrawImage(bmp, new Rect(Math.Round(x), Math.Round(y + (rowH - s) / 2.0), s, s));
						x += s + 12;
					}
				}
			}
		}
		SavePng(Render(dv, w, h), path);
	}

	// 24 frames at 64 px on dark and light.
	private static void RenderAnim(string path, AlchemyPalette p, CursorRole role)
	{
		const int cell = 76;
		int w = 24 * cell + 8;
		int h = 2 * cell + 34;
		DrawingVisual dv = new DrawingVisual();
		using (DrawingContext dc = dv.RenderOpen())
		{
			dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(0x30, 0x30, 0x30)), null, new Rect(0, 0, w, h));
			Label(dc, $"{p.Id} {(role == CursorRole.Wait ? "busy" : "working")}: 24 frames x {AlchemyCursorArt.AnimJiffies} jiffies", 8, 6, 13, Colors.White);
			Color[] bgs = new Color[2] { BgDark, BgLight };
			for (int b = 0; b < 2; b++)
			{
				dc.DrawRectangle(new SolidColorBrush(bgs[b]), null, new Rect(4, 30 + b * cell, w - 8, cell));
				for (int f = 0; f < 24; f++)
				{
					BitmapSource bmp = Bitmap(AlchemyCursorArt.RenderBgra(role, p, 64, f), 64);
					dc.DrawImage(bmp, new Rect(8 + f * cell + 6, 30 + b * cell + 6, 64, 64));
				}
			}
		}
		SavePng(Render(dv, w, h), path);
	}

	// One size of every role, enlarged with whole pixels, for checking crispness: rows = palettes on their own tone's
	// background and on the opposite one.
	private static void RenderZoom(string path, int size, int scale)
	{
		int cell = size * scale + 6;
		int rows = AlchemyPalettes.All.Length * 2;
		int w = 17 * cell + 6;
		int h = rows * cell + 6;
		byte[] buf = new byte[w * h * 4];
		Fill(buf, w, 0, 0, w, h, Color.FromRgb(0x30, 0x30, 0x30));
		int row = 0;
		foreach (AlchemyPalette p in AlchemyPalettes.All)
		{
			foreach (Color bg in p.Tone == AlchemyPalettes.Dark ? new Color[2] { BgDark, BgLight } : new Color[2] { BgLight, BgDark })
			{
				for (int ri = 0; ri < 17; ri++)
				{
					CursorRole role = AlchemyCursorArt.Roles[ri];
					int x0 = 6 + ri * cell;
					int y0 = 6 + row * cell;
					Fill(buf, w, x0, y0, size * scale, size * scale, bg);
					byte[] px = AlchemyCursorArt.RenderBgra(role, p, size, AlchemyCursorArt.IsAnimated(role) ? 6 : 0);
					(int hx, int hy) = AlchemyCursorArt.Hotspot(role, size);
					for (int y = 0; y < size; y++)
					{
						for (int x = 0; x < size; x++)
						{
							int k = (y * size + x) * 4;
							for (int dy = 0; dy < scale; dy++)
							{
								for (int dx = 0; dx < scale; dx++)
								{
									Blend(buf, w, x0 + x * scale + dx, y0 + y * scale + dy, px[k + 2], px[k + 1], px[k], px[k + 3]);
								}
							}
						}
					}
					// Hotspot marker: a 1 px magenta outline around the hotspot pixel.
					for (int t = 0; t < scale; t++)
					{
						Put(buf, w, x0 + hx * scale + t, y0 + hy * scale, 255, 0, 255);
						Put(buf, w, x0 + hx * scale + t, y0 + hy * scale + scale - 1, 255, 0, 255);
						Put(buf, w, x0 + hx * scale, y0 + hy * scale + t, 255, 0, 255);
						Put(buf, w, x0 + hx * scale + scale - 1, y0 + hy * scale + t, 255, 0, 255);
					}
				}
				row++;
			}
		}
		BitmapSource bmp = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, buf, w * 4);
		SavePng(bmp, path);
	}

	private static void RenderCompare(string path, List<(string Label, byte[][] Src, byte[][] Code)> pairs)
	{
		const int labelW = 200;
		int rowH = 140;
		int blockW = AlchemyCursorArt.Sizes.Sum(s => s + 12) + 12;
		int w = labelW + 2 * blockW;
		int h = 34 + pairs.Count * 2 * rowH;
		DrawingVisual dv = new DrawingVisual();
		using (DrawingContext dc = dv.RenderOpen())
		{
			dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(0x30, 0x30, 0x30)), null, new Rect(0, 0, w, h));
			Label(dc, "source .cur (upper row) vs code-drawn (lower row), on dark and light", 8, 8, 14, Colors.White);
			int row = 0;
			foreach ((string label, byte[][] src, byte[][] code) in pairs)
			{
				for (int k = 0; k < 2; k++)
				{
					double y = 34 + row * rowH;
					Label(dc, label + (k == 0 ? "  source" : "  code"), 8, y + 8, 13, Colors.White);
					for (int b = 0; b < 2; b++)
					{
						double x0 = labelW + b * blockW;
						dc.DrawRectangle(new SolidColorBrush(b == 0 ? BgDark : BgLight), null, new Rect(x0, y, blockW - 4, rowH - 2));
						double x = x0 + 8;
						for (int i = 0; i < 5; i++)
						{
							int s = AlchemyCursorArt.Sizes[i];
							byte[]? img = k == 0 ? src[i] : code[i];
							if (img != null)
							{
								dc.DrawImage(Bitmap(img, s), new Rect(Math.Round(x), Math.Round(y + (rowH - s) / 2.0), s, s));
							}
							x += s + 12;
						}
					}
					row++;
				}
			}
		}
		SavePng(Render(dv, w, h), path);
	}

	private static void Fill(byte[] buf, int w, int x0, int y0, int fw, int fh, Color c)
	{
		for (int y = y0; y < y0 + fh; y++)
		{
			for (int x = x0; x < x0 + fw; x++)
			{
				Put(buf, w, x, y, c.R, c.G, c.B);
			}
		}
	}

	private static void Put(byte[] buf, int w, int x, int y, byte r, byte g, byte b)
	{
		int k = (y * w + x) * 4;
		if (k < 0 || k + 3 >= buf.Length)
		{
			return;
		}
		buf[k] = b;
		buf[k + 1] = g;
		buf[k + 2] = r;
		buf[k + 3] = 255;
	}

	private static void Blend(byte[] buf, int w, int x, int y, byte r, byte g, byte b, byte a)
	{
		int k = (y * w + x) * 4;
		double t = a / 255.0;
		buf[k] = (byte)Math.Round(b * t + buf[k] * (1 - t));
		buf[k + 1] = (byte)Math.Round(g * t + buf[k + 1] * (1 - t));
		buf[k + 2] = (byte)Math.Round(r * t + buf[k + 2] * (1 - t));
		buf[k + 3] = 255;
	}

	// ===================================================================== PC Settings

	private static void PcSettingsRenders(string outDir, AppSettings real)
	{
		AppSettings auto = real.Clone();
		auto.AlchemyCursors = "auto";
		auto.UseWin81Cursors = false;
		SettingsStore.DiagnosticsOverride = auto;
		// The harness applies nothing, so the status shows what a user sees once the choice is on.
		PointerScheme.StatusOverrideForTest = new PointerStatus
		{
			AppliedKind = PointerKind.Alchemy,
			AppliedPalette = AlchemyPalettes.Resolve(auto.AlchemyCursors, ShellTheme.IsDark, auto.SeasonalActive, auto.AlchemyCursorsSeasonal),
			Mode = AlchemyPalettes.Auto
		};
		PcSettingsWindow pc = new PcSettingsWindow(null!)
		{
			Left = -6000.0,
			Top = -6000.0
		};
		try
		{
			pc.QaRenderPointers(Path.Combine(outDir, "pcsettings-pointers.png"));
			AppSettings season = real.Clone();
			season.AlchemyCursors = "dark";
			season.UseWin81Cursors = false;
			season.SeasonalActive = SeasonalTheme.Halloween;
			season.AlchemyCursorsSeasonal = true;
			SettingsStore.DiagnosticsOverride = season;
			PointerScheme.StatusOverrideForTest = new PointerStatus
			{
				AppliedKind = PointerKind.Alchemy,
				AppliedPalette = AlchemyPalettes.Resolve(season.AlchemyCursors, ShellTheme.IsDark, season.SeasonalActive, season.AlchemyCursorsSeasonal),
				Mode = AlchemyPalettes.Dark
			};
			pc.QaRenderSeasonalPointers(Path.Combine(outDir, "pcsettings-seasonal-pointers.png"));
			Check("pcsettings-renders", true, "Appearance > Pointers (auto) and the seasonal section (Halloween)");
		}
		catch (Exception ex)
		{
			Check("pcsettings-renders", false, ex.ToString());
		}
		finally
		{
			PointerScheme.StatusOverrideForTest = null;
			try
			{
				pc.Close();
			}
			catch
			{
			}
		}
	}

	// ===================================================================== engine (fake registry)

	private sealed class SimulatedCrash : Exception
	{
	}

	private sealed class FakePointerWorld : IPointerWorld
	{
		public const string FakeRoot = "X:\\Data\\Win81Layer\\cursors\\alchemy";

		public const string FakeWin81Assets = "X:\\Data\\Win81Layer\\assets\\win81\\cursors";

		public Dictionary<string, RegValue> Reg = new Dictionary<string, RegValue>(StringComparer.OrdinalIgnoreCase);

		public Dictionary<string, RegValue> Schemes = new Dictionary<string, RegValue>(StringComparer.OrdinalIgnoreCase);

		public string? StateJson;

		public bool StateLocked;

		public bool SavesFail;

		public Dictionary<string, string>? Legacy;

		public bool LegacyLocked;

		public bool FailGeneration;

		public bool RefreshFails;

		public bool LegacyDeletedBeforeCommit;

		public readonly HashSet<string> Drawn = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

		public readonly List<string> Cleanups = new List<string>();

		public readonly List<string> OpLog = new List<string>();

		public int RegistryWrites;

		public int Refreshes;

		public int StateSaves;

		public int Ops;

		public int CrashAt;

		public bool CrashAfter;

		private DateTime _now = new DateTime(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc);

		public string AlchemyRoot => FakeRoot;

		public string Win81AssetsRoot => FakeWin81Assets;

		public DateTime UtcNow
		{
			get
			{
				_now = _now.AddSeconds(1);
				return _now;
			}
		}

		private void Step(string what, Action apply)
		{
			Ops++;
			OpLog.Add(what);
			bool hit = CrashAt > 0 && Ops == CrashAt;
			if (hit && !CrashAfter)
			{
				throw new SimulatedCrash();
			}
			apply();
			if (hit && CrashAfter)
			{
				throw new SimulatedCrash();
			}
		}

		public Dictionary<string, RegValue> ReadRegistry()
		{
			return Reg.ToDictionary(kv => kv.Key, kv => kv.Value.Clone(), StringComparer.OrdinalIgnoreCase);
		}

		public RegValue? ReadSchemeEntry(string name)
		{
			return Schemes.TryGetValue(name, out RegValue? v) ? v.Clone() : null;
		}

		public void WriteValues(IReadOnlyList<(string Name, RegValue? Value)> values)
		{
			foreach ((string name, RegValue? value) in values)
			{
				Step("reg:" + name, delegate
				{
					RegistryWrites++;
					Reg.Remove(name);
					if (value != null)
					{
						Reg[value.Name] = value.Clone();
					}
				});
			}
		}

		public void WriteSchemeEntry(string name, RegValue? value)
		{
			Step("scheme", delegate
			{
				RegistryWrites++;
				Schemes.Remove(name);
				if (value != null)
				{
					RegValue v = value.Clone();
					v.Name = name;
					Schemes[name] = v;
				}
			});
		}

		public bool RefreshSystemCursors()
		{
			Step("refresh", delegate { Refreshes++; });
			return !RefreshFails;
		}

		public PointerStateFile LoadState()
		{
			if (StateLocked)
			{
				throw new PointerReadException("locked");
			}
			return StateJson == null ? new PointerStateFile() : JsonSerializer.Deserialize<PointerStateFile>(StateJson, PointerScheme.Json)!;
		}

		public PointerStateFile State => LoadState();

		public bool SaveState(PointerStateFile state)
		{
			bool ok = true;
			Step("save:" + state.Phase, delegate
			{
				StateSaves++;
				if (SavesFail)
				{
					ok = false;
					return;
				}
				StateJson = JsonSerializer.Serialize(state, PointerScheme.Json);
			});
			return ok;
		}

		public Dictionary<string, string>? LegacySnapshot()
		{
			if (LegacyLocked)
			{
				throw new PointerReadException("legacy locked");
			}
			return Legacy == null ? null : new Dictionary<string, string>(Legacy);
		}

		public void DeleteLegacySnapshot()
		{
			if (Legacy == null)
			{
				return;
			}
			PointerStateFile st = LoadState();
			if (st.Phase != PointerScheme.PhaseIdle)
			{
				LegacyDeletedBeforeCommit = true;
			}
			Step("legacy-delete", delegate { Legacy = null; });
		}

		public Dictionary<string, RegValue> SystemDefault()
		{
			Dictionary<string, RegValue> d = new Dictionary<string, RegValue>(StringComparer.OrdinalIgnoreCase);
			foreach (string role in PointerScheme.RoleNames)
			{
				string name = role == "Pin" ? "pin" : (role == "Person" ? "person" : role);
				string file = role is "Crosshair" or "IBeam" ? "" : "%SystemRoot%\\cursors\\aero_" + role.ToLowerInvariant() + ".cur";
				d[name] = new RegValue(name, RegistryValueKind.ExpandString, file);
			}
			d[""] = new RegValue("", RegistryValueKind.String, "@main.cpl,-1020");
			d["Scheme Source"] = new RegValue("Scheme Source", RegistryValueKind.DWord, "2");
			return d;
		}

		public IReadOnlyDictionary<CursorRole, string> ResolveWin81()
		{
			Dictionary<CursorRole, string> d = new Dictionary<CursorRole, string>();
			foreach (CursorRole r in AlchemyCursorArt.Roles)
			{
				d[r] = r is CursorRole.Crosshair or CursorRole.IBeam ? "" : "C:\\Windows\\Cursors\\Windows 8.1\\aero_" + r.ToString().ToLowerInvariant() + ".cur";
			}
			return d;
		}

		public AlchemyPaletteFiles? EnsurePalette(string paletteId)
		{
			if (FailGeneration || AlchemyPalettes.Find(paletteId) == null)
			{
				return null;
			}
			Dictionary<CursorRole, string> d = new Dictionary<CursorRole, string>();
			foreach ((CursorRole role, string name) in AlchemyCursorSet.Files)
			{
				d[role] = FakeRoot + "\\v1\\" + paletteId + "\\" + name;
			}
			return new AlchemyPaletteFiles { Paths = d, Generated = Drawn.Add(paletteId) };
		}

		public void CleanupPalettes(IReadOnlyCollection<string> keep)
		{
			Step("cleanup", delegate { Cleanups.Add(string.Join(",", keep)); });
		}

		public void ResetCounters()
		{
			RegistryWrites = 0;
			Refreshes = 0;
			StateSaves = 0;
			Ops = 0;
			OpLog.Clear();
		}
	}

	// The user's own pointers in the fake: mixed kinds, an absent role, a lower-case name, size and user schemes.
	private static FakePointerWorld NewWorld()
	{
		FakePointerWorld w = new FakePointerWorld();
		void Set(string name, RegistryValueKind kind, string data)
		{
			w.Reg[name] = new RegValue(name, kind, data);
		}
		Set("Arrow", RegistryValueKind.ExpandString, "%SystemRoot%\\cursors\\aero_arrow.cur");
		Set("Help", RegistryValueKind.String, "C:\\Windows\\cursors\\aero_helpsel.cur");
		Set("AppStarting", RegistryValueKind.ExpandString, "%SystemRoot%\\cursors\\aero_working.ani");
		Set("Wait", RegistryValueKind.ExpandString, "%SystemRoot%\\cursors\\aero_busy.ani");
		Set("Crosshair", RegistryValueKind.ExpandString, "");
		Set("IBeam", RegistryValueKind.String, "");
		Set("NWPen", RegistryValueKind.ExpandString, "%SystemRoot%\\cursors\\aero_pen.cur");
		Set("No", RegistryValueKind.ExpandString, "%SystemRoot%\\cursors\\aero_unavail.cur");
		Set("SizeNS", RegistryValueKind.ExpandString, "%SystemRoot%\\cursors\\aero_ns.cur");
		Set("SizeWE", RegistryValueKind.String, "C:\\Windows\\cursors\\aero_ew.cur");
		Set("SizeNWSE", RegistryValueKind.ExpandString, "%SystemRoot%\\cursors\\aero_nwse.cur");
		Set("SizeNESW", RegistryValueKind.ExpandString, "%SystemRoot%\\cursors\\aero_nesw.cur");
		Set("SizeAll", RegistryValueKind.ExpandString, "%SystemRoot%\\cursors\\aero_move.cur");
		Set("UpArrow", RegistryValueKind.String, "C:\\Windows\\cursors\\aero_up.cur");
		Set("Hand", RegistryValueKind.ExpandString, "%SystemRoot%\\cursors\\aero_link.cur");
		Set("pin", RegistryValueKind.ExpandString, "%SystemRoot%\\cursors\\aero_pin.cur");
		Set("", RegistryValueKind.String, "Windows Default");
		Set("Scheme Source", RegistryValueKind.DWord, "2");
		Set("CursorBaseSize", RegistryValueKind.DWord, "48");
		Set("ContactVisualization", RegistryValueKind.DWord, "1");
		Set("GestureVisualization", RegistryValueKind.DWord, "31");
		w.Schemes["Alchemy (Dark)"] = new RegValue("Alchemy (Dark)", RegistryValueKind.ExpandString, "%SystemRoot%\\Cursors\\Alchemy\\alchemy-dark-normal.cur,,,,,,,,,,,,,,%SystemRoot%\\Cursors\\Alchemy\\alchemy-dark-link.cur,,");
		w.Schemes["Alchemy Mono"] = new RegValue("Alchemy Mono", RegistryValueKind.ExpandString, "C:\\Windows\\Cursors\\Alchemy Mono\\arrow.cur");
		return w;
	}

	// A world where an older build had its Windows 8.1 pointers on and kept cursor-snapshot.json.
	private static FakePointerWorld LegacyWorld()
	{
		FakePointerWorld w = NewWorld();
		foreach ((CursorRole role, string path) in w.ResolveWin81())
		{
			w.Reg.Remove(role.ToString());
			w.Reg[role.ToString()] = new RegValue(role.ToString(), RegistryValueKind.ExpandString, path);
		}
		w.Reg[""] = new RegValue("", RegistryValueKind.String, CursorScheme.SchemeName);
		w.Reg["Scheme Source"] = new RegValue("Scheme Source", RegistryValueKind.DWord, "1");
		w.Legacy = new Dictionary<string, string> { ["(Default)"] = "Alchemy's mode" };
		foreach (string role in PointerScheme.RoleNames)
		{
			w.Legacy[role] = role is "UpArrow" or "Pin" or "Person" ? "C:\\WINDOWS\\cursors\\aero_" + role.ToLowerInvariant() + ".cur" : "C:\\WINDOWS\\Cursors\\alchemy-" + role.ToLowerInvariant() + ".cur";
		}
		return w;
	}

	private static string Sig(Dictionary<string, RegValue> d)
	{
		return string.Join("|", d.Values.OrderBy(v => v.Name, StringComparer.Ordinal).Select(v => v.Name + "=" + v.Kind + ":" + v.Data));
	}

	private static string OriginalSig(PointerOriginal? o)
	{
		if (o == null)
		{
			return "(none)";
		}
		return o.Source + "|" + string.Join("|", o.Values.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => kv.Key + "=" + (kv.Value?.ToString() ?? "-")))
			+ "|" + string.Join("|", o.Schemes.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => kv.Key + "=" + (kv.Value?.ToString() ?? "-")));
	}

	private static PointerTarget T(int choice, bool isDark = true, string season = "", bool seasonalOn = true)
	{
		AppSettings s = new AppSettings { SeasonalActive = season, AlchemyCursorsSeasonal = seasonalOn };
		PointerScheme.SetChoice(s, choice);
		return PointerScheme.Effective(s, isDark);
	}

	private const int Off = 0;

	private const int Win81 = 1;

	private const int Dark = 2;

	private const int Light = 3;

	private const int Auto = 4;

	private static string DarkPath(string palette, string file)
	{
		return FakePointerWorld.FakeRoot + "\\v1\\" + palette + "\\" + file;
	}

	private static void EngineChecks()
	{
		// a. Off -> Dark -> Off
		{
			FakePointerWorld w = NewWorld();
			string reg0 = Sig(w.Reg);
			string sch0 = Sig(w.Schemes);
			PointerEngine e = new PointerEngine(w);
			PointerResult on = e.Apply(T(Dark));
			RegValue? arrow = w.Reg.GetValueOrDefault("Arrow");
			RegValue? scheme = w.Schemes.GetValueOrDefault(PointerScheme.AlchemySchemeName);
			bool applied = on.Success && on.Changed
				&& arrow?.Kind == "ExpandString" && arrow.Data == DarkPath("dark", "arrow.cur")
				&& w.Reg[""].Data == PointerScheme.AlchemySchemeName && w.Reg[""].Kind == "String"
				&& w.Reg["Scheme Source"].Data == "1" && w.Reg["Scheme Source"].Kind == "DWord"
				&& w.Reg["Person"].Data == DarkPath("dark", "person.cur")
				&& w.Reg["CursorBaseSize"].Data == "48"
				&& scheme != null && scheme.Data.Split(',').Length == 17 && scheme.Data.Split(',')[2] == DarkPath("dark", "working.ani")
				&& w.Schemes.ContainsKey("Alchemy (Dark)") && w.Schemes.ContainsKey("Alchemy Mono")
				&& w.Refreshes == 1 && w.State.Phase == PointerScheme.PhaseIdle && w.State.Original?.Source == "captured";
			PointerResult off = e.Apply(PointerTarget.Windows);
			bool restored = off.Success && Sig(w.Reg) == reg0 && Sig(w.Schemes) == sch0 && w.Reg.Keys.Contains("pin", StringComparer.Ordinal)
				&& !w.Reg.ContainsKey("Person") && w.State.Original == null && w.State.Applied == null && w.State.Phase == PointerScheme.PhaseIdle
				&& w.Cleanups.LastOrDefault() == "";
			Check("engine-a-off-dark-off-exact", applied && restored, $"applied={applied} restored={restored}; {on.Message} / {off.Message}; reg={Sig(w.Reg)}");
		}

		// b. Off -> Win81 -> Dark -> Light -> Auto(dark) -> theme flip -> Auto(light) -> Win81 -> Off
		{
			FakePointerWorld w = NewWorld();
			string reg0 = Sig(w.Reg);
			string sch0 = Sig(w.Schemes);
			PointerEngine e = new PointerEngine(w);
			List<string> steps = new List<string>();
			string? first = null;
			DateTime captured = default;
			bool once = true;
			(string Name, PointerTarget Target, string Arrow)[] seq = new (string, PointerTarget, string)[6]
			{
				("win81", T(Win81), "C:\\Windows\\Cursors\\Windows 8.1\\aero_arrow.cur"),
				("dark", T(Dark), DarkPath("dark", "arrow.cur")),
				("light", T(Light), DarkPath("light", "arrow.cur")),
				("auto-dark", T(Auto, isDark: true), DarkPath("dark", "arrow.cur")),
				("auto-light", T(Auto, isDark: false), DarkPath("light", "arrow.cur")),
				("win81-again", T(Win81), "C:\\Windows\\Cursors\\Windows 8.1\\aero_arrow.cur")
			};
			foreach ((string name, PointerTarget target, string arrow) in seq)
			{
				PointerResult r = e.Apply(target);
				PointerStateFile st = w.State;
				string sig = OriginalSig(st.Original);
				first ??= sig;
				if (captured == default)
				{
					captured = st.Original?.CapturedUtc ?? default;
				}
				bool ok = r.Success && w.Reg["Arrow"].Data == arrow && sig == first && st.Original?.CapturedUtc == captured;
				once &= ok;
				steps.Add($"{name}:{(ok ? "ok" : "FAIL " + r.Message + " arrow=" + w.Reg["Arrow"].Data)}");
				if (name == "auto-light")
				{
					once &= w.Cleanups.LastOrDefault() == "light,dark";
					steps.Add("keep=" + w.Cleanups.LastOrDefault());
				}
				if (name == "win81-again")
				{
					once &= !w.Schemes.ContainsKey(PointerScheme.AlchemySchemeName) && w.Reg[""].Data == CursorScheme.SchemeName;
				}
			}
			PointerResult off = e.Apply(PointerTarget.Windows);
			bool exact = off.Success && Sig(w.Reg) == reg0 && Sig(w.Schemes) == sch0;
			Check("engine-b-original-captured-once", once && exact, string.Join(" ", steps) + $" final-exact={exact}");
		}

		// c. Seasonal colours follow the season and the toggle; tone follows light/dark for auto.
		{
			List<string> bad = new List<string>();
			void Want(string label, PointerTarget t, string palette)
			{
				if (t.Kind != PointerKind.Alchemy || t.Palette != palette)
				{
					bad.Add($"{label}: {t.Kind}/{t.Palette} want {palette}");
				}
			}
			Want("dark+halloween", T(Dark, true, "halloween"), "halloween-dark");
			Want("dark+christmas", T(Dark, true, "christmas"), "christmas-dark");
			Want("dark+normal", T(Dark, true, ""), "dark");
			Want("dark+halloween+toggle-off", T(Dark, true, "halloween", seasonalOn: false), "dark");
			Want("auto+light-mode+christmas", T(Auto, false, "christmas"), "christmas-light");
			Want("light+christmas (dark mode)", T(Light, true, "christmas"), "christmas-light");
			Want("auto+dark-mode+halloween", T(Auto, true, "HALLOWEEN"), "halloween-dark");
			if (T(Win81, true, "halloween").Kind != PointerKind.Win81 || T(Off, true, "christmas").Kind != PointerKind.Windows)
			{
				bad.Add("non-Alchemy choices ignore the season");
			}
			AppSettings both = new AppSettings { UseWin81Cursors = true, AlchemyCursors = "light" };
			if (PointerScheme.Effective(both, true).Kind != PointerKind.Alchemy)
			{
				bad.Add("Alchemy wins over the Windows 8.1 flag");
			}
			FakePointerWorld w = NewWorld();
			PointerEngine e = new PointerEngine(w);
			e.Apply(T(Dark, true, "halloween"));
			string orig = OriginalSig(w.State.Original);
			bool hall = w.Reg["Arrow"].Data == DarkPath("halloween-dark", "arrow.cur");
			e.Apply(T(Dark, true, ""));
			bool back = w.Reg["Arrow"].Data == DarkPath("dark", "arrow.cur") && OriginalSig(w.State.Original) == orig && w.Cleanups.LastOrDefault() == "dark";
			if (!hall || !back)
			{
				bad.Add($"engine season switch hall={hall} back={back}");
			}
			Check("engine-c-seasonal-palettes", bad.Count == 0, bad.Count == 0 ? "halloween-dark, christmas-dark, dark, dark (toggle off), christmas-light, normal brings blue back" : string.Join(" ; ", bad));
		}

		// d. Older build's snapshot with its Windows 8.1 pointers on.
		{
			FakePointerWorld w = LegacyWorld();
			string schemes0 = Sig(w.Schemes);
			PointerEngine e = new PointerEngine(w);
			PointerResult r = e.Apply(T(Win81));
			bool imported = r.Success && w.State.Original?.Source == "legacy-snapshot" && w.Legacy == null && !w.LegacyDeletedBeforeCommit
				&& w.State.Original.Values["Arrow"]?.Data == "C:\\WINDOWS\\Cursors\\alchemy-arrow.cur";
			PointerResult off = e.Apply(PointerTarget.Windows);
			bool restored = off.Success && w.Reg["Arrow"].Data == "C:\\WINDOWS\\Cursors\\alchemy-arrow.cur" && w.Reg["Arrow"].Kind == "ExpandString"
				&& w.Reg[""].Data == "Alchemy's mode" && w.Reg[""].Kind == "String" && w.Reg["Scheme Source"].Data == "1"
				&& w.Reg["Person"].Data == "C:\\WINDOWS\\cursors\\aero_person.cur" && Sig(w.Schemes) == schemes0 && w.Reg["CursorBaseSize"].Data == "48";
			// A crash between the commit and the delete keeps the snapshot; the next run retires it.
			List<string> crash = new List<string>();
			for (int n = 1; n < 80; n++)
			{
				FakePointerWorld c = LegacyWorld();
				c.CrashAt = n;
				bool crashed = false;
				try
				{
					new PointerEngine(c).Apply(T(Win81));
				}
				catch (SimulatedCrash)
				{
					crashed = true;
				}
				if (!crashed)
				{
					break;
				}
				string at = c.OpLog.Last();
				bool committed = c.StateJson != null && c.State.Phase == PointerScheme.PhaseIdle && c.State.Original != null;
				if (!committed && c.Legacy == null)
				{
					crash.Add($"#{n} {at}: snapshot deleted before the commit");
				}
				c.CrashAt = 0;
				new PointerEngine(c).Recover(() => T(Win81));
				PointerResult rr = new PointerEngine(c).Apply(T(Win81));
				if (!rr.Success || c.Legacy != null || c.State.Original?.Source != "legacy-snapshot" || c.LegacyDeletedBeforeCommit)
				{
					crash.Add($"#{n} {at}: after recovery legacy={(c.Legacy != null)} source={c.State.Original?.Source}");
				}
			}
			Check("engine-d-legacy-snapshot-import", imported && restored && crash.Count == 0, $"imported={imported} restored={restored} {string.Join(" ; ", crash)}");
		}

		// e. Drift: the user changes a pointer outside the launcher while Alchemy is on.
		{
			FakePointerWorld w = NewWorld();
			string reg0 = Sig(w.Reg);
			PointerEngine e = new PointerEngine(w);
			e.Apply(T(Dark));
			w.Reg["Arrow"] = new RegValue("Arrow", RegistryValueKind.ExpandString, "D:\\Pointers\\own-arrow.cur");
			PointerResult r = e.Apply(T(Light));
			bool merged = r.Success && w.State.Original?.Values["Arrow"]?.Data == "D:\\Pointers\\own-arrow.cur" && w.State.DriftMergedUtc != default
				&& w.Reg["Arrow"].Data == DarkPath("light", "arrow.cur");
			// A launcher pointer put back by hand is never taken as the user's own.
			w.Reg["Help"] = new RegValue("Help", RegistryValueKind.ExpandString, DarkPath("dark", "help.cur"));
			e.Apply(T(Dark));
			bool notOurs = w.State.Original?.Values["Help"]?.Data == "C:\\Windows\\cursors\\aero_helpsel.cur";
			e.Apply(PointerTarget.Windows);
			Dictionary<string, RegValue> expect = NewWorld().Reg;
			expect["Arrow"] = new RegValue("Arrow", RegistryValueKind.ExpandString, "D:\\Pointers\\own-arrow.cur");
			bool restored = Sig(w.Reg) == Sig(expect);
			Check("engine-e-drift-merged", merged && notOurs && restored && Sig(w.Reg) != reg0, $"merged={merged} launcherPathIgnored={notOurs} restored={restored}");
		}

		// f. The restore point is lost while Alchemy is on: the system default is used, never launcher paths.
		{
			FakePointerWorld w = NewWorld();
			new PointerEngine(w).Apply(T(Dark));
			w.StateJson = null;
			PointerResult off = new PointerEngine(w).Apply(PointerTarget.Windows);
			Dictionary<string, RegValue> sd = w.SystemDefault();
			bool noLauncher = !w.Reg.Values.Any(v => v.Data.Contains(FakePointerWorld.FakeRoot, StringComparison.OrdinalIgnoreCase));
			bool isDefault = off.Success && noLauncher && w.Reg["Arrow"].Data == sd["Arrow"].Data && w.Reg[""].Data == "@main.cpl,-1020" && w.Reg["Scheme Source"].Data == "2"
				&& w.Reg.Keys.Contains("pin", StringComparer.Ordinal) && !w.Schemes.ContainsKey(PointerScheme.AlchemySchemeName) && w.Reg["CursorBaseSize"].Data == "48";
			FakePointerWorld w2 = NewWorld();
			new PointerEngine(w2).Apply(T(Dark));
			w2.StateJson = null;
			new PointerEngine(w2).Apply(T(Light));
			PointerOriginal? o = w2.State.Original;
			bool fallback = o?.Source == "system-default-fallback" && !o.Values.Values.Any(v => v != null && v.Data.Contains(FakePointerWorld.FakeRoot, StringComparison.OrdinalIgnoreCase));
			Check("engine-f-lost-state-uses-system-default", isDefault && fallback, $"restore={isDefault} fallbackOriginal={fallback} {off.Message}");
		}

		// g. Unreadable restore point and failed drawing change nothing.
		{
			FakePointerWorld w = NewWorld();
			string reg0 = Sig(w.Reg);
			w.StateLocked = true;
			PointerResult locked = new PointerEngine(w).Apply(T(Dark));
			bool l = !locked.Success && w.RegistryWrites == 0 && w.Refreshes == 0 && w.StateSaves == 0 && Sig(w.Reg) == reg0;
			w.StateLocked = false;
			w.LegacyLocked = true;
			w.Legacy = new Dictionary<string, string> { ["Arrow"] = "x" };
			PointerResult legacyLocked = new PointerEngine(w).Apply(T(Dark));
			bool ll = !legacyLocked.Success && w.RegistryWrites == 0 && w.StateSaves == 0;
			FakePointerWorld g = NewWorld();
			g.FailGeneration = true;
			PointerResult gen = new PointerEngine(g).Apply(T(Dark));
			bool gf = !gen.Success && gen.Message == "Could not create the Alchemy pointers; nothing was changed." && g.RegistryWrites == 0 && g.Refreshes == 0 && g.StateSaves == 0;
			FakePointerWorld s = NewWorld();
			s.SavesFail = true;
			PointerResult save = new PointerEngine(s).Apply(T(Dark));
			bool sf = !save.Success && s.RegistryWrites == 0 && s.Refreshes == 0 && Sig(s.Reg) == reg0;
			Check("engine-g-refusals-change-nothing", l && ll && gf && sf, $"locked={l} legacyLocked={ll} generation={gf} saveFailed={sf}");
		}

		// h. A crash at every write, then a new start finishes the change.
		CrashMatrix("off-to-dark", NewWorld, null, T(Dark));
		CrashMatrix("dark-to-light", NewWorld, T(Dark), T(Light));
		CrashMatrix("win81-to-dark", NewWorld, T(Win81), T(Dark));
		CrashMatrix("dark-to-off", NewWorld, T(Dark), PointerTarget.Windows);
		CrashMatrix("auto-flip", NewWorld, T(Auto, true), T(Auto, false));
		CrashMatrix("legacy-to-off", LegacyWorld, null, PointerTarget.Windows);
		CrashMatrix("legacy-to-dark", LegacyWorld, null, T(Dark));

		// i. Applying what is already on writes nothing.
		{
			FakePointerWorld w = NewWorld();
			PointerEngine e = new PointerEngine(w);
			e.Apply(T(Dark));
			w.ResetCounters();
			PointerResult again = e.Apply(T(Dark));
			bool dark = again.Success && !again.Changed && w.Ops == 0 && w.RegistryWrites == 0 && w.Refreshes == 0;
			e.Apply(T(Win81));
			w.ResetCounters();
			PointerResult again81 = e.Apply(T(Win81));
			bool win81 = again81.Success && !again81.Changed && w.Ops == 0;
			FakePointerWorld f = NewWorld();
			PointerResult none = new PointerEngine(f).Apply(PointerTarget.Windows);
			bool off = none.Success && !none.Changed && f.Ops == 0;
			Check("engine-i-idempotent", dark && win81 && off, $"dark={dark} win81={win81} off={off}");
		}

		// k. Windows refuses to load the pointers: the change is not saved as applied and the next run finishes it; a
		// restore is complete in the registry and is committed.
		{
			FakePointerWorld w = NewWorld();
			string reg0 = Sig(w.Reg);
			w.RefreshFails = true;
			PointerResult r = new PointerEngine(w).Apply(T(Dark));
			bool pending = !r.Success && w.State.Phase == PointerScheme.PhaseApplying && w.State.Applied == null && w.Reg["Arrow"].Data == DarkPath("dark", "arrow.cur");
			w.RefreshFails = false;
			PointerResult rec = new PointerEngine(w).Recover(() => T(Dark));
			bool finished = rec.Success && w.State.Phase == PointerScheme.PhaseIdle && w.State.Applied?.Palette == "dark";
			w.RefreshFails = true;
			PointerResult off = new PointerEngine(w).Apply(PointerTarget.Windows);
			bool restored = off.Success && Sig(w.Reg) == reg0 && w.State.Phase == PointerScheme.PhaseIdle && w.State.Original == null;
			Check("engine-k-refresh-refused", pending && finished && restored, $"pending={pending} finished={finished} restoreCommitted={restored} ({r.Message} / {off.Message})");
		}

		// l. The restore point is lost while the Windows 8.1 set is on: it is never taken as the user's own.
		{
			List<string> bad = new List<string>();
			FakePointerWorld w = NewWorld();
			new PointerEngine(w).Apply(T(Win81));
			w.StateJson = null;
			new PointerEngine(w).Apply(T(Dark));
			PointerOriginal? o = w.State.Original;
			if (o?.Source != "system-default-fallback" || o.Values.Values.Any(v => v != null && (v.Data.Contains("Windows 8.1", StringComparison.OrdinalIgnoreCase) || v.Data == CursorScheme.SchemeName)))
			{
				bad.Add("win81 set: source=" + o?.Source);
			}
			// The launcher's own copy of the Windows 8.1 files, under any scheme name.
			FakePointerWorld a = NewWorld();
			a.Reg["Arrow"] = new RegValue("Arrow", RegistryValueKind.ExpandString, FakePointerWorld.FakeWin81Assets + "\\aero_arrow.cur");
			new PointerEngine(a).Apply(T(Dark));
			if (a.State.Original?.Source != "system-default-fallback")
			{
				bad.Add("assets path: source=" + a.State.Original?.Source);
			}
			// Drift: the Windows 8.1 set written back over Alchemy, or one launcher Windows 8.1 file, is not the user's.
			FakePointerWorld m = NewWorld();
			PointerEngine e = new PointerEngine(m);
			e.Apply(T(Dark));
			string orig = OriginalSig(m.State.Original);
			foreach ((CursorRole role, string path) in m.ResolveWin81())
			{
				m.Reg.Remove(role.ToString());
				m.Reg[role.ToString()] = new RegValue(role.ToString(), RegistryValueKind.ExpandString, path);
			}
			m.Reg[""] = new RegValue("", RegistryValueKind.String, CursorScheme.SchemeName);
			e.Apply(T(Light));
			m.Reg["Help"] = new RegValue("Help", RegistryValueKind.ExpandString, FakePointerWorld.FakeWin81Assets + "\\aero_helpsel.cur");
			e.Apply(T(Dark));
			if (OriginalSig(m.State.Original) != orig || m.State.DriftMergedUtc != default)
			{
				bad.Add("drift merged launcher Windows 8.1 values");
			}
			Check("engine-l-win81-never-the-users-own", bad.Count == 0, bad.Count == 0 ? "lost state with Windows 8.1 on -> system default; launcher Windows 8.1 files never merged as drift" : string.Join(" ; ", bad));
		}
	}

	// Runs the transition once cleanly for the expected result, then again with a crash at each mutating step (before
	// and after the step lands), then a fresh engine recovers and applies the target.
	private static void CrashMatrix(string name, Func<FakePointerWorld> make, PointerTarget? from, PointerTarget to)
	{
		FakePointerWorld clean = make();
		if (from != null)
		{
			new PointerEngine(clean).Apply(from);
		}
		clean.ResetCounters();
		PointerResult cr = new PointerEngine(clean).Apply(to);
		int steps = clean.Ops;
		string wantReg = Sig(clean.Reg);
		string wantSchemes = Sig(clean.Schemes);
		string wantOrig = OriginalSig(clean.State.Original);
		List<string> bad = new List<string>();
		int cases = 0;
		foreach (bool after in new bool[2] { false, true })
		{
			for (int n = 1; n <= steps; n++)
			{
				FakePointerWorld w = make();
				if (from != null)
				{
					new PointerEngine(w).Apply(from);
				}
				w.ResetCounters();
				w.CrashAt = n;
				w.CrashAfter = after;
				string at = "?";
				try
				{
					new PointerEngine(w).Apply(to);
					bad.Add($"#{n}: no crash");
					continue;
				}
				catch (SimulatedCrash)
				{
					at = w.OpLog.Last();
				}
				cases++;
				w.CrashAt = 0;
				PointerResult rec = new PointerEngine(w).Recover(() => to);
				PointerResult r = new PointerEngine(w).Apply(to);
				PointerStateFile st = w.State;
				if (!rec.Success || !r.Success || Sig(w.Reg) != wantReg || Sig(w.Schemes) != wantSchemes || OriginalSig(st.Original) != wantOrig || st.Phase != PointerScheme.PhaseIdle || w.Legacy != null)
				{
					bad.Add($"#{n}{(after ? "a" : "b")} {at}: rec={rec.Success} apply={r.Success} reg={(Sig(w.Reg) == wantReg)} orig={(OriginalSig(st.Original) == wantOrig)} phase={st.Phase} legacy={(w.Legacy != null)}");
				}
			}
		}
		Check("engine-h-crash-" + name, cr.Success && bad.Count == 0 && cases == steps * 2, $"{cases} crash points over {steps} steps" + (bad.Count == 0 ? "" : ": " + string.Join(" ; ", bad.Take(8))));
	}

	// j. Shell profiles own the pointer choice.
	private static void ProfileChecks(AppSettings real)
	{
		List<string> bad = new List<string>();
		AppSettings current = real.Clone();
		current.AlchemyCursors = "auto";
		current.UseWin81Cursors = false;
		ShellProfileOwnedSettings captured = ShellProfileOwnedSettings.Capture(current);
		if (captured.AlchemyCursors != "auto" || captured.ApplyTo(new AppSettings()).AlchemyCursors != "auto")
		{
			bad.Add("capture/apply round-trip");
		}
		foreach ((string id, PointerKind want) in new (string, PointerKind)[3] { (ShellProfileIds.Windows81, PointerKind.Win81), (ShellProfileIds.Windows7, PointerKind.Windows), (ShellProfileIds.Native, PointerKind.Windows) })
		{
			ShellProfileOwnedSettings owned = ShellProfileManager.BuildDefaultsForTest(id, current);
			AppSettings applied = owned.ApplyTo(current);
			PointerKind got = PointerScheme.Effective(applied, true).Kind;
			if (owned.AlchemyCursors.Length != 0 || applied.AlchemyCursors.Length != 0 || got != want)
			{
				bad.Add($"{id}: alchemy='{applied.AlchemyCursors}' effective={got} want {want}");
			}
		}
		AppSettings slot = new AppSettings { AlchemyCursors = "light" };
		AppSettings back = ShellProfileOwnedSettings.Capture(slot).ApplyTo(new AppSettings { UseWin81Cursors = true });
		if (PointerScheme.Effective(back, true).Palette != "light")
		{
			bad.Add("a saved Alchemy slot restores the Alchemy pointers");
		}
		Check("engine-j-profiles", bad.Count == 0, bad.Count == 0 ? "Capture/ApplyTo round-trip; windows81 -> Win81, windows7 and native -> Windows default" : string.Join(" ; ", bad));
	}

	private static void StatusChecks()
	{
		List<string> bad = new List<string>();
		void Want(PointerStatus s, string text)
		{
			string got = PointerScheme.StatusText(s);
			if (got != text)
			{
				bad.Add($"'{got}' want '{text}'");
			}
		}
		Want(new PointerStatus { AppliedKind = PointerKind.Windows }, "Your own Windows pointers are on.");
		Want(new PointerStatus { AppliedKind = PointerKind.Win81 }, "Windows 8.1 pointers are on. Windows default puts back the pointers you had before.");
		Want(new PointerStatus { AppliedKind = PointerKind.Alchemy, AppliedPalette = "dark", Mode = "dark" }, "Alchemy dark pointers are on.");
		Want(new PointerStatus { AppliedKind = PointerKind.Alchemy, AppliedPalette = "light", Mode = "auto" }, "Alchemy auto is on: light pointers now, they follow light/dark mode.");
		Want(new PointerStatus { AppliedKind = PointerKind.Alchemy, AppliedPalette = "halloween-dark", Mode = "dark" }, "Alchemy dark pointers are on, in Halloween colours.");
		Want(new PointerStatus { Busy = true, BusyAlchemy = true }, "Preparing the Alchemy pointers\u2026");
		Want(new PointerStatus { Phase = PointerScheme.PhaseApplying }, "A pointer change was interrupted; it will be finished automatically.");
		Want(new PointerStatus { Unreadable = true }, "The pointer restore point cannot be read right now, so pointer changes are paused.");
		Want(new PointerStatus { Failure = "Could not create the Alchemy pointers; nothing was changed." }, "Could not create the Alchemy pointers; nothing was changed.");
		Want(new PointerStatus { AppliedKind = PointerKind.Alchemy, AppliedPalette = "light", Mode = "light", Drift = true }, "Alchemy light pointers are on. Your pointers were changed outside the launcher; that choice is what Windows default brings back.");
		Check("status-texts", bad.Count == 0, string.Join(" ; ", bad));
	}

	// ===================================================================== helpers

	private sealed class Baseline
	{
		public string Registry = "";

		public string Schemes = "";

		public string Folder = "";

		public string Hashes = "";

		public string Legacy = "";

		public static Baseline Capture()
		{
			Baseline b = new Baseline();
			b.Registry = KeyHash("Control Panel\\Cursors");
			b.Schemes = KeyHash("Control Panel\\Cursors\\Schemes");
			string folder = Path.Combine(Root, "cursors");
			b.Folder = Directory.Exists(folder)
				? Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join(";", Directory.GetFiles(folder, "*", SearchOption.AllDirectories).OrderBy(f => f, StringComparer.OrdinalIgnoreCase).Select(f => Path.GetRelativePath(folder, f) + ":" + new FileInfo(f).Length + ":" + File.GetLastWriteTimeUtc(f).Ticks)))))[..16]
				: "(none)";
			List<string> files = new List<string> { Path.Combine(Root, "settings.json"), Path.Combine(Root, "pointer-state.json") };
			string state = Path.Combine(Root, "state");
			if (Directory.Exists(state))
			{
				files.AddRange(Directory.GetFiles(state, "settings.*").Concat(Directory.GetFiles(state, "pointer.*")).OrderBy(f => f, StringComparer.OrdinalIgnoreCase));
			}
			b.Hashes = string.Join(";", files.Select(f => Path.GetFileName(f) + "=" + (File.Exists(f) ? Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(f)))[..16] : "-")));
			b.Legacy = File.Exists(PointerScheme.LegacySnapshotPath) ? "present:" + Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(PointerScheme.LegacySnapshotPath)))[..16] : "absent";
			return b;
		}

		private static string KeyHash(string path)
		{
			try
			{
				using RegistryKey? k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(path);
				if (k == null)
				{
					return "(missing)";
				}
				StringBuilder sb = new StringBuilder();
				foreach (string n in k.GetValueNames().OrderBy(n => n, StringComparer.Ordinal))
				{
					object? v = k.GetValue(n, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
					string data = v switch
					{
						byte[] bytes => Convert.ToBase64String(bytes),
						string[] multi => string.Join("\0", multi),
						_ => v?.ToString() ?? ""
					};
					sb.Append(n).Append('|').Append(k.GetValueKind(n)).Append('|').Append(data).Append('\n');
				}
				return k.ValueCount + " values " + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())))[..16];
			}
			catch (Exception ex)
			{
				return "error:" + ex.Message;
			}
		}
	}

	private static BitmapSource Bitmap(byte[] bgra, int s)
	{
		BitmapSource bmp = BitmapSource.Create(s, s, 96, 96, PixelFormats.Bgra32, null, bgra, s * 4);
		bmp.Freeze();
		return bmp;
	}

	private static void Label(DrawingContext dc, string text, double x, double y, double size, Color color)
	{
		FormattedText ft = new FormattedText(text, System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), size, new SolidColorBrush(color), 1.0);
		dc.DrawText(ft, new Point(x, y));
	}

	private static BitmapSource Render(DrawingVisual dv, int w, int h)
	{
		RenderOptions.SetBitmapScalingMode(dv, BitmapScalingMode.NearestNeighbor);
		RenderTargetBitmap rtb = new RenderTargetBitmap(w, h, 96.0, 96.0, PixelFormats.Pbgra32);
		rtb.Render(dv);
		return rtb;
	}

	private static void SavePng(BitmapSource bmp, string path)
	{
		PngBitmapEncoder enc = new PngBitmapEncoder();
		enc.Frames.Add(BitmapFrame.Create(bmp));
		using FileStream fs = File.Create(path);
		enc.Save(fs);
	}

	[StructLayout(LayoutKind.Sequential)]
	private struct ICONINFO
	{
		public bool fIcon;

		public int xHotspot;

		public int yHotspot;

		public nint hbmMask;

		public nint hbmColor;
	}

	[StructLayout(LayoutKind.Sequential)]
	private struct BITMAP
	{
		public int bmType;

		public int bmWidth;

		public int bmHeight;

		public int bmWidthBytes;

		public ushort bmPlanes;

		public ushort bmBitsPixel;

		public nint bmBits;
	}

	[DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
	private static extern nint LoadImage(nint hInst, string name, uint type, int cx, int cy, uint fuLoad);

	[DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
	private static extern nint LoadCursorFromFileW(string fileName);

	[DllImport("user32.dll", SetLastError = true)]
	private static extern bool DestroyCursor(nint hCursor);

	[DllImport("user32.dll", SetLastError = true)]
	private static extern bool GetIconInfo(nint hIcon, out ICONINFO info);

	[DllImport("gdi32.dll")]
	private static extern int GetObject(nint h, int c, out BITMAP bm);

	[DllImport("gdi32.dll")]
	private static extern bool DeleteObject(nint h);

	[StructLayout(LayoutKind.Sequential)]
	private struct BITMAPINFOHEADER
	{
		public int biSize;

		public int biWidth;

		public int biHeight;

		public ushort biPlanes;

		public ushort biBitCount;

		public int biCompression;

		public int biSizeImage;

		public int biXPelsPerMeter;

		public int biYPelsPerMeter;

		public int biClrUsed;

		public int biClrImportant;
	}

	[DllImport("gdi32.dll")]
	private static extern nint CreateCompatibleDC(nint hdc);

	[DllImport("gdi32.dll")]
	private static extern bool DeleteDC(nint hdc);

	[DllImport("gdi32.dll")]
	private static extern nint CreateDIBSection(nint hdc, ref BITMAPINFOHEADER bmi, uint usage, out nint bits, nint section, uint offset);

	[DllImport("gdi32.dll")]
	private static extern nint SelectObject(nint hdc, nint obj);

	[DllImport("gdi32.dll")]
	private static extern bool GdiFlush();

	[DllImport("user32.dll", SetLastError = true)]
	private static extern bool DrawIconEx(nint hdc, int x, int y, nint icon, int cx, int cy, uint step, nint brush, uint flags);
}
