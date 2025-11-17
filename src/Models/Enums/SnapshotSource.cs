namespace Vigilante.Models.Enums;

/// <summary>
/// Source where snapshot information was retrieved from
/// </summary>
public enum SnapshotSource
{
    /// <summary>
    /// Snapshot info retrieved from Kubernetes storage (disk)
    /// </summary>
    KubernetesStorage = 0,
    
    /// <summary>
    /// Snapshot info retrieved from Qdrant API (may be stored in S3)
    /// </summary>
    QdrantApi = 1
}

