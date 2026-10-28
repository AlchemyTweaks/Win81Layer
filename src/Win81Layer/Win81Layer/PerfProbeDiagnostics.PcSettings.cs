#nullable enable

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;

namespace Win81Layer;

// --perfprobe <outDir> pcsettings: the PC Settings hub against the fake settings world (no Windows reads that could
// stall, nothing written): open to first frame (warm median of 5 after one cold open), every page build, the private
// bytes it takes while open and what stays after it closed. Offscreen, never activated.
internal static partial class PerfProbeDiagnostics
{
	private static async Task RunPcSettings()
	{
		SettingsWorld.Current = new FakeSettingsWorld("this-pc");
		// The live shell always has windows on screen (taskbar, tray host), which keep WPF's render device alive; without
		// one, every open would also pay for creating the device again. An offscreen anchor window stands in for them.
		Window anchor = new Window
		{
			Left = -5200.0,
			Top = -5200.0,
			Width = 64.0,
			Height = 64.0,
			ShowActivated = false,
			ShowInTaskbar = false,
			WindowStyle = WindowStyle.None
		};
		NoActivate(anchor);
		anchor.Show();
		await NextRender();
		GC.Collect();
		GC.WaitForPendingFinalizers();
		GC.Collect();
		(double priv0, _, _) = Memory();
		long managed0 = GC.GetTotalMemory(true);
		List<double> opens = new List<double>();
		Dictionary<string, double> builds = new Dictionary<string, double>(StringComparer.Ordinal);
		double openPriv = 0.0;
		long managedWarm = managed0;
		double privWarm = priv0;
		for (int i = 0; i < 6; i++)
		{
			Stopwatch sw = Stopwatch.StartNew();
			PcSettingsWindow w = new PcSettingsWindow(null!)
			{
				Left = -5000.0,
				Top = -5000.0,
				ShowActivated = false,
				ShowInTaskbar = false,
				Width = 980.0,
				Height = 760.0
			};
			double ctor = sw.Elapsed.TotalMilliseconds;
			NoActivate(w);
			double handle = sw.Elapsed.TotalMilliseconds;
			w.OpenCategory("home");
			double page = sw.Elapsed.TotalMilliseconds;
			w.Show();
			double shown = sw.Elapsed.TotalMilliseconds;
			await NextRender();
			sw.Stop();
			Note($"pcsettings open {i}: {sw.Elapsed.TotalMilliseconds:0.0} ms (ctor {ctor:0.0}, handle {handle - ctor:0.0}, page {page - handle:0.0}, show {shown - page:0.0}, first frame {sw.Elapsed.TotalMilliseconds - shown:0.0})");
			if (i > 0)
			{
				opens.Add(Math.Round(sw.Elapsed.TotalMilliseconds, 2));
			}
			if (i == 5)
			{
				foreach (HubPage p in HubCatalog.Pages.Where((HubPage x) => x.Id != "recent" && x.Id != "touchpad"))
				{
					w.QaTimeBuild(p.Id);
					builds[p.Id] = Math.Round(w.QaTimeBuild(p.Id), 2);
				}
				(double privOpen, _, _) = Memory();
				openPriv = privOpen - priv0;
			}
			w.Close();
			if (i == 0)
			{
				// what stays after the first (cold) visit is the static part: the catalog, shared styles, WPF's own caches
				await Delay(300);
				GC.Collect();
				GC.WaitForPendingFinalizers();
				GC.Collect();
				managedWarm = GC.GetTotalMemory(true);
				(privWarm, _, _) = Memory();
			}
			// a user does not reopen PC settings within a frame of closing it: let the closed window's HWND go first
			await Delay(600);
		}
		anchor.Close();
		await Delay(500);
		GC.Collect();
		GC.WaitForPendingFinalizers();
		GC.Collect();
		(double priv1, _, _) = Memory();
		long managed1 = GC.GetTotalMemory(true);
		opens.Sort();
		Report["pcsettingsOpenMs"] = opens;
		Report["pcsettingsOpenMedianMs"] = opens.Count > 0 ? opens[opens.Count / 2] : 0.0;
		Report["pcsettingsBuildMs"] = builds;
		Report["pcsettingsBuildMaxMs"] = builds.Count > 0 ? builds.Values.Max() : 0.0;
		Report["pcsettingsOpenPrivateDeltaMB"] = Math.Round(openPriv, 2);
		Report["pcsettingsAfterClosePrivateDeltaMB"] = Math.Round(priv1 - priv0, 2);
		Report["pcsettingsManagedRetainedMB"] = Math.Round((managed1 - managed0) / 1048576.0, 3);
		// five more visits after the first: what each visit leaves behind once closed
		Report["pcsettingsWarmManagedRetainedMB"] = Math.Round((managed1 - managedWarm) / 1048576.0, 3);
		Report["pcsettingsWarmPrivateDeltaMB"] = Math.Round(priv1 - privWarm, 2);
	}
}
