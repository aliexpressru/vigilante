namespace Vigilante.Models.Responses;

public class V1GetCollectionsInfoResponse
{
    public CollectionInfo[] Collections { get; set; } = [];
    
    public string[] Issues { get; set; } = [];

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
}