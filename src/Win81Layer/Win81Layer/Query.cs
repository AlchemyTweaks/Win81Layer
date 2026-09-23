namespace Win81Layer;

public readonly record struct Query(string Raw, string Normalized)
{
	public bool IsEmpty => Normalized.Length == 0;

	public static Query Of(string raw)
	{
		if (raw == null)
		{
			raw = "";
		}
		return new Query(raw.Trim(), raw.Trim().ToLowerInvariant());
	}
}
