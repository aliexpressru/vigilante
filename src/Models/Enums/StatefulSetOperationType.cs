namespace Vigilante.Models.Enums;

/// <summary>
/// Type of operation to perform on a StatefulSet
/// </summary>
public enum StatefulSetOperationType
{
    /// <summary>
    /// Rollout restart: kubectl rollout restart statefulset
    /// </summary>
    Rollout = 0,
    
    /// <summary>
    /// Scale: kubectl scale statefulsets --replicas=N
    /// </summary>
    Scale = 1
}

