using Vigilante.Extensions;

namespace Vigilante.Models;

public class CollectionSize
{
    public required string PodName { get; set; }

    public required string NodeUrl { get; set; }

    public required string PeerId { get; set; }

    public required string CollectionName { get; set; }

    public long SizeBytes { get; set; }

    public string PrettySize => SizeBytes.ToPrettySize();
}