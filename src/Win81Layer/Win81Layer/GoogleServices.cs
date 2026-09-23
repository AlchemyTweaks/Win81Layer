#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Win81Layer;

public static class GoogleServices
{
	public sealed record MailInfo(int Unread, string Summary);

	public sealed record AgendaInfo(string TimeText, string Title);
	public sealed record AgendaEvent(string Title, DateTimeOffset Start, DateTimeOffset End, bool AllDay, string Location);
	public sealed record AgendaSnapshot(AgendaEvent[] Events, DateTimeOffset FetchedAt, bool Stale = false);
	public sealed record AgendaReadResult(AgendaSnapshot? Snapshot, string Message, bool NeedsSignIn = false);

	public static async Task<AgendaSnapshot?> GetAgendaSnapshotAsync(CancellationToken cancellation = default)
	{
		return (await ReadAgendaAsync(cancellation)).Snapshot;
	}

	public static Task<AgendaReadResult> ReadAgendaAsync(CancellationToken cancellation = default) => ReadAgendaAsync(GoogleAuth.Session, Http, cancellation);

	internal static async Task<AgendaReadResult> ReadAgendaAsync(GoogleAuthSession auth, HttpClient http, CancellationToken cancellation = default)
	{
		string? key = auth.AgendaCacheKey;
		string? token = await auth.GetAccessTokenAsync(true, cancellation);
		if (token == null) return new(null, auth.IsCalendarConnected ? "Google could not refresh this connection. Check your network and retry." : "Sign in to Google Calendar to load your agenda.", !auth.IsCalendarConnected);
		try
		{
			DateTimeOffset now = DateTimeOffset.Now;
			string url = "https://www.googleapis.com/calendar/v3/calendars/primary/events?singleEvents=true&orderBy=startTime&maxResults=20&timeMin="
				+ Uri.EscapeDataString(now.ToString("o", CultureInfo.InvariantCulture)) + "&timeMax=" + Uri.EscapeDataString(now.AddDays(7).ToString("o", CultureInfo.InvariantCulture));
			using HttpRequestMessage request = new(HttpMethod.Get, url);
			request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
			using HttpResponseMessage response = await http.SendAsync(request, cancellation);
			string body = await response.Content.ReadAsStringAsync(cancellation);
			if (key != auth.AgendaCacheKey) return new(null, "The Google connection changed. Refresh to load the current calendar.", !auth.IsCalendarConnected);
			if (!response.IsSuccessStatusCode) return CalendarError(response.StatusCode, body);
			using JsonDocument json = JsonDocument.Parse(body);
			if (!json.RootElement.TryGetProperty("items", out JsonElement items) || items.ValueKind != JsonValueKind.Array)
				return new(null, "Google returned an incomplete calendar response. Try refreshing.");
			AgendaSnapshot snapshot = ParseAgenda(items, now);
			return new(snapshot, snapshot.Events.Length == 0 ? "No upcoming events in the next 7 days." : "Primary calendar updated.");
		}
		catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { throw; }
		catch (Exception ex) { Logger.Log("Agenda refresh: " + ex.GetType().Name); return new(null, "Calendar is unavailable. Check your connection and try refreshing."); }
	}

	internal static AgendaReadResult CalendarError(HttpStatusCode status, string body)
	{
		string reason = "";
		try
		{
			using JsonDocument json = JsonDocument.Parse(body);
			if (json.RootElement.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object &&
				error.TryGetProperty("errors", out var errors) && errors.ValueKind == JsonValueKind.Array && errors.GetArrayLength() > 0)
				reason = GoogleAuthSession.Text(errors[0], "reason");
		}
		catch (JsonException) { }
		return reason switch
		{
			"accessNotConfigured" => new(null, "Enable Google Calendar API in the project that owns your Desktop OAuth client, then refresh."),
			"insufficientPermissions" => new(null, "Calendar permission is missing. Sign in again and approve read-only Calendar access.", true),
			"rateLimitExceeded" or "userRateLimitExceeded" or "quotaExceeded" => new(null, "Google Calendar's request limit was reached. Wait a few minutes, then refresh."),
			_ => status switch
			{
				HttpStatusCode.Unauthorized => new(null, "Google rejected this connection. Sign in again to reconnect Calendar.", true),
				HttpStatusCode.Forbidden => new(null, "Calendar access was denied. Check API enablement, consent permissions and your organization's policy."),
				HttpStatusCode.TooManyRequests => new(null, "Google Calendar is busy. Wait a few minutes, then refresh."),
				_ => new(null, "Google Calendar is unavailable. Try refreshing shortly.")
			}
		};
	}

	internal static AgendaSnapshot ParseAgenda(JsonElement items, DateTimeOffset now)
	{
		List<AgendaEvent> events = new();
		foreach (JsonElement item in items.EnumerateArray())
		{
			if (item.ValueKind != JsonValueKind.Object) continue;
			if (GoogleAuthSession.Text(item, "status") == "cancelled") continue;
			if (!item.TryGetProperty("start", out var start) || !item.TryGetProperty("end", out var end) || start.ValueKind != JsonValueKind.Object || end.ValueKind != JsonValueKind.Object) continue;
			bool allDay = start.TryGetProperty("date", out _);
			DateTimeOffset? ReadTime(JsonElement part)
			{
				if (DateTimeOffset.TryParse(GoogleAuthSession.Text(part, "dateTime"), CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed)) return parsed.ToLocalTime();
				if (DateTime.TryParseExact(GoogleAuthSession.Text(part, "date"), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day))
					return new DateTimeOffset(day, TimeZoneInfo.Local.GetUtcOffset(day));
				return null;
			}
			DateTimeOffset? from = ReadTime(start), to = ReadTime(end);
			if (!from.HasValue || !to.HasValue || to <= now || to <= from) continue;
			string Text(string name) => GoogleAuthSession.Text(item, name);
			events.Add(new AgendaEvent(string.IsNullOrWhiteSpace(Text("summary")) ? "Untitled event" : Text("summary"), from.Value, to.Value, allDay, Text("location")));
			if (events.Count == 20) break;
		}
		return new AgendaSnapshot(events.ToArray(), now.ToUniversalTime());
	}

	private static readonly HttpClient Http = new HttpClient
	{
		Timeout = TimeSpan.FromSeconds(12L)
	};

	public static async Task<MailInfo?> GetMailAsync()
	{
		string? token = await GoogleAuth.GetMailAccessTokenAsync();
		if (token == null)
		{
			return null;
		}
		try
		{
			using JsonDocument? label = await GetJson("https://gmail.googleapis.com/gmail/v1/users/me/labels/UNREAD", token);
			if (label == null) return null;
			int unread = ((label != null && label.RootElement.TryGetProperty("messagesUnread", out var mu)) ? mu.GetInt32() : 0);
			string summary = "No new mail";
			if (unread > 0)
			{
				using JsonDocument? list = await GetJson("https://gmail.googleapis.com/gmail/v1/users/me/messages?q=is:unread%20in:inbox&maxResults=1", token);
				if (list != null && list.RootElement.TryGetProperty("messages", out var msgs) && msgs.GetArrayLength() > 0)
				{
					string? id = msgs[0].GetProperty("id").GetString();
					using JsonDocument? msg = await GetJson("https://gmail.googleapis.com/gmail/v1/users/me/messages/" + id + "?format=metadata&metadataHeaders=From&metadataHeaders=Subject", token);
					if (msg != null)
					{
						string from = CleanFrom(Header(msg.RootElement, "From"));
						string subj = Header(msg.RootElement, "Subject");
						summary = (string.IsNullOrWhiteSpace(subj) ? from : (from + " — " + subj));
					}
				}
			}
			return new MailInfo(unread, summary);
		}
		catch (Exception ex)
		{
			Logger.Log("Gmail fetch failed: " + ex.Message);
			return null;
		}
	}

	public static async Task<AgendaInfo?> GetAgendaAsync()
	{
		string? token = await GoogleAuth.GetCalendarAccessTokenAsync();
		if (token == null)
		{
			return null;
		}
		try
		{
			DateTime now = DateTime.Now;
			string timeMin = now.ToString("yyyy-MM-ddTHH:mm:sszzz");
			string timeMax = now.Date.AddDays(1.0).ToString("yyyy-MM-ddTHH:mm:sszzz");
			string url = "https://www.googleapis.com/calendar/v3/calendars/primary/events?timeMin=" + Uri.EscapeDataString(timeMin) + "&timeMax=" + Uri.EscapeDataString(timeMax) + "&singleEvents=true&orderBy=startTime&maxResults=1";
			using JsonDocument? json = await GetJson(url, token);
			if (json == null || !json.RootElement.TryGetProperty("items", out var items) || items.GetArrayLength() == 0)
			{
				return null;
			}
			JsonElement ev = items[0];
			string title = (ev.TryGetProperty("summary", out var s) ? (s.GetString() ?? "(no title)") : "(no title)");
			string timeText = "All day";
			if (ev.TryGetProperty("start", out var start) && start.TryGetProperty("dateTime", out var dt) && DateTime.TryParse(dt.GetString(), out var when))
			{
				timeText = when.ToString("HH:mm");
			}
			return new AgendaInfo(timeText, title);
		}
		catch (Exception ex)
		{
			Logger.Log("Calendar fetch failed: " + ex.Message);
			return null;
		}
	}

	private static async Task<JsonDocument?> GetJson(string url, string token)
	{
		using HttpRequestMessage req = new HttpRequestMessage(HttpMethod.Get, url);
		req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
		using HttpResponseMessage resp = await Http.SendAsync(req);
		string body = await resp.Content.ReadAsStringAsync();
		if (!resp.IsSuccessStatusCode)
		{
			Logger.Log($"Google API {url} → {(int)resp.StatusCode}");
			return null;
		}
		return JsonDocument.Parse(body);
	}

	private static string Header(JsonElement msg, string name)
	{
		if (msg.TryGetProperty("payload", out var p) && p.TryGetProperty("headers", out var hs))
		{
			foreach (JsonElement h in hs.EnumerateArray())
			{
				JsonElement v;
				if (h.TryGetProperty("name", out var n) && string.Equals(n.GetString(), name, StringComparison.OrdinalIgnoreCase))
				{
					return h.TryGetProperty("value", out v) ? (v.GetString() ?? "") : "";
				}
			}
		}
		return "";
	}

	private static string CleanFrom(string from)
	{
		if (string.IsNullOrWhiteSpace(from))
		{
			return "";
		}
		int lt = from.IndexOf('<');
		if (lt > 0)
		{
			return from.Substring(0, lt).Trim().Trim('"');
		}
		if (lt == 0)
		{
			int gt = from.IndexOf('>');
			from = ((gt > 1) ? from.Substring(1, gt - 1) : from.Trim(new char[2] { '<', '>' }));
		}
		int at = from.IndexOf('@');
		return (at > 0) ? from.Substring(0, at) : from;
	}
}
