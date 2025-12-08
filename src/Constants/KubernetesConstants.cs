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
}

