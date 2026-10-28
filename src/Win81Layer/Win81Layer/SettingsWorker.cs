using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;

#nullable enable annotations

namespace Win81Layer;

// The one background thread PC Settings writes Windows settings on: writes are serialized in the order they were asked
// for, a slider drag is throttled (one live write per 60 ms, the latest value wins) and nothing broadcasts on the UI
// thread. The thread ends after 30 s without work and starts again on the next write.
internal static class SettingsWorker
{
	internal const int ThrottleMs = 60;

	private static readonly BlockingCollection<Action> Queue = new BlockingCollection<Action>();

	private static readonly object Gate = new object();

	private static Thread? _thread;

	private sealed class Throttled
	{
		internal Action? Latest;

		internal bool Scheduled;

		internal long LastRun = long.MinValue / 2;
	}

	private static readonly Dictionary<string, Throttled> Throttles = new Dictionary<string, Throttled>(StringComparer.Ordinal);

	private static int _ran;

	// Work items run (QA).
	internal static int Ran => Volatile.Read(ref _ran);

	internal static bool IsWorkerThread => Thread.CurrentThread == _thread;

	internal static void Run(Action work)
	{
		Queue.Add(work);
		EnsureThread();
	}

	// The latest value of a drag: one write at most every 60 ms per key; a later call replaces the pending value. The
	// final value of a drag goes through here too, so writes stay 60 ms apart.
	internal static void Throttle(string key, Action work)
	{
		lock (Gate)
		{
			if (!Throttles.TryGetValue(key, out Throttled? t))
			{
				t = new Throttled();
				Throttles[key] = t;
			}
			t.Latest = work;
			if (t.Scheduled)
			{
				return;
			}
			t.Scheduled = true;
		}
		Run(delegate
		{
			Throttled t;
			lock (Gate)
			{
				t = Throttles[key];
			}
			long wait = t.LastRun + Stopwatch.Frequency * ThrottleMs / 1000 - Stopwatch.GetTimestamp();
			if (wait > 0)
			{
				Thread.Sleep((int)Math.Ceiling(wait * 1000.0 / Stopwatch.Frequency));
			}
			Action? act;
			lock (Gate)
			{
				act = t.Latest;
				t.Latest = null;
				t.Scheduled = false;
				t.LastRun = Stopwatch.GetTimestamp();
			}
			act?.Invoke();
		});
	}

	// Waits until every write asked for so far has run (QA, and the undo of a group).
	internal static bool Flush(int timeoutMs = 5000)
	{
		if (IsWorkerThread)
		{
			return true;
		}
		using ManualResetEventSlim done = new ManualResetEventSlim(false);
		Run(done.Set);
		return done.Wait(timeoutMs);
	}

	private static void EnsureThread()
	{
		lock (Gate)
		{
			if (_thread != null)
			{
				return;
			}
			_thread = new Thread(Loop)
			{
				IsBackground = true,
				Name = "PcSettings.Worker"
			};
			_thread.SetApartmentState(ApartmentState.MTA);
			_thread.Start();
		}
	}

	private static void Loop()
	{
		while (true)
		{
			if (!Queue.TryTake(out Action? work, 30000))
			{
				lock (Gate)
				{
					if (Queue.Count == 0)
					{
						_thread = null;
						return;
					}
				}
				continue;
			}
			try
			{
				work();
			}
			catch (Exception ex)
			{
				Logger.Log("PC settings worker: " + ex.Message);
			}
			Interlocked.Increment(ref _ran);
		}
	}
}
