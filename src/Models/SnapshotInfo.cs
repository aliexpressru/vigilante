using Vigilante.Extensions;

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
}

