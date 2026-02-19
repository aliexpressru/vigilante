using Vigilante.Extensions;

namespace Vigilante.Models;

/// <summary>
/// Detailed information about a shard including ID, state, and size
/// </summary>
public class ShardDetails
{
    public required uint ShardId { get; set; }
    
    public string? State { get; set; }
    
    public long? SizeBytes { get; set; }
    
    public string? PrettySize => SizeBytes?.ToPrettySize();
}
