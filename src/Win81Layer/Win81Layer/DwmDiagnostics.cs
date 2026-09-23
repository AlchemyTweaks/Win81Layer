using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace Win81Layer;

// Phase-0 DWM diagnostics + capability detection (spec §4.1 / §15 / §18).
// ON-DEMAND only — this is NOT a polling loop and installs no per-frame or per-vsync hook. It samples the SYSTEM
// composition timeline via DwmGetCompositionTimingInfo (NULL HWND, per the Win8.1+ contract), takes a short delta,
// and writes one baseline report. Gated behind EnableCompositionDiagnostics for any in-process use; the --dwmdiag
// CLI runs one capture out-of-process (a second, mutex-free instance) so it never perturbs the live shell.
public static class DwmDiagnostics
{
	// Default OFF (spec §23). Only the explicit --dwmdiag CLI or a deliberate call captures anything.
	public static bool EnableCompositionDiagnostics = false;

	[StructLayout(LayoutKind.Sequential)]
	private struct UNSIGNED_RATIO { public uint uiNumerator; public uint uiDenominator; }

	[StructLayout(LayoutKind.Sequential)]
	private struct DWM_TIMING_INFO
	{
		public uint cbSize;
		public UNSIGNED_RATIO rateRefresh;
		public ulong qpcRefreshPeriod;
		public UNSIGNED_RATIO rateCompose;
		public ulong qpcVBlank;
		public ulong cRefresh;
		public uint cDXRefresh;
		public ulong qpcCompose;
		public ulong cFrame;
		public uint cDXPresent;
		public ulong cRefreshFrame;
		public ulong cFrameSubmitted;
		public uint cDXPresentSubmitted;
		public ulong cFrameConfirmed;
		public uint cDXPresentConfirmed;
		public ulong cRefreshConfirmed;
		public uint cDXRefreshConfirmed;
		public ulong cFramesLate;
		public uint cFramesOutstanding;
		public ulong cFrameDisplayed;
		public ulong qpcFrameDisplayed;
		public ulong cRefreshFrameDisplayed;
		public ulong cFrameComplete;
		public ulong qpcFrameComplete;
		public ulong cFramePending;
		public ulong qpcFramePending;
		public ulong cFramesDisplayed;
		public ulong cFramesComplete;
		public ulong cFramesPending;
		public ulong cFramesAvailable;
		public ulong cFramesDropped;
		public ulong cFramesMissed;
		public ulong cRefreshNextDisplayed;
		public ulong cRefreshNextPresented;
		public ulong cRefreshesDisplayed;
		public ulong cRefreshesPresented;
		public ulong cRefreshStarted;
		public ulong cPixelsReceived;
		public ulong cPixelsDrawn;
		public ulong cBuffersEmpty;
	}

	// Capture the system composition timing snapshot. Returns false (and hr) if the call is unsupported. Tries the
	// documented NULL-HWND global timing first; if this context refuses it (a windowless helper process / hooked
	// dwmcore), falls back to the desktop HWND which supplies a composition context.
	private static bool TryTiming(out DWM_TIMING_INFO ti, out int hr)
	{
		ti = default(DWM_TIMING_INFO);
		hr = 0;
		try
		{
			ti = default(DWM_TIMING_INFO);
			ti.cbSize = (uint)Marshal.SizeOf<DWM_TIMING_INFO>();
			hr = DwmGetCompositionTimingInfo(IntPtr.Zero, ref ti);   // NULL HWND = whole-desktop composition (Win8.1+)
			if (hr == 0) { return true; }
			ti = default(DWM_TIMING_INFO);
			ti.cbSize = (uint)Marshal.SizeOf<DWM_TIMING_INFO>();
			hr = DwmGetCompositionTimingInfo(GetDesktopWindow(), ref ti);   // fallback: a real composed HWND
			return hr == 0;
		}
		catch (Exception ex)
		{
			hr = -1;
			Logger.Log("DwmGetCompositionTimingInfo threw: " + ex.Message);
			return false;
		}
	}

	// One baseline report: capability snapshot + a short system-composition delta + process counters for the live
	// managed shell (if running). Writes to %LocalAppData%\Win81Layer\dwm-diag\baseline-<utc>.txt and returns the path.
	public static string Baseline(int sampleMs = 1500)
	{
		StringBuilder sb = new StringBuilder();
		sb.AppendLine("=== Win81Layer DWM diagnostics baseline ===");
		sb.AppendLine("captured (local): " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));

		// --- capability snapshot (spec §15) ---
		sb.AppendLine();
		sb.AppendLine("[capabilities]");
		try
		{
			sb.AppendLine(CompositionCapabilities.Describe());   // ensures detection internally
		}
		catch (Exception ex) { sb.AppendLine("capabilities error: " + ex.Message); }
		AppSettings settings = SettingsStore.Load();
		CompositionProfile profile = CompositionProfiles.Resolve(DesktopComposition.EffectiveMode);
		sb.AppendLine("DWMBlurGlass installed: " + DwmBlurGlassBridge.Installed);
		sb.AppendLine("DWMBlurGlass policy: permanently disabled");
		sb.AppendLine("DWMBlurGlass host running: " + DwmBlurGlassBridge.HostRunning);
		sb.AppendLine("DWMBlurGlass injected (real foreign glass): " + DwmBlurGlassBridge.Injected);
		sb.AppendLine("DWMBlurGlass state: " + DwmBlurGlassBridge.DescribeState(profile, settings.UseDwmBlurGlassForWin7));

		// --- system composition timing delta (spec §4.1) ---
		sb.AppendLine();
		sb.AppendLine("[system composition timing — DwmGetCompositionTimingInfo(NULL)]");
		if (TryTiming(out DWM_TIMING_INFO a, out int hrA))
		{
			System.Threading.Thread.Sleep(Math.Max(200, sampleMs));
			if (TryTiming(out DWM_TIMING_INFO b, out int hrB) && hrB == 0)
			{
				double hz = (a.rateRefresh.uiDenominator != 0) ? (double)a.rateRefresh.uiNumerator / a.rateRefresh.uiDenominator : 0.0;
				double cHz = (a.rateCompose.uiDenominator != 0) ? (double)a.rateCompose.uiNumerator / a.rateCompose.uiDenominator : 0.0;
				sb.AppendLine("refresh rate           : " + hz.ToString("F2", CultureInfo.InvariantCulture) + " Hz");
				sb.AppendLine("compose rate           : " + cHz.ToString("F2", CultureInfo.InvariantCulture) + " Hz");
				sb.AppendLine("qpcRefreshPeriod       : " + a.qpcRefreshPeriod);
				sb.AppendLine("--- over " + sampleMs + " ms (deltas) ---");
				sb.AppendLine("frames displayed       : " + D(b.cFramesDisplayed, a.cFramesDisplayed));
				sb.AppendLine("frames complete        : " + D(b.cFramesComplete, a.cFramesComplete));
				sb.AppendLine("frames pending         : " + D(b.cFramesPending, a.cFramesPending));
				sb.AppendLine("frames available       : " + D(b.cFramesAvailable, a.cFramesAvailable));
				sb.AppendLine("frames LATE            : " + D(b.cFramesLate, a.cFramesLate));
				sb.AppendLine("frames DROPPED         : " + D(b.cFramesDropped, a.cFramesDropped));
				sb.AppendLine("frames MISSED          : " + D(b.cFramesMissed, a.cFramesMissed));
				sb.AppendLine("frames outstanding(now): " + b.cFramesOutstanding);
				sb.AppendLine("pixels received        : " + D(b.cPixelsReceived, a.cPixelsReceived));
				sb.AppendLine("pixels drawn           : " + D(b.cPixelsDrawn, a.cPixelsDrawn));
				sb.AppendLine("buffers empty          : " + D(b.cBuffersEmpty, a.cBuffersEmpty));
			}
			else
			{
				sb.AppendLine("second timing sample failed (hr=0x" + hrB.ToString("X8") + ")");
			}
		}
		else
		{
			sb.AppendLine("DwmGetCompositionTimingInfo unavailable (hr=0x" + hrA.ToString("X8") + ") — timing tier skipped; struct layout or OS gate.");
		}

		// --- live shell process counters (spec §18) ---
		sb.AppendLine();
		sb.AppendLine("[launcher process counters]");
		try
		{
			Process shell = FindLiveShell();
			if (shell != null)
			{
				shell.Refresh();
				sb.AppendLine("target                 : pid " + shell.Id + " (live managed shell)");
				AppendProc(sb, shell);
				shell.Dispose();
			}
			else
			{
				Process self = Process.GetCurrentProcess();
				sb.AppendLine("target                 : this process (no live managed shell found)");
				AppendProc(sb, self);
			}
		}
		catch (Exception ex) { sb.AppendLine("process counters error: " + ex.Message); }

		sb.AppendLine();
		sb.AppendLine("NOTE: full ETW/GPUView/PresentMon traces are a separate harness step (Phase 3). This baseline is");
		sb.AppendLine("the in-process, dependency-free tier: documented DWM timing + supported process counters.");

		string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Win81Layer", "dwm-diag");
		Directory.CreateDirectory(dir);
		string path = Path.Combine(dir, "baseline-" + DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + ".txt");
		try { File.WriteAllText(path, sb.ToString()); } catch { }
		try { Logger.Log("DWM diag baseline written: " + path); } catch { }
		return path;
	}

	// Phase-3 on-demand, BOUNDED frame-timing burst. MUST be called from INSIDE the live composed shell (which owns
	// visible DWM-composed windows and a running render thread) so the process has a real composition session — that is
	// why this can return S_OK where the windowless --dwmdiag helper gets 0x88980090. The HWND argument is largely
	// ignored on Win8.1+ (NULL = whole-desktop timing); we still try the shell's own composed HWND first, then NULL,
	// then the desktop, logging each hr. Samples N times then STOPS — no persistent hook, no infinite loop. If every
	// probe fails it writes an honest "unavailable" report and NEVER fabricates frame numbers.
	public static string FrameTimingBurst(nint shellHwnd, int frames = 300, int intervalMs = 16)
	{
		if (frames < 1) { frames = 1; }
		if (frames > 2000) { frames = 2000; }   // hard cap — bounded burst, never runaway
		if (intervalMs < 1) { intervalMs = 1; }
		StringBuilder sb = new StringBuilder();
		sb.AppendLine("=== Win81Layer DWM frame-timing burst (Phase 3) ===");
		sb.AppendLine("captured (local): " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));
		sb.AppendLine("shell HWND: 0x" + shellHwnd.ToString("X"));
		sb.AppendLine("DWMBlurGlass host running: " + DwmBlurGlassBridge.HostRunning);
		sb.AppendLine("DWMBlurGlass injected: " + DwmBlurGlassBridge.Injected);

		// Try owned composed HWND first, then NULL (documented Win8.1+ global), then desktop.
		nint[] targets = (shellHwnd != 0)
			? new nint[] { shellHwnd, IntPtr.Zero, GetDesktopWindow() }
			: new nint[] { IntPtr.Zero, GetDesktopWindow() };
		nint use = 0;
		int hr0 = unchecked((int)0x80004005);
		bool ok = false;
		foreach (nint t in targets)
		{
			DWM_TIMING_INFO probe = default(DWM_TIMING_INFO);
			probe.cbSize = (uint)Marshal.SizeOf<DWM_TIMING_INFO>();
			hr0 = DwmGetCompositionTimingInfo(t, ref probe);
			sb.AppendLine("probe hwnd 0x" + t.ToString("X") + " -> hr=0x" + hr0.ToString("X8"));
			if (hr0 == 0) { use = t; ok = true; break; }
		}
		if (!ok)
		{
			sb.AppendLine("NO composition-timing source available (last hr=0x" + hr0.ToString("X8") + "). Honest degrade: no frame numbers emitted. Use the Phase-3 ETW/PresentMon harness (separate, elevated).");
			return WriteBurst(sb);
		}

		DWM_TIMING_INFO prev = default(DWM_TIMING_INFO);
		prev.cbSize = (uint)Marshal.SizeOf<DWM_TIMING_INFO>();
		DwmGetCompositionTimingInfo(use, ref prev);
		double hz = (prev.rateRefresh.uiDenominator != 0) ? (double)prev.rateRefresh.uiNumerator / prev.rateRefresh.uiDenominator : 0.0;
		sb.AppendLine("refresh rate: " + hz.ToString("F2", CultureInfo.InvariantCulture) + " Hz");
		sb.AppendLine("--- per-sample deltas (frames=" + frames + ", ~" + intervalMs + "ms each) ---");
		ulong sumLate = 0, sumDropped = 0, sumMissed = 0;
		for (int i = 0; i < frames; i++)
		{
			System.Threading.Thread.Sleep(intervalMs);
			DWM_TIMING_INFO cur = default(DWM_TIMING_INFO);
			cur.cbSize = (uint)Marshal.SizeOf<DWM_TIMING_INFO>();
			if (DwmGetCompositionTimingInfo(use, ref cur) != 0) { sb.AppendLine(i + ": (sample failed)"); continue; }
			ulong late = Delta(cur.cFramesLate, prev.cFramesLate);
			ulong dropped = Delta(cur.cFramesDropped, prev.cFramesDropped);
			ulong missed = Delta(cur.cFramesMissed, prev.cFramesMissed);
			sumLate += late; sumDropped += dropped; sumMissed += missed;
			sb.AppendLine(i + ": disp=" + Delta(cur.cFramesDisplayed, prev.cFramesDisplayed) + " late=" + late + " dropped=" + dropped + " missed=" + missed + " outstanding=" + cur.cFramesOutstanding);
			prev = cur;
		}
		sb.AppendLine("--- totals over burst --- late=" + sumLate + " dropped=" + sumDropped + " missed=" + sumMissed);
		return WriteBurst(sb);
	}

	private static ulong Delta(ulong now, ulong then) => (now >= then) ? (now - then) : 0UL;

	private static string WriteBurst(StringBuilder sb)
	{
		string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Win81Layer", "dwm-diag");
		Directory.CreateDirectory(dir);
		string path = Path.Combine(dir, "frametiming-" + DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + ".txt");
		try { File.WriteAllText(path, sb.ToString()); } catch { }
		try { Logger.Log("DWM frame-timing burst written: " + path); } catch { }
		return path;
	}

	private static void AppendProc(StringBuilder sb, Process p)
	{
		sb.AppendLine("working set            : " + Mb(p.WorkingSet64) + " MB");
		sb.AppendLine("private bytes          : " + Mb(p.PrivateMemorySize64) + " MB");
		sb.AppendLine("handles                : " + p.HandleCount);
		sb.AppendLine("threads                : " + p.Threads.Count);
		try
		{
			uint gdi = GetGuiResources(p.Handle, 0);   // GR_GDIOBJECTS
			uint usr = GetGuiResources(p.Handle, 1);   // GR_USEROBJECTS
			sb.AppendLine("GDI objects            : " + gdi);
			sb.AppendLine("USER objects           : " + usr);
		}
		catch (Exception ex) { sb.AppendLine("GetGuiResources error  : " + ex.Message); }
	}

	// Dependency-free: the live managed shell is the heaviest Win81Layer process (a full WPF tree); the supervisor
	// and any watchdog are tiny. Pick the largest-working-set non-self instance.
	private static Process FindLiveShell()
	{
		int me = Environment.ProcessId;
		Process best = null;
		long bestWs = -1L;
		try
		{
			foreach (Process p in Process.GetProcessesByName("Win81Layer"))
			{
				try
				{
					if (p.Id == me) { p.Dispose(); continue; }
					long ws = p.WorkingSet64;
					if (ws > bestWs) { bestWs = ws; best?.Dispose(); best = p; }
					else { p.Dispose(); }
				}
				catch { try { p.Dispose(); } catch { } }
			}
		}
		catch { }
		return best;
	}

	private static string D(ulong now, ulong then) => (now >= then ? (now - then) : 0UL).ToString();
	private static string Mb(long bytes) => (bytes / (1024.0 * 1024.0)).ToString("F1", CultureInfo.InvariantCulture);

	[DllImport("dwmapi.dll")]
	private static extern int DwmGetCompositionTimingInfo(IntPtr hwnd, ref DWM_TIMING_INFO pTimingInfo);

	[DllImport("user32.dll")]
	private static extern uint GetGuiResources(IntPtr hProcess, uint uiFlags);

	[DllImport("user32.dll")]
	private static extern IntPtr GetDesktopWindow();
}
