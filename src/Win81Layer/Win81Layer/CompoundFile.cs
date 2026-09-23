using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Win81Layer;

internal sealed class CompoundFile
{
	private readonly record struct DirEntry(uint Start, long Size, byte Type);

	private const uint EndOfChain = 4294967294u;

	private const uint FreeSect = uint.MaxValue;

	private readonly byte[] _data;

	private readonly int _sectorSize;

	private readonly int _miniSectorSize;

	private readonly uint _miniCutoff;

	private readonly uint[] _fat;

	private readonly uint[] _miniFat;

	private readonly Dictionary<string, DirEntry> _streams = new Dictionary<string, DirEntry>(StringComparer.Ordinal);

	private byte[] _miniStream = Array.Empty<byte>();

	public IEnumerable<string> StreamNames => _streams.Keys;

	private CompoundFile(byte[] data)
	{
		_data = data;
		if (data.Length < 512 || BitConverter.ToUInt64(data, 0) != 16220472316735377360uL)
		{
			throw new InvalidDataException("Not a compound file");
		}
		_sectorSize = 1 << (int)BitConverter.ToUInt16(data, 30);
		_miniSectorSize = 1 << (int)BitConverter.ToUInt16(data, 32);
		uint numFatSectors = BitConverter.ToUInt32(data, 44);
		uint firstDirSector = BitConverter.ToUInt32(data, 48);
		_miniCutoff = BitConverter.ToUInt32(data, 56);
		uint firstMiniFat = BitConverter.ToUInt32(data, 60);
		uint numMiniFat = BitConverter.ToUInt32(data, 64);
		List<uint> fatSectors = new List<uint>();
		for (int i = 0; i < 109; i++)
		{
			uint s = BitConverter.ToUInt32(data, 76 + i * 4);
			if (s == uint.MaxValue)
			{
				break;
			}
			fatSectors.Add(s);
		}
		_fat = ReadUintSectors(fatSectors);
		_miniFat = ((firstMiniFat == 4294967294u || numMiniFat == 0) ? Array.Empty<uint>() : ReadUintSectors(FollowFat(firstMiniFat)));
		byte[] dirBytes = ReadChain(firstDirSector);
		ParseDirectory(dirBytes);
	}

	public static CompoundFile Load(byte[] data)
	{
		return new CompoundFile(data);
	}

	private int SectorOffset(uint sector)
	{
		return (int)((sector + 1) * _sectorSize);
	}

	private uint[] ReadUintSectors(IEnumerable<uint> sectors)
	{
		List<uint> result = new List<uint>();
		foreach (uint s in sectors)
		{
			int off = SectorOffset(s);
			for (int i = 0; i + 4 <= _sectorSize && off + i + 4 <= _data.Length; i += 4)
			{
				result.Add(BitConverter.ToUInt32(_data, off + i));
			}
		}
		return result.ToArray();
	}

	private List<uint> FollowFat(uint start)
	{
		List<uint> chain = new List<uint>();
		uint cur = start;
		int guard = 0;
		while (cur != 4294967294u && cur != uint.MaxValue && cur < _fat.Length && guard++ < 1000000)
		{
			chain.Add(cur);
			cur = _fat[cur];
		}
		return chain;
	}

	private byte[] ReadChain(uint start)
	{
		using MemoryStream ms = new MemoryStream();
		foreach (uint s in FollowFat(start))
		{
			int off = SectorOffset(s);
			int len = Math.Min(_sectorSize, _data.Length - off);
			if (len > 0)
			{
				ms.Write(_data, off, len);
			}
		}
		return ms.ToArray();
	}

	private void ParseDirectory(byte[] dir)
	{
		DirEntry? root = null;
		List<(string, DirEntry)> entries = new List<(string, DirEntry)>();
		for (int off = 0; off + 128 <= dir.Length; off += 128)
		{
			int nameLen = BitConverter.ToUInt16(dir, off + 64);
			byte type = dir[off + 66];
			bool flag = (((uint)(type - 1) <= 1u || type == 5) ? true : false);
			if (flag && nameLen >= 2)
			{
				string name = Encoding.Unicode.GetString(dir, off, nameLen - 2);
				uint start = BitConverter.ToUInt32(dir, off + 116);
				long size = BitConverter.ToInt64(dir, off + 120);
				DirEntry e = new DirEntry(start, size, type);
				switch (type)
				{
				case 5:
					root = e;
					break;
				case 2:
					entries.Add((name, e));
					break;
				}
			}
		}
		if (root.HasValue)
		{
			DirEntry r = root.GetValueOrDefault();
			if (true)
			{
				_miniStream = ReadChain(r.Start)[..(int)Math.Min(r.Size, ReadChain(r.Start).Length)];
			}
		}
		foreach (var (name2, e2) in entries)
		{
			_streams[name2] = e2;
		}
	}

	public byte[] ReadStream(string name)
	{
		if (!_streams.TryGetValue(name, out var e))
		{
			return Array.Empty<byte>();
		}
		if (e.Size >= _miniCutoff)
		{
			return TrimTo(ReadChain(e.Start), e.Size);
		}
		using MemoryStream ms = new MemoryStream();
		uint cur = e.Start;
		int guard = 0;
		while (cur != 4294967294u && cur != uint.MaxValue && cur < _miniFat.Length && guard++ < 1000000)
		{
			int off = (int)(cur * _miniSectorSize);
			int len = Math.Min(_miniSectorSize, _miniStream.Length - off);
			if (len > 0)
			{
				ms.Write(_miniStream, off, len);
			}
			cur = _miniFat[cur];
		}
		return TrimTo(ms.ToArray(), e.Size);
	}

	private static byte[] TrimTo(byte[] b, long size)
	{
		return (b.Length <= size) ? b : b[..(int)size];
	}
}
