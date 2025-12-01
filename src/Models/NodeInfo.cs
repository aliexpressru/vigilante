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
    /// Detailed error message for Issues section
    /// </summary>
    public string? Error { get; set; }
    
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
}