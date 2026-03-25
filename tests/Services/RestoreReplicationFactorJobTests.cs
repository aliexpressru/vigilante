using Aer.QdrantClient.Http.Models.Responses;
using Aer.QdrantClient.Http.Models.Shared;
using NUnit.Framework;
using Vigilante.Services.Jobs;

namespace Aer.Vigilante.Tests.Services;

[TestFixture]
public class RestoreReplicationFactorJobTests
{
    [Test]
    public void GetRestoreShardReplicationFactorStartFailureMessage_WhenStatusHasError_ReturnsMessage()
    {
        var status = new QdrantStatus(QdrantOperationStatusType.Error) { Error = "boom" };

        var msg = RestoreReplicationFactorJob.GetRestoreShardReplicationFactorStartFailureMessage(status);

        Assert.That(msg, Is.EqualTo("Failed to start restore replication factor: boom"));
    }

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

        Assert.That(failure, Is.EqualTo("Replication step failed to start: qdrant down"));
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

        Assert.That(failure, Is.EqualTo("Replication step failed: response has no ReplicatedShards"));
    }

    [Test]
    public void GetReplicationStepFailureMessage_WhenAnyShardReplicationFails_ReturnsItemFailureMessage()
    {
        var replicateResponse = new ReplicateShardsToPeerResponse
        {
            Status = new QdrantStatus(QdrantOperationStatusType.Ok),
            Result = new ReplicateShardsToPeerResponse.ReplicateShardsToPeerResponseUnit(
                ReplicatedShards: new[]
                {
                    new ReplicateShardsToPeerResponse.ReplicateShardToPeerResult(
                        IsSuccess: false,
                        ShardId: 1,
                        SourcePeerId: 2,
                        TargetPeerId: 3,
                        CollectionName: "col1")
                },
                AlreadyReplicatedShards: null!)
        };

        var failure = RestoreReplicationFactorJob.GetReplicationStepFailureMessage(
            replicateResponse,
            failedToStartPrefix: "Replication step failed to start",
            noReplicatedShardsMessage: "Replication step failed: response has no ReplicatedShards",
            itemFailurePrefix: "Replication failed");

        Assert.That(failure, Is.EqualTo("Replication failed (ShardId: 1, Source: 2, Target: 3)"));
    }

    [Test]
    public void GetReplicationStepFailureMessage_WhenAllShardReplicationsSucceeded_ReturnsNull()
    {
        var replicateResponse = new ReplicateShardsToPeerResponse
        {
            Status = new QdrantStatus(QdrantOperationStatusType.Ok),
            Result = new ReplicateShardsToPeerResponse.ReplicateShardsToPeerResponseUnit(
                ReplicatedShards: new[]
                {
                    new ReplicateShardsToPeerResponse.ReplicateShardToPeerResult(
                        IsSuccess: true,
                        ShardId: 1,
                        SourcePeerId: 2,
                        TargetPeerId: 3,
                        CollectionName: "col1")
                },
                AlreadyReplicatedShards: null!)
        };

        var failure = RestoreReplicationFactorJob.GetReplicationStepFailureMessage(
            replicateResponse,
            failedToStartPrefix: "Replication step failed to start",
            noReplicatedShardsMessage: "Replication step failed: response has no ReplicatedShards",
            itemFailurePrefix: "Replication failed");

        Assert.That(failure, Is.Null);
    }
}

