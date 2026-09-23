using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace Win81Layer;

internal sealed class SwitcherState
{
	private static readonly string StatePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Win81Layer", "switcher.json");

	private static readonly JsonSerializerOptions Opt = new JsonSerializerOptions
	{
		WriteIndented = true
	};

	public List<string> Order { get; set; } = new List<string>();

	public List<string> Pinned { get; set; } = new List<string>();

	public static SwitcherState Load()
	{
		try
		{
			if (File.Exists(StatePath))
			{
				return JsonSerializer.Deserialize<SwitcherState>(File.ReadAllText(StatePath)) ?? new SwitcherState();
			}
		}
		catch
		{
		}
		return new SwitcherState();
	}

	public void Save()
	{
		try
		{
			Directory.CreateDirectory(Path.GetDirectoryName(StatePath));
			File.WriteAllText(StatePath, JsonSerializer.Serialize(this, Opt));
		}
		catch (Exception ex)
		{
			Logger.Log("Switcher state save: " + ex.Message);
		}
	}

	public bool IsPinned(string? exe)
	{
		return exe != null && Pinned.Any((string p) => Same(p, exe));
	}

	public void TogglePin(string? exe)
	{
		if (!string.IsNullOrEmpty(exe))
		{
			int i = Pinned.FindIndex((string p) => Same(p, exe));
			if (i >= 0)
			{
				Pinned.RemoveAt(i);
			}
			else
			{
				Pinned.Add(exe);
			}
			Save();
		}
	}

	public int OrderIndex(string? exe)
	{
		if (exe == null)
		{
			return int.MaxValue;
		}
		int i = Order.FindIndex((string p) => Same(p, exe));
		return (i < 0) ? int.MaxValue : i;
	}

	public void SetOrder(IEnumerable<string?> exes)
	{
		Order = (from e in exes
			where !string.IsNullOrEmpty(e)
			select (e)).Distinct<string>(StringComparer.OrdinalIgnoreCase).ToList();
		Save();
	}

	private static bool Same(string a, string b)
	{
		return string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
	}
}
