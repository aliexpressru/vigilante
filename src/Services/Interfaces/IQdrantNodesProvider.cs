using Vigilante.Configuration;
using Vigilante.Models;

namespace Vigilante.Services.Interfaces;

public interface IQdrantNodesProvider
{
    Task<IReadOnlyList<QdrantNodeConfig>> GetNodesAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Builds a list of NodeInfo with peer IDs for all discovered nodes
    /// </summary>
    Task<List<NodeInfo>> BuildNodeInfoListAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Gets basic node information including peer ID for a single node configuration
    /// </summary>
    Task<NodeInfo> GetBasicNodeInfoAsync(QdrantNodeConfig nodeConfig, CancellationToken cancellationToken);

    /// <summary>
    /// Gets the peer ID for a specific node
    /// </summary>
    Task<string?> GetPeerIdForNodeAsync(string nodeUrl, QdrantNodeConfig nodeConfig, CancellationToken cancellationToken);
}
