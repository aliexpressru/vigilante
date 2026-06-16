using System.Reflection;
using Aer.QdrantClient.Http.Abstractions;
using Aer.QdrantClient.Http.Infrastructure.Replication;
using Aer.QdrantClient.Http.Models.Responses;
using Aer.QdrantClient.Http.Models.Shared;
using FluentAssertions;
using NSubstitute;
using NUnit.Framework;
using Vigilante.Constants;
using Vigilante.Services.Jobs;

namespace Aer.Vigilante.Tests.Services;

[TestFixture]
public class RestoreReplicationFactorJobTests
{
    [Test]
    public void GetReplicationStepFailureMessage_WhenStatusNotSuccess_ReturnsFailedToStartMessage()
    {
        var replicateResponse = new ReplicateShardsToPeerResponse
        {
            Status = new QdrantStatus(QdrantOperationStatusType.Error) { Error = "qdrant down" },
            Result = null!
        };

        var failure = RestoreReplicationFactorJob.GetReplicationStepFailureMessage(
            replicateResponse,
            failedToStartPrefix: "Replication step failed to start",
            noReplicatedShardsMessage: "Replication step failed: response has no ReplicatedShards",
            itemFailurePrefix: "Replication failed");

        failure.Should().Be("Replication step failed to start: qdrant down");
    }

    [Test]
    public void GetReplicationStepFailureMessage_WhenReplicatedShardsMissing_ReturnsNoReplicatedShardsMessage()
    {
        var replicateResponse = new ReplicateShardsToPeerResponse
        {
            Status = new QdrantStatus(QdrantOperationStatusType.Ok),
            Result = null!
        };

        var failure = RestoreReplicationFactorJob.GetReplicationStepFailureMessage(
            replicateResponse,
            failedToStartPrefix: "Replication step failed to start",
            noReplicatedShardsMessage: "Replication step failed: response has no ReplicatedShards",
            itemFailurePrefix: "Replication failed");

        failure.Should().Be("Replication step failed: response has no ReplicatedShards");
    }

    [Test]
    public void GetReplicationStepFailureMessage_WhenAnyShardReplicationFails_ReturnsItemFailureMessage()
    {
        var replicateResponse = new ReplicateShardsToPeerResponse
        {
            Status = new QdrantStatus(QdrantOperationStatusType.Ok),
            Result = new ReplicateShardsToPeerResponse.ReplicateShardsToPeerResponseUnit(
                ReplicatedShards:
                [
                    new ReplicateShardsToPeerResponse.ReplicateShardToPeerResult(
                        IsSuccess: false,
                        ShardId: 1,
                        SourcePeerId: 2,
                        TargetPeerId: 3,
                        CollectionName: "col1")
                ],
                AlreadyReplicatedShards: null!)
        };

        var failure = RestoreReplicationFactorJob.GetReplicationStepFailureMessage(
            replicateResponse,
            failedToStartPrefix: "Replication step failed to start",
            noReplicatedShardsMessage: "Replication step failed: response has no ReplicatedShards",
            itemFailurePrefix: "Replication failed");

        failure.Should().Be("Replication failed (ShardId: 1, Source: 2, Target: 3)");
    }

    [Test]
    public void GetReplicationStepFailureMessage_WhenAllShardReplicationsSucceeded_ReturnsNull()
    {
        var replicateResponse = new ReplicateShardsToPeerResponse
        {
            Status = new QdrantStatus(QdrantOperationStatusType.Ok),
            Result = new ReplicateShardsToPeerResponse.ReplicateShardsToPeerResponseUnit(
                ReplicatedShards:
                [
                    new ReplicateShardsToPeerResponse.ReplicateShardToPeerResult(
                        IsSuccess: true,
                        ShardId: 1,
                        SourcePeerId: 2,
                        TargetPeerId: 3,
                        CollectionName: "col1")
                ],
                AlreadyReplicatedShards: null!)
        };

        var failure = RestoreReplicationFactorJob.GetReplicationStepFailureMessage(
            replicateResponse,
            failedToStartPrefix: "Replication step failed to start",
            noReplicatedShardsMessage: "Replication step failed: response has no ReplicatedShards",
            itemFailurePrefix: "Replication failed");

        failure.Should().BeNull();
    }

    [Test]
    public async Task MetadataReplicationPlan_WhenJobAdvances_KeepsFirstStep()
    {
        var plan = new List<ScheduledShardReplication>
        {
            new(
                10,
                100,
                "http://node1:6333",
                200,
                "http://node2:6333",
                ScheduledShardReplication.ReplicatorAction.MoveReplica,
                1),
            new(
                11,
                101,
                "http://node1:6333",
                201,
                "http://node3:6333",
                ScheduledShardReplication.ReplicatorAction.AddReplica,
                1)
        };

        var successResponse = new ReplicateShardsToPeerResponse
        {
            Status = new QdrantStatus(QdrantOperationStatusType.Ok),
            Result = new ReplicateShardsToPeerResponse.ReplicateShardsToPeerResponseUnit(
                ReplicatedShards:
                [
                    new ReplicateShardsToPeerResponse.ReplicateShardToPeerResult(
                        IsSuccess: true,
                        ShardId: 10,
                        SourcePeerId: 100,
                        TargetPeerId: 200,
                        CollectionName: "col1")
                ],
                AlreadyReplicatedShards: [])
        };

        await using var enumerator = new TestReplicateEnumerator(successResponse);
        var client = Substitute.For<IQdrantHttpClient>();
        var job = CreateJobForTest(client, "col1", enumerator, plan, waitingForReady: false);

        var (HasMore, Success, _) = await job.AdvanceAsync(CancellationToken.None);

        Success.Should().BeTrue();
        HasMore.Should().BeTrue();

        var metadata = job.GetMetadata();
        metadata.Should().NotBeNull();
        metadata!.ContainsKey(JobMetadataKeys.ReplicationPlan).Should().BeTrue();
        metadata[JobMetadataKeys.ReplicationPlan].Should().BeSameAs(plan);
    }

    private static RestoreReplicationFactorJob CreateJobForTest(
        IQdrantHttpClient client,
        string collectionName,
        IAsyncEnumerator<ReplicateShardsToPeerResponse> enumerator,
        IReadOnlyList<ScheduledShardReplication> replicationPlanSnapshot,
        bool waitingForReady)
    {
        var ctor = typeof(RestoreReplicationFactorJob).GetConstructor(
            BindingFlags.NonPublic | BindingFlags.Instance,
            binder: null,
            [
                typeof(IQdrantHttpClient),
                typeof(string),
                typeof(IAsyncEnumerator<ReplicateShardsToPeerResponse>),
                typeof(IReadOnlyList<ScheduledShardReplication>),
                typeof(bool)
            ],
            modifiers: null);

        ctor.Should().NotBeNull("Private constructor signature changed.");
        return (RestoreReplicationFactorJob)ctor!.Invoke([client, collectionName, enumerator, replicationPlanSnapshot, waitingForReady]);
    }

    private sealed class TestReplicateEnumerator(params ReplicateShardsToPeerResponse[] responses)
        : IAsyncEnumerator<ReplicateShardsToPeerResponse>
    {
        private int _index = -1;

        public ReplicateShardsToPeerResponse Current { get; private set; } = null!;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        public ValueTask<bool> MoveNextAsync()
        {
            _index++;
            if (_index >= responses.Length)
            {
                return ValueTask.FromResult(false);
            }

            Current = responses[_index];
            return ValueTask.FromResult(true);
        }
    }
}

