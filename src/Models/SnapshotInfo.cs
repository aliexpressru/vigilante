using Vigilante.Extensions;
using Vigilante.Models.Enums;

namespace Vigilante.Models;

public class SnapshotInfo
{
    public string PodName { get; set; }

    public string NodeUrl { get; set; }

    public string PeerId { get; set; }

    public string CollectionName { get; set; }

    public string SnapshotName { get; set; }

    public long SizeBytes { get; set; }

    public string PrettySize => SizeBytes.ToPrettySize();
    
    public string PodNamespace { get; set; }
    
    /// <summary>
    /// Source where this snapshot information was retrieved from
    /// </summary>
    public SnapshotSource Source { get; set; }
}

