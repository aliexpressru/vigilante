using Aer.QdrantClient.Http.Abstractions;
using Aer.QdrantClient.Http.Infrastructure.Replication;
using Aer.QdrantClient.Http.Models.Responses;
using Aer.QdrantClient.Http.Models.Shared;
using Vigilante.Constants;
using Vigilante.Services.Interfaces;

namespace Vigilante.Services.Jobs;

/// <summary>
/// Job implementation: restore replication factor. Holds ShardReplicator and IAsyncEnumerator; advances only after CheckReady == true.
/// </summary>
internal sealed class RestoreReplicationFactorJob : IJob
{
    private readonly IQdrantHttpClient _client;
    private readonly IAsyncEnumerator<ReplicateShardsToPeerResponse> _enumerator;
    private readonly DateTime _startedAtUtc;
    private readonly IReadOnlyList<ScheduledShardReplication> _replicationPlanSnapshot;
    private bool _disposed;

    public string Key => CollectionName;

    public bool IsWaitingForReady { get; private set; }

    public string CollectionName { get; }

    private RestoreReplicationFactorJob(
        IQdrantHttpClient client,
        string collectionName,
        IAsyncEnumerator<ReplicateShardsToPeerResponse> enumerator,
        IReadOnlyList<ScheduledShardReplication> replicationPlanSnapshot,
        bool waitingForReady
    )
    {
        _client = client;
        CollectionName = collectionName;
        _enumerator = enumerator;
        _startedAtUtc = DateTime.UtcNow;
        _replicationPlanSnapshot = replicationPlanSnapshot;
        IsWaitingForReady = waitingForReady;
    }

    /// <summary>
    /// Creates job: gets replicator, creates enumerator, calls MoveNextAsync() once (starts first replication).
    /// Resolves ILogger from the service provider for the API call.
    /// </summary>
    public static async Task<(IJob? Job, string? InitialFailureMessage)> CreateAsync(
        IServiceProvider serviceProvider,
        IQdrantHttpClient client,
        string collectionName,
        ShardTransferMethod transferMethod,
        TimeSpan? timeout,
        CancellationToken cancellationToken
    )
    {
        var logger = serviceProvider.GetRequiredService<ILogger<RestoreReplicationFactorJob>>();
        var response = await client.RestoreShardReplicationFactor(
            collectionName,
            cancellationToken,
            logger: logger,
            isDryRun: false,
            shardTransferMethod: transferMethod,
            timeout: timeout
        );

        if (response is null)
        {
            return (null, null);
        }

        if (response.Status.IsSuccess != true)
        {
            return (null, $"Failed to start restore replication factor: {response.Status.GetErrorMessage() ?? "Unknown error"}");
        }

        if (response.Result is null)
        {
            return (null, null);
        }

        if (!response.Result.ShardsNeedReplication)
        {
            return (null, null);
        }

        var replicator = response.Result;

        // Snapshot the full plan BEFORE the first MoveNextAsync() call, because MoveNext dequeues step #1.
        var replicationPlanSnapshot = replicator.ReplicationPlan.OrderBy(p => p.StepNumber).ToList();

        var enumerator = replicator
            .ExecuteReplications(cancellationToken, transferMethod, timeout)
            .GetAsyncEnumerator(cancellationToken);

        if (!await enumerator.MoveNextAsync())
        {
            return (null, null);
        }

        var replicateResponse = enumerator.Current;

        var step1Failure = GetReplicationStepFailureMessage(
            replicateResponse,
            failedToStartPrefix: "Step 1: replication failed to start",
            noReplicatedShardsMessage: "Step 1: replication response has no ReplicatedShards",
            itemFailurePrefix: "Step 1: replication failed"
        );

        if (step1Failure is not null)
        {
            return (null, step1Failure);
        }

        var job = new RestoreReplicationFactorJob(
            client,
            collectionName,
            enumerator,
            replicationPlanSnapshot,
            waitingForReady: true
        );

        return (job, null);
    }

    public IReadOnlyDictionary<string, object?>? GetMetadata()
    {
        // Keep metadata stable for the whole job lifetime; live ReplicationPlan queue is consumed during execution.
        var currentAction = IsWaitingForReady ? "Waiting for shard transfers to complete" : "Restoring replication factor";

        var metadata = new Dictionary<string, object?>
        {
            [JobMetadataKeys.CurrentAction] = currentAction,
            [JobMetadataKeys.StartedAtUtc] = _startedAtUtc,
        };

        if (_replicationPlanSnapshot.Count > 0)
        {
            metadata[JobMetadataKeys.ReplicationPlan] = _replicationPlanSnapshot;
        }

        return metadata;
    }

    public async Task<bool?> CheckReadyAsync(CancellationToken cancellationToken)
    {
        var readyResponse = await _client.CheckCollectionReady(
            CollectionName,
            cancellationToken,
            requiredNumberOfGreenCollectionResponses: 1,
            isCheckShardTransfersCompleted: true
        );

        return readyResponse?.Result;
    }

    public void OnReady()
    {
        IsWaitingForReady = false;
    }

    public async Task<(bool HasMore, bool Success, string? ErrorMessage)> AdvanceAsync(CancellationToken cancellationToken)
    {
        if (IsWaitingForReady)
        {
            throw new InvalidOperationException(
                "Call OnReady() after CheckReadyAsync returned true before calling AdvanceAsync."
            );
        }

        if (!await _enumerator.MoveNextAsync())
        {
            return (false, true, null);
        }

        var replicateResponse = _enumerator.Current;
        var failure = GetReplicationStepFailureMessage(
            replicateResponse,
            failedToStartPrefix: "Replication step failed to start",
            noReplicatedShardsMessage: "Replication step failed: response has no ReplicatedShards",
            itemFailurePrefix: "Replication failed"
        );

        if (failure is not null)
        {
            return (false, false, failure);
        }

        IsWaitingForReady = true;
        return (true, true, null);
    }

    public ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return ValueTask.CompletedTask;
        }

        _disposed = true;
        return _enumerator.DisposeAsync();
    }

    internal static string? GetReplicationStepFailureMessage(
        ReplicateShardsToPeerResponse replicateResponse,
        string failedToStartPrefix,
        string noReplicatedShardsMessage,
        string itemFailurePrefix
    )
    {
        if (replicateResponse.Status.IsSuccess != true)
        {
            var msg = replicateResponse.Status.GetErrorMessage() ?? "Unknown error";
            return $"{failedToStartPrefix}: {msg}";
        }

        var replicatedShards = replicateResponse.Result?.ReplicatedShards;
        if (replicatedShards is null)
        {
            return noReplicatedShardsMessage;
        }

        foreach (var item in replicatedShards)
        {
            if (item.IsSuccess)
            {
                continue;
            }

            return $"{itemFailurePrefix} (ShardId: {item.ShardId}, Source: {item.SourcePeerId}, Target: {item.TargetPeerId})";
        }

        return null;
    }
}
