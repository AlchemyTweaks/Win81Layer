#nullable enable

using System;
using System.Collections.Generic;

namespace Win81Layer;

// The 17 Windows pointer roles, in the order a Control Panel\Cursors\Schemes entry lists them.
internal enum CursorRole
{
	Arrow,
	Help,
	AppStarting,
	Wait,
	Crosshair,
	IBeam,
	NWPen,
	No,
	SizeNS,
	SizeWE,
	SizeNWSE,
	SizeNESW,
	SizeAll,
	UpArrow,
	Hand,
	Pin,
	Person
}

// Colours of one Alchemy pointer set. Colours are 0xRRGGBB (straight), byte alphas are 0..255.
// Accent: dot, glow, link ring and fill, badges, text-select serifs, alternate head, pen nib, unavailable sign.
// Neutral: the normal ring and the glyph strokes. Halo: the outline under every pointer that keeps it readable on a
// background of the other tone (it disappears on the tone the set is made for). OnAccent: glyphs on a badge.
// Spinner: the moving arc of the working and busy pointers.
internal sealed record AlchemyPalette(string Id, string Tone, string Season, uint Accent, uint Neutral, byte NeutralAlpha, byte LinkFillAlpha, uint Halo, double HaloAlpha, uint OnAccent, uint Spinner);

internal static class AlchemyPalettes
{
	public const string Dark = "dark";

	public const string Light = "light";

	public const string Auto = "auto";

	// The Dark rest set: the dark tone with a muted blue-grey accent and a softer, less blue ring, used for every mode
	// while Dark rest is on. The ring stays light enough (3:1 on mid grey) that the thin resize pointers stay findable.
	// Its spinner is a light blue-grey rather than the accent: the muted accent alone sits too close to the grey busy
	// ring in brightness, and the set has no strong hue to make the moving arc stand out.
	public const string RestSeason = "rest";

	// Dark tone = white ring (for dark backgrounds and dark mode), light tone = dark ring. The seasonal sets keep the
	// structure and swap the accent (and the light ring) for the season's colours. The dots and rings are exactly the
	// website's seasonal cursors, so the pointer matches the site; that is why Christmas on the dark tone is the site's
	// coral red and Halloween on the light tone its deep rust. The spinner is not on the website: Christmas spins in
	// holly green so the set reads as Christmas, the other sets spin in their accent.
	// Halo strength: black at 0.62 gives about 6:1 on white and 3.3:1 on mid grey and stays invisible on dark
	// backgrounds; white at 0.85 does the same for the light tone on black and mid grey.
	public static readonly AlchemyPalette[] All = new AlchemyPalette[7]
	{
		new AlchemyPalette("dark", Dark, "", 0x2F9DFFu, 0xDEE6F2u, 225, 46, 0x000000u, 0.62, 0x0A0F18u, 0x2F9DFFu),
		new AlchemyPalette("light", Light, "", 0x0A6AC2u, 0x0F192Bu, 209, 40, 0xFFFFFFu, 0.85, 0xFFFFFFu, 0x0A6AC2u),
		new AlchemyPalette("halloween-dark", Dark, "halloween", 0xFF8A2Au, 0xDEE6F2u, 225, 46, 0x000000u, 0.62, 0x0A0F18u, 0xFF8A2Au),
		new AlchemyPalette("halloween-light", Light, "halloween", 0xAA4405u, 0x26160Cu, 209, 40, 0xFFFFFFu, 0.85, 0xFFFFFFu, 0xAA4405u),
		new AlchemyPalette("christmas-dark", Dark, "christmas", 0xFF5C70u, 0xDEE6F2u, 225, 46, 0x000000u, 0.62, 0x0A0F18u, 0x34B36Fu),
		new AlchemyPalette("christmas-light", Light, "christmas", 0xC51332u, 0x2C1115u, 209, 40, 0xFFFFFFu, 0.85, 0xFFFFFFu, 0x17693Fu),
		new AlchemyPalette("rest-dark", Dark, RestSeason, 0x8FA3B8u, 0xE0E5EBu, 225, 40, 0x000000u, 0.62, 0x0A0F18u, 0xC9D1DBu)
	};

	public static AlchemyPalette? Find(string? id)
	{
		if (string.IsNullOrWhiteSpace(id))
		{
			return null;
		}
		string t = id.Trim();
		foreach (AlchemyPalette p in All)
		{
			if (string.Equals(p.Id, t, StringComparison.OrdinalIgnoreCase))
			{
				return p;
			}
		}
		return null;
	}

	// "", "dark", "light" or "auto"; anything else is "" (off).
	public static string NormalizeMode(string? mode)
	{
		if (string.IsNullOrWhiteSpace(mode))
		{
			return "";
		}
		string t = mode.Trim().ToLowerInvariant();
		return (t == Dark || t == Light || t == Auto) ? t : "";
	}

	// "", "halloween", "christmas" or "rest".
	public static string NormalizeSeason(string? season)
	{
		if (string.IsNullOrWhiteSpace(season))
		{
			return "";
		}
		string t = season.Trim().ToLowerInvariant();
		return (t == "halloween" || t == "christmas" || t == RestSeason) ? t : "";
	}

	// The palette a pointer setting shows right now: the tone follows light/dark mode for "auto", and the season's
	// colours replace the blue while a season is on and seasonal pointer colours are allowed. "" when mode is off.
	public static string Resolve(string? mode, bool isDark, string? season, bool seasonalOn)
	{
		string m = NormalizeMode(mode);
		if (m.Length == 0)
		{
			return "";
		}
		string s = NormalizeSeason(season);
		if (s == RestSeason)
		{
			// Dark rest: dark pointers for every mode, in the muted set while seasonal pointer colours are allowed.
			return seasonalOn ? "rest-dark" : Dark;
		}
		string tone = m == Auto ? (isDark ? Dark : Light) : m;
		return (seasonalOn && s.Length > 0) ? s + "-" + tone : tone;
	}

	// The same set in the other tone (used to keep both tones ready while "auto" is on).
	public static string OtherTone(string paletteId)
	{
		AlchemyPalette? p = Find(paletteId);
		if (p == null || p.Season == RestSeason)
		{
			// The Dark rest set has only the dark tone.
			return "";
		}
		string other = p.Tone == Dark ? Light : Dark;
		return p.Season.Length > 0 ? p.Season + "-" + other : other;
	}
}

// Draws every Alchemy pointer in code, at every frame size, from the measured geometry of the brand cursors: a glowing
// dot inside a thin ring (normal) and a large open ring (link). Pure managed code, no WPF; safe on any thread.
// Coverage is analytic: each shape is a signed distance field and a pixel takes clamp(0.5 - distance, 0, 1).
internal static class AlchemyCursorArt
{
	public static readonly int[] Sizes = new int[5] { 32, 48, 64, 96, 128 };

	public const int AnimFrames = 24;

	// 1 jiffy = 1/60 s: 24 frames at 2 jiffies = one 0.8 s revolution.
	public const int AnimJiffies = 2;

	public static readonly CursorRole[] Roles = (CursorRole[])Enum.GetValues(typeof(CursorRole));

	// Geometry on a 32-unit grid (u = size / 32 px).
	private const double RD = 2.565;      // normal dot radius

	private const double G0 = 0.54;       // glow peak alpha

	private const double GC = 3.0;        // glow plateau radius

	private const double GS = 1.95;       // glow falloff (sigma)

	private const double KA = 0.145;      // dark shade alpha at the ring

	private const double KS0 = 2.6;

	private const double KS1 = 6.0;

	private const double RIN = 7.7225;    // normal ring

	private const double ROUT = 9.05;

	private const double RMID = 8.38625;

	private const double LRIN = 11.5775;  // link ring

	private const double LROUT = 13.05;

	private const double LRD = 2.5525;    // link dot

	private const double LinkRingAlpha = 242.0 / 255.0;

	private const double StrokeAlpha = 242.0 / 255.0;

	// The working pointer's spinner runs on its own orbit just outside the ring, so it shows against the background
	// instead of against a ring of the same brightness.
	private const double SpinR = 11.3;

	private const double SpinW = 2.6;

	private const double SpinSpan = 180.0;

	private const double SpinPower = 0.6;

	// Halo outline of the ring pointers: 1 unit, never thinner than 1.5 px so it still reads at 32 px.
	private static double KeyW(Canvas c)
	{
		return Math.Max(c.U, 1.5);
	}

	public static bool IsAnimated(CursorRole role)
	{
		return role == CursorRole.AppStarting || role == CursorRole.Wait;
	}

	// The click point of each role at a frame size. Ring roles click at the centre like the brand cursors; the
	// handwriting pen at its tip, the alternate arrow at its tip and the location pin at its point.
	public static (int X, int Y) Hotspot(CursorRole role, int size)
	{
		double u = size / 32.0;
		int c = size / 2;
		switch (role)
		{
		case CursorRole.NWPen:
		{
			int h = (int)Math.Floor(3.0 * u);
			return (h, h);
		}
		case CursorRole.UpArrow:
			return (c, (int)Math.Floor(3.0 * u));
		case CursorRole.Pin:
			return (c, PinTipRow(size));
		default:
			return (c, c);
		}
	}

	private static int PinTipRow(int size)
	{
		double u = size / 32.0;
		return (int)Math.Ceiling(size / 2.0 + 12.5 * u - 1e-9) - 1;
	}

	// Straight-alpha BGRA, top-down, size x size. frame is used by the animated roles (0..AnimFrames-1, wraps).
	// keyline: false leaves out the halo outline of the ring pointers (the self-test measures the brand geometry on it).
	public static byte[] RenderBgra(CursorRole role, AlchemyPalette p, int size, int frame = 0, bool keyline = true)
	{
		Canvas c = new Canvas(size) { Keyline = keyline };
		Draw(c, role, p, ((frame % AnimFrames) + AnimFrames) % AnimFrames);
		return c.ToBgra();
	}

	// ===================================================================== roles

	private static void Draw(Canvas c, CursorRole role, AlchemyPalette p, int frame)
	{
		double u = c.U;
		double C = c.S / 2.0;
		switch (role)
		{
		case CursorRole.Arrow:
			NormalBody(c, p, C, 1.0, dot: true, ring: true);
			return;
		case CursorRole.Hand:
			LinkBody(c, p, C);
			return;
		case CursorRole.Help:
			NormalBody(c, p, C, 1.0, dot: true, ring: true);
			HelpBadge(c, p, C + 8.5 * u, C - 8.5 * u);
			return;
		case CursorRole.Person:
			NormalBody(c, p, C, 1.0, dot: true, ring: true);
			PersonBadge(c, p, C + 8.5 * u, C - 8.5 * u);
			return;
		case CursorRole.AppStarting:
		{
			// The normal pointer with a spinner orbiting just outside its ring.
			double head = -90.0 + 15.0 * frame;
			if (c.Keyline)
			{
				Comet(c, p.Halo, C, C, SpinR * u, SpinW * u, head, SpinSpan, SpinPower, KeyW(c), p.HaloAlpha);
			}
			NormalBody(c, p, C, 1.0, dot: true, ring: true);
			Comet(c, p.Spinner, C, C, SpinR * u, SpinW * u, head, SpinSpan, SpinPower);
			return;
		}
		case CursorRole.Wait:
		{
			double phi = 2.0 * Math.PI * frame / AnimFrames;
			double head = -90.0 + 15.0 * frame;
			if (c.Keyline)
			{
				Comet(c, p.Halo, C, C, RMID * u, 2.0 * u, head, 250.0, 1.6, KeyW(c), p.HaloAlpha);
				c.Paint(Annulus(C, C, ROUT * u, ROUT * u + KeyW(c)), p.Halo, p.HaloAlpha);
			}
			NormalBody(c, p, C, 0.75 + 0.25 * Math.Cos(phi), dot: false, ring: false);
			c.Paint(Annulus(C, C, RIN * u, ROUT * u), p.Neutral, p.NeutralAlpha / 255.0 * 0.6);
			Comet(c, p.Spinner, C, C, RMID * u, 2.0 * u, head, 250.0, 1.6);
			c.Paint(Disc(C, C, (1.5 + 0.25 * (1.0 + Math.Cos(phi))) * u), p.Accent, 1.0);
			return;
		}
		case CursorRole.No:
		{
			double t = 1.4725 * u;
			double k = 0.7071067811865476 * (RIN + 0.2) * u;
			Sdf ring = Annulus(C, C, RIN * u, RIN * u + t);
			Sdf slash = Capsule(C - k, C - k, C + k, C + k, t);
			// One shape, so the crossing is not painted twice. The sign is solid and the fill light, so the slash leads.
			Sdf sign = Union(ring, slash);
			if (c.Keyline)
			{
				c.Paint(Grow(sign, KeyW(c)), p.Halo, p.HaloAlpha);
			}
			c.Paint(Disc(C, C, RIN * u), p.Accent, 0.6 * p.LinkFillAlpha / 255.0);
			c.Paint(sign, p.Accent, 1.0);
			return;
		}
		}
		DrawGlyph(c, role, p);
	}

	// The brand "normal" pointer: glow + shade inside the ring, then the dot, then the ring. With the keyline, the
	// palette's halo runs just outside the ring.
	private static void NormalBody(Canvas c, AlchemyPalette p, double C, double glow, bool dot, bool ring)
	{
		double u = c.U;
		Rgb a = Rgb.From(p.Accent);
		double rin = RIN * u;
		double gc = GC * u;
		double gs2 = 2.0 * (GS * u) * (GS * u);
		double k0 = KS0 * u;
		double k1 = KS1 * u;
		if (ring && c.Keyline)
		{
			c.Paint(Annulus(C, C, ROUT * u, ROUT * u + KeyW(c)), p.Halo, p.HaloAlpha);
		}
		c.PaintPremultiplied(delegate(double x, double y, out double pr, out double pg, out double pb, out double pa)
		{
			double r = Math.Sqrt((x - C) * (x - C) + (y - C) * (y - C));
			double inside = Cov(r - rin);
			double e = Math.Max(0.0, r - gc);
			double g = G0 * glow * Math.Exp(-e * e / gs2) * inside;
			double k = KA * SmoothStep(k0, k1, r) * inside;
			pr = a.R * g;
			pg = a.G * g;
			pb = a.B * g;
			pa = g + k;
		});
		if (dot)
		{
			c.Paint(Disc(C, C, RD * u), p.Accent, 1.0);
		}
		if (ring)
		{
			c.Paint(Annulus(C, C, rin, ROUT * u), p.Neutral, p.NeutralAlpha / 255.0);
		}
	}

	// The brand "link" pointer: translucent fill, dot, open accent ring (and the halo just outside it).
	private static void LinkBody(Canvas c, AlchemyPalette p, double C)
	{
		double u = c.U;
		if (c.Keyline)
		{
			c.Paint(Annulus(C, C, LROUT * u, LROUT * u + KeyW(c)), p.Halo, p.HaloAlpha);
		}
		c.Paint(Disc(C, C, LRIN * u), p.Accent, p.LinkFillAlpha / 255.0);
		c.Paint(Disc(C, C, LRD * u), p.Accent, 1.0);
		c.Paint(Annulus(C, C, LRIN * u, LROUT * u), p.Accent, LinkRingAlpha);
	}

	private static void Badge(Canvas c, AlchemyPalette p, double bx, double by)
	{
		double u = c.U;
		c.Erase(Disc(bx, by, 6.2 * u));
		if (c.Keyline)
		{
			// Under what is left of the ring, so where the halo does not show the badge looks exactly as without it.
			c.PaintUnder(Disc(bx, by, 5.0 * u + Math.Min(KeyW(c), 1.2 * u)), p.Halo, p.HaloAlpha);
		}
		c.Paint(Disc(bx, by, 5.0 * u), p.Accent, 1.0);
	}

	private static void HelpBadge(Canvas c, AlchemyPalette p, double bx, double by)
	{
		double u = c.U;
		Badge(c, p, bx, by);
		double w = Math.Max(1.0, 1.3 * u);
		// "?": a hook from the left over the top, round to the bottom, a short stem and the dot.
		Sdf hook = Arc(bx, by - 1.2 * u, 1.75 * u, w, 180.0, 280.0);
		Sdf stem = Capsule(bx + 0.3 * u, by + 0.5 * u, bx, by + 1.0 * u, w);
		c.Paint(Union(hook, stem), p.OnAccent, 1.0);
		c.Paint(Disc(bx, by + 2.8 * u, 0.85 * u), p.OnAccent, 1.0);
	}

	// A head and a rounded shoulder dome, kept inside the badge with a rim of accent all round.
	private static void PersonBadge(Canvas c, AlchemyPalette p, double bx, double by)
	{
		double u = c.U;
		Badge(c, p, bx, by);
		Sdf head = Disc(bx, by - 1.7 * u, 1.75 * u);
		double ex = 3.3 * u;
		double ey = 3.0 * u;
		double ecy = by + 4.4 * u;
		Sdf inner = Disc(bx, by, 4.1 * u);
		Sdf shoulders = delegate(double x, double y)
		{
			// Ellipse distance scaled to the x radius, clipped to the inside of the badge.
			double d = Math.Sqrt((x - bx) * (x - bx) + (y - ecy) * (y - ecy) * (ex / ey) * (ex / ey)) - ex;
			return Math.Max(d, inner(x, y));
		};
		c.Paint(Union(head, shoulders), p.OnAccent, 1.0);
	}

	// An arc that brightens towards its head: alpha t^power from tail (t=0) to head (t=1), a round cap at the head
	// only. Angles are screen angles in degrees (clockwise); headDeg is where the head is. grow > 0 draws the arc's
	// halo instead: the same arc widened by grow on each side, at alpha.
	private static void Comet(Canvas c, uint color, double cx, double cy, double rmid, double width, double headDeg, double spanDeg, double power, double grow = 0.0, double alpha = 1.0)
	{
		double head = headDeg * Math.PI / 180.0;
		double span = spanDeg * Math.PI / 180.0;
		double tail = head - span;
		double hx = cx + rmid * Math.Cos(head);
		double hy = cy + rmid * Math.Sin(head);
		double half = width / 2.0 + grow;
		c.PaintCoverage(delegate(double x, double y)
		{
			double dx = x - cx;
			double dy = y - cy;
			double r = Math.Sqrt(dx * dx + dy * dy);
			double band = Cov(Math.Abs(r - rmid) - half);
			double a = 0.0;
			if (band > 0.0)
			{
				double rel = Mod(Math.Atan2(dy, dx) - tail, 2.0 * Math.PI);
				if (rel <= span)
				{
					a = band * Math.Pow(rel / span, power);
				}
			}
			double cap = Cov(Math.Sqrt((x - hx) * (x - hx) + (y - hy) * (y - hy)) - half);
			return Math.Max(a, cap) * alpha;
		}, color);
	}

	// ===================================================================== glyph roles

	private static void DrawGlyph(Canvas c, CursorRole role, AlchemyPalette p)
	{
		double u = c.U;
		int s = c.S;
		double C = s / 2.0;
		// Stroke widths snap to whole pixels and the glyph centre sits on a pixel centre for odd widths, so the
		// straight strokes stay sharp at every size.
		double w = Math.Max(1.0, Math.Round(1.3275 * u, MidpointRounding.AwayFromZero));
		// At 32 px the 1 px strokes are drawn solid so they weigh the same as the ring next to them.
		double ink = s <= 32 ? 1.0 : StrokeAlpha;
		double cx = C + (((int)w % 2 == 1) ? 0.5 : 0.0);
		double cy = cx;
		List<Sdf> neutral = new List<Sdf>();
		List<Sdf> accent = new List<Sdf>();
		switch (role)
		{
		case CursorRole.Crosshair:
		{
			foreach ((double dx, double dy) in Axes4)
			{
				neutral.Add(Capsule(cx + dx * 3.5 * u, cy + dy * 3.5 * u, cx + dx * ROUT * u, cy + dy * ROUT * u, w));
			}
			accent.Add(Disc(cx, cy, 1.1 * u));
			break;
		}
		case CursorRole.IBeam:
		{
			// The serif rows snap to whole pixels so they stay one crisp line.
			double h = Math.Round(8.5 * u, MidpointRounding.AwayFromZero);
			double sw = 3.25 * u;
			neutral.Add(Capsule(cx, cy - h, cx, cy + h, w));
			accent.Add(Capsule(cx - sw, cy - h, cx + sw, cy - h, w));
			accent.Add(Capsule(cx - sw, cy + h, cx + sw, cy + h, w));
			break;
		}
		case CursorRole.SizeNS:
		case CursorRole.SizeWE:
		case CursorRole.SizeNWSE:
		case CursorRole.SizeNESW:
		{
			(double dx, double dy) = role switch
			{
				CursorRole.SizeNS => (0.0, 1.0),
				CursorRole.SizeWE => (1.0, 0.0),
				CursorRole.SizeNWSE => (0.7071067811865476, 0.7071067811865476),
				_ => (0.7071067811865476, -0.7071067811865476)
			};
			neutral.Add(Capsule(cx - dx * 6.5 * u, cy - dy * 6.5 * u, cx + dx * 6.5 * u, cy + dy * 6.5 * u, w));
			neutral.Add(ArrowHead(cx + dx * 10.5 * u, cy + dy * 10.5 * u, dx, dy, 5.0 * u, 3.6 * u, 0.35 * u));
			neutral.Add(ArrowHead(cx - dx * 10.5 * u, cy - dy * 10.5 * u, -dx, -dy, 5.0 * u, 3.6 * u, 0.35 * u));
			accent.Add(Disc(cx, cy, 2.0 * u));
			break;
		}
		case CursorRole.SizeAll:
		{
			foreach ((double dx, double dy) in Axes4)
			{
				neutral.Add(Capsule(cx, cy, cx + dx * 7.5 * u, cy + dy * 7.5 * u, w));
				neutral.Add(ArrowHead(cx + dx * 11.5 * u, cy + dy * 11.5 * u, dx, dy, 4.6 * u, 3.4 * u, 0.35 * u));
			}
			accent.Add(Disc(cx, cy, 2.2 * u));
			break;
		}
		case CursorRole.UpArrow:
		{
			double ws = Math.Max(1.0, Math.Round(2.655 * u, MidpointRounding.AwayFromZero));
			double ax = C + (((int)ws % 2 == 1) ? 0.5 : 0.0);
			double tip = Math.Floor(3.0 * u) + 0.2;
			// The rounded tip (not the sharp corner) lands on the hotspot row.
			double r = 0.3 * u;
			Sdf head = Polygon(new (double, double)[3]
			{
				(ax, tip + r),
				(ax + 5.5 * u, tip + r + 8.0 * u),
				(ax - 5.5 * u, tip + r + 8.0 * u)
			}, r);
			Sdf shaft = Capsule(ax, 10.0 * u, ax, 25.0 * u, ws);
			c.Paint(Grow(Union(head, shaft), u), p.Halo, p.HaloAlpha);
			c.Paint(shaft, p.Neutral, ink);
			c.Paint(head, p.Accent, 1.0);
			return;
		}
		case CursorRole.NWPen:
		{
			double h = Math.Floor(3.0 * u);
			double tx = h + 0.2;
			double ty = h + 0.2;
			const double ax = 0.7071067811865476;
			const double ay = 0.7071067811865476;
			double px = -ay;
			double py = ax;
			double r = 0.25 * u;
			// Corner of the nib, moved back along the pen so its rounded tip lands on the hotspot.
			double vx = tx + ax * r;
			double vy = ty + ay * r;
			double b0x = tx + ax * 6.0 * u;
			double b0y = ty + ay * 6.0 * u;
			double b1x = tx + ax * 24.0 * u;
			double b1y = ty + ay * 24.0 * u;
			double hw = 2.5 * u;
			Sdf body = Capsule(b0x, b0y, b1x, b1y, 2.0 * hw);
			Sdf nib = Polygon(new (double, double)[3]
			{
				(vx, vy),
				(b0x + px * hw, b0y + py * hw),
				(b0x - px * hw, b0y - py * hw)
			}, r);
			// The pen and the pin have no pixel-aligned edges, so their halo gets the ring pointers' wider minimum.
			c.Paint(Grow(Union(body, nib), KeyW(c)), p.Halo, p.HaloAlpha);
			c.Paint(body, p.Accent, Math.Min(1.0, 2.0 * p.LinkFillAlpha / 255.0));
			c.Paint(InnerBand(body, w), p.Neutral, ink);
			c.Paint(nib, p.Accent, 1.0);
			return;
		}
		case CursorRole.Pin:
		{
			double hcx = cx;
			double hcy = C - 5.5 * u;
			double R = 6.6 * u;
			double tipY = PinTipRow(s) + 0.8;
			double dist = tipY - hcy;
			double ang = Math.Acos(R / dist);
			double baseA = Math.PI / 2.0;
			(double, double) t1 = (hcx + R * Math.Cos(baseA - ang), hcy + R * Math.Sin(baseA - ang));
			(double, double) t2 = (hcx + R * Math.Cos(baseA + ang), hcy + R * Math.Sin(baseA + ang));
			Sdf body = Union(Disc(hcx, hcy, R), Polygon(new (double, double)[4] { (hcx, tipY), t2, (hcx, hcy), t1 }, 0.0));
			c.Paint(Grow(body, KeyW(c)), p.Halo, p.HaloAlpha);
			c.Paint(body, p.Accent, Math.Min(1.0, 1.6 * p.LinkFillAlpha / 255.0));
			c.Paint(InnerBand(body, 1.32 * u), p.Neutral, p.NeutralAlpha / 255.0);
			c.Paint(Disc(hcx, hcy, RD * u), p.Accent, 1.0);
			return;
		}
		}
		Sdf inkShape = Union(neutral.ToArray());
		Sdf all = accent.Count == 0 ? inkShape : Union(inkShape, Union(accent.ToArray()));
		c.Paint(Grow(all, u), p.Halo, p.HaloAlpha);
		c.Paint(inkShape, p.Neutral, ink);
		foreach (Sdf a in accent)
		{
			c.Paint(a, p.Accent, 1.0);
		}
	}

	private static readonly (double, double)[] Axes4 = new (double, double)[4]
	{
		(0.0, 1.0),
		(0.0, -1.0),
		(1.0, 0.0),
		(-1.0, 0.0)
	};

	// Triangle pointing along (dx, dy) with its tip at (tipX, tipY), rounded by r (the outline grows by r).
	private static Sdf ArrowHead(double tipX, double tipY, double dx, double dy, double length, double halfWidth, double r)
	{
		double bx = tipX - dx * length;
		double by = tipY - dy * length;
		double px = -dy;
		double py = dx;
		return Polygon(new (double, double)[3]
		{
			(tipX, tipY),
			(bx + px * halfWidth, by + py * halfWidth),
			(bx - px * halfWidth, by - py * halfWidth)
		}, r);
	}

	// ===================================================================== distance fields

	private delegate double Sdf(double x, double y);

	private delegate void PremulField(double x, double y, out double r, out double g, out double b, out double a);

	private static double Cov(double d)
	{
		double v = 0.5 - d;
		return v <= 0.0 ? 0.0 : (v >= 1.0 ? 1.0 : v);
	}

	private static double SmoothStep(double e0, double e1, double x)
	{
		double t = (x - e0) / (e1 - e0);
		t = t < 0.0 ? 0.0 : (t > 1.0 ? 1.0 : t);
		return t * t * (3.0 - 2.0 * t);
	}

	private static double Mod(double a, double m)
	{
		double r = a % m;
		return r < 0.0 ? r + m : r;
	}

	private static Sdf Disc(double cx, double cy, double r)
	{
		return (double x, double y) => Math.Sqrt((x - cx) * (x - cx) + (y - cy) * (y - cy)) - r;
	}

	private static Sdf Annulus(double cx, double cy, double ri, double ro)
	{
		return delegate(double x, double y)
		{
			double r = Math.Sqrt((x - cx) * (x - cx) + (y - cy) * (y - cy));
			return Math.Max(ri - r, r - ro);
		};
	}

	private static Sdf Capsule(double ax, double ay, double bx, double by, double width)
	{
		double vx = bx - ax;
		double vy = by - ay;
		double len2 = vx * vx + vy * vy;
		double half = width / 2.0;
		return delegate(double x, double y)
		{
			double px = x - ax;
			double py = y - ay;
			double t = len2 > 0.0 ? (px * vx + py * vy) / len2 : 0.0;
			t = t < 0.0 ? 0.0 : (t > 1.0 ? 1.0 : t);
			double qx = px - t * vx;
			double qy = py - t * vy;
			return Math.Sqrt(qx * qx + qy * qy) - half;
		};
	}

	// Round-capped arc of the circle (cx, cy, r), stroke width w, from startDeg clockwise through spanDeg.
	private static Sdf Arc(double cx, double cy, double r, double w, double startDeg, double spanDeg)
	{
		double a0 = startDeg * Math.PI / 180.0;
		double span = spanDeg * Math.PI / 180.0;
		double a1 = a0 + span;
		double e0x = cx + r * Math.Cos(a0);
		double e0y = cy + r * Math.Sin(a0);
		double e1x = cx + r * Math.Cos(a1);
		double e1y = cy + r * Math.Sin(a1);
		double half = w / 2.0;
		return delegate(double x, double y)
		{
			double dx = x - cx;
			double dy = y - cy;
			double rel = Mod(Math.Atan2(dy, dx) - a0, 2.0 * Math.PI);
			if (rel <= span)
			{
				return Math.Abs(Math.Sqrt(dx * dx + dy * dy) - r) - half;
			}
			double d0 = Math.Sqrt((x - e0x) * (x - e0x) + (y - e0y) * (y - e0y));
			double d1 = Math.Sqrt((x - e1x) * (x - e1x) + (y - e1y) * (y - e1y));
			return Math.Min(d0, d1) - half;
		};
	}

	// Exact distance to a convex polygon (any winding), grown by round so its corners are round.
	private static Sdf Polygon((double X, double Y)[] pts, double round)
	{
		int n = pts.Length;
		double area = 0.0;
		for (int i = 0; i < n; i++)
		{
			(double x0, double y0) = pts[i];
			(double x1, double y1) = pts[(i + 1) % n];
			area += x0 * y1 - x1 * y0;
		}
		double sign = area > 0.0 ? 1.0 : -1.0;
		double[] nx = new double[n];
		double[] ny = new double[n];
		double[] ex = new double[n];
		double[] ey = new double[n];
		double[] el2 = new double[n];
		for (int i = 0; i < n; i++)
		{
			(double x0, double y0) = pts[i];
			(double x1, double y1) = pts[(i + 1) % n];
			ex[i] = x1 - x0;
			ey[i] = y1 - y0;
			el2[i] = ex[i] * ex[i] + ey[i] * ey[i];
			double len = Math.Sqrt(el2[i]);
			// Outward normal.
			nx[i] = sign * ey[i] / len;
			ny[i] = -sign * ex[i] / len;
		}
		return delegate(double x, double y)
		{
			double plane = double.NegativeInfinity;
			double edge = double.PositiveInfinity;
			for (int i = 0; i < n; i++)
			{
				double px = x - pts[i].X;
				double py = y - pts[i].Y;
				double d = px * nx[i] + py * ny[i];
				if (d > plane)
				{
					plane = d;
				}
				double t = (px * ex[i] + py * ey[i]) / el2[i];
				t = t < 0.0 ? 0.0 : (t > 1.0 ? 1.0 : t);
				double qx = px - t * ex[i];
				double qy = py - t * ey[i];
				double e = qx * qx + qy * qy;
				if (e < edge)
				{
					edge = e;
				}
			}
			double dist = plane > 0.0 ? Math.Sqrt(edge) : plane;
			return dist - round;
		};
	}

	private static Sdf Union(params Sdf[] shapes)
	{
		if (shapes.Length == 1)
		{
			return shapes[0];
		}
		return delegate(double x, double y)
		{
			double d = double.PositiveInfinity;
			foreach (Sdf s in shapes)
			{
				double v = s(x, y);
				if (v < d)
				{
					d = v;
				}
			}
			return d;
		};
	}

	private static Sdf Grow(Sdf s, double by)
	{
		return (double x, double y) => s(x, y) - by;
	}

	// The band of width w just inside the outline of s.
	private static Sdf InnerBand(Sdf s, double w)
	{
		return delegate(double x, double y)
		{
			double d = s(x, y);
			return Math.Max(d, -d - w);
		};
	}

	// ===================================================================== canvas

	private readonly struct Rgb
	{
		public readonly double R;

		public readonly double G;

		public readonly double B;

		private Rgb(double r, double g, double b)
		{
			R = r;
			G = g;
			B = b;
		}

		public static Rgb From(uint rgb)
		{
			return new Rgb(((rgb >> 16) & 0xFF) / 255.0, ((rgb >> 8) & 0xFF) / 255.0, (rgb & 0xFF) / 255.0);
		}
	}

	// Premultiplied float RGBA, composited with "over"; pixel (i, j) is sampled at its centre (i + 0.5, j + 0.5).
	private sealed class Canvas
	{
		public readonly int S;

		public readonly double U;

		// False leaves out the halo outline of the ring pointers.
		public bool Keyline = true;

		private readonly double[] _p;

		public Canvas(int size)
		{
			S = size;
			U = size / 32.0;
			_p = new double[size * size * 4];
		}

		public void Paint(Sdf sdf, uint color, double alpha)
		{
			Rgb c = Rgb.From(color);
			for (int j = 0; j < S; j++)
			{
				double y = j + 0.5;
				for (int i = 0; i < S; i++)
				{
					double a = Cov(sdf(i + 0.5, y)) * alpha;
					if (a <= 0.0)
					{
						continue;
					}
					Over((j * S + i) * 4, c.R * a, c.G * a, c.B * a, a);
				}
			}
		}

		public void PaintCoverage(Func<double, double, double> coverage, uint color)
		{
			Rgb c = Rgb.From(color);
			for (int j = 0; j < S; j++)
			{
				double y = j + 0.5;
				for (int i = 0; i < S; i++)
				{
					double a = coverage(i + 0.5, y);
					if (a <= 0.0)
					{
						continue;
					}
					Over((j * S + i) * 4, c.R * a, c.G * a, c.B * a, a);
				}
			}
		}

		public void PaintPremultiplied(PremulField field)
		{
			for (int j = 0; j < S; j++)
			{
				double y = j + 0.5;
				for (int i = 0; i < S; i++)
				{
					field(i + 0.5, y, out double r, out double g, out double b, out double a);
					if (a <= 0.0)
					{
						continue;
					}
					Over((j * S + i) * 4, r, g, b, a);
				}
			}
		}

		// Destination-over: paints the shape behind what is already there.
		public void PaintUnder(Sdf sdf, uint color, double alpha)
		{
			Rgb c = Rgb.From(color);
			for (int j = 0; j < S; j++)
			{
				double y = j + 0.5;
				for (int i = 0; i < S; i++)
				{
					double a = Cov(sdf(i + 0.5, y)) * alpha;
					if (a <= 0.0)
					{
						continue;
					}
					int o = (j * S + i) * 4;
					double k = 1.0 - _p[o + 3];
					_p[o] += c.R * a * k;
					_p[o + 1] += c.G * a * k;
					_p[o + 2] += c.B * a * k;
					_p[o + 3] += a * k;
				}
			}
		}

		// Destination-out: removes what is already painted under the shape.
		public void Erase(Sdf sdf)
		{
			for (int j = 0; j < S; j++)
			{
				double y = j + 0.5;
				for (int i = 0; i < S; i++)
				{
					double a = Cov(sdf(i + 0.5, y));
					if (a <= 0.0)
					{
						continue;
					}
					int o = (j * S + i) * 4;
					double k = 1.0 - a;
					_p[o] *= k;
					_p[o + 1] *= k;
					_p[o + 2] *= k;
					_p[o + 3] *= k;
				}
			}
		}

		private void Over(int o, double r, double g, double b, double a)
		{
			double k = 1.0 - a;
			_p[o] = r + _p[o] * k;
			_p[o + 1] = g + _p[o + 1] * k;
			_p[o + 2] = b + _p[o + 2] * k;
			_p[o + 3] = a + _p[o + 3] * k;
		}

		public byte[] ToBgra()
		{
			byte[] o = new byte[S * S * 4];
			for (int i = 0; i < S * S; i++)
			{
				int k = i * 4;
				double a = _p[k + 3];
				int ab = (int)Math.Round(Math.Clamp(a, 0.0, 1.0) * 255.0, MidpointRounding.AwayFromZero);
				if (ab <= 0)
				{
					continue;
				}
				double inv = 1.0 / a;
				o[k] = ToByte(_p[k + 2] * inv);
				o[k + 1] = ToByte(_p[k + 1] * inv);
				o[k + 2] = ToByte(_p[k] * inv);
				o[k + 3] = (byte)ab;
			}
			return o;
		}

		private static byte ToByte(double v)
		{
			return (byte)Math.Round(Math.Clamp(v, 0.0, 1.0) * 255.0, MidpointRounding.AwayFromZero);
		}
	}
}
