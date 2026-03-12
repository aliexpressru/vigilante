using System.Collections.Concurrent;
using Vigilante.Models;
using Vigilante.Services.Interfaces;

namespace Vigilante.Services;

/// <summary>
/// In-memory registry for background jobs and their failure warnings. Used by QdrantMonitorService and by job starters (e.g. ClusterManager).
/// </summary>
public sealed class JobRegistry : IJobRegistry
{
    private readonly ConcurrentDictionary<string, (IJob Job, CancellationTokenSource Cts)> _jobs = new();

    public bool TryAddJob(IJob job, CancellationTokenSource cts)
    {
        return _jobs.TryAdd(job.Key, (job, cts));
    }

    public IReadOnlyList<PendingJob> GetPendingJobs()
    {
        return _jobs.Select(kv => new PendingJob(kv.Value.Job, kv.Value.Cts)).ToList();
    }

    public async Task RemoveJobAsync(string key)
    {
        if (!_jobs.TryRemove(key, out var entry))
            return;
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
        if (!_jobs.TryRemove(key, out var entry))
            return false;
        try
        {
            entry.Cts.Cancel();
            await entry.Job.DisposeAsync();
        }
        finally
        {
            entry.Cts.Dispose();
        }
        return await Task.FromResult(true);
    }

    private readonly ConcurrentDictionary<string, (string Message, DateTime RecordedAt)> _jobErrors = new();

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
                result.Add((key, message));
            else
                expired.Add(key);
        }
        foreach (var key in expired)
            _jobErrors.TryRemove(key, out _);
        return result;
    }

    public IReadOnlyList<JobInfoDto> GetJobInfos()
    {
        var list = new List<JobInfoDto>();
        foreach (var (key, (job, cts)) in _jobs)
        {
            var error = _jobErrors.TryGetValue(key, out var err) ? err.Message : null;
            var errorRecordedAt = _jobErrors.TryGetValue(key, out var err2) ? err2.RecordedAt : (DateTime?)null;
            var metadata = job.GetMetadata();
            list.Add(new JobInfoDto(key, error, errorRecordedAt, metadata));
        }
        return list;
    }
}
