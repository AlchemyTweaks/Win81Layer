#nullable enable
using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Win81Layer;

internal static class LiveTileDataCache
{
	private static readonly object Gate = new();
	internal static string Root => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Win81Layer", "cache", "live-tiles");
	private static string PathFor(string key) => Path.Combine(Root, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key))) + ".cache");
	internal static T? Load<T>(string key, bool encrypted = false) where T : class
	{
		lock (Gate)
		{
			try
			{
				string path = PathFor(key);
				if (!File.Exists(path) || new FileInfo(path).Length > 2_000_000) return null;
				byte[] bytes = File.ReadAllBytes(path);
				if (encrypted) bytes = ProtectedData.Unprotect(bytes, null, DataProtectionScope.CurrentUser);
				return JsonSerializer.Deserialize<T>(bytes);
			}
			catch { return null; }
		}
	}
	internal static void Save<T>(string key, T value, bool encrypted = false)
	{
		lock (Gate)
		{
			string temporary = PathFor(key) + "." + Guid.NewGuid().ToString("N") + ".tmp";
			try
			{
				Directory.CreateDirectory(Root);
				byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(value);
				if (encrypted) bytes = ProtectedData.Protect(bytes, null, DataProtectionScope.CurrentUser);
				File.WriteAllBytes(temporary, bytes);
				File.Move(temporary, PathFor(key), true);
				foreach (FileInfo old in new DirectoryInfo(Root).GetFiles("*.cache").OrderByDescending(f => f.LastWriteTimeUtc).Skip(8)) old.Delete();
			}
			catch (Exception ex) { Logger.Log("Live tile cache: " + ex.GetType().Name); }
			finally { try { if (File.Exists(temporary)) File.Delete(temporary); } catch { } }
		}
	}
	internal static void Delete(string key)
	{
		lock (Gate) { try { File.Delete(PathFor(key)); } catch { } }
	}
}
