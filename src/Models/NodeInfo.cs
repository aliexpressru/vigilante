using Vigilante.Models.Enums;

namespace Vigilante.Models;

public class NodeInfo
{
    public string PeerId { get; set; } = string.Empty;

    public string Url { get; set; } = string.Empty;
    
    public string? Namespace { get; set; }

    public bool IsLeader { get; set; }

    public bool IsHealthy { get; set; }

    public DateTime LastSeen { get; set; }

    /// <summary>
    /// Issues and errors found on this node
    /// </summary>
    public List<string> Issues { get; set; } = new();
    
    /// <summary>
    /// Short error message for cluster nodes UI display
    /// </summary>
    public string? ShortError { get; set; }
    
    /// <summary>
    /// Warning messages that don't indicate critical failures
    /// </summary>
    public List<string> Warnings { get; set; } = new();
    
    public NodeErrorType ErrorType { get; set; } = NodeErrorType.None;
    
    public string? PodName { get; set; }
    
    public string? StatefulSetName { get; set; }

    public HashSet<string> CurrentPeerIds { get; set; } = new();
    
    /// <summary>
    /// Qdrant version running on this node
    /// </summary>
    public string? Version { get; set; }
}