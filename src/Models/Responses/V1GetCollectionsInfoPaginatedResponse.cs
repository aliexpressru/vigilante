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
    }
    
    public class PaginationInfo
    {
        public int CurrentPage { get; set; }
        public int PageSize { get; set; }
        public int TotalItems { get; set; }
        public int TotalPages { get; set; }
    }
}

