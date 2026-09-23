using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Forms;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace Win81Layer;

internal static class LockScreen
{
	private static readonly List<LockWindow> _windows = new List<LockWindow>();

	private static DispatcherTimer? _clock;

	private static bool _shown;

	public static void Show()
	{
		//IL_00fa: Unknown result type (might be due to invalid IL or missing references)
		//IL_00ff: Unknown result type (might be due to invalid IL or missing references)
		//IL_0112: Expected O, but got Unknown
		if (_shown)
		{
			return;
		}
		_shown = true;
		try
		{
			LockWindow primaryWin = null;
			Screen[] allScreens = Screen.AllScreens;
			foreach (Screen screen in allScreens)
			{
				LockWindow w = new LockWindow(screen.Primary);
				w.Dismissed += Hide;
				w.PlaceOn(screen);
				_windows.Add(w);
				if (screen.Primary)
				{
					primaryWin = w;
				}
			}
			if (primaryWin == null)
			{
				primaryWin = ((_windows.Count > 0) ? _windows[0] : null);
			}
			if (primaryWin != null)
			{
				WindowUtil.ForceForeground(primaryWin);
				primaryWin.Activate();
				primaryWin.Focus();
			}
			_clock = new DispatcherTimer
			{
				Interval = TimeSpan.FromSeconds(1L)
			};
			_clock.Tick += delegate
			{
				primaryWin?.UpdateClock();
			};
			_clock.Start();
			primaryWin?.UpdateClock();
			LoadStatusAsync(primaryWin);
			LoadBackgroundAsync();
		}
		catch (Exception ex)
		{
			Logger.Log("LockScreen.Show: " + ex.Message);
			Hide();
		}
	}

	private static async Task LoadBackgroundAsync()
	{
		try
		{
			LockWindow[] snapshot = _windows.ToArray();
			ImageSource bg = await Task.Run(delegate
			{
				Wallpapers.SeedFromPack();
				return ResolveBackground();
			});
			if (bg != null && _shown)
			{
				LockWindow[] array = snapshot;
				foreach (LockWindow w in array)
				{
					w.SetBackground(bg);
				}
			}
		}
		catch (Exception ex)
		{
			Exception ex2 = ex;
			Logger.Log("LockScreen bg load: " + ex2.Message);
		}
	}

	public static void Hide()
	{
		if (_shown || _windows.Count != 0)
		{
			_shown = false;
			DispatcherTimer? clock = _clock;
			if (clock != null)
			{
				clock.Stop();
			}
			_clock = null;
			LockWindow[] wins = _windows.ToArray();
			_windows.Clear();
			LockWindow[] array = wins;
			foreach (LockWindow w in array)
			{
				w.DismissAndClose();
			}
		}
	}

	private static async Task LoadStatusAsync(LockWindow? primary)
	{
		if (primary == null)
		{
			return;
		}
		try
		{
			primary.SetNetwork(NetState81.Read());
			primary.SetBattery(PowerStatus.Read());
			bool isSignedIn = GoogleAuth.IsSignedIn;
			bool flag = isSignedIn;
			GoogleServices.MailInfo m = default(GoogleServices.MailInfo);
			if (flag)
			{
				m = await GoogleServices.GetMailAsync();
				flag = (object)m != null;
			}
			if (flag)
			{
				primary.SetMail(m.Unread);
			}
		}
		catch (Exception ex)
		{
			Logger.Log("LockScreen status: " + ex.Message);
		}
	}

	private static ImageSource? ResolveBackground()
	{
		try
		{
			string path = TaskbarTheme.WallpaperPath();
			if (string.IsNullOrEmpty(path) || !File.Exists(path))
			{
				List<string> presets = Wallpapers.Presets();
				path = ((presets.Count > 0) ? presets[0] : null);
			}
			if (string.IsNullOrEmpty(path) || !File.Exists(path))
			{
				return null;
			}
			int w = 1920;
			try
			{
				w = Math.Max(1920, SystemInformation.PrimaryMonitorSize.Width);
			}
			catch
			{
			}
			BitmapImage bmp = new BitmapImage();
			bmp.BeginInit();
			bmp.UriSource = new Uri(path);
			bmp.DecodePixelWidth = w;
			bmp.CacheOption = BitmapCacheOption.OnLoad;
			bmp.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
			bmp.EndInit();
			((Freezable)bmp).Freeze();
			return bmp;
		}
		catch (Exception ex)
		{
			Logger.Log("LockScreen bg: " + ex.Message);
			return null;
		}
	}
}
