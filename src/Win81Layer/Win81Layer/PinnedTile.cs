using System;

namespace Win81Layer;

public sealed class PinnedTile : PinItem
{
	public string LaunchPath { get; init; } = "";

	public string? Args { get; init; }

	public string? ExePath { get; init; }

	public string? IconPath { get; init; }

	public string? AumidExplicit { get; init; }

	public string Key => (ExePath ?? LaunchPath).ToLowerInvariant();

	public string? Aumid
	{
		get
		{
			if (!string.IsNullOrEmpty(AumidExplicit))
			{
				return AumidExplicit;
			}
			string launchPath = LaunchPath;
			object result;
			if (launchPath == null || !launchPath.StartsWith("shell:AppsFolder\\", StringComparison.OrdinalIgnoreCase))
			{
				result = null;
			}
			else
			{
				string launchPath2 = LaunchPath;
				int length = "shell:AppsFolder\\".Length;
				result = launchPath2.Substring(length, launchPath2.Length - length);
			}
			return (string?)result;
		}
	}
}
