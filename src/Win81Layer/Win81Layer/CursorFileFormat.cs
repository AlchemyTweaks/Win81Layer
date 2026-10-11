#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace Win81Layer;

// One frame of a .cur file: straight-alpha BGRA, top-down, size x size, with its hotspot.
internal readonly record struct CurFrame(int Size, int HotX, int HotY, byte[] Bgra);

internal sealed class ParsedCurFrame
{
	public int Width { get; init; }

	public int Height { get; init; }

	public int HotX { get; init; }

	public int HotY { get; init; }

	public int BytesInRes { get; init; }

	public int Offset { get; init; }

	public int HeaderSize { get; init; }

	public int BiWidth { get; init; }

	public int BiHeight { get; init; }

	public int Planes { get; init; }

	public int BitCount { get; init; }

	public int Compression { get; init; }

	public int SizeImage { get; init; }

	// Straight-alpha BGRA, top-down (empty for a PNG image; decode Png instead).
	public byte[] Bgra { get; init; } = Array.Empty<byte>();

	public bool IsPng { get; init; }

	public byte[] Png { get; init; } = Array.Empty<byte>();

	// True when every AND-mask bit is 1 exactly where alpha is 0 (always true for a PNG image, which has no mask).
	public bool MaskMatchesAlpha { get; init; }

	public bool MaskLengthOk { get; init; }
}

internal sealed class ParsedCur
{
	public int Reserved { get; init; }

	public int Type { get; init; }

	public List<ParsedCurFrame> Frames { get; } = new List<ParsedCurFrame>();
}

internal sealed class ParsedAni
{
	public long RiffSize { get; init; }

	public long FileLength { get; init; }

	public string Form { get; init; } = "";

	public string? Title { get; set; }

	public int HeaderSize { get; set; }

	public int Frames { get; set; }

	public int Steps { get; set; }

	public int Width { get; set; }

	public int Height { get; set; }

	public int BitCount { get; set; }

	public int Planes { get; set; }

	public int JifRate { get; set; }

	public int Flags { get; set; }

	public List<byte[]> Icons { get; } = new List<byte[]>();

	public bool ChunksPadded { get; set; } = true;
}

// Writers and readers for Windows pointer files. A .cur holds several sizes as 32-bit DIBs with an AND mask (or as
// PNG images); an .ani is a RIFF 'ACON' list of complete .cur frames shown in order at a fixed rate.
internal static class CursorFileFormat
{
	// Windows refuses an animated cursor when an image inside one of its frames starts 64 KB or more into the frame, and
	// when a cursor mixes DIB and PNG images it scales a DIB instead of using the PNG of the right size. Every image of
	// an animation frame is therefore a PNG: five sizes then take about 30 KB and each is drawn exactly as stored.
	public const int AniImageOffsetLimit = 65536;

	private static readonly byte[] PngSignature = new byte[8] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };

	// Every image as a 32-bit DIB with its AND mask, or (png) every image as an RGBA PNG.
	public static byte[] WriteCur(IReadOnlyList<CurFrame> frames, bool png = false)
	{
		if (frames == null || frames.Count == 0)
		{
			throw new ArgumentException("A cursor needs at least one frame.", nameof(frames));
		}
		List<byte[]> images = new List<byte[]>(frames.Count);
		foreach (CurFrame f in frames)
		{
			images.Add(png ? EncodePng(f.Bgra, f.Size) : Dib(f));
		}
		using MemoryStream ms = new MemoryStream();
		using BinaryWriter w = new BinaryWriter(ms);
		w.Write((ushort)0);
		w.Write((ushort)2);
		w.Write((ushort)frames.Count);
		int offset = 6 + 16 * frames.Count;
		for (int i = 0; i < frames.Count; i++)
		{
			CurFrame f = frames[i];
			w.Write((byte)(f.Size >= 256 ? 0 : f.Size));
			w.Write((byte)(f.Size >= 256 ? 0 : f.Size));
			w.Write((byte)0);
			w.Write((byte)0);
			w.Write((ushort)f.HotX);
			w.Write((ushort)f.HotY);
			w.Write(images[i].Length);
			w.Write(offset);
			offset += images[i].Length;
		}
		foreach (byte[] img in images)
		{
			w.Write(img);
		}
		w.Flush();
		return ms.ToArray();
	}

	// BITMAPINFOHEADER, bottom-up BGRA, then the 1 bpp AND mask (1 = transparent) with rows padded to 4 bytes.
	private static byte[] Dib(CurFrame f)
	{
		int s = f.Size;
		if (f.Bgra.Length != s * s * 4)
		{
			throw new ArgumentException("Frame pixel buffer does not match its size.");
		}
		int maskRow = (s + 31) / 32 * 4;
		byte[] o = new byte[40 + s * s * 4 + maskRow * s];
		using (MemoryStream ms = new MemoryStream(o))
		using (BinaryWriter w = new BinaryWriter(ms))
		{
			w.Write(40);
			w.Write(s);
			w.Write(s * 2);
			w.Write((ushort)1);
			w.Write((ushort)32);
			w.Write(0);
			w.Write(s * s * 4);
			w.Write(0);
			w.Write(0);
			w.Write(0);
			w.Write(0);
		}
		int p = 40;
		for (int y = s - 1; y >= 0; y--)
		{
			Buffer.BlockCopy(f.Bgra, y * s * 4, o, p, s * 4);
			p += s * 4;
		}
		for (int y = s - 1; y >= 0; y--)
		{
			for (int x = 0; x < s; x++)
			{
				if (f.Bgra[(y * s + x) * 4 + 3] == 0)
				{
					o[p + (x >> 3)] |= (byte)(0x80 >> (x & 7));
				}
			}
			p += maskRow;
		}
		return o;
	}

	// A plain 8-bit RGBA PNG (straight alpha), each row with the filter that gives the smallest sum.
	public static byte[] EncodePng(byte[] bgra, int s)
	{
		int stride = s * 4;
		byte[] raw = new byte[(stride + 1) * s];
		byte[] prev = new byte[stride];
		byte[] row = new byte[stride];
		byte[] best = new byte[stride];
		byte[] cand = new byte[stride];
		for (int y = 0; y < s; y++)
		{
			for (int x = 0; x < s; x++)
			{
				int k = (y * s + x) * 4;
				row[x * 4] = bgra[k + 2];
				row[x * 4 + 1] = bgra[k + 1];
				row[x * 4 + 2] = bgra[k];
				row[x * 4 + 3] = bgra[k + 3];
			}
			int bestType = 0;
			long bestSum = long.MaxValue;
			for (int type = 0; type < 5; type++)
			{
				long sum = 0;
				for (int i = 0; i < stride; i++)
				{
					int a = i >= 4 ? row[i - 4] : 0;
					int b = prev[i];
					int c = i >= 4 ? prev[i - 4] : 0;
					int pred = type switch
					{
						1 => a,
						2 => b,
						3 => (a + b) >> 1,
						4 => Paeth(a, b, c),
						_ => 0
					};
					byte v = (byte)(row[i] - pred);
					cand[i] = v;
					sum += v < 128 ? v : 256 - v;
				}
				if (sum < bestSum)
				{
					bestSum = sum;
					bestType = type;
					Buffer.BlockCopy(cand, 0, best, 0, stride);
				}
			}
			raw[y * (stride + 1)] = (byte)bestType;
			Buffer.BlockCopy(best, 0, raw, y * (stride + 1) + 1, stride);
			Buffer.BlockCopy(row, 0, prev, 0, stride);
		}
		byte[] idat;
		using (MemoryStream zs = new MemoryStream())
		{
			using (ZLibStream z = new ZLibStream(zs, CompressionLevel.SmallestSize, leaveOpen: true))
			{
				z.Write(raw, 0, raw.Length);
			}
			idat = zs.ToArray();
		}
		using MemoryStream o = new MemoryStream();
		o.Write(PngSignature, 0, PngSignature.Length);
		byte[] ihdr = new byte[13];
		WriteBE(ihdr, 0, s);
		WriteBE(ihdr, 4, s);
		ihdr[8] = 8;
		ihdr[9] = 6;
		WritePngChunk(o, "IHDR", ihdr);
		WritePngChunk(o, "IDAT", idat);
		WritePngChunk(o, "IEND", Array.Empty<byte>());
		return o.ToArray();
	}

	private static int Paeth(int a, int b, int c)
	{
		int p = a + b - c;
		int pa = Math.Abs(p - a);
		int pb = Math.Abs(p - b);
		int pc = Math.Abs(p - c);
		return (pa <= pb && pa <= pc) ? a : (pb <= pc ? b : c);
	}

	private static void WritePngChunk(Stream o, string type, byte[] data)
	{
		byte[] len = new byte[4];
		WriteBE(len, 0, data.Length);
		o.Write(len, 0, 4);
		byte[] t = Encoding.ASCII.GetBytes(type);
		o.Write(t, 0, 4);
		o.Write(data, 0, data.Length);
		uint crc = Crc32(Crc32(0xFFFFFFFFu, t), data) ^ 0xFFFFFFFFu;
		byte[] c = new byte[4];
		WriteBE(c, 0, (int)crc);
		o.Write(c, 0, 4);
	}

	private static void WriteBE(byte[] b, int o, int v)
	{
		b[o] = (byte)(v >> 24);
		b[o + 1] = (byte)(v >> 16);
		b[o + 2] = (byte)(v >> 8);
		b[o + 3] = (byte)v;
	}

	private static readonly uint[] CrcTable = BuildCrcTable();

	private static uint[] BuildCrcTable()
	{
		uint[] t = new uint[256];
		for (uint n = 0; n < 256; n++)
		{
			uint c = n;
			for (int k = 0; k < 8; k++)
			{
				c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
			}
			t[n] = c;
		}
		return t;
	}

	private static uint Crc32(uint crc, byte[] data)
	{
		foreach (byte b in data)
		{
			crc = CrcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
		}
		return crc;
	}

	public static byte[] WriteAni(IReadOnlyList<byte[]> curFiles, int jiffies, string title)
	{
		if (curFiles == null || curFiles.Count == 0)
		{
			throw new ArgumentException("An animated cursor needs at least one frame.", nameof(curFiles));
		}
		using MemoryStream body = new MemoryStream();
		using (BinaryWriter w = new BinaryWriter(body, Encoding.ASCII, leaveOpen: true))
		{
			w.Write(Encoding.ASCII.GetBytes("ACON"));
			if (!string.IsNullOrEmpty(title))
			{
				byte[] name = Encoding.ASCII.GetBytes(title + "\0");
				int inam = 8 + Pad(name.Length);
				w.Write(Encoding.ASCII.GetBytes("LIST"));
				w.Write(4 + inam);
				w.Write(Encoding.ASCII.GetBytes("INFO"));
				WriteChunk(w, "INAM", name);
			}
			byte[] anih = new byte[36];
			using (MemoryStream hs = new MemoryStream(anih))
			using (BinaryWriter hw = new BinaryWriter(hs))
			{
				hw.Write(36);
				hw.Write(curFiles.Count);
				hw.Write(curFiles.Count);
				hw.Write(0);
				hw.Write(0);
				hw.Write(32);
				hw.Write(1);
				hw.Write(jiffies);
				hw.Write(1);
			}
			WriteChunk(w, "anih", anih);
			long framSize = 4;
			foreach (byte[] cur in curFiles)
			{
				framSize += 8 + Pad(cur.Length);
			}
			w.Write(Encoding.ASCII.GetBytes("LIST"));
			w.Write((int)framSize);
			w.Write(Encoding.ASCII.GetBytes("fram"));
			foreach (byte[] cur in curFiles)
			{
				WriteChunk(w, "icon", cur);
			}
		}
		byte[] payload = body.ToArray();
		byte[] o = new byte[8 + payload.Length];
		Encoding.ASCII.GetBytes("RIFF").CopyTo(o, 0);
		BitConverter.GetBytes(payload.Length).CopyTo(o, 4);
		payload.CopyTo(o, 8);
		return o;
	}

	private static int Pad(int n)
	{
		return (n + 1) & ~1;
	}

	private static void WriteChunk(BinaryWriter w, string id, byte[] data)
	{
		w.Write(Encoding.ASCII.GetBytes(id));
		w.Write(data.Length);
		w.Write(data);
		if ((data.Length & 1) == 1)
		{
			w.Write((byte)0);
		}
	}

	public static ParsedCur ReadCur(byte[] b)
	{
		int reserved = U16(b, 0);
		int type = U16(b, 2);
		int count = U16(b, 4);
		ParsedCur cur = new ParsedCur { Reserved = reserved, Type = type };
		for (int i = 0; i < count; i++)
		{
			int e = 6 + 16 * i;
			int w = b[e] == 0 ? 256 : b[e];
			int h = b[e + 1] == 0 ? 256 : b[e + 1];
			int hx = U16(b, e + 4);
			int hy = U16(b, e + 6);
			int bytes = I32(b, e + 8);
			int off = I32(b, e + 12);
			if (b.AsSpan(off, 8).SequenceEqual(PngSignature))
			{
				byte[] png = new byte[bytes];
				Buffer.BlockCopy(b, off, png, 0, bytes);
				int pw = (b[off + 16] << 24) | (b[off + 17] << 16) | (b[off + 18] << 8) | b[off + 19];
				int ph = (b[off + 20] << 24) | (b[off + 21] << 16) | (b[off + 22] << 8) | b[off + 23];
				cur.Frames.Add(new ParsedCurFrame
				{
					Width = w,
					Height = h,
					HotX = hx,
					HotY = hy,
					BytesInRes = bytes,
					Offset = off,
					BiWidth = pw,
					BiHeight = ph,
					IsPng = true,
					Png = png,
					MaskMatchesAlpha = true,
					MaskLengthOk = true
				});
				continue;
			}
			int hs = I32(b, off);
			int bw = I32(b, off + 4);
			int bh = I32(b, off + 8);
			int planes = U16(b, off + 12);
			int bpp = U16(b, off + 14);
			int comp = I32(b, off + 16);
			int isz = I32(b, off + 20);
			int s = bw;
			byte[] bgra = new byte[s * s * 4];
			int px = off + hs;
			for (int y = 0; y < s; y++)
			{
				Buffer.BlockCopy(b, px + (s - 1 - y) * s * 4, bgra, y * s * 4, s * 4);
			}
			int maskRow = (s + 31) / 32 * 4;
			int maskStart = px + s * s * 4;
			bool lengthOk = hs + s * s * 4 + maskRow * s == bytes;
			bool maskOk = true;
			for (int y = 0; y < s && maskOk; y++)
			{
				int row = maskStart + (s - 1 - y) * maskRow;
				for (int x = 0; x < s; x++)
				{
					bool bit = (b[row + (x >> 3)] & (0x80 >> (x & 7))) != 0;
					bool clear = bgra[(y * s + x) * 4 + 3] == 0;
					if (bit != clear)
					{
						maskOk = false;
						break;
					}
				}
			}
			cur.Frames.Add(new ParsedCurFrame
			{
				Width = w,
				Height = h,
				HotX = hx,
				HotY = hy,
				BytesInRes = bytes,
				Offset = off,
				HeaderSize = hs,
				BiWidth = bw,
				BiHeight = bh,
				Planes = planes,
				BitCount = bpp,
				Compression = comp,
				SizeImage = isz,
				Bgra = bgra,
				MaskMatchesAlpha = maskOk,
				MaskLengthOk = lengthOk
			});
		}
		return cur;
	}

	public static ParsedAni ReadAni(byte[] b)
	{
		if (b.Length < 12 || Encoding.ASCII.GetString(b, 0, 4) != "RIFF")
		{
			throw new InvalidDataException("Not a RIFF file.");
		}
		ParsedAni ani = new ParsedAni
		{
			RiffSize = (uint)I32(b, 4),
			FileLength = b.Length,
			Form = Encoding.ASCII.GetString(b, 8, 4)
		};
		int end = Math.Min(b.Length, 8 + (int)ani.RiffSize);
		int p = 12;
		while (p + 8 <= end)
		{
			string id = Encoding.ASCII.GetString(b, p, 4);
			int size = I32(b, p + 4);
			int data = p + 8;
			if (id == "anih")
			{
				ani.HeaderSize = I32(b, data);
				ani.Frames = I32(b, data + 4);
				ani.Steps = I32(b, data + 8);
				ani.Width = I32(b, data + 12);
				ani.Height = I32(b, data + 16);
				ani.BitCount = I32(b, data + 20);
				ani.Planes = I32(b, data + 24);
				ani.JifRate = I32(b, data + 28);
				ani.Flags = I32(b, data + 32);
			}
			else if (id == "LIST")
			{
				string kind = Encoding.ASCII.GetString(b, data, 4);
				int q = data + 4;
				int listEnd = data + size;
				while (q + 8 <= listEnd)
				{
					string sub = Encoding.ASCII.GetString(b, q, 4);
					int subSize = I32(b, q + 4);
					if (kind == "fram" && sub == "icon")
					{
						byte[] icon = new byte[subSize];
						Buffer.BlockCopy(b, q + 8, icon, 0, subSize);
						ani.Icons.Add(icon);
					}
					else if (kind == "INFO" && sub == "INAM")
					{
						ani.Title = Encoding.ASCII.GetString(b, q + 8, subSize).TrimEnd('\0');
					}
					if ((subSize & 1) == 1 && (q + 8 + subSize >= b.Length || b[q + 8 + subSize] != 0))
					{
						ani.ChunksPadded = false;
					}
					q += 8 + Pad(subSize);
				}
			}
			if ((size & 1) == 1 && (data + size >= b.Length || b[data + size] != 0))
			{
				ani.ChunksPadded = false;
			}
			p = data + Pad(size);
		}
		return ani;
	}

	private static int U16(byte[] b, int o)
	{
		return b[o] | (b[o + 1] << 8);
	}

	private static int I32(byte[] b, int o)
	{
		return b[o] | (b[o + 1] << 8) | (b[o + 2] << 16) | (b[o + 3] << 24);
	}
}
