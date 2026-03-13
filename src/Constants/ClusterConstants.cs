namespace Vigilante.Constants;

/// <summary>
/// Cluster-related constants used for error messages and status descriptions
/// </summary>
public static class ClusterConstants
{
    // Error messages
    public const string ConsensusThreadErrorPrefix = "Consensus thread error: ";
    public const string MessageSendFailuresPrefix = "Message send failures: ";
    public const string StaleMessageSendFailuresPrefix = "Stale message send failures (older than consensus update): ";
    public const string KubernetesEventPrefix = "K8s Event: ";
    public const string CollectionIssuePrefix = "Collection: ";
    
    // Short error descriptions
    public const string TimeoutError = "Timeout";
    public const string ConnectionError = "Connection Error";
    public const string InvalidResponseError = "Invalid Response";
    public const string ClusterSplitError = "Cluster Split";
    public const string CollectionsError = "Collections Error";
    public const string ConsensusError = "Consensus Error";
    public const string MessageSendFailuresError = "Message Send Failures";
    public const string UnknownError = "Unknown Error";
    
    // Failure formatting
    public const string UnknownErrorMessage = "unknown error";
    public const string CommunicationErrorMessage = "communication error";
    public const string SendFailureMessage = "send failure";
    public const string SendFailuresFormat = "{0} send failures";
    public const string FailureCountFormat = "{0} failures";
    public const string FailureWithCountFormat = "{0} ({1} failures)";
    
    // Message parsing
    public const string MessagePrefix = "message: \"";
    public const string StatusPrefix = "status: ";
    public const string ErrorSuffix = " error";
    public const string ErrorWithCountFormat = "{0} error ({1} failures)";
    
    // Log messages
    public const string MarkingNodeUnhealthyMessage = "Marking node {NodeUrl} as unhealthy due to message send failures (not part of cluster split)";

    /// <summary>
    /// Prefix for Restore replication factor failure warnings in cluster state.
    /// </summary>
    public const string RestoreReplicationFactorFailedPrefix = "Restore replication factor failed: ";
}
