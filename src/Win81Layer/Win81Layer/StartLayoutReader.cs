#nullable enable

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;

namespace Win81Layer;

// Result of one read of the native Start pin store. Ok == false means "unknown" (never "no pins"): the sync then
// changes nothing.
internal sealed class StartLayoutRead
{
	public bool Ok { get; init; }

	public string? Error { get; init; }

	// The header write time (UTC FILETIME) of the file that was read.
	public long FileTime { get; init; }

	// pinnedList ids in order: "P~<packaged AUMID>", "W~<desktop AppsFolder parsing name>" (others are ignored).
	public List<string> Ids { get; init; } = new List<string>();
}

// Read-only reader for Windows 11's native Start pins
// (%LOCALAPPDATA%\Packages\Microsoft.Windows.StartMenuExperienceHost_cw5n1h2txyewy\LocalState\start2.bin), for
// interoperability with the user's own data. Only StartMenuExperienceHost writes this file; the launcher never writes
// it, never resumes that host to read it, and never calls Export-StartLayout (it hangs while the host is suspended).
//
// The file is obfuscated, not protected per user: AES-256-CBC (PKCS7) whose key and IV are derived with MT19937 from
// the header's write time, with random padding around the ciphertext. Every structural check (magic, version GUID,
// lengths, padding, JSON shape) must pass or the read reports "unknown", so a format change in a Windows update pauses
// the Start part of the sync instead of guessing.
//
// The two salts of the key schedule come from a Windows binary and are not part of this source tree. A local
// StartLayoutReader.Salts.cs provides them through LoadSalts; without it Available is false and the live Start sync
// stays off (the taskbar sync is unaffected).
internal static partial class StartLayoutReader
{
	internal static string DefaultPath => Path.Combine(
		Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
		"Packages",
		"Microsoft.Windows.StartMenuExperienceHost_cw5n1h2txyewy",
		"LocalState",
		"start2.bin");

	internal static readonly byte[] Magic = Convert.FromHexString("e27ae14b01fc4d1b9c00810bde6e5185");

	internal static readonly byte[] FormatVersion = Convert.FromHexString("4e5a5f47005bb1498a5c92af9084f95e");

	private static readonly uint KeySeedSalt;

	private static readonly uint PadSeedSalt;

	// True when the local salts are present, so real start2.bin files can be read.
	internal static readonly bool Available;

	static partial void LoadSalts(ref uint keySeedSalt, ref uint padSeedSalt, ref bool available);

	static StartLayoutReader()
	{
		uint key = 0u;
		uint pad = 0u;
		bool available = false;
		LoadSalts(ref key, ref pad, ref available);
		KeySeedSalt = key;
		PadSeedSalt = pad;
		Available = available;
	}

	internal const int HeaderSize = 0x2C;

	internal const int PadTotal = 0x200;

	// Bounded: the real file is ~25 KB; anything far larger is not this format.
	private const int MaxFileBytes = 8 * 1024 * 1024;

	// Up to 3 attempts 500 ms apart (the host may be mid-write); any failure after that is "unknown".
	public static StartLayoutRead TryRead(string path, int attempts = 3)
	{
		string? lastError = null;
		for (int i = 0; i < Math.Max(1, attempts); i++)
		{
			if (i > 0)
			{
				Thread.Sleep(500);
			}
			try
			{
				if (!File.Exists(path))
				{
					return new StartLayoutRead { Ok = false, Error = "start2.bin missing" };
				}
				byte[] data;
				using (FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
				{
					if (fs.Length > MaxFileBytes)
					{
						return new StartLayoutRead { Ok = false, Error = "start2.bin too large" };
					}
					data = new byte[fs.Length];
					fs.ReadExactly(data);
				}
				return Decode(data);
			}
			catch (Exception ex)
			{
				lastError = ex.GetType().Name + ": " + ex.Message;
			}
		}
		return new StartLayoutRead { Ok = false, Error = lastError };
	}

	public static StartLayoutRead Decode(byte[] data)
	{
		try
		{
			if (data.Length < HeaderSize || !data.AsSpan(0, 16).SequenceEqual(Magic))
			{
				return Fail("bad magic");
			}
			if (!data.AsSpan(16, 16).SequenceEqual(FormatVersion))
			{
				return Fail("unknown format version " + Convert.ToHexString(data, 16, 16));
			}
			ulong ft = BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan(0x20, 8));
			uint bodyLen = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(0x28, 4));
			if (bodyLen < PadTotal + 16 || (long)HeaderSize + bodyLen > data.Length)
			{
				return Fail("truncated body");
			}
			int cipherLen = (int)bodyLen - PadTotal;
			if (cipherLen % 16 != 0)
			{
				return Fail("ciphertext length not a block multiple");
			}
			int prefix = PadPrefix(ft);
			(byte[] key, byte[] iv) = KeyIv(ft);
			byte[] plain;
			using (Aes aes = Aes.Create())
			{
				aes.Key = key;
				plain = aes.DecryptCbc(data.AsSpan(HeaderSize + prefix, cipherLen), iv, PaddingMode.PKCS7);
			}
			List<string>? ids = PinnedIds(plain);
			if (ids == null)
			{
				return Fail("plaintext is not the expected JSON");
			}
			return new StartLayoutRead { Ok = true, FileTime = (long)ft, Ids = ids };
		}
		catch (CryptographicException)
		{
			return Fail("decrypt failed (padding)");
		}
		catch (Exception ex)
		{
			return Fail(ex.GetType().Name + ": " + ex.Message);
		}
	}

	private static StartLayoutRead Fail(string error) => new StartLayoutRead { Ok = false, Error = error };

	// pinnedList flattened in order. Entries are {"id": "...", ...}; a folder-like entry with a nested "items" or
	// "children" list is walked. Null when the document does not have a pinnedList array.
	private static List<string>? PinnedIds(byte[] utf8Json)
	{
		using JsonDocument doc = JsonDocument.Parse(utf8Json);
		if (doc.RootElement.ValueKind != JsonValueKind.Object
			|| !doc.RootElement.TryGetProperty("pinnedList", out JsonElement list)
			|| list.ValueKind != JsonValueKind.Array)
		{
			return null;
		}
		List<string> ids = new List<string>();
		Walk(list, ids, 0);
		return ids;
	}

	private static void Walk(JsonElement array, List<string> ids, int depth)
	{
		if (depth > 4)
		{
			return;
		}
		foreach (JsonElement e in array.EnumerateArray())
		{
			if (e.ValueKind != JsonValueKind.Object)
			{
				continue;
			}
			if ((e.TryGetProperty("items", out JsonElement kids) || e.TryGetProperty("children", out kids)) && kids.ValueKind == JsonValueKind.Array)
			{
				Walk(kids, ids, depth + 1);
				continue;
			}
			if (e.TryGetProperty("id", out JsonElement id) && id.ValueKind == JsonValueKind.String)
			{
				string? s = id.GetString();
				if (!string.IsNullOrWhiteSpace(s))
				{
					ids.Add(s);
				}
			}
		}
	}

	// ---- key schedule (StartMenu.dll StartMenuPersistenceManager) ----

	internal static int PadPrefix(ulong ft)
	{
		return (int)(new Mt19937(PadSeedSalt ^ (uint)ft).Next() & 0x1FF);
	}

	internal static (byte[] Key, byte[] Iv) KeyIv(ulong ft)
	{
		uint lo = (uint)ft;
		uint hi = (uint)(ft >> 32);
		Mt19937 e = new Mt19937(lo ^ hi ^ KeySeedSalt);
		uint s1 = e.Next();
		uint keyLen = StableUniform(e, 64, 128);
		uint s2 = e.Next();
		byte[] keyText = Encoding.UTF8.GetBytes(RandomString(s1, (int)keyLen));
		byte[] ivText = Encoding.UTF8.GetBytes(RandomString(s2, 16));
		// WinRT's AES_CBC_PKCS7 provider keeps the first 32 bytes of the key material (AES-256).
		byte[] key = new byte[32];
		Array.Copy(keyText, key, 32);
		return (key, ivText);
	}

	private static string RandomString(uint seed, int length)
	{
		Mt19937 g = new Mt19937(seed);
		g.Discard(StableUniform(g, 0, 1000));
		StringBuilder sb = new StringBuilder(length);
		for (int i = 0; i < length; i++)
		{
			sb.Append((char)(0x20 + StableUniform(g, 0, 95)));
		}
		return sb.ToString();
	}

	// Rejection-sampled uniform integer in [lo, hi].
	private static uint StableUniform(Mt19937 e, uint lo, uint hi)
	{
		uint range = hi - lo;
		if (range == uint.MaxValue)
		{
			return e.Next();
		}
		uint n = range + 1;
		uint limQ = uint.MaxValue / n;
		uint limR = uint.MaxValue % n;
		while (true)
		{
			uint x = range != 0 ? e.Next() : 0;
			uint q = x / n;
			uint r = x % n;
			if (q < limQ || limR == range)
			{
				return r + lo;
			}
		}
	}

	internal sealed class Mt19937
	{
		private readonly uint[] _mt = new uint[624];

		private int _index;

		public Mt19937(uint seed)
		{
			_mt[0] = seed;
			for (int i = 1; i < 624; i++)
			{
				uint prev = _mt[i - 1];
				_mt[i] = unchecked(0x6C078965u * (prev ^ (prev >> 30)) + (uint)i);
			}
			_index = 624;
		}

		public uint Next()
		{
			if (_index >= 624)
			{
				Twist();
			}
			uint y = _mt[_index++];
			y ^= y >> 11;
			y ^= (y << 7) & 0x9D2C5680u;
			y ^= (y << 15) & 0xEFC60000u;
			y ^= y >> 18;
			return y;
		}

		public void Discard(uint n)
		{
			for (uint i = 0; i < n; i++)
			{
				Next();
			}
		}

		private void Twist()
		{
			for (int i = 0; i < 624; i++)
			{
				uint y = (_mt[i] & 0x80000000u) | (_mt[(i + 1) % 624] & 0x7FFFFFFFu);
				_mt[i] = _mt[(i + 397) % 624] ^ (y >> 1) ^ ((y & 1) != 0 ? 0x9908B0DFu : 0u);
			}
			_index = 0;
		}
	}
}
