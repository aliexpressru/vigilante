using Vigilante.Extensions;
using Vigilante.Models.Enums;

namespace Vigilante.Models;

public class SnapshotInfo
{
    public required string PodName { get; set; }

    public required string NodeUrl { get; set; }

    public required string PeerId { get; set; }

    public required string CollectionName { get; set; }

    public required string SnapshotName { get; set; }

    public long SizeBytes { get; set; }

    public string PrettySize => SizeBytes.ToPrettySize();
    
    public required string PodNamespace { get; set; }
    
    /// <summary>
    /// Source where this snapshot information was retrieved from
    /// </summary>
    public SnapshotSource Source { get; set; }

    /// <summary>
    /// Snapshot creation date parsed from the snapshot name. Null if the name does not match the expected format.
    /// </summary>
    public DateTime? CreatedAt { get; set; }
}

