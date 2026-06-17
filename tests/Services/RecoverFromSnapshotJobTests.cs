using Aer.QdrantClient.Http.Models.Shared;
using Aer.QdrantClient.Http.Abstractions;
using Aer.QdrantClient.Http.Models.Responses;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;
using NUnit.Framework;
using Vigilante.Extensions;
using Vigilante.Configuration;
using Vigilante.Models;
using Vigilante.Models.Enums;
using Vigilante.Services.Interfaces;
using Vigilante.Services.Snapshots;
using Vigilante.Services.Jobs.Snapshots;

namespace Aer.Vigilante.Tests.Services;

[TestFixture]
public class RecoverFromSnapshotJobTests
{
    [Test]
    public async Task AdvanceAsync_WhenStartSucceeds_StartsWaitingForReady()
    {
        var (serviceProvider, _, _, _) = CreateServiceProviderWithSnapshotService(success: true);

        var job = new RecoverFromSnapshotJob(
            serviceProvider,
            "col1",
            "http://node1:6333",
            SnapshotPriority.NoSync,
            SnapshotSource.QdrantApi,
            "snap1",
            "source-col");

        var (HasMore, Success, ErrorMessage) = await job.AdvanceAsync(CancellationToken.None);

        HasMore.Should().BeTrue();
        Success.Should().BeTrue();
        ErrorMessage.Should().BeNull();

        job.IsWaitingForReady.Should().BeTrue();
    }

    [Test]
    public async Task AdvanceAsync_WhenStartFails_ReturnsError()
    {
        var (serviceProvider, _, _, _) = CreateServiceProviderWithSnapshotService(success: false);

        var job = new RecoverFromSnapshotJob(
            serviceProvider,
            "col1",
            "http://node1:6333",
            SnapshotPriority.NoSync,
            SnapshotSource.QdrantApi,
            "snap1",
            "source-col");

        var (HasMore, Success, ErrorMessage) = await job.AdvanceAsync(CancellationToken.None);

        HasMore.Should().BeFalse();
        Success.Should().BeFalse();
        ErrorMessage.Should().Be("Failed to recover collection 'col1' from snapshot 'snap1' on http://node1:6333");
        job.IsWaitingForReady.Should().BeFalse();
    }

    [Test]
    public async Task CheckReadyAsync_WhenNotWaiting_ReturnsNull()
    {
        var (serviceProvider, _, _, _) = CreateServiceProviderWithSnapshotService(success: true);
        var job = new RecoverFromSnapshotJob(
            serviceProvider,
            "col1",
            "http://node1:6333",
            SnapshotPriority.NoSync,
            SnapshotSource.QdrantApi,
            "snap1",
            "source-col");

        var ready = await job.CheckReadyAsync(CancellationToken.None);

        ready.Should().BeNull();
    }

    [Test]
    public async Task CheckReadyAsync_WhenCollectionIsHealthy_ReturnsTrue()
    {
        var (job, clusterManager, _) = await CreateStartedJobAsync(
            collectionMetrics: new CollectionMetrics
            {
                OutgoingTransfers = [],
                Shards =
                [
                    new ShardDetails { ShardId = 1, State = ShardState.Active.ToString() },
                    new ShardDetails { ShardId = 2, State = ShardState.Active.ToString() }
                ]
            });

        clusterManager.GetCollectionsInfoAsync(true, Arg.Any<CancellationToken>())
            .Returns(
            [
                new CollectionInfo
                {
                    CollectionName = "col1",
                    NodeUrl = "http://node1:6333",
                    Metrics = new CollectionMetrics
                    {
                        OutgoingTransfers =
                        [
                            new OutgoingTransferInfo { ShardId = 1, To = "node2", ToPeerId = "1002", Method = "stream_records" }
                        ],
                        Shards =
                        [
                            new ShardDetails { ShardId = 1, State = ShardState.Active.ToString() },
                            new ShardDetails { ShardId = 2, State = ShardState.Active.ToString() }
                        ]
                    }
                }
            ],
            [
                new CollectionInfo
                {
                    CollectionName = "col1",
                    NodeUrl = "http://node1:6333",
                    Metrics = new CollectionMetrics
                    {
                        OutgoingTransfers = [],
                        Shards =
                        [
                            new ShardDetails { ShardId = 1, State = ShardState.Active.ToString() },
                            new ShardDetails { ShardId = 2, State = ShardState.Active.ToString() }
                        ]
                    }
                }
            ],
            [
                new CollectionInfo
                {
                    CollectionName = "col1",
                    NodeUrl = "http://node1:6333",
                    Metrics = new CollectionMetrics
                    {
                        OutgoingTransfers = [],
                        Shards =
                        [
                            new ShardDetails { ShardId = 1, State = ShardState.Active.ToString() },
                            new ShardDetails { ShardId = 2, State = ShardState.Active.ToString() }
                        ]
                    }
                }
            ]);

        var firstCheck = await job.CheckReadyAsync(CancellationToken.None);
        var secondCheck = await job.CheckReadyAsync(CancellationToken.None);
        var thirdCheck = await job.CheckReadyAsync(CancellationToken.None);

        firstCheck.Should().BeFalse();
        secondCheck.Should().BeFalse();
        thirdCheck.Should().BeTrue();
    }

    [Test]
    public async Task CheckReadyAsync_WhenOutgoingTransfersExist_ReturnsFalse()
    {
        var (job, clusterManager, _) = await CreateStartedJobAsync(
            collectionMetrics: new CollectionMetrics
            {
                OutgoingTransfers =
                [
                    new OutgoingTransferInfo { ShardId = 1, To = "node2", ToPeerId = "1002", Method = "stream_records" }
                ],
                Shards = [new ShardDetails { ShardId = 1, State = ShardState.Active.ToString() }]
            });

        clusterManager.GetCollectionsInfoAsync(true, Arg.Any<CancellationToken>())
            .Returns(
            [
                new CollectionInfo
                {
                    CollectionName = "col1",
                    NodeUrl = "http://node1:6333",
                    Metrics = new CollectionMetrics
                    {
                        OutgoingTransfers =
                        [
                            new OutgoingTransferInfo { ShardId = 1, To = "node2", ToPeerId = "1002", Method = "stream_records" }
                        ],
                        Shards = [new ShardDetails { ShardId = 1, State = ShardState.Active.ToString() }]
                    }
                }
            ]);

        var firstCheck = await job.CheckReadyAsync(CancellationToken.None);
        var secondCheck = await job.CheckReadyAsync(CancellationToken.None);

        firstCheck.Should().BeFalse();
        secondCheck.Should().BeFalse();
    }

    [Test]
    public async Task CheckReadyAsync_WhenShardSizesChange_ReturnsTrueAfterBaseline()
    {
        var (job, clusterManager, _) = await CreateStartedJobAsync(
            collectionMetrics: new CollectionMetrics
            {
                OutgoingTransfers = [],
                Shards =
                [
                    new ShardDetails { ShardId = 1, State = ShardState.Active.ToString(), VectorsSizeBytes = 100 },
                    new ShardDetails { ShardId = 2, State = ShardState.Active.ToString(), VectorsSizeBytes = 200 }
                ]
            });

        clusterManager.GetCollectionsInfoAsync(true, Arg.Any<CancellationToken>())
            .Returns(
            [
                new CollectionInfo
                {
                    CollectionName = "col1",
                    NodeUrl = "http://node1:6333",
                    Metrics = new CollectionMetrics
                    {
                        OutgoingTransfers = [],
                        Shards =
                        [
                            new ShardDetails { ShardId = 1, State = ShardState.Active.ToString(), VectorsSizeBytes = 100 },
                            new ShardDetails { ShardId = 2, State = ShardState.Active.ToString(), VectorsSizeBytes = 200 }
                        ]
                    }
                }
            ],
            [
                new CollectionInfo
                {
                    CollectionName = "col1",
                    NodeUrl = "http://node1:6333",
                    Metrics = new CollectionMetrics
                    {
                        OutgoingTransfers = [],
                        Shards =
                        [
                            new ShardDetails { ShardId = 1, State = ShardState.Active.ToString(), VectorsSizeBytes = 150 },
                            new ShardDetails { ShardId = 2, State = ShardState.Active.ToString(), VectorsSizeBytes = 250 }
                        ]
                    }
                }
            ]);

        var firstCheck = await job.CheckReadyAsync(CancellationToken.None);
        var secondCheck = await job.CheckReadyAsync(CancellationToken.None);

        firstCheck.Should().BeFalse();
        secondCheck.Should().BeTrue();
    }

    [Test]
    public async Task AdvanceAsync_WhenTimedOutFlagIsSet_ReturnsTimeoutError()
    {
        var (job, _, _) = await CreateStartedJobAsync(
            collectionMetrics: new CollectionMetrics
            {
                OutgoingTransfers = [],
                Shards = [new ShardDetails { ShardId = 1, State = ShardState.Active.ToString() }]
            });

        typeof(RecoverFromSnapshotJob)
            .GetField("_timedOut", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .SetValue(job, true);

        var (HasMore, Success, ErrorMessage) = await job.AdvanceAsync(CancellationToken.None);

        HasMore.Should().BeFalse();
        Success.Should().BeFalse();
        ErrorMessage.Should().Be("Recovery did not complete within timeout");
    }

    [Test]
    public async Task AdvanceAsync_WhenUrlRecoveryStartSucceeds_StartsWaitingForReady()
    {
        var (serviceProvider, _, _, _) = CreateServiceProviderWithSnapshotService(success: true, urlRecovery: true);

        var job = new RecoverFromSnapshotJob(
            serviceProvider,
            "col1",
            "http://node1:6333",
            SnapshotPriority.NoSync,
            source: null,
            snapshotName: null,
            sourceCollectionName: null,
            snapshotUrl: "https://s3.example.com/bucket/snap1.snapshot",
            snapshotChecksum: "abc123");

        var (HasMore, Success, ErrorMessage) = await job.AdvanceAsync(CancellationToken.None);

        HasMore.Should().BeTrue();
        Success.Should().BeTrue();
        ErrorMessage.Should().BeNull();
        job.IsWaitingForReady.Should().BeTrue();
    }

    private static async Task<(RecoverFromSnapshotJob Job, IClusterManager ClusterManager, SnapshotService SnapshotService)> CreateStartedJobAsync(
        CollectionMetrics collectionMetrics)
    {
        var (serviceProvider, snapshotService, _, _) = CreateServiceProviderWithSnapshotService(success: true);
        var clusterManager = (IClusterManager)serviceProvider.GetService(typeof(IClusterManager))!;

        clusterManager.GetCollectionsInfoAsync(true, Arg.Any<CancellationToken>())
            .Returns(
            [
                new CollectionInfo
                {
                    CollectionName = "col1",
                    NodeUrl = "http://node1:6333",
                    Metrics = collectionMetrics
                }
            ]);

        var job = new RecoverFromSnapshotJob(
            serviceProvider,
            "col1",
            "http://node1:6333",
            SnapshotPriority.NoSync,
            SnapshotSource.QdrantApi,
            "snap1",
            "source-col");

        var (HasMore, Success, _) = await job.AdvanceAsync(CancellationToken.None);

        Success.Should().BeTrue();
        HasMore.Should().BeTrue();

        job.IsWaitingForReady.Should().BeTrue();

        return (job, clusterManager, snapshotService);
    }

    private static (IServiceProvider ServiceProvider, SnapshotService SnapshotService, IQdrantHttpClient QdrantClient, IClusterManager ClusterManager)
        CreateServiceProviderWithSnapshotService(bool success, bool urlRecovery = false)
    {
        var serviceProvider = Substitute.For<IServiceProvider>();
        var clusterManager = Substitute.For<IClusterManager>();
        serviceProvider.GetService(typeof(IClusterManager)).Returns(clusterManager);

        var nodesProvider = Substitute.For<IQdrantNodesProvider>();
        var clientFactory = Substitute.For<IQdrantClientFactory>();
        var qdrantClient = Substitute.For<IQdrantHttpClient>();
        var s3SnapshotService = Substitute.For<IS3SnapshotService>();
        var dynamicConfigService = Substitute.For<IDynamicConfigService>();
        var jobRegistry = Substitute.For<IJobRegistry>();
        var logger = Substitute.For<ILogger<SnapshotService>>();
        var options = Substitute.For<IOptions<QdrantOptions>>();
        options.Value.Returns(new QdrantOptions { ApiKey = "test", HttpTimeoutSeconds = 5 });

        clientFactory.CreateClientFromUrl("http://node1:6333", "test").Returns(qdrantClient);
        var response = Task.FromResult(new DefaultOperationResponse
        {
            Result = success,
            Status = new QdrantStatus(success ? QdrantOperationStatusType.Ok : QdrantOperationStatusType.Error)
        });
        if (urlRecovery)
        {
            qdrantClient.RecoverCollectionFromSnapshot(
                    "col1",
                    Arg.Any<Uri>(),
                    Arg.Any<CancellationToken>(),
                    false,
                    SnapshotPriority.NoSync,
                    "abc123")
                .Returns(response);
        }
        else
        {
            qdrantClient.RecoverCollectionFromSnapshot(
                    "col1",
                    "snap1",
                    Arg.Any<CancellationToken>(),
                    false,
                    SnapshotPriority.NoSync)
                .Returns(response);
        }

        var snapshotService = new SnapshotService(
            nodesProvider,
            clientFactory,
            s3SnapshotService,
            commandExecutor: null,
            options,
            dynamicConfigService,
            jobRegistry,
            serviceProvider,
            logger);

        serviceProvider.GetService(typeof(ISnapshotService)).Returns(snapshotService);
        return (serviceProvider, snapshotService, qdrantClient, clusterManager);
    }
}
