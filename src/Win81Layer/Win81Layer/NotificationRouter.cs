using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Threading;
using Windows.UI.Notifications;
using Windows.UI.Notifications.Management;

namespace Win81Layer;

public sealed class NotificationRouter : IDisposable
{
	private UserNotificationListener? _listener;

	private readonly HashSet<uint> _seen = new HashSet<uint>();

	private DispatcherTimer? _timer;

	private bool _primed;

	private bool _started;

	private bool _polling;

	public async void Start()
	{
		try
		{
			_listener = UserNotificationListener.Current;
			UserNotificationListenerAccessStatus status = await _listener.RequestAccessAsync();
			Logger.Log($"[notif] listener access = {status}");
			if (status != UserNotificationListenerAccessStatus.Allowed)
			{
				_listener = null;
				return;
			}
			_started = true;
			_timer = new DispatcherTimer((DispatcherPriority)4)
			{
				Interval = TimeSpan.FromMilliseconds(2500L)   // was 1200: passive toast mirror; halves WinRT IPC + GC churn
			};
			_timer.Tick += delegate
			{
				Poll();
			};
			_timer.Start();
			Poll();
			Logger.Log("[notif] router started (polling)");
		}
		catch (Exception ex)
		{
			Exception ex2 = ex;
			Logger.Log("[notif] start failed: " + ex2.Message);
			_listener = null;
		}
	}

	private async void Poll()
	{
		if (_polling || (object)_listener == null)
		{
			return;
		}
		_polling = true;
		try
		{
			IReadOnlyList<UserNotification> notifs = await _listener.GetNotificationsAsync(NotificationKinds.Toast);
			HashSet<uint> present = new HashSet<uint>();
			foreach (UserNotification n in notifs)
			{
				present.Add(n.Id);
				if (_seen.Add(n.Id) && _primed)
				{
					Render(n);
				}
			}
			_seen.RemoveWhere((uint id) => !present.Contains(id));
			_primed = true;
		}
		catch (Exception ex)
		{
			Exception ex2 = ex;
			Logger.Log("[notif] poll failed: " + ex2.Message);
		}
		finally
		{
			_polling = false;
		}
	}

	private void Render(UserNotification notif)
	{
		try
		{
			// Respect Do-Not-Disturb / Quiet hours: when banners are suppressed, don't pop the shell's mirrored toast either
			// (the Action Center list still captures it via the listener). Without this the DND toggle silenced only OS banners.
			if (NativeBanner.IsSuppressed())
			{
				return;
			}
			string appName = "";
			try
			{
				appName = notif.AppInfo?.DisplayInfo?.DisplayName ?? "";
			}
			catch
			{
			}
			if (!(appName == "Win81Layer"))
			{
				IReadOnlyList<AdaptiveNotificationText> texts = (notif.Notification.Visual?.GetBinding(KnownNotificationBindings.ToastGeneric))?.GetTextElements();
				string title = ((texts != null && texts.Count > 0) ? texts[0].Text : appName);
				string body = ((texts != null && texts.Count > 1) ? string.Join("  ", from t in texts.Skip(1)
					select t.Text) : "");
				string time = notif.CreationTime.LocalDateTime.ToString("h:mm tt");
				Logger.Log("[notif] '" + appName + "' → " + title);
				ToastService.Show(null, string.IsNullOrEmpty(appName) ? "Notification" : appName, title, body, null, time);
			}
		}
		catch (Exception ex)
		{
			Logger.Log("[notif] render failed: " + ex.Message);
		}
	}

	public void Dispose()
	{
		try
		{
			DispatcherTimer? timer = _timer;
			if (timer != null)
			{
				timer.Stop();
			}
		}
		catch
		{
		}
		_timer = null;
		_started = false;
	}
}
