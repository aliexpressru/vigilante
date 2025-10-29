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
            issues.Add($"{health.TotalNodes - health.HealthyNodes} nodes are unhealthy");
        }

        if (string.IsNullOrEmpty(health.Leader))
        {
            issues.Add("No leader elected");
        }

        health.Issues = issues;

        return health;
    }
}