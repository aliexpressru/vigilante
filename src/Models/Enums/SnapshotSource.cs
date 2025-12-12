namespace Vigilante.Models.Enums;

/// <summary>
/// Source where snapshot information was retrieved from
/// </summary>
public enum SnapshotSource
{
    /// <summary>
    /// Snapshot info retrieved from Kubernetes storage (local disk)
    /// </summary>
    KubernetesStorage = 0,

    /// <summary>
    /// Snapshot info retrieved from Qdrant API (may be stored in S3 or local)
    /// </summary>
    QdrantApi = 1,

    /// <summary>
    /// Snapshot info retrieved from S3-compatible storage
    /// </summary>
    S3Storage = 2
}
