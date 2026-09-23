using System;
using System.Text;

namespace Win81Layer;

internal static class ShellLink
{
	public static string? GetTargetPath(byte[] lnk)
	{
		try
		{
			if (lnk.Length < 76 || BitConverter.ToUInt32(lnk, 0) != 76)
			{
				return null;
			}
			uint flags = BitConverter.ToUInt32(lnk, 20);
			int pos = 76;
			if ((flags & 1) != 0)
			{
				if (pos + 2 > lnk.Length)
				{
					return null;
				}
				int idListSize = BitConverter.ToUInt16(lnk, pos);
				pos += 2 + idListSize;
			}
			if ((flags & 2) != 0 && pos + 28 <= lnk.Length)
			{
				int linkInfoStart = pos;
				int headerSize = BitConverter.ToInt32(lnk, linkInfoStart + 4);
				uint liFlags = BitConverter.ToUInt32(lnk, linkInfoStart + 8);
				if ((liFlags & 1) != 0)
				{
					string basePath = ReadCString(lnk, linkInfoStart + BitConverter.ToInt32(lnk, linkInfoStart + 16), unicode: false);
					string suffix = ReadCString(lnk, linkInfoStart + BitConverter.ToInt32(lnk, linkInfoStart + 24), unicode: false);
					string path = basePath + suffix;
					if (headerSize >= 36)
					{
						int uBase = BitConverter.ToInt32(lnk, linkInfoStart + 28);
						if (uBase > 0)
						{
							string ub = ReadCString(lnk, linkInfoStart + uBase, unicode: true);
							int uSuf = BitConverter.ToInt32(lnk, linkInfoStart + 32);
							string us = ((uSuf > 0) ? ReadCString(lnk, linkInfoStart + uSuf, unicode: true) : string.Empty);
							if (ub.Length > 0)
							{
								path = ub + us;
							}
						}
					}
					return string.IsNullOrWhiteSpace(path) ? null : path;
				}
			}
			return null;
		}
		catch
		{
			return null;
		}
	}

	private static string ReadCString(byte[] b, int start, bool unicode)
	{
		if (start < 0 || start >= b.Length)
		{
			return string.Empty;
		}
		int end = start;
		if (unicode)
		{
			for (; end + 1 < b.Length && (b[end] != 0 || b[end + 1] != 0); end += 2)
			{
			}
			return Encoding.Unicode.GetString(b, start, end - start);
		}
		for (; end < b.Length && b[end] != 0; end++)
		{
		}
		return Encoding.Default.GetString(b, start, end - start);
	}
}
