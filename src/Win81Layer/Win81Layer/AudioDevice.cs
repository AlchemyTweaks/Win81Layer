namespace Win81Layer;

public sealed class AudioDevice
{
	public string Id { get; init; } = "";

	public string Name { get; init; } = "";

	public bool IsDefault { get; init; }

	public override string ToString()
	{
		return Name;
	}
}
