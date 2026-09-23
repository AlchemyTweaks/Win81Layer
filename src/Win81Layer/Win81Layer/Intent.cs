namespace Win81Layer;

public sealed record Intent(IntentKind Kind, double Confidence, string? EntityHint);
