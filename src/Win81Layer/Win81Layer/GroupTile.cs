using System.Collections.Generic;

namespace Win81Layer;

public sealed class GroupTile : PinItem
{
	public List<PinnedTile> Members { get; } = new List<PinnedTile>();
}
