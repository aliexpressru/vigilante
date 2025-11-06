namespace Vigilante.Models.Responses;

public class V1GetSnapshotsInfoResponse
{
    public SnapshotInfoDto[] Snapshots { get; set; } = Array.Empty<SnapshotInfoDto>();

    public class SnapshotInfoDto
    {
        public string PodName { get; set; } = string.Empty;
        public string NodeUrl { get; set; } = string.Empty;
        public string PeerId { get; set; } = string.Empty;
        public string CollectionName { get; set; } = string.Empty;
        public string SnapshotName { get; set; } = string.Empty;
        public long SizeBytes { get; set; }
        public string PrettySize { get; set; } = string.Empty;
        public string PodNamespace { get; set; } = string.Empty;
    }
}

