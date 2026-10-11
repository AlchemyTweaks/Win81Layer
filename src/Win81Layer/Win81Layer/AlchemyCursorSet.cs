#nullable enable

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading.Tasks;

namespace Win81Layer;

internal sealed class AlchemyCursorManifest
{
	public int GeneratorVersion { get; set; }

	public string Palette { get; set; } = "";

	public DateTime CreatedUtc { get; set; }

	public List<AlchemyCursorManifestFile> Files { get; set; } = new List<AlchemyCursorManifestFile>();
}

internal sealed class AlchemyCursorManifestFile
{
	public string Role { get; set; } = "";

	public string Name { get; set; } = "";

	public long Bytes { get; set; }

	public string Sha256 { get; set; } = "";
}

internal sealed class AlchemyPaletteFiles
{
	public IReadOnlyDictionary<CursorRole, string> Paths { get; init; } = new Dictionary<CursorRole, string>();

	// True when the files were drawn now (missing, damaged or from an older generator), false when reused.
	public bool Generated { get; init; }

	public long Milliseconds { get; init; }
}

// The Alchemy pointer files on disk: drawn by AlchemyCursorArt into the launcher's own data folder, one folder per
// palette and generator version, written to a temporary folder, checked, then moved into place.
internal static class AlchemyCursorSet
{
	public const int GeneratorVersion = 1;

	public const string ManifestName = "palette.json";

	public static string Root => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Win81Layer", "cursors", "alchemy");

	private static readonly object Gate = new object();

	// Why the last Ensure returned null ("" after a success); shown by the self-test, which runs without a log.
	public static string LastError { get; private set; } = "";

	// Self-test only: returning true makes the final move of a new set into place fail.
	internal static Func<string, bool>? SwapFaultForTest;

	private static readonly JsonSerializerOptions Json = new JsonSerializerOptions
	{
		PropertyNameCaseInsensitive = true,
		WriteIndented = true
	};

	public static readonly (CursorRole Role, string Name)[] Files = new (CursorRole, string)[17]
	{
		(CursorRole.Arrow, "arrow.cur"),
		(CursorRole.Help, "help.cur"),
		(CursorRole.AppStarting, "working.ani"),
		(CursorRole.Wait, "busy.ani"),
		(CursorRole.Crosshair, "precision.cur"),
		(CursorRole.IBeam, "text.cur"),
		(CursorRole.NWPen, "handwriting.cur"),
		(CursorRole.No, "unavailable.cur"),
		(CursorRole.SizeNS, "size-ns.cur"),
		(CursorRole.SizeWE, "size-we.cur"),
		(CursorRole.SizeNWSE, "size-nwse.cur"),
		(CursorRole.SizeNESW, "size-nesw.cur"),
		(CursorRole.SizeAll, "move.cur"),
		(CursorRole.UpArrow, "alternate.cur"),
		(CursorRole.Hand, "link.cur"),
		(CursorRole.Pin, "location.cur"),
		(CursorRole.Person, "person.cur")
	};

	public static string FileName(CursorRole role)
	{
		foreach ((CursorRole r, string name) in Files)
		{
			if (r == role)
			{
				return name;
			}
		}
		throw new ArgumentOutOfRangeException(nameof(role));
	}

	public static string VersionFolder(string? rootOverride = null)
	{
		return Path.Combine(rootOverride ?? Root, "v" + GeneratorVersion);
	}

	public static string FolderFor(string paletteId, string? rootOverride = null)
	{
		return Path.Combine(VersionFolder(rootOverride), paletteId);
	}

	// The complete file for a role: a five-size .cur, or a 24-frame .ani for the animated roles (each frame a five-size
	// .cur of PNG images, see CursorFileFormat.AniImageOffsetLimit).
	public static byte[] BuildFile(CursorRole role, AlchemyPalette p)
	{
		if (AlchemyCursorArt.IsAnimated(role))
		{
			List<byte[]> frames = new List<byte[]>(AlchemyCursorArt.AnimFrames);
			for (int f = 0; f < AlchemyCursorArt.AnimFrames; f++)
			{
				frames.Add(BuildCur(role, p, f, png: true));
			}
			string title = "Alchemy " + (role == CursorRole.Wait ? "busy" : "working");
			return CursorFileFormat.WriteAni(frames, AlchemyCursorArt.AnimJiffies, title);
		}
		return BuildCur(role, p, 0, png: false);
	}

	private static byte[] BuildCur(CursorRole role, AlchemyPalette p, int frame, bool png)
	{
		List<CurFrame> frames = new List<CurFrame>(AlchemyCursorArt.Sizes.Length);
		foreach (int s in AlchemyCursorArt.Sizes)
		{
			(int hx, int hy) = AlchemyCursorArt.Hotspot(role, s);
			frames.Add(new CurFrame(s, hx, hy, AlchemyCursorArt.RenderBgra(role, p, s, frame)));
		}
		return CursorFileFormat.WriteCur(frames, png);
	}

	// Returns the role paths of a ready palette, drawing it first when it is missing or incomplete. null when the
	// palette is unknown or could not be written and checked; nothing in place is changed in that case.
	public static AlchemyPaletteFiles? Ensure(string paletteId, string? rootOverride, Func<string, bool>? validate)
	{
		AlchemyPalette? p = AlchemyPalettes.Find(paletteId);
		if (p == null)
		{
			Logger.Log("Alchemy pointers: unknown palette '" + paletteId + "'.");
			return null;
		}
		if (rootOverride == null && SettingsStore.ReadOnlyDiagnostics)
		{
			throw new InvalidOperationException("Pointer files are not written in read-only diagnostics mode.");
		}
		lock (Gate)
		{
			string folder = FolderFor(p.Id, rootOverride);
			Dictionary<CursorRole, string>? ready = TryReuse(folder, p.Id);
			if (ready != null)
			{
				return new AlchemyPaletteFiles { Paths = ready, Generated = false };
			}
			Stopwatch sw = Stopwatch.StartNew();
			string parent = VersionFolder(rootOverride);
			string tmp = Path.Combine(parent, $"{p.Id}.tmp-{Environment.ProcessId}-{DateTime.UtcNow.Ticks}");
			string? old = null;
			try
			{
				Directory.CreateDirectory(tmp);
				byte[][] built = new byte[Files.Length][];
				Parallel.For(0, Files.Length, (int i) => built[i] = BuildFile(Files[i].Role, p));
				AlchemyCursorManifest manifest = new AlchemyCursorManifest
				{
					GeneratorVersion = GeneratorVersion,
					Palette = p.Id,
					CreatedUtc = DateTime.UtcNow
				};
				for (int i = 0; i < Files.Length; i++)
				{
					string path = Path.Combine(tmp, Files[i].Name);
					WriteDurable(path, built[i]);
					manifest.Files.Add(new AlchemyCursorManifestFile
					{
						Role = Files[i].Role.ToString(),
						Name = Files[i].Name,
						Bytes = built[i].Length,
						Sha256 = Convert.ToHexString(SHA256.HashData(built[i]))
					});
				}
				for (int i = 0; i < Files.Length; i++)
				{
					string path = Path.Combine(tmp, Files[i].Name);
					if (validate != null && !validate(path))
					{
						LastError = $"{Files[i].Name} of {p.Id} did not load";
						Logger.Log($"Alchemy pointers: {Files[i].Name} of {p.Id} did not load; the set was not installed.");
						TryDeleteTree(tmp);
						return null;
					}
				}
				WriteDurable(Path.Combine(tmp, ManifestName), JsonSerializer.SerializeToUtf8Bytes(manifest, Json));
				if (Directory.Exists(folder))
				{
					old = Path.Combine(parent, $"{p.Id}.old-{DateTime.UtcNow.Ticks}");
					Directory.Move(folder, old);
				}
				if (SwapFaultForTest != null && SwapFaultForTest(folder))
				{
					throw new IOException("Simulated failure while moving the new set into place.");
				}
				Directory.Move(tmp, folder);
				if (old != null)
				{
					TryDeleteTree(old);
				}
				sw.Stop();
				LastError = "";
				Logger.Log($"Alchemy pointers: drew the {p.Id} set ({Files.Length} pointers) in {sw.ElapsedMilliseconds} ms");
				return new AlchemyPaletteFiles { Paths = PathsIn(folder), Generated = true, Milliseconds = sw.ElapsedMilliseconds };
			}
			catch (Exception ex)
			{
				LastError = ex.GetType().Name + ": " + ex.Message;
				Logger.Log("Alchemy pointers: could not prepare the " + p.Id + " set: " + ex.Message);
				// The registry may point at the folder that was moved aside: put it back so nothing in use goes missing.
				if (old != null && !Directory.Exists(folder) && Directory.Exists(old))
				{
					try
					{
						Directory.Move(old, folder);
						Logger.Log("Alchemy pointers: the previous " + p.Id + " set was put back.");
					}
					catch (Exception restoreEx)
					{
						Logger.Log("Alchemy pointers: could not put the previous " + p.Id + " set back: " + restoreEx.Message);
					}
				}
				TryDeleteTree(tmp);
				return null;
			}
		}
	}

	// Reuse needs a manifest for this version and palette and every file present at its recorded length. No hashing.
	private static Dictionary<CursorRole, string>? TryReuse(string folder, string paletteId)
	{
		try
		{
			string manifestPath = Path.Combine(folder, ManifestName);
			if (!File.Exists(manifestPath))
			{
				return null;
			}
			AlchemyCursorManifest? m = JsonSerializer.Deserialize<AlchemyCursorManifest>(File.ReadAllBytes(manifestPath), Json);
			if (m == null || m.GeneratorVersion != GeneratorVersion || !string.Equals(m.Palette, paletteId, StringComparison.Ordinal))
			{
				return null;
			}
			foreach ((CursorRole role, string name) in Files)
			{
				AlchemyCursorManifestFile? e = m.Files.FirstOrDefault(f => string.Equals(f.Name, name, StringComparison.Ordinal) && string.Equals(f.Role, role.ToString(), StringComparison.Ordinal));
				string path = Path.Combine(folder, name);
				if (e == null || e.Bytes <= 0)
				{
					return null;
				}
				FileInfo fi = new FileInfo(path);
				if (!fi.Exists || fi.Length != e.Bytes)
				{
					return null;
				}
			}
			return PathsIn(folder);
		}
		catch
		{
			return null;
		}
	}

	private static Dictionary<CursorRole, string> PathsIn(string folder)
	{
		Dictionary<CursorRole, string> d = new Dictionary<CursorRole, string>();
		foreach ((CursorRole role, string name) in Files)
		{
			d[role] = Path.GetFullPath(Path.Combine(folder, name));
		}
		return d;
	}

	// True when the palette's files are all in place (no drawing). Startup uses it to notice a deleted folder.
	public static bool IsReady(string paletteId, string? rootOverride = null)
	{
		AlchemyPalette? p = AlchemyPalettes.Find(paletteId);
		return p != null && TryReuse(FolderFor(p.Id, rootOverride), p.Id) != null;
	}

	// Best effort: removes other palettes, older generator versions and leftovers of interrupted writes. Files that
	// are in use are skipped.
	public static void Cleanup(IEnumerable<string> keepIds, string? rootOverride = null)
	{
		if (rootOverride == null && SettingsStore.ReadOnlyDiagnostics)
		{
			throw new InvalidOperationException("Pointer files are not removed in read-only diagnostics mode.");
		}
		HashSet<string> keep = new HashSet<string>(keepIds.Where(k => !string.IsNullOrEmpty(k)), StringComparer.OrdinalIgnoreCase);
		lock (Gate)
		{
			string root = rootOverride ?? Root;
			if (!Directory.Exists(root))
			{
				return;
			}
			string current = "v" + GeneratorVersion;
			foreach (string dir in SafeDirs(root))
			{
				if (!string.Equals(Path.GetFileName(dir), current, StringComparison.OrdinalIgnoreCase))
				{
					TryDeleteTree(dir);
				}
			}
			string version = VersionFolder(rootOverride);
			foreach (string dir in SafeDirs(version))
			{
				if (!keep.Contains(Path.GetFileName(dir)))
				{
					TryDeleteTree(dir);
				}
			}
		}
	}

	private static IEnumerable<string> SafeDirs(string path)
	{
		try
		{
			return Directory.Exists(path) ? Directory.GetDirectories(path) : Array.Empty<string>();
		}
		catch
		{
			return Array.Empty<string>();
		}
	}

	private static void WriteDurable(string path, byte[] data)
	{
		using FileStream fs = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, FileOptions.WriteThrough);
		fs.Write(data, 0, data.Length);
		fs.Flush(flushToDisk: true);
	}

	private static void TryDeleteTree(string path)
	{
		try
		{
			if (Directory.Exists(path))
			{
				Directory.Delete(path, recursive: true);
			}
		}
		catch (Exception ex)
		{
			Logger.Log("Alchemy pointers: could not remove " + Path.GetFileName(path) + ": " + ex.Message);
		}
	}

	// Loads every frame size the way Windows does and frees it again. Used before a set is installed.
	public static bool ValidateWithLoadImage(string path)
	{
		try
		{
			foreach (int s in AlchemyCursorArt.Sizes)
			{
				nint h = LoadImage(nint.Zero, path, 2u, s, s, 0x10u);
				if (h == nint.Zero)
				{
					return false;
				}
				DestroyCursor(h);
			}
			return true;
		}
		catch
		{
			return false;
		}
	}

	[DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
	private static extern nint LoadImage(nint hInst, string name, uint type, int cx, int cy, uint fuLoad);

	[DllImport("user32.dll", SetLastError = true)]
	private static extern bool DestroyCursor(nint hCursor);
}
