namespace Vigilante.Models.Enums;

/// <summary>
/// Type of collection deletion operation
/// </summary>
public enum CollectionDeletionType
{
    /// <summary>
    /// Delete collection via Qdrant API
    /// </summary>
    Api = 0,
    
    /// <summary>
    /// Delete collection from disk using kubectl
    /// </summary>
    Disk = 1
}

