namespace Vigilante.Constants;

/// <summary>
/// Warning messages for collection-level warning badges and tooltips.
/// </summary>
public static class CollectionWarningConstants
{
    public const string NonActiveShardsWarning = "Shards not in Active state";
    public const string ActiveTransfersWarning = "Active shard transfers in progress";
    public const string RunningOptimizationsPrefix = "Running optimizations";
    public const string OptimizerWithSegmentsAndProgressFormat = "{0} ({1} segments, {2}/{3})";
    public const string OptimizerWithSegmentsFormat = "{0} ({1} segments)";
}
