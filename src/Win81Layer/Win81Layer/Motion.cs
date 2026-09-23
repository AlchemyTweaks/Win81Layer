using System;
using System.Windows;
using System.Windows.Media.Animation;

namespace Win81Layer;

public static class Motion
{
	public enum Cat
	{
		Press,
		Hover,
		Micro,
		PressFill,
		EdgeEnter,
		EdgeExit,
		SwitcherIn,
		SwitcherOut,
		Enter,
		Exit,
		Navigation,
		ViewEnter,
		ViewExit,
		Reposition,
		Semantic
	}

	private static readonly IEasingFunction CubicOut = FreezeEase(new CubicEase
	{
		EasingMode = EasingMode.EaseOut
	});

	private static readonly IEasingFunction CubicIn = FreezeEase(new CubicEase
	{
		EasingMode = EasingMode.EaseIn
	});

	private static readonly IEasingFunction CubicInOut = FreezeEase(new CubicEase
	{
		EasingMode = EasingMode.EaseInOut
	});

	private static readonly IEasingFunction QuinticOut = FreezeEase(new QuinticEase
	{
		EasingMode = EasingMode.EaseOut
	});

	// Strong, front-loaded ease-out used ONLY by the Desktop<->Start entrance (authentic 8.1 "rush in, then settle").
	private static readonly IEasingFunction ExpoOut = FreezeEase(new ExponentialEase
	{
		EasingMode = EasingMode.EaseOut,
		Exponent = 6.0
	});

	public static MotionMode Mode { get; set; }

	// Public multiplier so hand-rolled animations (e.g. live-tile flips) can honor the same timing contract as Dur/Time.
	public static double Factor => Scale;

	// Dedicated Start entrance/exit curves. Only StartScreen consumes these, so no other Enter/Exit consumer is
	// affected. StartCloseEase REUSES the existing frozen CubicIn (the same curve Ease(Cat.Exit) returns) — no new alloc.
	public static IEasingFunction StartOpenEase => ExpoOut;

	public static IEasingFunction StartCloseEase => CubicIn;

	private static double Scale
	{
		get
		{
			MotionMode mode = Mode;
			if (1 == 0)
			{
			}
			double result = mode switch
			{
				MotionMode.Fast => 0.8, 
				MotionMode.Reduced => 0.55, 
				MotionMode.Off => 0.06, 
				_ => 1.0, 
			};
			if (1 == 0)
			{
			}
			return result;
		}
	}

	private static double BaseMs(Cat c)
	{
		if (1 == 0)
		{
		}
		int num = c switch
		{
			Cat.Press => 70, 
			Cat.Hover => 90, 
			Cat.Micro => 130, 
			Cat.PressFill => 150, 
			Cat.EdgeEnter => 165,
			// Dismissals feel best noticeably faster than entrances (Fluent/Material): flyouts + charms close snappier.
			Cat.EdgeExit => 130,
			Cat.SwitcherIn => 160, 
			Cat.SwitcherOut => 100, 
			Cat.Enter => 220, 
			Cat.Exit => 180, 
			Cat.Navigation => 165,
			Cat.ViewEnter => 145,
			Cat.ViewExit => 105,
			Cat.Reposition => 200,
			Cat.Semantic => 280, 
			_ => 160, 
		};
		if (1 == 0)
		{
		}
		return num;
	}

	public static Duration Dur(Cat c)
	{
		return new Duration(TimeSpan.FromMilliseconds(Math.Max(1.0, BaseMs(c) * Scale)));
	}

	public static TimeSpan Time(Cat c)
	{
		return TimeSpan.FromMilliseconds(Math.Max(1.0, BaseMs(c) * Scale));
	}

	public static IEasingFunction Ease(Cat c)
	{
		if (1 == 0)
		{
		}
		IEasingFunction result;
		switch (c)
		{
		case Cat.EdgeExit:
		case Cat.Exit:
			result = CubicIn;
			break;
		case Cat.Reposition:
			result = CubicInOut;
			break;
		case Cat.EdgeEnter:
		case Cat.Enter:
		case Cat.ViewEnter:
		case Cat.Semantic:
			result = QuinticOut;
			break;
		default:
			result = CubicOut;
			break;
		}
		if (1 == 0)
		{
		}
		return result;
	}

	public static DoubleAnimation To(double to, Cat c)
	{
		return new DoubleAnimation(to, Dur(c))
		{
			EasingFunction = Ease(c)
		};
	}

	public static DoubleAnimation Glide(double fromOffset)
	{
		return new DoubleAnimation(fromOffset, 0.0, Dur(Cat.Reposition))
		{
			EasingFunction = CubicOut
		};
	}

	// Explicit-target sibling of Glide (same Reposition timing + CubicOut easing). Used by the compositor-only pin
	// drag to slide neighbours to a specific gap offset and to ease the dragged tile home on drop.
	public static DoubleAnimation GlideTo(double from, double to)
	{
		return new DoubleAnimation(from, to, Dur(Cat.Reposition))
		{
			EasingFunction = CubicOut
		};
	}

	private static IEasingFunction FreezeEase(Freezable easing)
	{
		easing.Freeze();
		return (IEasingFunction)easing;
	}

	public static MotionMode Parse(string? s)
	{
		if (1 == 0)
		{
		}
		MotionMode result = s switch
		{
			"Fast" => MotionMode.Fast, 
			"Reduced" => MotionMode.Reduced, 
			"Off" => MotionMode.Off, 
			_ => MotionMode.Authentic, 
		};
		if (1 == 0)
		{
		}
		return result;
	}
}
