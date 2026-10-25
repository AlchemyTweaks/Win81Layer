using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace Win81Layer;

// One entrance for every menu and small popup (Win81, Metro81, Win7 child and legacy menus, the tray command popups,
// the overflow popups, the Settings-charm flyouts): an 83 ms linear fade and a 4 DIP slide away from the anchor over
// 167 ms DECELERATE (Motion tokens FadeInShort and MoveMicro). Menus close instantly (WPF destroys the popup), and
// hover/keyboard highlight stays instant. Calm motion (Reduced, Dark rest) keeps the fade and drops the 4 DIP slide;
// Off shows menus at once. Only Opacity and a cached TranslateTransform are animated, with one-shot clocks
// (FillBehavior.Stop over exact base values), so nothing stays alive after the entrance.
internal static class MenuMotion
{
	public enum MotionKind
	{
		Full,
		Fade,
		None
	}

	public const double Distance = 4.0;

	public static readonly DependencyProperty KindProperty = DependencyProperty.RegisterAttached("Kind", typeof(MotionKind), typeof(MenuMotion), new FrameworkPropertyMetadata(MotionKind.Full, FrameworkPropertyMetadataOptions.Inherits));

	public static MotionKind GetKind(DependencyObject d)
	{
		return (MotionKind)d.GetValue(KindProperty);
	}

	// Win7 child menus opt out of the slide (opacity only); None leaves a menu without an entrance.
	public static void SetKind(DependencyObject d, MotionKind kind)
	{
		d.SetValue(KindProperty, kind);
	}

	private static bool _hooked;

	// Registers the class handlers once, next to App.HookSecondaryMonitorPopupRendering, and keeps WPF's own popup
	// animation (150 ms fade/slide when Windows menu animation is on) from stacking on top of this entrance.
	internal static void Hook()
	{
		if (_hooked)
		{
			return;
		}
		_hooked = true;
		EventManager.RegisterClassHandler(typeof(ContextMenu), ContextMenu.OpenedEvent, new RoutedEventHandler(OnMenuOpened));
		EventManager.RegisterClassHandler(typeof(MenuItem), MenuItem.SubmenuOpenedEvent, new RoutedEventHandler(OnSubmenuOpened));
		try
		{
			ResourceDictionary res = Application.Current.Resources;
			res[SystemParameters.MenuPopupAnimationKey] = PopupAnimation.None;
			res[SystemParameters.ToolTipPopupAnimationKey] = PopupAnimation.None;
		}
		catch (Exception ex)
		{
			Logger.Log("MenuMotion: popup animation keys: " + ex.Message);
		}
	}

	// Opened fires right after the popup window is built and before its first frame: the start values are set here, in
	// the same dispatcher pass, so the first frame is already the first frame of the entrance.
	private static void OnMenuOpened(object sender, RoutedEventArgs e)
	{
		if (sender is not ContextMenu menu || !ReferenceEquals(e.OriginalSource, menu))
		{
			return;
		}
		try
		{
			MotionKind kind = GetKind(menu);
			if (kind == MotionKind.None || !Motion.Enabled)
			{
				Settle(menu);
				return;
			}
			double dy = kind == MotionKind.Full ? Motion.Distance(DirectionFromCursor(menu) * Distance) : 0.0;
			Play(menu, 0.0, dy);
		}
		catch (Exception ex)
		{
			Logger.Log("MenuMotion: menu entrance: " + ex.Message);
			Settle(menu);
		}
	}

	// +1 (the menu rises) when it opened above the cursor, -1 when below it; a menu opened from the keyboard (the cursor
	// is not next to it) comes down from above (-1).
	private static double DirectionFromCursor(FrameworkElement menu)
	{
		try
		{
			System.Drawing.Point c = System.Windows.Forms.Cursor.Position;
			Point tl = menu.PointToScreen(new Point(0.0, 0.0));
			Point br = menu.PointToScreen(new Point(Math.Max(1.0, menu.ActualWidth), Math.Max(1.0, menu.ActualHeight)));
			double slack = 24.0 * Math.Max(1.0, (br.X - tl.X) / Math.Max(1.0, menu.ActualWidth));
			bool nearX = c.X >= tl.X - slack && c.X <= br.X + slack;
			bool nearY = c.Y >= tl.Y - slack && c.Y <= br.Y + slack;
			if (!nearX || !nearY)
			{
				return -1.0;
			}
			return c.Y > (tl.Y + br.Y) * 0.5 ? 1.0 : -1.0;
		}
		catch
		{
			return -1.0;
		}
	}

	private static void OnSubmenuOpened(object sender, RoutedEventArgs e)
	{
		if (sender is not MenuItem item || !ReferenceEquals(e.OriginalSource, item))
		{
			return;
		}
		try
		{
			if (item.Template?.FindName("PART_Popup", item) is not Popup popup || popup.Child is not UIElement child)
			{
				return;
			}
			MotionKind kind = GetKind(item);
			// an opaque popup window would show black through a fading child: no entrance there
			if (kind == MotionKind.None || !Motion.Enabled || !popup.AllowsTransparency)
			{
				Settle(child);
				return;
			}
			// hidden until its position is known (Loaded runs after layout and the popup's placement)
			child.Opacity = 0.0;
			item.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, (Action)delegate
			{
				try
				{
					if (!popup.IsOpen)
					{
						Settle(child);
						return;
					}
					double dx = 0.0;
					if (kind == MotionKind.Full)
					{
						// -4 (slides right) when it opened to the right of its parent, +4 when it opened to the left
						Point childLeft = child.PointToScreen(new Point(0.0, 0.0));
						Point parentMid = item.PointToScreen(new Point(item.ActualWidth * 0.5, 0.0));
						dx = Motion.Distance(childLeft.X >= parentMid.X ? -Distance : Distance);
					}
					Play(child, dx, 0.0);
				}
				catch (Exception ex)
				{
					Logger.Log("MenuMotion: submenu entrance: " + ex.Message);
					Settle(child);
				}
			});
		}
		catch (Exception ex)
		{
			Logger.Log("MenuMotion: submenu: " + ex.Message);
		}
	}

	// The same entrance for a popup's root element, given its placement relative to the anchor: Top rises, Bottom comes
	// down, Left/Right slide outwards. Callers set PopupAnimation=None on the popup.
	internal static void PlayPopupEntrance(UIElement? child, PlacementMode placement)
	{
		if (child == null)
		{
			return;
		}
		double dx = 0.0;
		double dy = 0.0;
		switch (placement)
		{
		case PlacementMode.Top:
			dy = Distance;
			break;
		case PlacementMode.Left:
			dx = Distance;
			break;
		case PlacementMode.Right:
			dx = -Distance;
			break;
		default:
			dy = -Distance;
			break;
		}
		PlayPopupEntrance(child, dx, dy);
	}

	internal static void PlayPopupEntrance(UIElement? child, double dx, double dy)
	{
		if (child == null)
		{
			return;
		}
		try
		{
			if (!Motion.Enabled)
			{
				Settle(child);
				return;
			}
			Play(child, Motion.Distance(dx), Motion.Distance(dy));
		}
		catch (Exception ex)
		{
			Logger.Log("MenuMotion: popup entrance: " + ex.Message);
			Settle(child);
		}
	}

	// Base values first (opacity 1, offset 0), then one-shot clocks from the start values: the first frame shows the
	// start, the last frame lands exactly on the base values, and an interrupted entrance hands off smoothly.
	private static void Play(UIElement target, double dx, double dy)
	{
		target.Opacity = 1.0;
		target.BeginAnimation(UIElement.OpacityProperty, Motion.Anim(0.0, 1.0, Motion.Tok.FadeInShort), HandoffBehavior.SnapshotAndReplace);
		TranslateTransform? tt = (dx != 0.0 || dy != 0.0) ? CachedTranslate(target) : ExistingTranslate(target);
		if (tt == null)
		{
			return;
		}
		tt.X = 0.0;
		tt.Y = 0.0;
		if (dx != 0.0)
		{
			tt.BeginAnimation(TranslateTransform.XProperty, Motion.Anim(dx, 0.0, Motion.Move(dx)), HandoffBehavior.SnapshotAndReplace);
		}
		else
		{
			tt.BeginAnimation(TranslateTransform.XProperty, null);
		}
		if (dy != 0.0)
		{
			tt.BeginAnimation(TranslateTransform.YProperty, Motion.Anim(dy, 0.0, Motion.Move(dy)), HandoffBehavior.SnapshotAndReplace);
		}
		else
		{
			tt.BeginAnimation(TranslateTransform.YProperty, null);
		}
	}

	// Final values at once (Motion Off, a failure, a popup already closed).
	private static void Settle(UIElement target)
	{
		target.BeginAnimation(UIElement.OpacityProperty, null);
		target.Opacity = 1.0;
		TranslateTransform? tt = ExistingTranslate(target);
		if (tt != null)
		{
			tt.BeginAnimation(TranslateTransform.XProperty, null);
			tt.BeginAnimation(TranslateTransform.YProperty, null);
			tt.X = 0.0;
			tt.Y = 0.0;
		}
	}

	private static readonly DependencyProperty TranslateProperty = DependencyProperty.RegisterAttached("Translate", typeof(TranslateTransform), typeof(MenuMotion), new PropertyMetadata(null));

	private static TranslateTransform? ExistingTranslate(UIElement target)
	{
		return target.GetValue(TranslateProperty) as TranslateTransform;
	}

	// The element's own TranslateTransform, created once and reused; an element that already has another transform keeps
	// it and only fades.
	private static TranslateTransform? CachedTranslate(UIElement target)
	{
		if (target.GetValue(TranslateProperty) is TranslateTransform cached && ReferenceEquals(target.RenderTransform, cached))
		{
			return cached;
		}
		Transform current = target.RenderTransform;
		if (current != null && !ReferenceEquals(current, Transform.Identity) && !(current is MatrixTransform m && m.Matrix.IsIdentity))
		{
			return null;
		}
		TranslateTransform tt = new TranslateTransform();
		target.RenderTransform = tt;
		target.SetValue(TranslateProperty, tt);
		return tt;
	}

	// QA (--contextmenutest filmstrip): the menu entrance as controllable root clocks applied to an offscreen element, so
	// they can be seeked frame by frame without opening a popup.
	internal static (AnimationClock Opacity, AnimationClock Translate) CreateEntranceClocks(UIElement target, double dy)
	{
		target.Opacity = 1.0;
		TranslateTransform tt = CachedTranslate(target) ?? new TranslateTransform();
		tt.Y = 0.0;
		AnimationClock fade = Motion.Anim(0.0, 1.0, Motion.Tok.FadeInShort).CreateClock();
		AnimationClock move = Motion.Anim(dy, 0.0, Motion.Move(dy)).CreateClock();
		target.ApplyAnimationClock(UIElement.OpacityProperty, fade, HandoffBehavior.SnapshotAndReplace);
		tt.ApplyAnimationClock(TranslateTransform.YProperty, move, HandoffBehavior.SnapshotAndReplace);
		return (fade, move);
	}

	internal static double CurrentOffsetY(UIElement target)
	{
		return ExistingTranslate(target)?.Y ?? 0.0;
	}
}
