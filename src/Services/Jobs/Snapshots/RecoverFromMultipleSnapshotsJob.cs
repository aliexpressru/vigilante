using Aer.QdrantClient.Http.Abstractions;
using Aer.QdrantClient.Http.Infrastructure.Snapshots;
using Aer.QdrantClient.Http.Models.Responses;
using Aer.QdrantClient.Http.Models.Shared;
using Vigilante.Constants;
using Vigilante.Extensions;
using Vigilante.Services.Interfaces;

namespace Vigilante.Services.Jobs.Snapshots;

/// <summary>
/// Job: recovers a collection on multiple nodes sequentially from the provided snapshots.
/// Uses CollectionRecoverer from RecoverCollectionFromSnapshots API; advances one node per step.
/// </summary>
internal sealed class RecoverFromMultipleSnapshotsJob : IJob
{
    private readonly string _targetCollectionName;
    private readonly IReadOnlyList<string> _targetNodeUrls;
    private readonly IAsyncEnumerator<DefaultOperationResponse> _enumerator;
    private readonly IReadOnlyList<ScheduledPeerCollectionRecovery> _recoveryPlanSnapshot;
    private readonly DateTime _startedAtUtc;
    private readonly IServiceProvider _serviceProvider;
    private int _currentStepIndex;

    private bool _seenActiveTransfers;
    private bool _baselineCaptured;
    private string? _initialShardsFingerprint;
    private DateTime _stepStartedAtUtc;
    private bool _disposed;

    private static readonly TimeSpan _initialReadyGracePeriod = TimeSpan.FromSeconds(20);

    public string Key => $"snapshot-multi-recovery-{_targetCollectionName}";

    public bool IsWaitingForReady { get; private set; }

    private RecoverFromMultipleSnapshotsJob(
        string targetCollectionName,
        IReadOnlyList<string> targetNodeUrls,
        IAsyncEnumerator<DefaultOperationResponse> enumerator,
        IReadOnlyList<ScheduledPeerCollectionRecovery> recoveryPlanSnapshot,
        IServiceProvider serviceProvider
    )
    {
        _targetCollectionName = targetCollectionName;
        _targetNodeUrls = targetNodeUrls;
        _enumerator = enumerator;
        _recoveryPlanSnapshot = recoveryPlanSnapshot;
        _serviceProvider = serviceProvider;
        _startedAtUtc = DateTime.UtcNow;
        _stepStartedAtUtc = _startedAtUtc;
        IsWaitingForReady = true;
    }

    /// <summary>
    /// Creates the job by calling RecoverCollectionFromSnapshots and executing the first recovery step.
    /// Returns null job with null error when there are no recovery steps needed.
    /// </summary>
    public static async Task<(IJob? Job, string? ErrorMessage)> CreateAsync(
        IServiceProvider serviceProvider,
        IQdrantHttpClient[] peerClients,
        Uri[] snapshotUris,
        string[] targetNodeUrls,
        string targetCollectionName,
        SnapshotPriority snapshotPriority,
        CancellationToken cancellationToken
    )
    {
        var logger = serviceProvider.GetRequiredService<ILogger<RecoverFromMultipleSnapshotsJob>>();

        // Use the first peer as coordinator — it calls the API on behalf of all peers.
        var coordinatorClient = peerClients[0];
        var response = await coordinatorClient.RecoverCollectionFromSnapshots(
            targetCollectionName,
            peerClients,
            snapshotUris,
            cancellationToken,
            logger,
            snapshotPriority
        );

        if (response is null || response.Status.IsSuccess != true)
        {
            var error = response?.Status?.GetErrorMessage() ?? "Unknown error";
            return (null, $"Failed to initiate multi-snapshot recovery: {error}");
        }

        var recoverer = response.Result;
        if (recoverer is null)
        {
            return (null, null);
        }

        var planSnapshot = recoverer.RecoveryPlan.ToList();
        if (planSnapshot.Count == 0)
        {
            return (null, null);
        }

        var enumerator = recoverer
            .ExecuteRecoveries(cancellationToken, isWaitForResult: false, snapshotPriority)
            .GetAsyncEnumerator(cancellationToken);

        if (!await enumerator.MoveNextAsync())
        {
            return (null, null);
        }

        var firstStepResponse = enumerator.Current;
        if (firstStepResponse.Status.IsSuccess != true)
        {
            var error = firstStepResponse.Status.GetErrorMessage() ?? "Unknown error";
            await enumerator.DisposeAsync();
            return (null, $"Failed to start multi-snapshot recovery step 1 : {error}");
        }

        var job = new RecoverFromMultipleSnapshotsJob(
            targetCollectionName,
            targetNodeUrls,
            enumerator,
            planSnapshot,
            serviceProvider
        );

        return (job, null);
    }

    public async Task<bool?> CheckReadyAsync(CancellationToken cancellationToken)
    {
        if (!IsWaitingForReady)
        {
            return null;
        }

        var targetNodeUrl = _targetNodeUrls[_currentStepIndex];
        var clusterManager = _serviceProvider.GetRequiredService<IClusterManager>();
        var collections = await clusterManager.GetCollectionsInfoAsync(clearCache: true, cancellationToken);

        var nodeCollection = collections.FirstOrDefault(c =>
            string.Equals(c.CollectionName, _targetCollectionName, StringComparison.OrdinalIgnoreCase)
            && string.Equals(c.NodeUrl, targetNodeUrl, StringComparison.OrdinalIgnoreCase)
        );

        if (nodeCollection is null)
        {
            return false;
        }

        var metrics = nodeCollection.Metrics;
        var hasActiveTransfers = metrics.OutgoingTransfers is { Count: > 0 };
        var currentShardsFingerprint = metrics.Shards.BuildShardsFingerprint();

        if (hasActiveTransfers)
        {
            _seenActiveTransfers = true;
            return false;
        }

        if (!_baselineCaptured)
        {
            _initialShardsFingerprint = currentShardsFingerprint;
            _baselineCaptured = true;
            return false;
        }

        var shardsChanged = !string.Equals(_initialShardsFingerprint, currentShardsFingerprint, StringComparison.Ordinal);
        if (shardsChanged)
        {
            return true;
        }

        if (_seenActiveTransfers)
        {
            return true;
        }

        if (DateTime.UtcNow - _stepStartedAtUtc < _initialReadyGracePeriod)
        {
            return false;
        }

        return true;
    }

    public void OnReady() => IsWaitingForReady = false;

    public async Task<(bool HasMore, bool Success, string? ErrorMessage)> AdvanceAsync(CancellationToken cancellationToken)
    {
        if (!await _enumerator.MoveNextAsync())
        {
            return (false, true, null);
        }

        var stepResponse = _enumerator.Current;

        if (!stepResponse.Status.IsSuccess)
        {
            var error = stepResponse.Status.GetErrorMessage() ?? "Unknown error";
            var stepNumber = _currentStepIndex + 1;
            return (false, false, $"Multi-snapshot recovery step {stepNumber} failed: {error}");
        }

        _currentStepIndex++;
        ResetStepReadyState();
        IsWaitingForReady = true;
        return (true, true, null);
    }

    public IReadOnlyDictionary<string, object?>? GetMetadata()
    {
        var totalSteps = _recoveryPlanSnapshot.Count;
        var step = _currentStepIndex + 1;
        var action = IsWaitingForReady
            ? $"Waiting for collection recovery on node {step}/{totalSteps}"
            : $"Running collection recovery on node {step}/{totalSteps}";

        var recoveryPlan = _recoveryPlanSnapshot
            .Select((step, i) => new
            {
                StepNumber = i + 1,
                TargetNodeUrl = i < _targetNodeUrls.Count ? _targetNodeUrls[i] : string.Empty,
                SnapshotUri = step.SnapshotLocationUri?.ToString(),
                SnapshotChecksum = step.SnapshotChecksum
            })
            .ToList();

        return new Dictionary<string, object?>
        {
            [JobMetadataKeys.CurrentAction] = action,
            [JobMetadataKeys.StartedAtUtc] = _startedAtUtc,
            [JobMetadataKeys.RecoveryPlan] = recoveryPlan,
        };
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

    private void ResetStepReadyState()
    {
        _seenActiveTransfers = false;
        _baselineCaptured = false;
        _initialShardsFingerprint = null;
        _stepStartedAtUtc = DateTime.UtcNow;
    }
}
