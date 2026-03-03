namespace Vigilante.Models.Requests;

public class V1RemovePeerRequest
{
    /// <summary>
    /// The identifier of the peer to remove from the cluster.
    /// </summary>
    public ulong PeerId { get; set; }

    /// <summary>
    /// If true, removes the peer even if it has shards/replicas on it.
    /// </summary>
    public bool IsForceDropOperation { get; set; }

    /// <summary>
    /// Operation timeout in seconds. If not set, the default of 30 seconds is used.
    /// </summary>
    public int? TimeoutSeconds { get; set; }
}
