#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Win81Layer;

// What a notification-area icon with a seasonal picture shows.
internal enum SeasonalTrayLook
{
	// The seasonal picture: the app shows its usual (idle) icon.
	Seasonal,
	// The seasonal picture with the app's badge (unread dot, count, presence dot) copied onto it.
	SeasonalBadge,
	// The app's own current icon: it shows a state that changes the whole icon (call, muted, error, alert blinking).
	Original
}

// State-aware seasonal tray icons. Many apps change their notification-area icon to show a state: an unread dot, a
// mention count, a call, a muted microphone. A static seasonal picture would hide that, so each app's usual (idle) tray
// icon is learned and every new image is compared with it:
//   - the same as idle             -> the seasonal picture;
//   - a small change in one corner -> the seasonal picture with the badge copied onto it (only the badge's own colour,
//                                     separated from the app's logo where the two blend), ringed by a thin contrasting
//                                     halo so the badge reads on the art;
//   - any larger change            -> the app's own icon, exactly as the app draws it, until it is back to idle.
// Rapid alternation (blinking, a speaking indicator) is one state: while it lasts and any of its frames is a large
// change, the app's own frames are shown, so the icon never flips between the picture and the app's art.
//
// Learning, per seasonal key: images are grouped by a small perceptual fingerprint (16 x 16, premultiplied, compared
// with one pixel of shift tolerance so the 16, 20 and 24 px renderings of one state are the same state). The idle
// state is, in order:
//   1. the image the app settles on right after it starts (the first image it keeps for 20 s, so a short "connecting"
//      image does not count); one vote per process, the last five starts decide, and votes for an image that has not
//      been shown for a long time while later starts voted for others are dropped (the app changed its icon);
//   2. else the first image ever seen, unless it took part in a blinking state that changes the whole icon (a call's
//      speaking indicator) or another image has been shown far longer;
//   3. else the image with the longest display time, with some hysteresis; a frame of a whole-icon blinking state
//      cannot take over from a plain image this way, and while such a frame is the idle state it is shown as the
//      app's own icon for a while after its last blink.
// A badge-free base always wins over the same image with a badge. Display time is kept per app, so an icon that is
// recreated (the bar rebuilt after a display change) continues its episode, and an episode closes when the icon goes
// away. Images that are not the app's own tray image (the window's or program's icon while the tray image could not be
// read, a letter tile, a saved snapshot) are never learned. What was learned is kept in seasonal-tray.json in the
// launcher data folder, so restarts and apps that start with a badge keep working.
//
// Cost: nothing runs unless an app changes its tray icon (NIM_ADD/NIM_MODIFY with a new image) or a blinking state,
// a start-up vote or a pending idle change comes due (checked by the regular tray scan with a field read); an image
// sent again is one hash lookup; decisions and composites are cached per image; saves are throttled and serialised and
// written off the UI thread. The setting "original" turns all of this off and the tray shows the apps' own icons.
internal static class SeasonalTrayIcons
{
	// Fingerprint side: every comparison runs on 16 x 16 premultiplied pixels.
	internal const int S = 16;

	// Largest native image kept per state (larger icons are fitted to this first).
	private const int MaxNative = 64;

	// A pixel differs when its visible difference over black or white is above this, with one pixel of shift
	// tolerance in both directions. Same state: at most SameMaxPixels such pixels.
	private const float DiffThreshold = 0.35f;

	private const int SameMaxPixels = 2;

	// A local change (a badge): its box at most 11 x 11 of 16 and 45 % of the icon, at most 30 % of the pixels
	// changed, and touching an edge of the icon (badges sit in a corner or along an edge).
	private const int BadgeMaxSide = 11;

	private const int BadgeMaxBoxArea = 115;

	private const int BadgeMaxPixels = 77;

	private const int MaxClusters = 8;

	private const int MaxVariants = 4;

	private const int MaxVotes = 5;

	private const int MaxVoters = 8;

	private const int MaxRenderCache = 24;

	private const int MaxHashes = 256;

	private static readonly long Second = TimeSpan.TicksPerSecond;

	// Rapid alternation: at least BurstChanges changes within BurstWindow; it lasts until BurstQuiet without a change.
	private static readonly long BurstWindow = 4 * TimeSpan.TicksPerSecond;

	private const int BurstChanges = 3;

	internal static readonly long BurstQuiet = 2500 * TimeSpan.TicksPerMillisecond;

	// A frame of the app's own icon shown for less than this, then back to the picture, is treated as blinking.
	private static readonly long QuickReturn = 1500 * TimeSpan.TicksPerMillisecond;

	// An idle state chosen by display time only that was a frame of a whole-icon blinking state shows as the app's own
	// icon for this long after its last blink.
	internal static readonly long BurstMemory = 15 * TimeSpan.TicksPerMinute;

	// An app whose process started this recently when its icon first appeared votes for its start-up image.
	private static readonly long FreshStart = 120 * TimeSpan.TicksPerSecond;

	// The start-up vote goes to the first image kept for StableVote that appeared within VoteWindow of the first one.
	internal static readonly long StableVote = 20 * TimeSpan.TicksPerSecond;

	private static readonly long VoteWindow = 3 * TimeSpan.TicksPerMinute;

	// Votes for a state not shown for StaleVote, while at least two later starts voted for other states, are dropped.
	private static readonly long StaleVote = 12 * TimeSpan.TicksPerHour;

	// Display time: one episode counts at most 6 hours; totals fade with a 7 day time constant.
	private static readonly long EpisodeCap = 6 * TimeSpan.TicksPerHour;

	private const double DecayDays = 7.0;

	private static readonly long SaveStructural = 30 * TimeSpan.TicksPerSecond;

	private static readonly long SaveScores = 10 * TimeSpan.TicksPerMinute;

	private static readonly object Gate = new object();

	// Serialises writes of the store file (a background save and the save on exit never write at the same time).
	private static readonly object WriteGate = new object();

	private static readonly Dictionary<string, Model> Models = new Dictionary<string, Model>(StringComparer.Ordinal);

	private static bool _loaded;

	private static bool _exitHooked;

	private static Task<Dictionary<string, Model>>? _preload;

	// The store could not be read (locked, for example): nothing is written this session, so it is not overwritten.
	private static bool _readFailed;

	private static long _lastSave;

	private static bool _savePending;

	private static bool _structuralPending;

	private static long _saveSeq;

	private static long _writtenSeq;

	private static int _writesPending;

	// QA: the clock (UTC ticks), where the learned states are kept, and the identity of an icon's process when it has
	// just started (null when it has not).
	internal static Func<long>? QaClock { get; set; }

	internal static string? QaStorePath { get; set; }

	internal static Func<TrayAppIcon, string?>? QaFreshProcess { get; set; }

	// QA: write the store on the calling thread (deterministic), instead of off the UI thread like the shell does.
	internal static bool QaSyncWrites { get; set; }

	internal static string StorePath => QaStorePath ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Win81Layer", "seasonal-tray.json");

	private static bool CanPersist => QaStorePath != null || !SettingsStore.ReadOnlyDiagnostics;

	private static long Now => QaClock?.Invoke() ?? DateTime.UtcNow.Ticks;

	// ===================================================================== model

	// One image of a state at its native size.
	internal sealed class Print
	{
		public readonly int W;

		public readonly int H;

		// Premultiplied BGRA, W x H.
		public readonly byte[] Native;

		// Premultiplied BGRA in 0..1, S x S, placed like TrayIconFit places the image.
		public readonly float[] Small;

		public readonly ulong Hash;

		// Native, deflated and base64 encoded for the store (read from the store, or made by the first save).
		public string? Packed;

		public Print(int w, int h, byte[] native)
		{
			W = w;
			H = h;
			Native = native;
			Small = AreaFit(native, w, h, S);
			Hash = Fnv(native, w, h);
		}
	}

	private sealed class Cluster
	{
		public int Id;

		public readonly List<Print> Variants = new List<Print>();

		public double Score;

		public int Episodes;

		// When the state was last shown (an open episode counts as shown now).
		public long LastSeen;

		// When the state was last a frame of a blinking state that changes the whole icon; 0 = never.
		public long BurstAt;

		public Print Primary => Variants[0];

		public Print VariantFor(Print p)
		{
			Print best = Variants[0];
			foreach (Print v in Variants)
			{
				if (v.W == p.W && v.H == p.H)
				{
					return v;
				}
				if (Math.Abs(v.W - p.W) < Math.Abs(best.W - p.W))
				{
					best = v;
				}
			}
			return best;
		}
	}

	private sealed class Model
	{
		public readonly string Key;

		public readonly List<Cluster> Clusters = new List<Cluster>();

		public int IdleId;

		// The idle state was chosen by display time only (no start-up vote, no trusted first image).
		public bool IdleWeak;

		// Bumped whenever the idle state, its images or how it was chosen change; decisions and composites made for
		// another version are made again.
		public int Version;

		public int SeedId;

		public bool SeedTrusted;

		// Start-up votes (state, when cast), oldest first, and the processes that cast them.
		public readonly List<(int Id, long At)> Votes = new List<(int, long)>();

		public readonly List<string> Voters = new List<string>();

		public int NextId = 1;

		public long LastDecay;

		// The state shown now and since when (one episode per app, so a recreated icon continues it), how much of it
		// was already counted, and the icon showing it.
		public int OpenId;

		public long OpenSince;

		public long OpenCredited;

		public SeasonalTrayLive? OpenOwner;

		// Fingerprints of images seen, by pixel hash, so an image shown again is not fingerprinted again.
		public readonly Dictionary<ulong, Print> Prints = new Dictionary<ulong, Print>();

		// The state each image seen belongs to, by pixel hash, so an image shown again is not compared again.
		public readonly Dictionary<ulong, int> ByHash = new Dictionary<ulong, int>();

		// Whether state A is state B with a badge, per pair (the primary images never change).
		public readonly Dictionary<(int A, int B), bool> BadgeOf = new Dictionary<(int, int), bool>();

		public readonly Dictionary<(ulong Hash, int Version, int Px), (ImageSource Art, ImageSource Image)> Renders = new Dictionary<(ulong, int, int), (ImageSource, ImageSource)>();

		public Model(string key)
		{
			Key = key;
		}

		public Cluster? Find(int id)
		{
			if (id == 0)
			{
				return null;
			}
			foreach (Cluster c in Clusters)
			{
				if (c.Id == id)
				{
					return c;
				}
			}
			return null;
		}
	}

	// Classification of one image against the idle state.
	internal readonly record struct Verdict(SeasonalTrayLook Look, int X0, int Y0, int X1, int Y1, int Changed, bool[]? Mask);

	// ===================================================================== per icon

	// The image to show for a tray icon that has a seasonal picture (art, at px): the picture itself, the picture with
	// the app's badge, or null for the app's own icon. UI thread.
	internal static ImageSource? Show(TrayAppIcon vm, string key, ImageSource art, int px)
	{
		ImageSource? raw = vm.RawImage;
		if (raw == null)
		{
			return art;
		}
		if (SettingsStore.PeekSeasonalTrayOriginal())
		{
			// Always the app's own icon: nothing is learned, and what this icon was doing is closed.
			Drop(vm);
			return null;
		}
		try
		{
			lock (Gate)
			{
				long now = Now;
				Model m = ModelFor(key);
				SeasonalTrayLive? live = vm.SeasonalTray;
				if (live != null && live.Key != key)
				{
					CloseLocked(live, now);
					live = null;
				}
				bool observe = false;
				if (vm.RawSubstitute)
				{
					// Not the app's own tray image (the window's or program's icon while it could not be read, a letter tile,
					// a saved snapshot): it says nothing about the app's state, so nothing is learned. The decision for the
					// last real image stays, else the picture.
					vm.SeasonalTray = live;
					if (live?.Print == null || live.Observed == null)
					{
						return art;
					}
					raw = live.Observed;
				}
				else
				{
					if (live == null)
					{
						live = new SeasonalTrayLive { Key = key };
					}
					vm.SeasonalTray = live;
					observe = !ReferenceEquals(live.Observed, raw);
				}
				if (observe)
				{
					Observe(vm, live, m, raw, now);
				}
				if (live.BurstUntil != 0 && now >= live.BurstUntil)
				{
					Settle(live, m, now);
				}
				else if (live.RecheckAt != 0 && now >= live.RecheckAt)
				{
					Recheck(live, m, now);
				}
				else if (live.LookVersion != m.Version)
				{
					Decide(live, m, now, observed: false);
				}
				SaveIfDue(now);
				return Render(live, m, raw, art, px);
			}
		}
		catch (Exception ex)
		{
			Logger.Log("Seasonal tray: " + ex.Message);
			return art;
		}
	}

	// True when this icon should be drawn again although its image did not change: a blinking state has gone quiet, or
	// a start-up vote or an idle change has come due. Called by the regular tray scan; a field read when nothing is due.
	internal static bool NeedsSettle(TrayAppIcon vm)
	{
		SeasonalTrayLive? live = vm.SeasonalTray;
		if (live == null || (live.BurstUntil == 0 && live.RecheckAt == 0))
		{
			return false;
		}
		long now = Now;
		return (live.BurstUntil != 0 && now >= live.BurstUntil) || (live.RecheckAt != 0 && now >= live.RecheckAt);
	}

	// The icon no longer goes through the seasonal logic (it went away, its bar closed, the season or the setting
	// changed): its display time is counted up to now and its blinking and pending checks end with it.
	internal static void Drop(TrayAppIcon vm)
	{
		SeasonalTrayLive? live = vm.SeasonalTray;
		if (live == null)
		{
			return;
		}
		vm.SeasonalTray = null;
		try
		{
			lock (Gate)
			{
				CloseLocked(live, Now);
			}
		}
		catch (Exception ex)
		{
			Logger.Log("Seasonal tray: " + ex.Message);
		}
	}

	private static void CloseLocked(SeasonalTrayLive live, long now)
	{
		live.BurstUntil = 0;
		live.RecheckAt = 0;
		if (live.Key == null || !Models.TryGetValue(live.Key, out Model? m))
		{
			return;
		}
		if (ResolveVote(live, m, now, closing: true))
		{
			SelectIdle(m, now);
		}
		if (ReferenceEquals(m.OpenOwner, live))
		{
			CreditOpen(m, now);
			m.OpenId = 0;
			m.OpenOwner = null;
		}
	}

	private static void Observe(TrayAppIcon vm, SeasonalTrayLive live, Model m, ImageSource raw, long now)
	{
		bool first = live.Observed == null;
		live.Observed = raw;
		Print? print = PrintOf(m, raw);
		if (print == null)
		{
			if (ReferenceEquals(m.OpenOwner, live))
			{
				CreditOpen(m, now);
				m.OpenId = 0;
				m.OpenOwner = null;
			}
			live.Print = null;
			live.ClusterId = 0;
			live.Look = SeasonalTrayLook.Original;
			live.Verdict = default;
			live.LookVersion = m.Version;
			live.BurstUntil = 0;
			live.BurstGlobal = false;
			live.RecheckAt = 0;
			return;
		}
		if (!first && live.Print != null && (ReferenceEquals(print, live.Print) || (print.Hash == live.Print.Hash && print.W == live.Print.W && print.H == live.Print.H)))
		{
			// The same pixels sent again under a new handle: not a change.
			live.Print = print;
			return;
		}
		if (first)
		{
			StartVote(vm, live, m, now);
		}
		// While a blinking state lasts, frames never seen before (an animation) are judged against idle but not learned.
		bool bursting = live.BurstUntil != 0 && now < live.BurstUntil;
		Cluster? c = ClusterOf(m, print, now, allowNew: !bursting);
		int id = c?.Id ?? 0;
		if (!first && id != 0 && id == live.ClusterId)
		{
			// The same state again (another size, or a pixel or two different): not a change.
			live.Print = print;
			c!.LastSeen = now;
			Decide(live, m, now, observed: false);
			return;
		}
		Decay(m, now);
		bool voted = ResolveVote(live, m, now, closing: false);
		Switch(m, live, id, now);
		if (c != null)
		{
			c.Episodes++;
			c.LastSeen = now;
		}
		live.Print = print;
		live.ClusterId = id;
		live.Since = now;
		if (c != null && m.Find(m.SeedId) == null)
		{
			m.SeedId = c.Id;
			m.SeedTrusted = true;
			MarkDirty(structural: true);
		}
		live.Push(now, id);
		if (c != null || voted)
		{
			SelectIdle(m, now);
		}
		Decide(live, m, now, observed: true);
	}

	// Classifies the icon's current image against the idle state, applies the blinking rules and works out when the
	// icon has to be looked at again without a new image (RecheckAt).
	private static void Decide(SeasonalTrayLive live, Model m, long now, bool observed)
	{
		Print? p = live.Print;
		Cluster? idle = m.Find(m.IdleId);
		live.RecheckAt = 0;
		if (p == null || idle == null)
		{
			live.Look = SeasonalTrayLook.Original;
			live.Verdict = default;
			live.LookVersion = m.Version;
			return;
		}
		bool isIdle = live.ClusterId != 0 && live.ClusterId == idle.Id;
		Verdict v = isIdle ? new Verdict(SeasonalTrayLook.Seasonal, 0, 0, -1, -1, 0, null) : Classify(p, idle.VariantFor(p));
		live.SetLook(v.Look);
		if (observed)
		{
			bool burst = live.RecentChanges(now, BurstWindow) >= BurstChanges;
			// Back to the picture right after a short frame of the app's own: possibly the first blink of a flashing
			// icon, so the app's own icon stays until it has been quiet (no single frame of the picture in between).
			bool quick = v.Look != SeasonalTrayLook.Original && live.PreviousWas(SeasonalTrayLook.Original, now, QuickReturn);
			if (burst || quick)
			{
				if (live.BurstUntil == 0 || now > live.BurstUntil)
				{
					// A new blinking state: its frames so far count too.
					live.BurstGlobal = quick || live.RecentLooks(now, BurstWindow).Contains(SeasonalTrayLook.Original);
				}
				live.BurstUntil = now + BurstQuiet;
				live.BurstGlobal |= v.Look == SeasonalTrayLook.Original;
				if (live.BurstGlobal)
				{
					MarkBurst(live, m, now);
				}
				// The first image ever seen is no longer trusted as idle when it is one of the frames of a blinking state
				// that changes the whole icon (a call's speaking indicator).
				if (burst && live.BurstGlobal && m.SeedTrusted && live.RecentClusterCount(now, BurstWindow + BurstQuiet, m.SeedId) >= 2)
				{
					m.SeedTrusted = false;
					MarkDirty(structural: true);
					int before = m.Version;
					SelectIdle(m, now);
					if (before != m.Version)
					{
						Decide(live, m, now, observed: false);
						return;
					}
				}
			}
			else if (live.BurstUntil != 0 && now >= live.BurstUntil)
			{
				live.BurstUntil = 0;
				live.BurstGlobal = false;
			}
		}
		if (live.BurstUntil != 0 && live.BurstGlobal)
		{
			v = v with { Look = SeasonalTrayLook.Original };
		}
		else if (isIdle && m.IdleWeak && idle.BurstAt != 0 && now - idle.BurstAt < BurstMemory)
		{
			// Idle chosen by display time only, and a frame of a whole-icon blinking state a moment ago (the call image of
			// a call whose speaking indicator was seen, when the launcher first started during a call): more likely a
			// state than the plain icon, so the app's own icon is shown for a while after its last blink.
			v = v with { Look = SeasonalTrayLook.Original };
			Want(live, idle.BurstAt + BurstMemory, now);
		}
		live.Look = v.Look;
		live.Verdict = v;
		live.LookVersion = m.Version;
		if (live.VoteProc != null && live.ClusterId != 0)
		{
			// The start-up vote is cast once this image has been kept long enough.
			Want(live, live.Since + StableVote, now);
		}
		if (m.IdleWeak && !isIdle && live.ClusterId != 0 && live.ClusterId == m.OpenId && m.Find(live.ClusterId) is Cluster cur && MayTakeOver(m, idle, cur, now))
		{
			// Idle chosen by display time only: when this state has been shown long enough to take over, it is looked at
			// again (it becomes idle then, without waiting for the next image).
			double need = TakeOverScore(m, idle, cur, now) - EffScore(m, cur, now);
			if (need > 0.0 && need < EpisodeCap / (double)Second)
			{
				Want(live, now + (long)Math.Ceiling(need) * Second + Second, now);
			}
		}
	}

	private static void Want(SeasonalTrayLive live, long at, long now)
	{
		if (at > now && (live.RecheckAt == 0 || at < live.RecheckAt))
		{
			live.RecheckAt = at;
		}
	}

	private static void Settle(SeasonalTrayLive live, Model m, long now)
	{
		live.BurstUntil = 0;
		live.BurstGlobal = false;
		Recheck(live, m, now);
	}

	// A start-up vote or an idle change came due: cast the vote, choose idle again and decide again.
	private static void Recheck(SeasonalTrayLive live, Model m, long now)
	{
		live.RecheckAt = 0;
		Decay(m, now);
		ResolveVote(live, m, now, closing: false);
		SelectIdle(m, now);
		Decide(live, m, now, observed: false);
	}

	// The states of the frames of the current whole-icon blinking state.
	private static void MarkBurst(SeasonalTrayLive live, Model m, long now)
	{
		foreach (int id in live.RecentStates(now, BurstWindow + BurstQuiet))
		{
			if (m.Find(id) is Cluster c && c.BurstAt != now)
			{
				c.BurstAt = now;
				MarkDirty(structural: false);
			}
		}
	}

	// The process that owns the icon, when it started less than FreshStart ago ("pid:creation time"), else null.
	private static string? FreshProcess(TrayAppIcon vm)
	{
		Func<TrayAppIcon, string?>? qa = QaFreshProcess;
		if (qa != null)
		{
			return qa(vm);
		}
		if (vm.OwnerHwnd == IntPtr.Zero)
		{
			return null;
		}
		try
		{
			GetWindowThreadProcessId(vm.OwnerHwnd, out uint pid);
			if (pid == 0)
			{
				return null;
			}
			nint h = OpenProcess(0x1000u, false, pid);
			if (h == IntPtr.Zero)
			{
				return null;
			}
			try
			{
				if (!GetProcessTimes(h, out long created, out _, out _, out _))
				{
					return null;
				}
				long age = DateTime.UtcNow.Ticks - DateTime.FromFileTimeUtc(created).Ticks;
				return age >= 0 && age < FreshStart ? pid.ToString(System.Globalization.CultureInfo.InvariantCulture) + ":" + created.ToString(System.Globalization.CultureInfo.InvariantCulture) : null;
			}
			finally
			{
				CloseHandle(h);
			}
		}
		catch
		{
			return null;
		}
	}

	// ===================================================================== learning

	private static Model ModelFor(string key)
	{
		EnsureLoaded();
		if (!Models.TryGetValue(key, out Model? m))
		{
			m = new Model(key) { LastDecay = Now };
			Models[key] = m;
		}
		return m;
	}

	// The icon's process just started and has not voted yet: its start-up image will get its vote.
	private static void StartVote(TrayAppIcon vm, SeasonalTrayLive live, Model m, long now)
	{
		string? proc = FreshProcess(vm);
		if (proc == null || m.Voters.Contains(proc))
		{
			return;
		}
		live.VoteProc = proc;
		live.VoteFrom = now;
	}

	// Casts the pending start-up vote for the image shown now once it has been kept for StableVote (and appeared within
	// VoteWindow of the first image). True when a vote was cast.
	private static bool ResolveVote(SeasonalTrayLive live, Model m, long now, bool closing)
	{
		string? proc = live.VoteProc;
		if (proc == null)
		{
			return false;
		}
		int id = live.ClusterId;
		bool late = live.Since - live.VoteFrom > VoteWindow;
		if (id == 0 || late || m.Find(id) == null || now - live.Since < StableVote)
		{
			if (closing || late || now - live.VoteFrom > VoteWindow + StableVote)
			{
				live.VoteProc = null;
			}
			return false;
		}
		live.VoteProc = null;
		if (m.Voters.Contains(proc))
		{
			return false;
		}
		m.Voters.Add(proc);
		if (m.Voters.Count > MaxVoters)
		{
			m.Voters.RemoveAt(0);
		}
		m.Votes.Add((id, now));
		if (m.Votes.Count > MaxVotes)
		{
			m.Votes.RemoveAt(0);
		}
		MarkDirty(structural: true);
		return true;
	}

	// The state shown by the app changes to id (0 = a frame that is not learned): the time of the state shown until now
	// is counted, unless it is the same state (a recreated icon continues the episode).
	private static void Switch(Model m, SeasonalTrayLive live, int id, long now)
	{
		if (id != 0 && m.OpenId == id)
		{
			m.OpenOwner = live;
			return;
		}
		CreditOpen(m, now);
		m.OpenId = id;
		m.OpenSince = now;
		m.OpenCredited = 0;
		m.OpenOwner = id != 0 ? live : null;
	}

	// Counts the open episode's time so far (at most EpisodeCap for the whole episode); the episode stays open.
	private static void CreditOpen(Model m, long now)
	{
		if (m.OpenId == 0)
		{
			return;
		}
		long total = Math.Min(Math.Max(0, now - m.OpenSince), EpisodeCap);
		long add = total - m.OpenCredited;
		if (add > 0 && m.Find(m.OpenId) is Cluster c)
		{
			c.Score += add / (double)Second;
			c.LastSeen = Math.Max(c.LastSeen, now);
			MarkDirty(structural: false);
		}
		m.OpenCredited = Math.Max(m.OpenCredited, total);
	}

	// Display time in seconds, with the part of the open episode not counted yet.
	private static double EffScore(Model m, Cluster c, long now)
	{
		double s = c.Score;
		if (c.Id == m.OpenId)
		{
			long total = Math.Min(Math.Max(0, now - m.OpenSince), EpisodeCap);
			s += Math.Max(0, total - m.OpenCredited) / (double)Second;
		}
		return s;
	}

	// The score a state needs to take over from the idle state cur by display time: a frame of a whole-icon blinking
	// state needs only more time than cur, anything else 25 % and 10 minutes more.
	private static double TakeOverScore(Model m, Cluster cur, Cluster challenger, long now)
	{
		double s = EffScore(m, cur, now);
		return cur.BurstAt != 0 ? s : 1.25 * s + 600.0;
	}

	private static Print? PrintOf(Model m, ImageSource raw)
	{
		if (raw is not BitmapSource bs || bs.PixelWidth <= 0 || bs.PixelHeight <= 0)
		{
			return null;
		}
		BitmapSource src = bs;
		if (src.PixelWidth > MaxNative || src.PixelHeight > MaxNative)
		{
			src = TrayIconFit.Fit(src, MaxNative) as BitmapSource ?? src;
		}
		int w = src.PixelWidth;
		int h = src.PixelHeight;
		byte[] px = Pixels(src);
		ulong hash = Fnv(px, w, h);
		if (m.Prints.TryGetValue(hash, out Print? hit) && hit.W == w && hit.H == h && hit.Native.AsSpan().SequenceEqual(px))
		{
			return hit;
		}
		Print p = new Print(w, h, px);
		if (m.Prints.Count >= 64)
		{
			m.Prints.Clear();
		}
		m.Prints[hash] = p;
		return p;
	}

	// The state of an image: by its pixel hash when it was seen before, else by comparison; a new state when nothing
	// matches (unless allowNew is false: then null).
	private static Cluster? ClusterOf(Model m, Print p, long now, bool allowNew)
	{
		if (m.ByHash.TryGetValue(p.Hash, out int known) && m.Find(known) is Cluster hit)
		{
			return hit;
		}
		Cluster? found = null;
		bool exactSize = false;
		foreach (Cluster c in m.Clusters)
		{
			bool match = false;
			bool size = false;
			foreach (Print v in c.Variants)
			{
				if (Same(p, v))
				{
					match = true;
					size |= v.W == p.W && v.H == p.H;
				}
			}
			if (match && (found == null || (size && !exactSize)))
			{
				found = c;
				exactSize = size;
			}
		}
		if (found != null)
		{
			if (!exactSize && found.Variants.Count < MaxVariants)
			{
				// Another size of a known state (a DPI change): kept, so the badge mask can be made at this size.
				found.Variants.Add(p);
				m.BadgeOf.Clear();
				if (found.Id == m.IdleId)
				{
					m.Version++;
				}
				MarkDirty(structural: false);
			}
			Remember(m, p.Hash, found.Id);
			return found;
		}
		if (!allowNew)
		{
			return null;
		}
		Cluster nc = new Cluster { Id = m.NextId++, LastSeen = now };
		nc.Variants.Add(p);
		m.Clusters.Add(nc);
		Remember(m, p.Hash, nc.Id);
		MarkDirty(structural: false);
		if (m.Clusters.Count > MaxClusters)
		{
			Cluster? drop = m.Clusters
				.Where(c => c != nc && c.Id != m.IdleId && c.Id != m.SeedId && c.Id != m.OpenId && !m.Votes.Any(v => v.Id == c.Id))
				.OrderBy(c => c.Score)
				.ThenBy(c => c.LastSeen)
				.FirstOrDefault();
			if (drop != null)
			{
				m.Clusters.Remove(drop);
				m.BadgeOf.Clear();
				foreach (ulong h in m.ByHash.Where(kv => kv.Value == drop.Id).Select(kv => kv.Key).ToList())
				{
					m.ByHash.Remove(h);
				}
			}
		}
		return nc;
	}

	private static void Remember(Model m, ulong hash, int id)
	{
		if (m.ByHash.Count >= MaxHashes)
		{
			m.ByHash.Clear();
		}
		m.ByHash[hash] = id;
	}

	private static void Decay(Model m, long now)
	{
		long dt = now - m.LastDecay;
		if (dt <= 0)
		{
			return;
		}
		double k = Math.Exp(-dt / (DecayDays * TimeSpan.TicksPerDay));
		foreach (Cluster c in m.Clusters)
		{
			c.Score *= k;
		}
		m.LastDecay = now;
	}

	// Votes for a state that is gone, or that has not been shown for StaleVote while at least two later starts voted
	// for other states (the app changed its icon), are dropped.
	private static void PruneVotes(Model m, long now)
	{
		foreach (int id in m.Votes.Select(v => v.Id).Distinct().ToList())
		{
			Cluster? c = m.Find(id);
			bool stale = c == null;
			if (c != null)
			{
				long shown = c.Id == m.OpenId ? now : c.LastSeen;
				stale = now - shown > StaleVote && m.Votes.Count(v => v.Id != id && v.At > shown) >= 2;
			}
			if (stale)
			{
				m.Votes.RemoveAll(v => v.Id == id);
				MarkDirty(structural: true);
			}
		}
	}

	// Chooses the idle state (see the class comment).
	private static void SelectIdle(Model m, long now)
	{
		PruneVotes(m, now);
		Cluster? pick = null;
		bool weak = false;
		if (m.Votes.Count > 0)
		{
			int bestCount = 0;
			for (int i = m.Votes.Count - 1; i >= 0; i--)
			{
				int id = m.Votes[i].Id;
				Cluster? c = m.Find(id);
				if (c == null)
				{
					continue;
				}
				int count = m.Votes.Count(v => v.Id == id);
				if (count > bestCount)
				{
					bestCount = count;
					pick = c;
				}
			}
		}
		if (pick == null && m.SeedTrusted && m.Find(m.SeedId) is Cluster seed)
		{
			pick = seed;
			// Unless another state has been shown far longer (the app changed its icon, for example).
			Cluster? best = Best(m, now, null);
			if (best != null && best != seed && EffScore(m, best, now) > 3.0 * EffScore(m, seed, now) + 8.0 * 3600.0)
			{
				pick = best;
				m.SeedTrusted = false;
				MarkDirty(structural: true);
			}
		}
		if (pick == null)
		{
			weak = true;
			Cluster? cur = m.Find(m.IdleId);
			// A frame of a whole-icon blinking state (a call, a speaking indicator) does not take over from a plain
			// state by display time.
			Cluster? best = Best(m, now, cur != null ? (Cluster c) => MayTakeOver(m, cur, c, now) : null);
			pick = (cur != null && best != null && best != cur && EffScore(m, best, now) > TakeOverScore(m, cur, best, now)) ? best : cur ?? best;
		}
		if (pick == null)
		{
			return;
		}
		// A plain icon wins over the same icon with a badge.
		for (int round = 0; round < 3; round++)
		{
			Cluster? plain = null;
			foreach (Cluster y in m.Clusters)
			{
				if (y != pick && BadgeOf(m, pick, y) && (plain == null || y.Score > plain.Score))
				{
					plain = y;
				}
			}
			if (plain == null)
			{
				break;
			}
			pick = plain;
		}
		if (pick.Id != m.IdleId || weak != m.IdleWeak)
		{
			if (pick.Id != m.IdleId)
			{
				MarkDirty(structural: true);
			}
			m.IdleId = pick.Id;
			m.IdleWeak = weak;
			m.Version++;
		}
	}

	// A frame of a whole-icon blinking state does not take over from a plain idle state by display time.
	private static bool MayTakeOver(Model m, Cluster cur, Cluster challenger, long now)
	{
		return challenger.BurstAt == 0 || cur.BurstAt != 0 || EffScore(m, cur, now) < 60.0;
	}

	private static Cluster? Best(Model m, long now, Func<Cluster, bool>? eligible)
	{
		Cluster? best = null;
		double bestScore = double.MinValue;
		foreach (Cluster c in m.Clusters)
		{
			if (eligible != null && !eligible(c))
			{
				continue;
			}
			double s = EffScore(m, c, now);
			if (s > bestScore)
			{
				best = c;
				bestScore = s;
			}
		}
		return best;
	}

	// True when state a is state b with a badge (cached per pair).
	private static bool BadgeOf(Model m, Cluster a, Cluster b)
	{
		if (m.BadgeOf.TryGetValue((a.Id, b.Id), out bool hit))
		{
			return hit;
		}
		Print pa = a.Primary;
		Print pb = b.VariantFor(pa);
		Verdict v = Classify(pa, pb);
		bool r = v.Look == SeasonalTrayLook.SeasonalBadge && BadgeOver(pa, pb, v);
		m.BadgeOf[(a.Id, b.Id)] = r;
		return r;
	}

	// ===================================================================== comparison

	// The visible difference of two premultiplied pixels: the larger of the differences over black and over white.
	private static float Vis(float[] a, int i, float[] b, int j)
	{
		float da = a[i + 3] - b[j + 3];
		float m = 0f;
		for (int c = 0; c < 3; c++)
		{
			float dc = a[i + c] - b[j + c];
			float x = Math.Max(Math.Abs(dc), Math.Abs(dc - da));
			if (x > m)
			{
				m = x;
			}
		}
		return m;
	}

	// Per pixel, the difference that remains when either image may move by one pixel (both directions).
	private static float[] TolerantDiff(float[] a, float[] b)
	{
		float[] d = new float[S * S];
		for (int y = 0; y < S; y++)
		{
			for (int x = 0; x < S; x++)
			{
				int i = (y * S + x) * 4;
				float ab = float.MaxValue;
				float ba = float.MaxValue;
				for (int dy = -1; dy <= 1; dy++)
				{
					int yy = Math.Clamp(y + dy, 0, S - 1);
					for (int dx = -1; dx <= 1; dx++)
					{
						int j = (yy * S + Math.Clamp(x + dx, 0, S - 1)) * 4;
						ab = Math.Min(ab, Vis(a, i, b, j));
						ba = Math.Min(ba, Vis(b, i, a, j));
					}
				}
				d[y * S + x] = Math.Max(ab, ba);
			}
		}
		return d;
	}

	// The changed pixels (isolated ones dropped) and their count.
	private static bool[] ChangedMask(Print a, Print b, out int count)
	{
		float[] d = TolerantDiff(a.Small, b.Small);
		bool[] raw = new bool[S * S];
		for (int i = 0; i < raw.Length; i++)
		{
			raw[i] = d[i] > DiffThreshold;
		}
		bool[] m = new bool[S * S];
		count = 0;
		for (int y = 0; y < S; y++)
		{
			for (int x = 0; x < S; x++)
			{
				if (!raw[y * S + x])
				{
					continue;
				}
				bool neighbour = false;
				for (int dy = -1; dy <= 1 && !neighbour; dy++)
				{
					for (int dx = -1; dx <= 1; dx++)
					{
						int yy = y + dy;
						int xx = x + dx;
						if ((dx != 0 || dy != 0) && yy >= 0 && yy < S && xx >= 0 && xx < S && raw[yy * S + xx])
						{
							neighbour = true;
							break;
						}
					}
				}
				if (neighbour)
				{
					m[y * S + x] = true;
					count++;
				}
			}
		}
		return m;
	}

	internal static bool Same(Print a, Print b)
	{
		if (a.Hash == b.Hash && a.W == b.W && a.H == b.H)
		{
			return true;
		}
		ChangedMask(a, b, out int count);
		return count <= SameMaxPixels;
	}

	// Seasonal (same as idle), SeasonalBadge (a compact change at an edge) or Original (anything larger).
	internal static Verdict Classify(Print cur, Print idle)
	{
		bool[] m = ChangedMask(cur, idle, out int count);
		if (count <= SameMaxPixels)
		{
			return new Verdict(SeasonalTrayLook.Seasonal, 0, 0, -1, -1, count, null);
		}
		int x0 = S;
		int y0 = S;
		int x1 = -1;
		int y1 = -1;
		for (int y = 0; y < S; y++)
		{
			for (int x = 0; x < S; x++)
			{
				if (m[y * S + x])
				{
					x0 = Math.Min(x0, x);
					y0 = Math.Min(y0, y);
					x1 = Math.Max(x1, x);
					y1 = Math.Max(y1, y);
				}
			}
		}
		int bw = x1 - x0 + 1;
		int bh = y1 - y0 + 1;
		bool edge = x0 <= 1 || y0 <= 1 || x1 >= S - 2 || y1 >= S - 2;
		bool local = edge && bw <= BadgeMaxSide && bh <= BadgeMaxSide && bw * bh <= BadgeMaxBoxArea && count <= BadgeMaxPixels;
		return new Verdict(local ? SeasonalTrayLook.SeasonalBadge : SeasonalTrayLook.Original, x0, y0, x1, y1, count, m);
	}

	// True when a carries the badge over b (a = b + badge). Badges stick out past the icon's outline into the corner,
	// while the plain icon has nothing that the badged one lacks except a thin cut ring around the badge: so inside the
	// changed box, a has more pixels where b has nothing within one pixel. When that does not decide, the more
	// colourful of the two (badges are saturated) carries it.
	internal static bool BadgeOver(Print a, Print b, Verdict v)
	{
		int x0 = Math.Max(0, v.X0 - 1);
		int y0 = Math.Max(0, v.Y0 - 1);
		int x1 = Math.Min(S - 1, v.X1 + 1);
		int y1 = Math.Min(S - 1, v.Y1 + 1);
		int extA = 0;
		int extB = 0;
		double alphaA = 0;
		double alphaB = 0;
		double satA = 0;
		double satB = 0;
		for (int y = y0; y <= y1; y++)
		{
			for (int x = x0; x <= x1; x++)
			{
				int i = (y * S + x) * 4;
				if (a.Small[i + 3] > 0.5f && NearAlpha(b.Small, x, y) < 0.25f)
				{
					extA++;
				}
				if (b.Small[i + 3] > 0.5f && NearAlpha(a.Small, x, y) < 0.25f)
				{
					extB++;
				}
				if (v.Mask != null && v.Mask[y * S + x])
				{
					alphaA += a.Small[i + 3];
					alphaB += b.Small[i + 3];
					satA += Saturation(a.Small, i) * a.Small[i + 3];
					satB += Saturation(b.Small, i) * b.Small[i + 3];
				}
			}
		}
		if (extA - extB >= 2)
		{
			return true;
		}
		if (extB - extA >= 2)
		{
			return false;
		}
		double sa = alphaA > 0 ? satA / alphaA : 0;
		double sb = alphaB > 0 ? satB / alphaB : 0;
		return sa - sb > 0.15;
	}

	// The largest alpha within one pixel.
	private static float NearAlpha(float[] p, int x, int y)
	{
		float m = 0f;
		for (int dy = -1; dy <= 1; dy++)
		{
			int yy = y + dy;
			if (yy < 0 || yy >= S)
			{
				continue;
			}
			for (int dx = -1; dx <= 1; dx++)
			{
				int xx = x + dx;
				if (xx >= 0 && xx < S)
				{
					m = Math.Max(m, p[(yy * S + xx) * 4 + 3]);
				}
			}
		}
		return m;
	}

	private static double Saturation(float[] p, int i)
	{
		float a = p[i + 3];
		if (a <= 0.02f)
		{
			return 0;
		}
		float b = p[i] / a;
		float g = p[i + 1] / a;
		float r = p[i + 2] / a;
		float max = Math.Max(r, Math.Max(g, b));
		float min = Math.Min(r, Math.Min(g, b));
		return max <= 0.0001f ? 0 : (max - min) / max;
	}

	// ===================================================================== rendering

	private static ImageSource? Render(SeasonalTrayLive live, Model m, ImageSource raw, ImageSource art, int px)
	{
		switch (live.Look)
		{
			case SeasonalTrayLook.Seasonal:
				return art;
			case SeasonalTrayLook.SeasonalBadge:
				break;
			default:
				return null;
		}
		Print? p = live.Print;
		Cluster? idle = m.Find(m.IdleId);
		if (p == null || idle == null)
		{
			return null;
		}
		(ulong, int, int) cacheKey = (p.Hash, m.Version, px);
		if (m.Renders.TryGetValue(cacheKey, out (ImageSource Art, ImageSource Image) hit) && ReferenceEquals(hit.Art, art))
		{
			return hit.Image;
		}
		ImageSource? made = ComposeBadge(raw, idle.VariantFor(p), live.Verdict, art, px);
		if (made == null)
		{
			return null;
		}
		if (m.Renders.Count >= MaxRenderCache)
		{
			m.Renders.Clear();
		}
		m.Renders[cacheKey] = (art, made);
		return made;
	}

	// The seasonal picture with the badge of cur (what is new compared with idle inside the verdict's box) drawn over
	// it at the same place, ringed by a thin halo that contrasts with the badge. All premultiplied, px x px.
	internal static ImageSource? ComposeBadge(ImageSource raw, Print idle, Verdict v, ImageSource art, int px)
	{
		if (art is not BitmapSource artBmp || artBmp.PixelWidth != px || artBmp.PixelHeight != px || v.Mask == null)
		{
			return null;
		}
		byte[] a = Pixels(artBmp);
		byte[] cb = Pixels(TrayIconFit.Fit(raw, px) as BitmapSource ?? artBmp);
		if (cb.Length != a.Length)
		{
			return null;
		}
		float[] c = ToFloat(cb);
		float[] id = AreaFit(idle.Native, idle.W, idle.H, px);
		float[] w = BadgeWeights(c, id, v, px, out bool[] enclosed, out bool[] outside);
		int n = px * px;
		// The badge layer (premultiplied) and how much it covers each pixel.
		float[] layer = new float[n * 4];
		float[] cover = new float[n];
		for (int i = 0; i < n; i++)
		{
			for (int ch = 0; ch < 4; ch++)
			{
				layer[i * 4 + ch] = c[i * 4 + ch] * w[i];
			}
			cover[i] = layer[i * 4 + 3];
		}
		bool solid = BadgeColour(c, id, w, enclosed, n, out float br, out float bg, out float bb);
		if (solid)
		{
			Unmix(c, id, w, enclosed, outside, px, br, bg, bb, layer, cover);
		}
		// The halo is dark behind a light badge and light behind a dark one, whichever contrasts more.
		double lum = 0.2126 * Lin(br) + 0.7152 * Lin(bg) + 0.0722 * Lin(bb);
		float halo = lum > 0.179 ? 0f : 1f;
		int r = Math.Max(1, (int)Math.Round(px / 20.0));
		byte[] o = new byte[n * 4];
		for (int y = 0; y < px; y++)
		{
			for (int x = 0; x < px; x++)
			{
				int i = y * px + x;
				// The ring: distance to the nearest badge pixel (at least 30 % covered), full strength up to r
				// pixels away and fading over the next one, so it is round and crisp however soft the badge edge is.
				float ring = 0f;
				for (int dy = -r - 1; dy <= r + 1; dy++)
				{
					int yy = y + dy;
					if (yy < 0 || yy >= px)
					{
						continue;
					}
					for (int dx = -r - 1; dx <= r + 1; dx++)
					{
						int xx = x + dx;
						if (xx >= 0 && xx < px && cover[yy * px + xx] >= 0.3f)
						{
							ring = Math.Max(ring, Math.Clamp(r + 1f - MathF.Sqrt(dx * dx + dy * dy), 0f, 1f));
						}
					}
				}
				// Picture, then the halo over it, then the badge over both (premultiplied "over").
				float artA = a[i * 4 + 3] / 255f;
				float haloA = ring * 0.9f * artA;
				float badgeA = cover[i];
				for (int ch = 0; ch < 4; ch++)
				{
					float haloC = ch == 3 ? haloA : halo * haloA;
					float under = haloC + a[i * 4 + ch] / 255f * (1f - haloA);
					o[i * 4 + ch] = (byte)Math.Clamp((int)Math.Round((layer[i * 4 + ch] + under * (1f - badgeA)) * 255f), 0, 255);
				}
				// Premultiplied: colour never exceeds alpha.
				byte oa = o[i * 4 + 3];
				o[i * 4] = Math.Min(o[i * 4], oa);
				o[i * 4 + 1] = Math.Min(o[i * 4 + 1], oa);
				o[i * 4 + 2] = Math.Min(o[i * 4 + 2], oa);
			}
		}
		BitmapSource result = BitmapSource.Create(px, px, 96.0, 96.0, PixelFormats.Pbgra32, null, o, px * 4);
		result.Freeze();
		return result;
	}

	// The badge's own colour (straight, 0..1): the average of the pixels that are mostly badge, digits and other
	// enclosed pixels left out, then again over those that are not a blend with the app's logo. False when no pixel is
	// mostly badge (then the average of everything copied, for the halo only).
	private static bool BadgeColour(float[] c, float[] id, float[] w, bool[] enclosed, int n, out float br, out float bg, out float bb)
	{
		double sr = 0, sg = 0, sb = 0, sw = 0;
		double ar = 0, ag = 0, ab = 0, aw = 0;
		for (int i = 0; i < n; i++)
		{
			float ca = c[i * 4 + 3];
			if (w[i] * ca <= 0.5f || ca <= 0f)
			{
				continue;
			}
			ab += c[i * 4] / ca;
			ag += c[i * 4 + 1] / ca;
			ar += c[i * 4 + 2] / ca;
			aw += 1;
			if (!enclosed[i])
			{
				sb += c[i * 4] / ca;
				sg += c[i * 4 + 1] / ca;
				sr += c[i * 4 + 2] / ca;
				sw += 1;
			}
		}
		if (sw <= 0)
		{
			br = aw > 0 ? (float)(ar / aw) : 1f;
			bg = aw > 0 ? (float)(ag / aw) : 1f;
			bb = aw > 0 ? (float)(ab / aw) : 1f;
			return false;
		}
		float r0 = (float)(sr / sw);
		float g0 = (float)(sg / sw);
		float b0 = (float)(sb / sw);
		double tr = 0, tg = 0, tb = 0, tw = 0;
		for (int i = 0; i < n; i++)
		{
			float ca = c[i * 4 + 3];
			if (w[i] * ca <= 0.5f || ca <= 0f || enclosed[i])
			{
				continue;
			}
			float ia = id[i * 4 + 3];
			if (ia >= 0.3f && BadgeShare(c, id, i, r0, g0, b0) < 0.85f)
			{
				continue;
			}
			tb += c[i * 4] / ca;
			tg += c[i * 4 + 1] / ca;
			tr += c[i * 4 + 2] / ca;
			tw += 1;
		}
		br = tw > 0 ? (float)(tr / tw) : r0;
		bg = tw > 0 ? (float)(tg / tw) : g0;
		bb = tw > 0 ? (float)(tb / tw) : b0;
		return true;
	}

	// How much of pixel i (straight colour) is the badge colour B rather than idle's colour there: the position of the
	// pixel's colour along the line from idle's colour to B (0..1); 1 when the two colours are too close to tell apart.
	private static float BadgeShare(float[] c, float[] id, int i, float br, float bg, float bb)
	{
		float ca = c[i * 4 + 3];
		float ia = id[i * 4 + 3];
		if (ca <= 0.02f || ia <= 0.02f)
		{
			return 1f;
		}
		float ib = id[i * 4] / ia;
		float ig = id[i * 4 + 1] / ia;
		float ir = id[i * 4 + 2] / ia;
		float db = bb - ib;
		float dg = bg - ig;
		float dr = br - ir;
		float den = db * db + dg * dg + dr * dr;
		if (den < 0.02f)
		{
			return 1f;
		}
		float t = ((c[i * 4] / ca - ib) * db + (c[i * 4 + 1] / ca - ig) * dg + (c[i * 4 + 2] / ca - ir) * dr) / den;
		return Math.Clamp(t, 0f, 1f);
	}

	// Where the badge's outer edge blends into the app's logo (the badge drawn straight into the logo, or a resampled
	// edge that mixes both), the copied pixel is split into the logo's colour and the badge colour, and only the badge
	// part is kept, as the badge colour with the matching coverage: no pale or grey rim from the logo, and the halo
	// follows the badge's real edge. Only the outer edge is split (pixels next to what is not badge, outside); what lies
	// inside the badge, such as the digits of a count and their soft edges, is kept as it is.
	private static void Unmix(float[] c, float[] id, float[] w, bool[] enclosed, bool[] outside, int px, float br, float bg, float bb, float[] layer, float[] cover)
	{
		for (int y = 0; y < px; y++)
		{
			for (int x = 0; x < px; x++)
			{
				int i = y * px + x;
				if (w[i] <= 0f || enclosed[i] || id[i * 4 + 3] < 0.3f || c[i * 4 + 3] <= 0.02f || !Near(outside, px, x, y) || Near(enclosed, px, x, y))
				{
					continue;
				}
				float t = BadgeShare(c, id, i, br, bg, bb);
				if (t >= 0.98f)
				{
					continue;
				}
				float a = c[i * 4 + 3] * t * w[i];
				layer[i * 4] = bb * a;
				layer[i * 4 + 1] = bg * a;
				layer[i * 4 + 2] = br * a;
				layer[i * 4 + 3] = a;
				cover[i] = a;
			}
		}
	}

	// True when the pixel or one of its eight neighbours is set in mask.
	private static bool Near(bool[] mask, int px, int x, int y)
	{
		for (int dy = -1; dy <= 1; dy++)
		{
			int yy = y + dy;
			if (yy < 0 || yy >= px)
			{
				continue;
			}
			for (int dx = -1; dx <= 1; dx++)
			{
				int xx = x + dx;
				if (xx >= 0 && xx < px && mask[yy * px + xx])
				{
					return true;
				}
			}
		}
		return false;
	}

	// How much of the fitted current image to draw at each pixel of the px x px box: what is new compared with idle
	// fitted the same way, inside the verdict's box (one pixel of margin), with enclosed pixels (the white digits of a
	// count over a white logo) filled and marked, and what lies outside the badge (reached from the box's border without
	// crossing it, or outside the box) marked. Made at the size it is drawn, so it never reaches into the app's logo.
	private static float[] BadgeWeights(float[] c, float[] i, Verdict v, int px, out bool[] enclosed, out bool[] outside)
	{
		double k = px / (double)S;
		int x0 = Math.Clamp((int)Math.Floor((v.X0 - 1) * k), 0, px - 1);
		int y0 = Math.Clamp((int)Math.Floor((v.Y0 - 1) * k), 0, px - 1);
		int x1 = Math.Clamp((int)Math.Ceiling((v.X1 + 2) * k) - 1, 0, px - 1);
		int y1 = Math.Clamp((int)Math.Ceiling((v.Y1 + 2) * k) - 1, 0, px - 1);
		float[] w = new float[px * px];
		for (int y = y0; y <= y1; y++)
		{
			for (int x = x0; x <= x1; x++)
			{
				int j = y * px + x;
				w[j] = BadgeAmount(c, i, j * 4);
			}
		}
		enclosed = new bool[px * px];
		outside = new bool[px * px];
		for (int y = 0; y < px; y++)
		{
			for (int x = 0; x < px; x++)
			{
				outside[y * px + x] = x < x0 || x > x1 || y < y0 || y > y1;
			}
		}
		FillEnclosed(w, enclosed, outside, px, px, x0, y0, x1, y1);
		return w;
	}

	// How much of a pixel of the current image is badge: nothing where it matches idle, nothing where it is idle's own
	// colour with more or less of it (the logo cut away around a badge, which the halo stands in for, or a logo edge
	// drawn at another size), up to all of it where it shows something new.
	private static float BadgeAmount(float[] c, float[] i, int k)
	{
		float amount = SmoothStep(0.06f, 0.30f, Vis(c, k, i, k));
		if (amount <= 0f)
		{
			return 0f;
		}
		float ca = c[k + 3];
		float ia = i[k + 3];
		if (ca < 0.05f)
		{
			return 0f;
		}
		if (ia >= 0.05f)
		{
			float d = 0f;
			for (int ch = 0; ch < 3; ch++)
			{
				d = Math.Max(d, Math.Abs(c[k + ch] / ca - i[k + ch] / ia));
			}
			if (d < 0.15f)
			{
				return 0f;
			}
		}
		return amount;
	}

	// Pixels inside the box that are not reached from the box's border without crossing a weight of 0.5 or more are
	// enclosed by the badge: they are copied too, and the ones the badge did not cover itself are marked. The pixels
	// reached are marked in outsideMask.
	private static void FillEnclosed(float[] w, bool[] enclosed, bool[] outsideMask, int width, int height, int x0, int y0, int x1, int y1)
	{
		int bw = x1 - x0 + 1;
		int bh = y1 - y0 + 1;
		if (bw <= 2 || bh <= 2)
		{
			for (int y = y0; y <= y1; y++)
			{
				for (int x = x0; x <= x1; x++)
				{
					outsideMask[y * width + x] = w[y * width + x] < 0.5f;
				}
			}
			return;
		}
		bool[] outside = new bool[bw * bh];
		Stack<(int X, int Y)> stack = new Stack<(int, int)>();
		for (int y = y0; y <= y1; y++)
		{
			for (int x = x0; x <= x1; x++)
			{
				bool border = x == x0 || x == x1 || y == y0 || y == y1;
				if (border && w[y * width + x] < 0.5f)
				{
					outside[(y - y0) * bw + (x - x0)] = true;
					stack.Push((x, y));
				}
			}
		}
		while (stack.Count > 0)
		{
			(int x, int y) = stack.Pop();
			foreach ((int nx, int ny) in new (int, int)[4] { (x + 1, y), (x - 1, y), (x, y + 1), (x, y - 1) })
			{
				if (nx < x0 || nx > x1 || ny < y0 || ny > y1)
				{
					continue;
				}
				int j = (ny - y0) * bw + (nx - x0);
				if (!outside[j] && w[ny * width + nx] < 0.5f)
				{
					outside[j] = true;
					stack.Push((nx, ny));
				}
			}
		}
		for (int y = y0; y <= y1; y++)
		{
			for (int x = x0; x <= x1; x++)
			{
				int j = y * width + x;
				outsideMask[j] = outside[(y - y0) * bw + (x - x0)];
				if (!outside[(y - y0) * bw + (x - x0)])
				{
					if (w[j] < 0.5f)
					{
						enclosed[j] = true;
					}
					w[j] = Math.Max(w[j], 1f);
				}
			}
		}
	}

	private static float SmoothStep(float lo, float hi, float x)
	{
		float t = Math.Clamp((x - lo) / (hi - lo), 0f, 1f);
		return t * t * (3f - 2f * t);
	}

	private static double Lin(double s)
	{
		s = Math.Clamp(s, 0.0, 1.0);
		return s <= 0.04045 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
	}

	// ===================================================================== pixels

	internal static byte[] Pixels(BitmapSource src)
	{
		BitmapSource p = src.Format == PixelFormats.Pbgra32 ? src : new FormatConvertedBitmap(src, PixelFormats.Pbgra32, null, 0.0);
		byte[] px = new byte[p.PixelWidth * p.PixelHeight * 4];
		p.CopyPixels(px, p.PixelWidth * 4, 0);
		return px;
	}

	private static float[] ToFloat(byte[] b)
	{
		float[] f = new float[b.Length];
		for (int i = 0; i < b.Length; i++)
		{
			f[i] = b[i] / 255f;
		}
		return f;
	}

	// Area (box) resample of premultiplied BGRA into an n x n box, uniform and centred on whole pixels exactly like
	// TrayIconFit.Fit, so masks made here line up with the fitted icons. Result in 0..1.
	internal static float[] AreaFit(byte[] s, int sw, int sh, int n)
	{
		double k = Math.Min((double)n / sw, (double)n / sh);
		int dw = Math.Clamp((int)Math.Round(sw * k), 1, n);
		int dh = Math.Clamp((int)Math.Round(sh * k), 1, n);
		int ox = (n - dw) / 2;
		int oy = (n - dh) / 2;
		double fx = (double)sw / dw;
		double fy = (double)sh / dh;
		double[] t = new double[dw * sh * 4];
		for (int x = 0; x < dw; x++)
		{
			double x0 = x * fx;
			double x1 = x0 + fx;
			for (int sx = (int)x0; sx < sw && sx < x1; sx++)
			{
				double wgt = Math.Min(x1, sx + 1) - Math.Max(x0, sx);
				if (wgt <= 0.0)
				{
					continue;
				}
				for (int y = 0; y < sh; y++)
				{
					int si = (y * sw + sx) * 4;
					int ti = (y * dw + x) * 4;
					t[ti] += s[si] * wgt;
					t[ti + 1] += s[si + 1] * wgt;
					t[ti + 2] += s[si + 2] * wgt;
					t[ti + 3] += s[si + 3] * wgt;
				}
			}
		}
		float[] d = new float[n * n * 4];
		double norm = 1.0 / (fx * fy * 255.0);
		for (int y = 0; y < dh; y++)
		{
			double y0 = y * fy;
			double y1 = y0 + fy;
			for (int x = 0; x < dw; x++)
			{
				double b = 0.0, g = 0.0, r = 0.0, a = 0.0;
				for (int sy = (int)y0; sy < sh && sy < y1; sy++)
				{
					double wgt = Math.Min(y1, sy + 1) - Math.Max(y0, sy);
					if (wgt <= 0.0)
					{
						continue;
					}
					int ti = (sy * dw + x) * 4;
					b += t[ti] * wgt;
					g += t[ti + 1] * wgt;
					r += t[ti + 2] * wgt;
					a += t[ti + 3] * wgt;
				}
				int di = ((y + oy) * n + (x + ox)) * 4;
				d[di] = (float)Math.Clamp(b * norm, 0.0, 1.0);
				d[di + 1] = (float)Math.Clamp(g * norm, 0.0, 1.0);
				d[di + 2] = (float)Math.Clamp(r * norm, 0.0, 1.0);
				d[di + 3] = (float)Math.Clamp(a * norm, 0.0, 1.0);
			}
		}
		return d;
	}

	private static ulong Fnv(byte[] px, int w, int h)
	{
		ulong hash = 14695981039346656037UL;
		hash = (hash ^ (ulong)w) * 1099511628211UL;
		hash = (hash ^ (ulong)h) * 1099511628211UL;
		foreach (byte b in px)
		{
			hash = (hash ^ b) * 1099511628211UL;
		}
		return hash;
	}

	// ===================================================================== persistence

	private sealed class StoreFile
	{
		public int Version { get; set; } = 2;

		public Dictionary<string, AppState> Apps { get; set; } = new Dictionary<string, AppState>();
	}

	private sealed class AppState
	{
		public int Idle { get; set; }

		public int Seed { get; set; }

		public bool SeedTrusted { get; set; }

		public List<int> Votes { get; set; } = new List<int>();

		// When each vote was cast (UTC ticks), in the order of Votes.
		public List<long> VoteTimes { get; set; } = new List<long>();

		// The processes that voted ("pid:creation time"), so a process votes once.
		public List<string> Voters { get; set; } = new List<string>();

		public int NextId { get; set; }

		public long Decay { get; set; }

		public List<StateEntry> States { get; set; } = new List<StateEntry>();
	}

	private sealed class StateEntry
	{
		public int Id { get; set; }

		public double Score { get; set; }

		public int Episodes { get; set; }

		public long LastSeen { get; set; }

		public long BurstAt { get; set; }

		public List<ImageEntry> Images { get; set; } = new List<ImageEntry>();
	}

	private sealed class ImageEntry
	{
		public int W { get; set; }

		public int H { get; set; }

		// Premultiplied BGRA, deflated, base64.
		public string Pixels { get; set; } = "";

		// The image itself, packed by the writer (not serialised).
		internal Print? Source;
	}

	private static void MarkDirty(bool structural)
	{
		_savePending = true;
		if (structural)
		{
			_structuralPending = true;
		}
	}

	private static void SaveIfDue(long now)
	{
		if (!_savePending || !CanPersist || _readFailed)
		{
			return;
		}
		long gap = now - _lastSave;
		if (gap < 0 || gap >= (_structuralPending ? SaveStructural : SaveScores))
		{
			SaveNow(now, background: !QaSyncWrites);
		}
	}

	// Writes what was learned. Under the lock only a snapshot is taken (the images are immutable and referenced, not
	// copied); packing, serialising and writing happen off the UI thread unless background is false.
	internal static void SaveNow(long now, bool background)
	{
		if (!CanPersist || _readFailed)
		{
			return;
		}
		string path = StorePath;
		StoreFile f = new StoreFile();
		long seq;
		lock (Gate)
		{
			foreach (Model m in Models.Values)
			{
				// The state shown now counts up to now (its episode stays open and keeps its cap).
				CreditOpen(m, now);
				if (m.Clusters.Count == 0)
				{
					continue;
				}
				AppState a = new AppState
				{
					Idle = m.IdleId,
					Seed = m.SeedId,
					SeedTrusted = m.SeedTrusted,
					Votes = m.Votes.Select(v => v.Id).ToList(),
					VoteTimes = m.Votes.Select(v => v.At).ToList(),
					Voters = m.Voters.ToList(),
					NextId = m.NextId,
					Decay = m.LastDecay
				};
				foreach (Cluster c in m.Clusters)
				{
					StateEntry e = new StateEntry { Id = c.Id, Score = Math.Round(c.Score, 1), Episodes = c.Episodes, LastSeen = c.LastSeen, BurstAt = c.BurstAt };
					foreach (Print p in c.Variants)
					{
						e.Images.Add(new ImageEntry { W = p.W, H = p.H, Source = p });
					}
					a.States.Add(e);
				}
				f.Apps[m.Key] = a;
			}
			_lastSave = now;
			_savePending = false;
			_structuralPending = false;
			seq = ++_saveSeq;
		}
		if (!background)
		{
			WriteStore(path, f, seq);
			return;
		}
		Interlocked.Increment(ref _writesPending);
		Task.Run(delegate
		{
			try
			{
				WriteStore(path, f, seq);
			}
			finally
			{
				Interlocked.Decrement(ref _writesPending);
			}
		});
	}

	// Packs, serialises and writes one snapshot. Writes are serialised, and an older snapshot never replaces a newer one.
	private static void WriteStore(string path, StoreFile f, long seq)
	{
		string tmp = path + ".tmp";
		try
		{
			foreach (AppState a in f.Apps.Values)
			{
				foreach (StateEntry e in a.States)
				{
					foreach (ImageEntry img in e.Images)
					{
						if (img.Source is Print p)
						{
							string packed = p.Packed ?? Convert.ToBase64String(Deflate(p.Native));
							p.Packed = packed;
							img.Pixels = packed;
						}
					}
				}
			}
			string json = JsonSerializer.Serialize(f);
			lock (WriteGate)
			{
				if (seq <= _writtenSeq)
				{
					return;
				}
				Directory.CreateDirectory(Path.GetDirectoryName(path)!);
				File.WriteAllText(tmp, json);
				File.Move(tmp, path, overwrite: true);
				_writtenSeq = seq;
			}
		}
		catch (Exception ex)
		{
			Logger.Log("Seasonal tray: could not save " + Path.GetFileName(path) + ": " + ex.Message);
			try
			{
				lock (WriteGate)
				{
					if (File.Exists(tmp))
					{
						File.Delete(tmp);
					}
				}
			}
			catch
			{
			}
		}
	}

	// Starts reading the store off the UI thread (at shell start), so the first tray icon does not wait for the disk.
	internal static void Preload()
	{
		try
		{
			if (_preload != null || _loaded || !CanPersist)
			{
				return;
			}
			_preload = Task.Run(() => ReadStore(retry: true));
		}
		catch (Exception ex)
		{
			Logger.Log("Seasonal tray: " + ex.Message);
		}
	}

	// What was learned, read once (from the preload when there is one) and merged into the models. Under Gate.
	private static void EnsureLoaded()
	{
		if (_loaded)
		{
			return;
		}
		_loaded = true;
		Dictionary<string, Model> read;
		try
		{
			Task<Dictionary<string, Model>>? pre = _preload;
			read = pre != null ? pre.GetAwaiter().GetResult() : ReadStore(retry: false);
		}
		catch (Exception ex)
		{
			Logger.Log("Seasonal tray: could not read " + Path.GetFileName(StorePath) + ": " + ex.Message);
			read = new Dictionary<string, Model>(StringComparer.Ordinal);
		}
		long now = Now;
		foreach (KeyValuePair<string, Model> kv in read)
		{
			if (!Models.ContainsKey(kv.Key))
			{
				SelectIdle(kv.Value, now);
				Models[kv.Key] = kv.Value;
			}
		}
		_savePending = false;
		_structuralPending = false;
		if (!_exitHooked && QaStorePath == null && Application.Current is Application app)
		{
			// What was learned since the last save is written when the shell closes normally.
			_exitHooked = true;
			app.Exit += delegate
			{
				try
				{
					Flush();
				}
				catch
				{
				}
			};
		}
	}

	// Writes now (on the calling thread) when anything was learned or an episode is open; waits for a background write.
	internal static void Flush()
	{
		bool due;
		lock (Gate)
		{
			due = _loaded && (_savePending || Models.Values.Any(m => m.OpenId != 0));
		}
		if (due)
		{
			SaveNow(Now, background: false);
		}
	}

	// Reads the store without touching the models (any thread). One bad app or image is skipped, not the whole file;
	// a file that cannot be parsed is kept as seasonal-tray.json.bad; a file that cannot be opened stops saving for
	// this session so it is not overwritten.
	private static Dictionary<string, Model> ReadStore(bool retry)
	{
		Dictionary<string, Model> result = new Dictionary<string, Model>(StringComparer.Ordinal);
		if (!CanPersist)
		{
			return result;
		}
		string path = StorePath;
		string? text = null;
		for (int attempt = 0; ; attempt++)
		{
			try
			{
				FileInfo fi = new FileInfo(path);
				if (!fi.Exists)
				{
					return result;
				}
				if (fi.Length > 4 * 1024 * 1024)
				{
					Quarantine(path, "larger than 4 MB");
					return result;
				}
				text = File.ReadAllText(path);
				break;
			}
			catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
			{
				if (!retry || attempt >= 3)
				{
					_readFailed = true;
					Logger.Log("Seasonal tray: could not read " + Path.GetFileName(path) + " (learning is not saved this session): " + ex.Message);
					return result;
				}
				Thread.Sleep(250);
			}
		}
		StoreFile? f;
		try
		{
			f = JsonSerializer.Deserialize<StoreFile>(text);
		}
		catch (Exception ex)
		{
			Quarantine(path, ex.Message);
			return result;
		}
		if (f?.Apps == null)
		{
			return result;
		}
		int skipped = 0;
		foreach (KeyValuePair<string, AppState> kv in f.Apps)
		{
			try
			{
				Model? m = ModelFrom(kv.Key, kv.Value, ref skipped);
				if (m != null)
				{
					result[kv.Key] = m;
				}
			}
			catch (Exception ex)
			{
				skipped++;
				Logger.Log("Seasonal tray: skipped what was learned for " + kv.Key + ": " + ex.Message);
			}
		}
		if (skipped > 0)
		{
			Logger.Log($"Seasonal tray: {skipped} unreadable entr{(skipped == 1 ? "y" : "ies")} in {Path.GetFileName(path)} skipped");
		}
		return result;
	}

	private static Model? ModelFrom(string key, AppState? a, ref int skipped)
	{
		if (a?.States == null || string.IsNullOrEmpty(key))
		{
			return null;
		}
		Model m = new Model(key)
		{
			IdleId = a.Idle,
			SeedId = a.Seed,
			SeedTrusted = a.SeedTrusted,
			NextId = Math.Max(1, a.NextId),
			LastDecay = a.Decay
		};
		foreach (StateEntry? e in a.States.Take(MaxClusters))
		{
			if (e == null || e.Id <= 0 || m.Find(e.Id) != null)
			{
				continue;
			}
			Cluster c = new Cluster { Id = e.Id, Score = double.IsFinite(e.Score) ? Math.Max(0, e.Score) : 0, Episodes = e.Episodes, LastSeen = e.LastSeen, BurstAt = e.BurstAt };
			foreach (ImageEntry? img in e.Images?.Take(MaxVariants) ?? Enumerable.Empty<ImageEntry>())
			{
				if (img == null || img.W < 1 || img.H < 1 || img.W > MaxNative || img.H > MaxNative || string.IsNullOrEmpty(img.Pixels))
				{
					continue;
				}
				try
				{
					byte[] px = Inflate(Convert.FromBase64String(img.Pixels), img.W * img.H * 4);
					if (px.Length == img.W * img.H * 4)
					{
						c.Variants.Add(new Print(img.W, img.H, px) { Packed = img.Pixels });
					}
					else
					{
						skipped++;
					}
				}
				catch (Exception ex) when (ex is FormatException || ex is InvalidDataException)
				{
					skipped++;
				}
			}
			if (c.Variants.Count > 0)
			{
				m.Clusters.Add(c);
				m.NextId = Math.Max(m.NextId, c.Id + 1);
			}
		}
		if (m.Clusters.Count == 0)
		{
			return null;
		}
		List<int> votes = a.Votes ?? new List<int>();
		List<long> times = a.VoteTimes ?? new List<long>();
		for (int i = 0; i < votes.Count; i++)
		{
			if (m.Find(votes[i]) != null)
			{
				m.Votes.Add((votes[i], i < times.Count ? times[i] : 0L));
			}
		}
		while (m.Votes.Count > MaxVotes)
		{
			m.Votes.RemoveAt(0);
		}
		m.Voters.AddRange((a.Voters ?? new List<string>()).Where(s => !string.IsNullOrEmpty(s)).TakeLast(MaxVoters));
		if (m.Find(m.IdleId) == null)
		{
			m.IdleId = 0;
		}
		return m;
	}

	// A store that cannot be used is kept beside it for inspection, and learning starts afresh.
	private static void Quarantine(string path, string why)
	{
		try
		{
			File.Move(path, path + ".bad", overwrite: true);
			Logger.Log("Seasonal tray: " + Path.GetFileName(path) + " could not be used (" + why + "); kept as " + Path.GetFileName(path) + ".bad");
		}
		catch (Exception ex)
		{
			_readFailed = true;
			Logger.Log("Seasonal tray: " + Path.GetFileName(path) + " could not be used (" + why + ") or set aside: " + ex.Message);
		}
	}

	private static byte[] Deflate(byte[] data)
	{
		using MemoryStream ms = new MemoryStream();
		using (DeflateStream z = new DeflateStream(ms, CompressionLevel.Optimal, leaveOpen: true))
		{
			z.Write(data, 0, data.Length);
		}
		return ms.ToArray();
	}

	private static byte[] Inflate(byte[] data, int expected)
	{
		using MemoryStream ms = new MemoryStream(data, writable: false);
		using DeflateStream z = new DeflateStream(ms, CompressionMode.Decompress);
		byte[] buf = new byte[expected];
		int read = z.ReadAtLeast(buf, expected, throwOnEndOfStream: false);
		return read == expected && z.ReadByte() < 0 ? buf : Array.Empty<byte>();
	}

	// ===================================================================== QA

	// Forgets everything learned in memory (the store file is left alone).
	internal static void QaReset()
	{
		QaWaitWrites();
		lock (Gate)
		{
			Models.Clear();
			_loaded = false;
			_preload = null;
			_readFailed = false;
			_savePending = false;
			_structuralPending = false;
			_lastSave = 0;
		}
	}

	// Waits until background writes of the store have finished.
	internal static void QaWaitWrites()
	{
		SpinWait.SpinUntil(() => Volatile.Read(ref _writesPending) == 0, 5000);
	}

	// The idle state's id, the trusted seed, the number of states and votes learned for a key.
	internal static (int Idle, int Seed, bool SeedTrusted, int States, int Version, int Votes, bool Weak) QaModel(string key)
	{
		lock (Gate)
		{
			return Models.TryGetValue(key, out Model? m) ? (m.IdleId, m.SeedId, m.SeedTrusted, m.Clusters.Count, m.Version, m.Votes.Count, m.IdleWeak) : (0, 0, false, 0, 0, 0, false);
		}
	}

	// The state an image belongs to (0 when none) and its display time in seconds, the open episode included.
	internal static (int Id, double Score) QaState(string key, ImageSource img)
	{
		lock (Gate)
		{
			if (!Models.TryGetValue(key, out Model? m) || img is not BitmapSource bs)
			{
				return (0, 0);
			}
			Print p = new Print(bs.PixelWidth, bs.PixelHeight, Pixels(bs));
			foreach (Cluster c in m.Clusters)
			{
				if (c.Variants.Any(v => Same(p, v)))
				{
					return (c.Id, EffScore(m, c, Now));
				}
			}
			return (0, 0);
		}
	}

	internal static bool QaPending => Volatile.Read(ref _savePending);

	internal static bool QaStructuralPending => Volatile.Read(ref _structuralPending);

	internal static Print? QaPrint(ImageSource img)
	{
		return img is BitmapSource bs ? new Print(bs.PixelWidth, bs.PixelHeight, Pixels(bs)) : null;
	}

	// ===================================================================== interop

	[DllImport("user32.dll")]
	private static extern uint GetWindowThreadProcessId(nint hWnd, out uint pid);

	[DllImport("kernel32.dll", SetLastError = true)]
	private static extern nint OpenProcess(uint access, bool inherit, uint pid);

	[DllImport("kernel32.dll", SetLastError = true)]
	private static extern bool GetProcessTimes(nint process, out long creation, out long exit, out long kernel, out long user);

	[DllImport("kernel32.dll")]
	private static extern bool CloseHandle(nint h);
}

// What the seasonal tray logic last saw and decided for one notification-area icon (TrayAppIcon.SeasonalTray).
internal sealed class SeasonalTrayLive
{
	public ImageSource? Observed;

	public string? Key;

	public SeasonalTrayIcons.Print? Print;

	// The state of the image shown (0 = a frame that is not learned, during an animation).
	public int ClusterId;

	public long Since;

	public SeasonalTrayLook Look = SeasonalTrayLook.Seasonal;

	public SeasonalTrayIcons.Verdict Verdict;

	public int LookVersion = -1;

	public long BurstUntil;

	public bool BurstGlobal;

	// When the icon has to be looked at again without a new image (a start-up vote or an idle change comes due); 0 = never.
	public long RecheckAt;

	// The process whose start-up vote is pending, and when its icon first appeared.
	public string? VoteProc;

	public long VoteFrom;

	// The last changes (time, state, classification), newest last.
	private readonly long[] _times = new long[8];

	private readonly int[] _states = new int[8];

	private readonly SeasonalTrayLook[] _looks = new SeasonalTrayLook[8];

	private int _count;

	public void Push(long now, int state)
	{
		int i = _count % _times.Length;
		_times[i] = now;
		_states[i] = state;
		_looks[i] = SeasonalTrayLook.Seasonal;
		_count++;
	}

	// The classification of the newest change, before the blinking rule.
	public void SetLook(SeasonalTrayLook look)
	{
		if (_count > 0)
		{
			_looks[(_count - 1) % _looks.Length] = look;
		}
	}

	// True when the change before the newest one was classified as look and was shown for less than window.
	public bool PreviousWas(SeasonalTrayLook look, long now, long window)
	{
		if (_count < 2)
		{
			return false;
		}
		int k = (_count - 2) % _times.Length;
		return _looks[k] == look && now - _times[k] < window;
	}

	public int RecentChanges(long now, long window)
	{
		int n = 0;
		for (int k = 0; k < Math.Min(_count, _times.Length); k++)
		{
			if (now - _times[k] <= window)
			{
				n++;
			}
		}
		return n;
	}

	public IEnumerable<SeasonalTrayLook> RecentLooks(long now, long window)
	{
		for (int k = 0; k < Math.Min(_count, _times.Length); k++)
		{
			if (now - _times[k] <= window)
			{
				yield return _looks[k];
			}
		}
	}

	public IEnumerable<int> RecentStates(long now, long window)
	{
		for (int k = 0; k < Math.Min(_count, _times.Length); k++)
		{
			if (now - _times[k] <= window && _states[k] != 0)
			{
				yield return _states[k];
			}
		}
	}

	public int RecentClusterCount(long now, long window, int state)
	{
		int n = 0;
		for (int k = 0; k < Math.Min(_count, _times.Length); k++)
		{
			if (now - _times[k] <= window && _states[k] == state)
			{
				n++;
			}
		}
		return n;
	}
}
