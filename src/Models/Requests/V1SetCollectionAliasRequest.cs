namespace Vigilante.Models.Requests;

/// <summary>
/// Request to set (add) an alias for a collection.
/// If the alias already exists for this collection, the operation is idempotent.
/// If the alias exists for another collection, it is reassigned to this collection.
/// </summary>
public class V1SetCollectionAliasRequest
{
    /// <summary>
    /// Name of the collection to add the alias to
    /// </summary>
    public required string CollectionName { get; set; }

    /// <summary>
    /// Alias name to set. The collection can have multiple aliases; this adds one more.
    /// </summary>
    public required string AliasName { get; set; }
}
