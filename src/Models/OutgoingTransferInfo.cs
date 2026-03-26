namespace Vigilante.Models;

public sealed class OutgoingTransferInfo
{
    public ulong ShardId { get; set; }

    public string To { get; set; } = string.Empty;

    public string ToPeerId { get; set; } = string.Empty;

    public bool IsSync { get; set; }

    public string Method { get; set; } = string.Empty;
}