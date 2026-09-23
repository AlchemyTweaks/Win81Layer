using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows;
using System.Windows.Forms;
using System.Windows.Media;
using System.Windows.Threading;

namespace Win81Layer;

public static class ToastService
{
	private static readonly List<ToastWindow> _stack = new List<ToastWindow>();

	public static void Show(ImageSource? icon, string appName, string title, string message, Action? onClick = null, string? timestamp = null)
	{
		try
		{
			System.Windows.Application app = System.Windows.Application.Current;
			if (((app != null) ? ((DispatcherObject)app).Dispatcher : null) == null)
			{
				return;
			}
			if (timestamp == null)
			{
				timestamp = DateTime.Now.ToString("h:mm tt");
			}
			((DispatcherObject)app).Dispatcher.BeginInvoke((Delegate)(Action)delegate
			{
				ToastWindow toast = new ToastWindow(icon, appName, title, message, onClick, timestamp);
				toast.Closed += delegate
				{
					_stack.Remove(toast);
					Reflow();
				};
				_stack.Add(toast);
				toast.ShowToast();
				Reflow();
			}, Array.Empty<object>());
		}
		catch (Exception ex)
		{
			Logger.Log("Toast show failed: " + ex.Message);
		}
	}

	private static void Reflow()
	{
		//IL_0072: Unknown result type (might be due to invalid IL or missing references)
		//IL_0077: Unknown result type (might be due to invalid IL or missing references)
		//IL_00c0: Unknown result type (might be due to invalid IL or missing references)
		//IL_00c5: Unknown result type (might be due to invalid IL or missing references)
		Screen screen = Screen.PrimaryScreen;
		if (screen == null)
		{
			return;
		}
		Rectangle wa = TaskbarWorkArea.Current(screen);
		double top = wa.Top + 8;
		foreach (ToastWindow t in _stack)
		{
			PresentationSource src = PresentationSource.FromVisual(t);
			double? num;
			Matrix transformToDevice;
			if (src == null)
			{
				num = null;
			}
			else
			{
				CompositionTarget compositionTarget = src.CompositionTarget;
				if (compositionTarget == null)
				{
					num = null;
				}
				else
				{
					transformToDevice = compositionTarget.TransformToDevice;
					num = transformToDevice.M11;
				}
			}
			double sx = num ?? 1.0;
			double? num2;
			if (src == null)
			{
				num2 = null;
			}
			else
			{
				CompositionTarget compositionTarget2 = src.CompositionTarget;
				if (compositionTarget2 == null)
				{
					num2 = null;
				}
				else
				{
					transformToDevice = compositionTarget2.TransformToDevice;
					num2 = transformToDevice.M22;
				}
			}
			double sy = num2 ?? 1.0;
			if (sx <= 0.0)
			{
				sx = 1.0;
			}
			if (sy <= 0.0)
			{
				sy = 1.0;
			}
			t.Left = (double)wa.Right / sx - t.ActualWidth - 4.0;
			t.Top = top / sy;
			top += t.ActualHeight * sy - 8.0;
		}
	}
}
