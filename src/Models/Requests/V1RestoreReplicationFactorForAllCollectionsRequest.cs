namespace Vigilante.Models.Requests;

public class V1RestoreReplicationFactorForAllCollectionsRequest
{
    /// <summary>
    /// Shard transfer method (e.g. Snapshot, StreamRecords, WalDelta). Optional; defaults to Snapshot on the server.
    /// </summary>
    public string? ShardTransferMethod { get; set; }
}