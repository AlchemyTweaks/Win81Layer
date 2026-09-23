using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Windows.Controls;
using System.Windows.Media;

namespace Win81Layer;

public static class SmoothScroll
{
	private sealed class State
	{
		public double Target;

		public bool Active;

		public double VerticalTarget;

		public bool VerticalActive;
	}

	private static readonly ConditionalWeakTable<ScrollViewer, State> States = new ConditionalWeakTable<ScrollViewer, State>();

	private static readonly List<(ScrollViewer Sv, State St)> Active = new List<(ScrollViewer, State)>();

	private static readonly List<(ScrollViewer Sv, State St)> VerticalActive = new List<(ScrollViewer, State)>();

	private static bool _hooked;

	private const double Rate = 15.0;

	private const double VerticalRate = 36.0;

	private const double VerticalLookAheadViewports = 0.85;

	private const double VerticalMinimumLookAhead = 96.0;

	private const double SnapEpsilon = 0.5;

	private static TimeSpan _lastRender = TimeSpan.Zero;

	public static void By(ScrollViewer sv, double delta)
	{
		State st = States.GetValue(sv, (ScrollViewer _) => new State());
		if (!st.Active)
		{
			st.Target = sv.HorizontalOffset;
		}
		st.Target = Math.Clamp(st.Target + delta, 0.0, sv.ScrollableWidth);
		if (!st.Active)
		{
			st.Active = true;
			Active.Add((sv, st));
			EnsureRenderingHook();
		}
	}

	public static void To(ScrollViewer sv, double target)
	{
		By(sv, Math.Clamp(target, 0.0, sv.ScrollableWidth) - EffectiveOffset(sv));
	}

	public static bool VerticalSupported => true;

	public static bool IsVerticalActive(ScrollViewer sv)
	{
		return States.TryGetValue(sv, out State st) && st.VerticalActive;
	}

	public static void ByVertical(ScrollViewer sv, double delta)
	{
		State st = States.GetValue(sv, (ScrollViewer _) => new State());
		double current = sv.VerticalOffset;
		if (!st.VerticalActive)
		{
			st.VerticalTarget = current;
		}
		else
		{
			double remaining = st.VerticalTarget - current;
			if (Math.Abs(remaining) > SnapEpsilon && Math.Sign(delta) != Math.Sign(remaining))
			{
				// Direction changes must feel immediate instead of first draining the old target queue.
				st.VerticalTarget = current;
			}
		}
		double lookAhead = Math.Max(VerticalMinimumLookAhead, sv.ViewportHeight * VerticalLookAheadViewports);
		double minimum = Math.Max(0.0, current - lookAhead);
		double maximum = Math.Min(sv.ScrollableHeight, current + lookAhead);
		st.VerticalTarget = Math.Clamp(st.VerticalTarget + delta, minimum, maximum);
		if (Math.Abs(st.VerticalTarget - current) <= SnapEpsilon)
		{
			StopVertical(sv);
			return;
		}
		if (!st.VerticalActive)
		{
			st.VerticalActive = true;
			VerticalActive.Add((sv, st));
			EnsureRenderingHook();
		}
	}

	public static void ToVertical(ScrollViewer sv, double target)
	{
		ByVertical(sv, Math.Clamp(target, 0.0, sv.ScrollableHeight) - EffectiveVerticalOffset(sv));
	}

	public static void Stop(ScrollViewer sv)
	{
		if (States.TryGetValue(sv, out State st))
		{
			st.Active = false;
			st.VerticalActive = false;
		}
		for (int i = Active.Count - 1; i >= 0; i--)
		{
			if (Active[i].Sv == sv)
			{
				Active.RemoveAt(i);
			}
		}
		for (int i = VerticalActive.Count - 1; i >= 0; i--)
		{
			if (VerticalActive[i].Sv == sv)
			{
				VerticalActive.RemoveAt(i);
			}
		}
		RemoveRenderingHookIfIdle();
	}

	public static void StopVertical(ScrollViewer sv)
	{
		if (States.TryGetValue(sv, out State st))
		{
			st.VerticalActive = false;
		}
		for (int i = VerticalActive.Count - 1; i >= 0; i--)
		{
			if (VerticalActive[i].Sv == sv)
			{
				VerticalActive.RemoveAt(i);
			}
		}
		RemoveRenderingHookIfIdle();
	}

	private static double EffectiveOffset(ScrollViewer sv)
	{
		State st;
		return (States.TryGetValue(sv, out st) && st.Active) ? st.Target : sv.HorizontalOffset;
	}

	private static double EffectiveVerticalOffset(ScrollViewer sv)
	{
		State st;
		return (States.TryGetValue(sv, out st) && st.VerticalActive) ? st.VerticalTarget : sv.VerticalOffset;
	}

	private static void EnsureRenderingHook()
	{
		if (!_hooked)
		{
			_lastRender = TimeSpan.Zero;
			CompositionTarget.Rendering += OnRendering;
			_hooked = true;
		}
	}

	private static void RemoveRenderingHookIfIdle()
	{
		if (Active.Count == 0 && VerticalActive.Count == 0 && _hooked)
		{
			CompositionTarget.Rendering -= OnRendering;
			_hooked = false;
			_lastRender = TimeSpan.Zero;
		}
	}

	private static void OnRendering(object? sender, EventArgs e)
	{
		TimeSpan now = (e as RenderingEventArgs)?.RenderingTime ?? _lastRender;
		double dt = (now - _lastRender).TotalSeconds;
		_lastRender = now;
		if (dt <= 0.0 || dt > 0.1)
		{
			dt = 1.0 / 60.0;
		}
		double factor = Math.Exp(-Rate * dt);
		double verticalFactor = Math.Exp(-VerticalRate * dt);
		for (int i = Active.Count - 1; i >= 0; i--)
		{
			(ScrollViewer Sv, State St) tuple = Active[i];
			ScrollViewer sv = tuple.Sv;
			State st = tuple.St;
			double current = sv.HorizontalOffset;
			double target = Math.Clamp(st.Target, 0.0, sv.ScrollableWidth);
			double next = target + (current - target) * factor;
			if (Math.Abs(target - next) <= 0.5)
			{
				sv.ScrollToHorizontalOffset(target);
				st.Active = false;
				Active.RemoveAt(i);
			}
			else
			{
				sv.ScrollToHorizontalOffset(next);
			}
		}
		for (int i = VerticalActive.Count - 1; i >= 0; i--)
		{
			(ScrollViewer Sv, State St) tuple = VerticalActive[i];
			ScrollViewer sv = tuple.Sv;
			State st = tuple.St;
			double current = sv.VerticalOffset;
			double target = Math.Clamp(st.VerticalTarget, 0.0, sv.ScrollableHeight);
			double next = target + (current - target) * verticalFactor;
			if (Math.Abs(target - next) <= SnapEpsilon)
			{
				sv.ScrollToVerticalOffset(target);
				st.VerticalActive = false;
				VerticalActive.RemoveAt(i);
			}
			else
			{
				sv.ScrollToVerticalOffset(next);
			}
		}
		RemoveRenderingHookIfIdle();
	}
}
