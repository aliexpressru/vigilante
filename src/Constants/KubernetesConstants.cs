namespace Vigilante.Constants;

/// <summary>
/// Kubernetes-related constants used across the application
/// </summary>
public static class KubernetesConstants
{
    /// <summary>
    /// Default namespace for Qdrant resources
    /// </summary>
    public const string DefaultNamespace = "qdrant";

    /// <summary>
    /// Label selector for Qdrant pods
    /// </summary>
    public const string QdrantAppLabelSelector = "app=qdrant";

    /// <summary>
    /// Pod phase indicating the pod is running
    /// </summary>
    public const string PodPhaseRunning = "Running";

    /// <summary>
    /// Kubernetes owner reference kind for StatefulSet
    /// </summary>
    public const string StatefulSetKind = "StatefulSet";

    /// <summary>
    /// Path to the service account namespace file in Kubernetes pods
    /// </summary>
    public const string ServiceAccountNamespacePath = "/var/run/secrets/kubernetes.io/serviceaccount/namespace";

    /// <summary>
    /// Log message when Kubernetes client is not available
    /// </summary>
    public const string KubernetesClientNotAvailableMessage = "Kubernetes client is not available. Running outside Kubernetes cluster?";

    /// <summary>
    /// Annotation key for tracking StatefulSet restart time
    /// </summary>
    public const string RestartedAtAnnotationKey = "vigilante.aer.io/restartedAt";

    // Log message templates
    /// <summary>
    /// Log message for missing namespace parameter
    /// </summary>
    public const string NamespaceNotProvidedForPodMessage = "Namespace not provided for pod {PodName}, using default '{DefaultNamespace}'";

    /// <summary>
    /// Log message for missing namespace parameter for StatefulSet
    /// </summary>
    public const string NamespaceNotProvidedForStatefulSetMessage = "Namespace not provided for StatefulSet {StatefulSetName}, using default '{DefaultNamespace}'";

    // Operation descriptions
    /// <summary>
    /// Description for rollout restart operation
    /// </summary>
    public const string RolloutRestartOperation = "rollout restart";

    /// <summary>
    /// Format string for scale operation description
    /// </summary>
    public const string ScaleOperationFormat = "scale to {0} replicas";

    // Event API constants
    /// <summary>
    /// Field selector for Warning type events
    /// </summary>
    public const string WarningEventFieldSelector = "type=Warning";

    /// <summary>
    /// Limit for number of events to fetch
    /// </summary>
    public const int EventsFetchLimit = 20;

    /// <summary>
    /// Timestamp format for events
    /// </summary>
    public const string EventTimestampFormat = "yyyy-MM-dd HH:mm:ss";

    // Default values for missing event data
    /// <summary>
    /// Default text when timestamp is not available
    /// </summary>
    public const string UnknownTime = "Unknown time";

    /// <summary>
    /// Default text when involved object is not available
    /// </summary>
    public const string UnknownObject = "Unknown object";

    /// <summary>
    /// Default text when event reason is not available
    /// </summary>
    public const string UnknownReason = "Unknown reason";

    /// <summary>
    /// Default text when event message is not available
    /// </summary>
    public const string NoMessage = "No message";

    // Event format template
    /// <summary>
    /// Format template for event warnings
    /// </summary>
    public const string EventWarningFormat = "[{0}] {1}: {2} - {3}";

    // Log messages
    /// <summary>
    /// Log message when Events v1 API is not available
    /// </summary>
    public const string EventsV1NotAvailableMessage = "Events v1 API not available or failed, falling back to CoreV1 Events";

    /// <summary>
    /// RBAC permission error message template
    /// </summary>
    public const string RbacPermissionErrorMessage = "Access denied to read events in namespace {Namespace}. " +
        "ServiceAccount may be missing RBAC permissions for 'events' resource. " +
        "Please apply updated k8s/rbac.yaml to grant necessary permissions.";

    // Forbidden error identifiers
    /// <summary>
    /// Text to identify Forbidden HTTP errors
    /// </summary>
    public const string ForbiddenError = "Forbidden";

    /// <summary>
    /// HTTP status code for Forbidden errors
    /// </summary>
    public const string ForbiddenStatusCode = "403";

    // DateTime format for ISO 8601
    /// <summary>
    /// ISO 8601 round-trip date/time format
    /// </summary>
    public const string Iso8601Format = "o";

    // Dynamic Configuration
    /// <summary>
    /// Name of the ConfigMap used for storing dynamic configuration
    /// </summary>
    public const string DynamicConfigMapName = "vigilante-dynamic-config";

    /// <summary>
    /// Key in ConfigMap data for dynamic configuration JSON
    /// </summary>
    public const string DynamicConfigMapKey = "dynamic-config.json";

    /// <summary>
    /// Path to the mounted dynamic config file in the pod
    /// </summary>
    public const string DynamicConfigFilePath = "/app/config/dynamic-config.json";

    /// <summary>
    /// Label for Vigilante app
    /// </summary>
    public const string VigilanteAppLabel = "vigilante";

    /// <summary>
    /// Label value for managed-by
    /// </summary>
    public const string ManagedByVigilanteLabel = "vigilante";

    /// <summary>
    /// Description annotation for dynamic config Endpoints
    /// </summary>
    public const string DynamicConfigDescription = "Dynamic configuration for Vigilante - do not delete";
}

