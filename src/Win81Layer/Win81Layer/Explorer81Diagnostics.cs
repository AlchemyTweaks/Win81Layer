#nullable enable

using System;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace Win81Layer;

internal static class Explorer81Diagnostics
{
	private static readonly JsonSerializerOptions JsonOptions = new()
	{
		WriteIndented = true
	};

	internal static void Begin(App app)
	{
		string directory = Path.Combine(
			Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
			"Win81Layer",
			"explorer81qa");
		string screenshotPath = Path.Combine(directory, "explorer81-thispc.png");
		string highDpiScreenshotPath = Path.Combine(directory, "explorer81-thispc-150.png");
		string reportPath = Path.Combine(directory, "explorer81-qa.json");
		Directory.CreateDirectory(directory);
		long baselineWorkingSet = Process.GetCurrentProcess().WorkingSet64;
		long baselineManagedBytes = GC.GetTotalMemory(forceFullCollection: false);

		FileBrowserBody? body = null;
		Win81Window? window = null;
		Stopwatch total = Stopwatch.StartNew();
		try
		{
			body = new FileBrowserBody();
			window = new Win81Window
			{
				Title = "This PC",
				Width = 800,
				Height = 600,
				MinWidth = 640,
				MinHeight = 430,
				Left = -5000,
				Top = -5000,
				ShowActivated = false,
				ShowInTaskbar = false,
				WindowStartupLocation = WindowStartupLocation.Manual
			};
			window.ConfigureExplorer81();
			window.SetBody(body);
			body.TitleChanged += title => window.Title = title;
			body.Open();
			window.Show();
			_ = CompleteAsync(app, window, body, screenshotPath, highDpiScreenshotPath, reportPath, total, baselineWorkingSet, baselineManagedBytes);
		}
		catch (Exception ex)
		{
			WriteReport(reportPath, new
			{
				passed = false,
				error = ex.ToString(),
				screenshotPath,
				totalMilliseconds = total.ElapsedMilliseconds,
				timestampUtc = DateTime.UtcNow
			});
			try { body?.Dispose(); } catch { }
			try { window?.Close(); } catch { }
			app.Shutdown(1);
		}
	}

	private static async Task CompleteAsync(
		App app,
		Win81Window window,
		FileBrowserBody body,
		string screenshotPath,
		string highDpiScreenshotPath,
		string reportPath,
		Stopwatch total,
		long baselineWorkingSet,
		long baselineManagedBytes)
	{
		try
		{
			await body.WaitForReadyAsync(TimeSpan.FromSeconds(8));
			await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
			await Task.Delay(120);
			await window.Dispatcher.InvokeAsync(() =>
			{
				window.UpdateLayout();
				RenderWindow(window, screenshotPath, 800, 600, 1.0);
				RenderWindow(window, highDpiScreenshotPath, 800, 600, 1.5);
			}, DispatcherPriority.Render);

			FileInfo screenshot = new(screenshotPath);
			FileInfo highDpiScreenshot = new(highDpiScreenshotPath);
			long initialNavigationMs = body.QaLastNavigationMilliseconds;
			string desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
			await body.QaNavigateAsync(desktop);
			int desktopItemCount = body.QaVisibleItemCount;
			long desktopNavigationMs = body.QaLastNavigationMilliseconds;
			string? searchQuery = body.QaFirstItemDisplay;
			int searchResultCount = 0;
			if (!string.IsNullOrWhiteSpace(searchQuery))
			{
				await body.QaSearchAsync(searchQuery);
				searchResultCount = body.QaVisibleItemCount;
				await body.QaSearchAsync("");
			}
			body.QaSetViewMode("Icons");
			bool iconsModeApplied = body.QaViewMode == "Icons";
			body.QaSetViewMode("Details");
			bool detailsModeRestored = body.QaViewMode == "Details";
			await body.QaBackAsync();
			bool backReturnedToThisPc = body.QaCurrentTitle == "This PC";
			bool menusMeasured = body.QaMeasureMenus();
			bool passed = body.QaFolderCount == 6
				&& body.QaDriveCount >= 1
				&& initialNavigationMs < 1500
				&& desktopNavigationMs < 1500
				&& desktopItemCount >= 1
				&& (string.IsNullOrWhiteSpace(searchQuery) || searchResultCount >= 1)
				&& iconsModeApplied
				&& detailsModeRestored
				&& backReturnedToThisPc
				&& menusMeasured
				&& screenshot.Exists
				&& screenshot.Length > 10000
				&& highDpiScreenshot.Exists
				&& highDpiScreenshot.Length > 15000;
			long finalWorkingSet = Process.GetCurrentProcess().WorkingSet64;
			long finalManagedBytes = GC.GetTotalMemory(forceFullCollection: false);
			WriteReport(reportPath, new
			{
				passed,
				folderCount = body.QaFolderCount,
				driveCount = body.QaDriveCount,
				initialNavigationMilliseconds = initialNavigationMs,
				desktopNavigationMilliseconds = desktopNavigationMs,
				desktopItemCount,
				searchQuery,
				searchResultCount,
				iconsModeApplied,
				detailsModeRestored,
				backReturnedToThisPc,
				menusMeasured,
				totalMilliseconds = total.ElapsedMilliseconds,
				status = body.QaStatusText,
				screenshotPath,
				screenshotBytes = screenshot.Exists ? screenshot.Length : 0,
				highDpiScreenshotPath,
				highDpiScreenshotBytes = highDpiScreenshot.Exists ? highDpiScreenshot.Length : 0,
				workingSetBytes = finalWorkingSet,
				workingSetDeltaBytes = Math.Max(0, finalWorkingSet - baselineWorkingSet),
				managedBytes = finalManagedBytes,
				managedDeltaBytes = Math.Max(0, finalManagedBytes - baselineManagedBytes),
				renderWidth = 800,
				renderHeight = 600,
				highDpiRenderWidth = 1200,
				highDpiRenderHeight = 900,
				timestampUtc = DateTime.UtcNow
			});
			Logger.Log($"EXPLORER81TEST: passed={passed} initial={initialNavigationMs}ms desktop={desktopNavigationMs}ms folders={body.QaFolderCount} drives={body.QaDriveCount} -> {reportPath}");
			app.Shutdown(passed ? 0 : 1);
		}
		catch (Exception ex)
		{
			WriteReport(reportPath, new
			{
				passed = false,
				error = ex.ToString(),
				screenshotPath,
				totalMilliseconds = total.ElapsedMilliseconds,
				timestampUtc = DateTime.UtcNow
			});
			Logger.Log("EXPLORER81TEST failed: " + ex);
			app.Shutdown(1);
		}
		finally
		{
			try { body.Dispose(); } catch { }
			try { window.Close(); } catch { }
		}
	}

	private static void RenderWindow(Window window, string outputPath, int width, int height, double scale)
	{
		window.Measure(new Size(width, height));
		window.Arrange(new Rect(0, 0, width, height));
		window.UpdateLayout();
		int pixelWidth = Math.Max(1, (int)Math.Round(width * scale));
		int pixelHeight = Math.Max(1, (int)Math.Round(height * scale));
		RenderTargetBitmap bitmap = new(pixelWidth, pixelHeight, 96 * scale, 96 * scale, PixelFormats.Pbgra32);
		bitmap.Render(window);
		PngBitmapEncoder encoder = new();
		encoder.Frames.Add(BitmapFrame.Create(bitmap));
		using FileStream stream = new(outputPath, FileMode.Create, FileAccess.Write, FileShare.Read);
		encoder.Save(stream);
	}

	private static void WriteReport(string path, object report)
	{
		File.WriteAllText(path, JsonSerializer.Serialize(report, JsonOptions));
	}
}
