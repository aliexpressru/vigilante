namespace Vigilante.Models.Requests;

public class V1GetCollectionsInfoRequest
{
    /// <summary>
    /// Page number (1-based)
    /// </summary>
    public int Page { get; set; } = 1;
    
    /// <summary>
    /// Number of items per page
    /// </summary>
    public int PageSize { get; set; } = 20;
    
    /// <summary>
    /// Filter collections by name (case-insensitive partial match)
    /// </summary>
    public string? NameFilter { get; set; }
    
    /// <summary>
    /// Whether to clear cache and force refresh from data source
    /// </summary>
    public bool ClearCache { get; set; } = false;
}

