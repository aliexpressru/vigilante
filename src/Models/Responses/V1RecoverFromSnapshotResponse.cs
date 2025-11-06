namespace Vigilante.Models.Responses;
/// <summary>
/// Response for recover from snapshot operation
/// </summary>
public class V1RecoverFromSnapshotResponse
{
    /// <summary>
    /// Overall operation message
    /// </summary>
    public string Message { get; set; } = string.Empty;
    /// <summary>
    /// Whether the operation was successful
    /// </summary>
    public bool Success { get; set; }
    /// <summary>
    /// Error message if recovery failed
    /// </summary>
    public string? Error { get; set; }
}
