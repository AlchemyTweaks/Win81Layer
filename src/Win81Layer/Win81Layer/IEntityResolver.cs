using System.Threading;
using System.Threading.Tasks;

namespace Win81Layer;

public interface IEntityResolver
{
	IntentKind Kind { get; }

	Task<EntityCard?> ResolveEntityAsync(Query q, Intent intent, CancellationToken ct);
}
