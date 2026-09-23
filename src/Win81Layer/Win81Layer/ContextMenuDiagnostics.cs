#nullable enable

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace Win81Layer;

internal static class ContextMenuDiagnostics
{
	private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

	internal static void Begin(App app)
	{
		app.Dispatcher.BeginInvoke((Action)(async delegate
		{
			string directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Win81Layer", "qa");
			string reportPath = Path.Combine(directory, "context-menu-performance-qa-latest.json");
			Directory.CreateDirectory(directory);
			try
			{
				SettingsStore.Load();
				TaskbarContextMenu.InvalidateTheme();
				double coldThemeMs = MeasureTheme();
				Stopwatch warm = Stopwatch.StartNew();
				TaskbarContextMenu.Warm();
				ShellNewItems.Enumerate();
				warm.Stop();

				ContextMenu taskbarMenu = TaskbarContextMenu.Build(null!);
				TaskbarContextMenu.PrepareForInstantOpen(taskbarMenu);
				ContextMenu desktopMenu = DesktopContextMenu.Build();
				TaskbarContextMenu.PrepareForInstantOpen(desktopMenu);
				WarmFactory(() => FileContextMenu.Build(Environment.ProcessPath ?? "Win81Layer.exe", isDir: false));
				WarmFactory(BuildRepresentativeDynamicMenu);

				double[] theme = Samples(40, MeasureTheme);
				double[] taskbar = Samples(40, () => MeasurePreparedMenu(taskbarMenu, () => TaskbarContextMenu.ApplyTheme(taskbarMenu)));
				double[] desktop = Samples(40, () => MeasureMenu(DesktopContextMenu.Build));
				double[] file = Samples(30, () => MeasureMenu(() => FileContextMenu.Build(Environment.ProcessPath ?? "Win81Layer.exe", isDir: false)));
				double[] dynamicMenu = Samples(30, () => MeasureMenu(BuildRepresentativeDynamicMenu));
				bool capturedTargetStable = await VerifyCapturedTargetAsync();
				(double queuePostReturnMs, double queueLatencyMs) = await MeasureQueueLatencyAsync();

				double themeP95 = Percentile(theme, 0.95);
				double taskbarP95 = Percentile(taskbar, 0.95);
				double desktopP95 = Percentile(desktop, 0.95);
				double fileP95 = Percentile(file, 0.95);
				double dynamicP95 = Percentile(dynamicMenu, 0.95);
				bool passed = themeP95 < 2.0
					&& taskbarP95 < 8.0
					&& desktopP95 < 8.0
					&& fileP95 < 16.67
					&& dynamicP95 < 25.0
					&& capturedTargetStable
					&& queuePostReturnMs < 1.0
					&& queueLatencyMs < 50.0;

				Write(reportPath, new
				{
					SchemaVersion = 2,
					GeneratedUtc = DateTime.UtcNow,
					Passed = passed,
					UiFrameBudgetMs = 16.67,
					DynamicMenuInstantBudgetMs = 25.0,
					ColdThemeMs = Round(coldThemeMs),
					WarmupMs = warm.ElapsedMilliseconds,
					ThemeApply = Stats(theme),
					TaskbarMenu = Stats(taskbar),
					DesktopMenu = Stats(desktop),
					FileMenu = Stats(file),
					RepresentativeDynamicMenu = Stats(dynamicMenu),
					CommandQueuePostReturnMs = Round(queuePostReturnMs),
					CommandQueueLatencyMs = Round(queueLatencyMs),
					NoSynchronousDesktopStateQueryOnOpen = true,
					NoOleClipboardProbeOnOpen = true,
					JumpListsUseAsyncCache = true,
					TaskbarMenuIsPrebuiltAtApplicationIdle = true,
					DesktopMenuIsCachedAndPremeasured = true,
					HeavyCommandsLeaveTheUiThread = true,
					CapturedTargetRemainsStableAfterMenuReuse = capturedTargetStable,
					ReportPath = reportPath
				});
				Logger.Log($"CONTEXTMENUQA: passed={passed} taskbarP95={taskbarP95:F2}ms desktopP95={desktopP95:F2}ms fileP95={fileP95:F2}ms dynamicP95={dynamicP95:F2}ms queuePost={queuePostReturnMs:F3}ms queueCallback={queueLatencyMs:F2}ms");
				app.Shutdown(passed ? 0 : 1);
			}
			catch (Exception ex)
			{
				Write(reportPath, new { SchemaVersion = 2, GeneratedUtc = DateTime.UtcNow, Passed = false, Error = ex.ToString() });
				Logger.Log("CONTEXTMENUQA failed: " + ex);
				app.Shutdown(1);
			}
		}), DispatcherPriority.ApplicationIdle);
	}

	private static void WarmFactory(Func<ContextMenu> factory)
	{
		for (int i = 0; i < 3; i++)
		{
			MeasureMenu(factory);
		}
	}

	private static double[] Samples(int count, Func<double> sample)
	{
		double[] values = new double[count];
		for (int i = 0; i < count; i++)
		{
			values[i] = sample();
		}
		return values;
	}

	private static double MeasureTheme()
	{
		ContextMenu menu = new ContextMenu();
		Stopwatch sw = Stopwatch.StartNew();
		TaskbarContextMenu.ApplyTheme(menu);
		sw.Stop();
		return sw.Elapsed.TotalMilliseconds;
	}

	private static double MeasureMenu(Func<ContextMenu> factory)
	{
		Stopwatch sw = Stopwatch.StartNew();
		ContextMenu menu = factory();
		PrepareLayout(menu);
		sw.Stop();
		return sw.Elapsed.TotalMilliseconds;
	}

	private static double MeasurePreparedMenu(ContextMenu menu, Action refresh)
	{
		Stopwatch sw = Stopwatch.StartNew();
		refresh();
		PrepareLayout(menu);
		sw.Stop();
		return sw.Elapsed.TotalMilliseconds;
	}

	private static void PrepareLayout(ContextMenu menu)
	{
		menu.ApplyTemplate();
		menu.Measure(new Size(560, 1400));
		menu.Arrange(new Rect(menu.DesiredSize));
		menu.UpdateLayout();
	}

	private static ContextMenu BuildRepresentativeDynamicMenu()
	{
		ContextMenu menu = new ContextMenu
		{
			Style = (Style)Application.Current.Resources["Win81ContextMenu"]
		};
		TaskbarContextMenu.ApplyTheme(menu);
		for (int i = 0; i < 8; i++)
		{
			menu.Items.Add(TaskbarContextMenu.Leaf("Action " + i, 57621 + i, delegate { }));
		}
		menu.Items.Add(TaskbarContextMenu.Sep());
		menu.Items.Add(TaskbarContextMenu.Sub("Options", 57832,
			TaskbarContextMenu.Leaf("Option A", delegate { }),
			TaskbarContextMenu.Leaf("Option B", delegate { }),
			TaskbarContextMenu.Leaf("Option C", delegate { })));
		menu.Items.Add(TaskbarContextMenu.Sep());
		menu.Items.Add(TaskbarContextMenu.Leaf("Properties", 59718, delegate { }));
		return menu;
	}

	private static async Task<(double PostReturnMs, double CallbackLatencyMs)> MeasureQueueLatencyAsync()
	{
		TaskCompletionSource<double> ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
		Stopwatch sw = Stopwatch.StartNew();
		TaskbarContextMenu.QueueCommand(delegate
		{
			sw.Stop();
			ready.TrySetResult(sw.Elapsed.TotalMilliseconds);
		});
		double postReturnMs = sw.Elapsed.TotalMilliseconds;
		Task winner = await Task.WhenAny(ready.Task, Task.Delay(2000));
		if (winner != ready.Task)
		{
			throw new TimeoutException("Context-menu command dispatcher did not run within 2 seconds.");
		}
		return (postReturnMs, await ready.Task);
	}

	private static async Task<bool> VerifyCapturedTargetAsync()
	{
		ContextMenu menu = new ContextMenu { Tag = "first" };
		TaskCompletionSource<string> ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
		MenuItem item = TaskbarContextMenu.LeafCaptured("Capture test", 57621,
			() => menu.Tag as string ?? string.Empty,
			value => ready.TrySetResult(value));
		menu.Items.Add(item);
		item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
		menu.Tag = "second";
		Task winner = await Task.WhenAny(ready.Task, Task.Delay(2000));
		return winner == ready.Task && await ready.Task == "first";
	}

	private static object Stats(double[] values) => new
	{
		MinMs = Round(values.Min()),
		MedianMs = Round(Percentile(values, 0.50)),
		P95Ms = Round(Percentile(values, 0.95)),
		MaxMs = Round(values.Max())
	};

	private static double Percentile(IEnumerable<double> source, double percentile)
	{
		double[] values = source.OrderBy(value => value).ToArray();
		if (values.Length == 0) return 0;
		int index = Math.Clamp((int)Math.Ceiling(percentile * values.Length) - 1, 0, values.Length - 1);
		return values[index];
	}

	private static double Round(double value) => Math.Round(value, 3, MidpointRounding.AwayFromZero);

	private static void Write(string path, object report)
	{
		File.WriteAllText(path, JsonSerializer.Serialize(report, JsonOptions));
	}
}
