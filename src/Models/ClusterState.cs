using Vigilante.Models.Enums;

namespace Vigilante.Models;

public class ClusterState
{
    private ClusterStatus? _status;
    
    private ClusterHealth? _health;
    
    public ClusterStatus Status => _status ??= CalculateStatus();

    public ClusterHealth Health => _health ??= CalculateHealth();

    public List<NodeInfo> Nodes { get; set; } = new();

    public DateTime LastUpdated { get; set; }

    /// <summary>
    /// Invalidates cached health and status to force recalculation.
    /// Call this after modifying node warnings or errors.
    /// </summary>
    public void InvalidateCache()
    {
        _health = null;
        _status = null;
    }

    private ClusterStatus CalculateStatus()
    {
        if (Health.HealthyNodes == 0)
        {
            return ClusterStatus.Unavailable;
        }

        if (Health.HealthyNodes < Health.TotalNodes)
        {
            return ClusterStatus.Degraded;
        }
        
        return ClusterStatus.Healthy;
    }

    private ClusterHealth CalculateHealth()
    {
        var health = new ClusterHealth
        {
            TotalNodes = Nodes.Count,
            HealthyNodes = Nodes.Count(n => n.IsHealthy),
            Leader = Nodes.FirstOrDefault(n => n.IsLeader)?.PeerId ?? string.Empty
        };

        health.IsHealthy = health.HealthyNodes == health.TotalNodes;
        var issues = new List<string>();

        if (health.HealthyNodes < health.TotalNodes)
        {
            // Add detailed error information for each unhealthy node
            var unhealthyNodes = Nodes.Where(n => !n.IsHealthy);
            foreach (var node in unhealthyNodes)
            {
                var nodeName = !string.IsNullOrEmpty(node.PodName) ? node.PodName : node.Url;
                var errorInfo = !string.IsNullOrEmpty(node.Error) 
                    ? $"{nodeName}: {node.Error}" 
                    : $"{nodeName}: Unknown error ({node.ErrorType})";
                issues.Add(errorInfo);
            }
        }

        if (string.IsNullOrEmpty(health.Leader))
        {
            issues.Add("No leader elected");
        }
        
        health.Issues = issues;
        
        // Collect warnings separately from all nodes (both healthy and unhealthy)
        var warnings = new List<string>();
        var nodesWithWarnings = Nodes.Where(n => n.Warnings.Count > 0);
        foreach (var node in nodesWithWarnings)
        {
            var nodeName = !string.IsNullOrEmpty(node.PodName) ? node.PodName : node.Url;
            foreach (var warning in node.Warnings)
            {
                warnings.Add($"{nodeName}: {warning}");
            }
        }
        
        health.Warnings = warnings;

        return health;
    }
}