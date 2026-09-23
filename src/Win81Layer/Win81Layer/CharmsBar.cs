using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Windows;
using System.Windows.Forms;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace Win81Layer;

public partial class CharmsBar : Window, IComponentConnector
{
	private bool _hiding;

	private readonly TranslateTransform _slide = new TranslateTransform();

	private bool _tracking;   // true while the right-edge pull gesture is progressively revealing the bar

	private readonly DispatcherTimer _proximityTimer;

	private int _awayTicks;

	private long _pointerGraceUntil;

	private bool _dismissOnPointerLeave;

	private Rectangle _activeScreenBounds;

	private CharmsClock? _clock;

	public event Action? StartRequested;

	public event Action? SearchRequested;

	public event Action? SettingsRequested;

	public event Action? DevicesRequested;

	public event Action? ShareRequested;

	public CharmsBar()
	{
		//IL_0010: Unknown result type (might be due to invalid IL or missing references)
		//IL_0015: Unknown result type (might be due to invalid IL or missing references)
		//IL_002c: Expected O, but got Unknown
		InitializeComponent();
		_proximityTimer = new DispatcherTimer
		{
			Interval = TimeSpan.FromMilliseconds(250L)
		};
		_proximityTimer.Tick += delegate
		{
			ProximityCheck();
		};
	}

	private void ProximityCheck()
	{
		if (Environment.TickCount64 < _pointerGraceUntil)
		{
			_awayTicks = 0;
			return;
		}
		if (base.IsMouseOver)
		{
			_awayTicks = 0;
			return;
		}
		if (IsPointerInSafeZone())
		{
			_awayTicks = 0;
		}
		else if (++_awayTicks >= 2)
		{
			Logger.Log("Charms: proximity auto-hide");
			HideCharms();
		}
	}

	private bool IsPointerInSafeZone()
	{
		System.Drawing.Point pointer = System.Windows.Forms.Cursor.Position;
		Rectangle bounds = _activeScreenBounds;
		if (bounds.IsEmpty)
		{
			bounds = Screen.FromPoint(pointer).Bounds;
		}
		return pointer.X >= (double)bounds.Right - (base.Width + 160.0) &&
			pointer.X < bounds.Right && pointer.Y >= bounds.Top && pointer.Y < bounds.Bottom;
	}

	public void ShowCharms(bool fromPointer = false)
	{
		//IL_007d: Unknown result type (might be due to invalid IL or missing references)
		//IL_0082: Unknown result type (might be due to invalid IL or missing references)
		//IL_00c8: Unknown result type (might be due to invalid IL or missing references)
		//IL_00cd: Unknown result type (might be due to invalid IL or missing references)
		ForegroundContext.Capture();
		bool wasVisible = base.IsVisible;
		_hiding = false;
		_dismissOnPointerLeave = fromPointer;
		base.ShowActivated = !fromPointer;
		Show();
		// Win7-Aero → translucent smoked-glass strip (wallpaper shows through); Win8.1/native → opaque flat strip.
		BarRoot.Background = new SolidColorBrush(ShellSkin.GlassOn
			? System.Windows.Media.Color.FromArgb(0xC4, 0x1C, 0x1C, 0x1C)
			: System.Windows.Media.Color.FromArgb(0xFF, 0x1C, 0x1C, 0x1C));
		ShellSkin.ApplyGlass(this);
		try
		{
			StartCharm.Foreground = new SolidColorBrush(StartAccent.Color());
		}
		catch
		{
		}
		Screen screen = Screen.FromPoint(System.Windows.Forms.Cursor.Position);
		Rectangle b = screen.Bounds;
		_activeScreenBounds = b;
		// Use the TARGET monitor's DPI (cursor's screen), not the window's current TransformToDevice (see MonitorDpi).
		double sx = MonitorDpi.ScaleFor(b);
		double sy = sx;
		if (sx <= 0.0)
		{
			sx = 1.0;
		}
		if (sy <= 0.0)
		{
			sy = 1.0;
		}
		base.Height = (double)b.Height / sy;
		base.Top = (double)b.Top / sy;
		base.Left = (double)b.Right / sx - base.Width;
		double taskbarDiu = (double)(b.Bottom - TaskbarWorkArea.Current(screen).Bottom) / sy;
		if (_clock == null)
		{
			_clock = new CharmsClock();
		}
		_clock.ShowAt(b, sx, sy, taskbarDiu);
		if (!fromPointer)
		{
			WindowUtil.ForceForeground(this);
		}
		Logger.Log($"Charms: show (fromPointer={fromPointer})");
		_awayTicks = 0;
		_pointerGraceUntil = fromPointer ? Environment.TickCount64 + 1000L : 0L;
		if (fromPointer)
		{
			_proximityTimer.Start();
		}
		else
		{
			_proximityTimer.Stop();
		}
		BarRoot.RenderTransform = _slide;
		if (!wasVisible)
		{
			// fresh open: snap off-screen first so the entrance still slides in
			_slide.BeginAnimation(TranslateTransform.XProperty, null);
			_slide.X = base.Width;
		}
		// omit From so a re-trigger mid-flight animates from the current position (interruptible)
		_slide.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(0.0, Motion.Dur(Motion.Cat.EdgeEnter))
		{
			EasingFunction = Motion.Ease(Motion.Cat.EdgeEnter)
		});
	}

	// Progressive reveal driven by the right-edge pull gesture. reveal 0 = fully hidden, 1 = fully revealed. On the
	// first call it positions + shows the bar on the cursor's monitor with NO entrance animation (the drag IS the
	// animation); every call then tracks the pointer by setting _slide.X directly. Called on the UI thread.
	public void TrackReveal(double reveal)
	{
		if (reveal < 0.0) reveal = 0.0;
		if (reveal > 1.0) reveal = 1.0;
		if (!_tracking)
		{
			_tracking = true;
			_hiding = false;
			_dismissOnPointerLeave = false;
			base.ShowActivated = false;
			Show();
			BarRoot.Background = new SolidColorBrush(ShellSkin.GlassOn
				? System.Windows.Media.Color.FromArgb(0xC4, 0x1C, 0x1C, 0x1C)
				: System.Windows.Media.Color.FromArgb(0xFF, 0x1C, 0x1C, 0x1C));
			ShellSkin.ApplyGlass(this);
			try { StartCharm.Foreground = new SolidColorBrush(StartAccent.Color()); } catch { }
			PositionCharmsOnCursorScreen();
			BarRoot.RenderTransform = _slide;
			_slide.BeginAnimation(TranslateTransform.XProperty, null);
			_slide.X = base.Width;
			_proximityTimer.Stop();
		}
		_slide.BeginAnimation(TranslateTransform.XProperty, null);
		_slide.X = base.Width * (1.0 - reveal);
	}

	// Release of the gesture: past the threshold -> complete the open (deliberate + persistent, like Win+C, so it is
	// dismissed by click-outside/Esc, NOT by the pointer leaving); below it -> slide smoothly back off-screen.
	public void FinishReveal(bool open)
	{
		_tracking = false;
		if (open)
		{
			ShowCharms(fromPointer: false);
		}
		else
		{
			HideCharms();
		}
	}

	// Position the bar flush to the right edge of the monitor under the cursor, DPI-aware, and place the clock.
	private void PositionCharmsOnCursorScreen()
	{
		Screen screen = Screen.FromPoint(System.Windows.Forms.Cursor.Position);
		Rectangle b = screen.Bounds;
		_activeScreenBounds = b;
		// Use the TARGET monitor's DPI (cursor's screen), not the window's current TransformToDevice (see MonitorDpi).
		double sx = MonitorDpi.ScaleFor(b);
		double sy = sx;
		if (sx <= 0.0) { sx = 1.0; }
		if (sy <= 0.0) { sy = 1.0; }
		base.Height = (double)b.Height / sy;
		base.Top = (double)b.Top / sy;
		base.Left = (double)b.Right / sx - base.Width;
		double taskbarDiu = (double)(b.Bottom - TaskbarWorkArea.Current(screen).Bottom) / sy;
		if (_clock == null)
		{
			_clock = new CharmsClock();
		}
		_clock.ShowAt(b, sx, sy, taskbarDiu);
	}

	public void HideCharms()
	{
		if (_hiding || !base.IsVisible)
		{
			_proximityTimer.Stop();
			return;
		}
		Logger.Log($"Charms: hide (visible={base.IsVisible})");
		_hiding = true;
		_dismissOnPointerLeave = false;
		_pointerGraceUntil = 0L;
		_proximityTimer.Stop();
		_clock?.HideAnimated();
		BarRoot.RenderTransform = _slide;
		// omit From so an interrupted show slides out from its current position
		DoubleAnimation slide = new DoubleAnimation(base.Width, Motion.Dur(Motion.Cat.EdgeExit))
		{
			EasingFunction = Motion.Ease(Motion.Cat.EdgeExit)
		};
		slide.Completed += delegate
		{
			if (_hiding)
			{
				Hide();
				_hiding = false;
			}
		};
		_slide.BeginAnimation(TranslateTransform.XProperty, slide);
	}

	public void ToggleCharms()
	{
		if (base.IsVisible)
		{
			HideCharms();
		}
		else
		{
			ShowCharms();
		}
	}

	private static void TryLaunch(string command, string? args = null)
	{
		try
		{
			ProcessStartInfo psi = ((args == null) ? new ProcessStartInfo(command)
			{
				UseShellExecute = true
			} : new ProcessStartInfo(command, args)
			{
				UseShellExecute = true
			});
			Process.Start(psi);
		}
		catch (Exception ex)
		{
			Logger.Log($"Charm launch '{command} {args}' failed: {ex.Message}");
		}
	}

	public void QaRender(string outPath)
	{
		//IL_003e: Unknown result type (might be due to invalid IL or missing references)
		//IL_0068: Unknown result type (might be due to invalid IL or missing references)
		Show();
		try
		{
			StartCharm.Foreground = new SolidColorBrush(StartAccent.Color());
		}
		catch
		{
		}
		double h = 560.0;
		_slide.BeginAnimation(TranslateTransform.XProperty, null);
		_slide.X = 0.0;
		BarRoot.RenderTransform = _slide;
		BarRoot.Measure(new System.Windows.Size(base.Width, h));
		BarRoot.Arrange(new Rect(0.0, 0.0, base.Width, h));
		BarRoot.UpdateLayout();
		RenderTargetBitmap rtb = new RenderTargetBitmap((int)base.Width, (int)h, 96.0, 96.0, PixelFormats.Pbgra32);
		rtb.Render(BarRoot);
		PngBitmapEncoder enc = new PngBitmapEncoder();
		enc.Frames.Add(BitmapFrame.Create(rtb));
		using (FileStream fs = File.Create(outPath))
		{
			enc.Save(fs);
		}
		Hide();
		Logger.Log("QaRender charms -> " + outPath);
	}

	private void OnSearch(object sender, RoutedEventArgs e)
	{
		HideCharms();
		SearchRequested?.Invoke();
	}

	private void OnShare(object sender, RoutedEventArgs e)
	{
		HideCharms();
		ShareRequested?.Invoke();
	}

	private void OnStart(object sender, RoutedEventArgs e)
	{
		HideCharms();
		StartRequested?.Invoke();
	}

	private void OnDevices(object sender, RoutedEventArgs e)
	{
		HideCharms();
		DevicesRequested?.Invoke();
	}

	private void OnSettings(object sender, RoutedEventArgs e)
	{
		HideCharms();
		SettingsRequested?.Invoke();
	}

	private void OnDeactivated(object? sender, EventArgs e)
	{
		Logger.Log("Charms: hide cause = Deactivated");
		HideCharms();
	}

	private void OnMouseLeave(object sender, System.Windows.Input.MouseEventArgs e)
	{
		// Keyboard invocations own focus and remain open until Esc, a command, or deactivation. MouseLeave is only the
		// fast pointer-corner dismissal path; applying it to Win+C made the bar disappear before the key was released.
		if (!_dismissOnPointerLeave)
		{
			return;
		}
		if (IsPointerInSafeZone())
		{
			Logger.Log("Charms: ignored stale MouseLeave inside pointer safe zone");
			return;
		}
		Logger.Log("Charms: hide cause = MouseLeave");
		HideCharms();
	}

	private void OnPreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
	{
		//IL_0002: Unknown result type (might be due to invalid IL or missing references)
		//IL_0009: Invalid comparison between Unknown and I4
		if ((int)e.Key == 13)
		{
			HideCharms();
			e.Handled = true;
		}
	}

	protected override void OnClosing(CancelEventArgs e)
	{
		e.Cancel = true;
		HideCharms();
	}
}
