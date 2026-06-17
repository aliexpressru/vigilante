using Aer.QdrantClient.Http.Abstractions;
using k8s;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;
using FluentAssertions;
using NUnit.Framework;
using Vigilante.Configuration;
using Vigilante.Services;
using Vigilante.Services.Interfaces;

namespace Aer.Vigilante.Tests.Services;

[TestFixture]
public class QdrantNodesProviderTests
{
    private IKubernetes? _kubernetes;
    private IKubernetesManager? _kubernetesManager;
    private IQdrantClientFactory _clientFactory = null!;
    private IOptions<QdrantOptions> _options = null!;
    private ILogger<QdrantNodesProvider> _logger = null!;
    private QdrantNodesProvider _provider = null!;

    [SetUp]
    public void Setup()
    {
        _kubernetes = null; // Default to no K8s
        _kubernetesManager = null;
        _clientFactory = Substitute.For<IQdrantClientFactory>();
        _options = Options.Create(new QdrantOptions
        {
            HttpTimeoutSeconds = 5
        });
        _logger = Substitute.For<ILogger<QdrantNodesProvider>>();
    }

    private static IConfiguration CreateConfiguration(List<QdrantNodeConfig>? nodes = null)
    {
        var builder = new ConfigurationBuilder();

        if (nodes != null && nodes.Count > 0)
        {
            var inMemorySettings = new Dictionary<string, string?>();
            for (int i = 0; i < nodes.Count; i++)
            {
                inMemorySettings[$"Qdrant:Nodes:{i}:Host"] = nodes[i].Host;
                inMemorySettings[$"Qdrant:Nodes:{i}:Port"] = nodes[i].Port.ToString();
            }
            builder.AddInMemoryCollection(inMemorySettings);
        }

        return builder.Build();
    }

    #region StatefulSet Name Management Tests

    [Test]
    public async Task GetStatefulSetNameAsync_WhenNotSet_ReturnsNull()
    {
        // Arrange
        var configuration = CreateConfiguration();

        _provider = new QdrantNodesProvider(
            configuration,
            _kubernetes,
            _kubernetesManager,
            _clientFactory,
            _options,
            _logger);

        // Act
        var result = await _provider.GetStatefulSetNameAsync(CancellationToken.None);

        // Assert
        result.Should().BeNull();
    }

    [Test]
    public async Task GetStatefulSetNameAsync_AfterSetStatefulSetName_ReturnsStoredValue()
    {
        // Arrange
        var configuration = CreateConfiguration();

        _provider = new QdrantNodesProvider(
            configuration,
            _kubernetes,
            _kubernetesManager,
            _clientFactory,
            _options,
            _logger);

        var statefulSetName = "qdrant1";

        // Act
        _provider.SetStatefulSetName(statefulSetName);
        var result = await _provider.GetStatefulSetNameAsync(CancellationToken.None);

        // Assert
        result.Should().Be(statefulSetName);
    }

    [Test]
    public void SetStatefulSetName_WithEmptyString_ThrowsArgumentException()
    {
        // Arrange
        var configuration = CreateConfiguration();

        _provider = new QdrantNodesProvider(
            configuration,
            _kubernetes,
            _kubernetesManager,
            _clientFactory,
            _options,
            _logger);

        // Act & Assert
        Action act = () => _provider.SetStatefulSetName("");
        act.Should().Throw<ArgumentException>();
    }

    [Test]
    public void SetStatefulSetName_WithWhitespace_ThrowsArgumentException()
    {
        // Arrange
        var configuration = CreateConfiguration();

        _provider = new QdrantNodesProvider(
            configuration,
            _kubernetes,
            _kubernetesManager,
            _clientFactory,
            _options,
            _logger);

        // Act & Assert
        Action act = () => _provider.SetStatefulSetName("   ");
        act.Should().Throw<ArgumentException>();
    }

    [Test]
    public async Task GetStatefulSetNameAsync_WithMultipleSets_ReturnsLatest()
    {
        // Arrange
        var configuration = CreateConfiguration();

        _provider = new QdrantNodesProvider(
            configuration,
            _kubernetes,
            _kubernetesManager,
            _clientFactory,
            _options,
            _logger);

        // Act
        _provider.SetStatefulSetName("qdrant1");
        _provider.SetStatefulSetName("qdrant2");
        var result = await _provider.GetStatefulSetNameAsync(CancellationToken.None);

        // Assert
        result.Should().Be("qdrant2");
    }

    #endregion

    #region GetNodesAsync without Kubernetes Tests

    [Test]
    public async Task GetNodesAsync_WithoutK8sClient_UsesConfiguration()
    {
        // Arrange
        var nodes = new List<QdrantNodeConfig>
        {
            new() { Host = "localhost", Port = 6333 },
            new() { Host = "localhost", Port = 6334 }
        };
        var configuration = CreateConfiguration(nodes);

        _provider = new QdrantNodesProvider(
            configuration,
            null, // No K8s client
            null,
            _clientFactory,
            _options,
            _logger);

        // Act
        var result = await _provider.GetNodesAsync(CancellationToken.None);

        // Assert
        result.Should().HaveCount(2);
        result[0].Host.Should().Be("localhost");
        result[0].Port.Should().Be(6333);
    }

    [Test]
    public async Task GetNodesAsync_WithoutK8sClient_AndNoConfig_ReturnsEmpty()
    {
        // Arrange
        var configuration = CreateConfiguration();

        _provider = new QdrantNodesProvider(
            configuration,
            null, // No K8s client
            null,
            _clientFactory,
            _options,
            _logger);

        // Act
        var result = await _provider.GetNodesAsync(CancellationToken.None);

        // Assert
        result.Should().BeEmpty();
    }

    #endregion
}
