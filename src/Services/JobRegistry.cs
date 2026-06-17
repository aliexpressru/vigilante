using System.Collections.Concurrent;
using Vigilante.Models;
using Vigilante.Services.Interfaces;
using Vigilante.Services.Jobs.Snapshots;

namespace Vigilante.Services;

/// <summary>
/// In-memory registry for background jobs and their failure warnings. Used by QdrantMonitorService and by job starters (e.g. ClusterManager).
/// Processes pending jobs on request (monitor tick and API refresh) so jobs progress without waiting for the full monitor interval.
/// </summary>
public sealed class JobRegistry(ILogger<JobRegistry> logger) : IJobRegistry
{
    private readonly ConcurrentDictionary<string, (IJob Job, CancellationTokenSource Cts)> _jobs = new();

    /// <summary>
    /// One processor at a time per job key. Otherwise two concurrent <see cref="ProcessPendingJobsAsync"/>
    /// (e.g. monitor + JobsController from dashboard) could run <see cref="IJob.AdvanceAsync"/> on the same job;
    /// the first completion calls <see cref="RemoveJobAsync"/> and cancels the CTS, aborting in-flight HTTP in the second.
    /// </summary>
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _jobExecutionGates = new();

    private readonly ConcurrentDictionary<string, (string Message, DateTime RecordedAt)> _jobErrors = new();

    /// <summary>
    /// How long to keep and return failed job errors after the job was removed (so the frontend can show them).
    /// </summary>
    private static readonly TimeSpan _failedJobErrorTtl = TimeSpan.FromMinutes(5);

    public bool TryAddJob(IJob job, CancellationTokenSource cts)
    {
        return _jobs.TryAdd(job.Key, (job, cts));
    }

    public bool HasPendingSnapshotCreationForCollection(string collectionName)
        => HasJobWithPrefixForCollection(PendingSnapshotCreationJob.KeyPrefix, collectionName);

    public bool HasPendingMultiSnapshotRecoveryForCollection(string collectionName)
        => HasJobWithPrefixForCollection(RecoverFromMultipleSnapshotsJob.JobKeyPrefix, collectionName);

    private bool HasJobWithPrefixForCollection(string prefix, string collectionName)
    {
        foreach (var key in _jobs.Keys)
        {
            if (key.Length <= prefix.Length || !key.StartsWith(prefix, StringComparison.Ordinal))
            {
                continue;
            }

            if (string.Equals(key[prefix.Length..], collectionName, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    public IReadOnlyList<PendingJob> GetPendingJobs()
    {
        return [.. _jobs.Select(kv => new PendingJob(kv.Value.Job, kv.Value.Cts))];
    }

    public async Task RemoveJobAsync(string key)
    {
        if (!_jobs.TryRemove(key, out var entry))
        {
            return;
        }

        try
        {
            entry.Cts.Cancel();
            await entry.Job.DisposeAsync();
        }
        finally
        {
            entry.Cts.Dispose();
        }
    }

    public async Task<bool> CancelJobAsync(string key, CancellationToken cancellationToken = default)
    {
        if (_jobs.TryRemove(key, out var entry))
        {
            try
            {
                entry.Cts.Cancel();
                await entry.Job.DisposeAsync();
            }
            finally
            {
                entry.Cts.Dispose();
            }

            return true;
        }

        // Allow removing completed failed jobs from UI (Error rows are stored in _jobErrors).
        return _jobErrors.TryRemove(key, out _);
    }

    public void RecordJobFailure(string key, string message)
    {
        _jobErrors[key] = (message, DateTime.UtcNow);
    }

    public IReadOnlyList<(string Key, string Message)> GetActiveErrorsAndPruneExpired(DateTime now, TimeSpan ttl)
    {
        var result = new List<(string Key, string Message)>();
        var expired = new List<string>();

        foreach (var (key, (message, recordedAt)) in _jobErrors)
        {
            if (now - recordedAt <= ttl)
            {
                result.Add((key, message));
            }
            else
            {
                expired.Add(key);
            }
        }
        foreach (var key in expired)
        {
            _jobErrors.TryRemove(key, out _);
        }

        return result;
    }

    public IReadOnlyList<JobInfoDto> GetJobInfos()
    {
        var now = DateTime.UtcNow;
        var list = new List<JobInfoDto>();
        var jobKeys = new HashSet<string>();

        foreach (var (key, (job, _)) in _jobs)
        {
            jobKeys.Add(key);
            var error = _jobErrors.TryGetValue(key, out var err) ? err.Message : null;
            var errorRecordedAt = _jobErrors.TryGetValue(key, out var err2) ? err2.RecordedAt : (DateTime?)null;
            var metadata = job.GetMetadata();

            list.Add(new JobInfoDto(key, error, errorRecordedAt, metadata));
        }

        // Include recently failed jobs (already removed from _jobs) so the frontend can show the error
        var toPrune = new List<string>();
        foreach (var (key, (message, recordedAt)) in _jobErrors)
        {
            if (jobKeys.Contains(key))
            {
                continue;
            }

            if (now - recordedAt > _failedJobErrorTtl)
            {
                toPrune.Add(key);
                continue;
            }
            list.Add(new JobInfoDto(key, message, recordedAt, null));
        }
        foreach (var key in toPrune)
        {
            _jobErrors.TryRemove(key, out _);
        }

        return list;
    }

    public async Task ProcessPendingJobsAsync(
        CancellationToken cancellationToken = default,
        IReadOnlySet<string>? excludeJobKeys = null
    )
    {
        var jobs = GetPendingJobs();

        if (excludeJobKeys is { Count: > 0 })
        {
            jobs = [.. jobs.Where(p => !excludeJobKeys.Contains(p.Job.Key))];
        }

        if (jobs.Count == 0)
        {
            return;
        }

        var tasks = jobs.Select(pending => ProcessOneJobAsync(pending, cancellationToken));
        await Task.WhenAll(tasks);
    }

    private async Task ProcessOneJobAsync(PendingJob pending, CancellationToken cancellationToken)
    {
        var key = pending.Job.Key;
        var gate = _jobExecutionGates.GetOrAdd(key, static _ => new SemaphoreSlim(1, 1));

        await gate.WaitAsync(cancellationToken);

        try
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return;
            }

            if (!_jobs.TryGetValue(key, out var entry))
            {
                return;
            }

            var job = entry.Job;
            var cts = entry.Cts;

            if (cts.Token.IsCancellationRequested)
            {
                await RemoveJobAsync(key);
                return;
            }

            if (job.IsWaitingForReady)
            {
                var ready = await job.CheckReadyAsync(cts.Token);
                if (ready == true)
                {
                    job.OnReady();
                }
            }
            else
            {
                var (hasMore, success, error) = await job.AdvanceAsync(cts.Token);
                if (!hasMore)
                {
                    if (!success)
                    {
                        RecordJobFailure(key, error ?? "Unknown");
                    }

                    await RemoveJobAsync(key);
                    if (success)
                    {
                        logger.LogInformation("Job completed for key {Key}", key);
                    }
                }
                else if (!success)
                {
                    RecordJobFailure(key, error ?? "Unknown");
                    await RemoveJobAsync(key);
                }
            }
        }
        catch (OperationCanceledException)
        {
            logger.LogInformation("Job cancelled for key {Key}", key);
            await RemoveJobAsync(key);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Job failed for key {Key}", key);
            RecordJobFailure(key, ex.Message);
            await RemoveJobAsync(key);
        }
        finally
        {
            gate.Release();
        }
    }
}
