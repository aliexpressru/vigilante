using Vigilante.Models;

namespace Vigilante.Services.Interfaces;

/// <summary>
/// Registry for background jobs. Owned by the monitor layer; job starters (e.g. ClusterManager) add jobs here by key from IJob.Key.
/// </summary>
public interface IJobRegistry
{
    /// <summary>
    /// Tries to add a job by its Key. Returns false if a job with the same key already exists.
    /// </summary>
    bool TryAddJob(IJob job, CancellationTokenSource cts);

    /// <summary>
    /// Returns all pending jobs (for monitor to process each tick).
    /// </summary>
    IReadOnlyList<PendingJob> GetPendingJobs();

    /// <summary>
    /// Removes the job, disposes it and its CancellationTokenSource.
    /// </summary>
    Task RemoveJobAsync(string key);

    /// <summary>
    /// Cancels the job for the given key and removes it. Returns false if no job found.
    /// </summary>
    Task<bool> CancelJobAsync(string key, CancellationToken cancellationToken = default);

    /// <summary>
    /// Records a job failure for display and for ClusterManager to surface via ReportIssue. TTL applies.
    /// </summary>
    void RecordJobFailure(string key, string message);

    /// <summary>
    /// Returns active (non-expired) job errors and prunes expired ones. Used by ClusterManager to ReportIssue.
    /// </summary>
    IReadOnlyList<(string Key, string Message)> GetActiveErrorsAndPruneExpired(DateTime now, TimeSpan ttl);

    /// <summary>
    /// Returns current job infos for API: key, optional error, metadata (e.g. ReplicationPlan).
    /// </summary>
    IReadOnlyList<JobInfoDto> GetJobInfos();
}
