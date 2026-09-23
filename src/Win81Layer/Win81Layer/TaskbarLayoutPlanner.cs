using System;

namespace Win81Layer;

internal readonly record struct TaskbarLayoutPlan(int VisiblePins, int VisibleTasks, bool HasOverflow);

internal static class TaskbarLayoutPlanner
{
	internal const double PinSlot = 48.0;
	internal const double TaskSlot = 40.0;
	internal const double OverflowSlot = 40.0;

	public static TaskbarLayoutPlan Compute(int pinCount, int taskCount, double available)
	{
		pinCount = Math.Max(0, pinCount);
		taskCount = Math.Max(0, taskCount);
		available = Math.Max(0.0, available);
		bool overflow = pinCount * PinSlot + taskCount * TaskSlot > available + 0.5;
		if (!overflow)
		{
			return new TaskbarLayoutPlan(pinCount, taskCount, HasOverflow: false);
		}

		double remaining = Math.Max(0.0, available - OverflowSlot);
		double taskReserve = taskCount > 0 && remaining >= TaskSlot ? TaskSlot : 0.0;
		int visiblePins = Math.Min(pinCount, (int)Math.Floor(Math.Max(0.0, remaining - taskReserve) / PinSlot));
		remaining -= visiblePins * PinSlot;
		int visibleTasks = Math.Min(taskCount, (int)Math.Floor(Math.Max(0.0, remaining) / TaskSlot));
		return new TaskbarLayoutPlan(visiblePins, visibleTasks, HasOverflow: true);
	}
}
