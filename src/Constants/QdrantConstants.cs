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
    /// Base path to Qdrant snapshots directory in containers
    /// </summary>
    public const string SnapshotsPath = "/qdrant/snapshots";

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

    /// <summary>
    /// Status text for accepted snapshot operations
    /// </summary>
    public const string SnapshotAcceptedStatus = "accepted";

    /// <summary>
    /// Status text for successfully created snapshot operations
    /// </summary>
    public const string SnapshotCreatedStatus = "created successfully";

    /// <summary>
    /// Status text for accepted deletion operations
    /// </summary>
    public const string SnapshotDeletionAcceptedStatus = "deletion accepted";

    /// <summary>
    /// Status text for successfully deleted snapshot operations
    /// </summary>
    public const string SnapshotDeletedStatus = "deleted successfully";

    /// <summary>
    /// File pattern for snapshot files
    /// </summary>
    public const string SnapshotFilePattern = "*.snapshot";

    /// <summary>
    /// Directory pattern for listing directories
    /// </summary>
    public const string DirectoryPattern = "*/";
}

