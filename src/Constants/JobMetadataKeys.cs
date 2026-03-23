namespace Vigilante.Constants;

/// <summary>
/// Unified keys for background job metadata exposed via API.
/// </summary>
public static class JobMetadataKeys
{
    public const string CurrentAction = "CurrentAction";
    public const string StartedAtUtc = "StartedAtUtc";
    public const string ReplicationPlan = "ReplicationPlan";

    public const string Phase = "Phase";
    public const string LastRunStartedUtc = "LastRunStartedUtc";
    public const string LastCompletedUtc = "LastCompletedUtc";
    public const string LastRunSuccess = "LastRunSuccess";
    public const string LastRunSummary = "LastRunSummary";
}
