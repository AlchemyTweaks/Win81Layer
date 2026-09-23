using System;
using System.Drawing;
using System.IO;
using System.Windows;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Windows.Networking.Connectivity;

namespace Win81Layer;

public partial class CharmsClock : Window, IComponentConnector
{
	private readonly DispatcherTimer _tick;

	private readonly NetworkStatusChangedEventHandler _networkChanged;

	public CharmsClock()
	{
		//IL_0010: Unknown result type (might be due to invalid IL or missing references)
		//IL_0015: Unknown result type (might be due to invalid IL or missing references)
		//IL_0028: Expected O, but got Unknown
		InitializeComponent();
		_tick = new DispatcherTimer
		{
			Interval = TimeSpan.FromSeconds(1L)
		};
		_tick.Tick += delegate
		{
			UpdateNow();
		};
		_networkChanged = delegate
		{
			if (!base.Dispatcher.HasShutdownStarted)
			{
				base.Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(UpdateNet));
			}
		};
		NetworkInformation.NetworkStatusChanged += _networkChanged;
		base.Closed += delegate
		{
			NetworkInformation.NetworkStatusChanged -= _networkChanged;
		};
	}

	private void UpdateNow()
	{
		DateTime now = DateTime.Now;
		TimeText.Text = now.ToString("h:mm");
		DayText.Text = now.ToString("dddd");
		DateText.Text = now.ToString("MMMM d");
	}

	private void UpdateNet()
	{
		// Prefer the AUTHENTIC extracted Win8.1 network icon (pnidui) — the exact same source the tray / network flyout /
		// Action Center use — falling back to the vector renderer only if the asset library is absent. This makes the Charms
		// clock's network icon identical to everywhere else instead of the (slightly different) vector version.
		NetState81 st = NetState81.Read();
		NetImage.Source = Win81AssetResolver.NetworkImage(st, 40) ?? NetIcons81.For(st, 40, Colors.White);
	}

	public void ShowAt(Rectangle b, double sx, double sy, double taskbarDiu)
	{
		UpdateNow();
		UpdateNet();
		_tick.Start();
		Show();
		// Flat: opaque dark slab; Win7-Aero: translucent smoked-accent glass (plain alpha).
		Root.Background = (ShellSkin.GlassOn
			? ShellSkin.PanelBg()
			: new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(0xCC, 0, 0, 0)));
		UpdateLayout();
		base.Left = (double)b.Left / sx + 8.0;
		base.Top = (double)b.Bottom / sy - base.ActualHeight - taskbarDiu - 8.0;
		Root.Opacity = 0.0;
		Root.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0.0, 1.0, Motion.Dur(Motion.Cat.Micro))
		{
			EasingFunction = Motion.Ease(Motion.Cat.Micro)
		});
	}

	public void HideAnimated()
	{
		_tick.Stop();
		DoubleAnimation fade = new DoubleAnimation(Root.Opacity, 0.0, Motion.Dur(Motion.Cat.Exit))
		{
			EasingFunction = Motion.Ease(Motion.Cat.Exit)
		};
		fade.Completed += delegate
		{
			Hide();
		};
		Root.BeginAnimation(UIElement.OpacityProperty, fade);
	}

	public void HideNow()
	{
		_tick.Stop();
		Hide();
	}

	public void QaRender(string outPath)
	{
		UpdateNow();
		UpdateNet();
		Show();
		UpdateLayout();
		int w = (int)Math.Ceiling(Root.ActualWidth);
		int h = (int)Math.Ceiling(Root.ActualHeight);
		RenderTargetBitmap rtb = new RenderTargetBitmap(w, h, 96.0, 96.0, PixelFormats.Pbgra32);
		rtb.Render(Root);
		PngBitmapEncoder enc = new PngBitmapEncoder();
		enc.Frames.Add(BitmapFrame.Create(rtb));
		using (FileStream fs = File.Create(outPath))
		{
			enc.Save(fs);
		}
		Hide();
	}
}
