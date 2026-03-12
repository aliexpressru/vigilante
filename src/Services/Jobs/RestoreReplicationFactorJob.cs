using Aer.QdrantClient.Http.Abstractions;
using Aer.QdrantClient.Http.Infrastructure.Replication;
using Aer.QdrantClient.Http.Models.Responses;
using Aer.QdrantClient.Http.Models.Shared;
using Microsoft.Extensions.Logging;
using Vigilante.Services.Interfaces;

namespace Vigilante.Services.Jobs;

/// <summary>
/// Job implementation: restore replication factor. Holds ShardReplicator and IAsyncEnumerator; advances only after CheckReady == true.
/// </summary>
internal sealed class RestoreReplicationFactorJob : IJob
{
    private readonly IQdrantHttpClient _client;
    private readonly string _collectionName;
    private readonly IAsyncEnumerator<ReplicateShardsToPeerResponse> _enumerator;
    private readonly ShardReplicator? _replicator;
    private bool _waitingForReady;
    private bool _disposed;
    private readonly ILogger _logger;

    public string Key => _collectionName;
    public bool IsWaitingForReady => _waitingForReady;

    private RestoreReplicationFactorJob(
        IQdrantHttpClient client,
        string collectionName,
        IAsyncEnumerator<ReplicateShardsToPeerResponse> enumerator,
        ShardReplicator? replicator,
        bool waitingForReady,
        ILogger logger)
    {
        _client = client;
        _collectionName = collectionName;
        _enumerator = enumerator;
        _replicator = replicator;
        _waitingForReady = waitingForReady;
        _logger = logger;
    }

    /// <summary>
    /// Creates job: gets replicator, creates enumerator, calls MoveNextAsync() once (starts first replication).
    /// </summary>
    public static async Task<(IJob? Job, string? InitialFailureMessage)> CreateAsync(
        IQdrantHttpClient client,
        string collectionName,
        ShardTransferMethod transferMethod,
        TimeSpan? timeout,
        CancellationToken cancellationToken,
        ILogger logger)
    {
        var response = await client.RestoreShardReplicationFactor(
            collectionName,
            cancellationToken,
            logger: logger,
            isDryRun: false,
            shardTransferMethod: transferMethod,
            timeout: timeout);

        if (response?.Status?.IsSuccess != true || response.Result == null || !response.Result.ShardsNeedReplication)
        {
            return (null, null);
        }

        var replicator = response.Result;
        var enumerator = replicator.ExecuteReplications(cancellationToken, transferMethod, timeout).GetAsyncEnumerator(cancellationToken);

        if (!await enumerator.MoveNextAsync())
        {
            return (null, null);
        }

        var replicateResponse = enumerator.Current;
        var replicatedShards = replicateResponse.Result?.ReplicatedShards;
        if (replicatedShards != null)
        {
            foreach (var item in replicatedShards)
            {
                if (!item.IsSuccess)
                {
                    var msg = $"Step 1: replication failed (ShardId: {item.ShardId}, Source: {item.SourcePeerId}, Target: {item.TargetPeerId})";
                    return (null, msg);
                }
            }
        }

        var job = new RestoreReplicationFactorJob(client, collectionName, enumerator, replicator, waitingForReady: true, logger);
        return (job, null);
    }

    public IReadOnlyDictionary<string, object?>? GetMetadata()
    {
        if (_replicator == null)
            return null;
        var plan = _replicator.ReplicationPlan;
        if (plan is not { Count: > 0 })
            return null;
        return new Dictionary<string, object?> { ["ReplicationPlan"] = plan.ToList() };
    }

    public async Task<bool?> CheckReadyAsync(CancellationToken cancellationToken)
    {
        var readyResponse = await _client.CheckCollectionReady(
            _collectionName,
            cancellationToken,
            requiredNumberOfGreenCollectionResponses: 1,
            isCheckShardTransfersCompleted: true);
        return readyResponse?.Result;
    }

    public void OnReady()
    {
        _waitingForReady = false;
    }

    public async Task<(bool HasMore, bool Success, string? ErrorMessage)> AdvanceAsync(CancellationToken cancellationToken)
    {
        if (_waitingForReady)
        {
            throw new InvalidOperationException("Call OnReady() after CheckReadyAsync returned true before calling AdvanceAsync.");
        }

        if (!await _enumerator.MoveNextAsync())
        {
            return (false, true, null);
        }

        var replicateResponse = _enumerator.Current;
        var replicatedShards = replicateResponse.Result?.ReplicatedShards;
        if (replicatedShards != null)
        {
            foreach (var item in replicatedShards)
            {
                if (!item.IsSuccess)
                {
                    var msg = $"Replication failed (ShardId: {item.ShardId}, Source: {item.SourcePeerId}, Target: {item.TargetPeerId})";
                    return (false, false, msg);
                }
            }
        }

        _waitingForReady = true;
        return (true, true, null);
    }

    public ValueTask DisposeAsync()
    {
        if (_disposed)
            return ValueTask.CompletedTask;
        _disposed = true;
        return _enumerator.DisposeAsync();
    }
}
