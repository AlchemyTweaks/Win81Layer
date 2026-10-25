#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;

namespace Win81Layer;

// --contextmenutest, the visual part (after the timing part, so it never affects the budgets): the real taskbar and
// desktop menus and a submenu in light, dark and Dark rest, and the menu entrance as a filmstrip (the MenuMotion clocks
// applied to an offscreen menu and seeked frame by frame; no popup is opened).
internal static partial class ContextMenuDiagnostics
{
	internal sealed record FilmFrame(double Ms, double Opacity, double OffsetY);

	internal sealed record Filmstrip(bool Ok, string Detail, List<FilmFrame> Frames);

	private static readonly double[] FilmTimes = new double[6] { 0, 17, 33, 67, 125, 250 };

	internal static Filmstrip RenderEntranceFilmstrip(string directory)
	{
		ContextMenu menu = BuildRepresentativeDynamicMenu();
		TaskbarContextMenu.PrepareForInstantOpen(menu);
		int w = (int)Math.Ceiling(menu.ActualWidth);
		int h = (int)Math.Ceiling(menu.ActualHeight);
		// a still of the settled menu, drawn at each frame's opacity and offset (the values come from the real clocks)
		RenderTargetBitmap still = new RenderTargetBitmap(Math.Max(1, w), Math.Max(1, h), 96, 96, PixelFormats.Pbgra32);
		still.Render(menu);
		still.Freeze();
		(AnimationClock fade, AnimationClock move) = MenuMotion.CreateEntranceClocks(menu, MenuMotion.Distance);
		List<FilmFrame> frames = new List<FilmFrame>();
		List<BitmapSource> shots = new List<BitmapSource>();
		foreach (double ms in FilmTimes.Concat(new double[1] { 83 }).OrderBy(x => x))
		{
			fade.Controller!.SeekAlignedToLastTick(TimeSpan.FromMilliseconds(ms), TimeSeekOrigin.BeginTime);
			move.Controller!.SeekAlignedToLastTick(TimeSpan.FromMilliseconds(ms), TimeSeekOrigin.BeginTime);
			menu.UpdateLayout();
			double opacity = menu.Opacity;
			double offset = MenuMotion.CurrentOffsetY(menu);
			frames.Add(new FilmFrame(ms, Math.Round(opacity, 4), Math.Round(offset, 4)));
			if (FilmTimes.Contains(ms))
			{
				RenderTargetBitmap bmp = new RenderTargetBitmap(Math.Max(1, w), Math.Max(1, h + 12), 96, 96, PixelFormats.Pbgra32);
				DrawingVisual dv = new DrawingVisual();
				using (DrawingContext dc = dv.RenderOpen())
				{
					dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(0x30, 0x30, 0x30)), null, new Rect(0, 0, w, h + 12));
					dc.PushTransform(new TranslateTransform(0, 6 + offset));
					dc.PushOpacity(opacity);
					dc.DrawImage(still, new Rect(0, 0, w, h));
					dc.Pop();
					dc.Pop();
				}
				bmp.Render(dv);
				bmp.Freeze();
				shots.Add(bmp);
			}
		}
		fade.Controller!.Stop();
		move.Controller!.Stop();
		// the strip: one column per frame, labelled with its time
		int gap = 12;
		int stripW = shots.Count * (w + gap) + gap;
		int stripH = h + 12 + 40;
		DrawingVisual strip = new DrawingVisual();
		using (DrawingContext dc = strip.RenderOpen())
		{
			dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(0x20, 0x20, 0x20)), null, new Rect(0, 0, stripW, stripH));
			for (int i = 0; i < shots.Count; i++)
			{
				double x = gap + i * (w + gap);
				dc.DrawImage(shots[i], new Rect(x, 28, w, h + 12));
				FormattedText ft = new FormattedText($"{FilmTimes[i]:0} ms", System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), 13, Brushes.White, 1.0);
				dc.DrawText(ft, new Point(x, 6));
			}
		}
		RenderTargetBitmap out1 = new RenderTargetBitmap(stripW, stripH, 96, 96, PixelFormats.Pbgra32);
		out1.Render(strip);
		Save(out1, Path.Combine(directory, "context-menu-entrance-filmstrip.png"));
		FilmFrame f0 = frames.First(f => f.Ms == 0);
		FilmFrame f83 = frames.First(f => f.Ms == 83);
		FilmFrame f250 = frames.First(f => f.Ms == 250);
		bool ok = f0.Opacity <= 0.0001 && Math.Abs(f83.Opacity - 1.0) <= 0.0001 && f250.OffsetY == 0.0 && Math.Abs(f0.OffsetY - MenuMotion.Distance) < 0.0001;
		string detail = string.Join(" ", frames.Select(f => $"{f.Ms:0}ms:o={f.Opacity:0.000},y={f.OffsetY:0.00}"));
		return new Filmstrip(ok, detail, frames);
	}

	// The real menus in the three looks: contextmenu-taskbar-<look>.png, contextmenu-desktop-<look>.png,
	// contextmenu-submenu-<look>.png (the taskbar menu's "Taskbar position" items, drawn as their submenu) and
	// contextmenu-jumplist-<look>.png (a running window's menu with its "Recent" caption).
	internal static List<string> RenderLooks(string directory)
	{
		List<string> written = new List<string>();
		bool realDark = ShellTheme.IsDark;
		AppSettings real = SettingsStore.Load();
		try
		{
			foreach (string look in new string[3] { "light", "dark", "rest" })
			{
				if (look == "rest")
				{
					AppSettings rest = real.Clone();
					rest.SeasonalActive = SeasonalTheme.Rest;
					SettingsStore.DiagnosticsOverride = rest;
					ShellTheme.ForceForTest(true);
				}
				else
				{
					SettingsStore.DiagnosticsOverride = null;
					ShellTheme.ForceForTest(look == "dark");
				}
				ShellTheme.SyncRest(force: true);
				TaskbarContextMenu.InvalidateTheme();
				ContextMenu taskbar = TaskbarContextMenu.Build(null!);
				written.Add(RenderMenu(taskbar, Path.Combine(directory, $"contextmenu-taskbar-{look}.png")));
				ContextMenu desktop = DesktopContextMenu.Build();
				written.Add(RenderMenu(desktop, Path.Combine(directory, $"contextmenu-desktop-{look}.png")));
				MenuItem? position = taskbar.Items.OfType<MenuItem>().FirstOrDefault(m => m.Header as string == "Taskbar position");
				if (position != null)
				{
					ContextMenu sub = new ContextMenu { Style = (Style)Application.Current.Resources["Win81ContextMenu"] };
					TaskbarContextMenu.ApplyTheme(sub);
					foreach (MenuItem child in position.Items.OfType<MenuItem>().ToList())
					{
						position.Items.Remove(child);
						sub.Items.Add(child);
					}
					written.Add(RenderMenu(sub, Path.Combine(directory, $"contextmenu-submenu-{look}.png")));
				}
				// a running window's menu with its jump list (the "Recent" caption row, text-only recent items), built
				// from the same pieces as TaskbarWindow's (no real window or jump list is read)
				ContextMenu jump = new ContextMenu { Style = (Style)Application.Current.Resources["Win81ContextMenu"] };
				TaskbarContextMenu.ApplyTheme(jump);
				jump.Items.Add(TaskbarContextMenu.Caption("Recent"));
				foreach (string name in new string[3] { "Budget 2026.xlsx", "Notes.txt", "Holiday photos" })
				{
					jump.Items.Add(TaskbarContextMenu.Leaf(name, delegate { }));
				}
				jump.Items.Add(TaskbarContextMenu.Sep());
				jump.Items.Add(TaskbarContextMenu.Leaf("Open new window", 57724, delegate { }));
				jump.Items.Add(TaskbarContextMenu.Leaf("Switch to", 59559, delegate { }));
				jump.Items.Add(TaskbarContextMenu.Sub("More", 59154, TaskbarContextMenu.Leaf("Open file location", delegate { })));
				jump.Items.Add(TaskbarContextMenu.Sep());
				jump.Items.Add(TaskbarContextMenu.Leaf("Close window", 59579, delegate { }));
				written.Add(RenderMenu(jump, Path.Combine(directory, $"contextmenu-jumplist-{look}.png")));
			}
		}
		finally
		{
			SettingsStore.DiagnosticsOverride = null;
			ShellTheme.ForceForTest(realDark);
			ShellTheme.SyncRest(force: true);
			TaskbarContextMenu.InvalidateTheme();
		}
		return written;
	}

	private static string RenderMenu(ContextMenu menu, string path)
	{
		TaskbarContextMenu.PrepareForInstantOpen(menu);
		int w = Math.Max(1, (int)Math.Ceiling(menu.ActualWidth));
		int h = Math.Max(1, (int)Math.Ceiling(menu.ActualHeight));
		RenderTargetBitmap menuBmp = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
		menuBmp.Render(menu);
		DrawingVisual dv = new DrawingVisual();
		using (DrawingContext dc = dv.RenderOpen())
		{
			dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(0x30, 0x30, 0x30)), null, new Rect(0, 0, w + 40, h + 40));
			dc.DrawImage(menuBmp, new Rect(20, 20, w, h));
		}
		RenderTargetBitmap bmp = new RenderTargetBitmap(w + 40, h + 40, 96, 96, PixelFormats.Pbgra32);
		bmp.Render(dv);
		Save(bmp, path);
		return Path.GetFileName(path) + $" {w}x{h}";
	}

	private static void Save(BitmapSource bmp, string path)
	{
		PngBitmapEncoder enc = new PngBitmapEncoder();
		enc.Frames.Add(BitmapFrame.Create(bmp));
		using FileStream fs = File.Create(path);
		enc.Save(fs);
	}
}
