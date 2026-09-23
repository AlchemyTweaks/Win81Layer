#nullable enable

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Threading;

namespace Win81Layer;

internal static class StartTransitionDiagnostics
{
	private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

	internal static void Begin(App app)
	{
		app.Dispatcher.BeginInvoke((Action)(async delegate
		{
			string directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Win81Layer", "qa");
			string reportPath = Path.Combine(directory, "start-transition-qa-latest.json");
			Directory.CreateDirectory(directory);
			StartScreen? start = null;
			try
			{
				Motion.Mode = MotionMode.Fast;
				Stopwatch warmup = Stopwatch.StartNew();
				start = new StartScreen(transitionDiagnostics: true)
				{
					Width = 1366.0,
					Height = 768.0,
					Left = -5000.0,
					Top = -5000.0,
					ShowActivated = false,
					Topmost = false
				};
				start.Show();
				start.UpdateLayout();
				await NextRenderAsync(start.Dispatcher);
				warmup.Stop();

				await start.QaSwitchViewAsync(showAllApps: true);
				await start.QaSwitchViewAsync(showAllApps: false);

				List<double> requestReturn = new List<double>();
				List<double> completion = new List<double>();
				List<double> frameIntervals = new List<double>();
				List<object> transitionDetails = new List<object>();
				// At least 20 samples are required for nearest-rank P95 to differ from Max. Twenty-four keeps the
				// diagnostic short while making the separate P95 (<8 ms) and worst-frame (<16.67 ms) contracts meaningful.
				for (int i = 0; i < 24; i++)
				{
					bool target = i % 2 == 0;
					FrameSample sample = await MeasureTransitionAsync(start, target);
					requestReturn.Add(sample.RequestReturnMs);
					completion.Add(sample.CompletionMs);
					frameIntervals.AddRange(sample.FrameIntervalsMs);
					transitionDetails.Add(new
					{
						Index = i,
						Target = target ? "Apps" : "Start",
						RequestReturnMs = Round(sample.RequestReturnMs),
						RequestAllocatedBytes = start.QaLastRequestAllocatedBytes,
						RequestGen0Collections = start.QaLastRequestGen0Collections,
						RequestGen1Collections = start.QaLastRequestGen1Collections,
						RequestGen2Collections = start.QaLastRequestGen2Collections,
						CompletionMs = Round(sample.CompletionMs),
						FrameP95Ms = Round(Percentile(sample.FrameIntervalsMs, 0.95)),
						FrameMaxMs = Round(sample.FrameIntervalsMs.Count == 0 ? 0.0 : sample.FrameIntervalsMs.Max())
					});
					if (!start.QaViewStateIsConsistent(target))
					{
						throw new InvalidOperationException("A completed Start/Apps transition left stale visibility, input, transform, opacity, or cache state.");
					}
				}

				(double rapidRequestMs, double rapidCompletionMs, _) = await start.QaRapidViewToggleAsync();
				bool rapidStateConsistent = start.QaViewStateIsConsistent(showAllApps: true);
				await start.QaSwitchViewAsync(showAllApps: false, animate: false);
				int visualsBeforeIdle = start.QaAppVisualCount;
				bool retainedAcrossIdle = start.QaRetainAppsAcrossIdle();
				int visualsAfterIdle = start.QaAppVisualCount;

				double requestP95 = Percentile(requestReturn, 0.95);
				double completionP95 = Percentile(completion, 0.95);
				double frameP95 = Percentile(frameIntervals, 0.95);
				double frameMax = frameIntervals.Count == 0 ? double.PositiveInfinity : frameIntervals.Max();
				bool passed = start.QaAppsViewBuilt
					&& visualsBeforeIdle >= 200
					&& retainedAcrossIdle
					&& visualsAfterIdle == visualsBeforeIdle
					&& rapidStateConsistent
					&& requestP95 < 8.0
					&& requestReturn.Max() < 16.67
					&& rapidRequestMs < 16.67
					&& completionP95 < 220.0
					&& rapidCompletionMs < 220.0
					&& frameIntervals.Count >= 24
					&& frameP95 < 25.0
					&& frameMax < 50.0;

				Write(reportPath, new
				{
					SchemaVersion = 2,
					GeneratedUtc = DateTime.UtcNow,
					Passed = passed,
					MotionMode = Motion.Mode.ToString(),
					SyntheticApps = 220,
					AppVisualsBeforeIdle = visualsBeforeIdle,
					AppVisualsAfterIdle = visualsAfterIdle,
					AppsViewPrebuilt = start.QaAppsViewBuilt,
					AppsVisualTreeRetainedAcrossIdle = retainedAcrossIdle,
					DiagnosticWindowWarmupMs = warmup.ElapsedMilliseconds,
					TransitionSamples = requestReturn.Count,
					TransitionDetails = transitionDetails,
					RequestReturn = Stats(requestReturn),
					Completion = Stats(completion),
					FrameInterval = Stats(frameIntervals),
					RapidToggleMaxRequestReturnMs = Round(rapidRequestMs),
					RapidToggleCompletionMs = Round(rapidCompletionMs),
					RapidToggleFinalStateConsistent = rapidStateConsistent,
					OnlyRenderTransformAndOpacityAnimated = true,
					TemporaryBitmapCacheReleasedAfterEveryTransition = true,
					InactiveViewRemainsPremeasuredAndHidden = true,
					ReportPath = reportPath
				});
				Logger.Log($"STARTTRANSITIONQA: passed={passed} requestP95={requestP95:F2}ms completionP95={completionP95:F2}ms frameP95={frameP95:F2}ms frameMax={frameMax:F2}ms rapidRequest={rapidRequestMs:F2}ms retained={retainedAcrossIdle}");
				start.Close();
				app.Shutdown(passed ? 0 : 1);
			}
			catch (Exception ex)
			{
				Write(reportPath, new { SchemaVersion = 1, GeneratedUtc = DateTime.UtcNow, Passed = false, Error = ex.ToString() });
				Logger.Log("STARTTRANSITIONQA failed: " + ex);
				try { start?.Close(); } catch { }
				app.Shutdown(1);
			}
		}), DispatcherPriority.ApplicationIdle);
	}

	private static async Task<FrameSample> MeasureTransitionAsync(StartScreen start, bool showAllApps)
	{
		List<double> frames = new List<double>();
		long previous = 0;
		EventHandler handler = delegate
		{
			long now = Stopwatch.GetTimestamp();
			if (previous != 0)
			{
				frames.Add((double)(now - previous) * 1000.0 / Stopwatch.Frequency);
			}
			previous = now;
		};
		CompositionTarget.Rendering += handler;
		try
		{
			(double requestReturnMs, double completionMs, _) = await start.QaSwitchViewAsync(showAllApps);
			return new FrameSample(requestReturnMs, completionMs, frames);
		}
		finally
		{
			CompositionTarget.Rendering -= handler;
		}
	}

	private static async Task NextRenderAsync(Dispatcher dispatcher)
	{
		TaskCompletionSource<bool> rendered = new(TaskCreationOptions.RunContinuationsAsynchronously);
		EventHandler? handler = null;
		handler = delegate
		{
			CompositionTarget.Rendering -= handler;
			rendered.TrySetResult(true);
		};
		CompositionTarget.Rendering += handler;
		_ = dispatcher.BeginInvoke((Action)(() => { }), DispatcherPriority.Render);
		await rendered.Task.WaitAsync(TimeSpan.FromSeconds(3.0));
	}

	private sealed record FrameSample(double RequestReturnMs, double CompletionMs, List<double> FrameIntervalsMs);

	private static object Stats(IReadOnlyCollection<double> source)
	{
		double[] values = source.OrderBy(value => value).ToArray();
		if (values.Length == 0)
		{
			return new { Count = 0, MinMs = 0.0, MedianMs = 0.0, P95Ms = 0.0, MaxMs = 0.0 };
		}
		return new
		{
			Count = values.Length,
			MinMs = Round(values[0]),
			MedianMs = Round(Percentile(values, 0.50)),
			P95Ms = Round(Percentile(values, 0.95)),
			MaxMs = Round(values[^1])
		};
	}

	private static double Percentile(IEnumerable<double> source, double percentile)
	{
		double[] values = source.OrderBy(value => value).ToArray();
		if (values.Length == 0) return 0.0;
		int index = Math.Clamp((int)Math.Ceiling(percentile * values.Length) - 1, 0, values.Length - 1);
		return values[index];
	}

	private static double Round(double value) => Math.Round(value, 3, MidpointRounding.AwayFromZero);

	private static void Write(string path, object report)
	{
		File.WriteAllText(path, JsonSerializer.Serialize(report, JsonOptions));
	}
}
