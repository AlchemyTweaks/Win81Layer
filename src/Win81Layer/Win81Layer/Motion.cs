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

	// The motion tokens of the flat 8.1 system: opacity plus one translation. Values are Authentic; Fast plays them at
	// 0.8x, Reduced (and Dark rest's calm motion) at 1.0x with half the travel, Off sets the final values at once.
	public enum Tok
	{
		FadeInShort,
		FadeIn,
		FadeOut,
		FadeOutLong,
		MoveMicro,
		MoveShort,
		MoveLong,
		MovePanel,
		ExitMove,
		ExitMoveLong,
		PressDown,
		PressUp,
		Reposition
	}

	private static readonly IEasingFunction CubicOut = FreezeEase(new CubicEase
	{
		EasingMode = EasingMode.EaseOut
	});

	private static readonly IEasingFunction CubicIn = FreezeEase(new CubicEase
	{
		EasingMode = EasingMode.EaseIn
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

	// The Windows 8.1 standard curve, for short entrances: 50% of the travel at 8% of the time, 90% at 32%.
	public static readonly CubicBezierEase Decelerate = new CubicBezierEase(0.10, 0.90, 0.20, 1.00);

	// For long travel: the first 144 Hz frame moves less than a pixel, then the surface settles visibly.
	public static readonly CubicBezierEase SoftDecelerate = new CubicBezierEase(0.33, 0.00, 0.10, 1.00);

	// Only for the movement of exiting elements.
	public static readonly CubicBezierEase Accelerate = new CubicBezierEase(0.70, 0.00, 1.00, 0.50);

	// Every opacity fade, in and out (the 8.1 fades were linear).
	public static readonly IEasingFunction Linear = new LinearEase();

	private static MotionMode _userMode;

	// Dark rest's calmer animations (DarkRest.Sync): Authentic and Fast play as Reduced while this is set; Off and
	// Reduced are kept. The user's own mode is never changed by it.
	internal static volatile bool CalmOverride;

	public static MotionMode Mode
	{
		get => CalmOverride && _userMode is MotionMode.Authentic or MotionMode.Fast ? MotionMode.Reduced : _userMode;
		set => _userMode = value;
	}

	// The mode as the user (or the startup policy) set it, without the Dark rest override.
	internal static MotionMode UserMode => _userMode;

	// Public multiplier so hand-rolled animations (e.g. live-tile flips) can honor the same timing contract as Dur/Time.
	public static double Factor => Scale;

	// True unless the user turned animations off.
	public static bool Enabled => Mode != MotionMode.Off;

	// Scale (zoom) motion and staggered lists only play in Authentic and Fast; calm motion has neither.
	public static bool AllowScale => Mode is MotionMode.Authentic or MotionMode.Fast;

	public static bool AllowStagger => Mode is MotionMode.Authentic or MotionMode.Fast;

	// Dedicated Start entrance/exit curves. Only StartScreen consumes these, so no other Enter/Exit consumer is
	// affected. StartCloseEase REUSES the existing frozen CubicIn (the same curve Ease(Cat.Exit) returns) — no new alloc.
	public static IEasingFunction StartOpenEase => ExpoOut;

	public static IEasingFunction StartCloseEase => CubicIn;

	// The Win7 Start menu's own open curve (unchanged) and its close, now a linear fade.
	public static IEasingFunction Win7OpenEase => QuinticOut;

	// Reduced plays at 1.0x: calm motion is gentle (shorter travel), never shorter in time.
	private static double Scale
	{
		get
		{
			return Mode switch
			{
				MotionMode.Fast => 0.8,
				MotionMode.Reduced => 1.0,
				MotionMode.Off => 0.06,
				_ => 1.0,
			};
		}
	}

	private static double BaseMs(Cat c)
	{
		return c switch
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
			Cat.Reposition => 250,
			Cat.Semantic => 280,
			_ => 160,
		};
	}

	// Authentic duration of a token in milliseconds (before the mode's factor).
	public static double BaseMs(Tok t)
	{
		return t switch
		{
			Tok.FadeInShort => 83,
			Tok.FadeIn => 120,
			Tok.FadeOut => 100,
			Tok.FadeOutLong => 167,
			Tok.MoveMicro => 167,
			Tok.MoveShort => 250,
			Tok.MoveLong => 250,
			Tok.MovePanel => 300,
			Tok.ExitMove => 100,
			Tok.ExitMoveLong => 167,
			Tok.PressDown => 83,
			Tok.PressUp => 167,
			Tok.Reposition => 250,
			_ => 167,
		};
	}

	// Stagger step for lists that enter together with their surface (16 ms per item, at most 5 items).
	public const double StaggerMs = 16.0;

	public const int StaggerMax = 5;

	public static Duration Dur(Cat c)
	{
		return new Duration(TimeSpan.FromMilliseconds(Math.Max(1.0, BaseMs(c) * Scale)));
	}

	public static TimeSpan Time(Cat c)
	{
		return TimeSpan.FromMilliseconds(Math.Max(1.0, BaseMs(c) * Scale));
	}

	// A token's duration in the current mode: Off is instant, Fast 0.8x, Authentic and Reduced 1.0x.
	public static TimeSpan Time(Tok t)
	{
		return TimeSpan.FromMilliseconds(TokMs(t, Mode));
	}

	public static Duration Dur(Tok t)
	{
		return new Duration(Time(t));
	}

	internal static double TokMs(Tok t, MotionMode mode)
	{
		return mode switch
		{
			MotionMode.Off => 0.0,
			MotionMode.Fast => BaseMs(t) * 0.8,
			_ => BaseMs(t),
		};
	}

	public static IEasingFunction Ease(Tok t)
	{
		return t switch
		{
			Tok.FadeInShort or Tok.FadeIn or Tok.FadeOut or Tok.FadeOutLong => Linear,
			Tok.MoveMicro or Tok.MoveShort or Tok.PressDown or Tok.PressUp => Decelerate,
			Tok.MoveLong or Tok.MovePanel or Tok.Reposition => SoftDecelerate,
			Tok.ExitMove or Tok.ExitMoveLong => Accelerate,
			_ => Decelerate,
		};
	}

	// The entrance token for a travel of px DIP: micro (8 or less), short (9-48), long (49-150), panel (more).
	public static Tok Move(double px)
	{
		double d = Math.Abs(px);
		if (d <= 8.0)
		{
			return Tok.MoveMicro;
		}
		if (d <= 48.0)
		{
			return Tok.MoveShort;
		}
		return d <= 150.0 ? Tok.MoveLong : Tok.MovePanel;
	}

	// The exit token for a travel of px DIP.
	public static Tok ExitFor(double px)
	{
		return Math.Abs(px) <= 48.0 ? Tok.ExitMove : Tok.ExitMoveLong;
	}

	// The travel actually played for a designed distance: unchanged in Authentic and Fast; Reduced moves half as far,
	// at most 24 DIP, and drops the translation below 3 DIP; Off does not move.
	public static double Distance(double px)
	{
		return DistanceFor(px, Mode);
	}

	internal static double DistanceFor(double px, MotionMode mode)
	{
		switch (mode)
		{
		case MotionMode.Off:
			return 0.0;
		case MotionMode.Reduced:
		{
			double d = Math.Min(Math.Abs(px) * 0.5, 24.0);
			if (d < 3.0)
			{
				return 0.0;
			}
			return Math.Sign(px) * d;
		}
		default:
			return px;
		}
	}

	// A one-shot token animation (FillBehavior.Stop: the caller sets the exact base value first).
	public static DoubleAnimation Anim(double from, double to, Tok t, double beginMs = 0.0)
	{
		DoubleAnimation a = new DoubleAnimation(from, to, Dur(t))
		{
			EasingFunction = Ease(t),
			FillBehavior = FillBehavior.Stop
		};
		if (beginMs > 0.0)
		{
			a.BeginTime = TimeSpan.FromMilliseconds(Mode == MotionMode.Fast ? beginMs * 0.8 : beginMs);
		}
		return a;
	}

	public static IEasingFunction Ease(Cat c)
	{
		IEasingFunction result;
		switch (c)
		{
		case Cat.EdgeExit:
		case Cat.Exit:
			result = CubicIn;
			break;
		case Cat.Reposition:
			result = SoftDecelerate;
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
		return result;
	}

	public static DoubleAnimation To(double to, Cat c)
	{
		return new DoubleAnimation(to, Dur(c))
		{
			EasingFunction = Ease(c)
		};
	}

	// Reposition glides (taskbar buttons, pins): 250 ms SOFT DECELERATE.
	public static DoubleAnimation Glide(double fromOffset)
	{
		return new DoubleAnimation(fromOffset, 0.0, Dur(Tok.Reposition))
		{
			EasingFunction = SoftDecelerate
		};
	}

	// Explicit-target sibling of Glide (same Reposition timing and curve). Used by the compositor-only pin drag to slide
	// neighbours to a specific gap offset and to ease the dragged tile home on drop.
	public static DoubleAnimation GlideTo(double from, double to)
	{
		return new DoubleAnimation(from, to, Dur(Tok.Reposition))
		{
			EasingFunction = SoftDecelerate
		};
	}

	private static IEasingFunction FreezeEase(Freezable easing)
	{
		easing.Freeze();
		return (IEasingFunction)easing;
	}

	public static MotionMode Parse(string? s)
	{
		return s switch
		{
			"Fast" => MotionMode.Fast,
			"Reduced" => MotionMode.Reduced,
			"Off" => MotionMode.Off,
			_ => MotionMode.Authentic,
		};
	}

	private sealed class LinearEase : IEasingFunction
	{
		public double Ease(double normalizedTime)
		{
			return normalizedTime <= 0.0 ? 0.0 : normalizedTime >= 1.0 ? 1.0 : normalizedTime;
		}
	}
}

// A CSS-style cubic-bezier(x1, y1, x2, y2) easing curve. A plain class (not a Freezable, not a DispatcherObject), so
// a DoubleAnimation that uses it can still be frozen and shared. It builds a 257-entry table of the curve parameter at
// evenly spaced times once; each call then brackets the parameter from the table and refines it by bisection inside
// that bracket, which stays exact even where the curve is nearly vertical (the end of ACCELERATE). No allocation per
// frame.
public sealed class CubicBezierEase : IEasingFunction
{
	private const int Steps = 256;

	private readonly double _x1;

	private readonly double _y1;

	private readonly double _x2;

	private readonly double _y2;

	private readonly double[] _t = new double[Steps + 1];

	public CubicBezierEase(double x1, double y1, double x2, double y2)
	{
		_x1 = Math.Clamp(x1, 0.0, 1.0);
		_y1 = y1;
		_x2 = Math.Clamp(x2, 0.0, 1.0);
		_y2 = y2;
		_t[0] = 0.0;
		_t[Steps] = 1.0;
		for (int i = 1; i < Steps; i++)
		{
			_t[i] = Solve((double)i / Steps, 0.0, 1.0, 60);
		}
	}

	public double X1 => _x1;

	public double Y1 => _y1;

	public double X2 => _x2;

	public double Y2 => _y2;

	private static double Bez(double t, double p1, double p2)
	{
		double u = 1.0 - t;
		return 3.0 * u * u * t * p1 + 3.0 * u * t * t * p2 + t * t * t;
	}

	private static double BezD(double t, double p1, double p2)
	{
		double u = 1.0 - t;
		return 3.0 * u * u * p1 + 6.0 * u * t * (p2 - p1) + 3.0 * t * t * (1.0 - p2);
	}

	// The parameter t with x(t) = x inside [lo, hi]: Newton steps while they stay inside the bracket, bisection otherwise.
	private double Solve(double x, double lo, double hi, int iterations)
	{
		double t = lo + (hi - lo) * 0.5;
		for (int i = 0; i < iterations; i++)
		{
			double err = Bez(t, _x1, _x2) - x;
			if (Math.Abs(err) < 1e-9)
			{
				return t;
			}
			if (err > 0.0)
			{
				hi = t;
			}
			else
			{
				lo = t;
			}
			double d = BezD(t, _x1, _x2);
			double next = (Math.Abs(d) > 1e-6) ? t - err / d : double.NaN;
			t = (next > lo && next < hi) ? next : lo + (hi - lo) * 0.5;
		}
		return t;
	}

	public double Ease(double normalizedTime)
	{
		if (normalizedTime <= 0.0)
		{
			return 0.0;
		}
		if (normalizedTime >= 1.0)
		{
			return 1.0;
		}
		double f = normalizedTime * Steps;
		int i = Math.Min((int)f, Steps - 1);
		double t = Solve(normalizedTime, _t[i], _t[i + 1], 12);
		return Bez(t, _y1, _y2);
	}

	// The analytic value (a full-range solve), for the unit check of the table.
	internal double Analytic(double normalizedTime)
	{
		if (normalizedTime <= 0.0)
		{
			return 0.0;
		}
		if (normalizedTime >= 1.0)
		{
			return 1.0;
		}
		return Bez(Solve(normalizedTime, 0.0, 1.0, 200), _y1, _y2);
	}
}
