namespace Vigilante.Models.Responses;

/// <summary>
/// Base response class with common properties for API operations
/// </summary>
public abstract class BaseOperationResponse
{
    /// <summary>
    /// Whether the operation was successful
    /// </summary>
    public bool Success { get; set; }
    
    /// <summary>
    /// Operation result message
    /// </summary>
    public string Message { get; set; } = string.Empty;
    
    /// <summary>
    /// Error details if operation failed
    /// </summary>
    public string? Error { get; set; }
}

