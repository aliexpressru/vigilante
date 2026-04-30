using System.Text.Json.Serialization;
using Vigilante.Models;

namespace Vigilante.Models.Requests;

/// <summary>
/// Request for fetching Vigilante service logs with optional continuation token.
/// </summary>
public class V1GetVigilanteLogsRequest
{
    public string? Namespace { get; set; }

    public int Limit { get; set; } = 200;

    public string? Continuation { get; set; }

    [JsonConverter(typeof(LogLevelFilterJsonConverter))]
    public LogLevelFilter? Levels { get; set; }

    public string? SearchText { get; set; }
}

