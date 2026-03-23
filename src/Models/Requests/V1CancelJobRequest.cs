namespace Vigilante.Models.Requests;

public record V1CancelJobRequest
{
    public string Key { get; init; } = string.Empty;
}
