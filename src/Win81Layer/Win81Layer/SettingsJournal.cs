using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

#nullable enable annotations

namespace Win81Layer;

// One change made in PC Settings: what it was before and after (the knob's value and the text shown), when, and the group
// it belongs to (Windows defaults, Reduce motion everywhere, Undo my changes) so a group is undone as one.
internal sealed class JournalEntry
{
	[JsonPropertyName("id")]
	public string Id { get; set; } = "";

	[JsonPropertyName("title")]
	public string Title { get; set; } = "";

	[JsonPropertyName("before")]
	public string Before { get; set; } = "";

	[JsonPropertyName("after")]
	public string After { get; set; } = "";

	[JsonPropertyName("beforeText")]
	public string BeforeText { get; set; } = "";

	[JsonPropertyName("afterText")]
	public string AfterText { get; set; } = "";

	[JsonPropertyName("utc")]
	public DateTime Utc { get; set; }

	[JsonPropertyName("group")]
	public string? Group { get; set; }

	[JsonPropertyName("groupTitle")]
	public string? GroupTitle { get; set; }

	// The scheme GUID of a power value, the device name of an audio default.
	[JsonPropertyName("context")]
	public string? Context { get; set; }

	// "knob" (a Windows setting: undone through its knob), "ui" (a launcher row: undone through its own control) or
	// "look" (a preset: undone through the preset's own apply path).
	[JsonPropertyName("kind")]
	public string Kind { get; set; } = "knob";

	[JsonIgnore]
	internal string Line => Title + "  " + BeforeText + " → " + AfterText;
}

// The recent changes of PC Settings: at most 50 entries, at most 14 days old, in %LOCALAPPDATA%\Win81Layer\
// pc-settings-journal.json (DurableStateStore "pc-settings-journal"); in memory only under read-only diagnostics.
// Changes to the same row within 10 s merge (a drag, a few clicks), and a row back at its first value leaves no entry.
internal static class SettingsJournal
{
	internal const int MaxEntries = 50;

	internal static readonly TimeSpan MaxAge = TimeSpan.FromDays(14);

	internal static readonly TimeSpan MergeWindow = TimeSpan.FromSeconds(10);

	private const string StateName = "pc-settings-journal";

	private static readonly object Gate = new object();

	private static List<JournalEntry>? _entries;

	internal static event Action? Changed;

	internal static string PrimaryPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Win81Layer", "pc-settings-journal.json");

	private static List<JournalEntry> Entries
	{
		get
		{
			if (_entries == null)
			{
				_entries = SettingsStore.ReadOnlyDiagnostics ? new List<JournalEntry>() : Load();
			}
			return _entries;
		}
	}

	internal static int Count
	{
		get
		{
			lock (Gate)
			{
				return Entries.Count;
			}
		}
	}

	// Newest first.
	internal static List<JournalEntry> Snapshot()
	{
		lock (Gate)
		{
			return Entries.AsEnumerable().Reverse().ToList();
		}
	}

	internal static JournalEntry? Last()
	{
		lock (Gate)
		{
			return Entries.Count == 0 ? null : Entries[^1];
		}
	}

	// The entries of one group (or the entry alone).
	internal static List<JournalEntry> GroupOf(JournalEntry e)
	{
		lock (Gate)
		{
			if (e.Group == null)
			{
				return Entries.Contains(e) ? new List<JournalEntry> { e } : new List<JournalEntry>();
			}
			return Entries.Where((JournalEntry x) => x.Group == e.Group).ToList();
		}
	}

	// Adds a change (now = the world's clock). Returns the entry that holds it (a merged one) or null when the row is back
	// at its first value.
	internal static JournalEntry? Add(JournalEntry entry, DateTime now)
	{
		JournalEntry? result;
		lock (Gate)
		{
			List<JournalEntry> list = Entries;
			entry.Utc = now;
			JournalEntry? last = list.Count > 0 ? list[^1] : null;
			if (entry.Group == null && last != null && last.Group == null && last.Kind == entry.Kind && string.Equals(last.Id, entry.Id, StringComparison.OrdinalIgnoreCase) && now - last.Utc <= MergeWindow)
			{
				last.After = entry.After;
				last.AfterText = entry.AfterText;
				last.Utc = now;
				if (string.Equals(last.Before, last.After, StringComparison.Ordinal))
				{
					list.RemoveAt(list.Count - 1);
					result = null;
				}
				else
				{
					result = last;
				}
			}
			else
			{
				list.Add(entry);
				result = entry;
			}
			Prune(list, now);
		}
		Save();
		Raise();
		return result;
	}

	internal static void Remove(IEnumerable<JournalEntry> entries)
	{
		lock (Gate)
		{
			foreach (JournalEntry e in entries.ToList())
			{
				Entries.Remove(e);
			}
		}
		Save();
		Raise();
	}

	// "Clear list": the journal only, nothing is reverted.
	internal static void Clear()
	{
		lock (Gate)
		{
			Entries.Clear();
		}
		Save();
		Raise();
	}

	// QA: a fresh, empty in-memory journal.
	internal static void QaReset()
	{
		lock (Gate)
		{
			_entries = new List<JournalEntry>();
		}
		Raise();
	}

	private static void Prune(List<JournalEntry> list, DateTime now)
	{
		list.RemoveAll((JournalEntry e) => now - e.Utc > MaxAge);
		while (list.Count > MaxEntries)
		{
			list.RemoveAt(0);
		}
	}

	private static void Raise()
	{
		try
		{
			Changed?.Invoke();
		}
		catch
		{
		}
	}

	private static List<JournalEntry> Load()
	{
		try
		{
			DurableStateReadResult r = DurableStateStore.Read(StateName, PrimaryPath, null, Validate);
			if (string.IsNullOrEmpty(r.Json))
			{
				return new List<JournalEntry>();
			}
			List<JournalEntry> list = JsonSerializer.Deserialize<List<JournalEntry>>(r.Json!) ?? new List<JournalEntry>();
			Prune(list, DateTime.UtcNow);
			return list;
		}
		catch (Exception ex)
		{
			Logger.Log("PC settings journal: load failed: " + ex.Message);
			return new List<JournalEntry>();
		}
	}

	private static bool Validate(string json)
	{
		try
		{
			return JsonSerializer.Deserialize<List<JournalEntry>>(json) != null;
		}
		catch
		{
			return false;
		}
	}

	private static void Save()
	{
		if (SettingsStore.ReadOnlyDiagnostics)
		{
			return;
		}
		string json;
		lock (Gate)
		{
			json = JsonSerializer.Serialize(Entries);
		}
		SettingsWorker.Run(delegate
		{
			try
			{
				Directory.CreateDirectory(Path.GetDirectoryName(PrimaryPath)!);
				DurableStateStore.Commit(StateName, PrimaryPath, json, Validate);
			}
			catch (Exception ex)
			{
				Logger.Log("PC settings journal: save failed: " + ex.Message);
			}
		});
	}
}
