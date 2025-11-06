using Microsoft.AspNetCore.Http;

namespace Vigilante.Models.Requests;

/// <summary>
/// Request for recovering a collection from an uploaded snapshot file
/// </summary>
public class V1RecoverFromUploadedSnapshotRequest
{
    /// <summary>
    /// Node URL where to recover the collection
    /// </summary>
    public string NodeUrl { get; set; } = string.Empty;

    /// <summary>
    /// Name of the collection to recover
    /// </summary>
    public string CollectionName { get; set; } = string.Empty;

    /// <summary>
    /// Snapshot file to upload and recover from
    /// </summary>
    public IFormFile? SnapshotFile { get; set; }
}

