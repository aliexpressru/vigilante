using Vigilante.Models.Enums;

namespace Vigilante.Models;

public class NodeInfo
{
    /// <summary>
    /// The qdrant peer id of this node.
    /// </summary>
    public ulong PeerId { get; set; } = 0;

    /// <summary>
    /// The URI of this node.
    /// </summary>
    public string Url { get; set; } = string.Empty;

    /// <summary>
    /// Browser-reachable URL for this node (used by frontend links/buttons).
    /// </summary>
    public string BrowserUrl { get; set; } = string.Empty;

    /// <summary>
    /// K8s namespace for this node.
    /// </summary>
    public string? Namespace { get; set; }

    /// <summary>
    /// <c>true</c> if this node is a cluster leader.
    /// </summary>
    public bool IsLeader { get; set; }

    /// <summary>
    /// <c>true</c> if this node is healthy.
    /// </summary>
    public bool IsHealthy { get; set; }

    public DateTime LastSeen { get; set; }

    /// <summary>
    /// Issues and errors found on this node
    /// </summary>
    public List<string> Issues { get; set; } = [];

    /// <summary>
    /// Short error message for cluster nodes UI display
    /// </summary>
    public string? ShortError { get; set; }

    /// <summary>
    /// Warning messages that don't indicate critical failures
    /// </summary>
    public List<string> Warnings { get; set; } = [];

    public NodeErrorType ErrorType { get; set; } = NodeErrorType.None;

    public string? PodName { get; set; }

    /// <summary>
    /// The k8s statefulset this node is a part of.
    /// </summary>
    public string? StatefulSetName { get; set; }

    public HashSet<ulong> CurrentPeerIds { get; set; } = [];

    /// <summary>
    /// Qdrant version running on this node
    /// </summary>
    public string? Version { get; set; }

    /// <summary>
    /// Qdrant storage usage and PVC capacity data for this node.
    /// </summary>
    public NodeStorageInfo Storage { get; set; } = new();

    /// <summary>
    /// Qdrant memory usage and pod memory resources for this node.
    /// </summary>
    public NodeMemoryInfo Memory { get; set; } = new();
}
