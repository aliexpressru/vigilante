using System.Diagnostics.CodeAnalysis;
using Vigilante.Models.Enums;

namespace Vigilante.Models;

public class ClusterState
{
    private ClusterStatus? _status;

    public ClusterStatus Status => _status ??= CalculateStatus();

    [AllowNull]
    public ClusterHealth Health
    {
        get => field ??= CalculateHealth();
        private set;
    }

    public List<NodeInfo> Nodes { get; set; } = [];

    public DateTime LastUpdated { get; set; }

    /// <summary>
    /// StatefulSet name for the Qdrant cluster
    /// </summary>
    public string? StatefulSetName { get; set; }

    /// <summary>
    /// Invalidates cached health and status to force recalculation.
    /// Call this after modifying node warnings or errors.
    /// </summary>
    public void InvalidateCache()
    {
        Health = null;
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
            Leader = Nodes.FirstOrDefault(n => n.IsLeader)?.PeerId ?? 0
        };

        health.IsHealthy = health.HealthyNodes == health.TotalNodes;
        var issues = new List<string>();

        // Add issues from all nodes (both healthy and unhealthy)
        var nodesWithIssues = Nodes.Where(n => n.Issues.Count > 0);
        foreach (var node in nodesWithIssues)
        {
            var nodeName = !string.IsNullOrEmpty(node.PodName) ? node.PodName : node.Url;
            foreach (var issue in node.Issues)
            {
                if (string.IsNullOrWhiteSpace(issue))
                {
                    continue;
                }

                issues.Add($"{nodeName}: {issue.Trim()}");
            }
        }

        if (health.Leader == 0)
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
                if (string.IsNullOrWhiteSpace(warning))
                {
                    continue;
                }

                warnings.Add($"{nodeName}: {warning.Trim()}");
            }
        }

        health.Warnings = warnings;

        return health;
    }
}
