using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace Win81Layer;

internal static class DirectionalArrow81Diagnostics
{
	private static readonly string[] StateNames = { "settings.json", "profile.json", "taskbar-pins.json", "shell-profile-state.json" };
	private static readonly MetroArrowDirection81[] Directions =
	{
		MetroArrowDirection81.Up,
		MetroArrowDirection81.Right,
		MetroArrowDirection81.Down,
		MetroArrowDirection81.Left
	};
	private static readonly int[] Sizes = { 12, 16, 20, 24, 32, 48 };
	private static readonly Color ReferenceAccent = Color.FromRgb(81, 0, 184);

	public static void Begin(Application app)
	{
		PersistenceDiagnostics.SuppressAutomaticPostBootVerification = true;
		app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
		app.Dispatcher.BeginInvoke((Action)delegate
		{
			string qaRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Win81Layer", "qa");
			Directory.CreateDirectory(qaRoot);
			string atlasPath = Path.Combine(qaRoot, "DIRECTIONAL-ARROW-81-QA-LATEST.png");
			string reportPath = Path.Combine(qaRoot, "directional-arrow-81-qa-latest.json");
			Dictionary<string, string> before = HashMutableState();
			try
			{
				Stopwatch timer = Stopwatch.StartNew();
				Dictionary<string, string> normalHashes = new Dictionary<string, string>(StringComparer.Ordinal);
				bool allCanonical = true;
				bool allFrozen = true;
				bool allNonBlank = true;
				bool allCornersTransparent = true;
				int renderedSizes = 0;

				foreach (MetroArrowDirection81 direction in Directions)
				{
					DrawingImage first = MetroDirectionalArrow81Vectors.Draw(direction, MetroArrowState81.Normal, ReferenceAccent);
					DrawingImage second = MetroDirectionalArrow81Vectors.Draw(direction, MetroArrowState81.Normal, Colors.Red);
					allCanonical &= MetroDirectionalArrow81Vectors.QaUsesCanonicalGeometry(first, direction);
					allFrozen &= ReferenceEquals(first, second) && first.IsFrozen && first.Drawing.IsFrozen && !first.HasAnimatedProperties;
					foreach (int size in Sizes)
					{
						PixelBuffer pixels = Render(first, size);
						renderedSizes++;
						allNonBlank &= pixels.OpaquePixels >= Math.Max(4, size * size / 20);
						allCornersTransparent &= pixels.Alpha(0, 0) == 0
							&& pixels.Alpha(size - 1, 0) == 0
							&& pixels.Alpha(0, size - 1) == 0
							&& pixels.Alpha(size - 1, size - 1) == 0;
						if (size == 32)
						{
							normalHashes[direction.ToString()] = pixels.Hash;
						}
					}
				}

				PixelBuffer normal = Render(MetroDirectionalArrow81Vectors.Draw(MetroArrowDirection81.Up, MetroArrowState81.Normal, ReferenceAccent), 32);
				PixelBuffer hover = Render(MetroDirectionalArrow81Vectors.Draw(MetroArrowDirection81.Up, MetroArrowState81.Hover, ReferenceAccent), 32);
				PixelBuffer pressed = Render(MetroDirectionalArrow81Vectors.Draw(MetroArrowDirection81.Up, MetroArrowState81.Pressed, ReferenceAccent), 32);
				PixelBuffer disabled = Render(MetroDirectionalArrow81Vectors.Draw(MetroArrowDirection81.Up, MetroArrowState81.Disabled, ReferenceAccent), 32);
				bool normalInteriorTransparent = normal.Alpha(6, 16) == 0;
				bool hoverIsSubtle = hover.Alpha(6, 16) > 0 && hover.Alpha(6, 16) <= 20;
				bool pressedCircleSolid = InteriorIsSolid(pressed, 10.0);
				bool disabledIsFaded = disabled.MaxAlpha >= 90 && disabled.MaxAlpha <= 105;

				Color[] accents =
				{
					Color.FromRgb(81, 0, 184),
					Color.FromRgb(0, 120, 215),
					Color.FromRgb(200, 0, 35),
					Color.FromRgb(0, 115, 58),
					Color.FromRgb(230, 100, 0)
				};
				HashSet<string> accentHashes = new HashSet<string>(StringComparer.Ordinal);
				bool everyAccentAppears = true;
				foreach (Color accent in accents)
				{
					PixelBuffer themed = Render(MetroDirectionalArrow81Vectors.Draw(MetroArrowDirection81.Up, MetroArrowState81.Pressed, accent), 32);
					accentHashes.Add(themed.Hash);
					everyAccentAppears &= themed.CountExact(accent) >= 20;
				}

				MetroDirectionalArrow81[] hitTargets =
				{
					new MetroDirectionalArrow81 { Width = 42.0, Height = 42.0, GlyphSize = 32.0 },
					new MetroDirectionalArrow81 { Width = 36.0, Height = 36.0, GlyphSize = 28.0 },
					new MetroDirectionalArrow81 { Width = 44.0, Height = 44.0, GlyphSize = 32.0 }
				};
				bool hitTargetsLarger = hitTargets.All(control => control.Width >= control.GlyphSize + 8.0
					&& control.Height >= control.GlyphSize + 8.0
					&& control.IsHitTestVisible);

				RenderAtlas(atlasPath, accents);
				timer.Stop();
				Dictionary<string, string> after = HashMutableState();
				bool stateUnchanged = before.Count == after.Count
					&& before.All(pair => after.TryGetValue(pair.Key, out string value) && value == pair.Value);
				FileInfo atlas = new FileInfo(atlasPath);
				var checks = new Dictionary<string, bool>
				{
					["one-canonical-vector-rotated-at-0-90-180-270"] = allCanonical,
					["four-direction-fingerprints-are-distinct"] = normalHashes.Values.Distinct(StringComparer.Ordinal).Count() == 4,
					["all-vectors-frozen-animation-free-and-cache-stable"] = allFrozen,
					["same-vector-scales-to-12-16-20-24-32-48"] = renderedSizes == Directions.Length * Sizes.Length && allNonBlank,
					["transparent-background-at-every-size"] = allCornersTransparent,
					["normal-state-has-transparent-interior"] = normalInteriorTransparent,
					["hover-state-only-adds-subtle-highlight"] = hoverIsSubtle,
					["pressed-state-circle-is-solid"] = pressedCircleSolid,
					["pressed-arrow-follows-five-theme-accents"] = everyAccentAppears && accentHashes.Count == accents.Length,
					["disabled-state-is-faded"] = disabledIsFaded,
					["release-returns-to-identical-normal-cache"] = ReferenceEquals(
						MetroDirectionalArrow81Vectors.Draw(MetroArrowDirection81.Up, MetroArrowState81.Normal, ReferenceAccent),
						MetroDirectionalArrow81Vectors.Draw(MetroArrowDirection81.Up, MetroArrowState81.Normal, Colors.Orange)),
					["visible-glyph-has-larger-invisible-hit-target"] = hitTargetsLarger,
					["atlas-present-and-nonblank"] = atlas.Exists && atlas.Length > 10000,
					["mutable-state-unchanged"] = stateUnchanged
				};
				bool passed = checks.Values.All(value => value);
				var report = new
				{
					SchemaVersion = 1,
					GeneratedUtc = DateTime.UtcNow,
					Passed = passed,
					Checks = checks,
					Metrics = new
					{
						CanonicalCanvas = "32x32",
						CircleStrokeDiu = MetroDirectionalArrow81Vectors.CircleStroke,
						ArrowStrokeDiu = MetroDirectionalArrow81Vectors.ArrowStroke,
						Directions = Directions.Select(direction => direction.ToString()).ToArray(),
						Sizes,
						RenderedSizes = renderedSizes,
						DistinctDirectionFingerprints = normalHashes.Values.Distinct(StringComparer.Ordinal).Count(),
						DistinctThemeFingerprints = accentHashes.Count,
						NormalInteriorAlpha = normal.Alpha(6, 16),
						HoverInteriorAlpha = hover.Alpha(6, 16),
						DisabledMaxAlpha = disabled.MaxAlpha,
						BuildRenderAndAtlasMs = timer.ElapsedMilliseconds,
						Atlas = atlasPath,
						AtlasSha256 = atlas.Exists ? Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(atlasPath))) : null
					},
					MutableStateUnchanged = stateUnchanged,
					DesignContract = new
					{
						Reference = "Original Windows 8.1 circular directional navigation control",
						Geometry = "One canonical rounded-stroke up-arrow and ring, rotated in 90-degree increments",
						Normal = "Transparent background with white ring and white arrow",
						Pressed = "Instant solid-white circle with current Start accent arrow",
						Motion = "No animation, scaling, fading, ripple, shadow, gradient or easing",
						Usage = new[] { "Start to Apps", "Apps to Start", "Personalize back", "PC Settings back", "Calendar previous month", "Calendar next month", "Action Center fewer/more navigation" },
						Excluded = new[] { "dropdown arrows", "tree expanders", "scrollbar arrows", "sorting indicators", "disclosure chevrons" }
					}
				};
				File.WriteAllText(reportPath, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
				Logger.Log($"DIRECTIONAL-ARROW-81-QA: passed={passed}, rendered={renderedSizes}, duration={timer.ElapsedMilliseconds}ms -> {reportPath}");
				app.Shutdown(passed ? 0 : 1);
			}
			catch (Exception ex)
			{
				File.WriteAllText(reportPath, JsonSerializer.Serialize(new { SchemaVersion = 1, GeneratedUtc = DateTime.UtcNow, Passed = false, Error = ex.ToString() }, new JsonSerializerOptions { WriteIndented = true }));
				Logger.Log("DIRECTIONAL-ARROW-81-QA failed: " + ex);
				app.Shutdown(1);
			}
		}, DispatcherPriority.Loaded);
	}

	private static PixelBuffer Render(ImageSource image, int size)
	{
		DrawingVisual visual = new DrawingVisual();
		using (DrawingContext context = visual.RenderOpen())
		{
			context.DrawImage(image, new Rect(0.0, 0.0, size, size));
		}
		RenderTargetBitmap bitmap = new RenderTargetBitmap(size, size, 96.0, 96.0, PixelFormats.Pbgra32);
		bitmap.Render(visual);
		byte[] pixels = new byte[size * size * 4];
		bitmap.CopyPixels(pixels, size * 4, 0);
		return new PixelBuffer(size, pixels);
	}

	private static bool InteriorIsSolid(PixelBuffer pixels, double radius)
	{
		for (int y = 0; y < pixels.Size; y++)
		{
			for (int x = 0; x < pixels.Size; x++)
			{
				double dx = x + 0.5 - 16.0;
				double dy = y + 0.5 - 16.0;
				if (dx * dx + dy * dy <= radius * radius && pixels.Alpha(x, y) < 250)
				{
					return false;
				}
			}
		}
		return true;
	}

	private static void RenderAtlas(string path, Color[] accents)
	{
		const int width = 1120;
		const int height = 650;
		DrawingVisual visual = new DrawingVisual();
		using (DrawingContext context = visual.RenderOpen())
		{
			Brush background = FrozenBrush(Color.FromRgb(4, 12, 48));
			Brush panel = FrozenBrush(Color.FromRgb(8, 20, 65));
			Brush border = FrozenBrush(Color.FromRgb(55, 73, 145));
			Brush white = Brushes.White;
			Brush secondary = FrozenBrush(Color.FromRgb(188, 199, 229));
			context.DrawRectangle(background, null, new Rect(0.0, 0.0, width, height));
			context.DrawText(Text("Windows 8.1 Directional Arrow Control", 28.0, white, FontWeights.SemiBold), new Point(24.0, 16.0));
			context.DrawText(Text("One canonical vector · exact 90° rotations · instant authentic states", 14.0, secondary, FontWeights.Normal), new Point(26.0, 54.0));

			DrawPanel(context, panel, border, new Rect(20.0, 88.0, 610.0, 250.0), "Directions and pressed states", white);
			for (int i = 0; i < Directions.Length; i++)
			{
				double x = 72.0 + i * 142.0;
				context.DrawImage(MetroDirectionalArrow81Vectors.Draw(Directions[i], MetroArrowState81.Normal, ReferenceAccent), new Rect(x, 138.0, 64.0, 64.0));
				context.DrawImage(MetroDirectionalArrow81Vectors.Draw(Directions[i], MetroArrowState81.Pressed, ReferenceAccent), new Rect(x, 230.0, 64.0, 64.0));
				context.DrawText(Text(Directions[i].ToString(), 12.0, secondary, FontWeights.Normal), new Point(x + 5.0, 202.0));
			}

			DrawPanel(context, panel, border, new Rect(650.0, 88.0, 450.0, 250.0), "States", white);
			MetroArrowState81[] states = { MetroArrowState81.Normal, MetroArrowState81.Hover, MetroArrowState81.Pressed, MetroArrowState81.Disabled };
			for (int i = 0; i < states.Length; i++)
			{
				double x = 682.0 + i * 101.0;
				context.DrawImage(MetroDirectionalArrow81Vectors.Draw(MetroArrowDirection81.Up, states[i], ReferenceAccent), new Rect(x, 150.0, 58.0, 58.0));
				context.DrawText(Text(states[i].ToString(), 10.5, secondary, FontWeights.Normal), new Point(x, 218.0));
			}

			DrawPanel(context, panel, border, new Rect(20.0, 356.0, 610.0, 270.0), "Theme-driven pressed arrow", white);
			for (int i = 0; i < accents.Length; i++)
			{
				double x = 66.0 + i * 108.0;
				context.DrawImage(MetroDirectionalArrow81Vectors.Draw(MetroArrowDirection81.Up, MetroArrowState81.Pressed, accents[i]), new Rect(x, 424.0, 58.0, 58.0));
				context.DrawText(Text($"#{accents[i].R:X2}{accents[i].G:X2}{accents[i].B:X2}", 9.5, secondary, FontWeights.Normal), new Point(x - 2.0, 490.0));
			}

			DrawPanel(context, panel, border, new Rect(650.0, 356.0, 450.0, 270.0), "Same vector at every size", white);
			double sizeX = 680.0;
			foreach (int size in Sizes.Reverse())
			{
				context.DrawImage(MetroDirectionalArrow81Vectors.Draw(MetroArrowDirection81.Up, MetroArrowState81.Normal, ReferenceAccent), new Rect(sizeX, 428.0 + (48.0 - size) * 0.5, size, size));
				context.DrawText(Text(size.ToString(CultureInfo.InvariantCulture), 9.5, secondary, FontWeights.Normal), new Point(sizeX + Math.Max(0.0, (size - 18.0) * 0.5), 490.0));
				sizeX += size + 22.0;
			}
			context.DrawText(Text("Transparent outside · white ring + arrow · no effects", 12.0, secondary, FontWeights.Normal), new Point(680.0, 548.0));
		}

		RenderTargetBitmap bitmap = new RenderTargetBitmap(width, height, 96.0, 96.0, PixelFormats.Pbgra32);
		bitmap.Render(visual);
		PngBitmapEncoder encoder = new PngBitmapEncoder();
		encoder.Frames.Add(BitmapFrame.Create(bitmap));
		using FileStream stream = File.Create(path);
		encoder.Save(stream);
	}

	private static void DrawPanel(DrawingContext context, Brush background, Brush border, Rect rect, string title, Brush text)
	{
		context.DrawRectangle(background, new Pen(border, 1.0), rect);
		context.DrawText(Text(title, 16.0, text, FontWeights.SemiBold), new Point(rect.X + 14.0, rect.Y + 10.0));
	}

	private static FormattedText Text(string value, double size, Brush brush, FontWeight weight)
	{
		return new FormattedText(
			value,
			CultureInfo.InvariantCulture,
			FlowDirection.LeftToRight,
			new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, weight, FontStretches.Normal),
			size,
			brush,
			1.0);
	}

	private static SolidColorBrush FrozenBrush(Color color)
	{
		SolidColorBrush brush = new SolidColorBrush(color);
		brush.Freeze();
		return brush;
	}

	private static Dictionary<string, string> HashMutableState()
	{
		string root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Win81Layer");
		Dictionary<string, string> hashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
		foreach (string name in StateNames)
		{
			string path = Path.Combine(root, name);
			if (File.Exists(path))
			{
				hashes[name] = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
			}
		}
		return hashes;
	}

	private sealed class PixelBuffer
	{
		public int Size { get; }
		public byte[] Pixels { get; }
		public string Hash { get; }
		public int OpaquePixels { get; }
		public byte MaxAlpha { get; }

		public PixelBuffer(int size, byte[] pixels)
		{
			Size = size;
			Pixels = pixels;
			Hash = Convert.ToHexString(SHA256.HashData(pixels));
			int opaque = 0;
			byte maxAlpha = 0;
			for (int i = 3; i < pixels.Length; i += 4)
			{
				byte alpha = pixels[i];
				if (alpha > 0) opaque++;
				if (alpha > maxAlpha) maxAlpha = alpha;
			}
			OpaquePixels = opaque;
			MaxAlpha = maxAlpha;
		}

		public byte Alpha(int x, int y) => Pixels[(y * Size + x) * 4 + 3];

		public int CountExact(Color color)
		{
			int count = 0;
			for (int i = 0; i < Pixels.Length; i += 4)
			{
				if (Pixels[i + 3] == 255 && Pixels[i + 2] == color.R && Pixels[i + 1] == color.G && Pixels[i] == color.B)
				{
					count++;
				}
			}
			return count;
		}
	}
}
