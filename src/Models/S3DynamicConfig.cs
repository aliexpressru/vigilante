namespace Vigilante.Models;

/// <summary>
/// S3 settings managed via dynamic configuration (overrides appsettings for non-secret fields)
/// </summary>
public class S3DynamicConfig
{
    /// <summary>
    /// Whether S3 snapshot storage is enabled. Defaults to true.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// S3 bucket name for snapshots.
    /// </summary>
    public string? BucketName { get; set; }

    /// <summary>
    /// S3 region. Defaults to "default".
    /// </summary>
    public string Region { get; set; } = "default";
}
