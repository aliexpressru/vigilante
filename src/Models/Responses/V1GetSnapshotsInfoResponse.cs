namespace Vigilante.Models.Responses;

public class V1GetSnapshotsInfoResponse
{
    /// <summary>
    /// Flat list of all snapshots (for backward compatibility)
    /// </summary>
    public SnapshotInfoDto[] Snapshots { get; set; } = Array.Empty<SnapshotInfoDto>();
    
    /// <summary>
    /// Snapshots grouped by collection name
    /// </summary>
    public SnapshotCollectionGroup[] GroupedSnapshots { get; set; } = Array.Empty<SnapshotCollectionGroup>();

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
        
        /// <summary>
        /// Source where snapshot was retrieved from: "KubernetesStorage" or "QdrantApi"
        /// </summary>
        public string Source { get; set; } = string.Empty;
    }
    
    public class SnapshotCollectionGroup
    {
        /// <summary>
        /// Collection name (e.g., "monetization.recs_vectors_with_item_segment_data_v3~~202512110748")
        /// </summary>
        public string CollectionName { get; set; } = string.Empty;
        
        /// <summary>
        /// Total size of all snapshots for this collection
        /// </summary>
        public long TotalSize { get; set; }
        
        /// <summary>
        /// Formatted total size
        /// </summary>
        public string PrettyTotalSize { get; set; } = string.Empty;
        
        /// <summary>
        /// All snapshots for this collection
        /// </summary>
        public SnapshotInfoDto[] Snapshots { get; set; } = Array.Empty<SnapshotInfoDto>();
    }
}