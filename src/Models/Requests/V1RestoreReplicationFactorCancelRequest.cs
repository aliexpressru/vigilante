namespace Vigilante.Models.Requests;

public class V1RestoreReplicationFactorCancelRequest
{
    /// <summary>
    /// Collection name to cancel restore replication factor for.
    /// </summary>
    public string CollectionName { get; set; } = string.Empty;
}
