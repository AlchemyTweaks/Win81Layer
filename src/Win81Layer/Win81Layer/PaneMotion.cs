using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace Win81Layer;

// The charms bar and the charm panes (Settings, Search, Share/Devices lists) travel from the right edge by their whole
// width, as in 8.1: in with SOFT DECELERATE (MoveLong for the 86 DIP bar, MovePanel for the 320-346 DIP panes), out with
// ACCELERATE over 167 ms. Calm motion (Reduced, Dark rest) glides 24 DIP with a fade instead. Both stay interruptible:
// the animations omit From, so a re-trigger continues from wherever the surface is.
internal static class PaneMotion
{
	// fresh: the surface was hidden, so it starts from the edge (or from 24 DIP in calm motion). Otherwise (a re-trigger,
	// or the release of the charms edge pull) it glides on from wherever it is, never jumping.
	public static void Enter(TranslateTransform slide, UIElement content, double width, bool fresh)
	{
		double travel = Motion.Distance(width);
		bool calm = Motion.Mode == MotionMode.Reduced;
		if (fresh)
		{
			slide.BeginAnimation(TranslateTransform.XProperty, null);
			slide.X = Motion.Enabled ? travel : 0.0;
		}
		Motion.Tok tok = Motion.Move(Math.Max(Math.Abs(travel), Math.Abs(slide.X)));
		slide.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(0.0, Motion.Dur(tok))
		{
			EasingFunction = Motion.Ease(tok)
		}, HandoffBehavior.SnapshotAndReplace);
		content.BeginAnimation(UIElement.OpacityProperty, null);
		content.Opacity = 1.0;
		if (calm && fresh)
		{
			content.BeginAnimation(UIElement.OpacityProperty, Motion.Anim(0.0, 1.0, Motion.Tok.FadeIn), HandoffBehavior.SnapshotAndReplace);
		}
	}

	// done runs when the surface is out (hide it there); Reset puts the opacity back for the next entrance.
	public static void Exit(TranslateTransform slide, UIElement content, double width, Action done)
	{
		double travel = Motion.Distance(width);
		// Never inward: a surface already further out than the calm 24 DIP (a cancelled charms edge pull) leaves to the
		// edge from there instead of sliding back onto the screen while it fades.
		double target = !Motion.Enabled ? width : (slide.X > travel ? width : travel);
		Motion.Tok tok = Motion.ExitFor(Math.Max(1.0, Math.Abs(target - slide.X)));
		DoubleAnimation move = new DoubleAnimation(target, Motion.Dur(tok))
		{
			EasingFunction = Motion.Ease(tok)
		};
		move.Completed += delegate
		{
			done();
		};
		slide.BeginAnimation(TranslateTransform.XProperty, move, HandoffBehavior.SnapshotAndReplace);
		if (Motion.Mode == MotionMode.Reduced)
		{
			content.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0.0, Motion.Dur(Motion.Tok.FadeOut))
			{
				EasingFunction = Motion.Ease(Motion.Tok.FadeOut)
			}, HandoffBehavior.SnapshotAndReplace);
		}
	}

	// After the hide: opacity 1 and the slide parked at the full width (the drag-tracked reveal and the next entrance
	// both start from there).
	public static void Reset(TranslateTransform slide, UIElement content, double width)
	{
		content.BeginAnimation(UIElement.OpacityProperty, null);
		content.Opacity = 1.0;
		slide.BeginAnimation(TranslateTransform.XProperty, null);
		slide.X = width;
	}
}
