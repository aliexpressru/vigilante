namespace Vigilante.Models.Enums;

/// <summary>
/// Type of snapshot download operation
/// </summary>
public enum SnapshotDownloadType
{
    /// <summary>
    /// Download snapshot via Qdrant API
    /// </summary>
    Api = 0,
    
    /// <summary>
    /// Download snapshot from disk using kubectl
    /// </summary>
    Disk = 1
}

