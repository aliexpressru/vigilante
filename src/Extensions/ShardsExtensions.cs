using Vigilante.Models;

namespace Vigilante.Extensions;

internal static class ShardsExtensions
{
    extension(IReadOnlyList<ShardDetails>? shards)
    {
        public string BuildShardsFingerprint()
        {
            if (shards is not { Count: > 0 })
            {
                return "no-shards";
            }

            return string.Join(
                "|",
                shards.OrderBy(s => s.ShardId).Select(s => $"{s.ShardId}:{s.VectorsSizeBytes}:{s.PayloadsSizeBytes}")
            );
        }
    }
}
