using Vigilante.Models;

namespace Vigilante.Models.Requests;

/// <summary>
/// Request for fetching Qdrant pod logs with optional continuation token.
/// </summary>
public class V1GetQdrantLogsRequest
{
    public required string PodName { get; set; }

    public string? Namespace { get; set; }

    public int Limit { get; set; } = 200;

    public string? Continuation { get; set; }

    public LogLevelFilter? Levels { get; set; }

    public string? SearchText { get; set; }
}