using Vigilante.Constants;

namespace Vigilante.Extensions;

/// <summary>
/// Extension methods for consistent node and collection sorting
/// </summary>
internal static class NodeSortingExtensions
{
    /// <summary>
    /// Gets the sort key for a node/collection based on PodName (preferred) or PeerId (fallback).
    /// This ensures consistent sorting across the application.
    /// </summary>
    /// <param name="podName">The pod name</param>
    /// <param name="peerId">The peer ID (fallback if pod name is not available)</param>
    /// <returns>The sort key to use for ordering</returns>
    public static string GetNodeSortKey(string? podName, ulong peerId)
    {
        // Use PodName if it's available and not 'unknown', otherwise use PeerId
        if (!string.IsNullOrEmpty(podName) && podName != MetricConstants.UnknownPodName)
        {
            return podName;
        }

        return peerId.ToString();
    }
}

