namespace Vigilante.Models.Requests;

public class V1RecoverFromDiskSnapshotRequest
{
    public string? NodeUrl { get; set; }
    public string? CollectionName { get; set; }
    public string? SnapshotName { get; set; }
}

