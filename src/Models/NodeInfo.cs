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

    public string? Error { get; set; } 
    
    public NodeErrorType ErrorType { get; set; } = NodeErrorType.None;
    
    public string? PodName { get; set; }

    public HashSet<string> CurrentPeerIds { get; set; } = new();
}