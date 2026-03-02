namespace Vigilante.Models.Requests;

/// <summary>
/// Request to delete a collection alias.
/// </summary>
public class V1DeleteCollectionAliasRequest
{
    /// <summary>
    /// Alias name to delete
    /// </summary>
    public required string AliasName { get; set; }
}
