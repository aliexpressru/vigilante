namespace Vigilante.Models;

/// <summary>
/// Dynamic configuration that can be changed on the fly
/// Stored in Kubernetes Endpoints annotations
/// </summary>
public class DynamicConfig : IEquatable<DynamicConfig>
{
    public int MonitoringIntervalSeconds { get; set; } = 120;

    /// <summary>
    /// Snapshot automation and lifecycle settings
    /// </summary>
    public SnapshotConfiguration Snapshot { get; set; } = new();

    /// <summary>
    /// S3 storage settings (non-secret fields only; secrets come from environment variables)
    /// </summary>
    public S3DynamicConfig S3 { get; set; } = new();

    public bool Equals(DynamicConfig? other)
    {
        if (other is null) return false;
        if (ReferenceEquals(this, other)) return true;
        return MonitoringIntervalSeconds == other.MonitoringIntervalSeconds;
    }

    public override bool Equals(object? obj)
    {
        return Equals(obj as DynamicConfig);
    }

    public override int GetHashCode()
    {
        // ReSharper disable once NonReadonlyMemberInGetHashCode
        return MonitoringIntervalSeconds.GetHashCode();
    }

    public static bool operator ==(DynamicConfig? left, DynamicConfig? right)
    {
        return Equals(left, right);
    }

    public static bool operator !=(DynamicConfig? left, DynamicConfig? right)
    {
        return !Equals(left, right);
    }
}
