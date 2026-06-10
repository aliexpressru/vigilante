namespace Vigilante.Configuration;

public class QdrantOptions
{
    public int HttpTimeoutSeconds { get; set; } = 5;

    public string? ApiKey { get; set; }

    public List<QdrantNodeConfig> Nodes { get; set; } = [];

    /// <summary>
    /// S3 configuration for snapshot storage (optional)
    /// Fallback if Kubernetes secret is not available
    /// </summary>
    public S3Options? S3 { get; set; }
}
