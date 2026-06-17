using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;
using NUnit.Framework;
using Vigilante.Configuration;
using Vigilante.Models;
using Vigilante.Services;
using Vigilante.Services.Interfaces;

namespace Aer.Vigilante.Tests.Services;

[TestFixture]
public class RestoreReplicationFactorJobServiceTests
{
    private IClusterManager _clusterManager = null!;
    private IJobRegistry _jobRegistry = null!;
    private ILogger<RestoreReplicationFactorJobService> _logger = null!;
    private IServiceProvider _serviceProvider = null!;

    [SetUp]
    public void SetUp()
    {
        _clusterManager = Substitute.For<IClusterManager>();
        _jobRegistry = Substitute.For<IJobRegistry>();
        _logger = Substitute.For<ILogger<RestoreReplicationFactorJobService>>();
        _serviceProvider = new ServiceCollection()
            .AddLogging()
            .BuildServiceProvider();
    }

    [TearDown]
    public void TearDown()
    {
        (_serviceProvider as IDisposable)?.Dispose();
    }

    [Test]
    public async Task RequestRestoreReplicationFactorAsync_NoHealthyNode_ReturnsApiError()
    {
        _clusterManager.GetClusterStateAsync(Arg.Any<CancellationToken>())
            .Returns(new ClusterState
            {
                Nodes = [new() { IsHealthy = false, Url = "http://node1:6333" }]
            });

        var options = Substitute.For<IOptions<QdrantOptions>>();
        options.Value.Returns(new QdrantOptions());
        var clientFactory = Substitute.For<Aer.QdrantClient.Http.Abstractions.IQdrantClientFactory>();

        var service = new RestoreReplicationFactorJobService(
            _clusterManager,
            _jobRegistry,
            clientFactory,
            options,
            _serviceProvider,
            _logger);

        var result = await service.RequestRestoreReplicationFactorAsync("col1", null, null, CancellationToken.None);

        result.ApiError.Should().BeTrue();
        result.AlreadyInProgress.Should().BeFalse();
        result.Message.Should().Contain("No healthy node");
    }
}
