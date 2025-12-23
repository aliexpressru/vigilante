namespace Vigilante.Models.Responses;

/// <summary>
/// Represents a page of logs with optional continuation token.
/// </summary>
public class V1LogsPageResponse : BaseOperationResponse
{
    public IReadOnlyList<V1LogEntry> Logs { get; init; } = Array.Empty<V1LogEntry>();

    public string? Continuation { get; init; }

    public bool Truncated { get; init; }
}

public record V1LogEntry(DateTime Timestamp, string Message, string Source);
