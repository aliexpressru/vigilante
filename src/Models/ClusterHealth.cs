namespace Vigilante.Models;

public class ClusterHealth
{
    public bool IsHealthy { get; set; }

    public int TotalNodes { get; set; }

    public int HealthyNodes { get; set; }

    public string Leader { get; set; } = string.Empty;

    public List<string> Issues { get; set; } = new();

    public List<string> Warnings { get; set; } = new();

    public double HealthPercentage => TotalNodes > 0 ? (double)HealthyNodes / TotalNodes * 100 : 0;

    public string StatusDescription => IsHealthy ? "All systems operational" : "Cluster degraded";
}