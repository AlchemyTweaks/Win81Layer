using System.Collections.Generic;

namespace Win81Layer;

public sealed record EntityCard(IntentKind Kind, string Name, string TypeLabel, string? HeroImageUrl, IReadOnlyList<Fact> Facts, IReadOnlyList<LauncherAction> Actions);
