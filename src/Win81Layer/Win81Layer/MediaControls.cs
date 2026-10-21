using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Windows.Media.Control;

namespace Win81Layer;

public static class MediaControls
{
	private static GlobalSystemMediaTransportControlsSessionManager? _mgr;

	private static int _warming;

	private static readonly string[] Browsers = new string[6] { "chrome", "msedge", "firefox", "opera", "brave", "vivaldi" };

	private static GlobalSystemMediaTransportControlsSessionManager? Mgr
	{
		get
		{
			if ((object)_mgr == null)
			{
				Warm();
			}
			return _mgr;
		}
	}

	public static void Warm()
	{
		if ((object)_mgr != null || Interlocked.Exchange(ref _warming, 1) == 1)
		{
			return;
		}
		Task.Run(async delegate
		{
			try
			{
				_mgr = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
			}
			catch
			{
			}
			finally
			{
				Interlocked.Exchange(ref _warming, 0);
			}
		});
	}

	private static int _lookupBusy;

	// ForApp + Playing for a UI-thread caller (the task-button preview): every call on the session manager and its
	// sessions goes out of process to the system media broker, so the lookup runs on a worker and the caller waits at
	// most timeoutMs, getting no session (no media bar) when the broker is slow. One lookup at a time: a broker that
	// stopped answering holds one worker, and later callers return at once.
	public static (GlobalSystemMediaTransportControlsSession Session, bool Playing) LookupBounded(string exePath, int timeoutMs)
	{
		if (string.IsNullOrEmpty(exePath) || (object)Mgr == null)
		{
			return (null, false);
		}
		if (Interlocked.CompareExchange(ref _lookupBusy, 1, 0) != 0)
		{
			return (null, false);
		}
		Task<(GlobalSystemMediaTransportControlsSession, bool)> lookup;
		try
		{
			lookup = Task.Run(delegate
			{
				try
				{
					GlobalSystemMediaTransportControlsSession s = ForApp(exePath);
					return (s, (object)s != null && Playing(s));
				}
				finally
				{
					Interlocked.Exchange(ref _lookupBusy, 0);
				}
			});
		}
		catch
		{
			Interlocked.Exchange(ref _lookupBusy, 0);
			return (null, false);
		}
		try
		{
			return lookup.Wait(Math.Max(1, timeoutMs)) ? lookup.Result : (null, false);
		}
		catch
		{
			return (null, false);
		}
	}

	public static GlobalSystemMediaTransportControlsSession? ForApp(string? exePath)
	{
		if (string.IsNullOrEmpty(exePath))
		{
			return null;
		}
		GlobalSystemMediaTransportControlsSessionManager mgr = Mgr;
		if ((object)mgr == null)
		{
			return null;
		}
		string name = Path.GetFileNameWithoutExtension(exePath).ToLowerInvariant();
		bool isBrowser = Array.Exists(Browsers, (string b) => name.Contains(b));
		try
		{
			IReadOnlyList<GlobalSystemMediaTransportControlsSession> sessions = mgr.GetSessions();
			foreach (GlobalSystemMediaTransportControlsSession s in sessions)
			{
				string aumid = (s.SourceAppUserModelId ?? "").ToLowerInvariant();
				if (aumid.Length == 0)
				{
					continue;
				}
				if (!aumid.Contains(name))
				{
					string[] t = aumid.Split('.', '!', '_', ' ');
					if (t == null || t.Length <= 0 || !name.Contains(t[0]) || t[0].Length <= 2)
					{
						continue;
					}
				}
				return s;
			}
			if (isBrowser && sessions.Count > 0)
			{
				foreach (GlobalSystemMediaTransportControlsSession s2 in sessions)
				{
					if (Playing(s2))
					{
						return s2;
					}
				}
				return sessions[0];
			}
		}
		catch
		{
		}
		return null;
	}

	public static bool Playing(GlobalSystemMediaTransportControlsSession s)
	{
		try
		{
			return s.GetPlaybackInfo().PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
		}
		catch
		{
			return false;
		}
	}

	// The commands are started on a worker too: even starting one is a call to the media broker.
	public static void TogglePlayPause(GlobalSystemMediaTransportControlsSession s)
	{
		Task.Run(delegate
		{
			try
			{
				_ = s.TryTogglePlayPauseAsync();
			}
			catch
			{
			}
		});
	}

	public static void Next(GlobalSystemMediaTransportControlsSession s)
	{
		Task.Run(delegate
		{
			try
			{
				_ = s.TrySkipNextAsync();
			}
			catch
			{
			}
		});
	}

	public static void Previous(GlobalSystemMediaTransportControlsSession s)
	{
		Task.Run(delegate
		{
			try
			{
				_ = s.TrySkipPreviousAsync();
			}
			catch
			{
			}
		});
	}
}
