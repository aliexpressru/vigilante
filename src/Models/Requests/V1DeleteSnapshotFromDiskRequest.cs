namespace Vigilante.Models.Requests;

public class V1DeleteSnapshotFromDiskRequest
{
    public string? PodName { get; set; }
    public string? PodNamespace { get; set; }
    public string? CollectionName { get; set; }
    public string? SnapshotName { get; set; }
}

