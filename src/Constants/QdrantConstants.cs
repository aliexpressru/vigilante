namespace Vigilante.Constants;

/// <summary>
/// Qdrant-related constants used across the application
/// </summary>
public static class QdrantConstants
{
    /// <summary>
    /// Default Qdrant HTTP API port
    /// </summary>
    public const int DefaultPort = 6333;

    /// <summary>
    /// Base path to Qdrant storage directory in containers
    /// </summary>
    public const string StoragePath = "/qdrant/storage/collections";

    /// <summary>
    /// Name of the Qdrant container in Kubernetes pods
    /// </summary>
    public const string ContainerName = "qdrant";

    /// <summary>
    /// Environment variable name for Qdrant nodes configuration
    /// </summary>
    public const string NodesEnvironmentVariable = "QDRANT_NODES";

    /// <summary>
    /// Configuration section path for Qdrant nodes
    /// </summary>
    public const string NodesConfigurationPath = "Qdrant:Nodes";

    /// <summary>
    /// HTTP protocol prefix for node URLs
    /// </summary>
    public const string HttpProtocol = "http://";
}

