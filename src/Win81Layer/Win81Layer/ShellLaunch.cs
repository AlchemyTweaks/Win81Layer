using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;

namespace Win81Layer;

public static class ShellLaunch
{
	private const uint ASFW_ANY = uint.MaxValue;

	private sealed class WorkItem
	{
		internal WorkItem(Action action)
		{
			Action = action;
			QueuedTimestamp = Stopwatch.GetTimestamp();
		}

		internal Action Action { get; }

		internal long QueuedTimestamp { get; }
	}

	private sealed class LaunchWorker
	{
		private readonly ConcurrentQueue<WorkItem> _queue = new ConcurrentQueue<WorkItem>();

		private readonly AutoResetEvent _signal = new AutoResetEvent(false);

		private readonly object _startGate = new object();

		private readonly string _name;

		private Thread? _thread;

		private int _pending;

		private int _busy;

		internal LaunchWorker(string name)
		{
			_name = name;
		}

		internal int Load => Volatile.Read(ref _pending) + Volatile.Read(ref _busy);

		internal void Warm()
		{
			EnsureStarted();
		}

		internal void Enqueue(Action action)
		{
			EnsureStarted();
			Interlocked.Increment(ref _pending);
			_queue.Enqueue(new WorkItem(action));
			_signal.Set();
		}

		private void EnsureStarted()
		{
			if (_thread?.IsAlive == true)
			{
				return;
			}
			lock (_startGate)
			{
				if (_thread?.IsAlive == true)
				{
					return;
				}
				Thread thread = new Thread(WorkLoop)
				{
					IsBackground = true,
					Name = _name,
					Priority = ThreadPriority.AboveNormal
				};
				thread.SetApartmentState(ApartmentState.STA);
				_thread = thread;
				thread.Start();
			}
		}

		private void WorkLoop()
		{
			while (true)
			{
				_signal.WaitOne();
				while (_queue.TryDequeue(out WorkItem? work))
				{
					Interlocked.Decrement(ref _pending);
					Interlocked.Exchange(ref _busy, 1);
					double queueMs = Stopwatch.GetElapsedTime(work.QueuedTimestamp).TotalMilliseconds;
					Stopwatch execution = Stopwatch.StartNew();
					try
					{
						work.Action();
					}
					catch (Exception ex)
					{
						Logger.Log("ShellLaunch backstop: " + ex.Message);
					}
					finally
					{
						execution.Stop();
						Interlocked.Exchange(ref _busy, 0);
					}
					if (queueMs >= 25.0 || execution.ElapsedMilliseconds >= 250)
					{
						Logger.Log($"ShellLaunch slow path: worker={_name} queue={queueMs:F1}ms call={execution.Elapsed.TotalMilliseconds:F1}ms");
					}
				}
			}
		}
	}

	private static readonly LaunchWorker[] Workers =
	{
		new LaunchWorker("ShellLaunch-0"),
		new LaunchWorker("ShellLaunch-1")
	};

	internal static string DiagnosticMode => "dual-persistent-sta-workers";

	[DllImport("user32.dll")]
	private static extern bool AllowSetForegroundWindow(uint pid);

	public static void AllowForeground()
	{
		try
		{
			AllowSetForegroundWindow(ASFW_ANY);
		}
		catch
		{
		}
	}

	public static void Warm()
	{
		foreach (LaunchWorker worker in Workers)
		{
			worker.Warm();
		}
	}

	public static void Run(Action launch)
	{
		if (launch == null)
		{
			throw new ArgumentNullException(nameof(launch));
		}
		AllowForeground();
		LaunchWorker target = Workers[0].Load <= Workers[1].Load ? Workers[0] : Workers[1];
		target.Enqueue(launch);
	}
}
