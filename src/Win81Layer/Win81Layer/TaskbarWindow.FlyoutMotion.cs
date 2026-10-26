using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace Win81Layer;

// The clock, volume and network flyouts share one motion (Motion tokens): open = a 120 ms linear fade and a 12 DIP rise
// from the taskbar edge over 250 ms DECELERATE; close = a 100 ms linear fade and 6 DIP toward the taskbar with ACCELERATE.
// The clock and volume flyouts pass their inner content (as the Action Center does): their frame and body appear at once
// at full opacity, clipped, and only the content moves and fades. The network flyout still passes its whole root.
// No scale and no BitmapCache (the scale cost about 1.4x CPU per frame and the cache saved nothing consistently). Calm
// motion travels half as far (6 DIP in, 3 DIP out); Off shows and hides at once. One-shot clocks over exact base values.
public partial class TaskbarWindow
{
	private const double FlyoutEnterDistance = 12.0;

	private const double FlyoutExitDistance = 6.0;

	// A vector of px DIP pointing from the flyout toward the taskbar (the side the flyout opens from).
	private System.Windows.Vector TowardTaskbar(double px)
	{
		return _position switch
		{
			"Top" => new System.Windows.Vector(0.0, -px),
			"Left" => new System.Windows.Vector(-px, 0.0),
			"Right" => new System.Windows.Vector(px, 0.0),
			_ => new System.Windows.Vector(0.0, px)
		};
	}

	// Before IsOpen: hidden at the entrance's start position, so the popup's first frame is already transparent.
	private void FlyoutPrepare(FrameworkElement root, TranslateTransform slide)
	{
		root.BeginAnimation(UIElement.OpacityProperty, null);
		slide.BeginAnimation(TranslateTransform.XProperty, null);
		slide.BeginAnimation(TranslateTransform.YProperty, null);
		root.CacheMode = null;
		bool animate = Motion.Enabled;
		System.Windows.Vector start = TowardTaskbar(Motion.Distance(FlyoutEnterDistance));
		root.Opacity = animate ? 0.0 : 1.0;
		slide.X = animate ? start.X : 0.0;
		slide.Y = animate ? start.Y : 0.0;
	}

	// The entrance from wherever the flyout is now (its prepared start, or mid-close when it is reopened), ending exactly
	// on opacity 1 and offset 0. settled runs once the entrance is over (or at once when there is none).
	private void FlyoutEnter(FrameworkElement root, TranslateTransform slide, Func<bool> stillCurrent, Action? settled)
	{
		double fromOpacity = root.Opacity;
		double fromX = slide.X;
		double fromY = slide.Y;
		root.BeginAnimation(UIElement.OpacityProperty, null);
		slide.BeginAnimation(TranslateTransform.XProperty, null);
		slide.BeginAnimation(TranslateTransform.YProperty, null);
		root.CacheMode = null;
		root.Opacity = 1.0;
		slide.X = 0.0;
		slide.Y = 0.0;
		if (!Motion.Enabled)
		{
			settled?.Invoke();
			return;
		}
		DoubleAnimation fade = Motion.Anim(Math.Clamp(fromOpacity, 0.0, 1.0), 1.0, Motion.Tok.FadeIn);
		bool moves = Math.Abs(fromX) > 0.01 || Math.Abs(fromY) > 0.01;
		if (moves)
		{
			// The token comes from the designed 12 DIP travel, not from the (calm-halved) distance: calm motion rises 6 DIP
			// over the same 250 ms instead of switching to the shorter MoveMicro.
			Motion.Tok move = Motion.Move(FlyoutEnterDistance);
			DoubleAnimation sx = Motion.Anim(fromX, 0.0, move);
			DoubleAnimation sy = Motion.Anim(fromY, 0.0, move);
			sy.Completed += delegate
			{
				if (stillCurrent())
				{
					settled?.Invoke();
				}
			};
			slide.BeginAnimation(TranslateTransform.XProperty, sx, HandoffBehavior.SnapshotAndReplace);
			slide.BeginAnimation(TranslateTransform.YProperty, sy, HandoffBehavior.SnapshotAndReplace);
		}
		else
		{
			fade.Completed += delegate
			{
				if (stillCurrent())
				{
					settled?.Invoke();
				}
			};
		}
		root.BeginAnimation(UIElement.OpacityProperty, fade, HandoffBehavior.SnapshotAndReplace);
	}

	// The exit: fade out over 100 ms linear while moving 6 DIP toward the taskbar with ACCELERATE; done runs at the end.
	private void FlyoutExit(FrameworkElement root, TranslateTransform slide, Action done)
	{
		root.CacheMode = null;
		System.Windows.Vector to = TowardTaskbar(Motion.Distance(FlyoutExitDistance));
		DoubleAnimation fade = new DoubleAnimation(0.0, Motion.Dur(Motion.Tok.FadeOut))
		{
			EasingFunction = Motion.Ease(Motion.Tok.FadeOut)
		};
		fade.Completed += delegate
		{
			done();
		};
		Duration move = Motion.Dur(Motion.Tok.ExitMove);
		IEasingFunction accel = Motion.Ease(Motion.Tok.ExitMove);
		root.BeginAnimation(UIElement.OpacityProperty, fade, HandoffBehavior.SnapshotAndReplace);
		slide.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(to.X, move) { EasingFunction = accel }, HandoffBehavior.SnapshotAndReplace);
		slide.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(to.Y, move) { EasingFunction = accel }, HandoffBehavior.SnapshotAndReplace);
	}

	// Refresh results that land while a flyout is still entering are kept (the latest wins) and applied once the entrance
	// is over, at Background priority, so no rows are rebuilt during the entrance.
	private Action? _volumeEnterPending;

	private Action? _networkEnterPending;

	private bool _volumeEntering;

	private bool _networkEntering;

	private void ApplyAfterVolumeEntrance(Action apply)
	{
		if (_volumeEntering)
		{
			_volumeEnterPending = apply;
			return;
		}
		apply();
	}

	private void ApplyAfterNetworkEntrance(Action apply)
	{
		if (_networkEntering)
		{
			_networkEnterPending = apply;
			return;
		}
		apply();
	}

	private void FlushVolumeEntrance()
	{
		_volumeEntering = false;
		Action? pending = _volumeEnterPending;
		_volumeEnterPending = null;
		if (pending != null)
		{
			Dispatcher.BeginInvoke(DispatcherPriority.Background, (Action)delegate
			{
				if (VolumePopup.IsOpen && !_stopped)
				{
					pending();
				}
			});
		}
	}

	private void FlushNetworkEntrance()
	{
		_networkEntering = false;
		Action? pending = _networkEnterPending;
		_networkEnterPending = null;
		if (pending != null)
		{
			Dispatcher.BeginInvoke(DispatcherPriority.Background, (Action)delegate
			{
				if (NetPopup.IsOpen && !_stopped)
				{
					pending();
				}
			});
		}
	}

	// The volume and network bodies: a short fade into the footer while more content is below (the bottom fold then reads
	// as scrollable rather than as a cut-off line). Event-driven (scroll, size and extent changes), nothing at idle.
	private void OnFlyoutBodyScrollChanged(object sender, System.Windows.Controls.ScrollChangedEventArgs e)
	{
		System.Windows.Controls.ScrollViewer? scroll = sender as System.Windows.Controls.ScrollViewer;
		System.Windows.Shapes.Rectangle? fade = ReferenceEquals(scroll, VolumeBodyScroll) ? VolumeBodyFade
			: (ReferenceEquals(scroll, NetworkBodyScroll) ? NetworkBodyFade : null);
		if (scroll == null || fade == null)
		{
			return;
		}
		Visibility want = scroll.ScrollableHeight - scroll.VerticalOffset > 0.5 ? Visibility.Visible : Visibility.Collapsed;
		if (want == Visibility.Visible)
		{
			// the fade stops where the scrollbar's track starts: whatever the viewport and the padding leave of the width
			// (the track is wider than its 14 DIP style when the system scrollbar minimum applies); the volume body's slim
			// bar lies over its content, 10 DIP at the right edge
			double right = ReferenceEquals(scroll, VolumeBodyScroll)
				? (scroll.ComputedVerticalScrollBarVisibility == Visibility.Visible ? 10.0 : 0.0)
				: scroll.ActualWidth - scroll.ViewportWidth - scroll.Padding.Left - scroll.Padding.Right;
			right = double.IsNaN(right) || right < 0.0 ? 14.0 : right;
			if (Math.Abs(fade.Margin.Right - right) > 0.1)
			{
				fade.Margin = new Thickness(0.0, 0.0, right, 0.0);
			}
		}
		if (fade.Visibility != want)
		{
			fade.Visibility = want;
		}
	}

	// A footer tile's label is whole: its Auto row holds the text and the icon row above still has at least 14 DIP (the
	// icon shrinks first, so a label is only clipped when even that is gone).
	private static bool ActionTileLabelFits(System.Windows.Controls.Button button)
	{
		if (button.Content is not System.Windows.Controls.Grid content || content.RowDefinitions.Count < 2)
		{
			return false;
		}
		System.Windows.Controls.TextBlock? label = null;
		foreach (UIElement child in content.Children)
		{
			if (child is System.Windows.Controls.TextBlock text && System.Windows.Controls.Grid.GetRow(text) == 1)
			{
				label = text;
				break;
			}
		}
		return label != null
			&& content.RowDefinitions[1].ActualHeight + 0.5 >= label.DesiredSize.Height
			&& content.RowDefinitions[0].ActualHeight >= 14.0;
	}

	// "Updating..." only when a refresh takes longer than 300 ms (a quick refresh shows nothing).
	private DispatcherTimer? ShowStatusLater(System.Windows.Controls.TextBlock status, string text, Func<bool> stillBusy)
	{
		DispatcherTimer timer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher)
		{
			Interval = TimeSpan.FromMilliseconds(300.0)
		};
		timer.Tick += delegate
		{
			timer.Stop();
			if (stillBusy())
			{
				status.Text = text;
				status.Visibility = Visibility.Visible;
			}
		};
		timer.Start();
		return timer;
	}

	// The tray right-click command popups: the menu entrance (83 ms linear fade, 4 DIP away from the taskbar over 167 ms
	// DECELERATE) on their own slide transform; calm motion fades only.
	private void PlayCommandPopupEntrance(FrameworkElement root, TranslateTransform slide)
	{
		root.BeginAnimation(UIElement.OpacityProperty, null);
		slide.BeginAnimation(TranslateTransform.XProperty, null);
		slide.BeginAnimation(TranslateTransform.YProperty, null);
		root.CacheMode = null;
		root.Opacity = 1.0;
		slide.X = 0.0;
		slide.Y = 0.0;
		if (!Motion.Enabled)
		{
			return;
		}
		System.Windows.Vector start = TowardTaskbar(Motion.Distance(MenuMotion.Distance));
		root.BeginAnimation(UIElement.OpacityProperty, Motion.Anim(0.0, 1.0, Motion.Tok.FadeInShort), HandoffBehavior.SnapshotAndReplace);
		if (Math.Abs(start.X) > 0.01)
		{
			slide.BeginAnimation(TranslateTransform.XProperty, Motion.Anim(start.X, 0.0, Motion.Tok.MoveMicro), HandoffBehavior.SnapshotAndReplace);
		}
		if (Math.Abs(start.Y) > 0.01)
		{
			slide.BeginAnimation(TranslateTransform.YProperty, Motion.Anim(start.Y, 0.0, Motion.Tok.MoveMicro), HandoffBehavior.SnapshotAndReplace);
		}
	}
}
