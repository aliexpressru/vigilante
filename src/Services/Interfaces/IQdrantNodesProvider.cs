using Vigilante.Configuration;

namespace Vigilante.Services.Interfaces;

public interface IQdrantNodesProvider
{
    Task<IReadOnlyList<QdrantNodeConfig>> GetNodesAsync(CancellationToken cancellationToken);
}
