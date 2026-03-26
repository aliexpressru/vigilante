using Aer.QdrantClient.Http.Models.Shared;
using NSubstitute;
using NUnit.Framework;
using Vigilante.Models;
using Vigilante.Models.Enums;
using Vigilante.Services.Interfaces;
using Vigilante.Services.Jobs;

namespace Aer.Vigilante.Tests.Services;

[TestFixture]
public class RecoverFromSnapshotJobTests
{
    [Test]
    public async Task AdvanceAsync_WhenStartSucceeds_StartsWaitingForReady()
    {
        var serviceProvider = Substitute.For<IServiceProvider>();
        var snapshotService = Substitute.For<ISnapshotService>();
        snapshotService.RecoverFromSnapshotAsync(
                "col1",
                "snap1",
                "http://node1:6333",
                SnapshotSource.S3Storage,
                "source-col",
                SnapshotPriority.NoSync,
                false,
                Arg.Any<CancellationToken>())
            .Returns((true, null));

        var job = new RecoverFromSnapshotJob(
            serviceProvider,
            snapshotService,
            "col1",
            "snap1",
            "http://node1:6333",
            SnapshotSource.S3Storage,
            "source-col",
            SnapshotPriority.NoSync);

        var result = await job.AdvanceAsync(CancellationToken.None);

        Assert.That(result.HasMore, Is.True);
        Assert.That(result.Success, Is.True);
        Assert.That(result.ErrorMessage, Is.Null);
        Assert.That(job.IsWaitingForReady, Is.True);
    }

    [Test]
    public async Task AdvanceAsync_WhenStartFails_ReturnsError()
    {
        var serviceProvider = Substitute.For<IServiceProvider>();
        var snapshotService = Substitute.For<ISnapshotService>();
        snapshotService.RecoverFromSnapshotAsync(
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<SnapshotSource>(),
                Arg.Any<string?>(),
                Arg.Any<SnapshotPriority>(),
                Arg.Any<bool>(),
                Arg.Any<CancellationToken>())
            .Returns((false, "failed to start recovery"));

        var job = new RecoverFromSnapshotJob(
            serviceProvider,
            snapshotService,
            "col1",
            "snap1",
            "http://node1:6333",
            SnapshotSource.S3Storage,
            "source-col",
            SnapshotPriority.NoSync);

        var result = await job.AdvanceAsync(CancellationToken.None);

        Assert.That(result.HasMore, Is.False);
        Assert.That(result.Success, Is.False);
        Assert.That(result.ErrorMessage, Is.EqualTo("failed to start recovery"));
        Assert.That(job.IsWaitingForReady, Is.False);
    }

    [Test]
    public async Task CheckReadyAsync_WhenNotWaiting_ReturnsNull()
    {
        var serviceProvider = Substitute.For<IServiceProvider>();
        var snapshotService = Substitute.For<ISnapshotService>();
        var job = new RecoverFromSnapshotJob(
            serviceProvider,
            snapshotService,
            "col1",
            "snap1",
            "http://node1:6333",
            SnapshotSource.S3Storage,
            "source-col",
            SnapshotPriority.NoSync);

        var ready = await job.CheckReadyAsync(CancellationToken.None);

        Assert.That(ready, Is.Null);
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

        Assert.That(firstCheck, Is.False);
        Assert.That(secondCheck, Is.False);
        Assert.That(thirdCheck, Is.True);
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

        Assert.That(firstCheck, Is.False);
        Assert.That(secondCheck, Is.False);
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
                    new ShardDetails { ShardId = 1, State = ShardState.Active.ToString(), SizeBytes = 100 },
                    new ShardDetails { ShardId = 2, State = ShardState.Active.ToString(), SizeBytes = 200 }
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
                            new ShardDetails { ShardId = 1, State = ShardState.Active.ToString(), SizeBytes = 100 },
                            new ShardDetails { ShardId = 2, State = ShardState.Active.ToString(), SizeBytes = 200 }
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
                            new ShardDetails { ShardId = 1, State = ShardState.Active.ToString(), SizeBytes = 150 },
                            new ShardDetails { ShardId = 2, State = ShardState.Active.ToString(), SizeBytes = 250 }
                        ]
                    }
                }
            ]);

        var firstCheck = await job.CheckReadyAsync(CancellationToken.None);
        var secondCheck = await job.CheckReadyAsync(CancellationToken.None);

        Assert.That(firstCheck, Is.False);
        Assert.That(secondCheck, Is.True);
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

        var result = await job.AdvanceAsync(CancellationToken.None);

        Assert.That(result.HasMore, Is.False);
        Assert.That(result.Success, Is.False);
        Assert.That(result.ErrorMessage, Is.EqualTo("Recovery did not complete within timeout"));
    }

    private static async Task<(RecoverFromSnapshotJob Job, IClusterManager ClusterManager, ISnapshotService SnapshotService)> CreateStartedJobAsync(
        CollectionMetrics collectionMetrics)
    {
        var clusterManager = Substitute.For<IClusterManager>();
        var serviceProvider = Substitute.For<IServiceProvider>();
        serviceProvider.GetService(typeof(IClusterManager)).Returns(clusterManager);

        var snapshotService = Substitute.For<ISnapshotService>();
        snapshotService.RecoverFromSnapshotAsync(
                "col1",
                "snap1",
                "http://node1:6333",
                SnapshotSource.S3Storage,
                "source-col",
                SnapshotPriority.NoSync,
                false,
                Arg.Any<CancellationToken>())
            .Returns((true, null));

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
            snapshotService,
            "col1",
            "snap1",
            "http://node1:6333",
            SnapshotSource.S3Storage,
            "source-col",
            SnapshotPriority.NoSync);

        var start = await job.AdvanceAsync(CancellationToken.None);
        Assert.That(start.Success, Is.True);
        Assert.That(start.HasMore, Is.True);
        Assert.That(job.IsWaitingForReady, Is.True);

        return (job, clusterManager, snapshotService);
    }
}
