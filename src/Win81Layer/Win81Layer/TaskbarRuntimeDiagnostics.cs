using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace Win81Layer;

internal static class TaskbarRuntimeDiagnostics
{
	private struct RECT
	{
		public int Left;
		public int Top;
		public int Right;
		public int Bottom;
	}

	[DllImport("user32.dll")]
	private static extern bool GetWindowRect(nint window, out RECT rect);

	[DllImport("user32.dll")]
	private static extern bool IsWindowVisible(nint window);

	[DllImport("user32.dll", SetLastError = true)]
	private static extern bool SetWindowPos(nint window, nint insertAfter, int x, int y, int width, int height, uint flags);

	[DllImport("user32.dll")]
	private static extern bool SetForegroundWindow(nint window);

	public static async void Begin(System.Windows.Application application, TaskbarManager manager)
	{
		Logger.Log("[taskbar-runtime-qa] started");
		string[] stateNames = { "profile.json", "settings.json", "taskbar-pins.json", "shell-profile-state.json" };
		string dataRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Win81Layer");
		Dictionary<string, string?> before = stateNames.ToDictionary(name => name, name => Hash(Path.Combine(dataRoot, name)));
		string original = SettingsStore.Load().TaskbarPosition;
		List<object> checks = new List<object>();
		bool passed = true;
		Stopwatch total = Stopwatch.StartNew();
		System.Windows.Window? neutralWindow = null;
		try
		{
			await Task.Delay(2500);
			neutralWindow = new System.Windows.Window
			{
				Title = "Win81Layer Taskbar QA Control",
				WindowStyle = System.Windows.WindowStyle.ToolWindow,
				ResizeMode = System.Windows.ResizeMode.NoResize,
				ShowInTaskbar = false,
				Width = 180.0,
				Height = 80.0,
				Left = 80.0,
				Top = 80.0,
				Background = System.Windows.Media.Brushes.Black
			};
			neutralWindow.Show();
			neutralWindow.Activate();
			await Task.Delay(450);
			foreach (string edge in new[] { "Left", "Top", "Right", "Bottom" })
			{
				Stopwatch transition = Stopwatch.StartNew();
				TaskbarWindow.ApplyPositionForDiagnostics(edge);
				Logger.Log($"[taskbar-runtime-qa] validating edge={edge}");
				EdgeResult result = default;
				do
				{
					await Task.Delay(16);
					result = ValidateEdge(edge);
				} while (!result.Passed && transition.ElapsedMilliseconds < 1500);
				transition.Stop();
				bool fast = result.Passed && transition.ElapsedMilliseconds < 500;
				passed &= fast;
				checks.Add(new
				{
					Edge = edge,
					Passed = fast,
					TransitionMs = transition.ElapsedMilliseconds,
					Bars = result.Bars,
					Screens = result.Screens,
					Failures = result.Failures,
					Observations = result.Observations
				});
			}

			Process current = Process.GetCurrentProcess();
			int handlesBefore = current.HandleCount;
			Stopwatch rebuild = Stopwatch.StartNew();
			manager.RebuildForDiagnostics();
			EdgeResult rebuildResult = default;
			do
			{
				await Task.Delay(16);
				rebuildResult = ValidateEdge("Bottom");
			} while (!rebuildResult.Passed && rebuild.ElapsedMilliseconds < 1500);
			rebuild.Stop();
			current.Refresh();
			int handlesAfter = current.HandleCount;
			int handleGrowth = handlesAfter - handlesBefore;
			bool rebuildPassed = rebuildResult.Passed
				&& manager.BarCountForDiagnostics == Screen.AllScreens.Length
				&& rebuild.ElapsedMilliseconds < 1000
				&& handleGrowth < 500;
			passed &= rebuildPassed;
			checks.Add(new
			{
				Edge = "TopologyRebuild",
				Passed = rebuildPassed,
				TransitionMs = rebuild.ElapsedMilliseconds,
				Bars = rebuildResult.Bars,
				Screens = rebuildResult.Screens,
				ManagerBars = manager.BarCountForDiagnostics,
				HandleGrowth = handleGrowth,
				Failures = rebuildResult.Failures,
				Observations = rebuildResult.Observations
			});

			foreach (Screen screen in Screen.AllScreens)
			{
				FullscreenResult fullscreen = await ValidateFullscreenRoundTrip(neutralWindow, screen);
				passed &= fullscreen.Passed;
				checks.Add(new
				{
					Edge = "FullscreenRoundTrip", Monitor = screen.DeviceName,
					fullscreen.Passed,
					WorkAreaPolicy = "ReservedStable",
					fullscreen.ReservedWorkArea,
					fullscreen.EnterTransitionMs,
					fullscreen.StableForMs,
					fullscreen.ExitTransitionMs,
					fullscreen.Failures
				});
			}
		}
		catch (Exception ex)
		{
			passed = false;
			checks.Add(new { Edge = "runtime", Passed = false, Error = ex.ToString() });
		}
		finally
		{
			TaskbarWindow.ApplyPositionForDiagnostics(original);
			await Task.Delay(250);
			TaskbarWindow.EndPositionDiagnostics();
			try { neutralWindow?.Close(); } catch { }
		}

		Dictionary<string, string?> after = stateNames.ToDictionary(name => name, name => Hash(Path.Combine(dataRoot, name)));
		bool stateUnchanged = before.All(item => after[item.Key] == item.Value);
		passed &= stateUnchanged;
		Process process = Process.GetCurrentProcess();
		object report = new
		{
			SchemaVersion = 2,
			LauncherHash = Hash(typeof(App).Assembly.Location),
			Passed = passed,
			GeneratedUtc = DateTime.UtcNow,
			OriginalEdge = original,
			RestoredEdge = SettingsStore.Load().TaskbarPosition,
			MutableStateUnchanged = stateUnchanged,
			ForeignCompositionHookActive = DesktopComposition.ForegroundHookActiveForDiagnostics,
			ElapsedMs = total.ElapsedMilliseconds,
			WorkingSetBytes = process.WorkingSet64,
			PrivateBytes = process.PrivateMemorySize64,
			Handles = process.HandleCount,
			Checks = checks
		};
		string qaRoot = Path.Combine(dataRoot, "qa");
		Directory.CreateDirectory(qaRoot);
		string reportPath = Path.Combine(qaRoot, "taskbar-runtime-qa-latest.json");
		File.WriteAllText(reportPath, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
		Logger.Log($"[taskbar-runtime-qa] passed={passed} stateUnchanged={stateUnchanged} elapsed={total.ElapsedMilliseconds}ms report={reportPath}");
		application.Shutdown(passed ? 0 : 1);
	}

	private static EdgeResult ValidateEdge(string edge)
	{
		Screen[] screens = Screen.AllScreens;
		List<Rectangle> bars = FindTaskbars();
		List<string> failures = new List<string>();
		List<string> observations = new List<string>();
		if (bars.Count != screens.Length)
		{
			failures.Add($"bar-count {bars.Count} != screen-count {screens.Length}");
		}
		foreach (Screen screen in screens)
		{
			Rectangle bounds = screen.Bounds;
			Rectangle bar = bars.FirstOrDefault(candidate => bounds.Contains(candidate.Left + candidate.Width / 2, candidate.Top + candidate.Height / 2));
			if (bar.Width <= 0 || bar.Height <= 0)
			{
				failures.Add(screen.DeviceName + ": no taskbar");
				continue;
			}
			bool exact = edge switch
			{
				"Top" => bar.Left == bounds.Left && bar.Top == bounds.Top && bar.Right == bounds.Right,
				"Left" => bar.Left == bounds.Left && bar.Top == bounds.Top && bar.Bottom == bounds.Bottom,
				"Right" => bar.Right == bounds.Right && bar.Top == bounds.Top && bar.Bottom == bounds.Bottom,
				_ => bar.Left == bounds.Left && bar.Right == bounds.Right && bar.Bottom == bounds.Bottom
			};
			if (!exact)
			{
				failures.Add($"{screen.DeviceName}: bar={bar} bounds={bounds}");
			}

			Rectangle expectedWork = edge switch
			{
				"Top" => Rectangle.FromLTRB(bounds.Left, bar.Bottom, bounds.Right, bounds.Bottom),
				"Left" => Rectangle.FromLTRB(bar.Right, bounds.Top, bounds.Right, bounds.Bottom),
				"Right" => Rectangle.FromLTRB(bounds.Left, bounds.Top, bar.Left, bounds.Bottom),
				_ => Rectangle.FromLTRB(bounds.Left, bounds.Top, bounds.Right, bar.Top)
			};
			Rectangle actualWork = TaskbarWorkArea.Current(screen);
			Rectangle managedWork = screen.WorkingArea;
			if (actualWork != expectedWork)
			{
				failures.Add($"{screen.DeviceName}: native-work={actualWork} managed-work={managedWork} expected={expectedWork}");
			}
			else if (managedWork != expectedWork)
			{
				observations.Add($"{screen.DeviceName}: WinForms Screen cache={managedWork}; fresh native-work={actualWork}");
			}
		}
		return new EdgeResult(failures.Count == 0, bars.Count, screens.Length, failures.ToArray(), observations.ToArray());
	}

	private static async Task<FullscreenResult> ValidateFullscreenRoundTrip(System.Windows.Window neutralWindow, Screen screen)
	{
		List<string> failures = new List<string>();
		Process? probe = null;
		long enterMs = -1;
		long exitMs = -1;
		Rectangle reservedWorkArea = Rectangle.Empty;
		const int stabilityMs = 900;
		try
		{
			Rectangle bounds = screen.Bounds;
			reservedWorkArea = TaskbarWorkArea.Current(screen);
			string executable = Environment.ProcessPath
				?? Process.GetCurrentProcess().MainModule?.FileName
				?? throw new InvalidOperationException("Cannot resolve the diagnostic executable path.");
			probe = Process.Start(new ProcessStartInfo
			{
				FileName = executable,
				Arguments = "--fullscreen-probe",
				UseShellExecute = false
			});
			if (probe == null)
			{
				return new FullscreenResult(false, reservedWorkArea, -1, 0, -1, new[] { "fullscreen probe did not start" });
			}

			nint handle = nint.Zero;
			Stopwatch windowWait = Stopwatch.StartNew();
			while (handle == nint.Zero && windowWait.ElapsedMilliseconds < 5000 && !probe.HasExited)
			{
				await Task.Delay(40);
				probe.Refresh();
				handle = probe.MainWindowHandle;
			}
			if (handle == nint.Zero)
			{
				return new FullscreenResult(false, reservedWorkArea, -1, 0, -1, new[] { "fullscreen probe window was not created" });
			}
			TaskbarWindow.SetFullscreenDiagnostics(true, handle);

			if (!SetWindowPos(handle, nint.Zero, bounds.Left, bounds.Top, bounds.Width, bounds.Height, 0x0040u))
			{
				failures.Add("fullscreen SetWindowPos failed: " + Marshal.GetLastWin32Error());
			}
			SetForegroundWindow(handle);

			Stopwatch enter = Stopwatch.StartNew();
			bool entered = false;
			while (enter.ElapsedMilliseconds < 2000)
			{
				await Task.Delay(25);
				if (!HasVisibleTaskbar(screen) && TaskbarWorkArea.Current(screen) == reservedWorkArea)
				{
					entered = true;
					break;
				}
			}
			enterMs = enter.ElapsedMilliseconds;
			if (!entered)
			{
				failures.Add($"fullscreen enter did not hide taskbar while preserving reserved work area within {enterMs}ms; expected-work={reservedWorkArea} actual-work={TaskbarWorkArea.Current(screen)}");
			}

			if (entered)
			{
				foreach (Screen other in Screen.AllScreens.Where(value => value.DeviceName != screen.DeviceName))
				{
					if (!HasVisibleTaskbar(other)) failures.Add("fullscreen incorrectly hid taskbar on " + other.DeviceName);
				}
				Stopwatch stable = Stopwatch.StartNew();
				while (stable.ElapsedMilliseconds < stabilityMs)
				{
					await Task.Delay(75);
					Rectangle actualWorkArea = TaskbarWorkArea.Current(screen);
					if (HasVisibleTaskbar(screen) || actualWorkArea != reservedWorkArea)
					{
						failures.Add($"taskbar/work area oscillated after fullscreen entry at {stable.ElapsedMilliseconds}ms; expected-work={reservedWorkArea} actual-work={actualWorkArea}");
						break;
					}
				}
			}

			if (!SetWindowPos(handle, nint.Zero, bounds.Left + 120, bounds.Top + 120, 480, 320, 0x0040u))
			{
				failures.Add("windowed SetWindowPos failed: " + Marshal.GetLastWin32Error());
			}
			SetForegroundWindow(handle);

			Stopwatch exit = Stopwatch.StartNew();
			EdgeResult restored = default;
			do
			{
				await Task.Delay(25);
				restored = ValidateEdge("Bottom");
			} while (!restored.Passed && exit.ElapsedMilliseconds < 2000);
			exitMs = exit.ElapsedMilliseconds;
			if (!restored.Passed)
			{
				failures.AddRange(restored.Failures.Select(value => "fullscreen exit: " + value));
			}

			probe.CloseMainWindow();
			if (!probe.WaitForExit(2000))
			{
				probe.Kill(entireProcessTree: true);
				probe.WaitForExit(2000);
			}
			neutralWindow.Activate();
		}
		catch (Exception ex)
		{
			failures.Add(ex.GetType().Name + ": " + ex.Message);
		}
		finally
		{
			TaskbarWindow.SetFullscreenDiagnostics(false);
			try
			{
				if (probe != null && !probe.HasExited)
				{
					probe.Kill(entireProcessTree: true);
				}
			}
			catch
			{
			}
		}
		return new FullscreenResult(failures.Count == 0, reservedWorkArea, enterMs, stabilityMs, exitMs, failures.ToArray());
	}

	private static bool HasVisibleTaskbar(Screen screen)
	{
		Rectangle bounds = screen.Bounds;
		return FindTaskbars().Any(candidate =>
			bounds.Contains(candidate.Left + candidate.Width / 2, candidate.Top + candidate.Height / 2));
	}

	private static List<Rectangle> FindTaskbars()
	{
		List<Rectangle> result = new List<Rectangle>();
		foreach (System.Windows.Window window in System.Windows.Application.Current.Windows)
		{
			if (window is not TaskbarWindow)
			{
				continue;
			}
			nint handle = new System.Windows.Interop.WindowInteropHelper(window).Handle;
			if (handle != nint.Zero && GetWindowRect(handle, out RECT rect))
			{
				if (IsWindowVisible(handle))
				{
					result.Add(Rectangle.FromLTRB(rect.Left, rect.Top, rect.Right, rect.Bottom));
				}
			}
		}
		return result;
	}

	private static string? Hash(string path)
	{
		return File.Exists(path) ? Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))) : null;
	}

	private readonly record struct EdgeResult(bool Passed, int Bars, int Screens, string[] Failures, string[] Observations);

	private readonly record struct FullscreenResult(bool Passed, Rectangle ReservedWorkArea, long EnterTransitionMs, int StableForMs, long ExitTransitionMs, string[] Failures);
}
