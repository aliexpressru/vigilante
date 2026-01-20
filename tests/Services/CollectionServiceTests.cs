using Aer.QdrantClient.Http.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;
using NUnit.Framework;
using Vigilante.Configuration;
using Vigilante.Services;
using Vigilante.Services.Interfaces;

namespace Aer.Vigilante.Tests.Services;

/// <summary>
/// Tests for CollectionService snapshot retrieval functionality.
/// Uses mocked IPodCommandExecutor to validate business logic.
/// Low-level command parsing and WebSocket handling is covered by PodCommandExecutorTests.
/// </summary>
[TestFixture]
public class CollectionServiceTests
{
    private ILogger<CollectionService> _logger = null!;
    private ILogger<PodCommandExecutor> _commandExecutorLogger = null!;
    private IMeterService _meterService = null!;
    private IQdrantClientFactory _clientFactory = null!;
    private IOptions<QdrantOptions> _options = null!;
    private IPodCommandExecutor _mockCommandExecutor = null!;

    [SetUp]
    public void Setup()
    {
        _logger = Substitute.For<ILogger<CollectionService>>();
        _commandExecutorLogger = Substitute.For<ILogger<PodCommandExecutor>>();
        _meterService = Substitute.For<IMeterService>();
        _clientFactory = Substitute.For<IQdrantClientFactory>();
        _options = Substitute.For<IOptions<QdrantOptions>>();
        _mockCommandExecutor = Substitute.For<IPodCommandExecutor>();
        
        _options.Value.Returns(new QdrantOptions
        {
            HttpTimeoutSeconds = 5,
            Nodes = new List<QdrantNodeConfig>()
        });
    }
}
