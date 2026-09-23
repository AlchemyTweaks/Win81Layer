using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Win81Layer;

public sealed class NetworkIconSetQa
{
	public int Variants { get; set; }
	public int RenderedSizes { get; set; }
	public int Distinct32PxFingerprints { get; set; }
	public bool CompleteReferenceMatrix { get; set; }
	public bool Exact12_16_32Sizes { get; set; }
	public bool AllDrawingImages { get; set; }
	public bool AllFrozenAndCacheStable { get; set; }
	public bool AllSizesNonBlank { get; set; }
	public bool AllBackgroundsTransparent { get; set; }
	public bool EveryVariantDistinct { get; set; }
	public bool ReferencePalettePresent { get; set; }
	public int WhitePixels { get; set; }
	public int DimmedPixels { get; set; }
	public int ErrorRedPixels { get; set; }
	public int WarningAmberPixels { get; set; }
	public int TransparentPixels { get; set; }
	public long BuildAndRenderMs { get; set; }
	public string AtlasScreenshot { get; set; } = string.Empty;
	public string AtlasSha256 { get; set; } = string.Empty;
}

internal static class NetworkIconSetDiagnostics
{
	private readonly record struct PixelStats(string Hash, int Opaque, int Transparent, int White, int Dimmed, int Red, int Amber);

	internal static NetworkIconSetQa Render(string atlasPath)
	{
		Stopwatch timer = Stopwatch.StartNew();
		HashSet<string> fingerprints = new HashSet<string>(StringComparer.Ordinal);
		int rendered = 0;
		int white = 0;
		int dimmed = 0;
		int red = 0;
		int amber = 0;
		int transparent = 0;
		bool allVectors = true;
		bool allFrozen = true;
		bool allNonBlank = true;
		bool allTransparent = true;

		foreach ((NetworkIconKind kind, NetworkIconState state, int signal) in NetIcons81.QaMatrix)
		{
			ImageSource canonical = NetIcons81.Draw(kind, state, signal, 32);
			ImageSource cached = NetIcons81.Draw(kind, state, signal, 32);
			allVectors &= canonical is DrawingImage;
			allFrozen &= ReferenceEquals(canonical, cached)
				&& canonical.IsFrozen
				&& canonical is DrawingImage drawing
				&& drawing.Drawing.IsFrozen;
			foreach (int size in new[] { 12, 16, 32 })
			{
				ImageSource icon = NetIcons81.Draw(kind, state, signal, size);
				PixelStats stats = RenderPixels(icon, size);
				rendered++;
				white += stats.White;
				dimmed += stats.Dimmed;
				red += stats.Red;
				amber += stats.Amber;
				transparent += stats.Transparent;
				allNonBlank &= stats.Opaque >= Math.Max(3, size * size / 40);
				allTransparent &= stats.Transparent >= size * size / 8;
				if (size == 32)
				{
					fingerprints.Add(stats.Hash);
				}
			}
		}

		RenderAtlas(atlasPath);
		timer.Stop();
		return new NetworkIconSetQa
		{
			Variants = NetIcons81.QaMatrix.Length,
			RenderedSizes = rendered,
			Distinct32PxFingerprints = fingerprints.Count,
			CompleteReferenceMatrix = NetIcons81.QaMatrix.Length >= 49,
			Exact12_16_32Sizes = rendered == NetIcons81.QaMatrix.Length * 3,
			AllDrawingImages = allVectors,
			AllFrozenAndCacheStable = allFrozen,
			AllSizesNonBlank = allNonBlank,
			AllBackgroundsTransparent = allTransparent,
			EveryVariantDistinct = fingerprints.Count == NetIcons81.QaMatrix.Length,
			ReferencePalettePresent = white > 0 && dimmed > 0 && red > 0 && amber > 0,
			WhitePixels = white,
			DimmedPixels = dimmed,
			ErrorRedPixels = red,
			WarningAmberPixels = amber,
			TransparentPixels = transparent,
			BuildAndRenderMs = timer.ElapsedMilliseconds,
			AtlasScreenshot = atlasPath,
			AtlasSha256 = HashFile(atlasPath)
		};
	}

	private static PixelStats RenderPixels(ImageSource image, int size)
	{
		DrawingVisual visual = new DrawingVisual();
		using (DrawingContext dc = visual.RenderOpen())
		{
			dc.DrawImage(image, new Rect(0.0, 0.0, size, size));
		}
		RenderTargetBitmap bitmap = new RenderTargetBitmap(size, size, 96.0, 96.0, PixelFormats.Pbgra32);
		bitmap.Render(visual);
		int stride = size * 4;
		byte[] pixels = new byte[stride * size];
		bitmap.CopyPixels(pixels, stride, 0);
		int opaque = 0;
		int transparent = 0;
		int white = 0;
		int dimmed = 0;
		int red = 0;
		int amber = 0;
		for (int i = 0; i < pixels.Length; i += 4)
		{
			byte b = pixels[i];
			byte g = pixels[i + 1];
			byte r = pixels[i + 2];
			byte a = pixels[i + 3];
			if (a == 0)
			{
				transparent++;
				continue;
			}
			opaque++;
			if (a > 220 && r > 225 && g > 225 && b > 225) white++;
			if (a > 180 && r >= 75 && r <= 215 && Math.Abs(r - g) < 16 && Math.Abs(g - b) < 16) dimmed++;
			if (a > 200 && r > 185 && g < 95 && b < 115) red++;
			if (a > 200 && r > 215 && g > 125 && g < 220 && b < 75) amber++;
		}
		return new PixelStats(Convert.ToHexString(SHA256.HashData(pixels)), opaque, transparent, white, dimmed, red, amber);
	}

	private static void RenderAtlas(string path)
	{
		const int columns = 8;
		const int cellWidth = 124;
		const int cellHeight = 94;
		const int headerHeight = 50;
		int rows = (int)Math.Ceiling(NetIcons81.QaMatrix.Length / (double)columns);
		int width = columns * cellWidth;
		int height = headerHeight + rows * cellHeight;
		DrawingVisual visual = new DrawingVisual();
		using (DrawingContext dc = visual.RenderOpen())
		{
			SolidColorBrush background = FrozenBrush(Color.FromRgb(4, 22, 40));
			SolidColorBrush cellA = FrozenBrush(Color.FromRgb(7, 36, 62));
			SolidColorBrush cellB = FrozenBrush(Color.FromRgb(9, 43, 73));
			SolidColorBrush line = FrozenBrush(Color.FromRgb(39, 91, 127));
			SolidColorBrush text = FrozenBrush(Color.FromRgb(255, 255, 255));
			SolidColorBrush secondary = FrozenBrush(Color.FromRgb(180, 207, 225));
			dc.DrawRectangle(background, null, new Rect(0.0, 0.0, width, height));
			FormattedText title = Text("Windows 8.1 Network Icon Matrix", 20.0, text, FontWeights.SemiBold, width - 24.0);
			dc.DrawText(title, new Point(12.0, 8.0));
			FormattedText subtitle = Text("Production vectors · exact 32 / 16 / 12 px · transparent surfaces · composable state badges", 10.5, secondary, FontWeights.Normal, width - 24.0);
			dc.DrawText(subtitle, new Point(13.0, 32.0));

			for (int index = 0; index < NetIcons81.QaMatrix.Length; index++)
			{
				(NetworkIconKind kind, NetworkIconState state, int signal) = NetIcons81.QaMatrix[index];
				int column = index % columns;
				int row = index / columns;
				double x = column * cellWidth;
				double y = headerHeight + row * cellHeight;
				dc.DrawRectangle(((row + column) & 1) == 0 ? cellA : cellB, new Pen(line, 0.7), new Rect(x + 1.0, y + 1.0, cellWidth - 2.0, cellHeight - 2.0));
				dc.DrawImage(NetIcons81.Draw(kind, state, signal, 32), new Rect(x + 8.0, y + 8.0, 32.0, 32.0));
				dc.DrawImage(NetIcons81.Draw(kind, state, signal, 16), new Rect(x + 52.0, y + 10.0, 16.0, 16.0));
				dc.DrawImage(NetIcons81.Draw(kind, state, signal, 12), new Rect(x + 82.0, y + 12.0, 12.0, 12.0));
				dc.DrawText(Text("32", 8.0, secondary, FontWeights.Normal, 20.0), new Point(x + 14.0, y + 39.0));
				dc.DrawText(Text("16", 8.0, secondary, FontWeights.Normal, 20.0), new Point(x + 52.0, y + 29.0));
				dc.DrawText(Text("12", 8.0, secondary, FontWeights.Normal, 20.0), new Point(x + 81.0, y + 29.0));
				string signalLabel = kind is NetworkIconKind.Wifi or NetworkIconKind.Cellular && state == NetworkIconState.Connected ? $" L{signal}" : string.Empty;
				FormattedText label = Text($"{kind}\n{state}{signalLabel}", 9.5, text, FontWeights.Normal, cellWidth - 12.0);
				label.MaxTextHeight = 37.0;
				dc.DrawText(label, new Point(x + 7.0, y + 52.0));
			}
		}
		RenderTargetBitmap bitmap = new RenderTargetBitmap(width, height, 96.0, 96.0, PixelFormats.Pbgra32);
		bitmap.Render(visual);
		PngBitmapEncoder encoder = new PngBitmapEncoder();
		encoder.Frames.Add(BitmapFrame.Create(bitmap));
		using FileStream stream = File.Create(path);
		encoder.Save(stream);
	}

	private static FormattedText Text(string value, double size, Brush brush, FontWeight weight, double maxWidth)
	{
		FormattedText text = new FormattedText(
			value,
			CultureInfo.InvariantCulture,
			FlowDirection.LeftToRight,
			new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, weight, FontStretches.Normal),
			size,
			brush,
			1.0)
		{
			MaxTextWidth = Math.Max(1.0, maxWidth),
			Trimming = TextTrimming.CharacterEllipsis
		};
		return text;
	}

	private static SolidColorBrush FrozenBrush(Color color)
	{
		SolidColorBrush brush = new SolidColorBrush(color);
		brush.Freeze();
		return brush;
	}

	private static string HashFile(string path)
	{
		return File.Exists(path) ? Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))) : string.Empty;
	}
}
