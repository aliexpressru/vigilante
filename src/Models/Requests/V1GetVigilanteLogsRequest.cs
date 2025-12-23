namespace Vigilante.Models.Requests;

/// <summary>
/// Request for fetching Vigilante service logs with optional continuation token.
/// </summary>
public class V1GetVigilanteLogsRequest
{
    public string? Namespace { get; set; }

    public int Limit { get; set; } = 200;

    public string? Continuation { get; set; }
}

