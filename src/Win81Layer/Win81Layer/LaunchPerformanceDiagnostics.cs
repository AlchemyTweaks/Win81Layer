using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

namespace Win81Layer;

internal static class LaunchPerformanceDiagnostics
{
	private static readonly string ReportPath = Path.Combine(
		Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
		"Win81Layer",
		"qa",
		"launch-performance-latest.json");

	internal static void Begin(Application app)
	{
		Task.Run(() => Run()).ContinueWith(task =>
		{
			if (task.IsFaulted)
			{
				Logger.Log("LAUNCHPERFQA failed: " + task.Exception);
				app.Dispatcher.BeginInvoke((Action)(() => app.Shutdown(1)));
				return;
			}
			Logger.Log("LAUNCHPERFQA: " + task.Result);
			app.Dispatcher.BeginInvoke((Action)(() => app.Shutdown()));
		}, TaskScheduler.Default);
	}

	private static string Run()
	{
		ProcessPerformancePolicy.ApplyInteractiveShell();
		ShellLaunch.Warm();
		const int dispatchSamples = 32;
		const int processSamples = 12;
		List<double> requestReturn = new List<double>(dispatchSamples);
		List<double> dispatchDelay = new List<double>(dispatchSamples);

		for (int i = 0; i < dispatchSamples; i++)
		{
			using ManualResetEventSlim done = new ManualResetEventSlim(false);
			long queued = Stopwatch.GetTimestamp();
			Stopwatch request = Stopwatch.StartNew();
			ShellLaunch.Run(() =>
			{
				dispatchDelay.Add(Stopwatch.GetElapsedTime(queued).TotalMilliseconds);
				done.Set();
			});
			request.Stop();
			requestReturn.Add(request.Elapsed.TotalMilliseconds);
			if (!done.Wait(TimeSpan.FromSeconds(3)))
			{
				throw new TimeoutException("Shell launch dispatcher did not execute a diagnostic callback within 3 seconds.");
			}
		}

		List<double> processStartReturn = new List<double>(processSamples);
		string executable = Environment.ProcessPath ?? throw new InvalidOperationException("The diagnostic executable path is unavailable.");
		for (int i = 0; i < processSamples; i++)
		{
			Stopwatch start = Stopwatch.StartNew();
			if (!AppLauncher.TryLaunch(executable, "--launch-probe", null, asAdmin: false, out string? error))
			{
				throw new InvalidOperationException("Launch probe failed: " + error);
			}
			start.Stop();
			processStartReturn.Add(start.Elapsed.TotalMilliseconds);
			Thread.Sleep(25);
		}

		var report = new
		{
			Schema = 1,
			CapturedUtc = DateTime.UtcNow,
			ProcessArchitecture = RuntimeInformation.ProcessArchitecture.ToString(),
			DispatchSamples = dispatchSamples,
			RequestReturnP50Ms = Percentile(requestReturn, 0.50),
			RequestReturnP95Ms = Percentile(requestReturn, 0.95),
			DispatchDelayP50Ms = Percentile(dispatchDelay, 0.50),
			DispatchDelayP95Ms = Percentile(dispatchDelay, 0.95),
			DispatchDelayMaxMs = dispatchDelay.Max(),
			ProcessSamples = processSamples,
			ProcessStartReturnP50Ms = Percentile(processStartReturn, 0.50),
			ProcessStartReturnP95Ms = Percentile(processStartReturn, 0.95),
			ProcessStartReturnMaxMs = processStartReturn.Max(),
			ShellLaunch = ShellLaunch.DiagnosticMode,
			ExecutableLaunch = AppLauncher.DiagnosticExecutableLaunchMode,
			ProcessQoS = ProcessPerformancePolicy.Describe()
		};

		Directory.CreateDirectory(Path.GetDirectoryName(ReportPath)!);
		File.WriteAllText(ReportPath, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
		return ReportPath;
	}

	private static double Percentile(IReadOnlyList<double> values, double percentile)
	{
		double[] ordered = values.OrderBy(value => value).ToArray();
		int index = Math.Clamp((int)Math.Ceiling(ordered.Length * percentile) - 1, 0, ordered.Length - 1);
		return Math.Round(ordered[index], 3);
	}
}
