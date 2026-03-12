namespace Vigilante.Models.Requests;

public class V1RestoreReplicationFactorRequest
{
    /// <summary>
    /// Collection name to restore replication factor for.
    /// </summary>
    public string CollectionName { get; set; } = string.Empty;

    /// <summary>
    /// Shard transfer method (e.g. Snapshot, StreamRecords, WalDelta). Optional; defaults to Snapshot on the server.
    /// </summary>
    public string? ShardTransferMethod { get; set; }
}
