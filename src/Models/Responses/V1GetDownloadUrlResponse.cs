namespace Vigilante.Models.Responses;

/// <summary>
/// Response for getting a presigned download URL
/// </summary>
public class V1GetDownloadUrlResponse
{
    /// <summary>
    /// Indicates if the operation was successful
    /// </summary>
    public bool Success { get; set; }
    
    /// <summary>
    /// Message describing the result
    /// </summary>
    public string Message { get; set; } = string.Empty;
    
    /// <summary>
    /// The presigned download URL (if successful)
    /// </summary>
    public string? Url { get; set; }
}

