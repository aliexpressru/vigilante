namespace Vigilante.Models.Requests;

public class V1DrainPeerRequest
{
    public required ulong PeerId { get; set; }

    /// <summary>
    /// Collections to drain from the peer. Empty array means drain all collections.
    /// </summary>
    public string[] CollectionNames { get; set; } = [];
}
