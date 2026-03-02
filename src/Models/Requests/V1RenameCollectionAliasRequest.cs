namespace Vigilante.Models.Requests;

/// <summary>
/// Request to rename a collection alias.
/// </summary>
public class V1RenameCollectionAliasRequest
{
    /// <summary>
    /// Current alias name to rename
    /// </summary>
    public required string OldAliasName { get; set; }

    /// <summary>
    /// New alias name
    /// </summary>
    public required string NewAliasName { get; set; }
}
