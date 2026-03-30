using Vigilante.Models;

namespace Vigilante.Models.Responses;

public class V1GetCollectionsInfoPaginatedResponse
{
    public CollectionInfo[] Collections { get; set; } = [];
    
    public string[] Issues { get; set; } = [];
    
    public PaginationInfo Pagination { get; set; } = new();

    public class CollectionInfo
    {
        public string PodName { get; set; } = string.Empty;

        public string NodeUrl { get; set; } = string.Empty;

        public string CollectionName { get; set; } = string.Empty;

        public string PeerId { get; set; } = string.Empty;
        
        public string PodNamespace { get; set; } = string.Empty;

        public Dictionary<string, object> Metrics { get; set; } = new();
        
        public List<string> Issues { get; set; } = new();

        public List<string> Warnings { get; set; } = new();

        public List<CollectionOptimizationInfo> RunningOptimizations { get; set; } = new();
        
        public List<string> Aliases { get; set; } = new();
        
        /// <summary>
        /// Collection status from Qdrant API.
        /// Possible values: "Green" (healthy), "Yellow" (optimizing), "Red" (error).
        /// </summary>
        public string? Status { get; set; }
    }
    
    public class PaginationInfo
    {
        public int CurrentPage { get; set; }
        public int PageSize { get; set; }
        public int TotalItems { get; set; }
        public int TotalPages { get; set; }
    }
}

