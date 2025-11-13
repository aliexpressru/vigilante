namespace Vigilante.Models.Requests;

public class V1DownloadSnapshotFromDiskRequest
{
    public required string CollectionName { get; set; }
    public required string SnapshotName { get; set; }
    public required string PodName { get; set; }
    public required string PodNamespace { get; set; }
}

