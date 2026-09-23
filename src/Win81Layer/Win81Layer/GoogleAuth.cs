#nullable enable
using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Win81Layer;

public enum GoogleAuthOutcome { Connected, NotConfigured, Busy, Cancelled, TimedOut, Denied, Failed, Configured }
public sealed record GoogleAuthResult(GoogleAuthOutcome Outcome, string Message)
{
	public bool Success => Outcome is GoogleAuthOutcome.Connected or GoogleAuthOutcome.Configured;
}

public static class GoogleAuth
{
	// Only the primary calendar is queried. Mail is a separate, explicit permission.
	public const string CalendarScope = "https://www.googleapis.com/auth/calendar.events.owned.readonly";
	public const string MailScope = "https://www.googleapis.com/auth/gmail.readonly";
	internal const string LegacyScopes = MailScope + " https://www.googleapis.com/auth/calendar.readonly";
	internal static readonly GoogleAuthSession Session = new(
		Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Win81Layer"),
		new HttpClient { Timeout = TimeSpan.FromSeconds(30) },
		url => Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }), LiveTileDataCache.Delete);

	/// <summary>Raised after credentials or grants change, including expiry. May run on a worker thread.</summary>
	public static event EventHandler? ConnectionChanged
	{
		add => Session.ConnectionChanged += value;
		remove => Session.ConnectionChanged -= value;
	}
	public static bool IsConfigured => Session.IsConfigured;
	public static bool IsSignedIn => Session.IsCalendarConnected || Session.IsMailConnected;
	public static bool IsCalendarConnected => Session.IsCalendarConnected;
	public static bool IsMailConnected => Session.IsMailConnected;
	public static bool HasCalendarAccess => Session.IsCalendarConnected;
	public static bool HasMailAccess => Session.IsMailConnected;
	public static string ConfigDir => Session.DirectoryPath;
	public static string ConfigFilePath => Session.ConfigPath;
	internal static string? AgendaCacheKey => Session.AgendaCacheKey;
	public static void EnsureConfigTemplate() => Session.EnsureConfigTemplate();
	public static GoogleAuthResult ImportDesktopCredentials(string path) => Session.ImportDesktopCredentials(path);
	public static void SignOut() => Session.SignOut();
	public static Task<GoogleAuthResult> SignInCalendarAsync(CancellationToken cancellation = default) => Session.SignInAsync(true, cancellation);
	// Existing tray/settings callers explicitly connect Mail AND Agenda. Preserve that contract.
	public static async Task<bool> SignInAsync() => (await Session.SignInAsync(false)).Success;
	public static Task<string?> GetAccessTokenAsync() => Session.GetAccessTokenAsync(false);
	public static Task<string?> GetCalendarAccessTokenAsync(CancellationToken cancellation = default) => Session.GetAccessTokenAsync(true, cancellation);
	public static Task<string?> GetMailAccessTokenAsync(CancellationToken cancellation = default) => Session.GetAccessTokenAsync(false, cancellation);
}

// Instance dependencies keep protocol QA isolated from the launcher, user's files and real browser.
internal sealed class GoogleAuthSession
{
	internal sealed record Config(string client_id, string client_secret);
	internal sealed record Grant(string RefreshToken, string Scopes, string ClientId, string CacheId);
	private sealed record Access(string Token, DateTimeOffset Expires, string CacheId);
	internal const string AuthEndpoint = "https://accounts.google.com/o/oauth2/v2/auth";
	internal const string TokenEndpoint = "https://oauth2.googleapis.com/token";
	private readonly object _gate = new();
	private readonly SemaphoreSlim _refreshGate = new(1, 1);
	private readonly HttpClient _http;
	private readonly Action<string> _openBrowser;
	private readonly Action<string> _deleteCache;
	private readonly Dictionary<string, Access> _access = new();
	private readonly Dictionary<string, (long Length, DateTime Modified, string ClientId, Grant? Grant)> _grants = new();
	private (long Length, DateTime Modified)? _configStamp;
	private Config? _config;
	private CancellationTokenSource? _signIn;
	private long _revision;
	internal event EventHandler? ConnectionChanged;
	internal string DirectoryPath { get; }
	internal string ConfigPath => Path.Combine(DirectoryPath, "google.json");
	private string LegacyTokenPath => Path.Combine(DirectoryPath, "google-token.dat");
	private string CalendarTokenPath => Path.Combine(DirectoryPath, "google-calendar-token.dat");
	internal bool IsConfigured { get { lock (_gate) return ReadConfig() != null; } }
	internal bool IsCalendarConnected { get { lock (_gate) return FindGrant(true) != null; } }
	internal bool IsMailConnected { get { lock (_gate) return FindGrant(false) != null; } }
	internal bool HasStoredGrant { get { lock (_gate) return File.Exists(CalendarTokenPath) || File.Exists(LegacyTokenPath); } }
	internal string? AgendaCacheKey { get { lock (_gate) return FindGrant(true) is { } grant ? "agenda:" + grant.Value.CacheId : null; } }

	internal GoogleAuthSession(string directory, HttpClient http, Action<string> openBrowser, Action<string> deleteCache)
	{
		DirectoryPath = directory; _http = http; _openBrowser = openBrowser; _deleteCache = deleteCache;
	}

	internal static Config ParseConfig(string json, bool desktopOnly = false)
	{
		using JsonDocument document = JsonDocument.Parse(json);
		JsonElement root = document.RootElement;
		if (root.ValueKind != JsonValueKind.Object || root.TryGetProperty("web", out _) || root.TryGetProperty("type", out _))
			throw new FormatException("Choose OAuth credentials for a Desktop app, not a web app or service account.");
		if (root.TryGetProperty("installed", out var installed)) root = installed;
		else if (desktopOnly) throw new FormatException("Download the JSON for an OAuth client with application type Desktop app.");
		string id = Text(root, "client_id").Trim(), secret = Text(root, "client_secret").Trim();
		if (id.Length > 512 || !id.EndsWith(".apps.googleusercontent.com", StringComparison.Ordinal) || id.Any(char.IsWhiteSpace))
			throw new FormatException("The file does not contain a valid Google Desktop client ID.");
		if (secret.Length > 1024) throw new FormatException("The Desktop client secret is invalid.");
		// Never follow auth_uri/token_uri from an imported document.
		return new Config(id, secret);
	}

	internal GoogleAuthResult ImportDesktopCredentials(string path)
	{
		try
		{
			if (new FileInfo(path).Length > 64 * 1024) return Failed("The credentials file is too large. Choose Google's downloaded Desktop JSON.");
			Config config = ParseConfig(File.ReadAllText(path), desktopOnly: true);
			lock (_gate)
			{
				if (_signIn != null) return new(GoogleAuthOutcome.Busy, "Cancel the current sign-in before importing credentials.");
				Config? previous = ReadConfig();
				if ((previous == null || previous.client_id != config.client_id) && (File.Exists(LegacyTokenPath) || File.Exists(CalendarTokenPath)))
					return Failed("Sign out of Google before importing a different OAuth client. This also disconnects Mail.");
				AtomicWrite(ConfigPath, JsonSerializer.SerializeToUtf8Bytes(config));
				_configStamp = null;
				_revision++;
				_access.Clear();
			}
			NotifyChanged();
			return new(GoogleAuthOutcome.Configured, "Desktop credentials imported. Ready to sign in.");
		}
		catch (FormatException ex) { return Failed(ex.Message); }
		catch (JsonException) { return Failed("This is not valid JSON. Choose the downloaded Google Desktop credentials file."); }
		catch (Exception ex) { LogFailure("credentials import", ex); return Failed("Could not read or save the credentials file. Check file access and try again."); }
	}

	internal void EnsureConfigTemplate()
	{
		try
		{
			lock (_gate)
			{
				if (!File.Exists(ConfigPath)) AtomicWrite(ConfigPath, Encoding.UTF8.GetBytes("{\n  \"client_id\": \"\",\n  \"client_secret\": \"\"\n}\n"));
			}
		}
		catch (Exception ex) { LogFailure("config template", ex); }
	}

	internal void SignOut()
	{
		Exception? failure = null;
		lock (_gate)
		{
			_revision++;
			_signIn?.Cancel();
			DeleteAgendaCache();
			_access.Clear();
			_grants.Clear();
			foreach (string path in new[] { CalendarTokenPath, LegacyTokenPath })
				try { File.Delete(path); } catch (Exception ex) { failure = ex; }
		}
		NotifyChanged();
		if (failure != null) throw new IOException("Google could not be fully signed out. Check access to the local token files and retry.", failure);
	}

	internal async Task<GoogleAuthResult> SignInAsync(bool calendarOnly, CancellationToken cancellation = default, TimeSpan? timeout = null)
	{
		using CancellationTokenSource attempt = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
		attempt.CancelAfter(timeout ?? TimeSpan.FromMinutes(2));
		Config config;
		long revision;
		lock (_gate)
		{
			if (_signIn != null) return new(GoogleAuthOutcome.Busy, "Google sign-in is already open. Complete it or cancel it first.");
			Config? loaded = ReadConfig();
			if (loaded == null) return new(GoogleAuthOutcome.NotConfigured, "Import Google Desktop OAuth credentials first.");
			config = loaded; revision = _revision; _signIn = attempt;
		}
		try
		{
			attempt.Token.ThrowIfCancellationRequested();
			string verifier = RandomUrlToken(), state = RandomUrlToken();
			string scopes = calendarOnly ? GoogleAuth.CalendarScope : GoogleAuth.LegacyScopes;
			using HttpListener listener = CreateListener(out string redirect);
			_openBrowser(BuildAuthorizationUrl(config.client_id, redirect, scopes, verifier, state));
			string code;
			while (true)
			{
				HttpListenerContext context = await listener.GetContextAsync().WaitAsync(attempt.Token).ConfigureAwait(false);
				bool valid = context.Request.HttpMethod == "GET" && context.Request.Url?.AbsolutePath == "/" &&
					context.Request.Url?.Host == "127.0.0.1" && ValidState(context.Request.QueryString, state);
				if (!valid) { Reply(context, "This response could not be verified. Continue sign-in from the launcher.", 400); continue; }
				string? error = SingleValue(context.Request.QueryString, "error");
				string? responseCode = SingleValue(context.Request.QueryString, "code");
				if (error != null)
				{
					Reply(context, "Authorization was not completed. Return to the launcher.", 200);
					return error == "access_denied" ? new(GoogleAuthOutcome.Denied, "Google permission was not granted. You can try again.") : Failed(OAuthError(error));
				}
				if (string.IsNullOrWhiteSpace(responseCode)) { Reply(context, "The authorization response was incomplete. Return to the launcher.", 400); continue; }
				code = responseCode;
				Reply(context, "Authorization received. Return to the launcher to finish connecting.", 200);
				break;
			}
			listener.Stop();
			Dictionary<string, string> form = new()
			{
				["code"] = code, ["client_id"] = config.client_id, ["redirect_uri"] = redirect,
				["grant_type"] = "authorization_code", ["code_verifier"] = verifier
			};
			if (config.client_secret.Length > 0) form["client_secret"] = config.client_secret;
			using FormUrlEncodedContent content = new(form);
			using HttpResponseMessage response = await _http.PostAsync(TokenEndpoint, content, attempt.Token).ConfigureAwait(false);
			using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(attempt.Token).ConfigureAwait(false));
			JsonElement root = document.RootElement;
			if (!response.IsSuccessStatusCode) return Failed(OAuthError(Text(root, "error")));
			string accessToken = Text(root, "access_token"), refreshToken = Text(root, "refresh_token");
			string granted = root.TryGetProperty("scope", out _) ? Text(root, "scope") : scopes;
			if (accessToken.Length == 0 || refreshToken.Length == 0)
				return Failed("Google did not return an offline grant. Retry sign-in and approve access to keep the tile updated.");
			if (!HasCalendarScope(granted) || (!calendarOnly && !HasScope(granted, GoogleAuth.MailScope)))
				return new(GoogleAuthOutcome.Denied, "The required read-only permission was not granted. Select it on Google's consent screen and retry.");
			Grant grant = new(refreshToken, granted, config.client_id, Guid.NewGuid().ToString("N"));
			lock (_gate)
			{
				attempt.Token.ThrowIfCancellationRequested();
				if (revision != _revision || ReadConfig() != config) return new(GoogleAuthOutcome.Cancelled, "The connection changed. Start sign-in again.");
				DeleteAgendaCache();
				string path = calendarOnly ? CalendarTokenPath : LegacyTokenPath;
				SaveGrant(path, grant);
				_access[path] = new(accessToken, Expiry(root), grant.CacheId);
				_revision++;
			}
			NotifyChanged();
			return new(GoogleAuthOutcome.Connected, calendarOnly ? "Google Calendar connected." : "Google Mail and Calendar connected.");
		}
		catch (OperationCanceledException)
		{
			bool cancelled;
			lock (_gate) cancelled = cancellation.IsCancellationRequested || revision != _revision;
			return cancelled ? new(GoogleAuthOutcome.Cancelled, "Sign-in cancelled.") : new(GoogleAuthOutcome.TimedOut, "Sign-in timed out. Try again and complete consent in the browser.");
		}
		catch (HttpListenerException ex) { LogFailure("callback listener", ex); return Failed("The local sign-in listener could not start. Check local security settings and retry."); }
		catch (Exception ex) { LogFailure("sign-in", ex); return Failed("Google sign-in could not finish. Check your connection and browser, then retry."); }
		finally { lock (_gate) if (ReferenceEquals(_signIn, attempt)) _signIn = null; }
	}

	internal async Task<string?> GetAccessTokenAsync(bool calendar, CancellationToken cancellation = default)
	{
		await _refreshGate.WaitAsync(cancellation).ConfigureAwait(false);
		try
		{
			Config config;
			Grant grant;
			string path;
			long revision;
			lock (_gate)
			{
				Config? loaded = ReadConfig();
				var found = FindGrant(calendar);
				if (loaded == null || found == null) return null;
				config = loaded; grant = found.Value.Value; path = found.Value.Path; revision = _revision;
				if (_access.TryGetValue(path, out var current) && current.CacheId == grant.CacheId && current.Expires > DateTimeOffset.UtcNow) return current.Token;
			}
			Dictionary<string, string> form = new() { ["client_id"] = config.client_id, ["refresh_token"] = grant.RefreshToken, ["grant_type"] = "refresh_token" };
			if (config.client_secret.Length > 0) form["client_secret"] = config.client_secret;
			using FormUrlEncodedContent content = new(form);
			using HttpResponseMessage response = await _http.PostAsync(TokenEndpoint, content, cancellation).ConfigureAwait(false);
			using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellation).ConfigureAwait(false));
			JsonElement root = document.RootElement;
			if (!response.IsSuccessStatusCode)
			{
				// Transient outages, invalid_client and quota failures must not destroy an offline grant.
				if (Text(root, "error") == "invalid_grant") InvalidateGrant(path, grant, revision);
				return null;
			}
			string token = Text(root, "access_token");
			if (token.Length == 0) return null;
			lock (_gate)
			{
				cancellation.ThrowIfCancellationRequested();
				if (revision != _revision || ReadConfig() != config) return null;
				_access[path] = new(token, Expiry(root), grant.CacheId);
				return token;
			}
		}
		catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { throw; }
		catch (Exception ex) { LogFailure("token refresh", ex); return null; }
		finally { _refreshGate.Release(); }
	}

	private void InvalidateGrant(string path, Grant grant, long revision)
	{
		lock (_gate)
		{
			if (revision != _revision) return;
			_deleteCache("agenda:" + grant.CacheId);
			_access.Remove(path);
			// Keep a disabled Calendar slot so an expired account never falls back to a different Mail account.
			if (path == CalendarTokenPath) SaveGrant(path, grant with { RefreshToken = "" });
			else File.Delete(path);
			_revision++;
		}
		NotifyChanged();
	}

	private Config? ReadConfig()
	{
		try
		{
			FileInfo file = new(ConfigPath);
			if (!file.Exists || file.Length > 64 * 1024) return null;
			var stamp = (file.Length, file.LastWriteTimeUtc);
			if (_configStamp == stamp) return _config;
			_config = ParseConfig(File.ReadAllText(ConfigPath)); _configStamp = stamp;
			return _config;
		}
		catch { return null; }
	}
	private (string Path, Grant Value)? FindGrant(bool calendar)
	{
		Config? config = ReadConfig();
		if (config == null) return null;
		foreach (string path in calendar && File.Exists(CalendarTokenPath) ? new[] { CalendarTokenPath } : new[] { LegacyTokenPath })
		{
			Grant? grant = LoadGrant(path, config);
			if (grant != null && (calendar ? HasCalendarScope(grant.Scopes) : HasScope(grant.Scopes, GoogleAuth.MailScope))) return (path, grant);
		}
		return null;
	}
	private Grant? LoadGrant(string path, Config config)
	{
		try
		{
			FileInfo file = new(path);
			if (!file.Exists || file.Length > 64 * 1024) return null;
			if (_grants.TryGetValue(path, out var cached) && cached.Length == file.Length && cached.Modified == file.LastWriteTimeUtc && cached.ClientId == config.client_id) return cached.Grant;
			byte[] bytes = File.ReadAllBytes(path);
			string text = Encoding.UTF8.GetString(ProtectedData.Unprotect(bytes, null, DataProtectionScope.CurrentUser));
			Grant? grant = text.StartsWith("{", StringComparison.Ordinal) ? JsonSerializer.Deserialize<Grant>(text) :
				new Grant(text, GoogleAuth.LegacyScopes, config.client_id, Convert.ToHexString(SHA256.HashData(bytes)));
			if (grant == null || string.IsNullOrWhiteSpace(grant.RefreshToken) || string.IsNullOrWhiteSpace(grant.CacheId) || grant.ClientId != config.client_id) grant = null;
			_grants[path] = (file.Length, file.LastWriteTimeUtc, config.client_id, grant);
			return grant;
		}
		catch { return null; }
	}
	private void SaveGrant(string path, Grant grant)
	{
		AtomicWrite(path, ProtectedData.Protect(JsonSerializer.SerializeToUtf8Bytes(grant), null, DataProtectionScope.CurrentUser));
		_grants.Remove(path);
	}
	private static void AtomicWrite(string path, byte[] bytes)
	{
		Directory.CreateDirectory(Path.GetDirectoryName(path)!);
		string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
		try { File.WriteAllBytes(temporary, bytes); File.Move(temporary, path, true); }
		finally { if (File.Exists(temporary)) File.Delete(temporary); }
	}
	private void DeleteAgendaCache()
	{
		if (ReadConfig() is { } config)
			foreach (string path in new[] { CalendarTokenPath, LegacyTokenPath })
				if (LoadGrant(path, config) is { } grant) _deleteCache("agenda:" + grant.CacheId);
		_deleteCache("agenda");
	}
	private void NotifyChanged()
	{
		if (ConnectionChanged is not { } handlers) return;
		foreach (EventHandler handler in handlers.GetInvocationList())
			try { handler(this, EventArgs.Empty); } catch (Exception ex) { LogFailure("connection notification", ex); }
	}
	internal static bool HasScope(string scopes, string scope) => (scopes ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries).Contains(scope, StringComparer.Ordinal);
	internal static bool HasCalendarScope(string scopes) => new[] { GoogleAuth.CalendarScope, "https://www.googleapis.com/auth/calendar.events.readonly", "https://www.googleapis.com/auth/calendar.readonly", "https://www.googleapis.com/auth/calendar" }.Any(scope => HasScope(scopes, scope));
	internal static string Text(JsonElement root, string key) => root.ValueKind == JsonValueKind.Object && root.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";
	private static DateTimeOffset Expiry(JsonElement root) => DateTimeOffset.UtcNow.AddSeconds(root.TryGetProperty("expires_in", out var seconds) && seconds.TryGetInt32(out int value) ? Math.Clamp(value - 60, 0, 86400) : 3000);
	internal static string PkceChallenge(string verifier) => Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
	private static string RandomUrlToken() => Base64Url(RandomNumberGenerator.GetBytes(32));
	private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
	internal static string BuildAuthorizationUrl(string id, string redirect, string scopes, string verifier, string state)
	{
		Dictionary<string, string> parameters = new() { ["client_id"] = id, ["redirect_uri"] = redirect, ["response_type"] = "code", ["scope"] = scopes,
			["code_challenge"] = PkceChallenge(verifier), ["code_challenge_method"] = "S256", ["state"] = state, ["access_type"] = "offline", ["prompt"] = "consent select_account" };
		return AuthEndpoint + "?" + string.Join("&", parameters.Select(p => Uri.EscapeDataString(p.Key) + "=" + Uri.EscapeDataString(p.Value)));
	}
	internal static bool ValidState(NameValueCollection query, string expected)
	{
		string? state = SingleValue(query, "state");
		return state != null && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(state), Encoding.UTF8.GetBytes(expected));
	}
	private static string? SingleValue(NameValueCollection query, string key) => query.GetValues(key) is { Length: 1 } values ? values[0] : null;
	private static HttpListener CreateListener(out string redirect)
	{
		for (int retry = 0; ; retry++)
		{
			int port;
			using (TcpListener probe = new(IPAddress.Loopback, 0)) { probe.Start(); port = ((IPEndPoint)probe.LocalEndpoint).Port; }
			redirect = $"http://127.0.0.1:{port}/";
			HttpListener listener = new();
			listener.Prefixes.Add(redirect);
			try { listener.Start(); return listener; }
			catch (HttpListenerException) when (retry < 4) { listener.Close(); }
			catch { listener.Close(); throw; }
		}
	}
	private static void Reply(HttpListenerContext context, string message, int status)
	{
		try
		{
			byte[] body = Encoding.UTF8.GetBytes("<!doctype html><html lang=\"en\"><meta charset=\"utf-8\"><title>Win81Layer Google connection</title><body><h1>Google connection</h1><p>" + WebUtility.HtmlEncode(message) + "</p></body></html>");
			context.Response.StatusCode = status;
			context.Response.ContentType = "text/html; charset=utf-8";
			context.Response.Headers["Cache-Control"] = "no-store";
			context.Response.Headers["Content-Security-Policy"] = "default-src 'none'; frame-ancestors 'none'";
			context.Response.Headers["Referrer-Policy"] = "no-referrer";
			context.Response.ContentLength64 = body.Length;
			context.Response.OutputStream.Write(body);
		}
		catch (Exception ex) when (ex is HttpListenerException or IOException or ObjectDisposedException) { }
		finally { context.Response.Close(); }
	}
	private static GoogleAuthResult Failed(string message) => new(GoogleAuthOutcome.Failed, message);
	private static string OAuthError(string error) => error switch
	{
		"invalid_client" or "unauthorized_client" => "Google rejected the OAuth client. Import a current Desktop app JSON from Google Cloud.",
		"invalid_grant" => "This Google authorization expired or was revoked. Sign in again.",
		"admin_policy_enforced" or "org_internal" => "This Google account is restricted by its organization. Use an allowed account or contact its administrator.",
		"access_denied" => "Google permission was not granted. Check the project's test users and try again.",
		_ => "Google could not authorize this connection. Check the Desktop client, consent setup and connection, then retry."
	};
	private static void LogFailure(string operation, Exception ex) => Logger.Log("Google " + operation + ": " + ex.GetType().Name);
}
