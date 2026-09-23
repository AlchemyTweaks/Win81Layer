namespace Win81Layer;

internal static class WeatherTileFlipPolicy
{
	// Keep the useful forecast visible most of the time. The logo face is a short, slower brand beat.
	public const int ContentDwellMinMs = 14000;
	public const int ContentDwellMaxMs = 20000;
	public const int LogoDwellMinMs = 5000;
	public const int LogoDwellMaxMs = 8000;

	internal static (int ContentMin, int ContentMax, int LogoMin, int LogoMax) For(LiveKind kind) => kind switch
	{
		LiveKind.Clock or LiveKind.Agenda => (10000, 16000, 4000, 6500),
		LiveKind.News => (16000, 24000, 4000, 7000),
		_ => (ContentDwellMinMs, ContentDwellMaxMs, LogoDwellMinMs, LogoDwellMaxMs)
	};

	public static bool IsValid =>
		ContentDwellMinMs > 9500 &&
		ContentDwellMaxMs > ContentDwellMinMs &&
		LogoDwellMinMs >= 4000 &&
		LogoDwellMaxMs > LogoDwellMinMs;
}
