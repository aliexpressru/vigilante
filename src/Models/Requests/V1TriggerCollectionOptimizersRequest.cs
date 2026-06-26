namespace Vigilante.Models.Requests;

/// <summary>
/// Request to trigger optimizers for a collection.
/// </summary>
public class V1TriggerCollectionOptimizersRequest
{
    /// <summary>
    /// Name of the collection to trigger optimizers for.
    /// </summary>
    public required string CollectionName { get; set; }
}
