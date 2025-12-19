namespace Vigilante.Models.Responses;

public class V1GetSnapshotsInfoPaginatedResponse
{
    /// <summary>
    /// Paginated snapshots grouped by collection
    /// </summary>
    public SnapshotCollectionGroup[] GroupedSnapshots { get; set; } = Array.Empty<SnapshotCollectionGroup>();
    
    /// <summary>
    /// Pagination information
    /// </summary>
    public PaginationInfo Pagination { get; set; } = new();

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
        /// Source where snapshot was retrieved from: "KubernetesStorage", "QdrantApi", or "S3Storage"
        /// </summary>
        public string Source { get; set; } = string.Empty;
    }
    
    public class SnapshotCollectionGroup
    {
        /// <summary>
        /// Collection name (e.g., "my-collection~~20251211")
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
    
    public class PaginationInfo
    {
        /// <summary>
        /// Current page number (1-based)
        /// </summary>
        public int CurrentPage { get; set; }
        
        /// <summary>
        /// Number of items per page
        /// </summary>
        public int PageSize { get; set; }
        
        /// <summary>
        /// Total number of collection groups (not individual snapshots)
        /// </summary>
        public int TotalItems { get; set; }
        
        /// <summary>
        /// Total number of pages
        /// </summary>
        public int TotalPages { get; set; }
    }
}

